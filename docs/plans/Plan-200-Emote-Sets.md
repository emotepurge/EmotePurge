# Plan #200 — Emote-Sets: nicht-aktive Sets ansehen, bearbeiten, übertragen, pro Set zählen

Erstellt am 2026-09-20 gegen `docs/konzept-emote-sets` = `93af613` (der Commit, der die Spec
enthält). Quellen: [die Spec](../superpowers/specs/2026-09-20-emote-sets-200-spec.md) (Abschnitte
werden als „Spec N" zitiert, Entscheidungen als E1–E25, Fallen als F1–F16, Akzeptanzkriterien als
AK n), [das Konzept](../Konzept-Emote-Sets-2026-09-19.md) nur, wo die Spec darauf verweist,
`CLAUDE.md` (Regeln 1–22, Schichtentreue, Gates), `web/.claude/CLAUDE.md`, und der Code auf
`93af613`. Formatvorlagen: [Plan-149](Plan-149-7TV-v4-Schreibflaeche.md), [Plan-91](Plan-91-Datei-Weg.md).

Der Plan enthält keinen Code, und er wiederholt die Spec nicht. Verträge stehen dort; hier steht, wer
sie in welcher Reihenfolge mit welchem Gate umsetzt. Wo ein Task ohne eine Vertragszeile mehrdeutig
wäre, ist sie zitiert — sonst verwiesen.

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
  `ExpectedArchivedCount`) entstehen erst am 01.10. — der Task, der sie einträgt (T1.9), ist deshalb
  der letzte Commit von K1, und nichts anderes wartet darauf.
- **#76 läuft auf einem eigenen Branch vor diesem Vorhaben** (Entscheidung des Betreibers vom
  2026-09-20). Die Spec setzt #76 nicht voraus (Spec 22, erste Zeile), aber T1.5 und T1.2 fassen
  `SevenTvSyncService.cs:74-109` an — genau die Stelle, an der #76 die Plausibilitätssperre hält.
  Folge für den Branch: s. 0.4.
- **Keine der sieben Sonden ist gemessen.** Der Plan hängt an keiner: jede Sonde hat in der Spec
  zwei Zweige, und die Tasks bauen den Zweig, den die Spec als Beschluss trägt (E7 v3, E12 60 s,
  8.9 Duplikate ausgenommen). Der andere Zweig ist je Sonde als **Umbaukosten** beziffert (2.1),
  damit die Entscheidung „warten oder bauen" eine Zahl hat.

### 0.2 Was der Plan beim Nachprüfen am Code gefunden hat

Zwei Punkte hat die Spec ausdrücklich als Prüfaufgabe an den Plan gegeben. Beide sind geprüft;
beide werden Tasks, nicht Fußnoten.

