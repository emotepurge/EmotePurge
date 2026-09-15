# Export-Dialog nach Zweck sortieren — Entwurf

**Datum:** 2026-09-09 · **Issue:** [#141](https://github.com/emotepurge/EmotePurge/issues/141) · **Status:** entworfen, noch nicht geplant · **Vorgeschichte:** [#80 Aktionszeile](../../designs/Aktionszeile-80-2026-09-06.md), #92 (Verb „Übertragen…")

## Warum jetzt

Im Seitenkopf der Nutzungsstatistik erzeugen **zwei** Kommandos eine Datei: „Exportieren"
(Nutzungszahlen als CSV oder JSON) und „Übertragen…", dessen Ziel-Dialog als letzte Zeile der
Radiogruppe *„Zielkanal"* den Eintrag „Als Datei speichern" trägt. Der Eintrag ist kein Kanal, und
die Datei, die er erzeugt, ist eine dritte Sorte neben den zwei Exportformaten.

Das ist keine neue Beobachtung: Das #80-Konzept hat genau diese Ursache benannt („zwei Kommandos im
Seitenkopf erzeugen je eine Datei, die derselbe Importer frisst") und sie durch **Umbenennen**
entschärft — aus „In Kanal kopieren…" wurde „Übertragen…" (#92). Der Nutzer sagt am 2026-09-09:
Umbenennen hat nicht gereicht.

Die Gestaltung ist am 2026-09-09 anhand eines Mockup-Vergleichs von vier Varianten **entschieden**.
Dieser Entwurf spezifiziert sie, er entwirft sie nicht neu. Die verworfenen Alternativen stehen in
Abschnitt „Ausdrücklich nicht in dieser Runde" und sind nicht wieder aufzurollen.

## Ist-Zustand, verifiziert am 2026-09-09

| Ort | Befund |
|---|---|
| `shared/export/export-dialog.ts` | 163 Zeilen, CDK-`Dialog` über `openAppDialog`. `ExportDialogData = { rowCount, filtered, selectionCount, noticeKeys }`, Ergebnis `ExportChoice = { format: 'csv' \| 'json'; scope: 'visible' \| 'selection' }`. Zwei handgeschriebene Radiogruppen, Format **fest verdrahtet**. Default `scope='visible'`, `format='csv'`. **Keine eigene Spec-Datei** — als einzige Datei in `shared/export/` |
| Aufrufer des `ExportDialog` | **drei**: `usage-stats-page.ts:1161` (`openExport`), `voting/vote-session-detail-page.ts:581`, `shared/seven-tv/mass-delete-panel.ts:312` (Protokoll nach einem Löschlauf, setzt zusätzlich `protocolSaved`) |
| `shared/seven-tv/import-target-dialog.ts` | Ziel-Radiogruppe Z. 108–148; der Datei-Radio (Z. 138–147) steht **hartverdrahtet hinter** dem `@for` über `options()`, ist also auch bei null Kanälen da. Ergebnis `ImportTargetChoice = { scope; target: {kind:'channel'; channelName} \| {kind:'file'} }` |
| Aufrufer des `ImportTargetDialog` | **einer**: `usage-stats-page.ts:1238` (`openImportTarget`), Fortsetzung `startImportFromChoice` ab Z. 1284 mit dem Datei-Zweig als frühem `return` |
| Scope-Default | `ExportDialog` → `visible`; `ImportTargetDialog` → `selection` (bei vorhandener Auswahl). §7.2 hält die Asymmetrie als Absicht fest: „Nicht angleichen" |
| Erfassungsdisziplin | `openImportTarget` friert `channelName`, `emoteSetId`, `selection` und `visible` **vor** dem Öffnen ein (langer Docstring: die Seite lädt während des Dialogs weiter). `openExport` liest die Zeilen dagegen **nach** dem Schließen |
| Radio-Bausteine | Es gibt **keine** geteilte Radio-Komponente. Beide Dialoge schreiben `<label class="flex items-center gap-2 py-1">` mit einzeiligem Text von Hand. Eine zweite Zeile unter dem Label gibt es heute nirgends |
| `shared/export/emote-list-export.ts` | `buildEmoteListEnvelope({channelName, emoteSetId, scope, rows})`, `emoteListJson`, `emoteListFilename`. Umschlag `kind:'emote-list'`, `formatVersion: 1`. **Bleibt unverändert** |
| Tests | `import-target-dialog.spec.ts` prüft „Datei-Radio steht immer zuletzt" per DOM-Index (`toHaveLength(2)`, `inputs[1]`); `e2e/emote-import.e2e.spec.ts:211` matcht `/Als Datei speichern/` im Ziel-Dialog; `e2e/audit/ui-audit.audit.ts` hat den Fall `usage-stats-export-dialog` (Screenshot-Basislinie) |

Zwei Befunde gehen über die Übergabe hinaus und prägen den Entwurf — E4 und E5.

## Entscheidungen

### E1 — Die Optionsliste kommt vom Aufrufer, der Dialog verdrahtet nichts mehr

Der `ExportDialog` hat drei Aufrufer; die Zweck-Liste gilt **nur** für die Nutzungsstatistik. Der
Dialog bekommt die Optionen deshalb übergeben und behandelt ihre Bedeutung als undurchsichtig:

```ts
export interface ExportDialogOption {
  readonly id: string;        // für den Dialog bedeutungslos, der Aufrufer schaltet darauf
  readonly labelKey: string;  // erste Zeile
  readonly hintKey?: string;  // zweite Zeile, optional
}

export interface ExportDialogData {
  rowCount: number;
  filtered: boolean;
  selectionCount: number;
  noticeKeys: readonly string[];
  optionsLegendKey: string;                    // 'export.formatLabel' | 'export.purposeLabel'
  options: readonly ExportDialogOption[];      // mindestens eine, Reihenfolge ist die Anzeige
}

export interface ExportChoice { optionId: string; scope: ExportScope; }
```

Vorbelegt ist **`options[0].id`** — das erhält „CSV zuerst" für die zwei unveränderten Aufrufer und
„Zahlen auswerten" (= CSV) für die Nutzungsstatistik, ohne einen zweiten Default-Begriff einzuführen.

Voting-Detailseite und Mass-Delete-Panel bekommen eine geteilte Konstante, damit die
CSV/JSON-Formulierung an einer Stelle steht:

```ts
export const FORMAT_EXPORT_OPTIONS: readonly ExportDialogOption[] = [
  { id: 'csv', labelKey: 'export.formatCsv' },
  { id: 'json', labelKey: 'export.formatJson' },
];
```

Die Fabrik `openExportDialog` wird über die Id generisch (`<TId extends string>`), damit ein
Aufrufer sein `switch` erschöpfend prüfen lassen kann; die Komponente selbst arbeitet weiter mit
`string`. Ohne das fiele eine vertippte Id stumm auf „nichts passiert" durch — der Preis sind zwei
Typparameter an genau einer Funktion.

Verworfen: `format` als drittes Enum-Mitglied (`'csv' | 'json' | 'emote-list'`) mit fester Liste im
Dialog. Das hätte die Emote-Liste auch der Voting-Seite und dem Löschprotokoll angeboten, wo sie
nichts bedeutet.

### E2 — Der Wortlaut ist Vertrag, die zweite Zeile gehört in den zugänglichen Namen

Deutsch, verbindlich:

| Schlüssel | Erste Zeile | Zweite Zeile |
|---|---|---|
| `export.purposeLabel` | *Legende:* Wofür brauchst du die Datei? | — |
| `export.purposeAnalyse` / `…Hint` | Zahlen auswerten | Nutzungsstatistik als CSV |
| `export.purposeProcess` / `…Hint` | Zahlen weiterverarbeiten | Nutzungsstatistik als JSON |
| `export.purposeReimport` / `…Hint` | Emotes später wieder einlesen | Emote-Liste als JSON |

Englisch: „What do you need the file for?" · „Analyse the numbers" / „Usage statistics as CSV" ·
„Process the numbers further" / „Usage statistics as JSON" · „Import the emotes again later" /
„Emote list as JSON".

Die Hinweiszeile steht **innerhalb** des `<label>` und ist damit Teil des zugänglichen Namens
(„Zahlen auswerten Nutzungsstatistik als CSV"). Das ist kein Zufall, sondern das Hausmuster: der
Ziel-Dialog hängt „(Kanal muss erst beitreten)" genauso in den Namen, und der E2E-Fall matcht ihn
dort bereits als Ganzes. Ein `aria-describedby` wäre die Alternative, träfe aber eine
Abweichung ohne Anlass.

Markup: das `<label>` wird von `flex items-center` auf `flex items-start` umgestellt, der Text in
ein `flex flex-col`-Paar. `export-dialog.ts` bekommt damit als einzige Datei ein zweizeiliges
Radio-Label; eine geteilte Radio-Komponente wird **nicht** eingeführt (drei Vorkommen sind kein
Muster, und §7.2/§7.4 halten die Reihenfolgen ohnehin als Text fest).

### E3 — Die Emote-Liste wird nur angeboten, wenn sie erfüllbar ist

`buildEmoteListEnvelope` braucht `emoteSetId`. `openExport` kennt den heute nicht; `openImportTarget`
holt ihn aus `activeEmoteSetId()` und bricht bei `null` oder `!importScopeCurrent()` ab
(`usage-stats-page.ts:1211-1212`).

Der Export übernimmt diese Prüfung, aber **als Sichtbarkeitsregel statt als Abbruch**: die dritte
Zeile steht nur in der Liste, wenn `activeEmoteSetId() !== null && importScopeCurrent()`. Die ersten
beiden Zeilen sind davon unberührt — Nutzungszahlen exportiert man auch ohne aktives 7TV-Set.

Verworfen: die Option immer anzeigen und beim Absenden scheitern lassen. Das ist genau die Falle,
wegen der die Emote-Liste kein CSV bekommt (E6) — ein Angebot, das erst nach der Entscheidung
zurückgenommen wird.

### E4 — `openExport` friert seinen Umfang vor dem Öffnen ein

**Neu gegenüber der Übergabe.** `openExport` liest heute `rows`, `filtered` und den Scope erst in der
`closed`-Subscription; ein Kommentar begründet das damit, dass jede Zeilenentfernung auch die Auswahl
mitpflegt. `openImportTarget` macht das Gegenteil, mit ausführlicher Begründung: die
Nutzungsstatistik lädt bei `usageFlushed` oder `channel.synced` neu, während der Dialog offen ist,
und die verschlüsselte Auswahl überlebt das absichtlich — ein Nachlesen paart dann einen
eingefrorenen `emoteSetId` mit anderen Zeilen, ohne dass man es sieht.

Sobald der Datei-Weg im Export-Dialog liegt, gilt diese Begründung für den Export mit. `openExport`
erfasst deshalb **vor** dem Öffnen: `channelName`, `emoteSetId`, `filtered`, die Auswahl- und die
Sichtbar-Zeilen; alle drei Zweige lesen danach nur noch aus dieser Erfassung.

Das ändert auch die beiden Zahlen-Zweige — und zwar zum Besseren: der Dialog zeigt seine Zeilenzahl
ohnehin aus dem Stand beim Öffnen (`rowCount` in `ExportDialogData`). Heute kann die Datei mehr oder
weniger Zeilen enthalten, als der Dialog angekündigt hat; nach der Änderung nicht mehr.

Nicht erfasst wird `trendFor` — die Trendspalte wird beim Serialisieren aus dem Live-Zustand
gerechnet. Das bleibt so; die Alternative wäre, pro Zeile einen Trend mit einzufrieren, ohne dass
jemand den Unterschied bemerken könnte.

### E5 — Ohne wählbaren Kanal ist „Übertragen…" ein Sackgassen-Dialog, und das ist richtig

**Neu gegenüber der Übergabe.** Der Datei-Radio ist heute die einzige Zeile, die der Ziel-Dialog
*immer* zeigt. Fällt er weg, kann die Ziel-Radiogruppe leer sein — der Fall existiert bereits als
Meldung (`import.target.none`, „Kein weiterer Kanal, in dem du Broadcaster oder 7TV-Editor bist."),
war aber bisher nie das ganze Dialoginnere.

Das bleibt so: Meldung, leere Gruppe, „Weiter" gesperrt (`target() === null`). Ein Nutzer ohne
zweiten Kanal konnte „Übertragen…" bisher als Datei-Ausgang benutzen; künftig ist der Datei-Ausgang
„Exportieren". Das ist die beabsichtigte Trennung, kein Verlust — beide Kommandos stehen
nebeneinander im selben Seitenkopf.

### E6 — Die Emote-Liste gibt es nur als JSON

Der Importer liest ausschließlich JSON-Umschläge (`read-envelope.ts`, `import-source-parser.ts`).
Ein CSV-Angebot wäre eine Falle, die erst beim Importversuch auffällt. Deshalb ist „Emotes später
wieder einlesen" **eine** Zeile und nicht zwei — die Zweck-Sortierung macht das nebenbei
selbstverständlich, weil ein Zweck kein Format erzwingt.

### E7 — `ImportTargetChoice` verliert seine Ein-Varianten-Union

Mit dem Datei-Zweig entfällt der Diskriminator:

```ts
export interface ImportTargetChoice { scope: ExportScope; channelName: string; }
```

`startImportFromChoice` verliert den `kind === 'file'`-Zweig samt frühem `return` und ruft nur noch
`startImportFlow`. `ImportSource.origin` bleibt unangetastet — dort ist `{kind:'file'}` die
*Herkunft* eines eingelesenen Umschlags (§7.3) und weiterhin erreichbar.

## Was zwingend mitzieht

Kein stiller Refactor. Alles im selben Commit wie die Codeänderung (Regel 3):

- **`docs/UI-Designsprache.md` §7.2**, Zeilenreihenfolge Punkt 3: der Halbsatz „, zuletzt ein Radio
  ‚Als Datei speichern'" entfällt. Der Absatz zur Scope-Asymmetrie („Nicht angleichen") **bleibt**
  und bekommt einen Satz, dass der Datei-Weg jetzt im Export-Dialog liegt und dessen `visible` erbt.
  Die Zeilenreihenfolge des *Bestätigungs*-Dialogs bleibt unberührt — ihre Herkunftszeile „Kanal
  oder Datei" beschreibt das Einlesen, nicht das Schreiben.
- **`docs/UI-Designsprache.md` §7.4 (neu)** hält die Zeilenreihenfolge und den Zweck-Wortlaut des
  Export-Dialogs als Vertrag fest — bisher war sie nirgends festgeschrieben, obwohl §8.7 auf
  `export-dialog.ts:139-141` verweist. §7 hat heute 7.1–7.3; **7.4 ist frei und renummeriert
  nichts.** (Die Lehre aus #80: §8.6 durfte nicht umgewidmet werden, weil sechs Stellen darauf
  zeigen.)
- **§8.7** wird *bestätigt*, nicht geändert: ein Satz, dass „Übertragen" und „Exportieren" nach
  dieser Runde zwei echte Absichten sind statt zweier Namen für eine überlappende. Der bestehende
  Beispielabsatz zur fehlenden Export-Kurzform bleibt gültig — er stützt sich auf die
  Scope-Asymmetrie, und die bleibt.
- **`docs/DECISIONS.md`**, neuer Eintrag ganz oben (absteigend nach Datum), der ausdrücklich
  benennt: die #92-Begründung („das Kommando kann als Ziel einen Kanal *oder* eine Datei haben")
  **entfällt**, der Name „Übertragen…" bleibt trotzdem — nun aus dem einfacheren Grund, dass er
  genau einen Transportweg beschreibt. Ältere Einträge werden nicht nachgebessert.
- **`docs/Feature-Ideen-2026-08-01.md`**: A12 (Ergebnis-Export) und A16 (Emote-Liste importieren)
  sind beide ✅ und bleiben es — diese Runde ändert die Bedienung, nicht den Umsetzungsstand. Geprüft
  und bewusst unverändert.

## Testlandschaft

- **`export-dialog.spec.ts` (neu)** — die Datei hat heute als einzige in `shared/export/` keine Spec.
  Nach Regel 12 (Verhalten ja, Vorlage nein) geprüft: Vorbelegung auf `options[0]`; Scope-Gruppe nur
  bei `selectionCount > 0`; Zeilenzahl folgt dem gewählten Scope; Ergebnis `{optionId, scope}` bzw.
  `undefined` beim Abbrechen; die übergebene Reihenfolge ist die Anzeigereihenfolge; der zugängliche
  Name einer zweizeiligen Option enthält beide Zeilen. **Nicht** geprüft: Tailwind-Ketten,
  Verschachtelung, Übersetzungswortlaut als Selbstzweck.
- **`import-target-dialog.spec.ts`** — der Fall „Datei-Radio steht immer zuletzt" entfällt
  ersatzlos; `targetInputCount()`-Erwartungen sinken um eins; der Fall „nur der Datei-Radio, wenn es
  keinen wählbaren Kanal gibt" wird zu „leere Gruppe, `none`-Meldung, Weiter gesperrt" (E5). Die
  lokale Übersetzungsattrappe verliert `import.target.saveAsFile`.
- **`e2e/emote-import.e2e.spec.ts`** — die Zusicherung aus Z. 211 wandert vom Ziel-Dialog in den
  Export-Dialog und matcht dort die neue Zweckzeile. Die Scope-Vorbelegungs-Prüfung im Ziel-Dialog
  (`'Auswahl (2)'` ist gewählt) bleibt unverändert; sie belegt R12 weiterhin.
- **`e2e/audit/ui-audit.audit.ts`** — der Fall `usage-stats-export-dialog` bekommt eine neue
  Screenshot-Basislinie. Erwartet, kein Fehlschlag. Der Audit misst nie unter 768 px (#111) und der
  Dialog wird auf groben Zeigern zum Sheet — die dreizeilige Gruppe ist deshalb zusätzlich **am
  Gerät** anzusehen, nicht nur im Audit.
- **i18n** — neue Schlüssel in **beiden** Locale-Dateien, `import.target.saveAsFile` in beiden
  gelöscht.

## Ausdrücklich nicht in dieser Runde

Am 2026-09-09 durchgesprochen und verworfen — **nicht wieder vorschlagen**:

- Eine eigene Vorfrage „Was exportieren?" mit Format-Block, der bei der Emote-Liste verschwindet:
  der Dialog springt in der Höhe, der Bestätigungsknopf wandert unter dem Mauszeiger weg.
- Die Sorte als dritte Zeile unter der Überschrift „Format": dieselbe Fehlkategorie wie heute
  („Als Datei speichern" unter „Zielkanal"), nur umgezogen.
- CSV bei gewählter Emote-Liste sichtbar deaktivieren: eine dauerhaft tote Option, und die
  Formatgruppe hätte danach nur noch eine benutzbare Antwort.
- Variante B′ (Inhalt oben, Zweck darunter): ausdrücklich verworfen. Ausschlaggebend war „es soll
  klar sein, was genau für was ist" — der Zweck steht deshalb oben.

Ebenfalls nicht: eine geteilte Radio-Komponente (E2), eine Export-Kurzform im Dock (§8.7 begründet
ihre Abwesenheit), Änderungen am Umschlag oder Dateinamen der Emote-Liste.

## Definition of Done

- Ziel-Dialog zeigt nur noch Kanäle; Export-Dialog zeigt in der Nutzungsstatistik drei Zwecke, auf
  der Voting-Seite und im Löschprotokoll unverändert zwei Formate.
- §7.2 gekürzt, §7.4 neu, §8.7 um den bestätigenden Satz ergänzt, DECISIONS-Eintrag vorhanden —
  alles im selben Commit wie der Code.
- `npm --prefix web test -- --watch=false` grün, `npm --prefix web run e2e` grün (nur ohne Api auf
  `:5151`), `dotnet test EmotePurge.slnx` grün (unberührt, aber Gate).
- `node scripts/coverage-local.mjs` vor dem PR gelaufen; der neue `export-dialog.spec.ts` ist die
  Deckung für die größte neue Fläche.
- Am Gerät angesehen: die dreizeilige Zweckgruppe als Bottom-Sheet auf grobem Zeiger.
- Codex-Zweitmeinung (`--scope branch --base origin/main`) eingeholt und dem Nutzer **vorgelegt**.

## Beantwortete Fragen

Beide am 2026-09-09 vom Nutzer entschieden, bevor gebaut wurde:

1. **Scope-Vorbelegung für die Emote-Liste: `visible` wird geerbt.** Der Datei-Weg verhält sich damit
   wie die zwei Zahlen-Zeilen, obwohl er heute im Ziel-Dialog auf `selection` steht. Tragend ist die
   Begründung aus §7.2 selbst: `selection` schützt dort gegen das unbemerkte **Verbreitern eines
   Schreibvorgangs in ein fremdes 7TV-Set** — eine Datei schreibt nirgendwo hin, und der spätere
   Import hat seine eigene Vorschau mit Zahlen. Verworfen wurde, den Scope-Default beim Wählen der
   dritten Zeile umspringen zu lassen: das änderte unter der Hand eine Antwort, die der Nutzer schon
   gegeben hat. **Der Weg „7 markieren → Übertragen → Als Datei speichern" belegt danach `visible`
   statt `selection`** — bekannte, gewollte Verhaltensänderung.
2. **Der Ziel-Dialog ohne wählbaren Kanal bleibt ein Sackgassen-Dialog** (E5): `none`-Meldung, leere
   Gruppe, „Weiter" gesperrt. Verworfen wurde, „Übertragen…" im Seitenkopf vorab zu sperren — das
   bräuchte die Kanalliste schon vor dem Öffnen, die der Dialog heute selbst und bewusst ungecacht
   lädt.
