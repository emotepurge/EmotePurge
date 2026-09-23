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
Gates.** Wo Issue und Code auseinanderlaufen, steht es in Abschnitt 6 — seit der zweiten Fassung
als **Entscheidung des Betreibers vom 2026-09-23**, nicht mehr als offene Frage. Der Plan enthält
keinen Code: Signaturen und Typformen stehen nur, wo sie ein Vertrag zwischen zwei Tasks sind.

**Fassung 2 (2026-09-23).** Die erste Fassung (`83e6ce8`) ist an zwei Stellen überarbeitet: die
acht Betreiber-Antworten auf Abschnitt 6 sind eingearbeitet — Frage 4 **gegen** die erste Fassung
von Abschnitt 2 (Pflicht-Download der Rückweg-Datei **vor** dem ersten REMOVE statt Banner und
Rückfrage danach) —, und die sechs Befunde des adversarialen Codex-Reviews (gpt-6-sol) sind je
übernommen oder mit Grund zurückgewiesen (Abschnitt 9). Betroffen sind T3, T4, T5, T7, T8, T9,
T10 sowie die Abschnitte 1, 2, 4, 5, 7 und 8.

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
- **Acht weitere Betreiber-Entscheidungen vom 2026-09-23** (Antworten auf Abschnitt 6, dort im
  Wortlaut): set-zentrierter `sync-deleted` wie vorgeschlagen, #224 bleibt getrennt · Adopt ohne
  Audit-Meldung · Abbruch mitten in der Zeile endet `failed` mit Lückengrund · Auflösungsschritt mit
  der Breitklasse `app-dialog-panel-wide` · kein `imageUrl` im `emote-list`-Export, Envelope-
  `channelName` `''` bei ungetracktem Ziel · Adopt gibt den alten Alias nicht frei · und, **gegen**
  die erste Fassung: **Enthält der Lauf mindestens ein „Ziel ersetzen", ist vor dem ersten REMOVE
  ein Pflicht-Download der Rückweg-Datei nötig** — im Dialog, nach der Zusammenfassung, vor
  „Starten"; ohne Download startet der Lauf nicht; Läufe ohne Replace sind unberührt; keine
  `localStorage`-Ablage. Der Pflicht-Download ist **keine** getippte Bestätigung — Entscheidung 6
  bleibt unberührt. Abschnitt 2 trägt die Ausgestaltung.
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
| **Ein Transportfehler ist heute ein Fehlschlag.** `runOne` fängt jede `HttpErrorResponse` außer 429 und macht daraus `success: false` mit `httpStatus = error.status` — für einen Netzwerkabbruch Angulars `0`, übersetzt als `networkError`. Ob die Mutation bei 7TV angekommen ist, weiß niemand; für einen REMOVE oder ADD ohne Antwort heißt `failed` also „unbekannt", und die Meldung ließe eine womöglich hinzugefügte Id aus (Codex-Finding 3). | `seven-tv-run-engine.ts:406-424, 566-568` | T4 führt den Ausgang `unknown` ein, T5 klärt ihn per Live-Nachlesen |
| **Zwei Quellzeilen mit gleichem Namen und verschiedener Id landen heute beide in `toAdd`.** `dedupeImportRows` faltet nur nach `sevenTvEmoteId`, `buildImportPreview` prüft Namen nur gegen das Ziel, nie untereinander. 7TV lehnt dann die zweite ab — heutiges Verhalten, informativ wie `invalidNames`. Eine Validierung, die „keine zwei erzeugten Aliase gleich" wörtlich nimmt, blockierte diesen unveränderten Dialog (Codex-Finding 6). | `import-source.ts:141-144`, `import-preview.ts:110-117` | T3 nimmt die Bestandsdoppel von der Regel aus |
| **Der `filterAlreadyPresent`-Read liest bereits alle Aliase je Id** (`aliasesById`), tokenlos, aus dem globalen Bucket. Genau diese Daten braucht die Live-Verifikation der Replace-Ziele vor dem Download (Codex-Finding 1) — ein Read, drei Verwendungen: Duplikatfilter, Zielprüfung, Rückweg-Datei. | `seven-tv-set-entries.ts:39-60`, `already-present-filter.ts:87-99` | T5, T8 |

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
| Laufform: eine Queue-Zeile je Entscheidung, zwei Mutationen je Replace, Pacing/Backoff zwischen REMOVE und ADD | Issue „Run shape" | T4, T5 | Zeile trägt `failedStep`; Abbruch (`cancel`) zwischen REMOVE und ADD endet **`failed`** mit Lückengrund, nicht `cancelled` (Frage 3, entschieden) |
| Teilfehlschlag ohne Rollback | Issue „Partial failure", Entscheidung 4 | T4, T5, T7 | Lückengrund ist übersetzter Text **und** Feld `failedStep` im Protokoll |
| **Verlorene Antwort ist kein Fehlschlag** | Codex-Finding 3 | T4, T5, T7 | Ausgang `unknown` für einen Schritt ohne Antwort aus 7TVs GraphQL-Schicht (Status 0, 502, 503, 504); nach dem Lauf **ein** Live-Nachlesen klärt jede `unknown`-Zeile zu `done`/`failed`; bleibt es unklärbar, meldet der Lauf **keine** Lücke und die Id erscheint in **keiner** Meldung (Abschnitt 7) |
| Laufzeitkollision (Name inzwischen belegt) | T0-Kommentar | T4, T5 | Erkennung an `extensions.status === 409`, eigener Grund `import.errors.nameTakenNow`, **kein** Laufabbruch |
| **Live-Verifikation der Replace-Ziele vor dem Lauf** | Codex-Finding 1 | T5, T8 | vor dem Download liest der Dialog das Ziel-Set live (`loadSevenTvSetEntries`); weicht ein Replace-Ziel vom bestätigten Stand ab (andere Aliase, Eintrag weg, Name anderer Id), gibt es keinen Download und keine Startfreigabe, sondern Neuladen und Neu-Bestätigen; die Rückweg-Datei entsteht aus den **live gelesenen** Aliasen; der Frischcheck in `import-flow.ts` prüft dieselben Ziele ein zweites Mal als letztes Tor. Restfenster: zwischen letztem Lesen und jedem einzelnen REMOVE, nicht zu schließen (`already-present-filter.ts:55-57`) |
| `transfer-run`-Protokoll in **zwei Stufen**, Rückwegverbot | Issue „The transfer protocol", Frage 4 (entschieden) | T7, T8 | eine Envelope-Art, `meta.stage: 'planned' \| 'finished'`; eigene `TRANSFER_RUN_FORMAT_VERSION = 1`; Envelope-`channelName` = Zielkanal oder `''` bei ungetracktem Ziel, `meta` trägt Set, Besitzer, Herkunft |
| **Pflicht-Download vor dem ersten REMOVE** | Frage 4 (entschieden) | T7, T8 | im Dialog, nach der Zusammenfassung, vor „Starten"; nur bei `removeCount > 0`; Abschnitt 2 |
| Löschzahl in der Zusammenfassung, keine getippte Bestätigung | Issue „Guard", Entscheidung 6 | T3, T8 | Zahl wird aus **denselben** Entscheidungen abgeleitet, aus denen der Lauf gebaut wird — eine Quelle |
| Meldungen: `sync-imported` + Löschmeldung, getrackt/ungetrackt | Issue „Backend bookkeeping" | T5, T6 | Details-Shape des set-zentrierten `sync-deleted` (T6); **Adopt meldet nichts** (Frage 2, entschieden) |
| Bilder: `imageUrl` in beiden Modellen, Platzhalter statt abgeleiteter URL | Issue „Images" | T1, T8 | — |
| Entscheidungs-Validierung über den ganzen Lauf | Issue „Decision validation" | T3 | fünfte Regel: kein Zieleintrag wird von zwei Zeilen berührt; Adopt gibt den alten Alias **nicht** frei (Frage 7, entschieden); **ein Replace gibt seine Aliase nur für seine eigene Zeile frei**, nicht für eine fremde (Codex-Finding 4); **Namensdoppel innerhalb der unveränderten `toAdd`-Zeilen sind keine Verletzung** (Codex-Finding 6) |
| Layout: Schritt statt Gruppenbox, virtuelles Scrollen, gestapelt bei 360 px, roving tabindex | Issue „Layout" | T8 | Muster `foreign-emote-grid` (einziger Scrollcontainer, `dvh`-Höhe, Breitklasse während des Schritts — Frage 5, entschieden) |
| Slot-Projektion | AK 21, Codex-Finding 5 | T3 | `delta = addCount − removedEntryCount`, wobei `addCount` **jede ADD-Mutation** zählt (Add-, Rename- und Replace-Zeilen) — nicht Add plus Rename plus Replace obendrauf |

---

## 2. Der Protokoll-Rückweg — die Vorsession-Auflage

**Befund:** Ein „Ziel ersetzen" ohne gespeichertes Protokoll ist die Lücke, vor der F3 warnt: das
Audit-Log trägt Zahlen, keine Ids; nur das Protokoll sagt, **welches** Emote unter **welchen**
Aliassen entfernt wurde. Heute geht das Purge-Protokoll mit dem Tab verloren, und der Leave-Guard
schützt es nicht (0.2). Für einen Add-only-Import war das egal; für einen Replace ist es die
einzige Rückwegdatei.

