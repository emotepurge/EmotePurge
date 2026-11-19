# Nachtlauf 2026-11-18: Plan-485 T10–T15 + Gates

Branch `nacht/485-wish-ui`, Start bei `350727d6` (T9).

## Was ist passiert

**Erledigt:** T10 bis T15 sind gebaut und auf `nacht/485-wish-ui` committet, strikt nacheinander, jeder Task als eigener
Subagent. T12 lief auf Opus, alle übrigen auf Sonnet. Die Schluss-Gates sind grün: `dotnet test`, Vitest (5680), lint,
format, E2E (409 passed, 1 bewusst `fixme`) und Coverage (91,2 %). Das Skript endet trotzdem mit Exit 1, weil drei reine
Typdateien keine Messdaten haben (Details unter „Gates am Ende“). Nichts ist gepusht, kein PR, kein Codex-Review.

**Nicht erledigt / offen:**
- **T16 (Live-Verifikation, Stufen A und B, Handy) ist offen**, wie vereinbart, nicht gescheitert. Ohne T16 gibt es laut
  Plan §8.7 keinen PR.
- **Kopfzeile bei 360 px (AK44):** Stufe 2 ist gebaut, besteht aber die Fit-Prüfung in de nicht, solange ein Status-Badge
  steht. Der passende Audit-Fall ist rot, der E2E-Fall ist `test.fixme`. Das ist Betreiberfrage 1.
- **Audit-Harness:** Neben dem T12-Fall sind 66 weitere Fälle rot, in `admin-logs*` und `tags-page*`/`tag-*`. Lesend
  sieht das nach Bestand aus, bewiesen ist es nicht, weil `git worktree add` gesperrt war. Die Befehle zum Nachholen
  stehen unter „Gates am Ende“. Neue `smallTargetsUnder24` im Übernehmen-Dialog sind Betreiberfrage 2.
- Codex-Review (Sol), Opus-Review, Push, PR: morgen.

Commits seit dem Start (`350727d6`): T10 `f81c87c1`. T11 `6a5d59ad`, `78a03926`. T12 `00cacef8`. T13 `10188e5a`. T14
`11f3c92a`, `57660434`, `a5551a6e`, `c95dfa39`, `9a917266`. T15 `bf5b2279`, `3bad0dcc`. Danach `1b7a499a` (Korrektur
in `CLAUDE.md`) und `5b761297` (Spec `wish-operation`). Dazwischen liegen die Commits dieser Notizdatei.

## Annahmen und Strittiges

- Orchestrator-Annahme: Die Subagenten schreiben ihre Übergabe nicht in `~/nachtlaeufe/485-kontext/handoff-485.md`
  (Auftrag), sondern melden sie zurück; der Orchestrator hängt sie unten unter „Übergaben“ an. Folgende Subagenten lesen
  beide Dateien.
- T12: Falls Stufe 2 die Messung nicht besteht, bleibt es heute Nacht bei Stufe 2 (Voreinstellung des Plans), Stufe 3 wird
  nicht gebaut; die Frage steht dann unter „Offene Betreiberfragen“. Das ist so eingetreten.
- Orchestrator-Eingriff in T11: Die doppelte Abbildung von Status, Grund und Restzeit habe ich herausziehen lassen
  (`wish-labels.ts`), siehe T11.
