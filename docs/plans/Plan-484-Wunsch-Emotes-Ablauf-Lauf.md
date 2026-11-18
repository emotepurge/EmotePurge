# Plan #484: Wunsch-Emotes, Kind K2: Ablauf-Lauf im Worker

Erstellt am 2026-11-18 im Worktree `EmotePurge-wt-484` (Branch `feat/484-wish-expiry`, Basis `origin/main` @ `ab4882ea`, K1 ist
mit PR #492 gemergt; die Codestellen dieses Plans liegen alle in K1-Dateien und sind am Stand `ec8d9697` gelesen, den der Merge
unverändert enthält).
**Status: Revision 2 (2026-11-18). Betreiber hat L1 und L2 entschieden; Sol adversarial zweimal durch (1 × P1 und 3 × P2, in der
Nachprüfung 2 × P2, alle am Code geprüft und eingearbeitet). Keine offene Betreiberfrage. Eine weitere Sol-Runde über den Plan
ist nicht vorgesehen; die Code-Prüfung folgt vor dem Merge.**
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
**[B]** Betreiberentscheidung (Spec Abschnitt 17, Runden 1 bis 3, und 2026-11-18) · **Sol-P1/P2-*n*** Befund des adversarialen
Reviews (gpt-6.1-sol), am Code geprüft und eingearbeitet · „Spec E4“ meint die Festlegung der Spec,
„AK22“ das Akzeptanzkriterium aus K2, „K1 (j)“ den Absatz im DECISIONS-Eintrag von K1.

**Betreiberfestlegungen, die K2 trägt [B]:** eigener Worker im 15-Minuten-Takt; Laufzeit 1 bis 365 Tage; Bewertungsfenster des
Schutzes = min(7 Tage, Zeit im Set), unter einem vollen Tag gilt wie eine Lücke; Lückenregel nur bei eingeschaltetem Schutz;
Schwelle als Mittel je Tag über alle Slots des Emotes, ohne Wert ist der Schutz aus; ein schon fälliger oder eingereihter Wunsch wird
nicht erneut fällig gestellt; vorläufige Starts werden bei gesundem Sync nicht fällig; je Fälligkeit ein Audit-Eintrag `wish.due`;
im manuellen Modus erinnert K2 nur, der Bot-Teil ist K4.

### Revision 1 (2026-11-18): was sich geändert hat

| Änderung | Anlass |
|---|---|
| Eine Änderung der Schutzschwelle sperrt die aktiven Wünsche des Kanals (Kanal vor Wunsch), erhöht ihre `DecisionVersion` und leert ihre Einreihung im selben Commit; neuer Task T2b, Vertrag 2.6, zwei Sperr-Reihenfolgen als Tests (Befund 20, T2b, T3 Fälle 20, 21) | Sol-P1-1 |
| Genau ein `wish.due` je Ablauf: Neubewertung nach verworfener Einreihung bleibt, der Lauf prüft unter der Wunsch-Sperre, ob für diesen Ablauf schon ein Eintrag steht (Befund 11, 2.3, T3 Fall 19) | Sol-P2-2 |
| Vorläufiger Start: Aufschub höchstens 24 Stunden, danach fällig, aber **nie** eingereiht; neue Zeile 4 der Tabelle mit neuem Grund `unconfirmed_start`, die Tabelle hat sieben Zeilen; Spec (K2-Text, Tabelle, AK22, AK25, AK26, Gründe in Abschnitt 5) angeglichen (Befund 3, 21, 2.1, 2.2, T1, T3 Fall 12) | [B] L2, Sol-P2-3 |
| Pilot-Messgröße (a) rechnet ab dem im Audit festgehaltenen `expiresAtUtc`: Kohorte, Reife, verspätete Feststellung, Doppelte; Spec 13 angeglichen; Prüffälle in T5 (Befund 10, T5) | Sol-P2-4 |
| L1 (Fenster ab Start des Wunsches) ist entschieden und steht als [B] in Abschnitt 9 | [B] L1 |

### Revision 2 (2026-11-18): Nachprüfung durch Sol, zwei neue P2 (R2-1, R2-2)

| Änderung | Anlass |
|---|---|
| `unconfirmed_start` steht in der Tabelle **vor** `bot_unavailable` (Zeile 3 statt 4): Sonst bekäme ein vorläufiger Wunsch, der während eines Bot-Ausfalls fällig wird, einen Grund, der „Bot entfernen lassen“ erlaubt. Dazu als Vertrag für K4: Anfordern **und** Ausführen einer Bot-Entfernung verlangen `StartsAtAuthoritative = true`; Spec (Tabelle, Abschnitt 8, K4-Route) angeglichen (Befund 21, 2.2, 1.2, T1, T3 Fälle 11, 12, 22) | Sol-R2-1 |
| Späte Entscheidungen bleiben im Nenner: `extend`, `keep`, `close` und `complete-removal` halten bei einem überfälligen Wunsch den bisherigen Ablauf als `previousExpiresAtUtc` in ihrem eigenen Audit-Eintrag fest; neuer Task T2c, Kohorte in T5 nimmt diese Einträge auf, zwei weitere Prüffälle; Spec (K1-Audit, Abschnitt 13) angeglichen (Befund 22, 2.7, T2c, T5) | Sol-R2-2 |

---

## 0. Befund vor dem Plan

Alle Stellen sind am Stand `ec8d9697` nachgelesen.

1. **Zeile 1 der Entscheidungstabelle gegen `IsBotMode` aus K1.** `WishService.EffectiveSettings.IsBotMode`
   (`WishService.cs:958`) ist `BotModeEnabled && BotSuspendedReason is null`; ein ausgesetzter Bot-Kanal gilt dort als manuell
   (so steht er auch als `mode` im DTO). Nähme K2 diesen Wert für Zeile 1, ergäbe ein ausgesetzter Kanal `expired`, und Zeile 4
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
   (`WishReconcileService.cs:304-308`, Kommentar „the next dated sync decides“); liefert 7TV nie eines, bleibt es dabei. Nach dem
   ursprünglichen Wortlaut der Spec liefe ein solcher Wunsch in einem gesunden Kanal **nie** ab.
   **[B] L2, präzisiert nach Sol-P2-3:** Zwei Regeln, die zusammen gelten. (a) *Kein Wunsch hängt dauerhaft:* Ein vorläufiger
   Start hält die Fälligkeit nur zurück, solange der Sync frisch ist **und** der Ablauf weniger als 24 Stunden zurückliegt;
   danach wird der Wunsch fällig. (b) *Ein unbestätigter Start führt nie zu einer Bot-Entfernung:* Ein fälliger Wunsch mit
   vorläufigem Start wird immer vorgelegt, nie eingereiht, auch bei altem Sync und bei ausgeschaltetem Schutz. Im manuellen
   Modus ist das die normale Erinnerung (`expired`); im Bot-Modus mit Eigentum, ob der Bot verfügbar ist oder nicht (Sol-R2-1), der neue Grund
   `unconfirmed_start` (Befund 21). Der Normalfall (EventAPI-Datum, das der nächste Vollsync nach rund einer Minute bestätigt)
   ist davon unberührt. Die Spec ist in diesem Commit angeglichen (K2-Text, Tabelle, AK26).
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
10. **Audit-Details von `wish.due` und die Pilot-Messung (Sol-P2-4).** Die Spec nannte `wishId` und `outcome` und rechnete die
    24 Stunden der Messgröße (a) ab dem Zeitpunkt des Eintrags, obwohl das Ziel „binnen 24 Stunden nach `ExpiresAtUtc`“ heißt.
    Der Zeitpunkt des Eintrags ist der Zeitpunkt der Feststellung (bis 15 Minuten später, nach einem Worker-Ausfall oder dem
    Aufschub aus Befund 3 auch deutlich mehr): Eine Entscheidung eine Stunde nach einem 48-Stunden-Ausfall zählte als Erfolg,
    obwohl sie 49 Stunden nach dem Ablauf fiel. **[E]** Details sind `wishId`, `outcome` und `expiresAtUtc` (der Ablauf, auf den
    sich der Eintrag bezieht, als UTC-Zeitpunkt in voller Genauigkeit). Die Messung ankert am festgehaltenen `expiresAtUtc`:
    Kohorte, Reife und Frist stehen in T5 und in Spec 13 (in diesem Commit angeglichen). Kein Personenbezug, keine Namen (AK19
    bleibt gewahrt). Der Eintrag trägt den Kanalnamen wie die K1-Einträge und erscheint damit im Aktivitäts-Feed des Kanals
    unter „system“; die Übersetzung `audit.actions.wish.due` liefert K3 (Risiko R5).
