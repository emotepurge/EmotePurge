# Spec: Emote-Slots — der Zähl-Einheit wird der Set-Eintrag (Name im Set), nicht die 7TV-ID

**Datum:** 2026-10-10 · **Status:** fünfte Fassung (R5, nach Codex-Runde 3 abgeschlossen). D8 gilt: Die Spec ist nach R5 fertig und geht ohne vierte Review-Runde in den PR. R5 schließt alle sieben Befunde mit pass/fail-Kriterien; D0–D8 bleiben unverändert. Die Sonde S1 ist bestanden (2.4) · **Issue:** #365 · **Epic:** #346 (Chat-Log-Backfill) · **Vorgänger:** #74/#341 (Last-Alias-Regel, DECISIONS 2026-10-08), #200 (Zählung je Set, DECISIONS 2026-09-20), Backfill-Spec [2026-10-09-chat-log-backfill-spec.md](2026-10-09-chat-log-backfill-spec.md) · **Berührt:** #45 (Namenskollisionen), #69 (Harness, geschlossen), #201 (Tags), #230/#253 (Restore je Alias)

> Deutsch, wie DECISIONS 2026-10-09 („Later specs stay German") es vorsieht. Bezeichner, Spalten, Routen und Schlüssel stehen wie im Code. Zeilenangaben sind am 2026-10-10 gegen `main` @ `b9261c41` nachgeprüft; wo die Inventur-Zuarbeit abwich, gilt die Zeile hier.

Labels: `epic`, `feature`, `worker`, `api`, `web`, `migration`

## 1. Kontext

7TV erlaubt denselben Emote (eine ObjectID) **mehrfach in einem Set unter verschiedenen Namen**; jeder Eintrag belegt einen Platz („Slot") und trägt sein eigenes `added_at`. Jedes untersuchte Set trug solche Paare (#200-Spec, Zeile 40: 762/760, 687/686, …). EmotePurge kennt je `(ChannelId, SevenTvEmoteId)` genau eine Zeile mit genau einem `Name` und einem `FirstSeenAt` (Regel 8). Seit #341 gewinnt beim REST-Abgleich der **letzte** Eintrag der 7TV-Liste; der andere Name ist nicht zählbar, live wie im Backfill (AC 26 der Backfill-Spec).

Gemessener Schaden (Issue #365, 2026-10-10, Kanal `handofblood`, Emote `01KAMA120V9DC2P1YW9QWFHA16`): Original `ome44` (lange im Set), Alias `hob44` (7TV-Datum 2026-09-01). Live zählt Prod seit Juli rund 1.600 Nutzungen unter `ome44`; der 6-Monats-Backfill matchte nur `hob44` (599 Treffer, 01.–11.09.), schnitt mit dem Alias-Datum 2026-09-01 jede ältere Nachricht ab, und die Karte zeigte „Neu · 2 Tage", weil unser `FirstSeenAt` vom jüngsten Slot stammt. Untergezählt zu werden ist genau das, was einen Emote in Richtung „selten" und Löschung schiebt.

Der Betreiber hat am 2026-10-10 entschieden (Abschnitt 3, D0–D4 und D7): **Zähl-Einheit ist der Slot**, und **die Identität eines Slots ist sein 7TV-Eintrag, erkennbar am `added_at`.** Diese Spec baut das in Datenmodell, Live-Zählung, EventAPI, Backfill, API, Frontend, Migration und Deploy-Reihenfolge um und benennt jede Stelle, an der heute „ein Name, ein Datum je Emote" angenommen wird.

## 2. Verifizierter Ist-Stand (main @ `b9261c41`, 2026-10-10)

### 2.1 Die sechs Kollaps-Stellen (eine Zeile je 7TV-ID)

| # | Stelle | Regel heute | Folge |
|---|---|---|---|
| K1 | `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs:985` `liveEmotes.Reverse().DistinctBy(e => e.Id).Reverse()` in `ReconcileAsync` (`:971-1052`) | letzter Eintrag gewinnt (#341) | zweiter Alias nicht zählbar; `UpsertEmote` (`:1063-1143`) schreibt einen `Name`, ein `FirstSeenAt` |
| K2 | `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:1230` `result[entry.Emote!.Id] = addedAt.UtcDateTime` in `GetSetEntriesAsync` (`:1194-1240`); Overlay `:1130-1138` | Datum des letzten Slots überschreibt; `GqlSetEntriesQuery` (`:39-40`) selektiert **kein** `alias` | `FirstSeenAt` = Datum des **letzten** Eintrags; zusammen mit K1 ist die `Emote`-Zeile eine konsistente Kopie des letzten Eintrags (Name und Datum) |
| K3 | `src/EmotePurge.Infrastructure/Services/ChatLogBackfillService.cs:205-211` `preview.Emotes.Reverse().DistinctBy(e => e.SevenTvEmoteId).Reverse()` (D36) | letzter Alias gewinnt | Snapshot hält einen Alias je ID; PK `(RunId, EmoteId)` (`AppDbContext.cs:352`); `ValidateBlock` (`:1079-1097`) verwirft zwei Aggregate je `(EmoteId, Date)`; `InsertAggregatesAsync` (`:932-950`) schreibt ohne Slot |
| K4 | `src/EmotePurge.Worker/ChatLogBackfill/ChatLogBackfillWorker.cs:290` `addedToSetDay.TryAdd(emote.EmoteId, …)` | erster gewinnt | Gate je Emote-ID (`ChatLogBackfillBlockCounter.cs:80-83`); der Worker liest den Snapshot **einmal** je Lauf (`:273-319`) |
| K5 | `src/EmotePurge.Infrastructure/Services/VoteSessionService.cs:126-130` `liveMembersById.TryAdd(…)` | erster gewinnt | `NameAtCreation` (`:175`) friert einen Alias ein — **widerspricht** K1 |
| K6 | `src/EmotePurge.Infrastructure/Services/EmoteTagService.cs:799-803` `snapshot.TryAdd(…)` („should not happen") | erster gewinnt | Tag-Alias-Snapshot (`:394`) hält einen Alias |

### 2.2 Schlüssel, die „ein Emote = eine Zeile" festschreiben

| Objekt | Schlüssel | Datei:Zeile |
|---|---|---|
| `Emotes` | unique `(ChannelId, SevenTvEmoteId)` = `IX_Emotes_ChannelId_SevenTvEmoteId` | `AppDbContext.cs:42`; `SevenTvSyncService.cs:29`, `:1149-1154`; `ArchivedEmoteRowUpsert.cs:43-48` |
| `UsageStats` | unique covering `(EmoteId, EmoteSetId, Date) INCLUDE (UseCount)`; FK cascade | `AppDbContext.cs:64-66`, `:71-74`; `ON CONFLICT` in `UsageStatFlushService.cs:79-90` |
| Zähl-Schlüssel | `UsageCounterKey(EmoteId, EmoteSetId)` | `IUsageStatFlushService.cs:34`; Konsumenten `UsageStatFlushService.cs`, `Worker/IEmoteUsageCounter.cs`, `Worker/EmoteUsageCounter.cs` |
| Match-Map | `EmoteMatchSnapshot(NameToEmoteId, EmoteSetId, GeneratedAtUtc)`, Wert = `Emote.Id` | `IEmoteMatchCache.cs:12-15`; gebaut `SevenTvSyncService.cs:890-929`; Leser `TwitchChatManager.cs:1089`, `Worker.cs:166`, `SevenTvSyncService.cs:611-618` |
| Boot | `RunBootRecoveryAsync`: je Kanal Map aus Postgres wärmen **und** Chat joinen, erst danach Phase 2 `SyncSevenTvAsync` | `Worker.cs:137-180` |
| Snapshot Backfill | PK `(RunId, EmoteId)`; `ChatLogBackfillSnapshotEmote(EmoteId, Name, AddedToSetDay)`; `ChatLogBackfillAggregate(EmoteId, Date, …)` | `AppDbContext.cs:352-354`; `IChatLogBackfillService.cs:290-296` |
| Berichte | `POST /api/seventv/emote-sets/{id}/sync-deleted|sync-restored` mit `SyncInSetRequest(SevenTvEmoteIds, ExpectedChannelName, TargetOwnerTwitchId)`; `MarkInSetAsync` verarbeitet `Entries` je Treffer-Kanal, validiert `SlotId` im Ursprungskanal und löst die übrigen Kanäle über `AddedAt` auf; Leave nur beim tatsächlichen letzten Slot | `SevenTvEndpoints.cs:291`, `:909`; `EmoteService.cs:212-378`; `EmoteTagService.cs:555-562`; `seven-tv-restore.service.ts:626-633`, `seven-tv-delete.service.ts:640-680` |
| Lauf-Engine | `RunOperation.beforeStep(setId, emote, step)` wird **vor jedem Versuch** eines Schritts gefragt, auch nach Wartezeiten und bei Retries | `seven-tv-run-engine.ts:125-180`, `:526`, `:587` |
| Votes | `VoteSessionEmote` PK `(VoteSessionId, EmoteId)`, `NameAtCreation` einwertig | `AppDbContext.cs:240`, `:258`; `VoteSessionEmote.cs:16` |
| Tags / Leaves | `EmoteTagEntries` PK `(TagId, SevenTvEmoteId)`; `EmoteSetLeaveObservations` PK `(ChannelId, SevenTvEmoteId, SevenTvEmoteSetId)` | `AppDbContext.cs:143`, `:210` |

**Zählung:** 8 Entitäten an die Emote-ID geschlüsselt; 4 mit einwertiger Namens-/Datumsspalte; 3 Unique-Schlüssel, 2 `ON CONFLICT`-Ziele, 1 Validator und 2 Berichtsverträge sind umzubauen. **Die Sync-Sperre je Kanal ist prozesslokal** (`ChannelSyncGate`, `SevenTvSyncService.cs:561-581`); Api-Schreiber (Ballot, Backfill-Enqueue) laufen nebenläufig dazu. `PostgresFixture` migriert jede Test-DB auf den **neuesten** Stand (`Fixtures/PostgresFixture.cs:27`).

### 2.3 EventAPI (Delta-Pfad)

- `SevenTvDispatchParser.cs:21-52` → `SevenTvEmoteSetDelta(Pushed, Updated, PulledIds)` (`SevenTvModels.cs:102-108`); `TryMapEmoteChange` (`:129-150`) liest nur `value`, `TryGetPulledEmoteId` (`:152-170`) nur `old_value.id`. **`old_value.name` wird nirgends gelesen; ein Dispatch trägt kein `added_at`.**
- `ApplyEmoteSetUpdateAsync` (`:244-400`): `existing` nach `SevenTvEmoteId` (`:305-307`), Plausibilität über IDs (`:318-333`), Push mit `AddedToSetAt = UtcNow` (`:348`), Update überschreibt `Name` (`:353`), Pull archiviert die Zeile (`:356-365`), Leave je `PulledIds` (`:384-385`). `SetNotActive`/`ImplausibleSkipped` → `ResyncChannelAsync` (`SevenTvEventClient.cs:388-400`).
- 7TV-Quelle (`7tv-write-semantics.md`, SevenTV/SevenTV @ `e1733255`): `pushed.index`/`updated.index` zeigen auf den **ersten** Slot der ID; bei Umbenennung des zweiten Slots trägt `updated.value.name` den Namen des ersten, `old_value.name` ist korrekt; `pulled` liefert ein Item je entferntem Eintrag mit `old_value{id,name}`.

### 2.4 7TV-Schreibpfad, `added_at` und Sonde S1

- `seven-tv-delete.service.ts:93-111` `removeEmote(id: { emoteId })` **ohne** `alias`; `toDeleteQueue` `:815-841` eine Zeile je ID. v4: `EmoteSetEmoteId { emoteId, alias? }`; `removeEmote` mit `alias` zieht genau den Eintrag `(id, alias)`; `updateEmoteAlias` benennt genau einen Slot um und lässt `added_at` stehen; `addEmote` setzt `added_at: chrono::Utc::now()` **je Aufruf** (`op.rs:650`, `origin_set_id: None`). **7TV adressiert Einträge nur über `(emoteId, alias)`; einen Selektor über `added_at` gibt es nicht.** Die Live-Liste (`ForeignEmoteRow`) trägt seit #346 `addedAt` je Eintrag; unser DTO liest es als `DateTimeOffset?` (`SevenTvApiDtos.cs:243-247`), ISO mit Millisekunden.
- `seven-tv-import.service.ts:85-98` sendet den Alias-Selektor bereits; Restore-Queue schlüsselt `${sevenTvEmoteId}#${alias}` (`seven-tv-run-engine.ts:51-55`); der Löschlauf liest das Set vor dem `REMOVE` live (`mass-delete-panel.ts:211`, `:231`).
- **Sonde S1 (Betreiber, 2026-10-10, Wegwerf-Set, Schreiben über 7TVs Dialog „Manage Emote Aliases", Lesen über die öffentliche v4-GQL; [#365, Kommentar 6098447313](https://github.com/emotepurge/EmotePurge/issues/365#issuecomment-6098447313)):** drei Slots des Emotes `01KAMA120V9DC2P1YW9QWFHA16` (`hob44`/`ome44`/`hob`) trugen je ein eigenes `addedAt` (ms-genau verschieden); die Umbenennung jedes Slots einzeln (→ `hob1`/`hob2`/`hob3`) ließ **jedes** `addedAt` unverändert; das Löschen von `hob2` ließ `hob1` und `hob3` unberührt. **Ergebnis:** `added_at` ist eine stabile, je Eintrag eigene Identität (D4), die Löschung je Slot ist möglich (9.3). Offen blieb nur, dass die Sonde durch die Website lief und nicht durch **unsere** Mutation; das prüft Kind 8 (AK 36).
- **Gleichstand (`added_at` zweier Einträge derselben ID identisch):** in den geholten v4-Mutationen stempelt nur `addEmote` ein `added_at`, je Aufruf; die drei Adds der Sonde lagen ms-weit auseinander. Nicht geholt und daher **unbekannt**: der v3-Add-Pfad und das Zusammenführen von Origin-Sets (SevenTV/API#253, DECISIONS 2026-08-04 nennt es als Quelle der Duplikate; `origin_set_id` existiert auf dem Eintrag) — ein Massenpfad könnte identische Stempel setzen. Die Spec behandelt den Gleichstand deshalb deterministisch (D4, 6.2 Schritt 3), statt ihn auszuschließen.

### 2.5 Lesepfad und Frontend

- `UsageStatQueryService.GetUsageContextAsync` (`:26-133`): alle `Emote`-Zeilen (`:62-66`), Aggregation nach `EmoteId` (`:94-104`), `EmoteUsageContextDto(EmoteId, EmoteName, SevenTvEmoteId, ImageUrl, TotalUseCount, LastUsedDate, PreviousWindowUseCount, FirstSeenAt, IsArchived, NameTwinEmoteSetIds)` (`IUsageStatQueryService.cs:52-62`); `GetDailySeriesAsync` (`:135-215`) nach `emoteId`; `GetChannelSeriesAsync` (`:217-290`); `GetTotalsByEmoteIdsAsync` (`:292-331`); Harness `GetEmoteLifetimesAsync`/`GetRowsAsync` (`:379-437`, über Sets summiert je `(EmoteId, Date)`); `LoadNameTwinSetIdsAsync` (`:445-500`).
- `EmoteSetStatusService.cs:36` `occupiedSlots` zählt **Zeilen**, ein Duplikat belegt zwei Plätze; `FindDuplicateNamesAsync` (`:64-97`).
- Bänder, Rang, „nie benutzt", Pareto, „Neu", Export: Frontend aus `/totals`. `merge-set-view.ts:36-125` (aktive Ansicht `slotCount: 1` `:41-56`; nicht-aktiv gruppiert nach `sevenTvEmoteId` `:63-72`, `group[0].name` `:100`, Klasse 3 je ID `:116-123`). `sevenTvEmoteId` ist der seitenweite Schlüssel: `usage-stats-page.ts:1396`, `:1441`, `:1460`, `:1548`, `:1634`, `:1706`, `:1713`, `:1886`, `:1907`, `:1951-1980`, `:1990-1993`, `:2029`, `:2048`, `:2824`, `:2829`, `:2859`, `:2918`, `:3031-3062`, `:3130-3134`, `:3198-3218`; Template `track emote.sevenTvEmoteId` (`.html:784`), Slot-Text (`:821-835`, `:864-888`, `:423-429`, `:1073-1076`, `:1097`, `:1103-1109`). Suche `nameRegex.test(item.emoteName)` (`emote-usage-filter.ts:71`) — `hob44` findet nichts.
- Drilldown `emote-drilldown-dialog.ts:39-54`, `:295`, `:345-352`; Cache `usage-stat.service.ts:92`. Export `usage-export.ts:15-26`, `:92-93`; `import-source-parser.ts:66-77`; `usage-export-purposes.ts:58-67`. i18n `usageStats.setView.slots` (`en.json:844`, `de.json:844`), `usageStats.slots.occupied` (`:834`), `emoteCount` (`:676-678`).
- Tests, die das heutige Bild festnageln: `merge-set-view.spec.ts` (8), `usage-stats-page.spec.ts` (215; AK 58 `:3678-3760`), `usage-atlas.e2e.spec.ts:1224-1228`, `mocks.ts:766-793`; Backend `SevenTvSyncServiceDuplicateEmoteIdTests.cs` (2), `ChatLogBackfillServiceTests*.cs` (49), `ChatLogBackfillBlockCounterTests.cs` (14), `UsageStatFlushServiceTests.cs` (17), `UsageStatQueryServiceTests.cs` (74), `EmoteSetStatusServiceTests.cs` (21), `EmoteNameMatchingTests.cs` (24), `EmoteMatchCacheTests.cs` (12), `SevenTvDispatchParserTests.cs` (11), `SevenTvSyncServiceTests.cs` (66), `VoteSessionServiceTests.cs` (37), `VoteSessionQueryServiceTests.cs` (32), `EmoteUsageCounterTests.cs` (13), `ReplayDayCounterTests.cs` (24), `EmoteServiceTests.cs` (55).

### 2.6 Dokumente, die diese Spec ändert oder ablöst

- Backfill-Spec: `:153` (PK, D36), `:226` (§3.1 „Duplicate ids"), `:316-317` (§4.3), `:348` (§4.4), `:558` (D36), **AC 26** (`:601`), `:614` (Testplan), `:779` (Kind 3); D11, D25 sinngemäß.
- DECISIONS 2026-10-08 (`:773-795`, „the second alias is still not countable … `AlgorithmVersion`") — ergänzt durch den neuen Eintrag (D20). DECISIONS 2026-09-20 (`:8471-8560`): Präzedenz Wartungsfenster. DECISIONS 2026-08-03 (`:14968`): `FirstSeenAt` aus v4 `addedAt`, Korrektur bei Abweichung — wird zur Regel je Slot.
- `docs/Architectur.md:242-244`, `:284-298` (UsageStat-Schema, veraltet) · `docs/Operations.md:854-935` (Backfill-Rollback) · `docs/Feature-Ideen-2026-08-01.md` A11 (`:35`, `:353`).

## 3. Entscheidungen

Betreiber-Entscheidungen (D0–D4, D7) und Orchestrator-Vorgaben (O1–O6) sind bindend; D10 ff. sind die technischen Entscheidungen dieser Spec mit verworfener Alternative. „(R3)"/„(R4)" = in der jeweiligen Fassung geändert oder neu.

| # | Entscheidung | Begründung / verworfene Alternative |
|---|---|---|
| **D0** (Betreiber) | Zähl-Einheit = Slot (Name im Set), nicht die 7TV-ID. Nutzung je Name gehalten und gezeigt; Summe = Summe der Slots. Live **und** Backfill. Suche findet jeden Namen. | Issue #365. |
| **D1** (Betreiber) | **Zwei Karten.** Jeder Slot eine Karte mit eigenem Namen, Zahl, „Neu"/„Im Set seit" (7TV-Datum des Slots), Band, Rang, „nie benutzt"; kurzer Hinweis auf die anderen Namen desselben Emotes. | Eine Karte mit Aufschlüsselung verworfen. |
| **D2** (Betreiber) | Bestehende `UsageStats`-Zeilen eines Emotes mit mehreren Slots gehören je `EmoteSetId` dem Slot mit dem **ältesten** 7TV-Datum dieses Sets. Die Historien anderer Sets bleiben getrennt; weitere Slots beginnen bei 0. | Umsetzung in D12; Nutzung ist bereits je Set abgegrenzt. |
| **D3** (Betreiber) | **Votes und Tags bleiben je Emote (7TV-ID).** Ballot zeigt alle Namen („ome44 / hob44"); eine Slot-Karte für einen Vote fügt den Emote einmal hinzu; Löschen aus einem Vote-Ergebnis entfernt alle Slots; Tags an `SevenTvEmoteId`. | — |
| **D4** (Betreiber, R3) | **Die Identität eines Slots ist das autoritative REST-`added_at` seines 7TV-Eintrags; der Name ist ein Attribut.** Schlüssel `(EmoteId, AddedAt)` mit `AddedAtFromRest = true`. Namen entscheiden Identität **nur** dort, wo kein autoritatives Datum existiert (D11 nennt diese Menge abschließend). Ein neuer Eintrag ist grundsätzlich ein neuer Slot; die Rückkehr-Regel D7 ist die eng begrenzte Ausnahme. **Gleichstand:** teilen zwei Einträge derselben ID ein `added_at`, entscheidet innerhalb dieser Gruppe der Name; eine Umbenennung in der Gruppe wird als Entfernen + Hinzufügen gelesen. | S1 (2.4): jedes `added_at` eigen, ms-genau, umbenennungsfest. Die Reihenfolge Datum-vor-Name bewahrt R2 gegen Alias-Tausch; D7 gilt erst nach allen Datums-Treffern. Heute läuft die `Emote`-Zeile über Wiederaufnahme weiter — das bleibt für die **Emote**-Zeile so (D13). |
| **D7** (Betreiber, 2026-10-10, R4) | **Wiederaufnahme führt die Historie auf derselben Karte fort.** Restore-Berichte reaktivieren die gemeldeten `SlotId`s mit deren Nutzung; sie setzen das Datum provisorisch neu, damit REST das neue `added_at` autorisiert. Bei einer externen Wiederaufnahme bindet REST zuerst alle möglichen exakten Datums-Treffer. Ein danach verbleibender datierter Eintrag übernimmt nur dann den zuletzt archivierten Slot desselben Emotes mit gleichem Namen, wenn kein aktiver Slot dieses Emotes den Namen trägt; `AddedAt` wird auf das neue REST-Datum umgeschlüsselt. Sonst entsteht ein neuer Slot bei 0. | So bleibt die Historie wie heute erhalten, während „Im Set seit"/„Neu" dem neuen Eintritt folgen. Datumstreffer zuerst schützen Tausch und Alias-Wiederverwendung im selben Abgleich. |
| **O1** | Eine Nachricht mit beiden Namen zählt +1 je Slot; Dedup je Nachricht je Slot. | `EmoteNameMatching.MatchEmoteIds` unverändert; der Map-Wert ist die Slot-ID. |
| **O2** | Importierte Zeilen exakt über den Snapshot je Slot; Gate je Slot. AC 26/D36 der Backfill-Spec abgelöst (Abschnitt 8). | — |
| **O3** | Massenlöschung je Slot über `alias` in v4 `removeEmote`; S1 bestanden; Kind 8 fährt **unsere** Mutation live (AK 36). | — |
| **O4** | Namenskollisionen **verschiedener** Emotes nicht schlechter als heute (zuerst geladener gewinnt, Tracker meldet). | `Coalesce` bleibt; Tracker/`DuplicateNames` lesen Slots (D15). |
| **O5** | EventAPI-Deltas über `old_value`; Mehrdeutiges → REST-Abgleich statt Raten. | D17. |
| **O6** | Beitrittsdatum live aus REST `added_at` je Eintrag; Push = `UtcNow` provisorisch. | D14. |
| **D10** | **Tabelle `EmoteSlots`** je Set-Eintrag mit `Name`, `AddedAt`, `AddedAtFromRest`, Archivzustand, Stempel; `Emotes` bleibt unique je `(ChannelId, SevenTvEmoteId)` (Regel 8); `UsageStats` bekommt `SlotId` **zusätzlich** zu `EmoteId`. | *Verworfen:* `Name`-Spalte auf `UsageStats`; `Emotes` unique je `(ChannelId, SevenTvEmoteId, Name)` (bricht Regel 8, Votes, Tags, Leaves). `EmoteId` bleibt für Harness, Ballot-Summen, Namensvetter, Bot-Daten, Retention, Kaskade. |
| **D11** (R3/R4) | **Namens-Identität nur ohne autoritatives Datum — abschließende Menge:** (a) Migrations-Seeds je `(EmoteId, EmoteSetId)` bis zum Settle; (b) Slots aus einem Dispatch-Push bis zum nächsten REST-Abgleich; (c) Restore-Slots bis zum nächsten REST-Abgleich; (d) Slots einer Backfill-Enqueue für einen Preview-Eintrag ohne `addedAt`; (e) Live-Einträge, denen das v4-Overlay kein Datum liefert. Für diese gilt: gleicher Name = derselbe Eintrag; der nächste datierte REST-Abgleich macht sie autoritativ. **Ein provisorischer Slot wird nie gelöscht** (Löschlauf lässt ihn aus, 9.3). | Codex R2 Befund 1/5; R4 macht die Seed-Menge je UsageStats-Set getrennt. |
| **D12** (R4) | **D2 in zwei Schritten, ohne Zählung dazwischen; Migration und Abgleich sind je Set abgegrenzt.** M1 legt je vorhandenem Paar `(EmoteId, UsageStats.EmoteSetId)` einen **Seed-Slot** an (`AddedAt = null`, `AddedAtFromRest = false`, `SeedUnsettled = true`) und hängt nur die Zeilen dieses Sets daran; `Emote.FirstSeenAt` wird nicht über Sets kopiert. Der Slot bleibt dem Emote im Kanal zugeordnet; `UsageStats.EmoteSetId` hält die Zählung getrennt. Ein nicht abgeglichener Seed zählt nicht; nur ein nicht abgeglichener Seed des aktiven Sets blockiert dessen Dispatch mit `AmbiguousDelta`. **Abgleich je Set** = dessen Seed wird auf den ältesten datierten Eintrag dieses Sets umgeschlüsselt; nur dessen Nutzungszeilen bleiben daran, die übrigen Einträge beginnen bei 0. Ist derselbe autoritative `(EmoteId, AddedAt, Name)`-Schlüssel schon vorhanden, werden nur die Ziel-Set-Zeilen an dessen `SlotId` gebunden und der nun leere Seed archiviert; andere Sets bleiben unverändert. REST gleicht den Seed beim ersten vollständigen datierten Abgleich dieses Sets ab (6.3), Backfill bei einer datierten Vorschau eines nicht aktiven Sets (8). Fehlt ein Datum, wartet REST; Enqueue antwortet 409 `backfill_slots_unsettled` ohne Schreiben. | Die schreibgeschützte Analyse in R4 ergab: die bisherige Migration hätte Zeilen aller Sets an den Seed des aktiven Sets gehängt. Separate Seeds und ein Abgleich je Set schließen das; eine eigene Slot-Identität je Set würde D4 und die Slot-Verträge unnötig verbreitern. Preis: ein Set zählt nach seinem datierten REST-Abgleich bzw. seiner datierten Backfill-Vorschau. |
| **D13** | **`Emote.Name`/`FirstSeenAt` abgeleitet:** Name und Datum des aktiven Slots mit kleinstem `AddedAt` (`null` zuletzt, Gleichstand ordinal); archivierte Emotes behalten den letzten Stand. Die `Emote`-Zeile wird bei Wiederaufnahme weiter un-archiviert (`LastEnteredSetAtUtc`, `IsPlaceholder` wie heute). | Konsumenten: Tags `currentNames`, `EmoteListQueryService`, Admin, Null-Session, Harness. |
| **D14** | **Datum je Slot mit Herkunft:** `GqlSetEntriesQuery` selektiert `alias`; Overlay nach `(emoteId, alias)` im abgefragten Set. REST-Datum autoritativ; Push/Restore-Stempel `UtcNow` provisorisch; REST ersetzt provisorische Daten durch das autoritative und ändert ein autoritatives bei Abweichung nie — außer der ausdrücklich definierten D7-Wiederaufnahme, die dieselbe `SlotId` auf das neue Datum umschlüsselt. | O6; Datums-Identität bleibt geschützt, D7 ist die festgelegte Ausnahme bei Wiederaufnahme. |
| **D15** | **Match-Map `Name → SlotId`** aus aktiven, gesettelten Slots des aktiven Sets; `Coalesce` unverändert; `UsageCounterKey(SlotId, EmoteSetId)`; Tracker/`DuplicateNames` über Slots (Gruppe nach `Name` mit > 1 **verschiedenen** `EmoteId`). | O4; `EmoteNameMatching` textgleich, BCL-pur. |
| **D16** (R3/R4) | **Identitätsregel des REST-Abgleichs** (6.2): datierte Einträge werden zuerst über `(EmoteId, AddedAt)` gebunden (aktiv oder archiviert); danach gilt D7 für verbleibende datierte Einträge. Namensbindung für provisorische/undatierte Einträge bleibt D11. Ein unerklärter Eintrag ist neu; ein unerklärter autoritativer Slot ist entfernt. | Codex R2 Befund 1 (Tausch A→C, B→A); D7 greift erst nach exakten Treffern. |
| **D17** (R3/R4) | **EventAPI je `(Id, Name)`** — der Dispatch trägt kein Datum. Push eines Namens ohne aktiven oder archivierten Namensvetter → provisorischer Slot; Push mit passendem archiviertem Slot → `AmbiguousDelta` und REST-Abgleich, der D7 mit Datum anwenden kann. Rename → aktiver Slot `(Id, OldName)` in place; Pull → aktiven Slot `(Id, Name)` archivieren. Mehrdeutige Deltas führen zu `ResyncChannelAsync`, nichts wird geschrieben. Leave nur, wenn der letzte aktive Slot geht. | O5; Dispatch-Datum reicht nicht für die D7-Wiederaufnahme. |
| **D18** | **`UsageStats`-Schlüssel:** unique covering `(SlotId, EmoteSetId, Date) INCLUDE (UseCount)` als `ON CONFLICT`-Ziel; Emote-Index bleibt nicht-unique covering. | Zweite Indexkopie; Emote-Index für die Emote-Leser. |
| **D19** (R3/R5) | **Drei Migrationen, ein Fenster.** M1 additiv (Kind 1). M2 nicht-additiv im PR aller UsageStats-Schreiber: Flush, Zähler, Map sowie Backfill-Insert mit SlotId aus dem Snapshot (Kind 3). Kind 3 enthält außerdem die einfache Slot-Bindung und Snapshot-SlotId-Befüllung für den bisherigen Eintrag je Emote. M3 und die Erweiterung auf mehrere Slots je Emote folgen mit dem Backfill-Schreiber in Kind 5. Prod wendet alle drei im Wartungsfenster an (api/worker gestoppt, Backup, von Hand, beide Images gemeinsam hoch). | Codex R1 Befund 8, R2 Befund 7, Runde 3 Befund 7. AK 44, 56. |
| **D20** | **Harness unverändert, `harness-3`.** Liest `EmoteLifetimeDto` je Emote (D13) und Summen je `(EmoteId, Date)`; Abweichung (ein Alias) als Grenze des geschlossenen Instruments in DECISIONS. | Backfill nutzt keinen Harness-Code. **Vom Betreiber bestätigt (2026-10-10).** |
| **D21** (R3/R5) | **Frontend-Schlüssel slotId**; Klasse-3-Zeile `live:${sevenTvEmoteId}#${addedAt ?? name}`. **Bindung je Versuch:** beforeStep bindet über (sevenTvEmoteId, addedAt) an eine frische Live-Liste. Hat das Emote dort mindestens zwei Einträge, wird die Liste vor jedem Mutationsversuch einschließlich Retries neu gelesen; bei höchstens einem Eintrag gilt weiter die Erneuerung nach mehr als 5 s. Abweichender Name → renamed, fehlender Eintrag → departed, provisorische Zeilen werden nie gelöscht. Gesendet wird nur der soeben verifizierte Alias. | Codex R1/R2 und Runde 3 Befund 1. AK 38, 51; D33 begrenzt sich auf den Request-Umlauf nach diesem Read. |
| **D22** | **`mergeSetView`** joint datierte Zeilen über `(sevenTvEmoteId, addedAt)`, provisorische über den Namen; alles Unerklärte ist `'left'` bzw. Klasse 3 je Eintrag. `slotCount`/`aliases` entfallen; neu `siblingNames`. | Keine 1:1-Vermutung mehr nötig: die Live-Liste trägt `addedAt`. |
| **D23** | **Geschwister-Hinweis:** Scrim-Glyph `+{{n}}`, `title`, Inspector, Sidecar, `aria-label` mit `usageStats.setView.alsoAs`; ersetzt `×{{slotCount}}`/`setView.slots`. | UI-Designsprache §2.5 (wie `≈`). |
| **D24** | `/daily` je Slot (`slotId`) oder je Emote (`emoteId`, Summe); `/series` eine Entry je Slot (+`slotId`, `name`, additiv); `/totals` eine Zeile je Slot (+`slotId`, `firstSeenAtFromRest`, `siblingNames`). | Abschnitt 5. |
| **D25** | Export eine Zeile je Slot; `seven_tv_emote_id` nicht mehr eindeutig; `import-source-parser`/`toImportRow` dedupe nach ID (erste Zeile). Voting-Export unverändert. | Import-Queue bleibt je ID (K2). |
| **D26** | Ballot `EmoteNames` (Null-Session live; Set-Session aus `VoteSessionEmotes.NamesAtCreation text[]`), `EmoteName` = erstes Element; Vote-Erstellung dedupliziert IDs. | D3. |
| **D27** | `OccupiedSlots` = aktive Slots aktiver Emotes im aktiven Set; `pendingRemovalSlots` = markierte Live-Zeilen. | Belegung stimmt mit `emote_count` überein. |
| **D28** (R3/R4/R5) | **Backfill-Slots:** Für aktive-Set-Vorschauen bindet Enqueue vor Snapshot-Erstellung in derselben Reihenfolge wie REST: alle exakten (EmoteId, AddedAt)-Treffer, danach D7. Es verwendet dieselbe Transaktion mit SELECT FOR UPDATE auf der owning Emote-Zeile wie REST und Restore-Berichte. Nichtaktive Vorschauen behalten die genaue Datumsbindung und das bisherige Archivverhalten. Der Snapshot trägt SlotId und CreatedSlot. | Damit kann ein Preview-Eintrag keinen neuen Slot vor dem D7-Treffer erzeugen. AK 22, 27, 53. |
| **D29** | Tag-Alias-Snapshot (K6) = ältester Eintrag; `CurrentName` = `Emote.Name`. | D3. |
| **D30** | `GET /api/channels/{c}/emotes` bleibt eine Zeile je Emote. | Import-Vergleich je ID. |
| **D31** | **Rollback über `Down` im Fenster** mit Guards (M2: > 1 Zeile je `(EmoteId, EmoteSetId, Date)`; M3: > 1 Snapshot-Zeile je `(RunId, EmoteId)`); Kollaps-SQL geht voraus (12.2). | Präzedenz 2026-09-20, D40 Backfill. |
| **D32** (R3/R4/R5) | **Berichte je Slot mit Lebenszyklus-Identität.** sync-deleted und sync-restored tragen { sevenTvEmoteId, slotId, alias, addedAt }; addedAt ist das autoritative Datum der gemeldeten Entry-Generation. Im Ursprungskanal validiert der Server Eigentümer und Generation. Ein Datums-Konflikt ist ein veralteter Bericht und ein erfolgreicher No-op; eine fremde slotId im Ursprungskanal bleibt 400. Delete archiviert nur bei exakter Generation. Restore reaktiviert nur die passende archivierte Generation und setzt AddedAt einmalig provisorisch. Ist der Slot bereits aktiv oder hat sich seine Generation geändert, antwortet Restore erfolgreich ohne Mutation oder Datums-Reset. Entries-Berichte sperren die owning Emote-Zeile mit SELECT FOR UPDATE wie REST und Enqueue. Bei Shared-Set-Fan-out wird SlotId nur im Ursprungskanal geprüft; andere Kanäle lösen über (SevenTvEmoteId, AddedAt) genau einen lokalen Slot auf, sonst warten sie auf REST. Wiederholungen sind No-ops; ohne Entries bleibt das Altverhalten. Restore-Retries senden dieselbe slotId und das ursprüngliche AddedAt. | Schließt verspätete Delete-Berichte, REST-vor-Report und verlorene Restore-Antworten; die Historie bleibt auf D7s Slot. AK 41–42, 52, 54–55. |
| **D33** (R3, neu; R5 präzisiert) | **Einziges akzeptiertes destruktives Restrisiko der alias-adressierten Löschung:** 7TV kennt keinen Selektor über added_at (2.4). Bei Emotes mit mindestens zwei Live-Einträgen erfolgt ein frischer Read unmittelbar vor jedem REMOVE; bei einem Eintrag wird die Liste weiterhin höchstens 5 s wiederverwendet. Zwischen dem letzten Read und dem Request kann ein zweiter Editor einen anderen Eintrag desselben Emotes auf den gesendeten Alias umbenennen; dann entfernt 7TV den falschen Eintrag. Folge: ein nicht markierter Eintrag ist entfernt. **Erkennung und Reparatur:** die bestehende Nachlese des Laufs prüft alle gelöschten Zeilen: fehlt ein nicht gemeldeter Eintrag (id, addedAt) in der Live-Liste, meldet der Dock usageStats.delete.unexpectedRemoval mit Alias und Restore-Angebot (die Historie liegt auf dem archivierten Slot; die Wiederaufnahme ist ein neuer Eintrag, D4). | Ein Request-Umlauf bleibt ohne 7TV-Selektor unvermeidbar. R5 entfernt die kumulative Veraltung über mehrere Schritte; D33 bleibt das einzige akzeptierte destruktive Restrisiko (Abschnitt 14). |

## 4. Datenmodell

### 4.1 `EmoteSlots` (neu) — Entität `EmoteSlot` in `EmotePurge.Core.Entities`

| Spalte | Typ (Postgres) | Bedeutung |
|---|---|---|
| `Id` | `text` PK | interner Guid-String wie `Emote.Id` |
| `EmoteId` | `text NOT NULL` FK → `Emotes.Id` `ON DELETE CASCADE` | der Emote (Regel 8 bleibt dort) |
| `Name` | `text NOT NULL` | der Alias, wie 7TV ihn listet (ordinal); **Attribut, keine Identität** |
| `AddedAt` | `timestamptz NULL` | 7TV `added_at` des Eintrags (ms-genau, gespeichert wie geliefert); provisorisch `UtcNow` (D14); `null` = unbekannt, **nie** „neu" |
| `AddedAtFromRest` | `boolean NOT NULL DEFAULT false` | `true` = `AddedAt` ist das autoritative 7TV-Datum und damit die Identität (D4); `false` = provisorisch oder `null` (D11) |
| `IsArchived` | `boolean NOT NULL DEFAULT false` | `true` = nicht (mehr) im aktiven Set; ein Backfill-erzeugter Slot ist `true` mit `ArchivedAt = NULL` („war nie drin") |
| `ArchivedAt` | `timestamptz NULL` | gestempelt beim Archivieren eines aktiven Slots |
| `LastSyncedAt` | `timestamptz NOT NULL` | gestempelt bei Anlage, Umbenennung, Archivierung, Autorisierung des Datums |
| `SeedUnsettled` | `boolean NOT NULL DEFAULT false` | D12-Marke der Migrations-Seeds; nach dem Einschwingen bedeutungslos, bleibt |

Indizes: **partieller Unique-Index** `IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative` auf `(EmoteId, AddedAt, Name) WHERE "AddedAtFromRest"` (Identität; `Name` im Schlüssel nur für den Gleichstand D4 — bei einer Umbenennung ändert sich die Zeile, nicht der Treffer); nicht-unique `IX_EmoteSlots_EmoteId_Name` (FK-Index, Namensbindung provisorischer Slots). **Kein** Unique-Index auf Namen: 7TV hält Aliase im Set eindeutig, und ein Tausch zweier Namen in einem Abgleich würde einen Namensindex mitten im `SaveChanges` verletzen.

Ein `SeedUnsettled`-Slot ist während der Migration genau einem `EmoteSetId` zugeordnet: den an ihn gehängten `UsageStats`-Zeilen. M1 erzeugt getrennte Seeds je `(EmoteId, EmoteSetId)`; das Set steht nicht dauerhaft auf `EmoteSlots`, weil die Slot-Identität dem Emote im Kanal zugeordnet bleibt (D12).

Invarianten: ein aktiver Slot gehört zu einem aktiven Emote; ein Emote mit aktivem Slot ist aktiv (die Umkehr — Emote aktiv ⇒ Slot aktiv — stellt der nächste REST-Abgleich her, 5.7). Zwei autoritative Slots desselben Emotes mit gleichem `(AddedAt, Name)` gibt es nicht.

### 4.2 `UsageStats` (geändert)

| Änderung | Migration | Definition |
|---|---|---|
| neue Spalte | M1 | `SlotId text NULL` FK → `EmoteSlots.Id` `ON DELETE CASCADE`, in M1 aus dem Seed befüllt |
| neuer Index | M1 | `IX_UsageStats_SlotId_EmoteSetId_Date` auf `(SlotId, EmoteSetId, Date) INCLUDE (UseCount)`, in M1 nicht-unique |
| Schlüsseltausch | M2 | `SlotId NOT NULL`; Slot-Index **unique** (`ON CONFLICT`-Ziel); `IX_UsageStats_EmoteId_EmoteSetId_Date` **nicht-unique** |
| unverändert | — | `Id`, `EmoteId`, `Date`, `UseCount`, `BotUseCount`, `SharedChatUseCount`, `EmoteSetId`, `Source` |

Invariante (ab M2): eine Zelle `(SlotId, EmoteSetId, Date)` ist live **oder** importiert; zwei Slots eines Emotes können am selben Tag je eine Zeile haben; `SUM(UseCount)` über Slots bleibt auf das konkrete `EmoteSetId` begrenzt. Beim Settle bleiben die anderen Sets' Zeilen auf ihren eigenen Seeds/Slots.

### 4.3 `ChatLogBackfillRunEmotes` (geändert)

| Änderung | Migration | Definition |
|---|---|---|
| neue Spalten | M1 | `SlotId text NULL` (kein FK, D25 der Backfill-Spec), aus dem Seed desselben `EmoteSetId` befüllt; `CreatedSlot boolean NOT NULL DEFAULT false` |
| PK-Tausch | M3 | `SlotId NOT NULL`; PK `(RunId, SlotId)` |
| unverändert | — | `EmoteId`, `SevenTvEmoteId`, `Name`, `AddedToSetDay` (jetzt je Slot), `CreatedRow`; Index `(RunId, SevenTvEmoteId)` |

### 4.4 `VoteSessionEmotes` (M1, additiv)

`NamesAtCreation text[] NULL` — alle Aliase des Emotes im Set der Session zum Erstellzeitpunkt nach `addedAt`; `NameAtCreation` bleibt = erstes Element.

### 4.5 `Emotes` (Semantik)

`Name`/`FirstSeenAt` abgeleitet (D13). Neue Navigation `ICollection<EmoteSlot> Slots` (`EmoteSlot.Emote`), damit Abgleich und Dispatch die Slots eines Kanals mit **einem** `Include` laden.

### 4.6 Kaskaden

`Channel → Emote → EmoteSlot → UsageStat` und `Emote → UsageStat` (beide FKs cascade). Purge, Retention (`DataRetentionService.cs:435-446` zählt über `EmoteId`), Account-Löschung, Merge-Verweigerung (`ChannelIdentityService.cs:897-915`) unverändert. Snapshot-Zeilen fallen mit dem Run.

## 5. API-Verträge

### 5.1 `GET /api/channels/{c}/usage-stats/totals?from&to[&emoteSetId]`

Eine Zeile **je Slot**:

```
EmoteUsageContextDto(
  string EmoteId, string SlotId, string EmoteName /* Slot-Name */, string SevenTvEmoteId, string ImageUrl,
  int TotalUseCount, DateOnly? LastUsedDate, int PreviousWindowUseCount,
  DateTime? FirstSeenAt /* Slot.AddedAt */, bool FirstSeenAtFromRest,
  bool IsArchived /* Slot oder Emote archiviert */,
  IReadOnlyList<string> NameTwinEmoteSetIds /* je Slot-Name */,
  IReadOnlyList<string> SiblingNames /* andere aktive Slots desselben Emotes in dieser Ansicht, nach AddedAt */)
```

Aktive Ansicht: jeder aktive Slot jedes aktiven Emotes, mit 0 aufgefüllt (unsettled Seeds sichtbar, nur nicht zählend). Eine D7-Wiederaufnahme erscheint auf derselben Karte mit deren bisherigen Zählungen; das neue REST-Datum setzt nur „Neu"/„Im Set seit" zurück. Nicht-aktive Ansicht: jeder Slot mit ≥ 1 `UsageStat` unter dem Set (archivierte eingeschlossen, E23). Aggregation `GroupBy(u => u.SlotId)` nach Slot-ID-Liste (Regel 10). Für beide Ansichten zählt nur das angefragte `EmoteSetId`.

### 5.2 `GET …/usage-stats/daily?slotId=|emoteId=&from&to[&emoteSetId|setScope=all]`

`slotId` → Serie des Slots (`EmoteUsageSeriesDto` + `SlotId`, `EmoteName` = Slot-Name); `emoteId` → Summe über die Slots im angefragten Set (Ballot-Drilldown, D3; `EmoteName` = `Emote.Name`). Beide gesetzt → `slotId` gilt; keiner → 400 `emote_id_empty`; Slot ohne Nutzung im angefragten Set → leere Serie.

### 5.3 `GET …/usage-stats/series?from&to[&emoteSetId]`

Eine Entry **je Slot**: `EmoteSeriesEntryDto(string SevenTvEmoteId, string EmoteId, string SlotId, string Name, IReadOnlyList<int[]> Days)` — additiv, Kodierung bytegleich. Der Client joint über `SlotId`.

### 5.4 `GET /api/channels/{c}/emotes/active-set`

`OccupiedSlots` = aktive Slots aktiver Emotes (D27); `DuplicateNames` über Slots des aktiven Sets (D15).

### 5.5 Votes

`VoteSessionResultDto` + `IReadOnlyList<string> EmoteNames` (D26). `CreateVoteSessionRequest` unverändert; der Server dedupliziert `emoteIds`/`sevenTvEmoteIds`.

### 5.6 Backfill

`ChatLogBackfillRunDto.emoteCount` = Snapshot-Zeilen (Slots; i18n „Einträge"). `POST …/backfill` neu **409 `backfill_slots_unsettled`** (D12: ein unsettled Seed gehört zum aktiven Set **oder** seine Preview-Einträge sind nicht alle datiert); Text „Die Emotes dieses Kanals werden gerade umgestellt, in einer Minute erneut versuchen." Neuer `ApiErrorCodes.BackfillSlotsUnsettled`, Eintrag in `api-error.ts` und beiden Locales (Regel 7).

### 5.7 Set-zentrische Berichte (D32, R5)

`SyncInSetRequest(IReadOnlyList<string>? SevenTvEmoteIds, string? ExpectedChannelName, string? TargetOwnerTwitchId = null, IReadOnlyList<SyncInSetEntry>? Entries = null), SyncInSetEntry(string SevenTvEmoteId, string Alias, string? SlotId, DateTimeOffset? AddedAt)`. Bei gesetzter SlotId ist AddedAt erforderlich und enthält das autoritative Datum der gemeldeten Generation.

Der Api-Endpoint validiert den Bericht und delegiert an `IEmoteService` in Core/Services; `EmoteService` in Infrastructure/Services führt die Änderung aus. Der Handler verwendet weder `AppDbContext` noch `IConnectionMultiplexer` direkt.

- **Ursprungskanal und Shared-Set-Fan-out:** Der bereits über ExpectedChannelName/TargetOwnerTwitchId validierte Kanal ist der Ursprung. SlotId wird nur dort auf Zugehörigkeit zum Emote geprüft. Für jeden weiteren Treffer-Kanal des aktiven Shared-Sets wird genau ein lokaler Slot über (SevenTvEmoteId, AddedAt) aufgelöst; fehlt ein eindeutiger Treffer, wird dort nichts geschrieben und der nächste REST-Abgleich übernimmt. Eine für den Ursprung gültige SlotId ist in Geschwister-Kanälen kein Fehler.
- **sync-deleted mit Entries:** Je Ursprungskanal und Eintrag Slot-Eigentümer und AddedAt prüfen. Fremde SlotId im Ursprung → 400 invalid_request; bei eigener SlotId mit abweichendem aktuellem AddedAt veralteten Bericht erfolgreich ignorieren. Bei exakter Generation Slot archivieren (ArchivedAt = now). Ein bereits archivierter Slot ist ein No-op. Emote archivieren und Leave nur, wenn diese Mutation seinen letzten aktiven Slot entfernt; stale oder wiederholte Berichte erzeugen keine Leave.
- **sync-restored mit Entries:** Slot-Eigentümer und Generation prüfen. Fremde SlotId im Ursprung → 400; bei abweichender Generation oder bereits aktivem Slot erfolgreich ohne Änderung antworten. Nur eine passende archivierte Generation wird reaktiviert (ArchivedAt = null, AddedAt = UtcNow, AddedAtFromRest = false); UsageStats bleiben gebunden. Emote un-archivieren (LastEnteredSetAtUtc, IsPlaceholder = false wie heute). Der nächste REST-Abgleich autorisiert das neue Datum (6.2). So setzen verlorene Antwort-Retries und ein vorangegangener D7-REST-Abgleich kein Datum zurück.
- **Ohne `Entries`:** `sync-deleted` wie heute (`EmoteService.cs:268-297`); `sync-restored` un-archiviert nur die `Emote`-Zeile, legt keinen Slot an (Invariante 4.1, Umkehr) — der nächste Abgleich legt die Einträge datiert an. Audit-Details tragen `entryCount`.
SevenTvEmoteIds enthält die distinct IDs der Entries; SlotId fehlt → Eintrag wie bisher ignorieren, gesetzte SlotId ohne AddedAt → 400. Ein stale no-op antwortet 2xx. sync-imported unverändert.

### 5.8 Unverändert

`GET /emotes` (D30), Tag-Routen (D3), `/import-coverage`, Set-Preview-Routen, Guid-geschlüsselte Alt-Berichte.

## 6. Live-Zählung und REST-Abgleich

### 6.1 Zählpfad

1. `RefreshMatchCacheAsync` lädt aktive, gesettelte Slots des aktuellen `EmoteSetId` aktiver Emotes, `Coalesce` → `NameToSlotId`, Tracker, `ReplaceChannel`. Unsettled Seeds des aktiven Sets fehlen (D12); eine `Information`-Zeile nennt ihre Zahl, wenn > 0.
2. `TwitchChatManager.OnMessageReceived`: unverändert bis auf den Dictionary-Namen; `usageCounter.Increment(slotId, snapshot.EmoteSetId, category)`; `ome44 hob44` → +1 je Slot (O1).
3. `UsageStatFlushService.FlushAsync`: Gültigkeitsfilter über `EmoteSlots` (`Id`, `EmoteId`, `Emote.Channel.ChannelName`); `INSERT … ("EmoteId","SlotId","EmoteSetId","Date",…) ON CONFLICT ("SlotId","EmoteSetId","Date") DO UPDATE …`.
4. `Worker.cs`/`WarmMatchCacheIfEmptyAsync` lesen `NameToSlotId.Count` (reine Umbenennung, alle vier Konsumenten in einem PR, D19).

### 6.2 `ReconcileAsync` je Slot (ersetzt K1)

Eingabe: aktives `EmoteSetId`; je Live-Eintrag `Id`, `Name`, `ImageUrl`, `AddedToSetAt` aus dem v4-Overlay nach `(Id, Name)` (autoritativ, wenn vorhanden). Keine Dedup. Je `SevenTvEmoteId`-Gruppe *G*:

1. **Emote-Zeile** wie heute finden/anlegen/un-archivieren (`UpsertEmoteWithSlots`); `ImageUrl` aus dem ersten Eintrag.
2. **Seed-Abgleich je Set** (6.3): finde den noch nicht abgeglichenen Seed des Emotes, an den die bestehenden `UsageStats`-Zeilen des aktiven `EmoteSetId` gehängt sind; Seeds anderer Sets bleiben unangetastet. Nur ein noch nicht abgeglichener Seed dieses aktiven Sets verhindert dessen Zählung/Dispatch.
3. **Datums-Identität (D4/D16):** je datiertem Eintrag *n* autoritative Slots *s* mit `s.AddedAt == n.AddedToSetAt` (aktiv oder archiviert). Genau einer → derselbe Eintrag; `s.Name := n.Name`, archiviert → un-archivieren und `ArchivedAt := null`. Bei Gleichstand bindet gleicher Name, sonst keiner.
4. **Wiederaufnahme (D7), erst nach allen Datumstreffern:** je verbleibendem datiertem Eintrag *n* ohne aktiven Slot gleichen Namens den zuletzt tatsächlich archivierten Slot desselben Emotes mit `s.Name == n.Name` und `s.ArchivedAt != null` wählen (`ArchivedAt` absteigend, bei Gleichstand `Id` ordinal aufsteigend). Diesen Slot mit derselben `Id` auf `n.AddedToSetAt` umschlüsseln, `AddedAtFromRest = true`, un-archivieren, `ArchivedAt := null` und Historie behalten. Gibt es einen aktiven Slot gleichen Namens, greift D7 nicht.
5. **Namens-Bindung (D11):** verbleibende Einträge binden an den aktiven provisorischen Slot gleichen Namens; ein datierter Eintrag autorisiert dessen Datum. Undatierte Einträge dürfen zusätzlich an einen aktiven autoritativen Slot gleichen Namens binden.
6. **Neue Slots:** jeder verbleibende Eintrag → neue Zeile (`AddedAt = n.AddedToSetAt`, `AddedAtFromRest = n.AddedToSetAt != null`; undatiert → provisorisch mit `AddedAt = null`).
7. **Archivierung:** jeder verbleibende aktive Slot dieses Abgleichs → `IsArchived = true`, `ArchivedAt = now`.
8. **Ableitung (D13).**

Nach allen Gruppen: Emotes ohne Live-Eintrag → aktive Slots archivieren, Emote archivieren mit b1/b2 und Leave-Beobachtung **unverändert auf Emote-Ebene** (`:1000-1052`). `changed` = Anlage, Un-Archive, Umbenennung, Archivierung; Autorisierung eines Datums und das Löschen der Marke zählen nicht.

Beispiele (AK 7, 8, 13, 47–50): (i) `S1(A,t1)`, `S2(B,t2)`; Live `[C(t1), A(t2)]` → exakte Datumstreffer, keine D7-Reaktivierung (Tausch geschützt). (ii) `S1(A,t1)` archiviert; Live `[A(t3)]` ohne aktives `A` → derselbe Slot wird auf `t3` umgeschlüsselt und behält seine Historie. (iii) Ein aktiver `A(t2)` besteht beim REST-Abgleich, zusätzlich kommt `A(t3)` → D7 greift nicht; `A(t3)` ist neu bei 0 und der nicht mehr gelistete alte Slot wird archiviert.

### 6.3 Abgleichsregel (D12), einmal je Emote-Set-Paar nach dem Deploy

Slot *s0* mit `SeedUnsettled`, dessen angehängte `UsageStats` gehören zum selben `EmoteSetId` wie Live-Gruppe *G*:

Führt ein autoritativer Zielschlüssel bereits zu einem Slot, werden nur die Usage-Zeilen dieses Sets auf dessen `SlotId` gebunden; der leere Seed wird als settled archiviert. Kein anderer Set-Bucket wird geändert.

- |G| = 1, datiert: `s0.AddedAt := n.AddedToSetAt`, `s0.Name := n.Name`, autoritativ; Marke weg. |G| = 1, undatiert: `s0.Name := n.Name`, provisorisch; Marke weg.
- |G| ≥ 2, **alle datiert**: `oldest` = kleinstes `AddedToSetAt` (Gleichstand: ordinal kleinster Name); `s0` wird auf `oldest` umgeschlüsselt (`AddedAt`, `Name`, autoritativ; Inventaränderung); existiert dieser `(EmoteId, AddedAt, Name)`-Slot bereits, werden nur die Nutzungszeilen dieses `EmoteSetId` daran gebunden. Die übrigen Einträge werden neue Slots (Schritt 5); Marke weg.
- |G| ≥ 2, **mindestens einer undatiert**: nichts anlegen, nichts archivieren, Marke bleibt, dieses Set zählt den Emote weiter nicht; `Information` je Kanal und Durchlauf („settle deferred: v4 dates missing for {Count} emotes").

Ein Seed bleibt unabhängig von Emotes Archivstatus bis zum vollständigen datierten Abgleich seines Sets unsettled; eine Backfill-Enqueue darf einen Seed eines nicht aktiven Sets mit datierten Preview-Einträgen settle (8).

### 6.4 `GetSetEntriesAsync` (ersetzt K2)

`GqlSetEntriesQuery` → `… items { alias added_at: addedAt emote { id } } …`; `SetEntriesOverlay(Dictionary<(string EmoteId, string Alias), DateTime>? AddedAtBySlot, int EntryCount)`; das Overlay gehört zum angefragten Set. Query vom Betreiber von Hand live gefahren, Antwort als Fixture `Unit/TestData/set-entries-alias.json` (AK 29). `RemoteEntryCount` unverändert.

### 6.5 Nebenläufigkeit (R5)

IsSlotKeyConflict (Konstante IX_EmoteSlots_EmoteId_AddedAt_Name_Authoritative) behandelt weiterhin gleiche (EmoteId, AddedAt, Name)-Konflikte. Zusätzlich sperren REST-Abgleich, Backfill-Enqueue und Entries-Berichte innerhalb ihrer Transaktion die owning Emote-Zeile mit SELECT FOR UPDATE und lesen danach die vollständigen Slotgruppen erneut. Diese bestehende Emote-Zeile serialisiert damit jede autoritative (EmoteId, AddedAt)-Auflösung desselben Emotes, auch wenn zwei Beobachtungen verschiedene Aliase enthalten. Eine einzelne aktuelle Live-Identität bindet an genau einen vorhandenen Slot derselben AddedAt-Generation und aktualisiert dessen Name; bei mehreren gespeicherten Kandidaten oder mehreren Live-Einträgen mit gleichem Datum entscheidet der Name gemäß D4. Echte Zeitstempel-Gleichstände bleiben daher getrennte Slots. Die Unique-Constraint auf (EmoteId, AddedAt, Name) allein ist keine Konkurrenzkontrolle über Umbenennungen. AK 27.

## 7. EventAPI

### 7.1 Parser

```
SevenTvEmoteSetDelta(
  IReadOnlyList<SevenTvEmote> Pushed,            // Name = value.name
  IReadOnlyList<SevenTvEmoteRename> Updated,     // (Id, OldName?, NewName?, ImageUrl) aus old_value.name / value.name
  IReadOnlyList<SevenTvPulledEntry> Pulled)      // (Id, Name?) aus old_value
{ IReadOnlyList<string> PulledIds => …Distinct(); bool IsEmpty => …; }
```

`TryMapEmoteChange` liest für `updated` auch `old_value.name`; `TryGetPulledEmoteId` wird `TryGetPulledEntry`. Fehlende Namen → `null`, kein Wurf.

### 7.2 `ApplyEmoteSetUpdateAsync` je `(Id, Name)` (D17)

`existing` mit `Include(e => e.Slots)`. Plausibilität über aktive `(Id, Name)` nach Anwendung. Betroffener Emote mit unsettled Seed des aktiven Sets → `AmbiguousDelta`.

| Change | Regel | mehrdeutig |
|---|---|---|
| `pushed` `(Id, value.name)` | aktiver Slot gleichen Namens vorhanden → nichts (unser REST-Pass war schneller); archivierter Slot gleichen Namens vorhanden → nichts schreiben, `AmbiguousDelta` und REST-Abgleich für D7; sonst **neuer** provisorischer Slot (`UtcNow`, `FromRest = false`); Emote anlegen/un-archivieren wie heute | `value.name` fehlt |
| `updated` `(Id, old → new)` | aktiver Slot `(Id, OldName)` → `Name := NewName` in place (autoritativ oder provisorisch) | Namen fehlen; Slot nicht gefunden; `NewName` = anderer aktiver Slot derselben ID (7TV-Quirk) |
| `pulled` `(Id, old_value.name)` | aktiven Slot `(Id, Name)` archivieren; letzter aktiver → Emote archivieren, `IsPlaceholder = false` | `Name` fehlt bei ≥ 2 aktiven Slots (bei genau einem: diesen) |

`AmbiguousDelta` schreibt nichts; Worker wie `SetNotActive` (`ResyncChannelAsync`). Leave nur für IDs, deren letzter aktiver Slot ging oder die schon archiviert waren. `Emote.Name`/`FirstSeenAt` nach D13.

## 8. Backfill-Änderungen (löst AC 26 / D36 ab)

| Stelle | Neu |
|---|---|
| EnqueueAsync (K3; Kind 3 Basis, Kind 5 Mehrslot) | **Kind 3 / M2:** bis zur Mehrslot-Erweiterung bleibt die heutige Auswahl eines Preview-Eintrags je Emote; Enqueue bindet diesen Eintrag an einen bestehenden oder neu angelegten Basisslot, erstellt nötigenfalls den Emote-Platzhalter und schreibt dessen nicht-null SlotId in den Snapshot. Damit kann ReplaceBlockAsync ab dieser Merge-Grenze UsageStats.SlotId schreiben. **Kind 5 / M3:** keine Dedup mehr, vollständige datierte Bindung für alle Einträge (D28), D7 für aktive-Set-Vorschauen vor Snapshot-Erstellung, Set-Seed-Settle und 409. Enqueue nimmt bei autoritativen Schlüsseln dieselbe Emote-Zeilensperre mit SELECT FOR UPDATE wie REST und liest nach dem Lock erneut. Für aktive Vorschauen: zuerst alle exakten (EmoteId, addedAt)-Treffer, danach D7; Snapshot je tatsächlichem Slot. Für nichtaktive Sets: exakte Datumsbindung, neue Einträge archiviert. Undatierte Einträge nach Name gemäß D11. Nur Usage-Zeilen des Preview-Sets werden settled. Snapshot-Zeile je Eintrag mit SlotId, EmoteId, SevenTvEmoteId, Name, AddedToSetDay, CreatedRow, CreatedSlot.
| Snapshot-DTO | `ChatLogBackfillSnapshotEmote(string SlotId, string EmoteId, string Name, DateOnly? AddedToSetDay)` |
| Worker (K4) | `Coalesce(Name → SlotId)`; `addedToSetDay[SlotId]`; `emoteIdBySlotId` aus dem Snapshot. Der Snapshot wird wie heute **einmal** je Lauf gelesen; da kein Schreiber Slots mehr zusammenführt oder löscht (nur Kaskaden beim Purge, der den Run mitnimmt), bleibt er den Lauf lang gültig. |
| `ChatLogBackfillBlockCounter` | Gate je Slot; `_cells` je `(SlotId, Day)`; `ChatLogBackfillAggregate(string SlotId, string EmoteId, DateOnly Date, int UseCount, int BotUseCount, int SharedChatUseCount)` |
| `ReplaceBlockAsync` | `ValidateBlock` je `(SlotId, Date)` plus Snapshot-Zugehörigkeit und `SlotId→EmoteId`-Konsistenz (R1 Befund 7); Delete je Kanal unverändert (OD-A); Insert mit `SlotId` **und** `EmoteId`; Existenzprüfung gegen `EmoteSlots`; Coverage/Fortschritt unverändert in derselben Transaktion |
| `emoteCount` | = Snapshot-Zeilen |

HandOfBlood: der Snapshot hält `ome44` (altes Datum) **und** `hob44` (2026-09-01); `ome44`-Treffer vor dem 01.09. zählen; beide Namen landen auf getrennten Slots, auch am selben Tag.

Abgelöste Stellen der Backfill-Spec: `:153`, `:222-226`, `:316-317`, `:348`, `:558` (D36), `:601` (AC 26 → AK 15), `:614`, `:779`; datierter Kopfhinweis plus Einzeiler-Fußnoten, der Text bleibt.

## 9. Frontend

### 9.1 Modell

- `EmoteUsageTotalDto`: + `slotId`, + `firstSeenAtFromRest`, + `siblingNames`; `emoteName` = Slot-Name, `firstSeenAt` = Slot-Datum.
- `EmoteUsageTotal`: − `slotCount`, − `aliases`; + `slotId: string | null`, + `firstSeenAtFromRest`, + `siblingNames`, + `slotKey` (= `slotId` oder `live:${sevenTvEmoteId}#${addedAt ?? name}`).
- `EmoteSeriesEntry`: + `slotId`, + `name`. `EmoteUsageSeries`: + `slotId: string | null`.
- `mergeSetView` (D22): Zeile mit `firstSeenAtFromRest` ↔ Live-Eintrag über `(sevenTvEmoteId, addedAt)`; Zeile ohne ↔ über `(sevenTvEmoteId, name)`; Rest `'left'` bzw. Klasse 3 je Live-Eintrag; `siblingNames` aktiv aus dem DTO, nicht-aktiv aus den anderen Live-Einträgen derselben ID.

### 9.2 Seite

| Stelle | Neu |
|---|---|
| `selection`, `track`, `inspectedId`, `fillPercents`, `usageRank`, `inspect`/`onCellFocus` | `slotKey` (D21) |
| `seriesByEmote` | `Map<slotId, days>`; Klasse 3 → leere Kurve |
| Bänder, `deadCount`, `totalUses`, Pareto, `emoteCountKey` | je Zeile = je Slot (D1); Wortlaut „Emotes" bleibt |
| Tag-Filter / `assignTags` / `tagUnassignIds` | Filter unverändert (ID); Zuweisung distinct IDs |
| Vote-Erstellung | distinct `emoteId`/`sevenTvEmoteId`, `voteBallotSize` = distinct |
| Karte (D23) | `aria-label` + `alsoAs`; Scrim `+{{n}}`; Inspector/Sidecar `alsoAs`; `setView.slots` entfällt |
| „Neu"/„Im Set seit"/Trend | lesen das Slot-Datum (keine Codeänderung außer DTO); `hideObserved` je Slot |
| Drilldown | `slotId`; Cache `channel|scope|slot:<id>|…` bzw. `emote:<id>`; 7TV-Link je Emote |
| Suche | unverändert — findet je Slot |
| Export (D25) | eine Zeile je Slot; Parser/`toImportRow` dedupe nach ID |
| Vote-Detail | `emoteNames.join(' / ')` (`vote-session-detail-page.html:113`, `:273`, `:471`); Suche über `emoteNames`; Löschen je ID ohne `alias`/`Entries` (D3) |

### 9.3 Massenlöschung je Slot (O3, D21, D32, D33)

- `DeletableEmote` + `slotId`, `alias`, `addedAt`, `addedAtFromRest`; **nur Zeilen mit `addedAtFromRest`** erreichen den Lauf — provisorische Zeilen werden mit `usageStats.delete.pendingSync` („{{name}} wird gerade mit 7TV abgeglichen, in einer Minute erneut") aus dem Lauf genommen (D11). `toDeleteQueue` schlüsselt `${sevenTvEmoteId}#${slotId}`.
- `REMOVE_EMOTE_MUTATION` → `mutation RemoveEmote($setId: Id!, $emoteId: Id!, $alias: String) { … removeEmote(id: { emoteId: $emoteId, alias: $alias }) { id } }`; Nutzungsseite sendet `alias` immer; Vote-Seite und Import-Replace senden `alias: null` (D3).
- **Bindung je Versuch (`beforeStep`, D21, R2 Befund 2):** die Delete-Operation hält eine **Live-Liste des Sets** (`readLiveAliasesFromActiveSet`/`…FromSet`), die vor dem ersten Schritt gelesen und **vor jedem Versuch** erneuert wird, dessen Vorgänger länger als 5 s zurückliegt (Rate-Limit-Pause, Retry-Wartezeit, Fortsetzung) — also höchstens ein zusätzlicher Read je Pause, nicht je Zeile. Je Zeile sucht der Hook den Eintrag `(sevenTvEmoteId, addedAt)`: gefunden mit dem Zeilen-Alias → `continue` mit genau diesem Alias; gefunden mit **anderem** Alias → `skip` als `renamed` (Dock-Zeile `usageStats.delete.renamedSinceLoad`); nicht gefunden → `skip` als `departed`. Der Hook läuft auch für den Retry-Versuch nach einem Transportfehler.
- **Nachprüfung (D33):** die bestehende Nachlese (`settling`, ein Re-Read) prüft zusätzlich jede **nicht** gemeldete autoritative Zeile des Emotes, zu dem ein `REMOVE` ging: fehlt ihr Eintrag `(id, addedAt)` in der Live-Liste → `usageStats.delete.unexpectedRemoval` mit Alias und Restore-Angebot (das Protokoll trägt den Alias).
- `pendingRemovalSlots` = markierte Live-Zeilen mit `addedAtFromRest`; `onDeleted(doneKeys)` entfernt genau diese Zeilen; Geschwister-Hinweise neu berechnet.
- **Bericht (D32):** `reportDeletedInSet` sendet `sevenTvEmoteIds` (distinct) **und** `entries: [{ sevenTvEmoteId, slotId, alias }]` je erfolgreich entfernter Zeile; der Retry-Pfad wiederholt denselben Body; der Server archiviert über `slotId`. Restore (`seven-tv-restore.service.ts:626-633`) sendet dieselben Felder je `doneKey`; die Restore-Queue hält dazu die gelöschte `slotId`. Protokoll `PurgeRunRow.aliases = [alias]`.
- **Live-Nachweis unserer Mutation (AK 36):** Kind 8 fährt die Mutation aus unserem Code gegen ein Wegwerf-Set mit zwei Slots eines Emotes; `read_set` davor/danach, Set-ID, Datum im PR (ohne Token).

### 9.4 Mocks/E2E

`mockUsageTotals` + `slotId`, `firstSeenAtFromRest` (Default `true`), `siblingNames`; Member-Mock mit zwei Einträgen derselben ID und je `addedAt`; `mockUsageChannelSeries` je Slot; Report-Mocks prüfen `entries`. Alle E2E-Specs importieren aus `web/e2e/support/test.ts`.

## 10. Datenschutz, Logs, Retention

Keine neuen personenbezogenen Daten. Neue Log-Zeilen (englisch): ausgelassene Seeds (6.1), Settle-Deferral (6.3), `AmbiguousDelta` (7.2), Flush-Verwurf gelöschter Slots. Retention unverändert.

## 11. Migration und Deploy-Reihenfolge

### 11.1 Drei Migrationen (D19), auf Prod in einem Fenster

**M1 `AddEmoteSlots`** (Kind 1, additiv, kein Schreiber ändert sich):
1. `SET LOCAL lock_timeout = '5s';`
2. `CREATE TABLE "EmoteSlots"` (4.1) mit beiden Indizes.
3. Seed je distinct `(UsageStats.EmoteId, UsageStats.EmoteSetId)`-Paar, das bereits Nutzungszeilen hat: Name/Archivzustand aus `Emotes`, aber `AddedAt = NULL`, `AddedAtFromRest = false`, `SeedUnsettled = true`. `Emote.FirstSeenAt` wird nicht auf Seeds anderer Sets übertragen.
4. `UsageStats`: `ADD COLUMN "SlotId" text NULL` + FK; jede Zeile erhält den Seed nur ihres `(EmoteId, EmoteSetId)`-Paars; Prüfung keine NULL/falsche Set-Zuordnung, sonst Abbruch.
5. `CREATE INDEX "IX_UsageStats_SlotId_EmoteSetId_Date" … INCLUDE ("UseCount")` (nicht-unique).
6. `ChatLogBackfillRunEmotes`: `SlotId text NULL` befüllt über das `EmoteSetId` des Laufs und den Seed desselben Sets; `CreatedSlot boolean NOT NULL DEFAULT false`.
7. `VoteSessionEmotes`: `NamesAtCreation text[] NULL`.
`Down`: alles fällt (kein Guard nötig).

**M2 `SwitchUsageStatsToSlotKey`** (Kind 3, nicht-additiv, im PR **aller** `UsageStats`-Schreiber): `SlotId SET NOT NULL`; Emote-Index → nicht-unique; Slot-Index → unique — ab hier `ACCESS EXCLUSIVE` bis zum Commit. `Down`: wirft bei > 1 Zeile je `(EmoteId, EmoteSetId, Date)`; sonst zurück.

**M3 `SwitchBackfillSnapshotToSlotKey`** (Kind 5): Snapshot-`SlotId SET NOT NULL`, PK `(RunId, SlotId)`. `Down`: wirft bei > 1 Snapshot-Zeile je `(RunId, EmoteId)`.

Migrationstests nach `AddUsageStatEmoteSetIdMigrationTests` (eigene DB je Fall, Seed per Raw-SQL auf dem Vorgängerstand), **befüllte** `Up`- und `Down`-Fälle inkl. Kollaps-SQL als Fixture (AK 3–4). Vor dem Fenster misst der Betreiber `count(*)` auf `UsageStats` fürs Runbook.

### 11.2 Deploy (Wartungsfenster, 15 min geplant)

1. **Ankündigung** außerhalb großer Streams.
2. **worker und api stoppen**; `pg_stat_activity` ohne `EmotePurge%`.
3. **Backup** `scripts/backup-postgres.sh`.
3a. **Vorbedingung Backfill (Betreiber, 2026-10-10):** `SELECT count(*) FROM "UsageStats" WHERE "Source" = 1` = 0 und `SELECT count(*) FROM "ChatLogBackfillRuns"` = 0. Sonst Abbruch: alte Images hoch, Punkt 14.4 neu bewerten.
4. **Migration von Hand** über den Tunnel: `list` (erwartet `AddEmoteSlots`, `SwitchUsageStatsToSlotKey`, `SwitchBackfillSnapshotToSlotKey` als `(Pending)`), `update` (alle drei), `list`. Abbruch bei > 10 min: nach M1 tragen die alten Images den Stand noch (additiv), nach M2 nicht (dann `Down` M2 oder vorwärts).
5. **Beide Images gemeinsam hoch.** Niemals ein altes Image gegen die M2-DB.
6. **Nachkontrolle ≤ 5 min:** (a) unsettled Seeds des aktiven Sets → 0 nach dem ersten Resync-Tick (Ausnahmen: Kanäle mit Sync-Fehlergrund oder ohne v4-Daten — die Zeile `settle deferred` nennt sie); (b) Zahl der Mehr-Slot-Emotes (HandOfBlood ≥ 1); (c) kein `settle deferred`-Dauerzustand; (d) `handofblood`: zwei Karten, alte Summe bei `ome44`, `hob44` bei 0; (e) `usage.flushed` nach dem ersten Sync.
7. Runbook-Eintrag in `infra-docs`.

Alle Kinder mergen vor dem Fenster; kein Teil-Deploy (D19). **Das Backfill-Flag bleibt auf Prod aus, bis dieses Fenster durch ist** (Betreiber, 2026-10-10), zusätzlich zu B12 der Backfill-Spec und #366. Ein Backfill danach kann unsettled Seeds für ein nicht aktives Set bei lauter datierten Einträgen selbst settle.

## 12. Rollback

### 12.1 Code-Rollback ohne Schema-Rollback: nicht möglich (D19).

### 12.2 Schema-Rollback (Fenster)

1. worker/api stoppen, Backup. 2. **Kollaps-SQL** (Operations.md, Test-Fixture in Kind 1): je `(EmoteId, EmoteSetId, Date)` mit > 1 Zeile Summen auf die Zeile des Slots mit kleinstem `AddedAt` (`null` zuletzt, dann ordinal `Name`), übrige löschen; `Source` bleibt `0`, wenn eine Zeile `0` ist; je `(RunId, EmoteId)` die Zeile des ältesten Slots behalten. 3. `dotnet ef database update 20261009102325_AddChatLogBackfill`. 4. Alte Images hoch (der alte Abgleich benennt Mehr-Slot-Emotes auf den letzten Alias zurück — harmlos).

### 12.3 Backfill-Rollback (Operations.md `:854-935`)

Schritt **3b** nach dem Platzhalter-Cleanup und **vor** dem Löschen der Run-Zeilen:

```sql
DELETE FROM "EmoteSlots" s
WHERE s."IsArchived" AND s."ArchivedAt" IS NULL
  AND EXISTS (SELECT 1 FROM "ChatLogBackfillRunEmotes" r WHERE r."SlotId" = s."Id" AND r."CreatedSlot")
  AND NOT EXISTS (SELECT 1 FROM "UsageStats" u WHERE u."SlotId" = s."Id" AND u."Source" = 0);
```

Ein von der Enqueue gesettelter Seed ist kein `CreatedSlot` und bleibt (AK 39).

## 13. Akzeptanzkriterien (pass/fail)

**Schema und Migration**
1. M1–M3 laufen auf einer Kopie der Prod-Daten durch; keine `UsageStats`-Zeile ohne `SlotId`; jede zeigt auf einen Slot ihres `EmoteId`; `migrations list` ohne Pending.
2. Je distinct `(EmoteId, EmoteSetId)` mit `UsageStats` gibt es genau einen unsettled Seed; jede seiner Zeilen gehört zu diesem Set; `AddedAt = null`, `AddedAtFromRest = false`; `Emote.FirstSeenAt` wird nicht übernommen.
3. **Befüllte `Down`-Fixtures:** M2-`Down` wirft bei zwei Slot-Zeilen am selben `(EmoteSetId, Date)`; nach dem Kollaps-SQL grün, Summen je `(EmoteId, EmoteSetId, Date)` unverändert; M3-`Down` analog; M1-`Down` entfernt alles.
4. **Befüllte `Up`-Fixtures:** Emote `X` mit UsageStats in Sets A und B → M1 erzeugt getrennte Seeds und jede Zeile zeigt auf den Seed ihres Sets; nach Settle von A bleiben B-Zeilen unangetastet, nach Settle von B liegen sie auf Bs ältestem datiertem Slot; die Set-Summen vor/nachher sind je Set unverändert. Snapshot-Zeilen tragen den Seed des zugehörigen Sets, `CreatedSlot = false`, `NamesAtCreation` `NULL`; M2/M3 darauf grün.

**REST-Abgleich**
5. Settle für Set A: Seed mit dessen historischen Zeilen, Live `[ome44 (2026-07-01), hob44 (2026-09-01)]` → Seed heißt `ome44` mit `AddedAt` 2026-07-01 autoritativ und hält **nur die Zeilen aus A**; neuer Slot `hob44` ohne Zeilen; Marke weg; `Emote.Name = ome44`; `HasChanges` nur beim ersten Lauf.
6. Settle ohne Datum: dieselbe Liste undatiert → nichts angelegt, Marke bleibt, `Information`; `hob44 (datiert)` + `ome44 (undatiert)` → ebenso; mit Daten im nächsten Lauf greift AK 5.
7. **Tausch (R2 Befund 1):** `S1(A,t1)`, `S2(B,t2)` autoritativ; Live `[C(t1), A(t2)]` → `S1` heißt `C`, `S2` heißt `A`, gleiche IDs, Zeilen bleiben. **Entfernen + Hinzufügen:** `S1(A,t1)`; Live `[B(t2)]` → `S1` archiviert, `B` neu mit 0. **Rückkehr unter altem Namen:** `S1(A,t1)` archiviert; Live `[A(t3)]` bei keinem aktiven `A` → dieselbe `SlotId`, `AddedAt = t3`, Historie bleibt.
8. **Wiederkehr desselben Eintrags:** `S1(A,t1)` archiviert (REST-Lag); Live `[A(t1)]` → `S1` un-archiviert, kein neuer Slot. **Gleichstand:** Live `[A(t), B(t)]` auf Slots `[A(t), B(t)]` → exakte Treffer je Name; Live `[A(t), C(t)]` → `B` archiviert, `C` neu.
9. Letzter Slot geht: `[A, B]` aktiv, Live `[A]` → `B` archiviert, Emote aktiv, **keine** Leave; Live `[]` → beide und der Emote archiviert, Leave nach b1.
10. `Emote.Name` folgt dem ältesten aktiven Slot (Gleichstand ordinal; alle `null` → ordinal).
11. `GetSetEntriesAsync` liefert je `(emoteId, alias)` ein Datum (Fixture, AK 29).
12. `SevenTvSyncServiceDuplicateEmoteIdTests` ersetzt: zwei Einträge einer ID → zwei Slots; zweiter Lauf `HasChanges = false`.
13. **Provisorisch → autoritativ:** Push-Slot `C (UtcNow, provisorisch)`; Live `[C(t5)]` → `C.AddedAt = t5`, `FromRest = true`, kein `HasChanges`; Push-Slot `C`, Live `[D(t5)]` → `D` neu, `C` archiviert (6.2 Beispiel iv).

**Live-Zählung**
14. `ome44 hob44 ome44` → +1 je Slot; Flush zwei Zeilen `(SlotId, Set, heute)`, derselbe `EmoteId`; zweiter Flush addiert.
15. Namenskollision zweier **verschiedener** Emotes → ein Treffer je Nachricht, Tracker meldet, `DuplicateNames` nennt beide; zwei Slots **desselben** Emotes nie. *(Ersetzt AC 26 der Backfill-Spec.)*
16. Flush mit gelöschter Slot-ID → Zeile verworfen, Warnung, Rest geschrieben.
17. **Zählung erst nach dem Settle (R1 Befund 3):** Boot mit Seeds des aktiven Sets → Map leer, `hob44` zählt nichts, ein Flush vor dem Settle schreibt nichts für `X`; nach AK 5 zählt `hob44` auf den neuen Slot, `ome44` auf dem nun autoritativen Slot mit der Seed-Historie; ohne v4-Daten bleibt die Map für `X` leer.

**EventAPI**
18. Parser-Fixtures: `pushed` → `(Id, Name)`; `updated` mit beiden Namen; `pulled` zwei Items einer ID; fehlende Namen `null`.
19. Push `(X, probeB)` auf `probeA` → zweiter Slot provisorisch; Push eines Namens, der schon aktiv ist → No-op; Push unter dem Namen eines archivierten Slots → `AmbiguousDelta`, REST-Abgleich wendet D7 mit Datum an; Pull `(X, probeA)` → nur `probeA` archiviert, keine Leave; Pull `(X, probeB)` → Emote archiviert, Leave.
20. Rename `(X, probeB → probeC)` → in place; Rename auf den Namen eines **aktiven** Slots derselben ID → `AmbiguousDelta`; Pull ohne Name bei ≥ 2 aktiven → `AmbiguousDelta`; Delta auf unsettled Seed → `AmbiguousDelta`.
21. Worker: `AmbiguousDelta` → `ResyncChannelAsync`, kein `channel.synced`.

**Backfill**
22. Enqueue für Set A `[X ome44 (2026-07-01), X hob44 (2026-09-01)]` → eine `Emote`-Zeile, zwei autoritative Slots (archiviert, wenn Set nicht aktiv; sonst die bestehenden über `(EmoteId, addedAt)` getroffen), zwei Snapshot-Zeilen mit eigenem `AddedToSetDay`, `CreatedSlot` genau für neue; ein unsettled Seed von Set B und seine `UsageStats` bleiben unangetastet.
23. Blockzähler: 2026-08-15 `ome44` zählt, `hob44` verworfen (Gate je Slot); Aggregate je `(SlotId, Day)`.
24. **Beide Aliase an einem Tag (R1 Befund 7):** `ValidateBlock` akzeptiert, zwei Zeilen mit exakten Zählern je Kategorie; fremde `SlotId` oder falscher `EmoteId` → `ArgumentException`, nichts geschrieben.
25. **OD-A-Fixture:** Re-Run eines anderen Sets über ein Fenster mit Import-Zeilen beider Slots → Block ersetzt alle `Source = 1`-Zeilen des Kanals, Coverage trägt das neue Set, `WeeksDone` +1, eine Transaktion; der pinned 6-Monats-Block bleibt deterministisch, Summe je `(EmoteId, Day)` ≥ alt.
26. **Enqueue-Settle (R1 Befund 4, R2 Befund 6):** Seed von nicht aktivem Set A mit Preview `[ome44 (alt), hob44]` datiert → Seed heißt `ome44` (autoritativ), Marke weg, nur As Zeilen bleiben dort; `hob44` neuer archivierter Slot; undatierter Eintrag → 409 `backfill_slots_unsettled`, nichts geschrieben, Marke bleibt. Ein unsettled Seed des aktiven Sets → 409. Späterer Abgleich/Preview settelt je Set nach D2.
27. **Enqueue-Konkurrenz und Umbenennung:** Enqueue und REST sehen denselben neu erschienenen Eintrag einmal als A(t) und einmal nach Umbenennung als C(t); beide beginnen mit leerem Slot-Lookup. Der erste Schreiber legt A(t) an. Der zweite liest unter derselben Emote-Zeilensperre erneut, sieht genau einen vorhandenen Slot für den Schlüssel und die einzelne aktuelle Live-Identität C(t), benennt den Slot um und bindet beide Beobachtungen an genau eine SlotId und dieselbe Usage-Historie. Snapshot und Live-Abgleich zeigen darauf. Eine echte Live-Gruppe [A(t), C(t)] bleibt gemäß AK 8 zwei name-unterschiedliche Slots.

**API**
28. `/totals` aktiv: zwei Zeilen für `X`, `slotId` verschieden, `siblingNames` je `[anderer]`, `firstSeenAt`/`firstSeenAtFromRest` je Slot; nicht-aktiv nur Slots mit Zeilen unter dem abgefragten Set; ein D7-reaktivierter Slot erscheint aktiv mit seiner vorhandenen Nutzung.
29. Die geänderte `GqlSetEntriesQuery` ist vor dem Merge von Kind 2 von Hand live gefahren; Fixture liegt vor; PR nennt Datum und Set-ID (ohne Token).
30. `/daily?slotId=` nur der Slot; `?emoteId=` Summe; beide → `slotId`; fremde ID → 404; Filter-Matrix unverändert.
31. `/series` je Slot mit `slotId`/`name`; Kodierung bytegleich.
32. `occupiedSlots` = aktive Slots; `duplicateNames` über Slots.
33. Vote: Set-Session mit `X` in zwei Slots → eine Ballot-Zeile, `NamesAtCreation = [ome44, hob44]`; Null-Session `EmoteNames` live; doppelte `emoteId` im Request → eine Zeile, 201.

**Frontend**
34. Zwei Karten `ome44`/`hob44` mit Marker `+1` und „Auch im Set als …"; Suche `hob44` findet die zweite; Bänder/Rang je Karte; keine `NG0955`.
35. Markieren je `slotKey`; Klasse-3-Schlüssel `live:<id>#<addedAt>` kollidiert mit keiner DB-Zeile; Tag zuweisen/Vote erstellen dedupliziert IDs; Export zwei Zeilen, Parser eine; Drilldown `slotId` bzw. `emoteId`; Ballot-E2E „ome44 / hob44"; E2E `usage-atlas` zeigt zwei Zellen statt „2 Plätze im Set".
36. **Live-Nachweis unserer Mutation (Kind 8):** `removeEmote(id: { emoteId, alias })` aus unserem Code entfernt genau den gewählten Slot eines Wegwerf-Sets mit zwei Slots; `read_set` davor/danach im PR.
37. Löschen von `hob44` → `REMOVE { X, "hob44" }`, `ome44` steht noch, Karte weg, `occupiedSlots` −1, Bericht mit `entries: [{ X, slotId(hob44), "hob44" }]`.
38. **Bindung je Versuch (R2 Befund 2):** S1(A,t1) markiert; der Read vor dem ersten Versuch zeigt A(t1) → REMOVE(A); Variante: 429-Pause, danach der erneuerte Read zeigt C(t1) und A(t2) → S1 renamed, kein REMOVE, Dock nennt A → C; Transport-Retry nach Umbenennung → ebenfalls skip. Provisorische Zeile markiert → pendingSync, kein REMOVE. Für die Mehrslot-Frische bei kontinuierlichen Läufen siehe AK 51.
39. Backfill-Rollback-Cleanup (12.3) auf (i) backfill-erzeugtem Slot ohne Live-Nutzung, (ii) mit `Source = 0`-Zeile, (iii) Seed eines gelöschten Platzhalters, (iv) gesetteltem Seed → löscht genau (i); Votes bleiben.
40. **Nachprüfung (D33):** gemockte Live-Liste nach dem Lauf ohne den Eintrag `(X, t2)`, den der Lauf nicht gemeldet hat → Dock zeigt `unexpectedRemoval` mit Alias und Restore-Angebot; mit vollständiger Liste nichts.

**Berichte**
41. **Teil-Löschung (R1 Befund 5):** sync-deleted mit entries: [{ X, slotId(hob44), hob44, addedAt: t1 }] → nur dieser Slot im Ursprung archiviert, Emote aktiv, keine Leave, Holds aller Platzierungen unverändert; derselbe Bericht erneut → erfolgreicher No-op; ein zwischenzeitlich auf t3 per D7 wiederaufgenommener Slot empfängt einen verspäteten Bericht mit t1 als No-op ohne Leave (AK 52); ein fremder slotId im Ursprung → 400; danach entries für ome44 → Emote archiviert, Leave einmal. Ohne entries → heutiges Verhalten.
42. **Restore (R4/D7):** sync-restored mit entries: [{ X, slotId: S1, alias: A, addedAt: t1 }] → genau die passende archivierte Generation von S1 wird reaktiviert, ArchivedAt = null, Nutzung bleibt; AddedAt wird einmal provisorisch neu gesetzt. REST [A(t9)] autorisiert t9 auf derselben SlotId. Ohne entries → nur Emote un-archiviert, nächster Abgleich legt neue Slots datiert an. Restore-Lauf sendet je doneKey slotId und das ursprüngliche AddedAt; Retry- und REST-vor-Report-Verhalten siehe AK 55.

**Betrieb**
43. Deploy-Nachkontrolle 11.2 Schritt 6 (a)–(e); Dauer im Runbook.
44. Alle drei Suiten grün **an jeder Merge-Grenze**; `coverage-local.mjs` ≥ 80 %; `dotnet format`, `lint`/`format` sauber; `CoreAssemblyReferenceTests` grün; `api-error-locales.spec.ts` grün mit `backfill_slots_unsettled`.
45. Harness: `harness-3` unverändert, `ReplayDayCounterTests` (24) grün; DECISIONS nennt die Abweichung (D20).

**Wiederaufnahme (D7, pass/fail)**
46. **Undo/Restore:** gelöschter Slot `S1` mit vorhandenen `UsageStats` wird über den Restore-Bericht mit seiner `slotId` reaktiviert; derselbe Karten-/Serienschlüssel zeigt die alten und neuen Zählungen, und REST setzt `AddedAt` auf das neue `added_at`.
47. **Externe Wiederaufnahme:** archiviertes `A(t1)` ohne aktiven gleichnamigen Slot; REST liefert `A(t3)` ohne exakten Datumstreffer → dieselbe Slot-ID und Historie, `AddedAt = t3`, `AddedAtFromRest = true`.
48. **Gleichnamiger aktiver Slot:** archiviertes `A(t1)` plus aktiver `A(t2)`; REST liefert `A(t3)` → D7 reaktiviert `A(t1)` nicht; der neue Eintrag erhält eine neue Slot-ID bei 0, nicht mehr gelistete Slots werden archiviert.
49. **Zwei archivierte Namensvetter:** `A(t1)` und `A(t2)` archiviert, `ArchivedAt(S2) > ArchivedAt(S1)`; REST liefert `A(t3)` ohne aktiven gleichnamigen Slot → `S2` wird mit seiner Nutzung reaktiviert und auf `t3` umgeschlüsselt; `S1` bleibt archiviert.
50. **Tausch im selben Abgleich:** archiviertes `A(t0)`, autoritative `S1(A,t1)` und `S2(B,t2)`; REST `[C(t1), A(t2)]` → `S1`/`S2` bleiben die exakten Datumstreffer und D7 reaktiviert `A(t0)` nicht.

51. **Frischer Read im kontinuierlichen Mehrslot-Löschlauf:** Live stehen S1(A,t1) und ein Geschwisterslot S2(B,t2) im selben Emote. Zwischen allen Versuchen liegen weniger als 5 s. Nach dem ersten Schritt werden S1 zu C(t1) und S2 zu A(t2) umbenannt. Vor dem nächsten Mutationsversuch wird neu gelesen; S1 wird als renamed übersprungen und es gibt kein REMOVE(A). Der bestehende Ein-Request-Rest D33 bleibt auf einen Read-Request-Umlauf begrenzt.
52. **Verspäteter Delete-Bericht nach D7:** S1(A,t1) wurde gelöscht; REST nimmt denselben Slot als A(t3) wieder auf. Der verspätete Originalbericht und identische Retries mit slotId = S1, addedAt = t1 sind erfolgreiche No-ops: S1 bleibt aktiv bei t3, und es wird keine Leave-Beobachtung erzeugt.
53. **Aktive Preview-Reihenfolge:** Mit archiviertem S1(A,t1) samt Nutzung und ohne aktive gleichnamige Karte (a) Enqueue der aktiven Preview [A(t3)] vor REST → D7 bindet Snapshot und folgenden REST-Abgleich an S1; (b) Enqueue vor dem exakten Restore-Bericht → der Bericht ist danach ein erfolgreicher No-op und Snapshot/REST behalten S1; (c) Restore-Bericht vor Enqueue → Enqueue bindet den vorläufig aktiven S1 an A(t3). In allen Reihenfolgen gibt es keine neue S2, und die Historie bleibt auf S1.
54. **Shared-Set-Berichte mit lokalen SlotIds:** Kanäle A und B teilen das Set und haben für (X,t1) verschiedene SlotIds SA und SB. Ein Delete-Eintrag mit der gültigen Ursprungs-SlotId SA archiviert lokal SA und fan-out löst SB über (X,t1) auf; derselbe Restore reaktiviert die jeweiligen lokalen Slots. Kein Kanal wirft 400 wegen der SlotId des anderen; bei keinem eindeutigen Geschwistertreffer wird dort nichts geschrieben und REST gleicht ab.
55. **Restore-Idempotenz:** (a) Restore von archiviertem S1(t1) wird gespeichert, die Antwort geht verloren, identischer Retry antwortet 2xx ohne Änderung; nach REST t9 bleibt S1 autoritativ bei t9. (b) REST wendet D7 zuerst an und macht S1 bei t9 aktiv; der erste Restore-Bericht mit der alten Generation t1 antwortet 2xx ohne AddedAt zurückzusetzen. Nutzung und SlotId bleiben in beiden Fällen erhalten.
56. **Kind-3-Mergegrenze:** Auf einer leeren, migrierten Datenbank führt Enqueue für ein zuvor unbekanntes einzelnes Preview-Emote zu einer Emote-Zeile, einem Basisslot und einem Snapshot mit nicht-null SlotId; ein echter EnqueueAsync → ReplaceBlockAsync-Durchlauf schreibt die UsageStats-Zeile mit genau dieser SlotId. Alle drei Suiten sind an dieser Merge-Grenze grün.

## 14. Offene Punkte

1. ~~Vorbehalt S1.~~ **Erledigt (2026-10-10):** je Slot löschbar, `added_at` umbenennungsfest (2.4); Nachweis unserer Mutation in Kind 8 (AK 36).
2. ~~Slot-Identität über Umbenennungen (D11).~~ **Entschieden (Betreiber, 2026-10-10)** und mit D4 zur Datums-Identität verallgemeinert.
3. ~~Harness (D20).~~ **Entschieden (Betreiber, 2026-10-10):** unverändert.
4. ~~Importierte Zeilen aus Läufen vor der Migration.~~ **Entschieden (Betreiber, 2026-10-10):** Flag auf Prod erst nach dem Deploy; 11.2 Schritt 3a prüft es; Läufe danach treffen D12.

5. ~~Wiederaufnahme und Verlaufskarte.~~ **Entschieden (Betreiber, 2026-10-10, D7):** Restore reaktiviert die exakte `SlotId`; externe Wiederaufnahme folgt 6.2 nach den exakten Datumstreffern.

**Zur Kenntnis des Betreibers, keine Frage:** (a) D7 setzt „Neu"/„Im Set seit" auf das neue REST-Datum zurück, die Zählreihe bleibt auf derselben Karte; ein vorhandener aktiver Slot gleichen Namens erzwingt einen neuen Slot bei 0. (b) D33 ist das eine Restrisiko, das die Spec nicht schließen kann (kein 7TV-Selektor über `added_at`); es ist auf einen Request-Umlauf begrenzt, wird nach dem Lauf erkannt und ist über den Restore reparierbar. (c) D12: bleibt das v4-Overlay für ein Set dauerhaft aus, zählen dessen Mehr-Slot-Emotes bis zur ersten datierten Antwort nicht.

## 15. Testpyramide

| Schicht | Datei(en) | neu / geändert |
|---|---|---|
| **Infrastructure.Tests / Integration** | drei Migrationstest-Klassen (Muster `AddUsageStatEmoteSetIdMigrationTests`) | 10 neu (AK 1–4, inkl. Set-A/Set-B Seed und Settle) |
| | SevenTvSyncServiceSlotTests.cs (neu, ersetzt …DuplicateEmoteIdTests − 2) | 16 neu (AK 5–13, +4 D7-Fälle AK 47–50) |
| | `SevenTvSyncServiceTests.cs` (66), `…PlaceholderTests`, `…LeaveObservationTests` | ~10 geändert |
| | `SevenTvSyncServiceDeltaSlotTests.cs` (neu) | 9 neu (AK 19–20) |
| | `UsageStatFlushServiceTests.cs` (17) | 17 angepasst, +3 (AK 14, 16, 17) |
| | `UsageStatQueryServiceTests.cs` (74) | ~20 angepasst, +6 (AK 28, 30–31) |
| | `EmoteSetStatusServiceTests.cs` (21) | +2, ~3 angepasst (AK 32) |
| | ChatLogBackfillServiceTests*.cs (49) | ~10 angepasst, +10 (AK 22, 24–27, 39, 53, 56; AK 27 prüft Aliaswechsel unter Sperre) |
| | EmoteServiceTests.cs (55) | +11 (AK 41–42, 46, 52, 54–55) |
| | `VoteSessionServiceTests.cs` (37), `VoteSessionQueryServiceTests.cs` (32) | +3 (AK 33) |
| | `EmoteTagServiceTests.cs` | +2 (D29; `Holds` bei Teil-Löschung) |
| | Retention/Purge/Account-Tests | 0 geändert (Nachweis in Kind 1) |
| **Infrastructure.Tests / Unit** | `SevenTvDispatchParserTests.cs` (11) | +4 (AK 18) |
| | `EmoteMatchCacheTests.cs` (12), `EmoteNameMatchingTests.cs` (24) | Umbenennung; Matching 0 geändert |
| | `SevenTvApiClientTests` | +2 (AK 11) |
| **Worker.Tests** | `EmoteUsageCounterTests.cs` (13); `WorkerBootSequenceTests.cs`; `EmoteMatchCacheSwitchCountingTests.cs` | +1; +2 (AK 17) |
| | `ChatLogBackfillBlockCounterTests.cs` (14), `ChatLogBackfillWorkerTests.cs` | +3, +1 (AK 23–24) |
| | `SevenTvEventClient`-Dispatch-Test | +1 (AK 21) |
| | `ReplayDayCounterTests.cs` (24), `HarnessRunnerTests` | 0 (D20) |
| **Api.Tests** | UsageStatsDailyScopeEndpointTests.cs (2); Backfill-/SevenTv-Endpoint-Tests | +4 (AK 30); +5 (409 backfill_slots_unsettled; entries-Validierung und AddedAt; Ursprungskanal/Fan-out; stale No-op; AK 52, 54–55) |
| **Vitest** | `merge-set-view.spec.ts` (8) | 3 ersetzt, +4 (Join über `addedAt`, über Name, Klasse 3, `siblingNames`) |
| | `usage-stats-page.spec.ts` (215) | AK-58-Trio ersetzt, ~15 angepasst, +11 (Schlüssel, Klasse 3, D7-Karte/Datum, Dedupes, Drilldown-Gate, `pendingRemovalSlots`, `onDeleted`, Bericht) |
| | `emote-usage-filter.spec.ts` (17), `usage-stat.service.spec.ts` (11), `emote-drilldown-dialog.spec.ts` (17) | +1, +2, +2 |
| | `usage-export.spec.ts` (10), `import-source-parser.spec.ts` (13), `usage-export-purposes.spec.ts` (9) | +3 |
| | seven-tv-delete.service.spec.ts (100), seven-tv-run-engine.spec.ts, delete-confirm-dialog.spec.ts (19), mass-delete-panel.spec.ts (123), delete-flow.spec.ts | +11 (Alias im Request, Schlüsselraum, beforeStep je Versuch mit frischem Read für Mehrslot-Emotes, renamed/departed/pendingSync, Nachprüfung AK 40, Bericht mit entries samt AddedAt und Retry-Body; AK 51) |
| | seven-tv-restore.service.spec.ts | +4 (slotId und ursprüngliches AddedAt je doneKey, gleiche Historienkarte, verlorene Antwort; AK 46, 55) |
| | `vote-session-detail-page.spec.ts` | +1 |
| | `api-error-locales.spec.ts` | automatisch |
| **Playwright** (`web/e2e/support/test.ts`) | `usage-atlas.e2e.spec.ts` (37) | 1 ersetzt, +4 (Suche, Drilldown, Löschlauf, D7-Karte mit weiterlaufender Historie) |
| | `vote-ballot.e2e.spec.ts`; `support/mocks.ts` | +1; Defaults und Member-Mock mit `addedAt` |
| **Live-Verifikation** (Regel 16) | LAN-Stack, eigener Testkanal mit einem Emote in zwei Slots; `handofblood` lesend | Settle je Set (AK 4–5), Boot ohne Zählung (AK 17), EventAPI (AK 19–20), Backfill lokal (AK 22–26), unsere Mutation (AK 36), Teil-Löschung (AK 37, 41), exakter Restore (AK 42, 46), D7-Wiederaufnahme (AK 47–50) |

Summen: Backend ≈ 114 neue oder ersetzte Tests, ≈ 60 angepasste; Frontend ≈ 49 neue, ≈ 20 angepasste; E2E 6. coverage-local.mjs vor jedem PR.

## 16. Kinder-Issues (noch nicht angelegt)

Jedes Kind lässt an seiner Merge-Grenze Build und alle drei Suiten grün; die drei Migrationen liegen in den Kindern ihrer Schreiber (D19).

| # | Titel | Hängt ab von | Tage |
|---|---|---|---|
| 1 | **M1 `AddEmoteSlots`** (additiv) + Entitäten, `Emote.Slots`, nullable Spalten, `NamesAtCreation`; Seeds je `(EmoteId, EmoteSetId)` und nur dessen `UsageStats`; `AppDbContext`; befüllte `Up`/`Down`-Fixtures und Kollaps-SQL; Kaskaden-Nachweis. Kein Schreiber ändert sich. | — | 2,5 |
| 2 | **Abgleich je Slot:** GetSetEntriesAsync mit alias (Live-Probe, AK 29), ReconcileAsync/UpsertEmoteWithSlots mit Datums-Identität (6.2), D7-Reaktivierung nach Datumstreffern, Settle nur des passenden Set-Seeds (6.3), D13, ArchivedSlotUpsert, IsSlotKeyConflict; SELECT FOR UPDATE auf der owning Emote-Zeile mit erneutem Lesen; Tracker/DuplicateNames/OccupiedSlots über Slots. Zähl-Map, Zähler und Flush bleiben bis Kind 3 auf dem Emote-Schlüssel. AK 27. | 1 | 3,5 |
| 3 | **M2 SwitchUsageStatsToSlotKey** + alle UsageStats-Schreiber + Backfill-Basis: NameToSlotId/Map aus gesettelten Slots, UsageCounterKey(SlotId, …), Zähler, Flush, Kaltstart; einfache Enqueue-Bindung des bisherigen einen Preview-Eintrags je Emote, Emote-Platzhalter/Basisslot, Snapshot mit SlotId und Backfill-Insert mit dieser ID; Fixtures angepasst. AK 44, 56. Multi-Slot-Erweiterung erst Kind 5. | 2 | 3 |
| 4 | **EventAPI je `(Id, Name)`:** Parser, Apply, archivierter Namensvetter → `AmbiguousDelta`/REST, Leave nur beim letzten Slot, Worker-Fallback; Tests (AK 18–21) | 2 | 2,5 |
| 5 | **M3 + Backfill-Mehrslot-Erweiterung:** Enqueue ohne Dedup, vollständige datierte Bindung und D7 für aktive Vorschauen vor Snapshot-Erstellung, Set-Seed-Settle + 409 (Code, api-error.ts, Locales), Snapshot je Eintrag, Zähler/Aggregat je Slot, ValidateBlock je Slot; Operations-Schritt 3b; Tests (AK 22–27, 39, 53). | 3 | 2,5 |
| 6 | **Lese-API und Berichte:** /totals, /daily, /series je Slot, Vote-EmoteNames/NamesAtCreation, D29, SyncInSetRequest.Entries mit SlotId und Lebenszyklus-AddedAt, Ursprungskanal-Validierung und sichere Fan-out-Auflösung für Shared Sets, stale/no-op Restore- und Delete-Berichte; Infrastructure- und Api.Tests (AK 28–33, 41–42, 46, 52, 54–55) | 3 | 4 |
| 7 | **Frontend Modell und Seite:** `slotKey`, `mergeSetView` (D22), Karten (D23), Bänder/Rang/Auswahl/Serien je Slot, Drilldown, Export, Dedupes, i18n, Vote-Detail; D7 zeigt bei Wiederaufnahme dieselbe Karte und Nutzung mit neuem Datum; Vitest, E2E, Mocks (AK 34–35, 46). Löschlauf bleibt je ID (heutiger Code). | 6 | 3 |
| 8 | **Massenlöschung je Slot:** Mutation mit alias, Queue je slotId, Ausschluss provisorischer Zeilen, beforeStep mit frischem Live-Read vor jedem Versuch für Mehrslot-Emotes, Nachprüfung (D33), Delete-/Restore-Berichte mit AddedAt-Generation; Restore-Queue speichert SlotId und ursprüngliches Datum; Live-Nachweis (AK 36); Vitest, E2E (AK 36–38, 40, 42, 46, 51, 55) | 7, 6 | 3 |
| 9 | **Betrieb und Abnahme:** Operations.md, Architectur.md, DECISIONS final, Feature-Ideen A11, CLAUDE.md; Abnahmelauf LAN (AK 4–5, 14, 17–27, 34–42, 46–56); Runbook-Vorlage. D8: R5 ist die letzte Review-Runde und die Spec geht ohne Runde 4 in den PR. | 1–8 | 1,5 |
| | **Summe** | | **25,5** |

```
1 ──► 2 ──┬──► 3 ──┬──► 5 ──────────┐
          │        └──► 6 ──► 7 ──► 8 ──┼──► 9   (9 mergt zuletzt; Deploy erst nach 9)
          └──► 4 ───────────────────────┘
```

Begründung: **1** additiv, kein Schreiber (R1 Befund 8). **2** pflegt Slot-Reconcile und D7, lässt die bestehende Zähl-Map noch unangetastet. **3** ist die nicht-additive Grenze: M2 **und jeder** `UsageStats`-Schreiber — Flush, Zähler, Map **und Backfill-Insert** — in einem PR (R2 Befund 7). **4** braucht nur 2. **5** braucht 3 und trägt M3 mit dem Snapshot-Schreiber. **6** ist der Wire-Vertrag für 7 und bringt die Berichte; **7** shippt die Seite mit dem heutigen Löschlauf; **8** schaltet auf „je Slot" um. **9** Integrationsschwanz. Kinder 1–8 mergen auf `main`, ohne dass Prod sie sieht (`PendingMigrationGuard`).

## 17. Aufwand je Komponente

| Komponente | Tage |
|---|---|
| M1, Entitäten, Seeds je UsageStats-Set, Migrationstests (Kind 1) | 2,5 |
| Abgleich je Slot mit Datums-Identität, D7, Settle je Set, Overlay, Live-Probe (Kind 2) | 3,5 |
| M2 + alle UsageStats-Schreiber inkl. einfacher Backfill-Slotbindung und Snapshot-SlotId (Kind 3) | 3 |
| EventAPI inkl. archiviertem Namensvetter (Kind 4) | 2,5 |
| M3 + Backfill-Mehrslot-Auflösung, D7-Previewbindung, Enqueue-Settle je Set, Validator, 409 (Kind 5) | 2,5 |
| Lese-API + Votes + Tags + Berichte mit Lebenszyklus-AddedAt und Shared-Set-Fan-out (Kind 6) | 4 |
| Frontend Seite/Modell/Export/i18n/E2E (Kind 7) | 3 |
| Frontend Löschung/Restore je Slot mit frischem Read vor jedem Mehrslot-Versuch, AddedAt-Generation-Berichten und Nachprüfung (Kind 8) | 3 |
| Betrieb, Docs, Abnahme, Runbook (Kind 9) | 1,5 |
| **Summe** | **25,5** (plus das Wartungsfenster, 15 min geplant) |

## 18. Dateireferenz

**Core:** `Entities/{EmoteSlot (neu),Emote,UsageStat,ChatLogBackfillRunEmote,VoteSessionEmote}.cs` · `Services/{IEmoteMatchCache,IUsageStatFlushService,IUsageStatQueryService,IChatLogBackfillService,IEmoteSetStatusService,IVoteSessionQueryService,IEmoteService,DuplicateEmoteNameDto}.cs` · `SevenTv/SevenTvModels.cs` · `Matching/EmoteNameMatching.cs` (**unverändert**).

**Infrastructure:** `Persistence/AppDbContext.cs` · `Migrations/{AddEmoteSlots,SwitchUsageStatsToSlotKey,SwitchBackfillSnapshotToSlotKey}.cs` · `Services/{SevenTvSyncService,UsageStatFlushService,UsageStatQueryService,EmoteSetStatusService,ChatLogBackfillService,VoteSessionService,VoteSessionQueryService,EmoteTagService,EmoteService,ArchivedEmoteRowUpsert,ArchivedSlotUpsert (neu),EmoteMatchCache}.cs` · `SevenTv/{SevenTvApiClient,SevenTvApiDtos,SevenTvDispatchParser}.cs`.

**Worker:** `TwitchChatManager.cs` · `EmoteUsageCounter.cs` · `IEmoteUsageCounter.cs` · `Worker.cs` · `SevenTv/SevenTvEventClient.cs` · `ChatLogBackfill/{ChatLogBackfillWorker,ChatLogBackfillBlockCounter}.cs` · `Harness/*` (**unverändert**).

**Api:** Endpoints/UsageStatsEndpoints.cs · Endpoints/SevenTvEndpoints.cs (SyncInSetRequest.Entries, SyncInSetEntry.AddedAt für die Entry-Generation) · Endpoints/ChannelEndpoints.cs (409) · Validation/ApiErrorCodes.cs (BackfillSlotsUnsettled).

**Web:** `core/usage-stats/{usage-stat.model,merge-set-view,usage-stat.service}.ts` · `core/i18n/api-error.ts` · `features/usage-stats/usage-stats-page.{ts,html}` · `shared/emotes/emote-drilldown-dialog.ts` · `shared/export/{usage-export,import-source-parser,usage-export-purposes}.ts` · `shared/seven-tv/{mass-delete-panel,delete-confirm-dialog,delete-flow}.ts` · `core/seven-tv/{seven-tv-delete.service,seven-tv-restore.service,seven-tv-run-engine (beforeStep-Nutzung),seven-tv-emote-set.service}.ts` · `core/voting/vote-session.model.ts` · `features/voting/vote-session-detail-page.{ts,html}` · `public/i18n/{de,en}.json` (`usageStats.setView.alsoAs`, `usageStats.delete.{renamedSinceLoad,pendingSync,unexpectedRemoval}`, `errors.api.backfill_slots_unsettled` neu; `usageStats.setView.slots` entfällt; Backfill „Einträge") · `e2e/{usage-atlas,vote-ballot}.e2e.spec.ts` · `e2e/support/mocks.ts`.

**Docs:** `docs/DECISIONS.md` · `docs/Operations.md` · `docs/Architectur.md:242-298` · Backfill-Spec (Fußnoten) · `docs/Feature-Ideen-2026-08-01.md` (A11) · `CLAUDE.md`.

## 19. Out of Scope

- Namenshistorie des Chat-Archivs (Backfill-Spec Out of Scope bleibt).
- Slot-genaue Import-Berichte (`sync-imported` bleibt Audit je ID; Import-Queue je ID, K2); Leaderboard; Fremd-Set-Vorschau.
- Tags je Slot; Votes je Slot (D3).
- Rückbau von `SeedUnsettled`/`AddedAtFromRest`.
- Harness (D20).
- Wortlaut „Emotes" → „Einträge" in Bandüberschriften (D1).
- Mehr als eine Api-/Worker-Replica; eine kanalweite Postgres-Zeilensperre für den Sync (6.5 nutzt nur eine Emote-Zeilensperre).
- Ein 7TV-Selektor über `added_at` (D33 — läge bei 7TV).

## 20. Verwandt

#365 · #346 · #74/#341 (abgelöst) · #200 (Präzedenz Wartungsfenster) · #45 · #201 · #230/#253 (Restore je Alias; Restore-Berichte tragen jetzt slotId und addedAt in entries) · #69 (D20).

## 21. Zweitmeinungen (Codex Sol, 2026-10-10) — Befunde und Einarbeitung

**Runde 1** (needs-attention; eingearbeitet in Fassung 2, in Fassung 3 teils durch D4 ersetzt):

| # | Befund | Stand in Fassung 3 |
|---|---|---|
| 1 [high] | Rename in einen archivierten Alias belebt den archivierten Slot | **Behoben durch D4:** Identität ist das Datum; archivierte Namen spielen keine Rolle mehr. AK 7 |
| 2 [high] | 1:1-Regel macht Entfernen + Hinzufügen zur Umbenennung | **Behoben durch D4/D16:** keine 1:1-Vermutung zwischen autoritativen Seiten; `AddedAtFromRest`. AK 7 |
| 3 [high] | Zählung vor dem Settle | **Behoben (D12):** Seeds nicht in der Map, Dispatch → `AmbiguousDelta`. AK 17 |
| 4 [high] | Archivierte Seeds vs. späterer Backfill | **Behoben (D12/8):** Enqueue settelt bei lauter datierten Einträgen, sonst 409. AK 26 |
| 5 [high] | Löschung je Slot meldet Emote-weite Leave | **Behoben (D32):** `Entries` je `SlotId`, Leave nur beim letzten Slot. AK 41 |
| 6 [high] | Namens-Schlüssel lenkt Markierung um | **Behoben (D21):** `slotId`, Bindung über `(id, addedAt)`. AK 35, 38 |
| 7 [high] | `ValidateBlock` je `(EmoteId, Date)` | **Behoben (8).** AK 24 |
| 8 [medium] | Kinderfolge bricht Suiten | **Behoben (D19).** AK 44 |

**Runde 2** (6 high / 1 medium gegen Fassung 2; Betreiber-Mandat: Fundament vereinfachen — D4):

| # | Befund | Verdikt | Einarbeitung |
|---|---|---|---|
| 1 [high] | Autoritative Identität muss die Wiederverwendung eines **aktiven** Alias überleben (A→C, B→A): Namenstreffer vor Datum überschrieb das Datum | **Behoben** | D4/D16: datierte Einträge binden **zuerst** über `(EmoteId, AddedAt)`, Namen nur noch für provisorische/undatierte (D11). AK 7 (Tausch-Gegenbeispiel) |
| 2 [high] | Identität über Löschversuche und Bericht-Retries: Bindung nur einmal vor dem Lauf; `Entries` nur id+alias | **Behoben** | `beforeStep` je Versuch mit Live-Listen-Erneuerung nach Pausen (9.3, D21); `Entries` tragen `slotId`, der Server archiviert die `SlotId` (D32); provisorische Zeilen nie gelöscht. AK 38, 41. Verbleibende Lücke eines Request-Umlaufs: **D33, dokumentiertes Restrisiko** (Wahrscheinlichkeit: zwei fremde Umbenennungen desselben Emotes innerhalb von ~100–500 ms nach unserem Read; Folge: ein fremder Eintrag entfernt, nach dem Lauf erkannt, per Restore reparierbar) — nicht schließbar ohne einen 7TV-Selektor über `added_at` |
| 3 [high] | Absorption invalidiert den gecachten Snapshot eines laufenden Workers | **Entfallen** | Die Absorption ist gestrichen: zwei Schreiber desselben Eintrags treffen denselben Schlüssel `(EmoteId, AddedAt)` (6.5, D28); kein Schreiber führt Slots mehr zusammen oder löscht sie (8). AK 27 |
| 4 [high] | Legacy-Restore kann nicht jeden archivierten Slot reaktivieren (doppelte archivierte Namen, Namensindex) | **Behoben in R3** | R3 vermied eine Namensauswahl und trug `entries`; D7 ändert den Operatorpfad in R4: Restore meldet die exakte `SlotId`, externe Wiederaufnahme wählt erst nach Datumstreffern deterministisch nach jüngstem Archivzeitpunkt. AK 42, 46, 49 |
| 5 [high] | Wiederaufnahme behält ein autoritatives Datum des vorigen Eintrags | **Behoben** | D7 behält die Nutzung, setzt aber beim Restore provisorisch und bei externer Wiederaufnahme auf das neue REST-`added_at` um; das frühere Datum bleibt nie als Schlüssel bestehen. AK 46–49 |
| 6 [high] | Undatiertes Enqueue-Settle besiegt D2 dauerhaft | **Behoben** | Settle nur bei lauter datierten Preview-Einträgen, sonst 409 ohne Schreiben; Marke bleibt (8, D12). AK 26 |
| 7 [medium] | M2 vor dem Backfill-Insert ohne `SlotId` | **Behoben** | Kind 3 enthält den Backfill-Insert mit `SlotId` und die Fixtures (D19, 16). AK 44 |

Was die Datums-Identität aus Fassung 2 **gestrichen** hat: den partiellen Namensindex `IX_EmoteSlots_EmoteId_Name_Active`, die Absorptionsregel (6.5 alt), die Rückkehr-Regel nach Namen mit Kandidatenordnung (D16-4 alt), die 1:1-Umbenennungsvermutung zwischen autoritativen Seiten (D16-3 alt), die Reihenfolgefrage „Name vor Datum" (R2 Befund 1), die 1:1-Paarung in `mergeSetView` (D22 alt), den Alias-Schlüsselraum der Lösch-Queue und die Rename-Konfliktbehandlung mit archivierten Namen im Dispatch-Pfad.

### R4 (2026-10-10) — Quelle: Betreiber-Entscheidung D7 und schreibgeschützte Analyse

| Befund | Entscheidung und Einarbeitung |
|---|---|
| Im aktiven Set erschien eine Rückkehr als neue Karte bei 0; die archivierte Verlaufskarte war dort verborgen. D4s Aussage über ihre Sichtbarkeit widersprach §5.1 und dem heutigen Verhalten. | D7: Restore-Berichte reaktivieren die bekannten `SlotId`s; externe REST-Wiederaufnahme verwendet nach allen exakten Datumstreffern den zuletzt archivierten gleichnamigen Slot, sofern kein aktiver gleichnamiger Slot besteht. Sie behält die Nutzung und bekommt das neue REST-Datum. §5.1, §5.7, §6.2, §7, AK 42 und 46–50. |
| M1/D12 hätte vorhandene `UsageStats` aller `EmoteSetId`s an einen Seed gehängt, den der Abgleich des aktiven Sets nach dem ältesten Eintrag dieses Sets settle. Daten eines anderen Sets wären dadurch an den falschen Slot geraten. | Gewählt: Slots bleiben dem Emote im Kanal zugeordnet, `(EmoteId, AddedAt)` bleibt bestehen. M1 erzeugt einen unsettled Seed je `(EmoteId, EmoteSetId)` mit vorhandenen Zeilen; REST-Abgleich und Backfill settlen nur diesen Seed und die Nutzungszeilen dieses Sets. Das hält `UsageStats.EmoteSetId`-Abfragen getrennt und vermeidet ein neues Slot-Feld bzw. einen neuen Slot-Schlüssel. §3 D2/D12, §11.1, AK 2/4/5/22/26, Kinder 1/2/5. |

Die Runde-2-Befunde bleiben behoben: Datumstreffer werden weiterhin zuerst vollständig gebunden; D7 läuft erst danach. Eine neue D7-Zuordnung ändert `AddedAt` auf das neue REST-Datum, nie auf das Datum des alten Eintritts.

### R5 (Codex Runde 3, 2026-10-10) — Abschluss gemäß D8

| # | Befund | Einarbeitung und pass/fail-Nachweis |
|---|---|---|
| 1 [high] | Fortlaufender Löschlauf verwendet eine veraltete Aliasliste | Bei mindestens zwei Live-Einträgen derselben ID wird vor jedem Mutationsversuch frisch gelesen; AK 51 deckt Alias-Neuzuordnung bei Intervallen unter 5 s ab. Nur D33s einzelner Request-Umlauf bleibt. |
| 2 [high] | Verspäteter Delete-Bericht archiviert eine D7-Wiederaufnahme | Entries trägt das autoritative AddedAt der Entry-Generation; ein veralteter Bericht ist ein No-op ohne Leave. AK 52. |
| 3 [high] | Backfill-Enqueue umgeht D7 | Aktive Preview wird vor dem Snapshot mit Datumstreffern und danach D7 aufgelöst und mit REST/Restore serialisiert. AK 53 prüft Enqueue vor REST und Restore. |
| 4 [high] | Kanallokale SlotIds brechen Shared-Set-Fan-out | SlotId nur im Ursprung validieren; andere Kanäle über lokale AddedAt-Identität auflösen, sonst REST überlassen. AK 54 mit unterschiedlichen lokalen SlotIds. |
| 5 [high] | Eindeutigkeitsindex erkennt Aliasänderung nicht als gleichen Eintrag | REST und Enqueue verwenden dieselbe Emote-Zeilensperre mit SELECT FOR UPDATE und lesen danach erneut; echte Zeitstempel-Gleichstände bleiben namensgetrennt. AK 27 erweitert. |
| 6 [medium] | Restore-Retry oder REST-vor-Report wird fälschlich abgelehnt | Bereits aktiver Slot und überholte Generation antworten erfolgreich ohne Mutation oder Datums-Reset. AK 55 deckt verlorene Antwort und REST-vor-Report ab. |
| 7 [medium] | Kind 3 benötigt Snapshot-SlotId vor Kind 5 | Einfache Slotbindung und Snapshot-SlotId kommen mit M2 in Kind 3; Kind 5 macht nur die Mehrslot-Erweiterung. AK 56 verlangt einen echten Enqueue→ReplaceBlock-Durchlauf für ein neues Emote. |

Alle sieben Befunde sind eingearbeitet. D8 setzt R5 als Abschluss fest; es folgt keine vierte Review-Runde.
