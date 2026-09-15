# Shared Chat, Zug 2: die Anzeige — Implementierungsplan

**Datum:** 2026-09-08 · **Issue:** [#73](https://github.com/emotepurge/EmotePurge/issues/73) ·
**Vorgänger:** Zug 1 (PR #96, gemergt und auf Prod) ·
**Spec:** [`docs/superpowers/specs/2026-09-06-shared-chat-zaehlung-73-design.md`](../specs/2026-09-06-shared-chat-zaehlung-73-design.md) ·
**Plan Zug 1:** [`docs/superpowers/plans/2026-09-06-shared-chat-zaehlung-73.md`](2026-09-06-shared-chat-zaehlung-73.md) ·
**Status:** geschrieben, nicht ausgeführt · **Ausführung:** unbeaufsichtigter Nachtlauf

## Auftrag

Zug 1 schreibt seit dem 2026-09-07 drei getrennte Spalten (`UseCount`, `BotUseCount`,
`SharedChatUseCount`), lässt den Lesepfad der Oberfläche aber übergangsweise
`UseCount + SharedChatUseCount` summieren und filtern (Entscheidung D5), damit beim Deploy keine
angezeigte Zahl unerklärt fällt. Zug 2 beendet diese Brücke: Bänder, Zahlen und Sortierung zeigen
ab jetzt ausschließlich eigene Nutzung, und darunter erklärt **ein Satz**, seit wann Nutzung aus
geteiltem Chat nicht mehr mitzählt. Beides gehört in dasselbe Auslieferungspaket — der Rückbau
ohne die Erklärung wäre genau der unerklärte Sturz, den D5 verhindert.

Zug 2 ist laut Spec D1 **taktneutral**: Er ändert die Zählung nicht, berührt den Harness-Rechenkern
nicht und setzt die 30-Tage-Uhr aus [#69](https://github.com/emotepurge/EmotePurge/issues/69) nicht
zurück. Die Freeze-Liste aus Zug 1 gilt unverändert weiter.

## Entscheidungen dieses Plans

Wo mehrere Wege offenstanden, ist hier gewählt — der Nachtlauf entscheidet nichts davon neu.

1. **Kein Toolbar-Toggle, nur ein Hinweissatz.** Vom Nutzer vorentschieden (E3-Muster aus #31, plus
   die Projekt-Leitlinie zur Frontend-Zurückhaltung): Der Erstbesuch soll nicht zwei Zahlenbegriffe
   mitlesen müssen; erklärt werden muss nur, warum eine Zahl kleiner wurde. Der im Issue #73
   skizzierte Toggle und die Darstellung `12× (+3 Shared Chat)` sind damit **verworfen**, nicht
   vertagt-mit-Auftrag; ein Folge-Issue kann sie später aufnehmen.
2. **Das Datum ist datenabgeleitet, nicht das Deploy-Datum.** Vorbild ist `BotsExcludedSince`:
   `SharedChatSeparatedSince` ist der früheste Tag, an dem dieser Kanal eine `UsageStat`-Zeile mit
   `SharedChatUseCount > 0` trägt, `null`, wenn es keine gibt. Ein Deploy-Datum wäre eine im Code
   festgeschriebene Konstante, die niemand pflegt und die pro Umgebung falsch sein kann; die
   E4-Unschärfe („erste Sichtung, nicht Beginn der Trennung") wird wie bei den Bots hingenommen und
   im DECISIONS-Eintrag benannt.
3. **Ein Kanal ohne je geteilten Chat sieht keine Caption** — `null` heißt „hier gab es nichts zu
   trennen", und für diesen Kanal ändert der Rückbau auch keine einzige Zahl (alle
   `SharedChatUseCount` sind 0). Der Satz erschiene ohne Anlass.
4. **Die Caption wird nicht unterdrückt, wenn das Datum vor dem Kanalbeginn liegt.** Dieselbe
   Begründung wie im Docblock von `web/src/app/core/emotes/bots-excluded-caption.ts`: Die
   Sichtbarkeitsregel prüft ausschließlich „Datum vorhanden". Ein Abgleich gegen `trackedSince`
   wäre eine zweite, stillschweigende Regel, die genau dann greift, wenn sie am wenigsten geprüft
   ist. (Praktisch ist der Fall hier fast unmöglich — Shared-Chat-Zeilen existieren erst seit dem
   Zug-1-Deploy —, die Regel steht trotzdem gleich.)
5. **Träger der Information ist `EmoteSetStatusDto`**, ausgeliefert über den bestehenden Endpunkt
   `GET /api/channels/{channelName}/emotes/active-set` (`EmoteEndpoints.cs:189-196`). Kein neuer
   Endpunkt, kein neuer `IEndpointFilter`, keine geänderte Filter-Reihenfolge — damit ist
   `tests/EmotePurge.Api.Tests` nach Regel 11 **nicht** betroffen. Die Alternative (eigener
   Endpunkt für die Caption) hieße eine zweite Anfrage pro Seitenaufruf für ein Datum, das neben
   dem Bot-Datum in derselben Antwort stehen kann.
6. **Eine zusätzliche Query-Methode ist nötig:** `GetEarliestSharedChatUsageDateAsync` auf
   `IUsageStatQueryService`, Zwilling von `GetEarliestBotUsageDateAsync`. Die bestehende Methode
   umzubauen (etwa auf einen Parameter „welche Spalte") wäre eine Verallgemeinerung für zwei
   Aufrufer und machte die eingefrorene Bot-Methode (Freeze-Liste Zug 1) anfassbar. Sie bleibt
   unberührt.
7. **Keine Defaultwerte für den neuen Record-Parameter** in `EmoteSetStatusDto` — dieselbe Regel wie
   in Zug 1 (B2): Ohne Default bricht jede Konstruktionsstelle kompilierend auf und der Compiler ist
   die Suchhilfe. Der neue Parameter steht **am Ende** der positionellen Liste.
8. **Der Covering-Index bleibt unangetastet.** Zug 1 hat ihn nach der `EXPLAIN`-Messung bewusst bei
   `INCLUDE (UseCount)` belassen (DECISIONS 2026-09-06, Task 4); mit dem Rückbau auf `UseCount`
   allein greift der Index-Only-Scan von selbst wieder. Es gibt also **keine** Migration in Zug 2
   und nichts „nachzuziehen" — der als offen vererbte Punkt ist durch Zug 1 bereits beantwortet.
9. **Der Marker-Test wird umgeschrieben, nicht gelöscht.**
   `UsageStatQueryServiceTests.SharedOnlyRow_ReadsAsUsedUntilZug2` heißt danach
   `SharedOnlyRow_ReadsAsUnused_LikeBotOnly` und behauptet das umgekehrte Verhalten mit demselben
   Seed. Er ist der eingebaute Beleg, dass die Brücke gefallen ist; ein gelöschter Test belegt
   nichts.
10. **Die Nutzungsspalte auf der Voting-Ergebnisseite bekommt keine eigene Caption.** Sie speist
    sich aus `GetTotalsByEmoteIdsAsync` und fällt mit zurück. Präzedenz: Der Bot-Split hat dieselbe
    Spalte 2026-09-01 ebenso still verändert, und die Spalte ist laut `docs/Architectur.md:175`
    ausdrücklich Kontext, nicht Score. Die Asymmetrie steht im DECISIONS-Eintrag, damit sie eine
    Entscheidung bleibt und kein Versehen wird.
11. **Kein Vitest-Spec für `usage-stats-page.ts`.** Die Entscheidungslogik der Caption liegt
    vollständig in der puren Funktion, die ihren co-located Spec bekommt; die Seite verdrahtet nur.
    Regel 12 in der Fassung seit 2026-09-06 verlangt einen Komponenten-Spec für *eigene,
    nicht-triviale* Logik — die entsteht hier nicht, und die Seite hat bis heute keinen Spec-Aufbau
    (SSE-Stub, Transloco-Boot, Resources), den ein unbeaufsichtigter Lauf nebenbei richtig aufsetzt.
    Was stattdessen prüft, steht in Task 3 (E2E) und Task 2 (Funktions-Spec).
12. **E2E: zwei Fälle, gespiegelt an den beiden Bot-Fällen** in `web/e2e/usage-atlas.e2e.spec.ts`.
    Der Audit-Harness (§12 der Designsprache) läuft **nicht** — es kommt kein Element und kein
    Bedienelement hinzu, nur ein umbrechender Satz in einem bestehenden `<p>`; es gibt keine
    Flächenwirkung, die seine Metriken messen könnten.
13. **`docs/UI-Designsprache.md` wird mitgeführt**, mit **einem** Aufzählungspunkt in §2.5. Er hält
    fest, dass die Ehrlichkeitssätze unter dem Bogen die Ausschlüsse der gezeigten Zahlen tragen —
    als Satz, nicht als Bedienelement. Damit ist die Anzeigeregel dort dokumentiert, wo ein
    künftiger Umbau des Bogens sie findet (bei #80 war eine fehlende Nachführung schon einmal ein
    Review-Befund).

## Auslieferungsform und Reihenfolge

**Ein Branch, ein Merge: `feat/shared-chat-anzeige-73-zug2`.** Kein Task dieses Plans wird für sich
nach `main` gemergt.

Die Reihenfolge ist so geschnitten, dass **kein Zwischenstand entsteht, der für sich schädlich
wäre**:

- **Task 1** ergänzt nur Backend-Daten, die niemand liest — für die Oberfläche passiert nichts.
- **Task 2** legt die Frontend-Bausteine an (Modell-Feld, Caption-Funktion, Übersetzungen,
  E2E-Mock-Hilfe), **ohne sie ins Template zu hängen** — sichtbar ändert sich weiterhin nichts.
- **Task 3** ist der Schnitt: Rückbau des Lesepfads **und** Einbau der Caption **und** Umkehr des
  Marker-Tests **und** die drei Dokumente, in **einem** Commit. Der Rückbau ist an dieser Stelle
  nicht mehr von seiner Erklärung trennbar, und Regel 3 bindet den DECISIONS-Eintrag ohnehin an
  denselben Commit wie die Vertragsänderung. Dass dieser eine Commit größer ausfällt als die Regel-2-
  Gewohnheit, ist der Preis dafür und hier ausdrücklich gewollt.
- **Task 4** fährt die Gates, **Task 5** die Live-Probe, **Task 6** schließt ab.

Task 4 (E2E) läuft **vor** Task 5, weil die E2E-Suite nur grün ist, solange auf `:5151` keine Api
lauscht — und Task 5 startet genau dort eine.

## Rahmenbedingungen des Nachtlaufs

- Commits pro Task auf dem Feature-Branch sind Teil des Auftrags; **`git push` unterbleibt**, ein
  PR wird nicht angelegt, GitHub wird nicht beschrieben.
- Es gibt keine Rückfragemöglichkeit. Jede in diesem Plan getroffene Entscheidung gilt; taucht eine
  **nicht** vorgesehene Weggabelung auf, wird der konservativere Weg genommen (der, der weniger
  Bestand anfasst) und im Abschlussbericht als offener Punkt benannt — nicht stillschweigend
  entschieden.
- Docker muss laufen (Testcontainers). Compose-Aufrufe immer mit explizitem Service-Namen.
- Die Freeze-Liste aus Zug 1 gilt: `EmoteNameMatching`, `BotChatterDetector` samt Bot-Liste,
  `BotSplitCutover`/`GetEarliestBotUsageDateAsync`, die präregistrierten Schwellen und Konstanten
  des Harness, die Tagesgrenzen, die `.jsonl`/`.report.json`-Feldnamen, `AlgorithmVersion` und die
  TwitchLib-Versionen. **Nichts davon wird angefasst** — auch nicht „nur aufgeräumt".

---

## Task 1 — Das Datum kommt aus der Datenbank bis in den Api-Vertrag

**Absicht.** Die Caption braucht eine Zahl, bevor es sie anzeigen kann. Dieser Task liefert sie
additiv: eine neue Query-Methode, ein neues Feld im bestehenden Status-DTO, sonst nichts. Für sich
allein gemergt wäre er wirkungslos und damit harmlos.

**Dateien.**

- `src/EmotePurge.Core/Services/IUsageStatQueryService.cs` — Deklaration
  `Task<DateOnly?> GetEarliestSharedChatUsageDateAsync(string channelId, CancellationToken cancellationToken = default)`
  samt Doc-Kommentar nach dem Vorbild der Bot-Methode (Zeilen 190-198).
- `src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs` — Implementierung als Zwilling
  von `GetEarliestBotUsageDateAsync` (:266-285), unmittelbar dahinter platziert: erst die Emote-IDs
  des Kanals als skalare Liste (Regel 10), dann `MIN(Date)` über `UsageStats` mit
  `SharedChatUseCount > 0`, projiziert auf `DateOnly?`, damit die leere Menge kein `MinAsync`-Wurf
  wird. Archivierte Emotes sind **eingeschlossen**, aus demselben Grund wie dort.
- `src/EmotePurge.Core/Services/IEmoteSetStatusService.cs` — `EmoteSetStatusDto` bekommt als
  achten, letzten positionellen Parameter `DateOnly? SharedChatSeparatedSince`, ohne Default (Nr. 7),
  mit einem Doc-Kommentar, der drei Dinge sagt: was es ist (früheste Sichtung, nicht Deploy-Datum),
  wann es `null` ist (nie geteilten Chat gesehen — der Kanal braucht keine Erklärung) und dass
  Zahlen vor diesem Tag fremde Nutzung ununterscheidbar enthalten.
- `src/EmotePurge.Infrastructure/Services/EmoteSetStatusService.cs` — das Feld wird unter
  **demselben Gate** wie `botsExcludedSince` gefüllt: bei leerem `ActiveEmoteSetId` bleibt es `null`
  und es wird gar nicht erst abgefragt.
- `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatQueryServiceTests.cs` — vier Fälle,
  gespiegelt an den bestehenden Bot-Fällen (:664, :685, :700, :717): frühester Shared-Chat-Tag statt
  frühester Zeile überhaupt; keine Shared-Chat-Zeile ⇒ `null`; Zeile auf einem **archivierten**
  Emote zählt mit; Zeile eines **fremden Kanals** zählt nicht.
- `tests/EmotePurge.Infrastructure.Tests/Integration/EmoteSetStatusServiceTests.cs` — drei Fälle,
  gespiegelt an :151, :172, :187: Datum wird durchgereicht; ohne Shared-Chat-Zeile `null`; vor dem
  ersten Sync wird die Query übersprungen (also `null`, ohne dass eine Query läuft).

**Grenzfälle.**

- Der Compiler bricht überall dort auf, wo `EmoteSetStatusDto` konstruiert wird. Das ist gewollt
  (Nr. 7); jede solche Stelle wird nachgezogen, keine bekommt einen Defaultwert eingebaut.
- Eine Zeile mit `UseCount = 0, BotUseCount = 0, SharedChatUseCount = n` ist der Normalfall, den
  diese Query finden muss — der Seed der Tests muss ihn enthalten, sonst prüft er nichts.
- `GetEarliestBotUsageDateAsync` bleibt **wörtlich unverändert** (Freeze-Liste).

**Abnahme.** `dotnet build EmotePurge.slnx` ohne Warnung; die sieben neuen Fälle grün;
`dotnet test EmotePurge.slnx` insgesamt grün. Kein Frontend-Diff in diesem Task.

**Commit.** `feat(usage-stats): expose the earliest shared-chat usage date on the set status`

---

## Task 2 — Frontend-Bausteine, noch ohne Wirkung

**Absicht.** Alles bereitstellen, was die Caption braucht — Modell, reine Funktion samt Spec,
Übersetzungen, E2E-Mock — **ohne** sie zu rendern. Der Stand ist für sich harmlos: Die Oberfläche
zeigt weiterhin die Übergangssumme und sagt nichts dazu.

**Dateien.**

- `web/src/app/core/emotes/emote-set-status.model.ts` — `sharedChatSeparatedSince: string | null`
  (`yyyy-MM-dd`), mit demselben Doc-Kommentar-Zuschnitt wie `botsExcludedSince`.
- `web/src/app/core/emotes/shared-chat-separated-caption.ts` (neu) — eine reine Funktion
  `sharedChatSeparatedCaptionKey(sharedChatSeparatedSince: string | null): string | null`, die den
  i18n-Schlüssel zurückgibt oder `null`. Der Docblock erklärt die Sichtbarkeitsregel und dass sie
  **nicht** gegen `trackedSince` abgleicht (Nr. 4), mit Verweis auf den DECISIONS-Eintrag aus
  Task 3 — genauso wie es der Bot-Zwilling für den 2026-09-01er Eintrag tut.
- `web/src/app/core/emotes/shared-chat-separated-caption.spec.ts` (neu) — zwei Fälle: `null` ⇒ kein
  Schlüssel; Datum ⇒ Schlüssel. Verhalten, keine Vorlage (Regel 12).
- `web/public/i18n/de.json` und `web/public/i18n/en.json` — neuer Schlüssel
  `usageStats.sharedChatSeparatedSince`, in **beiden** Dateien, unmittelbar hinter
  `usageStats.botsExcludedSince` einsortiert. Wortlaut, Satzbau bewusst parallel zum Bot-Satz
  (gleiche Struktur „… zählt seit dem {{ date }} nicht mit; Zahlen davor enthalten sie noch."):
  - **de:** „Nutzung aus dem geteilten Chat anderer Kanäle zählt seit dem {{ date }} nicht mit;
    Zahlen davor enthalten sie noch."
  - **en:** „Usage from other channels' shared chat has not been counted since {{ date }}; numbers
    before that still include it."
  Interpoliert wird ein **bereits formatiertes** Datum, nicht der ISO-Wert — wie beim Bot-Satz.
- `web/e2e/support/mocks.ts` — `mockActiveEmoteSet` bekommt im `status`-Objekt das optionale Feld
  `sharedChatSeparatedSince?: string | null` mit Default `null` und gibt es in der Antwort mit aus.
  Der bestehende Erklärkommentar zum Bot-Default wird um denselben Satz für das neue Feld ergänzt.
- `web/e2e/usage-atlas.e2e.spec.ts` — die Hilfsfunktion `openAtlas` bekommt einen **vierten**
  optionalen Parameter `sharedChatSeparatedSince: string | null = null` und reicht ihn an
  `mockActiveEmoteSet` durch. Bewusst ein vierter Parameter statt eines Umbaus der Signatur auf ein
  Optionsobjekt: Der Umbau berührte jeden bestehenden Aufrufer und brächte einem Nachtlauf Risiko
  ohne Ertrag.

**Grenzfälle.**

- Der Schlüssel ist in diesem Task **noch unbenutzt**. Das ist Absicht und keine Lint-Verletzung;
  es gibt keine Prüfung auf ungenutzte i18n-Schlüssel (`api-error-locales.spec.ts` deckt nur
  Fehlercodes ab).
- Kein Aufruf von `sharedChatSeparatedCaptionKey` im Produktivcode dieses Tasks — nur im Spec.
- Prettier: `npm --prefix web run format` läuft am Task-Ende, damit die Locale-Dateien nicht am
  Formatgate hängenbleiben.

**Abnahme.** `npm --prefix web test -- --watch=false` grün (inklusive der zwei neuen Fälle);
`npm --prefix web run lint` grün; `npm --prefix web run format:check` grün. Die E2E-Suite ist von
diesem Task unberührt und muss unverändert grün bleiben — sie wird hier **nicht** eigens gefahren,
das erledigt Task 4.

**Commit.** `feat(usage-stats): add the shared-chat separation caption and its translations`

---

## Task 3 — Der Schnitt: Rückbau, Anzeige und Dokumentation in einem Commit

**Absicht.** Der Lesepfad rechnet ab hier mit `UseCount` allein, und im selben Moment steht der
erklärende Satz auf der Seite. Das ist die Kernänderung dieses Plans und der einzige Commit, der
sichtbares Verhalten ändert.

### 3.1 — Lesepfad zurückdrehen

`src/EmotePurge.Infrastructure/Services/UsageStatQueryService.cs`, **alle** Stellen, an denen heute
`UseCount + SharedChatUseCount` steht — Summen **und** Filter:

| Methode | Was zurückgeht |
|---|---|
| `GetUsageContextAsync` (:73-83) | `TotalUseCount`, `PreviousWindowUseCount` und das Prädikat in `LastUsedDate` rechnen wieder mit `UseCount` allein |
| `GetDailySeriesAsync` (:134-151) | das Tagesfilter-Prädikat, der projizierte Tageswert und das Prädikat der `First`/`Last`-Bounds |
| `GetChannelSeriesAsync` (:225-229) | Filter-Prädikat und projizierter Wert |
| `GetTotalsByEmoteIdsAsync` (:259-263) | die Summe |
| `GetUsageStatsAsync` (:16-20) | **unverändert** — rechnete nie mit der Übergangssumme; nur der Kommentar (:14-15) verliert seinen „noch nicht"-Charakter |
| `GetRowsAsync` (:327-332) | **unverändert** — roh für den Harness, das war nie Teil der Brücke |
| `GetEarliestBotUsageDateAsync` | **unverändert** (Freeze-Liste) |
| `GetEarliestSharedChatUsageDateAsync` (Task 1) | **unverändert** |

Die zugehörigen Erklärkommentare (:14-15, :57-72, :129-133, :140-142, :213-224, :254-256) werden
**umgeschrieben, nicht gelöscht**: Sie erklären danach den Zielvertrag („eigene menschliche Nutzung",
Shared-only-Zeile liest sich wie eine Bot-only-Zeile als unbenutzt) und den wiederhergestellten
Index-Only-Scan, nicht mehr eine Übergangszeit. Insbesondere fällt die Passage über den
Heap-Zugriff (:61-66), weil sie ab hier falsch ist.

**Der Verhaltensunterschied, den das erzeugt und der so gewollt ist:** Eine Zeile mit
`UseCount = 0, SharedChatUseCount > 0` verschwindet aus jeder Ansicht — sie zählt nicht mehr in die
Summen, nicht mehr in `LastUsedDate`/`FirstUsedDate`, nicht mehr in die Tagesreihen, und ein Emote
mit ausschließlich solchen Zeilen fällt in `GetChannelSeriesAsync` ganz aus `Emotes` heraus, genau
wie ein bot-only-Emote heute schon.

### 3.2 — Verträge und Kommentare nachziehen

- `src/EmotePurge.Core/Services/IUsageStatQueryService.cs` — die Doc-Kommentare, die heute „D5",
  „transitionally" oder „Zug 2" sagen: :9-11, :19-21, :25-27, :49-51, :54-56, :61-62, :86-89,
  :113-116, :183-186. Danach beschreiben sie durchgängig den Zielvertrag: Summen und Filter sind
  `UseCount`; eine Zeile ohne eigene menschliche Nutzung (bot-only **oder** shared-only) zählt
  nicht als Nutzung.
- `src/EmotePurge.Core/Entities/UsageStat.cs` (:12-25) — der Kommentar sagt danach, dass **beide**
  Nebenspalten eine Zeile mit `UseCount = 0` erzeugen können und dass die Lesequeries beide gleich
  behandeln: nicht als Nutzung. Der Verweis auf die Brücke fällt weg.
- `src/EmotePurge.Worker/Harness/ReplayFidelityCalculator.cs` (:210-215) — der `<para>`-Absatz im
  Docstring von `BuildPopulation`, der die D5-Übergangszeit erklärt („during the D5 transition
  period the grid still shows …"), wird entfernt; der Satz davor („`UseCount` is the target
  contract") bleibt und stimmt jetzt ohne Einschränkung. **Ausschließlich Kommentar** — keine Zeile
  Logik, kein `AlgorithmVersion`-Bump, keine Änderung an Identität, Kopf oder Berichtsfeldern. Der
  Harness bleibt taktneutral berührt.

### 3.3 — Anzeige einbauen

- `web/src/app/features/usage-stats/usage-stats-page.ts`:
  - ein `computed()` neben `botsExcludedKey` (:316-319), das
    `sharedChatSeparatedCaptionKey(this.setStatus()?.sharedChatSeparatedSince ?? null)` liefert. Das
    `?? null` hat denselben Grund wie dort und bekommt denselben Kommentar: Eine ältere Api-Antwort
    mitten im Deploy lässt das Feld weg, und das muss exakt wie „nie geteilten Chat gesehen" lesen.
  - der `usageFlushed`-Zweig der Live-Aktualisierung (:821-827) fragt den Status künftig nach, wenn
    **eines der beiden** Daten noch `null` ist. Begründung: Ein Flush kann jetzt auch das
    Shared-Chat-Datum erstmals setzen, und beide Felder sind monoton wachsende Minima — steht ein
    Datum, ist es fertig. Es entsteht keine neue Anfrageklasse: Es bleibt bei höchstens einer
    Statusanfrage je Flush-Ereignis, genau wie heute. Der bestehende Kommentar wird auf beide
    Felder erweitert.
  - `formatDate` (:1053-1074): Der Kommentar, der die reinen `yyyy-MM-dd`-Werte aufzählt, nennt das
    neue Feld mit. **Kein Code-Umbau** — der Wert hat dieselbe Form wie `botsExcludedSince`.
- `web/src/app/features/usage-stats/usage-stats-page.html` (:231-249) — der neue Satz kommt als
  **weiterer Satz in denselben `<p>`**, direkt hinter den Bot-Satz, im selben `@if (…; as key)`-
  Muster und mit `formatDate(...)` als `date`-Interpolation. Kein eigenes Element, kein `role`, kein
  Banner: Es ist eine Fußnote zur Zahl, keine Meldung. Der bestehende Erklärkommentar über der
  Gruppe („One more sentence in the same caption, not a banner …") deckt die neue Zeile mit ab und
  wird nur um ihre Nennung ergänzt.

**Reihenfolge im Absatz:** `trackedSince` → Live-Tage → Bots → Shared Chat. Das ist die
Entstehungsreihenfolge der beiden Ausschlüsse und damit stabil begründbar; sie wird in §2.5 der
Designsprache mit festgehalten (3.5), damit sie ein Vertrag ist und nicht Zufall — nur dann darf
ein Test sie prüfen (Regel 12).

### 3.4 — Tests umdrehen und ergänzen

- `tests/EmotePurge.Infrastructure.Tests/Integration/UsageStatQueryServiceTests.cs`:
  - `SharedOnlyRow_ReadsAsUsedUntilZug2` → **`SharedOnlyRow_ReadsAsUnused_LikeBotOnly`**. Derselbe
    Seed (die drei Emotes: shared-nach-human, gemischt, ausschließlich shared), die Erwartungen
    umgedreht: Für das gemischte Emote zählt nur die eigene Nutzung; das Emote mit ausschließlich
    fremden Zeilen hat `TotalUseCount = 0`; `LastUsedDate` fällt zurück auf den älteren
    **menschlichen** Tag statt auf den jüngeren fremden; die Tagesreihe enthält den fremden Tag
    nicht; `GetChannelSeriesAsync` führt das ausschließlich-fremde Emote gar nicht mehr;
    `GetTotalsByEmoteIdsAsync` liefert nur die eigene Summe. Der Kommentar im Test sagt, dass dies
    der umgekehrte Marker ist und wofür er steht.
  - der Dateikopf-Kommentar (:13-19) wird entsprechend umgeschrieben: Die Brücke ist gefallen.
  - `GetRowsAsync_…`-Fall (:856-870) bleibt inhaltlich unverändert; nur sein Kommentar verliert die
    Formulierung „still bridged at this point in the rollout".
- E2E `web/e2e/usage-atlas.e2e.spec.ts` — zwei Fälle, gespiegelt an den beiden Bot-Fällen
  (:308-330):
  1. Ist ein Datum in der Api-Antwort, steht der Shared-Chat-Satz mit diesem Datum sichtbar auf der
     Seite, **und** die drei bestehenden Ehrlichkeitssätze stehen weiter daneben (der Fall prüft,
     dass der neue Satz keinen alten verdrängt).
  2. Ohne Datum kommt der Satz nicht vor.
  Der Wortlaut dient dabei nur als Erkennungsmerkmal des Satzes, nicht als Prüfgegenstand
  (Regel 12).

### 3.5 — Dokumentation, im selben Commit (Regel 3)

- **`docs/DECISIONS.md`** — neuer Eintrag ganz oben, Datum 2026-09-08, Titel in der Art
  „Die Oberfläche zeigt nur noch eigene Nutzung, die D5-Übergangssumme fällt". Die
  `**Betrifft:**`-Zeile führt alle in diesem Plan berührten Dateien, ausdrücklich mit
  `docs/Architectur.md` und `docs/UI-Designsprache.md`. Inhaltlich **muss** der Eintrag tragen:
  - dass D5 damit endet und was der Lesevertrag ab jetzt ist (Summen und Filter über `UseCount`;
    shared-only liest wie bot-only als unbenutzt);
  - dass angezeigte Zahlen dadurch fallen — bis zu dem Anteil, den die Messung vom 2026-09-06 für
    `brudivoeller_tv` (84 %) und `ronnyberger` (66 %) ausweist — und dass genau dieser Sturz der
    Grund für den erklärenden Satz ist;
  - dass das Datum der Caption **datenabgeleitet** ist (erste Sichtung, nicht Deploy) und in
    Kanälen ohne je geteilten Chat dauerhaft `null` bleibt, obwohl die Trennung dort genauso gilt —
    ein späterer Konsument darf `null` nicht als „keine Trennung" lesen;
  - dass es **keinen** Toggle gibt und warum (Nr. 1), samt dem Hinweis, dass das Issue #73
    ursprünglich einen vorschlug;
  - dass die Nutzungsspalte der Voting-Ergebnisse still mitfällt und warum das der Präzedenz des
    Bot-Splits folgt (Nr. 10);
  - dass der Covering-Index unangetastet bleibt und der Index-Only-Scan von selbst zurückkehrt
    (Nr. 8);
  - dass Zug 2 **taktneutral** ist: keine Zählungsänderung, kein `AlgorithmVersion`-Bump, kein
    neuer Stichtag, die #69-Uhr läuft ungestört weiter; die Freeze-Liste bleibt bis zum Ende des
    bindenden Laufs in Kraft.
- **`docs/Architectur.md`** — `:232`: Der Absatz über die Übergangszeit („Übergangsweise (D5, #73)
  summieren und filtern die produktiven Lesequeries …") wird auf den Endzustand umgeschrieben:
  Beide Nebenspalten stehen bewusst nicht im `INCLUDE`, weil die Aggregat-Queries sie nicht lesen —
  also wieder die ursprüngliche Begründung, jetzt für zwei Spalten. `:80` und `:275-280` bleiben
  inhaltlich richtig und werden nur geprüft, nicht angefasst.
- **`docs/UI-Designsprache.md`** — ein Aufzählungspunkt in §2.5, vor dem `Referenz`-Punkt: Was die
  Zahlen des Bogens **nicht** enthalten, steht als Satz in der Caption unter dem Bogen — nicht als
  Bedienelement, nicht als Banner, nicht als zweite Zahl an der Zelle; die Sätze stehen in einem
  gemeinsamen `<p>` in fester Reihenfolge (Zählbeginn → Live-Tage → Bots → geteilter Chat), und ein
  neuer Ausschluss reiht sich hinten an. Der Punkt nennt die beiden Caption-Module als Referenz.

**Grenzfälle für diesen Task.**

- **Kein Kanal darf durch den Rückbau seine Kanal-Seite verlieren:** Ein Emote ohne jede eigene
  Nutzung bleibt im Raster (es wird in `GetUsageContextAsync` zero-filled), es rutscht nur ins Band
  „Nie benutzt". Genau das ist die beabsichtigte Aussage.
- **Bänder, Sortierung und Verteilungsstreifen brauchen keine Änderung.** Sie rechnen im Frontend
  ausschließlich über `totalUseCount` (`usage-stats-page.ts:364, 381, 386, 392, 408, 428, 437, 449,
  456, 460`, `shared/emotes/usage-bands.ts`) und erben den Rückbau vollständig. Wer hier etwas
  anfasst, ändert mehr als beauftragt.
- **`usage-bands.spec.ts` und die übrigen Frontend-Specs müssen unverändert grün bleiben** — sie
  arbeiten mit eigenen Zahlen und wissen von der Herkunft nichts.
- Die Api-Filter-Matrix ändert sich nicht (kein neuer Endpunkt, kein neuer Filter):
  `tests/EmotePurge.Api.Tests` bleibt unberührt.
- Sollte der Compiler oder eine Suite eine **weitere** Stelle aufdecken, die mit
  `UseCount + SharedChatUseCount` rechnet und oben nicht steht, gilt: zurückdrehen und im
  Abschlussbericht nennen.

**Abnahme.** `dotnet build EmotePurge.slnx` warnungsfrei; `dotnet test EmotePurge.slnx` grün, mit
dem umgedrehten Marker-Test unter seinem neuen Namen; `npm --prefix web test -- --watch=false`
grün; `npm --prefix web run lint` und `format:check` grün. Eine Volltextsuche nach `D5` und
`Zug 2` in `src/`, `tests/` und `web/` findet danach **keine** Stelle mehr, die eine noch
bestehende Übergangskonstruktion behauptet.

**Commit.** `feat(usage-stats): count only own usage and say since when shared chat is excluded`
(ein Commit, inklusive der drei Dokumente — Begründung oben unter „Auslieferungsform").

---

## Task 4 — Gates

**Absicht.** Die im Projekt verbindliche Fertigmeldung herstellen, in der Reihenfolge, in der sie
sich nicht gegenseitig sabotiert.

**Schritte.**

1. **Sicherstellen, dass auf `:5151` keine Api lauscht.** Prüfung mit `ss -ltnp` (nicht mit
   `netstat | grep LISTENING` — das findet in dieser Umgebung nie etwas). Lauscht dort etwas, wird
   es beendet, bevor die E2E-Suite startet.
2. `dotnet test EmotePurge.slnx` (Docker läuft, Testcontainers).
3. `npm --prefix web test -- --watch=false`.
4. `npm --prefix web run e2e`. Erwartungswert der Laufzeit: rund 1,5–1,7 Minuten. **Rote Fälle
   quer über unbeteiligte Dateien bei deutlich längerer Laufzeit sind Speicherdruck, keine
   Regression** — dann die Suite einmal allein wiederholen, statt in den Code zu debuggen. Nach
   zwei erfolglosen Anläufen wird nicht ein drittes Mal dasselbe versucht, sondern der Befund
   dokumentiert.
5. `npm --prefix web run lint`, `npm --prefix web run format:check`, `dotnet format EmotePurge.slnx
   --verify-no-changes`.
6. **Erst wenn alle Commits aus Tasks 1–3 stehen:** `node scripts/coverage-local.mjs`. Das Skript
   misst nur **committete** Änderungen; meldet es „0 geänderte Datei(en)", ist das eine
   Nichtmessung und keine Entwarnung.

**Grenzfälle.**

- **Coverage-Risiko, benannt und mit vorentschiedener Reaktion:** Die neuen Zeilen in
  `usage-stats-page.ts` (ein `computed()`, eine erweiterte Bedingung) liegen in einer großen Datei
  ohne Vitest-Spec und zählen für Sonar als ungedeckt; E2E zählt nicht in die Coverage. Der Rest
  des Neucodes (Query-Methode, Service-Feld, Caption-Funktion) ist durch Tests gedeckt. Fällt die
  lokale Schätzung dennoch unter 80 %, wird **kein** neuer Komponenten-Spec improvisiert (Nr. 11);
  stattdessen wird der Befund samt Zahl in den Abschlussbericht geschrieben und dem Nutzer als
  Entscheidung vorgelegt. Die Schätzung ist in beide Richtungen unscharf und ersetzt den
  PR-Befund nicht.
- Der UI-Audit-Harness läuft nicht (Nr. 12).

**Abnahme.** Alle sechs Schritte durchlaufen, ihre Ausgaben im Abschlussbericht mit Laufzeit und
Zählwerten (Anzahl Tests je Suite) — nicht nur „grün".

**Commit.** keiner (reine Verifikation), außer einer eventuell nötigen Formatierungs-Korrektur, die
dann als eigener `style:`-Commit ohne andere Änderung läuft.

---

## Task 5 — Live-Probe gegen echte Infrastruktur (Regel 16)

**Absicht.** Regel 16 verlangt für Backend-Änderungen eine Verifikation gegen echte Postgres/Redis,
nicht nur grüne Suiten. Hier ist zu belegen, dass die neue Query auf einer echten Datenbank das
richtige Datum liefert und dass der Endpunkt es ausliefert.

**Vorgehen, unbeaufsichtigt ausführbar und ohne Browser:**

1. `docker compose up -d postgres redis` (mit Service-Namen, nie ohne).
2. Migrationsstand der lokalen Dev-DB prüfen; die Zug-1-Migration
   `20260907080507_AddUsageStatSharedChatUseCount` muss angewandt sein. Ist sie es nicht:
   `dotnet ef database update` mit den Standardprojekten aus `CLAUDE.md`.
3. In der lokalen Dev-DB einen Prüfstand herstellen — **ohne Bestandszeilen zu verändern**: einen
   eigenen Kanal mit einem Emote und drei `UsageStat`-Zeilen anlegen (eine rein menschliche, eine
   gemischte, eine rein fremde an einem eigenen Tag). Das geschieht per SQL gegen den lokalen
   Container; nichts davon berührt Prod.
4. Api starten (`dotnet run --project src/EmotePurge.Api`, Port 5151) und die beiden Antworten
   prüfen:
   - `GET /api/channels/<prüfkanal>/emotes/active-set` trägt ein
     `sharedChatSeparatedSince` gleich dem Tag der rein fremden Zeile.
   - `GET /api/channels/<prüfkanal>/usage-stats/totals?from=&to=` zeigt für das Emote **nur** die
     eigene Summe; die rein fremde Zeile taucht in keiner Zahl auf.
   Die Endpunkte sind hinter Auth und dem Zugriffsfilter; ist eine Session im Nachtlauf nicht
   herstellbar, wird stattdessen dieselbe Aussage **direkt über die Services** belegt (kleines
   Wegwerf-Programm oder ein bereits vorhandener Integrationsweg gegen die Dev-DB) und im Bericht
   als der gewählte Weg benannt. Was **nicht** passiert: den Befund weglassen und „Suiten sind
   grün" als Nachweis führen.
5. **Api wieder beenden** und den Prüfstand aus der Dev-DB entfernen. Auf `:5151` darf nichts
   zurückbleiben, sonst reißt der nächste E2E-Lauf.

**Grenzfälle.**

- Findet die Probe einen Unterschied zur Erwartung, ist das ein Befund, kein Anlass für einen
  spontanen Umbau: erst die Ursache benennen, dann den kleinstmöglichen Fix, dann Task 4 komplett
  wiederholen.
- Twitch und 7TV werden hier **nicht** gebraucht — dieser Zug ändert keinen Zählpfad. Das ist der
  Unterschied zu Zug 1 und der Grund, warum die Negativprobe aus B6 hier nicht wiederholt wird.
- Die visuelle Kontrolle im Browser (steht der Satz da, wo er soll, in beiden Sprachen und beiden
  Themes) bleibt dem Nutzer; sie wird im Abschlussbericht ausdrücklich als offener Handgriff
  übergeben.

**Abnahme.** Die drei Aussagen aus Schritt 4 belegt und im Bericht mit ihren Zahlen zitiert;
Api beendet; `docker compose ps` zeigt nur `postgres`/`redis`.

**Commit.** keiner.

---

## Task 6 — Abschluss

**Absicht.** Den Branch in einen übergebbaren Zustand bringen und dem Nutzer genau die Handgriffe
hinterlassen, die ein unbeaufsichtigter Lauf nicht tun darf.

**Schritte.**

1. `git status` sauber, alle Änderungen in den drei Commits aus Tasks 1–3 (plus ggf. dem
   `style:`-Commit aus Task 4). **Kein `git push`, kein PR, keine GitHub-Schreibaktion.**
2. `git log --oneline origin/main..HEAD` in den Bericht.
3. Abschlussbericht schreiben (als Antwort, nicht als Datei im Repo) mit: Testzahlen je Suite,
   Coverage-Schätzung, dem Ergebnis der Live-Probe, jeder Abweichung vom Plan mit Begründung, und
   der Liste der offenen Handgriffe (unten).
4. **Optional, wenn das Kontingent es hergibt:** `/codex:review --model gpt-5.6-sol --scope branch
   --base origin/main` als vorbereitende Zweitmeinung. `--scope` ist Pflicht — ohne ihn reviewt
   Codex bei sauberem Tree den Working Tree und meldet eine falsche Entwarnung mit Exit 0. Bricht
   der Lauf mit „Reviewer failed to output a response" und Exit 1 ab, ist das das Kontingent und
   kein Fehler im Diff; dann wird es nicht wiederholt, sondern im Bericht vermerkt. Die Findings
   werden **unverändert** wiedergegeben und nicht selbstständig umgesetzt.

**Offene Handgriffe für den Nutzer (nach dem Lauf).**

- Sichtprüfung im Browser: der Satz in de und en, hell und dunkel, auf einem Kanal mit und einem
  ohne Shared-Chat-Historie.
- `git push` und PR; die Zweitmeinung durch Codex Sol vor dem Merge ist Pflicht (Regel 22), falls
  Schritt 4 sie nicht schon geliefert hat.
- Nach dem Merge: Prod-Deploy. **Keine Migration nötig** — Zug 2 ändert kein Schema.
- Kommentar an #73 (und, weil dort die Termine stehen, an #69), dass die Anzeige ausgeliefert ist
  und die Uhr davon unberührt bleibt. Die Freeze-Liste läuft **nicht** mit diesem Merge aus,
  sondern erst mit dem bindenden Lauf.

---

## Abnahme des Gesamtpakets

Das Paket ist fertig, wenn **alles** davon zutrifft:

1. `dotnet test EmotePurge.slnx` grün, mit `SharedOnlyRow_ReadsAsUnused_LikeBotOnly` in seiner
   umgekehrten Fassung und den sieben neuen Backend-Fällen aus Task 1.
2. `npm --prefix web test -- --watch=false` grün, inklusive des neuen Caption-Specs.
3. `npm --prefix web run e2e` grün (keine Api auf `:5151`), inklusive der zwei neuen Fälle,
   Laufzeit im gewohnten Rahmen.
4. `npm --prefix web run lint`, `npm --prefix web run format:check` und
   `dotnet format EmotePurge.slnx --verify-no-changes` ohne Befund.
5. `node scripts/coverage-local.mjs` **nach** den Commits gelaufen, mit einer echten Messung (nicht
   „0 geänderte Dateien"), Ergebnis im Bericht.
6. Keine Fundstelle mehr in `src/`, `tests/`, `web/`, die eine bestehende D5-Übergangssumme
   behauptet; `GetRowsAsync`, `GetEarliestBotUsageDateAsync` und der gesamte Harness-Rechenkern
   unverändert.
7. `docs/DECISIONS.md`, `docs/Architectur.md` und `docs/UI-Designsprache.md` im **selben** Commit
   wie die Vertragsänderung (Regel 3).
8. Die Live-Probe aus Task 5 belegt, dass die fremde Zeile nirgends mehr in eine angezeigte Zahl
   eingeht und das Datum am Endpunkt ankommt.
9. Branch lokal, nichts gepusht, nichts auf GitHub geschrieben.

## Was bewusst nicht dazugehört

- **Kein Toggle** „Shared Chat anzeigen", kein Zustand in `emote-usage-filter.ts`, keine zweite Zahl
  an der Zelle (`12× (+3 Shared Chat)`) — Nr. 1. Wer das später will, macht ein eigenes Issue auf.
- **Keine eigene Drilldown-Serie** über `SharedChatUseCount`, weder im Sidecar noch im Dialog. Sie
  wäre ein zweiter Zahlenbegriff auf der Seite und hinge zudem an einer neuen Query.
- **Keine Migration und keine Index-Änderung** — Nr. 8.
- **Keine Änderung am Harness-Rechenkern**: keine neue Kennzahl, kein `AlgorithmVersion`-Bump, kein
  neuer Stichtag, keine Berührung von `ReplayDayCounter`, `ReplayFidelityCalculator` (außer dem
  einen veralteten Docstring-Absatz), `HarnessRunner`, `HarnessInputHash` oder `HarnessOptions`.
- **Keine Caption auf der Voting-Ergebnisseite** — Nr. 10.
- **Keine Rückwirkung auf Bestandsdaten.** Zeilen aus der Zeit vor dem Zug-1-Deploy enthalten
  fremde Nutzung untrennbar in `UseCount`; genau das ist der Satz, den die Caption sagt.
- **Kein UI-Audit-Lauf**, kein neues Szenario in `ui-audit.audit.ts` — Nr. 12.
- **Kein `git push`, kein PR, keine Issue-Kommentare, kein Prod-Zugriff** (auch kein lesender, auch
  kein Tunnel).
- **Kein Aufräumen am Rand**: keine Umbenennung von Bestandsfeldern, keine Sprachvereinheitlichung
  in Bestandskommentaren, keine Formatierungswelle über unbeteiligte Dateien.
