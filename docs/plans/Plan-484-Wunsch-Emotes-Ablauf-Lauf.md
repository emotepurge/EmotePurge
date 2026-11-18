# Plan #484: Wunsch-Emotes, Kind K2: Ablauf-Lauf im Worker

Erstellt am 2026-11-18 im Worktree `EmotePurge-wt-484` (Branch `feat/484-wish-expiry`, Basis `feat/483-wish-core` @ `ec8d9697`;
nach dem Merge von PR #492 wird auf `main` rebased, die Codestellen dieses Plans liegen alle in K1-Dateien und ändern sich dabei
nicht).
**Status: Erstfassung, noch ohne adversariale Prüfung (Sol). Keine blockierende Betreiberfrage; zwei Lesarten zur Bestätigung
(Abschnitt 9).**
Quellen: [Spec](../superpowers/specs/2026-11-17-wunsch-emotes-spec.md) Abschnitte 1 bis 17 und „K2: Ablauf-Lauf im Worker“ (AK22 bis
AK31); aus K3, K4 und K6 nur, was sie von K2 erwarten; [Plan-483](Plan-483-Wunsch-Emotes-Backend-Kern.md) (Revision 2) als Vorbild
und für die Verträge aus K1; die K1-Übergaben `docs/nachtlaeufe/nacht-2026-11-17-setze-den-plan-docs-plans-plan-483.md` und
`docs/nachtlaeufe/live-T12-2026-11-18.md`; DECISIONS „Wish emotes, child 1: backend core“ samt den Betreiberentscheidungen vom
2026-11-18; Issue #484, Epic #481; `CLAUDE.md` (Schichten, Regeln 1 bis 22, Gates, Sprache).

**Was dieser Plan trägt:** Abweichungen zwischen Spec und dem gebauten K1-Code, die Planer-Entscheidungen dazu, den Schnitt in Tasks
mit Verträgen, Grenzfällen und Tests, die AK-Zuordnung, die Live-Verifikation, Gates und Deploy. Kein Code; Namen und Signaturen nur
als Form. Die Spec reist mit: Jeder Implementer liest seinen Task hier **und** die genannten Spec-Abschnitte, und er liest die
genannten K1-Dateien, bevor er schreibt. Der K1-Code ist Werkzeug, nicht Entwurf.

**Lesehinweis:** **[E]** Planer-Entscheidung (der Betreiber kann widersprechen) · **[P]** Präzisierung ohne Entscheidungsgehalt ·
**[B]** Betreiberentscheidung (Spec Abschnitt 17, Runden 1 bis 3, und 2026-11-18) · „Spec E4“ meint die Festlegung der Spec,
„AK22“ das Akzeptanzkriterium aus K2, „K1 (j)“ den Absatz im DECISIONS-Eintrag von K1.

**Betreiberfestlegungen, die K2 trägt [B]:** eigener Worker im 15-Minuten-Takt; Laufzeit 1 bis 365 Tage; Bewertungsfenster des
Schutzes = min(7 Tage, Zeit im Set), unter einem vollen Tag gilt wie eine Lücke; Lückenregel nur bei eingeschaltetem Schutz;
Schwelle als Mittel je Tag über alle Slots des Emotes, ohne Wert ist der Schutz aus; ein schon fälliger oder eingereihter Wunsch wird
nicht erneut fällig gestellt; vorläufige Starts werden bei gesundem Sync nicht fällig; je Fälligkeit ein Audit-Eintrag `wish.due`;
im manuellen Modus erinnert K2 nur, der Bot-Teil ist K4.

---

## 0. Befund vor dem Plan

Alle Stellen sind am Stand `ec8d9697` nachgelesen.

1. **Zeile 1 der Entscheidungstabelle gegen `IsBotMode` aus K1.** `WishService.EffectiveSettings.IsBotMode`
   (`WishService.cs:958`) ist `BotModeEnabled && BotSuspendedReason is null`; ein ausgesetzter Bot-Kanal gilt dort als manuell
   (so steht er auch als `mode` im DTO). Nähme K2 diesen Wert für Zeile 1, ergäbe ein ausgesetzter Kanal `expired`, und Zeile 3
   („oder `BotSuspendedReason`“) wäre nie erreichbar. K4 braucht aber genau `bot_unavailable`: Nur dieser Grund (neben
   `protected` und `coverage_gap`) erlaubt später „Bot entfernen lassen“. **[P]** Die Policy bekommt `BotModeEnabled` roh und
   `BotSuspendedReason` getrennt. Zeile 1 heißt „`BotModeEnabled` ist falsch“.
2. **Die Nutzungsrechnung ist in K1 privat.** `WishService.LoadUsageAsync` (`:621-670`) rechnet Fenster, Summe, Mittel, Rang und
   Lückenmerkmal für die Detailantwort; `SumUseAsync` (`:672`) daneben. K2 braucht dieselbe Summe zum Bewertungszeitpunkt des
   Laufs, ohne den Rang. Eine zweite Rechnung daneben liefe auseinander, und AK30 verlässt sich darauf, dass die Detailantwort
   dieselben Zahlen zeigt wie der Lauf. **[E]** T2 zieht den Teil „Fenster, Summe, Mittel, Lücke“ in einen statischen Abfragehelfer
   in Infrastructure (Form `WishUsageQueries`, Muster `ChannelQueries`, `RowLocks`); die Detailantwort ruft ihn und rechnet den Rang
   wie bisher dazu. Kein Interface (statischer Helfer ohne eigenen Zustand), keine Konstruktoränderung an `WishService`.
3. **Ein vorläufiger Start kann für immer vorläufig bleiben.** `ResolveStart` (`WishService.cs:853-870`) setzt
   `StartsAtAuthoritative` nur wahr, wenn der Slot ein REST-bestätigtes Datum trägt, auch bei ausdrücklichem `startsAtUtc`. Ein
   `adopt` auf einen **undatierten** Slot (`EmoteSlot.AddedAt` null, „unbekannt“) bleibt vorläufig, bis 7TV ein Datum liefert
   (`WishReconcileService.cs:304-308`, Kommentar „the next dated sync decides“); liefert 7TV nie eines, bleibt es dabei. Nach dem Wortlaut der
   Spec („nicht fällig, solange der Kanal aktiv gesynct wird“) liefe ein solcher Wunsch in einem gesunden Kanal **nie** ab.
   **[E]** Der Aufschub ist begrenzt: Ein vorläufiger Start hält die Fälligkeit nur zurück, solange der Sync frisch ist **und**
   der Ablauf weniger als 24 Stunden zurückliegt. Danach gilt das vorläufige Datum. Dieselben 24 Stunden wie in der Spec, eine
   benannte Konstante, keine Slot-Abfrage im Lauf. Der Normalfall (EventAPI-Datum, das der nächste Vollsync nach rund einer Minute
   bestätigt) ist davon unberührt. Erwogen und verworfen: am Slot ablesen, ob eine Korrektur noch kommen kann; das bräuchte drei
   Sonderfälle (undatiert, ohne Slot, gelöschter Slot) für denselben Effekt.
4. **„Letzter erfolgreicher Sync“** ist `Channel.LastSyncedAtUtc` (gestempelt in `SevenTvSyncService.cs:906`, auch ohne Änderung
   am Set). **[E]** Null (nie gesynct, seit es die Spalte gibt) zählt als nicht frisch: Es gibt keinen Beleg für einen laufenden
   Sync, also gilt das vorläufige Datum. Ein deaktivierter Kanal wird nicht mehr gesynct und fällt nach 24 Stunden von selbst
   unter diese Regel.
5. **Das genannte Muster passt nur halb.** `ChatLogBackfillWorker` wartet **vor** der Schalterprüfung auf die Boot-Recovery
   (`ChatLogBackfillWorker.cs:96-100`). E6 sagt: kein Warten. **[P]** `WishExpiryWorker` prüft zuerst den Schalter und nimmt
   `BootRecoveryGate` gar nicht als Abhängigkeit. Vorbild für Takt und Fehlerbehandlung ist die Log-Ablauf-Schleife in
   `DataRetentionWorker.cs:41-80` (erster Lauf nach einer Minute, Timer vor dem ersten Lauf angelegt, ein Fehler kostet einen Takt).
