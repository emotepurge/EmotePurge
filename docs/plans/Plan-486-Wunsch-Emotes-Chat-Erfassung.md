# Plan #486: Wunsch-Emotes, Kind K6: Chat-Erfassung und „Belohnung anlernen“

Erstellt am 2026-11-19 im Worktree `EmotePurge-wt-486` (Branch `feat/486-wish-capture`, Basis `origin/main` @ `264db942`; K1
mit PR #492, K2 mit PR #493, K3 mit PR #494 gemergt. Alle Codestellen dieses Plans sind an diesem Stand gelesen).
**Status: Revision 2 (2026-11-19), Endstand des Plans; eine weitere Plan-Runde gibt es nicht. Sol adversarial zweimal durch
(`gpt-6.2-sol`; Runde 1: 3 × P1, 8 × P2, 1 × P3; Runde 2: 2 × P1, 7 × P2). Jede Fundstelle ist am Code nachgeprüft, alle
halten. Der Betreiber hat nach Runde 2 entschieden: sieben Befunde voll, zwei abgestuft (Lückenmessung als Näherung,
Datumskorrektur nur für K6-Autozuordnungen). Die Betreiberfragen F1 bis F4 sind entschieden. Die Code-Prüfung durch Sol
folgt am fertigen Branch. Nichts committet.**
Quellen: [Spec](../superpowers/specs/2026-11-17-wunsch-emotes-spec.md) Abschnitte 1 bis 17 und „K6: Chat-Erfassung und Belohnung
anlernen“ (AK46 bis AK70), 6.3, 6.5, 6.6, E3, E13, E14, E20 bis E22, E26; [Entwurf](../designs/Wunsch-Emotes-mit-Ablauf-2026-11-17.md);
[Plan-483](Plan-483-Wunsch-Emotes-Backend-Kern.md), [Plan-484](Plan-484-Wunsch-Emotes-Ablauf-Lauf.md),
[Plan-485](Plan-485-Wunsch-Emotes-Verwalter-UI.md) (dort 1.2: Übergaben an K6); die Live-Protokolle
`docs/nachtlaeufe/live-T0c-2026-11-18.md`, `live-T12-2026-11-18.md`, `live-484-2026-11-18.md`, `live-T16-2026-11-19.md`;
DECISIONS „Wish emotes, child 1/2/3“; `CLAUDE.md` (Regeln 3, 4, 5, 6, 7, 10, 11, 12, 16, Schichtentreue, Tests, Sprache),
`web/.claude/CLAUDE.md`, `docs/UI-Designsprache.md` (verbindlich), `DESIGN.md`; Issue #486, Epic #481.

**Was dieser Plan trägt:** den Befund am Code, die Lücken und Widersprüche der Spec mit Entscheidung, die geteilten Verträge,
den Schnitt in Tasks mit Grenzfällen und Tests, die AK-Zuordnung, E2E-Fälle, die Live-Verifikation mit echtem Chat, Gates,
Risiken. Kein Code; Namen und Signaturen nur als Form. Die Spec reist mit: Jeder Implementer liest seinen Task hier, die
genannten Spec-Abschnitte **und** die genannten Bestandsdateien, bevor er schreibt. Vor jeder UI-Arbeit
`docs/UI-Designsprache.md` §11 und `DESIGN.md`.

**Lesehinweis:** **[E]** Planer-Entscheidung (der Betreiber kann widersprechen) · **[P]** Präzisierung ohne
Entscheidungsgehalt · **[B]** Betreiberentscheidung aus Spec Abschnitt 17 · **[W*n*]** Widerspruch oder Lücke aus Abschnitt 9 ·
**[F*n*]** Betreiberfrage aus Abschnitt 11 (alle entschieden) · **Sol-P1-*n*/P2-*n*/P3-*n*** Befund der
adversarialen Prüfung, Runde 1 (Tabelle „Revision 1“), **Sol-R2-…** Runde 2 (Tabelle „Revision 2“) · „Spec E26“ meint die Festlegung der Spec, „AK50“ das Akzeptanzkriterium aus K6.

### Revision 2 (2026-11-19): Nachprüfung durch Sol über Revision 1

| Befund | Prüfung am Code | Eingearbeitet in |
|---|---|---|
| Sol-R2-P1-1 Moderatorrechte für Einlöserdaten hängen am Login | hält: `ModeratorCheckService.cs:16-34` vergleicht nur `channel.Login`, obwohl `TwitchModeratedChannelInfo` die `BroadcasterId` trägt; `/mine` fragt je Kanal dieselbe Namensprüfung (`WishOverviewService.cs:129`) | 2.4a neu: Zugang zu K6-Daten nur „an der ID bestätigt“, interne Kanal-ID wird mitgeführt; T4, T6, T7; Bestandsprüfung für Nicht-K6-Daten unverändert, die allgemeine Änderung ist Folge-Issue (Abschnitt 14) |
| Sol-R2-P1-2 Platzhalter-CHECK blockiert den Link-Abgleich | hält: `WishReconcileService.Link` (`:414-433`) setzt `Alias`, nicht `EmoteName`; ein Link-Wunsch hätte beim Verknüpfen einen leeren Namen in `active` | 2.8a: Tabelle „Übergänge aus einem Platzhalterzustand“; T3, T6; T9 löscht die offene Dublette statt sie nach `merged` zu führen; Tests gegen das migrierte Schema |
| Sol-R2-P2-3 alte 7TV-Links treffen ihren Slot nicht | hält: DECISIONS K3 (m) belegt ObjectID → ULID und Großschreibung; im Backend gibt es keine Umrechnung (nur `canonicalSevenTvEmoteId` im Web) | 2.5a neu: `SevenTvEmoteIdCanonical` in Core, angewandt vor Speichern und Statusprüfung in Erfassung und Freigabe; T2 |
| Sol-R2-P2-4 Datumskorrektur verliert gebuchte K1-Entscheidungen | hält für die K1-Zeile aus Revision 1 | **abgestuft [B]:** Neuprüfung nur für K6-Autozuordnungen; K1 unverändert; Regel für zwischenzeitliche Entscheidungen in 2.5; K1-Lücke als Folge-Issue (Abschnitt 14) |
| Sol-R2-P2-5 Sitzungskennungen nur bei atomarer Prüfung wirksam | hält (Vertrag aus Revision 1 nannte keine Schreibbedingung) | 2.4: jede Änderung der Anlern-Zeile ist eine bedingte Anweisung mit `SessionId` (und `CandidateId` bzw. Ablauf) in der Bedingung; T4 mit zwei Verbindungen |
| Sol-R2-P2-6 Lückenmessung wertet alte Join-Bestätigungen aus | hält: `MarkAllChannelsUnconfirmed` läuft bei `FrameStale` erst im Clientwechsel (`TwitchChatManager.cs:320-361`) | **abgestuft [B]:** kein weiteres Modell; eine Regel im vorhandenen Tracker (bei offener Verbindungslücke schließt die Abtastung nichts), der Rest ist als Näherung benannt (2.7, Hilfetext, DECISIONS); keine neuen Rennen-Tests |
| Sol-R2-P2-7 Freigeben verbucht eine Einlösung ohne Verlängerung | hält (Vertrag aus Revision 1) | 2.6: 409 `wish_running_not_extendable`, der zweite Wunsch bleibt offen; Antwort und Oberfläche nennen den Ausgang; T6, T7, T14, E14 |
| Sol-R2-P2-8 Erfassungs-Worker verdrängt den letzten Nutzungs-Flush | hält: 30 s `ShutdownTimeout` (Compose-Kommentar `docker-compose.yml:143-151`), der neue Dienst stoppt zuerst | 2.3 „Stoppen“: Senke schließt, Leeren mit 5 s Teilbudget, Rest gezählt; T10, T11 |
| Sol-R2-P2-9 Live-Gate: warmer Cache, Admin-Health, C6 | hält: der Handler verzweigt am Snapshot des Workers (`SevenTvSyncService.cs:1341-1342`), nicht am 7TV-Stand; `WishEndpoints.cs:25-37,309-317` liefern bei ausgeschaltetem Schalter 404 | Abschnitt 7: A7 mit Nachweis des leeren Snapshots (Zähler aus T10), AK70 ohne A7 „teilweise geprüft“, Admin-Konto getrennt von K1 (V11), C6 mit Codes je Schalter |

**Widerlegt wurde kein Befund.** Geänderte Tasks: T2 (Normalisierung), T3, T4, T6, T7 (zehn Codes, Zugangsfilter), T9, T10
(Zähler „leerer Snapshot“, Senke schließt), T11 (Stopp-Budget), T14, T17 (E14), T18. Neu: Abschnitt 14 (Folge-Issue).

### Revision 1 (2026-11-19): was sich geändert hat

| Befund | Prüfung am Code | Eingearbeitet in |
|---|---|---|
| Sol-P1-1 Login-Rückweg bindet Einlöserdaten an einen wiederverwendbaren Namen | hält: `ChannelAccessService.cs:68-79` lässt bei fehlender ID den Login gelten | 2.4 „Kanalbindung“: nur gebundene Kanäle, Prüfung nach der Kanalsperre, Herausgabe gesperrt; 409 `wish_channel_unbound`; die Beobachtung hat wieder sieben Felder; T4, T5, T6 |
| Sol-P1-2 Api aus, Worker an erfasst verborgen weiter | hält: beide Prozesse lesen ihre eigenen Optionen | **schlank [B]:** kein gemeinsamer Laufzeitschalter; der Worker ist maßgeblich (2.3), Startlogzeile, `WishCaptureActive` in der Worker-Health samt Zeile in `/admin/monitoring`; Operations (T18); C1 und C3 mit echter Einlösung |
| Sol-P1-3 verspätetes Delta gibt einen alten Slot zur Entfernung frei | hält: `EmoteSlotDeltaPlanner.cs:125-127,161-172` setzt den Beobachtungszeitpunkt, `WishReconcileService.cs:342-346,488-506` bestätigt danach ohne Fensterprüfung | 2.5 „Datumskorrektur prüft das Fenster erneut“; T3 mit Regressionstest Delta → REST-Korrektur → keine Entfernung; B5a |
| Sol-P2-4 Bestätigen kann einen anderen Kandidaten speichern | hält (Vertrag aus Revision 0) | 2.4: `SessionId` und `CandidateId`, 409 `wish_learning_stale`; T1, T4, T7, T15, E12 |
| Sol-P2-5 Vorfilter verwirft die erste Einlösung nach einem Befehl | hält (Vertrag aus Revision 0) | **abgewandelt:** Vorfilter, Momentaufnahme und Kommando `WISHCAPTURE:` entfallen ganz; der Dienst entscheidet jede Beobachtung an der Datenbank (2.3, 2.4, [W25]) |
| Sol-P2-6 Dedupe verliert den Schlüssel | hält (Vertrag aus Revision 0) | 2.4 Schritt 3: inhaltsfreier Nachweis `WishCaptureReceipts`, 7 Tage; `IrcMessageId` am Wunsch wird nicht mehr für die Dedupe gebraucht und fällt bei Löschung weg; T1, T5, T8 |
| Sol-P2-7 Kanal-Merge lässt eine Einlösung zweimal entscheidbar | hält für den Vertrag aus Revision 0; **der Fall ist mit Sol-P1-1 konstruktiv ausgeschlossen** (`TwitchChannelId` ist eindeutig, `AppDbContext.cs:43`: zwei Zeilen können nicht dieselbe `RoomId` tragen) | **abgewandelt:** trotzdem eine Regel für nachweislich gleiche Einlösungen (T9), geprüft an von Hand gebauten Zeilen; Nachweise wandern mit |
| Sol-P2-8 Neustart und Reconnect verfälschen die Lücken | hält: `TwitchChatManager.cs:857` setzt `IsConnected` beim `004`, `Worker.cs:141-155` füllt den Roster nach und nach | **abgestuft [B]:** 2.7 neu (Bestand aus der Datenbank, Schließen erst mit bestätigtem Join, Beginn am Signalzeitpunkt, kein feineres Modell); A13, A15 |
| Sol-P2-9 Freigeben als Verlängerung umgeht Sperre und Audit von K2 | hält: `RunTransitionAsync` sperrt nur den einen Wunsch (`WishService.cs:1078-1090`), `ExtendAsync` schreibt `wish.extend` mit `previousExpiresAtUtc` (`:448-465`) | 2.6 `approve`: beide Zeilen in Reihenfolge der ID gesperrt, Zielwunsch erneut geprüft, die Verlängerungslogik und ihr Audit wiederverwendet; T6 |
| Sol-P2-10 Image-Rollback ungesichert | hält: `ChannelIdentityService.cs:974-976,1072-1078` ohne Feature-Schalter | **Weg (b):** 2.8a, Platzhalter statt nullbarer Pflichtspalten; T1, T13 schrumpfen; Rückweg-Probe C6 |
| Sol-P2-11 Live-Gate kann AK70 verfehlen und am Limit scheitern | hält: Revision 0 hatte K1 vor A14 schon bei drei offenen Wünschen, und C2 erwartete das Löschen eines unabgelaufenen Kandidaten | Abschnitt 7 neu: B-I als Freigabekriterium [B], Zählung je Konto, Vorbedingungen vor A14 und B4, C2 mit Ablaufzeitpunkt und ohne `GET`; AK67 nach V10 |
| Sol-P3-12 Bearbeiten-Dialog blockiert die Laufzeitänderung ohne Emote | hält: `wish-edit-dialog.ts:38-46,156-207` prüft den Alias bei jedem Absenden | 2.10 und T14: ohne Emote nur das Feld Laufzeit; Spec im Dialogtest, E13 |

Dazu aus den Betreiber-Antworten: F1 bis F4 entschieden (Abschnitt 11), Probe-Kanal und Checkliste V1 bis V10 (Abschnitt 7).
**Widerlegt wurde kein Befund.** Geänderte Tasks: T1 (ohne Nullbarkeit, mit zwei Kennungen und Nachweis-Tabelle), T4, T5, T6,
T7 (neun statt sieben Codes, Admin-Health), T9, T11 (ohne Vorfilter und Signal, mit Health-Feld), T13 (ohne
Nullbarkeits-Folgen), T14, T15, T16 (Zeile in `/admin/monitoring`), T17, T18.

---

## 0. Befund vor dem Plan

Alle Stellen am Stand `264db942` nachgelesen.

1. **TwitchLib liefert den Tag als typisierte Eigenschaft.** `TwitchLib.Client` 4.0.1 (`EmotePurge.Worker.csproj:12`),
   `TwitchLib.Client.Models` 4.0.1: Der Konstruktor von `ChatMessage` hat einen eigenen Zweig für `custom-reward-id` und setzt
   `ChatMessage.CustomRewardId` (`string?`, null ohne Tag). Der Tag erscheint deshalb **nicht** in `UndocumentedTags` (dort
   landen nur Schlüssel ohne eigenen Zweig). Ebenfalls typisiert: `Id` (Tag `id`), `TmiSent` (`tmi-sent-ts`), `RoomId`,
   `UserId`, `Username`, `Message`. Per Dekompilat geprüft (`ilspycmd`, Typ `TwitchLib.Client.Models.ChatMessage`). Die
   Quell-Marker des Shared Chat sind dagegen untypisiert und kommen weiter über `UndocumentedTags`
   (`SharedChatRuleTwitchLibTests`). [W1]
2. **Der Handler hat die Reihenfolge, die die Spec nennt.** `TwitchChatManager.OnMessageReceived`
   (`src/EmotePurge.Worker/TwitchChatManager.cs:1041`): Lebenszeichen (`:1050-1054`), Ausschluss-Filter (`:1063`),
   `stats.RecordChatLine()`, Splice-Wächter (`:1074-1087`), Rückkehr bei leerem Snapshot (`:1089-1094`),
   `SharedChatRule.FromTags` (`:1103`), Bot-Einordnung, Zählung. Der Handler läuft inline aus TwitchLibs Lese-Schleife; er ist
   heute synchron, ohne `await`, ohne Sperre, ohne I/O. Der Watchdog misst an `OnSendReceiveData` (`:1024`,
   `_lastFrameReceivedUtcTicks`), nicht am Nachrichten-Handler. Der Client wird nur in `ReconnectOnceAsync` ersetzt; die
   Handler hängen über `WireUpClient` am jeweils aktuellen Objekt.
3. **Der Worker schreibt Nutzung nur gebündelt.** `EmoteUsageCounter` zählt im Speicher, `UsageFlushWorker` schreibt alle
   30 Sekunden (`FlushInterval`), legt einen gescheiterten Stapel bis zu fünfmal zurück und macht beim Stoppen einen letzten
   Flush mit drei Versuchen. Sofort geschrieben wird im Worker heute nur außerhalb der Lese-Schleife (Sync, Ablauf-Lauf,
   Roster nach Redis).
4. **Fünfzehn Hosted Services**, der letzte ist `WishExpiryWorker` (`WorkerServiceRegistration.cs:54-74`). Der Host stoppt in
   umgekehrter Reihenfolge der Registrierung.
5. **Der Abgleich kennt `pending` noch nicht.** `WishReconcileService.ReconciledStatuses` ist `approved`, `active`,
   `presented` (`:54`); `ReconcileUnlinked` hat Zweige für `approved`, `removed_externally` und `active` ohne Slot
   (`:356-389`); `WishSlotMatcher.Match` liefert für `pending` `None` (`WishSlotMatcher.cs:74-80`). `Link` schreibt keinen
   Audit-Eintrag. Die Übergangstabelle kennt die K6-Zeilen schon (`WishTransitions.cs`: `Capture`, `Approve`, `Reject`,
   `Expire`, `MergeIntoRunning`, `Unlink` mit `IrcAutoLinked`, `Adopt`/`Link`/`Edit` aus `pending`).
6. **Die Hinweis-Konstanten sind da, ihr Speicherort nicht.** `WishStatusReason.PendingHints` (`WishStatus.cs`) und der
   TS-Typ `WishStatusReason` führen `ambiguous_match`, `already_in_set`, `already_running`, `unparsed`. Die Spec sagt nicht,
   in welcher Spalte der Hinweis steht. [W8]
7. **`Wish.SevenTvEmoteId`, `EmoteName`, `Alias` sind nicht nullbar**, in der Entität (`Wish.cs:16-22`, vorbelegt mit
   `string.Empty`), im `WishDto` (`IWishService.cs`) und im TS-Modell (`core/wishes/wish.model.ts`). Die Spec macht alle drei
   nullbar; dieser Plan nicht (2.8a). [W13] [W24]
8. **Die Entität trägt vier Spalten über die Spec-Tabelle 6.1 hinaus:** `LinkOrigin`, `SlotAddedAtUtc`,
   `SlotConfirmedAtUtc`, `FirstActivatedAtUtc`. Die Feldliste von `unlink` in der Spec kennt nur die erste. [W9]
9. **`view=open` sortiert `pending` ans Ende.** `WishService.ListAsync` (`:707-709`) ordnet nach `ExpiresAtUtc` aufsteigend;
   ein offener Wunsch hat keinen Ablauf (Postgres: NULL zuletzt). Die Statusmenge von `Open` enthält `pending` schon
   (`:1250-1252`), ebenso der Zähler (`WishOverviewService.cs:75`). [W22]
10. **`Channel.TwitchChannelId` ist nullbar** (`Channel.cs:6`) und eindeutig (`AppDbContext.cs:43`). Für eine Zeile ohne ID
    entscheidet `ChannelAccessService.IsBroadcaster` am Login (`:68-79`). Die Beobachtung der Spec trägt nur `roomId`. [W5]
11. **Retention und Kontolöschung kennen `pending` als hängenden Zustand** (`DataRetentionService.cs:90-95`,
    `RequesterDue`), leeren aber nur `RequesterTwitchUserId` und `RequesterLogin`.
12. **Die Zusammenführung verschiebt Wünsche und Einstellungen** (`ChannelIdentityService.MoveWishesAsync`, `:1072`), der
    Verlierer wird danach gelöscht; alles mit Kaskade auf `ChannelId` ginge sonst verloren.
13. **Die Api-Gruppe und der Vorgaben-Filter stehen.** `WishEndpoints.cs:20-23` (Namensvalidierung, Verwalter-Filter), 404
    `wishes_disabled` nach dem Filter (`:441`), `WishSettingsAuthorizationFilter` an `PUT …/settings`. `/permissions` liefert
    `wishesEnabled` (`ChannelEndpoints.cs:130`).
14. **Die Oberfläche aus K3:** `core/wishes/wish-view.ts` (`wishActions` gibt für `pending` nichts zurück, `needsAction`
    zählt es), `features/wishes/wish-row.ts`, `channel-wishes-page.ts`, `features/channel-workspace/wish-settings-section.ts`,
    `core/seven-tv/seven-tv-emote-lookup.ts` (Einzel-Read aus dem Browser), `core/wishes/wish-input.ts` (Link-Parser). Die
    E2E-Dateien liegen unter `web/e2e/` (`wishes.e2e.spec.ts`, `wishes-seven-tv.e2e.spec.ts`).
15. **Compose kennt nur `WISHES_ENABLED` und `WISHES_EXPIRY_INTERVAL_MINUTES`** (`docker-compose.yml:122,201,203`,
    `docker-compose.prod.yml:126,204,206`, `.env.example:68-70`). `WishOptions` (`Core/Services/WishOptions.cs`) hat keinen
    Erfassungsteil.
16. **Worker-Tests zerlegen Rohzeilen mit TwitchLibs eigenem Parser** und verwenden nur erfundene Zeilen, nie aufgezeichneten
    Chat (`SharedChatRuleTwitchLibTests.cs:14-17`). AK47 spricht von einer „aufgezeichneten Rohzeile“. [W3]
17. **Ein Debug-Kommando für den Worker gibt es als Muster** (`DEBUG:TWITCH-RECONNECT`, `Worker.cs:26,316`, hinter
    `Worker:Debug:AllowTwitchReconnectTrigger`). Dieser Plan fügt **kein** weiteres hinzu; die Live-Probe benutzt das
    vorhandene (Abschnitt 7, A13).
18. **Die Spec nennt den eigenen Kanal des Betreibers ohne Kanalpunkte** (Abschnitt 13). Die Einlösungen der Live-Probe
    laufen in einem fremden Probe-Kanal; 7TV-Schreibzugriffe sind nur mit `olaf_olaf_son` erlaubt. Entschieden mit [F1]:
    `olaf_olaf_son` wird dort 7TV-Editor.
19. **Verwalter ist, wer Broadcaster, Twitch-Moderator oder globaler Admin ist** (`ChannelAccessService.cs:15-31`); ein
    7TV-Editor ohne diese Rolle sieht nur die Nutzung (`CanViewUsageStatsAsync`).
20. **`WorkerHealthSnapshot` ist für angehängte Felder gebaut** (`IWorkerHealthReader.cs:19-33`: nullbare Admin-Felder,
    „unbekannt“ für ältere Worker); `/admin/monitoring` liest `GET /api/admin/health`.
22. **Die Moderator-Prüfung vergleicht nur den Login** (`ModeratorCheckService.cs:34`), obwohl die Liste die `BroadcasterId`
    trägt (`ModeratedChannelsProvider.cs:72,114`). `/mine` und `/summary` fragen je Kanal `CanManageChannelAsync` mit dem
    Namen (`WishOverviewService.cs:124-131`).
23. **Eine Umrechnung der 7TV-ID-Formen gibt es nur im Web** (`canonicalSevenTvEmoteId`, DECISIONS K3 (m)); das Backend
    prüft nur die Form.
24. **Der Host stoppt mit 30 Sekunden Budget** (`ShutdownTimeout` unverändert; Docker hält 60 s, `docker-compose.yml:143-151`),
    in umgekehrter Reihenfolge der Registrierung und nacheinander; der letzte Nutzungs-Flush liegt in diesem Budget
    (`UsageFlushWorker.StopAsync`).
21. **Ein Delta legt neue Slots mit dem Beobachtungszeitpunkt an** (`EmoteSlotDeltaPlanner.cs:125-127,161-172`); der
    Abgleich bestätigt ein späteres maßgebliches, früheres Datum ohne Fensterprüfung (`WishReconcileService.cs:342-346`).

---

## 1. Umfang

### 1.1 Drin

Migration `AddWishCapture` (Spec 6.6, mit den Abweichungen aus 2.4 und 2.8a), `Wishes:Capture:*`; `WishTextParser`; `pending` und
Namens-Treffer in `WishSlotMatcher` und im Abgleich samt Hinweisen und `wish.autoLink`; Erfassungsdienst (Kandidat, Wunsch,
Dedupe, Grenzen, Verfall, Aufräumen, Lücken); Verwaltung von Anlernen und Belohnungen; `approve`, `reject`, `unlink`,
`adopt`/`PATCH` für offene Wünsche; Routen, Fehlercodes, `wishCaptureEnabled`; Retention, Kontolöschung, Purge,
Kanal-Zusammenführung; im Worker Parser, Reihenfolge-Funktion, Warteschlange, Andocken im Manager, `WishCaptureWorker`,
Lückenquelle, Health-Feld samt Zeile im Admin-Monitoring; Oberfläche (Bereich „Offen“, Freigabe-Dialog, Ablehnen, Zuordnung lösen, Belohnungsliste, Anlernen,
Lückenanzeige); i18n, Audit-Aktionsnamen, E2E, Audit-Harness-Szenarien, Doku, Live-Verifikation.

### 1.2 Draußen (ausdrücklich nicht K6)

- **Bot-Modus** (K4, K5): Nach `approve` fügt in K6 niemand automatisch hinzu; `IWishBotAvailability` bleibt
  `WishBotNeverAvailable`. Die Spec-Zeile „im Bot-Modus fügt danach der Bot hinzu“ ist K4.
- **EventSub, Chat-Antworten, Helix-Rückerstattung, Belohnung selbst anlegen, Belohnungen ohne Texteingabe** (Spec 15).
- **Globaler Admin-Hinweis bei verschwundenem Tag** (X7). K6 zeigt „zuletzt erkannt“ und Lücken je Kanal.
- **Audit-Details der Wunsch-Einträge in der Oberfläche** (Plan-485 Befund 3): K6 liefert die sieben neuen Aktionsnamen,
  kein Detail-Kind.
- **Deploy und Einschalten.** `WISHES_ENABLED` und `WISHES_CAPTURE_ENABLED` bleiben auf Prod aus. Der Datenschutztext
  (Spec 6.5) ist Voraussetzung fürs Einschalten, nicht für den Merge, und liegt beim Betreiber.
- **Die Fensterrechnung der Nutzung** (Live-Protokoll #484 Befund 4) bleibt K4.
- **Ein Debug-Auslöser zum Einspielen von IRC-Zeilen.** Nicht gebaut (Befund 17).

### 1.3 Was K6 den anderen Kindern liefert

- **K4:** `approved` kann aus `pending` stammen (`Source = irc`); das Zuordnungsfenster solcher Wünsche beginnt bei
  `CreatedAtUtc` (2.5). `wish.autoLink` existiert und trägt `source`. `WishSlotExclusions` gilt für jeden Abgleichsweg.
  Ein offener Wunsch kann ohne Emote sein (leere ID, 2.8a): Der Bot fragt `HasEmote`, bevor er schreibt. Eine automatische
  Verknüpfung kann bei der Datumskorrektur wieder gelöst werden (2.5); der Bot darf an einem vorläufigen Start nichts
  festmachen. `WorkerHealthSnapshot.WishCaptureActive` ist das Muster für einen Bot-Zustand im Admin-Monitoring.
- **K5:** `wishActions` kennt `pending` je Hinweis; die Zeile rendert Einlösetext, Zuschauer und Belohnung.

---

## 2. Verträge, die mehrere Tasks teilen

### 2.1 Wie die Erfassung am Manager andockt (T10)

**Die Lese-Schleife bekommt genau eine Null-Prüfung und im Trefferfall eine nicht blockierende Übergabe.** Verbindlich:

- **Eine Stelle, im bestehenden Handler.** Kein zweiter Handler auf `OnMessageReceived`, kein neuer Client, keine Änderung
  an `WireUpClient`, `CreateClient`, `ReconnectOnceAsync`, an den Signalen oder am Watchdog-Vertrag. Der Manager bleibt bei
  genau einem `TwitchClient` zur Zeit.
- **Reihenfolge im Handler:** Lebenszeichen (unverändert zuerst) → Ausschluss-Filter → `RecordChatLine` → Splice-Wächter →
  **Erfassung** → Rückkehr bei leerem Snapshot → wie bisher. Die Erfassung steht hinter dem Splice-Wächter, damit sie dessen
  Ergebnis mitbenutzt, und vor der Cache-Rückkehr (Sol-Befund 7 der Spec).
- **Kosten im Normalfall:** `e.ChatMessage.CustomRewardId is null` → weiter. Kein Zugriff auf `UndocumentedTags`, keine
  Allokation. Nur bei gesetztem Tag wird die Raum-Einordnung berechnet (`SharedChatRule.FromTags`) und der Parser gerufen.
- **Schalter:** Der Manager liest beim Bau einmal `WishOptions` und behält ein `bool` (wirksam = `Enabled &&
  Capture.Enabled`, 2.8). Aus → der Zweig wird nie betreten.
- **Übergabe:** `IRewardObservationSink.TryEnqueue(RewardObservation)` (Worker, Singleton, konkret `RewardObservationQueue`).
  Synchron, wartet nie, wirft nie. Begrenzt auf 1.000 Einträge; ist die Warteschlange voll, wird die neue Beobachtung
  verworfen und gezählt (`WorkerStats`). **[P]** Die Grenze wird so gebaut, dass das Verwerfen beobachtbar ist: Ein
  `System.Threading.Channels`-Kanal im Modus `DropWrite` meldet `TryWrite` auch beim Verwerfen als Erfolg; entweder der Modus
  `Wait` mit `TryWrite` (liefert bei vollem Kanal falsch) oder der Rückruf für verworfene Einträge. Ein Test hält das fest.
- **Ausnahmefreiheit:** Der Erfassungszweig steht in einem eigenen `try/catch`, das nur zählt (`RecordCaptureFault`), nie
  loggt mit Nachrichteninhalt und nie weiterwirft. Eine Ausnahme im Handler träfe sonst TwitchLibs Ereigniskette; die
  Emote-Zählung der Nachricht läuft danach unverändert weiter (AK49).
- **Gespleißte Zeilen (#114) ergeben keine Beobachtung [E] [W23].** Meldet `IrcLineSpliceRule.IsSpliced` die Zeile, wird
  der Tag nicht ausgewertet und der Fall gezählt. Eine gespleißte Zeile kann Tags und Text zweier Nachrichten mischen; ein
  Wunsch mit falschem Zuschauer oder Text ist schlimmer als eine verpasste Einlösung (die fällt unter RR11).
- **Kein Log mit Text, Login oder Nutzer-ID** an irgendeiner Stelle dieses Wegs (AK64). Zähler landen in der
  Minuten-Zeile des `WishCaptureWorker` (2.3).

**`RewardRedemptionParser` (Worker, rein, TwitchLib-frei).** Eingabe sind einfache Werte, nicht das TwitchLib-Objekt:
`rewardId`, `MessageOrigin`, `roomId`, `messageId`, `userId`, `login`, `text`, `sentAtUtc`. Ausgabe: nichts oder
`RewardObservation(RoomId, RewardId, MessageId, UserId, Login, Text, SentAtUtc)` mit den sieben Feldern der Spec (Record in `EmotePurge.Core`,
weil `IWishCaptureService` ihn annimmt). Regeln:

- Nur `MessageOrigin.Own` ergibt eine Beobachtung (`Foreign` und `Indeterminate` nie).
- Formprüfung als Schutz gegen Spleiß-Reste **[E]**: `rewardId` und `messageId` 1 bis 64 Zeichen aus `[A-Za-z0-9-]`,
  `roomId` und `userId` nur Ziffern (1 bis 20), `login` 1 bis 25 Zeichen. Sonst nichts.
- `text` wird auf 500 Zeichen gekürzt und von Steuerzeichen befreit; leerer Text ergibt trotzdem eine Beobachtung
  (`unparsed`).
- **[W5]** Der Kanalname gehört nicht zur Beobachtung: Der Dienst findet den Kanal nur über `TwitchChannelId == RoomId`
  (2.4, Sol-P1-1).

**Reihenfolge als reine Funktion (AK67).** Die Entscheidung „Ausschluss → Erfassung → Cache-Prüfung“ wird als reine Funktion
im Worker gezogen (Form: Eingabe `isExcluded`, `captureEnabled`, `hasRewardTag`, `isSpliced`, `snapshotIsEmpty`; Ausgabe drei
Merkmale `CountLine`, `Observe`, `MatchEmotes`), die der Handler aufruft, es sei denn, der Implementer findet in
`TwitchChatManagerLeaveRaceTests` einen Zugang, der den echten Handler mit einer Rohzeile fährt. Der Plan nimmt die Funktion
an; sie ist auf dem heißen Pfad ein Aufruf ohne Allokation (Rückgabe als `readonly struct` oder Flags).

### 2.2 Schreibweise: sofort je Beobachtung, nicht gebündelt [E]

Die Nutzungszählung bündelt, weil sie viele gleichartige Zähler je Emote und Tag verdichtet und der Tag des Flushs die
Buchung bestimmt. Für Erfassungen gilt das Gegenteil, deshalb schreibt `WishCaptureWorker` **jede Beobachtung einzeln und
sofort**, außerhalb der Lese-Schleife:

- Jede Beobachtung ist eine eigene Zeile mit eigener Eindeutigkeit (`IrcMessageId`), eigenen Grenzen (je Zuschauer, je
  Kanal) und eigenem Ergebnis; ein Stapel müsste bei einem Konflikt zerlegt werden.
- Das Anlernen ist ein Dialog mit einem Menschen und einem 10-Minuten-Fenster: Der Kandidat soll Sekunden nach der
  Einlösung in der Oberfläche stehen (`wish.changed`), nicht bis zu 30 Sekunden später.
- Die Menge ist klein (Einlösungen mit Texteingabe, nicht Nachrichten); eine fremde Belohnung kostet ein Lesen ohne
  Sperre und keine Transaktion (2.4).
- Verlust ist begrenzt und benannt: höchstens der Inhalt der Warteschlange bei einem harten Absturz; ein regulärer Stopp
  leert sie (2.3). Ein Neustart ist ohnehin eine Erfassungslücke.

Redis wird für die Erfassung nicht als Zwischenspeicher benutzt; Redis trägt nur `wish.changed` (bestehender
`IWishChangedPublisher`).

### 2.3 `WishCaptureWorker` (T11): ein Dienst, zwei Schleifen

Hosted Service, konkrete Klasse (Regel 5, Ausnahme), als **letzter** registriert (stoppt damit als erster; der Log-Schreiber
stoppt weiter zuletzt). Der Worker hat danach **sechzehn** Hosted Services (AK65).

**Der Worker ist für die Erfassung maßgeblich (Sol-P1-2, [B]).** Ob erfasst wird, entscheidet allein
`WishOptions.CaptureActive` **im Worker-Prozess**; der Schalter der Api blendet nur Routen und Oberfläche aus. Es gibt keinen
gemeinsamen Laufzeitschalter in Datenbank oder Redis. Damit der Zustand ohne die Oberfläche sichtbar ist:

- **Startlogzeile, immer genau eine, auf Information:** `Wish capture is enabled.` bzw. `Wish capture is disabled
  (Wishes:Enabled={…}, Wishes:Capture:Enabled={…}); housekeeping keeps running.` Sie steht damit auch im Log-Viewer
  (`/admin/logs`, Quelle Worker).
- **Worker-Health:** `WorkerHealthSnapshot` bekommt ein angehängtes, nullbares Feld `WishCaptureActive` (null = älterer
  Worker, wie die Felder vom 2026-07-31); `GET /api/admin/health` gibt es aus, zusammen mit dem Wert der Api
  (`apiWishCaptureEnabled`). `/admin/monitoring` zeigt eine Zeile „Wunsch-Erfassung“ nur, wenn einer der beiden Werte wahr ist,
  und markiert sie als Abweichung, wenn Api und Worker sich widersprechen. Das ist der Ort, an dem der Betreiber „Api aus,
  Worker erfasst weiter“ sieht; der Settings-Abschnitt ist in diesem Zustand ausgeblendet.

1. **Haushalts-Takt** (E26): läuft **immer**, auch bei `Wishes:Enabled = false` und `Wishes:Capture:Enabled = false`. Erster
   Takt direkt beim Start, danach jede Minute. Je Takt `IWishCaptureService.RunHousekeepingAsync`: abgelaufene Anlern-Zeilen
   löschen; Dedupe-Nachweise älter als 7 Tage löschen; jede fünfzehnte Minute (und im ersten Takt) offene Wünsche jenseits
   der Verfallsfrist nach `expired`. Bei wirksamer Erfassung zusätzlich: den Bestand der Lücken-Kanäle neu lesen (2.7), die
   Minuten-Abtastung des Trackers, geschlossene Lücken schreiben, den Lebensstempel schreiben. **Vor** dem ersten Schreiben
   liest der Dienst den alten Lebensstempel und hält ihn im Speicher. Ein Fehlschlag wird höchstens einmal je Stunde gewarnt
   (Ausnahmetyp und SQLSTATE wie `WishExpiryWorker.DescribeFailure`), damit ein Betrieb ohne angewendete Migration nicht jede
   Minute warnt.
2. **Abarbeiten der Warteschlange:** nur bei wirksamer Erfassung. Ein einzelner Verbraucher, strikt der Reihe nach, jede
   Beobachtung geht an `IWishCaptureService.ProcessAsync`. **Es gibt keinen Vorfilter aus einer Momentaufnahme im Speicher
   und kein Kommando `WISHCAPTURE:` (Sol-P2-5, abgewandelt, [W25]).** Revision 0 sah beides vor; eine veraltete
   Momentaufnahme hätte die erste Einlösung nach Start, Bestätigen, Reaktivieren oder einer Zusammenführung verworfen, bevor
   die maßgebliche Prüfung sie sah. Stattdessen entscheidet der Dienst jede Beobachtung an der Datenbank: erst ein Lesen ohne
   Sperre (2.4, Schritt 1), die Kanalsperre nur, wenn etwas zu schreiben ist. Die Menge trägt das: Es sind Einlösungen mit
   Texteingabe, nicht Chat-Nachrichten, und die Warteschlange begrenzt den Rückstau. Scheitert der Dienst an der Datenbank:
   bis zu drei Versuche (Pausen 1 s, 5 s, 15 s) an derselben Beobachtung, danach verwerfen und zählen.

**Stoppen (Sol-R2-P2-8).** Der Dienst stoppt als erster und teilt sich die 30 Sekunden des Hosts mit allen anderen, vor
allem mit dem letzten Nutzungs-Flush. Deshalb: (1) Als Erstes schließt er die Senke; `TryEnqueue` weist ab dann ab und
zählt („rejected at shutdown“), der Handler bleibt dabei ausnahmefrei. (2) `base.StopAsync`. (3) Leeren mit einem **eigenen
Teilbudget von 5 Sekunden** (Konstante, an das Stopp-Token des Hosts gekoppelt, ein Versuch je Beobachtung, keine
Wiederholungspausen); das Budget bricht auch eine laufende Datenbank-Anweisung ab. (4) Was übrig ist, wird gezählt und in
einer Zeile geloggt (nur die Zahl) und verworfen. Verlorenes liegt in der Neustart-Lücke.

**Logzeile:** einmal je Minute und nur bei Werten ungleich null: angelegt, Kandidaten, Dubletten, über der Grenze je
Zuschauer, über der Kanalgrenze (dieser Wert höchstens einmal je Stunde als Warnung), ungebundener Kanal, Überlauf,
gespleißt, fehlgeschlagen. Nur Zahlen (AK53, AK64).

### 2.4 `IWishCaptureService` (T5) und `IWishRewardService` (T4) [E]

Die Spec nennt einen Dienst `IWishCaptureService`. Der Plan schneidet zwei Interfaces in `Core/Services/`, weil die eine
Hälfte nur der Worker ruft und die andere nur die Api (Regel 4, 5); eine Implementierungsdatei je Interface.

**Kanalbindung (Sol-P1-1, [B], ersetzt den Namens-Rückweg aus Revision 0).** `ChannelAccessService.IsBroadcaster`
(`:68-79`) lässt für eine Kanalzeile ohne `TwitchChannelId` den Login-Vergleich gelten; ein von Twitch neu vergebener Name
bekäme sonst Text und Login des früheren Kanals zu lesen. Deshalb, ohne Ausnahme:

- **Erfasst und angelernt wird nur in einem Kanal, dessen `TwitchChannelId` gesetzt ist und der IRC-`RoomId` gleicht.** Der
  Kanal wird ausschließlich über `TwitchChannelId == RoomId` gefunden (eindeutiger Index, `AppDbContext.cs:43`), nie über
  den Namen. Die Beobachtung hat wieder die sieben Felder der Spec.
- **Die Bindung wird nach dem Erwerb der Kanalsperre erneut gelesen** und mit der `RoomId` verglichen; erst dann wird
  geschrieben. Der `TwitchIdentityReconcileWorker` kann die ID gleichzeitig nachführen oder die Zeile zusammenführen.
- **Anlernen in einem ungebundenen Kanal:** `POST …/rewards/learning` antwortet 409 `wish_channel_unbound`; `GET …/rewards`
  liefert `channelBound: false`, und die Oberfläche erklärt, dass der Kanal erst nach dem nächsten Sync anlernbar ist.
- **Herausgabe:** nach 2.4a. Eine Kanalzeile ohne `TwitchChannelId` gibt nie Einlöserdaten heraus; mit der ersten Regel
  kann sie auch keine erfassten haben. Die Prüfung ist die zweite Wand und wird trotzdem getestet.

**`IWishCaptureService`** (Worker):

| Methode (Form) | Vertrag |
|---|---|
| `ProcessAsync(RewardObservation)` → Ergebnis (`CandidateStored`, `WishCreated`, `Duplicate`, `RequesterLimit`, `ChannelLimit`, `ChannelUnbound`, `Discarded`) | Schritte unten |
| `RunHousekeepingAsync(includePendingExpiry)` → Zählwerte | unabhängig von beiden Schaltern |
| `ListGapChannelsAsync()` → Kanäle (ID, Twitch-ID, Name) | der persistente Bestand für 2.7: `IsBotActive`, nicht deaktiviert, gebunden, mindestens eine aktive Belohnung |
| `ReadLastAliveAsync()`, `WriteLastAliveAsync(now)` | 2.7 |
| `RecordGapsAsync(Lücken)` | nur Kanäle mit mindestens einer aktiven Belohnung zum Schreibzeitpunkt; unter 60 Sekunden nichts |

`ProcessAsync`, in dieser Reihenfolge:

1. **Lesen ohne Sperre:** Kanal über `TwitchChannelId == RoomId`. Kein Kanal → `ChannelUnbound` (gezählt). Kanal
   deaktiviert → `Discarded`. Dann die Belohnung des Kanals mit dieser `RewardId` und die Anlern-Zeile. Belohnung pausiert →
   `Discarded`. Belohnung unbekannt und keine unabgelaufene Anlern-Zeile ohne Kandidat → `Discarded`, **keine Transaktion,
   keine Zeile in irgendeiner Tabelle** (AK51).
2. **Transaktion, Kanalzeile gesperrt** (`RowLocks.LockChannelsByIdsAsync`, wie `CreateAsync`). Danach `TwitchChannelId`
   der gesperrten Zeile erneut gegen `RoomId` prüfen (ungleich oder leer → `ChannelUnbound`, Rollback) und Belohnung und
   Anlern-Zeile erneut lesen; entschieden wird nur auf diesem zweiten Stand.
3. **Dedupe-Nachweis (Sol-P2-6):** Zeile in `WishCaptureReceipts` (`ChannelId`, `IrcMessageId`, `SeenAtUtc`; PK auf den
   ersten beiden, Kaskade am Kanal) einfügen. Existiert sie → `Duplicate`, Rollback. Der Nachweis ist inhaltsfrei (weder
   Text noch Login noch Nutzer-ID), wird für **Kandidat und Wunsch** in derselben Transaktion geschrieben und hängt an
   keinem Wunsch: Die Kontolöschung und die Retention lassen ihn stehen, der Haushalts-Takt löscht ihn nach 7 Tagen. Damit
   legt dieselbe IRC-Zeile weder nach dem Bestätigen des Anlernens noch nach einer Kontolöschung einen zweiten Wunsch an.
4. **Unbekannte Belohnung:** Anlern-Zeile mit `ExpiresAtUtc` > jetzt, ohne Kandidat, und `SentAtUtc` nicht mehr als
   10 Sekunden vor `StartedAtUtc` → Kandidat setzen (`CandidateId` neu gewürfelt, `CandidateRewardId`, `CandidateText`,
   `CandidateLogin`, `CandidateSeenAtUtc`), `CandidateStored`. Sonst Rollback, `Discarded` (auch der Nachweis entfällt).
5. **Bekannte, aktive Belohnung:** offene Wünsche dieses Zuschauers im Kanal ≥ `MaxPendingPerRequester` → `RequesterLimit`;
   offene Wünsche im Kanal ≥ 200 → `ChannelLimit` (beide mit Rollback, damit eine spätere Wiederholung nicht als Dublette
   zählt, die nie ein Wunsch war). Sonst `WishTextParser` auf den Text, Zeile einfügen: `Status = pending`
   (`WishTransitions.TargetForNew(Capture)`), `Source = irc`, `DurationDays` = `DefaultDurationDays` **der Belohnung**
   (AK52), `RewardId`, `IrcMessageId`, `RequestText`, `RequestedName`, `MatchKind`, `SevenTvEmoteId` nur bei `link`, sonst
   der Platzhalter aus 2.8a, `EmoteName` und `Alias` als Platzhalter, Einlöser-ID und Login, `StatusReason = unparsed` bei
   `none`, **`CreatedAtUtc` = `SentAtUtc`**, nach oben auf „jetzt“ begrenzt **[E]**, `StatusChangedAtUtc` = jetzt.
   `LastRedemptionSeenAtUtc` der Belohnung setzen. Kein Audit-Eintrag fürs Anlegen (die Spec nennt keinen).
6. Nach dem Commit: `wish.changed`, bei einem neuen Wunsch danach `IWishReconcileTrigger.TriggerAsync(channelId)`, damit
   Hinweise sofort stehen und ein schon vorhandener passender Slot im Fenster sofort verknüpft wird. Ein Fehlschlag des
   Anstoßes ändert das Ergebnis nicht (der nächste Sync heilt).

`RunHousekeepingAsync`: (a) Anlern-Zeilen mit `ExpiresAtUtc` ≤ jetzt löschen; (b) Dedupe-Nachweise älter als 7 Tage
löschen; (c) bei `includePendingExpiry` je Kanal mit offenen Wünschen: `pending` mit `StatusChangedAtUtc` < jetzt −
`PendingExpiryDays` (Kanalwert, sonst 14) per bedingtem Update nach `expired`, `EndedAtUtc` = jetzt, `StatusReason = null`,
in Batches; je betroffenem Kanal ein `wish.changed`. Kein Audit-Eintrag **[P]**. Die Sperrfolge „Kanal vor Wunsch“ gilt auch
hier (Abschnitt 10, R7).

**`IWishRewardService`** (Api):

| Methode (Form) | Antwort und Fehler |
|---|---|
| `GetAsync(channel, callerId, isAdmin)` | `WishRewardsDto` (unten); löscht eine abgelaufene Anlern-Zeile im selben Aufruf |
| `StartLearningAsync(channel, actor)` | 201 mit Anlern-Stand samt neuer `sessionId`; 409 `wish_channel_unbound`; 409 `wish_learning_active` (unabgelaufene Zeile); 409 `wish_reward_limit` schon beim Start, wenn 10 Belohnungen bestehen **[E]**; eine abgelaufene Zeile wird ersetzt |
| `ConfirmLearningAsync(channel, sessionId, candidateId, label, defaultDurationDays, actor)` | 201 mit Belohnung; **409 `wish_learning_stale`**, wenn die liegende Zeile eine andere `SessionId` oder einen anderen `CandidateId` trägt; 409 `wish_reward_no_candidate` (keine Zeile, abgelaufen oder ohne Kandidat), 409 `wish_reward_already_known`, 409 `wish_reward_limit`; 400 `invalid_request_body` (Kennungen fehlen, Bezeichnung leer oder über 50 nach Trim), 400 `wish_duration_out_of_range` |
| `CancelLearningAsync(channel, sessionId, actor)` | 204; **409 `wish_learning_stale`**, wenn eine Zeile mit anderer `SessionId` liegt; liegt keine, 204 (idempotent) |
| `UpdateRewardAsync(channel, id, label?, defaultDurationDays?, isActive?, actor)` | 200; 404 `wish_reward_not_found`; nur gesendete Felder ändern sich |
| `DeleteRewardAsync(channel, id, actor)` | 204; 404 `wish_reward_not_found`; `Wishes.RewardId` wird per SET NULL geleert |

- **Sitzung und Kandidat sind gebunden (Sol-P2-4).** `WishRewardLearnings` bekommt über die Spec hinaus `SessionId` (uuid,
  beim Start gewürfelt) und `CandidateId` (uuid, mit dem Kandidaten gewürfelt). Beide sind undurchsichtig und sagen nichts
  über Belohnung oder Zuschauer. Die Oberfläche schickt beim Bestätigen genau die Kennungen des **angezeigten** Kandidaten
  und beim Abbrechen die der angezeigten Sitzung; ein Dialog, hinter dem ein anderer Verwalter abgebrochen und neu
  gestartet hat, bekommt 409 und lädt neu. Die Zustimmung aus AK50 und AK69 hängt damit am Gesehenen.
- **Prüfung und Änderung sind eine Anweisung (Sol-R2-P2-5).** Keine Stelle liest die Anlern-Zeile, entscheidet im Speicher
  und schreibt dann über den Primärschlüssel. Jede Änderung ist **eine bedingte Anweisung**, deren Bedingung die erwartete
  Zeile benennt, und die Zahl der getroffenen Zeilen ist das Ergebnis: Bestätigen löscht mit `SessionId` **und**
  `CandidateId` **und** `ExpiresAtUtc` > jetzt (0 Zeilen → 409 `wish_learning_stale` bzw. `wish_reward_no_candidate`, je
  nachdem, was danach liegt; die Belohnung entsteht nur bei 1 Zeile, in derselben Transaktion); Abbrechen löscht mit
  `SessionId`; das Aufräumen in `GET` und im Haushalts-Takt löscht mit `SessionId` der gelesenen Zeile **und**
  `ExpiresAtUtc` ≤ jetzt bzw. nur mit der Ablaufbedingung; der Start ersetzt eine abgelaufene Zeile mit derselben
  Bedingung und fügt sonst ein (Konflikt am Primärschlüssel → 409 `wish_learning_active`). Der Worker setzt den Kandidaten
  mit `SessionId` der unter der Kanalsperre gelesenen Zeile und `CandidateId IS NULL` in der Bedingung. Ein `GET`, der die
  abgelaufene Sitzung A gelesen hat, kann damit eine inzwischen gestartete Sitzung B nicht löschen.
- Jede Methode, die den Anlern-Zustand liest, behandelt `ExpiresAtUtc` ≤ jetzt als nicht vorhanden und löscht die Zeile.
- `Confirm` löscht die Anlern-Zeile in derselben Transaktion. **Die Einlösung, mit der angelernt wurde, wird kein Wunsch
  [P]**; ihr Dedupe-Nachweis (Schritt 3) hält auch eine erneut zugestellte Zeile davon ab. Die Oberfläche sagt das.
- Jede schreibende Methode sendet nach dem Commit `wish.changed`. Ein Kommando an den Worker gibt es nicht (2.3).
- Audit: `wish.rewardAdd`, `wish.rewardChange`, `wish.rewardRemove` unter dem Verwalter, Details nur `rewardRowId` und bei
  `rewardChange` die Namen der geänderten Felder; **ohne** Bezeichnung, Text, Login. Start und Abbruch des Anlernens ohne
  Audit **[P]**.
- **`{rewardId}` der Route ist die Zeilen-ID** (`long`) **[E] [W18]**; der Tag-Wert von Twitch verlässt die Api nicht.

`WishRewardsDto`: `channelBound`, `rewards[]` (`id`, `label`, `defaultDurationDays`, `isActive`, `createdAtUtc`,
`lastRedemptionSeenAtUtc`), `limit` (10), `learning` (null oder `sessionId`, `startedAtUtc`, `expiresAtUtc`, `candidate`:
null oder `candidateId`, `text`, `login`, `seenAtUtc`), `gaps[]` der letzten 14 Tage (`fromUtc`, `toUtc`, `reason`),
`canEdit` (dieselbe Frage wie `WishSettingsAuthorizationFilter`), `pendingExpiryDays`, `captureAliveAtUtc` (2.7).

### 2.4a Wer K6-Daten sehen und ändern darf (Sol-R2-P1-1, [B])

Die Verwalter-Prüfung des Bestands hängt für Moderatoren am Login (`ModeratorCheckService.cs:34`). Ordnet die App einen neu
vergebenen Login noch der alten Kanalzeile zu, läse ein Moderator des **neuen** Kanals Einlösetexte und Logins des alten.
**K6-Daten** sind: `requestText`, `requestedName`, `requesterLogin` eines Wunsches mit `Source = irc`, der Kandidat des
Anlernens, Belohnungen, Lücken.

- **„An der ID bestätigt“** ist ein Aufrufer für eine Kanalzeile, wenn die Zeile eine `TwitchChannelId` trägt **und** er
  globaler Admin ist, **oder** seine Twitch-ID dieser ID gleicht (Broadcaster), **oder** seine Liste der moderierten
  Kanäle einen Eintrag mit `BroadcasterId` gleich dieser ID enthält. Der Login spielt keine Rolle.
- **Neue Frage an `IChannelAccessService`:** `ResolveWishCaptureAccessAsync(principal, channelName)` →
  `(ChannelId, TwitchChannelId, IdConfirmed)` oder nichts. Sie liest **eine** Kanalzeile und entscheidet an genau dieser;
  die Moderatorenliste kommt aus dem bestehenden `IModeratedChannelsProvider` (kein zweiter Helix-Abruf). Ist die Liste
  nicht ermittelbar, gilt wie im Bestand „nein“.
- **Die interne Kanal-ID wird mitgeführt.** Jede Dienstmethode, die K6-Daten liest oder ändert, nimmt die `ChannelId` aus
  dieser Antwort und löst den Namen nicht ein zweites Mal auf. Eine Umbenennung zwischen Prüfung und Abfrage kann damit
  keine andere Zeile treffen als die geprüfte.
- **Belohnungs-Routen** (alle aus 2.9, lesend wie schreibend): neuer `WishCaptureAccessFilter` nach dem Verwalter-Filter.
  Zeile ohne `TwitchChannelId` → 409 `wish_channel_unbound`; nicht an der ID bestätigt → 403 in der Form des
  Verwalter-Filters; sonst legt er die Antwort in `HttpContext.Items`, und der Handler reicht die `ChannelId` weiter.
- **Wunsch-Routen** (Liste, Detail, alle Befehlsantworten, die ein `WishDto` tragen): Zugang wie bisher über den
  Verwalter-Filter, **der Bestand wird nicht stillschweigend geändert**. Der Handler stellt zusätzlich die neue Frage und
  gibt dem Dienst `includeRequestData` mit; ohne Bestätigung an der ID sind die drei Felder eines `irc`-Wunsches leer.
  `rewardLabel` ebenso.
- **`/api/wishes/mine`:** `WishOverviewService` stellt die Frage je Kanal neben seiner bestehenden und leert die Felder
  entsprechend. `/summary` trägt nur Zahlen und bleibt.
- **Befehle an Wünschen** (`approve`, `reject`, `unlink`, `adopt`, `PATCH`) bleiben in der Zugangsprüfung, wie K1 sie
  gebaut hat: Sie legen keine K6-Daten offen (ihre Antworten folgen der Regel oben), und eine strengere Prüfung nur für
  `irc`-Wünsche machte dieselbe Liste halb bedienbar. Dass ein Moderator über den Login an **alle** Verwalter-Funktionen
  eines falsch zugeordneten Kanals kommt, ist ein Bestandsthema und steht im Folge-Issue (Abschnitt 14).

### 2.5 Zuordnung: `pending`, Namens-Treffer, Hinweise (T3)

**`WishSlotMatcher`** bekommt für `pending` eigene Fensterfunktionen und zwei Trefferarten; die Regeln für `approved` und
`removed_externally` bleiben bis auf den markierten Punkt unberührt. Eingabe erweitert um `CreatedAtUtc`, `MatchKind`,
`RequestedName`, das Fensterende und `FirstActivatedAtUtc`.

- **Fenster `pending`:** Beginn `CreatedAtUtc`, ohne Toleranz. Ende = `StatusChangedAtUtc` + `PendingExpiryDays` **[E]**:
  Ein Slot mit `AddedAt` nach dem nominellen Verfall ist kein Kandidat, auch wenn der Haushalts-Takt den Wunsch noch nicht
  nach `expired` gesetzt hat (AK57 gilt damit unabhängig vom Takt).
- **Kandidaten** wie bisher (nicht archiviert, kein ungesetzelter Seed, datiert, von keinem laufenden oder `kept`-Wunsch
  gehalten), zusätzlich ohne die Slots, die für **diesen** Wunsch in `WishSlotExclusions` stehen.
- **Treffer über die Emote-ID** (`MatchKind = link` oder ID bekannt): wie die bestehende `approved`-Regel (ein Kandidat;
  bei mehreren der mit dem Alias des Wunsches; sonst mehrdeutig).
- **Treffer über den Namen** (`MatchKind = name`, keine ID): genau ein Kandidat mit Alias ordinal gleich `RequestedName`;
  gibt es keinen, genau einer ohne Rücksicht auf Groß- und Kleinschreibung (invariant); jede Mehrdeutigkeit ist kein
  Treffer. Verglichen wird ausschließlich mit dem **Alias des Slots**, nie mit `EmoteName` irgendeines Wunsches (Plan-485
  1.2). Bei einem Treffer kommen `SevenTvEmoteId` aus dem Slot, `Alias` aus dem Slot, `EmoteName` aus dem Namen der
  Emote-Zeile des Slots.
- **`MatchKind = none`:** nie ein Treffer. Ein Wunsch ohne Emote (2.8a) nimmt nie am Treffer über die ID teil.
- **`approved` aus der Erfassung [E] [W11]:** Für einen Wunsch mit `Source = irc`, der nie aktiv war
  (`FirstActivatedAtUtc` null), beginnt das `approved`-Fenster beim früheren von `ApprovedAtUtc` und `CreatedAtUtc`. Grund:
  Der Mod fügt in 7TV hinzu und klickt vor dem nächsten Sync „Freigeben“; mit `ApprovedAtUtc` als Beginn läge der Slot
  Sekunden vor dem Fenster und der Wunsch hinge für immer in `approved`. Handeinträge und „Wieder hinzufügen“ behalten
  `ApprovedAtUtc`.

**Abgleich (`WishReconcileService`).** `pending` kommt in die Vorprüfung, in die Sperrmenge und in `ReconcileUnlinked`.

- **Reihenfolge der unverknüpften Wünsche:** aufsteigend nach `CreatedAtUtc`, dann `Id`, über alle Zustände hinweg **[E]**.
  Damit gewinnt der ältere von zwei offenen Wünschen (AK58) und ein älterer Handeintrag vor einer jüngeren Einlösung.
- **Verknüpfen:** `pending → active` (bei `Permanent`: `kept`) über `WishAction.Link`; Start = `AddedAt` des Slots,
  `LinkOrigin = auto`, `BotOwned` falsch, `ApprovedAtUtc` bleibt null, Generation wie beim Bestand (`HoldGeneration`),
  `FirstActivatedAtUtc` wie beim Bestand.
- **`wish.autoLink` [E] [W12]:** ein Audit-Eintrag je automatischer Verknüpfung durch `Link`, unter `AuditActor.System`, im
  selben Savepoint wie der Wunsch, für `pending` **und** `approved` (die Spec sagt „jede“). Beim Verknüpfen werden die
  Platzhalter nach 2.8a im selben Update gefüllt. Details: `wishId`, `source`,
  `fromStatus`, `matchKind`; kein Alias, kein Text, kein Einlöser. Die Pilot-Messgröße (c) zählt die Einträge mit
  `source = irc`. Eine Rückkehr aus `removed_externally` schreibt keinen. Löst die Datumskorrektur die Bindung wieder (unten), bleibt der Eintrag
  stehen; die Messgröße (c) zieht die `wish.unlink` mit `cause: date_correction` ab.
- **Hinweise** stehen in `StatusReason` des offenen Wunsches **[E] [W8]** und werden bei jedem Abgleich neu bestimmt, in
  dieser Rangfolge:

  | Lage | `StatusReason` |
  |---|---|
  | `MatchKind = none` | `unparsed` (bleibt, bis der Wunsch den Zustand verlässt) |
  | mehrere Kandidaten im Fenster, keine Entscheidung | `ambiguous_match` |
  | genau ein Kandidat im Fenster, aber für dessen Emote läuft schon ein Wunsch; oder der Wunsch kennt die ID und für sie läuft ein Wunsch | `already_running` |
  | kein Kandidat im Fenster, aber ein nicht archivierter Slot passt nach ID bzw. Namen mit `AddedAt` vor dem Fensterbeginn | `already_in_set` |
  | sonst | null |

  Ein geänderter Hinweis ist eine sichtbare Änderung und löst `wish.changed` aus; ein gleicher nicht.
- Trifft das Speichern auf den Laufend-Index (`IX_Wishes_ChannelId_SevenTvEmoteId_Running`), bleibt der Wunsch `pending`
  mit `already_running` (bestehender Savepoint-Weg, der Hinweis wird in einem zweiten Speichern gesetzt).
- **Vorprüfung:** Ein Kanal mit mindestens einem offenen Wunsch läuft durch die Transaktion. Das ist beabsichtigt.
- **Datumskorrektur prüft das Fenster erneut, nur für K6-Autozuordnungen (Sol-P1-3, Sol-R2-P2-4, [B]).** Die Spec stützt
  sich in 6.3 darauf, dass ein vorläufiges `AddedAt` „nie vor dem echten liegt und also keinen berechtigten Kandidaten
  ausschließt“. Es kann aber einen unberechtigten **einschließen**: Ein verspätetes Delta trägt den Beobachtungszeitpunkt
  und fällt ins Fenster, obwohl der Eintrag vor der Einlösung im Set lag ([W27]).

  **Geltung.** Die Neuprüfung gilt genau für Wünsche mit `Source = irc`, `LinkOrigin = auto` und `ApprovedAtUtc` null, also
  für das, was der Abgleich ohne jeden Klick aus `pending` verknüpft hat. **Handeinträge aus K1 und freigegebene Wünsche
  behalten ihr bisheriges Verhalten unverändert**; dieselbe Lücke dort ist Folge-Issue (Abschnitt 14).

  **Regel.** Übernimmt der Abgleich für einen solchen Wunsch ein maßgebliches Datum (`ConfirmDate` mit `AddedAtFromRest`),
  prüft er es gegen den Fensterbeginn (`CreatedAtUtc`). Liegt es davor:

  | Lage des Wunsches | Wirkung |
  |---|---|
  | `active`, oder `presented` mit Grund `unconfirmed_start`, und **keine Verlängerung gebucht** (`ExtensionCount` = 0) | zurück nach `pending` über `WishAction.Unlink` (dafür eine neue Zeile der Tabelle aus `presented` mit `unconfirmed_start`), **bevor** Start, Ablauf und `StartsAtAuthoritative` gesetzt werden; Felder wie beim `unlink` des Verwalters, Platzhalter nach 2.8a, `StatusReason = already_in_set`; **keine** Sperrzeile (das Fenster schließt den Slot ohnehin aus); Audit `wish.unlink` unter `AuditActor.System` mit `cause: date_correction`; `wish.changed` |
  | eine Verlängerung ist gebucht (`ExtensionCount` > 0) | **die Bindung bleibt**, die Datumskorrektur läuft wie im Bestand. Mit dem Verlängern hat ein Verwalter diesen Eintrag ausdrücklich als den des Wunsches behandelt; die Zuordnung ist ab da seine Entscheidung wie bei „Bestand übernehmen“, und eine gebuchte Entscheidung wird nie zurückgenommen |
  | `kept` | unberührt (der Abgleich bestätigt für `kept` kein Datum; „Behalten“ ist eine gebuchte Entscheidung) |
  | jeder Endzustand, `pending` | unberührt |

  Das ist die einfachste tragfähige Regel: Entweder hat noch niemand entschieden, dann wird die irrtümliche Bindung
  spurlos gelöst, oder jemand hat entschieden, dann gilt seine Entscheidung. Beide Reihenfolgen sind damit bestimmt:
  Verlängern vor der Korrektur → Bindung bleibt; Korrektur vor dem Verlängern → der Wunsch ist `pending`, das Verlängern
  antwortet 409 `wish_state_conflict`. Beide nehmen die Wunschsperre. Solange das Datum vorläufig ist, sperrt K3
  „Entfernen“ schon (`startsAtAuthoritative` falsch, Live-Protokoll T16 B4), und der Ablauf-Lauf hält vorläufige Starts
  zurück; die Lücke lag genau im Augenblick der Bestätigung. **Restrisiko, benannt (R24):** Ein Wunsch, der im vorläufigen
  Zustand verlängert wurde, kann nach der Bestätigung entfernt werden, obwohl der Eintrag älter ist als die Einlösung.

### 2.5a Eine Form der 7TV-Emote-ID (Sol-R2-P2-3)

7TV kennt zwei Formen derselben ID: die 24-stellige Hex-ObjectID alter Links und die 26-stellige ULID; Sets, Slots und der
Abgleich tragen die ULID in Großschreibung (DECISIONS K3 (m), live gemessen). Ein Link-Wunsch unter der Hex-Form oder einer
kleingeschriebenen ULID träfe seinen Slot nie, und „läuft schon“ sähe ihn nicht.

- **`SevenTvEmoteIdCanonical.Canonicalize(string id)`** in `EmotePurge.Core/SevenTv/` (rein, nur BCL, **kein 7TV-Aufruf**):
  ULID → Großschreibung; ObjectID → ULID nach der in K3 belegten Abbildung (die 48 Bit Zeit der ULID sind die Sekunden der
  ObjectID in Millisekunden, dann 16 Nullbits, dann ihre übrigen 8 Bytes); alles andere unverändert zurück (die
  Formprüfung bleibt bei `SevenTvEmoteIdValidation`).
- **Angewandt, bevor gespeichert oder verglichen wird:** im Ergebnis von `WishTextParser` (die Erfassung speichert nur die
  kanonische Form) und auf `sevenTvEmoteId` im Rumpf von `approve`, vor „läuft schon“, „schon im Set“ und dem Speichern.
- **Nicht in K6:** `POST …/wishes` und `adopt` aus K1. Dort schickt der eigene Client seit K3 schon die kanonische Form;
  die serverseitige Absicherung für fremde Aufrufer ist Folge-Issue (Abschnitt 14).


### 2.6 Befehle an einem offenen Wunsch (T6)

Alle über `WishTransitions`, alle mit Kanal- und Wunschsperre in der Reihenfolge des Bestands, alle mit `DecisionVersion + 1`.
„Kennt die Emote-ID“ heißt überall `HasEmote` (2.8a).

| Befehl | Vertrag |
|---|---|
| `approve` `{ sevenTvEmoteId?, emoteName?, alias?, durationDays? }` | Nur aus `pending`, sonst 409 `wish_state_conflict`. Emote-ID = Rumpf, sonst die des Wunsches; fehlt beides → 400 `wish_emote_required`. ID-Form wie beim Handeintrag (400 `emote_ids_invalid`), danach kanonisch (2.5a). Alias und Name wechselseitig abgeleitet wie in `CreateAsync`; für Namens-Wünsche ist `RequestedName` die Vorbelegung des Alias; fehlt am Ende beides → 400 `wish_alias_invalid`. Laufzeit 1 bis 365, sonst die des Wunsches. **Prüffolge [E]:** (1) läuft für die ID schon ein Wunsch → dieser Wunsch endet als `merged` mit `MergedIntoWishId`, der laufende wird nach E12 verlängert. **Sperre und Audit nach Sol-P2-9:** Kanalzeile, dann **beide** Wunschzeilen in aufsteigender ID in einem Aufruf von `LockWishesByIdsAsync` (dieselbe Zeilensperre, die `ExtendAsync` und der Ablauf-Lauf nehmen, `WishService.cs:1078-1090`, `WishExpiryService.cs:120-131`); **danach** den Zielwunsch neu lesen und erneut prüfen (läuft er noch, hat er einen Ablauf). Die Verlängerung benutzt dieselbe Funktion wie `ExtendAsync` (`WishSchedule.ExtendedExpiresAt`, `ExtensionCount + 1`, `DecisionVersion + 1`, Einreihung verworfen, E25) und schreibt auf dem Zielwunsch `wish.extend` mit `durationDays` und, wenn er überfällig war, `previousExpiresAtUtc`; beide Audit-Einträge und beide Zeilen in einer Transaktion. **Hat der laufende keinen Ablauf (`approved`, `unconfirmed`, `failed` gibt es hier nicht: nicht laufend), lässt sich keine Verlängerung buchen (Sol-R2-P2-7): 409 `wish_running_not_extendable` mit `wishId` des laufenden, nichts wird geschrieben, der offene Wunsch bleibt `pending` mit `already_running` und ist später entscheidbar** (sobald der laufende im Set ist, oder durch Ablehnen). Beim Zusammenführen bekommt der offene Wunsch im selben Update ID, Alias und Namen des Zielwunsches (2.8a). Ist der Zielwunsch nach der Sperre nicht mehr laufend, beginnt die Prüffolge neu bei (2). Antwort 200 mit dem zusammengeführten Wunsch; `WishDto` bekommt dafür `mergedIntoWishId`, und die Antwort trägt zusätzlich `extendedExpiresAtUtc` des Zielwunsches, damit die Oberfläche den tatsächlichen Ausgang nennt. (2) liegt das Emote schon übernehmbar im Set → 409 `wish_emote_already_in_set` mit `slotIds` und `wishId` wie beim Handeintrag; der Ausweg ist „Bestand übernehmen“. (3) sonst `approved`, `ApprovedAtUtc` = jetzt, `StatusReason` null. Audit `wish.approve` auf dem offenen Wunsch (Details `wishId`, `outcome`: `approved` oder `merged`, `mergedIntoWishId`). Zwei gleichzeitige Aufrufe: einmal 200, einmal 409 (AK59). |
| `reject` | `pending → rejected`, `EndedAtUtc` = jetzt, `StatusReason` null. Audit `wish.reject`. |
| `unlink` | Nur `active` mit `Source = irc` und `LinkOrigin = auto` (Kontext `IrcAutoLinked`), sonst 409. Wirkung in einem Update nach der Spec; **zusätzlich [W9]** `SlotAddedAtUtc` und `SlotConfirmedAtUtc` auf null. `FirstActivatedAtUtc` bleibt (ist als unveränderlich definiert). Bei `MatchKind = name` werden `SevenTvEmoteId`, `EmoteName`, `Alias` auf den Platzhalter gesetzt, bei `link` nur `EmoteName` und `Alias` **[P]** (der Slot hatte sie gesetzt). Zeile in `WishSlotExclusions` (`WishId`, `SlotId`), idempotent. Audit `wish.unlink`. Nach dem Commit Abgleich anstoßen (ein anderer offener Wunsch desselben Emotes darf den Slot bekommen, AK61). |
| `adopt` mit `wishId` | Schon in K1/K3. K6: Für einen offenen Wunsch ohne Emote-ID gilt „passt zum Wunsch“ als erfüllt (der Verwalter wählt); ID, Alias und Name kommen aus dem Slot. Für einen mit ID muss der Slot diese ID tragen (sonst wie bisher 404 `wish_slot_not_found`). Eine Sperrzeile für dieses Paar wird gelöscht. `ApprovedAtUtc` bleibt null. |
| `PATCH` | In `pending` erlaubt: `durationDays` immer, `alias` nur, wenn der Wunsch eine Emote-ID kennt, sonst 409 `wish_state_conflict` **[E]** (ein Alias ohne Emote hätte keine Bedeutung für die Zuordnung). Danach Abgleich anstoßen. |

DTO: `WishDto.SevenTvEmoteId`, `EmoteName`, `Alias` bleiben Zeichenketten, leer heißt „noch nicht bekannt“ (2.8a, **[W13]**); neu
`RequestText`, `RequestedName`, `MatchKind`,
`RewardLabel` (null nach dem Vergessen der Belohnung). `LinkOrigin` ist schon da. Die drei Felder aus der Einlösung und `rewardLabel` werden nur einem an der ID bestätigten Aufrufer ausgegeben (2.4a). `ImageUrl` bleibt
die Regel aus K3 (null ohne Emote-Zeile im Kanal); für einen offenen Link-Wunsch liest die Oberfläche Bild und Namen selbst (2.9).
`view=open` ordnet `pending` zuerst (älteste zuerst nach `CreatedAtUtc`), danach wie bisher **[E] [W22]**.

### 2.7 Lebensstempel und Erfassungslücken (T5, T11; Sol-P2-8, abgestuft nach [B])

Das Modell ist bewusst grob: drei Gründe, je Kanal höchstens eine offene Lücke, nichts Feineres.

**Bestand.** Welche Kanäle eine Lücke bekommen können, kommt aus der Datenbank (`ListGapChannelsAsync`, je Takt neu gelesen),
**nicht** aus dem Roster des Managers: Beim Kaltstart füllt `Worker.RunBootRecoveryAsync` (`Worker.cs:141-155`) den Roster
erst nach und nach, während der erste Takt sofort läuft.

**`CaptureGapTracker` (Worker, rein, getestet).** Ereignisse:

| Ereignis | Quelle | Wirkung |
|---|---|---|
| Start | `WishCaptureWorker`, mit dem alten Lebensstempel und dem Bestand | ist der Stempel gesetzt: für **jeden Kanal des Bestands** eine offene Lücke `worker_restart` ab dem Stempel; ohne Stempel (erster Start überhaupt) keine |
| Verbindung verloren | `TwitchConnectionWatchdog.RebuildAsync`, vor dem ersten Wiederaufbau, mit `request.RequestedUtc` | für jeden Kanal des Bestands ohne offene Lücke eine `disconnect` **ab dem Signalzeitpunkt**; der Tracker merkt sich „Verbindungslücke offen“ |
| Rejoin-Runde abgeschlossen | `RebuildAsync` nach `RejoinDesiredChannelsAsync` | „Verbindungslücke offen“ endet; **geschlossen wird hier nichts** |
| Minuten-Abtastung | `WishCaptureWorker`, mit Roster und Bestand | **solange eine Verbindungslücke offen ist: nichts** (Sol-R2-P2-6: zwischen Signal und Clientwechsel trägt der Roster noch die Bestätigungen des alten Clients). Sonst: Kanal mit offener Lücke und `JoinConfirmed` → schließen zu „jetzt“; Kanal des Bestands ohne offene Lücke und nicht bestätigt → `not_joined` ab „jetzt“; Kanal nicht mehr im Bestand → offene Lücke verwerfen |

- **Geschlossen wird je Kanal erst mit dem bestätigten Join der aktuellen Client-Generation.** `JoinConfirmed` im Roster
  erfüllt das: `MarkAllChannelsUnconfirmed` setzt beim Wiederaufbau alle Kanäle zurück (`TwitchChatManager.cs:344`, `:905`),
  und `OnJoinedChannel` bestätigt nur unter der Identitätsprüfung des aktuellen Clients (`:984-1004`). `IsConnected` wird
  **nicht** benutzt: Es steht schon beim `004`, bevor die gedrosselten Joins bestätigt sind (`:857`).
- **Beginn ist der Signalzeitpunkt, für jeden Abrissgrund.** Die Spec sagt „von der letzten empfangenen Zeile“. Bei einem
  absichtlichen oder von Twitch angeordneten Reconnect liegt der letzte Frame bis zu fünf Minuten zurück (Server-PING) und
  erzeugte eine Scheinlücke über 60 Sekunden, obwohl nichts verloren ging. Preis der Vereinfachung: Bei erkannter Funkstille
  (`FrameStale`, Schwelle 15 Minuten) ist die gezeigte Lücke um bis zu diese 15 Minuten zu kurz. Benannt, hingenommen ([W26]).
- **Ein kurzer absichtlicher Reconnect erzeugt keine Zeile:** Signal bis bestätigter Join liegt unter 60 Sekunden, und unter
  60 Sekunden wird nichts gespeichert. Die Abtastung hat Minuten-Auflösung; damit ein schneller Rejoin nicht künstlich auf
  eine volle Minute gestreckt wird, tastet der Dienst nach dem Ereignis „Rejoin-Runde abgeschlossen“ einmal sofort ab.
- **Die Lücke ist eine Näherung, kein Messwert (Sol-R2-P2-6, abgestuft nach [B]).** Genauigkeit: etwa ein Takt (eine
  Minute) bzw. eine Rejoin-Runde (0,6 s je Kanal). Ein Kanal, dessen Join früh in der Runde bestätigt war, bekommt die
  Lücke bis zum Ende der Runde zugeschrieben; bei vielen Kanälen kann das eine Zeile knapp über 60 Sekunden ergeben, die
  für ihn kürzer war. Die Anzeige überschätzt also eher, als dass sie verschweigt. Ein feineres Modell (Join-Zeitpunkt je
  Kanal und Client-Generation im Tracker) gibt es bewusst nicht. Das steht im DECISIONS-Eintrag und im Hilfetext der
  Lückenliste („ungefähre Zeiträume“). Die Tests aus T11 bleiben; neue Rennen-Tests für diese Unschärfe gibt es nicht.
- Der Watchdog bekommt **zwei Aufrufe** auf ein schmales Interface (`ICaptureGapRecorder`, Singleton im Worker), beide
  synchron, nur Speicher, außerhalb der Lese-Schleife und der Manager-Sperren. Wiederaufbau, Backoff und Signale bleiben
  unberührt.

**Lebensstempel [E] [W6].** `WishCaptureState.LastAliveAtUtc` wird vom Takt nur geschrieben, wenn die Erfassung im Worker
wirksam ist **und** der Tracker keine Verbindungslücke offen hat. Die Spec lässt den Takt den Stempel immer schreiben; dann
wären eine Abschaltzeit der Erfassung und ein Absturz mitten in einer Trennung unsichtbar. So ist der Stempel „zuletzt
sicher erfasst“, und die Neustart-Lücke deckt beides. Er hängt nicht an `IsConnected`. Die übrigen Aufgaben des Takts
bleiben unabhängig von den Schaltern (AK66). Die Api gibt den Stempel als `captureAliveAtUtc` aus; die Oberfläche warnt,
wenn er älter als 3 Minuten ist (Richtung „Api an, Worker aus“).

Offene `not_joined`-Lücken überleben einen Absturz nicht, und die Genauigkeit ist eine Minute (RR11).

### 2.8 Schalter und Konfiguration (T1)

| Schlüssel | Compose | Default | Grenzen | Wirkung |
|---|---|---|---|---|
| `Wishes:Capture:Enabled` | `WISHES_CAPTURE_ENABLED` an `api` **und** `worker`, beide Compose-Dateien | `false` | | wirksam nur zusammen mit `Wishes:Enabled` |
| `Wishes:Capture:MaxPendingPerRequester` | keine | 3 | 1 bis 50, sonst Startabbruch (`WishOptions.Validate`) | offene Wünsche je Zuschauer und Kanal |

`WishOptions` bekommt eine geschachtelte Klasse `Capture` und eine abgeleitete Eigenschaft `CaptureActive` (`Enabled &&
Capture.Enabled`). **Jeder Prozess liest seine eigene Umgebung; ob erfasst wird, entscheidet der Worker (2.3).** Beide
Dienste beziehen die Variable in beiden Compose-Dateien aus demselben `WISHES_CAPTURE_ENABLED`, eine Abweichung entsteht
nur durch einen Teil-Neustart oder ein Teil-Rollback. Abschalten: **zuerst den Worker neu starten** (ab seiner
Startlogzeile wird nichts mehr gespeichert), dann die Api. Feste Werte als Konstanten in `WishLimits`: 10 Belohnungen je Kanal, 10 Minuten Anlernen, 200 offene
Wünsche je Kanal, 7 Tage Dedupe-Nachweis, 14 Tage Lückenanzeige, 60 Sekunden Mindestlücke, `PendingExpiryDays` 1 bis 90 (Default 14), Längen 50
(Bezeichnung), 500 (Text), 64 (`RewardId`, `IrcMessageId`).

`PendingExpiryDays` ist eine Kanal-Vorgabe: `GET/PUT …/wishes/settings` führen das Feld mit (optional im `PUT`; fehlt es,
bleibt der Wert, wie bei der Schwelle), `WishSettingsDto` bekommt es.

### 2.8a Offene Wünsche ohne Emote: Platzhalter statt nullbarer Pflichtspalten (Sol-P2-10, Entscheidung [E])

Die Spec (6.6) macht `SevenTvEmoteId`, `EmoteName` und `Alias` nullbar. Sol-P2-10 trifft zu: Nach der ersten Erfassung lägen
Nullwerte in Spalten, die jedes Image vor K6 als Pflichtfeld liest, auch bei ausgeschaltetem Feature, denn
`ChannelIdentityService.MoveWishesAsync` läuft ohne Feature-Schalter und projiziert `SevenTvEmoteId` für alle Wünsche des
Verlierers (`:1072-1078`). Zwei Wege wurden abgewogen:

| | (a) nullbar wie Spec, Rückweg prüfen | (b) Pflichtspalten bleiben, Platzhalter |
|---|---|---|
| Schema | drei Spalten verlieren NOT NULL | unverändert; ein zusätzlicher CHECK |
| Altes Image nach K6-Erfassung | liest NULL in Pflichtfelder; Verhalten je Abfrage offen, der Merge ist ein belegter Kandidat für einen Lesefehler | liest eine leere Zeichenkette, die es schon kennt (`Wish.cs:16-22` belegt die drei Felder mit `string.Empty`) |
| Nachweis des Rückwegs | Probe mit alten Images gegen eine Kopie mit K6-Zeilen samt herbeigeführtem Identitäts-Merge; der Merge ist lokal nur mit präparierten Zeilen auslösbar | eine kurze Probe genügt (Abschnitt 7, C6); der kritische Lesefehler kann nicht entstehen |
| K3-Code ([W13]) | `WishDto` und TS-Modell werden `string \| null`; rund ein Dutzend K3-Dateien samt Specs ändern ihre Typen | Typen bleiben `string`; geändert wird nur, wo ein Emote **benutzt** wird |
| Risiko | Typfehler findet der Compiler; ein vergessener Null-Fall wirft | eine leere ID könnte irgendwo als echte ID durchgehen |

**Entschieden: (b).** Der Rückweg ist damit konstruktiv verträglich statt nur geprobt, und K3 bleibt weitgehend unberührt.
Ein eigener Tisch für offene Erfassungen wurde verworfen: `pending` ist seit K1 ein Zustand von `Wishes` mit Übergängen,
Listenansicht, Zähler und Retention; zwei Quellen dafür wären ein Umbau von K1 bis K3.

Vertrag:

- **Platzhalter ist die leere Zeichenkette** in `SevenTvEmoteId`, `EmoteName`, `Alias`. CHECK: Eine leere `SevenTvEmoteId`
  ist nur in `pending`, `rejected` und `expired` erlaubt (die Zustandsmenge der Spec); `EmoteName` und `Alias` dürfen in
  denselben Zuständen leer sein.
- **Übergänge aus einem Platzhalterzustand (Sol-R2-P1-2).** Der CHECK verlangt in jedem anderen Zustand alle drei Felder.
  Jeder Übergang füllt sie deshalb **im selben Update**, mit dem er den Zustand ändert:

  | Übergang | `SevenTvEmoteId` | `Alias` | `EmoteName` |
  |---|---|---|---|
  | Abgleich `pending → active` bzw. `kept`, Treffer über die ID (Link-Wunsch) | bleibt (kanonisch, 2.5a) | Name des Slots | Name der Emote-Zeile des Slots, ist der leer: Name des Slots. **`Link` setzt heute nur den Alias (`WishReconcileService.cs:414-433`); K6 ergänzt den Namen für Wünsche mit leerem `EmoteName`** |
  | Abgleich, Treffer über den Namen | aus der Emote-Zeile des Slots | Name des Slots | wie oben |
  | `adopt` mit `wishId` aus `pending` | aus dem Slot | Name des Slots | wie oben |
  | `approve → approved` | Rumpf, sonst Wunsch (Pflicht, sonst 400 `wish_emote_required`) | Rumpf, sonst `requestedName`, sonst der Name | Rumpf, sonst der Alias; bleibt eines leer → 400 `wish_alias_invalid`, nichts geschrieben |
  | `approve → merged` | vom Zielwunsch | vom Zielwunsch | vom Zielwunsch |
  | `reject`, `expire` | bleiben, wie sie sind (Zustand erlaubt Platzhalter) | | |
  | `unlink` und Datumskorrektur `active → pending` | nach 2.6 zurück auf Platzhalter (Zustand erlaubt es) | | |
  | Kanal-Zusammenführung, offene Dublette | **die Zeile wird gelöscht**, kein Übergang nach `merged` (T9) | | |

  Ein Test je Zeile läuft gegen das **migrierte Schema** der Postgres-Fixture (die echte Migration `AddWishCapture` mit
  ihrem CHECK), nicht gegen ein Modell ohne Constraint.
- **Eine Stelle sagt „hat ein Emote“:** `Wish.HasEmote` (bzw. eine Core-Funktion) im Backend, `hasEmote(wish)` in
  `wish-view.ts`. Jede Abfrage und jede Aktion, die mit der Emote-ID arbeitet (Zuordnung über ID, „läuft schon“,
  „schon im Set“, Bild, 7TV-Link, Hinzufügen, Nutzung), fragt zuerst dort. Ein Test je Verbraucher im Backend belegt, dass
  ein offener Wunsch ohne Emote weder als laufend für die leere ID zählt noch einen Slot trifft.
- Der partielle Laufend-Index bleibt, wie er ist: `pending` gehört nicht zu seinen Zuständen, und der CHECK hält die leere
  ID aus allen laufenden Zuständen heraus.
- **DTO:** `sevenTvEmoteId`, `emoteName`, `alias` bleiben Zeichenketten; leer heißt „noch nicht bekannt“. Abweichung von der
  Spec ([W13], [W24]).
- **Was ein altes Image nicht tut:** Es lässt offene Wünsche nicht verfallen und leert `RequestText` nicht (seine Retention
  kennt die Spalte nicht). Ein Rückweg auf ein Image vor K6 ist deshalb nur ein Zwischenzustand; Operations nennt das.

### 2.9 Api-Fläche (T7)

Kanal-Gruppe aus K1 (`/api/channels/{channelName}/wishes`), Filterfolge unverändert: Namensvalidierung, Verwalter-Filter,
dann im Handler `wishes_disabled`, dann je nach Route `wish_capture_disabled`.

| Route | Rate-Limit | Zusatzfilter | 404 bei abgeschalteter Erfassung |
|---|---|---|---|
| `GET …/rewards` | `InteractiveRead` | – | ja |
| `POST …/rewards/learning` | `Bookkeeping` | `WishSettingsAuthorizationFilter` | ja |
| `POST …/rewards/learning/confirm` `{ sessionId, candidateId, label, defaultDurationDays }` | `Bookkeeping` | `WishSettingsAuthorizationFilter` | ja |
| `DELETE …/rewards/learning?sessionId=…` | `Bookkeeping` | `WishSettingsAuthorizationFilter` | ja |
| `PATCH …/rewards/{rewardId:long}`, `DELETE …/rewards/{rewardId:long}` | `Bookkeeping` | `WishSettingsAuthorizationFilter` | ja |
| `POST …/{id:long}/approve`, `/reject`, `/unlink` | `Bookkeeping` | – | **nein [E] [W7]** |

**[W7]** Die Spec stellt alle Routen der Tabelle hinter `wish_capture_disabled`. Für `approve`, `reject` und `unlink` folgt der
Plan stattdessen E22 und AK65 („bestehende Wünsche laufen weiter“): Wird die Erfassung abgeschaltet, weil der Tag wegfällt,
müssen die schon erfassten offenen Wünsche weiter entscheidbar sein. AK63 verlangt das 404 nur für die Belohnungs-Routen.

Neue Codes in `ApiErrorCodes`, im selben Commit in `web/src/app/core/i18n/api-error.ts` und beiden Locale-Dateien (Regel 7):
`wish_capture_disabled` (404), `wish_learning_active` (409), `wish_reward_no_candidate` (409), `wish_reward_already_known`
(409), `wish_reward_limit` (409), `wish_reward_not_found` (404), `wish_emote_required` (400), dazu über die Spec hinaus
`wish_channel_unbound` (409, Sol-P1-1), `wish_learning_stale` (409, Sol-P2-4) und `wish_running_not_extendable` (409, mit
`wishId`, Sol-R2-P2-7): **zehn** Codes. Die Belohnungs-Routen tragen zusätzlich den `WishCaptureAccessFilter` (2.4a), direkt
nach dem Verwalter-Filter und vor `WishSettingsAuthorizationFilter`. `/permissions` bekommt
`wishCaptureEnabled` (= `CaptureActive` der Api). `GET /api/admin/health` bekommt `wishCaptureActive` (aus der
Worker-Health, null = unbekannt) und `apiWishCaptureEnabled` (2.3). Handler bleiben dünn (Regeln 4, 6).

### 2.10 Oberfläche (T13 bis T16)

- **Aktionen eines offenen Wunsches** (reine Tabelle in `wish-view.ts`, als Tabelle getestet):

  | Hinweis | Aktionen in dieser Reihenfolge |
  |---|---|
  | keiner | `approve`, `adopt`, `edit`, `reject` |
  | `unparsed` | `approve` (Dialog verlangt das Emote), `adopt`, `reject` |
  | `ambiguous_match` | `adopt`, `approve`, `reject` |
  | `already_in_set` | `adopt`, `reject` |
  | `already_running` | `approve` (beschriftet als Verlängern des laufenden Wunsches), `reject` |

  Ein `active`-Wunsch mit `source = irc` und `linkOrigin = auto` bekommt zusätzlich `unlink`. `reject` und `unlink` sind
  bestätigte Aktionen (`isConfirmedAction`); keine davon schreibt nach 7TV, alle stehen auch auf einem groben Zeiger.
  „In 7TV hinzufügen“ bleibt wie in K3 an `approved`: zwei Klicks, entschieden mit **[F3]**. `edit` an einem offenen
  Wunsch ohne Emote öffnet den Bearbeiten-Dialog **nur mit dem Feld Laufzeit** (Sol-P3-12): kein Alias-Feld, keine
  Alias-Prüfung, der Rumpf trägt ausschließlich `durationDays`.
- **Zeile eines offenen Wunsches:** Einlösetext (reiner Text, nie als Link oder HTML gerendert, umbrechend, bei Überlänge
  gekürzt mit vollem Text im `title`), Zuschauer-Login, Bezeichnung der Belohnung, Alter, Hinweis als Satz. Hat der Wunsch
  ein Emote (`hasEmote`), liest `WishEmotePreviewService` (neu, `core/wishes/`, Root) Bild und Standardnamen über den bestehenden
  `SevenTvEmoteLookup`: nacheinander, mit Zwischenspeicher je ID für die Sitzung, nur für Zeilen im Sichtbereich; nach einer
  Drosselung (auch 7TVs „429 als HTTP 200“) stoppt er für die Sitzung und die Fläche bleibt leer. Bildregel wie in K3
  (unbewegte Fassung nur bei bestätigt unbewegtem Emote; `prefers-reduced-motion`).
- **Freigabe-Dialog** (`wish-approve-dialog.ts`): zeigt Einlösetext und Zuschauer; bei bekannter ID Vorschau, Alias
  (vorbelegt mit dem Standardnamen bzw. `requestedName`), Laufzeit (vorbelegt mit der des Wunsches). Ohne ID ein Feld für
  Link oder ID (`wish-input.ts`), Read und Felder gekoppelt wie im Nachtragen-Dialog (Plan-485 T6). 409
  `wish_emote_already_in_set` → Angebot „Bestand übernehmen“ für diesen Wunsch; `merged` → Meldung „als Verlängerung verbucht, läuft jetzt bis …“ mit Sprung zum laufenden
  Wunsch; 409 `wish_running_not_extendable` → Meldung „der laufende Wunsch ist noch nicht im Set, Freigeben geht danach“,
  der Wunsch bleibt in „Offen“ (Sol-R2-P2-7).
- **Ablehnen:** Bestätigung mit dem Hinweis, die Einlösung in Twitchs Warteschlange zu erstatten.
- **Settings-Abschnitt** (`wish-rewards-section.ts`, in `wish-settings-section` eingehängt, nur bei
  `wishCaptureEnabled`): Belohnungsliste (Bezeichnung, Standardlaufzeit, aktiv/pausiert, „zuletzt erkannt“), Bearbeiten,
  Pausieren, Vergessen (bestätigt); Anlern-Ablauf als Zustandsmaschine `idle → waiting (Restzeit) → candidate (Text, Login) →
  bestätigen (Bezeichnung, Laufzeit)` bzw. `expired`/Abbrechen; der Dialog hält `sessionId` und `candidateId` des
  **angezeigten** Stands und schickt genau diese; 409 `wish_learning_stale` → Meldung „inzwischen hat jemand neu
  angelernt“, Stand neu laden, nichts gespeichert; bei `channelBound: false` ein erklärender Satz statt des Startknopfs; der Satz „nur Belohnungen mit Texteingabe werden erkannt“
  und „die Einlösung zum Anlernen wird kein Wunsch“; Feld `PendingExpiryDays`; Lückenliste der letzten 14 Tage, überschrieben als ungefähre Zeiträume (2.7), mit dem
  Hinweis auf Twitchs Warteschlange, nur hier und nicht im Wünsche-Tab (entschieden mit **[F4]**); Warnzeile bei altem `captureAliveAtUtc`. Ohne `canEdit` nur lesend.
- **Live:** `wish.changed` lädt Arbeitsliste **und** Belohnungs-Stand neu (bestehendes `liveReload` samt Aufholen aus K3);
  die Restzeit des Anlernens läuft über einen Signal-Takt, kein eigener Abruf je Sekunde.
- **Bestand anpassen (2.8a):** Die Typen aus K3 bleiben (`string`). Geändert wird nur, wo ein Emote **benutzt** wird:
  Titel der Zeile (`wish-row`, `wish-labels`, `my-wishes`: ohne Emote `requestedName`, sonst der Einlösetext), Bild
  (`wish-image`), 7TV-Link, die Vorbelegung der Dialoge. Alle fragen `hasEmote(wish)`; die 7TV-Aktionen (`add7tv`,
  `remove`) sind an Zustände gebunden, in denen der CHECK ein Emote garantiert.
- **`/admin/monitoring`:** eine Zeile „Wunsch-Erfassung“ nach 2.3 (T16).
- i18n: `wishes.pending.*`, `wishes.approve.*`, `channelSettings.wishes.rewards.*`, `errors.api.wish_*`,
  `audit.actions.wish.*` (sieben neue: `approve`, `reject`, `autoLink`, `unlink`, `rewardAdd`, `rewardChange`,
  `rewardRemove`). Keine „…“ an Knöpfen.

---

## 3. Tasks

Je Task ein Subagent mit frischem Kontext, **strikt nacheinander** (die Backend-Tasks teilen `obj/`, die Frontend-Tasks
die beiden Locale-Dateien, die E2E-Suite Port 4300; Läufe per `flock` serialisieren). Jeder Backend-Task endet mit grünem
`dotnet build EmotePurge.slnx` und seinen Tests, jeder Frontend-Task mit `npm --prefix web test -- --watch=false`,
`npm --prefix web run lint`, `npm --prefix web run format:check`; jeder mit einem eigenen Conventional Commit (englisch).
**Ein** englischer DECISIONS-Eintrag „Wish emotes, child 6: chat capture (#486, epic #481)“ über dem K3-Eintrag: T1 legt ihn
an, jeder Task mit Vertrags- oder Konventionsänderung ergänzt seinen Absatz und die `**Betrifft:**`-Zeile im selben Commit
(Regel 3). Befehle einzeln absetzen; **nie `docker compose` aus dem Worktree**. Kommentare, Logs, Commit-Texte englisch.

| Task | Inhalt | Modell |
|---|---|---|
| T1 | Datenmodell, Migration `AddWishCapture`, Platzhalter-CHECK, `WishOptions.Capture`, Compose | opus |
| T2 | `WishTextParser` und `SevenTvEmoteIdCanonical` (Core, rein) | sonnet |
| T3 | Zuordnung: `pending`, Namens-Treffer, Hinweise, Sperrliste, `wish.autoLink`, Fensterprüfung bei der Datumskorrektur | opus |
| T4 | `IWishRewardService`: Anlernen mit Sitzungs- und Kandidatenkennung, Belohnungen, Lesemodell, Kanalbindung | **opus** (vorher sonnet: Bindung und veraltete Dialoge sind Rennen) |
| T5 | `IWishCaptureService`: Kanalbindung, Dedupe-Nachweis, Verarbeitung, Grenzen, Verfall, Aufräumen, Lücken schreiben | opus |
| T6 | `WishService`: `approve`, `reject`, `unlink`, `adopt`/`PATCH` für offene Wünsche, DTO, Sortierung | opus |
| T7 | Api: Routen, Zugangsfilter (2.4a), zehn Fehlercodes, `wishCaptureEnabled`, Admin-Health | **opus** (vorher sonnet: neuer Autorisierungsweg) |
| T8 | Retention, Kontolöschung, Purge | sonnet |
| T9 | Kanal-Zusammenführung | opus |
| T10 | Worker: Parser, Reihenfolge-Funktion, Warteschlange, Andocken im Manager, Vertragstests | opus |
| T11 | Worker: `WishCaptureWorker`, `CaptureGapTracker`, Watchdog-Aufrufe, Health-Feld, Registrierung | opus |
| T12 | Frühe Live-Probe des Transports (Abschnitt 7, Stufe L0) | Orchestrator mit Betreiber |
| T13 | Web: Modell, Dienst, Schalter, reine Regeln | sonnet |
| T14 | Web: Bereich „Offen“, Zeile, Freigabe-Dialog, Ablehnen, Zuordnung lösen | opus |
| T15 | Web: Belohnungen, Anlernen, Lücken im Settings-Abschnitt | opus |
| T16 | Web: Audit-Aktionen, i18n-Abschluss, Gegenprobe der Fehlercodes, Zeile im Admin-Monitoring | sonnet |
| T17 | E2E und Audit-Harness-Szenarien | sonnet |
| T18 | Doku | sonnet |
| T19 | Live-Verifikation Stufen A bis C (Abschnitt 7) | Orchestrator mit Betreiber |
| T20 | Gates (Abschnitt 8) | Orchestrator |

Abhängigkeiten: T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8 → T9; T10 braucht nur T1; T11 braucht T5 und T10; T12 braucht T11;
T13 braucht T7; T14 und T15 brauchen T13; T16, T17 nach T15; T18 nach T17; T19 nach T18; T20 zuletzt. T12 darf vor T13
laufen und soll es (ein Befund am Transport ändert sonst spät den ganzen Rest).

### T1: Datenmodell und Migration

- **Ziel:** Tabellen und Spalten nach Spec 6.6 mit drei Abweichungen: Platzhalter statt Nullbarkeit (2.8a), `SessionId` und
  `CandidateId` an `WishRewardLearnings` (2.4), neue Tabelle `WishCaptureReceipts` (2.4); Schalter.
- **Dateien:** `src/EmotePurge.Core/Entities/Wish.cs`, `ChannelWishSettings.cs`, neu `ChannelWishReward.cs`,
  `WishRewardLearning.cs`, `WishCaptureGap.cs`, `WishCaptureState.cs`, `WishSlotExclusion.cs`, `WishCaptureReceipt.cs`,
  `WishStatus.cs` (`WishMatchKind`, `WishCaptureGapReason`, Grenzen); `Wish.cs` (`HasEmote`);
  `Core/Services/WishOptions.cs`; `Infrastructure/Persistence/AppDbContext.cs`, `Migrations/*_AddWishCapture.cs`; die
  Verbraucher der Emote-ID in `WishService.cs`, `WishReconcileService.cs`, `WishOverviewService.cs`,
  `WishImageQueries.cs`, `WishUsageQueries.cs`, `ChannelIdentityService.cs` bekommen die `HasEmote`-Frage, wo sie mit der ID
  suchen oder vergleichen; `docker-compose.yml`, `docker-compose.prod.yml`, `.env.example`; Tests
  `Integration/WishSchemaTests.cs`, `Unit/WishOptionsTests.cs`; `docs/DECISIONS.md`.
- **Vertrag:** Spec 6.6 wörtlich, dazu: `WishCaptureState` genau eine Zeile (CHECK `Id = 1` wie für `WishBotCredentials`
  vorgesehen); `WishRewardLearnings.ChannelId` als PK; eindeutig `(ChannelId, RewardId)` an `ChannelWishRewards`; eindeutig
  partiell `(ChannelId, IrcMessageId)` WHERE `IrcMessageId IS NOT NULL`; CHECK „`SevenTvEmoteId`, `EmoteName`, `Alias` dürfen
  nur in `pending`, `rejected`, `expired` leer sein“ (die Spalten bleiben NOT NULL); PK `(ChannelId, IrcMessageId)` und
  Index auf `SeenAtUtc` an `WishCaptureReceipts`, Kaskade am Kanal; CHECK `PendingExpiryDays BETWEEN 1 AND 90`; CHECK für `MatchKind` und
  `Reason`; Index `(ChannelId, FromUtc)` an `WishCaptureGaps`. **Jede neue NOT-NULL-Spalte an einer Bestandstabelle hat
  einen Datenbank-Default** (`PendingExpiryDays` 14), weil das alte Image während des Fensters weiter einfügt; `MatchKind`
  ist nullbar (Handeinträge haben keinen). **Die Migration ist rein additiv:** keine Bestandsspalte verliert NOT NULL, kein
  Daten-Update.
- **Grenzfälle:** Bestandszeilen erfüllen den neuen CHECK (alle drei Felder gefüllt); ein Wunsch in `merged` oder `active`
  mit leerer Emote-ID wird abgewiesen; zwei offene Wünsche ohne Emote im selben Kanal sind erlaubt (der Laufend-Index
  betrifft sie nicht); Löschen eines Kanals nimmt alle fünf Tabellen mit; Löschen eines Wunsches nimmt seine Sperrzeilen mit;
  Löschen einer Belohnung leert `Wishes.RewardId`; ein archivierter oder gelöschter Slot lässt die Sperrzeile stehen.
- **AKs:** Grundlage für alle; AK65 (Schalter). **Tests (Regel 11):** Schema-Tests je Constraint und Kaskade;
  `WishOptionsTests` (Bindung, Grenzen, `CaptureActive` in allen vier Kombinationen); Fixture-Test „keine ausstehende
  Modelländerung“; je Verbraucher der Emote-ID ein Test „offener Wunsch ohne Emote zählt nicht als laufend für die leere
  ID und trifft keinen Slot“ (kann in den Folgetasks liegen, T1 legt die Liste der Verbraucher im DECISIONS-Eintrag an).
- **Fertig:** `dotnet test EmotePurge.slnx` grün. **Commit:** `feat: add wish capture schema and switch`

### T2: `WishTextParser`

- **Ziel:** die Textauswertung der Spec als reine Funktion, und die eine Form der Emote-ID (2.5a). **Dateien:**
  `src/EmotePurge.Core/Services/WishTextParser.cs`, `src/EmotePurge.Core/SevenTv/SevenTvEmoteIdCanonical.cs`; Tests
  `tests/EmotePurge.Infrastructure.Tests/Unit/WishTextParserTests.cs`, `SevenTvEmoteIdCanonicalTests.cs`.
- **Vertrag:** `Parse(string? text)` → `(MatchKind, SevenTvEmoteId?, RequestedName?)`. Link-Regeln und Namens-Grammatik
  exakt nach Spec K6; nur BCL (Schichtentreue, `CoreAssemblyReferenceTests`). Ein regulärer Ausdruck, falls benutzt, mit
  Zeitgrenze oder als erzeugter, nicht rückverfolgender Ausdruck (der Text ist fremdbestimmt).
- **Grenzfälle über die Spec-Tabelle hinaus:** ID-Länge 23, 25, 27; Kleinbuchstaben in der 26er-ID; Link in spitzen
  Klammern; zwei Links; Link plus Name; Name mit 100 und 101 Zeichen; nur Erwähnungen; Tabulatoren und Zeilenumbrüche als
  Leerraum; Nicht-ASCII-Namen; 500 Zeichen ohne Leerraum. **Normalisierung:** die drei in K3 live geprüften Paare aus
  dem Web-Spec von `canonicalSevenTvEmoteId` in beide Richtungen übernommen (darunter `60ae958e229664e8667aea38` →
  `01F6MZGCNG000255K4X1K7NTHR`); Hex in Großbuchstaben; ULID klein und gemischt; ungültige Eingabe unverändert. Der Parser
  liefert für alle drei Link-Formen desselben Emotes dieselbe ID.
- **AKs:** AK54 (die dort verlangte „genau die ID“ ist die kanonische; für die 24-stellige Hex-ID weicht das vom Wortlaut
  ab, [W30]). **Fertig:** Tabelle grün. **Commit:** `feat: parse the redeemed wish text into link or name`

### T3: Zuordnung

- **Ziel:** 2.5 vollständig. **Dateien:** `Core/Services/WishSlotMatcher.cs`, `WishTransitions.cs` (Zeile `presented` mit
  `unconfirmed_start` → `pending` über `Unlink`), `Infrastructure/Services/WishReconcileService.cs`,
  `Core/Entities/AuditLogEntry.cs` (`WishAutoLink`, `WishUnlink`); Tests `Unit/WishSlotMatcherTests.cs`, neu
  `Integration/WishReconcileServiceTests.Pending.cs`; `docs/DECISIONS.md`.
- **Grenzfälle:** Slot eine Sekunde vor `CreatedAtUtc`; Slot genau auf `CreatedAtUtc`; vorläufiges `AddedAt` (Start
  nicht maßgeblich, spätere Datumskorrektur verschiebt Start und Ablauf wie beim Bestand); zwei Slots, die nur in der
  Schreibweise abweichen; ordinaler Treffer neben einem abweichend geschriebenen (ordinal gewinnt); gesperrter Slot als
  einziger Kandidat (kein Treffer, kein Hinweis `ambiguous_match`); zwei offene Wünsche und ein Slot; älterer Handeintrag in
  `approved` und jüngere Einlösung; offener Wunsch nach nominellem Verfall, Takt noch nicht gelaufen; Namens-Wunsch, dessen
  Kandidat ein Emote mit laufendem Wunsch trägt; `Permanent` gibt es an erfassten Wünschen nicht (Zweig trotzdem über die
  Tabelle); `approved` aus der Erfassung mit Slot zwischen Einlösung und Freigabe; derselbe Fall für einen Handeintrag
  (kein Treffer, unverändert). **Regression Sol-P1-3:** Eintrag liegt vor der Einlösung im Set, ist dem Abgleich aber
  unbekannt; ein Delta legt den Slot mit Beobachtungszeitpunkt nach der Einlösung an → Wunsch wird `active`, vorläufig
  (`startsAtAuthoritative` falsch); der Vollsync liefert das echte Datum vor `CreatedAtUtc` → Wunsch ist wieder `pending`
  mit `already_in_set`, ohne Slot, ohne Start und Ablauf; zu keinem Zeitpunkt ist er `active` **und** maßgeblich. Dasselbe
  für einen schon `presented` (`unconfirmed_start`) gewordenen. Gegenprobe: maßgebliches Datum im Fenster → bleibt `active`, Start verschoben.
  **Geltung und Entscheidungen (Sol-R2-P2-4):** derselbe Ablauf für einen **Handeintrag** und für einen freigegebenen
  Erfassungs-Wunsch → Verhalten wie vor K6 (bleibt `active`, Datum korrigiert; als Test festgenagelt, damit K6 es nicht
  ändert); K6-Autozuordnung mit gebuchter Verlängerung → Bindung bleibt; in beiden Commit-Reihenfolgen mit zwei
  Verbindungen: Verlängern vor der Korrektur, Korrektur vor dem Verlängern (dann 409); `kept` → unberührt.
  **Drei Formen (Sol-R2-P2-3):** drei offene Wünsche in drei Kanälen, erfasst mit ObjectID-Link, kleingeschriebener ULID
  und kanonischer ULID desselben Emotes; ein Slot mit der kanonischen ID je Kanal → alle drei werden `active`; ein
  laufender Wunsch unter der kanonischen ID lässt den Hex-Link-Wunsch `already_running` zeigen.
  **Platzhalter (Sol-R2-P1-2):** Link-Wunsch mit leerem Alias und Namen wird verknüpft → alle drei Felder gefüllt, der
  CHECK des migrierten Schemas hält; Slot, dessen Emote-Zeile keinen Namen trägt → Name = Alias.
- **AKs:** AK55, AK56, AK57 (Fenster, auch nach einer Datumskorrektur), AK58 (erste Hälfte), AK61 (Sperre im Abgleich,
  auch nach „Neustart“ = neue Dienstinstanz). **Tests:** Matcher als Tabelle; Abgleich gegen Postgres auf den drei Wegen Vollsync, Delta, manuelles
  Slot-Schreiben (über die bestehenden Hooks, `WishReconcileServiceTests.Hooks.cs`); genau ein `wish.autoLink` je
  Verknüpfung, keiner bei Rückkehr; Hinweis-Tabelle; `wish.changed` nur bei sichtbarer Änderung.
- **Fertig:** `dotnet test` grün; die Bestandstests des Abgleichs unverändert grün, mit Ausnahme der Stellen, die jetzt
  einen `wish.autoLink` für `approved` erwarten (im Commit benannt). **Commit:** `feat: link pending wishes by emote id or name`

### T4: Anlernen und Belohnungen

- **Ziel:** `IWishRewardService` nach 2.4. **Dateien:** `Core/Services/IWishRewardService.cs`,
  `Infrastructure/Services/WishRewardService.cs`, `Core/Services/IChannelAccessService.cs` und
  `Infrastructure/Services/ChannelAccessService.cs` (`ResolveWishCaptureAccessAsync`, 2.4a; mit Unit-Test in
  `ChannelAccessServiceTests`: gleicher Login bei anderer `BroadcasterId` → nicht bestätigt; passende `BroadcasterId` bei
  anderem Login → bestätigt; Zeile ohne ID → nie bestätigt, auch nicht für den Broadcaster nach Login; Liste nicht
  ermittelbar → nein; Admin → ja, wenn die Zeile eine ID trägt), `ServiceCollectionExtensions.cs`, `AuditLogEntry.cs` (drei Aktionen), `WishService.cs` (`PendingExpiryDays` in den
  Einstellungen); Tests `Integration/WishRewardServiceTests.cs`, `Unit/WishRegistrationTests.cs`.
- **Vertrag zusätzlich:** Alle Methoden nehmen die `ChannelId` aus 2.4a, nicht den Namen. Änderungen der Anlern-Zeile nur
  als bedingte Anweisungen (2.4).
- **Grenzfälle:** **atomar, mit zwei Postgres-Verbindungen (Sol-R2-P2-5):** `GET` hat die abgelaufene Sitzung A gelesen,
  dazwischen ersetzt ein Start sie durch B, dann führt `GET` sein Löschen aus → B lebt; dasselbe für ein Abbrechen mit A
  und für ein Bestätigen mit dem Kandidaten aus A; Haushalts-Takt gegen einen gleichzeitigen Start. **zwei Verwalter (Sol-P2-4):** A sieht Kandidat 1, B bricht ab, startet neu, Kandidat 2 trifft ein, A
  bestätigt mit seinen Kennungen → 409 `wish_learning_stale`, keine Belohnung; A bricht mit alter `sessionId` ab → 409, die
  neue Sitzung lebt; gleiche Sitzung, aber Kandidat erst nach dem Öffnen eingetroffen (Bestätigen ohne `candidateId`) →
  400. **Bindung (Sol-P1-1):** Start in einem Kanal ohne `TwitchChannelId` → 409 `wish_channel_unbound`; Lesen in einem
  solchen Kanal liefert `channelBound: false` und nie einen Kandidaten, auch wenn von Hand eine Zeile mit Kandidat liegt.
  Start bei abgelaufener Zeile; zwei gleichzeitige Starts (einer 409, über den PK); Bestätigen ohne
  Kandidat, nach 10 Minuten bei noch liegender Zeile (409, Zeile danach gelöscht), zweimal gleichzeitig; elfte Belohnung
  beim Start und beim Bestätigen; Bezeichnung nur aus Leerraum; Vergessen einer Belohnung mit Wünschen (Wünsche bleiben,
  `rewardLabel` null); Pausieren; Lesen löscht Abgelaufenes; Lücken älter als 14 Tage erscheinen nicht; `canEdit` je Rolle;
  Redis nicht erreichbar beim Senden von `wish.changed` (Befehl gelingt trotzdem).
- **AKs:** AK50 (Api-Hälfte), AK52 (Grenze), AK66 (Lesen zeigt Abgelaufenes nicht). **Tests:** Integration gegen Postgres
  mit `TimeProvider`-Fälschung; Publisher als Substitut.
- **Fertig:** grün. **Commit:** `feat: learn and manage wish rewards`

### T5: Erfassungsdienst

- **Ziel:** `IWishCaptureService` nach 2.4 und 2.7. **Dateien:** `Core/Services/IWishCaptureService.cs` (samt
  `RewardObservation`), `Infrastructure/Services/WishCaptureService.cs`, `ServiceCollectionExtensions.cs`; Tests
  `Integration/WishCaptureServiceTests.cs`.
- **Grenzfälle:** **Bindung (Sol-P1-1):** Kanal ohne `TwitchChannelId` mit dem Namen des IRC-Kanals → `ChannelUnbound`,
  keine Zeile; **wiedervergebener Login:** Zeile A (Name `x`, ID 1) wurde umbenannt, eine neue Zeile B (Name `x`, ohne ID)
  entsteht, eine Beobachtung mit `RoomId` 2 kommt für `x` → nichts in A, nichts in B; **gleichzeitige ID-Nachführung:** ein
  zweiter Task hält die Kanalsperre und setzt bzw. ändert `TwitchChannelId`, während `ProcessAsync` wartet → entschieden
  wird auf dem Stand nach der Sperre (ID passt → Wunsch; ID passt nicht mehr → `ChannelUnbound`); Zusammenführung der
  Zeile während des Wartens (Zeile weg → `ChannelUnbound`). **Dedupe (Sol-P2-6):** dieselbe Zeile nach dem Bestätigen des
  Anlernens erneut → `Duplicate`, kein Wunsch; dieselbe Zeile nach einer Kontolöschung des Zuschauers erneut →
  `Duplicate`, die Einlöser-Felder des ersten Wunsches bleiben leer und es entsteht kein zweiter; beide Reihenfolgen
  (Löschung vor und nach der Dublette); ein an der Grenze verworfener Versuch hinterlässt keinen Nachweis; Nachweis älter
  als 7 Tage wird gelöscht. **Erste Einlösung nach einem Befehl (Sol-P2-5):** unmittelbar nach `StartLearningAsync`,
  `ConfirmLearningAsync` und dem Reaktivieren einer Belohnung trifft die nächste Beobachtung zu, ohne Wartezeit.
  Deaktivierter Kanal; pausierte Belohnung; unbekannte Belohnung ohne Anlernen (keine Zeile in irgendeiner Tabelle, AK51);
  zweite unbekannte Belohnung nach gesetztem Kandidaten; bekannte Belohnung während eines laufenden Anlernens (wird
  Wunsch, nicht Kandidat); Einlösung vor Beginn des Anlernens; Dublette nacheinander und gleichzeitig (Index); vierter
  offener Wunsch bei Grenze 3, wobei ein inzwischen `active` gewordener nicht mitzählt; 201. offener Wunsch; Text leer,
  500 Zeichen; `SentAtUtc` in der Zukunft; Verfall mit 14 und mit 1 Tag (23 h, 25 h); Verfall nach `unlink` zählt ab
  `StatusChangedAtUtc`; Aufräumen bei ausgeschalteten Schaltern; Lücke von 30 s, 5 min, für Kanal ohne aktive Belohnung;
  Lebensstempel fehlt; `ListGapChannelsAsync` ohne ungebundene, deaktivierte und belohnungslose Kanäle.
- **AKs:** AK50 (Kandidat), AK51, AK52, AK53, AK57 (zweite Hälfte), AK60, AK62 (Schreiben), AK64 (kein Audit, kein Log
  mit Text), AK66. **Tests:** Integration gegen Postgres; ein Test prüft über einen Log-Sammler, dass keine Logzeile des
  Dienstes Text, Login oder Nutzer-ID enthält.
- **Fertig:** grün. **Commit:** `feat: turn reward observations into candidates and pending wishes`

### T6: Befehle am offenen Wunsch

- **Ziel:** 2.6. **Dateien:** `Core/Services/IWishService.cs`, `Infrastructure/Services/WishService.cs`,
  `AuditLogEntry.cs` (`WishApprove`, `WishReject`, `WishUnlink`); Tests neu `Integration/WishServiceTests.Pending.cs`,
  Ergänzung `WishServiceTests.Reads.cs`, `Unit/WishTransitionsTests.cs`.
- **Grenzfälle:** **Keine buchbare Verlängerung (Sol-R2-P2-7):** Zielwunsch in `approved` oder `unconfirmed` → 409
  `wish_running_not_extendable` mit dessen ID, der offene Wunsch unverändert `pending`, kein Audit-Eintrag; nachdem der
  Zielwunsch `active` ist, dieselbe Freigabe → `merged` mit Verlängerung. **Platzhalter (Sol-R2-P1-2), gegen das migrierte
  Schema:** `approve → merged` eines Namens-Wunsches ohne Emote füllt die drei Felder vom Ziel; `approve → approved` ohne
  ableitbaren Alias → 400, Zeile unverändert; `adopt` aus `pending` ohne Emote füllt alle drei. **Kanonische ID
  (Sol-R2-P2-3):** `approve` mit Hex-ID im Rumpf, während unter der ULID ein Wunsch läuft → Zusammenführung, nicht ein
  zweiter laufender Wunsch. **Verlängerung über die Freigabe (Sol-P2-9), gegen Postgres mit zwei Verbindungen:** `approve` und ein
  gleichzeitiges `ExtendAsync` auf dem Zielwunsch → beide Verlängerungen zählen (Ablauf um zwei Vorgaben weiter,
  `ExtensionCount` + 2, zwei `wish.extend`); `approve` gegen den Ablauf-Lauf, der den Zielwunsch gerade vorlegt → ein
  konsistenter Endstand (`active` mit neuem Ablauf, das `wish.extend` trägt `previousExpiresAtUtc`); Zielwunsch endet
  zwischen Lesen und Sperre (`removed`) → die Freigabe wird ein normales `approved` bzw. 409 „schon im Set“. **Herausgabe
  (Sol-P1-1, Sol-R2-P1-1):** zwei Kanalzeilen-Lagen mit **gleichem Login und verschiedenen Twitch-IDs**: Die Zeile trägt
  ID 1, der Aufrufer moderiert laut Liste einen Kanal gleichen Logins mit `BroadcasterId` 2 → `view=open`, `GET /{id}`,
  jede Befehlsantwort und `mine` liefern den `irc`-Wunsch mit leerem `requestText`, `requestedName`, `requesterLogin`,
  `rewardLabel`; mit `BroadcasterId` 1 → gefüllt; Zeile ohne ID → leer. Die Methoden lesen über die mitgeführte
  `ChannelId` (Test: Zeile wird zwischen Prüfung und Abfrage umbenannt, die Antwort stammt weiter von der geprüften Zeile). `approve` eines Link-Wunsches ohne Namen im Rumpf; eines Namens-Wunsches mit ID; mit laufendem Wunsch in
  `active` (verlängert), in `presented` (wird `active`), in `approved` (nur zusammengeführt); Emote im Set ohne laufenden
  Wunsch; zwei gleichzeitige `approve`; `approve` gegen einen gleichzeitig laufenden Abgleich (Wunschsperre entscheidet,
  der Verlierer sieht 409 bzw. keinen Treffer mehr); `unlink` auf `adopted`, auf Handeintrag, auf `presented`, zweimal;
  `unlink` bei eingereihter Entfernung (verworfen); `adopt` eines Namens-Wunsches auf einen gesperrten Slot (Sperre weg);
  `PATCH alias` ohne Emote (409); `PATCH` nur mit `durationDays` ohne Emote (200); Antworten tragen `requestText`, `requesterLogin`, `rewardLabel` nur auf Verwalter-Routen
  (`mine`, `summary` geprüft: `mine` liegt hinter der Verwalter-Zuordnung je Kanal und darf sie tragen, `summary` trägt nur
  Zahlen).
- **AKs:** AK58 (zweite Hälfte), AK59, AK61, AK64 (Audit-Details ohne Text). **Tests:** Integration; Audit-Details je
  Aktion wörtlich geprüft, bei der Zusammenführung beide Einträge (`wish.approve`, `wish.extend`) in derselben Transaktion.
- **Fertig:** grün. **Commit:** `feat: approve, reject and unlink captured wishes`

### T7: Api

- **Ziel:** 2.9. **Dateien:** `src/EmotePurge.Api/Endpoints/WishEndpoints.cs`, `Validation/ApiErrorCodes.cs`,
  `Endpoints/ChannelEndpoints.cs` (`WishCaptureEnabled`), der Admin-Health-Endpunkt (`wishCaptureActive`,
  `apiWishCaptureEnabled`), `web/src/app/core/i18n/api-error.ts`,
  `web/public/i18n/de.json`, `en.json` (nur die zehn `errors.api.*`); Tests `tests/EmotePurge.Api.Tests/WishEndpointsTests.cs`.
- **Grenzfälle:** `Wishes:Enabled` aus und Erfassung an (404 `wishes_disabled`); beide an; nur Wünsche an (Belohnungs-Routen
  404 `wish_capture_disabled`, `approve` erreichbar); Rumpf leer oder `null`; `confirm` ohne Kennungen (400); `DELETE …/learning` ohne `sessionId` (400); `rewardId` nicht
  numerisch (404 der Route); Admin-Health mit einer Worker-Health ohne das neue Feld (null, nicht falsch).
- **AKs:** AK63. **Tests (Regel 11):** der neue `WishCaptureAccessFilter` in der Matrix: Verwalter nach Login, aber nicht an
  der ID bestätigt → 403 auf **jeder** Belohnungs-Route (auch `GET`), Zeile ohne ID → 409 `wish_channel_unbound`, bestätigt
  → durch; Reihenfolge nach dem Verwalter-Filter und vor dem Vorgaben-Filter; `IChannelAccessService` ist im Projekt schon
  Substitut. Dazu die Filter-Matrix für jede neue Route: 401, 403 für Nicht-Verwalter, 404 je Schalter, 403
  `wish_settings_restricted` für einen Twitch-Mod im Bot-Modus ohne Aktivierung an den schreibenden Belohnungs-Routen, 400
  des Namensvertrags; `IWishRewardService` als Substitut registriert. `api-error-locales.spec.ts` bleibt grün.
- **Fertig:** `dotnet test` und Vitest grün. **Commit:** `feat: expose wish capture routes`

### T8: Retention, Kontolöschung, Purge

- **Ziel:** Spec 6.5 für K6. **Dateien:** `Core/Services/RetentionPolicy.cs` (`WishCaptureGap` = 30 Tage),
  `Infrastructure/Services/DataRetentionService.cs`, `AccountDeletionService.cs`, `RetentionRunSummaryFormatter.cs` (Worker),
  `docs/Operations.md` (Tabelle der Fristen); Tests `Integration/DataRetentionServiceTests.Wishes.cs`,
  `AccountDeletionServiceTests*`, `WishPurgeTests.cs`.
- **Vertrag:** Wo heute die Einlöser-Felder geleert werden, werden **zusätzlich** `RequestText`, `RequestedName` und
  `IrcMessageId` geleert **[E] [W10]** (alle stammen aus der Nachricht des Zuschauers). **Die Dedupe hängt daran nicht
  (Sol-P2-6):** Sie liegt im inhaltsfreien Nachweis (`WishCaptureReceipts`), den weder Retention noch Kontolöschung
  anfassen und der nach 7 Tagen von selbst geht. Löschung der Einlöserdaten und Schutz vor gepufferten Dubletten sind damit
  getrennt. Das Prädikat „fällig“ nimmt `RequestText IS NOT NULL` auf, der partielle Index aus K1 bleibt.
  Lücken älter als 30 Tage werden gelöscht, im Trockenlauf gezählt. Die Kontolöschung leert dieselben Felder für alle
  Wünsche der Twitch-ID sofort.
- **Grenzfälle:** offener Wunsch 120 Tage alt (Regel E14 c); abgelehnter Wunsch 31 Tage; Trockenlauf ändert nichts;
  Kanal-Purge und Selbstbereinigung nehmen alle fünf Tabellen mit.
- **AKs:** AK62 (Retention), AK64. **Fertig:** grün. **Commit:** `feat: clear captured wish text and gaps under the retention rules`

### T9: Kanal-Zusammenführung

- **Ziel:** Spec K6 „Kanal-Zusammenführung“. **Datei:** `Infrastructure/Services/ChannelIdentityService.cs`
  (`MoveWishesAsync` und Umgebung); Test `Integration/ChannelIdentityServiceTests.Wishes.cs`.
- **Vertrag:** In der Transaktion der Zusammenführung, vor dem Löschen des Verlierers: Belohnungen verschieben; bei gleicher
  `RewardId` gewinnt die Zeile des Überlebenden, und die Wünsche des Verlierers werden **vor** dem Löschen der
  Verlierer-Zeile auf sie umgehängt; Anlern-Zeile verschieben, wenn der Überlebende keine hat, sonst verwerfen; Lücken
  verschieben; `PendingExpiryDays` folgt den Einstellungen (die des Überlebenden gewinnen, Bestand). Reihenfolge der
  Schritte so, dass kein Fremdschlüssel und kein eindeutiger Index zwischendurch verletzt ist: erst Belohnungen
  zusammenführen und Wünsche umhängen, dann Wünsche verschieben. Dedupe-Nachweise des Verlierers wandern mit (bei gleichem Schlüssel
  bleibt der des Überlebenden). Ein Kommando an den Worker ist nicht nötig (2.3). Der Audit-Eintrag der Zusammenführung
  bekommt die Zählwerte.
- **Gleiche Einlösung in beiden Zeilen (Sol-P2-7, abgewandelt).** Mit der Kanalbindung aus 2.4 kann dieselbe IRC-Zeile
  nicht in zwei Kanalzeilen landen: `TwitchChannelId` ist eindeutig, also trägt höchstens eine Zeile die `RoomId`.
  Trotzdem gilt für den Fall, dass beide Seiten einen Wunsch mit derselben `IrcMessageId` tragen (Altbestand, Handgriff),
  **genau ein Besitzer der `IrcMessageId` und ein gültiger Endzustand (Sol-R2-P1-2):**

  | Lage | Wirkung |
  |---|---|
  | einer ist `pending`, der andere nicht | der offene wird **gelöscht** (dieselbe Einlösung, keine Entscheidung an ihm gebucht; seine Sperrzeilen gehen per Kaskade); der andere behält die `IrcMessageId` |
  | beide `pending` | der mit der größeren ID wird gelöscht |
  | beide nicht `pending` | beide bleiben (keine gebuchte Entscheidung wird umgeschrieben); der mit der größeren ID gibt seine `IrcMessageId` ab (null), **bevor** er verschoben wird |

  Gelöscht statt nach `merged` geführt wird, weil ein offener Wunsch ohne Emote in `merged` den CHECK aus 2.8a verletzte
  und `MergeChannels` in der Übergangstabelle nur für laufende Zustände steht. Kein Wunsch wird verlängert. Die leere
  Emote-ID nimmt an der bestehenden Kollisionsprüfung der Zusammenführung nicht teil (`loserEmoteIds` ohne die leere ID,
  sonst sperrte sie alle offenen Wünsche des Überlebenden mit). Der Zählwert der gelöschten Dubletten steht im
  Audit-Eintrag der Zusammenführung.
- **Grenzfälle:** mehr als zehn Belohnungen danach; offene Wünsche desselben Zuschauers in beiden Kanälen über der Grenze
  (bleiben, die Grenze gilt nur fürs Anlegen); dieselbe `IrcMessageId` in beiden Kanälen in den drei Lagen der
  Regel oben, an von Hand gebauten Zeilen gegen das migrierte Schema (auch: offener Verlierer-Wunsch ohne Emote wird
  verschoben, ohne CHECK oder Index zu verletzen); danach dieselbe Beobachtung noch einmal über `ProcessAsync` → `Duplicate`.
- **AKs:** AK68 (die „anschließende Einlösung“ über `ProcessAsync` im selben Test). **Fertig:** grün.
  **Commit:** `feat: carry wish rewards, learning and gaps through a channel merge`

### T10: Worker, Lesen

- **Ziel:** 2.1. **Dateien:** neu `src/EmotePurge.Worker/WishCapture/RewardRedemptionParser.cs`, `RewardObservationQueue.cs`
  (samt `IRewardObservationSink`), `ChatMessageRoute.cs` (die Reihenfolge-Funktion); `TwitchChatManager.cs` (Konstruktor,
  der eine Zweig im Handler), `WorkerStats.cs`, `WorkerServiceRegistration.cs` (`AddWorkerCore`); Tests neu
  `RewardRedemptionParserTests.cs`, `RewardRedemptionTwitchLibTests.cs`, `RewardObservationQueueTests.cs`,
  `ChatMessageRouteTests.cs`; `docs/DECISIONS.md`.
- **Vertragstest (AK48) [W1]:** Eine erfundene Zeile in der Form der Live-Probe (alle Tags der Probe, erfundene Werte
  **[W3]**) geht durch TwitchLibs eigenen Parser. Der Test hält fest: `CustomRewardId` trägt den Wert; `UndocumentedTags`
  enthält den Schlüssel **nicht**; `Id`, `TmiSent`, `RoomId`, `UserId`, `Username`, `Message` tragen die Werte der Zeile. Er
  bricht, wenn eine neue Version die Eigenschaft nicht mehr füllt. Dazu die Zeilen aus `SharedChatRuleTwitchLibTests` (nur
  Quell-Marker ohne `source-room-id`, je Marker, auch mit leerem Wert), um den Tag ergänzt: über Zerlegung,
  `SharedChatRule.FromTags` und Parser keine Beobachtung.
- **Grenzfälle:** Tag mit leerem Wert; `source-room-id` gleich und ungleich Raum; gespleißte Zeile mit Tag; Text mit
  `ACTION`; Text über 500 Zeichen; Warteschlange bei 1.000 und 1.001; Senke wirft (Handler zählt, Zählung der Emotes läuft);
  Schalter aus (Senke nie gerufen, auch nicht mit Tag); ausgeschlossener Chatter mit Tag; leerer Snapshot mit Tag.
- **AKs:** AK47, AK48, AK49, AK65 (Manager-Hälfte), AK67. AK49 wird auf der Ebene geprüft, auf der die Zählung liegt: die
  Reihenfolge-Funktion liefert für eine Nachricht mit und ohne Tag dasselbe `MatchEmotes`; die echte Zählung prüft T19.
- **Zusatz aus Runde 2:** Die Senke kennt „geschlossen“ (`Complete`): danach weist `TryEnqueue` ab und zählt, ohne zu
  werfen (Sol-R2-P2-8; Test). `WorkerStats` zählt Beobachtungen, die bei **leerem Snapshot** des Kanals entstanden sind;
  der Zähler erscheint als Zahl in der Minuten-Zeile des `WishCaptureWorker` und ist der Live-Nachweis für AK67
  (Sol-R2-P2-9). Der Handler liest den Snapshot dafür vor der Reihenfolge-Funktion (derselbe eine Zugriff wie heute).
- **Regel 16:** `TwitchChatManager` wird nicht gegen einen Fake getestet. Seine Änderung gilt erst mit bestandenem T12 als
  verifiziert.
- **Fertig:** `dotnet test tests/EmotePurge.Worker.Tests` grün. **Commit:** `feat: read custom-reward-id into a bounded observation queue`

### T11: Worker, Dienst und Lücken

- **Ziel:** 2.3 und 2.7. **Dateien:** neu `Worker/WishCapture/WishCaptureWorker.cs`,
  `CaptureGapTracker.cs` (samt `ICaptureGapRecorder`); `TwitchConnectionWatchdog.cs` (zwei Aufrufe),
  `WorkerHealthPublisher.cs` und `Core/Services/IWorkerHealthReader.cs` (`WishCaptureActive`),
  `WorkerServiceRegistration.cs` (Kommentar „sixteen“, Registrierung als letzter); Tests neu `WishCaptureWorkerTests.cs`,
  `CaptureGapTrackerTests.cs`, Ergänzung `WorkerServiceRegistrationTests.cs` und der Health-Tests. `Worker.cs` und
  `BotCommands.cs` bleiben unberührt (kein Kommando).
- **Grenzfälle:** **Schalter (Sol-P1-2):** Erfassung im Worker aus (Haushalt läuft, keine Abarbeitung, der Dienst wird nie
  mit einer Beobachtung gerufen, Startlogzeile „disabled“, Health-Feld falsch); Erfassung an (Startlogzeile „enabled“,
  Health-Feld wahr); der Schalter der Api kommt im Worker nirgends vor. Erster Takt liest den Stempel vor dem Schreiben;
  Stempel wird bei offener Verbindungslücke nicht geschrieben; Dienstfehler dreimal, dann Verwerfen; **Stopp-Budget (Sol-R2-P2-8):** 1.000 Beobachtungen in der
  Warteschlange und ein Dienst, der je Aufruf 2 Sekunden braucht → `StopAsync` kehrt binnen 5 Sekunden plus Toleranz
  zurück (gemessen mit der `TimeProvider`-Fälschung), der Rest ist gezählt und in einer Zeile geloggt, nach dem Schließen
  eingereichte Beobachtungen sind abgewiesen und gezählt; ein zweiter Test stoppt den echten `UsageFlushWorker` danach mit
  dem verbleibenden Budget und sieht dessen letzten Flush vollständig; Bestand nicht lesbar (alter bleibt, Warnung gedrosselt). **Tracker (Sol-P2-8):** Start mit Stempel und
  leerem Roster → Lücke für jeden Kanal des Bestands; Kanal wird erst Minuten später gejoint → seine Lücke reicht vom
  Stempel bis zu seinem bestätigten Join; `004` da, Joins noch offen → nichts geschlossen; absichtlicher Reconnect mit
  Rejoin nach 20 s und einem letzten Frame, der 4 Minuten alt ist → **keine** Zeile; Trennung 5 min → eine Zeile ab dem
  Signalzeitpunkt; Trennung während offener Neustart-Lücke (bleibt eine Lücke mit dem ersten Grund); Kanal nach der
  Rejoin-Runde offen (bleibt offen, kein zweiter Eintrag `not_joined`); Kanal fällt aus dem Bestand; zwei
  Wiederaufbau-Versuche in Folge (ein Beginn).
- **AKs:** AK62, AK65, AK66 (Takt). **Tests:** container-frei mit `TimeProvider`-Fälschung und Substituten für Dienst und
  Manager (der Dienst selbst ist ohne Vorfilter derselbe Weg wie im Integrationstest aus T5; es gibt keinen zweiten
  Entscheidungspfad im Worker, den nur ein Test über die ganze Pipeline sähe), nach dem Vorbild `WishExpiryWorkerTests` und `ChatLogBackfillWorkerTests`.
- **Fertig:** grün; die Zahl der Hosted Services im Registrierungstest ist sechzehn.
  **Commit:** `feat: drain reward observations and track capture gaps in the worker`

### T12: Frühe Live-Probe des Transports

Abschnitt 7, Stufe L0. Vor jeder Oberflächen-Arbeit. Ergebnis als `docs/nachtlaeufe/live-486-L0-<Datum>.md`.

### T13: Web, Modell und Regeln

- **Ziel:** Typen, Routen, Schalter, reine Regeln aus 2.10. **Dateien:** `core/wishes/wish.model.ts`, `wish.service.ts`,
  `wish-view.ts`, `wish-error.ts`, `core/channels/channel.model.ts` (`wishCaptureEnabled`), `e2e/support/mocks.ts`
  (`mockChannelPermissions`). Die K3-Typen bleiben (2.8a); neu ist `hasEmote` in `wish-view.ts`.
- **AKs:** stützt AK69. **Specs (Regel 12):** `wish.service.spec.ts` (jede neue Route: Methode, Pfad, Rumpf);
  `wish-view.spec.ts` (Aktionstabelle je Hinweis als Tabelle, `unlink` nur bei `irc` und `auto`, unbekannter Hinweis ändert
  nichts; `hasEmote` für leere und gefüllte ID); Fehlertext-Zuordnung der zehn Codes.
- **Fertig:** Vitest, Lint, Format grün. **Commit:** `feat(web): model and rules for captured wishes`

### T14: Web, Bereich „Offen“

- **Ziel:** 2.10, Arbeitsliste. **Dateien:** `features/wishes/channel-wishes-page.ts`, `wish-row.ts`, `wish-labels.ts`,
  `wish-edit-dialog.ts` (Laufzeit allein, Sol-P3-12), neu `wish-approve-dialog.ts`, `core/wishes/wish-emote-preview.service.ts`; Locale-Dateien.
- **Grenzfälle:** offener Wunsch ohne Emote; Einlösetext mit 500 Zeichen, mit Markup-Zeichen, mit Adresse (bleibt Text);
  `rewardLabel` null; 409 beim Freigeben in beiden Formen; Antwort `merged`; `wish.changed` während eines offenen Dialogs
  (der Dialog bleibt, die Liste dahinter lädt; ein inzwischen `active` gewordener Wunsch beantwortet das Absenden mit 409
  und einer Meldung); Vorschau-Read gedrosselt; `Wishes` an und Erfassung aus (offene Wünsche bleiben bedienbar).
- **AKs:** AK69 (Arbeitsliste). **Specs (Regel 12):** Komponenten-Verhalten: welche Aktion je Hinweis angeboten wird,
  Dialog-Rückgaben, Sperrgrund des Absendens ohne Emote, Zustandsübergang nach 409, zugänglicher Name der Gruppe „Offen“;
  `wish-edit-dialog.spec.ts`: für einen offenen Wunsch ohne Emote gibt es kein Alias-Feld, das Absenden ist ohne Alias
  möglich und der Rumpf ist genau `{ durationDays }`; für einen Wunsch mit Emote unverändert;
  Freigabe-Dialog: Antwort `merged` zeigt den neuen Ablauf des Zielwunsches, 409 `wish_running_not_extendable` lässt den
  Wunsch offen und nennt den Grund;
  `wish-emote-preview.service.spec.ts` (Reihenfolge, Zwischenspeicher, Stopp nach Drosselung). Keine Klassen, keine
  Schnappschüsse.
- **Fertig:** Vitest, Lint, Format grün. **Commit:** `feat(web): work list section for captured wishes`

### T15: Web, Belohnungen und Anlernen

- **Ziel:** 2.10, Settings-Abschnitt. **Dateien:** neu `features/channel-workspace/wish-rewards-section.ts` (+ Spec),
  `wish-settings-section.ts`, `channel-settings-page.ts`; Locale-Dateien.
- **Grenzfälle:** kein `canEdit`; zehn Belohnungen (Start gesperrt mit Grund); Kandidat erscheint per `wish.changed`;
  Restzeit läuft ab, während der Kandidat gezeigt wird; `confirm` antwortet 409 `wish_reward_no_candidate` (zurück nach
  `idle` mit Meldung); zweiter Verwalter startet parallel (409 `wish_learning_active`, Stand neu laden); zweiter Verwalter hat
  abgebrochen und neu gestartet, der eigene Dialog zeigt noch den alten Kandidaten (Bestätigen und Abbrechen → 409
  `wish_learning_stale`, nichts gespeichert, neuer Stand); `channelBound: false`; alter
  `captureAliveAtUtc`; keine Lücken; Verbindungsabriss des Live-Stroms (Aufholen aus K3).
- **AKs:** AK69 (Anlernen). **Specs (Regel 12):** Zustandsmaschine des Anlernens als Übergangstabelle; gespeichert wird
  erst auf Bestätigen, und zwar mit den Kennungen des angezeigten Kandidaten; Feldfehler; Nur-Lesen-Zustand; Reihenfolge der Meldungen nur, soweit `docs/UI-Designsprache.md` sie
  als Vertrag führt.
- **Fertig:** Vitest, Lint, Format grün. **Commit:** `feat(web): learn and manage wish rewards in the settings tab`

### T16: Web, Audit und i18n-Abschluss

- **Ziel:** sieben Audit-Aktionen in `core/audit/audit.model.ts`, `shared/audit/audit-actions.ts` (die festgenagelten
  Zahlen in `audit-actions.spec.ts` nachziehen), `audit.actions.*` in beiden Sprachen; Gegenprobe aller `wish_*`-Codes
  zwischen `ApiErrorCodes.cs`, `api-error.ts` und den Locales von Hand, Ergebnis mit Zahlen in DECISIONS; Wortlaut aller
  neuen Schlüssel in de und en gegen die fertige Oberfläche; `features/admin/admin-monitoring-page.ts`: Zeile
  „Wunsch-Erfassung“ (Worker an/aus/unbekannt, Abweichung zur Api markiert), mit Spec für die drei Lagen.
- **Fertig:** Vitest grün. **Commit:** `feat(web): audit actions and texts for wish capture`

### T17: E2E und Audit-Harness

- **Ziel:** Abschnitt 6; dazu Harness-Szenarien `wishes-pending` (Arbeitsliste mit je einem Hinweis, lange Texte) und
  `wish-rewards-learning` (Kandidat sichtbar) in de und en, hell und dunkel, 360 px, mit den Gates aus Plan-485 T14.
- **Fertig:** `npm --prefix web run e2e` grün (keine Api auf `:5151`). **Commit:** `test(web): e2e for wish capture`

### T18: Doku

- **Ziel und Dateien:** `docs/DECISIONS.md` (Abschluss des K6-Eintrags: die Lücken sind eine Näherung mit etwa einem Takt
  bzw. einer Rejoin-Runde Genauigkeit; die Datumskorrektur gilt nur für K6-Autozuordnungen; wer K6-Daten sieht (2.4a); Probe-Ergebnis AK46 als eigener Absatz, alle [E]
  und [W] dieses Plans, Stand „nicht deployt“); `docs/Operations.md` „Wish emotes“ (**Schalter: beide Dienste aus derselben Variable, der
  Worker ist maßgeblich; Abschalten zuerst am Worker, dann an der Api; nach einem Teil-Rollback beide Dienste neu starten;
  wo man den Zustand sieht: Startlogzeile, `/admin/monitoring`; Rückweg auf ein Image vor K6 ist verträglich, lässt aber
  offene Wünsche nicht verfallen und leert den Einlösetext nicht**; Migration
  `AddWishCapture` von Hand vor den Images, `list` zeigt auf Prod zusammen mit `AddWishes` zwei ausstehende, Haushalts-Takt
  läuft immer, Fristen, Rollback, Abfrage für Messgröße (c), Datenschutz-Voraussetzung); `docs/Architectur.md`
  (Worker-Liste: sechzehn, Datenfluss der Erfassung, Tabellen); `CLAUDE.md` (Zeile „Wunsch-Emotes“, Worker-Absatz:
  sechzehn Hosted Services, `WishCaptureWorker`); `docs/Feature-Ideen-2026-08-01.md` (Statuszeile A18); die Spec bekommt
  unter K6 einen Absatz „Stand der Umsetzung“ mit Verweis auf Abschnitt 9 dieses Plans; `.env.example`.
- **AKs:** AK46 (Eintrag). **Commit:** `docs: wish capture (child 6)`

---

## 4. AK-Abdeckung

| AK | Inhalt in einem Satz | Task(s) |
|---|---|---|
| AK46 | Probe ohne Mod-Status: nur noch festhalten, bleibt Regressionsfall | T18 (Eintrag), T19 (A6) |
| AK47 | Parser: sieben Felder, Raum-Fälle, Marker | T10 |
| AK48 | Vertragstest zum TwitchLib-Feld, Marker-Zeilen ohne Beobachtung | T10 |
| AK49 | Emote-Zählung unverändert | T10 (Reihenfolge), T19 (A4) |
| AK50 | Anlernen: Kandidat, Bestätigen, 10 und 11 Minuten | T4, T5, T11 (Takt), T19 (A2, C2) |
| AK51 | ohne Anlernen nichts gespeichert | T5 (Lesen ohne Sperre, keine Transaktion), T19 (L0-2, A9) |
| AK52 | Laufzeit der Belohnung, elfte Belohnung | T4, T5 |
| AK53 | Dedupe, Grenze je Zuschauer, Zähler im Log | T5, T11 |
| AK54 | `WishTextParser` als Tabelle | T2 |
| AK55 | Zuordnung über Link auf drei Wegen | T3 |
| AK56 | Zuordnung über Namen, Mehrdeutigkeit | T3 |
| AK57 | kein Treffer außerhalb des Fensters, nach Verfall | T3, T5 |
| AK58 | zwei offene Wünsche, Freigabe wird Verlängerung | T3, T6 |
| AK59 | Freigeben, Ablehnen, zwei gleichzeitige | T6 |
| AK60 | Verfall mit 14 und 1 Tag | T5 |
| AK61 | `unlink`: Felder, Sperre, 409 | T6, T3 |
| AK62 | Lücken und ihre Retention | T11, T5, T8 |
| AK63 | Filter-Matrix | T7 |
| AK64 | Text und Login nur auf Verwalter-Routen, Retention | T5, T6, T8 |
| AK65 | Schalter aus, sechzehn Hosted Services | T1, T10, T11 |
| AK66 | Aufräumen unabhängig vom Schalter | T4, T5, T11, T19 (C2) |
| AK67 | leerer Emote-Cache | T10, T19 (A7 nach V10) |
| AK68 | Kanal-Zusammenführung | T9 |
| AK69 | Oberfläche: Anlernen, Aktionen je Hinweis, live | T14, T15, T17 |
| AK70 | Live-Gesamtweg | T19 |

Nicht K6, obwohl in der Nähe: AK17/AK18 (K1) werden nur erweitert (T8). Die Bot-Fälle der Übergangstabelle (K4).

---

## 5. Specs nach Regel 12 (Verhalten, nicht Vorlage)

| Datei | Geprüft wird |
|---|---|
| `core/wishes/wish-view.spec.ts` | Aktionstabelle je Hinweis, `unlink`-Bedingung, `needsAction` unverändert |
| `core/wishes/wish.service.spec.ts` | neue Routen mit `HttpTestingController` |
| `core/wishes/wish-emote-preview.service.spec.ts` | ein Read je ID, Reihenfolge, Stopp nach Drosselung |
| `features/wishes/wish-row.spec.ts` | angebotene Aktionen und zugänglicher Name je Hinweis; Titel ohne Emote |
| `features/wishes/wish-approve-dialog.spec.ts` | Rückgabe, Sperre ohne Emote, Kopplung von Read und Feldern, 409-Wege |
| `features/wishes/channel-wishes-page.spec.ts` | Gruppe „Offen“ aus `view=open`, Neuladen auf `wish.changed` |
| `features/channel-workspace/wish-rewards-section.spec.ts` | Zustandsmaschine, speichert erst auf Bestätigen, Nur-Lesen, Warnzeile |

Rein darstellende Teile (Lückenliste, Hinweis-Sätze) bekommen keinen Spec. Kein Spec prüft CSS-Klassen, Verschachtelung oder
den Wortlaut einer Übersetzung als Selbstzweck.

---

## 6. E2E-Fälle (Playwright)

Datei `web/e2e/wishes-capture.e2e.spec.ts`. `test`/`expect` aus `web/e2e/support/test.ts`, `/api/**` je Test gemockt,
`mockChannelPermissions` mit `wishesEnabled` und `wishCaptureEnabled`, Live-Ereignisse über `__emitLive`. Zeitgesteuerte
Fälle: `page.clock.install()` vor `goto`, dann `runFor`, nie `fastForward`.

| # | Fall | Kern der Prüfung |
|---|---|---|
| E1 | Anlernen bis zur Bestätigung | Start → Zustand „wartet“ mit Restzeit; `__emitLive('wish.changed')` → Kandidat mit Text und Login; **kein** `POST …/confirm` vor dem Klick; nach Bezeichnung und Laufzeit genau ein `confirm` mit diesem Rumpf; Belohnung in der Liste |
| E2 | Anlernen läuft ab | mit `page.clock` 10 Minuten: Zustand „abgelaufen“, Start wieder möglich, kein `confirm` gesendet |
| E3 | Anlernen parallel | `POST …/learning` antwortet 409 `wish_learning_active` → Meldung, Stand neu geladen |
| E4 | Offener Wunsch wird aktiv | Zeile unter „Offen“ mit Einlösetext; `wish.changed` und neue Antwort → dieselbe Zeile unter den laufenden mit „Zuordnung lösen“, ohne Reload |
| E5 | Ablehnen | Bestätigung nennt die Erstattung in Twitch; nach `reject` steht der Wunsch unter „Beendet“ |
| E6 | Freigeben ohne Emote | Namens-Wunsch mit `unparsed`: Absenden gesperrt mit Grund; nach Eingabe eines Links ein `approve` mit der ID |
| E7 | Freigeben trifft „schon im Set“ | 409 `wish_emote_already_in_set` → Angebot „Bestand übernehmen“ öffnet den Übernehmen-Dialog für diesen Wunsch |
| E8 | Hinweis `already_running` | Aktion verlängert den laufenden Wunsch; Antwort `merged` → Meldung, Zeile unter „Beendet“ |
| E9 | Zuordnung lösen | Bestätigung; nach `unlink` steht der Wunsch wieder unter „Offen“ |
| E10 | Erfassung aus | `wishCaptureEnabled: false`: kein Belohnungs-Abschnitt, kein Abruf von `…/rewards`; ein vorhandener offener Wunsch bleibt bedienbar |
| E12 | Veralteter Anlern-Dialog | Kandidat sichtbar; `confirm` trägt `sessionId` und `candidateId` des gezeigten Stands; Antwort 409 `wish_learning_stale` → Meldung, neuer Stand geladen, keine Belohnung in der Liste |
| E13 | Laufzeit eines offenen Wunsches ohne Emote | Bearbeiten-Dialog ohne Alias-Feld; `PATCH` mit genau `{ durationDays }` |
| E14 | Freigeben ohne buchbare Verlängerung | `approve` antwortet 409 `wish_running_not_extendable` → Meldung, der Wunsch steht weiter unter „Offen“ mit `already_running`; im zweiten Anlauf Antwort `merged` → Meldung nennt den neuen Ablauf |
| E11 | Grober Zeiger | im Projekt `touch-mobile`: Freigeben, Ablehnen und Anlernen vorhanden, keine 7TV-Schreibknöpfe |

---

## 7. Live-Verifikation mit echtem Chat (T12 und T19; AK46, AK67, AK70, Regel 16)

**Rolle nach Regel 16.** `TwitchChatManager` ist eine Transportklasse und wird nicht gegen einen Fake getestet; die reinen
Teile (Parser, Reihenfolge, Tracker) tragen ihre Tests im container-freien `Worker.Tests`. Ob der Tag über die echte
Verbindung durch den geänderten Handler bis in die Datenbank kommt, zeigt **nur** diese Probe. Sie ist ein Gate, kein
Nachtrag: ohne bestandenes L0 keine Oberflächen-Arbeit, ohne bestandene Stufen A bis C kein PR.

**Probe-Kanal [B].** Der Affiliate-Kanal eines Freundes des Betreibers: Kanalpunkte vorhanden, es wird dort nicht gestreamt,
er dient nur dem Testen. **Variante B-I ist damit der Plan und Freigabekriterium (Sol-P2-11): AK70 läuft durchgehend live im
Probe-Kanal.** 7TV-Schreibzugriffe im Set des Probe-Kanals macht **ausschließlich `olaf_olaf_son`** als 7TV-Editor;
EmotePurgeBot bekommt keine Editor-Rechte (die Schreibfreigabe des Betreibers gilt nur für `olaf_olaf_son`). Das Set des
Probe-Kanals wird nach dem Test wiederhergestellt; der Vorher-Stand steht im Protokoll.

**Randbedingungen.** Kein `ssh`, kein Prod, **nie `docker compose` aus dem Worktree**. Die Dev-Datenbank `emotepurge` wird nur
gelesen (Dump), gearbeitet wird auf einer Kopie. Cookie und 7TV-Token des Testkontos liegen je in einer Scratchpad-Datei mit
Modus 600, werden nie ausgegeben und nach dem Test gelöscht (Löschung im Protokoll). Die Einlösungen macht der Betreiber
selbst. Das Protokoll enthält keine Kanal- und Kontonamen, keine Logins und keinen Einlösetext außer den vorgegebenen
`EPTEST`-Marken. Die Kopie wird am Ende gelöscht.

| Ressource | Wert |
|---|---|
| Datenbank | Kopie `emotepurge_wish486` im Dev-Postgres-Container; `AddWishCapture` per `dotnet ef database update --connection …` **gegen die Kopie** angewendet, `migrations list` davor und danach |
| Redis | eigener Container per `docker run` auf abgestimmtem Port |
| Api | `dotnet run` mit absolutem Projektpfad aus dem Worktree auf `:5151`, `Wishes__Enabled=true`, `Wishes__Capture__Enabled=true` |
| Worker | ebenso; zusätzlich `Wishes__ExpiryIntervalMinutes=1`, `Worker__Debug__AllowTwitchReconnectTrigger=true`; `SevenTv__EventApi__Enabled` je Schritt |
| Web | `npm --prefix <Worktree>/web start`, `http://localhost:4200` |
| Andere Worker | Ein zweiter Worker an derselben Datenbank ist verboten (doppelte Lücken); der Dev-Stack-Worker hängt an der Originaldatenbank und stört nicht |

### Checkliste für den Betreiber (vor L0 erledigt, Stand im Protokoll abgehakt)

| # | Was | Warum, und woran es hängt |
|---|---|---|
| V1 | **Eigenes Konto (K1) ist Twitch-Moderator im Probe-Kanal.** | Verwalter in Emote Purge ist, wer Broadcaster, **Twitch-Moderator** oder globaler Admin ist (`ChannelAccessService.CanManageChannelAsync`, `:15-31`). **7TV-Editor allein reicht nicht**: das öffnet nur die Nutzungsansicht (`CanViewUsageStatsAsync`). Als Mod kann K1 außerdem Einlösungen in Twitchs Warteschlange erstatten. Kein Admin-Eintrag in der lokalen Api: die Probe soll den echten Rollenweg fahren |
| V2 | **`olaf_olaf_son` ist 7TV-Editor des Probe-Kanals** (vom Freund in 7TV eingetragen, mit dem Recht, Emotes im Set zu verwalten). EmotePurgeBot wird **nicht** eingetragen | einziger zulässiger Schreibweg nach 7TV; gebraucht ab Stufe B, für B4 auch im Browser (Token-Dialog mit dem Token von `olaf_olaf_son`) |
| V3 | **Der Probe-Kanal hat ein 7TV-Konto mit aktivem Set**, und der Betreiber notiert, ob das Set leer ist | ohne aktives Set gibt es keinen Sync und keine Zuordnung; ein leeres Set ist die Bedingung für A7 (AK67) |
| V4 | **Drei Test-Belohnungen im Probe-Kanal, ohne Abklingzeit, ohne Limit je Stream oder Nutzer, „Überspringen der Warteschlange“ aus, Kosten 1 Punkt:** **R-A** und **R-B** mit Pflicht-Texteingabe, **R-C** ohne Texteingabe. Namen frei, z. B. „EP-Test A/B/C“ | der Betreiber legt sie selbst an (die Rechte dafür vergibt der Freund); R-C ist die Negativprobe. **Zeitpunkt:** spätestens vor L0; sie bleiben bis nach Stufe C bestehen und werden danach gelöscht |
| V5 | **Zweites Konto (K2) ohne Mod- und VIP-Status im Probe-Kanal** | AK46 als Regression |
| V6 | **Punktestand von K1 und K2 im Probe-Kanal reicht für je etwa 20 Einlösungen.** In einem Kanal, der nicht streamt, sammelt niemand Punkte durch Zuschauen; ob der Bonus fürs Folgen genügt, zeigt der Punktestand selbst. Reicht er nicht: Kosten bleiben bei 1, und der Freund startet einmal kurz einen Stream, oder die Zahl der Einlösungen je Konto wird im Drehbuch gekürzt und das vorab festgehalten | ohne Punkte keine Einlösung; nicht aus dem Code ableitbar, deshalb vor L0 am Kanal prüfen |
| V7 | **Der Probe-Kanal ist in der Kopie ein aktiver Kanal mit gesetzter `TwitchChannelId`** (K1 tritt ihm über die Oberfläche der lokalen Api bei und wartet den ersten Sync ab; der Orchestrator prüft die Spalte) | ohne Bindung wird weder angelernt noch erfasst (2.4) |
| V8 | **Vorher-Snapshot des 7TV-Sets des Probe-Kanals** (Set-ID, je Eintrag Emote-ID, Alias, `addedAt`) durch den Orchestrator, ins Protokoll | Grundlage der Wiederherstellung in B6 |
| V9 | **Vier Test-Emotes, die nicht im Set liegen** (IDs wie in T16), eines davon mit bekanntem Namen; für A4 ein Emote, das im Set liegt (entfällt bei leerem Set, siehe A4) | Drehbuch |
| V10 | **AK67 vorab entschieden [B]:** Ist das Set laut V3 leer, oder lässt der Freund für A7 kurz ein leeres Set aktiv schalten, läuft A7 live. Sonst bleibt AK67 beim Test aus T10; diese Abweichung wird **vor** dem Lauf im Protokollkopf festgehalten, nicht danach, und **AK70 gilt dann als „teilweise geprüft“, nicht als bestanden** (AK70 nennt den Kanal mit leerem Set als Regressionsfall) | Sol-P2-11, Sol-R2-P2-9 |
| V11 | **Admin-Sicht getrennt von K1.** Die lokale Api läuft mit `Auth__AdminTwitchUserIds__0` = Twitch-ID von `olaf_olaf_son`; der Orchestrator liest `GET /api/admin/health` und `/admin/monitoring` mit dessen Session-Cookie (liegt aus T16 vor bzw. kommt neu, Modus 600). **K1 steht in keiner wirksamen Admin-Liste:** Der Orchestrator prüft vor L0, dass `/permissions` für K1 im Probe-Kanal die Verwaltung über die Mod-Rolle ausweist und K1 `/admin/*` nicht erreicht (403); ein Eintrag aus den lokalen User-Secrets wird für die Probe überschrieben | L0-1, C1, C3 brauchen die Admin-Health; der Rollenweg von K1 bleibt der echte |

### Stufe L0: Transport (T12), etwa 15 Minuten

| # | Wer | Handlung | Erwartet | Messgröße |
|---|---|---|---|---|
| L0-1 | Orchestrator | Worker starten, Join des Probe-Kanals abwarten | Startlogzeile `Wish capture is enabled.`; Roster bestätigt den Kanal; `GET /api/admin/health` mit dem Admin-Konto aus V11 nennt `wishCaptureActive: true`; dieselbe Route mit dem Cookie von K1 antwortet 403 | Zeit bis Join |
| L0-2 | Betreiber, K1 | R-A einlösen, Text `EPTEST-1` | ohne Anlernen: **keine** Zeile in `Wishes`, `WishRewardLearnings`, `WishCaptureReceipts` | Zeilenzahlen vorher/nachher |
| L0-3 | Orchestrator | Anlernen per `curl` starten, **sofort danach** L0-4 | 201 mit `sessionId` | – |
| L0-4 | Betreiber, K1 | R-A einlösen, Text `EPTEST-2`, binnen Sekunden nach L0-3 | Kandidat in `GET …/rewards` mit genau diesem Text (die erste Einlösung nach dem Start zählt, Sol-P2-5) | Sekunden von der Einlösung bis zum Kandidaten |
| L0-5 | Orchestrator | Worker-Log der letzten 10 Minuten durchsuchen; Anlernen per `DELETE` mit `sessionId` beenden | kein `EPTEST`, kein Login, keine Nutzer-ID in irgendeiner Zeile; keine erzwungene Wiederverbindung | Trefferzahl 0 |

**Abbruch L0:** Kommt der Kandidat nach zwei Einlösungen nicht an, wird nicht weiter eingelöst, sondern die Methode
gewechselt (Rohzeile mit dem Wegwerf-Client der Probe P1 gegenprüfen). Ein Text im Log ist ein P1-Befund und stoppt alles.

### Stufe A: Erfassung im Probe-Kanal (kein 7TV-Schreiben)

Der Orchestrator führt je Konto eine Zählung der offenen Wünsche mit (Spalte „offen danach“, Form K1/K2); die Grenze ist 3.

| # | Wer | Handlung | Erwartet | offen danach |
|---|---|---|---|---|
| A1 | Betreiber im Browser | Settings-Abschnitt: Anlernen starten | Zustand „wartet“ mit Restzeit | 0/0 |
| A2 | Betreiber, K1 | R-A einlösen, Text `EPTEST-A2` | Kandidat erscheint **ohne Reload**; Bezeichnung „Test A“, 7 Tage, bestätigen; Belohnung in der Liste; Anlern-Zeile weg; **kein** Wunsch aus dieser Einlösung | 0/0 |
| A3 | Betreiber, K1 | **sofort nach dem Bestätigen** R-A einlösen, Text = 7TV-Link von Test-Emote 1 | offener Wunsch unter „Offen“ ohne Reload: Text, Login, „Test A“, 7 Tage, `matchKind = link`; Bild und Name über den Browser-Read | 1/0 |
| A4 | Betreiber, K1 | R-A einlösen, Text = Name eines Emotes, das **im Set liegt** (bei leerem Set entfällt A4 samt der Ablehnung in A5: `already_in_set` zeigt dann B5a, und AK49 wird nach B1 mit einer Einlösung geprüft, deren Text den Alias von Test-Emote 1 enthält) | offener Wunsch mit Hinweis `already_in_set`; **AK49:** nach dem nächsten Flush ist die Nutzung dieses Emotes um genau 1 gestiegen | 2/0 |
| A5 | Betreiber, K1 | den Wunsch aus A4 ablehnen; Anlernen starten, R-B einlösen (`EPTEST-A5`), „Test B“ mit 30 Tagen bestätigen; danach R-B einlösen mit einem beliebigen Namen (`EPTESTNAME`) | Wunsch aus R-B trägt 30 Tage, der aus A3 weiter 7 (AK52) | 2/0 |
| A6 | Betreiber, **K2** | R-A einlösen, Text = Link von Test-Emote 1 (dasselbe wie A3) | zweiter offener Wunsch für dasselbe Emote, mit dem Login von K2 (AK46, Regression) | 2/1 |
| A7 | Orchestrator, dann Betreiber K1 | **nur nach V10.** Vorbereitung: aktives Set in 7TV leer; Vollsync abwarten; per SQL belegen, dass der Kanal **0 nicht archivierte Slots** hat; Worker neu starten (der Warmstart baut den Snapshot aus Postgres) und den Join abwarten; `LastSyncedAtUtc` notieren. Dann löst K1 R-A ein (`EPTEST-A7`), **bevor** irgendetwas ins Set kommt | Wunsch entsteht; **Nachweis des leeren Snapshots zum Einlösezeitpunkt:** die Minuten-Zeile des `WishCaptureWorker` zählt genau 1 Beobachtung „bei leerem Snapshot“ (Zähler aus T10), und die Slot-Zahl ist nach der Einlösung weiter 0. Ohne diese Zeile gilt A7 als nicht bestanden, auch wenn der Wunsch entstanden ist (ein warmer Cache hätte ihn ebenso erzeugt). Danach ablehnen | 2/1 |
| A8 | Betreiber, K1 | R-C (ohne Texteingabe) einlösen | nichts, auch kein Zähler | 2/1 |
| A9 | Betreiber im Browser | „Test B“ **vergessen**; K1 löst R-B ein | keine Zeile in irgendeiner Tabelle (AK51: unbekannte Belohnung ohne Anlernen); der Wunsch aus A5 bleibt, seine Belohnungs-Bezeichnung ist leer. Danach R-B wie in A5 erneut anlernen (ohne weitere Einlösung als die des Anlernens) | 2/1 |
| A10 | Betreiber, K2 | R-A viermal hintereinander einlösen (`EPTEST-A10-1` bis `-4`) | zwei werden Wünsche, zwei verworfen (Grenze 3 mit dem aus A6); Logzeile zählt zwei über der Grenze | 2/3 |
| A11 | Betreiber im Browser | „Test A“ pausieren; K1 löst R-A ein; wieder aktivieren und **sofort** erneut einlösen (`EPTEST-A11`) | pausiert: kein Wunsch, „zuletzt erkannt“ unverändert; nach dem Aktivieren: die erste Einlösung wird ein Wunsch (Sol-P2-5) | 3/3 |
| A12 | Betreiber im Browser | die beiden Wünsche aus A10 und den aus A11 und den Namens-Wunsch aus A5 ablehnen | „abgelehnt“ unter „Beendet“, Hinweis auf die Erstattung; der Betreiber erstattet in Twitch selbst | 1/1 |
| A13 | Orchestrator | (a) `DEBUG:TWITCH-RECONNECT` senden; (b) danach den Worker stoppen, 3 Minuten warten, starten | (a) Rejoin unter 60 s: **keine** Lückenzeile; (b) eine Zeile `worker_restart` für den Probe-Kanal ab dem Lebensstempel bis zum bestätigten Join, keine für Kanäle ohne Belohnung; sie steht im Settings-Abschnitt | 1/1 |
| A14 | Betreiber, K1 | während der Worker in A13 (b) steht: R-A einlösen (`EPTEST-A14a`); nach dem Start und bestätigtem Join erneut (`EPTEST-A14b`). **Vorbedingung geprüft: K1 hat höchstens 2 offene** | der erste wird kein Wunsch (liegt in der Lücke), der zweite schon; danach den zweiten ablehnen | 1/1 |
| A15 | Orchestrator | Netz des Workers für 3 Minuten trennen, ohne ihn zu beenden (Firewall-Regel auf den Twitch-Endpunkt oder Netzwerk des Prozesses; mit dem Betreiber abgestimmt), dann wieder zulassen | der Watchdog baut neu auf; nach dem bestätigten Join eine Zeile `disconnect` für den Probe-Kanal von etwa der Dauer des Ausfalls (Beginn = Signalzeitpunkt) | 1/1 |

### Stufe B: Gesamtweg im Probe-Kanal (AK70, Variante B-I)

Am Anfang offen: K1 mit dem Link-Wunsch aus A3, K2 mit dem aus A6.

| # | Wer | Handlung | Erwartet | offen danach |
|---|---|---|---|---|
| B1 | Orchestrator (Token `olaf_olaf_son`) | Test-Emote 1 **direkt in 7TV** ins Set des Probe-Kanals, EventAPI aus | nach dem nächsten Vollsync steht der ältere Wunsch (A3) **ohne Handgriff** auf „aktiv“; `StartsAtUtc` = `addedAt` aus 7TV auf die Millisekunde; `LinkOrigin = auto`; genau ein `wish.autoLink`; der jüngere (A6) bleibt offen mit `already_running` (AK58) | 0/1 |
| B2 | Betreiber K1, dann K2, dann Orchestrator | K1 löst R-A mit dem **Namen** von Test-Emote 2 ein, danach K2 mit dessen **Link** (beide offen, beide vor dem Hinzufügen); dann das Emote unter genau diesem Namen in 7TV hinzufügen, EventAPI an | der ältere (K1, Name) erst „aktiv (vorläufig)“, „Entfernen“ gesperrt mit Grund, nach dem Vollsync bestätigt, Emote-ID aus dem Slot; der jüngere (K2) bleibt offen mit `already_running` | 0/2 |
| B3 | Betreiber im Browser | beim Wunsch von K1 aus B2 „Zuordnung lösen“ | er ist wieder „Offen“; der nächste Abgleich verknüpft ihn **nicht** erneut, auch nach einem Worker-Neustart; der Wunsch von K2 (vor dem Hinzufügen eingelöst, der Slot liegt in seinem Fenster) wird stattdessen „aktiv“ (AK61) | 1/1 |
| B4 | Betreiber K1 und Browser | **Vorbedingung geprüft: K1 hat höchstens 2 offene.** R-A einlösen mit Link von Test-Emote 3; „Freigeben“, dann „In 7TV hinzufügen“ aus der Zeile, Token von `olaf_olaf_son` im Token-Dialog | „freigegeben“ → nach dem Sync „aktiv“ (der Weg „Hinzufügen aus der Karte“, zwei Klicks nach [F3]) | 1/1 |
| B5 | Betreiber im Browser | den Wunsch aus A6 (Hinweis `already_running`) „Freigeben“ | er endet als „zusammengeführt“; der laufende aus B1 ist um die Kanal-Vorgabe verlängert, sein Zähler steht auf 1; Audit: `wish.approve` und `wish.extend` | 1/0 |
| B5a | Orchestrator, EventAPI an | **Sol-P1-3, live soweit herstellbar:** Test-Emote 4 in 7TV hinzufügen, **danach** löst K1 R-A mit dessen Link ein; Sync abwarten | der Slot ist älter als die Einlösung: Wunsch bleibt offen mit `already_in_set`, wird nicht aktiv, „Entfernen“ wird nie angeboten. (Das verspätete Delta selbst ist live nicht erzwingbar; es ist der Regressionstest aus T3) | 2/0 |
| B6 | Orchestrator | alle Test-Einträge aus dem Set des Probe-Kanals entfernen; Vergleich mit dem Snapshot aus V8 | dieselbe Set-ID, 0 fehlend, 0 zusätzlich, 0 mit geändertem `addedAt`, Aliase unverändert | – |

**Fallback B-II (nur wenn V2 nicht zustande kommt, und dann vor dem Lauf vom Betreiber als Abweichung angenommen):** B1 bis
B6 laufen im Kanal `olaf_olaf_son` auf derselben Kopie. Weil dort niemand einlösen kann, werden die offenen Wünsche als
markierter Handgriff in die Kopie eingefügt, mit den Feldwerten eines in Stufe A echt erfassten Wunsches. AK70 ist dann
nur in zwei Nähten gezeigt und **nicht** erfüllt; ohne ausdrückliche Annahme gibt es keinen PR.

### Stufe C: Schalter, Aufräumen, Rückweg

| # | Handlung | Erwartet |
|---|---|---|
| C1 | **Worker aus, Api an:** Worker mit `Wishes__Capture__Enabled=false` neu starten; K1 löst R-A ein (`EPTEST-C1`) | Startlogzeile „disabled“; **kein** Wunsch, kein Kandidat, kein Dedupe-Nachweis (Zeilenzahlen vorher/nachher); Warnzeile „Erfassung läuft nicht“ im Settings-Abschnitt nach höchstens 4 Minuten; `/admin/monitoring` zeigt die Abweichung; ein vorhandener offener Wunsch lässt sich ablehnen |
| C2 | **Aufräumen ohne Lesezugriff (AK66):** Worker wieder mit Erfassung starten; Anlernen starten, K1 löst ein (Kandidat liegt). Den Zeitpunkt `ExpiresAtUtc` der Zeile notieren. Worker stoppen und mit `Wishes__Capture__Enabled=false` so starten, dass sein erster Takt **nach** `ExpiresAtUtc` liegt (also nach Ablauf der 10 Minuten warten). **Bis zur Prüfung kein `GET …/rewards` und keine geöffnete Settings-Seite**; geprüft wird per SQL | vor dem Start: Zeile liegt noch, obwohl abgelaufen; nach dem ersten Takt: Zeile gelöscht. Wiederholung mit `Wishes__Enabled=false` am Worker. Gegenprobe: ein Neustart **vor** `ExpiresAtUtc` löscht die Zeile nicht |
| C3 | **Api aus, Worker an (Sol-P1-2):** Api mit `Wishes__Capture__Enabled=false`, Worker mit Erfassung; K1 löst R-A ein (`EPTEST-C3`) | Belohnungs-Routen 404 `wish_capture_disabled`, kein Belohnungs-Abschnitt, `approve` erreichbar; **der Wunsch entsteht** (der Worker ist maßgeblich) und steht in der Arbeitsliste; `/admin/monitoring` zeigt „Worker erfasst, Api ausgeblendet“ als Abweichung. Danach **beide** aus: K1 löst erneut ein (`EPTEST-C3b`) → nichts wird gespeichert |
| C4 | `PendingExpiryDays` auf 1, per markiertem Handgriff `StatusChangedAtUtc` eines offenen Wunsches um 25 Stunden zurück | binnen 15 Minuten „verfallen“, ohne Reload aus „Offen“ verschwunden |
| C5 | Handy-Durchgang (emuliert; am echten Gerät nach dem LAN-Weg aus `CLAUDE.md`, wenn der Betreiber ihn fährt) | Bereich „Offen“, Freigabe-Dialog und Anlernen bedienbar, kein horizontaler Überlauf bei 360 px |
| C6 | **Rückweg (Sol-P2-10, Sol-R2-P2-9):** Api und Worker aus dem Haupt-Checkout am Stand `264db942` (K3, ohne K6) gegen dieselbe Kopie mit den K6-Zeilen, zwei Durchgänge | **Durchgang 1, `Wishes__Enabled=true`:** `GET …/wishes?view=open` 200 und zeigt die offenen Wünsche neutral, `GET /api/wishes/summary` 200 und zählt sie, `GET /api/wishes/mine` 200; ein Vollsync des Probe-Kanals und ein Durchlauf des Ablauf-Laufs ohne Ausnahme. **Durchgang 2, `Wishes__Enabled=false`:** dieselben drei Routen **404 `wishes_disabled`** (`WishEndpoints.cs:25-37,292-317`), Worker-Log „Wish expiry worker is disabled“, Vollsync ohne Ausnahme. In beiden: keine Ausnahme in Api- und Worker-Log. Der Identitäts-Merge ist lokal nicht auslösbar und wird hier nicht behauptet; ihn deckt, dass keine Pflichtspalte NULL trägt (per SQL gezählt: 0). Danach zurück auf den Worktree: die Wünsche sind unverändert |

**Messgrößen im Protokoll:** Sekunden von der Einlösung bis zum Kandidaten bzw. zur Zeile in der Oberfläche (Erwartung unter
5 s, Befund ab 30 s); Zahl der Einlösungen gegen Zahl der Wünsche plus begründet verworfener (muss aufgehen, je Schritt mit
der `EPTEST`-Marke belegt); `StartsAtUtc` gegen `addedAt`; Nutzungszähler aus A4; Zahl der erzwungenen Wiederverbindungen
während der Probe (Erwartung 0 außer A13 und A15); Dauer der Lücken aus A13 und A15 gegen die gemessene Ausfallzeit; Treffer
von Text oder Login im Worker- und Api-Log (0).

**Abbruchkriterien:** Text, Login oder Nutzer-ID in einem Log oder Audit-Eintrag; eine Einlösung, die weder Wunsch noch
gezählt verworfen ist; eine Speicherung bei im Worker ausgeschalteter Erfassung (C1, C3b); eine erzwungene Wiederverbindung,
die mit einer Einlösung zusammenfällt; ein abweichender Nutzungszähler in A4; ein Vergleich in B6 mit Abweichung. Nach zwei
gleichartigen Fehlschlägen eines Schritts wird nicht ein drittes Mal eingelöst.

**Ergebnis für AK70 im Protokollkopf:** „bestanden“ nur mit B-I **und** A7; mit B-I ohne A7 „teilweise geprüft (ohne
Leer-Set-Fall, vorab angenommen)“; mit B-II „nicht erfüllt“.

Protokoll: `docs/nachtlaeufe/live-486-<Datum>.md` nach dem Aufbau von `live-T16-2026-11-19.md` (Checkliste V1 bis V11 mit
Stand, Tabelle je Stufe, Befunde, nicht Gefahrenes, Zustand am Ende, Löschung der Test-Belohnungen und der Kopie).

---

## 8. Gates vor dem PR (T20), Migration, Deploy

1. `dotnet build EmotePurge.slnx --no-incremental -warnaserror`, `dotnet format EmotePurge.slnx --verify-no-changes`,
   `dotnet test EmotePurge.slnx` grün (Docker läuft).
2. `npm --prefix web test -- --watch=false` grün (nicht `npx vitest`).
3. `npm --prefix web run lint` und `npm --prefix web run format:check` grün.
4. `npm --prefix web run e2e` grün, **nur wenn auf `:5151` keine Api lauscht** (die Api aus Abschnitt 7 vorher beenden). Rote
   Fälle quer durch fremde Dateien: erst `:5151`, dann Speicherdruck prüfen, Suite allein wiederholen.
5. Audit-Harness mit den Szenarien aus T17, Bilder in de und en gesichtet.
6. `node scripts/coverage-local.mjs` (nur Committetes zählt; die chirurgischen Änderungen in `TwitchChatManager.cs`,
   `TwitchConnectionWatchdog.cs`, `WishReconcileService.cs`, `WishService.cs`, `ChannelIdentityService.cs` sind der
   unscharfe Teil der Näherung. Der Zweig im Manager ist ohne Fake nicht gedeckt; deshalb liegt seine Logik in der
   Reihenfolge-Funktion und im Parser, der Rest ist eine Übergabe).
7. **Live-Verifikation: L0, Stufe A, B (Variante B-I) und C bestanden und protokolliert**, einschließlich B6 ohne
   Abweichung, C1 und C3 mit echter Einlösung nach dem Abschalten und C6 (Rückweg). **B-I ist Freigabekriterium
   (Sol-P2-11);** ein offener Schritt ist ein offenes Gate. Zwei Abweichungen sind möglich und nur gültig, wenn sie **vor**
   dem Lauf im Protokollkopf stehen und der Betreiber sie angenommen hat: AK67 ohne Live-Schritt (V10; AK70 steht dann als **teilweise geprüft** im
   Protokoll und im PR-Text, nicht als bestanden) und der Fallback B-II (dann ist AK70 nicht erfüllt, und der PR-Text sagt
   das im ersten Absatz).
8. **Orchestrator und Betreiber:** Der Plan ist zweimal adversarial geprüft, eine dritte Plan-Runde gibt es nicht;
   `/codex:review --model gpt-6.2-sol` einmal über den fertigen Branch, Ergebnis unverändert vorlegen; Opus-Review; bei
   Widerspruch Fable als Schiedsrichter; Push, PR; der Merge gehört dem Betreiber.
9. PR-Text (englisch) nennt: **Migration `AddWishCapture`, von Hand vor den Images**; die neuen Schalter an Api und Worker;
   den sechzehnten Hosted Service und dass sein Haushalts-Takt auch bei ausgeschaltetem Feature jede Minute läuft; die
   leere Zeichenkette als „noch kein Emote“ in drei DTO-Feldern; dass der Worker für die Erfassung maßgeblich ist und
   wie man abschaltet; die Abweichungen aus Abschnitt 9; was nur getestet, nicht live gezeigt ist.

**Migration: `AddWishCapture`, rein additiv, Hand-Schritt vor den Images** (Tunnel, `list`, `update`, `list`). Auf Prod
ist `AddWishes` aus K1 noch nicht angewendet: `list` zeigt dort **beide** als ausstehend, und beide gehen in einem `update`
in ihrer Reihenfolge. Das alte Image läuft mit dem neuen Schema weiter (neue Spalten mit Default bzw. nullbar, keine
Bestandsspalte ändert sich). **Deploy:** nicht Teil dieses Plans. `WISHES_ENABLED` und `WISHES_CAPTURE_ENABLED`
bleiben auf Prod aus; der Haushalts-Takt läuft trotzdem und braucht die Migration, sonst warnt er einmal je Stunde.
**Rollback (Sol-P1-2, Sol-P2-10):** Schalter aus genügt, in der Reihenfolge **Worker zuerst, dann Api**; nach einem
Teil-Rollback beide Dienste neu starten und die Startlogzeile des Workers bzw. `/admin/monitoring` prüfen. Ein Zurück auf
ein Image vor K6 ist mit vorhandenen K6-Zeilen verträglich (2.8a: keine Nullwerte in Pflichtspalten; Probe C6), auch für
den Identitäts-Merge bei ausgeschalteten Wünschen. Es ist ein Zwischenzustand: Das alte Image lässt offene Wünsche nicht
verfallen, leert `RequestText` nicht und nimmt bei einer Zusammenführung Belohnungen, Anlern-Zeile, Lücken und Nachweise
des Verlierers nicht mit (Kaskade). In Operations benannt.

---

## 9. Widersprüche und Lücken zwischen Spec und Code

| # | Fundstelle | Befund | Entscheidung |
|---|---|---|---|
| W1 | Spec K6 „Eingabe sind die Tags“, AK48 | TwitchLib 4.0.1 typisiert den Tag als `ChatMessage.CustomRewardId`; er steht nicht in `UndocumentedTags` | Parser nimmt einfache Werte; der Vertragstest nagelt die typisierte Eigenschaft und das Fehlen in `UndocumentedTags` fest (T10) |
| W3 | AK47 „aufgezeichnete Rohzeile“ gegen `SharedChatRuleTwitchLibTests.cs:14-17` | das Repo verwendet nur erfundene Zeilen | erfundene Zeile in der Form der Probe [P] |
| W5 | Spec K6 Beobachtung (nur `roomId`) gegen `Channel.cs:6`, `ChannelAccessService.cs:68-79` | `TwitchChannelId` ist nullbar, und eine Zeile ohne ID gehört dem, der den Login trägt | kein Rückweg über den Namen: erfasst und angelernt wird nur bei gebundener, passender ID (2.4, Sol-P1-1); 409 `wish_channel_unbound` |
| W6 | Spec K6 „Haushalts-Takt … `LastAliveAtUtc` schreiben“, E26 | ein immer geschriebener Stempel verdeckt Abschaltzeiten und einen Absturz während einer Trennung | Stempel nur bei wirksamer Erfassung und stehender Verbindung (2.7); der Rest des Takts bleibt unabhängig |
| W7 | Spec K6 Api-Tabelle „alle Routen 404 `wish_capture_disabled`“ gegen E22, AK65 | offene Wünsche wären nach dem Abschalten nicht mehr entscheidbar | `approve`, `reject`, `unlink` hängen nur an `Wishes:Enabled` (2.9) |
| W8 | Spec 5 „nur als Hinweis“, 6.1 `StatusReason` „bei `presented` und `failed`“ | Speicherort der Hinweise offen | `StatusReason` des offenen Wunsches, Rangfolge in 2.5 |
| W9 | Spec K6 `unlink`-Feldliste gegen `Wish.cs` (vier Spalten aus K1/K3) | `SlotAddedAtUtc`, `SlotConfirmedAtUtc`, `FirstActivatedAtUtc` fehlen in der Liste | die ersten zwei werden geleert, die dritte bleibt (2.6) |
| W10 | Spec 6.5, AK64 nennen nur `RequestText` | `RequestedName` und `IrcMessageId` stammen ebenfalls aus der Nachricht | werden mitgeleert (T8) |
| W11 | Spec 6.3 Fensterbeginn `ApprovedAtUtc` | Freigabe nach dem Hinzufügen in 7TV, aber vor dem Sync, ließe den Wunsch für immer in `approved` | für nie aktive Wünsche aus der Erfassung beginnt das Fenster bei `CreatedAtUtc` (2.5) |
| W12 | Spec K6 „jede automatische Zuordnung schreibt `wish.autoLink`“ gegen Abschnitt 13 (c) | „jede“ schließt Handeinträge ein und verfälschte (c) | Eintrag für jede Verknüpfung, Details tragen `source`; (c) zählt `irc` |
| W13 | Spec 6.6 gegen `IWishService.cs`, `wish.model.ts` | drei DTO-Felder würden nullbar; der K3-Code nimmt Zeichenketten an | Typen bleiben, leer heißt „noch nicht bekannt“ (2.8a) |
| W24 | Spec 6.6 „werden nullbar, mit CHECK“ gegen den Rückweg auf ältere Images (`ChannelIdentityService.cs:1072-1078` ohne Feature-Schalter) | Nullwerte in Pflichtspalten älterer Modelle | Platzhalter und CHECK statt Nullbarkeit (2.8a, Sol-P2-10) |
| W25 | Spec K6 „Konfiguration … auf das Kommando `WISHCAPTURE:` … alle 60 Sekunden“, Files Reference `BotCommands.cs:22` | jede Momentaufnahme kann die erste Einlösung nach einem Befehl verwerfen | keine Momentaufnahme für Entscheidungen, kein Kommando; der Dienst liest je Beobachtung (2.3, Sol-P2-5) |
| W26 | Spec K6 „von der letzten empfangenen Zeile bis zum abgeschlossenen Rejoin“ | der letzte Frame liegt bei einem gewollten Reconnect Minuten zurück und erzeugte Scheinlücken | Beginn am Signalzeitpunkt für jeden Grund; bei Funkstille bis zu 15 Minuten zu kurz, hingenommen (2.7, [B]) |
| W27 | Spec 6.3 „ein vorläufiges `AddedAt` … schließt also keinen berechtigten Kandidaten aus“ gegen `EmoteSlotDeltaPlanner.cs:125-127` | es kann einen unberechtigten einschließen (verspätetes Delta) | Fensterprüfung bei der Datumskorrektur (2.5, Sol-P1-3) |
| W28 | Spec 6.6 `WishRewardLearnings` und Api-Tabelle (`confirm` ohne Kennung) | die Zustimmung hängt nicht am angezeigten Kandidaten | `SessionId`, `CandidateId`, 409 `wish_learning_stale` (2.4, Sol-P2-4) |
| W30 | Spec K6 „Emote-ID aus dem Link“, AK54 „genau der ID“ gegen DECISIONS K3 (m) | Sets und Slots tragen nur die kanonische ULID | gespeichert und verglichen wird die kanonische Form (2.5a, Sol-R2-P2-3) |
| W31 | Spec E13 / Abschnitt 5 „läuft schon ein Wunsch desselben Emotes: stattdessen `merged`, der laufende wird verlängert“ | ein laufender Wunsch ohne Ablauf lässt sich nicht verlängern | 409 `wish_running_not_extendable`, der offene bleibt (2.6, Sol-R2-P2-7) |
| W32 | Spec K6 „alle Routen … nach dem Filter“ (Verwalter-Filter) gegen `ModeratorCheckService.cs:34` | Moderator nach Login reicht für K6-Daten nicht | Zugang an der ID bestätigt (2.4a, Sol-R2-P1-1) |
| W29 | Spec K6 „schon vorhandene `IrcMessageId` im Kanal: verwerfen“ | der Schlüssel am Wunsch deckt weder den Kandidaten noch die Zeit nach einer Löschung | eigener inhaltsfreier Nachweis (2.4, Sol-P2-6) |
| W18 | Spec K6 Route `…/rewards/{rewardId}` | offen, ob Zeilen-ID oder Tag-Wert | Zeilen-ID |
| W22 | Spec K6 „Bereich ‚Offen‘ oben“ gegen `WishService.cs:707-709` | `pending` sortiert ans Ende | `view=open` ordnet `pending` zuerst (2.6) |
| W23 | Spec schweigt zu gespleißten Zeilen (#114) | Tags und Text können gemischt sein | keine Beobachtung, gezählt (2.1) |
| – | Spec K6 „bestätigen legt die Belohnung an“ | ob die Einlösung des Anlernens ein Wunsch wird, steht nicht da | nein [P]; der Kandidat trägt keine IDs |
| – | Spec Abschnitt 13 gegen AK70 „im Probe-Kanal … direkt in 7TV hinzufügen“ | Einlösen geht nur im Probe-Kanal, 7TV-Schreiben ist nur mit `olaf_olaf_son` erlaubt | entschieden: `olaf_olaf_son` wird 7TV-Editor im Probe-Kanal (Abschnitt 11, F1) |

Die in der Spec genannten Zeilennummern (`TwitchChatManager.cs:1063`, `:1089-1094`, `:1103`,
`ChannelIdentityService.cs:902-1009`, `WorkerServiceRegistration.cs:56-70`) stimmen am Stand `264db942`.

---

## 10. Risiken und Rennen

| # | Risiko oder Rennen | Umgang |
|---|---|---|
| R1 | **Einlösung während Trennung, Wiederaufbau oder Rejoin.** Die Nachricht kommt nie an (kein Replay im IRC) | Lücke `disconnect` ab dem Signalzeitpunkt bis zum bestätigten Join des Kanals (2.7); Oberfläche verweist auf Twitchs Warteschlange (RR11) |
| R2 | **Dieselbe Zeile doppelt** (TwitchLibs doppelte Lese-Schleife #114, zwei Worker) | eindeutiger Index auf `(ChannelId, IrcMessageId)`; Kandidat per bedingtem Update; doppelte Lücken bei zwei Workern sind nicht abgefangen: ein Worker je Datenbank ist Betriebsannahme, in Operations benannt |
| R3 | **Handler wirft oder blockiert** und trifft Lese-Schleife oder Watchdog | Zweig ohne `await`, ohne Sperre, ohne I/O, eigenes `try/catch`; die Senke wartet nie; das Lebenszeichen steht weiter davor; der Watchdog misst an `OnSendReceiveData`, das unberührt bleibt |
| R4 | **Belohnung in Twitch umbenannt** | die ID bleibt, die Bezeichnung in Emote Purge ist nur ein Etikett des Verwalters |
| R5 | **Belohnung in Twitch gelöscht und neu angelegt, oder Texteingabe abgeschaltet** | neue ID bzw. keine Chat-Zeile: Einlösungen werden still nicht erfasst; sichtbar nur an „zuletzt erkannt“ (X7 schließt einen Alarm aus); der Settings-Text sagt das |
| R6 | **Gleichzeitiges Anlernen** zweier Verwalter, oder ein Zuschauer löst während des Anlernens eine andere Belohnung ein | PK je Kanal (zweiter Start 409); die erste Beobachtung gewinnt, der Dialog zeigt Text und Login, falscher Kandidat → Abbrechen und neu starten; zwei Bestätigungen: einmal 201, einmal 409 |
| R7 | **Abgleich gegen Verfall, Freigabe, Ablehnen, `unlink`** am selben Wunsch | überall Kanal vor Wunsch gesperrt, bedingte Übergänge über `WishTransitions`; der Verlierer sieht 409 bzw. keinen Treffer; Fenster-Ende ist nominell (2.5), also unabhängig davon, wer zuerst läuft |
| R8 | **Wunsch für ein Emote, das schon läuft oder schon im Set liegt** | Hinweise `already_running` und `already_in_set`; Freigabe wird Verlängerung (E13) bzw. 409 mit Ausweg „Bestand übernehmen“ |
| R9 | **Kanal inaktiv, deaktiviert oder nicht gejoint** | keine Beobachtung; späte Beobachtung für einen deaktivierten Kanal wird verworfen; Lücke `not_joined` nur für wieder gejointe Kanäle |
| R10 | **Schalter in Api und Worker uneinig (Sol-P1-2).** Api an, Worker aus: Anlernen startet und bekommt nie einen Kandidaten. **Api aus, Worker an: es wird weiter erfasst, und der Settings-Abschnitt ist ausgeblendet** | der Worker ist maßgeblich und sagt es (Startlogzeile, Health-Feld, Zeile in `/admin/monitoring` mit markierter Abweichung); `captureAliveAtUtc` für die erste Richtung; Operations: eine Variable für beide, Abschalten am Worker zuerst; C1 und C3 prüfen beide Richtungen mit echter Einlösung. Kein gemeinsamer Laufzeitschalter [B]: Ein Teil-Rollback bleibt ein Bedienfehler, den man sieht, nicht einer, den das System verhindert |
| R11 | **Ein Datenbank-Lesen je Einlösung mit Texteingabe**, auch für fremde Belohnungen (Preis für den Wegfall der Momentaufnahme) | ein indiziertes Lesen ohne Sperre und ohne Transaktion; ein Verbraucher, Warteschlange 1.000; der Bestand für die Lücken ist bis zu eine Minute alt, das betrifft nur die Anzeige |
| R12 | **Flut an Einlösungen** | Grenze je Zuschauer (3), je Kanal (200), Warteschlange (1.000, verwerfen und zählen), Lesen ohne Sperre vor jeder Transaktion |
| R13 | **Fremdbestimmter Text** (RR13): Markup, Adressen, Anstößiges, sehr lange Tokens | nur Text, nie gerendert; nie in Log oder Audit; 500 Zeichen; Parser mit Zeitgrenze; Löschung nach 6.5 |
| R14 | **Namens-Treffer trifft das falsche Emote** (RR12) | nur eindeutige Treffer; Zeile zeigt Bild und Einlösetext; „Zuordnung lösen“ mit dauerhafter Sperre |
| R15 | **Der Tag fällt weg** (RR10) | Schalter E22; offene Wünsche bleiben entscheidbar (W7); der Vertragstest schützt nur gegen TwitchLib, nicht gegen Twitch |
| R16 | **Uhren:** `tmi-sent-ts` (Twitch) gegen `addedAt` (7TV) gegen die eigene Uhr | `CreatedAtUtc` aus Twitchs Zeit, auf „jetzt“ begrenzt; Vergleich ohne Toleranz bleibt Spec; ein Hinzufügen in derselben Sekunde wie die Einlösung kann als `already_in_set` enden, Ausweg „Bestand übernehmen“ |
| R17 | **Altes Image schreibt während des Migrationsfensters** | jede neue Pflichtspalte hat einen Datenbank-Default, gelockerte Spalten bleiben gefüllt (T1) |
| R18 | **Haushalts-Takt ohne Migration** (Reihenfolge beim Deploy vertauscht, fremder Betreiber) | Warnung höchstens einmal je Stunde, sonst keine Wirkung; Operations nennt die Reihenfolge |
| R19 | **Viele 7TV-Reads aus dem Browser** für Vorschaubilder offener Link-Wünsche | nacheinander, nur Sichtbares, Zwischenspeicher, Stopp nach Drosselung |
| R20 | **Echte Einlöser-Daten in der Kopie der Live-Probe** | Protokoll ohne Namen und Texte; Kopie wird gelöscht; der Probe-Kanal streamt nicht, fremde Einlösungen sind nicht zu erwarten |
| R21 | **Reihenfolge beim Stoppen:** Beobachtungen nach dem Stopp des Dienstes gehen verloren | liegt in der Neustart-Lücke; benannt |
| R23 | **Leere Emote-ID wird irgendwo als echte ID behandelt** (2.8a) | eine Stelle `HasEmote`; CHECK hält sie aus allen laufenden Zuständen; je Verbraucher ein Test; Punkt der zweiten Sol-Runde |
| R24 | **Verspätetes Delta** verknüpft einen Eintrag, der vor der Einlösung im Set lag (Sol-P1-3) | Fensterprüfung bei der Datumskorrektur für K6-Autozuordnungen; bis dahin ist „Entfernen“ durch das vorläufige Datum gesperrt. **Rest:** ein im vorläufigen Zustand verlängerter Wunsch behält die Bindung (2.5); dieselbe Lücke bei K1-Handeinträgen bleibt offen (Abschnitt 14) |
| R26 | **Lückenanzeige ungenau** (Sol-R2-P2-6) | Näherung, benannt in 2.7, DECISIONS und Hilfetext; überschätzt eher |
| R27 | **Moderator nach Login** erreicht weiter alle Nicht-K6-Funktionen eines falsch zugeordneten Kanals, auch Befehle an Wünschen | Bestand, nicht K6; K6-Daten sind abgeschirmt (2.4a); Folge-Issue (Abschnitt 14) |
| R28 | **Stopp-Budget:** bei Rückstau gehen Beobachtungen jenseits von 5 Sekunden verloren | gezählt und geloggt, liegt in der Neustart-Lücke; der Nutzungs-Flush behält sein Budget |
| R25 | **Punktestand im Probe-Kanal** reicht nicht für das Drehbuch | V6 vor L0; Kürzung vorab festgehalten |
| R22 | **Coverage-Gate** an der nicht per Fake testbaren Stelle im Manager | Logik in reinen Funktionen; der Zweig selbst ist wenige Zeilen (Abschnitt 8, Punkt 6) |

---

## 11. Betreiberentscheidungen

**Entschieden am 2026-11-19 (nach Sol-Runde 1):**

| # | Frage | Entscheid | Steht in |
|---|---|---|---|
| F1 | Ort des 7TV-Teils der Live-Probe | **B-I:** Probe-Kanal ist der Affiliate-Kanal eines Freundes; 7TV-Schreiben dort nur über `olaf_olaf_son` als Editor, EmotePurgeBot ohne Editor-Rechte; Set wird wiederhergestellt. B-II nur als Fallback, falls die Rechte nicht zustande kommen | Abschnitt 7 |
| F2 | Belohnungen und AK67 | Der Betreiber legt drei Test-Belohnungen selbst an. AK67 live nur bei leerem Set oder mit Zustimmung des Freundes; sonst bleibt AK67 beim Test und steht vorab als Abweichung fest | Abschnitt 7, V4, V10 |
| F3 | „In 7TV hinzufügen“ am offenen Wunsch | **zwei Klicks** (erst „Freigeben“) | 2.10 |
| F4 | Ort der Lückenanzeige | **nur im Settings-Abschnitt** | 2.10 |
| – | Sol-Runde 1 | alle zwölf Befunde werden eingearbeitet; P1-2 schlank ohne gemeinsamen Laufzeitschalter, P2-8 abgestuft | Kopf „Revision 1“ |
| – | Sol-Runde 2 | sieben Befunde voll; **abgestuft:** die Lückenmessung bleibt eine Näherung ohne weiteres Modell, die Neuprüfung des Fensters bei der Datumskorrektur gilt nur für K6-Autozuordnungen, K1 bleibt unverändert und kommt ins Folge-Issue; AK70 ohne A7 heißt „teilweise geprüft“; keine dritte Plan-Runde | Kopf „Revision 2“, Abschnitt 14 |

**Offen: nichts.** Die Checkliste V1 bis V11 in Abschnitt 7 sind Handgriffe des Betreibers, keine Entscheidungen; V6
(Punktestand in einem Kanal, der nicht streamt) kann den Umfang des Drehbuchs kürzen und wird dann vorab festgehalten.

Alles andere ist in diesem Plan entschieden und mit **[E]** markiert; der Betreiber kann jedem Punkt widersprechen. Die
gewichtigsten: 2.8a (Platzhalter statt nullbarer Spalten, Abweichung von Spec 6.6), [W25] (kein Vorfilter, kein Kommando
`WISHCAPTURE:`), [W6] Lebensstempel, [W26] Lückenbeginn am Signalzeitpunkt, [W7] Erreichbarkeit von
`approve`/`reject`/`unlink`, [W11] Fenster freigegebener Erfassungs-Wünsche, [W12] Umfang von `wish.autoLink`, [W10]
zusätzliche Löschfelder.

---

## 12. Selbstprüfung des Plans

- Jede AK46 bis AK70 hat mindestens einen Task (Abschnitt 4); AK46 und AK70 haben einen Live-Schritt.
- Kein Task enthält Code; Signaturen und Feldnamen nur als Form.
- Regel 3: ein DECISIONS-Eintrag, je Task ergänzt. Regel 4/6: Handler dünn, zwei neue Interfaces in `Core/Services`.
  Regel 5: `WishCaptureWorker` konkret, Senke und Rekorder mit Interface. Regel 7: Codes samt Übersetzung im selben Commit
  (T7). Regel 10: Aggregate in T4/T5 erst auf ID-Listen. Regel 11: Tests je Schicht benannt, Worker-Tests container-frei,
  Filter-Matrix in `Api.Tests`. Regel 12: Abschnitt 5. Regel 16: Abschnitt 7 mit früher Stufe L0.
- Schichtentreue: `WishTextParser`, `RewardObservation`, Interfaces in Core ohne Paket; Parser, Warteschlange, Tracker im
  Worker ohne `AppDbContext`; der Worker schreibt nur über `IWishCaptureService`.
- Der Reconnect-Vertrag (#68/#114) ist nicht berührt: kein neuer Client, keine neue Schleife im Manager, zwei reine
  Speicher-Aufrufe im Watchdog außerhalb der Wiederaufbau-Entscheidung.
- **Es gibt keine weitere Plan-Runde.** Sol prüft am Ende den Branch (`/codex:review`, Abschnitt 8). Worauf diese
  Code-Prüfung und der Opus-Review besonders sehen sollen, weil der Plan es nur als Vertrag festlegt: ob jeder Verbraucher
  der Emote-ID die `HasEmote`-Frage stellt (2.8a); ob jeder Übergang aus einem Platzhalterzustand die drei Felder im selben
  Update füllt (2.8a, Tabelle); ob jede K6-Antwort die Bestätigung an der ID prüft und die mitgeführte Kanal-ID benutzt
  (2.4a); ob jede Änderung der Anlern-Zeile bedingt ist (2.4); die Sperrfolge der Freigabe als Verlängerung (2.6); die
  Geltung der Datumskorrektur (nur K6-Autozuordnungen, K1 unverändert, 2.5).
- Hingenommen und benannt, nicht offen: die Lückenanzeige als Näherung (2.7, R26), der Lückenbeginn am Signalzeitpunkt
  ([W26]), das Restrisiko eines im vorläufigen Zustand verlängerten Wunsches (R24), der Moderator nach Login für
  Nicht-K6-Daten (R27).

---

## 13. Sol-Runden: Einarbeitung

Die Zuordnung je Befund steht oben in den Tabellen „Revision 1“ (zwölf Befunde) und „Revision 2“ (neun Befunde): jeweils
Befund, Ergebnis der eigenen Prüfung am Code mit Fundstelle, und der Ort der Einarbeitung. Kein Befund wurde widerlegt;
abgewandelt wurden Sol-P2-5 (kein Vorfilter statt einer Konfigurationsrevision), Sol-P2-7 (durch die Kanalbindung
konstruktiv ausgeschlossen, Regel bleibt), Sol-R2-P2-4 und Sol-R2-P2-6 (abgestuft nach Betreiberentscheid).

---

## 14. Folge-Issue (legt der Orchestrator nach dem Merge an)

Drei Punkte, die Sol-Runde 2 sichtbar gemacht hat und die bewusst **nicht** in K6 liegen, weil sie Bestand außerhalb der
Erfassung ändern. Ein Issue, englisch, als Zeile im Epic #481; Text sinngemäß:

1. **Wish reconcile: a late EventAPI delta can link a hand-entered wish to an entry that was in the set before the
   approval (K1).** K6 re-checks the window when the provisional `AddedAt` is replaced by 7TV's date, but only for wishes
   the capture linked without a click. For `approved` wishes (hand entry, re-add, approved capture) the same sequence still
   ends in an `active`, date-confirmed wish whose entry predates the approval, and K3 then offers "remove". Needs a model
   of the provisional link and its original window, and rules for decisions booked in between (`extend`, `keep`, K2's
   `presented/unconfirmed_start`), tested in both commit orders. Sol round 2, finding 4; plan #486, 2.5.
2. **Moderator check compares the channel login only.** `ModeratorCheckService.IsModeratorAsync` matches
   `TwitchModeratedChannelInfo.Login` although the list carries `BroadcasterId`. While a channel row still holds a login
   Twitch has reassigned, a moderator of the new owner manages the old row (usage, vote sessions, wishes). K6 shields its
   own data with an id-based check (`ResolveWishCaptureAccessAsync`); the general fix is to decide on the row's
   `TwitchChannelId` wherever one exists, as `IsBroadcaster` already does. Sol round 2, finding 1; plan #486, 2.4a, R27.
3. **Canonical 7TV emote id on the K1 write routes.** `POST …/wishes` and `adopt` store the id as sent; the web client
   sends the canonical ULID since K3, another caller may send the hex ObjectID or a lower-case ULID and file a wish that
   never meets its slot. Apply `SevenTvEmoteIdCanonical` there too. Plan #486, 2.5a.
