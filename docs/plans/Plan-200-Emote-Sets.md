# Plan #200 — Emote-Sets: nicht-aktive Sets ansehen, bearbeiten, übertragen, pro Set zählen

Erstellt am 2026-09-20 gegen `docs/konzept-emote-sets` = `93af613` (der Commit, der die Spec
enthält). Quellen: [die Spec](../superpowers/specs/2026-09-20-emote-sets-200-spec.md) (Abschnitte
werden als „Spec N" zitiert, Entscheidungen als E1–E25, Fallen als F1–F17, Akzeptanzkriterien als
AK n), [das Konzept](../Konzept-Emote-Sets-2026-09-19.md) nur, wo die Spec darauf verweist,
`CLAUDE.md` (Regeln 1–22, Schichtentreue, Gates), `web/.claude/CLAUDE.md`, und der Code auf
`93af613`. Formatvorlagen: [Plan-149](Plan-149-7TV-v4-Schreibflaeche.md), [Plan-91](Plan-91-Datei-Weg.md).

**Die Spec ist die einzige Vertragsquelle. Dieser Plan trägt Reihenfolge, Abhängigkeiten,
Handgriffe und Gates.** Kein Wire-Format, kein Shape, kein Fehlercode, keine Schema- oder
Indexdefinition, keine Zustandsmatrix und keine Beschriftungsregel steht hier noch einmal — an ihrer
Stelle steht ein Verweis der Form „Vertrag: Spec 6.5, E3". Findet jemand beim Umsetzen eine
Abweichung zwischen beiden Dokumenten, gilt die Spec — und der Fund wird gemeldet, nicht
stillschweigend aufgelöst. Was hier steht und **nicht**
in der Spec: Task-Schnitt, betroffene Dateien mit Zeilen, Testerwartung je Task, Modellwahl, Gates,
Rückweg und die Betreiber-Handgriffe mit fertigen Kommandozeilen. Der Plan enthält keinen Code.
(Der Rückschnitt auf diese Arbeitsteilung ist am 2026-09-20 erfolgt — Abschnitt 9.)

---

## 0. Ausgangslage

### 0.1 Was feststeht

- **Der Schnitt ist entschieden** (E25, Spec 12): sieben Kind-Issues K0–K7 entlang der Bauschritte
  3–8 des Konzepts, zwei davon ohne Code (K0 Vorbedingungen, K7 Wartungsfenster). Dieser Plan
  gruppiert seine Tasks danach und übernimmt die Task-Nummern aus Spec 13 (T1.1 … T7), verfeinert
  nur dort, wo ein Schritt für einen Subagent zu groß war (T1.3a/b, T2.5a/b, T4.0 neu, T0.6 neu).
- **Vier DECISIONS-Einträge** (Spec 23) mit festgelegten Commits — Regel 3 gilt je Commit, nicht je
  PR. Die Zuordnung steht in 1.2.
- **Zeitbezug:** HandOfBlood wechselt am **2026-10-01**; der bindende Harness-Lauf ist am
  **2026-10-08**; der Deploy (K7) liegt **dahinter**. Die Werte der Zuordnungsliste (Grenze,
  `ExpectedArchivedCount`) entstehen erst am 01.10. — seit dem 2026-09-20 trägt sie **kein Task**
  mehr ein: die Klassifikation liegt in einer gitignorierten Datei (Spec E1), das Füllen ist der
  Betreiber-Handgriff **V5**, und nur **T1.10** wartet darauf.
- **Zwei adversariale Codex-Runden am 2026-09-20 sind eingearbeitet** — Runde 1 (fünf Befunde) in
  Abschnitt 7, **Runde 2 (sechs Befunde) in Abschnitt 8**. Aus Runde 2 stammen die drei
  Änderungen, die den Plan am stärksten betreffen: der **Datenstand der Bestätigung** samt
  Prüfung 7 und einer zweiten gitignorierten Datei (T1.3a/b, V5, K7), die **XOR-Invariante**
  (T1.3a/b, T1.10), die **Operationskennung im Breaker** (T2.1), der **Abbruchpunkt im Fenster**
  (T1.10, K7), der **Hash der Klassifikationsdatei** (V5, T1.10, K7) und das **gemeinsame Tor**
  für beide Übergangsfelder (K7).
- **Die Zuordnungsliste kommt nicht ins Repo** (Vertrag: Spec E1, 4.2 „Wo die Liste liegt";
  Entscheidung des Betreibers vom 2026-09-20, Nachtrag N3 in 7). Folgen **im Plan**: **T1.3b** baut
  den Mechanismus, **T1.9 entfällt als Commit**, **V5** ist neu in K0, und T1.10 hängt an V5 statt
  an T1.9.
- **#76 läuft auf einem eigenen Branch vor diesem Vorhaben** (Entscheidung des Betreibers vom
  2026-09-20). Die Spec setzt #76 nicht voraus (Spec 22, erste Zeile), aber T1.5 und T1.2 fassen
  `SevenTvSyncService.cs:74-109` an — genau die Stelle, an der #76 die Plausibilitätssperre hält.
  Folge für den Branch: s. 0.4.
- **Drei der T0-Aufgaben sind gemessen** (2026-09-20: T0.1, T0.5, T0.6), die übrigen nicht. Der
  Plan hängt an keiner: jede offene Sonde hat in der Spec zwei Zweige, und die Tasks bauen den
  Zweig, den die Spec als Beschluss trägt (E12 60 s, 8.9 Duplikate ausgenommen). Der andere Zweig
  ist je Sonde als **Umbaukosten** beziffert (K0, am jeweiligen T0.x), damit „warten oder bauen" eine
  Zahl hat. **E7 ist durch T0.5 entschieden und trägt jetzt v4** (Vertrag: Spec E7, E21, Sonde 7) —
  T2.1 baut keinen v3-Zweig mehr, auch nicht für die aktive Set-ID.

### 0.2 Was der Plan beim Nachprüfen am Code gefunden hat

Zwei Punkte hat die Spec ausdrücklich als Prüfaufgabe an den Plan gegeben. Beide sind geprüft;
beide werden Tasks, nicht Fußnoten.

