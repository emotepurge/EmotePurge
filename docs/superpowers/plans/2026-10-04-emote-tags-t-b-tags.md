# Emote-Tags T-B: Tags anlegen, zuweisen, lesen — Tabellen, Service, Endpoints, Filter, Dock, Tags-Seite (#201) — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Der Task bekommt diesen Plan, die Spec
> `docs/superpowers/specs/2026-10-04-emote-tags-design.md` (die unten je Task genannten Abschnitte)
> und die hier zitierten Dateien; er rollt die Betreiberentscheidungen E1–E34 **nicht** neu auf.
> Schritte sind als Checkbox (`- [ ]`) geführt. **Kein fertiger Code in diesem Plan** — Verträge,
> Namen, Grenzfälle und Reihenfolge ja, Rümpfe nein. Zeilenangaben gelten für `2db77d26`.
>
> Arbeitsort: eigener Worktree auf einem Branch `feat/201-t-b-tags` **von `main`**, angelegt
> **nach** dem Merge von Epic #303 auf `main` **und nach** dem bindenden Messlauf (ab 2026-10-08,
> Spec 12.2). Commits und Push erlaubt, Merge nicht (Regel 1); PR gegen `main`. Kein
> `docker compose` aus dem Worktree, kein zweiter Worker gegen die Dev-Datenbank; Testcontainers
> für `dotnet test` sind davon nicht berührt. **Nie auf `vps`/`nas` verbinden** — Prod-Befehle
> werden vorbereitet, nicht ausgeführt.

**Ziel:** Ein Kanal bekommt Tags (anlegen, umbenennen, löschen; 50 je Kanal, Name ≤ 40 Zeichen,
eindeutig je Kanal nach `Trim().ToLowerInvariant()`), Manager weisen Emotes aus der Rasterauswahl
des **aktiven** Sets einem oder mehreren Tags zu und nehmen sie wieder heraus (1000 Einträge je
Tag), alle Leseberechtigten sehen Tags als Filterdimension in der Filterzeile und auf einer eigenen
Tags-Seite je Kanal. Keine 7TV-Schreibvorgänge, keine Läufe, keine Platzierungen — die kommen mit
T-C. Der Kanal-Purge nimmt Tags per FK-Kaskade mit; die Kanalzusammenführung verweigert bei Tags
des Verlierers.

**Architektur (2–3 Sätze):** Zwei neue Tabellen (`EmoteTag`, `EmoteTagEntry`), ein Service-Paar
`IEmoteTagService`/`EmoteTagService`, eine Endpoint-Datei `EmoteTagEndpoints.cs` mit zwei Gruppen
(Lesen hinter `UsageStatsAccessAuthorizationFilter`, Pflegen hinter
`ChannelManagementAuthorizationFilter`); fünf neue Fehlercodes nach Regel 7. Im Frontend ein
`EmoteTagService` in `core/tags/`, ein Zuweisen-Dialog in `shared/tags/`, die Tag-Dimension in
`EmoteUsageFilter`, Select + Zahlen + Link in der Filterzeile, zwei Dock-Knöpfe, und eine lazy
geladene Tags-Seite als vierter Reiter. T-C baut auf exakt diesen Namen auf (Abschnitt 3 nennt sie
mit den Feldern, die T-C später **additiv** ergänzt).

**Tech-Stack:** .NET 10 Minimal API, EF Core/Npgsql (Migration `AddEmoteTags`), xUnit +
Testcontainers, NSubstitute; Angular 22 Standalone/Signals, Transloco, Vitest, Playwright;
Designsprache `docs/UI-Designsprache.md` §2.1/2.3 (Zeilen), §2.5 (Dock), §4.1/4.2 (Knöpfe), §4.5
(vergängliche Meldung), §5.3 (Feldfehler), §6.1/6.2 (Skeleton, EmptyState), §7 (Dialoge), §8.1
(Reiter), §8.6 (Up-Link), §8.7 (Aktionsflächen), §9, §10, §11, §12.

