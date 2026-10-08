# Set-Vorschau getrackter Kanäle: eigene Route, eigener Rate-Limit-Bucket (#220) — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Der Task bekommt diesen Plan und den Issue-Text von #220 samt Kommentar vom
> 2026-09-22; er argumentiert daraus und rollt die Betreiberentscheidungen (Abschnitt „Entschiedener
> Umfang") **nicht** neu auf. Schritte sind als Checkbox (`- [ ]`) geführt. **Kein fertiger Code in
> diesem Plan** — Verträge, Namen, Grenzfälle und Reihenfolge ja, Rümpfe nein.
>
> Arbeitsort: Worktree `/home/dev/projects/EmotePurge-220`, Branch `feat/220-set-lookup-rate-limit`,
> Basis `origin/feat/emote-sets-200`. Commits und Push auf den Feature-Branch sind erlaubt, der Merge
> nicht (Regel 1). Kein `docker compose` aus dem Worktree, kein zweiter Worker gegen die
> Dev-Datenbank (Messfenster #69/#73, Memory „Dev-Worker + :8080 gehören #73").

**Ziel:** Die Mitgliederliste eines nicht aktiven Sets auf der Usage-Seite (K4) und auf der
Vote-Detail-Seite (#227) kommt über eine **eigene Route** unter `/api/channels/…` mit **eigener
Rate-Limit-Policy** (`TrackedEmoteSetPreview`, 30 Permits/60 s je Nutzer), serverseitig abgesichert
durch den Nachweis, dass das Set zum getrackten Kanal gehört. `ForeignEmoteLookup` (10/60 s) bleibt
unverändert für alles, was tatsächlich fremd ist: K2-Zielwahl, K3-Fremdkanal, #216-Vorprüfung,
Import-Ziel-Loader, Restore-Slot, Vote-Session-Anlage.

**Quelle der Wahrheit:** Issue #220 (Optionen 1/2/3; Option 1a ist entschieden) und der
Betreiber-Brief vom 2026-09-29. Nicht im Umfang: Option 2 (Cache-Treffer vor dem Limiter), Option 3
(PermitLimit anheben), Änderungen an Budget/Breaker/Cache-TTL, der #216-Vertrag, die
#280-Invarianten, ein Schutz des Fremdkanal-Imports.

---

## 0. Befund (verifiziert am 2026-09-29 im Worktree)

Die Voranalyse aus dem Brief stimmt in der Sache; die Abweichungen sind unten unter „Korrekturen"
festgehalten, damit kein Task einer falschen Zeilenangabe hinterherläuft.

**Ist-Zustand des Aufrufpfads**

| Schicht | Stelle | Befund |
|---|---|---|
| Frontend | `web/src/app/core/seven-tv/seven-tv-emote-set.service.ts:408-421` `loadEmoteSetPreview` | ruft `GET /api/seventv/channels/{c}/emotes?emoteSetId=…[&refresh=true]` |
| Frontend | dito `:457-476` `loadCachedEmoteSetPreview` | 60-s-Client-Cache um `loadEmoteSetPreview`; Aufrufer: `usage-stats-page.ts:695` (`liveMembersResource`) und `vote-session-detail-page.ts:336` (`sessionSetMembersResource`) — **nur diese beiden** |
| Frontend | andere Aufrufer der **ungecachten** Methode | `core/emotes/import-target-loader.ts:105-106` (Import-Ziel, tracked-nicht-aktiv und untracked), `shared/seven-tv/foreign-channel-step.ts:524` (K3), `shared/seven-tv/restore-slot-preview.ts:51` (Restore-Slot) — bleiben auf der Foreign-Route |
| Api | `SevenTvEndpoints.cs:39-95` | Gruppe `/api/seventv/channels/{channelName}/emotes`: `RequireAuthorization` → `ChannelNameValidationFilter` → `RequireRateLimiting(ForeignEmoteLookup)`, Route-Filter `EmoteSetIdValidationFilter`; Handler mappt `ForeignEmoteSetLookupStatus` auf 200/404/503 (Zeilen 69-91) |
| Api | `RateLimitingOptions.cs:66` | `ForeignEmoteLookup` FixedWindow `PermitLimit = 10`; `Validate()` :98-109 |
| Api | `RateLimitRejection.cs:234-248` `PartitionPerUser`, `:277-280` `ResolveUserKey` | Partition = Twitch-User-ID, Fallback Remote-IP, Fallback `unknown` |
| Api | `Program.cs:161-163` `AddFixedWindowPolicy`, `:207` Registrierung | Policy-Registrierung, ein Helper je Limiter-Art |
| Api | `EmoteEndpoints.cs:58-101` | Dropdown-Route `GET /api/channels/{channelName}/emote-sets` — **auf `app` registriert, nicht in der `/emotes`-Gruppe** (Kommentar :48-57 erklärt warum: der Gruppen-Prefix würde sie unter `/emotes/emote-sets` nesten); Kette `RequireAuthorization` → `ChannelNameValidationFilter` → `UsageStatsAccessAuthorizationFilter` → `RequireRateLimiting(InteractiveRead)`; Status-Mapping :80-95 (Ok → 200, NoSevenTvAccount → 200 leere Liste, RateLimited/Unavailable/BudgetExhausted → 503 `foreign_channel_seventv_unavailable`) |
| Infrastructure | `VoteSessionService.cs:80-104` | Mitgliedschaftsregel „NORMAL-Set in `ListByTwitchIdAsync(channel.TwitchChannelId)` **oder** gleich `channel.ActiveEmoteSetId`"; `TwitchChannelId == null` → ungültig; NoSevenTvAccount → ungültig; RateLimited/Unavailable/BudgetExhausted → SevenTvUnavailable |
| Infrastructure | `SevenTv/HardenedForeignEmoteSetService.cs` | Set-ID-Pfad: Redis-Cache `7tvforeign:set:{setId}` (60 s), Coalescer-Schlüssel `set:{setId}`, Breaker `ForeignPreview`, providerweites Budget; `refresh` umgeht nur den Cache |
| Admin | `AdminEndpoints.cs:141` Endpoint, `:492-518` `RateLimitPolicyDescriptors` | Deskriptorliste in Registrierungsreihenfolge; `PerUserPartition = "twitch-user"` (:23) |
| Tests | `tests/EmotePurge.Api.Tests/AdminRateLimitsEndpointTests.cs:206-235` | erzwingt per Reflection über `RateLimitPolicyNames`, dass jede Policy einen Deskriptor hat |
| Tests | `EmoteRoutePolicyTests.cs:33-60` | pinnt die Policy je Route über die Endpoint-Metadaten (`EnableRateLimitingAttribute`) |
| Tests | `AuthFilterMatrixTests.cs:57-72` (401-Liste), `:454-540` (`EmoteSets_*`-Fälle der Dropdown-Route) | Muster für die neue Filterkette |
| Tests | `SevenTvForeignEmoteSetEndpointTests.cs:135-175` | 429-Fälle der Foreign-Route (10 Permits spenden, elfter → 429) — bleiben |
| i18n | `web/public/i18n/de.json:339-346`, `en.json` gleiche Stelle | `admin.rateLimits.policies.names.<PolicyName>`; `admin-monitoring-page.ts:327/426` rendert `'admin.rateLimits.policies.names.' + policy.name` |
| E2E | `web/e2e/support/mocks.ts:1061-1098` `mockForeignEmoteSetPreview` | mockt `**/api/seventv/channels/{c}/emotes*`; Tracked-Nutzer: `usage-atlas.e2e.spec.ts:1044/1142/1242`, `vote-ballot.e2e.spec.ts:366/611/676/742`; Foreign-Mock bleibt für den Import-Ziel-Loader: `emote-import.e2e.spec.ts` (7 Stellen, plus 4 eigene `page.route`); der K4-Block ab `:3890` (`mockNonActiveSetView` `:3893-3916`, Deep-Link `usage-stats?emoteSetId=set-halloween` `:3919`) braucht zusätzlich den Tracked-Mock |

**Policy-Vorrang Gruppe vs. Route (verifiziert):** `EnableRateLimitingAttribute` „Replaces any
policies currently applied to the endpoint" (Microsoft Learn, `EnableRateLimitingAttribute`-Remarks;
`RequireRateLimiting` legt genau dieses Attribut als Metadatum ab). Die Middleware liest das
**letzte** Metadatum; Gruppen-Konventionen werden vor Route-Konventionen angewandt, also gewinnt die
Route. Das ist im Repo bereits Produktionsverhalten (`EmoteEndpoints.cs:146-150`: `sync-deleted`
überschreibt die `InteractiveRead`-Gruppe mit `Bookkeeping`) und getesteter Vertrag
(`EmoteRoutePolicyTests`, Remarks: „the last `RequireRateLimiting` call on a route wins"). Es gilt
**genau eine** Policy je Endpoint, nie beide. Für die neue Route ist die Frage ohnehin
gegenstandslos: sie wird wie die Dropdown-Route auf `app` registriert und trägt ihre Policy selbst.

**Vote-Detail darf umziehen (verifiziert):** `vote-session-detail-page.ts:275` setzt
`canSelectForDelete = this.canManage`, und `sessionSetMembersResource` (`:302`) lädt nur bei
`canSelectForDelete() && !isCoarse()`. `ChannelAccessService.CanViewUsageStatsAsync` (:34-36)
beginnt mit `CanManageChannelAsync` — `canManage` ist eine echte Teilmenge von `canViewUsageStats`
(`channel.model.ts:11-12` dokumentiert das ebenso). Jeder Aufrufer, der die Vote-Detail-Lesung
auslöst, passiert also `UsageStatsAccessAuthorizationFilter`. **Kein Risiko, keine Entscheidung
nötig.** Der Kanalname der Vote-Detail-Seite ist der Routen-Kanal. Die Mitgliedschaft des Session-Sets
wurde aber **nur zur Anlagezeit** bewiesen: fällt das Set später aus der 7TV-Liste des Kanals und ist
nicht das aktive Set, antwortet die neue Route 404 → `sessionSetMembersState` `'unavailable'` →
das Massenlösch-Panel ist gesperrt (generischer Sperrtext, kein neuer i18n-Text). Das ist gewollt:
heute kann das Vote-Detail aus einem Set löschen, das nicht mehr zum Kanal gehört (die
Bestätigungs-Lesung geht direkt an 7TV, `mass-delete-panel.ts:1283`); die neue Route wendet die
Anlage-Scoping-Regel konsequent auch dort an. Bekannte Grenze, im DECISIONS-Eintrag zu nennen.

**Doppel-Request beim Deep-Link (aus dem Code hergeleitet, im Task 4 nachzuweisen):**
`liveMembersResource.params` (`usage-stats-page.ts:680-685`) liefert bei jedem Neuberechnen ein
**neues Objekt** `{ channelName, emoteSetId }`. Es liest `selectedEmoteSetId()` **und**
`activeEmoteSetId()`. Landet beim Deep-Link `?emoteSetId=B` die Set-Liste vor dem Set-Status, ist
`selectedEmoteSetId() = B` bei `activeEmoteSetId() = null` → Request 1. Landet dann der Status
(`activeEmoteSetId(): null → A`), wird `params` neu berechnet, liefert inhaltsgleich, aber
referenzverschieden → `rxResource` bricht Request 1 ab (`net::ERR_ABORTED`, der Issue-Kommentar)
und stellt Request 2. Beide haben den Limiter passiert: **2 Permits, ein Ergebnis.** Landet der
Status zuerst, gibt es nur einen Request. Dieselbe Bug-Klasse wurde am Vote-Detail bereits behoben
(DECISIONS 2026-09-22, #227 Absatz (d): `params` über ein primitives `computed()` geleitet).

**Korrekturen zur Voranalyse**

1. Die Dropdown-Route liegt **nicht** in der `InteractiveRead`-Gruppe, sondern ist eine auf `app`
   registrierte Geschwisterroute mit derselben Filterkette. Die neue Route muss aus demselben Grund
   (Gruppen-Prefix) ebenfalls auf `app` registriert werden.
2. `ResolveUserKey` steht in `RateLimitRejection.cs:277-280`, nicht :86-99.
3. `SevenTvEndpoints.cs:254-273` ist keine „Bookkeeping-Route innerhalb einer Foreign-Gruppe",
   sondern eine eigene Gruppe `/api/seventv/emote-sets/{emoteSetId}` mit `Bookkeeping` als
   Gruppen-Policy. Das Override-Präzedenz liegt in `EmoteEndpoints.cs:146-150`.
4. Die Admin-Policy-Labels liegen in `web/public/i18n/{de,en}.json` (nicht unter `web/src/assets`).
   Nebenbefund: `PublicLegal` und `Contact` haben dort **keine** Labels (s. Offene Punkte).
5. `docs/Operations.md` und `.env.example` führen keine Liste der Rate-Limit-Schlüssel (Operations
   nennt nur `RateLimiting:PublicLegal` im Rechtstext-Abschnitt) — dort ist nichts nachzuziehen.
   `README.md:147` behauptet, Rate-Limits seien Konstanten im Code — veraltet seit 2026-08-29, nicht
   Teil dieses Plans (s. Offene Punkte).
6. `docs/Architectur.md:149-158` hat eine Endpoint→Filter-Tabelle; sie nennt für die Emotes-Gruppe
   noch die 2026-08-29 abgeschaffte Policy `ExternalApi`. Die neue Route bekommt dort eine Zeile;
   die Altlast ist ein Offener Punkt.

---

## 1. Verträge

### 1.1 Route

`GET /api/channels/{channelName}/emote-sets/{emoteSetId}/emotes[?refresh=true]`

- Registriert in `EmoteEndpoints.cs` **auf `app`**, unmittelbar hinter der Dropdown-Route (Kommentar
  dort erklärt das Geschwister-Muster), Kette in dieser Reihenfolge:
  `RequireAuthorization()` → `AddEndpointFilter<ChannelNameValidationFilter>()` →
  `AddEndpointFilter<UsageStatsAccessAuthorizationFilter>()` →
  `AddEndpointFilter<EmoteSetIdValidationFilter>()` →
  `RequireRateLimiting(RateLimitPolicyNames.TrackedEmoteSetPreview)`.
  Filterreihenfolge bindend: `ChannelNameValidation` → `UsageStatsAccess` → `EmoteSetIdValidation`;
  ein Aufrufer ohne Zugriff bekommt also 403 **vor** 400 `invalid_emote_set_id` (Präzedenz der
  Usage-Stats-Routen).
  Middleware-Reihenfolge wie überall: Auth und Limiter laufen **vor** jedem Endpoint-Filter; ein
  ungültiger Set-ID-Wert bei verbrauchtem Budget antwortet 429, nicht 400 (derselbe dokumentierte
  Vertrag wie AK 14 der Foreign-Route).
- `emoteSetId` ist Routenwert (nicht Query) — `EmoteSetIdValidationFilter` prüft Routenwerte zuerst
  (Filter-Remarks). `refresh` ist optionaler Query-Bool mit C#-Default `false` wie auf der
  Foreign-Route.
- Handler-Ablauf (dünn, delegiert; Regel 4 — kein `AppDbContext`, kein `IConnectionMultiplexer`):
  1. Mitgliedschaft über den neuen Core-Vertrag (1.3) prüfen. Fail-closed: **keine Vorschau ohne
     Mitgliedschaftsnachweis.**
  2. Erst dann `IForeignEmoteSetService.GetForeignEmoteSetBySetIdAsync(channelName, emoteSetId, refresh, ct)`
     — dieselbe Kette wie heute (Redis-Cache `7tvforeign:set:{id}` 60 s, Coalescer, Breaker,
     Provider-Budget). `refresh=true` wird **nur** an diesen Aufruf durchgereicht; die
     Mitgliedschaftsprüfung liest die Set-Liste durch den eigenen 60-s-Cache des Listen-Dienstes
     (`7tvsets:{twitchId}`) und wird von `refresh` **nicht** umgangen (bewusst: die Liste ist der
     Beweis, nicht die Ware; sie hat dieselbe Frische wie das Dropdown, das den Nutzer überhaupt erst
     wählen lässt — bekannte Grenze, im DECISIONS-Eintrag zu nennen).
  3. Antwort: `Results.Ok(result.EmoteSet)` — **identische Antwortform** zur Foreign-Route (Record
     `ForeignEmoteSet`, Frontend-Modell `ForeignEmoteSetResponse` unverändert; `ChannelName` echot
     den Routen-Kanal, `SevenTvUserId` bleibt `null` wie im Set-ID-Modus).

### 1.2 Statuscodes und Fehlercodes (nur bestehende `ApiErrorCodes`, Regel 7 — kein neuer Code)

| Fall | Status | Body |
|---|---|---|
| anonym | 401 | — |
| Session ohne vollständige Claims | 401 | — |
| Kanalname formal ungültig | 400 | `invalid_channel_name` (gewinnt vor 403, wie überall) |
| Aufrufer ohne Usage-Stats-Zugriff | 403 | — (bare, wie die Dropdown-Route; gewinnt vor 400 `invalid_emote_set_id`) |
| Set-ID formal ungültig (Aufrufer mit Zugriff) | 400 | `invalid_emote_set_id` — **bevor** ein Service aufgerufen wird |
| Kanal nicht getrackt (`GetByNameAsync` null) | 404 | — (bare, exakt wie die Dropdown-Route `EmoteEndpoints.cs:66-69`) |
| Set gehört nicht zum Kanal (inkl. `TwitchChannelId == null`, `NoSevenTvAccount`, Set nicht `NORMAL` und nicht aktiv; das aktive Set ist stets Mitglied) | 404 | `emote_set_not_found` |
| Set-Liste nicht lesbar (`RateLimited`/`Unavailable`/`BudgetExhausted`) | 503 | `foreign_channel_seventv_unavailable` — dieselbe Semantik wie die Dropdown-Route `:88-92` |
| Mitglied, Vorschau-Lookup ≠ Ok | wie Foreign-Route | **dasselbe Mapping** wie `SevenTvEndpoints.cs:69-91` (NoActiveEmoteSet → 404 `foreign_channel_no_active_emote_set`, SevenTvUnavailable/SevenTvRateLimited/ProviderBudgetExhausted → 503 `foreign_channel_seventv_unavailable`, ChannelNotOnTwitch/TwitchUnavailable/NoSevenTvAccount → wie dort, obwohl der Set-ID-Pfad sie nie liefert) |
| Budget der neuen Policy verbraucht | 429 | `rate_limit_exceeded` + `Retry-After` (Limiter, unverändert) |

**Wahl von `emote_set_not_found` für „gehört nicht zum Kanal":** Der Code existiert (`ApiErrorCodes.cs:106`),
ist bereits ein 404-Code, und sein Frontend-Text („Dieses 7TV-Emote-Set ist unbekannt." /
„This 7TV emote set is unknown.") ist aus Sicht des getrackten Kanals wahr. Die Alternativen passen
nicht: `channel_not_found` (der Kanal existiert), `foreign_channel_no_active_emote_set` (der Text
behauptet „kein aktives Set"), `emote_ids_invalid` (ein 400-Code für Bodies der Vote-Session-Anlage).
Der Kommentar über `EmoteSetNotFound` („The set-centric import endpoint only") wird um die neue Route
ergänzt. Im Frontend ist kein Sonderfall nötig: `selectedEmoteSetId` fällt für Ids außerhalb der
Liste ohnehin auf das aktive Set zurück, ein 404 kann praktisch nur durch ein Rennen (Set zwischen
Listen- und Vorschau-Lesung entfernt) entstehen und landet im bestehenden `'unavailable'`-Zustand.

**Status-Mapping teilen, nicht kopieren:** Das Mapping `ForeignEmoteSetLookupStatus → IResult` wird
aus dem Foreign-Handler in einen `internal static`-Helfer in `SevenTvEndpoints` gezogen (Präzedenz:
`EmoteEndpoints.ValidateSyncImportedVocabulary` wird von `SevenTvEndpoints` mitbenutzt), sodass beide
Routen dieselbe Tabelle antworten und nicht auseinanderlaufen können.

### 1.3 Mitgliedschaft (Core-Vertrag + Infrastructure-Implementierung)

- **Core:** `Core/Services/ITrackedEmoteSetMembershipService.cs` mit einer Methode
  `CheckAsync(channelName, emoteSetId, ct)` und einem Ergebnis-Enum
  `TrackedEmoteSetMembership` mit genau vier Werten: `Member`, `NotMember`, `ChannelNotFound`,
  `SevenTvUnavailable`. Kein Payload — der Handler braucht nur die Entscheidung. Der Kanalname wird
  im Service normalisiert (Regel 9).
- **Reine Regel, geteilt mit `VoteSessionService`:** Die Entscheidung „Set gehört zum Kanal" wird als
  statische, abhängigkeitsfreie Funktion in `Core` extrahiert (Vorschlag: `Core/Services/EmoteSetMembershipRule.cs`,
  Muster `EmoteSetEditability.IsEditable`, `Core/Services/EmoteSetEditability.cs`): Eingabe
  Set-Liste (`EmoteSetList`), `channel.ActiveEmoteSetId`, gesuchte Id; Ausgabe bool; Regel exakt
  wie `VoteSessionService.cs:96-99` (NORMAL-Set in der Liste **oder** ordinal gleich der aktiven Id).
  `VoteSessionService` ruft künftig diese Funktion im `Ok`-Zweig auf — **sonst bleibt sein Switch
  unverändert** (Verhalten identisch, bestehende `VoteSessionServiceTests` bleiben grün ohne
  Anpassung). Die drei Nicht-Ok-Zweige (TwitchChannelId null, NoSevenTvAccount, Fehlerstatus) werden
  **nicht** geteilt, sondern im neuen Service mit derselben Entscheidung nachgebildet — so kann die
  Vote-Anlage nie durch diesen Plan anders antworten als heute.
- **`VoteSessionService` bleibt unverändert** über den `Ok`-Zweig hinaus (s. o.). Der neue Dienst
  beantwortet das aktive Set schon vor der Listen-Lesung (erste Zeile der Tabelle unten); die geteilte
  Regel behält ihre Aktiv-Id-Klausel für die Vote-Anlage.
- **Infrastructure:** `Infrastructure/Services/TrackedEmoteSetMembershipService.cs`, Konstruktor
  `AppDbContext` + `ISevenTvEmoteSetListService`; Kanal per `db.LoadChannelReadOnlyAsync`
  (`Persistence/ChannelQueries.cs:37`, reiner Lesepfad). Entscheidungstabelle:
  - Kanal null → `ChannelNotFound`
  - `channel.ActiveEmoteSetId` nicht leer und ordinal gleich der angefragten Set-Id → `Member`,
    **ohne** den Listen-Dienst aufzurufen. `IsBotActive` und die Ausschlussliste werden bewusst **nicht**
    geprüft (Geschwister-Semantik: die Dropdown-Route `EmoteEndpoints.cs:64-69` →
    `ChannelQueries.LoadChannelReadOnlyAsync`, `UsageStatsAccessAuthorizationFilter`/
    `ChannelAccessService.CanViewUsageStatsAsync` und die Vote-Anlage `VoteSessionService.cs:71-99`
    bedienen alle ausgeschiedene Kanäle, bis die Retention sie löscht). Das gilt auch für eine veraltete
    `ActiveEmoteSetId` eines inaktiven Kanals (dieselbe Regel wie bei der Vote-Anlage / E21).
  - `TwitchChannelId == null` → `NotMember` (wie Vote-Anlage: nichts, wonach man 7TV fragen könnte)
  - Liste `Ok` → Regel → `Member`/`NotMember`
  - Liste `NoSevenTvAccount` → `NotMember` (eine Antwort, kein Fehler — aber keine, in der das Set
    zum Kanal gehören kann)
  - Liste `RateLimited`/`Unavailable`/`BudgetExhausted` → `SevenTvUnavailable`
  - unbekannter Status → `UnreachableException` (Muster der Dropdown-Route)
  - `ActiveEmoteSetId == ""` (nie synchronisiert): zählt nie als Treffer (die Zeile oben verlangt
    „nicht leer"; die Id-Validierung lässt Leerstrings ohnehin nicht durch), also entscheidet allein
    die Liste. Ein Testfall.
- Registrierung **nur** in `ServiceCollectionExtensions.AddEmotePurgeInfrastructure` (Scoped, neben
  `IEmoteSetOwnershipService` :95).
- Kosten: für das aktive Set keine Listen-Lesung, sonst eine je Aufruf, im Normalfall aus dem 60-s-Cache, den das Dropdown derselben
  Seite gerade erst gefüllt hat; kalt eine gehärtete 7TV-Anfrage je Kanal und Minute — über alle
  Nutzer geteilt, unter Budget/Breaker des Listen-Dienstes.

### 1.4 Policy

- Name: `RateLimitPolicyNames.TrackedEmoteSetPreview` (Konstante `"TrackedEmoteSetPreview"`), mit
  Doc-Kommentar in der Art der Nachbarn: welche Route, per-Nutzer-Hälfte, providerweite Hälfte bleibt
  beim Hardening-Decorator.
- `RateLimitingOptions.TrackedEmoteSetPreview`: `FixedWindowPolicy`, Default `PermitLimit = 30`,
  Fenster = `RateLimitRejection.Window` (60 s, für alle Fixed-Window-Policies gemeinsam);
  `Validate()` ruft `TrackedEmoteSetPreview.Validate(nameof(...))` in derselben Reihenfolge wie die
  Registrierung. Konfigurationsschlüssel: `RateLimiting:TrackedEmoteSetPreview:PermitLimit`, per
  Umgebungsvariable `RateLimiting__TrackedEmoteSetPreview__PermitLimit`, Wirkung nach Neustart.
- `Program.cs`: Registrierung über den vorhandenen Helper `AddFixedWindowPolicy` **direkt hinter**
  `ForeignEmoteLookup` (:207), mit Kommentar, der die Abgrenzung erklärt (Lastprofil „Set-Wechsel
  eines getrackten Kanals, ein gecachter Vorschau-Aufruf je Wechsel" vs. „Fremdkanal-Import, bis zu
  zehn paginierte 7TV-Aufrufe je Lookup"). Partitionierung `PartitionPerUser` — dieselbe wie
  `ForeignEmoteLookup`.
- Warum 30: Das Client-Cache (60 s) fängt A→B→A ab; was bleibt, sind echte Erst-Wechsel plus die
  `refresh=true`-Nachladungen durch `channel.synced` und eigene Läufe. 30/min deckt einen Nutzer,
  der jede zweite Sekunde ein anderes Set öffnet, und bleibt eine Missbrauchsgrenze, kein
  Provider-Surrogat (Programm-Kommentar `Program.cs:120-128`).

### 1.5 Telemetrie und Admin-UI

- `AdminEndpoints.RateLimitPolicyDescriptors`: `RateLimitPolicyDescriptor.FixedWindow(TrackedEmoteSetPreview, options.TrackedEmoteSetPreview, PerUserPartition)`
  direkt hinter `ForeignEmoteLookup`, Kommentar analog (providerweite Hälfte bewusst nicht gelistet).
  `AdminRateLimitsEndpointTests.Get_ListsEveryPolicyNameTheAppKnows_…` schlägt sonst fehl — das ist
  der gewollte Zwang.
- `RateLimitTelemetryMiddleware` braucht nichts: sie zählt nach Policy-Name und Routen-Template.
- i18n: `admin.rateLimits.policies.names.TrackedEmoteSetPreview` in `web/public/i18n/de.json`
  („Set-Vorschau getrackter Kanäle (7TV)") und `en.json` („Tracked-channel set preview (7TV)") —
  Wortlaut ist Vorschlag, beide Locales im selben Commit.

### 1.6 Frontend

- `loadCachedEmoteSetPreview` ruft künftig die neue Route:
  `/api/channels/${normalizeChannelName(c)}/emote-sets/${encodeURIComponent(emoteSetId)}/emotes`,
  Query nur noch `refresh=true` bei Bedarf (kein `emoteSetId`-Query mehr). Pfad-Encoding wie in
  #216 (`reportImportedToSet`, `loadEditableSetPreCheck`; Commit 9e01d8df). Der 60-s-Client-Cache
  (`EMOTE_SET_PREVIEW_CACHE_TTL_MS`, `cachedPreviews`) bleibt wie er ist.
- **Weg von `loadEmoteSetPreview` als Unterbau:** `loadCachedEmoteSetPreview` darf die ungecachte
  Methode nicht mehr aufrufen, sonst zieht sie auf die Foreign-Route zurück. Sauberste Form: ein
  privater Loader für die Tracked-Route, den nur der Cache-Wrapper nutzt; `loadEmoteSetPreview`
  bleibt byte-identisch für K2/K3/Import-Ziel/Restore-Slot.
- Doc-Kommentare in `seven-tv-emote-set.service.ts` (:28-29, :424-456) und `usage-stats-page.ts:690-694`
  nennen den geteilten Bucket als „Follow-up-Issue" — das ist jetzt erledigt; Kommentare auf den
  neuen Stand bringen (kurz, kein Roman).
- Aufrufer-Inventar (verifiziert, s. Abschnitt 0): **umziehen** = `usage-stats-page.ts:695`,
  `vote-session-detail-page.ts:336` (beide über den Cache-Wrapper, also automatisch). **Bleiben** =
  `import-target-loader.ts:105-106`, `foreign-channel-step.ts:524`, `restore-slot-preview.ts:51`,
  sowie `listForeignChannelEmoteSets` (`:397`), `loadEditableSetPreCheck`, und die
  Vote-Session-Anlage (Backend, `VoteSessionEndpoints.cs:80`).

---

## 2. Tasks

Reihenfolge ist bindend: 2 braucht 1, 3 braucht 2, 4 braucht 3 (sein Test benutzt die neue URL),
5 braucht 4, 6 braucht 5. Jeder Task endet mit den
in ihm genannten Gates; „grün" ist keine Fertigmeldung ohne Blick auf die Naht (Memory „Grüne
Suiten keine Fertigmeldung").

### Task 1 — Mitgliedschaftsregel und -dienst (Core + Infrastructure)

**Kontext für den Subagent:** Abschnitt 1.3 dieses Plans; `VoteSessionService.cs:80-104`;
`Core/Services/EmoteSetEditability.cs` als Muster für eine reine Core-Regel;
`ISevenTvEmoteSetListService.cs` (Status-Enum, `EmoteSetList`, `EmoteSetSummary.Kind`);
`Persistence/ChannelQueries.cs`; `tests/EmotePurge.Infrastructure.Tests/Integration/VoteSessionServiceTests.cs`
(Fixture `PostgresFixture`, NSubstitute für den Listen-Dienst) und
`Unit/EmoteSetEditabilityTests.cs` als Testmuster; CLAUDE.md Regeln 4, 5, 9, 11, 19.

- [ ] Reine Regel in `Core` anlegen (Name/Ort s. 1.3), Doc-Kommentar nennt beide Nutzer.
- [ ] `VoteSessionService` im `Ok`-Zweig auf die Regel umstellen; Switch sonst unverändert.
- [ ] Interface `ITrackedEmoteSetMembershipService` + Enum in `Core/Services/`.
- [ ] Implementierung in `Infrastructure/Services/`, Member-Reihenfolge nach Regel 19, Registrierung
      in `AddEmotePurgeInfrastructure`.
- [ ] Tests, Unit (`tests/EmotePurge.Infrastructure.Tests/Unit/`): Regel — NORMAL-Treffer, Treffer nur
      über aktive Id, Set in Liste aber Kind ≠ NORMAL (PERSONAL/GLOBAL/SPECIAL), leere Liste + aktive
      Id gleich, leere Liste + aktive Id leer, Groß/Klein der Id (ordinal, kein Treffer).
- [ ] Tests, Integration (`Integration/`, `PostgresFixture`, Listen-Dienst substituiert): je ein
      Fall pro Zeile der Entscheidungstabelle in 1.3 inkl. `ChannelNotFound`, `TwitchChannelId null`,
      `NoSevenTvAccount`, alle drei Fehlerstatus, Normalisierung des Kanalnamens (`HandOfBlood` →
      Treffer), und dass der Listen-Dienst bei `ChannelNotFound`/`TwitchChannelId null` **nicht**
      aufgerufen wird. Dazu: aktives Set → `Member` und der Listen-Dienst wird **nicht** aufgerufen
      (`DidNotReceive`); ein deaktivierter Kanal (`IsBotActive = false`) antwortet wie die
      Dropdown-Route — Set in der Liste → `Member`, aktives Set → `Member` ohne Listen-Aufruf (auch mit
      veralteter `ActiveEmoteSetId`), nie `ChannelNotFound`.
- [ ] `VoteSessionServiceTests` unverändert grün.
- [ ] Gates: `dotnet build EmotePurge.slnx`, `dotnet test EmotePurge.slnx` (Docker läuft),
      `dotnet format EmotePurge.slnx --verify-no-changes`.
- [ ] Commit: `feat(infrastructure): decide whether a 7TV set belongs to a tracked channel` (Conventional
      Commit; die Regel-Extraktion aus `VoteSessionService` ist Teil desselben Commits, weil sie
      dessen Verhalten nicht ändert und der neue Dienst sie braucht).

**Abnahme:** Interface + Implementierung + Registrierung existieren, Tests decken jede Tabellenzeile,
`git diff` an `VoteSessionService` zeigt nur den `Ok`-Zweig.

### Task 2 — Policy, Route, Telemetrie, Api-Tests, DECISIONS

**Kontext für den Subagent:** Abschnitte 0 (Policy-Vorrang), 1.1, 1.2, 1.4, 1.5; Task-1-Ergebnis;
`EmoteEndpoints.cs:48-101` (Dropdown-Route als Vorlage), `SevenTvEndpoints.cs:39-95`
(Status-Mapping, `refresh`-Parameter), `RateLimitingOptions.cs`, `RateLimitPolicyNames.cs`,
`Program.cs:120-230`, `AdminEndpoints.cs:141-180` und `:492-518`, `ApiErrorCodes.cs:100-106`,
`EmoteSetIdValidationFilter.cs` (Doc listet die Routen), `tests/EmotePurge.Api.Tests/ApiFactory.cs`
(Substitute-Muster; Handler-Services werden **vor** der Filterkette aufgelöst, also muss der neue
Dienst substituiert werden), `EmoteRoutePolicyTests.cs`, `AuthFilterMatrixTests.cs:57-140` und
`:454-540`, `SevenTvForeignEmoteSetEndpointTests.cs:135-175`, `AdminRateLimitsEndpointTests.cs:206-235`;
`docs/DECISIONS.md:1-15` (Format, Sortierung, `**Betrifft:**`-Zeile; neue Einträge englisch);
`docs/Architectur.md:149-158`; CLAUDE.md Regeln 3, 4, 6, 7, 11, 19.

- [ ] `RateLimitPolicyNames.TrackedEmoteSetPreview`, `RateLimitingOptions.TrackedEmoteSetPreview`
      (Default 30) inkl. `Validate()`, Registrierung in `Program.cs` hinter `ForeignEmoteLookup`.
- [ ] Deskriptor in `AdminEndpoints.RateLimitPolicyDescriptors`; i18n-Labels de/en (1.5).
- [ ] Status-Mapping der Foreign-Route in einen `internal static`-Helfer ziehen (1.2); der
      Foreign-Handler ruft ihn — keine Verhaltensänderung, `SevenTvForeignEmoteSetEndpointTests`
      bleiben unverändert grün.
- [ ] Neue Route in `EmoteEndpoints.cs` (1.1) mit Kommentar: warum auf `app`, warum eigene Policy,
      warum Mitgliedschaft vor Vorschau, warum `refresh` die Liste nicht umgeht.
- [ ] Kommentare nachziehen: `RateLimitPolicyNames.ForeignEmoteLookup` (welche Routen), `ApiErrorCodes.EmoteSetNotFound`
      (zweite Route), `EmoteSetIdValidationFilter` (Routenliste), `UsageStatsAccessAuthorizationFilter`
      (Endpoint-Aufzählung ist ohnehin veraltet — nur die neue Route ergänzen, nicht neu schreiben).
- [ ] `ApiFactory`: Substitute für `ITrackedEmoteSetMembershipService`, Scoped registriert wie
      `ForeignEmoteSet`; `ClearReceivedCalls()` im `AuthFilterMatrixTests`-Konstruktor ergänzen.
- [ ] `EmoteRoutePolicyTests`: neue `InlineData`-Zeile → `TrackedEmoteSetPreview`.
- [ ] `AuthFilterMatrixTests`: neue Route in beide 401-Theorien; 403 ohne Usage-Stats-Zugriff (Dienst
      nicht aufgerufen); 400 `invalid_channel_name` gewinnt vor 403 — eigener Fall für die neue
            nach dem Muster `ChannelNameValidation_Answers400_BeforeAnyAuthorizationFilterRuns`
      (:1121-1135; die bestehende Theorie deckt nur `GET /api/channels/{channelName}`); 403 **vor**
      400 `invalid_emote_set_id` (Aufrufer ohne Zugriff mit formal ungültiger Set-Id → 403, Dienst nicht
      aufgerufen); 400 `invalid_emote_set_id` für Aufrufer mit Zugriff ohne Dienst-Aufruf; 404 bare bei
      `ChannelNotFound`.
- [ ] Neue Datei `TrackedEmoteSetPreviewEndpointTests.cs` (Muster `SevenTvForeignEmoteSetEndpointTests`):
      404 `emote_set_not_found` bei `NotMember` **und** `ForeignEmoteSet.DidNotReceive()`; 503
      `foreign_channel_seventv_unavailable` bei `SevenTvUnavailable` ohne Vorschau-Aufruf; 200 mit
      durchgereichtem `ForeignEmoteSet` bei `Member`, und dass `GetForeignEmoteSetBySetIdAsync` mit
      genau (kanal, set-id, refresh=true) aufgerufen wird, wenn `?refresh=true` gesetzt ist, sonst
      `false`; jede Nicht-Ok-Vorschau-Antwort auf dieselben Codes wie die Foreign-Route (Theory über
      die Status-Werte); 429 nach 30 gespendeten Permits (Zahl aus `RateLimitingOptions` lesen, nicht
      hart codieren — Präzedenz: die Foreign-Tests nennen 10 im Kommentar, `AdminRateLimitsEndpointTests`
      überschreibt Kapazität per Konfiguration, beides ist zulässig); **Unabhängigkeit der Buckets:**
      30 Permits auf der neuen Route spenden, danach antwortet die Foreign-Route demselben Nutzer
      noch ≠ 429 — und umgekehrt 10 auf der Foreign-Route, die neue antwortet noch ≠ 429.
- [ ] `AdminRateLimitsEndpointTests`: Reflection-Test grün; zusätzlich Kapazität 30 und Partition
      `twitch-user` für die neue Policy pinnen (wie :234-238 für `ForeignEmoteLookup`).
- [ ] `docs/DECISIONS.md`: neuer Eintrag **oben** (englisch, Titel mit Datum 2026-09-29), Inhalt
      s. Abschnitt 3 — in diesem Commit die Punkte 1–5 und 7; die `**Betrifft:**`-Zeile nennt nur
      Dateien, die nach Task 1/2 existieren (Task 3 erweitert sie, Task 4 fügt Punkt 6 hinzu).
- [ ] `docs/Architectur.md`: Zeile in der Endpoint→Filter-Tabelle für die neue Route (Kette + Policy).
      Optional in derselben Änderung: die veraltete `ExternalApi`-Nennung der Emotes-/Usage-Stats-
      Zeilen korrigieren — **nur, wenn der Betreiber den Offenen Punkt 3 freigibt**.
- [ ] Gates: `dotnet test EmotePurge.slnx`, `dotnet format EmotePurge.slnx --verify-no-changes`;
      da der Task `web/public/i18n/{de,en}.json` ändert, zusätzlich
      `npm --prefix web run format:check` und `npm --prefix web test -- --watch=false`.
- [ ] Ein Commit: `feat(api): serve a tracked channel's set preview from its own rate-limit bucket` —
      enthält Policy, Route, Telemetrie, i18n-Labels, Tests, den DECISIONS-Eintrag (Punkte 1–5, 7) und
      die Architectur-Zeile (Regel 3: Vertragsänderung und Eintrag im selben Commit).

**Abnahme:** `EmoteRoutePolicyTests` pinnt die Policy, `AuthFilterMatrixTests` deckt 401/403/404/400
in der richtigen Reihenfolge, die Bucket-Unabhängigkeit ist in beide Richtungen getestet, der
Reflection-Test der Admin-Deskriptoren ist grün, der DECISIONS-Eintrag steht ganz oben.

### Task 3 — Frontend auf die neue Route

**Kontext für den Subagent:** Abschnitt 1.6; `seven-tv-emote-set.service.ts:400-476` und dessen
`.spec.ts:150-320` (URL-Matcher der bestehenden Cache-Tests); `usage-stats-page.spec.ts` Stellen
`:2783`, `:3251` (`liveListRequests`), `:3501`, `:4940`, `:5012`, `:5244`;
`vote-session-detail-page.spec.ts:670`, `:703` (`EMOTE_SET_PATH`); `web/e2e/support/mocks.ts:1061-1098`;
`web/e2e/usage-atlas.e2e.spec.ts:875-890`, `:1044`, `:1142`, `:1242`; `web/e2e/vote-ballot.e2e.spec.ts:366`,
`:611`, `:676`, `:742`; `web/.claude/CLAUDE.md`; CLAUDE.md Regel 12, 18.

- [ ] Service: privater Tracked-Loader + `loadCachedEmoteSetPreview` darauf umstellen;
      `loadEmoteSetPreview` unverändert lassen (Diff darf diese Methode nicht berühren).
- [ ] `seven-tv-emote-set.service.spec.ts`: alle `loadCachedEmoteSetPreview`-Fälle auf die neue URL
      (Matcher: exakter Pfad bzw. Regex `…/emote-sets/[^/]+/emotes$` — **nicht** das Präfix
      `/api/channels/a/emote-sets`, das auch die Dropdown-Anfrage träfe; `refresh` als einziger
      Query-Parameter bei `refresh: true`, sonst keiner);
      neuer Fall: Set-ID mit URL-relevanten Zeichen wird per `encodeURIComponent` encodiert, Kanalname
      normalisiert (`HandOfBlood` → `handofblood` im Pfad); Regressionsfall: `loadEmoteSetPreview`
      ruft weiterhin `/api/seventv/channels/{c}/emotes?emoteSetId=`.
- [ ] `usage-stats-page.spec.ts` und `vote-session-detail-page.spec.ts`: Request-Matcher auf die neue
      URL (Pfad enthält die Set-ID, kein `emoteSetId`-Query; Matcher wie oben exakt, nicht Präfix).
      Helfer `liveListRequests`/`EMOTE_SET_PATH` zentral umstellen, damit nicht sechs Stellen einzeln
      driften. Neuer Vitest-Fall in `vote-session-detail-page.spec.ts`: 404 von der neuen Route →
      Sperrgrund `massDelete.memberRead.lock.unavailable` (Set nachträglich aus der Kanalliste gefallen).
- [ ] E2E: neuer Helfer `mockTrackedEmoteSetPreview(page, channelName, response)` in `mocks.ts` neben
      `mockForeignEmoteSetPreview` (Route-Glob `**/api/channels/{c}/emote-sets/*/emotes*`, gleiche
      Body-Form; Doc erklärt, welche Seiten ihn nutzen). `usage-atlas.e2e.spec.ts` (3 Stellen + der
      Kommentar :882) und `vote-ballot.e2e.spec.ts` (4 Stellen) wechseln auf den neuen Helfer;
      `emote-import.e2e.spec.ts`: der Block ab `:3890` (`mockNonActiveSetView` `:3893-3916`, Deep-Link
      `usage-stats?emoteSetId=set-halloween` `:3919`) registriert **zusätzlich** den Tracked-Mock; der
      Foreign-Mock bleibt dort für den Import-Ziel-Loader. Prüfen, ob ein Tracked-Test daneben noch
      einen Foreign-Mock braucht (z. B. Import-Dialog auf der Usage-Seite) — dann beide registrieren.
      **Suchregel:** jeder E2E, der `usage-stats?emoteSetId=` ansteuert oder im Dropdown das Set wechselt,
      braucht den Tracked-Mock — dateiunabhängig (`grep -rn "emoteSetId=" web/e2e`, dazu die
      Dropdown-Wechsel).
- [ ] Doc-Kommentare (1.6) nachziehen.
- [ ] Gates: `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`,
      `npm --prefix web run format:check` (bzw. `format` vorher), `npm --prefix web run e2e` — nur
      wenn auf `:5151` und `:4200` nichts lauscht (CLAUDE.md „Tests"); rote E2E zuerst auf
      Speicherdruck prüfen (Memory), Suite allein wiederholen.
- [ ] `docs/DECISIONS.md`: `**Betrifft:**`-Zeile des Eintrags um die Frontend-Dateien erweitern
      (im selben Commit).
- [ ] Commit: `feat(web): read a tracked channel's set preview from the tracked route`.

**Abnahme:** die Suche nach Aufrufstellen (`http.get`/Request-Aufbau mit `seventv/channels`, **nicht**
Doc-Kommentare wie `core/seven-tv/foreign-emote-set.model.ts:27`) — z. B.
`grep -rnE "(http|httpClient)\.get.*seventv/channels" web/src/app --include=*.ts` — zeigt außerhalb der
Specs nur noch `listForeignChannelEmoteSets` und `loadEmoteSetPreview`; Vitest und E2E grün.

### Task 4 — Doppel-Request beim Deep-Link: nachweisen, dann beheben

**Kontext für den Subagent:** Abschnitt 0 „Doppel-Request" (Herleitung); `usage-stats-page.ts:455-500`
(`setStatus`, `setStatusChannel`, `activeEmoteSetId`), `:540-600` (`selectedEmoteSetId`,
`awaitingEmoteSetId`), `:676-700` (`liveMembersResource`), `:2967-2980` (`reloadLiveMembers`);
`usage-stats-page.spec.ts:2465-2480` (Deep-Link-Simulation: Query vor `createComponent` seeden),
`:3251` (`liveListRequests`) und der `openView`-Helfer (:3255 ff., Reihenfolge der Flushes);
DECISIONS 2026-09-22 (#227) Absatz (d) als Präzedenz derselben Bug-Klasse; CLAUDE.md Regel 12, 14.

- [ ] **Nachweis zuerst (rot):** Vitest-Fälle in `usage-stats-page.spec.ts`, alle über den zentralen
      Request-Matcher der Tracked-Route (Task 3) und **alle** Requests zählend, auch abgebrochene
      (`TestRequest.cancelled`) — nicht nur solche nach Eintreffen des Status. Vor dem Fix müssen (a)
      und (c) scheitern; das Scheitern im Task-Bericht festhalten (Anzahl, welcher abgebrochen).
      (a) Deep-Link `?emoteSetId=set-b` (nicht aktiv), Kanal mit aktivem `set-a`, Flush-Reihenfolge
      **Set-Liste vor Set-Status** → genau **ein** Request, nicht abgebrochen.
      (b) Set-Liste ok, Set-Status **fehlgeschlagen** (`setStatusFailedChannel`) → genau ein Request für
      `set-b`.
      (c) Deep-Link auf das **aktive** Set (`?emoteSetId=set-a`): in **keiner** Flush-Reihenfolge ein
      Request an die Tracked-Route, auch kein abgebrochener. Rot heute, weil `activeEmoteSetId()` bis
      zum Status `null` ist und `params` dann für das später aktive Set einen (abgebrochenen) Request
      auslöst — der Test muss deshalb schon vor Eintreffen des Status mitzählen.
      Gegenprobe (grün vor und nach dem Fix): Status vor Liste → ein Request für `set-b`.
- [ ] **Fix:** `params` von `liveMembersResource` liefert erst dann ein Objekt, wenn der Status-Ausgang
      für den Kanal bekannt ist: `setStatusChannel() === channelName() || setStatusFailedChannel() ===
      channelName()` (Signale `usage-stats-page.ts:1252`/`:1257`; der Fehlerzweig setzt
      `setStatusFailedChannel` bei `:2781`; dieselbe Bedingung nutzt `rangeResolved` `:1306-1312`).
      **Zusätzlich** strukturelle Gleichheit (`equal`, vergleicht `channelName` und `emoteSetId`,
      `undefined` gleich `undefined`) auf den `params`, damit ein späteres Neuberechnen ohne
      inhaltliche Änderung kein neues Objekt in die Ressource gibt (Objekt-Entsprechung des primitiven
      `computed()` aus dem #227-Fix). Solange das Tor geschlossen ist („Status ausstehend"), meldet
      `liveMembersState` (`:726-737`) `'loading'`, **nicht** `'unavailable'` — eigener Test dafür
      (sonst flackert bei einem Deep-Link kurz der Fehlerzustand). Akzeptierte Restlücke, im
      DECISIONS-Absatz zu dokumentieren: `setStatusFailedChannel` wird nie zurückgesetzt (X
      fehlgeschlagen → Y → zurück zu X öffnet das Tor früh).
- [ ] Prüfen, dass kein anderer Ladezustand schlechter wird: `liveMembersState`, `liveMembersSettling`,
      `viewSwitching`, `reloadLiveMembers` (Refresh-Marke wird weiter nur vom Stream gelesen), der
      Wechsel des aktiven Sets auf das gewählte (`params` → `undefined`), der Kanalwechsel (neuer
      `channelName` → neuer Wert). Bestehende Set-View-Fälle in der Spec bleiben grün.
- [ ] Optional, wenn es ohne Realzeitwarten geht: im Playwright-Deep-Link-Fall der Set-View
      (`usage-atlas.e2e.spec.ts`, K4-Block) die Requests an die Tracked-Route zählen (`page.route`
      mit Zähler) und `=== 1` erwarten.
- [ ] Gates wie Task 3.
- [ ] `docs/DECISIONS.md`: Punkt 6 (Deep-Link-Befund, Fix, Restlücke) im Eintrag ergänzen und die
      `**Betrifft:**`-Zeile um `usage-stats-page.ts` (+ spec) erweitern — im selben Commit wie der Fix.
- [ ] Commit: `fix(web): request a deep-linked set's member list once` — oder, falls der Nachweis
      **nicht** gelingt (ein Request in jeder Flush-Reihenfolge): keine Code-Änderung, den Befund mit
      Testprotokoll als Punkt 6 in den DECISIONS-Eintrag aufnehmen (`docs:`-Commit) und den
      Issue-Kommentar damit beantworten.

**Abnahme:** Die Nachweis-Tests (a)–(c) und der `'loading'`-Test existieren und sind grün; der Bericht nennt das Vorher-Ergebnis.

### Task 5 — Gates, Coverage, PR-Vorbereitung

**Kontext für den Subagent:** CLAUDE.md „Arbeitsweise" und „Tests"; Memory „coverage-local.mjs:
Grenzen"; Regel 1 (PR ja, Merge nein), Regel 22 (Codex-Zweitmeinung macht der Orchestrator).

- [ ] Alle Gates am Stück auf dem Branch: `dotnet test EmotePurge.slnx`,
      `dotnet format EmotePurge.slnx --verify-no-changes`, `npm --prefix web test -- --watch=false`,
      `npm --prefix web run lint`, `npm --prefix web run format:check`, `npm --prefix web run e2e`
      (Port-Bedingung), `node scripts/coverage-local.mjs --base origin/feat/emote-sets-200`.
- [ ] Coverage-Ergebnis lesen, nicht nur bestehen: neue Dateien (Regel, Dienst, Endpoint-Tests,
      Frontend-Loader) müssen nahe 100 % liegen; die Näherung ist bei kleinen Änderungen in großen
      Dateien (`usage-stats-page.ts`, `EmoteEndpoints.cs`) unscharf.
- [ ] PR-Text (englisch, Vorlage `.github`): Problem, Lösung 1a, Bucket-Zuordnung, Deep-Link-Befund,
      Live-Verifikation (Task 6, Ergebnis eintragen), Gates. Attribution laut Session-Reminder.
- [ ] **Kein PR vor Task 6** — Regel 16 verlangt die Live-Verifikation vor der Fertigmeldung; die
      Commits auf dem Feature-Branch sind davon nicht betroffen.

### Task 6 — Live-Verifikation (Orchestrator + Betreiber, Regel 16)

Kein Subagent-Code; der Orchestrator bereitet vor, der Betreiber führt aus, was ein Cookie braucht.

- [ ] Worktree-Api starten: `dotnet run --project src/EmotePurge.Api` aus dem Worktree, lokal
      Postgres/Redis wie gewohnt (der laufende Dev-Stack aus `~/projects/EmotePurge` darf weiterlaufen;
      **kein** `docker compose` aus dem Worktree, kein zweiter Worker). HttpClient-Logging:
      `Logging__LogLevel__System.Net.Http.HttpClient=Information` als Umgebungsvariable, damit jede
      7TV-Anfrage im Log steht.
- [ ] `npm --prefix web start` aus dem Worktree; Betreiber loggt sich ein (Twitch-Redirect auf `:5151`).
- [ ] **Telemetrie vorher:** `GET /api/admin/rate-limits` (Admin-Cookie) — Zähler von
      `ForeignEmoteLookup` und `TrackedEmoteSetPreview` notieren.
- [ ] **Set-Wechsel:** Auf der Usage-Seite eines getrackten Kanals mit ≥ 2 NORMAL-Sets in einer Minute
      **mehr als 10** verschiedene nicht aktive Sets bzw. Wechsel (Client-Cache umgehen: entweder
      > 10 verschiedene Sets, oder je Wechsel den Refresh-Knopf, der `refresh=true` sendet) — kein
      429, keine „Rate-Limit erreicht: Policy ForeignEmoteLookup"-Zeile im Api-Log; stattdessen
      Zähler `TrackedEmoteSetPreview.acceptedLastMinute` steigt, `ForeignEmoteLookup` bleibt stehen.
- [ ] **Telemetrie nachher:** `GET /api/admin/rate-limits` erneut; Differenz je Policy im PR-Text.
- [ ] **Nicht-Mitglieds-Set per curl** (Session-Cookie aus dem Browser; `Program.cs:58-63` setzt
      keinen eigenen Namen, also der ASP.NET-Core-Default `.AspNetCore.Cookies`; Platzhalter im Befehl):
      `curl -i -b '.AspNetCore.Cookies=<COOKIE>' 'http://localhost:5151/api/channels/<kanal>/emote-sets/<fremde-set-id>/emotes'`
      → 404 `{"errorCode":"emote_set_not_found"}`; Log zeigt **keinen** 7TV-Vorschau-Aufruf für die Id.
- [ ] **Alte Foreign-Route unverändert:** `curl … 'http://localhost:5151/api/seventv/channels/<kanal>/emotes?emoteSetId=<set>'`
      → 200; elf Aufrufe in einer Minute → der elfte 429 mit `Policy ForeignEmoteLookup` im Log.
- [ ] **Vote-Detail:** eine SET-Session öffnen (Manager, feiner Zeiger) — Network-Tab zeigt die
      Tracked-Route, kein 403.
- [ ] **Deep-Link:** `…/usage-stats?emoteSetId=<nicht-aktiv>` mit leerem Cache laden; Network-Tab
      zeigt **einen** Request an die Tracked-Route, keinen `(canceled)`.
- [ ] Ergebnis in den PR-Text; erst dann PR öffnen (Regel 1), danach Codex-Zweitmeinung durch den
      Orchestrator (`/codex:review --model gpt-6-sol`, einmal für den Branch).

---

## 3. Inhalt des DECISIONS-Eintrags (Task 2, englisch)

Titel-Vorschlag: *„2026-09-29 — A tracked channel's set preview gets its own route and its own
per-user bucket; `ForeignEmoteLookup` keeps guarding only what is foreign (#220)"*. Der Eintrag
soll — knapp, im Stil der Nachbarn — festhalten:

1. **Was `ForeignEmoteLookup` schützt und was nicht.** Es ist die per-Nutzer-Fairness-/Missbrauchs-
   grenze für Lesungen, die 7TV bis zu zehn paginierte Aufrufe kosten können; **7TV selbst** schützen
   Provider-Budget, Coalescer, Breaker und der 60-s-Cache im Hardening-Decorator (spec E5b), nicht
   die Policy. Der Set-Wechsel eines getrackten Kanals hat ein anderes Lastprofil (ein gecachter
   Aufruf je Wechsel, Client-Cache davor) und saß nur historisch im selben Bucket, weil beide
   Aufrufer dieselbe Route teilten.
2. **Warum Option 1a und nicht 2 oder 3.** Option 2 (Cache-Treffer vor dem Limiter) verschöbe das
   Cache-Lesen aus dem Decorator in die Middleware-Reihenfolge und machte den Limiter vom Redis-
   Zustand abhängig — ein Partitioner ist synchron und läuft vor jedem Filter, er kann keinen Service
   befragen; Option 3 (PermitLimit anheben) schwächt die gemessene Grenze des Import-Falls für alle,
   um einen Aufrufer zu entlasten. Der Routen-Split ist der einzige Weg, auf dem „Set eines getrackten
   Kanals, den der Aufrufer sehen darf" **vor** dem Limiter erkennbar ist — an der Route, nicht an
   einer Laufzeit-Abfrage.
3. **Wer im Foreign-Bucket bleibt und warum:** #216-Vorprüfung (`/me/emote-set-targets/{id}`), K2
   (`/me/emote-set-targets`), K3 (`/seventv/channels/{c}/emote-sets` und `…/emotes`), Import-Ziel-
   Loader (liest auch getrackte nicht aktive Sets, `import-target-loader.ts:105`, bleibt aber per
   Umfangsentscheidung auf der Foreign-Route, weil dieser Pfad die Usage-Stats-Rolle nicht voraussetzt)
   und Restore-Slot, und die Vote-Session-Anlage (`POST …/vote-sessions`, K6-Review Fable A) —
   jeder davon kann 7TV eine paginierte Lesung kosten, ohne dass ein Kanal-Filter davor steht.
4. **Der Vertrag der neuen Route** (Abschnitt 1.1/1.2 in Kurzform), die Wahl von `emote_set_not_found`,
   die Mitgliedschaftsregel und ihre Teilung mit der Vote-Anlage (das aktive Set ist ohne Listen-Lesung
   Mitglied; `IsBotActive` und Ausschlussliste werden bewusst nicht geprüft — Geschwister-Semantik
   der Dropdown-Route, der Usage-Stats-Filter und der Vote-Anlage, die ausgeschiedene Kanäle bis zur
   Retention bedienen; die Vote-Anlage bleibt unverändert), die bekannte Grenze „`refresh`
   umgeht die Listen-Frische nicht (60 s)", und dass die Antwortform identisch bleibt, damit das
   Frontend nur die URL wechselt.
5. **Warum Vote-Detail mit umzieht:** `canManage ⊂ canViewUsageStats`, die Lesung ist auf `canManage`
   gegated. Bekannte Grenze: die Mitgliedschaft wurde nur zur Anlagezeit bewiesen; fällt das Set später
   aus der 7TV-Liste und ist nicht aktiv, antwortet die Route 404 und das Massenlösch-Panel ist gesperrt
   (`'unavailable'`, generischer Text) — gewollt, konsistent mit der Anlage-Scoping-Regel.
6. **Deep-Link-Doppel-Request** (Task 4, eigener Commit): Befund und Fix (Tor über
   `setStatusChannel`/`setStatusFailedChannel` plus strukturelle Gleichheit, `'loading'` solange das
   Tor zu ist) oder: nicht reproduzierbar, Protokoll. Akzeptierte Restlücke:
   `setStatusFailedChannel` wird nie zurückgesetzt (X fehlgeschlagen → Y → zurück zu X öffnet das Tor
   früh).
7. **Konfiguration und Telemetrie:** `RateLimiting__TrackedEmoteSetPreview__PermitLimit`, Deskriptor,
   i18n-Label; Default 30 und die Begründung aus 1.4.

`**Betrifft:**`-Zeile, gestaffelt nach Regel 3:

- **Task 2 (Erstanlage, Punkte 1–5 und 7):** nur Dateien, die nach Task 1/2 existieren — Core-Regel,
  Interface, Dienst, DI, `VoteSessionService`, `RateLimitingOptions`, `RateLimitPolicyNames`,
  `Program.cs`, `EmoteEndpoints`, `SevenTvEndpoints` (Mapping-Helfer), `AdminEndpoints`,
  `ApiErrorCodes` (Kommentar), `EmoteSetIdValidationFilter` (Kommentar), die vier Api-Testdateien, die
  zwei Infrastructure-Testdateien, `web/public/i18n/{de,en}.json`, `docs/Architectur.md`.
- **Task 3 (eigener Commit):** ergänzt `seven-tv-emote-set.service.ts` (+ spec),
  `vote-session-detail-page.spec.ts`, `web/e2e/support/mocks.ts`, `usage-atlas.e2e.spec.ts`,
  `vote-ballot.e2e.spec.ts`, `emote-import.e2e.spec.ts`, die weiteren angefassten E2E-Dateien.
- **Task 4 (eigener Commit):** ergänzt Punkt 6 sowie `usage-stats-page.ts` (+ spec).

---

## 4. Offene Punkte für den Betreiber

Nichts davon blockiert Task 1–4; jeder Punkt braucht ein Ja/Nein, bevor er umgesetzt wird.

1. **Admin-Labels fehlen für `PublicLegal` und `Contact`** (`web/public/i18n/{de,en}.json:339-346`):
   die Monitoring-Seite rendert für beide den rohen Transloco-Schlüssel. Nebenbefund außerhalb des
   Umfangs — in Task 2 mit zwei Zeilen je Locale mitnehmen, oder eigenes Issue?
2. **`README.md:147`** behauptet, Rate-Limits seien Konstanten im Code; seit 2026-08-29 sind sie
   konfigurierbar. Eigener `docs:`-Commit oder Issue — nicht Teil dieses Plans.
3. **`docs/Architectur.md:154-155`** nennt für die Emotes- und Usage-Stats-Gruppe noch `ExternalApi`
   (seit 2026-08-29 durch `InteractiveRead` ersetzt). Task 2 fügt nur die neue Zeile hinzu; soll die
   Altlast in derselben Änderung korrigiert werden (zwei Wörter), oder bleibt Bestandsdoku unberührt?
4. **Spec 2026-09-20, Tabelle 6.10 (`docs/superpowers/specs/2026-09-20-emote-sets-200-spec.md:953`)**
   ordnet `…/emotes?emoteSetId=` dem `ForeignEmoteLookup` zu. Specs sind Denkwerkzeug; ein
   einzeiliger Nachtrag („seit #220: Tracked-Aufrufer auf `TrackedEmoteSetPreview`, s. DECISIONS")
   wäre billig. Ja/Nein?
5. **Default 30/min** ist der Vorschlag aus dem Brief; die Live-Verifikation (Task 6) misst, wie viele
   Permits eine Minute intensiven Wechselns tatsächlich kostet. Falls die Messung deutlich darunter
   liegt, ist 30 trotzdem richtig (Fenster ist fix 60 s, und `refresh=true`-Nachladungen durch
   `channel.synced` kommen ohne Nutzeraktion) — nur falls sie **darüber** liegt, ist der Default neu zu
   entscheiden, nicht still zu erhöhen.
6. **Regel-Extraktion aus `VoteSessionService`** (Task 1) ist eine verhaltensneutrale Änderung an
   einem Bestandsdienst, die der Brief als „prüfen, ob teilbar" formuliert. Der Plan sagt ja
   (nur der `Ok`-Zweig, Tests unverändert). Veto möglich — dann bekommt der neue Dienst die Regel
   als eigene private Funktion und der DECISIONS-Eintrag nennt die bewusste Dopplung.
7. **Bekannte Grenze:** Ein Set, das binnen 60 s nach seiner Anlage auf 7TV gewählt wird, antwortet
   404, bis der Listen-Cache abläuft — dasselbe Fenster, in dem es auch im Dropdown noch fehlt. Der
   Plan akzeptiert das (kein `refresh` auf die Liste, damit `refresh=true` keinen zweiten 7TV-Aufruf
   je Nachladung kostet). Einspruch?
