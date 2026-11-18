# Nachtlauf 2026-11-17: Plan-483 (Wunsch-Emotes, K1 Backend-Kern)

Branch `nacht/483-wish-core`, Basis `ed7e5d47`. Plan: `docs/plans/Plan-483-Wunsch-Emotes-Backend-Kern.md` (Revision 2).

## Was ist passiert

**Erledigt:** T1 bis T11 aus Plan-483, in Planreihenfolge, als 14 Commits (`a9cabd5f..4283b510`) plus dieser Notiz.
Jeder Task lief als eigener Subagent mit dem Modell aus dem Auftrag, danach ein eigener Task-Review. Bei T4, T5 und T8
fand der Review je ein Important, das in einer Fix-Runde behoben und per Re-Review bestätigt wurde. Alles andere waren
Minor-Befunde, unten geparkt. Die Gates aus T13 sind gelaufen (ohne PR-Teil), alle im Vordergrund und am Stand
`4283b510`:

| Gate | Ergebnis |
|---|---|
| `dotnet build EmotePurge.slnx --no-incremental` | 0 Warnungen, 0 Fehler |
| `dotnet format EmotePurge.slnx --verify-no-changes` | Exit 0, sauber |
| `npm --prefix web run lint` / `run format:check` | sauber / sauber |
| `dotnet test EmotePurge.slnx` | **grün**: Worker 589/589, Api 972/972, Infrastructure 3606 bestanden + 2 übersprungen (die zwei bestehenden Performance-Gates), 0 Fehler |
| `npm --prefix web test -- --watch=false` | **grün**: 5082/5082 Tests in 208 Dateien |
| `node scripts/coverage-local.mjs` | **94,5 %** über 33 gemessene geänderte Dateien (6143/6423 Zeilen, 1377/1534 Zweige), Schwelle 80 %. **Exit-Code 1**, weil zwei Dateien keine Coverage-Daten haben: `ApiErrorCodes.cs` (nur `const`-Strings) und `IWishReconcileService.cs` (Interface). Beide enthalten keinen ausführbaren Code, das ist also kein Befund. Schwächste Dateien: `ChannelWishSettings.cs` 66,7 %, `WishSettingsAuthorizationFilter.cs` 76,7 %, `WishStatus.cs` 77,8 %. Sonar misst zeilengenau; bei den beiden Entities können die nicht gedeckten Zeilen Property-Initialisierer sein. |

**Nicht erledigt:**
- **T12 (Live-Verifikation):** nachts nicht möglich. Liste unten.
- **Codex-Review, Push, PR, Deploy, Prod-Migration:** gehören dem Betreiber.
- **Abschließender Claude-Gesamtreview des Branches:** bewusst ausgelassen. Jeder Task hatte seinen eigenen Review
  (T4, T5, T7, T8, T9 auf opus), und der Plan sieht als Branch-Review `/codex:review --model gpt-6.1-sol` vor. Ein
  Gesamtreview mit Fix-Welle hätte nachts neuen Code nach den grünen Gates erzeugt.

**Offen, bitte morgens entscheiden:** die zwei T9-Betreiberfragen (Kollisionsregel beim Zusammenführen; E14 (c) auch für
beendete Wünsche) und die T7-Spec-Frage, ob `kept` den Slot halten soll. Details unten unter „Annahmen und Strittiges“.
Für den PR-Text (Plan 6.7) liegen bereit: zwölf Codes plus drei wiederverwendete (T10), Befund 8 `recheck`, Befund 9,
Kosten der kalten Rollenprüfung (T6: ein Token-Lookup plus ein paginierter Helix-Aufruf „moderated channels“ je Nutzer
und TTL, kein 7TV-Aufruf; Cache ist `ModeratedChannelsProvider`, nicht `ModRoleCache`), F1/AK10-Lesart, die vier
Spalten über die Spec hinaus, und der Satz „migration `AddWishes` must be applied by hand before the images“.

## Fortschritt (Ledger)