11. **Neubewertung desselben Ablaufs, aber genau ein `wish.due` (Sol-P2-2).** Verwirft etwas anderes als eine Verlängerung die
    Einreihung, ist der Wunsch wieder `active`, abgelaufen und nicht eingereiht. Das ist schon mit K1-Code erreichbar, sobald
    etwas einreiht: `WishReconcileService.EndOwnership` (`:510-522`) beendet beim Umbenennen das Eigentum, erhöht
    `DecisionVersion` und leert die Einreihung, Zustand und Ablauf bleiben; ebenso die Schwellenänderung aus Befund 20 und ab K4
    der Bot, der sich unter der Sperre nicht mehr berechtigt findet. Der nächste Lauf **muss** neu bewerten (sonst bliebe der
    Wunsch unbemerkt hängen), typisch mit `not_bot_owned`. AK27 verlangt aber genau einen Eintrag je Fälligkeit, und andere
    Auswertungen als die eine Abfrage in Operations verlassen sich darauf. **[E]** Der Lauf prüft unter der Wunsch-Sperre, ob
    für diesen Wunsch schon ein `wish.due` mit demselben `expiresAtUtc` steht; wenn ja, schreibt er Zustand bzw. Einreihung und
    das Event, aber **keinen** zweiten Audit-Eintrag. Gesucht wird im Audit-Log nach Aktion, Ziel und Zeitpunkt nicht vor dem
    Ablauf (der Index auf `OccurredAtUtc` begrenzt die Suche; ein Eintrag zu diesem Ablauf kann nicht älter sein als der Ablauf).
    Kein neues Feld am Wunsch, keine Migration. Erst die Verlängerung ändert `ExpiresAtUtc` und macht den nächsten Eintrag möglich.
12. **Keine Advisory-Sperre.** E6 braucht die Retention-Sperre nicht, und Idempotenz hängt hier nicht an „nur ein Lauf“,
    sondern an der Zeilensperre: **[E]** Der Lauf bearbeitet jeden fälligen Wunsch in einer eigenen kurzen Transaktion, sperrt die
    Zeile (`RowLocks.LockWishesByIdsAsync`), liest sie erst danach und prüft die Auswahlbedingung erneut. Zwei gleichzeitige Läufe
    (zwei Worker-Container während eines Stack-Updates) ergeben damit genau einen Übergang und einen Audit-Eintrag. Die Kanalzeile
    sperrt der Lauf nicht: Er fügt nichts ein und schreibt keine `SlotId` (K1, Plan 2.3); er hält nie mehr als eine Wunsch-Zeile
    und fordert danach keine weitere Sperre an, ein Zyklus mit Abgleich, Zusammenführung oder Purge ist ausgeschlossen.
13. **`IWishBotAvailability` ist nur die globale Hälfte.** Zeile 4 hat zwei Quellen: global (Token, Notaus, Schalter, ab K4) und
    je Kanal (`BotSuspendedReason` aus den Einstellungen, schon in K1 angelegt). **[E]** Die Schnittstelle beantwortet nur
    „ist der Bot global verfügbar“; die Standard-Implementierung sagt immer nein. Der Lauf fragt sie höchstens einmal je Durchlauf
    und nur, wenn ein fälliger Wunsch die Zeilen 1 bis 3 passiert hat. K4 ersetzt die Registrierung.
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
    verfügbar, Start maßgeblich, Schwelle gesetzt). Die Zeilen 2 bis 7 sind vor K4 nur über Tests erreichbar, die Einstellungen und Eigentum direkt
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
20. **Schutz-Einstellungen sind nicht gegen den Lauf serialisiert (Sol-P1-1).** `WishService.UpdateSettingsAsync`
    (`WishService.cs:45-98`) sperrt nur die Kanalzeile, fasst keinen Wunsch an, erhöht keine `DecisionVersion` und leert keine
    Einreihung. Der Lauf liest die Einstellungen hinter der Wunsch-Sperre, hält aber keine Kanalsperre. Schaltet ein Verwalter
    den Schutz ein, nachdem der Lauf die Einstellung gelesen hat, reiht der Lauf nach dem alten Stand ein; die Prüfung des Bots
    vor dem Entfernen (Version, Zustand, Ablauf, Eigentum; Spec 8) bewertet den Schutz nicht neu und ließe die Entfernung durch.
    Dasselbe gilt für eine Einreihung, die schon steht, wenn der Schutz danach eingeschaltet oder die Schwelle gesenkt wird.
    **[E]** Eine Änderung der Schwelle ist eine Entscheidung im Sinn von E25: `UpdateSettingsAsync` sperrt nach der Kanalzeile
    die aktiven Wünsche des Kanals, erhöht ihre `DecisionVersion` und leert ihre Einreihung im selben Commit wie die neue
    Einstellung (Vertrag 2.6, Task T2b). Der Lauf selbst lässt `DecisionVersion` weiterhin unverändert (Befund 9). Die
    Sperrreihenfolge „Kanal vor Wunsch“ ist die aus K1 (Plan-483, 2.3); der Lauf hält nur eine Wunsch-Zeile und nie die
    Kanalzeile, ein Zyklus ist ausgeschlossen. Für K4 gilt dasselbe beim Ein- und Abschalten des Bot-Modus (dort schon so
    vorgesehen, Spec K4 „Entscheidungen verwerfen die Einreihung“).
21. **Neuer Vorlagegrund `unconfirmed_start`.** Folgt aus Befund 3 (b). Ein neuer Wert in `WishStatusReason` und in dessen Liste
    `Presented`; er ist Vertrag (gespeichert, im DTO als `statusReason`). Der vorhandene Grund `unconfirmed` meint etwas anderes
    (Bot hat geschrieben, Bestätigung fehlt) und hängt an `recheck` und `adopt` (`WishTransitions.cs:90-93`); der neue Grund
    kommt in keine dieser beiden Listen. Folgen: K3 braucht eine Übersetzung für den Grund (Karte und Filter) und einen Satz, was
    zu tun ist (verlängern, behalten oder von Hand entfernen); K4 lässt „Bot entfernen lassen“ für diesen Grund **nicht** zu
    (die Liste der Spec, `protected`, `coverage_gap`, `bot_unavailable`, bleibt, wie sie ist). **Sol-R2-1:** Die Liste allein
    schützt nicht. K1 kann Eigentum behalten und zugleich das vorläufige Merkmal eines Slots übernehmen
    (`WishReconcileService.cs:377-384`, `:502-506`); stünde der Bot-Ausfall in der Tabelle vor dem vorläufigen Start, bekäme
    ein solcher Wunsch `bot_unavailable` und wäre nach der Rückkehr des Bots über `bot-remove` entfernbar. Deshalb zwei
    Sicherungen: die Reihenfolge in 2.2 (Zeile 3 vor Zeile 4) und der Vertrag für K4, dass Anfordern und Ausführen einer
    Bot-Entfernung einen maßgeblichen Start verlangen (in der Spec nachgetragen; gebaut wird das in K4). T1 liest `WishTransitionsTests` und
    nimmt den neuen Wert dort auf, wo die Matrix Gründe aufzählt; erwartet ist dasselbe Verhalten wie für `expired`.

