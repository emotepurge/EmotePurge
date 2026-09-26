# Ersetzung rückgängig: ein geglücktes „Ziel ersetzen" aus seiner Übertragungsdatei vollständig zurücknehmen — Spec

**Datum:** 2026-09-25 · **Status:** **Freigegeben vom Betreiber am 2026-09-25** (dritte Fassung) — die sechs offenen Punkte der ersten Fassung sind vom Betreiber entschieden, jeweils wie empfohlen (Abschnitt 13, 2026-09-25); die parallel getroffene #255-Entscheidung zum Restore-Resync und der Routen-Umbau aus #264 sind eingearbeitet (11.4, F9); die sieben Befunde der adversarialen Zweitmeinung (Codex Sol, gpt-6-sol, 2026-09-25: fünf high, zwei medium) sind eingearbeitet (Abschnitt 16, E19–E24, F13–F17), ebenso die fünf Befunde der **zweiten und letzten** Runde (Abschnitt 16, Runde 2: kein Wiederverwenden eines Reads vor einem REMOVE, Normalisierung der `null`-Einträge vor jeder Zielprüfung, `addOnly` als Reparaturweg neben fremden Ziel-Einträgen, `partial` orthogonal zu `failed`/`unknown`, lauf-gebundener Abschluss im Vertrag an #256 — F18–F20, AK 35–40, keine neue Betreiberfrage); die zwei Betreiberfragen aus Runde 1 sind am 2026-09-25 entschieden (Abschnitt 15: Herkunftsnachweis per Kennzeichnung und Bestätigung; **#256 Punkt 1 ist Vorbedingung** — die Settling-Sperre liegt im Arbiter, nicht im Undo, Vertrag in 11.1); gegen den Code auf `feat/254-replace-undo` (= `origin/feat/emote-sets-200` @ `992eef14`, Merge von #270) belegt, Stichprobe der Belege 27/28 korrekt, der eine Pfad korrigiert · **Issue:** #254 · **Epic:** #200 · **Vorgänger:** #230 (PR #251, [Plan-230-Namenskonflikte.md](../../plans/Plan-230-Namenskonflikte.md) §2, §6 Frage 4, §7 Zeile „Untracked-Restore, Replace-Undo", T7, T7b), #253 (PR #270, [2026-09-24-restore-pro-set-253-design.md](2026-09-24-restore-pro-set-253-design.md)) · **Formatvorlage:** die #253-Spec · **Parallel in Arbeit:** #255 (Wortlaute/Zählungen), #256 (Robustheit) — Berührungspunkte in Abschnitt 11 · **Nicht Teil:** Abschnitt 10

Diese Spec ist ein Denkwerkzeug des Betreibers und deshalb deutsch; Bezeichner, Routen und
Wire-Felder bleiben englisch. Sie enthält keinen fertigen Code — Verträge, Verhalten, Grenzfälle,
Fallen und prüfbare Akzeptanzkriterien. Zeilenangaben sind am 2026-09-25 gegen den Branch
nachgeprüft (Pfade ohne Präfix liegen unter `web/src/app/`). Kleine, eindeutige Präzisierungen
sind als **Festlegung** markiert und einzeln kippbar; die Betreiberentscheidungen stehen in
Abschnitt 13 und 15 mit den verworfenen Alternativen. **Vorbedingung:** #256 Punkt 1 (Arbiter
kennt „läuft oder settelt noch") ist vor der Umsetzung gemergt; sein Vertrag steht in 11.1.

---

## 0. Auftrag in einem Satz

Ein Nutzer kann eine Übertragungsdatei — Rückweg-Datei (`planned`) oder Ergebnisprotokoll
(`finished`) — über dieselbe Datei-Tür wie den Restore einlesen und statt „Lücken schließen" die
zweite Richtung wählen: **je bestätigter Replace-Zeile das Quell-Emote, das jetzt den Namen hält,
aus dem Ziel-Set entfernen und die entfernten Ziel-Einträge unter ihren alten Aliassen zurückholen**
— als eigene, destruktive Aktion mit denselben Sicherungen wie das „Ziel ersetzen" aus #230:
Live-Check des Sets, Pflicht-Rückweg-Datei vor dem ersten REMOVE, Ausgang `unknown` mit
Nachlesen, zwei set-zentrische Meldungen, Unload-Schutz, eigenes Protokoll — und ohne eine
einzige Backend-Änderung, weil #253 die beiden Meldewege bereits gebaut hat.

**Die zwei offenen Fragen des Issues, beantwortet:**

