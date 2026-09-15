# Rate-Limits: Request-Verstärker entfernen und Providerverbrauch beobachten

**Datum:** 2026-08-30

**Status:** nach Review überarbeitet; Grundlage für einen noch zu schreibenden Implementierungsplan

**Issues:** [#33](https://github.com/emotepurge/EmotePurge/issues/33), [#35](https://github.com/emotepurge/EmotePurge/issues/35)

**Betrifft:** lokale HTTP-Policies, Angular-Reload-Pfade, den Cache für moderierte Twitch-Channels und das Admin-Monitoring

## Urteil und Ziel

Die lokale Policy `ExternalApi` misst eingehende Requests, nicht die Kosten bei Twitch oder 7TV. Dadurch kostet ein reiner DB-Read ein Permit, während `/channels/mine` trotz bis zu zehn Helix-Seiten nur eines kostet. Diese Grenze ist für Providerkontingente ungeeignet (`Program.cs:95-140`, `ChannelEndpoints.cs:103-121`, `TwitchHelixClient.cs:52-99`).

Issue #33 wird in dieser Runde mit vier einzeln deploybaren Maßnahmen behoben:

1. unnötige und aggressive Client-Requests entfernen;
2. `ExternalApi` durch großzügige lokale Policies für Navigation und Voting ersetzen;
3. die vollständige Moderated-Channels-Liste serverseitig gemeinsam cachen;
4. lokale Ablehnungen, Cachewirkung und echte Provider-429er read-only beobachtbar machen.

Die App befindet sich in der Testphase und hat gewöhnlich höchstens zwei gleichzeitige aktive Nutzer. Ein lokaler Fehlalarm ist derzeit schädlicher als zusätzliche eigene Last. Die Startwerte sind deshalb bewusst großzügige Missbrauchsgrenzen und keine nachgebildeten Providerbudgets.

Nicht Teil dieser Runde sind ein verteilter Provider-Budgetkoordinator, Observe-/Enforce-Modi, eine erfundene 7TV-Grenze und die dreiwertige Rollenauflösung. Für keinen untersuchten Fall ist ein Provider-429 belegt; alle identifizierten 429er stammen aus der lokalen ASP.NET-Policy. Die ausgegliederte Rollenauflösung verändert außerdem eigenständige Produktverträge und braucht eine eigene Spec.

## Beleglage

### Am Code belegt

- `ExternalApi` ist ein Fixed-Window-Limiter mit 40 Permits pro Minute, Partition Twitch-User-ID mit IP-Fallback und `QueueLimit = 0` (`Program.cs:95-120`, `RateLimitRejection.cs:49-66`).
- Usage-Stats und Emote-Reads erben `ExternalApi` auf Gruppenebene, obwohl ihre Handler aus PostgreSQL lesen; mögliche Fremdaufrufe liegen in den vorgeschalteten Rollenfiltern (`UsageStatsEndpoints.cs:15-20`, `EmoteEndpoints.cs:19-24`).
- Moderator-, 7TV-Grant- und Subscriber-Entscheidungen haben einen konfigurierbaren Zehn-Minuten-Cache (`ModRoleCache.cs:10-51`, `84-95`). Das entwarnt `/channels/mine` nicht: dessen Moderated-Channels-Paginierung ist ungecacht (`MyChannelsService.cs:35-56`).
- Selbst bei einem 7TV-Grant-Cachetreffer kann `/mine` für noch nicht getrackte Grants einen App-Token und `GetUsers` verwenden (`MyChannelsService.cs:109-129`, `156-183`). Benutzte Twitch-User-Tokens werden spätestens stündlich live validiert (`TwitchUserTokenService.cs:20-27`).
- `MyChannelsService` und `ModeratorCheckService` laden dieselbe vollständige Moderated-Channels-Liste unabhängig voneinander (`MyChannelsService.cs:35-55`, `ModeratorCheckService.cs:24-43`). Der Moderator-Cache speichert danach nur ein Bool pro Nutzer und Channel.
- Es gibt drei policy-freie SSE-Endpunkte: Channel, Overview und Admin (`LiveEndpoints.cs:26-77`, `AdminEndpoints.cs:241-246`). `MaxConnectionLifetime` erzwingt für offene Seiten alle zehn Minuten einen neuen Auth-Handshake, aber kein Rate-Limit-Permit (`LiveEndpoints.cs:18-24`).
- `dfabd81` spart Permits durch das Debouncing von `duplicate-names` bei `channel.synced`-Bursts (`channel-workspace-layout.ts:248-263`). Das Teilen der SSE-Verbindung selbst spart kein Permit.
- `GET /api/channels/{channelName}` hat heute keine Policy, obwohl sein `ChannelManagementAuthorizationFilter` bei einem Cache-Miss Helix erreichen kann (`ChannelEndpoints.cs:19-29`).
- `WorkerHealthService` fragt während der App-Lebensdauer alle 30 Sekunden `/api/worker/health` ab (`worker-health.service.ts:9-25`). Dieser policy-freie Dauerstrom gehört in der Anzeige nicht zu den limitierten Requests (`WorkerHealthEndpoints.cs:11-63`).

### Betriebsbeobachtung, nicht allein am Repository beweisbar

- Die untersuchten 429er stehen im nginx-Origin-Log und werden seit `dfabd81` von `RateLimitRejection` als lokale Ablehnungen geloggt. Die Zuordnung der historischen Antworten zu ASP.NET statt Cloudflare ist eine Produktionsbeobachtung aus den Issue-Kommentaren, kein reiner Codebeweis.
- Der neueste Kommentar in #33 nennt schnelles Voting. Die dort genannten Routen passen zu den unten belegten Reload-Pfaden.
- Für Twitch oder 7TV ist in der vorliegenden Beleglage kein echter Provider-429 dokumentiert.

### Extern verifiziert

Twitch begrenzt Helix mit Token-Buckets. App- und User-Access-Requests liegen in getrennten Buckets; User-Access-Requests werden pro Client-ID und Nutzer begrenzt. Antworten liefern `Ratelimit-Limit`, `Ratelimit-Remaining` und `Ratelimit-Reset`. Der oft genannte Wert 800 ist ein Beispiel, kein fest zu codierender Vertrag. Quelle: [Twitch API Concepts — Rate Limits](https://dev.twitch.tv/docs/api/guide/#twitch-rate-limits).

Damit ist die bisherige Begründung eines für alle Moderatoren gemeinsamen User-Token-Budgets falsch. Für 7TV ist kein belastbares öffentliches REST-/GraphQL-Kontingent belegt. Der Kommentar, ein neuer App-Token widerrufe automatisch den vorherigen (`TwitchAppTokenProvider.cs:6-10`), wird von der offiziellen Twitch-Dokumentation nicht bestätigt; diese Frage ist für den hier vertagten Multi-Prozess-Koordinator separat zu klären.

## Korrigierte Request-Zählung

Die Tabelle ist aus den aktuellen Aufrufpfaden abgeleitet. Während dieser Dokumentationsrunde wurden gemäß Arbeitsauftrag keine Builds, Tests oder Live-Messungen ausgeführt. Vor der Implementierung wird die Baseline zusätzlich einmal im Browser-Network-Tab aufgezeichnet; die Spec behauptet diese Messung nicht vorweg.

| Ablauf | Heutige `ExternalApi`-Permits | Weitere API-Requests | Herleitung |
|---|---:|---:|---|
| Usage-Workspace, frischer Permissions-Cache | 6 | 0 | `permissions`, `duplicate-names`, 2× `active-set`, `totals`, `series` |
| Zurück zur Overview | +1 | 0 | ungecachetes `/channels/mine` |
| Frischer Vote-Session-Einstieg | 4 | 1 | `permissions`, `duplicate-names`, Guard-`results`, Page-`results`; zusätzlich policy-freier Channel-Status |
| `n` schnelle Votes in einem 500-ms-Burst | `2n + 1` | `n` | `n` Mutationen, `n` direkte Result-Reloads, ein SSE-Result-Reload; zusätzlich `n` Channel-Status-Reads |
| `n` langsame Votes | bis `3n` | `n` | jedes SSE-Echo kann einen eigenen Result-Reload auslösen |
| Erstnutzung nach Join ohne Set-ID | 6 bis 21/22 | 0 | Baseline plus bis zu 15 `active-set`-Polls; bei erfolgreichem Poll ein weiterer `totals`-Read |
| Sichtbarer Sync-Fehlergrund | +2 oder +4 pro Minute | 0 | alle 30 s `active-set`, bei vorhandener Set-ID zusätzlich `totals` |

### Warum es genau zwei `active-set`-Abrufe sind

Der Load-Effect liest `rangeResolved` (`usage-stats-page.ts:599-602`). Nach der ersten Statusantwort bleibt dieses `computed` im Zwischenzustand wertgleich `false`; Angular überspringt den Effect, wenn kein Producer seinen Wert geändert hat. Erst die Korrektur von `from` auf den Tracking-Start löst den zweiten Lauf aus (`usage-stats-page.ts:612-621`, `1040-1079`). Der vorhandene E2E-Test fixiert `totals` und `series` entsprechend auf je einen Request (`usage-range-resolution.e2e.spec.ts:80-99`).

Damit kostet der normale Workspace-Einstieg sechs statt der früher behaupteten sieben Permits, der Rundgang mit Rückkehr sieben statt acht. Sechs Rundgänge ergeben 42 Requests und überschreiten das 40er-Fenster weiterhin. Ein `/permissions`-Cachetreffer reduziert den jeweiligen Einstieg um eins.

### Login mit vielen Channels

OAuth-Callback und `/auth/me` tragen keine `ExternalApi`-Policy (`AuthEndpoints.cs:47-139`). Erst die Overview ruft `/channels/mine` auf und verbraucht lokal ein Permit. Viele Channels füllen das lokale 40er-Budget daher nicht beim Login.

Providerseitig kann dieses eine `/mine` bis zu `ceil(channelCount / 100)`, höchstens zehn Helix-Seiten laden. Hinzu kommen bei Cache-Misses 7TV-Grant-Aufrufe und gegebenenfalls App-Token-`GetUsers` für ungetrackte Grants. Der Login ist nicht die Ursache des lokalen 429, `/mine` ist aber der teuerste einzelne Providerpfad (`MyChannelsService.cs:35-62`, `109-129`).

### Die vier bisher fehlenden Verstärker

1. `awaitSync` pollt alle zwei Sekunden bis zu 15-mal `active-set` (`usage-stats-page.ts:1096-1128`, Konstanten `:122-123`).
2. Ein sichtbarer Sync-Fehlergrund lädt alle 30 Sekunden `active-set` und bei gesetzter Set-ID zusätzlich `totals` (`usage-stats-page.ts:675-712`, Konstante `:135`).
3. Jeder Vote-Erfolg ruft über `load()` neben Ergebnissen auch den policy-freien Channel-Status ab (`vote-session-detail-page.ts:602-630`, `706-737`).
4. Der Vote-Guard lädt `/results`; die Seite lädt unmittelbar danach dieselbe Resource erneut (`vote-session-access.guard.ts:26-36`, `vote-session-detail-page.ts:322-325`, `711-728`).

## Design 1: Client-Verstärker zuerst entfernen

### Usage-Workspace

Der Set-Status wird aus dem range-abhängigen `load()` gelöst. Ein Channel-Wechsel lädt ihn einmal; eine Range-Änderung lädt nur `totals` und `series`. Manueller Refresh und `channel.synced` dürfen Status und Daten weiterhin aktualisieren. Das spart **ein Request pro normalem Workspace-Einstieg**: sechs werden zu fünf.

`awaitSync` verwendet den vorhandenen `channel.synced`-Stream als primären Abschlussimpuls. Als Schutz gegen verlorene Events bleiben innerhalb der bisherigen 30 Sekunden höchstens drei zeitlich auseinanderliegende Fallback-Probes statt 15. Ein erfolgreicher Probe lädt anschließend einmal die Totals. Das spart im Worst Case **zwölf Requests pro Erstnutzung**, ohne den Wait unbegrenzt zu machen.

Der Fehlergrund-Recheck wird auf höchstens einmal pro Minute gedrosselt; `channel.synced` aktualisiert weiterhin sofort. Da eine erfolgreiche periodische Synchronisation den Fehlergrund auch ohne Inventory-Änderung löschen kann (`SevenTvSyncService.cs:108-121`), darf der Fallback nicht vollständig entfallen. Bei vorhandener Set-ID sinkt der Dauerstrom von vier auf zwei Requests pro Minute.

### Voting

Der erfolgreiche `/results`-Abruf des Route-Guards wird als einmalige Navigation-Antwort an die Page übergeben. Die Page verbraucht diesen Wert und lädt ihn beim Mount nicht erneut. Das ist kein dauerhafter Result-Cache und wird bei Fehler, Routewechsel oder nach Verbrauch verworfen. Der frische Vote-Session-Einstieg spart **einen Request**.

Lokale Vote-Erfolge und SSE-Echos speisen dieselbe 500-ms-Reload-Pipeline. Der lokale Erfolg triggert die Pipeline selbst, damit die Aktualisierung nicht vom Redis-Publish abhängt. Ein Vote lädt nur Ergebnisse; der Channel-Status wird ausschließlich initial und bei `channel.synced` aktualisiert. Für `n` schnelle Votes entstehen danach `n` Mutationen und höchstens ein Result-Reload: vier schnelle Votes sinken von 13 API-Requests auf fünf und sparen **acht Requests**.

### `duplicate-names` bleibt separat

`duplicate-names` meldet aktive Emotes mit gleichem Namen, deren Chat-Usage nicht eindeutig zugeordnet werden kann. Es ist ein DB-basierter Diagnose-Read, den nur das Workspace-Layout benötigt. Ein Zusammenlegen mit `active-set` würde Poll- und Statusantworten für andere Aufrufer vergrößern. Nach der Policy-Korrektur spart das Zusammenlegen kein Provider-Permit; es bleibt deshalb außerhalb dieses Slices.

## Design 2: Lokale Policies statt Provider-Surrogat

`ExternalApi` wird vollständig von den Endpoints entfernt. Die neue Grenze schützt nur die eigene API vor Schleifen und Missbrauch; Providerkosten werden beobachtet und durch Caches reduziert, aber in dieser Runde nicht lokal nachgebildet.

### `InteractiveRead`

- Token-Bucket pro Twitch-User-ID; IP-Fallback nur für theoretisch anonyme Nutzung.
- Kapazität 300, Nachfüllung fünf Tokens pro Sekunde, automatische Nachfüllung, `QueueLimit = 0`.
- Gilt für `/channels/mine`, Channel-Status, Permissions, Usage-Stats, Emote-Reads einschließlich `active-set` und `duplicate-names`, Vote-Listen und Vote-Ergebnisse.
- `GET /api/channels/{channelName}` erhält damit erstmals eine Policy.

### `Voting`

- Token-Bucket für `POST .../votes` und `DELETE .../votes/{emoteId}`.
- Partition `TwitchUserId + SessionId`, damit eine Session Navigation und andere Abstimmungen nicht blockiert.
- Kapazität 120, Nachfüllung zwei Tokens pro Sekunde, automatische Nachfüllung, `QueueLimit = 0`.
- Beide Mutationsarten sind ausdrücklich erfasst; Retract darf nicht versehentlich unter `InteractiveRead` fallen.

### Bestehende Policies

- `Bookkeeping` bleibt für `sync-deleted` und `sync-restored` und übernimmt `join`; bereits auf 7TV ausgeführte Aktionen dürfen nicht wegen einer engeren Policy lokal verloren gehen.
- `ChannelResync` und sein zusätzlicher per-Channel-Cooldown bleiben unverändert.
- `PublicHealth` bleibt für `/api/health`.
- Die drei SSE-Endpunkte bleiben policy-frei; ihre Verbindungsgrenze liegt in `ILiveEventStream`.

### Vollständiges Inventar vor dem Umhängen

`ExternalApi` zu entfernen, ohne vorher jede Route zu kennen, lässt Endpunkte unbeabsichtigt ungeschützt. Der Ist-Stand aus `grep -rn RequireRateLimiting src/EmotePurge.Api/`:

| Policy | Routen |
|---|---|
| `ExternalApi` | `/{channelName}/permissions`, `/mine`, `/{channelName}/join` (`ChannelEndpoints.cs:101/121/139`); Vote-Liste, `/results`, `POST .../votes`, `DELETE .../votes/{emoteId}` (`VoteSessionEndpoints.cs:158/183/225/256`); die gesamte Emote-Gruppe und die gesamte Usage-Stats-Gruppe auf Gruppenebene (`EmoteEndpoints.cs:24`, `UsageStatsEndpoints.cs:20`) |
| `Bookkeeping` | `sync-deleted`, `sync-restored` (`EmoteEndpoints.cs:58/94`) und **zusätzlich `/{channelName}/audit-log` (`ChannelEndpoints.cs:64`)** |
| `ChannelResync` | `/{channelName}/resync` (`ChannelEndpoints.cs:195`) |
| `PublicHealth` | `/api/health` (`WorkerHealthEndpoints.cs:63`) |

Die Zuordnung von `join` zu `Bookkeeping` ist damit ein **Wechsel**, kein Ist-Zustand: `join` liegt heute auf `ExternalApi`.

Außerdem sind heute policy-frei und brauchen beim Umhängen eine bewusste Entscheidung statt eines stillen Weiter-so:

- `GET /{channelName}` (`ChannelEndpoints.cs:19`) — geht wie beschrieben auf `InteractiveRead`.
- `DELETE /{channelName}` und `DELETE /{channelName}/purge` (`ChannelEndpoints.cs:197/222`).
- `POST` Vote-Session anlegen, `POST /{sessionId}/end`, `DELETE /{sessionId}` (`VoteSessionEndpoints.cs:21/57/88`).
- `GET /api/vote-sessions/mine` (`VoteSessionEndpoints.cs:260`) — ein Read wie die übrigen Vote-Listen und deshalb `InteractiveRead`.
- Die gesamte `/api/admin`-Gruppe und `/api/auth` sowie `GET /api/worker/health`.

Für jede dieser Routen hält die Implementierung fest, ob sie eine Policy bekommt oder bewusst ohne bleibt. Der Schritt gilt erst als fertig, wenn `grep -rn RequireRateLimiting src/EmotePurge.Api/` kein `ExternalApi` mehr findet und jede Zeile der Tabelle oben einen Nachfolger hat.

Die Optionsvalidierung ist konkret: Kapazität, Tokens je Periode und Replenishment-Periode müssen größer null sein; `QueueLimit` bleibt fest null. Werte kommen aus stark typisierten Options und Environment-Variablen. Änderungen werden ausschließlich per Deployment und Neustart wirksam; es gibt keinen Write-Endpoint.

## Design 3: Gemeinsamer Moderated-Channels-Cache

Ein Infrastructure-Service wird zur einzigen Quelle der vollständigen moderierten Twitch-Channels eines Nutzers. `MyChannelsService` und `ModeratorCheckService` verwenden denselben Dienst.

- Redis-Key pro Twitch-Nutzer, TTL zunächst zehn Minuten.
- Inhalt: normalisierter Login und immutable Twitch-Channel-ID, soweit Helix sie liefert.
- Gleichzeitige Misses werden per In-Process-Single-Flight zusammengeführt; nach Eintritt ins Gate wird Redis erneut geprüft.
- Nur eine vollständige erfolgreiche Pagination wird gecacht. Timeout, 429, 5xx, Tokenfehler und unvollständige Pagination schreiben keinen Eintrag.
- Der Admin-Invalidate-Pfad löscht den Listencache zusammen mit 7TV-Grant- und Subscriber-Caches.
- Der alte Bool-Moderatorcache wird in diesem Schritt vollständig abgelöst; es gibt keinen read-only Übergang ohne Schreiber.

Bei 350 moderierten Channels kostet der erste Miss vier Helix-Seiten; weitere `/mine`- und Moderatorprüfungen innerhalb des TTL kosten dafür null Helix-Aufrufe. Die separate App-Token-Auflösung ungetrackter 7TV-Grants bleibt bestehen und wird lediglich beobachtet.

Ein pauschaler Browsercache für `/mine` wird nicht eingeführt: Overview-Live-Events sollen weiterhin frische Channel-, Bot- und Live-Zustände anzeigen. Der Servercache entfernt die Helix-Kosten, ohne die vollständige Overview-Antwort einzufrieren.

## Design 4: Read-only Beobachtbarkeit

Die bestehende Admin-Seite erhält einen Abschnitt „Rate Limits“ und den separaten global-admin-only Endpoint `GET /api/admin/rate-limits`. Er bleibt von `/api/admin/health` getrennt und hat keinen schreibenden Gegenpart.

### Angezeigte Daten

- Effektive Konfiguration je lokaler Policy: Kapazität, Nachfüllung, Partition und Queue.
- Akzeptierte und lokal abgelehnte Requests je Policy für letzte Minute und letzte 24 Stunden.
- Letzte lokale Ablehnung: Zeitpunkt, HTTP-Methode, Route-Template, Policy, Partition und `Retry-After`.
- Cache-Hits/-Misses für Moderated-Channels-Liste, 7TV-Grants und Subscriber-Check.
- Echte serverseitig beobachtete Provider-429er je Provider und Call-Source, `Retry-After` und letzter Zeitpunkt.
- Zuletzt beobachtete Twitch-`Ratelimit-*`-Header als Stichprobe, ausdrücklich nicht als reservierbarer oder autoritativer gemeinsamer Zustand.
- 7TV-Request-Rate und echte 429er ohne Prozentwert, weil kein belastbarer Nenner bekannt ist.
- Kennzeichnung, dass Mass-Delete und Restore browserseitig direkt `https://7tv.io/v3/gql` aufrufen (`seven-tv-run-engine.ts:23`, `Program.cs:206-207`) und deshalb in serverseitigen 7TV-Zahlen fehlen.
- Hinweis, dass policy-freie SSE-Reconnects und Worker-Health-Polls nicht in den Policy-Zählern erscheinen.

### Erfassung und Ausfallverhalten

Kleine Redis-Zeit-Buckets mit TTL speichern Zähler und den letzten Vorfall. Dimensionen sind stabile Policy-, Route- und Call-Source-Namen, keine rohen URLs. `RateLimitRejection` verwendet seine vorhandenen `HttpContext.Items` für Policy und Partition; ein um `UseRateLimiter` liegender Telemetriepfad unterscheidet akzeptierte Requests von der explizit markierten lokalen Ablehnung. Fachliche 429er wie der Resync-Cooldown werden nicht als Policy-Verstoß gezählt.

Provider-Clients erfassen Requests, Antwortheader und echte 429er an der ausgehenden Grenze. Es gibt keine Reservierung, keine Vorab-Ablehnung und keinen Observe-/Enforce-Schalter. Telemetrieschreibfehler werden strukturiert geloggt und beeinflussen den Produktpfad nicht. Ist Redis beim Admin-Read nicht verfügbar, liefert der Endpoint eine partielle `200`-Antwort mit `telemetryAvailable: false` und weiterhin der effektiven lokalen Konfiguration.

Die Admin-Seite aktualisiert beim Öffnen, manuell und alle 30 Sekunden. Konfiguration bleibt deployment-basiert.

## Schichten und Projektregeln

- Reine Monitoring-DTOs, Enums und Service-Interfaces liegen in `EmotePurge.Core` und verwenden nur BCL-Typen. Kein `HttpContext`, `HttpResponseMessage`, Redis- oder ASP.NET-Typ gelangt in Core.
- Redis-Zähler, Cache und Provider-HTTP-Instrumentierung liegen in `EmotePurge.Infrastructure` und werden ausschließlich über `AddEmotePurgeInfrastructure(configuration)` registriert.
- Minimal-API-Handler injizieren Core-Service-Interfaces; sie greifen weder auf `AppDbContext` noch auf `IConnectionMultiplexer` zu. Der neue Endpoint liegt in `Endpoints/*.cs`, nicht in `Program.cs`. Er wird in die bestehende `/api/admin`-`MapGroup` registriert und erbt damit deren `GlobalAdminAuthorizationFilter` (`AdminEndpoints.cs:24-26`); die Autorisierung läuft über einen `IEndpointFilter`, nicht über eine ASP.NET-Core-Policy.
- ASP.NET-spezifische Policy-Options und die Accepted/Rejected-Erfassung bleiben in `EmotePurge.Api/RateLimiting/`.
- Nicht-triviale Services und externe Abhängigkeiten erhalten Interfaces; C#- und Angular-Memberreihenfolge folgt `CLAUDE.md` und `web/.claude/CLAUDE.md`.
- Neue Bezeichner und Kommentare sind englisch, Logs deutsch, Projektdokumentation deutsch.

Diese Runde führt keinen neuen API-Fehlercode ein: lokale Ablehnungen behalten `ApiErrorCodes.RateLimitExceeded` samt bestehender Web-/de-/en-Zuordnung. Das Monitoring degradiert partiell mit `200`, und Providerfehler werden noch nicht als neuer HTTP-Vertrag exponiert.

Die separate Tri-State-Spec muss vor ihrer Umsetzung zwei konkrete Codes durch die vollständige Kette führen: `provider_rate_limited` und `authorization_provider_unavailable` in `ApiErrorCodes.cs`, `web/src/app/core/i18n/api-error.ts` sowie `web/public/i18n/de.json` und `web/public/i18n/en.json`. Die beiden Locale-Dateien sind namentlich genannt, weil `api-error-locales.spec.ts` nur den Schritt von `api-error.ts` in die Locales erzwingt: der Schritt von `ApiErrorCodes.cs` nach `api-error.ts` bleibt Disziplin, und genau dort ist die Liste bereits zweimal auseinandergelaufen. Sie muss außerdem jede Aufrufstelle als Gate oder Anreicherung klassifizieren und die Guard-Behandlung von `503` festlegen.

## Rollout und Rückrollbarkeit

1. **Client-Verstärker:** `active-set`, `awaitSync`, Fehler-Recheck, Vote-Handoff und gemeinsame Vote-Reload-Pipeline ändern. Rein frontendseitig deploy- und durch Rückkehr zum vorherigen Web-Bundle rückrollbar.
2. **Policies:** `InteractiveRead` und `Voting` in einem Backend-Deploy einführen, alle Zielrouten umhängen und `ExternalApi` entfernen. Kein Zwischenstand mit zwei konkurrierenden Policies auf denselben Routen. Rückrollbar durch Wiederherstellung der alten Policy-Zuordnung.
3. **Moderated-Channels-Cache:** neuen Listencache samt Single-Flight einführen und Bool-Cache vollständig ablösen. Rückrollbar, weil Redis nur abgeleitete TTL-Daten hält; die Live-Paginierung bleibt der Miss-Pfad.
4. **Beobachtbarkeit:** Zähler, Providerheader, Admin-Endpoint und UI ergänzen. Telemetrie ist fail-open und kann ohne Änderung des Produktpfads rückgerollt werden.

Der Fix für #33 steht damit am Anfang; er wartet nicht auf eine Observe-Testphase. Jeder Schritt ist separat deploybar und separat rückrollbar.

## Abnahmekriterien und Harness

| Kriterium | Zuständiger Harness |
|---|---|
| Sechs vollständige Rundgänge mit Rückkehr in einer Minute erzeugen keine lokale 429. | `WebApplicationFactory` in `tests/EmotePurge.Api.Tests`, nach Muster `RateLimitRejectionTests` |
| 100 Vote-Mutationen in einer Session erzeugen keine lokale 429; eine andere Session bleibt unbeeinflusst. | `WebApplicationFactory`, echte Policy-Metadaten und Test-Authentifizierung |
| All-Time-Auflösung lädt `active-set` nur einmal; `totals` und `series` je einmal. | Playwright-Requestzählung in `usage-range-resolution.e2e.spec.ts` |
| Ein fehlender Set-Status erzeugt in 30 Sekunden höchstens drei Fallback-Probes. | Playwright mit gemocktem `active-set` und Live-Stub |
| Sichtbarer Fehlergrund erzeugt höchstens einen Status-Recheck pro Minute. | Playwright in `usage-atlas.e2e.spec.ts` |
| Vier schnelle Votes erzeugen vier Mutationen, höchstens einen Result-Reload und keinen Status-Read pro Vote. | Playwright in `vote-ballot.e2e.spec.ts` |
| Guard und Vote-Page teilen beim Einstieg genau einen Result-Read. | Guard-Vitest plus Playwright-Requestzählung |
| Cache-Hit verursacht keinen Helix-Aufruf; parallele Misses verursachen eine vollständige Pagination; Teilfehler wird nicht gecacht. | `tests/EmotePurge.Infrastructure.Tests` mit Testcontainers/Fakes an der HTTP-Grenze |
| Admin-Snapshot zeigt effektive Config, lokale Ablehnung, Cachezähler und Provider-429; Redis-Ausfall ergibt partielles `200`. | Infrastructure-Test für Redis-Zeit-Buckets plus `EmotePurge.Api.Tests` für Endpointvertrag |
| Geänderte Environment-Konfiguration erscheint nach Neustart im Snapshot. | `WebApplicationFactory` mit überschriebenen Settings |
| Browserseitige 7TV-Calls sind als Monitoring-Lücke sichtbar. | Admin-UI-Vitest und Playwright-Sichtprüfung |
| Helix-Paginierung, Cacheinvalidierung und Headererfassung funktionieren mit echten Zugängen. | Live-Verifikation vor Commit nach `CLAUDE.md` Regel 16 |

Die Browser-Network-Baseline für Workspace, Rundgang, Vote-Einstieg, schnelle Votes und Erstnutzung nach Join wird vor der ersten Codeänderung dokumentiert; sie ist **Task 0 des Plans** und nicht Teil von Rollout-Schritt 1, damit sie nicht hinter der ersten Codeänderung herläuft und ihren Vergleichswert verliert. Playwright kann Requestzahlen prüfen, aber keine echte ASP.NET-429, weil `/api/**` dort gemockt wird.

## Betroffene Bestandstests

- `tests/EmotePurge.Api.Tests/RateLimitRejectionTests.cs`: Policy-Namen, Token-Bucket-Semantik und Rejection-Metadaten.
- `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs` und `ApiFactory.cs`: unveränderte bool-Verträge müssen trotz neuer Policy-Zuordnung weiter 401/403 liefern; keine Tri-State-Zeilen in diesem Slice.
- `web/e2e/usage-range-resolution.e2e.spec.ts`: zusätzlich `active-set` zählen und die bestehende Einmal-Semantik für `totals`/`series` erhalten.
- `web/e2e/usage-atlas.e2e.spec.ts`: Polling-Fälle auf neue Intervalle und Obergrenzen anpassen.
- `web/e2e/vote-ballot.e2e.spec.ts`: Result-, Mutation- und Status-Requests zählen.
- `web/src/app/core/voting/vote-session-access.guard.spec.ts` und `vote-session.service.spec.ts`: einmalige Übergabe und Invalidierung prüfen.
- `web/src/app/core/admin/admin.service.spec.ts`: neuer read-only Snapshot.

Neue Infrastructure-Services erhalten Tests im passenden `Unit/`- oder `Integration/`-Ordner. UI-Flows erhalten Playwright-Tests; neue Core-Services oder Guards erhalten co-located Vitest-Specs. Vor Fertigmeldung gelten die vollständigen Test- und Live-Verifikationsregeln aus `CLAUDE.md`; sie sind nicht Teil dieser reinen Dokumentationsrunde.

## Bewertete Alternativen

### `ExternalApi` nur erhöhen

Würde den unmittelbaren Fehler zwar verschieben, behielte aber die falsche Messeinheit. Die großzügigen neuen Policies übernehmen den gewünschten Testphasen-Spielraum, ohne DB-Reads als Providerkosten auszugeben.

### Eigene Navigation-Policy mit anderem Partitionsschlüssel

Ist als lokale Missbrauchsgrenze sinnvoll und wird als `InteractiveRead` umgesetzt. Als Provider-Schutz wäre sie falsch, weil ein `/mine` weiterhin zehn Helix-Calls und ein DB-Read weiterhin null kosten kann.

### Token-Bucket statt Fixed Window

Ist für legitime Bursts besser und wird für `InteractiveRead` und `Voting` eingesetzt. Allein hätte er weder die Client-Verstärker noch die falsche Providerkosten-Messung behoben.

### Mehr clientseitiges Caching

Der vorhandene Permissions-Cache verhindert dreifache identische Reads. Ein breiter `/mine`-Cache würde Live-Updates ausbremsen; ein dauerhafter Vote-Result-Cache wäre bei SSE-Updates fehleranfällig. Verwendet wird nur ein einmaliger Guard-zu-Page-Handoff.

### Endpunkte zusammenfassen

`duplicate-names` und `active-set` haben unterschiedliche Nutzer und Aktualisierungszyklen. Das Zusammenlegen spart nur einen HTTP-Roundtrip, während `awaitSync` allein bis zu zwölf vermeidbare Requests bietet und die Vote-Pipeline bei vier schnellen Votes acht spart. Es ist daher nicht die wirksamste nächste Maßnahme.

### Nur Server-Caches

Der Moderated-Channels-Cache ist die größte Helix-Ersparnis, verhindert aber die lokale 429 selbst nicht, solange jeder DB-Read ein `ExternalApi`-Permit verbraucht. Er ist Teil der Lösung, nicht ihre einzige Grenze.

### Provider-Budgetkoordinator jetzt bauen

Vertagt: Es gibt keinen belegten Provider-429, aber erhebliche offene Fragen zu Redis-Reservierungen, Crash-Leases, ungeordneten Headerantworten und App-Token-Topologie. Strukturierte Messdaten sind die Voraussetzung für eine spätere, kleinere Spec.

### Dreiwertige Rollenauflösung hier mitbauen

Ausgegliedert: `CanManageChannelAsync` ist nicht nur Gate, sondern auch Anreicherung für Vote-Listen und Ergebnisse (`VoteSessionEndpoints.cs:127`, `178`). `/permissions` ist bool-only (`ChannelEndpoints.cs:85-97`), und die Angular-Guards behandeln derzeit jeden Fehler als fehlende Rechte. Ein pauschales `Unavailable → 503` würde funktionierende Read-Modelle verschlechtern. Der Slice braucht eine eigene Aufrufstellenmatrix und UX-Entscheidung.

## Bewusst vertagt

- Twitch-App-Reservierungen oder anderer verteilter Budgetzustand.
- Observe-/Enforce-Modi und eine konfigurierbare 7TV-Grenze.
- Providerbedingte 429-/503-Fehlerverträge und Tri-State-Rollenauflösung.
- Laufzeit-editierbare Limits und ein Admin-Write-Endpoint.
- Zusammenlegen von `duplicate-names` und `active-set`.
- Entfernen von SSE oder vollständige Datenpayloads in Live-Events.
- Cloudflare-/nginx-Regeländerungen.

Eine Provider-Enforcement-Spec wird erst wieder aufgenommen, wenn das neue Monitoring einen echten Provider-429 oder eine wiederholt kritisch niedrige Twitch-Header-Stichprobe belegt. Vorher wäre jede Grenze erfunden.

## Dokumentation und Commitgrenzen

Jeder der vier Rollout-Schritte ist ein eigener logischer Commit. Vor jedem Commit ist die ausdrückliche Nutzerfreigabe erforderlich. Policy-, Cache- und Monitoring-Topologieänderungen erhalten gemäß `CLAUDE.md` Regel 3 ihren `docs/DECISIONS.md`-Eintrag im selben Commit.

Nur tatsächlich falsche Kommentare werden mit dem jeweiligen Schritt korrigiert: die app-weite User-Token-Behauptung in `Program.cs`, die ungecacheten 7TV-Behauptungen an Usage-/Emote-Gruppen und alte Permit-Zahlen. Der Kommentar an `/channels/mine`, dass dieser Endpunkt selbst ungecacht und teuer ist (`ChannelEndpoints.cs:117-120`), bleibt sachlich richtig, bis der Listencache implementiert wird.
