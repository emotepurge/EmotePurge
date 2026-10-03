# Broadcaster-Selbstbereinigung mit DB-Sperre und Admin per Twitch-ID (#245, Epic #248 D) — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Der Task bekommt diesen Plan und das Konzept
> [`docs/Konzept-Broadcaster-Selbstbereinigung-2026-10-03.md`](../../Konzept-Broadcaster-Selbstbereinigung-2026-10-03.md)
> (Entscheidungen E1–E12 in Abschnitt 4, Review-Zuordnung in Abschnitt 7); er rollt die
> Entscheidungen **nicht** neu auf. Schritte sind als Checkbox (`- [ ]`) geführt. **Kein fertiger
> Code in diesem Plan** — Verträge, Namen, Grenzfälle, Testfälle und Reihenfolge ja, Rümpfe nein.
>
> Arbeitsort: Worktree `/home/dev/projects/EmotePurge-245`, Branch `feat/245-broadcaster-self-purge`,
> Basis `origin/main` (Konzept-Commits `9cf20921`, `72ad9e9a`, `d68f2d42` liegen schon darauf). Kein
> Push, bis der Orchestrator es sagt. **Kein `docker compose up` aus dem Worktree** (die `.env` liegt
> nur im Haupt-Checkout; Memory „compose aus dem Worktree reißt den Stack ab"), kein Neustart des
> Dev-Workers vor dem 08.10. (Messfenster #69/#73). `cd` hält nicht zwischen Shell-Aufrufen — immer
> absolute Pfade.
>
> **`web/node_modules` fehlt im Worktree** (verifiziert 2026-10-03). T0 legt es an; jeder spätere
> Task prüft `ls /home/dev/projects/EmotePurge-245/web/node_modules/.bin/ng`, bevor er Frontend-Gates
> fährt.

**Sprache:** Plan deutsch (Denkwerkzeug). Neuer Code, Kommentare, Log-/`throw`-Meldungen,
Commit-Messages, Issue-Texte und **neue** `docs/DECISIONS.md`-Einträge englisch (CLAUDE.md „Sprache").
Commit-Trailer laut Sitzungs-Vorgabe des Orchestrators.

**Quelle der Wahrheit:** das Konzept in seiner dritten Fassung (2026-10-03) mit allen gefallenen
Entscheidungen. Was dort steht, ist entschieden; was diesem Plan beim Bauen widerspricht, gehört in
Abschnitt 8 „Offene Punkte für den Betreiber", nicht in eine stille Entscheidung des Tasks.

**Nicht im Umfang** (Konzept 1, 3.2, 3.8): ob ein Mod ohne Broadcaster joinen darf; Datenexport;
die Harness-Sperre (#260); der Fremdkanal-Preview (3.7 b); die ID-Bindung der Audit-Einträge
(Folgearbeit, im DECISIONS-Eintrag notiert); das spätere Entfernen von `Auth:AdminTwitchLogins`
(eigener kleiner Schritt, sobald Prod auf IDs läuft); Code für Restore/Rollback (E10/E12: nur
Doku); jede Änderung an `PurgeAsync` (Admin-Purge bleibt ungesperrt, schreibt keine Sperre).

**Zeitliche Schranke (E8):** Bau jetzt; **Deploy von Api und Worker erst nach dem bindenden
#69-Lauf, nicht vor dem 2026-10-08** (Abschnitt 6). Bis dahin trägt der E-Mail-Weg aus § 9.

---

## 0. Befund (verifiziert am 2026-10-03 im Worktree)

Jede Zeilenangabe ist gegen `d68f2d42` geprüft. Pfade sind relativ zum Worktree-Root.

### 0.1 Backend — was existiert

| Stelle | Befund, auf dem der Plan aufsetzt |
|---|---|
| `src/EmotePurge.Core/Services/IChannelService.cs` | `ChannelJoinStatus` = `Joined, ChannelNotOnTwitch, CapacityReached, ChannelExcluded` (`:28-50`); `ChannelJoinResult` ist `sealed`, privater Konstruktor, Fabriken `Joined(channel)`/`Failed(status)` mit `Enum.IsDefined`-Prüfung (`:65-100`); `JoinAsync(channelName, actor, isGlobalAdmin = false, ct)` (`:133`); `PurgeAsync` (`:142`), `PurgeIfInactiveSinceAsync` (`:151`, Ergebnis `ChannelRetentionPurgeResult`), `GetByNameAsync` (`:154`), `ListActiveChannelNamesAsync` (`:169`, Doku: lässt env-gesperrte IDs aus) |
| `src/EmotePurge.Infrastructure/Services/ChannelService.cs` | Konstruktor `:12-18` (`AppDbContext`, `IRedisPublisher`, `IChannelIdentityService`, `ChannelCapacityOptions`, `IExcludedChannelFilter`, `ILogger`). `JoinAsync` `:24-90`: Helix-Lookup **vor** der Transaktion (`:30`), Transaktion `:38`, env-Gate auf die Identität `:63`, `ResolveJoinTargetAsync` `:69`, env-Gate auf die gewählte Zeile `:83`, dann `CompleteJoinAsync` `:459-534` (Deckel `:474-492` **vor** jedem Schreiben, Reaktivierung `:494-509`, Audit `channel.join` `:514`, `SaveChanges` + Commit `:516-519`, Publishes **nach** dem Commit `:521-531`). `HandleUnknownTwitchLoginAsync` `:272-325` (NotFound-Pfad, eigenes env-Gate `:290`). `ResolveOrCreateChannelByNameAsync` `:408-451`: **id-lose Zeile entsteht bei `:419`** (`TwitchChannelId = identity?.Id`). `PurgeAsync` `:124-153`: LEAVE **vor** dem Schreiben, Audit ohne Details, ungesperrt. `PurgeIfInactiveSinceAsync` `:155-196`: Transaktion, `LoadChannelForUpdateAsync`, Audit mit `{ reason = "retention" }` (Konstante `:22`). `ListActiveChannelNamesAsync` `:203-226`: filtert env-IDs **in-memory** (`:223`) |
| `src/EmotePurge.Infrastructure/Persistence/ChannelQueries.cs` | `LoadChannelAsync` `:26`, `LoadChannelReadOnlyAsync` `:36`, `LoadChannelByIdAsync` `:70`, `LoadChannelByTwitchIdAsync` `:81`, **`LoadChannelForUpdateAsync` `:106`**, **`LoadChannelByTwitchIdForUpdateAsync` `:120`** (Doku `:116`: wer zwei Zeilen sperrt, sperrt in Merge-Reihenfolge). Beide `FOR UPDATE`-Helfer verlangen eine offene Transaktion (`ChannelRetentionPurgeTests.ChannelRowLocks_RefuseToRunOutsideATransaction`) |
| `src/EmotePurge.Infrastructure/Persistence/AuditLogWrites.cs:21-28` | `db.AddAuditEntry(actor, action, channelName?, targetType?, targetId?, details?)` — Details als anonymes Objekt (Präzedenz `{ reason = … }`) |
| `src/EmotePurge.Core/Services/AuditActor.cs:10` | `record AuditActor(string TwitchUserId, string Login)`; `AuditActor.System` existiert |
| `src/EmotePurge.Infrastructure/Services/IExcludedChannelFilter.cs:12-20` + `ExcludedChannelFilter.cs` | `bool IsExcluded(string? twitchChannelId)`, synchron, `FrozenSet`, Singleton (`ServiceCollectionExtensions.cs:74`), loggt beim Konstruieren die Anzahl (`:33`), liest Skalar-vor-Array (`:42-57`). **Das Interface liegt in Infrastructure, nicht in Core** — Präzedenz für ein rein infrastrukturinternes Interface |
| `src/EmotePurge.Infrastructure/Services/ChannelDeactivation.cs:33-58` | `DeactivateAsync(db, redisPublisher, channel, actor, forExclusion, ct)`: setzt `IsBotActive=false`, `DeactivatedAtUtc`, schreibt `channel.leave` (mit Name, oder bei `forExclusion` **ohne** Name mit `{ reason = "excluded" }`), `SaveChanges`, LEAVE-Publish. **Kein `reason`-Parameter, keine Transaktion** — T4 erweitert |
| `src/EmotePurge.Infrastructure/Services/ChannelIdentityService.cs` | Konstruktor `:26-33` (u. a. `AppDbContext`, `ITwitchHelixClient`, `ITwitchAppTokenProvider`, `IRedisPublisher`, `ChannelIdentityWarningState`, `IExcludedChannelFilter`). `ReconcileActiveChannelsAsync` `:35-169`: Snapshot **nur aktiver** Zeilen (`:43`), **unbedingter env-Pass auf gespeicherte IDs `:60-80`** (vor Token/Helix), danach Known-ID-Pass (`ReconcileKnownIdRowAsync` `:204`, env-Gate `:228`) und id-loser Pass (`ReconcileIdLessRowAsync` `:313`, env-Gate `:346`). `DeactivateExcludedRowAsync` `:393-491`: lädt per **Primärschlüssel ohne Zeilensperre**, prüft erneut, schreibt ggf. die ID auf eine id-lose Zeile, verlässt sich auf den `DbUpdateException`-Catch. `MergeAsync` `:562-773` sperrt beide Zeilen `FOR UPDATE`. `LookupByLoginAsync` `:171-202` |
| `src/EmotePurge.Core/Services/IChannelIdentityService.cs` | `TwitchUserLookupStatus` = `Found, NotFound, Unavailable` (`:12-17`); `TwitchUserLookup.User` non-null ⇔ `Found`; `LookupByLoginAsync(login, ct)` (`:128`) |
| `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs` | Konstruktor-Abhängigkeiten `:13-17` (u. a. `IExcludedChannelFilter`); env-Gates auf die **gespeicherte** ID in `SyncChannelAsync` `:51-53` und `ApplyEmoteSetUpdateAsync` `:169-171`; Backfill `:98`; **`ResolveTwitchUserIdAsync` `:345-391`: env-Gate auf die über 7TV aufgelöste ID `:369-372`, vor der Duplikat-Suche `:380` und vor dem Backfill**; `RefuseExcludedChannel(channel)` `:266-270` (räumt Match-Cache, Debug-Log ohne Namen) |
| `src/EmotePurge.Infrastructure/Services/ChannelAccessService.cs` | Konstruktor `:9-14` (`IModeratorCheckService`, `ISevenTvEditorService`, `IChannelService`, `IConfiguration`, `ILogger`). `CanManageChannelAsync` `:16-32` (admin → broadcaster → mod). **`IsGlobalAdmin` `:65-69`: Login-Vergleich, case-insensitive, gegen `GetAdminLogins` `:79-85` (Skalar `Auth:AdminTwitchLogins` schlägt Array)**. `IsBroadcaster` **privat** `:93-115`: id-lose Zeile → Login-Fallback (`:95-97`); ID ≠ Principal-ID bei gleichem Login → Warnung + false |
| `src/EmotePurge.Core/Services/IChannelAccessService.cs` | `record TwitchPrincipalInfo(TwitchUserId, TwitchLogin, AccessToken?)` (`:6`); `CanManageChannelAsync` `:10`, `CanViewUsageStatsAsync` `:15`, `IsGlobalAdmin(principal)` `:19` |
| `IsGlobalAdmin`-Aufrufer | `Api/Auth/GlobalAdminAuthorizationFilter.cs:16`, `Api/Endpoints/AuthEndpoints.cs:152` (`GET /api/auth/me` → `isGlobalAdmin`), `Api/Endpoints/ChannelEndpoints.cs:101` (`/permissions`) und `:153` (Join-Handler) — genau die vier aus Konzept 3.8; alle bleiben unverändert |
| `src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs` | neun `DbSet`s (`:8-16`); `Channel`: Unique-Index auf `ChannelName` und `TwitchChannelId` (`:22-23`); Kaskaden Channel→Emote→UsageStat/Vote, Channel→ChannelLiveDay, Channel→VoteSession→Vote/VoteSessionEmote (`:35,49,65,80,91,98,109,114`); `Vote→User` Restrict (`:121`); `AuditLogEntry`-Index `(ChannelName, OccurredAtUtc)` (`:151`) |
| `src/EmotePurge.Infrastructure/Migrations/` | jüngste Migration `20260923194321_AddRetentionTimestamps` (+ `.Designer.cs`, `AppDbContextModelSnapshot.cs`). `PendingMigrationGuardTests`/`RetentionTimestampBackfillMigrationTests` zeigen das Muster für Migrationstests |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` | `IExcludedChannelFilter` Singleton `:74`, `IChannelService` Scoped `:76`, `IChannelIdentityService` Scoped `:81`, `IChannelAccessService` Scoped `:233` — Api **und** Worker rufen nur diese Methode |
| `src/EmotePurge.Worker/Worker.cs` | LEAVE-Handling `:58-71` (Match-Cache, EventAPI-Unsubscribe, IRC-Part); Boot-Recovery `:111` und JOIN/RESYNC-Guard `:169` über `ListActiveChannelNamesAsync`; dito `SevenTvPeriodicResyncWorker.cs:59`, `TwitchLivePollWorker.cs:57`. **Der Worker selbst ändert sich in #245 nicht** — alle vier Lesestellen liegen in Infrastructure, der Worker trägt sie nur im Image mit |
| Konfiguration | `src/EmotePurge.Api/appsettings.json:44` `"AdminTwitchLogins": [ "sensitron" ]`; `.env.example:8` `ADMIN_TWITCH_LOGINS=`, `:33` `EXCLUDED_CHANNEL_IDS=`; `docker-compose.yml:70` / `docker-compose.prod.yml:71` `Auth__AdminTwitchLogins=${ADMIN_TWITCH_LOGINS}` (nur `api`); `README.md:65,104`; `PRODUCT.md:52` (Rollentabelle); `docs/Architectur.md:130` (B.3-Rollenquellen), `:153` (Endpoint-Matrix `/api/channels`), `:210,227`; `docs/Operations.md:382` |

### 0.2 Api — was existiert

| Stelle | Befund |
|---|---|
| `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs` | Gruppe `/api/channels` mit `RequireAuthorization()` + `ChannelNameValidationFilter` (`:16-18`). `GET /{name}/audit-log` `:45-70` (Filter `ChannelManagementAuthorizationFilter`, Policy `Bookkeeping`; Kanal nur aus der Route). `GET /{name}/permissions` `:78-109` (**ohne** Autorisierungsfilter, `InteractiveRead`, baut `ChannelPermissionsDto` `:336-341` = `CanManage, CanViewUsageStats, IsGlobalAdmin, IsTracked, IsBotActive`). `POST /{name}/join` `:135-207`: `isGlobalAdmin` aus `IsGlobalAdmin(principal)` `:153`, Switch über `ChannelJoinStatus` mit `#pragma warning disable CS8524` `:165-200` (CS8509 bleibt scharf — **ein neuer Enum-Wert bricht den Build, bis der Arm da ist**), 403 mit Code per `Results.Json(…, statusCode: 403)` `:186-187`. `DELETE /{name}` `:265-284`, `DELETE /{name}/purge` `:292-317` (`GlobalAdminAuthorizationFilter`, `Bookkeeping`). Zusatzfeld neben dem Code: `retryAfterSeconds` beim 429 der Resync-Cooldown `:239` |
| `src/EmotePurge.Api/Endpoints/AuthEndpoints.cs:156-228` | `DELETE /api/auth/me`: `string? expectedTwitchUserId` als **Query**; Mismatch → `Results.Conflict(new { errorCode = AccountMismatch })` `:180-182`; `Bookkeeping` |
| `src/EmotePurge.Api/Auth/` | `ChannelManagementAuthorizationFilter.cs` (400 → 401 → 403 per `Results.Forbid()`, Kanal aus `RouteValues["channelName"]`, `IChannelAccessService` aus `RequestServices`), `GlobalAdminAuthorizationFilter.cs`, `UsageStatsAccessAuthorizationFilter.cs`, `VoteAudienceFilter.cs`, `VoteEligibilityFilter.cs`, `ClaimsPrincipalExtensions.cs` (`TryBuildTwitchPrincipal`, `TryBuildAuditActor`), `SessionRejection.cs`, `TwitchClaimTypes.cs` |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | u. a. `ChannelNotFound`, `ChannelNotOnTwitch`, `ChannelCapacityReached`, `ChannelExcluded`, `AccountMismatch` — der Spiegel liegt in `web/src/app/core/i18n/api-error.ts` (`KNOWN_API_ERROR_CODES`, heute 43 Codes) |
| `tests/EmotePurge.Api.Tests/ApiFactory.cs` | substituiert `IChannelAccessService` (`:40`), `IChannelService` (`:44`), `IConnectionMultiplexer` (`:207`) u. a.; `UseSetting("Auth:AdminTwitchLogins", "")` `:167`. `SessionRejectionTests.cs:151` dito |
| `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs` | Anonym-Theory `:55-87` (enthält join/purge/audit-log), unvollständige Claims `:98-106`, Join-Fälle `:143-210` (Admin per `_factory.ChannelAccess.IsGlobalAdmin(...).Returns(true)`), Audit-Log-Namensvalidierung `:442`. Ein Test, der auf `/{name}/audit-log` mit `CanManage=true` durchreicht, bekommt mit T5 (404 ohne Zeile) ein `GetByNameAsync`-Substitut nötig — NSubstitute liefert für `Task<Channel?>` **`null`** (`Channel` ist keine substituierbare Klasse) |

### 0.3 Frontend — was existiert

| Stelle | Befund |
|---|---|
| `web/src/app/core/channels/channel.model.ts:14-20` | `ChannelPermissions { canManage, canViewUsageStats, isGlobalAdmin, isTracked, isBotActive }`; `MyChannelDto` `:31-39` (`isBroadcaster`, `isTracked`, `isBotActive`), `MyChannelsResult` `:41-53` |
| `web/src/app/core/channels/channel.service.ts` | `getPermissions` `:52` (30-s-Cache, `invalidatePermissions` `:83`), `join(channelName)` `:91-93` (`POST …/join`, leerer Body), `leave` `:96`, `purge` `:118`, `listMine` `:123`; Spec `channel.service.spec.ts` (`:141` „join and leave invalidate …", `:159` „join POSTs …") |
| `web/src/app/features/channel-workspace/channel-workspace-layout.ts` | Kopfzeile `:56-80`: `@if (canManage())` → „Channel verlassen" (`appButton="danger"`, `leave()` `:191-217`, `ConfirmDialog`) bzw. „Bot reaktivieren" (`rejoin()` `:219-240`, `rejoinInProgress`); `:88` Inaktiv-Hinweis; Permissions werden in `:279-288` in Signale geschrieben. Datum-Formatierung im Repo: `toLocale()` aus `core/i18n/locale.ts:11` + `toLocaleString` (z. B. `admin-channels-page.ts:478 formatDateTime`) |
| `web/src/app/features/admin/admin-channels-page.ts` | `pendingChannel` `:355`, `join()` `:427` → `runAction` `:506-519` (`actionError` per `apiErrorTranslationKey`), `confirmPurge` `:453-473` mit `TypedConfirmDialogData` (`title/message/requiredText/inputLabel/confirmLabel`; Nachricht nennt `{ channel, emotes, voteSessions }` aus den Admin-Aggregaten) |
| `web/src/app/shared/ui/` | `typed-confirm-dialog.ts` (`TypedConfirmDialogData` `:11-19`, `openTypedConfirmDialog(dialog, data)` `:110`), `confirm-dialog.ts` (`ConfirmDialogData { message, confirmLabel }`, `openConfirmDialog` `:57` → `DialogRef<boolean>`), `account-menu.ts` (`deleteAccount()` `:345-366`: `TypedConfirmDialog` mit `account.delete.*`-Schlüsseln, `requiredText = user.login`, danach `authService.startAccountDeletion(expectedTwitchUserId)`; `deletionNotice` `:247`; Spec `account-menu.spec.ts`) |
| `web/src/app/core/i18n/api-error.ts` | `KNOWN_API_ERROR_CODES` `:10-53`, `apiErrorTranslationKey(error)` liest `error.error.errorCode`; `api-error-locales.spec.ts` erzwingt Code ↔ `errors.api.<code>` in `web/public/i18n/de.json` und `en.json` (Block `:1180-1224`) |
| `web/e2e/support/mocks.ts` | `mockChannelPermissions(page, name, overrides)` `:646-668` (Overrides-Typ muss das neue Feld lernen), `mockPurge` `:616`, `mockMyChannels` `:217`, `mockAdminChannelList` `:261`, `installLiveStub` `:1015`; Specs `channel-workspace.e2e.spec.ts`, `admin-channels.e2e.spec.ts` (`:255` Purge-Dialog), `account-deletion.e2e.spec.ts` (`:63` Dialog-Flow) |
| Layering `web/` (CLAUDE.md-Tabelle) | **`core/` darf nichts aus `shared/` importieren** — ein Helfer, der einen Dialog öffnet, kann nicht in `core/channels/` liegen (Konzept T5 sagt „gemeinsamer Helfer in `core/channels`"; Auflösung in P8) |

### 0.4 infra-docs (außerhalb dieses Repos, nur für die Nachläufer in Abschnitt 7)

`~/projects/infra-docs/emotepurge/legal/privacy.de.md` § 9 (`:153-170`, der Satz „Auf Ihre Anfrage an contact@… entfernen wir Ihren Kanal sofort …" in `:170`), § 13 (`:220`), § 14 Übersicht (`:228`); `privacy.en.md` spiegelbildlich; `privacy-notes.md` (Merker zu Epic #200 `:48-51`); `EmotePurge-Backup-und-Restore.md` § 2.1 (`:220`) und § 2.2 (`:246`) für den Runbook-Satz (E10).

---

## 1. Entscheidungen dieses Plans (innerhalb des Konzepts)

Die Betreiberentscheidungen E1–E12 sind gesetzt. Innerhalb davon legt der Plan fest — jede
Abweichung vom Wortlaut des Konzepts ist als solche markiert:

**P1 — Namen.** Tabelle `BroadcasterChannelLocks`, Entität `BroadcasterChannelLock` in
`src/EmotePurge.Core/Entities/` (BCL-only, `CoreAssemblyReferenceTests`), Migration
`AddBroadcasterChannelLocks`. Service `IBroadcasterChannelLockService`/`BroadcasterChannelLockService`
in `src/EmotePurge.Infrastructure/Services/` — **Infrastructure, nicht Core**, nach dem Vorbild
`IExcludedChannelFilter`: alle Aufrufer sind Infrastructure-Klassen, Api und Worker sehen die Sperre
nur durch `IChannelService`. Route `DELETE /api/channels/{channelName}/data`. Service-Methode
`IChannelService.PurgeByBroadcasterAsync`. Enum `ChannelBroadcasterPurgeResult` =
`Purged | NotFound | NotBroadcaster | IdentityUnresolved`. Neuer Join-Status
`ChannelJoinStatus.LockedByBroadcaster`. Konfig `Auth:AdminTwitchUserIds` / env `ADMIN_TWITCH_USER_IDS`.
Audit-Details: `reason: "broadcasterRequest"` (Purge), `broadcasterLockLifted: true` /
`liftedByAdmin: true` + `lockedAtUtc` (Join), `reason: "locked"` (Reconcile-Deaktivierung).

**P2 — Der Sperr-Service teilt den `DbContext` des Aufrufers, nicht eine Transaktion per
Parameter.** Alle Aufrufer (`ChannelService`, `SevenTvSyncService`, `ChannelIdentityService`) sind
Scoped und bekommen denselben Scoped `AppDbContext`; ein Scoped `BroadcasterChannelLockService` mit
injiziertem `AppDbContext` liegt damit automatisch in derselben Transaktion. Seine Schreibmethoden
**stagen** nur (`Add`/`Update`/`Remove` im Change-Tracker) und rufen **nicht** `SaveChanges` — der
Aufrufer speichert und committet. Das ist im Interface-Kommentar festzuhalten, weil es die eine
Stelle ist, an der ein neuer Aufrufer sonst eine halbe Transaktion baut.

**P3 — Die Sperrprüfung im Join läuft nach dem Sperren der Zielzeile, nicht „direkt nach der
env-Prüfung".** Konzept 3.4 Punkt 1 sagt „direkt nach der env-Prüfung" (`ChannelService.cs:63`,
vor jedem Zeilen-Lock) und zugleich (F7) „der Inhaber-Join hält dieselbe Zeilensperre, während er
die Sperrzeile löscht". Beides zusammen geht nur, wenn die Entscheidung **nach**
`ResolveJoinTargetAsync` (`:69`, Zeilen gesperrt) fällt — also an der Stelle des zweiten env-Gates
(`:83`), geprüft auf `identity?.Id` **und** `channel.TwitchChannelId`; plus ein Gegenstück in
`HandleUnknownTwitchLoginAsync` nach dessen `LoadChannelForUpdateAsync` (`:275`). Die env-Liste
bleibt davor (env gewinnt). Eine abgelehnte Join-Transaktion wird ohne Commit verworfen — nichts
geschrieben, kein Audit (Konzept: das 409 des Admins ist ein reiner Lesevorgang).

**P4 — Die Aufhebung wird gestaged, bevor `CompleteJoinAsync` den Deckel prüft.** Scheitert der
Join am Deckel (`:485-490`, vor `SaveChanges`), wird die Transaktion verworfen und die Sperre bleibt
— genau Konzept 5 „Kapazitätsdeckel". Der Admin ist deckelbefreit.

**P5 — Zahlen im Dialog kommen aus einem neuen Leseendpoint.** Konzept 3.1/E6: „Der Text nennt
die Zahlen (Emotes, Abstimmungen, Live-Tage)". Der Admin-Dialog hat seine Zahlen aus den
Admin-Aggregaten; dem Broadcaster steht kein Endpoint mit Abstimmungs- und Live-Tage-Zahlen zu
Gebote, und `/permissions` ist die meistgefragte Route der App und darf keine drei `COUNT`s tragen.
Deshalb: `GET /api/channels/{channelName}/data-summary` hinter demselben Broadcaster-Filter,
`InteractiveRead`, Antwort `{ emoteCount, voteSessionCount, liveDayCount }`, gespeist aus
`IChannelService.GetDataSummaryAsync(channelName)` (null → 404). **Das ist eine Ergänzung, die das
Konzept nicht benennt** (Abschnitt 8, Punkt 1); der Betreiber kann sie auf „Kategorien ohne Zahlen"
zurückfahren, dann entfallen die drei Teilschritte, die unten mit „(P5)" markiert sind.

**P6 — Sichtbarkeit `canPurgeAsBroadcaster`.** `true` genau dann, wenn eine Zeile existiert **und**
(ihre `TwitchChannelId` ordinal gleich `principal.TwitchUserId` ist **oder** sie id-los ist und der
Login gleich ist). Der Login-Zweig ist reine Sichtbarkeit: der Filter lässt id-lose Zeilen durch
(Konzept 3.2), der Service führt den Nachweis live. Berechnet an **einer** Stelle in der Api
(`Api/Auth/BroadcasterOwnership.cs`, statisch, pur), die der neue Filter und der
`/permissions`-Handler teilen; `ChannelAccessService.IsBroadcaster` bleibt privat und unverändert.

**P7 — Audit-Log-404 als eigener kleiner Filter vor dem Autorisierungsfilter.** Neuer
`TrackedChannelFilter` (`Api/Auth/`): Kanalname aus der Route, `IChannelService.GetByNameAsync` →
`null` → 404 `channel_not_found` (bestehender Code), sonst `next`. Registriert auf
`GET /{name}/audit-log` **vor** `ChannelManagementAuthorizationFilter`. 401 bleibt davor: die
Authentifizierung ist Gruppen-Middleware (`RequireAuthorization`), kein Endpoint-Filter; die
Anonym-Theorie in `AuthFilterMatrixTests` bleibt grün. Für einen aktiven Kanal mit Zeile ändert
sich nichts (Konzept 3.2).

**P8 — Frontend-Helfer in zwei Hälften wegen der Schichtregel.** Konzept T5 verlangt „einen
gemeinsamen Helfer in `core/channels`" für 409 → Dialog → Wiederholung mit Flag. `core/` darf
`shared/ui` nicht importieren. Deshalb: die pure Hälfte (`readBroadcasterLock(error)`: 409 mit
`channel_locked_by_broadcaster` → `{ lockedAtUtc }`, sonst `null`) in
`web/src/app/core/channels/broadcaster-lock.ts`; die Dialog-Hälfte
(`joinWithBroadcasterLockPrompt(...)`: Join → bei Sperre `ConfirmDialog` mit Datum → bei Bestätigung
Join mit Flag, bei Abbruch nichts) in **`web/src/app/shared/channels/join-with-lock-prompt.ts`**
(neuer Ordner `shared/channels/`). Beide Join-Aufrufer (`admin-channels-page.ts`,
`channel-workspace-layout.ts`) nutzen die Dialog-Hälfte.

**P9 — Admin-Allowlist als Singleton mit Startwarnung aus `Program.cs`.** `ChannelAccessService`
ist Scoped; eine Warnung „bei jedem Start" braucht einen Ort, der einmal pro Prozess läuft. Neuer
`IGlobalAdminAllowlist`/`GlobalAdminAllowlist` (Infrastructure/Services, Singleton, Muster
`ExcludedChannelFilter`: liest beide Listen beim Konstruieren, loggt dort). Der Worker löst ihn nie
auf (nur `ChannelAccessService` braucht ihn) — damit die Warnung im **Api**-Log beim Start steht
und nicht erst beim ersten Request, löst `src/EmotePurge.Api/Program.cs` ihn nach `Build()` einmal
auf (ein `GetRequiredService`, kein Validate-Throw: eine reine Login-Liste ist Übergang, kein
Fehler). `ChannelAccessService.IsGlobalAdmin` delegiert; `IConfiguration` verschwindet aus seinem
Konstruktor.

**P10 — Dev-Default der ID-Liste.** `appsettings.json` bekommt `"AdminTwitchUserIds": []` neben
der unveränderten Login-Liste; der Betreiber trägt seine ID lokal per `dotnet user-secrets`
(`Auth:AdminTwitchUserIds`) ein, in Prod per Portainer-Env. Folge: ein lokaler `dotnet run` ohne
User-Secret zeigt die Übergangswarnung — gewollt, es ist genau der Erinnerungsmechanismus aus 3.8.
Alternative (ID ins Repo, sie ist über Helix öffentlich auflösbar) in Abschnitt 8, Punkt 3.

**P11 — `ChannelJoinResult` trägt `LockedAtUtc`.** Dritte Fabrik `LockedByBroadcaster(DateTime
lockedAtUtc)`; `LockedAtUtc` ist non-null ⇔ Status `LockedByBroadcaster` (Invariante im Stil der
bestehenden `Channel`-Invariante; `Failed(LockedByBroadcaster)` wirft, damit kein Aufrufer das Datum
vergisst). `ChannelJoinResultTests` (Unit) wächst mit.

**P12 — Sync-Gate an allen drei env-Stellen, nicht nur an `:369`.** Konzept 3.4 Punkt 2 nennt die
aufgelöste ID (`ResolveTwitchUserIdAsync`). Die zwei Gates auf die **gespeicherte** ID (`:51`,
`:169`) kosten je einen indizierten Lookup und schließen die Fälle (a)/(c) aus Konzept 3.4 Punkt 4
für die Beobachtung ab dem ersten Tick, falls so eine Zeile doch ein JOIN-Kommando bekommt
(Restore, E10). Dieselbe Wirkung wie `RefuseExcludedChannel`.

**P13 — Reconcile: eigener Pass für gesperrte IDs neben dem env-Pass, unter Zeilensperre.** Der
unbedingte env-Pass (`:60-80`) bekommt einen Zwilling für die Sperrtabelle (ein Batch-Lookup aller
Snapshot-IDs gegen die Tabelle, dann je Treffer eine eigene Transaktion mit
`LoadChannelByTwitchIdForUpdateAsync` → `IsLockedAsync` erneut → `ChannelDeactivation` mit Name und
`{ reason = "locked" }` → Commit). `DeactivateExcludedRowAsync` bleibt, wie es ist. Der id-lose
Pass (`:313`) prüft die frisch aufgelöste ID zusätzlich gegen die Sperre (Backfill + Deaktivierung
unter derselben Zeilensperre). `ChannelDeactivation.DeactivateAsync` bekommt dafür einen
`reason`-Parameter statt des Booleans `forExclusion` (drei Aufrufarten: Leave mit Name ohne Grund,
Exclusion ohne Name mit `excluded`, Lock mit Name und `locked`) — und `SaveChanges` bleibt darin,
weil die Reconcile-Transaktion ohnehin erst nach dem Aufruf committet.

**P14 — `expectedTwitchUserId` ist Pflicht für `/data`.** Fehlt der Query-Parameter, 409
`account_mismatch`, wie `DELETE /api/auth/me` (`DeleteMe_Answers409AccountMismatch_…_WhenTheExpectedIdIsMissing`).

---

## 2. Verträge

### 2.1 Datenbank

**Tabelle `BroadcasterChannelLocks`** (Konzept 3.4): `TwitchChannelId` `text`, PK; `LockedAtUtc`
`timestamp with time zone`, not null. Kein FK, kein weiteres Feld. Migration additiv; das alte Image
kennt die Tabelle nicht und ignoriert sie; Prod-Migration **von Hand vor dem Deploy** (Abschnitt 6).
`RetentionPolicy`/`DataRetentionWorker` kennen die Tabelle **nicht** (keine Frist, E9).

### 2.2 `IBroadcasterChannelLockService` (Infrastructure, Scoped)

- `Task<DateTime?> GetLockedAtUtcAsync(string? twitchChannelId, ct)` — `null`/leer → `null`; sonst
  das Sperrdatum oder `null`. Liest über den geteilten Kontext (ungetrackt).
- `Task LockAsync(string twitchChannelId, DateTime lockedAtUtc, ct)` — Upsert (bestehende Zeile:
  Datum überschreiben), **staged nur**.
- `Task<bool> UnlockAsync(string twitchChannelId, ct)` — staged das Entfernen, `true` wenn eine
  Zeile existierte, idempotent.
- `IQueryable<BroadcasterChannelLock> Locks` (oder gleichwertig) für den SQL-Anti-Join des Rosters
  — bewusst eine Query-Fläche, kein In-Memory-Set: anders als die env-Liste ist die Tabelle
  veränderlich, ein Cache davor hätte die Lücke, die die Sperre schließen soll (Konzept 3.4).

### 2.3 `IChannelService` — Erweiterungen (Core)

- `Task<ChannelBroadcasterPurgeResult> PurgeByBroadcasterAsync(string channelName, AuditActor actor, CancellationToken ct = default)`
  — Ablauf in T3; `actor.TwitchUserId` ist die Identität des Anrufers.
- `JoinAsync(channelName, actor, isGlobalAdmin = false, liftBroadcasterLock = false, ct)` —
  das Flag wirkt nur mit `isGlobalAdmin`; ohne Sperre ist es wirkungslos.
- `ChannelJoinStatus.LockedByBroadcaster` + `ChannelJoinResult.LockedAtUtc` (P11).
- `Task<ChannelDataSummary?> GetDataSummaryAsync(string channelName, ct)` (P5) — `record
  ChannelDataSummary(int EmoteCount, int VoteSessionCount, int LiveDayCount)` in Core; `null` ohne
  Zeile; zählt über die drei FK-Beziehungen (Regel 10: drei skalare `COUNT`s über die Kanal-ID,
  kein Navigations-`GroupBy`).
- `ListActiveChannelNamesAsync` lässt zusätzlich Zeilen aus, deren `TwitchChannelId` in der
  Sperrtabelle steht (Anti-Join in SQL; die env-Filterung bleibt in-memory wie heute).

### 2.4 Api

| Route | Filter (Reihenfolge) | Policy | Antworten |
|---|---|---|---|
| `DELETE /api/channels/{name}/data?expectedTwitchUserId=` | Gruppe (Auth, `ChannelNameValidation`) → **`ChannelBroadcasterAuthorizationFilter`** | `Bookkeeping` | 204 · 400 `invalid_channel_name` · 401 · 403 (`Results.Forbid()`, kein Code: ID vorhanden und fremd — Filter **und** Service `NotBroadcaster`) · 404 (keine Zeile, Filter oder Service `NotFound`) · 409 `account_mismatch` (fehlt/ungleich, **vor** dem Service) · 409 `channel_identity_unresolved` |
| `GET /api/channels/{name}/data-summary` (P5) | Gruppe → `ChannelBroadcasterAuthorizationFilter` | `InteractiveRead` | 200 `{ emoteCount, voteSessionCount, liveDayCount }` · 400 · 401 · 403 · 404 |
| `POST /api/channels/{name}/join?liftBroadcasterLock=true` (bestehend) | unverändert | unverändert | neu: `LockedByBroadcaster` → **403** `channel_locked_by_broadcaster` für Nicht-Admins; **409** `{ errorCode: channel_locked_by_broadcaster, lockedAtUtc }` für Admins ohne Flag. Mit Flag und Admin-Rolle: gewöhnlicher Join-Ablauf. Das Flag wird als `bool liftBroadcasterLock = false` gebunden (Query) und **nur** als `isGlobalAdmin && liftBroadcasterLock` an den Service gereicht |
| `GET /api/channels/{name}/audit-log` (bestehend) | Gruppe → **`TrackedChannelFilter`** → `ChannelManagementAuthorizationFilter` | unverändert | neu: 404 `channel_not_found` ohne Zeile, für jeden Principal |
| `GET /api/channels/{name}/permissions` (bestehend) | unverändert | unverändert | `ChannelPermissionsDto` + `CanPurgeAsBroadcaster` (P6) |

**`ChannelBroadcasterAuthorizationFilter`** (`Api/Auth/`): 400 bei ungültigem Namen → 401 ohne
Principal → Zeile per `IChannelService.GetByNameAsync` (substituierbar, `ApiFactory`) → 404 ohne
Zeile → 403, wenn `TwitchChannelId` gesetzt und ≠ `principal.TwitchUserId` (ordinal) → sonst
`next`. **Kein** Admin-Durchgriff, **kein** Mod-Zweig, **kein** Login-Fallback (E7). Id-lose Zeile
passiert; der Service entscheidet (Konzept 3.2/3.3).

**Regel-7-Kette** für `channel_identity_unresolved` und `channel_locked_by_broadcaster`:
`ApiErrorCodes.cs` → `api-error.ts` → `de.json` + `en.json`. Texte (de/en, Konzept 3.2):
unresolved „Die Kanal-Identität konnte gerade nicht bestätigt werden — bitte später erneut." /
„The channel's identity could not be confirmed right now — please try again later.";
locked „Der Streamer hat diesen Kanal aus EmotePurge entfernt. Nur er selbst kann ihn wieder
hinzufügen." / „The streamer has removed this channel from EmotePurge. Only they can add it back."
Der Admin-Dialogtext ist **kein** `errors.api`-Text (eigener Schlüssel, 2.5).

### 2.5 Frontend

- `ChannelPermissions.canPurgeAsBroadcaster: boolean`.
- `ChannelService.purgeOwnData(channelName, expectedTwitchUserId): Observable<void>` →
  `DELETE /api/channels/{name}/data?expectedTwitchUserId=…`; invalidiert die Permissions des Kanals.
  `ChannelService.getDataSummary(channelName): Observable<ChannelDataSummary>` (P5).
  `ChannelService.join(channelName, options?: { liftBroadcasterLock?: boolean })` — ohne Option
  byte-gleicher Request wie heute (Spec `:159` bleibt grün).
- `core/channels/broadcaster-lock.ts`: `readBroadcasterLock(error: unknown): { lockedAtUtc: string } | null`.
- `shared/channels/join-with-lock-prompt.ts`: `joinWithBroadcasterLockPrompt({ channelService,
  dialog, transloco, lang }, channelName): Observable<ChannelStatus | null>` — `null` = Abbruch
  (kein zweiter Request); andere Fehler laufen unverändert als Fehler durch. Das Datum wird mit
  `toLocale(lang)` + `toLocaleString({ dateStyle: 'medium' })` formatiert.
- i18n-Schlüssel (beide Locales): `channelWorkspace.purgeOwnData` (Knopf), `channelWorkspace.purgeOwnDataDialog.{title,message,inputLabel,confirm}`
  (Nachricht mit `{{ channelName, emotes, voteSessions, liveDays }}` und den fünf Sätzen aus
  Konzept 3.1 Punkt 2 **inklusive der Nachweisgrenze**), `channelWorkspace.errors.purgeOwnDataFailed`,
  `channels.broadcasterLock.liftConfirm` („Der Streamer hat diesen Kanal am {{ date }} gelöscht und
  gesperrt. Trotzdem hinzufügen?") + `channels.broadcasterLock.liftConfirmLabel`,
  `account.delete.channelHint` („Die Daten deines Kanals *{{ login }}* bleiben erhalten und werden
  weiter gezählt. Wenn du sie löschen willst, tu das zuerst im Workspace deines Kanals.").
- Nach 204: `channelService.invalidatePermissions(channelName)`, Navigation zur Übersicht
  (Route `''`, `web/src/app/app.routes.ts:40`).

### 2.6 Konfiguration (3.8)

`Auth:AdminTwitchUserIds` — Array in `appsettings.json` **oder** kommagetrennter Skalar (env
`ADMIN_TWITCH_USER_IDS`; Skalar gewinnt), Vergleich `principal.TwitchUserId` **ordinal**.
Übergang: ID-Liste nicht leer → **allein** die IDs entscheiden, Login-Liste wird ignoriert,
Startwarnung „Auth:AdminTwitchLogins is ignored because Auth:AdminTwitchUserIds is configured
({IdCount} id(s), {LoginCount} login(s))". ID-Liste leer, Login-Liste gesetzt → Login entscheidet,
Startwarnung „the global admin allowlist is login-based ({LoginCount} login(s)); migrate to
Auth:AdminTwitchUserIds". Beide leer → kein Admin, Information. **Keine Werte in den
Log-Zeilen**, nur Anzahlen. Compose: `Auth__AdminTwitchUserIds=${ADMIN_TWITCH_USER_IDS:-}` für
`api` in beiden Compose-Dateien (das Login-Mapping bleibt für den Übergang stehen).

---

## 3. Tasks

Gates je Task: nur die Suiten, die der Task berührt (Backend: `dotnet build EmotePurge.slnx`
mit 0 Warnungen, `dotnet test EmotePurge.slnx` — braucht Docker für Testcontainers —,
`dotnet format EmotePurge.slnx --verify-no-changes`; Frontend: `npm --prefix web test --
--watch=false`, `npm --prefix web run lint`, `npm --prefix web run format:check`). Der Schluss-Task
T8 fährt alles inklusive E2E, Coverage-Näherung und Live-Verifikation. Jeder Task committet selbst
(Conventional Commits; DECISIONS im selben Commit wie die Vertragsänderung), pusht nicht. Alle
Pfade unten absolut unter `/home/dev/projects/EmotePurge-245/`.

**Reihenfolge und Parallelität** (Begründung in Abschnitt 4): T0 → T1 → T2 → T3 → T4 → T5 → T7 → T8,
mit **T6 (Web) parallel zu T3–T5** ab T1-Ende und **T2 parallel zu T1** nur, wenn der Orchestrator
den Mini-Konflikt in `ServiceCollectionExtensions.cs` und `DECISIONS.md` selbst auflöst — sonst
sequenziell.

### T0 — Worktree vorbereiten und Ausgangslage messen (`haiku`)

**Kontext:** frischer Worktree, `web/node_modules` fehlt. Bevor ein Task eine Suite fährt, muss die
Ausgangslage grün bekannt sein.

- [ ] `npm --prefix /home/dev/projects/EmotePurge-245/web ci`; Nachweis `ls …/web/node_modules/.bin/ng`.
- [ ] `docker info` (Testcontainers). **Kein `docker compose up` aus dem Worktree.**
- [ ] `ss -ltn | grep -E ':5151|:4200'` notieren (E2E in T8 braucht `:5151` frei).
- [ ] Baseline: `dotnet build /home/dev/projects/EmotePurge-245/EmotePurge.slnx` (0 Warnungen),
      `npm --prefix …/web test -- --watch=false` (Zahl melden). `dotnet test` erst in T1.
- [ ] Kein Commit.

**Abnahme:** `node_modules` vorhanden, Build 0 Warnungen, Vitest-Zahl gemeldet.

### T1 — Sperrtabelle, Migration, Sperr-Service (`sonnet`)

**Kontext:** Konzept 3.4 (Datenmodell, „warum eine eigene Tabelle", „warum nicht in
`IExcludedChannelFilter`"), P1, P2, Vertrag 2.1/2.2. Regel 11.

**Dateien:** neu `src/EmotePurge.Core/Entities/BroadcasterChannelLock.cs`;
`src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs` (`DbSet`, Entity-Konfiguration: PK
`TwitchChannelId`, Spaltentyp wie die anderen `timestamptz`); Migration per
`dotnet ef migrations add AddBroadcasterChannelLocks --project src/EmotePurge.Infrastructure
--startup-project src/EmotePurge.Api` (erzeugt `.cs`, `.Designer.cs`, aktualisiert
`AppDbContextModelSnapshot.cs` — **Migration nur generieren, nicht gegen die Dev-DB anwenden**:
die Testcontainer-Suite migriert ihre eigene DB); neu
`src/EmotePurge.Infrastructure/Services/IBroadcasterChannelLockService.cs` und
`BroadcasterChannelLockService.cs`; `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs`
(Scoped-Registrierung neben `:76`); neu
`tests/EmotePurge.Infrastructure.Tests/Integration/BroadcasterChannelLockServiceTests.cs`
(`[Collection("Postgres")]`, Muster `ChannelRetentionPurgeTests`); `docs/DECISIONS.md` (neuer
Eintrag, oben einsortiert).

**Vertrag:** 2.1, 2.2. Interface-Doku nennt ausdrücklich: Schreibmethoden stagen nur; die Tabelle
hat keine Retention; env-Liste und Sperre sind zwei Mechanismen, env gewinnt; nur
`PurgeByBroadcasterAsync` schreibt eine Sperre (T3), nur ein Join hebt sie auf (T4).

**Tests (Integration):**
1. `LockAsync` auf leerer Tabelle, dann `SaveChanges` → `GetLockedAtUtcAsync` liefert das Datum.
2. `LockAsync` zweimal mit verschiedenen Daten → eine Zeile, jüngstes Datum (Upsert).
3. `UnlockAsync` → `true`, danach `GetLockedAtUtcAsync` `null`; zweites `UnlockAsync` → `false`,
   kein Fehler (idempotent).
4. `GetLockedAtUtcAsync(null)` und `("")` → `null` ohne DB-Fehler.
5. Staging-Vertrag: `LockAsync` **ohne** `SaveChanges` → ein zweiter Kontext sieht nichts.
6. Migration: `PendingMigrationGuardTests`-Muster — frische DB, alle Migrationen, Tabelle und PK
   existieren; (optional) `AppDbContextModelSnapshot` ohne Pending-Model-Changes.

**Schritte:**
- [ ] Entität (BCL-only), DbContext, Migration generieren, Snapshot prüfen.
- [ ] Interface + Implementierung + Registrierung.
- [ ] Tests 1–6.
- [ ] DECISIONS-Eintrag (englisch) „Broadcaster self-service purge and a DB re-add lock (#245)" mit
      `**Betrifft:**`-Zeile; in diesem Commit die Absätze: Tabelle und warum nicht Flag/Tombstone;
      zwei Mechanismen neben der env-Liste, env gewinnt; keine Retention (§ 9/§ 13); Datenschutz
      (nur die ID, nach Kontolöschung bleibt sie — § 5.5). Die Absätze zu Purge-Pfad, Lesestellen,
      Api und Admin-Aufhebung ergänzen T3–T5 **in demselben Eintrag**, je in ihrem Commit.
- [ ] Gates Backend.
- [ ] Commit: `feat(infrastructure): add the broadcaster channel lock table and service`.

**Abnahme:** Tests 1–6 grün, Suite grün, `dotnet ef migrations list` zeigt die neue Migration als
letzte, DECISIONS-Eintrag vorhanden.

### T2 — Admin-Allowlist per Twitch-ID (Konzept 3.8/T4b, E11) (`sonnet`)

**Kontext:** P9, P10, Vertrag 2.6. Vor T5, weil dessen Admin-Fälle auf der ID-Prüfung aufsetzen.
Unabhängig von T1 (berührt weder Sperre noch Purge).

**Dateien:** neu `src/EmotePurge.Infrastructure/Services/IGlobalAdminAllowlist.cs` +
`GlobalAdminAllowlist.cs` (Singleton; liest beide Listen in beiden Formen, Muster
`ExcludedChannelFilter.ReadExcludedChannelIds`); `ChannelAccessService.cs` (`IsGlobalAdmin`
delegiert, `IConfiguration` raus, `GetAdminLogins` weg); `ServiceCollectionExtensions.cs`
(Singleton-Registrierung neben `:74`); `src/EmotePurge.Api/Program.cs` (einmaliges Auflösen nach
`Build()`, Kommentar warum); `src/EmotePurge.Api/appsettings.json:44` (`"AdminTwitchUserIds": []`
dazu); `.env.example` (`ADMIN_TWITCH_USER_IDS=` mit Kommentar „preferred; ADMIN_TWITCH_LOGINS is
the transitional fallback"); `docker-compose.yml:70` / `docker-compose.prod.yml:71` (zweite Zeile);
`README.md:65,104`; `docs/Operations.md` (neuer Unterabschnitt „Global admins" nahe `:382`:
Schlüssel, Übergang, Warnungen, wie man die eigene ID findet — `GET helix/users?login=` wie in
`:247`); `docs/Architectur.md:130` (Rollenquellen-Tabelle: Quelle ist jetzt die ID, Login nur
Übergang) und `:210`; `PRODUCT.md:52`; Tests
`tests/EmotePurge.Infrastructure.Tests/Unit/ChannelAccessServiceAdminTests.cs` (umbauen auf den
Allowlist-Typ; Datei darf in `GlobalAdminAllowlistTests.cs` umbenannt werden),
`ChannelAccessServiceTests.cs` (`CreateService` baut die Allowlist aus `Auth:AdminTwitchUserIds`,
Principal-ID `"42"`), `tests/EmotePurge.Api.Tests/ApiFactory.cs:167` und
`SessionRejectionTests.cs:151` (zusätzlich `Auth:AdminTwitchUserIds` leer setzen — der Admin wird
dort über das `IChannelAccessService`-Substitut gestellt, die Settings sind nur Hygiene);
`docs/DECISIONS.md` (eigener Eintrag).

**Tests (Unit, container-frei):**
1. Array-Form `Auth:AdminTwitchUserIds:0/1` → beide IDs Admin; Login beliebig.
2. Skalar `"1,2"` schlägt Array; Trim, leere Einträge, ordinal (`"042"` ≠ `"42"`).
3. IDs gesetzt **und** Logins gesetzt → nur die ID zählt: Admin-Login mit fremder ID → `false`;
   Warnung „ignored" mit Anzahlen (RecordingLogger aus `tests/…/Fakes/`), **keine Werte**.
4. IDs leer, Logins gesetzt → Login entscheidet (case-insensitive wie heute); Warnung „login-based".
5. Beide leer → niemand Admin, keine Warnung.
6. `ChannelAccessServiceTests`: `CanManageChannelAsync_AllowsGlobalAdmin_WithoutConsultingTwitch`
   per ID; neuer Fall: Admin-**Login**, fremde ID, keine ID-Liste konfiguriert → Übergang greift
   (true) — und mit ID-Liste → false.
7. `AuthFilterMatrixTests`: ein Fall, der den Admin-Principal mit Admin-ID und beliebigem Login
   stellt, und der Gegenfall „Admin-Login, fremde ID → 403" — beide über das Substitut der
   Access-Service-Schnittstelle sind trivial; **der eigentliche Beleg ist Test 3/6**. Der
   Matrix-Fall dokumentiert nur, dass der Filter weiterhin ausschließlich `IsGlobalAdmin` fragt.

**Schritte:**
- [ ] Allowlist-Typ, Service-Umbau, Registrierung, `Program.cs`.
- [ ] Konfigurationsfläche (sieben Dateien oben) und Doku-Zeilen.
- [ ] Tests 1–7.
- [ ] DECISIONS-Eintrag „Global admins are allowlisted by immutable Twitch id, logins remain a
      transitional fallback (#245)": warum (Login ist wiedervergebbar, die Sperr-Aufhebung ist das
      erste Recht, das eine Streamer-Entscheidung übersteuert), die Übergangsregel, die beiden
      Warnungen ohne Werte, der notierte Folgeschritt „Login-Liste entfernen".
- [ ] Gates Backend.
- [ ] Commit: `feat(infrastructure): resolve global admins by immutable Twitch id` (Code + Tests +
      Konfig + Doku + DECISIONS in einem Commit — der Vertrag ist die Änderung).

**Abnahme:** Tests grün; `grep -rn "Auth:AdminTwitchLogins" src/` zeigt nur noch die
Allowlist-Klasse, `appsettings.json` und Kommentare; Startlog eines lokalen `dotnet run` (T8)
zeigt genau eine der beiden Warnungen.

### T3 — Purge-Pfad `PurgeByBroadcasterAsync` + Datenzusammenfassung (`sonnet`)

**Kontext:** Konzept 3.3 (Identität vor der Transaktion, Zielmenge, Nachweisgrenze, Audit,
Sperre vor Commit, LEAVE danach), 5 („Rennen Purge ↔ Join", „Duplizierte Zeilen"), P5.
Setzt T1 voraus. Regel 11.

**Dateien:** `src/EmotePurge.Core/Services/IChannelService.cs` (Enum
`ChannelBroadcasterPurgeResult`, `record ChannelDataSummary`, zwei Methoden mit Doku);
`src/EmotePurge.Infrastructure/Services/ChannelService.cs` (neue Methoden; Konstruktor um
`IBroadcasterChannelLockService`; **privater Helfer** `StagePurge(channel, actor, details)` für
Audit + `Remove`, den `PurgeAsync`, `PurgeIfInactiveSinceAsync` und der neue Pfad teilen —
Verhalten der beiden bestehenden Pfade bleibt byte-gleich: Admin ohne Details, Retention mit
`{ reason = "retention" }`); neue Konstante `BroadcasterPurgeReason = "broadcasterRequest"`;
`tests/EmotePurge.Infrastructure.Tests/Integration/ChannelServiceTests.cs` (`CreateService`
bekommt den Sperr-Service; Default: echte Implementierung auf demselben `db`) und neu
`ChannelBroadcasterPurgeTests.cs`; `docs/DECISIONS.md` (Absätze im T1-Eintrag).

**Vertrag — Ablauf von `PurgeByBroadcasterAsync` (Reihenfolge bindend):**
1. Normalisieren; Zeile unter dem Routennamen **ungesperrt** lesen. Keine → `NotFound`.
2. Trägt sie eine ID: ≠ `actor.TwitchUserId` → `NotBroadcaster` (kein Helix). Sonst „bewiesen".
3. Id-los: `LookupByLoginAsync(name)` **außerhalb** der Transaktion. `Found` mit `User.Id ==
   actor.TwitchUserId` → bewiesen (die ID wird in der Transaktion auf die Zeile geschrieben, sofern
   kein anderer die ID hält); `Found` fremd → `NotBroadcaster`; `NotFound`/`Unavailable` →
   `IdentityUnresolved`. Nichts geschrieben.
4. Transaktion. Sperren in Merge-Reihenfolge: (a) `LoadChannelByTwitchIdForUpdateAsync(actor.TwitchUserId)`,
   (b) `LoadChannelForUpdateAsync(name)`, falls eine **andere** Zeile als (a). (b) gehört zur
   Zielmenge nur, wenn sie **jetzt** id-los ist und Schritt 3 den Login bewiesen hat, **oder** sie
   inzwischen die ID des Akteurs trägt. Trägt (b) jetzt eine fremde ID (konkurrierender Backfill),
   fällt sie weg; ist dann die Zielmenge leer → `NotBroadcaster` wenn (a) fehlt. Beide fehlen →
   `NotFound` (konkurrierender Purge/Merge).
5. Nachprüfung unter der Sperre: jede Zielzeile hat `TwitchChannelId == actor.TwitchUserId` oder ist
   id-los-bewiesen. Für jede Zielzeile: Audit `channel.purge`, `ChannelName` gesetzt,
   `{ reason = "broadcasterRequest" }`; `Remove`.
6. `LockAsync(actor.TwitchUserId, UtcNow)` stagen. `SaveChanges`, Commit.
7. Nach dem Commit je gelöschter Zeile `LEAVE:<name>` publizieren. Ein Publish-Fehler wird mit
   Warnung geloggt (keine ID, kein Name — Konvention #246; die Zählung ist „eine von N Publishes"),
   **nicht** geworfen: die Zeile ist Quelle der Wahrheit, der Roster-Prune holt ≤ 2 Ticks nach.
8. Ergebnis `Purged`.

`GetDataSummaryAsync(name)` (P5): drei `COUNT`s über `ChannelId` (Emotes, VoteSessions,
ChannelLiveDays), `null` ohne Zeile; keine Autorisierung im Service (der Filter macht sie).

**Tests (Integration, `ChannelBroadcasterPurgeTests`, Muster `ChannelRetentionPurgeTests` mit
`PostgresLockProbe` für die Rennen):**
1. Zeile mit eigener ID, mit Emotes/UsageStats/LiveDays/VoteSession/Votes → `Purged`; alle
   Kaskaden leer; genau ein `channel.purge` mit Name und `broadcasterRequest`; Sperrzeile mit Datum;
   genau ein LEAVE **nach** dem Commit (Reihenfolge per `Received.InOrder` oder Zeitstempel-Probe
   gegen einen zweiten Kontext, der die Zeile beim Publish nicht mehr sieht).
2. Zeile mit fremder ID → `NotBroadcaster`; nichts gelöscht, kein Audit, keine Sperre, kein Helix-Aufruf.
3. Id-lose Zeile, Lookup `Found` eigene ID → `Purged`; Sperre auf die Akteurs-ID.
4. Id-lose Zeile, `Found` fremde ID → `NotBroadcaster`, nichts geschrieben.
5. Id-lose Zeile, `Unavailable` bzw. `NotFound` → `IdentityUnresolved`, nichts geschrieben.
6. Hauptzeile (eigene ID, Name A) + id-loses Duplikat (Name B = Routenname), Lookup(B) → eigene ID
   → **beide** gelöscht, zwei Audit-Einträge, zwei LEAVEs, eine Sperre.
7. Hauptzeile + id-loses Duplikat unter altem Login, Route = Hauptname → nur die Hauptzeile fällt,
   das Duplikat bleibt (Nachweisgrenze); Route = Duplikatname mit `Found` fremde ID → `NotBroadcaster`.
8. Unbekannter Name → `NotFound`, kein Helix-Aufruf, nichts geschrieben.
9. Rennen Purge ↔ `JoinAsync` (zwei Kontexte, `WaitUntilBlockedOnLockAsync`): Purge hält die Sperre,
   Join eines **Mods** wartet und landet danach auf `LockedByBroadcaster` (T4 — in T3 nur so weit
   testbar, dass der Join eine neue Zeile anlegt; der Sperr-Zweig kommt in T4 dazu; Test in T4
   fertigstellen).
10. Zweiter Aufruf nach `Purged` → `NotFound`.
11. `GetDataSummaryAsync`: Zahlen stimmen; ohne Zeile `null`.
12. Bestehende Purge-Tests (`ChannelRetentionPurgeTests`, Admin-Purge in `ChannelServiceTests`)
    bleiben unverändert grün (Helfer-Refactoring ohne Verhaltensänderung).

**Schritte:**
- [ ] Interface + Core-Typen mit Doku.
- [ ] Helfer-Refactoring der drei Purge-Pfade, neue Methoden.
- [ ] Tests 1–12.
- [ ] DECISIONS: Absätze „purge path" (Live-Auflösung, Zielmenge, Nachweisgrenze als benannte
      Grenze, Sperre vor Commit, LEAVE nach Commit — Abweichung von `PurgeAsync` und warum) im
      T1-Eintrag; `**Betrifft:**` ergänzen.
- [ ] Gates Backend.
- [ ] Commit: `feat(infrastructure): let a broadcaster purge their own channel's data`.

**Abnahme:** Tests grün; `grep -n "AddAuditEntry" ChannelService.cs` zeigt für `channel.purge`
genau **eine** Stelle (den Helfer).

### T4 — Sperre an den vier Lesestellen: Join, Sync-Gate, Roster, Reconcile (`opus`)

**Kontext:** Konzept 3.4 Punkte 1–4, 5 (Rennen, Helix-Ausfall, Deckel, env gewinnt), P3, P4, P11,
P12, P13. Setzt T1 und T3 voraus (teilt `ChannelService.cs`). Regel 11. Das ist der Task mit den
meisten Nebenläufigkeitsfallen — deshalb `opus`.

**Dateien:** `IChannelService.cs` (Status, Flag, `ChannelJoinResult`), `ChannelService.cs`
(`JoinAsync`, `HandleUnknownTwitchLoginAsync`, `CompleteJoinAsync` — Audit-Details —,
`ListActiveChannelNamesAsync`), `ChannelDeactivation.cs` (`reason`-Parameter, P13),
`ChannelIdentityService.cs` (Lock-Pass neben `:60-80`, id-loser Pass `:313`, Konstruktor),
`SevenTvSyncService.cs` (drei Gates, Konstruktor), Tests: `Unit/ChannelJoinResultTests.cs`,
`Integration/ChannelServiceTests.cs`, `ChannelBroadcasterPurgeTests.cs` (Test 9 fertigstellen),
`ChannelIdentityServiceTests.cs`, `SevenTvSyncServiceTests.cs`, neu
`Integration/ChannelServiceBroadcasterLockTests.cs`; `docs/DECISIONS.md` (Absätze im T1-Eintrag).
**`ChannelEndpoints.cs` bricht mit CS8509, sobald der Enum-Wert existiert** — T4 ergänzt dort nur
den minimalen Arm (403 `channel_locked_by_broadcaster` für alle), den T5 auf die 403/409-Logik
ausbaut; so bleibt die Solution zwischen T4 und T5 baubar.

**Vertrag Join (P3):** nach `ResolveJoinTargetAsync` bzw. nach dem Lock in
`HandleUnknownTwitchLoginAsync`: `lockedAt = GetLockedAtUtcAsync(identity?.Id) ?? GetLockedAtUtcAsync(channel.TwitchChannelId)`.
Treffer, Reihenfolge der Fälle **bindend**: (1) `actor.TwitchUserId == gesperrte ID` → `UnlockAsync`
stagen, Audit-Detail `{ broadcasterLockLifted = true }`, weiter; (2) `isGlobalAdmin &&
liftBroadcasterLock` → `UnlockAsync`, Detail `{ broadcasterLockLifted = true, liftedByAdmin = true,
lockedAtUtc }`, weiter; (3) `isGlobalAdmin` ohne Flag → `Failed`/`LockedByBroadcaster(lockedAt)`;
(4) sonst → dasselbe. Flag ohne Admin-Rolle ist bedeutungslos (Fall 4). Die Audit-Details landen
im bestehenden `channel.join`-Eintrag (`:514` bekommt `details`). Eine neue Zeile (Helix
`Unavailable`, id-los) kann keine gespeicherte ID haben — der Inhaber-Join im Ausfall legt sie an,
die Sperre bleibt (Konzept 5 „Twitch nicht erreichbar"; im DECISIONS-Eintrag nennen).

**Vertrag Roster:** `ListActiveChannelNamesAsync` ergänzt die Query um `!Locks.Any(l =>
l.TwitchChannelId == c.TwitchChannelId)` (SQL-Anti-Join, id-lose Zeilen matchen nie).

**Vertrag Sync-Gate (P12):** an `:51`, `:169` (gespeicherte ID) und `:369` (aufgelöste ID, **vor**
Duplikat-Suche und Backfill): Sperre → `RefuseExcludedChannel`-Analog (Match-Cache räumen, Debug-Log
„7TV sync skipped: the channel is locked by its broadcaster." — nicht geheim, aber ohne Namen, aus
demselben Grund wie `:260-265`), Rückgabe wie beim env-Fall. Reihenfolge: env zuerst, dann Sperre.

**Vertrag Reconcile (P13):** Lock-Pass vor Token/Helix; je Treffer eigene Transaktion,
`LoadChannelByTwitchIdForUpdateAsync`, Zeile weg/inaktiv/andere ID → nichts; `GetLockedAtUtcAsync`
erneut unter der Sperre → `null` (Inhaber-Join war schneller) → nichts, sonst
`ChannelDeactivation.DeactivateAsync(…, reason: "locked")` mit Name, Commit; `settledChannelIds`
und Zähler `Deactivated` wie beim env-Pass. Id-loser Pass: aufgelöste ID gesperrt → ID schreiben
(falls frei) + deaktivieren unter derselben Zeilensperre.

**Tests:**
- `ChannelJoinResultTests`: dritte Fabrik; `Failed(LockedByBroadcaster)` wirft; `LockedAtUtc`
  non-null ⇔ Status.
- `ChannelServiceBroadcasterLockTests` (Integration): (1) Mod-Join auf gesperrte ID → Status mit
  Datum, keine Zeile, kein Audit, Sperre bleibt; (2) Inhaber-Join → `Joined`, Sperre weg,
  `channel.join` mit `broadcasterLockLifted`; (3) Admin ohne Flag → Status mit Datum, nichts
  geschrieben; (4) Admin mit Flag → `Joined`, Sperre weg, `liftedByAdmin` + `lockedAtUtc` im Detail;
  (5) Flag ohne Admin → wie (1); (6) Inhaber ist zugleich Admin, kein Flag → wie (2) (Inhaber vor
  Admin); (7) Deckel voll + Inhaber-Join → `CapacityReached`, Sperre bleibt; (8) env-gesperrt **und**
  Broadcaster-gesperrt, Inhaber-Join → `ChannelExcluded`, Sperre bleibt; (9) `NotFound`-Pfad
  (`HandleUnknownTwitchLoginAsync`) mit inaktiver Zeile, deren gespeicherte ID gesperrt ist → Mod
  `LockedByBroadcaster`; (10) Roster: aktive Zeile mit gesperrter ID fehlt in
  `ListActiveChannelNamesAsync`, id-lose aktive Zeile bleibt drin; (11) Purge-Test 9 aus T3
  fertig: Mod-Join wartet an der Sperre und endet auf `LockedByBroadcaster`; Inhaber-Join wartet und
  legt neu an, Sperre weg.
- `SevenTvSyncServiceTests`: (12) id-lose Zeile, 7TV löst auf gesperrte ID → kein Backfill, kein Set,
  Match-Cache leer, kein Fehlversuch aufgezeichnet (Muster `:919`); (13) gespeicherte ID gesperrt →
  weder 7TV-Aufruf noch Cache-Warm-up (Muster `:895`); (14) `ApplyEmoteSetUpdate` mit gesperrter ID
  → nichts geschrieben (Muster `:953`).
- `ChannelIdentityServiceTests`: (15) aktive Zeile mit gesperrter ID → deaktiviert, `channel.leave`
  mit Name und `locked`, LEAVE publiziert, Zähler; (16) **deterministisches Rennen** (zwei Kontexte,
  `PostgresLockProbe`): Inhaber-Join hält die Zeilensperre und löscht die Sperrzeile; der
  Reconcile wartet, prüft erneut, schreibt nichts — Zeile bleibt aktiv; (17) Gegenrichtung:
  Reconcile hält die Sperre, Join wartet, findet die Zeile deaktiviert, reaktiviert sie (Inhaber)
  bzw. scheitert (Mod); (18) ohne App-Token/Helix `Unavailable` läuft der Lock-Pass trotzdem
  (Muster `:557`, `:586`); (19) id-lose Zeile, Helix-Login löst auf gesperrte ID → ID geschrieben +
  deaktiviert.
- Bestehende `forExclusion`-Aufrufer-Tests bleiben grün (Signaturwechsel ist mechanisch).

**Schritte:**
- [ ] Core-Typen (Status, Flag, Fabrik) + Unit-Test.
- [ ] Join-Pfad + minimaler Endpoint-Arm (Build grün).
- [ ] Roster, Sync-Gate, `ChannelDeactivation`, Reconcile.
- [ ] Tests 1–19.
- [ ] DECISIONS: Absätze „four read points", „who lifts the lock and how it is audited", „Helix
      outage: owner join leaves an id-less row, lock stays, self-healing", „why the reconcile locks
      the row where the exclusion gate does not" im T1-Eintrag; `**Betrifft:**` ergänzen.
- [ ] Gates Backend (inkl. `tests/EmotePurge.Worker.Tests` — unverändert, aber die Solution-Suite
      läuft ohnehin ganz).
- [ ] Commit: `feat(infrastructure): enforce the broadcaster lock on join, sync, roster and reconcile`.

**Abnahme:** Tests grün; `grep -n "IsExcluded" ChannelService.cs SevenTvSyncService.cs
ChannelIdentityService.cs` und `grep -n "GetLockedAtUtcAsync"` zeigen an jeder env-Stelle, die
Konzept 3.4 nennt, ein Sperr-Gegenstück (Join ×3, Sync ×3, Roster ×1, Reconcile ×2).

### T5 — Api: Filter, Endpoints, Error-Codes, Join-Mapping, Audit-Log-404 (`sonnet`)

**Kontext:** Konzept 3.2, P6, P7, P14, Vertrag 2.4. Setzt T2–T4 voraus. Regel 11 (neuer Filter +
Filter-Reihenfolge → `AuthFilterMatrixTests`).

**Dateien:** neu `src/EmotePurge.Api/Auth/ChannelBroadcasterAuthorizationFilter.cs`,
`TrackedChannelFilter.cs`, `BroadcasterOwnership.cs`; `src/EmotePurge.Api/Validation/ApiErrorCodes.cs`
(zwei Codes); `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs` (zwei neue Routen; Join-Switch mit
403/409-Verzweigung und `lockedAtUtc`; Query-Bindung `liftBroadcasterLock`; `TrackedChannelFilter`
am Audit-Log; `ChannelPermissionsDto` + `CanPurgeAsBroadcaster`); `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`
(+ ggf. neue Datei `ChannelBroadcasterPurgeEndpointTests.cs`, wenn die Matrix-Datei zu groß wird);
`tests/EmotePurge.Api.Tests/ApiFactory.cs` (nichts Neues nötig — `Channels` und `ChannelAccess` sind
substituiert; `GetByNameAsync` je Test konfigurieren); `docs/Architectur.md` B.3 (Filter-Tabelle:
sechs bzw. sieben Filter; Endpoint-Matrix `:153`: `/data`, `/data-summary`, Audit-Log mit
`TrackedChannel`; `:227` erwähnt den Broadcaster-Weg neben dem Admin-Purge); `docs/DECISIONS.md`
(Absätze im T1-Eintrag). **Nicht** `web/` — die Regel-7-Kette setzt T6 fort (Codes in
`api-error.ts` + Locales); bis dahin ist `api-error-locales.spec.ts` unberührt, weil es nur die
TS-Seite prüft.

**Tests (`AuthFilterMatrixTests`, WebApplicationFactory):**
1. Anonym-Theory um `DELETE …/data` und `GET …/data-summary` erweitert → 401.
2. `/data`: ungültiger Name → 400 `invalid_channel_name` vor allem anderen; keine Zeile → 404 und
   der Service wird **nicht** gerufen; Zeile mit fremder ID → 403, Service nicht gerufen; Zeile mit
   eigener ID ohne `expectedTwitchUserId` → 409 `account_mismatch`, Service nicht gerufen; mit
   falscher ID → 409 dito; passend → Service gerufen → `Purged` → 204 / `NotFound` → 404 /
   `NotBroadcaster` → 403 / `IdentityUnresolved` → 409 `channel_identity_unresolved`; id-lose Zeile
   mit Login-Match passiert den Filter (Service entscheidet); **Admin ohne Broadcaster-ID → 403**
   (kein Durchgriff); **Mod → 403** (`CanManage` wird gar nicht gefragt — Substitut nie gerufen).
3. `/data-summary`: 404/403/200-Form.
4. Join: `LockedByBroadcaster` + `IsGlobalAdmin=false` → 403 mit Code; `IsGlobalAdmin=true` ohne
   Flag → 409 mit Code **und** `lockedAtUtc` (ISO-8601, UTC); mit `?liftBroadcasterLock=true` und
   Admin → `JoinAsync` erhält `liftBroadcasterLock: true`; ohne Admin mit Flag → Service erhält
   `false`.
5. Audit-Log: keine Zeile → 404 `channel_not_found` auch für einen Principal mit gleichem Login
   (`CanManage`-Substitut wird nicht gerufen); mit Zeile → unverändert; ungültiger Name → 400 vor
   dem 404 (`:442`-Fall bleibt). Bestehende Audit-Log-Fälle bekommen `Channels.GetByNameAsync`
   → eine Zeile.
6. `/permissions`: `canPurgeAsBroadcaster` true/false für ID-Match, Login-Match ohne ID, fremde ID,
   keine Zeile.
7. Filter-Reihenfolge der Gruppe unverändert (bestehende Fälle grün).

**Schritte:**
- [ ] Codes, Ownership-Helfer, zwei Filter, Routen, Join-Mapping, DTO.
- [ ] Tests 1–7.
- [ ] Architectur.md B.3 + Matrix; DECISIONS-Absätze „api contract", „audit log answers 404 without
      a row (F1) and the follow-up: bind audit entries to the Twitch id", „admin lift is explicit:
      409 + flag + audit, never a side effect".
- [ ] Gates Backend.
- [ ] Commit: `feat(api): expose the broadcaster self-purge and the lock-aware join`.

**Abnahme:** Tests grün; `grep -c` der beiden neuen Codes in `ApiErrorCodes.cs` = 1 je Code;
Architectur-Matrix nennt beide Routen.

### T6 — Web: Knopf + Dialog, Admin-Bestätigung, Kontolösch-Hinweis, Regel-7-Kette (`sonnet`)

**Kontext:** Konzept 3.1, 3.5, T5 des Konzepts, E2/E3/E5, P5, P8, Vertrag 2.5. Regel 12
(Verhalten, nicht Vorlage). Kann **parallel zu T3–T5** laufen — die Api-Verträge stehen in diesem
Plan, die E2E läuft gegen gemocktes `/api/**`. Berührt keine Backend-Datei.

**Dateien:** `web/src/app/core/channels/channel.model.ts` (`canPurgeAsBroadcaster`,
`ChannelDataSummary`), `channel.service.ts` (+ Spec), neu `core/channels/broadcaster-lock.ts`
(+ Spec), neu `shared/channels/join-with-lock-prompt.ts` (+ Spec: 409 → Dialog → Bestätigung →
zweiter Aufruf mit Flag; Abbruch → kein zweiter Aufruf, Ergebnis `null`; anderer Fehler →
durchgereicht; Datum im Dialogtext aus `lockedAtUtc`), `features/channel-workspace/channel-workspace-layout.ts`
(Knopf „Kanaldaten löschen" `appButton="danger-quiet"` neben Verlassen/Reaktivieren, nur bei
`canPurgeAsBroadcaster`, auch im inaktiven Zustand; `purgeOwnData()`: (P5) erst
`getDataSummary`, dann `TypedConfirmDialog` mit `requiredText = channelName`; Doppelklickschutz per
Signal wie `rejoinInProgress`; 204 → Permissions invalidieren, Übersicht; 409 `account_mismatch`
und `channel_identity_unresolved` über `apiErrorTranslationKey`; `rejoin()` über den
Lock-Prompt-Helfer) + neuer Spec `channel-workspace-layout.spec.ts` **nur** für: Sichtbarkeit des
Knopfs aus den Permissions, Dialog-Rückgabe → Request/kein Request, Fehler-Mapping, 409 → Prompt →
Flag; `features/admin/admin-channels-page.ts` (`join` über den Helfer; `pendingChannel` bleibt) +
Spec-Fall; `shared/ui/account-menu.ts` (`deleteAccount()`: vor dem Dialog `listMine()`; Treffer
`isBroadcaster && isTracked` → Hinweisabsatz mit Login im Dialogtext; Fehler/leer → kein Hinweis,
Dialog öffnet trotzdem — fail-open; der Zustandsautomat aus #243 bleibt) + Spec-Fälle;
`core/i18n/api-error.ts` (zwei Codes), `web/public/i18n/de.json` + `en.json` (Codes + Schlüssel
aus 2.5); `web/e2e/support/mocks.ts` (`mockChannelPermissions`-Overrides + `canPurgeAsBroadcaster`,
neu `mockChannelDataSummary`, `mockPurgeOwnData(page, name, { status, body })`,
`mockJoinLocked(page, name, { status: 403 | 409, lockedAtUtc })`); E2E
`channel-workspace.e2e.spec.ts`, `admin-channels.e2e.spec.ts`, `account-deletion.e2e.spec.ts`.

**Designsprache:** `docs/UI-Designsprache.md` §4.2 (Tiers), §7 (`TypedConfirmDialog`-Vertrag:
`title` Pflicht, Vergleich getrimmt, case-sensitive). Kein neues UI-Primitiv; `shared/ui/` bleibt.

**E2E-Fälle (gemockt, `test`/`expect` aus `web/e2e/support/test.ts`):**
1. Broadcaster sieht den Knopf, Mod (`canPurgeAsBroadcaster: false`) nicht.
2. Flow: Knopf → Dialog zeigt Zahlen (P5) und Nachweisgrenze → Bestätigen gesperrt bis Name
   getippt → `DELETE …/data?expectedTwitchUserId=<AUTH_USER.twitchUserId>` → 204 → Übersicht.
3. 409 `account_mismatch` und 409 `channel_identity_unresolved` → Meldung, Seite bleibt.
4. Mod-Join (Admin-Kanalliste mit `isGlobalAdmin: false` ist nicht erreichbar — stattdessen
   Workspace „Bot reaktivieren") → 403 mit Code → Text „Der Streamer hat …".
5. Admin-Join in der Kanalliste → 409 → Dialog nennt das Datum → Bestätigen → zweiter Request
   trägt `liftBroadcasterLock=true`; Abbruch → genau ein Request.
6. Kontolösch-Dialog mit Hinweis (eigener Kanal getrackt) und ohne (`/mine` leer bzw. 500).
7. **Suite nur ohne Api auf `:5151`** (CLAUDE.md „Tests").

**Schritte:**
- [ ] Model, Service, Helfer (beide Hälften), Specs.
- [ ] Workspace, Admin-Liste, Konto-Menü, Specs.
- [ ] i18n (Codes + Schlüssel, beide Locales), `api-error-locales.spec.ts` grün.
- [ ] Mocks + E2E 1–7.
- [ ] Gates Frontend (Vitest, Lint, Format) und — wenn `:5151` frei — `npm --prefix web run e2e`;
      sonst nur die neuen/geänderten Spec-Dateien per `npx playwright test <datei>` und T8 fährt
      die ganze Suite.
- [ ] Commits (drei): `feat(web): let a broadcaster delete their channel's data from the workspace`
      (Model, Service, Workspace, Codes, Locales, Mocks, E2E 1–4), `feat(web): confirm before an
      admin lifts a broadcaster lock` (Helfer, Admin-Liste, Workspace-Rejoin, E2E 5),
      `feat(web): point at the channel's data in the account deletion dialog` (Konto-Menü, E2E 6).

**Abnahme:** Vitest/Lint/Format grün; E2E 1–7 grün; `grep -n "channel_identity_unresolved\|channel_locked_by_broadcaster"` in
`api-error.ts`, `de.json`, `en.json` je ein Treffer; kein Import aus `shared/` in `core/channels/`.

### T7 — Doku im Repo: Operations, CLAUDE.md-Statustabelle, Epic-Text (`sonnet`)

**Kontext:** Konzept T6 (Repo-Teil; der `infra-docs`-Teil steht in Abschnitt 7 und ist ein
Betreiber-Nachläufer beim Prod-Gang). Setzt T5 voraus (Verträge final). Die DECISIONS-Absätze sind
in T1–T5 schon geschrieben; T7 liest den fertigen Eintrag einmal ganz und glättet nur Verweise.

**Dateien:** `docs/Operations.md` — neuer Abschnitt „A broadcaster removes their own channel"
zwischen `:235` (Block list) und `:290` (Retention): Selbstbedienung neben dem E-Mail-Weg; was
gelöscht wird, was bleibt (Audit 12 Monate, Backups ≤ 60 Tage, Redis-TTLs, Logs bis Rotation);
Sperrtabelle und wie sie sich zur env-Liste verhält (env gewinnt, Sperre nicht geheim); die
Nachweisgrenze (id-lose Altzeile unter altem Login → 180-Tage-Retention); Aufhebung nur per Join
des Inhabers oder per Admin-Join mit Bestätigung (`liftedByAdmin` im Audit-Log); **ein Satz**
Restore-Grenze (E10, Wortlaut aus Konzept 4.2 — die laufende DB **vor** dem Restore lesen) mit
Verweis auf das private Runbook; **ein Satz** Mindest-Image-Version (E12: das erste Image mit
Migration `AddBroadcasterChannelLocks`; ein Rollback dahinter prüft die Tabelle nicht); der
Admin-Abschnitt aus T2 bekommt den Deploy-Hinweis `ADMIN_TWITCH_USER_IDS` vor dem Stack-Update;
„Deploying this feature"-Unterabschnitt im Stil von `:414`. `CLAUDE.md` Statustabelle Zeile E
(„#245 … Nachläufer" → umgesetzt, Deploy-Datum offen) und ggf. der Hosted-Service-Absatz
(unverändert — keine neuen Hosted Services). `docs/Feature-Ideen-2026-08-01.md`: nur, wenn dort
eine Idee #245 zugeordnet ist (grep ergab 2026-10-03 keinen Treffer → nichts). Issue-Text für
Epic #248 (Zeile D → „implemented on branch, deploy after 2026-10-08") als Vorschlag im
Abschlussbericht — **das Issue selbst ändert der Orchestrator beim Merge** (Memory: `gh issue edit`
scheitert an Projects-Classic, REST-PATCH nutzen).

**Schritte:**
- [ ] Operations.md, CLAUDE.md.
- [ ] DECISIONS-Eintrag einmal ganz lesen; `**Betrifft:**`-Zeile vollständig (alle Dateien aus
      T1–T6), Querverweise auf die 2026-09-24-Einträge (Block list, #252) und 2026-09-23
      (Join/Purge-Serialisierung); den Epic-#200-Merker (Konzept 3.7 a) als Satz.
- [ ] Commit: `docs: describe the broadcaster self-purge, its lock and the admin id allowlist`.

**Abnahme:** Operations.md hat die beiden Ein-Satz-Grenzen (E10, E12) und den
Admin-ID-Deploy-Schritt; CLAUDE.md-Tabelle aktuell.

### T8 — Gates, Coverage, Live-Verifikation, Codex-Zweitmeinung (`opus`)

**Kontext:** CLAUDE.md „Arbeitsweise" (Fertig = alle Gates grün), Regel 16, Regel 22. Letzter Task
vor dem PR.

**Gates (vollständig):**
- [ ] `dotnet build EmotePurge.slnx` 0 Warnungen; `dotnet format EmotePurge.slnx --verify-no-changes`.
- [ ] `dotnet test EmotePurge.slnx` (Docker läuft; **kein** `docker compose` aus dem Worktree).
- [ ] `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`, `format:check`.
- [ ] `npm --prefix web run e2e` — **nur wenn `:5151` frei** (`ss -ltn | grep 5151`); läuft dort
      die lokale Api aus der Live-Verifikation, erst beenden.
- [ ] `node scripts/coverage-local.mjs` — Ergebnis lesen, nicht als Urteil (CLAUDE.md „Tests");
      bei < 80 % die ungedeckten neuen Dateien benennen und nachtesten.

**Live-Verifikation (Regel 16) — lokal, isoliert.** Nicht gegen den geteilten Dev-Stack
(`emote-purge-dev`, Dev-Worker gehört bis 07.10. der Messung). Aufbau:
- [ ] Eigene Postgres/Redis als Wegwerf-Container mit **anderen Ports** (z. B. `docker run --rm -d
      -p 15433:5432 … postgres:17`, `-p 16380:6379 redis`), Connection-Strings per
      `--connection` bzw. Umgebungsvariablen **nur für diesen Prozess** (`ConnectionStrings__DefaultConnection`,
      `Redis__ConnectionString`), damit nichts in `appsettings`/User-Secrets hängen bleibt.
      `dotnet ef database update` gegen diese DB.
- [ ] Api aus dem Worktree starten: `dotnet run --project src/EmotePurge.Api` (Port `5151`), Worker
      `dotnet run --project src/EmotePurge.Worker` mit denselben Variablen. Beide Worktree-Prozesse
      sind vom Dev-Stack getrennt (eigene DB/Redis) — der geteilte Worker bleibt unberührt.
- [ ] Twitch-Login braucht User-Secrets im Worktree-Projekt (Memory „dotnet run braucht
      User-Secrets": `Auth:Twitch:ClientId/ClientSecret/TokenEncryptionKey/RedirectUri/PostLoginRedirectUrl`)
      und — wegen der Cookie-Bindung an den Content-Root — einen **frischen Login gegen diese
      Worktree-Api** (Memory „Worktree-Api verwirft lokales Cookie").

**Schritte, die der Betreiber ausführt (Cookie-Login, sein Twitch-Konto; der Agent bereitet
`curl`-Aufrufe mit Cookie-Platzhalter vor und wertet Logs/DB aus):**
1. Als Broadcaster des eigenen Kanals: Join, Daten entstehen (Worker-Log: JOIN, 7TV-Sync).
2. `DELETE /api/channels/<kanal>/data?expectedTwitchUserId=<id>` → 204; Worker-Log zeigt LEAVE
   **nach** dem Commit; DB: Zeile, Emotes, UsageStats, LiveDays, VoteSessions weg; Sperrzeile da;
   Audit `channel.purge` mit `broadcasterRequest`.
3. Dasselbe mit einer von Hand id-los gemachten Zeile (`UPDATE "Channels" SET "TwitchChannelId" =
   NULL`) → 204 über die Live-Auflösung; mit `Found`-Fremd-Simulation ist lokal nur per
   Zweitkonto möglich — ersatzweise der Integrationstest.
4. Mod-Join (Zweitkonto oder Admin-Konto mit leerer ID-Liste) → 403 mit dem Streamer-Text.
5. Inhaber-Join → 200, Sperre weg, `broadcasterLockLifted` im Audit.
6. Erneut purgen; Admin-Join (Konto in `Auth:AdminTwitchUserIds`) in der Admin-Kanalliste → Dialog
   mit Datum → Bestätigen → Request mit Flag → 200, `liftedByAdmin` + `lockedAtUtc` im Audit;
   Abbruch-Variante: kein zweiter Request (Netzwerk-Tab).
7. Sync-Gate: gesperrte ID, von Hand eine aktive id-lose Zeile anlegen (`INSERT`) und JOIN-Kommando
   publizieren (`redis-cli PUBLISH channel:bot:commands JOIN:<name>`) → Worker-Log „7TV sync
   skipped: … locked", keine Emotes; nächster Reconcile (Intervall lokal auf 1 min setzen:
   `Twitch__IdentityReconcileIntervalMinutes=1`) deaktiviert die Zeile mit `locked`.
8. Admin-Prüfung: Api einmal mit reiner Login-Liste starten (Warnung „login-based"), einmal mit
   beiden (Warnung „ignored"), einmal nur IDs (keine Warnung); `GET /api/auth/me` zeigt
   `isGlobalAdmin` passend.
9. Startlog der Api (nicht des Workers) enthält die Allowlist-Zeile genau einmal.

**Zweitmeinung (Regel 22):** `/codex:review --model gpt-6.1-sol --scope branch` über den fertigen
Branch (Memory „Codex-Review-Fallen": Session-CWD = Worktree, kein HEAD-Wechsel währenddessen);
Findings unverändert in den Abschlussbericht; Widersprüche zu einem Opus-Review entscheidet Fable
(global geregelt), nicht der Task.

- [ ] Kein Commit außer ggf. `fix:`/`test:`-Nachläufern aus den Gates (eigene Commits).
- [ ] Abschlussbericht: Gate-Zahlen, Live-Protokoll (Schritte 1–9 mit Belegzeilen), Coverage-Wert,
      Codex-Findings, offene Punkte.

**Abnahme:** alle Gates grün, Live-Schritte 1–9 belegt, Codex-Review vorgelegt. **Dann** PR gegen
`main` (Beschreibung englisch; Deploy-Hinweis „not before 2026-10-08, migration first" oben).

---

## 4. Reihenfolge, Abhängigkeiten, Parallelität

| Task | Setzt voraus | Berührt (konfliktrelevant) | Parallel möglich mit |
|---|---|---|---|
| T0 | — | nichts | — |
| T1 | T0 | `AppDbContext`, Migrations, `ServiceCollectionExtensions`, DECISIONS | T2 (nur mit manueller Konfliktauflösung) |
| T2 | T0 | `ChannelAccessService`, `ServiceCollectionExtensions`, `Program.cs`, Konfig/Doku, DECISIONS | T1 (s. o.) |
| T3 | T1 | `IChannelService`, `ChannelService`, DECISIONS | T6 |
| T4 | T1, T3 | `IChannelService`, `ChannelService`, `ChannelDeactivation`, `ChannelIdentityService`, `SevenTvSyncService`, `ChannelEndpoints` (ein Arm), DECISIONS | T6 |
| T5 | T2, T4 | `ChannelEndpoints`, `Auth/`, `ApiErrorCodes`, Api-Tests, Architectur, DECISIONS | T6 |
| T6 | T1 (nur zeitlich; inhaltlich nur dieser Plan) | ausschließlich `web/` | T3, T4, T5 |
| T7 | T5, T6 | Operations, CLAUDE.md, DECISIONS (Glättung) | — |
| T8 | T7 | ggf. kleine Fixes | — |

**Sequenziell wegen geteilter Dateien:** T3 → T4 → T5 (`ChannelService.cs`, `IChannelService.cs`,
`ChannelEndpoints.cs`, `DECISIONS.md`). **Locale-Dateien** berührt nur T6.

---

## 5. Live-Verifikation — Zuständigkeiten

Alles, was ein Twitch-Cookie braucht (Login, Join als Broadcaster/Mod/Admin, Dialoge im Browser),
ist **Betreiber-Schritt**: der Agent bereitet die Befehle (mit `<COOKIE>`/`<id>`-Platzhaltern)
und die SQL-/Redis-Handgriffe vor, der Betreiber führt sie aus, der Agent wertet Logs und DB aus.
Alles ohne Cookie (DB-Zustand, Worker-Log, Startwarnungen, `redis-cli PUBLISH`, Migrationslauf
gegen die Wegwerf-DB) macht der Agent selbst. Nie gegen `vps`/`nas` (global geregelt).

---

## 6. Deploy-Reihenfolge (nach dem bindenden #69-Lauf, nicht vor 2026-10-08)

Bereitgestellt vom Orchestrator als Befehle für den Betreiber (Prod-Handgriffe laufen von Hand;
Passwörter als Platzhalter):

1. **Migration zuerst**, per SSH-Tunnel wie in CLAUDE.md „Prod-Migration": `ssh -N -L
   15432:127.0.0.1:5433 vps`, dann `dotnet ef migrations list … --connection '…Port=15432…'`
   (erwartet genau `AddBroadcasterChannelLocks` als `(Pending)` — mehr heißt: Prod hängt zurück,
   erst durchsehen), `dotnet ef database update …`, `list` zur Gegenprobe. Additiv; das laufende
   alte Image ignoriert die Tabelle.
2. **`ADMIN_TWITCH_USER_IDS` in Portainers Stack-Environment** setzen (eigene numerische Twitch-ID;
   `GET https://api.twitch.tv/helix/users?login=<login>`). `ADMIN_TWITCH_LOGINS` bleibt vorerst
   stehen (wird mit gesetzter ID-Liste ignoriert — die Startwarnung „ignored" ist der Beleg, dass
   die IDs greifen; sie verschwindet mit dem späteren Entfernen der Login-Liste).
3. **Api und Worker gemeinsam** auf die neuen Images (`:latest` nach dem Merge-Publish — Memory
   „Prod-VPS: Betrieb und Fallen"): Stack-Update in Portainer, beide Services. Nicht nur die Api:
   Roster, Sync-Gate und Reconcile liegen in Infrastructure im Worker-Image.
4. Nachkontrolle: Api-Startlog zeigt die Allowlist-Zeile; `/api/health` 200; Admin-Bereich
   erreichbar (ID-basiert); keine `locked`-Deaktivierungen im Reconcile-Log (es gibt noch keine
   Sperre).
5. Erst danach die `infra-docs`-Nachläufer (Abschnitt 7) committen und die Rechtstexte per
   Kopierbefehl (infra-docs `5717077`) ausrollen.

---

## 7. Nachläufer in `infra-docs` (Betreiber, beim Prod-Gang — E9, E10)

Nicht Teil des Repo-Diffs; der Orchestrator legt dem Betreiber die Textvorschläge vor:

1. `emotepurge/legal/privacy.de.md` § 9 (`:170`) und `privacy.en.md`: der Satz „Auf Ihre Anfrage
   an contact@…" wird ergänzt um die Selbstbedienung („Als Streamer können Sie das auch selbst im
   Workspace Ihres Kanals auslösen: …") **und** die Nachweisgrenze (eine ältere Kanalzeile von vor
   einer Umbenennung, die sich nicht mehr Ihrem Konto zuordnen lässt, wird 180 Tage nach ihrer
   Deaktivierung automatisch gelöscht). Der E-Mail-Weg bleibt stehen.
2. § 13 (`:220`): „Nach der Wiederherstellung eines Backups kann ein nach dem Backup-Zeitpunkt
   gelöschter Kanal wieder beobachtet werden; der Betreiber wiederholt die Löschung, sobald sie ihm
   bekannt wird." (Konzept 4.2.)
3. § 14 Übersicht (`:228`): neue Zeile „Sperrvermerk (nur die Twitch-Kanal-ID): bis Sie den Kanal
   selbst wieder hinzufügen" (E9).
4. `EmotePurge-Backup-und-Restore.md` § 2.1/2.2 (`:220`, `:246`): der Runbook-Satz **vor** dem
   Restore (Konzept 4.2): aus der laufenden DB die `channel.purge`-Einträge mit `broadcasterRequest`
   seit dem Backup-Datum bzw. die Tabelle `BroadcasterChannelLocks` notieren; nach dem Restore die
   Kanäle per Admin-Purge erneut löschen und ihre IDs in `EXCLUDED_CHANNEL_IDS` oder die
   Sperrtabelle eintragen, **bevor** `api`/`worker` starten.
5. `privacy-notes.md`: Merker, dass `emotes.syncImported`-Einträge anderer Kanäle einen gelöschten
   Kanal weiter nennen (Konzept 3.7 a) — beim Prod-Gang von Epic #200 in § 8 aufnehmen.
6. Epic #248, Zeile D: Status auf „implemented (#245 branch), deploy after the binding run" — per
   REST-PATCH beim Merge.

---

## 8. Offene Punkte für den Betreiber (beim Planen aufgefallen)

1. **Zahlen im Dialog (E6/3.1) haben keine Datenquelle.** Dem Broadcaster steht kein Endpoint mit
   Abstimmungs- und Live-Tage-Zahlen zu Gebote; `/permissions` darf sie nicht tragen. Der Plan
   fügt `GET /{name}/data-summary` hinzu (P5). Alternative: Dialog nennt nur die Kategorien.
2. **„Direkt nach der env-Prüfung" vs. „unter derselben Zeilensperre" (3.4 Punkt 1 vs. F7)** ist im
   Konzept nicht vereinbar; der Plan wählt die Zeilensperre (P3). Folge: ein Mod-Join auf einen
   gesperrten Kanal sperrt kurz die Zielzeile, bevor er abgelehnt wird — harmlos.
3. **Dev-Default der ID-Liste (P10).** Vorschlag: User-Secret, Login-Liste bleibt im JSON.
   Alternative: die eigene ID ins `appsettings.json` neben den Login (öffentlich auflösbar).
4. **`core/channels` darf keinen Dialog öffnen** (Schichtregel); der Helfer liegt zweigeteilt
   (P8). Falls `shared/channels/` als Ordner unerwünscht ist: `shared/ui/` nehmen.
5. **403 für `NotBroadcaster` ohne Code** (`Results.Forbid()`, wie die Filter) — das Konzept nennt
   keinen Code; die Frontend-Meldung ist der generische 403-Text. Wer einen eigenen Code will,
   braucht einen dritten Regel-7-Eintrag.
6. **`expectedTwitchUserId` fehlt → 409** (P14, wie `/me`). Das Konzept sagt nur „Mismatch → 409".
7. **E2E-Fall „Mod-Join → 403-Text"** ist nur über den Workspace-„Reaktivieren"-Knopf erreichbar
   (die Admin-Liste sehen Mods nicht) — so geplant.
8. **Konzept 3.8 nennt `ApiFactory`/`SessionRejectionTests` „auf ID umgestellt"**; dort wird der
   Admin über das `IChannelAccessService`-Substitut gestellt, die Settings sind nur Hygiene — der
   echte Beleg liegt in den Unit-Tests der Allowlist (T2, Tests 1–6).
9. **Nach dem Deploy bleibt die Login-Liste konfiguriert** und erzeugt dauerhaft die
   „ignored"-Warnung, bis der notierte Folgeschritt sie entfernt — gewollt laut 3.8, aber als
   Dauerwarnung in der Log-Aggregation sichtbar.
