# Plan #487: Wunsch-Emotes, Kind K4: Bot-Backend

Erstellt am 2026-11-20 im Worktree `EmotePurge-wt-487` (Branch `feat/487-wish-bot`, Basis `origin/main` @ `801e64a6`; K1
(#492), K2 (#493), K3 (#494) und K6 (#495) gemergt, seit 2026-11-20 auf Prod live und eingeschaltet, Bot-Modus existiert noch
nicht). Alle Codestellen dieses Plans sind an diesem Stand gelesen.
**Status: Revision 2 (2026-11-20), Endstand des Plans; Sol adversarial zweimal (Runde 1: 4 × P1, 5 × P2, 1 × P3; Runde 2:
4 × P1, 5 × P2). Betreiber: acht voll, einer abgestuft. Keine weitere Plan-Runde. Die Code-Prüfung durch Sol folgt je PR.**
Betreiberfragen F1 bis F6 entschieden (2026-11-20). Nichts committet.
Quellen: [Spec](../superpowers/specs/2026-11-17-wunsch-emotes-spec.md) Abschnitte 5 bis 8, 6.4, 12 bis 14, 16, E7, E8, E11,
E17, E24, E25, RR2, RR3, Tabellen W/X/S und „K4: Bot-Backend“ (AK71 bis AK96); [Plan-483](Plan-483-Wunsch-Emotes-Backend-Kern.md)
(1.2, Befund 8, R10), [Plan-484](Plan-484-Wunsch-Emotes-Ablauf-Lauf.md) (1.2, Befunde 1, 11, 13, 15, 20, 21),
[Plan-485](Plan-485-Wunsch-Emotes-Verwalter-UI.md) (1.2), [Plan-486](Plan-486-Wunsch-Emotes-Chat-Erfassung.md) (1.2);
`docs/nachtlaeufe/nacht-2026-11-17-setze-den-plan-docs-plans-plan-483.md` (T1-, T5-, T7-Review „für K4 geparkt“),
`docs/nachtlaeufe/live-484-2026-11-18.md` (Auffälligkeit 4); DECISIONS „Wish emotes, child 1/2/3/6“; Issue #487 (nur Übersetzung
der Spec); `CLAUDE.md` (Regeln 3 bis 7, 11, 12, 16, 17, 19, Schichtentreue, Tests, Sprache), `web/.claude/CLAUDE.md`.

**Was dieser Plan trägt:** Befund am Code, die vier aus K2 vorgemerkten Punkte mit Entscheidung, Verträge, Tasks mit
Grenzfällen und Tests, AK-Zuordnung, frühe Live-Probe, Gates, Widersprüche, Risiken, Betreiberfragen, PR-Schnitt. Kein Code;
Namen und Signaturen nur als Form. Jeder Implementer liest seinen Task, die genannten Spec-Abschnitte **und** die genannten
Bestandsdateien, bevor er schreibt.

**Lesehinweis:** **[E]** Planer-Entscheidung (der Betreiber kann widersprechen) · **[P]** Präzisierung ohne
Entscheidungsgehalt · **[B]** schon entschieden (Spec oder Betreiber) · **[W*n*]** Widerspruch oder Lücke aus Abschnitt 7 ·
**[F*n*]** Betreiberfrage aus Abschnitt 9 · **Sol-P1-*n*/P2-*n*/P3-*n*** Befund der Sol-Runde 1, **R2-*n*** Befund der
Sol-Runde 2 (Tabellen unten) · „AK77“
meint das Akzeptanzkriterium der Spec, „Spec 8“ deren Abschnitt.

### Revision 1 (2026-11-20): Sol-Runde 1 und Betreiberentscheidungen

| Befund | Prüfung am Code | Eingearbeitet in |
|---|---|---|
| Sol-P1-1 gespeicherte Hinzufügeabsicht sperrt Verwalter-Entscheidungen nicht | **hält:** `WishTransitions.cs:145-178` erlaubt aus `approved` `edit`, `close`, `adopt`, `link`; `RunTransitionAsync` (`WishService.cs:1470-1511`) sperrt Kanal, dann Wunsch und zählt hoch, kennt aber `BotWriteAtUtc` nicht | 2.5a (b) offener Versuch → 409, (c) Nachprüfung unter Sperre vor dem Senden; 2.6; T6 (Routen), T9 (Rennen je Route) |
| Sol-P1-2 Set-Wechsel überträgt fremdes Bot-Eigentum | **hält:** `Wish` hat keinen Set-Bezug (`Wish.cs:67-92`), `SevenTvSyncService.cs:879-906` setzt `ActiveEmoteSetId` unabhängig | 2.5a (d) Set-Bindung `BotSetId` und Generationsfenster; 2.6 Punkt 6, 2.7; T1 (Spalte), T9, T10 |
| Sol-P1-3 alte Token-Prüfung gibt neues Token frei | **hält:** 2.2/2.8 schrieben Prüfergebnisse unbedingt auf `Id = 1` zurück | 2.2 `CredentialVersion`, 2.8, 2.5a (c); T1, T4, T5 (Rotation und Löschen während blockierter Prüfung) |
| Sol-P1-4 Lock-Verlust mitten im Durchgang | **hält:** 2.5 nannte den Lock nur beim Holen; das Vorbild prüft vor jeder Anfrage und vor jeder Buchung (`ChatLogBackfillWorker.cs:377,422`, `ChatLogBackfillService.cs:691-728`) | 2.5 und 2.5a (g) Lock als Schranke; T5, T9, T10 (getrennter Lock-Socket, blockierter Read, zweiter Runner) |
| Sol-P2-5 Beleg für die Reparatur geht verloren | **hält:** die Einreihung trägt nur Zeit und Version (`Wish.cs:73-74`), 2.7 rollte bei gescheitertem Nach-Read zurück | 2.5a (a), (e), (f): Versuch mit `BotRemoveEvidence` vor dem Senden committet, Wiederaufnahme, idempotente Buchung; 2.7; T1, T10 |
| Sol-P2-6 D7 zwischen Filter und Deaktivieren | **hält:** `WishSettingsAuthorizationFilter.cs:36-42` prüft vor dem Handler, `UpdateSettingsAsync` (`WishService.cs:65-108`) unter der Kanalsperre nicht erneut | 2.9 D7 unter Kanalsperre neu geprüft (auch `PUT /settings`), Abbruch einer offenen Anfrage, Bestätigung nur der geprüften Anfrage; T6, T7, T8 |
| Sol-P2-7 Lesestörungen halten Wünsche aus der Arbeitsliste | **hält:** 2.5 (`Unavailable` endet ohne Zustandswechsel) und 2.6 Punkt 6 („Versuch zählt nicht“) ohne Frist; `StatusesOf` (`WishService.cs:1685-1697`) | 2.3 Kanalzustand `fallback` bei unbekanntem Editor-Status ([F4]), 2.6 Punkt 5 absolute E11-Frist [P]; T6, T8, T9 |
| Sol-P2-8 Rückweg auf alte Images | **hält:** das alte Image leitet `mode = bot` aus `BotModeEnabled` ab (`WishService.cs:1823`), der alte Ablauf-Lauf überspringt Eingereihte (`WishExpiryService.cs:77`) | Abschnitt 6 „Rollback“ in Schalter-Rollback und Image-Downgrade getrennt, Prüfabfrage; T13, T14 (Rückweg-Probe) |
| Sol-P2-9 Klartext-Token vor dem Log-Test | **hält teilweise:** kein Leck belegt; die Lücke im PR-1-Gate besteht (Bindefehler `Program.cs:307-322`, Erfassung `LogCaptureProvider.cs`) | neuer Task T11a in PR 1; T11 (jetzt T11b) erweitert in PR 2 |
| Sol-P3 `PATCH`-Fall für AK83 nicht erreichbar | **hält:** `edit` nur aus `pending`, `approved`, `failed` (`WishTransitions.cs:156-160`), eine Einreihung nur in `active`/`presented` | [W19] (Spec-Text AK83), T10 testet 409 an der echten Route, T9 das erlaubte `PATCH` im Hinzufüge-Rennen |

**Widerlegt wurde kein Befund.** Betreiberentscheidungen 2026-11-20 als [B]: F1 (zwei PRs), F2 (Admin-IDs am Worker), F3
(Variante (a), falls AK86 scheitert). Am Task-Schnitt neu: **T11a** in PR 1 (Token-Vertrauensgrenze), T11 heißt **T11b**.
Geändert: T1 (fünf Spalten), T4, T5, T6, T7, T8, T9, T10, T13, T14. Die PR-Grenze bleibt; die Migration bleibt allein in PR 1.

### Revision 2 (2026-11-20): Sol-Runde 2, Betreiberentscheidung [B] F6

| Befund | Prüfung am Code | Eingearbeitet in |
|---|---|---|
| R2-1 (P1) 409-Guard nur in `RunTransitionAsync` | **hält:** `AdoptAsync` (`WishService.cs:294-335`), `CompleteRemovalAsync` (`:617-681`), `UnlinkAsync` (`:896-920`) sperren auf eigenen Pfaden; `approve` verlängert den Zielwunsch über `MergeIntoRunningAsync` (`:810-818`, `:1213-1237`); dazu `SupersedeActiveDecisionsAsync` (`:1278-1301`) erhöht `DecisionVersion` und leert Einreihungen | 2.5a (b) Guard an allen Pfaden, Supersede überspringt; (f) Bindung an `DecisionVersion` und Zustand; T6, T7 |
| R2-2 (P1) Absturzrest übernimmt fremdes Eigentum | **hält:** geteiltes Set (`EmoteService.cs:411-423`), laufender Wunsch nur je Kanal eindeutig | 2.5a (d) Eigentum nur mit `BotAddAckAtUtc`, (a) setweite Serialisierung; 2.6; T1, T9 |
| R2-3 (P1) Lock-Probe verhindert spätes Senden/Committen nicht | **hält:** `HoldsLoopLockAsync` (`ChatLogBackfillService.cs:691-715`) prüft nur den Augenblick; der Vollsync setzt `ActiveEmoteSetId` und committet unter eigener Kanalsperre (`SevenTvSyncService.cs:892-942`) | **abgestuft [B] F6:** 2.5a (c) Kanalzeile `FOR SHARE` über Senden und Buchen, (g) Runner-Epoche `RunnerEpoch`; Restfenster R11; T1, T5, T9, T10 |
| R2-4 (P1) Reparatur doppelt oder Beleg verloren | **hält:** E11/AK88 rechnen mit verzögerter Sichtbarkeit; (e) leerte den Beleg bei Frist und Notaus; `WishReconcileService.cs:179-208` prüft nur den eigenen Slot | 2.5a (h) Prüfphase `BotRepairAtUtc`, Senden nur im selben Durchgang, sonst nur lesend; 2.7, 2.8; T1, T10 |
| R2-5 (P2) Editor-Nachweis überlebt Token-Rotation | **hält:** `BotEditorCheckedAtUtc` ohne Konto- oder Versionsbezug (`ChannelWishSettings.cs:19-30`) | 2.3, 2.9 `BotEditorCheckedCredentialVersion`, Buchung bedingt auf Version und Bot-ID; T1, T6, T8 |
| R2-6 (P2) D7 an den Belohnungsrouten | **hält:** `PATCH`/`DELETE /rewards/{rewardId}` und die Anlern-Routen nur mit Vorfilter (`WishEndpoints.cs:617-655`), `LockAndCheckAsync` (`WishRewardService.cs:381-403`) prüft nur die Bindung | 2.9 D7 in `LockAndCheckAsync`; T6 mit Regressions- und Rennfall |
| R2-7 (P2) E11-Frist hängt am Lesepfad | **hält:** `Unavailable` beendet den Durchgang vor der Warteschlange; der Ablauf-Lauf-Takt ist bis zu einem Tag (`WishOptions.cs:22,39-42`) | 2.5 Schritt 0 Fristschritt (lokal, 30 s, ohne Lock, Token, Pause, Budget), `BotConfirmDeadlineAtUtc`; T1, T8, T9, T10 |
| R2-8 (P2) `recheck` ohne Set-Bezug | **hält:** `WishTransitions.cs:201`, `RecheckAsync` Platzhalter (`WishService.cs:699-720`) | 2.6 Punkt 5, 2.9 `recheck`: `BotSetId` und `BotWriteAtUtc` bleiben, nur die Frist neu, Set-Wechsel → 409 `wish_bot_set_changed`; T7, T9 |
| R2-9 (P2) Downgrade-Abfrage übersieht `kept` ohne Slot | **hält:** `WishReconcileService.cs:124-131` (`kept` nur mit `SlotId`), Nachtrag nur für `active` (`:472-482`) | Abschnitt 6 Prüfabfrage; T13, T14 mit verzögerter Slot-Sichtbarkeit |

**Widerlegt wurde kein Befund.** Neu an `AddWishBot` (T1, weiter rein additiv): an `Wishes` drei weitere nullbare Spalten
(`BotAddAckAtUtc`, `BotConfirmDeadlineAtUtc`, `BotRepairAtUtc`, zusammen sieben), an `ChannelWishSettings` eine nullbare
(`BotEditorCheckedCredentialVersion`), in der neuen Tabelle `RunnerEpoch`. Task-Schnitt und PR-Grenze bleiben; T6 berührt
zusätzlich live genutzten K6-Code (`WishRewardService`). Neuer Code `wish_bot_set_changed` (zehn statt neun).

---

## 0. Befund vor dem Plan

Alle Stellen am Stand `801e64a6`.

1. **Die Bot-Spalten stehen, nichts setzt sie.** `Wish` (`src/EmotePurge.Core/Entities/Wish.cs:67-77`) hat `BotOwned`,
   `BotOwnedAddedAtUtc`, `BotWriteAtUtc`, `BotAttemptCount`, `BotNextAttemptAtUtc`, `RemovalQueuedAtUtc`,
   `RemovalQueuedVersion`, `DecisionVersion`; dazu über die Spec hinaus `LinkOrigin`, `SlotAddedAtUtc`, `SlotConfirmedAtUtc`,
   `FirstActivatedAtUtc` (`:82-92`). `ChannelWishSettings` (`ChannelWishSettings.cs:19-33`) hat alle Bot-Felder aus Spec 6.2.
   `WishLinkOrigin.Bot` existiert (`WishStatus.cs`), wird nirgends gesetzt. **K4 braucht nur eine Migration für 6.4.**
2. **Die Übergänge des Bots stehen in der Tabelle.** `WishTransitions.cs:175-182`: `bot_write` (`approved` → `unconfirmed`),
   `bot_reject` (→ `failed`), `bot_already_in_set` (→ `presented`), `bot_confirm` (`unconfirmed` → **nur** `active`),
   `bot_unconfirmed` (→ `presented`); `recheck` aus `presented` nur mit Grund `unconfirmed` (`:93`, `:201`); `close` aus
   `presented` nur ohne Slot (`:200`). **Kein `kept`-Zweig für `bot_confirm`** (K2-Vormerkung 1, 2.1).
3. **`recheck` ist ein Platzhalter.** `WishService.RecheckAsync` (`WishService.cs:699-721`) antwortet für jeden Zustand 409
   und schreibt nichts (Plan-483 Befund 8). Für `recheck` gibt es keine Audit-Konstante (`AuditLogEntry.cs:54-98`).
4. **`re-add` und `retry` setzen die Bot-Felder nicht zurück.** `ReAddAsync` (`:533-586`) leert Start, Ablauf, Generation,
   `BotOwned`, `BotOwnedAddedAtUtc`, über `CountDecision` (`:1595-1600`) die Einreihung; es lässt `SlotId`, `LinkOrigin`,
   `BotWriteAtUtc`, `BotAttemptCount`, `BotNextAttemptAtUtc` stehen (`:571`: „SlotId … history“). `RetryAsync` (`:517-531`)
   sagt ausdrücklich „The bot's attempt fields are K4's to reset“ (`:527`). Folgen in [W4], [W5].
5. **Der Abgleich nimmt Bot-Wünsche aus und hat die K4-Ausnahme schon.** `WishReconcileService.cs:224-229`: Schritt 4 nimmt
   `approved` nur mit `BotWriteAtUtc IS NULL`; `unconfirmed` gehört nie dazu. `:472-482`: ein `active` ohne `SlotId` bekommt einen
   Slot über `Match(…, Approved, ApprovedAtUtc ?? CreatedAtUtc)` und `HoldGeneration` (`:715-720`), das `SlotAddedAtUtc`,
   `SlotConfirmedAtUtc` **und `StartsAtAuthoritative = slot.AddedAtFromRest`** setzt. `Match` entscheidet über die Emote-ID; der
   Alias bricht nur einen Gleichstand ([W7]). Schritt 3 (`:203`) prüft jeden `active` **und** `presented` mit gesetzter
   `SlotId` gegen seinen Slot (`IsLive`, `:319`); ein archivierter Slot führt nach `removed_externally` ([W5]).
   `EndOwnership` (`:723-735`) erhöht `DecisionVersion` und leert die Einreihung (E25 im Abgleich steht).
6. **Ablauf-Lauf und Verfügbarkeit (K2).** `IWishBotAvailability` beantwortet nur die globale Frage
   (`Core/Services/IWishBotAvailability.cs`); registriert ist `WishBotNeverAvailable` als Singleton
   (`ServiceCollectionExtensions.cs:364`). `WishExpiryService` wählt nur `active` mit `RemovalQueuedAtUtc IS NULL` (`:77`);
   ein eingereihter Wunsch wird vom Lauf nie wieder angefasst ([W8]). `WishExpiryPolicy.Decide` nimmt `BotModeEnabled` roh und
   `BotSuspendedReason` getrennt; Zeile 3 (`unconfirmed_start`) steht vor Zeile 4 (`bot_unavailable`).
7. **„Bot-Modus“ heißt heute `BotModeEnabled && BotSuspendedReason is null`** (`WishService.cs:1823`). Daran hängen: `mode` im
   DTO (`:1784`), welche Zustände Handlungsbedarf sind (`StatusesOf`, `:1685-1697`; `approved` zählt nur im manuellen Modus),
   der Zähler und `mine` (`WishOverviewService.cs:135`) und das Recht auf die Kanal-Vorgaben (`CanChangeSettings`,
   `:1732-1741`). Die globale Verfügbarkeit geht nirgends ein ([W1]); ein ausgesetzter Kanal gilt für die Vorgaben als
   manuell ([W2]).