22. **Späte Entscheidungen verschwanden aus dem Nenner (Sol-R2-2).** Entscheidet ein Verwalter einen überfälligen Wunsch, bevor
    der Lauf ihn gesehen hat (Worker-Ausfall, oder der Aufschub aus Befund 3), gibt es kein `wish.due`. `ExtendAsync`
    überschreibt den Ablauf (`WishService.cs:409-426`), und das Audit der Befehle trägt nur `wishId`, `sevenTvEmoteId` und
    `durationDays` (`AuditAndCommitAsync`, `:820-838`). Eine Verlängerung 30 Stunden nach dem Ablauf wäre damit weder im Nenner
    noch als verspätet sichtbar, und die Messgröße könnte Erfolg melden, wo Fristen gerissen wurden. Die Lücke ist nicht auf
    einen Takt begrenzt, wie Revision 1 annahm. **[E]** Die vier Befehle, die einen laufenden Wunsch entscheiden (`extend`,
    `keep`, `close`, `complete-removal`), schreiben `previousExpiresAtUtc` in die Details ihres eigenen Audit-Eintrags, wenn der
    Wunsch beim Befehl überfällig war (Vertrag 2.7, Task T2c). Im selben Commit wie der Befehl, kein neues Feld am Wunsch, keine
    Migration. Die Kohorte in T5 ist die Vereinigung aus `wish.due` und diesen Einträgen.

## 1. Umfang

### 1.1 Drin

Spec „K2“: reine Entscheidung (`WishExpiryPolicy`), Dienst (`IWishExpiryService`, `WishExpiryService`), Worker (`WishExpiryWorker`),
Schnittstelle `IWishBotAvailability` mit Standard „nie verfügbar“, Option `Wishes:ExpiryIntervalMinutes`, Audit `wish.due`,
gemeinsame Nutzungsabfrage (Befund 2), Schwellenänderung als Entscheidung (Befund 20), bisheriger Ablauf im Audit später
Entscheidungen (Befund 22), Compose-Mapping, Doku, Live-Verifikation AK30.

### 1.2 Was K2 den anderen Kindern liefert

| Für | Vertrag aus K2 |
|---|---|
| K3 | Ein abgelaufener Wunsch steht spätestens einen Takt nach `ExpiresAtUtc` auf `presented` mit Grund `expired`, zählt damit in `summary` und `mine`, und der Kanal-Stream bekommt `wish.changed` (Live-Probe AK45). Neuer Vorlagegrund `unconfirmed_start` (Befund 21, braucht eine Übersetzung; vor K4 nicht erreichbar). Neuer Audit-Typ `wish.due` im Aktivitäts-Feed, Akteur „system“, Details `wishId`, `outcome`, `expiresAtUtc`; K3 übersetzt ihn |
| K4 | `IWishBotAvailability` zum Ersetzen (eine Registrierung); Zeile 7 schreibt `RemovalQueuedAtUtc` und `RemovalQueuedVersion = DecisionVersion` und lässt `active`; die abgeleitete Warteschlange von K4 liest genau das. K2 stößt den Bot nicht an (kein `WISHBOT:`), der 30-Sekunden-Takt von K4 genügt. Fällt der Bot nach dem Einreihen aus, legt K4 selbst vor (Spec 7, Punkt 8); leert K4 eine Einreihung ohne Zustandswechsel, bewertet der nächste Lauf neu, ohne zweiten `wish.due` (Befund 11). Die Policy trennt `BotModeEnabled` von `BotSuspendedReason` (Befund 1). Eine Schwellenänderung verwirft Einreihungen aktiver Wünsche (Befund 20); K4 wendet dasselbe Muster auf das Schalten des Bot-Modus an und entscheidet, ob es auch für vorgelegte Wünsche mit Bot-Auftrag gelten soll. `unconfirmed_start` ist kein Grund für „Bot entfernen lassen“, und unabhängig vom Grund gilt für K4: Anfordern (`bot-remove`) **und** Ausführen (Prüfung unter Sperre vor `removeEmote`) verlangen `StartsAtAuthoritative = true`; K2 reiht ohne maßgeblichen Start nie ein (Befund 21, Sol-R2-1; in Spec 8 und der K4-Route nachgetragen). Späte Entscheidungen tragen `previousExpiresAtUtc` (Befund 22); `wish.botRemove` und `wish.botRemoveRequest` aus K4 folgen demselben Muster |
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
24 Stunden stehen einmal als benannte Konstante in `WishLimits`. Wird ein solcher Kandidat danach fällig, kann die Entscheidung
nur „vorlegen“ lauten, nie „einreihen“ (Zeile 3 in 2.2).

**2.2 `WishExpiryPolicy` (Core, rein).** Drei Fragen, jede eine statische Funktion ohne I/O:

- *Zurückhalten?* Eingabe: `StartsAtAuthoritative`, `ExpiresAtUtc`, `LastSyncedAtUtc` des Kanals, „jetzt“. Ausgabe: fällig oder
  zurückgehalten, nach 2.1.
- *Braucht die Entscheidung die Nutzung?* Wahr genau dann, wenn Bot-Modus an, Eigentum besteht, der Bot verfügbar ist (global und
  je Kanal), der Start maßgeblich ist und eine Schwelle gesetzt ist.
- *Entscheidung* (`Decide`): Eingabe sind die Fakten `BotModeEnabled`, `BotSuspendedReason`, `BotOwned`, globale Verfügbarkeit,
  `StartsAtAuthoritative`, Schwelle (leer = Schutz aus) und, wo nötig, die Bewertung (Mittel je Tag, Lückenmerkmal). Ausgabe:
  „vorlegen mit Grund“ oder „Entfernung einreihen“. Erste zutreffende Zeile gewinnt, exakt nach der Tabelle der Spec (Stand
  dieser Revision, sieben Zeilen): (1) Bot-Modus aus → `expired`; (2) kein Eigentum → `not_bot_owned`; (3) Start nur
  vorläufig → `unconfirmed_start`; (4) Bot global nicht verfügbar oder Kanal ausgesetzt → `bot_unavailable`; (5) Schwelle
  gesetzt und Lücke → `coverage_gap`; (6) Schwelle gesetzt und Mittel ≥ Schwelle → `protected`; (7) einreihen. Zeile 3 steht
  vor dem Bot-Ausfall (Sol-R2-1: `bot_unavailable` ist ein Grund, aus dem K4 „Bot entfernen lassen“ erlaubt; ein vorläufiger
  Wunsch darf ihn nie tragen), vor Schutz und Lücke, und gilt auch bei ausgeschaltetem Schutz. Daraus folgen zwei Eigenschaften,
  beide über die volle Kombinatorik getestet: Mit vorläufigem Start ist das Ergebnis nie „einreihen“ und nie einer der drei
  Gründe `bot_unavailable`, `coverage_gap`, `protected`. Fehlt die Bewertung, obwohl die zweite Frage „ja“ sagt, ist das ein
  Programmierfehler und wirft.