| Task | Commit(s) | Tests | Abweichungen / offen |
|---|---|---|---|
| T1 | `a9cabd5f` | Build 0 Warnungen; T1-Tests 33/33 (Matrix 97.344 Kombinationen); Subagent: Worker 589, Api 845, Infra 3247 (+2 skip) grün | Grenzkonstanten in `WishLimits` (in `WishStatus.cs`); `WishOptions.Validate()` leer (nur `Enabled`). Review: Spec ✅, Qualität ok, 6 × Minor (geparkt, s. u.) |
| T2 | `3d409e3f` | Build 0 Warnungen; `WishSchemaTests` 20/20 (selbst nachgefahren); Subagent: Infra 3267 grün (+2 skip) | Migration `20261117222139_AddWishes`: 2 × CreateTable, 6 × CreateIndex, Down 2 × DropTable (gegengelesen); „nichts ausstehend“ im Fixture geprüft (`HasPendingModelChanges` false), nicht gegen :5432. Snapshot-`ProductVersion` 10.0.12 → 10.0.14 (installiertes dotnet-ef, harmlos; der Subagent-Bericht nannte fälschlich 10.0.11). Review: Spec ✅, Qualität ok, 3 × Minor (kein Test für den partiellen `EndedAtUtc`-Index, vom Plan nicht verlangt) |
| T3 | `6b00a11a` | 43 neue Unit-Tests (Matcher 22, Schedule 8, UsageWindow 13), Matcher selbst nachgefahren 22/22; Subagent: Infra 3310 grün (+2 skip) | Erster Anlauf brach an Bash-Ablehnungen ab (Ketten mit `cd … &&` scheitern im Nachtmodus); fortgesetzt mit Einzelbefehlen. Zeitregeln nehmen „jetzt“ als Parameter, kein `FakeTimeProvider` nötig. Review: Spec ✅, Qualität ok, 5 × Minor (u. a. `removed_externally`-Tests prüfen nur `IsMatch`, nicht den Grund; `ApprovedAtUtc == null` ungetestet; drei Testklassen in einer Datei) — geparkt |
| T4 | `9fd3be63` | `WishServiceTests` + `WishRegistrationTests` 67/67 selbst nachgefahren; Subagent: Infra 3377, Worker 589, Api 845 grün | „Api/Worker starten mit und ohne Schalter“ über einen DI-Graph-Test von `AddEmotePurgeInfrastructure` (true/false/fehlend) belegt, nicht über echte Prozesse. Der Index-Gürtel `(ChannelId, SevenTvEmoteId)` ist wegen der Kanalsperre praktisch nicht erreichbar, nur der Slot-Index per Direkt-Insert getestet. Review (opus): Spec ✅, 1 × Important (Index-Fallback nie durchlaufen) → Fix-Runde 1 läuft; Rulings zu M1 (Sperre statt Zustands-Prädikat, in DECISIONS), M3 (`wish.settings`-Audit trägt die drei Werte, Abweichung von Spec l.840, in DECISIONS), M2 (Alias-Länge in Codepoints; 7TV-Blockliste bewusst nicht portiert). M4 → T7-Brief. Fix-Runde 1/5 `1d97b52e`: Wettlauf-Tests am Index für Anlegen und `adopt` (blockieren nachweislich am Schlüssel), Infra 3380 grün; Re-Review: alle 5 Punkte erledigt. **Abgeschlossen** (`9fd3be63..1d97b52e`) |
| T5 | `37a74369` | Wish-Filter 281/281 selbst nachgefahren; Subagent: Infra 3513 grün (+2 skip), Matrix 13 Zustände × 8 Befehle | Zusätze über den Plan hinaus (DECISIONS (j)): `WishAction.Edit` (PATCH fragt `WishTransitions`), neue Audit-Aktion `wish.update` für PATCH (K3 braucht dafür eine Übersetzung), Ausgang `ModeInvalid` bei `re-add` (T10 → bestehender Code für ungültige Eingaben); `re-add` setzt `ExtensionCount` und Bot-Eigentum zurück; `wish.extend` protokolliert die Verlängerungslänge als `durationDays`. Review (opus): Spec ✅, 1 × Important (`extend` auf `presented` ohne Ablauf erzeugte `active` ohne Start). Fix-Runde 1/5 `fcb76518`: Bedingung `HasExpiry` in `WishTransitions` (K2 muss sie mitgeben, DECISIONS (c)); Wish-Tests 215/215, Infra 3516 grün; Re-Review: erledigt. **Abgeschlossen** (`37a74369..fcb76518`) |
| T6 | `50489f7e` | Wish-Filter 301/301 selbst nachgefahren; Subagent: Worker 589, Api 845, Infra 3533 grün | **Korrektur an Plan-Befund 13:** Die 5-Minuten-Zusage trägt `ModeratedChannelsProvider` (`modlist:{userId}`, TTL `Auth:ModCheckCacheTtlMinutes`, Default 10), nicht `ModRoleCache`. Kalte Rollenprüfung: ein Token-Lookup plus ein paginierter Helix-Aufruf „moderated channels“ je Nutzer und TTL, kein 7TV-Aufruf (für den PR-Text, DECISIONS (k)). `usage` ist null ohne Start, `rank`/`rankOf` null ohne aktiven Slot (T10/K3 beachten). `IMyChannelsService`-Freiheit per Konstruktor-Reflexion belegt, weil es keinen Injektionspunkt gibt. Übersicht ohne Paginierung. Review: Spec ✅, Qualität ok, 5 × Minor (zwei gleichzeitig aktive Aliase nicht getestet; flache JSON-Form von `WishDetailDto` → T10). **Abgeschlossen** |
| T7 | `81fb9dcc` | Wish-Filter 343/343 selbst nachgefahren; Subagent: Infra 3575 grün (+2 skip), 42 neue Abgleich-Tests; Gegenprobe durch absichtliches Brechen der Startregel | Eigene Rulings (DECISIONS (l)): Ein `adopt`-Start gilt als vom Slot stammend, wenn `StartsAtUtc == SlotAddedAtUtc` oder, bei leerer Generation, `== SlotConfirmedAtUtc` (keine neue Spalte). Null-Generation zählt immer als Bestätigung. Ein `approved`-Wunsch gilt als unverknüpft, egal was in `SlotId` steht. Ein Rückkehrer behält `LinkOrigin`. Die Writer-Tests stellen die Abgleich-Uhr von der Wanduhr, weil `EmoteService` `DateTime.UtcNow` stempelt (laut Review nicht flaky, mindestens 59 min Abstand zu jeder Grenze). Review (opus): Spec ✅, Qualität ok, 5 × Minor (s. „Strittiges“). **Abgeschlossen** |
| T8 | `171730e6` | Subagent: Infra 3584 grün (+2 skip), 10 neue Hook-Tests; selbst nachgefahren: Worker 589/589, Api 845/845 | `IWishReconcileTrigger` als Wrapper; Trigger ist optionaler letzter Konstruktor-Parameter von `SevenTvSyncService` und `EmoteService`, damit keine bestehende Testkonstruktion bricht. Review (opus): Produktion injiziert ihn, aber kein Test belegte das (Important). Fix-Runde 1/5 `2cff9a94`: Verdrahtungstest gegen den echten DI-Graph, fremde Cancellation geschluckt, zwei Trefferkanäle, Deadlock-Retry → genau ein Abgleich, Trigger-Tests nach `Unit/`; Infra 3588 grün; Re-Review: alle 5 erledigt. **Abgeschlossen** (`171730e6..2cff9a94`) |
| T9 | `676fdc44` | Wish/Retention/AccountDeletion/ChannelIdentity-Filter 522/522 selbst nachgefahren; Subagent: Infra 3606 grün (+2 skip), Worker 589 grün | Entscheidungen des Implementers: Beendete Wünsche werden vor dem Leeren der Einlöser-Daten gelöscht (gleiche Zählung im Trockenlauf); E14 (a)/(b) nur für nicht beendete Wünsche, (c) als harte Kappe für alle; `merged` zählt als Entscheidung (`DecisionVersion` +1). Bedenken: Zusammenführungsregel „jüngerer endet“ kann einen verknüpften `active` verwerfen; seltener 40P01 zwischen Retention und Zusammenführung möglich. Review (opus): Spec ✅, Qualität ok, 5 × Minor (zwei davon Betreiberfragen, s. „Strittiges“). **Abgeschlossen** |
| T10 | `fa2251d3` | Selbst nachgefahren: Api 972/972, Vitest 5082/5082 (208 Dateien); Subagent: `dotnet test EmotePurge.slnx` grün (Worker 589, Api 972, Infra 3606 +2 skip), Lint sauber | Zwölf Codes in `ApiErrorCodes.cs`, `api-error.ts`, `de.json`, `en.json`. `EmoteIdInvalid` → `emote_ids_invalid`, `StartsAtInvalid`/`ModeInvalid` → `invalid_request_body` (kein 13. Code). Ein Körper, der gar kein JSON ist, bekommt schon global 400 vor dem Handler; „Schalter vor Körper“ gilt also nur für fehlende oder leere Körper, wie bei jeder anderen Route. `wishesEnabled` nicht im TS-Modell `ChannelPermissions`, das macht K3. Review: Spec ✅, Qualität ok, 5 × Minor (s. „Strittiges“). **Abgeschlossen** |