**Entscheidung des Betreibers (2026-09-23, Frage 4), gegen die erste Fassung dieses Abschnitts:**
Die Rückweg-Datei liegt **vor der ersten Löschung auf der Platte**. Das übersteht Absturz und
Tab-Schließen, was `beforeunload` nie leistet; es gibt einen Weg und keine neue Ablage-Oberfläche;
und es ist keine getippte Bestätigung (Entscheidung 6 bleibt). Die erste Fassung — Banner nach dem
Lauf, Rückfrage beim Schließen, Guard für das fertige Protokoll — schützte ein Dokument, das es
zu dem Zeitpunkt schon nicht mehr gegeben hätte, wenn der Tab mitten im Lauf stirbt.

**Wie der Download im Dialogfluss sitzt (T8, Datei aus T7, Verifikation mit T5):**

1. **Schritt 1 des Dialogs, unterhalb der Zusammenfassung mit der Löschzahl** (AK 20), ändert sich
   die Aktionszeile genau dann, wenn `removeCount > 0`: statt „Kopieren" steht dort **„Rückweg
   sichern"** (`primary`, `lg`). Ohne Replace bleibt die Zeile byte-identisch zu heute — „Kopieren",
   kein Download, kein Lesen (AK 2, 5).
2. **Der Klick liest das Ziel-Set live** — tokenlos, `loadSevenTvSetEntries`, ein Request aus dem
   globalen Bucket, derselbe Leser wie `filterAlreadyPresent` — und vergleicht jedes Replace-Ziel
   mit dem bestätigten Stand: dieselbe Id, **dieselbe Alias-Menge**, der kollidierende Name gehört
   noch dieser Id, `complete === true`. Solange der Read läuft, ist der Knopf gesperrt und der
   Grund steht daneben (`aria-describedby`, wie `loadingHint`).
