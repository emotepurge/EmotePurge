# Plan #384 — Importierte Backfill-Daten im UI entfernen

Erstellt am 2026-10-13 im Worktree `EmotePurge-384` (Branch `feat/384-remove-imported-backfill`, Basis `origin/main` @ `b11bf239`,
Spec-Commit `840f6eeb`).
**Status: Revision 1 (13.10.) — adversariale Sol-Prüfung des Plans (4 × medium) eingearbeitet; §10 Punkte 1–7 vom Orchestrator
angenommen, Punkt 8 liegt beim Nutzer.**

### Revision 1 (13.10.) — Sol-Befunde, am Code geprüft

| Befund | Prüfung | Änderung |
|---|---|---|
| Sol-1: Post-Cancel-Gate kann einen **vor** dem Abbruch begonnenen GET als „Post-Cancel-Read“ werten; `reload()` wird abgelehnt, solange ein Load läuft | bestätigt: Angulars `ResourceImpl.reload()` gibt `false` zurück, solange der Status `loading` ist (`idle`/`loading`; `reloading` bricht dagegen den alten Load ab). `loading` tritt bei gleichem Kanal nach einem fehlgeschlagenen Refetch auf (kein Wert mehr in der Resource, das zurückgehaltene `status` zeigt noch den Lauf). Alle sechs `statusResource.reload()`-Aufrufe in `backfill-section.ts` ignorieren den Rückgabewert | 3. „Post-Cancel-Read“ neu gefasst; Vitest-Fall in 4.; T4-Akzeptanz |
| Sol-2: S3 verlangt `GetCoverageAsync`, T2 verbietet `ChatLogBackfillService` | bestätigt: einzige Implementierung `ChatLogBackfillService.cs:328`, Konstruktor mit `SlotReconcileOptions` (Teil B entfernt ihn) | S3-Leserassertions nach T7 (4.) |
| Sol-3: laufender Worker verfälscht Vorher/Nachher (Flush alle 30 s, REST-Abgleich settelt Seeds) | bestätigt (`UsageFlushWorker`, `SevenTvPeriodicResyncWorker`) | 7.: Worker vor der Messung stoppen, nach den Prüfungen neu starten |
| Sol-4: `Source = 0` im verkürzten Fenster ⇒ `LiveRowConflict` ⇒ Lauf `failed` | bestätigt: `ReplaceBlockAsync` → `LiveRowConflict` (`ChatLogBackfillService.cs:558`), Worker → `FailOrAbandonAsync(… live_row_conflict …)` (`ChatLogBackfillWorker.cs:585-588`), keine Zellen-Auslassung | 7.2: kollisionsfreies Fenster ist Vorbedingung; ohne abgeschlossenen Import gilt der Live-Fluss als blockiert |
Quelle und einzige Vertragsquelle: [die Spec](../superpowers/specs/2026-10-13-remove-imported-backfill-spec.md) (§1–§17, D1–D15,
Nutzerentscheide §14, Sol-Befunde §14.5). Daneben: [Plan-379](Plan-379-Emote-Slots-Rollout.md) §3/§5 (Sweep-Prädikat, auf dem Branch
`feat/379-flag-removal`), [Plan-375](Plan-375-Emote-Slots-Backfill.md) §8 (Live-Muster), `docs/Operations.md` „Chat-log backfill“ →
„Rolling back“. Formatvorlage: Plan-375/Plan-379.

**Was dieser Plan trägt:** nur, was die Spec offen lässt — Schnitt in Aufgaben, Reihenfolge gegenüber #379 Teil B, Präzisierungen der
Verträge, Testzuordnung, Commit-Schnitt, Live-Ablauf mit Fallen. Kein Code: Typ- und Methodennamen stehen nur als Form, SQL nur als
Prädikat in Worten. D1–D15 und §14 werden nicht wieder aufgemacht.

**Lesehinweis:** **[E]** Planer-Entscheidung · **[P]** Präzisierung, die der Spec-Text offen lässt, ohne ihm zu widersprechen ·
**[L]** Lücke/Widerspruch der Spec, hier vorläufig gelöst und in Abschnitt 10 zur Bestätigung gelistet.

---

## 0. Befund vor dem Plan

1. **Keine Migration** (Spec §3.3, AK 19). Alles nötige Schema steht: `UsageStats.Source`/`SlotId`, `ChatLogBackfillCoverage`,
   `EmoteSlots.SeedUnsettled`. Kein Task darf eine Datei unter `src/EmotePurge.Infrastructure/Migrations/` anlegen oder ändern;
   T10 prüft das per `git diff --stat origin/main -- src/EmotePurge.Infrastructure/Migrations/` (leer) und einer Probe
   `dotnet ef migrations add Probe384` gegen ein leeres Ergebnis (danach verwerfen).
2. **#379 Teil B läuft parallel** (Worktree `EmotePurge-379b`, Branch `feat/379-flag-removal`, PR B frühestens 2026-10-14). Stand
   heute: sechs Test-Commits, uncommittete Änderungen u. a. an `ApiErrorCodes.cs` (Kommentar im Block „Chat-log backfill“, genau
   dort, wo #384 zwei Codes ergänzt), `SeedSettleWrites.cs` (Kommentar), `SevenTvSyncService.cs`, `ChatLogBackfillServiceTests.*`.
   Teil B entfernt `SlotReconcileOptions` — damit ändert sich der **Konstruktor** von `ChatLogBackfillService` — und `SnapshotChanged`
   (Ergebnis von `ReplaceBlockAsync`). Die Sweep-Konstante entsteht erst in Teil B (Plan-379 Aufgabe B5), ihr Name steht noch nicht
   fest; sie liegt neben `EmoteSlotRollbackSql` in **`src/EmotePurge.Infrastructure/Migrations/`** (nicht `Services/`).
3. **Fertige Bausteine**, die #384 nur benutzt (nie ändert): `ChannelQueries.LoadChannelForNoKeyUpdateAsync` (normalisiert, Kanal
   `FOR NO KEY UPDATE`, „während des Wartens gelöscht“ liest sich als `null`, kein Filter auf `IsBotActive` — passt zu D11),
   `RowLocks.LockEmotesByIdsAsync` (ein Statement, `COLLATE "C"`, offene Transaktion Pflicht), `DatabaseErrors.IsDeadlock`,
   `AppDbContext.AddAuditEntry`, `LiveEvents.BackfillProgress`/`UsageFlushed`, `IRedisPublisher`. Die bequeme
   `PublishChannelEventAsync`-Erweiterung liegt im **Worker** (`LiveEventPublisher.cs`) und ist für die Api nicht erreichbar; der
   neue Service bekommt seinen eigenen fehlertoleranten Publish nach dem Muster `ChatLogBackfillService.PublishSafelyAsync` (Muster
   übernehmen, nicht die Teil-B-Datei anfassen und keinen gemeinsamen Helfer daraus extrahieren).
4. **Frontend-Bestand:** `backfill-section.ts` (737 Zeilen, Inline-Template) trägt `statusResource`, das zurückgehaltene `status`,
   `cancelRefetchBaseline`/`awaitingPostCancelRead`, Request-Tokens als `linkedSignal` und den permanenten `role="status"`-Bereich,
   dessen Inhalt `announcement` als `linkedSignal` über `status` berechnet wird. E2E: `installLiveStub`/`emitLive` können SSE-Frames in
   die App schieben; `mockImportCoverage` und die Caption-Fälle der Nutzungsseite liegen in `web/e2e/usage-atlas.e2e.spec.ts`.
5. **Prod:** `ChatLogBackfill:Enabled` ist dort aus; die Route antwortet nach dem Deploy 404 `backfill_disabled`. Keine
   Prod-Handlung außer dem normalen Image-Deploy (Abschnitt 8).

## 1. Umfang

