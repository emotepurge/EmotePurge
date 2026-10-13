# Plan #379 — Emote-Slots, Kind 9: Flag auf Prod, Abnahmelauf, Rückbau der Aus-Zweige, Doku

Erstellt am 2026-10-13 im Worktree `EmotePurge-379` (Branch `feat/379-slot-rollout`, Basis `origin/main` @ `3720f7cf`).
**Status: Revision 1 (13.10.) — Sol adversarial durch (4 × P2 eingearbeitet), Betreiberfragen F1–F6 entschieden (Abschnitt 12),
Backend-Inventur eingearbeitet (Abschnitt 4).**
Quellen: [Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md) §11.2, §12, §13, §16 Kind 9, §19; Issue #379 samt Übergaben aus
#371–#378 und #403/PR #406 (bindend); DECISIONS #365 „Child 1“–„Child 8“; `docs/Operations.md` „Chat-log backfill“, „Slot-aware 7TV
reconcile“, „Usage key switch (M2)“, „Backfill snapshot key (M3)“; Plan-372 §4, §7, §11; Plan-375 §8, §14. Formatvorlage: Plan-374/375/378.

**Was dieser Plan trägt:** Reihenfolge Einschalten ↔ Rückbau, Migrationsfrage, Rückbau als Vertrag, Sweep, Abnahme (lokal, Prod),
Runbook-Gerüst, Rollback, Doku. Kein Code; Namen nur als Form, SQL nur als Skizze.

**Lesehinweis:** **[E]** Planer-Entscheidung · **[B]** Betreiberentscheidung 13.10. · **[P]** Präzisierung · **Sol-P2-*n*** Befund des
adversarialen Reviews (gpt-6.1-sol), eingearbeitet.

### Revision 1 (13.10.) — was sich geändert hat

