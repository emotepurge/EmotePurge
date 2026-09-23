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

**Fassung 3 (2026-09-23).** Die zweite Fassung (`4ef773c`) ist um die fünf Befunde der zweiten
Codex-Runde ergänzt, alle vom Betreiber angenommen (Abschnitt 9, Runde 2): bestätigte REMOVEs
gehen unabhängig vom Zeilenstatus ins Audit (`completedSteps`), aliaslose Zieleinträge werden
geprüft und abgebildet, das Nachlesen läuft auf einem lauf-gebundenen Ergebnis statt auf der
Engine-Queue, HTTP 500 ist `unknown`, und die Zeichensatzprüfung gilt nur für getippte Aliase.
Betroffen sind T2, T3, T4, T5, T7, T8 sowie die Abschnitte 1, 2, 4, 7, 8, 9.

**Fassung 4 (2026-09-23).** Betreiber-Entscheidung gegen „der Mensch stellt per ADD von Hand
wieder her": beide `transfer-run`-Stufen sind über den bestehenden Weg „Wiederherstellen" ladbar
und stellen nur die entfernten Ziel-Einträge wieder her, aliaslose per ADD ohne Alias, und nur wo
der Name frei ist („nur Lücken schließen"). Neuer Task **T7b**; T7 verliert die Restore-Abweisung;
T9 und T10 je einen Fall; Abschnitte 1, 2, 4 (AK 18 revidiert, R1–R4), 5, 7, 8 angepasst. Ein
Befund am Restore-Code grenzt die Vorgabe auf getrackte Ziele ein (T7b, Abschnitt 7).

**Fassung 5 (2026-09-23).** Betreiber-Entscheidung zu diesem Befund: **keine Löschung ohne
Restore-Weg** — „Ziel ersetzen" ist für ein ungetracktes Ziel-Set gesperrt, in Validierung (T3,
Regel 7, erkannt an `targetChannelName !== null`) und Oberfläche (T8, ausgegraut mit Grund);
Skip, Rename, Adopt bleiben. Damit **entfällt T6** (der set-zentrierte `sync-deleted` hatte keinen
anderen Grund); Frage 1 ist überholt, Frage 6 eingeengt, AK 19 in der ungetrackten Hälfte
gegenstandslos, R5 neu; T10 verlegt Replace und Restore auf ein getracktes Set und belegt an olafs
Set die Sperre. Folge-Issue „Restore pro Set", mit dem die Sperre fällt.

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
| **Der Audit-Leseweg kennt den Besitzer-Login schon** für eine `emoteCount`-Zeile mit `emoteSetId`: `ReadTargetEmoteSet(root, "emoteSetId")` liest `targetOwnerTwitchLogin` mit, `renderTargetSet` zeigt dann die „von {owner}"-Form. Ein set-zentrierter `sync-deleted` bräuchte also **keine** Änderung an Projektion oder `audit-row.ts`. | `AuditLogQueryService.cs:155-162, 250-263`, `audit-row.ts:128-146` | Seit Fassung 5 ohne Task: T6 ist entfallen; der Befund gehört ins Folge-Issue „Restore pro Set" |
| **`EmoteUsageTotal` trägt `imageUrl`, `ForeignEmoteRow` ebenso, die Datei nicht.** Drei `toImportRow`-Stellen: `usage-export-purposes.ts:58`, `foreign-import-flow.ts:52, 99`; der Parser `import-source-parser.ts:52` kennt kein Bild. | s. links | T1: drei Produzenten liefern die URL, der vierte `null` |
| **Der `ImportOrigin`-Union-Mechanismus** (zwei erschöpfende Helfer, `assertUnreachableOrigin`) zeigt, wie hier Vokabeln gesichert werden. Das neue Aktions-Union der Auflösung bekommt dasselbe Muster: jede Auseinandernahme erschöpfend, ein fünfter Wert ein Compile-Fehler. | `import-source.ts:88-135` | T3, T5, T7 |
| **Ein Transportfehler ist heute ein Fehlschlag.** `runOne` fängt jede `HttpErrorResponse` außer 429 und macht daraus `success: false` mit `httpStatus = error.status` — für einen Netzwerkabbruch Angulars `0`, übersetzt als `networkError`. Ob die Mutation bei 7TV angekommen ist, weiß niemand; für einen REMOVE oder ADD ohne Antwort heißt `failed` also „unbekannt", und die Meldung ließe eine womöglich hinzugefügte Id aus (Codex-Finding 3). | `seven-tv-run-engine.ts:406-424, 566-568` | T4 führt den Ausgang `unknown` ein, T5 klärt ihn per Live-Nachlesen |
| **Zwei Quellzeilen mit gleichem Namen und verschiedener Id landen heute beide in `toAdd`.** `dedupeImportRows` faltet nur nach `sevenTvEmoteId`, `buildImportPreview` prüft Namen nur gegen das Ziel, nie untereinander. 7TV lehnt dann die zweite ab — heutiges Verhalten, informativ wie `invalidNames`. Eine Validierung, die „keine zwei erzeugten Aliase gleich" wörtlich nimmt, blockierte diesen unveränderten Dialog (Codex-Finding 6). | `import-source.ts:141-144`, `import-preview.ts:110-117` | T3 nimmt die Bestandsdoppel von der Regel aus |
| **Der `filterAlreadyPresent`-Read liest bereits alle Aliase je Id** (`aliasesById`), tokenlos, aus dem globalen Bucket. Genau diese Daten braucht die Live-Verifikation der Replace-Ziele vor dem Download (Codex-Finding 1) — ein Read, drei Verwendungen: Duplikatfilter, Zielprüfung, Rückweg-Datei. | `seven-tv-set-entries.ts:39-60`, `already-present-filter.ts:87-99` | T5, T8 |

### 0.3 Modelle je Task

`sonnet` für klar spezifizierte Implementierung und Tests; `opus` an drei Stellen mit Begründung
(T4 Engine, T5 Service, T8 UI-Schritt) plus T7b (Restore-Filter, derselbe Grund wie T5.1 in
Plan-200); `haiku` nirgends — kein Task ist rein mechanisch. T0 und T10 haben kein Modell im Sinn
einer Implementierung: T0 ist erledigt, T10 ist ein Handgriff des Betreibers mit einem Subagent als
Protokollant. T6 ist entfallen (Fassung 5).

### 0.4 Branch und Commits

- **Branch `feat/230-name-conflicts` von `feat/emote-sets-200`**, PR gegen den Integrationsbranch.
  Worktree-Fallen aus Plan-200 0.4 gelten: aus dem Worktree nur bauen, nicht `docker compose up`;
  Live-Läufe aus dem Haupt-Checkout.