### 1.1 Drin
Alles aus Spec §3.1: Service samt SQL-Klasse und Erwartungsvergleich, Route, zwei Codes (Regel-7-Kette), Audit-Aktion und
Detail-Art (Server und Frontend), SSE nach Commit, UI-Abschnitt „Importierte Daten“ mit Dialog und Sperrlogik, Tests (Infrastructure,
Api, Vitest, E2E), Doku (Operations, DECISIONS, Feature-Ideen A17, CLAUDE.md), Live-Ablauf.

### 1.2 Draußen
Spec §3.2 unverändert: je Set/je Lauf, Platzhalter und gesettelte `CreatedSlot`-Slots, Lauf-/Snapshot-Zeilen, Retention, Worker,
Status-DTO, fremde SQL-Konstanten, der Nebenbefund `ReplaceBlockAsync` (Spec §2.4). Zusätzlich **[E]**: die Statuszeile der
Backfill-Spec `2026-10-09-chat-log-backfill-spec.md` bleibt unangetastet (Spec §15: Teil B markiert dort Zeilen historisch).

### 1.3 Dateien, die #384 anfasst — Teil-B-Berührungen fett

| Datei | Task | Art |
|---|---|---|
| `src/EmotePurge.Core/Services/IChatLogImportRemovalService.cs` | T2 | neu: Interface, Ergebnis, Status-Enum, `ExpectedCoverageInterval` |
| `src/EmotePurge.Infrastructure/Services/ChatLogImportScope.cs` | T1 | neu, rein: Vergleich Erwartung ↔ aktuelle Coverage (2.3) |
| `src/EmotePurge.Infrastructure/Services/ChatLogImportRemovalSql.cs`, `ChatLogImportRemovalService.cs` | T2 | neu |
| **`src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs`** | T2 | **eine** Registrierungszeile neben `IChatLogBackfillService`; Teil B entfernt im selben File die `SlotReconcileOptions`-Bindung |
| `src/EmotePurge.Core/Entities/AuditLogEntry.cs`, `src/EmotePurge.Core/Services/IAuditLogQueryService.cs`, `src/EmotePurge.Infrastructure/Services/AuditLogQueryService.cs` | T2 | Aktion + Detail-Art + Projektion |
| `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs` | T3 | Route + Request-Records |
| **`src/EmotePurge.Api/Validation/ApiErrorCodes.cs`** | T3 | zwei Konstanten im Block „Chat-log backfill“; Teil B ändert dort den Kommentar zu `BackfillSlotsUnsettled` (Nachbarzeilen) |
| **`tests/EmotePurge.Api.Tests/ApiFactory.cs`** | T3 | eine Substitution; Teil B kann Flag-Reste entfernen (prüfen beim Rebase) |
| `tests/EmotePurge.Api.Tests/AuthFilterMatrixTests.cs`, `EmoteRoutePolicyTests.cs`, neu `ChatLogImportRemovalEndpointTests.cs` | T3 | Matrix, Policy, Handler |
| `tests/EmotePurge.Infrastructure.Tests/Unit/ChatLogImportScopeTests.cs` | T1 | neu |
| `tests/EmotePurge.Infrastructure.Tests/Integration/ChatLogImportRemovalServiceTests.cs` (+ Teilklasse `….Backfill.cs`), `Fixtures/ImportedBackfillSeed.cs`, `Integration/AuditLogQueryServiceTests.cs` | T2, T7 | neu bzw. eine Theorie-Zeile |
| `web/src/app/core/channels/backfill.service.ts`/`.spec.ts`, `backfill.model.ts` | T4 | Methode, Typen |
| `web/src/app/features/channel-workspace/backfill-section.ts`/`.spec.ts` | T4 | Abschnitt, Logik |
| `web/src/app/core/i18n/api-error.ts`, `web/public/i18n/de.json`/`en.json` | T3 (Codes), T4 (UI, Audit) | Regel 7, Texte |
| `web/src/app/shared/audit/audit-actions.ts`, `web/src/app/core/audit/audit.model.ts` | T4 | Aktion/Detail-Art **[L]** (Spec §15 nennt `audit.model.ts` nicht) |
| **`web/e2e/support/mocks.ts`**, `web/e2e/channel-settings.e2e.spec.ts`, `web/e2e/usage-atlas.e2e.spec.ts` | T5 | Mocks am Dateiende anfügen; Teil B ändert Kommentare `mocks.ts:1244-1254` |
| **`docs/Operations.md`** | T6, T8 | neuer Unterabschnitt + Einleitung „Rolling back“ (T6); Schritt 4/3b (T8, nach Rebase) — Teil B ändert den 3b-Hinweis `~:1037` und den Slot-Abschnitt |
| **`docs/DECISIONS.md`** | T2, T3, T4, T8 | **ein** neuer Eintrag oben; Teil B ergänzt „Child 9“ weiter unten — nur Kopf-Konflikt |
| `docs/Feature-Ideen-2026-08-01.md`, `CLAUDE.md` | T6 | Statuszeilen |

**Nie angefasst** (gilt für jeden Task): `ChatLogBackfillService.cs`, `IChatLogBackfillService.cs`, `ChatLogBackfillWorker.cs`,
`ChatLogBackfillSnapshotMap`, `SevenTvSyncService.cs`, `SeedSettleWrites.cs`, `EmoteSlotRollbackSql.cs`, die Teil-B-Sweep-Konstante,
`ChatLogBackfillServiceTests.*`, `ChatLogBackfillWorkerTests`, die Backfill-Spec, appsettings/`.env.example`/Compose, `Migrations/`.
Lesen und in Tests **aufrufen** ist erlaubt.

## 2. Backend-Verträge (Präzisierungen zu Spec §5)

### 2.1 Core (T2)
- `IChatLogImportRemovalService` mit genau einer Methode wie Spec §5.1 (Kanalname, Erwartungsliste, `AuditActor`, Token → Ergebnis).
- Ergebnis als Record mit Status-Enum (`Removed`, `NothingToRemove`, `RunActive`, `ScopeChanged`, `NotFound`) und den drei Zählern;
  statische Fabriken für die zählerlosen Fälle wie bei `ChatLogBackfillEnqueueResult`. Nur `Removed` trägt Zähler ≠ 0.
- `ExpectedCoverageInterval(From, ToExclusive, EmoteSetId, ArchiveHost)` — reiner Record, kein Interface (Regel 5).
- Audit: `AuditActions.BackfillRemoveImported = "backfill.removeImported"` neben den beiden Backfill-Aktionen;
  `AuditLogDetail.Kinds.BackfillRemovedDays = "backfillRemovedDays"`; `AuditLogQueryService` projiziert die Art in der Count-Form
  direkt hinter `BackfillMonths` (gleiche Lesehilfe, gleiche Whitelist-Logik).

### 2.2 Ablauf im Service (T2) — Spec §5.3 Schritte 1–12, nur Ergänzungen
- **Wiederholung [P]:** Schleife um die ganze Transaktion, höchstens drei Versuche, nur bei `DatabaseErrors.IsDeadlock`; zwischen den
  Versuchen Change-Tracker leeren und eine kurze Zufallspause (wie `ChannelService` beim Broadcaster-Purge: 20–150 ms); Warn-Log je
  Wiederholung. Der dritte Deadlock geht als Ausnahme nach oben → 500 (Problem), nichts geschrieben.
- **Reihenfolge der Statements** genau wie Spec §5.3: Kanal sperren → aktiver Lauf → betroffene Emote-Ids (Prädikat: Emote des Kanals,
  das mindestens eine `UsageStats`-Zeile mit `Source = 1` trägt) → Nichts-da-Prüfung (Emote-Menge leer **und** keine Coverage-Zeile des
  Kanals) → Erwartungsvergleich (2.3) → Emote-Sperre → Schritt 7 → Schritt 8 → Schritt 9 → Audit + `SaveChanges` → Commit → Publish →
  Log. Schritt 7 und 8 sind **zwei getrennte Statements** (Spec §5.3 Schritt 8 begründet das; ein Implementer, der sie „optimiert“,
  bricht AK 7).