## Annahmen und Strittiges

- **Modellwahl nach Auftrag, nicht nach Plan-Tabelle.** Der Plan schlägt T3 opus, T5 sonnet, T8 opus, T9 sonnet vor; der
  Auftrag sagt sonnet für T1–T4, T6, T8, T10, T11 und opus für T5, T7, T9. Der Auftrag gilt.
- **Superpowers-Hilfsskripte gesperrt.** `scripts/sdd-workspace`/`task-brief`/`review-package` werden im Nachtmodus von der
  Berechtigung abgelehnt. Ersatz: Diese Datei ist das Ledger; Briefs sind Zeilenbereiche des Plans, Reports liegen im
  Scratchpad der Sitzung.

- **T1, `close` aus `presented`:** erlaubt für jeden Grund, solange kein Slot verknüpft ist. Grund: Spec Zeile 186, die
  allgemeine Zeile `presented` → `closed`. Zeile 174 nennt nur den Grund `unconfirmed`, und die Plan-Formulierung „close
  hängt am Grund“ legt die engere Lesart nahe. Wer die enge Lesart will, ändert eine Zeile in `WishTransitions`.

- **T1-Review, geparkt (Minor):** (a) `bot_confirm` (`unconfirmed` → `active`) kennt keinen `kept`-Zweig für
  `Permanent`. Ein im Bot-Modus per `re-add` dauerhaft freigegebener Wunsch würde damit `active` mit Frist. Das ist K4s
  Aufrufer: Beim Verdrahten von K4 dort `AddActiveOrKept` nehmen. (b) `WishAction` liegt in `EmotePurge.Core.Services`
  (`WishTransitions.cs`), nicht bei den Entities-Konstanten. (c) Die Matrix-Erwartung im Test spiegelt die Implementierung
  Zeile für Zeile. Der Reviewer hat jede Zeile gegen Spec 5 gegengelesen und keine Abweichung gefunden. (d) Die
  `**Betrifft:**`-Zeile in DECISIONS nennt `WishOptionsTests.cs` nicht. T11 vervollständigt sie.