- Orchestrator-Korrektur `1b7a499a`: Die `CLAUDE.md`-Zeile „Wunsch-Emotes“ führte K1 und K2 noch als Branch. Sie sind
  aber gemergt (`ab4882ea` PR #492, `135eae07` PR #493). T15 hatte diesen alten Stand als „alle drei nicht gemergt“
  übernommen.
- Berechtigungsregeln, die heute Nacht griffen und nicht umgangen wurden: Bash-Ketten, `git -C`, `flock`,
  `.bin/playwright` direkt, `git worktree add`. Das Grep-Tool fehlt, `grep` per Bash geht. Ohne `flock` liefen die
  Playwright-Läufe ohne Serialisierung, das war unkritisch, weil die Tasks nacheinander liefen.
- Flake: `touch-mobile.e2e.spec.ts:88` (Drilldown-Sheet) fiel im vollen T14-Lauf einmal aus. Einmal einzeln wiederholt,
  danach grün. In meinem Schlusslauf war er grün.
- T13 hat seinen eigenen Commit per Amend umgeschrieben, damit `Co-Authored-By` als letzte Zeile steht. Nur der eigene,
  noch ungepushte Commit war betroffen.

## Offene Betreiberfragen

1. **Kopfzeile bei 360 px (T12, AK44, F4):** Stufe 2 besteht die Fit-Prüfung in de nicht, solange ein Status-Badge
   steht (Überdeckung bis 33 px). In en passt sie nur knapp und bringt einen neuen `smallTargetsUnder24`-Eintrag. Drei
   Optionen:
   - **(A) Stufe 3:** unter `sm` nichts in der Kopfzeile, nur der Menüeintrag mit Zahl. Das ist eine ausdrückliche
     Abweichung von AK44.
   - **(B) Stufe 2 mit Vorrang:** Solange unter `sm` ein Badge steht, weicht der Zähler, so wie heute schon das
     Quota-Badge dem Worker-Badge weicht. Das wäre eine neue Regel.
   - **(C) Engere Abstände:** Geschätzt spart das höchstens rund 28 px, gebraucht werden 33 px. Nicht gemessen.

   Gebaut ist Stufe 2. Der Audit-Fall `shell-wishes-count-badge @ mobile [de]` bleibt bis zur Entscheidung rot, der
   passende E2E-Fall ist `test.fixme`.
2. **Radios im Übernehmen-Dialog (T14):** Sie sind 16×16 und erscheinen als neue `smallTargetsUnder24`-Einträge. Zwei
   Wege: als „equivalent target“-Ausnahme dokumentieren, so wie die Radios der Backfill-Sektion, oder die ganze Zeile zum
   Label machen, damit das Ziel mindestens 24 px groß ist. Empfehlung: die ganze Zeile zum Label machen, das ist eine
   kleine Änderung in `wish-adopt-dialog.ts`.
3. **Tab-Leiste bei 360 px (T4/T14):** Mit dem sechsten Tab „Wünsche“ scrollt die Leiste seitlich, „Einstellungen“ ist
   erst nach Wischen sichtbar. Das ist Bestandsverhalten mit `overflow-x-auto`. Zu entscheiden ist, ob das so bleiben darf.

## Je Task

### Rahmen

- Bash läuft im Modus „don't ask“: Ketten mit `cd`/`&&`/Pipes und `git -C` werden abgelehnt, einzelne Befehle laufen. Der
  erste T10-Agent hielt das für eine Komplettsperre und brach ab; nach Erklärung lief er durch. Kein Umweg genutzt.
- Der T10-Commit endet mit `Claude-Session:` nach der `Co-Authored-By`-Zeile (Vorgabe der Umgebung). Nicht umgeschrieben;
  ab T11 steht `Co-Authored-By` zuletzt.

### T10 Settings-Abschnitt — `f81c87c1` (Sonnet 5.5)

- Gates laut Agent: Vitest 228 Dateien / 5623 Tests grün, lint 0, format:check 0. Die Spec allein hat 21 Tests.
- Diff: 6 Dateien (neu `wish-settings-section.ts` + Spec, `channel-settings-page.ts`, beide Locales, DECISIONS (y)). Umfang
  passt.
- Annahmen: 400 `wish_settings_out_of_range` als Banner über `wishErrorKey(…,'update')` (kein Feldbezug in der Antwort);
  bei `canEdit=false` entfällt der Speichern-Knopf (Felder gesperrt + Satz); Laufzeit-Feldfehler nutzen
  `wishes.dialog.durationInvalid.*` wieder, Vorlauf hat eigene `wishes.settings.leadInvalid.*`.

### T11 Seite `my-wishes` — `6a5d59ad`, `78a03926` (Sonnet 5.5)

- Gates laut Agent nach dem zweiten Commit: Vitest 229 Dateien / 5629 Tests grün, lint 0, format:check 0, `ng build` grün.
  Die Budget-Warnung des Initial-Bundles (512,57 kB gegen 500 kB) wurde nicht gegen den Stand vor T11 verglichen. Die Seite
  ist lazy geladen; die Warnung prüfe ich bei den Schluss-Gates.
- Der Orchestrator hat nachgesteuert: Im ersten Commit hatte die Seite Status, Ton, Grund und Restzeit aus `WishRow`
  nachgebaut (gegen Plan §2.2, „nirgends sonst nachgebaut“). Der zweite Commit zieht das nach `features/wishes/wish-labels.ts`
  (rein, 6 Tests) heraus. Beide Ansichten nutzen die Datei jetzt, die `WishRow`-Specs liefen unverändert grün.
- Keine Seiten-Spec, so wie Plan §5 es vorsieht. Kein DECISIONS-Eintrag.
- Annahme: Der Plan meint mit „Stretched-Link-Vertrag §2.3“ UI-Designsprache §2.3, nicht Plan §2.3.
  404 `wishes_disabled` leitet auf `/` um.

### T12 Kopfzeile — `00cacef8` (Opus 5.5)

- Gates laut Agent: Vitest 230 Dateien, 5641 Tests grün. lint 0, format:check 0. Gezielte E2E über die Specs, die die
  Kopfzeile oder `mockAuthMe` berühren: 127 grün. Der erste Lauf brach in `theme.spec` ab, weil Chromium den
  Vorgabe-404 als Konsolenfehler loggt. Behoben mit dem Helfer `isWishSummaryNotFound` in den Konsolen-Wächtern.
- **Gebaut ist Stufe 2.** Messung im Audit-Harness, de und en, alle Viewports, `.audit-out/` vorher geleert:
  - Ohne Badge, Zahl 12: passt überall. Bei 360 px de bleiben der Wortmarke 78 px.
  - Mit Badge ab 480 px: passt.
  - Mit Badge bei 360 px en: passt knapp. Die Wortmarke steht bei 0 px, das Logo bei 1 px, und das bringt einen **neuen
    `smallTargetsUnder24`-Eintrag** „Emote Purge 1×28“.
  - **Mit Badge bei 360 px de:** „Abstimmungen“ überdeckt den Badge um 33 px (Worker-Warnung, Zahl 12), 26 px
    (Worker-Warnung, Zahl 3) bzw. 22 px (Live-Quota, Zahl 3). Die üblichen Harness-Metriken bleiben 0, nur die neue
    Fit-Prüfung schlägt an.
  - `h-14` hält.
  - Der Audit-Fall `shell-wishes-count-badge @ mobile [de]` ist absichtlich rot, bis der Betreiber entscheidet.
- Stufe 3 ist nicht gebaut (Auftrag: Voreinstellung des Plans, Frage an den Betreiber).
- Abweichungen und Annahmen:
  - Die Zahl steht als ruhiger Chip, nicht als `app-status-badge`.
  - Der Menüeintrag zeigt „Wünsche“ ohne Zahl, nur unter `sm`.
  - Das Audit-Szenario rechnet mit Zahl 12 statt 3, ist also strenger.
  - `my-wishes` ruft `refresh` nicht auf, weil der Dienst selbst taktet.
  - Die Kanal-Seite ruft `refresh` nach eigenen Befehlen, nach Dialog-Ergebnissen, nach beendeten Entfernungen, bei
    `wish.changed`/`channel.synced` und beim Wieder-Öffnen auf. Kein Aufruf beim Sicherheitstakt und beim Sichtbarwerden.
- Werkzeug: `flock` ist von der Berechtigungsregel gesperrt, auch `flock --version`. Die Playwright-Läufe liefen deshalb
  ohne flock. Port 4300 und :5151 waren frei, parallel lief nichts (die Tasks laufen strikt nacheinander).
- Für T14 offen: `account-deletion.e2e` mockt `/me` ohne `mockAuthMe`, die summary-Anfrage fällt dort durch den Proxy.
  Dabei bricht nichts.

### T13 Audit-Aktionen, i18n, Fehlercodes — `10188e5a` (Sonnet 5.5)

- Gates laut Agent: Vitest 230 Dateien, 5653 Tests grün; lint 0; format:check 0.
- Die elf `wish.*`-Aktionen sind ergänzt. `ACTION_KEYS` hat damit 34 Einträge, 31 davon kanalbezogen, wie im Plan.
  Die Servernamen hat der Agent lesend geprüft; `wish.readd` ist klein geschrieben, der Schlüssel heißt `wishReAdd`.
- Der Akteur „system“ bekommt keinen eigenen Text, weil die anderen Systemeinträge genauso lesen.
- **Gegenprobe AK43** (für den PR-Text): 92 Konstanten in `ApiErrorCodes.cs` stehen gegen 92 in
  `KNOWN_API_ERROR_CODES` und 92 `errors.api.*` je Locale, ohne Differenz. Die zwölf `errors.api.wish_*` passen im
  Wortlaut, geändert wurde keiner.
- i18n-Abschluss ohne Befund: de und en haben dieselben Schlüssel, nichts ist verwaist oder fehlt. „…“ stehen nur an
  Status- und Ladetexten.
- Annahme: Die englischen Audit-Labels hat der Agent selbst formuliert, z. B. „Set entry taken over as wish“ und „Wish
  came due“.

### T14 E2E und Audit-Harness: `11f3c92a`, `57660434`, `a5551a6e`, `c95dfa39`, `9a917266` (Sonnet 5.5)

- Commits:
  - Tests: `a5551a6e` mit Mocks und E2E-Fällen, `c95dfa39` mit E2E- und Audit-Szenarien. Die Fälle stehen in zwei
    Dateien, `web/e2e/wishes.e2e.spec.ts` und `web/e2e/wishes-seven-tv.e2e.spec.ts`.
  - Drei kleine Produktiv-Fixes, alle in K3 und beim Schreiben der Tests gefunden: `11f3c92a` (langer Alias verbreiterte
    den Übernehmen-Dialog, `fieldset` ohne `min-w-0`), `57660434` (Link „Auf 7TV ansehen“ in der Zeile von 20 auf 24 px
    Höhe), `9a917266` (langer Emote-Name in der Lese-Zeile des Nachtragen-Dialogs lief bei 360 px über den Rand).
- Gates laut Agent:
  - E2E komplett: **408 passed, 1 skipped**. Der übersprungene Fall ist `test.fixme`, die Kopfzeile bei 360 px de mit
    Badge, wartet auf Betreiberfrage 1. Die Überdeckung liegt in diesem Fall bei 26 px.
  - Ein Flake beim alten Test `touch-mobile.e2e.spec.ts:88` (Drilldown-Sheet): einmal einzeln wiederholt, dann grün (9/9).
  - Vitest 230/5653 grün, lint und format grün.
- Audit-Harness im Volllauf, `.audit-out/` vorher geleert: **785 passed, 67 rot**.
  - `horizontalOverflowPx` ist überall 0, `contrastViolations` überall leer.
  - Rot ist der bekannte T12-Fall. Dazu kommen `admin-logs*` (axe `label-content-name-mismatch`) und `tags-page*`/`tag-*`
    (`beyondRightEdge` an der Tab-Leiste). Laut Agent nennen deren Fehlermeldungen keine Wünsche, gegen die Basis
    verglichen hat er sie aber nicht. Den Vergleich lasse ich gerade nachprüfen, Ergebnis unten bei „Gates am Ende“.
- **Neue `smallTargetsUnder24`-Einträge** (Gate: „keine neuen“): 16×16-Radios im Übernehmen-Dialog (Eintragsliste und
  Start-Auswahl). Das Label ist nur über den Alias klickbar, nicht über die ganze Zeile. Im selben Muster stehen schon die
  Bestands-Radios der Backfill-Sektion als dokumentierte Ausnahme „equivalent target“ im Harness. Heute Nacht nicht
  geändert, siehe Betreiberfrage 2.
- Weitere Beobachtungen des Agenten (nicht geändert):
  - Bei 360 px scrollt die Kanal-Tab-Leiste mit sechs Tabs seitlich (`overflow-x-auto`, gewollt). Dabei ist „Wünsche“
    angeschnitten und „Einstellungen“ liegt außerhalb. Die Kanal-Szenarien laufen deshalb ohne `strictRightEdge`, die
    Dialog-Szenarien prüfen mit `assertDialogInsideViewport`.
  - Im Konflikt-Banner des Nachtragen-Dialogs wird die Textspalte bei 360 px sehr schmal.
- Annahmen: `reducedMotion` ist ein Szenariofeld im selben CDP-Aufruf wie Zeiger und Farbschema. Der Fall „Einträge
  fehlen in den Nutzungs-Summen“ ist so belegt, dass die Seite gar keine Nutzung mockt und trotzdem wählbar ist.

### T15 Doku: `bf5b2279`, `3bad0dcc` (Sonnet 5.5)

- Gates laut Agent: Vitest 230 Dateien mit 5653 Tests grün, lint und format grün.
- DECISIONS: (z) um die offene Kopfzeilen-Entscheidung ergänzt, neu sind (aa) bis (ad). (ad) hält den K3-Stand fest:
  Branch, nicht gemergt, keine Migration, T16 offen. Schon vorhandene Punkte aus (a) bis (y) wurden nicht doppelt
  aufgenommen.
- UI-Designsprache: §2.5, §4.2 und §8.7 sind ergänzt. `DESIGN.md` bleibt unberührt.
- Spec: neuer Block „Stand der Umsetzung“ mit den Fußnoten F1–F25 aus Plan §9. AK38 hat neuen Wortlaut. AK44 steht als
  offen und nicht erfüllt, mit Messwerten und den Optionen. Neu ist RR14 (Lücke zwischen Read und REMOVE).
- Feature-Ideen A18 und die `CLAUDE.md`-Zeile „Wunsch-Emotes“ nennen jetzt K3 auf dem Branch. `Operations.md` bleibt
  unberührt.
- Nach T16 und nach der Kopfzeilen-Entscheidung nachzuziehen: DECISIONS (z)/(ad), Spec AK44/AK45, die Statuszeilen.

## Gates am Ende

Selbst gefahren, im Vordergrund, am Stand nach `5b761297`, 2026-11-19 gegen 01:00–01:15 Uhr. Vor dem E2E-Lauf war
`:5151` frei (`ss -ltn`).

| Gate | Ergebnis | Exit |
|---|---|---|
| `dotnet test EmotePurge.slnx` | Worker 595/595, Api 996/996, Infrastructure 3820 passed + 2 skipped (die beiden Performance-Gates, wie im Bestand) | 0 |
| `npm --prefix web test -- --watch=false` | 231 Dateien, 5680 Tests passed | 0 |
| `npm --prefix web run lint` | sauber, `check-color-tokens` sauber | 0 |
| `npm --prefix web run format:check` | sauber | 0 |
| `npm --prefix web run e2e` | **409 passed, 1 skipped** (der `test.fixme` zur Kopfzeile, Betreiberfrage 1), 3,1 min, ohne Flake | 0 |
| `node scripts/coverage-local.mjs` | **91,2 %** über 60 gemessene Dateien (Zeilen 7590/8208, Zweige 3883/4374) | 1 |

- Zu Exit 1 bei der Coverage: Das Skript meldet „verdict incomplete“, weil drei Dateien ohne Messdaten sind:
  `audit.model.ts`, `channel.model.ts` und `wish.model.ts`. Alle drei enthalten nur Typen und keinen Laufzeitcode. Die
  80-%-Schwelle ist erreicht.
- Dateien, die die Zahl drücken: `channel-settings-page.ts` (0/13), `my-wishes-page.ts` (0/59, ohne Spec, so sieht
  es Plan §5 vor) und `app-shell.ts` (0/42, Shell-Vorlage, laut Plan §5 ohne Spec), außerdem
  `wish-removal-confirm-dialog.ts` (10 %, Darstellung) und `app.routes.ts`. Bei allen fünf deckt E2E das Verhalten.
  Sonar misst zeilengenau, die Shell- und Routen-Zeilen können dort deshalb anders zählen.
- Nachgezogen habe ich `wish-operation.ts`: Die Datei stand bei 26 % und enthält reine Abbildungslogik mit 29 Zweigen.
  Nach Regel 12 hat sie in `5b761297` einen Spec mit 20 Fällen bekommen und liegt jetzt bei 100 %. Der Spec ist kein
  Test nur für die Zahl.
- Nicht gefahren, weil es nicht im Auftrag stand und K3 heute Nacht kein Backend berührt hat: aus Plan §8.1 der Build mit
  `dotnet build --no-incremental` (Prüfung auf Warnungen) und `dotnet format --verify-no-changes`.
- Den Audit-Harness hat T14 komplett gefahren (Zahlen oben bei T14), ich habe ihn nicht noch einmal laufen lassen.
- Beobachtung aus T11: Der `ng build` warnt beim Initial-Bundle-Budget (512,57 kB gegen 500 kB). Gegen die Basis habe ich
  das nicht verglichen. Ein Gate ist es nicht.

### Basis-Vergleich der übrigen roten Audit-Fälle (nicht bewiesen)

Ein Subagent sollte `admin-logs*` und `tags-page*`/`tag-*` auf dem Stand vor K3 fahren (Basis `3eb8024c` = `ad830002^`).
`git worktree add` wurde von der Berechtigungsregel abgelehnt. Ich habe das nicht umgangen, auch nicht über `git archive`.
Lesend spricht alles dafür, dass die Fälle Bestand sind, bewiesen ist es nicht:
- K3 berührt `features/admin` nicht. `tags-page.ts` ändert sich nur in 5 Zeilen (`UNSHOWN_RUN_KINDS` um `wish`, ohne
  Layout).
- Der neue Tab „Wünsche“ erscheint nur bei `wishesEnabled`. Die Kanal-Mocks im Harness setzen das nur in den
  Wunsch-Szenarien (`channelWorkspace(page, { wishesEnabled: true })`), Vorgabe ist `false`.
- Der Kopfzeilen-Link erscheint nur, wenn `summary` verfügbar ist. Die Harness-Vorgabe ist `disabled`.

Morgen zum Nachholen, auf der Devbox:
```
git worktree add --detach ../ep-base-3eb8024c 3eb8024c
ln -s "$PWD/web/node_modules" ../ep-base-3eb8024c/web/node_modules
npm --prefix ../ep-base-3eb8024c/web exec -- playwright test --config=../ep-base-3eb8024c/web/playwright.audit.config.ts -g "admin-logs|tags-page|tag-"
git worktree remove --force ../ep-base-3eb8024c
```

## Übergaben (Fortsetzung von handoff-485.md)

## T10 (f81c87c1)
- **Abschnitt:** `features/channel-workspace/wish-settings-section.ts` (`WishSettingsSection`, Selektor `app-wish-settings-section`, Input `channelName`). Exportiert `wishSettingsBody(duration: string, lead: string): WishSettingsUpdate | null`.
- **Einbindung:** `ChannelSettingsPage` hat jetzt das Signal `wishesEnabled` (aus `getPermissions`, bei Fehler false) und zeigt den Abschnitt vor `app-backfill-section`.
- **Ids** (für T14): `#wish-settings-duration`, `#wish-settings-lead`, Hinweise `…-hint`, Fehler `…-error`, Nur-Lesen-Satz `#wish-settings-read-only`, Überschrift `#wish-settings-heading`. Schutzschwelle nie gezeigt oder gesendet.
- **Live:** `liveReload` auf `wishChanged` (500 ms Entprellung); bei ungespeicherten Änderungen Hinweis mit „Neu laden“ statt Neuladen.
- **i18n neu (de+en):** `wishes.settings.{title,intro,botActive,readOnly,duration.{label,hint},lead.{label,hint},leadInvalid.{empty,not_integer,out_of_range},save,saved,changed,reload,loadFailed,retry}`. Wiederverwendet: `wishes.dialog.durationInvalid.*`, `errors.api.wish_settings_*`.
- **Erweiterung:** K5 ergänzt Schwelle und Bot-Bedienung; `canEdit` und 403-Zustand tragen das. DECISIONS (y).

## T11 (6a5d59ad, 78a03926)
- **Seite:** `features/my-wishes/my-wishes-page.ts`, `MyWishesPage`, keine Inputs. Route `my-wishes` in `app.routes.ts` (lazy, `authGuard`, neben `my-votings`). Lädt `WishService.listMine()` beim Betreten, alle `MY_WISHES_RELOAD_MS` (5 min) bei sichtbarem Fenster und beim Sichtbarwerden; Fehler lässt die letzte Liste stehen (Banner + „Erneut laden“). 404 `wishes_disabled` → `navigateByUrl('/')`.
- **Zeilen:** Stretched Link (`app-card-link` auf dem Alias) nach `['/channels', channelName, 'wishes']` + `?wish=<id>`. Abschnitte `section[aria-labelledby="my-wishes-action-title"|"my-wishes-probation-title"]`.
- **`features/wishes/wish-labels.ts`** (rein): `wishStatusKey`, `wishStatusTone`, `wishReasonKey`, `wishRemainingPart(wish, now)`, `TextPart`, `KNOWN_STATUSES`, `EXPLAINED_REASONS` — einzige Quelle für diese Abbildungen; `WishRow` und `MyWishesPage` nutzen sie, jede weitere Ansicht ebenso.
- **i18n neu (de+en):** `myWishes.{title, actionNeeded, probation, empty, emptyHint, loadFailed, retry}`. Wiederverwendet: `wishes.status.*`, `wishes.reason.*`, `wishes.row.{probation, remaining.*}`, `nav.overview`.
- **Für T12:** Der Kopfzeilen-Link braucht eigene Schlüssel (`shell.*`). Die Seite selbst zeigt keine Zahl.
- **Für T14:** `/api/wishes/mine` mocken; Selektoren über die Abschnitte und den Alias-Link.

## T12 (00cacef8)
- **Dienst:** `core/wishes/wish-summary.service.ts`, `WishSummaryService` (`@Service`, root):
  - `actionCount: Signal<number|null>`, null heißt unbekannt, abgemeldet oder aus.
  - `available: Signal<boolean>`.
  - `refresh()` ist entprellt und tut nichts ohne Nutzer oder nach `wishes_disabled`.
  - Konstanten `WISH_SUMMARY_POLL_MS` (5 min), `WISH_SUMMARY_REFRESH_DEBOUNCE_MS` (500).
  - Fragt nur mit eingeloggtem Nutzer. 404 `wishes_disabled` beendet die Abfragen für die Seitenlebensdauer.
- **Kopfzeile (`app-shell.ts`):** `a[href="/my-wishes"]` nach „Abstimmungen“, nur solange `available`.
  - Zahl > 0: Wort `hidden sm:inline` plus Zahl als Chip, `aria-label` = `shell.wishesCount.{one,other}` (`count`).
  - Zahl 0: nur das Wort, ab `sm`.
  - Keine Ansage.
- **Konto-Menü:** `a[href="/my-wishes"]` (`shell.wishes`), `sm:hidden`, nur solange `available`, vor Admin, schließt das
  Panel. Jede Spec, die `AccountMenu` mit Nutzer und `HttpTestingController.verify()` rendert, braucht den Stub
  `{ provide: WishSummaryService, useValue: { available: signal(false) } }`.
- **Kanal-Seite:** private `reloadAfterChange()` = `reloadAll()` + `wishSummary.refresh()`. Die Page-Spec stubbt
  `{ refresh: vi.fn() }`.
- **i18n (de+en):** `shell.wishes`, `shell.wishesCount.{one,other}`.
- **E2E:** `mockAuthMe(page, user)` registriert `mockWishSummary(page, 'disabled')`. `mockWishSummary(page, n | 'disabled')`
  später registriert überschreibt das. `isWishSummaryNotFound(message)` dient den Konsolen-Wächtern.
- **Audit:** Szenarien `shell-wishes-count` (ohne Badge) und `shell-wishes-count-badge` (Worker stale, Zahl 12).
  `assertHeaderFits(page, slug, {badge})` schreibt `.audit-out/fit/*.json` und scheitert weich. Bekannt rot:
  `shell-wishes-count-badge @ mobile [de]`.
- **DECISIONS:** (z) mit Stufe und Messwerten.

## T13 (10188e5a)
- `AuditAction` und `ACTION_KEYS` kennen 34 Aktionen. Neu sind `wish.{create,adopt,extend,keep,remove,close,retry,readd,settings,due,update}`,
  alle kanalbezogen; `CHANNEL_SCOPED_ACTIONS` hat 31 Einträge, `CHANNELLESS_ACTIONS` 3. Admin-Log und Kanal-Aktivität leiten
  ihre Filter daraus ab und brauchen keine weitere Änderung.
- i18n: `audit.actions.wish{Create,Adopt,Extend,Keep,Remove,Close,Retry,ReAdd,Settings,Due,Update}`.
- AK43 belegt (92/92/92). DECISIONS nicht berührt.

## T14 (a5551a6e, c95dfa39; Fixes 11f3c92a, 57660434, 9a917266)
- **Mocks (`web/e2e/support/mocks.ts`):**
  - `mockWishBackend(page, channel, {open, running, ended, slots, settings})` gibt `MockWishBackend` zurück. Es hat die
    Listen `open`, `running`, `ended`, dazu `details` (Map Wunsch|Status), `usage`, `slots`, `settings` und `calls`.
    `of(kind, id?)` filtert die Aufrufe, `find(id)` sucht einen Wunsch. `gate(call)` hält Antworten zurück, Listen
    liefern dann den Stand beim Eintreffen. `onCommand(call, backend)` überschreibt eine Antwort, `status: 0` bricht ab.
  - Standardverhalten: `create` legt einen `approved`-Wunsch in open an, `adopt` einen `active`-Wunsch in running,
    `keep`/`close`/`complete-removal` verschieben nach ended.
  - `mockWishSummaryLive(page, n|'disabled')` zählt die Aufrufe, Wert per `set()`. `mockWishMine(page, items|status)`.
  - `mockAuthoritativeEmoteSets(page, channel, activeId)` prüft `refresh`.
  - `mockSevenTvEmoteReads(page, {ulid:{name, animated?}}, {gate})`, nicht zusammen mit `mockSevenTvGql` verwenden.
  - `mockSevenTvSetBackend` hat jetzt zusätzlich `rejectRemove` (429).
  - Helfer: `wishInstant(days)`, `wishUlid(suffix)`, `buildWish`, `buildWishSlot`, `buildWishUsage`.
- **Audit:** Szenarien `wishes-page`, `wishes-page-no-images` (mit `reducedMotion`, nur `_static`), `my-wishes`,
  `channel-settings-wishes`, `channel-settings-wishes-invalid`, `wishes-entry-dialog`, `wishes-adopt-dialog`. Feld
  `reducedMotion`; Hilfen `wishAuditLists()`, `wishAuditUsage()`, `assertDialogInsideViewport(page)`.