**Spec:** umgesetzt werden 5.1, 5.2, 5.5 Regeln 1, 7, 8; 6.1 (Gruppen Lesen/Pflegen), 6.2 (ohne
Platzierungsfelder), 6.3, 6.5 (T-B-Codes), 6.6 (`IEmoteTagService`-Substitution); 7.0, 7.0a; 8
(Zeilen „Tag löschen", „Emote aus Tag herausnehmen", „Kanal ohne aktives Set", „Shown set ≠ aktives
Set", „Grober Zeiger", „Kanal-Purge", „Kanalzusammenführung", „Limit erreicht", „Rennen Zuweisen ↔
Sync"); 9.1, 9.2 (ohne Lauf-Knöpfe), 9.3, 9.4 (ohne Lauf-Fläche), 9.6, 9.7 (T-B-Familien); 10;
11 (T-B-Zeilen, E2E Szenario 1); 12.1 Zeile T-B, 12.3 Punkte 4 und 5, 12.4, 12.5 Schritte 1–2.

**Abhängigkeiten:** T-B ist von T-A unabhängig (parallel möglich, beliebige Merge-Reihenfolge).
**T-C setzt T-A und T-B voraus.** E31 (Datenschutzerklärung) ist **Freigabevoraussetzung für den
Deploy** von T-B, nicht für den Merge — s. Task 12.

---

## 0. Befund gegen `2db77d26` und Abweichungen vom Spec

Verifiziert am 2026-10-04 (Dossiers B, C, D). Nichts davon ändert eine Entscheidung E1–E34.

| Thema | Befund | Folge |
|---|---|---|
| 6.6 „`IImportTargetOwnershipService` ist dort bereits ersetzt" | Falsch für T-C, irrelevant für T-B: `tests/EmotePurge.Api.Tests/ApiFactory.cs:252-277` ersetzt 26 Services, **nicht** `IImportTargetOwnershipService`. | T-B fügt `IEmoteTagService` hinzu (Task 5); den Rest klärt T-C. |
| Audit-Aktionen | Jede Aktion braucht einen Eintrag in `web/src/app/shared/audit/audit-actions.ts` (`ACTION_KEYS` → `audit.actions.*`, dazu `CHANNEL_SCOPED_ACTIONS`/`CHANNELLESS_ACTIONS`), sonst zeigt die Aktivitätsseite (`channel-activity-page.ts`) und der Admin-Audit-Log die Aktion ohne Label. Die Spec nennt nur die Backend-Konstanten. | Ergänzung in Task 10 (drei Aktionen, zwei Locales, Spec `audit-row.spec.ts`-Muster). |
| `EmoteSetIdValidationFilter` | Prüft Routenwert **zuerst, dann** Query (`EmoteSetIdValidationFilter.cs:29-48`); `EmoteSetIdValidation.IsValid`: 1–32 Zeichen `[0-9A-Za-z]`. | Leserouten tragen den Filter je Route, wie `UsageStatsEndpoints.cs` `/totals`. |
| `ChannelPermissionsDto` | `ChannelEndpoints.cs:336`, fünf Felder; Frontend `core/channels/channel.model.ts:14`. | T-B lässt sie unverändert; das Flag kommt in T-C. |
| Formprüfung der 7TV-IDs | `emote_ids_invalid` wird heute aus einem **Service-Status** gemappt (`VoteSessionEndpoints.cs:63`, `CreateVoteSessionResult.EmoteIdsInvalid`), nicht aus einem Filter. | Gleiches Muster: `EmoteTagService` liefert `EmoteIdsEmpty`/`EmoteIdsInvalid` als Status, der Handler mappt; die Formregel wird aus `VoteSessionService` **wiederverwendet**, nicht kopiert. |
| Audit-Details-Projektion | `AuditLogQueryService.ProjectDetail` (`:158-190`) kennt Import-, Set- und Titel-Formen und liefert sonst `null`. | `{ tagId }`-Details fallen auf `null` — zulässig; kein neuer Kind in T-B (Task 10 prüft nur, dass nichts wirft). |
| `EmoteUsageFilter<T extends FilterableEmote>` | `shared/emotes/emote-usage-filter.ts:5,35`; `apply(items, now?)`, `reset()`, `isAnyActive()`. | Tag-Dimension braucht `sevenTvEmoteId` auf `FilterableEmote` (Task 7). |
| `scrollWindow` | Attribut auf `cdk-virtual-scroll-viewport`, bindet an das Fenster (`usage-stats-page.html:614`). | Tags-Seite nutzt es für > 200 Einträge (9.4). |
| `/permissions` liefert Rechte, keine Konfiguration | Kein Config-Endpoint außer `GET /api/contact/config`. | Für T-B ohne Belang. |

---

## 1. Globale Zwänge

- **Schichtentreue** (CLAUDE.md-Tabelle): `Core` ohne EF/Redis/HTTP/ASP.NET; `Infrastructure` → Core;
  `Api` → Infrastructure/Core, **kein `AppDbContext`, kein `IConnectionMultiplexer` in Handlern**
  (Regel 4); `web/core` importiert nichts aus `shared`/`features`, `shared` nichts aus `features`.
- Regeln 5 (Interface für Logik mit DB), 6 (Minimal API, `Endpoints/*.cs`, `IEndpointFilter`),
  7 (Fehlercodes: `ApiErrorCodes.cs` + `api-error.ts` + beide Locales), 8 (`Emote.Id` ≠ 7TV-ID;
  Tags schlüsseln nach `SevenTvEmoteId`), 9 (`ChannelName.Normalize`; Tag-Namen analog
  `EmoteTagName.Normalize`), 10 (kein `GroupBy` über Navigationen — Zähler über skalare ID-Listen),
  11 (Infrastructure-Tests; **neuer Filter oder neue Filterkette → `tests/EmotePurge.Api.Tests`**),
  12 (co-located Specs, Verhalten nicht Vorlage), 13, 14, 18 (`dotnet format`, Prettier, Lint),
  19 (Member-Reihenfolge C#; Angular nach `web/.claude/CLAUDE.md`).
- **Sprache:** Bezeichner, Kommentare, Log-/Throw-Meldungen, Commits, DECISIONS-Einträge englisch;
  Plan deutsch. Conventional Commits; Regel 3: Vertrag/Topologie-Commit trägt seinen
  DECISIONS-Eintrag.
- **E2E:** `test`/`expect` aus `web/e2e/support/test.ts`; nur ohne Api auf `:5151`;
  vergängliche Meldungen mit `page.clock.install()` **vor** `goto`, `pauseAt`, `runFor` (nicht
  `fastForward`).
- **UI:** Primitive aus `shared/ui/` (§11/1), Farben nur Tokens, Labels konstant (§9), jede Sperre
  mit Grund (§10), keine Dauer-Controls (Select nur bei ≥ 1 Tag, E18), kein Layoutsprung, Mobile
  nach §2.5/§8.6 (Drilldown). Design-Pass je UI-Task: **vom Spec fixiert** ist Ort, Reihenfolge,
  Primitive, Texte (Vorschlag), Sichtbarkeitsbedingungen; **im Design-Pass zu entscheiden** ist
  Abstände, Micro-Zeilen-Format, Breite der Spalten ab `lg`, Marken-Form — nach §11-Checkliste,
  verifiziert mit dem Audit-Harness (§12).
- **Live-Verifikation (Regel 16):** Backend-Tasks werden gegen lokale Postgres/Redis
  (`docker compose up postgres redis`) und die Dev-Api verifiziert — nie gegen Prod.

---

## 2. Dateikarte

| Datei | Art | Verantwortung |
|---|---|---|
| `src/EmotePurge.Core/Entities/EmoteTag.cs` | neu | Entität `EmoteTag` (5.1) + `EmoteTagName` (Normalize/IsValid/MaxLength) + `EmoteTagLimits` (50/1000). |
| `src/EmotePurge.Core/Entities/EmoteTagEntry.cs` | neu | Entität (5.2). |
| `src/EmotePurge.Core/Entities/AuditLogEntry.cs` | ändern | `AuditActions.TagCreate/TagRename/TagDelete`. |
| `src/EmotePurge.Core/Services/IEmoteTagService.cs` | neu | Interface + Ergebnis-/DTO-Records (3.1). |
| `src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs` | ändern | `DbSet`s, Indizes, FK-Kaskaden. |
| `src/EmotePurge.Infrastructure/Migrations/<ts>_AddEmoteTags.cs` (+ Designer, Snapshot) | neu | Migration. |
| `src/EmotePurge.Infrastructure/Services/EmoteTagService.cs` | neu | Implementierung. |
| `src/EmotePurge.Infrastructure/Services/ChannelIdentityService.cs` | ändern | Merge-Guard „Verlierer hat Tags". |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` | ändern | `AddScoped<IEmoteTagService, EmoteTagService>()`. |
| `src/EmotePurge.Api/Endpoints/EmoteTagEndpoints.cs` | neu | `MapEmoteTagEndpoints`, Request-/Response-Records. |
| `src/EmotePurge.Api/Program.cs` | ändern | Aufruf `app.MapEmoteTagEndpoints()`. |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | ändern | fünf Codes. |
| `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteTagServiceTests.cs` | neu | Service-Tests (11). |
| `tests/EmotePurge.Infrastructure.Tests/Integration/ChannelIdentityServiceTests.cs` | ändern | Merge-Guard-Fall. |
| `tests/EmotePurge.Infrastructure.Tests/Integration/ChannelRetentionPurgeTests.cs` | ändern | Kaskade der zwei Tabellen. |
| `tests/EmotePurge.Api.Tests/ApiFactory.cs` | ändern | `IEmoteTagService` substituieren. |
| `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`, `EmoteRoutePolicyTests.cs` | ändern | Matrix + Policy je Route. |
| `tests/EmotePurge.Api.Tests/EmoteTagEndpointsTests.cs` | neu | Status-Mapping der Handler (400/404/409), Body-Formen. |
| `web/src/app/core/tags/emote-tag.model.ts`, `emote-tag.service.ts` (+ `.spec.ts`) | neu | Modelle + HttpClient-Service. |
| `web/src/app/core/i18n/api-error.ts` | ändern | fünf Codes. |
| `web/src/app/core/channels/channel.model.ts` | — | unverändert in T-B. |
| `web/src/app/shared/emotes/emote-usage-filter.ts` (+ `.spec.ts`) | ändern | Tag-Dimension (E18). |
| `web/src/app/shared/tags/tag-assign-dialog.ts` (+ `.spec.ts`) | neu | Zuweisen-Dialog (7.0). |
| `web/src/app/shared/audit/audit-actions.ts` (+ Spec-Fall) | ändern | drei Aktionen → Label-Keys. |
| `web/src/app/features/usage-stats/usage-stats-page.ts/.html` (+ `.spec.ts`) | ändern | Select, Zahlen, Link, Dock-Knöpfe, Meldungen. |
| `web/src/app/features/tags/tags-page.ts`, `tags-page.html`, `tags.routes.ts` (+ `.spec.ts`) | neu | Tags-Seite (9.3/9.4 ohne Läufe). |
| `web/src/app/features/channel-workspace/channel-workspace-layout.ts` | ändern | vierter Reiter. |
| `web/src/app/app.routes.ts` | ändern | Route `tags` (lazy, `usageStatsAccessGuard`). |
| `web/public/i18n/de.json`, `en.json` | ändern | Familien aus 9.7 (T-B-Teil), `errors.api.tag_*`, `audit.actions.tag*`. |
| `web/e2e/support/mocks.ts` | ändern | `mockTags`, `mockTagEntries`, `mockTagMutations`. |
| `web/e2e/emote-tags.e2e.spec.ts` | neu | Szenario 1 (11). |
| `web/e2e/audit/ui-audit.audit.ts` | ändern | Szenarien Tags-Seite (Liste/Detail, leer, lang), Filterzeile mit Select. |
| `docs/DECISIONS.md`, `docs/Architectur.md` (Endpoint→Filter-Tabelle), `docs/UI-Designsprache.md` (§8.1/§8.7 Referenzen) | ändern | Doku. |

---

## 3. Verträge (T-B-Stand; T-C ergänzt additiv — die T-C-Felder stehen zur Orientierung kursiv)

### 3.1 Backend

**Entitäten** (`EmotePurge.Core/Entities`):

- `EmoteTag { long Id; string ChannelId; string Name; string NormalizedName; DateTime CreatedAtUtc; Channel Channel }`.
  Konfiguration: `Name`/`NormalizedName` max 40; Unique-Index `(ChannelId, NormalizedName)`;
  Index `(ChannelId, CreatedAtUtc)`; FK → `Channel` `Cascade`; keine Inverse auf `Channel`
  (Muster `ChannelEmoteSetObservation`, `AppDbContext.cs:74-94`).
- `EmoteTagEntry { long TagId; string SevenTvEmoteId; string Alias; string ImageUrl; DateTime AddedAtUtc; EmoteTag Tag }`.
  PK `(TagId, SevenTvEmoteId)`; `SevenTvEmoteId` max 24; FK → `EmoteTag` `Cascade`; **kein** FK auf
  `Emote` (E22).
- `EmoteTagName` (static, in `EmoteTag.cs`): `MaxLength = 40`, `Normalize(string)` =
  `Trim().ToLowerInvariant()`, `IsValid(string)` = getrimmt nicht leer, ≤ 40, keine
  Steuerzeichen (`char.IsControl`). Haltung wie `ChannelName`.
- `EmoteTagLimits` (static): `MaxTagsPerChannel = 50`, `MaxEntriesPerTag = 1000` (E20; Konstanten,
  keine Konfiguration — Begründung: Produktgrenzen, keine Betriebsparameter).
- `AuditActions.TagCreate = "tag.create"`, `TagRename = "tag.rename"`, `TagDelete = "tag.delete"`.

**`IEmoteTagService`** (`EmotePurge.Core/Services/IEmoteTagService.cs`; alle Methoden nehmen den
Kanalnamen roh und normalisieren selbst — Regel 9; `tagId` wird immer als `(ChannelId, Id)` gesucht):

| Methode | Ergebnis |
|---|---|
| `ListAsync(channelName, emoteSetId?, ct)` | `EmoteTagListResult(Status: Ok \| ChannelNotFound, string? EmoteSetId, bool IsActiveSet, IReadOnlyList<EmoteTagSummaryDto> Tags)` |
| `ListEntriesAsync(channelName, tagId, emoteSetId?, ct)` | `EmoteTagEntriesResult(Status: Ok \| ChannelNotFound \| TagNotFound, string? EmoteSetId, bool IsActiveSet, *Guid? ActivationOperationId*, IReadOnlyList<EmoteTagEntryDto> Entries)` |
| `CreateAsync(channelName, name, actor, ct)` | `EmoteTagMutationResult(Status, EmoteTagDto? Tag)`; Status `Ok \| ChannelNotFound \| TagNotFound \| NameInvalid \| NameTaken \| LimitReached` |
| `RenameAsync(channelName, tagId, name, actor, ct)` | dito (ohne `LimitReached`) |
| `DeleteAsync(channelName, tagId, actor, ct)` | `EmoteTagMutationStatus` (`Ok \| ChannelNotFound \| TagNotFound`) |
| `AddEntriesAsync(channelName, tagId, sevenTvEmoteIds, ct)` | `EmoteTagAddEntriesResult(Status: Ok \| ChannelNotFound \| TagNotFound \| EmoteIdsEmpty \| EmoteIdsInvalid \| EntryLimitReached, int AddedCount, int AlreadyTaggedCount, IReadOnlyList<string> SkippedNotInSetIds)` |
| `RemoveEntriesAsync(channelName, tagId, sevenTvEmoteIds, ct)` | `EmoteTagRemoveEntriesResult(Status: Ok \| ChannelNotFound \| TagNotFound \| EmoteIdsEmpty \| EmoteIdsInvalid, int RemovedCount)` |

DTOs: `EmoteTagDto(long Id, string Name)`; `EmoteTagSummaryDto(long Id, string Name, int EntryCount, int? InSetCount, *int PlacedCount, bool Active, DateTime? ActivatedAtUtc*)`;
`EmoteTagEntryDto(string SevenTvEmoteId, string Alias, string ImageUrl, bool? InSet, string? CurrentName, *…T-C-Felder 6.2*)`.
`InSetCount`/`InSet` sind `null`, wenn `emoteSetId` nicht das aktive Set ist (6.2 Hinweis).

Serviceregeln:

- **Set-Auflösung** (6.2): `emoteSetId` fehlt → `Channel.ActiveEmoteSetId` (leer → `EmoteSetId = null`,
  `IsActiveSet = false`, Set-Felder `0`/`false`/`null`); sonst `IsActiveSet = (emoteSetId == ActiveEmoteSetId)`.
- **„Im Set"**: unarchivierte `Emote`-Zeile des Kanals mit dieser `SevenTvEmoteId` (E11). Zähler
  nach Regel 10: erst die Menge der unarchivierten `SevenTvEmoteId`s des Kanals materialisieren
  (skalare Liste), dann `EmoteTagEntry` gruppieren über die FK-Spalte `TagId` mit Filter über diese
  Liste — zwei Abfragen, kein Navigations-Join ins `GroupBy`.
- **Create/Rename** unter Kanal-Zeilensperre (`BeginTransactionAsync` + `LoadChannelForUpdateAsync`,
  Muster `PurgeIfInactiveSinceAsync` `ChannelService.cs:158-199`): Name prüfen (`NameInvalid`),
  Normalform gegen Unique (`NameTaken` — auch als Abfangen des Unique-Verstoßes bei parallelem
  Anleger), Limit 50 zählen (`LimitReached`, nur Create), Audit-Eintrag `tag.create`/`tag.rename`
  mit Details **nur `{ tagId }`** (E30), ein `SaveChangesAsync`, Commit. Rename auf denselben
  Namen in anderer Schreibweise ist erlaubt (Normalform gleich, `Name` ändert sich).
- **Delete**: Transaktion + Kanalsperre (5.5 Regel 6 gilt für Tag-Löschungen); Zähler
  `entryCount` (*T-C: `placementCount`*) vor dem Löschen für die Audit-Details `{ tagId, entryCount }`;
  `Remove(tag)` → Kaskade; Commit.
- **AddEntries** (E23, 6.3): IDs dedupen (ordinal), Form prüfen; Tag + Kanal laden (404-Fälle),
  Limit `1000` **vor** dem Schreiben prüfen: `bestehende + neue (ohne schon getaggte, ohne
  übersprungene) > 1000` → `EntryLimitReached`, nichts geschrieben; unarchivierte Zeilen zu den IDs
  laden; ohne Zeile → `SkippedNotInSetIds` (Eingabereihenfolge); bestehender Eintrag →
  `AlreadyTaggedCount`; Rest anlegen mit `Alias = Emote.Name`, `ImageUrl = Emote.ImageUrl`,
  `AddedAtUtc = UtcNow`. Unique-Verstoß durch parallelen Zuweiser → als `AlreadyTagged` zählen
  (Fang `DbUpdateException` auf dem PK, einmal erneut lesen). Kein Audit (6.3 Festlegung). Keine
  Kanalsperre (nur Tag-Tabellen, kein Zähler-Vertrag außer dem Limit — das Limit wird deshalb mit
  einem zweiten `Count` **nach** dem Insert abgesichert: liegt es über 1000, Transaktion
  zurückrollen und `EntryLimitReached`; das ist der billigere Weg gegenüber einer Sperre je
  Zuweisung). **Festlegung des Plans**, in DECISIONS zu nennen.
- **RemoveEntries** (E17): Einträge löschen (*T-C: plus alle Platzierungen `(tagId, id, ·)`*),
  `RemovedCount` = tatsächlich gelöschte. Unbekannte IDs sind kein Fehler.
- **Leseroute Entries**: Reihenfolge `AddedAtUtc`, dann `SevenTvEmoteId` (deterministisch);
  `CurrentName = Emote.Name`, wenn `InSet`, sonst `null`.
- Audit-Zeilen tragen `ChannelName` als Snapshot-String und `TargetType = "emoteTag"`,
  `TargetId = tagId` (Konvention `AuditLogEntry`).

**Merge-Guard** (5.5 Regel 8, `ChannelIdentityService.MergeAsync` `:655-679`): neben
`loserHasEmotes` ein `loserHasTags = db.EmoteTags.AnyAsync(t => t.ChannelId == loser.Id)`; bei
`true` dieselbe Verweigerung (`MergesRefused++`, beide Kanäle settled, `LogWarning` englisch mit
dem Grund „still has emote tags"). Läuft im Worker (E15 rev. 2) → Worker-Image neu bauen.

**Endpoints** (`EmoteTagEndpoints.cs`, 6.1/6.3; Routenwert `channelName`, `{tagId:long}` —
eine nicht-numerische ID ist Routing-404 ohne Body, wie bei `{sessionId:long}`):

| Gruppe | Kette | Routen |
|---|---|---|
| Lesen `/api/channels/{channelName}/tags` | `RequireAuthorization` → `ChannelNameValidationFilter` → `UsageStatsAccessAuthorizationFilter` → `RequireRateLimiting(InteractiveRead)`; je Route `EmoteSetIdValidationFilter` (Query `emoteSetId`) | `GET ""` → 200 `{ emoteSetId, isActiveSet, tags[] }` · `GET "/{tagId:long}/entries"` → 200 `{ emoteSetId, isActiveSet, entries[] }`; `ChannelNotFound` → 404 `channel_not_found`, `TagNotFound` → 404 `tag_not_found` |
| Pflegen (gleicher Präfix, zweite `MapGroup`) | `RequireAuthorization` → `ChannelNameValidationFilter` → `ChannelManagementAuthorizationFilter` → `RequireRateLimiting(Bookkeeping)` | `POST ""` `{ name }` → 201 `{ id, name }` (Location-Header nicht nötig); `PATCH "/{tagId:long}"` `{ name }` → 200; `DELETE "/{tagId:long}"` → 204; `POST "/{tagId:long}/entries"` `{ sevenTvEmoteIds }` → 200 `{ addedCount, alreadyTaggedCount, skippedNotInSetIds }`; `POST "/{tagId:long}/entries/remove"` → 200 `{ removedCount }` |

Status-Mapping: `NameInvalid` → 400 `tag_name_invalid`; `NameTaken` → 409 `tag_name_taken`;
`LimitReached` → 409 `tag_limit_reached`; `EntryLimitReached` → 409 `tag_entry_limit_reached`;
`EmoteIdsEmpty` → 400 `emote_ids_empty`; `EmoteIdsInvalid` → 400 `emote_ids_invalid`; 404 mit
Code wie `EmoteEndpoints.cs:123-139`. Ein `null`-Body / fehlendes `name` → 400 `tag_name_invalid`
(kein eigener Code). Der Handler ist dünn: Actor via `TryBuildAuditActor()` (null → 401),
Service-Aufruf, `switch` auf Status.

**Fehlercodes** (`ApiErrorCodes`): `TagNameInvalid`, `TagNameTaken`, `TagLimitReached`,
`TagNotFound`, `TagEntryLimitReached` — Werte wie in 6.5.

### 3.2 Frontend

- `core/tags/emote-tag.model.ts`: `EmoteTagSummary { id; name; entryCount; inSetCount: number | null; *placedCount; active; activatedAtUtc* }`,
  `EmoteTagList { emoteSetId: string | null; isActiveSet; tags }`, `EmoteTagEntry { sevenTvEmoteId; alias; imageUrl; inSet: boolean | null; currentName: string | null; *…* }`,
  `EmoteTagEntries { emoteSetId; isActiveSet; *activationOperationId*; entries }`,
  `AddTagEntriesResult`, `RemoveTagEntriesResult`.
- `core/tags/emote-tag.service.ts` (`providedIn: 'root'`): `list(channelName, emoteSetId?)`,
  `listEntries(channelName, tagId, emoteSetId?)`, `create(channelName, name)`,
  `rename(channelName, tagId, name)`, `delete(channelName, tagId)`,
  `addEntries(channelName, tagId, ids)`, `removeEntries(channelName, tagId, ids)` — alle
  `Observable`, Routen aus 3.1, `emoteSetId` als Query-Parameter nur wenn gesetzt.
- `EmoteUsageFilter` Tag-Dimension (E18): `tagId: Signal<number | null>`,
  `tagKeys: Signal<ReadonlySet<string> | null>`; `setTag(tagId: number | null)` (setzt Keys auf
  `null`, bis sie geladen sind), `setTagKeys(keys: ReadonlySet<string>)`; `apply` lässt bei
  `tagId !== null` nur Items mit `tagKeys.has(item.sevenTvEmoteId)` durch — **bis die Keys
  geladen sind, alle** (kein Flackern auf leer; `FilterableEmote` bekommt `sevenTvEmoteId: string`);
  `isAnyActive()` zählt `tagId !== null`; `reset()` löscht beide. Die Ladung der Keys liegt bei
  der Seite (`rxResource` über `listEntries`), nicht im Filter.
- `shared/tags/tag-assign-dialog.ts`: `TagAssignDialogData { channelName: string; sevenTvEmoteIds: readonly string[] }`;
  `TagAssignDialogResult { tagCount: number; addedCount: number; skippedNotInSetCount: number }`;
  `openTagAssignDialog(dialog, data): DialogRef<TagAssignDialogResult | undefined>` über
  `openAppDialog`. Der Dialog lädt `list`, zeigt Skeleton (§6.1), dann Checkboxen (add-only,
  keine Vorbelegung), ein Feld „Neuer Tag" mit Anlegen-Knopf (Feldfehler §5.3 für
  `tag_name_taken`/`tag_name_invalid`/`tag_limit_reached`; der neu angelegte Tag erscheint
  angehakt), Primärknopf „n Emotes zuweisen" (n aus `sevenTvEmoteIds.length`, **konstantes** Verb,
  Zahl im Label ist nach §8.7 zulässig), gesperrt ohne Haken mit Grund (§10); Bestätigen führt
  je Tag **nacheinander** `addEntries` aus, summiert, schließt mit dem Ergebnis; Fehler mitten in
  der Reihe → Banner im Dialog mit `apiErrorTranslationKey`, bereits Gelungenes bleibt (Teilerfolg
  ist ehrlicher, E23-Haltung).
- Nutzungsseite: `tagsResource` (`rxResource` über `list(channelName, activeEmoteSetId)`, nur
  wenn `canViewUsageStats`), `tagFilterEntriesResource` (über `listEntries` des gewählten Tags,
  `emoteSetId = activeEmoteSetId`), `selectedTagId` aus `usageFilter.tagId`, `selectedTag`
  computed; `assignTags()` → Dialog → Statusmeldung `tags.feedback.assigned.{one,other}` (+
  zweiter Satz `tags.feedback.skippedNotInSet.{one,other}`) als eigenes Regionspaar auf der
  Zählzeile (§4.5, `TAG_FEEDBACK_MS = 4000`, Timer vorher löschen, Cleanup); danach
  `tagsResource.reload()` und ggf. `tagFilterEntriesResource.reload()`; `removeFromTag()` →
  `removeEntries` ohne Dialog → Meldung `tags.feedback.unassigned.{one,other}` → Reloads.
  Sichtbarkeiten (7.0, 9.1, 9.2): Dock-Knöpfe nur `!isCoarse() && canManage() && importScopeCurrent() && shownSetId() === activeEmoteSetId()`
  und Auswahl > 0; „Aus Tag entfernen" zusätzlich nur bei `selectedTagId !== null`; Select nur bei
  `tags.length ≥ 1`; Inline-Gruppe nur bei gewähltem Tag; bei `shownSetId() !== activeEmoteSetId()`
  statt der Knöpfe der Satz `tags.filter.activeSetOnly`.
- Tags-Seite (`features/tags/tags-page.ts`, Route `channels/:channelName/tags`, lazy über
  `tags.routes.ts` mit `TAGS_ROUTES`, Guard `usageStatsAccessGuard` am Parent-Eintrag in
  `app.routes.ts` wie `usage-stats`): liest `channelName` als Input (Router-Binding wie die
  Nutzungsseite), `permissions` (für `canManage`), `activeSetStatus` (`EmoteAdminService.getSetStatus`
  für `activeEmoteSetId`), Set-Name aus `GET …/emote-sets` (Fallback ID), `tagsResource`,
  `selectedTagId` aus Query `?tag=` (`number | null`, ungültig → `null`), `entriesResource`.
  Aktionen T-B: „Neuer Tag" (`primary`, nur `canManage`) öffnet einen kleinen Namensdialog
  (derselbe Feld-Baustein wie im Zuweisen-Dialog → gemeinsame kleine Komponente
  `shared/tags/tag-name-field.ts` oder Wiederverwendung des Zuweisen-Dialogs im Modus „nur
  anlegen" — der Task entscheidet, Kriterium: eine Feldfehler-Logik, nicht zwei); „Umbenennen"
  (gleicher Dialog mit Vorbelegung); „Löschen" (`danger` → `ConfirmDialog` mit `danger-solid`;
  Nachricht `tags.deleteDialog.message`, Vorbereitung für T-C: der Platzierungs-Hinweis
  `tags.deleteDialog.placedHint.*` wird **erst in T-C** eingebaut, weil `placedCount` erst dort
  existiert); Rasterauswahl per `ListSelection` → Dock im Fluss mit „Aus Tag entfernen (n)"
  (`canManage`) und „Auswahl aufheben". Leerzustände nach 9.4. Unter `lg` Drilldown mit
  `BackLink` „Tags" (`channelWorkspace.tabs.tags` als Label, §8.6); Up-Link zum Kanal erbt die
  Seite vom Layout. Auf grobem Zeiger keine Rasterauswahl (Festlegung 9.4).
- Reiter: `<app-tab-link link="tags" [label]="'channelWorkspace.tabs.tags' | transloco" />` hinter
  „Nutzung", unter `@if (canViewUsageStats())`.

### 3.3 i18n (de Referenz, en gleichlautend; Wortlaute aus 9.7 sind Vorschlag)

T-B-Familien: `channelWorkspace.tabs.tags`; `tags.page.*` (`title`, `setLine` „Zahlen beziehen
sich auf das aktive Set {{set}}", `noActiveSet`, `empty.{title,description,cta}`,
`emptySelection`, `emptyEntries.{title,description}`, `micro.entries.{one,other}`,
`micro.inSet.{one,other}`, `micro.notInSet.{one,other}`, `currentName` „heute: {{name}}",
`notInSetBadge`); `tags.actions.*` (`assign`, `unassign`, `rename`, `delete`, `create`,
`clearSelection`); `tags.assignDialog.*` (`title`, `newTagLabel`, `createButton`,
`confirm.{one,other}` „{{count}} Emotes zuweisen", `noTagsHint`, `lockReason.noneChecked`);
`tags.nameDialog.*` (`createTitle`, `renameTitle`, `label`, `confirmCreate`, `confirmRename`);
`tags.deleteDialog.*` (`message` „{{tag}} wird gelöscht; die Emotes bleiben im Set.", `confirm`
„Trotzdem löschen" — **ohne** `placedHint`, der kommt in T-C); `tags.feedback.*` (`assigned.{one,other}`
„{{count}} Emotes zu {{tag}} hinzugefügt", `assignedMany.{one,other}` „… zu {{tags}} Tags",
`skippedNotInSet.{one,other}`, `unassigned.{one,other}`); `tags.filter.*` (`label` „Tag", `all`
„Alle Tags", `inSet.{one,other}` „{{count}} im Set", `notInSet.{one,other}` „{{count}} nicht im
Set", `overview` „Übersicht", `activeSetOnly` „Einspielen und Ausräumen wirken auf das aktive
Set"); `errors.api.tag_name_invalid|tag_name_taken|tag_limit_reached|tag_not_found|tag_entry_limit_reached`;
`audit.actions.tagCreate|tagRename|tagDelete`. Pluralformen über `pluralKey`.

---

## 4. Tasks

Reihenfolge: T1 → T2 → T3 → (T4 ∥ T5) → T6 → T7 → T8 → T9 → T10 → T11 → T12. T4/T5 sind
unabhängig; T6–T8 (Frontend-Bausteine) können nach T5 parallel zu T4 laufen, wenn der Vertrag 3.1
feststeht (er steht hier).

### Task 1 — Entitäten, DbContext, Migration `AddEmoteTags`

**Modell:** sonnet. **Kontext:** Spec 5.1, 5.2, 5.5/1, E21, E22; Plan 3.1 (Entitäten);
`AppDbContext.cs` (Muster `Emote` `:27-37`, `ChannelEmoteSetObservation` `:74-94`),
`ChannelEmoteSetObservation.cs` (Konstanten-Klasse), `ChannelName.cs`; CLAUDE.md „EF Core
Migrationen"; `tests/.../AddUsageStatEmoteSetIdMigrationTests.cs` (Muster).

**Dateien:** `EmoteTag.cs`, `EmoteTagEntry.cs`, `AppDbContext.cs`, Migration + Snapshot,
`AuditLogEntry.cs` (Konstanten).

**Schnittstellen — produziert:** `EmoteTag`, `EmoteTagEntry`, `EmoteTagName`, `EmoteTagLimits`,
`AuditActions.Tag*`, `AppDbContext.EmoteTags`, `AppDbContext.EmoteTagEntries`.

- [ ] Entitäten und statische Klassen nach 3.1 anlegen; XML-Kommentare englisch; Kommentar an
      `EmoteTagEntry.SevenTvEmoteId`: warum kein FK auf `Emote` (E22, Regel 8).
- [ ] `OnModelCreating` inline konfigurieren (Indizes, Längen, Kaskaden, PK des Entry).
- [ ] `dotnet ef migrations add AddEmoteTags --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api`;
      Migration lesen: nur zwei `CreateTable`, Indizes, FK `Cascade`; **rein additiv**.
- [ ] Unit-Test für `EmoteTagName` in `tests/EmotePurge.Infrastructure.Tests/Unit/EmoteTagNameTests.cs`:
      Trim, Case, 40/41, Steuerzeichen, leer/Whitespace.
- [ ] Integrationstest-Fall in `ChannelRetentionPurgeTests` (oder neu `EmoteTagCascadeTests` in
      `Integration/`): Kanal mit Tag + Entry → `PurgeAsync` **und** `PurgeIfInactiveSinceAsync`
      → beide Tabellen leer (Spec 8 „Kanal-Purge"; 10 nennt die Pflicht).
- [ ] `dotnet build`, `dotnet test` (Testcontainers), `dotnet format EmotePurge.slnx`.
- [ ] Ein Commit: `feat(core): add emote tag entities and the AddEmoteTags migration`.

**Abnahme:** Migration enthält keine Änderung an Bestandstabellen; Kaskadentest grün.

### Task 2 — `IEmoteTagService` + `EmoteTagService` (CRUD, Zuweisen, Lesen)

**Modell:** opus (Zeilensperre, Limit-Prüfung, Regel-10-Zähler, Set-Auflösung, Unique-Rennen).
**Kontext:** Spec 5.5 Regeln 1, 6, 7; 6.2 (ohne T-C-Felder), 6.3, E11, E20, E21, E23, E30; Plan
3.1; `ChannelService.cs:158-199` (Transaktion + `LoadChannelForUpdateAsync`), `ChannelQueries.cs`,
`AuditLogWrites.cs`, `VoteSessionService` (Formprüfung der 7TV-IDs, Status `EmoteIdsInvalid`),
`UsageStatQueryService.cs:51-56,94-96,115` (Regel 10, „im aktiven Set"),
`AdminChannelQueryService.cs:57-76`; `ServiceCollectionExtensions.cs:83-103`.

**Dateien:** `IEmoteTagService.cs`, `EmoteTagService.cs`, `ServiceCollectionExtensions.cs`,
`tests/.../Integration/EmoteTagServiceTests.cs`.

**Schnittstellen — konsumiert:** Task 1; `ChannelQueries.LoadChannelAsync/LoadChannelReadOnlyAsync/LoadChannelForUpdateAsync`,
`AppDbContext.AddAuditEntry`, `AuditActor`. **Produziert:** alles aus 3.1 „`IEmoteTagService`",
Registrierung `AddScoped<IEmoteTagService, EmoteTagService>()`.

- [ ] Interface mit Records und Status-Enums nach 3.1; Kommentar am Interface: Set-Auflösung,
      Bedeutung `null` bei `InSetCount`/`InSet`, dass T-C Felder additiv ergänzt.
- [ ] Implementierung nach den Serviceregeln in 3.1; Klassenkommentar nennt Sperrstrategie
      (Kanalsperre bei Create/Rename/Delete, keine bei Entries) und die Limit-Absicherung über den
      zweiten Count.
- [ ] Tests (Testcontainers, Muster `EmoteServiceTests`): Anlegen (Name getrimmt gespeichert,
      Normalform; Duplikat groß/klein/getrimmt → `NameTaken`; 40 ok / 41 → `NameInvalid`;
      Steuerzeichen; 50 ok / 51 → `LimitReached`; fremder Kanal → `ChannelNotFound`); Umbenennen
      (eigene Schreibweise erlaubt; fremder Tag → `TagNotFound`; Tag eines **anderen** Kanals →
      `TagNotFound`); Löschen (Kaskade der Einträge; Audit `{ tagId, entryCount }` ohne Name);
      Audit-Details enthalten nie den Namen (gemeinsame Assertion über alle Mutationen);
      Zuweisen (Snapshot `Alias`/`ImageUrl` aus der Zeile; archivierte Zeile → `SkippedNotInSetIds`;
      bestehender Eintrag → `AlreadyTaggedCount`; Dedupe der Eingabe; 1000 ok / 1001 →
      `EntryLimitReached` **ohne** geschriebene Zeile; leere/ungültige IDs); Herausnehmen
      (`RemovedCount`, unbekannte IDs toleriert); Lesen (Zähler `EntryCount`/`InSetCount`;
      `IsActiveSet` mit/ohne Parameter; `InSetCount == null` bei fremdem `emoteSetId`; Kanal ohne
      aktives Set → `EmoteSetId == null`); Entries-Reihenfolge `AddedAtUtc`; `CurrentName` nur bei
      `InSet`. **Zwei Transaktionen:** paralleler `CreateAsync` mit gleichem Namen (Tagged
      DbContext aus `PostgresLockProbe`) → genau ein `Ok`, einer `NameTaken`.
- [ ] Gates: `dotnet test`, `dotnet format`. Ein Commit:
      `feat(infra): add EmoteTagService for channel tags and tag entries`.

**Abnahme:** Jeder Status jeder Methode hat mindestens einen Fall; der 1001-Fall beweist „nichts
geschrieben" per Count nach dem Aufruf.

### Task 3 — Endpoints, Fehlercodes, Api-Tests

**Modell:** sonnet. **Kontext:** Spec 6.1 (Lesen/Pflegen), 6.3, 6.5, 6.6; Plan 3.1 „Endpoints";
`UsageStatsEndpoints.cs:1-60` (Gruppe), `VoteSessionEndpoints.cs:114-131` (`{id:long}`,
`TryBuildAuditActor`), `EmoteEndpoints.cs:123-139` (404 mit Code), `ChannelEndpoints.cs:180`
(409), `Program.cs:385-395`; `tests/EmotePurge.Api.Tests/ApiFactory.cs:240-293`,
`AuthFilterMatrixTests.cs:57-140, 648-657`, `EmoteRoutePolicyTests.cs:33-83`;
`docs/Architectur.md:149-165` (Tabelle).

**Dateien:** `EmoteTagEndpoints.cs`, `ApiErrorCodes.cs`, `Program.cs`, `ApiFactory.cs`,
`AuthFilterMatrixTests.cs`, `EmoteRoutePolicyTests.cs`, neu `EmoteTagEndpointsTests.cs`;
`docs/Architectur.md` (zwei Tabellenzeilen).

**Schnittstellen — konsumiert:** `IEmoteTagService` (substituiert in Tests). **Produziert:** die
sieben Routen aus 3.1, Request-Records `CreateTagRequest(string? Name)`, `RenameTagRequest`,
`TagEntryIdsRequest(IReadOnlyList<string>? SevenTvEmoteIds)`; Response-Records mit den Feldern
aus 6.2/6.3; `ApiErrorCodes.Tag*`.

- [ ] Fehlercodes; Endpoints nach 3.1 (zwei `MapGroup`s, dünne Handler, Status-`switch` mit
      `UnreachableException`-Default wie im Bestand); `Program.cs`-Aufruf.
- [ ] `ApiFactory`: `IEmoteTagService` als Substitute mit Property (Muster der anderen).
- [ ] `AuthFilterMatrixTests`: alle sieben Routen in der 401-Liste (anonym/unvollständig); je
      Gruppe 403-Fact (`CanViewUsageStatsAsync false` → Lesen 403; `CanManageChannelAsync false`
      → Pflegen 403, Lesen **nicht**, wenn `CanViewUsageStats true`); 400 `invalid_channel_name`
      vor Autorisierung auf je einer Lese- und Pflegeroute; 400 `invalid_emote_set_id` am Query
      beider Leserouten **nach** der Autorisierung (Reihenfolge wie Tracked-Preview, Plan #220).
- [ ] `EmoteRoutePolicyTests`: `InteractiveRead` für die zwei Leserouten, `Bookkeeping` für die
      fünf Pflegerouten.
- [ ] `EmoteTagEndpointsTests`: Status→HTTP je Enum-Wert (400/404/409 mit Code, 201/200/204),
      `null`-Body → 400 `tag_name_invalid`, Antwort-Formen (`emoteSetId: null` wird als JSON-`null`
      geliefert), `{tagId}` nicht numerisch → 404 ohne Body.
- [ ] `docs/Architectur.md`: zwei Zeilen in „Endpoint → filter mapping".
- [ ] Gates: `dotnet test` (Api-Tests container-frei), `dotnet format`. Ein Commit:
      `feat(api): add channel tag endpoints with five tag error codes`.

**Abnahme:** `AdminRateLimitsEndpointTests` bleibt grün (keine neue Policy); jeder neue Code
kommt in genau einem Mapping vor.

### Task 4 — Merge-Guard (Worker-Verhalten)

**Modell:** sonnet. **Kontext:** Spec 5.5 Regel 8, 3.2, E15 rev. 2, 12.4; `ChannelIdentityService.cs:569-679`,
`ChannelIdentityServiceTests.cs:320-353` (Fall „loser still has emotes").

- [ ] Bedingung `loserHasTags` neben `loserHasEmotes` (3.1 „Merge-Guard"); Logtext englisch,
      nennt beide Gründe getrennt.
- [ ] Test: Verlierer ohne Emotes, aber mit einem Tag → `MergesRefused == 1`, beide Zeilen
      unverändert, kein Audit (Spiegel des bestehenden Falls).
- [ ] `docs/DECISIONS.md`-Eintrag entsteht in Task 11 (dort als Punkt); hier nur Code + Test.
- [ ] Gates; ein Commit: `feat(infra): refuse a channel merge while the loser still has tags`.

**Abnahme:** bestehender Emote-Fall unverändert grün.

### Task 5 — Frontend-Kern: Modelle, `EmoteTagService`, Fehlercodes, Filter-Dimension

**Modell:** sonnet. **Kontext:** Spec 6.2/6.3 (Wire), E18; Plan 3.2 (Modelle, Service, Filter);
`core/emotes/emote-admin.service.ts` (HttpClient-Muster + Spec), `core/i18n/api-error.ts`,
`api-error-locales.spec.ts`, `shared/emotes/emote-usage-filter.ts` + `.spec.ts`,
`usage-stats-page.ts:1075-1077` und `vote-session-detail-page.ts` (alle Instanziierungen des
Filters — `FilterableEmote` erweitern und Kompilat prüfen).

**Dateien:** `core/tags/emote-tag.model.ts`, `emote-tag.service.ts` + `.spec.ts`,
`core/i18n/api-error.ts`, `shared/emotes/emote-usage-filter.ts` + `.spec.ts`, beide Locales
(`errors.api.tag_*`).

**Schnittstellen — produziert:** alles aus 3.2 Punkte 1–3.

- [ ] Modelle und Service; Spec mit `HttpTestingController`: Routen und Query (`emoteSetId` nur
      wenn gesetzt), Body-Formen, `remove` als `POST …/entries/remove`.
- [ ] Fünf Codes in `KNOWN_API_ERROR_CODES` + beide Locales (`api-error-locales.spec.ts` grün).
- [ ] Filter-Dimension; Spec-Fälle: `isAnyActive` mit nur Tag; `reset` löscht Tag und Keys;
      `apply` mit `tagId` ohne Keys lässt alles durch; mit Keys nur Treffer; Tag-Dimension
      kombiniert mit Namensfilter (Schnittmenge); `setTag(null)` löscht Keys.
- [ ] Gates: Vitest, Lint, Format. Ein Commit:
      `feat(web): add the tag model, service, error codes and the tag filter dimension`.

**Abnahme:** keine UI-Datei berührt; alle Instanziierungen von `EmoteUsageFilter` kompilieren.

### Task 6 — `TagAssignDialog` (und der gemeinsame Namensfeld-Baustein)

**Modell:** sonnet. **Kontext:** Spec 7.0 Schritte 2–3, E20, E23, 9.7; Plan 3.2 (Dialog), 3.3;
`shared/ui/dialog.ts` (`openAppDialog`), `dialog-shell.ts`, `confirm-dialog.ts`,
`features/usage-stats/create-vote-session-dialog.ts` (+ Spec, Muster eines Formulardialogs mit
Skeleton und Feldfehler), `docs/UI-Designsprache.md` §5.1–5.3, §6.1, §7 („Aktionszeile",
gesperrter Knopf mit Grund), §9.

**Dateien:** `shared/tags/tag-assign-dialog.ts` + `.spec.ts`; ggf. `shared/tags/tag-name-field.ts`
(+ Spec, falls eigene Logik); beide Locales (`tags.assignDialog.*`, `tags.nameDialog.*`).

**Schnittstellen — konsumiert:** `EmoteTagService.list/create/addEntries`,
`apiErrorTranslationKey`. **Produziert:** `openTagAssignDialog`, `TagAssignDialogData`,
`TagAssignDialogResult`; der Namensfeld-Baustein mit Input `initialName`, Output `submitted`,
Feldfehler-Signal.

- [ ] Dialog nach 3.2; **Design-Pass:** fix sind Checkbox-Liste, Feld „Neuer Tag" darunter,
      Aktionszeile mit gesperrtem Primärknopf + Grund; zu entscheiden ist die Liste ab 20 Tags
      (scrollbarer Block mit fester Maximalhöhe, kein zweiter Dialog) und die Position der Zahl
      „n Emotes" (im Knopf). Mobile: der Dialog wird nie auf grobem Zeiger geöffnet (der Knopf
      fehlt dort), aber die Vorlage muss bei 360 px nicht überlaufen (Audit-Szenario in Task 10).
- [ ] Spec (Regel 12): Knopf gesperrt ohne Haken mit Grund (`aria-describedby` auf den Grund);
      Anlegen mit `tag_name_taken` → Feldfehler, Dialog bleibt; Anlegen ok → neuer Tag erscheint
      angehakt; Bestätigen ruft `addEntries` je Tag **sequenziell** (Reihenfolge der Liste) und
      schließt mit Summe; Fehler beim zweiten Tag → Banner, Ergebnis enthält das Gelungene;
      Escape → `undefined`; Rolle `dialog` und zugänglicher Name (Titel).
- [ ] Gates: Vitest, Lint, Format. Ein Commit:
      `feat(web): add the tag assign dialog with inline tag creation`.

**Abnahme:** `HttpTestingController.verify()` zeigt genau die erwarteten Requests in Reihenfolge.

### Task 7 — Nutzungsseite: Tag-Filter, Inline-Gruppe, Dock-Knöpfe, Meldungen

**Modell:** opus (die Seite ist die zustandsreichste Komponente; drei neue Ressourcen, zwei
Sichtbarkeitsgatter, ein Regionspaar). **Kontext:** Spec 7.0, 7.0a, 9.1, 9.2 (ohne Knöpfe
Einspielen/Ausräumen), 8 („Shown set ≠ aktives Set", „Grober Zeiger", „Kanal ohne aktives Set",
„Rennen Zuweisen ↔ Sync"); Plan 3.2 (Nutzungsseite), 3.3; `usage-stats-page.html:213-325`
(Zeile zwei), `:1073-1225` (Dock, Slot `[selection-actions]`, Reihenfolge §8.7),
`usage-stats-page.ts` (`usageFilter :1075`, `filteredEmotes :1077`, `selection :1238`,
`importScopeCurrent :1696`, `shownSetId :645`, `activeEmoteSetId :482`, `canManage :396`,
`isCoarse :367`, `selectionPrunedFeedback :1051` + `SELECTION_PRUNED_FEEDBACK_MS :264`),
`usage-stats-page.spec.ts` (Muster Feedback-Timer, Dock-Gatter), `docs/UI-Designsprache.md` §4.5,
§8.7, §9, §10.

**Dateien:** `usage-stats-page.ts/.html/.spec.ts`; beide Locales (`tags.filter.*`,
`tags.feedback.*`, `tags.actions.assign/unassign`).

**Schnittstellen — konsumiert:** Task 5, 6. **Produziert:** Seitenmember `tagsResource`,
`tagFilterEntriesResource`, `selectedTag`, `tagFeedback`, `assignTags()`, `removeFromTag()`,
`onTagFilterChange(value)`; T-C hängt seine Knöpfe an `selectedTag` und dieselbe Inline-Gruppe.

- [ ] Ressourcen und Methoden nach 3.2; `onTagFilterChange` setzt `usageFilter.setTag`, die
      Keys folgen aus `tagFilterEntriesResource` per `effect` (Regel 14: der Filter liest
      Signale); „Filter zurücksetzen" löscht den Tag mit (E18, über `reset()`).
- [ ] Vorlage Zeile zwei: Select (`app-input-sm`, `aria-label` `tags.filter.label`, Option
      `tags.filter.all`) **zwischen** „Beobachtete ausblenden" und „Filter zurücksetzen", nur bei
      ≥ 1 Tag; Inline-Gruppe bei gewähltem Tag: `Name · k im Set · m nicht im Set` (m =
      `entryCount − inSetCount`; bei `inSetCount == null` entfällt die Zahl), Link „→ Übersicht"
      (`routerLink="../tags"`, `[queryParams]="{ tag: id }"`); Platzhalter-Satz
      `tags.filter.activeSetOnly` nur bei `shownSetId() !== activeEmoteSetId()` — in T-C kommen an
      genau diese Stelle die Knöpfe. **Design-Pass:** fix ist Zeile/Reihenfolge/`flex-wrap`; zu
      entscheiden ist Trennzeichen und Micro-Typo (§3.1) der Zahlen.
- [ ] Dock `[selection-actions]`: „Tag zuweisen…" (`appButton="neutral"`) **vor** dem Vote-Knopf
      (konstruktive Gruppe, §8.7), danach bei aktivem Tag-Filter „Aus ‚{{tag}}' entfernen (n)"
      (`neutral`, `title` nennt, dass Platzierungen mitgehen — Text schon jetzt, E17), beide unter
      dem Gatter aus 3.2.
- [ ] Zählzeile: drittes Regionspaar für `tagFeedback` (nicht die Region von
      `selectionPrunedFeedback` wiederverwenden — Begründung im Plan vom 2026-10-03, 1.3).
- [ ] Spec-Fälle: Select fehlt ohne Tags, erscheint mit Tags; Wahl → Filter-Tag gesetzt, Keys
      nach Laden gesetzt, `filteredEmotes` enthält nur Treffer; „Filter zurücksetzen" entfernt den
      Tag; Dock-Knopf fehlt bei `shownSetId !== activeEmoteSetId`, bei `isCoarse`, ohne
      `canManage`, bei leerer Auswahl; „Aus Tag entfernen" nur mit Tag-Filter; nach Zuweisen:
      Meldung mit Zahl (Timer per `vi.useFakeTimers`), Reload der Tags-Ressource, Auswahl
      unverändert; `skippedNotInSetCount > 0` → zweiter Satz; Platzhalter-Satz bei
      Fremd-Set-Ansicht.
- [ ] Gates: Vitest, Lint, Format. Ein Commit:
      `feat(web): add the tag filter, inline tag summary and assign/unassign dock actions`.

**Abnahme:** Reihenfolge im Slot ist per Spec gepinnt (Zuweisen vor Vote vor Löschen — §8.7 ist
der dokumentierte Vertrag); kein neues Dauer-Control ohne Tags.

### Task 8 — Tags-Seite, Route, Reiter

**Modell:** opus (neue Seite mit zwei Layouts, URL-Zustand, Raster, Dock im Fluss, vier
Leerzuständen). **Kontext:** Spec 9.3, 9.4 (ohne Lauf-Fläche), 9.6, 8 („Grober Zeiger", „Kanal
ohne aktives Set"), E25; Plan 3.2 (Tags-Seite), 3.3; `app.routes.ts:96-145`,
`usage-stats.routes.ts`, `channel-workspace-layout.ts:100-118`, `usageStatsAccessGuard` (Datei
finden), `shared/grid/atlas-grid.ts`, `grid-columns.ts`, `shared/emotes/emote-sprite.ts`,
`styles.css` (`.app-sprite-cell`, `.app-sprite-cell-void`), `usage-stats-page.html:600-780`
(Raster + `scrollWindow`, Void-Zelle `:716`, `dimmed` `:763`), `shared/selection/list-selection.ts`,
`shared/ui/empty-state.ts`, `back-link.ts`, `skeleton-*.ts`, `confirm-dialog.ts`,
`docs/UI-Designsprache.md` §2.1, §2.3, §2.5, §6, §8.1, §8.6, §8.7, §10.

**Dateien:** `features/tags/tags-page.ts/.html/.spec.ts`, `tags.routes.ts` (+ `.spec.ts` wie
`usage-stats.routes.spec.ts`), `app.routes.ts`, `channel-workspace-layout.ts` (+ Spec-Fall für
den Reiter), beide Locales (`channelWorkspace.tabs.tags`, `tags.page.*`, `tags.actions.*`,
`tags.nameDialog.*`, `tags.deleteDialog.*`).

**Schnittstellen — konsumiert:** Task 5, 6 (Namensfeld-Baustein). **Produziert:** `TagsPage`,
`TAGS_ROUTES`; Member, an die T-C andockt: `selectedTag`, `entriesResource`, `activeEmoteSetId`,
`canManage`, `isCoarse`, ein Slot/Bereich „Kopfzeilen-Aktionen" (Reihenfolge §8.7: konstruktiv
→ destruktiv → neutral) und ein Platz unter dem Raster für die Lauf-Fläche.

- [ ] Route `tags` unter `channels/:channelName` (lazy `loadChildren` → `TAGS_ROUTES`, Guard
      `usageStatsAccessGuard`); Reiter nach „Nutzung" unter `canViewUsageStats`.
- [ ] Seite nach 3.2/9.4: Kopf (Titel, Set-Satz oder `noActiveSet`, „Neuer Tag"), ab `lg` zwei
      Spalten — links geregelte Zeilen mit Stretched-Link (§2.3) und Micro-Zeile, rechts Detail
      (Kopfzeile mit Name, Aktionen Umbenennen/Löschen; Zustand „eingespielt" kommt in T-C); Raster
      der Einträge (64-px-Zellen, `atlasColumns`/`chunkIntoRows`, `scrollWindow` ab > 200,
      „nicht im Set" = Void + `dimmed`, Hover/Fokus-Meta-Zeile unter dem Raster mit Alias und
      „heute: …"); Rasterauswahl per `ListSelection` (nur `!isCoarse()`), Dock im Fluss mit „Aus
      Tag entfernen (n)" (`canManage`) und „Auswahl aufheben"; vier Leerzustände als `EmptyState`
      nach 9.4; URL-Zustand `?tag=` (ungültig → kein Tag, kein Fehler); unter `lg` Drilldown mit
      `BackLink`. **Design-Pass:** fix sind Primitive, Reihenfolgen, Zustände; zu entscheiden sind
      Spaltenbreiten, Micro-Zeilen-Format, die Form der Meta-Zeile.
- [ ] Löschen → `ConfirmDialog` (`danger`-Trigger, `danger-solid`-Bestätigung) ohne
      Platzierungshinweis (kommt in T-C); nach Erfolg: Wahl zurücksetzen, Reload.
- [ ] Nach jeder eigenen Aktion `tagsResource.reload()`/`entriesResource.reload()` (9.6).
- [ ] Spec-Fälle (Regel 12): Reiter nur bei `canViewUsageStats`; `?tag=` steuert die Wahl,
      ungültig → `null`; `canManage` false → keine Pflege-Knöpfe; `isCoarse` → kein Dock, keine
      Auswahl; Leerzustände je Fall (Rolle/Name, nicht Klasse); Löschen → Dialog → Service-Aufruf
      → Reload und Wahl `null`; Rename-Fehler `tag_name_taken` → Feldfehler; Drilldown-Zustand
      (Signal `isDrilldown` = `?tag` gesetzt und unter `lg`) steuert `BackLink`.
- [ ] Gates: Vitest, Lint, Format. Zwei Commits: `feat(web): add the channel tags page` und
      `feat(web): add the tags tab and lazy route`.

**Abnahme:** Layout-Shell unverändert (kein Routen-spezifischer Layoutsprung — Memory „Keine
Layout-Sprünge"); Seite rendert ohne aktives Set mit Hinweis statt Fehler.

### Task 9 — E2E Szenario 1, E2E-Mocks

**Modell:** sonnet. **Kontext:** Spec 11 E2E Punkt 1; `web/e2e/usage-atlas.e2e.spec.ts:51-82`
(`openAtlas`), `web/e2e/support/mocks.ts` (`mockChannelPermissions :646`, `mockUsageTotals`,
`mockActiveEmoteSet`, `fulfillJson`), `web/e2e/support/test.ts`, `touch-mobile.e2e.spec.ts`
(Muster grober Zeiger); CLAUDE.md „Tests" (`page.clock`).

**Dateien:** `web/e2e/support/mocks.ts` (`mockTags(page, channel, list)`, `mockTagEntries(page,
channel, tagId, entries)`, `mockTagMutations(page, channel)` mit Body-Aufzeichnung),
`web/e2e/emote-tags.e2e.spec.ts`.

- [ ] Szenario 1: Raster öffnen → zwei Zellen markieren → „Tag zuweisen…" → neuer Tag anlegen →
      bestätigen → `POST …/entries`-Body enthält genau die zwei IDs → Statusmeldung erscheint
      (`page.clock`: `install()` vor `goto`, `pauseAt`, `runFor(4000)` → verschwindet) → Select
      erscheint mit dem Tag → Wahl filtert das Raster → „→ Übersicht" → Tags-Seite zeigt Liste
      und Detail, eine Zelle dimm „nicht im Set", Umbenennen, Löschen mit Dialog.
- [ ] Mobile-Drilldown: Viewport 360 + `hasTouch`/coarse (Muster `touch-mobile`): Liste → Tag →
      Detail mit `BackLink`, keine Rasterauswahl, kein Dock-Knopf im Raster.
- [ ] Negativfall: `shownSetId !== activeEmoteSetId` (Deep-Link `?emoteSetId=` auf ein anderes
      Set, Muster `usage-atlas.e2e.spec.ts:3919`) → kein „Tag zuweisen…", Satz `activeSetOnly`.
- [ ] Gates: `npm --prefix web run e2e` komplett grün ohne Api auf `:5151`. Ein Commit:
      `test(web): cover assigning tags and the tags page end to end`.

**Abnahme:** Suite läuft ohne Realzeit-Wartezeit (Clock-Regeln eingehalten).

### Task 10 — Audit-Labels, Aktivitätsseite, Audit-Szenarien

**Modell:** sonnet. **Kontext:** Plan 0 (Befund Audit-Aktionen); `shared/audit/audit-actions.ts`,
`audit-row.ts` + `.spec.ts`, `features/channel-workspace/channel-activity-page.ts`,
`features/admin/admin-audit-log-page.ts`, `AuditLogQueryService.cs:158-190`;
`web/e2e/audit/ui-audit.audit.ts` (Szenarienliste, `requiresFinePointer`), Designsprache §12.

- [ ] `audit-actions.ts`: `tag.create`/`tag.rename`/`tag.delete` → `audit.actions.tagCreate|tagRename|tagDelete`,
      kanalgebunden; Locales; Spec-Fall im Muster `audit-row.spec.ts`.
- [ ] Nachweis (Test oder manuelle Probe im Dev-Stack): eine `tag.delete`-Zeile mit Details
      `{ tagId, entryCount }` rendert in Aktivitätsseite und Admin-Audit-Log ohne Fehler
      (`ProjectDetail` liefert `null`, die Zeile zeigt nur Aktion/Actor/Zeit).
- [ ] Audit-Harness-Szenarien: `tags-page-list-detail` (lange Namen, 40 Zeichen, 250 Einträge,
      `?tag=`), `tags-page-empty`, `usage-filter-with-tag` (`requiresFinePointer: true`, Select
      gewählt, Inline-Gruppe sichtbar), `tag-assign-dialog` (`requiresFinePointer`, `afterLoad`
      öffnet den Dialog). Lauf, Gates `horizontalOverflowPx = 0`, `contrastViolations` leer,
      `smallTargetsUnder24` ohne neue Einträge; Screenshots de/en gesichtet.
- [ ] Gates: Vitest, Lint, Format. Ein Commit:
      `feat(web): label tag audit actions and add tags audit scenarios`.

**Abnahme:** Harness-Metriken im Commit-Body.

### Task 11 — Dokumentation (DECISIONS, Designsprache, Operations)

**Modell:** sonnet. **Kontext:** `docs/DECISIONS.md:1-15`, `docs/UI-Designsprache.md` §8.1, §8.7
(Referenzen), `docs/Operations.md` (Abschnitt „Data retention" — ein Satz, dass Tags mit dem Kanal
fallen), Spec 10, 12.4; Plan 3.1 Festlegungen (Limit-Absicherung ohne Sperre, `entries/remove`
als POST, keine Audit-Zeile fürs Zuweisen).

- [ ] DECISIONS-Eintrag oben (englisch), Titel
      `### <Commit-Datum> — Channel-bound emote tags: tables, service, endpoints, filter and page (#201 T-B)`,
      `**Betrifft:**` die Dateien der Karte. Punkte: (1) Tags gehören dem Kanal, kein Ersteller
      (E1) und was sie tragen (10); (2) Schlüssel `SevenTvEmoteId`, kein FK auf `Emote` (E22);
      (3) Grenzen als Konstanten (E20) und die Limit-Absicherung über den zweiten Count statt
      Sperre; (4) Rechte Lesen/Pflegen (E9 T-B-Teil), `entries/remove` als POST; (5) Audit nur
      `tagId` (E30), kein Audit fürs Zuweisen; (6) Merge-Guard und Worker-Rebuild (E15 rev. 2);
      (7) UI-Orte (Filterzeile als Dimension, Dock-Knöpfe konstruktiv, vierter Reiter) mit Verweis
      auf §8.7; (8) was T-C ergänzt (Platzierungsfelder additiv, Flag).
- [ ] Designsprache: §8.1 Referenz „bars in … channel-workspace-layout.ts" bleibt; §8.7 ergänzt
      die Tags-Seite als dritte Seite mit Mehrfachauswahl (Dock im Fluss, wie Vote-Detail) und den
      Dock-Zuwachs der Nutzungsseite; §2.5 keine Änderung (keine Bänder auf der Tags-Seite —
      ausdrücklich als Satz).
- [ ] Operations.md „Data retention": Satz zu Tags in der Kanal-Kaskade; **kein** neuer
      Config-Schlüssel in T-B.
- [ ] Ein Commit: `docs: record the emote tags data model, rights and surfaces`.

### Task 12 — Gates, Live-Verifikation, Migration-Vorbereitung, Zweitmeinung, PR

**Modell:** Orchestrator (Opus). **Kontext:** CLAUDE.md „Fertig heißt", „Tests", „Prod-Migration",
„Lokal ausführen"; Spec 12.1 T-B, 12.4, 12.5, E31; Memory „Codex-Review-Fallen",
„coverage-local.mjs: Grenzen", „Prod-Migration handover".

- [ ] Gates vollständig: `dotnet test EmotePurge.slnx` (Docker läuft), `npm --prefix web test -- --watch=false`,
      `npm --prefix web run e2e` (ohne Api auf `:5151`), `dotnet format EmotePurge.slnx --verify-no-changes`,
      `npm --prefix web run format:check`, `npm --prefix web run lint`.
- [ ] `node scripts/coverage-local.mjs` **nach** dem letzten Commit; Zahl als Näherung im PR
      nennen (neue Dateien → nah an der Wahrheit; `usage-stats-page.ts` zieht nach unten).
- [ ] Live-Verifikation (Regel 16) lokal: `docker compose up postgres redis`, `dotnet ef database update`
      lokal, `dotnet run --project src/EmotePurge.Api`, `npm --prefix web start`; im Browser mit
      echtem Twitch-Login auf einem **eigenen** getrackten Kanal: Tag anlegen (Grenzen: 41 Zeichen,
      Duplikat), 2–3 Emotes zuweisen, Filter, Tags-Seite, Umbenennen, Löschen, Herausnehmen;
      `psql`-Blick auf `EmoteTags`/`EmoteTagEntries` und die Audit-Zeilen (kein Name in
      `DetailsJson`). Der lokale Worker **darf** gestartet werden, wenn das Messfenster vorbei ist
      (Plan läuft ohnehin erst danach) — er beweist, dass der Worker mit dem neuen EF-Modell bootet.
- [ ] Prod-Migration **vorbereiten, nicht ausführen** (Befehle für den Betreiber, PW als
      Platzhalter): Tunnel `ssh -N -L 15432:127.0.0.1:5433 vps`; dann
      `dotnet ef migrations list … --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'`
      (erwartet: genau `AddEmoteTags (Pending)`), `database update`, erneut `list`. Reihenfolge
      12.5: Migration → **Api- und Worker-Image zusammen** (Portainer) — Worker-Rebuild ist
      Pflicht (EF-Modell-Snapshot, Merge-Guard). Nichts freizugeben (kein Flag in T-B).
- [ ] **E31:** im PR-Body als Deploy-Voraussetzung: Betreiber prüft die Datenschutzerklärung
      (außerhalb des Repos) auf „vom Kanal-Manager eingegebene Bezeichner, 180 Tage nach
      Deaktivierung" und ergänzt sie vor dem Deploy. Kein Repo-Artefakt.
- [ ] Codex Sol (Regel 22) aus dem Worktree mit `--scope branch --base origin/main` (Flags in
      **einem** String; Diff-Gegenprobe vorher); Findings unverändert vorlegen; Widerspruch → Fable.
- [ ] PR gegen `main`: Titel englisch; Body: Umfang (T-B ohne Läufe), Gate-Zahlen, Coverage-
      Näherung, Codex-Ergebnis, Deploy-Checkliste (Migration → beide Images → E31), Hinweis auf
      T-C als Folge-PR.

**Abnahme:** Alle Gates in einem Lauf grün; Migration lokal angewandt und wieder sauber
(`dotnet ef migrations list` ohne Pending); PR offen, Merge beim Nutzer.

---

## 5. Spec-Abdeckung

| Spec-Stelle | Task |
|---|---|
| 5.1 `EmoteTag`, 5.2 `EmoteTagEntry`, E21, E22 | 1 |
| 5.5 Regel 1 (Eintrag ohne Zeile), Regel 7 (Regel 10 bei Zählern) | 2 |
| 5.5 Regel 8 Merge-Guard, 8 „Kanalzusammenführung", 12.4 Worker-Verhalten | 4, 12 |
| 5.5 Regel 6 (Kanalsperre bei Tag-Löschung) | 2 |
| 6.1 Gruppen Lesen/Pflegen, `tag_not_found` je Kanal, `channel_not_found` | 2, 3 |
| 6.2 Lesen (ohne Platzierungsfelder), `emoteSetId`-Semantik, `inSetCount null` | 2, 3, 5 |
| 6.3 Pflegen inkl. Audit ohne Name, `entries/remove` als POST, Unique-Rennen | 2, 3 |
| 6.5 fünf T-B-Codes, Regel 7 | 3, 5 |
| 6.6 `ApiFactory` substituiert `IEmoteTagService` | 3 |
| 7.0 Zuweisen (Dock-Knopf, Dialog, sequenzielle POSTs, Statusmeldung, Auswahl bleibt) | 6, 7 |
| 7.0a Aus Tag entfernen | 7 |
| 8 „Tag löschen" (ohne Platzierungshinweis bis T-C), „Herausnehmen", „Kanal ohne aktives Set", „Shown set ≠ aktiv", „Grober Zeiger", „Kanal-Purge", „Limit erreicht", „Rennen Zuweisen ↔ Sync" | 1, 2, 7, 8 |
| 9.1 Dock, 9.2 Filterzeile (T-B-Teil), 9.3/9.4 Tags-Seite, 9.6 Aktualität, 9.7 i18n | 7, 8 |
| 10 Datenschutz (Audit ohne Name, Kaskade, E31) | 2, 11, 12 |
| 11 Backend T-B, Kaskade, Merge-Guard; Api-Matrix; Vitest (Filter, Dialog, Seite); E2E 1 | 1–10 |
| 12.1 T-B Prüfen/Deploy, 12.3/4+5, 12.5 Schritte 1–2 | 5, 3, 12 |
| 12.6 Codex vor Merge | 12 |

## 6. Offene Punkte für den Betreiber

1. **Limit 1000 ohne Kanalsperre, Absicherung über zweiten Count** (Plan 3.1) — Alternative wäre
   die Kanalsperre auch beim Zuweisen; sie kostet jede Zuweisung einen `FOR UPDATE` auf der
   Kanalzeile, die der Sync parallel schreibt. Empfehlung: wie geplant.
2. **E31** ist Deploy-, nicht Merge-Voraussetzung — so im PR-Body; bitte bestätigen.