6. **Testplan der Spec „Worker.Tests: 0“ trägt nicht.** `WorkerServiceRegistrationTests.cs:22-36` nagelt die Zahl vierzehn fest
   und muss für AK29 auf fünfzehn. AK28 („genau eine Logzeile, kein Datenbankzugriff danach“) lässt sich nur am Worker selbst
   beweisen, und das geht container-frei (Vorbild `SystemLoadRollupWorkerTests`). **[E]** T4 schreibt Worker-Tests.
7. **Der Abgleich veröffentlicht selbst.** `WishReconcileService.ReconcileChannelAsync` (`:66-81`) arbeitet in eigenem Scope und
   schickt nach seinem Commit sein eigenes `wish.changed`. „Ein `wish.changed` je betroffenem Kanal“ aus der Spec heißt deshalb
   **[P]**: Der Fälligkeitsschritt schickt je Kanal genau eines, egal wie viele Wünsche er dort bewegt; ändert vorher der Abgleich
   etwas, kommt dessen eigenes dazu. Beide folgen dem K1-Vertrag „höchstens eines je Commit“.
8. **`WishTransitions` kennt die Zeile schon** (`WishTransitions.cs:189`, `active` mit `WishAction.Present` nach `presented`,
   ohne Bedingung). K1 hat nachträglich die Bedingung `HasExpiry` für `extend` eingeführt (`:185`, `:193`, K1 (c)). Für K2 folgt:
   **[P]** Der Lauf fragt die Tabelle (einzige Stelle, die Übergänge kennt) und gibt dabei den Kontext mit `HasExpiry` mit;
   beim Vorlegen bleibt `ExpiresAtUtc` stehen, sonst ließe sich ein vorgelegter Wunsch nicht mehr verlängern. Die Befehle aus K1
   leeren `StatusReason` bei jedem Zustandswechsel (`WishService.cs:797-803`); K2 ist der erste Schreiber, der einen Grund setzt.
9. **`DecisionVersion` beim Vorlegen.** E25 zählt „jede Verwalter-Aktion und jede Eigentumsänderung“. Der Lauf ist keines von
   beiden. **[E]** Vorlegen und Einreihen erhöhen `DecisionVersion` nicht; das Einreihen schreibt den aktuellen Wert nach
   `RemovalQueuedVersion`.
10. **Audit-Details von `wish.due`.** Die Spec nennt `wishId` und `outcome`. Die Messgröße (a) aus Spec 13 rechnet „binnen
    24 Stunden nach `ExpiresAtUtc`“, die Spalte wird beim Verlängern aber überschrieben, und der Zeitpunkt des Eintrags ist der
    Zeitpunkt der Feststellung (bis 15 Minuten später, nach einem Worker-Ausfall oder dem Aufschub aus Befund 3 auch deutlich
    mehr). **[E]** Details sind `wishId`, `outcome` und `expiresAtUtc` (der Ablauf, auf den sich der Eintrag bezieht). Kein
    Personenbezug, keine Namen (AK19 bleibt gewahrt). Der Eintrag trägt den Kanalnamen wie die K1-Einträge und erscheint damit
    im Aktivitäts-Feed des Kanals unter „system“; die Übersetzung `audit.actions.wish.due` liefert K3 (Risiko R5).
11. **Zweiter `wish.due` ohne Verlängerung ist möglich (ab K4).** Verwirft etwas anderes als eine Verwalter-Entscheidung die
    Einreihung (Eigentum endet im Abgleich durch Umbenennen, oder der Bot findet sich unter der Sperre nicht mehr berechtigt und
    leert `RemovalQueuedAtUtc`), ist der Wunsch wieder `active`, abgelaufen und nicht eingereiht. Der nächste Lauf bewertet ihn
    neu, typisch mit `not_bot_owned`. **[E]** Gewollt: Sonst bliebe er unbemerkt hängen. Es entsteht ein zweiter Eintrag zur selben
    Fälligkeit; über `expiresAtUtc` (Befund 10) fasst die Auswertung beide zusammen. Vor K4 nicht erreichbar.
12. **Keine Advisory-Sperre.** E6 braucht die Retention-Sperre nicht, und Idempotenz hängt hier nicht an „nur ein Lauf“,
    sondern an der Zeilensperre: **[E]** Der Lauf bearbeitet jeden fälligen Wunsch in einer eigenen kurzen Transaktion, sperrt die
    Zeile (`RowLocks.LockWishesByIdsAsync`), liest sie erst danach und prüft die Auswahlbedingung erneut. Zwei gleichzeitige Läufe
    (zwei Worker-Container während eines Stack-Updates) ergeben damit genau einen Übergang und einen Audit-Eintrag. Die Kanalzeile
    sperrt der Lauf nicht: Er fügt nichts ein und schreibt keine `SlotId` (K1, Plan 2.3); er hält nie mehr als eine Wunsch-Zeile
    und fordert danach keine weitere Sperre an, ein Zyklus mit Abgleich, Zusammenführung oder Purge ist ausgeschlossen.
13. **`IWishBotAvailability` ist nur die globale Hälfte.** Zeile 3 hat zwei Quellen: global (Token, Notaus, Schalter, ab K4) und
    je Kanal (`BotSuspendedReason` aus den Einstellungen, schon in K1 angelegt). **[E]** Die Schnittstelle beantwortet nur
    „ist der Bot global verfügbar“; die Standard-Implementierung sagt immer nein. Der Lauf fragt sie höchstens einmal je Durchlauf
    und nur, wenn ein fälliger Wunsch die Zeilen 1 und 2 passiert hat. K4 ersetzt die Registrierung.
14. **Lückenmerkmal.** `WishUsageWindow.HasCoverageGap` (`WishUsageWindow.cs:34-38`) prüft vier Dinge: Zählbeginn nach
    Fensterbeginn, `DeactivatedAtUtc` gesetzt, `IsBotActive` falsch, Fenster unter 24 Stunden. Die Kurzform des Betreibers nennt
    `IsBotActive` nicht, Spec E5 und der Code schon. **[P]** Der Code bleibt; die zusätzliche Bedingung wirkt nur in die sichere
    Richtung (vorlegen statt entfernen).
15. **Summe über ganze UTC-Tage, Länge mit Bruchteilen.** `LoadUsageAsync` summiert vom UTC-Tag des Fensterbeginns bis zum
    Bewertungstag einschließlich (`:628-630`) und teilt durch die Fensterlänge in Tagen. Der erste Tag zählt also ganz, auch vor
    der Uhrzeit des Fensterbeginns. So steht es in Spec E4; das Mittel fällt dadurch eher zu hoch aus (sichere Richtung). **[P]**
    Die Tests zu AK23 und AK24 legen ihre Nutzung deshalb bewusst auf Tage **im** Fenster und sagen im Kommentar, welcher Tag zählt.
16. **Im manuellen Modus braucht der Lauf keine Nutzung.** Zeile 1 gewinnt vor Schutz und Lücke. Vor K4 endet jede Fälligkeit
    in Produktion als `presented` (`expired`); der Schutz wirkt erst im Bot-Modus. Das deckt sich mit AK30 („Ergebnis … im
    manuellen Modus bleibt“). **[E]** Der Lauf lädt die Nutzung nur, wenn die Policy sie braucht (Bot-Modus, Eigentum, Bot
    verfügbar, Schwelle gesetzt). Die Zeilen 2 bis 6 sind vor K4 nur über Tests erreichbar, die Einstellungen und Eigentum direkt
    in die Datenbank schreiben und die Verfügbarkeit ersetzen.