- **SQL-Klasse [P]:** `ChatLogImportRemovalSql` (internal static, `Services/`) hält die Texte der Schritte 7, 8, 9 als Konstanten mit
  benannten Parametern (Kanal-Id; Kandidatenliste als Array). Schritt 7 liefert je `SlotId` die Zahl gelöschter Zeilen (datenverändernde
  CTE + Gruppierung); Zeilen mit `SlotId = NULL` kann es nach M2 nicht geben — falls doch, zählen sie mit und sind kein Kandidat.
  Prädikat Schritt 8 wörtlich in Worten: *Slot-Id in der Kandidatenliste UND `SeedUnsettled` UND keine `UsageStats`-Zeile mit dieser
  `SlotId`* — kein Prädikat auf `CreatedSlot`, `IsArchived`, `ArchivedAt`, Set oder Kanal.
- **Leere Kandidatenliste:** Schritt 8 wird übersprungen (`RemovedSeeds = 0`), kein Statement.
- **Publish:** nur bei `Removed`, nach Commit, zuerst `backfill.progress`, dann `usage.flushed`, beide mit `ChannelName.Normalize` des
  **gesperrten** Kanalnamens (nicht des Request-Namens); jede Ausnahme außer Abbruch wird geloggt und geschluckt.
- **Logs (englisch):** Info bei `Removed` (Kanal, drei Zähler, Dauer in ms **[E]** — die Dauer ist die Dev-Zahl aus Spec §6.8);
  Info bei `ScopeChanged` (Kanal, erwartete und aktuelle Tageszahl). Keine Logzeile bei `NothingToRemove`/`RunActive` außer Debug.

### 2.3 Erwartungsvergleich `ChatLogImportScope` (T1) — **[L]** Expansion ohne Obergrenze
Spec §5.3 Schritt 5 expandiert die Erwartung in Tage. Die Route begrenzt nur die **Anzahl** der Intervalle (≤ 400), nicht ihre
**Spannweite**: ein Intervall `0001-01-01 … 9999-12-31` würde ~3,6 Mio. Tupel je Intervall erzeugen. **[E] Vergleich ohne
Expansion:** reine Funktion über zwei Eingaben — die Erwartungsintervalle und die aktuellen Coverage-Tage als `(Day, EmoteSetId,
ArchiveHost)` (höchstens ~190 Zeilen). Semantik exakt wie Spec (Tagesmengen-Gleichheit), Vorgehen:
1. Erwartung nach `From` sortieren; zwei Intervalle mit gemeinsamem Tag (egal welches Set/Host, auch Duplikate) ⇒ **ungleich**
   (Überlappung, Spec Schritt 5). Ein Intervall mit `From ≥ ToExclusive` ⇒ ungleich (die Route lässt es nie durch; die Funktion ist
   trotzdem total).
2. Aktuelle Tage sortieren und zu maximalen Läufen zusammenfassen (aufeinanderfolgende Tage mit gleichem Set **und** Host).
3. Erwartung ebenso zusammenfassen (angrenzende Intervalle mit gleichem Set/Host verschmelzen — deckt „andere Zerlegung“, S17).
4. Gleich ⇔ beide kanonischen Listen sind elementweise gleich (Datum, Set ordinal, Host ordinal).
Rückgabe: gleich/ungleich plus erwartete und aktuelle Tageszahl (fürs Log; die erwartete Zahl als `long`, um bei absurden Spannen nicht
überzulaufen). Unit-Tests (container-frei, `Unit/ChatLogImportScopeTests.cs`): alle S17-Varianten, leer/leer, leer/voll, voll/leer,
Duplikat, Überlappung über Setgrenze, ein riesiges Intervall (endet schnell, ungleich), Groß-/Kleinschreibung von Set-Id ist **nicht**
gleich (ordinal, wie die DB).

### 2.4 Route (T3) — Spec §5.2, Ergänzungen
- **Request-Records [P]:** äußerer Record mit einer nullable Liste, Element-Record mit vier nullable Feldern (`from`, `to` als
  `DateOnly?`, `emoteSetId`, `archiveHost` als `string?`) — Muster `StartBackfillRequest`, damit fehlende Felder ein übersetzbarer Code
  sind und kein Bindungsfehler.
- **400 `invalid_request_body` [L]** zusätzlich zur Spec-Liste für: ein `null`-Element in der Liste, ein Element mit fehlendem Feld,
  ein leerer `emoteSetId`/`archiveHost`. **Keine** Formatprüfung der Set-Id und des Hosts **[E]**: eine falsche Angabe kann nur zu
  `ScopeChanged` führen, nie zu einer Löschung. Längenobergrenze je String (z. B. 200) **[E]**, damit der Vergleich nicht beliebig große
  Strings hält.
- **Reihenfolge** wie Spec: Flag → Akteur → Body → Service. **[P]** Ein *nicht bindbarer* Body (kaputtes JSON, falsches Datumsformat)
  scheitert schon in der Parameterbindung, also vor Filter und Flag, mit 400 `invalid_request_body` über den globalen Handler — wie bei
  der Start-Route heute (`UnbindableBodyTests`). AK 1 „Flag aus vor dem Body“ gilt für bindbare Bodies.
- Abbildung: `Removed`/`NothingToRemove` → 200 mit genau `removedUsageRows`, `removedCoverageDays` (camelCase, kein `removedSeeds`);
  `RunActive` → 409 `backfill_remove_run_active`; `ScopeChanged` → 409 `backfill_remove_scope_changed`; `NotFound` → 404
  `channel_not_found`; unbekannter Wert → `UnreachableException` wie beim Cancel.
- Filter/Policy: `ChannelManagementAuthorizationFilter`, `RateLimitPolicyNames.Bookkeeping`. **Kein neuer `IEndpointFilter`, keine
  geänderte Filterreihenfolge** einer Gruppe — Regel 11 greift nur über die Matrix-Einträge.
- Regel 4: der Handler bekommt nur `IChatLogImportRemovalService`, `ChatLogBackfillOptions`, `HttpContext`.

## 3. Frontend-Verträge (Präzisierungen zu Spec §10)

- **Service:** `removeImported(channelName, coverage)` nimmt die Intervalle aus `status.coverage` und schickt `from`, `to`,
  `emoteSetId`, `archiveHost` — `emoteSetName` wird **ausgelassen**, nicht als `null` gesendet. Antworttyp `BackfillRemovalResult`.
- **Eine Quelle [P]:** `removalScopeKey` (Kanal + kanonische Intervallzeilen in Status-Reihenfolge) ist zugleich Eingabe für den
  Dialogtext und für die gesendete Erwartung; beim Bestätigen wird die Erwartung aus dem **beim Öffnen festgehaltenen** Intervall-Array
  gebaut und nur gesendet, wenn der aktuelle Schlüssel gleich ist (Muster `startPlanKey`).
- **Request-Token `removeRequest`** als `linkedSignal` wie `cancelRequest`; `removing` = Token gesetzt. `canStart` wird um `!removing`
  erweitert, `canRemove` wie Spec §10.4. Der Start-Knopf ist außerdem gesperrt, solange `removing`.
