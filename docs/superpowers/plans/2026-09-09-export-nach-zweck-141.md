# Export-Dialog nach Zweck sortieren — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Schritte sind als Checkbox (`- [ ]`) geführt.

**Ziel:** „Als Datei speichern" verlässt den 7TV-Ziel-Dialog; der Export-Dialog bekommt die
Emote-Liste als dritte Zeile und sortiert seine Optionen nach Zweck statt nach Format.

**Vorgehen:** Additiv vor destruktiv. Der Export-Dialog lernt zuerst die vom Aufrufer übergebene
Optionsliste (T2), die Nutzungsstatistik bekommt darüber den Datei-Weg (T4), und **erst danach**
verliert der Ziel-Dialog seinen Datei-Radio (T5). Kein Commit auf dem Branch hat den Datei-Export
unerreichbar.

**Stack:** Angular 22 (Standalone, Signals, zoneless), CDK-`Dialog` über `openAppDialog`, Transloco,
Tailwind, Vitest, Playwright.

**Spec:** [`docs/superpowers/specs/2026-09-09-export-nach-zweck-design.md`](../specs/2026-09-09-export-nach-zweck-design.md) — der Plan
argumentiert aus ihr; **beide zusammen lesen.** Die Entscheidungen heißen dort E1–E7.