Die Gründe sind die Konstanten aus `WishStatusReason`; das `outcome` im Audit ist der Grund bzw. die Zeichenkette `queued`
(eine Konstante an der Policy). Fenster, Mittel und Lücke rechnet weiter `WishUsageWindow` aus K1; die Policy rechnet sie nicht nach.

**2.3 Schreiben je Wunsch (T3).** Eigene Transaktion je Kandidat: Zeile sperren, danach lesen, Auswahlbedingung und Kanal erneut
prüfen (ein inzwischen verlängerter, behaltener, entfernter, eingereihter oder in einen anderen Kanal gewanderter Wunsch wird
übersprungen), Einstellungen und Kanalfakten hinter der Sperre lesen, entscheiden, schreiben, Audit-Eintrag, Commit.

- *Vorlegen:* Zielzustand aus `WishTransitions` (Aktion `Present`, Kontext mit `HasExpiry`), `StatusReason` = Grund,
  `StatusChangedAtUtc` = „jetzt“. Unverändert bleiben `ExpiresAtUtc`, `EndedAtUtc` (leer), `DecisionVersion`, Slot und Eigentum.
- *Einreihen:* `RemovalQueuedAtUtc` = „jetzt“, `RemovalQueuedVersion` = `DecisionVersion`. Zustand, Grund und
  `StatusChangedAtUtc` bleiben.
- *Audit:* ein Eintrag `wish.due` unter `AuditActor.System`, Kanalname gesetzt, Ziel der Wunsch, Details nach Befund 10,
  im selben `SaveChanges` wie die Änderung. Nie Alias, Emote-Name oder Einlöser. **Genau einer je Ablauf:** Steht für diesen
  Wunsch schon ein `wish.due` mit demselben `expiresAtUtc` (geprüft hinter der Wunsch-Sperre, Befund 11), entfällt der Eintrag;
  Zustand bzw. Einreihung und Event werden trotzdem geschrieben. Verglichen wird der Zeitpunkt, nicht die Zeichenkette.
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

**2.6 Schwellenänderung ist eine Entscheidung (T2b; Sol-P1-1).** Ändert `UpdateSettingsAsync` den Wert von
`ProtectionThresholdPerDay` (ein, aus oder anderer Wert; verglichen wird der gerundete gespeicherte Wert), dann in derselben
Transaktion und in dieser Reihenfolge: Kanalzeile sperren (wie heute), die Wünsche des Kanals im Zustand `active` nach `Id`
aufsteigend sperren (`RowLocks.LockWishesByIdsAsync`), erst danach lesen; für jeden davon `DecisionVersion` + 1 und
`RemovalQueuedAtUtc`, `RemovalQueuedVersion` leeren; Einstellung, Audit `wish.settings` und Wünsche in einem Commit; ein
`wish.changed` wie bisher. Ändert sich die Schwelle nicht (nur Standardlaufzeit oder Vorlauf), fasst der Befehl keinen Wunsch an.
Daraus folgen beide Reihenfolgen: Hält der Lauf die Wunsch-Zeile zuerst, wartet die Einstellung und verwirft danach, was der Lauf
eingereiht hat, und der nächste Lauf bewertet mit der neuen Schwelle. Hält die Einstellung die Wunsch-Zeile zuerst, wartet der
Lauf und liest hinter der Sperre die schon bestätigte neue Schwelle. Vorgelegte Wünsche fasst der Befehl nicht an (vor K4 trägt
keiner eine Einreihung; K4 entscheidet für `bot-remove`).

**2.7 Bisheriger Ablauf im Audit einer Entscheidung (T2c; Sol-R2-2).** Betroffen sind `ExtendAsync`, `KeepAsync`, `CloseAsync`
und `CompleteRemovalAsync` mit ihren Einträgen `wish.extend`, `wish.keep`, `wish.close`, `wish.remove`. War `ExpiresAtUtc` des
Wunsches **vor** dem Befehl gesetzt und nicht nach „jetzt“ (dem Zeitpunkt des Befehls, gelesen hinter der Zeilensperre), bekommt
der Eintrag zusätzlich `previousExpiresAtUtc` mit genau diesem Wert, als UTC-Zeitpunkt in voller Genauigkeit wie `expiresAtUtc`
in `wish.due`. Sonst fehlt das Feld. Es steht unabhängig davon, ob schon ein `wish.due` für diesen Ablauf existiert (der Befehl
sucht nicht im Audit-Log); die Auswertung fasst je Paar zusammen. Die übrigen Details bleiben, wie sie sind; AK19 bleibt gewahrt.
Nicht betroffen: `PATCH`, `retry`, `re-add`, `adopt` (sie treffen keinen Wunsch mit laufender Frist), der Abgleich (externes
Entfernen ist keine Entscheidung eines Verwalters und hat keinen Audit-Eintrag) und die Kanal-Zusammenführung.

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
| T2b | Schwellenänderung verwirft Einreihungen (Änderung an `UpdateSettingsAsync`) | opus |
| T2c | Bisheriger Ablauf im Audit später Entscheidungen (Änderung an vier Befehlen in `WishService`) | sonnet |
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
  `Entities/WishStatus.cs` (in `WishLimits`: die 24 Stunden aus 2.1; in `WishStatusReason`: `unconfirmed_start` samt Aufnahme in
  die Liste `Presented`, Befund 21), `tests/…/Unit/WishTransitionsTests.cs` (der neue Grund in der Matrix), `docs/DECISIONS.md`.
- **Verträge:** Policy nach 2.2. `IWishBotAvailability`: eine asynchrone Frage „global verfügbar?“ mit Abbruch-Token, sonst
  nichts (Befund 13). `IWishExpiryService`: eine Methode `RunAsync(CancellationToken)` mit Ergebnis nach 2.4.
  `WishOptions.ExpiryIntervalMinutes`: Default 15, `Validate()` wirft außerhalb 1 bis 1440 mit einer Meldung, die den
  Konfigurationsschlüssel nennt (die Methode ist heute leer, `WishOptions.cs:23-25`).
- **Grenzfälle:** Reihenfolge der Zeilen (ein Fall, der 2 und 6 erfüllt, ergibt `not_bot_owned`; einer, der 3 und 4 erfüllt,
  `unconfirmed_start`, ebenso einer, der 3 und 6 erfüllt; einer, der 4 und 5 erfüllt, `bot_unavailable`; einer, der 5 und 6
  erfüllt, `coverage_gap`). **Nicht verfügbar, dann verfügbar (Sol-R2-1):** vorläufiger Start mit Eigentum ergibt bei
  ausgefallenem Bot `unconfirmed_start` und bei verfügbarem Bot ebenfalls, in beiden Lagen nie `bot_unavailable`. Mittel exakt
  gleich der Schwelle ist geschützt. Schutz aus bei Lücke und hoher Nutzung → einreihen (AK25, dritter Teil). Ausgesetzter Kanal
  mit Eigentum → `bot_unavailable`, ohne Eigentum → `not_bot_owned`. **Vorläufiger Start wird nie eingereiht:** über alle
  Kombinationen der übrigen Fakten ist das Ergebnis bei `StartsAtAuthoritative = false` nie „einreihen“ (als Eigenschaft über
  die volle Kombinatorik geprüft, nicht nur an einem Beispiel); im manuellen Modus bleibt es `expired`. Zurückhalten:
  maßgeblicher Start nie; vorläufig mit Sync vor 1 Stunde und Ablauf vor 1 Minute ja; Sync vor 25 Stunden nein;
  `LastSyncedAtUtc` leer nein; Sync frisch, aber Ablauf vor 25 Stunden nein; beide Grenzen exakt bei 24 Stunden als eigene Fälle
  festlegen und im Test benennen.
