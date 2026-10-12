# Admin-Dashboard: Aktivität und Last (#381) — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext (Regel 21).
> Der Task bekommt diesen Plan und die Spec
> [`docs/superpowers/specs/2026-10-11-admin-dashboard-381-spec.md`](../specs/2026-10-11-admin-dashboard-381-spec.md)
> (Betreiberentscheidungen in §14 und im „Decided"-Kommentar auf #381 vom 2026-10-11); er rollt die
> Entscheidungen **nicht** neu auf. **Kein fertiger Code in diesem Plan** — Verträge, Namen,
> Grenzfälle, Testfälle und Reihenfolge ja, Rümpfe nein. Pfade absolut unter
> `/home/dev/projects/EmotePurge/`; `cd` hält nicht zwischen Shell-Aufrufen.
>
> **Kein Prod-Zugriff.** Keine SSH-Verbindung zu `vps` oder `nas`, auch nicht zum Nachsehen (globale
> Regel „Fernzugriff"). Jede Zahl über Prod stammt aus der Spec oder vom Betreiber.
>
> **`:5151`-Falle:** Die E2E-Suite und der Audit-Harness laufen nur, wenn auf `:5151` keine Api
> lauscht (CLAUDE.md „Tests"). Wer Live-Verifikation und E2E in einem Task macht, beendet erst
> `dotnet run`.

> **Revision 2 (2026-10-11):** acht Befunde des adversarialen Codex-Sol-Reviews (gpt-6.1-sol)
> eingearbeitet, alle gegen Spec und Code nachgeprüft und **alle innerhalb der Spec** lösbar:
> (1) Load-`13mo` ist ein **rollendes** Fenster (Spec §5.2 „≈ 395", §3), nicht der Monatserste der
> Aktivität — Plan v1 hatte das vermischt (406–426 Buckets); jetzt ≤ 398, plus harte 400er-Kappe mit
> Test. (2) B5 misst Allokation prozessweit und isoliert, Ende-zu-Ende-Latenz über echtes HTTP in B6.
> (3) B6 erfasst den RSS-High-Water-Mark je Neuaufbau, koordiniert Health-Proben mit kalten Neuaufbauten
> und beweist per eingeschleusten Verstößen, dass das Gate reißt. (4) Das Chart-Primitiv trägt n
> Reihen; die Rate-Limit-Reihe hat lokale Ablehnungen plus eine Reihe je ablehnender
> Provider/Call-Source. (5) Hourly-Workload heißt „je Ausgabe-Bucket (N h)", nicht „je Sample-Stunde".
> (6) Das Lastmenü erscheint, sobald **irgendeine** Range Daten hat (Spec §10) — leerer Default sondiert
> `13mo`. (7) Host-Provenienz je Metrik (`CpuValueSource`, `MemoryValueSource`, `LoadAverageValueSource`)
> mit deterministischer Tie-Regel. (8) DECISIONS-Absätze wandern in die Commits, die den jeweiligen
> Vertrag einführen (A1 legt den Eintrag an; A2, A3, B1, B2 schreiben ihn fort); der Fehlercode-Commit
> geht im A3-Routen-Commit auf. Geänderte Stellen sind mit „(R2)" markiert.

**Stand:** 2026-10-11, geplant gegen `origin/main` @ `97c286e7` (Merge von PR #386, Spec).
**Branch:** `docs/381-dashboard-plan` trägt nur diesen Plan; die Umsetzung läuft auf
`feat/381-admin-dashboard-a` (Schnitt A) und danach `feat/381-admin-dashboard-b` (Schnitt B, von A
abzweigend bzw. nach dessen Merge von `main`).

## 0. Ziel und Grundlage

Die bestehende Seite `/admin/monitoring` bekommt drei neue Bereiche in fester Reihenfolge
(Spec §10): **Last und Arbeit** (B), **Aktivitätsverlauf** (A), **Rate-Limit-Ausnahmen** (A); darunter
bleiben Worker-Health und Roster wie heute. A und B sind Implementierungsschnitte je ein PR,
keine Deploy-Stufen (`PendingMigrationGuard`, Spec §3). **Das Dashboard bringt keine eigene
Migration mit** (Abschnitt 3).

Grundlage, verbindlich: Spec (oben), `CLAUDE.md` (Regeln 1–22, Schichtentreue, Tests),
`web/.claude/CLAUDE.md` (Member-Reihenfolge, Signals, `liveEvents()`), `docs/UI-Designsprache.md`
(§2.1 Abschnittsstapel, §6.1 Skeleton, §6.2 EmptyState, §12 Audit-Harness), DECISIONS-Eintrag
2026-10-11 (#380) zu `SystemLoadSamples`/`SystemLoadHourly`.

## 1. Befund — Code-Stand, auf den der Plan sich stützt

Verifiziert am 2026-10-11 gegen `97c286e7`. Wo der Code von der Spec abweicht, steht es hier und in
Abschnitt 10.

### 1.1 Backend

- `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs:34-36`: Gruppe `/api/admin` =
  `MapGroup(...).RequireAuthorization().AddEndpointFilter<GlobalAdminAuthorizationFilter>()`; keine
  Rate-Limit-Policy. Routen `/health` (:38), `/rate-limits` (:141), `/roster` (:189, liefert
  `trackedChannelCount` und `ceilings`), `/live` (:306) u. a. 400er-Muster im Repo:
  `Results.BadRequest(new { errorCode = ApiErrorCodes.X })` (z. B.
  `Validation/ChannelNameValidationFilter.cs:28`); es gibt keinen Helper.
- `src/EmotePurge.Api/Auth/GlobalAdminAuthorizationFilter.cs`: kein Principal → 401, kein Admin
  (`IChannelAccessService.IsGlobalAdmin`) → 403.
- `src/EmotePurge.Api/Validation/ApiErrorCodes.cs`: `internal static class`, Konstanten in
  Blöcken mit Kommentar je Code; Spiegel `web/src/app/core/i18n/api-error.ts`
  (`KNOWN_API_ERROR_CODES`), Locale-Dateien **`web/public/i18n/de.json`** und **`en.json`**
  (Block `errors.api`, `api-error-locales.spec.ts` erzwingt Gleichheit beider Locales).
- **`ceilings` enthält nicht den Admission-Cap.** `AdminEndpoints.cs:482-486` liefert
  `twitchConcurrentChannelLimit` (100) und `twitchJoinBudgetChannels` (20) aus
  `src/EmotePurge.Api/Health/WorkerCapacity.cs`. Der von der Spec (§4.1) gemeinte Wert
  `Channels:MaxActiveChannels` (Default 80) steht in
  `src/EmotePurge.Infrastructure/Services/ChannelCapacityOptions.cs:23` und wird vom Roster-Endpunkt
  nicht ausgegeben → Abschnitt 10, Auflösung in A3.
- `src/EmotePurge.Core/Services/IRateLimitTelemetryReader.cs`: `ReadAsync` → `RateLimitTelemetrySnapshot`
  (`TelemetryAvailable`, `Policies`, `LastLocalRejection`, `Caches`, `Providers`); `Unavailable`
  als Antwort bei Redis-Ausfall, nie eine Exception. `src/EmotePurge.Infrastructure/Redis/RateLimitTelemetryStore.cs`
  (Singleton, `ServiceCollectionExtensions.cs:358-363`, implementiert Writer **und** Reader):
  Minuten-Buckets `ratelimit:telemetry:m:{unixSeconds/60}` als Hash, Felder
  `policy|{name}|accepted|rejected`, `provider|{provider}|{callSource}|requests|rate-limited`,
  `cache|{name}|hit|miss`; TTL 25 h; `SumBucketsAsync` liest die 1.440 Buckets per nicht
  awaitetem `HashGetAllAsync`-Burst (pipelined) und **summiert** — die Trendreihe braucht dieselben
  Buckets **unsummiert**. 5-s-Buckets (`:s:`) sind für den Trend irrelevant. `TimeProvider` ist per
  `TryAddSingleton(TimeProvider.System)` registriert.
- Kein `IMemoryCache` im Repo. Vorbild für keyed TTL-Cache mit Single-Flight:
  `src/EmotePurge.Infrastructure/SevenTv/SevenTvLeaderboardStore.cs` (`ConcurrentDictionary<string, Lazy<Task<…>>>`),
  `ForeignEmoteSetRequestCoalescer.cs`; serielle Gates über `SemaphoreSlim` in `ChannelSyncGate.cs`.
- SystemLoad: Entitäten `src/EmotePurge.Core/Entities/SystemLoadSample.cs`, `SystemLoadHourly.cs`,
  `SystemLoadSource.cs` (`Api`, `Worker`) — Felder exakt wie Spec §2.4. **DbSets heißen
  `SystemLoadSamples` und `SystemLoadHourlies`** (`AppDbContext.cs:29-30`), Tabelle `SystemLoadHourly`,
  PK `(Source, HourUtc)`, `Source` als `varchar(16)`-String; Indexe
  `IX_SystemLoadSamples_Source_SampledAtUtc` und `…_Source_ProcessInstanceId_SampledAtUtc`.
  `SystemLoadRollupMath.Calculate(samples, source, hourUtc, samplingIntervalSeconds)`
  (`Infrastructure/Services/SystemLoadRollupMath.cs`): Lücke `> 2,5 × Intervall` verwirft das
  Paar (:84), Paar nur gleiche `ProcessInstanceId`, Zähler monoton, `elapsed > 0`; P95 Nearest
  Rank `sorted[ceil(n·0,95)−1]` (:221-237). `SystemLoadRollupService.LoadCandidateSamplesAsync`
  lädt je `(Source, ProcessInstanceId)` den Vorgänger per `LATERAL … ORDER BY SampledAtUtc DESC LIMIT 1`.
  `SystemLoadOptions` (`Infrastructure/Services/SystemLoadOptions.cs`): `IntervalSeconds` 1–3.600,
  Default 30. `RetentionPolicy.SystemLoadSample` = 7 Tage, `SystemLoadHourlyMonths` = 13.
- `GET /api/health` (`Endpoints/WorkerHealthEndpoints.cs:57-66`): 200 nur bei Snapshot unter
  `worker:health:twitch` (TTL 60 s, Worker schreibt alle 20 s) mit abgeleitetem Status
  `connected`; sonst 503; Policy `PublicHealth` = Fixed Window 30/min je IP
  (`Api/RateLimiting/RateLimitingOptions.cs:47`).
- `src/EmotePurge.Core/Messaging/LiveEvents.cs:60-65`: `AdminTypes = {worker.health, worker.roster,
  channel.synced, live.changed}`, `ChannelTypes` enthält `usage.flushed`. Bestehender
  Vertragstest `tests/EmotePurge.Infrastructure.Tests/Unit/LiveEventTests.cs:76-88`
  (`TypeSets_CoverTheEventsTheirStreamsAnnounce`) — **nicht** in `Api.Tests`, wie die Spec §11
  schreibt.
- Tests: `tests/EmotePurge.Api.Tests/ApiFactory.cs` substituiert Handler-Services per NSubstitute
  (`services.AddScoped(_ => …)`/`AddSingleton`), `TestAuthHandler` mit Headern
  `X-Test-UserId`/`X-Test-Login`; Admin per `ChannelAccess.IsGlobalAdmin(...).Returns(true)`;
  Muster für einen Admin-Endpunkt-Test mit eigenem Reader: `AdminRateLimitsEndpointTests.cs`
  (`SendAsAdminAsync`, `WithWebHostBuilder(... AddSingleton(reader))`). Infrastructure:
  `Fixtures/PostgresFixture.cs` (`[Collection("Postgres")]`, `CreateDbContext()`,
  `CreateDbContext(databaseName)`), `Fixtures/RedisFixture.cs` (`[Collection("Redis")]`,
  `Connection`); Muster mit eigener DB + `TRUNCATE`: `Integration/SystemLoadPersistenceTests.cs`,
  `SystemLoadRollupBoundedQueryTests.cs`; Redis-Muster mit steuerbarer Uhr:
  `Integration/RateLimitTelemetryStoreTests.cs` (`NewClock()`, `NewStore(clock)`, `NewName(...)`).
  Strukturtest-Muster: `Unit/CoreAssemblyReferenceTests.cs`.

### 1.2 Frontend

- `web/src/app/features/admin/admin-monitoring-page.ts` (Inline-Template, Klasse ab :609):
  Header mit Live-Marker und Refresh; Health-Fehlerbanner; Skeleton (`app-skeleton-sections`
  count 3) → Abschnitte Twitch IRC, `<app-admin-roster-card />`, 7TV EventAPI, Batch-Flush; dann
  Rate-Limits (Fehlerbanner, Skeleton count 1, Abschnitt `admin.rateLimits.*` mit Policies-`ul`,
  letzter Ablehnung, Caches, Providern, Hinweisen). `healthResource` lädt per
  `liveEvents(ADMIN_LIVE_URL, [workerHealth])` nach; `rateLimitsResource` per
  `interval(RATE_LIMITS_POLL_INTERVAL_MS = 30_000)` (:65, :769). **Kein `visibilitychange`** auf der
  Seite (nur in `core/live/live-update.service.ts:334` für den Stream). Roster hat eigene
  `rxResource` und eigenes `worker.roster`-Abo in `admin-roster-card.ts:221,341`; die Seite greift
  per `viewChild(AdminRosterCard)` nur auf `reload()` zu.
- `web/src/app/core/admin/admin.service.ts` (`getHealth`, `getRateLimits`, `getRoster`, …),
  `admin.model.ts` (`RateLimitTelemetrySnapshot`, `RateLimitPolicySnapshot`, `AdminRoster` mit
  `ceilings: RosterCeilings { twitchConcurrentChannelLimit; twitchJoinBudgetChannels }`),
  `admin.service.spec.ts` (22 Fälle, `HttpTestingController`).
- **Keine Chart-Bibliothek** (`web/package.json`: nur Angular, CDK, Transloco, rxjs). Einziges
  Kurven-Primitiv: `web/src/app/shared/emotes/usage-sparkline.ts` (100×40-viewBox-Polyline ohne
  Achsen, Lücken, Tooltip) + `usage-series.ts` (`toPolylinePoints`, `fillDailySeries`, …).
  `docs/UI-Designsprache.md` hat **keinen Chart-Abschnitt**.
- Bausteine: `shared/ui/segmented-control.ts` (`app-segmented-control`, `options: {value, labelKey}[]`,
  `value = model.required<string>()`, `ariaLabel`, Radiogroup), `empty-state.ts` (`title`,
  `description`, CTA-Projektion), `notice-banner.ts` (`variant: info|warning|error`),
  `dialog-shell.ts` + `dialog.ts` (`openAppDialog`), `skeleton-sections.ts`, `health-marker.ts`,
  `status-badge.ts`, `popover.ts`. Kein Tooltip-Primitiv.
- Routing inline in `web/src/app/app.routes.ts:59-80` (`admin` → `AdminLayout`, `adminGuard`,
  Kind `monitoring`).
- E2E: **kein** Spec für `/admin/monitoring`; Admin-Mocks in `web/e2e/support/mocks.ts`
  (`mockAdminHealth` :113, `mockAdminRoster` :169; **kein** Mock für `/api/admin/rate-limits`).
  `page.clock` wird in `footer-placement.e2e.spec.ts`, `emote-tags.e2e.spec.ts` benutzt.
- Audit-Harness: `web/e2e/audit/ui-audit.audit.ts`, Szenarien `admin-monitoring` (:714),
  `admin-monitoring-degraded` (:723), `admin-monitoring-near-capacity` (:747) mit `adminShell(page)`
  (:266); Aufruf `cd web && npx playwright test --config=playwright.audit.config.ts`
  (startet `ng serve` auf `:4300` selbst, Ausgabe `web/.audit-out/`).
- Coverage: `scripts/coverage-local.mjs` (`--skip-tests`, `--frontend-only`, `--backend-only`);
  `web/e2e/**`, `scripts/**`, `**/Migrations/**` sind coverage-ausgeschlossen.

## 2. Schnitt-Übersicht

| Task | Titel | Schicht | Abhängigkeit | Modell | Commit-Typ |
|---|---|---|---|---|---|
| A0 | Branch, Baseline, Ausgangslage | — | — | `haiku` | kein Commit |
| A1 | Core-Verträge + `AdminDashboardQueryService` (Aktivität) + Integrationstests, **DECISIONS-Eintrag anlegen** (R2) | Core, Infrastructure, docs | A0 | `sonnet` | `feat(infrastructure)` |
| A2 | Rate-Limit-Trendreihe im `RateLimitTelemetryStore` + Redis-Tests, **DECISIONS-Absatz** (R2) | Core, Infrastructure, docs | A0 (parallel zu A1) | `sonnet` | `feat(infrastructure)` |
| A3 | Api: drei Routen **inkl. Fehlercode-Kette im selben Commit** (R2), Cache/Single-Flight, Roster-`maxActiveChannels`, Contract-Tests, **DECISIONS-Absätze** | Api, Infrastructure, web (Locale), docs | A1, A2 | `sonnet` | `feat(api)` |
| A4 | Web-Grundlagen: Modelle, `AdminService`, Chart-Primitiv `shared/charts/`, Sichtbarkeits-Signal, Specs | web/core, web/shared | A3 (Vertrag reicht; parallel zu A3 möglich) | `sonnet` | `feat(web)` |
| A5 | Web: Aktivitätsverlauf, Rate-Limit-Ausnahmen exception-first, Details-Dialog, 60-s-Poll, i18n | web/features | A4 | `sonnet` | `feat(web)` |
| A6 | E2E `/admin/monitoring`, Mocks, Audit-Szenarien, Audit-Lauf | web/e2e | A5 | `sonnet` | `test(web)` |
| A7 | Gates A, Coverage, Live-Verifikation, Doku-Zeile, Codex-Zweitmeinung, PR | alle | A6 | `opus` | `docs`/`chore` |
| B0 | Branch B, Drift-Check gegen `main` | — | A gemergt | `haiku` | kein Commit |
| B1 | Load-Query-Service (Roh- und Hourly-Pfad, Bucket-Leiter) + Integrationstests + Strukturtest, **DECISIONS-Absätze zu Bucketing, Lückenregel, Host-Zusammenführung** (R2) | Core, Infrastructure, docs | B0 | `opus` | `feat(infrastructure)` |
| B2 | Api `/dashboard/load`, Load-Cache mit Single-Flight + seriellem Gate, 503/Retry-After, Contract-Tests, **DECISIONS-Absätze zu Route, Budget, Cache** | Api, Infrastructure, docs | B1 | `sonnet` | `feat(api)` |
| B3 | Web: Lastbereich (11 Small Multiples), Lastmenü, Poll/`visibilitychange`, Veraltet-Markierung, Rate-Limit-Reihe an Last-Achse | web | B2 (Vertrag), A4 | `sonnet` | `feat(web)` |
| B4 | E2E B, Audit-Szenarien B, Render-Messung bei 400 Buckets | web/e2e | B3 | `sonnet` | `test(web)` |
| B5 | Response-Performance-Gate (xUnit, opt-in, 24 h bei 1 s) | tests | B2 | `sonnet` | `test(infrastructure)` |
| B6 | Parallelitäts-Gate unter `--cpus=1 --memory=512m` (Skripte + Protokoll) | scripts, docs | B2, B5 | `opus` | `chore(scripts)` + `docs` |
| B7 | Gates B, Coverage, Live-Verifikation, Doku, Codex-Zweitmeinung, PR | alle | B4–B6 | `opus` | `docs`/`chore` |

Reihenfolge: A0 → (A1 ∥ A2) → A3 → A4 → A5 → A6 → A7 → Merge A → B0 → B1 → B2 → (B3 ∥ B5) → B4 →
B6 → B7. A4 darf parallel zu A3 starten, sobald der Vertrag (Abschnitt 4) steht; der Orchestrator
löst dann den Mini-Konflikt in `de.json`/`en.json` selbst auf.

## 3. Abhängigkeiten und Randbedingungen

- **Keine Migration.** Weder A noch B ändern Schema oder Indexe (Spec §3, §7: A1/A3/B3 bestanden,
  A2 gestrichen, B1 durch Hourly-Pfad abgedeckt). Damit **keine Abstimmung mit der Slot-Session
  (#365, #371 ff.) nötig** und kein Prod-Migrationsschritt. `dotnet ef migrations list` muss nach A
  und B dieselbe letzte Migration `20261011113207_AddSystemLoadSampling` zeigen — A7/B7 prüfen das.
- **Slot-Epic #365** baut `UsageStats` um; #381 liest `UsageStats` nicht (Betreiberentscheid). B1
  hält das per Strukturtest fest. Berührungspunkte sind nur `AdminEndpoints.cs`,
  `ServiceCollectionExtensions.cs`, `de.json`/`en.json`, `ApiErrorCodes.cs`/`api-error.ts` und
  `DECISIONS.md` — gewöhnliche Merge-Konflikte, wer als Zweiter mergt, rebased.
- **#385-Nachläufer sind nicht Teil dieses Plans:** die langsame Kandidatenabfrage des Rollups bei
  überzogener Messmenge und die zwei Rollup-Grenzen nach Kadenzwechsel (Spec §5.1, §12). B setzt den
  Rollup voraus, wie er auf `main` ist; der Rohpfad übernimmt dessen Lückenregel unverändert.
- **Zählbeginn Prod 2026-10-11, 18:47 CEST.** B zeigt bis zur Vollständigkeit je Range den
  Datenstand (`dataThroughUtc`), keine Nullreihen. Die Live-Verifikation von B läuft lokal gegen
  eigene Samples (Api und Worker laufen lokal mit 30 s; für dichte Daten `SystemLoad__IntervalSeconds=1`
  in der Shell des `dotnet run`).
- **`/api/admin` bekommt keine Rate-Limit-Policy** (Spec §8); die neuen Routen begrenzen Range,
  Bucketzahl und Antwortgröße.
- Dev-Stack wie in CLAUDE.md: `docker compose up -d postgres redis`, `dotnet run --project src/EmotePurge.Api`
  (`:5151`), `dotnet run --project src/EmotePurge.Worker`, `npm --prefix web start` (`:4200`).
  Login als Admin: eigene Twitch-ID in `Auth:AdminTwitchUserIds` per `dotnet user-secrets` im
  Api-Projekt (Regel 17), nie in `appsettings*.json`.

## 4. Verträge Schnitt A

### 4.1 Core: `IAdminDashboardQueryService` (neu, `src/EmotePurge.Core/Services/IAdminDashboardQueryService.cs`)

BCL-only, keine EF-Typen. Scoped-Implementierung `src/EmotePurge.Infrastructure/Services/AdminDashboardQueryService.cs`
(`AppDbContext`), registriert in `ServiceCollectionExtensions.cs` neben `IAdminChannelQueryService` (:99).

| Mitglied | Form |
|---|---|
| `AdminDashboardActivityRange` | Enum `Days30`, `Days90`, `Months13`; statischer Parser von `"30d"`/`"90d"`/`"13mo"` (ordinal, exakt) → `null` bei allem anderen. Wire-Namen bleiben die drei Strings. |
| `GetActivityAsync(AdminDashboardActivityRange range, DateTime nowUtc, CancellationToken)` | liefert `AdminDashboardActivity`. `nowUtc` kommt vom Aufrufer (testbar, `DateTimeKind.Utc` erzwingen). |
| `AdminDashboardActivity` (record) | `Range` (Wire-String), `FromDate` (`DateOnly`), `ToDate` (`DateOnly`, laufender UTC-Tag), `TodayIsPartial` (immer `true`), `GeneratedAtUtc`, `Days: IReadOnlyList<AdminDashboardActivityDay>` — **ein Eintrag je UTC-Tag im Fenster**, lückenlos, aufsteigend. |
| `AdminDashboardActivityDay` (record) | `Date` (`DateOnly`), `LiveChannels: int?` — Anzahl `ChannelLiveDays`-Zeilen mit `LiveMinutes > 0` an diesem `Date`; **`null`, wenn an diesem Tag gar keine `ChannelLiveDays`-Zeile existiert** (keine Beobachtung, nicht null Channels). *Annahme A-1*, s. Abschnitt 9. |

Fensterregel (Spec §6): `ToDate = nowUtc.Date`; `30d`/`90d`: `FromDate = ToDate − (N−1)` Tage, also N
Kalendertage einschließlich heute; `13mo`: `FromDate` = erster Tag des Monats, der 13 Monate vor dem
Monat von `ToDate` liegt (Beispiel: heute 2026-10-11 → 2025-09-01). DB-Filter `Date >= FromDate AND
Date <= ToDate`, `GROUP BY Date` auf der Tabelle selbst — **kein Join, keine Navigation** (Regel 10,
Spec §11 A). Für `LiveChannels = null` braucht die Abfrage zusätzlich, welche Tage überhaupt Zeilen
haben: eine Gruppierung ohne den `LiveMinutes`-Filter, die `COUNT(*) FILTER (WHERE LiveMinutes > 0)`
liefert, deckt beides in einem Scan (EF: `GroupBy(Date).Select(g => new { g.Key, Total = g.Count(),
Live = g.Count(x => x.LiveMinutes > 0) })`; wenn Npgsql das nicht übersetzt, zwei Gruppierungen).
Plan-Vergleich laut Spec §11 A: Seq Scan auf `ChannelLiveDays`, kein Join — in A1 per
`EXPLAIN` gegen den Testcontainer protokollieren (Testausgabe, kein Gate).

### 4.2 Core: Trendreihe auf `IRateLimitTelemetryReader`

Neue Methode `ReadTrendAsync(CancellationToken)` → `RateLimitTelemetryTrend` (record, Core):

| Feld | Bedeutung |
|---|---|
| `TelemetryAvailable` | `false` bei Redis-Fehler; dann alle Listen leer (Muster `RateLimitTelemetrySnapshot.Unavailable`). |
| `FromUtc`, `ToUtc` | `ToUtc` = Beginn der aktuellen Minute (UTC), `FromUtc = ToUtc − 1.439 min`; 1.440 Buckets einschließlich der laufenden Minute. |
| `MinuteStartsUtc: IReadOnlyList<DateTime>` | 1.440 Einträge aufsteigend. |
| `LocalRejections: IReadOnlyList<long>` | je Minute Summe aller `policy|*|rejected`-Felder. |
| `Providers: IReadOnlyList<RateLimitProviderTrend>` | je `(ProviderName, CallSource)`, die im Fenster mindestens ein `requests`- oder `rate-limited`-Feld hatten: `RateLimited: IReadOnlyList<long>` (1.440). |

Lesepfad im Store: dieselben 1.440 `CoarseBucketPrefix`-Keys wie `SumBucketsAsync`, derselbe
nicht-awaitete `HashGetAllAsync`-Burst, aber **je Bucket** die Felder behalten (eine neue private
Methode neben `SumBucketsAsync`; `ReadAsync` bleibt unverändert). Ein fehlender Key ist Minute mit
0 — das ist korrekt, weil jeder Event seinen Bucket anlegt; 0 heißt „keine Ablehnung", nicht
„unbekannt" (anders als bei SystemLoad). Nie pro Chartpunkt ein eigener Roundtrip (Spec §8).

### 4.3 Api — Routen in der `/api/admin`-Gruppe (`AdminEndpoints.cs`)

| Route | Parameter | Antwort (camelCase-JSON) | Fehler |
|---|---|---|---|
| `GET /api/admin/dashboard/activity` | `range` Query, Default `30d` (fehlend oder leer → Default; alles andere exakt) | `{ range, fromDate, toDate, todayIsPartial, generatedAtUtc, days: [{ date, liveChannels }] }` — `date` als `YYYY-MM-DD`, `liveChannels` Zahl oder `null` | `400 { errorCode: "invalid_dashboard_range" }`; 401/403 per Gruppenfilter; 500 `unexpected_error` per globalem Handler |
| `GET /api/admin/rate-limits/trend` | keine | `{ telemetryAvailable, fromUtc, toUtc, minuteStartsUtc: [...], localRejections: [...], providers: [{ providerName, callSource, rateLimited: [...] }] }` | nur 401/403; Redis-Ausfall = `200` mit `telemetryAvailable=false` und leeren Listen |
| `GET /api/admin/roster` (bestehend, **additiv**) | — | `ceilings` bekommt zusätzlich `maxActiveChannels` (Wert `ChannelCapacityOptions.MaxActiveChannels`; die Klasse ist als konkreter Singleton registriert, `ServiceCollectionExtensions.cs:72-75`, und wird so in den Handler injiziert — kein `IOptions<T>`) | unverändert |

Neuer Code `ApiErrorCodes.InvalidDashboardRange = "invalid_dashboard_range"` mit Kommentar
(400 von beiden Dashboard-Routen; `range` fehlt nicht, sondern ist unbekannt) → `api-error.ts`
→ `errors.api.invalid_dashboard_range` in `de.json` **und** `en.json`. **Im selben Task und Commit**
(Regel 7); in B wird der Code nur wiederverwendet.

Caches (Spec §8), alle pro Api-Instanz, Infrastructure, Singleton, mit `TimeProvider`:

- `IAdminDashboardReader` (Core, neu) als endpunktseitige Fassade mit `GetActivityAsync(range, ct)`,
  `GetRateLimitSnapshotAsync(ct)`, `GetRateLimitTrendAsync(ct)`; in B kommt `GetLoadAsync` dazu
  (4.3 in Abschnitt 6). Implementierung `AdminDashboardReader` (Infrastructure, Singleton mit
  `IServiceScopeFactory` für den scoped Query-Service): keyed TTL-Cache mit Single-Flight nach dem
  Muster `SevenTvLeaderboardStore` (`ConcurrentDictionary<string, Lazy<Task<T>>>` + Ablaufzeit).
  TTLs: Aktivität 60 s je Range-Key, Rate-Limit-Snapshot 30 s, Trend 30 s. Ein fehlgeschlagener
  Aufbau wird **nicht** gecacht (Lazy entfernen, Exception weiterreichen → 500 bzw. bei Redis die
  `Unavailable`-Antwort, die der Store ohnehin liefert und die wie jede Antwort 30 s gilt).
- `GET /api/admin/rate-limits` wechselt auf dieselbe Fassade (Snapshot-Cache 30 s); der bestehende
  Antwortvertrag bleibt byte-gleich — `AdminRateLimitsEndpointTests` müssen unverändert grün bleiben,
  abgesehen davon, dass ihr Reader jetzt hinter dem Cache sitzt (Test-Factory substituiert die
  Fassade **oder** den Reader; Entscheidung in A3: die Fassade wird in `ApiFactory` **nicht**
  pauschal substituiert, sondern bleibt echt und bekommt substituierte Query-/Reader-Services,
  damit der Cache-Pfad im Contract-Test mitläuft; `IAdminDashboardQueryService` wird in
  `ApiFactory` substituiert wie `IAdminUserQueryService`).

### 4.4 Web — Modelle und Service (`web/src/app/core/admin/`)

`admin.model.ts`: `AdminDashboardActivityRange = '30d' | '90d' | '13mo'`,
`AdminDashboardActivity { range; fromDate; toDate; todayIsPartial; generatedAtUtc; days: { date; liveChannels: number | null }[] }`,
`RateLimitTelemetryTrend { telemetryAvailable; fromUtc; toUtc; minuteStartsUtc: string[]; localRejections: number[]; providers: { providerName; callSource; rateLimited: number[] }[] }`,
`RosterCeilings` + `maxActiveChannels: number`.
`admin.service.ts`: `getDashboardActivity(range)`, `getRateLimitTrend()`; Spec-Fälle in
`admin.service.spec.ts` (Pfad + Query, degradierte Trend-Antwort).

Neue Utilities (alle mit Spec):

- `web/src/app/core/page-visibility/page-visibility.ts`: `pageVisible(): Signal<boolean>` aus
  `document.visibilityState` + `visibilitychange` (per `DOCUMENT`-Token, `DestroyRef`); dazu
  `pollWhileVisible(intervalMs, visible: Signal<boolean>): Observable<void>` in
  `core/live/` neben `live-reload.ts` (Timer läuft nur bei sichtbarer Seite, feuert beim Wechsel
  verborgen → sichtbar **einmal sofort**). Wird in A5 für Aktivität/Rate-Limits, in B3 für Last
  benutzt (Spec §8 `visibilitychange`).
- `web/src/app/shared/charts/chart-series.ts`: reine Funktionen — `toLinePath(values: (number|null)[], …)`
  (Pfad mit **Unterbrechung an `null`**, kein Verbinden über Lücken), `niceTicks(min, max, count)`,
  `formatUnit(value, unit, locale)` (Kerne 2 Dezimalen, Byte → GiB/MiB, Prozent, Tasks, Zähler),
  `summarizeSeries(values)` (min/max/letzter Wert/Anzahl bekannter Punkte für die Textalternative).
- `web/src/app/shared/charts/line-chart.ts` (`app-line-chart`): inline-SVG-Small-Multiple mit
  Inputs `series: { id; labelKey; labelParams?; values: (number|null)[]; emphasis?: 'line'|'marks' }[]`
  — **beliebig viele Reihen gleicher Einheit** (R2): die Lastdiagramme nutzen ein bis zwei Linien plus
  „Max" als Markierungsreihe, die Rate-Limit-Reihe lokale Ablehnungen **plus eine Linie je ablehnender
  Provider/Call-Source-Dimension** (bis zu `RateLimitCallSources`-Zahl, heute sechs); ab drei Reihen
  rendert das Primitiv eine Textlegende (Reihenname + letzter Wert) unter dem Chart, Farben aus einer
  festen Token-Leiter (`accent-fg`, `fg-muted`, `danger-fg`, `warning-fg`, …), nie Palette —,
  `xStartsUtc: string[]`, `bucketSeconds`,
  `unit`, `title`/`ariaLabel`, optional `partialLastBucket`. Rendert y-Achse mit 3–4 Ticks und
  sichtbarer Einheit, x-Achse mit UTC-Beschriftung (Datum oder Uhrzeit je Bucketbreite), Lücken als
  Linienbruch, eine `role="img"` + Textalternative aus `summarizeSeries` (Spec §10 Barrierefreiheit).
  Tooltip: **kein Hover-Tooltip-Primitiv**, sondern ein fokussierbarer, tastaturbedienbarer
  Lesecursor (Pfeiltasten, `aria-live="polite"`-Textzeile unter dem Chart mit Bucketzeit, Werten
  und — in B — Provenienz/`SampleCount`); Maus setzt denselben Cursor. *Annahme A-2.* Farben nur
  Token (`text-accent-fg`, `text-fg-muted`, Ablehnungen `danger`-Token; Lastkurven neutral, Spec §10).
  Kein Chart-Grid-Rahmen (keine Karte, §2.1).

### 4.5 Web — Seite (`admin-monitoring-page.ts`)

- Neue Bereiche zwischen Header und Twitch-IRC-Abschnitt, in dieser Reihenfolge: *(B-Platzhalter
  fehlt in A schlicht)*, **Aktivitätsverlauf**, **Rate-Limit-Ausnahmen**. Die bestehenden
  Health-Abschnitte, Roster-Card und die bestehende Rate-Limit-Sektion wandern: Policies/Caches/
  Provider/letzte Ablehnung werden zum Inhalt des **Details-Dialogs** (`openAppDialog` +
  `app-dialog-shell`), die Seite zeigt nur noch die Ausnahmen-Zusammenfassung plus die 24-h-Reihe.
- Aktivitätsverlauf: Abschnittstitel mit Kontextwert „`trackedChannelCount` / `maxActiveChannels`
  (Admission-Grenze für normale Nutzer)" aus dem Roster der bestehenden Card — `AdminRosterCard`
  bekommt dafür ein **öffentliches** `roster`-Signal (heute `protected`), die Seite liest es über
  das vorhandene `viewChild`; kein zweiter `/roster`-Aufruf (*Annahme A-3*). `app-segmented-control`
  mit `30d`/`90d`/`13mo` (`admin.dashboard.activity.range.*`), `rxResource` mit `params: () => range()`,
  60-s-Poll über `pollWhileVisible`, `reload()` des Headers lädt auch diese Resource. Chart: eine
  Linie „Beobachtete Live-Channels/Tag", letzter Punkt als unvollständig markiert, Hilfetext mit
  Poller-Semantik und Purge-Verzerrung (Spec §2.2). Beim Reload bleiben die letzten Werte stehen
  (Skeleton nur beim ersten Laden; Memory „Keine Layout-Sprünge").
- Rate-Limit-Ausnahmen: Liste nur der Policies mit `rejectedLast24Hours > 0`, absteigend nach
  `rejectedLast24Hours`, davor Policies mit `rejectedLastMinute > 0` (akut) — Sortierung als
  `computed()`; je Zeile Anteil `rejected/(accepted+rejected)` in Prozent (bei 0 Entscheidungen
  „keine Requests", keine Division) plus absolute Zähler; Provider mit `rateLimitedLast24Hours > 0`
  ebenso. Leere Liste → `app-empty-state` „Keine Ablehnungen in 24 h". Darunter das 24-h-Chart
  (feste UTC-Achse; **Reihen: 1 + n** — lokale Ablehnungen aggregiert plus je eine getrennte Reihe
  für jede Provider/Call-Source-Dimension mit ≥ 1 Ablehnung im Fenster (R2, Spec §4.2 „Provider
  bleiben als getrennte Reihen"); ohne ablehnende Provider nur die lokale Reihe plus Hinweiszeile;
  Spec-Fall in A5: drei Dimensionen → vier Reihen, jede getrennt adressierbar).
  `telemetryAvailable=false` → `warning`-Banner,
  Konfiguration im Dialog bleibt lesbar. Poll 60 s (ersetzt 30 s; `RATE_LIMITS_POLL_INTERVAL_MS`
  wird zu 60_000 oder entfällt zugunsten `pollWhileVisible`); der manuelle Refresh lädt beide
  Ressourcen.
- Transloco: neuer Block `admin.dashboard.*` (Unterblöcke `activity`, `rateLimits`, `chart`), de/en
  vollständig; keine Zahl im Wortlaut hartkodiert.

## 5. Tasks Schnitt A

Gates je Task: nur die Suiten, die der Task berührt (Backend: `dotnet build EmotePurge.slnx` mit
0 Warnungen, `dotnet test EmotePurge.slnx` — Docker für Testcontainers —, `dotnet format
EmotePurge.slnx --verify-no-changes`; Frontend: `npm --prefix web test -- --watch=false`,
`npm --prefix web run lint`, `npm --prefix web run format:check`). Jeder Task committet selbst
(Conventional Commits, nur die eigenen Dateien exakt stagen — parallele Sessions teilen den Index),
pusht nicht; A7 pusht und öffnet den PR.

### A0 — Branch, Baseline, Ausgangslage (`haiku`)

- [ ] `git switch -c feat/381-admin-dashboard-a origin/main` (frisch von `main`, nicht vom Plan-Branch).
- [ ] `docker info`; `ss -ltn | grep -E ':5151|:4200|:4300'` notieren; `ls web/node_modules/.bin/ng`
      (sonst `npm --prefix web ci`).
- [ ] Baseline: `dotnet build EmotePurge.slnx` (0 Warnungen), `npm --prefix web test -- --watch=false`
      (Zahl melden), `dotnet ef migrations list --project src/EmotePurge.Infrastructure
      --startup-project src/EmotePurge.Api` (letzte = `20261011113207_AddSystemLoadSampling`, nichts Pending).
- [ ] Kein Commit. **Abnahme:** Zahlen gemeldet, Branch steht.

### A1 — Core-Verträge + `AdminDashboardQueryService` (Aktivität) (`sonnet`)

**Dateien:** neu `src/EmotePurge.Core/Services/IAdminDashboardQueryService.cs` (Interface, Enum +
Parser, Records aus 4.1 — in B kommen Load-Mitglieder dazu); neu
`src/EmotePurge.Infrastructure/Services/AdminDashboardQueryService.cs`; `ServiceCollectionExtensions.cs`
(Scoped neben `:99`); neu `tests/EmotePurge.Infrastructure.Tests/Integration/AdminDashboardQueryServiceTests.cs`
(`[Collection("Postgres")]`, eigene DB + `TRUNCATE "ChannelLiveDays", "Channels"` wie
`SystemLoadPersistenceTests`); neu `tests/EmotePurge.Infrastructure.Tests/Unit/AdminDashboardActivityRangeTests.cs`
(Parser, Fensterregel ohne DB).

**Tests (Spec §11 A):**
1. Fenstergrenzen: `30d` bei `nowUtc` 2026-10-11T23:59Z → `FromDate` 2026-09-12, `ToDate` 2026-10-11,
   31 ≠ … genau 30 Einträge; `90d` 90 Einträge; `13mo` beginnt am Monatsersten (2025-09-01),
   Eintragszahl = Kalendertage bis heute; Jahreswechsel-Fall (`nowUtc` 2027-01-03 → `13mo` ab 2025-12-01).
2. Laufender Tag enthalten und `TodayIsPartial = true`; Zeile mit `Date = ToDate` zählt.
3. `LiveMinutes > 0`-Filter: Zeile mit 0 Minuten zählt nicht, Tag mit nur solchen Zeilen → `LiveChannels = 0`,
   Tag ohne Zeile → `null`.
4. Zwei Channels am selben Tag → 2; derselbe Channel an zwei Tagen → je 1.
5. Zeilen außerhalb des Fensters (vor `FromDate`, mehrjährige aktive Historie, > 400 Tage) ändern nichts;
   gepurgter Channel = fehlende Zeilen = Lücke, keine Interpolation.
6. Parser: `"30d"`,`"90d"`,`"13mo"` → Enum; `"30D"`, `" 30d"`, `"7d"`, `""`, `null` → `null`.
7. `EXPLAIN`-Protokoll (kein Assert): Seq Scan, kein Join; Ausgabe im Testlog.

**DECISIONS (R2, Regel 3 — im selben Commit wie der Vertrag):** A1 legt den Eintrag
„`2026-10-xx` — Admin dashboard: activity series, rate-limit trend and server-bucketed load ranges
(#381)" in `docs/DECISIONS.md` oben an (englisch, `**Betrifft:**`-Zeile) mit den Absätzen, die
dieser Commit begründet: Aktivitätsvertrag (`30d|90d|13mo`, UTC-Kalendertage einschließlich des
partiellen Tages, `13mo` ab Monatserstem, `null` = keine Beobachtung, kein Join), und dass das
Dashboard `UsageStats` nicht liest (Betreiberentscheid 2026-10-11). A2, A3, B1 und B2 schreiben
**denselben Eintrag** in ihren Commits fort; die `Betrifft`-Zeile wächst mit.

**Schritte:** Vertrag → Implementierung (Gruppierung auf der Tabelle, keine Navigation) → Tests →
DECISIONS-Eintrag → Gates Backend → Commit `feat(infrastructure): add the admin dashboard activity
query service` (inkl. DECISIONS). **Abnahme:** Tests 1–7 grün, Suite grün, 0 Warnungen, Eintrag vorhanden.

### A2 — Rate-Limit-Trendreihe im `RateLimitTelemetryStore` (`sonnet`)

**Dateien:** `src/EmotePurge.Core/Services/IRateLimitTelemetryReader.cs` (Methode + Records aus 4.2),
`src/EmotePurge.Infrastructure/Redis/RateLimitTelemetryStore.cs` (neue private Lesemethode je Bucket,
`ReadTrendAsync`, Fail-open wie `ReadAsync`), `tests/EmotePurge.Infrastructure.Tests/Integration/RateLimitTelemetryStoreTests.cs`
(erweitern, Muster `NewClock()`/`NewName()`), `tests/EmotePurge.Infrastructure.Tests/Fakes/RecordingRateLimitTelemetry.cs`
(falls es den Reader fakt: Methode ergänzen).

**Tests (Spec §11 A):**
1. Ablehnung zum Zeitpunkt T landet im Bucket der Minute von T; Uhr 61 s weiter → nächster Bucket;
   `MinuteStartsUtc` hat 1.440 Einträge, letzter = aktuelle Minute, aufsteigend.
2. Zwei Policies je eine Ablehnung in derselben Minute → `LocalRejections` = 2 dort; akzeptierte
   Entscheidungen zählen nicht.
3. Provider-429 (Status 429) → `Providers[(twitch, twitch-helix)].RateLimited` = 1; Status 500 → kein Eintrag
   in `RateLimited`, Dimension aber vorhanden (hatte `requests`); lokale Ablehnung erscheint **nicht** bei Providern.
4. Ereignis 24 h + 1 min alt → nicht mehr im Fenster (TTL 25 h: Key existiert noch, Fenster schneidet).
5. Verbindung tot (eigener Multiplexer auf Nonsens-Port, Muster bestehender `*FailureTests`) →
   `TelemetryAvailable=false`, leere Listen, keine Exception.
6. `ReadAsync` unverändert (bestehende Tests bleiben).

**DECISIONS (R2):** Absatz im A1-Eintrag: Trend aus den bestehenden Redis-Minuten-Buckets ohne neue
Keys oder Felder, 1.440 Minuten, 0 = keine Ablehnung (anders als SystemLoad-`null`), fail-open wie der
Snapshot. Läuft A2 parallel zu A1, löst der Orchestrator den Konflikt im Eintrag auf.

**Commit:** `feat(infrastructure): add the 24-hour rate-limit trend read path` (inkl. DECISIONS-Absatz).
**Abnahme:** Tests grün.

### A3 — Api: Routen, Fehlercode-Kette, Cache, Roster-Feld, Contract-Tests, DECISIONS (`sonnet`)

**Dateien:** `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs` (zwei neue `MapGet` in der Gruppe,
`/roster`-`ceilings` + `maxActiveChannels`, `/rate-limits` auf die Fassade); `src/EmotePurge.Api/Validation/ApiErrorCodes.cs`;
`web/src/app/core/i18n/api-error.ts`; `web/public/i18n/de.json`, `en.json` (`errors.api.invalid_dashboard_range`);
neu `src/EmotePurge.Core/Services/IAdminDashboardReader.cs`, `src/EmotePurge.Infrastructure/Services/AdminDashboardReader.cs`
(4.3 Caches), `ServiceCollectionExtensions.cs` (Singleton); `tests/EmotePurge.Api.Tests/ApiFactory.cs`
(Substitut `IAdminDashboardQueryService`; `IRateLimitTelemetryReader` bleibt wie heute je Test
substituierbar); neu `tests/EmotePurge.Api.Tests/AdminDashboardActivityEndpointTests.cs`,
`AdminRateLimitsTrendEndpointTests.cs`; `AuthFilterMatrixTests.cs` (die zwei Routen in die
Admin-Zeilen der 401/403-Matrix); `tests/EmotePurge.Infrastructure.Tests/Unit/LiveEventTests.cs`
(`DoesNotContain(UsageFlushed, AdminTypes)` ergänzen — *Annahme A-4*: der Stream-Vertragstest bleibt
dort, wo er heute liegt); neu `tests/EmotePurge.Infrastructure.Tests/Unit/AdminDashboardReaderTests.cs`
(Cache ohne Container: TTL, Single-Flight, Fehler nicht gecacht — mit `FakeTimeProvider`, das `SystemLoadSamplerTests` bereits nutzt); `docs/DECISIONS.md`.

**Contract-Tests (Spec §11 A):**
1. `GET /dashboard/activity` ohne `range` → 200, `range = "30d"`, Felder laut 4.3, `days.length` = 30,
   `date`-Format `YYYY-MM-DD`, `liveChannels` darf `null` sein.
2. `?range=13mo` → Service mit `Months13` aufgerufen; `?range=7d`, `?range=30D`, `?range=` + Leerzeichen → 400 `invalid_dashboard_range`.
3. Anonym → 401, Nicht-Admin → 403 (Matrix).
4. `GET /rate-limits/trend` → 200 mit 1.440er-Listen; Reader liefert `TelemetryAvailable=false` → 200,
   `telemetryAvailable=false`, leere Listen, **kein** Fehlercode.
5. `GET /roster` → `ceilings.maxActiveChannels` = konfigurierter Wert (UseSetting `Channels:MaxActiveChannels`).
6. Cache: zwei Aufrufe binnen 60 s → Query-Service **einmal** (`Received(1)`); nach Ablauf erneut;
   Range-Keys getrennt.
7. Bestehende `AdminRateLimitsEndpointTests` unverändert grün.

**DECISIONS (R2, Regel 3 — Fortschreibung des A1-Eintrags in diesem Commit):** Absätze zu den
Routen und ihrem Fehlercode (`invalid_dashboard_range`, 400 bei unbekannter Range, Default statt
Fehler bei fehlender), 60-s-REST-Poll statt Admin-Event (`AdminTypes` unverändert), Caches/
Single-Flight je Api-Instanz (60 s/30 s/30 s, Fehler nicht gecacht), `maxActiveChannels` im Roster.
Die Spec-Datei selbst wird nicht umgeschrieben.

**Commit (R2, ein Commit):** `feat(api): add the admin dashboard activity and rate-limit trend routes`
— enthält Routen, Fassade, `ApiErrorCodes` + `api-error.ts` + de/en, Tests **und** DECISIONS. Ein
getrennter Fehlercode-Commit ohne seine Route wäre keine eigene logische Einheit und hätte seinen
DECISIONS-Absatz nicht bei sich (Regel 3). **Abnahme:** Tests 1–7 grün, `api-error-locales.spec.ts`
grün, DECISIONS-Absätze vorhanden, `dotnet format` sauber.

### A4 — Web-Grundlagen: Modelle, Service, Chart-Primitiv, Sichtbarkeit (`sonnet`)

**Dateien:** `web/src/app/core/admin/admin.model.ts`, `admin.service.ts`, `admin.service.spec.ts`;
neu `web/src/app/core/page-visibility/page-visibility.ts` + Spec, `web/src/app/core/live/poll-while-visible.ts`
+ Spec (Vitest mit `vi.useFakeTimers()`); neu `web/src/app/shared/charts/chart-series.ts` + Spec,
`line-chart.ts` + Spec (nur Verhalten: Pfad bricht bei `null`, Cursor-Semantik, `role="img"`-Name,
Live-Region aktualisiert sich — keine CSS-Asserts, Regel 12).

**Spec-Fälle (Auswahl):** `toLinePath` mit `[1, null, 2]` erzeugt zwei Teilpfade; nur `null` → leerer
Pfad; `niceTicks(0, 0.87, 4)` liefert runde Schritte; `formatUnit` GiB/MiB-Schwellen, Prozent 1
Dezimale, Kerne 2 Dezimalen; `pollWhileVisible`: verborgen → kein Tick, sichtbar → sofortiger Tick
und danach Intervall; `pageVisible` folgt `visibilitychange`.

**Commit:** `feat(web): add the dashboard chart primitive, visibility-aware polling and admin dashboard models`.
**Abnahme:** Vitest/Lint/Format grün.

### A5 — Web: Aktivitätsverlauf, Rate-Limit-Ausnahmen, Details-Dialog, i18n (`sonnet`)

**Dateien:** `admin-monitoring-page.ts` (+ `.spec.ts` für `computed()`-Entscheidungen), neu
`web/src/app/features/admin/admin-rate-limit-details-dialog.ts` (bestehendes Policies/Caches/
Provider-Markup zieht um), neu `web/src/app/features/admin/admin-activity-section.ts` (Chart +
Range + Kontextwert, Inputs: Aktivität, Roster-Zahlen; Range als `model`), `admin-roster-card.ts`
(`roster` öffentlich), `web/public/i18n/de.json`/`en.json` (`admin.dashboard.*`), ggf.
`docs/UI-Designsprache.md` neuer Unterabschnitt „2.6 Small multiples (`app-line-chart`)" als
Ist-Stand-Beschreibung (*Annahme A-5*; Doku führt nur Ist-Stand, deshalb erst hier).

**Spec-Fälle (Verhalten, Regel 12):** Exception-first-Sortierung (akut vor 24 h, dann absteigend);
Anteil bei 0 Entscheidungen = Sonderfall „keine Requests"; **(R2) Trend-Reihen: lokale Ablehnungen
plus drei ablehnende Provider/Call-Source-Dimensionen → vier getrennte Reihen mit je eigenem Label,
eine Dimension ohne Ablehnung im Fenster erscheint nicht;** `telemetryAvailable=false` → Banner,
Dialog öffnet trotzdem; Range-Wechsel setzt `params` und löst Reload aus; Fehler der Aktivitäts-Resource
blendet Health nicht aus (Muster „the two resources fail independently" im bestehenden Spec);
Dialog-Rückgabe/Öffnen über `openAppDialog`.

**Commit:** `feat(web): add the activity history and exception-first rate-limit sections to admin monitoring`.
**Abnahme:** Vitest/Lint/Format grün; manuelle Sichtprüfung `ng serve` (de/en, hell/dunkel) protokolliert.

### A6 — E2E, Mocks, Audit-Szenarien (`sonnet`)

**Dateien:** `web/e2e/support/mocks.ts` (neu `mockAdminRateLimits`, `mockAdminRateLimitsTrend`,
`mockAdminDashboardActivity`, Roster-Mock um `maxActiveChannels`); neu `web/e2e/admin-monitoring.e2e.spec.ts`
(Import aus `./support/test`); `web/e2e/audit/ui-audit.audit.ts` (bestehende drei
`admin-monitoring*`-Szenarien bekommen die neuen Mocks — sonst messen sie Fehlerbanner; neu
`admin-monitoring-rejections` mit langen Policy-/Provider-Namen und 1.440-Punkte-Trend,
`admin-monitoring-telemetry-down`).

**E2E-Fälle (Spec §11 A):** Bereichswechsel 30d→13mo ruft `range=13mo` und zeichnet neu; leere
Tagesreihe (alles `null`) → Lückenchart ohne Fehler; `400 invalid_dashboard_range`-Mock → Banner nur im
Aktivitätsbereich; Redis-degradiert → Warnbanner, Dialog mit Konfiguration; **60-s-Refresh ohne
`usage.flushed`:** `page.clock.install()` **vor** `goto`, `pauseAt`, Mock-Antwort ändern, `runFor(60_000)`,
neue Zahl sichtbar, kein SSE-Event nötig (`installLiveStub`); verborgener Tab (`document.hidden`
per `page.evaluate`-Override oder CDP `Emulation.setFocusEmulationEnabled` — wählen, was der
Stub zulässt) → kein Request in 60 s, nach Rückkehr sofort einer.

**Audit:** `rm -rf web/.audit-out && cd web && npx playwright test --config=playwright.audit.config.ts`
(keine Api auf `:5151`/`:4300`); Gates: `horizontalOverflowPx` = 0 in allen `admin-monitoring*`-Metriken,
`contrastViolations` leer, `smallTargetsUnder24` keine neuen Einträge gegenüber dem letzten Lauf
(Baseline aus A0-Lauf oder vorherigem `.audit-out`, Memory: `smallTargets` mitprüfen); Screenshots
de/en sichten (lange deutsche Achsentitel).

**Commit:** `test(web): cover the admin monitoring dashboard sections end to end`.
**Abnahme:** E2E grün (`npm --prefix web run e2e`), Audit-Gates bestanden, Protokoll im Task-Ergebnis.

### A7 — Gates A, Coverage, Live-Verifikation, Doku, Codex, PR (`opus`)

- [ ] Volle Gates: `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`,
      `npm --prefix web run lint`, `format:check`, `npm --prefix web run e2e` (ohne Api auf `:5151`),
      `dotnet format --verify-no-changes`.
- [ ] `node scripts/coverage-local.mjs` — Ziel ≥ 80 % je neuer Datei; bei Unterdeckung Tests nachziehen
      (Memory: Näherung, nur Committetes zählt).
- [ ] **Live-Verifikation (Regel 16), lokal:** Abschnitt 8.1.
- [ ] `CLAUDE.md` Statustabelle: Zeile „Admin" um „Dashboard Schnitt A (#381): Aktivitätsverlauf +
      Rate-Limit-Ausnahmen" ergänzen; `docs/Feature-Ideen-…` nur, falls #381 dort als Idee geführt ist
      (prüfen per grep, sonst nichts).
- [ ] `dotnet ef migrations list` → unverändert.
- [ ] Commit `docs: note admin dashboard cut A in the status table`, Push, PR gegen `main`
      (englischer Titel/Body, Abschlusszeile laut Attribution-Regel; kein Merge).
- [ ] `/codex:review --model gpt-6.1-sol` über den Branch; Ergebnis unverändert an den Orchestrator
      (Regel 22; Widersprüche zu einem Opus-Review → Fable als Schiedsrichter, nicht still auflösen).

**Abnahme:** alle Gates grün, Coverage-Näherung ≥ 80 %, Live-Protokoll mit erwarteten Beobachtungen,
PR offen, Codex-Befund vorgelegt.

## 6. Verträge Schnitt B

### 6.1 Core: Load-Mitglieder auf `IAdminDashboardQueryService`

| Mitglied | Form |
|---|---|
| `AdminDashboardLoadRange` | Enum `Hour1`, `Hours24`, `Days7`, `Days30`, `Days90`, `Months13`; Parser von `"1h"|"24h"|"7d"|"30d"|"90d"|"13mo"` (ordinal exakt); Eigenschaften je Range: `Duration`, `BucketSeconds` (60, 300, 3600, 7200, 21600, 86400), `Source` (`Raw` für `1h`/`24h`, sonst `Hourly`). |
| `GetLoadAsync(AdminDashboardLoadRange range, DateTime nowUtc, int samplingIntervalSeconds, CancellationToken)` | `samplingIntervalSeconds` = `SystemLoadOptions.IntervalSeconds` des Aufrufers (Fassade injiziert das `SystemLoadOptions`-Singleton, `ServiceCollectionExtensions.cs:311-314`); dieselbe Zahl wie der Rollup-Worker. Liefert `AdminDashboardLoad`. |

Bucket-Leiter (Spec §5.2): feste Zuordnung Range → Breite wie oben; `toUtc = nowUtc`, `fromUtc` =
`floor((nowUtc − Duration) / BucketSeconds) × BucketSeconds` (UTC-Vielfache), Buckets von `fromUtc`
bis einschließlich des Buckets, der `toUtc` enthält; damit `1h` → 60–61, `24h` → 288–289, `7d` → 168–169,
`30d` → 360–361, `90d` → 360–361. **(R2) `13mo` ist für die Last ein rollendes Fenster** —
`Duration` = `nowUtc − nowUtc.AddMonths(−13)` (Kalendermonate, Tagesende-Klemmung wie `DateTime.AddMonths`),
auf UTC-Mitternacht abgerundet, Tagesbuckets bis einschließlich heute: 2026-10-11 → 396 Buckets,
Worst Case über 2025–2029 (Monatsende + Schaltjahr, z. B. 2028-03-31) → 398. Das ist die Spec-Angabe
„≈ 395" (§5.2) und die rollende Aussage „voll ab 2027-11-11" (§3); der Monatserste gilt **nur** für
die Aktivität (§6). Zusätzlich eine **harte Kappe**: liefert die Mathematik je mehr als 400 Buckets,
wirft der Service (`InvalidOperationException`, → 500), damit das Budget nie still reißt; Test mit
Grenzdaten (31.10., 31.01., 29.02.2028, 31.03.2028). Hourly-Zeilen mit `HourUtc < fromUtc` werden
**gefiltert, nicht angehängt** — ältere Historie (Retention aus oder gescheitert) verlängert die Range
nicht; das Weglassen führender leerer Buckets kann sie nur verkürzen.

`AdminDashboardLoad` (record, Core):

| Feld | Inhalt |
|---|---|
| `Available` | `false`, wenn in der Range keine einzige Beobachtung liegt (dann alle Listen leer, `Buckets` leer, `DataThroughUtc = null`). |
| `Range`, `SourceKind` (`"raw"|"hourly"`), `BucketSeconds`, `FromUtc`, `ToUtc`, `GeneratedAtUtc`, `DataThroughUtc`, `SamplingIntervalSeconds` | Metadaten; `DataThroughUtc` = jüngstes `SampledAtUtc` bzw. jüngste `HourUtc + 1 h` in der Range. |
| `BucketStartsUtc: IReadOnlyList<DateTime>` | gemeinsame x-Achse, **ohne führende leere Buckets** vor dem ersten vorhandenen Datum (Spec §3: Rangeanfang auf den ersten vorhandenen Bucket). |
| `Host` (`AdminDashboardHostSeries`) | `CpuBusyP95`, `CpuBusyMax` (Prozent 0–100, 1 Dezimale), `MemoryUsedP95Bytes`, `MemoryUsedMaxBytes`, `MemoryTotalBytes` (roh: Sample-`MemTotal` des Buckets; hourly: `LastHostMemoryTotalBytes`), `LoadAverage1P95`, `LoadAverage1Max`, `Provenance` (`"A"|"W"|"AW"|null` je Bucket: welche Sources beigetragen haben), **(R2) je Metrik eine Gewinnerquelle:** `CpuValueSource`, `MemoryValueSource`, `LoadAverageValueSource` (`"A"|"W"|null`: Source, deren Stunden-P95 im Bucket gezeigt wird; Hourly-Pfad wählt je Metrik unabhängig, CPU kann aus `Api`, Speicher aus `Worker` kommen). **Tie-Regel (deterministisch):** gleicher P95 → höheres Stundenmax gewinnt; immer noch gleich → `Worker` (die kanonische Workload-Source). Im Rohpfad sind die Beobachtungen vereinigt, dort ist `*ValueSource` die Source des Samples, das den P95-Rang stellt (bei Gleichstand dieselbe Regel). `ApiSampleCount`, `WorkerSampleCount` (je Bucket), `ApiCpuBusyMean`, `WorkerCpuBusyMean`, `ApiMemoryUsedMeanBytes`, `WorkerMemoryUsedMeanBytes`, `ApiLoadAverage1Mean`, `WorkerLoadAverage1Mean` (Tooltip-Werte). Alle Listen gleich lang wie `BucketStartsUtc`, `null` = unbekannt. |
| `Api`, `Worker` (`AdminDashboardProcessSeries`) | `ProcessCpuP95`, `ProcessCpuMax` (Kerne, 4 Dezimalen), `CgroupCpuP95`, `CgroupCpuMax`, `CgroupCpuQuotaCores` (roh: endliche Quota des Samples; hourly: `LastCpuQuota/Period`), `CgroupCpuQuotaShareP95`, `CgroupCpuQuotaShareMax` (Prozent; **nur roh**, nur Paare mit gleicher endlicher Quota an beiden Enden; hourly immer `null`), `CgroupMemoryP95Bytes`, `CgroupMemoryMaxBytes`, `CgroupMemoryLimitBytes` (roh: endliches `memory.max`; hourly: `LastCgroupMemoryMaxBytes`), `CgroupMemoryShareP95`, `CgroupMemoryShareMax` (Prozent; nur roh, nur bei endlichem Limit), `SampleCount`. |
| `Worker` zusätzlich (`AdminDashboardWorkloadSeries`) | `ActiveChannelsMax`, `LiveChannelsMax`, `ChatLines`, `EmoteMatches`, `WorkloadUnit` (`"perMinute"` roh: Zähler ÷ tatsächliche Intervallsekunden × 60; **(R2) `"perBucket"` hourly: Summe der gespeicherten Intervallzähler aller Stunden im Ausgabe-Bucket** — bei `7d` eine Stunde, bei `30d` zwei, `90d` sechs, `13mo` 24; der Client beschriftet mit der tatsächlichen Bucketdauer „gespeicherte Intervallzähler je 6 h" (Spec §5.2 „je Ausgabe-Bucket"), **nie** „je Stunde" und nie durch 60 geteilt), `WorkloadIntervalSeconds` (roh: Summe der gültigen Intervallsekunden je Bucket, Tooltip), `WorkloadHoursWithData` (hourly: Zahl der Stundenzeilen mit Workload im Bucket, Tooltip — damit 1.440 in einem Tagesbucket als 24 × 60 lesbar bleibt). |
| `Swap` | `LastSwapUsedBytes`, `LastSwapTotalBytes`, `ObservedAtUtc` (nur roh, nur wenn `SwapTotal > 0`; Detailwert). |

Rohpfad-Regeln (Spec §5.1/§5.2), vom Service einzuhalten und zu testen: Samples der Range je
`(Source, ProcessInstanceId)` laden plus **einen Vorgänger je Gruppe** vor `fromUtc` (LATERAL-Muster
wie `SystemLoadRollupService.LoadCandidateSamplesAsync`, nur projizierte Spalten); Paare = gleiche
Gruppe, `elapsed > 0`, `elapsed ≤ 2,5 × samplingIntervalSeconds`, Zähler monoton; Raten je Paar dem
Bucket des **späteren** Samples zuordnen; P95 Nearest Rank **identisch zu `SystemLoadRollupMath`**
(Paar-/Raten-Helfer aus `SystemLoadRollupMath` wiederverwenden bzw. dorthin extrahieren — keine
zweite Implementierung der Lückenregel); Host-Raten je Source bilden, dann je Bucket die
**Vereinigung** beider Sources für P95/Max; Gauges (Memory, LoadAvg, Channels) je Bucket aus allen
Samples. Hourly-Pfad: Zeilen `HourUtc ∈ [fromUtc, toUtc)` beider Sources; je Bucket **höchster
Stunden-P95** und **höchstes Stundenmax** über Stunden **und** Sources, je Metrik `Cpu|Memory|LoadAverageValueSource` = Source dieses
P95; Workload = Summe; Channels = höchstes Stundenmax; `MemoryTotalBytes`/Limits = letzter Wert im
Bucket; keine Quoten. Speicherdisziplin: keine Materialisierung ungebucketeter Rohobjekte über die
projizierten Zeilen hinaus (streamendes Bucketing über `AsAsyncEnumerable`, Ergebnislisten mit
`BucketCount` vorab dimensioniert) — das B5-Gate misst es.

### 6.2 Core: Fassade `IAdminDashboardReader.GetLoadAsync(range, ct)` → `AdminDashboardLoadRead`

`AdminDashboardLoadRead(AdminDashboardLoad? Load, bool Busy, int RetryAfterSeconds)`. Regeln
(Spec §8): Cache-Key = Range (sechs Keys), TTL 30 s für `1h`/`24h`, 60 s für längere; Single-Flight je
Key (`Lazy<Task>`); **ein** Neuaufbau zur Zeit über alle Keys (ein `SemaphoreSlim(1,1)` der Fassade,
`WaitAsync(5 s)`; Timeout → `Busy=true`, `RetryAfterSeconds=5`, kein Aufbau gestartet); ein
Wartender auf denselben Key teilt das laufende Ergebnis statt zu warten. Fehler nicht cachen.

### 6.3 Api: `GET /api/admin/dashboard/load`

| Parameter | Antwort | Fehler |
|---|---|---|
| `range` Query, Default `24h` | JSON-Serialisierung von `AdminDashboardLoad` (camelCase, Listen spaltenorientiert, `null` bleibt `null`; Zahlen gerundet wie in 6.1 — Budget), **≤ 256 KiB**, **≤ 400 Buckets**, keine `ProcessInstanceId`, keine Rohzeilen | `400 invalid_dashboard_range`; `Busy` → **`503` ohne Body mit `Retry-After: 5`** (*Annahme B-1*: kein neuer Fehlercode, die Spec nennt keinen; der Client behält die letzte Antwort); 401/403 Gruppenfilter; DB-Fehler → 500 `unexpected_error` |

Kein Tail-/`latest`-Endpunkt (Spec §6).

### 6.4 Web

`admin.model.ts`: `AdminDashboardLoadRange`, `AdminDashboardLoad` (Spiegel von 6.1 in camelCase);
`admin.service.ts`: `getDashboardLoad(range)`. Lastbereich-Komponente
`web/src/app/features/admin/admin-load-section.ts` mit festen **11 Small Multiples** in dieser
Reihenfolge (Spec §5.2-Tabelle, Zahl unabhängig von Range): Host-CPU busy %; Host-RAM belegt (Byte,
letztes Gesamt-RAM als Textzeile); Host-Load-Average 1 min; Prozess-CPU Kerne (Api + Worker, zwei
Linien); cgroup-CPU Kerne (Api + Worker, Quota als Textzeile je Source); cgroup-CPU-Quota-Anteil %
(roh; in Hourly-Ranges **bleibt das Diagramm stehen** und zeigt den Hinweis „keine Quote in
Stundenwerten, letztes Limit: …" — *Annahme B-2*); cgroup-Speicher Api; cgroup-Speicher Worker
(Limit als Textzeile, Anteil nur roh); Channels aktiv/live (zwei Linien); Chatzeilen; Emote-Treffer.
Achsentitel je Range („P95" ↔ „höchster Stunden-P95" ab `7d`; „/min" ↔ **„gespeicherte
Intervallzähler je N h"** mit N aus `bucketSeconds`, R2).
**Lastmenü (R2, Spec §10 „Messdaten für mindestens einen Bereich"):** sichtbar, sobald irgendeine
Range `available=true` geliefert hat, gemerkt in einem Signal `historyKnown`. Liefert der Default
`24h` `available=false`, sondiert die Komponente **einmal je Poll-Zyklus zusätzlich `13mo`** (die
weiteste Hourly-Range; serverseitig 60 s gecacht, also eine zweite billige Anfrage): ist sie
`available=true`, wird `historyKnown=true`, das Menü erscheint, und der Empty State des Defaults
nennt „Für 24 h liegen keine Samples vor, Stundenwerte ab <bucketStartsUtc[0] der Sondierung>".
Sind beide leer: Empty State „Wartet auf erste erfolgreiche Samples und den Hourly-Rollup", Sondierung
wiederholt sich mit jedem Poll. Serverseitige Alternative (Feld `earliestObservationUtc` in jeder
Antwort) bewusst nicht gewählt: sie erweitert den Spec-Vertrag §6. Poll via `pollWhileVisible`: 60 s für `1h`/`24h`,
300 s ab `7d`; Rückkehr aus dem Hintergrund → sofortiger Reload. Lesefehler/503: letzte Antwort bleibt,
Abschnitt bekommt Veraltet-Markierung (`app-notice-banner warning` mit Zeitpunkt der letzten
erfolgreichen Antwort); nächster Erfolg ersetzt alles. Rate-Limit-24-h-Reihe: bei Load-Range ≤ `24h`
unter die Last-Diagramme auf dieselbe x-Achse (Trend-Minuten in Load-Buckets summieren, Hilfsfunktion
in `chart-series.ts`), bei längeren Ranges nur die Ausnahmen-Zusammenfassung ohne Reihe.

## 7. Tasks Schnitt B

### B0 — Branch B, Drift-Check (`haiku`)

- [ ] `git switch -c feat/381-admin-dashboard-b origin/main` nach Merge von A (sonst von
      `feat/381-admin-dashboard-a`, später rebasen). Baseline wie A0. Prüfen, ob `SystemLoadRollupMath`,
      `SystemLoadRollupService`, `SystemLoadOptions` seit `97c286e7` geändert wurden (`git log --oneline
      -- src/EmotePurge.Infrastructure/Services/SystemLoad*`); Abweichungen melden, bevor B1 startet.

### B1 — Load-Query-Service, Bucket-Leiter, Strukturtest (`opus`)

**Dateien:** `IAdminDashboardQueryService.cs` (6.1), `AdminDashboardQueryService.cs` (Roh- und
Hourly-Pfad; Bucket-Mathematik in eigener reiner Klasse `AdminDashboardLoadBucketing` in
`Infrastructure/Services/`), `SystemLoadRollupMath.cs` (Paar-/Raten-/P95-Helfer `internal`
freilegen, ohne Verhalten zu ändern — bestehende `SystemLoadRollupMathTests` bleiben), neu
`tests/EmotePurge.Infrastructure.Tests/Unit/AdminDashboardLoadBucketingTests.cs`, neu
`tests/EmotePurge.Infrastructure.Tests/Integration/AdminDashboardLoadQueryTests.cs`
(`[Collection("Postgres")]`, eigene DB, `TRUNCATE "SystemLoadSamples", "SystemLoadHourly"`), neu
`tests/EmotePurge.Infrastructure.Tests/Unit/AdminDashboardQueryStructureTests.cs`.

**Tests (Spec §11 B):**
1. Leiter: je Range Breite und Bucketzahl (`1h`→60 min/≤ 61, `24h`→5 min/≤ 289, `7d`→1 h/≤ 169,
   `30d`→2 h/≤ 361, `90d`→6 h/≤ 361, `13mo`→24 h/≤ 398), UTC-Ausrichtung, Breite unabhängig von
   Kadenz (1 s und 30 s → gleiche Buckets), nie > 400. **(R2) `13mo`-Grenzdaten:** `nowUtc` =
   2026-10-11, 2026-10-31, 2027-01-31, 2028-02-29, 2028-03-31 (Schaltjahr + Monatsende) → Bucketzahl
   je ≤ 398, `fromUtc` = Mitternacht des Tages 13 Kalendermonate zurück (nicht Monatserster); harte
   Kappe: ein Fake-`nowUtc`/-Leiter, die 401 ergäbe, löst die `InvalidOperationException` aus.
   **Ältere Historie:** Hourly-Zeilen 14 und 20 Monate alt (Retention aus) liegen vor → Antwort beginnt
   frühestens bei `fromUtc`, Bucketzahl unverändert.
2. Vorgänger je `(Source, ProcessInstanceId)`; Instanz, die in der Range beginnt, hat keinen und ihr
   erster Punkt keine Rate (Rate-Listen `null` dort).
3. Lückenregel = Rollup: ein Testfall rechnet dieselbe Stunde über `SystemLoadRollupMath.Calculate`
   und über den Rohpfad (1-h-Buckets durch Range `24h` auf Stundengrenze gefiltert) und erwartet
   gleiche P95/Max/Mean-Raten; Werte 1/30/60/3600 s für `samplingIntervalSeconds`.
4. **Kadenzwechsel:** 20 h mit 30 s, dann 4 h mit 1 s, Intervall jetzt 1 s: vor dem Wechsel
   verdichtete Hourly-Zeile hält ihre Werte; Rohpfad zeigt dieselbe Stunde ohne CPU-Paare; 1-s-Phase in
   beiden Pfaden gleich.
5. **Ausfälle:** 30-s-Konfiguration, Instanz mit 600-s-Lücke, Instanz mit 3.600-s- und 120-s-Lücke →
   Paare über Lücken verworfen, Buckets leer, Paare davor/danach gültig; kein Paar nur deshalb
   akzeptiert, weil es das einzige seiner Instanz ist.
6. **Partielle Hostabdeckung roh:** Worker 5 ruhige Minuten, dann aus; Api erfasst danach einen Peak →
   Bucket zeigt Peak, `Provenance = "A"`, `ValueSource = "A"`; Vereinigung ohne Summieren.
7. **Partielle Hostabdeckung hourly:** Worker-Stunde niedriger P95 (wenige Samples), Api-Stunde Peak →
   höherer P95 und Max gezeigt, beide `SampleCount` ausgegeben, `CpuValueSource = "A"`.
   **(R2) Verschiedene Gewinner je Metrik:** ein Bucket, in dem die Api den höheren CPU-P95, der Worker
   den höheren Speicher-P95 und beide denselben LoadAvg-P95 bei höherem Worker-Max haben →
   `CpuValueSource="A"`, `MemoryValueSource="W"`, `LoadAverageValueSource="W"`; voller Gleichstand → `"W"`.
8. Quota: endlich/`max`, Wechsel innerhalb eines Paars → Kerne ja, Anteil `null`; Memory-Limit
   endlich/`max`; `current = null` vs echter Nullverbrauch; Hourly nie Quote (auch bei Limitwechsel
   in der Stunde).
9. `ProcessInstanceId`-Wechsel, Zählerreset (negatives Delta), Host-Counterabfall → Paar verworfen.
10. Workload: 60 Chatzeilen-Intervallzähler in einer Sample-Stunde → hourly `ChatLines = 60` je
    Stundenbucket (nicht 1/min); roh: Zähler ÷ Intervallsekunden × 60; Teilabdeckung wird nicht
    hochgerechnet; Intervall über Stundengrenze gehört zur Stunde des Samples. **(R2) Mehrstündige
    Buckets, beide Metriken:** je 60 pro Stunde über 48 Stunden → `30d` (2 h) liefert 120 je Bucket,
    `90d` (6 h) 360, `13mo` (24 h) 1.440, `WorkloadUnit = "perBucket"`, `WorkloadHoursWithData` = 2/6/24;
    fehlt eine Stunde im Bucket, sinkt die Summe und `WorkloadHoursWithData`, nichts wird hochgerechnet.
11. Raw-/Hourly-Grenze: `24h` liest keine `SystemLoadHourly`, `7d` keine `SystemLoadSamples`
    (per `DbCommandInterceptor` wie `SystemLoadRollupBoundedQueryTests` — SQL-Text prüfen).
12. `Available=false` bei leerer Range; `DataThroughUtc`; keine führenden leeren Buckets; mehrere
    Stunden je Bucket (`30d` → 2 h) → höchster Stunden-P95/Max, Workload summiert, Channels max.
13. Strukturtest: `typeof(AdminDashboardQueryService)` referenziert in keinem Member/IL-Body die Typen
    `UsageStat`/`DbSet<UsageStat>` (Reflexion über Methodenkörper per `MethodBody.GetILAsByteArray` +
    Token-Auflösung, oder einfacher: Quelltext-Scan der Datei auf `UsageStats`/`UsageStat` als
    Testdatei-Asset — eine der beiden Formen, im Test begründet).

**DECISIONS (R2, Regel 3 — Fortschreibung im selben Commit):** Absätze zur festen Bucket-Leiter
und dem 400-Bucket-Budget mit harter Kappe, `13mo` rollend, `7d` aus Hourly trotz siebentägiger
Rohaufbewahrung, Lückenregel des Rohpfads = Rollup (2,5 × konfiguriertes Intervall) samt den
benannten Grenzen nach Kadenzwechsel, Host-Zusammenführung (Vereinigung roh, höchster Stunden-P95/-Max
über Sources hourly, Provenienz je Metrik mit Tie-Regel statt behauptetem vereinigtem P95),
Workload-Einheiten (`perMinute`/`perBucket`), Null-/Reset-Semantik der Anzeige.

**Commit:** `feat(infrastructure): add the admin dashboard load query with server-side bucketing`
(inkl. DECISIONS). **Abnahme:** Tests 1–13 grün, bestehende SystemLoad-Tests unverändert grün.

### B2 — Api `/dashboard/load`, Load-Cache, 503/Retry-After, Contract-Tests, DECISIONS (`sonnet`)

**Dateien:** `IAdminDashboardReader.cs`/`AdminDashboardReader.cs` (6.2; `SystemLoadOptions`-Singleton),
`AdminEndpoints.cs` (Route 6.3), `ApiFactory.cs` (Substitut deckt die neue Methode), neu
`tests/EmotePurge.Api.Tests/AdminDashboardLoadEndpointTests.cs`, `AuthFilterMatrixTests.cs`
(Route in Matrix), `tests/EmotePurge.Infrastructure.Tests/Unit/AdminDashboardReaderTests.cs`
(erweitern: Single-Flight je Key, serielles Gate über Keys, 5-s-Timeout → Busy, Fehler nicht gecacht),
`docs/DECISIONS.md` (R2: nur die Absätze, die **dieser** Commit begründet — Route und 256-KiB-Budget,
kein Tail/Merge, Single-Flight je Range-Key + ein Neuaufbau zur Zeit + 503/Retry-After, Cache-TTLs
30 s/60 s; Bucketing und Host-Regeln stehen seit B1 im Eintrag).

**Contract-Tests:** Default `24h`; alle sechs Ranges → Service mit richtigem Enum und
`samplingIntervalSeconds` aus Options; `?range=2h` → 400; 401/403; `Available=false` → 200 mit leeren
Listen; Serialisierung: `bucketStartsUtc` ISO-UTC, Listen gleich lang, **kein** `processInstanceId`-Property
irgendwo im JSON (Substring-Suche), 400-Bucket-Antwort mit Vollbelegung aller Serien ≤ 256 KiB
(Byte-Länge messen); `Busy` → 503 + `Retry-After: 5`, leerer Body.

**Commits:** `feat(api): add the admin dashboard load route with single-flight rebuilds` (inkl.
DECISIONS). **Abnahme:** Tests grün, Eintrag ergänzt.

### B3 — Web: Lastbereich (`sonnet`)

**Dateien:** `admin.model.ts`, `admin.service.ts` (+ Spec), neu `admin-load-section.ts` (+ Spec:
Range-Wechsel ändert Achsentitel/Bucketbreite, nicht Diagrammzahl; Veraltet-Zustand nach Fehler;
Empty-State-Entscheidung aus `available`/`dataThroughUtc`; **(R2) `historyKnown`-Sondierung: leerer
`24h`-Default + verfügbares `13mo` → Menü sichtbar, Empty-State-Text nennt den Stundenstand; beide
leer → Wartetext, Sondierung je Poll;** Workload-Label aus `bucketSeconds`; Quota-Anteil nur roh;
Trend-Reihe nur ≤ 24h),
`admin-monitoring-page.ts` (Bereich **vor** dem Aktivitätsverlauf; `reload()` lädt auch Last),
`shared/charts/chart-series.ts` (`sumMinutesIntoBuckets`, Provenienz-Codes → Tooltip-Text, Spec),
`line-chart.ts` (Cursor-Textzeile nimmt Zusatzzeilen für Provenienz/`SampleCount`), `de.json`/`en.json`
(`admin.dashboard.load.*`), `docs/UI-Designsprache.md` 2.6 ergänzen (Ist-Stand).

**Commit:** `feat(web): add the load and work section to admin monitoring`. **Abnahme:** Vitest/Lint/Format grün.

### B4 — E2E B, Audit, Render-Messung (`sonnet`)

**Dateien:** `mocks.ts` (`mockAdminDashboardLoad` mit Generator für N Buckets), `admin-monitoring.e2e.spec.ts`
(erweitern), `ui-audit.audit.ts` (Szenarien `admin-monitoring-load-empty`, `admin-monitoring-load-24h`
mit 289 Buckets, `admin-monitoring-load-13mo` mit 397 Buckets und Lücken).

**E2E-Fälle (Spec §11 B):** Empty State ohne Lastmenü bei `available=false` in `24h` **und** `13mo`;
**(R2) leere jüngste Samples bei vorhandener Hourly-Historie:** `24h`-Mock `available=false`,
`13mo`-Mock mit Daten → Menü erscheint, Empty State nennt den Stundenstand, Wechsel auf `7d` zeigt
Kurven; zu kurze Historie (Datenstand genannt); Range-Wechsel `24h→13mo`: Diagramm- und Serienzahl konstant (11 `role="img"`),
Achsentitel wechselt, Rate-Limit-Reihe verschwindet; wiederholte 503/500 → letzte Antwort steht,
Veraltet-Markierung; Erfolg danach → Markierung weg, Werte ersetzt; **Tab-Suspendierung 30 min:**
`page.clock` + Sichtbarkeits-Override, 30 min `runFor` ohne Request, Rückkehr → ein Request sofort;
nachträglich erschienene ältere Stunden im nächsten Poll (Mock liefert beim zweiten Aufruf frühere
`bucketStartsUtc`) → Chart beginnt früher; **Render-Messung:** Test misst `performance.now()` von
`goto` bis alle 11 Charts gerendert (Mock 400 Buckets) und loggt die Dauer; Schwelle als weiche
Grenze 2 s (Log, kein Assert — die Zahl geht in B7s Protokoll). Audit wie A6.

**Commit:** `test(web): cover the admin load dashboard end to end`.

### B5 — Response-Performance-Gate (`sonnet`)

**Dateien:** neu `tests/EmotePurge.Infrastructure.Tests/Integration/AdminDashboardLoadPerformanceGateTests.cs`
(`[Collection("Postgres")]`, `[Trait("Category","PerformanceGate")]`, **Skip, solange
`EMOTEPURGE_PERF_GATES` ≠ `1`** — *Annahme B-3*: opt-in, damit `dotnet test EmotePurge.slnx` nicht
minutenlang seedet), neu `tests/EmotePurge.Infrastructure.Tests/Fixtures/SystemLoadSampleSeed.cs`
(generiert 24 h × 1 s je Source = 172.800 Zeilen per `COPY`/Batch-Insert mit realistischen Zählern,
zwei Instanzwechsel, eine 10-min-Lücke).

**Messung (Spec §11 B, R2 — zwei Hälften, beide müssen stehen):**

*In-Process (dieser Test):* Fassade **uncached** aufrufen (frische Instanz je Iteration), `Stopwatch`
um `GetLoadAsync(Hours24)` + `JsonSerializer.Serialize` mit den Api-`JsonOptions`;
**Allokation prozessweit** per `GC.GetTotalAllocatedBytes(precise: true)` davor/danach — nie
`GetAllocatedBytesForCurrentThread`, weil EF-/Cache-Continuations den Thread wechseln und die Differenz
dann Allokationen auslässt oder negativ wird. Damit die prozessweite Zahl dem Aufbau zurechenbar ist,
läuft der Test in einer **eigenen xUnit-Collection mit `DisableParallelization = true`** und misst erst
nach einem `GC.Collect()`-Warmlauf. **Nachweis je Iteration, dass wirklich 86.400 Zeilen je Source
gelesen wurden:** ein `DbCommandInterceptor` wickelt den `DbDataReader` und zählt `Read()`-Aufrufe
(172.800 ± Vorgängerzeilen), zusätzlich steht die Seed-Zeilenzahl per `COUNT(*)` im Testlog; ein
Lauf mit weniger gelesenen Zeilen ist **ungültig** (Assert), nicht „schnell".
**Bestanden:** In-Process-Zeit < 400 ms (Median aus 5, warm — Reserve für den HTTP-Anteil), serialisiert
≤ 256 KiB, prozessweite Allokation während des Aufbaus ≤ 64 MiB (Protokollwert; die bindende
Speichergrenze ist der RSS-Zuwachs in B6), Zeilenzähler stimmt.

*Ende-zu-Ende HTTP (in B6, derselbe Datensatz):* `curl -w '%{time_total}'` auf
`/api/admin/dashboard/load?range=24h` gegen den `--cpus=1 --memory=512m`-Container nach Cache-Leerung
— **Bestanden: < 500 ms uncached** (Median aus ≥ 10 kalten Zyklen) und Antwortgröße ≤ 256 KiB
(`%{size_download}`). Erst beide Hälften zusammen erfüllen „vollständige API-Abfrage, Bucketing,
JSON-Serialisierung und Server-Allokationen" aus §11.

Reißt die 400/500 ms, wandert das Bucketing laut Spec in SQL (`date_bin` + Fensterfunktion je Instanz)
— gleicher Vertrag, gleiche Tests; das entscheidet B5, nicht B1. Aufruf:
`EMOTEPURGE_PERF_GATES=1 dotnet test tests/EmotePurge.Infrastructure.Tests --filter Category=PerformanceGate`.
Zusätzlich einmal mit 30-s-Seed (5.760 Zeilen) mitschreiben.

**Commit:** `test(infrastructure): add the opt-in response performance gate for the load dashboard`.

### B6 — Parallelitäts-Gate unter Prod-Grenzen (`opus`)

Kein xUnit; ein reproduzierbares Skriptprotokoll, lokal:

- [ ] `docker compose up -d postgres redis`; Seed per neuem `scripts/seed-system-load-samples.sql`
      (`generate_series`, Parameter Kadenz/Dauer, 24 h × 1 s je Source) in die Dev-DB; **oder** die
      B5-Seed-Klasse über einen kleinen `dotnet run`-Einstieg — Entscheidung des Tasks, SQL bevorzugt
      (coverage-ausgeschlossen, keine Produktivabhängigkeit).
- [ ] Api-Image bauen: `docker compose build api` (Memory: Rebuild ohne Rückfrage), starten als
      Einzelcontainer **mit** `--cpus=1 --memory=512m`, Env wie `docker-compose.yml`
      (Connection-Strings auf das Compose-Netz), Port `127.0.0.1:8080`. Worker-Health-Snapshot: lokalen
      Worker starten (`dotnet run --project src/EmotePurge.Worker`, schreibt `worker:health:twitch`
      alle 20 s, Status `connected` nach IRC-Handshake) — Alternative `redis-cli SET` in Schleife, falls
      der Worker lokal nicht `connected` wird.
- [ ] Admin-Cookie für Skripte: einmal im Browser einloggen, `.AspNetCore.Cookies`-Wert in eine
      Umgebungsvariable (nicht ins Repo).
- [ ] Neues `scripts/dashboard-concurrency-gate.sh` (Messung) + `scripts/dashboard-concurrency-gate-eval.mjs`
      (Auswertung, R2 — getrennt, damit die Auswertung mit eingeschleusten Verstößen geprüft werden kann):
      - **Zyklus = kalter Neuaufbau, mit der Health-Probe synchronisiert (R2):** der Takt der
        Health-Proben bleibt **fest 2,5 s** (24/min < 30/min `PublicHealth`); in **jedem** Takt startet
        das Skript im selben Augenblick wie die Probe die drei parallelen `curl` auf
        `/api/admin/dashboard/load?range=<R>` mit einem Range-Wechsel je Takt (`24h`, `13mo`, `24h`, …
        — beide Keys sind nach 60 s TTL wieder kalt; zwischen zwei gleichen Keys liegen ≥ 2 Takte
        und ein Api-`POST`-freier Cache-Reset ist nicht nötig, weil das Skript nur alle 60 s denselben
        Key wiederholt; alternativ Api-Neustart je Zyklus, dann Grundlast neu messen). Damit trifft
        jede Probe nachweislich einen laufenden Aufbau, kein Leerlauf: der Aufbau bestätigt sich über die
        Debug-Logzeile der Fassade („cold rebuild range=… ms=…", Level `Debug`), und die Auswertung
        wertet nur Proben, deren Sendezeit in ein geloggtes Aufbauintervall fällt — **mindestens 40 solche
        Proben und ≥ 10 Zyklen je Key**, sonst ungültig.
      - **Speicher = Prozess-RSS-High-Water-Mark, nicht `docker stats` (R2):** vor jedem Zyklus
        `docker exec -u root <api> sh -c 'echo 5 > /proc/1/clear_refs'` (setzt `VmHWM` zurück), nach dem
        Zyklus `VmHWM` und `VmRSS` aus `/proc/1/status` lesen; Zuwachs = `VmHWM − VmRSS-Grundlast vor dem
        Zyklus`. Fällt `clear_refs` im Image aus (kein Root, kein `/proc`-Schreibrecht): Fallback
        `VmRSS`-Sampling alle 50 ms während des Aufbauintervalls, Maximum nehmen — und das im Protokoll
        als Fallback kennzeichnen. `docker stats` misst cgroup-Speicher samt Page-Cache und wird nur als
        Zusatzspalte mitgeschrieben.
      - Query-Zähler: Debug-Logzeile je echtem Aufbau **und** `pg_stat_statements`-Delta der
        Load-Queries je Zyklus (beide müssen 1 ergeben).
      - Ende-zu-Ende-Latenz der Load-Antwort je Zyklus (`%{time_total}`, `%{size_download}`) für die
        HTTP-Hälfte von B5.
- [ ] **Bestanden, wenn (Spec §11 B):** (a) je Zyklus genau **eine** Load-Abfrage für die drei
      Requests (Log und `pg_stat_statements` einig); (b) `VmHWM`-Zuwachs ≤ 64 MiB in **jedem** Zyklus;
      (c) **jede** Health-Probe `200`, p95 der Latenz der im Aufbauintervall gesendeten Proben < 100 ms
      (nur 200er gezählt; ein 429/503/Timeout/Verbindungsfehler = Gate gerissen); (d) `24h` und `13mo`
      gleichzeitig (ein eigener Zyklus mit sechs Clients): der zweite Key antwortet < 5 s oder mit
      `503` + `Retry-After`; (e) Load-Antwort uncached < 500 ms, ≤ 256 KiB.
- [ ] **Negativnachweis (R2):** die Auswertung liest CSV-Zeilen (Zeit, Probe-Status, Latenz,
      Aufbauintervalle, `VmHWM`-Zuwachs, Query-Zähler). Drei präparierte CSVs unter
      `scripts/fixtures/dashboard-gate/` — Speicherüberschreitung (65 MiB), langsame Health-Antwort
      (p95 120 ms), eine Nicht-200-Probe — müssen die Auswertung mit **Exit ≠ 0** und benanntem Grund
      beenden; ein vierter, sauberer CSV mit Exit 0. Das ist Teil des Protokolls, nicht optional.
- [ ] Protokoll mit Zahlen (1-s- und 30-s-Seed, inkl. Negativnachweis) als Abschnitt in diesem Plan
      („B6-Protokoll") — Commit `docs: record the dashboard concurrency gate run` +
      `chore(scripts): add the dashboard concurrency gate scripts`.

### B7 — Gates B, Coverage, Live-Verifikation, Doku, Codex, PR (`opus`)

Wie A7, zusätzlich: B5 opt-in gefahren (Zahlen im PR-Body), B6-Protokoll vorhanden, Live-Verifikation
8.2, `CLAUDE.md` Statustabelle (Dashboard komplett, Zählbeginn, Ranges), `docs/Operations.md`
Abschnitt „Admin dashboard" (englisch: was die Seite zeigt, welche Ranges aus welcher Tabelle,
Cache-/Poll-Takte, `SystemLoad:IntervalSeconds`-Folgen) — Doku-Commit `docs: describe the admin
dashboard in operations and the status table`. Codex-Zweitmeinung, PR, kein Merge.

## 8. Live-Verifikation (Regel 16), lokal, je Schnitt

Stack: `docker compose up -d postgres redis`; `dotnet run --project src/EmotePurge.Api`;
`dotnet run --project src/EmotePurge.Worker`; `npm --prefix web start`; Admin-Login per Twitch
(`Auth:AdminTwitchUserIds` in User-Secrets). Vor E2E/Audit die Api wieder beenden.

### 8.1 Schnitt A (in A7)

| Handgriff | Erwartete Beobachtung |
|---|---|
| `curl -b <cookie> 'http://localhost:5151/api/admin/dashboard/activity?range=30d'` | 200, 30 `days`, heutiger Tag letzter Eintrag; Tage ohne Poller-Lauf `null`; Zeit < 50 ms (Api-Log) |
| dieselbe Abfrage zweimal binnen 60 s | zweite ohne DB-Query (EF-Log `Executed DbCommand` fehlt) |
| `?range=7d` | 400 `{"errorCode":"invalid_dashboard_range"}` |
| ohne Cookie / als Nicht-Admin | 401 / 403 |
| `/api/admin/rate-limits/trend` | 200, 1.440 Minuten, `localRejections` überwiegend 0 |
| Ablehnung erzeugen: `ChannelResync`-Policy (5/min) mit sechs `POST /api/channels/<kanal>/resync` | sechster → 429; binnen 60 s zeigt die Seite die Policy oben in „Ausnahmen" mit Anteil 1/6 ≈ 16,7 %, Trend-Chart hat einen Punkt in der aktuellen Minute; Dialog nennt die letzte Ablehnung |
| `docker compose stop redis` → Seite neu laden → `start` | Warnbanner „Telemetrie nicht verfügbar", Konfiguration im Dialog sichtbar, Health-Abschnitt unbeeinflusst (Health braucht Redis: Fehlerbanner dort, aber Aktivität bleibt) |
| Tab in den Hintergrund, 2 min, zurück | Netzwerk-Panel: keine Requests im Hintergrund, einer sofort bei Rückkehr |
| `/api/admin/roster` | `ceilings.maxActiveChannels` = 80 (Default) |
| de/en, hell/dunkel | Achsentitel umbrechen nicht, Farben nur Token |

### 8.2 Schnitt B (in B7)

Api und Worker mit `SystemLoad__IntervalSeconds=1` laufen lassen (≥ 15 min), danach wieder Default.

| Handgriff | Erwartete Beobachtung |
|---|---|
| `?range=1h` | `sourceKind=raw`, `bucketSeconds=60`, erste Buckets fehlen (keine führenden Lücken), Api- und Worker-Prozess-CPU > 0, Host-CPU %, `provenance` meist `AW` |
| `?range=24h` | `bucketSeconds=300`, Antwort < 256 KiB (`curl -w %{size_download}`), Zeit uncached < 500 ms (Api-Log) |
| `?range=7d` nach ≥ 1 h + Rollup (Worker-Log `SystemLoadRollupWorker`) | `sourceKind=hourly`, Stundenbuckets, `chatLines` Summe je Stunde; davor `available=false` **oder** Teilreihe mit `dataThroughUtc` |
| Worker stoppen 5 min, wieder starten | Lücke in Worker-Reihen (keine Null-Linie), `liveChannels` nach Start `null` bis zum ersten Poll, Host-Reihe läuft über Api weiter mit `provenance="A"` |
| `cpu.max` lokal nicht gesetzt (`dotnet run`) | `cgroupCpuQuotaCores=null`, Quota-Anteil-Diagramm zeigt „keine endliche Quota", Kerne sichtbar; im Container (`docker compose up --build api` mit `cpus: 1.0`) Quota 1,0 und Anteil in % |
| Range-Wechsel `24h → 13mo` | 11 Diagramme bleiben, Achsentitel „höchster Stunden-P95", Rate-Limit-Reihe verschwindet |
| drei Tabs gleichzeitig öffnen | Api-Log: ein Aufbau je Range/TTL |
| Api kurz stoppen | Abschnitt veraltet-markiert, letzte Kurven stehen; nach Start nächster Poll ersetzt |

## 9. Annahmen dieses Plans (Spec schweigt; markiert, nicht still gesetzt)

- **A-1** `liveChannels = null` für Tage ohne jede `ChannelLiveDays`-Zeile (keine Beobachtung), `0`
  nur bei vorhandenen Zeilen ohne `LiveMinutes > 0`. Begründung: Spec §2.2 („absent row = no data,
  never offline") und §4.1 („Lücken werden nicht interpoliert"); ein Poller-Ausfall darf nicht als
  „0 live" erscheinen.
- **A-2** Chart-Interaktion ohne Hover-Tooltip-Primitiv: fokussierbarer Lesecursor mit Textzeile
  (`aria-live`), Maus setzt denselben Cursor. Begründung: Spec §10 verlangt Tastaturbedienbarkeit
  und Textalternative; ein Tooltip-Primitiv existiert nicht und wäre eine zweite Baustelle.
- **A-3** `AdminRosterCard.roster` wird öffentlich; die Seite liest es per `viewChild`. Begründung:
  Spec §4.1 „nicht ein zweites Mal laden"; Alternative (Resource in die Seite heben) würde die Card
  umbauen.
- **A-4** Der Stream-Vertragstest (`usage.flushed` nicht in `AdminTypes`) wird im bestehenden
  `LiveEventTests` ergänzt, nicht in `Api.Tests` neu angelegt. Spec §11 nennt `Api.Tests`; der
  bestehende Test liegt in Infrastructure.Tests und prüft genau diese Mengen.
- **A-5** `docs/UI-Designsprache.md` bekommt einen Unterabschnitt für das Chart-Primitiv als
  Ist-Stand nach der Umsetzung (A5/B3), nicht vorab.
- **A-6** `ceilings.maxActiveChannels` wird additiv ergänzt (Abschnitt 10, Punkt 1).
- **B-1** `503` bei besetztem Neuaufbau ohne Body, nur `Retry-After: 5`. Spec §6/§8 nennen keinen
  Fehlercode; ein neuer Code wäre eine Vertragserweiterung über die Spec hinaus.
- **B-2** Das Quota-Anteil-Diagramm bleibt in Hourly-Ranges als Diagramm mit Hinweis stehen, damit
  die Diagrammzahl konstant bleibt (Spec §10, §5.2 „zeigt nur Kerne" vs. „Zahl der Diagramme ändert
  sich nicht").
- **B-3** Das Response-Performance-Gate ist ein opt-in-xUnit-Test (`EMOTEPURGE_PERF_GATES=1`),
  das Parallelitäts-Gate ein Skriptprotokoll; beides läuft nicht in CI.
- **B-4** `AdminDashboardLoad.BucketStartsUtc` lässt führende leere Buckets weg (Spec §3
  „Rangeanfang auf den ersten vorhandenen Bucket"), innere Lücken bleiben als `null`; die Range selbst
  wird dadurch nur kürzer, nie länger (R2).
- **B-5 (R2)** Tie-Regel der Host-Provenienz je Metrik: gleicher P95 → höheres Max → `Worker`.
  Spec §5.1 nennt die Gewinnerquelle, aber keine Regel für Gleichstand.
- **B-6 (R2)** Das Lastmenü wird über eine clientseitige `13mo`-Sondierung bei leerem Default
  freigeschaltet, nicht über ein neues Antwortfeld; bleibt im Spec-Vertrag §6.
- **B-7 (R2)** B5 misst in-process (Allokation prozessweit, Zeilenzähler), die HTTP-Hälfte misst B6
  im Container; `Api.Tests` bleibt container-frei.
- **B-8 (R2)** B6 misst `VmHWM` über `clear_refs`, mit 50-ms-`VmRSS`-Sampling als gekennzeichnetem
  Fallback; `docker stats` nur als Zusatzspalte.
- **B-9 (R2)** Die harte 400-Bucket-Kappe im Service ist ein Programmierfehler-Schutz (500), kein
  Nutzerfehler — sie darf bei korrekter Leiter nie auslösen.

## 10. Spec/Code-Widersprüche (gefunden beim Planen)

1. **`ceilings` enthält den Admission-Cap nicht.** Spec §4.1 will „`trackedChannelCount` aus dem
   bestehenden Roster sowie der konfigurierte Wert aus `ceilings`" als Ist/Admission-Grenze; die
   `ceilings` des Rosters sind `WorkerCapacity`-Konstanten (100 Twitch-Decke, 20 Join-Budget),
   `Channels:MaxActiveChannels` (80) fehlt. **Auflösung (A3):** additives Feld `maxActiveChannels`
   aus `ChannelCapacityOptions`; DECISIONS nennt es. Keine Änderung an den bestehenden zwei Feldern.
2. Spec §2.1 sagt, die Seite aktualisiere Health **und** Rate-Limits im 30-s-Takt; tatsächlich ist
   Health SSE-getrieben (`worker.health`), nur der Rate-Limit-Snapshot pollt 30 s. Für den Plan
   unerheblich; der Poll wird auf 60 s gesetzt (Spec §8).
3. Spec §11 A verortet den Stream-Vertragstest in `Api.Tests`; er existiert in
   `Infrastructure.Tests/Unit/LiveEventTests.cs` (Annahme A-4).
4. Spec verweist auf einen „#385-DECISIONS-Eintrag"; `docs/DECISIONS.md` hat nur den #380-Eintrag vom
   2026-10-11 (#385 ist der PR). Inhaltlich deckungsgleich.
5. `SystemLoadHourly` wird in der Spec als Tabellenname genutzt; das EF-DbSet heißt
   `SystemLoadHourlies` — nur für Implementer relevant.
6. `docker-compose.prod.yml`/`docker-compose.yml` haben **keinen** `healthcheck`-Block am `api`-Service
   (der Container-HEALTHCHECK liegt im Image); das B6-Gate prüft deshalb `GET /api/health` direkt.
7. **(R2, spec-intern, kein Widerspruch zum Code)** `13mo` ist in der Spec zweimal verschieden
   definiert: Aktivität ab Monatserstem (§6), Last rollend ≈ 395 Tage (§5.2, §3). Plan v1 hatte beides
   fälschlich auf den Monatsersten gezogen; der Plan folgt jetzt beiden Definitionen getrennt. Die
   Fenster stimmen damit am Anfang um bis zu 30 Tage nicht überein, obwohl §4.1 die Ranges „zum
   Vergleich" gleich benennt — Darstellungsfrage, kein Vertragsbruch; nur zur Kenntnis.

## 11. Offene Punkte für den Betreiber

**Keine.** Alle Annahmen in Abschnitt 9 sind mit Begründung gesetzt und können beim Plan-Review
umgestoßen werden; Punkt 1 in Abschnitt 10 ist additiv und ändert keinen bestehenden Vertrag.

## B6-Protokoll (Parallelitäts-Gate unter Prod-Grenzen, 2026-10-12)

**Ergebnis: bestanden, für beide Seeds (1 s und 30 s), alle Kriterien (a)–(e).** Branch
`feat/381-admin-dashboard-b`. Herkunft der Zahlen:

- **1-s-Seed:** Lauf vom Vormittag (Api-Image aus `b4fbc721`, Skripte `a2c2bfc5`), nach dem Review mit
  dem korrigierten Auswerter neu ausgewertet (Abschnitt „Nachauswertung“). Nicht neu gemessen.
- **30-s-Seed:** Neulauf am Nachmittag über den Wrapper auf `0a9bd4ba` (Api-Image aus diesem Stand,
  also inklusive des Neuaufbau-Deadlines aus `5fbe857d`), mit exakter Request-Zeitmessung
  (`timing=curl_end`), 8 ms Proben-Offset und 480 Haupttakten.

### Aufbau

- **Wegwerf-Umgebung statt Dev-Stack** (Controller-Ruling R-2): eigenes Docker-Netz, `postgres:16-alpine`
  mit `shared_preload_libraries=pg_stat_statements`, `redis:7.2-alpine`; kein `docker compose up/down`,
  der Dev-Stack (`emotepurge-dev-*`) lief unberührt weiter. Migrationen per `dotnet ef database update`
  gegen die Wegwerf-DB, Image per `docker build -f src/EmotePurge.Api/Dockerfile` unter eigenem Tag.
  Seit `48ce7539` erledigt `scripts/dashboard-concurrency-gate-env.sh` Aufbau, Lauf, Auswertung und
  Abbau (Trap) in einem Aufruf; der 1-s-Lauf lief noch mit denselben Schritten von Hand, der finale
  30-s-Lauf über den Wrapper.
- **Api-Container** mit `--cpus=1 --memory=512m` (im Container geprüft: `cpu.max = 100000 100000`,
  `memory.max = 536870912`), `127.0.0.1:18080`, Produktions-Logging unverändert (EF-Kommandos auf
  Information wie in Prod) plus `Logging__LogLevel__EmotePurge.Infrastructure.Services.AdminDashboardReader=Debug`
  und Simple-Console-Formatter einzeilig mit UTC-Zeitstempel `yyyy-MM-ddTHH:mm:ss.fffZ` (der Parser des
  Messskripts braucht genau das). Der Sampler lief wie in Prod mit, und zwar in der Kadenz des Seeds
  (`SystemLoad__IntervalSeconds` = 1 bzw. 30); das Image bringt seinen eigenen `HEALTHCHECK` (30 s,
  aus dem Container, eigene Rate-Limit-Partition) mit.
- **Seed** `scripts/seed-system-load-samples.sql` (`-v confirm_truncate=yes -v cadence=1|30`): je
  Source 24 h + 6 Tage Vorlauf, zwei Instanzwechsel im Fenster, eine gemeinsame 10-min-Lücke,
  Intervallzähler; dazu 400 Tage `SystemLoadHourly`, damit `13mo` über ein volles Jahr Stundenwerte
  baut. 1 s: 1.208.400 Rohzeilen, davon 171.600 im 24-h-Fenster (wie der B5-Fixture-Seed), 19.200
  Stundenzeilen, ~14 s. 30 s: 40.280 Rohzeilen, 5.720 im Fenster.
- **Worker-Health**: kein echter Worker; das Messskript hält `worker:health:twitch` per `redis-cli SET …
  EX 60` alle 10 s frisch (`isConnected: true`, `twitchLastFrameUtc = jetzt`, Form wie
  `WorkerHealthSnapshot`, `JsonSerializerOptions.Web`).
- **Admin-Cookie ohne Twitch-Login und ohne Produktänderung**: Cookie-Auth ist ein selbsttragendes,
  per Data Protection geschütztes Ticket (Anwendungsname `EmotePurge`, Purpose der Cookie-Middleware,
  Schema `Cookies`). Ein Wegwerf-Werkzeug außerhalb des Repos hat einen Schlüsselring erzeugt und damit
  ein Ticket mit `NameIdentifier`, `twitch:login` und `twitch:session_issued_at` gemintet; derselbe
  Schlüsselring war als `/keys` in den Container gemountet (`DataProtection__KeyPath=/keys`). Dazu eine
  `Users`-Zeile für die Sitzungsprüfung in `OnValidatePrincipal` und `Auth__AdminTwitchUserIds` auf
  dieselbe ID. Ein Browser-Login ist **keine** Alternative: der registrierte Twitch-OAuth-Redirect zeigt
  auf `localhost:5151`, nicht auf den Gate-Container.
- **Lauf** `scripts/dashboard-concurrency-gate.sh`: Haupttakte à 2,5 s (Range-Wechsel `24h`/`13mo` je
  Takt, je Takt drei parallele `curl`), danach dreimal 26 Takte nur Probe (65 s, beide Keys werden kalt)
  und ein Doppeltakt mit 3 × `24h` + 3 × `13mo` im selben Augenblick. **Je Seed verschieden:**
  1 s → 384 Haupttakte, 50 ms Proben-Offset (465 Proben, 19,3 min); 30 s → 480 Haupttakte, 8 ms Offset
  (561 Proben, 23,3 min). Der Offset muss die Probe in einen laufenden Aufbau legen: beim 1-s-Seed
  dauert ein `24h`-Aufbau ≥ 266 ms, beim 30-s-Seed nur 10–55 ms ab Ankunft der Requests — ein erster
  exakt gemessener 30-s-Lauf mit 20 ms und 384 Takten traf nur 27 von 40 nötigen Aufbauten (ungültig),
  deshalb die 30-s-Vorgaben des Wrappers. Speicher und `pg_stat_statements` je Takt bei +2,0 s.
  Auswertung `scripts/dashboard-concurrency-gate-eval.mjs`.
- **Speicher über `clear_refs`, kein Fallback nötig**: `docker exec -u root … echo 5 > /proc/1/clear_refs`
  funktionierte im Image (PID 1 = `dotnet EmotePurge.Api.dll`). Zuwachs je Takt = `VmHWM` seit dem
  letzten Reset − `VmRSS` beim Reset. Zusatzspalte: cgroup `memory.current` (die Zahl hinter
  `docker stats`), nicht gewertet.

### Zahlen

| Kriterium | 1-s-Seed (171.600 Zeilen; Lauf vormittags, neu ausgewertet) | 30-s-Seed (5.720 Zeilen; Neulauf auf `0a9bd4ba`) |
|---|---|---|
| Vollständigkeit (je Takt 1 Probe, 1 Speicher-, 1 Query-Zeile, 3 Load-Zeilen je Range) | 465 Takte, 0 Lücken | 561 Takte, 0 Lücken |
| Zuordnung Request → Aufbau | Legacy-Heuristik (Sendezeit vor `curl`-Start) | exakt (`timing=curl_end`) |
| Gültigkeit: kalte Zyklen je Key (Hauptphase) | `24h` 28, `13mo` 15 | `24h` 35, `13mo` 19 |
| Gültigkeit: Proben im Aufbauintervall (≥ 40) | 45 (31 in `24h`-, 14 in `13mo`-Aufbauten) | **56** (36 / 20) |
| (a) Aufbauten je Zyklus / Log ↔ `pg_stat_statements` | je Zyklus genau 1; 49 Aufbauten = 49 Load-Queries, 0 Abweichungen in 465 Fenstern; alle 1.170 Load-Antworten 200 | je Zyklus genau 1, jeder bediente alle 3 Requests; 60 = 60, 0 Abweichungen in 561 Fenstern; alle 1.458 Load-Antworten 200 |
| (b) `VmHWM`-Zuwachs, Maximum über alle Takte | **6,1 MiB** (Median in Aufbautakten 0,0 MiB; erster Aufbau nach Start 4,3 MiB) | **10,1 MiB** (Median 0,0; erster 2,7 MiB) |
| (b) negative Zuwächse (als 0 gezählt, s. u.) | 75, kleinster −0,6 MiB | 108, kleinster −0,8 MiB |
| (b) Zusatz: `VmHWM` absolut / cgroup `memory.current` max. | 216,4 MiB / 128,1 MiB | 225,1 MiB / 137,3 MiB |
| (c) Health-Proben `200` | 465 / 465 | 561 / 561 |
| (c) p95 der Proben im Aufbau | **3,8 ms** (max. 23,8 ms) | **2,3 ms** (max. 36,2 ms) |
| (d) Doppeltakt: je Key ein Aufbau, Reihenfolge, Gate-Wartezeit des zweiten Keys | seriell (`24h` zuerst, je 3 Requests bedient, alle 200); `13mo` wartete 337 / 339 / 278 ms, Antworten 200 in 431–524 ms, 0 × 503 | seriell, je 3 bedient, alle 200; Wartezeit des zweiten Keys 26 / 167 / 20 ms, Antworten 200 in 179–197 ms, 0 × 503 |
| (e) `24h` uncached Ende-zu-Ende (`time_total`, n = Requests) — **gated** | **Median 300,7 ms**, p95 888,7 ms, max. 1.352,7 ms (n = 84) | **Median 20,9 ms**, p95 47,2 ms, max. 63,7 ms (n = 105) |
| (e) `13mo` uncached — nur berichtet | Median 172,7 ms, max. 275,1 ms (n = 43) | Median 172,6 ms, max. 325,3 ms (n = 57) |
| (e) Antwortgröße max. | `24h` 97.739 B, `13mo` 140.003 B (≤ 262.144) | `24h` 95.982 B, `13mo` 138.471 B |
| Zusatz: Aufbauzeit laut Log (Median / erster) | `24h` 298 / 1.323 ms; `13mo` 169 / 237 ms | `24h` 17 / 44 ms; `13mo` 167 / 302 ms |

Lesart:

- **(d) misst die Wartezeit auf das Aufbau-Gate**, nicht die gesamte Antwortzeit (B2-Review): die
  `5 s` der Spec sind das `WaitAsync(5 s)` der Fassade. Gemessen als Aufbaubeginn des zweiten Keys minus
  Sendezeit seiner Requests. Die Antwortzeit des zweiten Keys ist Wartezeit plus eigener Aufbau; sie lag
  in allen sechs Doppeltakten unter 0,6 s, also auch unter der strengeren Lesart.
- **(a) Single-Flight**: in 42 der 43 Hauptzyklen (1 s) bediente der eine Aufbau alle drei Requests. In
  Takt 77 (`13mo`) startete der dritte `curl` rund 160 ms nach den beiden anderen — Prozessstart unter
  Last auf der Devbox, nicht im Api-Container —, kam damit erst nach dem TTL-Ablauf an und löste den
  Aufbau aus; die beiden ersten bekamen noch den gültigen Cache-Wert (14 / 23 ms). Eine Query für den
  Takt, kein Doppelaufbau. (Erste Fassung des Protokolls: „TTL lief zwischen den Ankünften ab“ — die
  Ursache der späten Ankunft war der Spawn-Verzug, s. u.) Im finalen 30-s-Lauf bediente jeder der 54
  Aufbauten alle drei Requests.
- **(e) gated nur `24h`**, und zwar den **Median**, wie B5. Die Ausreißer sind die ersten beiden
  `24h`-Aufbauten nach Containerstart (1.323 und 882 ms, Tiered-JIT-Aufwärmphase unter einer CPU);
  danach lagen alle `24h`-Aufbauten bei 266–441 ms. Ein Admin, der direkt nach einem Deploy die Seite
  öffnet, sieht die erste `24h`-Antwort also nach gut einer Sekunde, nicht nach 0,3 s. Mit drei Clients
  serialisiert jeder Request die Antwort selbst; das ist in den 300,7 ms enthalten. `13mo` wird nur
  berichtet.
- **(b) Speicher**: die Rohzeilen werden gestreamt, der Zuwachs bleibt auch beim allerersten Aufbau im
  einstelligen MiB-Bereich — weit unter 64 MiB; die Budgetannahme der Spec (EF-Materialisierung, ~16 MiB
  Nutzdaten × 4) wird vom Typed-Reader-Pfad nicht annähernd erreicht. **Negative Zuwächse** (16–18 % der
  Takte, höchstens −1,1 MiB) sind echt und kein Messfehler des Skripts: die RSS-Zähler des Kernels sind
  per-CPU-Näherungen, `VmHWM` kann später ein paar hundert KiB unter dem direkt nach dem Reset gelesenen
  `VmRSS` liegen. Der Auswerter zählt sie als 0 und erklärt alles unter −4 MiB zur kaputten Messung.
- **30 s ist erwartungsgemäß Formsache** bei den Schwellen; schwierig ist dort nur die Gültigkeit
  (Proben im Aufbau), weil die Aufbauten so kurz sind — s. Lauf-Absatz oben.

### Nachauswertung nach dem Review (Auswerter `48ce7539`)

Das Review fand, dass der erste Auswerter kaputte oder fehlende Messungen durchließ (leerer
Speicher-Snapshot → großer negativer Zuwachs → „bestanden“; keine Vollständigkeitsprüfung) und Requests
über ihre Sendezeit einem Aufbau zuordnete. Korrigiert: Vollständigkeit gegen den Taktplan (Meta-Zeile),
kaputte Speicher-Snapshots → ungültig (Exit 2), Doppeltakt prüft auch den ersten Key, Zuordnung nur bei
nachweislich bedienten Requests.

Die Roh-CSVs beider Läufe lagen noch vor; sie haben keine Meta-Zeile, die wurde mit den tatsächlichen
Laufparametern (`clients=3; main_ticks=384; cooldown_ticks=26; dual_cycles=3`) nachgetragen. Ihre
Request-Zeiten stammen aus einer Uhr, die **vor** dem Start von `curl` gelesen wurde und dadurch um den
Spawn-Verzug zu früh liegt (typisch ~5 ms, unter Last bis ~160 ms). Für solche Alt-CSVs nutzt der
Auswerter eine ausdrücklich so benannte Heuristik („legacy attribution“: Request vor Aufbaubeginn
gesendet, Aufbau spätestens 200 ms nach dem notierten Ende begonnen, `time_total` ≥ halbe Aufbaudauer).
Neue Läufe notieren `end_ms` nach `curl` und rechnen `start = end − time_total` (`timing=curl_end`); dort
gilt die strenge Regel „in Flug, als der Aufbau endete“.

Ergebnis: beide Alt-Läufe weiter **bestanden**, 0 Vollständigkeitslücken; im alten 30-s-Lauf wurde ein
`24h`-Aufbau (13 ms) zusätzlich zugeordnet, den die erste Auswertung still übersprungen hatte. Bekannte
Unschärfe der Alt-CSVs: die Proben-Sendezeiten liegen ebenfalls um den Spawn-Verzug zu früh. Beim
1-s-Seed (Aufbauten ≥ 266 ms bei 50 ms Offset) ändert das an „Probe im Aufbau“ nichts — die 1-s-Zahlen
oben bleiben deshalb die neu ausgewerteten des Vormittagslaufs. Beim 30-s-Seed war die Vermutung richtig:
die Legacy-Zahl 43 war zu hoch. Ein Neulauf mit exakter Zeitmessung und den alten Vorgaben (20 ms, 384
Takte) traf nur **27** Proben im Aufbau → ungültig (Exit 2), alle Schwellen sonst eingehalten. Mit 8 ms
und 480 Takten kamen 54 (Lauf auf einem Arbeitsstand kurz vor `5fbe857d`) und im finalen Lauf auf
`0a9bd4ba` **56** Proben — das sind die 30-s-Zahlen der Tabelle; der alte 30-s-Lauf ist damit ersetzt.

### Negativnachweis

`node scripts/dashboard-concurrency-gate-eval.mjs scripts/fixtures/dashboard-gate/<datei>.csv`:

| Fixture | Exit | Benannter Grund |
|---|---|---|
| `clean.csv` | 0 | `GATE PASSED` |
| `cached-ticks-slow-probe-outside.csv` (50 gecachte Takte, eine 300-ms-Probe außerhalb jedes Aufbaus) | 0 | `GATE PASSED` — gecachte Takte und Proben außerhalb zählen nicht für p95/uncached |
| `memory-65mib.csv` | 1 | `(b) memory increment 65.0 MiB at tick 20 exceeds 64 MiB` |
| `health-p95-120ms.csv` | 1 | `(c) health probe p95 inside rebuilds 120.0 ms is not below 100 ms` |
| `health-non-200.csv` | 1 | `(c) health probe at tick 37 answered 429` |
| `dual-first-key-500.csv` | 1 | `(d) dual tick 50: first key 24h answered 500` |
| `memory-missing.csv` | 2 | `broken memory snapshot at tick 12: VmHWM '', VmRSS base ''` |
| `memory-negative.csv` | 2 | `broken memory snapshot at tick 12: increment -50.0 MiB is below the -4 MiB counter jitter` |
| `missing-probe-row.csv` | 2 | `incomplete measurement: tick 30: 0 probe rows` |
| `missing-second-key-rows.csv` | 2 | `incomplete measurement: tick 50: 0 of 3 13mo load rows` |
| `memory-jitter-1mib.csv` (−1 MiB in einem Takt) | 0 | `GATE PASSED` — innerhalb der −4-MiB-Zählerschwankung, als 0 gezählt |
| `memory-jitter-5mib.csv` (−5 MiB) | 2 | `broken memory snapshot at tick 12: increment -5.0 MiB is below the -4 MiB counter jitter` |
| `load-missing-size.csv` (200 ohne Größe) | 2 | `broken load row at tick 8: 24h answered 200 with size ''` |

Exit 2 = Lauf ungültig (unvollständig, kaputte Messung, zu wenige Zyklen oder Proben im Aufbau) und
geht vor Exit 1; 64 = Eingabefehler. Das Messskript selbst bricht mit Exit 4 ab, wenn ein Speicher- oder
`pg_stat_statements`-Snapshot keine Zahlen liefert, und mit Exit 5, wenn keine Aufbau-Logzeilen
gefunden werden; gemessene Zeilen bleiben in beiden Fällen erhalten (geprüft gegen Attrappen-Container).

### Reproduzieren

```
CADENCE=1  GATE_ADMIN_COOKIE_FILE=<datei> DP_KEYRING_DIR=<schlüsselring> ADMIN_TWITCH_USER_ID=<id> \
  scripts/dashboard-concurrency-gate-env.sh      # PROBE_OFFSET_MS default 50
CADENCE=30 GATE_ADMIN_COOKIE_FILE=<datei> DP_KEYRING_DIR=<schlüsselring> ADMIN_TWITCH_USER_ID=<id> \
  scripts/dashboard-concurrency-gate-env.sh      # PROBE_OFFSET_MS default 8, MAIN_TICKS default 480
```

Der Wrapper baut Netz, Postgres, Redis und Api-Image unter eigenem Präfix auf, migriert, seedet, legt die
`Users`-Zeile an, startet die Api mit `--cpus=1 --memory=512m`, misst, wertet aus und entfernt beim
Verlassen (auch bei Abbruch) Container, anonyme Volumes, Netz und Image. Passwörter und Cookie stehen in
keiner Kommandozeile. Das Cookie-Ticket muss vorher mit demselben Schlüsselring gemintet werden (Kopf des
Wrappers). Die CSV landet unter `${TMPDIR:-/tmp}`. **Live geprüft:** ein 1-s-Smoke-Lauf (24 Haupttakte, ein
Doppeltakt; Migration über die Umgebungsvariable, Seed, Cookie-Auth, Messung, Auswertung — Exit 2 wie
erwartet, weil 24 Takte zu wenige Zyklen liefern) und drei volle 30-s-Läufe; nach jedem Lauf waren keine
Container, Netze, Volumes oder Images mit dem Präfix übrig; den Dev-Stack fasst der Wrapper nicht an.
