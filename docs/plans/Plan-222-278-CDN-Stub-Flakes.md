# Plan #222 + #278 — 7TV-CDN suiteweit stubben, zwei Flakes (Unit + E2E) beheben

Erstellt am 2026-09-28 gegen `fix/222-278-cdn-stub-flakes` = `74e5cc09` (= `origin/feat/emote-sets-200`).
PR gegen `feat/emote-sets-200`. Quellen: Issues #222 und #278, die Analyse vom 2026-09-27/28 samt
Messprotokollen (Scratchpad `analysis.md`, `unit/`, `e2e/`) und die Betreiberentscheidungen dort in
Abschnitt 4 (**verbindlich, hier nicht neu verhandelt**), `CLAUDE.md` („Tests" inkl. `page.clock`-Regeln,
Regeln 3, 11, 12, 18, „Sprache"), `web/.claude/CLAUDE.md`. Alle genannten Codestellen sind gelesen.

**Kein Code im Plan.** Namen stehen nur, wo sie ein Vertrag sind. Weicht der Code vom Plan ab, gilt
der Plan; der Fund kommt in den Task-Bericht.

Betreiberentscheidungen: **B1** Der Guard lässt den Test **scheitern** (nicht nur loggen); „kein Request
an cdn.7tv.app" heißt: **kein Request erreicht das Netz** — per Stub beantwortete sind erlaubt.
**B2** Stub-Antwort ist ein 1×1-PNG; wer echte Maße braucht, überschreibt selbst. **B3** Nur das
7TV-CDN — `7tv.io`/Turnstile gehen als „Bekannte Grenzen" in die PR-Beschreibung. **B4** Liegt die
Flake-Ursache im Produkt, wird dort repariert (+ Unit-Test + Browser-Check). **B5** (Orchestrator)
`playwright.measure.config.ts` und `playwright.audit.config.ts` bleiben außen vor.

---

## 1. Kurzbefund

- **Beide Flakes sind Timing-Races, keiner berührt das echte CDN** (Analyse §1).
- **Unit** (`import-conflict-resolution-step.spec.ts:697`): echter Komponentenfehler. Die initiale
  Fokus-Übergabe (`afterNextRender`, `import-conflict-resolution-step.ts:734`) setzt
  `skipNextFocusedKey` und ruft `focusRow(0)`; Zeile 0 existiert dann nie, der Rückfall ist **ein**
  `requestAnimationFrame` (`:917`). Kommt der Frame vor dem CDK-Render, landet der Fokus nie, das Flag
  bleibt stehen und schluckt den nächsten echten `focusin`. Allein ausgeführt 20/20 rot; im
  Browser plausibel: Fokus-Übergabe (WCAG 2.4.3) fällt still aus, der erste echte Zeilenfokus
  animiert nicht.
- **E2E** (`emote-import.e2e.spec.ts:1802` und Geschwister `:1694`, gemeinsamer Helfer `openGrid`
  `:1571`ff): `click()` auf „Set laden" (`:1613`) lässt den Zeiger dort stehen, wo nach dem Mount die
  Zelle Anim10 liegt; Chromiums Hover-Neuberechnung startet die 200-ms-Verweilzeit, bevor
  `mouse.move(0,0)` (`:1623`) greift. Die Komponente verhält sich korrekt. Experiment „erst parken,
  dann per Tastatur laden": 30/30 bei 6 Workern (vorher 8/30).
- **Interception verstärkt das E2E-Race** (6 Worker: 22/30 rot mit Stub, 1/30 mit echtem Netz) —
  deshalb kommt der suiteweite Stub erst **nach** dem E2E-Fix.
- **Inventur #222** (Analyse §3): 21 Specs importieren direkt aus `@playwright/test`; 100 Tests
  schicken zusammen 434 echte Requests ans CDN; zwei lokale Stubs (`usage-atlas:1174`, `emote-import:1573`,
  Letzterer zeichnet URLs auf). Probelauf mit globalem `context.route`-Stub: 214/214 grün, Laufzeit
  unverändert.

---

## 2. Tasks

Ein Commit je Task (Conventional Commits, englisch, keine `#`-Referenzen in Git-Metadaten). Jeder
Task fährt die gefilterten Specs plus `npm --prefix web run build`,
`cd web && npx tsc -p tsconfig.spec.json --noEmit`, `npm --prefix web run lint` und Prettier
(`npx prettier --check` aus `web/`). Modelle: T1 `opus` (Fokuslogik), T2 `sonnet`, T3 `opus`
(Guard-Vertrag), T4 Orchestrator.

### T1 — Produktfix: initiale Fokus-Übergabe im Konfliktschritt

**Dateien:** `web/src/app/shared/seven-tv/import-conflict-resolution-step.ts` (+ `.spec.ts`).

**Vertrag.**
1. Die Übergabe ist ein **ausstehender Zustand**, der erst endet, wenn Zeile 0 tatsächlich im DOM
   steht — ereignisgetrieben (z. B. am gerenderten Bereich des Viewports, `renderedRange` bzw.
   `renderedRangeStream`, oder `afterRender`-Mechanik), **kein** einzelner Frame, **keine** feste
   Wartezeit, **keine** unbegrenzte Frame-Schleife.
2. Das Skip-Flag gilt für **genau diesen einen** Übergabe-`focus()`: gesetzt unmittelbar davor,
   zurückgesetzt unmittelbar danach (try/finally). `focusin` feuert synchron in `focus()`; landet der
   Fokus nicht (Element schon fokussiert, nicht fokussierbar, losgelöst), bleibt nichts stehen. Das
   Flag darf nie über eine Aufgabengrenze hinaus leben.
3. **Aufgabe der Übergabe**, ohne Fokus zu stehlen: der Nutzer fokussiert vorher selbst etwas im
   Schritt (jeder `focusin` im Host — dieser Fokus zählt dann normal und animiert); der Schritt wird
   zerstört (`DestroyRef`); die Liste ist leer (heutiges Verhalten von `focusRow`, unverändert).
   Empfehlung für den Implementer: nur landen, solange der Fokus „nirgends" ist (`body`/`null`) oder
   auf dem eigenen Container liegt; weicht er davon ab, begründen.
4. Pfeil-/Home-/End-Navigation (`focusRow` für Zeilen jenseits des Puffers, `:904ff`) bleibt
   funktional gleich; ob sie denselben „warten bis gerendert"-Mechanismus nutzt, entscheidet der
   Implementer — der Bestandsfall `:467` muss grün bleiben. Kommentar `:617-621` und `:731-733`
   (englisch) an den neuen Vertrag anpassen.

**Spec (Regel 12 — Verhalten, nicht Vorlage).** `settleInitialFocus` (`:650`) wartet auf eine echte
Bedingung (Zeile 0 ist `document.activeElement`, gepollt mit Obergrenze) statt 20 ms; der Kommentar
dazu wird korrigiert. Spec-würdig, weil Fokusführung Accessibility-Semantik ist: (a) Öffnen legt den
Fokus auf Zeile 0, ohne dass sie animiert (Bestand `:656` erweitert oder neuer Fall); (b) der erste
echte Fokus auf eine andere Zeile nach der Übergabe animiert — das ist der Regressionsfall `:697`;
(c) fokussiert der Nutzer vor der Übergabe eine Zeile, bleibt der Fokus dort und die Zeile animiert —
**nur wenn** sich das in jsdom deterministisch herstellen lässt, sonst im Bericht begründet weglassen;
(d) Zerstören vor der Übergabe wirft nicht und fokussiert nichts. **Nicht** geprüft: das Flag selbst,
Frame-Zählungen, Mechanikdetails.

**Akzeptanz.** `ng test --include=<spec> --filter "falls back to the focused row"` **allein ≥20× grün**
(vorher 20/20 rot); ganze Datei und volle Vitest-Suite grün. **Browser-Check** (Orchestrator, mit
Betreiber-Cookie, nach T1): Import-Dialog bis zum Namenskonflikt-Schritt; (1) Fokus sitzt sichtbar
auf Zeile 0, Zeile 0 animiert nicht; (2) Pfeil runter → Zeile 1 animiert; (3) direkt Zeile 1
anklicken/tabben ohne vorherige Taste → animiert sofort; (4) Zeiger auf andere Zeile und wieder weg →
Wiedergabe fällt an die fokussierte zurück; (5) dasselbe mit vielen Konflikten (virtualisiert) und
in der schmalen Anordnung.

**Abhängigkeiten:** keine — kann in einer Lane-Worktree parallel zu T2 laufen (eigenes
`npm ci` in `web/`).

### T2 — E2E-Flake in `openGrid`

**Dateien:** `web/e2e/emote-import.e2e.spec.ts` (nur `openGrid` und sein Kommentar).

**Vertrag.** Der Zeiger steht **vor** dem Laden des Sets außerhalb des Rasters (nach dem letzten
Klick, vor dem Auslösen), und „Set laden" wird per Tastatur ausgelöst (Fokus + Enter, wie im
Experiment der Analyse) — damit kann beim Mount kein Hover und keine Verweilzeit entstehen. Das
bisherige `mouse.move(0, 0)` nach `toBeVisible()` entfällt oder bleibt als harmloser Nachlauf;
der K3-Kommentar (`:1615-1622`) beschreibt danach die neue Ursache und warum „parken nach sichtbar"
nicht reichte. `page.clock`-Regeln (CLAUDE.md „Tests") bleiben: `install()` vor `goto`, **kein**
früheres `pauseClock()` (ein `pauseAt(now+1000)` springt und feuert eine fällige Verweilzeit).
Kein Umbau des CDN-Stubs hier — das ist T3.

**Akzeptanz.** Beide Animationstests (`:1694`, `:1802`) mit `--repeat-each=20` grün, **einmal mit
Standard-Workern und einmal mit `--workers=6`**, ohne parallele Last (keine Vitest-Läufe aus T1
gleichzeitig). Übrige Tests der Datei grün.

**Abhängigkeiten:** keine; T3 wartet auf T2.

### T3 — Gemeinsame Fixture, suiteweiter CDN-Stub, Guard, Konvention

**Dateien:** neu `web/e2e/support/test.ts` (Name frei, Vorschlag); alle 21 Specs in `web/e2e/`
(inkl. `theme.spec.ts`, `touch-mobile.e2e.spec.ts`); `usage-atlas.e2e.spec.ts` (lokalen Stub
`:1174` und `PNG_1X1` `:31` entfernen, Kommentar kürzen); `emote-import.e2e.spec.ts` (Aufzeichnung
bleibt, siehe 4); `web/eslint.config.mjs`; `docs/DECISIONS.md`; `CLAUDE.md` („Tests").

**Vertrag.**
1. **Fixture-Modul** re-exportiert `test` (per `test.extend`) und `expect` sowie die in Specs benutzten
   Typen. Eine **automatische** Fixture (`auto: true`) registriert auf dem `context` eine Route für
   `https://cdn.7tv.app/**`, die mit dem 1×1-PNG antwortet (B2). Kontext-Ebene, damit alle Seiten
   eines Tests und beide Projekte (`chromium`, `mobile-chrome`) sie bekommen — das Projekt spielt
   keine Rolle, es zählt allein der Import.
2. **Ein gemeinsamer Stub-Helfer** (z. B. `fulfillCdnStub(route, body?)`) ist der **einzige** Weg,
   eine CDN-Antwort zu erzeugen; er setzt immer einen Marker-Header (z. B. `x-e2e-cdn-stub`). Die
   Kontext-Route, die Aufzeichnung in `emote-import` und jede künftige Überschreibung (andere Maße,
   B2) benutzen ihn. Das PNG lebt nur noch dort.
3. **Guard** in derselben automatischen Fixture: beobachtet auf dem Kontext jede Antwort und jeden
   Fehlschlag für `cdn.7tv.app`. **Verstoß** = eine Antwort **ohne** Marker (kam aus dem Netz) oder
   ein fehlgeschlagener Request (Netzversuch, oder ein Stub hat abgebrochen statt beantwortet). Nach
   `use()` wirft die Fixture mit der Liste der verletzenden URLs, der Test ist rot (B1). Requests, die
   erst beim Abbau nach der Prüfung entstehen, zählen nicht — hinnehmbar.
4. **Vorrang:** Seiten-Routen gehen vor Kontext-Routen. Die Aufzeichnungs-Route in `openGrid`
   bleibt deshalb Seiten-Route und antwortet über den Helfer (Marker ⇒ Guard zufrieden); ihr lokales
   `PNG_1X1` (`:1550`) entfällt. Keine eigene Recorder-Fixture: ein einziger Konsument lohnt sie
   nicht. `route.fallback()` aus Seiten-Routen landet beim Kontext-Stub (in Ordnung);
   `route.continue()` an eine CDN-URL würde ans Netz gehen und den Guard auslösen — gewollt.
5. **`support/mocks.ts`** importiert weiter nur Typen aus `@playwright/test` und **nicht** die Fixture:
   `atlas-image-loading.measure.ts` nutzt `mocks.ts` und muss das echte CDN treffen (B5). Measure-
   und Audit-Config werden nicht angefasst; der Audit-Stub `ui-audit.audit.ts:96` bleibt.
6. **ESLint** — ja, eingeführt: `@typescript-eslint/no-restricted-imports` für `e2e/**/*.spec.ts`,
   Wert-Importe aus `@playwright/test` verboten (`allowTypeImports`), Meldung verweist auf das
   Fixture-Modul. Begründung: eine neue Spec mit dem alten Import schaltet Stub und Guard **still**
   ab — genau die Lücke, die #222 schließt; die Regel ist syntaktisch, braucht keine Typinfo und
   passt in die bestehende Lint-Stufe. `support/`, `*.measure.ts`, `audit/` sind nicht betroffen.
7. **Doku im selben Commit (Regel 3):** neuer DECISIONS-Eintrag oben, englisch, Bestand unverändert —
   Kernaussage: *E2E specs import `test`/`expect` from the shared fixture, never directly from
   `@playwright/test`; the 7TV CDN is stubbed suite-wide with a 1×1 PNG and guarded — a CDN request
   that reaches the network fails the test; measure and audit configs are deliberately excluded.*
   `**Betrifft:**` nennt Fixture, Helfer, ESLint-Regel, beide bisherigen Einzel-Stubs. In `CLAUDE.md`
   „Tests" ein Satz: Specs importieren aus dem Fixture-Modul, CDN ist gestubbt und bewacht, eigene
   Bild-Überschreibungen laufen über den Helfer.

**Nachweis, dass der Guard greift** — einmalig während der Entwicklung, **nicht committet**: (a) Stub
per temporärer Änderung aus, eine CDN-lastige Spec (z. B. `channel-workspace`) laufen lassen ⇒ rot,
Meldung nennt die URLs; (b) eine Seiten-Route, die ohne Helfer (ohne Marker) antwortet ⇒ rot. Beide
Ausgaben in den Task-Bericht und die PR-Beschreibung. Kein committeter Selbsttest: ein `test.fail()`-Fall
würde bei **jedem** Fehler grün und bliebe dauerhaft als „erwartet rot" im Report; ESLint-Regel plus
Guard-Meldung decken den Regressionsweg ab.

**Akzeptanz.** Volle E2E-Suite **≥2× grün** hintereinander, ohne parallele Last; Guard in keinem
Test ausgelöst. Beide Animationstests erneut `--repeat-each=20` bei Standard-Workern und
`--workers=6` (die Interception verstärkt das Race — hier zeigt sich, ob T2 trägt). `grep` zeigt
keinen Wert-Import aus `@playwright/test` in `e2e/*.spec.ts` und kein lokales `PNG_1X1` mehr in
Specs; Lint fängt eine absichtlich falsch importierende Probedatei (nicht committet).

**Abhängigkeiten:** T2 (gleiche Datei, und der Stub verstärkt das Race).

### T4 — Abnahme (Orchestrator)

Gates aus Abschnitt 4, Browser-Check aus T1, dann `/codex:review --model gpt-6-sol --scope branch
--base origin/feat/emote-sets-200`; Findings vorlegen, nicht umsetzen. PR-Beschreibung mit
Messwerten (vorher/nachher), Guard-Nachweis und „Bekannte Grenzen" (Abschnitt 5).

---

## 3. Reihenfolge und Parallelität

T1 ∥ T2 → T3 → T4. T1 ist unabhängig (andere Dateien, nur Vitest) und darf in einer Lane-Worktree
laufen; T2 vor T3, weil beide `emote-import.e2e.spec.ts` ändern und der Stub das Race verstärkt.
**E2E-Messungen laufen nie unter paralleler Last** — solange T2/T3 messen, ruhen Vitest- und
E2E-Läufe anderer Lanes; ein roter E2E-Lauf unter Last zählt nicht (Speicherdruck-Falle). Merge der
T1-Lane vor T3, damit die Abschlussläufe den ganzen Stand sehen.

## 4. Gates

`dotnet test EmotePurge.slnx` · `npm --prefix web run build` · `cd web && npx tsc -p tsconfig.spec.json
--noEmit` · `npm --prefix web test -- --watch=false` · `npm --prefix web run lint` · Prettier aus
`web/` · volle E2E (`npm --prefix web run e2e`) nur, wenn `:5151`, `:4200` und `:4300` frei sind ·
`node scripts/coverage-local.mjs`.

**Issue-Akzeptanz:** kein E2E-Lauf erreicht `cdn.7tv.app` im Netz (Guard) · beide Flake-Fälle
`--repeat-each` ≥20 grün (Unit allein ≥20×; E2E bei Standard- und 6 Workern) · volle Suite ≥2× grün.

## 5. Bekannte Grenzen (für die PR-Beschreibung)

- `7tv.io/v4/gql` (198 Requests im Inventurlauf) und Turnstile sind **nicht** bewacht (B3); keiner
  zeigte eine echte Serverantwort, Turnstile ist über `mockTurnstile` gestubbt.
- Measure-Config trifft bewusst das echte CDN, Audit-Config behält ihren eigenen Stub (B5).
- Verhaltensänderung: bisher fehlschlagende Sprites laden jetzt (1×1), werden sichtbar, animierte
  Overlays erreichen „settled". Keine Spec prüft Maße, `naturalWidth`, Screenshots oder andere
  Formate gegen ungestubbte Bilder (Analyse §3); das 1×1-Quadrat löste kein NG02952 aus.
- Requests nach der Guard-Prüfung (Test-Abbau) werden nicht gezählt.

## 6. Risiken und offene Punkte

1. **T1 im echten Browser** ist nur plausibel, nicht gemessen — der Browser-Check entscheidet, ob der
   Fehler dort auftrat; das Fixverhalten muss dort unverändert gut aussehen.
2. **Fall (c) in T1** ist in jsdom evtl. nicht deterministisch herstellbar; dann bleibt er dem
   Browser-Check.
3. **Marker-Header und Cache:** ob ein innerhalb desselben Tests aus dem Speicher-Cache bedientes Bild
   ein `response`-Ereignis mit Marker liefert, prüft T3 beim Guard-Nachweis; fehlt der Marker dort,
   muss der Guard Cache-Treffer (`fromServiceWorker`/Cache-Status) erkennen statt sie zu melden.
4. **Weitere Timing-Races** unter Interception sind denkbar; die zwei vollen Läufe plus die
   6-Worker-Wiederholung sind die Stichprobe, keine Garantie.