| Änderung | Anlass |
|---|---|
| F1–F6 entschieden, Folgen eingearbeitet (Folge-Issues WAL-Trockenlauf und #376 P3-1 als Aufgaben in A1/A3) | [B] |
| Abschnitt 4 aus der Backend-Inventur; B0 nur noch Gegenprobe; `SnapshotChanged` fällt (mit Ersatzantwort für Fall ii) | Inventur |
| Phase-2-Rollback: Compose-Mappings bleiben mit Default `true` bis zum Ende des Rollback-Fensters; Container-Env vor Freigabe prüfen; Portainer-Variable erst danach entfernen | Sol-P2-1 |
| Backfill-Zulassung: erst sperren (Api stoppen), dann leeren, erneut zählen, dann umstellen; Backfill bleibt aus, solange das Slot-Flag aus ist | Sol-P2-2 |
| Settle-Prüfung nur für Emotes im erfolgreich abgeglichenen Live-Set; Seeds abwesender Emotes und anderer Sets getrennt „unverändert“ | Sol-P2-3 |
| Erhaltungsprobe auf feste Population `Date < D0`, drei Zähler je `(EmoteId, EmoteSetId)`, heutiges Wachstum getrennt | Sol-P2-4 |

---

## 0. Befund vor dem Plan

1. **Rückbau in #379 oder danach?** #372 (Kommentar, Plan-372 §11) wollte ihn als eigenen Folgeschritt nach bewährtem Betrieb; #373–#378
   sprechen von „in #379“. **[B] F1:** Teil B unter #379, zwei PRs, #379 schließt nach Phase 2; die Bewährung (F5) liegt zwischen den Deploys.
2. **WAL-Vorbedingung** (Issue-Body, DECISIONS „Child 2 … Known cost“): **[B] F2:** Kosten tragen, `SevenTv:ResyncIntervalSeconds` bleibt
   60, **Bedingung** Hochrechnung aus Live A auf die Prod-Zeilenzahl < ~2 MB/min; sonst stoppt Phase 1 und der Betreiber entscheidet neu.
   Folge-Issue „Trockenlauf vor den Sperren“ für den Ausbau Richtung 50+ Kanäle.
3. **Prod-Kopie:** **[B] F3:** keine; Dev-DB-Kopie genügt. AK 1 ist durch die echten Prod-Fenster M1 (11.10.), M2 (12.10.), M3 (13.10.)
   belegt [P].
4. **Bekannte Grenzen:** **[B] F4:** #378-Klasse (vier Fälle, AK-48-Ergebnis), #376 P3-1 (kein gemeinsamer Snapshot `/totals`/`/series`),
   #376 P3-3 (Fan-out-Leave-Lücke) werden dokumentiert, das Flag geht trotzdem an; Folge-Issue nur für P3-1 (REPEATABLE-READ-Lesetransaktion).
5. **„LAN stack“** im Issue = lokaler Stack nach Plan-372 §7/Plan-375 §8 (Dev-DB-Kopie, eigener Redis, `dotnet run`) [P].
6. Spec §19: `SeedUnsettled`/`AddedAtFromRest` bleiben (tragen Flag an). Spec §11.2/Issue nennen noch ein Fenster mit M1–M3 — überholt.
7. DECISIONS „Child 2 … Flag contract“ („off: nothing writes to `EmoteSlots`“) ist seit #373 E2 überholt; der #379-Absatz ersetzt den
   Aus-Vertrag.
8. #406-Prüfung: die zweite Abfrage filtert auf eigene aktive Slots und liefert bei `own = 0` zwangsläufig 0 Zeilen; wirksam ist `own = 0`.

## 1. Umfang

### 1.1 Drin

- **Teil A (PR A, nur Doku):** Live A auf `3720f7cf` mit Flag an (6.1), Doku Teil A, Runbook Phase 1 (7.1), Folge-Issues anlegen lassen
  (WAL-Trockenlauf, #376 P3-1), Prod Phase 1 (Betreiber), Nachkontrolle.
- **Teil B (PR B):** Rückbau der Aus-Zweige (4), Sweep als Runbook-SQL mit Konstante und Test (5), Frontend-Shims/Kommentare, Doku
  Teil B, Live B (6.2), Prod Phase 2 (Betreiber).

### 1.2 Draußen (mit Besitzer)

- Trockenlauf-Optimierung — Folge-Issue (F2). #376 P3-1 — Folge-Issue (F4). Heilung der #378-Klasse — nicht geplant (F4).
- `SeedUnsettled`/`AddedAtFromRest` (Spec §19), Harness (D20), A11 (**[B] F6:** nicht anfassen).
- Einschalten von `ChatLogBackfill:Enabled` auf Prod — eigene Betreiberentscheidung, **frühestens nach Phase 1** und nur, solange das
  Slot-Flag an ist (Sol-P2-2; nach einem Phase-1-Rollback wieder aus).
- Entfernen der Compose-Mappings `SevenTv__SlotReconcile__Enabled` und der Portainer-Variable — nach dem Rollback-Fenster (7.2), kleiner
  Folge-Commit, nicht Teil des Phase-2-Deploys (Sol-P2-1).
- Finaler Runbook-Eintrag in `infra-docs` (Betreiber; Vorlage hier).

## 2. Reihenfolge: einschalten zuerst, Rückbau danach — zwei PRs, zwei Deploys [E]

**Was passiert, wenn ein Image ohne Aus-Zweig auf eine nie abgeglichene Prod-DB trifft?** Ein Rückbau-Image *ist* „Flag an, fest
verdrahtet“, und Flag an startet aus jedem Aus-Zustand: der erste Tick settelt die Seeds des aktiven Sets (`SevenTvSyncService.cs:1028-1046`
→ `ReconcileSlotsAsync`), die Map lässt Emotes mit unsettled Seed des aktiven Sets aus, bis sie gesettelt sind (`:1424-1426`; zeilenbasiert
über `SetSeeds.SeedsOfSet`, `Persistence/SetSeeds.cs:18-21`), die Leser fallen für sie in den Emote-Modus (`UsageViewRows.cs:69`). Belegt
durch Plan-372 §4 (Slot-Test 21) und den #373-Live-Lauf. **Kein Zustand muss erst von einem Flag-an-Lauf des alten Images hergestellt
werden.**

**Was der Rückbau voraussetzt** — er löscht die Übergangsnetze:

| Netz | wofür | Bedingung für den Wegfall |
|---|---|---|
| `SnapshotChanged` + Worker-Neulesen | nur Läufe, die **mit Flag aus** eingereiht wurden und noch `queued`/`running`/`paused` sind (Inventur: Flag-an-Enqueue bindet nur eigene Slots, 409 für Seed des aktiven Sets, nicht aktive Vorschau settelt in derselben Transaktion, Flag-an-Code erzeugt nie einen Seed) | Zulassungsfolge 2.1; auf Prod lief nie ein Backfill |
| Pending-Key + Flush-Phase 2 | nur die Aus-Map vergibt Pending-Keys | Puffer ist prozesslokal; Final-Flush des alten Workers |
| Z2-Mischfenster | Api und Worker in verschiedenen Flag-Zuständen | beide stoppen, bevor einer mit Flag an startet (#373) |

**Warum zwei Phasen:** (1) Phase 1 lässt sich per Variable in Sekunden zurücknehmen (Plan-372 Slot-Test 22, An → Aus → An); nach dem
Rückbau gibt es nur noch den Image-Rückweg. (2) Flag-an-Fehler und Rückbau-Fehler bleiben unterscheidbar. (3) Übergabe #372.

**Ablauf:** Phase 1 (Flag an mit den Images von `3720f7cf`; PR A ändert kein Image) → **Bewährung [B] F5:** ≥ 72 h inkl. eines großen
`handofblood`-Streams, ohne Rücknahme und ohne neuen P1/P2 → Phase 2 (PR B, normaler Deploy ohne Migration). Ein kurzes Mischfenster
alt/neu in Phase 2 ist unkritisch (beide Seiten an, gleiche Sperrreihenfolgen) [P].

### 2.1 Zulassungsfolge für Backfill-Läufe (Sol-P2-2) — gilt für Phase 2, jeden Rollback, die Dev-DB und fremde Betreiber

Ein Abbrechen von Läufen bei laufender Aus-Api lässt ein Enqueue-Fenster offen (`POST …/backfill` bleibt erreichbar,
`ChannelEndpoints.cs:348-369`); ein neuer Aus-Lauf bindet einen unsettled Seed (`ChatLogBackfillService.cs:1184-1194`) und bräuchte danach
`SnapshotChanged`. Deshalb **erst sperren, dann leeren, dann zählen, dann umstellen:**

1. **Sperren:** Api stoppen (schließt Enqueue; ein bloßes `ChatLogBackfill:Enabled=false` schlösse es auch, nimmt aber ebenso die
   Cancel-Route weg).
2. **Leeren:** Worker zu Ende laufen lassen oder stoppen; noch aktive Läufe bei gestopptem Worker mit dem Statement aus Operations
   „Rolling back“, Schritt 5, schließen (Status `cancelled`, `worker_error`).
3. **Zählen:** mit beiden Prozessen gestoppt `ChatLogBackfillRuns` in `queued`/`running`/`paused` = 0.
4. **Umstellen:** neue Images (bzw. Flag an) gemeinsam hoch.

Grundsatz: **Der Backfill ist aus, solange das Slot-Flag aus ist** — auch nach einem Phase-1-Rollback (dann zuerst Backfill aus nach 2.1,
dann Slot-Flag aus). Übersieht jemand einen Aus-Lauf, schlägt er nach dem Rückbau fehl (`worker_error` über die Ersatzantwort aus 4),
statt falsch zu zählen — fail-closed [P].

## 3. Migration: keine [E]

- Inventur: keine Aus-only-Spalte, kein Aus-only-Index. Der nicht-unique Emote-Index `(EmoteId, EmoteSetId, Date)` dient den emote-weiten
  Lesern (`UsageStatQueryService:193/457/608/732`), der Seed-SQL und Retention (`DataRetentionService:579`).
- **Sweep der zeilenlosen Seeds — drei Wege:**

| Weg | Für | Gegen |
|---|---|---|
| Datenmigration | läuft überall automatisch | eigenes Fenster (`PendingMigrationGuard`) für erwartet 0 Zeilen; macht den Image-Rollback zur `Down`-Frage |
| Laufzeit-Sweep (Boot/Retention) | ohne Handgriff | Dauercode für einen Einmalfall; falsches Prädikat löscht still |
| **Runbook-SQL, Text als Konstante mit Test** (Muster 3b) | kein Fenster, Zahl vorher sichtbar, Prädikat testfixiert | Handgriff je DB |

  **Empfehlung: Runbook-SQL.** Prädikat (Inventur bestätigt): `"SeedUnsettled" AND NOT EXISTS (UsageStats u WHERE u."SlotId" = s."Id")` —
  jedes Set, jede `Source`. Einziger FK auf `EmoteSlots` ist `UsageStats.SlotId`; `ChatLogBackfillRunEmotes.SlotId` hat keinen
  (`AppDbContext.cs:389-400`), Snapshotzeilen beendeter Läufe dürfen ins Leere zeigen (gelesen nur für aktive Läufe und von 3b, das den
  Slot voraussetzt). Vorbedingungen: Zulassungsfolge 2.1 abgeschlossen, Rückbau-Image läuft. Ablauf: zählen, in einer Transaktion löschen,
  Zahl notieren. **Nicht** gefegt: unsettled Seeds mit Zeilen (andere Sets, vertagte Settles, abwesende Emotes) und gesettelte leere Seeds
  (`SeedUnsettled = false`).
- **Folge fürs Runbook:** Phase 2 ohne Migrationsschritt; `migrations list` bleibt als Gegenprobe („nichts pending“, Lehre #391).

## 4. Rückbau (Teil B): Vertrag, Löschliste, Tests

**Vertrag nachher:** überall das heutige Flag-an-Verhalten. `SlotReconcileOptions`, die Bindung (`ServiceCollectionExtensions.cs:85-87`)
und die Schlüssel in `src/EmotePurge.Api/appsettings.json:23-25`, `src/EmotePurge.Worker/appsettings.json:21-23`, `.env.example:19-21`
entfallen; eine gesetzte Variable wird ignoriert, kein Startabbruch [E]. **Die Compose-Mappings (`docker-compose.yml:111-112, 151-152`,
`docker-compose.prod.yml:116, 154`) bleiben in PR B stehen, mit Default `true` und dem Kommentar „nur von Images vor #379 gelesen, für den
Rollback“** [E, Sol-P2-1]: Ein Rückweg auf `3720f7cf` mit einer Compose-Datei ohne Mapping startet mit Flag **aus** (appsettings-Default
`false`) — still geänderte Zählung und Leser, und die alte Api ignoriert `entries` in den Berichten. Mappings und Portainer-Variable
fallen erst nach dem Rollback-Fenster (7.2 Schritt 6).

**Löschliste (Inventur, Zeilen auf `3720f7cf`):**

| Datei | entfällt | bleibt |
|---|---|---|
| `SevenTvSyncService` | Delta-Aus-Zweig `:405-507`; `ReconcileAsync` (`:1579-1611`, D36 last-wins); `BuildEmoteMatchMapAsync` (`:1471-1504`); `UpsertEmote` (`:2003-2092`); `CommitIfOpenAsync` (`:2129`); Flag-Konjunkte in den Retry-Filtern `:335`, `:901` | `ApplySlotDeltaAttemptAsync`, `ReconcileSlotsAsync`, `BuildSlotMatchMapAsync`, `ArchiveAbsentEmotesAsync`, `IsNeverActivePlaceholder`, `IsCredibleRestLeave`, `IsEmoteKeyConflict`; Deadlock-Retries (Z4/Z5/Z7); Kommentare `:300-313`, `:1021-1026`, `:1421-1423` neu fassen |
| `SevenTvApiClient` | Aus-Overlay „letzter Treffer je Emote-ID“ (`:1217`, `:1250-1255`, `:1275-1277`), `SetEntriesOverlay.AddedAtByEmoteId` (`:1511`), `else if`-Zweig `:1149-1156`, Konstruktorparameter (typisierter Client, keine Registrierungsänderung) | `resultBySlotKey`, `PartialCountOnly` (erstes Record-Argument weg) |
| `EmoteService` | `MarkInSetUnlockedOnceAsync` (`:263-346`), Weiche `:238-240` | alle geteilten Helfer; `ApplyWithoutEntry` (`:681-718`, B1) |
| `ChatLogBackfillService` | D36-Auswahl `:218-225`; `BindOneSlotPerEmoteAsync` (`:904-951`), `BindSnapshotSlotsAsync` (`:1142-1198`), Weiche `:834-850`; Flag-Konjunkt `:256`; Ergebnis `SnapshotChanged` (`ReplaceBlockAsync:500-505`) | `MoveSeedSnapshotEntriesAsync`; Paar-Prüfung `LockAndCheckSnapshotRowsAsync` mit **neuer Antwort für Fall (ii)** (`:1310-1313`, „Slot des Emotes, der nicht im Snapshot ist“): `ArgumentException` wie ein fremdes Paar [E] |
| `ChatLogBackfillWorker` / `ChatLogBackfillSnapshotMap` | `SnapshotChanged`-Behandlung `:434-470`, `Rebind` (`:11-15`, `:76-127`), `ChatLogBackfillRebindResult` | übriger Lauf unverändert |
| `EmoteSetStatusService` | Aus-Zweige `:42-44`, `:47-49`; `FindDuplicateNamesAsync` | `ActiveNameEntries`, `FindDuplicateNamesBySlotAsync` |
| `UsageStatQueryService`/`UsageViewRows` | Durchreichung `:669`; Parameter und `!slotReconcileEnabled ||` (`UsageViewRows.cs:43`, `:69`) | `IsEmoteMode`, `EmoteModeRow` (Seed mit Zeilen unter X, E14) |
| `VoteSessionQueryService` | Flag-Teil in `:275` | `isSetSession || candidates.Count == 0` |
| `PendingSlotKey.cs` | ganze Datei | — |
| `BaseSlotRule` | `FindAsync`, `GetOrCreateAsync`, `LockAndGetOrCreateAsync`, drei SQL-Konstanten, `PickOldest`, `Distinct`, `BaseSlotPair`, `BaseSlotResult`; Klassen-Doku `:9-55` | `D2Order`, `Utf8Order`, `OldestAliasFirst`, `BaseSlotCandidate`; Umbenennung des Rests (z. B. `SlotOrder`) optional, ohne Verhaltensänderung, Entscheidung im Task |
| `UsageStatFlushService` / Core `IUsageStatFlushService` | Phase 2 (`WritePendingAsync`, `PendingPhaseLockTimeoutSql`, `PendingEntry`, Pending-Gültigkeitsfilter), `UsageFlushResult.Requeued` | Phase 1, Slot-Gültigkeitsfilter (verwirft auch einen verirrten Pending-String) |
| `UsageFlushWorker` | `_consecutivePartialFlushes` (`:31-36`), `RequeueHandedBack` (`:247-273`) samt Aufruf `:134`, Abzug `:109` | Final-Flush-Wiederholung E9a (deckt eine geworfene Phase 1) |
| Core-Doku-Kommentare | `SevenTvModels.cs:144`, `IEmoteService.cs:104`, `IUsageStatQueryService.cs:14`, `IUsageStatFlushService.cs:34-36, 62-71` | — |

**Bleibt ausdrücklich (Id-Zeilen, Emote-Modus):** `UsageViewRows.IsEmoteMode`, `ActiveNameEntries` (virtuell, E14), emote-weite Leser,
`ArchivedEmoteRowUpsert`, `EmoteSetLeaveObservations`, `EmoteSlotRollbackSql`; im Client `REMOVE_EMOTE_MUTATION` und alle Id-Zeilen in
Lösch-, Undo-, Restore-, Vote- und Import-Replace-Pfaden (`seven-tv-delete.service.ts:136/179-189/1103-1112`, `seven-tv-undo.service.ts`,
`usage-stats-page.ts:392/2193`, `purge-run-export.ts:195`, `seven-tv-run-engine.ts:54/75`), die `slotId`-Null-Zweige
(`merge-set-view.ts:73/86`, `usage-series.ts:70-125`, `emote-drilldown-dialog.ts:388`).

**Frontend:** Shims für eine Api vor #376/#377 dürfen fallen, weil nach Phase 2 keine mehr läuft (`merge-set-view.ts:237-252` `readWire`:
`Partial`-Cast und vier `??`; `usage-series.ts:97-117`; `usage-stats-page.ts:3415`, falls der Typ das Feld fordert). Flag-Wortlaut in
Kommentaren neu fassen (`merge-set-view.ts:14-16/29`, `usage-stat.model.ts:12-17`, `usage-stats-page.ts:396-397/1578/1865`,
`delete-progress-section.ts:455/515/527`, `seven-tv-delete.service.ts:548`, `purge-run-export.ts:45`, `e2e/support/mocks.ts:1244-1254`);
die Mock-Defaults bleiben (gültige Emote-Modus-Antworten).

**Tests (Inventur).** Ein Konstruktor ohne Options-Argument heißt heute **Flag aus**; mit dem Rückbau wechseln diese Tests still die
Semantik. Stellen ohne Options: `SevenTvSyncService` 29 in 8 Dateien (u. a. `SevenTvSyncServiceTests` 15, `EmptySetTests` 7), weitere
Helfer `LeaveObservationTests:753`, `PlaceholderTests:279`, `ChatLogBackfillServiceTests:860`; `SevenTvApiClient` 20; `EmoteService` 3;
`EmoteSetStatusService` 21; `UsageStatQueryService` 144; `VoteSessionQueryService` 35; `ChatLogBackfillServiceTests`-Helfer `:617-633`
(Default `slotsEnabled = false`; die Teilklassen `.cs`, `Races`, `Worker`, `Oracle`, `SlotBlocks` laufen aus). **Risiko** in den
Sync-REST-/Map-Tests (Warmstart ohne Sync → leere Map, D36, Pending-Keys, `Emote.Name`), den Enqueue-Tests (409 für Seed des aktiven Sets,
keine D36) und den In-Set-Tests von `EmoteServiceTests`; Lese-Tests überleben meist (`Fixtures/UsageSlots.cs` legt Zeilen auf
`UnsettledSeed` → Emote-Modus).

| Test | Aktion |
|---|---|
| `Unit/SlotReconcileOptionsTests`, `Unit/SlotReconcileRegistrationTests`, `Integration/SevenTvSyncServiceDeltaCharacterizationTests` | löschen |
| `SevenTvSyncServiceDuplicateEmoteIdTests` (2, D36) | löschen oder auf Mehr-Slot-Erwartung umstellen |
| `SevenTvSyncServiceSlotMapTests` | `FlagOff_*` (`:191`, `:219`, `:247`) löschen; `FlagOn_*` ohne Flag-Argument |
| `SevenTvSyncServiceSlotTests` | `FlagOff_SeedsAndMultiAlias…`, `OnOffOn_RenameWhileOff…`, `Tracker_Off_…` löschen |
| `SevenTvSyncServiceDeltaSlotTests` | `FlagOff_EveryChangeKind…` löschen |
| `SevenTvSyncServiceSlotConcurrencyTests`, `EmoteServiceInSetConcurrencyTests`, `LeaveObservationTests`, `EmoteServiceTests.Slots`, `MultiSlot`, `EmoteTagServiceTests` | `false`-Fälle der Bool-Theorien streichen |
| `ChatLogBackfillServiceTests.Oracle` (T0) | löschen |
| `ChatLogBackfillServiceTests.MultiSlot` | `Off_ActivePreview…IsNo409…`, `Off_Enqueue_PublishesNoChannelSynced` löschen |
| `ChatLogBackfillServiceTests.Slots` | Enqueue-Basis-Slot-Tests `:25/79/116/154/186/262/300` löschen; `ReplaceBlock_*` behalten; `:393`, `:554` prüfen (warten auf umgebundenen Snapshot) |
| `ChatLogBackfillServiceTests` (`.cs`, `Races`, `Worker`) | auf Flag an umstellen |
| `ChatLogBackfillServiceTests.SlotBlocks` | `:86` (Fall ii) auf die Ersatzantwort; `:114` (Settle bindet Aus-Snapshot um) löschen |
| `EmoteServiceTests.Slots` `Entries_FlagOff_AreIgnored…`; `EmoteSetStatusServiceSlotTests` `Off_WithOwnSlotsPresent…`; `UsageStatQueryServiceSlotTests` 2 × `Off_*` (Helfer `:336` ohne Parameter); `Unit/UsageViewRowsTests` `FlagOff_EveryEmote…` (`FirstSeenAtShared_IsFalse_…FlagOffRows` kürzen); `VoteSessionQueryServiceTests:735` (Parameter weg); `Unit/SevenTvApiClientChannelStateTests` `SlotReconcileOff_…LastHit` (Helfer `:428`) | löschen bzw. Parameter weg |
| `Unit/PendingSlotKeyTests` (4), `BaseSlotRuleTests` (13), `BaseSlotRuleOrdinalOrderTests` (1) | löschen; `Unit/BaseSlotD2OrderTests` bleibt |
| `UsageStatFlushServiceTests` Pending-Key-Tests `:378-529` (7); Worker `UsageFlushWorkerTests` `:31`, `:48`, `:124` | löschen |
| `SwitchUsageStatsToSlotKeyMigrationTests` `:268` (`HandOff_M1_OldWriter…`), `:402` (`FlagOffWorld_…`), `:575` | ohne Pending-Keys/Aus-Sync umbauen oder löschen; die Schema-Aussagen der Migration bleiben geprüft |
| `ChatLogBackfillWorkerTests:1232`, `ChatLogBackfillSnapshotMapTests` (5 × `Rebind_*`), `ChatLogBackfillFakes` | löschen bzw. `SnapshotChanged`-Unterstützung entfernen |
| Api `SevenTvEmoteSetSyncBookkeepingEndpointTests:486` | löschen (prüft Flag = false) |
| Api `EmoteUsageContextWireFormatTests` | behalten, Doku `:9` neu |

Grundsatz: Jede rote Stelle nach dem Umstellen wird bewusst entschieden und im Commit begründet, nie still an das neue Ergebnis
angeglichen. Migrationstests M1–M3 bleiben inhaltlich (Schema).

## 5. Sweep-Konstante (Teil B)

Konstante neben `EmoteSlotRollbackSql` (Operations zitiert wörtlich, „keep both texts identical“). Integrationstest (Muster
`BackfillSlotRollbackCleanupTests`) — löscht genau (i) unsettled Seed ohne Zeilen und (vi) einen solchen, auf den ein beendeter Lauf zeigt
(Snapshotzeile bleibt); lässt stehen: (ii) unsettled Seed mit Zeilen eines anderen Sets, (iii) mit nur importierten Zeilen, (iv)
gesettelter leerer archivierter Seed, (v) eigener Slot ohne Zeilen.

## 6. Live-Verifikation lokal (Regel 16) — Freigabe des Betreibers je Anlass

Randbedingungen wie Plan-372 §7/Plan-375 §8: geteilte Dev-DB `emotepurge` nur lesend; **nie `docker compose` aus dem Worktree, nichts im
Haupt-Checkout**; Ports vorab mit dem Orchestrator abstimmen. Ressourcen: DB `emotepurge_slots379` im Container `emotepurge-dev-postgres`
per `createdb` + `pg_dump emotepurge | psql` **im** Container; Wegwerf-Redis `redis-slots379`; Worker und Api per `dotnet run` aus dem
Worktree (PIDs notieren, vor jedem Signal `ps -o args= -p <pid>`); Env-Overrides: Connection → Kopie, Redis → Wegwerf-Port,
`Retention__Enforce=false`, `SevenTv__EventApi__Enabled`, `ChatLogBackfill__Enabled` und (nur Live A) `SevenTv__SlotReconcile__Enabled`
je Phase; Last begrenzen (`IsBotActive = false` außer `olaf_olaf_son`, `handofblood`). Anmeldung: Worktree-Api kurz auf `:5151`, Cookie in
eine Scratchpad-Datei (600), danach eigener Port. 7TV-Writes nur auf `olaf_olaf_son` (Token `~/.config/emotepurge-7tv-test-token`, nie
ausgeben, `curl -H @datei`); aktives Set „tttt“ `01M16ZBA3NHWWYNM7Q9416ZCH7`, Wegwerf-Set „test“ `01M320AYTGYMPJZD3RGPJGHH1S`, Testemote
`01KAMA120V9DC2P1YW9QWFHA16` (heute nur `hob44` in „tttt“); Ausgangszustand von „tttt“ vorher sichern, am Ende wiederherstellen. Vorher
`df -h /` und Größe der Dev-DB.

**Fallen:** (a) Testemote erst **settlen**, dann „nicht im Set“ herstellen (#374, T5-Falle). (b) Das Lösch-Dock lebt nur im Speicher —
Löschen und Restore im **selben** Tab, kein Reload. (c) `updateEmoteAlias` antwortet mit dem ersten Alias — kein Beleg; per REST/SQL
nachprüfen. (d) Nach jedem 7TV-Schritt mindestens ein Tick; REST kann bis 30 min hinken (SevenTV#81).

**Populationen (gelten für 6.1, 6.2 und 7.1) [P, Sol-P2-3, Sol-P2-4]:**
- **Settle-Population:** Seeds mit Zeilen des aktiven Sets eines aktiven Kanals, dessen erster An-Sync **erfolgreich** war
  (`LastSyncFailureReason` leer, Sync-Zeitpunkt nach dem Umschalten), **und** deren Emote danach aktiv ist (also im Live-Set steht). Nur hier
  wird „unsettled → 0“ verlangt, ausgenommen die Kanäle der Zeile „settle deferred“ (Zahl der vertagten Gruppen = Logzeile).
- **Unverändert-Population:** Seeds abwesender Emotes (archiviert, mit historischen Zeilen im aktiven Set; sie bleiben absichtlich unsettled,
  ohne Vertagungszeile, `SevenTvSyncService.cs:1711-1716`, Gegenbeispiel `SevenTvSyncServiceSlotTests.cs:548-562`) und Seeds anderer Sets:
  vorher/nachher byte-gleich (Prüfsumme über alle Spalten, sortiert).
- **Erhaltungs-Population:** `UsageStats` mit `Date < D0`, D0 = UTC-Datum der Messung (nicht zwischen 23:55 und 00:05 UTC messen). Je
  `(EmoteId, EmoteSetId)` Zeilenzahl und die Summen von `UseCount`, `BotUseCount`, `SharedChatUseCount`; Gleichheit verlangt. Der Flush
  schreibt nur den laufenden Tag, der Backfill ist aus, Retention löscht keine `UsageStats`-Zeilen außer per Kanal-Kaskade (Prod läuft mit
  `Retention__Enforce=true`: ein in der Prüfzeit gelöschter Kanal wird aus dem Vergleich genommen und benannt). Das heutige Wachstum
  (`Date = D0`) wird getrennt beobachtet, nicht verglichen.

### 6.1 Live A — Abnahmelauf auf `3720f7cf` (vor Phase 1)

0. **Aus-Basis:** Worker mit Flag aus, ein Tick; Populationen messen (CSV ins Scratchpad); `pg_current_wal_lsn()` vor/nach 10 Ticks; Zahl
   der Emote-Zeilen, die ein Tick sperrt (Reconcile-Ziele der aktiven Kanäle).
1. **Umschalten wie auf Prod** (2.1, dann beide stoppen, beide mit Flag an starten — Api zuerst). Vor dem ersten Tick: Enqueue für das
   aktive Set von `handofblood` → 409 `backfill_slots_unsettled`, nichts geschrieben (AK 26, dritter Satz).
2. **Erster An-Tick (AK 4–5, 17; Spec 11.2 Schritt 6):** Settle-Population → 0 (außer vertagt); Unverändert-Population byte-gleich;
   Erhaltung exakt; `handofblood`: `ome44` autoritativ mit altem Datum und allen Zeilen des aktiven Sets, `hob44` ohne Zeilen,
   `Emote.Name = ome44`; Tick-Dauer; `pg_current_wal_lsn()` über 10 Ticks → **F2-Hochrechnung** (WAL je gesperrter Zeile × Prod-Zeilenzahl
   aus 7.1.4 × Ticks/min) ins Protokoll. Zweiter Tick ohne Slot-Schreibung.
3. **Zählung (AK 14, 17, 34):** Testemote zusätzlich als `ome44` in „tttt“, Tick. **Der Betreiber tippt im Chat von `olaf_olaf_son`:**
   `ome44 hob44 ome44`, danach `hob44`. Erwartet: zwei Zeilen `(SlotId, Set, D0)`, `ome44` +1, `hob44` +2; zwei Karten mit `+1`, die Suche
   nach `hob44` findet die zweite.
4. **EventAPI (AK 18–21), Worker mit EventAPI an:** Push eines dritten Alias; Umbenennung bei zwei aktiven Slots → `AmbiguousDelta`
   `rename-with-siblings` + Resync; Pull eines Alias; Leave nur beim letzten Slot. Zeilen je Grund zählen.
5. **Löschen/Restore in der UI (AK 35, 37, 38, 41, 42, 46):** Karte `ome44` löschen → Request mit `alias`, `hob44` bleibt,
   `sync-deleted` mit `entries` → 200, Slot archiviert, Emote aktiv, keine Leave; **im selben Tab** Restore → dieselbe `SlotId`, Historie auf
   derselben Karte, der nächste Tick autorisiert das neue Datum. Variante: vor dem Löschen per Token umbenennen, ohne Reload → Dock
   „renamed“, kein REMOVE.
6. **Externe Wiederaufnahme (AK 47):** per Token entfernen, Tick, wieder hinzufügen, Tick → dieselbe SlotId, neues `AddedAt`.
7. **Ballot (AK 33):** Vote aus beiden Karten → eine Zeile „ome44 / hob44“.
8. **Backfill (AK 22–26, 39, 53 a)** wie Plan-375 §8.3 auf `handofblood` (lesend gegen `logs.cyex.app`, Fenster per `Channels.CreatedAt` nur
   in der Kopie): zwei Snapshotzeilen, Gate je Slot, zwei Zeilen an gemeinsamen Tagen, nächster Tick ohne neuen Slot; Cleanup 3b + Schritt 4.
9. **Nur Testnachweis** (live nicht gezielt herstellbar, im Protokoll benannt): AK 27, 40, 48, 51, 52, 54, 55, 53 b/c.
10. **Abbau:** „tttt“ auf die Sicherung, Prozesse SIGTERM (PID geprüft), Redis entfernen, `DROP DATABASE … WITH (FORCE)`, Cookie löschen.
    Protokoll mit SHA, Zahlen, Dauern, WAL; keine Hostnamen/Passwörter/Tokens; Kanalnamen nur `olaf_olaf_son`, `handofblood`.

### 6.2 Live B — Rückbau-Branch (vor PR B)

Neue Kopie der Dev-DB im Aus-Zustand — belegt den Fall „Rückbau-Image trifft nie abgeglichene DB“ (direktes Update eines fremden Betreibers).
1. Zulassungsfolge 2.1 auf der Kopie; Sweep-Zählung notieren; Populationen messen.
2. Api, dann Worker vom Rückbau-Branch (ohne Flag-Variable). Vor dem ersten Tick: 409 wie 6.1.1. Erster Tick wie 6.1.2.
3. Sweep aus der Konstante: zählen, löschen, Gegenprobe 0; Unverändert-Population byte-gleich.
4. Kurzlauf 6.1.3, 6.1.5, 6.1.6. Abbau wie 6.1.10.

## 7. Prod-Fenster (Runbook-Gerüst; Handgriffe macht der Betreiber, bash auf der Devbox, `sudo docker` auf dem VPS, Passwort als Platzhalter)

### 7.1 Phase 1 — Flag an mit den Images von `3720f7cf`

Vorbedingungen: Live A bestanden, F2-Bedingung erfüllt (Hochrechnung < ~2 MB/min); Folge-Issues WAL-Trockenlauf und #376 P3-1 angelegt;
laufende Images `3720f7cf`, `migrations list` ohne Pending; `ChatLogBackfill__Enabled=false` im Stack.

1. Ankündigung außerhalb großer Streams.
2. **#403/#406-Prüfung** (read-only, `BEGIN READ ONLY`, Abfrage aus PR #406): `own = 0`. Ergebnis als Kommentar auf #403. Sonst Abbruch.
3. **Backfill:** `ChatLogBackfillRuns` in `queued`/`running`/`paused` = 0 (erwartet; auf Prod lief nie ein Lauf). Sonst Zulassungsfolge 2.1.
4. **Vorher messen (read-only):** D0 festlegen; Settle-, Unverändert- und Erhaltungs-Population (6); Zahl der Reconcile-Ziel-Zeilen
   (F2-Hochrechnung gegenprüfen); zeilenlose unsettled Seeds (erwartet 0); `pg_current_wal_lsn()` mit Uhrzeit.
5. Backup `scripts/backup-postgres.sh`.
6. **Worker und Api stoppen** (beide, bevor einer mit Flag an startet — Z2); `pg_stat_activity` ohne `EmotePurge%`.
7. `SEVENTV_SLOT_RECONCILE_ENABLED=true` in der Portainer-Umgebung; beide gemeinsam hoch; Uhrzeit notieren; **wirksame Konfiguration
   prüfen** (`printenv SevenTv__SlotReconcile__Enabled` in beiden Containern = `true`).
8. **Nachkontrolle ≤ 5 min (AK 43, Dauer ins Runbook):** (a) Settle-Population → 0 außer den „settle deferred“-Kanälen (Logzeile
   „{Channel}: settle deferred: v4 dates missing for {Count} emotes“); (b) Unverändert-Population byte-gleich; (c) Mehr-Slot-Emotes > 0
   (`handofblood` ≥ 1); (d) kein Dauerzustand „settle deferred“ über drei Ticks; (e) `handofblood` in der UI: zwei Karten, alte Summe bei
   `ome44`, `hob44` klein; (f) `usage.flushed`, keine Flush-Fehler, heutiges Wachstum sichtbar; (g) Erhaltungs-Population exakt; (h) die
   Map-Ausschlusszeile fällt auf 0 (außer vertagten Kanälen).
9. **Tag 1 (Logs/read-only):** `AmbiguousDelta` je Grund (nur falls EventAPI auf Prod an ist; `seed-of-active-set` nur am Anfang, danach
   selten, häufigster Dauergrund `rename-with-siblings`) (#374); WAL über 10 Ticks gegen den Vorher-Wert (F2); `/totals`-Latenz auf
   `handofblood` gegen 1,45× (#376); der erste echte Löschlauf einer Slot-Karte sendet `alias` + `entries`, `sync-deleted` → 200 (#378);
   Leave-Zeile beim Entfernen eines Emotes nach dem 30-min-Fenster (#372).
10. Ergebnis als Kommentar auf #379; Vorlage für `infra-docs` mit Dauern. Beginn der Bewährung (F5).

**Rollback Phase 1:** Backfill ist und bleibt aus (2.1); Worker und Api stoppen, Variable `false`, beide hoch, `printenv` prüfen.
Datenzustand: Slots bleiben gesettelt; der Aus-Pfad zählt über Basis-Slot-Regel (a) auf den ältesten Slot mit Zeilen des Paars, die Leser
summieren je Emote → die Emote-Zahlen stimmen; das Slot-Inventar veraltet und heilt beim nächsten An-Lauf über die Datums-Identität
(Slot-Test 22). Folge: E6a gilt nicht mehr — M2-/M3-`Down` nur nach den Kollaps-Statements.

### 7.2 Phase 2 — Rückbau-Image

Vorbedingungen: Bewährung F5 erfüllt; PR B gemergt; Live B bestanden.
1. Ankündigung. **Zulassungsfolge 2.1**, falls der Backfill inzwischen an ist (dann sind alle Läufe An-Läufe; trotzdem keine aktiven
   Läufe beim Wechsel, damit der Sweep greift).
2. `migrations list`: nichts pending (sonst Abbruch, #391).
3. Stack mit den neuen Images und der neuen Compose-Datei aktualisieren (beide gemeinsam). Variable `true` und Mapping (Default `true`)
   bleiben.
4. Nachkontrolle: Health 200, Flush wie vorher, Resync-Tick ohne Fehler, `handofblood` unverändert zwei Karten.
5. Sweep: zählen (erwartet 0), bei > 0 in einer Transaktion löschen, Zahl notieren.
6. **Rollback-Fenster:** bis zum ersten Deploy nach #379, der eine Migration bringt, mindestens aber 14 Tage [E]. Erst danach Folge-Commit:
   Compose-Mappings entfernen, Portainer-Variable löschen, Operations-Hinweis streichen (Sol-P2-1).

**Rollback Phase 2:** Images von `3720f7cf` (sha-Tag) mit einer Compose-Datei, die das Mapping trägt (die von `3720f7cf` oder die aus
PR B mit Default `true`), und Variable `true`. **Vor der Freigabe** in beiden Containern `printenv SevenTv__SlotReconcile__Enabled` = `true`;
zeigt einer `false` oder nichts, sofort stoppen und korrigieren — sonst läuft das alte Image still mit Flag aus (Zählung und Leser
emote-weit, die alte Api ignoriert `entries`). Keine Migration, kein `Down`; gefegte Seeds fehlen dem alten Image nicht (inert bei An).
Zulassungsfolge 2.1 auch hier.

## 8. Dev-Datenbank und fremde Betreiber (Teil B)

- Mit dem Merge von PR B schaltet jeder Neubau des geteilten Dev-Stacks das Slot-Verhalten an. Lokal ist der Backfill an: vor dem
  Neubau Zulassungsfolge 2.1 auf der Dev-DB (Api stoppen, Läufe leeren, zählen), danach Sweep. Hinweis in den PR-Text; den Zeitpunkt
  bestimmt der Betreiber.
- Operations.md bekommt den Upgrade-Hinweis für Betreiber, die nie eingeschaltet haben: ein Image ab #379 schaltet ein; vorher
  Zulassungsfolge 2.1, Api und Worker gemeinsam stoppen und starten, danach Sweep. Rollback wie 7.2 (Mapping + Variable `true`).

## 9. Doku-Änderungen

| Datei | Teil | Änderung |
|---|---|---|
| `docs/Architectur.md` Modul D | A | Mutation (`:203-212`) um die Slot-Form mit `alias` ergänzen, die Id-Form bleibt (Vote, Import-Replace, Undo, Id-Zeilen); Berichtssatz (`:215`) um `entries` `{ sevenTvEmoteId, slotId, alias, addedAt }` in `sync-deleted`/`sync-restored` (Archivierung je Slot, Leave nur beim letzten Slot, veraltet = 2xx-No-op); `UsageStat`-Block (`:290-298`) auf `SlotId`/`EmoteSetId` und den Slot-Index; kurzer `EmoteSlots`-Absatz |
| `docs/Architectur.md` `:80` | B | Pending-Key-Satz streichen |
| `docs/Operations.md` | A | Abschnitt „Slot-aware 7TV reconcile“: Einschalt-Runbook (öffentlicher Teil von 7.1), Populationen, Rollback Phase 1, Zulassungsfolge 2.1, Grenzen (F4) |
| `docs/Operations.md` | B | Flag-Tabelle/Aus-Beschreibung (`:1079-1099`) → „Upgrade und Rollback“ (8, 7.2 inkl. Mapping-Hinweis); 3b-Hinweis `:1037-1040`; Sweep-Abschnitt mit Konstante; M2-/M3-Rollback (`:1156ff`, `:1252-1270`): Kollaps vor `Down` ist der Normalfall |
| `docs/DECISIONS.md` (englisch) | A + B | Absatz **„Child 9 (operations and switch-on), 2026-10-1x“** nach „Child 8“, wächst mit den Commits: zwei Phasen samt Begründung, F2 mit Messwerten und Bedingung, F4-Grenzen, F5, AK 1 durch die Prod-Fenster, D20 und D8 als Schlusssätze; Teil B: Rückbau, ignorierte Variable, Mapping-Rollback-Fenster, keine Migration, Sweep als Runbook-SQL, Ersatzantwort Fall (ii), was bewusst blieb. Titel „(#365, draft)“ → final; `Betrifft:` (`:454`) anpassen |
| Spec Statuszeile (`:3`), §16 Kind 9 (`:631`) | A/B | Satz zu Phase 1 und Phase 2 mit Datum |
| `CLAUDE.md` Umsetzungsstand | B | Zeile „Emote-Slots (Epic #365)“ nach „Chat-Log-Backfill“; Worker-Absatz knapp ergänzen |
| Backfill-Spec (`:7`, `:560`, `:603`) | B | „with the slot flag off only“ als historisch markieren |
| Epic #365, #346 | A/B | Statuszeilen; Folge-Issues als Zeilen |

`docs/Feature-Ideen-2026-08-01.md` bleibt unverändert (F6).

## 10. Gates

**Je Aufgabe:** `dotnet build EmotePurge.slnx` ohne Warnungen, betroffene Testprojekte grün; bei Web-Änderung Vitest, `lint`, `format`;
`dotnet format --verify-no-changes`. **Abschluss Teil B (AK 44, 45):** `dotnet test EmotePurge.slnx` (Docker), `npm --prefix web test --
--watch=false`, `npm --prefix web run e2e` (ohne Api auf `:5151`), `coverage-local.mjs` ≥ 80 % nach dem Commit, `CoreAssemblyReferenceTests`,
`api-error-locales.spec.ts`, `ReplayDayCounterTests` (24), `harness-3` unverändert. Keine neuen Testklassen außer dem Sweep-Test.

## 11. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext, strikt nacheinander)

| # | Aufgabe | Modell | Fertig, wenn / Commit |
|---|---|---|---|
| A1 | Doku Teil A (Architectur Modul D, Operations-Einschalt-Abschnitt mit 2.1 und Populationen, DECISIONS „Child 9“ Teil 1 mit F1–F6); Texte der zwei Folge-Issues (WAL-Trockenlauf; #376 P3-1 REPEATABLE READ) als Entwurf für den Orchestrator | sonnet | `docs:`-Commit; Issue-Entwürfe übergeben |
| A2 | Live A (6.1) mit dem Betreiber | sonnet (+ Orchestrator) | Protokoll inkl. F2-Hochrechnung; Abbau erledigt |
| A3 | PR A; fertige Fenster-Befehle 7.1 (Platzhalter); Folge-Issues angelegt und im Epic eingetragen | opus | PR offen; Befehle beim Betreiber |
| — | **Prod Phase 1** (Betreiber), Kommentare #403/#379, Bewährung F5 | Betreiber | 7.1 abgehakt, 72 h ohne Rücknahme |
| B0 | Gegenprobe der Löschliste und der Testtabelle (4) auf dem aktuellen `origin/main`; Abweichungen in den PR-Text | sonnet | Tabelle bestätigt |
| B1 | Tests vorab explizit auf „an“, wo sie An-Verhalten prüfen; Aus-Orakel bleiben noch — Suite grün auf unverändertem Code | sonnet | `test:`-Commit |
| B2 | Rückbau Sync/Delta/Overlay/Map/Status/Leser/Vote, zugehörige Aus-Tests löschen | opus | Suite grün, `refactor:`-Commit |
| B3 | Rückbau Flush-Phase 2, `PendingSlotKey`, `BaseSlotRule`-Einstiege, Worker-Rückgabe; Enqueue-Kind-3-Pfad, `SnapshotChanged` (mit Ersatzantwort Fall ii); `MarkInSetAsync`-Aus-Pfad | opus | Suite grün inkl. Worker.Tests, `refactor:`-Commit |
| B4 | Options-Typ, Bindung, appsettings, `.env.example` entfernen; Compose-Mappings auf Default `true` mit Rollback-Kommentar | sonnet | Suite grün, `chore:`-Commit |
| B5 | Sweep-Konstante + Test (5) + Operations-Abschnitt | sonnet | Test grün, Textgleichheit, `feat:`-Commit |
| B6 | Frontend-Shims und Flag-Kommentare, Mock-Kommentar | sonnet | Vitest, Lint, Format, E2E grün, `refactor(web):`-Commit |
| B7 | Doku Teil B (9) | sonnet | `docs:`-Commit |
| B8 | Live B (6.2) | sonnet (+ Orchestrator) | Protokoll |
| B9 | Opus-Review, Codex Sol `/codex:review --model gpt-6.1-sol`, Gates, PR B mit Deploy-Hinweis 7.2 und Dev-Hinweis 8 | opus | Gates grün, Befunde unverändert vorgelegt |
| — | **Prod Phase 2** (Betreiber) | Betreiber | 7.2 Schritte 1–5; #379 zu |
| — | Folge-Commit nach dem Rollback-Fenster: Compose-Mappings + Portainer-Variable entfernen | sonnet / Betreiber | 7.2 Schritt 6 |

## 12. Betreiberentscheidungen (2026-10-13) — keine offenen Rückfragen

- **F1 — entschieden:** Teil B unter #379, zwei PRs, #379 schließt nach Phase 2.
- **F2 — entschieden:** Kosten tragen, Intervall 60 s, Bedingung: Hochrechnung aus Live A auf die Prod-Zeilenzahl < ~2 MB/min; Folge-Issue
  „Trockenlauf vor den Sperren“ für 50+ Kanäle (legt der Orchestrator an, A1/A3).
- **F3 — entschieden:** keine Prod-Kopie, Dev-DB-Kopie.
- **F4 — entschieden:** alle drei Grenzen dokumentieren und einschalten; Folge-Issue nur für #376 P3-1 (REPEATABLE READ).
- **F5 — entschieden:** Bewährung ≥ 72 h inkl. eines großen `handofblood`-Streams, ohne Rücknahme und ohne neuen P1/P2.
- **F6 — entschieden:** A11 nicht anfassen.

## 13. Risiken für das Review von PR B

- Stiller Semantikwechsel von Tests ohne Options-Argument (4).
- Rollback auf `3720f7cf` ohne Mapping/Variable startet mit Flag aus (7.2 Rollback, `printenv`-Prüfung).
- Ein übersehener Aus-Lauf schlägt nach dem Rückbau über die Ersatzantwort fehl — fail-closed, aber der Lauf ist verloren (2.1).
- E6a ist nach einem Phase-1-Rollback dauerhaft verletzt — jeder spätere `Down`-Gedanke braucht den Kollaps.
