# CLAUDE.md

Schneller Einstieg für Claude Code. Drei Dokumente daneben, alle verbindlich:

- **[docs/Architectur.md](docs/Architectur.md)** — die vollständige Spezifikation (Module A–D, DB-Schema, Docker-Topologie, Kommunikationsfluss). **Bei Architektur-Fragen zuerst dort lesen.**
- **[docs/DECISIONS.md](docs/DECISIONS.md)** — die vollständige Historie aller Architektur-/Infrastruktur-Entscheidungen, absteigend nach Datum, jeder Eintrag mit `**Betrifft:**`-Zeile. Durchsuchbar per `grep <dateiname> docs/DECISIONS.md`. **„Warum ist X so gebaut?"** steht dort, nicht hier.
- **[docs/Feature-Ideen-2026-08-01.md](docs/Feature-Ideen-2026-08-01.md)** — der Produkt-Backlog (Ideen `A1`–`A15`, `B1`–`B10`, plus eine begründete „würde ich nicht bauen"-Liste). Der Umsetzungsstand steht in der Tabelle oben im Dokument und in einer Statuszeile je Idee. **„Was bauen wir als Nächstes?"** und **„haben wir das schon?"** stehen dort. Wer eine Idee umsetzt, pflegt ihre Statuszeile im selben Commit (analog Regel 3).

## Projekt-Überblick

Emote Purge: plattformübergreifende Webanwendung zur Analyse, Community-Bewertung und Bereinigung von 7TV-Emote-Sets auf Twitch. .NET 10, Clean/Layered Architecture, PostgreSQL (EF Core) als Single Source of Truth, Redis Pub/Sub zur Entkopplung von API und Worker, Angular 22 als Frontend. Produktion läuft auf `emotepurge.app` (VPS, GHCR-Images + Portainer-Stack).

## Umsetzungsstand

| Modul | Inhalt | Stand |
|---|---|---|
| **1 / A** | Chat-Analytics & Live-Synchronisation: Channel-Join/Leave über Redis, anonymes Twitch-IRC, hybrider 7TV-Sync (EventAPI-WebSocket-Live-Deltas hinter Feature-Flag + periodischer REST-Vollsync als Reconciliation), Chat-Matching + 30-Sekunden-Batch-Flush, Worker-Health | ✅ vollständig |
| **B** | Twitch-OAuth-Login (Cookie-Session, kein JWT), Live-Rollenprüfung (Admin-Allowlist / Broadcaster / Live-Moderator / 7TV-Editor), serverseitig wirksames Logout | ✅ vollständig |
| **C** | Voting Engine: `VoteSession`/`Vote`, rollengesteuerte Abstimmung, Score = Keep-Votes − Delete-Votes (Chat-Nutzung ist bewusst **kein** Bestandteil, nur Manager-Kontext neben der Karte — s. DECISIONS „kein Rückweg von Chat-Nutzung in den Beliebtheits-Score") | ✅ vollständig |
| **D** | Angular-Frontend: Login, Übersicht, Usage-Stats-Grid, Voting-UI, 7TV-Mass-Delete-Engine, i18n (de/en), Pagination | ✅ vollständig |
| **Admin** | Globaler Admin-Bereich (`/admin/*`, Allowlist `Auth:AdminTwitchLogins`): Monitoring, Channel-Liste inkl. Resync/Purge, Nutzerliste inkl. Session-Revoke und Rollen-Cache-Invalidierung, Audit-Log (`AuditLogEntry`), eigener SSE-Stream | ✅ vollständig |
| **E** | Launch-Vorbereitung: Ressourcenlimits, Container-Healthchecks (S3-35), Log-Aggregation/Alerting (S3-36), Rechtstexte | 🟡 fast fertig — die technische Seite ist seit 2026-08-05 komplett: Ressourcenlimits (S2-21), Migration-Fail-Fast (S3-34), getrennte Dev-Compose-Namen (S3-38), payloadfreier `GET /api/health` mit 503-Semantik + Container-HEALTHCHECKs (Z1/S3-35), Monitoring-Kette Uptime Kuma + Telegram + Backup VPS→NAS→OneDrive + Log-Rotation (S3-36, VPS-Härtung). Auch die CI-/Lieferketten-Seite ist komplett (SHA-Pins, Dependabot, schlanke Docker-Kontexte, AGPL-3.0-LICENSE), und der Kuma-Monitor zeigt seit dem 2026-09-04 auf `/api/health`. Offen ist damit nur noch, was nicht am Code hängt: **Rechtstexte (S2-20)** + `robots.txt` |
| **Review 2026-07-29** | 81 Befunde; Wellen A–D umgesetzt (D am 2026-08-02: Autorisierungs-Tests, Api-Filter-Matrix, Struktur-Test), nur E (Infra & Launch) offen — s. [Review-2026-07-29-Umsetzung.md](docs/Review-2026-07-29-Umsetzung.md) | 🟡 laufend |
| **Review 2026-08-01** | Struktur, Formatter-Tooling, Fremd-Wartbarkeit — s. [Review-2026-08-01-Struktur-und-Wartbarkeit.md](docs/Review-2026-08-01-Struktur-und-Wartbarkeit.md) | 🟡 laufend |

## Commands

### Build

```
dotnet build EmotePurge.slnx                              # ganze Solution
dotnet build src/EmotePurge.Api/EmotePurge.Api.csproj      # einzelnes Projekt
```

Solution-Datei ist `EmotePurge.slnx` (neues .NET-10-SDK-Format), keine `.sln`. `dotnet build`/`dotnet sln` funktionieren identisch damit.

### Lokal ausführen (ohne Docker)

```
dotnet run --project src/EmotePurge.Api
dotnet run --project src/EmotePurge.Worker
```

Erwartet Postgres/Redis erreichbar über die in `appsettings.json` hinterlegten `localhost`-Connection-Strings (Default-Credentials matchen `.env.example`: `emotepurge`/`change-me`), z. B. via `docker compose up postgres redis`. Api lauscht lokal per `launchSettings.json` auf `http://localhost:5151` (nicht `8080` — das gilt nur im Container). Login zuerst im Browser: `http://localhost:5151/api/auth/twitch/login` — `join`/`leave` erfordern eine authentifizierte Session.

### Frontend (Angular) lokal ausführen

```
npm --prefix web install     # einmalig
npm --prefix web start       # ng serve mit Dev-Proxy (web/proxy.conf.json, /api -> :5151)
```

Erwartet die Api parallel laufend per `dotnet run --project src/EmotePurge.Api` (Port `5151`) — **nicht** die VS-Code-`Api`-Launch-Config, die hart auf `:8080` bindet und damit den lokal registrierten Twitch-OAuth-Redirect (`http://localhost:5151/api/auth/twitch/callback`) bricht. `ng serve` läuft dann auf `http://localhost:4200`.

#### Auf einem echten Handy testen

**Sobald es um Mobile-Ansichten geht („ich will das auf dem Handy sehen", Touch-/`pointer: coarse`-Verhalten, Sheet-Dialoge), ist das der Startweg — nicht `npm start`:**

```
docker compose up -d postgres redis
dotnet run --project src/EmotePurge.Api --launch-profile lan
__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS=<eigener-hostname> npm --prefix web run start:lan
```

Das Handy ruft im heimischen WLAN einen eigenen Hostnamen auf, hinter dem ein Reverse Proxy TLS terminiert und an den Dev-Server auf `:4200` durchreicht. Gleiche Datenbank, gleiche Testdaten, Hot Reload, echter Twitch-Login. Die beiden `lan`-Varianten unterscheiden sich vom Alltagsstart nur darin, dass der Dev-Server auf allen Schnittstellen lauscht und die Twitch-Redirect-URI auf den Hostnamen umgestellt ist — **weder `appsettings.Development.json` noch `npm start` sind davon berührt**.

**Der Hostname steht nicht im Repo** (es ist öffentlich, s. DECISIONS 2026-09-10). Das `lan`-Profil setzt nur das Flag `EMOTEPURGE_LAN`; der Hostname selbst liegt in der gitignorierten `src/EmotePurge.Api/appsettings.Lan.json`. Einmalig anlegen:

```
cp src/EmotePurge.Api/appsettings.Lan.json.example src/EmotePurge.Api/appsettings.Lan.json
```

und darin die beiden Platzhalter durch den echten Hostnamen ersetzen. Fehlt die Datei, startet `--launch-profile lan` trotzdem und fällt auf die `localhost`-Redirect-URI zurück — der Login schlägt dann sichtbar fehl, statt still etwas Falsches zu tun. Die Datei ist zusätzlich aus `dotnet publish` und aus dem Docker-Build-Kontext ausgeschlossen, kann also nicht in ein Image geraten. **Der Dev-Server braucht denselben Hostnamen getrennt**, über `__VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS`; `ng serve --allowed-hosts` ist ausdrücklich **nicht** der Weg, weil die Angular-CLI die Option nur als Boolean kennt und damit Vites Schutz gegen DNS-Rebinding ganz abschaltet. Der projektöffentliche Teil steht in [docs/Operations.md](docs/Operations.md); die eigene Netz-Topologie (Proxy-Manager, DNS-Rewrite, Zertifikat) liegt in `infra-docs`.

Es gibt keine Staging-Stage; die Umgebung *ist* die lokale, nur unter anderem Namen erreichbar. Von außen (Mobilfunk) ist sie bewusst nicht erreichbar.

### Tests

```
dotnet test EmotePurge.slnx                # Backend, braucht laufendes Docker (Testcontainers)
npm --prefix web test -- --watch=false     # Frontend Unit (Vitest)
npm --prefix web run e2e                   # Frontend E2E (Playwright, /api/** gemockt)
node scripts/coverage-local.mjs            # Coverage-Vorabschätzung gegen die 80-%-Schwelle
```

**Die E2E-Suite läuft nur, wenn auf `:5151` keine Api lauscht.** Gemockt wird pro Test einzeln; was ein Test nicht mockt, fällt durch den Dev-Proxy. Ist dort nichts, scheitert die Anfrage sofort und die Seite rendert trotzdem — antwortet dort eine echte Api mit `401`, schickt der `apiAuthInterceptor` die App auf die Login-Seite, und ab da findet kein Test mehr seine Inhalte. Das Fehlerbild ist irreführend: rund die halbe Suite fällt mit „element not found" durch, quer über Dateien, die mit der Änderung nichts zu tun haben. Gemessen am 2026-08-07: mit laufender Api 38 von 76 rot in 5 min, ohne sie 76 grün in 52 s. Die Suite ist seither gewachsen — Stand 2026-08-30 sind es **96 Tests in rund 1,5 min**. Die beiden Fälle zum Sync-Fehlergrund (aus #32), die früher je rund 31 Sekunden echt gewartet haben, laufen seit #33 auf Playwrights `page.clock`; die Suite hat damit keine realzeitwartenden Fälle mehr. Wer selbst einen zeitgesteuerten Fall schreibt: `page.clock.install()` **vor** `goto` (sonst rennt die zoneless-Change-Detection gegen die gefälschten Timer), danach `runFor(...)` und nicht `fastForward(...)` — Letzteres feuert jeden fälligen Timer höchstens einmal und macht ein wiederkehrendes Intervall falsch grün. **`install()` hält die Uhr nicht an** — die Zeit läuft danach normal weiter, bis `pauseAt(...)` fällt (Playwright-Doku, `Clock.pauseAt`). Soll Echtzeit während einer Phase keinen Timer auslösen (z. B. eine 200-ms-Verweilzeit beim Scrollen), vorher `pauseAt` aufrufen. Wer gerade [mobil getestet](docs/Operations.md) hat, beendet also erst `dotnet run`.

**`scripts/coverage-local.mjs` schätzt vor dem PR, ob die SonarCloud-Quality-Gate reißt.** Es fährt beide Suiten mit Coverage (`--collect:"XPlat Code Coverage;Format=opencover"` bzw. `--coverage --coverage-reporters=lcov` — `dotnet test` allein erzeugt **keine** Coverage-Datei) und rechnet die Quote über die gegen `origin/main` geänderten Dateien. Die Schwelle ist 80 % auf neuem Code, und seit dem 2026-09-06 blockiert sie: `analyze` wartet auf das Gate und ist ein required check (s. DECISIONS). **Die Zahl ist eine dateigenaue Näherung, Sonar misst zeilengenau — und sie ist in _beide_ Richtungen unscharf, ohne Schranke.** Eine einzelne neue Zeile in einer großen, gut gedeckten Bestandsdatei sieht hier gut aus, während Sonar für genau diese Datei 0 % auf neuem Code meldet. Umgekehrt zieht eine schlecht gedeckte Bestandsdatei die Zahl hier nach unten, obwohl die wenigen neuen Zeilen von einem neuen Test voll gedeckt sind und Sonar 100 % zählt. Bei **neuen** Dateien ist die Näherung nah an der Wahrheit, weil dort fast alle Zeilen neu sind; bei kleinen, chirurgischen Änderungen in großen Dateien ist sie am schwächsten. Seit der Frontend-Lauf **alle** Produktivdateien in den Report zwingt, wiegt zudem eine große, ungetestete Datei mit vollem Zeilen- und Zweiggewicht im Nenner, auch wenn nur wenige Zeilen darin geändert wurden — die Gesamtquote fällt dadurch tendenziell strenger aus als Sonars. Lies das Ergebnis als Anlass hinzusehen, nicht als Urteil. `--skip-tests` wertet vorhandene Reports erneut aus, `--frontend-only`/`--backend-only` halbieren die Laufzeit.

Drei Backend-Testprojekte (xUnit):

- **`tests/EmotePurge.Infrastructure.Tests`** deckt `EmotePurge.Infrastructure` ab — Integrationstests laufen per Testcontainers gegen echte, ephemere Postgres-/Redis-Container (kein Mocking von `AppDbContext`/`IConnectionMultiplexer`), reine Logik-Tests (z. B. `EmoteMatchCache`, `SevenTvDispatchParser`, `ChannelAccessService`) ohne Container. Ausschlaggebend für `Unit/` vs. `Integration/` ist allein, ob die Klasse unter Test echte Infrastruktur berührt — `VoteEligibilityService` und `MyChannelsService` nehmen `AppDbContext` und liegen deshalb trotz reiner Entscheidungslogik in `Integration/`.
- **`tests/EmotePurge.Worker.Tests`** ist bewusst container-frei und testet die puren Entscheidungs-/Zustandsklassen der Worker-Transporte (`TwitchReconnectBackoffPolicy`, `TwitchReconnectSignalSlot`, `TwitchWatchdogPolicy`, `SevenTvSubscriptionRegistry`, `SevenTvBackoffPolicy`, `EmoteUsageCounter`).
- **`tests/EmotePurge.Api.Tests`** (seit 2026-08-02) fährt per `WebApplicationFactory` die echte `Program.cs`-Pipeline und prüft die Endpoint-Filter-Matrix (401/403/404/409 plus den 400-Vertrag der Namensvalidierung) an den echten Routen. Ebenfalls container-frei: substituiert werden nur `IChannelAccessService`, `IVoteEligibilityService`, `IChannelService` und `IConnectionMultiplexer` — Letzteres, weil `RequestDelegateFactory` die Handler-Services **vor** der Filter-Pipeline auflöst, ein abgelehnter Request also trotzdem den ganzen Service-Graph konstruiert.

### EF Core Migrationen

`dotnet-ef` ist als globales Tool installiert. Migrationen liegen in `EmotePurge.Infrastructure/Migrations/`, Startprojekt für die Connection-String-Auflösung ist `EmotePurge.Api`:

```
dotnet ef migrations add <Name> --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api
```

#### Prod-Migration (manuell, über SSH-Tunnel)

Migrationen laufen in Produktion **nicht** automatisch beim App-Start — sie werden von Hand nachgezogen, **bevor** die neuen Images deployt werden (additive Migrationen ignoriert das noch laufende alte Image; umgekehrt liefe die neue Api gegen fehlende Spalten). Prod-Postgres ist auf dem VPS nur an `127.0.0.1:5433` gebunden (`docker-compose.prod.yml`), also erst tunneln:

```
ssh -N -L 15432:127.0.0.1:5433 vps
```

`vps` ist der Host-Alias aus `~/.ssh/config` auf der Devbox, und er ist **nicht** nur Bequemlichkeit: seit dem Cloudflare-Umzug (s. DECISIONS 2026-08-29) zeigt `emotepurge.app` auf Cloudflare, nicht mehr auf den Origin — ein `ssh …@emotepurge.app` läuft ins Leere. Der Alias trägt die echte Origin-Adresse und den Benutzer, beides gehört nicht ins Repo.

Dann lokal, in einer zweiten Shell. `--connection` statt einer Umgebungsvariable, damit nichts für spätere lokale Läufe hängen bleibt; in PowerShell **einfache** Anführungszeichen, sonst interpoliert die Shell ein `$` im Passwort:

```
dotnet ef migrations list --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'
dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'
```

Erst `list` (zeigt, was `(Pending)` ist — mehr als erwartet heißt: Prod hängt mehrere Feature-Runden zurück, dann vorher die Migrationen durchsehen), dann `update`, dann `list` zur Gegenprobe. Das Passwort steht in der `.env` auf dem VPS und gehört nirgends ins Repo (Regel 17).

### Docker Compose (voller Stack)

```
docker compose up -d --build      # redis, postgres, api, worker
docker compose logs -f api
docker compose down                # -v zum Löschen des Postgres-Volumes
```

Konfiguration über `.env` am Repo-Root (Vorlage: `.env.example`). Der `api`-Build enthält eine `web-build`-Stage (Node), die `web/` baut und das Ergebnis nach `wwwroot/` kopiert — die Angular-App wird direkt von der Api unter `http://localhost:8080/` mitausgeliefert, kein eigener Frontend-Service/Port nötig. Produktion läuft über `docker-compose.prod.yml` (GHCR-Images, s. Architectur.md Abschnitt 6b).

### Backup

`scripts/backup-postgres.sh` plus [docs/Operations.md](docs/Operations.md) — dort stehen Aufrufform, Umgebungsvariablen, Restore-Probe und der Katastrophenfall. Was auf dem **eigenen** VPS einmalig einzurichten ist (Cron, Zielkette, Monitoring), steht nicht mehr im Repo, sondern in `infra-docs`.

## Architektur

Vier .NET-Projekte plus ein Angular-Workspace. Verweise: `Api`/`Worker` → `Infrastructure` → `Core`.

- **`EmotePurge.Core`** — Entitäten, Service-**Interfaces**, Messaging-Abstraktion (`IRedisPublisher`/`IRedisSubscriber`), 7TV-/Twitch-DTOs. Hat bewusst **keine** Abhängigkeit auf EF Core, StackExchange.Redis, HTTP-Clients oder ASP.NET Core: die Redis-Interfaces arbeiten mit reinen `string`-Signaturen, die DTOs sind reine `record`s.
- **`EmotePurge.Infrastructure`** — `AppDbContext` (Npgsql), Redis-Implementierungen, alle Service-Implementierungen, die typisierten 7TV-/Twitch-`HttpClient`s, und `ServiceCollectionExtensions.AddEmotePurgeInfrastructure(configuration)` als **einziger** DI-Registrierungspunkt (Api und Worker rufen nur diese Methode auf).
- **`EmotePurge.Api`** — ASP.NET Core **Minimal API**, keine Controllers. `Program.cs` ist reines Bootstrapping; die Endpoints liegen nach Domäne getrennt in `Endpoints/*.cs` (je eine `Map<X>Endpoints(this WebApplication app)`-Extension-Method, registriert per `MapGroup`). Autorisierung läuft über `IEndpointFilter`-Klassen in `Auth/`, Validierung über `Validation/`. Hört im Container auf `:8080`, lokal auf `:5151`.
- **`EmotePurge.Worker`** — .NET Worker Service mit neun Hosted Services: `Worker` (Boot-Recovery + Redis-Kommandos), `UsageFlushWorker`, `SevenTvPeriodicResyncWorker`, `SevenTvEventWorker` (7TV-EventAPI-WebSocket, Feature-Flag `SevenTv:EventApi:Enabled`), `TwitchConnectionWatchdog`, `WorkerHealthPublisher`, `WorkerRosterPublisher` (Per-Channel-Roster nach `worker:roster`, 60-s-Takt), `TwitchLivePollWorker` (Helix-Streams-Poll per App-Access-Token, `Twitch:LivePollIntervalSeconds` Default 300 s, schreibt `ChannelLiveDay`), `TwitchIdentityReconcileWorker` (hält Channel-Zeilen anhand der unveränderlichen Twitch-ID mit Helix in Sync, folgt Umbenennungen und führt Duplikate zusammen, `Twitch:IdentityReconcileIntervalMinutes` Default 60) — und einen zweiten Einstiegspunkt `harness <kanal>` (kein Hosted Service, s. DECISIONS). `TwitchChatManager` kapselt genau **einen** langlebigen `TwitchLib.Client` für die gesamte Worker-Lebensdauer — **nie** pro Channel/Join neu instanziieren. „Einen" heißt seit 2026-09-08 *einen zur Zeit*: TwitchLib rekonnektiert selbst nicht mehr (`NoReconnectionPolicy`), jeder Verbindungsverlust ist ein Signal, und der Watchdog trägt die Wiederaufbau-Schleife allein — Ersatz des Objekts statt Reparatur, Warten auf den Handshake (`004`), danach ein gedrosselter Rejoin außerhalb der Lese-Schleife (Issue #68/#114, s. DECISIONS). Die Backoff-Entscheidung liegt TwitchLib-frei in `TwitchReconnectBackoffPolicy`, die Stale-Erkennung des Watchdogs (empfangene IRC-Frames inkl. Twitchs ~5-min-Server-PING, Schwelle 15 min — seit 2026-08-03, s. DECISIONS) ebenso TwitchLib-frei in `TwitchWatchdogPolicy`. Analog kapselt `SevenTvEventClient` genau **eine** EventAPI-Verbindung; dessen Subscription-Zustand liegt in `SevenTvSubscriptionRegistry` (Desired-State-first), das Reconnect-Pacing in `SevenTvBackoffPolicy` — beide pur und getestet.
- **`web/`** — Angular 22, Standalone Components + Signals (kein NgModule), Tailwind CSS, `@angular/cdk` fürs Virtual Scrolling, Transloco für i18n. `core/` = injectable Services, Guards, Models · `shared/` = wiederverwendbare Bausteine · `features/` = geroutete Seiten. Auth ist cookie-basiert wie das Backend, kein Token im Frontend. Details in [`web/.claude/CLAUDE.md`](web/.claude/CLAUDE.md). Die visuelle Sprache steht in [docs/UI-Designsprache.md](docs/UI-Designsprache.md) (verbindlich, handgeschrieben) und zusätzlich als maschinenlesbare Ableitung in [DESIGN.md](DESIGN.md) am Repo-Root — bei Widerspruch gilt die Designsprache.

Connection-Strings/Redis-Config kommen aus `appsettings.json` (`ConnectionStrings:DefaultConnection`, `Redis:ConnectionString`), in Docker per Environment-Variablen (`ConnectionStrings__DefaultConnection`, `Redis__ConnectionString`) überschrieben. Der 7TV-Endpunkt (`https://7tv.io/v3/`) ist fest im Code verdrahtet — externer Drittanbieter-URL, kein Umgebungsunterschied wie bei DB/Redis.

### Schichtentreue

Verbindlich, und beim Review vom 2026-07-29 über alle `using`-Direktiven geprüft:

| Schicht | Erlaubt | Verboten |
|---|---|---|
| **`EmotePurge.Core`** | nur BCL | EF Core, StackExchange.Redis, `System.Net.Http`, ASP.NET Core — auch transitiv. 0 `PackageReference`, 0 `ProjectReference`. **Seit 2026-08-02 von `CoreAssemblyReferenceTests` erzwungen**, nicht mehr nur Review-Disziplin |
| **`EmotePurge.Infrastructure`** | → Core; EF/Redis/HTTP | ASP.NET-Core-Typen, Rückverweis auf Api/Worker |
| **`EmotePurge.Api`** | → Infrastructure, → Core | direkter `AppDbContext`- **oder** `IConnectionMultiplexer`-Zugriff aus Handlern; alles über Service-Interfaces |
| **`EmotePurge.Worker`** | → Infrastructure, → Core | direkter `AppDbContext`-Zugriff — seit 2026-08-02 verstoßfrei (die zwei Stellen laufen über `IChannelService.ListActiveChannelNamesAsync`) |
| **`web/core/`** | nichts aus `features/` oder `shared/` | Verweise „nach oben" |
| **`web/shared/`** | → `core/` | nichts aus `features/` |
| **`web/features/`** | → `core/` + `shared/` | — |

## Geltende Regeln

Die Begründung zu jeder Regel steht in [docs/DECISIONS.md](docs/DECISIONS.md).

1. **Committen, auf einen Feature-Branch pushen und PRs aufmachen darf eine Session selbstständig — mergen nicht.** Der Merge gehört dem Nutzer, und **direkt auf `main` pushen ist ihm gleichgestellt**: Git nennt es anders, wirksam ist es dasselbe. Drei Dinge hebt diese Freiheit ausdrücklich **nicht** auf — die Fertigmeldungs-Gates aus [Arbeitsweise](#arbeitsweise) gelten vor dem Commit, nicht statt seiner; die Zweitmeinung aus Regel 22 wird eingeholt und dem Nutzer **vorgelegt**, nicht ihm erspart; und ein Push ist kein Deploy, ein Stack-Update bleibt eine getrennte, ausdrücklich abgestimmte Handlung.
2. **Conventional Commits** (`feat:`, `fix:`, `chore:`, `docs:`, …), in mehreren logisch getrennten Commits statt einem Sammel-Commit pro Feature.
3. **Ein Commit, der eine Konvention, einen Vertrag oder eine Topologie ändert, enthält seinen Eintrag in `docs/DECISIONS.md` im selben Commit.**
4. **Kein `AppDbContext` und kein `IConnectionMultiplexer` direkt aus Minimal-API-Handlern.** Neue Backend-Fähigkeiten bekommen ein Interface in `Core/Services/` und eine Implementierung in `Infrastructure/Services/`. Ausdrücklich **kein** generisches Repository-Pattern über EF Core.
5. **Klassen mit nicht-trivialer Logik oder externer Abhängigkeit (DB, Redis, TwitchLib, 7TV) bekommen ein Interface**, reine Daten-/DTO-Typen nicht. Ausnahme: ein `BackgroundService`, der ausschließlich per `AddHostedService<T>()` läuft und nirgends injiziert wird, wird als konkrete Klasse registriert.
6. **Minimal API, keine Controllers.** Neue Endpoints kommen in `Endpoints/*.cs`, nicht in `Program.cs`. Autorisierung über `IEndpointFilter`, nicht über ASP.NET-Core-Policies.
7. **Die API liefert bei Fehlern nur sprachneutrale Codes** (`ApiErrorCodes`), nie fertigen Text. Ein neuer Code braucht denselben Eintrag in `web/src/app/core/i18n/api-error.ts` **und** in beiden Locale-Dateien. `api-error-locales.spec.ts` erzwingt seit 2026-08-02 die beiden hinteren Schritte (Code ↔ Übersetzung in beiden Sprachen); der Schritt von `ApiErrorCodes.cs` nach `api-error.ts` bleibt Disziplin — genau dort war die Liste vorher zweimal auseinandergelaufen.
8. **`Emote.Id` ist ein interner Guid, nicht die 7TV-ObjectID.** Die 7TV-ID steht in `SevenTvEmoteId` und ist nur pro Channel eindeutig (Unique-Index `(ChannelId, SevenTvEmoteId)`), weil dasselbe Emote in mehreren Channels aktiv sein kann.
9. **Channel-Namen laufen immer durch `ChannelName.Normalize`.** Die DB hält sie getrimmt und lowercase; jeder Lookup filtert auf die normalisierte Form. Client-Validatoren müssen die serverseitige *Normalisierung* mitdenken, nicht nur die Regex kopieren — Twitch-Namen werden mit Großbuchstaben getippt (`HandOfBlood`).
10. **Aggregat-Queries in `Infrastructure`: Navigations-Joins vor einem `GroupBy` erst auf eine skalare ID-Liste reduzieren**, nicht direkt darauf gruppieren — EF Core/Npgsql übersetzt das sonst nicht.
11. **Neue Services/Logik in `EmotePurge.Infrastructure` bekommen einen Test** in `tests/EmotePurge.Infrastructure.Tests` (Fixture pro externer Abhängigkeit unter `Fixtures/`, `Integration/` oder `Unit/` je nachdem, ob echte Infrastruktur gebraucht wird). **Neue reine Logik in `EmotePurge.Worker`** (Policies, Registries — alles ohne Postgres/Redis-Berührung) bekommt ihren Test im container-freien `tests/EmotePurge.Worker.Tests`; Transport-Klassen selbst (`TwitchChatManager`, `SevenTvEventClient`) werden bewusst live statt gegen Fakes verifiziert (Regel 16). **Ein neuer `IEndpointFilter` oder eine Änderung an der Filter-Reihenfolge einer `MapGroup` bekommt seinen Fall in `tests/EmotePurge.Api.Tests`** — Endpoint-*Handler* dagegen nicht: die bleiben dünn und delegieren an die getestete Infrastructure-Schicht.
12. **Neue Services/Guards/reine Utilities in `web/src/app/core/` + `shared/` bekommen einen co-located `*.spec.ts`** (Vitest; `HttpTestingController` für alles mit `HttpClient`, `TestBed.runInInjectionContext` für Guards). **Komponenten mit eigener, nicht-trivialer Entscheidungslogik ebenso — aber nur für diese Logik: Verhalten ja, Vorlage nein.** Rein darstellende Komponenten bekommen keinen Spec. **Erlaubt** sind Zustandsübergänge und `computed()`-Ergebnisse, Outputs und Dialog-Rückgaben, Sperrentscheidungen samt ihrem Grund, Accessibility-Semantik (Rolle, zugänglicher Name, `aria-*`-Bezüge) sowie eine Zeilen- oder Meldungsreihenfolge, **sofern sie anderswo als Vertrag dokumentiert ist** (z. B. `docs/UI-Designsprache.md` §7.2). **Nicht erlaubt** sind CSS-Klassen, Tailwind-Ketten, nicht-semantische Tags und Verschachtelung, Snapshots, und der Wortlaut einer Übersetzung als Selbstzweck — er darf eine Meldung identifizieren, er ist nicht der Prüfgegenstand. Faustregel im Zweifel: Was sich beim Umgestalten ändern darf, ohne dass ein Nutzer etwas anderes erlebt, gehört in keinen Spec; dafür bleibt Live-Testen im Browser das Mittel. Größere User-Flows über mehrere Seiten zusätzlich als Playwright-E2E mit gemockten `/api/**`-Responses.
13. **Nie ein required Signal-Input im Konstruktor lesen** — immer über `effect()`, `rxResource({ params })` oder `ngOnInit()` deferren, sonst `NG0950`.
14. **Ein `computed()`, das über eine Klasseninstanz auf mutable State zugreift statt auf ein Signal, reagiert nie auf Änderungen** daran. State, den ein `computed()` liest, gehört in ein Signal.
15. **Nach Code-Änderungen an `Api`/`Worker` vor jedem `docker compose up -d <service>`-Test entweder `--build` mitgeben oder vorher `docker compose build <service>` laufen lassen** — `up` allein reused ein vorhandenes, potenziell uraltes Image klaglos.
16. **Backend-Features vor dem Commit live gegen echte Postgres-/Redis-/Twitch-/7TV-Zugänge verifizieren**, nicht nur `dotnet build`.
17. **Twitch-Client-Secrets nie ins Repo**: lokal `dotnet user-secrets` im `EmotePurge.Api`-Projekt, im Container `.env` (gitignored, Platzhalter in `.env.example`).
18. **Formatierung ist Werkzeugsache, nicht Geschmackssache**: `npm --prefix web run format` (Prettier) und `dotnet format EmotePurge.slnx`. Die CI prüft beides plus `npm --prefix web run lint`. Repoweite Reformatierungen kommen in einen eigenen `style:`-Commit, der **nichts anderes** enthält, und wandern in `.git-blame-ignore-revs`.
19. **Member-Reihenfolge in C#-Klassen**: `const`/`static readonly` → `readonly` Felder → veränderliche Felder → öffentliche Properties → öffentliche Methoden → private Methoden → `private static` Helper; verschachtelte Typen ans Klassenende. Kein StyleCop — das ist Review-Disziplin, nicht erzwungen (Begründung im Entscheidungslog). Die Angular-Entsprechung steht in [`web/.claude/CLAUDE.md`](web/.claude/CLAUDE.md) und wird dort per ESLint teilweise erzwungen.
20. **Orchestrieren statt selbst ausführen** — Delegation, Modellwahl und Fable-Einsatz stehen global in `~/.claude/CLAUDE.md`.
21. **Plan-Tasks laufen immer als Subagent, und Pläne enthalten keinen fertigen Code** — ebenfalls global. Was hier zusätzlich gilt, steht unter [Arbeitsweise](#arbeitsweise).
22. **Vor jedem Merge auf `main` eine unabhängige Zweitmeinung** (Codex Sol), Konzepte und Pläne zusätzlich adversarial — global geregelt.

## Arbeitsweise

Orchestrierung, Modellwahl, Fable-Einsatz, Pläne ohne fertigen Code, Plan-Tasks als Subagent und die Codex-Zweitmeinung stehen **global** in `~/.claude/CLAUDE.md` und gelten hier unverändert. Die Datei ist versioniert: sie liegt in `sensitron/infra-docs` unter `claude/` und ist nach `~/.claude/` gesymlinkt — eine Änderung daran gehört in einen Commit dort. Hier steht nur, was projektspezifisch ist:

- **„Fertig" heißt in diesem Repo:** `dotnet test EmotePurge.slnx` (braucht laufendes Docker, Testcontainers), `npm --prefix web test -- --watch=false`, und bei UI-Änderungen zusätzlich `npm --prefix web run e2e` — Letzteres nur, wenn auf `:5151` keine Api lauscht. Ein `dotnet build` allein ist keine Fertigmeldung, und bei Backend-Features ersetzt keine Suite die Live-Verifikation aus Regel 16.
- **Vor dem PR zusätzlich `node scripts/coverage-local.mjs`.** Die SonarCloud-Quality-Gate verlangt 80 % Coverage auf neuem Code und blockiert den Merge (`analyze` ist required check). Keine der drei Suiten oben misst Coverage — wer nur sie laufen lässt, erfährt erst am PR, dass das Gate reißt. Das Skript ist eine dateigenaue Näherung an Sonars zeilengenaue Messung und in beide Richtungen unscharf — es ersetzt den PR-Befund nicht, macht ihn aber vorhersehbar (s. [Tests](#tests)).
- **Keine Abweichung von der globalen Arbeitsweise.** Sollte hier je eine nötig werden, steht sie an dieser Stelle und nicht als Kopie der ganzen Regel.

## Sprache

Die Sprachmischung im Bestand ist historisch gewachsen. Seit dem 2026-09-10 (#152) verläuft die
Grenze nicht mehr zwischen Code und Dokumentation, sondern zwischen **Außenwirkung und
Denkwerkzeug**: Was ein fremder Contributor liest, ist englisch; was der Betreiber zum Denken
schreibt, bleibt deutsch. Begründung im Entscheidungslog.

**Englisch:**

- Bezeichner, Typen und öffentliche APIs.
- Kommentare in neuem Code. Die Regel gilt seit Welle A und hat faktisch durchgegriffen (Messung
  2026-09-10: noch 48 von 7.984 C#-Kommentarzeilen und 6 von 2.017 in TypeScript sind deutsch).
  Verbliebene deutsche Kommentare bleiben stehen, werden aber nicht fortgeführt; gemischtsprachige
  Dateien wie `ITwitchChatManager.cs` sind Altlast, kein Muster.
- Log- und `throw`-Messages. **Das ist seit #152 umgedreht**, vorher galt hier deutsch. Wer einen
  Worker-Fehler debuggt, liest genau diese Zeilen — ein Fremder so oft wie der Betreiber.
- Commit-Messages, PR-Titel und -Beschreibungen, Issue-Titel und -Bodies, Release Notes.
- `README.md`, `CONTRIBUTING.md`, `SECURITY.md`, die GitHub-Templates, die Kommentare in
  `.github/workflows/`, Repo-Beschreibung und Topics.
- **Neue** Einträge in `docs/DECISIONS.md`.
- `docs/Architectur.md`, `docs/UI-Designsprache.md`, `DESIGN.md` und `docs/Operations.md`.
  Die drei früheren Betriebsanleitungen sind dabei **nicht** übersetzt, sondern aufgeteilt worden:
  was für jeden Betreiber gilt, steht englisch in `docs/Operations.md`; die eigene Infrastruktur
  (Proxy-Manager, NAS-Kette, Heimnetz) ist nach `infra-docs` gewandert.

**Deutsch:**

- Diese Datei und `PRODUCT.md`.
- Pläne, Konzepte, Specs, Design- und Untersuchungsberichte unter `docs/`. Das sind Denkwerkzeuge
  des Betreibers, kein Außenauftritt — sie in einer Fremdsprache zu schreiben kostet Genauigkeit,
  ohne jemandem zu nützen.
- Die **bestehenden** Einträge in `docs/DECISIONS.md`. Das Dokument wird dadurch dauerhaft
  zweisprachig; die Alternative wären 6.106 Zeilen Übersetzung gewesen.

Kein Bestandscode und keine Bestandsdokumentation wird rückwirkend umgeschrieben, und die offenen
deutschen Issues bleiben, wie sie sind.

## Bekannte offene Grenzen

- **Twitchs JOIN-Limits — zwei getrennte, das härtere ist ein Bestandslimit.** (1) *Rate*: eine nicht-verifizierte Verbindung darf 20 JOINs pro 10 Sekunden; TwitchLib drosselt JOINs überhaupt nicht, aber seit 2026-09-08 (#68) laufen **alle** unsere JOIN-Pfade — auch der Rejoin nach einem Reconnect — über dieselbe 600-ms-Drossel außerhalb von TwitchLibs Lese-Schleife; die Rejoin-Dauer eines einzelnen Ereignisses wächst dadurch linear mit der Kanalzahl (0,6 s × N). (2) *Bestand*: **seit 2024-05-15 darf ein Account maximal 100 Chatrooms gleichzeitig gejoint haben** ([dev.twitch.tv/docs/chat](https://dev.twitch.tv/docs/chat/)) — ausgenommen sind nur Kanäle, in denen der Account Broadcaster oder Moderator ist. Die Messung vom 2026-07-30 (28 ungedrosselte JOINs in 5 s, 0 Fehler) hat die *Rate* auf dem inzwischen abgeschafften ungedrosselten Rejoin-Pfad geprüft und sagt über diese Decke nichts. Bindend sind seither Twitchs 100-Chatroom-Decke und die 7TV-EventAPI-Subscription-Grenze (docs/Konzept-Worker-Reconnect-2026-09-08.md, 7.2). Vor einem größeren Ausbau: verifizierter Bot-Account (2.000 JOINs/10 s, hebt beide Limits) oder Sharding — Details in [Review-2026-07-29-Umsetzung.md](docs/Review-2026-07-29-Umsetzung.md). **Ein Wechsel auf EventSub ist ausdrücklich kein Ausweg**: Twitch wendet beide Limits auf `channel.chat.message` mit User-Token wortgleich an — s. [Untersuchung-Twitch-EventSub-2026-08-01.md](docs/Untersuchung-Twitch-EventSub-2026-08-01.md).
- **Twitch-Token-Refresh ist in-process.** Seit 2026-07-30 werden Access Tokens serverseitig per Refresh-Token erneuert (lazy, Single-Flight — s. DECISIONS-Eintrag); der Lock ist aber ein In-Process-Semaphor: bei mehr als einer Api-Replica bräuchte es einen verteilten Lock. Idle gespeicherte Tokens werden zudem nicht stündlich validiert, nur benutzte.
- **7TV-EventAPI-Grenzen.** `subscription_limit` ist 500 pro Verbindung (2 Subscriptions je Channel → Connection-Sharding nötig ab ~250 Channels; bisher nur eine 90-%-Warnung im Log). Kein Resume/Replay und ~1-h-Verbindungs-TTL — der periodische REST-Resync ist deshalb Pflicht, nicht Optimierung. Zustellqualität bei ~900er-Sets (HandOfBlood) ungemessen; 7TVs REST-Cache kann 10–30 min veraltet sein (SevenTV/SevenTV#81), das Mess-Log „archiviert <15 min altes Emote" in `ReconcileAsync` quantifiziert die Folgen. Details: [docs/Untersuchung-7TV-WebSocket-2026-07-30.md](docs/Untersuchung-7TV-WebSocket-2026-07-30.md).

## Skill routing

When the user's request matches an available skill, invoke it via the Skill tool. When in doubt, invoke the skill.

Key routing rules:
- Product ideas/brainstorming → invoke /office-hours
- Strategy/scope → invoke /plan-ceo-review
- Architecture → invoke /plan-eng-review
- Design system/plan review → invoke /design-consultation or /plan-design-review
- Full review pipeline → invoke /autoplan
- Bugs/errors → invoke /investigate
- QA/testing site behavior → invoke /qa or /qa-only
- Code review/diff check → invoke /review
- Visual polish → invoke /design-review
- Ship/deploy/PR → invoke /ship or /land-and-deploy
- Save progress → invoke /context-save
- Resume context → invoke /context-restore
- Author a backlog-ready spec/issue → invoke /spec