| Prüfaufgabe | Befund am Code (`93af613`) | Folge |
|---|---|---|
| **Gibt `core/routing/list-query-state` das Muster für den Set-Zustand in der URL her?** (Spec 8.1: „Zustand in der URL wie der Zeitraum") | **Die Prämisse stimmt nicht:** der Zeitraum der Nutzungsseite steht heute **nicht** in der URL. `usage-stats-page.ts` hat keinen `ActivatedRoute`-/`queryParamMap`-Zugriff; `rangePreset` (`:333`) ist ein lokales Signal. `listQueryState` (`core/routing/list-query-state.ts`, 142 Zeilen, 236 Zeilen Spec) ist für **paginierte Listen** gebaut (`page` + Filterparameter, `setParams` springt auf Seite 1, `replaceUrl`, Defaults werden aus der URL entfernt) und wird von fünf Listenseiten benutzt, nicht von der Nutzungsseite. | **T4.0** entscheidet zwischen zwei Wegen (s. dort). Das Set wäre der **erste** URL-getragene Zustand der Nutzungsseite; die Spec-Formulierung „wie der Zeitraum" ist damit ein Vorbild ohne Bestand und wird im DECISIONS-Eintrag 4 berichtigt. Ob der Zeitraum nachziehen soll, ist **nicht** Teil dieses Plans (3). |
| **Liefert `editor_of { user { id } }` die 7TV-ID des Account-Besitzers?** (Spec 19, Prüfaufgabe T2.4) | `GqlEditorOfQuery` (`SevenTvApiClient.cs:58`) liest heute `editor_of { user { connections { platform id username } } }` — das `user`-Objekt ist also schon der Besitzer, nur sein `id` wird nicht projiziert. Die Frage ist damit nicht „welches Objekt", sondern „trägt v3 dort ein `id`, und ist es die 7TV-ObjectID". Das misst nur ein Aufruf. | **T0.6 — gemessen am 2026-09-20:** `editor_of { user { id … } }` liefert je Grant `user.id` (7TV-Account-ID, z. B. `01FY9A4ZG8000BH1HKPGP0R1S0`) **und** die Connections; das `id`-Feld am `user` existiert. T2.4 bekommt die redigierte Antwort als **verifizierte** Fixture, der Vorbehalt „unverifiziert" entfällt. |

Dazu drei Beobachtungen, die den Schnitt beeinflusst haben:

- **Der Zählpfad (T1.1–T1.4) hat keinen grünen Zwischenzustand ohne Hilfsgriff.** `IUsageStatFlushService.FlushAsync` wechselt seine Signatur mit dem Schlüssel (Spec 5), und der Flush kann drei Spalten erst schreiben, wenn die Migration den Index getauscht hat (F1). Der Plan legt deshalb **einen** Übergang fest, der grün ist und keinen Wegwerfcode über drei Zeilen kostet: T1.1 stellt den Schlüssel im Worker und die neue Flush-Signatur um, der Flush **summiert vorläufig je `EmoteId`** und schreibt weiter zweispaltig; T1.4 ersetzt genau diese Summierung. Zwischen T1.1 und T1.2 ist der Build rot (der Chat-Pfad ruft noch `GetChannelEmotes`) — beide landen deshalb in **einem** Commit.
- **`SevenTvSyncService.cs` (543 Zeilen) und `usage-stats-page.ts` (1.904 Zeilen) sind die zwei Dateien, an denen die lokale Coverage-Näherung am schwächsten ist** (Spec 15, letzter Absatz; CLAUDE.md „Tests"). Die Tasks, die sie anfassen (T1.2, T1.5, T4.2–T4.5), nennen deshalb ihre Fälle einzeln, und T7 liest die lokale Zahl als Anlass hinzusehen, nicht als Urteil.
- **Die Spec beziffert die rot werdenden Bestandstests nur teilweise** (sicher: 77; Rest „beim Umstellen zählen"). Der Plan macht das je Task zur Erwartung (Datei, Fälle, Zahl) und die Zählung dort, wo die Spec sie offen lässt, zur **Teilaufgabe** des Tasks — das Ergebnis steht im PR-Text, nicht in einem offenen Punkt.

### 0.3 Modelle je Task

`haiku` für mechanische Umbauten und Inventuren, `sonnet` für klar spezifizierte Implementierung
und Tests, `opus` nur an den heiklen Stellen — mit Begründung am Task. Betreiber-Handgriffe (K0, K7)
haben kein Modell: sie sind fertige Kommandozeilen mit Platzhaltern, die der Betreiber ausführt.
**Dieser Plan führt keinen davon aus und verbindet sich nirgends.**

### 0.4 Branch und Commits

- **Integrationsbranch `feat/emote-sets-200`**, aufgesetzt auf `origin/main` **nach** dem Merge von
  #76. Ist #76 beim Start von K1 noch nicht gemergt, beginnt K1 trotzdem (T1.1, T1.3a/b, T1.4
  berühren die Sperre nicht) und rebased **vor T1.2 und T1.5** auf den Stand mit #76 — die beiden
  Tasks liegen in denselben Zeilen wie die Sperre.
- **Je Kind-Issue ein PR gegen den Integrationsbranch**, nicht gegen `main`. Grund: `main` ist
  deploybar, und ein gemergtes K1 auf `main` hieße, dass der nächste Hotfix-Deploy vor dem
  2026-10-08 ein Image mitbrächte, das am `PendingMigrationGuard` (S3-34) abbricht. Der eine PR auf
  `main` ist T7 — nach dem 08.10., mit der Zweitmeinung aus Regel 22 über das Ganze. Die
  Kind-Issue-PRs bekommen zusätzlich je eine `/codex:review --model gpt-5.6-sol --scope branch
  --base feat/emote-sets-200`, weil ein 100-Stunden-Diff in einem einzigen Review nicht mehr
  lesbar ist. (Planentscheidung, keine Spec-Vorgabe; vom Betreiber am 2026-09-20 bestätigt.)
- **K1 und K2 laufen parallel in getrennten Worktrees** (`superpowers:using-git-worktrees`); die
  Tasks *innerhalb* eines Kind-Issues laufen sequenziell im selben Worktree, weil mehrere davon
  auf einen gemeinsamen Commit warten. Bekannte Fallen: `docker compose` aus einem Worktree reißt
  den Dev-Stack ab (nur bauen, nicht starten); eine Worktree-Api verwirft das lokale Cookie
  (DataProtection-Name hängt am Pfad) — Live-Verifikationen (T1.7, T2.7, T5.3) laufen deshalb aus
  dem Haupt-Checkout oder mit eigenem Port.
- **Conventional Commits, englisch, ohne `#`-Referenzen.** Die Commit-Vorschläge stehen je Task;
  wo mehrere Tasks in einen Commit gehören, steht es dabei. Rule 3: der DECISIONS-Eintrag liegt im
  Commit, der den Vertrag ändert, und wird von dem Subagent geschrieben, der den Vertrag baut.

---

## 1. Vertragsänderungen

### 1.1 Wo die Verträge stehen

Nichts davon wird hier wiederholt. Je Vertrag: Spec-Abschnitt, die Tasks, die ihn tragen.

| Vertrag | Spec | Tasks |
|---|---|---|
| Schema, Migration mit **sieben** Prüfungen über einer lückenlosen Klassifikation (inkl. XOR-Invariante und Datenstand der Bestätigung), Saat, `Down`, **Lader der beiden gitignorierten Klassifikationsdateien** | 4.1–4.3, E1, E11 | T1.3a, T1.3b, T1.10 (Füllen: V5; Messwerte: K7-Schritt 4) |
| Zählpfad: Snapshot, Schlüssel, Flush, Log-Zeile | 5, F2 | T1.1, T1.2, T1.4, T1.7 |
| Lesepfade mit `emoteSetId`, `/series` **additiv** um 7TV-Id (6.5 Schritt 1), `GetRowsAsync`-Summe, `NameTwinEmoteSetIds` | 5 (Tabelle), 6.5, E15, E24 | T1.6 |
| Beobachtungs-Log: Öffnen/Schließen an sechs Stellen | 4.3, F9 | T1.5 |
| Api-Routen 6.1–6.5, 6.8, `EmoteSetIdValidationFilter`, vier Fehlercodes | 6, E13, E14 | T2.3, T3.1 |
| `sync-imported` mit `TargetEmoteSetId`, set-zentrierter Endpunkt, Besitzer-Prüfung, Audit-Projektion | 6.7, E5, E22, F7, F10 | T2.4 |
| `sync-deleted`/`sync-restored` neue Form + Altform, Papier-Fall | 6.6, E3 | T5.2 |
| Set-Session im Voting | 6.9, 9, E4, E10 | T6.1, T6.2, T6.3 |
| Zeilenidentität, Caches, Scope-Capture, Export | 7, F3, F4 | T4.1, T4.3, T4.4, T4.5, T5.1 |
| UI: Dropdown, Zeilenklassen, Laden, Tatsachenangabe, Preset | 8.1–8.5, F16 | T4.2, T4.4 |
| UI: Ziel-Picker, Loader, Preview-Gruppen, Bestätigung | 8.6 | T2.5a, T2.5b, T2.6 |
| UI: Quell-Set-Picker | 8.7 | T3.1 |
| UI: Lösch-/Restore-Bestätigung, Duplikat-Regel, Audit-Ansicht | 8.8–8.10 | T5.3, T2.6 |
| Wartungsfenster, Rollback | 10, 17 | K7 |

### 1.2 Die vier DECISIONS-Einträge — welcher Task welchen mitbringt

| Eintrag (Spec 23) | Geschrieben von | Liegt im Commit | Was der Plan ergänzt |
|---|---|---|---|
| 1 *Usage is counted per emote set …* | **T1.8** (eigener Task, weil der Eintrag Migration, Flush, Beobachtungs-Log **und** Wartungsfenster zusammenfasst und drei Subagents davor gearbeitet haben) | `feat(usage): count chat usage per emote set` — der Commit von T1.3b + T1.4 | **kein Werte-Nachtrag mehr.** Der Inhalt steht vollständig in Spec 23, Zeile 1; der Plan ergänzt nur die Zuordnung: dieser Task schreibt ihn, und die **Dauer des Fensters** (AK 86) samt der Dauer von Schritt 4 und Schritt 6 trägt **K7** später nach |
| 2 *An import may target any set of an account the user edits …* | **T2.5b** | `feat(import): pick any set of a tracked account as the target` | — |
| 3 *Voting: "member of the session's set" replaces "not archived" …* | **T6.3** | `feat(voting): show set-session results with frozen names and eligibility` | — |
| 4 *A row of the set view is identified by its 7TV id …* | **T4.3** (erster Teil) und **T5.2** (Nachtrag im selben Eintrag) | `feat(usage-stats): identify grid rows by their 7TV id and show non-active sets` bzw. `feat(api): accept set-scoped bookkeeping for sync-deleted and sync-restored` | berichtigt zusätzlich die Spec-Prämisse „URL wie der Zeitraum" (0.2) und hält fest, welchen Weg T4.0 gewählt hat |

Ein fünfter Eintrag ist nicht nötig (Spec 23, letzter Satz). Die Spec selbst bekommt je gemessener
Sonde einen Nachtrag (Zweig, Datum, der andere Zweig gestrichen — AK 1); das ist ein `docs:`-Commit
des jeweiligen K0-Handgriffs, kein DECISIONS-Eintrag.

---

## 2. Tasks

Jeder Task ist die Arbeit **eines** Subagents mit frischem Kontext. Format je Task: Ziel ·
**Vertrag** · Dateien · Tests · Gate · Abhängigkeiten · Modell · Commit. **„Vertrag" ist eine
Leseanweisung, keine Zusammenfassung:** der Subagent liest die genannten Spec-Abschnitte, bevor er
anfängt — das Ziel darüber sagt nur, *was* gebaut wird, nie *wie es sich verhalten muss*. Die
Prüfliste aus Spec 19 („Wiederverwendet — als Prüfliste") wird vom jeweiligen Task **vor** dem
Bauen abgehakt, wo sie ihn betrifft.

Die Gates verwenden die Kurzformen:

- **BE** = `dotnet test EmotePurge.slnx` (braucht laufendes Docker, Testcontainers) + `dotnet format
  EmotePurge.slnx --verify-no-changes`.
- **FE** = `npm --prefix web test -- --watch=false` + `npm --prefix web run lint` + `npm --prefix
  web run format:check`.
- **E2E** = `npm --prefix web run e2e` — **nur wenn auf `:5151` keine Api lauscht**; sonst fällt
  rund die halbe Suite mit irreführendem „element not found" durch. Wer gerade live getestet hat,
  beendet erst `dotnet run`.
- **Live** = Regel 16: was gegen echte Postgres/Redis/Twitch/7TV zu sehen sein muss, steht am Task.

### K0 — Vorbedingungen (Betreiber, kein Code, kein Modell)

Alle Kommandos sind fertig mit Platzhaltern; jede Sonde ist in Spec 11 mit beiden Zweigen und der
Regel beschrieben, welcher gilt. **Nach jeder Messung:** ein `docs:`-Commit auf dem
Integrationsbranch, der in der Spec den gemessenen Zweig mit Datum einträgt und den anderen streicht
(AK 1). Sonden brauchen keinen Branch-Stand; sie können heute laufen.

#### T0.1 — Sonde 1 (Rest): Set-Liste von HandOfBlood mit Namen

```
curl -s https://7tv.io/v3/users/twitch/49140130 | jq '{active: .emote_set_id, sets: [.user.emote_sets[] | {id, name, capacity, flags}]}'
```

**Gemessen am 2026-09-20: Zweig A** — Ergebnis und Set-IDs in Spec 11, Sonde 1. **Folge im Plan:**
keine für T2.1 — die Liste kommt seit T0.5 aus v4, und die v3-Antwort wird **nicht** als Fixture
abgelegt. Die Set-IDs stehen für V5 bereit.

#### T0.2 — Sonde 4: Reload-Frequenz der Nutzungsseite (Dev-Box)

Kein Kommando: Dev-Stack mit einem Kanal mit Chat-Betrieb, Nutzungsseite offen, im Netzwerk-Panel
den `EventSource` auf `/api/live/…` über **10 Minuten** zählen (`usage.flushed`, `channel.synced`).
Beide Zweige und ihr Vertrag: Spec 11, Sonde 4 (Beschluss in E12). **Umbaukosten von Zweig B:** in
T2.2 eine Konstante und ein Testfall — deshalb ist die Sonde **kein Tor** für T2.2.

#### T0.3 — Sonde 5: `REMOVE` bei doppelt eingetragener Emote-ID

Vier Aufrufe gegen ein **eigenes Testset** mit `<7TV-TOKEN>`, `<SET-ID>`, `<EMOTE-ID>` — die vier
curl-Zeilen stehen wörtlich in Spec 11 (Sonde 5) und werden hier nicht kopiert. Eine
GraphQL-Fehlermeldung ist ein Ergebnis, kein Anlass für Varianten; nach zwei Fehlschlägen derselben
Probe: abbrechen und fragen.

Beide Zweige und ihr Vertrag: Spec 11, Sonde 5 und 8.9. **Betrifft T5.1 und T5.3:** bis zur Messung
bauen beide den Beschluss aus Spec 8.9. **Umbaukosten von Zweig A bzw. B-mit-Alias-Treffer:** ein
eigener kleiner Task in K5 danach — geschätzt eine Sitzung `sonnet`, kein Vertrag außerhalb der
Queue.

#### T0.4 — Sonde 6: Gegenprobe vor der Migration (lesend gegen Prod, unmittelbar vor K7)

```
ssh -N -L 15432:127.0.0.1:5433 vps
psql 'host=localhost port=15432 dbname=emotepurge user=emotepurge password=<PROD-PW>' -f set-wechsel-pruefung.sql
```

(`set-wechsel-pruefung.sql` aus Konzept 11.6.) **Ein Tor, kein Zweig** — die erwarteten zwei Zeilen,
beide Zweige und was bei Abweichung zu tun ist: Vertrag Spec 11, Sonde 6. Ergebnis im Repo: im
DECISIONS-Eintrag 1, nachgetragen durch K7.

**Im selben Handgriff, zweite Abfrage — die Kanalliste für die Klassifikation.** Rein lesend, in
derselben `psql`-Sitzung am selben Tunnel; die `SELECT`-Zeile steht wörtlich in Spec 11 (Sonde 6,
„Im selben Handgriff, zweite Abfrage") und wird hier nicht kopiert. **Vertrag:** Spec 11 (Sonde 6),
4.2 („Woher die Kanalliste kommt"), E1, AK 2 — dort steht auch, dass die Ausgabe beim Betreiber
bleibt und nur **zwei Zahlen** in den PR-Text gehen (Zeilen der Abfrage, Einträge der
Klassifikation). **Folge im Plan:** die Abfrage ist die Eingabe von **V5** und damit Vorbedingung
von T1.10.

#### T0.5 — Sonde 7: trägt v4 am `EmoteSet` ein Merkmal für persönliche Sets?

```
curl -s https://7tv.io/v4/gql -H 'Content-Type: application/json' -d '{"query":"query { __type(name: \"EmoteSet\") { fields { name type { name kind ofType { name kind } } } } }"}' | jq
```

**Gemessen am 2026-09-20: Zweig A**, samt Nachmessung mit drei Befunden (A: `style
{ activeEmoteSetId }` spart den zweiten Request; B: unbekannter Account ⇒ `NoSevenTvAccount`;
C: `emoteSets` liefert das persönliche Set mit). **Messwerte, Abfragen und die Folge für den
Vertrag stehen vollständig in Spec 11, Sonde 7** — dazu E7, E21, 6.1 und AK 21.

**Folge im Plan:** T2.1 baut allein den v4-Weg; die redigierte v4-Antwort wird als Fixture in
`tests/EmotePurge.Infrastructure.Tests/Fixtures/` abgelegt, samt einer zweiten Fixture für den
unbekannten Account (Befund B).

#### T0.6 — `editor_of { user { id } }` (neu, Prüfaufgabe der Spec)

```
curl -s https://7tv.io/v3/gql -H 'Content-Type: application/json' -d '{"query":"query($id: ObjectID!) { user(id: $id) { editor_of { id user { id username connections { platform id } } } } }","variables":{"id":"<7TV-USER-ID>"}}' | jq
```

**Gemessen am 2026-09-20 — erledigt.** Ergebnis und Vertrag: Spec 19 (Zeile `GqlEditorOfQuery`,
„Prüfaufgabe erledigt") und E22. **Folge im Plan:** T2.4 bekommt die redigierte Antwort als
**verifizierte** Fixture; der Vorbehalt „T2.4 startet mit unverifizierter Fixture" ist
gegenstandslos.

#### V1 — Zwischenweg vor dem 2026-10-01 (Konzept 12.4)

Wegwerfkanal (nie getrackt) bestimmen, Halloween dort aktiv, tracken, von HandOfBloods
Nutzungsseite übertragen, Kollisionen im Dialog abwählen.

**V1 ist eine Empfehlung an HandOfBloods Mod-Team, keine Vorbedingung, die wir erfüllen können**
(Betreiber, 2026-09-20): er kann den Weg vorschlagen, das Mod-Team entscheidet. Findet er **nicht**
statt, entfällt **V3** (Purge des Wegwerfkanals) ersatzlos.

**An der Zuordnungsliste und an der erwarteten Trefferzahl von Sonde 6 ändert das nichts** — warum
nicht, und welche zwei Prüfungen den Fall „am Wegwerfkanal ist doch etwas passiert" abfangen:
Vertrag Spec 13 (V1), 4.2, AK 3/4. Ohne V1 entfällt eine Purge-Handlung, kein Listeneintrag und
keine erwartete Zeile. (Der frühere Satz „dann hat die Zuordnungsliste einen Kanal weniger" war
falsch; er ist am 2026-09-20 in beiden Dokumenten berichtigt worden — s. 8, Widerspruch 1.)

**Unabhängig davon, und in jedem Fall fällig:** HandOfBlood wechselt am 01.10.; **am Wechseltag**
`ExpectedArchivedCount` mit der ID-Sonde aus Konzept 11.2 messen und `BoundaryUtc` notieren. Beide
Werte gehen in **V5** und in keinen Commit (Vertrag: Spec E1, 13/V5).

#### V2 / V3 — Purges (Admin-UI, Prod)

V3: Wegwerfkanal nach dem 01.10. und **vor** Sonde 6 purgen — **entfällt, wenn V1 nicht
stattfindet** (dann gibt es keinen Wegwerfkanal). V2: Testkanal **nach** Sonde 6 und vor
K7 purgen (er ist die Positivkontrolle der Sonde). Ergebnis im Repo: AK 3/4 im PR-Text von T7.

#### V4 — Datenbank-Kopie für die Migrationsprobe beschaffen (neu, 2026-09-20)

**T1.10 braucht eine Datenbank, gegen die sich die Migration einmal vollständig vorführen lässt.**
Zwei Quellen, beide gültig:

- **Die Sicherung der Dev-Datenbank von vor dem Leerräumen** — liegt bereits vor unter
  `~/projects/emotepurge-devdb-vor-purge-2026-09-20.sql.gz` (766 K; 27 Kanäle, 9 214 Emotes,
  6 022 Nutzungszeilen über 18 Tage ab 2026-08-29, 3 Nutzer). Die Wiederherstellung in eine
  **Wegwerfdatenbank** im selben Postgres-Container ist am 2026-09-20 vorgeführt worden und
  deckungsgleich zurückgekommen.
- **Eine Kopie der Produktionsdatenbank** über die bestehende Backup-Kette (VPS → NAS → OneDrive;
  Aufrufform in `docs/Operations.md` und `infra-docs`). **Das ist die aussagekräftigere Quelle:**
  nur sie trägt die echte Zeilenzahl, und nur an ihr ist die gemessene `Up`-Dauer die erwartete
  Länge des Wartungsfensters. Gegen die Dev-Sicherung ist die Dauer eine untere Schranke, mehr
  nicht.

Das Beschaffen ist ein **Handgriff des Betreibers**, kein Task: der Plan verbindet sich nirgends
nach außen. Die Probe selbst läuft danach lokal (T1.10) und fasst weder Prod noch den laufenden
Teststack an.

#### V5 — Die Klassifikationsdatei anlegen und füllen (neu, 2026-09-20; war T1.9)

**Vertrag:** Spec 13 (V5), E1, 4.2, AK 2, AK 92 — dort steht, **was** hineingehört, warum die Datei
nicht ins Repo geht und welche zwei Zahlen stattdessen in den PR-Text gehen. Hier steht der
Handgriff. **T1.3b** baut den Mechanismus, **hier** wird gefüllt. Einmalig, im Haupt-Checkout bzw.
in dem Worktree, aus dem T1.10 und später die K7-Schritte −1 bis 6 laufen:

```
cp src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Local.cs.example \
   src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Local.cs
```

Darin, nach der Anleitung im Kopf der `.example`: HandOfBloods **Wechseleinträge** (beide Set-IDs
stehen seit T0.1/T0.5 fest, `BoundaryUtc` und `ExpectedArchivedCount` aus der ID-Sonde vom
Wechseltag) und **je ein Kein-Wechsel-Eintrag für jeden übrigen Kanal** aus der Zählabfrage neben
Sonde 6 (T0.4), `ConfirmedEmoteSetId` **abgeschrieben**, nicht abgeleitet. **Nur die Aussagen** —
die drei `Confirmed*`-Messwerte entstehen erst in K7, Schritt 4. Form, Regeln und Begründung: Spec
13 (V5) und 4.2.

**Was das für den Plan heißt:** die Zahl der Kein-Wechsel-Einträge steht vorher nicht fest, und
HandOfBlood ist der einzige Kanal mit Wechseleinträgen, mit V1 wie ohne. `git status` bleibt sauber.

**Gegenprobe vor Ort:** nach dem Füllen `dotnet build EmotePurge.slnx` — die Datei ist Teil des
Builds, ein Tippfehler ist hier ein Compile-Fehler und keine stille Fehlbuchung. Danach T1.10.

**Handgriff „Hash bilden und ablegen" (Vertrag: Spec 4.2 „Herkunft und Wiederherstellbarkeit",
AK 92).** Unmittelbar nach dem erfolgreichen Build:

```
sha256sum src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Local.cs
git -C . rev-parse HEAD
```

Beide Ausgaben wandern zusammen mit **Datum und Zeilenzahl der Zählabfrage aus T0.4** nach
`infra-docs`, und die **Datei selbst** wird dort abgelegt und eingecheckt. Dieser Plan fasst
`infra-docs` nicht an; er verweist nur darauf. Der **Hash** (nicht die Werte) geht außerdem in den
PR-Text von T7 und in den Runbook-Eintrag zu K7.

**Termin:** nach dem 01.10. **und** nach T0.4; vor T1.10, und noch einmal zu prüfen unmittelbar vor
dem Fenster (K7, Schritt −1 — ein anderer Checkout hat die Datei nicht). **Kein Task, kein Modell,
kein Commit.**

### K1 — Zählen pro Set (Schritt 3)

Reihenfolge innerhalb K1: T1.1 → T1.2 → T1.3a → T1.3b → T1.4 → T1.8 (Commit) → T1.5 ∥ T1.6 → T1.7 →
T1.10 (Migrationsprobe, vor dem K1-PR; braucht V5). **T1.9 entfällt als Commit** — s. dort.
Worktree A.

#### T1.1 — Schlüssel und Snapshot im Worker; Flush-Signatur mit vorläufiger Summe

**Ziel:** `UsageCounterKey`, `EmoteMatchSnapshot`, die drei Interface-Änderungen aus Spec 5 und die
puren Klassen dahinter — bei grünem Build, weil der Flush vorläufig je `EmoteId` summiert (0.2).

**Vertrag:** Spec 5 (Signaturen der drei Interfaces, Regeln 3 und 5), F2; Kriterien AK 12, 13.

**Dateien:** `Core/Services/IEmoteMatchCache.cs:5-9`, `Core/Services/IUsageStatFlushService.cs:26,39`
(`UsageCounterKey` neben `EmoteUsageCounts`), `Worker/IEmoteUsageCounter.cs:3-7`,
`Worker/EmoteUsageCounter.cs:8-14` (`TArg`-Muster bleibt), `Infrastructure/Services/EmoteMatchCache.cs:11-20`,
`Worker/UsageFlushWorker.cs:68-82` (reicht das keyed Wörterbuch durch; `RecordFlushSuccess` zählt
Schlüssel), `Infrastructure/Services/UsageStatFlushService.cs:29-32,67-87` — **nur** die
Projektion auf `EmoteId`-Summen vor dem bestehenden SQL; das SQL selbst bleibt (T1.4).

**Tests:** `Worker.Tests/EmoteUsageCounterTests.cs` — **10 von 10 rot** (Signatur), umgestellt,
**+3** (AK 12: zusammengesetzter Schlüssel, `Merge` mit anderer Set-ID, `PendingEmoteCount` = 2).
`Infrastructure.Tests/Unit/EmoteMatchCacheTests.cs` — **7 von 7 rot**, umgestellt, **+4** (AK 13:
Snapshot als ein Objekt; Set-ID und Generation; **Tausch-Konsistenz über 1.000 Tausche mit
Nebenläufigkeit** — nie ein gemischtes Paar; leerer Snapshot mit `EmoteSetId == ""`).
`Integration/UsageStatFlushServiceTests.cs` — **14 von 14 rot** (Signatur), umgestellt auf den
Schlüssel; **noch kein** neuer Fall (die kommen in T1.4). Grenzfall zu prüfen: zwei Schlüssel
desselben Emotes summieren in der vorläufigen Fassung auf **eine** Zeile — das ist der Zustand, den
T1.4 ablöst, und ein Test darf ihn **nicht** festschreiben.

**Gate:** BE grün. Kein Commit — der Chat-Pfad (T1.2) folgt in denselben Commit.

**Abhängigkeiten:** keine. **Modell:** `sonnet`.

#### T1.2 — Der Chat-Pfad liest einen Snapshot je Nachricht; Sync und Warmstart übergeben die Set-ID

**Ziel:** `TwitchChatManager` zählt alle Treffer einer Nachricht unter `(emoteId, snapshot.EmoteSetId)`;
`RefreshMatchCacheAsync` und der Warmstart geben `channel.ActiveEmoteSetId` an `ReplaceChannel`;
die Log-Zeile beim Tausch mit anderer Set-ID (Spec 5, Regel 2 — Wortlaut dort).

**Vertrag:** Spec 5 (Regeln 1–2, inkl. Wortlaut der Log-Zeile), F2; Kriterium AK 15.

**Dateien:** `Worker/TwitchChatManager.cs:1024-1055`, `Infrastructure/Services/SevenTvSyncService.cs:280-296`
(Warmstart), `:403-437` (Refresh, `:436`), `:86` (die Stelle, an der `ActiveEmoteSetId` gesetzt
wird — die Set-ID reist ohne zweite Quelle). **Nicht anfassen:** `:74-78` (#76-Sperre), `:83`
(`emoteSetSwitched` — T1.5).

**Tests:** `Integration/SevenTvSyncServiceTests.cs` (45 Fälle) — die Spec hat nicht verifiziert, wie
viele davon an den Fakes für `IEmoteMatchCache` hängen. **Teilaufgabe:** zählen, im PR-Text
nennen, umstellen. **+2** hier (Set-ID reist in den Cache beim Sync und beim Warmstart; AK 15:
genau eine Log-Zeile bei anderer Set-ID, keine bei gleicher — über einen `ILogger`-Fake, nicht über
Log-Text als Selbstzweck). `TwitchChatManager` selbst wird nach Regel 11 **live** verifiziert
(T1.7), nicht gegen Fakes.

**Gate:** BE grün. **Commit** (mit T1.1): `refactor(usage): key chat usage counts by the observed
emote set`.

**Abhängigkeiten:** T1.1; Rebase auf den Stand mit #76, falls noch nicht geschehen (0.4).
**Modell:** `opus` — die eine Lesestelle im Chat-Pfad entscheidet, ob ein Tausch zwischen zwei
Nachrichten sauber trennt oder ob je Nachricht zwei Aufrufe ein gemischtes Paar sehen können
(F2); ein Fehler hier ist eine leise Fehlbuchung ohne Test, der ihn fände.

#### T1.3a — Entitäten, `AppDbContext`, pure Prüf- und Zuordnungsfunktionen

**Ziel:** Die Modelländerungen und die Entscheidungslogik der Migration als **pure Funktionen** über
beide Eintragslisten, damit T1.3b sie nur noch in SQL abbildet und jeder Zweig einzeln getestet ist.

**Vertrag:** Spec 4.1 (Schema-Tabelle), 4.2 (Prüfungen 1–7 samt Umrechnungstabelle,
Zuordnungsregel, Ein-Tages-Unschärfe), E1 (Form der Liste, beide Eintragsarten).

**Dateien:** `Core/Entities/UsageStat.cs:3-26`, `Core/Entities/ChannelEmoteSetObservation.cs` (neu,
Muster `ChannelLiveDay`), `Core/Entities/VoteSession.cs:13-30`, `VoteSessionEmote.cs:7-14`,
`Infrastructure/Persistence/AppDbContext.cs:38-50` (neuer Unique-Index, Entität, partieller
Unique-Index — Muster `:52-65`), ein neuer purer Typ für die Prüfungen neben der Migration
(Infrastructure, kein EF-Bezug in der Signatur).

**Tests:** `Infrastructure.Tests/Unit/UsageStatMigrationChecksTests.cs` (neu, pur) **+22**, und die
Rechnung ausgeschrieben, weil die frühere Zahl offenließ, was sie zählt: **14** = je Prüfung 1–7
ein Abbruch- und ein Durchlauffall (Sonar zählt Zweige), **+1** zweite Abbruchgestalt von Prüfung 3
(XOR), **+2** weitere Abbruchgestalten von Prüfung 7, **+4** Zuordnungsfälle inklusive Grenztag,
**+1** für den Builder, der eine doppelte Eintragung schon beim Eintragen ablehnt (Lader, keine
Prüfung). Was die einzelnen Fälle prüfen, steht in Spec 4.2 und AK 6; hier steht, wie viele es
sind und warum.

**Gate:** BE grün — `dotnet ef migrations add` läuft hier **noch nicht** (das Snapshot-Diff gehört
zu T1.3b, sonst entsteht eine Migration ohne Prüfungen, die jemand versehentlich anwendet). Kein
Commit.

**Abhängigkeiten:** keine (parallel zu T1.1/T1.2 möglich, aber im selben Worktree — deshalb
sequenziell). **Modell:** `sonnet`.

#### T1.3b — Die Migration `AddUsageStatEmoteSetId`: Prüfungen, Backfill, Indextausch, Saat, `Down`

**Ziel:** Die eine nicht-additive Migration an der heißesten Tabelle — fünfzehn Schritte, sieben
Prüfungen, zwei temporäre Tabellen, Backfill in zwei `UPDATE`s, Indextausch, Saat und ein `Down`
mit zwei unabhängigen Schranken. Dazu der **Lader** der Klassifikation samt Abbruchmarken,
`.example`-Vorlagen und Ignore-Einträgen (das, was bis zum 2026-09-20 T1.9 war).

**Vertrag:** Spec 4.2 vollständig (Reihenfolge in `Up`, die sieben Prüfungen samt Meldungen, die
Unique-Constraints der temporären Tabellen, Backfill-Quelle, `Down` mit beiden Schranken, „Wo die
Liste liegt" mit allen vier Dateien, Marken `Confirm()`/`ConfirmWindow()`, Fail-fast-Meldung,
Image-Weg), 4.3 (Saat, inkl. der Auflage, dass sie `'set-switch'` nie vergibt), E1, E11, F1;
Kriterien AK 5–10, 90, 91. **Nichts davon wird hier wiederholt** — wer den Task ausführt, liest
Spec 4.2 als Ganzes, nicht diesen Absatz.

**Drei Dinge, die der Plan dazu festhält, weil sie den Task und nicht den Vertrag betreffen:**

- **Die Klassifikation ist in diesem Task und im ganzen Repo leer.** Der Subagent füllt sie nicht
  und legt sie nicht an; das ist V5, außerhalb des Repos. Seine eigenen Tests bringen ihre Werte
  über den `internal` Testsitz mit (AK 90).
- **Die Messwert-Datei füllt kein Task** — sie entsteht in K7, Schritt 4. T1.3b erzeugt die drei
  `Confirmed*`-Werte nur in seinen Fixtures.
- **Die Falle bei der partiellen Methode** ist in Spec 4.2 als Vertrag benannt (klassische Form,
  kein Rückgabetyp, keine partielle Property) — sie steht hier als Hinweis, weil ein Subagent
  sonst die modernere Form wählt und damit AK 90 bricht.

**Dateien:** `Infrastructure/Migrations/<stamp>_AddUsageStatEmoteSetId.cs` (neu, setzt auf
`20260907080507_AddUsageStatSharedChatUseCount` auf), `AppDbContextModelSnapshot.cs` (generiert),
`Infrastructure/Migrations/SetSwitchAssignments.cs` (neu, committet),
`Infrastructure/Migrations/SetSwitchAssignments.Local.cs.example` und
`SetSwitchAssignments.Window.cs.example` (neu, committet), `.gitignore`,
`.dockerignore`, **`CLAUDE.md`** (die gitignorierte Datei gehört dorthin, wo `appsettings.Lan.json`
schon steht — eine spätere Sitzung, die den Abbruch sieht, findet sonst nicht heraus, was fehlt).
`PendingMigrationGuard.cs:11` bleibt unverändert.

**Tests:** `Integration/AddUsageStatEmoteSetIdMigrationTests.cs` (neu, gegen den ephemeren
Container) **+18** (Zielwert aus Spec 15.1) über AK 5/6 (**acht** Abbruchfälle — die XOR-Hälfte von
Prüfung 3 ausdrücklich an einem `IsBotActive = false`-Kanal), AK 7 (Backfill; die bestätigte ID im
Test von der `ActiveEmoteSetId` **unterscheidbar** gemacht, damit die Assertion die Quelle prüft,
plus Einmaligkeit), AK 8, AK 9 (Saat **und**, als eigener Fall, die `'set-switch'`-Invariante),
AK 10 (**drei**) und AK 91 (**drei**). Die Fälle selbst stehen in Spec 14 unter diesen Nummern; die
Aufzählung dort ergibt addiert 19 statt 18 — beim Umsetzen nachzählen und die Zahl in beiden
Dokumenten geradeziehen (Abschnitt 9). `Integration/PendingMigrationGuardTests.cs` bleibt grün.
**Jeder** dieser Fälle setzt seine Klassifikation über den `internal` Testsitz; keiner liest die
Datei des Betreibers, und keiner hängt an ihrer Existenz (AK 90). Der Testaufbau braucht ein
Muster, um die Migration **bis zu einem bestimmten Stand** anzuwenden und dann Zeilen zu setzen —
der Task legt es in `Fixtures/` ab.

**Gate:** BE grün; `dotnet ef migrations list` gegen die lokale Dev-DB zeigt genau eine Pending;
`dotnet ef database update` lokal läuft durch **und** `dotnet ef database update
20260907080507_AddUsageStatSharedChatUseCount` rollt sauber zurück (Dev-DB hat keinen
Set-Wechsel — falls doch, ist der Abbruch an Schranke 1 das **erwartete** Ergebnis und kein
Task-Fehler; dann die `'set-switch'`-Zeile der Dev-DB vorher entfernen). **Zusätzlich, seit dem 2026-09-20:** der Task läuft
**ohne** `SetSwitchAssignments.Local.cs` (die entsteht erst in V5), und genau das ist die Probe auf
AK 90 — Build und alle drei Suiten grün ohne sie. Für den lokalen `database update`-Teil des Gates
legt der Subagent **beide** Dateien **vorübergehend** aus den `.example`s an, mit
`builder.Confirm();` bzw. `builder.ConfirmWindow();` und **ohne** Einträge (die Dev-DB trägt seit
dem Leerräumen keine Nutzungszeilen, Prüfung 3 und Prüfung 7 sind damit leer erfüllt), und löscht
sie danach wieder; beide sind gitignoriert und können nicht versehentlich in den Commit geraten. Kein Commit (Flush folgt).

**Abhängigkeiten:** T1.3a. **Modell:** `opus` — die einzige Migration des Vorhabens, die ein
laufendes altes Image nicht verträgt (F1), mit **sieben** `RAISE`-Zweigen in `Up`, zwei `UPDATE`s in
fester Reihenfolge und einem `Down`, das an **zwei** unabhängigen Schranken absichtlich scheitern
muss; jeder Fehler hier ist eine falsche
Zuordnung, die kein späterer Task erkennt.

#### T1.4 — Der Flush schreibt dreispaltig

**Ziel:** Der Flush schreibt die Set-ID mit; die vorläufige Summierung aus T1.1 fällt weg.

**Vertrag:** Spec 5 (Regel 4 — Arrays, Parameter, Conflict-Target), 4.1 (Conflict-Target = Index),
F1; Kriterium AK 11.

**Dateien:** `Infrastructure/Services/UsageStatFlushService.cs:29-32,67-87`.

**Tests:** `Integration/UsageStatFlushServiceTests.cs` **+3** (AK 11: zwei Schlüssel `(E, S1)`,
`(E, S2)` am selben Tag ⇒ zwei Zeilen; zweiter Flush auf `(E, S1)` addiert; alle drei Zählerspalten
unabhängig; zurückgestellter Batch mit anderer Set-ID). Die 14 umgestellten Fälle aus T1.1 bleiben
grün.

**Gate:** BE grün. **Kein Commit** — T1.8 schreibt den Eintrag und commitet T1.3a + T1.3b + T1.4 +
T1.8 zusammen.

**Abhängigkeiten:** T1.1, T1.3b. **Modell:** `sonnet`.

#### T1.8 — DECISIONS-Eintrag 1 und der Commit

**Ziel:** Der Eintrag *Usage is counted per emote set; the observed set travels with the match
cache* — englisch, `**Betrifft:**` mit den Dateien aus T1.1–T1.4.

**Vertrag:** Spec 23, Zeile 1 nennt den Inhalt vollständig und abschließend; der Task schreibt ihn
aus, ohne etwas hinzuzuerfinden. Quellen dafür: Spec 4.2, 4.3, 5, E1, E15, 10, 17, 22 (Zeile 1,
#76-Blockade). **Was ausdrücklich nicht hineingehört: die Werte der Klassifikation** — nur das
Verfahren (E1). **Kein späterer Werte-Nachtrag;** nachgetragen wird allein, was K7 misst (1.2).

**Gate:** BE grün auf dem Gesamtstand; `git status` zeigt nur die Dateien aus T1.3a/T1.3b/T1.4
und `docs/DECISIONS.md`. **Commit:** `feat(usage): count chat usage per emote set`.

**Abhängigkeiten:** T1.4. **Modell:** `sonnet`.

#### T1.5 — Beobachtungs-Log: Dienst und alle Schließ-/Öffnungsstellen

**Ziel:** `IChannelEmoteSetObservationService` (Core) / Implementierung (Infrastructure); kein
Aufrufer schreibt die Tabelle direkt. Alle sechs Anlässe aus der Tabelle in Spec 4.3 werden
verdrahtet.

**Vertrag:** Spec 4.3 (Tabelle: Anlass, Wirkung, `ClosedBy`, die beiden Stellen, die **nichts**
schreiben), F9; Kriterien AK 16–18.

**Dateien:** `Core/Services/IChannelEmoteSetObservationService.cs` (neu),
`Infrastructure/Services/ChannelEmoteSetObservationService.cs` (neu),
`SevenTvSyncService.cs:83-86,108`, `ChannelService.cs:64,244-247`,
`ChannelIdentityService.cs:338-342,448-450`, `ServiceCollectionExtensions.cs:88-121`
(Registrierung). Purge (`ChannelService.cs:105`) kaskadiert über die FK — keine Codezeile.

**Tests:** `Integration/ChannelEmoteSetObservationServiceTests.cs` (neu) **+9**: AK 16 (Öffnen
ohne offene Zeile; Set-Wechsel in einer Transaktion — Ausnahme nach dem Schließen ⇒ beides
zurückgerollt), AK 17 (fünf Schließstellen, je `ClosedBy`; Rejoin auf dasselbe Set öffnet eine
**neue** Zeile), AK 18 (partieller Index: zweiter `INSERT` mit `ObservedToUtc IS NULL` scheitert).
`Integration/ChannelServiceTests.cs`, `ChannelIdentityServiceTests.cs` **+3** (Leave/Rename/Merge
schließen — der Aufruf des Dienstes, nicht die Tabelle). `SevenTvSyncServiceTests.cs` **+1** (Öffnen
nach Sync; Wechsel über `emoteSetSwitched`). Grenzfall, den die Spec benennt und der ein Test
festhalten muss: während einer #76-Blockade bleibt die alte Zeile offen — **kein** Schreiben.

**Gate:** BE grün. **Commit:** `feat(sync): record observed emote-set intervals per channel`.

**Abhängigkeiten:** T1.8 (Tabelle existiert); Stand mit #76 (0.4). **Modell:** `sonnet`.

#### T1.6 — Lesepfade: Set-Filter, `/series` additiv um die 7TV-Id, `GetRowsAsync`-Summe, `NameTwinEmoteSetIds`

**Ziel:** Die Tabelle „Lesepfade" in Spec 5 Zeile für Zeile umsetzen, plus das additive
`/series`-Feld aus Spec 6.5 Schritt 1.

**Vertrag:** Spec 5 (Lesepfad-Tabelle), 6.5 (Schritt 1, additiv), E15, E16, E24, F4, F11; Regel 10;
Kriterien AK 19, 20.

**Zwei Übergangszustände, die nur den Plan betreffen:** der Aufrufer
`VoteSessionQueryService.cs:101-103` übergibt vorerst `channel.ActiveEmoteSetId` (K6 ersetzt das
durch `session.EmoteSetId ?? …`), und `emoteId` fällt **nicht** hier, sondern als Folge-Issue 5
hinter K7 (Spec 21).

**Dateien:** `Core/Services/IUsageStatQueryService.cs` (fünf Signaturen, `EmoteSeriesEntryDto`,
`EmoteUsageContextDto`), `Infrastructure/Services/UsageStatQueryService.cs:26-104,106-171,173-236,
238-264,326-352`, `VoteSessionQueryService.cs:101-103` (Aufruf), `Api/Endpoints/UsageStatsEndpoints.cs:35-100`
(Query-Parameter durchreichen — der Validierungsfilter kommt aus T2.3; bis dahin reicht der
Endpunkt den Rohwert durch, und das ist auf dem Integrationsbranch hinnehmbar, weil K2 vor T7
mergt). `Api.Tests/ChannelUsageSeriesWireFormatTests.cs:23-37` — **erweitert, nicht geändert**: der Fall
pinnt ab hier `"sevenTvEmoteId"` **und** `"emoteId"`; `:39-49` bleibt. **Kein bestehender Test wird
rot** — das frühere „bewusst rot" entfällt mit dem additiven Weg.

**Tests:** `Integration/UsageStatQueryServiceTests.cs` (54 Fälle) — **Teilaufgabe:** zählen, wie
viele an `GetChannelSeriesAsync`/`GetRowsAsync`/`EmoteSeriesEntryDto` hängen; umstellen; Zahl in
den PR-Text. **+10**: Set-Filter in Context/Daily/Series/Totals; `null` = aktiv; AK 19
(`GetRowsAsync` eine Zeile je `(EmoteId, Date)` als Summe — der Harness-Vertrag, F11); AK 20
(`/series`: aktiv + archiviert erscheinen, **beide mit gefülltem `emoteId` neben `sevenTvEmoteId`**,
eine in der DB nicht vorhandene Guid-lose Zeile nicht);
`NameTwinEmoteSetIds` (gleicher Name, andere ID, anderes Set ⇒ gelistet; gleiches Set ⇒ nicht);
nicht-aktive Grundmenge (archivierte Zeile mit Zahlen unter X erscheint, aktive Zeile ohne Zahlen
unter X erscheint **nicht**). `Worker.Tests/HarnessRunnerTests.cs`, `ReplayDayCounterTests.cs`:
**0 rot** (DTO unverändert) — das ist ein Gate, kein Zufall.

**Gate:** BE grün. **Commit:** `feat(usage-stats): filter usage queries by emote set and name
series entries by 7TV id`.

**Abhängigkeiten:** T1.8. **Modell:** `opus` — fünf Aggregat-Abfragen mit der Regel-10-Falle
(Navigations-Joins vor `GroupBy` erst auf ID-Listen reduzieren), ein neuer Aggregat-Pfad
(`NameTwinEmoteSetIds`) und der Index-Only-Scan von `GetUsageContextAsync`, der erhalten bleiben
muss (Spec 4.1); ein Fehler zeigt sich erst als falsche Zahl in der Set-Ansicht.

#### T1.7 — Wechsel-Tests und Live-Verifikation an der Dev-Box

**Ziel:** Die Fälle, die den ganzen Zählpfad zusammen prüfen (Spec 5 „Wechsel-Tests"), und der
Nachweis nach Regel 16.

**Tests:** `Integration/SevenTvSyncServiceTests.cs` **+2** (AK 14: drei Nachrichten mit demselben
Emote-Namen vor/während/nach dem Tausch ⇒ zwei Schlüssel, erste alt, dritte neu; gleichnamiges Paar
`Stare` Zeile A/B zählt nie auf beide — der Chat-Pfad wird dafür über den Snapshot und den Counter
getrieben, nicht über TwitchLib). `Worker.Tests`: ggf. **+1**, falls ein Fall ohne Container über
Counter + Snapshot ausdrückbar ist.

**Live (Dev-Box, Haupt-Checkout, `docker compose up -d --build worker` — Regel 15):** ein Dev-Kanal
mit Chat-Betrieb; Set-Wechsel auf 7TV am Testkanal; danach in Postgres **zwei** `UsageStats`-Zeilen
desselben Emotes am selben Tag mit beiden Set-IDs; die Log-Zeile „Match cache for … switched from
set …" genau einmal; Leave + Rejoin ⇒ zwei Beobachtungs-Intervalle, das erste `ClosedBy = 'leave'`;
Worker-Neustart ⇒ Warmstart-Log mit Set-ID. Der Befund (Zeilen, Zeitstempel) geht in den PR-Text.

**Gate:** BE grün; die vier Live-Befunde belegt. **Commit:** `test(usage): cover the set switch
across the match-cache swap`.

**Abhängigkeiten:** T1.2, T1.5. **Modell:** `sonnet`.

#### T1.9 — Entfällt als Commit (2026-09-20)

**Der Task hieß „Zuordnungsliste füllen — letzter Commit von K1“.** Mit der Entscheidung des
Betreibers vom 2026-09-20, die Liste **nicht** ins Repo zu geben (Spec E1/4.2), gibt es hier nichts
mehr zu committen. Der Task wird nicht neu geschnitten, sondern aufgeteilt — die Nummer bleibt als
Wegweiser stehen, damit keine andere wandert:

| Was | Wohin |
|---|---|
| Der **Lader** `SetSwitchAssignments` samt Abbruch bei nicht einkompilierter Klassifikation, die committete `.example`, die Einträge in `.gitignore` und `.dockerignore`, der `internal` Testsitz und die Tests mit eigener Fixture | **T1.3b** — dorthin, weil dessen eigene Migrationstests den Mechanismus brauchen, nicht erst ein späterer Task |
| Das **Füllen** der gitignorierten Datei mit den echten Werten, nach dem 01.10. und nach der Zählabfrage aus T0.4 | **V5** (K0, Betreiber) — der Handgriff hängt an Wissen, das nur der Betreiber hat, und hinterlässt nichts im Repo |
| Der Nachtrag der Werte im DECISIONS-Eintrag 1 | **entfällt ersatzlos** — T1.8 schreibt stattdessen das Verfahren samt Grund und Preis |

**Die beiden Testfälle dieses Tasks entfallen ebenfalls** (−2). Fall (1) verankerte
`ExpectedArchivedCount` ± 1 in einem Test — er hätte den echten Wert in eine **committete**
Testdatei geschrieben und damit den Ortswechsel unterlaufen; der Gedanke lebt in T1.10 weiter, wo
derselbe Verstoß gegen die **echte** Datei konstruiert wird (AK 88). Fall (2) — ein verfälschter
Kein-Wechsel-Eintrag bricht an Prüfung 1 ab — ist mit Fixture-Werten in T1.3b bereits abgedeckt
(AK 6, Prüfung 1). Dafür kommt in T1.3b der Fall zu AK 91 dazu: **netto −1** über den Plan.

**Was nicht entfällt: die Aussage selbst.** Sie trägt weiterhin die ganze Migration — sie steht
nur nicht mehr im Diff, sondern auf der Maschine des Betreibers, und wird nicht mehr von einem
Zweiten gegengelesen, sondern von sieben Abbruchprüfungen gegen echte Daten, von ihrem festgehaltenen SHA-256 und von T1.10
(Spec 4.2, „Was der Ortswechsel kostet“).

#### T1.10 — Migrationsprobe gegen eine wiederhergestellte Datenbank (neu, 2026-09-20)

**Ziel:** Die Migration **einmal vollständig vorführen**, bevor sie im Wartungsfenster über einem
Tunnel zum ersten Mal ernst wird. Keine Containertests, sondern ein Durchlauf gegen eine
wiederhergestellte, echte Datenbank — mit vier Fragen, von denen die erste bisher ausdrücklich
offen war.

**Testort:** der lokale Docker-Stack (`docker compose`, Api auf `:8080`) und eine
**Wegwerfdatenbank** im vorhandenen Postgres-Container. **Nicht** `:5151` — der Prozess überlebt
das Ausschalten des Arbeitsrechners nicht, und die Probe soll wiederholbar sein. Der Task berührt
**weder Prod noch den laufenden Teststack**: er legt die Wegwerfdatenbank an, arbeitet
ausschließlich darin und löscht sie danach.

**Quelle (V4, Handgriff des Betreibers):** die Dev-Sicherung
`~/projects/emotepurge-devdb-vor-purge-2026-09-20.sql.gz` (766 K; 27 Kanäle, 9 214 Emotes,
6 022 Nutzungszeilen über 18 Tage ab 2026-08-29, 3 Nutzer; Wiederherstellung am 2026-09-20
vorgeführt und deckungsgleich zurückgekommen) **oder** eine Prod-Kopie über die bestehende
Backup-Kette. Der Task läuft mit **beiden** Quellen; die **Prod-Kopie ist die aussagekräftigere**
und wird, wenn sie vorliegt, bevorzugt.

**Die vier Prüfungen des Tasks:**

1. **`Up` läuft durch, und die Dauer wird gemessen** (Quelle, Zeilenzahl, Sekunden). Der Task
   liefert **zwei** Zahlen: die gemessene **Untergrenze** und die daraus abgeleitete
   **Hochrechnung** nach der Formel aus Spec 10, ausdrücklich als Schätzung beschriftet. Warum die
   Probe die Fensterlänge nicht belegt und wie AK 86 stattdessen durchgesetzt wird: Vertrag
   Spec AK 87, AK 93, Abschnitt 10. Liegt die Hochrechnung über 10 Minuten, ist das ein **Befund
   vor dem Fenster**, kein Grund, im Fenster zu warten.
   **Zwei Teilzeiten werden getrennt gestoppt und getrennt berichtet:**
   - **Der Messwert-Schritt** (Spec 10, Schritt 4: Abfrage, Einfügen, Build) — die Zahl geht ins
     Runbook und ersetzt die veranschlagten 2–5 Minuten.
   - **Prüfung 7** (neu am 2026-09-20). Sie ist die einzige der sieben Prüfungen, die eine
     Aggregation über `UsageStats` ⋈ `Emotes` je Kanal fährt (`min`/`max`/`count`), und zwar
     **innerhalb** der Migrationstransaktion. Ihre Kosten sind heute ohne jede Zahl in das
     15-Minuten-Budget hineingerechnet. Der Task misst ihre Dauer **einzeln** — nicht als Teil der
     `Up`-Gesamtzeit — und berichtet sie getrennt; ohne diese Zahl steckt eine unbekannte Größe im
     Fenster (Spec AK 87). Fällt sie ins Gewicht, ist das ein Befund für die Fensterplanung, kein
     Grund, die Prüfung zu streichen.
2. **Jede Abbruchprüfung feuert, wenn man ihren Fall herstellt** — je Prüfung 1–7 ein konstruierter
   Verstoß gegen die wiederhergestellten Daten, dazu die zweite Hälfte von Prüfung 3 (XOR) an einem
   `IsBotActive = false`-Kanal. Welche Manipulation welche Prüfung auslöst und welche Meldung
   erwartet wird: Vertrag Spec 4.2 (Prüfungen 1–7) und AK 88. Nach jedem Abbruch ist die Datenbank
   unverändert.
3. **`Down` verweigert an Schranke 1, ohne etwas entfernt zu haben** — eine
   `ClosedBy = 'set-switch'`-Zeile setzen, `Down` laufen lassen, danach prüfen, dass Spalte,
   Tabelle und neuer Index noch stehen (AK 89).
4. **Die Gegenprobe-Abfrage (Sonde 6) läuft gegen dieselbe Datenbank** und liefert, was sie soll —
   ein Trockenlauf des Tors, samt der Zählabfrage aus T0.4. Am Migrationstag ist dann weder die
   SQL-Datei noch ihre Ausgabeform neu.

**Was die Probe nicht zeigt, ausdrücklich:** baut man die Klassifikation für die Wegwerfdatenbank
aus deren eigener `ActiveEmoteSetId`, ist Prüfung 1 dort eine Tautologie. Die Probe belegt die
**Mechanik** (Reihenfolge, Transaktion, Abbrüche, Dauer, Rückweg), **nicht** die Richtigkeit der
echten Liste aus V5 — die hängt am Wissen des Betreibers und an Sonde 6.

**Warum die Probe seit dem Ortswechsel der Liste zusätzlich Gewicht trägt** (Spec 4.2, „Was der
Ortswechsel kostet", Punkt 2): sie ist der **einzige** Lauf, in dem die echte, gefüllte Datei vor
dem Ernstfall einmal benutzt wird. Folge für den Plan: der Task läuft aus demselben Checkout wie
V5 — ein Worktree ohne die Datei bräche gleich an `Load()` ab (AK 91), was ein korrektes, aber
nutzloses Ergebnis wäre.

**Hash-Handgriff, vor dem ersten Lauf (Vertrag: Spec AK 92):** `sha256sum` über
`src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Local.cs` gegen den in `infra-docs`
abgelegten Wert aus V5, dazu `git rev-parse HEAD` gegen die dort notierte Commit-ID. Weicht eines
ab, läuft die Probe **nicht** — dann ist entweder die Datei eine andere als die geprüfte oder der
Branch-Stand ein anderer als der, gegen den sie gebildet wurde; beides ist ein Befund, kein
Startsignal. Für die Messwerte erzeugt der Task die `SetSwitchAssignments.Window.cs` aus der
Wegwerfdatenbank mit derselben Abfrage, die im Fenster läuft — das ist zugleich der Trockenlauf
dieses Schritts.

**Gate:** die vier Befunde belegt (Kommandos und Ausgaben im PR-Text von T7 — **ohne** die
Klassifikation selbst und ohne die Ausgabe der Zählabfrage; Zahlen, Hash und Commit-ID ja,
Kanalnamen nein, E1); BE grün bleibt
unberührt, weil der Task keine Quelldatei ändert. **Kein Commit** — es sei denn, die Probe findet
einen Fehler; dann geht der Fix zurück an T1.3b und der Befund in den PR-Text.

**Abhängigkeiten:** **V5** (die gefüllte, gitignorierte Datei) und V4 (die Kopie). Ein früherer
Trockenlauf gegen den Stand nach T1.3b mit einer einkompilierten, aber leeren Klassifikation ist
erlaubt und kostet nichts — er prüft dann nur Prüfung 3 und den Rückweg. **Modell:** `sonnet` — mechanisch, aber vielschrittig: Wiederherstellen, sieben
Verstöße herstellen und zurückrollen, Zeiten messen, aufräumen.

### K2 — Ziel-Set-Picker (Schritt 4)

Reihenfolge: T2.1 ∥ T2.2 → T2.3 → T2.4 → T2.5a → T2.5b → T2.6 → T2.7. Worktree B, parallel zu K1.

#### T2.1 — `ISevenTvEmoteSetListService`: v4 `emoteSets`, Cache, Singleflight, Breaker, Budget

**Ziel:** **Ein** Dienst für drei Routen (E6), der die v4-Set-Liste je Twitch-ID liest und hinter
der vollständigen Wächterkette läuft — Cache, Singleflight, Breaker, Budget, Request, in dieser
Reihenfolge.

**Vertrag:** Spec 6.1 vollständig (Antwortform, Zustandstabelle, die fünf Wächter mit ihrer
Wiederverwendung, die Operationskennung im Breaker samt Reichweitentabelle und Pflichtparameter,
die Haltbarkeit negativer Ergebnisse, der Kreuztest), E6, E7, E12, E21, F14, F17, 19 (Prüfliste);
Kriterien AK 21, 23, 24, 94. **Die Fehlerabbildung** (`NoSevenTvAccount` vs. `Unavailable`) steht in
der Zustandstabelle von 6.1 und in AK 21 — nie eine stille leere Liste.

**Was der Plan dazu festhält — die drei Bestandsklassen und ihr Zustand:**

| Klasse | Umgang in diesem Task |
|---|---|
| `ForeignEmoteSetProviderBudget` | **unverändert benutzt**, und zwar dieselbe typisierte Singleton-Instanz wie der Vorschaupfad (`ServiceCollectionExtensions.cs:107,113`) — **nicht** die keyed Bestenlisten-Instanz (`:128`) |
| `ForeignEmoteSetRequestCoalescer` | **geändert:** generisch geschlossen (`<TResult>`, je Ergebnistyp eine Registrierung), Muster `SevenTvLeaderboardStore<TValue>` (`:68`); der Vorschaupfad schließt den Typ auf seinen bisherigen |
| `ForeignSevenTvBreakerPolicy` | **geändert:** Operationskennung (Spec 6.1). Bis zum 2026-09-20 stand hier „unverändert" — das war eine falsche Gleichsetzung des Orchestrators, keine geänderte Faktenlage (8, Befund 3) |

**Verhaltensgleichheit ist ein Gate, keine Erwartung:** beide Änderungen müssen für den
Vorschaupfad bitgleich sein — die 9 `HardenedForeignEmoteSetServiceTests` und 14
`ForeignEmoteSetServiceTests` bleiben **ohne Änderung an ihren Dateien** grün (AK 94, AK 28).
`SevenTvApiClient.FetchV4PageAsync` (`:552`) bleibt unangetastet.

**Dateien:** `Core/Services/ISevenTvEmoteSetListService.cs` (neu; `EmoteSetSummary`),
`Core/SevenTv/ISevenTvApiClient.cs` (`GetEmoteSetListForTwitchUserAsync`), `SevenTvModels.cs`,
`Infrastructure/SevenTv/SevenTvApiClient.cs` (v4-GQL-Abfrage neben den vorhandenen v4-Abfragen,
über `FetchV4PageAsync`), `Infrastructure/Services/SevenTvEmoteSetListService.cs`
(neu), `Infrastructure/SevenTv/ForeignEmoteSetRequestCoalescer.cs:30-38` (generisch geschlossen —
**einzige** Änderung an einer Bestandsklasse dieser Kette; Breaker und Budget bleiben unangetastet),
`Infrastructure/SevenTv/ForeignSevenTvBreakerPolicy.cs` (Operationskennung — die **zweite**
Änderung an einer Bestandsklasse dieser Kette),
`ServiceCollectionExtensions.cs` (Registrierung des Dienstes **und** der zweiten geschlossenen
Coalescer-Instanz; die Breaker-Registrierung bleibt **eine** typisierte Singleton-Instanz).

**Tests:** `Unit/SevenTvApiClientEmoteSetListTests.cs` (neu) **+7** — gegen die v4-Fixture aus
Sonde 7, Fälle nach AK 21 (darunter die drei Fehlerfälle getrennt).
`Unit/SevenTvEmoteSetListServiceTests.cs` (neu) **+12** — AK 23/24 (Treffer, Miss, Redis-Ausfall
fail-open, ein Permit je Request über `RecordingForeignUpstreamRequestBudget`, Singleflight,
GraphQL-429 als HTTP 200, offener Breaker, gehaltenes negatives Ergebnis) **plus** den Kreuztest
aus AK 94. `Unit/ForeignSevenTvBreakerPolicyTests.cs` — **12 von 12 rot** (Signatur: jede Methode
nimmt die Kennung), umgestellt auf **eine** Kennung, **+4** nach AK 94. **Bestand, als Gate:**
`HardenedForeignEmoteSetServiceTests` (9) und `ForeignEmoteSetServiceTests` (14) bleiben grün —
wird dort etwas rot, ist die Umstellung falsch gemacht und nicht der Test.

**Gate:** BE grün. **Commit:** `feat(seventv): list the emote sets of a 7TV account`.

**Abhängigkeiten:** keine; T0.5/T0.1 sind gemessen (0.1). **Modell:** `opus` — angehoben von `sonnet`: der Task fasst **zwei** Bestandsklassen an (den Coalescer generisch, den Breaker um die Operationskennung — der Vorschaupfad muss dabei bitgleich bleiben) und verdrahtet vier Wächter in fester Reihenfolge an geteilten Singletons; ein Fehler darin ist kein falsches Feld, sondern ein geleertes Provider-Budget oder ein fremd geöffneter Breaker, der einen zweiten, unbeteiligten Lesepfad in 503 zieht.

#### T2.2 — Set-Vorschau nach Set-ID: `capacity`/`name`, zweiter Schlüsselraum, `sevenTvUserId` nullbar

**Ziel:** Die Set-Vorschau wird nach Set-ID lesbar und liefert Kapazität und Setnamen mit; der
zweite Schlüsselraum kommt dazu.

**Vertrag:** Spec 6.4 (Antwortform `ForeignEmoteSet`, Verhalten im Set-ID-Modus), E8, E12, F6, F15;
Kriterien AK 26, 28.

**Prüfaufgabe aus Spec 19, Teil des Tasks:** belegen, dass der Coalescer `set:{id}` und `{login}`
**getrennt** koalesziert.

**Dateien:** `Core/Services/IForeignEmoteSetService.cs:117-129`, `SevenTvApiClient.cs:67-68,704`,
`SevenTvModels.cs:503`, `Infrastructure/SevenTv/HardenedForeignEmoteSetService.cs:71-97`,
`ForeignEmoteSetCache.cs:24-25,63`, `Infrastructure/Services/ForeignEmoteSetService.cs:38-45 ff.`
(Set-ID-Modus ohne Helix), Frontend-Modell `web/…/core/seven-tv/foreign-emote-set.model.ts:34`
(`string | null`) und die Durchreichstellen `foreign-channel-step.ts:28,206`.

**Tests:** `Unit/SevenTvApiClientEmoteSetPreviewTests.cs` **+3** (`capacity`/`name`; 0 → `null`;
#74-Duplikate bleiben zwei Einträge — AK 28); `Unit/ForeignEmoteSetServiceTests.cs` **+2** (Set-ID-Modus
ohne Helix; `sevenTvUserId: null`); `Integration/HardenedForeignEmoteSetServiceTests.cs` **+2**
(AK 26: zweiter Schlüsselraum, Eintrag ohne Set-ID überschreibt nicht; Coalescing getrennt).
**Erwartung, ausdrücklich:** die 14 Vorschau-Client-, 9 Hardened- und 8 Endpunkt-Tests bleiben
**ohne Änderung an ihren Dateien** grün (AK 28) — wer sie anfassen muss, hat den Vertrag gebrochen.
Frontend: die Specs, die `sevenTvUserId: string` in Fixtures tragen, werden gezählt und umgestellt
(Teilaufgabe; erwartet klein).

**Gate:** BE + FE grün. **Commit:** `feat(seventv): read a foreign set preview by set id with
capacity and name`.

**Abhängigkeiten:** keine (parallel zu T2.1). **Modell:** `sonnet`.

#### T2.3 — Routen 6.1, 6.2, 6.4, 6.8; `EmoteSetIdValidationFilter`; vier Fehlercodes

**Ziel:** Die Api-Fläche von K2: zwei neue Routen (6.1, 6.2), der Query-Parameter `emoteSetId` an
drei bestehenden (6.4, 6.5, 6.8), der Validierungsfilter und die vier Fehlercodes.

**Vertrag:** Spec 6.1, 6.2, 6.4, 6.5, 6.8, 6.10 (Policies), E9, E13, E14; Regel 7; Kriterien
AK 22, 25, 27, 33, 45, 46.

**Dateien:** `Api/Endpoints/EmoteEndpoints.cs:24-29,212-221`, `SevenTvEndpoints.cs:27-70`,
`UsageStatsEndpoints.cs:35-100`, `Api/Validation/EmoteSetIdValidationFilter.cs` (neu),
`ApiErrorCodes.cs`, `Core/Services/IEmoteSetOwnershipService.cs`,
`Infrastructure/Services/EmoteSetOwnershipService.cs:21-60,105`, `web/src/app/core/i18n/api-error.ts:10-46`,
`web/public/i18n/de.json`, `en.json` (`errors.api.*`), `Api.Tests/ApiFactory.cs` (Substitute für
`ISevenTvEmoteSetListService`, `IEmoteSetOwnershipService`; **Prüfaufgabe:** ob
`IUsageStatQueryService` schon über `IChannelService` abgedeckt ist — die Spec hat es nicht
verifiziert).

**Tests:** `Unit/EmoteSetIdValidationFilterTests.cs` (neu) **+4**; `Api.Tests/AuthFilterMatrixTests.cs`
**+8** (AK 22: fünf Fälle der Set-Listen-Route; AK 27: drei 400-Fälle an `/usage-stats/*`;
`set-warning?emoteSetId` 400); `SevenTvForeignEmoteSetEndpointTests.cs` **+6** (`?emoteSetId=`
400/200/Set-Modus; `/me/emote-set-targets` AK 25 drei Fälle); `EmoteRoutePolicyTests.cs` **+5
`InlineData`** (AK 46); `Integration/EmoteSetOwnershipServiceTests.cs` **+3** (AK 33; die 5
bestehenden grün); `web/…/core/i18n/api-error-locales.spec.ts` grün ohne Änderung (AK 45).

**Gate:** BE + FE grün. **Commit:** `feat(api): expose emote-set lists and validate set ids`.

**Abhängigkeiten:** T2.1, T2.2. **Modell:** `sonnet`.

#### T2.4 — Grants mit 7TV-ID, `TargetEmoteSetId`, set-zentrierter Endpunkt, Audit-Projektion

**Ziel:** Die Papierspur für Importe in ein beliebiges Set: 7TV-ID im Grant, `TargetEmoteSetId` am
bestehenden Endpunkt, der set-zentrierte Endpunkt samt Besitzer-Prüfung, und die Audit-Projektion.

**Vertrag:** Spec 6.7 (beide Endpunkte, die fünfstufige Leiter, `AuditLogDetail.TargetEmoteSet`,
die Trennung Anzeigename/Twitch-Login), E5, E22, F7, F10; Kriterien AK 29–32. Die Vokabeltabelle
wird in eine gemeinsame statische Prüfmethode gezogen, damit sie nicht zweimal existiert.

**Dateien:** `SevenTvApiClient.cs:57-58,323-367`, `SevenTvModels.cs:207`,
`Core/Services/ISevenTvEditorService.cs:12,35`, `Infrastructure/Services/SevenTvEditorService.cs:14-57`,
`Infrastructure/Redis/ModRoleCache.cs:41`, `Api/Endpoints/EmoteEndpoints.cs:129-195,283-284`,
`SevenTvEndpoints.cs` (neue Gruppe `/api/seventv/emote-sets/{emoteSetId}`),
`Core/Services/IEmoteService.cs`, `Infrastructure/Services/EmoteService.cs:107-138`
(`MarkImportedAsync` + `MarkImportedToSetAsync`), `Core/Services/IAuditLogQueryService.cs:17-34`,
`Infrastructure/Services/AuditLogQueryService.cs:129-193`.

**Tests:** `Unit/SevenTvApiClientEditorOfTests.cs` (neu) **+2** (verifizierte Fixture aus T0.6;
fehlendes `id` ⇒ `null`); `Integration/ModRoleCacheTests.cs` oder neu `SevenTvEditorServiceTests.cs` **+2** (AK 31);
`Api.Tests/SevenTvEmoteSetSyncImportedEndpointTests.cs` (neu) **+9** (AK 30: 401; die sechs
400-Fälle aus `AuthFilterMatrixTests:394-503` gespiegelt; 404 `emote_set_not_found`; **403 bare**;
503 ohne Eintrag; 204 mit `ChannelName = null`); `AuthFilterMatrixTests` **+1** (`targetEmoteSetId`
ungültig ⇒ 400, AK 29); `Integration/EmoteServiceTests.cs` **+2** (Audit-Details des Imports mit
`targetIsActiveSetOfChannel` true/false/null); `Integration/AuditLogQueryServiceTests.cs` **+2**
(AK 32; die 19 bleiben grün). Frontend `core/audit/audit.model.ts` bekommt das Feld hier
(additiv), die Ansicht in T2.6.

**Gate:** BE grün; FE grün (Modell additiv). **Commit:** `feat(api): record imports into any set
the actor edits`.

**Abhängigkeiten:** T2.3 (Filter); T0.6 ist gemessen, die Fixture damit verifiziert.
**Modell:** `sonnet`.

#### T2.5a — Picker: Angebotsliste aus 6.2, Klassen, Vorauswahl, eigener Kanal

**Ziel:** Der Ziel-Picker wechselt seine Datenquelle auf `GET /api/seventv/me/emote-set-targets` und
wählt Sets statt Kanäle; `import-target-options.ts` wird durch eine **pure Funktion** über die neue
Antwort ersetzt (`import-target-choices.ts`).

**Vertrag:** Spec 8.6 (erste drei Punkte — `ImportTargetChoice`, Klassen und Reihenfolge,
Vorauswahl, die Wählbarkeitsregel `kind == NORMAL` samt Beschriftung, der eigene Kanal), 6.2, E6,
E7; Kriterium AK 34.

**Dateien:** `web/src/app/shared/seven-tv/import-target-dialog.ts:29-32,211`,
`import-target-choices.ts` (neu, ersetzt `import-target-options.ts`),
`core/seven-tv/seven-tv-emote-set.service.ts` (neu — die drei Listen-Routen und `?emoteSetId=`;
K3 und K4 nutzen ihn mit), `de.json`/`en.json` (Beschriftungen „aktiv", „nicht getrackt",
„persönliches Set", „kein Zielset" für `GLOBAL`/`SPECIAL`, „das ist die Quelle").

**Tests:** `import-target-dialog.spec.ts` (**28 rot**, Datenquelle wechselt) + `import-target-options.spec.ts`
(**4 rot**, Datei wandert nach `import-target-choices.spec.ts`) — beide umgestellt, **+6** (AK 34:
Reihenfolge getrackt/ungetrackt, Vorauswahl, `kind != NORMAL` deaktiviert **mit Grund**, eigener Kanal
gelistet mit deaktiviertem Quell-Set, `sevenTvUnavailable`/`setsUnavailable`-Hinweise als
Sperrgrund). `core/seven-tv/seven-tv-emote-set.service.spec.ts` (neu) **+4** (drei Routen,
`?emoteSetId=`, `HttpTestingController`). Nach Regel 12: Klassen und Rollen ja, Wortlaut nein.

**Gate:** FE grün. Kein Commit (T2.5b folgt in denselben Commit, weil der Picker ohne Loader ins
falsche Set schriebe — F5).

**Abhängigkeiten:** T2.3. **Modell:** `sonnet`.

#### T2.5b — Loader mit Set-Ziel, Preview-Gruppen, Projektion ohne Kollisionen, Setname im Kopf; DECISIONS 2

**Ziel:** Der Loader zieht Belegung, Kapazität und Setnamen aus dem **gewählten** Set statt aus dem
aktiven; die Vorschau führt Kollisionen und Alias-Abweichungen als eigene Gruppen und rechnet die
Projektion ohne sie. Der DECISIONS-Eintrag 2 liegt in diesem Commit.

**Vertrag:** Spec 8.6 (Punkte 4–8 — Loader-Fallunterscheidung, `truncated ⇒ failed`,
Warnungsquelle je Klasse, die drei Vorschau-Gruppen, `projectSlots`, Dialogkopf, Report), F5;
Spec 23 Zeile 2; Kriterien AK 36–40, 44.

**Dateien:** `core/emotes/import-target-loader.ts:27-34,66-98`, `core/emotes/emote-admin.service.ts`
(`syncImported` mit `targetEmoteSetId`), `shared/seven-tv/import-preview.ts:30-55`,
`import-confirm-dialog.ts:100-105,203-209,397,419,421-426,478`, `import-flow.ts:93-94`,
`core/seven-tv/seven-tv-import.service.ts:283-302`, `already-present-filter.ts:144-156` (**bleibt**
ID-Vergleich — nicht anfassen), `docs/DECISIONS.md`.

**Tests:** `core/emotes/import-target-loader.spec.ts` (**8 rot**, Signatur) umgestellt, **+5**
(AK 36: Set-Ziel, `truncated ⇒ failed`, Warnung je Klasse; aktives Set ⇒ kein anderer Request).
`import-preview.spec.ts` — Teilaufgabe: die Fälle „Kollisionen bleiben in `toAdd`" zählen und
invertieren; **+5** (AK 37; AK 38 mit einer **synthetischen Liste der Größen des Anlasses**:
762 Quellzeilen, 338 vorhanden, ~10 Alias-Abweichung, 192 Kollisionen, ~232 `toAdd`, Projektion
687 + 232 = 919 ohne Überlauf-Banner). `import-confirm-dialog.spec.ts` — Fälle mit Set-**ID** im
Kopf zählen und umstellen; **+4** (AK 38/39/40: Setname, Gruppen, Projektion, `failed` = 0 mit
gemockten Kollisionen). `emote-admin.service.spec.ts` **+1** (AK 44). `seven-tv-import.service.spec.ts`
**+1** (`targetEmoteSetId` im Report für getrackte Ziele). Grenzfall: #74-Duplikat, bei dem **ein**
Alias gleich ist ⇒ `alreadyPresent`, nicht Alias-Abweichung.

**Gate:** FE grün. **Commit** (mit T2.5a): `feat(import): pick any set of a tracked account as the
target` — enthält den DECISIONS-Eintrag 2.

**Abhängigkeiten:** T2.5a, T2.3. **Modell:** `sonnet`.

#### T2.6 — Ungetrackte Klasse: Bestätigung, `channelName: null`, set-zentrierter Report, Audit-Ansicht

**Ziel:** Die ungetrackte Klasse: Bestätigungsschritt im Picker, Report an den set-zentrierten
Endpunkt, und die Audit-Ansicht zeigt das Zielset.

**Vertrag:** Spec 8.6 (Bestätigung und Report), 8.10 (Zusätze der Audit-Zeile — dort bewusst der
**Twitch-Login** der Papierspur, nicht der Anzeigename des Dialogs), 6.7; Kriterien AK 35, 41, 43.

**Dateien:** `import-target-dialog.ts`, `seven-tv-import.service.ts:283-302`, `emote-admin.service.ts`
(oder der neue Set-Service — eine Methode für den set-zentrierten Report), `shared/audit/audit-row.ts`,
`audit-actions.ts`, `de.json`/`en.json` (`audit.details.*`, Bestätigungstext).

**Tests:** `import-target-dialog.spec.ts` **+4** (AK 35: Bestätigung öffnet, Abbruch, Bestätigung
schließt mit `channelName: null`, getrackt ohne zweiten Schritt). `seven-tv-import.service.spec.ts`
**+1** (AK 41: set-zentrierter Endpunkt, kein Resync). `shared/audit/audit-row.spec.ts` **+3**.
E2E `e2e/emote-import.e2e.spec.ts` **+2** (AK 43 „gleicher Kanal, anderes Set" — Request-Assertion
auf die GQL-Mutation, `setId` = gewähltes Set; ungetracktes Ziel mit Bestätigung und
set-zentriertem Report) mit neuen Mocks in `e2e/support/mocks.ts` (Set-Liste, Set-Vorschau,
`/me/emote-set-targets`).

**Gate:** FE + E2E grün. **Commit:** `feat(import): allow untracked target sets after
confirmation`.

**Abhängigkeiten:** T2.4, T2.5b. **Modell:** `sonnet`.

#### T2.7 — Live-Verifikation Dev-Box

**Live (Haupt-Checkout, Api per `dotnet run`, `npm start`; Testkanal mit einem nicht-aktiven Set,
Konzept 13.4):** Import in ein nicht-aktives Set des Testkanals — die Dialogzahlen (vorhanden,
Kollisionen, `toAdd`, Projektion) stimmen mit `GET /v3/emote-sets/{id}` überein; der Audit-Eintrag
trägt Set-ID und `targetIsActiveSetOfChannel: false`; eine absichtliche Namenskollision und eine
Alias-Abweichung erscheinen als Gruppen und der Lauf meldet keine `failed`-Zeile (AK 42). Zusätzlich
ein ungetracktes Ziel (Zweitkonto, Wegwerf-Set): Bestätigung, Lauf, Eintrag in der globalen
Admin-Audit-Ansicht mit `ChannelName = null` und `ownerLogin`. (Die T0.6-Fixture ist seit dem
2026-09-20 gemessen; der frühere Nachtrag „Fixture gegen die echte Antwort prüfen" entfällt.)

**Gate:** die drei Befunde im PR-Text; danach der K2-PR gegen den Integrationsbranch. Kein Commit,
außer eine Korrektur nötig ist (dann `fix(import): …`).

**Abhängigkeiten:** T2.6. **Modell:** `sonnet`.

### K3 — Quell-Set-Picker beim fremden Kanal (Schritt 5)

#### T3.1 — Route 6.3 und Radiogroup im `ForeignChannelStep`

**Ziel:** Die Set-Liste eines fremden Kanals als Route, und im `ForeignChannelStep` ein
Quell-Set-Picker, dessen Wahl bis ins Import-Ergebnis reist.

**Vertrag:** Spec 6.3 (Auflösung, Antwort, Zustandstabelle), 8.7 (Radiogroup, Vorauswahl, „kein
zweiter Request beim aktiven Set"), 8.6 (Wählbarkeitsregel), E7, E21; Kriterien AK 47–49.

**Dateien:** `Api/Endpoints/SevenTvEndpoints.cs:27-30`, `Infrastructure/Services/ForeignEmoteSetService.cs:45 ff.`
(Auflösung wiederverwenden), `web/…/shared/seven-tv/foreign-channel-step.ts:29,206`,
`core/seven-tv/seven-tv-emote-set.service.ts` (aus T2.5a), `de.json`/`en.json`.

**Tests:** `Api.Tests/SevenTvForeignEmoteSetEndpointTests.cs` **+5** (AK 47: die Zustandstabelle);
`foreign-channel-step.spec.ts` **+3** (AK 48/49: Radiogroup-Rolle und Vorauswahl; kein zweiter
Request beim aktiven Set; `emoteSetId` im Ergebnis; Regressionsfall #147 AK 17 grün). E2E
`emote-import.e2e.spec.ts` **+1** (Quell-Set-Picker).

**Gate:** BE + FE + E2E grün. **Commit:** `feat(import): pick the source set of a foreign
channel`. K3-PR.

**Abhängigkeiten:** T2.3 (Route/Filter), T2.5a (Service). **Modell:** `sonnet`.

### K4 — Set-Ansicht und Zeilenidentität (Schritt 6)

Reihenfolge: T4.0 → T4.1 → T4.2 → T4.3 + T4.4 (ein Commit) → T4.5 → T4.6. Beginnt erst, wenn K1
(T1.6) **und** K2 (T2.3, T2.2) auf dem Integrationsbranch sind.

#### T4.0 — Prüfaufgabe: der Set-Zustand in der URL (neu)

**Ziel:** Eine Entscheidung mit Beleg für T4.2, keine Implementierung. Befund aus 0.2: der
Zeitraum steht **nicht** in der URL; `listQueryState` ist für paginierte Listen gebaut. Zwei
zulässige Wege, beide am Code zu prüfen:

- **(a) `listQueryState({ emoteSetId: '' })` wiederverwenden.** Passt in dem, was 8.1 verlangt
  (`replaceUrl`, Default entfernt, Deep-Link, Reload); `page` bliebe ungenutzt, `textFilter`
  ungenutzt. Zu prüfen: ob das Anlegen aus einem Feld-Initialisierer der Seite (Injection-Kontext,
  `effect()`) mit der bestehenden Seite verträglich ist, und ob `setParams`' „zurück auf Seite 1"
  auf einer Seite ohne Pager irgendetwas Sichtbares tut (Erwartung: nein).
- **(b) Ein minimaler Lese-/Schreibweg** über `ActivatedRoute.queryParamMap` + `router.navigate`
  mit `replaceUrl` und `scroll: 'manual'` direkt in der Seite — weniger Kopplung, aber die fünf
  Regeln aus dem Doc-Kommentar von `list-query-state.ts` (kein Scroll, Default weg, `replaceUrl`)
  werden dann zum sechsten Mal von Hand eingehalten — genau das, wogegen die Datei geschrieben
  wurde.

**Empfehlung des Plans:** (a), sofern die Injection-Kontext-Prüfung nicht dagegen spricht; dann ist
das Set der erste URL-Zustand der Nutzungsseite und der Zeitraum bleibt, wo er ist (3). In beiden
Fällen: ein `emoteSetId`, das die Liste nicht kennt, fällt **still** auf das aktive Set (8.1).

**Ergebnis:** ein Absatz mit Beleg (Zeilen) an die Hauptsession; Entscheidung durch sie; T4.3
schreibt sie in den DECISIONS-Eintrag 4.

**Gate:** —. **Abhängigkeiten:** keine. **Modell:** `haiku` (Inventur: was die Datei kann, was 8.1
braucht, wo die Seite ihren Zustand hält).

#### T4.1 — `mergeSetView` und das Zeilenmodell

**Ziel:** Die pure Funktion `mergeSetView` und das geänderte `EmoteUsageTotal` — **vor** jeder
Template-Änderung.

**Vertrag:** Spec 7.1 (Zeilenmodell und Signatur von `mergeSetView`), E16, E17, E23, F16;
Kriterium AK 53.

**Der Übergang, den der Plan festlegt:** `emoteId: string | null` und `totalUseCount: number | null`
brechen Leser (F16). Der Task ändert deshalb Modell **und** Funktion, lässt die Seite aber noch mit
`live === null` laufen — die `null`-Zweige kennt vorerst nur die neue Funktion, ihre Abtrennung in
der Seite kommt in T4.4.

**Dateien:** `core/usage-stats/usage-stat.model.ts`, `core/usage-stats/merge-set-view.ts` (neu).
Leser, die durch `number | null` rot werden und **hier** nur typseitig aufgefangen werden (die
Abtrennung der `null`-Gruppe kommt in T4.4): `usage-stats-page.ts:535` (Sortierung),
`usage-bands.ts:56,113-118,198` (**bleibt** auf `number` — die Seite filtert davor),
`emote-usage-filter.ts:9,58-59` (kann `null` schon).

**Tests:** `core/usage-stats/merge-set-view.spec.ts` (neu) **+8** (AK 53: Live ohne Zeile ⇒
`emoteId: null, totalUseCount: null, 'live'`; Zeile ohne Live ⇒ `'left'`; zwei Live-Einträge derselben
ID ⇒ `slotCount 2`, beide Aliase; aktive Ansicht ⇒ alles `'live'`, `slotCount 1`; `aliases` =
`[emoteName]` ohne Live; `nameTwinEmoteSetIds` durchgereicht; Reihenfolge der Ausgabe stabil nach
Eingabe; leere Eingaben).

**Gate:** FE grün (Typprüfung über `ng test`, nicht `npx vitest` — Letzteres überspringt sie).
**Commit:** `feat(usage-stats): merge live members and counted rows into one set view`.

**Abhängigkeiten:** T1.6 (DTO-Felder `isArchived`, `nameTwinEmoteSetIds` in `/totals`). **Modell:**
`sonnet`.

#### T4.2 — Set-Dropdown, URL-Zustand, `retainAmong`, Set-Liste einmal je Kanal, `clearSeriesCache`

**Ziel:** Das Set-Dropdown in der Kopfzeile (`emote-set-menu.ts`, neu, Muster
`date-range-menu.ts:104-133`) und alles, was ein Set-Wechsel auslöst.

**Vertrag:** Spec 8.1 vollständig (Auslöser, Optionen und Beschriftung, `retainAmong` statt
`clear()`, URL-Zustand, degradierte Sicht bei 503, die Gates auf `selectedEmoteSetId()`), E19,
7.3 (Cache-Schlüssel, `clearSeriesCache` beim Set-Wechsel); Kriterien AK 50–52, 62 (zweiter Teil),
64. Der **URL-Weg** folgt der Entscheidung aus T4.0.

**Der Zwischenstand, den der Plan festlegt:** die Live-Liste lädt hier noch **nicht** (T4.4) — der
Wechsel zeigt in diesem Stand nur DB-Zeilen.

**Dateien:** `shared/emotes/emote-set-menu.ts` (neu), `usage-stats-page.ts:1665-1667` (Cache-Clear),
`.html:108,890` und die Kopfzeile, `core/usage-stats/usage-stat.service.ts:33,59,73-76`
(Cache-Schlüssel mit Set; beide Methoden bekommen `emoteSetId`), `de.json`/`en.json`.

**Tests:** `core/usage-stats/usage-stat.service.spec.ts` (**5 rot**, Signaturen) umgestellt, **+2**
(AK 64: zwei Sets, gleicher Zeitraum ⇒ zwei Requests). `features/usage-stats/usage-stats-page.spec.ts`
**+4** (AK 50: Optionen und Vorauswahl über Rolle/Name; AK 51: `retainAmong` statt `clear`,
`clearSeriesCache`, `/totals`/`/series` mit `emoteSetId`, Set-Liste **nicht** neu geladen; AK 52
teilweise: `usage.flushed` lädt die Set-Liste nicht — Request-Zählung mit `HttpTestingController`;
unbekanntes `emoteSetId` in der URL ⇒ aktives Set). Ggf. `shared/emotes/emote-set-menu.spec.ts`
für die Sperrentscheidung „`kind != NORMAL` ⇒ deaktiviert mit Grund" (Regel 12: Sperre samt Grund
ist erlaubt).

**Gate:** FE grün. **Commit:** `feat(usage-stats): switch the set from the page header`.

**Abhängigkeiten:** T4.1, T4.0 (Entscheidung), T2.3 (Route). **Modell:** `sonnet`.

#### T4.3 — Schlüsselwechsel (Spec 7.2) und DECISIONS-Eintrag 4

**Ziel:** Der Identitätswechsel des Rasters auf `sevenTvEmoteId`, quer durch alle Schlüsselstellen
der Seite. Der DECISIONS-Eintrag 4 (erster Teil) liegt in diesem Commit — inklusive der
Berichtigung aus 0.2 und der T4.0-Entscheidung.

**Vertrag:** Spec 7.2 (Tabelle, Zeilen 1–3 und 11–13), 7.3, F3, F4, F12, E4; Spec 23 Zeile 4;
Kriterien AK 54, 55.

**Zwei Abgrenzungen, die der Plan setzt:** `emoteId` wird aus `/series` **nicht hier** entfernt
(Folge-Issue 5, hinter K7 — Spec 21), und Queue-Keys, Protokoll und `doneIds` sind **T5.1**. Hier
werden die Typen nur so weit optional, dass T4.4 Guid-lose Zeilen anzeigen kann, ohne dass ein Lauf
sie schon annimmt — die Sperre aus Spec 8.9 bleibt bis T5.1 auf „Guid-lose Zeile nicht wählbar zum
Löschen".

**Dateien:** `usage-stats-page.ts:488-494,533-537,549-553,561-565,678-680,1349-1350,1375`,
`usage-stats-page.html:585` (**`:524` `trackBy: trackRow` bleibt** — DECISIONS 2026-08-30),
`shared/seven-tv/mass-delete-panel.ts:34-37`, `core/seven-tv/seven-tv-delete.service.ts:55-59`,
`emote-drilldown-dialog.ts:319-321`, `features/usage-stats/create-vote-session-dialog.ts`,
`docs/DECISIONS.md`.

**Tests:** `usage-stats-page.spec.ts` (42) — **Teilaufgabe:** die Fälle zählen, die `emoteId`-Keys
oder `totalUseCount: number` voraussetzen; umstellen; **+3** (AK 54 als Konsument-Spec: zwei
Guid-lose Zeilen einzeln wählbar und unterscheidbar, `retainAmong` behält die richtige; Drilldown-
Gate; Voting-Auflösung im Absende-Moment liefert Guids für eine Null-Session). `shared/selection/list-selection.spec.ts`
**+2** (AK 54 über `sevenTvEmoteId`-Keys; **die Klasse selbst bleibt unverändert** — Spec 18). `create-vote-session-dialog.spec.ts`
— Fälle zu `emoteIds` zählen und umstellen. E2E-Mock `e2e/support/mocks.ts:688-703`
(`mockUsageChannelSeries` liefert **beide** Felder, solange `/series` sie liefert — der Mock bildet
den Vertrag ab, nicht den Wunsch; er verliert `emoteId` mit Folge-Issue 5) — **die 30 bestehenden `usage-atlas`-Fälle laufen
mit umgestelltem Mock** (Teilaufgabe: Lauf, Zahl im PR-Text).

**Gate:** FE grün; E2E `usage-atlas` grün. **Kein eigener Commit** — T4.4 folgt in denselben, weil
das Raster ab der ersten Guid-losen Zeile nicht mehr auf der Guid stehen darf (NG0955) und
umgekehrt ein Schlüsselwechsel ohne Guid-lose Zeile nichts beweist (E25).

**Abhängigkeiten:** T4.2. **Modell:** `opus` — der Identitätswechsel quer durch 1.904 Zeilen mit
zehn Schlüsselstellen (F3), von denen eine falsche Map genügt, damit Rang, Füllgrad oder Serie zur
falschen Zelle gehören; und der DECISIONS-Eintrag, der drei Verträge auf einmal revidiert.

#### T4.4 — Nicht-aktive Ansicht: Laden, Klassen, Badge, `null`-Gruppe, Duplikat-Zelle, Namensvetter, Tatsachenangabe, Preset, Sperren

**Ziel:** Die nicht-aktive Ansicht ganz: paralleles Laden, Zeilenklassen, `null`-Gruppe,
Duplikat-Zelle, Namensvetter-Merkmal, Tatsachenangabe, Preset und die beiden Sperren.

**Vertrag:** Spec 8.2 (Zeilenklassen-Tabelle), 8.3 (Laden, `truncated`, Kapazität, Reload-Regeln),
8.4 (Tatsachenangabe als **Matrix**, mit der Begründung, warum aus einem fehlenden Intervall **nie**
eine Aussage über Zahlen folgt), 8.5 (Preset), E16, E17, E20, E23, E24, F16; Kriterien AK 52,
56–62.

**Dateien:** `usage-stats-page.ts` (Ladepfad, `computed()`s für Klassen, Sperren mit Grund,
Tatsachenangabe über das `rangeStartsBeforeTracking`-Idiom `:383-398`), `usage-stats-page.html:344-346`
und das Raster, `shared/datetime/date-range-menu.ts:19-30` (Preset), `shared/emotes/emote-set-menu.ts`
(Setnamen für Tooltips), `de.json`/`en.json` (E17-Beschriftung, Badge-Zusatz, Sperrgründe,
Tatsachenangabe, Preset-Beschriftung „während dieses Set beobachtet wurde").

**Tests:** `usage-stats-page.spec.ts` **+10** nach AK 52 und AK 56–62 — darunter **alle vier Fälle
der Matrix** aus AK 60, einschließlich des Falls „Zahlen ohne Beobachtungsintervall", den die
frühere Fassung falsch beantwortet hätte. `shared/datetime/date-range-menu.spec.ts` **+2** (AK 61).
Nach Regel 12: Zustandsübergänge, `computed()`-Ergebnisse, Sperrgründe, Rollen — keine Klassen,
keine Tailwind-Ketten.

**Gate:** FE grün; E2E `usage-atlas` grün (die neuen E2E-Fälle kommen in T4.6). **Commit** (mit
T4.3): `feat(usage-stats): identify grid rows by their 7TV id and show non-active sets` — enthält
den DECISIONS-Eintrag 4.

**Abhängigkeiten:** T4.3, T1.6, T2.2. **Modell:** `opus` — dieselbe Datei, derselbe Commit, dieselbe
Fehlerklasse wie T4.3 (die `null`-Zweige in Sortierung, Bändern und Summe sind je ein stiller
`NaN`, F16); ein Wechsel des Subagents zwischen T4.3 und T4.4 ist trotzdem gewollt, damit der zweite
den ersten liest, bevor er auf ihm baut.

#### T4.5 — Export mit Set und `null`; Türen und Protokollprüfung auf das gewählte Set

**Ziel:** Export und die drei anderen Import-Türen folgen dem **gewählten** Set; `null`-Zeilen
werden serialisiert statt ausgelassen.

**Vertrag:** Spec 7.4 (Export: Felder, Dateiname, `null`-Serialisierung), 7.3 (Scope-Capture),
8.6 (letzter Punkt: Türen und Protokollprüfung); Kriterien AK 64 (zweiter Teil), 65, 66.

**Dateien:** `shared/export/usage-export.ts:29-30,37-47`, `usage-export-purposes.ts:86-105`,
`usage-stats-page.ts:137,157-165,1452,1511-1531`, `shared/seven-tv/import-trigger.ts:120-132`,
`file-import-step.ts:80-82,136-140`.

**Tests:** `shared/export/usage-export.spec.ts` (**5 rot**, Fixtures ohne Set) und
`usage-export-purposes.spec.ts` (**8 rot**) umgestellt, **+4** (AK 65: Name und `meta`; `null`-Zeile
in CSV/JSON/`trend`). `usage-stats-page.spec.ts` **+2** (Scope-Capture: Export- und Import-Scope
lesen das gewählte Set beim Öffnen und nie wieder — Dropdown-Wechsel bei offenem Dialog ändert
nichts, AK 64 zweiter Teil). `file-import-step.spec.ts` **+2** (AK 66: Protokoll des Halloween-Sets
in der Halloween-Ansicht angenommen, in der Hauptset-Ansicht abgewiesen).

**Gate:** FE grün. **Commit:** `feat(usage-stats): export and import doors follow the selected
set`.

**Abhängigkeiten:** T4.4. **Modell:** `sonnet`.

#### T4.6 — E2E der Set-Ansicht; Statuszeile im Backlog prüfen

**Ziel:** `e2e/usage-atlas.e2e.spec.ts` **+3** (Set-Ansicht mit gemockter Live-Liste: Guid-lose
Zelle markieren; `null`-Gruppe am Ende; **keine NG0955 in der Konsole** — AK 55) mit Mocks für
Set-Liste und Set-Vorschau in `mocks.ts`. Dazu die Prüfaufgabe aus Spec 18 (letzte Zeile): ob
`docs/Feature-Ideen-2026-08-01.md` eine Idee im A16-Umfeld führt, deren Statuszeile zu pflegen
ist — wenn ja, im selben Commit.

**Gate:** E2E grün (freier `:5151`); Laufzeit als Kennzahl (rote Fälle mit deutlich längerer
Laufzeit sind Speicherdruck, nicht Regression). **Commit:** `test(e2e): cover the set view with a
mocked live member list`. K4-PR.

**Abhängigkeiten:** T4.5. **Modell:** `sonnet`.

### K5 — Löschen und Wiederherstellen im gewählten Set (Schritt 7)

Reihenfolge: T5.2 (Backend, parallel zu K4 möglich) → T5.1 → T5.3.

#### T5.2 — `sync-deleted`/`sync-restored`: neue Form, Altform, Papier-Fall; DECISIONS-4-Nachtrag

**Ziel:** `sync-deleted`/`sync-restored` nehmen die set-bezogene Body-Form an und buchen für ein
nicht-aktives Set nur Papier; die Altform bleibt gültig. Der Nachtrag zum DECISIONS-Eintrag 4 liegt
in diesem Commit.

**Vertrag:** Spec 6.6 vollständig (beide Body-Formen, Antwortform, die vierstufige
Validierungsleiter, die drei Service-Zweige samt Audit-Details und `channel.synced`-Regel), E3;
Spec 23 Zeile 4 (Nachtrag); Kriterium AK 70.

**Dateien:** `Api/Endpoints/EmoteEndpoints.cs:47-115,274`, `Core/Services/IEmoteService.cs`,
`Infrastructure/Services/EmoteService.cs:10-105`, `ApiErrorCodes.cs` (`emote_set_id_empty` ist aus
T2.3), `docs/DECISIONS.md`.

**Tests:** `Api.Tests/AuthFilterMatrixTests.cs` **+5** (AK 70: beide Formen je 200-Pfad über
Substitute; beide Listen leer 400 `emote_ids_empty`; beide gesetzt 400 `emote_ids_invalid`; neue Form
ohne Set-ID 400 `emote_set_id_empty`; ungültiges Format 400 — `SyncRestored_Answers400_WhenTheBodyCarriesNoEmoteIds`
bleibt gültig). `Integration/EmoteServiceTests.cs` (12) — **T5.2 entscheidet und meldet**, ob die
Überladung die alte Signatur ersetzt (12 rot) oder ergänzt (0 rot); die Spec lässt beides zu, der
Plan empfiehlt **ergänzen** (die Altform braucht den alten Pfad ohnehin bis Folge-Issue 1). **+6**
(neue Form aktiv: archiviert, Audit-Details, `channel.synced` bei `NewlyArchivedCount > 0`;
nicht-aktiv: keine Zeile geändert, Audit `false`, Antwort `archivedCount: 0`; Altform: heutiges
Verhalten + Log-Zeile über `ILogger`-Fake; `sync-restored` spiegelbildlich; `emoteCount`
dedupliziert).

**Gate:** BE grün. **Live:** ein `curl` mit Session-Cookie gegen die lokale Api in beiden Formen —
der Audit-Eintrag in der Admin-Ansicht trägt Set-ID und Flag. **Commit:** `feat(api): accept
set-scoped bookkeeping for sync-deleted and sync-restored`.

**Abhängigkeiten:** T2.3 (Filter, Code). **Modell:** `sonnet`.

#### T5.1 — `doneIds` entfernen, Queue-Keys, Protokoll, Laufdatensatz mit Set-ID, Panel-Output als Keys

**Ziel:** Lösch- und Wiederherstellungsläufe sprechen durchgängig 7TV-Ids — Queue-Key, Protokoll,
Laufdatensatz mit eingefrorener Set-ID, Panel-Ausgabe; `RunResult.doneIds` fällt.

**Vertrag:** Spec 7.2 (Tabelle, Zeilen 4–10 und 13), 8.9 (bis T0.3 gemessen ist), E2, E18, F3,
F12; Kriterien AK 67–69, 71, 72.

**Dateien:** `core/seven-tv/seven-tv-run-engine.ts:117-118`, `seven-tv-delete.service.ts:55-59,79-83,118,187-189`,
`seven-tv-restore.service.ts:62-66,152,200-205`, `shared/seven-tv/mass-delete-panel.ts:297-303,344-352,382,393-398`,
`shared/export/purge-run-export.ts:37-42,154-162`, `restore-flow.ts:82-86`,
`core/emotes/emote-admin.service.ts:55-68`, `usage-stats-page.ts:1349-1350`,
`features/voting/vote-session-detail-page.ts:690-695`.

**Tests — Teilaufgabe: alle Teilmengen zählen und im PR-Text nennen.** `seven-tv-run-engine.spec.ts`
(Fälle, die `doneIds` lesen, von 22) **+2** (AK 68: `doneKeys` einzige Identität; Lauf ohne `emoteId`).
`seven-tv-delete.service.spec.ts` (31) / `seven-tv-restore.service.spec.ts` (30) — Key- und
Body-Assertions umgestellt, **+6** (AK 68, AK 71: Set-ID beim Start eingefroren, Erstbericht und
`retrySyncReport` senden dieselbe Set-ID nach einem Dropdown-Wechsel; neue Body-Form; Altform wird
**nie** gesendet). `shared/export/purge-run-export.spec.ts` (14) **+3** (AK 69: `emoteId: null`,
altes Protokoll mit Guid, beides wiederherstellbar). `mass-delete-panel.spec.ts` (29) — `doneIds`- und
Protokoll-Filter-Fälle umgestellt, **+3** (AK 72: `deleted` als Keys; kein Protokoll-Filter;
Duplikat-Gruppe nach 8.9). `restore-flow.spec.ts` (16) — Fixtures. `usage-stats-page.spec.ts` **+1**,
`vote-session-detail-page.spec.ts` **+1** (AK 72: Hosts filtern nach `sevenTvEmoteId`; Detailseite
behält ihren Guid-Schlüssel — ein Test, der das festhält). `emote-admin.service.spec.ts` (4 von 9
rot: Bodies) umgestellt, **+2**.

**Gate:** FE grün; Build grün (AK 67 — kein `doneIds` mehr, `grep doneIds web/src` leer).
**Commit:** `feat(seventv): key delete and restore runs by 7TV id`.

**Abhängigkeiten:** T4.3 (Typen), T5.2 (Body-Form am Server). **Modell:** `opus` — das
Purge-Protokoll ist die **einzige** Rückwegdatei einer Löschung; ein Fehler in Filter, Parser oder
Laufdatensatz macht einen Halloween-Lauf zur Hälfte unumkehrbar, ohne dass es jemand sähe (F3), und
der Rückweg nach dem Deploy reicht dafür nicht (5).

#### T5.3 — Bestätigungen nennen das Set; Duplikat-Gruppe; Live-Verifikation am Testkanal

**Ziel:** Lösch- und Restore-Bestätigung nennen das Set; die Duplikat-Gruppe erscheint im Dialog;
dazu der Nachweis nach Regel 16.

**Vertrag:** Spec 8.8 (Felder und Wortlautregel, **kein** zusätzlicher Bestätigungsschritt),
8.9 (bis Sonde 5 gemessen ist); Kriterien AK 73, 74.

**Dateien:** `shared/seven-tv/delete-confirm-dialog.ts:15-25`, `restore-confirm-dialog.ts`,
`mass-delete-panel.ts` (Dialogdaten aus dem gewählten Set der Seite), `de.json`/`en.json`.

**Tests:** `delete-confirm-dialog.spec.ts` (14) / `restore-flow.spec.ts` — Fixtures, **+3** (AK 73:
Setname in beiden Dialogen; Zusatz nur bei `isActiveSet: false`; Duplikat-Gruppe mit Anzahl).
`mass-delete-panel.spec.ts` **+1** (Setname in den Dialogdaten aus dem Set der Seite, nicht aus
`activeEmoteSetId`).

**Live (Testkanal, Haupt-Checkout):** ein Set mit einem nie aktiven Emote (Klasse 3); löschen aus der
Set-Ansicht; das Protokoll enthält die Zeile mit `emoteId: null`; der Audit-Eintrag trägt Set-ID und
7TV-Id-Anzahl mit `targetIsActiveSetOfChannel: false`; Restore aus **genau diesem** Protokoll
(AK 74). Dazu einmal derselbe Weg im aktiven Set: Zeile archiviert, `channel.synced` gefeuert.

**Gate:** FE + E2E grün; die beiden Live-Befunde im PR-Text. **Commit:** `feat(seventv): name the
set in delete and restore confirmations`. K5-PR.

**Abhängigkeiten:** T5.1, T5.2. **Modell:** `sonnet`.

### K6 — Voting über ein nicht-aktives Set (Schritt 8)

#### T6.1 — Set-Session anlegen, Ausschlussregel, Votable

**Ziel:** Eine Abstimmung über ein nicht-aktives Set lässt sich anlegen, und das Abstimmen folgt dem
Wahlzettel statt dem Archiv-Flag.

**Vertrag:** Spec 6.9 (Request, Ausschlussregel, Fehlercode), 9 (Invariante, die fünf Schritte des
Anlegens, Abstimmen), E4, E13, F8; Kriterien AK 75–77, 79.

**Dateien:** `Api/Endpoints/VoteSessionEndpoints.cs:345-347`, `Core/Services/IVoteSessionService.cs:56-58`,
`Infrastructure/Services/VoteSessionService.cs:14-113,302-320`, `ApiErrorCodes.cs` (Code aus T2.3).

**Tests:** `Api.Tests/AuthFilterMatrixTests.cs` **+3** (AK 75: die drei Ausschlussfälle) **+1**
(`emoteSetId` ungültig ⇒ 400). `Integration/VoteSessionServiceTests.cs` (25, additive Felder ⇒ 0 rot)
**+5** (AK 76: nicht-Live-Mitglied ⇒ 400, keine Session, keine Zeile; AK 77: archivierte Zeile mit
`ArchivedAt = null` angelegt, nachgelesen, bestehende aktive Zeile übernommen und **nicht** verändert,
`NameAtCreation` gefüllt; AK 79: Abstimmen auf archiviertes Wahlzettel-Mitglied in Set-Session
erlaubt, in Null-Session gesperrt; 7TV nicht lesbar ⇒ `SevenTvUnavailable`, keine Session).

**Gate:** BE grün. **Commit:** `feat(voting): create sessions over a non-active set`.

**Abhängigkeiten:** T1.3b (Spalten), T2.2 (Lesepfad nach Set-ID). **Modell:** `sonnet`.

#### T6.2 — Worker wiederholt einmal bei 23505; Wettlauftest mit erzwungener Verschränkung

**Ziel:** Der Worker verliert keine Sync-Runde mehr, wenn die Api mitten im Sync eine Emote-Zeile
einfügt.

**Vertrag:** Spec E10 (Wiederholen einmal, kein Advisory-Lock, die Bedingungen im Einzelnen), F8;
Kriterium AK 78.

**Dateien:** `SevenTvSyncService.cs:108` (Umgebung des `SaveChangesAsync`), `:440-477`.

**Tests:** `Integration/SevenTvSyncServiceTests.cs` **+2** (AK 78: zwei `AppDbContext` — der Worker
hat gelesen, die Api fügt ein, der Worker speichert ⇒ Wiederholung, beide kommen durch, die Zeile
trägt den Zustand des Syncs; zweiter Konflikt im Wiederholungslauf propagiert). Die Verschränkung
wird **erzwungen** (Hook zwischen Lesen und Speichern), nicht mit Timing gehofft.

**Gate:** BE grün. **Live:** nicht sinnvoll erzwingbar — der Test ist der Nachweis; im Worker-Log
der Dev-Box darf nach einer Set-Session-Anlage während eines Syncs keine unbehandelte
`DbUpdateException` stehen. **Commit:** `fix(sync): retry once when a vote session inserts an emote
row mid-sync`.

**Abhängigkeiten:** T6.1. **Modell:** `opus` — ein Wettlauf zwischen zwei Prozessen, dessen Test nur
zählt, wenn die Verschränkung deterministisch erzwungen ist; ein Test, der nur meistens rot wäre,
wäre schlimmer als keiner.

#### T6.3 — Ergebnisse, Detailseite, Dialog, E2E; DECISIONS-Eintrag 3

**Ziel:** Ergebnisse, Detailseite und Anlege-Dialog für Set-Sessions. Der DECISIONS-Eintrag 3 liegt
in diesem Commit.

**Vertrag:** Spec 9 (Ergebnisse, Detailseite, Klasse 2b), 6.9 (DTO-Felder), F8, F12, E4;
Spec 23 Zeile 3; Kriterien AK 63, 80–83.

**Dateien:** `Core/Services/IVoteSessionQueryService.cs:21-22`, `Infrastructure/Services/VoteSessionQueryService.cs:64-125,175-195`
(`:101-103` jetzt `session.EmoteSetId ?? channel.ActiveEmoteSetId`), `features/voting/vote-session-detail-page.ts:198-220,842-846`,
`.html:155-160`, `features/usage-stats/create-vote-session-dialog.ts`, `docs/DECISIONS.md`.

**Tests:** `Integration/VoteSessionQueryServiceTests.cs` — **Teilaufgabe:** zählen, ob Tests
`VoteSessionResultDto` positional konstruieren (Spec: nicht verifiziert), umstellen; **+4** (AK 80:
`eligible`; Set-Totals nur Halloween; `null` ohne `UsageStat` unter der Set-ID; Name aus
`NameAtCreation` nach einem Sync, der `Emote.Name` überschreibt). `vote-session-detail-page.spec.ts`
(**2 von 8 rot**, `hasUsageData`-Gate) umgestellt, **+3** (AK 81/82). `create-vote-session-dialog.spec.ts`
**+2** (Set-Session-Body; Null-Session-Body unverändert). E2E `e2e/vote-ballot.e2e.spec.ts` **+1**
(AK 83). Integrationsfall für AK 63 (Klasse 2b bleibt `null` in `/totals?emoteSetId=` nach dem
Anlegen) in `UsageStatQueryServiceTests` **+1**.

**Gate:** BE + FE + E2E grün. **Commit:** `feat(voting): show set-session results with frozen
names and eligibility` — enthält den DECISIONS-Eintrag 3. K6-PR.

**Abhängigkeiten:** T6.1, T4.3 (Schlüssel der Set-Ansicht für den Dialog). **Modell:** `sonnet`.

### T7 — Gates, Coverage, Zweitmeinung, PR auf `main`

1. Auf dem Integrationsbranch nach dem letzten K-PR: BE, FE, E2E (freier `:5151`) komplett.
2. `node scripts/coverage-local.mjs` — **nach** dem letzten Commit (das Skript misst nur
   Committetes). Erwartung: die neuen Dateien (Dienst, Filter, Migration-Prüfungen, `mergeSetView`,
   Beobachtungs-Dienst, Set-Menü) nah an 100 %; `usage-stats-page.ts` und `SevenTvSyncService.cs`
   sind die zwei Stellen, an denen die Näherung in beide Richtungen unscharf ist — dort zählt die
   Frage, ob jede neue Zeile einen der in T1.2/T1.5/T4.2–T4.5 genannten Fälle hat, nicht die Zahl.
   Sonar misst `new_coverage` zeilen- **und** zweiggenau; die **sieben** `RAISE`-Zweige der
   Migration haben je einen Test (T1.3b).
3. `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` über das Ganze — nach den
   Kind-Issue-Reviews (0.4) ein Blick auf die Nähte zwischen den Kind-Issues. Findings sind Input;
   widersprechen sich Opus-Review und Codex bei einem P1/P2, entscheidet Fable (globale Regel).
4. PR-Text nennt: die Zahl der umgestellten Bestandstests je Datei (aus den Teilaufgaben), die
   gemessenen Sonden-Zweige, die Live-Befunde aus T1.7/T2.7/T5.3, AK 3/4 (Purges), die
   Rückrollgrenze, **die gemessene `Up`-Dauer aus T1.10 samt Quelle und der Angabe, ob sie
   überträgt (AK 87)**, und dass der Deploy ein getrenntes Wartungsfenster (K7) ist.
5. Der Merge gehört dem Nutzer. Ein Push ist kein Deploy.

**Modell:** `sonnet` für die Läufe und den PR-Text; Codex und Fable-Schiedsrichter steuert die
Hauptsession.

### K7 — Wartungsfenster (Betreiber, kein Code, kein Modell)

Einmalig, **hinter** dem 2026-10-08, nach Freigabe gegen das Harness-Runbook in `infra-docs`
(Nachläufer des Laufs brauchen den Zählpfad unverändert). Die Reihenfolge ist Spec 10; hier die
Kommandozeilen und was danach ins Repo gehört. Vorher: Sonde 6 (T0.4) Zweig A, V2/V3 erledigt, alte
Image-Tags notiert (Rollback) — **und die gitignorierte `SetSwitchAssignments.Local.cs` steht
gefüllt und mit geprüftem SHA-256 in dem Checkout, aus dem Schritt 6 läuft** (V5; ein frischer Klon
oder ein Worktree hat sie nicht, und `dotnet ef database update` baut aus genau diesem Checkout).
Die zweite gitignorierte Datei, `SetSwitchAssignments.Window.cs`, entsteht **im Fenster**
(Schritt 4) und steht vorher absichtlich nicht da.

| Schritt | Handgriff |
|---|---|
| −1 | **Bevor irgendetwas stoppt** (AK 92): `sha256sum src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Local.cs` und `git rev-parse HEAD` im selben Checkout, aus dem Schritt 6 läuft, gegen die in `infra-docs` abgelegten Werte aus V5. Fehlt die Datei, holt der Betreiber sie aus `infra-docs` zurück und prüft erneut; weicht der Hash ab, **wird nicht gestartet** — es ist dann eine andere Datei als die, mit der T1.10 gelaufen ist. Danach `dotnet build EmotePurge.slnx`. Ohne diese Zeile käme der Abbruch aus `Load()` (AK 91) erst bei gestoppter Site. Kostet eine Minute, spart ein halbes Fenster |
| 0 | Uptime-Kuma-Monitor pausieren; **Uhr starten** (AK 86 misst Schritt 1 bis zum Start der neuen Images) |
| 1 | Portainer: Stack-Service `worker` stoppen — `stop_grace_period: 60s` abwarten (Shutdown-Flush schreibt gegen das **alte** Schema) |
| 2 | Portainer: `api` stoppen |
| 3 | `ssh -N -L 15432:127.0.0.1:5433 vps` |
| 4 | **Messwerte ziehen** (Spec 4.2, Prüfung 7): in einer zweiten Shell die `SELECT`-Zeile aus `SetSwitchAssignments.Window.cs.example` lesend gegen Prod, Ausgabe als Rumpf nach `src/EmotePurge.Infrastructure/Migrations/SetSwitchAssignments.Window.cs` (Marke `builder.ConfirmWindow();` bleibt die erste Zeile), danach `sha256sum` dieser Datei **notieren**. **Erst hier** sind `max("Date")` und `count(*)` endgültig — vorher schreibt der Worker noch. Veranschlagt 2–5 min; die in T1.10 gestoppte Zahl steht im Runbook |
| 5 | `dotnet ef migrations list --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'` — **genau eine** Pending (`AddUsageStatEmoteSetId`); mehr heißt: Prod hängt Runden zurück, erst durchsehen |
| 6 | `dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>;Options=-c lock_timeout=5s'` — bricht bei Prüfung 1–7 ab statt zu schätzen; ein Abbruch ist ein Befund, kein Anlass zum Nachhelfen. **Abbruchpunkt: 10 Minuten** (AK 93, s. u.) |
| 7 | `dotnet ef migrations list …` — keine Pending |
| 8 | Portainer: neue Images (`ghcr.io/emotepurge/…`) für `api` **und** `worker` starten — Pull-Policy beachten (ein Re-Deploy ohne Pull fährt das alte Image weiter). **Hier stoppt die Uhr für AK 86** |
| 9 | Live-Verifikation Prod: `GET /api/health` 200; Set-Ansicht eines nicht-aktiven Sets lädt; eine `UsageStats`-Zeile des Tages trägt die aktive `EmoteSetId` (`psql` über den Tunnel); Worker-Log zeigt den Warmstart mit Set-ID; Picker am Testkanal — nicht-aktives Set zeigt dessen Belegung und Kapazität (AK 84/85) |
| 10 | Monitor wieder aktivieren; Tunnel schließen |

**Abbruchpunkt.** Vertrag: Spec AK 93 und Abschnitt 10 („Abbruchpunkt, verbindlich") — dort stehen
die drei Zahlen, was „Abbruch" konkret heißt und die Vorab-Regel „Hochrechnung über 10 min ⇒
Fenster anders planen". **Als Handgriff heißt das:** kehrt Schritt 6 nach 10 Minuten nicht zurück,
Kommando abbrechen, mit `migrations list` gegenprüfen, dass die Migration weiterhin `(Pending)`
ist, alte Images starten, Fenster als **Nulllauf** beenden. Ob `Up` noch rechnet oder hängt, zeigt
`pg_stat_activity` in einer dritten Shell am Tunnel — Diagnose, kein Grund, die Grenze zu dehnen.

**Danach ins Repo** (`docs:`-Commit): Nachtrag im DECISIONS-Eintrag 1 mit Datum, Dauer des Fensters
(AK 86: unter 15 min, sonst die Dauer als Befund), **Dauer von Schritt 4 und von Schritt 6
einzeln**, Ergebnis von Sonde 6, **die beiden Hashes** (Aussagen-Datei aus Schritt −1,
Messwert-Datei aus Schritt 4 — die Werte selbst nicht, E1) und dem Hinweis auf die Nachwirkung aus
Spec 17. **Ein** Folge-Issue für **beide** Übergangsfelder wird mit Datum „frühestens +14 Tage"
angelegt und ins Epic #200 eingetragen (Vertrag: Spec 21, ein Tor für beide). Die Messwert-Datei
wandert samt Hash nach `infra-docs` und wird aus dem Arbeitsbaum entfernt (Spec 4.2).

**Rollback im Fenster:** neuen `worker` **und** `api` stoppen, `dotnet ef database update
20260907080507_AddUsageStatSharedChatUseCount --connection '…'`, alte Images starten — nur bis
zum ersten beobachteten Set-Wechsel nach Schritt 8 (5). Ein Abbruch **während** Schritt 6 ist kein
Rollback, sondern ein Nulllauf: es ist nichts geschehen, und es genügt, die alten Images wieder zu
starten.

---

## 3. Nicht Teil dieses Plans

**Die Liste steht in Spec 20** und gilt hier unverändert und vollständig — sie wird nicht wiederholt.
Dazu, aus diesem Plan:

- **#76 läuft auf einem eigenen Branch vor diesem Vorhaben** (Entscheidung des Betreibers vom
  2026-09-20). Dieser Plan baut die Plausibilitätssperre weder um noch setzt er sie voraus; er
  rebased auf sie (0.4) und hält im DECISIONS-Eintrag 1 fest, was ohne sie passiert (Spec 22).
- **Der Zwischenweg vor dem 01.10. (V1) ist kein Bauauftrag** — und seit dem 2026-09-20 auch keine
  Vorbedingung, die wir erfüllen können: der Betreiber kann ihn HandOfBloods Mod-Team
  **empfehlen**, nicht steuern. Findet er nicht statt, entfällt V3 — und sonst nichts: weder
`SetSwitchAssignments` noch die erwartete Trefferzahl von Sonde 6 ändern sich (K0). Die Werte für V5
  (`BoundaryUtc`, `ExpectedArchivedCount`) hängen am Wechsel am 01.10., nicht am Zwischenweg.
- **Der Backfill-Parameter** (Set-ID als Pflichtparameter, Teilung an Set-Grenzen, Harness mit
  Set-ID) ist eine **Auflage an den Plan zu #69**; E15 hält den Harness-Vertrag bis dahin stabil,
  und T1.6 beweist es mit 0 roten Harness-Tests.
- **Der Zeitraum der Nutzungsseite bleibt außerhalb der URL.** Dass die Spec ihn als Vorbild nennt,
  ist ein Irrtum über den Bestand (0.2), kein Auftrag, ihn nachzuziehen. Wer das will, macht ein
  Issue daraus.
- **Die Altform `{ emoteIds }` bleibt** bis Folge-Issue 1 (E3), angelegt in K7.
- **Sonde-5-Zweig A** (Duplikate mit einer Queue-Zeile, Restore je Alias) ist erst nach der Messung
  ein Task (T0.3); bis dahin gilt 8.9.

---

## 4. Reihenfolge und Abhängigkeiten

```
K0 (Betreiber, jederzeit; T0.1/T0.5/T0.6 gemessen 2026-09-20; V1 als Empfehlung vor dem 01.10.)
  T0.1 T0.5 ──▶ T2.1 ✓      T0.6 ──▶ T2.4 ✓      T0.2 ──▶ T2.2 (nur TTL-Konstante)
  T0.3 ──▶ T5.1/T5.3 (8.9 bis dahin)             T0.4 ──▶ K7 (Tor)      T0.4 ──▶ V5

Worktree A — K1                                  Worktree B — K2
  T1.1 ─▶ T1.2 ─┐                                  T2.1 ─┐
  T1.3a ─▶ T1.3b ┼▶ T1.4 ─▶ T1.8 (Commit)          T2.2 ─┴▶ T2.3 ─▶ T2.4 ─▶ T2.5a ─▶ T2.5b ─▶ T2.6 ─▶ T2.7
                 │            ├─▶ T1.5 ─┐                              │
                 │            └─▶ T1.6 ─┴▶ T1.7                        └─▶ T3.1 (K3)
                 │                          │
   (01.10.) Wechseltag ─────────────────┴▶ V5 ─▶ T1.10 ─▶ K1-PR    (T1.9 entfällt)
   V4 (DB-Kopie) ─────────────────────────┘

K4 (nach K1-PR — tatsächlich reicht T1.6; und K2-PR)
  T4.0 ─▶ T4.1 ─▶ T4.2 ─▶ [T4.3 + T4.4] ─▶ T4.5 ─▶ T4.6 ─▶ K4-PR
                            │
K5                          │            K6
  T5.2 (∥ K4, braucht T2.3) ┴▶ T5.1 ─▶ T5.3 ─▶ K5-PR      T6.1 (braucht T1.3b, T2.2) ─▶ T6.2 ─┐
                                                          T4.3 ───────────────────────────────┴▶ T6.3 ─▶ K6-PR

T7 (nach K1–K6 auf dem Integrationsbranch; nach dem 08.10.) ─▶ PR auf main ─▶ Merge (Nutzer) ─▶ K7
```

**Warum diese Ordnung und keine andere:**

- **K1 ∥ K2, weil sie sich nicht berühren.** K1 lebt in Worker, Migration, Flush, Query; K2 in
  7TV-Client, Routen, Picker. Die einzige gemeinsame Stelle (`UsageStatsEndpoints.cs` bekommt den
  Filter aus T2.3, den Parameter aus T1.6) ist ein Einzeiler-Merge.
- **V5 wartet auf den 01.10., und nur T1.10 wartet auf V5.** Die Migration ist ab T1.3b mit einer
  leeren Klassifikation vollständig getestet — die Tests bringen ihre Werte als Fixture mit, nicht
  aus der Datei. Deshalb blockiert K1 die K4-Arbeit nicht: K4 braucht T1.6. **Seit dem 2026-09-20
  wartet auch kein Commit mehr auf den 01.10.**: früher war T1.9 der letzte Commit von K1 und
  terminlich gebunden, heute ist der Termin ein Handgriff außerhalb des Repos (V5). K1 ist damit
  **vor** dem 01.10. commit-fertig; nur die Probe und das Fenster liegen dahinter.
- **Die Messwerte der Bestätigung hängen an keinem Task.** `ConfirmedFromDate`,
  `ConfirmedThroughDate` und `ConfirmedRowCount` (Spec 4.2, Prüfung 7) entstehen erst **im
  Wartungsfenster nach dem Worker-Stopp** (K7, Schritt 4) — vorher wären sie garantiert veraltet.
  T1.3b baut nur den Lader dafür, T1.10 erzeugt sie für die Wegwerfdatenbank selbst, und **kein
  Commit** wartet darauf.
- **T1.10 steht zwischen V5 und dem K1-PR, und nirgends sonst.** Die Probe braucht die gefüllte
  Datei (sonst prüft sie eine leere Klassifikation) und soll vor dem PR laufen, weil ihr wichtigstes
  Ergebnis — die gemessene `Up`-Dauer — in den PR-Text und später ins Wartungsfenster-Runbook
  gehört. Sie hält nichts anderes auf: K2–K6 hängen nicht an ihr, und ein Fehlschlag fällt auf
  T1.3b zurück, nicht auf die Frontend-Stränge.
- **Schritt 6 (K4) vor Schritt 7 (K5)** — die Spec-Begründung, verdichtet: der Löschlauf im gewählten
  Set setzt voraus, dass das Raster Guid-lose Zeilen **anzeigen und markieren** kann; das ist der
  Schlüsselwechsel aus K4. Umgekehrt braucht K4 den Lauf nicht — Guid-lose Zeilen sind bis T5.1
  „wählbar, aber nicht löschbar", und das ist ein sichtbarer, kein stiller Zustand.
- **Der Identitätswechsel ist nicht aus Schritt 6 herauslösbar** (E25, Spec 22): ab der ersten
  Guid-losen Zeile stünde das Raster auf einem `null`-Schlüssel (NG0955, Sprite-Neuaufbau), und ein
  Schlüsselwechsel ohne Guid-lose Zeile beweist nichts. Deshalb T4.3 + T4.4 in **einem** Commit mit
  zwei Subagents nacheinander — der zweite liest den ersten.
- **K5 als letzter Code-Schritt vor K6** (Spec 17): Purge-Protokolle mit `emoteId: null` sind nach
  einem Revert vom alten Parser nicht lesbar — die Nachwirkung soll so spät wie möglich entstehen und
  im Runbook stehen. K6 hängt an K4 (Schlüssel) und K1/K2 (Spalten, Lesepfad), nicht an K5; die Spec
  ordnet K6 dennoch dahinter, weil es das Vorhaben mit dem geringsten Verlust bei Aufschub ist
  („Votings nutzt bisher niemand").
- **Der Zwischenstand zwischen K1 und K4 muss benutzbar bleiben.** K1 mergt vor K4, also gibt es auf
  dem Integrationsbranch eine Phase, in der `/series` schon umgebaut ist und das Frontend noch nicht.
  Der Branch wird nie deployt — aber die **Live-Verifikationen** des Plans (T2.7, T5.3, Regel 16)
  laufen aus ihm, und eine leere oder falsch zugeordnete Serienansicht wäre dort Rauschen, das einen
  echten Befund verdecken kann. Deshalb ist `/series` **additiv** (Vertrag: Spec 6.5, Schritt 1),
  und `emoteId` fällt erst **hinter K7** in einem eigenen Commit (Folge-Issue 5, Tor in Spec 21).
- **T5.2 darf K4 überholen.** Es ist reines Backend hinter T2.3 und lässt sich früh reviewen; T5.1
  braucht es dann fertig.
- **Der Deploy ist genau einer, hinter dem 08.10.** Ein gestaffelter Deploy scheidet aus (F1: das
  Conflict-Target ist ein Index; der alte Worker verwirft nach fünf Versuchen). Deshalb der
  Integrationsbranch statt Kind-Issue-Merges auf `main` (0.4).

---

## 5. Rückweg

**Global: Spec 17** — die Rückrollgrenze, die zwei unabhängigen `Down`-Schranken, die beiden
Handgriffe davor und die Nachwirkungen, die ein Revert nicht beseitigt. Wird hier nicht wiederholt.
Was der Plan trägt, ist die Zuordnung **je riskantem Task**:

| Task | Revert vor dem Deploy | Wo ein Revert nach dem Deploy nicht reicht |
|---|---|---|
| **T1.3b/T1.4** (Migration + Flush) | ein Commit, `git revert` genügt; die lokale Dev-DB per `dotnet ef database update <vorherige>` zurück | Prod: nur bis zum ersten Set-Wechsel; danach der Summier-Handgriff **und** das Entfernen der `'set-switch'`-Zeilen (zwei Schranken, s. o.). Die Beobachtungs-Tabelle und die Voting-Spalten stören ein altes Image **nicht** — das Argument gilt nur für alte Images gegen neues Schema |
| **T1.2** (Chat-Pfad) | Revert des K1-Commits; **Nachwirkung keine** (Zählung lief in der Zwischenzeit lokal) | Fehlbuchungen unter falscher Set-ID sind Daten, kein Schema; sie bleiben, sind aber je Zeile sichtbar (`EmoteSetId`) und über `GetRowsAsync` summenneutral (E15) |
| **T1.5** (Beobachtungs-Log) | eigener Commit, revertierbar; Tabelle bleibt leer stehen | Intervalle sind additive Daten; ein Revert lässt sie stehen, nichts liest sie mehr |
| **T1.6** (`/series` additiv um 7TV-Id) | einzeln revertierbar: solange `emoteId` mitläuft (Spec 6.5, Schritt 1), rendert ein altes Frontend gegen ein neues Backend korrekt. **Umgekehrt nicht:** ein Revert von T1.6 **ohne** T4.3 nähme dem umgestellten Frontend `sevenTvEmoteId` weg — T4.3 wird dann mit zurückgenommen | — (Wire-Format, kein Datenbestand) |
| **T2.4** (Papierspur, set-zentrierter Endpunkt) | revertierbar | Audit-Zeilen mit `targetEmoteSetId`, `ChannelName = null` bleiben; `ProjectDetail` fällt auf den `EmoteCount`-Zweig — Anzeigequalität, kein Verlust |
| **T4.3/T4.4** (Schlüsselwechsel) | **ein** Commit mit T1.6-Abhängigkeit (s. o.); Revert setzt das Raster auf Guids zurück — dann dürfen keine Guid-losen Zeilen mehr ankommen, also auch T2.2/T4.2 zurück oder die Live-Liste abschalten | — |
| **T5.1** (Protokoll `emoteId: null`) | revertierbar | **Protokolle, die mit `emoteId: null` geschrieben wurden, liest der alte Parser nicht** — ein Restore aus ihnen wäre nach einem Revert unmöglich. Das ist die eine Nachwirkung, die ein Rückweg nicht beseitigt; sie steht im Runbook (K7) und ist der Grund für die Position von K5 |
| **T5.2** (neue Body-Form) | revertierbar; die Altform bleibt ohnehin | ein alter Tab postet `{ emoteIds }` — gilt weiter (E3); ein **neuer** Tab gegen ein revertiertes Backend postet die neue Form und bekommt 400 nach der Mutation — deshalb Frontend und Backend gemeinsam zurück |
| **T6.1–T6.3** (Voting) | revertierbar; Spalten nullbar, alte Images ignorieren sie | Set-Sessions mit `EmoteSetId` und Wahlzettel-Zeilen mit `ArchivedAt = null` bleiben; ein altes Image zeigt sie als Null-Sessions mit archivierten Zeilen — lesbar, nicht löschbar aus der Session heraus |

Auslöser für einen Rückweg vor dem Deploy: ein Live-Befund aus T1.7, T2.7 oder T5.3 fehlt; Sonde 6
Zweig B ohne Klärung; ebenso ein Fehlschlag in T1.10, der nicht auf einen Fehler der Probe selbst
zurückgeht. Auslöser im Fenster: Prüfung 1–7 bricht ab (dann ist nichts geschehen — die
Transaktion ist zurückgerollt), oder AK 84 scheitert nach Schritt 6. Bricht später `Down` an einer
der beiden Schranken ab, ist das **kein** Fehler, sondern die Grenze, die der Plan zugesagt hat.

---

## 6. „Fertig" heißt

Je Task das Gate am Task. Je Kind-Issue-PR: BE, FE, E2E (freier `:5151`) grün, die genannten
Live-Befunde im PR-Text, die gezählten Bestandstests genannt, Codex-Review über den Kind-Branch.
Für T7 zusätzlich `node scripts/coverage-local.mjs` nach dem letzten Commit und die Zweitmeinung
über das Ganze. **Für K1 zusätzlich AK 87–92** — davon 87–89 und 92 aus **T1.10** (die
Migrationsprobe samt Hash-Prüfung ist ein Gate des K1-PR, kein optionaler Zusatz) und 90/91 aus
**T1.3b** (Bauen und Testen **ohne** die gitignorierte Datei). Für K2 zusätzlich **AK 94**
(Breaker-Kreuztest). Für K7: AK 84–86, 92 und 93 sowie der `docs:`-Nachtrag. Das ist dieselbe
Zuordnung wie in Spec 12.

Vergleichspunkte auf `93af613`: 45 `SevenTvSyncServiceTests`, 54 `UsageStatQueryServiceTests`,
14 `UsageStatFlushServiceTests`, 74 Harness-/Replay-Tests (müssen 0 rot bleiben), 42
`usage-stats-page.spec.ts`, 30 `usage-atlas`-E2E-Fälle. **Zielwerte: +277 neue Fälle und ≥ 88
umgestellte** — beide Zahlen stehen in Spec 15 und gelten als Summe der dortigen Tabellen, nicht als
fortgeschriebene Gegenrechnung. Die Zählungen aus T1.2, T1.6, T2.5b, T4.3, T5.1, T5.2 und T6.3
machen aus den offenen Teilmengen eine Zahl. **Fünf Zeilen der Spec-Tabellen stimmen mit der Summe
der Task-Erwartungen dieses Plans nicht überein** — sie sind in Abschnitt 9 einzeln benannt und beim
Umsetzen geradezuziehen.

Der Merge gehört dem Nutzer; der Deploy ist ein getrenntes Wartungsfenster.


---

## 7. Nachtrag: Codex-Review vom 2026-09-20, **erste Runde**

Adversariale Zweitmeinung (`/codex:adversarial-review --model gpt-5.6-sol`) über
[die Spec](../superpowers/specs/2026-09-20-emote-sets-200-spec.md) und diesen Plan. Fünf Befunde,
**alle fünf eingearbeitet** (Befund E am 2026-09-20 nach der Entscheidung des Betreibers), dazu
**drei** Nachzügler aus der Einarbeitung und ein daraus entstandener neuer Task. Die Verträge sind in
der Spec nachgezogen (dort Abschnitt 24); hier steht, was sich **an den Tasks** geändert hat.

| # | Befund | Was daraus wurde — im Plan |
|---|---|---|
| **A** | `Down` blockiert nicht zuverlässig [high]: die Index-Kollision ist ein Stellvertreter für „es hat einen Set-Wechsel gegeben", kein Synonym — disjunkte Sets oder ein Wechsel zwischen zwei Tagen rutschen durch, und `Down` verwirft `EmoteSetId` still. | **T1.3b**: `Down` mit **zwei unabhängigen Schranken** (Log-Prüfung auf `ClosedBy = 'set-switch'` vor jedem zerstörenden Schritt, plus die Index-Kollision); Saat vergibt `'set-switch'` nie; AK 10 mit drittem Fall („Wechsel ohne `(EmoteId, Date)`-Kollision ⇒ `Down` bricht trotzdem ab"), Migrationstests **+9 → +10**; Gate-Hinweis, dass ein Abbruch an Schranke 1 erwartetes Verhalten ist. **Abschnitt 5 (Rückweg)** nennt beide Schranken und beide Handgriffe. |
| **B** | Der v4-Listenpfad umgeht die vorhandene Härtung [medium]: kein Singleflight, kein Breaker, keine Fehler-Haltbarkeit; ein v4-Ausfall kann das gemeinsame 60/min-Budget leeren und die Fremdkanal-Vorschau mit in 503 ziehen. | **T2.1**: der Dienst läuft hinter der **vollständigen** Wächterkette aus Spec 6.1, mit **denselben** typisierten Singletons für Breaker und Budget wie der Vorschaupfad (nicht der keyed Bestenlisten-Instanz); einzige Bestandsänderung ist die generische Schließung von `ForeignEmoteSetRequestCoalescer`; Tests **+5 → +10** (paralleler Cold Miss, GraphQL-429 über HTTP 200, offener Breaker, gehaltenes negatives Ergebnis) plus die Auflage, dass die 9 Hardened- und 14 Vorschau-Tests grün bleiben; **Modell `sonnet` → `opus`** mit Begründung. |
| **C** | Integrationsbranch zwischen K1 und K4 wire-inkompatibel [medium]. | **T1.6** liefert `/series` **additiv** (`sevenTvEmoteId` **neben** `emoteId`), der Wire-Format-Test wird erweitert statt geändert und **kein** Bestandstest wird mehr bewusst rot; **T4.3** liest nur noch `sevenTvEmoteId`, entfernt das alte Feld aber nicht; der E2E-Mock bildet beide Felder ab; **Abschnitt 4** hat einen eigenen Begründungspunkt, warum der nie deployte Zwischenstand trotzdem zählt (die Live-Verifikationen T2.7/T5.3 laufen aus ihm); **Abschnitt 5** korrigiert die Revert-Zeile zu T1.6. Das Entfernen von `emoteId` ist Folge-Issue 5 der Spec — **hinter K7**, nicht hinter dem Merge von K4 (Runde 2, Befund 5). |
| **D** | Fehlende Beobachtung wurde als fehlende Zählung ausgegeben [medium] — Selbstwiderspruch zum begrenzten Zweck des Beobachtungslogs (Spec 4.3). | **T4.4**: Tatsachenangabe als **Matrix** aus zwei unabhängigen Aussagen; AK 60 mit allen vier Feldern, darunter „zuvor inaktiver Kanal, von der Migration backfillt, später rejoined: Zahlen ohne Beobachtungsintervall"; Tests **+8 → +10**. |
| **E** | Die vier Abbruchprüfungen der Migration [high]: ungelistete Kanäle galten implizit als „nie gewechselt", und Prüfung 3 erkannte einen Wechsel nur ab 10 Archivierungen oder 25 % — ein Wechsel zwischen stark überlappenden Sets lief still durch, und die Migration schrieb die ganze Historie des Kanals auf das heute aktive Set. | **Eingearbeitet; der Betreiber hat am 2026-09-20 die vollständige Klassifikation entschieden.** `SetSwitchAssignments` klassifiziert jetzt **jeden** Kanal mit Nutzungszeilen, mit einer zweiten Eintragsart für „nie gewechselt" samt mitgeschriebener `ConfirmedEmoteSetId`; aus vier Prüfungen werden **sechs** (Lückenlosigkeit ersetzt die alte Prüfung 3 und nennt den Kanal, die alte Schwelle lebt als **Widerspruch** weiter, dazu **Kettenschluss**; **Endzustand** gilt für beide Arten). **Im Plan:** **T1.3a** sechs pure Prüfungen, Tests **+8 → +16** (Rechnung ausgeschrieben: 6 × 2 + 4); **T1.3b** vierzehn statt zwölf Schritte, zwei temporäre Tabellen, Backfill aus der Liste statt aus `ActiveEmoteSetId`, Tests **+9 → +13**, Modellbegründung auf sechs `RAISE`-Zweige; **T1.9** füllte beide Eintragsarten, Tests **+1 → +2**, Modell `haiku → sonnet` — **überholt durch N3** (der Task entfällt, das Füllen wird V5); **T0.4** bekommt die lesende Zählabfrage, die die Kanalliste beschafft; **V1** verweist auf Prüfung 3/4 statt auf die alte Prüfung 3; K7-Schritt 4 und Abschnitt 5 nennen Prüfung 1–6. **Stand dieser Zeile ist der 2026-09-20 nach Runde 1; Runde 2 hat die Zahlen erneut bewegt** (sieben Prüfungen, fünfzehn Schritte, T1.3a **+22**, T1.3b **+18**, K7-Schritte um zwei verschoben) — s. Abschnitt 8. |

**Drei Nachzügler aus der Einarbeitung — Funde, keine Review-Befunde.** Alle drei sind beim
Nachprüfen am Code bzw. beim Gegenlesen der eingearbeiteten Entscheidung aufgefallen, nachdem
Codex seine fünf Befunde abgegeben hatte:

| # | Fund | Was daraus wurde — im Plan |
|---|---|---|
| **N1** | `Down`-Schranke 1 hängt an der Invariante „die Saat vergibt `ClosedBy` nie `'set-switch'`" — die stand als Auflage in Spec 4.3 und in T1.3b, hatte aber keinen eigenen Testfall. | **T1.3b** bekommt den Fall ausdrücklich: **nach `Up` existiert keine `ChannelEmoteSetObservation`-Zeile mit `ClosedBy = 'set-switch'`** (in der +13 oben enthalten, Spec AK 9). |
| **N2** | `SevenTvApiClient` liest jeden GraphQL-Fehler ohne `extensions.status: 429` als `Unavailable` (`:842-843`, `:446-451`, `:513`) — eine falsch geformte Abfrage sieht aus wie ein Dauerausfall; genau so war die Bestenliste einmal permanent „unavailable" (`:75-82`). | Als **F17** in Spec 3 aufgenommen. Für **T2.1** heißt das: die Live-Sonde ist das Einzige, was „unsere Abfrage ist falsch" von „7TV ist weg" trennt — die Fixture stammt aus der am 2026-09-20 live gemessenen v4-Antwort (Sonde 7) und ist kein Papierentwurf. Kein Task-Zuschnitt ändert sich. |
| **N3** | **Die Klassifikation ist die Nutzerliste des Dienstes — und E1 legte sie „sichtbar im PR-Diff“ in ein öffentliches Repo.** Kein Review-Befund: Codex hat E1 nicht angefasst. Der Fund ist eine **Folge der lückenlosen Klassifikation** (Befund E) und fiel erst beim Gegenlesen von E1 auf — solange die Liste nur die Wechsler nannte, waren das ein bis zwei Kanäle; seit sie jeden Kanal mit Nutzungszeilen nennt, ist sie die vollständige Trackingliste. Die Kennung wegzulassen geht nicht: die Set-IDs **sind** der Inhalt, und 7TV nennt zu jeder Set-ID den Besitzer. | **Entscheidung des Betreibers vom 2026-09-20: die Liste kommt nicht ins Repo**, nach dem Muster `appsettings.Lan.json` (gitignorierte Datei, committete `.example`, `.dockerignore`), aber als **kompilierte Konstanten** und damit weiter Teil des Builds. **Im Plan:** **T1.3b** baut den Lader (`static partial void AddLocalAssignments`, Marke `builder.Confirm()`, Fail-fast wie S3-34, `internal` Testsitz, `.example`, `.gitignore`/`.dockerignore`), Tests **+13 → +14**; **T1.9 entfällt als Commit** (−2 Tests), **V5** ist der neue Betreiber-Handgriff in K0 und ersetzt T1.9 als Abhängigkeit von **T1.10**; **T1.8** schreibt im DECISIONS-Eintrag 1 das Verfahren samt Grund und Preis statt der Werte; **T0.4** liefert seine Ausgabe nicht mehr in den PR-Text, sondern nur noch zwei Zahlen; **K7** bekommt Schritt 2a (Datei steht im Checkout) und **Abschnitt 4** die neue Ordnung (kein Commit wartet mehr auf den 01.10.). Neue **AK 90/91** in der Spec, hinten angehängt. **Der Preis steht in Spec 4.2 und 22:** Prüfbarkeit durch einen Zweiten gegen Nichtveröffentlichung der Nutzerliste. |

**Ein neuer Task, aus einer Zusage an den Betreiber:** **T1.10 — Migrationsprobe gegen eine
wiederhergestellte Datenbank** (K1, nach der gefüllten Klassifikation — heute V5, damals T1.9 —, vor dem K1-PR). Sie fährt `Up` gegen eine
Wegwerfdatenbank aus der Dev-Sicherung vom 2026-09-20 bzw. einer Prod-Kopie (**V4**, neuer
K0-Handgriff), **misst dabei die Dauer** — bisher ein ausdrücklich offener Punkt und zugleich die
Länge des Wartungsfensters —, stellt je Abbruchprüfung einen Verstoß her, prüft `Down` an
Schranke 1 und fährt Sonde 6 trocken. Neue AK 87–89 in der Spec (hinten angehängt).

Gegenrechnung der Zahlen: **+9** Testfälle aus dem Review (1 Migration, 5 Listen-Dienst,
2 Nutzungsseite, 1 Wire-Format) und **+12** aus der Einarbeitung von Befund E und N1 (8 + 3 + 1).
Die daraus fortgeschriebene Gesamtzahl („rund +236" hier, „+237" in der Spec) ist am 2026-09-20
**verworfen** worden: sie war nie eine Summe der Testtabellen, sondern eine Kette von
Gegenrechnungen, und lag um 24 daneben. Es gilt die nachgerechnete Summe der Spec-Tabellen —
**+277** (s. Abschnitt 6 und Spec 15). **−1** beim Bestand rot (der `/series`-Wire-Format-Test wird
nicht mehr rot). Aufwand: **~108 h → ~111 h** (T1.3 eine Stunde mehr für zwei zusätzliche
Prüfungen, T1.10 ~2 h); nach der zweiten Runde **~113,5 h** (Spec 16).

---

## 8. Nachtrag: Codex-Review vom 2026-09-20, **zweite Runde**

**Scope:** der Diff seit `b41b6e2` (die Einarbeitung von Befund E und N3 in Spec und Plan).
**Verdikt:** *needs-attention*. **Sechs Befunde, alle sechs eingearbeitet.** Der Wortlaut der
Befunde und die vollständige Einarbeitung stehen in **Spec 25**; hier steht nur, was daraus **im
Plan** wurde.

| # | Befund (Kurzform) | Was im Plan daraus wurde |
|---|---|---|
| **1** | Die Kein-Wechsel-Bestätigung ist an keinen festen Datenstand gebunden [high] — ein Rückwechsel S1 → S2 → S1 passiert alle Prüfungen. | **T1.3a** prüft `ConfirmedFromDate`/`ConfirmedThroughDate`/`ConfirmedRowCount` als **Prüfung 7** mit, Tests **+16 → +22**; **T1.3b** rendert die Messwerte in die zweite temporäre Tabelle und lädt sie aus einer **zweiten gitignorierten Datei** `SetSwitchAssignments.Window.cs` (eigene Marke, eigene Abbruchmeldung), Tests **+14 → +18**; **V5** füllt ausdrücklich **nur die Aussagen**; **K7** bekommt **Schritt 4** (Messwerte nach dem Worker-Stopp ziehen, einfügen, hashen). Der Snapshot-Zeitpunkt ist entschieden — **nach dem Worker-Stopp** —, der andere Weg steht als benannte, verworfene Alternative in Spec 4.2. |
| **2** | Keine Prüfung erzwingt genau eine Klassifikationsart je Kanal [high] — bei beiden Arten überschreibt Backfill (b) das Ergebnis von (a). | **T1.3a** bekommt den Builder-Fall (Doppelung wird beim Eintragen abgelehnt) und die zweite Abbruchgestalt von Prüfung 3; **T1.3b** setzt die Unique-Constraints auf beide temporären Tabellen und stellt den XOR-Testfall **an einem `IsBotActive = false`-Kanal**, wo der partielle Unique-Index der Saat nicht schützt; **T1.10** konstruiert denselben Verstoß gegen die wiederhergestellte Datenbank. |
| **3** | Der geteilte Breaker macht einen listenlokalen Fehler zum Ausfall der Vorschau [medium]. | **T2.1**: `ForeignSevenTvBreakerPolicy` steht **nicht mehr** in der „unverändert wiederverwenden"-Liste, sondern bekommt eine **Operationskennung** — Fehlerzähler und Half-open-Probe je Operation, Rate-Limit-Sperre und Budget providerweit. Eine zweite Instanz nach Bestenlisten-Muster wurde geprüft und **verworfen** (sie trennte auch das 429). `ForeignSevenTvBreakerPolicyTests` **12 rot, +4**; Kreuztest im Listen-Dienst **+2**; Modellbegründung auf zwei Bestandsklassen erweitert. **Das ist die Korrektur einer Einschätzung des Orchestrators, nicht eine geänderte Faktenlage** — der Code war schon vorher so, die Begründung „beide teilen das Budget, also teilen sie den Breaker" war die falsche Gleichsetzung. |
| **4** | T1.10 belegt die Fensterlänge nicht [medium] — lokales Docker-Postgres, frisch aufgebaute Indizes, und gemessen wird nur `Up`. | **T1.10** ist ausdrücklich **Funktionsprobe und Untergrenze**; es liefert zusätzlich eine als Schätzung beschriftete **Hochrechnung** (× Zeilenverhältnis × 3) und stoppt den neuen Messwert-Schritt. **K7** trägt den **Abbruchpunkt**: 10 Minuten für `Up`, danach Nulllauf statt Warten, plus die Vorab-Regel „Hochrechnung über 10 min ⇒ Fenster anders planen". AK 86 ist von der Probe entkoppelt (Spec AK 87/93). |
| **5** | Der zweite `/series`-Schritt hängt nicht hinter dem Deploy [medium]. | **T4.3**, **Abschnitt 4** und der Nachtrag zu Befund C nennen jetzt **K7** statt „Merge von K4"; **K7** legt **ein** Folge-Issue für **beide** Übergangsfelder an (Spec 21: hinter K7, ≥ 14 Tage, Beleg aus dem Betrieb). |
| **6** | Herkunft und Wiederherstellbarkeit der lokalen Klassifikation sind unbelegt [medium]. | **V5** bekommt den Handgriff „Hash bilden und ablegen" (SHA-256 + `git rev-parse HEAD` + Datum/Zeilenzahl der Zählabfrage, Datei nach `infra-docs`); **T1.10** prüft denselben Hash vor dem ersten Lauf; **K7** bekommt **Schritt −1** (Hash prüfen, Datei notfalls aus `infra-docs` zurückholen) und nimmt beide Hashes in den `docs:`-Nachtrag auf. Neue Spec-**AK 92**. |

**Dazu drei Dokument-Widersprüche, die Codex als Drift-Beleg genannt hat — alle drei behoben:**

| # | Widerspruch | Behoben |
|---|---|---|
| **1** | **V1 behauptete, `SetSwitchAssignments` enthalte „nur HandOfBlood"** — das widerspricht **V5**, wo jeder Kanal mit Nutzungszeilen einen Kein-Wechsel-Eintrag bekommt. Der Satz war ein Rest aus der Zeit vor der lückenlosen Klassifikation. | V1 (2.K0) sagt jetzt: die Liste umfasst **alle** Kanäle mit Nutzungszeilen, **nur die Wechseleinträge** betreffen allein HandOfBlood; Wegwerf- und Testkanal stehen in keiner der beiden Arten, weil V3/V2 sie vorher purgen. |
| **2** | **Die K1-Zeile in Spec 12 ließ AK 87–89 aus** — die Kriterien aus T1.10 fehlten in der Kind-Issue-Zuordnung, obwohl Abschnitt 6 dieses Plans sie längst als K1-Gate führte. | Spec 12: K1 trägt jetzt **AK 5–20, 87–92**, K2 zusätzlich **94**, K7 zusätzlich **92, 93**. Abschnitt 6 hier nennt dieselben Nummern. |
| **3** | **Die Testsumme stand bei +236 (Plan) gegen +237 (Spec).** | Beide waren falsch. Die Tabellen in Spec 15.1–15.5 addieren sich — vor dieser Runde — auf **+261**; die beiden Zahlen waren fortgeschriebene Gegenrechnungen und nie Summen. Nachgerechnet und in beiden Dokumenten auf **+277** gesetzt (inkl. der +16 aus dieser Runde); die Gegenrechnung ist abgeschafft, es gilt die Summe der Tabellen. „Bestand rot" steigt von ≥ 76 auf **≥ 88** (die 12 Breaker-Tests). |

---

## 9. Nachtrag: Rückschnitt des Plans, 2026-09-20

Codex hat in der zweiten Runde festgestellt, der Plan sei „faktisch ein zweites Entwurfsdokument"
und drifte bereits — belegt an drei Widersprüchen zwischen beiden Dokumenten (8, Tabelle
„Dokument-Widersprüche") und daran, dass die Testsumme in **beiden** Dateien falsch war, weil sie
unabhängig voneinander fortgeschrieben wurde. Der Betreiber hat daraufhin entschieden, den Plan
zurückzuschneiden: **die Spec ist die einzige Vertragsquelle, der Plan trägt Reihenfolge,
Abhängigkeiten, Handgriffe und Gates.** Wire-Formate, Request-/Response-Shapes, Fehlercodes,
Schema- und Indexdefinitionen, `ON CONFLICT`-Formen, UI-Verträge, Beschriftungsregeln,
Zustandsmatrizen, die Definition der Abbruchprüfungen, die Eintragsarten der Klassifikation, die
Härtungskette des Listen-Dienstes und die Regel für die Übergangsfelder stehen seither **nur** in
der Spec und hier nur noch als Verweis. Geblieben sind Task-Schnitt, betroffene Dateien mit Zeilen,
Testerwartung je Task, Modellwahl samt Begründung der `opus`-Fälle, Gates und Live-Verifikationen,
die Betreiber-Handgriffe mit ihren Kommandozeilen, der Rückweg je Task und diese Nachträge.
Gestrichen wurde dabei nichts: jede Aussage, die nur hier stand, ist **vorher** in die Spec
übernommen worden (4.2 zur Form der partiellen Methode samt SDK-Messung und zur Sprache der
Abbruchmeldungen, 4.2 zum Verbleib der Messwert-Datei nach dem Lauf, 6.1 zur Pflicht der
Operationskennung, 18 um `CLAUDE.md`, AK 87 um die getrennte Messung von Prüfung 7).

**Im selben Durchgang nachgezogen:** die E1-Zeile in Spec 2 trug noch die Ein-Datei-Lösung und den
Kein-Wechsel-Eintrag ohne die drei `Confirmed*`-Felder; sie trägt jetzt den aktuellen Stand, die
Geschichte steht in den Nachträgen (Spec 24/N3, Spec 25/Befund 1 und 2). Die übrigen E-Zeilen sind
dabei auf denselben Fehler geprüft — E7, E21 und E25 tragen ihre Aktualisierung bereits in place,
alle anderen sind unverändert gültig.

**Offen geblieben: fünf Zahlen, die nicht zusammenpassen.** Sie sind beim Gegenlesen nach dem
Schnitt aufgefallen, gehören zu derselben Drift und werden **nicht** im Vorbeigehen entschieden —
wer die Datei anfasst, zählt nach und zieht beide Dokumente gerade:

| Datei | Spec 15 | Summe der Task-Erwartungen hier |
|---|---|---|
| `Api.Tests/AuthFilterMatrixTests.cs` | +14 | T2.3 +8, T2.4 +1, T5.2 +5, T6.1 +4 = **+18** (die Aufzählung in T2.3 liest sich als 9, nicht 8) |
| `Api.Tests/SevenTvForeignEmoteSetEndpointTests.cs` | +9 | T2.3 +6, T3.1 +5 = **+11** |
| `Integration/EmoteServiceTests.cs` | +6 | T2.4 +2, T5.2 +6 = **+8** |
| `Integration/SevenTvSyncServiceTests.cs` | +5 | T1.2 +2, T1.5 +1, T1.7 +2, T6.2 +2 = **+7** |
| `Integration/VoteSessionServiceTests.cs` | +6 | T6.1 **+5** — AK 78 (Wettlauf) führt Spec 15.1 **zweimal**, hier **und** bei `SevenTvSyncServiceTests`; der Plan legt ihn zu T6.2 |

Dazu ergibt die Aufzählung der Migrationstests in Spec 15.1 addiert **19** statt der dort genannten
**+18** (T1.3b). Die Gesamtzahl **+277** bleibt bis zur Klärung stehen; sie ist die Summe der
Spec-Tabellen, und geändert wird sie erst, wenn die fünf Zeilen entschieden sind — nicht durch eine
neue Gegenrechnung.

**Geprüft und in Ordnung:** sieben Abbruchprüfungen, fünfzehn `Up`-Schritte, zwei `Down`-Schranken,
die Zahl der Kind-Issues und die AK-Zuordnung je Kind-Issue (Spec 12 gegen Abschnitt 6 hier:
K1 = AK 5–20 und 87–92, K2 = 21–46 und 94, K3 = 47–49, K4 = 50–66, K5 = 67–74, K6 = 75–83,
K7 = 84–86, 92, 93) stimmen nach dem Schnitt in beiden Dokumenten überein.

**Nicht angefasst:** drei Tabellenzeilen der Spec sehen für einen Pipe-Zähler defekt aus, weil sie
escapete `\|` in Codespans tragen — die E8-Zeile in Abschnitt 2, die Protokollzeile in 7.2 und die
`emote-usage-filter`-Zeile in 19 (am 2026-09-20 die Zeilen 96, 1251 und 2485). Sie sind in Ordnung;
eine „Reparatur" würde sie zerstören.
