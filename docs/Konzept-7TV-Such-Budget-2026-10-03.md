# Konzept: Gemeinsames Anfragebudget für 7TVs Such-Eimer (2026-10-03)

Auftrag: Issue #165 — ein prozessübergreifendes, fail-closed Budget für 7TVs GraphQL-Such-Eimer,
das Api und Worker gemeinsam belasten, plus ein Backoff für Kanäle, deren Twitch-ID nie auflöst.
Kurzentwurf, danach direkt Umsetzung. Kein Deploy vor dem **2026-10-08** (Messfenster #118).

## 1. Ausgangslage (belegt)

- **Der Eimer:** `x-ratelimit-search-limit: 100` pro ~60 s, Überziehung sperrt ~1 h
  (`x-ratelimit-search-reset: 3583`), gemeinsam für v3 und v4. Ein Überlauf kommt als HTTP 200 mit
  `errors[].extensions.status: 429`. `emotes.search` kostet 1, `users(query:)` und
  `Emote.channels.totalCount` ziehen ebenfalls daraus.
- **Verbraucher im Server-Code** (grep über `src/`, `web/` fragt keines der drei Felder ab):

| Verbraucher | Prozess | Aufruf | Wann |
|---|---|---|---|
| Kanal-Identität | Worker | `SevenTvApiClient.ResolveTwitchUserIdAsync` (`v3/gql users(query:)`), aufgerufen nur aus `SevenTvSyncService.ResolveTwitchUserIdAsync` | jeder `SyncChannelAsync` eines Kanals **ohne** gespeicherte `TwitchChannelId` — periodischer Resync (60 s), JOIN, RESYNC, EventAPI-Nachsync, Boot-Recovery |
| Bestenliste (#148, gemergt) | Api | `SevenTvApiClient.SearchEmotesAsync` (`v4/gql emotes.search`), nur aus `SevenTvLeaderboardService.FetchPageAsync` | bei Lagerauffüllung; In-Process-Deckel 10/rollende Stunde (`SevenTvLeaderboardRequestBudget`) |

- `Emote.channels.totalCount` fragt der Server nirgends ab.
- Ein Kanal ohne ID kostet heute **eine Suche pro Minute ohne Obergrenze**: bei
  `NoSevenTvAccount`/`Unavailable`, bei einem Rename-Duplikat (Auflösung gelingt, Zuordnung wird
  verweigert, kein Fehlergrund) und bei einem gesperrten Kanal (Auflösung gelingt, Einspruch greift).
- `ResolveTwitchUserIdAsync` erkennt weder echtes HTTP 429 (wirft in `EnsureSuccessStatusCode`)
  noch das 429-in-200; beides wird `Unavailable`. Die Such-Header liest nur der Bestenlisten-Pfad.

## 2. Entscheidung

### 2.1 Ein gemeinsames Budget in Redis

`ISevenTvSearchBudget` (Core/Services), Implementierung `RedisSevenTvSearchBudget`
(Infrastructure/Redis), Singleton, in `AddEmotePurgeInfrastructure` registriert — beide Hosts
bekommen dieselbe Implementierung gegen dasselbe Redis.

- **Rollendes Fenster als Zeitstempel-Log** (sorted set, Score = ms), geprüft und belastet in
  **einem** Lua-Skript: abgelaufene Einträge kürzen, Sperre prüfen, Gesamt- und Verbraucherzahl
  prüfen, dann beide Sets ergänzen. Atomar, also auch bei gleichzeitigen Api- und Worker-Aufrufen
  nie über der Decke. Rollend aus demselben Grund wie die beiden In-Process-Budgets: ein festes
  Fenster ließe an der Grenze das Doppelte durch. Die Uhr kommt als Argument vom Aufrufer
  (`TimeProvider`), weil Api und Worker auf demselben Host laufen und Tests sie so kontrollieren.
- **Decke 50 pro 60 s gesamt** — die Hälfte von 7TVs 100. Der Abstand deckt Uhren- und
  Fensterversatz zu 7TV und Verbrauch ab, den wir nicht sehen.
- **Anteile:** die Kanal-Identität darf höchstens **40** davon nutzen; die **10** darüber sind
  Reserve für die Bestenliste, die selbst bis zur Gesamtdecke darf (ihr eigener 10/h-Deckel bleibt
  unverändert davor). Ein Sturm ID-loser Kanäle kann die Bestenliste damit nie aussperren.
- **Belastet wird vor jeder Suchanfrage, vom Verbraucher.** Der Verbraucher weiß, wer er ist und
  was eine Ablehnung für ihn bedeutet; der Client meldet, was zurückkam (2.2). Reihenfolge in der
  Bestenliste: Breaker → **gemeinsames Budget** → 10/h-Deckel → Anfrage. Das gemeinsame Budget
  zuerst, weil ein dort verschenkter Platz nach 60 s verfällt, ein verschenkter Deckel-Platz erst
  nach einer Stunde.
- **Erschöpft heißt: nicht fragen.** Kein Warten, keine Warteschlange. Bestenliste: `BudgetRefused`
  (30 s Haltbarkeit, wie bisher bei ihrem eigenen Deckel), bei aktiver Sperre `SevenTvRateLimited`
  mit der Restsperre als Haltbarkeit; der Breaker erfährt in beiden Fällen nichts. Kanal-Identität:
  Sync endet ohne Ergebnis, **kein** Fehlergrund wird geschrieben, kein Backoff-Schritt — es wurde
  ja nichts gefragt; der nächste Tick versucht es erneut.

### 2.2 7TVs eigene Antwort ehren

Der Client meldet nach jeder Suchanfrage beider Methoden eine Beobachtung an das Budget
(`ObserveResponseAsync`: `remaining`, `reset`, „rate-limited", Retry-After). Daraus folgt eine
**Sperre** (pure Entscheidung in `SevenTvSearchBlockPolicy`), die das Lua-Skript vor jeder Belastung
prüft:

- **429 in beiden Formen** (HTTP 429 oder 429-in-200): Sperre bis `reset` bzw. Retry-After/GraphQL-
  Hinweis, ohne Hinweis **3600 s**; geklemmt auf [60 s, 6 h] (6 h wie `MaxResetHintSeconds`).
- **`remaining` ≤ 10 ohne 429:** Sperre bis `reset` (geklemmt auf [1 s, 6 h]). Heißt: jemand, den
  wir nicht zählen, leert den Eimer — wir hören auf, bevor die Stunde fällt.
- Eine Sperre wird nur **verlängert**, nie verkürzt (eine kurze Beobachtung darf eine laufende
  Stunde nicht aufheben). Ein Key mit absolutem Ablaufzeitpunkt plus TTL; kein Aufräumjob.

`ResolveTwitchUserIdAsync` liest dafür die `x-ratelimit-search-*`-Header und `errors[]` der v3-
Antwort; das Ergebnis bleibt nach außen `Unavailable` (`SevenTvLookupStatus` bekommt **kein**
neues Mitglied — es hängen UI-Fehlergründe daran).

### 2.3 Backoff für Kanäle, deren ID nie auflöst

`TwitchIdResolutionBackoff` (Infrastructure, pur bis auf eine Lock-geschützte Tabelle, Singleton im
Worker-Prozess), Schlüssel `Channel.Id`. Jede Auflösung, die eine Suche **gekostet** und **keine**
ID gespeichert hat, zählt als Fehlschlag: `NoSevenTvAccount`, `Unavailable`, Rename-Duplikat,
gesperrter Kanal. Nächster Versuch frühestens nach `min(60 s × 2^(n−1), 1 h)`; Erfolg löscht den
Eintrag. Ein hängender Kanal kostet damit nach rund zwei Stunden **24 statt 1440 Suchen am Tag**.

- In-Process statt Redis: nur der Worker ruft den Pfad, und ein Neustart kostet höchstens eine
  Suche pro hängendem Kanal. Persistenz würde eine Migration kosten, die Ein-Prozess-Lage nicht.
- Gilt für alle Auslöser, auch den manuellen RESYNC (ein Klick kostet sonst eine Suche).
- Information-Logzeile je Fehlschlag ab dem dritten in Folge — durch den Backoff selbst gedrosselt.

### 2.4 Telemetrie

- **Kleinster beobachteter `remaining` je UTC-Stunde**, über alle Verbraucher, als Redis-Key
  `seventv:search-budget:min-remaining:{yyyyMMddHH}` (TTL 25 h, atomares Minimum per Lua).
- **Warning** beim Setzen/Verlängern einer Sperre (Dauer, Anlass), **Warning** bei Redis-Ausfall.
- Belastungen selbst loggen nicht zusätzlich: die Bestenliste hat schon ihre eine Zeile je Anfrage.

### 2.5 Redis weg

Belasten ist **fail-closed**: Ausnahme → Ablehnung (`StoreUnavailable`), keine Suche. Alles andere
läuft weiter — Kanäle **mit** ID syncen ohne Suche, Chat-Zählung und Bestenlisten-Lager sind nicht
betroffen. Beobachten ist fail-open (Ausnahme schlucken, loggen): eine verlorene Beobachtung kann
nichts durchlassen, weil die Belastung ohne Redis ohnehin ablehnt.

## 3. Konfiguration

Abschnitt `SevenTv:SearchBudget`, Defaults im Options-Typ und gleichlautend in beiden
`appsettings.json`, beim Start validiert (Fail-fast):
`MaxRequestsPerWindow` 50 · `WindowSeconds` 60 · `ChannelIdentityMaxRequestsPerWindow` 40 ·
`LowWatermark` 10 · `DefaultLockoutSeconds` 3600 · `ResolutionBackoffBaseSeconds` 60 ·
`ResolutionBackoffMaxSeconds` 3600. Api und Worker **müssen** dieselben Werte haben — jeder Prozess
prüft gegen die Decke, die er selbst kennt.

## 4. Ausdrücklich nicht Teil davon

- Anzeige in der Admin-Monitoring-Seite (`/api/admin/rate-limits`) — Folge-Issue.
- Ein eigener Fehlergrund für Rename-Duplikate (UI-Vertrag, i18n) — Folge-Issue.
- Der `ProviderRequestTelemetryHandler` liest für v3 weiter Twitchs `Ratelimit-*`-Schreibweise.
- Die anderen 7TV-Eimer (Vorschau, `emote_set_change` im Browser) und der 10/h-Deckel der
  Bestenliste bleiben unverändert.
- Keine Änderung an Chat-Zählung oder Matching (Messfenster-Freeze).

## 5. Offene Punkte für den Betreiber — entschieden

Alle fünf Defaults sind **vom Betreiber am 2026-10-03 bestätigt** (nach Review und Live-Prüfung):

1. **Gesamtdecke 50/min** (Hälfte von 7TVs 100). Höher heißt mehr gleichzeitige Erst-Auflösungen,
   aber weniger Abstand zur Stunde Sperre. Bestätigt: 50.
2. **Reserve der Bestenliste 10/min** (Identität max. 40). Bestätigt.
3. **Backoff-Deckel 1 h.** 6 h spart weitere 20 Suchen/Tag je hängendem Kanal, lässt aber einen
   frisch angelegten 7TV-Account bis zu 6 h ungesynct. Bestätigt: 1 h.
4. **Low-Watermark 10.** Sperrt bis zum Reset, sobald fremder Verbrauch sichtbar wird; 0 schaltet
   das ab und verlässt sich allein auf 429. Bestätigt: 10.
5. **Manueller RESYNC unterliegt dem Backoff.** Alternative wäre ein Bypass je Klick (eine Suche).
   Bestätigt: kein Bypass.

## 6. Nachträge nach dem Review (2026-10-03)

- **Zwei Sperren statt einer**, je Ursache ein Key. Die Bestenliste antwortet nur auf eine
  429-Sperre mit `SevenTvRateLimited` (Haltbarkeit ≥ 60 s); eine Low-Watermark-Sperre ist unsere
  Vorsicht und antwortet `BudgetRefused`. Eine längere Watermark-Sperre überschreibt nie eine
  laufende 429-Sperre.
- **Beobachten schreibt die Sperre zuerst**, die Telemetrie danach und getrennt — ein Fehler beim
  Minimum kostet nie die Sperre.
- **Jeder Redis-Aufruf hat 1 s Zeitlimit**, das Belasten zusätzlich das Token des Aufrufers.
  Langsam heißt ablehnen (fail-closed), nie Sekunden Latenz in der Bestenliste.
- **Validierung:** `ChannelIdentityMaxRequestsPerWindow < MaxRequestsPerWindow` (Reserve bleibt) und
  `LowWatermark < 100 − MaxRequestsPerWindow` (unser eigener erlaubter Verkehr löst sie nie aus).