17. **Welche Kanäle der Lauf abgleicht.** Die Spec sagt „jeden Kanal mit laufenden Wünschen“. Der Abgleich aus K1 schaut auch auf
    Rückkehrer (`removed_externally`) und auf `kept`-Wünsche, die ihren Slot freigeben müssen (`WishReconcileService.cs:83-120`).
    **[E]** Der Lauf ruft den Abgleich für jeden Kanal, der einen Wunsch in `approved`, `active`, `presented`,
    `removed_externally` oder `kept` mit gesetzter `SlotId` hat. Die Vorprüfung des Abgleichs kostet für einen Kanal ohne Befund
    eine Abfrage; bei höchstens 80 aktiven Kanälen ist das je Takt vernachlässigbar.
18. **Dateiliste der Spec.** `WorkerServiceRegistration.cs:56-70` stimmt (Registrierungen in `:55-70`). Es fehlen:
    `Entities/AuditLogEntry.cs` (Konstante `wish.due`), `Entities/WishStatus.cs` (Konstante des Aufschubs in `WishLimits`),
    `ServiceCollectionExtensions.cs` (`:344-357`, zwei Registrierungen), `WishService.cs` (Befund 2), `docs/Architectur.md:86`
    (nennt „fourteen hosted services“), `docs/Operations.md` (Abschnitt „Wish emotes“, `:1841`), der Kommentar „fourteen“ in
    `WorkerServiceRegistration.cs:44`, die Tests.
19. **Was K1 aus der Live-Probe mitgibt** (`live-T12`, Auffälligkeit 5): `adopt` eines alten Slots mit Kanal-Default ergibt
    sofort einen `active`-Wunsch mit Ablauf in der Vergangenheit. Mit K2 wird ein solcher Wunsch im nächsten Takt vorgelegt. Das
    ist das gewollte Verhalten und zugleich der Hebel für AK30.

## 1. Umfang

### 1.1 Drin

Spec „K2“: reine Entscheidung (`WishExpiryPolicy`), Dienst (`IWishExpiryService`, `WishExpiryService`), Worker (`WishExpiryWorker`),
Schnittstelle `IWishBotAvailability` mit Standard „nie verfügbar“, Option `Wishes:ExpiryIntervalMinutes`, Audit `wish.due`,
gemeinsame Nutzungsabfrage (Befund 2), Compose-Mapping, Doku, Live-Verifikation AK30.

### 1.2 Was K2 den anderen Kindern liefert

| Für | Vertrag aus K2 |
|---|---|
| K3 | Ein abgelaufener Wunsch steht spätestens einen Takt nach `ExpiresAtUtc` auf `presented` mit Grund `expired`, zählt damit in `summary` und `mine`, und der Kanal-Stream bekommt `wish.changed` (Live-Probe AK45). Neuer Audit-Typ `wish.due` im Aktivitäts-Feed, Akteur „system“, Details `wishId`, `outcome`, `expiresAtUtc`; K3 übersetzt ihn |
| K4 | `IWishBotAvailability` zum Ersetzen (eine Registrierung); Zeile 6 schreibt `RemovalQueuedAtUtc` und `RemovalQueuedVersion = DecisionVersion` und lässt `active`; die abgeleitete Warteschlange von K4 liest genau das. K2 stößt den Bot nicht an (kein `WISHBOT:`), der 30-Sekunden-Takt von K4 genügt. Fällt der Bot nach dem Einreihen aus, legt K4 selbst vor (Spec 7, Punkt 8); leert K4 eine Einreihung ohne Zustandswechsel, bewertet der nächste Lauf neu (Befund 11). Die Policy trennt `BotModeEnabled` von `BotSuspendedReason` (Befund 1) |
| K6 | nichts Neues. Ein automatisch aktivierter Wunsch (E21) mit vorläufigem EventAPI-Datum fällt unter die Regel aus Befund 3 und 4; der Verfall offener Wünsche bleibt bei K6 (E26) |

### 1.3 Draußen

Jede 7TV-Schreibaktion, das Ausführen einer Einreihung, die echte Bot-Verfügbarkeit, `bot-remove` (K4); Verfall von `pending`
(K6); jede Oberfläche und `audit.actions.wish.due` (K3); eine Lückentabelle für Worker-Ausfälle (RR1 der Spec, akzeptiertes
Restrisiko); Migration (keine, Abschnitt 6).

## 2. Verträge, die mehrere Tasks teilen

Namen sind Form, nicht Wortlaut; wer davon abweicht, zieht alle Tasks nach.

**2.1 Auswahl und Fälligkeit.** Ein Wunsch ist Kandidat des Laufs, wenn `Status = active`, `ExpiresAtUtc` nicht nach „jetzt“
und `RemovalQueuedAtUtc` leer ist (Index `IX_Wishes_Status_ExpiresAtUtc`). „Jetzt“ ist **ein** Zeitpunkt je Durchlauf, vom
`TimeProvider` gelesen nach dem Abgleichsschritt; er ist zugleich der Bewertungszeitpunkt des Fensters (E4). Ein Kandidat mit
`StartsAtAuthoritative = false` wird **zurückgehalten** (kein Schreiben, kein Audit, kein Event), solange beides gilt: der Kanal
hat ein `LastSyncedAtUtc` jünger als 24 Stunden, und `ExpiresAtUtc` liegt weniger als 24 Stunden zurück (Befund 3, 4). Die
24 Stunden stehen einmal als benannte Konstante in `WishLimits`.

**2.2 `WishExpiryPolicy` (Core, rein).** Drei Fragen, jede eine statische Funktion ohne I/O:

- *Zurückhalten?* Eingabe: `StartsAtAuthoritative`, `ExpiresAtUtc`, `LastSyncedAtUtc` des Kanals, „jetzt“. Ausgabe: fällig oder
  zurückgehalten, nach 2.1.
- *Braucht die Entscheidung die Nutzung?* Wahr genau dann, wenn Bot-Modus an, Eigentum besteht, der Bot verfügbar ist (global und
  je Kanal) und eine Schwelle gesetzt ist.
- *Entscheidung* (`Decide`): Eingabe sind die Fakten `BotModeEnabled`, `BotSuspendedReason`, `BotOwned`, globale Verfügbarkeit,
  Schwelle (leer = Schutz aus) und, wo nötig, die Bewertung (Mittel je Tag, Lückenmerkmal). Ausgabe: „vorlegen mit Grund“ oder
  „Entfernung einreihen“. Erste zutreffende Zeile gewinnt, exakt nach der Tabelle der Spec: (1) Bot-Modus aus → `expired`;
  (2) kein Eigentum → `not_bot_owned`; (3) Bot global nicht verfügbar oder Kanal ausgesetzt → `bot_unavailable`; (4) Schwelle
  gesetzt und Lücke → `coverage_gap`; (5) Schwelle gesetzt und Mittel ≥ Schwelle → `protected`; (6) einreihen. Fehlt die
  Bewertung, obwohl die zweite Frage „ja“ sagt, ist das ein Programmierfehler und wirft.

Die Gründe sind die Konstanten aus `WishStatusReason`; das `outcome` im Audit ist der Grund bzw. die Zeichenkette `queued`
(eine Konstante an der Policy). Fenster, Mittel und Lücke rechnet weiter `WishUsageWindow` aus K1; die Policy rechnet sie nicht nach.

**2.3 Schreiben je Wunsch (T3).** Eigene Transaktion je Kandidat: Zeile sperren, danach lesen, Auswahlbedingung und Kanal erneut
prüfen (ein inzwischen verlängerter, behaltener, entfernter, eingereihter oder in einen anderen Kanal gewanderter Wunsch wird
übersprungen), Einstellungen und Kanalfakten hinter der Sperre lesen, entscheiden, schreiben, Audit-Eintrag, Commit.

- *Vorlegen:* Zielzustand aus `WishTransitions` (Aktion `Present`, Kontext mit `HasExpiry`), `StatusReason` = Grund,
  `StatusChangedAtUtc` = „jetzt“. Unverändert bleiben `ExpiresAtUtc`, `EndedAtUtc` (leer), `DecisionVersion`, Slot und Eigentum.
