# Plan #216 — Besitzer-Hinweis: der set-zentrierte Bericht und die Vorprüfung lesen die Liste des wahrscheinlichen Besitzers zuerst

Erstellt am 2026-09-28 gegen `feat/216-owner-hint` = `2d979e89` (= `origin/feat/emote-sets-200`, #280
gemergt). Quellen: Issue #216, Epic #200 (Tabelle „Open"), `docs/DECISIONS.md` (2026-09-28 #280,
Absatz „Known limit"; 2026-09-25 „Who may report …" mit F16; 2026-09-25 „The replace lock … falls"),
die Spec `docs/superpowers/specs/2026-09-20-emote-sets-200-spec.md` (§32 P1 und zweite Runde, §6.2,
§6.10), die Restore-pro-Set-Spec (`2026-09-24-restore-pro-set-253-design.md`, E19, F16, F17),
`CLAUDE.md` (Regeln 2, 3, 4, 5, 7, 11, 12, 16, 18, 22), `web/.claude/CLAUDE.md` und der Code auf
`2d979e89` — jede in Abschnitt 2 genannte Stelle ist gelesen, nicht vermutet. Die Betreiber-
Entscheidungen aus dem Auftrag (beide Pfade, Hinweis nur als Reihenfolge, geschützter Grant-Weg für
die Vorprüfung, keine Format-Versionssprünge, Wortlaut-Korrektur) sind **verbindlich und hier nicht
neu verhandelt**.

**Kein Code im Plan.** Namen stehen nur, wo sie ein Vertrag zwischen zwei Tasks sind. Weicht der
Code vom Plan ab, gilt der Plan; der Fund kommt in den Task-Bericht, nicht still in den Diff.
**Leitplanke:** Ein Hinweis ist eine *Reihenfolge*, nie eine *Erlaubnis*. Die Entscheidung
„darf dieses Konto in dieses Set schreiben" fällt weiter allein `EmoteSetEditability.IsEditable`
über die Listen **verifizierter** Konten (`{Akteur} ∪ Grants` aus der Session) — ein Hinweis, der
außerhalb dieser Menge liegt, wird verworfen, **bevor** irgendein 7TV-Request entsteht.

**Nicht wieder aufgemacht:** die #280-Invariante (bestätigte Startpunkte lesen `activeRun`/
`activeClaim`, nie `startLocked`), die 20-s-Schranke (`LIVE_READ_TIMEOUT_MS`,
`recovery-file-gate.ts:6`; `LIVE_ALIAS_READ_TIMEOUT_MS`, `mass-delete-panel.ts:121`, derselbe Wert)
und fail-closed bei Timeout.

---

## 1. Ziel und Nicht-Ziele