- **Post-Cancel-Read (Sol-1) [E]:** Als Post-Cancel-Read zählt nur ein Status-Read, der **nach** der 204-Antwort des Abbruchs
  begonnen hat. Das Gate merkt sich deshalb nicht den Resource-Wert (Vergleich „Wert hat sich geändert“ öffnet auch, wenn ein vorher
  begonnener GET mit `running` landet), sondern eine Read-Generation: Die Komponente bekommt **einen** Reload-Weg für alle sechs
  heutigen Aufrufer plus die neuen, der den Rückgabewert von `reload()` auswertet. Wird der Reload abgelehnt (Resource lädt gerade),
  merkt er sich „noch einen Read nachholen“ und startet ihn, sobald der laufende Load fertig ist (Antwort oder Fehler). Das Gate öffnet
  erst, wenn ein Read mit einer Generation ≥ der beim 204 vergebenen abgeschlossen ist. Damit kann ein alter GET das gepatchte
  `activeRun: null` zwar kurz überschreiben, das Gate bleibt aber zu, und der nachgeholte Read stellt den Stand nach dem Abbruch her;
  ebenso geht ein vom SSE ausgelöster, abgelehnter Reload nicht verloren. Betrifft `canStart` und `canRemove` gleichermaßen — der
  Bestand hat dieselbe Lücke, T4 behebt sie für beide im selben Commit und nennt das im DECISIONS-Absatz.
- **Fehlertext `removeErrorKey` [P]:** gehört zum Kanal, geleert beim nächsten Versuch und bei Erfolg. Solange `activeRun` existiert,
  zeigt der Abschnitt den Sperrgrund statt eines Fehlertexts (sonst stünden nach einem 409 `run_active` zwei gleichbedeutende Sätze
  untereinander). Abbildung der Antworten wie Spec §10.4; der clientseitige Schlüssel-Mismatch setzt denselben Schlüssel wie der Server-
  409 `scope_changed`.
- **Ansage [L]:** der vorhandene `role="status"`-Bereich zeigt heute nur `announcement` (aus `status` abgeleitet; jede Status-Änderung
  rechnet ihn neu). Die Erfolgsansage braucht einen eigenen Zustand `removalAnnouncement` (Tageszahl), der **im selben** Live-Bereich
  angezeigt wird und Vorrang vor der Lauf-Ansage hat, bis ein neuer aktiver Lauf erscheint oder der Kanal wechselt. Er muss den Refetch
  überleben, der den Abschnitt entfernt (der Live-Bereich liegt außerhalb des Abschnitts). Plural über `pluralKey`.
- **Datum:** Anzeige-Ende = `to − 1 Tag`, formatiert wie die Lauf-Fenster (vorhandene Hilfsfunktion der Komponente benutzen, nicht neu
  schreiben); `countingSince` im Dialog aus `status.countingSince`.
- **Markup:** eigene `<section aria-labelledby>` mit `h3`, nach „Letzter Lauf“, im selben Komponententemplate; Knopf `appButton="danger"`
  mit `aria-describedby` auf den Sperrgrund; Fehler als `app-notice-banner variant="error"`. Gestalt nach `docs/UI-Designsprache.md` wie
  der Backfill-Abschnitt — keine neue Optik.
- **Audit-Anzeige:** `audit.model.ts` (Unions `AuditAction`, `AuditDetailKind`), `audit-actions.ts` (`ACTION_KEYS`, `DETAIL_KEYS`), de/en
  `audit.actions.backfillRemoveImported`, `audit.details.backfillRemovedDays` mit Pluralformen wie `backfillMonths`. Der Aktivitätsfilter
  erbt die Aktion über `CHANNEL_SCOPED_ACTIONS`.
- **Nutzungsseite:** kein Eingriff erwartet (Spec §10.6). Bricht T5 dort etwas (z. B. „Gesamt“-Preset nach leerer Coverage), behebt T5
  es im selben PR mit eigenem `fix(web):`-Commit und meldet es dem Orchestrator.

## 4. Tests — Zuordnung zu Tasks

| Spec-Fall | Ort | Task | vor/nach Rebase |
|---|---|---|---|
| Vergleich (Teil von S17) | `Unit/ChatLogImportScopeTests.cs` | T1 | vor |
| S1–S5, S6 (a)–(f), S7, S8, S9, S14, S15, S16, S17 — S3 dabei nur über die Tabellen (`Source = 1` = 0, Coverage-Zeilen = 0, Zähler) | `Integration/ChatLogImportRemovalServiceTests.cs` | T2 | vor |
| S3 Leser: `GetCoverageAsync(AllSets)` und `(Set)` leer | `ChatLogImportRemovalServiceTests.Backfill.cs` | T7 | **nach** (Sol-2) |
| S12 Rennen gegen `SeedSettleWrites.MoveSeedUsageAsync` | dto. | T2 | vor (ruft den Helfer nur auf; bei Signaturänderung durch Teil B Nacharbeit in T7) |
| S8 Projektion | `AuditLogQueryServiceTests` (eine `InlineData`-Zeile) | T2 | vor |
| Retry: erzwungener Deadlock (Muster `LockRace_OppositeKeyOrderDeadlock_IsRetried…`) → zweiter Versuch gelingt, Zähler stimmen | `ChatLogImportRemovalServiceTests.cs` | T2 | vor |
| S10 Enqueue, S11 Block eines abgebrochenen Laufs (beide Varianten), S13 neuer Lauf danach | `ChatLogImportRemovalServiceTests.Backfill.cs` | T7 | **nach** — bauen `ChatLogBackfillService`, dessen Konstruktor und `ReplaceBlockAsync`-Ergebnis Teil B ändert **[L]** |
| S6 Zusatz: Prädikat Schritt 8 = Teil-B-Sweep-Konstante auf den Kandidaten | dto. | T7 | **nach** (Konstante existiert erst dann) |
| Api: Matrix, Policy, Handler | Api.Tests | T3 | vor |
| Vitest Service + Section | web | T4 | vor |
| E2E Flow + Nutzungsseite | web/e2e | T5 | vor |

**S3-Leser nach T7 (Sol-2) [E]:** `GetCoverageAsync` gibt es nur in `ChatLogBackfillService`. Statt T2 einen zweiten Fabrikhelfer
mit dem Teil-B-Konstruktorargument zu erlauben (zwei Stellen, die beim Rebase brechen), prüft T2 den Zustand, aus dem der Leser
rechnet (Coverage-Tabelle des Kanals leer), und T7 — das den Service ohnehin über **einen** Fabrikhelfer baut — ergänzt die beiden
Leser-Assertions. Abhängigkeit: AK 2/S3 sind erst nach T7 vollständig.

**Fixture [E]:** `Fixtures/ImportedBackfillSeed.cs` (neu) baut die Zustände per EF/SQL direkt — Kanal, Emotes, Slots (gesettelt,
unsettled, `CreatedSlot`), Läufe mit Snapshots (`BackfillRunSeed` darf benutzt werden), Coverage-Tage, Zeilen `Source = 0/1` — und
liefert die Hash-/Zählhilfen für „bytegleich“ (alle Spalten aller betroffenen Zeilen, sortiert nach `Id`). Eigene Datenbank je
Testklasse wie die bestehenden Integrationstests. Rennen nur über `PostgresLockProbe` (getaggte Kontexte, `WaitUntilBlockedOnLockAsync`),
nie über `Task.Delay`.

**Was jeder S-Fall prüft, steht in Spec §11.1** — die Tasks schreiben je Fall einen Test (Theorien, wo die Spec Varianten aufzählt:
S4 je Status, S17 je Abweichung). Für S4/S5/S17 zusätzlich: Publisher-Substitut hat nichts empfangen, Audit-Tabelle unverändert.
S9 „Redis-Ausfall“: Publisher wirft → Ergebnis `Removed`, Daten gelöscht. S2 vergleicht beim Nachbarkanal **alle** fünf Tabellen
(`UsageStats`, Coverage, `EmoteSlots`, Läufe, Snapshots) per Hash.