- *Einreihen:* `RemovalQueuedAtUtc` = „jetzt“, `RemovalQueuedVersion` = `DecisionVersion`. Zustand, Grund und
  `StatusChangedAtUtc` bleiben.
- *Audit:* genau ein Eintrag `wish.due` unter `AuditActor.System`, Kanalname gesetzt, Ziel der Wunsch, Details nach Befund 10,
  im selben `SaveChanges` wie die Änderung. Nie Alias, Emote-Name oder Einlöser.
- *Event:* nach dem letzten Commit eines Kanals genau ein `wish.changed` über `IWishChangedPublisher` (K1 (i)); auch das
  Einreihen zählt, weil die Karte `removalQueuedAtUtc` zeigt.
- *Fehler:* Ein Fehler an einem Wunsch rollt nur dessen Transaktion zurück, leert den Change-Tracker, wird gezählt und geloggt
  (Wunsch-ID, Ausnahmetyp, SQLSTATE; nie die Meldung) und lässt den Lauf weitergehen. Abbruch (`OperationCanceledException` am
  Stopp-Token) geht durch.

**2.4 Ergebnis des Laufs.** `IWishExpiryService.RunAsync` liefert eine Zusammenfassung mit reinen Zählwerten: abgeglichene Kanäle,
gescheiterte Abgleiche, vorgelegt (gesamt und je Grund), eingereiht, zurückgehalten, übersprungen, gescheitert. Der Worker macht
daraus höchstens eine Logzeile.

**2.5 Zeit und Sprache.** Alles nimmt `TimeProvider`; Zeiten sind UTC-`DateTime` wie im Bestand. Kommentare, Logs, Commits,
DECISIONS englisch.

## 3. Tasks

