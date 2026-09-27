# Plan #284 + #286 — Import und Undo: Wartezeit vor dem Settle-Read nach Abbruch, Dock während `settling`

Erstellt am 2026-09-27 gegen `fix/284-286-import-undo-settle` = `24926ca0` (= `origin/feat/emote-sets-200`,
#275 gemergt). Quellen: Issues #284 und #286, die Analyse vom 2026-09-27 samt Nutzerentscheidungen
(unten E1–E4, **verbindlich, hier nicht neu verhandelt**), `docs/plans/Plan-275-Abbruch-unklar.md`
(Festlegungen 4, 5, 10), `docs/DECISIONS.md` (2026-09-27 #275, 2026-09-23 #230), `CLAUDE.md`
(Regeln 3, 12, 16, 18, 22). Jede genannte Datei ist gelesen, nicht vermutet.

**Kein Code im Plan.** Namen stehen nur, wo sie ein Vertrag sind. Weicht der Code vom Plan ab, gilt
der Plan; der Fund kommt in den Task-Bericht. **Die Klärtabellen von Import (`settleUnknownRow`) und
Undo (§4.6/E24, `settleUnknownRemove`/`settleUnknownAdd`) bleiben unverändert** — insbesondere gilt
das „nur positiv bestätigen" aus #275 (N1) hier **nicht**.

Nutzerentscheidungen: **E1** #286 Variante A — nur Anzeige gaten, `result` wird nicht zurückgehalten.
**E2** Konstanten (a) — nur die zwei Settle-Timeouts wandern, `RECHECK_READ_TIMEOUT_MS` bleibt.
**E3** Add-only-Import bleibt Ausnahme, eigenes Issue #290. **E4** Wartezeit nur nach dem eigenen
`cancel()`, nur vor dem Settle-Read; der Undo-Recheck bekommt keine.

---

## 1. Ziel und Nicht-Ziele

**Ziel.** (#284) Bricht der Nutzer einen Import- oder Undo-Lauf mit Request in der Luft ab, wartet der
Settle-Read `CANCEL_SETTLE_GRACE_MS` (3 s), bevor er liest — damit „unverändert ⇒ nicht passiert"
nicht mehr eine Mutation überholt, die 7TV gerade noch anwendet. (#286) Solange ein Import- oder
Undo-Lauf `settling` ist, zeigt sein Dock wie Delete/Restore weder Summenblock noch `unknown`-Zeilen,
und der Announcer spricht keinen Hinweis, den das Dock nicht zeigt.

**Nicht-Ziele.**
- **Keine Änderung an Klärtabellen, Reports, Resync, Lifecycle, Arbiter, Engine.**
- **Kein Zurückhalten von `result`** (E1): Import und Undo veröffentlichen ihren Snapshot weiter mit
  `phase: 'settling'`; `items()`, `settleRun`, Protokoll-Gates und `watchRunSettle` bleiben.
- **Add-only-Import bleibt Ausnahme** (E3): `transportLossIsUnknown: deletes`
  (`seven-tv-import.service.ts:620-622`) bleibt; ein Abbruch in der Luft ist dort `cancelled`, ohne
  Settle. Eigenes Issue #290.
- **`usage-stats-page.ts` wird nicht angefasst** (#287 arbeitet dort, Abschnitt 6).

---

## 2. Festlegungen

| # | Festlegung | Grund / Beleg | Task |
|---|---|---|---|
| 1 | **Abbruch-Merker je Dienst**, exakt nach dem Delete-Muster (`seven-tv-delete.service.ts:281-285`, `:383-390`): ein privates Flag, das nur für die synchrone Spanne von `engine.cancel()` in `cancel()` gesetzt ist (try/finally); `onRunComplete` liest es als Erstes. Kein Engine-Feld, kein Zeilenfeld. Trägt, weil jeder Dienst seine eigene Engine hat (Import `:275`, Undo `:307`), `engine.cancel()` nur aus `cancel()` kommt und die Engine `onRunComplete` synchron aus `finish()` ruft (`seven-tv-run-engine.ts:420-430`, `:901-916`); `abortOn` endet über `complete:`, nicht über `cancel()`. | E4; Plan-275 F5 | T1, T2 |
| 2 | **Wartezeit nur vor dem Settle-Read**, nur wenn der Merker gesetzt war und der Lauf `unknown`-Zeilen hat: Import `onRunComplete` `:693-697`, Undo `:713-718` bekommen `timer(CANCEL_SETTLE_GRACE_MS)` davor. Ohne Abbruch (5xx, Status 0) liest der Read sofort wie heute. Der Undo-Recheck vor jedem REMOVE (`:587-600`) bleibt ohne Wartezeit. Die Wartezeit läuft **innerhalb** von `settling` (Phase, Snapshot und Merker-Zustand werden vor dem Timer gesetzt wie heute). | E4 | T1, T2 |
| 3 | **P6 bleibt:** jeder Ast endet in `settleRun` — `timer → Read mit timeout → catchError(null) → settleRun`. `reset()` während der Wartezeit löst nur die Anzeige; Datensatz settelt, meldet, schließt. | Plan-275 F19 | T1, T2 |
| 4 | **Konstanten (E2):** Import `SETTLE_READ_TIMEOUT_MS` (`:88-92`) und Undo `UNDO_SETTLE_READ_TIMEOUT_MS` (`:79-81`) entfallen zugunsten von `SET_ENTRIES_READ_TIMEOUT_MS` aus `seven-tv-run-settlement.ts`. `RECHECK_READ_TIMEOUT_MS` bleibt eigenständig (Gate vor einer Mutation, E19, fail-closed). Der Doc-Kommentar in `seven-tv-run-settlement.ts:3-11` und `:14-23` nennt danach alle vier Läufe und den Recheck als bewusst getrennt. Wert unverändert 20 s. | E2; Plan-275 Offener Punkt 2 | T1 (Modul-Doku, Import), T2 (Undo) |
| 5 | **Dock (E1):** `import-progress-section.ts` und `undo-progress-section.ts` binden `[settling]` an `run.phase === 'settling'` (Muster `restore-progress-section.ts:92`). Damit blendet `RunProgressPanel` den Summenblock samt `run-actions` aus (`run-progress-panel.ts:110`) und `unknown`-Zeilen aus der Alert-Liste (`:213-217`); Balken, Zähler, `failed`-Zeilen, „Wird abgeschlossen…" bleiben. Betrifft u. a. `unknownRows`/`unknownRecordedIn`/`removedCount` (Import `:160-173`), `counters()` (Undo `:115-119`), Resync- und NotActive-Hinweise. | E1; #286 | T3 |
| 6 | **Announcer-Gate:** `notActiveNoticeParams` (`dock-outcome-announcer.ts:104-116`) liefert `null`, solange `run.settlement !== 'settled'` — heute gated es auf `result === null`, was für den Import während `settling` nicht greift. Wirkt auf den gesprochenen (`:286-291`) und den sichtbaren Hinweis (gemeinsame Helfer), die damit nie auseinanderlaufen (§4.5). Andere Announcer-Zeilen sind unberührt (`resyncTrigger` entsteht erst nach dem Settle, `undoSkippedNotice` hängt nicht am Settle). | E1; UI-Designsprache §4.5 | T3 |
| 7 | **Doku-Kommentare mitziehen** (englisch, im selben Commit): `onRunComplete`-/`cancel()`-Doku beider Dienste, `isSettling`/`destructiveOpen` wo sie Dauer nennen, `RunProgressPanel.settling` (`:183-190`, „`false` … for a host without such a phase (import, undo)" entfällt). | Regel 3, Sprache | T1, T2, T3 |
| 9 | **Endgültige `failed`-Gründe schon im Settling-Snapshot** (Codex-Plan-Review, Finding 2): Die Gründe für `failed`-Zeilen (Import `withFailureReason` über `gqlStatusByKey`, Undo `withFailureReason` über `rejectedKeys`) hängen nicht am Read. Beide Dienste veröffentlichen mit `phase: 'settling'` deshalb den Snapshot, auf den diese Normalisierung schon angewendet ist (Import: `settleRunResult` mit `entries = null`, Undo sinngemäß). Eine sichtbare `failed`-Zeile ändert ihren Text damit zwischen `settling` und `closed` nicht mehr, und die Alert-Region sagt sie nur einmal an. `unknown`-Zeilen bleiben unangetastet (die blendet Festlegung 5 aus). Das ist kein Zurückhalten von `result` (E1), und die Klärtabellen ändern sich nicht. | Codex F2; E1 | T1, T2 |
| 8 | **Worst-Case des Guards nach dem letzten Klick** (nur nach Abbruch, +3 s): Import ≈ 116 → **≈ 119 s** (Reports parallel), Undo ≈ 212 → **≈ 215 s** (`sync-deleted`, dann `sync-restored`, `:752-796`). Steht im DECISIONS-Eintrag und in §2.5. | Analyse | T1, T2, T3 |

---

## 3. Grenzfälle

| Fall | Verhalten | Wo geprüft |
|---|---|---|
| Import-Replace: Abbruch mit REMOVE in der Luft, 7TV hat angewandt | `unknown` @0; 3 s; Read: Ziel weg ⇒ `failed` mit Lücken-Grund, REMOVE bestätigt, `sync-deleted` | T1 Unit |
| Import-Replace: Abbruch mit ADD in der Luft, 7TV hat angewandt | `unknown` @1; 3 s; Read: Quelle unter Alias ⇒ `done`, `sync-imported` + `sync-deleted` | T1 Unit, T4 E2E |
| Import/Undo: Abbruch in der Luft, Request kam **nicht** an | 3 s; Read zeigt alten Stand ⇒ Klärtabelle wie heute (`failed`/unknownOutcome) — gewollt, nicht neu verhandelt | T1/T2 Unit |
| Undo: Abbruch mit REMOVE in der Luft | `unknown` @0; 3 s; Read: Quelle weg ⇒ `failed` + Lücke, REMOVE gemeldet | T2 Unit (Bestandsfall angepasst) |
| Undo: Abbruch während Recheck-Read | Zeile `cancelled`, kein Settle, keine Wartezeit — wie heute | Bestands-Spec `:756-766` |
| Abbruch zwischen Zeilen / in Rate-Limit-Pause | keine neue `unknown`; nur wenn ältere Transportverlust-Zeilen existieren, wartet der Read 3 s — harmlos (Plan-275 F5) | T1/T2 Unit (ein Fall) |
| 5xx / Status 0 ohne Abbruch | Read sofort, wie heute | Bestands-Specs bleiben grün |
| Read scheitert / Timeout / `complete: false` nach Wartezeit | `unknown` bleibt, Settle wie heute | T1/T2 Unit |
| `reset()` während der Wartezeit | Anzeige weg; Timer, Read, Settle, Reports laufen | T1/T2 Unit |
| Add-only-Import, Abbruch in der Luft | `cancelled`, kein Settle (E3) | Bestands-Specs Arbiter `:475`, `:654` |
| Dock während `settling`, Snapshot hat `done`-Adds in nicht-aktives Set | kein NotActive-Hinweis sichtbar **und** keiner gesprochen; nach Settle beide | T3 Unit |
| Gemischter Lauf: Replace-ADD `failed` nach bestätigtem REMOVE plus eine `unknown`-Zeile | Die `failed`-Zeile zeigt schon während `settling` den Lücken-Grund, nach `closed` denselben Text; sie wird nur einmal angesagt | T1/T2 Unit (Dienst), T3 Unit (Section) |
| Protokoll-Download / Reports / Seiten-Reload während `settling` | unverändert gegated auf `settlement === 'settled'` | Bestands-Specs |

---

## 4. DECISIONS (Regel 3)

**Neuer Eintrag, englisch, oben** (Commit T1; `Betrifft:` in T2 und T3 ergänzt):

| Titel | Kernaussage |
|---|---|
| *Import and undo wait the cancel grace before their settle read, and their docks hold back summary and unclear rows while settling* | Nach dem eigenen `cancel()` wartet der Settle-Read von Import und Undo `CANCEL_SETTLE_GRACE_MS` wie Delete/Restore (nicht der Undo-Recheck, nicht nach reinem Transportverlust), beide nutzen `SET_ENTRIES_READ_TIMEOUT_MS`, und ihre Docks gaten Summe, `unknown`-Zeilen und den NotActive-Hinweis bis zum Settle — während `result` weiter der veröffentlichte Snapshot bleibt; die Klärtabellen sind unverändert, der Add-only-Import bleibt Ausnahme (#290), Guard-Worst-Case Import ≈ 119 s, Undo ≈ 215 s. |

**Betroffene Bestandseinträge** (nicht umgeschrieben, der neue Eintrag benennt sie): 2026-09-27 #275
(`:13ff`; „Unlike the import and the undo, which publish their snapshot" `:65-66` gilt weiter für die
Daten, nicht mehr für die Anzeige; „until #284" und „keep their own read timeouts" `:96-99` sind
damit erledigt bzw. — Add-only — auf #290 verschoben) · 2026-09-23 #230 (`:2187-2188`, „the dock
already shows the engine's live snapshot") · 2026-09-26 run-bound (`:575ff`) als Kontext.

**Nachziehen:** `docs/UI-Designsprache.md` §2.5 (`:114`): Wartezeit „for delete and restore" → alle
vier Läufe; Guard-Dauern um Import/Undo ergänzen; der Satz „While a delete or restore run is
`settling` …" gilt für alle vier Docks. `RunProgressPanel.settling`-Doc (Festlegung 7).

---

## 5. Tasks

PR gegen `feat/emote-sets-200`. Ein Commit je Task (Conventional Commits, englisch, keine
`#`-Referenzen in Git-Metadaten). Jeder Task fährt seine gefilterten Specs plus
`npm --prefix web run build`, `cd web && npx tsc -p tsconfig.spec.json --noEmit`, Lint, Prettier.
**Alle Tasks `opus`** (Sonnet hängt bis heute Abend am Wochenlimit). Reihenfolge sequenziell
**T1 → T2 → T3 → T4 → T5** — T1–T3 berühren alle `docs/DECISIONS.md`; der Umfang lohnt keine
Worktree-Parallelität.

### T1 — Import-Dienst: Merker, Wartezeit, Konstante, DECISIONS-Eintrag
**Dateien:** `core/seven-tv/seven-tv-import.service.ts` (+ `.spec.ts`), `seven-tv-run-settlement.ts`
(nur Doku), `docs/DECISIONS.md` (neuer Eintrag). **Festlegungen:** 1–4, 7, 8, 9.
**Akzeptanzfälle (Unit, Fake-Timer):** Replace, Abbruch mit ADD in der Luft ⇒ `settling`, kein Read
bei `GRACE − 1`, genau einer bei `GRACE`; Read zeigt Quelle unter Alias ⇒ `done`, `sync-imported`
nennt die Id · Abbruch mit REMOVE in der Luft, Ziel weg ⇒ Lücke, `sync-deleted` · Abbruch, Read
scheitert ⇒ `unknown` bleibt · 5xx ohne Abbruch ⇒ Read ohne Wartezeit · `reset()` während der
Wartezeit ⇒ Settle und Reports laufen · gemischter Lauf (Festlegung 9): die `failed`-Zeile trägt im Settling-Snapshot schon ihren endgültigen Grund · Bestands-Specs byte-gleich grün.
**Abhängigkeiten:** keine.

### T2 — Undo-Dienst: dasselbe, nur für den Settle-Read
**Dateien:** `core/seven-tv/seven-tv-undo.service.ts` (+ `.spec.ts`), `docs/DECISIONS.md`
(`Betrifft:`). **Festlegungen:** 1–4, 7, 8, 9.
**Akzeptanzfälle:** Bestandsfall `:768-783` wartet jetzt `GRACE` vor dem Read · kein Read bei
`GRACE − 1` · Abbruch mit ADD in der Luft (nach bestätigtem REMOVE) ⇒ Wartezeit, dann Klärung nach
E24 · Recheck-Read und `RECHECK_READ_TIMEOUT_MS`-Fall (`:706-712`) unverändert · Spec-Importe
`:23, :26, :991` auf die gemeinsame Konstante · 5xx ohne Abbruch ⇒ sofort · gemischter Lauf (Festlegung 9): `full`-Zeile `failed` nach REMOVE trägt im Settling-Snapshot schon `removedButNotRestored`/`cancelledMidRow`.
**Abhängigkeiten:** T1.

### T3 — Dock und Announcer
**Dateien:** `shared/seven-tv/import-progress-section.ts` (+ spec), `undo-progress-section.ts`
(+ spec), `dock-outcome-announcer.ts` (+ spec),
`run-progress-panel.ts` (nur Doku), `docs/UI-Designsprache.md` §2.5, `docs/DECISIONS.md` (`Betrifft:`).
**Festlegungen:** 5, 6, 7, 8.
**Akzeptanzfälle (Regel 12, Verhalten statt Vorlage):** je Section nach `restore-progress-section.spec.ts:512-545`:
während `settling` Fortschritt und `failed`-Zeile da, Summe und `unknown`-Zeile nicht; nach `closed`
beide da; der Text der `failed`-Zeile ist in beiden Phasen derselbe (Festlegung 9) · Import-Sektionsspec: Fixture `runInfo()` (`:101-130`) liefert ohne Override `settling` —
die Fälle `:351`, `:378`, `:521` (4×), `:553` erwarten aber einen abgeschlossenen Lauf; Fixture oder
Fälle so korrigieren, dass sie sagen, was sie meinen · Announcer: während `settling` mit `done`-Add
ins nicht-aktive Set kein `copiedNotActive`, nach `settled` ja.
**Abhängigkeiten:** T2.

### T4 — E2E
**Dateien:** `web/e2e/emote-import.e2e.spec.ts`.
**Akzeptanzfälle:** (1) `running import: the settling window` (`:2436ff`): während der gehaltene
Re-Read aussteht, keine Summenzeile und keine „unklar"-Zeile im Import-Dock. (2) Neuer Fall nach dem
Muster `delete/restore: a cancelled request is settled (#275)` (`:2730ff`): Import-Replace, ADD
„angekommen" (`mockSevenTvSetState`), Abbruch, „Wird abgeschlossen…" ohne Summe,
`page.clock.runFor(3_000)`, Settle-Read gehalten, dann freigegeben ⇒ grün, `sync-imported` nennt die
Id. `page.clock.install()` vor `goto`. Undo-E2E nicht nötig (Unit deckt ab).
**Abhängigkeiten:** T3.

### T5 — Abnahme
Gates grün: `dotnet test EmotePurge.slnx` · `npm --prefix web test -- --watch=false` · Lint/Format ·
`npm --prefix web run e2e` (nur ohne Api auf `:5151`) · `node scripts/coverage-local.mjs
--frontend-only`. **Live-Test** (Regel 16, Set vorher sichern, danach exakt wiederherstellen): ein
Import-Replace, ADD-Antwort per `route.fetch()` durchreichen und zurückhalten, „Abbrechen" ⇒ Read
≥ 3 s nach dem Klick, Zeile `done`, beide Reports. **Zweitmeinung:** `/codex:review --model
gpt-6-sol --scope branch --base origin/feat/emote-sets-200`; Findings vorlegen, nicht umsetzen.
**Abhängigkeiten:** T4.

---

## 6. Überschneidung mit #287

#287 (Worktree `EmotePurge-287`) ändert `usage-stats-page.ts` und `.spec.ts`
(`mayHaveChangedTheSet`, `watchRunSettle` `:2895-2912`). Dieser Plan fasst beide **nicht** an.
Gemeinsam ist nur `docs/DECISIONS.md` (beide Einträge oben) — trivialer Merge, wer zuletzt merged,
rebased.

---

## 7. Offene Punkte

1. ~~Issue-Nummer für den Add-only-Import~~ — #290 (E3), eingesetzt.
2. **Undo-E2E** bewusst weggelassen; wer es will, ergänzt T4 um den Undo-Abbruch nach demselben Muster.
3. **§2.5 nennt „all three docks"**, obwohl das Undo-Dock der vierte ist — T3 korrigiert das beim
   Nachziehen mit, ohne weitere Umformulierung.