**Api.Tests (T3):** `ApiFactory` substituiert `IChatLogImportRemovalService`. In `AuthFilterMatrixTests` kommt die Route in beide
401/403-Theorien und in `BackfillRoutes`; weil die drei Bestandsrouten einen gemeinsamen Body schicken, bekommt die Theorie eine
Body-Spalte **[P]** (für die neue Route eine gültige Erwartung, für die alten der bisherige Body) — die 403-Theorie prüft zusätzlich
`DidNotReceiveWithAnyArgs` auf dem neuen Service. `EmoteRoutePolicyTests`: Route → `Bookkeeping`. Neue
`ChatLogImportRemovalEndpointTests` mit allen Fällen aus Spec §11.2 plus den [L]-Fällen aus 2.4 (null-Element, fehlendes Feld, leere
Strings, zu lange Strings), Flag aus vor Body nur mit bindbarem Body.

**Vitest (T4, Regel 12 — Verhalten, keine Vorlage):** Spec §11.3 vollständig, zusätzlich: **Sol-1** — ein Refetch ist fehlgeschlagen,
ein neuer GET (vor dem Abbruch begonnen) hängt, Abbruch → 204, danach landet der alte GET mit `running`: `canRemove` und `canStart`
bleiben falsch, der nachgeholte Read wird gestartet, und erst seine Antwort (ohne Lauf) öffnet beide; Variante: der SSE-Reload
während des hängenden GET geht nicht verloren; Ansage überlebt den Refetch, der die Coverage
leert; Kanalwechsel während `removing` verwirft die späte Antwort; nach Server-409 `scope_changed` und neuem Status zeigt ein neuer Klick
den neuen Dialogtext. Keine Assertions auf CSS-Klassen, Tags oder Übersetzungswortlaut.

**E2E (T5):** Spec §11.4. Flow-Fälle in `channel-settings.e2e.spec.ts`; der Nutzungsseiten-Fall **[L]** in `usage-atlas.e2e.spec.ts`
neben den vorhandenen Caption-Fällen (dort leben `mockImportCoverage` und die Caption), ausgelöst über `emitLive` mit
`backfill.progress`, Import-Coverage-Mock vorher gefüllt, danach leer. Importe aus `web/e2e/support/test.ts`. Neue Mock-Helfer
(`mockBackfillRemove` o. ä., zeichnet Bodies auf) **am Ende** von `mocks.ts`, nicht in der Nähe von `:1244-1254`.

## 5. Doku

- **DECISIONS (englisch, ein Eintrag oben, Datum des ersten Commits):** Titel nach Spec §16. Wächst mit den Commits (Regel 3):
  T2 legt ihn an (D2, D5–D9, Sperrreihenfolge Kanal → Emotes → Zeilen, Schritt 7/8 getrennt, Retry, keine Migration §3.3, warum F2 (B)
  online nicht sicher ist, Audit-Art), T3 ergänzt Route/POST statt DELETE/Codes/D3/D15-Serverseite, T4 ergänzt die Client-Bindung
  (Post-Cancel-Gate, Schlüssel), T8 ergänzt den Satz zur Sweep-Äquivalenz mit dem Namen der Teil-B-Konstante. `**Betrifft:**`-Zeile
  wächst entsprechend (alle neuen Dateien plus `ChannelEndpoints.cs`, `ApiErrorCodes.cs`, `AuditLogEntry.cs`, `AuditLogQueryService.cs`,
  `api-error.ts`, `backfill-section.ts`, beide Locales, `docs/Operations.md`).
- **Operations (englisch), T6 vor dem Rebase:** neuer Unterabschnitt „Removing imported data (channel managers)“ unter „Chat-log
  backfill“, **vor** „Rolling back“ (Inhalt Spec §16 erster Punkt); in „Rolling back“ nur der Einleitungssatz aus Spec §16.
- **Operations, T8 nach dem Rebase:** Schritt 4 je-Kanal-Variante: Verweis auf die UI-Aktion + Seed-Hinweis mit dem echten Namen der
  Sweep-Konstante; Schritt 3b: Satz, dass ein von der UI-Aktion entfernter `CreatedSlot`-Seed dort nicht mehr auftaucht — beides
  direkt an den Stellen, die Teil B zuletzt geändert hat, deshalb erst nach dem Rebase.