3. **Weicht ein Ziel ab**, gibt es **keinen** Download und **keine** Startfreigabe: ein Banner
   nennt die betroffenen Zeilen, der Knopf „Ziel neu laden" ruft `data.retry()`, die Vorschau wird
   neu gebaut, die Entscheidungen der abgewichenen Zeilen fallen auf Skip zurück (die übrigen
   bleiben, sofern sie die Validierung noch bestehen), und der Nutzer bestätigt neu. Ein
   unvollständiger Read (`complete: false`) oder ein Lesefehler zählt als Abweichung — eine Liste,
   die nur die Hälfte kennt, darf keine Löschung freigeben (Muster 8.3 aus der #200-Spec).
4. **Stimmt alles**, wird die Rückweg-Datei aus den **live gelesenen** Aliasen gebaut (Stufe
   `planned`, T7) und sofort per `downloadFile` als JSON heruntergeladen — ein Klick, eine Datei,
   kein `ExportDialog` (die Formatwahl bekommt das Ergebnisprotokoll nach dem Lauf). Danach zeigt
   die Aktionszeile **„Starten"**; erst der ausgelöste Download gibt ihn frei. Mehr als „der
   Download wurde ausgelöst" kann der Browser nicht bestätigen; das ist die Grenze des Vertrags.
5. **„Starten" schließt den Dialog** mit dem Plan; danach wie heute Token-Prompt und der Frischcheck
   in `import-flow.ts`. Dieser zweite Read (er existiert schon) prüft die Replace-Ziele **noch
   einmal** — zwischen Download und Start kann der Token-Prompt liegen. Ist ein Ziel inzwischen
   abgewichen, fällt **diese** Zeile aus dem Lauf, gezählt und im Dock genannt
   (`import.summary.replaceSkippedDrift`, Mechanik wie `skippedDuplicates`); nichts Destruktives
   läuft auf veralteten Daten. Das ist bewusst kein erneutes Öffnen des Dialogs: der Nutzer hat
   die Datei, die Zeile ist nur nicht passiert, das Ergebnisprotokoll sagt es.
6. **Restfenster:** Zwischen dem letzten Lesen und jedem einzelnen REMOVE bleibt ein Fenster, in
   dem ein anderer Editor dem Ziel-Eintrag einen Alias geben kann, den die Datei nicht kennt. Es
   ist ohne atomare Operation auf 7TVs Seite nicht zu schließen — `already-present-filter.ts:55-57`
   sagt dasselbe für den Duplikatfilter, und dieser Plan behauptet nichts anderes. Das
   Ergebnisprotokoll trägt deshalb je Replace-Zeile zusätzlich die Aliase, die der REMOVE
   **tatsächlich** genommen hat, soweit das Nachlesen nach dem Lauf sie liefert (T5).

**Was nach dem Lauf bleibt, und was aus der ersten Fassung entfällt:**

- **Das Ergebnisprotokoll** (Stufe `finished`, mit Status je Zeile, `failedStep`, `unknown`) wird
  nach **jedem** Übertragungslauf im Dock angeboten (AK 16), mit dem stillen Hinweis
  `protocolNotSaved` wie beim Löschen. Es ist ein Dokument **zweiten Rangs**: die Rückweg-Datei
  sagt, was weg sein kann; das Ergebnisprotokoll sagt, was davon wirklich passiert ist und welche
  ADDs fehlschlugen. Das Dock zeigt dieselben Ausgänge, das Audit-Log die Zählwerte.
- **Entfallen:** das `warning`-Banner nach dem Lauf, die Rückfrage beim Schließen und der
  Leave-Guard für ein **fertiges** ungespeichertes Protokoll. Alle drei schützten das
  Ergebnisprotokoll, weil es die einzige Datei war; jetzt ist es die zweite. Ein Guard für ein
  Dokument zweiten Rangs wäre die Reibung, die Entscheidung 6 gerade vermeidet.
- **Bleibt und wird erweitert:** der Schutz des **laufenden** destruktiven Laufs. `usageStatsLeaveGuard`
  fragt heute bei jedem laufenden Import (R11) — das bleibt. Neu ist ein `beforeunload`, solange
  `isRunning()` **und** der Plan Replace-Zeilen hat (`destructiveRunActive`, ein Signal im
  Import-Service): ein Tab, der mitten zwischen REMOVE und ADD stirbt, hinterlässt eine Lücke ohne
  Ergebnisprotokoll, und die Rückweg-Datei allein sagt nicht, **welche** Zeile es war. Für einen
  Add-only-Lauf feuert er nie. Nach dem Lauf feuert nichts mehr.
- **Keine `localStorage`-Ablage** (Betreiber-Entscheidung). Die Begründung der ersten Fassung
  gilt weiter, gestärkt: mit einer Datei vor jeder Löschung löst die Ablage kein Problem mehr.

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
  jede Violation mit Regelname und den Schlüsseln der beteiligten Zeilen. **Sechs Regeln**, alle
  unabhängig von der Zeilenreihenfolge (Codex-Finding 4 — eine Regel, die nur in einer Reihenfolge
  gilt, ist keine):
  1. **Kein erzeugter Alias doppelt** — „erzeugt" sind die Aliase aller Add-, Rename-, Replace- und
     Adopt-Zeilen. **Ausnahme (Codex-Finding 6):** ein Doppel zwischen zwei **unveränderten**
     `toAdd`-Zeilen ist keine Verletzung — das ist heutiges Verhalten (7TV lehnt die zweite ab,
     informativ wie `invalidNames`), und ein Dialog, an dem niemand etwas geändert hat, darf nicht
     blockieren (AK 2/5). Sobald eine **Entscheidung** an einem der beiden Enden hängt, gilt die
     Regel voll: ein Rename auf den Namen einer `toAdd`-Zeile ist eine Verletzung.
  2. **Kein erzeugter Alias gleich einem Namen, den das Ziel beim Öffnen hält** — mit **einer**
     Ausnahme: die Aliase des **eigenen** Replace-Ziels (der ADD einer Replace-Zeile darf den Namen
     tragen, den ihr REMOVE freigibt; das ist der Sinn der Aktion). Ein Alias, den ein **anderes**
     Replace oder ein Adopt in diesem Lauf freigäbe, bleibt gesperrt (Codex-Finding 4, konsistent
     mit Frage 7): die Engine läuft in Quellreihenfolge, der Rename könnte vor dem Replace kommen und
     bekäme 409; eine Abhängigkeitsordnung im Lauf wäre Komplexität für einen Fall, der sich in
     zwei Läufen sauber lösen lässt. Abschnitt 7 hält das als Abweichung vom Issue fest.
  3. **Jeder erzeugte Alias passiert `isNameRejectedBySevenTv`**; leer oder nur Leerraum ist eine
     Verletzung, keine Ausnahme.
  4. **Keine zwei Zeilen ersetzen denselben Zieleintrag.**
  5. **Kein Zieleintrag wird von zwei Zeilen berührt** — Replace und Adopt derselben Ziel-Id
     schließen sich aus (Abschnitt 1, fünfte Regel der ersten Fassung).
  6. **Adopt nur bei `adoptBlocked === null`** — das Modul verlässt sich nicht auf die UI.
  Adopt gibt den alten Zielalias **nicht** frei (Frage 7, entschieden).
- `buildTransferPlan(preview, decisions) → TransferPlan` mit `rows: TransferRow[]` in Quellreihenfolge:
  jede Zeile `{ action: 'add' | 'renameSource' | 'replace' | 'adoptSourceName', source: ImportRow,
  alias: string, target?: { sevenTvEmoteId, aliases: string[] } }`. `toAdd` wird zu `add`;
  `skip` erzeugt keine Zeile. Nur nach `ok: true` aufrufbar (wirft sonst).
- `summarizeTransferPlan(plan) → { addCount, removeCount, removedEntryCount }` — **`addCount` zählt
  jede ADD-Mutation**, also Add-, Rename- **und** Replace-Zeilen (Codex-Finding 5: die erste
  Fassung addierte Rename und Replace zusätzlich zu einem `addCount`, das sie schon enthielt);
  `removeCount` zählt Replace-Zeilen (die Löschzahl, AK 20); `removedEntryCount` die Einträge, die
  die REMOVEs nehmen (zwei bei einem #74-Duplikat).
- `projectSlots(occupied, capacity, delta)`: die Signatur bleibt, der dritte Parameter wird als
  **Netto-Delta** dokumentiert: **`delta = addCount − removedEntryCount`**, nichts weiter. Ein
  Rename ist +1, ein gewöhnlicher Replace 0, ein Replace auf ein doppelt belegtes Ziel −1 (AK 21).

**Grenzfälle (alle als Testfall):** Zwei Renames auf denselben Alias · Rename auf den Namen einer
unangetasteten `toAdd`-Zeile (Verletzung) · **zwei unveränderte `toAdd`-Zeilen mit gleichem Alias
und ohne Zielkonflikt (keine Verletzung, Plan wie heute)** · Rename auf einen Namen, den ein
**anderes** Replace in diesem Lauf freigibt (**Verletzung, in beiden Zeilenreihenfolgen**) · der
ADD einer Replace-Zeile auf den Namen des eigenen Ziels (erlaubt) · Rename auf einen Namen, den ein
Adopt frei machen würde (Verletzung) · Rename auf einen der beiden Aliase eines #74-Ziel-Duplikats,
das ein **anderes** Replace nimmt (Verletzung) · zwei Replaces auf dieselbe Ziel-Id · Replace und
Adopt auf dieselbe Ziel-Id · Adopt mit `adoptBlocked !== null` · Alias mit Leerzeichen / 101
Zeichen / leer · leere Entscheidungen ⇒ Plan enthält **exakt** `preview.toAdd` als `add`-Zeilen,
`removeCount === 0` (AK 5) · Projektion: einzelner Rename +1, gewöhnlicher Replace 0, Replace auf
doppelt belegtes Ziel −1, gemischt (Add + Rename + Replace auf Duplikat = 3 − 2 = +1).

**Tests:** `conflict-resolution.spec.ts` **+15** (Liste oben), `slot-projection.spec.ts` **+3**
(die drei Fälle aus Codex-Finding 5 einzeln).

**Abnahme:** Modul pur, ohne TestBed testbar; jede Verletzung nennt Zeilen. **AK 5, 10, 11
(Logikseite), 20 (Zahl), 21.**

**Commit:** `feat(import): derive one transfer plan from per-row conflict decisions`.
**Abhängigkeiten:** T2. **Modell:** `sonnet`.

### T4 — Engine: mehrschrittige Zeilen, `gqlStatus`, der Ausgang `unknown`

**Ziel:** Eine Queue-Zeile kann mehr als eine Mutation brauchen; Status, Pacing, Backoff und
Abbruch behandeln jeden Schritt wie heute eine ganze Zeile. Ein Schritt, dessen Antwort nie aus
7TVs GraphQL-Schicht kam, gilt nicht als gescheitert, sondern als **unbekannt** — für die
Operationen, die das verlangen. Delete, Restore und der heutige Import verhalten sich
**byte-identisch** (ein Schritt, kein `unknown`).

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
- **Der Ausgang `unknown` (Codex-Finding 3).** `RunItemStatus` bekommt den Wert `'unknown'`. Ein
  Schritt endet so, wenn **keine Antwort aus 7TVs GraphQL-Schicht** vorliegt: `httpStatus` `0`
  (Netzwerkabbruch, Timeout — heute `describeHttpError`s `networkError`-Zweig, `:566`) oder `502`,
  `503`, `504` (ein Proxy antwortete, nicht 7TV; die Mutation kann angewendet sein oder nicht).
  `500` bleibt `failed` — dort hat 7TV geantwortet, und der Vertrag „Fehler heißt nicht angewendet"
  ist so gut wie er bei jeder anderen Ablehnung ist. Eine GQL-Ablehnung über HTTP 200 ist **nie**
  `unknown`. Nur eine Operation, die es verlangt (`RunOperation.transportLossIsUnknown: true`),
  bekommt den Ausgang; die Voreinstellung `false` reproduziert heute: Delete, Restore und der
  Import-Lauf ohne Replace bleiben bei `failed`. Eine `unknown`-Zeile: kein weiterer Schritt (ein
  ADD auf ein womöglich noch besetztes Ziel ist ein Ticket für einen 409), `failedStep` = der
  Schritt ohne Antwort, `abortOn` wird **nicht** gerufen (es gibt keinen Grund zu bewerten), der
  Lauf geht weiter, `progress` zählt sie als erledigt, `doneKeys` enthält sie **nicht**.
- **`settle(key, status: 'done' | 'failed', errorMessage?)`**, nur außerhalb eines Laufs aufrufbar
  und nur für `unknown`-Zeilen: der Weg, auf dem T5s Nachlesen die Zeile klärt, bevor Dock und
  Protokoll sie zeigen. Eine geklärte `done`-Zeile wird in `doneKeys` **nicht** nachgetragen — das
  `RunResult` ist geschrieben; der Import-Service liest für seine Meldungen ohnehin die `items`.

**Grenzfälle:** Rate-Limit-Pause **zwischen** REMOVE und ADD — die Zeile steht `in-progress`, die
Anzeige zeigt den Countdown wie heute; nach der Pause läuft der ADD, nicht der REMOVE noch einmal
(Retry gilt je Schritt). `abortOn` sagt bei Schritt 2 „abbrechen" ⇒ die Zeile ist `failed` mit
`failedStep = 1`, der Rest `cancelled`. Ein Hook, der wirft, wird je Schritt wie heute behandelt.
Ein 429 bleibt Backoff, nie `unknown` — 7TV hat geantwortet. Ein `unknown` in Schritt 1 einer
Replace-Zeile ⇒ Schritt 2 wird nicht gesendet.

**Tests:** `seven-tv-run-engine.spec.ts` (24) **+10**: zwei Schritte in Reihenfolge mit Pacing
dazwischen; Schritt-1-Fehler unterdrückt Schritt 2; Schritt-2-Fehler ⇒ `failedStep = 1`, Lauf geht
weiter; Rate-Limit zwischen den Schritten wiederholt nur Schritt 2; **`cancel()` zwischen den
Schritten ⇒ `failed` + `failedStep = 1`, kein ADD gesendet, Rest `cancelled`** (Codex-Finding 2);
`gqlStatus` erreicht `abortOn` (409-Fixture aus dem T0-Kommentar); **Status 0 auf einer Operation
mit `transportLossIsUnknown` ⇒ `unknown`, kein Schritt 2, `abortOn` nicht gerufen**; **derselbe
Status 0 auf einer Operation ohne das Flag ⇒ `failed` wie heute**; **`settle` klärt eine
`unknown`-Zeile und verweigert sich einer `failed`-Zeile und einem laufenden Lauf**; Einschritt-
Operation unverändert (Snapshot der gesendeten Requests eines Zwei-Zeilen-Laufs gegen den heutigen
Stand — erlaubt, weil Wire-Vertrag, nicht Vorlage). `run-progress-panel.spec.ts` **+1**
(`unknown` zählt in `finished`, erscheint in der Fehlerliste mit eigener Wortfamilie
`<prefix>.unknownOutcome`). `seven-tv-delete.service.spec.ts` / `seven-tv-restore.service.spec.ts` /
`seven-tv-import.service.spec.ts`: nur Signatur-Fixtures, **0 neue Fälle**, alle bestehenden grün.

**Abnahme:** `grep -n "doneKeys" web/src` unverändert; drei Bestandsdienste ohne Verhaltensänderung;
`purge-run-export.ts`' `readProtocolRow` liest `status as RunItemStatus` weiter — ein `unknown`
kann dort nur aus einer Datei kommen, die dieser Build nicht schreibt, und fällt als „nicht `done`"
aus dem Restore, wie jeder andere Nicht-`done`-Status. **AK 13 (Reihenfolge, Nachbarschaft), 14, 15
(Engine-Hälfte).**

**Commit:** `feat(seventv): let one run row issue a sequence of mutations`.
**Abhängigkeiten:** keine (parallel zu T1, T6). **Modell:** `opus` — die Engine trägt drei Läufe,
ein Fehler in Pacing oder Statusführung ist in allen dreien und nur live sichtbar.

### T5 — Import-Service: der Lauf aus dem Plan, drei Mutationen, zwei Meldungen, Filterausnahme, Ziel-Verifikation, Nachlesen

**Ziel:** `SevenTvImportService` startet einen `TransferPlan` statt einer Zeilenliste, baut je
Aktion die richtigen Mutationen, meldet Hinzugefügtes und Entferntes getrennt, klärt `unknown`-Zeilen
per Live-Nachlesen, bevor irgendetwas gemeldet wird; der Frischcheck wirft keine Adopt-Zeile mehr
weg und verifiziert die Replace-Ziele ein zweites Mal.

**Dateien:** `web/src/app/core/seven-tv/seven-tv-import.service.ts:34-50, 118-140, 210-260, 300-360`
+ `.spec.ts`; `web/src/app/shared/seven-tv/import-flow.ts:230-290` + `.spec.ts`;
`web/src/app/shared/seven-tv/already-present-filter.ts` (eine dritte Funktion
`verifyReplaceTargets(entries, plan)` neben den zwei Filtern — pur, über einem schon gelesenen
`SevenTvSetEntries`, damit Dialog (T8) und Flow denselben Vergleich rechnen) + `.spec.ts`;
`web/src/app/core/seven-tv/seven-tv-emote-set.service.ts:167` (Aufrufer von T6s Methode);
`web/public/i18n/{de,en}.json` (`import.errors.nameTakenNow`, `import.errors.removedButNotAdded`,
`import.errors.unknownOutcome`, `import.summary.removed`, `import.summary.replaceSkippedDrift`,
`import.summary.unknownRows`).

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
- **`transportLossIsUnknown` ist genau dann gesetzt, wenn der Plan mindestens eine Replace-Zeile
  hat.** Ein Add-only-Lauf bleibt damit byte-identisch zu heute (Regel 24); ein Lauf, der löscht,
  bekommt den ehrlicheren Ausgang für **alle** seine Zeilen — auch Rename und Adopt, denn dort
  gilt dasselbe: eine verlorene Antwort heißt nicht „nicht angewendet" (Codex-Finding 3).
- **Nachlesen nach dem Lauf, vor jeder Meldung.** Hat das `RunResult` mindestens eine
  `unknown`-Zeile, liest der Service das Ziel-Set **einmal** live (`loadSevenTvSetEntries`,
  tokenlos, globaler Bucket) und klärt jede Zeile per `engine.settle`:
  - ADD-Schritt unbekannt (Add, Rename, Replace-Schritt 2): Quell-Id unter dem Plan-Alias im Set ⇒
    `done`; nicht im Set ⇒ `failed` — bei Replace mit `removedButNotAdded`, sonst mit dem generischen
    `unknownOutcome`-Grund plus 7TVs letztem Text.
  - REMOVE-Schritt unbekannt (Replace-Schritt 1): Ziel-Id noch im Set ⇒ `failed`, `failedStep 0`,
    nichts ist passiert; Ziel-Id weg ⇒ `failed` mit `removedButNotAdded` — der REMOVE ist passiert,
    der ADD wurde nie gesendet; die Id gehört in die Löschmeldung.
  - Adopt unbekannt: Ziel-Id unter dem Quellnamen ⇒ `done`; unter dem alten Alias ⇒ `failed`.
  - **Das Nachlesen scheitert oder ist `complete: false`:** die Zeilen bleiben `unknown`. Sie
    erscheinen in **keiner** Meldung — nicht als hinzugefügt, nicht als entfernt —, das
    Ergebnisprotokoll schreibt `unknown`, und das Dock sagt es (`import.summary.unknownRows`, mit
    der Aufforderung, das Set bei 7TV zu prüfen). Eine unbekannte Zeile als Lücke zu melden wäre
    dieselbe falsche Sicherheit wie sie als Erfolg zu melden.
- **Meldungen nach dem Lauf (nach dem Nachlesen):** `syncImported` für jede `done`-Zeile mit Aktion
  `add`, `renameSource`, `replace` (die hinzugefügten Ids, wie heute); zusätzlich eine
  **Löschmeldung** für jede Zeile, deren REMOVE **bestätigt** ist — `done`-Replaces, `failed`-Replaces
  mit `failedStep === 1`, und per Nachlesen geklärte Replaces, deren Ziel weg ist. Getracktes Ziel
  ⇒ `emoteAdminService.syncDeleted(channel, { emoteSetId, sevenTvEmoteIds })` (der bestehende Weg,
  Papierfall bei nicht-aktivem Set); ungetracktes Ziel ⇒ `emoteSetService.reportDeletedFromSet`
  (T6). `adoptSourceName` meldet **nichts** (Frage 2, entschieden). Beide Meldungen haben eigene
  `SyncReportState`-Signale (`syncReport` bleibt für den Import, `removalReport` neu), eigenen
  Retry, und beide hängen am `ImportRunInfo`-Objekt (R15-Muster).
- `ImportRunInfo` wächst um `plan`, `removedCount` (bestätigte REMOVEs, für Dock und Protokoll) und
  `unknownCount`; der Service trägt das Signal **`destructiveRunActive`** = `isRunning() &&
  plan.rows.some(replace)` für den `beforeunload` aus Abschnitt 2 (T7 hängt ihn an).
- **`filterAlreadyPresent` in `import-flow.ts`** läuft nur über die Planzeilen mit Aktion `add`,
  `renameSource`, `replace` — eine `adoptSourceName`-Zeile ist per Definition im Ziel und darf nicht
  herausfallen. Eine `replace`-Zeile, deren **Quell**-Id inzwischen im Ziel steht, fällt ganz heraus
  (kein REMOVE ohne ADD — konservativ, richtig).
- **`verifyReplaceTargets(entries, plan)` — das zweite Tor (Codex-Finding 1, Abschnitt 2 Punkt 5).**
  Aus **demselben** Read, den `filterAlreadyPresent` ohnehin macht, wird jede Replace-Zeile geprüft:
  Ziel-Id im Set, Alias-Menge gleich der Planzeile (`target.aliases`, die T8 aus der ersten
  Live-Verifikation geschrieben hat), kollidierender Name gehört dieser Id. Eine abgewichene Zeile
  fällt aus dem Lauf, gezählt in `replaceSkippedDrift` und im Dock genannt; der Rest läuft. Ein
  fehlgeschlagener oder unvollständiger Read lässt **keine** Replace-Zeile durch (anders als der
  Duplikatfilter, der offen ausfällt — eine Löschung auf ungeprüfter Grundlage ist der schlechtere
  Ausgang) und meldet es als `available: false`, wie heute.

**Grenzfälle:** Der Ziel-Eintrag eines Replace ist zur Laufzeit schon weg ⇒ das zweite Tor fängt
es, oder — im Restfenster — `removeEmote` scheitert, Zeile `failed` mit `failedStep = 0`, kein ADD
(AK 14), der Nutzer sieht 7TVs Grund. Der Quellname eines Adopt ist zur Laufzeit belegt ⇒ 409 ⇒
`nameTakenNow`. Ein Lauf ohne einzige bestätigte REMOVE sendet **keine** Löschmeldung (Endpunkt
verlangt nicht-leere Liste). Ein Replace-Lauf gegen ein Set, in dem das Token kein Schreibrecht hat,
bricht beim **ersten** REMOVE ab — vor dem ersten ADD, keine Lücke. Das Nachlesen trifft ein Set,
in dem ein Dritter inzwischen dieselbe Quell-Id hinzugefügt hat ⇒ die unbekannte ADD-Zeile wird zu
`done` geklärt, obwohl vielleicht der Dritte es war — hinnehmbar, denn das Emote **ist** unter dem
Alias im Set, und genau das meldet `syncImported`.

**Tests:** `seven-tv-import.service.spec.ts` (29) **+13**: Rename sendet den Alias, nicht den
Quellnamen; Replace sendet REMOVE dann ADD, dieselbe `setId`, benachbart; Adopt sendet
`updateEmoteAlias` mit altem Alias im `id` und neuem als Argument, **kein** `addEmote`; 409 ⇒
`nameTakenNow`, Lauf läuft weiter; `failedStep = 1` ⇒ `removedButNotAdded` **und** Id in der
Löschmeldung; Löschmeldung getrackt (`syncDeleted` mit `emoteSetId`) / ungetrackt
(`reportDeletedFromSet`); Lauf ohne REMOVE ⇒ keine Löschmeldung; Retry der Löschmeldung nutzt
denselben Laufdatensatz; **Add-only-Plan setzt `transportLossIsUnknown` nicht, Replace-Plan schon;
unbekannter ADD wird per Nachlesen zu `done` geklärt und gemeldet; unbekannter REMOVE mit
verschwundenem Ziel wird zu `failed`/Lücke und landet in der Löschmeldung; gescheitertes Nachlesen
lässt `unknown` stehen und hält die Id aus beiden Meldungen; Meldungen warten auf das Nachlesen**.
`already-present-filter.spec.ts` **+3** (`verifyReplaceTargets`: gleich, abgewichen durch neuen
Alias, abgewichen durch fremden Namensinhaber). `import-flow.spec.ts` **+4**: Adopt-Zeile überlebt
den Frischcheck; Replace mit inzwischen vorhandener Quell-Id fällt ganz weg; **abgewichenes
Replace-Ziel fällt weg und wird gezählt; fehlgeschlagener Read hält jede Replace-Zeile zurück**.

**Abnahme:** Ein `TransferPlan` nur aus `add`-Zeilen erzeugt **exakt** die heutigen Requests und
Meldungen (Wire-Snapshot wie in T4). **AK 7 (Mutation), 12, 13, 15 (Grund), 17 (live verifizierte
Ziel-Aliase), 19 (Client-Hälfte).**

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

### T7 — Das Transfer-Protokoll in zwei Stufen: Rückweg-Datei vor dem Lauf, Ergebnis danach, Abweisung beim Einlesen, Schutz des laufenden Laufs

**Ziel:** Vor der ersten Löschung liegt eine Datei auf der Platte, die je Replace-Zeile die live
verifizierte Ziel-Id und **alle** ihre Aliase nennt; nach jedem Übertragungslauf eine zweite, die
sagt, was davon wirklich passiert ist. Keine der beiden kann als Emote-Liste oder Restore
missverstanden werden. Dazu der `beforeunload` für den laufenden destruktiven Lauf (Abschnitt 2).
Den Download-Knopf **im Dialog** baut T8 — T7 liefert Datei, Dateiname und den Schutz.

**Dateien:** `web/src/app/shared/export/export-envelope.ts:9` (`ExportKind` + `'transfer-run'`),
`web/src/app/shared/export/transfer-run-export.ts` (neu: `buildTransferPlanRecord` für die Stufe
`planned`, `buildTransferRunProtocol` für `finished`, JSON, CSV, zwei Dateinamen,
`TRANSFER_RUN_FORMAT_VERSION`) + `.spec.ts`, `web/src/app/shared/export/import-source-parser.ts:33-38`
(`transfer-run` **namentlich** abweisen, Key `restore.import.errors.transferRun`),
`web/src/app/shared/export/purge-run-export.ts:109-111` (`FOREIGN_KIND_ERROR_KEYS` + derselbe Key),
`web/src/app/shared/seven-tv/file-import-step.ts` (nur Doku: die Dispatch-Reihenfolge bleibt),
`web/src/app/shared/seven-tv/import-progress-section.ts:95-125` (Ergebnisprotokoll im
`run-actions`-Slot mit dem stillen `protocolNotSaved`-Hinweis, Zeile `unknownRows`),
`web/src/app/core/seven-tv/seven-tv-import.service.ts` (`protocolSaved`; `beforeunload` an
`destructiveRunActive` — Ort: der Import-Service, der ohnehin Root ist, mit `DestroyRef`),
`web/public/i18n/{de,en}.json` (`import.summary.downloadProtocol`, `import.summary.protocolNotSaved`,
`restore.import.errors.transferRun`; `restore.import.sorts.*` **unverändert** — die Datei ist keine
Einlesesorte).

**Vertrag:**

- **Eine Envelope-Art, zwei Stufen.** `meta.stage: 'planned' | 'finished'`. Die Rückweg-Datei
  (`planned`) entsteht in T8 aus dem **live gelesenen** Ziel-Set: je Replace-Zeile `removedTarget`
  mit Ziel-Id und **allen** Aliasen aus dem Read, nicht aus der Vorschau (Codex-Finding 1); alle
  Zeilen `status: 'pending'`, `failedStep: null`, `errorMessage: null`; `counts` mit `planned`,
  `removals`. Das Ergebnisprotokoll (`finished`) trägt dieselben Zeilen mit Ausgang: `status` (auch
  `unknown`), `failedStep: number | null`, `errorMessage` (7TVs Rohtext), und bei Replace zusätzlich
  `removedTarget.confirmed: boolean` (REMOVE bestätigt, direkt oder per Nachlesen). Zeilen
  **ungefiltert** (F3: auch `failed`, `cancelled`, `unknown`).
- Zeilenform aus dem Issue, plus `failedStep`, `sourceName` (der Quellname, auch wenn `alias`
  davon abweicht — sonst ist ein Rename nicht rekonstruierbar) und `removedTarget.confirmed`.
- `meta`: `stage`, `targetEmoteSetId`, `targetChannelName: string | null`, `targetOwnerDisplayName:
  string | null`, `origin` (die `ImportOrigin`, verbatim), `verifiedAt` (Zeitpunkt des Reads, Stufe
  `planned`) bzw. `startedAt`/`finishedAt` (Stufe `finished`), `counts` (`finished`: wie beim
  Purge-Protokoll plus `removed` = bestätigte REMOVEs und `unknown`). Envelope-`channelName` =
  Zielkanal oder `''` (Frage 6, entschieden). Dateinamen
  `emotepurge_<kanal-oder-setid>_transfer-plan_<yyyy-mm-dd-HHmm>.json` und
  `emotepurge_<kanal-oder-setid>_transfer_<yyyy-mm-dd-HHmm>.<ext>` — zwei Suffixe, damit die zwei
  Dateien eines Laufs nebeneinander auf der Platte unterscheidbar sind.
- `TRANSFER_RUN_FORMAT_VERSION = 1`, **kein** Bump von `EXPORT_FORMAT_VERSION` (Issue). CSV nur für
  die Stufe `finished`: `action`, `source_name`, `alias`, `seven_tv_emote_id`, `status`,
  `failed_step`, `error_message`, `removed_seven_tv_emote_id`, `removed_aliases` (durch `|`
  getrennt), `removed_confirmed`. Die Rückweg-Datei ist **nur JSON** — ein Klick, eine Datei.
- **Rückweg-Verbot:** `parseImportSource` antwortet auf `kind === 'transfer-run'` — **beide
  Stufen** — mit dem eigenen Key **vor** der `emote-list`/`usage`-Prüfung; `parsePurgeRunProtocol`
  ebenso über `FOREIGN_KIND_ERROR_KEYS`. Ein unbekannter `kind` bleibt `wrongKind` (schon heute
  „mit Grund" — AK 18 zweiter Satz, kein neuer Code). Es gibt **keinen** Parser für `transfer-run`.
- **Ergebnisprotokoll im Dock** nach jedem Lauf (AK 16): `ExportDialog` mit `FORMAT_EXPORT_OPTIONS`,
  `selectionCount: null` (wie das Purge-Protokoll), stiller Hinweis `protocolNotSaved`, kein Banner,
  keine Rückfrage beim Schließen (Abschnitt 2). `protocolSaved` wird beim Start eines neuen Laufs
  zurückgesetzt.
- **`beforeunload`** hängt an `destructiveRunActive` (T5): registriert, solange das Signal `true`
  ist, entfernt danach; der Browser zeigt seinen eigenen Dialog. Der Leave-Guard bleibt, wie er ist
  (er fragt schon bei jedem laufenden Import). **Kein** Guard nach dem Lauf.

**Grenzfälle:** Ein Lauf, der per `abortOn` nach der ersten Zeile abbricht, hat trotzdem ein
Ergebnisprotokoll (alle Zeilen, eine `failed`, Rest `cancelled`). Ein Lauf nur aus Adopt-Zeilen hat
`counts.removed = 0` und `added = 0` — das Protokoll erscheint trotzdem (AK 16), eine Rückweg-Datei
gibt es für ihn **nicht** (kein Replace). Ein untracked Ziel ohne Anzeigenamen ⇒
`targetOwnerDisplayName: null`, Dateiname aus der Set-Id. Der `beforeunload` darf **nie** feuern,
solange kein Replace im Plan ist, und **nie** nach dem Lauf. Ein `unknown`, das das Nachlesen nicht
klären konnte, steht als `unknown` im Ergebnisprotokoll — kein Leser darf es als `failed` lesen.

**Tests:** `transfer-run-export.spec.ts` **+8**: Rückweg-Datei aus Plan + Live-Read (Replace mit
#74-Duplikat ⇒ zwei Aliase in `removedTarget`, `status: 'pending'`, `stage: 'planned'`);
Ergebnisprotokoll aus einem gemischten Lauf (jede Aktion einmal); `failed` mit `failedStep = 1` und
`removedTarget.confirmed: true`; `unknown`-Zeile bleibt `unknown`, zählt in `counts.unknown`, nicht
in `removed`; CSV-Spalten; beide Dateinamen; Envelope-Felder für getrackt/ungetrackt.
`import-source-parser.spec.ts` **+2** (beide Stufen abgewiesen), `purge-run-export.spec.ts` **+1**,
`file-import-step.spec.ts` **+1** (die Datei landet bei keinem `picked`, Banner nennt den
Transfer-Grund). `import-progress-section.spec.ts` (18) **+3**: Download-Knopf nach jedem Lauf;
stiller Hinweis bis `protocolSaved`; Schließen ruft `reset()` ohne Rückfrage.
`seven-tv-import.service.spec.ts` **+2** (`beforeunload` registriert genau während
`destructiveRunActive`, danach entfernt; Add-only-Lauf registriert nichts).

**Abnahme:** Kein Weg führt aus einer `transfer-run`-Datei (beide Stufen) in `startImportFlow` oder
`startRestoreFlow`; `beforeunload` feuert genau während eines laufenden Replace-Laufs. **AK 16, 17
(Dateiform), 18.**

**Commit:** `feat(import): record a transfer run before and after it runs` (Envelope, beide
Builder, Abweisung, Dock) und `feat(import): warn before unloading a running destructive transfer`
(`beforeunload`) — zwei Commits, weil der zweite Fensterverhalten ändert. **Abhängigkeiten:** T5
(Planzeilen und `destructiveRunActive` am Laufdatensatz). **Modell:** `sonnet`.

### T8 — Der Auflösungsschritt im Bestätigungsdialog; Zusammenfassung; Pflicht-Download; DECISIONS-Eintrag

**Ziel:** Aus beiden Konfliktgruppen öffnet sich der zweite Schritt: eine virtualisierte Tabelle
mit Quellbild/-name links, Zielbild/-name rechts, Aktion je Zeile; zurück im ersten Schritt zeigt
die Zusammenfassung Hinzufügungen und Entfernungen getrennt. Enthält der Plan ein Replace, führt
die Aktionszeile über Live-Verifikation und Pflicht-Download zur Startfreigabe (Abschnitt 2);
sonst ist sie byte-identisch zu heute.

**Dateien:** `web/src/app/shared/seven-tv/import-confirm-dialog.ts` (Schrittzustand, Einstiegs-
Controls an den beiden Gruppen `:266, :274`, Zusammenfassung, Sperrgrund, **Aktionszeile mit drei
Zuständen** bei `removeCount > 0`, `execute()` schließt mit dem `TransferPlan`, Breitklasse über
`DialogRef.overlayRef` wie `import-source-dialog.ts:269`), `web/src/app/shared/seven-tv/import-conflict-resolution-step.ts`
(neu, + `.spec.ts`), `web/src/app/shared/seven-tv/import-flow.ts` (Outcome-Typ; `ImportFlowDeps`
bekommt den `HttpClient` für den Read schon heute — er reicht ihn an den Dialog durch),
`web/src/app/shared/emotes/emote-sprite.ts` (unverändert — nur Aufrufer), `web/public/i18n/{de,en}.json`
(`import.resolve.*`, `import.confirm.saveRecovery`, `import.confirm.start`,
`import.confirm.verifying`, `import.confirm.targetDrifted`, `import.confirm.reloadTarget`),
`docs/DECISIONS.md` (Eintrag, Regel 3), `docs/UI-Designsprache.md` §7.2 (Zeilenreihenfolge des
Dialogs um die zwei Einstiegs-Controls, die Entfernungszeile und die dreistufige Aktionszeile
ergänzen — der Abschnitt ist Vertrag).

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
  Merkmal macht den Lauf ungewöhnlich); die Titelzahl zählt `addCount` (jede ADD-Mutation, T3);
  Slot-Projektion mit `delta = addCount − removedEntryCount` (AK 21, Codex-Finding 5). Beide Zahlen
  kommen aus **demselben** Plan, den `execute()` zurückgibt.
- **Aktionszeile bei `removeCount > 0` — drei Zustände, ein Knopf (Abschnitt 2, Punkte 1–5):**
  `idle` zeigt „Rückweg sichern" (`primary`, `lg`), gesperrt wie heute „Kopieren" durch
  `blockReason`/`runBlocked`; der Klick geht nach `verifying` (Knopf gesperrt, Grund
  `import.confirm.verifying` daneben, `aria-describedby`), der Read läuft über
  `loadSevenTvSetEntries` mit dem `HttpClient` aus den Flow-Deps, der Vergleich über
  `verifyReplaceTargets` (T5). **Abweichung** ⇒ zurück nach `idle`, Banner `targetDrifted` nennt die
  Zeilen (`error`, mit `notice-action` „Ziel neu laden" → `data.retry()`; die abgewichenen Zeilen
  fallen auf Skip zurück, die übrigen Entscheidungen werden neu validiert). **Gleichstand** ⇒
  Rückweg-Datei bauen (`buildTransferPlanRecord`, T7, aus den **gelesenen** Aliasen), `downloadFile`
  auslösen, Zustand `saved`: der Knopf heißt jetzt „Starten" und schließt mit dem Plan. Jede
  Änderung an den Entscheidungen nach `saved` (Schritt 2 erneut geöffnet und übernommen) setzt auf
  `idle` zurück — die Datei auf der Platte beschreibt einen anderen Plan. Ohne Replace bleibt die
  Zeile **byte-identisch** zu heute: „Kopieren", kein Read, kein Download (AK 2, 5).
- `ImportConfirmOutcome.rows` wird `plan: TransferPlan`; die Planzeilen tragen bei Replace die
  **live gelesenen** `target.aliases` (das zweite Tor in `import-flow.ts` vergleicht dagegen).
  `import-flow.ts` reicht ihn an T5 durch. Keine Entscheidungen ⇒ Plan aus `toAdd` (AK 5 — Test
  vergleicht den Plan eines unberührten Dialogs mit `preview.toAdd`).
- **Kein Sheet:** keine `isCoarse`-Verzweigung im Schritt; der Dialog ist auf Touch nicht erreichbar
  (0.1). Ein Kommentar am Schritt sagt das.

**Grenzfälle (alle als Spec-Fall, außer wo E2E steht):** Gruppe mit 200 Zeilen ⇒ Viewport, nicht
`app-name-preview-list` · Zeile mit `imageUrl: null` links, Bild rechts · Dialog ohne Änderung
öffnen/schließen ⇒ heutiger Plan, kein Read · Rename mit Leerzeichen ⇒ Feldfehler, Übernehmen
gesperrt · zwei Renames gleich ⇒ Grund nennt beide Quellnamen · Replace auf eine Ziel-Id, die eine
andere Zeile adoptiert ⇒ gesperrt mit Grund · Sprachwechsel bei offenem Schritt re-übersetzt Labels
(`lang()`-Muster wie `aliasMismatchRows`) · `runBlocked` sperrt „Rückweg sichern" und „Starten"
weiter still · Read liefert `complete: false` ⇒ wie Abweichung · Read scheitert (Netz) ⇒ wie
Abweichung, Banner nennt den Lesefehler statt Zeilen · nach `saved` wird Schritt 2 geöffnet und eine
Zeile geändert ⇒ „Rückweg sichern" erscheint erneut · Dialog wird im Zustand `saved` abgebrochen ⇒
kein Lauf, die Datei auf der Platte beschreibt einen Plan, der nie lief (unschädlich, `stage:
'planned'` sagt es).

**Tests:** `import-conflict-resolution-step.spec.ts` **+8**: Aktionsverfügbarkeit je Konfliktart
(AK 6); Adopt deaktiviert mit Grund bei `nameTaken`/`duplicateTarget`; Default Skip; Rename
öffnet Feld, Feldfehler bei ungültigem Alias; Übernehmen gesperrt mit Zeilen im Grund; zugänglicher
Name je Aktionsgruppe nennt die Zeile; Pfeiltaste ruft `scrollToIndex` und verschiebt den Fokus;
Platzhalter ohne `<img>` bei `null`. `import-confirm-dialog.spec.ts` (43) **+12**: kein
Einstiegs-Control ohne Konflikte, Reihenfolge nach §7.2 unverändert (AK 2); Control je Gruppe
(AK 3); Entfernungszeile nur bei Replace (AK 20); Projektion mit gemischten Entscheidungen (AK 21,
die drei Fälle aus Codex-Finding 5); Outcome ist der Plan, unberührt = `toAdd` (AK 5); Breitklasse
nur in Schritt 2; **ohne Replace kein Read und „Kopieren"; mit Replace „Rückweg sichern", Read
gegen das gemockte Set, Download ausgelöst, dann „Starten"; „Starten" vor dem Download nicht
erreichbar; Abweichung ⇒ kein Download, Banner mit Zeilen, Zeilen auf Skip; Änderung nach `saved` ⇒
zurück auf `idle`; Planzeilen tragen die gelesenen Aliase**. `import-flow.spec.ts` **+1** (Plan
wird durchgereicht).

**Abnahme:** UI-Audit-Harness-Szenario `usage-stats-import-confirm-dialog` weiter grün, plus ein
neues Szenario für Schritt 2 (T9). **AK 2, 3, 4 (Anzeige), 5, 6, 7 (Angebot), 8, 10, 11, 17
(Rückweg-Datei mit live gelesenen Aliasen), 20, 21 (Anzeige), 22, 23.**

**Commit:** `feat(import): resolve name conflicts per row inside the confirm dialog` — mit dem
DECISIONS-Eintrag (0.4) und der §7.2-Ergänzung. **Abhängigkeiten:** T3, T5 (Outcome-Typ,
`verifyReplaceTargets`), **T7** (`buildTransferPlanRecord`). **Modell:** `opus` — größter
UI-Entwurf der Runde, Barrierefreiheit in einem virtualisierten Raster, und die eine Stelle, an
der der Nutzer eine Löschung auslöst.

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

**E2E (+5):** (1) Kollision + Alias-Abweichung im Mock; eine Zeile je Aktion (Rename, Replace,
Adopt); „Rückweg sichern" ⇒ der gemockte Set-Read antwortet, ein Download feuert (Playwrights
`waitForEvent('download')`, Inhalt: `stage: 'planned'`, `removedTarget` mit allen Aliasen) ⇒
„Starten"; Assertion auf die **Reihenfolge und Argumente** der GQL-Requests (REMOVE vor ADD, Alias
aus dem Rename, `updateEmoteAlias` mit altem/neuem Alias) und auf **beide** Meldungen
(`sync-imported` mit den hinzugefügten Ids, `sync-deleted` mit der entfernten) — `page.clock`
vor `goto`, `runFor` statt `fastForward` (CLAUDE.md Tests). (2) Replace, dessen ADD mit 409
scheitert ⇒ Zeile rot mit Lückengrund, Löschmeldung enthält die Id trotzdem, Ergebnisprotokoll
(`stage: 'finished'`) trägt `failedStep: 1`, `removedTarget.confirmed: true`. (3) Dialog mit
Konflikten öffnen, nichts anfassen, Kopieren ⇒ **kein** Set-Read, **kein** Download, nur
`addEmote`-Requests, exakt die `toAdd`-Zahl, keine Entfernungszeile (AK 5 durch den Browser).
(4) **Abweichung:** der gemockte Set-Read gibt dem Replace-Ziel einen dritten Alias ⇒ kein Download,
Banner nennt die Zeile, „Starten" nicht vorhanden; nach „Ziel neu laden" zeigt die Vorschau die neue
Alias-Menge. (5) **Verlorene Antwort:** der gemockte ADD eines Replace bricht die Verbindung ab
(`route.abort()`), der Nachlese-Read zeigt die Quell-Id unter dem Alias ⇒ Zeile grün, Id in
`sync-imported`; Variante: der Read zeigt sie nicht ⇒ Zeile rot mit Lückengrund.

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
2. **Pflicht-Download:** „Rückweg sichern" liest das Set (ein tokenloser Request im Netzwerktab),
   die Datei landet auf der Platte **bevor** irgendein REMOVE gesendet ist, `stage: 'planned'`,
   `removedTarget` des Duplikats mit **beiden** Aliassen; erst dann steht „Starten".
3. Rename, Replace (auf das Duplikat) und Adopt in einem Lauf: Reihenfolge der Requests im
   Netzwerktab REMOVE → ADD benachbart; das Set danach per tokenlosem Read-back (Probe D aus dem
   T0-Kommentar) exakt wie erwartet — Duplikat mit **beiden** Aliassen weg, Quell-Emote unter
   Quellname drin, Adopt-Eintrag unter Quellname, Eintragszahl um genau eins gesunken.
4. Audit-Log (globale Admin-Ansicht): ein `syncImported`- und ein `syncDeleted`-Eintrag mit
   `ChannelName = null`, „von olaf_olaf_son", Zählwerte 2 und 1.
5. Ergebnisprotokoll heruntergeladen: `stage: 'finished'`, `removedTarget.confirmed: true`,
   `counts.removed = 1`; **beide** Dateien danach im Import-Dialog einlesen ⇒ je Abweisung mit dem
   Transfer-Grund, kein Dialog geöffnet.
6. Laufzeitkollision: vor dem Kopieren im 7TV-Web einen Rename-Zielnamen belegen ⇒ genau diese
   Zeile rot mit `nameTakenNow`, Lauf läuft weiter.
7. **Abweichung:** im 7TV-Web dem Replace-Ziel zwischen Dialogöffnen und „Rückweg sichern" einen
   dritten Alias geben ⇒ kein Download, Banner nennt die Zeile; nach „Ziel neu laden" drei Aliase
   in der Vorschau.
8. Tab schließen **während** eines Replace-Laufs ⇒ Browser-Rückfrage; nach dem Lauf keine.
9. Rate-Limit-Beobachtung aus der Konsolen-Abschlussmessung (`requestsSent` = Zeilen + Replaces;
   die zwei Set-Reads zählen nicht mit — sie laufen nicht durch die Engine).

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
| 15 | ADD scheitert nach REMOVE ⇒ eigener Grund, Lauf geht weiter — **eine verlorene Antwort ist kein Scheitern** (`unknown`, Nachlesen) | T4, T5, T9 (E2E 2, 5) |
| 16 | Protokoll nach jedem Lauf (Ergebnisprotokoll); **zusätzlich Rückweg-Datei vor jedem Lauf mit Replace** | T7, T8 |
| 17 | Replace-Zeile trägt Ziel-Id und **alle** Aliase — **live gelesen** vor dem Download, nicht aus der Vorschau; Ergebnis mit `confirmed` | T2, T5, T7, T8, T9 (E2E 1, 4), T10 (Punkte 2, 7) |
| 18 | `transfer-run` wird namentlich abgewiesen (beide Stufen); unbekannter `kind` mit Grund | T7 |
| 19 | Löschmeldung getrackt / ungetrackt, Audit zeigt beides — nur **bestätigte** REMOVEs | T5, T6, T10 |
| 20 | Entfernungszeile genau bei Replace | T3, T8 |
| 21 | Projektion: `addCount − removedEntryCount` — Rename +1, Replace 0, Replace auf Duplikat −1 | T3, T8 |
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
    │                 ▼
    │                 T7
    ▼                 ▼
    T8 ◄──────────────┘
    ▼
    T9 ──► T10
```

- **Welle 1 (parallel, drei Worktrees oder sequenziell in einem):** T1, T4, T6.
- **Welle 2:** T2 (nach T1), dann T3.
- **Welle 3:** T5 (nach T3, T4, T6).
- **Welle 4:** T7 (nach T5).
- **Welle 5:** T8 (nach T3, T5, T7 — der Pflicht-Download im Dialog braucht T7s Builder; in der
  ersten Fassung liefen T7 und T8 parallel).
- **Welle 6:** T9, dann T10.

Zwischen T4 und T5 ist der Build grün (Signaturanpassung in T4 schließt die drei Dienste ein);
zwischen T3 und T8 ist der Dialog unverändert (der neue Plan-Typ hat noch keinen Aufrufer). Es gibt
keinen roten Zwischenstand, der zwei Tasks in einen Commit zwingt.

---

## 6. Fragen an den Betreiber — **entschieden 2026-09-23**

Die acht Fragen der ersten Fassung, je mit der Antwort des Betreibers. Sieben Vorschläge sind
angenommen, einer (Frage 4) ist **gegen** den Vorschlag entschieden. Nichts hier ist mehr offen;
neue Fragen aus der Codex-Runde gibt es keine (Abschnitt 9).

1. **Ungetracktes Zielset.** Die #200-Spec kennt keinen set-zentrierten `sync-deleted` (6.6 setzt
   einen Kanal voraus); der Plan baut den Endpunkt wie im Issue vorgeschlagen (T6), mit dem
   Details-Shape aus 0.2. — **Entschieden: so bauen; #224 bleibt getrennt** und unangetastet.
2. **Adopt hat keine Buchführung.** Weder `sync-imported` noch `sync-deleted` passen; ein
   Alias-Wechsel hat keinen Audit-Vertrag. — **Entschieden: Adopt meldet nichts**; die Zeile steht
   im Ergebnisprotokoll, ein dritter Report ist nicht Teil von #230.
3. **`cancel()` zwischen REMOVE und ADD** endet `failed` mit Lückengrund, nicht `cancelled` (T4). —
   **Entschieden: so.** Test dazu in T4 (Codex-Finding 2).
4. **Protokoll-Rückweg.** Vorschlag der ersten Fassung: Banner nach dem Lauf, Leave-Guard und
   `beforeunload` für das ungespeicherte fertige Protokoll, Rückfrage beim Schließen, keine Ablage.
   — **Entschieden gegen den Vorschlag: Pflicht-Download der Rückweg-Datei vor dem ersten REMOVE**,
   im Dialog nach der Zusammenfassung und vor „Starten"; die Datei nennt je Replace-Zeile die live
   verifizierte Ziel-Id und alle ihre Aliase; ohne Download kein Lauf; Läufe ohne Replace
   unberührt; **keine** `localStorage`-Ablage; keine getippte Bestätigung (Entscheidung 6 bleibt);
   das Ergebnisprotokoll nach dem Lauf bleibt zusätzlich; Leave-Guard und `beforeunload` decken den
   **laufenden** destruktiven Lauf. Ausgestaltung: Abschnitt 2; Tasks T5, T7, T8.
5. **Nested Scroll im Auflösungsschritt.** Der Plan folgt dem `foreign-emote-grid`-Muster (Viewport
   = einziger Scrollcontainer). — **Entschieden: mit der Breitklasse `app-dialog-panel-wide`**
   während Schritt 2 (T8).
6. **Dateiformate.** — **Entschieden: `emote-list`-Export weiterhin ohne `imageUrl`;
   Envelope-`channelName` des `transfer-run` ist `''` bei ungetracktem Ziel** (T1, T7).
7. **Adopt gibt den alten Zielalias nicht frei** (T3, konservativ). — **Entschieden: so lassen.**
   Codex-Finding 4 zieht daraus die konsistente Folge für fremde Replace-Freigaben (T3, Regel 2).
8. **`targetGroup[0].name` vs. `target.aliases[0]`** — nur zur Kenntnis, deckungsgleich mit AK 8;
   keine Entscheidung nötig.

---

## 7. Wo der Plan vom Issue abweicht oder es ergänzt — mit Grund

| Stelle | Issue | Plan | Grund |
|---|---|---|---|
| Fünfte Validierungsregel | vier Regeln | plus „kein Zieleintrag von zwei Zeilen berührt" (Replace + Adopt derselben Id) | Am Code sichtbar: eine Ziel-Id kann **gleichzeitig** Namensinhaber (Kollisionszeile) und Alias-Abweichungs-Gegenstück sein; ohne die Regel verspricht der Dialog eine Umbenennung eines Eintrags, den er gerade entfernt |
| `failedStep` im Protokoll und in der Queue | „distinct reason that names the gap" (Text) | Text **und** Feld | Ein übersetzter Text ist kein maschinenlesbares Merkmal; ein späteres #201 oder ein Skript des Betreibers muss die Lücke ohne Wortlautvergleich finden |
| Löschmeldung bei `failedStep = 1` | „reports the removed ids" | schließt Replaces ein, deren ADD scheiterte | Die REMOVE ist passiert; ein Audit, das nur fertige Replaces zählt, unterschlüge genau die Lücke |
| `cancel()` mitten in der Zeile | nicht behandelt | `failed` + Lückengrund | Frage 3, entschieden |
| `gqlStatus` in der Engine | nicht erwähnt | neues Feld in `RunOneResult`/`abortOn` | Das Issue verlangt „detect it by `status`, not by text"; die Engine trägt den Status heute nicht durch |
| **Rückweg-Datei vor dem Lauf** | „Offered after every transfer run" (nur danach) | zusätzlich Pflicht-Download **vor** dem ersten REMOVE, `stage: 'planned'`, aus live gelesenen Aliasen | Betreiber-Entscheidung Frage 4 und Codex-Findings 1/2: eine Datei, die erst nach dem Lauf entsteht, gibt es nicht, wenn der Tab mitten im Lauf stirbt; eine Datei aus der Vorschau kann Aliase nicht kennen, die seither dazukamen |
| **Ausgang `unknown`** | „if the ADD fails, the row ends `failed`" | ein Schritt ohne Antwort aus 7TVs GraphQL-Schicht endet `unknown`; ein Nachlesen klärt ihn; unklärbar heißt: in keiner Meldung | Codex-Finding 3: eine verlorene Antwort ist kein Beweis, dass nichts passiert ist; `failed` würde eine womöglich hinzugefügte Id aus `sync-imported` halten und eine Lücke behaupten, die es nicht gibt |
| **Fremde Freigabe im selben Lauf** | „a replace frees its target's name for use in the same run, and that has to be allowed" | frei nur für die **eigene** Replace-Zeile; ein Rename auf einen Namen, den ein **anderes** Replace freigibt, ist eine Verletzung | Codex-Finding 4: die Engine läuft in Quellreihenfolge, der Rename kann vor dem Replace kommen und 409 bekommen; die Alternative — Abhängigkeiten im Lauf ordnen — kostet eine Topologie samt Zyklusfall für etwas, das in zwei Läufen sauber geht. Der eigene Fall (REMOVE gibt frei, der ADD derselben Zeile nimmt) bleibt erlaubt und ist, was das Issue mit „the whole point of the action" meint |
| **Bestandsdoppel in `toAdd`** | „No two produced aliases may be equal" | Doppel zwischen zwei **unveränderten** `toAdd`-Zeilen sind keine Verletzung | Codex-Finding 6, am Code belegt: `dedupeImportRows` faltet nur nach Id, der Fall existiert heute, 7TV lehnt die zweite Zeile ab; die Regel wörtlich genommen blockierte einen unveränderten Dialog gegen AK 2/5 |
| **Slot-Formel** | „renames as +1 and a replace as +1 minus the number of target entries" | `delta = addCount − removedEntryCount`, `addCount` = alle ADD-Mutationen | Codex-Finding 5: keine Abweichung vom Issue, sondern die Korrektur einer Doppelzählung in der ersten Fassung dieses Plans |
| `sourceName` in der Protokollzeile | nur `alias` | beides | Ein Rename ist sonst nicht rekonstruierbar |
| `removedTarget.confirmed` | nicht spezifiziert | im Ergebnisprotokoll je Replace | Mit `unknown` gibt es Zeilen, deren REMOVE weder bestätigt noch widerlegt ist; ein Leser muss das ohne Statusvergleich sehen |
| `transfer-run`-CSV | nicht spezifiziert | Spaltenliste in T7 | Der Export-Dialog bietet immer CSV neben JSON (§7.4); ohne Spaltenvertrag entstünde er ad hoc |
| Spec-Querverweis F7 | „correct … while this issue is being built" | eigener `docs:`-Commit in T6 | Regel 2: logisch getrennter Commit |
| Konzept-Ausblick auf #201 | „point the outlook at this issue" | T9 | reine Doku, ans Ende |
| i18n als eigener Task | eigene Aufwandszeile | in jedem Task, der Text erzeugt | Hausregel: Schlüssel landen mit dem Feature in beiden Locales, nicht als Sammelschritt |
| Effort-Schätzung | 41 h | nicht neu geschätzt | Fassung 2 ändert den Umfang um den Pflicht-Download mit Live-Verifikation (grob +4 h), den Ausgang `unknown` samt Nachlesen (+4 h) und die zwei Validierungskorrekturen (+1 h); Abschnitt 2 der ersten Fassung (+3 h) entfällt größtenteils |

---

## 8. Rückweg

Frontend-Änderungen sind per Revert des PR rückgängig; die eine additive DTO-Eigenschaft
(`EmoteListItemDto.ImageUrl`) und der neue Endpunkt sind unabhängig davon harmlos, wenn sie bleiben
(kein Aufrufer, kein Schema). Keine Migration. Bereits heruntergeladene `transfer-run`-Dateien —
Rückweg-Datei wie Ergebnisprotokoll — bleiben in einem revertierten Build **mit Grund** unlesbar
(`wrongKind`) — das ist die vorgesehene Antwort, kein Bruch; als JSON bleiben sie für einen
Menschen lesbar, und nur darauf kommt es beim Rückweg an. Ein bereits gelaufener Replace ist
**nicht** durch Revert rückgängig; die Rückweg-Datei, die vor seinem ersten REMOVE auf der Platte
lag, ist der einzige Weg zurück — deshalb der Pflicht-Download, deshalb die Live-Verifikation davor.
Eine `unknown`-Zeile im Ergebnisprotokoll ist nach einem Revert genauso unbekannt wie davor: der
Nutzer prüft das Set bei 7TV, wie das Dock es ihm gesagt hat.

---

## 9. Nachtrag: Codex-Review vom 2026-09-23 (gpt-6-sol, adversarial)

Sechs Befunde über die erste Fassung. Je übernommen oder mit Grund zurückgewiesen; die Folgen
stehen in den Tasks und den Abschnitten 1, 2, 4, 7.

| # | Schwere | Befund | Entscheidung | Folge |
|---|---|---|---|---|
| 1 | high | Replace kann Aliase löschen, die im Rückweg-Record fehlen: `removedTarget` kam aus der Vorschau, die Vorprüfung testete nur die Quell-Id | **Übernommen.** Vor dem Download liest der Dialog das Ziel-Set live und vergleicht jede Replace-Ziel-Id samt Alias-Menge; bei Abweichung kein Download, kein Start, Neuladen und Neu-Bestätigen; die Datei entsteht aus dem Read. Der Frischcheck im Flow prüft ein zweites Mal. Das Restfenster zwischen letztem Lesen und jedem REMOVE bleibt und ist nicht zu schließen (`already-present-filter.ts:55-57`); das Ergebnisprotokoll trägt deshalb, was das Nachlesen nach dem Lauf über die tatsächlich genommenen Aliase liefert | Abschnitt 2, T5 (`verifyReplaceTargets`), T8 |
| 2 | high | Die Rückweg-Datei entsteht zu spät | **Übernommen**, durch Frage 4 entschieden: Pflicht-Download vor dem ersten REMOVE. Test für den Abbruch zwischen den beiden Mutationen: T4 (`cancel()` zwischen Schritt 1 und 2 ⇒ `failed`, `failedStep 1`, kein ADD gesendet) | Abschnitt 2, T4, T7, T8 |
| 3 | high | Eine verlorene ADD-Antwort gilt als bestätigte Lücke | **Übernommen.** Am Code geprüft: `runOne` macht aus jeder `HttpErrorResponse` außer 429 ein `failed` mit `httpStatus = error.status`, Status 0 wird `networkError` (`seven-tv-run-engine.ts:406-424, 566`). Vertrag: Status 0/502/503/504 ⇒ `unknown` für Operationen mit `transportLossIsUnknown` (der Import-Lauf mit Replace, für alle seine Zeilen — Add, Rename, Replace, Adopt); 500 und jede GQL-Ablehnung bleiben `failed`; ein Live-Nachlesen nach dem Lauf klärt jede `unknown`-Zeile vor jeder Meldung; unklärbar ⇒ in keiner Meldung, `unknown` im Protokoll, Hinweis im Dock. Delete/Restore/Add-only unverändert | T4, T5, T7 |
| 4 | medium | Quellreihenfolge verhindert eine erlaubte Alias-Wiederverwendung | **Übernommen, Variante „ablehnen".** Regel 2 in T3 gibt die Aliase eines Replace nur für die eigene Zeile frei; ein Rename auf einen fremd freigegebenen Namen ist eine Verletzung, in beiden Zeilenreihenfolgen getestet. Grund: konsistent mit Frage 7, keine Abhängigkeitsordnung samt Zyklusfall in der Engine, der Fall geht in zwei Läufen. Abweichung vom Issue-Wortlaut in Abschnitt 7 festgehalten | T3, Abschnitt 7 |
| 5 | medium | Slot-Formel zählt Rename und Replace doppelt | **Übernommen** — der Befund traf zu: die erste Fassung addierte `renameCount` und `replaceCount` auf ein `addCount`, das sie schon enthielt. Jetzt `delta = addCount − removedEntryCount`, `addCount` = alle ADD-Mutationen; drei Einzeltests (Rename, Replace, Replace auf Duplikat) in T3 und T8 | T3, T8, AK 21 |
| 6 | medium | Die Laufvalidierung kann einen unveränderten Import blockieren | **Übernommen** — am Code belegt: `dedupeImportRows` faltet nur nach `sevenTvEmoteId`, `buildImportPreview` prüft Namen nur gegen das Ziel; zwei Quellzeilen gleichen Namens landen heute beide in `toAdd`, 7TV lehnt die zweite ab. Regel 1 in T3 nimmt Doppel zwischen zwei **unveränderten** `toAdd`-Zeilen aus; sobald eine Entscheidung beteiligt ist, gilt sie voll. Test: zwei `toAdd`-Ids mit gleichem Alias, kein Zielkonflikt ⇒ `ok: true`, Plan wie heute | T3 |

Keiner der sechs Befunde ist zurückgewiesen. Offene Fragen an den Betreiber ergeben sich aus der
Runde nicht: die Entscheidungen zu 4 (ablehnen statt ordnen) und 3 (Statusliste 0/502/503/504,
500 bleibt `failed`) sind Planentscheidungen mit Grund, die der Betreiber beim Lesen kippen kann,
ohne dass ein Task davon abhängt, bevor er beginnt.
