# 7TVs Bestenliste als Import-Quelle — Spec

**Datum:** 2026-09-13 · **Status:** Entwurf, wartet auf die adversariale Zweitmeinung (Issue #148) · **Konzept:** [7TV-Bestenliste-als-Import-Quelle-2026-09-13.md](../../designs/7TV-Bestenliste-als-Import-Quelle-2026-09-13.md) (APPROVED 2026-09-13) · **Vorgänger:** [2026-09-09-fremde-kanaele-import-quelle-spec.md](2026-09-09-fremde-kanaele-import-quelle-spec.md) (#147, **läuft auf Prod** seit PR #150 / `84b39d2`) · **Epic:** #118 (messungsneutral)

Diese Spec **zerlegt** den freigegebenen Entwurf. Sie entwirft ihn nicht neu. Ansatz A (Vorrat, faul
gefüllt), die verworfenen Ansätze B und C, die Stufung nach Vorratsfähigkeit statt Quotenrisiko, der
Prozessspeicher statt Redis und die Schiedsentscheidung „zwei Dinge tragen die Sicherheit" stehen
mit Begründung und vier Live-Messungen im Entwurf und werden hier **nicht** wieder aufgerollt. Wer
eine davon umwerfen will, braucht einen Befund, keine Präferenz.

Neu gegenüber dem Entwurf ist nur dies: die sieben Entscheidungen des Betreibers vom 2026-09-13
als verbindliche Festlegungen (Abschnitt 2, E1–E7), neun weitere Festlegungen, die der Entwurf
offen ließ (E8–E16), **sieben im Code nachgeprüfte Fallen** (Abschnitt 3) — fünf aus dem Entwurf,
zwei beim Nachprüfen gefunden —, die Verträge in prüfbarer Form und die Aufgabenreihenfolge.

Gemessene Fakten, auf denen alles Weitere steht (alle live am 2026-09-13, Details im Entwurf):

- `emotes.search` auf `https://7tv.io/v4/gql` zieht `x-ratelimit-search-remaining` bei **jedem**
  Aufruf um 1 — auch mit leerem `query`, bei jeder Sortierung, beim reinen Blättern. Limit 100,
  Fenster ~60 s; bei Überziehung springt `reset` auf ~3583 s.
- **Der Eimer ist über v3 und v4 geteilt.** Protokoll v4 → v3 → v4: 99 → 98 → 97, `reset`
  60/60/59. Der Resync-Pfad `ResolveTwitchUserIdAsync` (`SevenTvSyncService.cs:75`) postet auf
  `v3/gql` und zieht denselben Zähler.
- `perPage` ist bei **250** hart gedeckelt (`Failed to parse "Int": the value is 500, must be less
  than or equal to 250`). Die Kosten hängen nicht an `perPage`; `complexity` ist 7. Eine
  Validierungsablehnung liefert **keine** `x-ratelimit-search`-Header und kostet den Eimer nichts.
- **Kein quotenfreier Zugang:** `EmoteQuery.search` ist die einzige sortierbare Liste im v4-Schema,
  `Emote.ranking(Ranking!)`/`Emote.scores` sind Pro-Emote-Felder, Root-`search.all` hat kein `sort`
  und zieht denselben Eimer, `GET /v3/emotes` → 405 (`allow: POST`), POST → 401.
- 250 Zeilen der Tagesliste geprüft: **0 gelöschte, 0 `nsfw`, 0 `private`, 0 nicht
  `publicListed`**, 5 × `defaultZeroWidth`, 173 animiert. **Ein eigener Sicherheitsfilter ist
  deshalb nicht nötig** — das steht hier, damit niemand später einen baut.
- `totalCount` für `TRENDING_DAILY` war vormittags 705, nachmittags 664. Die Liste bewegt sich über
  Stunden; 1 h Haltbarkeit ist vertretbar.
- Namenskollisionen sind **abgedeckt**, aber nicht von `already-present-filter.ts` (die filtert nach
  7TV-ObjectID, `:7-15`). Der Hinweis sitzt in `buildImportPreview` (`nameCollisions`,
  `import-preview.ts:12`, seit #72), wird im Bestätigungsdialog gezeigt
  (`import-confirm-dialog.ts:198-203`, `:326-327`) und ist in #147 als AK 17 gesichert. Die neue
  Quelle erbt ihn — als Regressionskriterium, nicht als Aufgabe (AK 18).

---

## 1. Auftrag in einem Satz

Ein eingeloggter Nutzer kann 7TVs netzwerkweite Bestenliste — „Trend heute" oder „Top insgesamt",
je bis zu 500 Emotes — als **dritte Option im bestehenden Import-Dialog** ansehen, einzelne Emotes
daraus auswählen und in ein Set übertragen, in dem er 7TV-Rechte hat; der Server ruft 7TV dafür
**höchstens 10-mal pro Stunde** an, egal wie viele Nutzer wie oft klicken.

Der Weg, den das eröffnet: „ich suche irgendwas Gutes" hat heute keinen Einstieg — beide
ausgelieferten Quellen (`SOURCE_OPTIONS`, `import-source-dialog.ts:55-66`) setzen voraus, dass der
Nutzer schon weiß, **wo** die Emotes liegen.

---

## 2. Entscheidungen dieser Spec

E1–E7 hat der Betreiber am 2026-09-13 entschieden; sie werden hier nicht neu aufgerollt. E8–E16
sind Festlegungen, die der Entwurf offen ließ oder nur als Alternative nannte.

| # | Frage | Entscheidung | Begründung |
|---|---|---|---|
| E1 | Gemeinsames Request-Budget für Api **und** Worker (Codex-Empfehlung)? | **Nicht in dieser Runde.** Nur die Bestenliste bekommt ihr hartes Fensterbudget (10 Requests je 60 min). Die gemeinsame Bremse wird ein **eigenes Folge-Issue, fällig nach dem Messfenster** (ab 2026-10-08) | Sie fasst den Resync-Pfad an, also Code, den der Worker ausführt — im Messfenster gesperrt. Und die Risikorichtung ist gemessen umgekehrt: unser Pfad belastet den Eimer im ungünstigsten Fall mit 10 von 100 Freigaben je Minute (der Eimer erneuert sich pro ~60 s, der Minutenmaßstab zählt), eine dauerhaft nicht auflösbare Kanalzeile das Sechsfache unseres Budgets. Die Verschiebung ist durch die Vorab-Kontrolle aus AK 36 abgesichert, die das eigentliche Risiko prüft, ohne den Worker anzufassen. Steht in Abschnitt 15 **und** 16 |
| E2 | Was trägt die Audit-Zeile als Herkunft, wenn es keinen Quellkanal gibt? | **Die Sortierung:** „7TV Trend heute" bzw. „7TV Top insgesamt". Der Bestätigungsdialog zeigt dasselbe | Ehrlicher als ein leeres Feld, und der Dialog hat wieder etwas zu zeigen. Genau deshalb muss `EmoteEndpoints.cs:164` **geändert** werden und nicht nur die Vokabelliste auf `:142` erweitert (F1) |
| E3 | Sortier-Semantik auf einer Bestenliste vs. P5' und Punkt 5 aus `DECISIONS.md` 2026-09-10 | **Beide Verträge werden auf die Beschriftung eingegrenzt.** Sortieren nach Score ist auf der Bestenliste zulässig — die Rangfolge *ist* der Inhalt. Verboten bleibt, die Spalte bloß „Beliebtheit" zu nennen oder sie als kanal-lokal lesbar zu machen | Vertragsänderung → `DECISIONS.md`-Eintrag **im selben Commit** wie die Umsetzung (Regel 3; Aufgabe T6, AK 25). Nachtrag zum Entwurf: `DECISIONS.md:507` schreibt in Punkt 5 die Labels „7TV-Verbreitung (gesamt)/(Trend)" fest, ausgeliefert ist „7TV-Score (gesamt)/(Trend)" — **Punkt 10 desselben Eintrags (`:538-546`) hält diese Umbenennung aber bereits fest.** Der neue Eintrag verweist also nur darauf, dass Punkt 5 in seiner Label-Angabe von Punkt 10 überholt ist; ein veralteter Vertrag ohne Korrektur existiert nicht |
| E4 | Overlay-Emotes (`flags.defaultZeroWidth`) kennzeichnen? | **Nein.** Bekannte Einschränkung, keine Aufgabe (Abschnitt 15) | Gemessen 5 von 250. Die ausgelieferte Fremdkanal-Ansicht kennzeichnet sie auch nicht; Gleichlauf schlägt Verbesserung an geteiltem Code |
| E5 | Seitendecke | **2 Upstream-Seiten à 250 = 500 Zeilen je Sortierung** | Gemessen: 250 ist 7TVs Maximum, Kosten je Seite 1, Schlüsselraum 2 Einträge (je Sortierung einer), je bis zu 2 Upstream-Seiten |
| E6 | Deploy-Verhältnis zu #147 | **#147 läuft auf Prod.** #148 ist ein eigener Deploy oder wird mit einem anderen Arbeitspaket gebündelt (Epic #118: „Deploys bündeln") | Nachschlagbar, keine offene Frage mehr (Entwurf, Offene Frage 6) |
| E7 | Idee C — Beliebtheitszahlen an den **eigenen** Emotes als Löschhilfe | **Kein Issue.** Bleibt eine Notiz im Entwurf | Beantwortet eine andere Frage. Wird in Abschnitt 15 erwähnt, **nicht** in Abschnitt 16 geführt |
| E8 | Wie kommt die Sortierung (E2) auf die Leitung und in die Audit-Zeile? | **Neues, optionales Feld `leaderboardSort` in `SyncImportedRequest`** (`EmoteEndpoints.cs:262`), Allowlist genau `TRENDING_DAILY`/`TOP_ALL_TIME`, ordinal. `SourceChannelName` ist für die neue Vokabel **`null`**. Der Kind/Name-Abgleich `:164` wird zu einer Tabelle je Vokabel (F1). `IEmoteService.MarkImportedAsync` (`IEmoteService.cs:39`) bekommt den Parameter, `EmoteService.cs:129` schreibt ihn in `DetailsJson` | Die Sortierung **kann nicht** in `SourceChannelName` reisen: `EmoteEndpoints.cs:150` validiert jeden gesetzten Namen mit `ChannelNameValidation.IsValid`, und `TRENDING_DAILY` (Großbuchstaben, Unterstrich) fällt durch — 400 **nach** der Mutation, also genau der F6-Fehler aus #147. Das hat der Entwurf nicht gesehen |
| E9 | Form des Audit-Details | **Ein** neues Mitglied `AuditLogDetail.Kinds.ImportedFromLeaderboard`, `Count` = Emote-Zahl, `Text` = der **sprachneutrale Sortiercode** (`TRENDING_DAILY`/`TOP_ALL_TIME`). Das Frontend übersetzt den Code in `renderDetail` (`audit-row.ts:53-68`) über eine kleine Tabelle, statt ihn wie bei `importedFromChannel` roh als `title` einzusetzen. Ein Code außerhalb der Allowlist fällt beim Lesen auf den nackten `EmoteCount`-Zweig | Regel 7: die API liefert Codes, nie fertigen Text. Zwei Kinds (eines je Sortierung) wären ebenso gegangen, hätten aber die Allowlist in den Kind-Namensraum kopiert |
| E10 | Fensterbudget: `ForeignEmoteSetProviderBudget` parametrisieren oder „kleine Schwester"? | **Kleine Schwester:** neue pure Klasse `SevenTvLeaderboardRequestBudget(int maxRequests, TimeSpan window, TimeProvider)`, synchrones `TryCharge(out int usedInWindow)`, **Wartezeit 0** (Ablehnen, nicht Warten). `ForeignEmoteSetProviderBudget` bleibt unangetastet | Die #147-Klasse kombiniert Nebenläufigkeitsslots mit einem *wartenden* Fensterbudget (`:77-91`), und ihre `const`-Werte sind Teil ihres dokumentierten Vertrags und ihrer 8 Tests. Hier wird nichts gewartet und nichts parallel begrenzt; ~20 Zeilen Zeitstempel-Queue doppelt sind billiger als ein Eingriff in ausgelieferten Code mitten im Messfenster |
| E11 | 429-Semantik im Client: duplizieren oder herausziehen? | **Herausziehen.** `FetchPreviewPageAsync` (`SevenTvApiClient.cs:410-466`) wird zu einem gemeinsamen privaten v4-Seitenabruf mit Call-Source-Parameter; `RecordForeignPreviewObservation` (`:467-476`) bekommt die Call-Source als Parameter statt der Konstante. Beleg, dass #147 unverändert ist: die 16 + 4 + 1 bestehenden Client-Tests (`SevenTvApiClientEmoteSetPreviewTests`, `SevenTvApiClientForeignTelemetryTests`, `SevenTvApiClientForeignTelemetryStoreTests`) bleiben **ohne Änderung an den Testdateien** grün, dazu drei Charakterisierungstests, die **vor** dem Umbau entstehen (T0, AK 28/34) | Die Erkennung „HTTP 200 mit `extensions.status: 429`" ist der teuerste Einzelfehler beider Specs. Zwei Kopien davon driften; das bestehende Testnetz ist genau der Grund, warum der Eingriff heute billiger ist als später |
| E12 | Raster-Erweiterung | **Vier additive, optionale Inputs** auf `ForeignEmoteGrid`: `forcedSortMode` (`null` \| `'topAllTime'` \| `'trending'`, Default `null`) sowie `emptyMessageKey`, `truncatedMessageKey`, `scoreHintKey` (Defaults = die heutigen `import.foreignChannel.*`-Schlüssel). Die DOM-id des Sortier-`<select>` wird **instanzeindeutig** (Zähler) | `forcedSortMode` statt `hideSortControl`: `sortMode` steuert vier Dinge (F2). Die drei Schlüssel-Inputs, weil die heutigen Texte kanalgebunden formuliert sind. Die id, weil `:272` hart `'foreign-emote-sort'` ist — auch wenn die beiden Instanzen im Dialog nie gleichzeitig gerendert werden, kostet die Eindeutigkeit nichts und die Falle ist damit weg |
| E13 | Fehlercodes | **Ein** neuer Code `invalid_leaderboard_sort` (400). Die 503-Fälle (7TV nicht erreichbar, 429, Breaker offen, **Budget verweigert**) verwenden den **vorhandenen** `foreign_channel_seventv_unavailable` | Sein Text ist quellneutral („7TV ist gerade nicht erreichbar", `de.json:1097`, `en.json:1097`); ein zweiter Code mit demselben Satz wäre Vokabular ohne Unterschied. Eine Budget-Verweigerung ist `SevenTvUnavailable`, wie bei #147 (`SevenTvEndpoints.cs:60-66`) |
| E14 | Auswahl beim Sortierwechsel | **Sichtbar geleert**, „Weiter" wieder gesperrt, `result()` wieder `null` | Die beiden Listen sind verschiedene Grundmengen; ein Emote kann in beiden stehen, und `ListSelection` hält Schlüssel über Listenwechsel hinweg (`foreign-emote-grid.ts:314-317`). Zustandsübergang → Spec nach Regel 12 |
| E15 | Nachweis, dass `x-ratelimit-search-remaining` bei den Bestenlisten-Stichproben über 90 bleibt | **Aus den Log-Zeilen des Abnahmelaufs, eingegrenzt auf die Stichproben der Bestenliste** — nicht aus einem gespeicherten Minimum im `RateLimitTelemetryStore` und ausdrücklich **keine** Aussage über das Eimer-Minimum aller Verbraucher. Der Client loggt je Upstream-Request `remaining` und `reset` (Information) | Der Store hält nur `LastHeaderSample` (F5). Der Eimer erneuert sich pro ~60 s, verglichen wird also pro Minute, nicht pro Stunde: im ungünstigsten Fall feuert unser Budget alle 10 Freigaben innerhalb einer Minute, das sind 10 von 100 — „10 von 6.000 pro Stunde" war der falsche Maßstab. Das Minimum über alle Verbraucher gehört zur gemeinsamen Bremse (E1, Abschnitt 16); die Vorab-Kontrolle aus AK 36 prüft das eigentliche Risiko unterdessen ohne Code |
| E16 | Per-Nutzer-Policy | Neue Fixed-Window-Policy `SevenTvLeaderboard`, **20 Requests/Minute** je Nutzer, an **drei** Stellen (`RateLimitPolicyNames`, `Program.cs`, `RateLimitingOptions.Validate()`) | Der Endpunkt kostet 7TV nichts (Vorrat), aber 30 KB Antwort; 20/min deckt jeden Sortierwechsel und jedes Neuöffnen. `ForeignEmoteLookup` (10/min) ist nicht wiederverwendbar, weil es einen anderen Pfad bemisst. `Validate()` ist der Fail-Fast: eine nicht validierte Policy mit Kapazität 0 ist laut Kommentar dort (`RateLimitingOptions.cs:61`) „a total outage" |

---

## 3. Sieben Fallen, im Code nachgeprüft am 2026-09-13

Jede davon ist eine **prüfbare Vorgabe**, keine Fußnote. F1–F5 standen im Entwurf und sind hier mit
korrigierten Zeilennummern nachgeprüft; F6 und F7 sind beim Nachprüfen dazugekommen.

### F1 — Die Herkunftskette hat sechs Stationen, und die erste lehnt die Sortierung ab

Der Kommentar auf `EmoteEndpoints.cs:161-163` sagt es im Voraus: „a fourth kind without a source
name would have to change this line, not just add to the vocabulary above." Nachgeprüft ist die
Kette länger:

1. **Vokabelliste** `EmoteEndpoints.cs:142`: `"channel" | "file" | "seventv-channel"` → vierte
   Vokabel **`"seventv-leaderboard"`**.
2. **Namensvalidierung `:150`** — die Station, die der Entwurf übersehen hat: jeder gesetzte
   `SourceChannelName` läuft durch `ChannelNameValidation.IsValid`. Die Sortierung *kann* dort
   nicht reisen (E8). Sie bekommt ein eigenes Feld `leaderboardSort`.
3. **Kind/Name-Abgleich `:164`**: `IsNullOrWhiteSpace(SourceChannelName) != (SourceKind == "file")`
   ist binär „Datei gegen nicht-Datei". Für die vierte Vokabel ist er **falsch**, nicht bloß
   zufällig richtig wie bei #147 (F5.2). Neu ist eine Tabelle je Vokabel:

   | `sourceKind` | `sourceChannelName` | `leaderboardSort` |
   |---|---|---|
   | `channel`, `seventv-channel` | Pflicht | muss `null` sein |
   | `file` | muss `null` sein | muss `null` sein |
   | `seventv-leaderboard` | muss `null` sein | Pflicht, aus der Allowlist |

   Jede Abweichung ist 400 `invalid_source_kind` (bzw. `invalid_leaderboard_sort` für einen Wert
   außerhalb der Allowlist).
4. **Persistenz** `IEmoteService.cs:39` → `EmoteService.cs:107` (`MarkImportedAsync`), `:120`
   normalisiert den Quellnamen, `:129` schreibt `{ emoteCount, sourceChannelName, sourceKind }`.
   Neu: `leaderboardSort` daneben.
5. **Projektion** `AuditLogQueryService.cs:121` (`ProjectDetail`): `:155` kennt drei Vokabeln, der
   Zweig `:182-185` gibt `ImportedFromChannel` nur bei `source is not null`; sonst fällt die Zeile
   auf `:189-192`, den nackten `EmoteCount`-Zweig. **Das ist der Herkunftsverlust, dauerhaft und
   unbemerkt** — Audit-Zeilen sind write-once. Neu: ein Zweig für `seventv-leaderboard` **vor** der
   `source`-Prüfung, der `ImportedFromLeaderboard` mit `Text` = Sortiercode liefert (E9), plus das
   Mitglied in `AuditLogDetail.Kinds` (`IAuditLogQueryService.cs:20-30`, heute fünf).
6. **Frontend-Vokabular:** `ImportOrigin` (`import-source.ts:29-38`) bekommt den vierten Zweig
   `{ kind: 'seventv-leaderboard'; sortBy: LeaderboardSort }`. Die Union wird an **einer** Stelle
   erschöpfend zerlegt: `importOriginSourceChannelName` (`:53-61`, `default`-Arm mit `never`) — der
   vierte Zweig ist dort ein **Compile-Fehler**, bis `return null` ergänzt ist. Das ist die laute
   Stelle. Die **stillen** Stellen: `emote-admin.service.ts:37` (`sourceKind`-Union, Typfehler,
   laut) und `:38` (`SyncImportedBody` braucht `leaderboardSort`), `seven-tv-import.service.ts:303-304`
   (`reportImported` muss `leaderboardSort` senden — sonst 400 **nach** der Mutation, die
   F6-Klasse aus #147), `import-confirm-dialog.ts:290-298` (`originChannelName()` liefert `null`,
   `fileOrigin()` auch → der Dialog nennt **keine** Herkunft; neuer dritter Zweig), und die
   Audit-Anzeige `audit.model.ts:32-33`, `audit-actions.ts:51-57`, `audit-row.ts:53-68` plus beide
   Locales unter `audit.details`.

**Vorgabe:** eine zweite erschöpfende Hilfsfunktion `importOriginLeaderboardSort(origin)` neben
der bestehenden, damit `reportImported` beide Felder über `switch`-mit-`never` bezieht und nie über
`=== 'seventv-leaderboard'`.

### F2 — Das Raster hat ein eigenes Sortier-Control, und `sortMode` steuert vier Dinge

`foreign-emote-grid.ts`: `ForeignEmoteSortMode` mit Pflicht-Default `'none'` (`:41`, `:269`,
P5'/AK 16 als Vertrag im Code), die Optionen `:63-67`, das `<select>` `:145-150`. Auf der
Bestenliste ist die Sortierung die **Server**-Dimension; zwei Sortier-Controls — eines wählt die
Liste, das andere sortiert sie um — sind ein Bedienfehler.

Ein Input, der nur das `<select>` versteckt, ließe `sortMode` auf `'none'` stehen und damit vier
Dinge aus, die der Produktkern sind:

| Was `sortMode` steuert | Zeile |
|---|---|
| Erklärsatz unter der Kopfzeile | `:171-175` |
| Score-Kachel auf jeder Zelle | `:208-216` |
| `cellLabel`/`spokenScore` — der Score steckt im `aria-label` | `:367-395` |
| `sortedEmotes`, worüber auch `ListSelection` konstruiert wird | `:280-300`, `:314-317` |

**Vorgabe:** `forcedSortMode` (E12) setzt den effektiven Modus; das `<select>` wird bei gesetztem
Input nicht gerendert. Dazu: `sortSelectId` ist hart `'foreign-emote-sort'` (`:272`); die drei
Caption-Schlüssel sind kanalgebunden formuliert — `import.foreignChannel.empty` („…**dieses
Kanals**", `de.json:959`), `.truncated` („…**des Sets**", `:960`), `.sort.scoreHint` („…in **diesem
Kanal**", `:971`). Die #147-Fälle (`foreign-emote-grid.spec.ts`, 13 Tests; `:116` prüft den
`'none'`-Default) setzen keinen der neuen Inputs und bleiben ohne Änderung grün — **Regression,
keine Deckung**. Der Sortieralgorithmus `:285-299` sortiert absteigend nach Score, `null` zuletzt,
stabil; auf der Bestenliste ist das dieselbe Dimension wie 7TVs Rangfolge (Abnahme prüft die ersten
zehn Positionen gegen 7TV, AK 30).

### F3 — Die 429-Semantik ist privat, die Call-Source hart verdrahtet, und der HTTP-Header wird nirgends gelesen

Die ganze Erkennung steckt in `FetchPreviewPageAsync` (`SevenTvApiClient.cs:410-466`): literales
HTTP 429 (`:427-431`), `extensions.status == 429` hinter HTTP 200 (`IsRateLimited`, `:651-652`),
Reset-Hinweis aus dem **GraphQL-Payload** (`ReadResetHintSeconds`, `:669-692` — laut eigenen
Remarks „no confirmation that 7TV sends this at all"), Telemetrie-Suppression des Handlers (`:418`,
`ProviderTelemetrySuppression.OptionsKey`, `ProviderRequestTelemetryHandler.cs:15`), genau eine
Beobachtung je Request. `RecordForeignPreviewObservation` (`:467-476`) trägt die Call-Source
**als Konstante** (`SevenTvForeignPreview`) und liest Twitchs Schreibweise `Ratelimit-*` (`:473-475`)
— dieselbe, die der Handler liest (`ProviderRequestTelemetryHandler.cs:47-51`). **Der gemessene
HTTP-Header `x-ratelimit-search-*` wird nirgends ausgewertet.** Folge ohne Nacharbeit: nach einer
Sperre fiele der Breaker auf 60 s zurück und sondierte im Minutentakt gegen einen gesperrten Eimer.

Korrektur am Entwurf: `V4GqlPath` wird an **zwei** Stellen benutzt (`:417` und `:563`), nicht an
einer.

**Vorgabe (E11):** ein gemeinsamer privater v4-Seitenabruf mit Call-Source-Parameter; die
Bestenlisten-Variante liest zusätzlich `x-ratelimit-search-limit/-remaining/-reset` in
`RateLimitLimit/Remaining/Reset` der `ProviderResponseObservation` (`IRateLimitTelemetry.cs:64-71`)
und nimmt für `RetryAfter` in dieser Reihenfolge: HTTP `x-ratelimit-search-reset` → GraphQL-Hinweis
→ `Retry-After` (`:421`) → nichts.

### F4 — Der Dialog ist hart auf `ForeignChannelStep` typisiert

`import-source-dialog.ts`: `viewChild(ForeignChannelStep)` (`:192`), `channelResult` (`:214`),
`gridVisible = channelStep()?.showsGrid()` (`:219` — treibt Pane-Breite **und** ob es überhaupt
ein „Weiter" gibt, `:170-180`), `continueWithChannel` (`:246-252`), `titleKey` (`:203-212`, braucht
einen dritten Titel), `ImportSourceDialogResult` (`:37-38`), `ImportSourceStep` (`:43`), die
`@case`-Zweige (`:139-148`), plus die Verzweigung `import-trigger.ts:120-125`. Dazu verlangt
`ForeignChannelImportResult` (`foreign-channel-step.ts:25-31`) `channelName`, `sevenTvUserId` und
`emoteSetId` — eine Bestenlisten-Auswahl hat keines der drei, also ist `buildForeignImportSource`
(`foreign-import-flow.ts:38`) nicht wiederverwendbar. Der Code-Kommentar „one more entry here plus
one more `@case`" (`:40-42`) beschreibt den Dialog-Eintrag, nicht das Arbeitspaket.

**Vorgabe:** eigener Schritt `LeaderboardStep` mit eigenem Ergebnistyp
`LeaderboardImportResult { sortBy; rows }`, eigenes `buildLeaderboardImportSource`, eigener
`startLeaderboardImportFlow`; `gridVisible` und das „Weiter" werden über **beide** Steps gebildet.

### F5 — Der Telemetrie-Store führt zwei Fenster, behält nur das letzte Header-Sample, und die Admin-Ansicht braucht einen Locale-Schlüssel je Call-Source

`RateLimitTelemetryStore` liegt unter `src/EmotePurge.Infrastructure/Redis/` (nicht `Telemetry/`):
`FineBucketsPerWindow = 12` (`:66`, 60-s-Fenster) und `CoarseBucketsPerWindow = 1440` (`:70`,
24-h-Fenster). „Pro Stunde" ist dort keine ablesbare Zahl — deshalb sind AK 4 und AK 5 als
Minuten- und 24-h-Werte formuliert. Der Header-Sample wird je Antwort **überschrieben**
(`:128-141`); ein gefährlicher Tiefstand würde von der nächsten Antwort verdeckt (→ E15).

Beim Nachprüfen dazugekommen: die Admin-Monitoring-Seite rendert jede Call-Source über
`'admin.rateLimits.providers.callSources.' + provider.callSource` (`admin-monitoring-page.ts:524`);
die Tabelle kennt drei Einträge (`de.json:350-354`, `en.json:350`). **Eine neue Call-Source ohne
Schlüssel in beiden Locales erscheint dort als roher Übersetzungsschlüssel** — AK 14.

### F6 — „Kein Worker-Code" heißt kein `src/EmotePurge.Worker/**` und kein Zählpfad, nicht „das Worker-Image bleibt bitgleich"

Der Entwurf schreibt, das Worker-Image bleibe bitgleich. Das gilt nur für Änderungen unter `web/**`
und `src/EmotePurge.Api/**` (`publish.yml:160-170`): `src/EmotePurge.Infrastructure/**` und
`src/EmotePurge.Core/**` fallen auf „beide Images bauen" durch — absichtlich, als Allowlist dessen,
was sicher übersprungen werden kann. Diese Spec ändert Infrastructure (Client, Services, DI) und
Core (Interfaces, Modelle), **wie #147 auch**, und baut damit ein neues Worker-Image mit Code,
den der Worker enthält, aber nie ausführt.

**Vorgabe:** keine Änderung an `src/EmotePurge.Worker/**`, an `SevenTvSyncService`,
`ResolveTwitchUserIdAsync`/`GqlUsersQuery` (`SevenTvApiClient.cs:18`, `:72-118`) oder an einer
Klasse des Zählpfads (`EmoteMatchCache`, `UsageFlushWorker`, Matching). Der Neustart des
Worker-Prozesses beim Stack-Update ist der akzeptierte Preis (Epic #118: setzt das Fenster nicht
zurück). AK 26 prüft es am Diff.

### F7 — `emotes.search` liefert `Emote`, nicht `EmoteSetEmote`, und die Bildadresse braucht `flags.animated`

Die #147-Vorschau liest Set-Einträge: `{ alias emote { id default_name flags { animated } scores {…} } }`
(`GqlEmoteSetPreviewQuery`, `SevenTvApiClient.cs:61-62`; DTO
`SevenTvGqlEmoteSetPreviewItemDto`, `SevenTvApiDtos.cs:291`). Die Bestenliste liefert **flache**
`Emote`-Objekte ohne `alias` — neues DTO, `Name` = `defaultName` (F4-Zeilentyp bleibt).

Und: `BuildForeignImageUrl` (`:750-753`) hängt an `animated` — `4x_static.webp` existiert **nur**
für animierte Emotes, ein statisches Emote antwortet dort 404 (gemessen, Kommentar `:728-745`). Wer
`flags { animated }` in der Suchabfrage vergisst und `false` annimmt, bekommt korrekte, aber schwere
Bilder (173 von 250 Zeilen der Tagesliste sind animiert; die #147-Messung: 73 MB als `2x.webp` gegen
15 MB als `4x_static.webp`). Wer `true` annimmt, bekommt 404 auf 77 von 250.

**Vorgabe:** die Suchabfrage holt `flags { animated }` und baut die Adresse über dieselbe Funktion.
`defaultZeroWidth` wird **nicht** abgefragt (E4).

---

## 4. Vertrag: Endpunkt

```
GET /api/seventv/leaderboard?sortBy=<TRENDING_DAILY|TOP_ALL_TIME>
```

Eigene `MapGroup` `/api/seventv/leaderboard` in `SevenTvEndpoints.cs` neben der bestehenden
Kanal-Gruppe (`:25-28`). Nur `RequireAuthorization()`, **kein** `ChannelNameValidationFilter` (es
gibt keinen Kanalnamen), **kein** `UsageStatsAccessAuthorizationFilter`. Die `sortBy`-Prüfung ist
ein eigener `IEndpointFilter` `LeaderboardSortValidationFilter` nach dem Muster von
`ChannelNameValidationFilter`, damit der Handler dünn bleibt und der 400-Vertrag in `Api.Tests`
festgenagelt wird (Regel 11).

**Tatsächliche Reihenfolge** (Vertrag, wird getestet):

| Position | Läuft wo | Wirkung |
|---|---|---|
| 1 | `UseAuthentication`/`UseAuthorization`, Middleware (`Program.cs:290-291`) | nicht eingeloggt → **401** |
| 2 | `UseRateLimiter`, Middleware (`Program.cs:298`), Policy `SevenTvLeaderboard` (E16) | über Budget → **429** |
| 3 | `LeaderboardSortValidationFilter`, **Endpoint**-Filter | fehlender/ungültiger `sortBy` → **400** `invalid_leaderboard_sort`, **ohne** Service-Aufruf |

Ein ungültiger `sortBy` über Budget bekommt 429, nicht 400 — dieselbe bewusste Folge wie bei #147
(Abschnitt 4 dort). Die Allowlist ist **ordinal**: `trending_daily` ist ungültig, `TRENDING_WEEKLY`
auch (`ForeignEmoteRow` hat kein `trendingWeek`-Feld — `IForeignEmoteSetService.cs:135-141`).

**Was der Client nicht senden kann — Bypass-Freiheit, Bedingung des Sicherheitsarguments:**

- **kein `page`** — die Seitendecke ist serverseitig (E5); der Server setzt die Liste aus den
  Upstream-Seiten 1 und 2 zusammen und liefert bis zu 500 Zeilen in **einer** Antwort (~30 KB;
  das Raster hat bei #147 956 Zeilen virtuell gescrollt). Seite 2 wird nur geholt, wenn Seite 1
  `pageCount ≥ 2` meldet.
- **`perPage` = 250 serverseitig**, nie vom Client.
- **kein `refresh`**, kein `Cache-Control`-Durchgriff. Der #147-Endpunkt hat `?refresh=true`
  (`SevenTvEndpoints.cs:34`, `HardenedForeignEmoteSetService.cs:76`); mit ihm könnte jeder
  eingeloggte Client pro Klick einen Upstream-Request erzwingen. Ein „neu laden"-Knopf im
  Frontend liest nur den Vorrat.
- **kein Suchbegriff, keine Tags, keine Filter** — das ist die Grenze zu Stufe 3 des Tickets:
  nicht vorratsfähig.
- Unbekannte Query-Parameter werden ignoriert, nicht abgelehnt (wie überall in der Api).

**Upstream-Abfrage** (Form, kein Code; die Variablenform für `sort` aus der Sonde vom
2026-09-13 übernehmen, nicht raten):

```
emotes { search(sort: $sort, page: $page, perPage: 250) {
  totalCount pageCount
  items { id defaultName flags { animated } scores { topAllTime trendingDay } }
} }
```

Ohne `query`, ohne `filters`, ohne `tags`. `defaultZeroWidth` wird nicht geholt (E4).

**Antwortform** (Vertrag):

```
SevenTvLeaderboardResponse {
  sortBy: "TRENDING_DAILY" | "TOP_ALL_TIME"   // Echo der Allowlist-Vokabel
  totalCount: int                              // was 7TV ansagt (705 bzw. 1.371.890 gemessen)
  truncated: bool                              // totalCount > emotes.length — Decke erreicht
  emotes: ForeignEmoteRow[]                    // in 7TVs Rangfolge, höchstens 500
}

ForeignEmoteRow  // wiederverwendet, IForeignEmoteSetService.cs:135-141
{
  sevenTvEmoteId, name (= defaultName), defaultName, imageUrl, topAllTime, trending
}
```

`truncated` ist auf der Bestenliste der **Normalfall** (500 von 705 bzw. von 1,37 Mio). Der Text
dazu darf nicht für beide Sortierungen derselbe sein: 71 % der Liste gegen 0,036 %. Deshalb
**zwei** Schlüssel, beide mit `loaded` und `totalCount`, gewählt nach `sortBy` (Abschnitt 7).

Der Endpunkt schreibt **nichts** in die DB. Keine Migration.

---

## 5. Vertrag: Zustände und Fehlercodes

| Zustand | Quelle | Antwort |
|---|---|---|
| `sortBy` fehlt oder nicht in der Allowlist | `LeaderboardSortValidationFilter` | 400, **neuer** Code `invalid_leaderboard_sort`, **kein** Upstream-Aufruf |
| Vorrat trifft, oder Füllung erfolgreich | `Ok` | 200 |
| Liste mit 0 Zeilen | `Ok`, leere Liste | **200**, Leerzustand, kein Code — und **gecacht wie ein Treffer** (1 h) |
| 7TV 429 (HTTP 429 **oder** HTTP 200 mit `extensions.status: 429`) | `SevenTvRateLimited` | 503, **vorhandener** `foreign_channel_seventv_unavailable` (E13) |
| 7TV 5xx, Timeout, Parsefehler, Validierungsablehnung | `SevenTvUnavailable` | 503, derselbe Code |
| Breaker offen | `SevenTvRateLimited` bzw. `SevenTvUnavailable` je Öffnungsgrund, ohne Upstream | 503, derselbe Code |
| Fensterbudget verweigert (11. Request in 60 min) | `BudgetRefused`, **ohne** Upstream, **ohne** Breaker-Meldung | 503, derselbe Code — **kein** neuer Code (E13) |
| Seite 1 ok, Seite 2 fehlgeschlagen (auch vom Budget verweigert) | Ausgang von Seite 2 | Antwort **wie der Fehler von Seite 2** — keine stille Kürzung auf 250 (F3 aus #147). **Seite 1 wird verworfen**, nicht behalten: der ganze Eintrag übernimmt Ausgang und Haltbarkeit des Fehlers, und die nächste Füllung holt **beide** Seiten neu (Füllpfad, Abschnitt 6) |

**Ein neuer Code**, nicht zwei. Er braucht denselben Eintrag in `ApiErrorCodes.cs`, in
`web/src/app/core/i18n/api-error.ts` (`KNOWN_API_ERROR_CODES`) **und** in beiden Locales
(`errors.api.invalid_leaderboard_sort`) — Regel 7. `api-error-locales.spec.ts` erzwingt die
beiden hinteren Schritte; der Schritt von `ApiErrorCodes.cs` nach `api-error.ts` bleibt Disziplin
(AK 24).

**429 aus dem GraphQL-Payload:** 7TV meldet Überlast als HTTP **200** mit `extensions.status: 429`.
Ohne diese Erkennung sähe ein Fehlschlag aus wie eine **leere Bestenliste** — die laut Tabelle
oben eine Stunde lang gecacht würde. Das ist derselbe teuerste Einzelfehler wie bei #147, hier mit
einer Stunde Nachwirkung statt 60 s.

---

## 6. Vertrag: Härtung

**Zwei Dinge tragen die Sicherheit, nicht eines** — das ist das Ergebnis der Schiedsentscheidung
im Entwurf und wird leicht falsch abgeschrieben: der endliche Schlüsselraum (2 Einträge, je
bis zu 2 Upstream-Seiten, 1 h Haltbarkeit; 2 Sortierungen × 2 Seiten = 4 Requests) hält den
**Erwartungswert** bei 4 Upstream-Requests pro Stunde; das harte Fensterbudget hält den **Deckel**
bei 10 je rollendem 60-min-Fenster **pro Prozesslauf**, unabhängig vom Nutzerverhalten, auch bei
Fehlern. Nicht „der Schlüsselraum genügt": ein endlicher Schlüsselraum deckelt nur erfolgreich
gecachte Antworten — auf einem fehlschlagenden Schlüssel liefe die Füllung sonst immer wieder an.

**Neustart-Vertrag (je Prozesslauf):** Das Budget lebt im Prozess und beginnt nach einem Neustart
leer. Über einen Neustart innerhalb derselben rollenden Stunde **addieren sich** die Läufe: der
ungünstigste Fall ist 10 × Anzahl der Prozessläufe in dieser Stunde; ein Kaltstart mit gültigen
7TV-Antworten kostet 4. Akzeptiert, weil Neustarts Deploys oder Abstürze sind und kein Nutzer sie
auslösen kann; eine Absturzschleife ist ein Betriebsfehler, den der Container-Healthcheck sichtbar
macht. Ein Deckel „auch über Neustarts hinweg" hieße persistierter Zustand — genau der
Redis-/verteilte Weg, den die Replica-Bedingung unten erst für eine zweite Replica verlangt.

| Maßnahme | Festlegung |
|---|---|
| Vorrat | **Im Prozessspeicher**, nicht Redis (Entwurf: `ForeignEmoteSetCache` ist fail-open, `ForeignEmoteSetCache.cs:17-20`; ein Redis-Ausfall würde hier den Deckel wegnehmen). Eine Api-Replica; `docker-compose.prod.yml:72-74` schließt eine zweite aus einem anderen Grund ohnehin aus. **Bedingung:** kommt je eine zweite Replica, braucht es Redis fail-closed **und** ein verteiltes Fensterbudget, sonst ist der Deckel `Replicas × 10` — gehört in den `DECISIONS.md`-Eintrag |
| Schlüssel | **`sortBy`** — 2 Einträge, nicht 4. Ein Eintrag hält die vollständig zusammengesetzte Liste **eines** Füllvorgangs (Upstream-Seite 1 und, bei `pageCount ≥ 2`, Seite 2); Seiten sind kein Schlüsselbestandteil |
| Haltbarkeit | nach Ausgang **des ganzen Füllvorgangs**, s. Tabelle unten; ein `ExpiresAt` je Eintrag, nicht je Seite; **eine leere Liste wird gecacht wie ein Treffer** |
| Koaleszierung | im Vorrat eingebaut (`Lazy<Task<…>>`) **je Sortier-Schlüssel**, nicht der #147-Coalescer; ein Factory-Lauf umfasst beide Upstream-Seiten, der Eintrag wird atomar ersetzt — keine Antwort mischt Seiten aus zwei Füllvorgängen |
| Fensterbudget | **10 Requests je rollendem 60-min-Fenster pro Prozesslauf, Wartezeit 0**, providerweit, vor **jedem** `emotes.search` — jede Upstream-Seite wird **einzeln** vor ihrem Request belastet. Seite 1 gewährt, Seite 2 verweigert → ganzer Eintrag `BudgetRefused` (30 s); die Freigabe für Seite 1 gilt als verbraucht. Verweigerung → `BudgetRefused`, 30 s Haltbarkeit, **kein** Upstream, **keine** Breaker-Meldung (wie `ReleaseProbeWithoutOutcome`, `HardenedForeignEmoteSetService.cs:128`) |
| Alarm | **Warnung bei ≥ 6 Requests im 60-min-Fenster**, einmal je Fenster, **vor** dem Deckel. 6 = Erwartung 4 plus ein fehlgeschlagener Füllzyklus innerhalb eines Prozesslaufs (ein solcher Zyklus kostet 2 Requests, s. Füllpfad). Ein Doppel-Fill bei Ablauf ergäbe 8 und löst den Alarm aus. Der Alarmzähler zählt **je Prozesslauf** (er liest `usedInWindow` desselben In-Process-Budgets) und **vergisst Anfragen vor dem Neustart** — Requests mehrerer Läufe in derselben Stunde sieht er nie zusammen; eine Absturzschleife macht der Container-Healthcheck sichtbar, nicht dieser Alarm |
| Breaker | **Eigene Instanz** von `ForeignSevenTvBreakerPolicy` per **Keyed-Registration** (`AddKeyedSingleton`, Schlüssel z. B. `"seventv-leaderboard"`), weil die #147-Instanz per Typ als Singleton registriert ist (`ServiceCollectionExtensions.cs:101`). Andere Fehlerdomäne: eine Such-Eimer-Sperre darf die Fremdkanal-Vorschau nicht schließen, die den Eimer nie berührt. **Rolle: schlüsselübergreifende Ausbreitung** über die 2 Sortier-Schlüssel, nicht Wiederanlauf je Schlüssel (das macht `ExpiresAt`): ein 429 beim Füllen von `TRENDING_DAILY` (auf Seite 1 oder 2) liefert für `TOP_ALL_TIME` `Failed(RateLimited)` **ohne Upstream**, mit `RemainingOpenTime` als Haltbarkeit. Schwellen wie E4 bei #147: 429 öffnet sofort, sonst 5 aufeinanderfolgende (`:94`), danach genau ein Probe-Request (`:135-141`) |
| Neustart | Vorrat, Breaker, Budget und Alarmzähler sind kalt. Ein Kaltstart mit gültigen Antworten kostet 4 Requests; der Deckel gilt **je Prozesslauf**, über Neustarts in derselben rollenden Stunde addieren sich die Läufe (ungünstigster Fall 10 × Läufe, s. Neustart-Vertrag oben). Von keinem Nutzer auslösbar, akzeptiert |
| Telemetrie | eigene Call-Source, s. Telemetrievertrag |

### Der Vorrat — Komposition, verbindlich

`ForeignEmoteSetRequestCoalescer` (`:32`, `:47-49`, `:63-67`) ist genau das Muster, das hier
gebraucht wird — mit dem Unterschied, dass er den Eintrag im `finally` **entfernt**, mit der
Begründung „a permanently cached 'in flight' entry would freeze every later lookup … on today's
answer". Ein Vorrat mit Haltbarkeit muss das anders lösen. Verbindlich:

- **Eintrag ist `(Lazy<Task<Outcome>>, DateTimeOffset ExpiresAt)`**, nicht nur der `Lazy` — ein
  `Lazy` kennt keine Haltbarkeit. `ExpiresAt` berechnet die **Factory beim Abschluss** aus dem
  Ausgang, nicht der Leser beim Anlegen; ob es physisch im Tupel oder auf dem `Outcome` liegt, ist
  Implementierungsfreiheit — verbindlich ist: **ein laufender Eintrag hat kein `ExpiresAt` und
  läuft damit nie ab.**
- **Ersetzen per `TryUpdate` gegen den gelesenen Eintrag.** `GetOrAdd` ersetzt nichts; ein naives
  `AddOrUpdate` lässt bei Ablauf zwei Aufrufer zwei Upstream-Requests starten — der Deckel
  verdoppelt sich still. Verliert ein Leser das `TryUpdate`, nimmt er den Eintrag des Gewinners.
- **Fehler werden negativ gecacht, nicht entfernt.** Ein 429 ist ein **erfolgreich abgeschlossener**
  Task mit Fehlerstatus (`ExecuteGuardedAsync` gibt `Failed(...)` **zurück**,
  `HardenedForeignEmoteSetService.cs:110-113`, `:133`), kein geworfener. „Sofort entfernen" öffnete
  ein Loch: auf einem fehlschlagenden Schlüssel liefe die Factory bei jedem Klick erneut an, der
  Breaker ließe fünf gewöhnliche Fehler zu und danach je Öffnungsfenster einen Probeaufruf.

  | Ausgang | Haltbarkeit |
  |---|---|
  | Treffer (alle nötigen Seiten `Ok`), auch leere Liste | **1 h** |
  | `SevenTvRateLimited` | `RetryAfter` von 7TV (Reihenfolge s. F3), **geklemmt auf [60 s, 1 h]**; ohne Hinweis 60 s |
  | `SevenTvUnavailable` (5xx, Timeout, Parsefehler) | **60 s** |
  | Breaker offen (ohne Upstream) | `RemainingOpenTime`, geklemmt auf [1 s, 1 h] |
  | Budget verweigert (Seite 1 oder Seite 2) | **30 s** |

  Schlägt eine der beiden Seiten fehl, bestimmt **dieser** Fehler Ausgang und Haltbarkeit des
  ganzen Eintrags; eine Teilliste wird weder ausgeliefert noch behalten.

- **Ein `Faulted`- oder `Canceled`-Task entsteht nur über den Backstop** (analog
  `HardenedForeignEmoteSetService.cs:146-151`: eine unerwartete Exception, die alle
  Ergebnispfade übersprungen hat). Er gilt als **sofort abgelaufen** und wird beim nächsten Leser
  per `TryUpdate` **ersetzt — nicht entfernt**. Der Backstop meldet dem Breaker `OtherFailure`.
- **Die Füllung läuft unter `CancellationToken.None`**, nie unter dem Token des ersten Aufrufers
  — sonst killt ein wegnavigierender Browser den Vorrat für alle (der Bug, den das
  Coalescer-Remark `:10-17` als behoben beschreibt). Der Aufrufer wartet mit **seinem** Token auf
  den geteilten Task.
- Speicher: 2 Einträge × ≤ 500 Zeilen. Kein Eviction nötig.

Ein Vorbild für „Haltbarkeit im Prozess" gibt es im Repo **nicht** (`EmoteMatchCache` ohne
Haltbarkeit, `ChannelSyncGate` ein Semaphor-Dictionary, `IMemoryCache` nirgends referenziert) — der
Vorrat ist eine **neue pure Klasse** `SevenTvLeaderboardStore` mit `TimeProvider`, und sie bekommt
die Tests aus Abschnitt 10 **vor** dem Service, der sie benutzt.

### Füllpfad, in Reihenfolge

1. Endpunkt → `ISevenTvLeaderboardService.GetLeaderboardAsync(sortBy, ct)`.
2. Service fragt den Vorrat nach dem Schlüssel `sortBy`; Treffer und nicht abgelaufen → Antwort
   ist der Eintrag, fertig.
3. Sonst **ein** Factory-Lauf unter `CancellationToken.None`, der den ganzen Eintrag füllt. Je
   Upstream-Seite, beginnend mit Seite 1: Breaker `TryAcquire` → nicht erlaubt: Ausgang nach
   Öffnungsgrund, Haltbarkeit `RemainingOpenTime` → erlaubt: Budget `TryCharge` → verweigert:
   `BudgetRefused`, 30 s, `ReleaseProbeWithoutOutcome` → gewährt (Alarm bei ≥ 6): Client
   `SearchEmotesAsync(sortBy, page)` → Ausgang → Breaker-Rückmeldung (`RecordSuccess`/
   `RecordFailure` mit Generation).
4. Ist Seite 1 `Ok` und `pageCount ≥ 2`: Schritt 3 für Seite 2 **im selben Factory-Lauf**. Das
   Budget wird dafür erneut belastet; eine Verweigerung hier macht den ganzen Eintrag zu
   `BudgetRefused`, die Freigabe für Seite 1 bleibt verbraucht.
5. Zusammensetzen: `emotes` = Seite 1 + Seite 2 in 7TVs Reihenfolge verkettet, **nach 7TV-Id
   dedupliziert, erstes Vorkommen gewinnt**; `totalCount` von Seite 1, `truncated` =
   `totalCount > emotes.Count`. Fehlschlag **einer** der beiden Seiten → der ganze Eintrag
   übernimmt Ausgang und Haltbarkeit dieses Fehlers; keine Teilliste wird ausgeliefert oder
   behalten. `ExpiresAt` nach Tabelle, dann **atomarer** Ersatz des Eintrags.

Folge von Schritt 5: nach einem Fehler von Seite 2 holt die nächste Füllung **auch Seite 1 neu**.
Ungünstigster Fall 2 Requests je fehlgeschlagenem Zyklus (60 s bei `SevenTvUnavailable`, ≥ 60 s bei
`SevenTvRateLimited`) — weiterhin unter dem Budget, das jede Seite einzeln belastet.

**Grenze des Zusammensetzens, ehrlich:** 7TV bietet weder Cursor noch Snapshot; Seite 1 und Seite 2
sind zwei unabhängige Abfragen im Abstand von Sekunden. Wandert ein Emote **innerhalb desselben**
Füllvorgangs über die 250er-Grenze, kann es in der Antwort **fehlen** (es stand beim ersten Request
auf Seite 2, beim zweiten auf Seite 1). **Duplikate** sind durch die Deduplizierung ausgeschlossen,
und Seiten aus **verschiedenen** Füllvorgängen kommen nie zusammen. Bei einer Liste, die sich über
Stunden bewegt, ist ein fehlendes Emote an Position 250 hingenommen (AK 35).

### Telemetrievertrag

Wie bei #147 (Abschnitt 6 dort): der Message-Handler bekommt seine Call-Source **fest bei der
Registrierung des typisierten Clients** (`ServiceCollectionExtensions.cs:82`,
`RateLimitCallSources.SevenTvRest`) und sieht bei `extensions.status: 429` nur HTTP 200. Deshalb
**meldet die Client-Methode selbst**, der Handler ist per `ProviderTelemetrySuppression.OptionsKey`
stillgelegt, und die Beobachtung entsteht **nach** dem GraphQL-Parsing mit semantischem Status.

- **Neue Call-Source `RateLimitCallSources.SevenTvLeaderboard = "seventv-leaderboard"`**
  (`IRateLimitTelemetry.cs:88-109`), plus Locale-Schlüssel
  `admin.rateLimits.providers.callSources.seventv-leaderboard` in beiden Sprachen (F5).
- **Genau eine Beobachtung je Upstream-Request.** Nicht null, nicht zwei. Ein 429 in beiden
  Formen zählt als Rate-Limit-Ereignis.
- **Die HTTP-Header `x-ratelimit-search-limit/-remaining/-reset` werden erfasst** — neue Arbeit
  (F3) — und landen in `RateLimitLimit/Remaining/Reset` der Beobachtung; dazu **eine Log-Zeile je
  Upstream-Request** (Information) mit `sortBy`, `page`, `remaining`, `reset`, Requests im
  Budgetfenster. Das ist die Grundlage für E15/AK 31.
- Die Beobachtungen der Fremdkanal-Vorschau bleiben unter `seventv-foreign-preview`; keine
  Beobachtung wandert zwischen den Quellen (AK 13).

### Bedeutungsvertrag der Score-Spalte (P5', eingegrenzt — E3)

`topAllTime`/`trending` kommen im Suchbatch gratis mit (`complexity` 7). Ihre **Bedeutung** kostet
weiter, nur anders als bei #147:

- Sie werden als **netzwerkweit** beschriftet, in beiden Locales — bleibt.
- Die Spalte heißt **nie** bloß „Beliebtheit" und ist **nie** als kanal-lokal lesbar — bleibt.
- **Neu:** auf der Bestenliste **ist** die Sortierung nach Score die Standardsortierung, weil die
  Rangfolge der Inhalt ist; die Sortierauswahl benennt hier die **Liste** („Trend heute"/„Top
  insgesamt"), nicht eine Eigenschaft des Emotes. Punkt 5 und P5' aus `DECISIONS.md` 2026-09-10
  verengen sich damit auf die Beschriftung. Für die Fremdkanal-Ansicht ändert sich **nichts**:
  dort bleibt `'none'` die Vorbelegung (`foreign-emote-grid.ts:269`, AK 16 aus #147).

`Emote.channels.totalCount` wird weiterhin **nicht** geholt (eigener Such-Eimer).

---

## 7. Vertrag: Frontend

Fluss unverändert: **Quelle → Auswahl → Ziel → Bestätigung.** Dritte Option im bestehenden Dialog,
keine eigene Seite; die Erzählung bleibt „eine Quelle unter anderen". Der Sortierwechsel läuft aus
dem Vorrat, also ohne Upstream. **Die UI paginiert nicht** — das löst den Codex-Befund zur
seitenweisen Auswahl auf (`ListSelection.selectedItems` löst nur gegen die sichtbare Liste auf,
`list-selection.ts:39-42`), statt ihn zu mildern.

| Baustein | Was passiert |
|---|---|
| `import-source-dialog.ts` | Dritter `SourceOption` (`step: 'leaderboard'`, `import.source.leaderboard.label/.hint`), dritter `@case`, `ImportSourceStep` um `'leaderboard'`, `titleKey` → `import.leaderboard.title`, `viewChild(LeaderboardStep)`, `gridVisible` = Kanal-Grid **oder** Bestenlisten-Grid, `ImportSourceDialogResult` um `{ kind: 'leaderboard'; picked: LeaderboardImportResult }`, `continueWithLeaderboard` (F4) |
| `leaderboard-step.ts` (**neu**) | Sortier-`<select>` mit den beiden Allowlist-Werten als **Server**-Dimension (Default `TRENDING_DAILY`), lädt beim Betreten und bei jedem Wechsel über `SevenTvLeaderboardService`; Zustände `loading`/`error`/`loaded` wie `ForeignChannelStep` (`:67-91`); bettet `ForeignEmoteGrid` mit `forcedSortMode` (`TRENDING_DAILY` → `'trending'`, `TOP_ALL_TIME` → `'topAllTime'`) und den drei Caption-Schlüsseln ein; `showsGrid`, `result()` (`null` ohne Auswahl), `focusFirstControl` nach demselben Vertrag wie `foreign-channel-step.ts:191-209`. **Sortierwechsel leert die Auswahl sichtbar** (E14): `selectedRows = []`, `result() === null`, „Weiter" gesperrt; das Raster wird für die neue Liste neu instanziiert (Zustand `loading` dazwischen), so dass `ListSelection` keine Schlüssel mitnimmt |
| `leaderboard.model.ts` + `seven-tv-leaderboard.service.ts` (**neu**, `core/seven-tv/`) | `LeaderboardSort = 'TRENDING_DAILY' \| 'TOP_ALL_TIME'`, `SevenTvLeaderboardResponse`, `LeaderboardImportResult { sortBy; rows }`; Service mit `load(sortBy)` → `GET /api/seventv/leaderboard?sortBy=…`, **ohne** `refresh`-Option (Bypass-Freiheit) |
| `foreign-emote-grid.ts` | Vier optionale Inputs (E12/F2): `forcedSortMode`, `emptyMessageKey`, `truncatedMessageKey`, `scoreHintKey`; effektiver Modus = `forcedSortMode ?? sortMode()`; `<select>` nur ohne `forcedSortMode`; `sortSelectId` instanzeindeutig. **Alle 13 #147-Fälle unverändert grün** |
| `ImportOrigin` (`import-source.ts:29-38`) | Vierter Zweig `{ kind: 'seventv-leaderboard'; sortBy: LeaderboardSort }`; `importOriginSourceChannelName` → `null` für ihn; **neue** erschöpfende Hilfsfunktion `importOriginLeaderboardSort` → Sortiercode bzw. `null`; Docstring `:12-28` mitziehen |
| `emote-admin.service.ts:37-38` | `sourceKind`-Union um die vierte Vokabel, `SyncImportedBody.leaderboardSort: LeaderboardSort \| null` |
| `seven-tv-import.service.ts:303-304` | `reportImported` sendet **beide** Felder über die beiden Hilfsfunktionen (F1) |
| `foreign-import-flow.ts` | **Neu daneben:** `startLeaderboardImportFlow` + `buildLeaderboardImportSource` (`name` = `defaultName`, `dedupeImportRows`, `discardedRows: 0`); `import-trigger.ts:120-125` verzweigt auf `result.kind === 'leaderboard'` |
| `import-confirm-dialog.ts:290-298` | Dritter Herkunftszweig `leaderboardOrigin()` → `import.confirm.originLeaderboard` mit dem übersetzten Sortierlabel (E2). Der Kollisionshinweis (`nameCollisions`, `:198-203`) greift unverändert (AK 18) |
| `ImportTargetDialog` | Nicht berührt — #147 hat den Ziel-Dialog aus dem Fremdkanal-Fluss bereits entfernt (Ziel = Kanal der Seite, `import-trigger.ts:121-123`); die Bestenliste geht denselben Weg |
| Audit-Anzeige | `AuditDetailKind` (`audit.model.ts:32-33`) um `importedFromLeaderboard`; `DETAIL_KEYS` (`audit-actions.ts:51-57`) um den Schlüssel; `renderDetail` (`audit-row.ts:53-68`) übersetzt für **diese eine Kind** den `text`-Code über `audit.details.leaderboardSort.<code>` in den `title`-Parameter (E9) |
| Locales, beide | `import.source.leaderboard.{label,hint}`, `import.leaderboard.{title,sortLabel,sort.TRENDING_DAILY,sort.TOP_ALL_TIME,empty,truncated.TRENDING_DAILY,truncated.TOP_ALL_TIME,scoreHint}`, `import.confirm.originLeaderboard`, `audit.details.importedFromLeaderboard`, `audit.details.leaderboardSort.{TRENDING_DAILY,TOP_ALL_TIME}`, `admin.rateLimits.providers.callSources.seventv-leaderboard`, `errors.api.invalid_leaderboard_sort`. Die beiden Sortierlabels heißen im Audit und im Bestätigungsdialog „7TV Trend heute"/„7TV Top insgesamt" (E2); die Score-Beschriftung bleibt netzwerkweit und nie bloß „Beliebtheit" (E3) |

Das Schreiben läuft weiterhin **vollständig im Browser** über den 7TV-Token aus dem
`sessionStorage`; das Backend sieht ihn nie. Diese Spec fügt eine **Lese**fähigkeit hinzu.

---

## 8. Aufgaben in Reihenfolge

Jeder Task läuft als eigener Subagent mit frischem Kontext (globale Regel). **Die Prüfliste aus
Abschnitt 14 wird zuerst abgearbeitet, nicht am Ende** — bei #147 saßen drei von fünf
Review-Befunden in Code, den die Spec als unverändert deklariert hatte.

| # | Task | Warum diese Position |
|---|---|---|
| T0 | **Charakterisierungstests für den #147-Vorschaupfad** (AK 34): kaputtes JSON → genau eine Beobachtung + `SevenTvUnavailable`; `Ratelimit-*` in der Beobachtung; HTTP-Header `x-ratelimit-search-reset` ohne Wirkung auf `RetryAfterSeconds`/Breaker. Nur Testdateien, **eigener Commit**, gegen den unveränderten Client grün | Die 21 bestehenden Client-Tests decken genau diese drei Verhaltensweisen nicht ab; ohne sie beweist „unverändert grün" nach dem Umbau nichts über sie. Muss **vor** T1 committet sein |
| T1 | **Client:** gemeinsamer privater v4-Seitenabruf mit Call-Source-Parameter (E11, F3), neue `ISevenTvApiClient.SearchEmotesAsync(sortBy, page, ct)` mit neuem DTO (F7), `flags.animated` → Bildadresse, HTTP-`x-ratelimit-search-*`-Erfassung, `RetryAfter`-Reihenfolge, neue Call-Source samt Locale-Schlüssel, Log-Zeile je Request. **Abnahme: die 21 bestehenden Client-Tests und die drei aus T0 unverändert grün** | Alles Weitere hängt am Ausgangstyp. Die 429-Erkennung gehört hierher, weil sie Teil des Parsers ist: ohne sie ist ein Fehlschlag nicht von einer leeren Liste zu trennen |
| T2 | **Pure Klassen mit Tests zuerst:** `SevenTvLeaderboardStore` (ein Eintrag je `sortBy`, Lazy + `ExpiresAt`, `TryUpdate`, negative Haltbarkeit, Backstop-Ersatz, `CancellationToken.None`) und `SevenTvLeaderboardRequestBudget` (E10), beide `TimeProvider`-getrieben, plus die Haltbarkeitsregel nach Ausgang als pure Funktion | Unabhängig von T1; hier sitzt der wahrscheinlichste Fehler (Doppel-Fill bei Ablauf). Tests **vor** dem Service |
| T3 | **Service + Endpunkt:** `ISevenTvLeaderboardService` (Core) + Implementierung (Infrastructure) mit Füllpfad aus Abschnitt 6, Keyed-Breaker, Alarm bei 6; `MapGroup`, `LeaderboardSortValidationFilter`, Policy an drei Stellen (E16), `invalid_leaderboard_sort` in `ApiErrorCodes.cs` + `api-error.ts` + beiden Locales; DI in `ServiceCollectionExtensions` | Braucht T1 und T2. Muss **vor** der UI stehen: eine Oberfläche gegen einen ungehärteten Pfad ist der verworfene Ansatz |
| T4 | **Herkunft (F1, E8, E9):** `leaderboardSort` in `SyncImportedRequest`, Vokabeltabelle statt `:164`, `IEmoteService`/`EmoteService`, `AuditLogDetail.Kinds.ImportedFromLeaderboard` + `ProjectDetail`-Zweig; Frontend: vierter `ImportOrigin`-Zweig, zweite Hilfsfunktion, `SyncImportedBody`, `reportImported`, Bestätigungsdialog-Zweig, Audit-Anzeige, Locales | Unabhängig von T1–T3 (eigene Leitung). Vor T6, damit der Fluss beim ersten Durchlauf eine korrekte Herkunft schreibt |
| T5 | **Raster:** die vier Inputs (E12/F2), instanzeindeutige id, Specs für `forcedSortMode`-Verhalten | Unabhängig; Aufsatz für T6 |
| T6 | **Verdrahtung:** `LeaderboardStep`, dritte Dialog-Option (F4), `leaderboard.model.ts`, `SevenTvLeaderboardService`, `startLeaderboardImportFlow`/`buildLeaderboardImportSource`, `import-trigger`-Zweig, Regressionsfall `nameCollisions`, **`DECISIONS.md`-Eintrag im selben Commit** (E3, Regel 3) | Braucht T3, T4, T5 |
| — | **Vorab-Kontrolle vor dem Stack-Update** (AK 36): im Admin-Bereich auf Prod von Hand prüfen, ob Kanäle mit Sync-Fehlergrund und leerer Twitch-ID existieren — kein Subagent, kein Code, letzter Schritt vor dem Stack-Update, nicht vor dem Merge | Kein Task im üblichen Sinn, weil ohne Code; steht trotzdem hier, weil sie **vor** dem Stack-Update laufen muss und sonst vergessen wird |

**Abhängigkeiten:** T0 → T1 → T3; T2 → T3; T3 + T4 + T5 → T6. **Parallel möglich:** T0/T1, T2, T4,
T5 gegeneinander, sobald die Antwortform aus Abschnitt 4 steht (T0 und T1 bleiben untereinander
sequenziell). Ein Task ist erst abgenommen, wenn er
für sich prüfbar ist — T1 also inklusive beider 429-Formen, T2 inklusive des Rennens beim Ablauf.

**Vor dem Merge:** `/codex:review --model gpt-5.6-sol` über das fertige Arbeitspaket (Regel 22).
Den Merge fährt der Nutzer. **Vor dem Stack-Update:** die Vorab-Kontrolle aus AK 36. Deploy nach
E6.

---

## 9. Akzeptanzkriterien

Nummeriert, pass/fail.

**Endpunkt und Bypass-Freiheit**

1. `GET /api/seventv/leaderboard?sortBy=TRENDING_DAILY` und `…=TOP_ALL_TIME` liefern eingeloggt
   HTTP 200 mit `sortBy`-Echo, `totalCount`, `truncated` und höchstens 500 Zeilen in 7TVs
   Rangfolge; jede Zeile trägt `sevenTvEmoteId`, `name` = `defaultName`, `imageUrl`, `topAllTime`,
   `trending`.
2. Ein fehlender, kleingeschriebener (`trending_daily`) oder fremder (`TRENDING_WEEKLY`) `sortBy`
   ergibt 400 `invalid_leaderboard_sort` — und der Service-Ersatz in `Api.Tests` wird **nicht**
   aufgerufen (kein Upstream).
3. `page`, `perPage`, `refresh`, `query`, `tags`, `filters` und `Cache-Control` im Request ändern
   **nichts**: zwei Aufrufe mit `?sortBy=TRENDING_DAILY&refresh=true&page=3` innerhalb einer
   Stunde erzeugen genau **einen** Satz Upstream-Requests.

**Deckel und Erwartungswert**

4. **Harter Deckel:** Bei erschöpftem Vorrat und wiederholtem Ablauf (Test mit `TimeProvider`)
   erzeugen 20 Füllversuche innerhalb von 60 min genau **10** Beobachtungen unter der Call-Source
   `seventv-leaderboard`; der 11. Versuch liefert 503 ohne Beobachtung und ohne Breaker-Meldung.
   Live: die Admin-Rate-Limit-Ansicht zeigt für diese Call-Source ≤ 10 im Minutenfenster
   unmittelbar nach einem Neustart — gezählt je Prozesslauf; ein Kaltstart mit gültigen Antworten
   trägt 4 bei, Requests des vorigen Laufs in derselben Stunde kommen hinzu (Neustart-Vertrag,
   Abschnitt 6, AK 33) — und ≤ 96 im 24-h-Fenster (F5: „pro Stunde" ist dort nicht ablesbar).
5. **Erwartungswert:** Ein warmer Prozess ohne Fehler erzeugt je Stunde ≤ 4 Beobachtungen unter
   `seventv-leaderboard`, egal wie viele Aufrufe der Endpunkt bedient (Test: 1.000 Aufrufe über
   beide Sortierungen in einer simulierten Stunde → 4 Upstream-Requests).
6. Der Alarm feuert bei ≥ 6 Requests im Fenster genau einmal je Fenster, **bevor** der Deckel
   greift.

**Vorrat**

7. Die vier Haltbarkeiten gelten: Treffer und leere Liste 1 h; `SevenTvRateLimited` =
   `RetryAfter` geklemmt auf [60 s, 1 h] (Test: 30 s → 60 s, 5 h → 1 h, ohne Hinweis → 60 s);
   `SevenTvUnavailable` 60 s; Budget verweigert 30 s. Jeweils: eine Sekunde vor Ablauf kein
   Upstream, eine Sekunde nach Ablauf genau einer.
8. Eine leere Liste wird gecacht wie ein Treffer: zwei Aufrufe innerhalb 1 h → ein Upstream.
9. Zwei Leser, die **genau beim Ablauf** desselben Sortier-Schlüssels gleichzeitig kommen, lösen
   genau **eine** Füllung aus (`TryUpdate`) — ein Factory-Lauf mit höchstens 2 Upstream-Requests,
   nicht zwei; der Verlierer bekommt das Ergebnis des Gewinners.
10. Ein `Faulted`- und ein `Canceled`-Task (Backstop) gelten als sofort abgelaufen und werden beim
    nächsten Leser ersetzt; der Eintrag wird **nie** entfernt (Dictionary-Größe bleibt 2).
11. Bricht der erste Aufrufer seinen Token während der Füllung ab, bekommt ein zweiter Aufrufer
    trotzdem das Ergebnis derselben Füllung (kein zweiter Upstream, kein `Canceled`).
12. Ein 429 beim Füllen eines Sortier-Schlüssels (Seite 1 oder 2) öffnet den Bestenlisten-Breaker;
    der andere Sortier-Schlüssel liefert `SevenTvRateLimited` **ohne** Upstream mit `RemainingOpenTime` als Haltbarkeit — und
    der #147-Breaker (`ForeignSevenTvBreakerPolicy` per Typ) bleibt **geschlossen**.

**Telemetrie**

13. Je Upstream-Request genau **eine** Beobachtung unter `seventv-leaderboard`; HTTP 200 mit
    `extensions.status: 429` erscheint als Rate-Limit-Ereignis; `x-ratelimit-search-remaining`
    landet in `RateLimitRemaining`. Keine Beobachtung unter `seventv-foreign-preview` oder
    `seventv-rest` für diese Requests, und umgekehrt keine der Fremdkanal-Vorschau unter der neuen
    Quelle.
14. Die Admin-Monitoring-Seite zeigt die neue Call-Source mit übersetztem Label in beiden Sprachen,
    nicht als rohen Schlüssel (F5).

**Herkunft (nach echter Mutation, wie AK 12/13 bei #147)**

15. `POST …/sync-imported` akzeptiert `{ sourceKind: "seventv-leaderboard", sourceChannelName: null,
    leaderboardSort: "TRENDING_DAILY" }` und lehnt mit 400 ab: dieselbe Vokabel **mit** Name, ohne
    `leaderboardSort`, mit `leaderboardSort` außerhalb der Allowlist (`invalid_leaderboard_sort`),
    sowie jede **andere** Vokabel **mit** `leaderboardSort` (`invalid_source_kind`). Die drei alten
    Vokabeln verhalten sich unverändert (bestehende Fälle `AuthFilterMatrixTests.cs:387-447` grün).
16. Ein vollständiger Import aus der Bestenliste erreicht `/sync-imported` nach den 7TV-Mutationen
    mit HTTP 2xx (kein 400 nach erfolgter Mutation — F1/F6-Klasse), und die Audit-Zeile erscheint
    als `importedFromLeaderboard` mit Emote-Zahl **und** dem Sortierlabel („7TV Trend heute"), nicht
    als nackte Emote-Zahl. Geprüft **live** nach echter Mutation (AK 30) **und** in
    `AuditLogQueryServiceTests` (Zeile mit `leaderboardSort` → Kind mit Text; Zeile mit unbekanntem
    Code → nackter `EmoteCount`).
17. Der Bestätigungsdialog nennt für die Bestenliste die Sortierung als Herkunft (E2); für Datei
    und Kanal unverändert.
18. Der bestehende Kollisionshinweis (`nameCollisions`) greift für Bestenlisten-Zeilen genauso wie
    für die beiden alten Quellen und blockiert nichts (Regression — nicht neu zu bauen).

**Frontend**

19. Mit `forcedSortMode` rendert das Raster **kein** Sortier-`<select>`, zeigt Score-Kachel und
    Erklärsatz, trägt den Score im `aria-label` jeder Zelle und ordnet `sortedEmotes` nach dem
    erzwungenen Modus; ohne den Input verhält es sich exakt wie heute — die 13 #147-Fälle bleiben
    ohne Änderung an der Spec-Datei grün.
20. Die drei Caption-Schlüssel-Inputs wirken (der gerenderte Leer-/Trunkierungs-/Hinweistext nutzt
    den übergebenen Schlüssel); ohne sie greifen die heutigen `import.foreignChannel.*`-Schlüssel.
    Zwei Raster-Instanzen haben verschiedene Select-ids.
21. Ein Sortierwechsel im `LeaderboardStep` leert die Auswahl sichtbar: `result()` wird `null`,
    „Weiter" ist gesperrt, das Raster meldet nach dem Neuladen eine leere Auswahl; die Rangliste der
    neuen Sortierung ist danach vollständig sichtbar.
22. Der Dialog bietet drei Optionen; die dritte betritt den `LeaderboardStep`, nennt ihren Titel,
    widert die Pane mit dem Grid, sperrt „Weiter" bis zur ersten Auswahl und schließt mit
    `{ kind: 'leaderboard', picked }`; `import-trigger` startet daraus `startLeaderboardImportFlow`
    mit dem Kanal der Seite als Ziel.
23. `truncated: true` wird gesagt, nicht verschluckt — mit **verschiedenen** Texten für
    `TRENDING_DAILY` (Anteil der Liste) und `TOP_ALL_TIME` (Auswahl aus Millionen), beide mit
    `loaded` und `totalCount`.

**Regeln**

24. **Regel 7:** `invalid_leaderboard_sort` steht in `ApiErrorCodes.cs`, in
    `KNOWN_API_ERROR_CODES` und in beiden Locales; die Budget-Verweigerung bekommt **keinen** neuen
    Code (`foreign_channel_seventv_unavailable`, E13). `api-error-locales.spec.ts` grün.
25. **Regel 3:** Der Commit, der `forcedSortMode` als Vorbelegung auf der Bestenliste einführt,
    enthält den `DECISIONS.md`-Eintrag, der Punkt 5 und P5' (2026-09-10) auf die Beschriftung
    eingrenzt, die In-Process-Grenze des Vorrats (Replica-Bedingung) festhält und vermerkt, dass die
    Label-Angabe in Punkt 5 (`:507`) von Punkt 10 (`:538-546`) überholt ist.
26. **Regel 11:** `tests/EmotePurge.Api.Tests` enthält für die neue `MapGroup`: 401 anonym, 200
    für beide Sortierungen, 400 für fehlenden/kleingeschriebenen/fremden `sortBy` ohne
    Service-Aufruf, **429 bei ungültigem `sortBy` über Budget** (Rate-Limit vor Validierung), die
    503-Abbildung aller Fehlzustände, und die `sync-imported`-Fälle aus AK 15. Die neue Policy steht
    in `RateLimitPolicyBudgetTests`, sofern die Datei die Policies enumeriert.
27. **Regel 12:** co-located Specs für `LeaderboardStep` (Zustandsübergänge, `result()`,
    Sperrgrund), `forcedSortMode` und die Caption-Inputs im Raster, `importOriginLeaderboardSort`,
    `SevenTvLeaderboardService` (`HttpTestingController`), den Bestätigungsdialog-Zweig,
    `renderDetail` für die neue Kind, `buildLeaderboardImportSource` und den `import-trigger`-Zweig.
    Verhalten, keine Vorlage.
28. **E11-Beleg:** Die 21 bestehenden Client-Tests (16 in `SevenTvApiClientEmoteSetPreviewTests`,
    4 in `SevenTvApiClientForeignTelemetryTests`, 1 in `SevenTvApiClientForeignTelemetryStoreTests`;
    `[Fact]` und `[Theory]` gezählt) bleiben **ohne Änderung an den Testdateien** grün; die 9 + 8 + 12
    Tests von `HardenedForeignEmoteSetServiceTests`, `ForeignEmoteSetProviderBudgetTests`,
    `ForeignSevenTvBreakerPolicyTests` ebenso. Die drei Charakterisierungstests aus AK 34 stehen in
    einem Commit **vor** dem Umbau-Commit (T0 vor T1), sind dort gegen den unveränderten Code grün und
    bleiben nach dem Umbau **unverändert** grün.
29. **Kein Zählpfad, kein Join, keine Migration:** `git diff --stat main` zeigt keine Datei unter
    `src/EmotePurge.Worker/`, keine Änderung an `SevenTvSyncService.cs`, `EmoteMatchCache`,
    `UsageFlushWorker` oder `ResolveTwitchUserIdAsync`/`GqlUsersQuery`; keine neue Migration. Dass
    das Worker-Image neu gebaut wird (F6), ist akzeptiert und im PR-Text genannt.
30. **Live gegen echte 7TV-Zugänge (Regel 16):** beide Sortierungen laden; die ersten zehn
    Positionen stimmen mit `7tv.app` überein; ein Sortierwechsel leert die Auswahl; ein
    vollständiger Import von mindestens zwei Emotes, davon eines mit Namenskollision im Zielset;
    Audit-Zeile mit Sortierlabel; danach ein zweites Öffnen ohne neuen Upstream.
31. Über den Abnahmelauf (mindestens eine volle Stunde, Prod) zeigen die Log-Zeilen der
    Bestenliste: ≤ 4 Upstream-Requests je Stunde und ≤ 10 in jedem rollenden 60-min-Fenster,
    **je Prozesslauf**, und `x-ratelimit-search-remaining` liegt **bei jeder Stichprobe der
    Bestenliste** über 90 (E15). **Das belegt nicht** das Minimum des Eimers über alle
    Verbraucher — Worker-Requests zwischen den Stichproben bleiben unsichtbar; dieses Minimum
    gehört zur gemeinsamen Bremse (§16).
32. Gates grün: `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`,
    `npm --prefix web run e2e` (ohne Api auf `:5151`), `node scripts/coverage-local.mjs` vor dem PR.

**Nachträge aus der adversarialen Prüfung (Codex Sol)**

33. **Neustart-Vertrag je Prozesslauf:** Zwei Instanzen von `SevenTvLeaderboardRequestBudget` auf
    demselben `TimeProvider`. Die alte Instanz vergibt 10 Freigaben; innerhalb desselben
    60-min-Fensters entsteht eine neue Instanz; die neue vergibt unabhängig bis zu 10 und verweigert
    die 11. Der Test dokumentiert, dass die Summe beider im Fenster ≤ 20 ist (10 × Prozessläufe,
    Abschnitt 6). Zusätzlich: ein Kaltstart des Service mit gültigen Antworten beider Sortierungen
    (je `pageCount ≥ 2`) verbraucht genau **4** Freigaben.
34. **Charakterisierung des #147-Vorschaupfads vor dem E11-Umbau (T0):** drei neue Tests, gegen den
    heutigen Code geschrieben und grün, bevor `FetchPreviewPageAsync` angefasst wird:
    (1) kaputtes JSON hinter HTTP 200 → genau **eine** Beobachtung unter `SevenTvForeignPreview`
    (heute `SevenTvApiClient.cs:449` vor dem Weiterwerfen, `:440-451`), Ergebnis
    `SevenTvUnavailable`; (2) `Ratelimit-Limit`/`-Remaining`/`-Reset` aus der Antwort stehen in
    `RateLimitLimit`/`RateLimitRemaining`/`RateLimitReset` der Beobachtung (`:468-476`); (3) ein
    **HTTP-Response-Header** `x-ratelimit-search-reset` auf dem Vorschaupfad ändert weder
    `RetryAfterSeconds` noch die Breaker-Öffnungsdauer — heute wird der Hinweis nur aus
    `errors[].extensions.headers` gelesen (`:669-699`), und die Bestenlisten-Variante darf diese
    Auswertung nicht in den Vorschaupfad tragen. Vierte Absicherung, **bestehend, kein neuer Test**:
    die Handler-Suppression ist belegt durch vier `Assert.Single` in
    `SevenTvApiClientForeignTelemetryTests` (Zeilen 40, 66, 90, 106) und
    `Assert.DoesNotContain(… SevenTvRest)` in `SevenTvApiClientForeignTelemetryStoreTests` (Zeile 50).
35. **Ein zusammenhängender Vorrat je Sortierung:** Mit `TimeProvider` und einem Upstream-Stub, dessen
    Seitengrenze sich verschiebt (ein Emote wandert von Position 251 auf 250), (a) zwischen einem
    Fehler von Seite 2 und dem erneuten Versuch und (b) zwischen Ablauf und Neufüllung: die Antwort
    enthält **nie** eine doppelte 7TV-Id, kombiniert **nie** Seiten aus zwei Füllvorgängen (im Fall
    (a) wird Seite 1 neu geholt) und behält die 7TV-Reihenfolge. Zusätzlich: Seite 1 vom Budget
    gewährt, Seite 2 verweigert → ganzer Eintrag `BudgetRefused` mit 30 s, keine Teilliste, die
    Freigabe für Seite 1 bleibt verbraucht.
36. **Vorab-Kontrolle vor dem Stack-Update, pass/fail, von Hand im Admin-Bereich auf Prod:**
    Kanalliste öffnen und notieren, welche Kanäle einen Sync-Fehlergrund zeigen
    (`admin-channels-page.ts:276-284` zeigt `lastSyncFailureReason`). Für jeden davon die
    Detailseite öffnen: ist die Twitch-ID leer (`admin-channel-detail-page.ts:183-189`)?
    **Die Anzahl der Kanäle mit Fehlergrund und leerer Twitch-ID muss 0 sein.** Begründung, kurz:
    Die Auflösung läuft nur, wenn die Twitch-ID fehlt (`SevenTvSyncService.cs:75`), und beim
    ersten Erfolg wird die ID gespeichert (`:146`). Fehlergrund plus leere ID heißt also, die
    Auflösung scheitert dauerhaft. Jeder solche Kanal zieht jede Minute eine Suchabfrage aus dem
    geteilten Eimer (60 pro Stunde) und zählt dabei nichts. Ist die Anzahl nicht 0: diese Kanäle
    zuerst klären. Sonst wartet der Stack-Update bis nach dem 2026-10-07, es sei denn, der
    Betreiber nimmt es ausdrücklich in Kauf. Hinweis: Fehlergrund allein reicht nicht — aus den
    Codes (`SevenTvSyncFailureReasons`) lässt sich nicht unterscheiden, ob die Auflösung oder ein
    späterer Schritt gescheitert ist. Erst die leere Twitch-ID macht es eindeutig.

---

## 10. Testpyramide

| Ebene | Was | Anzahl |
|---|---|---|
| Unit (`Infrastructure.Tests/Unit/`) | `SevenTvLeaderboardStore`, `TimeProvider`-getrieben: Treffer bis Ablauf, Ablauf → eine Füllung, Rennen zweier Leser genau beim Ablauf, `Faulted` und `Canceled` sofort abgelaufen und ersetzt statt entfernt, Füllung überlebt Aufrufer-Abbruch, laufender Eintrag läuft nie ab | +7 |
| Unit | `SevenTvLeaderboardRequestBudget`: 10 ok, 11. verweigert ohne Wartezeit, Fenster gleitet, Verweigerung verbraucht nichts, `usedInWindow` für den Alarm, zwei Instanzen auf demselben `TimeProvider` je ≤ 10 (AK 33) | +6 |
| Unit | Haltbarkeitsregel nach Ausgang: Treffer, leer, `RateLimited` (Klemme unten/oben/ohne Hinweis), `Unavailable`, Breaker offen, Budget | +7 |
| Unit | Charakterisierung #147-Vorschaupfad (T0, AK 34): kaputtes JSON → eine Beobachtung + `Unavailable`, `Ratelimit-*` in der Beobachtung, HTTP-`x-ratelimit-search-reset` ohne Wirkung | +3 |
| Unit | Client `SearchEmotesAsync`: Seite parsen (`totalCount`, `pageCount`, `animated` → Bildadresse), literales 429, 429 hinter HTTP 200, `x-ratelimit-search-*` erfasst, `RetryAfter`-Reihenfolge, Validierungsablehnung → `Unavailable`, genau eine Beobachtung je Request unter der neuen Quelle | +8 |
| Unit | `SevenTvLeaderboardService` mit Client-Ersatz: zwei Seiten zusammengesetzt und nach Id dedupliziert, `truncated`, Seite 2 nur bei `pageCount ≥ 2`, Fehlschlag Seite 2 → ganzer Eintrag Fehler, Breaker-Ausbreitung über die Sortier-Schlüssel bei geschlossenem #147-Breaker, Budget verweigert → kein Upstream + 30 s + keine Breaker-Meldung, Alarm bei 6, Kaltstart = 4 Freigaben (AK 33), verschobene Seitengrenze nach Seite-2-Fehler und nach Ablauf (AK 35 a/b), Seite 2 vom Budget verweigert (AK 35) | +12 |
| Integration (`Infrastructure.Tests/Integration/`) | `AuditLogQueryServiceTests`: `seventv-leaderboard` mit Code → `ImportedFromLeaderboard` mit Text; unbekannter Code → `EmoteCount`; `seventv-channel` unverändert | +2 |
| Integration | Telemetrie-Store: neue Call-Source erscheint mit Minuten-/24-h-Zählern und Header-Sample | +1 |
| `Api.Tests` | Filter-Matrix der neuen `MapGroup`: 401 / 200 × 2 / 400 × 3 ohne Service-Aufruf / **429-vor-400 über Budget** / 503-Abbildung | +8 |
| `Api.Tests` | `sync-imported`-Vertrag: Vokabeltabelle aus F1 in allen Richtungen (AK 15), Weitergabe von `leaderboardSort` an `MarkImportedAsync` | +6 |
| Vitest (`web/`) | Raster: `forcedSortMode` (kein Select, Kachel, Hinweis, `aria-label`, Ordnung), Caption-Inputs, instanzeindeutige id | +6 |
| Vitest | `LeaderboardStep`: lädt beim Betreten, Sortierwechsel leert Auswahl + `result()` `null`, Fehlerzustand, Leerzustand, `truncated`-Schlüssel je Sortierung, Fokus | +7 |
| Vitest | Dialog: dritte Option, Titel, `gridVisible` aus dem Bestenlisten-Step, „Weiter" gesperrt/frei, Schließen mit `leaderboard`-Ergebnis | +4 |
| Vitest | Herkunft: `importOriginSourceChannelName` → `null`, `importOriginLeaderboardSort`, `reportImported` sendet beide Felder, Bestätigungsdialog-Zweig, `nameCollisions`-Regression, `renderDetail` übersetzt den Code, `buildLeaderboardImportSource`, `import-trigger`-Zweig, `SevenTvLeaderboardService` (`HttpTestingController`) | +10 |
| Playwright E2E | Ein Durchlauf Bestenliste → Auswahl → Bestätigung → `sync-imported` mit `leaderboardSort` im Body (gemocktes `/api/**`); ein Fehlerfall (503) | +2 |

Nur **Verhalten**, keine Vorlage (Regel 12). Übersetzungswortlaut identifiziert eine Meldung, ist
nicht der Prüfgegenstand. **Summe: +89.**

**Coverage:** T2, T3 und T6 legen überwiegend **neue** Dateien an — dort ist `coverage-local.mjs`
nah an Sonars Messung, und die 80-%-Schwelle auf neuem Code beißt (`analyze` ist required check).
Der Eingriff in `SevenTvApiClient.cs` (T1) ist der Fall „kleine Änderung in großer Datei", wo die
lokale Näherung am schwächsten ist — die neuen Zeilen dort sind durch die +8 Client-Tests gedeckt, das Bestandsverhalten zusätzlich
durch die +3 Charakterisierungstests aus T0.

---

## 11. Aufwand

| Task | Mensch | CC |
|---|---|---|
| T0 Charakterisierungstests #147-Vorschaupfad (Test-Anteil in der Testzeile) | ~0,5 h | ~5 min |
| T1 Client: Seitenabruf herausziehen, Suchabfrage, Header, Call-Source | ~4 h | ~30 min |
| T2 Vorrat + Fensterbudget + Haltbarkeitsregel, pure, mit Tests zuerst | ~5 h | ~30 min |
| T3 Service, Endpunkt, Filter, Policy (3 Stellen), Fehlercode, DI | ~4 h | ~25 min |
| T4 Herkunft: Backend-Tabelle, Persistenz, Projektion, Frontend-Union, Audit-Anzeige | ~4 h | ~25 min |
| T5 Raster-Inputs | ~2 h | ~15 min |
| T6 Step, Dialog, Fluss, `DECISIONS.md` | ~5 h | ~30 min |
| Tests über alle Ebenen (+89) | ~6,5 h | ~45 min |
| Live-Verifikation inkl. 1-h-Abnahmelauf (Regel 16, AK 30/31) | ~2 h | — |
| **Summe** | **~33 h** | **~3,4 h** |

Gegenüber #147 (~29 h) teurer trotz kleinerer Oberfläche: der Vorrat ist eine neue pure Klasse
mit Nebenläufigkeitsfällen, und die Herkunft braucht ein zusätzliches Leitungsfeld.

---

## 12. Rollback

Keine Migration, kein Worker-Code, kein persistierter Zustand: Vorrat, Budget und Breaker leben im
Prozess und sind nach dem Neustart weg. Ein Revert des PR entfernt Endpunkt, Policy, dritte Option
und die vier Raster-Inputs.

**Eine Nachwirkung bleibt:** bereits geschriebene Audit-Zeilen tragen
`sourceKind: "seventv-leaderboard"` und `leaderboardSort` dauerhaft. Nach einem Revert fiele
`ProjectDetail` für sie auf den nackten `EmoteCount`-Zweig zurück (`:189-192`). Anzeigequalität,
kein Datenverlust — und der Grund, warum die Vokabel und der Feldname **vor** dem ersten
Produktionslauf feststehen (E8) und nicht später umbenannt werden.

**Eine zweite Nachwirkung ist keine:** das Worker-Image, das der PR neu baut (F6), enthält keinen
ausgeführten neuen Pfad; ein Revert baut es erneut, mehr nicht.

---

## 13. Dateireferenz

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` | zweite `MapGroup` `/api/seventv/leaderboard` neben `:25-28` |
| `src/EmotePurge.Api/Validation/LeaderboardSortValidationFilter.cs` | **neu**, `IEndpointFilter` nach dem Muster von `ChannelNameValidationFilter` |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | `InvalidLeaderboardSort = "invalid_leaderboard_sort"` |
| `src/EmotePurge.Api/RateLimiting/RateLimitPolicyNames.cs` | `SevenTvLeaderboard` |
| `src/EmotePurge.Api/RateLimiting/RateLimitingOptions.cs:56`, `:64-72` | Policy `SevenTvLeaderboard` (20/min) **und** ihr `Validate()`-Aufruf |
| `src/EmotePurge.Api/Program.cs:188` | `AddFixedWindowPolicy(RateLimitPolicyNames.SevenTvLeaderboard, …)` daneben |
| `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs:142`, `:164`, `:175-176`, `:262` | vierte Vokabel, Vokabeltabelle statt Binärabgleich, Weitergabe von `leaderboardSort`, `SyncImportedRequest` |
| `src/EmotePurge.Core/Services/ISevenTvLeaderboardService.cs` | **neu** (Regel 4/5) — Sortier-Allowlist als Enum/Konstanten, Ergebnistyp, Statusenum |
| `src/EmotePurge.Core/Services/IEmoteService.cs:39` | `MarkImportedAsync` um `leaderboardSort` |
| `src/EmotePurge.Core/Services/IAuditLogQueryService.cs:20-30` | `Kinds.ImportedFromLeaderboard` |
| `src/EmotePurge.Core/Services/IRateLimitTelemetry.cs:88-109` | `RateLimitCallSources.SevenTvLeaderboard` |
| `src/EmotePurge.Core/SevenTv/ISevenTvApiClient.cs` | `SearchEmotesAsync(sortBy, page, ct)` |
| `src/EmotePurge.Core/SevenTv/SevenTvModels.cs` | Ergebnistyp der Suche (Seite: `TotalCount`, `PageCount`, Items als `SevenTvEmoteSetPreviewItem` mit `Alias = DefaultName`, oder eigener Typ) |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:410-476` | gemeinsamer v4-Seitenabruf mit Call-Source-Parameter (E11); neue Suchabfrage; `x-ratelimit-search-*`; `RetryAfter`-Reihenfolge; `BuildForeignImageUrl :750-753` wiederverwenden. **`:18` und `:72-118` nicht anfassen** |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiDtos.cs` | Such-DTOs (flaches `Emote`, F7) |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvLeaderboardStore.cs` | **neu**, pur, `TimeProvider` |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvLeaderboardRequestBudget.cs` | **neu**, pur, `TimeProvider` (E10) |
| `src/EmotePurge.Infrastructure/Services/SevenTvLeaderboardService.cs` | **neu** — Füllpfad aus Abschnitt 6 |
| `src/EmotePurge.Infrastructure/Services/EmoteService.cs:107`, `:129` | `leaderboardSort` in `DetailsJson` |
| `src/EmotePurge.Infrastructure/Services/AuditLogQueryService.cs:30-32`, `:155-185` | vierte Vokabel, Zweig `ImportedFromLeaderboard` **vor** der `source`-Prüfung |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs:99-116` | Keyed-Breaker, Store, Budget, Service |
| `web/src/app/core/seven-tv/leaderboard.model.ts` | **neu** — `LeaderboardSort`, Antwort, `LeaderboardImportResult` |
| `web/src/app/core/seven-tv/seven-tv-leaderboard.service.ts` (+ spec) | **neu** — ohne `refresh` |
| `web/src/app/core/seven-tv/import-source.ts:12-38`, `:53-61` | vierter Zweig, `importOriginLeaderboardSort`, Docstring |
| `web/src/app/core/emotes/emote-admin.service.ts:37-38` | Union + `leaderboardSort` |
| `web/src/app/core/seven-tv/seven-tv-import.service.ts:303-304` | beide Felder über die Hilfsfunktionen |
| `web/src/app/core/audit/audit.model.ts:32-33` | `importedFromLeaderboard` |
| `web/src/app/core/i18n/api-error.ts` | `invalid_leaderboard_sort` |
| `web/src/app/shared/audit/audit-actions.ts:51-57`, `audit-row.ts:53-68` | Schlüssel, Code-Übersetzung für die eine Kind |
| `web/src/app/shared/seven-tv/leaderboard-step.ts` (+ spec) | **neu** |
| `web/src/app/shared/seven-tv/foreign-emote-grid.ts:145-150`, `:171`, `:208`, `:269-272`, `:280-317`, `:367-395` | vier Inputs, effektiver Modus, instanzeindeutige id |
| `web/src/app/shared/seven-tv/import-source-dialog.ts:37-66`, `:110-180`, `:192-252` | dritte Option, Step, Ergebnis, `gridVisible`, „Weiter" |
| `web/src/app/shared/seven-tv/foreign-import-flow.ts` | `startLeaderboardImportFlow`, `buildLeaderboardImportSource` daneben |
| `web/src/app/shared/seven-tv/import-trigger.ts:120-125` | dritter Zweig |
| `web/src/app/shared/seven-tv/import-confirm-dialog.ts:76-92`, `:290-298` | Herkunftszweig für die Bestenliste |
| `web/public/i18n/de.json`, `web/public/i18n/en.json` | alle Schlüssel aus Abschnitt 7, `:350-354` Call-Source, `:1097`-Nachbar für den Fehlercode |
| `docs/DECISIONS.md` | Eintrag: Vertrag P5'/Punkt 5 eingegrenzt, Label-Nachtrag, In-Process-Vorrat + Replica-Bedingung, vierte Vokabel + `leaderboardSort` (Regel 3) |
| `tests/EmotePurge.Api.Tests/` | Filter-Matrix der neuen Gruppe, `sync-imported`-Vokabeltabelle |
| `tests/EmotePurge.Infrastructure.Tests/Unit/`, `Integration/` | Fälle aus Abschnitt 10 |
| `web/e2e/emote-import.e2e.spec.ts` | zwei Fälle |

**Nicht angefasst:** `src/EmotePurge.Worker/**`, `SevenTvSyncService.cs`,
`ResolveTwitchUserIdAsync`/`GqlUsersQuery`, `GetChannelStateForTwitchUserAsync`,
`ForeignEmoteSetProviderBudget`, `ForeignEmoteSetCache`, `ForeignEmoteSetRequestCoalescer`,
`HardenedForeignEmoteSetService`, `ForeignEmoteSetService`, `ForeignChannelStep`,
`ImportTargetDialog`, `usage-stats-page.*`, `already-present-filter.ts`, `import-preview.ts`.

---

## 14. Wiederverwendet — als Prüfliste, nicht als Behauptung

Bei #147 saßen drei von fünf Review-Befunden in Code, den die Spec als unverändert deklariert
hatte. Jede Zeile hier trägt deshalb entweder einen **Beleg** (heute nachgeprüft) oder eine
**Prüfaufgabe**, die vor dem betreffenden Task abgearbeitet wird.

| Baustein | Anspruch | Status |
|---|---|---|
| `ForeignEmoteRow` (Core `:135-141`, Web `foreign-emote-set.model.ts:15-24`) | trägt Bestenlisten-Zeilen ohne Kanalbezug | **Belegt:** kein Kanalfeld; `name`/`defaultName`/`imageUrl`/zwei Scores genügen. `name = defaultName` ist die einzige Setzung |
| `ForeignEmoteGrid` | trägt die Liste mit den vier neuen Inputs, ohne Regression | **Prüfaufgabe T5:** die 13 Fälle setzen keinen Input (`foreign-emote-grid.spec.ts:92-94`) — Regression belegt; die neue Deckung kommt aus den +6 Fällen. Offen bis T5: dass `sortedEmotes` mit `forcedSortMode` und `ListSelection` keinen Schlüssel über einen Listenwechsel behält, wenn der Step das Raster neu instanziiert (E14) |
| `ListSelection<ForeignEmoteRow>` | Einzelauswahl, Shift-Bereich | **Belegt:** unverändert; die Auswahl-über-Listenwechsel-Falle wird durch Neuinstanziierung im Step umgangen, nicht in der Klasse gelöst (`list-selection.ts:39-42`) |
| `buildImportPreview` / `nameCollisions` | Kollisionshinweis greift | **Belegt:** wirkt auf `ImportRow.name`, quellunabhängig (`import-preview.ts:12`, Dialog `:198-203`); Regressionsfall AK 18 |
| `already-present-filter.ts` | filtert bereits vorhandene Emotes | **Belegt:** nach 7TV-ObjectID (`:7-15`), unabhängig vom Namen; kein Beitrag zur Kollision, kein Umbau |
| `ForeignEmoteSetCache`, `ForeignEmoteSetRequestCoalescer` | werden **nicht** gebraucht | **Belegt:** beide auf `ForeignEmoteSetLookupResult` typisiert (`ForeignEmoteSetRequestCoalescer.cs:32`); der Vorrat koalesziert selbst und ist in-process (Entwurf). Kein Umbau |
| `ForeignEmoteSetProviderBudget` | bleibt unangetastet (E10) | **Belegt** durch Entscheidung; Prüfung am Diff (AK 28) |
| `ForeignSevenTvBreakerPolicy` | zweite Instanz per Keyed-Registration, Klasse unverändert | **Prüfaufgabe T3:** die Klasse hält ihren Zustand in Instanzfeldern (`:99-110`), nicht statisch — nachgeprüft (`_open`, `_openUntil`, `_probeInFlight`, `_consecutiveFailures`, `_generation` sind Instanzfelder, `_gate` ist ein Instanz-`Lock`); die Keyed-Registration ist damit ausreichend. Offen bis T3: dass `HardenedForeignEmoteSetService` weiter die **typisierte** Instanz bekommt (`ServiceCollectionExtensions.cs:113`) und nicht versehentlich die keyed |
| `SevenTvApiClient` — Preview-Pfad | bleibt in Verhalten identisch nach dem Herausziehen (E11) | **Prüfaufgabe T0/T1:** 21 bestehende Tests plus die drei Charakterisierungstests aus T0 unverändert grün (AK 28, AK 34); zusätzlich Sichtprüfung, dass `RecordForeignPreviewObservation` für den Preview-Pfad weiter `SevenTvForeignPreview` meldet |
| `ProviderRequestTelemetryHandler` | Suppression funktioniert für den neuen Pfad | **Belegt:** der Schlüssel ist pro Request (`:59`), nicht pro Client; dieselbe Setzung wie `:418` |
| `RateLimitTelemetryStore` | zählt die neue Call-Source ohne Änderung | **Belegt:** dimensioniert über `(provider, callSource)`-Strings, keine Allowlist im Store (`:174-175`); +1 Integrationsfall bestätigt es |
| `ImportTargetDialog` | nicht im Fluss | **Belegt:** #147 hat den Ziel-Dialog aus dem Fremdkanal-Fluss entfernt (`import-trigger.ts:121-123`); `forcedScope` (`import-target-dialog.ts:20-25`) bleibt dem Dock-Shortcut |
| `startImportFlow`, `SevenTvRunEngine`, Token-Prompt, `dedupeImportRows` | unverändert | **Prüfaufgabe T6:** `buildForeignImportSource` (`foreign-import-flow.ts:38-46`) ist die Vorlage; sie liest `picked.channelName` — das neue Gegenstück darf **kein** Feld aus `ForeignChannelImportResult` erwarten. Sonst belegt: der Fluss kennt nur `ImportSource` |
| `ChannelNameValidation` | unbeteiligt | **Belegt, mit Falle:** unbeteiligt am Endpunkt, aber beteiligt an `sync-imported` (`:150`) — genau deshalb E8 |

---

## 15. Nicht in dieser Runde

Aus dem Entwurf übernommen und hier verbindlich: **Stufe 3 des Tickets** (Suchfeld, Tags, Filter —
nicht vorratsfähig, unbegrenzter Schlüsselraum), `TRENDING_WEEKLY` (kein Zahlfeld auf der Kachel),
Blättern über 500 hinaus, ein `refresh`-Bypass, ein eifrig gewärmter Vorrat (Ansatz B, kein Hosted
Service in der Api), ein Redis-Vorrat, `Emote.channels.totalCount`, Worker-Änderungen, zusätzliche
Joins, Migrationen.

Dazu die Entscheidungen des Betreibers:

- **Kein gemeinsames Request-Budget für Api und Worker** (E1) — eigenes Folge-Issue nach dem
  Messfenster, s. Abschnitt 16.
- **Overlay-Emotes (`defaultZeroWidth`) werden nicht gekennzeichnet** (E4) — **bekannte
  Einschränkung**: 5 von 250 Zeilen der Tagesliste sind Overlays und erscheinen im Raster wie alle
  anderen; wer eines importiert, bekommt ein Overlay. Die Fremdkanal-Ansicht verhält sich genauso.
- **Idee C — Beliebtheitszahlen an den eigenen Emotes als Löschhilfe** (E7) — bleibt eine Notiz im
  Entwurf, wird **kein** Issue.
- **Kein gespeichertes Minimum von `x-ratelimit-search-remaining` im Telemetrie-Store** (E15) —
  Log-Zeilen tragen die Abnahme; ein Store-Minimum gehört zur gemeinsamen Bremse.
- **Messung (b) aus dem Entwurf** (Sperrradius: trifft eine Sperre auch `userByConnection`
  `SevenTvApiClient.cs:204-205`, `GET /v3/users/twitch/{id}` `:124`, `emoteSet(id:)` `:28-29`?) —
  teuer, weil Devbox und Arbeitsrechner denselben Heimanschluss teilen und das Frontend 7TV direkt
  aus dem Browser aufruft; misst nur den Schaden einer Überziehung, die dieses Feature nicht
  auslösen kann. Wandert in das Folge-Issue.

---

## 16. Folge-Issues

1. **Gemeinsame Bremse für Api und Worker gegen 7TVs Such-Eimer (Issue #165) — fällig
   nach dem Messfenster (ab 2026-10-08).** Trägt:
   - ein **providerweites, fail-closed wirkendes Budget**, das Worker-Auflösung und Bestenliste
     vor **jedem** Request belastet — über alle Aufrufer von `emotes.search` (v4) **und**
     `users(query:)` (v3, `ResolveTwitchUserIdAsync`, `SevenTvSyncService.cs:75`), weil der Eimer
     gemessen geteilt ist;
   - das **gespeicherte Minimum** von `x-ratelimit-search-remaining` über **alle** Verbraucher —
     heute behält `RateLimitTelemetryStore` nur `LastHeaderSample`, und der v3-Handler liest
     Twitchs Schreibweise `Ratelimit-*`, nicht 7TVs;
   - einen **Umgang mit dauerhaft nicht auflösbaren Kanälen** (Schwelle oder Backoff statt fester
     60 s) — jede solche Zeile (`LastSyncFailureReason`, Admin-Kanalliste) zieht heute jede Minute
     eine Suchabfrage, das Sechsfache des Bestenlisten-Budgets, ohne etwas zu zählen (AK 36 ist die
     Vorab-Kontrolle dafür, nicht der Fix); Messung (b) vorab.

   Fasst Code an, den der Worker ausführt, deshalb nicht früher.

Kein weiteres. Idee C ist ausdrücklich keines (E7); die Overlay-Kennzeichnung auch nicht (E4).

---

## 17. Risiken

| Risiko | Umgang |
|---|---|
| Doppel-Fill beim Ablauf verdoppelt den Erwartungswert still | `TryUpdate`-Vertrag, AK 9, Alarm bei 6 (AK 6) macht 8/h sichtbar |
| Ein 429 hinter HTTP 200 wird als leere Bestenliste gelesen — und **eine Stunde** gecacht | AK 13, +8 Client-Tests; das ist der teuerste Einzelfehler, mit längerer Nachwirkung als bei #147 |
| Fehler „sofort entfernen" statt negativ cachen → Deckel weg auf fehlschlagendem Schlüssel | Haltbarkeitstabelle, AK 7/10; Store-Tests vor dem Service (T2) |
| Füllung unter dem Aufrufer-Token → wegnavigierender Browser killt den Vorrat für alle | AK 11 |
| Sortierung in `SourceChannelName` → 400 **nach** der Mutation | E8/F1, AK 15/16; Live-Verifikation AK 30 ist kein Formalismus |
| Herkunft geht im Audit-Log verloren (`ProjectDetail` fällt auf `EmoteCount`) | F1 Station 5, AK 16 |
| Der #147-Breaker schließt mit, weil die Instanz geteilt wird | Keyed-Registration, AK 12 und Prüfliste |
| Neue Call-Source erscheint im Admin als roher Schlüssel | F5, AK 14 |
| `flags.animated` vergessen → schwere Bilder oder 404 | F7, Client-Test |
| Eingriff in `SevenTvApiClient.cs` bricht den #147-Pfad | E11 mit 21 unveränderten Tests als Beleg, AK 28 |
| Zweite Api-Replica → Deckel `Replicas × 10` | Bedingung im `DECISIONS.md`-Eintrag (AK 25); heute eine Replica, `docker-compose.prod.yml:72-74` |
| Worker-Neustart beim Stack-Update mitten im Messfenster | F6, E6: Deploy bündeln; Epic #118 setzt das Fenster dadurch nicht zurück |
| Nutzer liest den Score als Kanal-Beliebtheit | E3-Bedeutungsvertrag, Beschriftung netzwerkweit, AK 19/25 |
| Die Rangfolge im Raster weicht von 7TVs ab (Sortierung nach Score-Feld statt nach gelieferter Reihenfolge) | AK 30 prüft die ersten zehn Positionen live; weicht sie ab, wird `sortedEmotes` bei `forcedSortMode` auf die gelieferte Reihenfolge festgelegt (kleiner Eingriff in T5, kein Vertragsbruch) |