- **Entscheidung: zwölf Codes, keiner dazu.** `WishService` kennt zwei Eingabefehler ohne eigenen Code der Spec:
  ungültige Emote-ID und ungültiges `startsAtUtc` bei `adopt`. T10 bildet sie auf die bestehenden Validierungscodes ab
  (Emote-ID-Validierung bzw. allgemeiner Code für ungültige Eingaben), statt einen dreizehnten Code einzuführen. Grund: Die
  Spec nennt genau zwölf Codes, und Regel 7 macht jeden neuen Code teuer. Ist das falsch, kostet ein eigener Code einen
  kleinen Folge-Commit samt Locales.
- **T4-Annahme Alias-Regel:** Ein Alias darf keine Leerzeichen und keine Steuerzeichen enthalten und hat höchstens 100
  Zeichen. Das ist die Lesart des Implementers zu 7TV-Aliasen. Der Review prüft sie gegen die bestehende Alias-Validierung.

- **T5-Review, für K4 geparkt (Minor):** (a) `re-add` lässt `SlotId` stehen, wie der Plan es will (Befund 5). `close` aus
  `presented` prüft „kein Slot verknüpft“ als `SlotId == null`. Ein per `re-add` neu freigegebener Wunsch, der in K4 nach
  `presented(unconfirmed)` fällt, ließe sich damit nicht schließen. K4 muss „verknüpft in dieser Runde“ sauber
  unterscheiden. (b) `re-add` leert `BotOwned`, aber nicht `BotWriteAtUtc` und die Versuchszähler. Ein vom Bot
  geschriebener, entfernter und von Hand wieder freigegebener Wunsch bliebe für den Abgleich gesperrt (Ausnahme
  `BotWriteAtUtc`). K4 muss den Reset für `re-add` und `retry` festlegen.