1. **Modus von „Restore" oder eigener Einstieg?** — Weder reiner Modus noch neue Tür: **dieselbe
   Datei-Tür, eine Weiche im Datei-Schritt, dahinter eine eigene Aktion** (eigener Flow, Dienst,
   Bestätigungsdialog, Dock-Abschnitt, Protokoll, Arbiter-Zustand). Begründung E1/E2: der
   Restore ist ein reiner ADD-Lauf ohne `unknown`, ohne `abortOn`, ohne Unload-Schutz und ohne
   Pflicht-Datei (`core/seven-tv/seven-tv-restore.service.ts:46-58`); jede dieser Sicherungen in
   ihn hineinzubauen hieße, den nicht-destruktiven Weg mit destruktiven Zweigen zu durchsetzen —
   genau das, was #230 mit der Trennung von Import und Restore vermieden hat. Und der Restore
   erreicht den Undo-Fall gar nicht: nach einem vollständig geglückten Replace-Lauf sortiert Regel 4
   („Name belegt") **jede** Zeile vor dem Dialog aus, es gibt keine Bestätigung, in der eine zweite
   Option stehen könnte (`shared/seven-tv/restore-flow.ts:145-163`).
2. **Teilerfolg einer Zeile — Lücke oder Fehler?** — **Beides, wie in #230:** die Zeile endet
   `failed` mit `failedStep`, und was sie hinterlässt, ist eine benannte Lücke. Kein Auto-Rollback
   (Betreiber-Entscheidung (4) aus Plan-230 §0.1, hier unverändert übernommen, E9). Im Protokoll
   steht `status: 'failed'`, `failedStep`, `completedSteps`, `removedSource.confirmed` und je
   Ziel-Eintrag `added`; das Dock nennt die Zahl der Lücken-Zeilen und den Weg, der sie schließt:
   **derselbe Undo aus derselben Datei noch einmal** — die Zeile ist dann `addOnly` und holt genau
   die fehlenden Ziel-Einträge (E9, E18; der Restore aus der Übertragungsdatei täte es nur, solange
   das Ziel keinen fremden Eintrag trägt, F14). Ein REMOVE, dessen Antwort verloren ging, ist weder
   Lücke noch Fehler, sondern `unknown` mit Nachlesen (4.6).

---

## 1. Ausgangslage — was am Code steht

Alles Folgende ist am Branch nachgeprüft; es ist die Begründung für jede Entscheidung in
Abschnitt 2.

**Was ein geglückter Replace hinterlässt.** Eine Replace-Zeile läuft in zwei Schritten: Schritt 0
`REMOVE { setId, emoteId: target.id }`, Schritt 1 `ADD { setId, emoteId: source.id, alias }`
(`core/seven-tv/seven-tv-import.service.ts:865-897`, `stepCount` 2 `:542`). Ein REMOVE nimmt
**alle** Einträge der Ziel-ID (`seven-tv-delete.service.ts:44-58`, Sonde 5 der Spec-200). Der
Alias des ADD ist genau der Name, den das Ziel belegte (`core/seven-tv/transfer-plan.ts:26-59`,
`shared/seven-tv/conflict-resolution.ts:294-332`). Zum Laufzeitpunkt war die Quell-ID **nicht** im
Set — `recheckTransferPlan` verwirft Replace-Zeilen mit bereits vorhandener Quelle
(`shared/seven-tv/import-flow.ts:194-234`), und der Duplikatfilter des Imports überspringt jede
vorhandene ID unabhängig vom Alias (`shared/seven-tv/already-present-filter.ts:78-90`). **Der
Zustand nach einem geglückten Replace ist deshalb exakt:** die Quell-ID hat im Set genau einen
Eintrag, und der trägt den Alias der Zeile; die Ziel-ID hat keinen.

**Was die Übertragungsdatei weiß.** `TransferRunRow` trägt je Zeile `action`, `sourceName`, `alias`,
`sevenTvEmoteId` (= **Quell**-ID), `status`, `failedStep`, `errorMessage`, `removedTarget`
(`shared/export/transfer-run-export.ts:56-72`); `removedTarget` trägt die **Ziel**-ID, `entries:
{ alias: string | null }[]` (ein Eintrag je benanntem Alias plus höchstens einer mit `null`),
`aliases`, `defaultName`, `confirmed` (`:40-54`). `confirmed` heißt: 7TV hat das REMOVE bestätigt,
direkt oder über das Nachlesen, **unabhängig vom Endstatus der Zeile** (`:50-53`, `= completedSteps
>= 1`, `:194`); in `planned` ist es immer `false` (`liveRowTarget`, `:126-137`). Die Quellseite
einer Replace-Zeile ist damit vollständig in der Datei: Quell-ID, Alias, Status. „Die Quelle hält den
Namen" entspricht in einem Ergebnisprotokoll `action === 'replace' && status === 'done'`; bei
`failed@1` hält sie ihn nicht; bei `unknown` ist es offen; die `planned`-Datei sagt darüber nichts
(alle Zeilen `pending`, und sie enthält auch Zeilen, die `recheckTransferPlan` später verworfen
hat). **Die Datei ist nicht vertrauenswürdiges JSON** — was sie über den Set-Zustand behauptet,
wird live geprüft, nie geglaubt (F1).

**Was der Restore heute daraus macht.** `parseTransferRunForRestore` liest nur
`action === 'replace'` mit `removedTarget`; `finished` ⇒ nur `confirmed === true`, `planned` ⇒
alle; die Quellzeilen liest er nicht (`transfer-run-export.ts:366-401`, `:406-442`).
`filterAlreadyPresentForRestore` streicht einen fehlenden benannten Alias, den eine **andere** ID
hält (Regel 4, `already-present-filter.ts:165-177`, Zähler `skippedNameTaken`) — und die Doku
nennt den #254-Fall ausdrücklich als Grund (`:131-133`). Nach einem vollständig geglückten
Replace-Lauf fällt jede Zeile heraus; `startRestoreFlow` zeigt eine transiente Notiz und öffnet
keinen Dialog (`restore-flow.ts:145-163`). Der Restore-Lauf ist ein reiner ADD-Lauf: die Operation
setzt weder `transportLossIsUnknown` noch `abortOn` (`seven-tv-restore.service.ts:46-58`), ein
Verbindungsverlust ergibt `failed`, `LACKING_PRIVILEGES` bricht den Lauf nicht ab; weder
`beforeunload` noch `canDeactivate` decken ihn (`features/usage-stats/usage-stats-leave.guard.ts:30-55`,
nur `importService.isRunning()`).

**Was die Engine kann.** Streng sequentiell (`seven-tv-run-engine.ts:331-342`), 275 ms zwischen
Zeilen **und** zwischen den Schritten einer Zeile (`:29`, `:404`), je Zeile `stepCount` Schritte,
ein `failed`- oder `unknown`-Schritt beendet die Zeile (`:387-425`); `completedSteps` und
`failedStep` am `RunQueueItem` (`:75-87`). `unknown` entsteht nur, wenn die Operation
`transportLossIsUnknown` setzt: dann ergeben HTTP 0, jede 5xx und ein Abbruch während eines
laufenden Requests `unknown` (`:69-73`, `:122-134`, `:535-540`, `:738-760`); jede GraphQL-Antwort
ist `done` oder `failed`, jede 4xx `failed`. 401/403 löschen das Token (`:679-685`). Rate-Limit:
Bucket `emote_set_change`, ein Ticket je Mutation, Pause aus `reset`, höchstens 5 Retries
(`:36-46`, `:430-462`, `:554-581`).

**Was der Import-Dienst dem Undo vorgemacht hat.** `ImportSettlement 'pending' | 'settled'`
(`seven-tv-import.service.ts:145`); nach dem Engine-Ende ohne `unknown` sofort `settled`, sonst ein
Live-Re-Read mit 20 s Timeout (`:88`, `onRunComplete` `:583-616`); `settleUnknownRow` klärt je
Aktion (`:996-1043`); Meldungen erst nach `settled` (`sendFollowUp` `:646-688`): ADDs an
`sync-imported` (`:703-737`), REMOVEs — jede Replace-Zeile mit `completedSteps >= 1` — an
`POST /api/seventv/emote-sets/{id}/sync-deleted` (`:745-779`, `:916-922`). `beforeunload` hängt an
`destructiveRunActive` = Plan enthält Replace **und** (Engine läuft **oder** `settlement ===
'pending'`) (`:273-277`, `:356-363`, `:826-829`). `abortOn` bei 401/403/`LACKING_PRIVILEGES`
(`:107-117`, `:544-551`). Der Bestätigungsdialog hält die Zustandsmaschine `idle → verifying →
saved` (`shared/seven-tv/import-confirm-dialog.ts:139-142`), liest das Ziel live (20 s Timeout,
nur der neueste Read zählt, `:1204-1279`), vergleicht auf Eintragsebene (`verifyReplaceTargets`,
`already-present-filter.ts:268-296`, Drift-Gründe `targetGone | nameHeldElsewhere | aliasesChanged
| aliaslessEntryChanged`), lädt die Rückweg-Datei herunter und gibt erst dann „Starten" frei
(`:1123-1142`); ohne Download kein Start.

**Was #253 gebaut hat und das Undo unverändert nutzt.** Die Datei bestimmt das Ziel-Set; der
Datei-Schritt prüft es über `resolveEditableSet` (`shared/seven-tv/file-import-step.ts:228-256`,
`core/seven-tv/seven-tv-emote-set.service.ts:221-225`, 60-s-Client-Kopie der Zielliste, vier
Ausgänge `editable | notSelectable | notEditable | unavailable`), und gibt ein
`ResolvedRestoreTarget` weiter. Die beiden set-zentrischen Meldewege `POST
/api/seventv/emote-sets/{emoteSetId}/sync-deleted` und `…/sync-restored` mit Body
`{ sevenTvEmoteIds, expectedChannelName }`, Antwort `{ reportedCount, channels[], unresolvedChannel,
resyncTriggered }` (`core/seven-tv/seven-tv-emote-set.model.ts:123-162`; #253-Spec 5.1–5.5), die
Dreiwertigkeit mit Grund (`core/seven-tv/sync-report-outcome.ts:18-30`, `:75-108`), die
Resync-Regel mit `resyncTriggered`-Sperre und dem N1-Fallback — wobei der Client-Resync für ein
nicht-aktives Ziel (E12 dort) durch die #255-Entscheidung vom 2026-09-25 entfällt (11.4) —, die Retry-Regel N4 (kein
„Erneut melden" bei `channelMismatch`), der Papier-Eintrag mit Besitzerkanal (N3), die
Mitgliederlisten-Vormerkung (N2). **Die Replace-Sperre für ungetrackte Ziele ist vollständig
entfernt:** `replaceNeedsTrackedTarget`, `ResolutionContext`, der Wächter in `startImport` und
beide Locale-Schlüssel existieren nicht mehr (`conflict-resolution.ts:44-59`, Commit `ae95d89a`,
DECISIONS 2026-09-25 „The replace lock for an untracked target falls"); an ihre Stelle trat die
Vorprüfung vor jeder ersten Mutation. Die Pflicht-Rückweg-Datei vor dem ersten REMOVE gilt
**unverändert auch für ungetrackte Ziele** (DECISIONS 2026-09-23 „The safeguard is a file, not a
typed confirmation"; #253-Spec 4.5 Nr. 18).

**Der Arbiter.** `SevenTvRunArbiter.activeRun: 'delete' | 'restore' | 'import' | null`, abgeleitet aus
den `isRunning`-Signalen der drei Dienste; kein Lock; jeder Startpunkt prüft zweimal
(`core/seven-tv/seven-tv-run-arbiter.ts:8-57`, `import-flow.ts:305, :350`, `restore-flow.ts:122,
:152`). Das Settling-Fenster ist nicht abgedeckt — das ist #256 Punkt 1, und seit der
Betreiberentscheidung vom 2026-09-25 (Abschnitt 15 B) **Vorbedingung** dieser Spec: der Undo
baut auf dem Arbiter auf, den #256 liefert (Vertrag in 11.1), nicht auf dem heutigen.

**7TV: kein neuer aliasloser Eintrag.** Ein `addEmote` mit `alias: null` landet unter dem
Standardnamen; ein Re-Read liefert `alias: "<defaultName>"`, nicht `null` (live gemessen am
2026-09-23, Epic-Notiz „Found live: 7TV no longer creates aliasless entries"). Ein Eintrag, den die
Datei als `{ alias: null }` führt, kommt also als **benannter** Eintrag zurück (F5).

---

## 2. Entscheidungen dieser Spec

E1–E4 beantworten die Issue-Fragen und stellen die #230-Entscheidungen neu; E5–E18 sind die
Festlegungen dieser Spec. Wo eine Entscheidung dem Betreiber vorgelegt wurde, steht „(Abschnitt 13,
Nr. …)" — dort steht seit dem 2026-09-25 die getroffene Entscheidung samt verworfener Alternativen.

| # | Frage | Entscheidung | Begründung / Beleg |
|---|---|---|---|
| E1 | Modus von „Restore" oder eigener Einstieg? | **Dieselbe Tür, eine Weiche, eine eigene Aktion.** Der Datei-Schritt erkennt eine `transfer-run`-Datei (beide Stufen) und endet — statt mit `picked` — mit einer Wahl: „Lücken schließen" (Restore, wie heute) oder „Ersetzungen rückgängig machen" (neu). Dahinter ein eigener Flow (`undo-flow.ts`), Dienst (`SevenTvUndoService`), Bestätigungsdialog, Dock-Abschnitt, Protokoll (`transfer-undo`) und Arbiter-Zustand `'undo'` | Der Restore erreicht den Fall nicht (Regel 4 sortiert alles aus, kein Dialog), und seine Operation trägt keine der Sicherungen, die eine Löschung braucht (Abschnitt 1). Kein neuer Dauer-Control auf der Seite: die Wahl lebt im Dialog und erscheint nur für Übertragungsdateien; Purge-Protokolle laufen wie heute ohne Weiche (Abschnitt 13, Nr. 3) |
| E2 | Was ist eine „Zeile" des Undo? | Je Replace-Zeile der Datei ein **Undo-Paar** `(Quell-ID, Alias) ↔ (Ziel-ID, entries)`. Kandidaten: `planned` ⇒ **jede** Replace-Zeile, aber als **unbelegt** markiert (`provenance: 'unproven'`, F17, Abschnitt 15 A — entschieden: Kennzeichnung und Datei-Bestätigung); `finished` ⇒ nur `removedTarget.confirmed === true` (`provenance: 'confirmed'`) — dieselbe Auswahl wie der Restore-Parser | Eine `finished`-Zeile ohne bestätigten REMOVE hat kein ADD gesehen (Schritt 1 läuft nur nach Schritt 0), es gibt dort nichts zurückzunehmen; für `planned` entscheidet der Live-Check (E5), was übrig ist — aber er beweist nur den **Zustand**, nicht die **Herkunft**: eine `planned`-Datei eines nie gestarteten Laufs kann später zufällig die `full`-Form treffen (Codex-Befund 2, F17). Rows mit `status: 'done'` sind der Normalfall, `failed@1` und `unknown` bleiben Kandidaten |
| E3 | **#230-Entscheidung 4 (Pflicht-Rückweg-Datei vor dem ersten REMOVE) — neu bewertet** | **Übernommen, gespiegelt:** der Undo lädt vor seinem ersten REMOVE eine eigene Rückweg-Datei (`transfer-undo`, Stufe `planned`) aus dem Live-Read herunter; ohne Download kein Start. Sie ist über den Restore-Weg einlesbar und holt die **entfernten Quell-Emotes** zurück (E12). Das Ergebnisprotokoll (`finished`) folgt nach dem Lauf, zweiten Rangs (Abschnitt 13, Nr. 1 und 2) | Nicht still übernommen, sondern geprüft: Die Übertragungsdatei liegt beim Undo bereits auf der Platte und nennt jede Quelle mit Alias — als **Papier** reicht sie. Als **Rückweg** reicht sie nicht: der Import weist `transfer-run` namentlich ab (Plan-230 T7 „Import-Verbot, Restore erlaubt"), der Restore liest nur `removedTarget`; ein entferntes Quell-Emote käme nur „im 7TV-Web von Hand" zurück — genau der Handgriff, den der Betreiber am 2026-09-23 als Regelweg abgelehnt hat. Der Grundsatz „the safeguard is a file" verlangt eine Datei, die **das** wiederherstellt, was **dieser** Lauf entfernt; das ist beim Undo die Quelle, nicht das Ziel. Kosten: ein Klick mehr, eine neue Einlesesorte |
| E4 | **#230-Entscheidung 6 (Replace-Sperre für ungetrackte Ziele) — neu bewertet** | **Kommt nicht zurück.** Der Undo ist für ein ungetracktes Ziel-Set genauso erlaubt wie für ein getracktes; die Vorprüfung `resolveEditableSet` läuft im Datei-Schritt wie beim Restore, die Meldungen sind set-zentrisch, ohne Kanal nur Papier | Die Sperre hatte einen einzigen Grund: „keine Löschung ohne Restore-Weg", und der Restore war kanalgebunden (Plan-230 §9 Fassung 5). Seit #253 hat jedes Set einen Restore und eine Papierspur — am Code verifiziert (Abschnitt 1). Der Undo fügt eine zweite Löschung hinzu, aber mit demselben Rückweg (E3, E12): die Undo-Rückweg-Datei ist per Restore einlesbar, set-zentrisch, ohne Kanalseite. Die Bedingung, die die Sperre trug, ist für den Undo ebenso erfüllt wie für das Replace; eine Sperre ohne ihren Grund wäre eine zweite Wahrheit |
| E5 | Was heißt „hält noch exakt den Alias"? | **Gleiche 7TV-Emote-ID UND die Eintragsmenge dieser ID im Live-Set ist genau `{ alias }`:** ein benannter Eintrag mit ordinal gleichem Alias (`===`, Groß-/Kleinschreibung zählt), kein zweiter Alias, kein aliasloser Eintrag. Jede Abweichung ist Drift mit Grund (4.3) | Ein REMOVE nimmt **alle** Einträge der ID (`seven-tv-delete.service.ts:44-47`): hält die Quelle inzwischen einen zweiten Alias, den jemand nach dem Lauf vergeben hat, nähme der Undo ihn mit — Kollateralschaden, den keine Datei kennt. Der Zustand nach einem geglückten Replace ist exakt ein Eintrag (Abschnitt 1); alles andere hat ein Mensch seither verändert, und das entscheidet ein Mensch. `heldNames`/`aliasesById` vergleichen heute ordinal (`already-present-filter.ts:165`), das bleibt |
| E6 | Reihenfolge der Mutationen je Zeile | **REMOVE der Quelle zuerst, dann die ADDs der Ziel-Einträge**, ein ADD je fehlendem Eintrag, sequentiell im Engine-Takt; `stepCount = 1 + Anzahl fehlender Einträge`. Für eine `addOnly`-Zeile (E7) entfällt Schritt 0 | Der kollidierende Alias gehört bis zum REMOVE der Quelle; ein ADD davor bekäme einen sicheren 409 (`NAME_TAKEN_GQL_STATUS`, `seven-tv-import.service.ts:92`). Spiegelbild des Replace (Plan-230 §0.1 Entscheidung (5)). Ein `{ alias: null }`-Eintrag wird als ADD **mit** dem `defaultName` aus der Datei gesendet (E21) — der kollidierende Name kann genau dieser sein (F5), auch dann gibt das REMOVE ihn vorher frei |
| E7 | Schließt der Undo auch Lücken, die der Replace-Lauf hinterlassen hat? | **Ja, für seine eigenen Zeilen:** ist die Quelle im Live-Set gar nicht vorhanden (REMOVE-Teil gegenstandslos), aber Ziel-Einträge fehlen, läuft die Zeile als `addOnly` (nur die ADDs). Fehlen keine Einträge, ist die Zeile `nothingToDo` und wird übersprungen (Abschnitt 13, Nr. 4) | Ziel des Undo ist der Zustand **vor** der Übertragung für die Replace-Zeilen. Eine Zeile, deren Replace bei Schritt 1 scheiterte (`failed@1`: Ziel weg, Quelle nie da), ist genau so eine Lücke; sie den Nutzer in einem zweiten Lauf über den Restore schließen zu lassen, wäre ein Umweg ohne Sicherheitsgewinn — ein ADD in ein leeres Namensfeld ist die nicht-destruktive Hälfte. Der Undo wird damit für Übertragungsdateien eine Obermenge des Restore, ohne dessen Flow zu berühren |
| E8 | Ziel-Einträge, deren Name inzwischen ein Dritter hält | Ein fehlender Ziel-Eintrag, dessen Name (benannt oder `defaultName` bei `null`) im Live-Set eine **dritte** ID hält — weder die Quelle noch das Ziel —, macht eine **`full`-Zeile ganz zur übersprungenen Zeile** (`targetNameTaken`, Zähler je Eintrag daneben): der Undo entfernt keine Quelle, wenn er das Ziel nicht vollständig zurückbringen kann (Codex-Befund 6, fail-closed). Eine **`addOnly`**-Zeile läuft ohne den belegten Eintrag weiter, und der ausgelassene Eintrag steht mit Grund in `omittedEntries` der Protokollzeile und im Dock (`omittedEntryCount`) — die Zeile ist nicht „fertig", sondern `partial` (E23). Hält die **Quelle** den Namen, ist das der kollidierende Alias, den das REMOVE freigibt — kein Hindernis | Regel 4 des Restore-Filters (`already-present-filter.ts:165-177`), um die Quelle als erlaubten Inhaber erweitert und für die destruktive Zeile zur Alles-oder-nichts-Regel verschärft: ein `done` mit fehlendem Eintrag hätte weder Dock noch Datei als Lücke gezeigt. Für die nicht-destruktive Zeile ist Fortschritt ohne Schaden erlaubt, aber nur mit dauerhaftem Vermerk. **Festlegung:** anders als Regel 4 wird auch der `defaultName` eines `null`-Eintrags verglichen (F5, E21) |
| E9 | Teilerfolg einer Zeile | `failed` mit `failedStep`, `completedSteps`; kein Auto-Rollback; die Lücke ist benannt (Dock, Protokoll) und wird geschlossen, indem **derselbe Undo aus derselben Datei erneut läuft**: die Quelle ist dann weg, die Zeile wird `addOnly` und holt genau die fehlenden Ziel-Einträge (E7, E18). Das Wiederherstellen aus der Übertragungsdatei schließt dieselbe Lücke nur, solange das Ziel keinen fremden Eintrag trägt (Regel 2 des Restore-Filters wirft sonst die ganze Zeile, F14) — der Dock-Hinweis nennt deshalb den Undo-Wiederholungslauf als Weg | Betreiber-Entscheidung (4) aus Plan-230 §0.1 und Frage 3 (`cancel()` zwischen den Schritten ⇒ `failed`), hier ohne neuen Grund übernommen. Ein Auto-Rollback (Quelle nach gescheitertem ADD wieder hinzufügen) wäre eine dritte Mutation auf unsicherem Stand — dieselbe Klasse Risiko, die #230 abgelehnt hat. Codex-Befund 3 hat gezeigt, dass die erste Fassung mit „Restore aus der Übertragungsdatei" einen Weg versprach, der bei einem fremden Ziel-Eintrag nicht existiert; der Undo selbst ist der Weg, der immer existiert |
| E10 | `unknown` | Alle Undo-Zeilen tragen `transportLossIsUnknown`; nach dem Lauf ein Nachlesen wie beim Import (20 s), das je Schritt klärt (4.6); unklärbar ⇒ `unknown` im Protokoll, in **keiner** Meldung, Hinweis im Dock — außer der bestätigte Teil derselben Zeile, der gemeldet wird | Wörtlich die #230-Regel (Plan-230 §7 „Ausgang `unknown`", §9 Runde 2 Finding 1): eine verlorene Antwort ist kein Beweis, dass nichts passiert ist; die Bestätigung eines Schritts ist eine Tatsache aus dem Lauf |
| E11 | Meldungen und Audit | **Zwei** set-zentrische Meldungen je Lauf, ohne Backend-Änderung: `sync-deleted` mit den Quell-IDs aller Zeilen mit bestätigtem REMOVE (`mode === 'full' && completedSteps >= 1`), `sync-restored` mit den Ziel-IDs aller Zeilen mit mindestens einem bestätigten ADD; beide mit `expectedChannelName` nach #253; Audit = die bestehenden `emotes.syncDeleted`/`syncRestored`-Einträge (Kanal- oder Papier-Eintrag nach N3) | In einem getrackten Kanal, dessen aktives Set das Ziel ist, hat der Resync nach der Übertragung die Quell-Zeilen angelegt (`sync-imported` ist reines Audit und rührt keine Zeile an, `SevenTvEndpoints.cs:227`, `EmoteEndpoints.cs:198`) — sie werden archiviert; die Ziel-Zeilen sind seit dem `sync-deleted` der Übertragung archiviert und werden reaktiviert — genau die Semantik von `MarkInSetAsync` (`EmoteService.cs:208-333`, #253-Spec 5.2). Fehlt eine Zeile (Resync noch nicht gelaufen, ungetrackt), ist sie `notFoundIds` bzw. Papier — F12. Ein eigener Audit-Vertrag „Undo" wäre eine Backend-Änderung für eine Unterscheidung, die das Protokoll trägt (Abschnitt 13, Nr. 5) |
| E12 | Was ein Restore aus einer `transfer-undo`-Datei wiederherstellt | Die **entfernten Quell-Emotes** unter ihrem Alias: `planned` ⇒ jede `full`-Zeile, `finished` ⇒ nur `removedSource.confirmed === true`. Der Restore-Filter (Regeln 1–4) entscheidet, was davon fehlt; nach einem geglückten Undo hält das Ziel den Namen ⇒ „Name belegt", nichts passiert (Abschnitt 13, Nr. 2) | Ein Grundsatz für alle Rückweg-Dateien: **jede Datei stellt wieder her, was ihr Lauf entfernt hat** — Purge ⇒ gelöschte Emotes, Übertragung ⇒ entfernte Ziele, Undo ⇒ entfernte Quellen. Die Ziel-Lücke eines gescheiterten Undo schließt die Übertragungsdatei (E9); die Quell-Lücke schließt die Undo-Datei. Zwei Dateien, zwei Richtungen, keine Datei mit zwei Bedeutungen |
| E13 | Vorprüfung, Token, Rechte | `resolveEditableSet(meta.targetEmoteSetId)` im Datei-Schritt **vor** der Weiche (wie heute, `file-import-step.ts:228-256`); Token-Prompt **vor** dem Bestätigungsdialog (Restore-Muster, `restore-flow.ts:167-175`); `abortOn` bei 401/403/`LACKING_PRIVILEGES` mit Token-Löschung (Import-Muster) | Die Vorprüfung ist seit #253 die eine Entscheidung für Liste und Meldung (E19 dort). Token vor dem Dialog, damit zwischen Download der Rückweg-Datei und „Starten" kein Prompt liegt — das Drift-Fenster wird kleiner; der Frischcheck vor dem Lauf bleibt trotzdem (E14). `abortOn` wie beim Import, weil ein Lauf ohne Recht nach der ersten Ablehnung nichts mehr Sinnvolles tut |
| E14 | Drift zwischen Vorschau und Lauf | **Drei** Prüfstellen: ein Read beim Öffnen des Dialogs (Klassifikation, Anzeige, Rückweg-Datei), ein Read **unmittelbar vor dem Start** (Frischcheck, alle Zeilen), und **vor jedem einzelnen REMOVE** eine erneute Prüfung genau dieser Zeile gegen einen frischen Read (E19). Eine Zeile, deren Klassifikation sich seit dem gestempelten Plan geändert hat, fällt aus dem Lauf, gezählt als `skippedDrift`; ein unvollständiger oder gescheiterter Read lässt **keine** `full`-Zeile laufen | Das Muster aus Plan-230 §2 Nr. 2–5 und `recheckTransferPlan`, um die Prüfung je REMOVE erweitert: bei sequentiellem Lauf mit Rate-Limit-Pausen von bis zu Minuten (`seven-tv-run-engine.ts:36-46`) ist ein einmaliger Frischcheck für die zehnte Zeile so alt wie der Dialog-Read für die erste (Codex-Befund 1). Das Restfenster schrumpft damit auf die Zeit zwischen dem Zeilen-Read und dem REMOVE selbst (F13); ganz zu schließen ist es ohne atomare 7TV-Operation nicht (Plan-230 §2 Nr. 6) |
| E15 | Unload-Schutz und Arbiter | Der Undo registriert sich als vierter Lauf-Typ `'undo'` beim Arbiter, den **#256 Punkt 1 liefert** (Vorbedingung, Vertrag in 11.1): der Arbiter kennt je Dienst „läuft" **und** „settelt noch" (Nachlesen oder Meldungen ohne Endzustand), und jeder Startpunkt — Delete, Restore, Import, Undo — startet nur, wenn **kein** Dienst in einem dieser Zustände ist. Der Unload-Schutz (`beforeunload`) ist die Vereinigung der destruktiven offenen Läufe aller Dienste: ein Undo-Lauf mit mindestens einer `full`-Zeile zählt, solange er läuft, settelt oder eine seiner beiden Meldungen keinen Endzustand hat — auch nach `reset()` oder wenn ein neuer Lauf gezeigt wird (E22). Der `canDeactivate`-Guard der Nutzungsseite deckt zusätzlich `undoService.isRunning()` | Dieselbe Begründung wie Plan-230 §2 („ein Tab, der mitten zwischen REMOVE und ADD stirbt, hinterlässt eine Lücke ohne Ergebnisprotokoll"). Codex-Befund 5: ein zweiter Lauf während des Settlings ersetzte den gezeigten Lauf und ließ den Schutz des ersten fallen. **Betreiber 2026-09-25 (Abschnitt 15 B):** die Sperre liegt im Arbiter, den #256 vor #254 baut — keine lokale Doppelsperre im Undo |
| E16 | Resync und Seite | Wie der Restore **nach der #255-Entscheidung vom 2026-09-25** (11.4): **kein** Client-Resync im Erfolgsfall — für kein Ziel, auch nicht für ein nicht-aktives Set eines getrackten Kanals; der Backend-Resync aus den beiden Meldungen deckt jeden getroffenen Kanal, das Dock zeigt dafür `backendTriggered` („wird abgeglichen"). Einziger Client-Resync: der N1-Fallback für `expectedChannelName` (das aktive Set eines getrackten Kanals), wenn **beide** Meldungen endgültig scheitern. Mitgliederliste des gewählten nicht-aktiven Sets beim Settle mit `refresh`, sonst Vormerkung (N2) | Keine eigene Regel; der Undo ist der vierte Lauf, für den die #253-Regeln in ihrer #255-Fassung gelten. Ein Resync eines nicht-aktiven Sets lädt dessen Mitgliederliste ohnehin nicht nach (N2-Befund) — das erledigt die Seite beim Settle. Der Cooldown fängt Doppelte (F15 dort) |
| E17 | Slot-Grenze | Projektion im Dialog: `delta = Σ fehlende Einträge − Anzahl full-Zeilen`; Überschreitung ist eine **Warnung** wie im Import-Dialog (`import-confirm-dialog.ts:923-934`), keine Sperre | Eine `full`-Zeile mit einem Eintrag ist netto 0 und kann an der Kapazität nicht scheitern, weil ihr REMOVE zuerst läuft; nur eine Duplikat-Zelle (#74) mit ≥ 2 Einträgen ist netto positiv. 7TV kennt keinen Slot-Fehlercode, den das Frontend heute auswertet; ein gescheitertes ADD ist eine benannte Lücke (E9). Eine Sperre bräuchte eine Kapazitätsquelle, die für ungetrackte Ziele über einen budgetierten Read kommt (Abschnitt 13, Nr. 6) |
| E18 | Idempotenz | Dieselbe Datei ein zweites Mal: jede Zeile ist `nothingToDo` (Quelle hält den Namen nicht mehr, Ziel-Einträge vorhanden) ⇒ transiente Notiz, kein Dialog, kein Lauf, keine Datei. Nach einem **teilweise** gescheiterten Lauf ist der zweite Lauf derselben Datei der Weg, der die Lücke schließt (`addOnly` für die fehlenden Ziel-Einträge, E9) | Folgt aus E5/E7 ohne Sonderregel: der Live-Check klassifiziert, die Datei behauptet nichts. Dasselbe gilt für eine Übertragungsdatei eines nie gestarteten Laufs (Quelle nie da, Ziel vorhanden) |
| E19 | **Prüfung vor jedem REMOVE** (Codex-Befund 1, Runde 2 Befund 1) | Unmittelbar vor **jedem** REMOVE einer `full`-Zeile liest der Dienst das Ziel-Set **neu und vollständig** (`loadSevenTvSetEntries`, tokenlos, `complete: true`) und klassifiziert **diese** Zeile neu (4.3); nur bei unveränderter Klassifikation (Modus `full`, dieselbe ADD-Liste, Quelle exakt `{ alias }`, Ziel ohne fremden Eintrag) läuft das REMOVE, sonst wird die Zeile `skippedDrift` und der Lauf geht zur nächsten. **Kein Wiederverwenden eines früheren Reads** — auch nicht des Frischchecks, auch nicht eines Reads der vorigen Zeile, auch nicht für ein REMOVE, das die Engine nach einer Rate-Limit-Pause wiederholt: jeder REMOVE-Versuch hat seinen eigenen Read. Scheitert der Read oder ist er `complete: false`, wird die Zeile `skippedDrift` mit Grund `recheckUnavailable` (fail-closed); nach **drei** aufeinanderfolgenden Read-Fehlern bricht der Dienst die verbleibenden `full`-Zeilen als `cancelled` mit Grund ab, `addOnly`-Zeilen laufen weiter | Der Lauf ist sequentiell, mit 275 ms je Schritt und Rate-Limit-Pausen bis zu Minuten (Abschnitt 1); ein Editor kann einer späteren Quelle in dieser Zeit einen zweiten Alias geben, und das REMOVE nähme ihn mit, ohne dass die Rückweg-Datei ihn kennt (F2). Die zweite Fassung erlaubte, einen Read bis 5 s wiederzuverwenden — das widersprach AK 26 (die Zeilen liegen 275 ms auseinander, ein Alias, der nach dem ersten REMOVE dazukam, wäre im wiederverwendeten Read unsichtbar; Runde 2 Befund 1). Sicherheit gewinnt: die Koaleszierung entfällt. **Kosten:** ein tokenloser, paginierter Read je REMOVE-Versuch — bei ≤ 500 Einträgen eine Seite, bei HandOfBloods ~900 zwei; für einen Lauf mit N `full`-Zeilen also N bis 2N Requests zusätzlich zu den 2N+ Mutationen, verteilt über ≥ N × 0,55 s Engine-Takt. Das Read-Budget von 7TV für diese tokenlose Abfrage ist **ungemessen** (Abschnitt 10); ein 429 dort ist ein Read-Fehler und fail-closed, die Live-Verifikation (9.5 Nr. 6a) misst den Takt mit. Das Replace aus #230 hat dasselbe Fenster und **nicht** diese Prüfung — benannte Asymmetrie, kein Teil dieser Spec (Abschnitt 10). Die Engine hat heute keinen Vor-Schritt-Hook (`runRowFrom`, `seven-tv-run-engine.ts:389-406`); wie die Prüfung eingehängt wird, ist Plansache |
| E20 | **Fremde Einträge auf dem Ziel** (Codex-Befund 3, Runde 2 Befund 3) | Trägt die Ziel-ID im Live-Set einen Eintrag, den die **normalisierten** Einträge der Datei (`N`, E21) nicht nennen — ein benannter Alias ∉ `N`, oder ein aliasloser Eintrag, obwohl `E` kein `null` enthält —, gilt: eine **`full`-Zeile ist übersprungen** (`targetHasForeignEntries`, Live-Gegenstück, nichts berührt) an allen drei Prüfstellen (E14); eine **`addOnly`-Zeile läuft trotzdem**, eintragsweise: sie fügt nur die fehlenden Einträge aus `N` hinzu, lässt den fremden Eintrag `C` unangetastet und trägt `targetHasForeignEntries` als **Vermerk** (`notes`) in Dialog, Dock und Protokoll | Fail-closed für die destruktive Zeile: ein Eintrag, den die Datei nicht kennt, ist etwas, das ein Mensch seither angelegt hat, und ein REMOVE darf nicht auf einem Ziel aufsetzen, dessen Zustand die Datei nicht beschreibt (F14). Die nicht-destruktive Zeile muss dagegen laufen dürfen, sonst gibt es keinen ausführbaren Reparaturweg (Runde 2 Befund 3): eine `full`-Zeile kann ihre Prüfung bestehen, REMOVE S und ADD A schaffen, dann bekommt T den fremden Alias `C`, und ADD B scheitert — die Wiederholung aus E9 wäre mit einer `addOnly`-Sperre blockiert, der Restore-Filter wirft die Übertragungszeile (Regel 2, `already-present-filter.ts:210-215`), die Undo-Datei kann S nicht zurückholen (T hält A): drei geschlossene Türen. Ein ADD neben `C` entfernt nichts und ist per Purge umkehrbar; das Live-Gegenstück zeigt `C` im Dialog, der Nutzer entscheidet mit Bildern. Die zweite Fassung sperrte auch `addOnly` „konsistent mit Regel 2" — die Konsistenz war die falsche: Regel 2 schützt eine Zeile, die den Eintrag nicht kennt, vor einem **Re-ADD**; hier kennt die Zeile ihre eigenen Einträge, `C` ist ein Dritter |
| E21 | **`null`-Einträge bekommen einen expliziten Namen** (Codex-Befund 4, Runde 2 Befund 2) | Ein Ziel-Eintrag `{ alias: null }` wird als `ADD { alias: D }` mit dem **`defaultName` aus der Datei** gesendet, nicht als ADD ohne Alias. **Die Normalisierung ist der erste Schritt der Zielprüfung:** aus `E` entsteht die Namensmenge `N` (benannt ⇒ der Alias, `null` ⇒ `D`), und **jede** weitere Prüfung des Ziels — fremde Einträge, Vorhandensein, Namensfreiheit, Frischcheck, Prüfung vor dem REMOVE, Nachlesen — arbeitet auf `N`, nie auf dem rohen `E`. Ein zurückgeholter `null`-Eintrag steht im Live-Set als benannter Eintrag `D` und ist damit ein **eigener** Eintrag der Zeile, kein fremder. Fehlt `D` in der Datei (`null` oder leer), ist die Zeile übersprungen mit Grund `targetNameUnverifiable` — bei `full` die ganze Zeile, bei `addOnly` nur dieser Eintrag (`omittedEntries`) | Nach einem geglückten Replace ist das Ziel nicht im Set, der Live-Read kennt also keinen `defaultNameById`-Wert für T (`seven-tv-set-entries.ts:41-71`) — `n = e ?? D` war ohne `D` aus der Datei unbestimmt, und ein ADD ohne Alias landete unter 7TVs **heutigem** Standardnamen, den niemand geprüft hat: ein 409 nach dem REMOVE, eine Lücke. Mit explizitem `D` prüft und schreibt der Undo denselben Namen; das Ergebnis ist identisch mit dem, was ein ADD ohne Alias erzeugt hätte (7TV legt ohnehin nur benannte Einträge an, F5), und es ist der Name, den das Ziel **damals** trug — genau der Zustand vor der Übertragung, auch wenn der Emote-Besitzer den Standardnamen seither geändert hat. Ein Einzel-Emote-Lookup existiert im Client nicht und wäre ein budgetierter Request je Eintrag |
| E22 | **Settling schließt den nächsten Lauf aus** (Codex-Befund 5) | **Über den Arbiter aus #256 Punkt 1, nicht über eine lokale Sperre.** `startUndoFlow` prüft — wie jeder andere Startpunkt — allein den Arbiter, und der antwortet „belegt", solange irgendein Dienst läuft **oder settelt** (Nachlesen läuft, oder eine Meldung hat noch keinen Endzustand `succeeded \| partial \| failed`). Der Undo-Dienst liefert dem Arbiter dafür sein `isRunning` und sein `isSettling` (11.1) und trägt seine destruktiven offenen Läufe in den gemeinsamen Unload-Schutz ein; ein `reset()` oder ein neuer gezeigter Lauf nimmt einen noch meldenden Lauf nicht aus dem Schutz | Der heutige Arbiter sieht nur `isRunning` (`seven-tv-run-arbiter.ts:46-55`); ein zweiter Undo mit reinem `addOnly`-Plan hätte den gezeigten Lauf ersetzt, `destructiveRunActive` wäre dem neuen Plan gefolgt und der Tab ohne Schutz gewesen, während die Meldungen des ersten Laufs noch ausstanden — und seine zweite Mutation hätte verändert, was das Nachlesen des ersten seinem `unknown`-Schritt zuschreibt. **Betreiber 2026-09-25:** #256 Punkt 1 wird vor #254 gebaut und gemergt (Reihenfolge #255 → #256 → #254); eine lokale Doppelsperre hätte keinen Gewinn und müsste wieder raus (Abschnitt 15 B) |
| E23 | **Teilweise Zeilen sind sichtbar und dauerhaft** (Codex-Befund 6, Runde 2 Befund 4) | `omittedEntries: { alias, reason }[]` (`targetNameTaken`, `targetNameUnverifiable`) ist ein **eigenes Feld** der Zeile, **orthogonal** zum Ausführungsstatus: es steht in der Protokollzeile beider Stufen und im Dock, gleich wie die Zeile endete. Der Status `partial` — den nur der Undo-Dienst setzt, die Engine kennt ihn nicht — ersetzt beim Settle **ausschließlich** ein `done`: eine `addOnly`-Zeile, deren gesendete ADDs alle bestätigt sind und die Einträge ausgelassen hat, wird `partial`. Eine Zeile, die `failed`, `unknown` oder `cancelled` endete, **behält** diesen Status und trägt `omittedEntries` daneben; sie zählt in `failed`/`unknownCount`/`cancelled` **und** in `omittedEntryCount`, nie in `partialRows`. Das Dock zählt `partialRows` und `omittedEntryCount` getrennt und nennt den Weg (Name freimachen, Undo erneut). Für `full`-Zeilen gibt es keinen teilweisen Plan (E8) | Ein „fertig" ohne den zweiten Alias wäre eine Lücke ohne Papierspur; `gapCount` zählte nur `failed`-Zeilen. Die zweite Fassung setzte `partial` „unabhängig vom Engine-Status" — das hätte eine `unknown`-Zeile (ein ADD ohne Antwort, Nachlesen unlesbar) aus `unknownCount` und dem Hinweis „bei 7TV prüfen" genommen und einen `failed`-ADD hinter der Auslassung versteckt (Runde 2 Befund 4, F19) |
| E24 | **Nachlesen nach Modus und Operation** (Codex-Befund 7) | Das Nachlesen einer `unknown`-Zeile richtet sich nach der **Operation des Schritts**, nicht nach seiner Nummer: `full` ⇒ Schritt 0 ist REMOVE, Schritte ≥ 1 sind ADDs; `addOnly` ⇒ **jeder** Schritt ab 0 ist ein ADD. Tabelle in 4.6 | Die erste Fassung ordnete Schritt 0 pauschal dem REMOVE zu; eine wörtliche Umsetzung hätte bei einer `addOnly`-Zeile eine abwesende Quelle als bestätigtes REMOVE gelesen und die Quell-ID in die Löschmeldung geschrieben |

---

## 3. Fallen, im Code nachgeprüft am 2026-09-25

### F1 — Die Datei sagt nicht, ob die Quelle den Namen hält; nur `finished`/`done` legt es nahe

Ein Ergebnisprotokoll mit `status: 'done'` beschreibt den Stand **am Ende des Laufs**; seither kann
ein Editor die Quelle umbenannt, entfernt oder ergänzt haben. Eine `planned`-Datei weiß nicht
einmal, ob der Lauf begann, und enthält Zeilen, die `recheckTransferPlan` verworfen hat
(`import-flow.ts:194-234`). **Vorgabe (E2, E5):** die Datei liefert Kandidaten, der Live-Read
klassifiziert; kein Wert aus der Datei wird dem Live-Stand gleichgesetzt. `status` und `confirmed`
dienen nur der Kandidatenwahl.

### F2 — Ein REMOVE nimmt alle Einträge der ID, auch die, die nach dem Lauf entstanden sind

`removeEmote` kennt keinen Alias (`seven-tv-delete.service.ts:44-58`). Ein Quell-Emote, das seit
der Übertragung einen zweiten Alias bekam, verlöre beide. **Vorgabe (E5):** die Eintragsmenge der
Quelle muss exakt `{ alias }` sein; jede Abweichung ist Drift mit Grund (`sourceHasMoreEntries`,
`sourceUnderOtherName`), die Zeile läuft nicht, der Dialog zeigt das Live-Gegenstück.

### F3 — Der Restore-Filter kennt nur eine Seite; der Undo braucht beide

`filterAlreadyPresentForRestore` reasoniert über **eine** ID je Zeile und ihre Aliase
(`already-present-filter.ts:111-188`); Regel 4 kennt keinen erlaubten Inhaber außer der Zeilen-ID.
Der Undo muss je Zeile die Quelle (exakt ein Eintrag) und das Ziel (fehlende Einträge, Inhaber
der Namen) zugleich prüfen, und für ihn ist die Quelle als Inhaber des kollidierenden Alias
**erlaubt** (E8). **Vorgabe:** eine eigene reine Klassifikation (`classifyUndoRows`, 6.2) neben
`verifyReplaceTargets`, aus demselben Read (`loadSevenTvSetEntries`), kein zweiter Request; der
Restore-Filter bleibt unverändert.

### F4 — Der Restore-Lauf trägt keine der Sicherungen einer Löschung

Keine `transportLossIsUnknown`, kein `abortOn`, kein Nachlesen, kein `beforeunload`, kein
`canDeactivate`, eine Meldung (`seven-tv-restore.service.ts:46-58`, `usage-stats-leave.guard.ts:30-55`).
Der Undo als „Modus" hätte jede davon in den Restore-Dienst getragen. **Vorgabe (E1):** eigener
Dienst nach dem Muster des Import-Dienstes (Settling, zwei Meldungen, `destructiveRunActive`), der
die Engine, den Set-Reader, die Meldeclients und die Klassifikation der Dreiwertigkeit teilt — was
davon als gemeinsamer Baustein extrahiert wird, entscheidet der Plan (Berührung mit #256 Punkt 5).

### F5 — Ein `null`-Eintrag kommt als benannter Eintrag zurück

7TV legt keine aliaslosen Einträge mehr an; ein ADD ohne Alias landet unter `defaultName`, und ein
Re-Read zeigt `alias: "<defaultName>"` (Abschnitt 1). Folgen: (a) der Vorhandenseins-Check für
einen `{ alias: null }`-Eintrag muss **beides** anerkennen — `aliaslessIds.has(id)` **oder**
`aliasesById.get(id)` enthält `defaultName` (der Restore-Filter kennt nur das Erste, Regel 3, und
läse den zurückgeholten Eintrag beim nächsten Lauf als „fremden Alias" nach Regel 2; das ist eine
Restore-Frage, hier nur benannt, 11.3); (b) der kollidierende Alias einer Replace-Zeile **kann**
der `defaultName` des Ziels sein (das Ziel stand aliaslos unter seinem Standardnamen, die Quelle
bekam genau diesen Namen) — das REMOVE der Quelle gibt ihn frei wie jeden anderen (E6); (c) der
Undo sendet für einen `null`-Eintrag gar keinen ADD ohne Alias mehr, sondern `ADD { alias: D }`
mit dem `defaultName` aus der Datei (E21, F15), und das Nachlesen sucht genau diesen Namen. Der
Live-Read (`defaultNameById`) kennt ein Ziel nach dem Replace nicht mehr; `D` kommt deshalb aus
der Datei, und ohne `D` läuft die Zeile nicht (E21).

### F6 — `planned` und `finished` einer Übertragung sind zwei Dateien für einen Lauf

Beide sind Kandidatenquellen (E2). Wer beide hat und beide nacheinander einliest, bekommt beim
zweiten Mal `nothingToDo` (E18) — kein doppelter Lauf. Wer nur `planned` hat (Tab starb), bekommt
Kandidaten, von denen der Live-Read die verworfenen und nie gelaufenen aussortiert. Der Undo trägt
in `meta.undoneFile` fest, aus welcher Datei er kam (Stufe, Zeitstempel, `origin`), damit ein
Mensch die Kette lesen kann.

### F7 — Die `transfer-undo`-Datei ist eine vierte Einlesesorte, und drei Parser müssen sie kennen

`readEnvelope`/`ExportKind` (`shared/export/export-envelope.ts:9-28`), der Import-Parser
(`import-source-parser.ts`, namentliche Abweisung wie `transfer-run`), der Datei-Schritt (Dispatch
`purge-run` → Restore, `transfer-run` → Weiche, `transfer-undo` → Restore ohne Weiche), der
Restore-Parser (neu: `parseTransferUndoForRestore`), und der Undo-Parser selbst weist
`transfer-undo` als Eingabe ab (kein Undo des Undo — der Weg zurück ist die Übertragung selbst,
Abschnitt 10). Ein Build vor dieser Spec weist die Datei mit `wrongKind` ab — mit Grund, wie
Plan-230 §8 es für `transfer-run` vorsieht.

### F8 — Zwei Meldungen, ein Resync-Budget, ein Fallback

Der Undo meldet `sync-deleted` **und** `sync-restored` an dasselbe Set; jede Meldung kann Stufe 7
(Backend-Resync) auslösen, der Cooldown lässt je Kanal und 60 s einen durch (#253 F15). Der Client
liest **beide** `resyncTriggered` nur für die Anzeige (`backendTriggered`, sobald eine der beiden
Listen den Kanal nennt) und stößt im Erfolgsfall **keinen** eigenen Resync an (E16). Der N1-Fallback
für `expectedChannelName` greift, wenn **beide** Meldungen endgültig scheitern — scheitert nur eine,
hat die andere Stufe 7 erreicht. **Festlegung:** die Reihenfolge ist `sync-deleted` zuerst, dann
`sync-restored` (die Löschung ist die destruktive Tatsache; ihre Papierspur zuerst).

### F9 — Der Arbiter kennt drei Laufarten, der Guard kennt einen Dienst, und die Route zieht um

`activeRun` ist aus drei `isRunning`-Signalen abgeleitet (`seven-tv-run-arbiter.ts:8-57`); der
Leave-Guard fragt nur `importService.isRunning()` (`usage-stats-leave.guard.ts:30-55`);
`importTriggerDisabled` sperrt den Einstieg, solange irgendein Lauf aktiv ist
(`shared/seven-tv/import-trigger.ts`). Jede dieser drei Stellen bekommt den Undo dazu; sonst
startete ein Delete neben einem laufenden Undo, oder ein Kanalwechsel ginge ohne Rückfrage durch.
#256 Punkt 1 ändert den Arbiter parallel (11.1).

**#264 (parallel):** die Nutzungs-Route wird ein Parent mit `canActivate` und `loadChildren`; die
neue Datei `features/usage-stats/usage-stats.routes.ts` bindet `UsageStatsPage` statisch ein und
trägt `canDeactivate: [usageStatsLeaveGuard]` — der Guard hängt dann dort, nicht mehr in
`app.routes.ts`. Für den Undo bleibt die Aussage „der Guard fragt bei laufendem Undo" wahr; **was
dazukommt:** der Guard darf `SevenTvUndoService` nur so einbinden, dass nichts davon ins
Initial-Bundle gerät (der Grund für #264 war genau ein statischer Import des Import-Dienstes aus
dem Guard heraus, 510,4 kB gegen 500 kB Budget). Nach #264 ist das automatisch erfüllt, solange
der Guard **nur** in `usage-stats.routes.ts` referenziert wird — ein Import des Guards oder des
Undo-Dienstes aus `app.routes.ts` oder einem eagerly geladenen Modul wäre eine Regression, die
der neue Router-Spec (`leadsToSameRoute`) und `ng build --stats-json` sichtbar machen.

### F10 — Die Weiche im Datei-Schritt ist eine Verhaltensänderung für bestehende E2E-Tests

Heute schließt der Datei-Schritt für eine `transfer-run`-Datei sofort mit `picked` (`kind:
'restore'`). Mit der Weiche (E1) braucht jeder E2E- und Spec-Fall, der eine Übertragungsdatei
einliest, einen Klick mehr (`emote-import.e2e.spec.ts`, `file-import-step.spec.ts`,
`import-trigger.spec.ts`, Fixtures aus #253 T5). Purge-Protokolle sind nicht betroffen (F1 der
#253-Spec: es gibt genau zwei Klassen, und nur eine bekommt die Weiche).

### F11 — Der kollidierende Alias steht immer in `removedTarget.entries`

Ein Replace entsteht nur aus einer Namenskollision: das Ziel hielt `row.name`, und genau dieser
Name wurde `alias` (`conflict-resolution.ts:294-332`). Also ist `alias` ∈ `entries` (als benannter
Eintrag oder als `defaultName` eines `null`-Eintrags, F5). Eine `full`-Zeile hat deshalb **immer**
mindestens einen fehlenden Ziel-Eintrag (den, den die Quelle hält) — eine `full`-Zeile mit null
ADDs ist ein Widerspruch und wird als Drift behandelt, nicht als leerer Lauf. Seit E8 (dritte
Fassung) ist der Fall durch die Alles-oder-nichts-Regel abgedeckt: ein `full`-Kandidat, dem auch
nur ein Eintrag fehlt, weil ein Dritter den Namen hält, läuft gar nicht.

### F13 — Der Frischcheck altert während des Laufs (Codex-Befund 1)

Die Engine läuft sequentiell mit 275 ms je Schritt und pausiert bei Rate-Limit bis zu
`reset + 0,5 s`, sonst 60 s, bis zu fünfmal (`seven-tv-run-engine.ts:36-46`, `:430-462`). Ein Lauf
mit zwanzig `full`-Zeilen dauert Sekunden, mit einer Pause Minuten; der eine Frischcheck vor dem
Start bürgt dann für die letzte Zeile so wenig wie der Dialog-Read für die erste. Ein Editor, der
in dieser Zeit einer späteren Quelle einen zweiten Alias gibt, verlöre ihn mit dem REMOVE, und die
Rückweg-Datei (aus dem Dialog-Read) kennt ihn nicht. **Vorgabe (E19):** Prüfung je REMOVE-Versuch
gegen einen **eigenen, vollständigen** Read — kein Wiederverwenden, auch nicht binnen Sekunden,
auch nicht nach einer Rate-Limit-Wiederholung; Read-Fehler ⇒ Zeile übersprungen; drei Fehler in
Folge ⇒ Rest der `full`-Zeilen `cancelled`. Die zweite Fassung ließ einen Read bis 5 s
wiederverwenden; bei 275 ms zwischen zwei Zeilen hätte die zweite Zeile fast immer den Read der
ersten benutzt, und genau den Alias, den AK 26 erkannt sehen will, nicht gesehen (Runde 2
Befund 1). **Rest:** die Zeit zwischen dem Zeilen-Read und dem REMOVE selbst (die Latenz eines
Requests) bleibt offen — dieselbe Klasse wie Plan-230 §2 Nr. 6, nur kleiner. Ein in diesem Rest
hinzugefügter Alias ist in keiner Datei; das Ergebnisprotokoll trägt die Einträge des letzten
Reads je Zeile (`removedSource.entries`), damit ein Mensch wenigstens sieht, was der Read zuletzt
sah. **Preis:** N bis 2N tokenlose Reads je Lauf mit N `full`-Zeilen; das 7TV-Budget dieser
Abfrage ist ungemessen (Abschnitt 10), ein 429 ist fail-closed ein Read-Fehler.

### F14 — Regel 2 des Restore-Filters macht die Übertragungsdatei bei einem fremden Ziel-Eintrag wertlos (Codex-Befund 3)

`missingAliases` wirft die **ganze** Zeile, sobald die ID im Live-Set unter einem Alias steht, den
die Zeile nicht nennt (`already-present-filter.ts:210-215`). Hat jemand das Ziel T nach der
Übertragung unter einem neuen Namen `C` zurückgeholt, kann ein Restore aus der Übertragungsdatei die
alten Aliase `A`/`B` von T nie mehr ergänzen. Für den Undo hieße das: läuft eine `full`-Zeile in
diesem Zustand (REMOVE S, ADD A, ADD B scheitert), gibt es **keine** Datei, die B zurückbringt — und
die Quelle ist weg. **Vorgabe (E20):** fremde Einträge auf dem Ziel sind ein Sperrgrund für
`full`-Zeilen an allen drei Prüfstellen; `addOnly`-Zeilen laufen eintragsweise daneben und lassen
den fremden Eintrag stehen — sonst gäbe es nach einem Teilerfolg, bei dem `C` **während** der
Zeile dazukam, keinen ausführbaren Reparaturweg (Runde 2 Befund 3; die zweite Fassung sperrte
`addOnly` mit). **Folge für E9:** der kanonische Weg, eine Lücke nach
einem teilweise gescheiterten Undo zu schließen, ist der Undo selbst (zweiter Lauf, `addOnly`),
nicht der Restore aus der Übertragungsdatei; der Dock-Hinweis sagt das. Ob Regel 2 im
Restore-Filter für Zeilen aus Übertragungs- und Undo-Dateien zu streng ist, ist eine
Restore-Frage und steht als Nebenbefund für #256 (11.3).

### F15 — Ein `null`-Eintrag hat nach dem Replace keinen Live-Standardnamen (Codex-Befund 4)

`defaultNameById` kennt nur IDs, die im Set stehen (`seven-tv-set-entries.ts:41-71`); nach einem
geglückten Replace steht T nicht im Set. `removedTarget.defaultName` wird beim Replace aus dem
Verifikations-Read gestempelt (`stampReplaceTargets`, `already-present-filter.ts:304-322`,
`liveRowTarget`, `transfer-run-export.ts:126-137`) und ist deshalb in einer regulären Datei
gesetzt — aber der Typ ist `string | null` (`:49`), der Parser lässt `null` durch (`:433`), und
der Standardname eines Emotes kann sich seit dem Stempel geändert haben. Ein ADD ohne Alias landet
unter 7TVs **jetzigem** Standardnamen, den der Klassifikator nie gesehen hat. **Vorgabe (E21):**
der Undo sendet `D` aus der Datei als expliziten Alias und prüft gegen dasselbe `D`; ohne `D` läuft
die Zeile nicht (`full`) bzw. der Eintrag nicht (`addOnly`, `omittedEntries`).

### F16 — Ein zweiter Lauf während des Settlings löst den Schutz des ersten (Codex-Befund 5)

`activeRun` ist aus drei `isRunning`-Signalen abgeleitet und fällt mit dem Engine-Ende
(`seven-tv-run-arbiter.ts:46-55`); das Settling (Re-Read bis 20 s) und die Meldungen danach liegen
außerhalb. `destructiveRunActive` des Imports folgt `run()` — dem **gezeigten** Lauf
(`seven-tv-import.service.ts:273-277`). Ein zweiter Undo mit `addOnly`-Plan ersetzte `run()`, der
Schutz folgte dem neuen Plan (keine `full`-Zeile ⇒ `false`), und ein Tab-Schließen in diesem
Fenster verlöre Meldung und Ergebnisprotokoll des ersten Laufs. Dazu verfälschte die zweite
Mutation, was das Nachlesen des ersten Laufs seinem `unknown`-Schritt zuschreibt. **Vorgabe
(E22, Betreiber 2026-09-25):** die Sperre liegt im Arbiter — #256 Punkt 1 ist **Vorbedingung**
dieser Spec und liefert einen Arbiter, der „läuft oder settelt noch" über alle Lauf-Dienste kennt
und den Unload-Schutz über alle offenen destruktiven Läufe bis zum Endzustand von Settling und
Meldungen spannt. Der Undo registriert sich dort als vierter Lauf-Typ; der Vertrag, den #256
dafür erfüllen muss, steht in 11.1 (Abschnitt 15 B).

### F17 — Eine `planned`-Datei beweist nicht, dass ihr Lauf je begann (Codex-Befund 2)

Die Rückweg-Datei entsteht **vor** dem ersten REMOVE (Plan-230 §2); ein Lauf, der danach nie
startete (Token-Prompt abgebrochen, Tab geschlossen), hinterlässt eine gültige `planned`-Datei
ohne jede Spur eines Laufs. Entfernt später ein Mensch das Ziel und fügt die Quelle unter demselben
Namen hinzu — unabhängig von EmotePurge —, trifft der Live-Check die `full`-Form, und der Undo
entfernte ein von Hand hinzugefügtes Emote. Der Klassifikator beweist den **Zustand**, die Datei
belegt die **Herkunft** nur bei `finished`/`confirmed`. Das Audit hilft nicht: es trägt Zählwerte,
keine IDs (Plan-230 §2). Ein Sonderfall zweiter Ordnung (nie gestarteter Lauf **und** manuelle
Nachbildung desselben Zustands), aber einer, der eine Löschung autorisiert, die sich nicht beweisen
lässt. **Vorgabe (Betreiber 2026-09-25, Abschnitt 15 A):** Kandidaten aus `planned`-Dateien
tragen `provenance: 'unproven'`; jede unbelegte `full`-Zeile ist im Dialog sichtbar gekennzeichnet,
und ohne eine ausdrückliche Bestätigung je Datei läuft keine davon (`skippedUnproven`);
`addOnly`-Zeilen sind nicht betroffen.

### F18 — Ein zurückgeholter `null`-Eintrag sähe für die Zielprüfung wie ein fremder aus (Runde 2 Befund 2)

Die zweite Fassung prüfte fremde Einträge (Schritt 2) gegen das rohe `E` und wandelte `null` erst
danach (Schritt 3) in `D`. Nach `ADD { alias: D }` steht T im Live-Set unter dem benannten `D`, das
in `E` nicht als Name vorkommt — der nächste Read hätte die Zeile als `targetHasForeignEntries`
übersprungen: die Wiederholung (E9, E18) wäre blockiert, und die Prüfung vor dem REMOVE hätte
eine `full`-Zeile mit `null`-Eintrag nach einem früheren Teilerfolg nie wieder laufen lassen.
**Vorgabe (E21):** Normalisierung zuerst — jede Zielprüfung arbeitet auf `N`; ein aliasloser
Live-Eintrag auf T zählt als eigener, wenn `E` ein `null` nennt (F5, Altbestand vor dem
7TV-Verhalten „keine neuen aliaslosen Einträge"). Die E2E-Erwartung `addEmote(T2, null)` der
zweiten Fassung war ein Rest der alten Regel und ist auf `addEmote(T2, D)` korrigiert (9.4).

### F19 — `partial` darf `failed` und `unknown` nicht überschreiben (Runde 2 Befund 4)

6.5 der zweiten Fassung ließ den Dienst `partial` „unabhängig vom Engine-Status" setzen. Eine
`addOnly`-Zeile mit einem ausgelassenen Eintrag und einem ADD, der `unknown` blieb (Nachlesen
unlesbar), wäre damit `partial` geworden — aus `unknownCount` verschwunden, ohne den Hinweis „bei
7TV prüfen", obwohl eine Mutation mit unbekanntem Ausgang im Set stehen kann; ein `failed`-ADD
ebenso hinter der Auslassung versteckt. **Vorgabe (E23):** `omittedEntries` ist ein Feld,
`partial` ein Status nur für sonst fertige Zeilen; `failed`/`unknown`/`cancelled` bleiben, was sie
sind, und tragen die Auslassungen daneben.

### F20 — Ein `reset()` während des Laufs lässt heute Settling und Meldungen ausfallen (Runde 2 Befund 5)

`onRunComplete` des Imports kehrt zurück, sobald `this.run() !== started` ist — „only reachable
via reset() during the run" (`seven-tv-import.service.ts:583-592`): weder Nachlesen noch
`sendFollowUp` laufen dann; der Satz „`reset()` während des Nachlesens sendet trotzdem" (Plan-230
§7) gilt nur für ein `reset()`, das **nach** dem Engine-Ende kommt. Delete und Restore haben denselben
Frühausstieg (`seven-tv-delete.service.ts:292`, `seven-tv-restore.service.ts:321`) und setzen bei
`reset()` `syncReport` auf `idle` (`:248-253`, `:273-276`); ihre Rückrufe schreiben nur unter
`applyIfCurrent`. Ein Arbiter, der nur `isSettling`/`destructiveOpen`-Signale liest, könnte den
Stub-Test aus 11.1 bestehen und trotzdem eine Meldung verlieren oder einen offenen Lauf nie
schließen — dann bliebe der Arbiter für immer „belegt". **Vorgabe (11.1 Punkt 6):** der Abschluss
eines Laufs (Nachlesen, beide Meldungen, Endzustand) ist **lauf-gebunden**, nicht an `run()`
gebunden; `reset()` löst nur die Anzeige, nie den Abschluss; die Rückrufe eines abgelösten Laufs
schreiben seine Meldungszustände in den Laufdatensatz, nicht in Dienst-Signale, die inzwischen
einem anderen Lauf gehören. Für den Undo-Dienst gilt das ab dem ersten Commit; für Import, Delete
und Restore ist es Teil von #256 Punkt 1.

### F12 — `sync-restored` reaktiviert, was archiviert ist — und nur das

`MarkRestoredInSetAsync` zählt gefundene Zeilen und reaktiviert archivierte (#253-Spec 5.2 Schritt
4); eine Ziel-ID, die in einem getrackten Kanal nie eine Zeile hatte (weil der Replace ins
Ungetrackte ging oder der Kanal erst später gejoint wurde), ist `notFoundIds` ⇒ `partial`/`shortfall`
im Dock. Das ist heute beim Restore genauso und keine Undo-Besonderheit; der periodische Resync
legt die Zeile an. Der Dock-Wortlaut dazu ist #255.

---

## 4. Vertrag: Verhalten

### 4.1 Einstieg und Weiche

1. Der Einstieg bleibt **„Importieren → Datei"** auf jeder Kanalseite (`ImportTrigger`,
   `ImportSourceDialog`, `FileImportStep`); kein neuer Knopf, keine neue Tür, keine neue Seite.
   Der Trigger bleibt gesperrt, solange irgendein Lauf aktiv ist — jetzt auch ein Undo (F9).
2. Der Datei-Schritt liest den Umschlag und verzweigt auf `kind`: `purge-run` → Restore (unverändert);
   `transfer-run` → Parser (unverändert) → Vorprüfung `resolveEditableSet(meta.targetEmoteSetId)`
   (unverändert, vier Ausgänge nach #253 4.2) → **Weiche**; `transfer-undo` → Restore-Parser für
   Undo-Dateien (6.4) → Vorprüfung → `picked` mit `kind: 'restore'`, ohne Weiche.
3. **Die Weiche** erscheint im Datei-Schritt an der Stelle, an der heute `picked` feuert, als zwei
   Optionen mit je einem Satz Erklärung (Wortlaut #255): „Lücken schließen" (Restore — holt entfernte
   Ziel-Einträge zurück, wo ihr Name frei ist) und „Ersetzungen rückgängig machen" (Undo — entfernt die
   Quell-Emotes, die ihren Namen übernommen haben, und holt die Ziele zurück; als destruktiv
   gekennzeichnet). Die Wahl ist eine Navigation, keine Bestätigung — der Bestätigungsdialog folgt.
   Der Dialog schließt mit `picked` erst nach der Wahl; ein Fehler der Vorprüfung steht wie heute
   im Banner des Schritts, vor der Weiche.
4. `FileImportResult` bekommt die dritte Art `{ kind: 'transfer-undo'; candidates: UndoCandidate[];
   target: ResolvedRestoreTarget; sourceFile: UndoSourceFileInfo }` (6.1). `ImportTrigger` ruft dafür
   `startUndoFlow` (6.3) statt `startRestoreFlow`.

### 4.2 Token, Arbiter, Dialog

5. Reihenfolge: **Vorprüfung (im Schritt) → Weiche → Arbiter (frei: kein Dienst läuft oder
   settelt, 11.1) → Token-Prompt → Bestätigungsdialog → Arbiter → Frischcheck → Lauf.** Token vor dem Dialog (E13);
   der Prompt-Titel spricht vom Löschen — richtig für den Undo, bekannt falsch für Restore/Kopieren
   (#255).
6. Der Bestätigungsdialog (`UndoConfirmDialog`, `app-dialog-panel-wide` wie der Auflösungsschritt)
   liest beim Öffnen das Ziel-Set live (`loadSevenTvSetEntries`, tokenlos, 20 s Timeout, nur der
   neueste Read zählt — dieselbe R15-Disziplin wie `import-confirm-dialog.ts:1204-1279`) und
   klassifiziert die Kandidaten (4.3). Bis der Read da ist, ist die Aktionszeile gesperrt mit Grund
   (`aria-describedby`, wie `loadingHint`). Ein Lesefehler oder `complete: false` zeigt ein Banner
   mit „Ziel neu laden" und gibt **nichts** frei — eine Liste, die nur die Hälfte kennt, darf keine
   Löschung freigeben (Plan-230 §2 Nr. 3).
7. Der Dialog zeigt je Kandidat eine Zeile mit **Quelle und Ziel nebeneinander als Bild** (das
   Muster aus #268: welche Seite Quelle, welche Ziel ist, und was mit jeder passiert), die
   Klassifikation (`full` „Quelle raus, Ziel zurück" · `addOnly` „nur Ziel zurück" · übersprungen mit
   Grund), die Aliase, die zurückkommen, und bei Drift das Live-Gegenstück. Darunter die
   Zusammenfassung: Anzahl `full`, Anzahl `addOnly`, Anzahl übersprungen je Grund, **Löschzahl**
   (= Anzahl `full`), Anzahl ADDs, Slot-Projektion mit Warnung (E17), Zielzeile (Set-Name,
   aufgelöste Set-ID, Besitzer, bei getracktem Ziel der Kanal), die „nicht aktiv"-Zeile für ein
   getracktes nicht-aktives Ziel, der Fremd-Hinweis bei `target.emoteSetId !== hostSelectedSetId`
   (#253 E21). Ein Dialog ohne eine einzige laufende Zeile (alles übersprungen) öffnet **nicht**:
   der Flow zeigt die transiente Notiz mit den Gründen (E18).
8. **Aktionszeile, drei Zustände** (Muster `import-confirm-dialog.ts:139-142`, `:985-992`): „Rückweg
   sichern" (`primary`) baut aus dem Read die `transfer-undo`-Datei der Stufe `planned` (6.4) und
   lädt sie per `downloadFile` herunter; erst der ausgelöste Download schaltet auf „Starten"; ein
   Fehler beim Download bleibt `idle` mit Notiz. Ändert sich die Klassifikation (Neuladen), fällt
   der Zustand auf `idle` zurück. Ein Plan **ohne** `full`-Zeile (nur `addOnly`) hat keine Löschung
   und keine Rückweg-Datei: die Aktionszeile zeigt direkt „Starten" — wie ein Import ohne Replace
   „Kopieren" zeigt (Plan-230 §2 Nr. 1).
9. „Starten" schließt den Dialog mit dem **gestempelten** Plan (die Klassifikation des Reads, aus
   dem die Datei entstand); „Abbrechen" verwirft alles, es gibt nichts zu bewahren.

### 4.3 Klassifikation je Kandidat (der Live-Check)

Aus einem Read `{ aliasesById, aliaslessIds, defaultNameById, complete }` und dem Kandidaten
`(S = Quell-ID, A = Alias, T = Ziel-ID, E = entries, D = defaultName)`; `held(name)` = die ID, die
`name` im Live-Set als Alias hält (aus `aliasesById` umgekehrt), `entriesOf(id)` = benannte Aliase
∪ `{ null }` falls `aliaslessIds.has(id)`. **Reihenfolge ist Vertrag:** die Normalisierung (Schritt
2) kommt vor jeder Prüfung des Ziels, weil ein zurückgeholter `null`-Eintrag im Live-Set benannt
ist (F18).

| Schritt | Prüfung | Ergebnis |
|---|---|---|
| 0 Datei-Doppel | zwei Kandidaten mit derselben Quell-ID oder derselben Ziel-ID (nur aus einer manipulierten Datei möglich; die Regeln `duplicateReplaceTarget` und die Quell-Deduplizierung schließen es beim Schreiben aus) | beide **übersprungen** `duplicateInFile` |
| 1 Quelle | `entriesOf(S) === { A }` (genau ein benannter Eintrag, ordinal gleich A, kein `null`) | Quellteil **REMOVE** |
| | `entriesOf(S)` leer | Quellteil **keiner** (Quelle schon weg) |
| | sonst | **übersprungen**: `sourceUnderOtherName` (A ∉ entriesOf(S)) oder `sourceHasMoreEntries` (A ∈ entriesOf(S), aber weitere) — Live-Gegenstück wird gezeigt; **nichts an dieser Zeile wird berührt**, auch das Ziel nicht |
| 2 Ziel, Normalisierung (E21, F18) | je `e ∈ E` der effektive Name `n`: benannt ⇒ `n = e`; `null` ⇒ `n = D` aus der Datei. Ergebnis ist die Namensmenge `N` und das Flag `E ∋ null`. Fehlt `D` (`null`/leer) für ein `null`-`e` ⇒ dieser Eintrag ist `targetNameUnverifiable` und **nicht** in `N` | bei `full` ⇒ ganze Zeile **übersprungen** `targetNameUnverifiable`; bei `addOnly` ⇒ Eintrag ausgelassen (`omittedEntries`), Rest läuft |
| 3 Ziel, fremde Einträge (E20) | `entriesOf(T)` enthält einen benannten Alias ∉ `N`, oder `null`, obwohl `E ∌ null` | bei `full` ⇒ **übersprungen** `targetHasForeignEntries`, Live-Gegenstück wird gezeigt, nichts berührt; bei `addOnly` ⇒ **läuft**, mit Vermerk `targetHasForeignEntries` in `notes` (Dialog zeigt den fremden Eintrag, Dock und Protokoll tragen den Vermerk), der fremde Eintrag bleibt unangetastet |
| 4 Ziel, vorhanden | je `n ∈ N`: vorhanden, wenn `aliasesById.get(T) ∋ n`, oder — nur für ein `null`-`e` — `aliaslessIds.has(T)` (F5) | vorhandene Einträge entfallen (`alreadyPresent`, gezählt je Eintrag) |
| 5 Ziel, frei | je fehlendem `n`: `held(n)` ∈ { keiner, S } | Eintrag wird **`ADD { alias: n }`** — immer mit explizitem Alias, auch für `null`-Einträge (E21); bei `held(n) === S` gibt das REMOVE ihn frei |
| | `held(n)` = dritte ID (E8) | bei `full` ⇒ ganze Zeile **übersprungen** `targetNameTaken`; bei `addOnly` ⇒ Eintrag ausgelassen (`omittedEntries`, Grund `targetNameTaken`) |
| 6 Zeile | Quellteil REMOVE, alle fehlenden Einträge als ADD | **`full`**, `stepCount = 1 + ADDs`; dazu `provenance` aus der Datei (`confirmed` \| `unproven`, F17) |
| | Quellteil REMOVE, 0 ADDs | **übersprungen** `inconsistent` (F11: unerreichbar, seit Schritt 5 die Zeile bei jedem belegten Namen ganz überspringt; bleibt als Wächter) |
| | Quellteil keiner, ≥ 1 ADD | **`addOnly`**, `stepCount = ADDs`; mit `omittedEntries` und/oder `notes`; endet `partial` nur, wenn sonst `done` (E23) |
| | Quellteil keiner, 0 ADDs | **übersprungen** `nothingToDo` |

`complete: false` oder ein Read-Fehler ⇒ **keine** Klassifikation, keine Freigabe (4.2 Nr. 6). Die
Klassifikation ist eine reine Funktion (`classifyUndoRows`, 6.2) und wird an **drei** Stellen
aufgerufen: im Dialog, im Frischcheck vor dem Start und je `full`-Zeile vor ihrem REMOVE (E14,
E19). Frischcheck und Zeilen-Prüfung vergleichen die neue Klassifikation mit der gestempelten:
gleicher Modus **und** gleiche ADD-Liste (Namen und Reihenfolge) ⇒ läuft; sonst ⇒ `skippedDrift`,
im Dock gezählt und genannt, im Ergebnisprotokoll als `cancelled` mit Grund (`skippedReason`).
`available: false` oder `complete: false` ⇒ keine `full`-Zeile läuft (`recheckUnavailable`);
`addOnly`-Zeilen laufen nach dem Frischcheck auch dann — die nicht-destruktive Hälfte darf, wie der
Restore, fail-open sein (Festlegung; der Restore-Filter ist ebenso fail-open,
`already-present-filter.ts:186`). **Herkunft (F17, Abschnitt 15 A, entschieden):** eine `full`-Zeile
mit `provenance: 'unproven'` läuft nur, wenn der Nutzer im Dialog die Datei-weite Bestätigung
gesetzt hat; ohne sie ist sie `skippedUnproven` — `addOnly`-Zeilen sind davon nicht betroffen.

### 4.4 Lauf

10. Der Undo-Dienst startet die Engine mit einer Queue-Zeile je laufendem Kandidat; Queue-Key =
    Quell-ID (wie der Import; eindeutig nach Schritt 0 der Klassifikation). Schritte nach E6:
    `full` ⇒ Schritt 0 `REMOVE { setId, emoteId: S }`, Schritte 1…n `ADD { setId, emoteId: T, alias:
    n }` — **immer mit explizitem Alias**, für einen `null`-Eintrag der Datei-`defaultName` (E21);
    `addOnly` ⇒ nur die ADDs. Operation mit `transportLossIsUnknown: true` für **alle** Zeilen
    (E10) und `abortOn` bei 401/403/`LACKING_PRIVILEGES` (E13). Engine-Takt, Rate-Limit-Pausen und
    Retries unverändert.
10a. **Vor jedem REMOVE-Versuch** (E19): ein **eigener, vollständiger** Read — nie ein
    wiederverwendeter, auch nicht nach einer Rate-Limit-Wiederholung desselben REMOVE — und
    Neuklassifikation dieser Zeile; abweichend ⇒ `skippedDrift`, Read-Fehler oder `complete: false`
    ⇒ `recheckUnavailable`, drei Read-Fehler in Folge ⇒ verbleibende `full`-Zeilen `cancelled` mit
    Grund `recheckUnavailable`, `addOnly`-Zeilen laufen weiter. Die Zeile im Ergebnisprotokoll trägt
    die Quell-Einträge des Reads unmittelbar vor ihrem REMOVE (F13).
11. Während des Laufs: Dock-Abschnitt sichtbar (4.7), `beforeunload` scharf (E15), Trigger und
    andere Startpunkte gesperrt (Arbiter). `cancel()`: laufende Zeile endet wie beim Import — mit
    `completedSteps > 0` als `failed` mit `cancelledMidRow`, sonst `cancelled`; ein Abbruch mitten in
    einem Request endet `unknown` (Plan-230 §7 „`cancel()` während eine Anfrage unterwegs ist").
12. Nach dem Engine-Ende: `settlement: 'pending'`; ohne `unknown`-Zeile sofort `settled`; sonst
    Nachlesen (4.6). Erst danach die Meldungen (4.5), das Protokoll (4.8) und die Seitenwirkung (E16).

### 4.5 Meldungen

13. **`sync-deleted`** an `POST /api/seventv/emote-sets/{targetSetId}/sync-deleted` mit
    `sevenTvEmoteIds` = Quell-IDs aller `full`-Zeilen mit `completedSteps >= 1` (dedupliziert),
    `expectedChannelName` = `trackedChannelName`, wenn `isActiveSet`, sonst `null` (#253 E18). Nur,
    wenn die Liste nicht leer ist.
14. **`sync-restored`** an `…/sync-restored` mit `sevenTvEmoteIds` = Ziel-IDs aller Zeilen mit
    mindestens einem bestätigten ADD (`full`: `completedSteps >= 2`; `addOnly`: `completedSteps >= 1`),
    dedupliziert; derselbe `expectedChannelName`. Nur, wenn nicht leer.
15. Reihenfolge: 13 vor 14 (F8). Beide mit den Retries und der Dreiwertigkeit aus #253 6.4
    (`succeeded | partial | failed` mit `syncReportReason`), beide mit eigener Dock-Zeile und eigenem
    „Erneut melden" nach N4 (nicht bei `channelMismatch`); die Dienste weisen den Retry am Dienst ab.
16. `unknown`-Zeilen, die das Nachlesen nicht klärte, stehen in **keiner** Meldung — außer der Teil
    derselben Zeile, der bestätigt ist: ein bestätigtes REMOVE (`completedSteps >= 1`) geht in 13,
    auch wenn ein späterer ADD `unknown` blieb (Plan-230 §9 Runde 2 Finding 1, gespiegelt).
17. Kein Client-Resync im Erfolgsfall (E16); das Dock zeigt `backendTriggered`, wenn eine der
    beiden Antworten den Kanal in `resyncTriggered` nennt. Fallback nach N1 für
    `expectedChannelName`, wenn **beide** Meldungen endgültig scheiterten; für ein nicht-aktives oder
    ungetracktes Ziel (`expectedChannelName: null`) keiner.

### 4.6 Nachlesen (`unknown`)

Ein Re-Read (`loadSevenTvSetEntries`, 20 s Timeout, lauf-gebundenes Ergebnis unter `applyIfCurrent`
wie `seven-tv-import.service.ts:583-616`) klärt je `unknown`-Zeile den Schritt, an dem sie stand —
**nach der Operation des Schritts, nicht nach seiner Nummer** (E24): in einer `full`-Zeile ist
Schritt 0 das REMOVE und jeder Schritt k ≥ 1 der ADD des Eintrags `adds[k − 1]`; in einer
`addOnly`-Zeile ist jeder Schritt k ≥ 0 der ADD des Eintrags `adds[k]`. `n` ist der explizite Alias
des Eintrags (E21).

| Operation des Schritts | Live-Befund | Klärung |
|---|---|---|
| REMOVE S (nur `full`, Schritt 0) | `entriesOf(S)` leer | REMOVE bestätigt: `completedSteps = 1`, Zeile `failed@1` mit Grund `removedButNotRestored` (die ADDs liefen nie) — Lücke, S in 13 |
| REMOVE S | `entriesOf(S) === { A }` | nichts passiert: `failed@0`, nicht in 13 |
| REMOVE S | sonst | bleibt `unknown` |
| ADD (T, n) an Schritt k | `aliasesById.get(T) ∋ n` | Schritt bestätigt: `completedSteps = k + 1`; die **folgenden** ADDs liefen nie ⇒ Zeile `failed@(k+1)` mit Lückengrund, sofern noch Einträge ausstanden, sonst `done` (bzw. `partial`, E23); T in 14 |
| ADD (T, n) | `n` fehlt auf T — frei oder von T-fremder ID gehalten | `failed@k`, Zähler unverändert; T in 14 nur, wenn `completedSteps` einen früheren ADD deckt; S in 13 nur bei `full` mit `completedSteps >= 1` |
| ADD (T, n) | Read scheitert / `complete: false` / Timeout | bleibt `unknown` |

Eine `addOnly`-Zeile kann **nie** in die Löschmeldung geraten — sie hat keinen REMOVE-Schritt,
gleich an welcher Nummer sie `unknown` blieb. Das Nachlesen kann `completedSteps` heben, nie senken. Unklärbar ⇒ `unknown` im Protokoll und im
Dock mit dem Hinweis, das Set bei 7TV zu prüfen; die Quelle steht dann in keiner Meldung, aber —
falls ihr REMOVE bestätigt war — in der `sync-deleted`-Meldung (Nr. 16). Der Dock-Hinweis, **wo** ein
unklärbares REMOVE aufgezeichnet ist (nur in der Undo-Rückweg-Datei), ist #256 Punkt 4 gespiegelt
und wird hier für den Undo von Anfang an gezeigt (11.2).

### 4.7 Dock

18. Ein eigener Dock-Abschnitt (`UndoProgressSection`, Muster `import-progress-section.ts`,
    `labelPrefix: 'undo'` am `RunProgressPanel`), außerhalb des Set-Gates, an `hostChannelName`
    gebunden (`resetIfChannelChanged` wie #253 E13): Fortschritt, Zähler `done`/`failed`/`cancelled`,
    zusätzlich `removedCount` (bestätigte REMOVEs), `restoredCount` (bestätigte ADDs, je Eintrag),
    `unknownCount`, `gapCount` (Zeilen `failed` mit `completedSteps >= 1` — Quelle weg, Ziel nicht
    vollständig zurück) **mit dem Satz, dass ein erneuter Undo aus derselben Datei sie schließt**
    (E9), `partialRows` (nur sonst fertige Zeilen mit Auslassungen) und `omittedEntryCount` (über
    alle Zeilen, auch `failed`/`unknown`, E23) mit dem Hinweis, den Namen freizumachen und den Undo
    zu wiederholen, `foreignNotedRows` (`addOnly`-Zeilen mit fremdem Ziel-Eintrag, E20) als Hinweis
    ohne Handlungsaufforderung, die transienten Übersprungen-Zeilen je Grund (`nothingToDo`,
    `sourceUnderOtherName`, `sourceHasMoreEntries`, `targetHasForeignEntries`, `targetNameTaken`,
    `targetNameUnverifiable`, `alreadyPresent`, `skippedDrift`, `recheckUnavailable`,
    `skippedUnproven`, `duplicateInFile`),
    zwei Meldungszeilen (Löschung, Wiederherstellung) mit Grund und Retry nach N4, die Resync-Zeile
    nur als `backendTriggered` oder als N1-Fallback (nie für ein nicht-aktives Ziel, E16),
    die Zielzeile, „Ergebnisprotokoll speichern" ab `settled` mit stillem `protocolNotSaved`,
    `insufficientPrivileges` nach Abbruch. Bildschirmleser-Zwilling über `DockOutcomeAnnouncer`.
    Wortlaute #255.
19. Die Mitgliederliste der Seite folgt N2; das Raster folgt `channel.synced` wie bei jedem Lauf.

### 4.8 Protokoll

20. Vor dem ersten REMOVE: `transfer-undo`, Stufe `planned`, aus dem Dialog-Read, nur JSON, Pflicht
    (E3). Nach jedem Undo-Lauf (auch nur `addOnly`): Stufe `finished`, JSON oder CSV, angeboten,
    nicht Pflicht — Dokument zweiten Rangs wie das Ergebnisprotokoll der Übertragung. Form in 6.4.
21. Beide Stufen sind über den Restore-Weg einlesbar (E12): `planned` ⇒ jede `full`-Zeile,
    `finished` ⇒ nur `removedSource.confirmed === true`; je Zeile eine `RestoreRow { sevenTvEmoteId:
    S, name: sourceName, aliases: [A] }`. Der Import weist die Datei namentlich ab; der Undo weist sie
    als Eingabe ab (F7).

### 4.9 Rechte, Set, Ziel

22. Vorprüfung nach #253 4.2 im Datei-Schritt; ein Set, das nicht bearbeitbar oder nicht mehr
    vorhanden ist, kommt nie bis zur Weiche. Ungetracktes Ziel: erlaubt (E4), Papier-Meldungen,
    kein Resync, Slot-Vorschau über `twitchLogin` — wie der Restore.
23. Recht während des Laufs entzogen: `LACKING_PRIVILEGES` ⇒ `abortOn`, Token gelöscht, Rest
    `cancelled`, Dock `insufficientPrivileges`; bestätigte Schritte werden gemeldet; ein 403 der Meldung
    ⇒ `failed`/`forbidden`, kein Nachmeldeweg (#253 E20).
24. Set zwischen Vorprüfung und Lauf gelöscht: Mutationen scheitern (`failed`), keine Meldung ohne
    bestätigten Schritt; gelingt eine (Race), antwortet die Meldung 404 ⇒ `failed`/`setNotFound`,
    Fallback-Resync nach N1.

---

## 5. Vertrag: Backend

**Keine Änderung.** Der Undo nutzt ausschließlich Bestehendes:

| Baustein | Stand (am Branch) | Verwendung durch den Undo |
|---|---|---|
| `GET /api/seventv/me/emote-set-targets` mit `editable` | `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs:138`, Policy `ForeignEmoteLookup` (10/min); Regel `EmoteSetEditability.IsEditable` (`src/EmotePurge.Core/Services/EmoteSetEditability.cs`), dieselbe wie in `ImportTargetOwnershipService.CheckAsync` | Vorprüfung im Datei-Schritt (Client-Kopie, ein Permit je Minute) |
| `POST /api/seventv/emote-sets/{id}/sync-deleted` | `SevenTvEndpoints.cs:286`, Body `SyncInSetRequest` (`:756`), Leiter `PassSyncInSetLadderAsync` (`:527`), Policy `Bookkeeping` (120/min) | Quell-IDs mit bestätigtem REMOVE (4.5 Nr. 13) |
| `POST /api/seventv/emote-sets/{id}/sync-restored` | `:318`, gleiche Form; Antwort `:769-793` | Ziel-IDs mit bestätigtem ADD (4.5 Nr. 14) |
| `MarkDeletedInSetAsync` / `MarkRestoredInSetAsync` | `src/EmotePurge.Infrastructure/Services/EmoteService.cs:208-333` (`MarkInSetAsync`: Treffer-Kanäle `:223-227`, Papier-Eintrag `:296-327`) | Zeilen archivieren bzw. reaktivieren; Papier für ungetrackt/nicht-aktiv |
| Audit `emotes.syncDeleted` / `emotes.syncRestored` | `src/EmotePurge.Core/Entities/AuditLogEntry.cs:9-33` (`AuditActions`), Projektion `AuditLogQueryService.cs:144` | je Meldung Kanal-Einträge und/oder Papier-Eintrag mit Besitzerkanal; keine neue `AuditActions`-Konstante |
| Backend-Resync unter Cooldown, `resyncTriggered` | `PublishAndResyncAfterSyncInSetAsync` (`SevenTvEndpoints.cs:558-588`), `TryTriggerGuardedResyncAsync` (`:607-655`), nie für `notTracked` (`:573`) | je Meldung; der Client stößt keinen zweiten an |
| `POST /api/channels/{c}/resync` | Policy `ChannelResync` (5/min), Cooldown 60 s | nur der N1-Fallback für `expectedChannelName`, wenn beide Meldungen endgültig scheitern (E16) |

Was das Audit **nicht** kann: einen Undo von einem Delete-plus-Restore unterscheiden — es gibt
serverseitig kein Replace-Konstrukt, ein Replace ist heute schon `syncDeleted` + `syncImported` als
zwei getrennte Einträge. Die Unterscheidung trägt das Protokoll (`meta.undoneFile`), nicht das
Audit (Abschnitt 13, Nr. 5). Die Backend-Tests
`tests/EmotePurge.Api.Tests/SevenTvEmoteSetSyncBookkeepingEndpointTests.cs` und
`tests/EmotePurge.Infrastructure.Tests/Integration/EmoteServiceTests.cs` decken beide Routen samt
Leiter, Papier-Eintrag, Mismatch und Resync; ein Undo bringt keinen neuen Backend-Fall.

---

## 6. Vertrag: Frontend

### 6.1 Datei-Schritt und Weiche

- `FileImportResult` bekommt die dritte Art:

  ```
  { kind: 'transfer-undo'
    candidates: UndoCandidate[]          // aus der transfer-run-Datei, s. u.
    target: ResolvedRestoreTarget        // wie beim Restore (#253 6.1), inkl. host*
    sourceFile: UndoSourceFileInfo }     // { stage, exportedAt, verifiedAt | finishedAt, origin }

  UndoCandidate {
    sourceSevenTvEmoteId: string         // TransferRunRow.sevenTvEmoteId
    sourceName: string
    alias: string                        // TransferRunRow.alias
    fileStatus: RunItemStatus | 'pending'
    target: { sevenTvEmoteId: string; entries: { alias: string | null }[]; defaultName: string | null }
  }
  ```

- `parseTransferRunForUndo(text)` neben `parseTransferRunForRestore`: dieselbe Leiter (Umschlag,
  `kind`, `formatVersion === TRANSFER_RUN_FORMAT_VERSION`, `meta`, `stage`, `rows`), Kandidaten
  nach E2; keine Kandidaten ⇒ `restore.import.errors.transferRunNoRows` (bestehender Schlüssel —
  die Menge ist dieselbe wie beim Restore). Ergebnis `{ ok, candidates, stage, target: { emoteSetId } }`.
- Der Schritt ruft **beide** Parser für eine `transfer-run`-Datei; gelingt der Restore-Parser,
  gelingt der Undo-Parser (gleiche Leiter, gleiche Zeilenauswahl). Die Weiche (4.1 Nr. 3) hält
  beide Ergebnisse und emittiert je Wahl `picked` mit `kind: 'restore'` oder `'transfer-undo'`.
  Neue Schlüssel unter `restore.import.choice.*` (Titel, zwei Optionen, zwei Erklärungen; #255).
- `ImportTrigger` verzweigt auf `picked.kind`: `'transfer-undo'` ⇒ `startUndoFlow(deps, result)`.
- UI-Designsprache §7.3 bekommt die Weiche als Vertrag: eine Übertragungsdatei endet im
  Datei-Schritt mit der Wahl der Richtung; ein Purge-Protokoll nicht.

### 6.2 Reine Klassifikation

`shared/seven-tv/undo-plan.ts` (rein, ohne Angular):

```
classifyUndoRows(candidates: UndoCandidate[], read: SevenTvSetEntries): UndoPlan
UndoPlan { rows: UndoPlanRow[]; skipped: UndoSkippedRow[]; counts: { full, addOnly, removals, additions, skippedByReason } }
UndoPlanRow { candidate; mode: 'full' | 'addOnly'; adds: { alias: string | null }[]; stepCount }
UndoSkippedRow { candidate; reason: 'nothingToDo' | 'sourceUnderOtherName' | 'sourceHasMoreEntries'
                 | 'targetHasForeignEntries' | 'targetNameTaken' | 'targetNameUnverifiable'
                 | 'inconsistent' | 'duplicateInFile'; live: { sourceEntries, targetEntries } }
UndoPlanRow      += provenance: 'confirmed' | 'unproven'; omittedEntries: { alias; reason }[]   // omittedEntries nur bei addOnly
UndoPlanRow      += notes: ('targetHasForeignEntries')[]                                        // nur bei addOnly (E20)
UndoPlanRow.adds : { alias: string }[]                                                          // immer der normalisierte Name n, nie null (E21)
normalizeTargetEntries(target): { names: string[]; hadNull: boolean; unverifiable: boolean }     // Schritt 2 aus 4.3, für sich testbar
classifyUndoRow(candidate, read): UndoPlanRow | UndoSkippedRow                                 // dieselbe Regel für eine Zeile (E19)
diffUndoPlans(stamped: UndoPlan, fresh: UndoPlan): { runnable: UndoPlanRow[]; drifted: UndoPlanRow[] }
sameClassification(stamped: UndoPlanRow, fresh: UndoPlanRow | UndoSkippedRow): boolean          // Modus + ADD-Liste (Namen, Reihenfolge); notes zählen nicht
summarizeUndoPlan(plan): { removeCount, addCount, slotDelta, omittedEntryCount, foreignNotedRows }
```

`alreadyPresent` ist ein Zähler je Eintrag am `UndoPlan`; `targetNameTaken` und
`targetNameUnverifiable` sind bei `full` Zeilengründe, bei `addOnly` Einträge in `omittedEntries`;
`targetHasForeignEntries` ist bei `full` Zeilengrund, bei `addOnly` ein Vermerk in `notes` (E8,
E20, E21, E23). Die Tabelle in 4.3 ist der Vertrag dieser Funktion; jede Zeile der Tabelle ist ein
Testfall (9.3).

### 6.3 Flow und Dialog

- `startUndoFlow(deps, result: FileImportResult<'transfer-undo'>)`: Arbiter (frei nach 11.1 —
  kein Dienst läuft oder settelt; bei Belegung die transiente Notiz, die der Arbiter aus #256 für
  jeden Startpunkt vorsieht) → Token (`openSevenTvTokenPromptDialog`) → `openUndoConfirmDialog(data)`
  → Arbiter → Frischcheck (`loadSevenTvSetEntries` + `classifyUndoRows` + `diffUndoPlans`) →
  `undoService.startUndo(target, runnable, drifted, counts, acknowledgedUnproven)`. Alles übersprungen
  und keine laufende Zeile ⇒ transiente Notiz über den Undo-Dienst (Mechanik `showDuplicateNotice`),
  kein Dialog. **Keine** lokale Settling-Prüfung im Undo-Flow und **keine** Spiegelzeile im
  `import-flow.ts` — beides leistet der Arbiter (E22, Betreiber 2026-09-25).
- `UndoConfirmDialogData { candidates, target (ResolvedRestoreTarget), sourceFile }`; Rückgabe
  `{ plan: UndoPlan; acknowledgedUnproven: boolean } | null` (der gestempelte Plan). Zustandsmaschine
  `idle → verifying → saved` (4.2 Nr. 8); „Ziel neu laden" setzt auf `idle`. Bilder über dieselbe
  Bild-URL-Ableitung wie der Auflösungsschritt (Plan-230 T1). Slot-Vorschau über
  `loadRestoreSlotPreview` (Gabel nach #253 4.3 Nr. 8). **Herkunft (F17, Abschnitt 15 A,
  entschieden):** stammt die Datei aus der Stufe `planned`, zeigt der Dialog je `full`-Zeile die
  Kennzeichnung „unbelegt" und über der Aktionszeile eine Bestätigung (Checkbox, Wortlaut #255:
  „Diese Rückweg-Datei belegt nicht, dass die Übertragung gelaufen ist; ich habe geprüft, dass die
  gezeigten Quell-Emotes aus ihr stammen"); ohne gesetzte Bestätigung bleibt „Rückweg sichern"
  gesperrt mit Grund, und `full`-Zeilen laufen nicht (`skippedUnproven`); `addOnly`-Zeilen sind
  davon unberührt. Für `finished`-Dateien gibt es weder Kennzeichnung noch Bestätigung.
- Berührung mit #256 Punkt 5: landet die extrahierte Zustandsmaschine des Import-Dialogs zuerst,
  konsumiert der Undo-Dialog sie; sonst trägt er eine eigene, minimale, und #256 zieht beide zusammen.

### 6.4 Datei `transfer-undo`

- `ExportKind` += `'transfer-undo'`; `TRANSFER_UNDO_FORMAT_VERSION = 1`; kein Bump von
  `EXPORT_FORMAT_VERSION` oder `TRANSFER_RUN_FORMAT_VERSION`.
- `meta`: `stage: 'planned' | 'finished'`, `targetEmoteSetId`, `targetChannelName: string | null`,
  `targetOwnerDisplayName: string | null`, `undoneFile: { stage: 'planned' | 'finished'; exportedAt;
  verifiedAt | finishedAt; origin: ImportOrigin }` (Provenienz, F6), `planned`: `verifiedAt`, `counts
  { planned, removals, additions }`; `finished`: `startedAt`, `finishedAt`, `counts { requested,
  succeeded, failed, cancelled, unknown, removed, added }`. Umschlag-`channelName` = Zielkanal oder
  `''` (wie `transfer-run`, wird vom Parser nicht gelesen).
- Zeile:

  ```
  TransferUndoRow {
    mode: 'full' | 'addOnly'
    sourceSevenTvEmoteId, sourceName, alias
    removedSource: { entries: { alias: string }[]; confirmed: boolean } | null   // null bei addOnly; entries = [{ alias }] aus dem Read
    restoredTarget: { sevenTvEmoteId; defaultName: string | null;
                      entries: { alias: string | null; added: boolean }[] }      // nur die ADDs dieser Zeile; added = Schritt bestätigt
    provenance: 'confirmed' | 'unproven'                                        // aus der Übertragungsdatei (F17)
    omittedEntries: { alias: string; reason: 'targetNameTaken' | 'targetNameUnverifiable' }[]  // nur addOnly (E23), orthogonal zu status
    notes: ('targetHasForeignEntries')[]                                                        // nur addOnly (E20)
    status: RunItemStatus | 'partial'; failedStep: number | null; completedSteps: number; errorMessage: string | null
                                                                                                // partial nur statt done; failed/unknown/cancelled bleiben (E23, F19)
    skippedReason: string | null                                                // finished: skippedDrift / recheckUnavailable / duplicateInFile …; sonst null
  }
  ```

  `removedSource.entries` ist in `planned` der Dialog-Read, in `finished` der **letzte** Read vor
  dem REMOVE dieser Zeile (F13); `restoredTarget.entries[].alias` ist immer der explizite Name
  (E21), nie `null`.

  `planned` ⇒ alle Zeilen `status: 'pending'`, `confirmed: false`, `added: false`, nur die laufenden
  Zeilen (übersprungene stehen **nicht** in der Rückweg-Datei — sie beschreibt, was weg sein kann).
  `finished` ⇒ alle Zeilen ungefiltert (auch `cancelled`, `unknown`, Drift), `confirmed = completedSteps
  >= 1` bei `full`, `added` je Eintrag aus `completedSteps`.
- Dateinamen: `emotepurge_<kanal-oder-setid>_transfer-undo-plan_<yyyy-mm-dd-HHmm>.json` und
  `emotepurge_<kanal-oder-setid>_transfer-undo_<stamp>.<json|csv>`. CSV (`finished`): `mode`,
  `source_name`, `alias`, `source_seven_tv_emote_id`, `removed_confirmed`, `target_seven_tv_emote_id`,
  `target_default_name`, `target_aliases` (`|`-getrennt, `null` als leer), `target_added` (`|`-getrennt
  `true`/`false`), `status`, `failed_step`, `error_message`.
- `parseTransferUndoForRestore(text)`: Leiter wie oben; Zeilen nach E12; `RestoreRow { emoteId: null,
  sevenTvEmoteId: S, name: sourceName, aliases: [alias], defaultName: null }`; keine Zeile ⇒ neuer
  Schlüssel `restore.import.errors.transferUndoNoRows`. `parseImportSource` weist `transfer-undo`
  namentlich ab (Schlüssel `restore.import.errors.transferUndo`, Muster `transferRun`); der
  Undo-Parser weist `transfer-undo` mit `wrongKind` ab.

### 6.5 Dienst

- `SevenTvUndoService` (`core/seven-tv/seven-tv-undo.service.ts`, Root): `startUndo(target:
  UndoRunTarget, rows: UndoPlanRow[], drifted, counts)`, `run(): Signal<UndoRunInfo | null>`,
  `isRunning`, `items` (Engine-Queue während des Laufs, danach das lauf-gebundene Ergebnis),
  `settlement`, `destructiveRunActive`, `removalReport`/`restoreReport` (je `SyncReportState` +
  `syncReportReason`), `retryRemovalReport()`/`retryRestoreReport()` (N4), `resyncTrigger`,
  `protocolSaved`, `reset()`, `resetIfChannelChanged(hostChannelName)`, `cancel()`.
  `UndoRunTarget = { setId, expectedChannelName, hostChannelName, setName, ownerOrChannelLabel }`
  — die Form des Restore (#253 6.4) **ohne** `resyncChannelName`, weil es keinen Client-Resync für
  ein nicht-aktives Ziel mehr gibt (E16, 11.4); `resyncTrigger` kennt nur `idle | backendTriggered |
  pending | succeeded | cooldown | failed`, die drei letzten allein aus dem N1-Fallback.
- Operation: REMOVE-/ADD-Mutationen wie Delete-/Restore-Dienst (`removeEmote`, `addEmote` — hier
  **immer** mit gesetztem `$alias`, E21), `transportLossIsUnknown: true`, `abortOn` wie der Import;
  vor jedem REMOVE-Versuch die Zeilen-Prüfung nach E19 (eigener vollständiger Read, kein
  Wiederverwenden, Zähler für aufeinanderfolgende Read-Fehler, Abbruch der `full`-Zeilen nach
  dreien). Wie die Prüfung in die Engine kommt (ein
  `beforeStep`-Hook der Operation, oder der Dienst reicht je Zeile eine Vorbedingung mit), ist
  Plansache — die Engine läuft eine Zeile heute in `runRowFrom` ohne Hook
  (`seven-tv-run-engine.ts:389-406`).
- Registrierung beim Arbiter aus #256 Punkt 1 (11.1): der Dienst exponiert `isRunning`,
  `isSettling` (Nachlesen läuft **oder** eine der beiden Meldungen hat noch keinen Endzustand
  `succeeded | partial | failed`) und `destructiveOpen` (mindestens ein offener Lauf mit
  `full`-Zeile) als Signale; ein interner Satz „offener Läufe", aus dem ein Lauf erst fällt, wenn
  Settling und beide Meldungen im Endzustand sind oder es für ihn keine Meldung gibt; `reset()` und
  ein neuer Lauf leeren ihn nicht (E22). Keine eigene Start-Sperre im Dienst — der Arbiter ist die
  einzige. **Lauf-gebundener Abschluss (F20, 11.1 Punkt 6):** Nachlesen, beide Meldungen und der
  Endzustand hängen am Laufdatensatz, nicht an `run()`; ein `reset()` während des Laufs oder des
  Settlings löst nur die Anzeige, der Lauf läuft in seinem Datensatz zu Ende, seine Meldungen gehen
  raus, und erst ihr Endzustand nimmt ihn aus dem Satz offener Läufe — nie ein Frühausstieg wie
  heute in `onRunComplete` des Imports. Zeilenstatus `partial` (E23) setzt der Dienst beim Settle
  **nur** für `addOnly`-Zeilen, die sonst `done` wären und `omittedEntries` tragen; `failed`,
  `unknown` und `cancelled` bleiben stehen, `omittedEntries` daneben (F19).
- Was der Plan als gemeinsamen Baustein aus dem Import-Dienst herauszieht (Settling mit Re-Read,
  `sendFollowUp` mit zwei Meldungen, `destructiveRunActive`, Protokollgatter), ist Plansache; der
  Vertrag ist das Verhalten in Abschnitt 4. Kopieren ist erlaubt, wenn Extrahieren #256 vorgreift —
  dann mit Verweis, damit #256 beide Stellen sieht.
- Arbiter: der Undo ist der vierte Lauf-Typ (`'undo'`) des Arbiters aus #256 Punkt 1 (11.1);
  `usageStatsLeaveGuard` fragt `importService.isRunning() || undoService.isRunning()` mit eigener
  Textfamilie `undo.leaveWhileRunning.*`; `ImportTrigger` sperrt, solange der Arbiter belegt ist
  (unverändert in der Form, sieht jetzt vier Dienste in zwei Zuständen). Der Guard hängt nach
  #264 an `canDeactivate` in `features/usage-stats/usage-stats.routes.ts` und wird **nur** dort
  referenziert — so gerät `SevenTvUndoService` wie der Import-Dienst nicht ins Initial-Bundle (F9);
  der Undo-Dienst wird aus keiner eagerly geladenen Datei importiert.

### 6.6 Dock und Seite

- `UndoProgressSection` (`shared/seven-tv/undo-progress-section.ts`) nach 4.7; in
  `usage-stats-page.html` neben `app-import-progress-section` und `app-restore-progress-section`
  außerhalb des Set-Gates; `dockVisible()` zählt `undoShown`/`undoNoticePending`; die
  Vote-Session-Detailseite bindet sie **nicht** ein (dort gibt es keine Datei-Tür).
- `usage-stats-page.ts`: Settle-Effekt nach N2 auch für den Undo-Lauf; `resetIfChannelChanged`
  aus dem `channel-workspace-layout` wie bei den drei anderen Diensten.

### 6.7 Locale-Familien (Wortlaut vorläufig, #255)

`restore.import.choice.*` (Weiche), `restore.import.sorts.transferUndo`, `restore.import.errors.{transferUndo,
transferUndoNoRows}`, `undo.confirm.*` (Titel, Zeilen-Modi, Gründe, Löschzahl, Slot, Aktionszeile,
Banner), `undo.summary.*` (Zähler, `removed`, `restored`, `gaps`, `gapsHint`, `unknownRows`,
`unknownRecordedIn`, übersprungen je Grund, `downloadProtocol`, `protocolNotSaved`,
`insufficientPrivileges`), `undo.removalSync*`, `undo.restoreSync*`, `undo.resync.*`, `undo.settling`,
`undo.leaveWhileRunning.*`, `undo.errors.{removedButNotRestored, cancelledMidRow}`; das
`RunProgressPanel` bildet `undo.summary.counts` u. a. über `labelPrefix`. Bestehende Familien
`syncReportReason.*` und `massDelete.errors.*` (Engine) werden geteilt.

---

## 7. Grenzfälle

| Fall | Verhalten |
|---|---|
| **Ergebnisprotokoll eines vollständig geglückten Laufs** (der Normalfall) | Alle Replace-Zeilen `done`/`confirmed`; Live: Quelle hält exakt den Alias, Ziel fehlt ⇒ alle `full`; Dialog, Rückweg-Datei, Lauf; danach Restore aus derselben Übertragungsdatei ⇒ alles vorhanden, nichts passiert |
| **Rückweg-Datei (`planned`) nach Tab-Tod mitten im Lauf** | Kandidaten = alle Replace-Zeilen; Live sortiert: gelaufene ⇒ `full`; bei Schritt 1 gescheiterte ⇒ `addOnly`; nie gelaufene ⇒ `nothingToDo`; von `recheckTransferPlan` verworfene ⇒ `nothingToDo` oder `sourceUnderOtherName`, je nachdem, was seither geschah |
| **Rückweg-Datei eines nie gestarteten Laufs** | Jede Zeile `nothingToDo` ⇒ Notiz, kein Dialog, kein Lauf (E18) |
| **Dieselbe Datei zweimal** | Zweites Mal `nothingToDo` für jede Zeile (E18); dasselbe für `planned` nach `finished` derselben Übertragung (F6) |
| **Quelle hat einen zweiten Alias bekommen** | `sourceHasMoreEntries`, Zeile übersprungen mit Live-Gegenstück, nichts berührt (F2) |
| **Quelle umbenannt** | `sourceUnderOtherName`, übersprungen; das Ziel bleibt fehlend — der Nutzer entscheidet im 7TV-Web oder schließt die Ziel-Lücke per Restore (dort gilt Regel 4 nicht mehr, der Name ist frei) |
| **Quelle schon von Hand entfernt** | `addOnly`, nur die ADDs; keine Löschung, keine Rückweg-Datei, wenn es die einzige Zeilenart ist (4.2 Nr. 8) |
| **Ziel-Eintrag inzwischen von Hand unter einem seiner alten Aliase zurückgeholt** | Eintrag `alreadyPresent`; hält die Quelle den kollidierenden Alias noch, bleibt die Zeile `full` mit den übrigen fehlenden Einträgen — F11 garantiert mindestens einen |
| **Ziel inzwischen von Hand unter einem neuen Namen `C` zurückgeholt, Quelle hält A** (Codex-Befund 3) | `full` ⇒ übersprungen `targetHasForeignEntries` (E20), Live-Gegenstück zeigt `C`, nichts berührt — ein REMOVE setzt nicht auf einem Ziel auf, dessen Zustand die Datei nicht beschreibt (F14) |
| **Ziel unter `C` zurückgeholt, Quelle schon weg** (Runde 2 Befund 3) | `addOnly` ⇒ **läuft**: ADDs der fehlenden Einträge aus `N` neben `C`, `C` bleibt; Vermerk `targetHasForeignEntries` in Dialog, Dock (`foreignNotedRows`) und Protokoll (`notes`) |
| **`C` kommt mitten in der `full`-Zeile dazu** (nach REMOVE S und ADD A, vor ADD B; Runde 2 Befund 3) | ADD B scheitert (409, wenn `C = B`) oder gelingt (wenn `C ≠ B`, denn ein ADD prüft keine fremden Einträge). Scheitert er: `failed@2`, Lücke; **Undo erneut** ⇒ Quelle weg ⇒ `addOnly` ⇒ läuft neben `C` (Zeile oben) und holt B, oder lässt B als `targetNameTaken` aus, wenn `C` den Namen B hält (dann `partial`, Name freimachen, erneut). Der Reparaturweg aus E9 bleibt damit in jeder Lage ausführbar |
| **Zweiter Lauf nach zurückgeholtem `null`-Eintrag** (Runde 2 Befund 2) | T steht unter dem benannten `D`; Normalisierung macht `D` zum eigenen Eintrag (`N ∋ D`) ⇒ `alreadyPresent`, kein `targetHasForeignEntries`; die Zeile ist `nothingToDo` (E18) — oder `addOnly` für andere noch fehlende Einträge |
| **Duplikat-Zelle (#74): Ziel hatte zwei Aliase und einen aliaslosen Eintrag** | `full` mit drei ADDs, `stepCount` 4; Slot-Delta +2; der aliaslose kommt als `ADD { alias: D }` zurück (E21); `restoredTarget.entries` mit drei `added`-Flags |
| **Ziel-Alias inzwischen von Dritten belegt, `full`-Zeile** (Codex-Befund 6) | Ganze Zeile übersprungen `targetNameTaken` (E8), Quelle bleibt; Dock nennt den belegten Namen; nach Freimachen des Namens Undo erneut |
| **Ziel-Alias inzwischen von Dritten belegt, `addOnly`-Zeile** | Zeile läuft ohne den Eintrag; sind alle gesendeten ADDs bestätigt, endet sie `partial`, `omittedEntries` in Datei und Dock (E23); Undo erneut, sobald der Name frei ist |
| **`addOnly`-Zeile mit Auslassung, deren gesendeter ADD `unknown` bleibt** (Runde 2 Befund 4) | Status bleibt `unknown` (in `unknownCount`, Dock-Hinweis „bei 7TV prüfen"), `omittedEntries` daneben (in `omittedEntryCount`), **nicht** in `partialRows` (F19); dasselbe für `failed`: Status `failed`, Auslassung daneben |
| **`null`-Eintrag ohne `defaultName` in der Datei** (Codex-Befund 4) | `full` ⇒ Zeile übersprungen `targetNameUnverifiable`; `addOnly` ⇒ Eintrag ausgelassen (`omittedEntries`); kein ADD ohne Alias, nie (E21) |
| **`defaultName` des Ziels hat sich seit dem Replace geändert** | Der Undo schreibt den Namen aus der Datei (den das Ziel damals trug), nicht 7TVs heutigen Standardnamen — der Zustand vor der Übertragung (E21); die Namensfreiheit ist gegen genau diesen Namen geprüft |
| **Kollidierender Alias = `defaultName` des Ziels** | REMOVE der Quelle gibt den Namen frei, `ADD { alias: D }` landet darunter (E21); die Zeile ist ein gewöhnliches `full` |
| **Eine spätere Quelle bekommt während des Laufs einen zweiten Alias** (Codex-Befund 1, Runde 2 Befund 1) | Die Zeilen-Prüfung vor ihrem REMOVE (E19) liest **neu** — auch 275 ms nach dem vorigen REMOVE — und sieht `sourceHasMoreEntries` ⇒ `skippedDrift`, nichts berührt; der Lauf geht weiter. Offen bleibt nur die Latenz zwischen diesem Read und dem REMOVE (F13, benannter Rest) |
| **REMOVE nach Rate-Limit-Pause wiederholt** | Vor der Wiederholung ein neuer Read (E19); hat sich die Zeile in der Pause verändert ⇒ `skippedDrift` statt Wiederholung |
| **Read vor einem REMOVE scheitert** (429, Netz, `complete: false`) | Zeile `skippedDrift`/`recheckUnavailable`, nichts berührt; nach drei Fehlern in Folge verbleibende `full`-Zeilen `cancelled` mit Grund, `addOnly`-Zeilen laufen zu Ende |
| **`planned`-Datei eines nie gestarteten Laufs, Zustand später von Hand nachgebildet** (Codex-Befund 2) | Live-Form `full`, `provenance: 'unproven'`: Zeile gekennzeichnet, ohne Datei-Bestätigung `skippedUnproven`, mit Bestätigung läuft sie (Betreiberentscheidung Abschnitt 15 A, F17) |
| **Zweiter Undo, während der erste noch settelt oder meldet** (Codex-Befund 5) | Der Arbiter (#256 Punkt 1, 11.1) meldet „belegt": kein Start, transiente Notiz des Arbiters; `beforeunload` bleibt bis zum Endzustand beider Meldungen des ersten Laufs; dasselbe gegen einen settelnden Import-Lauf, und ein Import, Delete oder Restore startet nicht gegen einen settelnden Undo |
| **`addOnly`-Zeile, erster ADD `unknown`** (Codex-Befund 7) | Nachlesen prüft den ADD (`n` auf T vorhanden?) — nie ein REMOVE; die Quelle gerät in keine Löschmeldung (E24) |
| **REMOVE ok, ADD scheitert (409, Slot, Netz)** | `failed@1`, `completedSteps 1`, Quelle in `sync-deleted`, Ziel nicht in `sync-restored`; Dock `gapCount` + Hinweis; **derselbe Undo erneut** schließt die Lücke als `addOnly` (E9, E18); der Restore aus der Übertragungsdatei täte es nur ohne fremde Ziel-Einträge (F14) |
| **REMOVE `unknown`, Nachlesen: Quelle weg** | `failed@1` mit `removedButNotRestored`; Quelle gemeldet; Lücke wie oben |
| **REMOVE `unknown`, unklärbar** | `unknown` im Protokoll, nirgends gemeldet, Dock-Hinweis „bei 7TV prüfen" + „aufgezeichnet in der Undo-Rückweg-Datei" (4.6) |
| **ADD `unknown`, Nachlesen: Eintrag da** | Schritt bestätigt; Folge-ADDs liefen nicht ⇒ `failed` mit Lückengrund, sofern welche ausstanden; Ziel in `sync-restored` |
| **`cancel()` zwischen REMOVE und erstem ADD** | `failed@1`, `cancelledMidRow`, Lücke, gemeldet; Rest `cancelled` |
| **Recht vor dem Lauf entzogen** | Vorprüfung im Datei-Schritt blockt (`targetNotEditable`), keine Weiche |
| **Recht mitten im Lauf entzogen** | `LACKING_PRIVILEGES` ⇒ Abbruch, Token weg; bestätigte Schritte gemeldet; 403 der Meldung ⇒ `failed`/`forbidden` (4.9 Nr. 23) |
| **Set gelöscht zwischen Vorprüfung und Lauf** | 4.9 Nr. 24 |
| **Ungetracktes Ziel** (olafs `test`) | Erlaubt (E4); beide Meldungen Papier ohne Kanal (N3), kein Resync, keine Resync-Zeile, auch kein Fallback (`expectedChannelName: null`); Slot-Vorschau über `twitchLogin` |
| **Nicht-aktives Set eines getrackten Kanals** (olafs `test`, wenn `tttt` aktiv) | Papier-Einträge mit Besitzerkanal (N3); **kein** Client-Resync und keine Resync-Zeile (E16, #255-Regel); Mitgliederliste per N2 beim Settle bzw. Vormerkung |
| **Aktives Set des Seitenkanals** | Kanal-Einträge, `channel.synced`, Backend-Resync unter Cooldown (Dock `backendTriggered`), Raster folgt; scheitern beide Meldungen endgültig ⇒ N1-Fallback des Kanals |
| **Zwei Läufe gleichzeitig** | Arbiter (`'undo'`, 11.1) blockt vor Dialog und vor Start; ein Delete/Restore/Import während eines laufenden **oder settelnden** Undo startet nicht (F9, F16) |
| **Kanalwechsel während des Laufs** | Guard fragt (E15); fertiger Lauf verschwindet aus dem Dock beim Wechsel wie bei den anderen |
| **Rate-Limit der Vorprüfung** | `targetCheckUnavailable` im Datei-Schritt, keine Weiche (#253 F3) |
| **Datei mit zwei Zeilen auf dieselbe Quell- oder Ziel-ID** (manipuliert) | beide `duplicateInFile` (4.3 Schritt 4) |
| **`transfer-undo`-Datei als Undo-Eingabe** | `wrongKind` mit Grund (F7) |
| **`transfer-undo`-Datei als Restore-Eingabe nach geglücktem Undo** | Jede Quelle: Name vom Ziel gehalten ⇒ Regel 4 „Name belegt", nichts passiert (E12) |
| **`transfer-undo`-Datei als Restore-Eingabe nach gescheiterter Zeile** (Quelle weg, Ziel nicht zurück) | Name frei ⇒ Quelle kommt unter ihrem Alias zurück — der Nutzer hat damit den Replace-Stand wieder; alternativ Restore aus der Übertragungsdatei für den Vor-Replace-Stand. Beides sichtbar im jeweiligen Bestätigungsdialog (Namen), keine Datei mit zwei Bedeutungen |
| **Alter Build liest eine `transfer-undo`-Datei** | `wrongKind` mit Grund; als JSON lesbar (Abschnitt 12) |

---

## 8. Akzeptanzkriterien

Jedes Kriterium ist so formuliert, dass ein Test oder ein Handgriff es entscheidet.

1. Eine `transfer-run`-Datei (beide Stufen) endet im Datei-Schritt nach bestandener Vorprüfung mit
   der Weiche; „Lücken schließen" führt in den heutigen Restore-Flow (`picked.kind === 'restore'`),
   „Ersetzungen rückgängig machen" in `startUndoFlow` (`picked.kind === 'transfer-undo'`). Ein
   Purge-Protokoll zeigt keine Weiche.
2. Eine Datei, deren Set die Vorprüfung nicht besteht (`notEditable`, `notSelectable`,
   `unavailable`), zeigt den Fehler im Banner und keine Weiche; kein Request an 7TV.
3. `parseTransferRunForUndo` liefert für `planned` jede Replace-Zeile, für `finished` nur
   `removedTarget.confirmed === true`, mit Quell-ID, Alias, `sourceName`, Ziel-ID, `entries`,
   `defaultName`; keine Zeile ⇒ `transferRunNoRows`.
4. `classifyUndoRows` entscheidet nach der Tabelle in 4.3 — je Tabellenzeile ein Fall: exakt ein
   Eintrag ⇒ `full`; Quelle leer + fehlende Einträge ⇒ `addOnly`; Quelle leer + nichts fehlt ⇒
   `nothingToDo`; zweiter Alias ⇒ `sourceHasMoreEntries`; anderer Name ⇒ `sourceUnderOtherName`;
   aliasloser Eintrag der Quelle ⇒ `sourceHasMoreEntries`; Groß-/Kleinschreibung ⇒
   `sourceUnderOtherName`; Ziel mit fremdem Alias oder fremdem aliaslosen Eintrag ⇒
   `targetHasForeignEntries` (auch bei `addOnly`); `null`-Ziel-Eintrag vorhanden über `aliaslessIds`
   **oder** über `defaultName`-Alias ⇒ `alreadyPresent`; `null`-Eintrag ohne `defaultName` ⇒
   `targetNameUnverifiable` (Zeile bei `full`, Eintrag bei `addOnly`); Name von dritter ID ⇒
   `targetNameTaken` (Zeile bei `full`, `omittedEntries` bei `addOnly`); Name von der Quelle ⇒ ADD
   mit explizitem Alias; jeder ADD trägt einen nicht-leeren Alias; doppelte Quell- oder Ziel-ID ⇒
   `duplicateInFile`; `planned` ⇒ `provenance: 'unproven'`, `finished` ⇒ `'confirmed'`.
5. Der Bestätigungsdialog öffnet nur, wenn mindestens eine `full`- oder `addOnly`-Zeile existiert;
   sonst eine transiente Notiz mit den Übersprungen-Gründen und kein Request an 7TV außer dem einen
   Read.
6. Mit mindestens einer `full`-Zeile zeigt die Aktionszeile „Rückweg sichern"; der Klick lädt eine
   `transfer-undo`-Datei der Stufe `planned` herunter, die genau die laufenden Zeilen mit
   `removedSource.entries = [{ alias }]` aus dem Live-Read und `restoredTarget.entries` = die ADDs
   trägt; erst danach „Starten"; ohne Download kein Start; ein Neuladen setzt auf „Rückweg sichern"
   zurück. Ohne `full`-Zeile zeigt die Aktionszeile direkt „Starten", und es entsteht keine Datei.
7. Ein Lesefehler oder `complete: false` beim Öffnen gibt nichts frei und bietet „Ziel neu laden".
8. Der Frischcheck vor dem Lauf lässt eine Zeile mit geänderter Klassifikation (Modus oder ADD-Liste)
   nicht laufen (`skippedDrift`, gezählt, im Dock genannt, im Protokoll `cancelled` mit Grund); bei
   `available: false`/`complete: false` läuft keine `full`-Zeile, `addOnly`-Zeilen laufen.
9. Der Lauf sendet je `full`-Zeile zuerst `removeEmote(S)`, dann je fehlendem Eintrag
   `addEmote(T, alias)` — immer mit nicht-leerem Alias, für `null`-Einträge der Datei-`defaultName`
   (E21) —, im Engine-Takt; je `addOnly`-Zeile nur die ADDs; niemals einen ADD vor dem REMOVE
   derselben Zeile.
10. HTTP 0 und jede 5xx auf einem Undo-Schritt ergeben `unknown`; 401/403/`LACKING_PRIVILEGES`
    brechen den Lauf ab und löschen das Token; jede GraphQL-Ablehnung ist `failed`.
11. Nach einem `unknown` läuft genau ein Re-Read (20 s), der nach der Tabelle in 4.6 klärt;
    `completedSteps` wird nie gesenkt; unklärbare Zeilen bleiben `unknown`, stehen in keiner Meldung,
    und das Dock nennt die Undo-Rückweg-Datei als Aufzeichnungsort.
12. Nach `settled` geht — sofern nicht leer — zuerst `POST /api/seventv/emote-sets/{set}/sync-deleted`
    mit den Quell-IDs aller `full`-Zeilen mit `completedSteps >= 1`, dann `…/sync-restored` mit den
    Ziel-IDs aller Zeilen mit mindestens einem bestätigten ADD; beide mit `expectedChannelName` nach
    #253 (getrackter Kanal bei aktivem Set, sonst `null`); IDs dedupliziert; kein Request an eine
    kanalgebundene Route.
13. Beide Meldungen werden nach #253 6.4 klassifiziert (`succeeded | partial | failed` mit Grund),
    jede mit eigener Dock-Zeile; „Erneut melden" je Meldung bei `failed` und `partial`/`shortfall`,
    nicht bei `channelMismatch`; der Dienst weist den Retry bei `channelMismatch` ab.
14. Im Erfolgsfall stößt der Client **keinen** `POST /api/channels/{c}/resync` an — für kein Ziel,
    auch nicht für ein nicht-aktives Set eines getrackten Kanals; nennt eine der beiden Antworten den
    Kanal in `resyncTriggered`, zeigt das Dock `backendTriggered`. Scheitern **beide** Meldungen
    endgültig, läuft genau ein N1-Fallback für `expectedChannelName`; ist er `null` (nicht-aktives
    oder ungetracktes Ziel), keiner; scheitert nur eine Meldung, keiner.
15. `beforeunload` ist registriert genau, solange ein Undo-Lauf mit mindestens einer `full`-Zeile
    läuft, settelt oder eine seiner beiden Meldungen keinen Endzustand hat; für einen reinen
    `addOnly`-Lauf nie; danach nie. Der `canDeactivate`-Guard der Nutzungsseite fragt bei laufendem
    Undo.
16. Der Arbiter aus #256 Punkt 1 kennt den Undo als vierten Lauf-Typ (`'undo'`): während ein Undo
    läuft oder settelt, starten Delete, Restore, Import und ein zweiter Undo nicht, und der
    Import-Trigger ist gesperrt; umgekehrt startet kein Undo, während ein anderer Dienst läuft
    oder settelt.
17. Eine `full`-Zeile mit `completedSteps >= 1` und `status: 'failed'` zählt als Lücke; das Dock zeigt
    `gapCount` mit dem Hinweis, den Undo aus derselben Datei zu wiederholen; der zweite Lauf
    klassifiziert die Zeile als `addOnly` und sendet genau die ADDs der fehlenden Ziel-Einträge —
    auch dann, wenn das Ziel inzwischen unter einem seiner alten Aliase steht (kein fremder Eintrag).
18. Das Ergebnisprotokoll (`finished`) ist ab `settled` herunterladbar (JSON/CSV), trägt alle Zeilen
    ungefiltert mit `mode`, `status`, `failedStep`, `completedSteps`, `removedSource.confirmed`,
    `restoredTarget.entries[].added`, `skippedReason`, und `meta.undoneFile` mit Stufe, Zeitstempel und
    `origin` der Übertragungsdatei; der stille Hinweis `protocolNotSaved` steht bis zum Download.
19. `parseImportSource` weist `transfer-undo` namentlich ab; `parseTransferRunForUndo` weist
    `transfer-undo` mit `wrongKind` ab; `parseTransferUndoForRestore` liefert für `planned` jede
    `full`-Zeile, für `finished` nur `removedSource.confirmed === true`, als `RestoreRow { S,
    sourceName, [alias] }`; keine Zeile ⇒ `transferUndoNoRows`.
20. Ein Restore aus einer `transfer-undo`-Datei nach geglücktem Undo überspringt jede Zeile mit
    „Name belegt" und sendet nichts; nach einer gescheiterten `full`-Zeile (Quelle weg, Ziel nicht
    zurück) sendet er genau den ADD der Quelle unter ihrem Alias.
21. Dieselbe Übertragungsdatei ein zweites Mal als Undo ⇒ jede Zeile `nothingToDo`, Notiz, kein
    Dialog, kein Lauf, keine Datei.
22. Ein ungetracktes Ziel-Set durchläuft Weiche, Dialog, Rückweg-Datei und Lauf wie ein getracktes;
    beide Meldungen antworten 200 mit `channels: []`, `unresolvedChannel: null`; kein Resync-Request;
    das Audit (globale Ansicht) zeigt je Meldung einen Eintrag ohne Kanal „für <ownerLogin>".
23. Bestehende `transfer-run`-Dateien werden vom Restore-Parser zeilen-identisch gelesen wie vor
    dieser Spec (`transfer-run-export.spec.ts` bleibt grün); `purge-run`-Dateien sind unberührt.
24. Kein Backend-Diff außer Tests, sofern der Plan welche für die Doppelmeldung ergänzt; `dotnet
    test` grün ohne neue Endpunkte, Services oder `AuditActions`.
25. Alle vier Gates des Repos grün (`dotnet test`, Vitest, E2E, `coverage-local`), plus die
    Live-Verifikation aus Abschnitt 9.5 mit Zahlen im PR-Text.
26. **(Codex 1, Runde 2 Befund 1)** Bekommt die Quelle einer späteren `full`-Zeile während des
    Laufs einen zweiten Alias (Stub ändert den Set-Stand unmittelbar nach dem REMOVE der ersten
    Zeile, also innerhalb der 275 ms bis zur nächsten), geht vor dem REMOVE der zweiten Zeile ein
    **neuer** Read (der Stub zählt je REMOVE-Versuch genau einen Set-Read, nie null), die
    Zeilen-Prüfung sieht `sourceHasMoreEntries`, sendet für sie **kein** REMOVE und keinen ADD,
    zählt `skippedDrift` und läuft mit der nächsten Zeile weiter; ein REMOVE, das die Engine nach
    einer Rate-Limit-Pause wiederholt, bekommt vor der Wiederholung ebenfalls einen neuen Read.
27. **(Codex 1)** Scheitert der Read vor einem REMOVE, läuft die Zeile nicht (`recheckUnavailable`);
    nach drei Read-Fehlern in Folge enden alle verbleibenden `full`-Zeilen `cancelled` mit Grund,
    `addOnly`-Zeilen laufen zu Ende; das Ergebnisprotokoll trägt je `full`-Zeile die Quell-Einträge
    des letzten Reads vor ihrem REMOVE.
28. **(Codex 2)** Kandidaten aus einer `planned`-Datei tragen `provenance: 'unproven'`, aus einer
    `finished`-Datei `'confirmed'`; im Dialog sind unbelegte `full`-Zeilen gekennzeichnet, und ohne
    die Datei-Bestätigung ist „Rückweg sichern" gesperrt und jede unbelegte `full`-Zeile
    `skippedUnproven`; mit Bestätigung läuft sie. Gegenbeispiel als Test: `planned`-Datei eines nie
    gestarteten Laufs, Live-Stand von Hand nachgebildet (T weg, S unter A) ⇒ ohne Bestätigung kein
    REMOVE. (Betreiberentscheidung Abschnitt 15 A.)
29. **(Codex 3, Runde 2 Befund 3)** Trägt das Ziel im Live-Set einen Alias außerhalb der
    normalisierten Namen `N` oder einen aliaslosen Eintrag, den die Datei nicht nennt, ist eine
    `full`-Zeile `targetHasForeignEntries` — im Dialog, im Frischcheck und vor dem REMOVE; kein
    Request an 7TV für diese Zeile (Test: T mit alten Aliasen A/B, von Hand unter C zurückgeholt, S
    hält A ⇒ übersprungen, S bleibt). Eine `addOnly`-Zeile in derselben Lage **läuft** mit genau den
    fehlenden ADDs aus `N`, sendet kein REMOVE, lässt C stehen und trägt `notes:
    ['targetHasForeignEntries']` in Protokoll und Dock (Test: S weg, T unter C, A/B fehlen ⇒
    `addEmote(T, A)`, `addEmote(T, B)`, C danach noch da).
30. **(Codex 3)** Nach einem Undo-Lauf, dessen `full`-Zeile bei `ADD B` scheiterte (S weg, A da, B
    fehlt), klassifiziert ein zweiter Undo aus derselben Übertragungsdatei die Zeile als `addOnly`
    mit genau `[B]` und schließt die Lücke; ein Restore aus der Undo-Rückweg-Datei überspringt S
    mit „Name belegt" (T hält A).
31. **(Codex 4)** Jeder ADD des Undo trägt einen nicht-leeren Alias; ein `null`-Eintrag wird mit
    dem `defaultName` der Datei gesendet, und die Namensfreiheit ist gegen denselben Namen geprüft;
    ein `null`-Eintrag ohne `defaultName` macht eine `full`-Zeile zu `targetNameUnverifiable` und
    fällt bei `addOnly` in `omittedEntries`; ein Ziel, dessen 7TV-Standardname sich seit der Datei
    geändert hat, kommt unter dem Namen der Datei zurück.
32. **(Codex 5)** Der Arbiter aus #256 Punkt 1 meldet „belegt", solange der Undo-Dienst
    `isSettling` liefert (Nachlesen oder Meldungen ohne Endzustand), und solange ein anderer
    Dienst settelt: in beiden Fällen startet kein Undo (Notiz, kein Dialog, kein Request), und kein
    Import, Delete oder Restore startet gegen einen settelnden Undo; `beforeunload` bleibt
    registriert, bis beide Meldungen des Undo-Laufs einen Endzustand haben — auch wenn inzwischen
    `reset()` gerufen oder ein neuer Lauf gezeigt wurde. Der Undo-Flow und der Import-Flow
    enthalten **keine** eigene Settling-Prüfung (`grep` findet keine `settlement`-Abfrage außerhalb
    des Arbiters).
33. **(Codex 6)** Eine `full`-Zeile, deren Ziel-Einträge A und B sind, S hält A, ein Dritter hält
    B ⇒ ganze Zeile `targetNameTaken`, S bleibt, kein Request. Eine `addOnly`-Zeile in derselben Lage
    ⇒ `ADD A`, Ende `partial`, `omittedEntries: [{ B, targetNameTaken }]` im Ergebnisprotokoll, Dock
    `partialRows 1`, `omittedEntryCount 1`.
34. **(Codex 7)** Eine `addOnly`-Zeile, deren erster ADD (Schritt 0) `unknown` bleibt, wird beim
    Nachlesen als ADD geklärt (`n` auf T ⇒ `completedSteps 1`; fehlt ⇒ `failed@0`; unlesbar ⇒
    `unknown`) und erscheint in keiner `sync-deleted`-Meldung — auch wenn die Quelle im Set fehlt.
35. **(Runde 2 Befund 2)** Nach einem Undo, der einen `null`-Eintrag als `ADD { alias: D }`
    zurückgeholt hat, klassifiziert ein zweiter Lauf derselben Datei die Zeile **nicht** als
    `targetHasForeignEntries`: `D` ist `alreadyPresent`, die Zeile `nothingToDo` (oder `addOnly`
    für andere fehlende Einträge). `normalizeTargetEntries` liefert für `{ alias: null }` mit
    `defaultName: 'D'` den Namen `D`, für `defaultName: null` `unverifiable`.
36. **(Runde 2 Befund 3)** Eine `full`-Zeile, die nach REMOVE S und `ADD A` an `ADD B` scheitert,
    nachdem T den fremden Alias C bekam (Stub fügt C nach ADD A ein), endet `failed@2`; ein zweiter
    Undo aus derselben Datei klassifiziert sie als `addOnly` mit `notes: ['targetHasForeignEntries']`
    und sendet genau `addEmote(T, B)` (oder lässt B als `targetNameTaken` aus, wenn C den Namen B
    hält), C bleibt im Set.
37. **(Runde 2 Befund 4)** Eine `addOnly`-Zeile mit einem ausgelassenen Eintrag (`targetNameTaken`)
    und einem gesendeten ADD, der `unknown` bleibt (Stub: HTTP 503, Nachlesen scheitert), endet
    `unknown`, zählt in `unknownCount` und `omittedEntryCount`, **nicht** in `partialRows`, trägt
    `omittedEntries` im Protokoll und den Dock-Hinweis „bei 7TV prüfen". Dieselbe Zeile mit einem
    `failed`-ADD (409) endet `failed` mit `omittedEntries`; nur eine Zeile, deren gesendete ADDs
    alle bestätigt sind, endet `partial`.
38. **(Runde 2 Befund 1)** Ein Lauf mit N `full`-Zeilen ohne Rate-Limit-Pause sendet genau N
    Set-Reads zwischen Start und Ende (einen je REMOVE), zusätzlich zum Frischcheck; der Stub zählt
    sie, und kein REMOVE geht ohne einen unmittelbar vorausgehenden Read.
39. **(Runde 2 Befund 5)** Ein `reset()` des Undo-Dienstes während des Laufs, während des
    Nachlesens oder während eine Meldung unterwegs ist, ändert nur `run()`; der Lauf läuft in seinem
    Datensatz zu Ende, beide Meldungen werden gesendet (Stub sieht `sync-deleted` und
    `sync-restored`), ihr Endzustand landet im Laufdatensatz, und erst dann fallen `isSettling` und
    `destructiveOpen` — der Arbiter wird frei, ohne dass eine Meldung fehlt.
40. **(Runde 2 Befund 2)** Die E2E-Erwartung für den aliaslosen Ziel-Eintrag ist `addEmote(T2, D)`
    mit dem `defaultName` aus der Datei; kein `addEmote` des Undo trägt `alias: null`.

---

## 9. Testpyramide

### 9.1 Backend

Keine neuen Fälle nötig; bestehende Tests aus #253 9.1/9.2 decken beide Routen. Optional (Plan):
ein Api-Test, der `sync-deleted` und `sync-restored` nacheinander auf dasselbe Set mit demselben
Kanal schickt und **einen** `TryBeginAsync`-Erfolg sieht (F8, #253 AK 27 — bereits gedeckt, hier nur
als Undo-Reihenfolge benannt).

### 9.2 Vitest — Parser und Datei

- `transfer-run-export.spec.ts` **+5**: `parseTransferRunForUndo` (`planned` alle, `finished` nur
  `confirmed`, `unknown` mit `confirmed: true` bleibt Kandidat, `failed@0` fällt, keine Zeile ⇒ Fehler);
  die bestehenden Restore-Parser-Fälle bleiben unverändert (AK 23).
- `transfer-undo-export.spec.ts` (neu) **+10**: `planned` aus Plan + Read (nur laufende Zeilen,
  `removedSource.entries` aus dem Read, `addOnly` ohne `removedSource`), `finished` aus einem
  gemischten Lauf (`done`, `failed@1`, `unknown`, `cancelled`, Drift), `confirmed`/`added`-Ableitung
  aus `completedSteps`, Duplikat-Zelle mit drei Einträgen, CSV-Spalten, beide Dateinamen (getrackt/
  ungetrackt), `meta.undoneFile`, `parseTransferUndoForRestore` (beide Stufen, keine Zeile),
  `wrongKind` für `transfer-undo` als Undo-Eingabe.
- `import-source-parser.spec.ts` **+1**, `purge-run-export.spec.ts` **+0**, `read-envelope`/`export-envelope`
  **+1** (neue Sorte).

### 9.3 Vitest — Klassifikation, Flow, Dialog, Dienst

- `undo-plan.spec.ts` (neu): jede Zeile der Tabelle 4.3 (AK 4), `diffUndoPlans` (gleicher Modus +
  gleiche ADDs ⇒ läuft; Moduswechsel ⇒ Drift; ADD-Liste geändert ⇒ Drift), `summarizeUndoPlan`
  (Slot-Delta für Einzel- und Duplikat-Zelle); die Codex-Gegenbeispiele: fremder Alias `C` auf T
  für `full` (übersprungen) und `addOnly` (läuft mit `notes`) (AK 29), `null` ohne `defaultName`
  und geänderter Standardname (AK 31), A/B mit Dritt-Inhaber für `full` und `addOnly` (AK 33),
  nie gestartete `planned`-Datei mit nachgebildetem Stand (AK 28), `normalizeTargetEntries` und
  der zweite Lauf nach zurückgeholtem `D` (AK 35), `sameClassification` für jede Kombination,
  `notes` ohne Einfluss auf `sameClassification`.
- `file-import-step.spec.ts` **+4**: Weiche für `transfer-run` beider Stufen, keine Weiche für
  `purge-run` und `transfer-undo`, Vorprüfungsfehler vor der Weiche, `picked.kind` je Wahl (AK 1, 2).
- `undo-flow.spec.ts` (neu): Reihenfolge Arbiter → Token → Dialog → Arbiter → Frischcheck → Start;
  alles übersprungen ⇒ Notiz, kein Dialog; Frischcheck-Drift ⇒ `skippedDrift`; `available: false`
  ⇒ keine `full`-Zeile (AK 5, 8).
- `undo-confirm-dialog.spec.ts` (neu): Zustandsmaschine (Read gesperrt → `idle` → `verifying` →
  `saved` → Neuladen ⇒ `idle`), Rückweg-Datei nur mit `full`-Zeile, Download-Fehler bleibt `idle`,
  Lesefehler gibt nichts frei, Rückgabe = gestempelter Plan, Dialog-Rückgaben und Sperrgründe (AK 6, 7)
  — Verhalten, keine Vorlage (Regel 12).
- `seven-tv-undo.service.spec.ts` (neu): Schrittfolge je Modus (AK 9), `transportLossIsUnknown`
  und `abortOn` (AK 10), Settling mit Re-Read nach Tabelle 4.6 — je Modus und Operation, inklusive
  `addOnly` an Schritt 0 (AK 11, 34), Zeilen-Prüfung vor jedem REMOVE-Versuch mit Drift einer
  späteren Quelle 275 ms nach dem vorigen REMOVE, Read-Zählung je REMOVE, neuer Read vor einer
  Rate-Limit-Wiederholung, drei Read-Fehler (AK 26, 27, 38), `isSettling`/`destructiveOpen` über
  offene Läufe bis zum Endzustand beider Meldungen, auch nach `reset()` (AK 32), lauf-gebundener
  Abschluss: `reset()` während des Laufs, während des Nachlesens und während einer laufenden
  Meldung ⇒ beide Meldungen gehen trotzdem raus und der Lauf schließt (AK 39), `partial` nur statt
  `done`; `unknown`/`failed` mit `omittedEntries` bleiben (AK 33, 37), `addOnly` neben fremdem
  Eintrag nach mittendrin gescheiterter `full`-Zeile (AK 36), beide Meldungen in
  Reihenfolge mit richtigen ID-Mengen (AK 12), Dreiwertigkeit und N4-Retry (AK 13), kein
  Client-Resync im Erfolgsfall (nicht-aktives Ziel ⇒ kein Request, `backendTriggered` aus beiden
  Antworten) und N1-Fallback nur bei zwei endgültig gescheiterten Meldungen (AK 14),
  `destructiveRunActive`/`beforeunload` (AK 15), `resetIfChannelChanged`,
  `reset()` während des Nachlesens sendet trotzdem (Plan-230 §7).
- `seven-tv-run-arbiter.spec.ts` **+3** (auf dem Arbiter aus #256: Undo läuft ⇒ belegt; Undo
  settelt ⇒ belegt; Import settelt ⇒ Undo-Start abgewiesen), `usage-stats-leave.guard.spec.ts` **+1**,
  `import-trigger.spec.ts` (Fixtures um die dritte Art) (AK 16, 32); der Router-Spec aus #264
  (`leadsToSameRoute`) bleibt unverändert grün — der Undo fügt keine Route hinzu, und `ng build
  --stats-json` zeigt das Initial-Bundle ohne `seven-tv-undo.service` (F9).
- `undo-progress-section.spec.ts` (neu): Zähler, `gapCount` mit Hinweis, `unknownRecordedIn`,
  zwei Meldungszeilen mit/ohne Retry, Download ab `settled` (AK 17, 18) — Struktur, nicht Wortlaut.
- `usage-stats-page.spec.ts` **+2**: Section eingebunden, N2-Settle-Effekt für den Undo.
- `restore-flow.spec.ts`/`seven-tv-restore.service.spec.ts` **+2**: Restore aus `transfer-undo`
  (AK 20).

### 9.4 Playwright E2E (nur ohne Api auf `:5151`)

- Neu: Ergebnisprotokoll mit zwei `done`-Replace-Zeilen (eine mit Duplikat-Zelle) auf der
  Nutzungsseite → Weiche → „Ersetzungen rückgängig machen" → Token → Dialog zeigt zwei `full`-Zeilen,
  Löschzahl 2, Slot-Delta +1 → „Rückweg sichern" lädt `transfer-undo-plan` → „Starten" → 7TV-Stub
  sieht `removeEmote(S1)`, `addEmote(T1, a)`, `removeEmote(S2)`, `addEmote(T2, a)`, `addEmote(T2, b)`,
  `addEmote(T2, D)` — `D` der `defaultName` aus der Datei, nie `null` (AK 40) — in dieser
  Reihenfolge, mit je einem Set-Read unmittelbar vor `removeEmote(S1)` und `removeEmote(S2)`
  (AK 38) → `sync-deleted` `[S1, S2]`, dann `sync-restored`
  `[T1, T2]` mit `expectedChannelName` → Dock mit beiden Meldungszeilen, Protokoll-Download
  (AK 1, 6, 9, 12, 18). Mocks: `mockEmoteSetTargets` mit `editable: true`, GraphQL-Stub mit
  Set-Stand vor/nach.
- Neu: dieselbe Datei erneut → Notiz `nothingToDo`, kein Dialog (AK 21).
- Neu: Stub lässt den zweiten ADD einer Zeile mit 409 scheitern → Dock `gapCount 1` mit Hinweis →
  Undo aus derselben Datei erneut → Dialog zeigt `addOnly [B]` → genau ein `addEmote(T, B)`
  (AK 17, 30).
- Neu: Stub gibt der zweiten Quelle **unmittelbar** nach dem ersten REMOVE einen zweiten Alias →
  zweiter Read vor dem zweiten REMOVE, zweite Zeile `skippedDrift`, kein zweites REMOVE (AK 26).
  Neu: `planned`-Datei ohne Bestätigung ⇒ „Rückweg sichern" gesperrt (AK 28). Neu: T unter fremdem
  `C`, S hält A ⇒ Zeile übersprungen, kein Request; S weg ⇒ `addOnly` neben `C` (AK 29). Neu: Stub
  fügt `C` nach `addEmote(T, A)` ein und lässt `addEmote(T, B)` mit 409 scheitern → `failed@2` →
  zweiter Lauf ⇒ `addOnly` mit `notes`, `addEmote(T, B)` (AK 36). Neu: zweiter Lauf nach
  zurückgeholtem `D` ⇒ `nothingToDo` (AK 35). Neu: `addOnly` mit Auslassung und 503 auf dem ADD ⇒
  `unknown`, nicht `partial` (AK 37).
- Neu: Stub antwortet auf ein REMOVE mit 503 → Re-Read zeigt Quelle weg → `failed@1`, Quelle in
  `sync-deleted` (AK 11).
- Neu: Weiche → „Lücken schließen" verhält sich wie der bestehende Restore-Test (Regressionsprobe).
- Angepasst: jeder bestehende Test, der eine `transfer-run`-Datei einliest, klickt die Weiche (F10).

### 9.5 Live-Verifikation (Regel 16, Betreiber-Handgriff)

Haupt-Checkout, Api per `dotnet run`, `npm start`; Testkonto Broadcaster `olaf_olaf_son` mit den
Sets `tttt` (aktiv, getrackt) und `test` (nicht-aktiv bzw. ungetrackt, je nach Zustand der Kanalzeile);
Zweitkonto `definitiv_nicht_sensitron` (Mod ohne 7TV-Editor-Recht) als Negativfall. Zu belegen im
PR-Text mit Zahlen:

1. **Vorbereitung:** in `test` zwei Emotes anlegen, deren Namen mit zwei Emotes aus `tttt`
   kollidieren, eines davon zusätzlich unter einem zweiten Alias (Duplikat-Zelle). Übertragung
   `tttt → test` mit „Ziel ersetzen" für beide (#230-Weg, Rückweg-Datei + Ergebnisprotokoll
   sichern). Read-back: Quellen unter den Namen, Ziele weg.
2. **Undo aus dem Ergebnisprotokoll** auf der Seite von `olaf_olaf_son` (Ansicht `tttt`): Weiche →
   Undo → Token → Dialog zeigt zwei `full`-Zeilen mit Bildern, Löschzahl 2, Slot +1, Fremd-Hinweis
   (Ziel ≠ gewähltes Set), „nicht aktiv"-Zeile falls `test` nicht-aktiv → „Rückweg sichern" →
   Datei auf der Platte → „Starten" → Netzwerktab: REMOVE, ADD, REMOVE, ADD, ADD in Reihenfolge →
   `sync-deleted` (2 IDs) dann `sync-restored` (2 IDs), beide 200 → **kein** `POST /resync` aus dem
   Client (nicht-aktives Ziel: keine Resync-Zeile; aktives Ziel: `backendTriggered`) → Dock: 2
   entfernt, 3 zurückgeholt, beide Meldungen `succeeded` → Read-back im 7TV-Web: Quellen weg, Ziele
   unter den alten Namen (der ehemals aliaslose unter seinem Standardnamen); Mitgliederliste der
   Ansicht `test` nach Wechsel mit `refresh=true` (N2-Vormerkung) → Ergebnisprotokoll speichern.
3. **Idempotenz:** dieselbe Datei erneut → Notiz, kein Dialog, kein Request außer dem Read.
4. **Restore aus der Undo-Rückweg-Datei:** einlesen → jede Zeile „Name belegt", kein Lauf.
5. **Restore aus der Übertragungsdatei nach dem Undo:** jede Zeile „schon vorhanden", kein Lauf.
6. **Sperrgründe erzwingen:** Übertragung wiederholen; vor dem Undo im 7TV-Web dem Ziel-Namen der
   zweiten Zeile ein drittes Emote geben → Dialog zeigt die Zeile als `targetNameTaken`, nur eine
   Zeile läuft, die Quelle der zweiten bleibt. Varianten: Quelle im 7TV-Web umbenennen →
   `sourceUnderOtherName`; der Quelle einen zweiten Alias geben → `sourceHasMoreEntries`; das Ziel
   unter einem neuen Namen `C` zurückholen → `targetHasForeignEntries` — jeweils nichts berührt.
6a. **Drift während des Laufs:** Übertragung mit drei Replace-Zeilen; Undo starten und **nach dem
   ersten REMOVE** im 7TV-Web der dritten Quelle einen zweiten Alias geben → die dritte Zeile endet
   `skippedDrift`, Netzwerktab zeigt vor ihrem REMOVE den frischen Read und kein REMOVE; die zweite
   Zeile läuft normal.
6b. **Lücke schließen:** eine `full`-Zeile so scheitern lassen, dass `ADD B` 409 bekommt (B im
   7TV-Web an ein drittes Emote vergeben, **nach** dem Zeilen-Read — Restfenster F13) → Dock
   `gapCount 1` mit Hinweis → B im 7TV-Web freimachen → Undo aus derselben Datei erneut → Dialog
   zeigt die Zeile als `addOnly [B]` → ein ADD → Lücke zu.
6c. **`planned`-Datei:** die Rückweg-Datei der Übertragung (Stufe `planned`) einlesen → jede
   `full`-Zeile „unbelegt", „Rückweg sichern" gesperrt, bis die Bestätigung gesetzt ist; mit
   Bestätigung läuft der Undo wie aus dem Ergebnisprotokoll (Abschnitt 15 A).
6d. **Settling-Sperre:** Undo mit einer Zeile starten, deren REMOVE der Stub (oder ein
   Netzwerk-Throttle) mit 503 beantwortet, sodass das Nachlesen läuft → währenddessen „Importieren"
   öffnen → Trigger gesperrt, Arbiter-Notiz; Tab-Schließen zeigt den Browser-Dialog bis beide
   Meldungen durch sind.
7. **Audit (Kanal- und globale Ansicht):** je Undo ein `syncDeleted`- und ein `syncRestored`-Eintrag
   mit Besitzerkanal (nicht-aktives Set) bzw. ohne Kanal „für olaf_olaf_son" (ungetrackt), plus
   `channel.resync`, falls ausgelöst.
8. **Negativprobe Rechte:** als `definitiv_nicht_sensitron` dieselbe Datei einlesen →
   `targetNotEditable` im Datei-Schritt, keine Weiche, kein Request an 7TV.
9. **Unload-Schutz:** während des Laufs Tab schließen → Browser-Dialog; Kanalwechsel → Guard-Rückfrage.
10. **Ungetracktes Ziel** (falls `test` in dieser Runde ungetrackt ist): Punkte 2–5 zeigen
    `channels: []`, keine Resync-Zeile, Papier-Einträge ohne Kanal.

---

## 10. Nicht in dieser Spec, mit Grund

- **Rückgängigmachen der übrigen Aktionen einer Übertragung** (ADD-, Rename-, Adopt-Zeilen): das
  Issue nennt den geglückten Replace; die Adopt-Zeile hat nicht einmal ihren alten Alias in der
  Datei (`removedTarget: null`, `core/seven-tv/transfer-plan.ts`), eine ADD-Zeile ist über den Purge-Weg
  löschbar. Ein „Übertragung ganz zurücknehmen" wäre ein eigenes Issue.
- **Undo des Undo** (`transfer-undo` als Undo-Eingabe): der Weg zurück ist die Übertragung selbst
  (derselbe Import mit denselben Entscheidungen, `meta.undoneFile.origin` sagt woher) — F7.
- **Auto-Rollback bei Teilerfolg** (E9) — Betreiber-Entscheidung (4) aus #230, nicht neu gestellt.
- **Die Prüfung je REMOVE für das Replace aus #230** (E19): das Replace hat dasselbe alternde
  Fenster (Plan-230 §2 Nr. 6) und prüft heute nur einmal vor dem Start; dieselbe Prüfung dort
  einzuziehen ist naheliegend, aber eine Änderung am Import-Lauf, die #230 revidiert — ein
  Folge-Issue, hier nur benannt.
- **Das 7TV-Budget der tokenlosen Set-Abfrage** (E19, F13): `loadSevenTvSetEntries` läuft ohne
  Token gegen `7tv.io/v4/gql`; ein Limit ist nicht gemessen (die bekannten Messungen betreffen die
  Suche und die Schreib-Bucket `emote_set_change`). Der Undo verbraucht N bis 2N solcher Reads je
  Lauf; ein 429 ist fail-closed ein Read-Fehler (`recheckUnavailable`, drei in Folge ⇒ Rest
  `cancelled`). Messen ist Teil der Live-Verifikation (9.5 Nr. 6a), nicht dieser Spec; fällt das
  Budget knapp aus, ist die Antwort ein eigener Undo-Takt (langsamer, nicht weniger Reads).
- **Regel 2 des Restore-Filters für Zeilen aus Übertragungs- und Undo-Dateien** (F14): ob ein
  fremder Eintrag auf dem Ziel die ganze Restore-Zeile werfen soll, ist eine Restore-Frage (11.3).
- **Backend-Kennzeichnung „Undo" im Audit** — Abschnitt 13, Nr. 5, verworfen.
- **Eine Slot-Sperre statt Warnung** — Abschnitt 13, Nr. 6, verworfen.
- **Änderung des Restore-Filters für `defaultName`-Einträge** (F5 a) — Restore-Frage, 11.3.
- **Wortlaute** — #255; **Arbiter-Settling, Zustandsmaschinen-Extraktion, Dock-Hinweis beim Import**
  — #256; **Laden der Datei als Auswahl** — #201.
- **Ein Undo aus dem Dock unmittelbar nach dem Übertragungslauf** (ohne Datei): verlockend, aber
  das Ergebnis im Speicher ist kein Rückweg-Dokument; die Datei-Tür ist der eine Weg, und sie ist
  einen Klick entfernt. Kann als Folge-Idee notiert werden, wenn der Datei-Weg sich als Reibung
  erweist.

---

## 11. Berührungspunkte mit #255 und #256

### 11.1 #256 Punkt 1 — Arbiter und Settling-Fenster: **Vorbedingung** dieser Spec

Der Undo hat dasselbe Settling-Fenster wie der Import (Re-Read bis 20 s, Meldungen danach) und
hätte dieselbe Lücke gehabt: `activeRun` fällt mit `isRunning`, ein zweiter Start wäre möglich, und
`destructiveRunActive` hinge am gezeigten Lauf. Die erste Fassung wollte damit ausliefern („der
Rest fällt sicher"); Codex-Befund 5 hat gezeigt, dass er **nicht** sicher fällt (F16). Die zweite
Fassung wollte die Sperre lokal im Undo setzen. **Betreiber 2026-09-25 (Abschnitt 15 B):** #256
Punkt 1 wird vor #254 gebaut und gemergt (Reihenfolge #255 → #256 → Plan und Umsetzung von #254);
der Undo hängt sich in den Arbiter ein, den #256 liefert, und enthält keine eigene Sperre.

#### Vertrag, den #256 Punkt 1 für #254 erfüllen muss

Dieser Unterabschnitt ist so geschrieben, dass er als Anforderung an #256 ohne die übrige Spec
lesbar ist. „Lauf-Dienst" meint heute `SevenTvDeleteService`, `SevenTvRestoreService` und
`SevenTvImportService`; #254 fügt `SevenTvUndoService` hinzu.

1. **Zustände je Lauf-Dienst.** Jeder Lauf-Dienst liefert dem Arbiter zwei Signale:
   `isRunning` (die Engine arbeitet eine Queue ab — heute schon vorhanden) und `isSettling`
   (die Engine ist fertig, aber der Lauf ist noch nicht abgeschlossen: ein Nachlesen nach
   `unknown`-Zeilen läuft, **oder** eine Meldung an das Backend hat noch keinen Endzustand
   `succeeded | partial | failed`). Für einen Dienst ohne Nachlesen und ohne Meldung ist
   `isSettling` konstant `false`. Der Import settelt heute schon (`settlement: 'pending'`), meldet
   danach aber außerhalb jedes Zustands — `isSettling` muss beides umfassen.
2. **Was der Arbiter daraus macht.** `activeRun` (oder ein Nachfolger) liefert den Dienst, der
   läuft **oder settelt**, sonst `null`; ein Startpunkt darf nur bei `null` starten. Die Prüfung
   sitzt weiterhin an jedem Startpunkt (vor dem Dialog und unmittelbar vor dem Start), aber sie
   fragt nur den Arbiter — kein Startpunkt fragt einen fremden Dienst direkt. Der Arbiter kennt
   dafür einen Grund, den ein Startpunkt als transiente Notiz zeigen kann (mindestens
   `running | settling`, je Dienst), damit der Nutzer erfährt, warum nichts startet.
3. **Unload-Schutz.** Der `beforeunload`-Schutz (heute nur im Import, an dessen
   `destructiveRunActive`) wird die **Vereinigung** über alle Lauf-Dienste: ein Dienst liefert
   `destructiveOpen` (mindestens ein Lauf mit destruktiver Zeile ist offen — läuft, settelt oder
   meldet noch), und der Schutz ist scharf, solange irgendein Dienst `destructiveOpen` liefert.
   „Offen" hängt **nicht** am gezeigten Lauf: ein `reset()`, ein Kanalwechsel oder ein neuer Lauf
   nehmen einen Lauf, dessen Meldungen noch ausstehen, nicht aus dem Schutz. Der Import muss dafür
   seinen Schutz von `run()` (dem gezeigten Lauf) auf einen Satz offener Läufe umstellen.
4. **Vierter Lauf-Typ.** Ein neuer Dienst registriert sich beim Arbiter, indem er die drei
   Signale (`isRunning`, `isSettling`, `destructiveOpen`) liefert und einen Namen im
   `SevenTvRunKind`-Union bekommt (`'undo'`); der Arbiter darf keine Dienstliste hart in seiner
   Logik tragen, die ein vierter Dienst an mehr als einer Stelle erweitern müsste — eine
   Registrierung, ein Union-Wert, fertig. Der Leave-Guard der Nutzungsseite (`canDeactivate`)
   bleibt daneben, weil er nur beim Kanalwechsel fragt und nur `isRunning` braucht.
5. **Prüfbarkeit des Arbiters.** Ein Test kann einen Dienst-Stub mit `isRunning: false,
   isSettling: true` registrieren und erwartet, dass jeder Startpunkt abweist; ein Stub mit
   `destructiveOpen: true` hält den `beforeunload` scharf, auch wenn `run()` des Dienstes `null`
   ist. Dieser Test prüft nur den Arbiter; die Signale selbst müssen wahr sein — dafür Punkt 6.
6. **Lauf-gebundener Abschluss je Dienst.** Die drei Signale sind nur so viel wert wie die Läufe
   dahinter. Heute kehrt `onRunComplete` des Imports zurück, sobald `this.run() !== started` ist —
   „only reachable via reset() during the run" (`seven-tv-import.service.ts:583-592`): ein
   `reset()` während der Lauf noch läuft, lässt Nachlesen **und** beide Meldungen ausfallen; Delete
   und Restore haben denselben Frühausstieg (`seven-tv-delete.service.ts:292`,
   `seven-tv-restore.service.ts:321`), setzen bei `reset()` `syncReport` auf `idle` und schreiben
   Rückrufe nur unter `applyIfCurrent`, also nur für den gezeigten Lauf. Ein Arbiter, der auf
   solchen Diensten aufsetzt, meldet entweder „belegt" für einen Lauf, der nie abschließt, oder
   „frei", während eine Meldung verloren ging. **Anforderung an jeden Lauf-Dienst:**
   - Ein Lauf ist ein eigener Datensatz mit eigenem Lebenszyklus: `running → settling
     (Nachlesen) → reporting (Meldungen) → closed`. Nachlesen und Meldungen laufen **auf diesem
     Datensatz** zu Ende, gleich ob er noch `run()` ist; `reset()`, `resetIfChannelChanged()` und
     ein neuer Lauf lösen nur die Anzeige. Der Frühausstieg in `onRunComplete` entfällt; an seine
     Stelle tritt: das Ergebnis wird im Laufdatensatz gespeichert und nur dann zusätzlich in `run()`
     gespiegelt, wenn der Lauf noch gezeigt wird.
   - Meldungszustände (`syncReport`, `syncReportReason`, beim Undo je Meldung) leben **am
     Laufdatensatz**; die Dienst-Signale, die das Dock liest, sind Projektionen des gezeigten Laufs.
     Ein Rückruf eines abgelösten Laufs schreibt in dessen Datensatz, nicht in eine Projektion,
     die inzwischen einem anderen Lauf gehört; `applyIfCurrent` schützt die Projektion, nicht den
     Abschluss.
   - `isSettling` ist wahr, solange **irgendein** Lauf des Dienstes in `settling` oder `reporting`
     steht; `destructiveOpen`, solange irgendein Lauf mit destruktiver Zeile nicht `closed` ist.
     `closed` erreicht ein Lauf, wenn Nachlesen beendet ist und jede seiner Meldungen einen
     Endzustand hat (`succeeded | partial | failed`) — oder es für ihn keine Meldung gibt. Ein
     manueller Retry einer Meldung öffnet den Lauf **nicht** wieder (er ist eine neue Meldung am
     geschlossenen Lauf; der Arbiter bleibt frei, der Unload-Schutz bleibt aus).
   - **Tests je Dienst (nicht nur am Arbiter):** `reset()` während `running` ⇒ die Engine läuft
     oder wird sauber abgebrochen, aber jede bis dahin bestätigte Mutation wird gemeldet und der
     Lauf schließt; `reset()` während `settling` ⇒ Nachlesen endet, Meldungen gehen raus, Lauf
     schließt; `reset()` während eine Meldung unterwegs ist ⇒ ihre Antwort landet im Laufdatensatz,
     der Lauf schließt, `isSettling` fällt erst danach; in allen drei Fällen sieht der Stub-Server
     jede Meldung genau einmal, und der Arbiter ist danach frei. Ein Lauf, dessen Meldung
     endgültig `failed` ist, ist trotzdem `closed`.

Was #254 davon nutzt: die Registrierung als `'undo'` (Punkt 4) mit `isSettling` = Nachlesen oder
eine der beiden Meldungen offen, `destructiveOpen` = ein offener Lauf mit `full`-Zeile (6.5); der
Arbiter-Grund für die Notiz am Startpunkt (Punkt 2); der gemeinsame Unload-Schutz (Punkt 3, E15);
den lauf-gebundenen Abschluss (Punkt 6) baut der Undo-Dienst von Anfang an so, ohne den
Frühausstieg der drei bestehenden Dienste zu kopieren (F20, AK 39).
Was #254 **nicht** tut: eine eigene Settling-Prüfung im Undo-Flow, eine Spiegelzeile im
Import-Flow, einen eigenen `beforeunload`, und keine Änderung an Import, Delete oder Restore — deren
Umstellung auf Punkt 6 ist #256.

### 11.2 #256 Punkt 4 — Dock-Hinweis bei unklärbarem REMOVE

Für den Undo wird der Hinweis von Anfang an gezeigt (`undo.summary.unknownRecordedIn`, 4.6). Wenn
#256 den Import-Hinweis baut, sollen beide denselben Schlüssel-Aufbau und dieselbe Stelle im Dock
haben; die Spec legt nur fest, **dass** er da ist.

### 11.3 #256 Punkt 5 — Zustandsmaschine des Bestätigungsdialogs

Der Undo-Dialog hat dieselbe Maschine `idle → verifying → saved` mit denselben Übergängen
(Neuladen ⇒ `idle`, Download-Fehler ⇒ `idle`, Planänderung ⇒ `idle`). 6.3: konsumieren, wenn
extrahiert; sonst minimal eigen, mit Verweis. Zusätzlich für #256 notiert: F5 (a) — der
Restore-Filter liest einen zurückgeholten `null`-Eintrag beim nächsten Lauf als fremden Alias;
und F14 — Regel 2 macht eine Übertragungsdatei wertlos, sobald das Ziel einen fremden Eintrag
trägt. Beides sind Restore-Befunde, die der Undo nur aufgedeckt hat.

### 11.4 #255 — Wortlaute, Zählungen — und die Resync-Regel für nicht-aktive Ziele

**Betreiber-Entscheidung 2026-09-25 (in #255, parallel umgesetzt):** der Restore stößt bei einem
nicht-aktiven Ziel eines getrackten Kanals **keinen** Client-Resync mehr an und zeigt keine
Resync-Zeile — genau wie der Import. Hintergrund: Nachtrag N2 der #253-Spec hat gezeigt, dass der
Resync die Mitgliederliste der nicht-aktiven Ansicht nicht nachlädt; das erledigt
`usage-stats-page` beim Settle. Der N1-Fallback-Resync für das **aktive** Set bleibt, ebenso die
Anzeige `backendTriggered`. E12 und 6.4 der #253-Spec werden dafür per Nachtrag aufgehoben. **Der
Undo übernimmt diese Regel von Anfang an** (E16, F8, 4.5 Nr. 17, 6.5, AK 14): kein
`resyncChannelName` am Laufdatensatz, kein Client-Resync im Erfolgsfall, Fallback nur für
`expectedChannelName`, wenn beide Meldungen endgültig scheitern. Landet #255 nach dem Undo, ist
der Undo bereits auf der neuen Regel; landet es davor, gibt es nichts anzugleichen.

Jeder neue Schlüssel unter `restore.import.choice.*`, `undo.*` ist vorläufig. Drei Stellen, an denen
#255 den Undo ausdrücklich mitbedenken soll: der Token-Prompt spricht vom Löschen (für den Undo
richtig, für Restore/Kopieren falsch); die Zählung „zurückgeholt" zählt Einträge, „entfernt" zählt
Emotes (eine Duplikat-Zelle macht den Unterschied sichtbar); die Weiche braucht zwei Sätze, die
„Lücken schließen" und „rückgängig machen" ohne Fachwort unterscheiden.

---

## 12. Rückweg

Kein Schema, keine Migration, kein Backend-Diff: der Revert ist ein Frontend-Revert. Bereits
heruntergeladene `transfer-undo`-Dateien bleiben als JSON lesbar, sind in einem revertierten Build
aber mit `wrongKind` nicht mehr einlesbar — dieselbe Regel wie Plan-230 §8 für `transfer-run`. Ein
gelaufener Undo ist nicht durch Revert rückgängig; sein Rückweg ist die Undo-Rückweg-Datei (Restore)
oder die Übertragung selbst. Wer revertiert, revertiert nicht zwischen einem Undo-Lauf und dem
Restore aus seiner Datei. Die Weiche im Datei-Schritt verschwindet mit dem Revert; Übertragungsdateien
laufen dann wieder direkt in den Restore.

---

## 13. Betreiberentscheidungen (2026-09-25)

Die sechs Punkte, die die erste Fassung dem Betreiber vorgelegt hat, sind am 2026-09-25
entschieden — jeweils die empfohlene Option. Je Punkt die Entscheidung mit Begründung und die
verworfenen Alternativen in einer Zeile; die Stelle, an der die Entscheidung in der Spec steht,
in Klammern.

**1. Eigene Pflicht-Rückweg-Datei vor dem ersten REMOVE (Spiegel von #230-Entscheidung 4) —
entschieden: ja, gespiegelt** (E3, 4.2 Nr. 8, 4.8 Nr. 20). `transfer-undo` Stufe `planned`,
Pflicht-Download, per Restore einlesbar. Erfüllt „the safeguard is a file" wörtlich: die Datei
stellt wieder her, was dieser Lauf entfernt; ein Klick mehr, keine neue Oberfläche.
- Verworfen: die Übertragungsdatei auf der Platte als Rückweg gelten lassen, weil ein entferntes
  Quell-Emote dann nur „im 7TV-Web von Hand" zurückkäme (Import verbietet die Datei, Restore liest
  nur Ziele) — der am 2026-09-23 abgelehnte Handgriff.
- Verworfen: nur das Ergebnisprotokoll nach dem Lauf, weil es beim Tab-Tod mitten im Lauf nicht
  existiert — genau die Lücke, die Entscheidung 4 geschlossen hat.

**2. Was ein Restore aus einer `transfer-undo`-Datei wiederherstellt — entschieden: die
entfernten Quell-Emotes** unter ihrem Alias (E12, 4.8 Nr. 21, 6.4). Regel 4 blockt nach einem
geglückten Undo. Ein Grundsatz für alle Rückweg-Dateien: jede stellt her, was ihr Lauf entfernt
hat; die Ziel-Lücke schließt die Übertragungsdatei.
- Verworfen: die nicht zurückgekommenen Ziel-Einträge, weil das redundant zum Restore aus der
  Übertragungsdatei wäre und den Grundsatz bräche — eine Datei, deren Restore etwas anderes
  zurückholt, als ihr Lauf entfernt hat.
- Verworfen: gar nichts (nur Papier), weil Nr. 1 dann ohne Wert wäre.

**3. Ort der Weiche Restore/Undo — entschieden: im Datei-Schritt**, nach der Vorprüfung, nur für
Übertragungsdateien (E1, 4.1 Nr. 3, 6.1). Der Ort, an dem der Nutzer die Datei gerade eingelesen
hat; keine neue Tür; Purge-Protokolle unberührt.
- Verworfen: eine eigene Tür „Übertragung rückgängig machen" im Quellen-Dialog, weil sie ein
  Dauer-Control mehr und einen zweiten Datei-Schritt für dieselbe Datei bedeutete.
- Verworfen: ein Knopf im Import-Dock nach dem Übertragungslauf ohne Datei, weil das Ergebnis im
  Speicher kein Rückweg-Dokument ist und der Weg nach einem Reload fehlte (Abschnitt 10).

**4. Der Undo schließt auch die Lücken seiner Zeilen — entschieden: ja** (`addOnly`, E7, 4.3).
Zeilen ohne Quelle laufen als reine ADDs mit. Ziel ist der Zustand vor der Übertragung; die
nicht-destruktive Hälfte in einen zweiten Lauf zu verlegen, gewinnt nichts.
- Verworfen: nur `full`-Zeilen und `addOnly` als „Lücke, per Restore schließen" überspringen, weil
  das enger, aber ein Lauf mehr für den Nutzer wäre — ohne Sicherheitsgewinn.

**5. Kennzeichnung des Undo im Audit — entschieden: nein** (E11, Abschnitt 5). `syncDeleted` +
`syncRestored` wie heute; die Unterscheidung trägt das Protokoll (`meta.undoneFile`). Null
Backend-Änderung; das Audit zählt Mutationen, es erzählt keine Absicht.
- Verworfen: ein optionales Body-Feld (`reason: 'transferUndo'`) auf beiden Routen, weil es eine
  Backend-Änderung samt Test je Route für eine Anzeige wäre, die die Admin-Ansicht noch nicht rendert.

**6. Slot-Überschreitung im Dialog — entschieden: Warnung** wie im Import-Dialog (E17, 4.2 Nr. 7).
Ein gescheitertes ADD ist eine benannte Lücke; nur Duplikat-Zellen sind netto positiv.
- Verworfen: eine Sperre, sobald `delta` die freie Kapazität übersteigt, weil sie auch bei
  veralteter Kapazitätsvorschau blockte und für jedes Ziel den budgetierten Read verlangte.

---

## 14. Dateireferenz

**Frontend (neu):** `web/src/app/shared/export/transfer-undo-export.ts` (+ `.spec.ts`) ·
`web/src/app/shared/seven-tv/undo-plan.ts` (+ `.spec.ts`) · `undo-flow.ts` · `undo-confirm-dialog.ts` ·
`undo-progress-section.ts` · `web/src/app/core/seven-tv/seven-tv-undo.service.ts` (+ `.spec.ts`).

**Frontend (geändert):** `shared/export/export-envelope.ts` (`ExportKind`), `transfer-run-export.ts`
(`parseTransferRunForUndo`), `import-source-parser.ts` (Abweisung), `purge-run-export.ts` (nur Typen) ·
`shared/seven-tv/file-import-step.ts` (Weiche, dritte Ergebnisart), `import-trigger.ts` (Verzweigung),
`import-source-dialog.ts` (Bindung) · `core/seven-tv/seven-tv-run-arbiter.ts` (Registrierung
`'undo'` auf dem Arbiter aus #256, 11.1 — `import-flow.ts` bleibt unberührt) ·
`core/seven-tv/seven-tv-run-engine.ts` (nur,
falls der Plan die Zeilen-Prüfung als Hook einhängt, E19) ·
`features/usage-stats/usage-stats-leave.guard.ts` (Undo-Dienst; nach #264 nur aus
`usage-stats.routes.ts` referenziert, F9), `usage-stats-page.html`/`.ts` (Section, N2) ·
`features/channel-workspace/channel-workspace-layout.ts` (`resetIfChannelChanged`) ·
`web/public/i18n/de.json`, `en.json` · `docs/UI-Designsprache.md` §7.3 (Weiche, fünfte Einlesesorte).

**Backend:** keine Änderung.

**DECISIONS (Regel 3, englisch, im jeweils ersten betroffenen Commit):** (1) „A replace can be
undone from its transfer file — a fourth destructive run with the replace's safeguards" (E1, E2, E5,
E6, E7, E9, E10, E11, E15, E16, E17); (2) „Every recovery file restores what its own run removed —
the transfer-undo file" (E3, E12, F7), mit dem ausdrücklichen Satz, dass #230-Entscheidung 4
gespiegelt und #230-Entscheidung 6 nicht wiederkehrt (E4) und warum.

**Issues:** #254 schließt mit dem Merge in den Epic-Branch (von Hand); das Epic #200 bekommt die
Zeile; #256 erhält den Hinweis auf 11.1/11.3, F5 (a) und F14; #255 den auf 11.4.

---

## 15. Betreiberentscheidungen nach Codex-Review (2026-09-25)

Zwei Befunde der Zweitmeinung warfen Fragen auf, die sich nicht innerhalb der sechs Entscheidungen
aus Abschnitt 13 beantworten ließen. Beide sind am 2026-09-25 entschieden; je Punkt die
Entscheidung mit Begründung, die verworfenen Alternativen in einer Zeile, die Stelle in Klammern.

**A. Herkunftsnachweis bei Dateien der Stufe `planned` (Codex-Befund 2, F17) — entschieden:
`planned`-Dateien bleiben Undo-fähig, mit sichtbarer Kennzeichnung je `full`-Zeile und einer
ausdrücklichen Bestätigung je Datei**, ohne die keine `full`-Zeile läuft; `addOnly`-Zeilen
unberührt (E2, F17, 4.3, 6.3, AK 28, 9.5 Nr. 6c) — wie empfohlen. Erhält den Tab-Tod-Fall (nur
die `planned`-Datei existiert, die gelaufenen Replace-Zeilen sind ohne Undo nicht zurückzunehmen)
und legt die Beweislast dorthin, wo sie hingehört — der Nutzer sieht Bilder und Namen und
bestätigt, dass er die Herkunft geprüft hat. Der Restfall ist ein Zufall zweiter Ordnung, gegen
den die Bestätigung ein bewusster Handgriff ist, keine Formalie.
- Verworfen: `planned`-Dateien nur für `addOnly`-Zeilen zulassen, weil der Tab-Tod-Fall damit
  seinen einzigen In-App-Rückweg verlöre — die vor dem Tab-Tod geglückten Zeilen ließen sich nur
  noch im 7TV-Web von Hand zurücknehmen, der abgelehnte Handgriff.
- Verworfen: `planned` wie `finished` behandeln (erste Fassung), weil das im Restfall eine Löschung
  autorisiert, die sich nicht beweisen lässt — gegen die Leitplanke „im Zweifel fail-closed".

**B. Arbiter-Settling als Vorbedingung (Codex-Befund 5, F16, 11.1) — entschieden: #256 Punkt 1
wird Vorbedingung von #254**; der Undo hängt sich in den Arbiter ein, den #256 liefert, und
enthält keine eigene Settling-Sperre (E15, E22, F16, 6.3, 6.5, AK 16, 32, 11.1) — **abweichend
von der Empfehlung** der zweiten Fassung. Grund: #256 wird in dieser Session ohnehin vor der
Umsetzung von #254 gebaut und gemergt (Reihenfolge #255 → #256 → Plan und Umsetzung von #254);
die lokale Doppelsperre hätte keinen Gewinn und müsste wieder raus. Der Vertrag, den #256 Punkt 1
dafür erfüllen muss, steht in 11.1 und gilt zugleich als Anforderung an #256.
- Verworfen: lokale Sperre im Undo-Flow plus Spiegelzeile im Import-Flow, später von #256
  konsolidiert (die Empfehlung), weil die Reihenfolge der Issues die Wartezeit, die sie vermeiden
  sollte, gar nicht erzeugt — und der Code zweimal geschrieben und einmal gelöscht würde.
- Verworfen: ausliefern wie in der ersten Fassung („der Rest fällt sicher"), weil F16 gezeigt hat,
  dass er nicht sicher fällt.

---

## 16. Nachtrag: Adversariale Zweitmeinung (Codex Sol, gpt-6-sol, 2026-09-25)

Urteil „needs-attention", fünf Befunde high, zwei medium. Jeder Befund wurde gegen den Code und
die Spec geprüft; keiner ist widerlegt, einer (Befund 2) und die Vorbedingungsfrage aus Befund 5
waren Betreiberfragen — beide am 2026-09-25 entschieden (Abschnitt 15).

| # | Schwere | Befund | Prüfung am Code | Lösung | Wo |
|---|---|---|---|---|---|
| 1 | high | Der Frischcheck altert während des sequentiellen Laufs; ein späteres REMOVE nimmt einen inzwischen vergebenen zweiten Alias mit | Zutreffend: Engine sequentiell mit 275 ms je Schritt und Rate-Limit-Pausen bis zu Minuten (`seven-tv-run-engine.ts:36-46`, `:430-462`); kein Vor-Schritt-Hook (`runRowFrom`, `:389-406`) | Prüfung je REMOVE gegen frischen Read — in Runde 1 noch koalesziert ≤ 5 s, **in Runde 2 gestrichen** (eigener Read je REMOVE-Versuch, Befund 1 dort); Read-Fehler ⇒ Zeile übersprungen, drei in Folge ⇒ Rest `cancelled`; Restfenster benannt | E14, E19, F13, 4.3, 4.4 Nr. 10a, 6.5, 7, AK 26–27, 38, 9.5 Nr. 6a |
| 2 | high | Eine `planned`-Datei beweist nicht, dass ihr Lauf je begann; der Live-Check beweist Zustand, nicht Herkunft | Zutreffend: Rückweg-Datei entsteht vor dem ersten REMOVE (Plan-230 §2), Audit trägt Zählwerte ohne IDs | `provenance` je Kandidat; Kennzeichnung + Datei-Bestätigung, ohne die keine unbelegte `full`-Zeile läuft — **Betreiber: so entschieden** | E2, F17, 4.3, 6.3, 7, AK 28, 9.5 Nr. 6c, **Abschnitt 15 A** |
| 3 | high | Bei einem fremden Ziel-Eintrag schließt nach einem Teilerfolg keine der beiden Dateien die Lücke (Regel 2 wirft die Restore-Zeile) | Zutreffend: `missingAliases` liefert bei fremdem Alias `[]` (`already-present-filter.ts:210-215`) | Fremde Ziel-Einträge sind Sperrgrund für `full` **und** `addOnly` an allen drei Prüfstellen; der kanonische Lückenschluss ist der Undo selbst (zweiter Lauf ⇒ `addOnly`), nicht der Restore aus der Übertragungsdatei | E9, E18, E20, F14, 4.3 Schritt 2, 4.7, 7, AK 17, 29, 30, 9.5 Nr. 6b, 11.3 |
| 4 | high | Ein `null`-Eintrag hat nach dem Replace keinen Live-Standardnamen; `defaultName` aus der Datei ist nullbar und kann veraltet sein | Zutreffend: `defaultNameById` nur für IDs im Set (`seven-tv-set-entries.ts:41-71`), `defaultName: string \| null` (`transfer-run-export.ts:49`, Parser `:433`); kein Einzel-Emote-Lookup im Client | Jeder ADD mit explizitem Alias, für `null` der Datei-`defaultName`; Prüfung und Mutation gegen denselben Namen; ohne `defaultName` keine `full`-Zeile bzw. Eintrag ausgelassen | E21, F15, 4.3 Schritt 3/5, 4.4 Nr. 10, 6.4, 6.5, 7, AK 31 |
| 5 | high | Ein zweiter Lauf während des Settlings ersetzt den gezeigten Lauf und lässt dessen Unload-Schutz und Meldungen fallen | Zutreffend: `activeRun` nur aus `isRunning` (`seven-tv-run-arbiter.ts:46-55`), `destructiveRunActive` folgt `run()` (`seven-tv-import.service.ts:273-277`) | Sperre im Arbiter: #256 Punkt 1 ist **Vorbedingung** (Betreiber, abweichend von der Empfehlung); der Undo registriert sich dort mit `isRunning`/`isSettling`/`destructiveOpen`, der Unload-Schutz spannt über alle offenen destruktiven Läufe bis zum Endzustand beider Meldungen; Vertrag an #256 in 11.1 | E15, E22, F16, 6.3, 6.5, 7, AK 16, 32, 11.1, **Abschnitt 15 B** |
| 6 | medium | Eine `full`-Zeile mit einem belegten Ziel-Namen lief mit dem Rest und endete `done`, ohne Lücken-Spur | Zutreffend (Spec-Logik der ersten Fassung, E8) | `full` alles-oder-nichts (`targetNameTaken` sperrt die Zeile); `addOnly` läuft mit `omittedEntries`, endet `partial`, Dock und Datei tragen den Vermerk | E8, E23, 4.3 Schritt 5, 4.7, 6.2, 6.4, 6.5, 7, AK 33 |
| 7 | medium | Das Nachlesen ordnete Schritt 0 pauschal dem REMOVE zu; eine `addOnly`-Zeile hätte eine abwesende Quelle als bestätigtes REMOVE gelesen | Zutreffend (Spec-Logik der ersten Fassung, 4.6) | Nachlesen nach Operation des Schritts je Modus; `addOnly` gerät nie in die Löschmeldung | E24, 4.6, AK 34 |

### Runde 2 (2026-09-25, zweite und letzte Runde)

Urteil erneut „needs-attention", vier Befunde high, einer medium. Jeder gegen Code und Spec
geprüft; keiner widerlegt, keiner wirft eine neue Betreiberfrage auf — die Entscheidungen aus 13
und 15 bleiben unverändert. Die Leitplanke „fail-closed" entscheidet Befund 1 gegen die
Koaleszierung und Befund 3 für `full` gegen, für `addOnly` **für** das Laufen — weil ein ADD nichts
entfernt und der Reparaturweg sonst nicht existiert.

| # | Schwere | Befund | Prüfung | Lösung | Wo |
|---|---|---|---|---|---|
| 1 | high | Ein bis 5 s alter Read darf vor dem nächsten REMOVE wiederverwendet werden; die Zeilen liegen 275 ms auseinander — AK 26 verlangt, was E19 nicht leistet | Zutreffend: Spec-Widerspruch zwischen E19 (Koaleszierung) und AK 26; Engine-Takt `RUN_DELAY_MS = 275` (`seven-tv-run-engine.ts:29`) | Koaleszierung gestrichen: eigener vollständiger Read je REMOVE-Versuch, auch nach Rate-Limit-Wiederholung; Kosten benannt (N–2N Reads, ungemessenes Budget, 429 fail-closed) | E19, F13, 4.4 Nr. 10a, 6.5, 7, 10, AK 26, 38, 9.3, 9.4 |
| 2 | high | Fremde Einträge werden gegen das rohe `E` geprüft, `null` erst danach zu `D`; ein zurückgeholtes `D` gilt beim nächsten Read als fremd — Idempotenz und Reparatur gebrochen; E2E erwartete `addEmote(T2, null)` | Zutreffend: Spec-Reihenfolge 4.3 der zweiten Fassung; 9.4 trug den alten Wert | Normalisierung als Schritt 2 vor jeder Zielprüfung, `N` statt `E` überall, `normalizeTargetEntries` als eigene reine Funktion; E2E auf `addEmote(T2, D)` | E21, F18, 4.3 (Reihenfolge), 6.2, 7, AK 35, 40, 9.3, 9.4 |
| 3 | high | `addOnly` war bei fremdem Ziel-Eintrag gesperrt; kommt `C` mitten in einer `full`-Zeile dazu, gibt es nach dem Teilerfolg keinen ausführbaren Reparaturweg (Undo gesperrt, Restore-Regel 2 wirft, Undo-Datei kann S nicht) | Zutreffend: E20 der zweiten Fassung, Regel 2 (`already-present-filter.ts:210-215`) | Sperre nur für `full` (destruktiv); `addOnly` läuft eintragsweise neben `C` mit Vermerk `notes`, `C` bleibt; Reparaturweg E9 in jeder Lage ausführbar | E20, F14, 4.3 Schritt 3, 4.7, 6.2, 6.4, 7 (drei Zeilen), AK 29, 36, 9.3, 9.4 |
| 4 | high | `partial` wurde „unabhängig vom Engine-Status" gesetzt und hätte `unknown`/`failed` einer ADD hinter einer Auslassung versteckt | Zutreffend: 6.5 der zweiten Fassung | `omittedEntries` orthogonal; `partial` nur statt `done`; `failed`/`unknown`/`cancelled` bleiben und zählen in ihren Zählern und in `omittedEntryCount` | E23, F19, 4.3 Schritt 6, 4.7, 6.4, 6.5, 7, AK 33, 37 |
| 5 | medium | Der Vertrag an #256 verlangte offene Läufe über `reset()` hinaus, testete aber nur Signal-Stubs; die Dienste steigen heute bei abgelöstem Lauf vor Settling und Meldung aus | Zutreffend: `onRunComplete` kehrt bei `run() !== started` zurück (`seven-tv-import.service.ts:583-592`); Delete/Restore `:292`/`:321`, `reset()` setzt `syncReport` auf `idle` | Punkt 6 im Vertrag: Lauf-Lebenszyklus `running → settling → reporting → closed` am Laufdatensatz, Meldungszustände am Lauf, Dienst-Signale als Projektion, Retry öffnet nicht wieder, Tests je Dienst für `reset()` in allen drei Phasen; der Undo-Dienst baut es so von Anfang an | F20, 6.5, 11.1 Punkt 5–6, AK 39, 9.3 |

---

## 17. Klärungen zum Plan (2026-09-25)

Vier Stellen, an denen der Plan ([Plan-254-Replace-Undo.md](../../plans/Plan-254-Replace-Undo.md),
Abschnitt 7) einen Widerspruch oder eine Lücke dieser Spec gefunden hat. Entschieden am
2026-09-25 — K1, K2 und K4 vom Betreiber, K3 vom Orchestrator —, nach dem adversarialen
Codex-Review des Plans (gpt-6-sol, drei Befunde `high`, Plan Abschnitt 10). Wo eine Klärung einen
Satz dieser Spec präzisiert oder ersetzt, steht es hier; die genannten Stellen gelten in dieser
Fassung. Bezeichner bleiben englisch, der Nachtrag ist deutsch wie die Spec.

**K1 — Bilder im Bestätigungsdialog (präzisiert 4.2 Nr. 7 und 6.3).** Die Quelle zeigt ihr
echtes Bild, das Ziel einen Platzhalter. Der Live-Read (`loadSevenTvSetEntries`) liest zusätzlich
`flags { animated }` — dasselbe Feld, das die Vorschau-Query in
`src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs` liest; `Emote.images` liest sie
absichtlich nicht — und liefert `animatedById`. Die **Quelle** bekommt ihre URL aus ID und Flag
nach der Backend-Regel `BuildForeignImageUrl` (ebenda): `https://cdn.7tv.app/emote/{id}/4x_static.webp`
nur bei `animated`, sonst `4x.webp`; fehlt das Flag, `4x.webp` — die Rendition, die für jedes
Emote existiert (statische Emotes antworten unter `_static` mit 404, live gemessen am
2026-09-09). Das ist die erste Ableitung einer URL aus der ID im Frontend (`emoteStillUrl` neben
`animatedEmoteUrl` in `shared/emotes/emote-url.ts`, bytegleich mit dem Backend); das Verbot aus
Plan-230 T1 galt der Ableitung **ohne** Kenntnis der Animiertheit und bleibt für jede Ableitung
ohne Flag bestehen. Das **Ziel** steht nach einem geglückten Replace in keinem Set und bekommt den
Platzhalter des Auflösungsschritts, mit Name und Aliasen daneben; keine zusätzliche 7TV-Anfrage.
Tests: statisches und animiertes Quell-Emote. Der Satz „über dieselbe Bild-URL-Ableitung wie der
Auflösungsschritt (Plan-230 T1)" in 6.3 ist damit ersetzt.

**K2 — Herkunfts-Bestätigung bei gemischten `planned`-Dateien (präzisiert 6.3, 4.2 Nr. 8, AK 28;
vereinheitlicht die `startUndo`-Signatur aus 6.3 und 6.5).** Lesart „effektiver Plan", doppelt
gesichert. Ohne gesetzte Bestätigung sind unbelegte `full`-Zeilen im Dialog als `skippedUnproven`
übersprungen (sichtbar, mit Grund); die Aktionszeile folgt dem effektiven Plan — nur
`addOnly`-Zeilen ⇒ direkt „Starten", keine Rückweg-Datei (4.2 Nr. 8); keine laufende Zeile ⇒
Aktionszeile gesperrt mit Grund. Mit Bestätigung sind sie `full`, und „Rückweg sichern" erscheint.
Das Dialog-Ergebnis übergibt die laufenden und die übersprungenen Zeilen **explizit**
(`runnable`, `skipped`, `acknowledgedUnproven`, `read`). **Der Dienst prüft die Herkunftssperre
selbst noch einmal:** ohne `acknowledgedUnproven` läuft keine `full`-Zeile mit `provenance:
'unproven'` — sie wird vor dem Aufbau der Queue `skippedUnproven`, es entsteht kein REMOVE, gleich
was der Dienst bekommt. Signatur in 6.3 **und** 6.5: `startUndo(target, runnable, skipped,
acknowledgedUnproven)`; die Drift-Zeilen des Frischchecks stehen in `skipped` mit `skippedDrift`.
`acknowledgedUnproven` steht im Laufdatensatz und in `meta` beider Undo-Datei-Stufen (Papierspur,
F6). Ein Test verfolgt eine gemischte `planned`-Datei ohne Bestätigung über „Starten" bis in den
Dienst und belegt, dass kein REMOVE gesendet wird — als Unit-Test mit dem echten Dienst und als
E2E.

**K3 — Der erste Read liegt im Flow (präzisiert 4.2 Nr. 6, E14, AK 5, AK 7).** `startUndoFlow`
macht den ersten Read und die erste Klassifikation. Nichts Laufendes ⇒ transiente Notiz, kein
Dialog, kein zweiter Request (E18, AK 5). Read-Fehler oder `complete: false` ⇒ der Dialog öffnet
im Fehlerzustand mit „Ziel neu laden" und gibt nichts frei (AK 7). Erfolg ⇒ der Dialog öffnet mit
Read und Plan als Eingabe und liest **nur** bei „Ziel neu laden" erneut. „Ein Read beim Öffnen des
Dialogs" (E14, 4.2 Nr. 6) ist dieser Read des Flows; es bleiben drei Prüfstellen.

**K4 — Übersprungene Kandidaten im Ergebnisprotokoll (präzisiert 6.4, 4.8 Nr. 20, AK 18).** Das
`finished`-Format bekommt eine eigene Zeilenart „übersprungen" (`kind: 'skipped'`): Quell-ID,
`sourceName`, `alias`, Ziel-ID, `provenance`, `skippedReason` (jeder Grund aus 4.3 sowie
`skippedUnproven` und `skippedDrift`), **ohne** `mode`, `restoredTarget`, `removedSource`,
`status`, `completedSteps` — die bisherige Zeilenform kann einen Kandidaten, der vor der
Klassifikation übersprungen wurde (`duplicateInFile`), nicht abbilden. Gelaufene Zeilen behalten
die Form aus 6.4 mit `kind: 'executed'`; eine Zeile, die erst im Lauf über die Prüfung vor dem
REMOVE übersprungen wird (`skippedDrift`, `recheckUnavailable`), bleibt `executed` mit `status:
'cancelled'` und `skippedReason`, wie 6.4 es sagt. Das Ergebnisprotokoll führt jeden Kandidaten der
Datei: gelaufene mit ihrem Status, im Dialog, im Frischcheck oder durch die Herkunftssperre des
Dienstes übersprungene als „übersprungen". `counts.requested` zählt nur Zeilen, die tatsächlich
gelaufen sind; `counts.skipped` kommt dazu. Der Restore-Parser der `transfer-undo`-Datei
überspringt „übersprungen"-Zeilen ausdrücklich (nichts wurde entfernt). Die Rückweg-Datei
(`planned`) führt weiterhin nur die laufenden Zeilen. CSV: Spalten `kind` und `skipped_reason`,
die übrigen Spalten einer übersprungenen Zeile leer. Die Festlegung gehört vor die Umsetzung des
Dateiformats (Plan T1), mit Tests für `duplicateInFile` und die übrigen Übersprünge vor der
Klassifikation.

---

## 18. Nachtrag: Wo die Umsetzung vom Spec-Text abweicht (2026-09-26)

Festgehalten nach T8 (E2E und Doku). Die Umsetzung gilt; der Text oben bleibt stehen, dieser
Abschnitt sagt, wo er nicht mehr stimmt.

1. **„Ergebnisprotokoll herunterladen" statt „speichern" (4.7 Nr. 18, 9.5 Punkt 2).** Der Knopf im
   Dock heißt wie beim Löschen und beim Import „herunterladen" (Wortwahl des Orchestrators). Der
   stille Hinweis `protocolNotSaved` spricht weiter von „gespeichert". Der Live-Schritt 9.5 benutzt
   den Knopf unter diesem Namen.
2. **Übersprungen-Zeilen sind nur vor dem Lauf transient (4.7 Nr. 18).** Die Notiz eines Starts
   (4 s) nennt die übersprungenen Kandidaten je Grund, auch wenn nichts startet (AK 5, 21). Sobald
   der gestartete Lauf nicht mehr läuft, nennt seine Zusammenfassung dieselben Kandidaten je Grund
   und bleibt stehen, solange der Lauf gezeigt wird; die Notiz weicht ihr. Beide stehen nie
   gleichzeitig, kein Kandidat wird doppelt genannt.
3. **Schlüssel der Löschmeldung: `undo.sync*`, nicht `undo.removalSync*` (6.7).** Das
   `RunProgressPanel` bildet seine Meldungsschlüssel aus `labelPrefix` (`undo.syncFailedTitle`,
   `undo.syncRetry` usw.); die Löschmeldung sitzt im Panel und erbt diese Form. Die
   Wiederherstellungsmeldung heißt wie geplant `undo.restoreSync*`. Einen Schlüssel
   `undo.summary.gapsHint` gibt es nicht; der Hinweis steht in `undo.summary.gaps.*` selbst.
4. **Token-Löschung bei `LACKING_PRIVILEGES`: E13 widerspricht 4.9 Nr. 23 und AK 10.** E13 nennt
   die Token-Löschung „Import-Muster"; der Import löscht das Token bei `LACKING_PRIVILEGES` aber
   nicht (nur die Engine bei 401/403). Umgesetzt ist 4.9 Nr. 23 / AK 10: der Undo löscht es auch
   bei `LACKING_PRIVILEGES`.
5. **N1-Fallback bei nur einer gesendeten Meldung (AK 14, 4.9 Nr. 24).** Schickt ein Lauf nur eine
   Meldung (etwa ein reiner `addOnly`-Lauf nur `sync-restored`) und scheitert sie endgültig, läuft
   der Fallback-Resync für `expectedChannelName`. Lesart von „scheitern beide": keine Meldung hat
   die Resync-Stufe des Backends erreicht. „Scheitert nur eine, keiner" gilt für einen Lauf mit
   zwei Meldungen.
6. **`undo-plan.ts` und `undo-candidate.ts` liegen in `core/seven-tv/` (14).** 14 legt
   `undo-plan.ts` nach `shared/seven-tv/`. Der Dienst in `core/` konsumiert es zur Laufzeit, und
   `core/` darf nicht aus `shared/` importieren. Deshalb liegen die Klassifikation und die
   Kandidatentypen (`UndoCandidate`, `UndoCandidateTargetEntry`, `UndoSourceFileInfo`) in `core/`.
   `transfer-run-export.ts` reicht die Typen weiter. `buildUndoRunProtocol` liegt in
   `shared/export/transfer-undo-export.ts`, nicht im Dienst.
7. **`removedSource` im Ergebnisprotokoll (6.4, F13).** Die Quell-Einträge einer `full`-Zeile
   kommen aus dem letzten Read vor ihrem REMOVE. Antwortete vor ihr kein Read (Lesefehler, Abbruch
   vor dem Read), steht der gestempelte Stand `[{ alias }]` darin. Eine Zeile mit `confirmed:
   false` und `entries: [{ alias }]` sagt deshalb nicht, ob ein Read genau das zeigte oder keiner
   antwortete; die Datei unterscheidet die beiden Fälle nicht.
8. **Slot-Delta im E2E-Hauptweg (9.4).** „Slot-Delta +1" passt nicht zur ADD-Liste daneben: zwei
   REMOVEs und vier ADDs (`T1, a`; `T2, a`; `T2, b`; `T2, D`) ergeben +2. Der E2E-Fall prüft +2.
9. **Mitgliederliste eines gewählten, nicht-aktiven Sets nach dem Settle (19, N2).** Die Seite lädt
   sie nur neu, wenn der gesettelte Lauf `doneKeys.length > 0` hat (`watchRunSettle` in
   `usage-stats-page.ts`). Ein Lauf, in dem jede Zeile nach bestätigtem REMOVE `failed` endet, hat
   das Set verändert, aber keinen `done`-Schlüssel — die Liste bleibt veraltet. Der Import hat
   dieselbe Lücke; ein Folge-Issue für beide entscheidet der Betreiber.
10. **„Rückweg sichern gesperrt" (AK 28, 9.4).** Ohne Bestätigung gibt es keinen gesperrten Knopf
    „Rückweg sichern": die unbelegten `full`-Zeilen sind `skippedUnproven`, der Knopf fehlt, und
    „Starten" ist mit Grund gesperrt, solange keine Zeile laufen kann (17 K2).
11. **Ein `finished`-Ergebnisprotokoll beweist die Herkunft nicht mehr allein durch seine Stufe
    (Review-Nachbesserung, 2026-09-26).** `parseTransferRunForUndo` markiert eine Zeile aus einer
    `finished`-Datei nur dann `provenance: 'confirmed'`, wenn ihr eigener `status` `'done'` ist;
    jeder andere Wert (`failed`, `unknown`, `cancelled`, ein gestempeltes `pending`) macht sie
    `'unproven'` — dieselbe Herkunftssperre und dieselbe Checkbox wie bei einer `planned`-Datei
    (17 K2). Grund: eine vom REMOVE bestätigte Zeile sagt nichts darüber, ob ihr eigener ADD-Teil
    je fertig wurde; der live Zustand belegt den Zustand, nicht die Dateistufe.
12. **Das Dock nennt den belegten Namen nicht (7, Zeile „Ziel-Alias inzwischen von Dritten belegt")
    — bewusst nicht nachgezogen.** Nur der Bestätigungsdialog nennt einen belegten Namen (die
    übersprungene Zeile mit ihrem Live-Gegenstück); das Dock und das Ergebnisprotokoll zählen einen
    `targetNameTaken` nur je Grund (`summary().skippedByReason`, wie jeden anderen Übersprung).
    Ein `targetNameTaken`, das erst der Frischcheck vor einem REMOVE findet, erscheint dort
    ohnehin nicht als eigener Grund, sondern als `skippedDrift` (die Klassifikation ändert sich,
    der Frischcheck sagt nicht, wonach) — die Vorgabe würde also nur beim ersten Read vor dem Lauf
    greifen können.
13. **`firstReadPending` sperrt einen zweiten Klick, solange der erste Read (17 K3) offen ist
    (`undo-flow.ts`, `import-trigger.ts`).** Ohne die Sperre könnte ein zweiter Klick auf denselben
    Auslöser einen zweiten Flow starten, dessen Dialog sich auf den ersten stapelt, bevor die erste
    Klassifikation überhaupt zurück ist. Weder 6.3 noch 17 K3 nennen dieses Signal; es liegt neben
    Arbiter und Token als dritte Sperre am Einstieg.
14. **Das Token wird zweimal mehr geprüft als 6.3 aufzählt.** 6.3 nennt die Token-Prüfung einmal, vor
    dem Öffnen des Dialogs. Umgesetzt ist sie zusätzlich nach der Bestätigung im Dialog (bevor der
    Frischcheck liest) und noch einmal nach dem Frischcheck (bevor der Lauf startet) — beide Male mit
    erneutem Prompt, falls das Token in der Zwischenzeit gelöscht wurde, und beide Male gefolgt von
    einer erneuten Arbiter-Prüfung (`undo-flow.ts`, `confirmStart`/`startChecked`).
15. **Die Weiche bekommt einen eigenen Nichtverfügbarkeits-Schlüssel, `restore.import.choice.undo.unavailable`**,
    statt den generischen `editable | notSelectable | notEditable | unavailable`-Zustand aus 6.1 nur
    intern zu tragen — die Wahl „Ersetzungen rückgängig machen" erklärt sich selbst, wenn sie
    ausgegraut ist.
16. **`restore.import.sorts.transferRun` ist umformuliert, nicht nur um `transferUndo` ergänzt (6.7).**
    6.7 sah nur einen neuen Schlüssel für die vierte Einlesesorte vor; der bestehende Sortierlabel für
    eine Übertragungsdatei nennt jetzt auch „Ersetzungen rückgängig machen", weil dieselbe Datei seit
    T6 beide Wege bedient.
17. **Vier `undo.errors.*`-Schlüssel, die 6.7 nicht auflistet:** `restoreIncomplete`, `unknownOutcome`
    (beide für einen verlorenen ADD ohne verwertbares Nachlesen, F20/E24) sowie `skippedDrift` und
    `recheckUnavailable` (die Fehlertexte des Frischchecks vor einem REMOVE, E19) — `undo.errors.${reason}`
    liest jeden `UndoInRunSkipReason`/verlorenen Ausgang generisch, nicht nur die in 6.7 benannten.
18. **`UndoRunTarget` trägt drei Felder mehr als 6.5 aufzählt: `trackedChannelName`,
    `ownerDisplayName`, `sourceFile`.** 6.5 nennt nur `setId`, `expectedChannelName`,
    `hostChannelName`, `setName`, `ownerOrChannelLabel` — Anzeige und Lauf-Adressierung. Die drei
    weiteren tragen ausschließlich Werte, die erst das `finished`-Protokoll (`buildUndoRunProtocol`)
    braucht und die nach dem Start niemand sonst mehr hereinreichen könnte (`meta.targetChannelName`,
    `meta.targetOwnerDisplayName`, `meta.undoneFile`).
19. **`sevenTvRun.kind.undo` und sein Eintrag in `SEVEN_TV_RUN_KIND_LABEL_KEY`** kommen aus #256s
    Vertrag (Plan Abschnitt 11), nicht aus dieser Spec — der Compiler erzwingt beides, sobald der
    Arbiter eine vierte Laufart kennt.
