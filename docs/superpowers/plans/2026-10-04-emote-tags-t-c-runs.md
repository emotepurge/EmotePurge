# Emote-Tags T-C: Einspielen und Ausräumen — Platzierungen, Aktivierung, Operationen, Sync-Beobachtungen, Läufe (#201) — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Der Task bekommt diesen Plan, die Spec
> `docs/superpowers/specs/2026-10-04-emote-tags-design.md` (je Task genannte Abschnitte — für
> jeden Backend-Task **immer** 0a, 5.3–5.5 und 6.4), die Pläne T-A und T-B (für die Namen, die
> hier konsumiert werden) und die zitierten Dateien; er rollt E1–E34 **nicht** neu auf und
> übernimmt die Empfehlungen aus Spec 13.4 (Flag; Löschung ohne T′; Kanalsperre; 30 Minuten).
> Schritte als Checkbox (`- [ ]`). **Kein fertiger Code in diesem Plan.** Zeilenangaben gelten
> für `2db77d26`.
>
> Arbeitsort: eigener Worktree, Branch `feat/201-t-c-tag-runs` **von `main`, nachdem T-A und
> T-B gemergt sind** (und nach Epic #303 und dem Messlauf, Spec 12.2). Commits/Push erlaubt, Merge
> nicht (Regel 1); PR gegen `main`. Kein `docker compose` aus dem Worktree; **nie auf `vps`/`nas`
> verbinden** — Prod-Befehle werden vorbereitet, nicht ausgeführt.

**Ziel:** Ein Tag lässt sich mit einem Klick **einspielen** (Import-Lauf über alle Einträge, die
laut vollständiger Live-Lesung nicht im aktiven Set sind; das Tag gilt danach als eingespielt) und
**ausräumen** (Vorschau = Bestätigung mit angehakten eigenen Platzierungen und unangehakten Zeilen
mit Grund; Lösch-Lauf; das Tag gilt danach als nicht eingespielt — auch ohne Löschung). Der Server
führt Platzierungen, Aktivierungen und vorab registrierte Operationen, wendet die Lese-Zeit-Regel
gegen Verlassens-Beobachtungen des Syncs an und hält die Invariante „inaktiv ⇒ keine Platzierung".
Freigabe über das Flag `Tags:RunsEnabled` nach dem ersten Vollsync des neuen Workers.

**Architektur (2–3 Sätze):** Drei Tag-Tabellen (`EmoteTagPlacement`, `EmoteTagActivation`,
`EmoteTagOperation`) plus die Kanal-Tabelle `EmoteSetLeaveObservation` und die Spalte
`Emote.LastEnteredSetAtUtc`; `IEmoteTagService` bekommt Registrieren/Melden und liefert
Platzierungsfelder nur für **geltende** Platzierungen (Beobachtung jünger als die Registrierung der
Operation ⇒ verfallen). `SevenTvSyncService` und `EmoteService` schreiben Beobachtungen an den
glaubwürdigen Archivierungsstellen und stempeln den Eintritt — ohne Sperre, ohne Löschung. Im
Frontend laufen Einspielen über `startImportFlow` mit angepinnter Set-ID und `ImportOrigin 'tag'`,
Ausräumen über `resolveDeleteTarget`/`readLiveSetAliases` (T-A) + eigenen Vorschau-Dialog +
`SevenTvDeleteService.startDelete`; beide Läufe tragen eine dritte bzw. zweite Meldung am Record.

**Tech-Stack:** wie T-B; zusätzlich `crypto.randomUUID()`, Playwright `page.clock`, zwei
Transaktionen in Testcontainers (`PostgresLockProbe`).

**Spec:** umgesetzt werden 5.3, 5.4, 5.5 Regeln 2–6, 9, 10 und Gegenbeispiele 1–9; 6.1 Gruppe
„Registrieren und Melden" inkl. Besitzleiter; 6.2 Platzierungs-/Aktivierungsfelder mit
Lese-Zeit-Regel; 6.4; 6.5 T-C-Codes + `"tag"`-Vokabel; 7.1; 7.2; 8 (alle Lauf-Zeilen); 9.2
(Knöpfe), 9.4 (Kopfzeile, Marke, Lauf-Fläche), 9.5, 9.7 (T-C-Familien); 11 T-C komplett; 12.1
T-C, 12.3 Punkte 2, 2a, 3; 12.4; 12.5 Schritte 1–3; 13.2 (Hinweistext am Restore); 13.4/1–4
als Empfehlungen übernommen.

**Abhängigkeiten:** T-A (konsumiert `DeleteProgressSection` mit Input `hostSelectedSetId` und
Output `notice`, `resolveDeleteTarget`, `readLiveSetAliases`, `DeleteAbortNotice`,
`sevenTvRunLeaveGuard`), T-B (konsumiert `RemoveEntriesAsync` **unter der Kanalsperre**,
`IEmoteTagService`, `EmoteTagService` beider Seiten, `TagsPage`, `selectedTag`, Filterzeilen-
Inline-Gruppe, `tags.*`-Familien).

---

## 0. Befund gegen `2db77d26` und Abweichungen vom Spec

| Spec sagt | Code | Folge |
|---|---|---|
| 6.6: `IImportTargetOwnershipService` ist in `ApiFactory` bereits ersetzt | **Nein.** `ApiFactory.cs:252-277` ersetzt es nicht; `SevenTvEmoteSetSyncBookkeepingEndpointTests.cs` lässt die Besitzprüfung **echt** laufen und stellt die Fakes darunter (`ISevenTvEmoteSetListService`, `ISevenTvApiClient.LookUpEmoteSetOwnerAsync`, Helfer `ArrangeActorWithoutGrants`, `SetList`). | **Abweichung 1:** Die Leiter-Fälle der drei Melde-Routen arrangieren die Fakes wie dort (gleiche Helfer, ggf. in eine gemeinsame Test-Hilfsklasse gezogen) und prüfen zusätzlich, dass `IEmoteTagService` (substituiert) **nicht** aufgerufen wurde. |
| 3.3/6.1: „exakt die Leiter von `sync-deleted`" | `PassSyncInSetLadderAsync` (`SevenTvEndpoints.cs:638-680`) ist privat und an `SyncInSetRequest` gebunden; `sync-imported` (`:270-288`) hat denselben `switch` inline. | **Planentscheidung:** die Status→`IResult`-Abbildung wird in eine interne statische Hilfe `EmoteSetOwnershipRejection.For(status)` (`src/EmotePurge.Api/Endpoints/EmoteSetOwnershipRejection.cs`) gezogen und von beiden Bestandsstellen **und** den Tag-Routen benutzt — „exakt dieselbe Leiter" per Konstruktion; die bestehenden Bookkeeping-/Imported-Tests sind die Regression. |
| 7.1/6: der `trackedSet`-Zweig „liest über die #220-Vorschau-Route und kostet ein Permit aus `TrackedEmoteSetPreview`" | `import-target-loader.ts` `trackedSet` → `emoteSetService.loadEmoteSetPreview` → `GET /api/seventv/channels/{c}/emotes` = Policy **`ForeignEmoteLookup`** (10/60 s je Nutzer; Plan #220 ließ den Loader bewusst dort). | **Abweichung 2:** ein Einspielen kostet ein `ForeignEmoteLookup`-Permit, nicht `TrackedEmoteSetPreview`. Für zwei Läufe je Stream unkritisch; **vom Betreiber hingenommen (2026-10-04)**; kein Umbau des Loaders in T-C. **Nachläufer** (in den DECISIONS-Eintrag, Task 15): `import-target-loader.ts` liest getrackte Sets über die #220-Route `GET /api/channels/{c}/emote-sets/{id}/emotes` (`TrackedEmoteSetPreview`, 30/min). |
| 7.1/6: Vergleich „in `start()`, nach dem Arbiter-Check" | Der letzte Punkt vor `startImport` ist `startAfterCheck` (`import-flow.ts:~485-530`, zweiter Arbiter-Check `:507`); `start()` (`:414`) geht bei `replace`-Zeilen erst in `resolveEditableSet`. | Der Set-Vergleich sitzt **unmittelbar vor `startImport`** in `startAfterCheck`, nach `recheckTransferPlan` und dem zweiten Arbiter-Check — das ist „unmittelbar vor dem Start" im Sinn der Spec. |
| 3.4/E14: „dritte Meldung am Lauf-Record" | `ImportRunInfo` trägt schon **zwei** (`syncReport`, `removalReport`; Lifecycle-OR `seven-tv-import.service.ts:298`); `DeleteRunInfo` eine (`syncReport`); Präzedenz für mehrere ist `UndoRunInfo` (`removalReport`/`restoreReport`, `retryReport(kind)`). | Import: dritte, Delete: zweite Meldung — Muster Undo. Kein `reports[]`-Array (gibt es nicht). |
| 9.7: `import.origin.tag` | Die Origin-Zeile lebt unter `import.confirm.originChannel|originFile|originFileDetails|originLeaderboard`; der Undo-Dialog unter `undo.confirm.origin.*`. | **Abweichung 3 (Benennung):** `import.confirm.originTag` + `import.confirm.originTagSkipped.{one,other}`, `undo.confirm.origin.tag`. |
| 6.5: „`ValidateSyncImportedVocabulary` um `"tag"` ergänzen" | Drei Backend-Stellen kennen die Vokabel (`EmoteEndpoints.cs:358,385`; `AuditLogQueryService.cs:62-65, 220-260`; Kommentar `IEmoteService.cs:98`) und im Frontend zwei Literal-Unions (`emote-admin.service.ts:29`, `seven-tv-emote-set.service.ts:225`), `undo-confirm-dialog.ts:863-878` (erschöpfender `switch`), `transfer-run-export.ts:401-438` (`readImportOrigin`, fail-closed), `import-confirm-dialog.ts:234-254, 670-695` (`originChannelName` würde einen Tag-Origin mit `channelName` still als „aus Kanal" zeigen). Memory „ImportOrigin-Union: 6 Bruchstellen". | **Ergänzung:** Task 6 und 7 listen jede Stelle; die Origin-Zeile bekommt einen **eigenen** Zweig vor `originChannelName`. |
| 7.1/6: „Übersprungen-Zeile ‚3 sind schon im Set' (`skippedDuplicates`-Vorgabe)" | Der Bestätigungsdialog zeigt `preview.alreadyPresent` aus der **geladenen Zielliste** (`import-confirm-dialog.ts:449`), `skippedDuplicates` erscheint erst im Dock (`import-progress-section.ts:68-71`). Einträge, die Schritt 4 schon aussortiert hat, sieht der Dialog nicht. | **Planentscheidung:** der Tag-Origin trägt `alreadyInSetCount`; die Origin-Zeile des Dialogs zeigt „aus Tag Stronghold · 3 sind schon im Set". Keine Änderung an `ImportSource`. |
| 5.5 Regel 5/E34: Konstante `TagLeaveCredibilityWindow` „im Sync" | `SevenTvSyncService` hat nur `EmoteKeyIndexName`, `MissesBeforeLogging`; die 15-min-Messschwelle ist ein Literal (`:768-778`). | Konstante wie benannt als `private static readonly TimeSpan` in `SevenTvSyncService`; das Literal 15 bleibt (Messung), bekommt aber einen Kommentar, der auf das Fenster verweist. |
| 3.1: Sync-Speicherstellen | `ApplyEmoteSetUpdateAsync` Pulled-Schleife `:285-293`, Save `:300`; `ReconcileAsync(channelId, liveEmotes)` `:750`, Archiv `:780-782`, Aufruf aus `ApplyAndSaveAsync` `:410`, Save `:411` (zusammen mit Kanalzeile und Observation); `UpsertEmote` `:797-852`, Entarchivieren **teilt den Zweig** mit Umbenennung (`:825` Bedingung `Name != || ImageUrl != || IsArchived`); `IsRowVanishedFor` (`:884-900`) zählt erlaubte Entity-Typen in einem fehlgeschlagenen Save auf. | `ReconcileAsync` braucht die Set-ID als Parameter (steht in `channel.ActiveEmoteSetId`, das `ApplyAndSaveAsync` vorher setzt); der Eintrittsstempel prüft `IsArchived` **vor** dem Zweig. **Beide Archivierungsschleifen überspringen schon archivierte Zeilen** (`existing.TryGetValue(...) && !emote.IsArchived` `:287`, `!liveIds.Contains(id) && !emote.IsArchived` `:766`, Zuweisung `:780-782`) — eine Beobachtung, die nur „für tatsächlich archivierte IDs" geschrieben würde, verlöre jedes Verlassen einer schon archivierten Zeile (Codex-Befund 2, s. 3.4: Beobachtung **unabhängig** vom Archivübergang). Die Beobachtung wird per rohem `INSERT … ON CONFLICT` geschrieben (Muster `UsageStatFlushService.cs:84-91`), nicht als getrackte Entität — `IsRowVanishedFor` bleibt deshalb unverändert. |
| 9.5/9.4: Lauf-Fläche der Tags-Seite „mit `app-import-progress-section` und `app-delete-progress-section`" | `actionDockHasContent` (`action-dock.ts:72-90`) hält `deleteShown`/`deleteConfirmPending` **in** der `hasActiveSet`-Klammer; mit `hasActiveSet: false` wäre ein Dock mit nur einem Lösch-Lauf unsichtbar (kein Abbrechen, kein Retry, kein Protokoll, kein „Wiederherstellen" — Codex-Befund 1). Der Restore-Knopf der `DeleteProgressSection` startet `SevenTvRestoreService`, dessen Lauf nur `RestoreProgressSection` zeigt. | **Planentscheidung:** eigene reine Funktion `tagRunDockHasContent` neben `actionDockHasContent` (3.8), und die Tags-Seite mountet **drei** Sections: Import, Delete, Restore. |
| 5.3: „Kein FK auf Entry (E17 löscht explizit im Service)" | Platzierung und Eintrag teilen den Präfix `(TagId, SevenTvEmoteId)`; ein zusammengesetzter FK mit Kaskade ist im Modell möglich. Ohne FK kann eine Meldung, die unter der Kanalsperre den Eintrag las, nach einem parallelen Herausnehmen eine Waise schreiben (Codex-Befund 3). | **Festlegung 5.3 gekippt (Planentscheidung, s. Offene Punkte):** FK `(TagId, SevenTvEmoteId)` → `EmoteTagEntry` mit `Cascade` **zusätzlich** zur Kanalsperre beim Herausnehmen (T-B). Regel 5.5/2 wird damit von der Datenbank gehalten. |
| 12.5/3: Freigabe „nachdem ein Vollsync mit dem neuen Worker gelaufen ist (Log: Resync-Zusammenfassung)" | `SevenTvPeriodicResyncWorker` schreibt keine Zusammenfassung (nur Warnungen je Kanal/Durchlauf, `:102, :112`); die Boot-Recovery loggt je Kanal und gibt `BootRecoveryGate` auch nach Fehlern frei. | **Abweichung 4:** die Freigabe hängt an einer **ausführbaren** Prüfung über `Channels` (`LastSyncedAtUtc` nach dem Start des neuen Workers, `LastSyncFailureReason IS NULL` für jeden aktiven Kanal) — Task 15/16. |
| 3.1: `EmoteService.MarkInSetAsync` | privat (`:208`), Richtung `InSetDirection.Delete/Restore`, Zielzustand-Semantik (`found.Where(e => e.IsArchived != archive)`, `:262-274`), Tests über `MarkDeletedInSetAsync`/`MarkRestoredInSetAsync`. | Beobachtung für **jede gefundene Zeile** des Treffer-Kanals (nicht nur geänderte — die Meldung ist glaubwürdig, egal ob wir schon archiviert hatten); Stempel bei Restore nur für Zeilen, die tatsächlich entarchiviert werden. |
| 12.1: Flag „über `permissions` oder Config-Endpoint" | `ChannelPermissionsDto` (`ChannelEndpoints.cs:336`, fünf Felder); kein Config-Endpoint außer `GET /api/contact/config`; die Seite lädt `permissions` ohnehin je Kanal. | **Planentscheidung (13.4/1):** Feld `tagRunsEnabled` in `ChannelPermissionsDto`/`ChannelPermissions`, gespeist aus `EmoteTagOptions.RunsEnabled` (Section `Tags`). Keine neue Route, keine Matrix-Zeile, kein zweiter Request. |
| Sync-Stellen im Worker | `SevenTvPeriodicResyncWorker.cs:75` (nicht :53) ruft `SyncChannelAsync`; `Worker.cs:197/276`; `SevenTvEventClient.cs:380` ruft `ApplyEmoteSetUpdateAsync`. | Nur Zeilenkosmetik; Worker-Quelltext bleibt unberührt (E15 rev. 2). |

---

## 1. Globale Zwänge

Wie T-B Abschnitt 1, zusätzlich:

- **Sperrreihenfolge (5.5 Regel 6):** jede Meldung und jede Registrierung läuft in einer
  Transaktion, nimmt **zuerst** die Kanalzeile (`LoadChannelForUpdateAsync`), schreibt dann nur
  Tag-Tabellen und **liest** `EmoteSetLeaveObservation`. Der Sync schreibt nie eine Tag-Tabelle
  und sperrt nicht. Ein Verstoß ist ein Review-Blocker.