- **Ein Commit je Task**, Conventional Commits, englisch, ohne `#`-Referenzen in Commit-Metadaten
  (Memory: „Keine Ticketnummern in Git-Metadaten"). Vorschläge stehen je Task.
- **Regel 3:** Der DECISIONS-Eintrag („der Import-Dialog wird eine löschende Operation") liegt im
  Commit von **T8**, weil erst dort der REMOVE-Pfad für einen Nutzer erreichbar wird. Er nennt in
  seiner `Betrifft:`-Zeile auch die Dateien aus T4, T7 und T7b — ein Eintrag, nicht vier.

---

## 1. Verträge — wo sie stehen, was der Plan festlegt

Nichts aus dem Issue wird hier wiederholt. Die Tabelle sagt je Vertrag, welcher Task ihn trägt
und was der Plan **zusätzlich** zum Issue festlegt (mit Grund in Abschnitt 7).

| Vertrag | Quelle | Tasks | Plan-Zusatz |
|---|---|---|---|
| Aktionen je Zeile, Ausschlüsse, Defaults | Issue „Actions per row" | T3, T8 | — |
| Laufform: eine Queue-Zeile je Entscheidung, zwei Mutationen je Replace, Pacing/Backoff zwischen REMOVE und ADD | Issue „Run shape" | T4, T5 | Zeile trägt `failedStep`; Abbruch (`cancel`) zwischen REMOVE und ADD endet **`failed`** mit Lückengrund, nicht `cancelled` (Frage 3, entschieden) |
| Teilfehlschlag ohne Rollback | Issue „Partial failure", Entscheidung 4 | T4, T5, T7 | Lückengrund ist übersetzter Text **und** Feld `failedStep` im Protokoll |
| **Verlorene Antwort ist kein Fehlschlag** | Codex-Finding 3 (Runde 1), 4 (Runde 2) | T4, T5, T7 | Ausgang `unknown` für einen Schritt, dessen Antwort **kein auswertbarer GraphQL-Fehler und keine Ablehnung vor der Verarbeitung** ist: keine Antwort (Status 0) oder jede 5xx-Antwort — 500 eingeschlossen. Eine 4xx-Antwort aus 7TVs HTTP-Schicht (401/403 Token, 429 Limit, sonstige) heißt „abgelehnt, bevor verarbeitet" und bleibt `failed`/Backoff/Abbruch wie heute. Nach dem Lauf **ein** Live-Nachlesen klärt jede `unknown`-Zeile auf einem **lauf-gebundenen** Ergebnis; bleibt sie unklärbar, erscheint ihre Quell-Id in **keiner** Import-Meldung — **ein bestätigter REMOVE geht aber immer in die Löschmeldung ein**, unabhängig vom Endstatus der Zeile (Runde 2, Finding 1) |
| Laufzeitkollision (Name inzwischen belegt) | T0-Kommentar | T4, T5 | Erkennung an `extensions.status === 409`, eigener Grund `import.errors.nameTakenNow`, **kein** Laufabbruch |
| **Live-Verifikation der Replace-Ziele vor dem Lauf** | Codex-Finding 1 (Runde 1), 2 (Runde 2) | T5, T8 | vor dem Download liest der Dialog das Ziel-Set live (`loadSevenTvSetEntries`); weicht ein Replace-Ziel vom bestätigten Stand ab (andere Aliase, **aliasloser Eintrag hinzugekommen oder weggefallen** — `aliaslessIds` wird an **beiden** Prüfstellen gelesen —, Eintrag weg, Name anderer Id), gibt es keinen Download und keine Startfreigabe, sondern Neuladen und Neu-Bestätigen; die Rückweg-Datei entsteht aus den **live gelesenen Einträgen**, aliaslose eingeschlossen; der Frischcheck in `import-flow.ts` prüft dieselben Ziele ein zweites Mal als letztes Tor. Restfenster: zwischen letztem Lesen und jedem einzelnen REMOVE, nicht zu schließen (`already-present-filter.ts:55-57`) |
| **Aliaslose Zieleinträge** | Codex Runde 2, Finding 2; Betreiber 2026-09-23 | T2, T5, T7, T7b, T8 | Ein Replace-Ziel darf aliaslose Einträge haben; die Rückweg-Datei bildet sie ab (`removedTarget.entries`, je Eintrag `alias: string \| null`, dazu `defaultName` aus dem Live-Read), sie zählen in `removedEntryCount`, und **der Restore-Weg stellt sie per ADD ohne Alias wieder her** (7TV fällt auf den Standardnamen zurück — dieselbe Regel, die `ADD_EMOTE_MUTATION`s Kommentar in `seven-tv-import.service.ts:34-40` nennt). Kein Sperren, kein Handgriff von Hand (Grund in Abschnitt 7) |
| `transfer-run`-Protokoll in **zwei Stufen**; **ladbar für den Restore**, nicht als Import-Quelle | Issue „The transfer protocol", Frage 4 (entschieden), Betreiber 2026-09-23 | T7, T7b, T8 | eine Envelope-Art, `meta.stage: 'planned' \| 'finished'`; eigene `TRANSFER_RUN_FORMAT_VERSION = 1`; Envelope-`channelName` = Zielkanal oder `''` bei ungetracktem Ziel, `meta` trägt Set, Besitzer, Herkunft. **Beide Stufen gehen über den bestehenden Einstieg „Wiederherstellen"** (`file-import-step.ts` → Parser → `filterAlreadyPresentForRestore` → `SevenTvRestoreService`): `planned` ⇒ alle geplanten Ziel-Einträge, `finished` ⇒ die mit `removedTarget.confirmed`; nur Ziel-Einträge, nie Quell-ADDs, Renames oder Adopts. **Nur Lücken schließen:** ein Alias, den inzwischen eine andere Id hält, wird vor dem Lauf mit Grund aussortiert. Das Verbot bleibt **nur** für das Einlesen als Import-Quelle (`parseImportSource`). AK 18 ist damit in seiner Restore-Hälfte durch den Betreiber revidiert (Abschnitt 4, 7) |
| **Pflicht-Download vor dem ersten REMOVE** | Frage 4 (entschieden) | T7, T8 | im Dialog, nach der Zusammenfassung, vor „Starten"; nur bei `removeCount > 0`; Abschnitt 2 |
| Löschzahl in der Zusammenfassung, keine getippte Bestätigung | Issue „Guard", Entscheidung 6 | T3, T8 | Zahl wird aus **denselben** Entscheidungen abgeleitet, aus denen der Lauf gebaut wird — eine Quelle |
| Meldungen: `sync-imported` + Löschmeldung | Issue „Backend bookkeeping" | T5 | Löschmeldung **nur** über das kanalgebundene `sync-deleted`, weil ein Replace nur für getrackte Ziele wählbar ist (Fassung 5); der set-zentrierte `sync-deleted` (T6) ist **entfallen**; **Adopt meldet nichts** (Frage 2, entschieden) |
| **Keine Löschung ohne Restore-Weg** | Betreiber 2026-09-23 (Fassung 5) | T3, T8 | „Ziel ersetzen" ist für ein ungetracktes Ziel-Set gesperrt — in der Validierung (Regel 7, `targetIsTracked` aus `targetChannelName !== null`) **und** in der Oberfläche (ausgegraut, Grund „Nur für getrackte Kanäle wiederherstellbar"); Skip, Rename, Adopt bleiben. Die Sperre fällt mit dem Folge-Issue „Restore pro Set" (set-zentrierter `sync-deleted` + `sync-restored`, Restore-Einstieg ohne Kanalseite) |
| Bilder: `imageUrl` in beiden Modellen, Platzhalter statt abgeleiteter URL | Issue „Images" | T1, T8 | — |
| Entscheidungs-Validierung über den ganzen Lauf | Issue „Decision validation" | T3 | fünfte Regel: kein Zieleintrag wird von zwei Zeilen berührt; Adopt gibt den alten Alias **nicht** frei (Frage 7, entschieden); **ein Replace gibt seine Aliase nur für seine eigene Zeile frei**, nicht für eine fremde (Codex-Finding 4); **Namensdoppel innerhalb der unveränderten `toAdd`-Zeilen sind keine Verletzung** (Codex-Finding 6); **die Zeichensatzprüfung gilt nur für Aliase, die der Nutzer selbst tippt (Rename)** — `toAdd`-Zeilen mit `invalidNames` laufen wie heute (Runde 2, Finding 5) |
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
   auf **Eintragsebene** mit dem bestätigten Stand: dieselbe Id, dieselbe Alias-Menge
   (`aliasesById`), **derselbe Bestand an aliaslosen Einträgen (`aliaslessIds`)**, der kollidierende
   Name gehört noch dieser Id, `complete === true`. Ein aliasloser Eintrag ist ein Eintrag, den der
   REMOVE ebenso nimmt (Sonde 5: **ein** REMOVE nimmt jeden Eintrag der Id) — eine Prüfung, die nur
   Aliase vergleicht, sähe ihn nicht (Codex Runde 2, Finding 2). Solange der Read läuft, ist der
   Knopf gesperrt und der Grund steht daneben (`aria-describedby`, wie `loadingHint`).
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
- **Beide Dateien sind der Rückweg, nicht nur die Aufzeichnung (Betreiber-Entscheidung
  2026-09-23, T7b).** Der bestehende Einstieg „Wiederherstellen" liest sie ein — die Rückweg-Datei
  für den Fall „Tab mitten im Lauf gestorben" (jeder geplante Ziel-Eintrag wird angeboten; was nie
  entfernt wurde, liegt noch im Set und fällt über den Duplikatfilter heraus), das Ergebnisprotokoll
  für den Fall „ADD nach REMOVE gescheitert" (nur bestätigte REMOVEs). Wiederhergestellt werden
  **nur die entfernten Ziel-Einträge**, unter genau den Aliasen, die sie hatten, aliaslose per ADD
  ohne Alias. **Nur Lücken schließen:** hält nach einem geglückten Replace das Quell-Emote den
  Namen, wird der alte Ziel-Eintrag **nicht** wiederhergestellt und nichts entfernt — die Zeile
  erscheint vor dem Lauf mit Grund „Name belegt", statt ein Ticket für einen sicheren 409 zu
  verbrennen. Ein vollständiges Rückgängigmachen eines Replace (Quelle wieder raus, Ziel wieder
  rein) ist **nicht** Teil von #230 — Folge-Issue (Abschnitt 7). Der Restore bleibt vom Nutzer
  ausgelöst; Entscheidung 4 (kein Auto-Rollback) steht.
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
T3 muss das sehen (fünfte Regel). **Aliaslose Einträge (K5-Fixrunde, `aliaslessIds`):** die
Ziel-Liste des Dialogs ist `name`-basiert und kennt den Unterschied nicht — **Prüfaufgabe des
Tasks**, wie ein Eintrag mit `alias: null` heute durch `ForeignEmoteSetService.cs:141` und
`import-target-loader.ts:197` in `EmoteListItem.name` landet (Standardname, leer oder gar nicht).
Das Ergebnis steht als Kommentar an `nameCollisionRows`; die verbindliche Sicht auf aliaslose
Einträge hat erst der Live-Read in T8/T5, und die Rückweg-Datei entsteht aus ihm, nicht aus dieser
Vorschau. Für die Zählung (`removedEntryCount`, AK 21) gilt die Vorschau; weicht der Live-Read ab,
ist das eine Abweichung (Abschnitt 2, Punkt 3) und die Vorschau wird neu geladen.

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
  jede Violation mit Regelname und den Schlüsseln der beteiligten Zeilen. **Sieben Regeln**, alle
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
  3. **Jeder vom Nutzer getippte Alias passiert `isNameRejectedBySevenTv`** — das ist genau der
     Alias einer **Rename**-Zeile; leer oder nur Leerraum ist eine Verletzung, keine Ausnahme.
     **Nicht** geprüft werden Aliase, die aus der Quelle stammen: unveränderte `toAdd`-Zeilen
     (die Vorschau lässt `invalidNames` bewusst im Lauf — „7TV entscheidet", `import-preview.ts:47-52`;
     ein unveränderter Dialog darf nicht blockieren, AK 2/5 — Codex Runde 2, Finding 5), der ADD
     einer Replace-Zeile und der neue Alias eines Adopt (beide tragen den Quellnamen, den der Nutzer
     nicht getippt hat; eine Ablehnung dort ist 7TVs Sache und endet als `failed`-Zeile mit 7TVs
     Grund, wie heute bei `invalidNames`).
  4. **Keine zwei Zeilen ersetzen denselben Zieleintrag.**
  5. **Kein Zieleintrag wird von zwei Zeilen berührt** — Replace und Adopt derselben Ziel-Id
     schließen sich aus (Abschnitt 1, fünfte Regel der ersten Fassung).
  6. **Adopt nur bei `adoptBlocked === null`** — das Modul verlässt sich nicht auf die UI.
  7. **Replace nur bei getracktem Ziel** (Betreiber-Entscheidung 2026-09-23, Fassung 5: **keine
     Löschung ohne Restore-Weg**). `validateResolution` bekommt dafür einen dritten Parameter
     `context: { targetIsTracked: boolean }`; eine `replaceTarget`-Entscheidung bei
     `targetIsTracked === false` ist eine Verletzung mit Regelname `replaceNeedsTrackedTarget`, die
     UI (T8) bietet die Aktion in diesem Fall ausgegraut mit Grund an, aber die Regel steht hier,
     damit kein Aufrufer sie umgehen kann. Skip, Rename und Adopt bleiben erlaubt. **Woran der
     Dialog „getrackt" erkennt:** an `ImportConfirmDialogData.targetChannelName !== null` — der
     Vertrag steht seit K2 (`import-confirm-dialog.ts:32-36`, Spec 8.6, AK 39): `null` genau für
     ein ungetracktes Ziel, gefüllt aus `toTargetChannelName` in `import-flow.ts`, das für einen
     `'chosen'`-Pick `choice.channelName` liest — und `ImportTargetChoice.channelName` ist per
     Picker-Vertrag genau dann gesetzt, wenn `isTracked` (`import-target-dialog.ts:75`,
     `import-flow.ts:155-162` wirft sonst). Kein zweites Flag, keine neue Ableitung: dasselbe
     Feld, das schon Kopfzeile, Meldungsweg und Resync-Entscheidung steuert. Die Sperre fällt mit
     dem Folge-Issue „Restore pro Set" (Abschnitt 7).
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
`removeCount === 0` (AK 5) · **keine Entscheidungen, eine `invalidNames`-Zeile in `toAdd` ⇒
`ok: true`, Zeile im Plan (Runde 2, Finding 5)** · Rename auf einen Alias mit Leerzeichen ⇒
Verletzung (Regel 3 greift, weil getippt) · Projektion: einzelner Rename +1, gewöhnlicher Replace 0,
Replace auf doppelt belegtes Ziel −1, gemischt (Add + Rename + Replace auf Duplikat = 3 − 2 = +1),
**Replace auf ein Ziel mit einem benannten und einem aliaslosen Eintrag −1** · **Replace bei
`targetIsTracked: false` ⇒ Verletzung `replaceNeedsTrackedTarget` mit der Zeile; dieselbe
Entscheidungsmenge bei `true` ⇒ `ok`; Rename und Adopt bei `false` ⇒ `ok`** (Regel 7).