| Prüfaufgabe | Befund am Code (`93af613`) | Folge |
|---|---|---|
| **Gibt `core/routing/list-query-state` das Muster für den Set-Zustand in der URL her?** (Spec 8.1: „Zustand in der URL wie der Zeitraum") | **Die Prämisse stimmt nicht:** der Zeitraum der Nutzungsseite steht heute **nicht** in der URL. `usage-stats-page.ts` hat keinen `ActivatedRoute`-/`queryParamMap`-Zugriff; `rangePreset` (`:333`) ist ein lokales Signal. `listQueryState` (`core/routing/list-query-state.ts`, 142 Zeilen, 236 Zeilen Spec) ist für **paginierte Listen** gebaut (`page` + Filterparameter, `setParams` springt auf Seite 1, `replaceUrl`, Defaults werden aus der URL entfernt) und wird von fünf Listenseiten benutzt, nicht von der Nutzungsseite. | **T4.0** entscheidet zwischen zwei Wegen (s. dort). Das Set wäre der **erste** URL-getragene Zustand der Nutzungsseite; die Spec-Formulierung „wie der Zeitraum" ist damit ein Vorbild ohne Bestand und wird im DECISIONS-Eintrag 4 berichtigt. Ob der Zeitraum nachziehen soll, ist **nicht** Teil dieses Plans (3). |
| **Liefert `editor_of { user { id } }` die 7TV-ID des Account-Besitzers?** (Spec 19, Prüfaufgabe T2.4) | `GqlEditorOfQuery` (`SevenTvApiClient.cs:58`) liest heute `editor_of { user { connections { platform id username } } }` — das `user`-Objekt ist also schon der Besitzer, nur sein `id` wird nicht projiziert. Die Frage ist damit nicht „welches Objekt", sondern „trägt v3 dort ein `id`, und ist es die 7TV-ObjectID". Das misst nur ein Aufruf. | **T0.6**: eine curl-Sonde des Betreibers (7TV-Sonden blockt der Klassifikator aus einer Session). T2.4 darf vorher starten und trägt bis zur Messung eine als „unverifiziert" markierte Fixture. |

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
| Schema, Migration mit vier Prüfungen, Saat, `Down` | 4.1–4.3, E1, E11 | T1.3a, T1.3b, T1.9 |
| Zählpfad: Snapshot, Schlüssel, Flush, Log-Zeile | 5, F2 | T1.1, T1.2, T1.4, T1.7 |
| Lesepfade mit `emoteSetId`, `/series` nach 7TV-Id, `GetRowsAsync`-Summe, `NameTwinEmoteSetIds` | 5 (Tabelle), E15, E24 | T1.6 |
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
| 1 *Usage is counted per emote set …* | **T1.8** (eigener Task, weil der Eintrag Migration, Flush, Beobachtungs-Log **und** Wartungsfenster zusammenfasst und drei Subagents davor gearbeitet haben) | `feat(usage): count chat usage per emote set` — der Commit von T1.3b + T1.4 | die Werte aus V1 trägt **T1.9** nach (Nachtrag im selben Eintrag, eigener Commit); die Dauer des Fensters (AK 86) trägt K7 nach |
| 2 *An import may target any set of an account the user edits …* | **T2.5b** | `feat(import): pick any set of a tracked account as the target` | — |
| 3 *Voting: "member of the session's set" replaces "not archived" …* | **T6.3** | `feat(voting): show set-session results with frozen names and eligibility` | — |
| 4 *A row of the set view is identified by its 7TV id …* | **T4.3** (erster Teil) und **T5.2** (Nachtrag im selben Eintrag) | `feat(usage-stats): identify grid rows by their 7TV id and show non-active sets` bzw. `feat(api): accept set-scoped bookkeeping for sync-deleted and sync-restored` | berichtigt zusätzlich die Spec-Prämisse „URL wie der Zeitraum" (0.2) und hält fest, welchen Weg T4.0 gewählt hat |

Ein fünfter Eintrag ist nicht nötig (Spec 23, letzter Satz). Die Spec selbst bekommt je gemessener
Sonde einen Nachtrag (Zweig, Datum, der andere Zweig gestrichen — AK 1); das ist ein `docs:`-Commit
des jeweiligen K0-Handgriffs, kein DECISIONS-Eintrag.

---

## 2. Tasks

Jeder Task ist die Arbeit **eines** Subagents mit frischem Kontext. Format je Task: Ziel · Spec ·
Dateien · Tests · Gate · Abhängigkeiten · Modell · Commit. Die Prüfliste aus Spec 19 („Wiederverwendet
— als Prüfliste") wird vom jeweiligen Task **vor** dem Bauen abgehakt, wo sie ihn betrifft.

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

Ergebnis im Repo: Zweig A/B in Spec 11 (Sonde 1) und E7. **Zweig B** (Halloween fehlt oder ohne
Namen) kostet: T2.1 liest v4 statt v3 (eine Client-Methode, eine Fixture, `isPersonal` bleibt
`false` mit Vermerk) — kein anderer Task ändert sich. Die JSON-Antwort wird als Fixture für T2.1
in `tests/EmotePurge.Infrastructure.Tests/Fixtures/` abgelegt (redigiert auf drei Sets reicht).

#### T0.2 — Sonde 4: Reload-Frequenz der Nutzungsseite (Dev-Box)

Kein Kommando: Dev-Stack mit einem Kanal mit Chat-Betrieb, Nutzungsseite offen, im Netzwerk-Panel
den `EventSource` auf `/api/live/…` über **10 Minuten** zählen (`usage.flushed`, `channel.synced`).
Ergebnis im Repo: Zweig A (TTL 60 s) oder B (TTL 300 s für `7tvforeign:set:*`) in E12. **Zweig B**
kostet in T2.2 eine Konstante und einen Testfall — deshalb ist die Sonde kein Tor für T2.2.

#### T0.3 — Sonde 5: `REMOVE` bei doppelt eingetragener Emote-ID

Vier Aufrufe gegen ein **eigenes Testset** mit `<7TV-TOKEN>`, `<SET-ID>`, `<EMOTE-ID>` — die vier
curl-Zeilen stehen wörtlich in Spec 11 (Sonde 5) und werden hier nicht kopiert. Eine
GraphQL-Fehlermeldung ist ein Ergebnis, kein Anlass für Varianten; nach zwei Fehlschlägen derselben
Probe: abbrechen und fragen.

Ergebnis im Repo: Zweig A/B(5c) in Spec 11 und 8.9. **Betrifft T5.1 und T5.3:** bis zur Messung
bauen beide **8.9** (Duplikat-Zellen wählbar, aber vom Lauf ausgenommen, eigene Gruppe im Dialog).
Zweig A oder B-mit-Alias-Treffer ist danach ein eigener kleiner Task in K5 (Queue-Key
`sevenTvEmoteId#alias` nur im betroffenen Lauf, `REMOVE_OPERATION` mit Alias) — geschätzt eine
Sitzung `sonnet`, kein Vertrag außerhalb der Queue.

#### T0.4 — Sonde 6: Gegenprobe vor der Migration (lesend gegen Prod, unmittelbar vor K7)

```
ssh -N -L 15432:127.0.0.1:5433 vps
psql 'host=localhost port=15432 dbname=emotepurge user=emotepurge password=<PROD-PW>' -f set-wechsel-pruefung.sql
```

(`set-wechsel-pruefung.sql` aus Konzept 11.6.) **Ein Tor, kein Zweig:** genau zwei Zeilen (Testkanal
als Positivkontrolle, HandOfBlood mit Tag = `BoundaryUtc::date` und `archived_that_day` =
`ExpectedArchivedCount`) — sonst Halt, klären, Liste korrigieren, neu bauen; nichts wird geschätzt.
Ergebnis im Repo: das Ergebnis steht im DECISIONS-Eintrag 1 (Nachtrag durch K7).

#### T0.5 — Sonde 7: trägt v4 am `EmoteSet` ein Merkmal für persönliche Sets?

```
curl -s https://7tv.io/v4/gql -H 'Content-Type: application/json' -d '{"query":"query { __type(name: \"EmoteSet\") { fields { name type { name kind ofType { name kind } } } } }"}' | jq
```

Ergebnis im Repo: Zweig A (E7 dreht auf v4 — ein Request mit Besitzer, kein `flags`-Rätsel, kein
Anteil an der #43-Fläche) oder B (E7 bleibt v3). **Gehört vor T2.1**, ist aber kein Tor: T2.1 baut
ohne Messung den v3-Beschluss; Zweig A kostet danach dieselbe eine Client-Methode wie Sonde 1
Zweig B — die Kosten fallen also höchstens einmal an, egal welche der beiden Sonden dreht.

#### T0.6 — `editor_of { user { id } }` (neu, Prüfaufgabe der Spec)

```
curl -s https://7tv.io/v3/gql -H 'Content-Type: application/json' -d '{"query":"query($id: ObjectID!) { user(id: $id) { editor_of { id user { id username connections { platform id } } } } }","variables":{"id":"<7TV-USER-ID>"}}' | jq
```

`<7TV-USER-ID>` ist die eigene 7TV-ObjectID (ein Account, der irgendwo Editor ist). Zu prüfen: ist
`editor_of[].user.id` gesetzt, und ist es die ObjectID des Accounts, dessen `connections` daneben
stehen (Vergleich mit `GET /v3/users/twitch/<TWITCH-ID>` → `.user.id` desselben Accounts). Ergebnis
im Repo: die redigierte Antwort als Fixture in T2.4; Vermerk in Spec 19 (Prüfaufgabe erledigt).
**Gehört vor T2.4**; T2.4 darf vorher mit einer als unverifiziert markierten Fixture starten.

#### V1 — Zwischenweg vor dem 2026-10-01 (Konzept 12.4)

Wegwerfkanal (nie getrackt) bestimmen, Halloween dort aktiv, tracken, von HandOfBloods
Nutzungsseite übertragen, Kollisionen im Dialog abwählen; am 01.10. wechseln; **am Wechseltag**
`ExpectedArchivedCount` mit der ID-Sonde aus Konzept 11.2 messen und `BoundaryUtc` notieren. Beide
Werte gehen in **T1.9**. Kein Repo-Anteil vor T1.9.

#### V2 / V3 — Purges (Admin-UI, Prod)

V3: Wegwerfkanal nach dem 01.10. und **vor** Sonde 6 purgen. V2: Testkanal **nach** Sonde 6 und vor
K7 purgen (er ist die Positivkontrolle der Sonde). Ergebnis im Repo: AK 3/4 im PR-Text von T7.

### K1 — Zählen pro Set (Schritt 3)

Reihenfolge innerhalb K1: T1.1 → T1.2 → T1.3a → T1.3b → T1.4 → T1.8 (Commit) → T1.5 ∥ T1.6 → T1.7 →
T1.9 (nach dem 01.10.). Worktree A.

#### T1.1 — Schlüssel und Snapshot im Worker; Flush-Signatur mit vorläufiger Summe

**Ziel:** `UsageCounterKey`, `EmoteMatchSnapshot`, die drei Interface-Änderungen aus Spec 5 und die
puren Klassen dahinter — bei grünem Build, weil der Flush vorläufig je `EmoteId` summiert (0.2).

**Spec:** 5 (Signaturen, Regeln 3 und 5), F2.

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

**Spec:** 5 (Regeln 1–2), F2.

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

**Ziel:** Die Modelländerungen aus Spec 4.1 und die Entscheidungslogik der Migration als **pure
Funktionen** über Listen — vier Prüfungen und die Intervallzuordnung `Date` → Set-ID inklusive
Grenztag —, damit T1.3b sie nur noch in SQL abbildet und die Zweige einzeln getestet sind.

**Spec:** 4.1 (Tabelle), 4.2 (Prüfungen 1–4, Zuordnungsregel), E1 (Form der Liste).

**Dateien:** `Core/Entities/UsageStat.cs:3-26`, `Core/Entities/ChannelEmoteSetObservation.cs` (neu,
Muster `ChannelLiveDay`), `Core/Entities/VoteSession.cs:13-30`, `VoteSessionEmote.cs:7-14`,
`Infrastructure/Persistence/AppDbContext.cs:38-50` (neuer Unique-Index, Entität, partieller
Unique-Index — Muster `:52-65`), ein neuer purer Typ für die Prüfungen neben der Migration
(Infrastructure, kein EF-Bezug in der Signatur).

**Tests:** `Infrastructure.Tests/Unit/UsageStatMigrationChecksTests.cs` (neu, pur) **+8**:
je Prüfung 1–4 ein Abbruch- und ein Durchlauffall (Sonar zählt Zweige, Spec 15 letzter Absatz);
Zuordnung: `Date < boundary` alt, `>= boundary` neu, mehrere Grenzen (jüngste Grenze ≤ Date),
Grenztag ganz an die neue ID (Ein-Tages-Unschärfe, benannt).

**Gate:** BE grün — `dotnet ef migrations add` läuft hier **noch nicht** (das Snapshot-Diff gehört
zu T1.3b, sonst entsteht eine Migration ohne Prüfungen, die jemand versehentlich anwendet). Kein
Commit.

**Abhängigkeiten:** keine (parallel zu T1.1/T1.2 möglich, aber im selben Worktree — deshalb
sequenziell). **Modell:** `sonnet`.

#### T1.3b — Die Migration `AddUsageStatEmoteSetId`: Prüfungen, Backfill, Indextausch, Saat, `Down`

**Ziel:** Die eine nicht-additive Migration an der heißesten Tabelle — in der Reihenfolge aus Spec
4.2 (zwölf Schritte, verbindlich), in **einer** Transaktion, mit `SET LOCAL lock_timeout = '5s'` als
erstem Statement, ohne Default, ohne `COALESCE`; Saat aus derselben Eingabe (4.3); `Down`, das ab
dem ersten beobachteten Set-Wechsel **fehlschlägt** (gewollt).

**Spec:** 4.2, 4.3 (Saat), E1, E11, F1; Zuordnungsliste **leer** bis T1.9 (nur die Form; HandOfBlood
kommt mit den V1-Werten).

**Dateien:** `Infrastructure/Migrations/<stamp>_AddUsageStatEmoteSetId.cs` (neu, setzt auf
`20260907080507_AddUsageStatSharedChatUseCount` auf), `AppDbContextModelSnapshot.cs` (generiert).
`PendingMigrationGuard.cs:11` bleibt unverändert.

**Tests:** `Integration/AddUsageStatEmoteSetIdMigrationTests.cs` (neu, gegen den ephemeren
Container) **+9**: AK 5 (Prüfung 4: leere `ActiveEmoteSetId` ⇒ Ausnahme, **nichts** geändert), AK 6
(Prüfungen 1–3, drei Fälle), AK 7 (Backfill alt/neu/ungelistet), AK 8 (`pg_indexes`: alter Index
weg, neuer unique mit `INCLUDE`), AK 9 (Saat: aktiv-ungelistet eine offene Zeile ab
`COALESCE(TrackingResumedAt, CreatedAt)`; gelistet je Intervall; inaktiv keine), AK 10 (`Down` ohne
Wechsel stellt den alten Index her; mit zwei Set-IDs an einem `(EmoteId, Date)` schlägt sie fehl).
`Integration/PendingMigrationGuardTests.cs` bleibt grün. Der Testaufbau braucht ein Muster, um die
Migration **bis zu einem bestimmten Stand** anzuwenden und dann Zeilen zu setzen — der Task legt
es in `Fixtures/` ab, damit T1.9 es wiederverwendet.

**Gate:** BE grün; `dotnet ef migrations list` gegen die lokale Dev-DB zeigt genau eine Pending;
`dotnet ef database update` lokal läuft durch **und** `dotnet ef database update
20260907080507_AddUsageStatSharedChatUseCount` rollt sauber zurück (Dev-DB hat keinen
Set-Wechsel). Kein Commit (Flush folgt).

**Abhängigkeiten:** T1.3a. **Modell:** `opus` — die einzige Migration des Vorhabens, die ein
laufendes altes Image nicht verträgt (F1), mit vier `RAISE`-Zweigen, zwei `UPDATE`s in fester
Reihenfolge und einem `Down`, das absichtlich scheitern muss; jeder Fehler hier ist eine falsche
Zuordnung, die kein späterer Task erkennt.

#### T1.4 — Der Flush schreibt dreispaltig

**Ziel:** Vier `UNNEST`-Arrays (fünf Parameter plus `@date`), Conflict-Target dreispaltig, die
vorläufige Summe aus T1.1 entfernt; das Zurückstellen fehlgeschlagener Batches bleibt korrekt, weil
der Schlüssel das Set konserviert.

**Spec:** 5 (Regel 4), 4.1 (Conflict-Target = Index).

**Dateien:** `Infrastructure/Services/UsageStatFlushService.cs:29-32,67-87`.

**Tests:** `Integration/UsageStatFlushServiceTests.cs` **+3** (AK 11: zwei Schlüssel `(E, S1)`,
`(E, S2)` am selben Tag ⇒ zwei Zeilen; zweiter Flush auf `(E, S1)` addiert; alle drei Zählerspalten
unabhängig; zurückgestellter Batch mit anderer Set-ID). Die 14 umgestellten Fälle aus T1.1 bleiben
grün.

**Gate:** BE grün. **Kein Commit** — T1.8 schreibt den Eintrag und commitet T1.3a + T1.3b + T1.4 +
T1.8 zusammen.

**Abhängigkeiten:** T1.1, T1.3b. **Modell:** `sonnet`.

#### T1.8 — DECISIONS-Eintrag 1 und der Commit

**Ziel:** Der Eintrag *Usage is counted per emote set; the observed set travels with the match cache*
mit dem Inhalt aus Spec 23 (Zeile 1) — englisch, `**Betrifft:**` mit den Dateien aus T1.1–T1.4, und
zusätzlich: die #76-Blockade als offene Fehlerquelle (Spec 22, Zeile 1), die Rückrollgrenze (Spec 17),
und ein Platzhalter-Absatz „Zuordnungsliste: Werte folgen mit dem Nachtrag vom 2026-10-0x" (T1.9
füllt ihn).

**Gate:** BE grün auf dem Gesamtstand; `git status` zeigt nur die Dateien aus T1.3a/T1.3b/T1.4
und `docs/DECISIONS.md`. **Commit:** `feat(usage): count chat usage per emote set`.

**Abhängigkeiten:** T1.4. **Modell:** `sonnet`.

#### T1.5 — Beobachtungs-Log: Dienst und alle Schließ-/Öffnungsstellen

**Ziel:** `IChannelEmoteSetObservationService` (Core) / Implementierung (Infrastructure) trägt die
Regeln aus der Tabelle in Spec 4.3; kein Aufrufer schreibt die Tabelle direkt. Öffnen nach
erfolgreichem Sync **in derselben `SaveChangesAsync`** wie `:108`; Set-Wechsel schließt und öffnet
in **einer** Transaktion; `Leave`, Rename (zwei Stellen), Merge schließen mit ihrem `ClosedBy`;
Delta-Pfad und #76-Sperre schreiben nichts.

**Spec:** 4.3, F9.

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

#### T1.6 — Lesepfade: Set-Filter, `/series` nach 7TV-Id, `GetRowsAsync`-Summe, `NameTwinEmoteSetIds`

**Ziel:** Die Tabelle in Spec 5 („Lesepfade") Zeile für Zeile: fünf Methoden bekommen
`string? emoteSetId` (`null` = `Channel.ActiveEmoteSetId`), `GetTotalsByEmoteIdsAsync` den
**Pflicht**parameter, `GetRowsAsync` summiert (E15), `GetChannelSeriesAsync` benennt nach
`SevenTvEmoteId` (F4), `GetUsageContextAsync` liefert `IsArchived` und `NameTwinEmoteSetIds` (E24)
und für ein nicht-aktives Set die Grundmenge „Zeilen mit ≥ 1 `UsageStat` unter X, archivierte
eingeschlossen" (E16). Der Aufrufer `VoteSessionQueryService.cs:101-103` übergibt vorerst
`channel.ActiveEmoteSetId` (K6 ersetzt das durch `session.EmoteSetId ?? …`).

**Spec:** 5 (Tabelle), E15, E16, E24, F4, F11; Regel 10.

**Dateien:** `Core/Services/IUsageStatQueryService.cs` (fünf Signaturen, `EmoteSeriesEntryDto`,
`EmoteUsageContextDto`), `Infrastructure/Services/UsageStatQueryService.cs:26-104,106-171,173-236,
238-264,326-352`, `VoteSessionQueryService.cs:101-103` (Aufruf), `Api/Endpoints/UsageStatsEndpoints.cs:35-100`
(Query-Parameter durchreichen — der Validierungsfilter kommt aus T2.3; bis dahin reicht der
Endpunkt den Rohwert durch, und das ist auf dem Integrationsbranch hinnehmbar, weil K2 vor T7
mergt). `Api.Tests/ChannelUsageSeriesWireFormatTests.cs:23-37` — **der eine Test, der bewusst rot
wird und geändert wird** (`"sevenTvEmoteId"`); `:39-49` bleibt.

**Tests:** `Integration/UsageStatQueryServiceTests.cs` (54 Fälle) — **Teilaufgabe:** zählen, wie
viele an `GetChannelSeriesAsync`/`GetRowsAsync`/`EmoteSeriesEntryDto` hängen; umstellen; Zahl in
den PR-Text. **+10**: Set-Filter in Context/Daily/Series/Totals; `null` = aktiv; AK 19
(`GetRowsAsync` eine Zeile je `(EmoteId, Date)` als Summe — der Harness-Vertrag, F11); AK 20
(`/series`: aktiv + archiviert erscheinen, eine in der DB nicht vorhandene Guid-lose Zeile nicht);
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

#### T1.9 — Zuordnungsliste mit den Werten aus V1 füllen (letzter Commit von K1, nach dem 01.10.)

**Ziel:** Der HandOfBlood-Eintrag in `SetSwitchAssignments` (`ChannelId`, `TwitchChannelId`
`49140130`, alt `01GV88A38G0006FW5TVZVMG507`, neu `01J94NYQR0000D15QN0BDGN85E`, `BoundaryUtc`,
`ExpectedArchivedCount` aus V1) und der Nachtrag im DECISIONS-Eintrag 1 (AK 2). Testkanal und
Wegwerfkanal stehen **nicht** in der Liste.

**Tests:** `Integration/AddUsageStatEmoteSetIdMigrationTests.cs` **+1**: mit dem echten Eintrag und
einer synthetischen Datenbank, die die Signatur trägt, läuft die Migration durch; mit
`ExpectedArchivedCount ± 1` bricht Prüfung 2 ab (der Wert ist damit im Test verankert, nicht nur
im Code).

**Gate:** BE grün. **Commit:** `feat(usage): fill the set-switch assignment list for the
migration`. Danach der K1-PR gegen den Integrationsbranch.

**Abhängigkeiten:** V1 (Werte), T1.8. **Modell:** `haiku` — eine Konstante, ein Test, ein
Nachtrag; das Denken ist in V1 und T1.3b erledigt.

### K2 — Ziel-Set-Picker (Schritt 4)

Reihenfolge: T2.1 ∥ T2.2 → T2.3 → T2.4 → T2.5a → T2.5b → T2.6 → T2.7. Worktree B, parallel zu K1.

#### T2.1 — `ISevenTvEmoteSetListService`: v3 `emote_sets`, Cache, Budget

**Ziel:** Ein Dienst für drei Routen (E6): `ListByTwitchIdAsync` → v3 `users/twitch/{id}`,
`isPersonal = (flags & 4) != 0` (E7), Cache `7tvsets:{twitchId}` 60 s fail-open (E12), **ein Permit
je Upstream-Request** (F14); `null`-`user.emote_sets` ist `Unavailable`, nicht leer (F13).

**Spec:** 6.1 (Quelle), E6, E7, E12, F13, F14; Sonde 1/7 (Zweig-Kosten in K0).

**Dateien:** `Core/Services/ISevenTvEmoteSetListService.cs` (neu; `EmoteSetSummary`),
`Core/SevenTv/ISevenTvApiClient.cs` (`GetEmoteSetListForTwitchUserAsync`), `SevenTvModels.cs`,
`Infrastructure/SevenTv/SevenTvApiClient.cs:163-241` (Endpunkt wie der Sync, `:167`),
`SevenTvApiDtos.cs:114-118` (additive DTO-Klasse), `Infrastructure/Services/SevenTvEmoteSetListService.cs`
(neu), `ServiceCollectionExtensions.cs` (Registrierung).

**Tests:** `Unit/SevenTvApiClientEmoteSetListTests.cs` (neu) **+5** (AK 21: parsen aus der
Sonde-1-Fixture — bis T0.1 gemessen ist, eine synthetische Fixture in derselben Form, markiert;
`flags & 4`; `null` ⇒ `Unavailable`; 404 ⇒ `NoSevenTvAccount`; `capacity` 0 ⇒ `null`).
`Unit/SevenTvEmoteSetListServiceTests.cs` (neu) **+5** (AK 23/24: Treffer, Miss, Redis-Ausfall
fail-open, ein Permit je Request über `RecordingForeignUpstreamRequestBudget`, zweiter Aufruf in 60 s
ohne Upstream).

**Gate:** BE grün. **Commit:** `feat(seventv): list the emote sets of a 7TV account`.

**Abhängigkeiten:** keine; T0.5/T0.1 wünschenswert vorher (0.1). **Modell:** `sonnet`.

#### T2.2 — Set-Vorschau nach Set-ID: `capacity`/`name`, zweiter Schlüsselraum, `sevenTvUserId` nullbar

**Ziel:** Die Preview-Abfrage trägt `capacity` und `name` (F6); `ForeignEmoteSet` wird um
`EmoteSetName`, `Capacity` erweitert und `SevenTvUserId` nullbar (E8); `GetEmoteSetByIdAsync` läuft
hinter demselben Dekorator mit Schlüsselraum `7tvforeign:set:{setId}` und Coalescing-Schlüssel
`set:{setId}` (E12) — Breaker und Budget unverändert. **Prüfaufgabe aus Spec 19** (Teil des Tasks):
der Coalescer koalesziert `set:{id}` und `{login}` **getrennt** — mit Test belegen.

**Spec:** 6.4, E8, E12, F6, F15.

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

**Ziel:** `GET /api/channels/{c}/emote-sets` (in der `/emotes`-Gruppe, gleiche Filter),
`GET /api/seventv/me/emote-set-targets` (neue Gruppe `/api/seventv/me`, `ForeignEmoteLookup`),
`?emoteSetId=` an `…/emotes` (6.4), an `/usage-stats/{totals,daily,series}` (6.5) und an
`set-warning` (6.8, `CheckAsync` auf die Set-ID parametrisiert, E9); der Filter nach dem Muster
`ChannelNameValidationFilter` (E14); die vier Codes in `ApiErrorCodes.cs` **und** `api-error.ts`
**und** beiden Locales (E13, Regel 7). `EmoteRoutePolicyTests` bekommt die neuen Routen.

**Spec:** 6.1, 6.2, 6.4, 6.5, 6.8, 6.10, E9, E13, E14.

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

**Ziel:** `GqlEditorOfQuery` liest zusätzlich `user { id }`; `SevenTvEditorGrant(Entry).SevenTvUserId`
additiv; Legacy-Payload ohne 7TV-ID wird **live nachgelöst**, nie als „kein Editor" gelesen (F10);
`SyncImportedRequest.TargetEmoteSetId` dauerhaft nullbar (E5); `POST
/api/seventv/emote-sets/{id}/sync-imported` mit der Leiter aus Spec 6.7 (Vokabeltabelle in eine
gemeinsame statische Prüfmethode gezogen); `ProjectDetail` liefert `TargetEmoteSet`.

**Spec:** 6.7, E5, E22, F7, F10; T0.6 (Fixture).

**Dateien:** `SevenTvApiClient.cs:57-58,323-367`, `SevenTvModels.cs:207`,
`Core/Services/ISevenTvEditorService.cs:12,35`, `Infrastructure/Services/SevenTvEditorService.cs:14-57`,
`Infrastructure/Redis/ModRoleCache.cs:41`, `Api/Endpoints/EmoteEndpoints.cs:129-195,283-284`,
`SevenTvEndpoints.cs` (neue Gruppe `/api/seventv/emote-sets/{emoteSetId}`),
`Core/Services/IEmoteService.cs`, `Infrastructure/Services/EmoteService.cs:107-138`
(`MarkImportedAsync` + `MarkImportedToSetAsync`), `Core/Services/IAuditLogQueryService.cs:17-34`,
`Infrastructure/Services/AuditLogQueryService.cs:129-193`.

**Tests:** `Unit/SevenTvApiClientEditorOfTests.cs` (neu) **+2** (Fixture aus T0.6; fehlendes `id`
⇒ `null`); `Integration/ModRoleCacheTests.cs` oder neu `SevenTvEditorServiceTests.cs` **+2** (AK 31);
`Api.Tests/SevenTvEmoteSetSyncImportedEndpointTests.cs` (neu) **+9** (AK 30: 401; die sechs
400-Fälle aus `AuthFilterMatrixTests:394-503` gespiegelt; 404 `emote_set_not_found`; **403 bare**;
503 ohne Eintrag; 204 mit `ChannelName = null`); `AuthFilterMatrixTests` **+1** (`targetEmoteSetId`
ungültig ⇒ 400, AK 29); `Integration/EmoteServiceTests.cs` **+2** (Audit-Details des Imports mit
`targetIsActiveSetOfChannel` true/false/null); `Integration/AuditLogQueryServiceTests.cs` **+2**
(AK 32; die 19 bleiben grün). Frontend `core/audit/audit.model.ts` bekommt das Feld hier
(additiv), die Ansicht in T2.6.

**Gate:** BE grün; FE grün (Modell additiv). **Commit:** `feat(api): record imports into any set
the actor edits`.

**Abhängigkeiten:** T2.3 (Filter); T0.6 für die verifizierte Fixture — ohne Messung: Fixture
markiert, T2.7 prüft live. **Modell:** `sonnet`.

#### T2.5a — Picker: Angebotsliste aus 6.2, Klassen, Vorauswahl, eigener Kanal

**Ziel:** `ImportTargetChoice { scope, emoteSetId, channelName: string | null, ownerLogin, setName,
isTracked }`; der Picker lädt `GET /api/seventv/me/emote-set-targets` statt `listMine()`; Sets
klappen unter ihrem Account auf; getrackt oben, aktives Set beschriftet und vorausgewählt;
ungetrackt darunter gekennzeichnet; persönliche Sets deaktiviert mit Beschriftung; der eigene Kanal
bleibt in der Liste, nur das Quell-Set ist deaktiviert. `import-target-options.ts` wird durch eine
**pure Funktion** über die neue Antwort ersetzt (`import-target-choices.ts`).

**Spec:** 8.6 (erste drei Punkte), 6.2, E6.

**Dateien:** `web/src/app/shared/seven-tv/import-target-dialog.ts:29-32,211`,
`import-target-choices.ts` (neu, ersetzt `import-target-options.ts`),
`core/seven-tv/seven-tv-emote-set.service.ts` (neu — die drei Listen-Routen und `?emoteSetId=`;
K3 und K4 nutzen ihn mit), `de.json`/`en.json` (Beschriftungen „aktiv", „nicht getrackt",
„persönlich", „das ist die Quelle").

**Tests:** `import-target-dialog.spec.ts` (**28 rot**, Datenquelle wechselt) + `import-target-options.spec.ts`
(**4 rot**, Datei wandert nach `import-target-choices.spec.ts`) — beide umgestellt, **+6** (AK 34:
Reihenfolge getrackt/ungetrackt, Vorauswahl, persönliche deaktiviert **mit Grund**, eigener Kanal
gelistet mit deaktiviertem Quell-Set, `sevenTvUnavailable`/`setsUnavailable`-Hinweise als
Sperrgrund). `core/seven-tv/seven-tv-emote-set.service.spec.ts` (neu) **+4** (drei Routen,
`?emoteSetId=`, `HttpTestingController`). Nach Regel 12: Klassen und Rollen ja, Wortlaut nein.

**Gate:** FE grün. Kein Commit (T2.5b folgt in denselben Commit, weil der Picker ohne Loader ins
falsche Set schriebe — F5).

**Abhängigkeiten:** T2.3. **Modell:** `sonnet`.

#### T2.5b — Loader mit Set-Ziel, Preview-Gruppen, Projektion ohne Kollisionen, Setname im Kopf; DECISIONS 2

**Ziel:** `loadImportTarget(emoteAdminService, foreignEmoteSetService, choice)`: getrackt **und**
`emoteSetId === activeEmoteSetId` ⇒ heutiger Weg; sonst Live-Liste nach Set-ID mit `occupiedSlots =
totalCount`, `capacity`, `setName`, `truncated ⇒ failed`; Warnung getrackt ⇒
`set-warning?emoteSetId=`, ungetrackt ⇒ `UNAVAILABLE_WARNING`. `buildImportPreview` nimmt
Namenskollisionen **aus `toAdd`** und führt sie als Gruppe; Alias-Abweichungen als Gruppe mit
Quellname/Zielalias; `projectSlots` rechnet ohne Kollisionen. Dialogkopf nennt Kanal **und** Setname.
`reportImported` sendet `targetEmoteSetId` bei **jedem** `sync-imported` (AK 44). Der Eintrag 2
liegt in diesem Commit.

**Spec:** 8.6 (Punkte 4–8), F5, Spec 23 Zeile 2.

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

**Ziel:** Wahl eines ungetrackten Sets ⇒ Bestätigung „In das Set ‚<setName>' von ‚<ownerLogin>'
kopieren?" vor dem Schließen; Abbruch lässt die Wahl unverändert; nach dem Lauf Report an den
set-zentrierten Endpunkt **ohne** Nachlauf-Resync; `audit-row.ts` zeigt „in Set <id-Kurzform>",
„(nicht das aktive Set)", „für <ownerLogin>" (8.10).

**Spec:** 8.6 (Bestätigung, Report), 8.10, 6.7.

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
Admin-Audit-Ansicht mit `ChannelName = null` und `ownerLogin`. Und die T0.6-Fixture gegen die echte
Antwort geprüft, falls T0.6 vorher nicht gemessen war.

**Gate:** die drei Befunde im PR-Text; danach der K2-PR gegen den Integrationsbranch. Kein Commit,
außer eine Korrektur nötig ist (dann `fix(import): …`).

**Abhängigkeiten:** T2.6. **Modell:** `sonnet`.

### K3 — Quell-Set-Picker beim fremden Kanal (Schritt 5)

#### T3.1 — Route 6.3 und Radiogroup im `ForeignChannelStep`

**Ziel:** `GET /api/seventv/channels/{c}/emote-sets` in der bestehenden Gruppe (Helix by login →
Twitch-ID → Listen-Dienst; `activeEmoteSetId` aus v3 `emote_set_id`, `observations: []`; Zustände
wie `…/emotes :47-69`); im `ForeignChannelStep` nach der Auflösung ein Radiogroup (aktives
vorausgewählt und beschriftet, persönliche deaktiviert); Vorschau mit `?emoteSetId=<gewählt>`, **kein**
zweiter Request, wenn das aktive Set gewählt bleibt; `ForeignChannelImportResult.emoteSetId` trägt
das gewählte Set.

**Spec:** 6.3, 8.7.

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

**Ziel:** Die pure Funktion aus Spec 7.1 in `core/usage-stats/merge-set-view.ts` und das geänderte
`EmoteUsageTotal` — **vor** jeder Template-Änderung; die Seite kompiliert danach noch gegen das alte
Modell? Nein: `emoteId: string | null` und `totalUseCount: number | null` brechen Leser (F16). Der
Task ändert deshalb das Modell **und** legt die Funktion an, lässt die Seite aber noch mit
`live === null` (aktive Ansicht: jede Zeile `'live'`, `slotCount 1`) laufen, sodass die `null`-Zweige
nur die neue Funktion kennt.

**Spec:** 7.1, E16, E17, E23, F16.

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

**Ziel:** `emote-set-menu.ts` (neu, Muster `date-range-menu.ts:104-133`, `Popover` +
`role="radiogroup"`) in der Kopfzeile neben dem Datumsbereich; aktives vorausgewählt und beschriftet,
persönliche deaktiviert mit Beschriftung, **kein** „Gesamt"; Wechsel ⇒ `selection.retainAmong` mit
der #94-Meldung (kein `clear()`), `clearSeriesCache()`, `/totals` + `/series` mit `emoteSetId`;
Set-Liste **einmal je Kanal-Aufruf** und bei lautem Reload (E19); URL-Zustand nach T4.0. Die
Gates auf `activeEmoteSetId()` (`.html:108`, `:890`) stehen auf `selectedEmoteSetId()`. Die
Live-Liste lädt hier noch **nicht** (T4.4) — der Wechsel zeigt in diesem Zwischenstand nur DB-Zeilen.

**Spec:** 8.1, E19, 7.3 (Cache-Schlüssel und `clearSeriesCache` beim Set-Wechsel).

**Dateien:** `shared/emotes/emote-set-menu.ts` (neu), `usage-stats-page.ts:1665-1667` (Cache-Clear),
`.html:108,890` und die Kopfzeile, `core/usage-stats/usage-stat.service.ts:33,59,73-76`
(Cache-Schlüssel mit Set; beide Methoden bekommen `emoteSetId`), `de.json`/`en.json`.

**Tests:** `core/usage-stats/usage-stat.service.spec.ts` (**5 rot**, Signaturen) umgestellt, **+2**
(AK 64: zwei Sets, gleicher Zeitraum ⇒ zwei Requests). `features/usage-stats/usage-stats-page.spec.ts`
**+4** (AK 50: Optionen und Vorauswahl über Rolle/Name; AK 51: `retainAmong` statt `clear`,
`clearSeriesCache`, `/totals`/`/series` mit `emoteSetId`, Set-Liste **nicht** neu geladen; AK 52
teilweise: `usage.flushed` lädt die Set-Liste nicht — Request-Zählung mit `HttpTestingController`;
unbekanntes `emoteSetId` in der URL ⇒ aktives Set). Ggf. `shared/emotes/emote-set-menu.spec.ts`
für die Sperrentscheidung „persönlich ⇒ deaktiviert mit Grund" (Regel 12: Sperre samt Grund ist
erlaubt).

**Gate:** FE grün. **Commit:** `feat(usage-stats): switch the set from the page header`.

**Abhängigkeiten:** T4.1, T4.0 (Entscheidung), T2.3 (Route). **Modell:** `sonnet`.

#### T4.3 — Schlüsselwechsel (Spec 7.2) und DECISIONS-Eintrag 4

**Ziel:** Der Schlüssel des Rasters und des inneren `track` ist `sevenTvEmoteId`; jede Map der
Seite ebenso; `DeletableEmote.emoteId?`, `DeleteQueueEmote.emoteId?`; Drilldown nur für
`emoteId !== null && totalUseCount !== null` mit `emoteSetId` eingefroren in `data`; Voting-Draht
als Signal der 7TV-Ids mit Auflösung in Guids **im Absende-Moment** über `selectedItems()` (E4);
Vote-Session-Detailseite behält die Guid (F12 — nur `onDeleted` ändert sich, das ist T5.1). Der
Eintrag 4 (erster Teil) inklusive der Berichtigung aus 0.2 und der T4.0-Entscheidung.

**Spec:** 7.2 (Tabelle, Zeilen 1–3, 11–12), 7.3, F3, F4, Spec 23 Zeile 4.

**Dateien:** `usage-stats-page.ts:488-494,533-537,549-553,561-565,678-680,1349-1350,1375`,
`usage-stats-page.html:585` (**`:524` `trackBy: trackRow` bleibt** — DECISIONS 2026-08-30),
`shared/seven-tv/mass-delete-panel.ts:34-37`, `core/seven-tv/seven-tv-delete.service.ts:55-59`,
`emote-drilldown-dialog.ts:319-321`, `features/usage-stats/create-vote-session-dialog.ts`,
`docs/DECISIONS.md`. **Nicht:** Queue-Keys, Protokoll, `doneIds` — das ist T5.1; hier werden die
Typen nur so weit optional, dass T4.4 Guid-lose Zeilen anzeigen kann, ohne dass ein Lauf sie
schon annimmt (8.9-Sperre bleibt bis T5.1 auf „Guid-lose Zeile nicht wählbar zum Löschen").

**Tests:** `usage-stats-page.spec.ts` (42) — **Teilaufgabe:** die Fälle zählen, die `emoteId`-Keys
oder `totalUseCount: number` voraussetzen; umstellen; **+3** (AK 54 als Konsument-Spec: zwei
Guid-lose Zeilen einzeln wählbar und unterscheidbar, `retainAmong` behält die richtige; Drilldown-
Gate; Voting-Auflösung im Absende-Moment liefert Guids für eine Null-Session). `shared/selection/list-selection.spec.ts`
**+2** (AK 54 über `sevenTvEmoteId`-Keys; **die Klasse selbst bleibt unverändert** — Spec 18). `create-vote-session-dialog.spec.ts`
— Fälle zu `emoteIds` zählen und umstellen. E2E-Mock `e2e/support/mocks.ts:688-703`
(`mockUsageChannelSeries` auf `sevenTvEmoteId`) — **die 30 bestehenden `usage-atlas`-Fälle laufen
mit umgestelltem Mock** (Teilaufgabe: Lauf, Zahl im PR-Text).

**Gate:** FE grün; E2E `usage-atlas` grün. **Kein eigener Commit** — T4.4 folgt in denselben, weil
das Raster ab der ersten Guid-losen Zeile nicht mehr auf der Guid stehen darf (NG0955) und
umgekehrt ein Schlüsselwechsel ohne Guid-lose Zeile nichts beweist (E25).

**Abhängigkeiten:** T4.2. **Modell:** `opus` — der Identitätswechsel quer durch 1.904 Zeilen mit
zehn Schlüsselstellen (F3), von denen eine falsche Map genügt, damit Rang, Füllgrad oder Serie zur
falschen Zelle gehören; und der DECISIONS-Eintrag, der drei Verträge auf einmal revidiert.

#### T4.4 — Nicht-aktive Ansicht: Laden, Klassen, Badge, `null`-Gruppe, Duplikat-Zelle, Namensvetter, Tatsachenangabe, Preset, Sperren

**Ziel:** Beim Wählen eines nicht-aktiven Sets `/totals`, `/series` (DB) und
`…/emotes?emoteSetId=` (7TV) **parallel**, Vereinigung erst, wenn beide da sind; stiller Reload
ohne Live-Liste, lauter mit (8.3, E16); Zeilenklassen nach 8.2 (`'left'`-Badge wie `archivedBadge`,
nicht wählbar mit Grund; `null`-Gruppe am Ende in Namensreihenfolge, nicht in Summe/Pareto/Bändern —
F16; Slot-Zahl an Duplikat-Zellen mit beiden Aliasen im Tooltip; Namensvetter-Merkmal mit Setname
aus der Dropdown-Liste); Kapazität/Belegung fürs Dock aus der Live-Liste, nicht aus `EmoteSetStatus`;
Live-Liste 503/429 oder `truncated` ⇒ Hinweis, Dock gesperrt **mit Grund**; Tatsachenangabe (8.4)
über `observations`; Preset `'set-observed'` (8.5) nur mit Intervall.

**Spec:** 8.2–8.5, E16, E17, E20, E23, E24, F16.

**Dateien:** `usage-stats-page.ts` (Ladepfad, `computed()`s für Klassen, Sperren mit Grund,
Tatsachenangabe über das `rangeStartsBeforeTracking`-Idiom `:383-398`), `usage-stats-page.html:344-346`
und das Raster, `shared/datetime/date-range-menu.ts:19-30` (Preset), `shared/emotes/emote-set-menu.ts`
(Setnamen für Tooltips), `de.json`/`en.json` (E17-Beschriftung, Badge-Zusatz, Sperrgründe,
Tatsachenangabe, Preset-Beschriftung „während dieses Set beobachtet wurde").

**Tests:** `usage-stats-page.spec.ts` **+8** (AK 52 vollständig: Request-Zählung stiller/lauter
Reload; AK 56: `null`-Gruppe außerhalb Summe/Nenner/Bändern, Beschriftung hängt an
`totalUseCount === null`, nicht an `emoteId === null` — Klasse 2b; AK 57: `'left'` nicht wählbar mit
Grund, zählt in Summe; AK 58: Slot-Projektion zählt 2; AK 59: Merkmal, keine Summierung; AK 60:
drei Fälle der Tatsachenangabe; AK 62: 503 und `truncated` ⇒ Sperre mit Grund, DB-Zeilen sichtbar).
`shared/datetime/date-range-menu.spec.ts` **+2** (AK 61). Nach Regel 12: Zustandsübergänge,
`computed()`-Ergebnisse, Sperrgründe, Rollen — keine Klassen, keine Tailwind-Ketten.

**Gate:** FE grün; E2E `usage-atlas` grün (die neuen E2E-Fälle kommen in T4.6). **Commit** (mit
T4.3): `feat(usage-stats): identify grid rows by their 7TV id and show non-active sets` — enthält
den DECISIONS-Eintrag 4.

**Abhängigkeiten:** T4.3, T1.6, T2.2. **Modell:** `opus` — dieselbe Datei, derselbe Commit, dieselbe
Fehlerklasse wie T4.3 (die `null`-Zweige in Sortierung, Bändern und Summe sind je ein stiller
`NaN`, F16); ein Wechsel des Subagents zwischen T4.3 und T4.4 ist trotzdem gewollt, damit der zweite
den ersten liest, bevor er auf ihm baut.

#### T4.5 — Export mit Set und `null`; Türen und Protokollprüfung auf das gewählte Set

**Ziel:** `UsageExportInput`/`Meta` mit `emoteSetId`, `emoteSetName`; Dateiname mit Set-Kennung
(letzte 6 Zeichen); `null`-Zeilen als leere CSV-Zelle / JSON `null` / `trend: 'unknown'`, nie
ausgelassen (7.4); `CapturedExportScope.emoteSetId` und `CapturedImportScope.emoteSetId` tragen das
**gewählte** Set (7.3); Datei-, Fremdkanal- und Bestenlisten-Import zielen auf das gewählte Set, das
Purge-Protokoll wird gegen `meta.emoteSetId` = gewähltes Set geprüft (8.6, letzter Punkt).

**Spec:** 7.3, 7.4, 8.6 (letzter Punkt).

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

**Ziel:** `SyncDeletedRequest` als **ein** Record mit zwei Formen und der Validierungsleiter aus
Spec 6.6 (400 **vor** jedem Service-Aufruf); Service-Überladung `(channelName, emoteSetId,
sevenTvEmoteIds, actor)`: aktives Set ⇒ Match über `(ChannelId, SevenTvEmoteId)`, Audit mit
`emoteSetId`, `targetIsActiveSetOfChannel: true`; nicht-aktives Set ⇒ **nur Papier**, `channel.synced`
feuert nicht; Altform ⇒ heutiger Pfad plus Log-Zeile (E3). `SyncDeletedResponse` mit
`targetIsActiveSetOfChannel`. Der Nachtrag zum Eintrag 4 liegt in diesem Commit.

**Spec:** 6.6, E3, Spec 23 Zeile 4 (Nachtrag).

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

**Ziel:** `RunResult.doneIds` **entfernt** (E2 — jede vergessene Stelle ist ein Compile-Fehler);
Queue-Key Delete/Restore `sevenTvEmoteId`; `PurgeRunRow.emoteId: string | null`, der Panel-Filter
„silently short protocol" entfällt, der Parser akzeptiert `null` **und** alte Protokolle mit Guid;
Restore aus Protokoll mit `emoteId ?? undefined`; `DeleteRunInfo`/`RestoreRunInfo` `+ setId`, beim
Start eingefroren, `reportDeleted`/`reportRestored` und beide Retries lesen Set-ID **und** Keys aus
dem Datensatz; `emote-admin.service.ts` sendet **nur** die neue Form; das Panel emittiert `deleted`
als 7TV-Ids (E18); beide Hosts filtern nach `sevenTvEmoteId` (F12). Bis T0.3 gemessen ist: 8.9
(Duplikat-Zellen vom Lauf ausgenommen, eigene Gruppe im Dialog).

**Spec:** 7.2 (Tabelle, Zeilen 4–10, 13), 8.9, E2, E18, F3, F12.

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

**Ziel:** `DeleteConfirmDialogData`/`RestoreConfirmDialogData` mit `setName`, `isActiveSet`; der
Dialog nennt „aus dem Set ‚<Name>'" und bei nicht-aktivem Set den Zusatz; **kein** zusätzlicher
Bestätigungsschritt (8.8); die Duplikat-Gruppe im Dialog (8.9, bis Sonde 5); der Nachweis nach
Regel 16.

**Spec:** 8.8, 8.9, AK 73/74.

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

**Ziel:** `CreateVoteSessionRequest` mit `emoteSetId` + `sevenTvEmoteIds` neben `emoteIds` (E4);
Ausschlussregel ⇒ 400 `vote_session_set_ballot_invalid`; `CreateAsync` Set-Pfad in den fünf
Schritten aus Spec 9 (Live-Liste nach Set-ID; All-or-nothing auf der 7TV-Identität; `INSERT … ON
CONFLICT DO NOTHING` mit `IsArchived = true`, `ArchivedAt = null`; **nachlesen**, Wahlzettel aus dem
Gelesenen; `NameAtCreation`/`ImageUrlAtCreation`; `VoteSession.EmoteSetId`); `IsEmoteVotableAsync`:
Set-Session „steht auf dem Wahlzettel", `IsArchived` kein Kriterium.

**Spec:** 6.9, 9 (Anlegen, Abstimmen), E4, F8.

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

**Ziel:** `SyncChannelAsync` fängt beim `SaveChangesAsync` die Unique-Verletzung auf
`IX_Emotes_ChannelId_SevenTvEmoteId` **genau einmal** ab, leert den Change-Tracker, lädt die
Kanalzeile neu, wiederholt Zuweisung + `ReconcileAsync` unter denselben Gates; der zweite Konflikt
propagiert wie heute (E10). Kein Advisory-Lock.

**Spec:** E10, AK 78.

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

**Ziel:** `GetResultsAsync`: `eligible` je Zeile (Null-Session `!IsArchived`, Set-Session `true`);
`useCount` für Manager einer Set-Session über `GetTotalsByEmoteIdsAsync(…, session.EmoteSetId)`;
Name/Bild aus `NameAtCreation ?? …`; kein Mid-Session-Badge für Set-Sessions.
`VoteSessionSummary`/Detail mit `emoteSetId`. Detailseite: `canSelectForDelete = canManage()`,
`hasUsageData` gatet nur Nutzungsspalte und Drilldown, Panel bekommt `session.emoteSetId ??
activeEmoteSetId()`, Badge und Vote-Sperre auf `eligible`. Dialog „Zur Abstimmung stellen" mit
Set-Session-Body aus der Set-Ansicht (`sevenTvEmoteIds` direkt, E4). Eintrag 3 in diesem Commit.

**Spec:** 9 (Ergebnisse, Detailseite, Klasse 2b), 6.9, F8, Spec 23 Zeile 3.

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
   Sonar misst `new_coverage` zeilen- **und** zweiggenau; die vier `RAISE`-Zweige der Migration
   haben je einen Test (T1.3b).
3. `/codex:review --model gpt-5.6-sol --scope branch --base origin/main` über das Ganze — nach den
   Kind-Issue-Reviews (0.4) ein Blick auf die Nähte zwischen den Kind-Issues. Findings sind Input;
   widersprechen sich Opus-Review und Codex bei einem P1/P2, entscheidet Fable (globale Regel).
4. PR-Text nennt: die Zahl der umgestellten Bestandstests je Datei (aus den Teilaufgaben), die
   gemessenen Sonden-Zweige, die Live-Befunde aus T1.7/T2.7/T5.3, AK 3/4 (Purges), die
   Rückrollgrenze, und dass der Deploy ein getrenntes Wartungsfenster (K7) ist.
5. Der Merge gehört dem Nutzer. Ein Push ist kein Deploy.

**Modell:** `sonnet` für die Läufe und den PR-Text; Codex und Fable-Schiedsrichter steuert die
Hauptsession.

### K7 — Wartungsfenster (Betreiber, kein Code, kein Modell)

Einmalig, **hinter** dem 2026-10-08, nach Freigabe gegen das Harness-Runbook in `infra-docs`
(Nachläufer des Laufs brauchen den Zählpfad unverändert). Die Reihenfolge ist Spec 10; hier die
Kommandozeilen und was danach ins Repo gehört. Vorher: Sonde 6 (T0.4) Zweig A, V2/V3 erledigt, alte
Image-Tags notiert (Rollback).

| Schritt | Handgriff |
|---|---|
| 0 | Uptime-Kuma-Monitor pausieren; Zeit notieren (AK 86 misst Schritt 1 bis 6) |
| 1 | Portainer: Stack-Service `worker` stoppen — `stop_grace_period: 60s` abwarten (Shutdown-Flush schreibt gegen das **alte** Schema) |
| 2 | Portainer: `api` stoppen |
| 3 | `ssh -N -L 15432:127.0.0.1:5433 vps` — dann in einer zweiten Shell: `dotnet ef migrations list --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>'` — **genau eine** Pending (`AddUsageStatEmoteSetId`); mehr heißt: Prod hängt Runden zurück, erst durchsehen |
| 4 | `dotnet ef database update --project src/EmotePurge.Infrastructure --startup-project src/EmotePurge.Api --connection 'Host=localhost;Port=15432;Database=emotepurge;Username=emotepurge;Password=<PROD-PW>;Options=-c lock_timeout=5s'` — bricht bei Prüfung 1–4 ab statt zu schätzen; ein Abbruch ist ein Befund, kein Anlass zum Nachhelfen |
| 5 | `dotnet ef migrations list …` — keine Pending |
| 6 | Portainer: neue Images (`ghcr.io/emotepurge/…`) für `api` **und** `worker` starten — Pull-Policy beachten (ein Re-Deploy ohne Pull fährt das alte Image weiter) |
| 7 | Live-Verifikation Prod: `GET /api/health` 200; Set-Ansicht eines nicht-aktiven Sets lädt; eine `UsageStats`-Zeile des Tages trägt die aktive `EmoteSetId` (`psql` über den Tunnel); Worker-Log zeigt den Warmstart mit Set-ID; Picker am Testkanal — nicht-aktives Set zeigt dessen Belegung und Kapazität (AK 84/85) |
| 8 | Monitor wieder aktivieren; Tunnel schließen |

**Danach ins Repo** (`docs:`-Commit): Nachtrag im DECISIONS-Eintrag 1 mit Datum, Dauer des Fensters
(AK 86: unter 15 min, sonst die Dauer als Befund), Ergebnis von Sonde 6, und dem Hinweis auf die
Nachwirkung „Purge-Protokolle mit `emoteId: null` sind vom alten Parser nicht lesbar" (Spec 17).
Folge-Issue 1 (Altform entfernen, E3) wird mit Datum „frühestens +14 Tage" angelegt und ins Epic
#200 eingetragen.

**Rollback im Fenster:** neuen `worker` **und** `api` stoppen, `dotnet ef database update
20260907080507_AddUsageStatSharedChatUseCount --connection '…'`, alte Images starten — nur bis
zum ersten beobachteten Set-Wechsel nach Schritt 6 (5).

---

## 3. Nicht Teil dieses Plans

Aus Spec 20 übernommen, verbindlich: keine EventAPI-Subscriptions für nicht-aktive Sets; keine
`EmoteSet`-Entität und keine Zugehörigkeits-Tabelle; keine Sicht „alle Sets zusammen"; keine
automatische Teilung eines Backfill-Laufs; keine Schutzmechanik in der Set-Ansicht; kein Rückweg der
Set-Zahlen in einen Score; keine Umbenennung auf 7TV (#201); kein v4-Fallback für `user.emote_sets`
(#43); kein Audit-Eintrag für den Set-Wechsel; persönliche Sets bleiben deaktiviert; kein
Owner-Anzeigename aus v4.

Dazu, aus diesem Plan:

- **#76 läuft auf einem eigenen Branch vor diesem Vorhaben** (Entscheidung des Betreibers vom
  2026-09-20). Dieser Plan baut die Plausibilitätssperre weder um noch setzt er sie voraus; er
  rebased auf sie (0.4) und hält im DECISIONS-Eintrag 1 fest, was ohne sie passiert (Spec 22).
- **Der Zwischenweg vor dem 01.10. (V1) ist eine Betreiber-Handlung**, kein Bauauftrag; er liefert
  nur die Werte für T1.9.
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
K0 (Betreiber, jederzeit; V1 am 01.10.)
  T0.1 T0.5 ──▶ T2.1        T0.6 ──▶ T2.4        T0.2 ──▶ T2.2 (nur TTL-Konstante)
  T0.3 ──▶ T5.1/T5.3 (8.9 bis dahin)             T0.4 ──▶ K7 (Tor)      V1 ──▶ T1.9

Worktree A — K1                                  Worktree B — K2
  T1.1 ─▶ T1.2 ─┐                                  T2.1 ─┐
  T1.3a ─▶ T1.3b ┼▶ T1.4 ─▶ T1.8 (Commit)          T2.2 ─┴▶ T2.3 ─▶ T2.4 ─▶ T2.5a ─▶ T2.5b ─▶ T2.6 ─▶ T2.7
                 │            ├─▶ T1.5 ─┐                              │
                 │            └─▶ T1.6 ─┴▶ T1.7                        └─▶ T3.1 (K3)
                 │                          │
   (01.10.) V1 ─────────────────────────────┴▶ T1.9 ─▶ K1-PR

K4 (nach K1-PR ohne T1.9 reicht: T1.6; und K2-PR)
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
- **T1.9 wartet auf den 01.10., sonst wartet nichts darauf.** Die Migration ist ab T1.3b mit leerer
  Liste vollständig getestet; der HandOfBlood-Eintrag ist eine Konstante. Deshalb blockiert K1 die
  K4-Arbeit nicht: K4 braucht T1.6, nicht T1.9.
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
- **T5.2 darf K4 überholen.** Es ist reines Backend hinter T2.3 und lässt sich früh reviewen; T5.1
  braucht es dann fertig.
- **Der Deploy ist genau einer, hinter dem 08.10.** Ein gestaffelter Deploy scheidet aus (F1: das
  Conflict-Target ist ein Index; der alte Worker verwirft nach fünf Versuchen). Deshalb der
  Integrationsbranch statt Kind-Issue-Merges auf `main` (0.4).

---

## 5. Rückweg

Global (Spec 17): **bis zum ersten beobachteten Set-Wechsel nach dem Deploy** ist die Migration
verlustfrei umkehrbar (`Down`, alte Images). Danach trägt ein `(EmoteId, Date)` Zeilen unter zwei
Set-IDs, `Down` scheitert am Unique-Index, und der Handgriff davor (je `(EmoteId, Date)` summieren,
Duplikate löschen) verliert genau die Zuordnung, die das Vorhaben eingeführt hat. Je riskantem Task:

| Task | Revert vor dem Deploy | Wo ein Revert nach dem Deploy nicht reicht |
|---|---|---|
| **T1.3b/T1.4** (Migration + Flush) | ein Commit, `git revert` genügt; die lokale Dev-DB per `dotnet ef database update <vorherige>` zurück | Prod: nur bis zum ersten Set-Wechsel; danach der Summier-Handgriff. Die Beobachtungs-Tabelle und die Voting-Spalten stören ein altes Image **nicht** — das Argument gilt nur für alte Images gegen neues Schema |
| **T1.2** (Chat-Pfad) | Revert des K1-Commits; **Nachwirkung keine** (Zählung lief in der Zwischenzeit lokal) | Fehlbuchungen unter falscher Set-ID sind Daten, kein Schema; sie bleiben, sind aber je Zeile sichtbar (`EmoteSetId`) und über `GetRowsAsync` summenneutral (E15) |
| **T1.5** (Beobachtungs-Log) | eigener Commit, revertierbar; Tabelle bleibt leer stehen | Intervalle sind additive Daten; ein Revert lässt sie stehen, nichts liest sie mehr |
| **T1.6** (`/series` nach 7TV-Id) | revertierbar, **zusammen mit T4.3** — ein Frontend, das `sevenTvEmoteId` liest, gegen ein Backend, das `emoteId` liefert, rendert leere Serien | — (Wire-Format, kein Datenbestand) |
| **T2.4** (Papierspur, set-zentrierter Endpunkt) | revertierbar | Audit-Zeilen mit `targetEmoteSetId`, `ChannelName = null` bleiben; `ProjectDetail` fällt auf den `EmoteCount`-Zweig — Anzeigequalität, kein Verlust |
| **T4.3/T4.4** (Schlüsselwechsel) | **ein** Commit mit T1.6-Abhängigkeit (s. o.); Revert setzt das Raster auf Guids zurück — dann dürfen keine Guid-losen Zeilen mehr ankommen, also auch T2.2/T4.2 zurück oder die Live-Liste abschalten | — |
| **T5.1** (Protokoll `emoteId: null`) | revertierbar | **Protokolle, die mit `emoteId: null` geschrieben wurden, liest der alte Parser nicht** — ein Restore aus ihnen wäre nach einem Revert unmöglich. Das ist die eine Nachwirkung, die ein Rückweg nicht beseitigt; sie steht im Runbook (K7) und ist der Grund für die Position von K5 |
| **T5.2** (neue Body-Form) | revertierbar; die Altform bleibt ohnehin | ein alter Tab postet `{ emoteIds }` — gilt weiter (E3); ein **neuer** Tab gegen ein revertiertes Backend postet die neue Form und bekommt 400 nach der Mutation — deshalb Frontend und Backend gemeinsam zurück |
| **T6.1–T6.3** (Voting) | revertierbar; Spalten nullbar, alte Images ignorieren sie | Set-Sessions mit `EmoteSetId` und Wahlzettel-Zeilen mit `ArchivedAt = null` bleiben; ein altes Image zeigt sie als Null-Sessions mit archivierten Zeilen — lesbar, nicht löschbar aus der Session heraus |

Auslöser für einen Rückweg vor dem Deploy: ein Live-Befund aus T1.7, T2.7 oder T5.3 fehlt; Sonde 6
Zweig B ohne Klärung. Auslöser im Fenster: Prüfung 1–4 bricht ab (dann ist nichts geschehen — die
Transaktion ist zurückgerollt), oder AK 84 scheitert nach Schritt 6.

---

## 6. „Fertig" heißt

Je Task das Gate am Task. Je Kind-Issue-PR: BE, FE, E2E (freier `:5151`) grün, die genannten
Live-Befunde im PR-Text, die gezählten Bestandstests genannt, Codex-Review über den Kind-Branch.
Für T7 zusätzlich `node scripts/coverage-local.mjs` nach dem letzten Commit und die Zweitmeinung
über das Ganze. Für K7: AK 84–86 und der `docs:`-Nachtrag.

Vergleichspunkte auf `93af613`: 45 `SevenTvSyncServiceTests`, 54 `UsageStatQueryServiceTests`,
14 `UsageStatFlushServiceTests`, 74 Harness-/Replay-Tests (müssen 0 rot bleiben), 42
`usage-stats-page.spec.ts`, 30 `usage-atlas`-E2E-Fälle. Die Spec erwartet **rund +215** neue Fälle
und **≥ 60** umgestellte (sicher 77 aus Signatur- und Datenquellenwechseln: 7 + 14 + 10 + 1 + 5 + 8
+ 28 + 4) — die Zählungen aus T1.2, T1.6, T2.5b, T4.3, T5.1, T5.2 und T6.3 machen daraus eine Zahl.

Der Merge gehört dem Nutzer; der Deploy ist ein getrenntes Wartungsfenster.