- **Uhr:** alle Zeitstempel `DateTime.UtcNow` der Anwendung (kein `now()` in SQL), damit
  `RegisteredAtUtc` (Api) und `LastObservedAtUtc` (Worker) vergleichbar bleiben (3.1, 12.4).
- **Fail-safe-Richtung (0a):** jede Unsicherheit kippt zu „weniger vorschlagen"; ein Test, der
  „mehr entfernen" belohnt, ist falsch.
- **Worker-Quelltext unverändert**, Worker-Image wird neu gebaut und zusammen mit der Api deployt
  (12.4). Frontend-Läufe nur hinter `!isCoarse()`, `startLocked() === false` und
  `tagRunsEnabled`.
- **`page.clock`:** `install()` vor `goto`, `pauseAt`, `runFor` — für Szenarien mit vergänglichen
  Meldungen und für Szenario 8 (Live-Event `channel.synced`).

---

## 2. Dateikarte

| Datei | Art | Verantwortung |
|---|---|---|
| `src/EmotePurge.Core/Entities/EmoteTagPlacement.cs`, `EmoteTagActivation.cs`, `EmoteTagOperation.cs` (+ `EmoteTagOperationKind`), `EmoteSetLeaveObservation.cs` | neu | Tabellen 5.3, 5.4. |
| `src/EmotePurge.Core/Entities/Emote.cs` | ändern | `LastEnteredSetAtUtc` (E34). |
| `src/EmotePurge.Core/Entities/AuditLogEntry.cs` | ändern | `AuditActions.TagPlayedIn`, `TagRemoved`. |
| `src/EmotePurge.Core/Services/IEmoteTagService.cs` | ändern | Platzierungsfelder, `RegisterOperationAsync`, `ReportPlacementsAsync`, `ReportRemovalAsync`, Records. |
| `src/EmotePurge.Core/Services/IEmoteService.cs` | ändern | Kommentar `:98` (Vokabel). |
| `src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs`, Migration `AddEmoteTagPlacements` | ändern/neu | Tabellen, Spalte, Indizes. |
| `src/EmotePurge.Infrastructure/Services/EmoteTagService.cs` | ändern | Lese-Zeit-Regel, Operationen, Meldungen, Übertragung, Sweep, Deaktivierung. |
| `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs` | ändern | Beobachtungen (Delta immer; REST mit 30-min-Fenster und Nachbetrachtung archivierter Zeilen), Eintrittsstempel, explizite Transaktion je Speicherversuch. |
| `src/EmotePurge.Infrastructure/Persistence/EmoteSetLeaveObservations.cs` | neu | Atomarer Upsert der Beobachtungen (`ON CONFLICT … GREATEST`) und die Lesehilfe für die Nachbetrachtung. |
| `web/src/app/core/seven-tv/tag-run-settlement.ts` (+ Spec) | neu | `deriveTagKeptIds` — `keptIds` aus `DeleteTagContext` und `RunResult` (core, damit der Delete-Service es nutzen darf). |
| `web/src/app/shared/seven-tv/action-dock.ts` (+ Spec) | ändern | `tagRunDockHasContent` neben `actionDockHasContent`. |
| `src/EmotePurge.Infrastructure/Services/EmoteService.cs` | ändern | Beobachtung (Delete), Stempel (Restore). |
| `src/EmotePurge.Infrastructure/Services/EmoteTagOptions.cs`, `ServiceCollectionExtensions.cs` | neu/ändern | `Tags:RunsEnabled`. |
| `src/EmotePurge.Infrastructure/Services/AuditLogQueryService.cs` | ändern | Vokabel `"tag"` in der Import-Projektion. |
| `src/EmotePurge.Api/Endpoints/EmoteSetOwnershipRejection.cs` | neu | Status→`IResult` der Besitzleiter (geteilt). |
| `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` | ändern | nutzt die Hilfe (zwei Stellen). |
| `src/EmotePurge.Api/Endpoints/EmoteTagEndpoints.cs` | ändern | Gruppe „Registrieren und Melden", Leserouten mit neuen Feldern. |
| `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs` | ändern | `ValidateSyncImportedVocabulary` + `"tag"`. |
| `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs` | ändern | `ChannelPermissionsDto.TagRunsEnabled`. |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | ändern | vier Codes. |
| `src/EmotePurge.Api/appsettings.json`, `.env.example`, `docker-compose.yml`, `docker-compose.prod.yml` | ändern | `Tags:RunsEnabled` Default `false`; `TAGS_RUNS_ENABLED` → `Tags__RunsEnabled` am **api**-Dienst. |
| `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteTagServiceTests.cs`, `SevenTvSyncServiceTests.cs`, `EmoteServiceTests.cs`, `ChannelRetentionPurgeTests.cs` | ändern | 11 T-C. |
| `tests/EmotePurge.Infrastructure.Tests/Unit/EmoteTagOptionsTests.cs` | neu | Options. |
| `tests/EmotePurge.Api.Tests/EmoteTagEndpointsTests.cs`, `AuthFilterMatrixTests.cs`, `EmoteRoutePolicyTests.cs`, `SevenTvEmoteSetSyncImportedEndpointTests.cs`, neu `EmoteTagReportLadderTests.cs` | ändern/neu | Leiter, Codes, Vokabel, Flag. |
| `web/src/app/core/tags/emote-tag.model.ts`, `emote-tag.service.ts` (+ Spec) | ändern | Felder 6.2, Bodies 6.4, drei Methoden. |
| `web/src/app/core/channels/channel.model.ts` | ändern | `tagRunsEnabled`. |
| `web/src/app/core/i18n/api-error.ts` | ändern | vier Codes. |
| `web/src/app/core/seven-tv/import-source.ts` (+ Spec) | ändern | `ImportOrigin` `'tag'`, Helfer. |
| `web/src/app/core/emotes/emote-admin.service.ts`, `core/seven-tv/seven-tv-emote-set.service.ts` | ändern | `sourceKind`-Unions. |
| `web/src/app/shared/seven-tv/import-flow.ts` (+ Spec) | ändern | `pinSetId`, `ImportFlowSetGuard`. |
| `web/src/app/core/seven-tv/seven-tv-import.service.ts` (+ Spec) | ändern | `tag`-Kontext, `tagPlacementReport`, Retry. |
| `web/src/app/core/seven-tv/seven-tv-delete.service.ts` (+ Spec) | ändern | `tag`-Kontext, `tagRemovalReport`, Retry. |
| `web/src/app/shared/seven-tv/import-confirm-dialog.ts`, `undo-confirm-dialog.ts`, `shared/export/transfer-run-export.ts` (+ Specs) | ändern | Tag-Origin. |
| `web/src/app/shared/seven-tv/import-progress-section.ts`, `delete-progress-section.ts` (+ Specs) | ändern | Dock-Zeilen `sevenTvRun.tagReport.*`, `discardedStale`, Restore-Hinweis 13.2. |
| `web/src/app/shared/tags/tag-play-in.ts`, `tag-removal.ts` (+ Specs) | neu | reine Funktionen 7.1/4, 7.2/5 (`ownInLiveIds`; die `keptIds`-Ableitung liegt in core). |
| `web/src/app/shared/tags/tag-removal-confirm-dialog.ts` (+ Spec) | neu | Vorschau = Bestätigung (E19). |
| `web/src/app/shared/tags/tag-play-in-flow.ts`, `tag-removal-flow.ts` (+ Specs) | neu | Abläufe 7.1, 7.2. |
| `web/src/app/shared/tags/tag-run-actions.ts` (+ Spec) | neu | Knöpfe Einspielen/Ausräumen + Notiz, gemeinsam für Filterzeile und Tags-Seite. |
| `web/src/app/features/usage-stats/usage-stats-page.ts/.html` (+ Spec) | ändern | Knöpfe in der Inline-Gruppe. |
| `web/src/app/features/tags/tags-page.ts/.html`, `tags.routes.ts` (+ Specs) | ändern | Zustand, Knöpfe, Marke, Dock mit drei Sections (`tagRunDockHasContent`), Announcer, Guard, Platzierungshinweis. |
| `web/src/app/shared/audit/audit-actions.ts` | ändern | zwei Aktionen. |
| `web/public/i18n/de.json`, `en.json` | ändern | 9.7 T-C-Familien. |
| `web/e2e/support/mocks.ts`, `web/e2e/emote-tags.e2e.spec.ts`, `web/e2e/audit/ui-audit.audit.ts` | ändern | Szenarien 2–8, Dialog-Szenario. |
| `docs/DECISIONS.md`, `docs/Architectur.md`, `docs/Operations.md`, `docs/UI-Designsprache.md` | ändern | Doku. |

---

## 3. Verträge

### 3.1 Datenmodell (5.3, 5.4)

- `EmoteTagPlacement { long TagId; string SevenTvEmoteId; string SevenTvEmoteSetId; DateTime PlacedAtUtc; Guid OperationId; EmoteTag Tag }` —
  PK `(TagId, SevenTvEmoteId, SevenTvEmoteSetId)`, Index `(SevenTvEmoteSetId, SevenTvEmoteId)`,
  FK → `EmoteTag` Cascade; **zusammengesetzter FK `(TagId, SevenTvEmoteId)` → `EmoteTagEntry`
  mit Cascade** (Festlegung 5.3 der Spec gekippt, Plan 0): die Datenbank hält damit Regel 5.5/2
  („keine Platzierung ohne Eintrag") und E17 (Herausnehmen nimmt Platzierungen aller Sets mit),
  unabhängig von der Sperrdisziplin eines Aufrufers. Verträglich mit jeder Schreibregel in 3.3:
  Einspielen platziert nur IDs **mit** Eintrag; die Übertragung setzt `TagId` nur auf ein T′, das
  per Regel 5.5/4 (b) einen Eintrag für X **hat**; Sweep und Deaktivierung löschen nur. Zwei
  Kaskadenpfade (Tag → Platzierung, Tag → Eintrag → Platzierung) sind in Postgres zulässig. In EF
  als Beziehung mit zwei FK-Spalten auf dem Entry-PK konfiguriert, ohne Navigation vom Entry.
  **Kein** FK auf Operation (ein FK zwänge eine Löschreihenfolge, die der Sweep nicht braucht —
  Festlegung).
- `EmoteTagActivation { long TagId; string SevenTvEmoteSetId; DateTime ActivatedAtUtc; Guid OperationId }` — PK `(TagId, SevenTvEmoteSetId)`, FK → `EmoteTag` Cascade.
- `EmoteTagOperation { Guid OperationId; long TagId; string Kind; string SevenTvEmoteSetId; DateTime RegisteredAtUtc; DateTime? AppliedAtUtc }` —
  PK `OperationId`, FK → `EmoteTag` Cascade, Index `(TagId, SevenTvEmoteSetId)`;
  `EmoteTagOperationKind.PlayIn = "playIn"`, `Removal = "removal"` (Konstanten wie
  `ChannelEmoteSetObservationClosedBy`).
- `EmoteSetLeaveObservation { string ChannelId; string SevenTvEmoteId; string SevenTvEmoteSetId; DateTime LastObservedAtUtc; Channel Channel }` —
  PK `(ChannelId, SevenTvEmoteId, SevenTvEmoteSetId)`, FK → `Channel` Cascade, keine Inverse.
- `Emote.LastEnteredSetAtUtc: DateTime?` — `null` für Bestand (= unbekannt ⇒ REST-Verlassen
  glaubwürdig).
- Migration `AddEmoteTagPlacements`: additiv (vier Tabellen, eine nullable Spalte).

### 3.2 Lese-Zeit-Regel und Leserouten (6.2)

Eine Platzierung **gilt**, wenn keine `EmoteSetLeaveObservation (Kanal, X, Set)` mit
`LastObservedAtUtc > RegisteredAtUtc` der Operation `Placement.OperationId` existiert. Abfrageform
(Regel 10): Platzierungen des Tags/Sets laden → Operationen zu den `OperationId`s als Dictionary →
Beobachtungen zu `(SevenTvEmoteId, Set)` des Kanals als Dictionary → Filter im Speicher. Drei
Abfragen über skalare Schlüssel, kein Navigations-Join.

Neue Felder (additiv zu T-B): `EmoteTagSummaryDto.PlacedCount`, `.Active`, `.ActivatedAtUtc`;
`EmoteTagEntriesResult.ActivationOperationId: Guid?`; `EmoteTagEntryDto.PlacedByThisTag`,
`.PlacedAtUtc`, `.PlacementOperationId: Guid?`, `.HeldByActiveTags: IReadOnlyList<EmoteTagRefDto>`,
`.PlacedByOtherTags: IReadOnlyList<EmoteTagRefDto>`; `EmoteTagRefDto(long Id, string Name)`.
Sortierung von `HeldByActiveTags`/`PlacedByOtherTags`: `CreatedAtUtc`, dann `Id` (5.5 Regel 3).
`HeldByActiveTags` = andere Tags mit `EmoteTagActivation (T′, Set)` **und** Eintrag für X —
unabhängig von Platzierungen. `PlacedByOtherTags` = andere Tags mit **geltender** Platzierung
`(·, X, Set)`.

### 3.3 Registrieren und Melden (6.4) — `IEmoteTagService`

| Methode | Eingabe | Ergebnis |
|---|---|---|
| `RegisterOperationAsync(channelName, tagId, RegisterTagOperationRequest(Guid OperationId, string Kind, string EmoteSetId), actor, ct)` | | `TagOperationRegistrationResult(Status: Ok \| Replayed \| ChannelNotFound \| TagNotFound \| Conflict, DateTime? RegisteredAtUtc)` |
| `ReportPlacementsAsync(channelName, tagId, TagPlacementReport(Guid OperationId, string EmoteSetId, IReadOnlyList<string> SevenTvEmoteIds), actor, ct)` | IDs leer erlaubt | `TagPlacementReportResult(Status: Ok \| ChannelNotFound \| TagNotFound \| OperationUnknown \| OperationConflict, bool Replayed, int RecordedCount, int AlreadyRecordedCount, IReadOnlyList<string> NotTaggedIds, IReadOnlyList<string> DiscardedStaleIds)` |
| `ReportRemovalAsync(channelName, tagId, TagRemovalReport(Guid OperationId, string EmoteSetId, Guid? ActivationOperationId, IReadOnlyList<TagPlacementSnapshotEntry(string SevenTvEmoteId, Guid PlacementOperationId)> Snapshot, IReadOnlyList<string> RemovedIds, IReadOnlyList<string> KeptIds), actor, ct)` | | `TagRemovalReportResult(Status wie oben, bool Replayed, int DeletedCount, int TransferredCount, int DroppedCount, int SweptCount, bool Deactivated)` |

Regeln (alle in **einer** Transaktion mit Kanalsperre zuerst; `AppliedAtUtc` am Ende):

- **Registrieren:** Operation existiert nicht → anlegen, `RegisteredAtUtc = UtcNow` (Serverzeit),
  `Ok`; existiert mit gleichem `(TagId, Kind, SevenTvEmoteSetId)` → `Replayed` mit dem alten
  Wert; existiert anders → `Conflict`. Ein `kind` außerhalb der zwei Konstanten ist ein Formfehler
  des Bodys und wird **im Handler** vor dem Service abgefangen: 400 mit dem **vierten T-C-Code
  `tag_operation_kind_invalid`** (Regel 7 vollständig: `ApiErrorCodes.TagOperationKindInvalid`,
  `api-error.ts`, beide Locales). Die Spec kennt nur drei Codes; `tag_operation_id_invalid` meint
  die UUID, `tag_operation_conflict` eine fremde Registrierung, `invalid_source_kind` ein anderes
  Vokabular — keiner passt, und ein falscher `kind` ist ein Programmierfehler des Browsers, den
  der Code für die Diagnose benennen soll. **Vom Betreiber entschieden (2026-10-04): der vierte
  Code kommt** — Task 6 (Konstante, Handler, Api-Test), Task 7 (`api-error.ts`, beide Locales).
- **Meldung allgemein:** Operation fehlt → `OperationUnknown`; `(TagId, Kind, EmoteSetId)` der
  Operation ≠ Meldung → `OperationConflict` (Kind: Einspiel-Meldung braucht `playIn`,
  Ausräum-Meldung `removal`); `AppliedAtUtc != null` → `Replayed`, alle Zähler 0, nichts
  geschrieben; `EmoteSetId` muss **nicht** aktiv sein (E6).
- **Einspielen** (6.4): je ID mit Eintrag: Beobachtung `(Kanal, id, Set)` mit
  `LastObservedAtUtc > RegisteredAtUtc` → `DiscardedStaleIds`; sonst Upsert
  `(TagId, id, Set)` mit `OperationId = operationId`, `PlacedAtUtc = UtcNow` — **neu und
  bestehend** (Regel 10 der Spec 5.5); `RecordedCount` neue, `AlreadyRecordedCount` bestehende
  Zeilen; ohne Eintrag → `NotTaggedIds`. Danach **immer** Upsert `EmoteTagActivation (TagId, Set)`
  mit `ActivatedAtUtc = UtcNow`, `OperationId = operationId`. Audit `tag.playedIn`
  `{ tagId, emoteSetId, operationId, emoteCount = RecordedCount + AlreadyRecordedCount }`.
- **Ausräumen** (6.4, Reihenfolge 4 → 1 → 2 → 3 → 5): P = alle Platzierungen `(TagId, ·, Set)`
  jetzt (geltend und verfallen); Treffer = Snapshot-Eintrag mit gleicher ID **und** gleicher
  `OperationId`. (4) Aktivierung löschen, wenn `Activation.OperationId == ActivationOperationId`
  → `Deactivated = true`; sonst `false`. (1) Treffer mit ID in `RemovedIds` → löschen
  (`DeletedCount`). (2) nur bei `Deactivated`: Treffer mit ID in `KeptIds` → Übertragung an das
  **älteste** andere Tag T′ mit `EmoteTagActivation (T′, Set)` und Eintrag für X: hat T′ schon
  `(T′, X, Set)` → nur löschen (`TransferredCount` zählt trotzdem die Übertragung? **Festlegung:**
  zählt als `Transferred`, weil die Verantwortung bei T′ liegt — die Zeile `(T, X, S)` verschwindet
  so oder so); sonst Übertragung als **Löschen + Neuanlegen** der Zeile `(T′, X, S)` mit
  `OperationId = operationId` und dem **alten** `PlacedAtUtc` — `TagId` ist Teil des PK, EF
  ändert Schlüsselwerte getrackter Entitäten nicht (Alternative: ein `ExecuteUpdate` auf den
  PK-Teil; der Task wählt, Verhalten identisch) (`TransferredCount`); kein T′ → löschen
  (`DroppedCount`). Bei `Deactivated == false` bleiben
  `KeptIds`-Treffer unberührt. (3) Treffer, deren ID weder in `RemovedIds` noch in `KeptIds` →
  löschen (`DroppedCount`), in beiden Fällen von (4). (5) nur bei `Deactivated`: jede weitere
  Platzierung in P (nicht getroffen) → geltend: Übertragung an ein aktives T′ **mit Eintrag für
  X** (dieselbe Kandidatenregel wie in (2) — ohne Eintrag verletzte die neue Zeile den FK) oder
  Löschung; verfallen: Löschung — `SweptCount`. Danach gilt `(T, S) inaktiv ⇒ keine Platzierung (T, ·, S)`.
  (6) IDs ohne Treffer sind kein Fehler. Audit `tag.removed`
  `{ tagId, emoteSetId, operationId, emoteCount = DeletedCount }`.
- **Retry-Regel:** `40001`/`40P01` werden **nicht** im Service gefangen — sie werden zur
  Exception, der Handler antwortet 500, der Browser wiederholt mit derselben `operationId`
  (E27 macht die Wiederholung harmlos). Kein eigener Retry-Code im Server (5.5 Regel 6).
- `RemoveEntriesAsync` (T-B, dort schon **unter der Kanalsperre**): die Platzierungen
  `(TagId, id, ·)` fallen über den FK (3.1) mit dem Eintrag; der Service zählt sie vorher nur für
  die Antwort/den Log. Die Sperre bleibt trotzdem Pflicht: sie ordnet das Herausnehmen gegenüber
  einer laufenden Meldung — ohne sie könnte eine Ausräum-Meldung unter der Sperre den Eintrag
  gelesen haben und nach dem Herausnehmen eine Übertragung an T′ versuchen, die der FK dann mit
  einer Verletzung abbricht (korrekt, aber ein 500 statt eines sauberen `dropped`). Mit der Sperre
  sieht jede Meldung den Stand nach dem Herausnehmen. `DeleteAsync` ergänzt `placementCount` in
  den Audit-Details (6.3).

### 3.4 Sync (5.5 Regel 5, E34)

- **Schreibhilfe** `EmoteSetLeaveObservations.RecordAsync(db, channelId, setId, ids, observedAtUtc, ct)`
  in `Infrastructure/Persistence/` (neben `AuditLogWrites`), von Sync **und** `EmoteService`
  benutzt: **ein** parametrisiertes `INSERT … ON CONFLICT ("ChannelId","SevenTvEmoteId","SevenTvEmoteSetId") DO UPDATE SET "LastObservedAtUtc" = GREATEST(existing, excluded)`
  über `db.Database.ExecuteSqlRawAsync` mit Array-Parametern für die IDs (Muster
  `UsageStatFlushService.cs:84-91`). Die Hilfe **dedupliziert und sortiert** die IDs (ordinal),
  bevor sie das Statement baut: `SevenTvDispatchParser` sammelt `PulledIds` als `List<string>` ohne
  Dedupe (`SevenTvDispatchParser.cs:26`), und eine #74-Duplikatzelle liefert dieselbe ID zweimal —
  ein mehrzeiliges `ON CONFLICT DO UPDATE` bricht dann mit „cannot affect row a second time"; die
  Sortierung gibt Api- und Worker-Upserts dieselbe Sperrreihenfolge auf den Zeilen. Warum atomar und mit `GREATEST` (Codex-Befund 4): Api
  (`MarkInSetAsync`) und Worker (Sync) schreiben aus getrennten Kontexten; ein Load/Update-Upsert
  könnte eine **ältere** Beobachtung nach einer neueren committen und `LastObservedAtUtc`
  **zurückdrehen** — eine dazwischen registrierte Platzierung gälte dann wieder; zwei erste Inserts
  derselben Zeile kollidierten am PK, und der Retry von `SaveSyncAsync` kennt nur den
  Emotes-Index. `GREATEST` macht die Reihenfolge der Commits egal, `ON CONFLICT` die Kollision.
  Die Entität `EmoteSetLeaveObservation` bleibt für Migration und **Lesen** (Lese-Zeit-Regel,
  Nachbetrachtung); geschrieben wird sie nie über den ChangeTracker. **Fehlerpfad:** verschwindet
  der Kanal zwischen Lesen und Upsert (Purge, Merge), wirft das rohe Statement eine
  `PostgresException` mit SQLSTATE `23503` (FK auf `Channels`) — **nicht** die
  `DbUpdateException`, die `IsRowVanishedFor` (`:884`) prüft. `SaveSyncAsync` behandelt diese
  Ausnahme deshalb ausdrücklich wie „Zeile verschwunden" (Sync-Versuch abbrechen, kein Retry, wie
  heute bei einer verschwundenen Kanalzeile); `MarkInSetAsync` lässt sie wie jeden anderen
  Datenbankfehler zum 500 werden (der Browser wiederholt die Meldung).
