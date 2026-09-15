# Feature-Ideen — Stand 2026-08-01

Ergebnis einer Ideenfindungs-Session (Produktsicht, **keine** Umsetzung). Grundlage: vollständige
Durchsicht des Ist-Stands (`CLAUDE.md`, `docs/Architectur.md`, `docs/DECISIONS.md`, beide Reviews,
alle `Endpoints/*.cs`, `web/src/app/features/**`) plus Außenrecherche zu 7TV-/Twitch-API-Fähigkeiten
und benachbarten Tools.

Jede Idee ist gegen `docs/DECISIONS.md` geprüft — bereits verworfene Ansätze stehen nicht in den
Ja-Listen, und wo eine Idee an eine getroffene Entscheidung grenzt, ist das unter „Risiko" benannt.
Nichts hiervon ist ein Ticket; die Aufwandsangaben sind Größenordnungen, keine Schätzungen.

> **Statuspflege (nachgetragen am 2026-08-02).** Die Ideentexte selbst bleiben im Stand vom
> 2026-08-01 stehen — sie sind die Begründung, nicht der Bauplan. Was davon umgesetzt wurde, steht
> ausschließlich in der Statuszeile direkt unter der jeweiligen Überschrift und in der Tabelle unten.
> **Wer eine dieser Ideen umsetzt, pflegt seine Statuszeile im selben Commit**, so wie Regel 3 es für
> `DECISIONS.md` verlangt. Die Begründung *warum* etwas so gebaut wurde, gehört weiterhin
> ausschließlich nach [DECISIONS.md](DECISIONS.md); hier steht nur *ob*.

## Umsetzungsstand

Legende: ✅ umgesetzt · 🟡 teilweise · ⬜ offen

| Idee | Stand | Wo |
|---|---|---|
| **A1** Slot-Budget | ✅ 2026-08-01 | DECISIONS „Das Slot-Budget kommt aus unserer DB …" |
| **A2** Zuletzt/nie benutzt | ✅ 2026-08-01 | DECISIONS „Nutzungs-Kontext statt nackter Summe …" |
| **A3** Getrackt-seit + Karenz | ✅ 2026-08-01 | dito — als Paket mit A2/A4 ausgeliefert |
| **A4** Trend-Label | ✅ 2026-08-01 | dito |
| **A5** Emote-Drilldown | ✅ 2026-08-02 | DECISIONS „Der Emote-Drilldown bekommt einen eigenen … Endpoint" |
| **A6** Purge-Sicherheitsnetz | ✅ 2026-08-02 | DECISIONS „Restore läuft im Browser …" + „`Emote.ArchivedAt` wird geschrieben …" |
| **A7** Kanal-Aktivitätsverlauf | ✅ 2026-08-02 | DECISIONS „Der Channel bekommt seinen eigenen Audit-Log" |
| **A8** Resync für Channel-Manager | ✅ 2026-08-02 | DECISIONS „Resync als Self-Service" |
| **A9** Globale Verbreitung | ⬜ | — |
| **A10** Nutzung pro Live-Stunde | 🟡 2026-08-03 | Stufe 1 (Datenerfassung + Chart-Markierung) — DECISIONS „Live-Abdeckung pro Tag …"; Stufe 2 (umschaltbare Metrik) offen |
| **A11** Duplikat-Erkennung | ⬜ | — |
| **A12** Ergebnis-Export | ✅ 2026-08-02 | DECISIONS „Der Export ist eine Client-Serialisierung …" |
| **A13**–**A15** | ⬜ | — |
| **A16** Emote-Liste importieren (Nachtrag 2026-08-04) | ✅ 2026-09-06 | DECISIONS „Import-Lauf: dritter Arbiter-Zweig ohne DI-Zirkel, kein Kanal-Reset, Nachlauf ans Laufobjekt gebunden …" (#72, K3) |
| **B1** Support-Drilldown | 🟡 2026-08-01 | Audit-Zeilen und Per-Channel-Flush fehlen |
| **B2** Soll/Ist-Roster | ✅ 2026-08-02 | DECISIONS „Auslastungsbalken bekommen eine Schwellen-Leiter, das Roster-Badge nicht" |
| **B3**–**B9** | ⬜ | — |
| **B10** LIVE-Badge in Channel-Listen (Nachtrag 2026-08-03) | ✅ 2026-08-03 | DECISIONS „LIVE-Badge in den Channel-Listen: der Worker publiziert den Live-Poll-Momentzustand …" |

## Inhalt

