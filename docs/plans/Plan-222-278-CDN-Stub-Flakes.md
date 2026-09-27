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

**Überarbeitet nach Codex-Sol-Plan-Review (2026-09-28, needs-attention, vier Findings angenommen):**
B1 wird **konstruktiv** umgesetzt — das Netz ist für `cdn.7tv.app` per Resolver-Regel unerreichbar,
der Guard erkennt nur noch Ausbrüche aus den Stubs (F1/F2, T3); off-buffer-Tastaturnavigation
bekommt denselben Warte-Mechanismus wie die Übergabe (F3, T1); alle E2E-Abnahmeläufe mit
`--retries=0`, ein Report mit „flaky" gilt als rot (F4).

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
  Letzterer zeichnet URLs auf). Probeläufe: globaler `context.route`-Stub 214/214 grün; Chromium mit
  `--host-resolver-rules=MAP cdn.7tv.app ~NOTFOUND` ebenfalls 214/214, lokale `page.route`-Stubs
  wurden weiter bedient (Routing greift vor DNS). Laufzeit jeweils unverändert.

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
4. **Off-buffer-Navigation nutzt denselben Mechanismus** (Codex F3): Pfeil/Home/End auf eine Zeile
   jenseits des Puffers (`focusRow`, `:904ff`) scrollt und wartet dann ebenfalls, bis die Zielzeile
   gerendert ist — heute hängt das an einem einzigen Frame (`:917`), `activeIndex` wandert, der
   DOM-Fokus womöglich nicht. Es gibt **höchstens ein** ausstehendes Fokusziel: ein neueres Ziel
   (weitere Taste, eigener Fokus des Nutzers) **ersetzt** das ältere, die initiale Übergabe ist
   nur der erste Fall davon. Das Skip-Flag gehört nur zur initialen Übergabe; eine Tastaturnavigation
   zählt normal (Kommentar `:617-621`). Kommentare `:617-621`, `:731-733` und die `focusRow`-Doku
   (englisch) an den neuen Vertrag anpassen.

**Spec (Regel 12 — Verhalten, nicht Vorlage).** `settleInitialFocus` (`:650`) wartet auf eine echte
Bedingung (Zeile 0 ist `document.activeElement`, gepollt mit Obergrenze) statt 20 ms; der Kommentar
dazu wird korrigiert. Spec-würdig, weil Fokusführung Accessibility-Semantik ist: (a) Öffnen legt den
Fokus auf Zeile 0, ohne dass sie animiert (Bestand `:656` erweitert oder neuer Fall); (b) der erste
echte Fokus auf eine andere Zeile nach der Übergabe animiert — das ist der Regressionsfall `:697`;
(c) fokussiert der Nutzer vor der Übergabe eine Zeile, bleibt der Fokus dort und die Zeile animiert —
**nur wenn** sich das in jsdom deterministisch herstellen lässt, sonst im Bericht begründet weglassen;
(d) Zerstören vor der Übergabe wirft nicht und fokussiert nichts; (e) `End` auf einer Liste größer
als der Puffer (Bestand `:467`, 200 Zeilen) führt `document.activeElement` auf Zeile 199, und ein
schnell folgendes zweites Ziel gewinnt gegen das erste. jsdom hat kein `scrollTo` — der Bestandsfall
mockt `scrollToIndex` deshalb weg; Weg dafür: der Mock verschiebt den gerenderten Bereich des
Viewports (z. B. `setRenderedRange`) statt zu scrollen. Lässt sich das nicht deterministisch
herstellen, bleibt (e) im Bericht begründet weg und der Browser-Check (6) deckt ihn. **Nicht**
geprüft: das Flag selbst, Frame-Zählungen, Mechanikdetails.

**Akzeptanz.** `ng test --include=<spec> --filter "falls back to the focused row"` **allein ≥20× grün**
(vorher 20/20 rot); ganze Datei und volle Vitest-Suite grün. **Browser-Check** (Orchestrator, mit
Betreiber-Cookie, nach T1): Import-Dialog bis zum Namenskonflikt-Schritt; (1) Fokus sitzt sichtbar
auf Zeile 0, Zeile 0 animiert nicht; (2) Pfeil runter → Zeile 1 animiert; (3) direkt Zeile 1
anklicken/tabben ohne vorherige Taste → animiert sofort; (4) Zeiger auf andere Zeile und wieder weg →
Wiedergabe fällt an die fokussierte zurück; (5) dasselbe mit vielen Konflikten (virtualisiert) und
in der schmalen Anordnung; (6) bei vielen Konflikten `End`, dann `Home`, dann mehrfach schnell
`End`/`Home` im Wechsel → der sichtbare Fokus sitzt jeweils auf der Zielzeile, nie „verloren".

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