Je Task ein Subagent mit frischem Kontext, strikt nacheinander (Builds teilen `obj/`). Jeder Task endet mit grünem
`dotnet build EmotePurge.slnx`, seinen Tests und einem eigenen Conventional Commit. Der DECISIONS-Eintrag ist **ein** englischer
Eintrag „Wish emotes, child 2: expiry run (#484, epic #481)“, direkt über dem K1-Eintrag; T1 legt ihn an, jeder Task mit
Vertragsänderung ergänzt seinen Absatz und die `**Betrifft:**`-Zeile im selben Commit (Regel 3). Befehle im Nachtlauf einzeln
absetzen, keine Ketten mit `cd … &&` (Erfahrung aus dem K1-Lauf). Nie `docker compose` aus dem Worktree.

| Task | Inhalt | Modell (Vorschlag) |
|---|---|---|
| T1 | Verträge in Core: Policy, Schnittstellen, Option, Konstanten | sonnet |
| T2 | Gemeinsame Nutzungsabfrage (reines Umziehen) | sonnet |
| T3 | `WishExpiryService` samt Standard-Verfügbarkeit und Registrierung | opus |
| T4 | `WishExpiryWorker`, Registrierung, Compose | sonnet |
| T5 | Doku | sonnet |
| T6 | Live-Verifikation (Abschnitt 5) | sonnet mit Orchestrator |
| T7 | Gates (Abschnitt 6) | Orchestrator |

### T1: Verträge in Core

- **Ziel:** Alles Reine, gegen das T3 und T4 bauen. Keine Datenbank.
- **Dateien (neu, `src/EmotePurge.Core/Services/`):** `WishExpiryPolicy.cs`, `IWishExpiryService.cs` (mit der Zusammenfassung
  aus 2.4), `IWishBotAvailability.cs`. **Geändert:** `Services/WishOptions.cs`, `Entities/AuditLogEntry.cs` (Konstante
  `wish.due` bei den übrigen `wish.*`, mit dem Hinweis, dass der Worker sie unter `AuditActor.System` schreibt),
  `Entities/WishStatus.cs` (in `WishLimits`: die 24 Stunden aus 2.1), `docs/DECISIONS.md`.
- **Verträge:** Policy nach 2.2. `IWishBotAvailability`: eine asynchrone Frage „global verfügbar?“ mit Abbruch-Token, sonst
  nichts (Befund 13). `IWishExpiryService`: eine Methode `RunAsync(CancellationToken)` mit Ergebnis nach 2.4.
  `WishOptions.ExpiryIntervalMinutes`: Default 15, `Validate()` wirft außerhalb 1 bis 1440 mit einer Meldung, die den
  Konfigurationsschlüssel nennt (die Methode ist heute leer, `WishOptions.cs:23-25`).
- **Grenzfälle:** Reihenfolge der Zeilen (ein Fall, der 2 und 5 erfüllt, ergibt `not_bot_owned`; einer, der 3 und 4 erfüllt,
  `bot_unavailable`; einer, der 4 und 5 erfüllt, `coverage_gap`). Mittel exakt gleich der Schwelle ist geschützt. Schutz aus
  bei Lücke und hoher Nutzung → einreihen (AK25, dritter Teil). Ausgesetzter Kanal mit Eigentum → `bot_unavailable`, ohne
  Eigentum → `not_bot_owned`. Zurückhalten: maßgeblicher Start nie; vorläufig mit Sync vor 1 Stunde und Ablauf vor 1 Minute ja;
  Sync vor 25 Stunden nein; `LastSyncedAtUtc` leer nein; Sync frisch, aber Ablauf vor 25 Stunden nein; beide Grenzen exakt bei
  24 Stunden als eigene Fälle festlegen und im Test benennen.
- **AKs:** AK22; reiner Teil von AK23, AK25, AK26.
- **Tests:** `Infrastructure.Tests/Unit` (dort liegen die reinen Wunsch-Tests aus K1), tabellengetrieben: sechs Zeilen plus
  Reihenfolgefälle; Zurückhalte-Regel; „braucht Nutzung“ für jede Kombination; `Decide` ohne Bewertung, wo sie nötig ist, wirft.
  AK23 als Kette aus `WishUsageWindow` und Policy: Laufzeit 30 Tage → Fenster genau 7 Tage; 2 Tage → 2 Tage, Mittel = Summe
  durch 2; 1 Tag → 24 Stunden, gültig; 23 Stunden → Lücke. Vorhandene Fälle in `WishUsageWindowTests`
  (`WishSlotMatcherTests.cs:206-262`) nicht doppeln, nur ergänzen. `WishOptionsTests`: Default 15; 0 und 1441 werfen; 1 und 1440
  gelten. `CoreAssemblyReferenceTests` bleibt grün.
- **Fertig:** Tests grün; DECISIONS-Eintrag angelegt mit: Tabelle der sechs Zeilen und Befund 1; begrenzter Aufschub (Befund 3,
  4) als Abweichung von der Spec; `DecisionVersion` bleibt (Befund 9); Schnittstelle nur global (Befund 13).
- **Commit:** `feat: add wish expiry policy and contracts`

### T2: Gemeinsame Nutzungsabfrage

- **Ziel:** Detailantwort und Ablauf-Lauf rechnen Fenster, Summe, Mittel und Lücke an **einer** Stelle. Verhalten unverändert.
- **Dateien:** neu ein statischer Abfragehelfer in `src/EmotePurge.Infrastructure/` (Form `WishUsageQueries`, Ort neben
  `ChannelQueries` bzw. `RowLocks`); geändert `Services/WishService.cs` (`LoadUsageAsync` ruft ihn, der Rang bleibt dort).
- **Verträge:** Eingabe: Kontext, Kanal-ID, 7TV-Emote-ID, Start des Wunsches, Bewertungszeitpunkt. Ausgabe: Fensterbeginn,
  Bewertungszeitpunkt, Summe, Mittel je Tag, Lückenmerkmal. Inhalt exakt wie heute: `UseCount` allein, alle Slots des Emotes im
  Kanal, archivierte eingeschlossen, unsettled Seeds nicht, UTC-Tage vom Tag des Fensterbeginns bis zum Bewertungstag, Regel 10
  (erst Slot-IDs, dann Summe), Kanalfakten über `TrackingCoverage.TrackedSince`. Der Helfer liest nur (kein Tracking).
- **Grenzfälle:** kein Slot des Emotes → Summe 0, Mittel 0; Kanalzeile fehlt (Purge im selben Moment) → der Aufrufer bekommt
  „kein Ergebnis“ statt einer Ausnahme, und die Detailantwort verhält sich wie bisher.
- **AKs:** AK24 (Zulieferung), AK30 (Detail und Lauf zeigen dieselbe Zahl).
- **Tests:** `Infrastructure.Tests/Integration`: Die bestehenden Fälle in `WishServiceTests.Detail.cs` bleiben **unverändert**
  grün (das ist der Beweis des Umzugs). Neu ein Fall, den der K1-Review als ungetestet geparkt hat: dasselbe Emote unter zwei
  **gleichzeitig aktiven** Aliasen mit 10 und 20 Nutzungen je Tag ergibt 30 je Tag.
- **Fertig:** Tests grün; kein Diff an bestehenden Testerwartungen.
- **Commit:** `refactor: share the wish usage window read`

### T3: `WishExpiryService`

- **Ziel:** Ein Durchlauf nach Spec „K2“ und den Verträgen 2.1 bis 2.4, idempotent, ohne 7TV-Aufruf.
- **Dateien (neu, `src/EmotePurge.Infrastructure/Services/`):** `WishExpiryService.cs`, die Standard-Implementierung von
  `IWishBotAvailability` (Form: eine kleine Klasse „nie verfügbar“). **Geändert:** `ServiceCollectionExtensions.cs` (bei
  `:344-357`: Dienst scoped, Verfügbarkeit so registriert, dass K4 sie mit einer Zeile ersetzt), `docs/DECISIONS.md`.
- **Ablauf eines Durchlaufs:**
  1. *Abgleich:* Kanal-IDs nach Befund 17 lesen (ohne Tracking, nach ID sortiert), je Kanal
     `IWishReconcileService.ReconcileChannelAsync` rufen, nacheinander. Ein Fehler dort wird gezählt und geloggt (Kanal-ID,
     Ausnahmetyp, SQLSTATE) und bricht nichts ab. **[E]** Der Fälligkeitsschritt läuft für diesen Kanal trotzdem: Vorlegen ist im
     manuellen Modus eine Erinnerung, und vor jedem Bot-Entfernen liest K4 frisch; ein dauerhaft scheiternder Abgleich darf
     Fristen nicht anhalten. Der Dienst ruft den Abgleichsdienst direkt, nicht `IWishReconcileTrigger` (dessen Logtext spricht von
     einem Slot-Schreiben).
  2. *Zeitpunkt* lesen (2.1), *Kandidaten* lesen (nur IDs und Kanal-ID, sortiert nach Kanal, dann Wunsch-ID).
  3. *Je Kandidat* nach 2.3: sperren, lesen, erneut prüfen, zurückhalten oder entscheiden. Die globale Verfügbarkeit wird beim
     ersten Bedarf gefragt und für den Durchlauf gemerkt. Die Nutzung kommt aus dem Helfer von T2 und nur, wenn die Policy sie
     verlangt.
  4. *Je Kanal* mit mindestens einer Änderung ein `wish.changed`.
  5. Zusammenfassung zurückgeben. Der Dienst loggt selbst nur Fehler; die Summenzeile gehört dem Worker.
- **Grenzfälle:** `ExpiresAtUtc` exakt gleich „jetzt“ ist fällig. Wunsch in einem deaktivierten Kanal im manuellen Modus wird
  vorgelegt (`expired`). Kanal während des Laufs gelöscht oder zusammengeführt: Die Zeile fehlt oder trägt eine andere
  Kanal-ID → überspringen, der nächste Lauf findet sie. Einstellungszeile fehlt → Defaults (manuell, Schutz aus). Der Abgleich
  hat den Wunsch im selben Durchlauf nach `removed_externally` gesetzt → kein Kandidat mehr, kein `wish.due`. Ein Rückkehrer
  (`removed_externally` → `active` mit alter, schon abgelaufener Frist) wird im selben Durchlauf vorgelegt. Der Kontext des
  Dienstes darf vor dem Abgleich keine Wunsch-Zeilen tracken (der Abgleich arbeitet in eigenem Scope und schreibt dieselben
  Zeilen).
- **AKs:** AK24, AK25, AK26, AK27; die Datenbankseite von AK22 und AK23.
- **Tests:** `Infrastructure.Tests/Integration` (Sammlung „Postgres“, `HandWoundTimeProvider`, `IRedisPublisher` als Double wie in
  `WishServiceTests`; Bot-Modus, Eigentum und Schwelle direkt in die Zeilen geschrieben; Verfügbarkeit als Double). Je Zeile ein
  Fall, Namen als Verhalten:
  1. manuell und abgelaufen → `presented` (`expired`); `StatusChangedAtUtc` = Laufzeitpunkt; `ExpiresAtUtc`,
     `DecisionVersion`, `EndedAtUtc` unverändert; genau ein `wish.due` (Akteur System, Details genau `wishId`, `outcome`,
     `expiresAtUtc`); genau ein `wish.changed`.
  2. noch nicht abgelaufen → unberührt; exakt auf der Grenze → fällig.
  3. zweiter Durchlauf über denselben Stand → keine Zeile geändert (alle Spalten verglichen), kein Event, kein Audit (AK27).
  4. vorlegen, mit dem echten `WishService` verlängern, Uhr hinter den neuen Ablauf stellen, Durchlauf → zweiter `wish.due`
     mit dem neuen `expiresAtUtc` (AK27; zeigt zugleich, dass ein vorgelegter Wunsch verlängerbar bleibt, Befund 8).
  5. Zeile 6: Bot-Modus, Eigentum, verfügbar, Schutz aus → `active` bleibt, `RemovalQueuedAtUtc` = Laufzeitpunkt,
     `RemovalQueuedVersion` = `DecisionVersion`, Audit `queued`, ein Event; zweiter Durchlauf: beide Felder unverändert, kein
     Audit (AK27).
  6. eingereiht, dann `extend` aus K1 → Einreihung leer (E25); nach dem neuen Ablauf reiht der Lauf mit der neuen Version ein.
  7. AK25: Schutz an, Zählbeginn einen Tag nach Fensterbeginn, Nutzung über der Schwelle → `coverage_gap`; `DeactivatedAtUtc`
     gesetzt → `coverage_gap`; Schutz aus, sonst gleich → eingereiht.
  8. AK24: zwei Aliase mit 10 und 20 je Tag, Schwelle 30 → `protected`; Schwelle knapp darüber → eingereiht.
  9. AK23 an der Datenbank: Laufzeit 2 Tage, Summe durch 2 gegen die Schwelle; 23 Stunden im Set → `coverage_gap`.
  10. Zeile 2 vor Zeile 5: kein Eigentum bei Nutzung über der Schwelle → `not_bot_owned`.
  11. Zeile 3: Standard-Verfügbarkeit (nie) → `bot_unavailable`; verfügbar, aber `BotSuspendedReason` gesetzt →
      `bot_unavailable`.
  12. AK26: vorläufiger Start, Sync vor 1 Stunde → nichts geschrieben, Zähler „zurückgehalten“; Sync vor 25 Stunden →
      vorgelegt; `LastSyncedAtUtc` leer → vorgelegt; Sync frisch, Ablauf vor 25 Stunden → vorgelegt (Befund 3).
  13. Abgleich zuerst: `active`, abgelaufen, Slot archiviert, mit dem **echten** Abgleichsdienst → `removed_externally`, kein
      `wish.due`.
  14. Abgleich scheitert für Kanal A (Double wirft): Kanal B läuft normal, der fällige Wunsch in A wird trotzdem vorgelegt, die
      Zusammenfassung zählt einen gescheiterten Abgleich.
  15. Zwei Durchläufe gleichzeitig (zwei Kontexte) → genau ein `wish.due`. Dazu: Eine fremde Transaktion hält die Zeile und setzt
      den Wunsch auf `kept`; der Lauf wartet, liest danach und überspringt.
  16. Zwei fällige Wünsche in einem Kanal → ein Event; in zwei Kanälen → zwei.
  17. Nicht gewählt werden: `presented`, eingereihte, beendete Wünsche und `active` ohne Ablauf.
  18. Die Verfügbarkeit wird im rein manuellen Durchlauf nie gefragt und sonst höchstens einmal.
  Dazu in `Infrastructure.Tests/Unit/WishRegistrationTests`: Dienst und Verfügbarkeit lösen sich mit und ohne Schalter auf, die
  Standard-Verfügbarkeit sagt nein.
- **Fertig:** Tests grün; `dotnet test` für `Infrastructure.Tests` gesamt grün; DECISIONS ergänzt um: je Wunsch eine Transaktion
  mit Zeilensperre, keine Advisory- und keine Kanalsperre (Befund 12); Kanalauswahl für den Abgleich (Befund 17) und Weiterlaufen
  nach einem gescheiterten Abgleich; Details von `wish.due` (Befund 10) und der zweite Eintrag nach verworfener Einreihung
  (Befund 11); Nutzung nur bei Bedarf (Befund 16).
- **Commit:** `feat: add wish expiry run`

### T4: `WishExpiryWorker`

- **Ziel:** Der Takt. Keine Logik außer Schalter, Timer, Fehlerfang und Summenzeile.
- **Dateien:** neu `src/EmotePurge.Worker/WishExpiryWorker.cs` (konkrete Klasse, nur `AddHostedService`, Regel 5). Geändert:
  `WorkerServiceRegistration.cs` (Registrierung als **letzter** Hosted Service **[E]**, Kommentar „fourteen“ → „fifteen“),
  `docker-compose.yml`, `docker-compose.prod.yml` (Mapping `Wishes__ExpiryIntervalMinutes` aus
  `WISHES_EXPIRY_INTERVAL_MINUTES`, Default 15, **nur am Dienst `worker`** **[E]**: die Api liest den Wert nicht),
  `.env.example` (kommentierter Eintrag neben `WISHES_ENABLED`), `tests/EmotePurge.Worker.Tests/WorkerServiceRegistrationTests.cs`,
  neu `tests/EmotePurge.Worker.Tests/WishExpiryWorkerTests.cs`, `docs/DECISIONS.md`.
- **Verträge:** Abhängigkeiten: Logger, `IServiceScopeFactory`, `WishOptions`, `TimeProvider`; **kein** `BootRecoveryGate`
  (Befund 5), kein `AppDbContext` (Schichtentreue). Bei `Wishes:Enabled = false`: eine Informationszeile, Ende; kein Scope, kein
  Timer. Sonst: eine Minute warten, Timer mit `ExpiryIntervalMinutes` anlegen, erster Durchlauf, danach je Tick einer. Je
  Durchlauf ein eigener Scope, daraus `IWishExpiryService`. Summenzeile (Information, reine Zählwerte) nur, wenn etwas vorgelegt
  oder eingereiht wurde; eine Warnung, wenn die Zusammenfassung gescheiterte Abgleiche oder Wünsche nennt. Ein Fehler des
  Durchlaufs kostet einen Takt und wird als Warnung mit Ausnahmetyp und SQLSTATE geloggt, nie mit der Meldung (Muster
  `DataRetentionWorker.DescribeFailure`). Der Stopp beendet die Schleife ohne Fehlerzeile.
- **Grenzfälle:** Stopp während der ersten Minute; Durchlauf länger als der Takt (der Timer staut nicht, es läuft nie mehr als
  ein Durchlauf dieses Workers).
- **AKs:** AK28, AK29.
- **Tests:** `Worker.Tests`, container-frei mit `FakeTimeProvider` (Vorbild `SystemLoadRollupWorkerTests`,
  `DataRetentionWorkerLogExpiryTests`): Schalter aus → genau eine Logzeile, die Ausführung endet, die Scope-Fabrik wird nie
  benutzt, auch nicht nach einer vorgestellten Stunde (AK28); Schalter an → kein Durchlauf vor Ablauf der ersten Minute, einer
  danach, der nächste nach genau einem Takt (mit einem kleinen Testwert); ein werfender Durchlauf → Warnung ohne Meldungstext, der
  nächste Tick läuft; Zusammenfassung ohne Änderung → keine Zeile, mit Änderung → eine; mit Fehlzählern → Warnung.
  Registrierungstest: fünfzehn Hosted Services, `WishExpiryWorker` darunter, die beiden „erster ist die Log-Erfassung“-Fälle
  bleiben unverändert grün (AK29); der Worker löst sich aus dem echten Graphen auf.
- **Fertig:** Tests grün; DECISIONS ergänzt (Takt, kein Warten auf die Boot-Recovery, Variable nur am Worker, fünfzehn Dienste).
- **Commit:** `feat: add wish expiry worker`

### T5: Doku

- **Ziel:** Was ein Betreiber und die nächste Sitzung wissen müssen.
- **Dateien:** `docs/Operations.md` (englisch, Abschnitt „Wish emotes“: der Ablauf-Lauf in drei Sätzen, die Variable, dass der
  Lauf nie nach 7TV schreibt, der begrenzte Aufschub bei vorläufigem Start, RR1 als bekannte Grenze; dazu die Auswertung der
  Pilot-Messgröße (a) aus Spec 13 als Abfrage gegen das Audit-Log: Nenner `wish.due`, zusammengefasst je `wishId` und
  `expiresAtUtc`, Zähler die Folgeeinträge `wish.extend`, `wish.keep`, `wish.remove`, `wish.close` binnen 24 Stunden;
  `wish.botRemove` trägt K4 nach), `docs/Architectur.md` (`:86`: fünfzehn, `WishExpiryWorker` in der Liste), `CLAUDE.md`
  (deutsch: Worker-Liste „fünfzehn“ mit einem Satz zum neuen Dienst, Zeile „Wunsch-Emotes“ im Umsetzungsstand um K2 ergänzt,
  Branch-Name aktuell), `docs/Feature-Ideen-2026-08-01.md` (Statuszeile A18 oben in der Tabelle: K2 dazu), `docs/DECISIONS.md`
  (`**Betrifft:**`-Zeile vollständig, auch die Testdateien).
- **AKs:** AK29 (Doku-Teil), Vorbereitung AK31.
- **Tests:** keine. `grep` nach „fourteen“ und „vierzehn“ außerhalb von `docs/plans`, `docs/superpowers`, `docs/nachtlaeufe` und
  alten DECISIONS-Einträgen findet nichts mehr.
- **Fertig:** Die Abfrage aus Operations ist einmal gegen eine lokale Datenbank mit Testdaten gelaufen (in T6 gegenlesen).
- **Commit:** `docs: document the wish expiry run`

## 4. AK-Abdeckung

| AK | Task | AK | Task |
|---|---|---|---|
| AK22 | T1 (Policy, Reihenfolge), T3 (Fälle 10, 11) | AK27 | T3 (Fälle 3 bis 6, 15) |
| AK23 | T1 (Rechnung), T3 (Fall 9) | AK28 | T4 |
| AK24 | T2 (Summe), T3 (Fall 8) | AK29 | T4, T5 |
| AK25 | T1 (Regel), T3 (Fall 7) | AK30 | T6 |
| AK26 | T1 (Regel), T3 (Fall 12) | AK31 | Pilot nach Freigabe 1, kein Merge-Tor; T5 liefert die Auswertung |

## 5. Live-Verifikation (T6; Regel 16, AK30)

**Randbedingungen.** Kein `ssh`, kein Prod. Nie `docker compose` aus dem Worktree; die geteilte Dev-Datenbank wird nur gelesen
(Dump). Ports (`:5151`, Redis) vorher mit anderen laufenden Sitzungen abstimmen (Orchestrator). **K2 braucht keinen
7TV-Schreibzugriff:** `adopt` übernimmt einen Eintrag, der schon im Set liegt. Alle Zahlen sind Dev-Zahlen.

| Ressource | Wert |
|---|---|
| Datenbank | Kopie der Dev-Datenbank unter eigenem Namen im Dev-Postgres-Container; Zugangsdaten aus der Container-Umgebung, nie ausgeben. Auf der Kopie muss `AddWishes` angewendet sein (`dotnet ef migrations list` zeigt nichts Ausstehendes; K2 bringt keine Migration mit) |
| Redis | eigener Container per `docker run` auf abgestimmtem Port |
| Api, Worker | `dotnet run` mit absolutem Pfad aus dem Worktree; `Wishes__Enabled=true`, am Worker `Wishes__ExpiryIntervalMinutes=1`, `SevenTv__EventApi__Enabled=false` |
| Sitzung | Session-Cookie des Testkontos des Betreibers in einer Scratchpad-Datei (Modus 600). **Nachts nur, wenn ein gültiges Cookie schon vorliegt**; ein Browser-Login braucht den Betreiber |

**Stufe A, nachts ohne Cookie möglich (Vorprobe, ersetzt AK30 nicht):**

1. Worker mit `Wishes__Enabled=false` starten: genau eine Zeile des Ablauf-Laufs im Log, danach keine weitere über fünf
   Minuten; alle anderen Dienste laufen. Dann mit `true` neu starten.
2. Auf der Kopie einen `active`-Wunsch mit abgelaufener Frist und maßgeblichem Start von Hand anlegen (als Handgriff an der
   Kopie im Protokoll markieren). Spätestens zwei Minuten nach dem Start (eine Minute Anlauf, ein Takt): `presented` mit Grund
   `expired`; im Audit-Log genau ein `wish.due` mit Akteur „system“ und den drei Detailfeldern; im Worker-Log genau eine
   Summenzeile. Drei weitere Takte abwarten: kein zweiter Eintrag, keine weitere Summenzeile (AK27 live).
3. Die Abfrage aus `docs/Operations.md` (T5) gegen die Kopie laufen lassen und das Ergebnis gegen die Tabellen lesen.

**Stufe B, mit Cookie (das eigentliche AK30):**

4. Einen Bestandseintrag im Set des Testkontos wählen, der seit mehr als 8 Tagen im Set liegt und dessen Slot ein
   REST-bestätigtes Datum trägt (an der Kopie ablesen; sonst greift der Aufschub aus Befund 3 und der Schritt dauert bis zu
   24 Stunden). Kanal-Stream (`/api/channels/{kanal}/live`) und `/api/channels/live-events` mitschneiden.
5. `POST …/wishes/adopt` mit `startsAtUtc` vor 8 Tagen und `durationDays = 7` → 201 `active`, `expiresAtUtc` vor einem Tag,
   `startsAtAuthoritative` wahr. Nach höchstens einem Takt: `GET …/wishes/{id}` zeigt `presented`, `statusReason` `expired`;
   auf dem Kanal-Stream genau ein `wish.changed` aus dem Lauf (zusätzlich zu dem des `adopt`), auf dem Übersichts-Stream keines;
   `GET /api/wishes/summary` zählt den Wunsch; Audit `wish.due` wie in Schritt 2.
6. `extend` → `active` mit Ablauf jetzt plus Kanal-Default (E12), `removalQueuedAtUtc` leer; über zwei Takte bleibt der Wunsch
   `active`, kein neuer `wish.due`.
7. **Schutz (zweiter Teil von AK30):** Ein zweites Emote wählen, das in den letzten 7 Tagen gezählte Nutzung hat, in einem
   Kanal, dessen Zählbeginn vor dem Fenster liegt und der aktiv ist (an der Kopie ablesen). In diesem Kanal
   `PUT …/wishes/settings` mit Schwelle 1, dann `adopt` wie in Schritt 5. Erwartet: weiterhin `presented` (`expired`), weil der Kanal manuell läuft; die
   Detailantwort zeigt `perDayMean` > 0 und `coverageGap = false`. Hat der Kanal des Testkontos keine solche Nutzung auf der
   Kopie, den Schritt in einem anderen aktiven Kanal der Kopie fahren, sofern die Sitzung dort lokal verwalten darf; sonst als
   „morgens, braucht eine Chatnachricht im Kanal des Testkontos bei laufendem Worker“ markieren.
8. **Echte Frist statt zurückdatierter:** `adopt` eines dritten Eintrags (seit mehr als einem Tag im Set, Datum
   REST-bestätigt) mit `durationDays = 1` und `startsAtUtc` vor 23 Stunden 50 Minuten. Der Wunsch ist zuerst `active` und nicht fällig; rund zehn Minuten später, spätestens einen Takt nach
   `expiresAtUtc`, steht er auf `presented`. Zeitpunkte von Ablauf und `wish.due` notieren (Abstand höchstens ein Takt).
9. **Aufräumen:** Prozesse per `ps -o args= -p <pid>` prüfen, dann SIGTERM; Redis-Container und Datenbankkopie entfernen. Das
   7TV-Set wurde nicht angefasst; das im Protokoll ausdrücklich festhalten. Protokoll nach
   `docs/nachtlaeufe/live-484-<Datum>.md`, ohne Kanal- und Kontonamen, eigener `docs:`-Commit.

**Live nicht herstellbar, nur durch Tests belegt (im Protokoll so benennen):** die Zeilen 2 bis 6 der Tabelle (vor K4 gibt es
weder einen Weg, den Bot-Modus einzuschalten, noch einen verfügbaren Bot); der Aufschub bei vorläufigem Start (das vorläufige
Datum hält live nur bis zum nächsten Vollsync, rund eine Minute); zwei gleichzeitige Läufe; AK31 (Pilot nach Freigabe 1).

## 6. Gates vor dem PR (T7), Migration, Deploy

**Gates.**
1. `dotnet build EmotePurge.slnx --no-incremental` ohne neue Warnungen; `dotnet format EmotePurge.slnx --verify-no-changes`.
2. `dotnet test EmotePurge.slnx` grün (Docker läuft, Testcontainers).
3. Frontend-Suiten und E2E sind nicht nötig **[E]**: K2 ändert keine Datei unter `web/`, keinen Fehlercode und keine Route.
4. `node scripts/coverage-local.mjs --backend-only` (nur Committetes zählt; Schwelle 80 % auf neuem Code; die drei
   Interface- und Konstanten-Dateien ohne ausführbaren Code sind kein Befund, wie im K1-Lauf).
5. Live-Verifikation: Stufe A bestanden und protokolliert; Stufe B bestanden oder mit Grund in der Morgenliste.
6. **Nicht im Nachtlauf, gehört dem Orchestrator und dem Betreiber:** Rebase auf `main` nach dem Merge von K1 (falls noch nicht
   geschehen), `/codex:review --model gpt-6.1-sol` einmal über den fertigen Branch und das Ergebnis unverändert vorlegen,
   adversariale Prüfung dieses Plans vor dem Lauf, Push, PR, Merge.
7. PR-Text (englisch) nennt: keine Migration; die Abweichungen von der Spec (begrenzter Aufschub, `expiresAtUtc` im Audit,
   Worker-Tests); dass die Zeilen 2 bis 6 bis K4 nur getestet, nicht live erreichbar sind; den neuen Audit-Typ für K3; die
   Übergabepunkte an K4 aus 1.2.

**Migration: keine.** K2 ändert kein Entity und keinen Index; die Spalten (`RemovalQueuedAtUtc`, `RemovalQueuedVersion`,
`StatusReason`) und der Index `IX_Wishes_Status_ExpiresAtUtc` stammen aus `AddWishes`. Der Fixture-Test auf ausstehende
Modelländerungen bleibt grün; schlägt er an, hat ein Task versehentlich das Modell berührt, und das ist ein Fehler des Tasks.

**Deploy.** Api und Worker (ein Build, gemeinsame Infrastructure). Voraussetzung ist `AddWishes` aus K1, von Hand vor den Images;
den Deploy-Hinweis gegen **alle** ausstehenden Migrationen prüfen. In Portainer ist nichts zu setzen: `WISHES_ENABLED` bleibt auf
Prod aus bis Freigabe 1, der neue Dienst schreibt dann eine Zeile und endet. `WISHES_EXPIRY_INTERVAL_MINUTES` hat im Mapping
den Default 15. Rollback: Schalter aus genügt; ein älteres Image ignoriert die neue Variable.

## 7. Risiken und offene Punkte

| # | Risiko | Umgang |
|---|---|---|
| R1 | Worker-Ausfälle im Fenster bleiben unerkannt; ein beliebtes Emote kann trotz Schutz eingereiht werden (RR1 der Spec) | [B], akzeptiert; wirkt erst ab K4; Operations nennt es |
| R2 | Der begrenzte Aufschub (Befund 3) weicht vom Wortlaut der Spec ab; ein Wunsch auf einem undatierten Slot wird bis zu 24 Stunden später vorgelegt als sein Ablauf | [E], in DECISIONS; ohne die Grenze liefe er nie ab |
| R3 | Zweiter `wish.due` zur selben Fälligkeit nach verworfener Einreihung (Befund 11) verfälscht den Nenner der Pilot-Messung | `expiresAtUtc` im Audit, Abfrage fasst zusammen; erst ab K4 möglich, K4 liest diesen Punkt |
| R4 | Der Abgleich je Takt für jeden Kanal mit Wünschen verlängert den Lauf | eine Vorprüf-Abfrage je Kanal, nacheinander; höchstens 80 aktive Kanäle; Fehler isoliert |
| R5 | `wish.due` erscheint im Aktivitäts-Feed unübersetzt, bis K3 es übersetzt | nur lokal sichtbar (Schalter auf Prod aus); im PR benannt |
| R6 | Schutz und Lücke sind vor K4 in Produktion ohne Wirkung; ein Fehler in den Zeilen 2 bis 6 fiele erst mit K4 live auf | vollständige Tabellen- und Datenbanktests (T1, T3); Sol-Review mit Blick auf Reihenfolge und Fensterrechnung; K4 wiederholt die Live-Probe im Bot-Modus |
| R7 | Kein Limit je Durchlauf: sehr viele gleichzeitig fällige Wünsche (etwa nach einem langen Worker-Ausfall) laufen in einem Takt | je Wunsch eine kurze Transaktion; bei Pilotgröße unkritisch; kein Limit gebaut **[E]** |
| R8 | Einstellungen ändern sich zwischen Lesen und Schreiben eines Wunsches (ab K4: Bot-Modus wird abgeschaltet) | gelesen hinter der Zeilensperre des Wunsches; den Rest fängt die Prüfung des Bots unter Sperre (E25) |
| R9 | `coverage-local` ist bei der chirurgischen Änderung in `WishService.cs` unscharf | der Umzug (T2) verschiebt gedeckte Zeilen in eine neue, durch die Detail-Tests gedeckte Datei |
| R10 | Die Rückkehr eines extern entfernten Wunsches kurz vor Ablauf verkürzt die echte Zeit im Set, ohne dass das Fenster es zeigt | Lesart L1 in Abschnitt 9; wirkt nur im Bot-Modus mit Schutz |

**Für den Sol-Review dieses Branches (Fokus):** Reihenfolge der sechs Zeilen und Trennung von Zeile 1 und 3; Idempotenz unter
zwei gleichzeitigen Läufen und gegen Befehle aus K1; die Zurückhalte-Regel an ihren beiden 24-Stunden-Grenzen; dass der Lauf nie
`ExpiresAtUtc` oder `DecisionVersion` schreibt; dass kein Log und kein Audit-Eintrag Alias, Namen oder Einlöser trägt.

## 8. Selbstprüfung des Plans

- Jeder Punkt aus Spec „K2“ hat einen Task: Takt und Schalter (T4), vier Schritte des Durchlaufs (T3), vorläufiger Start (T1,
  T3), bedingtes Schreiben und Idempotenz (T3), `wish.due` (T1, T3), Entscheidungstabelle (T1), `IWishBotAvailability` (T1, T3),
  kurze Laufzeiten ohne eigene Logik (T1, T3 Fall 9), Dateiliste samt Compose und `CLAUDE.md` (T4, T5). AK22 bis AK31 sind
  zugeordnet (Abschnitt 4).
- Namen sind über die Tasks gleich: `WishExpiryPolicy`, `IWishExpiryService.RunAsync`, `WishExpiryService`, `WishExpiryWorker`,
  `IWishBotAvailability`, `WishOptions.ExpiryIntervalMinutes`, `wish.due`, `WishUsageQueries`.
- Lagen, die die Spec nicht ausbuchstabiert und die ein Test festhält: ausgesetzter Bot-Kanal (T1, T3 Fall 11), ewig vorläufiger
  Start (T1, T3 Fall 12), nie gesyncter Kanal (T1), zwei gleichzeitige Läufe (T3 Fall 15), gescheiterter Abgleich (T3 Fall 14),
  Abgleich entfernt den Kandidaten (T3 Fall 13), erneutes Einreihen nach `extend` (T3 Fall 6), Worker mit Fehler im Durchlauf (T4).
- Kein Task braucht eine Rückfrage; was Betreiber oder Orchestrator braucht, steht in Abschnitt 6 Punkt 6 und in Abschnitt 9.

## 9. Betreiberfragen vor dem Lauf und Lesarten

**Blockierende Betreiberfragen: keine.** Alles Übrige ist als **[E]** entschieden und widerrufbar; der Lauf kann starten.

**Vor dem Lauf zu erledigen (Orchestrator, keine Entscheidung):** Der Worktree steht auf einem Stand, der die K1-Commits enthält
(`git log` zeigt `ec8d9697` oder dessen Entsprechung auf `main` nach dem Merge). Ein Rebase mitten im Lauf findet nicht statt.

**Lesarten zur Bestätigung beim Review des Plans (blockieren nichts):**

- **L1, „tatsächliche Zeit im Set“ bei Rückkehrern.** Der Betreiber hat das Fenster als min(7 Tage, tatsächliche Zeit im Set)
  gefasst; Spec E4 und der K1-Code (`WishUsageWindow.WindowStart`) rechnen ab dem **Start des Wunsches**. Beides ist gleich,
  außer bei einem Wunsch, der extern entfernt und binnen 6 Stunden wieder übernommen wurde: Er behält Start und Frist, war aber
  bis zu 6 Stunden nicht im Set. Der Plan bleibt bei Spec und Code (Fenster ab Start; die Nutzung früherer Aufenthalte zählt mit,
  die Abwesenheit senkt das Mittel geringfügig). Die Alternative wäre, das Fenster frühestens bei der aktuellen Slot-Generation
  (`SlotAddedAtUtc`) beginnen zu lassen: Dann gälte eine Rückkehr weniger als 24 Stunden vor Ablauf als Lücke (vorlegen statt
  entfernen), die Nutzung des früheren Aufenthalts fiele aber aus dem Mittel. Wirkung nur im Bot-Modus mit Schutz, also ab K4;
  ein Widerspruch kostet eine Zeile in der Fensterfunktion und zwei Tests.
- **L2, begrenzter Aufschub (Befund 3).** Die Spec kennt nur „Sync älter als 24 Stunden“. Der Plan ergänzt „oder Ablauf älter
  als 24 Stunden“. Wer den Wortlaut der Spec will, streicht die zweite Bedingung in der Policy und nimmt in Kauf, dass ein Wunsch
  auf einem undatierten Eintrag in einem gesunden Kanal nie abläuft.