- **Transaktion:** Beobachtung und Archivierung sind nur dann **ein** Commit, wenn eine explizite
  Transaktion beide umschließt. Deshalb nehmen die drei Schreibstellen je Speicherversuch
  `BeginTransactionAsync` → `RecordAsync` → `SaveChangesAsync` → `Commit`, und zwar **genau hier:**
  (1) `ApplyEmoteSetUpdateAsync`: **vor** dem `!HasChanges()`-Guard (`:295`), also nach den
  Upsert-/Pulled-Schleifen — der Guard gibt `NoChange` zurück, ohne zu speichern, und genau der
  Codex-2-Fall (REMOVE-Delta für eine schon archivierte Zeile oder eine ID ohne Zeile) erzeugt
  **keine** getrackte Änderung; der Upsert läuft deshalb in einer eigenen Transaktion vor dem
  Guard, das Ergebnis bleibt `NoChange`, der Match-Cache wird nicht aufgefrischt. (2)
  `ApplyAndSaveAsync`: **nach** `emoteSetObservationService.RecordObservedSetAsync` (`:386`),
  das bei einem Set-Wechsel eine **eigene** Transaktion öffnet und committet
  (`ChannelEmoteSetObservationService.cs:66`; Kommentar `:384` will diesen Commit bewusst nicht
  teilen) — EF kann Transaktionen nicht schachteln, ein Umschließen von außen würfe bei jedem
  Set-Wechsel. Die neue Transaktion beginnt also erst mit den Kanalfeld-Zuweisungen (`:388ff`)
  und umfasst `ReconcileAsync` (inkl. `RecordAsync`) und `SaveChangesAsync` (`:411`); der
  Einmal-Retry von `SaveSyncAsync` bei `IsEmoteKeyConflict` öffnet je Versuch eine neue, der
  Upsert ist idempotent. (3) `MarkInSetAsync`: um das `SaveChangesAsync` (`:330`). **Abweichung vom Spec 3.1** („der Sync nimmt
  keine explizite Transaktion"): die Transaktion fügt **keine** Sperre hinzu, die der Sync nicht
  ohnehin durch seine Schreibzugriffe hält (Zeilensperren auf `Emotes`, `EmoteSetLeaveObservations`,
  `Channels`); die Deadlock-Analyse aus 5.5 Regel 6 bleibt: der Sync schreibt keine Tag-Tabelle,
  Meldungen schreiben keine Beobachtung und **lesen** sie nur (ein Lesen blockiert unter
  `READ COMMITTED` nicht auf eine gesperrte Zeile). Reihenfolge innerhalb des Syncs: Upsert zuerst,
  dann `SaveChanges` (mit dem Kanal-`UPDATE`), dann Commit — der Sync wartet so höchstens auf die
  Kanalzeile einer laufenden Meldung, nie umgekehrt.
- **Konstante** `private static readonly TimeSpan TagLeaveCredibilityWindow = TimeSpan.FromMinutes(30)`
  in `SevenTvSyncService` (13.4/4; Kommentar: SevenTV#81, warum Konstante statt Konfiguration).
- **Wo beobachtet wird — unabhängig vom Archivübergang** (Codex-Befund 2; beide Schleifen
  überspringen heute archivierte Zeilen, Plan 0):
  - (a) `ApplyEmoteSetUpdateAsync`: für **jede** ID in `delta.PulledIds`, ob die Zeile existiert,
    schon archiviert ist oder gerade archiviert wird — ein Delta ist immer glaubwürdig, und ein
    REMOVE für eine Zeile, die der REST-Sync kurz zuvor **ohne** Beobachtung (Fenster) archiviert
    hat, ist genau der Fall, der sonst verloren ginge. Set = `emoteSetId` des Deltas. Der Upsert
    liegt **vor** dem `NoChange`-Guard (s. „Transaktion"), sonst liefe er für diese Fälle nie.
  - (b) `ReconcileAsync(channelId, activeEmoteSetId, liveEmotes)`: für jede Zeile, die **nicht**
    im Live-Set ist, gilt: (b1) wird sie jetzt archiviert → Beobachtung nur, wenn
    `LastEnteredSetAtUtc is null || <= now − Window` (E34, wie bisher); (b2) ist sie **schon
    archiviert** → **Nachbetrachtung:** Beobachtung, wenn `LastEnteredSetAtUtc` älter als das
    Fenster oder `null` ist **und** keine Beobachtung `(Kanal, X, activeEmoteSetId)` mit
    `LastObservedAtUtc > LastEnteredSetAtUtc` existiert (bei `null`: keine Beobachtung existiert).
    Das stempelt ein im Fenster unterdrücktes Verlassen genau **einmal**, sobald das Fenster
    vorbei ist — und stempelt **nicht** bei jedem Durchlauf neu (sonst verfiele jede Platzierung
    einer Zeile, die der stale Cache noch als fehlend zeigt, Gegenbeispiel 8). Die Nachbetrachtung
    liest die vorhandenen Beobachtungen der archivierten Zeilen mit **einer** Abfrage über die
    skalare ID-Liste (Lesehilfe in `EmoteSetLeaveObservations`).
  - (c) `EmoteService.MarkInSetAsync` Richtung Delete: für jede **gefundene** Zeile je
    Treffer-Kanal (auch schon archivierte — die Meldung ist glaubwürdig), Set = `emoteSetId` der
    Meldung.
- **Eintrittsstempel:** `UpsertEmote`: neue Zeile → `LastEnteredSetAtUtc = UtcNow`; bestehende
  Zeile → nur wenn sie **vorher** `IsArchived == true` war (Umbenennung stempelt nicht).
  `MarkInSetAsync` Richtung Restore → Stempel für jede Zeile, die von archiviert auf unarchiviert
  wechselt; bestehende Beobachtungen bleiben. **Vierte Anlegestelle:** der Vote-Session-Upsert
  (`VoteSessionService.cs:479-482`, `INSERT … ON CONFLICT DO NOTHING`) legt Emote-Zeilen für
  Ballot-Emotes an, die der Sync noch nicht kennt — ohne Stempel stünden sie auf `null`
  (= „unbekannt, älter als das Fenster") und ein stale REST-Durchlauf schriebe sofort eine
  Beobachtung (Gegenbeispiel-8-Schutz fehlt für genau diese Zeilen). Der Upsert setzt
  `LastEnteredSetAtUtc = now` mit (die Zeile wird angelegt, **weil** das Set sie gerade enthält);
  Zeilen aus dem Bestand vor der Migration bleiben `null` (fail-safe).

### 3.5 Api (6.1 Gruppe „Registrieren und Melden", 6.4, 6.5)

- Gruppe `/api/channels/{channelName}/tags/{tagId:long}` mit Kette `RequireAuthorization` →
  `ChannelNameValidationFilter` → `UsageStatsAccessAuthorizationFilter` → `RequireRateLimiting(Bookkeeping)`:
  `POST /operations`, `POST /placements`, `POST /placements/removed`. Request-Records:
  `RegisterTagOperationRequest(string? OperationId, string? Kind, string? EmoteSetId, string? TargetOwnerTwitchId)`,
  `TagPlacementsRequest(string? OperationId, string? EmoteSetId, string? TargetOwnerTwitchId, IReadOnlyList<string>? SevenTvEmoteIds)`,
  `TagPlacementsRemovedRequest(…, string? ActivationOperationId, IReadOnlyList<TagPlacementSnapshotEntryRequest>? Snapshot, IReadOnlyList<string>? RemovedIds, IReadOnlyList<string>? KeptIds)`.
- Handler-Reihenfolge (bindend): Formprüfung (`OperationId` UUID → 400 `tag_operation_id_invalid`;
  `EmoteSetId` per `EmoteSetIdValidation.IsValid` → 400 `invalid_emote_set_id`; `Kind` → s. 3.3;
  Snapshot-Revisionen UUID → 400 `tag_operation_id_invalid`) → Actor (`TryBuildAuditActor`, null →
  401) → `IImportTargetOwnershipService.CheckAsync(actor.TwitchUserId, actor.Login, emoteSetId, ct, BuildOwnerHint(TargetOwnerTwitchId))`
  → `EmoteSetOwnershipRejection.For(result.Status)` (`SetNotFound` 404 `emote_set_not_found`,
  `Forbidden` 403 ohne Body, `Unavailable` 503 `foreign_channel_seventv_unavailable`) → Service →
  Status-Mapping: `OperationUnknown` 404 `tag_operation_unknown`, `OperationConflict`/`Conflict`
  409 `tag_operation_conflict`, `TagNotFound` 404 `tag_not_found`, `ChannelNotFound` 404
  `channel_not_found`; Registrieren `Ok` → 201 `{ registeredAtUtc }`, `Replayed` → 200 gleicher
  Body; Meldungen → 200 mit den Zählern (camelCase) und `replayed`.
- `BuildOwnerHint` wird aus `SevenTvEndpoints` **internal** gemacht oder in die neue Hilfe gezogen
  (eine Implementierung).
- `EmoteEndpoints.ValidateSyncImportedVocabulary`: `"tag"` wie `"file"` (kein
  `SourceChannelName`, kein `LeaderboardSort`). `AuditLogQueryService`: `"tag"` in der
  Import-Projektion wie `"file"` behandeln (eigener Konstantenwert `TagSourceKind`), Frontend-Label
  der Herkunft im Audit-Detail (Admin-Audit-Log/Aktivität) ergänzen, wo `seventv-leaderboard`
  heute eines hat.
- `EmoteTagOptions { const SectionName = "Tags"; bool RunsEnabled = false; Validate() }`,
  Singleton wie `ChannelCapacityOptions`; `ChannelPermissionsDto` + `bool TagRunsEnabled`
  (Handler liest die Options). Fehlercodes `TagOperationIdInvalid`, `TagOperationUnknown`,
  `TagOperationConflict`, `TagOperationKindInvalid` (vier, Betreiberentscheidung 2026-10-04).

### 3.6 Frontend — Kern

- `ChannelPermissions.tagRunsEnabled: boolean`; E2E-Default in `mockChannelPermissions` `true`.
- `core/tags`: Modelle um 3.2-Felder (camelCase) und `activationOperationId: string | null`;
  neue Typen `EmoteTagRef { id: number; name: string }` (für `heldByActiveTags`/`placedByOtherTags`)
  und `TagPlacementSnapshotEntry { sevenTvEmoteId: string; placementOperationId: string }`;
  Bodies `RegisterTagOperationBody { operationId; kind: 'playIn' | 'removal'; emoteSetId; targetOwnerTwitchId: string | null }`,
  `TagPlacementsBody { operationId; emoteSetId; targetOwnerTwitchId; sevenTvEmoteIds }`,
  `TagRemovalBody { operationId; emoteSetId; targetOwnerTwitchId; activationOperationId: string | null; snapshot: { sevenTvEmoteId; placementOperationId }[]; removedIds; keptIds }`;
  Antworten `TagOperationRegistration { registeredAtUtc }`, `TagPlacementsResult { replayed; recordedCount; alreadyRecordedCount; notTaggedIds; discardedStaleIds }`,
  `TagRemovalResult { replayed; deletedCount; transferredCount; droppedCount; sweptCount; deactivated }`;
  Methoden `registerOperation`, `reportPlacements`, `reportRemoval` (alle `POST`).
- `ImportOrigin` += `{ kind: 'tag'; tagId: number; tagName: string; channelName: string; alreadyInSetCount: number }`;
  `importOriginSourceChannelName('tag') → null`, `importOriginLeaderboardSort('tag') → null`;
  `sourceKind`-Unions in `emote-admin.service.ts:29`, `seven-tv-emote-set.service.ts:225` +
  `'tag'`; `undo-confirm-dialog.ts` `originView` Zweig `tag` (Key `undo.confirm.origin.tag`);
  `transfer-run-export.ts` `readImportOrigin` `case 'tag'` (Felder prüfen, sonst `null`);
  `import-confirm-dialog.ts`: computed `tagOrigin` **vor** `originChannelName` ausgewertet, Zeile
  `import.confirm.originTag` + bei `alreadyInSetCount > 0` `import.confirm.originTagSkipped.{one,other}`.
- `ImportFlowTarget` `'chosen'` += `pinSetId?: true`; `toTargetSelection`: mit `pinSetId` nie
  `trackedActive`, sondern `trackedSet` mit `choice.emoteSetId`; **ohne** Flag byte-identisch.
  `startImportFlow(deps, source, target, tagHook?: ImportFlowTagHook)` mit
  `ImportFlowTagHook { context: ImportTagContext; frozenSetId: string; activeEmoteSetId: Signal<string | null>; onSetChanged(): void; onNothingToImport(): void }`
  — ein Objekt für alles, was ein Tag-Lauf dem Flow mitgibt; ohne Hook verhält sich der Flow
  byte-identisch. Zwei Eingriffe in `startAfterCheck`, beide **nach** `recheckTransferPlan` und
  dem zweiten Arbiter-Check, unmittelbar vor `startImport`: (1) Set-Wächter —
  `outcome.targetSetId`, `activeEmoteSetId()`, `frozenSetId` gleich, sonst `onSetChanged()` und
  kein Lauf; (2) **No-op nach der letzten Duplikatprüfung** (Codex-Befund 5): hat der Plan nach
  `recheckTransferPlan` keine zu sendende Zeile mehr (ein Fremdeditor hat alles während der
  Bestätigung hinzugefügt), ruft der Flow `onNothingToImport()` statt `startImport` — denn
  `startImport` weist eine leere Liste ab und verwirft den Record (`seven-tv-import.service.ts:553-558`),
  `settleRun` liefe nie, die Aktivierung (E26) bliebe ungemeldet und ohne Retry. Der Tag-Flow
  sendet dann die leere Einspiel-Meldung der registrierten Operation (derselbe Pfad wie 7.1/5).
  `context` wird als `target.tag` bis `startImport` durchgereicht. **Dritter No-op-Pfad, im
  Dialog:** findet die **eigene** Zielladung des Bestätigungsdialogs schon alle Zeilen vor, ist
  `plan.rows` leer, und `execute()` kehrt heute ohne Schließen zurück (`import-confirm-dialog.ts:1188`,
  `nothingToAdd` `:1009`) — der Nutzer könnte nur abbrechen, nichts würde aktiviert. Deshalb:
  `ImportConfirmDialogData` bekommt `emptyConfirmAllowed: boolean` (nur Tag-Läufe setzen `true`),
  `ImportConfirmOutcome` ein optionales `nothingToAdd: true`; mit `emptyConfirmAllowed` bleibt der
  Bestätigen-Knopf bei leerem Plan aktiv (konstantes Label, §9; die vorhandene
  `import.confirm.alreadyPresent`-Zeile erklärt den Zustand) und `execute()` schließt mit
  `{ targetSetId, targetSetName, plan: leer, nothingToAdd: true }`; `start()` ruft dafür sofort
  `tagHook.onNothingToImport()` (nach dem Set-Wächter, ohne `recheckTransferPlan`). Ohne das Flag
  verhält sich der Dialog byte-identisch. Der `trackedSet`-Zweig liefert per Konstruktion die
  angefragte ID (kein Vergleich nach der Zielauflösung — 7.1/6).
- `SevenTvImportService.startImport(target, origin, plan, skippedDuplicates, duplicateCheckAvailable, replaceSkippedDrift)`:
  `target` += `tag?: ImportTagContext` mit `ImportTagContext { tagId: number; operationId: string }`
  (`targetOwnerTwitchId` steht schon am Ziel). `ImportRunInfo` += `tag: ImportTagContext | null`,
  `tagPlacementReport: SyncReportState` (`idle` ohne Tag), `tagPlacementReportReason`,
  `tagPlacementDiscardedStaleCount: number`; Lifecycle-OR um `tagPlacementReport === 'pending'`;
  `settleRun` setzt `pending`, wenn `tag !== null` (auch bei leerem `importedKeys` — die Aktivierung
  muss gemeldet werden); `reportTagPlacements(runId)` sendet
  `reportPlacements(channelName, tagId, { operationId, emoteSetId: run.targetSetId, targetOwnerTwitchId, sevenTvEmoteIds: importedKeys(run) })`
  mit `timeoutReportAttempt`/`retryTransientSyncFailures`, Endzustand über `endReport(runId, 'tag-placements', …)`;
  `retryTagPlacementReport()` (gleiche `operationId`); Signale `tagPlacementReport`,
  `tagPlacementReportReason`, `tagPlacementDiscardedStaleCount` als `linkedSignal` wie beim Undo.
- `SevenTvDeleteService.startDelete(setId, channelName, emotes, expectedChannelName, targetOwnerTwitchId, tag?: DeleteTagContext)`
  mit `DeleteTagContext { tagId; operationId; activationOperationId: string | null; snapshot: TagPlacementSnapshotEntry[]; checkedOwnIds: string[]; uncheckedOwnIds: string[]; channelName }`
  (definiert — wie `ImportTagContext` und `deriveTagKeptIds` — in
  `core/seven-tv/tag-run-settlement.ts`, die Services importieren den Typ; `checkedOwnIds` = angehakte **eigene**
  Platzierungen, `uncheckedOwnIds` = unangehakte eigene, beide `inLive`);
  `DeleteRunInfo` += `tag`, `tagRemovalReport`, `tagRemovalReportReason`; Lifecycle-OR;
  `settleRun` → `pending` bei `tag`; `reportTagRemoval(runId)`: `removedIds = result.doneKeys`,
  `keptIds = deriveTagKeptIds(run.tag, run.result)` — **reine Funktion in
  `core/seven-tv/tag-run-settlement.ts`** (core, nicht `shared/tags`: der Delete-Service darf
  nichts aus `shared/` importieren — Codex-Befund 8): `uncheckedOwnIds ∪ { id ∈ checkedOwnIds |
  keine Zeile mit Status done }`; bei `result === null` (kein Lauf) `uncheckedOwnIds ∪ checkedOwnIds`.
  `retryTagRemovalReport()`.
- Dock-Zeilen: `import-progress-section.ts` zeigt bei `tag !== null` eine Zeile
  `sevenTvRun.tagReport.{pending,succeeded,failed}` mit Retry-Knopf („Erneut melden", bestehender
  Wortlaut `sevenTvRun`-Familie, konstantes Label) und bei `discardedStaleCount > 0`
  `sevenTvRun.tagReport.discardedStale.{one,other}`; `delete-progress-section.ts` analog für
  `tagRemovalReport`; dazu der Hinweis am Restore-Knopf nach einem Tag-Lauf
  (`restore.afterTagRunHint`, 13.2). Alle `aria-hidden`; Ansage über `DockOutcomeAnnouncer`
  (neue Fälle dort: Tag-Meldung fehlgeschlagen/erfolgreich — eine Zeile je Zustand).

### 3.7 Frontend — Tag-Bausteine (`shared/tags/`)

- `tag-play-in.ts`: `partitionTagPlayIn(entries: readonly EmoteTagEntry[], live: SevenTvSetEntries): TagPlayInPartition`
  mit `{ toAdd: ImportRow[]; alreadyInSetIds: string[] }`; `inLive(id) = aliasesById.has(id) || aliaslessIds.has(id)`;
  Eintragsreihenfolge; `ImportRow { sevenTvEmoteId, name: alias, imageUrl }`. Kein Fallback auf
  `inSet` (E28). `buildTagImportSource(partition, tag: { id; name }, channelName): ImportSource`
  mit Origin `'tag'`, `duplicatesCollapsed = 0`, `discardedRows = 0`.
- `tag-removal.ts`: `proposeTagRemoval(entries, live): TagRemovalProposal` mit
  `rows: TagRemovalRow[]` (nur `inLive`), `notInSetCount`, `snapshot: TagPlacementSnapshotEntry[]`
  (alle `placedByThisTag`-Einträge, sichtbar oder nicht), `ownInLiveIds: string[]`;
  `TagRemovalRow { sevenTvEmoteId; aliases: string[]; checked: boolean; reason: 'placed' | 'heldBy' | 'alreadyPresent'; heldBy: EmoteTagRef[]; placedAtUtc: string | null }`
  nach den fünf Fällen 7.2/5 (`heldBy` = `heldByActiveTags` bei eigener Platzierung, sonst
  `placedByOtherTags`). `ownInLiveIds` ist die Grundlage, aus der der Flow nach dem Dialog
  `checkedOwnIds = ownInLiveIds ∩ checkedIds` und `uncheckedOwnIds = ownInLiveIds \ checkedIds` für
  den `DeleteTagContext` bildet; die Ableitung der `keptIds` nach dem Lauf liegt in **core**
  (`deriveTagKeptIds`, 3.6), nicht hier.
- `tag-removal-confirm-dialog.ts`: `TagRemovalConfirmDialogData { tagName; setName; isActiveSet; proposal; warning: Signal<EmoteSetWarning | null>; warningLoading: Signal<boolean> }`,
  `TagRemovalConfirmResult { checkedIds: string[] }` (Snapshot und `ownInLiveIds` kennt der Flow
  aus dem Proposal — der Dialog gibt nur die Haken zurück), `openTagRemovalConfirmDialog(dialog, data): DialogRef<TagRemovalConfirmResult | undefined>`.
  Aufbau 7.2/6: Titel `tags.removalDialog.title`; Zusammenfassung folgt den Haken; Set-Zeile
  (`massDelete.confirmSetLine`, `confirmSetNotActive`); Shared-Set-Warnung (`NoticeBanner error`,
  gleiche Keys wie der Delete-Dialog); erst angehakte Zeilen (Checkbox, Sprite-Still, Alias,
  `placedAt`), dann „Nicht vorgeschlagen" mit Grund; Haken in beide Richtungen umschaltbar; leise
  Sätze des Delete; `nothingToDelete` bei n = 0; `danger-solid` „Ausräumen", **immer aktiv**;
  `NamePreviewList [cap]="null"` der angehakten; virtuelles Scrollen ab > 50 Zeilen. **Design-Pass:**
  fix sind Reihenfolge, Primitive, Knopfzustand; zu entscheiden sind Zeilenhöhe, Position des
  Datums (Micro-Zeile), Trennung der zwei Blöcke (Hairline + Label wie §2.5/§6.2).
- `tag-run-actions.ts` (`TagRunActions`, Selector `app-tag-run-actions`): Inputs
  `channelName`, `tag: EmoteTagSummary` (required), `activeEmoteSetId: string | null`,
  `setName: string | null`, `enabled: boolean` (Host-Gatter: `tagRunsEnabled && !isCoarse() && aktives Set bekannt`);
  Outputs `completed` (nach `closed` eines Laufs oder direktem Melde-Erfolg → Host lädt neu),
  `feedback: { key; params }` (vergängliche Meldung, der Host zeigt sie in **seiner** Region).
  Rendert `[Einspielen]` (`neutral`, immer) und `[Ausräumen]` (`danger`, nur `tag.active`),
  beide `[disabled]` bei `arbiter.startLocked()` oder `pending()` — Grund als Text nur bei einer
  **anderen** Sperre als dem laufenden Lauf (§4.2 „ohne Hinweistext" für den Arbiter); darunter
  eine `NoticeBanner error`-Zeile für den Flow-Fehler mit „Erneut versuchen" (gleiche
  `operationId`). Besitzt `pending`, `notice`, baut `TagRunFlowDeps` aus den injizierten Services
  und ruft die beiden Flows. Wird von Filterzeile **und** Tags-Seite gemountet (9.5: identische
  Abläufe, andere Fläche).
- `tag-play-in-flow.ts`: `TagRunFlowDeps = ImportFlowDeps & { deleteService; tagService; translocoService; destroyRef }`;
  `TagRunRequest { channelName; tag: { id; name }; activeEmoteSetId: Signal<string | null>; pending: WritableSignal<boolean>; notice: WritableSignal<TagRunNotice | null>; onFeedback(key, params); onCompleted() }`;
  `startTagPlayInFlow(deps, request)`: Schritte 7.1/1–6 in der Reihenfolge 1 → 2 (Entries frisch,
  Set-Vergleich) → 2b (`resolveDeleteTarget` aus T-A — die Set-Prüfung ist dieselbe Funktion; bei
  `blocked` Banner mit **neutralem** Wortlaut `tags.errors.target.{notEditable,notSelectable,unavailable}`,
  gemappt aus `blocked.reason` (T-A 3.1) — nicht die löschbezogenen `massDelete.errors.*`-Texte;
  Ende) → 2a (Registrieren `playIn`; 403 → `noWriteRight`; 503 →
  `ownershipUnavailable`; andere → `reportFailed`) → 3 (`readLiveSetAliases`; `blocked` →
  `setReadIncomplete`/`setReadUnavailable`) → 4 (Partition) → 5 (leer: direkt `reportPlacements`
  mit `[]`, Erfolg → `onFeedback('tags.feedback.allPresent')`, `onCompleted()`; Fehler → Banner
  mit Retry) → 6 (`startImportFlow(deps, source, { kind: 'chosen', choice, pinSetId: true }, tagHook)`
  mit `choice` gebaut wie `toImportTarget` in `import-trigger.ts:56-75` und `tagHook` nach 3.6:
  `onSetChanged` → `notice = tags.errors.setChanged`; `onNothingToImport` → **derselbe** Pfad wie
  Schritt 5 (leere Einspiel-Meldung mit der registrierten `operationId`, Feedback `allPresent`,
  Fehler → Banner mit Retry) — der Lauf ist dann ein erfolgreicher No-op, obwohl die Partition
  nicht leer war). Abbruchprüfung „Set gewechselt" nach Schritt 2: Antwort-`emoteSetId` ≠
  eingefroren oder `isActiveSet === false`.
- `tag-removal-flow.ts`: `startTagRemovalFlow(deps, request)`: 7.2/1–8: Entries frisch →
  Set-Vergleich → `resolveDeleteTarget` → Registrieren `removal` → `readLiveSetAliases` →
  `proposeTagRemoval` → Shared-Set-Warnung laden (`emoteAdminService.getSetWarning`) → Dialog →
  Bestätigung: Abbruchprüfung (`activeEmoteSetId() !== frozen` → `massDelete.setChangedDuringConfirm`;
  `arbiter.activeRun() !== null` → `noteRefusedStart('delete')`) → n = 0: direkt
  `reportRemoval` mit `removedIds: []`, `keptIds = deriveTagKeptIds(tagContext, null)` (= alle
  eigenen `inLive`) → `onFeedback('tags.feedback.removedNothing')`; n > 0:
  `deleteService.startDelete(frozenSetId, channelName, queue, channelName, ownerTwitchChannelId, tagContext)`
  mit `queue` aus den angehakten Zeilen (`DeleteQueueEmote { sevenTvEmoteId, name: aliases[0] ?? defaultName, aliases }`),
  `tagContext.checkedOwnIds = ownInLiveIds ∩ checkedIds`, `tagContext.uncheckedOwnIds = ownInLiveIds \ checkedIds`
  (3.7 `tag-removal.ts`). Token-Prompt vor dem Dialog wie
  `MassDeletePanel.openConfirm` (nach der Registrierung, damit 403 vor dem Prompt kommt).
  `beginConfirmedRun`/`endConfirmedRun` am `deleteService` wie im Delete-Flow (Dock-Gatter).

### 3.8 Oberflächen (9.2, 9.4, 9.5)

- Filterzeile: in der Inline-Gruppe (T-B) ersetzt `<app-tag-run-actions>` den Platzhalter-Satz,
  wenn `shownSetId() === activeEmoteSetId()` **und** `permissions().tagRunsEnabled`; sonst bleibt
  der Satz. Die Zahl „eingespielt" (`tags.filter.active`) erscheint in der Gruppe bei
  `selectedTag().active`. `completed` → `tagsResource.reload()`, `tagFilterEntriesResource.reload()`;
  `feedback` → Zählzeilen-Region aus T-B.
- Tags-Seite: Kopfzeile des Details mit Zustand (`tags.page.state.active` „eingespielt seit
  {{date}}" / `state.inactive`), `<app-tag-run-actions>` vor Umbenennen/Löschen (§8.7: konstruktiv
  → destruktiv), Platzierungs-Marke an Zellen mit `placedByThisTag` (kleine Marke unten links,
  `aria-hidden`, Wort im `aria-label` der Zelle), Meta-Zeile zeigt `placedAt`; Löschen-Dialog mit
  `tags.deleteDialog.placedHint.{one,other}` bei `placedCount > 0`; Micro-Zeile links mit
  „eingespielt ({{placed}} platziert)". Lauf-Fläche: eigener `.app-dock` (§2.5) mit
  `DockClearanceService` (`reserve`/`release` wie `usage-stats-page.ts:358`). **Dock-Vertrag der
  Tags-Seite** (Codex-Befund 1 — `actionDockHasContent` hält den Lösch-Lauf in der
  `hasActiveSet`-Klammer, `action-dock.ts:78-90`; mit `hasActiveSet: false` bliebe ein Dock mit
  nur einem Ausräum-Lauf unsichtbar): neue reine Funktion `tagRunDockHasContent(state: TagRunDockState)`
  in `shared/seven-tv/action-dock.ts` mit `TagRunDockState { deleteShown; restoreShown; restoreNoticePending; importShown; importNoticePending }`
  — `true`, sobald **eines** der fünf Felder `true` ist; kein `hasActiveSet`, kein `markedCount`
  (die Seite hat keine Markierungshälfte), kein Undo (die Seite startet keinen), und **kein
  `deleteConfirmPending`**: auf der Nutzungsseite hält dieses Feld das Dock mit der
  Markierungshälfte offen, während der Dialog offen ist bzw. `ABORTED_DELETE_NOTICE_MS` (8 s) nach
  einem Abbruch; die Tags-Seite hätte in dieser Zeit nur eine **leere** Leiste — die Abbruchnotiz
  steht dort ohnehin in der Status-Region der Seite außerhalb des Docks (`runNotice`). Die Seite
  speist die Felder aus denselben Service-Signalen wie `usage-stats-page.ts:1660-1681`
  (`deleteService.isRunning() || queue().length > 0`, `restoreService.isRunning() || queue().length > 0`,
  `restoreService.duplicateNoticePending()`, `importService.run() !== null && (…)`,
  `importService.duplicateNoticePending()`). **Sperrgrund für fremde Läufe:** sperrt
  `arbiter.startLocked()` die Knöpfe wegen eines Laufs, dessen Fläche die Seite **nicht** mountet
  (heute nur der Undo, gestartet auf der Nutzungsseite), zeigt `TagRunActions` den Grund als Text
  mit `aria-describedby` (`tags.errors.otherRunActive` mit dem Lauf-Label aus
  `SEVEN_TV_RUN_KIND_LABEL_KEY`; §10 „jede Sperre erklärt sich"); für Import/Delete/Restore gilt
  §4.2 unverändert — deren Dock erklärt den Zustand. Im Dock
  **drei** Sections: `<app-import-progress-section />`,
  `<app-delete-progress-section [hostSelectedSetId]="activeEmoteSetId()" (notice)="runNotice.set($event)" />`
  (T-A) und `<app-restore-progress-section />` — der „Wiederherstellen"-Knopf der Delete-Section
  startet `SevenTvRestoreService`, dessen Lauf nur die Restore-Section zeigt (E13, Rückweg für
  R1–R4). Permanenter `<app-dock-outcome-announcer [withImport]="true" />` außerhalb des Docks;
  eigene Status-Region für `runNotice`; `tags.routes.ts` bekommt `canDeactivate: [sevenTvRunLeaveGuard]`.
  Mobile (grober Zeiger): keine Knöpfe, kein Dock (9.4 Festlegung).

### 3.9 i18n (T-C-Familien, de Referenz)

`tags.page.state.{active,inactive}`, `tags.page.micro.active` „eingespielt ({{placed}} platziert)",
`tags.page.micro.inactive`, `tags.page.placedMark` „eingespielt", `tags.page.placedAt`;
`tags.actions.{playIn,remove}`; `tags.removalDialog.*` (9.7 vollständig: `title`, `summary`,
`placedAt`, `reason.alreadyPresent`, `reason.heldBy`, `notProposedHeading`, `notInSet.{one,other}`,
`nothingToDelete`, `confirm`, `proposedHeading`); `tags.deleteDialog.placedHint.{one,other}`;
`tags.feedback.{allPresent,removedNothing}`; `tags.errors.*` (`setChanged`, `setReadIncomplete`,
`setReadUnavailable`, `reportFailed`, `retry`, `noWriteRight`, `ownershipUnavailable`,
`registrationFailed`, `target.{notEditable,notSelectable,unavailable}` — neutral formuliert, für
das Einspielen; das Ausräumen bleibt bei `massDelete.errors.*` —, `otherRunActive` „Ein Lauf
({{kind}}) läuft auf einer anderen Seite"); `sevenTvRun.tagReport.{pending,succeeded,failed,retry}`,
`sevenTvRun.tagReport.discardedStale.{one,other}`; `tags.filter.active`;
`import.confirm.originTag`, `import.confirm.originTagSkipped.{one,other}`, `undo.confirm.origin.tag`;
`restore.afterTagRunHint`; `errors.api.tag_operation_id_invalid|tag_operation_unknown|tag_operation_conflict|tag_operation_kind_invalid`;
`audit.actions.tagPlayedIn|tagRemoved`; Herkunfts-Label
`"tag"` im Audit-Detail, wo die anderen Vokabeln eines haben.

---

## 4. Tasks

Reihenfolge: T1 → T2 → T3 → T4 → T5 → T6 (Backend) ∥ T7 → T8 → T9 → T10 → T11 → T12 → T13
(Frontend; T7 braucht nur den Vertrag 3.6) → T14 → T15 → T16. T2 und T3 sind unabhängig. T7
liefert den core-Helfer `deriveTagKeptIds`, **bevor** T9 ihn im Delete-Service benutzt; T10
(reine `shared/tags`-Funktionen) hängt nur an T7.

**Schichtenprüfung über alle drei Pläne** (Codex-Befund 8): `core/` importiert nur `core/`
(`tag-run-settlement.ts`, `DeleteTagContext`, `ImportTagContext`, `TagPlacementSnapshotEntry`,
`EmoteTagRef` liegen alle in `core/`); `shared/tags/*` und `shared/seven-tv/*` importieren
`core/` und `shared/ui`; `features/` importiert beide. `sevenTvRunLeaveGuard` liegt in
`shared/seven-tv/` (T-A), weil er `shared/ui/confirm-dialog` öffnet. Jeder Task prüft seine
Importe gegen diese Tabelle (`npm run lint` kennt die Grenze nicht — Review-Disziplin).

### Task 1 — Tabellen, Spalte, Migration `AddEmoteTagPlacements`

**Modell:** sonnet. **Kontext:** Spec 5.3, 5.4, E22, E33, E34; Plan 3.1; T-B Task 1 (Muster);
`AppDbContext.cs`; `ChannelRetentionPurgeTests.cs` (Kaskade).

- [ ] Vier Entitäten, `EmoteTagOperationKind`, `Emote.LastEnteredSetAtUtc`; `AuditActions.TagPlayedIn/TagRemoved`;
      `OnModelCreating` nach 3.1 inkl. des zusammengesetzten FK Platzierung → Eintrag mit Cascade;
      Kommentare englisch (warum FK auf Entry — Regel 5.5/2 in der Datenbank — und kein FK auf
      Operation, warum Kanal- statt Tag-Tabelle für Beobachtungen, warum `null` = unbekannt).
- [ ] `dotnet ef migrations add AddEmoteTagPlacements …`; Migration lesen: rein additiv (vier
      Tabellen, eine nullable Spalte, der FK).
- [ ] Test (Integration): Löschen eines Eintrags kaskadiert zu allen Platzierungen `(Tag, X, ·)`
      über mehrere Sets; Löschen eines Tags kaskadiert über beide Pfade ohne Fehler; eine
      Platzierung ohne Eintrag lässt sich nicht einfügen (FK-Verletzung).
- [ ] Kaskadentest erweitern: Kanal mit Tag, Platzierung, Aktivierung, Operation und
      Beobachtung → Purge → alle sechs Tabellen leer (Spec 10 Pflichtaussage).
- [ ] Gates; ein Commit: `feat(core): add tag placement, activation, operation and leave observation tables`.

### Task 2 — Sync: Beobachtungen und Eintrittsstempel (Worker-Verhalten)

**Modell:** opus (Hot-Path des Workers; drei Schreibstellen; explizite Transaktion um den
Einmal-Retry; 30-min-Fenster; Nachbetrachtung). **Kontext:** Spec 3.1, 5.5 Regel 5, E8 rev. 4, E33, E34, 12.4, Gegenbeispiel 8;
Plan 0 (Sync-Zeilen), 3.4; `SevenTvSyncService.cs:187-307, 316-359, 375-413, 750-852, 884-900`;
`EmoteService.cs:208-332`; `AuditLogWrites.cs` (Muster für die Persistence-Hilfe);
`SevenTvSyncServiceTests.cs` (Fabriken `CreateService`/`CreateRestService*`, Seeds, Tests ab
`:161, 179, 475, 789, 807`), `EmoteServiceTests.cs:410-1069`.

**Dateien:** `SevenTvSyncService.cs`, `EmoteService.cs`, neu
`Infrastructure/Persistence/EmoteSetLeaveObservations.cs`, Tests.

- [ ] Persistence-Hilfe `EmoteSetLeaveObservations` nach 3.4: `RecordAsync(db, channelId, setId, ids, observedAtUtc, ct)`
      als **ein** parametrisiertes `INSERT … ON CONFLICT … DO UPDATE SET … = GREATEST(…)` über
      `ExecuteSqlRawAsync` (Tabellen-/Spaltennamen wie im Modell-Snapshot, quoted; IDs als
      Array-Parameter, leere Liste → kein Statement); dazu
      `LoadLatestAsync(db, channelId, setId, ids, ct)` → `Dictionary<string, DateTime>` für die
      Nachbetrachtung und (Task 3) die Lese-Zeit-Regel. Kopfkommentar: warum atomar, warum
      `GREATEST`, warum nicht über den ChangeTracker.
- [ ] `RecordAsync` dedupliziert und sortiert die IDs vor dem Statement (3.4; Duplikat-ID aus
      einer #74-Zelle darf das Statement nicht brechen).
- [ ] Explizite Transaktion je Speicherversuch an den drei Stellen **genau wie in 3.4
      „Transaktion" verortet**: in `ApplyEmoteSetUpdateAsync` **vor** dem `!HasChanges()`-Guard
      (`:295`); in `ApplyAndSaveAsync` **nach** `RecordObservedSetAsync` (`:386`, eigene
      Transaktion) ab den Kanalfeld-Zuweisungen bis zum Save (`:411`), Retry öffnet eine neue; in
      `MarkInSetAsync` um das Save (`:330`). Kommentar mit der Deadlock-Begründung und dem
      Hinweis auf die nicht schachtelbare Transaktion des Observation-Service.
- [ ] `SaveSyncAsync`: `PostgresException` mit SQLSTATE `23503` aus dem rohen Upsert wie „Zeile
      verschwunden" behandeln (3.4 Fehlerpfad).
- [ ] `ApplyEmoteSetUpdateAsync`: Beobachtung für **jede** ID in `delta.PulledIds` (Set =
      `emoteSetId`), unabhängig von Zeile/Archivzustand, **vor** dem `NoChange`-Guard.
- [ ] `VoteSessionService`-Upsert (`:479-482`): `LastEnteredSetAtUtc = now` für neu angelegte
      Zeilen mitschreiben (3.4 „Vierte Anlegestelle"). `ReconcileAsync(channelId, activeEmoteSetId, liveEmotes)`:
      (b1) Archivierung + Beobachtung nur außerhalb des Fensters; (b2) Nachbetrachtung schon
      archivierter, nicht-live Zeilen nach 3.4 (einmalig, über `LoadLatestAsync`). `UpsertEmote`
      stempelt wie 3.4. Kommentar an der 15-min-Messzeile (`:768-778`) verweist auf das Fenster;
      Konstante `TagLeaveCredibilityWindow` mit Kommentar (SevenTV#81, 13.4/4, Konstante statt
      Konfiguration).
- [ ] `EmoteService.MarkInSetAsync`: Delete → `RecordAsync` für jede gefundene Zeile je
      Treffer-Kanal (auch schon archivierte); Restore → Stempel für Zeilen, die entarchiviert
      werden.
- [ ] Tests `SevenTvSyncServiceTests` (Testcontainers; Seeds setzen `LastEnteredSetAtUtc` und
      Beobachtungen direkt): REST-Vollsync archiviert eine fehlende Zeile **und** schreibt die
      Beobachtung `(Kanal, ID, ActiveEmoteSetId)` für `LastEnteredSetAtUtc = null` und für
      `now − 31 min`; **keine** Beobachtung bei `now − 5 min`, Archivierung trotzdem; Kanal ohne
      Tags bekommt Beobachtungen; Delta `PulledIds` schreibt **immer** — auch für eine **schon
      archivierte** Zeile und für eine ID **ohne** Zeile, **und zwar bei Ergebnis `NoChange`**
      (Assertion: Rückgabe `NoChange`, Beobachtung existiert trotzdem, Match-Cache nicht
      aufgefrischt); Delta mit **derselben ID zweimal** in `PulledIds` → eine Zeile, kein Fehler;
      **Set-Wechsel mit anstehenden Beobachtungen** (REST-Stub liefert eine neue Set-ID und
      lässt Zeilen fehlen) → `RecordObservedSetAsync` schließt sein Intervall, danach Archivierung
      + Beobachtung im eigenen Commit, kein Transaktionsfehler; zweiter Delta zur selben ID → eine Zeile,
      `LastObservedAtUtc` neuer; Delta mit **älterem** `observedAtUtc` als die vorhandene Zeile
      (direkt über die Hilfe) → Wert bleibt (`GREATEST`); **Nachbetrachtung:** archivierte Zeile
      mit `LastEnteredSetAtUtc = now − 31 min` und ohne Beobachtung → REST-Durchlauf stempelt;
      zweiter Durchlauf stempelt **nicht** erneut (Wert unverändert); archivierte Zeile mit
      Beobachtung **jünger** als der Eintritt → kein Stempel; `UpsertEmote` stempelt beim Anlegen
      und beim Entarchivieren, **nicht** bei Umbenennung; Platzierungen bleiben vom Sync
      unberührt. **Szenario Codex-Befund 2** als Ablauf: Eintritt (Stempel jetzt) → REST-Durchlauf
      mit stale Cache archiviert **ohne** Beobachtung → REMOVE-Delta → Beobachtung existiert →
      PUSH-Delta (Wiederhinzufügen, Eintritt neu gestempelt) → eine Platzierung, deren Operation
      **vor** dem REMOVE registriert wurde, gilt nicht mehr (Lese-Zeit-Regel über `LoadLatestAsync`);
      Variante ohne REMOVE-Delta (EventAPI aus): nach Ablauf des Fensters stempelt der nächste
      REST-Durchlauf per Nachbetrachtung genau einmal. **Zwei Kontexte** (Tagged DbContexts,
      `PostgresLockProbe`): (i) gleichzeitige erste Inserts derselben Zeile → beide Commits
      erfolgreich, eine Zeile; (ii) Kontext A stempelt `t₂`, Kontext B stempelt `t₁ < t₂`, B
      committet **nach** A → Zeile trägt `t₂`. Save-Konflikt-Pfad: der Einmal-Retry von
      `SaveSyncAsync` läuft mit neuer Transaktion durch und hinterlässt genau eine Beobachtung —
      Muster `SyncChannel_VoteSessionInsertsTheSameEmoteMidSync_RetriesOnce…`.
- [ ] Tests `EmoteServiceTests`: `MarkDeletedInSetAsync` schreibt die Beobachtung je Treffer-Kanal
      (auch für eine schon archivierte Zeile); `MarkRestoredInSetAsync` stempelt
      `LastEnteredSetAtUtc` und lässt eine bestehende Beobachtung stehen; untracked Set → keine
      Beobachtung (kein Treffer-Kanal); ein Api-Kontext (`MarkDeletedInSetAsync`) und ein
      Worker-Kontext (Delta) stempeln dieselbe Zeile überlappend → Maximum gewinnt.
      `VoteSessionServiceTests`: eine vom Upsert neu angelegte Zeile trägt `LastEnteredSetAtUtc`,
      eine schon vorhandene behält ihren Wert (`DO NOTHING`).
- [ ] Gates; ein Commit: `feat(infra): record credible set leave observations and stamp set entry in the 7TV sync`.

**Abnahme:** `git diff src/EmotePurge.Worker` leer; jeder der drei Beobachtungspfade hat einen
positiven und (REST) einen negativen Fall; die Nachbetrachtung hat einen „einmal, nicht zweimal"-
Fall; die zwei Zwei-Kontexte-Fälle sind grün.

### Task 3 — Lese-Zeit-Regel und Platzierungsfelder der Leserouten

**Modell:** opus. **Kontext:** Spec 6.2 vollständig, 5.5 Regeln 3, 5, 7; Plan 3.2; T-B Task 2/3
(Lesemethoden, Handler); `UsageStatQueryService.cs:51-56` (Regel 10).

- [ ] Records erweitern (3.2), `ListAsync`/`ListEntriesAsync` berechnen Platzierungs-,
      Aktivierungs- und Halterfelder über drei Abfragen mit skalaren Schlüsseln; Leserouten
      geben die Felder aus (camelCase).
- [ ] Tests (`EmoteTagServiceTests`, Zeilen „Lese-Zeit-Regel" der Spec 11): Platzierung mit
      Beobachtung **jünger** als die Registrierung fehlt in `placedByThisTag`/`placedByOtherTags`/
      `placedCount`; **älter** → unverändert; Beobachtung in **anderem** Set → unverändert
      (set-bezogen); `heldByActiveTags` nur bei Aktivierung und Eintrag, sortiert nach
      `CreatedAtUtc`/`Id`; `placedByOtherTags` nur geltende; `activationOperationId` oder `null`;
      `active`/`activatedAtUtc`; Fremd-Set → `inSetCount`/`inSet` `null`, Platzierungsfelder
      aber für dieses Set berechnet; Kanal ohne aktives Set und ohne Parameter → Felder leer.
- [ ] Api-Test: Antwortform der beiden Leserouten mit den neuen Feldern (Substitute liefert ein
      Beispiel; Assertion auf JSON-Feldnamen).
- [ ] Gates; ein Commit: `feat(infra): expose valid tag placements and activations with the read-time rule`.

### Task 4 — Registrieren und Einspiel-Meldung

**Modell:** opus. **Kontext:** Spec 5.4, 5.5 Regeln 2, 6, 10; 6.4 (Registrieren, Einspielen);
E10, E27, E33; Gegenbeispiele 4, 6, 8, 9; Plan 3.3; `ChannelService.cs:158-199` (Transaktion),
`PostgresLockProbe.cs` (zwei Transaktionen).

- [ ] `RegisterOperationAsync`, `ReportPlacementsAsync` nach 3.3; Kommentare: warum
      Registrierung vor dem Lauf (E27), warum Upsert immer `OperationId`/`PlacedAtUtc` setzt
      (Regel 10), warum `discardedStaleIds` kausal gelesen wird.
- [ ] Tests: Registrierung idempotent (`Replayed`, gleicher Zeitwert); Konflikt bei anderem Tag,
      Set oder Kind; `RegisteredAtUtc` liegt zwischen zwei `UtcNow`-Lesungen des Tests; Meldung
      ohne Registrierung → `OperationUnknown`; anderes Set → `OperationConflict`; falsches Kind
      → `OperationConflict`; Upsert neu (`RecordedCount`) und bestehend (`AlreadyRecordedCount`,
      `OperationId` und `PlacedAtUtc` überschrieben); leere Liste aktiviert; `NotTaggedIds`;
      `DiscardedStaleIds` bei Beobachtung **nach** der Registrierung, nicht bei Beobachtung
      davor (kausal: Beobachtung wird mit gestempeltem Zeitpunkt geseedet); alles verworfen →
      Aktivierung trotzdem; Replay → `Replayed`, nichts geschrieben (Counts vorher/nachher
      gleich); Audit `tag.playedIn` ohne Name. **Gegenbeispiel 4** (Wiederholung), **6**
      (verspätete Meldung nach Verlassen), **8** (Stronghold-Standardfall: Zeilen mit frischem
      Stempel, REST-Sync archiviert ohne Beobachtung → Meldung platziert), **9** (zwei
      Transaktionen: Meldung hält die Kanalsperre und hat eingefügt, Beobachtung committet
      parallel → nach beiden Commits gilt die Platzierung **nicht**; umgekehrte Reihenfolge →
      `DiscardedStaleIds`) als je ein Szenario; Invarianten-Assertion „inaktiv ⇒ keine
      Platzierung" als gemeinsame Hilfe, nach jedem Szenario aufgerufen.
- [ ] Gates; ein Commit: `feat(infra): register tag operations and record play-in placements`.

### Task 5 — Ausräum-Meldung: Treffer, Übertragung, Sweep, Deaktivierung

**Modell:** fable (Rechenlogik mit gehebelter Fehlerwirkung: ein Fehler hier löscht Platzierungen
falsch oder verletzt die Invariante; sieben Gegenbeispiele sind der Prüfstein). **Kontext:** Spec
0a, 5.3, 5.5 Regeln 3, 4, 6, 10 und Gegenbeispiele 1–7; 6.4 „Ausräumen" (Reihenfolge 4 → 1 → 2 →
3 → 5 → 6); E7 rev. 2, E26 rev. 3, E27; 13.4/2; Plan 3.3; Task 4 (Code und Testhilfen).

- [ ] `ReportRemovalAsync` nach 3.3; `RemoveEntriesAsync` zählt die kaskadierenden Platzierungen
      für Antwort/Log (E17 hält der FK); `DeleteAsync` zählt `placementCount` ins Audit.
      Kommentare: die Reihenfolge der Schritte und warum (2) nur bei `Deactivated`.
- [ ] **Rennen Herausnehmen ↔ Meldungen** (Codex-Befund 3; zwei Tagged DbContexts,
      `PostgresLockProbe`): (i) Einspiel-Meldung hält die Kanalsperre und hat den Eintrag für X
      gelesen; `RemoveEntriesAsync(X)` startet parallel und wartet; Meldung platziert X und
      committet; Herausnehmen läuft durch → **keine** Platzierung für X (FK-Kaskade), Eintrag weg;
      (ii) umgekehrt (Herausnehmen zuerst) → Meldung sieht keinen Eintrag → `NotTaggedIds` enthält
      X; (iii) Ausräum-Meldung will `(T, X, S)` an T′ übertragen, T′s Eintrag für X wird parallel
      herausgenommen → serialisiert: läuft das Herausnehmen zuerst, ist T′ nicht mehr Kandidat →
      `DroppedCount`; läuft die Meldung zuerst, kaskadiert das Herausnehmen die übertragene
      Platzierung weg. In allen Fällen danach: keine Platzierung ohne Eintrag (Assertion über einen
      Join Platzierung ⟕ Eintrag).
- [ ] Tests: Treffer nur bei ID **und** Revision (anderer `placementOperationId` → kein
      Treffer, Zeile bleibt); `RemovedIds` löscht; `KeptIds` wandert zum ältesten aktiven T′ mit
      Eintrag (Löschen + Neuanlegen — PK-Teil), `OperationId` neu, `PlacedAtUtc` alt; Sweep-
      Kandidat, dessen einziges aktives T′ **keinen** Eintrag für X hat → gelöscht, nicht
      übertragen (kein FK-Fehler); T′ hat schon eine Platzierung → Zeile weg,
      `TransferredCount`; kein T′ → `DroppedCount`; weder removed noch kept → `DroppedCount`;
      Sweep räumt nicht getroffene, hereingewanderte und verfallene Zeilen (verfallene nur
      löschen, nie übertragen); `deactivated: false` → `KeptIds` unberührt, kein Sweep;
      `ActivationOperationId` null bei fehlender Aktivierung → `Deactivated false`; Replay; Audit
      `tag.removed`. **Gegenbeispiele 1, 2, 3, 5, 7** als je ein Szenario (5 und 7 mit zwei
      verschachtelten Operationen; 7 zusätzlich mit zwei gleichzeitig startenden Transaktionen
      gegen die Kanalsperre per `PostgresLockProbe`: die zweite wartet, danach beide inaktiv,
      keine Platzierung, X-Eintrag bleibt); Invarianten-Assertion nach jedem Szenario.
- [ ] Gates; ein Commit: `feat(infra): apply tag removal reports with transfer, sweep and deactivation`.

**Abnahme:** Jeder Zähler des Ergebnisses hat mindestens einen Fall > 0 und einen = 0; die
Invariante ist in jedem Szenario geprüft.

### Task 6 — Api: Besitzleiter, drei Routen, Vokabel, Flag, Codes

**Modell:** sonnet. **Kontext:** Spec 6.1 (Registrieren/Melden), 6.4, 6.5, 6.6, E9 rev. 3,
12.1 (Flag), 13.4/1; Plan 0 (Abweichung 1, Hilfe), 3.5; `SevenTvEndpoints.cs:242-370, 620-680`,
`EmoteEndpoints.cs:358-400`, `AuditLogQueryService.cs:62-65, 220-260, 360-400`,
`ChannelEndpoints.cs:78-109, 336`, `ChannelCapacityOptions.cs` + Registrierung
(`ServiceCollectionExtensions.cs:72-75`), `appsettings.json`;
`SevenTvEmoteSetSyncBookkeepingEndpointTests.cs` (Helfer), `SevenTvEmoteSetSyncImportedEndpointTests.cs:70-77`,
`AuthFilterMatrixTests.cs`, `EmoteRoutePolicyTests.cs`; `docs/Architectur.md` Tabelle.

- [ ] `EmoteSetOwnershipRejection.For(status)` extrahieren, beide Bestandsstellen umstellen
      (Tests bleiben grün — Regression), `BuildOwnerHint` teilen.
- [ ] Drei Routen nach 3.5 in `EmoteTagEndpoints.cs` (dritte `MapGroup`); Fehlercodes;
      `ValidateSyncImportedVocabulary` + `"tag"`; `AuditLogQueryService` Vokabel + Frontend-Label
      (Task 7 trägt die Locale-Zeile); Kommentar `IEmoteService.cs:98`.
- [ ] `EmoteTagOptions` + Registrierung + `appsettings.json` (`"Tags": { "RunsEnabled": false }`);
      `ChannelPermissionsDto.TagRunsEnabled`; Unit-Test `EmoteTagOptionsTests` (Default false,
      Bind true).
- [ ] Api-Tests: 401 für die drei Routen; 403 bei `CanViewUsageStatsAsync false` **vor** der
      Leiter (Besitz-Fakes so arrangiert, dass die Leiter 404 gäbe — Nachweis der Reihenfolge);
      `CanManageChannelAsync false` + `CanViewUsageStats true` → **nicht** 403 (E9);
      `invalid_channel_name` vor Autorisierung; 400 `tag_operation_id_invalid` (UUID, Snapshot-
      Revision), 400 `tag_operation_kind_invalid` (`kind` unbekannt oder fehlend, nur
      Registrier-Route), 400 `invalid_emote_set_id` am Body **vor** der Leiter; Leiter auf allen drei
      Routen: `Forbidden` → 403 leer, `SetNotFound` → 404 `emote_set_not_found`, `Unavailable` →
      503 — jeweils `IEmoteTagService` nicht aufgerufen; `Owner` → Service aufgerufen mit
      normalisiertem Kanalnamen und der `RegisteredAtUtc`-Antwort 201/200; Status-Mapping
      (`OperationUnknown` 404, `Conflict` 409, `Replayed` 200); Policy `Bookkeeping` je Route;
      Vokabel `"tag"` ohne Namen → ok, mit Namen → `invalid_source_kind` (`InlineData`
      ergänzen); `/permissions` liefert `tagRunsEnabled` aus den Options (Factory mit
      Konfigurationswert `Tags:RunsEnabled=true`).
- [ ] `docs/Architectur.md`: Zeile für die Melde-Gruppe (Kanalfilter + Besitzleiter im Handler).
- [ ] Gates; zwei Commits: `refactor(api): share the emote set ownership rejection ladder` und
      `feat(api): add tag operation and placement report endpoints behind the ownership ladder`.

### Task 7 — Frontend-Kern: Modelle, Service, Codes, `ImportOrigin 'tag'`, Flag

**Modell:** sonnet. **Kontext:** Spec 6.4 (Bodies), 6.5, E12, 12.3/2; Plan 0 (Vokabel-Stellen),
3.6 (Kern); Memory „ImportOrigin-Union: 6 Bruchstellen"; alle in 3.6 genannten Dateien.

- [ ] Modelle/Service-Methoden (+ `HttpTestingController`-Spec: Routen, Bodies, `emoteSetId`
      nie als Query bei den POSTs); vier Codes + Locales; `ChannelPermissions.tagRunsEnabled`.
- [ ] `core/seven-tv/tag-run-settlement.ts`: `deriveTagKeptIds(tag: Pick<DeleteTagContext, 'checkedOwnIds' | 'uncheckedOwnIds'>, result: RunResult | null): string[]`
      (3.6) — **vor** Task 9, weil der Delete-Service es importiert; `DeleteTagContext` und
      `ImportTagContext` werden hier als Typen in `core/seven-tv/` angelegt (Service-Dateien
      erweitern sie in Task 8/9 nicht, sie importieren sie). Spec: ohne Lauf → beide Mengen;
      mit Lauf → unangehakte + angehakte ohne `done`-Zeile (`failed`/`cancelled`/`unknown`/
      fehlend); `done` nicht; Reihenfolge stabil (unangehakte zuerst, dann angehakte in
      Kontextreihenfolge); keine Duplikate.
- [ ] `ImportOrigin 'tag'` und **jede** Stelle aus 3.6; `import-source.spec.ts`: Helfer liefern
      `null`; `import-confirm-dialog.spec.ts`: Tag-Origin zeigt die Tag-Zeile, **nicht** die
      Kanal-Zeile, mit und ohne `alreadyInSetCount`; `undo-confirm-dialog.spec.ts`: Tag-Origin
      hat ein Label; `transfer-run-export.spec.ts`: Roundtrip des Tag-Origins, kaputte Felder →
      `null`.
- [ ] Audit-Labels `tagPlayedIn`/`tagRemoved` in `audit-actions.ts` + Locales; Herkunfts-Label
      `"tag"` im Audit-Detail.
- [ ] Gates: Vitest, Lint, Format. Zwei Commits:
      `feat(web): add tag operation and report contracts to the tag service` und
      `feat(web): add the tag import origin to every origin consumer`.

**Abnahme:** `grep -rn "'seventv-leaderboard'" web/src/app` → jede Fundstelle hat ein `'tag'`
daneben oder ist erschöpfend über die Helfer.

### Task 8 — Import-Flow: `pinSetId`, Set-Wächter, Tag-Kontext, dritte Meldung

**Modell:** opus. **Kontext:** Spec 7.1/6–8, E14, E29 rev. 3, 12.3/2a, 3; Plan 0 (Set-Vergleich
in `startAfterCheck`), 3.6; `import-flow.ts` (`toTargetSelection :143-157`, `start :414`,
`startAfterCheck :485-530`), `import-target-loader.ts:150-220`, `seven-tv-import.service.ts`
(`startImport :472`, `ImportRunInfo`, Lifecycle `:298`, `settleRun :~770`, `sendFollowUp :~795`,
`reportImported :850`, `importedKeys :1085`, `retrySyncReport :621`), `seven-tv-undo.service.ts:239-243, 342, 604-609, 812-815, 947`
(Zwei-Meldungen-Muster), `import-progress-section.ts:60-120`, `dock-outcome-announcer.ts`;
Specs `import-flow.spec.ts`, `seven-tv-import.service.spec.ts`, `import-progress-section.spec.ts`.

- [ ] `pinSetId` + vierter Parameter `tagHook?: ImportFlowTagHook` in `startImportFlow` (3.6);
      `toTargetSelection` respektiert `pinSetId`; in `startAfterCheck` nach `recheckTransferPlan`
      und dem zweiten Arbiter-Check: Set-Wächter, dann der No-op-Zweig `onNothingToImport()`
      (Codex-Befund 5), sonst `startImport` mit `target.tag = tagHook.context`.
- [ ] `startImport` nimmt `target.tag`; `ImportRunInfo`-Felder, Lifecycle-OR, `settleRun`,
      `reportTagPlacements` parallel zu `reportImported` in `sendFollowUp`, `endReport`-Variante,
      `retryTagPlacementReport`, Signale.
- [ ] Dock-Zeile in `import-progress-section.ts` (3.6) + Announcer-Fälle.
- [ ] Specs: `import-flow.spec.ts` — `pinSetId: true` erzwingt `trackedSet` auch bei
      `emoteSetId === activeEmoteSetId` (ohne Flag unverändert `trackedActive`; bestehende Fälle
      bleiben); Loader mit genau der angepinnten ID; **Set-Wechsel während der Bestätigung**
      (Signal ändert sich vor `start`) → kein `startImport`, `onSetChanged` gerufen; unveränderter
      Fall → `startImport` mit `targetSetId` = eingefroren und `tag` im Ziel; **nicht-leere
      Partition, die `recheckTransferPlan` vollständig leert** (Mock: zweite Live-Lesung enthält
      alle Zeilen) → kein `startImport`, `onNothingToImport` genau einmal, `onSetChanged` nicht;
      ohne Hook bleibt der heutige Pfad (Aufruf von `startImport` mit leerer Liste, der Service
      verwirft — bestehender Fall unverändert); **Dialog findet alles vor** (`emptyConfirmAllowed`,
      Ziel-Liste enthält alle Zeilen) → Knopf aktiv, Schließwert `nothingToAdd: true`, Flow ruft
      `onNothingToImport` ohne `recheckTransferPlan`; ohne das Flag kehrt `execute()` wie heute
      zurück (`import-confirm-dialog.spec.ts`, bestehender Fall).
      `seven-tv-import.service.spec.ts` — ohne Tag `tagPlacementReport === 'idle'` und `closed`
      wartet nicht; mit Tag `pending` hält `closed` auf, `succeeded` schließt; `failed` bietet
      Retry mit **derselben** `operationId` (Body-Vergleich zweier Aufrufe); Body-Form
      (`sevenTvEmoteIds = importedKeys`, leer nach Abbruch vor der ersten Zeile); Antwort
      `discardedStaleIds.length` landet im Signal; 403 → `failed` mit `forbidden` ohne Auto-Retry.
      `import-progress-section.spec.ts` — Zeile nur bei Tag-Lauf; Retry-Knopf ruft die Methode.
- [ ] Gates; zwei Commits: `feat(web): pin the target set for tag imports and guard the start`
      und `feat(web): report tag placements as a third report on the import run`.

### Task 9 — Delete-Service: Tag-Kontext und zweite Meldung

**Modell:** sonnet. **Kontext:** Spec 7.2/7–8, E14; Plan 3.6 (`DeleteTagContext`,
`deriveTagKeptIds` aus Task 7 — core, kein Import aus `shared/`);
`seven-tv-delete.service.ts` (`startDelete :375`, `DeleteRunInfo :157`, Lifecycle `:202`,
`reportDeleted :580`, `retrySyncReport :460`), Undo-Muster, `delete-progress-section.ts` (T-A),
`restore-progress-section.ts`; Specs.

- [ ] `startDelete` sechster Parameter `tag?`; `DeleteRunInfo`-Felder; Lifecycle-OR; `settleRun`;
      `reportTagRemoval` parallel zu `reportDeleted`; `retryTagRemovalReport`; Signale.
- [ ] Dock-Zeile in `delete-progress-section.ts` + Restore-Hinweis nach Tag-Lauf (13.2) +
      Announcer-Fälle.
- [ ] Specs: ohne Tag unverändert; mit Tag hält `pending` `closed`; Body: `removedIds = doneKeys`,
      `keptIds = deriveTagKeptIds(run.tag, run.result)` (Fall: eine angehakte Zeile `failed` →
      in `keptIds`; alle `uncheckedOwnIds` immer), `snapshot`
      und `activationOperationId` durchgereicht; Retry gleiche `operationId`; Section-Spec: Zeile
      und Hinweis nur bei Tag-Lauf.
- [ ] Gates; ein Commit: `feat(web): report tag removals as a second report on the delete run`.

### Task 10 — Reine Funktionen `partitionTagPlayIn`, `proposeTagRemoval` (`shared/tags/`)

**Modell:** sonnet. **Kontext:** Spec 7.1/4, 7.2/5, 7.2/8, E28, 3.6 des Spec (Namensvettern,
ordinaler Vergleich); Plan 3.7; `seven-tv-set-entries.ts` (`SevenTvSetEntries`),
`import-source.ts` (`ImportRow`, `ImportSource`), `seven-tv-run-engine.ts:76, 244` (`RunResult`,
Zeilenstatus).

- [ ] Beide Dateien nach 3.7; Kopfkommentare: warum aliaslose IDs als „im Set" zählen (3.4), warum
      kein `inSet`-Fallback (E28), warum der Snapshot auch unsichtbare Platzierungen trägt (6.4/3).
- [ ] `tag-play-in.spec.ts`: Partition gegen `aliasesById` **und** `aliaslessIds`; Reihenfolge =
      Eintragsreihenfolge; `ImportRow`-Abbildung (`name = alias`, `imageUrl`); leeres `toAdd`;
      `alreadyInSetIds`; Origin-Form inkl. `alreadyInSetCount`; Eintrag mit `inSet: true` aber
      nicht in `live` → **in `toAdd`** (kein Fallback).
- [ ] `tag-removal.spec.ts`: die fünf Zeilenfälle je ein Fall; `notInSetCount`; Reihenfolge =
      Eintragsreihenfolge; `snapshot` enthält unsichtbare eigene Platzierungen mit Revision;
      `aliases` aus `live`; `ownInLiveIds` = eigene Platzierungen, die `inLive` sind (die
      `keptIds`-Ableitung selbst liegt in core, Task 7).
- [ ] Gates; ein Commit: `feat(web): add the pure tag play-in partition and removal proposal`.

### Task 11 — `TagRemovalConfirmDialog`

**Modell:** sonnet. **Kontext:** Spec 7.2/6, E19, 9.7; Plan 3.7 (Dialog); `delete-confirm-dialog.ts`
(Bausteine, `DeleteConfirmDialogData`, leise Sätze), `name-preview-list.ts`, `dialog-shell.ts`,
`emote-sprite.ts`, `usage-stats-page.html:600-700` (Virtual-Scroll-Muster),
`docs/UI-Designsprache.md` §4.2, §7, §7.2 (Dialog-Aktionszeile), §10.

- [ ] Dialog nach 3.7 mit Design-Pass (fix/zu entscheiden dort benannt); Mobile: wird nie auf
      grobem Zeiger geöffnet, Vorlage muss bei 480 px (feiner Zeiger) nicht überlaufen.
- [ ] Spec (Regel 12): Zusammenfassung folgt den Haken (Umschalten ändert `removeCount`/`keepCount`);
      Knopf bei n = 0 aktiv **und** Zusatzsatz vorhanden; Reihenfolge angehakt → „Nicht
      vorgeschlagen" (dokumentierter Vertrag 7.2/6); Grund je unangehakter Zeile vorhanden (Key
      identifiziert den Fall); Schließwert `{ checkedIds }` nach Umschalten; Escape → `undefined`;
      Rolle/Name; Shared-Set-Warnung sichtbar bei `warning.isOwnSet === false`.
- [ ] Gates; ein Commit: `feat(web): add the tag removal preview dialog`.

### Task 12 — Flows und `TagRunActions`

**Modell:** opus. **Kontext:** Spec 7.1, 7.2 vollständig, 8 (Lauf-Zeilen), E26–E29, 9.7
(`tags.errors.*`, `tags.feedback.*`); Plan 3.7 (Flows, Komponente), T-A 3.1 (`resolveDeleteTarget`,
`readLiveSetAliases`), `mass-delete-panel.ts` (`openConfirm`: Token-Prompt, Host-Lock),
`import-trigger.ts:56-75` (`toImportTarget`), `seven-tv-run-arbiter.ts`, `seven-tv-token.service.ts`,
`docs/UI-Designsprache.md` §4.2 (Sperre ohne Hinweis beim laufenden Lauf), §4.4, §10.

- [ ] `tag-play-in-flow.ts`, `tag-removal-flow.ts`, `tag-run-actions.ts` nach 3.7; Kommentare:
      Reihenfolge 2b vor 2a (Owner-Hint), Registrierung **vor** jeder 7TV-Mutation und vor dem
      Dialog, warum die Live-Lesung blockt, warum n = 0 direkt meldet.
- [ ] Specs `tag-play-in-flow.spec.ts`/`tag-removal-flow.spec.ts`: Set-Einfrieren (Antwort mit
      anderer `emoteSetId` → Abbruch, kein Registrieren); Registrierung vor `readLiveSetAliases`
      und vor jedem Dialog/Import (Aufrufreihenfolge per Spy); 403 der Registrierung →
      `noWriteRight`, kein Lauf, keine Lesung; 503 → `ownershipUnavailable` mit Retry;
      `resolveDeleteTarget blocked` → Banner, kein Registrieren; `readLiveSetAliases blocked` →
      `setReadIncomplete`/`setReadUnavailable`, kein Lauf; Partition leer → direkte Meldung mit
      `[]`, `onFeedback('tags.feedback.allPresent')`; Partition nicht leer → `startImportFlow`
      mit `pinSetId: true` und `tagHook` (Kontext, eingefrorene ID, Signal, beide Callbacks);
      **`onNothingToImport` aus dem Flow** → dieselbe leere Einspiel-Meldung mit **derselben**
      `operationId`, Feedback `allPresent`, Fehler → Banner mit Retry (Codex-Befund 5); Removal
      n = 0 → direkte Meldung mit `keptIds` = alle eigenen `inLive`, Feedback `removedNothing`;
      n > 0 → `startDelete` mit Queue und Kontext (`checkedOwnIds`, `uncheckedOwnIds` korrekt
      aus `ownInLiveIds` und den Haken gebildet); Abbruchprüfung bei Bestätigung (Set gewechselt; Arbiter belegt →
      `noteRefusedStart('delete')`); Retry nach Melde-Fehler sendet **dieselbe** `operationId`;
      Token-Prompt abgebrochen → kein Lauf, Registrierung bleibt (kein weiterer Call).
      `tag-run-actions.spec.ts`: „Ausräumen" nur bei `tag.active`; beide gesperrt bei
      `startLocked`; `enabled false` → nichts gerendert; `completed`/`feedback` durchgereicht.
- [ ] Gates; ein Commit: `feat(web): add the tag play-in and removal flows with shared run actions`.

### Task 13 — Oberflächen: Filterzeile, Tags-Seite (Zustand, Marke, Dock, Guard)

**Modell:** opus. **Kontext:** Spec 9.2 (Knöpfe), 9.4 (Kopfzeile, Marke, Lauf-Fläche), 9.5,
8 („Kanal ohne aktives Set", „Grober Zeiger", „Tag löschen mit Platzierungen"); Plan 3.8; T-B
Task 7/8 (Member), T-A 3.2 (Section-Output), `usage-stats-page.ts:358, 1660-1681` (Dock-Clearance,
`dockVisible`), `action-dock.ts:72`, `usage-stats-page.html:1060-1064, 1073-1080, 1205-1225`,
`tags.routes.ts`, `usage-stats.routes.ts`; `docs/UI-Designsprache.md` §2.5, §4.5, §8.7.

- [ ] Filterzeile: `<app-tag-run-actions>` statt Platzhalter unter dem Gatter; „eingespielt" in
      der Gruppe; Reloads bei `completed`.
- [ ] `tagRunDockHasContent` in `action-dock.ts` (3.8) mit Spec: delete-only `true`,
      restore-only `true`, import-notice-only `true`, alles `false` → `false`; **kein**
      `hasActiveSet`- und kein `deleteConfirmPending`-Feld im Typ (Kompilat erzwingt es).
- [ ] `TagRunActions`: Sperrgrund-Text für einen fremden Lauf ohne Fläche auf der Seite (3.8,
      `tags.errors.otherRunActive`); Spec: `activeRun() === 'undo'` → Grund sichtbar und per
      `aria-describedby` verknüpft; `activeRun() === 'import'` → gesperrt **ohne** Text (§4.2).
- [ ] Tags-Seite nach 3.8 inkl. Dock mit **drei** Sections (Import, Delete mit
      `[hostSelectedSetId]="activeEmoteSetId()"`, Restore), Announcer, Status-Region, Guard,
      Platzierungshinweis im Löschdialog, Marke, Meta-Zeile; `DockClearanceService.release()` bei
      Destroy.
- [ ] Specs (Regel 12): Knöpfe fehlen bei `tagRunsEnabled false`, bei `isCoarse`, ohne aktives
      Set; „Ausräumen" nur bei `active`; Zustandszeile folgt `active`/`activatedAtUtc`;
      Löschdialog-Hinweis nur bei `placedCount > 0`; **Dock-Zustände durch die echte Funktion**
      (Service-Signale gemockt, kein Mock von `tagRunDockHasContent`): nur Lösch-Lauf → Dock
      gemountet und `app-delete-progress-section` vorhanden; nur Restore-Lauf → Dock gemountet
      und `app-restore-progress-section` vorhanden; nur Import-Notiz → gemountet; nichts →
      kein Dock; `BackLink` im Drilldown unberührt; Guard in `TAGS_ROUTES` registriert
      (`tags.routes.spec.ts`).
- [ ] Audit-Harness-Szenarien: `tags-page-active-with-dock` (Lauf-Record gemockt im Service? —
      der Harness mockt nur Routen; stattdessen Szenario mit Platzierungs-Marken und Zustandszeile,
      `requiresFinePointer`), `tag-removal-dialog` (`afterLoad` öffnet den Dialog gegen gemockte
      7TV-`setRead`), `usage-filter-with-tag-run-actions`. Gates wie T-B.
- [ ] Gates; zwei Commits: `feat(web): offer tag play-in and removal from the usage filter row`
      und `feat(web): show tag activation, placements and the run dock on the tags page`.

### Task 14 — E2E Szenarien 2–8

**Modell:** sonnet. **Kontext:** Spec 11 E2E 2–8; `web/e2e/emote-import.e2e.spec.ts:640-720, 820-900, 2200-2260`
(Body-Capture, `mockSevenTvGql`-Handler mit `setRead`/`addEmote`/`removeEmote`), `mocks.ts`
(`mockSevenTvGql :~1578`, `mockActiveEmoteSet :1265`, `installLiveStub :1426`/`emitLive`,
`mockChannelPermissions :646`, `mockSyncImported`, `mockSyncDeletedInSet`), T-B Task 9 (Tag-Mocks),
CLAUDE.md „Tests" (`page.clock`).

- [ ] Mocks: `mockTagOperations(page, channel, tagId, { status })` mit Body-Capture;
      `mockTagPlacements`, `mockTagRemoval` mit Capture und konfigurierbarer Antwort;
      `mockChannelPermissions` Default `tagRunsEnabled: true`.
- [ ] Szenarien 2–8 wörtlich nach Spec 11 (Assertions auf `sync-imported`-Body `sourceKind: 'tag'`,
      `POST …/placements` mit genau einer ID und UUID-Form; leere Liste; Vorschau mit Datum und
      Grund; `removeEmote` und `sync-deleted`; `POST …/placements/removed` mit `snapshot`
      (ID + Revision), `removedIds`, `keptIds: []`; Protokoll-Download angeboten; Knopf
      „Ausräumen" verschwindet nach Reload-Mock mit `active: false`; Gegenbeispiel 2 ohne
      `removeEmote`; `setRead` unvollständig blockt beides ohne Mutation; 403 der Registrierung →
      Banner, kein `setRead`, kein `addEmote`, kein Dialog; Set-Wechsel via `emitLive('channel.synced')`
      und umgestelltem `active-set`-Mock → kein `addEmote`, Notiz). Jedes Szenario zählt die
      7TV-Mutationen über den GQL-Handler.
- [ ] Gates: `npm --prefix web run e2e` komplett grün ohne Api; ein Commit:
      `test(web): cover tag play-in and removal runs end to end`.

### Task 15 — Dokumentation, Konfiguration, Rollout-Unterlagen

**Modell:** sonnet. **Kontext:** Spec 10, 12.4, 12.5, 13.1–13.4; `docs/DECISIONS.md:1-15`,
`docs/Operations.md` („Data retention" als Muster für einen Konfigurationsabschnitt, `.env`-Hinweise),
`.env.example:34-36`, `docker-compose.yml:148`, `docker-compose.prod.yml:151`,
`docs/Architectur.md` (Tabelle, Abschnitt zu Sync-Deviationen `:204`), `docs/UI-Designsprache.md`
§7, §8.7.

- [ ] `.env.example` `TAGS_RUNS_ENABLED=false` mit Kommentar (Freigabe erst, wenn die
      Bereitschaftsprüfung aus `docs/Operations.md` leer ist); beide Compose-Dateien
      `Tags__RunsEnabled=${TAGS_RUNS_ENABLED:-false}` am **api**-Dienst.
- [ ] `docs/Operations.md`: Abschnitt „Emote tags: enabling play-in and removal runs" —
      Reihenfolge Migration → beide Images → **Bereitschaftsprüfung** → Flag → Stack-Update.
      Die Prüfung (Codex-Befund 9; es gibt keine Resync-Zusammenfassung im Log, Plan 0) ist eine
      `psql`-Abfrage über `"Channels"`, die der Betreiber über den Tunnel ausführt — **zwei
      Listen** (Spaltennamen aus `Channel.cs`: `IsBotActive`, `TwitchChannelId`, `ActiveEmoteSetId`
      (leer = kein Set), `LastSyncedAtUtc`, `LastSyncAttemptAtUtc`, `LastSyncFailureReason`;
      Vokabular `SevenTvSyncFailureReasons`: dauerhaft `no_seventv_account`, `no_active_emote_set`;
      vorübergehend `seventv_unavailable`, `seventv_response_unusable`):
      **Liste A — muss leer sein:** aktive Kanäle (`"IsBotActive"`), die **synchronisierbar** sind
      (`"TwitchChannelId" IS NOT NULL`, `"ActiveEmoteSetId" <> ''`, `"LastSyncFailureReason"`
      `NULL` oder vorübergehend) und deren `"LastSyncedAtUtc"` `NULL` ist oder **vor** dem
      Startzeitpunkt des neuen Worker-Containers liegt. Ein Kanal mit vorübergehendem Fehler oder
      mit greifendem Wipe-Guard (kein `ApplyAndSaveAsync`, alter `LastSyncedAtUtc`) bleibt hier
      stehen — der Betreiber wartet den nächsten Takt ab bzw. sieht sich das Worker-Log dieses
      Kanals an; solange Liste A nicht leer ist, bleibt das Flag aus. **Liste B — nur zur
      Kenntnis:** aktive Kanäle, die **nicht** synchronisierbar sind (dauerhafter Fehlergrund,
      leeres `ActiveEmoteSetId`, fehlende Twitch-ID, Einträge aus `Channels:ExcludedChannelIds`):
      sie können keine Tag-Läufe tragen (ohne aktives Set fehlen Einspielen/Ausräumen, Spec 8) und
      blockieren die Freigabe nicht; der Betreiber prüft nur, dass die Liste den erwarteten
      Dauerfällen entspricht. Den Startzeitpunkt liest der Betreiber aus Portainer bzw.
      `docker inspect` des Worker-Containers (`State.StartedAt`) und setzt ihn als Parameter ein;
      beide Abfragen stehen als fertige Befehle mit Platzhaltern im Abschnitt, mit dem Satz, warum
      nur dieser Nachweis zählt (die Boot-Recovery gibt ihr Gate auch nach Fehlern frei; ein Kanal,
      dessen letzter Vollsync noch vom alten Worker stammt, hätte für seit dem Deploy archivierte
      Zeilen keine Beobachtungen). Auf einem gesunden System ist Liste A nach einem Takt
      (`SevenTv:ResyncIntervalSeconds`, Default 60) leer. Hinweis auf Uhr-Annahme (12.4) und
      30-min-Fenster als Konstante; Satz in „Data retention" zu den sechs Tabellen; Nachläufer
      „Loader für getrackte Sets auf die #220-Route" als Satz.
- [ ] `docs/Architectur.md`: Sync-Absatz um Beobachtungen/Eintrittsstempel ergänzen; Tabelle
      (Task 6 hat die Zeile).
- [ ] `docs/UI-Designsprache.md`: §7 kurzer Unterabschnitt „Preview-as-confirmation (tag
      removal)" — angehakte vor nicht vorgeschlagenen, Haken beidseitig, Knopf immer aktiv, n = 0
      Satz; §8.7 Tags-Seite trägt den Laufzustand im eigenen Dock; §4.2 Reference ergänzt den
      Dialog als Ausführung.
- [ ] `docs/DECISIONS.md`-Eintrag (englisch) `### <Commit-Datum> — Tag play-in and removal: placements, activations, pre-registered operations, read-time validity against sync leave observations (#201 T-C)`:
      (1) Sicherheitsmodell 0a; (2) Platzierungen explizit gemeldet (E5), Operations-ID vorab
      (E27), Revision = `OperationId` (5.3); (3) Lese-Zeit-Regel statt Sync-Löschung (E8/E33
      rev. 4), Glaubwürdigkeit E34 mit 30 min als Konstante; (4) Aktivierung, Übertragung, Sweep,
      Invariante (E26 rev. 3, E7 rev. 2, 13.4/2); (5) Kanalsperre und Sperrreihenfolge (5.5/6,
      13.4/3); (6) Rechte: Kanalfilter + Besitzleiter (E9 rev. 3), geteilte Hilfe; (7) Import-Flow
      `pinSetId` + Wächter + No-op-Hook (E29, E26), Foreign-Permit-Kosten hingenommen mit
      Nachläufer #220-Route (Abweichung 2); (8) dritte/zweite Meldung am Record (E14); (9) Flag
      in `permissions` (13.4/1) und Rollout 12.5 mit der ausführbaren Bereitschaftsprüfung statt
      einer Logzeile; (10) Restrisiken R1–R4 und 13.2; (11) Worker-Verhalten ohne
      Quelltextänderung (E15 rev. 2); (12) FK Platzierung → Eintrag (Festlegung 5.3 gekippt) und
      die Kanalsperre bei jeder Tag-Mutation; (13) Beobachtungen als atomarer `GREATEST`-Upsert in
      einer expliziten Transaktion je Speicherversuch, unabhängig vom Archivübergang, mit
      Nachbetrachtung nach dem Fenster — damit sind **drei Spec-Sätze überholt** und werden im
      Eintrag als solche benannt: 3.1 („der Sync nimmt keine explizite Transaktion"), 12.4 („je
      archivierter Zeile ein Upsert … sonst nichts" — jetzt ein Upsert je `PulledId`, eine
      Beobachtungslesung je REST-Takt für die Nachbetrachtung, eine Transaktion je Versuch) und
      5.3 („kein FK auf Entry"); (14) Dock-Vertrag der Tags-Seite (`tagRunDockHasContent`, drei
      Sections, ohne `deleteConfirmPending`).
- [ ] **Spec-Nachtrag (eine Zeile je Punkt)** in `docs/superpowers/specs/2026-10-04-emote-tags-design.md`
      unter 13.0 bzw. als Fußnote an 3.1, 5.3 und 12.4: „Vom Plan T-C am <Datum> überholt: FK
      Platzierung → Eintrag (5.3); explizite Transaktion je Speicherversuch und Upsert je
      `PulledId` plus Nachbetrachtung (3.1, 12.4) — Begründung in docs/DECISIONS.md." Kein
      Umschreiben des Spec-Texts; der Nachtrag hält Spec und Plan lesbar nebeneinander.
- [ ] Ein Commit: `docs: record tag runs, placements and the leave observation rule`.

### Task 16 — Gates, Live-Verifikation, Migration und Rollout vorbereiten, Zweitmeinung, PR

**Modell:** Orchestrator (Opus). **Kontext:** CLAUDE.md „Fertig heißt", „Prod-Migration", Regel
16; Spec 12.1 T-C, 12.4, 12.5, 12.6; Memory „Codex-Review-Fallen", „coverage-local.mjs",
„Prod-Migration handover", „7TV-Verhalten mit Code-Folgen".

- [ ] Gates vollständig: `dotnet test EmotePurge.slnx` (Docker), `npm --prefix web test -- --watch=false`,
      `npm --prefix web run e2e` (ohne Api auf `:5151`), `dotnet format … --verify-no-changes`,
      `npm --prefix web run format:check`, `npm --prefix web run lint`; danach
      `node scripts/coverage-local.mjs` (nach letztem Commit; Näherung im PR nennen).
- [ ] Live-Verifikation (Regel 16) lokal gegen Postgres/Redis, Dev-Api **und** lokalen Worker
      (Messfenster ist vorbei), echter Twitch-Login, **eigener** Testkanal mit eigenem 7TV-Set
      (nie fremd), `Tags:RunsEnabled=true` lokal: (a) Tag mit 3 Emotes, davon 1 schon im Set →
      Einspielen → Dialog „2 hinzugefügt, 1 schon im Set" → Lauf → `EmoteTagOperations`
      (registriert, `AppliedAtUtc`), `EmoteTagPlacements` (2 Zeilen mit `OperationId`),
      `EmoteTagActivation`; (b) Vollsync-Zyklus abwarten: Worker-Log zeigt den stale-Archiv-Fall
      (`REST-Resync archiviert …`) **ohne** neue `EmoteSetLeaveObservation`-Zeile für frische
      Stempel; (c) ein Emote von Hand bei 7TV entfernen → EventAPI oder Vollsync → Beobachtung
      geschrieben → `GET …/entries` zeigt `placedByThisTag: false`; (d) Ausräumen → Vorschau (1
      angehakt mit Datum, 1 „war schon vorher im Set") → Lauf → Platzierungen leer, Aktivierung
      weg, Protokoll-Download, „Wiederherstellen" → Emotes zurück ohne Platzierung; (e) No-op-
      Einspielen eines vollständig vorhandenen Tags → Aktivierung ohne Lauf; (f) Set-Wechsel
      (zweites eigenes Set aktivieren) zwischen Klick und Bestätigung → Notiz, kein ADD. Befunde
      im PR-Text.
- [ ] Prod-Migration vorbereiten (nicht ausführen): Tunnel, `migrations list` (erwartet genau
      `AddEmoteTagPlacements (Pending)`), `database update`, `list`; Platzhalter `<PROD-PW>`.
- [ ] Rollout-Checkliste für den Betreiber (12.5): 1. Migration; 2. Api- **und** Worker-Image
      zusammen (Portainer), `TAGS_RUNS_ENABLED` noch `false`; 3. **Bereitschaftsprüfung** aus
      `docs/Operations.md` (Task 15): Startzeitpunkt des neuen Worker-Containers ablesen, beide
      `psql`-Abfragen über den Tunnel mit diesem Zeitpunkt ausführen — erst wenn **Liste A** leer
      ist (jeder synchronisierbare aktive Kanal seit dem Start voll synchronisiert), weiter;
      liefert sie Zeilen, einen Takt warten bzw. die genannten Kanäle im Worker-Log ansehen;
      Liste B (nicht synchronisierbare Kanäle) nur gegen die Erwartung prüfen; 4. `TAGS_RUNS_ENABLED=true` in der `.env`, Stack-Update (Api); 5. Probe:
      `GET /api/channels/<eigener>/permissions` zeigt `tagRunsEnabled: true`. Der Umsetzer
      bereitet die Befehle vor (Platzhalter für Passwort und Zeitpunkt), ausgeführt werden sie vom
      Betreiber.
- [ ] Codex Sol (Regel 22) aus dem Worktree, `--scope branch --base origin/main` in **einem**
      String, Diff-Gegenprobe vorher; Findings unverändert vorlegen; Widerspruch Opus ↔ Sol →
      Fable mit den strittigen Findings. (12.6 nennt zusätzlich den adversarialen Review **vor**
      dem Plan — der ist Sache des Orchestrators vor Start der Umsetzung, nicht dieses Tasks.)
- [ ] PR gegen `main`: Umfang, Gates, Coverage-Näherung, Live-Befunde (a)–(f), Codex, Deploy-
      Checkliste, Abweichungen aus Plan 0, Restrisiken-Verweis (Spec 13.1).

**Abnahme:** Alle Gates grün in einem Lauf; Live-Verifikation (a)–(f) dokumentiert; PR offen;
Merge beim Nutzer; Flag in Prod bleibt `false` bis Schritt 3 der Checkliste.

---

## 5. Spec-Abdeckung

| Spec-Stelle | Task |
|---|---|
| 5.3 `EmoteTagPlacement`, 5.4 Aktivierung/Operation/Beobachtung, `Emote.LastEnteredSetAtUtc` | 1 |
| 5.5 Regel 2 (Platzierung nur mit Eintrag — jetzt per FK), Regel 10 (Upsert überschreibt); E17 | 1, 4, 5 |
| 5.3 Festlegung „kein FK auf Entry" (gekippt, Plan 0) | 1 |
| 3.1/E34: Beobachtung unabhängig vom Archivübergang, Nachbetrachtung nach dem Fenster, atomarer Upsert, explizite Transaktion | 2 |
| 7.1/5–6, E26: erfolgreicher No-op auch nach der letzten Duplikatprüfung (`onNothingToImport`) | 8, 12 |
| 9.5 Dock der Tags-Seite: eigener Vertrag, Restore-Section (E13, R1–R4) | 13 |
| 12.5/3 Freigabe-Nachweis (ausführbare Prüfung statt Logzeile) | 15, 16 |
| 5.5 Regel 3 (ältester Halter), Regel 4 (Übertragung, Sweep, Invariante), 13.4/2 | 3, 5 |
| 5.5 Regel 5 (Beobachtung statt Invalidierung, Lese-Zeit-Regel, Uhr), E34, 13.4/4 | 2, 3 |
| 5.5 Regel 6 (Kanalsperre, Sperrreihenfolge, Retry-Regel), 13.4/3 | 4, 5, 6 |
| 5.5 Regel 9 (Set-Wechsel ändert keine Tabelle) | 3 (Fremd-Set-Lesung), 16 (f) |
| 5.5 Gegenbeispiele 1–3, 5, 7 | 5 |
| 5.5 Gegenbeispiele 4, 6, 8, 9 | 4 (8 zusätzlich 2) |
| 6.1 Gruppe Registrieren/Melden, Besitzleiter im Handler, Reihenfolge Kanalfilter → Leiter | 6 |
| 6.2 Platzierungs-/Aktivierungsfelder, `activationOperationId`, Regel 10 | 3 |
| 6.4 Registrieren, Einspielen, Ausräumen, Audit, kein Resync | 4, 5, 6 |
| 6.5 vier Codes (drei aus der Spec + `tag_operation_kind_invalid`), `"tag"`-Vokabel (drei Backend-Stellen) | 6, 7 |
| 6.6 Substitution/echte Besitzprüfung in Tests (Abweichung 1) | 6 |
| 7.1 Schritte 1–9, Fehlerpfade | 7, 8, 10, 12 |
| 7.2 Schritte 1–9, Fehlerpfade | 9, 10, 11, 12 |
| 8 Lauf-Zeilen (Set-Wechsel, Platzierungen, Mods ohne Schreibrecht, zwei Mods, Duplikatzelle, Tab geschlossen, Meldung nach Verlassen, …) | 4, 5, 12, 14 |
| 9.2 Knöpfe, 9.4 Kopfzeile/Marke/Lauf-Fläche, 9.5 Dock der Tags-Seite + Guard | 13 |
| 9.7 T-C-Familien (inkl. Abweichung 3) | 7–13 |
| 11 Backend T-C (Service, Sync, EmoteService, Lese-Zeit-Regel), Api, Vitest, E2E 2–8 | 2–14 |
| 12.1 T-C, 12.3/2, 2a, 3 | 7, 8, 9 |
| 12.4 Worker-Verhalten, Uhr-Annahme | 2, 15 |
| 12.5 Rollout 1–3, Flag (13.4/1) | 6, 15, 16 |
| 12.6 Codex vor Merge | 16 |
| 13.2 Restore ohne Platzierung, Hinweistext | 9 |
| 13.1 R1–R4 | 15 (DECISIONS), 16 (PR) |

## 6. Betreiberentscheidungen und offene Punkte

**Entschieden (Betreiber, 2026-10-04):** (1) vierter Fehlercode `tag_operation_kind_invalid`
(3.3, Task 6/7); (2) Foreign-Permit-Kosten beim Einspielen hingenommen, Nachläufer „Loader für
getrackte Sets auf die #220-Route" in DECISIONS/Operations (Plan 0, Task 15); (3) T-A: der
Restore-Einstieg wandert in die `DeleteProgressSection` (dort entschieden).

**Durch die adversariale Zweitmeinung (Codex Sol) geändert — zur Kenntnis, kippbar nur mit
Ersatzlösung:** FK Platzierung → Eintrag (Festlegung 5.3 der Spec gekippt, 3.1); explizite
Transaktion je Speicherversuch an den drei Beobachtungsstellen (Spec 3.1 beschreibt den Ist-Zustand
ohne; 3.4 begründet, warum die Deadlock-Analyse hält); Beobachtung unabhängig vom
Archivübergang mit Nachbetrachtung (3.4); `tagRunDockHasContent` und drei Sections (3.8);
No-op-Hook im Import-Flow (3.6).

**Entschieden (Betreiber, 2026-10-04)** — alle drei Empfehlungen bestätigt (die Pläne sind darauf geschrieben):

1. **Beobachtung für jede ID in `PulledIds`, auch ohne Zeile, und bei `MarkDeletedInSetAsync`
   für jede gefundene Zeile** (3.4): fail-safe-Richtung, kostet je Fall eine Upsert-Zeile.
2. **R1 bleibt wie getragen, mit Nachbetrachtung „einmal je Eintrittsperiode"** (3.4/b2): die
   Alternative — bei jedem REST-Durchlauf stempeln — würde Gegenbeispiel 8 brechen; die gewählte
   Regel lässt eine echte Entfernung innerhalb des Fensters bei ausgefallener EventAPI erst
   **nach** dem Fenster sichtbar werden.
3. **T-A: Section wird im Panel gemountet** (T-A §6).

**Offen (echt):**

1. **Flag in `permissions`** statt eigenem Config-Endpoint (13.4/1) — Planentscheidung,
   kippbar ohne Folgen für die anderen Tasks (nur Task 6 und 7).
2. **Spec-Nachtrag** zu 3.1, 5.3 und 12.4 (Task 15) — der Betreiber entscheidet, ob er den
   Nachtrag selbst formuliert oder den Satz aus dem Plan übernimmt.
