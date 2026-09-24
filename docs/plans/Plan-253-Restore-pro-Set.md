# Plan #253 — Restore pro Set: Restore ohne Kanalseite, set-zentrische Meldungen, Aufhebung der Replace-Sperre

Erstellt am 2026-09-24 gegen `feat/253-restore-per-set` = `0cf193eb`; **Fassung 2 vom 2026-09-25**
steht auf `feat/emote-sets-200` = `dd1e6320` (PR #266 hat `main` mit dem Kontaktformular in den
Epic-Branch gemergt; Spec-Commits `498a57b9`/`89bcbb20`/`6ec22dc5`, erste Plan-Fassung `b5f4098f`).
Quellen:
[die Spec](../superpowers/specs/2026-09-24-restore-pro-set-253-design.md) — zweite Fassung, vom
Betreiber freigegeben; Abschnitte werden als „Spec N" zitiert, Entscheidungen als E1–E24, Fallen
als F1–F17, Akzeptanzkriterien als AK n —, Issue #253 (Hauptissue), #224 (wird geschlossen), #256
(Punkt 3 erledigt sich), das Epic #200, [Plan-230](Plan-230-Namenskonflikte.md) (T6 „Entfallen",
T7b, Abschnitte 7 und 8) und [Plan-200](Plan-200-Emote-Sets.md) als Formvorbilder, `CLAUDE.md`
(Regeln 1–22, Tests, Gates, Sprache), `web/.claude/CLAUDE.md`, `docs/UI-Designsprache.md` (§7.3,
Dock-Abschnitte), und der Code auf `0cf193eb` — jede Datei aus Spec 16 ist gelesen, nicht vermutet.

**Die Spec ist die einzige Vertragsquelle. Dieser Plan trägt Schnitt, Reihenfolge, Nahtstellen,
Testerwartung je Task, Modellwahl und Gates.** Kein Wire-Format, kein Shape, kein Fehlercode und
keine Zustandsmatrix steht hier noch einmal — an ihrer Stelle steht ein Verweis der Form „Vertrag:
Spec 5.3, E9". Der Plan enthält keinen Code: Namen stehen nur, wo sie ein Vertrag zwischen zwei
Tasks sind, und die Spec sie ausdrücklich der Plansache überlässt („Die Namen sind Plansache, die
Felder Vertrag", Spec 5.3). Findet ein Task eine Abweichung zwischen Spec und Code, gilt die Spec,
und der Fund wird im Task-Bericht gemeldet, nicht still aufgelöst. Was der Plan über die Spec hinaus
festlegt, steht gesammelt in Abschnitt 6 — als **Festlegung des Plans mit Betreiber-Veto**, nicht
als Interpretation im Fließtext.

**Fassung 2 (2026-09-25).** Die erste Fassung (`b5f4098f`) ist um die fünf Befunde des adversarialen
Codex-Reviews (gpt-6-sol) überarbeitet, alle vom Orchestrator angenommen (Abschnitt 9): das
400-Fenster zwischen Altform-Umbau und Frontend-Umstellung entfällt (T3 folgt jetzt T7), T7 nimmt
die direkten Aufrufer der geänderten Dienst-Signaturen mit und bekommt eine volle Typprüfung, T9
registriert die neue Komponente in beiden Host-Seiten, T5 migriert alle typisierten
Restore-Fixtures, und T5/T8 laufen seriell. Dazu eine grep-Inventur aller Aufrufer je geänderter
Signatur (0.6), die über Codex' Stichproben hinaus weitere Stellen fand und in die Dateilisten
eingegangen ist; Abschnitt 6 ist von „offen" auf „entschieden (Orchestrator, Betreiber-Veto
möglich)" gestellt; die zwei reinen Spec-Wortlautfehler daraus sind in `6ec22dc5` in der Spec
korrigiert.

---

## 0. Ausgangslage

### 0.1 Was feststeht

- **Die Spec ist zweimal abgenommen:** sieben Betreiber-Entscheidungen (Spec 13) und sechs
  Codex-Befunde H1–H4, M5, M6 (Spec 14) sind eingearbeitet. Spec 15 nennt drei Stellen, an denen
  ein Betreiber-Veto die Spec an genau einer Stelle änderte (E24, E22, F16) — keine davon blockiert
  einen Task; die drei Tasks, die sie tragen (T3, T9, T1), sind so geschnitten, dass ein Veto nur
  den einen Task trifft.
- **Zwei Endpunkte, kein Schema, keine Migration** (Spec 17). Backend und Frontend gehen
  **zusammen** zurück; der Rückweg ist ein Revert des PR.
- **Vier DECISIONS-Einträge** (Spec 12.1), englisch, im jeweils vertragsändernden Commit (Regel 3).
  Die Zuordnung steht in 0.4 und je Task.
- **Der PR geht gegen `feat/emote-sets-200`**, nicht gegen `main` (Plan-200 0.4); Zweitmeinung
  `/codex:review --model gpt-6-sol --scope branch --base feat/emote-sets-200` einmal je Branch
  (Regel 22, T11). Deploy hinter dem 2026-10-08 zusammen mit dem Rest von #200 (K7).
- **Der Halloween-Wechsel am 01.10.** macht ein Purge-Protokoll mit inzwischen nicht-aktivem Set
  zum Normalfall (E1) — der Plan ordnet deshalb den Datei-Weg (T5) **vor** die Einstiegs-Erweiterung
  (T9): sollte die Runde vor dem 01.10. abbrechen müssen, ist der Kern (Meldungen, Zielprüfung,
  Restore in jedes Set) schon im Branch.

### 0.2 Was der Plan beim Nachprüfen am Code gefunden hat

| Befund | Beleg | Folge |
|---|---|---|
| **`ApiErrorCodes.EmoteIdsInvalid` hat einen zweiten Nutzer** (`VoteSessionEndpoints`, `CreateVoteSessionResult.EmoteIdsInvalid`). Nur die Stufe 2 der Leiter `ValidateSyncBookkeepingBody` fällt, nicht der Code. | `ApiErrorCodes.cs`, `VoteSessionService.cs` | T3 entfernt genau `EmoteSetIdEmpty` (E14), sonst nichts |
| **`editable` braucht zwei Durchläufe im Endpunkt.** `GET /me/emote-set-targets` baut je Account sofort das DTO (`ResolveEmoteSetTargetAccountAsync`); `editable` eines Sets hängt aber an der Menge der 7TV-IDs **aller** lesbaren Accounts der Antwort (Spec 5.8). | `SevenTvEndpoints.cs`, Handler `/emote-set-targets` | T1 sammelt erst die Accounts, rechnet dann `editable` über die fertige Menge — die Sortierung (eigener Account zuerst, Editoren ordinal) bleibt |
| **Die Besitzprüfung kennt die Regel schon als private Methode** (`OwnershipEvidence.MatchAgainstAllKnownAccounts`): „Besitzer-ID ∈ IDs der gelesenen Accounts". Sie steht in Infrastructure; der Endpunkt (Api) darf sie so nicht rufen. | `ImportTargetOwnershipService.cs` | T1 zieht sie als reine Funktion nach Core; beide Aufrufer rufen dieselbe (AK 30) |
| **`EmoteService` wird in Tests 22-mal direkt konstruiert** (`new EmoteService(db, NullLogger…)`); ein echter `ExcludedChannelFilter` aus Konfiguration ist in `ExcludedChannelFilterTests` vorgeführt. | `EmoteServiceTests.cs`, `ExcludedChannelFilterTests.cs` | T2 führt einen Fixture-Helfer ein (F10), alle 22 Stellen wandern in **einem** Zug |
| **Das Muster für Stufe 7 existiert wortgleich:** `POST /channels/{name}/resync` erwirbt den Cooldown vor `TriggerResyncAsync` und gibt ihn bei `NotFound`/`NotActive` frei. `ApiFactory` substituiert `IChannelResyncCooldown` und `IChannelService` bereits; `IEmoteService` ebenso. | `ChannelEndpoints.cs`, `ApiFactory.cs` | T2 kopiert das Muster in eine private Hilfsmethode, die beide neuen Routen **und** (T3) die Altform rufen; ob `IRedisPublisher` ein Substitut braucht, prüft T2 am `ApiFactory` |
| **`SyncReportState` lebt im Delete-Dienst** und wird von Restore und Import importiert; die Dreiwertigkeit (`succeeded`/`partial`/`failed`) wird heute in jedem Dienst einzeln aus `archivedCount >= n` gerechnet. | `seven-tv-delete.service.ts`, `seven-tv-restore.service.ts`, `seven-tv-import.service.ts` | T4 zieht Zustand **und** Grund in eine pure Klassifikation (0.5), die alle drei Dienste in T7 konsumieren — eine Wahrheit für AK 15 |
| **Der Panel-Restore hat eine eigene Kette**, nicht `startRestoreFlow`: `MassDeletePanel.openRestoreConfirm` → private `openRestoreConfirmDialog` → `restoreService.startRestore`. Der Kommentar von `restore-flow.ts` sagt, dass es „aus dem Panel extrahiert" wurde — die Kopie im Panel blieb. | `mass-delete-panel.ts`, `restore-flow.ts` | T6 darf beide Ketten zusammenführen, muss aber nur eins: beide Einstiege bauen ihr Ziel aus der Vorprüfung (Spec 6.3) |
| **Der Ziel-Picker lädt je Öffnen frisch** (`rxResource` über `listEmoteSetTargets()`), sein Reload ebenso. Spec 6.2 will, dass er die 60-s-Kopie liest und nur `reload` sie umgeht. | `import-target-dialog.ts` | T4 gibt dem Client eine gecachte Variante und stellt den Picker um; Verhaltensänderung: der Picker kann bis zu 60 s alte Listen zeigen (Spec-Vorgabe, Abschnitt 6) |
| **`DockOutcomeAnnouncer` liest die Dienste direkt** (`restoreService`, `importService`), keine Inputs. Der Umzug der Restore-Blöcke aus dem Panel ändert am gesprochenen Zwilling nichts. | `dock-outcome-announcer.ts` | T9 zieht nur die sichtbaren Blöcke um; der Announcer bleibt, wo er ist |
| **Die transiente Notiz des Import-Dienstes** (`duplicateNoticePending`, 4000 ms, `showDuplicateNotice`) ist der Ort für den Drift-Abbruch; `ImportProgressSection` und `dockVisible()` (`importNoticePending`) hängen daran. | `seven-tv-import.service.ts`, `import-progress-section.ts`, `usage-stats-page.ts` | T8 hängt den Vorprüfungs-Abbruch an dieselbe Mechanik (Spec 4.5 Punkt 17), damit der Dock sichtbar wird, obwohl kein Lauf existiert |
| **`mockWorkspace`, `gotoUsageStats`, `openFileImportDialog` sind lokale Helfer** in `emote-import.e2e.spec.ts`; `mockEmoteSetTargets` liegt in `e2e/support/mocks.ts` und kennt weder `editable` noch IDs. | `web/e2e/…` | T4 erweitert den Mock **mit Default `editable: true`**, damit kein Bestandstest still rot wird (F9); T10 nutzt die lokalen Helfer |
| **Der Dev-Proxy schlägt die Vorprüfung durch:** ein E2E-Test ohne `mockEmoteSetTargets` sieht ab T8 nur das Fehlerbanner. | F9 | Jeder Task, der einen Bestandstest bricht, repariert ihn im selben Commit (0.4) — keine rote Zwischenstufe bis T10 |

### 0.3 Modelle je Task

`sonnet` für klar spezifizierte Implementierung und Tests; `opus` an vier Stellen mit Begründung
(T2 Service und Routen, T5 Datei-Schritt, T7 die drei Dienste, T10 E2E über den ganzen Weg);
`haiku` nur für die mechanischen Gate-Läufe in T11. T12 ist ein Handgriff des Betreibers mit einem
`sonnet`-Subagent als Protokollant. Die Begründung steht je Task.

### 0.4 Branch, Worktrees, Commits