- **AKs:** AK22; reiner Teil von AK23, AK25, AK26.
- **Tests:** `Infrastructure.Tests/Unit` (dort liegen die reinen Wunsch-Tests aus K1), tabellengetrieben: sieben Zeilen plus
  Reihenfolgefälle; Zurückhalte-Regel; „braucht Nutzung“ für jede Kombination; `Decide` ohne Bewertung, wo sie nötig ist, wirft.
  AK23 als Kette aus `WishUsageWindow` und Policy: Laufzeit 30 Tage → Fenster genau 7 Tage; 2 Tage → 2 Tage, Mittel = Summe
  durch 2; 1 Tag → 24 Stunden, gültig; 23 Stunden → Lücke. Vorhandene Fälle in `WishUsageWindowTests`
  (`WishSlotMatcherTests.cs:206-262`) nicht doppeln, nur ergänzen. `WishOptionsTests`: Default 15; 0 und 1441 werfen; 1 und 1440
  gelten. `CoreAssemblyReferenceTests` bleibt grün.
- **Fertig:** Tests grün; DECISIONS-Eintrag angelegt mit: Tabelle der sieben Zeilen und Befund 1; begrenzter Aufschub und „vorläufig wird nie eingereiht“ samt neuem Grund (Befund 3,
  4, 21) als Betreiberentscheidung vom 2026-11-18; `DecisionVersion` bleibt (Befund 9); Schnittstelle nur global (Befund 13).
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

### T2b: Schwellenänderung verwirft Einreihungen

- **Ziel:** Eine Änderung des Schutzes ist gegen Fälligkeitsentscheidungen serialisiert und entwertet, was nach dem alten Stand
  eingereiht wurde (Befund 20, Vertrag 2.6). Liegt vor T3, damit der Lauf von Anfang an gegen den richtigen Vertrag gebaut wird.
- **Dateien:** `src/EmotePurge.Infrastructure/Services/WishService.cs` (`UpdateSettingsAsync`, `:45-98`),
  `tests/…/Integration/WishServiceTests.cs` bzw. eine neue Teildatei daneben, `docs/DECISIONS.md` (Absatz im K2-Eintrag; der
  K1-Absatz (h) zu den Sperren bleibt unangetastet, der neue verweist auf ihn).
- **Verträge:** 2.6. Der Vergleich „Schwelle geändert“ läuft auf dem gerundeten Wert gegen den gespeicherten; eine fehlende
  Einstellungszeile zählt als „Schutz aus“. `wish.settings` bleibt der einzige Audit-Eintrag des Befehls (kein Eintrag je
  Wunsch); die Zahl der entwerteten Einreihungen steht nicht in den Details **[E]** (das Feld wäre bis K4 immer 0).
- **Grenzfälle:** Kanal ohne aktive Wünsche (keine Wunsch-Sperre, Verhalten wie heute); gleiche Schwelle erneut gesetzt (kein
  Wunsch angefasst, `DecisionVersion` unverändert); Schutz von aus auf an, von an auf aus, anderer Wert (jeweils angefasst);
  nur `active` wird angefasst, `presented`, `approved` und beendete Wünsche nicht; Wünsche anderer Kanäle nie.
- **AKs:** keins der Spec direkt; Voraussetzung dafür, dass AK25 („Schutz an → vorlegen“) unter Nebenläufigkeit gilt.
- **Tests:** `Infrastructure.Tests/Integration`, deterministisch über eine fremde Transaktion, die eine Sperre hält, und die
  Beobachtung „der Befehl ist noch nicht fertig“ vor dem Freigeben (Vorbild: die Wettlauf-Tests am Index aus dem K1-Lauf, die
  nachweislich am Schlüssel blockieren; kein Schlafen auf Zeit als Beweis):
  1. Schwelle geändert, zwei aktive Wünsche, einer davon eingereiht (Felder direkt gesetzt) → beide `DecisionVersion` + 1,
     Einreihung leer, eine neue Einstellung, ein Audit, ein Event, alles oder nichts (ein erzwungener Fehler rollt alles zurück).
  2. Schwelle unverändert → kein Wunsch geschrieben.
  3. **Reihenfolge A (Wunsch-Sperre zuerst gehalten):** Eine fremde Transaktion hält die Zeile eines aktiven Wunsches (sie
     steht für den Lauf) und reiht ihn ein; `UpdateSettingsAsync` blockiert an der Wunsch-Zeile, nicht an der Kanalzeile; nach
     dem Commit der fremden Transaktion läuft der Befehl durch und die Einreihung ist leer.
  4. **Reihenfolge B (Kanal zuerst):** Eine fremde Transaktion hält die Kanalzeile; der Befehl wartet dort und hat noch keine
     Wunsch-Zeile gesperrt (eine dritte Verbindung kann den Wunsch sperren und freigeben). Belegt „Kanal vor Wunsch“.
  5. Kein Deadlock mit dem Abgleich: Abgleich und Befehl gleichzeitig auf demselben Kanal enden beide ohne `40P01` (beide
     nehmen Kanal vor Wunsch).
- **Fertig:** Tests grün, die bestehenden Einstellungs-Tests unverändert grün.
- **Commit:** `fix: let a protection change drop queued wish removals`

### T2c: Bisheriger Ablauf im Audit später Entscheidungen

- **Ziel:** Eine Fälligkeit, die ein Verwalter entscheidet, bevor der Lauf sie gesehen hat, bleibt für die Pilot-Messung
  auffindbar (Befund 22, Vertrag 2.7).
- **Dateien:** `src/EmotePurge.Infrastructure/Services/WishService.cs` (`RunTransitionAsync` `:758-816` und
  `AuditAndCommitAsync` `:820-838`: der bisherige Ablauf wird vor der Entscheidung des Befehls festgehalten und an das Audit
  durchgereicht; die vier Befehle aus 2.7 schalten das ein, die übrigen nicht), die Übergangs-Tests daneben
  (`WishServiceTests.Transitions.cs`), `docs/DECISIONS.md` (Absatz im K2-Eintrag; ergänzt K1 (j), ohne ihn umzuschreiben).
- **Verträge:** 2.7. Festgehalten wird der Wert **vor** der Änderung (bei `extend` also nicht der neue Ablauf). „Überfällig“
  schließt die Grenze ein (Ablauf gleich „jetzt“ zählt), wie die Auswahl des Laufs.
- **Grenzfälle:** `extend` vor dem Ablauf (Feld fehlt); `extend` auf `presented` (`expired`) nach einem `wish.due` (Feld steht,
  gleicher Wert wie im `wish.due`); `keep` auf einen überfälligen `active`; `close` auf `presented` ohne Ablauf, etwa Grund
  `unconfirmed` (Feld fehlt); `complete-removal` aus `removed_externally` mit abgelaufener Frist (Feld steht); ein abgelehnter
  Befehl (409) schreibt weiterhin gar keinen Eintrag.
