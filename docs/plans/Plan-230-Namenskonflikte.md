# Plan #230 — Namenskonflikte beim Übertragen auflösen: Quelle und Ziel nebeneinander, Entscheidung je Zeile

Erstellt am 2026-09-23 gegen `feat/emote-sets-200` = `f35ff68` (enthält #226 und #227). Quellen:
Issue #230 (Body und der T0-Kommentar vom 2026-09-23), die inhaltsgleiche Spec-Fassung unter
`~/.gstack/projects/sensitron-EmotePurge/specs/20260922-205949-2574119-resolve-name-conflicts-on-transfer.md`,
[die #200-Spec](../superpowers/specs/2026-09-20-emote-sets-200-spec.md) (8.6, F3, 6.6, 6.7),
[das Konzept](../Konzept-Emote-Sets-2026-09-19.md) (7.5, 12.1), [Plan-200](Plan-200-Emote-Sets.md)
als Formvorbild (K6b im Epic #200), `CLAUDE.md`, `web/.claude/CLAUDE.md`,
`docs/UI-Designsprache.md` (§4.2, §7, §7.2, §7.4, §10), die DECISIONS-Einträge vom 2026-09-20
(„An import may target any set …"), 2026-09-21 (K4, 7TV-Ids) und 2026-09-22 (#226), und der Code
auf `f35ff68` — jede Datei aus der Referenzliste des Issues ist gelesen, nicht vermutet.

**Das Issue ist die Vertragsquelle; dieser Plan trägt Reihenfolge, Schnitt, Grenzfälle, Tests und
Gates.** Wo Issue und Code auseinanderlaufen, steht es in Abschnitt 6 als Frage an den Betreiber,
nicht als stille Entscheidung. Der Plan enthält keinen Code: Signaturen und Typformen stehen nur,
wo sie ein Vertrag zwischen zwei Tasks sind.

---

## 0. Ausgangslage

### 0.1 Was feststeht

- **T0 ist positiv** (Issue-Kommentar 2026-09-23, live gegen `7tv.io/v4/gql`):
  `updateEmoteAlias(id: EmoteSetEmoteId!, alias: String!): EmoteSetEmote!` existiert; der
  **aktuelle** Alias im `id`-Objekt wählt den Eintrag aus, auch bei einem #74-Duplikat. Eine
  Kollision kommt als HTTP 200 mit `extensions.code = BAD_REQUEST`, `extensions.status = 409`,
  Text `emote name conflict` — **anderer Wortlaut als bei `addEmote`** („this emote has a
  conflicting name"). Erkannt wird sie am `status`, nie am Text. Folge: T4 „Adopt source name"
  bleibt im Umfang, AK 7–8 gelten, **AK 9 entfällt**.
- **Sechs Betreiber-Entscheidungen, nicht neu aufzurollen:** (1) beide Konfliktarten, (2) „Ziel
  ersetzen" ist dabei und bewusst destruktiv, (3) die Auflösung ist ein **zweiter Schritt im
  Bestätigungsdialog**, keine Seite, (4) bei Teilfehlschlag (REMOVE gelingt, ADD scheitert) bleibt
  die Lücke, kein Auto-Rollback, (5) je Zeile REMOVE, dann ADD, ein Lauf, (6) Absicherung nur über
  die Löschzahl in der Zusammenfassung, keine getippte Bestätigung.
- **#226 ist gemergt** (`e0293d8`, PR #232): die Aktionszeile jedes `DialogShell` klebt am unteren
  Rand des Panes. Die Layout-Voraussetzung des Issues ist damit erfüllt; T8 hat keine offene
  Abhängigkeit mehr.
- **Der Auflösungsschritt erscheint nie als Bottom-Sheet.** Jeder 7TV-Schreibweg — Dock,
  „Übertragen", Import-Trigger — steht hinter `!isCoarse()` (`usage-stats-page.html:115, 1042`).
  Die 360-px-Anforderung (AK 22) gilt deshalb für ein **schmales Desktopfenster** mit Maus; das
  Pane ist dort `min(28rem, 100vw − 2rem)` = 328 px breit. Keine Sheet-Variante planen, keine
  Touch-Ziele über das hinaus, was `appButton` `lg` schon mitbringt.
- **Deploy hinter dem 2026-10-08**, zusammen mit dem Rest von #200 (Issue „Timing"). Der PR geht
  gegen `feat/emote-sets-200`, nicht gegen `main` (Plan-200 0.4).

### 0.2 Was der Plan beim Nachprüfen am Code gefunden hat

| Befund | Beleg | Folge |
|---|---|---|
| **Das Import-Protokoll gibt es heute nicht, und das Purge-Protokoll ist nur einmal abrufbar.** `mass-delete-panel.ts:207-223` bietet den Download im Dock an, `reset()` (Schließen) wischt den Lauf, und der einzige Schutz ist der Hinweistext `massDelete.summary.protocolNotSaved`. Kein `beforeunload`, keine Ablage. Der `usageStatsLeaveGuard` fragt nur, **solange `isRunning()`** — ein fertiger Lauf mit ungespeichertem Protokoll passiert ihn wortlos. | `mass-delete-panel.ts:461-464, 568-610`, `usage-stats-leave.guard.ts:38-40` | Abschnitt 2 (Protokoll-Rückweg) und T7 |
| **Ein virtuell gescrolltes Raster in einem `DialogShell` gibt es schon**, und es ist der sanktionierte Ausnahmefall der „kein zweiter Scrollbereich"-Regel aus #226: `foreign-emote-grid.ts:105-135` macht den Viewport zum **einzigen** Scrollcontainer, misst seine Höhe gegen `dvh` und bekommt vom Dialog die Breitklasse `app-dialog-panel-wide` nur, während das Raster sichtbar ist (`import-source-dialog.ts:57, 96`, `styles.css:622-650`). | s. links; DECISIONS 2026-09-22 (#226) | T8 baut den Auflösungsschritt nach genau diesem Muster (Abschnitt 6, Frage 5) |
| **`filterAlreadyPresent` würde jede „Adopt"-Zeile aus dem Lauf werfen.** Der Frischcheck in `import-flow.ts:236-282` vergleicht nur die 7TV-Id; ein Alias-Abweichungs-Emote **ist** per Id im Ziel. Ohne Ausnahme startet ein Lauf, der die Umbenennung still weglässt und `skippedDuplicates` hochzählt. | `already-present-filter.ts:87-99`, `import-flow.ts:236` | T5 |
| **Die Engine kennt keinen `extensions.status`.** `RunOneResult` trägt `httpStatus` und `errorCode`; die 409-Kollision zur Laufzeit ist damit von einem beliebigen `BAD_REQUEST` nicht zu unterscheiden — und das Issue verlangt, sie **am Status** zu erkennen. | `seven-tv-run-engine.ts:105-130, 371-382` | T4 ergänzt `gqlStatus` |
| **Der Audit-Leseweg kennt den Besitzer-Login schon** für eine `emoteCount`-Zeile mit `emoteSetId`: `ReadTargetEmoteSet(root, "emoteSetId")` liest `targetOwnerTwitchLogin` mit, `renderTargetSet` zeigt dann die „von {owner}"-Form. Der neue set-zentrierte `sync-deleted` braucht also **keine** Änderung an Projektion oder `audit-row.ts`, wenn sein Details-JSON `emoteSetId` **und** `targetOwnerTwitchLogin` trägt. | `AuditLogQueryService.cs:155-162, 250-263`, `audit-row.ts:128-146` | T6 legt das Details-Shape fest |
| **`EmoteUsageTotal` trägt `imageUrl`, `ForeignEmoteRow` ebenso, die Datei nicht.** Drei `toImportRow`-Stellen: `usage-export-purposes.ts:58`, `foreign-import-flow.ts:52, 99`; der Parser `import-source-parser.ts:52` kennt kein Bild. | s. links | T1: drei Produzenten liefern die URL, der vierte `null` |
| **Der `ImportOrigin`-Union-Mechanismus** (zwei erschöpfende Helfer, `assertUnreachableOrigin`) zeigt, wie hier Vokabeln gesichert werden. Das neue Aktions-Union der Auflösung bekommt dasselbe Muster: jede Auseinandernahme erschöpfend, ein fünfter Wert ein Compile-Fehler. | `import-source.ts:88-135` | T3, T5, T7 |

### 0.3 Modelle je Task

`sonnet` für klar spezifizierte Implementierung und Tests; `opus` an drei Stellen mit Begründung
(T4 Engine, T5 Service, T8 UI-Schritt); `haiku` nirgends — kein Task ist rein mechanisch. T0 und
T10 haben kein Modell im Sinn einer Implementierung: T0 ist erledigt, T10 ist ein Handgriff des
Betreibers mit einem Subagent als Protokollant.

### 0.4 Branch und Commits

- **Branch `feat/230-name-conflicts` von `feat/emote-sets-200`**, PR gegen den Integrationsbranch.
  Worktree-Fallen aus Plan-200 0.4 gelten: aus dem Worktree nur bauen, nicht `docker compose up`;
  Live-Läufe aus dem Haupt-Checkout.
- **Ein Commit je Task**, Conventional Commits, englisch, ohne `#`-Referenzen in Commit-Metadaten
  (Memory: „Keine Ticketnummern in Git-Metadaten"). Vorschläge stehen je Task.
- **Regel 3:** Der DECISIONS-Eintrag („der Import-Dialog wird eine löschende Operation") liegt im
  Commit von **T8**, weil erst dort der REMOVE-Pfad für einen Nutzer erreichbar wird. Er nennt in
  seiner `Betrifft:`-Zeile auch die Dateien aus T4, T6 und T7 — ein Eintrag, nicht vier.

---

## 1. Verträge — wo sie stehen, was der Plan festlegt

Nichts aus dem Issue wird hier wiederholt. Die Tabelle sagt je Vertrag, welcher Task ihn trägt
und was der Plan **zusätzlich** zum Issue festlegt (mit Grund in Abschnitt 7).

| Vertrag | Quelle | Tasks | Plan-Zusatz |
|---|---|---|---|
| Aktionen je Zeile, Ausschlüsse, Defaults | Issue „Actions per row" | T3, T8 | — |
| Laufform: eine Queue-Zeile je Entscheidung, zwei Mutationen je Replace, Pacing/Backoff zwischen REMOVE und ADD | Issue „Run shape" | T4, T5 | Zeile trägt `failedStep`; Abbruch (`cancel`) zwischen REMOVE und ADD endet **`failed`** mit Lückengrund, nicht `cancelled` (Abschnitt 7) |
| Teilfehlschlag ohne Rollback | Issue „Partial failure", Entscheidung 4 | T4, T5, T7 | Lückengrund ist übersetzter Text **und** Feld `failedStep: 'add'` im Protokoll |
| Laufzeitkollision (Name inzwischen belegt) | T0-Kommentar | T4, T5 | Erkennung an `extensions.status === 409`, eigener Grund `import.errors.nameTakenNow`, **kein** Laufabbruch |
| `transfer-run`-Protokoll, Rückwegverbot | Issue „The transfer protocol" | T7 | eigene `TRANSFER_RUN_FORMAT_VERSION = 1`; Envelope-`channelName` = Zielkanal oder `''` bei ungetracktem Ziel, `meta` trägt Set, Besitzer, Herkunft |
| Protokoll-Rückweg vor dem Schließen | Vorsession | T7 | Abschnitt 2 |
| Löschzahl in der Zusammenfassung, keine getippte Bestätigung | Issue „Guard", Entscheidung 6 | T3, T8 | Zahl wird aus **denselben** Entscheidungen abgeleitet, aus denen der Lauf gebaut wird — eine Quelle |
| Meldungen: `sync-imported` + Löschmeldung, getrackt/ungetrackt | Issue „Backend bookkeeping" | T5, T6 | Details-Shape des set-zentrierten `sync-deleted` (T6); **Adopt meldet nichts** (Frage 2) |
| Bilder: `imageUrl` in beiden Modellen, Platzhalter statt abgeleiteter URL | Issue „Images" | T1, T8 | — |
| Entscheidungs-Validierung über den ganzen Lauf | Issue „Decision validation" | T3 | fünfte Regel: kein Zieleintrag wird von zwei Zeilen berührt (Replace **und** Adopt derselben Ziel-Id); Adopt gibt den alten Alias **nicht** frei (konservativ) |
| Layout: Schritt statt Gruppenbox, virtuelles Scrollen, gestapelt bei 360 px, roving tabindex | Issue „Layout" | T8 | Muster `foreign-emote-grid` (einziger Scrollcontainer, `dvh`-Höhe, Breitklasse während des Schritts) |
| Slot-Projektion: Rename +1, Replace +1 − entfernte Einträge | AK 21 | T3 | — |

---

## 2. Der Protokoll-Rückweg — die Vorsession-Auflage

**Befund:** Ein „Ziel ersetzen" ohne gespeichertes Protokoll ist die Lücke, vor der F3 warnt: das
Audit-Log trägt Zahlen, keine Ids; nur das Protokoll sagt, **welches** Emote unter **welchen**
Aliassen entfernt wurde. Heute geht das Purge-Protokoll mit dem Tab verloren, und der Leave-Guard
schützt es nicht (0.2). Für einen Add-only-Import war das egal; für einen Replace ist es die
einzige Rückwegdatei.

**Was der Plan festlegt (T7), von innen nach außen:**

1. **Das Protokoll wird nach jedem Übertragungslauf angeboten** (AK 16), im Dock neben den
   Zählern, mit demselben Hinweis wie beim Löschen (`protocolNotSaved`-Muster).
2. **Enthält der Lauf mindestens eine erfolgreiche REMOVE**, ist das kein Hinweistext mehr, sondern
   ein `warning`-Banner mit dem Download-Knopf **im Banner** (`notice-action`-Slot, wie
   `import.confirm.loadFailed`). Farbe heißt „dieser Lauf ist ungewöhnlich" (§7) — genau das ist
   er: er hat gelöscht. Ein Lauf ohne REMOVE bleibt beim stillen Hinweis.
3. **Der Leave-Guard und `beforeunload` decken den ungespeicherten Replace-Fall.**
   `usageStatsLeaveGuard` bekommt eine zweite Bedingung neben `isRunning()`: ein fertiger Lauf
   mit `removedCount > 0` und `protocolSaved === false`. Dieselbe Bedingung hängt ein
   `beforeunload` an das Fenster (Tab schließen, Reload) — der Browser zeigt seinen eigenen
   Dialog, mehr erlaubt er nicht. Beides lebt im Import-Service als ein Signal
   (`unsavedRemovalProtocol`), damit Guard und Listener nicht zwei Wahrheiten haben. Der
   In-App-Wechsel zwischen Kanälen bleibt frei (Root-Service, das Dock folgt — bewiesen in
   `emote-import.e2e.spec.ts:1985-1997`).
4. **„Schließen" am Panel bei ungespeichertem Replace-Protokoll** löst denselben `ConfirmDialog`
   aus wie der Leave-Guard (Nachricht: „Protokoll nicht gespeichert — trotzdem verwerfen?"), statt
   still `reset()` zu rufen. Ein Lauf ohne REMOVE schließt wie heute ohne Rückfrage.

**Was der Plan bewusst nicht baut, mit Grund:** eine Ablage im `localStorage` („letzte
Protokolle"). Sie würde eine neue Oberfläche brauchen (wo liegt die Liste, wer räumt sie auf), sie
läge außerhalb des Zero-Knowledge-Rahmens nur knapp (Emote-Ids und Aliase, kein Token — vertretbar),
und sie löst ein Problem, das die Punkte 2–4 auf den einen Handgriff „Download klicken" verengen.
Das Purge-Protokoll hat dieselbe Lücke seit A6 und der Betreiber hat sie akzeptiert. Sollte der
Betreiber die Ablage trotzdem wollen, ist sie ein eigener Task nach T7 (Frage 4).

---

## 3. Tasks

Jeder Task läuft als eigener Subagent mit frischem Kontext und bekommt diesen Abschnitt plus die
genannten Dateien. „Fertig" je Task: `npm --prefix web test -- --watch=false`; bei Backend-Berührung
`dotnet test EmotePurge.slnx` (Docker läuft); bei UI zusätzlich `npm --prefix web run e2e` — **nur
ohne Api auf `:5151`**; Formatter (`npm --prefix web run format`, `dotnet format EmotePurge.slnx`)
und `npm --prefix web run lint` grün. Vor dem PR `node scripts/coverage-local.mjs` (T9). Regel 11/12:
Verhalten ja, Vorlage nein — kein Test prüft Tailwind-Ketten oder Wortlaut als Selbstzweck.

### T0 — Sonde `updateEmoteAlias` — **erledigt 2026-09-23**

Ergebnis im Issue-Kommentar, verbatim. **AK 1 erfüllt.** Für den Plan festgehalten in 0.1. Kein
Commit.

### T1 — Die Bild-URL durch beide Modelle

**Ziel:** Ziel- und Quellzeile tragen eine Bild-URL, damit T8 zwei Bilder nebeneinander zeigen
kann, ohne aus der Id eine URL zu raten.

**Dateien:** `src/EmotePurge.Core/Services/IEmoteListQueryService.cs:9` (`EmoteListItemDto` +
`ImageUrl`), `src/EmotePurge.Infrastructure/Services/EmoteListQueryService.cs:17-30` (Projektion
`e.ImageUrl`), `web/src/app/core/emotes/emote-list-item.model.ts` (`imageUrl: string`),
`web/src/app/core/emotes/import-target-loader.ts:197-200` (nicht mehr verwerfen),
`web/src/app/core/seven-tv/import-source.ts:9-12` (`ImportRow.imageUrl: string | null`),
`web/src/app/shared/export/usage-export-purposes.ts:58-63`, `web/src/app/shared/seven-tv/foreign-import-flow.ts:52, 99`
(URL mitnehmen), `web/src/app/shared/export/import-source-parser.ts:52` (`null`),
`web/src/app/shared/export/emote-list-export.ts:36` (schreibt **weiterhin** nur Id und Name —
das Dateiformat ändert sich nicht, Frage 6), plus jede Fixture, die `ImportRow`/`EmoteListItem`
literal baut (Compile-Fehler zeigen sie).

**Vertrag:** `EmoteListItem.imageUrl` ist Pflicht (beide Ziel-Lesewege haben sie: Postgres
`Emote.ImageUrl`, `ForeignEmoteRow.imageUrl`); `ImportRow.imageUrl` ist nullbar, `null` nur aus
einer Datei. Kein Produzent leitet eine URL aus der Id ab (Issue „Images", `_static`-Befund).

**Grenzfälle:** Eine Datei-Quelle ergibt `null` auf der Quellseite, das Ziel hat trotzdem ein Bild
— beide Spalten müssen unabhängig leer sein können. `EmoteListItemDto` ist additiv; ein alter
Client ignoriert das Feld.

**Tests:** `tests/EmotePurge.Infrastructure.Tests/Integration/…EmoteListQueryServiceTests` **+1**
(`ListActiveAsync` liefert `ImageUrl`; berührt `AppDbContext`, also `Integration/`).
`import-target-loader.spec.ts` **+1** (Live-Liste behält `imageUrl`). `import-source-parser.spec.ts`
**+1** (Datei ⇒ `imageUrl: null`). `foreign-import-flow.spec.ts` **+1** (beide Abbildungen tragen
die URL).

**Abnahme:** `buildImportPreview`s Ein- und Ausgaben tragen die URL durch; Wire-Format von
`GET /api/channels/{c}/emotes` um ein Feld erweitert; Datei-Export unverändert. **AK 4 (Datenseite).**

**Commit:** `feat(import): carry the emote image url on both sides of the target comparison`.
**Abhängigkeiten:** keine. **Modell:** `sonnet`.

### T2 — `buildImportPreview`: auflösbare Zeilen mit ihrem Zielgegenstück

**Ziel:** Die Vorschau liefert je Konfliktzeile alles, was der Auflösungsschritt und der Lauf
brauchen — Quellzeile, betroffene Zieleinträge, und ob eine Aktion überhaupt möglich ist.

**Dateien:** `web/src/app/shared/seven-tv/import-preview.ts:78-135, 171`, `import-preview.spec.ts`.

**Vertrag:** `ImportPreview` bleibt vollständig abwärtskompatibel (die drei Namenslisten, die
Zähler, die Summen-Invariante — alle 22 Bestandstests bleiben grün) und wächst um zwei Listen:

- `nameCollisionRows`: je Quellzeile, die am Namen scheitert, die Quellzeile **und** den Zieleintrag,
  der den Namen trägt, plus **alle** Aliase, die dessen 7TV-Id im Ziel hält (ein REMOVE nimmt bei
  einem #74-Duplikat beide — T5.3/Sonde 5, Zweig A). Länge `=== nameCollisionRowCount`.
- `aliasMismatchRows`: je Quellzeile, die per Id im Ziel ist, aber unter anderem Alias: Quellzeile,
  alle Zielaliase dieser Id, und `adoptBlocked: null | 'nameTaken' | 'duplicateTarget'` —
  `nameTaken`, wenn der Quellname im Ziel schon einer **anderen** Id gehört; `duplicateTarget`, wenn
  die Ziel-Id unter mehr als einem Alias steht (AK 8; #74 bleibt offen, diese Aktion geht ihm aus
  dem Weg). Länge `=== aliasMismatches.length`.
- `isNameRejectedBySevenTv` wird exportiert (AK 10 braucht es in T3), bleibt Blockliste.

**Grenzfälle:** Zwei Quellzeilen kollidieren mit **demselben** Zieleintrag — beide Zeilen zeigen
dasselbe Gegenstück (T3 verbietet dann, dass beide ersetzen). Der Zieleintrag, der den Namen hält,
hat eine Id, die **auch** als Quellzeile vorkommt (Alias-Abweichung) — beide Listen nennen ihn;
T3 muss das sehen (fünfte Regel). Ein Zielname, der von 7TV ohne Alias gelistet wird (K5-Fixrunde,
`aliaslessIds`), existiert in der Ziel-Liste des Dialogs nicht (die Liste ist `name`-basiert) —
festhalten als Kommentar, nicht als Sonderfall.

**Tests:** `import-preview.spec.ts` **+4**: Gegenstück je Kollisionszeile mit allen Zielaliasen
eines #74-Duplikats; zwei Quellzeilen ⇒ dasselbe Gegenstück; Alias-Abweichung mit
`adoptBlocked: null` / `'nameTaken'` / `'duplicateTarget'` (die drei Fälle als Theory); Invariante
`nameCollisionRows.length === nameCollisionRowCount` und `aliasMismatchRows.length ===
aliasMismatches.length` am Halloween-Proportionsfall (bestehender AK-38-Test erweitert).

**Abnahme:** Bestandsfelder byte-identisch, neue Listen gefüllt, Export sichtbar. **AK 8
(Datenseite), AK 5 (Vorbereitung: die alten Felder bleiben die Quelle des unveränderten Laufs).**

**Commit:** `feat(import): expose resolvable conflict rows with their target counterpart`.
**Abhängigkeiten:** T1 (Bild-URL in den Zeilen). **Modell:** `sonnet`.

### T3 — Entscheidungsmodell, Laufplan, Validierung über den ganzen Lauf, Slot-Projektion

**Ziel:** Ein pures Modul, das aus Vorschau und Nutzerentscheidungen genau einen Laufplan ableitet,
jede Regelverletzung mit den betroffenen Zeilen benennt, und die Zahlen der Zusammenfassung liefert.
Kein Angular, kein DOM.

**Dateien (neu):** `web/src/app/shared/seven-tv/conflict-resolution.ts` + `.spec.ts`;
`web/src/app/shared/seven-tv/slot-projection.ts:17` + `.spec.ts`.

**Vertrag (Typformen als Vertrag zwischen T3, T5, T7, T8):**

- Entscheidung je Zeile, Schlüssel = `sevenTvEmoteId` der Quellzeile:
  `skip` (Default) · `renameSource { alias }` · `replaceTarget` · `adoptSourceName`.
  Erschöpfende Auseinandernahme nach dem `ImportOrigin`-Muster (0.2).
- `validateResolution(preview, decisions) → { ok: true } | { ok: false; violations: Violation[] }`,
  jede Violation mit Regelname und den Schlüsseln der beteiligten Zeilen. Regeln: die vier aus dem
  Issue („produced alias" schließt die `toAdd`-Zeilen ein; ein Replace gibt **alle** Aliase seiner
  Ziel-Id frei; jeder erzeugte Alias passiert `isNameRejectedBySevenTv`; keine zwei Zeilen ersetzen
  denselben Zieleintrag) plus die fünfte aus Abschnitt 1: **kein Zieleintrag wird von zwei Zeilen
  berührt** — ein Replace und ein Adopt auf dieselbe Ziel-Id schließen sich aus. Adopt gibt den
  alten Zielalias **nicht** frei (konservativ; Grund in Abschnitt 7). Eine Rename-Entscheidung mit
  leerem oder nur aus Leerraum bestehendem Alias ist eine Verletzung, keine Ausnahme.
- `buildTransferPlan(preview, decisions) → TransferPlan` mit `rows: TransferRow[]` in Quellreihenfolge:
  jede Zeile `{ action: 'add' | 'renameSource' | 'replace' | 'adoptSourceName', source: ImportRow,
  alias: string, target?: { sevenTvEmoteId, aliases: string[] } }`. `toAdd` wird zu `add`;
  `skip` erzeugt keine Zeile. Nur nach `ok: true` aufrufbar (wirft sonst).
- `summarizeTransferPlan(plan) → { addCount, removeCount, removedEntryCount }` — `removeCount` zählt
  Replace-Zeilen (die Löschzahl der Zusammenfassung, AK 20), `removedEntryCount` die Einträge, die
  die REMOVEs nehmen (Slot-Projektion, AK 21).
- `projectSlots(occupied, capacity, delta)`: die Signatur bleibt, der dritte Parameter wird als
  **Netto-Delta** dokumentiert; der Aufrufer rechnet `addCount + renameCount + replaceCount −
  removedEntryCount`. Alternativ eine zweite Funktion, die den Plan nimmt — Entscheidung des
  Implementers, beide Wege sind ein Test.

**Grenzfälle (alle als Testfall):** Zwei Renames auf denselben Alias · Rename auf den Namen einer
unangetasteten `toAdd`-Zeile · Rename auf einen Namen, den ein Replace **in diesem Lauf** freigibt
(erlaubt) · Rename auf einen Namen, den ein Adopt frei machen würde (blockiert, konservativ) ·
Rename auf einen der beiden Aliase eines #74-Ziel-Duplikats, das ein Replace nimmt (erlaubt, beide
frei) · zwei Replaces auf dieselbe Ziel-Id · Replace und Adopt auf dieselbe Ziel-Id · Adopt mit
`adoptBlocked !== null` ist eine Verletzung, auch wenn die UI sie nicht anbieten sollte (Modul
verlässt sich nicht auf die UI) · Alias mit Leerzeichen / 101 Zeichen / leer · leere Entscheidungen
⇒ Plan enthält **exakt** `preview.toAdd` als `add`-Zeilen, `removeCount === 0` (AK 5) · Projektion:
Rename +1, Replace ±0, Replace auf #74-Duplikat −1, gemischt.

**Tests:** `conflict-resolution.spec.ts` **+12** (Liste oben), `slot-projection.spec.ts` **+2**.

**Abnahme:** Modul pur, ohne TestBed testbar; jede Verletzung nennt Zeilen. **AK 5, 10, 11
(Logikseite), 20 (Zahl), 21.**

**Commit:** `feat(import): derive one transfer plan from per-row conflict decisions`.
**Abhängigkeiten:** T2. **Modell:** `sonnet`.

### T4 — Engine: mehrschrittige Zeilen, `gqlStatus` in der Fehlermeldung

**Ziel:** Eine Queue-Zeile kann mehr als eine Mutation brauchen; Status, Pacing, Backoff und
Abbruch behandeln jeden Schritt wie heute eine ganze Zeile. Delete, Restore und der heutige Import
verhalten sich **byte-identisch** (ein Schritt).

**Dateien:** `web/src/app/core/seven-tv/seven-tv-run-engine.ts:48-130, 245-310, 340-400` +
`.spec.ts`.

**Vertrag:**

- `RunOperation` liefert je Zeile eine **Folge** von Requests (heute genau einen). Form nach Wahl
  des Implementers — `buildRequest` wird zu einer Liste, oder `stepCount(emote)` + `buildRequest(setId,
  emote, step)`. Bedingung: die drei Bestandsoperationen ändern **keine** Zeile außer der
  Signaturanpassung.
- Zwischen zwei Schritten **derselben** Zeile gilt dieselbe Pacing-Verzögerung wie zwischen Zeilen;
  jeder Schritt hat eigenen Rate-Limit-Backoff; `requestTimestamps` zählt jeden Schritt (die
  Abschlussmessung bleibt ehrlich: ein Replace-Lauf hat doppelt so viele Requests wie Zeilen).
- **Schritt 1 scheitert** ⇒ Zeile `failed`, Schritt 2 wird nie gesendet (AK 14).
  **Schritt 2 scheitert** ⇒ Zeile `failed`, `RunQueueItem.failedStep` = Index des gescheiterten
  Schritts, `errorMessage` wie heute (AK 15; der Lückengrund selbst ist Sache der Operation, T5).
  `failedStep` ist auch bei Einschritt-Zeilen gesetzt (`0`), damit Leser nicht auf `undefined`
  prüfen müssen.
- `abortOn` läuft je gescheitertem Schritt, mit denselben Feldern **plus `gqlStatus: number | null`**
  (`extensions.status` der GQL-Rejection, sonst `null`; auch `RunOneResult` trägt es). Der Hook
  bekommt `failedStep` nicht — ob abzubrechen ist, hängt am Grund, nicht am Schritt.
- **`cancel()` zwischen Schritt 1 (erfolgreich) und Schritt 2:** die Zeile endet **`failed`** mit
  `failedStep = 1` und einer eigenen, übersetzten Meldung (`massDelete.errors.cancelledMidRow` —
  gemeinsamer Namensraum wie die anderen Engine-Texte), nicht `cancelled`. Grund: 7TV hat sich
  geändert, und `cancelled` heißt im Protokoll „nichts passiert" (Abschnitt 7, Frage 3).
- `progress` zählt weiter Zeilen, nicht Schritte — die Fortschrittsleiste zeigt Entscheidungen.

**Grenzfälle:** Rate-Limit-Pause **zwischen** REMOVE und ADD — die Zeile steht `in-progress`, die
Anzeige zeigt den Countdown wie heute; nach der Pause läuft der ADD, nicht der REMOVE noch einmal
(Retry gilt je Schritt). `abortOn` sagt bei Schritt 2 „abbrechen" ⇒ die Zeile ist `failed` mit
`failedStep = 1`, der Rest `cancelled`. Ein Hook, der wirft, wird je Schritt wie heute behandelt.

**Tests:** `seven-tv-run-engine.spec.ts` (24) **+7**: zwei Schritte in Reihenfolge mit Pacing
dazwischen; Schritt-1-Fehler unterdrückt Schritt 2; Schritt-2-Fehler ⇒ `failedStep = 1`, Lauf geht
weiter; Rate-Limit zwischen den Schritten wiederholt nur Schritt 2; `cancel()` zwischen den
Schritten ⇒ `failed` + `failedStep = 1`; `gqlStatus` erreicht `abortOn` (409-Fixture aus dem
T0-Kommentar); Einschritt-Operation unverändert (Snapshot der gesendeten Requests eines
Zwei-Zeilen-Laufs gegen den heutigen Stand — erlaubt, weil Wire-Vertrag, nicht Vorlage).
`seven-tv-delete.service.spec.ts` / `seven-tv-restore.service.spec.ts` / `seven-tv-import.service.spec.ts`:
nur Signatur-Fixtures, **0 neue Fälle**, alle bestehenden grün.

**Abnahme:** `grep -n "doneKeys" web/src` unverändert; drei Bestandsdienste ohne Verhaltensänderung.
**AK 13 (Reihenfolge, Nachbarschaft), 14, 15 (Engine-Hälfte).**

**Commit:** `feat(seventv): let one run row issue a sequence of mutations`.
**Abhängigkeiten:** keine (parallel zu T1, T6). **Modell:** `opus` — die Engine trägt drei Läufe,
ein Fehler in Pacing oder Statusführung ist in allen dreien und nur live sichtbar.

### T5 — Import-Service: der Lauf aus dem Plan, drei Mutationen, zwei Meldungen, eine Filterausnahme

**Ziel:** `SevenTvImportService` startet einen `TransferPlan` statt einer Zeilenliste, baut je
Aktion die richtigen Mutationen, meldet Hinzugefügtes und Entferntes getrennt, und der Frischcheck
wirft keine Adopt-Zeile mehr weg.

**Dateien:** `web/src/app/core/seven-tv/seven-tv-import.service.ts:34-50, 118-140, 210-260, 300-360`
+ `.spec.ts`; `web/src/app/shared/seven-tv/import-flow.ts:230-290` + `.spec.ts`;
`web/src/app/shared/seven-tv/already-present-filter.ts` (nur Doku-Kommentar);
`web/src/app/core/seven-tv/seven-tv-emote-set.service.ts:167` (Aufrufer von T6s Methode);
`web/public/i18n/{de,en}.json` (`import.errors.nameTakenNow`, `import.errors.removedButNotAdded`,
`import.summary.removed`).

**Vertrag:**

- `startImport(target, origin, plan: TransferPlan, skippedDuplicates, duplicateCheckAvailable)`:
  eine Queue-Zeile je Planzeile, Key = `source.sevenTvEmoteId` (bleibt eindeutig: die Vorschau
  dedupliziert per Id, und keine Aktion erzeugt zwei Zeilen für eine Quell-Id). Die Zeile trägt
  ihre `TransferRow` (Aktion, Alias, Ziel) — die Engine liest sie nicht, das Protokoll (T7) schon.
- Mutationen je Aktion: `add`/`renameSource` ⇒ `addEmote` mit `alias` aus der Planzeile (AK 12);
  `replace` ⇒ `removeEmote(target.sevenTvEmoteId)`, dann `addEmote(source.sevenTvEmoteId, alias)`
  gegen dieselbe `setId` (AK 13); `adoptSourceName` ⇒ `updateEmoteAlias(id: { emoteId, alias:
  <aktueller Zielalias> }, alias: <Quellname>)` — genau die Mutationsform aus dem T0-Kommentar
  (AK 7). Der aktuelle Zielalias kommt aus `target.aliases[0]`, und `adoptBlocked === null`
  garantiert, dass es genau einen gibt.
- **Fehlergründe der Zeile:** `gqlStatus === 409` (Kollision zur Laufzeit, gilt für `addEmote` und
  `updateEmoteAlias`) ⇒ `import.errors.nameTakenNow`, **kein** Laufabbruch. `failedStep === 1` bei
  einem Replace ⇒ `import.errors.removedButNotAdded` (der Lückengrund, AK 15), unabhängig vom
  7TV-Text; der rohe 7TV-Text wandert ins Protokoll (`errorMessage`), der übersetzte in die
  Anzeige. `abortsForMissingPrivileges` unverändert.
- **Meldungen nach dem Lauf:** `syncImported` für jede `done`-Zeile mit Aktion `add`,
  `renameSource`, `replace` (die hinzugefügten Ids, wie heute); zusätzlich eine **Löschmeldung** für
  jede Zeile, deren REMOVE erfolgreich war — also `done`-Replaces **und** `failed`-Replaces mit
  `failedStep === 1` (der REMOVE ist passiert, die Lücke ist real). Getracktes Ziel ⇒
  `emoteAdminService.syncDeleted(channel, { emoteSetId, sevenTvEmoteIds })` (der bestehende Weg,
  Papierfall bei nicht-aktivem Set); ungetracktes Ziel ⇒ `emoteSetService.reportDeletedFromSet`
  (T6). `adoptSourceName` meldet **nichts** (Frage 2). Beide Meldungen haben eigene
  `SyncReportState`-Signale (`syncReport` bleibt für den Import, `removalReport` neu), eigenen
  Retry, und beide hängen am `ImportRunInfo`-Objekt (R15-Muster).
- `ImportRunInfo` wächst um `plan` und um `removedCount` (abgeleitet, für Dock und Guard);
  `unsavedRemovalProtocol` als Signal für Abschnitt 2 (gesetzt in T7).
- **`filterAlreadyPresent` in `import-flow.ts`** läuft nur über die Planzeilen mit Aktion `add`,
  `renameSource`, `replace` — eine `adoptSourceName`-Zeile ist per Definition im Ziel und darf nicht
  herausfallen. Eine `replace`-Zeile, deren **Quell**-Id inzwischen im Ziel steht, fällt ganz heraus
  (kein REMOVE ohne ADD — konservativ, richtig).

**Grenzfälle:** Der Ziel-Eintrag eines Replace ist zur Laufzeit schon weg ⇒ `removeEmote` scheitert,
Zeile `failed` mit `failedStep = 0`, kein ADD — genau AK 14, und der Nutzer sieht 7TVs Grund. Der
Quellname eines Adopt ist zur Laufzeit belegt ⇒ 409 ⇒ `nameTakenNow`. Ein Lauf ohne einzige
erfolgreiche REMOVE sendet **keine** Löschmeldung (Endpunkt verlangt nicht-leere Liste). Ein
Replace-Lauf gegen ein Set, in dem das Token kein Schreibrecht hat, bricht beim **ersten** REMOVE
ab — vor dem ersten ADD, keine Lücke.

**Tests:** `seven-tv-import.service.spec.ts` (29) **+8**: Rename sendet den Alias, nicht den
Quellnamen; Replace sendet REMOVE dann ADD, dieselbe `setId`, benachbart; Adopt sendet
`updateEmoteAlias` mit altem Alias im `id` und neuem als Argument, **kein** `addEmote`; 409 ⇒
`nameTakenNow`, Lauf läuft weiter; `failedStep = 1` ⇒ `removedButNotAdded` **und** Id in der
Löschmeldung; Löschmeldung getrackt (`syncDeleted` mit `emoteSetId`) / ungetrackt
(`reportDeletedFromSet`); Lauf ohne REMOVE ⇒ keine Löschmeldung; Retry der Löschmeldung nutzt
denselben Laufdatensatz. `import-flow.spec.ts` **+2**: Adopt-Zeile überlebt den Frischcheck;
Replace mit inzwischen vorhandener Quell-Id fällt ganz weg.

**Abnahme:** Ein `TransferPlan` nur aus `add`-Zeilen erzeugt **exakt** die heutigen Requests und
Meldungen (Wire-Snapshot wie in T4). **AK 7 (Mutation), 12, 13, 15 (Grund), 19 (Client-Hälfte).**

**Commit:** `feat(import): run a transfer plan with rename, replace and adopt rows`.
**Abhängigkeiten:** T3 (Plan-Typ), T4 (Schritte, `gqlStatus`), T6 (`reportDeletedFromSet`).
**Modell:** `opus` — hier entsteht der einzige löschende Pfad des Import-Dialogs, und die
Meldungslogik entscheidet, ob das Audit-Log eine Lücke sieht.

### T6 — Backend: set-zentriertes `sync-deleted` und seine Client-Methode

**Ziel:** Der Spiegel von `POST /api/seventv/emote-sets/{emoteSetId}/sync-imported` für Löschungen
in ein ungetracktes Set — gleiche Leiter, gleiche Besitzerprüfung, gleiches Audit-Shape.

**Dateien:** `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs:203-260` (zweite Route in der
bestehenden `emoteSetGroup`), `src/EmotePurge.Core/Services/IEmoteService.cs:85` (neue Methode
`MarkDeletedFromSetAsync(emoteSetId, ownerSevenTvUserId, ownerTwitchLogin, sevenTvEmoteIds, actor)`),
`src/EmotePurge.Infrastructure/Services/EmoteService.cs:299-331` (Implementierung neben
`MarkImportedToSetAsync`), `web/src/app/core/seven-tv/seven-tv-emote-set.service.ts:167`
(`reportDeletedFromSet(emoteSetId, { sevenTvEmoteIds })`), `docs/superpowers/specs/2026-09-20-emote-sets-200-spec.md:230`
(F7-Querverweis 6.6 → 6.7, Issue „Backend bookkeeping").

**Vertrag:**

- Body `{ sevenTvEmoteIds: string[] }`, Leiter wie 6.7: 401 → `EmoteSetIdValidationFilter` 400 →
  leere Liste 400 `emote_ids_empty` → Besitzerprüfung `IImportTargetOwnershipService.CheckAsync`
  (404 `emote_set_not_found` / 403 bare / 503 `foreign_channel_seventv_unavailable`, kein Eintrag)
  → Service → 204. Policy `Bookkeeping`. **Kein neuer `ApiErrorCode`** — alle vier existieren
  (Regel 7 unberührt).
- Audit-Eintrag `emotes.syncDeleted`, `ChannelName = null`, `TargetType = "emoteSet"`,
  `TargetId = emoteSetId`, Details `{ emoteCount, emoteSetId, targetOwnerSevenTvUserId,
  targetOwnerTwitchLogin }`. **`emoteSetId`**, nicht `targetEmoteSetId`: die bestehende
  `emoteCount`-Leiter liest den Zielsatz unter diesem Namen (0.2) und zeigt dann „von {owner}" —
  ohne Änderung an `AuditLogQueryService` oder `audit-row.ts`. Keine Emote-Zeile wird berührt; der
  Eintrag **ist** die Leistung.
- **Kein `channel.synced`**-Event, kein Redis — es gibt keinen Kanal.

**Grenzfälle:** Doppelte Ids in der Liste ⇒ `emoteCount` dedupliziert (wie `MarkImportedToSetAsync`).
Ein Set, das dem Akteur per Editor-Grant gehört ⇒ `targetOwnerTwitchLogin` = Login des Grants.

**Tests:** `tests/EmotePurge.Api.Tests/SevenTvEmoteSetSyncDeletedEndpointTests.cs` (neu, Spiegel
der Imported-Tests) **+4**: 401; leere Liste 400 vor der Besitzerprüfung; Fremdset 403 bare, Service
nicht gerufen; Besitzer 204 mit Service-Aufruf und `ChannelName = null`. `AuthFilterMatrixTests.cs:70`
und `EmoteRoutePolicyTests.cs:48` je **+1 InlineData** (Regel 11: neue Route in einer gefilterten
Gruppe). `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteServiceTests.cs` **+1**
(Audit-Zeile, Details-Shape, keine Emote-Zeile berührt). `tests/EmotePurge.Infrastructure.Tests`
**+1** an `AuditLogQueryServiceTests`: eine `syncDeleted`-Zeile mit diesem Shape projiziert
`TargetEmoteSet.ownerLogin` (bestätigt den Befund aus 0.2 per Test statt per Lesen).
`seven-tv-emote-set.service.spec.ts` **+1** (URL und Body).

**Abnahme:** `dotnet test` grün; Route in beiden Matrizen. **AK 19 (Server-Hälfte).**

**Commit:** `feat(api): report removals from an untracked emote set`. Der Spec-Querverweis kommt
als `docs: point F7 at the set-centric endpoint in 6.7` im selben PR, eigener Commit.
**Abhängigkeiten:** keine (parallel zu T1, T4). **Modell:** `sonnet`.

### T7 — Das Transfer-Protokoll: Envelope, Download, Rückweg, Abweisung beim Einlesen

**Ziel:** Jeder Übertragungslauf hinterlässt eine Datei, die sagt, was hinzugefügt, umbenannt und
**entfernt** wurde — und diese Datei kann nirgends als Emote-Liste oder Restore missverstanden
werden. Dazu der Rückweg aus Abschnitt 2.

**Dateien:** `web/src/app/shared/export/export-envelope.ts:9` (`ExportKind` + `'transfer-run'`),
`web/src/app/shared/export/transfer-run-export.ts` (neu: build, JSON, CSV, Dateiname,
`TRANSFER_RUN_FORMAT_VERSION`) + `.spec.ts`, `web/src/app/shared/export/import-source-parser.ts:33-38`
(`transfer-run` **namentlich** abweisen, Key `restore.import.errors.transferRun`),
`web/src/app/shared/export/purge-run-export.ts:109-111` (`FOREIGN_KIND_ERROR_KEYS` + derselbe Key),
`web/src/app/shared/seven-tv/file-import-step.ts` (nur Doku: die Dispatch-Reihenfolge bleibt),
`web/src/app/shared/seven-tv/import-progress-section.ts:95-125` (Download im `run-actions`-Slot,
Hinweis/Banner, Schließen-Rückfrage), `web/src/app/core/seven-tv/seven-tv-import.service.ts`
(`protocolSaved`, `unsavedRemovalProtocol`), `web/src/app/features/usage-stats/usage-stats-leave.guard.ts:38`
(zweite Bedingung), `beforeunload`-Listener (Ort: der Import-Service, der ohnehin Root ist, oder
ein kleiner `core/`-Helfer mit `DestroyRef`), `web/public/i18n/{de,en}.json`
(`import.summary.downloadProtocol`, `import.summary.protocolNotSaved`,
`import.summary.protocolRemovalsUnsaved`, `import.leaveUnsavedProtocol.*`,
`restore.import.errors.transferRun`, `restore.import.sorts.*` **unverändert** — die Datei ist keine
Einlesesorte).

**Vertrag:**

- Zeilenform aus dem Issue, plus `failedStep: number | null` (Abschnitt 1) und `sourceName`
  (der Quellname, auch wenn `alias` davon abweicht — sonst ist ein Rename im Protokoll nicht
  rekonstruierbar). `removedTarget` genau bei `replace`, mit **allen** Aliasen der Ziel-Id, gefüllt
  aus der Planzeile (T5), nicht aus der Queue. Zeilen **ungefiltert** (F3: auch `failed` und
  `cancelled`).
- `meta`: `targetEmoteSetId`, `targetChannelName: string | null`, `targetOwnerDisplayName: string |
  null`, `origin` (die `ImportOrigin`, verbatim), `startedAt`, `finishedAt`, `counts` wie beim
  Purge-Protokoll **plus** `removed` (erfolgreiche REMOVEs). Envelope-`channelName` = Zielkanal
  oder `''` (Frage 6); Dateiname `emotepurge_<kanal-oder-setid>_transfer_<yyyy-mm-dd-HHmm>.<ext>`.
- `TRANSFER_RUN_FORMAT_VERSION = 1`, **kein** Bump von `EXPORT_FORMAT_VERSION` (Issue). CSV-Spalten:
  `action`, `source_name`, `alias`, `seven_tv_emote_id`, `status`, `failed_step`, `error_message`,
  `removed_seven_tv_emote_id`, `removed_aliases` (durch `|` getrennt — CSV ist Leseformat, der JSON
  ist der Datensatz).
- **Rückweg:** `parseImportSource` antwortet auf `kind === 'transfer-run'` mit dem eigenen Key
  **vor** der `emote-list`/`usage`-Prüfung; `parsePurgeRunProtocol` ebenso über
  `FOREIGN_KIND_ERROR_KEYS`. Ein unbekannter `kind` bleibt `wrongKind` (schon heute „mit Grund" —
  AK 18 zweiter Satz ist damit erfüllt, kein neuer Code). Es gibt **keinen** Parser für
  `transfer-run` (Issue: Aufzeichnung, keine Liste).
- **Download-Angebot** im Dock nach Abschnitt 2, Punkte 1–4; `ExportDialog` mit
  `FORMAT_EXPORT_OPTIONS`, `selectionCount: null` (wie das Purge-Protokoll). `protocolSaved` wird
  beim Start eines neuen Laufs zurückgesetzt.

**Grenzfälle:** Ein Lauf, der per `abortOn` nach der ersten Zeile abbricht, hat trotzdem ein
Protokoll (alle Zeilen, eine `failed`, Rest `cancelled`). Ein Lauf nur aus Adopt-Zeilen hat
`counts.removed = 0` und `added = 0` — das Protokoll erscheint trotzdem (AK 16). Ein untracked Ziel
ohne Anzeigenamen ⇒ `targetOwnerDisplayName: null`, Dateiname aus der Set-Id. Der `beforeunload`
darf **nie** feuern, solange kein Replace gelaufen ist — sonst bestraft er jeden Add-Import.

**Tests:** `transfer-run-export.spec.ts` **+6**: Build aus einem gemischten Lauf (jede Aktion einmal,
Replace mit #74-Duplikat ⇒ zwei Aliase in `removedTarget`); `failed` mit `failedStep = 1` bleibt
als Zeile; CSV-Spalten und Dateiname; `counts.removed` zählt auch den `failedStep = 1`-Replace;
Envelope-Felder für getrackt/ungetrackt. `import-source-parser.spec.ts` **+1**, `purge-run-export.spec.ts`
**+1**, `file-import-step.spec.ts` **+1** (die Datei landet bei keinem `picked`, Banner nennt den
Transfer-Grund). `import-progress-section.spec.ts` (18) **+4**: Download-Knopf nach jedem Lauf;
Hinweis vs. Banner je `removedCount`; Schließen mit ungespeichertem Replace-Protokoll öffnet die
Rückfrage, ohne `reset()`; ohne REMOVE schließt sofort. `usage-stats-leave.guard.spec.ts` **+2**
(fertiger Lauf mit ungespeichertem Replace fragt; nach `protocolSaved` nicht mehr).
`seven-tv-import.service.spec.ts` **+2** (`unsavedRemovalProtocol` Übergänge).

**Abnahme:** Kein Weg führt aus einer `transfer-run`-Datei in `startImportFlow` oder
`startRestoreFlow`; der Guard fragt genau dann, wenn Abschnitt 2 es sagt. **AK 16, 17, 18.**

**Commit:** `feat(import): record every transfer run in a downloadable protocol` (Envelope, Export,
Abweisung) und `feat(import): guard an unsaved removal protocol before it is lost` (Dock, Guard,
`beforeunload`) — zwei Commits, weil der zweite den Vertrag des Leave-Guards ändert.
**Abhängigkeiten:** T5 (Planzeilen am Laufdatensatz, `removedCount`). **Modell:** `sonnet`.

### T8 — Der Auflösungsschritt im Bestätigungsdialog; Zusammenfassung; DECISIONS-Eintrag

**Ziel:** Aus beiden Konfliktgruppen öffnet sich der zweite Schritt: eine virtualisierte Tabelle
mit Quellbild/-name links, Zielbild/-name rechts, Aktion je Zeile; zurück im ersten Schritt zeigt
die Zusammenfassung Hinzufügungen und Entfernungen getrennt, und „Kopieren" ist gesperrt, bis der
Plan gültig ist.

**Dateien:** `web/src/app/shared/seven-tv/import-confirm-dialog.ts` (Schrittzustand, Einstiegs-
Controls an den beiden Gruppen `:266, :274`, Zusammenfassung, Sperrgrund, `execute()` schließt mit
dem `TransferPlan`, Breitklasse über `DialogRef.overlayRef` wie `import-source-dialog.ts:269`),
`web/src/app/shared/seven-tv/import-conflict-resolution-step.ts` (neu, + `.spec.ts`),
`web/src/app/shared/seven-tv/import-flow.ts` (Outcome-Typ), `web/src/app/shared/emotes/emote-sprite.ts`
(unverändert — nur Aufrufer), `web/public/i18n/{de,en}.json` (`import.resolve.*`),
`docs/DECISIONS.md` (Eintrag, Regel 3), `docs/UI-Designsprache.md` §7.2 (Zeilenreihenfolge des
Dialogs um die zwei Einstiegs-Controls und die Entfernungszeile ergänzen — der Abschnitt ist
Vertrag).

**Vertrag:**

- **Zwei Schritte, ein Dialog, kein zweiter Overlay.** Schritt 1 ist der heutige Dialog; die
  beiden Gruppen Namenskollision und Alias-Abweichung bekommen je einen Knopf „Auflösen" (`outline`,
  §4.2 — das ist der Trigger, die Ausführung bleibt „Kopieren"), sichtbar genau bei
  `nameCollisionRowCount > 0` bzw. `aliasMismatches.length > 0` (AK 3). Bei null Konflikten ist
  der Dialog **byte-identisch** zu heute (AK 2 — ein Test rendert beide Zustände und vergleicht
  die Reihenfolge der Findings nach §7.2, nicht das Markup). Schritt 2 zeigt die Tabelle der
  jeweiligen Gruppe (oder beide nacheinander gruppiert — Entscheidung des Implementers, aber
  **eine** Tabelle je Öffnung), Aktionszeile „Zurück" / „Übernehmen"; „Übernehmen" ist bei
  Verletzungen gesperrt, der Grund steht als Text neben dem Knopf (`aria-describedby`) und nennt
  die Zeilen (AK 10, 11). „Zurück" verwirft die Änderungen dieses Öffnens nicht — Entscheidungen
  leben im Dialogzustand, bis der Dialog schließt.
- **Zeile:** `app-emote-sprite` links mit `source.imageUrl`, rechts mit dem Zielgegenstück;
  `imageUrl === null` ⇒ die `app-sprite-cell`-Platte ohne `<img>` — **kein** Request an eine
  abgeleitete URL (AK 4). Namen darunter, Zielseite bei #74-Duplikat mit beiden Aliasen. Aktion als
  Radiogroup je Zeile (Skip · Rename · Replace bzw. Skip · Adopt), `aria-label` „Aktion für
  {sourceName}" (AK 23); bei Rename erscheint ein Textfeld mit dem Quellnamen als Vorbelegung und
  dem Feldfehler-Muster aus §5.3. Ein Adopt mit `adoptBlocked !== null` wird **angezeigt, aber
  deaktiviert und beschriftet** (AK 8, Idiom aus 8.6 für nicht wählbare Sets).
- **Layout:** `cdk-virtual-scroll-viewport` mit fester Zeilenhöhe, Höhe gegen `dvh` nach dem Muster
  `foreign-emote-grid.ts:115-135` (der Viewport ist der einzige Scrollcontainer; `reservedRem` für
  den Kopf des Schritts), Breitklasse `app-dialog-panel-wide` nur während Schritt 2. Unter `sm`
  stapeln die beiden Seiten (Quelle über Ziel), die Zeilenhöhe wechselt mit — zwei Konstanten, ein
  `computed`, kein horizontaler Scroll bei 328 px Pane (AK 22). 200 Zeilen laden nicht 200 Bilder:
  der Viewport rendert nur den Puffer.
- **Tastatur:** Roving tabindex über die **Zeilen** (Pfeil hoch/runter, Home/End), `scrollToIndex`
  bringt die Zeile in den Viewport, bevor der Fokus wandert; innerhalb der Zeile Tab durch Radiogroup
  und Textfeld. Falle, die den Implementer erwartet: virtualisierte Zeilen außerhalb des Puffers sind
  **nicht im DOM**, Tab allein erreicht sie nie — deshalb die Zeilennavigation.
- **Zusammenfassung in Schritt 1:** neben der heutigen Titelzahl eine Zeile „N Emotes werden aus
  dem Zielset entfernt" genau bei `removeCount > 0` (AK 20), als `warning`-Banner (§7: dieses
  Merkmal macht den Lauf ungewöhnlich); die Titelzahl zählt `addCount` (inkl. Rename und Replace);
  Slot-Projektion aus `summarizeTransferPlan` (AK 21). Beide Zahlen kommen aus **demselben** Plan,
  den `execute()` zurückgibt.
- `ImportConfirmOutcome.rows` wird `plan: TransferPlan`; `import-flow.ts` reicht ihn an T5 durch.
  Keine Entscheidungen ⇒ Plan aus `toAdd` (AK 5 — Test vergleicht den Plan eines unberührten
  Dialogs mit `preview.toAdd`).
- **Kein Sheet:** keine `isCoarse`-Verzweigung im Schritt; der Dialog ist auf Touch nicht erreichbar
  (0.1). Ein Kommentar am Schritt sagt das.

**Grenzfälle (alle als Spec-Fall, außer wo E2E steht):** Gruppe mit 200 Zeilen ⇒ Viewport, nicht
`app-name-preview-list` · Zeile mit `imageUrl: null` links, Bild rechts · Dialog ohne Änderung
öffnen/schließen ⇒ heutiger Plan · Rename mit Leerzeichen ⇒ Feldfehler, Übernehmen gesperrt ·
zwei Renames gleich ⇒ Grund nennt beide Quellnamen · Replace auf eine Ziel-Id, die eine andere
Zeile adoptiert ⇒ gesperrt mit Grund · Sprachwechsel bei offenem Schritt re-übersetzt Labels
(`lang()`-Muster wie `aliasMismatchRows`) · `runBlocked` sperrt „Kopieren" weiter still.

**Tests:** `import-conflict-resolution-step.spec.ts` **+8**: Aktionsverfügbarkeit je Konfliktart
(AK 6); Adopt deaktiviert mit Grund bei `nameTaken`/`duplicateTarget`; Default Skip; Rename
öffnet Feld, Feldfehler bei ungültigem Alias; Übernehmen gesperrt mit Zeilen im Grund; zugänglicher
Name je Aktionsgruppe nennt die Zeile; Pfeiltaste ruft `scrollToIndex` und verschiebt den Fokus;
Platzhalter ohne `<img>` bei `null`. `import-confirm-dialog.spec.ts` (43) **+6**: kein
Einstiegs-Control ohne Konflikte, Reihenfolge nach §7.2 unverändert (AK 2); Control je Gruppe
(AK 3); Entfernungszeile nur bei Replace (AK 20); Projektion mit gemischten Entscheidungen (AK 21);
Outcome ist der Plan, unberührt = `toAdd` (AK 5); Breitklasse nur in Schritt 2. `import-flow.spec.ts`
**+1** (Plan wird durchgereicht).

**Abnahme:** UI-Audit-Harness-Szenario `usage-stats-import-confirm-dialog` weiter grün, plus ein
neues Szenario für Schritt 2 (T9). **AK 2, 3, 4 (Anzeige), 5, 6, 7 (Angebot), 8, 10, 11, 20, 21
(Anzeige), 22, 23.**

**Commit:** `feat(import): resolve name conflicts per row inside the confirm dialog` — mit dem
DECISIONS-Eintrag (0.4) und der §7.2-Ergänzung. **Abhängigkeiten:** T3, T5 (Outcome-Typ), T7 nicht
(Download ist Dock-Sache). **Modell:** `opus` — größter UI-Entwurf der Runde, Barrierefreiheit in
einem virtualisierten Raster, und die eine Stelle, an der der Nutzer eine Löschung auslöst.

### T9 — E2E, Harness-Szenario, Doku-Korrekturen, Coverage, Zweitmeinung

**Ziel:** Der ganze Weg einmal durch den Browser; die drei Dokustellen aus dem Issue; die Gates
vor dem PR.

**Dateien:** `web/e2e/emote-import.e2e.spec.ts` (+ `e2e/support/mocks.ts`: `mockSevenTvGql` muss
je Request unterscheiden — `removeEmote`, `addEmote`, `updateEmoteAlias` — und die Reihenfolge
aufzeichnen), `web/e2e/audit/ui-audit.audit.ts` (Szenario `usage-stats-import-resolve-step`,
`requiresFinePointer: true`, mit Bild-Mocks über `cdn.7tv.app/**` wie `:1548`),
`docs/Konzept-Emote-Sets-2026-09-19.md:1588-1592, 2124-2126` (Ausblick zeigt auf #230 statt #201),
`docs/Feature-Ideen-2026-08-01.md` (nur prüfen, ob eine Idee betroffen ist — #230 ist keine
Backlog-Idee; ohne Treffer keine Änderung), `CLAUDE.md` (keine Änderung erwartet).

**E2E (+3):** (1) Kollision + Alias-Abweichung im Mock; eine Zeile je Aktion (Rename, Replace,
Adopt), Kopieren; Assertion auf die **Reihenfolge und Argumente** der GQL-Requests (REMOVE vor ADD,
Alias aus dem Rename, `updateEmoteAlias` mit altem/neuem Alias) und auf **beide** Meldungen
(`sync-imported` mit den hinzugefügten Ids, `sync-deleted` mit der entfernten) — `page.clock`
vor `goto`, `runFor` statt `fastForward` (CLAUDE.md Tests). (2) Replace, dessen ADD mit 409 scheitert
⇒ Zeile rot mit Lückengrund, Löschmeldung enthält die Id trotzdem, Protokoll-Banner erscheint,
Download liefert eine Datei mit `removedTarget`. (3) Dialog mit Konflikten öffnen, nichts anfassen,
Kopieren ⇒ nur `addEmote`-Requests, exakt die `toAdd`-Zahl, keine Entfernungszeile (AK 5 durch den
Browser).

**Gates:** alle drei Suiten; `node scripts/coverage-local.mjs` (Näherung — Sonar zählt Zweige mit,
lokal pessimistisch; bei < 80 % nachsehen, ob es die neue `import-conflict-resolution-step.ts`
ist); `/codex:review --model gpt-6-sol --scope branch --base feat/emote-sets-200` über den Branch,
Ergebnis unverändert dem Betreiber vorlegen (Regel 22); Widersprüche zwischen Opus-Review und Codex
gehen an Fable (global). **AK 24.**

**Commit:** `test(e2e): resolve one conflict per action and assert the mutation order` und
`docs: point the concept's resolution-table outlook at its own issue`. **Abhängigkeiten:** T1–T8.
**Modell:** `sonnet`.

### T10 — Live-Verifikation gegen olafs Testset (Betreiber-Handgriff, Regel 16)

**Konto** `olaf_olaf_son`, **Set `test`** `01M320AYTGYMPJZD3RGPJGHH1S` (Ziel), **aktives Set
`tttt`**; sensitron ist dort 7TV-Editor, also ist das Ziel aus sensitrons Sicht ein **ungetracktes**
Set (Set-zentrierte Meldungen, T6). Haupt-Checkout, Api per `dotnet run`, `npm start`, danach
`dotnet run` beenden, bevor E2E läuft (`:5151`-Falle).

**Vorbereitung (Betreiber, einmalig):** im Set `test` einen Eintrag anlegen, dessen Alias mit einem
Emote der Quelle kollidiert, aber eine andere Id trägt; einen zweiten, dessen Id in der Quelle
steht, dort aber anders heißt; einen dritten als #74-Duplikat (dieselbe Id zweimal, zwei Aliase),
dessen einer Alias mit einer Quellzeile kollidiert. Quelle: eine Auswahl aus sensitrons Set oder
eine Emote-Liste-Datei (dann `imageUrl: null` links — auch das ist ein Prüfpunkt).

**Zu belegen, im PR-Text mit Zahlen:**

1. Schritt 2 zeigt drei Zeilen mit Bildern; die Datei-Quelle zeigt links die Platte ohne Request
   (Netzwerktab: kein `cdn.7tv.app`-Aufruf mit der Quell-Id).
2. Rename, Replace (auf das Duplikat) und Adopt in einem Lauf: Reihenfolge der Requests im
   Netzwerktab REMOVE → ADD benachbart; das Set danach per tokenlosem Read-back (Probe D aus dem
   T0-Kommentar) exakt wie erwartet — Duplikat mit **beiden** Aliassen weg, Quell-Emote unter
   Quellname drin, Adopt-Eintrag unter Quellname, Eintragszahl um genau eins gesunken.
3. Audit-Log (globale Admin-Ansicht): ein `syncImported`- und ein `syncDeleted`-Eintrag mit
   `ChannelName = null`, „von olaf_olaf_son", Zählwerte 2 und 1.
4. Protokoll heruntergeladen: `removedTarget` mit zwei Aliassen, `counts.removed = 1`; die Datei
   danach im Import-Dialog einlesen ⇒ Abweisung mit dem Transfer-Grund, kein Dialog geöffnet.
5. Laufzeitkollision: vor dem Kopieren im 7TV-Web einen Rename-Zielnamen belegen ⇒ genau diese
   Zeile rot mit `nameTakenNow`, Lauf läuft weiter.
6. Tab schließen mit ungespeichertem Replace-Protokoll ⇒ Browser-Rückfrage; nach Download keine.
7. Rate-Limit-Beobachtung aus der Konsolen-Abschlussmessung (`requestsSent` = Zeilen + Replaces).

Der Betreiber führt die Handgriffe aus; ein Subagent (`sonnet`) bereitet die Read-back-Abfrage und
die Erwartungswerte vor und schreibt die Befunde in den PR-Text. **Kein Commit**, außer ein Fund
verlangt einen `fix(import): …`. **AK 7 (live), 19 (live), 24.**

---

## 4. Nachverfolgung der Akzeptanzkriterien

| AK | Inhalt (Kurzform) | Tasks |
|---|---|---|
| 1 | T0-Ergebnis im Issue | T0 (erledigt) |
| 2 | Null Konflikte ⇒ Dialog wie heute | T8 |
| 3 | Einstieg je Gruppe bei Zählern > 0 | T8 |
| 4 | Zeile mit beiden Bildern, Platzhalter ohne abgeleitete URL | T1, T8 |
| 5 | Default Skip; unberührt ⇒ `toAdd` | T3, T8, T9 (E2E 3) |
| 6 | Aktionsangebot je Konfliktart | T8 |
| 7 | Adopt ändert den Zielalias, fügt nichts hinzu | T5, T8, T10 |
| 8 | Adopt blockiert bei `nameTaken` / `duplicateTarget`, mit Grund | T2, T3, T8 |
| 9 | *(T0-negativ)* | **entfällt** — T0 positiv |
| 10 | Rename braucht Alias; Validität, Eindeutigkeit, kein Überlebender; Grund nennt Zeilen | T3, T8 |
| 11 | Kein doppeltes Replace desselben Ziels | T3, T8 |
| 12 | Rename sendet den Alias | T5 |
| 13 | Replace: REMOVE dann ADD, dieselbe `setId`, benachbart | T4, T5, T9 (E2E 1) |
| 14 | REMOVE scheitert ⇒ kein ADD, `failed` | T4 |
| 15 | ADD scheitert nach REMOVE ⇒ eigener Grund, Lauf geht weiter | T4, T5, T9 (E2E 2) |
| 16 | Protokoll nach jedem Lauf | T7 |
| 17 | Replace-Zeile trägt Ziel-Id und alle Aliase | T7 |
| 18 | `transfer-run` wird namentlich abgewiesen; unbekannter `kind` mit Grund | T7 |
| 19 | Löschmeldung getrackt / ungetrackt, Audit zeigt beides | T5, T6, T10 |
| 20 | Entfernungszeile genau bei Replace | T3, T8 |
| 21 | Projektion: Rename +1, Replace ±0 / −1 bei Duplikat | T3, T8 |
| 22 | 200 Zeilen, 360 px, kein horizontaler Scroll, kein Bildsturm | T8 |
| 23 | Tastatur, zugänglicher Name je Zeile | T8 |
| 24 | Tests grün, keine Regression in Import/Delete/Restore | alle, T9 |

---

## 5. Reihenfolge und Abhängigkeiten

```
T0 (erledigt)
T1 ─┐            T4 ─┐        T6 ─┐
    ▼                │            │
    T2               │            │
    ▼                ▼            ▼
    T3 ─────────────► T5 ◄────────┘
    │                 │
    │                 ├──► T7
    ▼                 ▼
    T8 ◄──────────────┘
    ▼
    T9 ──► T10
```

- **Welle 1 (parallel, drei Worktrees oder sequenziell in einem):** T1, T4, T6.
- **Welle 2:** T2 (nach T1), dann T3.
- **Welle 3:** T5 (nach T3, T4, T6).
- **Welle 4 (parallel):** T7 und T8 (beide nach T5; T8 zusätzlich nach T3).
- **Welle 5:** T9, dann T10.

Zwischen T4 und T5 ist der Build grün (Signaturanpassung in T4 schließt die drei Dienste ein);
zwischen T3 und T8 ist der Dialog unverändert (der neue Plan-Typ hat noch keinen Aufrufer). Es gibt
keinen roten Zwischenstand, der zwei Tasks in einen Commit zwingt.

---

## 6. Offene Fragen an den Betreiber

Nichts davon ist im Plan still entschieden; wo der Plan einen Vorschlag macht, steht er dabei.

1. **Ungetracktes Zielset — das Issue markiert es selbst als ungeklärt.** Die #200-Spec kennt
   keinen set-zentrierten `sync-deleted` (6.6 setzt einen Kanal voraus). Der Plan baut den Endpunkt
   so, wie das Issue ihn vorschlägt (T6), mit dem Details-Shape aus 0.2, damit die Audit-Ansicht
   ohne Änderung „von {owner}" zeigt. **Zu bestätigen:** Endpunkt so bauen? Und soll #224 (der
   kanalgebundene Set-Report meldet Erfolg für einen unbekannten Kanal) im selben Zug auf dasselbe
   Shape gezogen werden, oder bleibt #224 getrennt? Der Plan lässt #224 unangetastet.
2. **Adopt hat keine Buchführung.** Weder `sync-imported` noch `sync-deleted` passen (nichts kommt
   hinzu, nichts geht); ein Alias-Wechsel hat heute keinen Audit-Vertrag. Der Plan meldet **nichts**
   und lässt die Lücke offen sichtbar (Protokoll trägt die Zeile). Alternative: ein dritter Report
   `sync-alias-changed` — neuer Endpunkt, neue Audit-Vokabel, neue Ansicht. Vorschlag: nicht in #230.
3. **`cancel()` zwischen REMOVE und ADD** endet im Plan `failed` mit Lückengrund, nicht `cancelled`
   (Abschnitt 1, T4). Das Issue sagt dazu nichts. Vorschlag steht; Alternative wäre ein vierter
   Status, was Protokoll-Leser und Panel ändert.
4. **Protokoll-Rückweg (Abschnitt 2):** Hinweis→Banner bei Replace, Leave-Guard und `beforeunload`
   für den ungespeicherten Fall, Rückfrage beim Schließen. **Keine** `localStorage`-Ablage.
   Reicht das, oder soll die Ablage ein eigener Task werden?
5. **Nested Scroll im Auflösungsschritt.** Das Issue verlangt virtuelles Scrollen; DECISIONS #226
   lehnt einen zweiten Scrollbereich im Dialog ab; `foreign-emote-grid` ist der eine sanktionierte
   Fall (Viewport = einziger Scrollcontainer + Breitklasse). Der Plan folgt diesem Muster (T8).
   **Zu bestätigen:** Breitklasse `app-dialog-panel-wide` auch für den Auflösungsschritt (zwei
   Bilder plus Radiogroup je Zeile brauchen sie ab ~5 sichtbaren Zeilen nicht, aber 72rem gibt der
   Tabelle Luft) — oder 28rem behalten und nur stapeln?
6. **Dateiformate:** `emote-list`-Export schreibt weiterhin **kein** `imageUrl` (sonst
   Format-Version-Frage, und die Datei ist eine Liste, kein Bildkatalog); Envelope-`channelName`
   des `transfer-run` ist `''` bei ungetracktem Ziel (das Feld ist im Envelope `string`, nicht
   nullbar; ein Nullable-Umbau hieße alle vier Bestands-Kinds anfassen). Beides ok?
7. **Adopt gibt den alten Zielalias nicht frei** (T3, konservativ): ein Rename auf genau den Alias,
   den ein Adopt in derselben Runde abgibt, wird blockiert, obwohl er in Queue-Reihenfolge klappen
   könnte. Vorschlag: so lassen (die Reihenfolge ist keine Vertragszusage der Engine; ein Fehler
   hier kostet einen 409 zur Laufzeit, kein Datenverlust). Freigeben wäre eine Zeile in T3.
8. **Das Issue nennt für T4 „Adopt" die Ziel-Id-Prüfung `targetGroup[0].name`** (AK 8) — der Plan
   liest den aktuellen Alias aus `target.aliases[0]` und garantiert per `adoptBlocked` genau einen
   Alias. Deckt sich mit AK 8; nur zur Kenntnis, falls #74 vorher geschlossen wird und die Regel
   dann fällt.

---

## 7. Wo der Plan vom Issue abweicht oder es ergänzt — mit Grund

| Stelle | Issue | Plan | Grund |
|---|---|---|---|
| Fünfte Validierungsregel | vier Regeln | plus „kein Zieleintrag von zwei Zeilen berührt" (Replace + Adopt derselben Id) | Am Code sichtbar: eine Ziel-Id kann **gleichzeitig** Namensinhaber (Kollisionszeile) und Alias-Abweichungs-Gegenstück sein; ohne die Regel verspricht der Dialog eine Umbenennung eines Eintrags, den er gerade entfernt |
| `failedStep` im Protokoll und in der Queue | „distinct reason that names the gap" (Text) | Text **und** Feld | Ein übersetzter Text ist kein maschinenlesbares Merkmal; ein späteres #201 oder ein Skript des Betreibers muss die Lücke ohne Wortlautvergleich finden |
| Löschmeldung bei `failedStep = 1` | „reports the removed ids" | schließt Replaces ein, deren ADD scheiterte | Die REMOVE ist passiert; ein Audit, das nur fertige Replaces zählt, unterschlüge genau die Lücke |
| `cancel()` mitten in der Zeile | nicht behandelt | `failed` + Lückengrund | s. Frage 3 |
| `gqlStatus` in der Engine | nicht erwähnt | neues Feld in `RunOneResult`/`abortOn` | Das Issue verlangt „detect it by `status`, not by text"; die Engine trägt den Status heute nicht durch |
| Protokoll-Rückweg | nicht Teil des Issues | Abschnitt 2 | Auflage der Vorsession |
| `sourceName` in der Protokollzeile | nur `alias` | beides | Ein Rename ist sonst nicht rekonstruierbar |
| `transfer-run`-CSV | nicht spezifiziert | Spaltenliste in T7 | Der Export-Dialog bietet immer CSV neben JSON (§7.4); ohne Spaltenvertrag entstünde er ad hoc |
| Spec-Querverweis F7 | „correct … while this issue is being built" | eigener `docs:`-Commit in T6 | Regel 2: logisch getrennter Commit |
| Konzept-Ausblick auf #201 | „point the outlook at this issue" | T9 | reine Doku, ans Ende |
| i18n als eigener Task | eigene Aufwandszeile | in jedem Task, der Text erzeugt | Hausregel: Schlüssel landen mit dem Feature in beiden Locales, nicht als Sammelschritt |
| Effort-Schätzung | 41 h | nicht neu geschätzt | Der Plan ändert den Umfang nur um Abschnitt 2 (grob +3 h) und die fünfte Regel (+0,5 h) |

---

## 8. Rückweg

Frontend-Änderungen sind per Revert des PR rückgängig; die eine additive DTO-Eigenschaft
(`EmoteListItemDto.ImageUrl`) und der neue Endpunkt sind unabhängig davon harmlos, wenn sie bleiben
(kein Aufrufer, kein Schema). Keine Migration. Bereits heruntergeladene `transfer-run`-Dateien
bleiben in einem revertierten Build **mit Grund** unlesbar (`wrongKind`) — das ist die vorgesehene
Antwort, kein Bruch. Ein bereits gelaufener Replace ist **nicht** durch Revert rückgängig; sein
Protokoll ist der einzige Rückweg, und genau deshalb steht Abschnitt 2 in diesem Plan.