8. **`botWritePending`** ist `BotWriteAtUtc != null && Status != active` (`:1781`), also auch für `kept`, `failed`, `presented`
   und jeden Endzustand mit altem Stempel ([W9]).
9. **Getrennte Registrierung als Muster.** `AddLogCapture` (`Infrastructure/Logging/LogCaptureRegistration.cs:16-60`) und
   `AddSystemLoadSampler` werden nur von Api (`Program.cs:38-40`) und Worker (`WorkerServiceRegistration.cs:64`, `:75`)
   aufgerufen, nie aus `AddEmotePurgeInfrastructure`. `CLAUDE.md:148` nennt „genau zwei“ solche Einstiege.
10. **Hosted Services.** Sechzehn (`WorkerServiceRegistration.cs:62-89`); `WishExpiryWorker` vorletzter, `WishCaptureWorker`
    **bewusst letzter** (stoppt zuerst, `:82-87`). Ein neuer Dienst gehört **vor** `WishCaptureWorker`.
11. **Advisory-Lock-Muster.** `ChatLogBackfillService.cs:58-59` (Schlüssel als `hashtext`-Text), `:672`
    (`pg_try_advisory_lock` auf eigener `NpgsqlConnection`, `:76`), Prüfung über `pg_locks` (`:701-708`), Freigabe durch
    Schließen der Verbindung (`:730`). Anstoß über `BotCommands.BackfillPrefix` (`Core/Messaging/BotCommands.cs`),
    `Worker.cs:102-108` setzt nur `ChatLogBackfillSignal` (latching, Kapazität eins).
12. **7TV-Lesepfad.** `SevenTvApiClient` (`Infrastructure/SevenTv/SevenTvApiClient.cs`), ein typisierter Client mit Basis
    `https://7tv.io/v3/`, 10 s Timeout, `ProviderRequestTelemetryHandler` mit `RateLimitCallSources.SevenTvRest`
    (`ServiceCollectionExtensions.cs:134-140`). `GqlSetEntriesQuery` (`:42-43`) liest v4 (`/v4/gql`, `:46`) `alias`, `addedAt`,
    Emote-ID, Standardname, 500 je Seite, höchstens 10 Seiten (`:50-51`), **kein `addedById`** (im ganzen Repo nicht vorhanden).
    Das maßgebliche `AddedAt` eines Slots (`AddedAtFromRest`) kommt aus genau diesem v4-Feld; das EventAPI-Delta setzt ein
    vorläufiges Datum (Beobachtungszeitpunkt). `GqlEditorOfQuery` (`:61-62`) ist v3 `user.editor_of` und liefert nur Kanäle,
    keine Berechtigungsstufe. Verkleidetes 429 (HTTP 200, `extensions.status`) wird an mehreren Stellen erkannt (`:189-197`).
13. **Gemessene Antwortformen der 7TV-Schreibfläche** (Web, `core/seven-tv/seven-tv-import.service.ts:94-120`): Name im Set
    vergeben = GraphQL-Fehler mit `extensions.status = 409` (für `addEmote` und `updateEmoteAlias`); fehlende Berechtigung =
    HTTP 200 mit `extensions.code = 'LACKING_PRIVILEGES'`; ungültiges oder abgelaufenes Token = echtes HTTP 401 mit Körper
    außerhalb des Schemas. Die Mutationen `addEmote(id: { emoteId, alias })` und `removeEmote(id: { emoteId, alias })` stehen
    in `seven-tv-add-emote.ts` und `seven-tv-delete.service.ts:174-186`. **Volles Set, entferntes Emote: Form unbekannt.**
14. **Editor-Auskunft.** `ISevenTvEditorService.GetEditorGrantsAsync(twitchUserId)` (Core) geht über `ModRoleCache`
    (`SevenTvEditorService.cs:10`), unterscheidet `Ok`, `NoSevenTvAccount`, `Unavailable`; die Grants tragen `TwitchChannelIds`.
    Für den Aufrufer (E24) passend; für den Bot (frisch, ohne Cache) nicht: dort direkt `ISevenTvApiClient.GetEditorOfChannelsAsync`
    mit der 7TV-ID des Bots.
15. **Admin.** `/api/admin` mit `GlobalAdminAuthorizationFilter`, ohne Rate-Limit (`AdminEndpoints.cs:31-37`); Unterdateien
    hängen sich als Gruppen-Erweiterung an (`group.MapAdminLogEndpoints()`, `:39`). Web: `features/admin/` (Seiten,
    `admin-layout.ts`), `core/admin/admin.service.ts`, Routen `app.routes.ts:67-110`, `typed-confirm-dialog` existiert
    (genutzt in `admin-users-page.ts`). Aufrufquellen der Rate-Limit-Seite sind übersetzt (`en.json:469` `seventv-rest`).
16. **Globale Admins am Worker fehlen.** `IGlobalAdminAllowlist` ist geteilt registriert (`ServiceCollectionExtensions.cs:91`),
    `Auth__AdminTwitchUserIds` ist aber nur am Dienst `api` gemappt (`docker-compose.prod.yml:85`), nicht am `worker`
    (`:143-210`). E8 („Aktivierender ist zum Zeitpunkt des Schreibens globaler Admin“) ist im Worker so nicht prüfbar ([W3]).
17. **Kontolöschung** pseudonymisiert `BotModeActivatedBy*` und `BotActivationRequestedBy*` schon
    (`AccountDeletionService.cs:171-182`). Nichts zu tun.
18. **Nutzungsfenster** (K2-Vormerkung 4): `WishUsageWindow.WindowStart` = später von Start und „jetzt minus 7 Tage“,
    `LengthInDays` mit Bruchteilen; `WishUsageQueries.ReadAsync` summiert alle UTC-Tage vom Tag des Fensterbeginns bis zum
    Bewertungstag einschließlich (höchstens acht Tageszeilen) und teilt durch die Länge (`WishUsageQueries.cs`,
    `WishUsageWindow.cs`). Bei 30 Tagen Laufzeit zählt der angebrochene erste Tag ganz: acht Tage geteilt durch 7,0.
19. **Hotpath.** `/permissions` ist laut Kommentar die meistgerufene Route (`ChannelEndpoints.cs:518`), heute ohne
    Datenbankzugriff für die Wunsch-Felder (`:98-131`).
20. **Prod läuft im manuellen Modus mit echten Wünschen** (alle Kanäle, `WISHES_ENABLED` und `WISHES_CAPTURE_ENABLED` an).
    Jede K4-Änderung an geteiltem Code (`WishService`, `WishReconcileService`, `WishExpiryService`, `WishOverviewService`)
    muss bei ausgeschaltetem Bot-Modus verhaltensgleich sein; das ist in jedem betroffenen Task ein eigener Regressionsfall.

---

## 1. Umfang

### 1.1 Drin

Migration `AddWishBot` (6.4), Versiegeln und Öffnen, Admin-Api und Admin-Seite, Schreib-Client, echte Verfügbarkeit,
`WishBotWorker` mit Token-Haushalt, Aktivierung, Aussetzen, Rückfall, Hinzufügen, Bestätigen, Entfernen, Reparatur,
Erkennungsgrenze samt gemeinsamem Vertrag für Schreibversuche (2.5a); Kanal-Api (`GET …/wishes/bot`, `activate`, `DELETE`, `bot-remove`, `recheck`), `wishBotModeAvailable`;
die K1/K2-Nähte aus 2.1 und [W1] bis [W10]; Doku nach AK95 samt korrigierter `CLAUDE.md`-Zeile; frühe Live-Probe (T0) und AK93.

### 1.2 Draußen

Aktivierungsdialog, Editor-Mutation im Browser, Bot-Zustände auf Karten und im Settings-Abschnitt (K5); automatische
Erneuerung des JWT, Chat-Antworten (Spec 15); Bot-Entfernen auf der Nutzungsseite (F11); Statuszeile `A18` (K5).

### 1.3 Was K4 an K5 liefert

`GET …/wishes/bot` mit der Form der Spec, `activate` (202, asynchron bestätigt), `DELETE …/wishes/bot`, `bot-remove`,
`recheck`, `wishBotModeAvailable`; DTO-Felder `botOwned`, `botWritePending` (nach [W9] präzisiert), `botNextAttemptAtUtc`,
`removalQueuedAtUtc`, `mode` (nach [W1] effektiv); alle Fehlercodes samt `api-error.ts` und Locales (Regel 7); die
Audit-Übersetzungen `wish.bot*` und `wishBot.*` ([W15]); der Hinweis, dass Wunsch-Befehle während eines offenen Schreibversuchs
409 `wish_state_conflict` antworten (2.5a (b)), und `recheck` bei Set-Wechsel 409 `wish_bot_set_changed`. Offen für K5: `wishes.bot.*`, `wishes.reason.*` der Bot-Gründe.

### 1.4 Verhältnis zum laufenden Betrieb

Der manuelle Modus läuft auf Prod für alle Kanäle; K4 wird parallel gebaut, der Bot-Modus bleibt bis Freigabe 2 (Spec 12) aus.
**Kein K4-Vertrag hängt von Beobachtungen aus dem laufenden Betrieb ab.** Abhängig ist K4 nur von der Probe T0 (AK86 bis
AK92). Einzige weiche Berührung: Die Fensterrechnung (2.1, Punkt 4) ließe sich mit echten Nutzungszahlen nachprüfen; sie ist
aber nur bei gesetzter Schwelle im Bot-Modus wirksam und braucht keine Messung vor dem Merge. Folge des Live-Betriebs für den
Bau: Befund 20 (Regressionsfälle bei ausgeschaltetem Bot) und eine additive Migration, die das laufende Image nicht stört.

---

## 2. Verträge

### 2.1 Die vier Vormerkungen aus K2

1. **`bot_confirm` für dauerhafte Wünsche [E].** `bot_confirm` bekommt dieselbe Doppelzeile wie `link`
   (`AddActiveOrKept`): `Permanent` → `kept`, sonst `active`. Bei `kept` entsteht **kein** Eigentum (Spec 8: „Behalten“ beendet
   es), kein Ablauf, `LinkOrigin = bot`, Start nach Punkt 3. Die Slot-Nachtragung des Abgleichs (Befund 5, `:472`) gilt dann auch
   für `kept` ohne `SlotId` mit `LinkOrigin = bot`, sonst hielte der behaltene Eintrag seinen Slot nicht und ein späterer
   Wunsch desselben Emotes könnte ihn übernehmen (Index 6.1 schließt `kept` ein). Test: `re-add permanent` im Bot-Kanal → Bot
   fügt hinzu → `kept`, `BotOwned` falsch, nächster Abgleich trägt `SlotId` nach, Ablauf-Lauf und Bot fassen ihn nie an.
2. **Zurücksetzen beim erneuten Hinzufügen [E].** `re-add` setzt zusätzlich zum Bestand: `BotWriteAtUtc = null`,
   `BotAttemptCount = 0`, `BotNextAttemptAtUtc = null`, `LinkOrigin = null` und die Versuchsfelder aus 2.5a außer einer offenen
   Prüfphase (`BotRepairAtUtc` mit Beleg, 2.5a (h)). `retry` setzt die
   Bot-Felder ebenso zurück
   (`BotWriteAtUtc` ist nach `bot_reject` schon leer, die Regel gilt trotzdem). `SlotId` bleibt bei `re-add` als Historie
   (K1-Entscheid), wird aber beim **Festschreiben der Bot-Absicht** geleert (2.6 Schritt 1), weil ein `approved` per Definition
   unverknüpft ist (`WishReconcileService.cs:222-224`) und eine alte `SlotId` sonst im Folgezustand (`unconfirmed`,
   `presented`) als Verknüpfung gälte ([W5]). `FirstActivatedAtUtc` bleibt (E14 (c)). Begründung: Ohne Rücksetzen findet der
   Bot den Wunsch mit gesetztem `BotWriteAtUtc` und behandelt ihn als Absturzrest (Spec K4: „wie `unconfirmed`, nicht erneut
   hinzufügen“), und der Abgleich nimmt ihn aus ([W4]).
3. **Maßgeblicher Start ist Pflicht [E, P].** Eigentum und Bot-Entfernung setzen `StartsAtAuthoritative = true` voraus (K2,
   Spec 8). Der Bestätigungs-Read ist v4 `addedAt`, also dieselbe Quelle wie `AddedAtFromRest` (Befund 12): Er setzt
   `StartsAtUtc = addedAt`, `ExpiresAtUtc = addedAt + DurationDays`, `StartsAtAuthoritative = true`, `BotOwnedAddedAtUtc = addedAt`
   (nur bei Eigentum). **Ein Eintrag ohne `addedAt` gilt als noch nicht bestätigt** (weiter nach E11, am Ende `presented`
   (`unconfirmed`)); ein vorläufiger Start entsteht im Bot-Weg nie. Die nachträgliche Slot-Verknüpfung (`HoldGeneration`) kann
   `StartsAtAuthoritative` auf falsch setzen, wenn der Slot erst per EventAPI-Delta (vorläufig) bekannt ist; der nächste Vollsync
   (60 s) heilt das über den vorhandenen Bestätigungszweig, ohne Start oder Eigentum zu ändern (`addedAt ≤ generation`). Das ist
   gewollt: Es gibt keinen zweiten Wahrheitsbegriff für „maßgeblich“. Bis dahin hält K2 einen fälligen Wunsch zurück; bei
   Laufzeiten ab einem Tag praktisch nie. Ein Satz dazu in DECISIONS (Nachtlauf-Notiz T7-Review 3). Prüfungen für
   `bot-remove` (Route) **und** unter der Sperre vor `removeEmote` verlangen `StartsAtAuthoritative = true`.
   Generationswechsel durch Uhrenabweichung (Plan-483 R10) beendet Eigentum: hingenommen, Folge ist Vorlage statt Entfernen.
4. **Nutzungsfenster: bleibt, wie es ist [E].** Der angebrochene erste Tag zählt voll (acht Tageszeilen geteilt durch 7).
   Begründung: (a) Tagesgranularität lässt kein exaktes Fenster zu; jede Alternative irrt in eine Richtung. Ganze Tage durch
   die Zahl der Tageszeilen zu teilen (8) senkt das Mittel und irrt Richtung **Entfernen**, die bestehende Rechnung irrt Richtung
   **Schutz** (Vorlage statt Entfernen), höchstens um die Nutzung eines Teiltags geteilt durch 7. (b) Spec E4 sagt wörtlich
   „an den UTC-Tagen vom Tag des Fensterbeginns bis zum Bewertungstag“. (c) Die Rechnung ist geteilt mit der Detailantwort und
   der Sparkline (K3, `daily` höchstens acht Einträge); eine Änderung träfe live genutzten Code ohne Nutzen für den
   manuellen Modus. (d) Bei 1 bis 7 Tagen Laufzeit ist der erste Tag der Einfügetag: Vor dem Start hatte dieser Slot keine
   Nutzung, der Fehler betrifft nur andere Slots desselben Emotes. K4 hält das fest: ein Test „30 Tage Laufzeit, Nutzung nur am
   angebrochenen ersten Tag vor Fensterbeginn → zählt mit, Ergebnis `protected` bei passender Schwelle“, ein Satz in DECISIONS
   und im Operations-Text zur Schwelle („rechnet eher zu hoch“).

### 2.2 Token: Ablage, Siegel, Öffnen (T1, T2)

- **Tabelle `WishBotCredentials`** nach Spec 6.4, CHECK `Id = 1`, Status-CHECK auf die fünf Werte. **Keine Seed-Zeile [E]**
  (Muster `WishCaptureState`, DECISIONS K6 (a)): fehlt die Zeile, gilt `Status = none`, `KillSwitch = false`; jede Schreibung ist
  ein Upsert auf `Id = 1`. Additiv; das laufende Image liest die Tabelle nie.
- **`CredentialVersion` (int, Default 0) [E, W20] (Sol-P1-3):** jedes `PUT /token` und `DELETE /token` erhöht sie im selben
  Upsert. Der Worker merkt sich zum geöffneten Token dessen Version; **jedes** Zurückschreiben aus dem Worker (Prüfergebnis,
  `BotSevenTvUserId`, `invalid` nach 401/403, `expired`, `PausedUntilUtc`) ist ein bedingtes Update mit
  `CredentialVersion = <geöffnete Version>`; trifft es keine Zeile, wird das Ergebnis verworfen und das neue Siegel im nächsten
  Takt geöffnet. Vor jedem 7TV-Schreiben vergleicht die Nachprüfung (2.5a (c)) die Version des geöffneten Tokens mit der frisch
  gelesenen; `KillSwitch` hängt nicht an der Version.
- **`RunnerEpoch` (bigint, Default 0) [E] (R2-3, Fencing):** erhöht nur der Runner, direkt nach dem Erwerb des Advisory-Locks
  (Upsert, legt die Zeile bei Bedarf mit `Status = none` an). `PUT`/`DELETE /token` und `PUT /kill-switch` lassen die Spalte
  unberührt; weil der Runner die Zeile während einer Sende-Transaktion `FOR SHARE` hält, wartet ein solches Update höchstens
  deren Ende ab (≤ 20 s, 2.5a (c)). Verwendung in 2.5a (g).