- **AKs:** keins der Spec direkt; Voraussetzung für AK31 und Spec 13 (a).
- **Tests:** `Infrastructure.Tests/Integration`: je Befehl ein Fall „überfällig“ mit dem erwarteten Wert und einer „nicht
  überfällig“ ohne Feld; bestehende Erwartungen an die Details der K1-Einträge werden nur dort angepasst, wo sie das Fehlen
  weiterer Felder prüfen, und der AK19-Test (kein Alias, kein Name, kein Login) bleibt unverändert grün.
- **Fertig:** Tests grün.
- **Commit:** `feat: record the previous expiry on late wish decisions`

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
  5. Zeile 7: Bot-Modus, Eigentum, verfügbar, maßgeblicher Start, Schutz aus → `active` bleibt, `RemovalQueuedAtUtc` = Laufzeitpunkt,
     `RemovalQueuedVersion` = `DecisionVersion`, Audit `queued`, ein Event; zweiter Durchlauf: beide Felder unverändert, kein
     Audit (AK27).
  6. eingereiht, dann `extend` aus K1 → Einreihung leer (E25); nach dem neuen Ablauf reiht der Lauf mit der neuen Version ein.
  7. AK25: Schutz an, Zählbeginn einen Tag nach Fensterbeginn, Nutzung über der Schwelle → `coverage_gap`; `DeactivatedAtUtc`
     gesetzt → `coverage_gap`; Schutz aus, sonst gleich → eingereiht.
  8. AK24: zwei Aliase mit 10 und 20 je Tag, Schwelle 30 → `protected`; Schwelle knapp darüber → eingereiht.
  9. AK23 an der Datenbank: Laufzeit 2 Tage, Summe durch 2 gegen die Schwelle; 23 Stunden im Set → `coverage_gap`.
  10. Zeile 2 vor Zeile 6: kein Eigentum bei Nutzung über der Schwelle → `not_bot_owned`.
  11. Zeile 4 (maßgeblicher Start): Standard-Verfügbarkeit (nie) → `bot_unavailable`; verfügbar, aber `BotSuspendedReason`
      gesetzt → `bot_unavailable`.
  12. AK26: vorläufiger Start, Sync vor 1 Stunde, Ablauf vor 1 Minute → nichts geschrieben, Zähler „zurückgehalten“. Danach
      fällig, je einmal im manuellen Modus und im Bot-Modus (Eigentum, Bot verfügbar, **Schutz aus**): Sync vor 25 Stunden;
      `LastSyncedAtUtc` leer; Sync frisch, Ablauf vor 25 Stunden. Erwartet manuell `presented` (`expired`), im Bot-Modus
      `presented` (`unconfirmed_start`) mit `RemovalQueuedAtUtc` leer, Audit-`outcome` gleich dem Grund. Gegenprobe: derselbe
      Bot-Fall mit maßgeblichem Start wird eingereiht.
  13. Abgleich zuerst: `active`, abgelaufen, Slot archiviert, mit dem **echten** Abgleichsdienst → `removed_externally`, kein
      `wish.due`.
  14. Abgleich scheitert für Kanal A (Double wirft): Kanal B läuft normal, der fällige Wunsch in A wird trotzdem vorgelegt, die
      Zusammenfassung zählt einen gescheiterten Abgleich.
  15. Zwei Durchläufe gleichzeitig (zwei Kontexte) → genau ein `wish.due`. Dazu: Eine fremde Transaktion hält die Zeile und setzt
      den Wunsch auf `kept`; der Lauf wartet, liest danach und überspringt.
  16. Zwei fällige Wünsche in einem Kanal → ein Event; in zwei Kanälen → zwei.
  17. Nicht gewählt werden: `presented`, eingereihte, beendete Wünsche und `active` ohne Ablauf.
  18. Die Verfügbarkeit wird im rein manuellen Durchlauf nie gefragt und sonst höchstens einmal.
  19. **Ein `wish.due` je Ablauf (Sol-P2-2), mit den echten Schreibern:** Wunsch im Bot-Modus mit Eigentum wird eingereiht (ein
      `wish.due`, `queued`); der Slot wird umbenannt und der **echte** Abgleich läuft (`EndOwnership`: Eigentum weg, Einreihung
      leer, `DecisionVersion` + 1, weiter `active`); nächster Durchlauf → `presented` (`not_bot_owned`), ein `wish.changed`, und
      im Audit-Log steht weiterhin **genau ein** `wish.due` für diesen Wunsch. Danach `extend`, Uhr hinter den neuen Ablauf,
      Durchlauf → der zweite Eintrag mit dem neuen `expiresAtUtc`. Dazu ein Fall mit einem fremden `wish.due` desselben Wunsches
      zu einem **anderen** Ablauf: der unterdrückt nichts.
  20. **Schwelle gegen Lauf, Reihenfolge A (Sol-P1-1):** Der Lauf reiht bei ausgeschaltetem Schutz ein; danach setzt der echte
      `UpdateSettingsAsync` eine Schwelle unter der Nutzung → Einreihung leer, `DecisionVersion` + 1; nächster Durchlauf →
      `presented` (`protected`), kein zweiter `wish.due` (Fall 19).
  21. **Reihenfolge B:** Eine fremde Transaktion hält die Wunsch-Zeile und hat die Schwelle geändert, aber nicht bestätigt (sie
      steht für den Einstellungsbefehl); der Lauf wartet an der Zeile; nach dem Commit entscheidet er mit der neuen Schwelle
      (`protected`), es gibt zu keinem Zeitpunkt eine Einreihung. Der Lauf selbst ändert in keinem Fall `DecisionVersion`.
  22. **Vorläufig, Bot erst ausgefallen, dann zurück (Sol-R2-1):** Bot-Modus, Eigentum, vorläufiger Start, fällig (Sync alt),
      Verfügbarkeit „nein“ → `presented` (`unconfirmed_start`), nicht `bot_unavailable`, keine Einreihung. Verfügbarkeit auf
      „ja“, weiterer Durchlauf → der Wunsch ist kein Kandidat mehr, nichts ändert sich, keine Einreihung. Derselbe Ablauf mit
      `BotSuspendedReason` statt globalem Ausfall.
  23. **Späte Entscheidung vor dem ersten Lauf nach Wiederanlauf (Sol-R2-2, mit T2c):** Wunsch seit 30 Stunden abgelaufen, kein
      Lauf dazwischen; echter `extend` → Audit `wish.extend` trägt `previousExpiresAtUtc` gleich dem alten Ablauf; danach ein
      Durchlauf → kein `wish.due` (der neue Ablauf liegt in der Zukunft), nichts geschrieben. Die Fälligkeit ist damit allein
      über den Eintrag des Befehls auffindbar.
  Dazu in `Infrastructure.Tests/Unit/WishRegistrationTests`: Dienst und Verfügbarkeit lösen sich mit und ohne Schalter auf, die
  Standard-Verfügbarkeit sagt nein.
