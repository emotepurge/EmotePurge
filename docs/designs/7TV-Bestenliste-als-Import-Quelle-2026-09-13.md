# Design: 7TVs Bestenliste als Import-Quelle (#148)

Erzeugt von `/office-hours` am 2026-09-13
Branch: `main`
Repo: sensitron/EmotePurge
Status: APPROVED (2026-09-13, durch den Betreiber, nach der Messung zu P3)
Modus: Builder
Folgt auf: `docs/designs/Fremde-Kanaele-als-Import-Quelle-2026-09-09.md` (#147, ausgeliefert
als PR #150 / `84b39d2`). **Kein** Supersedes — jener Entwurf bleibt gültig, dieser ist sein
Folge-Issue.
Review: zwei Claude-Runden (6/10 → 7/10, 16 + 15 Befunde), dann Codex Sol adversarial
(`needs-attention`, 3 × high), dann eine Fable-Schiedsentscheidung über den einen Punkt, an dem
sich Opus-Review und Codex widersprachen. Alle Befunde eingearbeitet.

## Problem

#147 hat den Import-Dialog auf **zwei** Quellen gebracht, nicht auf drei: `SOURCE_OPTIONS`
(`import-source-dialog.ts:55-66`) bietet „Aus einer Datei" und „Aus einem Kanal" — getrackt und
fremd sind im Schritt zusammengefallen. Der Kommentar darüber nennt #148 die „vierte" Quelle und
zählt damit die Begriffe, nicht die Auswahlpunkte. **In der ausgelieferten UI wäre die
Bestenliste die dritte Option.** Dieses Dokument zählt Auswahlpunkte.

Beide heutigen Wege setzen voraus, dass der Nutzer schon weiß, **wo** die Emotes liegen, die er
haben will. Wer nur weiß, dass er „irgendwas Gutes" sucht, hat keinen Einstieg — obwohl 7TV eine
netzwerkweite Rangliste öffentlich und ohne Login anbietet.

#148 stellt die Bestenliste als weitere Quelle daneben. Das Ticket legt dafür eine Dreistufung
an (1: feste Liste, 2: Blättern und Sortierwechsel, 3: Suchfeld und Filter) und begründet die
Reihenfolge mit dem Quotenrisiko. **Diese Begründung ist in dieser Sitzung widerlegt worden**
(s. „Gemessene Ausgangslage"), die Stufung dieses Entwurfs ist deshalb eine andere.

## Was das Ding besonders macht

Der Positionierungssatz aus #147 gilt unverändert: Emote Purge ist das einzige Produkt, dessen
These schon „nicht jedes Emote verdient es zu bleiben" lautet. Dieselbe These auf das
Hinzufügen angewandt heißt: auswählen statt schlucken. Keins der drei bekannten
Kopierwerkzeuge zeigt Popularität — im Markup von merge7tv findet sich kein Treffer für
`trend`, `popular`, `rank` oder `usage`.

Dazu ein Argument, das nur die Bestenliste hat: **sie hat kein Opfer.** Die Fremdkanal-Quelle
hebt jemandes Kuration, und dieses Risiko ist mit #147 bereits ausgeliefert. Eine globale
Rangliste nimmt keinem Einzelnen etwas ab. Sie ist die sozial billigere der beiden Quellen.

## Randbedingungen

- **Messfenster Epic #118 bis 2026-10-07.** Kein Worker-Code. Api- und Frontend-Code sind
  ausdrücklich erlaubt: beide Images werden neu gebaut, das Worker-Image bleibt bitgleich
  (#129 Pfadfilter), es wandert kein Zählpfad auf Prod. #148 braucht keinen Worker-Code.
- **Jeder Stack-Update ersetzt trotzdem den Worker-Prozess.** Epic #118 sagt ausdrücklich, ein
  Vorwärts-Deploy ohne Zählregeländerung setze das Fenster **nicht** zurück und koste nur diesen
  Neustart; „Deploys bündeln" bleibt die Vorgabe, ist aber ein schwaches Argument. **Der
  Zeitanker aus #117 ist kein laufender Preis mehr — #117 ist geschlossen**, die Nachkontrolle
  ist erledigt.
- **Keine Migration, kein Join.** Wie #147 messungsneutral.
- **Die #147-Härtung deckt #148 nicht ab.** `ForeignEmoteSetProviderBudget` (2 gleichzeitig,
  60 Requests/Minute) gilt nur für den Fremdkanal-Pfad, und die Rate-Limit-Policy
  `ForeignEmoteLookup` ist **pro `twitch-user`** partitioniert. 7TVs Eimer zählt pro unserer
  Server-IP; eine nutzerpartitionierte Drossel schützt ihn nicht.
- **Zwei Verträge aus `DECISIONS.md` (2026-09-10) sind betroffen**, nicht nur einer: P5' (die
  Zahlen sind netzwerkweit und nie Standardsortierung) **und** Punkt 5 („Die Sortierung benennt
  eine Eigenschaft des Emotes, nicht die Herkunft der Liste"). Auf einer Bestenliste benennt
  die Sortierauswahl genau die Liste. S. Offene Frage 1.

## Gemessene Ausgangslage

Zwei Live-Sonden gegen `https://7tv.io/v4/gql`, anonym, read-only, insgesamt 9 Requests.
Der Eimer wurde nie unter 96 gedrückt.

### Sonde 1 — wer zieht den Such-Eimer?

| Request | `query` | `sortBy` | Seite | `x-ratelimit-search-remaining` |
|---|---|---|---|---|
| A | leer | `TOP_ALL_TIME` | 1 | 100 → 99 |
| B | leer | `TRENDING_DAILY` | 1 | → 98 |
| C | leer | `TOP_ALL_TIME` | 2 | → 97 |

**7TV bucketet am Feldnamen, nicht am Inhalt von `query`.** Jeder Aufruf von `emotes.search`
zieht 1 — mit leerem Suchbegriff, bei jeder Sortierung, beim reinen Blättern. Damit ist die
Prämisse des Tickets („Stufe 1 unkritisch, erst Stufe 3 berührt den Such-Eimer") widerlegt:
alle drei Stufen teilen einen Zähler, die Stufung nach Quotenrisiko sortiert nichts.

`x-ratelimit-search-limit: 100`, und `reset` zählte über den Lauf 60 → 40 → 18 herunter — also
ein **60-Sekunden-Fenster**, nicht 100 pro Stunde. Bei Überziehung springt `reset` auf
~3583 s; das ist die Messung vom 2026-09-09 in der #147-Spec. Beide Lesarten der Doku stimmen
also, sie beschreiben Normalbetrieb und Strafe.

**Wichtig, weil eine frühere Fassung dieses Dokuments es falsch hatte:** Der Bestandscode
liest diesen Hinweis **nicht** in der gemessenen Form. `ReadResetHintSeconds`
(`SevenTvApiClient.cs:669-699`) liest ausschließlich `errors[].extensions.headers` aus dem
**GraphQL-Payload** — seine eigenen Remarks sagen „We have no confirmation that 7TV sends this
at all". Daneben wird nur `Retry-After` gelesen (`:421`). Der **HTTP**-Header aus der Messung
wird nirgends ausgewertet, auch nicht vom `ProviderRequestTelemetryHandler` (der liest
Twitchs Schreibweise `Ratelimit-*`). Folge ohne Nacharbeit: nach einer Sperre fällt der
Breaker auf 60 s zurück und sondiert im Minutentakt gegen einen gesperrten Eimer. Ob solche
Sonden die Sperre verlängern, ist ungemessen. **Das Erfassen der HTTP-Header ist deshalb Teil
dieses Arbeitspakets, nicht Bestand.**

### Sonde 2 — gibt es einen quotenfreien Weg, und was kostet ein Vorrat?

**Es gibt keinen quotenfreien Weg.** `EmoteQuery.search` ist die einzige sortierbare,
paginierbare Emote-Liste im v4-Schema:

```graphql
type EmoteQuery {
    emote(id: Id!): Emote
    search(filters: Filters, page: Int, perPage: Int, query: String, sort: Sort!, tags: Tags): EmoteSearchResult!
}
```

`Emote.ranking(ranking: Ranking!): Int` und `Emote.scores` sind Felder **auf einem einzelnen,
bereits aufgelösten Emote** — kein Listeneinstieg. Der Root-`search.all` hat gar kein
`sort`-Argument und zieht denselben Eimer (99 → 98 gemessen). v3-REST hat keinen anonymen
Weg: `GET /v3/emotes` antwortet **405** mit `allow: POST`, der POST antwortet **401**.

**Der Vorrat ist dagegen fast gratis, aus einem Grund, der im Ticket nicht steht:**

| `perPage` | Items | `pageCount` | Bytes | `complexity` | Eimer-Kosten |
|---|---|---|---|---|---|
| 100 | 100 | 8 | 5.977 | 7 | **1** |
| 250 | 250 | 3 | 14.778 | 7 | **1** |

Die Kosten hängen **nicht** an `perPage`. **250 ist die harte Obergrenze**, von 7TV selbst
durchgesetzt — `perPage: 500` und `1000` antworten mit
`Failed to parse "Int": the value is 500, must be less than or equal to 250`. Ein Request
liefert also höchstens 250 Emotes; die Hoffnung, eine Top-500 in einem Request zu holen, ist
gemessen widerlegt. Nebenbefund: die abgelehnten Requests lieferten **keine**
`x-ratelimit-search`-Header — eine Validierungsablehnung kostet den Eimer nicht.

`TRENDING_DAILY` hat `totalCount: 705`, `TOP_ALL_TIME` dagegen `totalCount: 1.371.890`.

### Die Erkenntnis daraus

**Die Bruchlinie liegt nicht beim Quotenrisiko, sondern bei der Vorratsfähigkeit.** Eine
Bestenliste ist für alle Nutzer **dieselbe** Liste — anders als bei #147, wo jede Anfrage einen
anderen Kanal betrifft. Der Schlüsselraum ist damit endlich: Sortierungen × Seiten. Ein Vorrat
mit einer Stunde Haltbarkeit deckelt den Verbrauch bei einem festen Betrag pro Stunde,
**unabhängig davon, wie viel geklickt wird.**

Stufe 1 und Stufe 2 des Tickets fallen dadurch zusammen. Stufe 3 ist die einzige, die sich
nicht vorhalten lässt: ein freies Suchfeld hat einen unbegrenzten Schlüsselraum. **Das ist die
Stufengrenze, die das Ticket sucht.**

## Prämissen

1. **P1** — Die Dreistufung nach Quotenrisiko ist widerlegt. Gestuft wird nach
   Vorratsfähigkeit.
2. **P2** — Der Eimer ist 100 Requests pro ~60 s, bei Überziehung ~1 h Sperre.
3. **P3** — **Gemessen am 2026-09-13: der Eimer ist geteilt.** Protokoll v4 → v3 → v4, drei
   Requests, keine Fenstergrenze dazwischen (`reset` 60/60/59):

   | Schritt | Aufruf | `x-ratelimit-search-remaining` |
   |---|---|---|
   | 1 | `v4/gql emotes.search` | 99 |
   | 2 | `v3/gql users(query:)` — der Resync-Pfad | **98** |
   | 3 | `v4/gql emotes.search` | **97** |

   Der v3-Aufruf hat gezogen, und die v3-Antwort liefert die `x-ratelimit-search-*`-Header selbst
   mit. Direkter Beleg, keine Folgerung aus einem Header-Präfix. **Messung (b) ist damit die
   einzige noch offene, und sie ist teuer** — s. Nächste Schritte.

   Eine frühere Fassung hat hier zweimal etwas Falsches behauptet; was übrig bleibt:

   Den Upstream-Request macht `ResolveTwitchUserIdAsync` (`SevenTvSyncService.cs:75`), hinter
   `if (twitchUserId is null)`. `RecordFailedAttemptAsync` (`:341`) schreibt nur die DB-Zeile —
   Symptom, nicht Verbrauch.

   - **Es sind nicht „neu gejointe Kanäle":** `ChannelService.JoinAsync` setzt
     `TwitchChannelId` bereits beim Join aus Helix (`ChannelService.cs:126`, `:136`).
   - **Es ist auch kein Dauerverbrauch von 1 Request/min je Zeile**, wie eine frühere Fassung
     schrieb: `SevenTvSyncService.cs:146` heilt die Zeile mit `channel.TwitchChannelId ??=
     twitchUserId` beim **ersten erfolgreichen** Sync. Eine solche Zeile kostet genau **einen**
     Request. Dauerverbraucher sind allein Zeilen, deren Auflösung **dauerhaft** scheitert.
   - **Der Resync läuft über v3, die Bestenliste über v4 — und beide teilen den Eimer.**
     `ResolveTwitchUserIdAsync` postet auf `"gql"` relativ zur BaseAddress `https://7tv.io/v3/`
     (`ServiceCollectionExtensions.cs:76`); nur eine Stelle im Client benutzt `V4GqlPath`
     (`SevenTvApiClient.cs:563`). Die Messung oben zeigt, dass die Pfadversion für den Zähler
     keine Rolle spielt.

   Die Fremdkanal-Suche im Frontend zieht den Eimer nicht (Helix + `userByConnection`, Auflage
   als Kommentar in `ForeignEmoteSetService.cs:72`).

   **Was die Messung an der Risikorichtung ändert — und das ist der wichtigste Satz dieses
   Abschnitts: unser Feature ist nicht die Gefahr.** Der harte Deckel der Bestenliste ist 10
   Requests pro Stunde, die Eimerkapazität 100 pro 60 s, also 6.000 pro Stunde. Die Bestenliste
   kann den Eimer nicht überziehen, auch nicht unter Last, auch nicht bei Fehlern. Eine dauerhaft
   nicht auflösbare Kanalzeile im Worker zieht dagegen 1 pro Minute — **60 pro Stunde, pro
   Zeile**, also das Sechsfache unseres gesamten Budgets. Der dominante Term ist der Bestand,
   nicht die Neuerung.

   **Offen bleibt Messung (b):** trifft eine Sperre wirklich nur `search`-Aufrufer, oder auch
   `userByConnection`, `emoteSet(id:)` und `GET /v3/users/twitch/{id}`? Davon hängt ab, wie groß
   der Schaden **einer bereits eingetretenen** Überziehung ist — nicht mehr, ob wir sie auslösen
   können.
4. **P4** — Die #147-Härtung deckt #148 nicht ab (s. Randbedingungen).
5. **P5** — **Prüfauftrag, keine Behauptung.** Bei #147 saßen drei von fünf
   Review-Befunden in Code, den die Spec als unverändert deklariert hatte. Die Prüfaufträge
   stehen unter Nächste Schritte 4 und sind gegenüber der ersten Fassung gewachsen, weil das
   Review zwei weitere Stellen gefunden hat.

## Ansätze

- **A — Vorrat, faul gefüllt.** Nachfüllen bei Zugriff, 1 h Haltbarkeit, Allowlist der
  Sortierungen und harte Seitendecke. Nur Api und Frontend. **Gewählt.**
- **B — Vorrat, eifrig gewärmt.** *Verworfen: die Api hat heute keinen Hosted Service
  (verifiziert: 0 Treffer für `AddHostedService`/`IHostedService`/`BackgroundService` in
  `src/EmotePurge.Api`), Hintergrundarbeit liegt bewusst im Worker, der im Fenster gesperrt
  ist. Neue Fläche für unter eine Sekunde Gewinn einmal pro Stunde.*
- **C — Bestenliste als Markierung statt als Seite.** *Verworfen: liefert nicht die Quelle,
  die entschieden wurde. Bleibt als eigenständige Idee brauchbar, s. Offene Frage 7.*

Zusätzlich stand zur Wahl, die Scores stattdessen an die **eigenen** Emotes zu hängen
(Löschhilfe: „tot bei dir und global" gegen „tot bei dir, Platz 4 global"). Verworfen, weil der
freigegebene #147-Entwurf die Quelle als Positionierung schon trägt.

## Empfohlener Ansatz: A — Vorrat, faul gefüllt

Kein **Serverpfad** zu 7TV hängt mehr an einem Nutzerklick: die Anfrage trifft einen Vorrat,
und nur wenn der abgelaufen ist, füllt genau ein koaleszierter Aufruf nach. Der Deckel gilt für
die **Server-IP** — das ist der Satz, der zählt. „Nutzeranfragen treffen nie 7TV" wäre falsch:
das Frontend ruft 7TV an anderen Stellen direkt aus dem Browser auf
(`already-present-filter.ts:7`, die Mass-Delete-Mutationen), und diese Aufrufe tragen die IP des
Nutzers, nicht unsere.

### Schlüsselraum — verbindlich

**Zwei Dinge tragen die Sicherheit, nicht eines** — das ist die Korrektur aus der
Schiedsentscheidung: der endliche Schlüsselraum hält den **Erwartungswert** bei 4 Upstream-
Requests pro Stunde, das harte Fensterbudget (s. „Vorrat") hält den **Deckel** bei 10 pro Stunde.
Eine frühere Fassung schrieb, die Sicherheit hänge „allein" am Schlüsselraum. Das war falsch:
ein endlicher Schlüsselraum deckelt nur **erfolgreich gecachte** Antworten, auf einem
fehlschlagenden Schlüssel läuft die Füllung sonst immer wieder an.

Der Schlüsselraum bleibt trotzdem verbindlich, jede Lücke darin verschlechtert den
Erwartungswert:

- **Allowlist der Sortierungen.** Feste Liste im Server. Ein Wert, der nicht darin steht, ist
  ein 400 — **kein** Durchreichen an 7TV.
- **Der Client sendet keine Seite.** Die Seitendecke ist serverseitig; es gibt keinen
  `page`-Parameter, den ein Client hochzählen könnte (s. Endpunktvertrag).
- **`perPage` wird serverseitig gesetzt**, nie vom Client. Gemessen kostet 250 dasselbe wie
  100, also ist 250 der Wert.
- **Kein `refresh`-Parameter und kein anderer Bypass.** Der #147-Endpunkt hat `?refresh=true`,
  das die Cache-Stufe übergeht (`SevenTvEndpoints.cs:34`, `HardenedForeignEmoteSetService.cs:76`).
  Dieses Muster wird **nicht** kopiert: mit ihm könnte jeder eingeloggte Client pro Klick einen
  Upstream-Request erzwingen, gedeckelt nur durch die nutzerpartitionierte Policy
  (`PermitLimit = 10`, `RateLimitingOptions.cs:56`) — zehn Nutzer reichen rechnerisch für
  100/min. Auch kein `Cache-Control`-Durchgriff. Ein „neu laden"-Knopf im Frontend liest nur
  den Vorrat.
- **Eine leere Seite wird gecacht wie ein Treffer.** Sonst ist dieser Schlüssel vom Deckel
  ausgenommen und jeder Klick darauf geht upstream. Das ist eine **Bedingung** des
  Sicherheitsarguments, keine Spec-Frage.
- **Kein Suchbegriff, keine Tags, keine Filter.** Das ist die Grenze zu Stufe 3, und sie ist
  jetzt begründet: nicht vorratsfähig.

### Vorrat — im Prozessspeicher, nicht in Redis

**Entschieden, gegen das #147-Muster.** `ForeignEmoteSetCache` ist ausdrücklich fail-open („a
Redis outage degrades this feature to always live", `ForeignEmoteSetCache.cs:17-20`). Bei #147
fängt das providerweite Budget diesen Fall ab — genau die Stufe, die hier entfällt. Ein
fail-open-Vorrat plus kein Budget heißt: bei einem Redis-Ausfall geht jeder Klick upstream, der
Koaleszierer fängt nur Gleichzeitiges, der Breaker öffnet nur auf Fehler, nicht auf Erfolg.
Der Deckel wäre dann weg.

Deshalb: **ein Vorrat im Prozessspeicher.** Keine externe Abhängigkeit, also keine
fail-open-Lücke. Der Grund, der Redis rechtfertigen würde, existiert heute nicht: es läuft eine
Api-Replica, und `docker-compose.prod.yml:73` schließt eine zweite ohnehin aus einem anderen
Grund aus (Data-Protection-Keys, „logins would fail sporadically").

Ein **neuer** Redis-Vorrat würde fail-open nicht erben — man schreibt ihn fail-closed, 503 statt
live. Er kauft aber wenig: Neustart-Überleben kostet ≤ 4 Requests und wird von keinem Nutzer
ausgelöst, und er ersetzt nichts — Koaleszierer, Breaker und Fensterbudget blieben in-process.
**Bedingung, erweitert:** kommt je eine zweite Replica hinzu, braucht es Redis fail-closed
**und** ein verteiltes Fensterbudget, sonst ist der Deckel `Replicas × 10`.

**Neustart:** Vorrat, Breaker und Budget sind kalt. Kosten ≤ 4 Requests, betrieblich, akzeptiert.

**Die Komposition muss ausgeschrieben werden, weil ein nackter `Lazy<Task<…>>` in vier Fallen
läuft — und drei davon stehen im Repo zwanzig Zeilen daneben.**
`ForeignEmoteSetRequestCoalescer` ist genau dieses Muster und entfernt den Eintrag im `finally`,
mit der Begründung: *„a permanently cached 'in flight' entry would freeze every later lookup for
this channel on today's answer."* Verbindlich deshalb:

- **Eintrag ist `(Lazy<Task<Result>>, DateTimeOffset ExpiresAt)`**, nicht nur der `Lazy`. Ein
  `Lazy` kennt keine Haltbarkeit.
- **Ersetzen per `TryUpdate` gegen den gelesenen Eintrag.** `GetOrAdd` ersetzt nichts, und ein
  naives `AddOrUpdate` lässt bei Ablauf zwei Aufrufer zwei Upstream-Requests starten — der
  Deckel verdoppelt sich still. Genau dieser Fehler ist der wahrscheinlichste, s. Wächterbudget.
- **Fehler werden negativ gecacht, nicht entfernt.** Eine frühere Fassung verlangte „sofort
  entfernen", mit der Begründung, `Lazy<Task<T>>` zementiere einen `Faulted`-Task. Die
  Schiedsentscheidung hat das widerlegt: `ExecuteGuardedAsync` **gibt** `Failed(...)` zurück
  (`HardenedForeignEmoteSetService.cs:110-113`, `:133`), ein 429 ist also ein erfolgreich
  abgeschlossener Task mit Fehlerstatus, kein geworfener. Und `ExpiresAt` schließt die dauerhafte
  Vergiftung schon aus. „Sofort entfernen" hätte stattdessen ein Loch geöffnet: auf einem
  fehlschlagenden Schlüssel läuft die Factory bei jedem Klick erneut an, und der Breaker lässt
  fünf gewöhnliche Fehler zu (`ForeignSevenTvBreakerPolicy.cs:94`) und danach je Öffnungsfenster
  einen Probeaufruf (`:135-141`). Haltbarkeit nach Ausgang:

  | Ausgang | Haltbarkeit |
  |---|---|
  | Treffer, auch leere Seite | 1 h |
  | `SevenTvRateLimited` | `RetryAfter` von 7TV, geklemmt auf [60 s, 1 h] |
  | `SevenTvUnavailable` (5xx, Timeout, Parsefehler) | 60 s |
  | Budget verweigert | 30 s |

  Ein `Faulted`- oder `Canceled`-Task entsteht nur noch über den Backstop
  (`HardenedForeignEmoteSetService.cs:146-152`); er gilt als **sofort abgelaufen** und wird
  ersetzt — nicht entfernt.
- **`ExpiresAt` berechnet die Factory beim Abschluss**, nicht der Leser beim Anlegen. Ein
  laufender Eintrag läuft damit nie ab.
- **Die Füllung läuft unter `CancellationToken.None`**, nie unter dem Token des ersten
  Aufrufers. Sonst killt ein wegnavigierender Browser den Vorrat für alle — der Bug, den das
  Coalescer-Remark als behoben beschreibt.

Ein Vorbild für „Haltbarkeit im Prozess" gibt es im Repo **nicht**: `EmoteMatchCache` hält ohne
Haltbarkeit und ohne Koaleszierung, `ChannelSyncGate` ist ein Semaphor-Dictionary,
`IMemoryCache` ist nirgends referenziert. Der bestehende Coalescer ist dagegen die halbe
Lösung — er ist **nicht** „vermutlich nicht gebraucht", wie eine frühere Fassung schrieb.
Speicher ist unkritisch: 2 Sortierungen × 2 Seiten × 250 Zeilen.

- **Haltbarkeit 1 Stunde**, nicht die 60 s aus #147. Eine Rangliste ist keine
  Kanalzustandsabfrage; 7TVs eigener REST-Cache ist ohnehin 10–30 min alt
  (SevenTV/SevenTV#81), und `TRENDING_DAILY` bewegt sich über Stunden.
- **Eigener Breaker, nicht der aus #147**, und mit **anderer Rolle**. Ein geteilter Breaker
  würde bei einer Such-Eimer-Sperre (~1 h) auch die Fremdkanal-Vorschau schließen, die den Eimer
  nie berührt — andere Fehlerdomäne. `ForeignSevenTvBreakerPolicy` ist pur und per Typ als
  Singleton registriert (`ServiceCollectionExtensions.cs:101`); eine zweite Instanz braucht
  Keyed-Registration. Seine Aufgabe ist hier **nicht** der Wiederanlauf je Schlüssel — das macht
  `ExpiresAt` — sondern die **schlüsselübergreifende Ausbreitung**: ein 429 auf einem Schlüssel
  liefert für die anderen `Failed(RateLimited)` ohne Upstream, mit `RemainingOpenTime` als
  Haltbarkeit.
- **Ein hartes providerweites Fensterbudget vor jedem `emotes.search`: 10 Requests je 60 min,
  Wartezeit 0.** Das ist der eigentliche Deckel und die wichtigste Änderung gegenüber einer
  früheren Fassung, die hier nur einen Alarm hatte. Verweigerung ergibt `Unavailable` mit 30 s
  Haltbarkeit, **keinen** Upstream-Aufruf und **keine** Breaker-Meldung (wie
  `ReleaseProbeWithoutOutcome`, `HardenedForeignEmoteSetService.cs:128`). Damit ist der
  ungünstigste Fall **10/h unabhängig vom Nutzerverhalten** — dieser Satz trägt die Spec, nicht
  „der Schlüsselraum genügt".
  `ForeignEmoteSetProviderBudget` hat die richtige Form (Zeitstempel-Queue, `TimeProvider`),
  aber `const`-Werte 60/min und 5 s Wartezeit: Fenster, Maximum und Wartezeit werden
  Konstruktorparameter, oder es entsteht eine kleine Schwester.
- **Der Alarm bei 6/h bleibt** und feuert **vor** dem harten Deckel. 6 = Erwartung 4 plus ein
  Neustart-Zuschlag; der Doppel-Fill bei Ablauf ergäbe 8/h und wird damit sichtbar.

### Endpunktvertrag

```
GET /api/seventv/leaderboard?sortBy=<allowlist>
```

**Kein `page`-Parameter** — das ist die Auflösung eines Codex-Befundes, nicht seine Milderung
(s. Frontend). Der Server setzt die Liste aus den beiden Upstream-Seiten zusammen und liefert bis
zu 500 Zeilen in einer Antwort. Bei 250 Zeilen waren es 14.778 Bytes, 500 sind also rund 30 KB;
das Raster stellt bei #147 bereits 956 Zeilen virtuell gescrollt dar.

Eigene `MapGroup`, nur `RequireAuthorization()`, **ohne** `ChannelNameValidationFilter` (es
gibt keinen Kanalnamen). Eigene Rate-Limit-Policy, weil `ForeignEmoteLookup` einen anderen Pfad
bemisst — und die braucht **drei** Stellen, nicht eine: `RateLimitPolicyNames`, die Registrierung
in `Program.cs` und `RateLimitingOptions.Validate()` (`:64-72`). Letzteres ist der Fail-Fast beim
Start; eine nicht validierte Policy kann mit Kapazität 0 hochkommen, was der Kommentar dort
ausdrücklich „total outage" nennt. Eigene `RateLimitCallSources`-Quelle, damit der Pfad seinen eigenen Verbrauch
zeigt — das hat die #147-Spec (§6) zum Vertrag gemacht: genau eine Beobachtung je
Upstream-Request.

- `sortBy` nur aus der Allowlist; darüber 400 mit einem **neuen** `ApiErrorCodes`-Wert. Nach
  Regel 7 braucht der denselben Eintrag in `web/src/app/core/i18n/api-error.ts` **und** in
  beiden Locale-Dateien. Eine Budget-Verweigerung braucht dagegen **keinen** neuen Code — sie ist
  `SevenTvUnavailable`.
- Antwort trägt `totalCount`, die Emote-Zeilen, und ein `truncated`-Analogon für „Decke
  erreicht, 7TV hat mehr" — wie `ForeignEmoteSet.Truncated` bei #147. **Der Text dazu darf nicht
  für beide Sortierungen derselbe sein:** 500 von 705 (`TRENDING_DAILY`) sind 71 % der Liste,
  500 von 1.371.890 (`TOP_ALL_TIME`) sind 0,036 %. Entweder nach Sortierung unterscheiden oder
  die Zahl mitliefern.
- Der Schlüsselraum des **Vorrats** bleibt `(sortBy, Upstream-Seite)` — zwei Sortierungen × zwei
  Seiten = vier Einträge. Nach außen ist davon nichts sichtbar.

### Herkunft und Audit — die Stelle, die #147 vorhergesagt hat

Eine Bestenlisten-Zeile hat **keinen Quellkanal**, und der Server lehnt das heute ab.
`EmoteEndpoints.cs:142` kennt genau `"channel" | "file" | "seventv-channel"`, und `:164`
erzwingt, dass jede Nicht-Datei-Herkunft einen `SourceChannelName` trägt. Der Kommentar dort
benennt genau diesen Fall im Voraus:

> `a fourth kind without a source name would have to change this line, not just add to the`
> `vocabulary above.`

Zu tun ist deshalb mehr als eine Vokabel:

1. Vierte Herkunfts-Vokabel (die Vokabelliste kennt drei, auch wenn die UI zwei Optionen
   zeigt), Vorschlag `"seventv-leaderboard"`.
2. **Die Zeile `:164` ändern**, nicht nur die Liste `:142` erweitern: leerer
   `SourceChannelName` muss für diese Herkunft erlaubt sein.
3. **Ein neues `AuditLogDetail.Kinds`-Mitglied** (`IAuditLogQueryService.cs:20-30` hat bisher
   fünf). Die Vokabel nur in die Konstantenliste aufzunehmen reicht **nicht**:
   `ProjectDetail` (`AuditLogQueryService.cs:121`) gibt `ImportedFromChannel` nur zurück, wenn
   `source is not null` (`:182-185`); sonst fällt die Zeile in den nackten
   `EmoteCount`-Zweig (`:189-192`) — also genau der Herkunftsverlust, den dieser Schritt
   verhindern soll, nur eine Ebene tiefer.
4. **Der `ProjectDetail`-Zweig für eine Herkunft ohne Namen**, plus seine Darstellung im
   Audit-Log-Frontend und beide Locales.
5. Frontend-Vokabular: `ImportOrigin`-Union (`web/src/app/core/seven-tv/import-source.ts:29-38`)
   und der erschöpfende `switch` in `importOriginSourceChannelName`,
   `emote-admin.service.ts:37`, beide Locales.
6. **Der Bestätigungsdialog nennt die Herkunft sonst gar nicht:** `originChannelName`
   (`import-confirm-dialog.ts:290`) liefert `null`.
7. Zu entscheiden: trägt die Audit-Zeile stattdessen die **Sortierung** als Herkunftsmerkmal
   („Trend heute")? Das wäre ehrlicher als ein leeres Feld — und der Dialog hätte wieder etwas
   zu zeigen.

Der Fehler schlägt sonst **nach** der 7TV-Mutation zu: Emotes kopiert, Herkunft weg. Genau das
war AK 12 bei #147.

### Frontend

Dritte Option im bestehenden Dialog. Keine eigene Seite; die Erzählung bleibt „eine Quelle
unter anderen". Der Sortierwechsel läuft aus dem Vorrat, also ohne Upstream.

**Die UI paginiert nicht — und genau das löst einen `high`-Befund auf, statt ihn zu mildern.**
Codex hat gezeigt, dass eine seitenweise Auswahl mit dem Bestandsraster falsch wird:
`ForeignEmoteGrid` hält Schlüssel über Änderungen von `emotes` hinweg (`:314-317`), emittiert aber
`selection.selectedItems()` (`:346`), und `ListSelection.selectedItems` löst ausschließlich gegen
die **aktuell sichtbare** Eingabeliste auf (`list-selection.ts:39-42`). Nach einem Seitenwechsel
bliebe eine unsichtbare Auswahl im Zähler stehen und fiele beim Import aus dem Ergebnis — dieselbe
Fehlerklasse wie #132/#133, und in diesem Projekt schon zweimal zugeschlagen.

Statt einen Zustandsvertrag über Seiten hinweg zu erfinden, gibt es **keine Seiten:** eine Liste
mit bis zu 500 Zeilen, virtuell gescrollt, wie das Raster es bei #147 mit 956 Zeilen schon tut. Ein
Sortierwechsel tauscht die Liste vollständig; die Auswahl wird dabei **sichtbar geleert**, weil die
beiden Listen verschiedene Grundmengen sind. Das ist ein Zustandsübergang und braucht nach Regel 12
seinen Spec. Die von Codex verlangten Testfälle („Seite 1 wählen → Seite 2 wählen → importieren")
entfallen mit den Seiten; der Sortierwechsel mit bestehender Auswahl bleibt ein Pflichtfall.

**Das Raster braucht einen Eingriff, nicht nur Wiederverwendung.** `foreign-emote-grid.ts` hat
ein eigenes clientseitiges Sortier-`<select>` mit Default `'none'` („set's own order", P5'/AK16
als Vertrag im Code, `:41-67`, `:269`). Auf der Bestenliste ist die Sortierung die
**Server**-Dimension. Zwei Sortier-Controls — eines wählt die Liste, das andere sortiert sie um —
sind ein Bedienfehler.

Entschieden: **ein additiver, optionaler Input `forcedSortMode`** — nicht `hideSortControl`. Der
Unterschied ist nicht kosmetisch: `sortMode` steuert vier Dinge, nicht eines. Die Score-Kachel
(`:208-215`), den Erklärsatz (`:171-175`), `cellLabel`/`spokenScore` (`:367-395` — der Score
steckt im `aria-label`) und `sortedEmotes`, worüber auch `ListSelection` konstruiert wird
(`:314-317`). Ein Input, der nur das `<select>` versteckt, ließe `sortMode` auf `'none'` stehen:
keine Zahl auf der Kachel, kein Erklärsatz — also genau der Produktkern weg.

Drei weitere Stellen hängen mit:

- **Drei i18n-Schlüssel sind auf einer Bestenliste sachlich falsch**, weil der Namensraum
  kanalgebunden ist: `import.foreignChannel.empty` („das aktive 7TV-Set **dieses Kanals**"),
  `.truncated` („nur ein Teil **des Sets**") und `.sort.scoreHint` („…wie oft es in **diesem
  Kanal** benutzt wird"). Entweder eigene Input-Schlüssel oder Varianten, in beiden Locales.
- **`sortSelectId` ist hart `'foreign-emote-sort'`** (`:272`) — bei zwei Instanzen im selben
  Dokument eine doppelte DOM-id.
- Die #147-Fälle bleiben grün, weil `foreign-emote-grid.spec.ts:113`/`:98` den neuen Input nicht
  setzen. Das ist geprüft, nicht gehofft — aber es ist auch nur eine Regressionsaussage, keine
  neue Deckung (s. Erfolgskriterien, Regel 12).

**Folge für die Allowlist:** `ForeignEmoteRow` kennt nur `topAllTime` und `trending`
(= `trendingDay`). Ein `TRENDING_WEEKLY` hätte kein Zahlfeld auf der Kachel. Die Allowlist ist
deshalb **`TRENDING_DAILY` und `TOP_ALL_TIME`** — zwei Sortierungen, nicht drei. Bei einer
Seitendecke von 2 ergibt das **4 Upstream-Requests pro Stunde** im Höchstfall.

## Offene Fragen

1. **Score-Beschriftung und Sortier-Semantik auf einer Bestenliste.** Zwei Verträge aus
   `DECISIONS.md` (2026-09-10) sind betroffen: P5' verlangt, dass die Zahl nie
   Standardsortierung ist, und Punkt 5, dass die Sortierung eine Eigenschaft des Emotes
   benennt, nicht die Herkunft der Liste. Auf einer Rangliste ist die Rangfolge der Inhalt und
   die Sortierauswahl benennt die Liste. Vorschlag: beide Verbote verengen sich auf die
   Beschriftung (nie bloß „Beliebtheit", nie als kanal-lokal lesbar), die Sortierung nach Score
   ist hier zulässig. **Vertragsänderung — braucht einen `DECISIONS.md`-Eintrag im selben
   Commit (Regel 3).** Beim Anfassen gleich mitkorrigieren: `DECISIONS.md:507-508` schreibt die
   Optionslabels „7TV-Verbreitung (gesamt)/(Trend)" fest, ausgeliefert sind „7TV-Score
   (gesamt)/(Trend)" — der Code-Kommentar (`foreign-emote-grid.ts:54-58`) begründet, warum
   „Verbreitung" falsch war. Der Vertrag, der geändert werden soll, ist an dieser Stelle schon
   veraltet.
2. **Seitendecke.** Vorschlag 2 Seiten à 250 = 500 Emotes je Sortierung.
3. **Trägt die Audit-Zeile die Sortierung als Herkunftsmerkmal?** S. „Herkunft und Audit", 5.
4. **Namenskollisionen beim Import.** Ein global gezogenes Emote trägt einen Namen, der im
   Zielset belegt sein kann. #149 (Umlaut-Aliase) ist seit 2026-09-10 geschlossen (PR #157) und
   **kein** offener Punkt mehr; `web/src/app/shared/seven-tv/already-present-filter.ts`
   existiert seit dann und deckt den Fall vermutlich ab — zu prüfen, nicht anzunehmen.
5. **Liefert `emotes.search` ohne `filters` auch Zero-Width- oder ungelistete Emotes**, und
   behandelt der Import-Pfad die schon?
6. *(gestrichen — der Deploy-Status von #147 ist nachschlagbar, keine offene Frage. Vor der
   Spec feststellen und unter „Ausliefern" eintragen.)*
7. **Bleibt C als eigene Idee?** Die Markierung „steht netzwerkweit in den Top 500" an den
   eigenen Emotes ist billig und beantwortet eine andere Frage. Falls gewollt: eigenes Issue.

## Erfolgskriterien

Alle beobachtbar formuliert — zwei Kriterien einer früheren Fassung waren mit dem heutigen
Code nicht messbar:

- **Die Admin-Rate-Limit-Ansicht zeigt für die neue Call-Source ≤ 96 Beobachtungen im
  24-h-Fenster und ≤ 4 im Minutenfenster unmittelbar nach einem Ablauf.** So formuliert, weil
  `RateLimitTelemetryStore` nur ein Minuten- und ein 24-h-Fenster führt (`:16-17`,
  `FineBucketsPerWindow = 12`, `CoarseBucketsPerWindow = 1440`) — „pro Stunde" ist dort keine
  ablesbare Zahl. `ProviderResponseObservation.RateLimitRemaining` existiert bereits
  (`IRateLimitTelemetry.cs:70`), die Erfassung ist also machbar.
- **`x-ratelimit-search-remaining` wird erfasst** (neue Arbeit, s. Sonde 1) und fällt in der
  Live-Abnahme nie unter 90. **Als Minimum, nicht als letzter Wert:** `RateLimitTelemetryStore`
  überschreibt den Header-Sample je Antwort und liefert nur `LastHeaderSample` — ein gefährlicher
  Tiefstand würde von der nächsten Antwort verdeckt. Ohne ein gespeichertes Minimum über mehrere
  vollständige Worker-Zyklen ist dieses Kriterium nicht belegbar.
- **Für den Fall eines geteilten Eimers: eine Obergrenze für dauerhaft nicht auflösbare
  Kanalzeilen**, nicht nur ihre Anzahl. Ohne eine Schwelle ist der Rollout freigebbar, ohne dass
  Luft für Worker **und** Bestenliste zusammen nachgewiesen wäre.
- Ein `sortBy` oder `page` außerhalb der Allowlist ergibt 400 und **keinen** Upstream-Aufruf.
- Ein Import aus der Bestenliste schreibt eine Audit-Zeile, die ihre Herkunft nennt — geprüft
  **nach** echter Mutation, wie AK 12/13 bei #147.
- **Regel 11:** die neue `MapGroup` bekommt ihre Fälle in `tests/EmotePurge.Api.Tests` — 401,
  400 für Allowlist-Verstöße, und die Filter-Reihenfolge (Rate-Limit vor Validierung, wie AK 14
  bei #147).
- **Regel 12:** der neue Input und der neue Dialogschritt sind **Verhalten** — Sperrentscheidung
  samt Grund, `computed()`-Ergebnisse, `aria`-Semantik — und brauchen co-located Specs. „Die
  #147-Fälle bleiben grün" ist eine Regressionsaussage, keine Deckung. Bei neuen Dateien liegt
  `coverage-local.mjs` nah an Sonar: eine ungetestete neue Komponente reißt das Gate sichtbar.
- **Wie viele Kanalzeilen auf Prod heute ohne `TwitchChannelId` sind** (Admin-Kanalliste,
  `LastSyncFailureReason`) — jede zieht 1 Request/min und ist damit Grundlast auf demselben
  Eimer.
- Gates grün: `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`,
  `npm --prefix web run e2e` (nur ohne Api auf `:5151`), `node scripts/coverage-local.mjs` vor
  dem PR.
- Live-Verifikation gegen echte 7TV-Zugänge nach Regel 16.

## Ausliefern

Bestehende Lieferkette: GHCR-Images plus Portainer-Stack auf `emotepurge.app`. Api und
Frontend, keine Migration. Deploy mit anderen offenen Arbeitspaketen bündeln (Epic #118) —
schwaches Argument, s. Randbedingungen.

**Rollout im Messfenster.** Die konservative Vorgabe war „bauen und mergen jetzt, Stack-Update
erst ab 2026-10-08". Ansatz A nimmt ihr die Grundlage, weil der Verbrauch bauartbedingt
gedeckelt ist statt durch Disziplin. **Diese Folgerung steht unter zwei Bedingungen**, und
beide müssen erfüllt sein, bevor der Stack aktualisiert wird:

1. Die Messung aus Nächste Schritte 1 ist gefahren, **und fehlende oder mehrdeutige Header
   gelten als „geteilt"**, nicht als Entwarnung.
2. Die Live-Abnahme bestätigt Erwartungswert (≤ 4/h) **und** Deckel (≤ 10/h), und das
   Minimum von `x-ratelimit-search-remaining` bleibt über 90.
3. **Der Eimer ist geteilt — gemessen, s. P3.** Codex' Empfehlung dazu war ein gemeinsames,
   fail-closed wirkendes Request-Budget für Api **und** Worker. **Das ist zu entscheiden und die
   erste Frage der Spec**, denn die Messung hat das Argument dafür geschwächt: ein Budget für
   *unseren* Pfad ist schon da (10/h gegen 6.000/h Kapazität), und ein *gemeinsames* Budget würde
   den Worker drosseln — also den Pfad, der das Produkt trägt, wegen eines Features, das
   0,17 % des Eimers benutzt. Empfehlung: **kein gemeinsames Budget**, stattdessen die Zahl der
   dauerhaft nicht auflösbaren Kanalzeilen aus den Erfolgskriterien als Betriebskennzahl führen.
   **Aber:** ein gemeinsames Budget bräuchte Änderungen am Resync-Pfad, also an Code, den der
   Worker ausführt — das ist im Messfenster gesperrt. Fällt die Entscheidung doch dafür, gilt
   ohne Ausnahme: bauen und mergen jetzt, Stack-Update ab 2026-10-08.

Ist eine der drei Bedingungen nicht erfüllt, gilt: bauen und mergen jetzt, Stack-Update erst ab
2026-10-08.

## Nächste Schritte

1. **Zwei Messungen zuerst, vor der Spec** — sie entscheiden P3 und beide
   Rollout-Bedingungen.
   - **(a) erledigt am 2026-09-13** — Protokoll v4 → v3 → v4, Ergebnis in P3: der Eimer **ist**
     geteilt. Messung (b) ist damit nicht erspart.
   - **(b) fällig, aber teuer — und nach der Risikoumkehr in P3 weniger dringend, weil sie nur
     den Schadensradius einer Überziehung misst, die wir selbst nicht auslösen können.**
     Den Eimer absichtlich überziehen und während der Sperre
     `v3/gql users(query:)`, `userByConnection` (`SevenTvApiClient.cs:124`), `emoteSet(id:)`
     (`:524`) und `GET /v3/users/twitch/{id}` (`:220`) prüfen.
     **Kosten größer als eine frühere Fassung schrieb:** Devbox und Arbeitsrechner hängen am
     selben Heimanschluss, und das Frontend ruft 7TV direkt aus dem Browser auf
     (`already-present-filter.ts:7`, Mass-Delete). Eine Sperre trifft damit auch den eigenen
     Import-/Purge-Betrieb und normales 7tv.app-Surfen aus dem Netz — nicht nur „den lokalen
     Resync". In ein Zeitfenster ohne eigene 7TV-Nutzung legen; wenn eine fremde IP verfügbar
     ist, die nehmen. Ob Sonden während der Sperre sie verlängern, ist ungemessen.
2. **Spec** nach `docs/superpowers/specs/`, mit Akzeptanzkriterien für Allowlist, Seitendecke,
   Bypass-Freiheit, das Herkunfts-Vokabular und den Nachweis über die Call-Source.
3. **Adversariale Zweitmeinung auf Spec und Plan** (`/codex:adversarial-review --model
   gpt-5.6-sol`), bevor gebaut wird.
4. **Prüfaufträge zuerst abarbeiten, nicht am Ende** — alle vier sind Wiederverwendungs-
   Behauptungen, und genau dort saßen bei #147 die Befunde:
   - **`import-source-dialog.ts`, `foreign-channel-step.ts`, `foreign-import-flow.ts` — hier
     sitzt die eigentliche Kopplung, und eine frühere Fassung hat sie übersehen.** Der Dialog ist
     hart auf `ForeignChannelStep` typisiert: `viewChild(ForeignChannelStep)` (`:192`),
     `gridVisible = channelStep()?.showsGrid()` (`:219`, treibt Pane-Breite **und** ob es
     überhaupt ein „Weiter" gibt), `channelResult` (`:214`), `continueWithChannel` (`:246-252`),
     `titleKey` (`:203-212`, braucht einen neuen Titel-String), `ImportSourceDialogResult`
     (`:37-38`), plus die Verzweigung in `import-trigger.ts:120-126`. Dazu verlangt
     `ForeignChannelImportResult` (`foreign-channel-step.ts:25-33`) `channelName`,
     `sevenTvUserId` und `emoteSetId` — eine Bestenlisten-Auswahl hat **keines** der drei, also
     ist `buildForeignImportSource` (`foreign-import-flow.ts:38`) nicht wiederverwendbar.
     Einzuplanen: eigener Ergebnistyp und ein eigenes `buildLeaderboardImportSource`. Der
     Code-Kommentar „ein Eintrag plus ein `@case`" beschreibt den Dialog-Eintrag, nicht das
     Arbeitspaket.
   - `foreign-emote-grid.ts`: trägt es Zeilen ohne Kanalbezug, und bleibt `forcedSortMode`
     ohne Regression an den #147-Fällen?
   - `ForeignEmoteSetCache` / `ForeignEmoteSetRequestCoalescer`: heute auf
     `ForeignEmoteSetLookupResult` typisiert. Mit dem Prozessspeicher-Vorrat werden beide
     vermutlich **nicht** gebraucht — das ist zu bestätigen, nicht zu hoffen.
   - **`SevenTvApiClient`:** die ganze 429-Semantik (literales 429, `extensions.status == 429`
     hinter HTTP 200, Reset-Hinweis, Telemetrie-Suppression, genau eine Beobachtung) steckt
     privat in `FetchPreviewPageAsync` (`:410-466`). Eine neue Methode für `emotes.search` muss
     diese ~50 Zeilen duplizieren **oder** in einen gemeinsamen v4-Page-Fetch herausziehen —
     Letzteres berührt den ausgelieferten #147-Pfad in einer 766-Zeilen-Datei. Zu entscheiden
     mit dem bestehenden Testnetz (`SevenTvApiClient*Tests`) **vor** dem Feature.
   - `already-present-filter.ts`: deckt es die Namenskollision ab?
5. **Plan** aus der Spec, Tasks je Subagent.
6. **Bauen**, Live-Abnahme, `/codex:review --model gpt-5.6-sol` über das fertige Arbeitspaket,
   dann PR.

## Was mir an deinem Denken aufgefallen ist

- Bei der Rollout-Frage hast du keine meiner beiden Optionen genommen, sondern **„Eimer ganz
  vermeiden"** geschrieben — also die Fragestellung abgelehnt statt eine Antwort gewählt. Beide
  Empfehlungen, die ich dir gegeben hatte, waren schlechter als das. Aus deinem Einwurf ist
  Ansatz A entstanden, und mit ihm ist die Sperre weggefallen, um die es in der Frage ging.
- **„heißt das, wenn jemand im front end nach kanälen sucht. kann das den resync im backend für
  alle blockieren?"** — du hast nach dem Wirkungsradius gefragt, bevor gebaut wurde, nicht
  danach. Genau diese Frage hat P3 auf seine echte Größe zusammengezogen, und das Review hat
  danach gezeigt, dass selbst diese Antwort noch eine unbewiesene Annahme enthielt.
- Bei der ersten Frage hast du gegen meine Empfehlung **„#148 wie geschrieben"** gewählt. Ich
  habe danach im freigegebenen #147-Entwurf nachgesehen, und dort stand der
  Positionierungssatz, der dir recht gibt und nicht mir. Du hast an einer Entscheidung
  festgehalten, die vier Tage alt war, statt sie neu aufzurollen, weil jemand eine schlaue
  Umdeutung anbietet.