- **Siegelformat** nach Spec 7.1: `v1.` + base64(verpackter Schlüssel ‖ Nonce ‖ Chiffrat ‖ Tag), RSA-3072-OAEP-SHA256 über
  einen frischen AES-256-Schlüssel, AES-GCM mit 12 Byte Nonce und 16 Byte Tag. **[E]** Feste Zusatzdaten (AAD) `emotepurge:wishbot:v1`,
  damit ein Siegel nicht als anderes Geheimnis taugt. **Schlüsselformat [E]:** je eine Zeile base64-DER (öffentlich
  SubjectPublicKeyInfo, privat PKCS#8), weil mehrzeiliges PEM in Compose-Variablen bricht; der Opener prüft die
  Schlüssellänge 3072.
- **Schnittstellen (Core):** `IWishBotTokenSealer.Seal(token) → string` (nur Api), `IWishBotTokenOpener.TryOpen(sealed) →
  string?` (nur Worker; jede Abweichung, auch Präfix, Länge, Tag, falscher Schlüssel, ergibt `null`, nie eine Ausnahme mit
  Inhalt, AK72). Dazu rein (Core): `WishBotJwt.TryReadExpiry(token) → DateTime?` (drei base64url-Segmente, `exp` als Zahl;
  sonst `null` = `wish_bot_token_malformed`; **ein Token ohne `exp` ist malformed [E]**, weil Spec 7.7 die Anzeige braucht).
- **Registrierung [E]:** zwei Einstiege nach Befund 9, `AddWishBotTokenSealing(configuration)` (Api, `Program.cs` neben
  `AddLogCapture`) und `AddWishBot(configuration)` (Worker, in `AddWorkerCore`; registriert Opener, Schreib-Client,
  Runner, Signal). **Die Schlüssel stehen nicht in `WishOptions`** (die Optionen sind in beiden Prozessen injizierbar); jeder
  Einstieg liest seinen Schlüssel selbst aus `IConfiguration`. Fail-Fast: `Wishes:BotMode:Enabled = true` und Schlüssel fehlt
  oder ist ungültig → Start bricht mit englischer Meldung ab, die den Schlüsselnamen nennt, nie den Wert. Bei ausgeschaltetem
  Bot-Modus registriert der Api-Einstieg einen Sealer, der immer ablehnt (die Route antwortet ohnehin 404), der Worker-Einstieg
  keinen Opener. AK72: `Api.Tests` prüft über den Service-Provider der Factory, dass kein `IWishBotTokenOpener` und kein
  `ISevenTvWriteClient` auflösbar ist.
- **Klartext in der Api** lebt nur in der einen Anfrage: Form prüfen, `exp` lesen, abgelaufen → 400, versiegeln, Upsert mit
  `Status = unverified`, `ExpiresAtUtc = exp`, `SetAtUtc = jetzt`, `BotSevenTvUserId = null`, `LastErrorCode = null`, Audit
  `wishBot.tokenSet` (Details nur `expiresAtUtc`), Commit, dann `WISHBOT:` ohne Nutzlast. Die Api ruft mit dem Token nie 7TV.
  Der Anfragekörper wird nie geloggt; das Modell des Körpers hat kein `ToString`, das den Wert zeigt.

### 2.3 Verfügbarkeit und effektiver Modus (T4, T6)

- **Global [P]:** `IWishBotAvailability` echt (Infrastructure, **scoped**, liest die eine Zeile): verfügbar genau dann, wenn
  `Wishes:Enabled` **und** `Wishes:BotMode:Enabled` (eigener Prozess), `Status = valid`, `ExpiresAtUtc > jetzt`,
  `KillSwitch = false`. Eine Pause nach 429 (`PausedUntilUtc`) ist **keine** Nichtverfügbarkeit (sonst legte ein kurzer 429 alle
  fälligen Entfernungen vor). Ersetzt `WishBotNeverAvailable` in der einen Registrierung (Befund 6); die Lebensdauer passt zu
  `WishExpiryService` (scoped). `WishBotNeverAvailable` entfällt.
- **Je Kanal [E], eine reine Funktion `WishBotChannelState.Of(settings, globallyAvailable, listed, activatorIsAdminById, now)`**
  mit Ergebnis `manual` (Bot-Modus nicht aktiviert), `bot` (aktiviert, nicht ausgesetzt, global verfügbar, gedeckt), `fallback`
  (aktiviert, aber global nicht verfügbar, ausgesetzt, nicht gedeckt **oder Editor-Status unbekannt**). „Gedeckt“ (E8) = Kanal
  in `Wishes:PilotChannelIds` (Twitch-ID, `*` = alle) **oder** `BotModeActivatedByTwitchUserId` ist globaler Admin per ID
  ([B] F2: `Auth__AdminTwitchUserIds` auch am Worker, Prüfung im Betrieb per Twitch-ID, nie über die Login-Liste).
- **Editor-Status unbekannt [E, F4] (Sol-P2-7):** `BotEditorCheckedAtUtc` älter als 15 Minuten (feste Konstante) → `fallback`,
  ohne gespeicherten Grund; Api und Worker rechnen es gleich aus derselben Spalte, die Wiederaufnahme ist die nächste
  erfolgreiche Prüfung. Der Runner schreibt `BotEditorCheckedAtUtc` nach jeder erfolgreichen Prüfung (höchstens einmal je
  Minute und Kanal). Damit erscheint `approved` bei dauerhafter 7TV-Lesestörung nach 15 Minuten wieder als Handlungsbedarf.
- **Editor-Nachweis gehört zu einem Token [E] (R2-5):** jedes Editor-Ergebnis (positiv, `not_editor`, Aktivierung) wird mit
  `BotEditorCheckedCredentialVersion = V` gebucht, bedingt auf die Credentials-Zeile (`FOR SHARE`, im selben Commit)
  `CredentialVersion = V`, `Status = valid` und `BotSevenTvUserId` = die Bot-ID, mit der gelesen wurde; trifft es nicht, wird das
  Ergebnis verworfen. „Frisch“ (hier und in 2.5) heißt zusätzlich: gespeicherte Version = aktuelle `CredentialVersion`. Nach
  einer Rotation ist also jeder Kanal ungeprüft, bis das neue Token seine eigene Prüfung bestanden hat (höchstens ein Takt).
- **Wer was daraus liest [E]:** `mode` im DTO ist `bot` nur bei `bot`; `approved` zählt als Handlungsbedarf bei `manual` und
  `fallback` (Spec 5 „oder der Bot ausgefallen ist“, AK79); `StatusesOf` und `WishOverviewService` bekommen den Kanalzustand
  statt `IsBotMode` ([W1]). Die globale Verfügbarkeit wird je Anfrage höchstens einmal gelesen (Übersicht: einmal für alle
  Kanäle). **Recht auf die Kanal-Vorgaben** (`CanChangeSettings`) hängt an `BotModeEnabled` roh, nicht am effektiven Zustand
  ([W2]): Solange die Aktivierung gespeichert ist, gilt D7, auch im Rückfall.
- **`wishBotModeAvailable` in `/permissions` [E]:** nur wenn `Wishes:Enabled` und `Wishes:BotMode:Enabled` an sind, wird die
  Zeile gelesen; sonst `false` ohne Datenbankzugriff (Befund 19). Wert = global verfügbar **und** (Kanal gelistet **oder** Aufrufer
  globaler Admin per ID). Ein In-Process-Cache der Zeile für 30 s ist erlaubt, wenn T7 einen messbaren Mehraufwand findet; die
  Admin-Routen und der Worker lesen immer frisch.

### 2.4 Schreib-Client (T3)

- `ISevenTvWriteClient` (Core, `SevenTv/`), Implementierung in Infrastructure, eigener typisierter HttpClient gegen
  `https://7tv.io/v4/gql`, 10 s Timeout, `Authorization: Bearer` je Aufruf aus dem geöffneten Token (nie in
  `DefaultRequestHeaders`, nie in einem Feld des Clients), Telemetrie über `ProviderRequestTelemetryHandler` mit neuer Quelle
  `RateLimitCallSources.SevenTvBot` (`seventv-bot`). **Das Standard-Logging des HttpClient-Factorys ist für diesen Client
  abgeschaltet [E]** (kein „Request Headers“-Eintrag auf Trace-Stufe); AK75 beweist es.
- **Operationen (Form):** `ReadSetEntriesAsync(token, setId)` → alle Einträge mit `emoteId`, `alias`, `addedAt?`, `addedById?`,
  alle Seiten oder Fehler (Seitengrenze wie Befund 12; fehlende Seite, `null`-Daten, Abbruch → `Unavailable`, fail-closed);
  `AddEmoteAsync(token, setId, emoteId, alias)`; `RemoveEmoteAsync(token, setId, emoteId, alias)` (Alias immer gesetzt);
  `VerifyAsync(token)` → eigene 7TV-ID. Die genaue Abfrage für `VerifyAsync` und die Feldnamen für `addedById` liefert T0.
- **Ergebnis je Aufruf (eine geschlossene Form [E]):** `Ok`, `RateLimited(retryAfter)` (HTTP 429 mit `Retry-After`, oder HTTP
  200 mit `extensions.status = 429`; Default 60 s, Kappe 6 h, AK76), `Unauthorized` (HTTP 401/403), `LackingPrivileges`
  (`extensions.code = LACKING_PRIVILEGES`), `Rejected(reason)` mit `alias_conflict` (`extensions.status = 409`), `set_full`,
  `emote_gone`, `unknown` (Zuordnung der beiden mittleren nach AK91; nicht herstellbar → `unknown`), `Timeout` und
  `Unavailable` (Netz, 5xx, unlesbar). **Kein Fehlertext von 7TV geht in ein Log oder in die Datenbank** (nur Code und Status).
- Der Lesepfad des Vollsyncs bleibt unverändert (Spec Files: `SevenTvApiClient` ohne `addedById`).

### 2.5 `WishBotWorker` und Runner (T5, T8 bis T10)

- **Dienst:** konkrete Klasse, nur `AddHostedService`, registriert **vor** `WishCaptureWorker` (Befund 10), damit siebzehn
  (AK96). Bei `Wishes:Enabled = false` oder `Wishes:BotMode:Enabled = false` eine Logzeile, Ende, kein Scope, kein Öffnen.
  Wartet nicht auf `BootRecoveryGate` (der Runner liest nur Datenbank und 7TV). Takt 30 s per `PeriodicTimer`, Anstoß über
  `WishBotSignal` (Form von `ChatLogBackfillSignal`), gesetzt in `Worker.cs` beim Präfix `WISHBOT:` (neu in `BotCommands`).
- **Advisory-Lock [P]:** eigener Schlüssel `emotepurge:wish-bot`, gehalten auf eigener Verbindung für die Lebensdauer der
  Schleife (Befund 11). Ohne Lock schreibt die Instanz nichts, liest nichts aus 7TV und versucht es im nächsten Takt erneut
  (AK81). Gehalten wird er nicht nur beim Holen: die Probe ist eine Schranke vor und nach jedem 7TV-Aufruf, beim Commit zählt
  die Runner-Epoche (2.5a (g)). Rückfall-Lauf (2.8) und Fristschritt brauchen den Lock nicht.
- **Ein Durchgang, in dieser Reihenfolge [E]:** (0) **Fristschritt [E] (R2-7)**, jeden Takt **vor** dem Lock-Versuch, rein lokal,
  ohne Token, 7TV, 429-Pause und Aktionsbudget, je Wunsch Zeilensperre und bedingtes Update: `unconfirmed` mit
  `BotConfirmDeadlineAtUtc ≤ jetzt` → `presented` (`unconfirmed`); offener Entfernungsversuch mit `BotRemoveAtUtc + 15 min ≤ jetzt`
  → nach 2.5a (e) ohne 7TV (Beleg bleibt, (h)). Ist der Bot-Schalter aus, endet der Dienst, und der Ablauf-Lauf (Schritt 0, 2.8)
  legt ohnehin alles vor. (1) Token-Haushalt (2.8), (2) Aktivierungsanfragen (2.9), (3) Kanalzustände:
  Deckung und Editor-Status je Bot-Kanal, Aussetzen und Wiederaufnehmen, (4) Rückfall-Lauf, (5) die abgeleitete Warteschlange
  abarbeiten, eine Aktion zur Zeit, `WriteDelaySeconds` (Default 2, 0 bis 60) zwischen zwei 7TV-Schreibzugriffen, bis sie leer
  ist oder 50 Aktionen erreicht sind (dann der nächste Takt).
- **Warteschlange (abgeleitet, keine Tabelle):** zuerst offene Prüfphasen (`BotRemoveEvidence` mit `BotRepairAtUtc`, 2.5a (h),
  auch außerhalb des Zustands `bot`, nur lesend), dann in Bot-Kanälen (Zustand `bot`) offene Versuche (`BotRemoveAtUtc`
  gesetzt, 2.5a (e)), dann `approved` (inklusive Absturzrest mit `BotWriteAtUtc`), `unconfirmed` mit `BotNextAttemptAtUtc ≤
  jetzt`, `active` oder `presented` mit `RemovalQueuedAtUtc`; älteste zuerst (Zeitpunkt der Freigabe, des letzten Schreibens bzw.
  der Einreihung), dann ID.
- **Vorprüfung vor jedem 7TV-Schreibzugriff** als reine Funktion `WishBotPreflight.Check(…)` mit fester Reihenfolge (Spec K4):
  Schalter → Deckung (E8) → `KillSwitch` (frisch aus der Datenbank, AK81) → `PausedUntilUtc` → Token-Status, Ablauf und
  `CredentialVersion` gleich der des geöffneten Tokens (2.2) → Editor-Status des Kanals (höchstens 1 Minute alt, zur selben
  `CredentialVersion` gebucht, 2.3). Das erste Nein
  gewinnt und bestimmt den Rückfall (global oder je Kanal). Lesezugriffe für Bestätigung und Vor-Read prüfen dieselbe Kette ohne
  den Editor-Status. Die Vorprüfung läuft als Teil der Nachprüfung unter Sperre (2.5a (c)), nicht davor.
- **Editor-Status [E]:** ein `GetEditorOfChannelsAsync(BotSevenTvUserId)` (unauthentifiziert, ohne `ModRoleCache`) beantwortet alle
  Kanäle auf einmal und gilt 1 Minute; ausgesetzte `not_editor`-Kanäle werden alle 15 Minuten neu geprüft (Wiederaufnahme ohne neue
  Aktivierung, Spec 7.8). `Unavailable` setzt nicht aus, sondern lässt den Durchgang ohne Schreiben enden (keine Aussetzung wegen
  7TV-Störung); hält es 15 Minuten an, ist der Kanal nach 2.3 im `fallback`, bis eine Prüfung wieder gelingt. Was die Prüfung wirklich unterscheiden kann (Einladung, Berechtigung „Emote-Sets verwalten“), klärt AK92; reicht
  `editor_of` nicht, nimmt T8 das in T0 gefundene authentifizierte Lesen.

### 2.5a Schreibversuche: der gemeinsame Vertrag (T1, T5, T6, T7, T9, T10) (Sol-P1-1, P1-2, P1-4, P2-5; R2-1 bis R2-4, R2-7)

Jeder 7TV-Schreibzugriff des Bots (Hinzufügen, Entfernen, Reparatur) ist ein **Versuch** mit diesen acht Regeln. 2.6 und 2.7
sagen nur noch, was je Ablauf geprüft und gebucht wird.

**Felder an `Wishes` [E, W20]** (Migration `AddWishBot`, T1, alle nullbar, additiv): `BotSetId` (Set des offenen Versuchs; nach
einer Bestätigung mit Eigentum das Set des eigenen Eintrags; in `presented` (`unconfirmed`) das Set des Versuchs für `recheck`;
sonst leer), `BotAttemptVersion` (`DecisionVersion` beim Festschreiben), `BotRemoveAtUtc` (Entfernungsversuch festgeschrieben,
Gegenstück zu `BotWriteAtUtc`), `BotRemoveEvidence` (jsonb: Zieleintrag mit den vier Merkmalen und die übrigen Einträge derselben
Emote-ID mit Alias und `addedAt`, **ohne fremde `addedById`**; nie im DTO, nie im Audit), `BotAddAckAtUtc` (Revision 2: `addEmote`
hat in **diesem** Versuch nachweislich `Ok` geliefert, gebucht unter derselben Sperre wie das Senden; einzige Grundlage für
Eigentum, (d)), `BotConfirmDeadlineAtUtc` (Revision 2: Ende der E11-Frist, (e)), `BotRepairAtUtc` (Revision 2: Prüfphase
festgeschrieben, (h)).

- **(a) Festschreiben vor dem Senden.** Eigene Transaktion unter Wunsch-Sperre, bedingt auf den eben geprüften Zustand, vor
  jedem 7TV-Schreiben: Hinzufügen nach 2.6 Punkt 1, Entfernen nach 2.7, Reparatur nach (h). Trifft das Update keine Zeile, wird
  nichts gesendet. **Setweit eins zur Zeit [E] (R2-2):** eine Hinzufüge-Absicht wird nicht festgeschrieben, solange ein anderer
  Wunsch (kanalübergreifend) mit demselben `BotSetId` und derselben Emote-ID `approved` mit `BotWriteAtUtc` oder `unconfirmed`
  ist; der Wunsch bleibt `approved` bis zu einem späteren Takt.
- **(b) Ein offener Versuch sperrt Entscheidungen [E, W21] (R2-1).** Solange `BotWriteAtUtc` in `approved` oder `BotRemoveAtUtc`
  gesetzt ist, antwortet jeder Verwalterbefehl 409 `wish_state_conflict`. Geprüft wird über **eine** reine Funktion
  (`WishBotAttemptGuard.Blocks(wish)`) unter der Wunsch-Sperre an **jedem** mutierenden Pfad: `RunTransitionAsync` (`PATCH`,
  `extend`, `keep`, `close`, `retry`, `re-add`, `reject`), `AdoptAsync` mit Wunsch, `CompleteRemovalAsync`, `UnlinkAsync`,
  `ApproveAsync` **am Zielwunsch** vor `MergeIntoRunningAsync`, `recheck`, `bot-remove`. `SupersedeActiveDecisionsAsync`
  (Schwellenänderung) überspringt Wünsche mit offenem Versuch; die Änderung kommt für sie zu spät wie ein 409 **[E]**. Der Abgleich
  nimmt solche Wünsche aus Schritt 3 **und** 4 aus (heute nur `approved` mit `BotWriteAtUtc` in Schritt 4,
  `WishReconcileService.cs:227`). Eine offene Prüfphase (h) sperrt nichts. Ausnahmen sind nur `DELETE …/wishes/bot`, der
  Rückfall-Lauf und der Fristschritt (2.5); sie lösen den Versuch nach (e) ohne 7TV auf.
- **(c) Nachprüfung unter Sperre, Senden und Buchen in einer Transaktion [E] (R2-3).** Sperrfolge: Kanalzeile `FOR SHARE` (neue
  Sperrform in `RowLocks`, Form wie `LockChannelsByIdsAsync`), dann
  Wunsch-Zeile `FOR UPDATE`, dann Credentials-Zeile `FOR SHARE` (dieselbe Folge Kanal → Wunsch wie alle übrigen Pfade). `FOR
  SHARE` auf dem Kanal hält den Set-Wechsel des Vollsyncs (`FOR UPDATE`, `SevenTvSyncService.cs:933-942`) bis zum Commit an, lässt
  Fremdschlüssel-Inserts aber durch. Geprüft werden: Versuchsstempel, `BotAttemptVersion = DecisionVersion`, Zustand, Kanalzustand
  `bot` (2.3), `ActiveEmoteSetId = BotSetId` (jetzt unter der Kanalsperre: ein Set-Wechsel liegt vollständig davor oder danach),
  Vorprüfung 2.5 (`KillSwitch`, `CredentialVersion` aus der gesperrten Zeile), Runner-Epoche (g). Ein Nein → nicht senden; ein in
  **diesem** Durchgang festgeschriebener, nachweislich nie gesendeter Versuch wird bedingt zurückgenommen (Hinzufügen: bleibt
  `approved`), jeder andere nach (e) aufgelöst. Sonst senden und unter **derselben** Sperre buchen (Spec 8). **Höchstens zwei
  7TV-Aufrufe je solcher Transaktion** (Senden, Nach-Read; je 10 s), damit wartende Kanal-Schreiber unter Npgsqls
  Standard-Timeout von 30 s bleiben (im Repo nicht überschrieben). Reads ohne Senden (Vor-Read, Bestätigung, Wiederaufnahme,
  Prüfphase) laufen ohne Kanalsperre; ihre Buchung ist eine kurze Transaktion derselben Sperrfolge ohne 7TV-Aufruf.
- **(d) Set-Bindung und Eigentum.** Alle Reads und Schreibzugriffe eines Versuchs gehen an `BotSetId`, nie an das inzwischen
  aktive Set. Bestätigt wird nur ein Eintrag in `BotSetId` mit `addedAt` zwischen `BotWriteAtUtc − 2 min` **[E]** (Uhrenabweichung)
  und dem Read: die Generation dieses Versuchs (auch nach `recheck`). Weicht `ActiveEmoteSetId` bei der Buchung ab, wird nicht
  bestätigt. **Eigentum nur mit Sende-Nachweis [B] F6 (R2-2):** zusätzlich zu `addedById == BotSevenTvUserId` verlangt Eigentum
  `BotAddAckAtUtc`. Ohne ihn (Absturz vor oder nach dem Senden, `Timeout`, `Unavailable`, Lock- oder Epochenverlust zwischen Antwort
  und Buchung) wird höchstens **ohne** Eigentum bestätigt: sichere Richtung, Folge ist Vorlage statt Entfernen. Eigentum gilt nur
  in `BotSetId`; Entfernen verlangt `ActiveEmoteSetId = BotSetId`.
- **(e) Wiederaufnahme unklarer Ergebnisse.** Was nach (a) offen bleibt (Timeout, `Unavailable`, gescheiterter Nach-Read, Absturz,
  Lock-Verlust nach dem Senden), löst der nächste Lock-Inhaber auf, nie durch erneutes Senden. Hinzufügen: `approved` mit
  `BotWriteAtUtc` → `unconfirmed` ohne `BotAddAckAtUtc`, Reads auf `BotSetId` (Spec K4, Absturzrest). Entfernen: frischer Read auf
  `BotSetId`; Zieleintrag mit allen vier Merkmalen noch da → nicht entfernt, Versuch leeren (ohne `DecisionVersion`), die
  Einreihung bleibt; fehlt er → als Bot-Entfernung buchen (`removed`, `wish.botRemove` mit `outcome: resumed`), Beleg in die
  Prüfphase (h, nur lesend). **Auflösung ohne 7TV** (Fristschritt nach 15 Minuten, `DELETE …/wishes/bot`, Rückfall-Lauf): Hinzufügen
  → `presented` (`unconfirmed`); Entfernen → `presented` (`bot_unavailable`), Versuch und Einreihung leer, **der Beleg bleibt** als
  Prüfphase (h) **[B] F6 (R2-4)**. Ein dann tatsächlich entfernter Eintrag endet über den Abgleich als `removed_externally`; das ist
  die einzige Stelle dafür (Operations).
- **(f) Idempotente, gebundene Buchung (R2-1).** Jede Buchung, auch jede Wiederaufnahme, ist ein bedingtes Update auf
  Versuchsstempel, `BotAttemptVersion = DecisionVersion = <gelesener Wert>` und den erwarteten Status; Audit und `wish.changed` nur,
  wenn es eine Zeile traf, im selben Commit. Trifft sie nicht, hat ein anderer Pfad den Versuch schon aufgelöst; gebucht wird nichts.
- **(g) Lock und Runner-Epoche [B] F6 (R2-3, abgestuft).** Die Probe nach Muster `HoldsLoopLockAsync` (nie ein Neuerwerb) bleibt
  der billige Abbruch vor (a), vor dem Senden und nach jedem 7TV-Aufruf. **Beim Commit** zählt die Epoche: der Runner erhöht
  `RunnerEpoch` nach dem Lock-Erwerb und merkt sich den Wert; jede Runner-Transaktion, die bucht oder sendet, liest die
  Credentials-Zeile `FOR SHARE` mit `RunnerEpoch = <eigener Wert>`, sonst Rollback und Abbruch des Durchgangs. Weil die Erhöhung
  des Nachfolgers auf dieses `FOR SHARE` wartet, endet eine laufende Sende-Transaktion des Vorgängers vollständig vor dem ersten
  Schritt des Nachfolgers, und danach bucht der Vorgänger nichts mehr. **Nicht rückholbar** ist ein HTTP-Aufruf, der schon
  unterwegs ist, wenn die Transaktion des Vorgängers abbricht (Arbeitsverbindung getrennt, Prozess hängt): Restfenster R11, gedeckt
  durch den vor dem Senden festgeschriebenen Versuch und (e) bzw. (h). Dieses Fenster wird nicht weiter geschlossen.
- **(h) Reparatur als eigene Phase [B] F6 (R2-4).** Braucht es nach einer Entfernung eine Reparatur (2.7), bucht die
  Sende-Transaktion der Entfernung zugleich `BotRepairAtUtc = jetzt` und behält `BotRemoveEvidence` (dauerhaft vor dem Senden der
  Reparatur). Gesendet wird die Reparatur **nur im selben Durchgang direkt danach** in eigener Transaktion nach (c), genau ein
  `addEmote`; eine eindeutige Antwort (`Ok`, `Rejected`, `LackingPrivileges`, `Unauthorized`, `RateLimited`) wird gebucht (Audit
  `wish.botRepair` mit `outcome`, Phase und Beleg leer). Jede andere offene Prüfphase (Absturz, `Timeout`, Lock-Verlust, ein Nein in (c), Auflösung nach (e)) ist **unklar** und wird
  **nur lesend** aufgelöst, sobald ein Read möglich ist (Token gültig, kein Notaus; Kanalzustand egal): Zieleintrag noch da → nichts
  entfernt, Phase und Beleg leer, ohne Audit; sonst je Eintrag des Belegs, der fehlt, Audit `wish.botRepair` mit `outcome:
  needs_review` (ohne Alias), Phase und Beleg leer. Notaus, Token-Ausfall und Bot-Schalter aus lassen Phase und Beleg unverändert
  liegen; keine Frist löscht sie. Verwalterbefehle lassen die Phase unberührt (`re-add` und `retry` setzen sie nicht zurück); ein
  neuer Entfernungsversuch desselben Wunsches wartet, bis sie leer ist.

### 2.6 Hinzufügen und Bestätigen (T9)

1. **Absicht festschreiben (2.5a (a)):** bedingtes Update auf `Status = approved AND BotWriteAtUtc IS NULL AND DecisionVersion =
   <gelesen>` setzt `BotWriteAtUtc = jetzt`, `BotSetId = Channel.ActiveEmoteSetId`, `DecisionVersion + 1`, `BotAttemptVersion`,
   `SlotId = null`, `LinkOrigin = null`, `BotAddAckAtUtc = null` (2.1 Punkt 2), nur wenn setweit frei (2.5a (a)). Keine Zeile → nichts senden (Abgleich oder Mensch war schneller; der Wunsch
   ist dann `active` ohne Eigentum, AK83; ein `PATCH` davor wird im nächsten Takt mit dem neuen Alias aufgenommen).
2. **Vor-Read** auf `BotSetId`: Emote-ID schon im Set → Versuch leeren (2.5a (f)), `presented` (`already_in_set`), kein Schreiben.
   Read nicht möglich → Absicht zurücknehmen (bedingt), Wunsch bleibt `approved`, nächster Takt.
3. **Nachprüfung (2.5a (c)), dann `addEmote` mit dem Alias aus der gesperrten Zeile:** `Ok` → `unconfirmed` (`bot_write`),
   `BotAddAckAtUtc = jetzt` (Sende-Nachweis, 2.5a (d)), `BotAttemptCount = 0`, `BotNextAttemptAtUtc` und
   `BotConfirmDeadlineAtUtc` nach Punkt 5, Audit `wish.botAdd` (Details `wishId`, `sevenTvEmoteId`, nie Alias).
   `Rejected(r)` → Versuch leeren, `failed` mit Grund `r`, kein Retry (Spec F2). `LackingPrivileges` → Versuch leeren, bleibt
   `approved`, Kanal `not_editor` (AK80). `Unauthorized` → wie `LackingPrivileges` für den Wunsch, Token `invalid` (bedingt auf die
   `CredentialVersion`, 2.2), globaler Rückfall. `RateLimited` → Versuch leeren, `PausedUntilUtc`, bleibt `approved`. **`Timeout`
   und `Unavailable` nach dem Senden → `unconfirmed` ohne `BotAddAckAtUtc` [E]**: ob 7TV geschrieben hat, entscheiden die
   Bestätigungs-Reads (höchstens ohne Eigentum), ein zweites `addEmote` gibt es nicht.
4. **Absturzrest:** nach 2.5a (e), nie erneut hinzufügen.
5. **Bestätigungsplan E11, absolute Frist [P] (Sol-P2-7):** reine Funktion `WishBotConfirmSchedule.Next(plannedAt, attempt)`:
   Reads bei 0 s, 30 s, 2 min, 5 min, 15 min nach dem Anker (`BotWriteAtUtc`, nach `recheck` der Zeitpunkt des `recheck`). Der
   Plan rechnet vom **geplanten**, nicht vom tatsächlichen Termin, und **ein gescheiterter Read zählt als Versuch**.
   **Frist (R2-7):** `BotConfirmDeadlineAtUtc = Anker + 16 min` **[E]** (eine Minute Luft für den fünften Read) wird beim Übergang
   nach `unconfirmed` gespeichert; danach setzt allein der Fristschritt (2.5, Schritt 0) `presented` (`unconfirmed`) über
   `bot_unconfirmed`, auch wenn kein Read ausführbar war (Editor-Ausfall, Token, 429-Pause, Budget). `BotSetId`, `BotWriteAtUtc` und
   `BotAddAckAtUtc` bleiben dabei für `recheck` stehen (2.9). Liefert AK89 einen nutzbaren
   Rückgabewert von `addEmote`, ersetzt er den ersten Read; AK88 über 5 min in einem Fall → E11 vor dem Merge neu.
6. **Bestätigungs-Read** (reine Entscheidung `WishBotOwnership.Confirm(entries, emoteId, alias, botUserId, generationFrom)`) auf
   `BotSetId` mit Set-Bindung (2.5a (d)): genau ein Eintrag mit Emote-ID und Alias **und** `addedAt` in der Generation des Versuchs
   → `bot_confirm`; `addedById == BotSevenTvUserId` (der Version, die gerade gilt) **und** `BotAddAckAtUtc` gesetzt → Eigentum
   (`BotOwned`, `BotOwnedAddedAtUtc`, `BotSetId` bleibt), sonst ohne (`BotSetId` leer). Gebucht in kurzer Transaktion nach 2.5a (c)
   ohne 7TV-Aufruf, bedingt nach 2.5a (f), `ActiveEmoteSetId = BotSetId` unter der Kanalsperre. Start nach 2.1 Punkt 3, `LinkOrigin = bot`, `ApprovedAtUtc` unverändert,
   `FirstActivatedAtUtc ??= jetzt`, `Permanent` → `kept` (2.1 Punkt 1). Eine Eigentumsänderung erhöht `DecisionVersion`. Kein
   passender Eintrag oder Read nicht möglich → nächster Termin nach Punkt 5.
7. **Koordination mit dem Abgleich [B]:** Spec K4 wörtlich; der Abgleich verknüpft danach nur die `SlotId` ([W7]).

### 2.7 Entfernen, Reparatur, Erkennungsgrenze (T10)

- **Transaktion 1 (prüfen, lesen, festschreiben):** Wunsch-Zeile sperren (`RowLocks.LockWishesByIdsAsync`), lesen, prüfen:
  `RemovalQueuedAtUtc` gesetzt, `RemovalQueuedVersion == DecisionVersion`, (`active` mit `ExpiresAtUtc ≤ jetzt`) oder
  (`presented` mit Auftrag aus `bot-remove`), `BotOwned`, `StartsAtAuthoritative`, Kanal `bot`, `ActiveEmoteSetId = BotSetId`
  (2.5a (d); abweichend → Eigentum endet, `presented` (`ownership_lost`), kein Senden). Nicht berechtigt → nichts senden,
  Einreihung leeren (ohne `DecisionVersion` zu erhöhen; der Ablauf-Lauf bewertet `active` neu, ohne zweiten `wish.due`,
  Plan-484 Befund 11), Commit. Sonst **frischer Read** auf `BotSetId` unter der Sperre (Spec 8): Eintrag mit Emote-ID und Alias
  fehlt → `removed_externally` (`EndedAtUtc = jetzt`, Eigentum endet). Emote-ID, Alias, `addedAt == BotOwnedAddedAtUtc`,
  `addedById == BotSevenTvUserId` nicht alle exakt → kein Senden, Eigentum endet, `presented` (`ownership_lost`). Sonst Versuch
  festschreiben (2.5a (a)): `BotRemoveAtUtc`, `BotAttemptVersion = DecisionVersion` (keine Erhöhung: der Versuch ist keine
  Entscheidung), `BotRemoveEvidence` aus diesem Read; Commit nur mit gültiger Epoche (2.5a (g)). Offene Prüfphase (2.5a (h)) →
  nicht festschreiben, Einreihung bleibt.
- **Transaktion 2 (senden, buchen):** Nachprüfung (2.5a (c), Kanal `FOR SHARE`, ohne neuen Vor-Read; das Fenster zum Read aus
  Transaktion 1 ist ein Commit lang, Restrisiko Spec 8), `removeEmote`, **Nach-Read**, Wunsch `removed` (Bestand `remove`), Audit
  `wish.botRemove` (mit `previousExpiresAtUtc`, wenn überfällig, wie K2 (e)), Versuch leeren; alles unter dieser einen Sperre.
- **Reparatur (R2-4):** Nach-Read gegen `BotRemoveEvidence`: fehlt ein Eintrag derselben Emote-ID, der **nicht** der Bot-Eintrag
  war, bucht Transaktion 2 statt `removed` → `presented` (`repair_review`) mit leerer Einreihung und die Prüfphase
  (`BotRepairAtUtc`, Beleg bleibt). Danach nach 2.5a (h) genau ein `addEmote` unter demselben Alias (ohne Eigentum, ohne Frist,
  ohne neuen Wunsch) in Transaktion 3, Audit `wish.botRepair` mit `outcome`; das Ergebnis des Wunsches hängt nicht davon ab.
  Unklare Fälle (auch aus der Wiederaufnahme 2.5a (e)) nur lesend nach 2.5a (h).
- **Erkennungsgrenze [E] (Spec 8, AK77):** unmittelbar vor dem Commit liest der Runner die lokalen Slots: Gibt es einen nicht
  archivierten Slot derselben Emote-ID mit dem Alias des Wunsches, dessen `AddedAt` eine andere Generation als
  `BotOwnedAddedAtUtc` zeigt, und zeigt der Nach-Read keinen Eintrag dieses Alias mehr → Audit `wish.botSuspectRemoval`
  (`wishId`, `slotId`), keine Reparatur. Ein nie gesehener Ersatz hinterlässt keinen Eintrag (so gewollt).
- **Fehler nach dem Senden:** `removeEmote` mit `Timeout`/`Unavailable` → Nach-Read entscheidet; ist er nicht möglich, bucht
  Transaktion 2 nichts, der Versuch aus Transaktion 1 bleibt, Wiederaufnahme nach 2.5a (e) mit dem gespeicherten Beleg.
- **Sperrdauer:** ein 7TV-Aufruf in Transaktion 1 (nur Wunsch-Sperre), zwei in Transaktion 2 (Entfernen, Nach-Read), einer in
  Transaktion 3 (Reparatur), je 10 s; in 2 und 3 ist die Kanalzeile `FOR SHARE` gehalten (2.5a (c)). Ein Befehl während Transaktion 2 wartet und trifft danach den neuen Zustand (409, AK83); zwischen den Transaktionen antwortet er
  sofort 409 (2.5a (b)) [P]: beobachtbar dasselbe. Risiko R3.

### 2.8 Token-Haushalt, Rückfall, Notaus (T5, T8)

- **Token-Haushalt (Worker):** `unverified` → öffnen, `VerifyAsync`, `valid` mit `BotSevenTvUserId` und `LastVerifiedAtUtc`, oder
  `invalid` mit `LastErrorCode`; Öffnen scheitert → `invalid` (`seal_unreadable`). `exp` erreicht → `expired`. Erneute Prüfung alle
  6 Stunden **[E]** und nach jedem `Unauthorized`. Ab 7 Tagen vor `exp` höchstens eine Warnung je UTC-Tag (Zähler im Speicher,
  Neustart darf einmal mehr warnen). Wechsel nach `invalid` oder `expired` → je Wechsel ein Audit `wishBot.tokenState`. Jedes
  dieser Ergebnisse wird nur auf die `CredentialVersion` gebucht, deren Token geprüft wurde (2.2, Sol-P1-3): eine verspätete
  Prüfung oder ein verspätetes 401 von Token A trifft ein inzwischen gesetztes Token B nie; Audit nur, wenn das Update traf.
  Ändert sich `BotSevenTvUserId` gegenüber dem Vortoken, bleibt bestehendes Eigentum stehen und scheitert beim frischen Read
  (`ownership_lost`) **[E]**; die Admin-Seite sagt das beim Setzen.
- **Rückfall-Lauf `IWishBotFallback.ApplyAsync` [E] ([W8]):** für jeden Kanal im Zustand `fallback` (und für alle Bot-Kanäle bei
  globaler Nichtverfügbarkeit): `unconfirmed` und `approved` mit gesetztem `BotWriteAtUtc` → `presented` (`unconfirmed`);
  `active` mit Einreihung → `presented` (`bot_unavailable`), Einreihung bleibt **nicht** (geleert; ein Mensch entscheidet,
  „Bot entfernen lassen“ reiht neu ein); `presented` mit Auftrag → Auftrag geleert, bleibt `presented`; offener
  Entfernungsversuch → nach 2.5a (e) ohne 7TV, der Beleg bleibt als Prüfphase (2.5a (h)); offene Prüfphasen bleiben unberührt.
  `approved` ohne Absicht bleibt `approved` (zählt als Handlungsbedarf, 2.3). Je Wunsch Zeilensperre, bedingte Updates, ein `wish.changed` je Kanal.
  Aufgerufen vom Runner (Schritt 4) **und vom Ablauf-Lauf als Schritt 0** (K2-Dienst, läuft mit `Wishes:Enabled` auch bei
  ausgeschaltetem Bot), damit ein Abschalten von `WISHES_BOT_MODE_ENABLED` nichts hängen lässt. Ohne Bot-Kanäle eine Indexabfrage.
- **Kanal aussetzen und aufnehmen:** `not_listed` bei fehlender Deckung, `not_editor` bei negativem Editor-Status oder
  `LackingPrivileges`; je Wechsel Audit `wish.botSuspend` (Details `reason`, beim Aufnehmen `resumed`), `BotEditorCheckedAtUtc`
  bei bestandener Prüfung. Aufnehmen leert den Grund; der Bot nimmt nur `approved` auf (Spec 7.8).
- **Notaus:** `PUT /kill-switch` wirkt vor dem nächsten Schreibzugriff (in der Sende-Transaktion aus der gesperrten Zeile
  gelesen; ein laufender Aufruf endet zuerst, das Update wartet höchstens dessen Transaktion ab); Zustand dann wie AK79 über die
  Verfügbarkeit (global nein → Rückfall-Lauf). Prüfphasen bleiben bis zum nächsten möglichen Read liegen (2.5a (h)).

### 2.9 Kanal-Api (T7)

| Route | Filter nach der Gruppe | Antwort |
|---|---|---|
| `GET …/wishes/bot` | – (`InteractiveRead`) | Form der Spec; `available` = global verfügbar, `listed` = Deckung durch Liste, `enabled` = `BotModeEnabled`, `canActivate`/`canDeactivate` spiegeln die Filter (ohne 7TV-Aufruf: ein Mod bekommt `canActivate = true` nur, wenn der Grant im Cache steht, sonst `null` = „unbekannt“ **[E]**) |
| `POST …/wishes/bot/activate` | `WishBotActivationAuthorizationFilter` (`Bookkeeping`) | 202 `{ activationPending: true }` |
| `DELETE …/wishes/bot` | `WishSettingsAuthorizationFilter` (`Bookkeeping`) | 204 |
| `POST …/wishes/{id}/bot-remove` | – (`Bookkeeping`) | 202 `WishDto` |
| `POST …/wishes/{id}/recheck` | – (`Bookkeeping`, Bestand) | 200 `WishDto` |

- **Aktivierungsfilter, Reihenfolge [E] ([W12]):** (1) `Wishes:Enabled` oder `Wishes:BotMode:Enabled` aus → durchlassen (Handler:
  404 `wishes_disabled` bzw. 409 `wish_bot_unavailable`, kein 7TV-Aufruf); (2) globaler Admin per ID → durch; (3) Kanal nicht
  gelistet → 403 `wish_bot_channel_not_listed` (billig, vor jedem 7TV-Aufruf); (4) Broadcaster per Twitch-ID → durch; (5)
  `ISevenTvEditorService.GetEditorGrantsAsync(caller)`: `Ok` mit der Twitch-ID des Kanals → durch; `Ok` ohne, `NoSevenTvAccount`,
  Kanal ohne `TwitchChannelId` → 403 `wish_bot_activation_forbidden`; `Unavailable` → 503 mit dem vorhandenen, schon mehrfach
  wiederverwendeten quellneutralen Code `foreign_channel_seventv_unavailable` (`ApiErrorCodes.cs:66-73`) **[E]**; kein
  Autorisierungsfilter antwortet heute 503, das ist neu. Neuer Fall in `Api.Tests` (Regel 11).
- **Handler `activate`:** nicht verfügbar → 409 `wish_bot_unavailable`; `BotModeEnabled` → 409 `wish_bot_already_active`
  (Aktivierender unverändert); unter Kanalsperre `BotActivationRequested*` setzen, `BotActivationError` leeren. Liegt schon eine
  Anfrage, die jünger als 10 Minuten ist, bleibt deren Anfragender stehen, die Route stößt nur erneut an **[E]** (K5 „Erneut
  prüfen“ ruft dieselbe Route). `WISHBOT:`, `wish.changed`.
- **Bestätigung im Worker (T8):** bestätigt wird nur genau die geprüfte Anfrage (Sol-P2-6): der Editor-Aufruf läuft ohne Sperre,
  danach unter Kanalsperre ein bedingtes Update auf `BotActivationRequestedAtUtc = <geprüfter Stempel>` und `BotModeEnabled =
  false`, im selben Commit gebunden an `CredentialVersion` und Bot-ID des Reads (2.3, R2-5); trifft es nichts (abgebrochen, neu
  angefragt, Token rotiert), bleibt alles, wie es ist, und das neue Token prüft im nächsten Takt selbst. Editor-Status `true` → `BotModeEnabled = true`,
  `BotModeActivated* = BotActivationRequested*`, `BotEditorCheckedAtUtc` mit `BotEditorCheckedCredentialVersion`, Anfrage leeren, `BotSuspendedReason = null`, Audit
  `wish.botActivate` mit dem Anfragenden als Akteur **[E]** (die Entscheidung ist seine; der Worker bestätigt nur). `false` →
  Anfrage leeren, `BotActivationError = not_editor`. Global nicht verfügbar → Anfrage bleibt liegen. Deckung wird erneut geprüft.
  **Bestehendes Eigentum entsteht nie** (Moduswechsel überträgt nichts).
- **D7 unter der Kanalsperre neu geprüft [E] (Sol-P2-6):** `DELETE …/wishes/bot` **und** das bestehende `PUT …/wishes/settings`
  prüfen `CanChangeSettings` nach dem Erwerb der Kanalsperre an frisch gelesenen Einstellungen erneut (403
  `wish_settings_restricted`); der Filter bleibt die billige Vorprüfung. So kann ein reiner Mod, der den Filter im manuellen
  Modus passierte, einen inzwischen bestätigten Bot nicht abschalten. Für `PUT /settings` ist das eine Änderung an live
  genutztem K1-Code: Regressionsfall „manueller Kanal, Mod ändert Vorgaben“ unverändert. **Ebenso alle schreibenden
  Belohnungsrouten [B] F6 (R2-6):** Anlernen starten, bestätigen, abbrechen, `PATCH` und `DELETE /rewards/{rewardId}`; die Prüfung
  sitzt einmal in `WishRewardService.LockAndCheckAsync` nach der Kanalsperre (der Dienst bekommt dafür Twitch-ID des Aufrufers und
  das Admin-Flag als Form), neues Ergebnis → 403 `wish_settings_restricted`. Live genutzter K6-Code: Regressionsfall „manueller
  Kanal, Mod ändert und löscht eine Belohnung“ unverändert.
- **`DELETE`:** unter Kanalsperre, dann die Wunsch-Zeilen des Kanals aufsteigend (Muster K2 (d)): `BotModeEnabled = false`,
  `BotModeActivated*`, `BotSuspendedReason`, Anfrage leeren; jedes Eigentum endet (`DecisionVersion + 1`, Einreihung leer,
  `BotSetId` leer, auch bei `presented` mit Auftrag); offene Versuche nach 2.5a (e) ohne 7TV; Audit `wish.botDeactivate`, ein
  `wish.changed`. Erreichbar auch bei ausgeschaltetem Bot-Schalter, solange `Wishes:Enabled` an ist (Rollback-Weg (b), Abschnitt
  6). **Abbruch einer offenen Anfrage [E, F5]:** nicht aktiviert, aber Anfrage liegt → `DELETE` leert sie, erlaubt jedem,
  der D7 besteht (im manuellen Modus also jedem Verwalter, wie jede andere Vorgabe), Audit `wish.botDeactivate` mit
  `pendingOnly: true`. Weder aktiviert noch angefragt → 204 ohne Audit (idempotent).
- **`bot-remove`:** in dieser Reihenfolge: Wunsch fehlt → 404; nicht `presented` oder Grund nicht `protected`, `coverage_gap`,
  `bot_unavailable` → 409 `wish_state_conflict`; nicht `BotOwned` → 409 `wish_not_bot_owned`; nicht `StartsAtAuthoritative` → 409
  `wish_start_unconfirmed` **[E, neuer Code]**; Kanal nicht `bot` → 409 `wish_bot_unavailable`; sonst unter Zeilensperre
  `DecisionVersion + 1`, dann `RemovalQueuedAtUtc = jetzt`, `RemovalQueuedVersion = DecisionVersion` (die Anforderung ist selbst eine
  Entscheidung; die Version danach ist die, gegen die der Bot prüft), Audit `wish.botRemoveRequest` (Akteur Verwalter,
  `previousExpiresAtUtc` nach K2 (e)), `WISHBOT:`. Schon eingereiht oder offener Versuch (2.5a (b)) → 409 `wish_state_conflict`.
- **`recheck` (R2-8):** `WishTransitions`-Zeile (Bestand), Guard 2.5a (b); Kanal nicht `bot` → 409 `wish_bot_unavailable`;
  `BotSetId` leer oder ≠ `ActiveEmoteSetId` (unter der Kanalsperre gelesen) → 409 `wish_bot_set_changed` **[E, neuer Code]**; sonst
  `unconfirmed`, `BotAttemptCount = 0`, `BotNextAttemptAtUtc = jetzt`, `BotConfirmDeadlineAtUtc = jetzt + 16 min`; `BotSetId`,
  `BotWriteAtUtc` (Generationsanker) und `BotAddAckAtUtc` bleiben; Audit `wish.recheck` **[E, neue Konstante]**, `WISHBOT:`.
- **Schwellenänderung und Auftrag [E]:** Eine Änderung der Schutzschwelle (K2 (d)) leert Aufträge an `presented` **nicht**: Der
  Auftrag wurde gegen Schutz oder Lücke erteilt und hängt nicht an der Schwelle.

### 2.10 Admin-Api und -Seite (T4, T12)

- `/api/admin/wish-bot` als eigene Datei mit Gruppen-Erweiterung (Befund 15): `GET /` (Form der Spec; `queue` zählt `approved` in
  Bot-Kanälen, `unconfirmed`, Einreihungen; `botChannels` = aktivierte Kanäle; dazu **[E]** `suspendedChannels` und
  `openRepairChecks`, Zahl offener Prüfphasen, 2.5a (h)), `PUT /token`,
  `DELETE /token` (Siegel, Ablauf, ID, Fehler leeren, `Status = none`; `KillSwitch` bleibt; Audit `wishBot.tokenDelete`),
  `PUT /kill-switch` (Audit `wishBot.killSwitch` mit `enabled`). Mit `Wishes:BotMode:Enabled = false`: alle vier 404
  `wish_bot_disabled` **nach** dem Filter. Keine Route gibt Token oder Siegel zurück (AK74).
- **Seite `admin/wish-bot`** (Spec K4): Zustand, Ablauf mit Hinweis ab 7 Tagen, Eingabe Typ Passwort ohne Vorbelegung, nach dem
  Senden geleert (auch bei Fehler), Löschen mit `typed-confirm-dialog`, Notaus-Schalter mit Bestätigung, Warteschlange, Bot-Kanäle,
  Hinweis bei geänderter Bot-ID (2.8). Eintrag in `admin-layout.ts`. Vitest nur für Zustandslogik (Regel 12): Hinweisgrenze 7 Tage,
  Feld geleert nach Erfolg und Fehler, Sperre des Senden-Knopfs bei leerem Feld.

### 2.11 Audit und Fehlercodes (T4, T7)

- **Audit (Details nie Alias, Name, Login, Token):** neu `wishBot.tokenSet`, `wishBot.tokenDelete`, `wishBot.killSwitch`,
  `wishBot.tokenState`, `wish.botActivate`, `wish.botDeactivate`, `wish.botAdd`, `wish.botRemove`, `wish.botRepair`,
  `wish.botSuspectRemoval`, `wish.botSuspend`, `wish.botRemoveRequest`, `wish.recheck`. Übergänge des Bots ohne eigenen Eintrag
  (`bot_unconfirmed`, `bot_already_in_set`, `bot_reject`, `ownership_lost`, Rückfall) schreiben **keinen** Eintrag **[E]**; sie
  sind am Wunsch sichtbar, und die Pilot-Messung braucht sie nicht. Die Operations-Abfrage der Pilot-Messung (a) zählt
  `wish.botRemove` mit (Spec 13).
- **Codes (Regel 7, je Code `api-error.ts` und beide Locales im selben Task):** die acht der Spec plus `wish_start_unconfirmed` und
  `wish_bot_set_changed` (Revision 2).

### 2.12 Konfiguration und Compose (T1)

`WishOptions.BotMode` (`Enabled`, `WriteDelaySeconds` 0 bis 60, Default 2) und `WishOptions.PilotChannelIds` (Zeichenkette, geparst
zu „alle“ oder einer Menge; leere Einträge ignoriert, ungültige Zeichen → Fail-Fast mit Schlüsselname). Compose (`docker-compose.yml`,
`docker-compose.prod.yml`, `.env.example`): `WISHES_BOT_MODE_ENABLED` und `WISHES_PILOT_CHANNEL_IDS` an **api und worker**,
`WISHES_BOT_TOKEN_PUBLIC_KEY` nur `api`, `WISHES_BOT_TOKEN_PRIVATE_KEY` nur `worker`; **[B] F2:** zusätzlich
`Auth__AdminTwitchUserIds` am `worker` (gleicher Wert wie an der Api). Kommentare englisch, Werte nie im Repo (Regel 17).

---

## 3. Tasks

Je Task ein Subagent mit frischem Kontext, **strikt nacheinander** (geteiltes `obj/`, Locale-Dateien). Jeder Backend-Task endet
mit grünem `dotnet build EmotePurge.slnx` und seinen Tests, jeder Frontend-Task mit `npm --prefix web test -- --watch=false`,
`lint`, `format:check`; jeder mit eigenem Conventional Commit (englisch). **Ein** englischer DECISIONS-Eintrag
„Wish emotes, child 4: bot backend (#487, epic #481)“ über dem K6-Eintrag; T1 legt ihn an, jeder Task mit Vertragsänderung
ergänzt Absatz und `**Betrifft:**`-Zeile im selben Commit (Regel 3). Nie `docker compose` aus dem Worktree. Jeder Task, der
geteilten Wunsch-Code ändert, hat einen Regressionsfall „Bot-Modus aus: unverändert“ (Befund 20).

| Task | Inhalt | Modell | PR |
|---|---|---|---|
| T0 | Frühe Live-Probe AK86 bis AK92 mit Wegwerf-Skript (Abschnitt 5) | Orchestrator mit Betreiber | – |
| T1 | Migration `AddWishBot`, Entität, Optionen, Pilot-Liste, Compose, DECISIONS-Gerüst | sonnet | 1 |
| T2 | Siegel, Öffnen, JWT-`exp`, zwei Registrierungs-Einstiege, Fail-Fast; Doku-Pflicht AK95 | opus | 1 |
| T3 | Schreib-Client mit Fehlerabbildung, ohne Logging, eigene Aufrufquelle | opus | 1 |
| T4 | Admin-Api, `IWishBotService` (Admin-Teil), echte `IWishBotAvailability`, `WISHBOT:`, Codes, Audit | opus | 1 |
| T5 | `WishBotWorker`, Advisory-Lock, Signal, Token-Haushalt | opus | 1 |
| T11a | Token-Vertrauensgrenze: AK74/AK75 für Setzen, Fehleingaben, Prüfen, 401, Löschen (Sol-P2-9) | sonnet | 1 |
| T12 | Web: Admin-Seite, Service, Route, i18n (Admin, Codes, Audit, Aufrufquelle) | sonnet | 1 |
| T6 | Nähte K1/K2: 2.1, [W1], [W2], [W4] bis [W10] | opus | 2 |
| T7 | Kanal-Api: `GET bot`, `activate` samt Filter, `DELETE`, `bot-remove`, `recheck`, `/permissions` | opus | 2 |
| T8 | Runner: Vorprüfung, Kanalzustände, Aktivierung bestätigen, Aussetzen, Rückfall-Lauf (auch im Ablauf-Lauf) | opus | 2 |
| T9 | Runner: Hinzufügen und Bestätigen | opus | 2 |
| T10 | Runner: Entfernen, Reparatur, Erkennungsgrenze, `bot-remove` ausführen | opus | 2 |
| T11b | T11a erweitert um Kanalrouten, Hinzufügen, Entfernen | sonnet | 2 |
| T13 | Doku-Abschluss: Operations, Architectur, CLAUDE.md, DECISIONS | sonnet | 2 |
| T14 | Live-Verifikation AK93 (Abschnitt 5) | Orchestrator mit Betreiber | 2 |
| T15 | Gates (Abschnitt 6) | Orchestrator | 1 und 2 |

Reihenfolge: T0 → **Tor G0** → T1 → T2 → T3 → T4 → T5 → T11a → T12 → T15 (PR 1) → T6 → T7 → T8 → T9 → T10 → T11b → T13 → T14
→ T15 (PR 2). T3 darf erst nach G0 beginnen (Feldnamen, Prüfabfrage, Antwortformen). **[B] F1:** zwei PRs, Merge strikt
nacheinander; PR 2 setzt auf den gemergten PR 1 auf.

**Tor G0 (Orchestrator):** AK86 bestanden → wie geplant. AK86 nicht bestanden → **[B] F3: Variante (a)**, K4 wird zu Ende gebaut;
es entfallen T10 (bis auf den Rückfall) und jedes Eigentum, `WishBotOwnership.Confirm` liefert nie Eigentum, Policy-Zeile 2 legt
jeden Bot-Wunsch vor. AK88 über 5 min → E11 neu (Spec und 2.6 Punkt 5) vor T9. AK89 positiv → Rückgabe ersetzt den ersten Read.
AK92: `editor_of` erkennt Ein- und Austragen nicht binnen einer Minute → T8 nimmt die in T0 gefundene authentifizierte Abfrage.

### T1: Schema, Optionen, Pilot-Liste

- **Lesen:** Spec 6.1, 6.4, 14, E8; 2.2, 2.3, 2.5a (Felder); `AppDbContext.cs` (Muster `WishCaptureState`), `Wish.cs`,
  `ChannelWishSettings.cs`, `WishOptions.cs`,
  `AddWishCaptureMigrationTests.cs`, DECISIONS K6 (a), (d); Compose-Dateien (Befund 16).
- **Liefert:** Entität `WishBotCredential` (Core) mit `CredentialVersion` und `RunnerEpoch`, Konfiguration inline, Migration
  `AddWishBot` (additiv: die Tabelle mit CHECKs, die **sieben** nullbaren Spalten aus 2.5a an `Wishes` und die nullbare
  `BotEditorCheckedCredentialVersion` an `ChannelWishSettings`), `WishOptions.BotMode`, `PilotChannelIds` mit reiner
  Auswertung `WishPilotList` (Core), `Validate()` mit Schlüsselnamen, Compose und `.env.example` (2.12, mit Admin-IDs am Worker),
  DECISIONS-Gerüst.
- **Grenzfälle:** zweites Insert scheitert (CHECK und PK); unbekannter Status scheitert; Liste `""`, `" , "`, `*`, `* ,123`
  (`*` gewinnt **[E]**), `123,abc` → Fail-Fast; `WriteDelaySeconds` −1, 61; ein Insert in `Wishes` und ein Upsert in
  `ChannelWishSettings` ohne die neuen Spalten (Form des laufenden Images) gelingen.
- **Tests:** Schema-Test je Constraint, Migrationstest „additiv, keine Bestandsspalte geändert“ (Muster K6), `WishOptionsTests`
  erweitert, `WishPilotListTests`. **AKs:** AK71. **Commit:** `feat: add wish bot credential schema and switches`

### T2: Siegel, Öffnen, Registrierung, Vertrauensgrenze in der Doku

- **Lesen:** Spec 7 vollständig, E7, AK72, AK95; `LogCaptureRegistration.cs`, `Program.cs:30-60`, `WorkerServiceRegistration.cs`,
  `AesGcmTokenCipher.cs` (nur als Abgrenzung: dieser bleibt unberührt), `docs/Architectur.md:21`, `PRODUCT.md:81`,
  `docs/Operations.md:1842-1890`.
- **Liefert:** 2.2 vollständig; dazu im **selben Commit** (AK95, Regel 3): DECISIONS-Absatz „zero-knowledge narrowed to user
  tokens; one sealed bot token; bot mode is unofficial and not confirmed by 7TV“, `Architectur.md` Grundsatz 4 neu (englisch),
  `PRODUCT.md:81` (deutsch: Nutzer-Token verlässt den Browser nie, genau ein versiegeltes Bot-Token liegt am Server, Zweck,
  Positionierung „Purge plus Wünsche“), `Operations.md` Runbook Teil 1 (Schlüsselpaar mit `openssl` erzeugen und als
  einzeiliges base64-DER setzen, welcher Dienst welchen Schlüssel bekommt, Fail-Fast, Regel „mit dem Bot-Konto nirgends normal
  ausloggen“). `CLAUDE.md:148` („genau zwei“ → vier Einstiege).
- **Grenzfälle:** leeres Token, 64 KB Token (Obergrenze **[E]** 8 KB → malformed), Präfix `v2.`, gekürztes Siegel, Tag gekippt,
  Nonce gekippt, verpackter Schlüssel gekippt, Schlüssel 2048 Bit, öffentlicher Schlüssel als privater; `exp` als Text, negativ,
  fehlend; Token mit vier Segmenten.
- **Tests:** Unit Rundlauf und alle Fälle oben (AK72), `WishBotJwtTests`; `Api.Tests`: Opener und Schreib-Client nicht auflösbar,
  Fail-Fast mit fehlendem Schlüssel (Factory mit Konfiguration), Meldung enthält keinen Schlüsselwert. **AKs:** AK72, AK95.
  **Commit:** `feat: seal the wish bot token for the worker only`

### T3: Schreib-Client

- **Lesen:** 2.4, Protokoll T0, Befund 12 und 13, `SevenTvApiClient.cs` (Seitenlogik `:1311-1365`, 429-Erkennung `:189-197`),
  `ProviderRequestTelemetryHandler.cs`, `IRateLimitTelemetry.cs:119`, Web `seven-tv-import.service.ts:94-120`.
- **Liefert:** `ISevenTvWriteClient` und Implementierung, Registrierung nur in `AddWishBot`, Aufrufquelle `seventv-bot`.
- **Grenzfälle (Stub-Server, Integration):** verkleidetes 429 mit und ohne Reset-Hinweis, HTTP 429 mit `Retry-After` 30 s, 7 h
  (→ 6 h), ohne Header (→ 60 s); 401 mit Nicht-GraphQL-Körper; `LACKING_PRIVILEGES`; `extensions.status = 409`; Timeout; Seite 2
  von 3 fehlt; `page_count` größer als die Kappe; `data: null` neben Fehler; Eintrag ohne `addedAt` oder ohne `addedById`.
  Kein Log enthält Token oder Fehlertext (Logger-Fake).
- **AKs:** AK76 (Client-Teil). **Commit:** `feat: 7tv write client for the wish bot`

### T4: Admin-Api, Verfügbarkeit, Anstoß

- **Lesen:** 2.2, 2.3 (global), 2.10, 2.11; `AdminEndpoints.cs:31-60`, `AdminLogEndpoints.cs`, `GlobalAdminAuthorizationFilter`,
  `AuditLogWrites.cs`, `ApiErrorCodes.cs:229-277`, `api-error.ts`, Locales, `BotCommands.cs`, `Worker.cs:56-112`,
  `ChatLogBackfillSignal.cs`, `ServiceCollectionExtensions.cs:344-364`, `WishExpiryService.cs:209-250`.
- **Liefert:** `IWishBotService` (Core; Admin-Teil: Lesen, Token setzen, löschen, Notaus) und Implementierung, Admin-Routen,
  `IWishBotAvailability` echt (2.3), `BotCommands.WishBotPrefix`, Codes `wish_bot_disabled`, `wish_bot_token_malformed`,
  `wish_bot_token_expired` samt Frontend-Hälfte, Audit-Konstanten aus 2.11 (alle, damit T7 bis T10 nur noch nutzen).
- **Grenzfälle:** zwei gleichzeitige `PUT /token` (letzter gewinnt, zwei Audit-Einträge, `CredentialVersion` um zwei höher);
  `DELETE` ohne Zeile (204, kein Audit **[E]**); Notaus setzen ohne Token-Zeile (Upsert, Version unverändert); Verfügbarkeit bei
  `valid` mit `exp` in der Vergangenheit (nein), bei Pause (ja).
- **Tests:** `Api.Tests` AK73 (alle Zweige), Filtermatrix der Admin-Gruppe für die neuen Routen; Integration `WishBotServiceTests`
  (Admin-Teil), `WishBotAvailabilityTests`; Regression: `WishExpiryService` mit Bot aus unverändert.
- **AKs:** AK73, AK74 (Admin-Teil). **Commit:** `feat: admin routes for the wish bot token`

### T5: Worker-Dienst und Token-Haushalt

- **Lesen:** 2.5 (Dienst, Lock), 2.8 (Token-Haushalt); `ChatLogBackfillWorker.cs`, `ChatLogBackfillService.cs:58-76,660-735`,
  `WishExpiryWorker.cs`, `WorkerServiceRegistration.cs`, `WorkerServiceRegistrationTests.cs`.
- **Liefert:** `WishBotWorker` (Worker), `WishBotSignal`, Registrierung vor `WishCaptureWorker`, `IWishBotRunner` (Core, Gerüst:
  Lock holen, Token-Haushalt, Durchgang), Token-Haushalt vollständig.
- **Liefert außerdem:** die Lock-Schranke **und** die Runner-Epoche aus 2.5a (g) als Baustein (Erhöhung nach dem Lock-Erwerb,
  Epochenprüfung `FOR SHARE` in der laufenden Transaktion), den T9 und T10 nur noch aufrufen; den Fristschritt (2.5, Schritt 0)
  als leeres Gerüst, das T9 und T10 füllen.
- **Grenzfälle:** Schalter aus → eine Logzeile, Opener nie aufgerufen (AK96); zweite Instanz ohne Lock schreibt nichts; `exp` genau
  jetzt; Warnung genau einmal je UTC-Tag über Mitternacht; **Rotation während blockierter Prüfung** (Stub hält `VerifyAsync` von
  Token A an, `PUT` setzt B, A antwortet `Ok` bzw. 401 → B bleibt `unverified`, kein Audit für A, nächster Takt prüft B);
  **Löschen während blockierter Prüfung** (Zeile bleibt `none`) (Sol-P1-3).
- **Tests:** `Worker.Tests` Registrierung (siebzehn, Reihenfolge, Capture letzter), Signal; Integration: zwei Runner, **die
  Lock-Verbindung des ersten wird per `pg_terminate_backend` getrennt, während ein Stub-Read blockiert**; der zweite übernimmt, der
  erste bucht nach dem Read nichts (Sol-P1-4); **Epoche (R2-3):** der erste hält eine Transaktion mit Epochenprüfung offen, der
  zweite erwirbt den Lock und wartet mit der Erhöhung, bis der erste committet; danach scheitert jede Buchung des ersten, auch
  bei intakter Arbeitsverbindung; `PUT /token` lässt `RunnerEpoch` unverändert; Token-Haushalt mit gestubbtem Client. **AKs:** AK96, AK81 (Lock-Teil).
  **Commit:** `feat: wish bot worker with token upkeep`

### T11a: Token-Vertrauensgrenze, Log- und Antwortfreiheit (PR 1) (Sol-P2-9)

- **Lesen:** AK74, AK75, 2.2, 2.4; `Program.cs:300-322` (Bindefehler, Ausnahme-Middleware), `LogCaptureProvider.cs`,
  `LogCaptureRegistration.cs`, die Tests aus T2 bis T5.
- **Liefert:** einen Integrationstest mit Log-Erfassung auf Trace-Stufe in **allen** Kategorien und **persistierter** Erfassung
  (`LogCapture:Enabled = true` gegen Testcontainers-Postgres, Muster `LogCaptureRedactionChainTests`) über: `PUT /token` gültig, abgelaufen,
  ohne `exp`, 9 KB, kaputtes JSON, falscher Typ des Felds (Bindefehler-Pfad), eine erzwungene Ausnahme hinter dem Versiegeln
  (Ausnahme-Middleware), `DELETE /token`; Worker-Seite `VerifyAsync` mit `Ok` und mit 401 über den Stub. Geprüft wird jede formatierte
  Zeile, jede Ausnahme samt Inner, jede strukturierte Eigenschaft und jede gespeicherte Zeile: weder Token noch `Bearer` plus Wert
  noch das Siegel (AK75). Dazu der `Api.Tests`-Fall, der alle Antworten der Admin-Routen nach Wert und Siegelpräfix durchsucht
  (AK74). **AKs:** AK74, AK75 (Token-Pfade). **Commit:** `test: the wish bot token never leaves its seal`

### T12: Web-Admin-Seite

- **Lesen:** 2.10, `web/.claude/CLAUDE.md`, `docs/UI-Designsprache.md` §11, `DESIGN.md`, `features/admin/admin-users-page.ts`
  (Bestätigungsdialog), `admin-layout.ts`, `core/admin/admin.service.ts`, `app.routes.ts:67-110`.
- **Liefert:** Modell und Service-Methoden (mit `HttpTestingController`-Spec, Regel 12), Seite, Route, Navigation, i18n
  `admin.wishBot.*`, `audit.actions.wishBot.*` und `audit.actions.wish.bot*`/`wish.recheck` ([W15]), `seventv-bot` in den Locales.
- **AKs:** stützt AK73/AK74 (Eingabe, keine Anzeige des Werts). **Commit:** `feat(web): admin page for the wish bot`

### T6: Nähte K1/K2

- **Lesen:** 2.1, 2.3, 2.5a (b), 2.9 (D7), Befunde 2 bis 8, [W1], [W2], [W4] bis [W10], [W21]; `WishTransitions.cs`, `WishTransitionsTests`, `WishService.cs`
  (`:264-335`, `:517-697`, `:699-721`, `:723-830`, `:883-947`, `:1202-1301`, `:1453-1511`, `:1685-1697`, `:1725-1828`),
  `WishRewardService.cs:98-410`, `WishEndpoints.cs:560-660`, `WishReconcileService.cs:203-240,447-482,703-735`,
  `WishOverviewService.cs:100-140`, Nachtlauf-Notizen T1/T5/T7.
- **Liefert:** `bot_confirm` mit `kept`-Zweig; Zurücksetzen bei `re-add`/`retry`; Abgleich-Ausnahme für bot-bestätigte Wünsche
  (`active`/`kept`, `LinkOrigin = bot`, `SlotId` leer: nur Slot gleicher Emote-ID **und** gleichen Alias, ordinal, aus dem Fenster
  ab `ApprovedAtUtc`); `WishBotChannelState` (rein) und seine Verwendung in `mode`, `StatusesOf`, Übersicht; `CanChangeSettings` auf
  `BotModeEnabled`; `botWritePending` nur in `approved`/`unconfirmed`; die Matrix in `WishTransitionsTests` erweitert; **offener
  Versuch sperrt Entscheidungen** (2.5a (b): `WishBotAttemptGuard` an **allen** dort genannten Pfaden, Supersede überspringt,
  Ausnahme im Abgleich für Schritt 3 und 4) (R2-1); **D7 unter der Kanalsperre** in `UpdateSettingsAsync` **und**
  `WishRewardService.LockAndCheckAsync` (2.9, R2-6); `fallback` bei Editor-Prüfung älter als 15 min **oder** zu einer anderen
  `CredentialVersion` (2.3, R2-5).
- **Grenzfälle:** dauerhafter `re-add` im manuellen Kanal unverändert (`link` → `kept`); bot-bestätigter `active` mit zwei
  Kandidaten gleicher ID, einer mit falschem Alias → nur der richtige; mit vorläufigem Slot → `StartsAtAuthoritative` falsch, nach
  REST-Sync wahr, Start und `ExpiresAtUtc` unverändert; ausgesetzter Kanal: Mod ohne Aktivierung → 403 auf `PUT /settings`;
  `PUT /settings` eines Mods, der den Filter im manuellen Modus passiert, während der Kanal unter Sperre aktiviert wird → 403;
  jeder Befehl aus 2.5a (b) **an seiner echten Route** auf `approved` mit `BotWriteAtUtc` und auf einen Wunsch mit `BotRemoveAtUtc`
  → 409, Zeile samt `DecisionVersion` unverändert, ausdrücklich `adopt` mit Wunsch, `complete-removal`, `unlink` und `approve`
  eines zweiten Wunsches, dessen laufender Zielwunsch einen offenen Entfernungsversuch hat (keine Verlängerung); Schwellenänderung
  mit einem offenen Versuch → dieser Wunsch unverändert, die übrigen wie bisher; Abgleich mit archiviertem Slot und
  `BotRemoveAtUtc` → kein `removed_externally`; offene Prüfphase sperrt keinen Befehl; **Belohnung:** reiner Mod passiert den
  Filter für `PATCH /rewards/{id}`, der Kanal wird unter Sperre aktiviert → 403, Belohnung unverändert; Editor-Prüfung zu Version
  V, danach `PUT /token` (V+1) → Kanal `fallback`, bis V+1 geprüft ist.
- **Tests:** Unit Matrix, `WishBotChannelStateTests`, `WishBotAttemptGuardTests`; Integration Service und Abgleich; Regression
  manueller Modus (Listen, Zähler, `mine`, `re-add`, `retry`, Belohnungsrouten, Schwellenänderung) unverändert. **AKs:** AK77 (Teile „wieder hinzufügen“, „Moduswechsel“), AK79 (Zähler-Teil).
  **Commit:** `feat: prepare wishes for the bot flow`

### T7: Kanal-Api

- **Lesen:** 2.3, 2.5a (b), (e), 2.9, E8, E24, AK82, AK84, AK85; `WishEndpoints.cs`, `WishSettingsAuthorizationFilter.cs`,
  `ChannelManagementAuthorizationFilter`, `IChannelAccessService.cs:30-45`, `ISevenTvEditorService.cs`, `ChannelEndpoints.cs:98-131`,
  `AuthFilterMatrixTests.cs`, `WishService.cs:1259-1300` (Sperrmuster).
- **Liefert:** Routen und Filter nach 2.9, `IWishBotService` (Kanal-Teil), `wishBotModeAvailable`, Codes
  `wish_bot_unavailable`, `wish_bot_channel_not_listed`, `wish_bot_activation_forbidden`, `wish_bot_already_active`,
  `wish_not_bot_owned`, `wish_start_unconfirmed`, `wish_bot_set_changed` samt Frontend-Hälfte.
- **Grenzfälle:** wie 2.9; dazu `activate` bei gelöschtem Aktivierenden-Konto (Pseudonym) und anschließend nicht gelistet;
  `DELETE` während der Runner eine Wunsch-Zeile hält (wartet, danach Zustand korrekt); `bot-remove` zweimal (409); **beide
  Reihenfolgen** „Mod passiert Filter, Worker bestätigt die Aktivierung, `DELETE` erwirbt die Sperre“ (403) und „`DELETE` bricht die
  Anfrage ab, Worker bestätigt danach“ (bedingtes Update trifft nichts, Modus bleibt aus) (Sol-P2-6); `DELETE` mit offenem
  Entfernungsversuch (2.5a (e), Beleg bleibt als Prüfphase); **`recheck` (R2-8):** gleiches Set → `unconfirmed`, neue Frist,
  `BotSetId` und `BotWriteAtUtc` unverändert, ein danach gefundener Eintrag aus der ursprünglichen Generation wird bestätigt;
  Set gewechselt oder `BotSetId` leer → 409 `wish_bot_set_changed`, Zeile unverändert.
- **Tests:** `Api.Tests` AK82 vollständig an der echten Route, AK85 Aktivierungsteil, Filterfolge 400/401/403/404/409/503,
  `bot-remove` AK84 Routenteil; Integration `DELETE` (Eigentum endet, Einreihungen leer, AK83 Teil „Abschalten“).
  **AKs:** AK82, AK84 (Route), AK85 (Aktivierung). **Commit:** `feat: channel routes for the wish bot mode`

### T8: Runner I: Vorprüfung, Kanalzustände, Aktivierung, Rückfall

- **Lesen:** 2.3, 2.5, 2.5a (c), (e), 2.8, 2.9 (Bestätigung), Spec 7.8, E8, AK79, AK80, AK85; `WishExpiryService.cs:60-115` (Schritt 0),
  `ISevenTvApiClient.GetEditorOfChannelsAsync`, `IGlobalAdminAllowlist`.
- **Liefert:** `WishBotPreflight` (rein), Kanalzustände mit Aussetzen und Aufnehmen, Aktivierung bestätigen,
  `IWishBotFallback` und sein Aufruf im Runner und als Schritt 0 im Ablauf-Lauf; Editor-Ergebnisse gebunden an Version und Bot-ID
  (2.3).
- **Grenzfälle:** Kanal fällt aus der Liste mit `approved`, `unconfirmed`, eingereihtem `active`, `presented` mit Auftrag; wieder
  gelistet → nur `approved` läuft weiter; Editor-Auskunft `Unavailable` → kein Aussetzen; global aus während Aktivierungsanfrage;
  Admin-Aktivierung außerhalb der Liste, Admin wird entfernt ([B] F2); Rückfall zweimal hintereinander ändert nichts; Editor-Auskunft
  **dauerhaft** `Unavailable` (Stub) → nach 15 min `fallback`, `approved` zählt als Handlungsbedarf, nach der ersten gelungenen
  Prüfung wieder `bot` (Sol-P2-7); Credential-Version wechselt zwischen Vorprüfung und Senden → nicht senden; **Rotation während
  der Aktivierung (R2-5):** Stub hält den positiven Editor-Read für Token A an, `PUT /token` setzt B (Bot-ID B ohne Editor-Recht),
  A antwortet `true` → Anfrage bleibt offen, `BotModeEnabled` falsch, kein Audit; B wird `valid` → Kanal nicht `bot`, bis Bs
  eigener Read; derselbe Fall mit negativem Read für A → keine Aussetzung.
- **Tests:** Unit Preflight-Reihenfolge (jede Stufe einzeln falsch), Integration AK79, AK80, AK85 Betrieb, Ablauf-Lauf mit
  ausgeschaltetem Bot leert eingereihte Wünsche und löst offene Versuche auf ([W8], 2.5a (e)), Beleg bleibt als Prüfphase. **AKs:** AK79, AK80, AK81 (Notaus), AK85.
  **Commit:** `feat: wish bot channel states and fallback`

### T9: Runner II: Hinzufügen und Bestätigen

- **Lesen:** 2.5a vollständig, 2.6, 2.1, Spec 8, E11, AK77, AK83, Protokoll T0 (AK86, AK88, AK89); `WishService.cs:1470-1511`.
- **Liefert:** Hinzufügen nach 2.5a, Absturzrest, `WishBotConfirmSchedule` (rein), `WishBotOwnership.Confirm` (rein, mit
  Sende-Nachweis als Eingang), Bestätigen, den Bestätigungsteil des Fristschritts.
- **Grenzfälle:** alle aus 2.6; **zwischen Absicht und Senden** je ein Fall für `PATCH` des Alias, `close`, `adopt`, `DELETE
  …/wishes/bot` und Notaus (die ersten drei: 409; `DELETE`: kein Senden, `presented` (`unconfirmed`); Notaus: kein Senden) und ein
  `PATCH` **vor** der Absicht (der Bot schreibt den neuen Alias) (Sol-P1-1, P3); **Set-Wechsel** nach dem Schreiben, wobei das neue
  aktive Set schon einen Bot-Eintrag mit gleicher ID und Alias hat → kein Eigentum, nach der Frist `presented` (`unconfirmed`)
  (Sol-P1-2); Eintrag älter als `BotWriteAtUtc − 2 min` → nicht bestätigt; **Reads scheitern dauerhaft** → nach 15 min
  `presented` (`unconfirmed`) (Sol-P2-7); Lock-Verlust zwischen `addEmote` und Buchung → keine Buchung, der nächste Inhaber setzt
  `unconfirmed` ohne zweites `addEmote` und bestätigt höchstens ohne Eigentum; `addedById` fehlt; zwei Einträge gleicher ID, einer
  mit dem Alias; Eintrag ohne `addedAt`. **Revision 2:** **zwei Kanäle, ein Set, Absturz vor dem Vor-Read (R2-2):** A schreibt die
  Absicht fest und stürzt ab; Bs Absicht für dieselbe Emote-ID wird nicht festgeschrieben, solange As Versuch offen ist; liegt Bs
  Bot-Eintrag schon vorher im Fenster, bestätigt A ihn nur ohne Eigentum, und As Ablauf legt vor, statt Bs Eintrag zu entfernen;
  **Frist ohne ausführbaren Read (R2-7):** Editor-Auskunft `Unavailable`, Token `expired`, 429-Pause von 6 h und erschöpftes
  Budget je einzeln → Uhr über `BotConfirmDeadlineAtUtc`, der Fristschritt setzt `presented` (`unconfirmed`) ohne einen Read;
  **Set-Wechsel gegen Senden (R2-3):** der Vollsync will `ActiveEmoteSetId` wechseln, während die Sende-Transaktion den Kanal hält
  → er wartet und committet danach, gesendet wurde ins alte, noch aktive Set; Set-Wechsel vor der Nachprüfung → nicht senden;
  **Epoche:** ein zweiter Runner erhöht die Epoche zwischen Vor-Read und Nachprüfung → kein Senden, Absicht bleibt für (e).
- **Tests:** Unit Plan und Eigentumsentscheidung; Integration mit gestubbtem `ISevenTvWriteClient`: AK77 (Bestätigungsfälle),
  AK83 (Abgleich vor und nach der Absicht, Absturz zwischen `addEmote` und `unconfirmed`). **AKs:** AK77 (Teil), AK83 (Teil).
  **Commit:** `feat: wish bot adds and confirms`

### T10: Runner III: Entfernen

- **Lesen:** 2.5a vollständig, 2.7, Spec 8 vollständig, E25, RR2, AK77, AK78, AK83, AK84; `RowLocks.cs`, Plan-484 Befund 11.
- **Liefert:** Entfernen samt Prüfung unter Sperre, Reparatur, Erkennungsgrenze, Ausführung von `bot-remove`.
- **Grenzfälle:** Abweichung je eines der vier Merkmale; Eintrag schon weg; zweiter Alias desselben Emotes bleibt; Reparatur
  scheitert; Ersatz zwischen Vor-Read und `removeEmote` (Stub) mit und ohne vorher gesehenen Slot; `extend` nach Auswahl und vor
  Sperre (Version), `extend` gegen gehaltene Sperre (wartet, 409), `extend` zwischen den beiden Transaktionen (sofort 409);
  `keep` und Abschalten nach Einreihen; **`PATCH` auf einen eingereihten Wunsch an der echten Route → 409, Einreihung bleibt**
  ([W19]); `ActiveEmoteSetId ≠ BotSetId` → kein Senden, `ownership_lost`; **Wiederaufnahme** (2.5a (e)): Timeout und gescheiterter
  Nach-Read, danach fehlt der Zieleintrag und ein zweiter Alias → `removed` mit `outcome: resumed`, Prüfphase nur lesend, **kein**
  `addEmote`, `wish.botRepair` `needs_review`; Zieleintrag noch da → Versuch leer, Einreihung bleibt; 15 min ohne Read → Fristschritt
  `presented` (`bot_unavailable`), Beleg bleibt (Sol-P2-5). **Revision 2 (R2-4):** Reparatur gesendet, `Timeout`, Absturz vor der
  Buchung, der nächste Read zeigt den Alias (verzögert) noch nicht → kein zweites `addEmote`, `needs_review`; zeigt er ihn →
  Phase leer; Notaus und Fristablauf mit offener Prüfphase → Beleg bleibt, nach Aufheben des Notaus wird nur gelesen; neuer
  `bot-remove` desselben Wunsches mit offener Prüfphase → wird erst nach deren Auflösung festgeschrieben; Vollsync wartet während
  Transaktion 2 (Stub hält 9 s) und committet danach ohne Timeout.
- **Tests:** Integration AK77 Rest, AK78, AK83 Rest, AK84 Ausführung, „genau ein `wish.due`“ nach geleerter Einreihung.
  **AKs:** AK77, AK78, AK83, AK84. **Commit:** `feat: wish bot removes only what it owns`

### T11b: Log- und Antwortfreiheit, PR 2

- **Liefert:** dieselben zwei Tests aus T11a, erweitert um Hinzufügen, Bestätigen, Entfernen, Reparatur und 401 mitten im Durchgang
  sowie um alle Antworten der Kanal-Bot-Routen. **AKs:** AK74, AK75 (Rest). **Commit:** `test: the wish bot flows log no token`

### T13: Doku-Abschluss

- **Liefert:** `Operations.md` Runbook Teil 2 (Token setzen und erneuern, Aktivieren, Notaus in der Reihenfolge Spec 14, Rückfall,
  Rollback in zwei Wegen samt Prüfabfrage (Abschnitt 6), Pilot-Liste, Schwelle „rechnet eher zu hoch“, Messabfrage mit
  `wish.botRemove`, „extern entfernt“ nur nach einer ohne 7TV aufgelösten Entfernung (2.5a (e)), Prüfphasen und
  `needs_review` samt Abfrage des Belegs (2.5a (h)), Restfenster R11);
  `Architectur.md:86` siebzehn Dienste; `CLAUDE.md`: Worker-Liste siebzehn mit `WishBotWorker`, **Zeile „Wunsch-Emotes“ korrigiert**
  (K1 bis K3 und K6 gemergt und seit 2026-11-20 auf Prod live und eingeschaltet, K4 auf Branch, K5 offen); DECISIONS vollständig
  inklusive Probe-Ergebnisse T0 (eigener Absatz, „unofficial, not confirmed by 7TV“) und [E]-Liste. **AKs:** AK95 (Rest).
  **Commit:** `docs: wish bot backend (child 4)`

---

## 4. AK-Zuordnung

| AK | Task | AK | Task |
|---|---|---|---|
| AK71 Migration, eine Zeile | T1 | AK84 `bot-remove` | T7, T10 |
| AK72 Siegel, Opener nicht in Api | T2 | AK85 Pilot-Liste | T7, T8 |
| AK73 `PUT /token` | T4 | AK86 `addedById` | T0 |
| AK74 keine Antwort mit Token | T4, T11a, T11b | AK87 Umbenennen | T0 |
| AK75 kein Log mit Token | T3, T11a, T11b | AK88 Sofort-Read | T0 (G0) |
| AK76 Client-Fehlerformen | T3, T8 | AK89 Rückgabe `addEmote` | T0 (G0) |
| AK77 Eigentum, Erkennungsgrenze | T6, T9, T10 | AK90 Notaus live | T0 |
| AK78 F12 `not_bot_owned` | T10 | AK91 Verschränkung, Fehlerformen | T0, T3 |
| AK79 Rückfall global | T6, T8 | AK92 Editor-Lesen | T0, T8 |
| AK80 Rückfall je Kanal | T8 | AK93 Gesamtweg lokal | T14 |
| AK81 Notaus, Lock | T5, T8 | AK94 Pilot | nach Freigabe 2, kein Tor |
| AK82 Aktivieren prüft Aufrufer | T7 | AK95 Doku | T2, T13 |
| AK83 Bot gegen Abgleich, E25 | T6, T7, T9, T10 | AK96 siebzehn Dienste | T5 |

AK76 Teil „401 → alle Bot-Kanäle im Rückfall“ prüft T8 über den Runner, nicht den Client.

---

## 5. Live-Proben und Live-Verifikation (T0, T14; Regel 16)

### Betreiber-Vorbereitung vor T0

| # | Was | Wofür |
|---|---|---|
| V1 | 7TV-Konto für das Bot-Konto (Login über dessen Twitch-Konto) in einem **eigenen Browserprofil**; dort nie normal ausloggen | Token, AK90 |
| V2 | Bot-JWT aus der 7TV-Website dieses Profils in eine Datei **außerhalb des Repos** (Rechte 600), Pfad dem Orchestrator nennen, nie in den Chat | T0, später T14 |
| V3 | Testkanal `olaf_olaf_son`: Zugang als Inhaber in einem zweiten Profil; Bot-Konto **noch nicht** als Editor eintragen (AK92 misst das Eintragen live); Stand des Sets notieren | AK87, AK91, AK92, Wiederherstellen |
| V4 | Drei Test-Emotes, die nicht im Set liegen (Links), eines mit einem im Set schon vergebenen Namen | AK86, AK88, AK91 |
| V5 | Ein drittes Browserprofil oder Gerät für den zweiten Login des Bot-Kontos | AK90 Teil 2 |
| V6 | Das Schlüsselpaar für **Prod** erst vor Freigabe 2; lokal erzeugt der Implementer in T2 ein Paar in die User-Secrets bzw. die lokale `.env` | – |

### T0: Probe (Wegwerf-Skript im Scratchpad, nichts im Repo)

Ein kleines Skript liest das Token aus V2 und gibt es nie aus. Reihenfolge (AK90 zerstört das Token, daher zuletzt):
P1 Prüfabfrage und eigene 7TV-ID, JWT-Felder (`exp`, ID-Claim), Gültigkeit 30 Tage bestätigen; P2 Eintragen des Bots als Editor
durch den Betreiber, Editor-Lesen per `editor_of` (und authentifiziert, falls vorhanden) bis zum Erscheinen, Einladung ja/nein,
Berechtigungsfelder (AK92 Teil 1); P3 `addEmote` mit angeforderter Rückgabe (AK89), danach Reads bis `addedAt` sichtbar, fünfmal (AK88),
`addedById` = Bot-ID (AK86); P4 Umbenennen durch den Betreiber, Read (AK87); P5 Verschränkung (AK91): Bot fügt hinzu, Betreiber
entfernt und fügt gleichen Alias hinzu, Read zeigt fremdes `addedById`; `removeEmote` mit ID und Alias entfernt den
Menschen-Eintrag (Erkennungsgrenze bestätigt); P6 Alias-Konflikt und, falls herstellbar, volles Set, Antwortformen; P7 Austragen
des Bots, Editor-Lesen binnen einer Minute (AK92 Teil 2), Schreibversuch → Antwortform; P8 zweiter Login (V5), dort normal
ausloggen, Token noch gültig? (AK90 Teil 2); P9 „Sign Out Everywhere“, Schreibversuch → 401 (AK90). Danach Set wiederherstellen,
Betreiber holt ein neues Token (V2). Protokoll `docs/nachtlaeufe/live-487-probe-<Datum>.md`: je Probe Erwartung, Befund, Folge
für G0. Fehlerformen ohne Körpertexte mit Personenbezug.

### T14: AK93 lokal

Lokaler Stack mit Kopie der Dev-Datenbank, Schalter an, `WISHES_PILOT_CHANNEL_IDS` = Twitch-ID von `olaf_olaf_son`, Schlüsselpaar
aus T2. Schritte, je eine Anweisung an den Betreiber (Speicher „Eine Anweisung je Schritt“): Token über die Admin-Seite setzen
→ `valid`; Bot als Editor eintragen (Betreiber), `activate` per `curl` als Broadcaster → nach dem Takt `enabled`; Handeintrag →
Bot fügt hinzu → `active`, `botOwned`; Ablauf per SQL in die Vergangenheit → nächster Ablauf-Lauf reiht ein, nächster Bot-Takt
entfernt, Wunsch `removed`, Audit `wish.due` und `wish.botRemove`; Gegenprobe Verlängern nach dem Einreihen → kein Entfernen;
`bot-remove` auf `presented` (`protected`, Schwelle 0,01); Notaus während der Warteschlange; Bot austragen → `not_editor` binnen
eines Schreibversuchs; `DELETE …/wishes/bot`. **Rückweg-Probe (Sol-P2-8):** Bot wieder aktivieren, ein Wunsch eingereiht, einer
`unconfirmed`, einer dauerhaft mit Bot-Bestätigung (`kept`), dessen Slot noch nicht sichtbar ist (Vollsync des Kanals vor der
Bestätigung angehalten, R2-9): Prüfabfrage zeigt ihn; nach Admin-Resync = 0; dann Rollback-Weg (b) aus Abschnitt 6 bis zur
Prüfabfrage = 0, Api und Worker als Image von `801e64a6` mit
Erfassung an: Übersicht zeigt `approved` als Handlungsbedarf, Ablauf-Lauf und Erfassung ohne Fehler; danach zurück auf K4. Set
danach wiederherstellen. Protokoll `docs/nachtlaeufe/live-487-<Datum>.md`. Was live nicht herstellbar ist (Erkennungsgrenze,
Absturzrest, Wiederaufnahme, zwei Instanzen, Lock-Verlust), steht dort als „nur getestet“.

---

## 6. Gates (T15), Migration, Deploy

1. `dotnet build EmotePurge.slnx --no-incremental -warnaserror`, `dotnet format EmotePurge.slnx --verify-no-changes`,
   `dotnet test EmotePurge.slnx` grün (Docker läuft).
2. `npm --prefix web test -- --watch=false`, `npm --prefix web run lint`, `npm --prefix web run format:check` grün.
3. `npm --prefix web run e2e` grün, nur ohne Api auf `:5151` (PR 1 ändert UI; PR 2 nur, wenn T6 Locales berührt hat).
4. `node scripts/coverage-local.mjs` je PR (die chirurgischen Änderungen in `WishService.cs`, `WishReconcileService.cs`,
   `WishExpiryService.cs`, `WishOverviewService.cs` sind der unscharfe Teil).
5. Live: PR 1 mit T0 und einem Kurzlauf (Token setzen, `valid`, Notaus, Löschen, Fail-Fast ohne Schlüssel); PR 2 mit T14. Ein
   offener Schritt ist ein offenes Gate.
6. Orchestrator: `/codex:adversarial-review --model gpt-6.2-sol` über diesen Plan vor T1 (zwei Runden durch, Revision 2 ist der
   Endstand, keine dritte); `/codex:review --model gpt-6.2-sol` je
   Branch; Opus-Review; bei Widerspruch Fable. Push, PR; der Merge gehört dem Betreiber.
7. PR-Text (englisch): Migration `AddWishBot` von Hand vor den Images (PR 1); neue Variablen und welcher Dienst welchen Schlüssel
   bekommt; Bot-Modus bleibt aus; siebzehnter Dienst; [E]-Abweichungen; Probe-Ergebnisse; „nur getestet“.

**Migration:** `AddWishBot`, eine neue Tabelle, sieben nullbare Spalten an `Wishes` (2.5a) und eine an `ChannelWishSettings`
(2.3), rein additiv; Prod hat `AddWishes` und
`AddWishCapture` schon, `list` zeigt genau eine. Das laufende Image liest weder Tabelle noch Spalten und legt Wünsche ohne sie an
(T1-Test). **Deploy:** nicht Teil dieses Plans; Images dürfen mit `WISHES_BOT_MODE_ENABLED` aus ausgeliefert werden (dann kein
Schlüssel nötig).

**Rollback, zwei getrennte Wege (Sol-P2-8, T13 schreibt beide ins Runbook):**

- **(a) Schalter-Rollback, K4-Images bleiben:** Notaus auf der Admin-Seite, dann `WISHES_BOT_MODE_ENABLED=false` an Worker **und**
  Api. Der Ablauf-Lauf (Schritt 0, 2.8) löst offene Versuche auf und legt `unconfirmed` und Einreihungen vor; die Api rechnet jeden
  aktivierten Kanal als `fallback`, `approved` ist wieder Handlungsbedarf. `BotModeEnabled` bleibt gespeichert, Wiedereinschalten
  setzt fort. Genügt für jeden Fehler im Bot-Weg.
- **(b) Image-Downgrade auf ein Image vor K4:** Das alte Image leitet `mode = bot` aus `BotModeEnabled` ab
  (`WishService.cs:1823`) und überspringt Eingereihte (`WishExpiryService.cs:77`); die nach einer Aktivierung geschriebenen
  Zustände sind mit ihm nicht verträglich. Reihenfolge: (1) Weg (a), einen Ablauf-Lauf abwarten; (2) je aktiviertem Kanal
  `DELETE …/wishes/bot` (bleibt bei ausgeschaltetem Bot-Schalter erreichbar, solange `Wishes:Enabled` an ist **[E]**; es beendet
  nur); (3) **Prüfabfrage** (Operations): Kanäle mit `BotModeEnabled` oder offener Anfrage, Wünsche in `unconfirmed`, in `approved`
  mit `BotWriteAtUtc`, mit `RemovalQueuedAtUtc`, `BotRemoveAtUtc`, `BotRepairAtUtc` oder `BotOwned`, **und `kept` mit `LinkOrigin =
  bot` ohne `SlotId` (R2-9)**; erwartet überall 0, sonst kein Downgrade. Ein `kept`-Treffer heißt: der Abgleich hat den Slot noch
  nicht verknüpft; Admin-Resync des Kanals, Abfrage wiederholen; bleibt er, prüft der Betreiber das Set von Hand (Runbook). Eine
  offene Prüfphase klärt er mit der Beleg-Abfrage aus dem Runbook und leert sie danach per SQL. (4) Images von Api und Worker
  tauschen. Tabelle und Spalten bleiben liegen.

---

## 7. Widersprüche und Lücken zwischen Spec und Code

| # | Fundstelle | Befund | Vorschlag |
|---|---|---|---|
| W1 | Spec 5 „Handlungsbedarf … wenn der Bot ausgefallen ist“, AK79 gegen `WishService.cs:1685-1697`, `WishOverviewService.cs:135` | nur der Kanal zählt, nicht die globale Verfügbarkeit; AK79 wäre unerfüllbar | effektiver Kanalzustand (2.3), T6 |
| W2 | Spec E17 (D7 für Vorgaben im Bot-Modus) gegen `CanChangeSettings` (`:1732-1741`) | ausgesetzter Kanal gilt als manuell, jeder Mod ändert Schwelle und dürfte abschalten | an `BotModeEnabled` binden, T6 |
| W3 | Spec E8 (Admin zum Zeitpunkt des Schreibens) gegen `docker-compose.prod.yml:143-210` | Worker kennt die Admin-IDs nicht | **[B] F2:** `Auth__AdminTwitchUserIds` auch am Worker; Prüfung nur per ID |
| W4 | Spec K4 „Absicht nur bei `BotWriteAtUtc IS NULL`“, Absturzrest-Regel gegen `ReAddAsync`/`RetryAsync` | Bot fasst einen erneut hinzugefügten Wunsch nie an bzw. bestätigt ins Leere | 2.1 Punkt 2, T6 |
| W5 | Spec 5 `presented` → `closed` „nur ohne Slot“, Abgleich Schritt 3 (`:203`, `:319`) | alte `SlotId` aus der Vorrunde macht `close` unmöglich und führt `presented` über den archivierten Slot nach `removed_externally`; der Laufend-Index kann kollidieren | Absicht leert `SlotId` und `LinkOrigin` (2.6 Schritt 1) |
| W6 | Spec 5/6.3 `Permanent` → `kept` gegen `WishTransitions.cs:181` | `bot_confirm` kennt nur `active` | 2.1 Punkt 1 |
| W7 | Spec K4 „Slot gleicher Emote-ID **und** gleichen Alias“ gegen `WishReconcileService.cs:472-482` | `Match` verknüpft einen einzigen Kandidaten auch mit anderem Alias | für bot-bestätigte Wünsche Alias-Gleichheit verlangen, T6 |
| W8 | Spec 7.8 „unconfirmed wird sofort presented“, AK96 (Worker endet bei Schalter aus) gegen `WishExpiryService.cs:77` | bei ausgeschaltetem Bot blieben `unconfirmed` und eingereihte Wünsche liegen; der Ablauf-Lauf überspringt Eingereihte | Rückfall-Lauf auch im Ablauf-Lauf (2.8), T8 |
| W9 | Spec K1 `botWritePending` „bis `active`“ gegen `:1781` | wahr für `kept`, `failed`, Endzustände | nur `approved`/`unconfirmed`, T6 |
| W10 | Spec 5 `recheck` gegen K1-Platzhalter, `AuditLogEntry.cs` | Route antwortet immer 409, keine Audit-Konstante | 2.9, neue Konstante `wish.recheck` |
| W11 | Spec 5 `failed` mit Grund `not_editor` gegen Spec K4/AK80 (Aussetzen) | kein Erzeuger für `failed`/`not_editor` | Konstante bleibt, K4 setzt sie nie; K5 übersetzt sie trotzdem (AK104) |
| W12 | AK85 „Liste leer → Broadcaster **und Mod** 403 `not_listed`“ gegen E24-Filterfolge | Reihenfolge Rolle/Liste offen | Liste vor Rollenprüfung (2.9), ein reiner Mod im ungelisteten Kanal bekommt `not_listed` |
| W13 | Spec K4 `bot-remove` „Code legt K4 fest“ | – | `wish_start_unconfirmed` (neunter Code); `wish_bot_set_changed` für `recheck` (zehnter, R2-8) |
| W14 | Spec K4 Testplan „Worker.Tests: Vorprüfung, Plan, Pause“ | die Logik liegt rein in Core, wie `WishExpiryPolicy` | Tests in `Infrastructure.Tests/Unit`; `Worker.Tests` nur Registrierung und Signal |
| W15 | Spec K5 i18n `audit.actions.wish.bot*` | K4 schreibt die Einträge, die Admin-Audit-Seite zeigte Rohschlüssel | Übersetzungen in K4 (T12) |
| W16 | `CLAUDE.md:25` | nennt K3/K6 als ungemergt, Prod aus | T13 korrigiert |
| W17 | `CLAUDE.md:148` „genau zwei Einstiege“ | K4 bringt zwei dazu | T2 |
| W18 | Spec 6.4 ohne Aussage zur Seed-Zeile | – | keine Seed-Zeile (2.2) |
| W19 | AK83 „Dasselbe für … `PATCH` der Laufzeit“ gegen `WishTransitions.cs:156-160` (Sol-P3) | eine Einreihung gibt es nur in `active`/`presented`, `PATCH` nur in `pending`/`approved`/`failed`: der Fall ist unerreichbar | **[P]**, Spec-Text: „`PATCH` auf einen eingereihten Wunsch → 409, die Einreihung bleibt; das `PATCH`-Rennen gilt in `approved` zwischen Absicht und Senden“; T9, T10 |
| W20 | Spec 6.1, 6.2 und 6.4 gegen Sol-P1-2, P1-3, P2-5, R2-2 bis R2-5, R2-7 | kein Set-Bezug, kein Versuchsbeleg, keine Token-Version, kein Sende-Nachweis, keine Fencing-Epoche | **[E]** sieben Spalten an `Wishes`, eine an `ChannelWishSettings`, `CredentialVersion` und `RunnerEpoch` (2.2, 2.3, 2.5a); Spec 6.1/6.2/6.4 bei Gelegenheit nachtragen |
| W21 | Spec 8 „Sperre hält bis zum Ergebnis“, K1-Matrix (`edit`, `close`, `adopt` aus `approved`) gegen Sol-P1-1 | zwischen Absicht und Senden darf ein Verwalter den Wunsch ändern | **[E]** offener Versuch → 409 (2.5a (b)); Spec 8 um „zwischen Festschreiben und Senden antwortet jede Entscheidung 409“ ergänzen |
| W22 | Spec E11 und 7.8 gegen Sol-P2-7 | keine Aussage zu gescheiterten Reads und zu dauerhaft unbekanntem Editor-Status | E11 als absolute Frist **[P]** (2.6 Punkt 5); unbekannter Editor-Status nach 15 min `fallback` (2.3, [F4]) |
| W23 | Spec 14 „Schalter aus genügt in jedem Fall; die Tabellen stören kein älteres Image“ gegen `WishService.cs:1823`, `WishExpiryService.cs:77` (Sol-P2-8) | nach einer Aktivierung stimmt das für ein älteres Image nicht | zwei Rollback-Wege (Abschnitt 6); Spec 14 entsprechend präzisieren |
| W24 | Spec 8 (Eigentum über `addedById`, Reparatur „genau ein `addEmote`“) und 5 (`recheck`) gegen R2-2, R2-4, R2-8 | `addedById` allein unterscheidet zwei Kanäle auf einem Set nicht; eine Reparatur nach unklarem Ergebnis kann doppelt senden; `recheck` hat kein Set | **[B] F6:** Eigentum nur mit Sende-Nachweis, Reparatur nur im selben Durchgang, sonst lesend, `recheck` behält Set und Anker (2.5a (d), (h), 2.9); Spec 8 und 5 präzisieren |

---

## 8. Risiken

| # | Risiko | Umgang |
|---|---|---|
| R1 | 7TV ändert JWT, Schema oder Editor-Modell (RR3) | Rückfall ist Normalpfad; fail-closed im Client; DECISIONS „unofficial“ |
| R2 | Erkennungsgrenze (RR2): Ersatz zwischen Vor-Read und `removeEmote` wird entfernt | wie Spec 8; Pilotmessung ist untere Schranke |
| R3 | Bot hält Wunsch-Sperre und Kanalzeile (`FOR SHARE`) bis ~20 s über HTTP; Vollsync, Abgleich und Kanalbefehle warten | höchstens zwei Aufrufe je Transaktion, eine Aktion zur Zeit, 10-s-Timeouts, unter dem 30-s-Standard-Timeout der Wartenden (2.5a (c)); im Live-Lauf gemessen |
| R4 | Token gestohlen (RR4) | Siegel, Notaus, Editor je Kanal; Runbook |
| R5 | Änderungen an geteiltem Wunsch-Code treffen den live laufenden manuellen Modus | Regressionsfälle je Task (Befund 20), Bot-Schalter bleibt aus |
| R6 | Eigentum geht durch Datumsheuristik (Plan-483 R10) oder Token-Wechsel verloren | Vorlage statt Entfernen, sichere Richtung; dokumentiert |
| R7 | `/permissions` wird teurer | nur bei eingeschalteten Schaltern, eine PK-Abfrage, optional 30-s-Cache (2.3) |
| R8 | Probe AK90 entwertet das Token und alle Sitzungen des Bot-Kontos | zuletzt in T0, neues Token danach (V2) |
| R9 | Timeout nach `addEmote`/`removeEmote`, Absturz, Lock-Verlust | Versuch vor dem Senden committet, Wiederaufnahme mit Read, idempotente Buchung (2.5a) |
| R10 | Verwalter sehen 409, solange der Bot an einem Wunsch schreibt | Sekunden, höchstens 15 min bei 7TV-Störung (Fristschritt 2.5, 2.5a (e)); K5 lädt neu und erklärt |
| R11 | Ein bereits abgeschickter HTTP-Aufruf eines Runners, dessen Transaktion nach Lock-Verlust abbricht, ist nicht rückholbar ([B] F6, R2-3) | Versuch und Beleg sind vor dem Senden committet; der Nachfolger löst lesend auf (2.5a (e), (h)); Eigentum entsteht ohne Sende-Nachweis nie; bewusst nicht weiter geschlossen |
| R12 | Unklare Reparaturen bleiben ungeheilt (`needs_review`) | sichere Richtung statt Doppel-Hinzufügen; Audit und Runbook-Abfrage; Pilot-Messung zählt sie |

---

## 9. Betreiberfragen

**Entschieden (Betreiber, 2026-11-20) [B]:**

- **[F1] PR-Schnitt:** zwei PRs, Merge strikt nacheinander. PR 1: T1 bis T5, T11a, T12; PR 2: T6 bis T10, T11b, T13, T14. Die
  Migration gehört nur zu PR 1; Deploy darf zusammen erfolgen.
- **[F2] Admin-IDs am Worker:** `Auth__AdminTwitchUserIds` wird auch am Worker gemappt; der Worker prüft E8 im Betrieb per Twitch-ID.
- **[F3] Falls AK86 scheitert:** K4 wird mit Variante (a) zu Ende gebaut (Bot fügt hinzu und bestätigt, entfernt nie, jeder
  Ablauf wird vorgelegt); T10 entfällt bis auf den Rückfall.

- **[F4] Unbekannter Editor-Status:** nach 15 Minuten ohne lesbaren Editor-Status geht der Bot-Kanal in den Rückfall
  (`fallback`), bis eine Prüfung wieder gelingt (2.3, [W22]). Entschieden 2026-11-20 wie empfohlen.
- **[F5] Abbruch einer offenen Aktivierung:** jeder Verwalter darf eine noch nicht bestätigte Aktivierung per `DELETE`
  abbrechen (2.9). Entschieden 2026-11-20 wie empfohlen.
- **[F6] Sol-Runde 2:** acht Befunde voll übernommen (R2-1, R2-2, R2-4 bis R2-9), R2-3 abgestuft: lokale Buchungen an die
  Runner-Epoche gebunden und Set-Wechsel mit dem Senden verzahnt (2.5a (c), (g)); ein schon abgeschickter HTTP-Aufruf nach
  Lock-Verlust ist nicht rückholbar und bleibt als R11 über die Wiederaufnahme gedeckt. Entschieden 2026-11-20.

Planer-Entscheidungen mit größerem Gewicht, denen der Betreiber widersprechen kann: Fensterrechnung bleibt (2.1 Punkt 4);
Timeout nach dem Senden gilt als „vielleicht geschrieben“ (2.6, 2.7); offener Schreibversuch beantwortet Verwalter-Befehle mit
409 (2.5a (b)); Auflösungsfrist 15 min und Bestätigungsfenster `BotWriteAtUtc − 2 min` (2.5a); Rückfall-Lauf auch im Ablauf-Lauf
(2.8); Liste vor Rolle beim Aktivieren (2.9); `wish.botActivate` mit dem Anfragenden als Akteur; Schwellenänderung leert keine
`bot-remove`-Aufträge und überspringt Wünsche mit offenem Versuch; Revision 2: Kanalzeile `FOR SHARE` während des Sendens,
Hinzufügen setweit eins zur Zeit, E11-Frist `Anker + 16 min`, unklare Reparatur nur `needs_review`.

---

## 10. Selbstprüfung

- Jede AK71 bis AK96 hat einen Task (Abschnitt 4); AK86 bis AK93 haben einen Live-Schritt; AK94 ist Beobachtung.
- Die vier Vormerkungen aus K2 sind in 2.1 entschieden, F12 ist durch Spec 8 und AK78 entschieden **[B]** (`not_bot_owned`,
  T10-Test).
- Keine Aussage über Bestandscode ohne Fundstelle am Stand `801e64a6`; die Feldnamen der 7TV-Schreibfläche, die nicht im Repo
  gemessen sind (`addedById`, Prüfabfrage, volles Set, entferntes Emote), stehen als Ergebnis von T0, nicht als Annahme.
- Jeder Sol-Befund der Runden 1 und 2 hat eine Zeile in der Tabelle „Revision 1“ bzw. „Revision 2“ mit Fundstelle und Ort der
  Lösung. Die Migration bleibt eine, rein additiv, jede neue Spalte an einer Bestandstabelle nullbar.
- Kein fertiger Code; Namen und Signaturen nur als Form.