**Issue:** [#141](https://github.com/emotepurge/EmotePurge/issues/141)

## Globale Randbedingungen

- **Kein fertiger Code in diesem Plan** (globale `CLAUDE.md`). Verträge und Signaturen ja, Rümpfe
  nein — was nur abzutippen wäre, wird implementiert, nicht geplant.
- **Frontend-only.** Kein Worker-, Api- oder Migrationscode. Messungsneutral gegenüber Epic #118.
- **Wortlaut ist Vertrag** (E2). Die deutschen und englischen Strings stehen in der Spec-Tabelle und
  sind wörtlich zu übernehmen, nicht zu paraphrasieren.
- **Regel 3:** Ein Commit, der einen Vertrag ändert, trägt seine Doku im selben Commit. Deshalb
  gehört §7.4 in T2 und §7.2/§8.7/DECISIONS in T5 — **kein** eigener Doku-Commit am Ende.
- **Regel 12:** Verhalten ja, Vorlage nein. Keine Tailwind-Ketten, keine Verschachtelung, keine
  Snapshots, kein Übersetzungswortlaut als Selbstzweck.
- **Regel 18:** `npm --prefix web run format` vor jedem Commit.
- **Sprache:** Bezeichner und Kommentare in neuem Code englisch; Doku deutsch; Commit-Messages
  englisch, Conventional Commits.
- **Reihenfolge in `ExportDialogData.options` ist die Anzeigereihenfolge**, und `options[0]` ist die
  Vorbelegung. Nirgends eine zweite Default-Regel einführen.

---

## Task-Reihenfolge und Parallelität

| Welle | Tasks | Warum hier |
|---|---|---|
| A | T1 | Alle Specs und E2E-Fälle danach brauchen die Schlüssel |
| B | T2 | Definiert den Vertrag, auf den T3 und T4 schalten |
| C | ~~T3~~ · T4 | T3 in T2 aufgegangen (s. Korrektur dort), T4 baut auf T2 auf |
| D | T5 | Entfernt den alten Weg, erst nachdem der neue steht |
| E | T6 | Gates und E2E gegen den fertigen Stand |

---

### Task 1: i18n-Schlüssel in beiden Locales

**Dateien:** `web/public/i18n/de.json` · `web/public/i18n/en.json`

**Erzeugt:** die Schlüssel, auf die T2, T4 und T6 sich beziehen.

Neu unter dem bestehenden **top-level** `export`-Objekt (nicht unter `restore.import` — in beiden
Dateien existieren zwei verschiedene `import`-Objekte, das ist die Falle):

| Schlüssel | de | en |
|---|---|---|
| `export.purposeLabel` | Wofür brauchst du die Datei? | What do you need the file for? |
| `export.purposeAnalyse` | Zahlen auswerten | Analyse the numbers |
| `export.purposeAnalyseHint` | Nutzungsstatistik als CSV | Usage statistics as CSV |
| `export.purposeProcess` | Zahlen weiterverarbeiten | Process the numbers further |
| `export.purposeProcessHint` | Nutzungsstatistik als JSON | Usage statistics as JSON |
| `export.purposeReimport` | Emotes später wieder einlesen | Import the emotes again later |
| `export.purposeReimportHint` | Emote-Liste als JSON | Emote list as JSON |

`export.formatLabel`, `export.formatCsv`, `export.formatJson` **bleiben unverändert** — die
Voting-Seite und das Löschprotokoll benutzen sie weiter. `export.formatJson` trägt seit #92 den
Zusatz „(Datenauszug)"; nicht anfassen.

`import.target.saveAsFile` wird **noch nicht** gelöscht — der Radio existiert bis T5. Die Löschung
gehört in T5, sonst ist der Zwischenstand kaputt.

- [ ] Schlüssel in beide Dateien einfügen, Reihenfolge im JSON an den Nachbarn ausrichten
- [ ] `npm --prefix web test -- --watch=false` — der i18n-Parität-Spec muss grün bleiben
- [ ] Commit: `feat(i18n): add the purpose-sorted export wording`

---

### Task 2: `ExportDialog` nimmt seine Optionen vom Aufrufer

**Dateien:** Ändern `web/src/app/shared/export/export-dialog.ts` · Neu
`web/src/app/shared/export/export-dialog.spec.ts` · Ändern `docs/UI-Designsprache.md` (§7.4 neu)

**Verbraucht:** die Schlüssel aus T1.

**Erzeugt** (exakt diese Namen benutzen T3 und T4):

```ts
export interface ExportDialogOption { readonly id: string; readonly labelKey: string; readonly hintKey?: string; }
export interface ExportDialogData<TId extends string = string> { rowCount: number; filtered: boolean; selectionCount: number; noticeKeys: readonly string[]; optionsLegendKey: string; options: readonly (ExportDialogOption & { id: TId })[]; }
export interface ExportChoice<TId extends string = string> { optionId: TId; scope: ExportScope; }
export const FORMAT_EXPORT_OPTIONS: readonly ExportDialogOption[];   // csv, dann json
export function openExportDialog<TId extends string>(dialog: Dialog, data: ExportDialogData<TId>): DialogRef<ExportChoice<TId> | undefined>;
```

`ExportScope` (`'visible' | 'selection'`) bleibt unverändert exportiert — `import-target-dialog.ts`
importiert ihn von hier. `ExportFormat` entfällt ersatzlos; vorher `grep -rn "ExportFormat" web/src`
laufen lassen und alle Fundstellen mitziehen.

**Verhalten (E1, E2):**

- Vorbelegung ist `options[0].id`. Der Scope-Default bleibt `visible` samt seinem bestehenden
  Kommentar.
- Die Optionsgruppe trägt `optionsLegendKey` als Legende und rendert je Option `labelKey`, darunter
  `hintKey`, falls gesetzt. Beide Texte stehen **innerhalb** des `<label>` und damit im zugänglichen
  Namen — Hausmuster, siehe „(Kanal muss erst beitreten)" im Ziel-Dialog.
- Markup: `flex items-center` → `flex items-start`, Textpaar in einem `flex flex-col`. Keine geteilte
  Radio-Komponente einführen.
- Scope-Gruppe, Zeilenzahl, `filteredHint` und die Notice-Banner bleiben unverändert.

**Spec-Datei** (Regel 12 — Verhalten, nicht Vorlage). Fälle:

- [ ] Vorbelegung ist die erste übergebene Option
- [ ] Optionen erscheinen in der übergebenen Reihenfolge
- [ ] Eine Option mit `hintKey` trägt beide Zeilen im zugänglichen Namen; eine ohne trägt nur eine
- [ ] Scope-Gruppe fehlt bei `selectionCount === 0`, ist da bei `> 0`
- [ ] Die angezeigte Zeilenzahl folgt dem gewählten Scope
- [ ] Absenden schließt mit `{ optionId, scope }`, Abbrechen mit `undefined`
- [ ] `optionsLegendKey` wird als Legende benutzt (mit einem anderen Wert als dem Default prüfen)

Attrappe für Transloco wie in `import-target-dialog.spec.ts` — dort steht das Hausmuster.

**Doku im selben Commit:** neuer Abschnitt **§7.4 „Export-Dialog (Zweck statt Format)"** in
`docs/UI-Designsprache.md`, direkt nach §7.3. Inhalt: die Zeilenreihenfolge (Exportumfang, dann
Optionsgruppe, dann Zeilenzahl/Hinweis, dann Notice-Banner, dann Abbrechen/Exportieren), dass die
Optionsliste vom Aufrufer kommt, der Zweck-Wortlaut der Nutzungsstatistik als Vertrag, und dass
CSV/JSON für Voting und Löschprotokoll bleibt. **§7.4 ist frei — nichts renummerieren** (die Lehre
aus #80: §8.6 durfte nicht umgewidmet werden, weil sechs Stellen darauf zeigen).

- [ ] Spec zuerst schreiben, gegen den alten Dialog laufen lassen, Fehlschlag bestätigen
- [ ] Dialog umbauen, Spec grün
- [ ] `npm --prefix web test -- --watch=false` gesamt grün (T3/T4-Aufrufer sind hier noch rot —
      **das ist erwartet**, sie kommen in Welle C; den Build erst nach T4 als Ganzes bewerten)
- [ ] Commit: `feat(export): let callers supply the export dialog's option list`

> **Korrektur vom 2026-09-09, nach T1:** Die erste Fassung wollte die drei Aufrufer erst in Welle C
> nachziehen. Das geht nicht — `ng test` typprüft den ganzen Workspace, ein Commit mit `choice.format`
> an drei Stellen kompiliert also gar nicht, und die Suite liefe nicht einmal an. **T2 zieht die drei
> Aufrufer deshalb mechanisch mit** (`FORMAT_EXPORT_OPTIONS`, `optionsLegendKey: 'export.formatLabel'`,
> Verzweigung auf `optionId`) — bei der Nutzungsstatistik ausdrücklich nur als Zwischenstand mit
> unveränderten CSV/JSON-Optionen. **T3 entfällt damit**, T4 baut den Zwischenstand zur Zweck-Liste um.

---

### Task 3 — entfallen, in T2 aufgegangen

**Grund:** siehe die Korrektur in T2 — der Workspace kompiliert sonst zwischen T2 und T3 nicht.
Der ursprüngliche Inhalt steht unten und ist als Prüfliste für T2 weiter gültig.

#### Ursprünglicher Task 3: Die zwei unveränderten Aufrufer auf die neue Signatur

**Dateien:** `web/src/app/features/voting/vote-session-detail-page.ts` (~Z. 574–590) ·
`web/src/app/shared/seven-tv/mass-delete-panel.ts` (~Z. 305–320)

**Verbraucht:** `FORMAT_EXPORT_OPTIONS`, `ExportDialogData`, `ExportChoice` aus T2.

Beide behalten ihre CSV/JSON-Wahl unverändert. Je Aufrufer:

- `options: FORMAT_EXPORT_OPTIONS` und `optionsLegendKey: 'export.formatLabel'` ergänzen
- Verzweigung von `choice.format === 'csv'` auf `choice.optionId === 'csv'` umstellen
- `selectionCount: 0` und die vorhandenen Kommentare bleiben, wie sie sind
- Im Mass-Delete-Panel bleibt `protocolSaved.set(true)` an „irgendeine Wahl getroffen" gebunden,
  nicht an ein bestimmtes Format

Kein neuer Spec. Beide Stellen sind dünne Aufrufer ohne eigene Entscheidungslogik; ihr Verhalten
hängt an T2s Vertrag, der dort geprüft wird.

- [ ] Beide Aufrufer umstellen
- [ ] `npm --prefix web test -- --watch=false`
- [ ] Commit: `refactor(export): move the remaining callers to the option list`

---

### Task 4: `openExport` friert seinen Umfang ein und bietet die Emote-Liste an

**Dateien:** `web/src/app/features/usage-stats/usage-stats-page.ts` — `openExport()` ab Z. 1161

**Verbraucht:** T1s Schlüssel, T2s Vertrag.

Der inhaltlich dichteste Task. Drei Dinge, die zusammengehören:

**(a) Erfassung vor dem Öffnen (E4).** Heute liest `openExport` `rows`, `filtered` und den Scope erst
in der `closed`-Subscription. Das wird auf dasselbe Muster umgestellt, das `openImportTarget`
(Z. 1210–1247) bereits benutzt und dort ausführlich begründet: `channelName`, `emoteSetId`,
`filtered`, die Auswahl- **und** die Sichtbar-Zeilen werden vor dem Öffnen gelesen, danach nur noch
aus dieser Erfassung. Grund im Kommentar festhalten: die Seite lädt bei `usageFlushed` und
`channel.synced` weiter, und die verschlüsselte Auswahl überlebt das absichtlich.

`trendFor` bleibt eine Live-Rückrufe — nicht mit einfrieren (Begründung in der Spec, E4).

**(b) Zweck-Optionen.** `optionsLegendKey: 'export.purposeLabel'`, Optionen in dieser Reihenfolge:

| `id` | `labelKey` | `hintKey` |
|---|---|---|
| `usage-csv` | `export.purposeAnalyse` | `export.purposeAnalyseHint` |
| `usage-json` | `export.purposeProcess` | `export.purposeProcessHint` |
| `emote-list` | `export.purposeReimport` | `export.purposeReimportHint` |

Die dritte Zeile wird **nur angehängt**, wenn `activeEmoteSetId() !== null && importScopeCurrent()`
(E3) — die ersten beiden hängen nicht davon ab.

**(c) Der Emote-Listen-Zweig.** Baut aus der Erfassung `buildEmoteListEnvelope({channelName,
emoteSetId, scope, rows})` und lädt über `emoteListFilename` / `emoteListJson` / `JSON_MIME` herunter
— **identisch zu dem, was `startImportFromChoice` heute im Datei-Zweig tut.** Umschlag und Dateiname
ändern sich nicht. Die Zeilen laufen wie dort durch `toImportRow` und `dedupeImportRows`.

`startImportFromChoice` bleibt in diesem Task **unangetastet**; sein Datei-Zweig verschwindet in T5.
Auf dem Branch existieren zwischen T4 und T5 beide Wege — gewollt.

**Scope-Vorbelegung:** Die Emote-Liste erbt `visible`. Das ist am 2026-09-09 ausdrücklich entschieden
(Spec, „Beantwortete Fragen" Nr. 1) und **keine offene Frage** — nicht auf `selection` drehen.

- [ ] Erfassung umstellen, Kommentar mit der Begründung setzen
- [ ] Optionsliste bauen, dritte Zeile bedingt anhängen
- [ ] Emote-Listen-Zweig ergänzen
- [ ] `npm --prefix web test -- --watch=false`
- [ ] **Regel 16 gilt hier nicht** (kein Backend), aber im Browser gegen die laufende Api ansehen:
      alle drei Zwecke einmal auslösen, die drei Dateien öffnen
- [ ] Commit: `feat(usage-stats): offer the emote list in the export dialog`

---

### Task 5: Der Ziel-Dialog verliert sein Datei-Ziel

**Dateien:** `web/src/app/shared/seven-tv/import-target-dialog.ts` (Radiogruppe Z. 108–148) ·
`web/src/app/shared/seven-tv/import-target-dialog.spec.ts` ·
`web/src/app/features/usage-stats/usage-stats-page.ts` (`startImportFromChoice` ab Z. 1284) ·
`web/public/i18n/de.json` + `en.json` · `docs/UI-Designsprache.md` (§7.2, §8.7) · `docs/DECISIONS.md`

**Verbraucht:** T4 — der Datei-Weg muss im Export schon stehen, bevor er hier verschwindet.

**Code:**

- Der hartverdrahtete Datei-Radio hinter dem `@for` entfällt, ebenso `selectFile()` und der
  `{kind:'file'}`-Zweig von `TargetSelection`.
- `ImportTargetChoice` verliert die Ein-Varianten-Union (E7):
  `{ scope: ExportScope; channelName: string }`. `target()` intern bleibt nullbar, weil „noch nichts
  gewählt" weiter existiert und „Weiter" sperrt.
- `startImportFromChoice` verliert den `kind === 'file'`-Zweig samt frühem `return` und ruft nur
  noch `startImportFlow`. **`ImportSource.origin` nicht anfassen** — dort ist `{kind:'file'}` die
  Herkunft eines *eingelesenen* Umschlags (§7.3) und bleibt erreichbar.
- `import.target.saveAsFile` aus **beiden** Locale-Dateien löschen.

**Spec anpassen:**

- Der Fall „rendert die Datei-Option, gelistet hinter jedem Kanal" entfällt ersatzlos.
- Die Erwartungen auf `targetInputCount()` sinken um eins; die DOM-Index-Annahmen (`toHaveLength(2)`,
  `inputs[1]`) sind entsprechend zu korrigieren.
- Der Fall „nur die Datei-Option, wenn kein Kanal in Frage kommt" wird zu: **leere Ziel-Gruppe,
  `import.target.none`-Meldung, „Weiter" gesperrt** (E5, am 2026-09-09 bestätigt).
- `import.target.saveAsFile` aus der lokalen Übersetzungsattrappe entfernen.

**Doku im selben Commit (Regel 3):**

- **§7.2**, Zeilenreihenfolge Punkt 3: der Halbsatz „, zuletzt ein Radio ‚Als Datei speichern'"
  entfällt. Der Absatz zur Scope-Asymmetrie („Nicht angleichen") **bleibt** und bekommt einen Satz:
  der Datei-Weg liegt jetzt im Export-Dialog und erbt dessen `visible`. Die Zeilenreihenfolge des
  *Bestätigungs*-Dialogs bleibt unberührt — ihre Herkunftszeile „Kanal oder Datei" beschreibt das
  Einlesen.
- **§8.7:** ein Satz, dass „Übertragen" und „Exportieren" nach dieser Runde zwei echte Absichten
  sind statt zweier Namen für eine überlappende. **Keine Regeländerung.** Der Beispielabsatz zur
  fehlenden Export-Kurzform bleibt gültig — er stützt sich auf die Scope-Asymmetrie, und die bleibt.
- **`docs/DECISIONS.md`:** neuer Eintrag **ganz oben** (absteigend nach Datum, über den beiden
  bestehenden 2026-09-09-Einträgen), Format wie die Nachbarn: `### 2026-09-09 — <Satz>`, dann
  `**Betrifft:**` mit den Pfaden, dann Absätze mit fettem Auftakt, abgeschlossen mit `---`.
  Muss ausdrücklich benennen: die #92-Begründung — dort wörtlich „das Kommando kann als Ziel einen
  Kanal *oder* eine Datei haben" — **entfällt**, der Name „Übertragen…" bleibt trotzdem, nun aus dem
  einfacheren Grund, dass er genau einen Transportweg beschreibt. Ältere Einträge werden **nicht**
  nachgebessert.

- [ ] Datei-Radio und Typ entfernen, `startImportFromChoice` vereinfachen
- [ ] Locale-Schlüssel löschen
- [ ] Spec anpassen
- [ ] §7.2, §8.7, DECISIONS schreiben
- [ ] `npm --prefix web test -- --watch=false`
- [ ] Commit: `feat(import): drop the file destination from the target picker`

---

### Task 6: E2E, Audit-Basislinie und Gates

**Dateien:** `web/e2e/emote-import.e2e.spec.ts` (~Z. 211) · ggf. `web/e2e/audit/ui-audit.audit.ts`

- Die Zusicherung `getByRole('radio', { name: /Als Datei speichern/ })` im Ziel-Dialog entfällt dort
  und wandert in den Export-Dialog, wo sie gegen die neue Zweckzeile matcht. Playwright berechnet den
  zugänglichen Namen aus dem gesamten Label-Inhalt — die Hinweiszeile ist Teil davon (E2), ein
  Teilstring-Regex trifft trotzdem.
- Die Scope-Vorbelegungs-Prüfung im Ziel-Dialog (`'Auswahl (2)'` ist gewählt) **bleibt unverändert**;
  sie belegt R12 weiter.
- Der Audit-Fall `usage-stats-export-dialog` bekommt eine neue Screenshot-Basislinie. Erwartet, kein
  Fehlschlag.

**Gates — erst hier, gegen den fertigen Stand:**

- [ ] `npm --prefix web test -- --watch=false`
- [ ] `npm --prefix web run e2e` — **nur, wenn auf `:5151` keine Api lauscht.** Läuft dort eine,
      fallen rund die Hälfte der Fälle mit irreführendem „element not found" durch
- [ ] `dotnet test EmotePurge.slnx` (unberührt, aber Gate; braucht laufendes Docker)
- [ ] `node scripts/coverage-local.mjs` — misst nur Committetes, also nach dem letzten Commit
- [ ] **Am Gerät ansehen:** die dreizeilige Zweckgruppe als Bottom-Sheet auf grobem Zeiger
      (`docs/Testumgebung-Mobile-2026-08-07.md`), weil der Audit nie unter 768 px misst (#111)
- [ ] Commit: `test(export): move the file-destination case to the export dialog`

---

## Nach dem letzten Task

- [ ] Branch pushen, PR gegen `main` aufmachen (Regel 1 — Merge gehört dem Nutzer)
- [ ] `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` — `--scope` und `--base`
      **explizit**, sonst reviewt Codex den Working Tree bzw. `main` gegen `origin/main` und
      entwarnt fälschlich
- [ ] Das Codex-Ergebnis dem Nutzer **unverändert vorlegen**, nicht selbst umsetzen und nicht
      stillschweigend gegen ein eigenes Review auflösen