**Ziel.** (Pfad A, #216) Die drei set-zentrierten Berichte `sync-imported`, `sync-deleted`,
`sync-restored` nehmen im Body einen optionalen Hinweis auf den Besitzer des Sets (Twitch-ID) mit;
die Besitzer-Prüfung liest die Liste dieses Kontos zuerst und kostet mit gültigem Hinweis bei kaltem
Cache **einen** Listen-Request statt `1 + k`. (Pfad B, #280 „Known limit") Die geteilte Vorprüfung
`resolveEditableSet` antwortet aus der 60-s-Client-Kopie der Zielliste, wenn die frisch ist (0
Requests), und fragt sonst eine **neue, set-bezogene Route**, die dieselbe Prüfung mit demselben
Hinweis fährt — statt wie heute die ganze Zielliste zu laden, deren Backend `1 + k` Konten seriell
liest. Beide Pfade laufen durch **einen** gemeinsamen Kern. Die Behauptung „der Normalfall kostet
null Requests" wird in Spec und Code durch die wahre Aussage ersetzt.

**Nicht-Ziele.**
- **Keine Änderung an `EmoteSetEditability`**, an `OwnershipEvidence`s Regel (§32 P1) oder an der
  Sperre „nie lockerer als der Bericht" (F16). Die Vorprüfung bleibt strenger als der Bericht.
- **Kein Bypass der Wächterkette.** Auch der Hinweis-Pfad läuft durch `ListByTwitchIdAsync`
  (Cache, Coalescer, Breaker-Operation `emote-set-list`, Provider-Budget); ein offener Breaker ⇒
  fail-closed `Unavailable`.
- **Keine neue Rate-Limit-Policy, kein neuer Fehlercode** (Regel 7). Die neue Route sitzt in der
  bestehenden `/api/seventv/me`-Gruppe unter `ForeignEmoteLookup`.
- **Kein Versionssprung** bei `PURGE_RUN_FORMAT_VERSION` (3), `TRANSFER_RUN_FORMAT_VERSION` (1),
  `TRANSFER_UNDO_FORMAT_VERSION` (1). Das neue `meta`-Feld ist additiv; alte Dateien lesen sich wie
  heute und laufen ohne Hinweis in den heutigen Vollgang.
- **Keine längere Server-TTL** für die Listen (#216, Option 2 — verworfen: die 60 s sind E12).
- **Picker-Route `GET /api/seventv/me/emote-set-targets` bleibt unverändert**, inklusive ihres
  ungeschützten Grant-Wegs (`ISevenTvEditorService`, §32 zweite Runde „Warum der Autorisierungspfad
  bewusst ungeschützt bleibt").
- **Der kanalgebundene `sync-imported`** (`EmoteEndpoints`, getracktes Ziel) und die Legacy-Guid-Routen
  bleiben unberührt — sie haben keine Besitzer-Prüfung.
- **`IEmoteSetOwnershipService`** (Set-Warnung, `EmoteSetOwnershipService.cs`) ist ein anderer
  Dienst und wird nicht angefasst — nicht verwechseln.
- **Keine Wortlaut-Runde** an Nutzertexten; es kommen keine neuen Locale-Schlüssel hinzu.

---

## 2. Ist-Stand (auf `2d979e89`)

| Stelle | Was dort steht |
|---|---|
| `src/EmotePurge.Infrastructure/Services/ImportTargetOwnershipService.cs:54-104` | `CheckAsync`: eigene Liste (`:59`), bei Fund mit eigenem Besitz sofort zurück (`:60-63`); sonst Grants über `IGuardedSevenTvEditorGrantsService` (`:109`), je Grant eine Liste **seriell**, Abbruch beim ersten Treffer (`:122-134`); Grant mit `TwitchChannelId == Akteur` übersprungen (`:124-127`); dann `ListedOnlyUnderForeignOwners` ⇒ 403/503 (`:76-81`); zuletzt **ein** budgetierter Owner-Lookup (`:83-103`, Breaker-Op `emote-set-owner`). Kein früher Ausstieg über Konten hinweg, keine Reihenfolge nach Wahrscheinlichkeit. Kommentar `:23-27`: „the ordinary report costs no upstream request at all" — **falsch**, sobald mehr als 60 s zwischen Picker und Bericht liegen (#216: 6 Requests gemessen). |
| `ImportTargetOwnershipService.cs:253-315` (`OwnershipEvidence`) | Sammelt je gelesener Liste `SevenTvUserId → (Login, TwitchId)`, die Besitzer-IDs, unter denen das Set gelistet war, und `AnyListUnreadable`; `MatchAgainstAllKnownAccounts` ruft `EmoteSetEditability.IsEditable` (`:300-314`). |
| `src/EmotePurge.Core/Services/IImportTargetOwnershipService.cs` | `CheckAsync(actorTwitchUserId, actorTwitchLogin, emoteSetId, ct)`; Ergebnis `SevenTvEmoteSetOwnershipCheckResult` mit `Status` (Owner/SetNotFound/Forbidden/Unavailable) und den drei Owner-Identitäten (`:23-68`). Kein Set-Summary, kein Hinweis. |
| `src/EmotePurge.Core/Services/IGuardedSevenTvEditorGrantsService.cs:220-257` | „for the set-centric import's owner check, and for nobody else". `GuardedSevenTvEditorGrantsService.cs:14-17`: „the ordinary report never reaches this class's guarded half". Miss ⇒ **zwei** Requests (Identität + `editor_of`), je ein Permit (`SevenTvApiClient.LookUpEditorGrantsAsync`); Fehler gehalten (`7tveditorhold:`). |
| `src/EmotePurge.Infrastructure/Services/SevenTvEmoteSetListService.cs:80-98` | Cache-Hit kurzschließt alles; sonst Coalescer → Breaker `emote-set-list` → Slot (5 s Wartezeit, `:78`) → Client (HTTP-Timeout 10 s, Permit im Client). `AnswerTimeToLive` 60 s (`:53`). |
| `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs:134-136` | `meGroup = /api/seventv/me`, `RequireAuthorization` + `ForeignEmoteLookup` (10/min je Nutzer, `RateLimitingOptions.cs:66`). Einzige Route: `GET /emote-set-targets` (`:138-210`), liest Grants **ungeschützt** (`ISevenTvEditorService`, `:162`), `1 + k` Listen seriell, `Editable` im zweiten Durchgang (`:187-207`); je Konto `GetActiveByTwitchChannelIdAsync` (`:411`) und `activeEmoteSetId` = Kanal-Zeile vor 7TV (`:417-418`). |
| `SevenTvEndpoints.cs:222-276` | `emoteSetGroup = /api/seventv/emote-sets/{emoteSetId}`, `EmoteSetIdValidationFilter` (Routenwert), `Bookkeeping`. `sync-imported`: Vokabular (`:237`), `CheckAsync` (`:251`), Mapping 404 `emote_set_not_found` / 403 bare / 503 `foreign_channel_seventv_unavailable` (`:253-267`). Kommentar `:215-219`: „the owner check below costs no unguarded 7TV request … answers from the cached … set lists" — „unguarded" stimmt, „cached" ist die falsche Nebenbehauptung. |
| `SevenTvEndpoints.cs:502-543` (`PassSyncInSetLadderAsync`) | Body-Prüfung, Akteur, `CheckAsync` (`:527`), dieselben drei Ausgänge; Ergebnis trägt `OwnerTwitchUserId` weiter (`:541-542`), das `EmoteService.MarkDeletedInSetAsync`/`MarkRestoredInSetAsync` (`EmoteService.cs:183-200`) als `InSetOwner` verbraucht. |
| `SevenTvEndpoints.cs:742-756` | `SyncImportedToSetRequest(SevenTvEmoteIds, SourceChannelName, SourceKind, LeaderboardSort = null)`; `SyncInSetRequest(SevenTvEmoteIds?, ExpectedChannelName?)`. Beides `internal sealed record`, additiv erweiterbar (System.Text.Json ignoriert fehlende Felder). |
| `src/EmotePurge.Api/Validation/EmoteSetIdValidationFilter.cs:27-35` | Prüft den Routenwert `emoteSetId` zuerst; registrierbar je Route. Fehlercode `invalid_emote_set_id` existiert. |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs:57,106` und `web/src/app/core/i18n/api-error.ts:36,53` | `foreign_channel_seventv_unavailable`, `emote_set_not_found` in Backend und Frontend vorhanden. |
| `web/src/app/core/seven-tv/seven-tv-emote-set.service.ts:192-225` | `loadCachedEmoteSetTargets` (60 s, nur Erfolg gecacht, `refresh` bypasst); `resolveEditableSet(emoteSetId)` = `loadCachedEmoteSetTargets().pipe(map(classifyEditableSet))` — bei kalter Kopie **immer** die volle Liste. `classifyEditableSet` (`:106-122`): kind≠NORMAL ⇒ `notSelectable`; editable ⇒ `editable`; sonst `unavailable`, wenn `sevenTvUnavailable` oder ein `setsUnavailable`, sonst `notEditable`. `toEditableSetTarget` (`:74-87`) hat `account.twitchChannelId` in der Hand, gibt es aber nicht weiter. |
| `web/src/app/core/seven-tv/seven-tv-emote-set.model.ts:171-191` | `EditableSetTarget {emoteSetId, setName, ownerDisplayName, twitchLogin, trackedChannelName, isActiveSet}` — **kein** `twitchChannelId`; `EditableSetResolution` = vier Ausgänge. `SyncInSetBody {sevenTvEmoteIds, expectedChannelName}` (`:124-127`); `SyncImportedToSetBody` in der Service-Datei (`:128-133`). |
| Aufrufer der Vorprüfung | `import-flow.ts:411-435` (Replace-Start, `timeout(LIVE_READ_TIMEOUT_MS)`, `startCheckPending`); `file-import-step.ts:392-427` (Datei-Tür, `takeUntilDestroyed`, kein Timeout — Bestand); `mass-delete-panel.ts:794-830` (Restore aus dem Dock) und `:1080-1119` (vor der Lösch-Bestätigung, `openConfirmDialogAfterCheck(checkedSetId)` `:1107`). |
| Twitch-ID im Client | Nur `auth.model.ts:2` (`twitchUserId` des Akteurs) und `EmoteSetTargetAccount.twitchChannelId` (Zielliste). Eine `ChannelSummary` trägt **keine** Twitch-ID — der Lösch-Pfad kennt die ID seines Kontos nur über die Antwort der Vorprüfung. |
| `import-target-dialog.ts:69-78, 520-566` | `ImportTargetChoice` ohne `twitchChannelId`; `selectSet` baut die Auswahl aus `group` (`ImportTargetAccountGroup.twitchChannelId`, `import-target-choices.ts:40, 172`). |
| `import-flow.ts:77-79, 143-156, 440-500` | `ImportFlowTarget` = `activeSet {channelName}` \| `chosen {choice}`; `start` (Vorprüfung nur bei Replace-Zeile) → `startAfterCheck` → `startImport({setId, channelName, ownerDisplayName, setName, isActiveSet}, …)`. |
| `seven-tv-import.service.ts:169-215, 430-520, 837-870, 892` | `ImportRunInfo` (`targetChannelName`, `targetOwnerDisplayName`, `targetSetId`, …); `startImport` mit Ziel-Parameterobjekt; `reportImported`: getrackt ⇒ kanalgebunden, sonst `reportImportedToSet` (`:863-867`); Removal-Report `reportDeletedInSet` (`:892`). |
| `seven-tv-delete.service.ts:157-177, 360-372, 563-575` | `DeleteRunInfo {channelName, expectedChannelName, setId, …}`; `startDelete(setId, channelName, emotes, expectedChannelName)`; Report-Body `{sevenTvEmoteIds, expectedChannelName}`. |
| `seven-tv-restore.service.ts:131-138, 153-172, 368-374, 620-624` | `RestoreStartTarget {setId, expectedChannelName, resyncChannelName, hostChannelName, setName, ownerOrChannelLabel}`; `RestoreRunInfo`; Report-Body wie oben. `restore-flow.ts:27-38` `ResolvedRestoreTarget` (Kopie von `EditableSetTarget` + `host*`), `:386` baut `expectedChannelName`. |
| `seven-tv-undo.service.ts:125-134, 202-227, 469-474, 865-882` | `UndoRunTarget`, `UndoRunInfo`, `startUndo`, `sendReport` mit `{sevenTvEmoteIds, expectedChannelName}`. `undo-flow.ts:67`. |
| Dateiformate | `purge-run-export.ts:39` v3, `PurgeRunMeta {emoteSetId, startedAt, finishedAt, counts}` (`:104-116`), `RestoreFileTarget {emoteSetId}` (`:180-185`), `parsePurgeRunProtocol` liest `meta` „unvalidated beyond `emoteSetId`" (`:227-260`). `transfer-run-export.ts:41` v1, `TransferRunMetaBase {targetEmoteSetId, targetChannelName, targetOwnerDisplayName, origin}` (`:86-93`), Parser `:454-504`, `:597-631` lesen nur `targetEmoteSetId`/`stage`. `transfer-undo-export.ts:42` v1, Parser `:620-667`. `read-envelope.ts` prüft nur `source`/`kind`. Alle drei Parser tolerieren unbekannte `meta`-Felder (kein Whitelisting) — **verifiziert**. Builder-Aufrufer: `mass-delete-panel.ts`, `import-confirm-dialog.ts` (planned), `import-progress-section.ts` (finished), `transfer-undo-export.ts:484` (aus `UndoRunInfo`). |
| Tests | `tests/EmotePurge.Infrastructure.Tests/Unit/ImportTargetOwnershipServiceTests.cs`: 17 Fälle; echte Kette `CreateRealChainAsync` (`:366-390`) mit `InMemoryListCache` (`:449`), `CountingOwnerHandler` (`:465-481`, antwortet **nur** die v3-Owner-Abfrage); `Fakes/SevenTvGqlRouteHandler.cs` kennt `Identity`/`EditorOf`/`Owner`, **keine v4-Liste**. `SevenTvEmoteSetListServiceTests.cs:536-547` `StubHandler(Func<body,string>)`; v4-Antwortform `:426`. `Api.Tests/EmoteRoutePolicyTests.cs:36-56` (Policy je Route), `AuthFilterMatrixTests.cs:56-97` (401-Matrix), `SevenTvEmoteSetSyncImportedEndpointTests.cs`, `SevenTvEmoteSetSyncBookkeepingEndpointTests.cs` (echte Prüfung über substituierte Listen/Grants, `ApiFactory.cs:107,139,146`). Frontend: `seven-tv-emote-set.service.spec.ts:339-496` (alle `resolveEditableSet`-Fälle flushen die **Listen**-Route); `mass-delete-panel.spec.ts:3155-3300` fährt `resolveEditableSet` durch den echten Service gegen die Listen-Route; `file-import-step.spec.ts:388-420` mockt die Funktion. E2E: `web/e2e/support/mocks.ts:943-973` `mockEmoteSetTargets`, genutzt in `emote-import` (13×), `vote-ballot` (3×), `dialog-action-row`, `footer-placement`. |
| Messmittel | `GET /api/admin/rate-limits` liefert `providers` je `CallSource` (`IRateLimitTelemetryReader.cs:33, 86`), u. a. `seventv-emote-set-list` (`IRateLimitTelemetry.cs:152`) und `seventv-rest` (v3-Owner/Grants). `appsettings.json:6` stellt `System.Net.Http.HttpClient` auf `Warning` — auf `Information` gestellt, loggt jeder ausgehende Request eine Zeile. |
| Spec-Stellen mit der Null-Behauptung | `2026-09-20-emote-sets-200-spec.md:2894-2897` („erzeugt der Normalfall **null** HTTP-Requests"), `:2991-2992` („Cache-Treffer … Das ist der Normalfall, weil der Picker den Grant-Cache Minuten vor dem Bericht füllt" — für die 10-min-Grants richtig, als Verallgemeinerung irreführend), `:3013-3014` („Der geschützte Weg kostet im Normalfall nichts"), `:3018-3019` („`IGuardedSevenTvEditorGrantsService`, nur von `ImportTargetOwnershipService` aufgelöst"). Letzter Nachtrag ist §40. |
| DECISIONS #280 (`docs/DECISIONS.md:63-70`) | „Known limit: a cold `resolveEditableSet` walk over several editor grants (the backend walks 1+k accounts serially … #216) can exceed the 20 s pre-check bound". |

---

## 3. Vertrag

### 3.1 Der Hinweis (Server)

| # | Festlegung | Grund |
|---|---|---|
| 1 | **Neuer Core-Typ `EmoteSetOwnerHint`** (`Core/Services/`, neben `IImportTargetOwnershipService`): zwei optionale Felder, `TwitchUserId` und `TwitchLogin`. Beide dürfen fehlen; ein Hinweis ohne beides ist kein Hinweis. | Ein Typ für Berichte und Route; kein Tupel, kein `string?`-Paar in jeder Signatur. |
| 2 | **Auflösung des Hinweises, vor jedem 7TV-Request:** Die Menge der zulässigen Konten ist `{Akteur (Session)} ∪ Grants.Entries` (geschützter Weg, Festlegung 9). `TwitchUserId` gewinnt: gleich dem Akteur ⇒ „Akteur"; gleich `entry.TwitchChannelId` eines Grants (ordinal) ⇒ dieser Grant; sonst verworfen. Nur ohne `TwitchUserId` wird `TwitchLogin` geprüft: über `ChannelName.Normalize` (Regel 9) gegen den normalisierten Akteur-Login und die normalisierten `entry.ChannelLogin` — ein Treffer liefert die Twitch-ID **des Grants**, nie den Login selbst als Schlüssel. Ein verworfener Hinweis wird bei `Debug` geloggt, nie mit 400 beantwortet, nie in die Antwort gespiegelt. | Auftrag: „ein Hinweis außerhalb der Menge wird verworfen, bevor irgendein Request entsteht". Logins gehören nicht in die Listenabfrage (`ISevenTvEmoteSetListService.cs:155-158`). |
| 3 | **Reihenfolge.** Hinweis ⇒ Akteur, oder kein gültiger Hinweis: **wie heute** — eigene Liste zuerst, bei Fund mit eigenem Besitz sofort fertig, Grants gar nicht erst gelesen (`:59-63` ist bereits genau das). Hinweis ⇒ Grant G: Grants (Cache, meist warm) → **Liste von G** → eigene Liste → übrige Grants in Reihenfolge der `Entries`. Ein Treffer (`Inspect` liefert einen Match) beendet den Gang sofort; das ist heute schon so und bleibt. | Auftrag. Die eigene Liste bleibt beim Akteur-Hinweis vorn, weil sie dort ohnehin die wahrscheinlichste ist. |
| 4 | **Die Grants-Sonderregel bleibt:** liefert die eigene Liste `NoSevenTvAccount`, werden die übrigen Grants nicht gelesen (`editor_of` hängt am selben Konto, `:65-72`). Beim Grant-Hinweis sind Grants und Hinweis-Liste zu diesem Zeitpunkt schon gelesen — höchstens ein Listen-Request umsonst, nur in einem manipulierten oder verirrten Aufruf. | Bestand; der Sonderfall ist kein Normalfall. |
| 5 | **Fehlende Hinweis-Liste ist kein Ende.** Ist die Liste von G nicht lesbar (`Unavailable`, `RateLimited`, `BudgetExhausted`, offener Breaker), wird sie wie jede andere als unlesbar vermerkt (`AnyListUnreadable`) und der Gang läuft weiter. Enthält sie das Set nicht, oder unter fremdem Besitz: Gang läuft weiter (die Liste ist danach 60 s im Cache, ein zweiter Blick kostet nichts). | Auftrag; Teilausfall-Regel §32 unverändert. |
| 6 | **Das frühe 403 („nur unter fremdem Besitzer gelistet") fällt erst nach dem vollständigen Gang**, wie heute (`:74-81` steht nach `InspectEditorAccountsAsync`). Ein Set, das in der Hinweis-Liste unter Besitzer B steht, kann von B selbst — einem später gelesenen Konto — zulässig werden (`MatchAgainstAllKnownAccounts`, `:136-138`). | Auftrag; §32 „X steht in einer Liste, aber unter fremdem Besitzer". |
| 7 | **Kein Bypass.** Die Hinweis-Liste wird ausschließlich über `ISevenTvEmoteSetListService.ListByTwitchIdAsync` gelesen — Cache, Coalescer, Breaker-Operation `emote-set-list` (je Operation gezählt), Provider-Budget. Offener Breaker ⇒ die Liste ist unlesbar ⇒ ohne anderweitigen Fund `Unavailable` (fail-closed). | Auftrag; F14 („ein Request, der das Budget nicht belastet, ist einer zu viel"). |
| 8 | **Der Hinweis erzeugt nie eine Schreiberlaubnis.** Owner-Ausgang nur über `EmoteSetEditability.IsEditable(ownerId, readableAccountIds)` wie heute. Der Test „fremder Hinweis wird verworfen" prüft: null zusätzliche Requests und ein Ergebnis, das byte-gleich dem ohne Hinweis ist. | Leitplanke. |

### 3.2 Der gemeinsame Kern

| # | Festlegung | Grund |
|---|---|---|
| 9 | **Ein Gang, zwei Betriebsarten.** `ImportTargetOwnershipService` behält seine Klasse und seine DI-Abhängigkeiten; der Listen-Gang (Hinweis-Auflösung, Reihenfolge, `OwnershipEvidence`) wird in **einen** privaten Kern gezogen, den zwei öffentliche Methoden aufrufen: (a) `CheckAsync(actorTwitchUserId, actorTwitchLogin, emoteSetId, EmoteSetOwnerHint? hint, ct)` — die Berichte, **mit** dem budgetierten Owner-Lookup als Fallback wie heute; (b) eine zweite Methode für die Vorprüfung (Name des Implementers, z. B. `ResolveEditableAsync`), **ohne** Owner-Lookup: nach dem Gang gilt „in keiner lesbaren Liste, alle lesbar" ⇒ `SetNotFound`; „nur unter fremdem Besitz oder ohne Besitzer-ID gelistet" ⇒ `Forbidden`; „eine Quelle unlesbar, kein zulässiger Fund" ⇒ `Unavailable`. Beide lesen Grants über `IGuardedSevenTvEditorGrantsService`. | Auftrag „ein Helfer für beide"; F16 („nie lockerer als der Bericht", ein Set ohne Besitzer-ID ⇒ nicht bearbeitbar). Bestehende Signatur bleibt aufrufbar: `hint` ist optional. |
| 10 | **Das Ergebnis trägt additiv, was die Route braucht:** `SevenTvEmoteSetOwnershipCheckResult` bekommt — non-null genau bei `Owner`, sofern der Fund aus einer **Liste** stammt (nie aus dem Owner-Lookup-Fallback) — das `EmoteSetSummary` des Sets (Name, `Kind`, `OwnerDisplayName`, `OwnerSevenTvUserId`) und `SevenTvActiveEmoteSetId` der Liste des **Besitzer-Kontos** (null, wenn das Set nur unter einem anderen Konto gelistet war). Das Besitzer-Konto ist das, dessen `SevenTvUserId` die Besitzer-ID ist (`CheckedAccount`: Login + Twitch-ID) — dieselbe Identität, die der Bericht in die Audit-Zeile schreibt. | Die Route muss `setName`, `ownerDisplayName`, `twitchLogin`, `twitchChannelId`, `isActiveSet` liefern (3.4). Die Zielliste nennt für ein „unter A gelistet, von B besessen"-Set das **listende** Konto A; die Route nennt B. Beide sind `editable`; die Papierspur nutzt B. Unterschied dokumentieren, nicht angleichen. |
| 11 | **Kein Ausbau von `IGuardedSevenTvEditorGrantsService`.** Die Vorprüfung liest die Grants durch dieselbe Instanz und dieselbe Methode; die Doku beider Typen (Interface `:220-257`, Klasse `:14-17`) nennt danach beide Aufrufer und die wahre Kostenformel (3.8). Die Picker-Route bleibt ungeschützt (Nicht-Ziel). | Auftrag; §32 zweite Runde. Folge (DECISIONS): eine gehaltene Grant-Störung (`7tveditorhold:`, 30–60 s) macht die Vorprüfung `unavailable`, wo die Zielliste `sevenTvUnavailable: true` **und** trotzdem Konten geliefert hätte — strenger, nicht lockerer. |

### 3.3 Die Berichte (Pfad A)

| # | Festlegung | Grund |
|---|---|---|
| 12 | **Bodies:** `SyncImportedToSetRequest` und `SyncInSetRequest` bekommen je ein optionales `TargetOwnerTwitchId` (`string?`, Default null). Nur die ID — kein Login-Feld auf den Berichten: jeder set-zentrierte Bericht folgt einer Vorprüfung, deren Antwort die Twitch-ID des Besitzers liefert (3.5/3.6), und der ungetrackte `sync-imported` kommt nur aus dem Picker (ID am Konto). Ein fehlendes Feld ⇒ heutiger Gang. Leerer/weißer String ⇒ wie fehlend. | Auftrag; Regel 7 (kein Fehlercode für einen Hinweis). Alte Clients/Tabs bleiben gültig. |
| 13 | Beide Aufrufstellen (`:251`, `:527`) reichen den Hinweis in `CheckAsync`. Vokabular- und Body-Prüfungen bleiben davor; das Mapping der drei Ausgänge bleibt. `Bookkeeping` bleibt (Regel §32: Policy ist wieder zutreffend, weil kein **ungeschützter** Request entsteht). | Nichts am Leiter-Vertrag (AK 30) ändert sich. |

### 3.4 Die neue Route (Pfad B, Server)

| # | Festlegung | Grund |
|---|---|---|
| 14 | **`GET /api/seventv/me/emote-set-targets/{emoteSetId}`** in `meGroup` (`RequireAuthorization`, `ForeignEmoteLookup`), zusätzlich `EmoteSetIdValidationFilter` **an dieser Route** (Routenwert; der Filter ist je Route zu registrieren, `:8-10`). Query: `ownerTwitchId` und `ownerLogin`, beide optional (C#-Defaults, wie `refresh`/`emoteSetId` an `:39-41`). Werte länger als 64 Zeichen gelten als kein Hinweis (kein 400). | Auftrag (Route unter `/me`, `ForeignEmoteLookup`, nicht `Bookkeeping`); die Vorprüfung ist die „ein Nutzer zieht 7TV-Requests"-Gestalt. |
| 15 | **Antwort immer 200** mit `{ status, target }`: `status` ∈ `editable` \| `notSelectable` \| `notEditable` \| `unavailable`; `target` non-null genau bei `editable`: `{ emoteSetId, setName, ownerDisplayName (string \| null, roh), twitchLogin, twitchChannelId, trackedChannelName (string \| null), isActiveSet }`. Mapping: `Owner` mit `Kind ≠ NORMAL` ⇒ `notSelectable`; `Owner` ⇒ `editable`; `Forbidden`/`SetNotFound` ⇒ `notEditable`; `Unavailable` ⇒ `unavailable`. `trackedChannelName` und `isActiveSet` nach derselben Regel wie `ResolveEmoteSetTargetAccountAsync` (`:411-418`): Kanal-Zeile über `GetActiveByTwitchChannelIdAsync(ownerTwitchId)`; `isActiveSet` = (`Channel.ActiveEmoteSetId` ?? `SevenTvActiveEmoteSetId` der Besitzer-Liste) == Set-ID. Middleware liefert 401/429, der Filter 400 `invalid_emote_set_id`. **Kein 503 und kein 404** aus dem Handler. | Entscheidung „200 mit Status" statt 503: (1) die vier Zustände **sind** die `EditableSetResolution`, die der Client ohnehin verbraucht — `unavailable` ist eine Antwort auf die Frage „darf ich?", kein Transportfehler; (2) die Listen-Route degradiert selbst mit 200 + Flags (`sevenTvUnavailable`, `setsUnavailable`), nie mit 503 — Parität; (3) ein 503 käme im Client ohnehin im `error:`-Zweig als `unavailable` an, brächte also nichts, kostete aber einen Statuscode-Vertrag mehr und (bei 404) eine falsche Auskunft, denn der Client unterscheidet „unbekannt" nicht von „nicht bearbeitbar". Vorhandene Codes reichen; **kein** neuer Code (Regel 7). |
| 16 | **Kosten-Permit:** ein Aufruf = ein `ForeignEmoteLookup`-Permit (10/min je Nutzer), wie ein Picker-Öffnen. Rechnung kalter Worst-Case in einer Minute: Set-Vorschau (1) + Lösch-Vorprüfung (1) + Restore-aus-Dock-Vorprüfung (1, oder 0 mit Festlegung 20) + Picker (1) + Replace-Vorprüfung nach langer Bestätigung (1) + Vote-Session (1) ≈ 5–6 < 10. Reicht; die geteilte Eimer-Frage der Vorschau ist #220 und bleibt dort. | Auftrag „prüfen, ob 10/min reicht". |
| 17 | **Wire-DTOs** als `internal sealed record` in `SevenTvEndpoints.cs` neben `EmoteSetTargetsResponse`; `status` als String-Konstanten, nicht als Enum-Ordinal. | Muster der Datei. |

### 3.5 `resolveEditableSet` (Pfad B, Client)

| # | Festlegung | Grund |
|---|---|---|
| 18 | **Signatur:** `resolveEditableSet(emoteSetId, hint?: OwnerHint)` mit `OwnerHint = { twitchChannelId: string \| null; twitchLogin: string \| null }` (Core-Modell in `seven-tv-emote-set.model.ts`). Die vier Ausgänge und `EditableSetResolution` bleiben. | Alle vier Aufrufer, ein Vertrag. |
| 19 | **Cache-first:** Ist `cachedTargets` frisch (dieselbe Prüfung wie `loadCachedEmoteSetTargets` ohne `refresh`), wird lokal mit `classifyEditableSet` klassifiziert — **0 Requests**, byte-gleiche Antwort wie heute. Sonst **die neue Route** mit `ownerTwitchId`/`ownerLogin` aus dem Hinweis; die Antwort wird 1:1 auf `EditableSetResolution` abgebildet, `setName`/`ownerDisplayName` mit **denselben** Fallbacks wie `toEditableSetTarget` (`nonBlank(name) ?? id`, `ownerDisplayName ?? twitchLogin`). Die volle Liste wird von der Vorprüfung **nie mehr** geladen; `loadCachedEmoteSetTargets` bleibt für den Picker. Ein Fehler der Route (429, 503, Netz, Timeout des Aufrufers) landet wie heute im `error:`-Zweig der Aufrufer ⇒ `unavailable`, fail-closed. | Auftrag. Die 20-s-Schranken bleiben bei den Aufrufern (`timeout(...)` an `import-flow.ts:413`, `mass-delete-panel.ts:798, 1085`) und greifen unverändert. |
| 20 | **Antwort-Cache je Set, 60 s** (Erweiterung gegenüber dem Auftrag — Offener Punkt 2): Eine Route-Antwort mit `status ≠ unavailable` wird unter der Set-ID für `EMOTE_SET_TARGETS_CACHE_TTL_MS` gehalten (Muster `cachedPreviews`, nur Erfolg, ein `Map`); die Frische-Prüfung ist: Listen-Kopie frisch → lokal; sonst Set-Eintrag frisch → Eintrag; sonst Route. `unavailable` wird nie gecacht. Ein `refresh` des Pickers ersetzt die Listen-Kopie und **löscht** die Set-Einträge. | Lösch-Bestätigung → Restore aus dem Dock derselben Minute kostet sonst zwei Permits für dieselbe Frage; dieselbe TTL und dieselbe Semantik wie E19/F3. Fällt die Entscheidung dagegen, entfällt nur diese Zeile. |
| 21 | **`EditableSetTarget` bekommt `twitchChannelId: string`** (Pflichtfeld) — aus `account.twitchChannelId` in `toEditableSetTarget`, aus `target.twitchChannelId` der Route. `ResolvedRestoreTarget` (eigene Feldliste, `restore-flow.ts:27-38`) zieht das Feld nach. | Der einzige Weg, auf dem Delete/Restore/Undo an die Twitch-ID des Besitzers kommen (Ist-Stand „Twitch-ID im Client"). |

### 3.6 Client-Plumbing (Hinweise an Vorprüfung und Berichte)

| Aufrufer | Hinweis an die **Vorprüfung** | Hinweis an den **Bericht** (immer ID aus der Vorprüfungs-Antwort bzw. dem Picker) |
|---|---|---|
| Replace-Start `import-flow.ts:411` | `chosen` ⇒ `{ twitchChannelId: choice.twitchChannelId }` (neues Feld an `ImportTargetChoice`, aus `group.twitchChannelId` in `selectSet`); `activeSet` ⇒ `{ twitchLogin: channelName }` | `resolution.target.twitchChannelId` → `startImport(target.ownerTwitchChannelId)` → `ImportRunInfo.targetOwnerTwitchId` → Removal-Report (`:892`) **und** `reportImportedToSet` (`:867`). Ein Add-only-Lauf (keine Vorprüfung): `chosen` ⇒ ID aus der Wahl; `activeSet` ⇒ getrackt ⇒ kanalgebundener Bericht, kein Hinweis nötig. |
| Datei-Tür `file-import-step.ts:397` | `{ twitchChannelId: parsed.target.ownerTwitchId }` (3.7); alte Datei ⇒ kein Hinweis | `ResolvedRestoreTarget.twitchChannelId` → `RestoreStartTarget`/`UndoRunTarget` → `RestoreRunInfo`/`UndoRunInfo` → Body |
| Lösch-Vorprüfung `mass-delete-panel.ts:1083` | `{ twitchLogin: channelName() }` — die Seite kennt nur den Login ihres Kanals; alle Sets der Seite gehören diesem Konto (6.1) | `resolution.target.twitchChannelId` wird mit `checkedSetId` **eingefroren** und durch `openConfirmDialogAfterCheck` → `startDelete(…, ownerTwitchId)` → `DeleteRunInfo.targetOwnerTwitchId` → Body und Purge-Protokoll (3.7) getragen |
| Restore aus dem Dock `mass-delete-panel.ts:795` | `{ twitchChannelId: run.targetOwnerTwitchId }` des Lösch-Laufs (Fallback `{ twitchLogin: run.channelName }` für einen Lauf ohne ID, z. B. aus einem älteren Tab) | `resolution.target.twitchChannelId` → `RestoreStartTarget` |
| Undo (`undo-flow.ts`) | über die Datei-Tür | wie Restore |

Alle Run-Records (`DeleteRunInfo`, `RestoreRunInfo`, `UndoRunInfo`, `ImportRunInfo`) tragen das
Feld `targetOwnerTwitchId: string | null`, gefroren beim Start; Retries eines Berichts senden
dasselbe. `RestoreStartTarget`, `UndoRunTarget` und das Ziel-Objekt von `startImport` bekommen das
Feld als Pflichtfeld (`string | null`), damit kein Aufrufer es vergisst.

### 3.7 Dateiformate (additiv, keine Versionssprünge)

| Datei | Neues `meta`-Feld | Schreiber | Leser |
|---|---|---|---|
| `purge-run` (v3 bleibt) | `targetOwnerTwitchId: string \| null` | `buildPurgeRunProtocol` aus `DeleteRunInfo.targetOwnerTwitchId` | `parsePurgeRunProtocol` → `RestoreFileTarget.ownerTwitchId: string \| null` (nur wenn nicht-leerer String, sonst `null`) |
| `transfer-run` `planned` + `finished` (v1 bleibt) | dito in `TransferRunMetaBase` | `buildTransferPlanRecord` (aus dem Hinweis des Import-Flows, über `ImportConfirmDialogData`), `buildTransferRunProtocol` (aus `ImportRunInfo`) | `parseTransferRunForRestore`, `parseTransferRunForUndo` → `target.ownerTwitchId` |
| `transfer-undo` `planned` + `finished` (v1 bleibt) | dito | aus `UndoRunInfo` | `parseTransferUndoForRestore` |

Dateiinhalt ist untrusted — unproblematisch, weil ein Hinweis nur eine Reihenfolge ist (3.1 Nr. 2,
Nr. 8). Alte Dateien ohne das Feld ⇒ `ownerTwitchId: null` ⇒ Vorprüfung ohne Hinweis ⇒ heutiger
Gang (Auftrag; Offener Punkt 1 zum Login-Fallback aus dem Envelope). Die Parser tolerieren das Feld
schon heute (Ist-Stand); der Test macht es fest.

### 3.8 Wortlaut-Korrektur — die wahre Kostenformel

Überall, wo heute „null Requests im Normalfall" steht, gilt ab diesem Plan:

| Fall | Listen-Requests | dazu |
|---|---|---|
| Warm (Liste ≤ 60 s alt) | 0 | 0 |
| Kalt, gültiger Hinweis | **1** (die Hinweis-Liste) | +2 (Identität, `editor_of`), wenn der Grant-Cache (10 min) kalt ist — **außer** Hinweis ⇒ Akteur |
| Kalt, ohne/ungültiger Hinweis | bis zu **1 + k** (Akteur + k Grants) | + 1 Owner-Lookup, wenn das Set in keiner Liste steht; +2 wie oben |

Alles budgetiert, hinter Breaker und Coalescer. Zu korrigieren (englisch im Code, deutsch in der
Spec): `ImportTargetOwnershipService.cs:23-27`, `IImportTargetOwnershipService.cs:84-89`,
`SevenTvEndpoints.cs:215-219`, `GuardedSevenTvEditorGrantsService.cs:14-17`,
`IGuardedSevenTvEditorGrantsService.cs:220-226` (Aufruferkreis), Spec-Stellen aus Abschnitt 2 als
**Nachtrag §41** (die Spec hebt überholte Stellen per Nachtrag auf, sie schreibt Fließtext nicht um —
§32 macht es genau so). Sätze aus DECISIONS-Bestandseinträgen werden nicht umgeschrieben; der neue
Eintrag nennt sie.

---

## 4. Grenzfälle

| Fall | Verhalten | Wo geprüft |
|---|---|---|
| Besitzer ist der Akteur (Hinweis ⇒ Akteur oder gar keiner) | eigene Liste zuerst, Grants nicht gelesen, wenn das Set dort mit eigenem Besitz steht — heutiges Verhalten, 1 Request kalt | T1 Unit („hint==actor skips grants") |
| Konto ist zugleich eigen und gegrantet | der Grant wird übersprungen (`:124-127`, Bestand); ein Hinweis auf diese ID löst als „Akteur" auf | T1 Unit |
| Hinweis auf einen widerrufenen Grant | nicht in `Grants.Entries` ⇒ verworfen vor jedem Request, heutiger Gang; Ergebnis identisch mit „ohne Hinweis" | T1 Unit („foreign hint dropped, zero extra requests") |
| Besitzer hat gewechselt (Set in Hinweis-Liste, Besitzer-ID ≠ Hinweis-Konto) | kein früher 403; Gang läuft, Besitzer wird ggf. später als zulässiges Konto gefunden | T1 Unit („set in hinted list but owned by another account") |
| Umbenannter Login (Login-Hinweis passt zu keinem Grant-Login) | verworfen ⇒ heutiger Gang; Grants-Cache erneuert Logins alle 10 min, danach passt er wieder | T1 Unit (Login-Fall negativ) |
| Login-Hinweis passt (Groß-/Kleinschreibung, Whitespace) | `ChannelName.Normalize` beidseitig; Auflösung auf die Twitch-ID des Grants | T1 Unit („login hint") |
| Hinweis-Liste unlesbar (Breaker offen, Budget, 429) | als unlesbar vermerkt, Gang läuft; kein anderweitiger Fund ⇒ `Unavailable` (fail-closed); Fund anderswo ⇒ `Owner` | T1 Unit („hinted list failure → walk continues", „open breaker → Unavailable") |
| Set listed unter A, Besitzer B, Hinweis auf A | A gelesen (Set unter B), B später gelesen ⇒ `Owner` B; Route nennt B (3.2 Nr. 10) | T1 Unit, T3 Endpoint |
| Kein 7TV-Konto des Akteurs + Grant-Hinweis | Grants (gehalten 60 s), Hinweis-Liste, dann eigene Liste `NoSevenTvAccount` ⇒ übrige Grants übersprungen | T1 Unit (bestehender Sonderfall erweitert) |
| Set ohne `owner.id` in der Liste | Bericht: Owner-Lookup wie heute; Vorprüfung: `Forbidden` ⇒ `notEditable` (F16, strenger) | T1 Unit (beide Betriebsarten) |
| Set in keiner Liste | Bericht: Owner-Lookup; Vorprüfung: `SetNotFound` ⇒ `notEditable`, **kein** Lookup (`LookUpEmoteSetOwnerAsync` nie aufgerufen) | T1 Unit, T3 Endpoint |
| Gehaltene Grant-Störung (`7tveditorhold:`) | Vorprüfung `unavailable`, wo die Picker-Liste noch Konten zeigt — dokumentierte Verschärfung (3.2 Nr. 11) | T3 Endpoint, DECISIONS |
| Alter Tab / alter Client ohne Hinweis-Feld | Bericht ohne Feld ⇒ heutiger Gang; `resolveEditableSet` ohne zweiten Parameter ⇒ Route ohne Query | T2 Endpoint, T4 Spec |
| Alte Datei ohne `targetOwnerTwitchId` | `ownerTwitchId: null` ⇒ kein Hinweis ⇒ heutiger Gang | T5 Spec (alle drei Formate, v1/v2/v3 bzw. v1) |
| Replace zielt immer auf genau ein Set | ein Hinweis je Lauf; Removal-Report und `sync-imported` tragen denselben | T6a Spec |
| Kalte Client-Kopie, neue Route kostet ein Permit | 1 `ForeignEmoteLookup`-Permit je Vorprüfung; Rechnung in 3.4 Nr. 16 | T3 Route-Policy-Row |
| Budget-Konkurrenz ohne gültigen Hinweis | weiter bis zu `1 + k` Listen × (5 s Slot-Wartezeit + 10 s HTTP) — kann 20 s überschreiten; das ist der **verbleibende** Known-limit-Rest (DECISIONS-Nachtrag) | — (dokumentiert) |
| Route-Antwort für ein `PERSONAL`/`GLOBAL` Set | `notSelectable`, auch wenn der Akteur Besitzer ist (Reihenfolge wie `classifyEditableSet` Nr. 1) | T3 Endpoint, T4 Spec |
| `timeout()` des Aufrufers schlägt auf die Route | `error:` ⇒ `unavailable`, `startCheckPending` freigegeben (`finalize`), #280 unverändert | T4/T6 Spec (bestehende Timeout-Fälle bleiben grün) |
| Picker-`refresh: true` während Set-Einträge (Nr. 20) leben | Listen-Kopie ersetzt, Set-Einträge geleert | T4 Spec |
| E2E: Vorprüfung auf frischer Seite (kalte Kopie) | trifft die neue Route ⇒ `mockEmoteSetTargets` beantwortet sie aus derselben Fixture | T4 (mocks.ts), T7 |

---

## 5. DECISIONS (Regel 3)

**Ein neuer Eintrag, englisch, oben** — im Commit von **T1** (dort ändert sich der Vertrag von
`IImportTargetOwnershipService`); T2, T3, T4, T5, T6a/b ergänzen nur die `**Betrifft:**`-Zeile:

| Titel | Kernaussage |
|---|---|
| *The owner check reads the hinted owner's list first, the pre-check gets a set-scoped route on the guarded grants path, and "zero requests" becomes the true cost* | (1) Ein Hinweis (`targetOwnerTwitchId` im Body; `ownerTwitchId`/`ownerLogin` als Query) ist nur eine Reihenfolge über `{Akteur} ∪ Grants` aus der Session; außerhalb ⇒ verworfen vor jedem Request; nie eine Erlaubnis; `EmoteSetEditability` entscheidet weiter. (2) `GET /api/seventv/me/emote-set-targets/{emoteSetId}`, 200 mit `status`, `ForeignEmoteLookup`; die Vorprüfung ist cache-first und lädt die Zielliste nie mehr. (3) Die Vorprüfung liest Grants geschützt — **Abweichung von Spec §32** („nur von `ImportTargetOwnershipService` aufgelöst" bleibt wörtlich wahr, der Aufruferkreis ist jetzt Bericht **und** Vorprüfung); eine gehaltene Störung macht die Vorprüfung `unavailable`. (4) Die Kostenformel aus 3.8 ersetzt „null Requests im Normalfall" in `ImportTargetOwnershipService.cs`, `SevenTvEndpoints.cs`, `GuardedSevenTvEditorGrantsService.cs` und Spec (§41). (5) F16 bleibt: die Vorprüfung nimmt den Owner-Lookup nicht. Genannt werden die Bestandseinträge 2026-09-25 „Who may report …" (F16), 2026-09-25 „The replace lock … falls" (Vorprüfung), 2026-09-28 #280. |

**Nachtrag an den Bestandseintrag 2026-09-28 #280** (`docs/DECISIONS.md:63-70`), als eigener
Absatz „*Addendum 2026-09-xx (#216)*" am Ende des Eintrags, **nicht** als Umschreibung — im Commit
von **T4** (dort ändert sich das Verhalten von `resolveEditableSet`): Das Known limit verengt sich
auf den Fall **ohne gültigen Hinweis** — alte Dateien ohne `targetOwnerTwitchId`, ein Login-Hinweis
auf einen inzwischen umbenannten Kanal, ein widerrufener Grant — und auf Budget-Konkurrenz, unter
der auch ein Hinweis-Request die 5 s Slot-Wartezeit plus 10 s HTTP-Timeout ausschöpfen kann. Mit
gültigem Hinweis und kaltem Cache kostet die Vorprüfung einen Listen-Request (+2 bei kalten
Grants), warm keinen.

**Entscheidung ein oder zwei Einträge:** ein Eintrag plus Nachtrag. Der Hinweis-Vertrag, die Route
und der Grant-Weg sind eine zusammenhängende Entscheidung (ein Kern, zwei Betriebsarten); das
#280-Limit ist eine Folge davon und gehört an seinen Eintrag, nicht in einen dritten.

---

## 6. Testpflichten

### 6.1 `tests/EmotePurge.Infrastructure.Tests/Unit/ImportTargetOwnershipServiceTests.cs` (T1)

Der Kern des Issues („Done when"): **mit kalter Listen-Kopie und dem Besitzer als letztem von k
Grants — mit Hinweis genau 1 Listen-Request, ohne Hinweis 1 + k.** Dafür muss die echte Kette
(`CreateRealChainAsync`, `:366-390`) die **v4-Listenabfrage** beantworten, was
`CountingOwnerHandler` nicht kann. Vorgabe: `Fakes/SevenTvGqlRouteHandler` um eine Art `List`
erweitern (Erkennung am Abfragetext der v4-Liste, `SevenTvApiClient.cs:82-83` — etwa an
`emoteSets`; die drei bestehenden Arten dürfen sich nicht verschieben) und um eine Antwortform, die
den Request-Body sieht (Muster `StubHandler(Func<string, string>)`,
`SevenTvEmoteSetListServiceTests.cs:536-547`), damit je `platformId` eine andere Liste kommt
(Antwortform `:426`). `CountOf(List)` liefert die Zählung. `CreateRealChainAsync` bekommt k Grants
mit leerem Listen-Cache statt zwei vorbefüllten.

Fälle (jeder ein eigener Test, Namen frei):
1. kalt, Besitzer = letzter von k = 5 Grants, Hinweis = Besitzer-ID ⇒ **1** Listen-Request, 0 Owner-Requests, `Owner` mit dessen Login/ID;
2. dasselbe ohne Hinweis ⇒ **1 + k** Listen-Requests (Bestandsverhalten), 0 Owner-Requests;
3. fremder Hinweis (weder Akteur noch Grant) ⇒ Requests **und** Ergebnis identisch mit Fall 2;
4. Hinweis ⇒ Akteur, Set in eigener Liste ⇒ Grants nie gefragt (`IGuardedSevenTvEditorGrantsService`-Substitute `DidNotReceive`);
5. Hinweis-Liste antwortet 503 ⇒ Gang läuft weiter, Set bei Grant 3 gefunden ⇒ `Owner`; Hinweis-Liste 503 **und** nirgends gefunden ⇒ `Unavailable`;
6. Set in Hinweis-Liste, Besitzer ein anderes geprüftes Konto ⇒ `Owner` des anderen, kein früher 403;
7. Set in Hinweis-Liste unter fremdem Besitzer, sonst nirgends ⇒ `Forbidden` erst nach vollem Gang (Zählung = 1 + k);
8. Breaker `emote-set-list` offen ⇒ `Unavailable` ohne Request (auch mit Hinweis);
9. Login-Hinweis: passend (mit Groß-/Kleinschreibung) ⇒ wie Fall 1; unpassend ⇒ wie Fall 2; ID-Hinweis gewinnt über Login;
10. Vorprüfungs-Betriebsart: Set in keiner Liste ⇒ `SetNotFound`, `LookUpEmoteSetOwnerAsync` nie aufgerufen; Set ohne Besitzer-ID ⇒ `Forbidden`; Ergebnis trägt `EmoteSetSummary` und Aktiv-Flag der Besitzer-Liste;
11. Grants kalt + Hinweis ⇒ Grant: 2 Grant-Requests (`CountOf(Identity)`, `CountOf(EditorOf)`) + 1 Listen-Request, 3 Permits;
12. alle 17 Bestandsfälle byte-gleich grün (kein Hinweis = heutiges Verhalten).

### 6.2 `tests/EmotePurge.Api.Tests` (T2, T3)

- `EmoteRoutePolicyTests.cs:44`: Zeile `GET /api/seventv/me/emote-set-targets/{emoteSetId}` ⇒ `ForeignEmoteLookup`.
- `AuthFilterMatrixTests.cs:68`: 401-Zeile für die neue Route (mit gültiger Set-ID, wie `:71`).
- Neu `SevenTvEmoteSetPreCheckEndpointTests.cs`: 401; 400 `invalid_emote_set_id` (33 Zeichen, Muster `:55-66` des Import-Tests); je ein Fall pro `status` (Listen/Grants über `ApiFactory.EmoteSetList`/`GuardedEditorGrants` arrangiert, wie die Bestandstests); `target`-Felder inkl. `trackedChannelName`/`isActiveSet` über `ApiFactory.Channels.GetActiveByTwitchChannelIdAsync`; `notSelectable` für `Kind = PERSONAL`; **nie** `SevenTvApi.LookUpEmoteSetOwnerAsync`; **nie** `EditorService.GetEditorGrantsAsync` (ungeschützter Weg); Hinweis-Query ⇒ Reihenfolge der `ListByTwitchIdAsync`-Aufrufe (`Received.InOrder`) Hinweis-Konto vor Akteur; fremder `ownerTwitchId` ⇒ Reihenfolge wie ohne Hinweis; `ownerLogin` löst auf die Grant-ID auf.
- `SevenTvEmoteSetSyncImportedEndpointTests`, `SevenTvEmoteSetSyncBookkeepingEndpointTests`: Body **mit** `targetOwnerTwitchId` ⇒ Hinweis erreicht `CheckAsync` (Reihenfolge wie oben); Body **ohne** ⇒ Bestandsverhalten (alle Bestandsfälle unverändert); Body mit leerem String ⇒ wie ohne.

### 6.3 Vitest (T4, T5, T6a, T6b)

- `seven-tv-emote-set.service.spec.ts:339-496`: **umschreiben** — frisch nach `loadCachedEmoteSetTargets` ⇒ 0 Requests und Ergebnis wie heute (die vier Bestandsklassifikationen bleiben als lokale Fälle); kalt ⇒ `expectOne` auf `/api/seventv/me/emote-set-targets/<id>` mit `ownerTwitchId`/`ownerLogin`, Mapping der vier Status, `setName`/`ownerDisplayName`-Fallbacks, `twitchChannelId` im Ziel; Route-Fehler ⇒ Observable-Fehler (kein Verschlucken); Nr. 20: zweiter Aufruf innerhalb 60 s ohne Request, `unavailable` nie gecacht, Picker-`refresh` leert die Einträge.
- `mass-delete-panel.spec.ts:3155-3300, ~3780`: die Blöcke, die die Listen-Route flushen, flushen die neue Route (oder wärmen erst die Liste, wo der Fall „warm" gemeint ist); neue Fälle: Login-Hinweis der Lösch-Vorprüfung; eingefrorene Besitzer-ID landet in `startDelete`; Restore aus dem Dock reicht `run.targetOwnerTwitchId`.
- `file-import-step.spec.ts:388-420, 693, 741`: `toHaveBeenCalledWith(id, { twitchChannelId })` bzw. `null`-Hinweis bei alter Datei.
- `import-flow.spec.ts`, `restore-flow.spec.ts`, `undo-flow.spec.ts`: Hinweis-Weitergabe an Vorprüfung und `start*`; Bestands-Timeout-Fälle grün.
- `seven-tv-import.service.spec.ts`, `seven-tv-delete.service.spec.ts`, `seven-tv-restore.service.spec.ts`, `seven-tv-undo.service.spec.ts`: Report-Bodies tragen `targetOwnerTwitchId` (auch bei Retry), `null` ohne Hinweis.
- `purge-run-export.spec.ts`, `transfer-run-export.spec.ts`, `transfer-undo-export.spec.ts`: Round-trip des Felds; Datei ohne Feld ⇒ `ownerTwitchId: null` (v1/v2/v3 bzw. v1); Nicht-String ⇒ `null`; Versionskonstanten unverändert (Assertion auf den Wert).
- `import-target-dialog.spec.ts`, `import-target-choices.spec.ts`: `twitchChannelId` in der Auswahl.

### 6.4 Playwright (T4 Helfer, T7 Fälle)

- `web/e2e/support/mocks.ts`: `mockEmoteSetTargets` registriert **zusätzlich** eine Route für `**/api/seventv/me/emote-set-targets/*`, die aus derselben `accounts`-Fixture antwortet (Set suchen; `kind ≠ NORMAL` ⇒ `notSelectable`; `editable` ⇒ `editable` mit `target` aus Konto + Set; sonst `unavailable`, wenn `sevenTvUnavailable`/`setsUnavailable`, sonst `notEditable`). Damit bleiben die 20 bestehenden Nutzungen grün, ohne dass ein Spec angefasst wird.
- `emote-import.e2e.spec.ts` (T7): (1) Lösch-Vorprüfung auf frischer Seite ⇒ genau ein Request auf die neue Route, keiner auf die Listen-Route, Query `ownerLogin=<kanal>`; (2) Picker geöffnet, dann Replace-Start binnen 60 s (`page.clock`) ⇒ **kein** Request auf die neue Route; (3) `sync-deleted`-Body eines Lösch-Laufs trägt `targetOwnerTwitchId` des Kontos aus der Fixture. `page.clock.install()` vor `goto`, `runFor`, nicht `fastForward` (CLAUDE.md). Specs importieren `test`/`expect` aus `e2e/support/test.ts`.

---

## 7. Tasks

PR gegen `feat/emote-sets-200`. Jeder Task fährt seine gefilterten Suiten plus Build und
Formatprüfung und hinterlässt Build und Suiten grün. Backend-Tasks: `dotnet build EmotePurge.slnx`,
`dotnet test EmotePurge.slnx` (Docker), `dotnet format EmotePurge.slnx --verify-no-changes`.
Frontend-Tasks: `npm --prefix web run build`, `cd web && npx tsc -p tsconfig.spec.json --noEmit`,
`npm --prefix web test -- --watch=false`, `npm --prefix web run lint`, `npm --prefix web run format`
(Prüfung, dass nichts geändert wird). Englisch in Code, Kommentaren, Log-Zeilen, DECISIONS und
Commits; **keine Ticketnummern in Git-Metadaten**. Member-Reihenfolge nach Regel 19 bzw.
`web/.claude/CLAUDE.md`.

Reihenfolge: **T1 → (T2 ∥ T3) → T4 → T5 → (T6a ∥ T6b) → T7 → T8.** Parallele Paare in eigenen
Worktrees; T2 und T3 berühren beide `SevenTvEndpoints.cs` und `docs/DECISIONS.md` (`Betrifft:`) —
wer zuletzt merged, rebased. Modellvorschlag: T1 `opus` (Reihenfolge-, Breaker- und
Evidence-Semantik), T3 `opus` (neue Route + Mapping), alle übrigen `sonnet`; T8 Orchestrator +
Betreiber.

### T1 — Backend: Hinweis-Vertrag, gemeinsamer Kern, Vorprüfungs-Betriebsart, Unit-Tests, DECISIONS, Spec-Nachtrag
**Art:** Backend (Core + Infrastructure + Tests + Doku).
**Dateien:** `Core/Services/IImportTargetOwnershipService.cs` (Typ `EmoteSetOwnerHint`, erweiterte `CheckAsync`, zweite Methode, additives Ergebnis), `Infrastructure/Services/ImportTargetOwnershipService.cs`, `Core/Services/IGuardedSevenTvEditorGrantsService.cs` + `Infrastructure/Services/GuardedSevenTvEditorGrantsService.cs` (nur Doku), `tests/…/Fakes/SevenTvGqlRouteHandler.cs`, `tests/…/Unit/ImportTargetOwnershipServiceTests.cs`, `docs/DECISIONS.md` (neuer Eintrag), `docs/superpowers/specs/2026-09-20-emote-sets-200-spec.md` (Nachtrag §41, deutsch).
**Vertrag:** 3.1, 3.2, 3.8. Die bestehende Signatur bleibt für Aufrufer gültig (optionaler Parameter), damit Api und Api.Tests ohne Änderung bauen.
**Grenzfälle:** alle Zeilen aus Abschnitt 4 bis „Set in keiner Liste".
**Tests:** 6.1 vollständig.
**Fertig:** Backend-Gates grün; DECISIONS-Eintrag im selben Commit wie die Vertragsänderung; Spec-Nachtrag als eigener `docs:`-Commit.
**Abhängigkeiten:** keine.

### T2 — Backend: Berichte nehmen den Hinweis an
**Art:** Backend (Api + Api.Tests).
**Dateien:** `Api/Endpoints/SevenTvEndpoints.cs` (`SyncImportedToSetRequest`, `SyncInSetRequest`, `:251`, `:527`, Kommentar `:215-219`), `tests/EmotePurge.Api.Tests/SevenTvEmoteSetSyncImportedEndpointTests.cs`, `SevenTvEmoteSetSyncBookkeepingEndpointTests.cs`, `docs/DECISIONS.md` (`Betrifft:`).
**Vertrag:** 3.3.
**Tests:** 6.2, dritter Spiegelstrich.
**Fertig:** Backend-Gates grün; alle Bestandsfälle beider Klassen unverändert grün.
**Abhängigkeiten:** T1.

### T3 — Backend: die set-bezogene Vorprüfungs-Route
**Art:** Backend (Api + Api.Tests).
**Dateien:** `Api/Endpoints/SevenTvEndpoints.cs` (`meGroup`, neue Route, DTOs), `tests/EmotePurge.Api.Tests/SevenTvEmoteSetPreCheckEndpointTests.cs` (neu), `EmoteRoutePolicyTests.cs`, `AuthFilterMatrixTests.cs`, `docs/DECISIONS.md` (`Betrifft:`).
**Vertrag:** 3.4. Handler bleibt dünn (Regel 11: Mapping und Kanal-Auflösung, keine Entscheidungslogik — die liegt in T1).
**Grenzfälle:** „Set in keiner Liste" (kein Lookup), `PERSONAL`, gehaltene Grant-Störung, 400 vom Filter.
**Tests:** 6.2, erster und zweiter Spiegelstrich.
**Fertig:** Backend-Gates grün.
**Abhängigkeiten:** T1 (parallel zu T2 möglich).

### T4 — Frontend: `resolveEditableSet` cache-first + Route, Modelle, E2E-Helfer, #280-Nachtrag
**Art:** Frontend (core + e2e/support).
**Dateien:** `core/seven-tv/seven-tv-emote-set.model.ts` (`OwnerHint`, `EditableSetTarget.twitchChannelId`, `SyncInSetBody.targetOwnerTwitchId`), `core/seven-tv/seven-tv-emote-set.service.ts` (`resolveEditableSet`, Route-Aufruf, Set-Cache Nr. 20, `SyncImportedToSetBody.targetOwnerTwitchId`) + `.spec.ts`, `web/e2e/support/mocks.ts`, `docs/DECISIONS.md` (#280-Nachtrag, `Betrifft:`).
**Vertrag:** 3.5; die Body-Felder sind hier optional (`?:`), damit die Aufrufer erst in T6 nachziehen — ab T6 Pflicht.
**Hinweis für den Implementer:** `EditableSetTarget.twitchChannelId` als Pflichtfeld bricht die Typisierung von `ResolvedRestoreTarget`-Literalen in `restore-flow.ts`, `mass-delete-panel.ts`, `file-import-step.ts` und deren Specs — diese Stellen bekommen das Feld in **diesem** Task (mechanisch aus `resolution.target`), damit der Build grün bleibt; die *Nutzung* als Hinweis ist T6.
**Tests:** 6.3, erster Spiegelstrich; 6.4, erster Spiegelstrich; E2E-Lauf (nur ohne Api auf `:5151`).
**Fertig:** Frontend-Gates grün, E2E grün.
**Abhängigkeiten:** T3 (die Route muss existieren, damit der Live-Test später trägt; für die Unit-Suite reicht der Vertrag).

### T5 — Frontend: Dateiformate tragen den Besitzer
**Art:** Frontend (shared/export).
**Dateien:** `shared/export/purge-run-export.ts`, `transfer-run-export.ts`, `transfer-undo-export.ts` (+ die drei Specs); Builder bekommen das Eingabefeld als **Pflichtfeld** `targetOwnerTwitchId: string | null`, die vier Aufrufer (`mass-delete-panel.ts`, `import-confirm-dialog.ts`, `import-progress-section.ts`, Undo) übergeben in diesem Task `null` — T6 füllt sie.
**Vertrag:** 3.7; `RestoreFileTarget.ownerTwitchId`, Undo-`target.ownerTwitchId`.
**Tests:** 6.3, sechster Spiegelstrich.
**Fertig:** Frontend-Gates grün; Versionskonstanten unverändert.
**Abhängigkeiten:** T4.

### T6a — Frontend: Import-Pfad — Picker-Wahl, Replace-Vorprüfung, `ImportRunInfo`, beide Berichte, Transfer-Dateien
**Art:** Frontend (shared + core).
**Dateien:** `shared/seven-tv/import-target-dialog.ts` (`ImportTargetChoice.twitchChannelId`, `selectSet`), `import-target-choices.ts` (nur, falls der Gruppe ein Feld fehlt — sie hat es), `import-flow.ts` (Hinweis an `resolveEditableSet`, an `startImport`, an `ImportConfirmDialogData` für die `planned`-Datei), `import-confirm-dialog.ts`, `import-progress-section.ts`, `core/seven-tv/seven-tv-import.service.ts` (`ImportRunInfo.targetOwnerTwitchId`, `startImport`-Ziel, `reportImported`, Removal-Report), alle zugehörigen Specs, `docs/DECISIONS.md` (`Betrifft:`).
**Vertrag:** 3.6 Zeilen 1 und Undo-unabhängig; 3.7 für `transfer-run`.
**Tests:** 6.3 (import-flow, import service, dialog, choices).
**Fertig:** Frontend-Gates grün.
**Abhängigkeiten:** T5.

### T6b — Frontend: Delete/Restore/Undo — Login-Hinweis der Lösch-Vorprüfung, eingefrorene Besitzer-ID, Run-Records, Berichte, Purge-/Undo-Dateien
**Art:** Frontend (shared + core).
**Dateien:** `shared/seven-tv/mass-delete-panel.ts` (Nr. 3.6 Zeilen 3–4, `openConfirmDialogAfterCheck` reicht die ID durch, `startDelete`-Aufruf, Purge-Protokoll), `restore-flow.ts` (`ResolvedRestoreTarget`, `RestoreStartTarget`), `undo-flow.ts` (`UndoRunTarget`), `file-import-step.ts` (Hinweis aus `parsed.target`), `core/seven-tv/seven-tv-delete.service.ts` (`DeleteRunInfo`, `startDelete`-Signatur, Body), `seven-tv-restore.service.ts`, `seven-tv-undo.service.ts` (Records, Bodies, Undo-Protokoll), alle zugehörigen Specs, `docs/DECISIONS.md` (`Betrifft:`).
**Vertrag:** 3.6 Zeilen 2–5; 3.7 für `purge-run` und `transfer-undo`.
**Tests:** 6.3 (mass-delete-panel, file-import-step, restore-/undo-flow, die drei Run-Services).
**Fertig:** Frontend-Gates grün.
**Abhängigkeiten:** T5 (parallel zu T6a; gemeinsame Datei nur `docs/DECISIONS.md`).

### T7 — E2E
**Art:** Frontend (e2e).
**Dateien:** `web/e2e/emote-import.e2e.spec.ts`.
**Tests:** 6.4, zweiter Spiegelstrich.
**Fertig:** `npm --prefix web run e2e` grün (nur ohne Api auf `:5151`/`:4200`).
**Abhängigkeiten:** T6a, T6b.

### T8 — Abnahme: Gates, Coverage, Reviews, Live-Verifikation
Läuft im Orchestrator mit dem Betreiber, nicht als Implementer-Subagent.
1. Alle Gates: `dotnet test EmotePurge.slnx` · `dotnet format EmotePurge.slnx --verify-no-changes` · `npm --prefix web test -- --watch=false` · `npm --prefix web run lint` · Prettier-Prüfung · `npm --prefix web run e2e` · `node scripts/coverage-local.mjs` (80 % auf neuem Code; Näherung, s. CLAUDE.md).
2. **Opus-Review** über den Branch, dann **Codex Sol**: `/codex:review --model gpt-6-sol --scope branch --base origin/feat/emote-sets-200`. Findings vorlegen, nicht umsetzen; Widerspruch ⇒ Fable als Schiedsrichter (global).
3. **Live-Verifikation** (Regel 16), Abschnitt 8.
4. PR gegen `feat/emote-sets-200` (Merge durch den Nutzer). Nach dem Merge: Epic #200, Tabelle „Open" → Zeile #216 nach „Done" mit PR-Nummer und Commit; Issue #216 von Hand schließen (Merges in den Integrationsbranch schließen nichts automatisch).

---

## 8. Live-Verifikation (T8, Betreiber)

**Setup:** lokaler Stack (`docker compose up -d postgres redis`, `dotnet run --project src/EmotePurge.Api`, `npm --prefix web start`), Testkonto `olaf_olaf_son` (ungetrackt; `sensitron` ist dort 7TV-Editor), frischer Cookie vom Betreiber. Vergleich **vorher** (Checkout `origin/feat/emote-sets-200`) und **nachher** (Branch), je dieselben drei Handgriffe; zwischen den Handgriffen mindestens 61 s warten, damit die Listen-Kopie kalt ist (Server 60 s, Client 60 s) — oder Redis-Schlüssel `7tvsets:*` löschen (`redis-cli --scan --pattern '7tvsets:*' | xargs redis-cli del`, lokal erlaubt).

**Messen, ohne Produktionscode:** (a) lokal, nicht committet: `Logging:LogLevel:System.Net.Http.HttpClient` in `appsettings.Development.json` auf `Information` — jeder ausgehende Request loggt „Start processing HTTP request POST https://7tv.io/v4/gql" bzw. `…/v3/gql`; Zeilen zwischen dem Klick und dem Audit-Insert zählen (so ist #216 gemessen worden). (b) Gegenprobe: `GET /api/admin/rate-limits` (Admin-Allowlist), `providers[]` je `callSource` — `seventv-emote-set-list` für Listen, `seventv-rest` für Owner-Lookup und Grants; Differenz vorher/nachher je Handgriff. (c) Nur bei Störung sichtbar: die `LogWarning`-Zeilen zu Budget/Breaker in `SevenTvEmoteSetListService`, `GuardedSevenTvEditorGrantsService`, `ImportTargetOwnershipService`. **Kein** Produktionscode nur zum Messen.

| Handgriff | Erwartung vorher | Erwartung nachher |
|---|---|---|
| Kalte Replace-Vorprüfung über den Picker (Picker öffnen, > 61 s warten, Replace bestätigen) | Listen-Route: 1 + k Listen-Requests | neue Route mit `ownerTwitchId`: **1** Listen-Request (Grants warm) |
| Kalter `sync-imported` an ungetracktes Set mit k Grants (Import in `olaf_olaf_son`, Bericht > 61 s nach Picker) | 1 + k Listen (#216: 6) | **1** Liste, Audit-Zeile identisch (Besitzer-Login `olaf_olaf_son`) |
| Datei-Tür ohne Hinweis (Restore aus einer **alten** Purge-Datei ohne `targetOwnerTwitchId`, kalte Kopie) | 1 + k | **1 + k** (Fallback belegt) — und dieselbe Datei nach einem neuen Lauf gespeichert: 1 |
| Kalte Lösch-Vorprüfung auf der Nutzungsseite (Login-Hinweis) | 1 + k | 1 (Login → Grant-ID aufgelöst; bei `sensitron` selbst: Akteur ⇒ 1) |

Zusätzlich prüfen: fremder `ownerTwitchId` per Hand (curl mit Cookie) ⇒ Antwort und Zählung wie
ohne Hinweis; die Set-Warnung/Vorschau bleiben unberührt; das Set nach dem Test exakt
wiederherstellen.

---

## 9. Commit-Aufteilung (Conventional Commits, englisch, keine `#`-Referenzen)

1. `feat(seven-tv): read the hinted owner's set list first in the ownership check` — T1 Kern + Tests + DECISIONS-Eintrag (ein Commit, Regel 3).
2. `docs(spec): correct the zero-request claim of the owner check (addendum 41)` — T1.
3. `feat(api): accept an owner hint on the set-centric reports` — T2.
4. `feat(api): add the set-scoped editable pre-check route` — T3.
5. `feat(web): resolve the editable pre-check cache-first, else through the set-scoped route` — T4 (mit #280-Nachtrag in DECISIONS).
6. `test(e2e): answer the set-scoped pre-check from the target-list fixture` — T4 (mocks.ts).
7. `feat(web): carry the target owner's Twitch id in the run protocols` — T5.
8. `feat(web): hint the owner on the import pre-check and both import reports` — T6a.
9. `feat(web): hint the owner on delete, restore and undo` — T6b.
10. `test(e2e): cover the cold and warm pre-check and the hinted delete report` — T7.

---

## 10. Offene Punkte (nicht aus dem Code entscheidbar)

1. **Login-Fallback aus dem Envelope für alte Dateien.** Eine `purge-run`-Datei trägt `channelName` (die Seite, deren Konto alle ihre Sets besitzt), eine `transfer-run`/`transfer-undo`-Datei `meta.targetChannelName` (null bei ungetracktem Ziel). Beides taugt als `ownerLogin` für eine Datei ohne `targetOwnerTwitchId`. Der Auftrag sagt „alte Dateien ⇒ heutiger Gang"; der Plan folgt dem. Will der Betreiber den Fallback, ist er eine Zeile in `file-import-step.ts` (T6b) plus ein Spec-Fall.
2. **Antwort-Cache je Set (Festlegung 20)** ist eine Erweiterung gegenüber dem Auftrag. Empfehlung: ja (spart ein Permit je Delete→Restore-Folge, gleiche TTL und Semantik wie E19). Nein ⇒ Zeile streichen, sonst nichts.
3. **Route nennt das Besitzer-Konto, die Liste das listende Konto** bei „unter A gelistet, von B besessen" (Festlegung 10). Empfehlung: Besitzer (Papierspur). Bestätigung erbeten.
4. **Kein Login-Feld auf den Berichten** (Festlegung 12) — der Auftrag nannte den Login-Fallback auch für „delete, activeSet"; im Code liefert jede Vorprüfung die ID, sodass die Berichte ihn nicht brauchen. Falls doch gewünscht (z. B. für einen Bericht aus einem Tab, der die Vorprüfung vor diesem Deploy gefahren hat): `TargetOwnerLogin` additiv, T2/T6 je eine Zeile.
5. **Name der zweiten Methode** und ob `IImportTargetOwnershipService` umbenannt wird (es dient jetzt auch der Vorprüfung). Empfehlung: nicht umbenennen (Churn in Api, Tests, DECISIONS-`Betrifft`); Doku nachziehen.
6. **`ownerLogin`-Vergleich über `ChannelName.Normalize`** setzt voraus, dass Twitch-Logins dieselbe Normalform haben wie Kanalnamen (trim + lowercase) — im Code identisch (Regel 9), aber als Annahme benannt.
7. **Messung:** ob `providers[]` in `/api/admin/rate-limits` die Grants-/Owner-Requests unter `seventv-rest` zählt oder ob `LookUpEditorGrantsAsync`/`LookUpEmoteSetOwnerAsync` die Telemetrie unterdrücken — im Live-Test prüfen; die HttpClient-Log-Zeilen sind unabhängig davon vollständig.

---

## 11. Abweichungen vom Auftrag, im Code gefunden

- „Konto zugleich eigen und gegrantet — schon übersprungen bei ~224": die Stelle ist `ImportTargetOwnershipService.cs:124-127` (Zeile 224 ist der `ApplyBreakerFeedback`-Switch).
- „Hinweis ⇒ Akteur: eigene Liste zuerst, keine Grants nötig bei Fund" ist **heute schon** das Verhalten (`:59-63`); die Umordnung betrifft nur Grant-Hinweise.
- **Der Client kennt die Twitch-ID eines getrackten Kanals nicht** (`ChannelSummary` ohne ID); Delete/Restore/Undo kommen an sie nur über die Antwort der Vorprüfung — deshalb ist `EditableSetTarget.twitchChannelId` tragend, nicht nur „verify the model".
- Daraus folgt: **die Berichte brauchen keinen Login-Hinweis** (Offener Punkt 4).
- Die Vitest-Fälle zu `resolveEditableSet` (`seven-tv-emote-set.service.spec.ts:339-496`) und der Block `mass-delete-panel.spec.ts:3155-3300`, der den echten Service gegen die Listen-Route fährt, müssen **umgeschrieben** werden — im Auftrag nicht genannt.
- Die E2E-Suite mockt nur die Listen-Route (`mocks.ts:943-973`, 20 Nutzungen); eine kalte Vorprüfung auf frischer Seite würde die neue Route ungemockt treffen und über den Dev-Proxy scheitern ⇒ Helfer-Erweiterung ist Pflicht (T4), nicht Kür.
- `SevenTvGqlRouteHandler` erkennt Arten am Abfragetext und antwortet Unbekanntes mit 503 — die v4-Liste ist heute „other" ⇒ 503; die Fake-Erweiterung muss die Liste **vor** `other` erkennen.
- Die Spec-Stelle `:2991-2992` betrifft den **Grant**-Cache (10 min) und ist für sich richtig; korrigiert wird die Verallgemeinerung, nicht der Satz.
- Es gibt zwei 20-s-Konstanten: `LIVE_READ_TIMEOUT_MS` (`recovery-file-gate.ts:6`, Import/Undo/Restore-Flow) und `LIVE_ALIAS_READ_TIMEOUT_MS` (`mass-delete-panel.ts:121`, Delete-Panel); beide bleiben unberührt.
- `Api.Tests` substituiert `IGuardedSevenTvEditorGrantsService`, `ISevenTvEmoteSetListService` und `ISevenTvApiClient`, **nicht** `IImportTargetOwnershipService` (`ApiFactory.cs:107-146`) — die Endpoint-Tests fahren den echten Kern; die Request-Zählung gehört deshalb in `Infrastructure.Tests`, nicht in `Api.Tests`.