- **T7-Review, geparkt, bitte morgens ansehen:**
  1. *Seltene Lücke (Minor, echt):* `adopt` auf einen undatierten Slot, dann wird der Archiv-Hook verpasst, dann stellt
     der Bericht den Slot mit **vorläufigem** Datum wieder her. Der Abgleich hält die vorläufige Generation fest,
     verschiebt den Start aber nicht. Bei der späteren REST-Bestätigung folgt der Platzhalter-Start dann nie mehr. Auch der
     Wiedereintritt bleibt unbemerkt, obwohl `AddedAt > SlotConfirmedAtUtc` ihn beweist. Billige Abhilfe nach Wahl:
     Platzhalter-Start auch auf das vorläufige Datum schieben, oder `AddedAt > SlotConfirmedAtUtc` bei leerer Generation
     als Generationswechsel werten. Nicht umgesetzt, weil Minor und selten.
  2. *Spec-Frage für den Betreiber:* Ein `kept`-Wunsch hält seinen Slot nicht (der Index bindet nur `active` und
     `presented`, so steht es in der Spec). Konstruierter Fall: A wird `removed_externally`, das Emote kommt unter anderem
     Alias zurück, wird als B übernommen, 7TV benennt es zurück auf A's Alias, B wird behalten. Im nächsten Lauf kehrt A
     dann als `active` mit alter Frist auf den behaltenen Eintrag zurück. Zu entscheiden ist, ob `kept` den Slot halten
     soll.
  3. *Für K4:* Die K4-Ausnahme („`active` ohne Slot bekommt ihn“) setzt auch `StartsAtAuthoritative` nach dem Slot. Bis
     zum nächsten REST-Sync (~60 s) stellt K2 einen solchen Wunsch deshalb nicht fällig. Ein Satz in DECISIONS bei K4.
  4. Drei Zweige ohne Testfall: `EndedAtUtc = jetzt`, wenn `ArchivedAt` fehlt oder vor `SlotConfirmedAtUtc` liegt;
     undatierter Slot mit bekannter Generation; ein anderer Fehler als ein Index-Treffer rollt den ganzen Lauf zurück.
     Dazu die Member-Reihenfolge nach Regel 19 in `WishReconcileService` (Kosmetik).

- **T9, Betreiberfrage 1 — Kollision beim Zusammenführen:** Die Plan-Regel (Befund 15) lässt den jüngeren laufenden
  Wunsch als `merged` enden. Ist der jüngere ein per `adopt` verknüpfter `active` und der ältere ein unverknüpfter
  `approved`, überlebt der `approved`. Weil sein Slot vor der Freigabe liegt, verknüpft er sich nie von selbst, und das
  Emote ist nicht mehr nachverfolgt. Er steht dann aber sichtbar in der Sicht „offen“. War der jüngere automatisch
  verknüpft, verknüpft sich der ältere beim nächsten Abgleich, und nur Laufzeit und Verlängerungen des jüngeren gehen
  verloren. Der Fall ist selten: Er braucht zwei Kanalzeilen desselben Twitch-Kanals mit laufendem Wunsch für dasselbe
  Emote. Vorschlag des Reviewers: Gewinner ist der Wunsch mit Spur in 7TV (`SlotId` gesetzt, ab K4 auch
  `BotWriteAtUtc`), sonst gilt die Altersregel. **Nicht umgesetzt**, weil es Plan-Befund 15 überstimmt. Bitte im PR
  entscheiden.
- **T9, Betreiberfrage 2 — E14 (c) auch für beendete Wünsche:** Ein nie aktiver Wunsch, über 120 Tage alt und gerade
  beendet, verliert die Einlöser-Daten sofort, nicht erst 30 Tage nach `EndedAtUtc`. Spec 6.5 wendet E14 nur auf
  Wünsche ohne Endzustand an. Die Abweichung ist datenschutzfreundlicher und in DECISIONS (n) benannt. Bestätigen oder
  zurückdrehen.