- **Branch `feat/253-restore-per-set`**, seit dem 2026-09-25 auf `feat/emote-sets-200` =
  `dd1e6320` (PR #266: `main` mit dem Kontaktformular ist im Epic-Branch); der PR geht gegen
  `feat/emote-sets-200`.
- **Zwei Lanes in Welle 1** (Abschnitt 5): Backend (T1 → T2, sequenziell in einem Worktree) und
  Frontend (T4, eigener Worktree); Welle 3 wieder zwei Lanes (T3 Backend ∥ T6 Frontend). Alles
  andere sequenziell im Haupt-Arbeitsverzeichnis des Branchs — **T5 und T8 seriell**, weil beide
  `web/e2e/emote-import.e2e.spec.ts` ändern und beide einen DECISIONS-Eintrag an den Anfang von
  `docs/DECISIONS.md` setzen (Codex-Befund 5). Worktree-Fallen aus Plan-200 0.4 und dem
  Plan-230-Ledger gelten: aus dem Worktree nur bauen, nie `docker compose up`; E2E je Worktree auf
  eigenem Port; eine Worktree-Api verwirft das lokale Cookie — Live-Läufe (T12) aus dem
  Haupt-Checkout.
- **Ein Commit je Task**, Conventional Commits, englisch, ohne `#`-Referenzen in Commit-Metadaten;
  Vorschläge stehen je Task. Ein Task, der einen Bestandstest bricht (E2E-Routen, Parser-Fälle,
  Fixtures), repariert ihn **im selben Commit** — es gibt keine rote Zwischenstufe, die auf T10
  wartet.
- **Aufrufer-Regel (Codex-Befunde 2 und 4):** die Dateiliste jedes Tasks enthält jeden Aufrufer,
  jede typisierte Fixture und jeden Host-Import der Signaturen, die er ändert — die Inventur steht
  in 0.6 und ist Teil des Task-Briefs. Jeder Frontend-Task fährt zusätzlich zu seinen gefilterten
  Specs `npm --prefix web run build` als Typprüfung über Templates und Specs hinweg: `npx vitest`
  prüft keine Typen, `ng test`/`ng build` schon (Memory). Ein Task, dessen Build rot ist, ist nicht
  fertig, auch wenn seine Specs grün sind.
- **Regel 3 — die vier Einträge (Spec 12.1):** Eintrag 3 („Wer melden darf …") in **T1**, weil
  dort die gemeinsame Regel entsteht; Eintrag 2 („… melden set-zentrisch — Meldung + Resync") in
  **T2**, dem Commit mit den Routen — **ohne** den Altform-Absatz; den hängt **T3** in seinem
  eigenen Commit an denselben Eintrag an, weil die Altform erst dort ihren Vertrag ändert. So
  stimmt der Eintrag zu jedem Commit-Stand (Codex-Befund 1); Eintrag 1 („Die Datei bestimmt das Ziel") in
  **T5**; Eintrag 4 („Die Replace-Sperre fällt") in **T8**. Jeder Eintrag nennt in `Betrifft:` auch
  die Dateien der Tasks, die denselben Vertrag konsumieren — ein Eintrag je Vertrag, nicht je Task.
  Die Hauptsession liest jeden DECISIONS-Diff selbst (Memory: „Grüne Suiten sind keine
  Fertigmeldung").
- **Kein 400-Fenster (Codex-Befund 1):** die kanalgebundene set-scoped Form bleibt stehen, bis T7
  ihre drei Frontend-Aufrufer umgestellt hat; erst T3 (Welle 3) entfernt sie. Eine Meldung folgt
  auf die 7TV-Mutation — ein Live-Lauf aus dem Branch, der in ein 400 liefe, verlöre Buchung und
  Audit, und das darf es auch Branch-intern nicht geben. Die erste Fassung hatte T3 direkt hinter
  T2 gesetzt und das Fenster in Kauf genommen; das ist zurückgenommen.

### 0.5 Vom Plan benannte Bausteine (Namen als Vertrag zwischen Tasks)

Die Spec überlässt diese Namen dem Plan; ab hier gelten sie in jedem Task. Was ein Name trägt,
steht in der Spec, nicht hier.

| Baustein | Name | Ort | Spec | erzeugt in | konsumiert in |
|---|---|---|---|---|---|
| Reine Regel „ist das Set für diese Accounts bearbeitbar" | `EmoteSetEditability` (statische Funktion in Core) | `src/EmotePurge.Core/Services/` | 5.8, E19, F16 | T1 | T1 (Endpunkt, Besitzprüfung), T2 (nur transitiv) |
| Body der zwei Routen | `SyncInSetRequest` | `SevenTvEndpoints.cs` | 5.1 | T2 | T3 (Doku), T10 (Mock) |
| Antwort-Records (Wire) | `SyncDeletedInSetResponse`, `SyncRestoredInSetResponse`, `UnresolvedChannelResponse` | `SevenTvEndpoints.cs` | 5.3 | T2 | T4 (Modell) |
| Service-DTOs (Core) | `SyncDeletedInSetResultDto`, `SyncRestoredInSetResultDto`, `SyncInSetChannelResultDto`, `UnresolvedChannelDto`, Konstanten `UnresolvedChannelReasons` | `IEmoteService.cs` | 5.2, 5.3 | T2 | T2 (Endpunkt), T3 |
| Api-Testklasse der zwei Routen | `SevenTvEmoteSetSyncBookkeepingEndpointTests` | `tests/EmotePurge.Api.Tests/` | 9.1 | T2 | T3 (ergänzt nichts dort) |
| Frontend-Modell der Antwort | `SyncInSetBody`, `SyncDeletedInSetResponse`, `SyncRestoredInSetResponse`, `UnresolvedChannel` | `seven-tv-emote-set.model.ts` | 5.1, 5.3 | T4 | T7 |
| Vorprüfung | `resolveEditableSet(emoteSetId)`, `EditableSetResolution`, `EditableSetTarget` (= Account-/Set-Felder ohne `host*`), `loadCachedEmoteSetTargets({ refresh })` | `seven-tv-emote-set.service.ts` / `.model.ts` | 6.2, E19, F3 | T4 | T5, T6, T8 |
| Meldeaufrufe | `reportDeletedInSet(setId, body)`, `reportRestoredInSet(setId, body)` | `seven-tv-emote-set.service.ts` | 6.4, 6.5 | T4 | T7 |
| Dreiwertigkeit + Grund | `SyncReportState` (zieht um), `SyncReportReason`, `classifySyncInSetResponse`, `classifySyncInSetFailure` | `web/src/app/core/seven-tv/sync-report-outcome.ts` (neu) | 6.4, E23, F8, AK 15 | T4 | T7 |
| Aufgelöstes Restore-Ziel | `ResolvedRestoreTarget` | `restore-flow.ts` | 6.1 | T6 | T5 (Produzent), T6 (Konsument) |
| Start-Ziel des Restore-Dienstes | `RestoreStartTarget` (der erste Parameter von `startRestore`) | `seven-tv-restore.service.ts` | 6.4 | T7 | T6 |
| Abbruchgrund der Vorprüfung | `TargetCheckBlockReason = 'notEditable' \| 'notSelectable' \| 'unavailable'` | `sync-report-outcome.ts` | 4.2, 4.5, 4.6 | T4 | T5, T6, T8 |
| Restore-Dock | `RestoreProgressSection`, Selector `app-restore-progress-section` | `shared/seven-tv/restore-progress-section.ts` | 6.6, E22 | T9 | Seiten (T9) |
| Locale-Familien (Wortlaut #255) | Datei-Schritt `restore.import.errors.{targetNotEditable,targetNotSelectable,targetCheckUnavailable,noTargetSetForCopy}` (Spec 6.1) · Panel-Restore `restore.errors.{targetNotEditable,targetNotSelectable,targetCheckUnavailable}` · Delete `massDelete.errors.{…dieselben drei}` · Replace `import.errors.{…dieselben drei}` · Grundzeile `syncReportReason.{forbidden,setNotFound,unavailable,channelMismatch,shortfall,other}` · Quelldialog `import.source.noTargetSet` · Picker `import.target.disabled.notEditable` (neben den bestehenden Disabled-Gründen) · Bestätigung `restore.confirmSetIdLine`, `restore.confirmOwnerLine`, `restore.confirmChannelLine`, `restore.confirmForeignToView` · Dock-Zielzeile `restore.targetLine.channel`, `restore.targetLine.owner` | `web/public/i18n/{de,en}.json` | 6.1, 4.3, 4.4, E23 | der Task, der den Text erstmals zeigt | — |

### 0.6 Aufrufer-Inventur je geänderter Signatur (grep, 2026-09-25)

Codex hat zwei Stichproben gefunden (`mass-delete-panel.ts` ruft `startRestore` direkt,
`seven-tv-run-arbiter.spec.ts` ruft beide alten Signaturen; `import-trigger.spec.ts` baut
Restore-Ergebnisse ohne Ziel; die Host-Seiten importieren die neue Komponente nicht). Die
vollständige Inventur steht hier; jede Zeile ist in die Dateiliste des genannten Tasks
eingegangen. Zahlen sind Aufrufstellen laut grep, keine Testfälle.

| Signatur / Typ, geändert durch | Aufrufer, typisierte Fixtures, Host-Imports | landet in |
|---|---|---|
| `EmoteSetTargetSummary`, `EmoteSetTargetAccount` — drei neue Pflichtfelder (T4) | typisierte Literale in `seven-tv-emote-set.service.spec.ts`, `import-target-dialog.spec.ts` (13 Set-Literale), `import-target-choices.spec.ts` (8); die zwei `disabledReason`-Zweige im Template von `import-target-dialog.ts` (der dritte fehlt); `mockEmoteSetTargets` in `e2e/support/mocks.ts` | T4 |
| `SyncReportState` — Umzug nach `sync-report-outcome.ts` (T4) | Importe in `seven-tv-delete.service.ts`, `seven-tv-restore.service.ts`, `seven-tv-import.service.ts`, `run-progress-panel.ts`, `run-progress-panel.spec.ts`, `import-progress-section.spec.ts`, `mass-delete-panel.spec.ts` | T4 |
| `EmoteAdminService.syncDeleted/syncRestored`, `SyncDeletedResult`, `SyncRestoredResult` — entfallen (T7) | `seven-tv-delete.service.ts`, `seven-tv-restore.service.ts`, `seven-tv-import.service.ts`, `emote-admin.service.spec.ts` (vier Fälle), Klassendoku von `import-trigger.ts` (nennt `syncRestored(channelName, …)`) | T7 |
| `SevenTvRestoreService.startRestore` — erster Parameter `RestoreStartTarget` (T7) | `restore-flow.ts` (ein Aufruf), **`mass-delete-panel.ts` (direkter Aufruf im Panel-Restore)**, `seven-tv-run-arbiter.spec.ts` (zwei), `seven-tv-restore.service.spec.ts` (~45), Stubs in `mass-delete-panel.spec.ts`, `import-trigger.spec.ts`, `restore-flow.spec.ts` | T7 (Aufrufe, Zwischenstand), T6 (Ableitung aus dem Ziel) |
| `SevenTvDeleteService.startDelete` — vierter Parameter (T7) | `mass-delete-panel.ts` (ein Aufruf), `seven-tv-run-arbiter.spec.ts` (drei), `seven-tv-delete.service.spec.ts` (~20 plus die `resetIfChannelChanged`-Fälle), `seven-tv-restore.service.spec.ts` (ein Aufruf), Stubs in `mass-delete-panel.spec.ts` | T7 |
| `ResyncTriggerState` — neuer Wert „vom Backend abgeglichen" (T7) | `seven-tv-import.service.ts` (nutzt den Typ für den Import-Resync), `resyncNoticeKey` in `dock-outcome-announcer.ts` (bildet jeden Nicht-`idle`-Wert auf `<family>.resync.<state>` ab — der neue Wert braucht `restore.resync.<state>` in beiden Locales; der Import nimmt ihn nie an), `import-progress-section.ts` | T7 |
| `RestoreRunInfo.channelName` → `hostChannelName` und E13-Felder (T7) | `resetIfChannelChanged` im Dienst, `channel-workspace-layout.ts` (Aufruf unverändert), `seven-tv-restore.service.spec.ts` | T7 |
| `startRestoreFlow(deps, target, rows)` (T6) | `import-trigger.ts`; `restore-flow.spec.ts` (26 Aufrufe mit Positionsparametern) | T6 |
| `RestoreConfirmDialogData` — vier neue Felder (T6) | Builder in `restore-flow.ts` und `mass-delete-panel.ts` (Panel-Restore); `restore-confirm-dialog.spec.ts`; `restore-flow.spec.ts` (liest) | T6 |
| `parsePurgeRunProtocol(text)`, `parseTransferRunForRestore(text)` — ohne `expected` (T5) | `file-import-step.ts` (zwei Aufrufe); `purge-run-export.spec.ts` (18 Aufrufe mit `EXPECTED`, zwei davon `wrongChannel`/`wrongSet`); `transfer-run-export.spec.ts` (8, zwei davon) | T5 |
| `FileImportResult` für `'restore'` — Pflichtfeld `target` (T5) | `file-import-step.spec.ts` (4 Literale); **`import-trigger.spec.ts` (6 Literale plus die Assertions auf die `startRestoreFlow`-Argumente)**; `import-source-dialog.spec.ts` (Emit und Erwartung) | T5 |
| `FileImportStep.setId` → `hostSelectedSetId` (T5), nullbar (T9) | Bindung in `import-source-dialog.ts`; `ImportSourceDialogData` wird nur in `import-trigger.ts` gebaut | T5, T9 |
| `ResolutionContext`, `targetIsTracked` — entfallen (T8) | `import-confirm-dialog.ts` (Feld, `buildTransferPlan`, `validateResolution`, Aufruf von `collisionStepRows`); `import-conflict-resolution-step.ts` (`collisionStepRows(rows, targetIsTracked, overlays)` verliert den Parameter); `conflict-resolution.spec.ts` (Konstanten `TRACKED`/`UNTRACKED`, ~25 Aufrufe mit drittem Parameter); `import-conflict-resolution-step.spec.ts` (Fall „untracked") | T8 |
| `RestoreProgressSection` — neu (T9) | `imports`-Arrays in `usage-stats-page.ts` und `vote-session-detail-page.ts` (Codex-Befund 3) | T9 |
| `IEmoteService` set-scoped Überladungen, `SyncDeletedResultDto`/`SyncRestoredResultDto` — Parameter entfallen (T3) | `EmoteEndpoints.cs` (zwei Handler); `AuthFilterMatrixTests.cs` (zwei DTO-Konstruktoren, einer mit `TargetIsActiveSetOfChannel: false`); `EmoteServiceTests.cs` (vier `TargetIsActiveSetOfChannel`-Assertions in den set-scoped Fällen, die entfallen); Kommentare in `AuditLogQueryService.cs`, `AuditLogQueryServiceTests.cs`, `EmoteEndpoints.cs` (Model-Binder-Hinweis), `UsageStatsAccessAuthorizationFilter.cs` | T3 |
| `ApiErrorCodes.EmoteSetIdEmpty` — entfällt (T3) | `EmoteEndpoints.cs`, `AuthFilterMatrixTests.cs`, `api-error.ts`, beide Locales | T3 |
| `EmoteService`-Konstruktor — dritter Parameter (T2) | 22 `new EmoteService(…)` in `EmoteServiceTests.cs`; die DI löst `IExcludedChannelFilter` auf (Singleton registriert) | T2 |
| `EmoteSetTargetAccount`, `EmoteSetTargetSummaryDto` — neue Record-Parameter (T1) | drei Konstruktoraufrufe in `SevenTvEndpoints.cs`, sonst keine (die Api-Tests lesen JSON) | T1 |

---

## 1. Verträge — wo sie stehen, welcher Task sie trägt

Nichts aus der Spec wird hier wiederholt. Je Vertrag: Spec-Stelle, der Task, der ihn **festlegt**
(baut und testet), die Tasks, die ihn **konsumieren**, und was der Plan zusätzlich festlegt (Grund
in Abschnitt 6).

| Vertrag | Spec | legt fest | konsumiert | Plan-Zusatz |
|---|---|---|---|---|
| Zielliste mit `sevenTvUserId`, `ownerSevenTvUserId`, `editable`; reine Funktion; Picker-Grund `notEditable` | 5.8, E19, F16, AK 29–30 | T1 (Backend), T4 (Modell, Picker) | T5, T6, T8 (über `resolveEditableSet`) | zwei Durchläufe im Endpunkt (0.2); Funktion in Core (0.5) |
| Zwei Routen, Leiter, Policy `Bookkeeping`, Besitzprüfung ohne Audit auf 404/403/503 | 5.1, E3, E5, AK 9 | T2 | T7 (Aufrufer), T10 (Mocks) | — |
| Service-Verhalten: Dedup, betroffene Kanäle (E8), erwarteter Kanal (E18), Zeilen je Kanal, Audit je Kanal + Papier, eine Transaktion | 5.2, 5.5, E6–E8, AK 10–13, 28 | T2 | — | `EmoteService` bekommt `IExcludedChannelFilter` (E8); Test-Helfer (F10) |
| Antwort-DTO mit `channels[]`, `unresolvedChannel`, `resyncTriggered` | 5.3, E9 | T2 (Wire), T4 (Modell) | T7 | Naht 2.1 |
| Live-Event je Kanal mit `NewlyChangedCount > 0`, im Endpunkt | 5.4, AK 14 | T2 | — | — |
| Resync-Stufe 7: je getroffenem Kanal und bei `activeSetDiffers`, unter dem Cooldown, `resyncTriggered` = `Triggered`-Kanäle | 5.1 Stufe 7, E17, F15, AK 26–27 | T2 | T3 (Altform), T7 (Client-Regel E12) | eine private Hilfsmethode für alle drei Routen; Naht 2.3 |
| Guid-Altform nur noch Audit + Resync, `legacyBodyForm: true`, Antwort nach E24; set-scoped Kanalform, `emote_set_id_empty` entfallen | 5.6, E4, E14, E24, F14, AK 22–23 | T3 | — | Legacy-DTOs verlieren auch `NewlyArchivedCount`/`NewlyRestoredCount` (Abschnitt 6, Nr. 6); Stufe 7 läuft für die Altform unbedingt (Nr. 7) |
| Rechte-Änderung | 5.7, E5, E20 | T1 (DECISIONS 3) | — | — |
| Vorprüfung `resolveEditableSet` mit 60-s-Kopie, vier Ausgänge, Picker liest dieselbe Kopie | 6.2, 4.2, E19, F3, F5, AK 3–6 | T4 | T5, T6, T8 | Reihenfolge der Ausgänge nach 6.2 (Abschnitt 6, Nr. 2) |
| Parser ohne `expected`, Ziel aus `meta`, `wrongChannel`/`wrongSet` entfallen; `readProtocolRow` byte-identisch | 6.1, 4.1, E1, E15, F1, F2, AK 2, 22, 24 | T5 | T6 | — |
| `FileImportResult` für `'restore'` trägt `ResolvedRestoreTarget`; Zielprüfung als dritter Schritt; `picked` erst danach; Dateiknopf gesperrt; späte Antwort wirkungslos | 6.1, 4.2, E10, F6, AK 1, 3–5, 35 | T5 (Ablauf), T6 (Typ) | T6 | Typ liegt beim Konsumenten (0.5) |
| `startRestoreFlow(deps, target, rows)`; Bestätigung mit Set-ID, Besitzer, Kanal, „nicht aktiv" nur getrackt, Fremd-Hinweis aus dem Set-Vergleich; Slot-Vorschau-Gabel | 6.3, 4.3, E13, E21, AK 19, 35 | T6 | T5, T9 | Panel-Einstieg zeigt eine gescheiterte Vorprüfung als Abbruchnotiz (Abschnitt 6, Nr. 3) |
| Restore-Dienst: `RestoreStartTarget`, `RestoreRunInfo` nach E13, Meldung set-zentrisch, Dreiwertigkeit mit Grund, Resync-Regel E12 mit `resyncTriggered`-Sperre, `resetIfChannelChanged` gegen `hostChannelName` | 6.4, 4.4, E12, E13, E23, F7, F8, AK 7, 15, 20–21 | T7 | T6 | — |
| Delete-Dienst mit `expectedChannelName`; Import-Dienst `reportRemoved` set-zentrisch, Import-Resync nach der Löschmeldung und mit `resyncTriggered`-Sperre; `EmoteAdminService.syncDeleted/-Restored` entfallen | 6.5, 4.6 Punkt 21, F15, AK 8, 15, 27 | T7 | T8 (Wächter fällt danach) | — |
| `RunProgressPanel.syncReportReason`, Grundzeile; Removal-Grund im Import-Dock | 6.5, E23, 4.4 Punkt 14 | T7 | T9 (Restore-Dock bindet das Panel ein) | gemeinsame Locale-Familie (0.5) |
| Regel 7 und `targetIsTracked` entfallen; Wächter in `startImport` entfällt; Vorprüfung vor Replace (nur bei Replace-Zeilen) und vor der Delete-Bestätigung | 6.6, 4.5, 4.6 Punkt 20, E19, F17, AK 16–17, 31–32 | T8 | — | `ResolutionContext` fällt ganz (Abschnitt 6, Nr. 8); Abbruchgründe je Familie (0.5) |
| Einstieg ohne gewähltes Set: Trigger hinter `!isCoarse()`, `setId` nullbar, Türen deaktiviert mit Grund, nur Rückweg-Dateien; `RestoreProgressSection` außerhalb des Set-Gates auf beiden Seiten | 6.6, 4.1 Punkt 1, 4.4 Punkt 12–13, E22, AK 33–34 | T9 | — | Ort der Section auf der Vote-Seite (Abschnitt 6, Nr. 9) |
| E2E: drei neue, vier angepasste Fälle; jeder Delete/Replace-Test mit `mockEmoteSetTargets` | 9.4, F9 | T10 (neu), T5/T7/T8 (Anpassungen im brechenden Commit) | — | — |
| Doku: Architectur.md, Filter-Kommentar, UI-Designsprache §7.3 und Dock, Spec-200-Nachtrag, Issues | 12.2, F11 | T3, T5, T9, T1, T12 | — | Verteilung je Task |

---

## 2. Nahtstellen — wer den Vertrag festlegt, wer ihn konsumiert

Jede Naht hat einen festlegenden Task, einen konsumierenden Task und eine Prüfung, die beide Seiten
gegeneinander hält. Die Hauptsession prüft nach jeder Welle die Nähte der nächsten (Preflight im
Ledger, wie in Plan-230).

### 2.1 Backend-DTO ↔ Frontend-Modell

**Festlegend:** T2 (Wire-Records nach Spec 5.3, Feldnamen sind Vertrag). **Konsumierend:** T4
(Modell in `seven-tv-emote-set.model.ts`), T7 (Dienste), T10 (Mocks). **Prüfung:** T4 wird in
Welle 1 parallel zu T2 gebaut — gegen Spec 5.3, nicht gegen T2s Code. Die Hauptsession vergleicht
nach Welle 1 die JSON-Feldnamen des Api-Tests (T2) mit den Interface-Feldern (T4) und dem Mock
(T10 später); eine Abweichung ist ein Planfehler, nicht ein Fehler eines der beiden Tasks — die Spec
entscheidet.

### 2.2 Zielliste und `editable`

**Festlegend:** T1 (Backend rechnet `editable` mit der reinen Funktion; AK 29–30). **Konsumierend:**
T4 (`resolveEditableSet` liest `editable`, berechnet es **nie** — Spec 5.8), T5/T6/T8 (Vorprüfung
vor jeder ersten Mutation). **Prüfung:** AK 30 (Unit-Test, T1) hält Zielliste und Besitzprüfung
gegeneinander; AK 6 (T4-Spec + T10-E2E) hält fest, dass die Vorprüfung ein Permit je Minute kostet.
Die Asymmetrie F16 steht im DECISIONS-Eintrag 3 (T1) und wird von keinem Frontend-Task „repariert".

### 2.3 Meldung und Resync-Cooldown

**Festlegend:** T2 (Stufe 7: `TryBeginAsync` je Kanal, `resyncTriggered` = genau die `Triggered`-
Kanäle, nicht erworben ⇒ nicht genannt, kein Fehler — F15). **Konsumierend:** T7 (E12: der Client
stößt nur für ein nicht-aktives Set eines getrackten Kanals an, und nur, wenn der Kanal nicht in
`resyncTriggered` steht; der Import-Resync überspringt einen Kanal aus `resyncTriggered`), T3 (die
Altform ruft dieselbe Stufe). **Prüfung:** AK 26–27 in beiden Richtungen: Api-Test (T2) für die
Zahl der `TryBeginAsync`-Aufrufe und den Inhalt von `resyncTriggered`; Vitest (T7) für „kein eigener
`POST /resync`, wenn der Kanal genannt ist". Die Aussage „höchstens ein Resync je Kanal und 60 s"
belegt erst T12 (Punkt 8, zwei `curl` binnen 60 s) live.

### 2.4 Restore-Dock-Umzug und `MassDeletePanel`

**Festlegend:** T7 (die Anzeigefelder am Laufdatensatz: `setName`, `ownerOrChannelLabel`,
`syncReportReason`, `resyncTrigger`-Zustände inkl. „wird abgeglichen ohne eigenen Request").
**Konsumierend:** T9 (zieht die drei Restore-Blöcke — Notizen, Fortschritt mit `RunProgressPanel`,
Zielzeile — in `RestoreProgressSection` und bindet sie auf beiden Seiten außerhalb des Set-Gates
ein). **Was im Panel bleibt:** der „Wiederherstellen"-Knopf am fertigen Delete-Lauf und der
Restore-Einstieg mit Vorprüfung (T6). **Prüfung:** `mass-delete-panel.spec.ts` verliert die
Restore-Anzeige-Fälle, `restore-progress-section.spec.ts` nimmt sie **inhaltsgleich** auf (Regel 12:
Verhalten, nicht Vorlage); `dockVisible()` bleibt unverändert und zählt `restoreShown`/
`restoreNoticePending` weiter (Spec 6.6) — T9 belegt das mit dem bestehenden `usage-stats-page.spec.ts`.

### 2.5 `ResolvedRestoreTarget` zwischen Datei-Schritt und Flow

**Festlegend:** T6 (Typ in `restore-flow.ts`, Signatur `startRestoreFlow(deps, target, rows)`, die
Ableitung `RestoreStartTarget` aus dem Ziel: `expectedChannelName`/`resyncChannelName` nach Spec
6.4). **Konsumierend:** T5 (der Datei-Schritt emittiert das aufgelöste Ziel), T9 (der Trigger reicht
`hostSelectedSetId: null` durch). **Zwischenstand:** T6 baut in `ImportTrigger` das Ziel vorläufig
aus den bis dahin gefrorenen Seitenwerten (Kanal als `trackedChannelName`, `isActiveSet` aus
`activeSetId`, `ownerDisplayName` leer) — fünf Zeilen, die T5 durch das Ergebnis des Schritts
ersetzt. **Prüfung:** `restore-flow.spec.ts` (T6) prüft die Ableitung; `file-import-step.spec.ts`
(T5) prüft, dass `picked` genau das trägt, was `resolveEditableSet` lieferte, plus `host*` (AK 35).

### 2.6 Fehlergründe und Locale-Familien

**Festlegend:** T4 (`TargetCheckBlockReason` als Vokabular). **Konsumierend:** T5
(`restore.import.errors.*`), T6 (`restore.errors.*`), T8 (`massDelete.errors.*`, `import.errors.*`).
Ein Grund, ein Wert, vier Anzeigeorte — die Texte sind vorläufig (#255), die **Struktur** (welche
Familie an welchem Ort) ist hier Vertrag (0.5).

---

## 3. Tasks

Jeder Task läuft als eigener Subagent mit frischem Kontext und bekommt diesen Abschnitt, Abschnitt
0.5, seine Spec-Abschnitte und die genannten Dateien. „Fertig" je Task: die gezielten Gates des
Tasks **plus** Formatter (`npm --prefix web run format`, `dotnet format EmotePurge.slnx`) und
`npm --prefix web run lint` grün; die vollen Gates aus `CLAUDE.md` fährt T11. Regel 11/12:
Verhalten ja, Vorlage nein — kein Test prüft Tailwind-Ketten oder einen Wortlaut als Selbstzweck.
Vitest-Gates laufen über `npm --prefix web test -- --watch=false --include='<spec-pfad>'`
(Memory: `npx vitest` überspringt die Typprüfung). Jeder Task-Bericht nennt Abweichungen von der
Spec ausdrücklich.

### T1 — `editable` auf dem Draht: eine reine Funktion für Liste und Besitzprüfung

**Ziel:** `GET /api/seventv/me/emote-set-targets` trägt je Account `sevenTvUserId`, je Set
`ownerSevenTvUserId` und `editable`, berechnet nach genau der Regel, mit der die Besitzprüfung bei
der Meldung entscheidet — herausgezogen in eine reine Funktion, die beide rufen.

**Vertrag:** Spec 5.8, E19, F16, AK 29–30; DECISIONS-Eintrag 3 (Spec 12.1) in diesem Commit.

**Dateien:** `src/EmotePurge.Core/Services/EmoteSetEditability.cs` (neu, rein, 0.5);
`src/EmotePurge.Infrastructure/Services/ImportTargetOwnershipService.cs` (`OwnershipEvidence` ruft
die Funktion statt der privaten Kopie — Verhalten byte-identisch, alle bestehenden
`ImportTargetOwnershipServiceTests` bleiben grün); `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs`
(`EmoteSetTargetAccount` + `SevenTvUserId`, `EmoteSetTargetSummaryDto` + `OwnerSevenTvUserId`,
`Editable`; Handler in zwei Durchläufen, 0.2); `tests/EmotePurge.Infrastructure.Tests/Unit/EmoteSetEditabilityTests.cs`
(neu); `tests/EmotePurge.Api.Tests/SevenTvForeignEmoteSetEndpointTests.cs` (die Klasse, die
`/me/emote-set-targets` pinnt); `docs/DECISIONS.md` (Eintrag 3);
`docs/superpowers/specs/2026-09-20-emote-sets-200-spec.md` (Nachtrag am Ende „Restore pro Set,
2026-09-24", Spec 12.2: 6.2 um `editable`/IDs erweitert, 6.6 zeigt auf die #253-Spec, F7 zeigt auf
6.7 statt 6.6).

**Grenzfälle:** Set ohne `owner.id` ⇒ `editable: false` (F16) · Set unter Account A gelistet,
Besitzer B ist ein anderer Account **derselben** Antwort mit lesbarer Liste ⇒ `true` · Besitzer B
ist ein Account der Antwort, dessen Liste **nicht** lesbar war (`setsUnavailable`) ⇒ `false`,
`sevenTvUserId` dieses Accounts `null` · Account mit `NoSevenTvAccount` ⇒ `sevenTvUserId` `null`,
keine Sets · Grants-Lookup `Unavailable` ⇒ `sevenTvUnavailable: true`, `editable` der gelisteten
Sets trotzdem nach der Regel (der Client entscheidet aus beidem, Spec 6.2).

**Tests:** `EmoteSetEditabilityTests` **+5** (die vier Konstellationen oben plus AK 30: dieselbe
Listenkonstellation, mit der `ImportTargetOwnershipServiceTests` „admissible"/„forbidden" belegen,
ergibt an der reinen Funktion dieselbe Antwort — je ein Fall pro bestehendem Ownership-Fall, der
keinen Owner-Lookup braucht; der Lookup-Fall ergibt an der Funktion `false`, F16).
`SevenTvForeignEmoteSetEndpointTests` **+4** (eigener Besitzer / `editor_of`-Besitzer / fremder
Besitzer / Set ohne Besitzer-ID — AK 29; jeweils die drei neuen Felder). `ImportTargetOwnershipServiceTests`
**0 neue**, alle grün (Regressionsschutz der Extraktion).

**Abnahme:** die Antwort ist additiv (der Picker läuft unverändert, bis T4 die Felder liest);
`ImportTargetOwnershipService` hat keine eigene Kopie der Regel mehr. **AK 29, 30.**

**Gates:** `dotnet build EmotePurge.slnx`; `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~EmoteSetEditability|FullyQualifiedName~ImportTargetOwnership"`;
`dotnet test tests/EmotePurge.Api.Tests --filter "FullyQualifiedName~SevenTvForeignEmoteSet"`;
`dotnet format EmotePurge.slnx`.

**Commit:** `feat(seventv): expose set editability on the target list from the ownership rule`.
**Abhängigkeiten:** keine. **Modell:** `sonnet` — die Extraktion ist klein, AK 30 hält sie fest.

### T2 — Set-zentrische Meldungen: Service, zwei Routen, Live-Event, Resync-Stufe

**Ziel:** `POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted` und `…/sync-restored` existieren
mit der Leiter aus Spec 5.1, ändern Zeilen je getroffenem Kanal, prüfen den erwarteten Kanal,
schreiben Audit je Kanal und Papier, veröffentlichen `channel.synced` je geänderten Kanal und stoßen
je Kanal höchstens einen Resync unter dem Cooldown an.

**Vertrag:** Spec 5.1–5.5, E3, E5–E9, E17, E18, F12, F13, F15, AK 9–14, 26–28; DECISIONS-Eintrag 2
(Spec 12.1) in diesem Commit — Routen, Leiter, Antwortform, erwarteter Kanal, Audit je Kanal und
Papier, Live-Event, Backend-Resync und der Absatz zum Restrisiko F12/F13; **ohne** den
Altform-Absatz, den T3 an denselben Eintrag anhängt, wenn die Altform ihren Vertrag ändert (0.4).
Die kanalgebundenen Routen samt set-scoped Form bleiben in diesem Task **unverändert** — sie haben
bis T7 drei Frontend-Aufrufer.

**Dateien:** `src/EmotePurge.Core/Services/IEmoteService.cs` (zwei Methoden nach Spec 5.2, die
DTO-Familie aus 0.5; die set-scoped Kanal-Überladungen bleiben bis T3 stehen);
`src/EmotePurge.Infrastructure/Services/EmoteService.cs` (Implementierung; Konstruktor bekommt
`IExcludedChannelFilter`, E8); `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` (nur
prüfen, dass die DI den Parameter auflöst — `IExcludedChannelFilter` ist registriert);
`src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` (zwei Routen in `emoteSetGroup` neben
`/sync-imported`, `SyncInSetRequest`, Antwort-Records, eine private Hilfsmethode für Stufe 7 nach dem
Muster von `ChannelEndpoints`' Resync-Handler, Live-Event über dieselbe Hilfsmethode und
Fehlerbehandlung wie `EmoteEndpoints.PublishChannelSyncedAsync` — die Methode wird dafür `internal`
oder wandert in eine gemeinsame Hilfsklasse, keine Kopie); `tests/EmotePurge.Api.Tests/SevenTvEmoteSetSyncBookkeepingEndpointTests.cs`
(neu, Muster `SevenTvEmoteSetSyncImportedEndpointTests`, Theory über den Routen-Suffix);
`tests/EmotePurge.Api.Tests/ApiFactory.cs` (nur falls `IRedisPublisher` noch kein Substitut ist);
`tests/EmotePurge.Api.Tests/EmoteRoutePolicyTests.cs` (+2 `InlineData` mit `Bookkeeping`);
`tests/EmotePurge.Infrastructure.Tests/Integration/EmoteServiceTests.cs` (Helfer `CreateService(db)`
mit echtem `ExcludedChannelFilter` aus Konfiguration, alle 22 Konstruktionen darüber — F10; die
neuen Fälle); `docs/DECISIONS.md` (Eintrag 2).

**Grenzfälle (alle als Testfall, Spec 7):** geteiltes Set (zwei Kanäle) · gesperrter Kanal als
Treffer (ignoriert, nicht genannt) und als erwarteter Kanal (`notTracked`, nie ein Hinweis auf die
Sperre) · erwarteter Kanal aktiv mit anderem `ActiveEmoteSetId` (`activeSetDiffers`, Resync) ·
erwarteter Kanal fehlend / `IsBotActive == false` (`notTracked`, kein Resync) · erwarteter Kanal ist
selbst ein Treffer (`unresolvedChannel == null`) · kein Treffer, kein erwarteter Kanal (nur Papier)
· Kanal getroffen, aber keine Zeile gefunden (Zähler 0 **und** Papier-Eintrag, Spec 5.5) · Dedup vor
`reportedCount` · bereits archivierte Zeile behält ihr Datum (heutige Semantik) · Cooldown nicht
erworben (Kanal fehlt in `resyncTriggered`, kein Fehler) · `TriggerResyncAsync` ≠ `Triggered`
(Cooldown freigegeben, Kanal fehlt in `resyncTriggered`) · 404/403/503 der Besitzprüfung ohne
Service-Aufruf, Audit, Resync, Cooldown-Aufruf · `expectedChannelName` mit Großbuchstaben wird
normalisiert (Regel 9).

**Tests:** `EmoteServiceTests` **+14 je Richtung als Theory oder +14/+6** (Implementer wählt; die
Restore-Richtung braucht mindestens: aktives Set, geteiltes Set, nur Papier, Mismatch beider Gründe,
Dedup, Kanal ohne Zeile — AK 10–13, 28). `SevenTvEmoteSetSyncBookkeepingEndpointTests` **+12** über
beide Routen (Spec 9.1: 401; 400 Routenwert vor jedem Service-Aufruf; 400 `emote_ids_empty`; 400
`invalid_channel_name`; 404; 403 bare ohne Body; 503 ohne Service-, Cooldown-Aufruf; 200 mit
weitergereichter Besitzer-Identität und normalisiertem `expectedChannelName`; Live-Event je Kanal mit
`NewlyChangedCount > 0` und keines bei 0; `TryBeginAsync`/`TriggerResyncAsync` je Treffer und für
`activeSetDiffers`, nicht für `notTracked`; Freigabe bei `NotFound`/`NotActive`; `resyncTriggered`
= genau die `Triggered`-Kanäle; kein Resync bei nicht erworbenem Cooldown — AK 9, 14, 26–28).
`EmoteRoutePolicyTests` **+2**. `EmoteSetEditabilityTests` unberührt.

**Abnahme:** beide Routen tragen `Bookkeeping`, `EmoteSetIdValidationFilter` und
`RequireAuthorization` aus der Gruppe; die kanalgebundenen Routen sind **unverändert** (T3 kommt
danach); `dotnet test` der beiden Projekte grün. **AK 9–14, 26–28.**

**Gates:** `dotnet build EmotePurge.slnx`; `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "FullyQualifiedName~EmoteServiceTests"`
(Docker läuft); `dotnet test tests/EmotePurge.Api.Tests`; `dotnet format EmotePurge.slnx`.

**Commit:** `feat(seventv): report deletions and restores per emote set, with a guarded resync`.
**Abhängigkeiten:** T1 (nur derselbe Worktree; kein Symbol von T1 wird gebraucht). **Modell:**
`opus` — die Meldung ändert Zeilen auf Zuruf, entscheidet über die Papierspur und stößt Resyncs an;
ein Fehler hier ist still und trifft alle drei Frontend-Läufe.

### T3 — Die Guid-Altform nur noch Audit + Resync; die set-scoped Kanalform und `emote_set_id_empty` entfallen; Doku

**Ziel:** Die kanalgebundene Route bleibt bis zum E3-Tor der Spec-200, aber ihr Guid-Body ändert
keine Zeile mehr — Audit mit `legacyBodyForm: true`, Resync unter dem Cooldown, Antwort in alter
Form mit gefundener Zahl; die nie produktive set-scoped Kanalform, ihre Leiterstufen 2–4, das
Antwortfeld `targetIsActiveSetOfChannel` und der Fehlercode `emote_set_id_empty` verschwinden.

**Vertrag:** Spec 5.6, E4, E14, E24, F14, AK 22 (Backend-Hälfte), 23; Spec 12.2 (Architectur.md,
Filter-Kommentar). DECISIONS: **kein eigener Eintrag**, sondern der Altform-Absatz (Guid-Form nur
noch Audit + Resync, `legacyBodyForm`, Antwort nach E24, set-scoped Kanalform entfällt) an
Eintrag 2 aus T2 — **in diesem Commit**, Regel 3; bis dahin beschreibt der Eintrag nur, was T2
gebaut hat. **Reihenfolge (Codex-Befund 1):** dieser Task läuft erst, wenn T7 die drei
Frontend-Aufrufer der set-scoped Kanalform umgestellt hat — eine Meldung folgt auf die
7TV-Mutation, ein 400 dort kostete Buchung und Audit.

**Dateien:** `src/EmotePurge.Core/Services/IEmoteService.cs` (set-scoped Überladungen entfallen;
`SyncDeletedResultDto`/`SyncRestoredResultDto` verlieren `TargetIsActiveSetOfChannel` **und** die
`Newly…Count`-Parameter — Abschnitt 6, Nr. 6; Altform-Doku); `src/EmotePurge.Infrastructure/Services/EmoteService.cs`
(Guid-Überladungen ohne Zuweisung an `IsArchived`/`ArchivedAt`/`LastSyncedAt`, Audit mit
`legacyBodyForm: true` bei Zähler > 0, Log-Zeile „legacy body form" bleibt; set-scoped
Implementierungen entfallen); `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs` (Records
`SyncDeletedRequest`/`SyncRestoredRequest` zurück auf `EmoteIds`; `ValidateSyncBookkeepingBody`
auf die Leere-Prüfung reduziert oder ersetzt; kein Live-Event für die Altform; Stufe 7 über T2s
Hilfsmethode; Antwort ohne `targetIsActiveSetOfChannel`); `src/EmotePurge.Api/Validation/ApiErrorCodes.cs`
(`EmoteSetIdEmpty` entfällt; `EmoteIdsInvalid` bleibt — 0.2); `src/EmotePurge.Api/Auth/UsageStatsAccessAuthorizationFilter.cs`
(Kommentar); `web/src/app/core/i18n/api-error.ts` und `web/public/i18n/{de,en}.json`
(`emote_set_id_empty` entfällt — Regel 7, `api-error-locales.spec.ts` bleibt grün);
`tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs` (Leiterfälle 2–4 entfallen; der Fall
„Guid-Form → 200 und Guid-Überladung erreicht" prüft zusätzlich `TryBeginAsync`/`TriggerResyncAsync`;
seine zwei DTO-Konstruktoraufrufe — einer mit `TargetIsActiveSetOfChannel: false` — wandern auf die
zweistellige Form; neu: Body `{ emoteSetId, sevenTvEmoteIds }` → 400 `emote_ids_empty`);
Kommentare, die die set-scoped Überladung als Normalfall nennen: `src/EmotePurge.Infrastructure/Services/AuditLogQueryService.cs`,
`tests/EmotePurge.Infrastructure.Tests/Integration/AuditLogQueryServiceTests.cs`, der
Model-Binder-Hinweis am Ende von `EmoteEndpoints.cs` (0.6);
`tests/EmotePurge.Infrastructure.Tests/Integration/EmoteServiceTests.cs` (die set-scoped Fälle
entfallen — ihre Aussagen leben seit T2 in den `…InSetAsync`-Fällen; die Altform-Fälle kehren um:
Zeilen unverändert, Zähler = gefundene, Audit mit `legacyBodyForm: true`); `docs/Architectur.md`
(die vier Stellen aus F11, inklusive „die Altform ändert keine Zeile mehr").

**Grenzfälle:** Kanalzeile fehlt ⇒ alte Antwort mit allen IDs als `notFoundIds`, kein Eintrag;
Stufe 7 läuft trotzdem an und gibt den Cooldown bei `NotFound` frei (Abschnitt 6, Nr. 7) · alle
Guids unbekannt ⇒ Zähler 0, kein Eintrag, Resync trotzdem · zweiter Aufruf binnen 60 s ⇒ kein
zweiter Resync, Antwort unverändert · ein Body mit **beiden** Listen ⇒ Guid-Pfad, `sevenTvEmoteIds`
wird ignoriert (die Leiterstufe 2 ist weg; Abschnitt 6, Nr. 6).

**Tests:** `EmoteServiceTests` **−5 / ±4** (set-scoped Fälle weg; Altform-Fälle umgedreht, plus
„Audit trägt `legacyBodyForm: true`", „Zeilen unverändert auch bei Guid-Treffern"). `AuthFilterMatrixTests`
**−4 / +1 / ±1**. `api-error-locales.spec.ts` grün ohne den Code.

**Abnahme:** `grep -rn "targetIsActiveSetOfChannel" src web/src` findet nur noch den
Audit-Detailschlüssel des aktiven Zweigs (Spec 5.5) und die Audit-Ansicht, keinen Wire-Vertrag;
`grep -rn "emote_set_id_empty\|EmoteSetIdEmpty"` leer. **AK 22 (Backend), 23.**

**Gates:** wie T2, zusätzlich `npm --prefix web test -- --watch=false --include='src/app/core/i18n/api-error-locales.spec.ts'`
und `npm --prefix web run build` (der Task fasst `api-error.ts` an).

**Commit:** `refactor(emotes): keep the channel-scoped legacy report as audit plus resync only`.
**Abhängigkeiten:** T2 (Hilfsmethode Stufe 7, DTO-Familie), **T7** (kein Frontend-Aufrufer der
set-scoped Kanalform mehr — vorher darf sie nicht fallen). **Modell:** `sonnet`.

### T4 — Frontend-Client: Modell, Vorprüfung mit 60-s-Kopie, zwei Meldeaufrufe, eine Klassifikation

**Ziel:** `SevenTvEmoteSetService` kennt die erweiterte Zielliste, bietet `resolveEditableSet` über
eine 60-s-Client-Kopie an, meldet an die zwei set-zentrischen Routen, und eine pure Funktion
übersetzt jede Antwort und jeden HTTP-Fehler in Zustand und Grund — einmal für alle drei Dienste.
Der Picker deaktiviert nicht bearbeitbare Sets mit Grund.

**Vertrag:** Spec 5.3 (Wire, für das Modell), 5.8, 6.2, 6.4 (Dreiwertigkeit), 4.2 (vier Ausgänge),
E19, E23, F3, F5, F8, AK 3–6 (Client-Hälfte), 15 (Klassifikation), 29 (Picker).

**Dateien:** `web/src/app/core/seven-tv/seven-tv-emote-set.model.ts` (drei neue Felder;
`SyncInSetBody`, `SyncDeletedInSetResponse`, `SyncRestoredInSetResponse`, `UnresolvedChannel`;
`EditableSetResolution`, `EditableSetTarget`); `web/src/app/core/seven-tv/seven-tv-emote-set.service.ts`
(`loadCachedEmoteSetTargets({ refresh })` nach den Regeln von `loadCachedEmoteSetPreview` — nur
Erfolg gecacht, Fehler nie, `refresh` umgeht; `resolveEditableSet`; `reportDeletedInSet`,
`reportRestoredInSet`); `web/src/app/core/seven-tv/sync-report-outcome.ts` (neu, pur:
`SyncReportState` zieht hierher um, bestehende Importe zeigen auf den neuen Ort — kein Re-Export;
`SyncReportReason`, `TargetCheckBlockReason`, `classifySyncInSetResponse(response, reportedCount)`,
`classifySyncInSetFailure(httpStatus)`) + `.spec.ts`; `web/src/app/shared/seven-tv/import-target-choices.ts`
(`ImportTargetDisabledReason` + `'notEditable'`; ein Set mit `editable: false` ist deaktiviert mit
diesem Grund — `notNormalKind` bleibt vorrangig, wenn beides zutrifft); `import-target-dialog.ts`
(liest die gecachte Variante, `reload` mit `refresh: true`; zeigt den dritten Grund);
`web/public/i18n/{de,en}.json` (`import.target.disabled.notEditable` oder der bestehende Ort der
Disabled-Gründe — der Task übernimmt die vorhandene Familie); `web/e2e/support/mocks.ts`
(`mockEmoteSetTargets`: `sevenTvUserId`, `ownerSevenTvUserId`, `editable` mit **Default `true`**, F9).
**Aufrufer und Fixtures (0.6):** die typisierten Ziellisten-Literale in
`seven-tv-emote-set.service.spec.ts`, `import-target-dialog.spec.ts` (13 Set-Literale) und
`import-target-choices.spec.ts` (8) bekommen die drei Felder; das Template von
`import-target-dialog.ts` bekommt den dritten `disabledReason`-Zweig neben `isSourceSet` und
`notNormalKind`; die sieben Importe von `SyncReportState` zeigen auf `sync-report-outcome.ts`.

**Grenzfälle (Spec 4.2, 6.2):** Set gefunden, `kind !== 'NORMAL'` ⇒ `notSelectable`, auch wenn
`editable` wahr wäre · Set gefunden, `editable: true`, aber `sevenTvUnavailable` ⇒ `editable`
(Abschnitt 6, Nr. 2) · Set nicht gefunden, alle Listen lesbar ⇒ `notEditable` · Set nicht gefunden
oder `editable: false`, und ein Account `setsUnavailable` oder `sevenTvUnavailable` ⇒ `unavailable`
· Request 429/503/Netz ⇒ `unavailable`, **nichts** gecacht · zweiter Aufruf binnen 60 s ⇒ kein
Request (`HttpTestingController` erwartet **einen**) · `refresh: true` ⇒ Request trotz Kopie ·
Klassifikation: `channels: []` ohne Mismatch ⇒ `succeeded`; ein Kanal mit `count < reportedCount` ⇒
`partial`/`shortfall`; zwei Kanäle, einer vollständig ⇒ `partial`/`shortfall` (F8: je Kanal); ein
`unresolvedChannel` ⇒ `partial`/`channelMismatch` auch bei vollständigen Kanälen; 403 ⇒
`failed`/`forbidden`; 404 ⇒ `failed`/`setNotFound`; 429, 503, 0 ⇒ `failed`/`unavailable`; sonst
`failed`/`other`.

**Tests:** `seven-tv-emote-set.service.spec.ts` **+9** (vier Ausgänge, Cache-Treffer, Fehler nie
gecacht, `refresh`, die zwei Meldeaufrufe mit Route und Body — AK 3–6, Wire-Vertrag erlaubt).
`sync-report-outcome.spec.ts` **+10** (die Fälle oben — AK 15). `import-target-choices.spec.ts`
**+2** (`notEditable`; Vorrang von `notNormalKind`). `import-target-dialog.spec.ts` **+1** (Reload
erzwingt `refresh`).

**Abnahme:** kein Aufrufer außer dem Picker und den Specs ruft die neuen Methoden — die Dienste
kommen in T7, die Vorprüfungen in T5/T6/T8; `grep -n "archivedCount >= \|restoredCount >= " web/src`
unverändert (T7 räumt es ab). **AK 3–6 (Client), 15 (Klassifikation), 29 (Picker).**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/core/seven-tv/**/*.spec.ts' --include='src/app/shared/seven-tv/import-target-*.spec.ts'`;
`npm --prefix web run build` (Typprüfung über alle Fixtures, die das Modell bauen);
`npm --prefix web run lint`; `npm --prefix web run format`.

**Commit:** `feat(seventv): resolve set editability client-side and classify set-centric reports`.
**Abhängigkeiten:** keine (gegen Spec 5.3/5.8, parallel zu T1/T2). **Modell:** `sonnet`.

### T5 — Die Datei bestimmt das Ziel: Parser ohne Erwartung, Zielprüfung im Datei-Schritt

**Ziel:** Beide Restore-Parser geben das Ziel-Set aus `meta` zurück statt es gegen die Seite zu
prüfen; `FileImportStep` prüft das Ziel über `resolveEditableSet` als dritten Schritt und emittiert
`picked` erst mit einem aufgelösten Ziel; `wrongChannel`/`wrongSet` gibt es nicht mehr; ohne
gewähltes Set liest der Schritt nur Rückweg-Dateien.

**Vertrag:** Spec 6.1, 4.1, 4.2, E1, E2, E10, E11, E15, E21 (Feld `hostSelectedSetId`), F1, F2,
F5, F6, AK 1–5 (Schritt), 22 (Parser-Hälfte), 24, 35; DECISIONS-Eintrag 1 (Spec 12.1) in diesem
Commit; UI-Designsprache §7.3 (Spec 12.2: Datei-Zweig nimmt das Ziel aus der Datei und prüft es;
ohne gewähltes Set nur Rückweg-Dateien; „into the channel of the page" und „the target is not
asked for, it is fixed" werden entsprechend eingeschränkt).

**Dateien:** `web/src/app/shared/export/purge-run-export.ts` (`parsePurgeRunProtocol(text)`,
Ergebnis mit `target.emoteSetId` aus `meta.emoteSetId`; `readProtocolRow` und die Prüfleiter
Envelope/`kind`/`formatVersion`/`meta`-Form/Zeilen **unverändert**) + `.spec.ts`;
`web/src/app/shared/export/transfer-run-export.ts` (`parseTransferRunForRestore(text)`, Ziel aus
`meta.targetEmoteSetId`, Envelope-`channelName` wird **nicht** gelesen — F2) + `.spec.ts`;
`web/src/app/shared/seven-tv/file-import-step.ts` (Input `setId` entfällt, Input
`hostSelectedSetId: string | null` kommt; injiziert `SevenTvEmoteSetService`; Ablauf Envelope →
Parser → `resolveEditableSet` → `picked`; Dateiknopf gesperrt, solange die Prüfung läuft; eine
Antwort nach Zerstörung oder Abbruch ändert nichts — F6; `hostSelectedSetId === null` ⇒ Kopier-Dateien
(`emote-list`, `usage`) werden mit `noTargetSetForCopy` abgewiesen, **vor** `parseImportSource`)
+ `.spec.ts`; `web/src/app/shared/seven-tv/import-source-dialog.ts` (Bindung `[hostSelectedSetId]="data.setId"`
— `data.setId` bleibt in diesem Task `string`, T9 macht es nullbar); `web/src/app/shared/seven-tv/import-trigger.ts`
(reicht das aufgelöste Ziel des Schritts an `startRestoreFlow` — ersetzt den Zwischenstand aus T6,
Naht 2.5; die Klassendoku, die `syncRestored(channelName, …)` als Meldeweg nennt, zieht mit)
+ `.spec.ts` (**Codex-Befund 4:** sechs `kind: 'restore'`-Fixtures ohne Ziel und die Assertions
auf die `startRestoreFlow`-Argumente wandern auf das aufgelöste Ziel); `web/src/app/shared/seven-tv/import-source-dialog.spec.ts`
(das `picked`-Fixture und seine Erwartung); `web/public/i18n/{de,en}.json` (`restore.import.errors.targetNotEditable`,
`targetNotSelectable`, `targetCheckUnavailable`, `noTargetSetForCopy`; `wrongChannel`, `wrongSet`
entfallen); `web/e2e/emote-import.e2e.spec.ts` (der Fall „fremdes Purge-Protokoll" in
`push flow: rejection` erwartet `targetNotEditable` mit einer Zielliste ohne dieses Set —
`mockEmoteSetTargets` kommt in diesen Test); `docs/DECISIONS.md` (Eintrag 1); `docs/UI-Designsprache.md`
(§7.3).

**Grenzfälle:** Purge-Protokoll der Formatversion 1 und 2 ⇒ Zeilen byte-identisch zu vor dieser
Spec (AK 24) · Purge-Protokoll ohne `meta.emoteSetId` ⇒ `wrongKind` wie heute (F1) ·
Übertragungsdatei mit `targetChannelName: null` und Envelope `''` ⇒ Ziel aus `meta`, kein Fehler
(F2) · `planned` ⇒ jeder `removedTarget`, `finished` ⇒ nur `confirmed` (Spec 4.1 Punkt 3,
unverändert aus #230) · Zielprüfung liefert `notSelectable` / `notEditable` / `unavailable` ⇒
Banner mit dem passenden Schlüssel, kein `picked`, Dialog offen · zweiter Dateipick während der
Prüfung ⇒ Knopf gesperrt, kein zweiter Request · Dialog geschlossen, Antwort kommt später ⇒ kein
`picked`, kein Fehler · `hostSelectedSetId: null` + Rückweg-Datei ⇒ läuft durch, `target.hostSelectedSetId`
ist `null` · `hostSelectedSetId: null` + Emote-Liste ⇒ `noTargetSetForCopy` · Groß-/Kleinschreibung
im Kanalnamen der Datei ⇒ irrelevant (kein Vergleich mehr — #256 Punkt 3 erledigt sich).

**Tests:** `purge-run-export.spec.ts` (**−2 / +2**: `wrongChannel`/`wrongSet` weg; Ziel-Extraktion
je Formatversion; `readProtocolRow`-Fälle **0 geändert** — AK 24). `transfer-run-export.spec.ts`
(**−2 / +2**: beide Stufen liefern `target.emoteSetId`, Envelope `''` stört nicht).
`file-import-step.spec.ts` **+8** (vier Ausgänge im Banner; gesperrter Knopf; späte Antwort ohne
Wirkung; `hostSelectedSetId: null` mit Rückweg-Datei und mit Emote-Liste; `picked` trägt exakt das
Ergebnis von `resolveEditableSet` plus `host*` — AK 3–5, 35). `import-trigger.spec.ts` **±6 / +1**
(die sechs Fixtures tragen ein Ziel; reicht das Ziel durch). `import-source-dialog.spec.ts` **±1**.
E2E: der angepasste Rejection-Fall.

**Abnahme:** `grep -rn "wrongChannel\|wrongSet" web/src web/public` leer; kein Parser liest
`expected`; kein Aufrufer gibt `setId` an den Schritt. **AK 1–5 (Schritt), 22 (Parser), 24, 35.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/export/*.spec.ts' --include='src/app/shared/seven-tv/file-import-step.spec.ts' --include='src/app/shared/seven-tv/import-trigger.spec.ts'`;
`npm --prefix web run build` (Typprüfung über alle Fixtures, die `FileImportResult` bauen —
Codex-Befund 4); `npm --prefix web run e2e -- e2e/emote-import.e2e.spec.ts` (nur ohne Api auf
`:5151`); Lint, Format.

**Commit:** `feat(restore): let the file name the target set and verify it against the target list`.
**Abhängigkeiten:** T4 (`resolveEditableSet`, `TargetCheckBlockReason`), T6 (`ResolvedRestoreTarget`,
neue Flow-Signatur); läuft **vor** T8, seriell (Codex-Befund 5). **Modell:** `opus` — die Datei ist nicht vertrauenswürdig, und dieser Schritt
ist die eine Stelle, an der aus ihr ein Ziel wird; dazu ein asynchroner Zustand in einem Dialog,
der mit `picked` schließt (F6).

### T6 — Restore-Flow und Bestätigung mit aufgelöstem Ziel; der Panel-Einstieg mit Vorprüfung

**Ziel:** `startRestoreFlow` nimmt ein `ResolvedRestoreTarget` statt vier Positionsparameter, leitet
daraus das Start-Ziel des Dienstes ab, zeigt in der Bestätigung Set-Name, Set-ID, Besitzer, Kanal,
die „nicht aktiv"-Zeile nur für getrackte Ziele und den Fremd-Hinweis aus dem Set-Vergleich; der
„Wiederherstellen"-Einstieg am fertigen Delete-Lauf baut sein Ziel ebenfalls aus der Vorprüfung.

**Vertrag:** Spec 6.3, 4.3, E13, E16, E21, F7 (Ableitung), AK 19, 35; Spec 4.6 Punkt 22.

**Dateien:** `web/src/app/shared/seven-tv/restore-flow.ts` (Typ `ResolvedRestoreTarget` nach Spec
6.1; Signatur; Slot-Vorschau-Gabel nach Spec 4.3 Punkt 8: getrackt-aktiv ⇒ `getSetStatus`, sonst
`loadEmoteSetPreview(trackedChannelName ?? twitchLogin, emoteSetId)`; Ableitung von
`RestoreStartTarget` — `expectedChannelName`/`resyncChannelName` nach Spec 6.4,
`ownerOrChannelLabel` = Kanal, wenn getrackt, sonst `ownerDisplayName`) + `.spec.ts`;
`web/src/app/shared/seven-tv/restore-confirm-dialog.ts` (`RestoreConfirmDialogData` +
`emoteSetId`, `ownerDisplayName`, `trackedChannelName`, `foreignToView`; Zeilenreihenfolge: Set-Zeile,
Set-ID, Besitzer, Kanal (nur getrackt), „nicht aktiv" (nur getrackt und nicht aktiv),
Fremd-Hinweis, Namen, Projektion, Historien-Hinweis — die Reihenfolge ist Vertrag, der Wortlaut
#255) + `.spec.ts`; `web/src/app/shared/seven-tv/mass-delete-panel.ts` (`openRestoreConfirm`: vor
der Bestätigung `resolveEditableSet(run.setId)`; Erfolg ⇒ `ResolvedRestoreTarget` mit
`hostChannelName = channelName()`, `hostSelectedSetId = setId()`; Misserfolg ⇒ Abbruchnotiz mit
`restore.errors.*` — Abschnitt 6, Nr. 3; die private Kopie der Kette darf auf `startRestoreFlow`
zusammengeführt werden, wenn der Task belegt, dass beide danach gleich sind — sonst bleibt sie und
bekommt nur das Ziel) + `.spec.ts`; `web/src/app/shared/seven-tv/import-trigger.ts`
(Zwischenstand aus Naht 2.5: baut das Ziel aus den gefrorenen Seitenwerten, bis T5 den Schritt
umstellt — und ruft dafür **ebenfalls** `resolveEditableSet`, damit auch dieser Zwischenstand keine
Mutation ohne Vorprüfung startet); `web/public/i18n/{de,en}.json` (Bestätigungszeilen aus 0.5;
`restore.errors.*`).

**Grenzfälle:** Ziel = gewähltes Set der Seite ⇒ kein Fremd-Hinweis · anderes Set desselben Kanals
⇒ Fremd-Hinweis **und** (wenn getrackt, nicht aktiv) „nicht aktiv" · `hostSelectedSetId: null` ⇒
Fremd-Hinweis · ungetrackt ⇒ Besitzer-Zeile, keine Kanal-Zeile, keine „nicht aktiv"-Zeile,
Slot-Vorschau über `twitchLogin` · Slot-Vorschau scheitert ⇒ nur die Projektionszeile fehlt ·
Vorprüfung am Panel-Einstieg scheitert ⇒ keine Bestätigung, Abbruchnotiz, kein Request an 7TV ·
Token fehlt ⇒ Prompt **vor** der Bestätigung (Reihenfolge bleibt, Spec 4.3 Punkt 8) · Arbiter
belegt ⇒ still (unverändert).

**Tests:** `restore-flow.spec.ts` **±26 / +6** (die 26 Aufrufe mit Positionsparametern wandern auf
das Ziel — 0.6; Slot-Vorschau-Gabel für
getrackt-aktiv / getrackt-nicht-aktiv / ungetrackt; `foreignToView` in den drei Fällen aus Spec
9.3; Ableitung `expectedChannelName`/`resyncChannelName` — AK 19, 35). `restore-confirm-dialog.spec.ts`
**+4** (Zeilen je Zielklasse — Vertrag der Reihenfolge, nicht des Wortlauts). `mass-delete-panel.spec.ts`
**+2** (Vorprüfung vor der Restore-Bestätigung; Abbruchnotiz mit Grund).

**Abnahme:** kein Aufrufer von `startRestoreFlow` reicht einen Kanal als Pflichtparameter;
`RestoreConfirmDialogData` ohne `emoteSetId` kompiliert nicht. **AK 19, 35 (Bestätigung), 4.6
Punkt 22.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/restore-*.spec.ts' --include='src/app/shared/seven-tv/mass-delete-panel.spec.ts' --include='src/app/shared/seven-tv/import-trigger.spec.ts'`;
`npm --prefix web run build`; Lint, Format.

**Commit:** `feat(restore): confirm a restore against the resolved target set instead of the page`.
**Abhängigkeiten:** T4 (`resolveEditableSet`), T7 (`startRestore` mit `RestoreStartTarget`).
**Modell:** `sonnet`.

### T7 — Die drei Dienste melden set-zentrisch: erwarteter Kanal, Grund, Resync-Regel

**Ziel:** Restore-, Delete- und Import-Dienst melden an die set-zentrischen Routen mit
`expectedChannelName`, lesen Zustand **und** Grund aus der Antwort über die Klassifikation aus T4,
stoßen den Resync nach E12/F15 an, und das Dock zeigt den Grund. `EmoteAdminService` verliert die
zwei kanalgebundenen Meldeaufrufe.

**Vertrag:** Spec 6.4, 6.5, 4.4 Punkte 10–11, 13–14, 4.6 Punkt 21, E12, E13, E23, F7, F8, F15, AK
7, 8, 15, 20, 21, 27.

**Dateien:** `web/src/app/core/seven-tv/seven-tv-restore.service.ts` (`startRestore(target:
RestoreStartTarget, emotes, skippedDuplicates, duplicateCheckAvailable, skippedNameTaken)`;
`RestoreRunInfo` nach E13 plus Anzeigefelder `setName`, `ownerOrChannelLabel`; `reportRestored` über
`reportRestoredInSet`; `syncReportReason`-Signal; Resync nur bei `resyncChannelName !== null &&
!resyncTriggered.includes(resyncChannelName)`, sonst `idle` — und ein neuer `resyncTrigger`-Zustand
für „Backend hat ihn angestoßen", ohne eigenen Request; `resetIfChannelChanged` gegen
`hostChannelName`; Retry-Policy unverändert) + `.spec.ts`; `web/src/app/core/seven-tv/seven-tv-delete.service.ts`
(`startDelete(setId, channelName, emotes, expectedChannelName)`, `DeleteRunInfo.expectedChannelName`,
`reportDeleted` über `reportDeletedInSet`, `syncReportReason`; `SyncReportState` importiert aus
`sync-report-outcome.ts`) + `.spec.ts`; `web/src/app/core/seven-tv/seven-tv-import.service.ts`
(`reportRemoved` über `reportDeletedInSet(run.targetSetId, …)` mit `expectedChannelName` nach Spec
6.5; der `channelName === null`-Frühausstieg entfällt; `removalReportReason`; der Import-Resync
läuft **nach** der Löschmeldung und überspringt den Kanal, wenn er in deren `resyncTriggered` steht
— ohne Löschmeldung wie heute; der Wächter in `startImport` bleibt bis T8) + `.spec.ts`;
`web/src/app/core/emotes/emote-admin.service.ts` (`syncDeleted`, `syncRestored`,
`SyncBookkeepingBody`, `SyncDeletedResult`, `SyncRestoredResult` entfallen) + `.spec.ts`;
`web/src/app/shared/seven-tv/run-progress-panel.ts` (Input `syncReportReason`, Grundzeile unter
der `syncReportFailed`-Notiz, Familie `syncReportReason.*`) + `.spec.ts`;
`web/src/app/shared/seven-tv/import-progress-section.ts` (Grundzeile unter der Removal-Report-Notiz)
+ `.spec.ts`; `web/src/app/shared/seven-tv/mass-delete-panel.ts` (Aufrufer von `startDelete` gibt
`expectedChannelName` = `channelName()`, wenn `setId === effectiveActiveSetId()`, sonst `null`;
**der direkte `restoreService.startRestore`-Aufruf im Panel-Restore** baut `RestoreStartTarget`
aus den Laufwerten des Delete-Laufs — Zwischenstand, den T6 durch die Ableitung aus dem
aufgelösten Ziel ersetzt (Codex-Befund 2); bindet `[syncReportReason]` am Restore-Panel — bis T9
den Block umzieht) + `.spec.ts` (Stubs beider Signaturen, Importpfad von `SyncReportState`);
`web/src/app/core/seven-tv/seven-tv-run-arbiter.spec.ts` (**fünf Aufrufe der alten Signaturen**
von `startDelete`/`startRestore` — Codex-Befund 2); `web/src/app/shared/seven-tv/dock-outcome-announcer.ts`
(`resyncNoticeKey` bildet jeden Nicht-`idle`-Wert auf `<family>.resync.<state>` ab — der neue
`ResyncTriggerState`-Wert braucht `restore.resync.<state>` in beiden Locales, und der Import-Zweig
nimmt ihn nie an); `web/src/app/shared/seven-tv/restore-flow.ts`
(nur der eine `startRestore`-Aufruf: baut `RestoreStartTarget` aus den bis T6 vorhandenen Werten —
Naht 2.5, Zwischenstand von wenigen Zeilen); `web/public/i18n/{de,en}.json` (`syncReportReason.*`,
ein Restore-Resync-Text für „wird vom Backend abgeglichen"); `web/e2e/vote-ballot.e2e.spec.ts`
(die drei Stellen, die `**/api/channels/{c}/emotes/sync-deleted` routen, routen die set-zentrische
Route und prüfen `{ sevenTvEmoteIds, expectedChannelName }` — AK 8); `web/e2e/emote-import.e2e.spec.ts`
(jede Stelle, die `sync-deleted`/`sync-restored` kanalgebunden routet — 0.2 nennt fünf — auf die
set-zentrische Route mit dem neuen Body; `mockSyncDeletedInSet`/`mockSyncRestoredInSet` als
Helfer in `e2e/support/mocks.ts`, mit konfigurierbarer Antwort nach Spec 5.3).

**Grenzfälle (alle als Spec-Fall):** Restore-Antwort `channels: []`, `unresolvedChannel: null` ⇒
`succeeded`, kein Grund · ein Kanal mit `restoredCount < reportedCount` ⇒ `partial`/`shortfall` ·
`unresolvedChannel` ⇒ `partial`/`channelMismatch` · 403 nach den Retries ⇒ `failed`/`forbidden`;
404 ⇒ `failed`/`setNotFound` (nie `succeeded` — #224, AK 15); 429/503/Netz ⇒ `failed`/`unavailable`
· `resyncChannelName` gesetzt und **nicht** in `resyncTriggered` ⇒ genau ein `POST /resync`;
gesetzt und genannt ⇒ keiner, Dock zeigt „wird abgeglichen"; `null` ⇒ keiner, keine Zeile (AK 21,
27) · `resetIfChannelChanged` mit dem Kanal der Seite ≠ `hostChannelName` ⇒ fertiger Lauf weg,
laufender bleibt (AK 20) · Delete auf nicht-aktives Set ⇒ `expectedChannelName: null` · Replace mit
`targetIsActiveSet` ⇒ `expectedChannelName = targetChannelName`, sonst `null`; Import-Resync
überspringt einen genannten Kanal; ohne Replace läuft der Import-Resync wie heute · IDs bleiben
dedupliziert (eine je Emote auch bei zwei Aliasen — AK 7, heutiges Verhalten).

**Tests:** `seven-tv-restore.service.spec.ts` **±45 / +9**, `seven-tv-delete.service.spec.ts`
**±20 / +5**, `seven-tv-run-arbiter.spec.ts` **±5**, `seven-tv-import.service.spec.ts` **+5** (die
Fälle oben; jeder Bestandsaufruf wandert auf die neue Signatur, jeder Bestandsfall, der
`emoteAdminService.syncDeleted/syncRestored` stubbt, wechselt auf `reportDeletedInSet/
reportRestoredInSet` — Zählung im Bericht). `emote-admin.service.spec.ts` **−4**.
`run-progress-panel.spec.ts` **+1** (Grundzeile je `syncReportReason`, Struktur).
`import-progress-section.spec.ts` **+1**. E2E: die angepassten Fälle (AK 8).

**Abnahme:** `grep -rn "emotes/sync-deleted\|emotes/sync-restored" web/src` leer; kein Dienst
rechnet `archivedCount >= …` selbst. **AK 7, 8, 15 (Dienste), 20, 21, 27 (Client).**

**Gates (Codex-Befund 2 — Checkpoint mit voller Typprüfung):** `npm --prefix web run build`;
`npm --prefix web test -- --watch=false` **voll** (drei Dienste, sieben Spec-Dateien mit
Signaturänderungen — ein Filter übersähe den Rest); `npm --prefix web run e2e --
e2e/vote-ballot.e2e.spec.ts e2e/emote-import.e2e.spec.ts` (nur ohne Api auf `:5151`); Lint, Format.

**Commit:** `feat(seventv): report deletes, restores and replace removals per set with the expected channel`.
**Abhängigkeiten:** T4. **Modell:** `opus` — drei Dienste, eine Meldungslogik, die entscheidet, ob
das Audit eine Lücke sieht, und die Verschränkung mit dem Backend-Resync (F15); ein Fehler ist
still.

### T8 — Die Sperre fällt, die Vorprüfung kommt: Regel 7, Wächter, Delete und Replace

**Ziel:** „Ziel ersetzen" ist für ein ungetracktes Ziel wählbar; `validateResolution` kennt sechs
Regeln; der Wächter in `startImport` ist weg; vor der Delete-Bestätigung und vor dem Start eines
Plans mit Replace-Zeile läuft die Vorprüfung, und ein Scheitern nennt den Grund, ohne dass etwas
läuft.

**Vertrag:** Spec 6.6 (Regel 7, Wächter, Locale-Schlüssel), 4.5 Punkte 15–19, 4.6 Punkt 20, E19,
F17, AK 16, 17 (Client-Hälfte), 22 (Regel-7-Schlüssel), 31, 32; DECISIONS-Eintrag 4 (Spec 12.1) in
diesem Commit.

**Dateien:** `web/src/app/shared/seven-tv/conflict-resolution.ts` (Regel 7, `ruleReplaceNeedsTrackedTarget`,
Union-Wert `replaceNeedsTrackedTarget`, `ResolutionContext` entfallen ganz — `validateResolution`
und `buildTransferPlan` verlieren den dritten Parameter, Abschnitt 6, Nr. 8; Doku zählt sechs
Regeln) + `.spec.ts` (Konstanten `TRACKED`/`UNTRACKED` und ~25 Aufrufe mit drittem Parameter —
0.6); `web/src/app/shared/seven-tv/import-conflict-resolution-step.ts`
(`collisionStepRows(rows, targetIsTracked, overlays)` verliert den Parameter und die
Replace-Zuordnung; die Aufrufstelle in `import-confirm-dialog.ts` zieht mit) + `.spec.ts` (Fall
„untracked"); `web/src/app/shared/seven-tv/import-confirm-dialog.ts`
(`resolutionContext`-Feld, die Aufrufe von `buildTransferPlan`, `validateResolution` und
`collisionStepRows` verlieren den Kontext; Replace-Option für ungetracktes Ziel wählbar; die Pflicht-Rückweg-Datei
bleibt, Dateiname mit Set-ID bei ungetracktem Ziel — Spec 4.5 Punkt 18) + `.spec.ts`;
`web/src/app/core/seven-tv/seven-tv-import.service.ts` (Wächter entfällt; Signal für den
Vorprüfungs-Abbruch an der transienten Notiz-Mechanik — 0.2) + `.spec.ts`; `web/src/app/shared/seven-tv/import-flow.ts`
(`start`: hat der Plan eine Replace-Zeile, `resolveEditableSet(targetSetId)` **vor**
`recheckTransferPlan`; Scheitern ⇒ nichts startet, Grund über das Signal; ohne Replace keine
Vorprüfung) + `.spec.ts`; `web/src/app/shared/seven-tv/import-progress-section.ts` (Notizzeile für
den Abbruchgrund, mit `importNoticePending`-Fenster; gesprochener Zwilling im
`DockOutcomeAnnouncer`) + `.spec.ts`, `dock-outcome-announcer.ts` + `.spec.ts`;
`web/src/app/shared/seven-tv/mass-delete-panel.ts` (vor `openDeleteConfirmDialog`:
`resolveEditableSet(setId())`, Knopf gesperrt solange die Prüfung läuft — dasselbe Idiom wie
`liveAliasReadPending`; Scheitern ⇒ `abortNotice` mit `massDelete.nothingDeleted` + Grund aus
`massDelete.errors.*`, keine Bestätigung, kein 7TV-Request) + `.spec.ts`; `web/public/i18n/{de,en}.json`
(`import.resolve.replaceNeedsTracked` und `import.resolve.violation.replaceNeedsTrackedTarget`
entfallen; `massDelete.errors.*`, `import.errors.*` je drei Gründe); `web/e2e/emote-import.e2e.spec.ts`,
`web/e2e/vote-ballot.e2e.spec.ts` (jeder Test, der einen Delete oder Replace startet, bekommt
`mockEmoteSetTargets` mit dem Ziel-Set — F9; der Fall (7) „Ungetracktes Ziel (R5)" aus Plan-230
T9 dreht sich um: Replace wählbar, Rückweg sichern, `removeEmote` gesendet, Löschmeldung an die
set-zentrische Route mit `expectedChannelName: null`); `docs/DECISIONS.md` (Eintrag 4: löst die
beiden Absätze des Eintrags vom 2026-09-23 ab, die Spec 12.1 nennt).

**Grenzfälle:** Plan mit Replace-Zeile, Vorprüfung `notEditable` ⇒ kein `recheckTransferPlan`, kein
Request an 7TV, Notiz mit Grund · Plan ohne Replace ⇒ kein Aufruf von `resolveEditableSet` (AK 32)
· Delete: Vorprüfung `unavailable` ⇒ Abbruchnotiz `targetCheckUnavailable`, keine Bestätigung (AK
31) · Delete auf der Vote-Session-Seite ⇒ dieselbe Vorprüfung (das Panel ist dasselbe) ·
Vorprüfung ist Cache-Treffer ⇒ kein Request (AK 6) · ungetracktes Ziel im Auflösungsschritt ⇒
Replace wählbar, Zusammenfassung mit Entfernungszeile, Aktionszeile „Rückweg sichern" (AK 16, 17)
· `buildTransferPlan` ohne Kontext ⇒ jeder Aufrufer kompiliert nur ohne den dritten Parameter.

**Tests:** `conflict-resolution.spec.ts` (**−3 / +1**: die Regel-7-Fälle weg; Replace mit
ungetracktem Ziel ist gültig — AK 16). `import-confirm-dialog.spec.ts` (**±1**: der R5-Fall dreht
sich um: Replace angeboten, Rückweg-Datei mit Set-ID im Namen). `import-conflict-resolution-step.spec.ts`
**±1**. `seven-tv-import.service.spec.ts` (**−1 / +1**: der Wurf-Fall weg; Replace in ein Ziel mit
`channelName === null` meldet set-zentrisch — schon T7 —, hier: startet). `import-flow.spec.ts`
**+3** (Vorprüfung nur bei Replace; Scheitern startet nichts; Erfolg ruft `recheckTransferPlan` —
AK 32). `mass-delete-panel.spec.ts` **+3** (Vorprüfung vor der Bestätigung; Abbruchnotiz je Grund;
Knopf gesperrt während der Prüfung — AK 31). `import-progress-section.spec.ts` **+1**,
`dock-outcome-announcer.spec.ts` **+1** (Grund sichtbar und gesprochen). E2E: angepasste Fälle.

**Abnahme:** `grep -rn "replaceNeedsTracked\|targetIsTracked\|ResolutionContext" web/src web/public`
leer; `startImport` wirft nicht mehr für einen Replace gegen `channelName === null`. **AK 16, 17
(Client), 22 (Regel 7), 31, 32.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/**/*.spec.ts' --include='src/app/core/seven-tv/seven-tv-import.service.spec.ts'`;
`npm --prefix web run build`; `npm --prefix web run e2e -- e2e/emote-import.e2e.spec.ts e2e/vote-ballot.e2e.spec.ts`
(nur ohne Api auf `:5151`); Lint, Format.

**Commit:** `feat(import): offer replace for untracked targets and check editability before every first mutation`.
**Abhängigkeiten:** T4, T7 (set-zentrische Löschmeldung, sonst liefe ein Replace ins Ungetrackte
ohne Meldung), **T5** (seriell davor — beide ändern `emote-import.e2e.spec.ts` und setzen einen
DECISIONS-Eintrag an den Anfang, Codex-Befund 5). **Modell:** `sonnet`.

### T9 — Einstieg ohne gewähltes Set und das Restore-Dock als eigene Section

**Ziel:** Der Import-Einstieg steht auf jeder Kanalseite auch ohne gewähltes Set; die drei
Kopier-Türen sind dann mit Grund deaktiviert; das Restore-Dock lebt in `RestoreProgressSection`
außerhalb des Set-Gates auf beiden Seiten; kein neuer Button.

**Vertrag:** Spec 6.6 (Trigger-Gate, Section), 4.1 Punkt 1, 4.4 Punkte 12–13, E22, AK 33–34; Spec
12.2 (UI-Designsprache: Dock-Abschnitt — Restore-Dock als eigene Section — und §7.3: Einstieg ohne
Set).

**Dateien:** `web/src/app/features/usage-stats/usage-stats-page.html` (`app-import-trigger` in einen
eigenen `@if (!isCoarse())`, `[setId]="selectedEmoteSetId()"`; der Kopier-Knopf bleibt im Set-Gate;
`<app-restore-progress-section />` neben `app-import-progress-section` außerhalb des Set-Gates) +
`usage-stats-page.spec.ts` (Trigger ohne Set sichtbar, Kopier-Knopf nicht — AK 33–34;
`dockVisible()` unverändert); **`web/src/app/features/usage-stats/usage-stats-page.ts` und
`web/src/app/features/voting/vote-session-detail-page.ts`** (`RestoreProgressSection` im
jeweiligen `imports`-Array neben `ImportProgressSection` bzw. `MassDeletePanel` — Codex-Befund 3;
ohne den Eintrag kennt die Standalone-Seite das Element nicht, und der Build bricht);
`web/src/app/shared/seven-tv/import-trigger.ts` (`setId: string | null`;
ohne Set kein `startImportFlow`-Zweig — der Schritt liefert dann nur `'restore'`) + `.spec.ts`;
`web/src/app/shared/seven-tv/import-source-dialog.ts` (`ImportSourceDialogData.setId: string | null`;
Türen „Kanal" und „Bestenliste" bei `null` deaktiviert mit `import.source.noTargetSet` — sichtbar,
nicht ausgeblendet, das Idiom aus Spec-200 8.6) + `.spec.ts`; `web/src/app/shared/seven-tv/restore-progress-section.ts`
(neu, + `.spec.ts`: die drei Restore-Blöcke aus `MassDeletePanel` — Notizen, Fortschritt mit
`RunProgressPanel` inkl. `syncReportReason`, Zielzeile aus `setName`/`ownerOrChannelLabel` nach dem
Muster der Import-Zielzeile; rendert nichts ohne Lauf oder Notiz, wie `ImportProgressSection`);
`web/src/app/shared/seven-tv/mass-delete-panel.ts` (gibt die Blöcke ab; `restoreService` bleibt
injiziert für den Knopf und die Sperre `arbiter.activeRun()`) + `.spec.ts`; `web/src/app/features/voting/vote-session-detail-page.html`
(Section neben dem Panel, hinter `!isCoarse()` — Abschnitt 6, Nr. 9); `web/public/i18n/{de,en}.json`
(`import.source.noTargetSet`, `restore.targetLine.*`); `docs/UI-Designsprache.md` (Dock-Abschnitt,
§7.3).

**Grenzfälle:** Seite ohne aktives Set (Kanal vor dem ersten Sync, oder nach einem Replace ins
Ungetrackte) ⇒ Trigger sichtbar, Türen deaktiviert, Rückweg-Datei lesbar, Dock sichtbar (AK 33) ·
Kanalwechsel mit fertigem Restore-Lauf ⇒ Section verschwindet (AK 20, T7s `resetIfChannelChanged`)
· laufender Restore, Nutzer wechselt das Set im Dropdown ⇒ Section bleibt (außerhalb des Gates) ·
Vote-Session-Seite: Restore aus dem Delete-Lauf ⇒ Section dort sichtbar · Touch (`isCoarse()`) ⇒
weder Trigger noch Section (unverändert).

**Tests:** `restore-progress-section.spec.ts` **+5** (die Anzeige-Fälle, die
`mass-delete-panel.spec.ts` bisher trug, inhaltsgleich: Notizen, Zielzeile getrackt/ungetrackt,
Grundzeile, „wird abgeglichen"; nichts ohne Lauf). `mass-delete-panel.spec.ts` **−n** (dieselben
Fälle). `import-source-dialog.spec.ts` **+2** (Türen bei `null` deaktiviert mit Grund; Datei-Tür
offen). `import-trigger.spec.ts` **+1** (`setId: null` öffnet den Dialog). `usage-stats-page.spec.ts`
**+2** (AK 33–34).

**Abnahme:** `grep -n "restoreService\." web/src/app/shared/seven-tv/mass-delete-panel.ts` findet
nur noch den Einstieg (Knopf, Sperre, Vorprüfung), keine Anzeige; kein neuer Button im Header.
**AK 33, 34; 4.4 Punkte 12–13.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/**/*.spec.ts' --include='src/app/features/usage-stats/usage-stats-page.spec.ts' --include='src/app/features/voting/*.spec.ts'`;
`npm --prefix web run build` (beide Host-Seiten kompilieren mit der neuen Komponente);
`npm --prefix web run e2e` (voll, nur ohne Api auf `:5151` — der Umzug berührt jede Seite, die das
Dock zeigt); Lint, Format.

**Commit:** `feat(restore): open the file entry without a selected set and dock restore runs outside the set gate`.
**Abhängigkeiten:** T5 (`hostSelectedSetId` nullbar im Schritt), T7 (Anzeigefelder am
Laufdatensatz). **Modell:** `sonnet` — ein Umzug von etwa dreißig Template-Zeilen und zwei Gates;
das Risiko ist Vollständigkeit, und die hält die inhaltsgleiche Spec-Übernahme (2.4).

### T10 — E2E über den ganzen Weg, Doku-Reste

**Ziel:** Die drei neuen Fälle aus Spec 9.4 durch den Browser; die Mocks tragen alles, was die
Vorprüfung und die zwei Routen brauchen; die Doku-Stellen, die kein Code-Task trug.

**Vertrag:** Spec 9.4, AK 1, 7, 18 (Request-Hälfte), 19, 33–34; Spec 12.2 (`docs/Feature-Ideen-2026-08-01.md`
nur prüfen — keine Idee betroffen, ohne Treffer keine Änderung; `CLAUDE.md` keine Änderung
erwartet).

**Dateien:** `web/e2e/emote-import.e2e.spec.ts` (drei neue Fälle), `web/e2e/support/mocks.ts`
(nur falls T7s Helfer nicht reichen), `web/e2e/audit/ui-audit.audit.ts` (prüfen, ob das Szenario
`usage-stats-import-confirm-dialog` noch die Sperre erwartet — dann anpassen).

**E2E (+3):** (1) Übertragungsdatei `finished` mit einem bestätigten REMOVE und ungetracktem Ziel,
eingelesen auf der Nutzungsseite eines **fremden** Kanals; `mockEmoteSetTargets` mit einem
ungetrackten Account, dessen Set `editable: true` ist; 7TV-GraphQL-Stub ⇒ Bestätigung nennt Set,
Set-ID, Besitzer und den Fremd-Hinweis ⇒ genau ein `addEmote` an das Ziel-Set ⇒ `POST
/api/seventv/emote-sets/{set}/sync-restored` mit `{ sevenTvEmoteIds, expectedChannelName: null }`
⇒ kein `POST /resync` (AK 1, 7, 18, 19, 21). (2) Kanalseite **ohne** aktives Set (`mockWorkspace`
ohne `activeEmoteSetId`) ⇒ Import-Einstieg sichtbar, Kopier-Knopf nicht, Kanal-/Bestenlisten-Tür
deaktiviert ⇒ Übertragungsdatei wiederhergestellt, Dock sichtbar (AK 33–34). (3) Purge-Protokoll
eines anderen Sets **desselben** Kanals ⇒ Bestätigung mit Fremd-Hinweis und „nicht aktiv"-Zeile
(AK 2, 19). `page.clock` vor `goto`, `runFor` statt `fastForward` (CLAUDE.md Tests), falls ein
Fall Timer braucht. Zählung der Suite am Ende im Bericht (Stand 2026-08-30: 96; seit #230 mehr).

**Abnahme:** die volle E2E-Suite grün ohne Api auf `:5151`; jeder Test, der einen Delete, Replace
oder Restore startet, mockt die Zielliste (F9 — der Task prüft das mit einem Grep über
`sync-deleted|sync-restored|removeEmote` gegen `mockEmoteSetTargets` je `test(`-Block).

**Gates:** `npm --prefix web run e2e` (voll); Lint, Format.

**Commit:** `test(e2e): restore a transfer file on a foreign channel page and without a selected set`.
**Abhängigkeiten:** T1–T9. **Modell:** `opus` — die drei Fälle schneiden durch Vorprüfung, Datei,
Bestätigung, Lauf und Meldung, und ein E2E-Fall, der an der falschen Stelle grün ist (Memory:
„Rote E2E-Fälle = Speicherdruck"; „Selbstprüfung am falschen Ort"), ist teurer als jeder Unit-Test.

### T11 — Volle Gates, Coverage, Zweitmeinung

**Ziel:** Die Fertigmeldung nach `CLAUDE.md` („Fertig heißt …") über den ganzen Branch und die
unabhängige Zweitmeinung vor dem PR.

**Schritte:** (1) `dotnet test EmotePurge.slnx` (Docker läuft), `npm --prefix web test --
--watch=false`, `npm --prefix web run e2e` — **nur ohne Api auf `:5151`** (wer gerade T12
vorbereitet hat, beendet `dotnet run` erst); `npm --prefix web run lint`, beide Formatter mit
`--verify`-Semantik. (2) `node scripts/coverage-local.mjs` — die Zahl ist eine dateigenaue
Näherung, in beide Richtungen unscharf; bei < 80 % nachsehen, ob es `restore-progress-section.ts`
oder `sync-report-outcome.ts` sind (neue Dateien messen nah an Sonar); Memory: das Skript misst nur
Committetes, Exit 0 ist keine Entwarnung, und es ist von den Sonar-Ausschlüssen abgedriftet.
(3) `/codex:review --model gpt-6-sol --scope branch --base feat/emote-sets-200` — von der
Hauptsession gestartet (Memory: ohne Rückfrage, `--scope branch` erzwingen, läuft im Session-CWD),
einmal je Branch, Ergebnis unverändert dem Betreiber vorlegen (Regel 22). Widersprüche zwischen
Opus-Review und Codex gehen an Fable als Schiedsrichter (global). (4) Ergebnisse ins Ledger.

**Commit:** keiner, außer ein Befund verlangt einen `fix:`; dann Schritt (1) für die betroffene
Suite wiederholen und Codex nur bei geändertem Diff erneut. **Abhängigkeiten:** T10. **Modell:**
`haiku` für die Gate-Läufe (mechanisch); die Codex-Zweitmeinung ruft die Hauptsession. **AK 25
(Gate-Hälfte).**

### T12 — Live-Verifikation (Regel 16, Betreiber-Handgriff) und Abschluss

**Ziel:** Die neun Punkte aus Spec 11 mit Zahlen im PR-Text; danach die Issue-Pflege.

**Aufbau:** Haupt-Checkout, Api per `dotnet run --project src/EmotePurge.Api` (`:5151`,
User-Secrets gesetzt — Memory), `npm --prefix web start`; danach `dotnet run` beenden, bevor E2E
läuft. Zweitkonto ohne `editor_of` auf olafs Konto für Punkt 6 (Plan-230 T10 nennt die Konten).
Für Punkt 5 (Mismatch erzwingen) den Dev-Worker anhalten, **nicht** den Prod-Worker; für Punkt 8
den `curl` mit dem Session-Cookie aus dem Browser — der Subagent bereitet beide Kommandozeilen mit
Platzhaltern vor. Kein Handgriff dieses Tasks verbindet sich mit `vps` oder `nas`.

**Zu belegen:** Spec 11, Punkte 1–9, wortgleich — der Plan wiederholt sie nicht. Zusätzlich als
Regressionsprobe: ein Add-only-Import in das aktive Set der eigenen Seite (Meldungen und Resync wie
vor dieser Runde, `sync-imported` unverändert — Spec 6.5).

**Danach (Betreiber):** PR gegen `feat/emote-sets-200` mit den Zahlen; #253 schließt mit dem Merge
in den Epic-Branch (von Hand, wie alle Kind-Issues); **#224** schließen mit Verweis auf Spec 5.5
und 6.4 (kein Erfolg ohne Papierspur, 404 endet in `failed`); **#256** um Punkt 3 kürzen, nicht
schließen (Memory: `gh issue edit` scheitert an Projects-Classic — Body per `--json` lesen, per
REST-`PATCH` schreiben); **Epic #200** bekommt die Kind-Zeile „Restore pro Set — #253, PR #…" und
den Verweis auf Spec und Plan (Memory: unvollständiges Epic macht seine Zusage kaputt). Keine
Ticketnummern in Commit-Metadaten.

Der Betreiber führt die Handgriffe aus; ein Subagent (`sonnet`) bereitet Read-back-Abfragen,
Erwartungswerte, den `curl` und die Issue-Kommandos vor und schreibt die Befunde in den PR-Text.
**Kein Commit**, außer ein Fund verlangt einen `fix(…)`. **AK 25 (Live-Hälfte); Spec 11 Punkte
1–9 decken AK 1, 2, 6, 7, 8, 17, 18, 21, 23, 27, 28, 31 (Negativprobe), 33 live.**

---

## 4. Nachverfolgung der Akzeptanzkriterien

| AK | Inhalt (Kurzform) | Tasks |
|---|---|---|
| 1 | Übertragungsdatei mit ungetracktem Ziel auf fremder Kanalseite ⇒ Bestätigung mit Set, ID, Besitzer; Lauf gegen `meta.targetEmoteSetId` | T5, T6, T10 (E2E 1), T12 (2) |
| 2 | Purge-Protokoll eines anderen Sets nicht abgewiesen; „nicht aktiv"-Zeile bei getracktem nicht-aktivem Ziel | T5, T6, T10 (E2E 3), T12 (4) |
| 3 | `targetNotEditable` im Banner, kein Dialog-Schluss, kein 7TV-Request | T4, T5, T5 (E2E-Anpassung), T12 (6) |
| 4 | `targetCheckUnavailable` bei degradierter Liste, 429, 503 | T4, T5 |
| 5 | `targetNotSelectable` bei `kind !== 'NORMAL'` | T4, T5 |
| 6 | Höchstens ein Zielliste-Request je 60 s; Bestätigung löst keinen zweiten aus; Fehler nie gecacht | T4, T5, T6, T8, T12 (1) |
| 7 | Restore-Meldung set-zentrisch mit `expectedChannelName`, IDs dedupliziert; kein kanalgebundener Request mehr | T7, T10 (E2E 1), T12 (2) |
| 8 | Delete-Meldung (beide Seiten) und Replace-Löschmeldung set-zentrisch mit `expectedChannelName` | T7, T8, T7 (vote-ballot E2E), T12 (1, 7) |
| 9 | Leiter 401/400/400/400/404/403/503 ohne Service, Audit, Resync; Policy `Bookkeeping` | T2 |
| 10 | Aktives Set eines getrackten Kanals: Zeilen, Audit mit Kanal, Antwort mit Zähler und `notFoundIds` | T2 |
| 11 | Geteiltes Set: beide Kanäle | T2 |
| 12 | Gesperrter Kanal weder berührt noch genannt; als erwarteter Kanal `notTracked` | T2 |
| 13 | Ungetrackt / nicht-aktiv: keine Zeile, ein Papier-Eintrag mit Besitzer, `channels` leer | T2 |
| 14 | `channel.synced` je Kanal mit Änderung, sonst keines | T2 |
| 15 | Dreiwertigkeit mit Grund in allen drei Diensten; 404 nie `succeeded` | T4 (Klassifikation), T7 |
| 16 | Replace für ungetracktes Ziel wählbar; keine Regel 7; kein Wurf in `startImport` | T8 |
| 17 | Replace ins Ungetrackte verlangt Rückweg-Datei, meldet set-zentrisch; Audit ohne Kanal „für <owner>" | T8, T12 (1, 3) |
| 18 | Restore dieser Datei ⇒ `syncRestored` ohne Kanal im Audit | T10 (E2E 1, Request-Hälfte), T12 (3) |
| 19 | Fremd-Hinweis beim Set-Vergleich, nicht für das gewählte Set | T6, T10 (E2E 1, 3) |
| 20 | Fertiger Restore-Lauf verschwindet beim Kanalwechsel; Meldung trotzdem gesendet | T7, T9 |
| 21 | Client-Resync nur für nicht-aktives Set eines getrackten Kanals und nur ohne `resyncTriggered`-Treffer; keiner für ungetrackt | T7, T10 (E2E 1), T12 (4) |
| 22 | `wrongChannel`, `wrongSet`, `emote_set_id_empty`, Regel-7-Schlüssel existieren nirgends mehr; `api-error-locales.spec.ts` grün | T3 (`emote_set_id_empty`), T5 (`wrongChannel`/`wrongSet`), T8 (Regel 7) |
| 23 | Altform: alte Antwort, keine Zeilenänderung, `legacyBodyForm: true`, Resync unter Cooldown; set-scoped Body ⇒ 400 | T3, T12 (8) |
| 24 | Bestehende `purge-run`-Dateien zeilen-identisch gelesen | T5 |
| 25 | Alle vier Gates grün plus Live-Verifikation mit Zahlen | T11, T12 |
| 26 | Je getroffenem Kanal genau ein `TryBeginAsync`, bei Erfolg `TriggerResyncAsync`; `resyncTriggered` = `Triggered`-Kanäle | T2 |
| 27 | Zwei Meldungen binnen 60 s ⇒ ein Resync; Client stößt für genannten Kanal keinen an | T2, T7, T12 (8) |
| 28 | `activeSetDiffers` ⇒ Mismatch, Papier mit `unresolved*`, Resync; fehlend/inaktiv/gesperrt ⇒ `notTracked` ohne Resync | T2, T12 (5) |
| 29 | Zielliste mit `sevenTvUserId`, `ownerSevenTvUserId`, `editable`; Set ohne Besitzer `false`; Picker deaktiviert mit Grund | T1, T4 |
| 30 | `editable` und `CheckAsync` über dieselbe reine Funktion; Owner-Lookup-Fall als einzige Asymmetrie | T1 |
| 31 | Vorprüfung vor der Delete-Bestätigung; Scheitern ⇒ keine Bestätigung, Grund, kein Request | T8, T12 (6) |
| 32 | Vorprüfung vor einem Plan mit Replace-Zeile; ohne Replace keine | T8 |
| 33 | Seite ohne gewähltes Set: Einstieg sichtbar, Türen deaktiviert, Datei lesbar, Dock sichtbar | T9, T10 (E2E 2), T12 (9) |
| 34 | Kopier-Knopf ohne Set unsichtbar; kein neuer Button | T9, T10 (E2E 2) |
| 35 | Bestätigung zeigt die aufgelöste Set-ID, nie einen unbestätigten Dateiwert | T5, T6, T10 (E2E 1) |

**Spec-Abschnitte ↔ Tasks:** 4.1 → T5, T9 · 4.2 → T4, T5 · 4.3 → T6 · 4.4 → T7, T9 · 4.5 → T8 ·
4.6 → T6 (Punkt 22), T7 (Punkt 21), T8 (Punkt 20) · 4.7 → T2 · 4.8 → T12 (Issue-Pflege; #254,
#255, #201 unberührt) · 5.1–5.5 → T2 · 5.6 → T3 · 5.7 → T1 (DECISIONS 3) · 5.8 → T1, T4 · 6.1 →
T5 · 6.2 → T4 · 6.3 → T6 · 6.4 → T7 · 6.5 → T7, T8 (Wächter) · 6.6 → T8 (Regel 7), T9 (Einstieg,
Section) · 7 → als Grenzfälle auf T2, T4, T5, T6, T7, T8, T9 verteilt · 9.1 → T1, T2, T3 · 9.2 →
T1, T2, T3 · 9.3 → T4–T9 · 9.4 → T5, T7, T8 (Anpassungen), T10 (neu) · 11 → T12 · 12.1 → T1, T2,
T5, T8 · 12.2 → T1 (Spec-200-Nachtrag), T3 (Architectur.md, Filter-Kommentar), T5 (§7.3), T9
(Dock, §7.3), T10 (Feature-Ideen prüfen), T12 (Issues) · 15 → Abschnitt 6 · 17 → Abschnitt 7.

---

## 5. Reihenfolge und Abhängigkeiten

```
Welle 1   Backend-Lane: T1 ──► T2              Frontend-Lane: T4
                               │                              │
Welle 2                        │                              ▼
                               │                             T7
Welle 3                        ▼                              ▼
                              T3                             T6
Welle 4                        │                              ▼
                               │                             T5
                               │                              ▼
                               │                             T8
Welle 5                        │                              ▼
                               │                             T9
Welle 6                        └──────────────────────────► T10
Welle 7                                                      T11
Welle 8                                                      T12
```

- **Welle 1 (parallel, zwei Worktrees):** Backend-Lane T1 → T2 sequenziell (T2 braucht T1s
  Worktree, nicht sein Symbol) ∥ Frontend-Lane T4 (gegen Spec 5.3/5.8, Naht 2.1). Preflight vor
  dem Merge der Welle: Feldnamen T2 ↔ T4.
- **Welle 2:** T7 (nach T4; die Routen aus T2 werden gemockt). Checkpoint mit voller Typprüfung
  und voller Vitest-Suite (Codex-Befund 2).
- **Welle 3 (parallel, zwei Worktrees):** T3 (Backend, nach T2 **und T7** — die set-scoped
  Kanalform fällt erst, wenn kein Frontend-Aufrufer mehr auf ihr steht, Codex-Befund 1) ∥ T6
  (Frontend, nach T7 — `startRestore` mit `RestoreStartTarget`; nach T4). Gemeinsame Dateien nur
  `de.json`/`en.json` (T3 entfernt einen `apiError`-Schlüssel, T6 fügt Bestätigungszeilen hinzu —
  Textkonflikt, trivial).
- **Welle 4 (seriell, Codex-Befund 5):** T5 (nach T6, T4), danach T8 (nach T7, T4, T5). Beide
  ändern `web/e2e/emote-import.e2e.spec.ts` und setzen je einen DECISIONS-Eintrag an den Anfang
  von `docs/DECISIONS.md`; parallel liefe das auf zwei Konflikte, deren Auflösung Review-Arbeit
  ohne Erkenntnis wäre. Preflight: Locale-Familien nach 0.5.
- **Welle 5:** T9 (nach T5, T7).
- **Welle 6:** T10 (nach allem).
- **Welle 7:** T11. **Welle 8:** T12.

**Grüne Zwischenstände:** zwischen T2 und T7 stehen beide Meldeformen nebeneinander — die
kanalgebundene set-scoped Form hat weiter ihre drei Aufrufer, die set-zentrische noch keinen;
zwischen T7 und T6 ist der Build grün (T7 passt den einen `startRestore`-Aufruf in
`restore-flow.ts` und den direkten im `MassDeletePanel` an, Naht 2.5); zwischen T6 und T5 ebenso
(Zwischenstand im `ImportTrigger`). Es gibt keine Branch-interne Unstimmigkeit mehr: die
set-scoped Kanalform lebt, bis T7 ihre Aufrufer umgestellt hat, und fällt erst in T3 (Welle 3).

---

## 6. Festlegungen des Plans, wo die Spec schweigt oder unscharf ist — entschieden (Orchestrator, 2026-09-25), Betreiber-Veto möglich

Nichts hier ist still aufgelöst: jede Zeile nennt die Stelle, den Befund, die Festlegung des Plans
und ihren Stand. Der Orchestrator hat alle Punkte am 2026-09-25 angenommen; der Betreiber sieht sie
parallel und kann widersprechen — ein Veto ändert genau den genannten Task. Die zwei reinen
Spec-Wortlautfehler (Nr. 1, Nr. 6) sind in `6ec22dc5` direkt in der Spec korrigiert.

| # | Stelle | Befund | Festlegung des Plans | Stand / Task |
|---|---|---|---|---|
| 1 | Spec E10, Klammer „(core → shared ist erlaubt)" | Die Richtung war vertauscht: `shared/` darf aus `core/` importieren, nicht umgekehrt (CLAUDE.md Schichtentreue). Die Aussage selbst — der Schritt in `shared/` injiziert `SevenTvEmoteSetService` aus `core/` — ist richtig | Der Plan liest es als `shared → core` | **Erledigt:** Spec-Wortlaut in `6ec22dc5` korrigiert; kein Task betroffen |
| 2 | Spec 4.2 Tabelle vs. 6.2 | Zeile 4 der Tabelle („Set nicht bearbeitbar gefunden **und** … oder der Request scheitert") ist grammatisch mehrdeutig; 6.2 ist eindeutig: `editable` gewinnt, wenn das Set bearbeitbar gefunden wurde, auch bei degradierter Liste; `unavailable` gilt für jeden nicht-bearbeitbaren Befund bei unvollständiger Liste | T4 implementiert 6.2 in dieser Reihenfolge: `notSelectable` → `editable` → `unavailable` (Liste unvollständig oder Request gescheitert) → `notEditable` | **Entschieden** (Orchestrator), Veto möglich; T4 |
| 3 | Spec E16, 4.6 Punkt 22 | Die Spec verlangt die Vorprüfung auch am Panel-Einstieg, sagt aber nicht, **wo** ein Scheitern angezeigt wird — der Flow hat keinen Banner, der Datei-Schritt schon | T6 nutzt die bestehende Abbruchnotiz des Panels (`abortNotice`) mit einer Restore-Leadzeile und der Familie `restore.errors.*` | **Entschieden**, Veto möglich; T6 |
| 4 | Spec 4.5 Punkt 17, 4.6 Punkt 20 | Die Gründe für Delete- und Replace-Abbruch sind benannt (`targetNotEditable`/`targetCheckUnavailable`), ihre Locale-Schlüssel nicht; 6.1 legt nur die Familie des Datei-Schritts fest | Je Anzeigeort die bestehende Fehlerfamilie des Laufs (`massDelete.errors.*`, `import.errors.*`), je drei Gründe (auch `notSelectable`, obwohl im Normalfall unerreichbar) | **Entschieden**, Veto möglich; T8 (Wortlaut #255) |
| 5 | Spec E23, 6.5 | `syncReportReason` ist Vertrag, der Ort der Texte nicht | Eine gemeinsame Familie `syncReportReason.*` — der Grund hängt nicht an der Laufart | **Entschieden**, Veto möglich; T7 |
| 6 | Spec 5.6, „Was entfällt" | Die Spec strich `TargetIsActiveSetOfChannel` aus den Legacy-DTOs, schwieg zu `NewlyArchivedCount`/`NewlyRestoredCount` — die nach E4 immer 0 sind und keinen Leser mehr haben (kein Live-Event) | T3 streicht beide mit; die Spec-200-Folge-Issue 1 räumt die Route später ganz ab | **Erledigt:** Spec 5.6 in `6ec22dc5` ergänzt; T3 |
| 7 | Spec 5.6 Punkt 1 und 4 | Bei fehlender Kanalzeile „kein Eintrag" — ob Stufe 7 (Resync) dann läuft, sagt die Spec nicht | T3 lässt Stufe 7 nach jeder 200-Antwort laufen; `TriggerResyncAsync` antwortet `NotFound`, der Cooldown wird freigegeben — dasselbe Verhalten wie `POST /resync` für einen unbekannten Kanal | **Entschieden**, Veto möglich; T3 |
| 8 | Spec 4.5 Punkt 15 | „`ResolutionContext` … bleibt als leeres Objekt oder fällt, das entscheidet der Plan" | Fällt ganz: `validateResolution`, `buildTransferPlan` und `collisionStepRows` verlieren den Parameter — kein Aufrufer reicht einen Kontext ohne Bedeutung weiter | **Entschieden**, Veto möglich; T8 |
| 9 | Spec 6.6, letzter Satz | Die Vote-Session-Seite „bindet die neue Section ebenso ein" — ihr Panel steht hinter `results() && canSelectForDelete() && !isCoarse() && massDeletePanelSetId()`; wo die Section steht, ist offen | Neben dem Panel, nur hinter `!isCoarse()`, weil die Section sich selbst auf den Lauf gated und ein laufender Restore die Ergebnisse überleben muss; dazu der `imports`-Eintrag in beiden Host-Seiten (Codex-Befund 3) | **Entschieden**, Veto möglich; T9 |
| 10 | Spec 6.2, AK 6 | „Der Picker liest dieselbe Kopie" ist eine Verhaltensänderung: der Picker kann bis zu 60 s alte Listen zeigen (heute lädt er je Öffnen frisch); `reload` umgeht sie | Übernommen wie in der Spec; im Task-Bericht von T4 ausdrücklich genannt | **Entschieden** (Orchestrator, ausdrücklich inklusive dieses Punkts), Veto möglich; T4 |
| 11 | Spec 9.1 | „Substitut `IRedisPublisher`" — `ApiFactory` substituiert heute `IConnectionMultiplexer`; ob `IRedisPublisher` darüber schon testbar ist, entscheidet der Blick in die Fabrik | T2 prüft und ergänzt das Substitut nur, wenn nötig | **Entschieden**; T2 |
| 12 | Plan 0.4 der ersten Fassung | Die erste Fassung setzte T3 direkt hinter T2 und nahm ein 400-Fenster für die set-scoped Kanalform bis T7 in Kauf | **Zurückgenommen (Codex-Befund 1):** T3 läuft nach T7 (Welle 3); DECISIONS-Eintrag 2 entsteht in T2 ohne den Altform-Absatz, T3 hängt ihn im eigenen Commit an — der Eintrag stimmt zu jedem Commit-Stand | **Entschieden**; T2, T3, Abschnitt 5 |
| 13 | Spec 15 (E24, E22, F16) | Die drei Veto-Stellen der Spec | Der Plan baut die Spec-Fassung; jede Alternative trifft genau einen Task (E24 → T3, E22 → T9, F16 → T1) | Kein Veto vor Beginn nötig |

---

## 7. Rückweg

Wie Spec 17: kein Schema, keine Migration; Backend und Frontend gehen **zusammen** zurück (ein
Frontend mit set-zentrischen Meldungen gegen ein Backend ohne die Routen verlöre die Papierspur; ein
Backend mit den Routen ohne Aufrufer ist harmlos). Die Guid-Altform kehrt mit dem Revert zur
zeilenändernden Form zurück — der heutige Produktionsstand. Eine Übertragungsdatei eines Replace in
ein ungetracktes Set bleibt nach einem Revert lesbar, aber **nicht mehr einlesbar** (der revertierte
Parser weist sie mit `wrongChannel` ab); wer revertiert, revertiert deshalb nicht zwischen einem
solchen Replace und seinem Restore — dieselbe Regel wie Plan-230 §8. Bereits geschriebene
Papier-Einträge ohne Kanal und Einträge mit `legacyBodyForm`/`unresolved*` bleiben lesbar.

---

## 8. Ledger

`.superpowers/sdd/Plan-253-Restore-pro-Set/progress.md` (gitignoriert, wie bei Plan-230; das Gerüst
liegt im Worktree). Je Welle: Preflight der Nähte aus Abschnitt 2, Dispatch mit BASE-SHA und Modell,
Bericht, Review, Rulings mit „cost if wrong", Merge-SHA. Die Codex-Zweitmeinung (T11) und die
Live-Befunde (T12) landen ebenfalls dort, bevor sie in den PR-Text wandern.

---

## 9. Nachtrag: Codex-Adversarial-Review über die erste Fassung (gpt-6-sol, 2026-09-25)

Fünf Befunde über `b5f4098f`, alle vom Orchestrator angenommen und in dieser Fassung eingearbeitet.
Codex hat Stichproben genannt, keine vollständige Liste — die vollständige Aufrufer-Inventur, die
daraus folgte, steht in 0.6 und hat weitere Stellen gefunden (Ziellisten-Fixtures in drei
Spec-Dateien, die sieben Importe von `SyncReportState`, den `resyncNoticeKey`-Abgleich für den
neuen `ResyncTriggerState`-Wert, zwei DTO-Konstruktoren in `AuthFilterMatrixTests`, die
`collisionStepRows`-Signatur und ~25 Aufrufe mit `ResolutionContext`, vier Kommentarstellen).

| # | Schwere | Befund | Lösung | Wo |
|---|---|---|---|---|
| 1 | high | Zwischen T3 (Altform-Umbau, set-scoped Kanalform entfernt) und T7 (Frontend-Umstellung) hätte die kanalgebundene Route dem alten Frontend mit 400 geantwortet; die Meldung folgt auf die 7TV-Mutation, ein Live-Lauf verlöre Buchung und Audit | T3 rückt hinter T7 (Welle 3); die set-scoped Kanalform bleibt, bis kein Aufrufer mehr auf ihr steht. DECISIONS-Eintrag 2 entsteht in T2 ohne den Altform-Absatz, T3 hängt ihn im eigenen Commit an | 0.4, T2, T3, Abschnitt 5, Abschnitt 6 Nr. 12 |
| 2 | high | T7 übersah direkte Aufrufer der geänderten Dienst-Signaturen (`mass-delete-panel.ts` ruft `startRestore` direkt; `seven-tv-run-arbiter.spec.ts` ruft beide alten Signaturen) | Beide in die T7-Dateiliste; T7-Checkpoint mit `npm --prefix web run build` (volle Typprüfung — `npx vitest` prüft keine Typen) und voller Vitest-Suite; Aufrufer-Regel in 0.4, Inventur in 0.6 | 0.4, 0.6, T7 |
| 3 | high | T9 registrierte `RestoreProgressSection` nicht in den `imports`-Arrays der beiden Host-Seiten | `usage-stats-page.ts` und `vote-session-detail-page.ts` in der T9-Dateiliste; Build-Gate | T9, Abschnitt 6 Nr. 9 |
| 4 | medium | T5 zählte zu wenige Fixtures: `import-trigger.spec.ts` baut sechs typisierte Restore-Ergebnisse ohne Ziel | Alle Restore-Result-Fixtures (Datei-Schritt, Trigger, Quelldialog) und ihre Assertions in der T5-Dateiliste; Build-Gate | T5, 0.6 |
| 5 | medium | Welle 4 teilte mehr Dateien, als der Plan sagte: T5 und T8 ändern beide `emote-import.e2e.spec.ts` und setzen je einen DECISIONS-Eintrag an den Anfang | T5 → T8 seriell | Abschnitt 5, T5, T8 |