- **Fertig:** Tests grün; `dotnet test` für `Infrastructure.Tests` gesamt grün; DECISIONS ergänzt um: je Wunsch eine Transaktion
  mit Zeilensperre, keine Advisory- und keine Kanalsperre (Befund 12); Kanalauswahl für den Abgleich (Befund 17) und Weiterlaufen
  nach einem gescheiterten Abgleich; Details von `wish.due` (Befund 10) und der zweite Eintrag nach verworfener Einreihung
  (Befund 11, genau einer je Ablauf); Nutzung nur bei Bedarf (Befund 16).
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
  Lauf nie nach 7TV schreibt, der begrenzte Aufschub bei vorläufigem Start samt „wird vorgelegt, nie eingereiht“, RR1 als
  bekannte Grenze, dass eine Schwellenänderung Einreihungen verwirft; dazu die Auswertung der Pilot-Messgröße (a), siehe
  unten), `docs/Architectur.md` (`:86`: fünfzehn, `WishExpiryWorker` in der Liste), `CLAUDE.md` (deutsch: Worker-Liste
  „fünfzehn“ mit einem Satz zum neuen Dienst, Zeile „Wunsch-Emotes“ im Umsetzungsstand um K2 ergänzt, Branch-Name aktuell),
  `docs/Feature-Ideen-2026-08-01.md` (Statuszeile A18 oben in der Tabelle: K2 dazu), `docs/DECISIONS.md`
  (`**Betrifft:**`-Zeile vollständig, auch die Testdateien).
- **Vertrag der Messgröße (a) (Sol-P2-4; Wortlaut wie Spec 13 nach dieser Revision):**
  - *Fälligkeit* = ein Paar (`wishId`, Ablauf). Quellen sind `expiresAtUtc` aus `wish.due` **und** `previousExpiresAtUtc` aus
    `wish.extend`, `wish.keep`, `wish.close`, `wish.remove` (T2c; K4 trägt seine Einträge nach). Dasselbe Paar aus mehreren
    Einträgen oder beiden Quellen zählt einmal.
  - *Kohorte* = Fälligkeiten, deren Ablauf im Beobachtungszeitraum liegt (nicht der Zeitpunkt eines Eintrags).
  - *Reife* = Ablauf plus 24 Stunden liegt nicht nach dem Auswertungszeitpunkt. Unreife Fälligkeiten gehören weder in den
    Nenner noch in den Zähler.
  - *Erledigt* = zum `wishId` gibt es einen Eintrag `wish.extend`, `wish.keep`, `wish.remove` oder `wish.close` (K4 trägt
    `wish.botRemove` nach) mit Zeitpunkt nach dem Ablauf und höchstens 24 Stunden danach. Die Frist läuft ab dem Ablauf, nie ab
    dem Zeitpunkt von `wish.due`.
  - *Verspätet* = die Fälligkeit ist reif und nicht erledigt, und entweder liegt ihr frühester `wish.due` mehr als 24 Stunden
    nach dem Ablauf, oder sie hat gar keinen `wish.due` und ist nur über eine Entscheidung mit `previousExpiresAtUtc` bekannt
    (der Lauf hat sie nie gesehen). Solche Fälligkeiten bleiben im Nenner und werden als eigene Zahl ausgewiesen, damit ein
    Worker-Ausfall von säumigen Verwaltern unterscheidbar ist.
  - *Ausgabe:* Nenner, Zähler, Quote, Zahl der verspäteten, und die Liste der Nicht-Treffer (`wishId`, Ablauf) für die
    Handzählung extern entfernter Einträge (Spec 13).
  - *Bekannte Grenze, im Text nennen:* Ein überfälliger Wunsch, den der Lauf nie gesehen hat und der **außerhalb** von Emote
    Purge entfernt wird (Abgleich nach `removed_externally`), hinterlässt weder `wish.due` noch eine Entscheidung und fehlt im
    Nenner. Dasselbe gilt für eine Kanal-Zusammenführung, die ihn als `merged` beendet. Beides sind keine Entscheidungen eines
    Verwalters über eine Frist.
- **AKs:** AK29 (Doku-Teil), Vorbereitung AK31.
- **Prüffälle der Abfrage (Pflicht, Ergebnis in die Nachtnotiz):** gegen eine eigene Wegwerf-Datenbank im Dev-Postgres-Container
  (wie in Abschnitt 5, nie die geteilte Dev-Datenbank) mit von Hand gesäten Audit-Zeilen, Auswertungszeitpunkt fest gewählt:
  (1) rechtzeitig: `wish.due` 10 Minuten nach Ablauf, `wish.keep` 3 Stunden nach Ablauf → Treffer; (2) verspätete
  Feststellung: `wish.due` 48 Stunden nach Ablauf, `wish.extend` 49 Stunden nach Ablauf → **kein** Treffer, zählt als verspätet
  festgestellt; (3) knapp daneben: Entscheidung 24 Stunden und 1 Minute nach Ablauf → kein Treffer; exakt 24 Stunden → Treffer;
  (4) unreif: Ablauf vor 5 Stunden → weder Nenner noch Zähler; (5) doppelt: zwei `wish.due` zum selben Paar → Nenner 1;
  (6) zwei Fälligkeiten desselben Wunsches (verlängert, erneut abgelaufen) → Nenner 2, jede mit eigener Frist, die Verlängerung
  der ersten zählt nicht als Erledigung der zweiten; (7) Entscheidung **vor** dem Ablauf (altes `wish.extend`) → kein Treffer;
  (8) Ablauf außerhalb des Beobachtungszeitraums, Eintrag innerhalb → nicht in der Kohorte;
  (9) späte Entscheidung ohne `wish.due`: `wish.extend` mit `previousExpiresAtUtc` 30 Stunden nach dem Ablauf → im Nenner, kein
  Treffer, zählt als verspätet; (10) frühe Entscheidung ohne `wish.due`: `wish.keep` mit `previousExpiresAtUtc` 5 Minuten nach
  dem Ablauf → im Nenner, Treffer; (11) dasselbe Paar aus `wish.due` und aus `previousExpiresAtUtc` → Nenner 1. Erwartet insgesamt: die Zahlen, die
  sich aus dieser Liste ergeben, vorab im Task-Bericht ausgerechnet und dann gegen die Abfrage gelesen.
- **Tests:** keine automatisierten (die Abfrage ist Betriebsdoku, kein Produktivcode). `grep` nach „fourteen“ und „vierzehn“ außerhalb von `docs/plans`, `docs/superpowers`, `docs/nachtlaeufe` und
  alten DECISIONS-Einträgen findet nichts mehr.
- **Fertig:** Alle elf Prüffälle liefern die vorab ausgerechneten Zahlen; die Wegwerf-Datenbank ist entfernt.
- **Commit:** `docs: document the wish expiry run`

## 4. AK-Abdeckung

| AK | Task | AK | Task |
|---|---|---|---|
| AK22 | T1 (Policy, Reihenfolge), T3 (Fälle 10, 11) | AK27 | T3 (Fälle 3 bis 6, 15, 19) |
| AK31 (Zulieferung) | T2c, T3 (Fall 23), T5 | AK26 (Bot-Ausfall) | T1, T3 (Fall 22) |
| AK23 | T1 (Rechnung), T3 (Fall 9) | AK28 | T4 |
| AK24 | T2 (Summe), T3 (Fall 8) | AK29 | T4, T5 |
| AK25 | T1 (Regel), T2b (Nebenläufigkeit), T3 (Fälle 7, 20, 21) | AK30 | T6 |
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