- **T9, kleinere Punkte:** Bulk-Retention und Kontolöschung sperren Wunsch-Zeilen ohne feste Reihenfolge. Ein 40P01 ist
  theoretisch möglich und kostet einen Retention-Durchlauf. Das entspricht dem Bestand, DECISIONS sagt aber nicht, dass
  ein solcher Fehler den Rest des Durchlaufs abbricht. `ChannelIdentityService` nimmt `DateTime.UtcNow` statt
  `TimeProvider` (Bestand). Eine Zusammenführung sendet kein `wish.changed` und stößt keinen Abgleich an; verschobene
  `approved`-Wünsche verknüpfen sich erst beim nächsten Sync des Überlebenden.

- **T10-Review, geparkt:** (a) Der wiederverwendete Text von `emote_ids_invalid` („gewähltes Emote gehört zu einem anderen
  Kanal oder ist nicht mehr im Set“) passt nicht zu einer formal ungültigen Emote-ID beim Handeintrag. K3 braucht
  entweder einen eigenen Text oder doch einen dreizehnten Code. (b) Deutsche Texte: `wish_state_conflict` hat ein
  Komma-Splice, `wish_settings_restricted` liest sich holprig. Das ist bei K3 bzw. beim Review zu glätten. (c) Kein Test
  belegt, dass `/wishes/settings` nie auf `{id:long}` fällt. (d) `wishesEnabled` fehlt noch im TS-Modell
  `ChannelPermissions` (K3).

- **T11:** `docs/Operations.md` schreibt, der Worker-Sync stoße den Abgleich an. Das stimmt, aber auch die Api stößt
  ihn an (Bericht nach `MarkInSetAsync`). Der Schalter muss deshalb wirklich an beiden stehen, was dort auch steht. Die
  CLAUDE.md-Zeile nennt den Branch `nacht/483-wish-core` und muss nach dem Merge angepasst werden.

## Morgens live zu prüfen (T12)

Ablauf nach Plan Abschnitt 5 (l.571–614); nichts davon lief nachts. Kurzfassung der Schritte:

1. **AK1, Migration gegen eine Kopie der Dev-DB:** `dotnet ef migrations list` zeigt genau
   `20261117222139_AddWishes` als ausstehend, dann `update`, dann `list` leer. Beide Tabellen leer, Schema der
   Bestandstabellen vorher und nachher identisch.
2. **Schalter:** `Wishes__Enabled=false` → `GET …/wishes` antwortet dem Verwalter 404 `wishes_disabled`, und
   `/permissions` zeigt `wishesEnabled`. Danach mit `true` neu starten. Der Schalter muss an Api **und** Worker stehen.
3. **Handeintrag:** per `curl` mit Cookie → 201 `approved`. Auf dem Kanal-Stream genau ein `wish.changed`, auf
   `/api/channels/live-events` keins.
4. **Hinzufügen in 7TV** (Testkonto `olaf_olaf_son`): nach dem Sync `active`, `startsAtUtc` = 7TVs `added_at`. Einmal mit
   EventAPI aus, einmal an; mit EventAPI an zuerst `startsAtAuthoritative = false`, nach dem Vollsync verschoben (AK8).
5. **Umbenennen** (AK9): derselbe Slot, `alias` nachgezogen, Ablauf unverändert.
6. **AK10:** (a) Entfernen und unter gleichem Alias wieder hinzufügen → zurück nach `active` mit alter Frist; dabei
   notieren, ob dieselbe `SlotId` reaktiviert wurde. (b) Beides zwischen zwei Vollsyncs → `active`. (c) Wieder hinzufügen
   unter anderem Alias → bleibt `removed_externally`; danach `adopt` ohne `wishId` → neuer Wunsch.
7. **Übernehmen und Befehle:** `adopt`, `extend`, `keep`, `complete-removal` je einmal. `wish.*`-Audit ohne Alias, Namen
   und Login (AK19). Aktivitäts-Feed ansehen (R6: `wish.*`, und auch das neue `wish.update`, sind dort bis K3
   unübersetzt).
8. Worker-Log über den ganzen Lauf ohne Fehler aus dem Abgleich (Warnung „wish reconcile failed“ darf nicht auftauchen).
9. **Aufräumen:** Set wiederherstellen, Prozesse prüfen und mit SIGTERM beenden, Redis-Container und DB-Kopie entfernen.

Zusätzlich, weil nachts nur über Tests belegt: Api und Worker starten echt mit und ohne `Wishes__Enabled`. T4 hat das
nur über den DI-Graph von `AddEmotePurgeInfrastructure` geprüft.
