# Plan #346 — Chat-Log-Backfill: Betriebsplan

Erstellt am 2026-10-09 auf `docs/chat-log-backfill`. **Status: Entwurf, noch nicht freigegeben.**
Quellen: [die Spec](../superpowers/specs/2026-10-09-chat-log-backfill-spec.md) (B1–B13, D1–D49, AC n, „Child n"),
[der Untersuchungsbericht](../Untersuchung-Chat-Log-Archiv-Range-2026-10-08.md) (Sonde 1/2), Epic #346, Kind-Issues #347–#354,
Doku-PR #355. Formatvorlage: [Plan-200](Plan-200-Emote-Sets.md) (nur Aufbau, nicht Umfang).

**Die Spec ist die einzige Vertragsquelle.** Dieser Plan trägt nur, was sie nicht sagt: Reihenfolge, Branches, Modellwahl,
Gates, Prod-Ablauf und die Handgriffe des Nutzers. Kein Schema, keine API-Form, kein Code. Bei Abweichung gilt die Spec, und der
Fund wird gemeldet. **Die Links in Spec und Epic zeigen bis zum Merge von #355 auf den Branch** — Empfehlung: #355 vor dem
ersten Kind mergen, damit Kind-PRs gegen `main` auf eine Spec verweisen, die dort liegt.

## 1. Gesetzte Entscheidungen

- **Jedes Kind ist ein eigener Feature-Branch und ein eigener PR direkt nach `main`**, kein Integrationsbranch (anders als
  Plan-200). Alles ab #348 liegt hinter `ChatLogBackfill:Enabled` (Default `false`); #347 ist ein eigenständiger Fix ohne
  Flag und geht zuerst (Spec Child 1, D23, D42).
- Sessions committen, pushen und öffnen PRs selbst (Regel 1); **mergen tut der Nutzer**. Ein Push ist kein Deploy.
- **Codex Sol einmal je Kind-PR vor dem Merge:** `/codex:review --model gpt-6.1-sol --scope branch --base origin/main`, aus dem
  Worktree des Kinds gestartet (Session-CWD-Falle), ohne Rückfrage Vorder-/Hintergrund, Befund unverändert vorgelegt. Widerspricht
  er dem Opus-Review (P1/P2 nur bei einem, gegensätzliche Bewertung, unvereinbare Fixes), entscheidet Fable als Schiedsrichter
  nur über die strittigen Findings. Gegenprobe nach dem Lauf: nennt der Befund die erwarteten Dateien? Sonst lief er ins Leere.
  Im Bericht das tatsächlich genutzte Modell nennen.
- Jeder Umsetzungs-Subagent bekommt die Spec-Abschnitte seines Kinds plus diesen Plan, nicht das ganze Epic; Review-Subagent
  immer `opus`.

## 2. Reihenfolge, Parallelität, Modelle

| Kind | Branch | Hängt ab von (Spec) | Umsetzung | Review | Codex Sol |
|---|---|---|---|---|---|
| #347 Placeholder-Marker | `fix/emote-placeholder-marker` | — (erster PR) | **opus** — Eingriff in `ReconcileAsync`/`IsCredibleRestLeave`; ein zu breiter Skip unterdrückt echte Leave-Beobachtungen still, und die Migration ist der Rollback-Boden (D42), wird also nie zurückgenommen | opus | vor Merge |
| #348 Schema + Range-Client | `feat/backfill-schema-archive-range` | — (parallel zu #347) | sonnet — Verträge sind in §1/§2/§8 bis auf die Spalte festgelegt | opus | vor Merge |
| #349 Service | `feat/backfill-service` | #347, #348 | **opus** — Enqueue unter `FOR UPDATE`/`FOR SHARE` per Id, Race-ACs 25a–e, Placeholder-Erzeugung (D27, „riskiest write"), Transaktion in `ReplaceBlockAsync` | opus | vor Merge |
| #350 Worker | `feat/backfill-worker` | #348, #349 | **opus** — Advisory-Lock, Status-Monitor mit run-scoped CTS, persistierte Zähler über Restart, Pause außerhalb `RunAsync` (D38/D39/D44/D46) | opus | vor Merge |
| #351 API | `feat/backfill-api` | #349 (parallel zu #350) | sonnet — dünne Handler, Mapping-Tabelle §5, Filter-Matrix | opus | vor Merge |
| #352 Settings-Tab + Picker | `feat/backfill-settings-tab` | #351 (Vertrag); #350 nur für den Live-Klick | sonnet | opus | vor Merge |
| #353 Fortschritt/Abbruch | `feat/backfill-run-status` | #352 | sonnet | opus | vor Merge |
| #354 Caption, Doku, Compose, Abnahme | `feat/backfill-caption-and-ops` | #347–#353 | sonnet (Umsetzung); der Abnahmelauf ist Beobachtung, kein Modellproblem | opus | vor Merge |

Parallel laufen #347 ∥ #348 und #350 ∥ #351; #352 kann starten, sobald #351 gemergt ist, auch während #350 noch live
verifiziert wird (E2E ist vollständig gemockt). Je Kind ein eigener Worktree; Branch-Namen wie in der Tabelle, die Verknüpfung
zum Issue entsteht über `Closes #<n>` im PR-Text. Worktree-Fallen: `docker compose` nie aus dem Worktree **starten** (nur bauen),
und das lokale Session-Cookie gilt nur für den Checkout-Pfad, in dem es erzeugt wurde — Live-Prüfungen aus dem Haupt-Checkout
fahren oder dort neu einloggen.

**Migrations-Erzeugung ist nicht parallel, auch wenn die Kinder es sind:** #348 darf `AddChatLogBackfill` erst scaffolden
(oder vor dem PR nach dem Rebase neu erzeugen), wenn #347 auf `main` liegt — die Migrations-ID muss hinter dem Marker sortieren,
sonst zeigt `AppDbContextModelSnapshot` den falschen Stand und Prod bekäme die Kette in anderer Reihenfolge als jede Testdatenbank.
Gate vor dem PR von #348: nach dem Rebase `dotnet ef migrations list` in der erwarteten Reihenfolge (Marker vor Backfill), die
Snapshot-Datei ohne Diff zu einem frischen `migrations add`, und eine lokale Rollback-Probe `database update
AddEmotePlaceholderMarker`, die den Marker samt seiner Werte erhält (D42). Vorbild: DECISIONS 2026-09-20 (#200, `migrations list`
als Gate einer Migration, die ein altes Image nicht toleriert) und 2026-09-01 („Prod-Reihenfolge", Default auf der Spalte);
einen eigenen Eintrag zur Scaffold-Reihenfolge paralleler Branches gibt es noch nicht — #348 schreibt ihn (Regel 3).

## 3. Gates je Kind

Grundsatz für alle: `dotnet format EmotePurge.slnx` bzw. `npm --prefix web run format` + `lint`; **erst committen, dann**
`node scripts/coverage-local.mjs` (es misst nur Committetes; `--backend-only`/`--frontend-only` nach Berührung); E2E nur ohne
Api auf `:5151` und nie parallel zu Testcontainers-Läufen (Laufzeit > ~2 min = Speicherdruck, Suite allein wiederholen).

| Kind | Backend `dotnet test` (Docker) | `npm test` | `npm run e2e` | Live (Regel 16) |
|---|---|---|---|---|
| #347 | ja | — | — | lokaler Stack: Migration gegen die Dev-DB (Zahl der markierten Zeilen notieren), ein REST-Resync gegen echtes 7TV; Fälle laut Child 1 AC 1–3 sind deterministisch im Test |
| #348 | ja | — | — | Harness `docker compose --profile harness run … zokka --days 3 --diagnostic` aus dem Haupt-Checkout: **beobachtend** (AC 18 — Byte-Identität nur über die gepinnte Tages-Fixture, nicht live); vorher der 7TV-Handgriff aus Abschnitt 5 |
| #349 | ja | — | — | DB-Rauchprobe gegen die lokale Postgres (nicht nur Testcontainers): Enqueue auf einem echten Kanal mit echtem 7TV-Read, dann `leave` desselben Kanals über die laufende Api, damit das neue `FOR UPDATE`-Locking von `LeaveAsync` einmal gegen den echten Pfad läuft; Run-Zeile danach `cancelled`/`channel_left`. Geprüften SHA im PR vermerken. Die Archiv-Läufe liegen per Spec bei #350 |
| #350 | ja (Worker.Tests, Infrastructure) | — | — | **LAN-Profil**, Flag lokal an (Api **und** Worker), echtes Archiv, Kanäle laut D24: `zokka` 1 Monat, dann `handofblood` 6 Monate; Restart (AC 10), Abbruch (AC 8), Ausschluss-Id (AC 23), Speicher (AC 24) — alles **beobachtend** mit Toleranz; Pass/Fail nur über gepinnte Blöcke (AC 2). Bis #351 da ist: Run-Zeile + Snapshot seeden (Child 4 AC 4) |
| #351 | ja (Api.Tests) | ja (`api-error-locales`, `audit-actions`) | ja (Mock-Default in `mockChannelPermissions` ändert sich) | authentifizierte Aufrufe (Session-Cookie) gegen die lokale Api mit echter Postgres/Redis, **Flag aus und an**: die drei Backfill-Routen (404 `backfill_disabled` ↔ 200/202/204), `/permissions` mit dem Flag-Wert, und der flag-unabhängige `import-coverage`-Endpunkt in beiden Zuständen 200. Geprüften SHA im PR vermerken |
| #352 | — (kein Backend) | ja | ja | nach Merge von #350 einmal echter Start im lokalen Stack mit Flag an |
| #353 | — | ja | ja | Abbruch einmal live gegen einen laufenden Run |
| #354 | ja | ja | ja | **Abnahmelauf** auf dem LAN-Stack: AC 2, 3, 4, 5, 10, 11, 18 mit Messwerten im PR; Rollback-Cleanup einmal gegen die LAN-DB (Child 8 AC 3 / AC 32); Compose-Änderung → `--build` (Regel 15) |

Für die LAN-Läufe das Flag nur per Umgebungsvariable setzen (`ChatLogBackfill__Enabled=true` für beide Prozesse), nicht in
einer committeten `appsettings`. Der Harness-Lauf startet die geteilten Container mit — deshalb Haupt-Checkout, nie Worktree.

## 4. Prod-Ablauf

**Zwei Migrationen, zwei Kinder:** #347 bringt `AddEmotePlaceholderMarker`, #348 bringt `AddChatLogBackfill` (Spec §1). Beide
sind additiv: das noch laufende alte Image ignoriert sie. Migrationen laufen in Prod **von Hand und vor dem Stack-Update**
(CLAUDE.md „Prod-Migration": Tunnel, dann `dotnet ef migrations list` → `update` → `list` mit `--connection` und
Passwort-Platzhalter). Die Session bereitet die Befehle vor und verbindet sich nie selbst.

**Erst veröffentlichte Images, dann Redeploy:** `publish.yml` baut erst, nachdem `test` und `test-web` grün sind, und Api
und Worker **getrennt** nach berührten Pfaden (`web/*`, `src/EmotePurge.Api/*` → api; `src/EmotePurge.Worker/*` → worker; alles
andere unter `src/` → beide), mit den Tags `:latest` und `:<merge-sha>`. Vor jedem Redeploy: den Publish-Lauf des Merge-Commits
abwarten und prüfen, dass **beide** betroffenen Images unter `:<merge-sha>` liegen; nach dem Redeploy die laufende Revision
gegen diesen Digest prüfen (Portainer zeigt ihn je Container; für Web-Änderungen genügt ein neuer i18n-Schlüssel in
`/i18n/de.json`). Das Image-Paar von #347 (`:<merge-sha>` von api und worker) wird als **Rollback-Boden** notiert.

**Merge-Rhythmus:** Jeder Merge nach `main` baut `:latest`. Nach dem Merge eines Migrations-Kinds darf deshalb kein Re-Pull auf
dem VPS passieren, bevor die Migration drin ist — der `PendingMigrationGuard` (S3-34) lässt das neue Image sonst abbrechen.
Praktisch: Merge von #347 bzw. #348 jeweils in **einem** Zug mit Publish-Wartezeit, Migration und Redeploy (Portainer: „Re-pull
image and redeploy" — ohne Häkchen fährt die alte lokale Kopie weiter). Alle anderen Kinder haben keine Migration und können in
beliebigem Rhythmus deployt werden; bis #354 bleibt das Feature ohne Flag unsichtbar.

**Alt-Schreiber-Fenster bei #347:** Zwischen Migration und Redeploy läuft die alte Api weiter und legt über Ballots weiterhin
Platzhalter-Zeilen **ohne** Marker an (Spaltendefault `false`). Kein Api-Stopp, sondern ein verifizierter Nachlauf: sobald der
Redeploy durch und die Revision geprüft ist (kein alter Schreiber mehr), das Backfill-Prädikat von Child 1 (Spec §1, `IsArchived
AND ArchivedAt IS NULL AND LastEnteredSetAtUtc IS NOT NULL`) einmal per Tunnel erneut als `UPDATE` anwenden, mit Zählung der
Treffer vorher/nachher als Gegenprobe. Das Prädikat ist nach Konstruktion idempotent — es setzt nur `true`, trifft genau die
Ballot-Kombination, und eine im Fenster angelegte Zeile trägt sie, solange der neue Sync sie nicht schon un-archiviert hat (dann
ist `IsArchived` falsch und sie gehört nicht markiert). **Offener Punkt für #347:** DECISIONS-Eintrag und `docs/Operations.md`
nennen das Prädikat als wiederholbaren Betreiber-Schritt und sagen die Idempotenz ausdrücklich; die Session bereitet das SQL mit
Platzhaltern vor.

**Rollback-Untergrenze (D42):** #347s Migration wird nie zurückgerollt; jedes spätere Rollback endet beim Image-Paar von #347.
**Image-only-Rollback ab #354:** Sobald importierte Daten existieren, ist das D40-Aufräumen (Flag aus auf api **und** worker,
beide neu erstellen, Runs abbrechen/prüfen, importierte Zeilen und Coverage löschen, optional Placeholder-Cleanup D41 — alles
in der Reihenfolge des Spec-„Rollback Plan" bzw. `docs/Operations.md`) Voraussetzung für **jedes** Zurückgehen auf ein Image ohne
Caption-Unterstützung, auch wenn das Schema stehen bleibt: das ältere Image zeigt importierte Zahlen sonst ohne Herkunftsangabe.
`AddChatLogBackfill` selbst lässt sich nur nach demselben Aufräumen zurückrollen (`Down` verweigert, D40).

**Konfiguration (Portainer-Stack-Umgebungsvariablen, nicht `stack.env` von Hand):**

| Wann | Variable | Wert |
|---|---|---|
| mit dem Deploy von #354 (Compose-Wiring kommt dort) | `CHAT_LOG_BACKFILL_ENABLED` | `false` — explizit, damit der Schalter sichtbar existiert |
| mit #354 | `CHAT_LOG_ARCHIVE_BASE_URL` | nur setzen, wenn vom Code-Default abgewichen wird; gilt für api, worker **und** harness (D45) — ein Wechsel relabelt alte Importe nicht, aber laufende Runs scheitern mit `archive_mismatch` |
| mit #354, optional | `CHAT_LOG_BACKFILL_REQUEST_DELAY_SECONDS` | nicht unter den Default 10 |
| nach Datenschutztext **und** Abnahme **und** Nachweis, dass die laufende Api #354 enthält (Revision = `:<merge-sha>` von #354 oder später; `usageStats.trackedSinceWithImport` in `/i18n/de.json`) | `CHAT_LOG_BACKFILL_ENABLED` | `true`; api **und** worker neu erstellen (Operations „Chat-log backfill") |

Vor #354 gibt es keine Compose-Verdrahtung; bis dahin gilt der Code-Default (`BaseUrl` → cyex ab #348, Flag `false`).

## 5. Handgriffe des Nutzers (Checkliste mit Zeitpunkt)

1. **Vor dem ersten Kind:** #355 mergen (Spec-Links zeigen danach auf `main`).
2. **Während #348, bevor die Fixture aufgezeichnet wird (spätestens vor dem Merge):** 7TV-Vorschau-Abfrage mit `added_at`
   einmal per `curl` von Hand prüfen — automatisierte Sonden gegen 7tv.io blockt der Klassifikator, die Session kann es nicht.
   Skizze: `curl -s -X POST https://7tv.io/v4/gql -H 'Content-Type: application/json' [-H 'Authorization: Bearer <TOKEN>' falls nötig] -d '{"query":"<Vorschau-Query aus SevenTvApiClient mit added_at auf items>","variables":{"id":"<SET-ID>","page":1,"perPage":5}}'`.
   Erwartung: jedes Item trägt `added_at` als Zeitstempel oder `null`; ein GraphQL-Fehler auf dem Feld heißt: §2.1 der Spec
   steht in Frage, Kind stoppen und melden. Die redigierte Antwort wird die Fixture. (`emoteSet(id)` zieht nicht den
   Such-Eimer, `emotes.search` schon.)
3. **Nach Merge #347:** Publish beider Images abwarten → Tunnel → `migrations list` → `database update` → `list` → Re-Pull +
   Redeploy → Revision prüfen → **Nachlauf** (Prädikat erneut anwenden, Zählung vorher/nachher, Abschnitt 4) → Image-Paar als
   Rollback-Boden notieren.
4. **Nach Merge #348:** Publish abwarten → Migration `AddChatLogBackfill` wie oben → Re-Pull + Redeploy → Revision prüfen.
5. **Vor dem ersten `handofblood`-6-Monats-Lauf (#350), optional:** Archiv-Betreiber nach Rate-Limits fragen — die Zusage vom
   08.10. nennt keinen Wert; der Startwert bleibt der Default-Abstand (10 s), nie darunter.
6. **Nach Merge #354:** Publish abwarten, Deploy, Revision prüfen, Umgebungsvariablen laut Abschnitt 4 setzen (Flag bleibt `false`).
7. **Vor dem Einschalten auf Prod:** Datenschutzerklärung ergänzen — operator-eigenes Markdown im read-only gemounteten
   `Legal:ContentPath`-Verzeichnis (`privacy.de.md`/`privacy.en.md`), Archiv als Datenquelle; kein Repo-Commit nötig.
8. **Einschalten:** erst Nachweis, dass die laufende Api #354 enthält (Abschnitt 4), dann `CHAT_LOG_BACKFILL_ENABLED=true`, api
   und worker neu erstellen; erster Prod-Run auf dem eigenen Testkanal mit 1 Monat als Rauchprobe, Caption und Link prüfen.

## 6. Wann das Epic fertig ist

Alle acht Kinder gemergt; jede AC der Spec bestanden (deterministische über gepinnte Fixtures, beobachtende mit Messwerten im
PR von #354); Abnahmelauf #354 dokumentiert; `docs/Feature-Ideen-2026-08-01.md` Zeile **A17** auf ✅ im Commit von #354 (D22);
DECISIONS-Einträge: #347 (Marker, D37) und #354 (der Sammeleintrag aus Spec §9), je im Commit, der den Vertrag ändert
(Regel 3); CLAUDE.md: elf Hosted Services (#350) und Umsetzungsstand-Zeile (#354); `docs/Operations.md` Abschnitt
„Chat-log backfill" mit Cleanup und Rollback-Untergrenze. **Das Flag geht auf Prod erst nach Datenschutztext und Abnahme an** —
nicht mit dem Merge, nicht mit dem Deploy.

## 7. Risiken und Fallen (betrieblich)

- `docker compose up -d <service>` ohne `--build` nach Code-Änderung fährt das alte Image (Regel 15); auf dem VPS dasselbe als
  Re-Pull-Häkchen. Bei „verhält sich wie vorher" zuerst den ausgelieferten Stand prüfen, nicht den Code.
- E2E rot quer über fremde Dateien: entweder lauscht eine Api auf `:5151` (nach einem LAN-Test `dotnet run` beenden) oder
  Speicherdruck (Laufzeit als Kennzahl). Suite allein wiederholen, bevor jemand debuggt.
- `coverage-local.mjs` ist eine dateigenaue Näherung, pessimistischer als Sonar, misst nur Committetes und zählt Migrationen
  mit — eine niedrige Zahl ist Anlass hinzusehen, kein Grund für Quoten-Tests gegen Transportklassen.
- Die Live-Läufe von #350 und #354 belasten den Archiv-Betreiber (705 MB je 6-Monats-Lauf): Abstand nie unter Default, Läufe
  nicht parallel, einen 429 als Signal nehmen und den Lauf nicht wiederholen, bis die Cooldown-Zeit verstrichen ist.
- Merge eines Migrations-Kinds ohne anschließende Migration lässt den nächsten Re-Pull am `PendingMigrationGuard` scheitern —
  Merge, Publish-Wartezeit, Migration und Redeploy gehören in einen Zug (Abschnitt 4). Ein Redeploy vor dem Ende des
  Publish-Laufs zieht das vorige `:latest` und sieht wie Erfolg aus.
- #348 mit einer Migrations-ID vor dem Marker gescaffoldet: Snapshot und Kette stimmen lokal, in Prod liefe `update` in der
  falschen Reihenfolge — deshalb das Reihenfolge-Gate aus Abschnitt 2, nicht erst `migrations list` am Tunnel.
- Codex-Review aus der falschen CWD oder ohne `--scope branch` liefert eine Entwarnung mit Exit 0 auf leerem Diff; nach einem
  Codex-CLI-Update alte Broker mit gelöschter Binary beenden, bevor man am Modell zweifelt.
- Sync-Eingriffe (#347, #349) sind lokal nur mit laufendem Worker prüfbar; ein Dev-Container ohne DNS zeigt sich als
  `/api/health` 503 und „Emote-Set wird geladen", nicht als Code-Fehler — `docker compose restart api worker`.
- Worktree-Cookie-Bindung: Vorher/Nachher-Messungen mit einem Cookie laufen aus demselben Checkout-Pfad; während ein Codex-Lauf
  im Worktree läuft, dort nicht den HEAD umstellen.