- [Umsetzungsstand](#umsetzungsstand)
- [Zwei Befunde vorweg](#zwei-befunde-vorweg)
- [A) Nutzer-Features](#a-nutzer-features)
- [B) Admin-Panel](#b-admin-panel)
- [Würde ich nicht bauen](#würde-ich-nicht-bauen)
- [Die drei Top-Kandidaten](#die-drei-top-kandidaten)
- [Quellen](#quellen)

---

## Zwei Befunde vorweg

### 1. Das dokumentierte Twitch-Limit ist das falsche

`CLAUDE.md` nennt unter „Bekannte offene Grenzen" das JOIN-**Raten**limit (20 JOINs pro 10 Sekunden
für unverifizierte Verbindungen). Daneben existiert seit **2024-05-15** ein **Concurrent-Join-Limit
von 100 Channels pro Chat-Verbindung** — gestaffelt eingeführt (2024-02-28: 100.000 → 2024-03-30:
10.000 → 2024-04-30: 1.000 → 2024-05-15: 100). Das ist ein Bestandslimit, keine Rate.

Ausgenommen sind Verified Bots (die Komplettausnahme lief allerdings am 2024-06-26 aus) und Channels,
in denen der Chat-User Moderator ist. Der Vorabtest vom 2026-07-30 („28 ungedrosselte JOINs in 5 s,
0 Fehler") misst die Rate, nicht diese Decke — die schlägt erst bei Channel Nr. 101 zu.

Zusätzlich: **`justinfan` ist von Twitch nie offiziell dokumentiert worden.** Ob das 100er-Limit für
anonyme Verbindungen gilt, hat Twitch im Announcement-Thread nie beantwortet. Ein undokumentiertes
Feature genießt weder Deprecation-Schutz noch Ankündigungspflicht — das ist das eigentliche
Architekturrisiko hinter Modul A.

IRC selbst ist **nicht** deprecated (Twitch wörtlich: „no plan to remove IRC as a third-party
supported interface"), aber neue Chat-Funktionalität entsteht nur noch in EventSub. Der
EventSub-Weg (`channel.chat.message`) hat kein anonymes Äquivalent: er braucht `user:read:chat`
(User-Token, WebSocket) bzw. `user:bot` + `channel:bot` oder Mod-Status des Bot-Users
(App-Token, Conduit/Webhook).

→ Konsequenz für die Ideenliste: **B2** (Kapazitäts-Frühwarnung) und die Zurückhaltung bei
Discovery-Features, die die Channel-Zahl schnell treiben würden.

### 2. Die Marktlücke ist belegt, nicht vermutet

[SevenTV/SevenTV#197](https://github.com/SevenTV/SevenTV/issues/197), eröffnet am 29.12.2024,
Label „Enhancement", bis heute **offen ohne Assignee und ohne PR**, wörtlich:

> „When working with full emote sets it is hard to determine what emotes to remove to add in
> new emotes."

Gefordert wird ein „last used"-Feld analog zum bestehenden „added at" plus eine nach Last-Used
sortierte Mod-View für Emote-Set-Editoren. 7TVs eigener Statistik-Anlauf
([`SevenTV/7tv-bot`](https://github.com/SevenTV/7tv-bot), README: „aggregating statistics on emote
usage") ist seit **22.02.2024 archiviert**.

Der Markt daneben ist entlang von *Zählen* (kattah, ZonianMidian, ChatStats.live, StreamElements)
und *Duplikate finden* (GreenComfyTea, Emote Content ID) besetzt. **Niemand verbindet Nutzungsdaten
mit Set-Kuration und Mass-Delete.** Die einzige Seite, die den „Never Used"-Gedanken überhaupt
zeigt, ist eine Einzel-Channel-Bastellösung ohne Aktion dahinter.

→ Das ist die Begründung dafür, dass **A2/A3/A4** als Paket vor allem anderen an Nutzerwert stehen.

---

## A) Nutzer-Features

Sortiert nach Wirkung ÷ Aufwand.

### A1 — Slot-Budget: „847 / 1000 belegt, nach der Purge 612"

**Status: ✅ umgesetzt am 2026-08-01.** Wie beschrieben, mit einer Abweichung: `emote_count` aus dem
7TV-Response wird bewusst **nicht** übernommen — die belegten Slots zählt die eigene DB, nur
`capacity` kommt von 7TV. Begründung im DECISIONS-Eintrag „Das Slot-Budget kommt aus unserer DB, die
Kapazität aus 7TV".

Ein Kapazitätsbalken im Channel-Workspace und im Mass-Delete-Panel, der live mitrechnet, wie viele
Slots die aktuelle Auswahl freiräumt. Für Mods, Broadcaster und 7TV-Editoren.

**Warum es zieht.** Aufräumen ist nie Selbstzweck — es passiert, weil neue Emotes rein sollen. Heute
ist das Ziel unsichtbar; der Mod löscht ins Blaue. Der Ist-Zustand der Kuration im Ökosystem ist bis
heute die Tabellenkalkulation („keep a spreadsheet tracking which emotes are on which platform, as
it's easy to lose track with 50+ slots across three services", StreamEmote-Guide).

**Datenlage.** Reicht — **kostet null zusätzliche Requests.** `GET /v3/emote-sets/{id}` und der
verschachtelte `emote_set` in `GET /v3/users/twitch/{id}` liefern `emote_count` und `capacity`
bereits (live verifiziert). Der Sync holt diesen Response ohnehin und verwirft beide Felder. Braucht
zwei Spalten auf `Channel`, im `SevenTvSyncService` mitgeschrieben, additive Migration.

**Aufwand.** S · **Risiko.** Praktisch keins. 7TV-Subscriber können `capacity` > 1000 haben — nie
hart 1000 annehmen, immer den gelieferten Wert verwenden.

### A2 — „Zuletzt benutzt" und „nie benutzt" als erstklassige Spalte

**Status: ✅ umgesetzt am 2026-08-01**, gemeinsam mit A3 und A4 als ein Paket ausgeliefert (Commits
`fc2c74c`…`6c668c3`). DECISIONS-Eintrag „Nutzungs-Kontext statt nackter Summe".

Pro Emote das Datum der letzten Nutzung bzw. ein Badge „nie benutzt (seit Tracking-Beginn)".
Sortier- und filterbar im Usage-Grid und als Vorauswahl für eine Vote-Session.

**Warum es zieht.** Beantwortet SevenTV#197 wörtlich. Eure Summe über 30 Tage beantwortet die Frage
**nicht**: „0 Nutzungen in 30 Tagen" unterscheidet nicht zwischen „seit acht Monaten tot" und
„letzte Woche noch aktiv".

**Datenlage.** Reicht vollständig. `MAX(Date)` je `EmoteId` über `UsageStat` — der Covering-Index
`(EmoteId, Date) INCLUDE (UseCount)` bedient das als Index-Only-Scan. **Regel 10 beachten**: erst auf
eine skalare Emote-ID-Liste reduzieren, dann gruppieren. Neue Methode auf `IUsageStatQueryService`
(Regel 4 eingehalten).

**Aufwand.** S–M · **Risiko.** „Nie benutzt" ist nur so alt wie das Tracking. Ohne **A3** ist die
Angabe gefährlich irreführend — die beiden gehören zusammen ausgeliefert.

### A3 — Vertrauens-Kontext: „getrackt seit" + Karenz für neue Emotes

**Status: ✅ umgesetzt am 2026-08-01**, als Paket mit A2/A4. Von den beiden Wegen für die Emote-Seite
wurde wie empfohlen **(b)** gewählt: `ActiveEmote.timestamp` aus dem 7TV-Set-Response wird
mitgeschrieben, die Karenz ist damit auch rückwirkend korrekt.

Zwei zusammengehörige Angaben: pro Channel „wir zählen seit dem 12.07.2026" (mit Warnung, wenn der
gewählte Zeitraum davor beginnt), und pro Emote „seit vier Tagen im Set — noch in Beobachtung",
womit es als Löschkandidat explizit ausgeschlossen wird.

**Warum es zieht.** Der offenste Vertrauensbruch im ganzen Feld. StreamElements schreibt seine eigene
Einschränkung in die Doku: `!emotecount` ist genau: „accuracy depending on when the bot joined and
started tracking messages". Auf unvollständigen Daten löscht kein Mod-Team gern — und ein frisch
hinzugefügtes Emote hat notwendigerweise 0 Nutzungen. Das offen zu adressieren ist Vertrauensvorsprung,
keine Schwäche.

**Datenlage.** Lücke, aber billig zu schließen.

- Channel-Seite: `Channel.CreatedAt` existiert, wird für Nicht-Admins nur nirgends ausgeliefert.
- Emote-Seite: `Emote` hat **kein** `FirstSeenAt` — nur `LastSyncedAt`, das jeder Resync überschreibt.
  Zwei Wege: (a) neue Spalte, beim ersten Sichten im Sync gesetzt (nur zukunftswirksam), oder
  (b) `ActiveEmote.timestamp` aus dem 7TV-Set-Response mitschreiben — **auch rückwirkend korrekt**,
  das Feld liegt im Response, wird nur nicht geparst. **(b) ist klar besser** bei gleicher Migration.

**Aufwand.** S–M · **Risiko.** Technisch keins. Bewusst als Ehrlichkeit framen, nicht verstecken.

### A4 — Trend-Label statt nackter Summe

**Status: ✅ umgesetzt am 2026-08-01**, als Paket mit A2/A3. Bei zu kurzer Historie wird das Label
wie gefordert unterdrückt statt geraten.

Pro Emote ein Momentum-Signal: letzte 30 Tage gegen die 30 davor → „im Sinkflug / stabil / im
Aufwind", als Filter und als Kontextspalte in der Voting-UI.

**Warum es zieht.** „150 Uses" ist ohne Richtung wertlos. Ein Emote mit 150 Uses, das vor drei
Monaten 4.000 hatte, ist ein Löschkandidat; eins, das von 20 auf 150 gestiegen ist, gerade nicht.
Vergleichbare Tools zeigen Monatsscheiben — den Vergleich muss der Mod selbst im Kopf machen.

**Datenlage.** Reicht vollständig, ohne jede Schema-Änderung. Zwei Summen über denselben Index.
Nutzt genau die Zeitreihe, die `GET /api/channels/{c}/usage-stats` schon liefert und die das
Frontend heute **nie aufruft**.

**Aufwand.** S · **Risiko.** Nur mit **A3** kombiniert sinnvoll — bei kurzer Historie das Label
unterdrücken statt raten. Sauber abgrenzen gegen die Entscheidung vom 2026-08-01: das ist eine
**Kontextspalte**, kein Rückweg von Chat-Nutzung in den Beliebtheits-Score.

### A5 — Emote-Drilldown mit Tages-Sparkline

**Status: ✅ umgesetzt am 2026-08-02** — neuer Endpoint `GET …/usage-stats/daily?emoteId=` (der
Debug-Endpoint blieb unangetastet), CDK-Dialog mit SVG-Sparkline über ein Info-Icon in der Karte,
auf der Usage-Stats- **und** der Vote-Session-Detailseite. Zwei bewusste Abweichungen: der
Vote-Stand kommt aus den ohnehin geladenen Kartenwerten der Vote-Seite (keine eigene Tally-Query),
und auf der Vote-Seite ist das Icon auf Nutzer mit Usage-Zugriff beschränkt (der Endpoint hängt
hinter dem Usage-Stats-Filter). Begründung in DECISIONS „Der Emote-Drilldown bekommt einen eigenen,
auf ein Emote gefilterten Endpoint".

Klick auf ein Emote öffnet ein Panel mit Tagesverlauf, erstem und letztem Auftreten, Trend und dem
Vote-Stand in laufenden Sessions.

**Warum es zieht.** Der Grenzfall ist der teure Fall. Bei 900 Emotes sind ~700 offensichtlich („tot"
oder „läuft") und ~200 brauchen einen zweiten Blick — dafür gibt es heute keinen Ort.

**Datenlage.** Endpoint existiert bereits (`GET /api/channels/{c}/usage-stats`, im Frontend
ungenutzt). Für einen Drilldown will man ihn auf ein Emote filterbar und um `emoteId` erweitert
haben — heute liefert er `(emoteName, date, useCount)` ungefiltert und ungepaged für den ganzen
Channel.

**Aufwand.** S (Frontend) + S (Endpoint-Parameter) · **Risiko.** Der ungefilterte Vollabruf bei
900 Emotes × 90 Tagen ist eine große Antwort — serverseitig filtern, nicht clientseitig zerlegen.

### A6 — Purge-Sicherheitsnetz: Vorschau, Protokoll, Restore-Liste

**Status: ✅ umgesetzt am 2026-08-02** — Vorschau gab es bereits (DeleteConfirmDialog); neu sind
Post-Run-Zusammenfassung, Protokoll-Download (JSON/CSV, `kind: 'purge-run'` im geteilten
Export-Envelope), **In-App-Restore** (Post-Run-Button + Protokoll-Import, GQL-ADD im Browser über
die aus dem Delete extrahierte `SevenTvRunEngine`) und die additive Spalte `Emote.ArchivedAt`.
Bewusst nicht gebaut: eine DB-gestützte Archiv-Liste (böte Emotes an, die jemand auf 7TV absichtlich
entfernt hat) und ein `sync-restored`-Endpoint (der A8-Resync genügt, die Richtung ist konservativ).
Begründungen in DECISIONS „Restore läuft im Browser über dieselbe Lauf-Mechanik wie der Delete" und
„`Emote.ArchivedAt` wird geschrieben, aber noch nicht ausgeliefert".

Vor dem Mass-Delete eine Bestätigungsliste; nach dem Lauf ein Ergebnisprotokoll mit allen entfernten
7TV-IDs und -Namen, als JSON/CSV herunterladbar und als „diese wieder hinzufügen"-Liste nutzbar.

**Warum es zieht.** Der Worst Case ist belegt: [SevenTV/Extension#650](https://github.com/SevenTV/Extension/issues/650)
— ein Klick auf das Papierkorb-Icon löschte ein komplettes Personal-Set. Eine irreversible
400-Emote-Aktion ohne Papierspur ist die Sorte Feature, die genau einmal falsch läuft. Export ist im
Feld etabliert (chat.vote: JSON/TXT; ChatStats verkauft Archivierung als Pro-Feature).

**Datenlage.** Reicht. `sync-deleted` archiviert soft, `Emote` bleibt mit `SevenTvEmoteId` und `Name`
erhalten. Das Protokoll baut der Client aus dem, was er gerade gelöscht hat. Für „was haben wir wann
rausgeworfen" fehlt `Emote.ArchivedAt` — additive Spalte.

**Aufwand.** S (Export/Protokoll) bis M (echte Restore-Funktion) · **Risiko.** Restore muss aus
demselben Grund im Browser laufen wie der Delete: **Grundsatz 4 (Zero-Knowledge)** gilt unverändert,
das 7TV-Schreib-Token darf das Backend nie sehen. Ein serverseitiger Undo-Button ist keine Option.

### A7 — Kanal-Aktivitätsverlauf für Broadcaster und Mods

**Status: ✅ umgesetzt am 2026-08-02.** `GET /api/channels/{c}/audit-log` plus ein dritter Tab
„Aktivität" im Workspace. Wie vorgeschlagen ohne neuen Service und ohne Migration — nur die Route,
der `channelManageGuard` und die Seite. Zwei Konkretisierungen: der Channel kommt **ausschließlich**
aus dem Route-Wert (ein `channel`-Query-Parameter wird gar nicht gebunden, sonst läse ein Manager
über seine eigene Route fremde Logs), und die geforderte defensive Projektion von `DetailsJson`
wurde nicht in dieser Seite gebaut, sondern einen Tag zuvor serverseitig für **beide** Endpoints —
s. den DECISIONS-Eintrag „`DetailsJson` verlässt den Server nicht mehr roh".

Ein Tab im Channel-Workspace: wer hat wann gejoint/verlassen, Sessions angelegt/beendet/gelöscht,
Emotes als gelöscht gemeldet, Resync ausgelöst.

**Warum es zieht.** Die Lücke mit dem besten Aufwand-Nutzen-Verhältnis im ganzen Repo — Daten,
Compound-Index `(ChannelName, OccurredAtUtc DESC)` und Query-Service sind fertig, es fehlt nur die
Route. Heute kann ein Broadcaster nicht sehen, welcher seiner Mods eine Abstimmung gelöscht hat; das
steht ausschließlich im Global-Admin-Log. In einem Mod-Team mit 15 Leuten ist das die erste Frage
nach jedem Missverständnis.

**Datenlage.** Reicht vollständig. `IAuditLogQueryService` + `AuditLogFilter` können es bereits. Neuer
Endpoint `/api/channels/{c}/audit-log` hinter dem Manager-Filter — Regel 4/6 vollständig eingehalten.

**Aufwand.** S · **Risiko.** **Attribution ist sensibel.**
[SevenTV/Extension#267](https://github.com/SevenTV/Extension/issues/267), ein Streamer wörtlich:
„I don't really want that everyone knows if I add or remove an emote." Deshalb: Manager-only, nicht
für alle Voter. `DetailsJson` nicht roh ausliefern (kann Interna tragen), sondern dieselbe defensive
Projektion wie im Admin-UI.

### A8 — Resync-Button für Channel-Manager

**Status: ✅ umgesetzt am 2026-08-02.** `POST /api/channels/{c}/resync` hinter
`UsageStatsAccessAuthorizationFilter` — also wie hier gefordert **inklusive 7TV-Editoren**, im
Gegensatz zum Aktivitätsverlauf (A7) direkt daneben. Der geforderte Missbrauchsschutz ist beides
geworden: die neue Policy `ChannelResync` (5/min) **plus** `IChannelResyncCooldown`, ein
Redis-`SET NX EX` pro Channel (60 s). Die Begründung, warum keiner der beiden allein reicht, und die
Erkenntnis, dass die eigentlich teure Ressource nicht 7TV ist, sondern der `channel.synced`-Fanout
an alle offenen Seiten, stehen im DECISIONS-Eintrag „Resync als Self-Service".

Der 7TV-Vollsync, den heute nur der Global-Admin auslösen kann, im Channel-Workspace für
Broadcaster, Mods und 7TV-Editoren.

**Warum es zieht.** Der häufigste Support-Fall überhaupt: „ich hab ein Emote hinzugefügt, es taucht
nicht auf." Heute lautet die Antwort „warte den nächsten Tick ab" — oder jemand schreibt dem Admin.
Der ganze Rest der App ist rollengegated Self-Service; das hier ist die Ausnahme.

**Datenlage.** Reicht. `IChannelService` hat den Resync-Trigger, `RESYNC:<name>` geht über den
bestehenden Redis-Kanal.

**Aufwand.** S · **Risiko.** Missbrauchsfläche — jeder Resync ist ein 7TV-REST-Call. Braucht eine
eigene, gegenüber `ExternalApi` deutlich strengere Rate-Limit-Policy **plus** serverseitigen Cooldown
pro Channel, sonst hämmert ein gelangweilter Mod 40×/min gegen 7TV. Audit-Eintrag `channel.resync`
existiert bereits.

### A9 — Globale Verbreitung als zweite Achse: „14 Channels" vs. „300.000 Channels"

Pro Emote die 7TV-weite Verbreitung und der globale Trend, als Kontextspalte in der Voting-UI.

**Warum es zieht.** Verändert die Löschentscheidung fundamental. Ein Emote mit 5 lokalen Nutzungen,
das global in 300k Sets steckt, ist ein generisches Standard-Emote, das man risikolos rauswirft; eins
mit 5 Nutzungen und 14 Channels ist channel-eigene Identität. Diese Unterscheidung bietet **kein**
anderes Tool an.

**Datenlage.** Neuer externer Call. 7TV GQL v3: `Emote { channels { total }, trending }` — live
verifiziert und ohne Auth abrufbar (GIGACHAD: 304.867 Channels, `trending: 67`; gachiHYPER: 57.711,
`trending: null`). v4 GQL hat es reicher als
`EmoteScores { trendingDay/Week/Month, topDaily/…/topAllTime }`. Braucht eine Spalte auf `Emote`
plus einen langsam laufenden Anreicherungs-Worker.

**Aufwand.** M · **Risiko.** Der Hauptfallstrick ist das Volumen: 900 Emotes × N Channels gegen ein
Rate-Limit von 5.000 (`x-ratelimit-global-limit`), zusätzlich ein serverseitiger Query-**Complexity**-Analyzer.
Zwingend: aliasierte Batch-Queries, Cache mit Tages-TTL, eigener Worker mit Backoff — **nicht** in den
60-s-Resync einweben. `Emote.common_names` ist in v3 faktisch tot (leeres Array bei allen getesteten
Emotes) — nicht darauf bauen. Für *neue* Felder ist v4 die reichere Quelle; das ist weiterhin kein
Migrationsgrund für den Bestand (vgl. Entscheidung 2026-07-30), aber ein Argument, v4 punktuell
danebenzustellen.

### A10 — Nutzung pro Live-Stunde statt pro Kalendertag

**Status: 🟡 Stufe 1 umgesetzt am 2026-08-03** — App-Access-Token-Flow (`ITwitchAppTokenProvider`),
`TwitchLivePollWorker` (Helix-Streams-Poll, Default 300 s), Entität `ChannelLiveDay` (LiveMinutes pro
Channel pro UTC-Tag) und Stream-Tage-Markierung im Emote-Drilldown (`liveDays` in der
`/usage-stats/daily`-Antwort). Begründung in DECISIONS „Live-Abdeckung pro Tag …". **Stufe 2**
(Nutzung pro Live-Stunde als umschaltbare Ansicht) ist offen; die Minuten-Erfassung ist dafür
ausgelegt.

Zusätzliche Normalisierung: Emote-Nutzung geteilt durch die Stunden, die der Channel im Zeitraum
tatsächlich live war.

**Warum es zieht.** Behebt eine echte Verzerrung. Ein Channel, der in den gewählten 30 Tagen nur
sechsmal gestreamt hat, produziert Zahlen, die mit einem Daily-Streamer nicht vergleichbar sind — und
innerhalb eines Channels verzerren Urlaubswochen jeden Zeitraumvergleich. Betrifft direkt **A4**.

**Datenlage.** Neuer Sync, aber billig: `GET /helix/streams` braucht **keinen Scope**, läuft mit
App-Token und nimmt 100 Channels pro Request; offline = Channel fehlt im `data`-Array. Ein Poll alle
paar Minuten im Worker plus eine schmale Entität „Live-Minuten pro Channel pro Tag". Nebeneffekt: die
Join-Steuerung könnte sich am Live-Status ausrichten und damit direkt das Concurrent-Join-Limit
entlasten.

**Aufwand.** M · **Risiko.** Neue Entität + Migration. Ein **App-Access-Token im Worker** existiert
heute nicht — das war 2026-07-27 in anderem Kontext schon einmal ein Ablehnungsgrund, ist also ein
echter neuer Baustein. Nicht als Default-Metrik ausrollen, sondern als umschaltbare Ansicht, sonst
erklärt niemand, warum sich die Zahlen geändert haben.

### A11 — Duplikat-Erkennung, Stufe 1 (Name)

Emotes, deren normalisierte Namen kollidieren (`peepoSad` / `PeepoSad2` / `peeposad_`), plus Aliase,
die vom Originalnamen abweichen, als Gruppe anzeigen — mit den Nutzungszahlen nebeneinander.

**Warum es zieht.** Etablierte Nachbarschaft ohne Nutzungsdaten:
[GreenComfyTea Dup Emote Check](https://greencomfytea.github.io/duplicate-emote-check-tool/) findet
Duplikate über Twitch/FFZ/BTTV/7TV, [Emote Content ID](https://twitch-tools.rootonline.de/emotes_content_id.php)
sogar bildähnliche. Beiden fehlt der Satz, den ihr als Erste sagen könntet: „Diese beiden sind quasi
identisch, dieses hier hat 40× mehr Uses — lösch das andere."

**Datenlage.** Reicht für Stufe 1. Der Alias steckt bereits im Sync-Response: `emotes[].name` ist der
Alias im Set, `emotes[].data.name` der Originalname (live bei KarmikKoala: `AYAYA → AYAYA2`,
`GigaStul → Stul`). Persistiert wird heute nur eines der beiden.

**Aufwand.** S–M · **Risiko.** Falschpositive („OMEGALUL" vs. „OMEGALULiguess" sind zwei Emotes). Als
*Hinweis* rendern, nie als Vorauswahl.

### A12 — Ergebnis-Export (CSV/JSON) für Voting und Usage

**Status: ✅ umgesetzt am 2026-08-02** — als reine Client-Serialisierung des geladenen Read-Models
(kein Export-Endpoint), Export-Dialog mit Formatwahl auf beiden Seiten, exportiert wird die sichtbare
(gefilterte) Liste. Verdeckte Werte (Secret Ballot, manager-only Usage) fallen als ganze Spalte weg
statt leer zu exportieren; im JSON stehen sie im `withheld`-Feld des Envelopes. Begründung in
DECISIONS „Der Export ist eine Client-Serialisierung, und eine verdeckte Spalte fehlt statt leer zu
sein".

Download-Button auf der Vote-Session-Detailseite und im Usage-Grid.

**Warum es zieht.** Mod-Teams arbeiten in Discord und Spreadsheets, nicht in eurer UI. Der einzige
Teilen-Weg ist heute „Link kopieren" — und der zwingt jeden Empfänger durch Login *und*
Zielgruppenprüfung.

**Datenlage.** Reicht vollständig, ist eine Serialisierung des bestehenden Read-Models.

**Aufwand.** S · **Risiko.** Muss dieselben Sichtbarkeitsregeln respektieren wie der Endpoint: bei
laufendem `HideResultsUntilEnd` **kein** Tally-Export, sonst hebelt der Download den Secret Ballot
aus. `totalUseCount` bleibt manager-only.

### A13 — Unique-Chatter je Emote (Reichweite statt Volumen)

Pro Emote und Tag zusätzlich die Anzahl **verschiedener** Chatter — als Filter „hohe Nutzung, aber
weniger als drei Personen".

**Warum es zieht.** Trennt den Superfan vom Community-Emote. 5.000 Uses von einer Person sehen in
jeder Top-Liste kerngesund aus und sind trotzdem ein Löschkandidat. Twitch validiert die Metrik
selbst (Emote-Analytics-Spalte „Unique Users"), aber nur für Twitch-eigene Emotes — für 7TV gibt es
sie nirgends.

**Datenlage.** Neue Dimension, und hier liegt der interessante Teil: `UsageStat` kennt heute bewusst
keine Nutzerdimension. Datensparsam zu bekommen wäre sie so — der Worker hält pro (Emote, Tag) ein
HyperLogLog oder Hash-Set im Speicher und persistiert beim 30-s-Flush **nur die Kardinalität**, nie
die Identitäten. Additive `UniqueChatterCount`-Spalte auf `UsageStat`.

**Aufwand.** L · **Risiko.** **Das größte Risiko der Liste, und zwar rechtlich.** Auch eine rein
transiente In-Memory-Verarbeitung von Chatter-Namen ist DSGVO-relevante Verarbeitung
personenbezogener Daten und ändert das Datenschutz-Profil — das derzeit hervorragend ist (keine
Chatnachrichten, keine Chatter gespeichert) und gegenüber Wettbewerbern ohne sichtbare Privacy Policy
ein echtes Alleinstellungsmerkmal darstellt. Kollidiert frontal mit dem offenen Befund **S2-20
(Rechtstexte)**: **nicht anfangen, bevor Impressum und Datenschutzerklärung stehen.** Technisch
zusätzlich: der 30-s-Flush zerschneidet die Kardinalität über den Tag — braucht Cross-Flush-Aggregation.

### A14 — Auffindbarkeit offener Abstimmungen

Eine Seite „Abstimmungen, an denen ich teilnehmen darf" — für Sessions mit `AllowedRoles.Everyone` in
Channels, in denen der Nutzer weder Mod noch Broadcaster noch 7TV-Editor ist.

**Warum es zieht.** Heute strukturell kaputt: eine `Everyone`-Session ist für jeden Eingeloggten
*zugänglich*, aber für niemanden *auffindbar*, außer er kennt die URL oder hat dort schon einmal
abgestimmt. Die Beteiligungswarnung „< 5 Voter" im UI ist zum Teil ein Discovery-Problem.

**Datenlage.** Reicht — eine Query über aktive Sessions mit `Everyone`. Eine Zuschauer-Bindung
(„welchem Channel folgt der Nutzer") existiert nicht und wäre ein neuer Helix-Call mit Scope.

**Aufwand.** M · **Risiko.** **Muss bewusst gegen zwei Entscheidungen abgewogen werden**: die globale
Channel-Liste wurde bewusst ins Admin-Backend verschoben, und anonyme Share-Links wurden am
2026-07-27 auf Nutzerwunsch komplett zurückgenommen. Diese Idee verletzt keine der beiden — Login und
Zielgruppenprüfung bleiben unangetastet — öffnet aber erstmals wieder eine channelübergreifende Sicht
für Nicht-Beteiligte. Das ist eine Produktentscheidung, keine technische.

### A15 — „Nachrücker-Liste": was kommt rein, wenn Slots frei werden

Eine zweite Session-Art: Vorschläge für *neue* Emotes sammeln und darüber abstimmen, gekoppelt an das
Slot-Budget aus **A1**.

**Warum es zieht.** Der reale Gegen-Workflow, den heute jemand von Hand fährt:
[thatsaurus.com/emote-voting](https://www.thatsaurus.com/emote-voting) reserviert zwei Slots fürs
Community-Voting und wickelt das komplett per Chat-Bot, Channel Points und Bits ab. Aufräumen und
Nachfüllen sind für ein Mod-Team ein Vorgang, nicht zwei.

**Datenlage.** Substanziell neu: Vorschlagsentität mit 7TV-Emote-ID (nicht in eurem `Emote`-Bestand,
der channel-scoped ist — **Regel 8 kollidiert direkt**), 7TV-Suche über GQL
(`emotes(query, filter, sort)` mit Kategorien `TOP | TRENDING_DAY/WEEK/MONTH | FEATURED | NEW | GLOBAL`;
einen REST-Such-Endpoint gibt es nicht, `GET /v3/emotes?query=…` antwortet 405), und ein
Hinzufüge-Pfad, der wieder im Browser laufen muss.

**Aufwand.** L · **Risiko.** Erweitert das Produkt von „Purge" auf „Set-Management" — eine
Positionierungsentscheidung. Vorschlags-Spam ist eine neue Missbrauchsfläche und braucht
Per-User-Limits (chat.vote löst das mit einem `!suggest`-Limit). Regel 8 zwingt zu einem eigenen
Datenmodell.

### A16 — Emote-Liste importieren: kopieren statt nur wiederherstellen (Nachtrag 2026-08-04)

**Status: ✅ umgesetzt am 2026-09-06** (#72, K3) — Ziel-Picker (Bereich + Zielkanal oder Datei) und
Bestätigungsdialog (Herkunft, Ziel, Slot-Projektion, Namenskollisionen) als neue Dialoge, Token-Prompt
hier bewusst **nach** der Bestätigung statt davor; Datei-Weg über eine eigene `emote-list`-Envelope
plus Wiederverwendung eines Nutzungs-Exports als Quelle; das Restore-Panel nimmt dieselbe Datei
zusätzlich für Emote-Listen/Nutzungs-Exporte an und startet dafür denselben Import-Lauf. Details in
DECISIONS (fünf Einträge vom 2026-09-06) und docs/UI-Designsprache.md §7.2.

Eine hochgeladene Emote-Liste dem aktiven Set hinzufügen, **ohne** dass sie aus einem Löschlauf
genau dieses Sets stammen muss — der kurze Weg, um ein Set in ein zweites zu übernehmen
(Zweitkanal, Test-Set, Set-Wechsel).

**Warum es zieht.** Die Maschinerie steht bereits: der Restore-Import aus **A6** *ist* schon „nimm
diese Liste 7TV-IDs und füge sie dem aktiven Set hinzu", inklusive Token-Prompt, Slot-Anzeige im
Bestätigungsdialog und Fortschrittsanzeige. Was fehlt, ist nicht der Ausführungsweg, sondern die
Erlaubnis: `parsePurgeRunProtocol` weist eine Datei ab, deren `channelName` oder `meta.emoteSetId`
nicht zum aktuellen Kanal und Set passt. Genau diese zwei Prüfungen sind für einen Restore richtig
und für ein Kopieren falsch. Anlass war eine Nutzerfrage vom 2026-08-04 — der Usage-Stats-Export
wurde ins Restore-Panel hochgeladen, in der Annahme, Export und Import seien ein Paar. Die
Fehlermeldungen sind seither schärfer (`restore.import.errors.usageExport`), aber die Erwartung
dahinter war ein echter Wunsch.

**Datenlage.** Reicht, kein neues Backend — das Hinzufügen läuft wie jeder Schreibzugriff im
Browser gegen 7TV. Braucht eine vierte Envelope-Sorte `kind: 'emote-list'` mit
`rows: [{ sevenTvEmoteId, name }]` statt einer Zweckentfremdung des Purge-Protokolls; dessen `kind`
steht genau dafür im Dateiformat, damit ein Import nicht raten muss. Der interne `emoteId` (Regel 8)
gehört bewusst **nicht** in so eine Datei — er ist channel-scoped und im Zielkanal bedeutungslos.

**Aufwand.** S–M · **Risiko.** Der Bestätigungsdialog trägt hier die ganze Last: ohne Set-Prüfung
sagt keine Validierung mehr „das gehört woanders hin", also muss er Herkunft, Zielset und freie
Slots nebeneinander zeigen — ein 900er-Set in ein Set mit 50 freien Slots zu kippen, muss vorher
sichtbar sein statt als Fehlerregen danach. Zweiter Punkt sind Namenskollisionen, und die sind am
2026-08-04 aus 7TVs Quellcode geklärt (`apps/api/src/http/v3/gql/mutations/emote_sets/`, gleiche
Logik in v4): `ADD` lehnt mit `this emote has a conflicting name` ab, sobald der Alias im Zielset
schon vergeben ist. **Den Alias wegzulassen hilft nicht** — `EmoteSetEmote.alias` ist ein `String`,
kein `Option<String>`, und wird bei fehlender Angabe mit `emote.default_name` befüllt
(`let alias = name.unwrap_or_else(|| emote.default_name.clone())`). Ein Set-Kopieren muss also
damit rechnen, dass ein Teil der Liste abprallt, und das als Ergebnis zeigen statt als Fehler —
die Purge-Protokoll-Zeilen mit `status: 'failed'` sind das Muster dafür. Der Vergleich ist
case-sensitiv und unnormalisiert, deckt sich also exakt mit dem ordinalen Vergleich in
`EmoteSetStatusService` (die Duplikat-Erkennung ist seit #45 dort gefaltet, kein eigener
Query-Service mehr): was unser Duplikat-Banner zeigt, ist auch für 7TV eine Kollision.

---

## B) Admin-Panel

Sortiert nach Wirkung ÷ Aufwand.

### B1 — Support-Drilldown pro Channel: „warum syncht der nicht?"

**Status: 🟡 teilweise umgesetzt am 2026-08-01** (Commits `de97e07`…`76cb5d6`, DECISIONS-Eintrag
„Der Worker sagt, in welchen Channels er wirklich ist"). `GET /api/admin/channels/{name}` plus
`admin-channel-detail-page.ts` beantworten die IRC-Frage und die EventAPI-Frage (Subscriptions inkl.
`acked`, dazu ein Set-ID-Abgleich DB gegen Worker, der über die ursprüngliche Anforderung hinausgeht)
sowie den letzten erfolgreichen Sync. **Offen:** der letzte Flush **mit Zeilen für diesen Channel**
(Flush-Zahlen liegen bis heute nur global im Health-Snapshot) und die letzten ~20 **Audit-Zeilen**
(der Endpoint zieht `IAuditLogQueryService` nicht, und die Admin-Audit-Log-Seite nimmt keine
Query-Parameter an, ist also auch nicht channel-gefiltert verlinkbar).

Eine Detailansicht je Channel, die die vier Frageketten eines Support-Falls auf einer Seite
beantwortet: Ist der Channel im IRC gejoint? Existiert eine EventAPI-Subscription für sein Emote-Set,
und ist sie acked? Wann war der letzte erfolgreiche Sync, wann der letzte Flush mit Zeilen für diesen
Channel? Was steht in den letzten 20 Audit-Zeilen?

**Warum es zieht.** Der wörtlich genannte Support-Fall — heute unbeantwortbar. Der Admin sieht globale
Aggregate („EventAPI connected", „desired: 14") und pro Channel ein `LastSyncedAtUtc`. Ob *dieser eine*
Channel Nachrichten empfängt, ist nirgends sichtbar; die Diagnose läuft über SSH und
`docker compose logs`.

**Datenlage.** DB- und Audit-Seite sind vollständig da. Lücke im Worker-Snapshot: der Worker müsste
seinen Ist-Zustand **pro Channel** exportieren (IRC-Join-Set, `SevenTvSubscriptionRegistry`-Zustand,
letzter Nachrichtenzeitpunkt je Channel). `WorkerHealthSnapshot` ist heute rein global — das wäre eine
Erweiterung des bestehenden Redis-Wegs, kein neuer Transport.

**Aufwand.** M · **Risiko.** Snapshot-Größe: der Health-Key hat 60 s TTL und wird alle 20 s
geschrieben. Bei 100 Channels ist eine Zeile pro Channel unkritisch, bei 1.000 nicht — nicht das ganze
Roster in jeden Heartbeat packen, sondern einen zweiten Key mit längerem Takt.

### B2 — Soll/Ist-Abgleich des IRC-Rosters + Kapazitäts-Frühwarnung

**Status: ✅ vollständig umgesetzt am 2026-08-02** (Frühwarnung nachgezogen; Soll/Ist-Teil vom
2026-08-01, `GET /api/admin/roster` + `admin-roster-card.ts`).
Der Soll/Ist-Abgleich ist vollständig da, inklusive der Gegenrichtung („Worker hat Channel, DB nicht
mehr aktiv") und mit Boot-Recovery- und Staleness-Gate gegen Fehlalarme. Bei den Decken weicht die
Umsetzung bewusst ab: der Twitch-Balken läuft gegen ein **Join-Budget von 20** (selbst gesetzt,
kein Twitch-Riegel — seit dem Reconnect-Umbau begründet über die Rejoin-Dauer nach einem Reconnect,
nicht mehr über einen TwitchLib-Burst; Einzelheiten in `Api/Health/WorkerCapacity.cs`), die 100er-Decke
steht als Hinweistext daneben; 7TV wird gegen das gemeldete `subscription_limit` gezeigt statt gegen
eine abgeleitete 250er-Channel-Decke; für 7TV-REST gibt es bewusst nur eine Rate ohne Balken, weil es
kein veröffentlichtes Quota als ehrlichen Nenner gibt. Die Frühwarnung fährt seit dem 2026-08-02 die
geteilte 80/95-Schwellen-Leiter (`shared/ui/utilization-tone.ts`) auf beiden Balken, mit Warntext ab
80 %; zwei bewusste Abweichungen: das Roster-Status-Badge bekommt keine Kapazitätsstufe, und
7TV-REST bleibt balkenlos — Begründung in DECISIONS „Auslastungsbalken bekommen eine
Schwellen-Leiter, das Roster-Badge nicht".

Ein Panel im Monitoring: „DB sagt 34 aktive Channels, IRC ist in 31 gejoint — diese drei fehlen",
plus Auslastungsbalken gegen die drei harten Decken:

| Decke | Wert | heute beobachtet |
|---|---|---|
| Twitch Concurrent Joins | **100 Channels** pro Verbindung (seit 2024-05-15) | ❌ nirgends |
| 7TV EventAPI | **250 Channels** (`subscription_limit` 500 ÷ 2 Subs je Channel) | 🟡 nur 90-%-Log-Warnung |
| 7TV REST | 1 Request je Channel je Resync-Tick (Default 60 s) | ❌ nirgends |

**Warum es zieht.** Der stille Ausfall ist der teuerste. Ein Channel, der nach einem Reconnect nicht
wieder gejoint wurde, zählt einfach keine Emotes mehr — niemand merkt es, und die Usage-Daten sind
dann leise falsch, was direkt auf Löschentscheidungen durchschlägt.

**Datenlage.** Ist-Seite fehlt (dieselbe Worker-Erweiterung wie **B1**), Soll-Seite ist eine triviale
DB-Query.

**Aufwand.** S–M (aufbauend auf B1) · **Risiko.** Keins. Eher Betriebspflicht als Feature — und der
einzige Punkt der Liste, den ich vor einem größeren Rollout ansetzen würde.

### B3 — Health-Historie statt Snapshot

Den Worker-Health-Snapshot in eine schmale Zeitreihe schreiben (z. B. minütlich) und im Monitoring als
24-h-/7-Tage-Verlauf zeigen: Uptime-Anteil, Reconnect-Zeitpunkte, Flush-Fehlerquote über Zeit,
EventAPI-Subscription-Verlauf.

**Warum es zieht.** Heute lebt der Zustand ausschließlich als Redis-Key mit 60 s TTL — es gibt **keine
Historie**. „Ist das gerade zum ersten Mal passiert oder jede Nacht um 4?" ist nicht beantwortbar.
Genau diese Frage stand hinter dem Stuck-Reconnect-Bug, dessen Fix bis heute nicht live verifiziert
ist.

**Datenlage.** Neue Entität mit Retention — oder billiger ein Redis-Ringpuffer, falls Persistenz über
Neustarts nicht nötig ist.

**Aufwand.** M · **Risiko.** Wächst unbegrenzt, braucht also eine Retention-Entscheidung (anders als
beim Audit-Log, wo „unbegrenzt" bewusst gewählt wurde). Überschneidet sich teilweise mit dem offenen
**S3-36**: ein externer Uptime-Check plus `healthchecks.io`-Dead-Man's-Switch ist deutlich billiger und
beantwortet „läuft es überhaupt", aber nicht „wie war die Qualität".

### B4 — Wachstums- und Nutzungs-Dashboard

Eine Kennzahlenseite: getrackte Channels über Zeit, Nutzer gesamt, Sessions und Votes pro Woche,
Emotes unter Beobachtung, gelöschte Emotes gesamt, Top-Channels nach Aktivität.

**Warum es zieht.** Vor dem Launch (Modul E) gibt es keine Zahl, an der Wachstum ablesbar wäre. Für
die anstehende Invite-only-vs-Veröffentlichung-Entscheidung ist „wie viele Leute nutzen das
eigentlich" die Grundlage.

**Datenlage.** Teilweise — und die Lücke gehört ehrlich benannt. Channel-Zuwachs geht über
`Channel.CreatedAt`, Session- und Vote-Volumen gehen. **Aktive Nutzer gehen nicht sauber**: `User` hat
nur `LastLogin` (wird überschrieben) und **kein `CreatedAt`**; „Neuanmeldungen pro Woche" und jedes
DAU/WAU sind mit dem heutigen Schema nicht rekonstruierbar. Login-Events wurden bewusst aus dem
Audit-Log herausgehalten (Rauschen) — das bleibt richtig. Der billige Fix ist eine additive
`User.CreatedAt`-Spalte: ab dann korrekt, rückwirkend nicht.

**Aufwand.** S–M · **Risiko.** Keins, solange man nicht so tut, als seien die Zahlen rückwirkend
vollständig.

### B5 — Vote-Manipulations-Signale

Eine Auffälligkeitenliste je Session: Voter, die in unter einer Minute den halben Wahlzettel
einheitlich auf Delete gesetzt haben; Accounts, deren erster Login unmittelbar vor der Session lag;
ungewöhnlich hohe Voter-Überschneidung zwischen Sessions verschiedener Channels; Sessions, deren
Ergebnis von sehr wenigen Personen getragen wird.

**Warum es zieht.** Das Ergebnis löscht am Ende echte Emotes aus einem fremden Set. Sobald das Tool
über einen Channel hinaus bekannt wird, ist Brigading die naheliegendste Angriffsform — und
`AllowedRoles.Everyone` ist der offene Eingang. Heute existiert dafür **kein einziges Signal**.

**Datenlage.** Teilweise, mit einer relevanten Lücke: `Vote.UpdatedAt`, `Vote.UserId` und
`User.LastLogin` sind da, aber `Vote` hat **kein `CreatedAt`** — ein Umstimmen überschreibt die Zeile,
„wie schnell wurde ursprünglich abgestimmt" ist also nur für unveränderte Stimmen sauber. Eine
additive `CreatedAt`-Spalte macht die Zeitanalyse belastbar; ein voller Vote-Verlauf wäre eine eigene
Entität und wäre es nicht wert.

**Aufwand.** M · **Risiko.** **Falschpositive sind der Normalfall** — ein Mod, der 200 offensichtlich
tote Emotes schnell durchklickt, sieht exakt aus wie ein Angreifer. Deshalb strikt als Hinweisliste
für den Admin, niemals als automatische Sperre und niemals als Anzeige für den Broadcaster.
Datenschutz: das ist eine Auswertung individuellen Nutzerverhaltens und gehört in die
Datenschutzerklärung, bevor es gebaut wird.

### B6 — Datenhygiene-Ansicht

Eine Liste der stillen Problemfälle: aktive Channels ohne Usage-Zeilen in den letzten N Tagen;
Channels, deren 7TV-Verknüpfung oder Emote-Set verschwunden ist; Channels mit `IsBotActive = true`, in
denen sich seit Wochen niemand eingeloggt hat; Emotes, die seit Monaten archiviert sind und nur noch
Historie tragen.

**Warum es zieht.** Jeder Zombie-Channel kostet dauerhaft einen IRC-Join (gegen die 100er-Decke), zwei
EventAPI-Subscriptions (gegen die 250er-Decke) und einen 7TV-REST-Call pro Tick. Bei knappen Decken ist
Aufräumen der billigste Kapazitätsgewinn — und der Admin hat heute keine Sicht darauf, welche Channels
tot sind.

**Datenlage.** Reicht vollständig; Aggregat-Queries über `Channel`, `UsageStat`, `Emote`.
`IAdminChannelQueryService` rechnet die halbe Sache bereits.

**Aufwand.** S–M · **Risiko.** Regel 10 (`GroupBy`-Übersetzung) beachten. Nur **anzeigen**, nie
automatisch aufräumen — `purge` ist irreversibel und hat aus gutem Grund einen TypedConfirm.

### B7 — Audit-Log: Detailfilter und Export

Filter auf ein `DetailsJson`-Feld (z. B. alle Purges mit mehr als 100 Emotes) plus CSV-Export der
gefilterten Ansicht.

**Warum es zieht.** Bei unbegrenzter Retention wird das Log irgendwann zum Archiv, das man nur noch
gezielt durchsucht. Der Weg ist bereits vorbereitet: `DetailsJson` wurde bewusst als `jsonb` statt
`text` angelegt, „damit ein späterer Filter eine Query bleibt und keine Migration wird".

**Datenlage.** Reicht vollständig.

**Aufwand.** S · **Risiko.** Der Zeitraumfilter wurde am 2026-07-31 bewusst abgelehnt (UI-Gewicht ohne
Suchgewinn). Für einen Export ist er aber der natürliche Selektor — dann als Export-Parameter, nicht
als vierte Spalte in der Filterleiste.

### B8 — 7TV-/Twitch-Fehlerraten-Telemetrie

Im Monitoring: Fehlerquote und Latenz der ausgehenden Aufrufe (7TV REST, 7TV GQL, Helix),
aufgeschlüsselt nach Statuscode, plus die gelesenen Rate-Limit-Header.

**Warum es zieht.** Drei externe Abhängigkeiten, für keine eine Kennzahl. 7TVs REST-Cache kann
10–30 min veraltet sein, Helix arbeitet mit Token-Buckets, 7TV GQL hat
`x-ratelimit-global-limit: 5000` plus Complexity-Analyzer. Sobald **A9** oder **A10** kommen, wird das
von „nice" zu „notwendig".

**Datenlage.** Neu, aber lokalisiert: Zähler in den typisierten HttpClients, publiziert über denselben
Health-Snapshot.

**Aufwand.** M · **Risiko.** Nicht auf den Mass-Delete übertragbar — der läuft im Browser, und dessen
Rate-Limit-Header sind per CORS nicht lesbar. Das bleibt bei der Laufzeit-Lernstrategie; ein
Server-Proxy dafür wurde am 2026-08-01 ausdrücklich verworfen (bricht Zero-Knowledge).

### B9 — DB-gestützte Admin-Verwaltung

Admins in der Datenbank statt in `Auth:AdminTwitchLogins`, mit Ernennen/Entziehen im UI und
Audit-Eintrag.

**Warum es zieht.** Im Entscheidungslog selbst als „der naheliegende nächste Schritt, sobald das
gebraucht wird" benannt. Heute kostet jede Admin-Änderung ein Config-Deployment.

**Datenlage.** Neue Spalte oder Tabelle plus Migration.

**Aufwand.** M · **Risiko.** **Es gibt genau einen Admin — der Auslöser fehlt.** Warten, bis ein
zweiter Mensch tatsächlich Admin werden soll; vorher ist das Aufwand ohne Nutzen. Ein
Bootstrap-Fallback (Config gewinnt immer) wäre Pflicht, sonst sperrt eine falsche DB-Zeile den letzten
Admin aus.

### B10 — LIVE-Badge in den Channel-Listen (Nachtrag 2026-08-03)

**Status: ✅ umgesetzt am 2026-08-03** — Redis-TTL-Key `worker:live-status` (`TwitchLivePollWorker`
publiziert, `TwitchLiveStatusStore` liest), dreiwertiger `liveState` (`live`/`offline`/`unknown`) in
beiden Listen-Antworten plus `livePolledAtUtc`, Badges in Admin-Channel-Liste und „Meine Channels"
mit „Stand vor x min"-Tooltip; `unknown` bleibt bewusst badge-los. Begründung in DECISIONS
„LIVE-Badge in den Channel-Listen: der Worker publiziert den Live-Poll-Momentzustand …"; seit
2026-08-05 mit `live.changed`-Push (SSE) statt Refresh-only. Nachtrag
vom 2026-08-03 (Nutzerwunsch während der Watchdog-Runde), nicht Teil der ursprünglichen Session vom
2026-08-01.

In der Admin-Channel-Liste und in „Meine Channels" anzeigen, ob ein Kanal gerade live ist.

**Warum es zieht.** Die Daten existieren seit A10 Stufe 1: `TwitchLivePollWorker` fragt ohnehin alle
5 Minuten Helix, welche aktiven Channels live sind — die Antwort wird heute nur in Tagesminuten
(`ChannelLiveDay`) verdichtet und wirft den Momentzustand weg. Für Admins erklärt ein LIVE-Badge auf
einen Blick, warum ein Channel gerade Nachrichten-Traffic hat (oder warum Stille normal ist); für
Broadcaster/Mods ist es schlicht Orientierung.

**Datenlage.** Kein neuer Poll nötig: der Worker publiziert den letzten Live-Zustand (Set der
Live-Logins) z. B. als TTL-Key nach Redis — dasselbe Muster wie `worker:health:twitch` — und die Api
reicht ihn in den bestehenden Listen-Antworten mit durch. Alternative (Api fragt Helix selbst) wäre
ein zweiter Konsument des App-Tokens; der DECISIONS-Eintrag zu A10 warnt genau davor (parallele
Client-Credentials-Grants widerrufen sich gegenseitig), also beim Worker als einzigem Token-Halter
bleiben.

**Aufwand.** S–M · **Risiko.** Anzeige hinkt dem Poll-Takt bis zu 5 Minuten hinterher — als Badge mit
„Stand vor x min" unkritisch, aber nicht als Echtzeit versprechen. Frontend-Zurückhaltung beachten:
ein kleines Badge in bestehenden Listen, kein neues Dauer-Control.

---

## Würde ich nicht bauen

### 1. Chat-Bot mit `!keep`/`!delete`-Commands

Verlockend (mehr Beteiligung, erreicht Leute, die nie eine Website öffnen), aber der Preis ist die
gesamte Architektur: `justinfan` ist read-only, ein Bot-Account braucht einen verifizierten
Twitch-Account, eigene Tokens im Worker, ein neues Missbrauchs- und Moderationsprofil — und pro Channel
eine Zustimmung. Der Kern-Use-Case ist ein *Mod-Team*, das kuratiert, nicht der Massen-Chat. Der Nutzen
rechtfertigt die Verdopplung der Betriebsfläche nicht.

### 2. Top-Chatter-Leaderboards / „wer nutzt dieses Emote am meisten"

Technisch aus derselben Pipeline wie **A13** zu holen und deshalb naheliegend. Aber es macht aus einem
Aggregat-Tool ein Personen-Tracking-Tool, mit voller DSGVO-Konsequenz und Auskunfts-/Löschpflichten.
Der einzige Wettbewerber, der das offensiv bewirbt, nimmt Geld dafür und hat keine sichtbare
Datenschutzerklärung — das ist nichts, was man kopiert. Die Datensparsamkeit ist ein Verkaufsargument,
kein Mangel. (**A13** ist der Grenzfall: Kardinalität ohne Identitäten ist verteidigbar, ein Ranking
nach Personen nicht.)

### 3. Geplante oder automatische Purges („lösch alles unter 5 Uses in 30 Tagen")

Bricht **Grundsatz 4** direkt — serverseitiges Löschen bräuchte das 7TV-Schreib-Token im Backend, und
die Zero-Knowledge-Architektur ist eine bewusste, gut begründete Entscheidung. Unabhängig davon: eine
irreversible Massenaktion ohne Menschen davor ist genau der Fehlermodus aus Extension#650, nur
automatisiert.

### 4. Multi-Provider-Support (BTTV, FFZ, Twitch-Sub-Emotes gleichrangig)

Verdreifacht Sync-Fläche, Rate-Limit-Risiko und Datenmodell für einen Bruchteil der Relevanz — 7TV ist
im deutschsprachigen Twitch-Raum der De-facto-Standard.
**Ausnahme:** Twitchs eigene Emote-Endpoints (`GET /helix/chat/emotes`, **kein Scope**) *lesend*
mitzunehmen, um in der Duplikat-Ansicht zu sagen „dieses 7TV-Emote hast du auch als Sub-Emote", ist
billig und sinnvoll. Das ist Kontext, nicht Verwaltung.

### 5. Session-Bearbeitung nach dem Anlegen (Titel, Wahlzettel, Zielgruppe ändern)

Naheliegend als UX-Wunsch, aber eine laufende Abstimmung, deren Regeln sich ändern können, ist keine
Abstimmung mehr. Die Unveränderlichkeit ist konsistent mit den Entscheidungen vom 2026-08-01 (kein
`HideResultsUntilEnd`-Toggle, kein Session-Scheduler, all-or-nothing beim Wahlzettel) — dieselbe
Begründung sollte hier gelten. Bei falsch angelegten Sessions ist Löschen und neu Anlegen der
ehrlichere Weg.

### 6. Perceptual Image Hashing für bildähnliche Duplikate

Fachlich reizvoll — Emote Content ID zeigt mit seinem „Max difference 0–50"-Regler, dass es geht. Aber:
Bilder herunterladen (animierte WebP), Hashes berechnen und speichern, Schwellwerte tunen, und das für
~900 Emotes je Channel. Ein eigenes Teilprojekt für einen Randfall. **Stufe 1 (Namensähnlichkeit,
A11) fängt den überwiegenden Teil zu einem Bruchteil der Kosten** — und wenn sie sich als
unzureichend erweist, hat man Daten für die Entscheidung.

---

## Die drei Top-Kandidaten

> **Alle drei wurden am 2026-08-01 angegangen**, die ersten beiden vollständig, der dritte mit den
> oben bei B1/B2 benannten Lücken. Die Begründungen darunter stehen unverändert im Stand vom
> 2026-08-01.

### 1. A1 — Slot-Budget (`capacity` / `emote_count`)

Beste Wirkung pro Aufwand in der gesamten Liste: die Zahlen liegen bereits in einem Response, den der
Sync ohnehin abholt und dann wegwirft — der einzige Aufwand sind zwei Spalten und eine Migration. Und
es gibt dem Produkt zum ersten Mal ein *Ziel* statt nur einer Liste: „847/1000, nach dieser Purge
612" ist der Satz, der aus einer Emote-Tabelle ein Aufräum-Tool macht.

### 2. A2 + A3 + A4 als ein Paket — „zuletzt benutzt", Karenz und Trend

Das ist die Antwort auf SevenTV#197, die 7TV seit Dezember 2024 nicht liefert, und die drei Teile
funktionieren einzeln nur halb: „nie benutzt" ohne „getrackt seit" ist irreführend, und ohne Karenz
löscht das Mod-Team beim ersten Durchlauf die frisch hinzugefügten Emotes. Zusammen brauchen sie keine
neue externe Abhängigkeit, keinen neuen Sync und keinen neuen Worker — nur zwei Queries auf einem
Index, der genau dafür gebaut wurde, plus ein Feld aus dem 7TV-Response.

### 3. B1 + B2 — Support-Drilldown und Soll/Ist-Roster

Steht bewusst vor den restlichen Nutzer-Features, weil es das größte offene *Risiko* schließt statt
eines Wunsches: mit dem 100-Channel-Concurrent-Limit, dem 250-Channel-EventAPI-Limit und dem
undokumentierten `justinfan`-Status gibt es drei Decken, von denen genau eine überhaupt beobachtet
wird — und ein nicht-gejointer Channel fällt nicht auf, er liefert nur leise falsche Zahlen, auf deren
Basis dann Emotes gelöscht werden. Es beantwortet außerdem den konkreten Support-Fall, für den heute
SSH und `docker compose logs` die einzige Antwort sind.

---

## Quellen

**7TV — live gegen `7tv.io` verifiziert** (REST-Responses und GraphQL-Introspection, nicht aus Doku
übernommen):

- `GET /v3/emote-sets/{id}` → `emote_count`, `capacity`; `GET /v3/emotes/{id}` → `flags`, `tags`,
  `lifecycle`, `state`, `owner`, `versions`
- `POST /v3/gql`: `Emote { channels { total }, trending, common_names }` — `common_names` ist in v3
  faktisch tot (leeres Array bei allen Testkandidaten)
- `POST /v4/gql`: `Emote { scores { trendingDay/Week/Month, topDaily/…/topAllTime }, flags {…} }`,
  `User { editableEmoteSetIds }`
- Kein REST-Such-Endpoint: `GET /v3/emotes?query=…` → `405 Method Not Allowed`; Discovery nur über GQL
- Rate-Limit-Header `x-ratelimit-global-limit: 5000` plus serverseitiger Query-Complexity-Analyzer
- [SevenTV/SevenTV](https://github.com/SevenTV/SevenTV) (aktiver Monorepo) ·
  [SevenTV/API](https://github.com/SevenTV/API) und
  [SevenTV/EventAPI](https://github.com/SevenTV/EventAPI) (beide 2024 archiviert, laufen aber weiter)

**Community-Belege:**

- [SevenTV/SevenTV#197](https://github.com/SevenTV/SevenTV/issues/197) — „hard to determine what
  emotes to remove", offen seit 2024-12-29
- [SevenTV/Extension#650](https://github.com/SevenTV/Extension/issues/650) — Bulk-Delete löschte ein
  komplettes Personal-Set
- [SevenTV/Extension#267](https://github.com/SevenTV/Extension/issues/267) — Streamer will
  Add/Remove-Attribution nicht öffentlich
- [SevenTV/7tv-bot](https://github.com/SevenTV/7tv-bot) — 7TVs eigener Usage-Statistik-Bot, seit
  2024-02-22 archiviert

**Twitch:**

- [Concurrent join limits for IRC and EventSub](https://discuss.dev.twitch.com/t/giving-broadcasters-control-concurrent-join-limits-for-irc-and-eventsub/54997)
  — die 100er-Staffelung
- [Twitch Chat on EventSub + Conduits](https://discuss.dev.twitch.com/t/available-today-twitch-chat-on-eventsub-an-api-for-sending-chat-and-the-conduit-transport-method-for-eventsub/54596)
- [API Reference](https://dev.twitch.tv/docs/api/reference/) ·
  [Scopes](https://dev.twitch.tv/docs/authentication/scopes/) ·
  [IRC Migration](https://dev.twitch.tv/docs/chat/irc-migration/)

**Benachbarte Tools:**

- [chat.squeexclips.com/emotes](https://chat.squeexclips.com/emotes) — „Never Used"-Sektion,
  Monats-Breakdown, Einzel-Channel
- [kattah7/7tv-emotes](https://github.com/kattah7/7tv-emotes) ·
  [ZonianMidian/emote-stats](https://github.com/ZonianMidian/emote-stats)
- [ChatStats.live](https://chatstats.live/) — kostenpflichtig, keine sichtbare Datenschutzerklärung
- [GreenComfyTea Dup Emote Check](https://greencomfytea.github.io/duplicate-emote-check-tool/) ·
  [Emote Content ID](https://twitch-tools.rootonline.de/emotes_content_id.php)
- [chat.vote](https://chat.vote/) — Chat-Polls mit 7TV-Emotes, JSON/TXT-Export ·
  [thatsaurus.com/emote-voting](https://www.thatsaurus.com/emote-voting) — manuelles Community-Voting
  fürs Hinzufügen
- [justlog (gempir)](https://github.com/gempir/justlog) — `!justlog optout` als De-facto-Muster für
  Chat-Logging-Datenschutz
- [StreamElements `!emotecount`](https://docs.streamelements.com/chatbot/commands/default/emotecount)
  — „accuracy depending on when the bot joined and started tracking messages"

**Nicht belegbar:** Reddit (r/Twitch, r/moderators) ist für den Crawler gesperrt, der 7TV-Discord
nicht indexierbar. Die O-Töne oben stammen deshalb ausschließlich aus GitHub-Issues, Anbieter-Doku und
öffentlichen Tool-Seiten.
