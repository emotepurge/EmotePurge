# Plan #256 — Robustheit der Übertragungen: lauf-gebundener Abschluss, Arbiter mit Settling-Sperre, Live-Neuladen nach Drift, Dock-Hinweis, Dialog-Zustandsmaschine

Erstellt am 2026-09-26 gegen `fix/256-robustness` = `9e380cc4` (= `origin/feat/emote-sets-200`,
PR #274, #264: Routen-Split). Quellen: Issue #256 (Punkte 1, 2, 4, 5; Punkt 3 ist durch #270
erledigt), der Kommentar „Point 1 is now a precondition of #254" darin, **die vom Betreiber am
2026-09-25 freigegebene Spec zu #254**
(`docs/superpowers/specs/2026-09-25-replace-undo-254-design.md` auf `feat/254-replace-undo`,
Abschnitt 11.1 „Vertrag, den #256 Punkt 1 für #254 erfüllen muss", Punkte 1–6, im Folgenden
„Vertrag P1–P6"; 11.2 zu Punkt 4; 11.3 zu Punkt 5), der [Plan zu #254](Plan-254-Replace-Undo.md)
auf demselben Branch (T0 prüft den Vertrag, T4 konsumiert ihn, Festlegungen 9 und 10 dort), der
Branch `origin/fix/255-wording-counts` (Stand 2026-09-26, neun Commits, Abschnitt 0.7),
[Plan-253](Plan-253-Restore-pro-Set.md) als Formvorbild, `CLAUDE.md` (Regeln 1–22, Tests, Gates,
Sprache), `web/.claude/CLAUDE.md` (Member-Reihenfolge, „Verhalten ja, Vorlage nein"),
`docs/UI-Designsprache.md` (§4.5 transiente Statusmeldung, §7.2), `docs/DECISIONS.md`
(2026-09-05 „Run-Arbiter", 2026-09-06 „dritter Arbiter-Zweig ohne DI-Zirkel") und der Code auf
`9e380cc4` — jede in Abschnitt 0.6 genannte Datei ist gelesen, nicht vermutet.

**Der Vertrag aus Spec 11.1 ist für Punkt 1 die einzige Vertragsquelle; für die Punkte 2, 4 und 5
ist es der Issue-Text.** Dieser Plan trägt Schnitt, Reihenfolge, Nahtstellen, Testerwartung je
Task, Modellwahl und Gates. Er enthält keinen Code: Namen stehen nur, wo sie ein Vertrag zwischen
zwei Tasks (oder zwischen diesem Plan und #254 T0/T4) sind. Findet ein Task eine Abweichung
zwischen Vertrag und Code, gilt der Vertrag, und der Fund wird im Task-Bericht gemeldet, nicht
still aufgelöst. Was der Plan über Vertrag und Issue hinaus festlegt, steht in Abschnitt 6 als
**Festlegung mit Betreiber-Veto**. Echte Betreiberfragen hat der Plan keine gefunden (Abschnitt 7).

**Leitplanke: fail-closed.** Wo eine Regel zwei Lesarten zulässt, gilt die, bei der eher nichts
startet, eher der Unload-Schutz scharf bleibt und eher eine Meldung doppelt geprüft als einmal
verschluckt wird. **Eine Mutation, die bei 7TV angekommen ist, bleibt nie ungemeldet.**

**Fassung 2 (2026-09-26).** Die erste Fassung (`be68db06`) ist um die drei Befunde des adversarialen
Codex-Reviews (gpt-6-sol, zwei `high`, ein `medium`, Abschnitt 11) überarbeitet — alle drei am Code
bestätigt: `reset()` während `running` lässt die Engine zu Ende laufen statt sie abzubrechen
(Festlegung Nr. 3 neu gefasst), die Meldung eines abgelösten Laufs bleibt sichtbar und
wiederholbar (Festlegung Nr. 13, Abschnitt 8 „Für den Betreiber zur Kenntnis"), die Dock-Signale
werden schreibbare Projektionen statt `computed` (Festlegung Nr. 14), Meldungen bekommen einen
Zeitrahmen je Versuch (Festlegung Nr. 15), die Spec-Fixtures gehören zu T1/T2, und jeder Task fährt
den Seiten-Spec und eine Typprüfung der Specs mit. Außerdem: der Branch wird **auf
`origin/fix/255-wording-counts` gestapelt**, statt auf den Epic-Stand nach dem Merge von #255 zu
warten (0.1, 0.4, 0.7, T0, T9).

---

## 0. Ausgangslage

### 0.1 Was feststeht

- **Reihenfolge #255 → #256 → #254** (Betreiber 2026-09-25, Spec 15 B). #255 ist inhaltlich fertig
  (drei Tasks auf `fix/255-wording-counts`); Review, PR und Merge folgen am nächsten Tag. Dieser
  Plan wartet nicht darauf: **`fix/256-robustness` wird auf `origin/fix/255-wording-counts`
  gestapelt** (T0 merged den Branch), und nach dem Merge von #255 wird auf
  `origin/feat/emote-sets-200` rebased (T9). Der PR geht gegen `feat/emote-sets-200`. Kommen aus dem
  Review von #255 noch Fixes, merged T0 sie vor dem Start bzw. T9 vor den Gates nach. #254 wartet
  auf #256 Punkt 1: dessen T0 prüft den Vertrag P1–P6
  Punkt für Punkt am Code und endet mit Befund, wenn ein Punkt fehlt. **Dieser Plan liefert genau
  das, was #254 T0 prüft** — die Abbildung steht in Abschnitt 4.
- **Kein Backend-Diff, kein 7TV-Vertragswechsel.** Alle Meldeaufrufe (`sync-imported`,
  `sync-deleted`, `sync-restored`, `resync`) und alle 7TV-Requests bleiben, wie sie sind; es ändert
  sich nur, **wann** und **an welchen Datensatz gebunden** sie laufen, plus ein zusätzlicher
  Auslöser für den bereits existierenden Live-Read des Zielsets (Punkt 2). Regel 16 greift damit
  nicht als Pflicht-Gate; die Entscheidung zur Verifikation steht in T8 und T10.
- **Bundle-Grenze:** Initial-Bundle unter 500 kB (Budget-Warnung in `web/angular.json`), heute
  ≈ 404 kB; nichts aus der Import-Engine im Initial-Chunk. Jede Datei dieses Plans liegt im
  Lazy-Chunk (`core/seven-tv/**`, `shared/seven-tv/**`, `features/usage-stats/**`,
  `features/voting/**`); der Plan fasst `app.routes.ts`, `app.config.ts` und `app-shell.ts` nicht
  an. T0 misst die Baseline, T9 vergleicht.
- **Der PR geht gegen `feat/emote-sets-200`**, Zweitmeinung einmal je Branch über den Companion
  direkt im Worktree (T9). Deploy zusammen mit dem Rest von #200 nach dem 2026-10-08.
- **Zwei neue Konventionen, zwei DECISIONS-Einträge (englisch), je im einführenden Commit:** der
  lauf-gebundene Abschluss (T1) und die Arbiter-Registrierung samt Settling-Sperre und
  Unload-Vereinigung (T3). Ein dritter, kurzer Eintrag für den Live-Reload nach Drift (T6), weil er
  einen 7TV-Request an einer neuen Stelle auslöst. Punkt 4 und Punkt 5 ändern keine Konvention.

### 0.2 Was der Plan beim Nachprüfen am Code gefunden hat

1. **Die Meldungen gehen heute auf zwei Wegen verloren, nicht auf einem.** Der Frühausstieg
   `if (this.run() !== started) return;` in `onRunComplete` (`seven-tv-import.service.ts:588-592`,
   `seven-tv-delete.service.ts:292`, `seven-tv-restore.service.ts:321`) ist der benannte Weg. Der
   zweite: `SevenTvRunEngine.reset()` leert **nur die Queue** (`seven-tv-run-engine.ts:380-382`),
   bricht die laufende Subscription aber nicht ab. Ein `reset()` des Dienstes **während `running`**
   ruft `engine.reset()` — die Engine läuft weiter, schreibt ihre Zeilenzustände aber in eine leere
   Queue, und `finish()` baut sein `RunResult` aus dieser Queue: `doneKeys` leer, `items` leer,
   gemeldet wird nichts, selbst wenn der Frühausstieg fiele. T1 muss beide Wege schließen
   (Festlegung Nr. 3). **Abbrechen ist dafür kein Weg** (Codex-Befund 1, am Code bestätigt): die
   Delete- und Restore-Operationen setzen `transportLossIsUnknown` nicht (`REMOVE_OPERATION`,
   `addOperation`), und `cancelRemainingRows` (`seven-tv-run-engine.ts:727-760`) macht eine Zeile
   mit Request in der Luft dann `cancelled` — 7TV kann die Mutation längst angewendet haben, der
   Lauf meldet sie nicht, und der Unload-Schutz des Delete fiele mit `closed`. Die Engine muss also
   zu Ende laufen; `reset()` löst nur die Anzeige und leert die Queue erst nach `finish()`.
2. **`applyIfCurrent` schützt die Projektion, und die Projektion ist heute der einzige Speicherort.**
   `syncReport`, `syncReportReason`, `removalReport`, `resyncTrigger` sind Dienst-Signale, nicht
   Felder des Laufdatensatzes; ein abgelöster Lauf hat deshalb keinen Ort, an dem seine Antwort
   landen könnte. Vertrag P6 verlangt genau diesen Ort.
3. **Delete hat kein Lauf-Signal.** `SevenTvDeleteService.run` ist ein privates Feld, `lastRun` ein
   Signal, das erst mit dem Ergebnis gefüllt wird; Restore und Import haben `runState`/`run`. T2
   gleicht Delete an (Naht 2.4), `lastRun` bleibt als Projektion, weil `mass-delete-panel.ts` (vier
   Stellen) und `usage-stats-page.ts` (zwei) es lesen.
4. **Der Unload-Schutz sitzt im Import-Dienst** (`effect` im Konstruktor,
   `seven-tv-import.service.ts:356-363`, modulweite `preventUnload`-Funktion) und hängt an
   `destructiveRunActive`, das `run()` liest — also am gezeigten Lauf. Vertrag P3 will die
   Vereinigung über alle Dienste; sie wandert in den Arbiter (T3).
5. **Der Arbiter injiziert die drei Dienste und liest nur `isRunning`.** Die DECISIONS-Einträge
   vom 2026-09-05 (R1: ableiten statt sperren) und 2026-09-06 (kein DI-Zirkel: Dienste kennen den
   Arbiter nicht) begründen die heutige Kante Arbiter → Dienste. Vertrag P4 verlangt, dass ein
   Dienst **sich registriert**. Das dreht die Kante um (Dienste → Arbiter), ohne Zirkel, weil der
   Arbiter dann keinen Dienst mehr injiziert (Festlegung Nr. 1). R1 bleibt unberührt: der Arbiter
   leitet weiter ab, er hält keinen Lock.
6. **Zehn Startpunkte fragen `activeRun()`** (0.6): drei Buttons (`transferButtonDisabled`,
   `importTriggerDisabled`, der Delete-Button), die Restore-Taste nach einem Delete-Lauf, je zwei
   Prüfungen in `import-flow.ts` und `restore-flow.ts`, drei im `MassDeletePanel`, dazu
   `runBlocked` im Bestätigungsdialog. Nur der `MassDeletePanel`-Delete-Pfad zeigt heute einen
   Grund (`massDelete.anotherRunStarted`); alle anderen brechen still ab, mit der Begründung, der
   laufende Fortschritt sei im Dock sichtbar. Für ein settelndes Dock stimmt das weniger („Wird
   abgeschlossen…" sagt nicht, dass es sperrt) — deshalb die Notiz aus Vertrag P2 (T4).
7. **Punkt 2 hat eine klare Stelle:** `applyDrift` legt die Live-Aliase als Overlay über die Zeile,
   `collisionStepRows` sperrt „Ersetzen" mit `import.resolve.reloadTargetFirst`, sobald das
   Overlay den kollidierenden Namen nicht mehr trägt, und der Banner-Knopf „Ziel neu laden" ruft
   `data.retry()` = `load()` des Flows = `loadImportTarget` — für ein getracktes aktives Set der
   Postgres-Weg (`'trackedActive'`, drei „heutige" Requests, AK 36 aus #200). `loadImportTarget`
   hat einen Live-Zweig (`'trackedSet'`/`'untrackedSet'` → `loadEmoteSetPreview`), der für das
   aktive Set bisher bewusst nicht genommen wird. Nach einem Drift ist er der richtige (T6).
8. **Punkt 4 ist eine Zählung, die es noch nicht gibt:** `outcomeCounts` liefert `removedCount`
   (REMOVE bestätigt) und `unknownCount` (Zeile `unknown`), aber nicht „REMOVE selbst unklar" —
   eine `replace`-Zeile mit `status === 'unknown'` und `failedStep === 0` (so unterscheidet
   `settleUnknownRow` den unbeantworteten REMOVE vom unbeantworteten ADD). Genau die Zeilen fehlen
   im Ergebnisprotokoll als bestätigte Entfernung, und nur die Rückweg-Datei deckt sie (T5).
9. **Punkt 5 ist sauber abgrenzbar:** `rawActionState`/`actionState`, `targetCheckNotice`,
   `verifyAndSave`, `onTargetRead`, der Download-Versuch und die „nur der neueste Read
   antwortet"-Regel (`import-confirm-dialog.ts:977-982, 1204-1279`) sind zusammen die Maschine
   `idle → verifying → saved`; `applyDrift` bleibt Dialogsache, weil es Entscheidungen und Overlays
   des Dialogs schreibt. #255 hat in T2 `liveOccupiedSlots` in `onTargetRead` gehängt (0.7) — das
   ist ein weiterer Konsument des Live-Reads, den die Maschine nach außen geben muss (T7).
10. **Der Leave-Guard bleibt, wie er ist** (Vertrag P4 letzter Satz): `usageStatsLeaveGuard`
    fragt nur `importService.isRunning()` und nur beim Verlassen der Seite; ein settelnder Lauf
    verliert beim Seitenwechsel nichts, weil die Dienste `providedIn: 'root'` sind. Kein Task fasst
    `usage-stats-leave.guard.ts` oder `usage-stats.routes.ts` an; #254 T7 ergänzt dort den Undo.
11. **Eine Ablösung während der Meldephase ist heute erreichbar, und ihr Scheitern wäre unsichtbar**
    (Codex-Befund 2, am Code bestätigt): `resetIfChannelChanged` in Delete und Restore setzt zurück,
    sobald `isRunning()` falsch ist — also auch, während `syncReport` `pending` ist; und der
    Schließen-Knopf des `RunProgressPanel` erscheint, sobald `isRunning()` fällt (`dismissible`
    Default `true`; nur die Import-Section gated ihn auf `settlement === 'settled'`). Mit dem
    lauf-gebundenen Abschluss ginge die Meldung dann zwar raus, aber ein späteres `failed` (403,
    Retries erschöpft) stünde an einem Datensatz, den kein Dock zeigt und kein Retry erreicht —
    der Audit-Eintrag fehlte ohne sichtbaren Weg. Festlegung Nr. 13 schließt das: Schließen und
    Kanalwechsel lösen erst einen `closed`-Lauf, ein abgelöster Lauf mit nicht erfolgreicher
    Meldung zeigt sich selbst wieder, und jede Meldung erreicht per Zeitrahmen sicher einen
    Endzustand (Nr. 15).
12. **99 Spec-Zeilen schreiben die Signale, die Projektionen werden** (Codex-Befund 3, gezählt am
    2026-09-26): `import-progress-section.spec.ts` 48, `mass-delete-panel.spec.ts` 21,
    `restore-progress-section.spec.ts` 14, `dock-outcome-announcer.spec.ts` 11,
    `usage-stats-page.spec.ts` 4 (`lastRun.set` ×3, `run.set` ×1), `seven-tv-import.service.spec.ts`
    1 — alle mit `.set(...)` auf `syncReport`, `syncReportReason`, `resyncTrigger`,
    `removalReport`, `lastRun`, `run`. Ein `computed` bräche sie alle, und keine gefilterte Suite
    aus T1/T2 sähe es. Festlegung Nr. 14: die Dock-Signale bleiben **schreibbar** als
    `linkedSignal`-Projektionen des gezeigten Laufs, `run` bleibt das schreibbare Signal des
    gezeigten Datensatzes; Produktivcode schreibt nur den Datensatz (Abnahme-Grep). Die Fixtures,
    die einen Datensatz bauen, bekommen die neuen Felder in T1 (Import) bzw. T2 (Delete, Restore),
    und beide Tasks fahren den Seiten-Spec plus `npx tsc -p tsconfig.spec.json --noEmit` (in
    `web/`; am 2026-09-26 auf `9e380cc4` geprüft: läuft in ~4 s, Exit 0 — `tsconfig.spec.json`
    schließt `src/**/*.spec.ts` ein, `tsconfig.app.json` schließt sie aus, der Build prüft Specs
    also nicht).

### 0.3 Modelle je Task

`opus` an vier Stellen mit Begründung: T1 (Lebenszyklus-Baustein plus Referenzumbau des Imports —
jeder Fehler ist still und vergiftet T2, T3 und #254 T4), T3 (Arbiter: DI-Kante, Unload-Vereinigung,
Stub-Prüfbarkeit), T7 (Zustandsmaschine aus einem 1.300-Zeilen-Dialog, die #254 T5 konsumiert),
T8 (E2E über das Settling-Fenster und den Unload-Pfad — die Fallen aus Memory zu `page.clock` und
`role=status` liegen dort). `sonnet` für die klar vorgezeichneten Teile: T0 (Vorbedingungen), T2
(Delete/Restore nach dem Muster aus T1 — mit vollständiger Testmatrix als Vertrag und
Diff-Lesung durch die Hauptsession), T4 (Startpunkte, Notiz, i18n), T5 (Zählung + Dock-Zeile), T6
(Live-Reload), T10 (PR-Text). `haiku` nur für die mechanischen Gate-Läufe in T9. Fable nicht als
Implementer (global); die Codex-Zweitmeinung (T9) ist der Beurteilungs-Checkpoint, bei
Widerspruch zwischen Opus-Review und Codex entscheidet Fable als Schiedsrichter (global).

### 0.4 Branch, Worktree, Commits, Gates

- **Branch `fix/256-robustness`**, Worktree `/home/dev/projects/EmotePurge-sets`, PR gegen
  `feat/emote-sets-200`. T0 merged `origin/fix/255-wording-counts` hinein (gestapelt auf #255);
  nach dem Merge von #255 in das Epic rebased T9 auf `origin/feat/emote-sets-200`, bevor die
  vollen Gates laufen. Andere Worktrees und `/home/dev/projects/EmotePurge` bleiben
  unberührt; aus dem Worktree nur bauen und testen, nie `docker compose up` (Memory:
  „compose aus dem Worktree reißt den Stack ab"); E2E nur, wenn auf `:5151` keine Api lauscht.
- **Lanes** (Abschnitt 5): Welle 2 mit drei Lanes (T2 ∥ T5 ∥ T6), Welle 3 mit zwei (T3 ∥ T7); alles
  andere seriell. Parallele Lanes laufen in eigenen Worktrees unter `/home/dev/projects/` und
  mergen in `fix/256-robustness`; gemeinsame Dateien je Welle stehen in Abschnitt 5.
- **Ein Commit je Task**, Conventional Commits, englisch, ohne `#`-Referenzen in
  Commit-Metadaten (Memory). Ein Task, der einen Bestandstest bricht, repariert ihn **im selben
  Commit** — keine rote Zwischenstufe.
- **Aufrufer-Regel** (Plan-253 0.4): die Dateiliste jedes Tasks nennt jeden Aufrufer, jede
  typisierte Fixture und jeden Provider-Stub der Signaturen, die er ändert (Inventur in 0.6).
  Jeder Task fährt neben seinen gefilterten Specs `npm --prefix web run build` als Typprüfung —
  `npx vitest` prüft keine Typen (Memory).
- **Regel 3:** DECISIONS-Eintrag 1 (Lebenszyklus) in T1, Eintrag 2 (Arbiter) in T3, Eintrag 3
  (Live-Reload nach Drift) in T6. T2 hängt seine zwei Dienste in `Betrifft:` von Eintrag 1 an. Die
  Hauptsession liest jeden DECISIONS-Diff selbst (Memory: „Grüne Suiten sind keine
  Fertigmeldung").
- **Gates je Task:** gefilterte Vitest-Specs, **immer** `src/app/features/usage-stats/usage-stats-page.spec.ts`
  mit dabei, sobald ein Dienst-Signal oder ein Laufdatensatz die Form ändert (T1–T5), `npm --prefix
  web run build` (Typprüfung des Produktivcodes — die Specs sind in `tsconfig.app.json`
  ausgeschlossen), **`cd web && npx tsc -p tsconfig.spec.json --noEmit`** (Typprüfung aller Specs,
  ~4 s; Codex-Befund 3), `npm --prefix web run lint`, `npm --prefix web run format`. **Gates am
  Ende (T9):** `npm --prefix web test --
  --watch=false` voll, `npm --prefix web run e2e` (ohne Api auf `:5151`), `dotnet test
  EmotePurge.slnx` (unverändert, Docker nötig — der Plan ändert kein Backend, das Gate bleibt
  trotzdem die Fertigmeldung des Repos), `node scripts/coverage-local.mjs --frontend-only` (Memory:
  pessimistisch, Anlass hinzusehen, kein Urteil; Sonar misst `new_coverage` inklusive Zweigen),
  Bundle-Vergleich gegen die T0-Baseline.
- **Wortlaut:** jeder neue Locale-Schlüssel dieses Plans ist **vorläufig**; #255 ist der
  Wortlaut-Eigentümer und wird vor diesem Branch gemergt, also bekommen die neuen Schlüssel eine
  Wortlaut-Runde erst in einem Folgeschritt — der PR-Text nennt sie gesammelt (T10).

### 0.5 Vom Plan benannte Bausteine (Namen als Vertrag zwischen Tasks und für #254)

| Baustein | Name | Ort | Vertrag | erzeugt in | konsumiert in |
|---|---|---|---|---|---|
| Lauf-Phase | `RunPhase = 'running' \| 'settling' \| 'reporting' \| 'closed'` | `core/seven-tv/seven-tv-run-lifecycle.ts` (neu) | P6 | T1 | T1, T2, T3 (nur über die drei Signale), #254 T4 |
| Lebenszyklus-Baustein | `SevenTvRunLifecycle<TRun>` — hält die offenen Läufe eines Dienstes **per `runId`** (nicht per Objektreferenz: Datensätze werden bei jedem Übergang als neues Objekt ersetzt, wie heute `finished = { ...started, result }`), den gezeigten Lauf, und liefert `isSettling`, `destructiveOpen`, `shown`; Operationen: Lauf eröffnen, Datensatz patchen, Phase setzen, Meldung eröffnen/abschließen, Lauf schließen, wenn keine Meldung mehr offen ist, Anzeige lösen | ebenda | P1, P6 | T1 | T1 (Import), T2 (Delete, Restore), #254 T4 (Undo) |
| Laufdatensatz-Basis | jeder Laufdatensatz trägt `runId`, `phase`, `destructive` (mindestens eine destruktive Zeile) und seine Meldungszustände als Felder (`syncReport`/`syncReportReason` bzw. `removalReport`/`removalReportReason`, `resyncTrigger`); `settlement: 'pending' \| 'settled'` bleibt am Import als abgeleitetes Feld (`'settled'` ⇔ Phase `reporting` oder `closed`), weil `usage-stats-page.ts` (`watchRunSettle`), `import-progress-section.ts` (vier Stellen) und #254 es lesen | die drei Dienste | P6 | T1, T2 | Docks, Seite, #254 |
| Dock-Projektionen (Festlegung Nr. 14) | `run` bleibt das **schreibbare** Signal des gezeigten Datensatzes (der Baustein hält es); `syncReport`, `syncReportReason`, `removalReport`, `removalReportReason`, `resyncTrigger`, `protocolSaved`, Delete-`lastRun` werden `linkedSignal`-Projektionen von `run()` — schreibbar, damit die 99 Spec-Zeilen aus 0.2 Nr. 12 stehen bleiben; Produktivcode schreibt nie in sie, nur in den Datensatz | die drei Dienste | P6 | T1, T2 | Docks, Announcer, Seite, alle Specs aus 0.6 |
| Dienst-Signale für den Arbiter | `isRunning` (Engine, unverändert), `isSettling`, `destructiveOpen` — je Dienst, Projektionen des Lebenszyklus-Bausteins; das Import-Signal `destructiveRunActive` **entfällt** zugunsten von `destructiveOpen` | die drei Dienste | P1, P3 | T1, T2 | T3, #254 T0 (Prüfung), #254 T4 |
| Meldungs-Zeitrahmen (Festlegung Nr. 15) | `REPORT_TIMEOUT_MS = 30_000` je Versuch der Meldungskette (`sync-imported`, `sync-deleted`, `sync-restored`); ein Ablauf zählt als transienter Fehler in derselben Retry-Policy, nach den Retries `failed`/`unavailable` | `seven-tv-delete.service.ts` (neben `SYNC_RETRY_DELAY_MS`, exportiert wie dieses) | P6 („jede Meldung erreicht einen Endzustand") | T1 | T2, #254 T4 |
| Schließen-Gate | `RunProgressPanel.dismissible` wird in allen drei Docks auf „Lauf `closed`" gebunden (Import: heute `settlement === 'settled'`; Delete/Restore: heute ungegated); `resetIfChannelChanged` löst nur einen `closed`-Lauf | `import-progress-section.ts`, `mass-delete-panel.ts`, `restore-progress-section.ts`, Delete/Restore-Dienste | Festlegung Nr. 13 | T1 (Import), T2 (Delete, Restore) | #254 T7 |
| Teilnehmer und Registrierung | `SevenTvRunParticipant { kind: SevenTvRunKind; isRunning; isSettling; destructiveOpen }`, `SevenTvRunArbiter.register(participant)`; jeder Dienst registriert sich **in seinem Konstruktor**; `SevenTvRunKind` bleibt der eine Union-Typ (`'delete' \| 'restore' \| 'import'`, #254 ergänzt `'undo'`) | `core/seven-tv/seven-tv-run-arbiter.ts` | P4, P5 | T3 | T1/T2 (Registrierung, nachgezogen in T3), #254 T4 |
| Arbiter-Antwort | `activeRun: Signal<SevenTvRunKind \| null>` (Bedeutung erweitert: läuft **oder** settelt), neu `activeClaim: Signal<{ kind; phase: 'running' \| 'settling' } \| null>` als Grund, `destructiveOpen: Signal<boolean>` (Vereinigung), `refusedStart: Signal<{ attempted: SevenTvRunKind; blockedBy: { kind; phase } } \| null>` mit `noteRefusedStart(attempted)` und `REFUSED_START_FEEDBACK_MS = 4000` (§4.5) | ebenda | P2, P3, P5 | T3 | T4 (Startpunkte, Seite, Panel), #254 T6 |
| Unload-Schutz | der `beforeunload`-Effekt samt `preventUnload` wandert aus dem Import-Dienst in den Arbiter und hängt an dessen `destructiveOpen` | ebenda | P3 | T3 | — |
| Unklare Entfernungen | `ImportRunInfo.unknownRemovalCount` (REMOVE einer `replace`-Zeile unbeantwortet geblieben, auch nach dem Nachlesen), Zeile `import.summary.unknownRecordedIn` im Dock — Schlüssel-Aufbau wie `undo.summary.unknownRecordedIn` in #254 (Spec 11.2), gleiche Stelle (unter `unknownRows`) | `seven-tv-import.service.ts`, `import-progress-section.ts` | Issue Punkt 4 | T5 | #254 T7 (gleicher Aufbau) |
| Live-Reload nach Drift | `ImportConfirmDialogData.reloadLive: () => void` neben `retry`; der Flow löst damit den Live-Zweig des Loaders für das bereits aufgelöste Set aus | `import-flow.ts`, `import-confirm-dialog.ts` | Issue Punkt 2 | T6 | T7 (der Dialog reicht es an die Maschine nicht weiter — Reload bleibt Dialogsache) |
| Zustandsmaschine | `RecoveryFileGate<TPlan, TDrift>` mit Zuständen `idle \| verifying \| saved` (mit `stampedPlan`), Notiz `readFailed \| saveFailed \| drifted`, Regeln „Planwechsel ⇒ idle", „nur der neueste Read antwortet", „Download-Fehler ⇒ idle"; Abhängigkeiten als Funktionen (Read, Verifikation, Stempeln, Speichern, Drift-Rückruf, Live-Read-Beobachter für `liveOccupiedSlots`) | `shared/seven-tv/recovery-file-gate.ts` (neu) | Issue Punkt 5, #254 Spec 11.3 | T7 | `import-confirm-dialog.ts` (T7), #254 T5 (`UndoConfirmDialog`) |
| Locale-Familie der Notiz | `sevenTvRun.notStarted.running`, `sevenTvRun.notStarted.settling` (Parameter `kind`), `sevenTvRun.kind.delete`/`.restore`/`.import` (#254: `.undo`); `massDelete.anotherRunStarted` entfällt zugunsten dieser Familie | `web/public/i18n/{de,en}.json` | P2 | T4 | Seite, Panel, #254 T6 |

### 0.6 Aufrufer-Inventur je geänderter Signatur (grep, 2026-09-26, Stand `9e380cc4`)

| Signatur / Typ, geändert durch | Aufrufer, Fixtures, Provider-Stubs | landet in |
|---|---|---|
| `SevenTvImportService`: `ImportRunInfo` bekommt `runId`, `phase`, `destructive`, Meldungsfelder; `syncReport`, `removalReport`, `removalReportReason`, `resyncTrigger`, `protocolSaved` werden schreibbare Projektionen; `destructiveRunActive` entfällt; `reset()`-Semantik; Schließen-Gate auf `closed` (T1) | `import-progress-section.ts` (sieben Lesestellen, `[dismissible]`), `dock-outcome-announcer.ts`, `usage-stats-page.ts` (`watchRunSettle`), `import-flow.ts`; Specs mit **Schreibzugriffen oder `ImportRunInfo`-Fixtures — alle in T1:** `seven-tv-import.service.spec.ts` (1.597 Zeilen — die Fälle „drops the follow-up answers of a superseded run" ~702, „keeps a re-read answer after reset() off the dock but still sends its reports" ~1319, „counts a replace run as destructive until its re-read has settled it" ~1527 ändern ihre Erwartung; `describe('beforeunload guard')` ~1552 zieht in T3 um), `import-progress-section.spec.ts` (48 Schreibzeilen), `dock-outcome-announcer.spec.ts` (11, Import-Anteil), `usage-stats-page.spec.ts` (`run.set` Zeile ~3543 — Fixture braucht die neuen Felder), `import-trigger.spec.ts`, `seven-tv-run-arbiter.spec.ts` | T1 (Dienst, eigener Spec, **alle** genannten Fixtures), T3 (Unload-Fälle ziehen um) |
| `SevenTvDeleteService`: `run` wird Signal (Baustein), `lastRun`/`syncReport`/`syncReportReason` schreibbare Projektionen; `resetIfChannelChanged` nur bei `closed`; Schließen-Gate (T2) | `mass-delete-panel.ts` (`lastRun` ×4, `syncReport` ×3, `[dismissible]` neu), `usage-stats-page.ts` (`lastRun` ×2), `run-progress-panel.ts` (Input), `channel-workspace-layout.ts` (`resetIfChannelChanged`); Specs mit **Schreibzugriffen — alle in T2:** `seven-tv-delete.service.spec.ts` (978 Zeilen; „discards a late sync-deleted answer from a superseded run" ~663 ändert die Erwartung), `mass-delete-panel.spec.ts` (2.897 Zeilen, 21 Schreibzeilen, Provider-Stubs), `usage-stats-page.spec.ts` (`lastRun.set` Zeilen ~3465, ~3482, ~3551 — Shape bleibt, Fälle laufen mit), `dock-outcome-announcer.spec.ts` (Restore-Anteil), `vote-session-detail-page.spec.ts`, `channel-workspace-layout.spec.ts` | T2 |
| `SevenTvRestoreService`: `RestoreRunInfo` += Basisfelder, `syncReport`/`syncReportReason`/`resyncTrigger` schreibbare Projektionen; `resetIfChannelChanged` nur bei `closed`; Schließen-Gate (T2) | `restore-progress-section.ts` (drei, `[dismissible]` neu), `dock-outcome-announcer.ts`, `mass-delete-panel.ts`, `restore-flow.ts`; Specs mit **Schreibzugriffen — alle in T2:** `seven-tv-restore.service.spec.ts` (1.000 Zeilen; `describe('superseded run (R15)')` ~869), `restore-progress-section.spec.ts` (14 Schreibzeilen), `restore-flow.spec.ts`, `channel-workspace-layout.spec.ts` | T2 |
| `SevenTvRunArbiter`: kein `inject` der Dienste mehr, `register`, `activeClaim`, `destructiveOpen`, `refusedStart`, `noteRefusedStart` (T3) | `usage-stats-page.ts` (`transferButtonDisabled`, `hasActiveRun`, `startImportFlow`-Deps), `import-trigger.ts`, `import-trigger-gate.ts` (nur Doku-Kommentar), `import-shortcut.ts` (Doku), `import-flow.ts` (zwei Prüfungen + `runBlocked`), `restore-flow.ts` (zwei), `mass-delete-panel.ts` (drei Prüfungen, zwei Template-Gates); **Stubs mit `activeRun` allein:** `foreign-import-flow.spec.ts`, `import-flow.spec.ts`, `import-trigger.spec.ts` (2), `mass-delete-panel.spec.ts` (10), `restore-flow.spec.ts`; `seven-tv-delete.service.spec.ts` und `seven-tv-restore.service.spec.ts` nennen die Klasse ebenfalls (prüfen, was sie damit tun) | T3 (Arbiter, Registrierung in den drei Konstruktoren, eigener Spec), T4 (Startpunkte + Stubs bekommen `noteRefusedStart`/`refusedStart`) |
| `ImportRunInfo` += `unknownRemovalCount` (T5) | `import-progress-section.ts`, Fixtures in `import-progress-section.spec.ts`, `usage-stats-page.spec.ts`, `dock-outcome-announcer.spec.ts` (jede typisierte `ImportRunInfo`-Fixture) | T5 |
| `ImportConfirmDialogData` += `reloadLive` (T6) | `import-flow.ts` (einziger Produktiv-Aufrufer), `import-confirm-dialog.spec.ts` (jede Data-Fixture, 1.984 Zeilen), `foreign-import-flow.spec.ts`/`import-flow.spec.ts` (falls sie den Dialog-Aufruf prüfen) | T6 |
| `ImportConfirmDialog`: private Maschine → `RecoveryFileGate` (T7) | keine Aufruferänderung nach außen (`ImportConfirmOutcome`, `openImportConfirmDialog` unverändert); `import-confirm-dialog.spec.ts` bleibt als Integrationsspec, die Fälle 1691–1965 (Live-Read, Download, Drift, „nur der neueste Read") bleiben grün | T7 |
| `massDelete.anotherRunStarted` entfällt (T4) | `mass-delete-panel.ts` (eine Stelle), `mass-delete-panel.spec.ts`, beide Locale-Dateien | T4 |

### 0.7 Überschneidungen mit #255 (Stand `origin/fix/255-wording-counts`, 2026-09-26)

#255 hat neun Commits über dem Epic-Stand (`89fbe0bb` … `ce478ff9`, Diff über 33 Dateien); sein
T3 (Aufspaltung von `channelMismatch` in `notTracked` und `activeSetDiffers` in
`sync-report-outcome.ts`, mit den N4-Vergleichen `!== 'channelMismatch'` in `run-progress-panel.ts`,
`import-progress-section.ts` und den drei Lauf-Diensten) ist noch **nicht** gepusht. Was diesen Plan
berührt:

| #255-Änderung | Datei(en) | trifft Task | Konsequenz |
|---|---|---|---|
| Restore ohne Client-Resync für nicht-aktives Ziel (`resyncAfterReport` liest nur noch `expectedChannelName`; `resyncChannelName` bleibt am Datensatz, hat aber keinen Leser mehr) — DECISIONS-Eintrag 2026-09-25 | `seven-tv-restore.service.ts` + Spec | T2 | T2 baut den Lebenszyklus auf **dieser** Fassung; die Resync-Regel bleibt, wie #255 sie setzt |
| Restore-Vorschau beim Öffnen (`loadRestoreConfirmPreview`, `RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS`, `previewPending`, `destroyRef` in `RestoreFlowDeps`; Panel: `restoreConfirmPending`, `handleRestoreConfirmPreview`); die Arbiter-Prüfungen stehen jetzt an **vier** Stellen je Weg (leere Vorschau ⇒ Direktstart, nach Bestätigung, nach dem Confirm-Check) | `restore-flow.ts`, `mass-delete-panel.ts`, `already-present-filter.ts`, `import-trigger.ts` | T4 | T4 verdrahtet `noteRefusedStart` an **allen** Prüfstellen der #255-Fassung, nicht an den zwei der Epic-Fassung; die Inventur in 0.6 gilt für `9e380cc4` und ist in T0 nachzuziehen |
| Adopt-Zählung: `doneAdoptCount` im Import-Dienst, `renamedCount`-Input und `summaryCountsKey` im `RunProgressPanel`, Bindung in `import-progress-section.ts` | `seven-tv-import.service.ts`, `run-progress-panel.ts`, `import-progress-section.ts` | T1, T5 | `doneAdoptCount` liest `items()` — bleibt als Projektion erhalten; T5 setzt seine Zeile neben `unknownRows`, nicht neben die Zählung |
| Confirm-Dialog: `runNoticeKey`, `executeLabelKey` mit `executeRenameOnly`, `liveOccupiedSlots` (linkedSignal, in `onTargetRead` aus `entries.occupiedSlots` gesetzt), `SevenTvSetEntries.occupiedSlots` | `import-confirm-dialog.ts`, `seven-tv-set-entries.ts` | T6, T7 | T7 extrahiert die Maschine **inklusive** eines Beobachters für den Live-Read, damit `liveOccupiedSlots` weiter gesetzt wird; T6 fasst nur `retry`/`reloadLive` und den Banner-Knopf an |
| Familien-Vereinheitlichung der „nicht bearbeitbar"-Fehler, Token-Dialog-Intro, Audit-Plurale | Locales, `audit-row.ts`, Token-Dialog | keiner | nur Textkonflikte in `de.json`/`en.json`, trivial |
| **T3 (in Arbeit):** `channelMismatch` → `notTracked` \| `activeSetDiffers`, N4-Vergleiche | `sync-report-outcome.ts`, `run-progress-panel.ts`, `import-progress-section.ts`, drei Dienste | T1, T2, T5 | Die Retry-Sperre bei Kanal-Mismatch wandert in T1/T2 auf den Laufdatensatz; sie muss die **aufgespaltenen** Werte prüfen, wie #255 T3 sie liefert — nicht `'channelMismatch'` |

**Konsequenz für die Reihenfolge (Fassung 2):** T0 merged `origin/fix/255-wording-counts` in
`fix/256-robustness`, sobald **alle drei** #255-Tasks dort liegen — Prüfkriterium für T3 ist
`grep -n "channelMismatch" web/src/app/core/seven-tv/sync-report-outcome.ts` leer und
`notTracked`/`activeSetDiffers` vorhanden. Liegt T3 noch nicht auf dem Branch, endet T0 mit
Befund: jeder Task dieses Plans arbeitete sonst in denselben Dateien. Review-Fixes von #255, die
danach noch kommen, merged T0 (vor dem Start) bzw. T9 (vor den Gates) nach; nach dem Merge von
#255 in das Epic rebased T9 auf `origin/feat/emote-sets-200`.

---

## 1. Verträge — wo sie stehen, welcher Task sie trägt

| Vertrag | Quelle | trägt | prüft |
|---|---|---|---|
| Zustände je Dienst (`isRunning`, `isSettling` = Nachlesen **oder** offene Meldung, `destructiveOpen`) | Spec 11.1 P1, P3 | T1 (Import, Baustein), T2 (Delete, Restore) | eigene Dienst-Specs, #254 T0 Schritt 2 (1) |
| Arbiter sperrt bei laufend **oder** settelnd, liefert einen Grund; jeder Startpunkt fragt nur den Arbiter | P2 | T3 (Arbiter), T4 (Startpunkte) | `seven-tv-run-arbiter.spec.ts`, Flow-/Panel-Specs, #254 T0 (2) |
| Unload-Schutz = Vereinigung über alle offenen destruktiven Läufe, nicht über den gezeigten | P3 | T3 | Arbiter-Spec (Stub `destructiveOpen: true` bei `run() === null`), E2E T8, #254 T0 (3) |
| Vierter Lauf-Typ: eine Registrierung, ein Union-Wert, keine Dienstliste im Arbiter | P4 | T3 | Abnahme-Grep in T3, #254 T0 (4) |
| Arbiter gegen Stubs prüfbar | P5 | T3 | Arbiter-Spec, #254 T0 (5) |
| Lauf-gebundener Abschluss `running → settling → reporting → closed`; kein Frühausstieg; Meldungszustände am Datensatz; Retry öffnet nicht wieder; drei `reset()`-Fälle je Dienst | P6 | T1, T2 | Dienst-Specs, #254 T0 (6) |
| Live-Reload nach Drift statt Postgres-Vorschau | Issue Punkt 2 | T6 | `import-flow.spec.ts`, `import-confirm-dialog.spec.ts` |
| Dock sagt, wo ein unklarer REMOVE steht | Issue Punkt 4, #254 Spec 11.2 | T5 | `seven-tv-import.service.spec.ts`, `import-progress-section.spec.ts` |
| Zustandsmaschine als eigene Einheit, von #254 konsumierbar | Issue Punkt 5, #254 Spec 11.3 | T7 | `recovery-file-gate.spec.ts`, Dialog-Spec unverändert grün |
| Transiente Notiz mit Grund, §4.5-konform (permanente `role="status"`-Region + sichtbarer `aria-hidden`-Zwilling) | P2, `docs/UI-Designsprache.md` §4.5 | T4 | `usage-stats-page.spec.ts`, `mass-delete-panel.spec.ts` |

---

## 2. Nahtstellen — wer den Vertrag festlegt, wer ihn konsumiert

### 2.1 Lebenszyklus-Baustein ↔ die drei Dienste

**Festlegend:** T1 (`SevenTvRunLifecycle`, `RunPhase`, die Laufdatensatz-Basis aus 0.5).
**Konsumierend:** T1 selbst (Import als Referenzumbau), T2 (Delete, Restore), #254 T4 (Undo).
**Prüfung:** `seven-tv-run-lifecycle.spec.ts` (pur, ohne TestBed) belegt die Ableitungen; die
drei Dienst-Specs belegen, dass die Signale wahr sind (P6, „die drei Signale sind nur so viel wert
wie die Läufe dahinter"). Die Hauptsession vergleicht nach T1 die Baustein-API mit dem Brief für
T2 und legt sie #254 T0 als Ledger-Eintrag vor.

### 2.2 Dienste ↔ Arbiter

**Festlegend:** T3 (`SevenTvRunParticipant`, `register`). **Konsumierend:** die drei Dienste
(Registrierung im Konstruktor — T3 trägt sie nach, damit T1/T2 nicht gegen einen Arbiter bauen,
den es noch nicht gibt), #254 T4. **Prüfung:** Arbiter-Spec mit Stubs (P5) **und** mit den echten
Diensten (die heutigen sechs Fälle, angepasst); Abnahme-Grep in T3, dass der Arbiter keinen
Dienst importiert. **Zwischenstand:** zwischen T2 und T3 liest der Arbiter noch `isRunning` der
injizierten Dienste — grün, aber ohne Settling-Sperre; das ist ein Branch-interner Zwischenstand
ohne Nutzerkontakt.

### 2.3 Arbiter ↔ Startpunkte ↔ Notiz

**Festlegend:** T3 (`activeClaim`, `refusedStart`, `noteRefusedStart`). **Konsumierend:** T4 an
allen Prüfstellen aus 0.2 Nr. 6 in der #255-Fassung (0.7), die Seite (Rendering) und das Panel
(`abortNotice`). **Prüfung:** Flow-Specs stubben den Arbiter mit `activeClaim` und erwarten den
`noteRefusedStart`-Aufruf mit der eigenen Sorte; `usage-stats-page.spec.ts` belegt Region und
Zwilling; `mass-delete-panel.spec.ts` den Grund im `abortNotice`.

### 2.4 Delete-Laufdatensatz ↔ Panel ↔ Seite

**Festlegend:** T2 (`run` wird Signal, `lastRun` Projektion mit unverändertem Shape
`{ setId, channelName, result }`). **Konsumierend:** `mass-delete-panel.ts`, `usage-stats-page.ts`
(unverändert, weil das Shape bleibt). **Prüfung:** `mass-delete-panel.spec.ts` grün ohne
Änderung an den `lastRun`-Fällen; T2 meldet, wenn eine Stelle doch anders lesen muss.

### 2.5 Confirm-Dialog ↔ Flow (Reload) ↔ Maschine

**Festlegend:** T6 (`reloadLive` in `ImportConfirmDialogData`, Flow-Implementierung),
T7 (`RecoveryFileGate`-API). **Konsumierend:** T7 baut auf T6 auf (Welle 3 nach Welle 2), #254 T5
konsumiert die Maschine. **Prüfung:** die Dialog-Spec bleibt als Integrationsspec unverändert grün;
die Maschinen-Spec deckt die Zustandsmatrix; T7 belegt per Grep, dass `liveOccupiedSlots` (#255)
weiter aus dem Live-Read gesetzt wird.

### 2.6 Import-Laufdatensatz ↔ Dock (Punkt 4)

**Festlegend:** T5 (`unknownRemovalCount`). **Konsumierend:** `import-progress-section.ts`, #254
T7 (gleicher Schlüssel-Aufbau). **Prüfung:** Dienst-Spec zählt, Section-Spec zeigt die Zeile nur
bei `> 0`.

---

## 3. Tasks

Jeder Task läuft als eigener Subagent mit frischem Kontext und bekommt: diesen Abschnitt, die
Nahtstellen aus Abschnitt 2, die er berührt, den Vertrag (Spec 11.1 im Original, per `git show`
aus `/home/dev/projects/EmotePurge-254`), den Stand von 0.7, die Sprachregel (Kommentare,
Log-/Throw-Texte, Commit englisch) und die Member-Reihenfolge aus `web/.claude/CLAUDE.md`.

### T0 — Vorbedingungen: auf #255 gestapelt, Inventur nachgezogen, Bundle-Baseline

**Ziel:** Der Branch steht auf `origin/fix/255-wording-counts` mit allen drei #255-Tasks; die
Inventur aus 0.6/0.7 ist gegen diesen Stand verifiziert; die Bundle-Baseline ist gemessen; kein
Feature-Task startet gegen einen Stand, den #255 noch in denselben Dateien verändert.

**Schritte:**

1. `git fetch origin`; prüfen, dass **alle drei** #255-Tasks auf `origin/fix/255-wording-counts`
   liegen: T1 (`89fbe0bb`, Restore-Resync), T2 (`cdfd1fb4`…`847d7def`, Adopt-Zählung,
   Rename-only-Texte, `liveOccupiedSlots`) und T3 (`grep -n "channelMismatch"
   web/src/app/core/seven-tv/sync-report-outcome.ts` auf dem Branch-Stand leer, `notTracked` und
   `activeSetDiffers` vorhanden). **Fehlt T3, endet der Task hier mit Befund.** Ist #255 inzwischen
   in das Epic gemergt, wird stattdessen `origin/feat/emote-sets-200` gemergt und der Rebase-Schritt
   in T9 entfällt.
2. `git merge origin/fix/255-wording-counts` in `fix/256-robustness` (Konflikte nur in `docs/`
   denkbar; `docs/DECISIONS.md` beide Seiten behalten, neuere oben). `npm --prefix web install`
   nur, wenn `package-lock.json` sich geändert hat. Den gemergten #255-SHA ins Ledger — T9 prüft
   dagegen, ob Review-Fixes nachzumergen sind.
3. Inventur nachziehen: die Greps aus 0.6 und die Zählung aus 0.2 Nr. 12 auf dem gemergten Stand
   wiederholen (`activeRun`, `destructiveRunActive`, `lastRun`, die Signal-Leser, die
   Spec-Schreibzugriffe, die Arbiter-Stubs); Abweichungen zu 0.6 und die tatsächliche Zahl der
   Prüfstellen in `restore-flow.ts` und `mass-delete-panel.ts` nach #255 ins Ledger; die
   aufgespaltenen Mismatch-Werte, auf die die Retry-Sperre in T1/T2 prüfen muss, ebenfalls.
4. Gates auf dem gemergten Stand: `npm --prefix web run build`, `cd web && npx tsc -p
   tsconfig.spec.json --noEmit`, `npm --prefix web test -- --watch=false` (voll). Alles grün ist die
   Freigabe für Welle 1.
5. Bundle-Baseline: aus der Build-Ausgabe die Zeile „Initial total" (Erwartung ≈ 404 kB) und per
   `npx ng build --stats-json` (im Worktree, `--output-path` in den Scratch-Ordner) die Liste der
   Initial-Chunks ins Ledger, damit T9 vergleichen kann.
6. Ledger anlegen: `.superpowers/sdd/Plan-256-Robustheit/progress.md` (gitignoriert) mit
   Basis-SHA, Baseline, Inventur-Abweichungen, #255-T3-Werten.

**Fertig-Kriterium:** Merge-Commit auf dem Branch; Build und Vitest grün; Ledger mit Baseline und
Inventur; Bericht nennt jede Abweichung von 0.6/0.7. **Kein Feature-Commit.** **Modell:** `sonnet`.

### T1 — Lebenszyklus-Baustein und der Import als Referenzumbau (Vertrag P1, P6 für den Import)

**Ziel:** Ein Lauf ist ein eigener Datensatz mit eigenem Lebenszyklus `running → settling →
reporting → closed`, gebunden an sich selbst, nicht an die Anzeige. Der Import-Dienst läuft
darauf: kein Frühausstieg mehr, Meldungszustände am Datensatz, Dienst-Signale als Projektionen
des gezeigten Laufs, `isSettling`/`destructiveOpen` über **alle** offenen Läufe des Dienstes.

**Vertrag:** Spec 11.1 P1, P6 (alle vier Spiegelstriche), P3 (Dienst-Hälfte: `destructiveOpen`);
Festlegungen Nr. 2, 3, 4; #255-Berührung (0.7: `doneAdoptCount`, aufgespaltene Mismatch-Werte).

**Dateien:** `web/src/app/core/seven-tv/seven-tv-run-lifecycle.ts` (neu, + `.spec.ts`, pur —
kein TestBed, kein HTTP; Baustein aus 0.5); `web/src/app/core/seven-tv/seven-tv-import.service.ts`
(+ `.spec.ts`): `ImportRunInfo` bekommt `runId`, `phase`, `destructive`, die Meldungsfelder;
`settlement` bleibt abgeleitet; `run` bleibt das schreibbare Signal des gezeigten Datensatzes;
`syncReport`, `removalReport`, `removalReportReason`, `resyncTrigger`, `protocolSaved` werden
**schreibbare** `linkedSignal`-Projektionen von `run()` (Festlegung Nr. 14) — Produktivcode
schreibt sie nie; `onRunComplete` ohne Frühausstieg — Ergebnis in den Datensatz, in `run()` nur
gespiegelt, wenn der Lauf noch gezeigt wird; `settleRun`/`sendFollowUp` auf dem Datensatz;
`reportImported`/`reportRemoved` schreiben per `runId` in den Datensatz (der
`applyIfCurrent`-Helfer entfällt — die Projektion erledigt, was er schützte) und laufen mit
`REPORT_TIMEOUT_MS` je Versuch (Festlegung Nr. 15; die Konstante entsteht neben
`SYNC_RETRY_DELAY_MS` im Delete-Dienst, exportiert); `retrySyncReport`/`retryRemovalReport`
öffnen den Lauf nicht wieder; `reset()` und `startImport` lösen nur die Anzeige — **`reset()`
während `running` bricht die Engine nicht ab**, die Queue wird erst nach `finish()` geleert
(Festlegung Nr. 3); ein abgelöster Lauf, dessen Meldung nicht `succeeded` endet, zeigt sich
selbst wieder, wenn `run()` leer ist (Festlegung Nr. 13); `destructiveRunActive` **entfällt**,
`destructiveOpen` und `isSettling` kommen vom Baustein; der `beforeunload`-Effekt bleibt in
diesem Task noch im Dienst, hängt aber schon an `destructiveOpen` (T3 zieht ihn um);
`web/src/app/shared/seven-tv/import-progress-section.ts` (`[dismissible]` von `settlement ===
'settled'` auf „Lauf `closed`"; Kommentar dazu) + `.spec.ts` (48 Schreibzeilen bleiben, Fixtures
mit den neuen Feldern); `dock-outcome-announcer.spec.ts` (Import-Fixtures);
`usage-stats-page.spec.ts` (`run.set`-Fixture ~3543); `docs/DECISIONS.md` (Eintrag 1, oben: „7TV
runs complete run-bound — running → settling → reporting → closed; reset() detaches the display
only", `Betrifft:` nennt Baustein und Import; T2 ergänzt Delete/Restore). Leser der Signale aus
0.6 bleiben unverändert, weil die Projektionen dieselben Namen und Typen tragen — der Task belegt
das per Build **und** per Spec-Typprüfung.

**Grenzfälle (alle als Spec-Fall am Import):**
- ohne `unknown`-Zeile: Engine fertig ⇒ Phase `reporting` (oder `closed`, wenn keine Meldung
  ansteht: `importedKeys` **und** `removedTargetIds` leer) ohne Zwischenstopp in `settling`;
  `isSettling` genau so lange wahr, wie eine Meldung `pending` ist
- mit `unknown`-Zeile: Phase `settling` während des Nachlesens (bis Antwort, Fehler oder
  `SETTLE_READ_TIMEOUT_MS`), danach `reporting`; `isSettling` wahr über beide Phasen
- `closed`, sobald jede eröffnete Meldung einen Endzustand hat (`succeeded | partial | failed`);
  ein Resync ist **keine** Meldung (Festlegung Nr. 5) und hält den Lauf nicht offen
- eine Meldung, die nach den Retries endgültig `failed` ist ⇒ trotzdem `closed`, Arbiter frei
- manueller Retry am geschlossenen Lauf ⇒ Meldungsfeld `pending` → Endzustand, Phase bleibt
  `closed`, `isSettling` bleibt falsch, `destructiveOpen` bleibt falsch
- `destructiveOpen` wahr für einen Plan mit `replace`-Zeile von `running` bis `closed`, auch nach
  `reset()` und nach einem neuen Lauf; nie für einen Plan ohne `replace`
- **die drei `reset()`-Fälle (P6, letzter Spiegelstrich):** `reset()` während `running` ⇒ die
  Engine läuft **zu Ende** (Festlegung Nr. 3), auch die noch ausstehenden Zeilen; jede bestätigte
  Mutation wird gemeldet, Lauf schließt — **mit dem Fall „Request in der Luft":** `reset()`, während
  ein REMOVE unbeantwortet ist, dann bestätigt 7TV ihn ⇒ die Zeile wird `done`, das Ziel steht in
  `sync-deleted` (Codex-Befund 1); `reset()` während `settling` ⇒ Nachlesen endet, beide Meldungen
  gehen raus, Lauf schließt; `reset()` während eine Meldung unterwegs ist ⇒ ihre Antwort landet im
  Datensatz, Lauf schließt, `isSettling` fällt erst danach — in allen drei Fällen sieht der
  `HttpTestingController` jede Meldung **genau einmal**, `run()` ist `null` (Ausnahme: der
  Wiederanzeige-Fall unten), die Projektionen stehen auf `idle`, und der Arbiter (hier: die zwei
  Dienst-Signale) ist danach frei; `queue()` ist erst nach `finish()` leer
- **Wiederanzeige (Festlegung Nr. 13):** `reset()` während `reporting`, dann endet die Meldung
  `failed` (403) oder `partial` ⇒ `run()` zeigt den Lauf wieder, die Projektion trägt `failed` samt
  Grund, `retrySyncReport()` sendet erneut; ist inzwischen ein anderer Lauf gezeigt ⇒ keine
  Wiederanzeige, `console.warn` (englisch) mit `runId` und Grund, der Datensatz behält den Zustand
- **Zeitrahmen (Festlegung Nr. 15):** eine Meldung ohne Antwort ⇒ nach `REPORT_TIMEOUT_MS` zählt
  der Versuch als transient, Retry-Policy wie bei 429; nach den Retries `failed`/`unavailable`,
  Lauf `closed`, Schließen möglich
- **Schließen-Gate:** `dismissible` falsch während `settling` und `reporting`, wahr ab `closed` —
  auch bei `failed`
- ein zweiter `startImport` während der erste settelt (nur konstruiert erreichbar, weil die Engine
  frei ist): beide Läufe schließen, jede Meldung genau einmal, `run()` zeigt den zweiten, die
  Antworten des ersten schreiben in seinen Datensatz und nie in die Projektion des zweiten (das ist
  der bisherige „superseded run"-Fall mit neuer Erwartung: nicht verworfen, sondern verbucht)
- Kanalwechsel: der Import hat kein `resetIfChannelChanged` (DECISIONS 2026-09-06), unverändert
- `doneAdoptCount` (#255) liefert nach dem Umbau dieselben Werte (Bestandsfall bleibt grün)

**Tests:** `seven-tv-run-lifecycle.spec.ts` (neu) **≥ 12** (eröffnen, patchen, Phasen, Schließen
nur ohne offene Meldung, `isSettling`/`destructiveOpen` über zwei Läufe, Anzeige lösen ohne
Schließen, Retry am geschlossenen Lauf, Identität per `runId` nach Ersatz des Objekts,
Wiederanzeige bei leerer Anzeige, keine Wiederanzeige bei belegter);
`seven-tv-import.service.spec.ts` **+15 / ±4** (die Liste oben inklusive Request-in-der-Luft,
Wiederanzeige, Zeitrahmen; die vier Bestandsfälle aus 0.6 mit geänderter Erwartung;
`describe('beforeunload guard')` bleibt hier, bis T3 ihn umzieht); `import-progress-section.spec.ts`
**+1 / ±0** (Schließen-Gate; die 48 Schreibzeilen bleiben); `usage-stats-page.spec.ts`,
`dock-outcome-announcer.spec.ts` **±0** (nur Fixtures).

**Abnahme:** `grep -n "this.run() !== started\|applyIfCurrent\|destructiveRunActive\|engine.cancel()"
web/src/app/core/seven-tv/seven-tv-import.service.ts` trifft nur `cancel()` selbst; `grep -n
"syncReport.set\|removalReport.set\|removalReportReason.set\|resyncTrigger.set\|protocolSaved.set"
web/src/app/core/seven-tv/seven-tv-import.service.ts` leer (Produktivcode schreibt den Datensatz,
nicht die Projektion — Ausnahme nur `protocolSaved` über `markProtocolSaved`, falls es so heißt;
dann als Datensatz-Feld); `grep -rn "destructiveRunActive" web/src/app` nennt nur noch
Spec-Zeilen, die T3 umzieht, oder ist leer; DECISIONS-Eintrag 1 steht oben. **Vertrag P1 (Import),
P6 (Import).**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/core/seven-tv/**/*.spec.ts'
--include='src/app/shared/seven-tv/import-progress-section.spec.ts'
--include='src/app/shared/seven-tv/dock-outcome-announcer.spec.ts'
--include='src/app/shared/seven-tv/import-trigger.spec.ts'
--include='src/app/features/usage-stats/usage-stats-page.spec.ts'`; `npm --prefix web run build`;
`cd web && npx tsc -p tsconfig.spec.json --noEmit`; Lint, Format.

**Commit:** `feat(seventv): close import runs run-bound and keep their reports after reset`.
**Abhängigkeiten:** T0. **Modell:** `opus`.

### T2 — Delete und Restore auf den Lebenszyklus (Vertrag P1, P6 für beide)

**Ziel:** Delete- und Restore-Dienst laufen auf dem Baustein aus T1: eigener Laufdatensatz mit
Phase, Meldung am Datensatz, kein Frühausstieg, `isSettling`/`destructiveOpen` je Dienst.

**Vertrag:** Spec 11.1 P1, P6; Festlegungen Nr. 3, 5, 6; #255-Fassung des Restore (0.7).

**Dateien:** `web/src/app/core/seven-tv/seven-tv-delete.service.ts` (+ `.spec.ts`): `run` wird
ein Signal (heute privates Feld), `lastRun`/`syncReport`/`syncReportReason` Projektionen mit
unverändertem Shape (Naht 2.4); `DeleteRunInfo` += `runId`, `phase`, `destructive: true` (jede
Delete-Zeile ist destruktiv — Festlegung Nr. 6), Meldungsfelder; `onRunComplete` ohne
Frühausstieg; `reportDeleted` per `runId` mit `REPORT_TIMEOUT_MS`; **`resetIfChannelChanged`
löst nur einen `closed`-Lauf** (heute: nur nicht während `running` — Festlegung Nr. 13);
`confirmedRunPending`-Mechanik unverändert; `reset()` während `running` bricht nicht ab
(Festlegung Nr. 3). `web/src/app/core/seven-tv/seven-tv-restore.service.ts` (+ `.spec.ts`):
`RestoreRunInfo` += die Basisfelder, `destructive: false` (nur ADDs — `destructiveOpen` konstant
falsch); `syncReport`/`syncReportReason`/`resyncTrigger` schreibbare Projektionen (Nr. 14);
`reportRestored`/`resyncAfterReport`/`triggerResync` per `runId`, **auf der #255-Fassung** (kein
Client-Resync für nicht-aktives Ziel); `resetIfChannelChanged` nur bei `closed`; `applyIfCurrent`
entfällt in beiden; Wiederanzeige bei nicht erfolgreicher Meldung eines abgelösten Laufs (Nr. 13)
in beiden. `web/src/app/shared/seven-tv/mass-delete-panel.ts` (`[dismissible]` auf „Lauf `closed`"
am Delete-Panel; sonst nichts) + `.spec.ts` (21 Schreibzeilen bleiben; ein Fall zum Gate);
`web/src/app/shared/seven-tv/restore-progress-section.ts` (`[dismissible]` ebenso) + `.spec.ts`
(14 Schreibzeilen bleiben); `usage-stats-page.spec.ts` (`lastRun.set` ×3 — Shape unverändert,
läuft mit); `dock-outcome-announcer.spec.ts` (Restore-Anteil); `web/public/i18n/{de,en}.json`
(`massDelete.settling`, `restore.settling` — das Panel zeigt `<prefix>.settling`, sobald
`dismissible` falsch und `isRunning` falsch ist; heute gibt es den Schlüssel nur für `import`;
Wortlaut vorläufig, wie `import.settling`); `docs/DECISIONS.md` (Eintrag 1:
`Betrifft:` um beide Dienste ergänzt, ein Absatz zu Delete als destruktivem Lauf im Unload-Schutz
— Festlegung Nr. 6 — und einer zum Schließen-Gate und zum Kanalwechsel — Nr. 13). Provider-Stubs
aus 0.6 nur, wo Build oder Spec-Typprüfung sie verlangen.

**Grenzfälle (je Dienst als Spec-Fall):** Engine fertig ohne `doneKeys` ⇒ sofort `closed` · mit
`doneKeys` ⇒ `reporting` bis Endzustand, dann `closed` · endgültig `failed` ⇒ `closed`, Fallback-
Resync (N1) läuft, ohne den Lauf offen zu halten · Retry am geschlossenen Lauf öffnet nicht wieder;
Retry-Sperre bei Kanal-Mismatch prüft die aufgespaltenen Werte aus #255 T3 (Ledger aus T0) · die
drei `reset()`-Fälle (`running`: Engine läuft zu Ende, **Request in der Luft wird abgewartet und
bei Bestätigung gemeldet** — Codex-Befund 1, der Fall, den `cancel()` heute verliert; `reporting`
mit Meldung unterwegs: Antwort landet im Datensatz, Lauf schließt; für Delete/Restore gibt es kein
`settling`, der Spec-Fall belegt den direkten Übergang) — jede Meldung genau einmal, Projektionen
danach `idle`, Signale frei · **`resetIfChannelChanged` mit fremdem Kanal während `reporting` ⇒
Lauf bleibt gezeigt** (Nr. 13), Meldung läuft zu Ende; endet sie `failed` ⇒ Lauf bleibt mit Grund
und Retry sichtbar; endet sie `succeeded` ⇒ nächster Kanalwechsel löst ihn (heute: der Reset
greift schon während `pending`, und ein späteres `failed` hätte keinen Ort — Codex-Befund 2) ·
programmatisches `reset()` während `reporting`, dann `failed` ⇒ Wiederanzeige, Retry sendet
erneut; bei inzwischen gezeigtem anderem Lauf ⇒ `console.warn`, keine Wiederanzeige · Meldung
ohne Antwort ⇒ `REPORT_TIMEOUT_MS`, Retries, `failed`/`unavailable`, `closed`, Schließen möglich ·
Schließen-Gate: `dismissible` falsch während `reporting`, wahr ab `closed` · der bisherige
„superseded run"-Fall (Delete ~663, Restore ~869) mit neuer Erwartung: verbucht im eigenen
Datensatz, Projektion des neuen Laufs unberührt · Delete: `destructiveOpen` wahr von `startDelete`
bis `closed`, auch nach `reset()`; Restore: nie.

**Tests:** `seven-tv-delete.service.spec.ts` **+10 / ±3**, `seven-tv-restore.service.spec.ts`
**+10 / ±3** (die Liste oben; Zählung im Bericht); `mass-delete-panel.spec.ts` **+1**,
`restore-progress-section.spec.ts` **+1** (Schließen-Gate); `usage-stats-page.spec.ts`,
`channel-workspace-layout.spec.ts` **±0**.

**Abnahme:** `grep -n "this.run !== started\|this.runState() !== started\|applyIfCurrent\|engine.cancel()"`
in beiden Diensten trifft nur `cancel()` selbst; `grep -n "syncReport.set\|syncReportReason.set\|resyncTrigger.set\|lastRun.set"`
in beiden Diensten leer; `lastRun`-Leser in `mass-delete-panel.ts`/`usage-stats-page.ts`
unverändert (Diff der Lesestellen leer; das Panel ändert nur `[dismissible]`). **Vertrag P1, P6
(Delete, Restore).**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/core/seven-tv/**/*.spec.ts'
--include='src/app/shared/seven-tv/mass-delete-panel.spec.ts'
--include='src/app/shared/seven-tv/restore-progress-section.spec.ts'
--include='src/app/shared/seven-tv/dock-outcome-announcer.spec.ts'
--include='src/app/features/usage-stats/usage-stats-page.spec.ts'
--include='src/app/features/voting/**/*.spec.ts'
--include='src/app/features/channel-workspace/**/*.spec.ts'`; `npm --prefix web run build`;
`cd web && npx tsc -p tsconfig.spec.json --noEmit`; Lint, Format.

**Commit:** `feat(seventv): close delete and restore runs run-bound like the import`.
**Abhängigkeiten:** T1. **Modell:** `sonnet` — das Muster liegt nach T1 in einem Dienst vor; die
Testmatrix oben ist der Vertrag, und die Hauptsession liest den Diff (Memory).

### T3 — Arbiter: Registrierung, Settling-Sperre mit Grund, Unload-Vereinigung, Stub-Prüfbarkeit

**Ziel:** Der Arbiter kennt keine Dienstliste mehr. Dienste registrieren sich mit drei Signalen
und ihrer Sorte; `activeRun` meldet „belegt" bei laufend **oder** settelnd; `activeClaim` liefert
Sorte und Phase als Grund; `destructiveOpen` ist die Vereinigung und trägt den `beforeunload`;
`refusedStart`/`noteRefusedStart` sind die transiente Notiz-Mechanik für T4.

**Vertrag:** Spec 11.1 P2, P3, P4, P5; DECISIONS 2026-09-05 (R1 bleibt), 2026-09-06 (DI-Kante
gedreht, Festlegung Nr. 1); Festlegung Nr. 7 (Reihenfolge bei mehreren Anspruchstellern).

**Dateien:** `web/src/app/core/seven-tv/seven-tv-run-arbiter.ts` (+ `.spec.ts`): keine
`inject`-Zeile für einen Dienst mehr; Registry als Signal (damit `computed` auf eine späte
Registrierung reagiert); `register(participant)`; `activeRun` (laufend vor settelnd, sonst
Registrierungsreihenfolge), `activeClaim`, `destructiveOpen`, `refusedStart` mit 4000-ms-Fenster
nach §4.5 (Timer-Handle wird bei erneutem Setzen **zuerst** gelöscht; `DestroyRef`-Cleanup), der
`beforeunload`-Effekt samt `preventUnload` aus dem Import-Dienst (modulweite Funktion, exakt die
registrierte Referenz wird entfernt, `DestroyRef`-Cleanup); Klassen-Doku nennt die gedrehte Kante
und warum kein Zirkel entsteht. Die drei Dienste: `inject(SevenTvRunArbiter).register(...)` im
Konstruktor mit `kind` und den drei Signalen; der Import verliert seinen `beforeunload`-Effekt und
den `DestroyRef`-Import, falls sonst ungenutzt. Specs: `seven-tv-run-arbiter.spec.ts`
(Stub-Fälle in einem eigenen `describe` mit TestBed **ohne** die echten Dienste; die sechs
Bestandsfälle mit echten Diensten angepasst — „prefers delete" wird „running before settling",
Festlegung Nr. 7); die Unload-Fälle aus `seven-tv-import.service.spec.ts` ziehen hierher um (der
Import-Spec behält nur `destructiveOpen`-Fälle). `docs/DECISIONS.md` (Eintrag 2, oben: „The run
arbiter takes registrations, counts settling as busy and owns the unload guard"; nennt R1
unverändert, die gedrehte DI-Kante, die Vereinigung, dass ein laufender Delete den Tab jetzt
schützt — Festlegung Nr. 6 —, und dass die Notiz-Mechanik Arbitersache ist). Keine Änderung an
Startpunkten in diesem Task (T4) — sie lesen weiter `activeRun() !== null` und werden dadurch
bereits während des Settlings gesperrt.

**Grenzfälle (alle als Spec-Fall):** kein Teilnehmer ⇒ `activeRun` `null`, kein Listener ·
Stub `isRunning: false, isSettling: true` ⇒ `activeRun` = Sorte, `activeClaim.phase` = `settling` ·
Stub `isRunning: true` und zweiter Stub `isSettling: true` ⇒ der laufende gewinnt, unabhängig von
der Reihenfolge · zwei Stubs gleicher Phase ⇒ der zuerst registrierte · Stub `destructiveOpen:
true` bei `isRunning: false, isSettling: false` (der Fall „`run()` ist `null`") ⇒ `addEventListener('beforeunload', …)`
gerufen, `removeEventListener` nicht; Signal auf `false` ⇒ entfernt (mit `TestBed.tick()`, Muster
aus dem heutigen Import-Spec) · zwei Stubs, einer `destructiveOpen: true` ⇒ scharf; beide `false`
⇒ aus · Registrierung **nach** der ersten Ableitung wird gesehen (Registry ist Signal) · doppelte
Sorte wird nicht abgewiesen (Vereinigung, Festlegung Nr. 7) · `noteRefusedStart('import')` bei
`activeClaim` `{ delete, running }` ⇒ `refusedStart` trägt beides, fällt nach 4000 ms
(`vi.useFakeTimers`), zweiter Aufruf innerhalb des Fensters setzt neu, ohne dass der alte Timer den
neuen löscht · `noteRefusedStart` bei freiem Arbiter ⇒ keine Notiz (nichts hat gesperrt — fail-closed
für die Anzeige heißt hier: nichts Falsches behaupten) · mit echten Diensten: Import mit
`replace`-Zeile und verlorener Antwort ⇒ `activeRun` `'import'` über Nachlesen **und** Meldung,
`null` erst nach dem Endzustand beider Meldungen; Delete ⇒ `'delete'` bis die Meldung antwortet.

**Tests:** `seven-tv-run-arbiter.spec.ts` **+12 / ±6**; `seven-tv-import.service.spec.ts` **−2
(Unload) / +1 (`destructiveOpen`)**.

**Abnahme:** `grep -n "seven-tv-delete.service\|seven-tv-restore.service\|seven-tv-import.service"
web/src/app/core/seven-tv/seven-tv-run-arbiter.ts` leer (P4: keine Dienstliste); `grep -rn
"beforeunload" web/src/app --include=*.ts -l` nennt nur den Arbiter und seinen Spec; jeder Dienst
hat genau einen `register(`-Aufruf; DECISIONS-Eintrag 2 steht oben. **Vertrag P2 (Arbiter-Hälfte),
P3, P4, P5.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/core/seven-tv/**/*.spec.ts'`;
danach die **volle** Vitest-Suite (Provider-Stubs in Seiten-Specs können durch die gedrehte Kante
reißen — ein Stub eines Dienstes, der den Arbiter nicht mehr injiziert, ist harmlos, ein Spec, der
den echten Dienst mit einem Arbiter-Stub ohne `register` kombiniert, nicht); `npm --prefix web run
build`; `cd web && npx tsc -p tsconfig.spec.json --noEmit`; Lint, Format.

**Commit:** `feat(seventv): let run services register with the arbiter, block while settling and guard unload as a union`.
**Abhängigkeiten:** T2. **Modell:** `opus`.

### T4 — Startpunkte: Grund als transiente Notiz, Seite und Panel, i18n

**Ziel:** Jeder Startpunkt fragt weiterhin nur den Arbiter und meldet einen abgewiesenen Start
mit `noteRefusedStart(eigene Sorte)`; die Nutzungsseite zeigt `refusedStart` als transiente
Statusmeldung nach §4.5; das `MassDeletePanel` zeigt denselben Grund in seinem `abortNotice`;
die Texte sind vorläufig, in beiden Sprachen.

**Vertrag:** Spec 11.1 P2 (Startpunkt-Hälfte); `docs/UI-Designsprache.md` §4.5; Festlegung Nr. 8
(Dialog-Sperre bleibt still); #255-Fassung der Flows (0.7).

**Dateien:** `web/src/app/shared/seven-tv/import-flow.ts` (beide Prüfungen: `noteRefusedStart('import')`
statt stillem `return`; `runBlocked` bleibt), `web/src/app/shared/seven-tv/restore-flow.ts` (alle
Prüfstellen der #255-Fassung: `noteRefusedStart('restore')`),
`web/src/app/shared/seven-tv/mass-delete-panel.ts` (Delete-Pfad: `abortNotice` mit Grund aus
`activeClaim` — Familie `sevenTvRun.notStarted.*` mit `kind`-Parameter statt
`massDelete.anotherRunStarted`; die Restore-Pfade des Panels: `abortNotice` mit Restore-Leadzeile
nach Plan-253 §6 Nr. 3 statt stillem Abbruch; `openConfirm` vor dem Dialog bleibt still, weil der
Button disabled ist und nichts bestätigt wurde), `web/src/app/features/usage-stats/usage-stats-page.html`
und `.ts` (Rendering von `arbiter.refusedStart()` in der Zählzeile neben `selectionPrunedFeedback`:
permanente `sr-only`-`role="status"`-Region, sichtbarer `aria-hidden`-Zwilling; **kein** eigener
Timer auf der Seite — das Fenster hält der Arbiter), `web/src/app/features/voting/vote-session-detail-page.html`
(**keine** Änderung: dort ist das Panel der einzige Startpunkt, und es hat sein `abortNotice`),
`web/public/i18n/{de,en}.json` (Familie aus 0.5; `massDelete.anotherRunStarted` entfernt),
`docs/UI-Designsprache.md` §4.5 (Referenzliste um die Notiz ergänzt — englisch), Specs aus 0.6
(Arbiter-Stubs bekommen `activeClaim`, `refusedStart`, `noteRefusedStart`).

**Vorläufiger Wortlaut (de):** `sevenTvRun.notStarted.running` = „Nichts gestartet — {{ kind }} läuft
noch." · `sevenTvRun.notStarted.settling` = „Nichts gestartet — {{ kind }} wird noch abgeschlossen."
· `sevenTvRun.kind.delete` = „der Löschlauf", `.restore` = „die Wiederherstellung", `.import` =
„die Übertragung" (Verb nach #92 „Übertragen"). Englisch entsprechend. Die Deixis-Falle aus Memory
gilt: kein „dieser/jener" Lauf.

**Grenzfälle (als Spec-Fall):** Import-Flow: Arbiter meldet `{ import, settling }` nach der
Bestätigung ⇒ kein `startImport`, `noteRefusedStart('import')` genau einmal; zweite Prüfung nach
`recheckTransferPlan` ebenso · Restore-Flow: je Prüfstelle einmal, auch am Direktstart bei leerer
Vorschau (#255) · Panel-Delete: `abortNotice` trägt `sevenTvRun.notStarted.running` mit `kind` des
Sperrenden; Panel-Restore: Restore-Leadzeile + Grund · Seite: `refusedStart` gesetzt ⇒ Region
enthält den Text, Zwilling sichtbar; `null` ⇒ Region leer, Zwilling nicht gerendert; der Text
identifiziert nur die Meldung (Regel 12) · der Bestätigungsdialog bleibt bei `runBlocked` still
gesperrt (Bestandsfall „locks it silently while another 7TV run is going" unverändert).

**Tests:** `import-flow.spec.ts` **+2**, `restore-flow.spec.ts` **+3** (je Prüfstelle der
#255-Fassung), `mass-delete-panel.spec.ts` **+3 / ±1**, `usage-stats-page.spec.ts` **+2**;
Locale-Parität (falls ein Spec Schlüsselgleichheit beider Dateien prüft: grün).

**Abnahme:** `grep -rn "activeRun() !== null" web/src/app/shared/seven-tv/import-flow.ts
web/src/app/shared/seven-tv/restore-flow.ts` — jede Fundstelle steht unmittelbar neben einem
`noteRefusedStart`; `grep -rn "anotherRunStarted" web/` leer; kein Startpunkt liest
`isSettling`/`settlement` eines Dienstes direkt (`grep -rn "isSettling\|settlement"
web/src/app/shared/seven-tv/import-flow.ts web/src/app/shared/seven-tv/restore-flow.ts
web/src/app/shared/seven-tv/mass-delete-panel.ts` leer — dieselbe Lesart wie #254 AK 32).
**Vertrag P2 (Startpunkte).**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/**/*.spec.ts'
--include='src/app/features/**/*.spec.ts'`; `npm --prefix web run build`; `cd web && npx tsc -p
tsconfig.spec.json --noEmit`; Lint, Format.

**Commit:** `feat(seventv): say why a confirmed run did not start while another one runs or settles`.
**Abhängigkeiten:** T3. **Modell:** `sonnet`.

### T5 — Punkt 4: das Dock sagt, wo ein unklarer REMOVE steht

**Ziel:** Der Import-Laufdatensatz zählt `replace`-Zeilen, deren REMOVE auch nach dem Nachlesen
unbeantwortet blieb, und das Dock sagt bei `> 0`, dass diese Entfernungen im Ergebnisprotokoll
nicht als bestätigt stehen und nur die Rückweg-Datei sie deckt.

**Vertrag:** Issue Punkt 4; #254 Spec 11.2 (gleicher Schlüssel-Aufbau, gleiche Stelle wie
`undo.summary.unknownRecordedIn`); 0.2 Nr. 8 (Bedingung: `status === 'unknown'`, Aktion `replace`,
`failedStep === 0`).

**Dateien:** `web/src/app/core/seven-tv/seven-tv-import.service.ts` (`ImportRunInfo.unknownRemovalCount`,
in `outcomeCounts` mitgezählt, `0` im Flug) + `.spec.ts`; `web/src/app/shared/seven-tv/import-progress-section.ts`
(Zeile unter `unknownRows`, plural, gleiche Klasse wie die Nachbarzeile; nicht `aria-hidden`, wie
`unknownRows`) + `.spec.ts`; Fixtures aus 0.6; `web/public/i18n/{de,en}.json`
(`import.summary.unknownRecordedIn.one/other`, vorläufig: „Bei {{ count }} Ersetzung ist unklar,
ob das Ziel-Emote entfernt wurde — im Ergebnisprotokoll steht sie nicht als bestätigte
Entfernung, nur die Rückweg-Datei deckt sie ab.").

**Grenzfälle:** REMOVE `unknown`, Nachlesen zeigt Ziel noch da ⇒ Zeile `failed@0`, zählt **nicht**
· REMOVE `unknown`, Ziel weg ⇒ `failed@1` mit `completedSteps 1`, zählt **nicht** (im Protokoll
bestätigt) · REMOVE `unknown`, Nachlesen gescheitert/unvollständig ⇒ bleibt `unknown@0`, zählt ·
ADD `unknown` (Schritt 1) ⇒ zählt nicht · zwei solcher Zeilen ⇒ 2, Pluralschlüssel · `0` ⇒ keine
Zeile.

**Tests:** `seven-tv-import.service.spec.ts` **+3**, `import-progress-section.spec.ts` **+2**.

**Abnahme:** `unknownRemovalCount ≤ unknownCount` in jedem Spec-Fall (Assertion); Schlüsselname
bytegleich mit dem Aufbau `<family>.summary.unknownRecordedIn`. **Issue Punkt 4.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/core/seven-tv/seven-tv-import.service.spec.ts'
--include='src/app/shared/seven-tv/import-progress-section.spec.ts'
--include='src/app/shared/seven-tv/dock-outcome-announcer.spec.ts'
--include='src/app/features/usage-stats/usage-stats-page.spec.ts'` (die `ImportRunInfo`-Fixtures
dort bekommen das neue Feld); `npm --prefix web run build`; `cd web && npx tsc -p
tsconfig.spec.json --noEmit`; Lint, Format.

**Commit:** `feat(import): say in the dock where an unconfirmed removal is recorded`.
**Abhängigkeiten:** T1. **Modell:** `sonnet`.

### T6 — Punkt 2: „Ziel neu laden" nach Drift liest das Set live

**Ziel:** Nach einem Drift lädt der Banner-Knopf „Ziel neu laden" das Ziel über den Live-Zweig des
Loaders (`loadEmoteSetPreview` für das bereits aufgelöste Set), nicht über die Postgres-Vorschau —
für ein getracktes aktives Set der Unterschied zwischen „noch eine Drift-Runde" und „eine
Vorschau-Berechtigung".

**Vertrag:** Issue Punkt 2; 0.2 Nr. 7; Festlegung Nr. 9 (eine Vorschau-Berechtigung je Drift-Reload);
#255-Fassung des Dialogs (0.7: `liveOccupiedSlots` bleibt).

**Dateien:** `web/src/app/shared/seven-tv/import-flow.ts` (`reloadLive`: setzt die Generation,
`status: 'loading'`, ruft `loadImportTarget` mit `'trackedSet'` (getrackt) bzw. `'untrackedSet'`
(ungetrackt) und der `setId` des **zuletzt geladenen** `ready`-Zustands — nie aus `target`
neu abgeleitet (F5-Regel aus #200); ohne bekannten `ready`-Zustand fällt `reloadLive` auf `load()`
zurück — fail-closed: lieber die alte Vorschau als kein Reload; `withChosenSetName` gilt weiter),
`web/src/app/shared/seven-tv/import-confirm-dialog.ts` (`ImportConfirmDialogData.reloadLive`; der
Banner-Knopf der `drifted`-Notiz ruft `reloadLive`, der der `readFailed`-Notiz weiter `retry`;
`import.resolve.reloadTargetFirst` bleibt als Sperrgrund, sein Text darf auf den Knopf zeigen),
Specs aus 0.6, `docs/DECISIONS.md` (Eintrag 3, kurz: „After a drifted replace target, reload reads
the target live"; nennt AK 36 aus #200 als unberührt — der **erste** Load bleibt der heutige Weg),
`docs/UI-Designsprache.md` §7.2 (ein Satz zum Drift-Reload, englisch).

**Grenzfälle (als Spec-Fall):** Flow: Drift ⇒ `reloadLive` ⇒ genau ein `loadEmoteSetPreview`-Aufruf
mit der geladenen `setId`, keine `getSetStatus`/`listEmotes`-Requests; ohne vorherigen `ready`
⇒ heutiger Weg; Generation: eine ältere Antwort nach `reloadLive` schreibt nichts · Dialog: Klick
auf den Drift-Banner-Knopf ruft `reloadLive`, nicht `retry`; `readFailed` ruft `retry`; nach dem
Reload sind Overlays und Notiz weg (linkedSignal, Bestandsverhalten) · ungetracktes Ziel:
`'untrackedSet'` mit `twitchLogin` — der heutige Weg ist dort schon live, der Reload ändert nur
nichts.

**Tests:** `import-flow.spec.ts` **+3**, `import-confirm-dialog.spec.ts` **+2**.

**Abnahme:** `grep -n "reloadLive" web/src/app/shared/seven-tv/import-confirm-dialog.ts` trifft
genau den Drift-Knopf und die Data-Schnittstelle; DECISIONS-Eintrag 3 steht oben. **Issue Punkt 2.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/import-flow.spec.ts'
--include='src/app/shared/seven-tv/import-confirm-dialog.spec.ts'
--include='src/app/shared/seven-tv/foreign-import-flow.spec.ts'`; `npm --prefix web run build`;
`cd web && npx tsc -p tsconfig.spec.json --noEmit`; Lint, Format.

**Commit:** `fix(import): reload the target live after a drifted replace target`.
**Abhängigkeiten:** T0 (unabhängig von T1–T3). **Modell:** `sonnet`.

### T7 — Punkt 5: die Zustandsmaschine aus dem Bestätigungsdialog

**Ziel:** Die Maschine `idle → verifying → saved` mit ihren Regeln lebt als eigene, pur getestete
Einheit `RecoveryFileGate`; der Dialog konsumiert sie und behält nur, was Dialogsache ist
(Overlays, Entscheidungen, Namen im Banner, Slot-Projektion). #254 T5 baut den Undo-Dialog auf
derselben Einheit.

**Vertrag:** Issue Punkt 5; #254 Spec 11.3 und Festlegung 9 dort (entfällt, sobald dieser Task
gemergt ist); 0.2 Nr. 9; #255-Fassung (`liveOccupiedSlots`).

**Dateien:** `web/src/app/shared/seven-tv/recovery-file-gate.ts` (neu, + `.spec.ts`): Zustand
(`idle | verifying | saved`, mit dem Plan, für den er gilt, und `stampedPlan`), abgeleiteter
Zustand nach der Regel „ein anderer aktueller Plan ⇒ `idle`", Notiz (`readFailed`, `saveFailed`,
`drifted` mit Schlüsseln), `verifyAndSave(target, plan)`, „nur der neueste Read antwortet"
(inklusive der Fälle 1842, 1865, 1906 der Dialog-Spec: verspätete Antwort, verspäteter Fehler,
Antwort für einen inzwischen anderen Plan), Timeout `LIVE_READ_TIMEOUT_MS` (Konstante zieht
um), Abbruch beim Zerstören; Abhängigkeiten als Funktionen: Read, Verifikation (liefert
`available` und `drifted`), Stempeln, Speichern (wirft bei Verweigerung), Drift-Rückruf, ein
Beobachter für jede erhaltene Live-Antwort (für `liveOccupiedSlots`); generisch über Plan- und
Drift-Typ, damit der Undo seine Klassifikation einhängen kann — **keine** Angular-Injektion in der
Einheit (`signal`/`computed` ohne Kontext, `DestroyRef` als Parameter). `web/src/app/shared/seven-tv/import-confirm-dialog.ts`:
`rawActionState`, `actionState`, `isVerifying`, `targetRead`, `verifyAndSave`, `onTargetRead`
werden durch die Einheit ersetzt; `applyDrift`, `targetOverlays`, `targetCheckNotice`-Rendering
(Namen aus Schlüsseln), `execute`, `close`, `liveOccupiedSlots` bleiben im Dialog; Klassen-Doku
verweist auf die Einheit. `import-confirm-dialog.spec.ts` bleibt als Integrationsspec unverändert
(keine Zeile gelöscht — das ist die Abnahme).

**Grenzfälle (in der Maschinen-Spec):** Planwechsel während `verifying` ⇒ Antwort verworfen, `idle`
· Planwechsel nach `saved` ⇒ liest als `idle`, `stampedPlan` nicht mehr angeboten · Read-Fehler ⇒
`idle` + `readFailed` (nur, wenn der Plan noch aktuell ist) · `available: false` ⇒ `idle` +
`readFailed` · Drift ⇒ Rückruf mit den Drifts, `drifted`-Notiz, `idle` · Speichern wirft ⇒ `idle` +
`saveFailed` · Erfolg ⇒ `saved` mit gestempeltem Plan, `verifiedAt` an den Speicherer · zweiter
`verifyAndSave` während des ersten ⇒ der erste antwortet nie mehr · Zerstören während `verifying`
⇒ kein Rückruf mehr · jede Live-Antwort erreicht den Beobachter genau einmal, auch bei Drift.

**Tests:** `recovery-file-gate.spec.ts` (neu) **≥ 12**; `import-confirm-dialog.spec.ts`
**±0** (unverändert grün).

**Abnahme:** `git diff --stat` zeigt für `import-confirm-dialog.spec.ts` keine Änderung; `wc -l
import-confirm-dialog.ts` liegt unter 1.200 (heute 1.327 + #255); `grep -n "liveOccupiedSlots"
import-confirm-dialog.ts` trifft weiterhin eine Setz-Stelle, gespeist aus dem Beobachter; `grep -rn
"inject(" recovery-file-gate.ts` leer. **Issue Punkt 5.**

**Gates:** `npm --prefix web test -- --watch=false --include='src/app/shared/seven-tv/**/*.spec.ts'`;
`npm --prefix web run build`; `cd web && npx tsc -p tsconfig.spec.json --noEmit`; Lint, Format.

**Commit:** `refactor(import): extract the verify-and-save state machine from the confirm dialog`.
**Abhängigkeiten:** T6. **Modell:** `opus`.

### T8 — E2E: das Settling-Fenster sperrt Starts und hält den Unload-Schutz

**Ziel:** Über die echte Angular-App mit gemockten `/api/**`- und 7TV-GQL-Routen ist belegt: während
ein Ersetzungslauf settelt, sind die Startpunkte gesperrt, der `beforeunload`-Schutz ist scharf,
und nach dem Endzustand beider Meldungen ist beides wieder frei.

**Vertrag:** Spec 11.1 P2, P3 (Nutzerseite); Memory-Fallen (`page.clock.install()` vor `goto`,
`runFor` statt `fastForward`, `role=status` ohne accessible name — nach Rolle matchen, dann
`hasText`; Speicherdruck macht Fälle rot, Suite allein wiederholen).

**Dateien:** `web/e2e/emote-import.e2e.spec.ts` (neues `describe('running import: the settling
window')` neben „running import: leaving the page"; Muster aus „a lost ADD answer settles green…"
um 3294 für den verlorenen REMOVE/ADD und den Re-Read), ggf. `web/e2e/support/mocks.ts` (ein
Helfer, der die Re-Read-Route **offen hält**, bis der Test sie beantwortet — das Fenster wird
nicht per Uhr, sondern per unbeantworteter Route gehalten; kein `page.clock` nötig).

**Fälle:** (a) Lauf mit `replace`-Zeile, deren Antwort verloren geht ⇒ Engine fertig, Re-Read
offen: `app-import-trigger` und „Übertragen" sind `disabled`, das Dock zeigt „Wird
abgeschlossen…"; Re-Read beantwortet, beide Meldungen (`sync-imported`, set-zentrisches
`sync-deleted`) `204`/Antwort ⇒ beide Knöpfe wieder frei. (b) Im selben Fenster:
`page.evaluate` sendet ein `cancelable` `beforeunload`-Ereignis und liest `defaultPrevented` —
`true` während Re-Read und Meldung, `false` nach dem Endzustand; zusätzlich **ein** Fall mit
`page.close({ runBeforeUnload: true })` und `page.on('dialog')`, der `dialog.type() ===
'beforeunload'` sieht und `dismiss()` ruft (Playwright-dokumentierter Weg; echte Navigationen
bräuchten Nutzeraktivierung und sind deshalb **nicht** der Prüfweg). (c) Delete-Lauf ⇒
`defaultPrevented` `true` bis `sync-deleted` antwortet (Festlegung Nr. 6 sichtbar gemacht).

**Nicht per E2E:** die Notiz „Nichts gestartet" — ihr Auslöser (Bestätigung *vor* dem fremden
Start, Start *hinter* dem Modal) ist mit einem einzelnen Browser nicht ohne Konstruktion erreichbar;
die Unit-Specs aus T4 tragen sie.

**Tests:** **+3** E2E-Fälle. **Abnahme:** Suite ohne Api auf `:5151` grün, Laufzeit der drei
Fälle zusammen < 30 s (kein Realzeitwarten). **Gates:** `npm --prefix web run e2e --
e2e/emote-import.e2e.spec.ts`.

**Commit:** `test(e2e): cover the settling window for starts and the unload guard`.
**Abhängigkeiten:** T4. **Modell:** `opus`.

### T9 — Volle Gates, Coverage, Bundle-Gate, Zweitmeinung

**Ziel:** Alle Gates des Repos grün, Coverage-Vorabschätzung gelesen, Bundle gegen die Baseline
verglichen, Codex-Zweitmeinung eingeholt und unverändert wiedergegeben.

**Schritte:** Zuerst der Stand von #255: `git fetch origin`; ist #255 in `origin/feat/emote-sets-200`
gemergt ⇒ `git rebase origin/feat/emote-sets-200` (die #255-Commits fallen dabei heraus, weil sie
im Epic liegen; Konflikte nur in `docs/DECISIONS.md` denkbar, neuere oben), danach ein
`git push --force-with-lease`; ist er noch nicht gemergt, aber `origin/fix/255-wording-counts` hat
Review-Fixes über dem in T0 gemergten SHA ⇒ diese nachmergen; erst dann die Gates.
`npm --prefix web test -- --watch=false` (voll); `cd web && npx tsc -p tsconfig.spec.json
--noEmit`; `npm --prefix web run e2e` (voll,
ohne Api auf `:5151`; rote Fälle einmal allein wiederholen — Memory Speicherdruck); `dotnet test
EmotePurge.slnx` (Docker; unverändertes Backend — grün ist die Fertigmeldung des Repos, nicht
optional); `npm --prefix web run lint`, `npm --prefix web run format` (Diff leer); `node
scripts/coverage-local.mjs --frontend-only` (Ergebnis ins Ledger, nur Committetes zählt —
Memory); `npm --prefix web run build`: „Initial total" ≤ T0-Baseline, Initial-Chunk-Liste
unverändert (`--stats-json`-Vergleich), `grep -rn "seven-tv-run-arbiter\|seven-tv-run-lifecycle\|recovery-file-gate"
web/src/app/app.routes.ts web/src/app/app.config.ts web/src/app/app-shell.ts` leer. Dann die
Zweitmeinung, direkt im Worktree: `node ~/.claude/plugins/cache/openai-codex/codex/1.0.6/scripts/codex-companion.mjs
review "--wait --model gpt-6-sol --scope branch --base origin/feat/emote-sets-200"` (Memory:
`--scope branch` erzwingen; „failed to output"/Exit 1 kann Kontingent sein; ein verwaister Broker
bei wiederverwendetem Worktree-Pfad meldet „failed to load configuration"). Befunde
**unverändert** ins Ledger und in den Bericht; Widerspruch zwischen Opus-Review der Hauptsession
und Codex ⇒ Fable als Schiedsrichter mit nur den strittigen Findings (global). Umsetzung von
Befunden ist ein eigener Task auf Anweisung, nie Teil von T9.

**Fertig-Kriterium:** alle Gates grün, Bundle-Zeile und Coverage im Ledger, Codex-Ergebnis
wörtlich im Bericht. **Modell:** `haiku` für die Läufe; die Bewertung macht die Hauptsession.

### T10 — Verifikation im Browser (nicht Regel 16), PR

**Ziel:** Der PR gegen `feat/emote-sets-200` steht mit Beschreibung, Vertragsabbildung für #254
T0 und der Liste vorläufiger Wortlaute; ein kurzer Browser-Handgriff belegt das eine Verhalten, das
kein Mock beweist.

**Entscheidung zur Live-Verifikation:** Regel 16 verlangt Live-Verifikation für
**Backend-Features** gegen echte Zugänge. Dieser Plan ändert kein Backend, keinen 7TV-Request-Vertrag
und keine Meldungs-Bodies; alles Geänderte ist Client-Timing und Client-Zustand, das E2E (T8) und
die Dienst-Specs (T1–T3) mit denselben Antwortformen belegen, die Prod liefert. Ein echter
Ersetzungslauf gegen 7TV nur für das Settling-Fenster würde ein Set mutieren, um eine Wartezeit zu
beobachten. **Daher kein Regel-16-Gate.** Was ein Mock nicht zeigt, ist die Browser-eigene
Nachfrage beim Schließen des Tabs (Playwright dismisst sie nur): **ein** Handgriff im lokalen
Dev-Stack aus dem Haupt-Checkout (nicht aus dem Worktree — Memory: das Worktree-Cookie und der
Compose-Stack), ein Delete von **einem** entbehrlichen Emote im Testkanal, Tab schließen während
„Rückmeldung…" ⇒ der Browser fragt; danach den Restore aus dem Dock. Ergebnis in den PR-Text,
auch wenn er ausfällt (dann steht dort „nicht gelaufen").

**Dateien:** keine Code-Änderung; PR-Text (englisch) mit: Vertragsabbildung P1–P6 → Datei:Zeile
und Testfall (die Tabelle, die #254 T0 abfragt), Abschnitt 4 dieses Plans als Checkliste, die
Verhaltensänderungen (Settling sperrt; Delete schützt den Tab; Retry öffnet nicht wieder;
Drift-Reload live), die vorläufigen Schlüssel für die Wortlaut-Runde, Codex-Befunde und ihre
Behandlung, das Bundle-Ergebnis. Der Merge bleibt beim Nutzer (Regel 1).

**Fertig-Kriterium:** PR offen mit dem Text oben; Ledger abgeschlossen. **Modell:** `sonnet`.

---

## 4. Nachverfolgung — was #254 T0 prüft, und wo es steht

| #254 T0 Schritt 2 | Vertrag | erfüllt durch | Beleg (Datei, Testfall) |
|---|---|---|---|
| (1) jeder Dienst liefert `isRunning`, `isSettling`, `destructiveOpen`; `isSettling` deckt Nachlesen **und** Meldung | P1 | T1 (Import), T2 (Delete, Restore) | Dienst-Specs: „isSettling über Nachlesen und Meldung", „closed erst nach dem Endzustand" |
| (2) Arbiter belegt bei laufend oder settelnd, mit Grund; Startpunkte fragen nur den Arbiter, zeigen den Grund transient | P2 | T3, T4 | `seven-tv-run-arbiter.spec.ts` (Stub `isSettling: true`), Flow-/Panel-/Seiten-Specs; Mechanik: `activeClaim` + `refusedStart`/`noteRefusedStart` + Rendering auf der Seite, `abortNotice` im Panel |
| (3) `beforeunload` = Vereinigung, hängt an offenen Läufen; registriert im Arbiter | P3 | T3 (T1 liefert `destructiveOpen`) | Arbiter-Spec (Stub `destructiveOpen: true` bei `run() === null`), E2E T8 (b), (c) |
| (4) vierter Dienst: eine Registrierung (`register`), ein Union-Wert (`SevenTvRunKind`); kein Dienst-Import im Arbiter | P4 | T3 | Abnahme-Grep T3 |
| (5) Arbiter-Spec hat die Stub-Fälle | P5 | T3 | `describe` mit Stubs ohne echte Dienste |
| (6) kein Frühausstieg; je Dienst die drei `reset()`-Fälle; Retry öffnet nicht wieder | P6 | T1, T2 | Abnahme-Greps T1/T2; die `reset()`-Fälle je Dienst; „Retry am geschlossenen Lauf" |
| Punkt 2 | Issue | T6 | `import-flow.spec.ts`, `import-confirm-dialog.spec.ts` |
| Punkt 4 | Issue, #254 Spec 11.2 | T5 | `import.summary.unknownRecordedIn`, Dienst- und Section-Spec |
| Punkt 5 | Issue, #254 Spec 11.3 | T7 | `recovery-file-gate.spec.ts`; #254 Festlegung 9 entfällt |

Die tatsächlichen Namen für #254 T4/T6/T7 (Registrierung, Union-Typ, Grund-Signal, Notiz-Mechanik,
Schutz-Signal) stehen in 0.5 und werden in T10 als Tabelle in den PR-Text übernommen.

---

## 5. Reihenfolge und Abhängigkeiten

```
Welle 0   T0
Welle 1   T1
Welle 2   T2 ─────────────┐     T5 (nach T1)     T6 (nach T0)
Welle 3   T3 (nach T2)    │                       T7 (nach T6)
Welle 4   T4 (nach T3, T5, T6, T7 gemergt)
Welle 5   T8 (nach T4)
Welle 6   T9
Welle 7   T10
```

- **Welle 2 (parallel, drei Worktrees):** T2 (`core/seven-tv/seven-tv-delete.service.ts`,
  `…-restore.service.ts` + Specs, `mass-delete-panel.spec.ts` nur Stubs), T5
  (`seven-tv-import.service.ts` — **nur** `outcomeCounts`/`ImportRunInfo`, `import-progress-section.ts`),
  T6 (`import-flow.ts`, `import-confirm-dialog.ts`). Gemeinsame Dateien: `web/public/i18n/{de,en}.json`
  (T5; Textkonflikt trivial), `docs/DECISIONS.md` (T2 ergänzt `Betrifft:` von Eintrag 1, T6 setzt
  Eintrag 3 oben — zwei verschiedene Stellen, Merge ohne Konflikt erwartet; sonst neuere oben).
  Preflight: T5 und T1 berühren dieselbe Datei — T5 startet erst nach dem Merge von T1.
- **Welle 3 (parallel, zwei Worktrees):** T3 (Arbiter, drei Dienst-Konstruktoren, Arbiter-Spec,
  Import-Spec) ∥ T7 (Dialog, neue Einheit). Keine gemeinsame Datei.
- **Welle 4:** T4 braucht T3 (Notiz-Mechanik), T5/T6/T7 nur, weil es die volle Suite fahren soll,
  ohne dass eine Lane noch Dateien hält.
- **Grüne Zwischenstände:** nach T1 ist der Arbiter unverändert (liest `isRunning`), der Import
  meldet schon lauf-gebunden; nach T2 dasselbe für alle drei; nach T3 sperrt Settling still (die
  Startpunkte lesen `activeRun() !== null`), erst T4 macht den Grund sichtbar. Kein Zwischenstand
  ist fail-open gegenüber heute.
- **Stapel auf #255:** Der Branch trägt bis zum Rebase in T9 die #255-Commits mit. Lanes, die in
  eigenen Worktrees laufen, zweigen von `fix/256-robustness` ab, nie vom Epic. Ein Rebase vor T9
  findet nicht statt, damit die Lanes eine stabile Basis haben.

---

## 6. Festlegungen des Plans, wo Vertrag und Issue schweigen — entschieden (Orchestrator, 2026-09-26), Betreiber-Veto möglich

| # | Stelle | Befund | Festlegung | Task |
|---|---|---|---|---|
| 1 | Vertrag P4 „registriert sich beim Arbiter" vs. DECISIONS 2026-09-06 „Dienste kennen den Arbiter nicht" | Die Registrierung dreht die DI-Kante | Dienste injizieren den Arbiter und registrieren sich im Konstruktor; der Arbiter injiziert **keinen** Dienst mehr — kein Zirkel. R1 (ableiten statt sperren) bleibt: die Registry hält Signale, keinen Lock. Alternative verworfen: Multi-Provider-Token in `usage-stats.routes.ts` (Root-Arbiter sieht Routen-Provider nicht; zwei Arbiter-Instanzen hießen zwei Unload-Effekte) | T3 |
| 2 | Vertrag P6: Laufdatensatz „ist ein eigener Datensatz" | Datensätze werden heute bei jedem Übergang als neues Objekt ersetzt; Identität per Referenz bricht dann | Identität per `runId`; der Baustein hält die Datensätze per Id, Rückrufe schreiben per Id | T1 |
| 3 | Vertrag P6 „`reset()` während `running` ⇒ die Engine läuft **oder** wird sauber abgebrochen" | Zwei erlaubte Lesarten; 0.2 Nr. 1: `engine.reset()` mitten im Lauf verliert das Ergebnis. **Fassung 1 wählte den Abbruch — falsch (Codex-Befund 1):** ohne `transportLossIsUnknown` (Delete, Restore) macht `cancelRemainingRows` einen Request in der Luft `cancelled`, obwohl 7TV ihn anwenden kann; die Mutation bliebe ungemeldet | **`reset()` während `running` lässt die Engine zu Ende laufen.** Es löst nur die Anzeige; der Baustein merkt sich, dass die Queue nach `finish()` zu leeren ist; `onRunComplete` verbucht das volle Ergebnis am Datensatz, jede bestätigte Zeile wird gemeldet. Erreichbar heute nur programmatisch (die Docks bieten Schließen erst nach dem Lauf, T1/T2 gaten es zusätzlich auf `closed`). Ein Spec-Fall je Dienst deckt den Request in der Luft, der nach dem `reset()` bestätigt wird | T1, T2 |
| 4 | Vertrag P6 „`closed`, wenn … jede seiner Meldungen einen Endzustand hat" | Ob der Resync eine Meldung ist, sagt der Vertrag nicht; #254 Spec 11.1 letzter Absatz zählt für den Undo „eine der beiden Meldungen" | Resync ist keine Meldung: er hält weder `isSettling` noch `destructiveOpen`; der periodische Worker-Sync ist sein Fallback | T1, T2 |
| 5 | Vertrag P1 für Delete/Restore | Kein Nachlesen dort | Phase `settling` wird übersprungen; `isSettling` = `reporting` | T2 |
| 6 | Vertrag P3 „mindestens ein Lauf mit destruktiver Zeile" | Jede Delete-Zeile ist destruktiv; heute schützt nur der Import den Tab | Delete armiert den Unload-Schutz von `startDelete` bis `closed` — sichtbare Verhaltensänderung, im DECISIONS-Eintrag 2 genannt; Restore nie | T2, T3 |
| 7 | Arbiter-Reihenfolge bei mehreren Anspruchstellern (heute „delete vor restore vor import", per Spec gepinnt) | Eine feste Sortenliste widerspräche P4 | `running` vor `settling`, sonst Registrierungsreihenfolge; doppelte Sorte erlaubt (Vereinigung). Der Fall bleibt konstruiert — die Startpunkte schließen ihn aus | T3 |
| 8 | Vertrag P2 „transiente Notiz" vs. DECISIONS 2026-09-05 „kein Hinweistext, der Fortschritt im Dock ist der Hinweis" | Die Modal-Sperre (`runBlocked`) verdeckt das Dock | Die Sperre **im** Dialog bleibt still (heutige Betreiberentscheidung); die Notiz erscheint, wenn eine **Bestätigung** nichts startet — auf der Seite (Flows) und im Panel (`abortNotice`) | T4 |
| 9 | Issue Punkt 2 | Ein Live-Reload kostet eine 7TV-Vorschau-Berechtigung (Memory: 10/min-Policy teilt Liste und Vorschau) | Nach einem Drift ist das der Preis für „keine zweite Drift-Runde"; nur der Drift-Knopf liest live, der erste Load und der `readFailed`-Knopf bleiben beim heutigen Weg (AK 36 aus #200 unberührt) | T6 |
| 10 | Issue Punkt 5 | Wie generisch die Einheit sein muss, sagt das Issue nicht; #254 Spec 11.3 nennt dieselben Übergänge mit anderer Klassifikation | Generisch über Plan- und Drift-Typ mit Funktions-Abhängigkeiten, ohne Injektion; `applyDrift` bleibt Dialogsache | T7 |
| 11 | Vertrag P2 „Grund … mindestens `running \| settling`, je Dienst" | Wortlaut ist #255-Sache, #255 ist dann gemergt | Familie `sevenTvRun.*`, vorläufig, gesammelt im PR-Text für eine Wortlaut-Runde | T4 |
| 12 | Regel 16 | Kein Backend, kein 7TV-Vertrag berührt | Kein Regel-16-Gate; ein Browser-Handgriff für die Tab-Nachfrage, Ergebnis transparent im PR | T10 |
| 13 | Vertrag P6 „`reset()`, `resetIfChannelChanged()` und ein neuer Lauf lösen nur die Anzeige" (Codex-Befund 2) | 0.2 Nr. 11: Ablösung während `reporting` ist heute per Kanalwechsel und per Schließen erreichbar; ein späteres `failed` der Meldung hätte weder Anzeige noch Retry — der Audit-Eintrag fehlte ohne sichtbaren Weg. Zwei Wege standen zur Wahl: (a) abgelöste Fehlschläge als eigene Liste im Dock sichtbar und wiederholbar halten; (b) die Ablösung bis zum Endzustand aufschieben. | **fail-closed, dreiteilig:** (1) **Schließen erst ab `closed`** — `dismissible` in allen drei Docks hängt am Lauf-Zustand; ein `failed`/`partial` ist ein Endzustand, also erscheint Schließen genau dann, wenn der Fehlschlag mit Grund und Retry sichtbar ist. (2) **`resetIfChannelChanged` löst nur einen `closed`-Lauf** — ein meldender Lauf folgt dem Nutzer für die Sekunden bis zum Endzustand in den nächsten Kanal; endet er `failed`, bleibt er dort mit Retry stehen, bis der Nutzer schließt oder erneut wechselt (heutiges Verhalten für fertige Läufe). (3) **Wiederanzeige:** endet die Meldung eines per programmatischem `reset()` abgelösten Laufs nicht `succeeded` und ist nichts gezeigt, zeigt der Dienst diesen Lauf wieder (`run()` gesetzt, das Dock mountet über `dockVisible`); ist ein anderer Lauf gezeigt, bleibt der Fehlschlag am Datensatz und im `console.warn`. Weg (a) verworfen: eine zweite Liste im Dock für einen Fall, den (1) und (2) im UI unerreichbar machen, wäre neue Fläche ohne Nutzer; (3) deckt den programmatischen Rest. **Die Umsetzung hängt nicht an einer Betreiberantwort** (Abschnitt 8) | T1, T2 |
| 14 | Vertrag P6 „Dienst-Signale … sind Projektionen des gezeigten Laufs" (Codex-Befund 3) | 99 Spec-Zeilen schreiben diese Signale (0.2 Nr. 12); ein `computed` bräche sie alle | `run` bleibt das schreibbare Signal des gezeigten Datensatzes; die Dock-Signale werden `linkedSignal`-Projektionen von `run()` — schreibbar für Specs, vom Produktivcode nie geschrieben (Abnahme-Grep je Dienst). Das erfüllt P6 (der Datensatz ist die Quelle, die Projektion folgt ihm) und hält die Specs | T1, T2 |
| 15 | Vertrag P6 „`closed` erreicht ein Lauf, wenn … jede seiner Meldungen einen Endzustand hat" | Die Meldeketten haben Retries, aber keinen Zeitrahmen; eine Anfrage ohne Antwort hielte den Lauf offen — mit Nr. 13 wäre dann Schließen nie möglich, mit Nr. 6 der Tab dauerhaft geschützt | `REPORT_TIMEOUT_MS = 30_000` je Versuch (großzügiger als der 20-s-Read, weil die Meldung serverseitig Resync-Stufen anstößt); Ablauf = transienter Fehler in derselben Retry-Policy; nach den Retries `failed`/`unavailable`, der Retry-Knopf bleibt (die Meldungen sind wiederholbar, das steht an jedem `retry…`) | T1 (Konstante), T1, T2 |

---

## 7. Offene Entscheidungen

Keine. Jede Stelle, an der Vertrag oder Issue schweigen, ist in Abschnitt 6 mit Begründung
entschieden und steht unter Betreiber-Veto; ein Veto trifft jeweils genau den genannten Task. Die
zwei Festlegungen, die der Nutzer im Produkt bemerken kann, stehen zusätzlich in Abschnitt 8.

---

## 8. Für den Betreiber zur Kenntnis

Drei Entscheidungen aus Abschnitt 6 sind ohne Rückfrage getroffen (2026-09-26, nachts) und stehen
unter Veto; keine davon blockiert die Umsetzung, ein Veto trifft nur T1/T2.

1. **Schließen wartet auf die Rückmeldung, und ein Lauf folgt beim Kanalwechsel bis dahin
   (Festlegung Nr. 13).** Im Lösch- und Wiederherstellungs-Dock erscheint „Schließen" künftig erst,
   wenn die Rückmeldung an EmotePurge einen Endzustand hat — in der Regel ein bis zwei Sekunden
   nach dem letzten Emote, mit Retries höchstens ~100 s (drei Versuche à 30 s plus Pausen);
   solange steht „Wird abgeschlossen…" — das `RunProgressPanel` zeigt in diesem Zustand
   `<prefix>.settling`, den es heute nur für `import` gibt; T2 legt `massDelete.settling` und
   `restore.settling` in beiden Sprachen an (vorläufiger Wortlaut wie beim Import). Ein Kanalwechsel in diesem
   Fenster nimmt den Lauf mit auf die nächste Seite; scheitert die Rückmeldung, bleibt der Lauf
   dort mit Grund und „Erneut melden" stehen, bis Sie schließen oder erneut wechseln. Grund: bisher
   konnte ein Kanalwechsel in genau diesem Fenster einen später gescheiterten Audit-Eintrag
   unsichtbar machen (Codex-Befund 2). Alternative, wenn Sie das nicht wollen: eine eigene Zeile
   „eine frühere Rückmeldung ist gescheitert" im Dock — ein neuer Baustein, in T2 austauschbar.
2. **Rückmeldungen haben jetzt einen Zeitrahmen von 30 s je Versuch (Festlegung Nr. 15).** Eine
   Rückmeldung, die länger als 30 s keine Antwort bekommt, gilt als vorübergehend gescheitert,
   wird wie ein 429 wiederholt und endet nach den Wiederholungen als „fehlgeschlagen" mit
   Retry-Knopf. Vorher konnte sie beliebig lange offen bleiben. Da die Rückmeldungen wiederholbar
   sind (ein doppelt gemeldetes Emote zählt einmal), ist ein falsches „fehlgeschlagen" nur ein
   Klick, kein Schaden.
3. **Nicht Teil dieses Plans, aber gefunden (Codex-Befund 1, am Code bestätigt):** „Abbrechen"
   während ein Lösch- oder Wiederherstellungs-Request bei 7TV in der Luft ist, markiert die Zeile
   als „abgebrochen", obwohl 7TV sie anwenden kann — die Delete- und Restore-Operationen setzen
   `transportLossIsUnknown` nicht (nur der Import mit `replace`-Zeilen tut es). Die Mutation wird
   dann nicht gemeldet, der periodische Sync heilt die Datenbank, der Audit-Eintrag fehlt. Dieser
   Plan vermeidet nur, denselben Pfad **programmatisch** zu nehmen (Festlegung Nr. 3); das
   Nutzer-„Abbrechen" bleibt, wie es ist. **Vorschlag:** Folge-Issue im Epic #200 („Delete/Restore:
   `transportLossIsUnknown` + Nachlesen wie beim Import"); die Memory-Regel „neue Issues gehören ins
   Epic" gilt.

---

## 9. Rückweg

Kein Backend, keine Migration, kein Dateiformat: der Rückweg ist ein Revert des PR. Die
Rückweg-Dateien und Ergebnisprotokolle bleiben in beiden Richtungen lesbar (T5 fügt eine
Dock-Zeile hinzu, keine Protokollzeile). #254 hängt an diesem PR: ein Revert nach dem Merge von #254
nimmt dessen Registrierung die Grundlage — die Reihenfolge beim Zurücknehmen ist dann #254 zuerst
(Plan-254 Abschnitt 8 sagt dasselbe).

---

## 10. Ledger

`.superpowers/sdd/Plan-256-Robustheit/progress.md` (gitignoriert, wie bei Plan-253). Je Welle:
Preflight der Nähte aus Abschnitt 2, Dispatch mit BASE-SHA und Modell, Bericht, Review, Rulings
mit „cost if wrong", Merge-SHA. Dazu: die Bundle-Baseline (T0), der gemergte #255-SHA und die
#255-T3-Werte (T0), die Baustein-API nach T1 (für den T2-Brief und für #254 T0), die
Codex-Befunde (T9) und der Browser-Handgriff (T10), bevor sie in den PR-Text wandern.

---

## 11. Nachtrag: Codex-Adversarial-Review über die erste Fassung (gpt-6-sol, 2026-09-26)

Drei Befunde über `be68db06`, alle am Code geprüft und bestätigt, alle in dieser Fassung
eingearbeitet. Codex hat den Engine-Pfad aus der Doku gefolgert („inference"); die Prüfung am Code
(`cancelRemainingRows`, `REMOVE_OPERATION`, `addOperation`) hat die Folgerung belegt.

| # | Schwere | Befund | Prüfung am Code | Lösung | Wo |
|---|---|---|---|---|---|
| 1 | high | `engine.cancel()` bei `reset()` während `running` kann eine angewandte Mutation ungemeldet lassen: ohne `transportLossIsUnknown` wird ein Request in der Luft `cancelled` | Bestätigt: `REMOVE_OPERATION` und `addOperation` setzen das Flag nicht; `cancelRemainingRows` (`seven-tv-run-engine.ts:727-760`) macht die Zeile `cancelled`, `finish()` meldet sie nicht; der Delete-Unload-Schutz fiele mit `closed` | Festlegung Nr. 3 neu: die Engine läuft zu Ende, `reset()` löst nur die Anzeige, die Queue wird nach `finish()` geleert; je Dienst ein Spec-Fall „Request in der Luft, nach `reset()` bestätigt ⇒ gemeldet". Der Nutzer-Abbruch bleibt als bekannte Grenze (Abschnitt 8 Nr. 3) | 0.2 Nr. 1, T1, T2, Abschnitt 6 Nr. 3, Abschnitt 8 |
| 2 | high | Eine Meldung, die nach einem Kanalwechsel scheitert, hat weder Anzeige noch Retry — die Retry-Methoden sehen nur den gezeigten Lauf | Bestätigt: `resetIfChannelChanged` (Delete `:263`, Restore `:290`) setzt zurück, sobald `isRunning()` falsch ist, also auch bei `syncReport === 'pending'`; `RunProgressPanel.dismissible` ist im Delete- und Restore-Dock ungegated | Festlegung Nr. 13 (Schließen erst ab `closed`, Kanalwechsel löst nur `closed`, Wiederanzeige eines abgelösten Fehlschlags), Nr. 15 (Zeitrahmen, damit `closed` immer erreicht wird); Spec-Fälle je Dienst; Abschnitt 8 für den Betreiber | 0.2 Nr. 11, 0.5, T1, T2, Abschnitt 6 Nr. 13/15, Abschnitt 8 |
| 3 | medium | T1/T2 machten `run`, `lastRun`, `runState` und die Meldungs-Signale zu `computed`, aber `usage-stats-page.spec.ts` und weitere Specs schreiben sie per `.set`; die gefilterten Suiten sahen es nicht, `tsconfig.app.json` prüft keine Specs | Bestätigt und gezählt: 99 Schreibzeilen in sechs Spec-Dateien (0.2 Nr. 12); `tsconfig.spec.json` schließt die Specs ein, `npx tsc -p tsconfig.spec.json --noEmit` läuft in ~4 s mit Exit 0 | Festlegung Nr. 14 (schreibbare `linkedSignal`-Projektionen, `run` bleibt schreibbar); Fixtures in T1 (Import) und T2 (Delete, Restore) zugeordnet; jeder Task T1–T7 fährt `usage-stats-page.spec.ts` mit, sobald er Datensatz oder Signale ändert, und die Spec-Typprüfung als Gate | 0.2 Nr. 12, 0.4, 0.5, 0.6, T0–T7, T9, Abschnitt 6 Nr. 14 |