**Tests:** `conflict-resolution.spec.ts` **+19** (Liste oben), `slot-projection.spec.ts` **+3**
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
- **Der Ausgang `unknown` (Codex-Finding 3, verschärft durch Runde 2, Finding 4).**
  `RunItemStatus` bekommt den Wert `'unknown'`. Die Regel ist **keine Statusliste**, sondern eine
  Dreiteilung dessen, was `runOne` heute schon unterscheidet (`:371-424`):
  - **Eine GraphQL-Antwort** (HTTP 200 mit oder ohne `errors`) ist eindeutig: Erfolg, 429-Backoff,
    oder `failed` mit `errorCode`/`gqlStatus` — wie heute, nie `unknown`.
  - **Eine HTTP-Ablehnung vor der Verarbeitung** — jede `4xx`-Antwort aus 7TVs HTTP-Schicht — ist
    ebenso eindeutig: die Mutation ist nicht gelaufen. `401`/`403` bleiben `failed` mit
    `tokenInvalid`, `describeHttpError` löscht das Token, und `abortsForMissingPrivileges` bricht ab
    (der Body liegt außerhalb des Schemas, `seven-tv-import.service.ts:60-62` — genau deshalb wird
    hier nach `httpStatus` entschieden, nicht nach einem GQL-Fehler); `429` bleibt Backoff; eine
    sonstige `4xx` bleibt `failed` mit `genericStatus`.
  - **Alles andere ist `unknown`**: keine Antwort (`httpStatus 0` — Netzwerkabbruch, Timeout, heute
    der `networkError`-Zweig `:566`) und **jede `5xx`**, `500` eingeschlossen. Runde 2 hat die
    Planentscheidung der ersten Fassung („500 bleibt `failed`, dort hat 7TV geantwortet") gekippt,
    der Betreiber hat zugestimmt: eine 500 sagt, dass etwas fehlschlug, nicht **wo** — vor, während
    oder nach dem Schreiben —, und der Vertrag muss die Unsicherheit tragen, nicht wegdefinieren.
  Nur eine Operation, die es verlangt (`RunOperation.transportLossIsUnknown: true`), bekommt den
  Ausgang; die Voreinstellung `false` reproduziert heute: Delete, Restore und der Import-Lauf ohne
  Replace bleiben bei `failed`. Eine `unknown`-Zeile: kein weiterer Schritt (ein ADD auf ein
  womöglich noch besetztes Ziel ist ein Ticket für einen 409), `failedStep` = der Schritt ohne
  Antwort, `abortOn` wird **nicht** gerufen (es gibt keinen Grund zu bewerten), der Lauf geht
  weiter, `progress` zählt sie als erledigt, `doneKeys` enthält sie **nicht**.
- **`completedSteps` je Zeile (Runde 2, Finding 1).** `RunQueueItem` zählt die Schritte, die 7TV
  **bestätigt** hat (Erfolgsantwort), unabhängig vom Endstatus der Zeile: eine Replace-Zeile mit
  `completedSteps >= 1` hat einen bestätigten REMOVE — ob sie `done`, `failed` oder `unknown`
  endet. Das ist die eine Größe, aus der T5 die Löschmeldung und T7 `removedTarget.confirmed`
  ableiten; ein Leser, der es aus `status` und `failedStep` rekonstruieren müsste, verlöre genau
  den Fall „REMOVE bestätigt, ADD ohne Antwort, Nachlesen gescheitert".
- **Kein `settle` auf der Engine (Runde 2, Finding 3).** Die erste Fassung ließ T5 die Zeilen der
  Engine-Queue nachträglich klären. Die Queue gehört aber dem **nächsten** Lauf, sobald einer
  startet — `isRunning` fällt in `finish()`, **bevor** `onComplete` feuert, und der Arbiter gibt
  den nächsten Start genau daran frei (`seven-tv-import.service.ts:83-92`). Die Engine bleibt
  lauf-agnostisch: sie liefert das `RunResult` als Snapshot (wie heute) und bietet **keinen**
  Rückkanal. Geklärt wird auf einem lauf-gebundenen Ergebnis im Service (T5).

**Grenzfälle:** Rate-Limit-Pause **zwischen** REMOVE und ADD — die Zeile steht `in-progress`, die
Anzeige zeigt den Countdown wie heute; nach der Pause läuft der ADD, nicht der REMOVE noch einmal
(Retry gilt je Schritt). `abortOn` sagt bei Schritt 2 „abbrechen" ⇒ die Zeile ist `failed` mit
`failedStep = 1`, der Rest `cancelled`. Ein Hook, der wirft, wird je Schritt wie heute behandelt.
Ein 429 bleibt Backoff, nie `unknown` — 7TV hat geantwortet. Ein `unknown` in Schritt 1 einer
Replace-Zeile ⇒ Schritt 2 wird nicht gesendet.

**Tests:** `seven-tv-run-engine.spec.ts` (24) **+12**: zwei Schritte in Reihenfolge mit Pacing
dazwischen; Schritt-1-Fehler unterdrückt Schritt 2; Schritt-2-Fehler ⇒ `failedStep = 1`, Lauf geht
weiter; Rate-Limit zwischen den Schritten wiederholt nur Schritt 2; **`cancel()` zwischen den
Schritten ⇒ `failed` + `failedStep = 1`, kein ADD gesendet, Rest `cancelled`** (Codex-Finding 2);
`gqlStatus` erreicht `abortOn` (409-Fixture aus dem T0-Kommentar); **Status 0 und Status 500 auf
einer Operation mit `transportLossIsUnknown` ⇒ `unknown`, kein Schritt 2, `abortOn` nicht
gerufen**; **derselbe Status 0 auf einer Operation ohne das Flag ⇒ `failed` wie heute**; **401 auf
einer Operation mit dem Flag ⇒ `failed`, Token gelöscht, `abortOn` mit `httpStatus 401` gerufen —
nie `unknown`**; **`completedSteps` ist 1 für eine Replace-Zeile, deren ADD `unknown` blieb, und 0
für eine, deren REMOVE `unknown` blieb**; Einschritt-Operation unverändert (Snapshot der gesendeten
Requests eines Zwei-Zeilen-Laufs gegen den heutigen Stand — erlaubt, weil Wire-Vertrag, nicht
Vorlage). `run-progress-panel.spec.ts` **+1**
(`unknown` zählt in `finished`, erscheint in der Fehlerliste mit eigener Wortfamilie
`<prefix>.unknownOutcome`). `seven-tv-delete.service.spec.ts` / `seven-tv-restore.service.spec.ts` /
`seven-tv-import.service.spec.ts`: nur Signatur-Fixtures, **0 neue Fälle**, alle bestehenden grün.

**Abnahme:** `grep -n "doneKeys" web/src` unverändert; drei Bestandsdienste ohne Verhaltensänderung;
`purge-run-export.ts`' `readProtocolRow` liest `status as RunItemStatus` weiter — ein `unknown`
kann dort nur aus einer Datei kommen, die dieser Build nicht schreibt, und fällt als „nicht `done`"
aus dem Restore, wie jeder andere Nicht-`done`-Status. **AK 13 (Reihenfolge, Nachbarschaft), 14, 15
(Engine-Hälfte).**

**Commit:** `feat(seventv): let one run row issue a sequence of mutations`.
**Abhängigkeiten:** keine (parallel zu T1). **Modell:** `opus` — die Engine trägt drei Läufe,
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
- **Nachlesen nach dem Lauf, auf einem lauf-gebundenen Ergebnis, vor jeder Meldung (Runde 2,
  Finding 3).** `onRunComplete` erhält das `RunResult` als Snapshot und schreibt es **nicht**
  sofort als Endstand: `ImportRunInfo` bekommt `settlement: 'pending' | 'settled'`. Hat der Snapshot
  mindestens eine `unknown`-Zeile, liest der Service das Ziel-Set **einmal** live
  (`loadSevenTvSetEntries`, tokenlos, globaler Bucket) und klärt jede Zeile in einer **Kopie** der
  `items`; danach veröffentlicht er Kopie und Meldungen **atomar** in einem `run.set({ …finished,
  result: settled, settlement: 'settled' })` — geschützt durch `applyIfCurrent`: gehört die Antwort
  zu einem Lauf, der nicht mehr `run()` ist (ein zweiter Import hat gestartet, `reset()` lief), wird
  sie fallen gelassen, wie jede andere späte Antwort im R15-Muster. Ohne `unknown`-Zeile ist der
  Snapshot sofort `settled`. **Die Anzeige liest nicht mehr die Engine-Queue:** der Service bietet
  `items` = Engine-Queue, solange `isRunning()`, sonst `run()?.result.items` — damit zeigt das Dock
  nach dem Lauf das geklärte Ergebnis des **eigenen** Laufs, auch wenn die Engine längst die Queue
  des nächsten hält. Klärungsregeln:
  - ADD-Schritt unbekannt (Add, Rename, Replace-Schritt 2): Quell-Id unter dem Plan-Alias im Set ⇒
    `done`; nicht im Set ⇒ `failed` — bei Replace mit `removedButNotAdded`, sonst mit dem generischen
    `unknownOutcome`-Grund plus 7TVs letztem Text.
  - REMOVE-Schritt unbekannt (Replace-Schritt 1): Ziel-Id noch im Set ⇒ `failed`, `failedStep 0`,
    nichts ist passiert; Ziel-Id weg ⇒ `failed` mit `removedButNotAdded` — der REMOVE ist passiert,
    der ADD wurde nie gesendet; `completedSteps` wird auf 1 gesetzt (die Bestätigung kam per
    Nachlesen), die Id gehört in die Löschmeldung.
  - Adopt unbekannt: Ziel-Id unter dem Quellnamen ⇒ `done`; unter dem alten Alias ⇒ `failed`.
  - **Das Nachlesen scheitert oder ist `complete: false`:** die Zeilen bleiben `unknown`, das
    Ergebnis wird trotzdem `settled` veröffentlicht. Eine `unknown`-Zeile erscheint in **keiner**
    Import-Meldung — nicht als hinzugefügt —, das Ergebnisprotokoll schreibt `unknown`, und das Dock
    sagt es (`import.summary.unknownRows`, mit der Aufforderung, das Set bei 7TV zu prüfen). **Aber:
    ein REMOVE, den 7TV bestätigt hat (`completedSteps >= 1`), geht immer in die Löschmeldung ein**,
    auch wenn die Zeile `unknown` bleibt (Runde 2, Finding 1) — die Bestätigung ist eine Tatsache
    aus dem Lauf, nicht aus dem Nachlesen, und sie hängt nicht am Endstatus der Zeile.
  **Warum kein „Start gesperrt, bis das Nachlesen fertig ist":** der Arbiter leitet „ein Lauf ist
  aktiv" aus `isRunning` der drei Dienste ab (§4.3); eine Sperre bräuchte einen vierten Zustand im
  Arbiter, einen Sperrtext an allen 7TV-Startknöpfen und würde Delete und Restore mit blockieren —
  und den Guard gegen späte Antworten bräuchte es **trotzdem**, weil `reset()` und der
  Kanalwechsel dieselbe Wettlauflage erzeugen wie ein Neustart. Das R15-Muster existiert für genau
  diese Lage bereits an drei Stellen; es um eine vierte zu erweitern ist der kleinere Vertrag.
- **Meldungen nach dem Lauf (nach der Veröffentlichung des geklärten Ergebnisses):** `syncImported`
  für jede `done`-Zeile mit Aktion `add`, `renameSource`, `replace` (die hinzugefügten Ids, wie
  heute); zusätzlich eine **Löschmeldung** für jede Replace-Zeile mit **bestätigtem REMOVE**
  (`completedSteps >= 1`, direkt oder per Nachlesen) — **unabhängig vom Endstatus**: `done`,
  `failed` mit `failedStep 1`, oder `unknown` mit bestätigtem Schritt 1. Immer über
  `emoteAdminService.syncDeleted(channel, { emoteSetId, sevenTvEmoteIds })` (der bestehende Weg,
  Papierfall bei nicht-aktivem Set) — ein Replace gibt es nur für ein **getracktes** Ziel (Regel 7
  in T3, Fassung 5), also hat jede Löschmeldung einen Kanal; ein Plan mit Replace-Zeile und
  `targetChannelName === null` ist ein Programmierfehler und wirft in `startImport`, statt still
  ohne Meldung zu laufen. `adoptSourceName` meldet **nichts** (Frage 2, entschieden). Beide Meldungen haben eigene
  `SyncReportState`-Signale (`syncReport` bleibt für den Import, `removalReport` neu), eigenen
  Retry, und beide hängen am `ImportRunInfo`-Objekt (R15-Muster).
- `ImportRunInfo` wächst um `plan`, `settlement`, `removedCount` (bestätigte REMOVEs, für Dock und
  Protokoll) und `unknownCount`; der Service trägt das Signal **`destructiveRunActive`** =
  `isRunning() && plan.rows.some(replace)` für den `beforeunload` aus Abschnitt 2 (T7 hängt ihn an).
- **`filterAlreadyPresent` in `import-flow.ts`** läuft nur über die Planzeilen mit Aktion `add`,
  `renameSource`, `replace` — eine `adoptSourceName`-Zeile ist per Definition im Ziel und darf nicht
  herausfallen. Eine `replace`-Zeile, deren **Quell**-Id inzwischen im Ziel steht, fällt ganz heraus
  (kein REMOVE ohne ADD — konservativ, richtig).
- **`verifyReplaceTargets(entries, plan)` — das zweite Tor (Codex-Finding 1, Abschnitt 2 Punkt 5).**
  Aus **demselben** Read, den `filterAlreadyPresent` ohnehin macht, wird jede Replace-Zeile auf
  **Eintragsebene** geprüft: Ziel-Id im Set, benannte Aliase gleich der Planzeile
  (`aliasesById` gegen `target.entries`, die T8 aus der ersten Live-Verifikation geschrieben hat),
  **aliasloser Bestand gleich (`aliaslessIds.has(id)` gegen den `alias: null`-Eintrag der
  Planzeile — Runde 2, Finding 2)**, kollidierender Name gehört dieser Id. Eine abgewichene Zeile
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

**Tests:** `seven-tv-import.service.spec.ts` (29) **+17**: Rename sendet den Alias, nicht den
Quellnamen; Replace sendet REMOVE dann ADD, dieselbe `setId`, benachbart; Adopt sendet
`updateEmoteAlias` mit altem Alias im `id` und neuem als Argument, **kein** `addEmote`; 409 ⇒
`nameTakenNow`, Lauf läuft weiter; `failedStep = 1` ⇒ `removedButNotAdded` **und** Id in der
Löschmeldung; Löschmeldung über `syncDeleted` mit `emoteSetId` des Ziel-Sets; **Plan mit
Replace-Zeile und `targetChannelName === null` ⇒ `startImport` wirft, nichts wird gesendet**
(Fassung 5, zweite Sicherung hinter T3 Regel 7); Lauf ohne REMOVE ⇒ keine Löschmeldung; Retry der Löschmeldung nutzt
denselben Laufdatensatz; **Add-only-Plan setzt `transportLossIsUnknown` nicht, Replace-Plan schon;
unbekannter ADD wird per Nachlesen zu `done` geklärt und gemeldet; unbekannter REMOVE mit
verschwundenem Ziel wird zu `failed`/Lücke, `completedSteps 1`, und landet in der Löschmeldung;
gescheitertes Nachlesen lässt `unknown` stehen und hält die Quell-Id aus `syncImported` — **die
bestätigte REMOVE derselben Zeile steht trotzdem in der Löschmeldung** (Runde 2, Finding 1);
Meldungen warten auf die Veröffentlichung des geklärten Ergebnisses; **ein zweiter `startImport`
während des Nachlesens: das Nachlesen klärt und meldet den alten Lauf, `items` zeigt die neue
Queue, und der alte Lauf wird nie als `run()` veröffentlicht** (Runde 2, Finding 3); **`reset()`
während des Nachlesens ⇒ Antwort fallen gelassen, keine Meldung**; `items` wechselt von der
Engine-Queue auf `run().result.items`, sobald `isRunning` fällt**.
`already-present-filter.spec.ts` **+5** (`verifyReplaceTargets`: gleich, abgewichen durch neuen
Alias, abgewichen durch fremden Namensinhaber, **gemischt: ein benannter und ein aliasloser Eintrag
— gleich, wenn die Planzeile beide kennt, abgewichen, wenn der aliaslose neu ist oder fehlt** —
Runde 2, Finding 2). `import-flow.spec.ts` **+4**: Adopt-Zeile überlebt
den Frischcheck; Replace mit inzwischen vorhandener Quell-Id fällt ganz weg; **abgewichenes
Replace-Ziel fällt weg und wird gezählt; fehlgeschlagener Read hält jede Replace-Zeile zurück**.

**Abnahme:** Ein `TransferPlan` nur aus `add`-Zeilen erzeugt **exakt** die heutigen Requests und
Meldungen (Wire-Snapshot wie in T4). **AK 7 (Mutation), 12, 13, 15 (Grund), 17 (live verifizierte
Ziel-Aliase), 19 (Client-Hälfte).**

**Commit:** `feat(import): run a transfer plan with rename, replace and adopt rows`.
**Abhängigkeiten:** T3 (Plan-Typ), T4 (Schritte, `gqlStatus`). T6 ist entfallen (Fassung 5).
**Modell:** `opus` — hier entsteht der einzige löschende Pfad des Import-Dialogs, und die
Meldungslogik entscheidet, ob das Audit-Log eine Lücke sieht.

### T6 — Entfallen (Fassung 5, 2026-09-23)

Der set-zentrierte `POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted` samt
`MarkDeletedFromSetAsync` und `reportDeletedFromSet` hatte genau **eine** Begründung: die
Löschmeldung für ein Replace in ein **ungetracktes** Set. Mit der Sperre aus Fassung 5 („Ziel
ersetzen" ist für ungetracktes Ziel nicht wählbar — T3 Regel 7, T8) entsteht dort keine Löschung
mehr; Adopt ändert nur einen Alias und meldet nichts (Frage 2), Rename und Add fügen hinzu und
melden über den bestehenden set-zentrierten `sync-imported`. Es gibt keinen zweiten Grund für den
Endpunkt: der Befund aus 0.2 (die `emoteCount`-Leiter läse den Besitzer-Login mit) war eine
Erleichterung für ihn, kein Bedarf; #224 bleibt getrennt (Frage 1). Damit **entfällt auch die
Backend-Berührung dieses Plans bis auf T1** (`EmoteListItemDto.ImageUrl`); `dotnet test` bleibt
Gate für T1.

**Was aus T6 wandert:** der Spec-Querverweis F7 (6.6 → 6.7, Issue „Backend bookkeeping") als
`docs: point F7 at the set-centric endpoint in 6.7` nach **T9** — das Issue verlangt die Korrektur,
sie ist reine Doku und hängt an keinem Endpunkt. Das Backend bleibt für einen späteren
set-zentrierten `sync-deleted`/`sync-restored` offen — beides zusammen ist das Folge-Issue
„Restore pro Set", mit dem die Sperre fällt (Abschnitt 7).

### T7 — Das Transfer-Protokoll in zwei Stufen: Rückweg-Datei vor dem Lauf, Ergebnis danach, Import-Abweisung, Schutz des laufenden Laufs

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
`restore.import.errors.transferRun` — der Import-Grund; die Restore-Seite, die Einlesesorte
`restore.import.sorts.transferRun` und die Parser-Fehler liegen in **T7b**).

**Vertrag:**

- **Eine Envelope-Art, zwei Stufen.** `meta.stage: 'planned' | 'finished'`. Die Rückweg-Datei
  (`planned`) entsteht in T8 aus dem **live gelesenen** Ziel-Set: je Replace-Zeile `removedTarget`
  mit Ziel-Id und **allen Einträgen** aus dem Read, nicht aus der Vorschau (Codex-Finding 1) —
  `entries: { alias: string | null }[]`, ein Eintrag je benanntem Alias aus `aliasesById` **plus
  einer mit `alias: null`, wenn die Id in `aliaslessIds` steht** (Runde 2, Finding 2; der Leser
  weiß dann: dieser Eintrag stand unter dem Standardnamen des Emotes und kommt per ADD **ohne**
  Alias zurück). `aliases: string[]` bleibt daneben als die benannten, für den CSV-Leser. Alle
  Zeilen `status: 'pending'`, `failedStep: null`, `errorMessage: null`; `counts` mit `planned`,
  `removals`. Das Ergebnisprotokoll (`finished`) trägt dieselben Zeilen mit Ausgang: `status` (auch
  `unknown`), `failedStep: number | null`, `errorMessage` (7TVs Rohtext), und bei Replace zusätzlich
  `removedTarget.confirmed: boolean` = `completedSteps >= 1` (REMOVE bestätigt — direkt durch 7TVs
  Antwort oder per Nachlesen —, **unabhängig vom Endstatus der Zeile**, also auch `true` bei einer
  `unknown`-Zeile, deren ADD ohne Antwort blieb; Runde 2, Finding 1). Zeilen **ungefiltert** (F3:
  auch `failed`, `cancelled`, `unknown`).
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
  getrennt), `removed_aliasless_entry` (`true`/`false`), `removed_confirmed`. Die Rückweg-Datei ist
  **nur JSON** — ein Klick, eine Datei.
- **Import-Verbot, Restore erlaubt (Betreiber 2026-09-23):** `parseImportSource` antwortet auf
  `kind === 'transfer-run'` — **beide Stufen** — mit dem eigenen Key **vor** der
  `emote-list`/`usage`-Prüfung: die Datei ist keine Emote-Liste, und ihre Quell-Zeilen sind nichts,
  was man noch einmal kopiert. Geprüft, wo das Verbot greift: `file-import-step.ts:117-127`
  verzweigt **vor** `parseImportSource` auf `kind` — heute nur `purge-run` → Restore, alles andere
  → Import. T7b hängt dort den zweiten Restore-Zweig für `transfer-run` ein; `parseImportSource`
  erreicht die Datei dann nur noch, wenn jemand den Dispatch umgeht — und antwortet trotzdem mit
  Grund. `parsePurgeRunProtocol`s `FOREIGN_KIND_ERROR_KEYS` bekommt **keinen** Eintrag: dieser
  Parser wird nur noch für `purge-run` gerufen. Ein unbekannter `kind` bleibt `wrongKind` (schon
  heute „mit Grund" — AK 18 zweiter Satz, kein neuer Code). Der Restore-Parser für `transfer-run`
  ist T7b.
- **`removedTarget.defaultName`** in beiden Stufen: der Standardname des Ziel-Emotes aus dem
  Live-Read (`seven-tv-set-entries.ts` fragt dafür ein Feld mehr ab, `emote { id defaultName }`) —
  der Anzeigename eines aliaslosen Eintrags in der Restore-Vorschau, und der Name, unter dem 7TV ihn
  nach einem ADD ohne Alias zeigt. Ohne ihn hätte eine Restore-Zeile für einen rein aliaslosen
  Eintrag keinen Namen, den ein Mensch wiedererkennt.
- **Ergebnisprotokoll im Dock** nach jedem Lauf (AK 16): `ExportDialog` mit `FORMAT_EXPORT_OPTIONS`,
  `selectionCount: null` (wie das Purge-Protokoll), stiller Hinweis `protocolNotSaved`, kein Banner,
  keine Rückfrage beim Schließen (Abschnitt 2). `protocolSaved` wird beim Start eines neuen Laufs
  zurückgesetzt. Der Download-Knopf erscheint erst bei `settlement === 'settled'` und baut aus
  `run().result.items` — dem geklärten, lauf-gebundenen Ergebnis —, nie aus der Engine-Queue
  (Runde 2, Finding 3); `import-progress-section.ts` bindet `[items]` an `importService.items`
  statt an `importService.queue`.
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

**Tests:** `transfer-run-export.spec.ts` **+10**: Rückweg-Datei aus Plan + Live-Read (Replace mit
#74-Duplikat ⇒ zwei Aliase in `removedTarget`, `status: 'pending'`, `stage: 'planned'`);
**Rückweg-Datei für ein Ziel mit einem benannten und einem aliaslosen Eintrag ⇒ `entries` mit zwei
Einträgen, einer davon `alias: null`, `aliases` mit einem** (Runde 2, Finding 2); Ergebnisprotokoll
aus einem gemischten Lauf (jede Aktion einmal); `failed` mit `failedStep = 1` und
`removedTarget.confirmed: true`; **`unknown`-Zeile mit `completedSteps 1` ⇒ `confirmed: true`,
zählt in `counts.removed` **und** `counts.unknown`** (Runde 2, Finding 1); `unknown`-Zeile mit
`completedSteps 0` ⇒ `confirmed: false`, nicht in `removed`; CSV-Spalten inkl.
`removed_aliasless_entry`; beide Dateinamen; Envelope-Felder für getrackt/ungetrackt.
`import-source-parser.spec.ts` **+2** (beide Stufen abgewiesen), `purge-run-export.spec.ts` **+1**,
`file-import-step.spec.ts` **+1** (die Datei landet bei keinem `picked`, Banner nennt den
Transfer-Grund). `import-progress-section.spec.ts` (18) **+3**: Download-Knopf nach jedem Lauf;
stiller Hinweis bis `protocolSaved`; Schließen ruft `reset()` ohne Rückfrage.
`seven-tv-import.service.spec.ts` **+2** (`beforeunload` registriert genau während
`destructiveRunActive`, danach entfernt; Add-only-Lauf registriert nichts).

**Abnahme:** Kein Weg führt aus einer `transfer-run`-Datei (beide Stufen) in `startImportFlow`;
der Weg in `startRestoreFlow` ist T7b und in diesem Task noch **nicht** verdrahtet (die Datei wird
bis T7b als `wrongKind` abgewiesen — ein Zwischenstand, kein Vertrag); `beforeunload` feuert genau
während eines laufenden Replace-Laufs. **AK 16, 17 (Dateiform), 18 (Import-Hälfte).**

**Commit:** `feat(import): record a transfer run before and after it runs` (Envelope, beide
Builder, Abweisung, Dock) und `feat(import): warn before unloading a running destructive transfer`
(`beforeunload`) — zwei Commits, weil der zweite Fensterverhalten ändert. **Abhängigkeiten:** T5
(Planzeilen und `destructiveRunActive` am Laufdatensatz). **Modell:** `sonnet`.

### T7b — Restore aus Übertragungsdateien: beide Stufen einlesen, nur Lücken schließen, aliaslose Einträge (neu, Betreiber-Entscheidung 2026-09-23)

**Ziel:** Nach einem „Ziel ersetzen" führt der bestehende Weg „Wiederherstellen" zurück: die
Rückweg-Datei (`planned`) und das Ergebnisprotokoll (`finished`) werden über denselben Einstieg wie
ein Purge-Protokoll eingelesen und stellen **nur die entfernten Ziel-Einträge** wieder her — unter
ihren alten Aliasen, aliaslose per ADD ohne Alias, und nur dort, wo der Name nicht inzwischen einem
anderen Emote gehört. „Der Mensch stellt von Hand wieder her" ist abgelehnt.

**Befund am Restore-Code, der die Vorgabe eingrenzt:** Der Restore-Weg ist **kanalgebunden**, von
vorn bis hinten. `FileImportStep` friert `channelName` und `setId` der **Seite** ein
(`file-import-step.ts:73-86`), `parsePurgeRunProtocol` verlangt `envelope.channelName === Seite`
und `meta.emoteSetId === gewähltes Set` (`purge-run-export.ts:144-153`), `startRestoreFlow`
nimmt `channelName` als Pflichtparameter, und `SevenTvRestoreService` meldet an das kanalgebundene
`sync-restored` (`seven-tv-restore.service.ts:236-240`); einen set-zentrierten `sync-restored` gibt
es nicht (`SevenTvEndpoints.cs` kennt nur `sync-imported`; der set-zentrierte `sync-deleted` aus
T6 ist in Fassung 5 entfallen). Ein Replace in
ein **ungetracktes** Set hat keine Kanalseite, auf der man seine Datei einlesen könnte, und keinen
Endpunkt, der die Wiederherstellung buchte. Das ist keine Lücke, die dieser Task aufreißt — für ein
ungetracktes Set gibt es heute überhaupt keinen Restore, weil es auch keinen Delete gibt —, aber
sie begrenzt die Vorgabe: **der Restore aus Übertragungsdateien gilt für getrackte Ziele** (der
Nutzer öffnet die Nutzungsseite des Zielkanals, wählt im K4-Dropdown das Zielset, aktiv oder nicht,
und liest die Datei ein). Der ungetrackte Fall wird als **Folge-Issue** vermerkt (Abschnitt 7),
zusammen mit dem set-zentrierten `sync-restored`, den er bräuchte.

**Dateien:** `web/src/app/shared/export/transfer-run-export.ts` (Restore-Parser
`parseTransferRunForRestore(text, expected)` neben den Buildern), `web/src/app/shared/seven-tv/file-import-step.ts:117-127`
(zweiter Restore-Zweig auf `kind === 'transfer-run'`; vierte Einlesesorte in der Liste),
`web/src/app/shared/seven-tv/restore-flow.ts` (Eingabetyp), `web/src/app/shared/export/purge-run-export.ts`
(nur Typ-Export; **keine** Änderung an `readProtocolRow` oder am `purge-run`-Lesen),
`web/src/app/shared/seven-tv/already-present-filter.ts:87-150` (`filterAlreadyPresentForRestore`:
Regel 4 „Name belegt", aliaslose Zeilen), `web/src/app/shared/seven-tv/seven-tv-set-entries.ts`
(`defaultName` im Read — geteilt mit T7), `web/src/app/core/seven-tv/seven-tv-restore.service.ts:300-320`
(`toRestoreQueue`: ADD ohne Alias), `web/src/app/shared/seven-tv/restore-confirm-dialog.ts`
(Namen aliasloser Einträge), `web/src/app/shared/seven-tv/mass-delete-panel.ts` und
`import-progress-section.ts` (Dock-Zeile „N Aliase übersprungen — Name inzwischen belegt"),
`web/public/i18n/{de,en}.json` (`restore.import.sorts.transferRun`, `restore.import.errors.transferRunNoRows`,
`restore.skippedNameTaken`), `docs/UI-Designsprache.md` §7.3 (vierte Einlesesorte — der Abschnitt
ist Vertrag), `docs/DECISIONS.md` (Eintrag im selben Commit, Regel 3: der Restore liest
`transfer-run`, und Regel 4 „Name belegt" gilt für **alle** Restore-Quellen — auch eine bestehende
`purge-run`-Datei sortiert eine belegte Zeile jetzt vor dem Lauf aus, statt einen 409 zu kassieren).

**Vertrag:**

- **Der Eingabetyp des Restore-Flows wird `RestoreRow`** — `{ emoteId: string | null, sevenTvEmoteId,
  name, aliases: (string | null)[] }` — statt `PurgeRunRow`. `null` ist ein aliasloser Eintrag.
  `parsePurgeRunProtocol` liefert weiter `PurgeRunRow[]` (alle Aliase Strings, nichts am Lesen
  bestehender Dateien ändert sich, `readProtocolRow` bleibt wie es ist) und wird am Aufrufer auf
  `RestoreRow` abgebildet — eine Zuweisung, kein Umbau. **Die `purge-run`-Datei schreibt und liest
  weiter nur nicht-leere Alias-Strings**; der aliaslose Eintrag existiert nur im `transfer-run`-Shape
  (`removedTarget.entries`) und in der In-Memory-Zeile.
- **`parseTransferRunForRestore(text, { channelName, emoteSetId })`**: dieselbe Leiter wie
  `parsePurgeRunProtocol` — Envelope, `kind === 'transfer-run'`, `formatVersion ===
  TRANSFER_RUN_FORMAT_VERSION`, `meta.targetChannelName === channelName` (nicht der Envelope-
  `channelName`, der bei ungetracktem Ziel `''` ist — beides zusammen ergibt hier ohnehin
  `wrongChannel`, s. Befund), `meta.targetEmoteSetId === emoteSetId`, sonst `wrongVersion` /
  `wrongChannel` / `wrongSet` mit den **bestehenden** Keys. Zeilen: nur `action === 'replace'`;
  Stufe `planned` ⇒ **jeder** `removedTarget`; Stufe `finished` ⇒ nur `removedTarget.confirmed ===
  true`. Je Ziel-Eintrag eine `RestoreRow`: `sevenTvEmoteId` = Ziel-Id, `aliases` = die `entries`
  (Strings und `null`), `name` = erster benannter Alias, sonst `defaultName`, `emoteId: null`.
  Zwei Replace-Zeilen auf dieselbe Ziel-Id gibt es nicht (T3, Regel 4). Keine Zeile ⇒
  `transferRunNoRows` („Diese Übertragungsdatei enthält keine entfernten Emotes").
- **`filterAlreadyPresentForRestore` bekommt eine vierte Regel und lernt `null`:**
  4. **Ein Alias, den im Live-Set eine andere Id hält, wird aus der Zeile gestrichen** — gezählt in
     einem eigenen `skippedNameTaken`, nicht in `skipped` (das sind „schon vorhanden"). Eine Zeile,
     deren Aliase alle belegt sind, fällt ganz weg. Das ist der Fall „Replace geglückt, Quell-Emote
     hält den Namen": heute prüft der Filter nur die **eigene** Id (`aliasesById.get(row.id)`),
     nie, wer einen Alias sonst hält — die Zeile ginge durch und holte sich einen sicheren 409
     (Ticket verbrannt, rote Zeile). Der Vergleich braucht die Namensinhaber: `loadSevenTvSetEntries`
     liefert `aliasesById`; die Umkehrung (Alias → Id) baut der Filter selbst aus derselben Antwort,
     kein zweiter Read. Die Regel gilt **für jede Restore-Quelle**, auch Purge-Protokolle — dort war
     der 409 bisher genauso sicher, nur seltener (jemand hat den Namen seit dem Purge neu vergeben);
     eine Regel, die nur für eine Quelle gilt, wäre die zweite Wahrheit im selben Filter (Abschnitt 7).
  - **`null` in `aliases`:** ein aliasloser Eintrag der Zeile gilt als **vorhanden**, wenn
     `aliaslessIds.has(id)`, sonst als fehlend. Die K5-Regel 2 („die Id sitzt unter einem Alias, den
     die Zeile nicht nennt ⇒ ganze Zeile weg") wird so gelesen, dass **ein aliasloser Live-Eintrag
     von einer Zeile, die `null` nennt, als benannt gilt** — `aliaslessIds.has(id)` macht die Zeile
     nur dann zur „fremden", wenn sie **kein** `null` trägt. Ohne diese Lesart filterte die K5-Regel
     jede Zeile mit aliaslosem Eintrag still weg (Vorgabe des Betreibers: genau das darf nicht
     passieren). Die Zählung von `skipped` bleibt je Alias, `null` zählt als einer.
- **`toRestoreQueue`:** ein Alias `null` ⇒ Queue-Key `${sevenTvEmoteId}#` (leerer Suffix — bleibt
  eindeutig, weil 7TV je Id höchstens einen aliaslosen Eintrag zulässt und Regel 3 aus T3 zwei
  gleiche Keys schon in der Datei ausschließt), ADD mit `alias: null` in den Variablen — die
  Mutation `ADD_EMOTE_MUTATION` erlaubt das heute (`$alias: String`, nullbar), es ist der
  dokumentierte Weg zum Standardnamen. Anzeige der Queue-Zeile: `defaultName`.
- **`startRestoreFlow`** unverändert in Signatur und Reihenfolge (Token vor Bestätigung — nicht
  „angleichen"); `RestoreConfirmDialogData.names` zeigt aliaslose Einträge unter `defaultName`;
  `addCount` zählt `null` mit.
- **Dock nach dem Lauf:** `skippedNameTaken > 0` ⇒ eigene Zeile mit Grund (`restore.skippedNameTaken`,
  transiente Mechanik wie `skippedDuplicates`), damit „übersprungen" nicht als „war schon da"
  gelesen wird.
- **Nicht Teil dieses Tasks:** ein Rückgängigmachen des Replace (Quell-Emote entfernen, Ziel
  wiederherstellen) — Folge-Issue; der Restore in ein ungetracktes Set — Folge-Issue (s. Befund);
  ein Laden der Datei als **Auswahl** (#201).

**Grenzfälle:** Rückweg-Datei eines Laufs, der nie gestartet wurde ⇒ jede Zeile fällt über Regel 3
(alles vorhanden) heraus, Dock zeigt „N übersprungen (schon vorhanden)", kein Lauf ·
Ergebnisprotokoll mit `unknown`-Zeile, `confirmed: true` ⇒ Restore-Zeile; das Live-Set entscheidet,
ob sie nötig ist · Ziel mit einem benannten und einem aliaslosen Eintrag, Replace geglückt ⇒ der
benannte Alias ist belegt (Regel 4), der aliaslose fehlt und wird wiederhergestellt — die Zeile
läuft mit **einem** ADD ohne Alias · dieselbe Datei zweimal eingelesen ⇒ zweiter Lauf leer (alles
vorhanden) · Datei auf der Seite eines anderen Kanals ⇒ `wrongChannel`; anderes Set gewählt ⇒
`wrongSet` · `finished`-Datei ohne einen bestätigten REMOVE ⇒ `transferRunNoRows` · Purge-Protokoll,
dessen Alias inzwischen ein anderes Emote hält ⇒ Regel 4 greift auch dort (neu, gewollt).

**Tests (Regel 12):** `transfer-run-export.spec.ts` **+7** (Parser: `planned` liefert alle
Ziel-Einträge; `finished` nur `confirmed`; Zeile mit `null`-Alias und `defaultName`; `wrongChannel`
gegen `meta.targetChannelName`; `wrongSet`; `wrongVersion`; keine Zeile ⇒ `transferRunNoRows`).
`already-present-filter.spec.ts` (bestehend) **+5**: Alias von anderer Id gehalten ⇒ gestrichen,
`skippedNameTaken 1`; alle Aliase belegt ⇒ Zeile weg; `null`-Alias vorhanden (`aliaslessIds`) ⇒
übersprungen, fehlend ⇒ bleibt; **gemischte Zeile (benannt belegt, aliaslos fehlend) ⇒ ein ADD**;
Zeile ohne `null` bei aliaslosem Live-Eintrag ⇒ weiterhin „fremd" (K5 bleibt für Purge-Zeilen).
`seven-tv-restore.service.spec.ts` **+2** (`null`-Alias ⇒ ADD mit `alias: null`, Key `${id}#`;
`skippedNameTaken` durchgereicht). `file-import-step.spec.ts` **+3** (`transfer-run` beider Stufen
⇒ `picked` mit `kind: 'restore'`; falscher Kanal ⇒ Banner, kein `picked`). `restore-flow.spec.ts`
**+1** (aliasloser Eintrag zählt in `addCount`, Name ist `defaultName`). `purge-run-export.spec.ts`
**0 neue** — die bestehenden Fälle beweisen, dass das Lesen unverändert ist (Regressionsschutz).
E2E in T9 (Fall 6).

**Abnahme:** Replace-Lauf mit gescheitertem ADD → Ergebnisprotokoll einlesen → Lücke geschlossen;
geglückter Replace in derselben Datei → Zeile „Name belegt", kein Request; Purge-Protokolle lesen
und restaurieren wie vor diesem Task, plus Regel 4. **Plan-eigene Kriterien R1–R4 (Abschnitt 4).**

**Commit:** `feat(restore): restore removed target entries from a transfer record` und
`feat(restore): skip aliases another emote now holds instead of burning a 409` — zwei Commits, der
zweite ändert das Verhalten bestehender Purge-Restores. **Abhängigkeiten:** T7 (Dateiform,
`defaultName`). **Modell:** `opus` — derselbe Grund wie T5.1 in Plan-200: der Restore ist der
einzige Rückweg einer Löschung, und ein Fehler im Filter (stilles Wegfiltern) sähe niemand.

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
  deaktiviert und beschriftet** (AK 8, Idiom aus 8.6 für nicht wählbare Sets). **Dasselbe Idiom für
  Replace bei ungetracktem Ziel** (`data.targetChannelName === null`, Fassung 5): die Option steht
  in jeder Kollisionszeile, ist deaktiviert und trägt den Grund `import.resolve.replaceNeedsTracked`
  („Nur für getrackte Kanäle wiederherstellbar") — sichtbar, nicht ausgeblendet, damit der Nutzer
  weiß, dass es die Aktion gibt und warum sie hier fehlt. Die Oberfläche ist dabei nur die zweite
  Sicherung: die Regel selbst steht in T3 (Regel 7), und die Zusammenfassung zeigt für ein
  ungetracktes Ziel nie eine Entfernungszeile, weil kein Plan mit Replace entstehen kann.
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
  `verifyReplaceTargets` (T5) — auf Eintragsebene, `aliaslessIds` eingeschlossen (Runde 2,
  Finding 2). **Abweichung** ⇒ zurück nach `idle`, Banner `targetDrifted` nennt die
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
(AK 3); **ungetracktes Ziel ⇒ Replace-Option je Zeile deaktiviert mit Grund, Rename und Adopt
wählbar, keine Entfernungszeile möglich** (R5); Entfernungszeile nur bei Replace (AK 20); Projektion mit gemischten Entscheidungen (AK 21,
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

**E2E (+6, Zählung am Ende):** (1) Kollision + Alias-Abweichung im Mock; eine Zeile je Aktion (Rename, Replace,
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
`sync-imported`; Variante: der Read zeigt sie nicht ⇒ Zeile rot mit Lückengrund. (6) **Restore aus
dem Ergebnisprotokoll (T7b):** Lauf mit zwei Replaces — einer glückt, beim anderen scheitert der
ADD mit 409 —, Ergebnisprotokoll herunterladen (`waitForEvent('download')`), im Import-Dialog
„Aus einer Datei" einlesen ⇒ Restore-Bestätigung nennt beide Ziel-Einträge; der gemockte Live-Read
zeigt den Namen des geglückten Replace bei der Quell-Id ⇒ genau **ein** `addEmote` mit der Ziel-Id
und dem alten Alias der Lücke, das Dock zeigt „1 Alias übersprungen — Name inzwischen belegt", kein
`removeEmote`.

(7) **Ungetracktes Ziel (R5):** Pick eines ungetrackten Sets im Picker (Mock
`/me/emote-set-targets` wie in `usage-stats-import-target-dialog`), Kollision im Mock ⇒ Schritt 2
zeigt die Replace-Option deaktiviert mit dem Grund, Rename ist wählbar, Kopieren ohne
Entfernungszeile und ohne „Rückweg sichern" ⇒ nur `addEmote`, kein `removeEmote`, kein Download.

**E2E-Zählung:** damit **+7**.

**Gates:** alle drei Suiten; `node scripts/coverage-local.mjs` (Näherung — Sonar zählt Zweige mit,
lokal pessimistisch; bei < 80 % nachsehen, ob es die neue `import-conflict-resolution-step.ts`
ist); `/codex:review --model gpt-6-sol --scope branch --base feat/emote-sets-200` über den Branch,
Ergebnis unverändert dem Betreiber vorlegen (Regel 22); Widersprüche zwischen Opus-Review und Codex
gehen an Fable (global). **AK 24.**

**Commit:** `test(e2e): resolve one conflict per action and assert the mutation order` und
`docs: point the concept's resolution-table outlook at its own issue`. **Abhängigkeiten:** T1–T8.
**Modell:** `sonnet`.

### T10 — Live-Verifikation an zwei Sets: getrackter Testkanal und olafs ungetracktes Set (Betreiber-Handgriff, Regel 16)

**Zwei Ziele, zwei Rollen (Fassung 5).** Replace und Restore gibt es nur für getrackte Ziele; die
destruktiven Belegpunkte laufen deshalb **gegen ein Set eines getrackten Testkanals** (Zweitkonto
mit Wegwerf-Set, wie in Plan-200 T2.7 — ein Set, das im K4-Dropdown dieses Kanals wählbar ist,
aktiv oder nicht). **Olafs Set `test`** (`01M320AYTGYMPJZD3RGPJGHH1S`, Konto `olaf_olaf_son`,
aktives Set `tttt`; sensitron ist dort 7TV-Editor, das Ziel ist aus sensitrons Sicht **ungetrackt**)
belegt die **Sperre** und die drei nicht-destruktiven Aktionen. Haupt-Checkout, Api per `dotnet
run`, `npm start`, danach `dotnet run` beenden, bevor E2E läuft (`:5151`-Falle).

**Vorbereitung (Betreiber, einmalig), in beiden Sets gleich:** einen Eintrag anlegen, dessen Alias
mit einem Emote der Quelle kollidiert, aber eine andere Id trägt; einen zweiten, dessen Id in der
Quelle steht, dort aber anders heißt; einen dritten als #74-Duplikat (dieselbe Id zweimal, zwei
Aliase), dessen einer Alias mit einer Quellzeile kollidiert; im getrackten Set zusätzlich ein Ziel
mit einem aliaslosen Eintrag (Punkt 10). Quelle: eine Auswahl aus sensitrons Set oder eine
Emote-Liste-Datei (dann `imageUrl: null` links — auch das ist ein Prüfpunkt).

**Zu belegen, im PR-Text mit Zahlen — Punkte 1–10 am getrackten Set, Punkt 11 an olafs Set:**

1. Schritt 2 zeigt drei Zeilen mit Bildern; die Datei-Quelle zeigt links die Platte ohne Request
   (Netzwerktab: kein `cdn.7tv.app`-Aufruf mit der Quell-Id).
2. **Pflicht-Download:** „Rückweg sichern" liest das Set (ein tokenloser Request im Netzwerktab),
   die Datei landet auf der Platte **bevor** irgendein REMOVE gesendet ist, `stage: 'planned'`,
   `removedTarget` des Duplikats mit **beiden** Aliassen; erst dann steht „Starten".
3. Rename, Replace (auf das Duplikat) und Adopt in einem Lauf: Reihenfolge der Requests im
   Netzwerktab REMOVE → ADD benachbart; das Set danach per tokenlosem Read-back (Probe D aus dem
   T0-Kommentar) exakt wie erwartet — Duplikat mit **beiden** Aliassen weg, Quell-Emote unter
   Quellname drin, Adopt-Eintrag unter Quellname, Eintragszahl um genau eins gesunken.
4. Audit-Log des Testkanals: ein `syncImported`- und ein `syncDeleted`-Eintrag, beide mit der
   Set-Id und `targetIsActiveSetOfChannel` passend zum gewählten Set, Zählwerte 2 und 1; ist das
   Set nicht das aktive, ist der `syncDeleted` der Papierfall (`archivedCount 0`, Spec 6.6).
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
10. **Restore aus der Übertragungsdatei (T7b):** zweiter Replace-Lauf, dessen zweiter ADD
    absichtlich scheitert (Zielname vorher im 7TV-Web belegen ⇒ 409), Ergebnisprotokoll
    herunterladen, auf der Nutzungsseite des Kanals mit dem Zielset im Dropdown einlesen ⇒
    Bestätigung nennt beide Ziel-Einträge, Lauf sendet genau einen ADD (die Lücke), die Zeile des
    geglückten Replace steht im Dock als „Name belegt"; Read-back zeigt die Lücke geschlossen und
    das Quell-Emote unangetastet. Dazu einmal die **Rückweg-Datei** desselben Laufs einlesen ⇒ alles
    „schon vorhanden" bzw. „Name belegt", kein Request. Und einmal das Ziel mit aliaslosem Eintrag
    ⇒ ein ADD ohne Alias, das Emote erscheint unter seinem Standardnamen.
11. **Die Sperre an olafs Set `test` (R5):** Picker ⇒ ungetracktes Set bestätigen ⇒ Dialog ⇒
    Schritt 2 zeigt in jeder Kollisionszeile „Ziel ersetzen" ausgegraut mit dem Grund „Nur für
    getrackte Kanäle wiederherstellbar"; Rename und Adopt lassen sich wählen; Schritt 1 zeigt keine
    Entfernungszeile und „Kopieren" statt „Rückweg sichern"; der Lauf sendet nur `addEmote` und
    `updateEmoteAlias`, kein `removeEmote`; Audit (globale Admin-Ansicht) zeigt genau einen
    `syncImported` mit `ChannelName = null`, „von olaf_olaf_son", und **keinen** `syncDeleted`.
    Read-back: Eintragszahl um die Rename-Zeilen gewachsen, der Adopt-Eintrag unter dem Quellnamen,
    nichts entfernt.

Der Betreiber führt die Handgriffe aus; ein Subagent (`sonnet`) bereitet die Read-back-Abfrage und
die Erwartungswerte vor und schreibt die Befunde in den PR-Text. **Kein Commit**, außer ein Fund
verlangt einen `fix(import): …`. **AK 7 (live), 19 (live), 24; R1–R3, R5 (live).**

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
| 15 | ADD scheitert nach REMOVE ⇒ eigener Grund, Lauf geht weiter — **eine verlorene oder 5xx-Antwort ist kein Scheitern** (`unknown`, Nachlesen auf lauf-gebundenem Ergebnis) | T4, T5, T9 (E2E 2, 5) |
| 16 | Protokoll nach jedem Lauf (Ergebnisprotokoll, aus dem geklärten Ergebnis); **zusätzlich Rückweg-Datei vor jedem Lauf mit Replace** | T7, T8 |
| 17 | Replace-Zeile trägt Ziel-Id und **alle Einträge** — benannte Aliase **und** einen aliaslosen — **live gelesen** vor dem Download, nicht aus der Vorschau; Ergebnis mit `confirmed`, das am bestätigten REMOVE hängt, nicht am Zeilenstatus | T2, T5, T7, T8, T9 (E2E 1, 4), T10 (Punkte 2, 7) |
| 18 | `transfer-run` wird als **Import-Quelle** namentlich abgewiesen (beide Stufen); unbekannter `kind` mit Grund. **Die Restore-Hälfte des AK ist durch den Betreiber am 2026-09-23 revidiert:** beide Stufen sind über „Wiederherstellen" ladbar (T7b) | T7 (Import), T7b (Restore) |
| R1 *(Plan)* | Rückweg-Datei und Ergebnisprotokoll sind über den bestehenden Restore-Einstieg ladbar; `planned` ⇒ alle geplanten Ziel-Einträge, `finished` ⇒ nur bestätigte REMOVEs; nur Ziel-Einträge | T7b, T9 (E2E 6), T10 (Punkt 10) |
| R2 *(Plan)* | Nur Lücken schließen: ein Alias, den eine andere Id hält, wird vor dem Lauf mit Grund aussortiert, nichts wird entfernt | T7b, T9 (E2E 6), T10 |
| R3 *(Plan)* | Ein aliasloser Ziel-Eintrag wird per ADD ohne Alias wiederhergestellt und von keinem Filter still verworfen | T7, T7b, T10 |
| R4 *(Plan)* | Bestehende `purge-run`-Dateien werden byte-identisch gelesen wie vor #230 | T7b |
| 19 | Löschmeldung, Audit zeigt Hinzugefügtes und Entferntes — **jede bestätigte** REMOVE (`completedSteps >= 1`), auch bei einer `unknown`-Zeile. **Die ungetrackte Hälfte des AK (neuer set-zentrierter `sync-deleted`) ist durch den Betreiber am 2026-09-23 gegenstandslos:** Replace ist für ungetrackte Ziele gesperrt, die Löschmeldung läuft immer kanalgebunden | T4, T5, T10 |
| R5 *(Plan)* | Keine Löschung ohne Restore-Weg: „Ziel ersetzen" ist für ein ungetracktes Ziel in Validierung und Oberfläche gesperrt, mit Grund; Skip, Rename, Adopt bleiben | T3, T8, T9 (E2E 7), T10 (Punkt 11) |
| 20 | Entfernungszeile genau bei Replace | T3, T8 |
| 21 | Projektion: `addCount − removedEntryCount` — Rename +1, Replace 0, Replace auf Duplikat −1 | T3, T8 |
| 22 | 200 Zeilen, 360 px, kein horizontaler Scroll, kein Bildsturm | T8 |
| 23 | Tastatur, zugänglicher Name je Zeile | T8 |
| 24 | Tests grün, keine Regression in Import/Delete/Restore | alle, T9 |

---

## 5. Reihenfolge und Abhängigkeiten

```
T0 (erledigt)
T1 ─┐            T4 ─┐        (T6 entfallen)
    ▼                │
    T2               │
    ▼                ▼
    T3 ─────────────► T5
    │                 │
    │                 ▼
    │                 T7 ──► T7b
    ▼                 ▼        │
    T8 ◄──────────────┘        │
    ▼                          │
    T9 ◄───────────────────────┘
    ▼
    T10
```

- **Welle 1 (parallel, zwei Worktrees oder sequenziell in einem):** T1, T4.
- **Welle 2:** T2 (nach T1), dann T3.
- **Welle 3:** T5 (nach T3, T4).
- **Welle 4:** T7 (nach T5).
- **Welle 5:** T8 (nach T3, T5, T7 — der Pflicht-Download im Dialog braucht T7s Builder; in der
  ersten Fassung liefen T7 und T8 parallel).
- **Welle 5, parallel dazu:** T7b (nach T7; unabhängig von T8 — der Restore-Weg fasst den
  Import-Dialog nur an der Einlesesorte an).
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
   einen Kanal voraus); der Plan baute den Endpunkt wie im Issue vorgeschlagen (T6). —
   **Entschieden: so bauen; #224 bleibt getrennt.** — **Überholt in Fassung 5 (2026-09-23):** mit
   der Sperre „kein Replace für ungetracktes Ziel" entsteht dort keine Löschung mehr, T6 ist
   **entfallen**; der Endpunkt wandert ins Folge-Issue „Restore pro Set". #224 bleibt getrennt.
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
   Envelope-`channelName` des `transfer-run` ist `''` bei ungetracktem Ziel** (T1, T7). —
   **Eingeengt in Fassung 5:** der Fall `''` betrifft nur noch das **Ergebnisprotokoll** eines
   Add/Rename/Adopt-Laufs in ein ungetracktes Set; eine **Rückweg-Datei** (Stufe `planned`) hat
   immer einen Kanal, weil sie nur bei Replace entsteht und Replace nur getrackt ist. Die
   Entscheidung bleibt, ihr Gewicht ist kleiner: eine solche Datei hat keine Restore-Zeilen.
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
| **Ausgang `unknown`** | „if the ADD fails, the row ends `failed`" | ein Schritt ohne GraphQL-Antwort und ohne HTTP-Ablehnung vor der Verarbeitung (Status 0, jede 5xx) endet `unknown`; ein Nachlesen auf einem lauf-gebundenen Ergebnis klärt ihn; unklärbar heißt: Quell-Id in keiner Import-Meldung — der bestätigte REMOVE derselben Zeile aber in der Löschmeldung | Codex Runde 1 Finding 3, Runde 2 Findings 1, 3, 4: eine verlorene Antwort ist kein Beweis, dass nichts passiert ist; eine 500 sagt nicht, ob vor oder nach dem Schreiben; eine Engine-Queue gehört dem nächsten Lauf; und die Bestätigung eines REMOVE ist eine Tatsache aus dem Lauf, die kein späterer Zustand der Zeile zurücknehmen kann |
| **Aliaslose Zieleinträge** | nicht behandelt (Alias-Liste) | `removedTarget.entries` mit `alias: string \| null` und `defaultName`; Drift-Vergleich auf Eintragsebene an beiden Prüfstellen; Replace **erlaubt**; **der Restore-Weg stellt den Eintrag per ADD ohne Alias wieder her** | Codex Runde 2, Finding 2, und Betreiber 2026-09-23 („von Hand" abgelehnt). Gewählt: **abbilden statt sperren, und wiederherstellbar machen**. Am Code geprüft: der Purge-Restore kann einen aliaslosen Eintrag nicht ausdrücken (`PurgeRunRow.aliases` verlangt nicht-leere Strings, `readProtocolRow` fällt auf `[name]` zurück) — das bleibt so für `purge-run`-Dateien; der Restore-Flow bekommt eine In-Memory-Zeile mit `null`-Alias, die `toRestoreQueue` als ADD ohne Alias sendet (die Mutation erlaubt es heute, `$alias: String`). Sperren hätte eine ganze Zielklasse aus Replace genommen, weil eine Datei ein Feld nicht hatte; die K5-Regel „aliaslos ist fremd" schützte vor einem **Re-ADD** auf einen unbekannten Eintrag — eine Zeile, die den Eintrag selbst als `null` nennt, kennt ihn, und für sie gilt die Regel nicht |
| **`transfer-run` ladbar für den Restore** | AK 18: „never parsed as an import source or a restore, in whole or in part"; Out of Scope: „Loading a `transfer-run` protocol back in … Also #201's subject" | beide Stufen über den bestehenden Restore-Einstieg ladbar; nur Ziel-Einträge; Import-Abweisung bleibt | **Betreiber-Entscheidung 2026-09-23**, gegen das Issue: ein Rückweg, den nur ein Mensch von Hand gehen kann, ist keiner. Das Issue hat die Frage „welche Zeile wird geladen — `removedTarget` oder die hinzugefügte" als Grund für den Ausschluss genannt; der Plan beantwortet sie eng: **nur** `removedTarget`, nie Quell-ADDs, Renames oder Adopts. #201 (Protokoll als **Auswahl** laden) bleibt unberührt |
| **Regel 4 im Restore-Filter gilt für alle Quellen** | — | „Alias von anderer Id gehalten ⇒ aussortiert mit Grund" auch für Purge-Protokolle | Ein Filter mit zwei Wahrheiten je Quelle ist der Fehler, den `import-source.ts`' erschöpfende Helfer für `ImportOrigin` vermeiden; der 409 war beim Purge-Restore genauso sicher, nur seltener. Verhaltensänderung für Bestandsrestores: ein roter 409 wird ein grauer „übersprungen — Name belegt" ohne verbranntes Ticket |
| **Untracked-Restore, Replace-Undo** | Rückweg für jedes Ziel implizit | Restore aus Übertragungsdateien nur für **getrackte** Ziele — und seit Fassung 5 gibt es Replace auch nur dort; das **Folge-Issue „Restore pro Set"** bündelt set-zentrierten `sync-deleted` (Ex-T6), set-zentrierten `sync-restored`, einen Restore-Einstieg ohne Kanalseite und das Fallen der Sperre; vollständiges Rückgängigmachen eines Replace ist ein zweites Folge-Issue | Befund T7b: der Restore-Weg ist kanalgebunden (Seite, Parser, `sync-restored`), ein set-zentrierter `sync-restored` existiert nicht; ein ungetracktes Set hat heute für nichts einen Restore. Das Undo bräuchte ein REMOVE der Quelle plus ADD des Ziels — eine neue destruktive Aktion, die Entscheidung 4 und 6 neu stellen würde |
| **Zeichensatzprüfung nur für getippte Aliase** | „Every produced alias must pass `isNameRejectedBySevenTv`" | nur der Alias einer Rename-Zeile; `toAdd`-, Replace- und Adopt-Aliase (Quellnamen) laufen wie heute | Codex Runde 2, Finding 5: `buildImportPreview` lässt `invalidNames` bewusst in `toAdd` („7TV entscheidet"); eine Prüfung aller erzeugten Aliase blockierte einen unveränderten Dialog gegen AK 2/5 |
| **Fremde Freigabe im selben Lauf** | „a replace frees its target's name for use in the same run, and that has to be allowed" | frei nur für die **eigene** Replace-Zeile; ein Rename auf einen Namen, den ein **anderes** Replace freigibt, ist eine Verletzung | Codex-Finding 4: die Engine läuft in Quellreihenfolge, der Rename kann vor dem Replace kommen und 409 bekommen; die Alternative — Abhängigkeiten im Lauf ordnen — kostet eine Topologie samt Zyklusfall für etwas, das in zwei Läufen sauber geht. Der eigene Fall (REMOVE gibt frei, der ADD derselben Zeile nimmt) bleibt erlaubt und ist, was das Issue mit „the whole point of the action" meint |
| **Bestandsdoppel in `toAdd`** | „No two produced aliases may be equal" | Doppel zwischen zwei **unveränderten** `toAdd`-Zeilen sind keine Verletzung | Codex-Finding 6, am Code belegt: `dedupeImportRows` faltet nur nach Id, der Fall existiert heute, 7TV lehnt die zweite Zeile ab; die Regel wörtlich genommen blockierte einen unveränderten Dialog gegen AK 2/5 |
| **Slot-Formel** | „renames as +1 and a replace as +1 minus the number of target entries" | `delta = addCount − removedEntryCount`, `addCount` = alle ADD-Mutationen | Codex-Finding 5: keine Abweichung vom Issue, sondern die Korrektur einer Doppelzählung in der ersten Fassung dieses Plans |
| `sourceName` in der Protokollzeile | nur `alias` | beides | Ein Rename ist sonst nicht rekonstruierbar |
| `removedTarget.confirmed` | nicht spezifiziert | im Ergebnisprotokoll je Replace | Mit `unknown` gibt es Zeilen, deren REMOVE weder bestätigt noch widerlegt ist; ein Leser muss das ohne Statusvergleich sehen |
| `transfer-run`-CSV | nicht spezifiziert | Spaltenliste in T7 | Der Export-Dialog bietet immer CSV neben JSON (§7.4); ohne Spaltenvertrag entstünde er ad hoc |
| Spec-Querverweis F7 | „correct … while this issue is being built" | eigener `docs:`-Commit in T9 (bis Fassung 4 in T6) | Regel 2: logisch getrennter Commit; reine Doku ohne Endpunkt |
| **T6 entfallen** | „This issue adds `POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted`" | kein neuer Endpunkt, keine Client-Methode | Betreiber-Entscheidung Fassung 5: der Endpunkt hatte als einzigen Grund die Löschmeldung eines Replace in ein ungetracktes Set; Replace ist dort gesperrt, Adopt meldet nichts, Add/Rename melden über den bestehenden set-zentrierten `sync-imported`. Ein Endpunkt ohne Aufrufer ist ein Endpunkt ohne Test seiner Wahrheit. Er kehrt mit dem Folge-Issue „Restore pro Set" zurück — dann zusammen mit `sync-restored` pro Set, als Paar |
| **Replace für ungetracktes Ziel gesperrt** | Aktionstabelle ohne Unterscheidung | Regel 7 in T3, ausgegraut mit Grund in T8 | Betreiber-Entscheidung Fassung 5: **keine Löschung ohne Restore-Weg** — der Restore ist kanalgebunden (Befund T7b), ein ungetracktes Set hat keinen; eine Aktion, deren Rückweg „im 7TV-Web von Hand" heißt, ist genau die, die der Betreiber abgelehnt hat |
| Konzept-Ausblick auf #201 | „point the outlook at this issue" | T9 | reine Doku, ans Ende |
| i18n als eigener Task | eigene Aufwandszeile | in jedem Task, der Text erzeugt | Hausregel: Schlüssel landen mit dem Feature in beiden Locales, nicht als Sammelschritt |
| Effort-Schätzung | 41 h | nicht neu geschätzt | Fassung 2 ändert den Umfang um den Pflicht-Download mit Live-Verifikation (grob +4 h), den Ausgang `unknown` samt Nachlesen (+4 h) und die zwei Validierungskorrekturen (+1 h); Abschnitt 2 der ersten Fassung (+3 h) entfällt größtenteils |

---

## 8. Rückweg

Frontend-Änderungen sind per Revert des PR rückgängig; die eine additive DTO-Eigenschaft
(`EmoteListItemDto.ImageUrl`) ist unabhängig davon harmlos, wenn sie bleibt (kein Schema). Einen
neuen Endpunkt gibt es seit Fassung 5 nicht mehr (T6 entfallen). Keine Migration. Bereits heruntergeladene `transfer-run`-Dateien —
Rückweg-Datei wie Ergebnisprotokoll — bleiben in einem revertierten Build **mit Grund** unlesbar
(`wrongKind`) — das ist die vorgesehene Antwort, kein Bruch; als JSON bleiben sie für einen
Menschen lesbar, und nur darauf kommt es beim Rückweg an. Ein bereits gelaufener Replace ist
**nicht** durch Revert rückgängig; die Rückweg-Datei, die vor seinem ersten REMOVE auf der Platte
lag, ist der einzige Weg zurück — deshalb der Pflicht-Download, deshalb die Live-Verifikation davor.
Eine `unknown`-Zeile im Ergebnisprotokoll ist nach einem Revert genauso unbekannt wie davor: der
Nutzer prüft das Set bei 7TV, wie das Dock es ihm gesagt hat — ihr bestätigter REMOVE steht
unabhängig davon im Audit-Log und in der Rückweg-Datei. **Nach einem Revert ist der Restore aus
einer Übertragungsdatei nicht mehr möglich** (der Build kennt die Einlesesorte nicht mehr und weist
sie mit `wrongKind` ab); die Datei bleibt als JSON lesbar, und die Ziel-Einträge lassen sich dann
nur noch im 7TV-Web zurücksetzen — genau der Handgriff, den der Betreiber als Regelweg abgelehnt
hat. Wer revertiert, revertiert deshalb **nicht** zwischen einem Replace-Lauf und seinem Restore;
T7b und T7 gehen zusammen zurück oder gar nicht. Die Regel-4-Änderung am Restore-Filter ist
unabhängig davon rückgängig und harmlos: ohne sie kommt der 409 zur Laufzeit zurück, mehr nicht.

---

## 9. Nachtrag: Codex-Reviews vom 2026-09-23 (gpt-6-sol, adversarial)

### Runde 1 — über die erste Fassung (`83e6ce8`), von Codex als geschlossen bestätigt

Sechs Befunde. Je übernommen oder mit Grund zurückgewiesen; die Folgen stehen in den Tasks und den
Abschnitten 1, 2, 4, 7.

| # | Schwere | Befund | Entscheidung | Folge |
|---|---|---|---|---|
| 1 | high | Replace kann Aliase löschen, die im Rückweg-Record fehlen: `removedTarget` kam aus der Vorschau, die Vorprüfung testete nur die Quell-Id | **Übernommen.** Vor dem Download liest der Dialog das Ziel-Set live und vergleicht jede Replace-Ziel-Id samt Alias-Menge; bei Abweichung kein Download, kein Start, Neuladen und Neu-Bestätigen; die Datei entsteht aus dem Read. Der Frischcheck im Flow prüft ein zweites Mal. Das Restfenster zwischen letztem Lesen und jedem REMOVE bleibt und ist nicht zu schließen (`already-present-filter.ts:55-57`); das Ergebnisprotokoll trägt deshalb, was das Nachlesen nach dem Lauf über die tatsächlich genommenen Aliase liefert | Abschnitt 2, T5 (`verifyReplaceTargets`), T8 |
| 2 | high | Die Rückweg-Datei entsteht zu spät | **Übernommen**, durch Frage 4 entschieden: Pflicht-Download vor dem ersten REMOVE. Test für den Abbruch zwischen den beiden Mutationen: T4 (`cancel()` zwischen Schritt 1 und 2 ⇒ `failed`, `failedStep 1`, kein ADD gesendet) | Abschnitt 2, T4, T7, T8 |
| 3 | high | Eine verlorene ADD-Antwort gilt als bestätigte Lücke | **Übernommen.** Am Code geprüft: `runOne` macht aus jeder `HttpErrorResponse` außer 429 ein `failed` mit `httpStatus = error.status`, Status 0 wird `networkError` (`seven-tv-run-engine.ts:406-424, 566`). Vertrag: Status 0/502/503/504 ⇒ `unknown` für Operationen mit `transportLossIsUnknown` (der Import-Lauf mit Replace, für alle seine Zeilen — Add, Rename, Replace, Adopt); 500 und jede GQL-Ablehnung bleiben `failed`; ein Live-Nachlesen nach dem Lauf klärt jede `unknown`-Zeile vor jeder Meldung; unklärbar ⇒ in keiner Meldung, `unknown` im Protokoll, Hinweis im Dock. Delete/Restore/Add-only unverändert | T4, T5, T7 |
| 4 | medium | Quellreihenfolge verhindert eine erlaubte Alias-Wiederverwendung | **Übernommen, Variante „ablehnen".** Regel 2 in T3 gibt die Aliase eines Replace nur für die eigene Zeile frei; ein Rename auf einen fremd freigegebenen Namen ist eine Verletzung, in beiden Zeilenreihenfolgen getestet. Grund: konsistent mit Frage 7, keine Abhängigkeitsordnung samt Zyklusfall in der Engine, der Fall geht in zwei Läufen. Abweichung vom Issue-Wortlaut in Abschnitt 7 festgehalten | T3, Abschnitt 7 |
| 5 | medium | Slot-Formel zählt Rename und Replace doppelt | **Übernommen** — der Befund traf zu: die erste Fassung addierte `renameCount` und `replaceCount` auf ein `addCount`, das sie schon enthielt. Jetzt `delta = addCount − removedEntryCount`, `addCount` = alle ADD-Mutationen; drei Einzeltests (Rename, Replace, Replace auf Duplikat) in T3 und T8 | T3, T8, AK 21 |
| 6 | medium | Die Laufvalidierung kann einen unveränderten Import blockieren | **Übernommen** — am Code belegt: `dedupeImportRows` faltet nur nach `sevenTvEmoteId`, `buildImportPreview` prüft Namen nur gegen das Ziel; zwei Quellzeilen gleichen Namens landen heute beide in `toAdd`, 7TV lehnt die zweite ab. Regel 1 in T3 nimmt Doppel zwischen zwei **unveränderten** `toAdd`-Zeilen aus; sobald eine Entscheidung beteiligt ist, gilt sie voll. Test: zwei `toAdd`-Ids mit gleichem Alias, kein Zielkonflikt ⇒ `ok: true`, Plan wie heute | T3 |

Keiner der sechs Befunde ist zurückgewiesen. Die Planentscheidung zu 3 („Statusliste 0/502/503/504,
500 bleibt `failed`") hat Runde 2 gekippt (s. u., Finding 4).

### Runde 2 — über die zweite Fassung (`4ef773c`), alle fünf Befunde vom Betreiber angenommen

| # | Schwere | Befund | Lösung | Folge |
|---|---|---|---|---|
| 1 | high | Eine bestätigte REMOVE fällt aus dem Audit, wenn die ADD-Antwort verloren geht und das Nachlesen scheitert: die Zeile bleibt `unknown` und stand in keiner Meldung | **Übernommen.** Die Bestätigung eines Schritts ist eine eigene Größe: `RunQueueItem.completedSteps` zählt die von 7TV bestätigten Schritte, unabhängig vom Endstatus der Zeile. Die Löschmeldung nimmt jede Replace-Zeile mit `completedSteps >= 1` — `done`, `failed` oder `unknown` —, `removedTarget.confirmed` hängt daran; nur die Quell-Id bleibt bei `unknown` aus `syncImported`. Das Nachlesen kann `completedSteps` auf 1 heben (Ziel weg), nie senken | T4, T5, T7; AK 17, 19 |
| 2 | high | Einträge ohne Alias umgehen beide Prüfungen: `aliaslessIds` wurde nicht verglichen, der REMOVE nimmt den Eintrag trotzdem, die Rückweg-Datei kannte ihn nicht | **Übernommen, Variante „abbilden".** Beide Prüfstellen (Dialog-Read vor dem Download, Flow-Read vor dem Start) vergleichen auf Eintragsebene, `aliaslessIds` eingeschlossen; die Rückweg-Datei trägt `removedTarget.entries` mit `alias: string \| null`; `removedEntryCount` zählt den aliaslosen Eintrag; T2 prüft, wie ein solcher Eintrag heute in der Vorschau erscheint. Sperren verworfen: der Restore-Code kann den Eintrag zwar nicht ausdrücken (`aliases` verlangt nicht-leere Strings), aber diese Datei wird nie geladen — ihr Rückweg ist ein Mensch mit einem ADD ohne Alias, und den kann die Datei anleiten. Test für den gemischten Fall an beiden Prüfstellen und im Builder. **Stand der Runde; am selben Tag vom Betreiber überholt:** die Datei ist seit Fassung 4 über den Restore-Weg ladbar, und der aliaslose Eintrag kommt per ADD ohne Alias aus dem Lauf zurück, nicht von Hand (T7b) | Abschnitt 2, T2, T5, T7, **T7b**, T8; Abschnitt 7 |
| 3 | high | Das asynchrone `settle` hing nicht am abgeschlossenen Lauf: `isRunning` fällt vor `onComplete`, ein neuer Import kann starten, `settle(key)` träfe eine fremde Queue; `RunResult.items` war ohnehin ein Snapshot | **Übernommen, Variante „lauf-gebundenes Ergebnis".** `engine.settle` entfällt; die Engine bleibt lauf-agnostisch. Der Service klärt auf einer Kopie des Snapshots, veröffentlicht Kopie und `settlement: 'settled'` atomar über `run.set` unter `applyIfCurrent`, und die Anzeige liest `importService.items` (Engine-Queue nur während `isRunning`, danach das eigene Ergebnis). Späte Rückrufe gegen einen neueren Lauf oder nach `reset()` fallen weg wie jede andere R15-Antwort. „Start gesperrt bis fertig" verworfen: es bräuchte einen vierten Arbiter-Zustand, blockierte Delete/Restore mit und ersetzte den Guard gegen späte Antworten trotzdem nicht (`reset()` und Kanalwechsel erzeugen dieselbe Lage). Tests: Neustart während des Lesens, `reset()` während des Lesens | T4, T5, T7; AK 15, 16 |
| 4 | high | HTTP 500 als `failed` war eine falsche Sicherheit | **Übernommen**, Betreiber hat zugestimmt. Regel statt Liste: eine GraphQL-Antwort ist eindeutig (Erfolg/Backoff/`failed`); eine `4xx` aus 7TVs HTTP-Schicht ist „abgelehnt, bevor verarbeitet" und bleibt `failed`/Backoff/Abbruch — darunter der 401 mit dem Body außerhalb des Schemas, den `abortsForMissingPrivileges` am `httpStatus` erkennt und der weiter Token löscht und abbricht; alles andere (Status 0, jede 5xx inkl. 500) ist `unknown`. Test: 500 ⇒ `unknown`, 401 ⇒ `failed` + Abbruch, beide auf einer Operation mit dem Flag | T4; Abschnitt 1, 7 |
| 5 | medium | Die Regel „jeder erzeugte Alias gültig" blockierte einen unveränderten Dialog mit `invalidNames`-Zeilen | **Übernommen.** Regel 3 gilt nur für den getippten Alias einer Rename-Zeile; Quellnamen (`toAdd`, Replace-ADD, Adopt) laufen wie heute, 7TV entscheidet. Test: keine Entscheidungen, eine `invalidNames`-Zeile ⇒ `ok: true` | T3; Abschnitt 7 |

Keiner der fünf Befunde ist zurückgewiesen. Neue offene Fragen ergeben sich nicht; die beiden
Variantenwahlen (2 „abbilden", 3 „lauf-gebunden") sind mit Grund im Plan und kippbar, ohne dass
ein Task vor Beginn davon abhängt.

### Nachtrag: zwei Betreiber-Entscheidungen nach Runde 2 (2026-09-23, Fassungen 4 und 5)

Keine Codex-Runde, aber zwei Entscheidungen, die den Plan nach Runde 2 noch einmal verschoben
haben und hier festgehalten sind, damit die nächste Runde sie nicht als Drift liest:

| Fassung | Entscheidung | Folge |
|---|---|---|
| 4 | **Die Übertragungsdateien sind über den bestehenden Restore-Weg ladbar**; „von Hand wiederherstellen" ist abgelehnt. Nur entfernte Ziel-Einträge, nur Lücken schließen, aliaslose per ADD ohne Alias | Neuer Task T7b; T7 verliert die Restore-Abweisung; AK 18 in der Restore-Hälfte revidiert; R1–R4 |
| 5 | **Keine Löschung ohne Restore-Weg:** weil der Restore kanalgebunden ist (Befund T7b), ist „Ziel ersetzen" für ein ungetracktes Ziel gesperrt — in der Validierung (T3, Regel 7) und in der Oberfläche (T8, ausgegraut mit Grund). Skip, Rename, Adopt bleiben | **T6 entfällt** (kein Replace ohne Kanal ⇒ keine Löschmeldung ohne Kanal); Frage 1 überholt, Frage 6 eingeengt; AK 19 in der ungetrackten Hälfte gegenstandslos; R5; T10 läuft Replace/Restore gegen ein getracktes Set und belegt an olafs Set nur die Sperre. Folge-Issue „Restore pro Set" (set-zentrierter `sync-deleted` + `sync-restored`, Restore ohne Kanalseite), mit dem die Sperre fällt |