**Live nicht herstellbar, nur durch Tests belegt (im Protokoll so benennen):** die Zeilen 2 bis 7 der Tabelle (vor K4 gibt es
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
7. PR-Text (englisch) nennt: keine Migration; die Änderungen an der Spec (begrenzter Aufschub, neue Zeile 3 mit
   `unconfirmed_start`, `expiresAtUtc` im Audit, Messgröße (a)) und die Abweichung im Testplan (Worker-Tests); die
   Verhaltensänderung an `PUT …/wishes/settings` (Schwellenänderung verwirft Einreihungen) und das neue Detailfeld
   `previousExpiresAtUtc` an vier K1-Audit-Einträgen; den Vertrag für K4 (maßgeblicher Start beim Anfordern und Ausführen); dass die Zeilen 2 bis 7 bis K4 nur getestet, nicht live erreichbar sind; den neuen Audit-Typ für K3; die
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
| R2 | Ein Wunsch auf einem undatierten Slot wird bis zu 24 Stunden nach seinem Ablauf vorgelegt, und im Bot-Modus muss ihn dann ein Mensch entscheiden (`unconfirmed_start`) | [B] L2; sichere Richtung; die Pilot-Auswertung weist verspätete Feststellungen eigens aus |
| R3 | Die Suche nach einem vorhandenen `wish.due` (Befund 11) läuft ohne eigenen Index über das Audit-Log | begrenzt über `OccurredAtUtc` ab dem Ablauf; nur je fälligem Wunsch; bei Pilotgröße unkritisch, kein Index gebaut **[E]** |
| R4 | Der Abgleich je Takt für jeden Kanal mit Wünschen verlängert den Lauf | eine Vorprüf-Abfrage je Kanal, nacheinander; höchstens 80 aktive Kanäle; Fehler isoliert |
| R5 | `wish.due` erscheint im Aktivitäts-Feed unübersetzt, bis K3 es übersetzt | nur lokal sichtbar (Schalter auf Prod aus); im PR benannt |
| R6 | Schutz und Lücke sind vor K4 in Produktion ohne Wirkung; ein Fehler in den Zeilen 2 bis 7 fiele erst mit K4 live auf | vollständige Tabellen- und Datenbanktests (T1, T3); Sol-Review mit Blick auf Reihenfolge und Fensterrechnung; K4 wiederholt die Live-Probe im Bot-Modus |
| R7 | Kein Limit je Durchlauf: sehr viele gleichzeitig fällige Wünsche (etwa nach einem langen Worker-Ausfall) laufen in einem Takt | je Wunsch eine kurze Transaktion; bei Pilotgröße unkritisch; kein Limit gebaut **[E]** |
| R8 | Eine Schwellenänderung schreibt jede aktive Wunsch-Zeile des Kanals und wartet auf einen gerade laufenden Lauf oder Abgleich | wenige Zeilen je Kanal, Sperren nur für die Dauer einer kurzen Transaktion; Reihenfolge Kanal vor Wunsch wie überall (2.6, T2b Fall 5). Für den Bot-Modus-Schalter zieht K4 nach |
| R9 | `coverage-local` ist bei der chirurgischen Änderung in `WishService.cs` unscharf | der Umzug (T2) verschiebt gedeckte Zeilen in eine neue, durch die Detail-Tests gedeckte Datei |
| R10 | Die Rückkehr eines extern entfernten Wunsches kurz vor Ablauf verkürzt die echte Zeit im Set, ohne dass das Fenster es zeigt | [B] L1 (Abschnitt 9): Fenster ab Start des Wunsches; wirkt nur im Bot-Modus mit Schutz |

**Für den Sol-Review dieses Branches (Fokus):** Reihenfolge der sieben Zeilen und Trennung von Zeile 1 und 4; `unconfirmed_start` vor `bot_unavailable`; dass ein vorläufiger Start auf keinem Weg eingereiht wird; beide
Sperr-Reihenfolgen zwischen Schwellenänderung und Lauf; genau ein `wish.due` je Ablauf; Idempotenz unter
zwei gleichzeitigen Läufen und gegen Befehle aus K1; die Zurückhalte-Regel an ihren beiden 24-Stunden-Grenzen; dass der Lauf nie
`ExpiresAtUtc` oder `DecisionVersion` schreibt; dass kein Log und kein Audit-Eintrag Alias, Namen oder Einlöser trägt.

## 8. Selbstprüfung des Plans

- Jeder Punkt aus Spec „K2“ hat einen Task: Takt und Schalter (T4), vier Schritte des Durchlaufs (T3), vorläufiger Start (T1,
  T3), bedingtes Schreiben und Idempotenz (T3), `wish.due` (T1, T3), Entscheidungstabelle (T1), `IWishBotAvailability` (T1, T3),
  kurze Laufzeiten ohne eigene Logik (T1, T3 Fall 9), Dateiliste samt Compose und `CLAUDE.md` (T4, T5). AK22 bis AK31 sind
  zugeordnet (Abschnitt 4).
- Namen sind über die Tasks gleich: `WishExpiryPolicy`, `IWishExpiryService.RunAsync`, `WishExpiryService`, `WishExpiryWorker`,
  `IWishBotAvailability`, `WishOptions.ExpiryIntervalMinutes`, `wish.due`, `WishUsageQueries`, `unconfirmed_start`.
- Lagen, die die Spec nicht ausbuchstabiert und die ein Test festhält: ausgesetzter Bot-Kanal (T1, T3 Fall 11), ewig vorläufiger
  Start (T1, T3 Fall 12), nie gesyncter Kanal (T1), zwei gleichzeitige Läufe (T3 Fall 15), gescheiterter Abgleich (T3 Fall 14),
  Abgleich entfernt den Kandidaten (T3 Fall 13), erneutes Einreihen nach `extend` (T3 Fall 6), Neubewertung ohne zweiten Audit-Eintrag (T3 Fall 19), vorläufiger Start bei Bot-Ausfall und Rückkehr (T3 Fall 22), späte
  Entscheidung vor dem ersten Lauf (T2c, T3 Fall 23), Schwellenänderung gegen
  den Lauf in beiden Reihenfolgen (T2b, T3 Fälle 20, 21), Worker mit Fehler im Durchlauf (T4).
- Kein Task braucht eine Rückfrage; was Betreiber oder Orchestrator braucht, steht in Abschnitt 6 Punkt 6 und in Abschnitt 9.

## 9. Betreiberfragen vor dem Lauf und Betreiberentscheidungen

**Offene Betreiberfragen: keine.** Alles Übrige ist als **[E]** entschieden und widerrufbar; der Lauf kann starten.

**Vor dem Lauf zu erledigen (Orchestrator, keine Entscheidung):** Der Worktree steht auf `origin/main` mit den K1-Commits
(erledigt, Basis `ab4882ea`). Ein Rebase mitten im Lauf findet nicht statt.

**Entschieden [B], 2026-11-18:**

- **L1, Fenster ab Start des Wunsches: angenommen.** Das Bewertungsfenster rechnet wie Spec E4 und der K1-Code
  (`WishUsageWindow.WindowStart`) ab dem Start des Wunsches, auch bei einem Rückkehrer, der bis zu 6 Stunden nicht im Set war.
  Die Nutzung früherer Aufenthalte zählt mit, die Abwesenheit senkt das Mittel geringfügig. Keine Änderung an der
  Fensterfunktion.
- **L2, begrenzter Aufschub: angenommen, mit Präzisierung.** Kein Wunsch hängt dauerhaft: Ein vorläufiger Start hält die
  Fälligkeit höchstens zurück, solange der Sync frisch ist und der Ablauf weniger als 24 Stunden zurückliegt. Danach wird der
  Wunsch **vorgelegt** und **nie** zur Bot-Entfernung eingereiht (`unconfirmed_start` im Bot-Modus, `expired` im manuellen
  Modus). Eingearbeitet in Befund 3 und 21, Vertrag 2.1 und 2.2, T1, T3 Fall 12, und in die Spec (K2-Text, Tabelle, AK22, AK25,
  AK26, Gründe in Abschnitt 5).