- **Feature-Ideen A17 (deutsch), T6:** Tabellenzeile (`:39`) und Statuszeile (`:502ff.`) um „Entfernen im UI (#384)“.
- **CLAUDE.md (deutsch), T6:** Zeile „Chat-Log-Backfill“ im Umsetzungsstand: ein Halbsatz „Manager können die importierten Daten
  eines Kanals im Settings-Tab wieder entfernen (#384)“.
- **Epic #346 (GitHub, T10, durch den Orchestrator):** Tabellenzeile/Statuszeile für #384 im Issue-Body (Memory-Regel „neue Issues
  gehören ins Epic“; REST-PATCH statt `gh issue edit`, falls Projects-Classic-Fehler).

## 6. Reihenfolge gegenüber #379 Teil B

```
T1 → T2 → T3 → T4 → T5        (T6 parallel zu T4/T5 möglich: nur Doku)
          ── warten auf Merge PR B ──
R  Rebase auf origin/main (Orchestrator) → T7 → T8 → T9 → T10
```

- **Vor dem Rebase (T1–T6):** alles, was nur eigene Dateien plus die genannten Einzeilen-Berührungen trifft. Der Code ist für
  „Slot-Flag an“ korrekt und liest `SlotReconcileOptions` nicht (Spec §3.4). Tests von T2 seeden ihre Zustände direkt und brauchen
  kein Slot-Flag.
- **Rebase (R, Orchestrator, kein Subagent nötig, außer bei Konflikten):** erwartete Konflikte nur in `ServiceCollectionExtensions.cs`,
  `ApiErrorCodes.cs` (Nachbarzeilen), `ApiFactory.cs`, `DECISIONS.md` (Kopf), `Operations.md`, `mocks.ts`. Nach dem Rebase: Build +
  Api.Tests + gefilterte Infrastructure-Tests (`ChatLogImportRemoval*`, `ChatLogImportScope*`, `AuditLogQueryService*`) grün, bevor T7
  startet.
- **Nach dem Rebase (T7–T10):** Tests, die `ChatLogBackfillService` bauen oder die Sweep-Konstante brauchen; Operations an den
  Teil-B-Stellen; Live-Ablauf (Spec §13 setzt PR B voraus); Gates und PR.
- **Falls PR B sich verzögert [E]:** der Orchestrator darf T7 vorziehen, wenn die Konstruktion von `ChatLogBackfillService` in genau
  **einem** privaten Fabrikhelfer der neuen Teilklasse steckt (heute mit `SlotReconcileOptions { Enabled = true }`); der
  Sweep-Äquivalenztest bleibt dann bis nach dem Rebase offen und wird gegen das Plan-379-§3-Prädikat als Testtext geprüft (Spec §11.1
  S6 erlaubt das ausdrücklich). Der PR geht trotzdem erst nach PR B auf.

## 7. Live-Verifikation (Regel 16; T9, sonnet mit Orchestrator; Freigabe je Anlass)

**Randbedingungen.** Keine Prod-Verbindung, kein `ssh`. Geteilte Dev-DB `emotepurge` nur lesend; nie `docker compose`, nichts im
Haupt-Checkout. **Vorher mit der #379b-Sitzung abstimmen (Orchestrator):** `:5151`, `:4200`, Redis-Port, DB-Kopie-Name,
Schreibzugriffe auf `olaf_olaf_son` (erwartet: keine 7TV-Writes; der Ablauf liest 7TV und das Archiv). Alle Zahlen sind
**Dev-Zahlen** und werden so berichtet, nie als Prod.

| Ressource | Wert |
|---|---|
| DB | `emotepurge_384` im Container `emotepurge-dev-postgres`, per `createdb` + `pg_dump emotepurge \| psql emotepurge_384` **im** Container (nicht `createdb -T`); Zugangsdaten aus der Container-Umgebung, nie ausgeben |
| Migrationsstand | vor dem ersten Start `dotnet ef migrations list --project <abs>/src/EmotePurge.Infrastructure --startup-project <abs>/src/EmotePurge.Api --connection '…emotepurge_384…'` — erwartet: nichts `(Pending)`; sonst Abbruch und Rückfrage (kein `update` auf eigene Faust) |
| Redis | `docker run -d --name redis-384 -p 127.0.0.1:<port>:6379 redis:7.2-alpine` (Port abgestimmt) |
| Api | `dotnet run --project /home/dev/projects/EmotePurge-384/src/EmotePurge.Api` (absoluter Pfad), Env: `ConnectionStrings__DefaultConnection` → Kopie, `Redis__ConnectionString`, `ChatLogBackfill__Enabled=true`, `SevenTv__SlotReconcile__Enabled=true` (nach PR B ignoriert, schadet nicht), `SevenTv__EventApi__Enabled=false`, `Retention__Enforce=false`; Login-Phase auf `:5151` |
| Worker | `dotnet run --project /home/dev/projects/EmotePurge-384/src/EmotePurge.Worker`, gleiche Env + die Twitch-Secrets der Api als Env-Variablen (aus `dotnet user-secrets list` der Api bzw. `secrets.json` — **BOM entfernen**; Werte aus `.env` — **CRLF entfernen**) |
| Web | `npm --prefix /home/dev/projects/EmotePurge-384/web start` (Proxy auf `:5151`) |
| Browser | Playwright als Node-Skript mit `web/node_modules` (kein Chrome-MCP), Cookie aus der Datei injiziert |
| Dateien | Scratchpad: Cookie (Modus 600), SQL-Ausgaben, Antwort-JSONs, Protokoll |

**Ablauf.**
0. `df -h /`, Größe der Dev-DB; Abbruch, wenn der Platz die Kopie nicht doppelt trägt. PIDs aller gestarteten Prozesse notieren; vor
   jedem Signal `ps -o args= -p <pid>`, dann SIGTERM.
1. **Kopie vorbereiten (nur Kopie):** aktive Backfill-Läufe zählen; > 0 ⇒ auf der Kopie auf `cancelled` setzen (sonst setzt der
   Worker sie gegen `logs.cyex.app` fort) und die Zahl notieren. `IsBotActive = false` außer `olaf_olaf_son`. Sweep aus der
   Teil-B-Konstante auf der Kopie: zählen, ausführen, Zahl notieren — die Kopie entspricht danach dem Dev-Stand nach Teil B (Spec §13
   Voraussetzung). Inventar notieren: Kanäle mit `Source = 1`-Zeilen, davon solche mit unsettled Seeds, die nur `Source = 1` tragen
   (mit/ohne `CreatedSlot`).
2. **Kanal und Fenster wählen.** Hauptkanal `olaf_olaf_son`. **Fenster klein (nur Kopie):** `Channels.CreatedAt` von
   `olaf_olaf_son` so setzen, dass ein 1-Monats-Lauf genau **einen** 7-Tage-Block hat (Zählbeginn eine Woche nach `heute − 1 Monat`,
   Muster Plan-375 §8.1). **Vorbedingung (Sol-4): das Fenster ist kollisionsfrei** — kein `Source = 0`-Eintrag des Kanals an einem Tag
   des Fensters. Eine Zelle, die das Archiv-Aggregat mit einer Live-Zeile teilt (gleicher Slot, Set, Tag), endet nicht als
   übersprungene Zelle, sondern mit `LiveRowConflict` und `failed` für den ganzen Lauf. Liegen Live-Zeilen im Fenster, ein anderes
   Fenster wählen (anderer `CreatedAt`, ggf. 3 Monate mit entsprechend verschobenem Zählbeginn) — **nie** Live-Zeilen löschen.
   **S16 live** braucht einen Kanal mit Importen aus der Flag-aus-Zeit; ist das auf der Kopie `handofblood`, darf das Entfernen dort
   auf der Kopie `emotepurge_384` schreiben (Nutzerentscheid 13.10., §10 Punkt 8). Kein 7TV-Schreibzugriff auf `handofblood`, kein Prod.
3. **Anmelden:** Worktree-Api auf `:5151`, Login im Browser über `http://localhost:5151/api/auth/twitch/login`, Cookie in die
   Scratchpad-Datei. Das Cookie gilt nur für die Api **aus genau diesem Worktree** (Schlüsselring). Probe `GET /api/auth/me`.
4. **Import erzeugen:** 1-Monats-Lauf für `olaf_olaf_son` (aktives Set) über die UI oder `curl`; abwarten bis `completed`. Kommen aus
   dem Archiv keine Nachrichten, entsteht nur Coverage ohne Zeilen (S15 live) — notieren; der Zeilenpfad wird dann am S16-Kanal
   geprüft, falls freigegeben. **Lässt sich kein abgeschlossener Import herstellen** (kein kollisionsfreies Fenster, Lauf `failed`,
   Archiv nicht erreichbar) und gibt es keinen freigegebenen Kanal mit vorhandenem Import, gilt der Live-Fluss als **blockiert**: T9
   bricht ab, protokolliert den Grund und meldet an den Orchestrator — kein Ersatz durch handgeschriebene `Source = 1`-Zeilen.
5. **409-Pfade (Spec §13.4, Worker läuft):** zweiten Lauf starten → Knopf „Importierte Daten entfernen“ gesperrt mit Grund; `curl` mit
   aktueller Coverage als Erwartung → 409 `backfill_remove_run_active`. Lauf abbrechen; Knopf bis zum abgeschlossenen Post-Cancel-Read
   gesperrt (Playwright: Zustand direkt nach der 204-Antwort). `curl` mit um einen Tag gekürzter Erwartung (Coverage **nach** dem
   Abbruch frisch lesen — der abgebrochene Lauf kann einen Block committet haben) → 409 `backfill_remove_scope_changed`.
6. **Worker stoppen (Sol-3):** SIGTERM an den Worker (PID geprüft), auf das Prozessende warten (letzter Flush und Abschluss im Log);
   Api und Redis laufen weiter (Entfernen, SSE-Relay). Ab hier ändert nur noch das Entfernen die Daten — der 30-s-Flush und der
   periodische REST-Abgleich (Seed-Settle) würden sonst Hashes und Seed-Zahlen unabhängig verändern.
7. **Vorher-Messung (psql auf der Kopie), je Prüfkanal:** Zahl `Source = 1`; Zahl Coverage-Zeilen; Hash über alle Spalten aller
   `Source = 0`-Zeilen des Kanals (sortiert nach `Id`); Hash über **alle** `UsageStats` aller anderen Kanäle; Hash über Coverage,
   `EmoteSlots`, Läufe, Snapshots der anderen Kanäle; Zahl zeilenloser unsettled Seeds (Kanal, global); Zahl der unsettled Seeds des
   Kanals, die nur `Source = 1` tragen, davon mit `CreatedSlot`; Zahl der Platzhalter mit `CreatedRow`-Snapshot. Zur Kontrolle der
   Stille: dieselbe Messung nach einer Minute wiederholen — muss gleich sein.
8. **Nutzungsseite** des Kanals in einer zweiten Playwright-Seite öffnen (Caption mit Archiv-Link sichtbar, Screenshot).
9. **Entfernen im UI** per Playwright: Abschnitt öffnen, Dialogtext prüfen (Intervalle, Set, Bleibendes), bestätigen; Antwortzeit und
   die Log-Zeile der Api (Zähler, Dauer) notieren.
10. **Nachher-Messung:** `Source = 1` = 0, Coverage = 0, alle Hashes gleich, zeilenlose unsettled Seeds ≤ vorher, die Seeds aus 7.
    (nur `Source = 1`) sind weg, auch mit `CreatedSlot` (Zahl = `RemovedSeeds` im Log), Platzhalter/Läufe/Snapshots unverändert.
11. **Zweite Seite ohne Neuladen:** Caption „Wir zählen seit …“ ohne Archiv-Satz, Summen ohne importierte Tage (Screenshot).
12. **Aktivitäts-Tab:** Eintrag „Importierte Daten entfernt · n Tage“ (de) bzw. en.
13. **Idempotenz:** `curl` mit der alten Erwartung → 200 mit Nullen; Audit-Zahl unverändert.
14. **S16-Kanal** (nur mit Freigabe aus 2., Worker weiterhin gestoppt): Schritte 7, 9, 10 dort.
15. **Worker neu starten** (gleiche Env, neue PID notieren).
16. **Änderung hinter dem Dialog [P]** (Spec §13.10 verlangt einen zweiten Manager; ein zweites Konto gibt es nicht): neuen
    1-Monats-Lauf erzeugen und abschließen lassen → Abschnitt erscheint; Dialog öffnen, offen lassen; per `curl` mit demselben Cookie
    einen weiteren Lauf auf einem **anderen** Set starten und abwarten (ändert die Coverage); bestätigen → keine Anfrage bzw. 409,
    Hinweis auf geänderten Stand; neuer Klick zeigt den neuen Umfang. Geht das zweite Set auf der Kopie nicht (oder ist sein Fenster
    nicht kollisionsfrei), genügt der Vitest-Fall — im Protokoll vermerken.
17. **Neuer Lauf danach (S13 live):** nach erneutem Entfernen ein 1-Monats-Lauf auf demselben Set → läuft durch, keine doppelte
    `Emote`-Zeile, Caption kommt zurück.
18. **Abbau:** alle Prozesse SIGTERM (PID geprüft), `docker rm -f redis-384`, `DROP DATABASE emotepurge_384 WITH (FORCE)`,
    Cookie-Datei löschen, `:5151`/`:4200` an die #379b-Sitzung zurückmelden. Protokoll mit SHA, Dev-Zahlen, Laufzeiten; keine
    Passwörter, Tokens, Hostnamen; Kanalnamen nur `olaf_olaf_son` (und ggf. `handofblood`).

**Bekannte Fallen.** Startbefehle immer mit absolutem Pfad (die Bash-cwd springt zurück). Der Worktree hat **kein**
`web/node_modules` — vor T4, T5 und T9 einmal `npm --prefix /home/dev/projects/EmotePurge-384/web ci`. Worker ohne Twitch-Secrets startet, verbindet
aber nicht — BOM/CRLF sind die üblichen Ursachen. Cookie gilt nur für die Api dieses Worktrees; Login immer über `:5151`. Läuft auf
`:5151` noch die Api, darf die E2E-Suite nicht laufen (CLAUDE.md „Tests“). Im Warte-Loop kein `pgrep -f` auf ein Muster der eigenen
Kommandozeile. Archiv-Cooldown und `RequestDelaySeconds` gelten; keine zwei Läufe ohne Abstand. Dev-Zahlen nie als Prod-Zahlen in
Doku, DECISIONS oder PR.

## 8. Prod-Übergabe

Keine Migration, keine neue Konfiguration. Deploy-Hinweis im PR: „no migration — still run `dotnet ef migrations list` against prod
before the images (memory: check **all** pending migrations)“; die Route ist bei `ChatLogBackfill:Enabled=false` (Prod) 404
`backfill_disabled`. Das Einschalten des Backfills auf Prod bleibt eine getrennte Entscheidung (Spec Kopf, #379-Bewährung).

## 9. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; nacheinander, weil Builds `obj/` teilen — Ausnahme T6)

Jeder Task bekommt: diesen Plan, die Spec, `CLAUDE.md` (Regeln 3–7, 11, 12, 18, 19; Sprache), bei Web-Tasks `web/.claude/CLAUDE.md`.
Jeder Task committet selbst (Conventional Commits, englisch, letzte Zeile `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`),
staged Dateien **einzeln** (geteilter Index mit Parallel-Sitzungen), pusht nicht. „Lokale Gates“ = vor dem Commit grün.

| # | Aufgabe | Modell | vor/nach PR B | Akzeptanz (Spec) | Lokale Gates | Commit(s) | Darf nicht |
|---|---|---|---|---|---|---|---|
| **T1** | Reiner Erwartungsvergleich `ChatLogImportScope` (2.3) + Unit-Tests | sonnet | vor | S17 (Vergleichslogik), [L] Expansion | `dotnet build EmotePurge.slnx`; `dotnet test tests/EmotePurge.Infrastructure.Tests --filter ChatLogImportScope`; `dotnet format EmotePurge.slnx --verify-no-changes` | `feat(infrastructure): compare a confirmed backfill coverage with the current one by day` | DB berühren; Core ändern |
| **T2** | Core-Vertrag (2.1), Audit-Aktion/Art + Projektion, `ChatLogImportRemovalSql`, Service (2.2), DI-Zeile, Fixture, Integrationstests S1–S9, S12, S14–S17, Retry-Test, Projektionszeile; DECISIONS-Eintrag anlegen | **opus** | vor | AK 2 (ohne Leser, → T7), 3–11, 12 (S12, Retry), 14, 19 | Build; `dotnet test tests/EmotePurge.Infrastructure.Tests --filter "ChatLogImportRemoval\|ChatLogImportScope\|AuditLogQueryService"`; Format | (a) `feat(audit): name the removal of imported backfill data` (Aktion, Art, Projektion, Test); (b) `feat(infrastructure): remove a channel's imported backfill data under the channel lock` (Rest + DECISIONS) | Teil-B-Dateien aus 1.3 „Nie angefasst“; `ChatLogBackfillService` instanziieren (→ T7); Migration |
| **T3** | Route, Request-Records, Validierung (2.4), zwei Codes in `ApiErrorCodes`, Regel-7-Kette (`api-error.ts`, `errors.api.*` de/en mit Spec-§8-Texten), `ApiFactory`, Matrix, Policy, `ChatLogImportRemovalEndpointTests`; DECISIONS ergänzen | sonnet | vor | AK 1, 17; Spec §11.2 | Build; `dotnet test tests/EmotePurge.Api.Tests`; `npm --prefix web test -- --watch=false` (wegen `api-error-locales.spec.ts`); Format; `npm --prefix web run lint`; `npm --prefix web run format` | `feat(api): let channel managers remove a channel's imported backfill data` | `IEndpointFilter` anlegen oder Filterreihenfolge ändern; Kommentar zu `BackfillSlotsUnsettled` anfassen |
| **T4** | Frontend: Modell, Service + Spec, Abschnitt „Importierte Daten“, Dialog, Logik (3.), Ansage, i18n `backfill.remove.*`, Audit-Anzeige (`audit.model.ts`, `audit-actions.ts`, `audit.*` de/en), Vitest; DECISIONS ergänzen | **opus** | vor | AK 10 (Anzeige), 16; Spec §11.3; Post-Cancel-Read nach 3. (Sol-1) inkl. Vitest-Fall | `npm --prefix web test -- --watch=false`; lint; format; `npm --prefix web run build` (Typprüfung der Vorlagen — ein direkter `npx vitest`-Lauf überspringt sie) | (a) `feat(web): label imported-data removals in the activity log`; (b) `feat(web): remove a channel's imported backfill data from the settings tab` | Nutzungsseite ändern (außer Fund → melden); `usage-stats-page.ts` anfassen; neue Optik |
| **T5** | E2E: Flow, Sperre, 409, Nutzungsseite (4.), Mock-Helfer am Ende von `mocks.ts` | sonnet | vor | AK 15, 16 (E2E-Teil); Spec §11.4 | **vorher prüfen, dass auf `:5151` nichts lauscht**; `npm --prefix web run e2e`; lint; format | `test(e2e): cover removing imported backfill data and the usage caption afterwards` (+ ggf. `fix(web):` getrennt) | Kommentare `mocks.ts:1244-1254`; bestehende Mocks umbauen |
| **T6** | Doku vor dem Rebase: Operations-Unterabschnitt + Einleitungssatz „Rolling back“ (englisch), Feature-Ideen A17, CLAUDE.md-Zeile (deutsch) | sonnet | vor (parallel zu T4/T5 möglich) | AK 20 (Teil 1) | Markdown-Links prüfen (`grep` auf Anker) | `docs: describe removing imported backfill data for channel managers` | Schritt 3b/4 und Slot-Abschnitt von Operations (→ T8); DECISIONS; Backfill-Spec |
| **R** | Rebase auf `origin/main` nach Merge von PR B, Konflikte lösen | Orchestrator (bei Konflikten sonnet) | — | — | Build; Api.Tests; gefilterte Infrastructure-Tests wie T2 | keine eigenen | — |
| **T7** | `ChatLogImportRemovalServiceTests.Backfill.cs` mit **einem** Fabrikhelfer für `ChatLogBackfillService`: S10, S11 (beide Varianten), S13, S3-Leser (`GetCoverageAsync` leer, Sol-2); Sweep-Äquivalenz gegen die Teil-B-Konstante (Kandidatenmenge, beide Prädikate liefern dieselben Ids); ggf. S12 an Teil-B-Signatur anpassen | **opus** | nach | AK 2 (Leser), 7 (Äquivalenz), 12, 13 | gefilterte Infrastructure-Tests (`ChatLogImportRemoval`); Format | `test(infrastructure): race the imported-data removal against enqueue, a cancelled block and a new run` | Produktivcode ändern — ein roter Rennfall geht als Befund an den Orchestrator, nicht als stiller Fix |
| **T8** | Operations Schritt 4 (Verweis + Seed-Hinweis mit Konstantennamen) und 3b-Satz; DECISIONS: Satz zur Sweep-Äquivalenz | sonnet | nach | AK 20 (Teil 2) | Text gegen Spec §16 | `docs: point the per-channel rollback step at the settings action` | Teil-B-Text umformulieren (nur ergänzen) |
| **T9** | Live-Verifikation (7.) | sonnet + Orchestrator | nach | Spec §13, AK 15, 18 (Ballot/Export-Summen stichprobenartig) | Protokoll vollständig, Abbau erledigt | keiner (Protokoll in den PR-Text) | Prod; `docker compose`; Haupt-Checkout; 7TV-Writes |
| **T10** | Opus-Review des Branchs, Gates, Coverage, PR | **opus**; Codex Sol durch den Orchestrator | nach | AK 19, 21 | `dotnet test EmotePurge.slnx`; `npm --prefix web test -- --watch=false`; `npm --prefix web run e2e` (ohne Api auf `:5151`); `dotnet format EmotePurge.slnx --verify-no-changes`; `npm --prefix web run lint`; `npm --prefix web run format`; **erst committen, dann** `node scripts/coverage-local.mjs` ≥ 80 % auf den neuen Dateien; Migrations-Probe (0.1) | `fix:`/`test:` aus dem Review | mergen; pushen nur nach Freigabe des Orchestrators; Sol-Befunde umsetzen ohne Vorlage beim Nutzer |

**Nach T10 (Orchestrator):** `/codex:review --model gpt-6.1-sol` über den fertigen Branch, Befunde unverändert vorlegen; bei Widerspruch
Opus ↔ Sol Fable als Schiedsrichter (nur strittige Befunde). PR-Text: AK-Tabelle 1–21, Live-Protokoll (Dev-Zahlen), Deploy-Hinweis
(8.), Hinweis „no migration“. Epic #346 aktualisieren (5.).

**Grün-Strategie:** T1 ist rein und schnell; T2 bringt das gesamte Datenverhalten mit Tests in einem Zug (der Service hat ohne Route
keinen Aufrufer, das ist gewollt); T3 macht ihn erreichbar; T4/T5 die Oberfläche. Jeder Task lässt die Suiten seines Bereichs grün;
die Gesamt-Suite läuft in T10.

**Aufwand:** T1 0,25 · T2 1,25 · T3 0,5 · T4 1 · T5 0,5 · T6 0,25 · T7 0,5 · T8 0,1 · T9 0,5 + Betreiberzeit · T10 0,5 ≈ **5,3 Tage**
(Spec §17: 3,75). Mehr durch den Schnitt um PR B (T7/T8 getrennt), den Vergleich ohne Expansion und die zusätzlichen 400-Fälle.

## 10. Spec-Lücken und -Widersprüche

**Punkte 1–7: vorläufige Antworten vom Orchestrator am 2026-10-13 angenommen** (keine Nutzerentscheidung nötig). **Punkt 8: offen,
liegt beim Nutzer.**

1. **[angenommen 13.10.] Expansion ohne Obergrenze (§5.2/§5.3 Schritt 5):** Nur die Intervallzahl ist begrenzt, nicht die Spannweite → Speicher-DoS durch
   ein einziges riesiges Intervall. Plan: Vergleich über kanonische Intervalle ohne Expansion (2.3), semantisch identisch.
2. **[angenommen 13.10.] 400-Fälle unvollständig (§5.2):** `null`-Element, fehlende Felder, leere/überlange Strings nicht genannt. Plan: alle 400
   `invalid_request_body` (2.4).
3. **[angenommen 13.10.] Unbindbarer Body vor Filter/Flag (§5.2, AK 1):** Minimal API bindet vor den Filtern; ein kaputtes JSON gibt 400 auch für
   Nicht-Manager und bei Flag aus. Wie die Start-Route heute; AK 1 gilt für bindbare Bodies.
4. **[angenommen 13.10.] §15 Dateiliste unvollständig:** `web/src/app/core/audit/audit.model.ts` (Unions) fehlt; der Nutzungsseiten-E2E gehört nach
   `usage-atlas.e2e.spec.ts`, nicht nach `channel-settings.e2e.spec.ts` (§11.4).
5. **[angenommen 13.10.] §15 „nur S6 hängt an Teil B“ zu eng:** S10, S11, S13 bauen `ChatLogBackfillService`; Teil B ändert dessen Konstruktor
   (`SlotReconcileOptions`) und das `ReplaceBlockAsync`-Ergebnis (`SnapshotChanged`). Plan: diese Fälle nach dem Rebase (T7).
6. **[angenommen 13.10.] Ansage (§10.2/§10.4):** Der `role="status"`-Bereich ist heute aus dem Status abgeleitet; Lebensdauer und Vorrang einer
   Entfernen-Ansage sind nicht festgelegt. Plan: eigener Zustand, Vorrang bis zum nächsten aktiven Lauf oder Kanalwechsel (3.).
7. **[angenommen 13.10.] Live §13.10 „zweiter Manager in zweitem Browser“:** kein zweites Konto vorhanden. Plan: Coverage-Änderung per `curl` mit
   demselben Cookie während des offenen Dialogs (7.16).
8. **[Nutzerentscheid 13.10.] Live-Kanal:** Nutzervorgabe `olaf_olaf_son`, Spec §13.1 wünscht einen Kanal mit Flag-aus-Importen (S16 live). Ist das auf der
   Kopie `handofblood`, darf das Entfernen dort **nur auf der DB-Kopie** schreiben; 7TV und Prod bleiben unberührt. Liefert das Archiv für `olaf_olaf_son` keine
   Nachrichten, ist dessen Import reine Coverage (S15 live), der Zeilenpfad dann nur am S16-Kanal oder per Integrationstest.
9. **Backfill-Spec-Statuszeile:** im Auftrag „falls üblich“ genannt, Spec §15 schließt die Datei aus (Teil B). Plan: nicht anfassen.