**Akzeptanz.** Beide Animationstests (`:1694`, `:1802`) mit `--repeat-each=20 --retries=0` grün,
**einmal mit Standard-Workern und einmal mit `--workers=6`**, ohne parallele Last (keine
Vitest-Läufe aus T1 gleichzeitig); ein Report mit „flaky" gilt als rot. Übrige Tests der Datei grün.

**Abhängigkeiten:** keine; T3 wartet auf T2.

### T3 — Resolver-Sperre, gemeinsame Fixture, suiteweiter CDN-Stub, Guard, Konvention

**Dateien:** `web/playwright.config.ts`; neu `web/e2e/support/test.ts` (Name frei, Vorschlag); alle
21 Specs in `web/e2e/` (inkl. `theme.spec.ts`, `touch-mobile.e2e.spec.ts`); `usage-atlas.e2e.spec.ts`
(lokalen Stub `:1174` und `PNG_1X1` `:31` entfernen, Kommentar kürzen); `emote-import.e2e.spec.ts`
(Aufzeichnung bleibt, siehe 4); `web/eslint.config.mjs`; `docs/DECISIONS.md`; `CLAUDE.md` („Tests").

**Vertrag.** Grundsatz nach Codex F1/F2: Netzverkehr zum CDN wird **konstruktiv unmöglich**, statt
ihn nachträglich zu erkennen; der Guard erkennt nur noch, dass ein Request an den Stubs vorbeikam.
1. **Resolver-Sperre** nur in `web/playwright.config.ts`: Chromium-Startargument
   `--host-resolver-rules=MAP cdn.7tv.app ~NOTFOUND` im gemeinsamen `use.launchOptions`, damit beide
   Projekte es erben (`devices['Pixel 5']` bringt kein eigenes `launchOptions` mit und läuft auf
   Chromium — T3 prüft beides, siehe Nachweis). Routing greift vor DNS (Analyse, Experiment b), Stubs
   werden also weiter bedient. Measure- und Audit-Config bleiben unberührt (B5). Setzt ein Projekt
   später eigene `launchOptions`, muss es das Argument mitnehmen — Kommentar an der Stelle.
2. **Fixture-Modul** re-exportiert `test` (per `test.extend`) und `expect` sowie die in Specs
   benutzten Typen. Eine **automatische** Fixture (`auto: true`) registriert auf dem `context` eine
   Route für `https://cdn.7tv.app/**`, die mit dem 1×1-PNG antwortet (B2) — Kontext-Ebene, damit
   alle Seiten eines Tests sie bekommen; das Projekt spielt keine Rolle, es zählt der Import.
3. **Ein gemeinsamer Stub-Helfer** (z. B. `fulfillCdnStub(route, body?)`) ist der Ort, an dem das PNG
   lebt; Kontext-Route, Aufzeichnung in `emote-import` und künftige Überschreibungen (andere Maße, B2)
   benutzen ihn. **Kein Marker-Header, kein Guard-Vertrag** — der Guard braucht ihn nicht mehr, und
   Routing schaltet den HTTP-Cache ab, eine Cache-Sonderbehandlung entfällt.
4. **Guard** in derselben automatischen Fixture, genau **ein** Signal: ein `requestfailed` auf dem
   Kontext für `cdn.7tv.app` mit Fehlertext `net::ERR_NAME_NOT_RESOLVED`. Das heißt: ein Request ist
   an allen Stubs vorbei in Richtung Netz gegangen (`route.continue()`, fehlende Route, nicht
   migrierter Import) und an der Sperre gescheitert. `ERR_ABORTED` und jede andere Abbruchart werden
   **ignoriert** — URL-Wechsel eines Sprites und Recycling im virtuellen Scroll brechen gestubbte
   Requests ab, ohne dass etwas ins Netz ging (F1). Nach `use()` wirft die Fixture mit der
   URL-Liste, der Test ist rot (B1).
5. **Vollständigkeit der Erkennung** (F2 ist damit keine Egress-Frage mehr): keine feste Wartezeit
   vor der Prüfung — sie kostete jeden der ~214 Tests. Stattdessen wartet die Prüfung nur, **wenn**
   noch CDN-Requests offen sind (Request gesehen, weder beendet noch gescheitert), und dann begrenzt
   (Größenordnung 1 s); ein DNS-Fehlschlag an der Sperre kommt in Millisekunden. Was danach noch
   offen ist oder erst im Abbau entsteht, zählt nicht — steht unter „Bekannte Grenzen".
6. **Vorrang:** Seiten-Routen gehen vor Kontext-Routen. Die Aufzeichnungs-Route in `openGrid`
   bleibt Seiten-Route und antwortet über den Helfer; ihr lokales `PNG_1X1` (`:1550`) entfällt.
   Keine eigene Recorder-Fixture: ein einziger Konsument lohnt sie nicht. `route.fallback()` landet
   beim Kontext-Stub (in Ordnung).
7. **`support/mocks.ts`** importiert weiter nur Typen aus `@playwright/test` und **nicht** die
   Fixture: `atlas-image-loading.measure.ts` nutzt `mocks.ts` und muss das echte CDN treffen (B5).
8. **ESLint** — ja, eingeführt: `@typescript-eslint/no-restricted-imports` für `e2e/**/*.spec.ts`,
   Wert-Importe aus `@playwright/test` verboten (`allowTypeImports`), Meldung verweist auf das
   Fixture-Modul. Begründung: dank Sperre kann eine Spec mit dem alten Import zwar nichts mehr ins
   Netz schicken, sie verliert aber Stub und Guard **still** (Sprites bleiben unsichtbar, Konsole
   voller DNS-Fehler); die Regel ist syntaktisch, braucht keine Typinfo und passt in die Lint-Stufe.
   `support/`, `*.measure.ts`, `audit/` sind nicht betroffen.
9. **Doku im selben Commit (Regel 3):** neuer DECISIONS-Eintrag oben, englisch, Bestand unverändert —
   Kernaussage: *E2E specs import `test`/`expect` from the shared fixture, never directly from
   `@playwright/test`; the regular Playwright config makes `cdn.7tv.app` unresolvable for Chromium, so
   no run can reach the CDN; the fixture stubs it suite-wide with a 1×1 PNG, and a CDN request that
   fails with `ERR_NAME_NOT_RESOLVED` — one that got past every stub — fails the test; cancellations
   are ignored; measure and audit configs are deliberately excluded.* `**Betrifft:**` nennt Config,
   Fixture, Helfer, ESLint-Regel, beide bisherigen Einzel-Stubs. In `CLAUDE.md` „Tests" ein Satz:
   Specs importieren aus dem Fixture-Modul, das CDN ist gesperrt, gestubbt und bewacht, eigene
   Bild-Überschreibungen laufen über den Helfer.

**Nachweis, dass der Guard greift** — einmalig während der Entwicklung, **nicht committet**, Ausgaben
in Task-Bericht und PR-Beschreibung: (a) eine Seiten-Route ruft `continue()` für eine CDN-URL ⇒ rot
mit `ERR_NAME_NOT_RESOLVED` und der URL; (b) eine gehaltene CDN-Route plus URL-Wechsel oder virtueller
Scroll, der den Request abbricht ⇒ grün; (c) Kontext-Stub aus, eine CDN-lastige Spec (z. B.
`channel-workspace`) ⇒ rot; (a) zusätzlich einmal in `touch-mobile` (Projekt `mobile-chrome`), um
Vererbung und Chromium dort zu belegen. Kein committeter Selbsttest: ein `test.fail()`-Fall würde bei
**jedem** Fehler grün und bliebe dauerhaft als „erwartet rot" im Report.

**Akzeptanz.** Volle E2E-Suite **≥2× grün** hintereinander mit `--retries=0`, ohne parallele Last,
kein „flaky" im Report, Guard in keinem Test ausgelöst. Beide Animationstests erneut
`--repeat-each=20 --retries=0` bei Standard-Workern und `--workers=6` (die Interception verstärkt das
Race — hier zeigt sich, ob T2 trägt). `grep` zeigt keinen Wert-Import aus `@playwright/test` in
`e2e/*.spec.ts` und kein lokales `PNG_1X1` mehr in Specs; Lint fängt eine absichtlich falsch
importierende Probedatei (nicht committet).

**Abhängigkeiten:** T2 (gleiche Datei, und der Stub verstärkt das Race).

### T4 — Abnahme (Orchestrator)

Gates aus Abschnitt 4, Browser-Check aus T1, dann `/codex:review --model gpt-6-sol --scope branch
--base origin/feat/emote-sets-200`; Findings vorlegen, nicht umsetzen. PR-Beschreibung mit
Messwerten (vorher/nachher, alle mit `--retries=0`), Guard-Nachweis und „Bekannte Grenzen" (Abschnitt 5).

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
`web/` · volle E2E (`npm --prefix web run e2e -- --retries=0`) nur, wenn `:5151`, `:4200` und
`:4300` frei sind · `node scripts/coverage-local.mjs`.

**Issue-Akzeptanz:** kein E2E-Lauf erreicht `cdn.7tv.app` im Netz (Sperre, Guard nie ausgelöst) ·
beide Flake-Fälle `--repeat-each` ≥20 grün (Unit allein ≥20×; E2E bei Standard- und 6 Workern) ·
volle Suite ≥2× grün. **Jeder E2E-Abnahmelauf mit `--retries=0`** (die Config setzt unter `CI` 2
Retries, die einen Flake verdecken würden), und ein Report mit „flaky" gilt als rot (Codex F4).

## 5. Bekannte Grenzen (für die PR-Beschreibung)

- `7tv.io/v4/gql` (198 Requests im Inventurlauf) und Turnstile sind **nicht** bewacht (B3); keiner
  zeigte eine echte Serverantwort, Turnstile ist über `mockTurnstile` gestubbt.
- Measure-Config trifft bewusst das echte CDN, Audit-Config behält ihren eigenen Stub (B5).
- Verhaltensänderung: bisher fehlschlagende Sprites laden jetzt (1×1), werden sichtbar, animierte
  Overlays erreichen „settled". Keine Spec prüft Maße, `naturalWidth`, Screenshots oder andere
  Formate gegen ungestubbte Bilder (Analyse §3); das 1×1-Quadrat löste kein NG02952 aus.
- Der Guard prüft nach dem Testkörper und wartet nur begrenzt auf noch offene CDN-Requests; ein
  Ausbruch, der erst danach scheitert oder erst im Abbau entsteht, wird nicht gemeldet. Ins Netz
  gelangt er trotzdem nicht — die Resolver-Sperre gilt für jeden Request.
- Die Sperre wirkt nur, solange Chromium Namen selbst auflöst: mit einem HTTP-Proxy (`use.proxy`,
  Proxy-Umgebung) löst der Proxy auf. Heute ist keiner konfiguriert.
- Abgebrochene CDN-Requests werden bewusst nicht bewertet (virtueller Scroll, URL-Wechsel).

## 6. Risiken und offene Punkte

1. **T1 im echten Browser** ist nur plausibel, nicht gemessen — der Browser-Check entscheidet, ob der
   Fehler dort auftrat; das Fixverhalten muss dort unverändert gut aussehen.
2. **Fall (c) in T1** ist in jsdom evtl. nicht deterministisch herstellbar; dann bleibt er dem
   Browser-Check.
3. **Fall (e) in T1** (End auf großer Liste bis `document.activeElement`) hängt daran, ob der
   gerenderte Bereich in jsdom deterministisch verschiebbar ist; sonst trägt Browser-Check (6).
4. **Fehlertext der Sperre:** dass `continue()` an eine gesperrte Adresse genau
   `net::ERR_NAME_NOT_RESOLVED` liefert, belegt Nachweis (a); weicht der Text ab, passt T3 das Signal
   an und nennt es im Bericht — ein Abbruch darf nie darunter fallen.
5. **Weitere Timing-Races** unter Interception sind denkbar; die zwei vollen Läufe plus die
   6-Worker-Wiederholung sind die Stichprobe, keine Garantie.
