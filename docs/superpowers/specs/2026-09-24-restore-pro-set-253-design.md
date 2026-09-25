# Restore pro Set: Restore ohne Kanalseite, set-zentrische Meldungen, Aufhebung der Replace-Sperre — Spec

**Datum:** 2026-09-24 · **Status:** Zweite Fassung — die sieben offenen Punkte der ersten Fassung sind vom Betreiber entschieden (Abschnitt 13), und die sechs Befunde der adversarialen Zweitmeinung (Codex Sol, gpt-6-sol: H1–H4, M5, M6) sind eingearbeitet (Abschnitt 14); gegen den Code auf `feat/253-restore-per-set` (auf `chore/200-sync-main`, PR #263) belegt · **Issues:** #253 (Hauptissue), **#224** (wird durch diese Spec mit Verweis geschlossen, nicht separat gefixt), #256 Punkt 3 (erledigt sich) · **Epic:** #200 · **Vorgänger:** #230 (PR #251), [Plan-230-Namenskonflikte.md](../../plans/Plan-230-Namenskonflikte.md) T6 „Entfallen", T7b, Abschnitte 7 und 8 · **Formatvorlage:** [2026-09-20-emote-sets-200-spec.md](2026-09-20-emote-sets-200-spec.md) (Abschnitte 6.6, 6.7, 7.2, 8.6, F7) · **Nicht Teil:** #254 (Rückgängigmachen eines geglückten Replace), #255 (Wortlaute)

Diese Spec ist ein Denkwerkzeug des Betreibers und deshalb deutsch; Bezeichner, Routen und
Wire-Felder bleiben englisch. Sie enthält keinen fertigen Code — Verträge, Verhalten, Grenzfälle,
Fallen und prüfbare Akzeptanzkriterien. Zeilenangaben sind am 2026-09-24 gegen den Branch
nachgeprüft. Kleine, eindeutige Präzisierungen sind im Vertrag als **Festlegung** markiert; was
nach der zweiten Fassung noch offen ist, steht in Abschnitt 15.

---

## 0. Auftrag in einem Satz

Ein Nutzer kann **jede** Rückweg-Datei — Purge-Protokoll, Rückweg-Datei (`planned`) oder
Ergebnisprotokoll (`finished`) einer Übertragung — auf **jeder** Kanalseite einlesen; **die Datei
bestimmt das Ziel-Set**, die App prüft nur noch, ob der Nutzer dieses Set bearbeiten darf — mit
**derselben** Entscheidung, die das Backend bei der Meldung trifft —, und meldet Löschung wie
Wiederherstellung **set-zentrisch** an die Api (`POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted`
und `…/sync-restored`), die Zeilen ändert, den erwarteten Kanal gegenprüft und jeden getroffenen
Kanal per Resync gegen 7TV absichert — sodass auch ein ungetracktes Set einen Rückweg und eine
Papierspur hat, womit die Sperre „Ziel ersetzen nur für getrackte Ziele" aus #230 fällt und #224
gegenstandslos wird.

---

## 1. Ausgangslage — was am Code steht

Alles Folgende ist am Branch nachgeprüft; es ist die Begründung für jede Änderung in den
Abschnitten 4 bis 6.

**Der Restore-Weg ist kanalgebunden, von vorn bis hinten.**

- `ImportTrigger.openDialog` friert `channelName` und `setId` der Seite beim Klick ein und reicht
  sie an `openImportSourceDialog` und weiter an `startRestoreFlow`
  (`web/src/app/shared/seven-tv/import-trigger.ts:164-200`, `import-source-dialog.ts:160-164`).
- `FileImportStep` verlangt beide als Pflicht-Inputs (`file-import-step.ts:83-89`) und gibt sie
  beiden Restore-Parsern als Erwartung mit (`:151-173`).
- `parsePurgeRunProtocol` verlangt `envelope.channelName === Seite` (`wrongChannel`) und
  `meta.emoteSetId === gewähltes Set` (`wrongSet`) (`web/src/app/shared/export/purge-run-export.ts:192-201`);
  `parseTransferRunForRestore` vergleicht `meta.targetChannelName` und `meta.targetEmoteSetId`
  genauso (`transfer-run-export.ts:385-390`). Beide Vergleiche sind `!==`, also
  groß-/kleinschreibungs-genau — das ist #256 Punkt 3.
- `startRestoreFlow(deps, channelName, setId, setName, isActiveSet, rows)` nimmt den Kanal als
  Pflichtparameter (`restore-flow.ts:56-63`) und liest die Slot-Vorschau je nach `isActiveSet` über
  `getSetStatus(channelName)` oder `loadEmoteSetPreview(channelName, setId)` (`:71-91`).
- `SevenTvRestoreService.startRestore(setId, channelName, …)` hält `RestoreRunInfo { channelName,
  setId, result }` (`seven-tv-restore.service.ts:80-87`, `:169-198`), meldet über
  `EmoteAdminService.syncRestored(channelName, { emoteSetId, sevenTvEmoteIds })` an die
  **kanalgebundene** Route (`:268-297`) und stößt danach `channelService.resync(channelName)` an
  (`:255-265`). `resetIfChannelChanged(channelName)` vergleicht mit `run.channelName` (`:216-221`)
  und wird aus `channel-workspace-layout.ts:151-160` mit dem Kanal der Seite aufgerufen.
- Der Delete-Lauf meldet genauso kanalgebunden (`seven-tv-delete.service.ts:278-310`), der
  Replace-Lauf des Imports ebenso über `reportRemoved` (`seven-tv-import.service.ts:687-707`).
- Ein dritter Restore-Einstieg existiert neben der Datei: der „Wiederherstellen"-Knopf am
  fertigen Delete-Lauf im `MassDeletePanel` (`mass-delete-panel.ts:628-660`), der `run.setId` und
  `run.channelName` des Delete-Laufs an dieselbe Kette gibt.
- **Der Einstieg und das Restore-Dock hängen am gewählten Set der Seite.** Kopier-Knopf und
  `ImportTrigger` stehen im Block `@if (!isCoarse() && selectedEmoteSetId(); as selectedSetId)`
  (`usage-stats-page.html:122-135`), das `MassDeletePanel` — und mit ihm die Restore-Fortschrittsanzeige
  (`mass-delete-panel.ts:242-275`) — im Block `@if (selectedEmoteSetId(); as setId)` (`:1058`). Nur
  `app-import-progress-section` steht außerhalb (`:1185`). Ein Kanal ohne aktives Set hat damit
  weder einen Datei-Einstieg noch ein Restore-Dock.

**Die Sperre aus #230.** Regel 7 `replaceNeedsTrackedTarget` in `validateResolution`
(`web/src/app/shared/seven-tv/conflict-resolution.ts:159-163`, `:548-562`, Kontextfeld
`ResolutionContext.targetIsTracked` `:44-51`), im Dialog als `targetIsTracked:
this.data.targetChannelName !== null` gesetzt (`import-confirm-dialog.ts:639-642`) und als
ausgegraute Option mit Grund gezeigt (`de.json:1234` „Nur für getrackte Kanäle wiederherstellbar",
`:1254`; `en.json:1254`; Schlüssel `import.resolve.violation.replaceNeedsTrackedTarget` in
`import-conflict-resolution-step.ts:88`). Zweiter Wächter: `SevenTvImportService.startImport` wirft
bei einem Replace gegen `target.channelName === null` (`seven-tv-import.service.ts:376-379`).

**Die Api kennt zwei Formen der kanalgebundenen Buchführung — nur eine davon ist in Produktion.**

- `main` (Produktion): `POST /api/channels/{channelName}/emotes/sync-deleted|sync-restored` mit
  Body `{ emoteIds: Guid[] }` und `IEmoteService.MarkDeletedAsync(channelName, emoteIds, actor)`
  bzw. `MarkRestoredAsync` (`git show origin/main:src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs`
  Zeilen 47-105, 274-276; `origin/main:…/IEmoteService.cs:24,29`). Sie ändert Zeilen: archiviert
  bzw. reaktiviert die Guid-Treffer des Kanals (`EmoteService.cs:29-42`, `:82-94`) — ohne zu
  wissen, welches Set der Client meinte.
- Epic-Branch zusätzlich (K5, Spec-200 6.6): dieselbe Route mit Body `{ emoteSetId, sevenTvEmoteIds
  }` und der set-scoped Überladung `MarkDeletedAsync(channelName, emoteSetId, sevenTvEmoteIds,
  actor)` (`src/EmotePurge.Core/Services/IEmoteService.cs:50,53`;
  `src/EmotePurge.Infrastructure/Services/EmoteService.cs:115-239`), Validierungsleiter
  `ValidateSyncBookkeepingBody` (`src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs:269-315`),
  Antwortfeld `targetIsActiveSetOfChannel` (`:134-139`, `:182-187`). **Diese Form lief nie in
  Produktion.** Genau in ihr steckt #224: ein unbekannter Kanal liefert
  `TargetIsActiveSetOfChannel: false` **ohne** Audit-Eintrag (`EmoteService.cs:120-124`,
  `:187-191`), und beide Frontend-Dienste lesen `false` als Papier-Erfolg
  (`seven-tv-restore.service.ts:289-293`, `seven-tv-delete.service.ts:305`).
- Die ganze Gruppe `/api/channels/{channelName}/emotes` steht hinter
  `UsageStatsAccessAuthorizationFilter` (`EmoteEndpoints.cs:25-30`); der Filter lässt Admin,
  Broadcaster, Live-Moderator und 7TV-Editor durch (`ChannelAccessService.cs:34-63`).

**Der set-zentrische Baukasten existiert bereits — für `sync-imported`.** Die `MapGroup`
`/api/seventv/emote-sets/{emoteSetId}` mit `RequireAuthorization`, `EmoteSetIdValidationFilter`
(Routenwert, `EmoteSetIdValidationFilter.cs:27-35`) und Policy `Bookkeeping`
(`SevenTvEndpoints.cs:198-201`); der Handler mit Besitzprüfung
`IImportTargetOwnershipService.CheckAsync` und den Ausgängen 404 `emote_set_not_found` / 403 bare
/ 503 `foreign_channel_seventv_unavailable` **ohne** Audit-Eintrag (`:203-252`); die Prüfung
selbst über die **gecachten** Set-Listen und die geschützten Editor-Grants, mit genau **einem**
budgetierten Owner-Lookup nur für ein Set, das in keiner Liste steht
(`ImportTargetOwnershipService.cs:54-104`, Doku `IImportTargetOwnershipService.cs:59-79`). **Ihre
Regel:** ein Set ist zulässig, wenn es in der Liste eines geprüften Accounts steht **und** sein
`OwnerSevenTvUserId` die 7TV-ID eines geprüften Accounts ist (`:15-20`, `OwnershipEvidence`
`:252-311`) — ein Set, das unter Account A gelistet ist, aber Konto B gehört, ist nur zulässig,
wenn B ebenfalls geprüft wird. Beide IDs trägt das Listenmodell (`EmoteSetSummary.OwnerSevenTvUserId`,
`EmoteSetList.SevenTvUserId`, `ISevenTvEmoteSetListService.cs:34-62`), **aber keine davon steht
auf dem Draht der Zielliste**: `EmoteSetTargetAccount` und `EmoteSetTargetSummaryDto` kennen sie
nicht (`SevenTvEndpoints.cs:413-427`). Der Papier-Eintrag `MarkImportedToSetAsync` schreibt
`ChannelName = null`, `TargetType = "emoteSet"`, `targetOwnerSevenTvUserId`/`targetOwnerTwitchLogin`
(`EmoteService.cs:299-330`). Die Audit-Ansicht liest `emoteSetId` für `syncDeleted`/`syncRestored`
und `targetOwnerTwitchLogin` generisch (`AuditLogQueryService.cs:153-162`, `:237-263`) und rendert
„für <ownerLogin>" bzw. „nicht das aktive Set" (`audit-row.ts:128-146`).

**Die Datei kennt ihr Ziel.**

- Purge-Protokoll: `meta.emoteSetId` ist Pflichtfeld **seit der ersten Fassung** (Commit
  `fe53329b`, `PurgeRunMeta.emoteSetId`), `envelope.channelName` trägt den Kanal des Delete-Laufs
  (`purge-run-export.ts:65-71`, `:87-101`). Ein Protokoll **ohne** `meta.emoteSetId` gibt es nicht;
  der Parser weist es als `wrongKind` ab (`:196-198`). Beide Formatversionen (`1` vor K5,
  `PURGE_RUN_FORMAT_VERSION = 2`) tragen das Feld.
- Übertragungsdatei: `meta.targetEmoteSetId`, `meta.targetChannelName` (`null` bei ungetracktem
  Ziel), `meta.targetOwnerDisplayName`, `meta.stage` (`transfer-run-export.ts:74-115`); der
  Envelope-`channelName` ist bei ungetracktem Ziel `''` (`:219`, `:260`) und wird vom
  Restore-Parser bewusst nicht gelesen (DECISIONS 2026-09-23, Zeile 696-698).
- Die Datei ist **nicht vertrauenswürdiges JSON** — jeder Wert daraus wird geprüft, keiner wird
  einem Wert der Seite gleichgesetzt.

**Die Zielliste des Pickers ist die Quelle für „darf ich dieses Set bearbeiten".**
`GET /api/seventv/me/emote-set-targets` (Spec-200 6.2, `SevenTvEndpoints.cs:127-186`) liefert je
Account (eigener + `editor_of`) `trackedChannelName`, `activeEmoteSetId`, `twitchLogin` und die
Set-Liste mit `kind`, `isActive`, `ownerDisplayName` (`seven-tv-emote-set.model.ts:50-87`);
Frontend-Client `SevenTvEmoteSetService.listEmoteSetTargets()` (`seven-tv-emote-set.service.ts:72-74`),
ohne Client-Cache (der Picker lädt je Öffnen, `import-target-dialog.ts:384-386`); Policy
`ForeignEmoteLookup` = **10 Permits je Nutzer und Minute** (`RateLimitingOptions.cs:66`,
`RateLimitRejection.cs:42`), serverseitig 60-s-Redis-Cache je Twitch-ID mit Koaleszierung (E6/E12
der Spec-200). Die Besitzprüfung des Backends liest **dieselben** Listen
(`ImportTargetOwnershipService.cs:59-72`).

**Was den Rest der Seite nach einer Meldung bewegt, und was ein Resync ist.** `channel.synced` wird
von den kanalgebundenen Routen nur bei `NewlyArchivedCount > 0` bzw. `NewlyRestoredCount > 0` und nur
für den Routenkanal veröffentlicht (`EmoteEndpoints.cs:129-132`, `:177-180`, `:409-`). Die
Nutzungsseite reagiert darauf mit einem lauten Reload (Totals, Set-Status, Set-Liste, Mitgliederliste
einer nicht-aktiven Ansicht; `usage-stats-page.ts:1900-1933`). Ein **Resync** ist
`IChannelService.TriggerResyncAsync(channelName, actor)`: lädt die Kanalzeile, verlangt
`IsBotActive`, schreibt einen `channel.resync`-Audit-Eintrag und veröffentlicht den RESYNC-Befehl an
den Worker, der das **aktive** Set des Kanals gegen 7TV abgleicht und dabei **unbedingt**
`channel.synced` veröffentlicht (`ChannelService.cs:250-274`, `docs/Architectur.md:68`). Der
manuelle Weg `POST /api/channels/{name}/resync` steht hinter `UsageStatsAccessAuthorizationFilter`,
der Policy `ChannelResync` (5/min je Nutzer) und einem **Redis-Cooldown je Kanal von 60 s**
(`ChannelEndpoints.cs:217-263`, `ChannelResyncCooldown.cs:21`,
`SevenTv:ManualResyncCooldownSeconds`); der Admin-Resync umgeht den Cooldown bewusst
(`AdminEndpoints.cs:336-354`). Der Cooldown ist laut seiner eigenen Doku ein Werkzeug des
Endpunkts, nicht des Services (`IChannelResyncCooldown.cs:20-24`) — die Meldungs-Endpunkte dieser
Spec dürfen ihn deshalb ebenso benutzen. Der Restore-Dienst stößt heute nach jedem Lauf den
manuellen Resync des Seitenkanals an; für ein nicht-aktives Set der eigenen Seite ist **dieser
Resync** das, was die Mitgliederliste der Ansicht nachlädt.

---

## 2. Entscheidungen dieser Spec

E1–E6 sind die vom Betreiber freigegebenen Grundentscheidungen; E7–E16 die Festlegungen der ersten
Fassung, alle am 2026-09-24 vom Betreiber bestätigt (Abschnitt 13); E17–E24 die Entscheidungen aus
der adversarialen Zweitmeinung (Abschnitt 14).

| # | Frage | Entscheidung | Begründung / Beleg |
|---|---|---|---|
| E1 | Woher kommt das Ziel eines Restore? | **Aus der Datei**, nie aus der Seite: Übertragungsdatei ⇒ `meta.targetEmoteSetId`; Purge-Protokoll ⇒ `meta.emoteSetId`. Die Prüfung „falscher Kanal / falsches Set gegen die Seite" entfällt ganz, für **alle** Rückweg-Dateien. **Es gibt genau diese zwei Klassen** — eine „alte Purge-Datei ohne Set-ID" existiert nicht (F1), und der Betreiber hat die Klasse am 2026-09-24 ersatzlos gestrichen | Der Halloween-Wechsel am 01.10. macht ein Protokoll, das ein inzwischen nicht-aktives Set nennt, zum Normalfall — heute ein `wrongSet` (`purge-run-export.ts:199-201`, Text „der Channel hat das aktive Set gewechselt", `de.json:936`). Der Hinweis dafür ist die bestehende Zeile „dieses Set ist gerade nicht aktiv" |
| E2 | Was tritt an die Stelle der Kanalprüfung? | Eine **Zielprüfung**: das Set muss in der Antwort von `GET /api/seventv/me/emote-set-targets` stehen, mit `kind === 'NORMAL'` **und `editable === true`** (E19). Scheitert sie, gibt es einen Fehler mit Grund und **keinen** Lauf | Dieselbe gecachte Quelle wie der Ziel-Picker und wie die Besitzprüfung des Backends — und seit E19 auch **dieselbe Entscheidung**, weil das Backend sie in die Liste schreibt. `kind === 'NORMAL'` ist die positive Regel aus Spec-200 8.6 (E7); ein persönliches Set ist damit nicht wiederherstellbar (E11) |
| E3 | Wohin melden Delete, Restore und die Replace-Löschung? | An die **set-zentrischen** Routen `POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted` und `…/sync-restored`. **Alle** Aufrufer wechseln; die kanalgebundene set-scoped Form `{ emoteSetId, sevenTvEmoteIds }` samt Überladung `(channelName, emoteSetId, …)` und Antwortfeld `targetIsActiveSetOfChannel` **entfällt** | Sie existiert nur auf dem Epic-Branch (Abschnitt 1), hat nach dem Wechsel keinen Aufrufer, und #224 sitzt genau in ihr |
| E4 | Bleibt die kanalgebundene Altform `{ emoteIds }` (Guid)? | **Ja, bis zum E3-Tor der Spec-200** (§21, Folge-Issue 1: hinter K7, ≥ 14 Tage, keine Altform-Log-Zeile) — aber **nur noch als Audit + Resync**: sie ändert **keine Zeile mehr** (H4). Route, Filter, Policy und Log-Zeile bleiben; die Guid-Überladung schreibt den Audit-Eintrag mit `legacyBodyForm: true`, stößt den Resync des Kanals unter dem Cooldown an und antwortet in der alten Form (5.6) | Aufruferprüfung (5.6): nach dem Wechsel gibt es in `web/src` **keinen** Code-Aufrufer der kanalgebundenen Routen mehr — der einzige verbleibende Aufrufer sind **Browser-Tabs mit dem Produktions-Frontend** über K7 hinweg; ihre Meldung kommt **nach** der 7TV-Mutation, ein 404 dort kostete die Papierspur. Aber ein solcher Tab weiß nichts vom Set: nach einem Set-Wechsel archivierte er eine Zeile, deren Emote im neuen aktiven Set liegt (H4). Der Resync stellt in beiden Richtungen den 7TV-Stand her; die Papierspur bleibt |
| E5 | Wer darf melden? | Wer das Set laut 7TV **bearbeiten** darf — Besitzer oder `editor_of` des Besitzers, geprüft über `IImportTargetOwnershipService.CheckAsync` mit dem **eingeloggten** Twitch-Konto. Die Kanalrolle (Admin-Allowlist, Broadcaster, Live-Moderator) entscheidet nicht mehr | Deckt sich mit 7TV: ohne Editor-Recht des Token-Kontos scheitert schon die Mutation (`LACKING_PRIVILEGES`, `abortsForMissingPrivileges` im Engine). **Betreiber, 2026-09-24:** HandOfBloods Mod-Team arbeitet mit **eigenen** 7TV-Editor-Rechten, nicht mit geteiltem Token — ein 403 bei der Meldung gibt es damit nur bei echtem Rechteentzug (F4) |
| E6 | Audit für ungetrackte und nicht-aktive Ziele | Ein Set-Eintrag `TargetType = "emoteSet"`, `TargetId = emoteSetId`. **Ohne Kanal** (`ChannelName = null`, wie heute `MarkImportedToSetAsync`) nur für ein **ungetracktes** Ziel — sichtbar in der globalen Admin-Ansicht, nicht in einem Kanal-Feed. Hat der Besitzer-Account des Sets einen getrackten Kanal, trägt der Eintrag **dessen Kanal** und steht damit auch in der Kanal-Audit-Ansicht (Nachtrag N3; die erste Fassung schrieb hier immer `null`, was gegenüber dem Stand vor #253 eine Regression war) | Kein Schema-Wechsel — `AuditLogEntry.ChannelName` ist nullable (`AuditLogEntry.cs:72`), `AuditActions.EmotesSyncDeleted/-Restored` existieren (`:17-18`) |
| E7 | Signaturen der neuen Service-Methoden | `IEmoteService.MarkDeletedInSetAsync(emoteSetId, ownerSevenTvUserId, ownerTwitchLogin, ownerTwitchUserId, sevenTvEmoteIds, expectedChannelName, actor, ct)` und `MarkRestoredInSetAsync(…)` mit identischer Parameterliste (`ownerTwitchUserId` seit Nachtrag N3) | Bestätigt (Abschnitt 13, Punkt 2); `expectedChannelName` kommt mit H2 dazu (E18). Die Besitzer-Felder braucht der Papier-Eintrag, um in der Audit-Ansicht „für <ownerLogin>" zu rendern (`audit-row.ts:136-141`) — dieselbe Signaturform wie `MarkImportedToSetAsync` (`IEmoteService.cs:85-88`); die Twitch-ID des Besitzers löst seinen getrackten Kanal auf (N3) |
| E8 | Woher weiß der Service, welche Kanäle betroffen sind? | Aus `Channels` mit `ActiveEmoteSetId == emoteSetId && IsBotActive`, danach in-memory `!IExcludedChannelFilter.IsExcluded(TwitchChannelId)` — dasselbe Muster wie `ListActiveChannelNamesAsync` (`ChannelService.cs:225-248`). `EmoteService` bekommt `IExcludedChannelFilter` als Konstruktorabhängigkeit | Ein gesperrter Kanal (#252) wird nirgends mehr beschrieben; die Prüfung sitzt im Service, nicht im Endpunkt, damit sie in `Infrastructure.Tests` ohne Api-Pipeline geprüft wird |
| E9 | Antwort-DTO der beiden neuen Routen | `{ reportedCount, channels: [{ channelName, archivedCount \| restoredCount, notFoundIds }], unresolvedChannel: { channelName, reason } \| null, resyncTriggered: string[] }` — `channels` leer **und** `unresolvedChannel` null heißt „nur Papier". Vertrag in 5.3 | Bestätigt; um `unresolvedChannel` (H2/E18) und `resyncTriggered` (H1/E17) erweitert, damit das Frontend dreiwertig liest und keinen zweiten Resync auslöst |
| E10 | Wo läuft die Zielprüfung im Frontend? | Im **`FileImportStep`**, als dritter Prüfschritt nach Envelope und Parser, vor `picked`. Der Schritt zeigt den Fehler in seinem bestehenden Banner; der Dialog schließt erst mit einem geprüften Ziel | Bestätigt (Abschnitt 13, Punkt 7). Der Schritt „liest und prüft die Datei" (UI-Designsprache §7.3) und besitzt den einzigen Fehler-Ort dieser Kette. Er bekommt dafür `SevenTvEmoteSetService` injiziert (`shared/` darf aus `core/` importieren, nicht umgekehrt) und nutzt dessen gemeinsame Vorprüfung (E19) |
| E11 | Datei mit persönlichem Set als Ziel | **Abgewiesen** (`kind !== 'NORMAL'`), mit eigenem Grund | Bestätigt. Kein Picker und kein Dropdown bietet ein persönliches Set an (Spec-200 §34, §35, §39), also kann EmotePurge nie eine Löschung daraus erzeugt haben |
| E12 | Resync nach einem Restore (Frontend-Anteil) | Der **Backend-Resync** aus E17 deckt jeden getroffenen Kanal und den unaufgelösten erwarteten Kanal ab. Der Restore-Dienst stößt selbst **nur noch dann** einen Resync an, wenn das Ziel ein **nicht-aktives** Set eines getrackten Kanals ist — der Fall, den kein Backend-Resync trifft, und der, in dem der Resync die Mitgliederliste der nicht-aktiven Ansicht nachlädt (Spec-200 8.3). Kein Resync für ein ungetracktes Ziel. **Ausnahme (Nachtrag N1):** scheitert die Meldung endgültig, hat das Backend keinen Resync ausgelöst, und der Client stößt ersatzweise den des erwarteten bzw. des nicht-aktiven Kanals an | Bestätigt (Abschnitt 13, Punkt 5) und mit E17 zusammengeführt: **höchstens ein Resync je Kanal und Meldung**, weil das Backend den ausgelösten Resync in `resyncTriggered` sichtbar macht und der Client für einen dort genannten Kanal keinen zweiten anstößt |
| E13 | Was `RestoreRunInfo` trägt | `{ targetSetId, expectedChannelName: string \| null, resyncChannelName: string \| null, hostChannelName, result }` plus Anzeigefelder — das Ziel-Set, der erwartete Treffer (E18), der Kanal aus E12, und der Kanal der Seite, auf der der Lauf gestartet wurde | Bestätigt (Abschnitt 13, Punkt 3): `resetIfChannelChanged` vergleicht mit `hostChannelName`, weil das Layout nur den Kanal der Seite kennt (F7) |
| E14 | Was mit `emote_set_id_empty` geschieht | **Entfällt** aus `ApiErrorCodes`, `api-error.ts` und beiden Locales | Der Code existiert nur für die entfallende Leiter (`EmoteEndpoints.cs:304`, `ApiErrorCodes.cs:102`, `api-error.ts:53`) |
| E15 | Was mit `wrongChannel` und `wrongSet` geschieht | Beide Fehler und ihre Schlüssel **entfallen** aus beiden Parsern und beiden Locales; der Parser gibt das Ziel zurück statt es zu prüfen | Es gibt nach E1 keinen Weg mehr, der sie erzeugt. Die neuen Schlüssel stehen in 6.1; ihr Wortlaut ist vorläufig und gehört #255 |
| E16 | Restore direkt aus dem fertigen Delete-Lauf | Bleibt; das Ziel kommt aus dem Laufdatensatz (`lastRun.setId`, `lastRun.channelName`), die Vorprüfung aus E19 läuft trotzdem — sie ist im Normalfall ein Cache-Treffer (60 s), weil der Delete sie eben gestellt hat | Präzisiert gegenüber der ersten Fassung: seit H3 gilt **eine** Vorprüfung vor jeder ersten Mutation, ohne Ausnahme; der Cache macht sie kostenlos |
| E17 | **H1 — Meldung + Resync** | Die Meldung ändert Zeilen weiterhin **sofort**, geprüft über die gecachten Rechte. Zusätzlich stößt **jede** Meldung, die mindestens einen getrackten Kanal trifft (oder einen erwarteten Kanal nicht auflöst, E18), den Resync **dieses** Kanals an — im Endpunkt, über `IChannelResyncCooldown.TryBeginAsync` und `IChannelService.TriggerResyncAsync`, je Kanal höchstens einmal je Meldung und unter dem 60-s-Cooldown je Kanal. Der Resync stellt den 7TV-Stand wieder her, falls die Meldung gelogen hat | Betreiber-Entscheidung 2026-09-24. Die gecachten Rechte sind strenger als die alte Kanalrollen-Prüfung (5.7); das Restrisiko — bis zu 10 min nach Rechteentzug, Reichweite = Zeilen eines Kanals, Heilung binnen Cooldown bzw. Worker-Tick — steht als F12 und im DECISIONS-Eintrag. Kein Resync-Sturm: ein Lauf meldet je Richtung einmal, je Kanal ein Resync, und der Cooldown schluckt, was innerhalb von 60 s wiederkommt (F15) |
| E18 | **H2 — erwarteter Kanal, Mismatch** | Der Client schickt `expectedChannelName` (den getrackten Kanal des Ziel-Accounts, wenn das Ziel dessen **aktives** Set ist; sonst `null`). Trifft der Service diesen Kanal nicht, antwortet er mit `unresolvedChannel: { channelName, reason: 'notTracked' \| 'activeSetDiffers' }`, zählt dort nichts als geändert, schreibt den Kanal und die IDs in den Papier-Eintrag und stößt bei `activeSetDiffers` den Resync dieses Kanals an. Das Frontend zeigt `partial` mit Grund | Das gespeicherte `ActiveEmoteSetId` kann einem 7TV-Set-Wechsel hinterherhinken; ohne den erwarteten Kanal sähe eine Nur-Papier-Antwort wie Erfolg aus — dieselbe Klasse wie #224. Bei einem geteilten Set kann der Client nur **seinen** Kanal erwarten (F13) |
| E19 | **H3 — eine Entscheidung für Frontend und Backend** | `GET /api/seventv/me/emote-set-targets` liefert zusätzlich je Account `sevenTvUserId` und je Set `ownerSevenTvUserId` und **`editable: boolean`** — vom Backend nach **derselben** Regel berechnet, die `ImportTargetOwnershipService` bei der Meldung anwendet (eine gemeinsame reine Funktion, 5.8). Das Frontend bündelt die Vorprüfung in `SevenTvEmoteSetService.resolveEditableSet(emoteSetId)` über eine 60-s-Client-Kopie der Zielliste, und **jede** erste Mutation — Restore, Delete, Replace — läuft erst nach dieser Vorprüfung. Der Picker deaktiviert nicht bearbeitbare Sets mit Grund | Der schlankere Weg: kein neuer Endpunkt, **kein zusätzlicher 7TV-Request** (beide IDs stehen schon in der gecachten Antwort, `ISevenTvEmoteSetListService.cs:34-62`), ein Permit je Minute für alle Vorprüfungen zusammen. Die eine bewusste Asymmetrie steht in F16 |
| E20 | **H3 — kein vorautorisierter Nachmeldeweg** | Scheitert die Meldung trotz Vorprüfung (Rechteentzug mitten im Lauf), zeigt das Dock den Grund (`syncReportReason`, E23); es gibt **keinen** Weg, die Meldung unter einer früheren Autorisierung nachzureichen | Bewusst ausgelassen (Betreiber): das Mod-Team hat eigene Rechte, der Fall ist ein echter Entzug, und ein Nachmeldeweg wäre eine zweite Autorisierung neben der, die 7TV gerade zurückgenommen hat |
| E21 | **M5 — Hinweis „Ziel ≠ Seite"** | Der Flow bekommt die **gewählte Set-ID der Seite** (`hostSelectedSetId: string \| null`). Der Hinweis entsteht beim Vergleich `target.emoteSetId !== hostSelectedSetId`, nie beim Kanalvergleich; die Bestätigung zeigt neben dem Set-Namen die **aufgelöste Set-ID** | Ein anderes Set desselben Kanals erzeugte sonst keinen Hinweis, und die Datei ist nicht vertrauenswürdig — die ID aus der Zielliste ist die geprüfte |
| E22 | **M6 — Einstieg ohne gewähltes Set** | `ImportTrigger` verlässt das Gate `selectedEmoteSetId()` und steht nur noch hinter `!isCoarse()`; sein `setId` wird `string \| null`. Ohne Set sind im Quellschritt die drei Kopier-Türen mit Grund deaktiviert, der Datei-Zweig bleibt offen und liest nur Rückweg-Dateien. Das Restore-Dock zieht aus dem `MassDeletePanel` in eine eigene `RestoreProgressSection` **außerhalb** des Set-Gates, nach dem Muster von `ImportProgressSection` | Ein Host-Kanal ohne aktives Set (z. B. nach einem Replace ins Ungetrackte, oder ein Kanal vor dem ersten Sync) hätte sonst keinen Einstieg und kein Dock. Kein neuer Button: derselbe Einstieg bleibt sichtbar („keine neuen Dauer-Controls") |
| E23 | Grund am Meldungszustand | `SyncReportState` bleibt; daneben `syncReportReason: 'forbidden' \| 'setNotFound' \| 'unavailable' \| 'channelMismatch' \| 'shortfall' \| 'other' \| null`, gesetzt bei `failed`/`partial`, vom Dock als eigene Zeile gezeigt. Bei `channelMismatch` bietet das Dock **kein** „Erneut melden" an (Nachtrag N4) | Ein nacktes `failed` sagt nicht, ob die Rechte weg sind oder 7TV nicht antwortet; H3 verlangt einen klaren Grund. Wortlaut #255 |
| E24 | Antwort der Guid-Altform | `archivedCount`/`restoredCount` = Zahl der gemeldeten Guids, die als Zeile des Kanals **existieren** (unverändert gelassen), `notFoundIds` = Rest; kein `channel.synced` aus dem Endpunkt (nichts geändert), der Resync veröffentlicht es | **Festlegung** zu H4: das alte Frontend liest `archivedCount >= emoteIds.length` als `succeeded` und würde bei `0` fälschlich `partial` zeigen; die gefundene Zahl ist die ehrlichste, die die alte Antwortform tragen kann. Was der Audit-Eintrag dann behauptet, steht in 5.6 |

---

## 3. Fallen, im Code nachgeprüft am 2026-09-24

### F1 — Es gibt keine Purge-Datei ohne Set-ID, aber es gibt Purge-Dateien mit inzwischen nicht-aktivem Set

`meta.emoteSetId` ist seit `fe53329b` Pflicht; `parsePurgeRunProtocol` weist ein fehlendes Feld als
`wrongKind` ab (`purge-run-export.ts:196-198`). Ein Fallback „aktives Set des Kanals aus dem
Envelope" hätte keinen Eingabefall — und er wäre gefährlich: nach dem Set-Wechsel am 01.10. würde
er ein Protokoll des alten Sets in das neue schieben. **Vorgabe (Betreiber 2026-09-24):** kein
Fallback, keine dritte Klasse; der Hinweis ist die bestehende „nicht aktiv"-Zeile
(`restore-confirm-dialog.ts:51-55`).

### F2 — Der Envelope einer ungetrackten Übertragungsdatei trägt `''` als Kanal

`buildTransferPlanRecord`/`buildTransferRunProtocol` schreiben `channelName: targetChannelName ??
''` (`transfer-run-export.ts:219`, `:260`). Wer den Kanal aus dem Envelope liest, hält `''` für einen
Kanalnamen. **Vorgabe:** der Restore-Parser liest für das Ziel ausschließlich
`meta.targetEmoteSetId`; Kanal-Hinweis, erwarteter Kanal und Besitzer kommen aus der Zielprüfung,
nie aus der Datei.

### F3 — Die Zielprüfung teilt sich 10 Permits je Minute mit dem Rest der Seite

`ForeignEmoteLookup` ist mit 10/min je Nutzer die knappste Policy der Seite
(`RateLimitingOptions.cs:66`) und deckt Ziel-Picker, Set-Dropdown, Set-Vorschau der nicht-aktiven
Ansicht und den Quell-Picker. Seit E19 hängen **drei** Vorprüfungen daran (Restore, Delete,
Replace). **Vorgabe:** die Zielliste bekommt eine 60-s-Client-Kopie (wie
`loadCachedEmoteSetPreview`, `seven-tv-emote-set.service.ts:140-160`), sodass alle Vorprüfungen
einer Minute **ein** Permit kosten; die Slot-Vorschau der Bestätigung für ein nicht-(getrackt-aktives)
Ziel kostet ein zweites; `filterAlreadyPresentForRestore` und der Live-Read des Delete lesen 7TV
direkt und tokenlos (`seven-tv-set-entries.ts:6`) und kosten **keins**. Ein 429 auf der Zielprüfung
ist ein „gerade nicht prüfbar" mit Wiederholen-Hinweis, nie ein „nicht erlaubt".

### F4 — Token-Identität und Login-Identität sind zwei verschiedene Konten — im Betrieb aber dieselben

Der Lauf schreibt mit dem Token aus `sessionStorage` (`seven-tv-token.service.ts:6-18`); die
Besitzprüfung fragt nach dem **eingeloggten Twitch-Konto** (`SevenTvEndpoints.cs:227`). Ein Nutzer,
der mit dem Token eines fremden berechtigten Kontos schreibt, käme durch die Mutation und scheiterte
an der Meldung. **Betreiber 2026-09-24:** das Mod-Team von HandOfBlood arbeitet mit **eigenen**
7TV-Editor-Rechten; der Fall „fremdes Token" ist kein Ablauf, den es zu erhalten gilt. Damit gilt:
ein 403 bei der Meldung entsteht **nur bei echtem Rechteentzug** zwischen Vorprüfung und Meldung —
und seit E19 blockt die Vorprüfung jeden Lauf vor der ersten Mutation, wenn das Recht schon vorher
fehlte. Bleibt der Entzug mitten im Lauf: `failed` mit `syncReportReason: 'forbidden'` (E23), kein
Nachmeldeweg (E20), Zeilen heilt der periodische Resync.

### F5 — Beide Caches der Besitzprüfung sind älter als der Lauf

Die Set-Listen sind 60 s gecacht (E12 der Spec-200), die Editor-Grants 10 min (F10 dort). Ein Set,
das 7TV in den letzten 60 s gelöscht hat, oder ein Recht, das in den letzten 10 min entzogen wurde,
passiert Zielprüfung **und** Besitzprüfung noch; ein Set, das jünger als 60 s ist, fehlt in der
Liste und wird als „nicht bearbeitbar" gemeldet. Die Fehlermeldung der Zielprüfung sagt deshalb
„nicht bearbeitbar oder nicht (mehr) vorhanden", nie nur eines von beiden. Was das für die
Zeilen bedeutet, steht in F12.

### F6 — Der `ImportSourceDialog` schließt mit `picked`, und die Zielprüfung ist asynchron

`(picked)="dialogRef.close($event)"` (`import-source-dialog.ts:164`). Heute sind beide Parser
synchron. **Vorgabe:** `picked` feuert erst **nach** der Zielprüfung; während sie läuft, nimmt der
Schritt keinen zweiten Pick an (Dateiknopf gesperrt), ein Fehler bleibt im Banner des Schritts, der
Dialog bleibt offen. Eine spät eintreffende Antwort eines geschlossenen Dialogs ändert nichts mehr.

### F7 — `resetIfChannelChanged` kennt nur den Kanal der Seite

`channel-workspace-layout.ts:151-160` ruft `resetIfChannelChanged(channelName)` je Kanalwechsel;
es gibt dort kein Set. Deshalb vergleicht der Restore-Dienst mit `hostChannelName` (E13).

### F8 — Ein unvollständiger Treffer ist `partial`, kein Fehler — auch bei einem geteilten Set

Heute liest das Frontend `archivedCount >= sevenTvEmoteIds.length` als `succeeded`
(`seven-tv-delete.service.ts:305`). Mit mehreren betroffenen Kanälen gilt die Regel **je Kanal**;
ein `unresolvedChannel` ist immer `partial` (5.3, 6.4).

### F9 — Die E2E-Suite mockt je Test einzeln; vier bestehende Tests hängen an der kanalgebundenen Route oder am Kanalvergleich

`vote-ballot.e2e.spec.ts:389`, `:580`, `:650` routen `**/api/channels/{c}/emotes/sync-deleted`
und `:513-515` prüfen den Body; `emote-import.e2e.spec.ts:1835-1919` erwartet, dass ein
**fremdes** Purge-Protokoll mit `wrongChannel` abgewiesen wird. Alle ändern sich (9.4). Jeder
E2E-Test, der eine Rückweg-Datei einliest **oder einen Delete/Replace startet**, braucht ab jetzt
`mockEmoteSetTargets` (`e2e/support/mocks.ts:855-890`) mit `editable: true` — ohne Mock fällt die
Vorprüfung durch den Dev-Proxy und der Test sieht nur das Fehlerbanner.

### F10 — `EmoteService` wird in Tests direkt konstruiert

`new EmoteService(db, NullLogger<EmoteService>.Instance)` steht in jedem Fall von
`EmoteServiceTests.cs` (z. B. `:393`). E8 fügt eine dritte Abhängigkeit hinzu; jede Konstruktion im
Test wandert mit, am besten über einen Fixture-Helfer. `ExcludedChannelFilterTests.cs:77` zeigt,
wie ein echter Filter aus Konfiguration gebaut wird.

### F11 — Zwei Doku-Stellen behaupten die kanalgebundene Meldung als Normalfall

`docs/Architectur.md:68`, `:133`, `:154`, `:202` und der Kommentar an
`UsageStatsAccessAuthorizationFilter.cs:6-17` beschreiben `sync-deleted` als den Meldeweg. Nach
dieser Spec ist die kanalgebundene Route nur noch die Altform bis zum E3-Tor, und sie ändert keine
Zeile mehr. Beide Stellen werden im selben Vorgang angepasst (12.2); UI-Designsprache §7.3 („brings
emotes **into the channel of the page**") ebenso.

### F12 — Die Meldung ändert Zeilen auf Zuruf, und die Rechte dahinter sind bis zu 10 min alt (H1)

Ein Client, der die Meldung sendet, behauptet eine 7TV-Mutation; der Service prüft die Behauptung
nicht gegen 7TV, sondern nur, ob der Absender das Set laut gecachten Rechten bearbeitet. Ein
Editor, dem das Recht eben entzogen wurde, kann bis zu 10 min lang (Grants-Cache) Zeilen eines
getrackten Kanals archivieren oder reaktivieren, ohne dass 7TV etwas davon weiß. **Vorgabe (E17):**
jede Meldung, die einen Kanal trifft, stößt dessen Resync an; der Resync liest 7TV und stellt den
wahren Stand her — binnen des 60-s-Cooldowns, spätestens mit dem nächsten Worker-Tick. Das
Restrisiko ist also ein **Minutenfenster** mit Reichweite „Zeilen eines Kanals", und die Altform
war schwächer: sie prüfte nur die Kanalrolle und wusste nicht einmal, welches Set gemeint war. Die
Alternative — die Meldung erst nach einem eigenen 7TV-Read wirken zu lassen — hätte jede Meldung
mit einem unbudgetierten Request belastet und den Sinn der `Bookkeeping`-Policy (die Meldung darf
nie an einem Budget scheitern) aufgehoben. Steht so im DECISIONS-Eintrag 2.

### F13 — `ActiveEmoteSetId` kann 7TV hinterherhinken, und ein geteiltes Set kann einen Kanal verstecken (H2)

`Channel.ActiveEmoteSetId` ist beobachteter Stand (E21), im schlechtesten Fall eine Minute alt.
Nach einem Set-Wechsel im 7TV-Web trifft die Meldung den Kanal nicht („nur Papier"), obwohl die
Mutation dessen jetzt aktives Set betraf. **Vorgabe (E18):** der Client nennt den Kanal, den er
treffen will; ein Fehlschlag ist `unresolvedChannel` mit Grund, `partial` im Dock, benannt im
Papier-Eintrag, und der Resync des Kanals wird angestoßen. **Rest:** bei einem geteilten Set kennt
der Client nur den Kanal seines Ziel-Accounts; ein **zweiter** Kanal mit veraltetem
`ActiveEmoteSetId` bleibt unentdeckt, bis sein periodischer Resync ihn binnen einer Minute
nachzieht. Das ist derselbe Rest, den der Delete-Lauf heute für jeden zweiten Kanal hat, und er
wird als solcher im DECISIONS-Eintrag genannt, nicht geschlossen.

### F14 — Ein alter Guid-Tab kennt kein Set (H4)

Das Produktions-Frontend meldet `{ emoteIds }` ohne Set. Nach einem Set-Wechsel archivierte die
Altform eine Zeile, deren Emote im **neuen** aktiven Set liegt — die Zeile stünde bis zum nächsten
Resync als archiviert da. **Vorgabe (E4):** die Altform ändert keine Zeile mehr; Audit + Resync.
Ihr Audit-Eintrag trägt `legacyBodyForm: true`, damit die Admin-Ansicht ihn von einer geprüften
Meldung unterscheiden kann.

### F15 — Cooldown und Resync-Zahl je Lauf

Ein Import-Lauf mit Replace meldet zweimal (`sync-imported`, `sync-deleted`); ein Delete-Lauf
einmal; ein Restore einmal. Backend-Resync gibt es nur aus `sync-deleted`/`sync-restored`
(set-zentrisch und Altform), **nicht** aus `sync-imported` — dort bleibt es beim heutigen
Frontend-Resync für ein getracktes aktives Ziel (`seven-tv-import.service.ts:619-646`). Je Meldung
und Kanal höchstens ein `TryBeginAsync`; ein nicht erworbener Cooldown (Resync lief binnen 60 s, z. B.
der `sync-imported`-Resync desselben Laufs) heißt „kommt ohnehin" — der Kanal steht dann **nicht** in
`resyncTriggered`, und der Client stößt **trotzdem keinen** eigenen an (der Cooldown antwortete
ihm ohnehin mit 429). Damit gilt: **je Kanal und 60 s höchstens ein Resync**, unabhängig davon, wie
viele Meldungen oder Läufe ihn berühren. Der Admin-Resync (`AdminEndpoints.cs:336-354`) bleibt
außerhalb dieser Rechnung, wie heute.

### F16 — Die eine Asymmetrie zwischen `editable` und der Besitzprüfung

Die Backend-Prüfung fragt 7TV **einmal** nach dem Besitzer eines Sets, das in keiner Liste steht
oder ohne Besitzer gelistet ist (`ImportTargetOwnershipService.cs:83-103`). Die Zielliste kann diese
Frage nicht stellen, ohne selbst einen Request zu kosten. **Vorgabe:** `editable` ist in diesem Fall
`false` — das Frontend ist dort **strenger** als das Backend, nie lockerer: ein Lauf unterbleibt,
wo die Meldung vielleicht gelungen wäre; nie umgekehrt. Praktisch betrifft das nur ein Set, das
7TV ohne `owner.id` liefert. Steht im DECISIONS-Eintrag 3.

### F17 — Der Delete hat heute keine Vorprüfung, und seine Bestätigung liest schon einmal live

`MassDeletePanel` öffnet die Bestätigung, liest danach die Live-Aliase (`loadSevenTvSetEntries`,
`mass-delete-panel.ts:856-866`) und startet über `startDelete` mit `abortReasonBeforeStart`
(`:918-934`). **Vorgabe (E19):** die Vorprüfung sitzt **vor** `openDeleteConfirmDialog` — ein Nutzer
ohne Bearbeitungsrecht sieht die Bestätigung gar nicht erst, sondern den Grund als Abbruchnotiz
(`abortNotice`, derselbe Mechanismus wie „Nichts wurde gelöscht" + Grund). Beim Replace sitzt sie in
`import-flow.ts`' `start` **vor** `recheckTransferPlan` (`:300-345`), nur wenn der Plan eine
Replace-Zeile hat — die Picker-Wahl trug `editable` schon, die drei Seiten-Türen nicht.

---

## 4. Vertrag: Verhalten

### 4.1 Einstieg und Ziel

1. Der Einstieg bleibt **„Importieren → Datei"** auf jeder Kanalseite (`ImportTrigger`,
   `ImportSourceDialog`, `FileImportStep`). Kein neuer Knopf, keine neue Seite. Der Einstieg ist
   **auch ohne gewähltes Set** sichtbar (E22): der Trigger steht nur noch hinter `!isCoarse()`;
   ohne Set sind im Quellschritt die Türen „Kanal", „Bestenliste" und die Kopier-Dateien mit Grund
   deaktiviert („kein Set, in das kopiert werden könnte" — Wortlaut #255), die Rückweg-Dateien
   werden gelesen. Der Kopier-Knopf („Übertragen") bleibt hinter dem Set-Gate.
2. Erkennt der Schritt eine Rückweg-Datei (`kind === 'purge-run'` oder `kind === 'transfer-run'`,
   beide Stufen), bestimmt **die Datei das Ziel-Set**:
   - Übertragungsdatei ⇒ `meta.targetEmoteSetId`;
   - Purge-Protokoll ⇒ `meta.emoteSetId`.
   Der Kanal der Seite und das gewählte Set der Seite sind für die Gültigkeit der Datei **ohne
   Bedeutung**. Es gibt keine dritte Klasse (F1).
3. Was die Datei an Zeilen liefert, bleibt wie in #230 festgelegt: Purge ⇒ `status: 'done'`-Zeilen;
   `planned` ⇒ jeder `removedTarget`; `finished` ⇒ nur `removedTarget.confirmed === true`; nur
   Ziel-Einträge, nie Quell-ADDs, Renames oder Adopts (DECISIONS 2026-09-23). `readProtocolRow`
   und das Lesen bestehender `purge-run`-Dateien bleiben byte-identisch (R4 des Plans #230).

### 4.2 Zielprüfung

4. Vor jedem `picked` ruft der Schritt `resolveEditableSet(emoteSetId)` (E19) — im Normalfall ein
   Treffer der 60-s-Client-Kopie, sonst **ein** Request an `GET /api/seventv/me/emote-set-targets`.
   Vier Ausgänge:

| Befund | Ausgang |
|---|---|
| Set unter einem Account gefunden, `kind === 'NORMAL'`, `editable === true` | **geprüft** — der Schritt emittiert `picked` mit dem aufgelösten Ziel (6.1) |
| Set gefunden, `kind !== 'NORMAL'` | Fehler `targetNotSelectable` (E11), kein Lauf |
| Set in keiner Liste, oder gefunden mit `editable === false`, und weder `sevenTvUnavailable` noch ein `setsUnavailable`-Account | Fehler `targetNotEditable` — „nicht bearbeitbar oder nicht (mehr) vorhanden" (F5), kein Lauf |
| Set nicht bearbeitbar gefunden **und** `sevenTvUnavailable === true` oder ein `setsUnavailable`-Account, **oder** der Request scheitert (429, 503, Netz) | Fehler `targetCheckUnavailable` — „gerade nicht prüfbar, später erneut", kein Lauf |

5. Aus dem Treffer nimmt der Schritt mit: `emoteSetId`, `setName`, `ownerDisplayName` (nur
   Anzeige, nie verglichen), `twitchLogin` des Accounts, `trackedChannelName` (`null` =
   ungetrackt), `isActive` (für getrackte Accounts aus `Channel.ActiveEmoteSetId`, E21). Das ist das
   **aufgelöste Ziel** (`ResolvedRestoreTarget`, 6.1); es wird nicht erneut abgefragt (F3).

### 4.3 Bestätigung

6. Der Bestätigungsdialog nennt **immer** Set-Name, die aufgelöste **Set-ID** (E21) und den
   Besitzer (`ownerDisplayName`), bei einem getrackten Ziel zusätzlich den Kanal
   (`trackedChannelName`). Die bestehende Zeile „dieses Set ist gerade nicht aktiv" gilt für ein
   getracktes Ziel mit `isActive === false`; für ein ungetracktes Ziel wird sie **nicht** gezeigt.
7. Ist das Ziel **nicht** das gewählte Set der Seite — `target.emoteSetId !== hostSelectedSetId`,
   was auch ein anderes Set **desselben** Kanals und jede Seite ohne gewähltes Set einschließt
   (E21) — zeigt der Dialog einen eigenen Hinweis, dass diese Ansicht von dem Lauf nichts zeigen
   wird. Wortlaut #255; die Struktur (welche Zeile wann) ist hier Vertrag.
8. Reihenfolge der Kette bleibt: **Token vor Bestätigung** (`startRestoreFlow`, Doku `:44-48`),
   nicht angleichen. Die Slot-Vorschau liest für ein getracktes **aktives** Ziel
   `getSetStatus(trackedChannelName)`, sonst `loadEmoteSetPreview(trackedChannelName ??
   twitchLogin, emoteSetId)` — dieselbe Gabel wie `loadImportTarget`; ein Fehler dort blendet nur
   die Projektionszeile aus, wie heute.
9. `filterAlreadyPresentForRestore` (Regeln 1–4 inkl. „Name belegt") läuft **unverändert** gegen
   das Ziel-Set — sie liest 7TV nach Set-ID und braucht keinen Kanal.

### 4.4 Lauf, Meldung, Seite

10. Der Lauf sendet die ADDs an `emoteSetId` wie heute (Engine unverändert). Nach dem Lauf meldet
    der Restore-Dienst an `POST /api/seventv/emote-sets/{emoteSetId}/sync-restored` (5.1) mit den
    IDs und dem **erwarteten Kanal** (E18: `trackedChannelName`, wenn `isActive`, sonst `null`) —
    für jedes Ziel, getrackt wie ungetrackt, aktiv wie nicht-aktiv. Es gibt **keinen** zweiten
    Meldeweg mehr.
11. Resync: das Backend stößt ihn für jeden getroffenen und für den unaufgelösten erwarteten Kanal
    an (E17/E18) und nennt die Kanäle in `resyncTriggered`. Der Client stößt selbst nur noch für ein
    **nicht-aktives** Set eines getrackten Kanals einen an (E12), und nur, wenn dieser Kanal nicht
    in `resyncTriggered` steht. Für ein ungetracktes Ziel keiner; das Dock zeigt dann auch keine
    Resync-Zeile. **Scheitert die Meldung endgültig** (`failed` nach den Retries), gibt es kein
    `resyncTriggered`, und der Client stößt ersatzweise den Resync des nicht-aktiven bzw. des
    erwarteten Kanals an (Nachtrag N1).
12. Die Seite, auf der der Lauf gestartet wurde, ändert sich **nur**, wenn sie ein `channel.synced`
    für ihren eigenen Kanal bekommt (5.4, Resync). Ist das Ziel ein anderes Set, bleibt das Raster
    stehen; das Dock zeigt den Lauf mit einer Zielzeile (Set-Name, Kanal oder Besitzer), analog
    zur Zielzeile des Import-Docks (`import-progress-section.ts:80-95`).
13. Das **Dock** des Restore-Laufs (`RestoreProgressSection`, E22) bleibt an die Seite gebunden,
    auf der er gestartet wurde (`hostChannelName`): ein Kanalwechsel setzt einen **fertigen** Lauf
    zurück wie heute, nie einen laufenden (`resetIfChannelChanged` kehrt bei `isRunning()` früh
    zurück, `:216-221`). Seine Meldungen gehen unabhängig davon raus, weil sie am Laufdatensatz
    hängen. Das Dock steht außerhalb des Set-Gates, also auch auf einer Seite ohne gewähltes Set.
14. Der Meldungszustand im Dock trägt bei `failed`/`partial` einen Grund (E23): `forbidden`
    (403), `setNotFound` (404), `unavailable` (503/429/Netz nach den Retries), `channelMismatch`
    (`unresolvedChannel`), `shortfall` (ein Kanal mit `notFoundIds`), `other`. Wortlaut #255. Bei
    `channelMismatch` zeigt das Dock **keinen** „Erneut melden"-Knopf (Nachtrag N4).

### 4.5 Die Sperre fällt, die Vorprüfung kommt

15. Regel 7 `replaceNeedsTrackedTarget` wird aus `validateResolution` **entfernt**; `ViolationRule`
    verliert den Wert, `ResolutionContext` das Feld `targetIsTracked` (danach leer — die
    Schnittstelle bleibt als leeres Objekt oder fällt, das entscheidet der Plan; kein Aufrufer
    darf einen Kontext ohne Bedeutung weiterreichen). Der Dialog zeigt „Ziel ersetzen" für ein
    ungetracktes Ziel wie für ein getracktes; der Deaktivierungsgrund `replaceNeedsTracked`
    (`de.json:1234`) und die Verletzungszeile (`:1254`, `en.json:1254`) entfallen.
16. Der Wächter in `startImport` (`seven-tv-import.service.ts:376-379`) entfällt. Ein Replace-Lauf
    meldet seine bestätigten REMOVEs (`completedSteps >= 1`, unverändert) an
    `…/sync-deleted` des Ziel-Sets mit erwartetem Kanal (`targetChannelName`, wenn
    `targetIsActiveSet`, sonst `null`); `reportRemoved` hat keinen Kanal-Zweig mehr.
17. **Vorprüfung vor dem Replace (E19):** hat der Plan mindestens eine Replace-Zeile, ruft
    `import-flow.ts`' `start` vor `recheckTransferPlan` `resolveEditableSet(targetSetId)`; scheitert
    sie, startet nichts, und die Seite zeigt den Grund an derselben Stelle, an der ein
    Drift-Abbruch steht (transiente Notiz des Import-Dienstes, `showDuplicateNotice`-Mechanik).
    Für einen Plan ohne Replace läuft keine Vorprüfung — eine ADD in ein fremdes Set scheitert bei
    7TV selbst, und ihre Meldung ist heute schon set-zentrisch geprüft.
18. **Die Pflicht-Rückweg-Datei aus #230 bleibt** — für ein ungetracktes Ziel genauso: Live-Read,
    `verifyReplaceTargets`, Download, erst dann „Starten" (DECISIONS 2026-09-23, „The safeguard is
    a file"). Der Dateiname nutzt bei ungetracktem Ziel weiterhin die Set-ID
    (`transferPlanFilename(targetChannelName ?? setId, …)`, `import-confirm-dialog.ts:1259-1261`).
19. `beforeunload` und der `canDeactivate`-Guard decken den laufenden Replace-Lauf unverändert; die
    Frage 6 des Plans #230 (Envelope-`channelName` `''` bei ungetracktem Ziel) bleibt so, weil der
    Restore-Parser das Feld nicht liest (F2).

### 4.6 Delete (K5)

20. **Vorprüfung vor dem Delete (E19, F17):** `MassDeletePanel` ruft vor `openDeleteConfirmDialog`
    `resolveEditableSet(setId)`; scheitert sie, öffnet keine Bestätigung, die Abbruchnotiz nennt den
    Grund (`targetNotEditable` / `targetCheckUnavailable`), nichts wird gelöscht. Im Normalfall ist
    das ein Cache-Treffer (Seite oder Picker haben die Liste binnen 60 s geladen) oder ein Permit.
21. Die Delete-Meldung geht an `…/sync-deleted` des Laufs-Sets mit erwartetem Kanal
    (`channelName` der Seite, wenn `setId === activeSetId`, sonst `null`). Das Purge-Protokoll,
    sein Envelope-`channelName` und der Dateiname bleiben, wie sie sind.
22. Restore aus dem fertigen Delete-Lauf (E16): Ziel aus dem Laufdatensatz, Vorprüfung wie jede
    andere (Cache-Treffer), Bestätigung nach 4.3.

### 4.7 Audit

23. Löschung und Wiederherstellung erscheinen im Audit-Log auch für ungetrackte Sets — als
    Set-Eintrag ohne Kanal (E6). Für getrackte Kanäle, deren aktives Set das Ziel ist, erscheint je
    Kanal ein Eintrag **mit** Kanal, wie heute der aktive Zweig (5.5). Ein Papier-Eintrag für ein
    **nicht-aktives** Set trägt den getrackten Kanal des Besitzer-Accounts, sofern es einen gibt,
    und steht damit in dessen Kanal-Audit-Ansicht (Nachtrag N3); nur ein ungetracktes Ziel bleibt
    ohne Kanal. Ein unaufgelöster erwarteter Kanal steht mit den IDs im Papier-Eintrag (5.5). Die
    Audit-Ansicht braucht **keine** neue Projektion für das Rendern: `emoteSetId`,
    `targetIsActiveSetOfChannel` und `targetOwnerTwitchLogin` werden generisch gelesen
    (`AuditLogQueryService.cs:153-162`, `:237-263`); die neuen Detailfelder liegen im JSON und
    werden erst mit #255 angezeigt.

### 4.8 Nicht in #253

24. Das Rückgängigmachen eines geglückten Replace (#254). Regel 4 des Restore-Filters gilt
    unverändert.
25. Wortlaute und Zählungen in Dock, Dialogen und Audit-Log (#255). Neue Schlüssel bekommen hier
    einen vorläufigen Text, #255 revidiert ihn.
26. #256, Punkte 1, 2, 4, 5. **Punkt 3** („Kanalnamen beim Wiederherstellen exakt verglichen")
    **erledigt sich durch E1**: es gibt keinen Kanalvergleich mehr. Das Issue wird um diesen Punkt
    gekürzt, nicht geschlossen.
27. Das Laden einer Datei als **Auswahl** (#201).
28. **Ein vorautorisierter Nachmeldeweg** (E20) — bewusst ausgelassen.

---

## 5. Vertrag: Backend

### 5.1 Routen

Beide in der bestehenden Gruppe `emoteSetGroup` (`SevenTvEndpoints.cs:198-201`), neben
`/sync-imported`:

```
POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted
POST /api/seventv/emote-sets/{emoteSetId}/sync-restored
Body: { "sevenTvEmoteIds": string[], "expectedChannelName": string | null }
```

Ein gemeinsamer Body-Record für beide Routen (Name Plansache, Vorschlag `SyncInSetRequest`), ohne
`emoteSetId` im Body — die Route trägt es, wie bei `SyncImportedToSetRequest` begründet
(`SevenTvEndpoints.cs:429-436`). `expectedChannelName` ist der Kanal, den der Client zu treffen
erwartet (E18), oder `null`, wenn er keinen erwartet.

**Leiter, in dieser Reihenfolge (jede Stufe nur, wenn die vorige passiert ist):**

| Stufe | Wirkung |
|---|---|
| 1 Middleware | nicht eingeloggt → 401; `Bookkeeping`-Budget (120/min je Nutzer, `RateLimitingOptions.cs:41`) → 429 |
| 2 `EmoteSetIdValidationFilter` (Gruppe) | ungültiger Routenwert → 400 `invalid_emote_set_id` |
| 3 Body | `sevenTvEmoteIds` fehlt oder leer → 400 `emote_ids_empty`; `expectedChannelName` gesetzt, aber nicht `ChannelNameValidation.IsValid` → 400 `invalid_channel_name`; kein Actor-Principal → 401 (wie `sync-imported`, `:220-224`) |
| 4 Besitzprüfung | `IImportTargetOwnershipService.CheckAsync(actor.TwitchUserId, actor.Login, emoteSetId)`: `SetNotFound` → 404 `emote_set_not_found`; `Forbidden` → 403 **bare** (`Results.Forbid()`); `Unavailable` → 503 `foreign_channel_seventv_unavailable`. **Kein Audit-Eintrag** auf diesen drei Ausgängen, kein Service-Aufruf, kein Resync. Bei `Owner` trägt das Ergebnis neben `OwnerSevenTvUserId`/`OwnerTwitchLogin` auch `OwnerTwitchUserId` (Nachtrag N3) |
| 5 Service | `MarkDeletedInSetAsync` bzw. `MarkRestoredInSetAsync` (5.2), mit den drei Besitzer-Werten aus Stufe 4 |
| 6 Live-Event | je Kanal der Antwort mit `NewlyChangedCount > 0` ein `channel.synced` (5.4) |
| 7 Resync | je getroffenem Kanal und für einen `unresolvedChannel` mit `reason: 'activeSetDiffers'`: `IChannelResyncCooldown.TryBeginAsync`, bei Erfolg `TriggerResyncAsync(channel, actor)`; nicht erworben ⇒ kein Resync, kein Fehler; `TriggerResyncAsync` ≠ `Triggered` ⇒ Cooldown freigeben (wie `ChannelEndpoints.cs:238-252`). Die Kanäle mit `Triggered` bilden `resyncTriggered` |
| 8 Antwort | 200 (5.3) |

Policy `Bookkeeping`, nicht `ForeignEmoteLookup` — dieselbe Begründung wie am set-zentrischen
`sync-imported` (`SevenTvEndpoints.cs:188-195`): die Mutation ist passiert, ein verbrauchtes
Lesebudget darf die Papierspur nicht kosten; das passt nur, weil die Besitzprüfung keinen
ungebudgetierten 7TV-Request kostet. Der Resync in Stufe 7 kostet einen — aber genau einen je Kanal
und 60 s, gedeckelt vom Cooldown, und er ist der Preis dafür, dass die Meldung sofort wirken darf
(F12). Er läuft unter dem Konto des Meldenden, das die Besitzprüfung eben passiert hat — strenger
als das, was `POST /resync` selbst verlangt (`UsageStatsAccessAuthorizationFilter`), also keine
neue Autorität. `IImportTargetOwnershipService` wird **nicht umbenannt**.

### 5.2 Service

```
Task<SyncDeletedInSetResultDto>  MarkDeletedInSetAsync (string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, string ownerTwitchUserId, IReadOnlyList<string> sevenTvEmoteIds, string? expectedChannelName, AuditActor actor, CancellationToken ct)
Task<SyncRestoredInSetResultDto> MarkRestoredInSetAsync(string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, string ownerTwitchUserId, IReadOnlyList<string> sevenTvEmoteIds, string? expectedChannelName, AuditActor actor, CancellationToken ct)
```

(`ownerTwitchUserId` seit Nachtrag N3 — die Twitch-ID des Accounts, dem das Set gehört; die
Besitzprüfung kennt sie ohne weiteren 7TV-Request, s. N3.)

Verhalten, für beide Richtungen spiegelbildlich:

1. `sevenTvEmoteIds` ordinal deduplizieren (wie `MarkImportedAsync`, `EmoteService.cs:259`);
   `reportedCount` = Anzahl danach. `expectedChannelName` normalisieren (`ChannelName.Normalize`).
2. Betroffene Kanäle bestimmen (E8): `IsBotActive && ActiveEmoteSetId == emoteSetId`, danach
   `IExcludedChannelFilter` in-memory. Reihenfolge: `ChannelName` ordinal.
3. **Erwarteten Kanal auflösen (E18):** ist `expectedChannelName` gesetzt und **nicht** unter den
   Treffern, wird die Kanalzeile geladen: fehlt sie, ist sie `IsBotActive == false` oder
   ausgeschlossen ⇒ `unresolvedChannel = { channelName, reason: 'notTracked' }` (die Sperre wird
   **nicht** verraten — derselbe Grund wie `notTracked`, wie `channel_excluded` nur beim Join
   existiert); ist sie aktiv mit anderem `ActiveEmoteSetId` ⇒ `reason: 'activeSetDiffers'`. In
   keinem Fall wird eine Zeile dieses Kanals berührt oder gezählt.
3a. **Besitzerkanal auflösen (Nachtrag N3):** die Kanalzeile mit `TwitchChannelId ==
   ownerTwitchUserId && IsBotActive`, die nicht auf der Sperrliste steht — dieselbe Regel wie
   `IChannelService.GetActiveByTwitchChannelIdAsync` und damit dieselbe, nach der die Zielliste
   `trackedChannelName` bestimmt. Fehlt sie, ist sie inaktiv oder gesperrt ⇒ kein Besitzerkanal
   (`null`). Sie dient **nur** dem `ChannelName` des Papier-Eintrags (5.5); keine Zeile dieses
   Kanals wird deshalb berührt oder gezählt, und ein gesperrter Kanal wird so wenig verraten wie in
   Schritt 3.
4. Je betroffenem Kanal: `Emotes` mit `ChannelId == channel.Id && SevenTvEmoteId ∈ ids` laden;
   archivieren (`IsArchived = true`, `ArchivedAt = now`, `LastSyncedAt = now`, **nur** für noch
   nicht archivierte — Datum eines bereits archivierten bleibt) bzw. zurückholen (`IsArchived =
   false`, `ArchivedAt = null`, `LastSyncedAt = now`, nur für archivierte). Zielzustands-Zähler
   (`archivedCount`/`restoredCount`) = gefundene Zeilen, `NewlyChangedCount` = tatsächlich
   geänderte, `notFoundIds` = IDs ohne Zeile in **diesem** Kanal. Das ist die heutige Semantik
   des aktiven Zweigs (`EmoteService.cs:146-178`), nur je Kanal.
5. Audit (5.5).
6. **Ein** `SaveChangesAsync` für alles — Zeilen und Audit in einer Transaktion, wie heute.

Kein Treffer in Schritt 2 und kein erwarteter Kanal ⇒ keine Zeile berührt, nur der Papier-Eintrag;
`channels` leer, `unresolvedChannel` null.

### 5.3 Antwort (Wire)

```
SyncDeletedInSetResponse {
  reportedCount: int                        // deduplizierte 7TV-IDs des Requests
  channels: [                               // getroffene getrackte Kanäle, ordinal nach Name; [] = keiner
    { channelName: string, archivedCount: int, notFoundIds: string[] }
  ]
  unresolvedChannel: { channelName: string, reason: "notTracked" | "activeSetDiffers" } | null
  resyncTriggered: string[]                 // Kanäle, für die Stufe 7 einen Resync ausgelöst hat
}
SyncRestoredInSetResponse {
  reportedCount: int
  channels: [ { channelName: string, restoredCount: int, notFoundIds: string[] } ]
  unresolvedChannel: { channelName: string, reason: "notTracked" | "activeSetDiffers" } | null
  resyncTriggered: string[]
}
```

`NewlyChangedCount` steht **nicht** auf dem Draht (es steuert nur das Live-Event). Core-DTOs:
`SyncDeletedInSetResultDto(ReportedCount, Channels, UnresolvedChannel)` und
`SyncRestoredInSetResultDto` mit je einer `…ChannelResultDto(ChannelName, Count, NewlyChangedCount,
NotFoundIds)` und `UnresolvedChannelDto(ChannelName, Reason)`; `resyncTriggered` baut der Endpunkt
(Stufe 7), nicht der Service. Die Namen sind Plansache, die Felder Vertrag.

### 5.4 Live-Event

Je Kanal in `channels` mit `NewlyChangedCount > 0` ein `LiveEvent(LiveEvents.ChannelSynced,
channelName)` über `IRedisPublisher` — dieselbe Hilfsmethode und dieselbe Fehlerbehandlung wie
`PublishChannelSyncedAsync` (`EmoteEndpoints.cs:409-`): im Endpunkt, nicht im Service; Fehler
geloggt und geschluckt; kein Request-Token. Der Resync desselben Kanals (Stufe 7) veröffentlicht
später ein zweites, aus dem Worker — das ist heute schon so (Meldung + Frontend-Resync) und bleibt
unter dem Reload-Debounce der Seite.

### 5.5 Audit

| Fall | Einträge |
|---|---|
| Getroffener Kanal mit Zielzustands-Zähler > 0 | je Kanal **ein** Eintrag: `Action = emotes.syncDeleted`/`syncRestored`, `ChannelName = <Kanal>`, `TargetType = "emoteSet"`, `TargetId = emoteSetId`, Details `{ emoteCount: <Zähler dieses Kanals>, emoteSetId, targetIsActiveSetOfChannel: true }` — byte-gleich mit dem heutigen aktiven Zweig (`EmoteService.cs:162-171`), damit `audit-row` ihn wie bisher rendert |
| Kein Kanal-Eintrag geschrieben (kein Treffer, oder jeder Treffer mit Zähler 0), **oder** ein `unresolvedChannel` — **Besitzerkanal aufgelöst** (5.2 Schritt 3a; Nachtrag N3) | **ein** Papier-Eintrag: `ChannelName = <Besitzerkanal>`, `TargetType = "emoteSet"`, `TargetId = emoteSetId`, Details `{ emoteCount: reportedCount, emoteSetId, targetIsActiveSetOfChannel: false, unresolvedChannelName?, unresolvedReason?, unresolvedSevenTvEmoteIds? }` — **ohne** die beiden `targetOwner*`-Felder (der Kanal nennt den Besitzer), mit `targetIsActiveSetOfChannel: false`, damit die Ansicht „(nicht das aktive Set)" wählt wie für die Einträge vor #253 (`audit-row.ts:141-142`). Der Eintrag steht in der Kanal-Audit-Ansicht dieses Kanals, weil `AuditLogQueryService.ApplyFilter` exakt auf `ChannelName` filtert (`:109-115`) |
| Kein Kanal-Eintrag geschrieben, **oder** ein `unresolvedChannel` — **kein Besitzerkanal** (ungetrackt, verlassen oder gesperrt) | **ein** Papier-Eintrag: `ChannelName = null`, `TargetType = "emoteSet"`, `TargetId = emoteSetId`, Details `{ emoteCount: reportedCount, emoteSetId, targetOwnerSevenTvUserId, targetOwnerTwitchLogin, unresolvedChannelName?, unresolvedReason?, unresolvedSevenTvEmoteIds? }` — die drei `unresolved*`-Felder nur bei einem Mismatch (die IDs sind die gemeldeten, weil in diesem Kanal keine getroffen wurde); ohne `targetIsActiveSetOfChannel`, damit die Ansicht „für <ownerLogin>" wählt (`audit-row.ts:136-141`) |
| Resync ausgelöst (Stufe 7) | je Kanal der `channel.resync`-Eintrag, den `TriggerResyncAsync` selbst schreibt (`ChannelService.cs:269`) — unverändert, unter dem Konto des Meldenden |

Es gibt **immer mindestens einen** Eintrag je erfolgreichem Aufruf. Das ist die Antwort auf #224:
kein Erfolg ohne Papierspur, und kein verfehlter Kanal ohne Spur. Ein wiederholter Bericht (Retry)
darf einen zweiten Eintrag schreiben — „ein Duplikat schlägt eine Lücke" (`EmoteService.cs:44-48`).

**Invariante der Detailfelder (Nachtrag N3):** ein Eintrag trägt entweder einen Kanal **und**
`targetIsActiveSetOfChannel` (Kanal-Eintrag `true`, Papier-Eintrag mit Besitzerkanal `false`), oder
keinen Kanal **und** die beiden `targetOwner*`-Felder — nie beides. Das ist genau die Zweiteilung,
die `AuditLogTargetEmoteSet` dokumentiert und nach der `audit-row.ts` rendert; Projektion und
Ansicht bleiben unverändert.

### 5.6 Die Guid-Altform — nur noch Audit + Resync (E4, H4) — und das Ergebnis der Aufruferprüfung

**Aufruferprüfung (am Branch, 2026-09-24).** Code-Aufrufer der kanalgebundenen Routen sind heute
genau drei, alle über `EmoteAdminService.syncDeleted/syncRestored`
(`emote-admin.service.ts:76-91`): `SevenTvDeleteService.reportDeleted`
(`seven-tv-delete.service.ts:284`), `SevenTvRestoreService.reportRestored`
(`seven-tv-restore.service.ts:273`), `SevenTvImportService.reportRemoved`
(`seven-tv-import.service.ts:696`). Die Vote-Session-Detailseite läuft über das eingebettete
`MassDeletePanel` und damit über den Delete-Dienst, kein vierter Weg. Nach dem Wechsel (6.5) gibt es
**null** Code-Aufrufer; `EmoteAdminService.syncDeleted/syncRestored` und `SyncBookkeepingBody`
entfallen im Frontend. Übrig bleibt **ein** Aufrufer außerhalb des Codes: Browser-Tabs mit dem
Produktions-Frontend, die vor K7 geöffnet wurden und nach dem Deploy `{ emoteIds }` senden.

**Entscheidung (E4):** die kanalgebundene Route mit Guid-Body bleibt bis zum E3-Tor der Spec-200
§21 — Filter (`UsageStatsAccessAuthorizationFilter`), Policy (`Bookkeeping`,
`EmoteRoutePolicyTests.cs:33`), Log-Zeile „legacy body form" —, aber die Guid-Überladung
`MarkDeletedAsync(channelName, emoteIds, actor)` / `MarkRestoredAsync(…)` **ändert keine Zeile
mehr**:

1. Kanalzeile laden; fehlt sie ⇒ Antwort `{ archivedCount: 0, notFoundIds: <alle> }` wie heute
   (`EmoteService.cs:19-23`), kein Eintrag.
2. Guid-Treffer des Kanals zählen (`ChannelId == channel.Id && Id ∈ emoteIds`), **ohne** Zuweisung
   an `IsArchived`/`ArchivedAt`/`LastSyncedAt`.
3. Audit-Eintrag wie heute (bei Zähler > 0): `{ emoteCount: <Treffer>, legacyBodyForm: true }`
   — das Feld ist neu und sagt, dass diese Meldung ohne Set-Bezug und ohne Zeilenänderung kam. Der
   Eintrag behauptet damit nichts Irreführendes: er ist die Aufzeichnung eines Berichts, wie jeder
   `syncDeleted`-Eintrag, und die Ansicht rendert bis #255 nur den Zähler; eine Kennzeichnung
   „(alte Form)" gehört #255.
4. Antwort nach E24. Das Live-Event des Endpunkts entfällt für diese Form (nichts geändert); der
   Endpunkt stößt stattdessen den Resync des Kanals unter dem Cooldown an (Stufe 7 wie in 5.1) —
   der Worker liest 7TV und stellt jede Zeile auf den wahren Stand.

**Was entfällt:** die set-scoped Überladungen `MarkDeletedAsync(channelName, emoteSetId, …)` und
`MarkRestoredAsync(channelName, emoteSetId, …)` (`IEmoteService.cs:50,53`,
`EmoteService.cs:115-239`); der Body-Zweig `{ emoteSetId, sevenTvEmoteIds }` der kanalgebundenen
Routen samt Stufen 2–4 der Leiter `ValidateSyncBookkeepingBody` (`EmoteEndpoints.cs:269-315`) — die
Records `SyncDeletedRequest`/`SyncRestoredRequest` kehren auf `(IReadOnlyList<string> EmoteIds)`
zurück; das Antwortfeld `targetIsActiveSetOfChannel` und der DTO-Parameter
`TargetIsActiveSetOfChannel` (`IEmoteService.cs:9,13`) sowie die DTO-Parameter
`NewlyArchivedCount`/`NewlyRestoredCount` der Guid-Altform (nach E4 immer 0, und ohne das
Live-Event des Endpunkts ohne Leser); `emote_set_id_empty` (E14). Ein Body mit
`sevenTvEmoteIds` an der kanalgebundenen Route landet dann bei `EmoteIds == null` und 400
`emote_ids_empty` — dieselbe Antwort wie auf `main`. Der Abbau der Altform selbst ist Folge-Issue 1
der Spec-200 §21, nicht Teil von #253.

### 5.7 Rechte-Änderung, ausformuliert

| Heute (kanalgebunden, `CanViewUsageStatsAsync`) | Neu (set-zentrisch, `CheckAsync`) |
|---|---|
| Admin-Allowlist | nur, wenn Besitzer oder `editor_of` |
| Broadcaster (Twitch-Konto = Kanal) | Besitzer des Sets — ja, wenn das Set dem eigenen 7TV-Konto gehört; ein fremdes Set, das der Kanal aktiv hat, **nein** |
| Live-Moderator ohne 7TV-Editor-Recht | **nein** |
| 7TV-Editor des Kanal-Accounts | ja (`editor_of`) |

Die Prüfung ist keine Zugriffskontrolle (die Mutation ist passiert), sondern Audit-Ehrlichkeit
(`IImportTargetOwnershipService.cs:64-72`); sie ist in jeder Zeile **enger** als die alte, nie
weiter. Wer die 7TV-Mutation mit dem eigenen Token schaffte, ist Besitzer oder Editor und passiert
die Prüfung. Der Fall „Broadcaster, dessen Kanal ein fremdes Set aktiv hat" konnte auch heute nichts
löschen, ohne auf dem fremden Konto Editor zu sein. Mit E17 dazu: die Meldung wirkt sofort, der
Resync prüft sie nach — das Restrisiko steht in F12.

### 5.8 `GET /api/seventv/me/emote-set-targets` — die Entscheidung wandert in die Liste (E19)

Additiv, kein Bruch für den Picker:

```
EmoteSetTargetAccount   += sevenTvUserId: string | null        // userByConnection.id; null = Liste nicht lesbar / kein 7TV-Konto
EmoteSetTargetSummaryDto += ownerSevenTvUserId: string | null  // owner.id; null = 7TV nannte keinen
EmoteSetTargetSummaryDto += editable: boolean
```

`editable` ist wahr genau dann, wenn `ownerSevenTvUserId` gesetzt ist und gleich dem
`sevenTvUserId` eines Accounts **dieser Antwort** ist, dessen Liste lesbar war — die Regel von
`OwnershipEvidence.MatchAgainstAllKnownAccounts` (`ImportTargetOwnershipService.cs:299-310`),
herausgezogen in **eine** reine Funktion (Core), die der Endpunkt für die Liste und der
Besitzprüfungs-Service für die Meldung gleichermaßen aufrufen. Ein Set ohne `ownerSevenTvUserId`
ist `editable: false` (F16). `kind` bleibt daneben die zweite Bedingung (positiv, `NORMAL`). Die
IDs stehen auf dem Draht, damit ein Test die Regel nachrechnen kann, nicht damit das Frontend sie
nachbaut: **das Frontend liest `editable`, es berechnet es nicht.** Der Picker
(`import-target-choices.ts`) bekommt einen dritten `ImportTargetDisabledReason` `notEditable`
(Wortlaut #255).

---

## 6. Vertrag: Frontend

### 6.1 Datei-Schritt und Parser

- `parsePurgeRunProtocol(text)` und `parseTransferRunForRestore(text)` verlieren den Parameter
  `expected`. Ergebnis: `{ ok: true, rows, target: { emoteSetId }, … }` — der Purge-Parser gibt
  zusätzlich `channelName` (Envelope) und `meta` weiter wie heute, der Transfer-Parser `stage`;
  beide liefern das Ziel **nur** aus `meta`. Die Prüfleiter behält Envelope, `kind`, eigene
  `formatVersion`, `meta`-Form und Zeilen; `wrongChannel`/`wrongSet` entfallen (E15).
- `FileImportResult` für `'restore'` wird `{ kind: 'restore'; rows: RestoreRow[]; target:
  ResolvedRestoreTarget }` mit

  ```
  ResolvedRestoreTarget {
    emoteSetId: string
    setName: string                     // aus der Zielliste; Fallback Set-ID
    ownerDisplayName: string            // aus der Zielliste, nur Anzeige
    twitchLogin: string                 // Account, für die Vorschau-Route des ungetrackten Falls
    trackedChannelName: string | null   // null = ungetrackt
    isActiveSet: boolean                // nur für getrackte Ziele bedeutsam (E21); false bei ungetrackt
    hostChannelName: string             // Kanal der Seite, an der der Lauf gestartet wird (E13)
    hostSelectedSetId: string | null    // gewähltes Set der Seite, für den Hinweis (E21); null ohne Set
  }
  ```

- `FileImportStep` behält `channelName` als Input (er ist der `hostChannelName`) und bekommt
  `hostSelectedSetId: string | null`; das bisherige Pflicht-Input `setId` entfällt, weil kein
  Prüfschritt es mehr liest. `ImportSourceDialogData.setId` wird `string | null` (E22) und dient
  den drei Kopier-Türen; bei `null` sind sie deaktiviert.
- Ablauf im Schritt: Envelope → Parser → Zielprüfung (4.2, über `resolveEditableSet`) → `picked`.
  Fehler-Schlüssel, neu in beiden Locales unter `restore.import.errors.*`: `targetNotEditable`,
  `targetNotSelectable`, `targetCheckUnavailable`, `noTargetSetForCopy` (die deaktivierten
  Kopier-Türen ohne Set). Wortlaut vorläufig, #255.
- UI-Designsprache §7.3 erhält den Satz, dass der Datei-Zweig für Rückweg-Dateien das Ziel aus der
  Datei nimmt und prüft, statt es gegen die Seite zu halten, und dass er ohne gewähltes Set nur
  Rückweg-Dateien liest (12.2).

### 6.2 Die gemeinsame Vorprüfung (E19)

`SevenTvEmoteSetService.resolveEditableSet(emoteSetId): Observable<EditableSetResolution>` mit

```
EditableSetResolution =
  | { status: 'editable'; target: <Account- und Set-Felder wie in ResolvedRestoreTarget ohne host*> }
  | { status: 'notSelectable' }          // gefunden, kind !== 'NORMAL'
  | { status: 'notEditable' }            // nicht gefunden oder editable === false, Liste vollständig
  | { status: 'unavailable' }            // Liste unvollständig, 429/503/Netz
```

— über `listEmoteSetTargets()` mit einer 60-s-Client-Kopie (Regeln wie
`loadCachedEmoteSetPreview`, `seven-tv-emote-set.service.ts:106-160`: nur Erfolg wird gecacht, ein
Fehler nie; `refresh` umgeht sie). Der Picker liest dieselbe Kopie (sein `reload` erzwingt
`refresh`). Drei Aufrufer: `FileImportStep` (Restore), `MassDeletePanel` vor der Bestätigung
(Delete), `import-flow.ts` vor `recheckTransferPlan` bei Replace-Zeilen. Die Vorprüfung ist die
Antwort auf H3: sie trifft **dieselbe** Entscheidung wie das Backend, weil sie `editable` liest.

### 6.3 Flow und Bestätigung

- `startRestoreFlow(deps, target: ResolvedRestoreTarget, rows)` ersetzt die vier Positionsparameter
  `channelName, setId, setName, isActiveSet`. Beide Aufrufer (`ImportTrigger` und
  `MassDeletePanel.openRestoreConfirm`, E16) bauen das Ziel aus dem Ergebnis der Vorprüfung; der
  Panel-Aufrufer mit `hostSelectedSetId = setId()` der Seite.
- `RestoreConfirmDialogData` bekommt `emoteSetId: string` (angezeigt, E21), `ownerDisplayName:
  string`, `trackedChannelName: string | null`, `foreignToView: boolean` (= `target.emoteSetId !==
  hostSelectedSetId`). `setName`/`isActiveSet` bleiben.
- Slot-Vorschau nach 4.3, Punkt 8.

### 6.4 Restore-Dienst

- `startRestore(target: { setId, expectedChannelName: string | null, resyncChannelName: string |
  null, hostChannelName, setName, ownerOrChannelLabel }, emotes, skippedDuplicates,
  duplicateCheckAvailable, skippedNameTaken)`; `RestoreRunInfo` nach E13.
  `expectedChannelName = trackedChannelName` wenn `isActiveSet`, sonst `null`;
  `resyncChannelName = trackedChannelName` wenn getrackt und **nicht** aktiv, sonst `null` (E12).
- `reportRestored` → `SevenTvEmoteSetService.reportRestoredInSet(setId, { sevenTvEmoteIds,
  expectedChannelName })` (neu, neben `reportImportedToSet`, `seven-tv-emote-set.service.ts:169-171`),
  Retry-Policy unverändert (`MAX_AUTOMATIC_SYNC_RETRIES`, 401/403 ohne Retry).
- `syncReport`/`syncReportReason` aus der Antwort (F8, E23): `succeeded`, wenn `unresolvedChannel`
  null ist **und** (`channels` leer ist **oder** für jeden Kanal `restoredCount === reportedCount`);
  sonst `partial` mit `channelMismatch` bzw. `shortfall`; jeder HTTP-Fehler nach den Retries →
  `failed` mit `forbidden` (403), `setNotFound` (404), `unavailable` (429/503/Netz), sonst
  `other`. Ein 404 (Set inzwischen weg) endet damit in `failed`, was #224 im Frontend schließt.
- Resync (E12): `channelService.resync(resyncChannelName)` nur bei `resyncChannelName !== null`
  **und** `!resyncTriggered.includes(resyncChannelName)`; Zustände `succeeded`/`cooldown`/`failed`
  wie heute; sonst bleibt `resyncTrigger` auf `idle`, das Dock zeigt keine Resync-Zeile — außer
  der Kanal steht in `resyncTriggered`, dann zeigt es „wird abgeglichen" ohne eigenen Request.
  **Fallback bei endgültig gescheiterter Meldung (Nachtrag N1):** endet die erste Meldung nach den
  Retries in `failed` (jeder Grund), gilt `resyncTriggered` als leer, und der Dienst stößt
  `channelService.resync(resyncChannelName ?? expectedChannelName)` an, sofern der Wert nicht
  `null` ist — mit denselben Zuständen im Dock; für ein ungetracktes Ziel (beide `null`) weiterhin
  keiner. Ein manueller Retry löst keinen zweiten Fallback aus.
- „Erneut melden" (`retrySyncReport`) gibt es bei `failed` (jeder Grund) und bei
  `partial`/`shortfall`, **nicht** bei `partial`/`channelMismatch` (Nachtrag N4): das Dock zeigt den
  Knopf dort nicht, und der Dienst weist den Aufruf ab.
- `resetIfChannelChanged(pageChannelName)` vergleicht mit `run.hostChannelName` (E13).
- Die Dock-Zielzeile (4.4, Punkt 12) liest `setName` und `ownerOrChannelLabel` aus dem
  Laufdatensatz (Anzeigefelder, nie verglichen).

### 6.5 Delete- und Import-Dienst

- `SevenTvDeleteService.startDelete(setId, channelName, emotes, expectedChannelName)` — der vierte
  Parameter ist `channelName`, wenn `setId === activeSetId` der Seite, sonst `null`;
  `DeleteRunInfo` trägt ihn. `reportDeleted` → `reportDeletedInSet(run.setId, { sevenTvEmoteIds,
  expectedChannelName })`; `syncReport`/`syncReportReason` nach derselben Dreiwertigkeit wie 6.4;
  kein eigener Resync im Erfolgsfall (der Delete hat heute keinen, die Seite lebt vom
  `channel.synced`). **Fallback (Nachtrag N1):** endet die erste Meldung nach den Retries in
  `failed`, stößt der Dienst `channelService.resync(expectedChannelName)` an, sofern nicht `null`
  — der Delete kennt keinen `resyncChannelName`, für ein nicht-aktives oder ungetracktes Set also
  keiner. Das Delete-Dock zeigt dafür keine eigene Resync-Zeile (N1, Festlegung).
  `DeleteRunInfo.channelName` **bleibt** — Kanal der Seite, Envelope-`channelName` des
  Purge-Protokolls und Dateiname (`mass-delete-panel.ts:592-618`), nur nicht mehr Adressat der
  Meldung. Die Retry-Regel aus 6.4 (kein „Erneut melden" bei `channelMismatch`, N4) gilt auch hier.
- `SevenTvImportService.reportRemoved` → `reportDeletedInSet(run.targetSetId, { sevenTvEmoteIds,
  expectedChannelName: run.targetIsActiveSet ? run.targetChannelName : null })`; `removalReport`
  nach derselben Dreiwertigkeit; der `channelName === null`-Frühausstieg (`:688-691`) entfällt.
  Der Import-Resync (`:619-646`) bleibt, wie er ist, aber er läuft **nach** der Löschmeldung und
  überspringt den Kanal, wenn er in deren `resyncTriggered` steht (F15); scheitert die Löschmeldung
  endgültig, gilt `resyncTriggered` als leer und der Import-Resync läuft wie ohne Löschmeldung
  (Nachtrag N1 — hier keine Lücke, nur ausdrücklich gemacht); `reportImported` bleibt
  unverändert (kanalgebunden für getrackte, set-zentrisch für ungetrackte Ziele — Spec-200 6.7).
  Die Retry-Regel aus 6.4 (kein „Erneut melden" bei `channelMismatch`, N4) gilt auch für
  `retryRemovalReport` und die Notiz der Löschmeldung im Import-Dock.
- `EmoteAdminService.syncDeleted/syncRestored`, `SyncBookkeepingBody`, `SyncDeletedResult`,
  `SyncRestoredResult` entfallen; die neuen Antworttypen leben bei `SevenTvEmoteSetService`.
- `RunProgressPanel` bekommt `syncReportReason` als Input und zeigt die Grundzeile unter der
  bestehenden `syncReportFailed`-Notiz (`run-progress-panel.ts:92-107`).

### 6.6 Regel 7, der Wächter, der Einstieg und das Dock

- `conflict-resolution.ts`: Regel 7, `ruleReplaceNeedsTrackedTarget`, der Union-Wert
  `replaceNeedsTrackedTarget` und `ResolutionContext.targetIsTracked` entfallen; die Doku von
  `validateResolution` zählt sechs Regeln. `import-confirm-dialog.ts:639-642` und die
  Disabled-Reason-Zuordnung in `import-conflict-resolution-step.ts:88` entfallen mit.
- `seven-tv-import.service.ts:376-379` entfällt.
- Beide Locale-Schlüssel (`import.resolve.replaceNeedsTracked`, `de.json:1234`, und
  `import.resolve.violation.replaceNeedsTrackedTarget`, `:1254`) entfallen.
- `usage-stats-page.html:122-135`: `ImportTrigger` verlässt den Block `selectedEmoteSetId()`
  (E22) und steht in einem eigenen `@if (!isCoarse())`; `[setId]` bindet `selectedEmoteSetId()`
  (nullbar); der Kopier-Knopf bleibt im Set-Gate.
- `RestoreProgressSection` (neu, `shared/seven-tv/restore-progress-section.ts`) übernimmt aus
  `MassDeletePanel` die Restore-Blöcke (`:242-275`: Notizen, Fortschritt, Announcer-Zwilling) und
  wird in `usage-stats-page.html` neben `app-import-progress-section` (`:1185`) außerhalb des
  Set-Gates eingebunden; `dockVisible()` zählt `restoreShown`/`restoreNoticePending` bereits
  (`usage-stats-page.ts:1494-1503`) und bleibt. Der „Wiederherstellen"-Knopf am fertigen
  Delete-Lauf bleibt im Panel. Die Vote-Session-Detailseite bindet die neue Section ebenso ein,
  weil ihr Panel denselben Restore-Knopf hat.
- `usage-stats-page.ts` (Nachtrag N2): beim Settle eines Restore-, Delete- oder Import-Laufs mit
  mindestens einer erfolgreichen Zeile, dessen Ziel-Set das gewählte nicht-aktive Set ist, ruft die
  Seite `reloadLiveMembers()` (`refresh: true`); ist das Ziel-Set nicht-aktiv, aber nicht gewählt,
  merkt sie es in `liveMembersRefreshFor` für den nächsten Ladevorgang genau dieses Sets vor.

---

## 7. Grenzfälle

| Fall | Verhalten |
|---|---|
| **Set gelöscht zwischen Zielprüfung und Lauf** | Die ADDs scheitern bei 7TV (Zeilen `failed`); ohne `doneKeys` keine Meldung (`onRunComplete`, `:250-252`). Gelingt ein ADD trotzdem (7TV-Race), antwortet die Meldung 404 → `failed`/`setNotFound`, Retry-Knopf; kein Audit; Fallback-Resync des erwarteten bzw. nicht-aktiven Kanals (Nachtrag N1). Kein stiller Erfolg (#224) |
| **Meldung scheitert endgültig** (403, 404, 503, 429, Netz nach den Retries) (Nachtrag N1) | Das Backend hat Stufe 7 nie erreicht, kein Backend-Resync. Restore: Client-Resync von `resyncChannelName ?? expectedChannelName`; Delete: von `expectedChannelName`; Replace-Löschmeldung: der Import-Resync läuft wie ohne Löschmeldung. Ein ungetracktes Ziel: keiner. Läuft der Resync in den Cooldown (429), zeigt das Dock „kommt ohnehin" (F15); ein 403 des Resync (Kanalrolle fehlt) ist `failed` in der Resync-Zeile, die Zeilen heilt der periodische Resync |
| **Recht entzogen vor dem Lauf** (älter als der Listen-/Grants-Cache) | Vorprüfung blockt: kein Delete-Dialog, kein Replace-Start, kein Restore — `targetNotEditable` (E19) |
| **Recht entzogen mitten im Lauf** (jünger als der Cache) | Mutation scheitert mit `LACKING_PRIVILEGES` → Engine bricht ab, Token wird gelöscht (bestehendes Verhalten). Gelingt sie noch (7TV-Cache), und die Meldung bekommt 403 → `failed`/`forbidden` im Dock, kein Nachmeldeweg (E20). Passiert auch die Meldung noch (Grants-Cache, F12) → Zeilen geändert, Resync stellt den 7TV-Stand binnen 60 s wieder her |
| **Veralteter aktiver Set-Stand** (7TV-Set-Wechsel, `ActiveEmoteSetId` hinkt) | Kein Treffer, aber `expectedChannelName` gesetzt → `unresolvedChannel: { …, 'activeSetDiffers' }`, `partial`/`channelMismatch`, Papier-Eintrag **mit** dem Kanal als `ChannelName` (er ist der aktive, ungesperrte Besitzerkanal, Nachtrag N3) und den `unresolved*`-Feldern, Resync des Kanals ausgelöst; nach dessen `channel.synced` stimmt die Seite wieder. Kein „Erneut melden" (N4) |
| **Erwarteter Kanal inzwischen verlassen oder gesperrt** | `unresolvedChannel: { …, 'notTracked' }`, `partial`, kein Resync (nichts zu synchronisieren, und die Sperre wird nicht verraten); der Papier-Eintrag bleibt **ohne** Kanal, weil der Besitzerkanal nach derselben Regel nicht auflösbar ist (N3). Kein „Erneut melden" (N4) |
| **Geteiltes Set** (zwei getrackte Kanäle mit demselben aktiven Set) | Beide in `channels`, beide Zeilen geändert, je Kanal ein Audit-Eintrag, je Kanal `channel.synced` und Resync (`resyncTriggered` beide); `succeeded` nur, wenn beide vollständig |
| **Geteiltes Set, ein Kanal hinkt** | Der Kanal des Ziel-Accounts wird über `expectedChannelName` erkannt (Mismatch, s. o.); ein **zweiter** Kanal mit veraltetem Stand bleibt unentdeckt bis zu seinem periodischen Resync (F13, benannter Rest) |
| **Ziel ist das gewählte Set der Seite** (aktives Set des Seitenkanals) | Wie heute erlebbar: Zeilen im Seitenkanal geändert, `channel.synced` für die Seite, lauter Reload; Dialog ohne Fremd-Hinweis; Audit mit Kanal; Backend-Resync des Kanals (Cooldown) |
| **Ziel ist ein anderes, nicht-aktives Set des Seitenkanals** | Nur Papier (`channels` leer, `expectedChannelName: null`), Papier-Eintrag **mit** dem Seitenkanal (N3), Dialog mit „nicht aktiv"-Zeile **und** Fremd-Hinweis (E21: anderes Set als das gewählte), Frontend-Resync des Seitenkanals (E12). **Die Mitgliederliste des Ziel-Sets lädt nicht über `channel.synced` neu** — das erreicht nur die Zeilen des aktiven Sets und umgeht den 60-s-Cache der Mitgliederroute nicht (Befund F1 der Live-Verifikation). Stattdessen: ist das Ziel-Set das gewählte Set der Seite, lädt die Seite die Liste beim Settle des Laufs mit `refresh` neu; sonst trägt der nächste Ladevorgang genau dieses Sets `refresh` (Nachtrag N2) |
| **Ziel ist das gewählte, nicht-aktive Set der Seite** (Nachtrag N2) | Wie die Zeile darüber, ohne Fremd-Hinweis; beim Settle des Laufs (Restore, Delete oder Import) lädt die Seite die Mitgliederliste mit `refresh=true` neu, sodass der Nutzer den neuen Stand sieht, ohne auf den Cache-Ablauf zu warten |
| **Ziel ist ein getrackter Fremdkanal** | Zeilen dort geändert, `channel.synced` und Resync dort (unter dem Konto des Meldenden); die Seite des Nutzers bleibt stehen, Dock mit Zielzeile |
| **Ungetracktes Ziel** | Vorprüfung über `editable`; nur Papier mit Besitzer und **ohne** Kanal (N3: kein Besitzerkanal); `expectedChannelName: null`; kein Resync, keine Resync-Zeile, auch nicht als Fallback (N1); Slot-Vorschau über `loadEmoteSetPreview(twitchLogin, setId)` |
| **Geteiltes Set, Besitzerkanal nicht getroffen** (Set des Accounts A ist aktives Set des getrackten Kanals X, nicht des getrackten Kanals A) (Nachtrag N3) | Kanal-Eintrag für X; **kein** Papier-Eintrag (ein Kanal-Eintrag wurde geschrieben, kein Mismatch), also nichts in der Kanal-Ansicht von A. Benannter Rest derselben Klasse wie F13, nicht geschlossen |
| **Besitzerkanal umbenannt, `expectedChannelName` trägt den alten Namen** (N3) | Die Namensauflösung in Schritt 3 verfehlt ihn (`notTracked`), die ID-Auflösung in Schritt 3a findet die umbenannte Zeile: Papier-Eintrag mit dem **neuen** Kanalnamen, `unresolvedChannelName` = der alte. Heilt sich mit der nächsten Zielliste (60 s) |
| **Seite ohne gewähltes Set** (kein aktives Set, Kanal vor dem ersten Sync) | `ImportTrigger` sichtbar (E22); Kopier-Türen deaktiviert mit Grund; Rückweg-Datei lesbar; Bestätigung mit Fremd-Hinweis (`hostSelectedSetId === null`); Dock in `RestoreProgressSection` sichtbar |
| **Zwei Restore-Läufe gleichzeitig** | Unmöglich: `SevenTvRunArbiter.activeRun()` wird vor der Bestätigung **und** nach dem Filter-Read geprüft (`restore-flow.ts:112`, `:142`), auch am Panel-Einstieg (`mass-delete-panel.ts:630`). Ein zweiter Pick während eines Laufs endet still. Die Vorprüfung kostet höchstens ein Permit je Minute |
| **Persönliches Set als Ziel** | `targetNotSelectable` (E11), kein Lauf |
| **Rate-Limit der Vorprüfung** (429 auf `ForeignEmoteLookup`) | `targetCheckUnavailable`, kein Lauf; nach dem Minutenfenster erneut wählbar (F3); ein gecachter Treffer braucht kein Permit |
| **Set ohne `owner.id` in 7TVs Antwort** | `editable: false` → Vorprüfung blockt; das Backend hätte per Lookup vielleicht zugelassen (F16, bewusst strenger) |
| **Rückweg-Datei eines nie gestarteten Laufs** | Unverändert (#230): jede Zeile fällt über „schon vorhanden" heraus, transiente Notiz, kein Lauf — jetzt auch für ein ungetracktes Ziel |
| **Dieselbe Datei auf zwei verschiedenen Kanalseiten** | Beide Male dasselbe Ziel, dieselbe Prüfung; nur `hostChannelName`/`hostSelectedSetId` unterscheiden sich (Dock-Bindung, Hinweis) |
| **Alter Guid-Tab während K7** (Produktions-Frontend nach einem Set-Wechsel) | Meldet `{ emoteIds }` an die kanalgebundene Route; keine Zeile ändert sich, Audit-Eintrag mit `legacyBodyForm: true`, Resync des Kanals (Cooldown), Antwort in alter Form mit gefundener Zahl (E24) — der alte Tab zeigt `succeeded`, der Resync setzt den 7TV-Stand |
| **Zwei Meldungen desselben Laufs auf denselben Kanal** (Import mit Replace: `sync-imported` + `sync-deleted`) | Höchstens ein Resync je Kanal und 60 s (F15): der zweite `TryBeginAsync` scheitert, kein Fehler, der Kanal fehlt in `resyncTriggered`, der Client stößt keinen eigenen an |

---

## 8. Akzeptanzkriterien

Jedes Kriterium ist so formuliert, dass ein Test oder ein Handgriff es entscheidet.

1. Eine Übertragungsdatei mit ungetracktem Ziel (`meta.targetChannelName: null`), auf der
   Nutzungsseite eines **anderen** Kanals eingelesen, öffnet die Restore-Bestätigung mit Set-Name,
   Set-ID und Besitzer und startet nach Bestätigung einen Lauf gegen `meta.targetEmoteSetId`.
2. Ein Purge-Protokoll, dessen `meta.emoteSetId` nicht das gewählte Set der Seite ist, wird
   **nicht** abgewiesen; das Ziel ist das Set der Datei; ist es ein nicht-aktives Set seines
   getrackten Kanals, zeigt die Bestätigung die „nicht aktiv"-Zeile.
3. Eine Rückweg-Datei, deren Set in keinem Account der Zielliste steht oder dort `editable: false`
   trägt, führt zum Fehler `targetNotEditable` im Banner des Datei-Schritts; kein Dialog schließt,
   kein Request an 7TV.
4. Antwortet die Zielliste mit `sevenTvUnavailable: true`, mit einem `setsUnavailable`-Account, mit
   429 oder 503, und das Set ist nicht bearbeitbar gefunden, lautet der Fehler
   `targetCheckUnavailable`; kein Lauf.
5. Ein Ziel mit `kind !== 'NORMAL'` führt zu `targetNotSelectable`; kein Lauf.
6. Innerhalb von 60 s gehen für beliebig viele Vorprüfungen (Datei-Pick, Delete-Bestätigung,
   Replace-Start) **höchstens ein** Request an `/api/seventv/me/emote-set-targets`; die Bestätigung
   löst keinen zweiten aus; ein Fehler wird nie gecacht.
7. Die Restore-Meldung geht an `POST /api/seventv/emote-sets/{setId}/sync-restored` mit
   `{ sevenTvEmoteIds, expectedChannelName }` (IDs dedupliziert, eine je Emote auch bei zwei
   Aliasen; `expectedChannelName` = getrackter Kanal des Ziels bei aktivem Set, sonst `null`); es
   gibt keinen Request an `/api/channels/*/emotes/sync-restored` mehr aus dem Frontend.
8. Die Delete-Meldung (Nutzungsseite **und** Vote-Session-Detailseite) geht an `…/sync-deleted`
   des Laufs-Sets; ebenso die Löschmeldung eines Replace-Laufs; beide mit `expectedChannelName`
   nach 4.5/4.6.
9. `…/sync-deleted` und `…/sync-restored` antworten 401 anonym, 400 `invalid_emote_set_id` bei
   ungültigem Routenwert, 400 `emote_ids_empty` bei leerer Liste, 400 `invalid_channel_name` bei
   ungültigem `expectedChannelName`, 404 `emote_set_not_found`, 403 bare, 503
   `foreign_channel_seventv_unavailable` — auf 404/403/503 **ohne** Service-Aufruf, Audit-Eintrag
   und Resync; beide Routen tragen die Policy `Bookkeeping`.
10. Für ein Set, das das aktive Set eines getrackten, nicht gesperrten Kanals ist, archiviert bzw.
    reaktiviert der Service die Zeilen dieses Kanals, schreibt einen Audit-Eintrag mit Kanal und
    `targetIsActiveSetOfChannel: true`, und die Antwort nennt den Kanal mit Zähler und `notFoundIds`.
11. Für ein geteiltes Set (zwei Kanäle) geschieht 10 für **beide** Kanäle; die Antwort listet beide.
12. Ein Kanal, dessen `TwitchChannelId` auf `Channels:ExcludedChannelIds` steht, wird nicht berührt
    und nicht genannt, auch wenn sein aktives Set das Ziel ist; als erwarteter Kanal ergibt er
    `reason: 'notTracked'`, nie einen Hinweis auf die Sperre.
13. Für ein ungetracktes Set und für ein nicht-aktives Set eines getrackten Kanals wird keine Zeile
    berührt; genau ein Papier-Eintrag mit `TargetType = "emoteSet"`, `TargetId = setId` entsteht;
    `channels` ist leer. Für das ungetrackte Set trägt er `ChannelName = null` und
    `targetOwnerTwitchLogin`; für das nicht-aktive Set des getrackten Kanals trägt er dessen
    `ChannelName` und `targetIsActiveSetOfChannel: false` (Nachtrag N3, AK 38).
14. Je Kanal mit tatsächlich geänderten Zeilen wird genau ein `channel.synced` aus dem Endpunkt
    veröffentlicht; ohne Änderung keines.
15. Der Delete-, der Restore- und der Import-Dienst lesen `channels: []` ohne `unresolvedChannel`
    als `succeeded`, einen Kanal mit `count < reportedCount` als `partial`/`shortfall`, einen
    `unresolvedChannel` als `partial`/`channelMismatch`, 403 als `failed`/`forbidden`, 404 als
    `failed`/`setNotFound`, 429/503/Netz nach den Retries als `failed`/`unavailable` — ein 404 endet
    nie in `succeeded` (#224); das Dock zeigt den Grund.
16. Der Auflösungsschritt bietet „Ziel ersetzen" für ein ungetracktes Ziel wählbar an;
    `validateResolution` kennt keine Regel `replaceNeedsTrackedTarget`; `startImport` wirft nicht
    mehr für einen Replace gegen `channelName === null`.
17. Ein Replace-Lauf in ein ungetracktes Set verlangt weiterhin die Rückweg-Datei vor dem Start und
    meldet seine bestätigten REMOVEs set-zentrisch; das Audit (globale Ansicht) zeigt danach einen
    `syncImported`- und einen `syncDeleted`-Eintrag ohne Kanal, beide „für <ownerLogin>". Ist das
    Ziel ein nicht-aktives Set eines **getrackten** Kanals, trägt der `syncDeleted`-Eintrag statt
    dessen diesen Kanal (Nachtrag N3, AK 38) — wie der `syncImported`-Eintrag, den `reportImported`
    dort kanalgebunden schreibt.
18. Nach einem Restore dieser Datei auf einer anderen Kanalseite zeigt das Audit zusätzlich einen
    `syncRestored`-Eintrag — ohne Kanal für das ungetrackte, mit dem Besitzerkanal für das
    getrackte nicht-aktive Set (N3).
19. Die Bestätigung eines Restore, dessen Ziel nicht das **gewählte Set** der Seite ist — auch ein
    anderes Set desselben Kanals, auch eine Seite ohne gewähltes Set — zeigt den Fremd-Hinweis; für
    das gewählte Set nicht.
20. Ein fertiger Restore-Lauf verschwindet beim Wechsel auf einen anderen Kanal aus dem Dock (wie
    heute); seine Meldung ist trotzdem gesendet.
21. Nach einem Restore in ein nicht-aktives Set eines getrackten Kanals löst der Client `POST
    /api/channels/{kanal}/resync` aus, sofern der Kanal nicht in `resyncTriggered` steht; nach einem
    Restore in ein ungetracktes Set keinen. (Bleibt gültig; was bei endgültig gescheiterter Meldung
    gilt, steht in AK 36.)
22. `wrongChannel`, `wrongSet`, `emote_set_id_empty` und beide Regel-7-Schlüssel existieren in
    keiner Locale, keinem Parser und keinem `ApiErrorCodes` mehr; `api-error-locales.spec.ts` bleibt
    grün.
23. Die kanalgebundene Route mit `{ emoteIds }` antwortet in der alten Form (200 mit
    `archivedCount` = gefundene Guids des Kanals, `notFoundIds`, Log-Zeile „legacy body form"),
    **ändert keine Zeile** (`IsArchived`/`ArchivedAt`/`LastSyncedAt` unverändert), schreibt den
    Audit-Eintrag mit `legacyBodyForm: true` und stößt den Resync des Kanals unter dem Cooldown an;
    ein Body `{ emoteSetId, sevenTvEmoteIds }` dort → 400 `emote_ids_empty`.
24. Bestehende `purge-run`-Dateien beider Formatversionen werden zeilen-identisch gelesen wie vor
    dieser Spec (die Parser-Specs für `readProtocolRow` bleiben unverändert grün).
25. Alle vier Gates des Repos grün (`dotnet test`, Vitest, E2E, `coverage-local`), plus die
    Live-Verifikation aus Abschnitt 11 mit Zahlen im PR-Text.
26. Jede Meldung, die mindestens einen Kanal trifft, ruft je Kanal genau einmal
    `IChannelResyncCooldown.TryBeginAsync` und bei Erfolg `TriggerResyncAsync`; die Antwort nennt
    genau die Kanäle mit `Triggered` in `resyncTriggered`; ein nicht erworbener Cooldown erzeugt
    weder Fehler noch Eintrag in `resyncTriggered`.
27. Zwei Meldungen auf denselben Kanal binnen 60 s lösen genau einen Resync aus; der Client stößt
    für einen Kanal in `resyncTriggered` nie einen eigenen an. (Bleibt gültig; der Fallback aus
    AK 36 läuft nur ohne Antwort, also ohne `resyncTriggered`, und der Cooldown fängt Doppelte.)
28. Ein `expectedChannelName`, dessen Kanal aktiv ist, aber ein anderes `ActiveEmoteSetId` trägt,
    ergibt `unresolvedChannel: { channelName, reason: 'activeSetDiffers' }`, keine geänderte Zeile,
    einen Papier-Eintrag mit `unresolvedChannelName`, `unresolvedReason`, `unresolvedSevenTvEmoteIds`
    (und, weil dieser Kanal der Besitzerkanal ist, mit ihm als `ChannelName` — N3, AK 39),
    und einen Resync dieses Kanals; ein fehlender, inaktiver oder gesperrter Kanal ergibt
    `'notTracked'` ohne Resync und einen Papier-Eintrag ohne Kanal.
29. `GET /api/seventv/me/emote-set-targets` liefert je Account `sevenTvUserId` und je Set
    `ownerSevenTvUserId` und `editable`; `editable` ist genau dann wahr, wenn der Besitzer die 7TV-ID
    eines Accounts derselben Antwort mit lesbarer Liste ist; ein Set ohne Besitzer-ID ist
    `editable: false`; der Picker deaktiviert ein nicht bearbeitbares Set mit Grund.
30. `editable` und `IImportTargetOwnershipService.CheckAsync` entscheiden über dieselbe reine
    Funktion: ein Test füttert beide mit derselben Listenkonstellation und erhält dieselbe Antwort
    für jeden Fall außer dem Owner-Lookup-Fall (F16), in dem `editable` `false` ist.
31. Vor der Delete-Bestätigung läuft die Vorprüfung; scheitert sie, öffnet keine Bestätigung, die
    Abbruchnotiz nennt den Grund, es gibt keinen Request an 7TV.
32. Vor dem Start eines Plans mit Replace-Zeile läuft die Vorprüfung; scheitert sie, startet nichts
    und die Seite nennt den Grund; ein Plan ohne Replace läuft ohne Vorprüfung.
33. Auf einer Kanalseite ohne gewähltes Set ist der Import-Einstieg sichtbar; der Quellschritt
    deaktiviert Kanal, Bestenliste und Kopier-Dateien mit Grund; eine Übertragungsdatei wird gelesen,
    bestätigt (mit Fremd-Hinweis) und wiederhergestellt; das Restore-Dock ist sichtbar.
34. Der Kopier-Knopf („Übertragen") bleibt ohne gewähltes Set unsichtbar; kein neuer Button
    entsteht.
35. Die Bestätigung eines Restore zeigt die aufgelöste Set-ID aus der Zielliste, nie einen Wert aus
    der Datei, der dort nicht bestätigt wurde.

---

## 9. Testpyramide

### 9.1 `tests/EmotePurge.Api.Tests`

- Neue Klasse nach dem Muster `SevenTvEmoteSetSyncImportedEndpointTests.cs` (`:44-136`), für
  **beide** Routen (Theory über den Routen-Suffix): 401 anonym; 400 bei 33-stelligem Routenwert vor
  jedem Service-Aufruf; 400 `emote_ids_empty`; 400 `invalid_channel_name` bei ungültigem
  `expectedChannelName`; 404 bei `NotFound` des Owner-Lookups; 403 bare ohne Body; 503 ohne
  Service-Aufruf und ohne Cooldown-Aufruf; 200 mit weitergereichter Besitzer-Identität und
  `expectedChannelName`, Live-Event je Kanal mit `NewlyChangedCount > 0` (Substitut
  `IRedisPublisher`), `TryBeginAsync`/`TriggerResyncAsync` je getroffenem Kanal und für einen
  `activeSetDiffers`-Mismatch (Substitute `IChannelResyncCooldown`, `IChannelService`), Freigabe
  des Cooldowns bei `NotFound`/`NotActive`, `resyncTriggered` = genau die `Triggered`-Kanäle, kein
  Resync bei nicht erworbenem Cooldown. AK 9, 14, 26–28.
- `EmoteRoutePolicyTests.cs:33-48`: zwei neue `InlineData` mit `Bookkeeping`.
- `AuthFilterMatrixTests.cs:622-735`: die Leiterfälle 2–4 entfallen; der Fall „Guid-Form → 200 und
  Guid-Überladung erreicht" bleibt und prüft zusätzlich Cooldown + Resync-Trigger; neu:
  `{ emoteSetId, sevenTvEmoteIds }` → 400 `emote_ids_empty` (AK 23).
- Zielliste: `SevenTvForeignEmoteSetEndpointTests.cs` (oder die Klasse, die `/me/emote-set-targets`
  heute pinnt) prüft `sevenTvUserId`, `ownerSevenTvUserId`, `editable` in den drei Fällen eigener
  Besitzer / `editor_of`-Besitzer / fremder Besitzer, plus Set ohne Besitzer-ID (AK 29).

### 9.2 `tests/EmotePurge.Infrastructure.Tests`

- `EmoteServiceTests.cs`: die set-scoped Fälle (`:378-560`) werden durch die `…InSetAsync`-Fälle
  ersetzt, je Richtung: aktives Set eines getrackten Kanals; geteiltes Set (zwei Kanäle, zwei
  Einträge, zwei Kanal-Objekte); gesperrter Kanal ignoriert (echter `ExcludedChannelFilter` aus
  Konfiguration); ungetracktes Set nur Papier mit Besitzer; nicht-aktives Set nur Papier;
  `IsBotActive = false` kein Treffer; Deduplizierung vor `reportedCount`; Kanal ohne gefundene
  Zeile → Zähler 0 **und** Papier-Eintrag; **Mismatch** `activeSetDiffers` (Kanal aktiv, anderes
  Set) und `notTracked` (fehlend, inaktiv, gesperrt — je ein Fall) mit Papier-Eintrag samt
  `unresolved*`-Feldern und ohne Zeilenänderung; erwarteter Kanal, der getroffen wird, ergibt
  `UnresolvedChannel == null`. AK 10–13, 28.
- Altform (`:19-215`, `:457-478`): die Zeilen-Assertions kehren um — Zeilen **unverändert**,
  `archivedCount` = gefundene, Audit-Eintrag mit `legacyBodyForm: true` (AK 23).
- Neuer Unit-Test der reinen `editable`-Funktion mit denselben Konstellationen wie die
  Ownership-Tests (`SevenTvEditorServiceTests`/Ownership-Tests, bestehend), inklusive F16 (AK 30).

### 9.3 Vitest (`web/`)

- `purge-run-export.spec.ts`, `transfer-run-export.spec.ts`: Ziel-Extraktion für alle drei
  Dateiarten; `wrongChannel`/`wrongSet`-Fälle entfallen; `readProtocolRow`-Fälle bleiben (AK 24).
- `seven-tv-emote-set.service.spec.ts`: `resolveEditableSet` — vier Ausgänge, Cache-Treffer ohne
  zweiten Request, Fehler nie gecacht, `refresh` umgeht (AK 3–6).
- `file-import-step.spec.ts`: die vier Ausgänge im Banner, gesperrter Dateiknopf während der
  Prüfung, späte Antwort nach Abbruch ohne Wirkung (F6), `hostSelectedSetId: null` läuft durch.
- `restore-flow.spec.ts`: Ziel statt Positionsparameter; Slot-Vorschau-Gabel für die drei Fälle;
  `foreignToView` aus dem Set-Vergleich (anderes Set desselben Kanals ⇒ wahr; gewähltes Set ⇒
  falsch; `null` ⇒ wahr) (AK 19, 35).
- `seven-tv-restore.service.spec.ts`, `seven-tv-delete.service.spec.ts`,
  `seven-tv-import.service.spec.ts`: Meldung set-zentrisch mit `expectedChannelName` (AK 7–8);
  Dreiwertigkeit mit Grund aus `channels`/`unresolvedChannel`/HTTP-Status (AK 15); Resync-Regel
  E12 samt `resyncTriggered`-Sperre (AK 21, 27); Import-Resync überspringt einen Kanal aus
  `resyncTriggered`; `resetIfChannelChanged` gegen `hostChannelName`.
- `mass-delete-panel.spec.ts`: Vorprüfung vor der Bestätigung, Abbruchnotiz mit Grund (AK 31).
- `import-flow.spec.ts`: Vorprüfung nur bei Replace-Zeilen (AK 32).
- `conflict-resolution.spec.ts`, `import-confirm-dialog.spec.ts`: Replace für ungetracktes Ziel
  gültig und angeboten (AK 16). `import-target-choices.spec.ts`: `notEditable` deaktiviert (AK 29).
- `run-progress-panel.spec.ts`: Grundzeile je `syncReportReason` (Struktur, nicht Wortlaut).
- `usage-stats-page.spec.ts`: Trigger ohne gewähltes Set sichtbar, Kopier-Knopf nicht (AK 33–34).
- `api-error-locales.spec.ts` bleibt das Gate für E14.

### 9.4 Playwright E2E

- Neu: Übertragungsdatei (`finished`, ein bestätigter REMOVE) mit ungetracktem Ziel auf der
  Nutzungsseite eines **fremden** Kanals → Bestätigung nennt Set, ID, Besitzer und den
  Fremd-Hinweis → ein `addEmote` an das Ziel-Set → `POST /api/seventv/emote-sets/{set}/sync-restored`
  mit `{ sevenTvEmoteIds, expectedChannelName: null }` (AK 1, 7, 18, 19). Mocks:
  `mockEmoteSetTargets` (um `editable`/IDs erweitert) mit einem ungetrackten Account, 7TV-GraphQL-Stub.
- Neu: Kanalseite **ohne** aktives Set (`mockWorkspace` ohne `activeEmoteSetId`) → Import-Einstieg
  sichtbar, Kopier-Knopf nicht, Kanal-/Bestenlisten-Tür deaktiviert → Übertragungsdatei
  wiederhergestellt, Dock sichtbar (AK 33–34).
- Neu: Purge-Protokoll eines anderen Sets **desselben** Kanals → Bestätigung mit Fremd-Hinweis (AK 19).
- Angepasst: `emote-import.e2e.spec.ts:1835-1919` — das fremde Purge-Protokoll wird über
  `targetNotEditable` abgewiesen (Zielliste ohne dieses Set); `vote-ballot.e2e.spec.ts:389`, `:580`,
  `:650` routen die set-zentrische Route und prüfen den neuen Body samt `expectedChannelName`
  (AK 8); jeder Delete-Test mockt die Zielliste (F9).
- Läuft nur, wenn auf `:5151` keine Api lauscht (CLAUDE.md).

---

## 10. Nicht in dieser Spec, mit Grund

- **Ein vorautorisierter Nachmeldeweg** (E20): bewusst ausgelassen, Betreiber-Entscheidung.
- **Eine Meldung, die erst nach einem eigenen 7TV-Read wirkt** (Alternative zu E17): verworfen, F12.
- **Ein `expectedChannelNames[]` für geteilte Sets** (Alternative zu E18): der Client kennt nur
  seinen Ziel-Account; die übrigen Kanäle deckt der periodische Resync (F13).
- **Aufräumen der kanalgebundenen Altform** — Folge-Issue 1 der Spec-200 §21, an sein Tor gebunden.
- **Umbenennung von `IImportTargetOwnershipService`** — Refactoring ohne Vertragswert.
- **#254**, **#255**, **#256** (Punkte 1, 2, 4, 5), **#201** — je eigene Issues.

---

## 11. Live-Verifikation (Regel 16, Betreiber-Handgriff)

Haupt-Checkout, Api per `dotnet run`, `npm start`; danach `dotnet run` beenden, bevor E2E läuft.
Zu belegen im PR-Text mit Zahlen:

1. **Replace in ein ungetracktes Set** (z. B. olafs Set `test`, Konto `olaf_olaf_son`, auf dem der
   Betreiber `editor_of` ist — Plan-230 T10): Picker zeigt das Set `editable` → Schritt 2 bietet
   „Ziel ersetzen" **wählbar** → Vorprüfung im Netzwerktab: kein zweiter Request an
   `/me/emote-set-targets` (Cache) → „Rückweg sichern" → Lauf: REMOVE → ADD benachbart →
   Löschmeldung an `POST /api/seventv/emote-sets/<set>/sync-deleted` mit Body
   `{ sevenTvEmoteIds, expectedChannelName: null }`, Antwort 200 mit `channels: []`,
   `unresolvedChannel: null`, `resyncTriggered: []` → Ergebnisprotokoll heruntergeladen.
2. **Restore aus dieser Datei auf einer anderen Kanalseite** (z. B. `sensitron`): Datei einlesen →
   Bestätigung nennt Set `test`, seine ID, Besitzer, Fremd-Hinweis, keine „nicht aktiv"-Zeile → Lauf
   sendet genau die ADDs der Lücke → Meldung an `…/sync-restored` mit 200, `channels: []` → kein
   Resync-Request → Read-back zeigt die Lücke geschlossen.
3. **Audit (globale Admin-Ansicht):** drei Einträge ohne Kanal für das Set — `syncImported`,
   `syncDeleted`, `syncRestored` — alle „für olaf_olaf_son", `TargetId` = Set-ID.
4. **Purge-Protokoll mit nicht-aktivem Set des eigenen Kanals:** Set-Wechsel im 7TV-Web, Resync
   abwarten, altes Protokoll auf der Seite dieses Kanals einlesen → Bestätigung mit „nicht
   aktiv"-Zeile und Fremd-Hinweis → Lauf → Meldung 200 mit `channels: []`, `expectedChannelName:
   null` → Client-Resync des Kanals ausgelöst → Mitgliederliste des nicht-aktiven Sets zeigt die
   Emotes.
5. **Mismatch erzwingen:** Delete von einem Emote auf der eigenen Seite, dann **vor** der Meldung
   (Netzwerk pausieren oder Worker anhalten, damit `ActiveEmoteSetId` hinkt) im 7TV-Web das aktive
   Set wechseln → Meldung antwortet `unresolvedChannel: { <kanal>, 'activeSetDiffers' }`, Dock
   `partial` mit Grund, Papier-Eintrag mit `unresolved*`-Feldern, `resyncTriggered: [<kanal>]`,
   Kanal danach synchron.
6. **Negativprobe Rechte:** dieselbe Datei als Konto ohne `editor_of` auf olafs Konto (Zweitkonto)
   einlesen → `targetNotEditable`, kein Lauf, kein Request an 7TV; Delete-Versuch auf einer Seite,
   deren Set das Zweitkonto nicht bearbeitet → Abbruchnotiz statt Bestätigung.
7. **Aktives Set des eigenen Kanals** (Regressionsprobe): Delete von zwei Emotes → Meldung mit
   `channels: [{ <kanal>, archivedCount: 2 }]`, `resyncTriggered: [<kanal>]`, `channel.synced` im
   SSE-Stream, Raster aktualisiert; „Wiederherstellen" aus dem Dock → `restoredCount: 2`; Audit des
   Kanals zeigt beide Einträge **mit** Kanal und je einen `channel.resync`.
8. **Altform:** ein `curl` mit dem Session-Cookie an `POST /api/channels/<kanal>/emotes/sync-deleted`
   mit `{ "emoteIds": [<guid>] }` → 200 in alter Form, Zeile in Postgres unverändert, Audit-Eintrag
   mit `legacyBodyForm: true`, Log-Zeile „legacy body form", ein `channel.resync`-Eintrag;
   ein zweiter `curl` binnen 60 s → kein zweiter `channel.resync`.
9. **Seite ohne aktives Set:** ein frisch gejointer Kanal vor dem ersten Sync → Import-Einstieg
   sichtbar, Kopier-Türen deaktiviert, Übertragungsdatei aus Punkt 1 wiederhergestellt, Dock sichtbar.

---

## 12. DECISIONS-Einträge und Doku-Pflege (Regel 3)

### 12.1 Vier Einträge, die der Plan im jeweils ersten betroffenen Commit liefert

1. **„Die Datei bestimmt das Ziel eines Restore"** — E1/E2/E21/E22: kein Kanal- und
   Set-Vergleich gegen die Seite mehr; Zielprüfung über die Zielliste mit `editable`; persönliche
   Sets ausgeschlossen; Hinweis beim Set-Vergleich; Einstieg ohne gewähltes Set; F1 (es gab nie ein
   Protokoll ohne Set-ID) ausdrücklich festgehalten.
2. **„Delete, Restore und Replace-Löschung melden set-zentrisch — Meldung + Resync"** —
   E3/E4/E9/E17/E18/E24: die beiden Routen, Leiter, Antwortform, erwarteter Kanal und Mismatch,
   Audit je Kanal plus Papier, Live-Event, Backend-Resync unter dem Cooldown; **ein eigener Absatz
   zum Restrisiko** (F12: Meldung wirkt sofort, Rechte bis 10 min alt, Reichweite Zeilen eines
   Kanals, Heilung binnen Cooldown/Tick, Altform war schwächer; F13: zweiter Kanal eines geteilten
   Sets bleibt bis zum periodischen Resync unentdeckt); die kanalgebundene set-scoped Form entfällt
   (nie in Produktion); die Guid-Altform bleibt bis zum E3-Tor, aber nur als Audit + Resync; #224
   damit geschlossen. **Ergänzung nach der Live-Verifikation (Nachtrag N1, N3):** der Absatz
   „Audit" erhält die Besitzerkanal-Regel für den Papier-Eintrag, der Absatz „Live event and
   resync" den Client-Fallback bei endgültig gescheiterter Meldung — im selben Commit wie die
   jeweilige Umsetzung, s. N1/N3.
3. **„Wer melden darf, entscheidet die 7TV-Bearbeitungsberechtigung — in Liste und Meldung
   dieselbe Regel"** — E5/E19/E20/5.7: Mod-Team mit eigenen Rechten (Betreiber), 403 nur bei
   echtem Entzug; `editable` aus derselben reinen Funktion; F16 als bewusste Asymmetrie; kein
   vorautorisierter Nachmeldeweg.
4. **„Die Replace-Sperre für ungetrackte Ziele fällt"** — löst den Absatz „Replace is **only**
   offered for a tracked target" im Eintrag vom 2026-09-23 (DECISIONS `:535-537`) und den Satz
   „There is no restore into an untracked set" (`:696-699`) ab; die Pflicht-Rückweg-Datei bleibt;
   die Vorprüfung vor Delete und Replace kommt hinzu.

Alle vier englisch (Projektsprache seit #152).

### 12.2 Bestehende Dokumente, die im selben Vorgang wandern

- `docs/Architectur.md:68`, `:133`, `:154`, `:202` (F11) — inklusive der neuen Aussage, dass die
  Altform keine Zeilen mehr ändert.
- `src/EmotePurge.Api/Auth/UsageStatsAccessAuthorizationFilter.cs:6-17` (Kommentar).
- `docs/UI-Designsprache.md` §7.3 (Datei-Zweig, Zielprüfung, Einstieg ohne Set, vierte
  Einlesesorte bleibt) und der Dock-Abschnitt (Restore-Dock als eigene Section).
- `docs/superpowers/specs/2026-09-20-emote-sets-200-spec.md`: **kein** Umbau; ein Nachtrag am Ende
  („Restore pro Set, 2026-09-24"), der 6.2 (Zielliste um `editable`/IDs erweitert), 6.6 und 8.3
  (der laute Reload der Mitgliederliste hat seit Nachtrag N2 einen dritten Anlass: das Settle eines
  eigenen Laufs in das gewählte nicht-aktive Set) auf diese Spec zeigt, wie es die Nachträge 32–39
  für frühere Runden tun. F7 dort verweist noch auf 6.6
  (die in Plan-230 T9 vorgesehene Korrektur auf 6.7 ist nicht gelandet, `:230`); der Nachtrag setzt
  den Verweis auf 6.7 und auf diese Spec.
- `docs/Feature-Ideen-2026-08-01.md`: keine Idee betroffen.
- Issues: #253 schließt mit dem Merge in den Epic-Branch (von Hand, wie alle Kind-Issues); #224
  wird mit Verweis auf 5.5/6.4 geschlossen; #256 verliert Punkt 3; das Epic #200 bekommt die Zeile.

---

## 13. Entschiedene Punkte (Betreiber, 2026-09-24)

Die sieben offenen Punkte der ersten Fassung, je mit der Entscheidung und der Stelle, an der sie
jetzt steht:

| # | Punkt | Entscheidung | Wo |
|---|---|---|---|
| 1 | „Alte Purge-Datei ohne Set-ID" existiert nicht | Dritte Klasse fällt ersatzlos; Hinweis = bestehende „nicht aktiv"-Zeile | E1, F1, 4.1 |
| 2 | Signatur mit Besitzer-Feldern, Antwort mit `channels[]` | Angenommen — um `expectedChannelName`/`unresolvedChannel`/`resyncTriggered` erweitert (H1, H2) | E7, E9, 5.2, 5.3 |
| 3 | `resetIfChannelChanged` gegen `hostChannelName` | Angenommen | E13, 4.4 Punkt 13 |
| 4 | Rechte-Änderung; Moderator mit fremdem Token | Angenommen; Mod-Team hat eigene Rechte, 403 nur bei echtem Entzug | E5, F4, DECISIONS 3 |
| 5 | Resync-Regel E12 | Angenommen — mit dem Backend-Resync aus H1 verschränkt | E12, E17, F15 |
| 6 | Persönliches Set abgewiesen | Angenommen | E11 |
| 7 | Zielprüfung im `FileImportStep` | Angenommen | E10 |

---

## 14. Nachtrag: Adversariale Zweitmeinung (Codex Sol, gpt-6-sol, 2026-09-24)

Sechs Befunde, alle vom Betreiber angenommen und eingearbeitet.

| # | Schwere | Befund | Lösung | Wo |
|---|---|---|---|---|
| H1 | high | Gecachte Editor-Rechte erlauben Zeilenänderungen ohne 7TV-Beleg, bis zu 10 min nach Rechteentzug | **Meldung + Resync:** die Meldung wirkt weiter sofort (gecachte Rechte, strenger als die alte Kanalrolle); jede Meldung, die einen Kanal trifft, stößt dessen Resync unter dem 60-s-Cooldown an; je Kanal und Meldung höchstens einer, verschränkt mit dem Frontend-Resync über `resyncTriggered`; Restrisiko als Falle und DECISIONS-Absatz | E17, E12, F12, F15, 5.1 Stufe 7, 6.4 |
| H2 | high | Nur-Papier-Antworten verbergen einen getrackten Kanal, dessen `ActiveEmoteSetId` hinkt oder der bei einem geteilten Set fehlt — Klasse #224 | Der Client nennt den erwarteten Kanal; fehlt der Treffer, antwortet der Service mit `unresolvedChannel` + Grund, zählt nichts, nennt Kanal und IDs im Papier-Eintrag, stößt den Resync an; Frontend `partial`/`channelMismatch`; der zweite Kanal eines geteilten Sets bleibt benannter Rest | E18, F13, 5.2 Schritt 3, 5.3, 5.5, 7, AK 28 |
| H3 | high | Zielprüfung lockerer als die Besitzprüfung (Besitzer-ID nicht auf dem Draht); Delete/Replace ohne Vorprüfung | Die Zielliste trägt `editable` aus **derselben** reinen Funktion wie die Besitzprüfung, plus beide IDs; eine gemeinsame Vorprüfung (`resolveEditableSet`, 60-s-Client-Kopie, ein Permit) vor **jeder** ersten Mutation; Grund im Dock bei späterem Entzug; kein vorautorisierter Nachmeldeweg (bewusst ausgelassen, Mod-Team hat eigene Rechte); eine benannte Asymmetrie (F16, strenger) | E19, E20, E23, F16, F17, 5.8, 6.2, 4.5–4.6 |
| H4 | high | Die Guid-Altform kann nach einem Set-Wechsel den falschen Zeilenstand schreiben; ihr Audit hat keine Set-ID | Altform bleibt bis zum E3-Tor, aber nur Audit + Resync — keine Zeilenänderung; Eintrag mit `legacyBodyForm: true`; Antwort mit gefundener Zahl, damit der alte Tab nicht `partial` liest; geprüft: der Eintrag behauptet nichts Falsches, er protokolliert einen Bericht | E4, E24, F14, 5.6, AK 23 |
| M5 | medium | Der Hinweis „Ziel ≠ Seite" verglich Kanäle statt Sets | Hinweis beim Vergleich mit der gewählten Set-ID der Seite; Bestätigung zeigt die aufgelöste Set-ID | E21, 4.3, 6.1, 6.3, AK 19, 35 |
| M6 | medium | Kein Restore-Einstieg auf Seiten ohne gewähltes Set | `ImportTrigger` verlässt das Set-Gate, Kopier-Türen ohne Set deaktiviert, Restore-Dock als eigene Section außerhalb des Gates; kein neuer Button | E22, 4.1, 6.6, AK 33–34 |

---

## 15. Was nach der zweiten Fassung offen bleibt

Nichts, das ein Task vor Beginn braucht. Drei Stellen, an denen ein Betreiber-Veto die Spec an
genau einer Stelle änderte:

1. **E24 — die Antwort der Altform** nennt die *gefundenen* Guids als `archivedCount`, obwohl
   nichts archiviert wurde, damit der alte Tab nicht `partial` zeigt. Alternative: `0`, dann zeigt
   jeder alte Tab bis zum Reload eine Sync-Warnung, die der Resync sofort gegenstandslos macht.
2. **E22 — die `RestoreProgressSection`** ist ein Umzug von etwa dreißig Template-Zeilen aus dem
   `MassDeletePanel`. Alternative: das Panel auch ohne Set mounten (mit nullbarem `setId` und
   versteckten Delete-Teilen) — weniger Dateien, mehr Bedingungen in einem ohnehin großen Panel.
3. **F16 — ein Set ohne `owner.id`** ist im Frontend nicht bearbeitbar, im Backend nach Lookup
   vielleicht doch. Alternative: auch das Backend lehnt es ab — das änderte das K2-Verhalten von
   `sync-imported` und ist deshalb nicht Teil dieser Spec.

---

## 16. Dateireferenz

**Backend:** `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` (zwei Routen in `emoteSetGroup`,
Live-Event, Resync-Stufe, Zielliste mit `editable`/IDs) · `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs`
(Leiter zurück auf Guid-Form, Resync-Stufe für die Altform, Records) ·
`src/EmotePurge.Api/Validation/ApiErrorCodes.cs` (`EmoteSetIdEmpty` entfällt) ·
`src/EmotePurge.Api/Auth/UsageStatsAccessAuthorizationFilter.cs` (Kommentar) ·
`src/EmotePurge.Core/Services/IEmoteService.cs` (zwei Methoden, DTO-Familien; set-scoped
Überladungen und `TargetIsActiveSetOfChannel` entfallen; Altform-Doku) ·
`src/EmotePurge.Core/Services/…` (reine `editable`-Funktion) ·
`src/EmotePurge.Infrastructure/Services/EmoteService.cs` (Konstruktor `IExcludedChannelFilter`,
Implementierung, Altform ohne Zeilenänderung) · `ImportTargetOwnershipService.cs` (nutzt die reine
Funktion) · `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` (unverändert, sofern die
DI den neuen Konstruktorparameter auflöst).

**Frontend:** `web/src/app/shared/export/purge-run-export.ts`, `transfer-run-export.ts` (Parser
ohne `expected`) · `web/src/app/shared/seven-tv/file-import-step.ts` (Zielprüfung,
`ResolvedRestoreTarget`, `hostSelectedSetId`) · `import-source-dialog.ts` (Bindung, nullbares
`setId`, deaktivierte Türen) · `import-trigger.ts` (nullbares `setId`), `mass-delete-panel.ts`
(Vorprüfung, Restore-Aufrufer, Abgabe der Restore-Blöcke) · `restore-progress-section.ts` (neu) ·
`restore-flow.ts`, `restore-confirm-dialog.ts` · `web/src/app/core/seven-tv/seven-tv-restore.service.ts`,
`seven-tv-delete.service.ts`, `seven-tv-import.service.ts` (Meldungen, erwarteter Kanal,
Laufdatensatz, Grund, Wächter) · `seven-tv-emote-set.service.ts` (`resolveEditableSet`,
Client-Kopie, zwei Client-Methoden, Antworttypen) · `seven-tv-emote-set.model.ts`
(`editable`, IDs) · `web/src/app/core/emotes/emote-admin.service.ts` (zwei Methoden entfallen) ·
`web/src/app/shared/seven-tv/conflict-resolution.ts`, `import-conflict-resolution-step.ts`,
`import-confirm-dialog.ts` (Regel 7) · `import-flow.ts` (Vorprüfung bei Replace) ·
`import-target-choices.ts` (`notEditable`) · `run-progress-panel.ts` (`syncReportReason`) ·
`web/src/app/features/usage-stats/usage-stats-page.html` (Trigger-Gate, Section) ·
`web/src/app/features/voting/vote-session-detail-page.ts` (Section) · `web/src/app/core/i18n/api-error.ts`,
`web/public/i18n/de.json`, `en.json` (Schlüssel) ·
`web/src/app/features/channel-workspace/channel-workspace-layout.ts` (unverändert im Aufruf).

**Tests:** 9.1–9.4. **Doku:** 12.1–12.2.

---

## 17. Rückweg

Kein Schema, keine Migration. Backend und Frontend gehen **zusammen** zurück: ein Frontend mit
set-zentrischen Meldungen gegen ein Backend ohne die Routen verlöre jede Papierspur (404 nach der
Mutation), ein Backend mit den Routen ohne Frontend-Aufrufer ist harmlos. Die kanalgebundene
Guid-Altform kehrt mit dem Revert zur zeilenändernden Form zurück — auch das ist harmlos, es ist der
heutige Produktionsstand. Bereits geschriebene Papier-Einträge ohne Kanal und Einträge mit
`legacyBodyForm`/`unresolved*` bleiben lesbar — die Audit-Ansicht kennt die Form seit K2 und
ignoriert unbekannte Detailfelder. Eine heruntergeladene Rückweg-Datei eines Replace in ein
ungetracktes Set bleibt nach einem Revert als JSON lesbar, aber **nicht mehr einlesbar** (der
revertierte Parser weist sie mit `wrongChannel` ab); wer revertiert, revertiert deshalb nicht
zwischen einem solchen Replace und seinem Restore — dieselbe Regel wie in Plan-230 §8.

---

## 18. Nachtrag nach der Live-Verifikation (2026-09-26)

Die Live-Verifikation vom 2026-09-25 (T12, Konto `olaf_olaf_son`, Branch @ `d4148057`, PR #270)
bestand die Punkte 1, 2, 4, 5, 7, 8 und 9 und brachte drei Befunde (F1, B2, B3); dazu kommt der
Codex-Befund C1 aus dem Review, den der Schiedsspruch als echte Lücke in 6.4 eingeordnet hatte. Der
Betreiber hat alle vier am 2026-09-26 entschieden; dieser Nachtrag bringt die Entscheidungen in die
Spec. Die betroffenen Stellen oben tragen den Verweis „(Nachtrag N…)". Was hier als **Festlegung**
markiert ist, ist eine Präzisierung dieses Nachtrags, keine Betreiber-Entscheidung, und kann
einzeln zurückgenommen werden.

### N1 — C1: Fallback-Resync bei endgültig gescheiterter Meldung

**Befund.** Codex C1 (P2): ein Restore in das **aktive** Set eines getrackten Kanals, dessen
`sync-restored` endgültig scheitert (z. B. 503 an der Besitzprüfung), löst **keinen** Resync aus —
das Backend erreicht Stufe 7 nicht, und der Client überspringt seinen eigenen, weil
`resyncChannelName` für ein aktives Set `null` ist (`seven-tv-restore.service.ts`,
`resyncAfterReport`). Die Zeilen blieben bis zum periodischen Resync (≤ 60 s) oder einem manuellen
Retry, wie sie sind. 6.4 setzte den Backend-Resync stillschweigend voraus.

**Entscheidung (Betreiber).** Scheitert die Meldung endgültig — nach den Retries, mit 403, 404, 503,
429 oder Netzfehler, also jeder Ausgang `failed` — gilt der Backend-Resync als **nicht** ausgelöst.
Der Client stößt dann selbst `resync(resyncChannelName ?? expectedChannelName)` an, sofern einer
der beiden Werte gesetzt ist; Doppelte fängt der 60-s-Cooldown (F15). AK 21 und AK 27 bleiben
gültig — sie beschreiben den Erfolgsfall mit Antwort.

**Prüfung an Delete und Import.**

| Dienst | Lücke? | Befund am Code |
|---|---|---|
| Restore | **ja** | `resyncAfterReport` kehrt bei `resyncChannelName === null` ohne Request zurück; auf dem Fehlerpfad kommt es mit leerer Liste dort an (`:391-398`) |
| Delete | **ja** | `SevenTvDeleteService` hat keinen Resync-Pfad (`reportDeleted`, „No resync of its own"); für das aktive Set der Seite (`expectedChannelName` gesetzt) bleibt bei gescheiterter Meldung nichts, was die Zeilen zieht |
| Import (Replace-Löschmeldung) | **nein** | `sendFollowUp` ruft `reportRemoved` mit `afterReport`, das auf dem Fehlerpfad mit leerer Liste läuft, und `triggerResync` feuert dann für ein getracktes aktives Ziel wie ohne Löschmeldung (`seven-tv-import.service.ts:671-681`). Die Präzisierung wird in 6.5 nur ausdrücklich gemacht |

**Vertrag.**

- **Restore (6.4):** endet die **erste** Meldung eines Laufs nach den Retries in `failed`, läuft
  `channelService.resync(resyncChannelName ?? expectedChannelName)`, wenn der Wert nicht `null`
  ist. `resyncTrigger` durchläuft `pending` → `succeeded` | `cooldown` (429) | `failed`, die
  Resync-Zeile im Dock wie heute. Für ein ungetracktes Ziel (beide `null`) weiterhin kein Request
  und keine Zeile. Der Erfolgsfall (Antwort mit `resyncTriggered`) bleibt exakt E12.
- **Delete (6.5):** unter derselben Bedingung `channelService.resync(expectedChannelName)`, wenn
  nicht `null`; der Delete kennt keinen `resyncChannelName`, für ein nicht-aktives oder
  ungetracktes Set also kein Fallback (dort hätte auch das Backend nichts resynct). **Festlegung:**
  das Delete-Dock zeigt dafür keine eigene Resync-Zeile — es hatte nie eine, die
  `syncFailed`-Notiz steht bereits, und die sichtbare Wirkung ist das `channel.synced` des Resync
  auf dem Raster. Ein 429 oder ein Fehler des Fallbacks bleibt damit unsichtbar; das ist bewusst,
  weil der periodische Resync denselben Kanal binnen einer Minute ohnehin zieht.
- **Import (6.5):** keine Verhaltensänderung; 6.5 sagt jetzt ausdrücklich, dass eine endgültig
  gescheiterte Löschmeldung als „`resyncTriggered` leer" gilt.
- **Festlegung:** der Fallback läuft nur nach der **ersten** Meldung eines Laufs, nie nach einem
  manuellen „Erneut melden" — dasselbe Muster wie `afterReport` heute („the first report only,
  never a manual retry"). Ein Retry, der gelingt, bringt sein eigenes `resyncTriggered` mit; ein
  Retry, der wieder scheitert, liegt mit hoher Wahrscheinlichkeit jenseits des nächsten
  Worker-Ticks.
- Nicht betroffen: `partial` (`channelMismatch`, `shortfall`) — das Backend wurde erreicht und
  Stufe 7 ist gelaufen.

**Grenzfälle.**

- **403 (`forbidden`):** der Fallback-Resync läuft unter demselben Konto gegen `POST
  /api/channels/{c}/resync` (`UsageStatsAccessAuthorizationFilter`, 7TV-Editor eingeschlossen).
  Fehlt dem Konto auch die Kanalrolle, antwortet der Resync 403 → `resyncTrigger: 'failed'`; die
  Zeilen heilt der periodische Resync. Kein Nachmeldeweg (E20).
- **404 (`setNotFound`):** das Set ist bei 7TV weg; der Resync des Kanals ist trotzdem richtig,
  weil der Kanal dann ein anderes aktives Set hat oder keines.
- **Cooldown:** lief binnen 60 s ein Resync (Import-Resync desselben Laufs, Altform-Tab,
  Vorgänger-Lauf), antwortet der Fallback 429 → `cooldown`, „kommt ohnehin" (F15). Kein zweiter
  Versuch.
- **Superseded run:** wie `afterReport` heute läuft der Fallback auch für einen Lauf, den `reset`
  inzwischen abgelöst hat; nur der Zustand wird über `applyIfCurrent` geschützt.

**Betroffene Abschnitte.** E12, 4.4 Nr. 11, 6.4, 6.5, 7 (neue Zeile „Meldung scheitert
endgültig"), AK 21/27 (Klammerzusatz), DECISIONS-Eintrag 2 vom 2026-09-25 (Absatz „Live event and
resync", ein Satz zum Client-Fallback — schreibt der Implementer im selben Commit).

**Tests.** `seven-tv-restore.service.spec.ts`: 503 nach den Retries mit `expectedChannelName`
gesetzt ⇒ genau ein `POST /api/channels/{c}/resync`; mit `resyncChannelName` gesetzt ⇒ Resync
dieses Kanals (wie heute); beide `null` ⇒ keiner; manueller Retry nach `failed` ⇒ kein zweiter.
`seven-tv-delete.service.spec.ts`: dasselbe für `expectedChannelName`. `seven-tv-import.service.spec.ts`:
der bestehende Fall „Löschmeldung scheitert ⇒ Resync läuft" bleibt.

**AK 36.** Scheitert die erste Meldung eines Restore- oder Delete-Laufs nach den automatischen
Retries mit einem HTTP-Status oder Netzfehler (`syncReport === 'failed'`, jeder Grund), löst der
Dienst genau einen `POST /api/channels/{kanal}/resync` aus — beim Restore für
`resyncChannelName ?? expectedChannelName`, beim Delete für `expectedChannelName` — und keinen,
wenn der jeweilige Wert `null` ist; ein 429 darauf endet als `cooldown`, nicht als Fehler; ein
manuelles „Erneut melden" löst keinen weiteren aus. Die Replace-Löschmeldung des Imports verhält
sich bei endgültigem Scheitern so, als wäre `resyncTriggered` leer (der Import-Resync läuft).

### N2 — F1: Mitgliederliste eines nicht-aktiven Sets nach einem Lauf

**Befund (T12, Punkt 4).** Nach einem Restore in das nicht-aktive Set `tttt` (Seite olaf, Ansicht
`test`) zeigte die Ansicht von `tttt` 33 s später 78 statt 79 Emotes; erst nach Ablauf bzw.
„Aktualisieren" stimmte sie. Ursache: die Ansicht eines nicht-aktiven Sets liest
`GET /api/seventv/channels/{c}/emotes?emoteSetId=…` ohne `refresh`, also aus dem 60-s-Cache des
Hardening-Decorators, und genau diese Route hatte die Slot-Vorschau der Restore-Bestätigung (4.3
Nr. 8) kurz vorher befüllt. Weder der Client-Resync noch `channel.synced` umgehen diesen Cache: die
Seite setzt `refresh: true` nur in `reloadLiveMembers()`, und das nur für das **gerade gewählte**
Set beim Eintreffen von `channel.synced` (`usage-stats-page.ts:651-682`, `:2817-2826`). Die Zeile
in Abschnitt 7 („Mitgliederliste lädt neu, wenn der Nutzer dieses Set wählt") war damit falsch —
sie ist oben korrigiert. Dasselbe gilt für Delete und Import in ein nicht-aktives Set.

**Entscheidung (Betreiber).** Nach einem abgeschlossenen Lauf (Restore oder Delete, gegebenenfalls
Replace) in ein nicht-aktives Set, das die Seite gerade als gewähltes Set hat, lädt die Seite die
Mitgliederliste mit `refresh` neu.

**Vertrag.**

- **Auslöser ist das Settle des Laufs** im jeweiligen Dienst, nicht die Meldung und nicht ein
  Resync: Restore — `restoreService.run()` wechselt auf einen Datensatz mit `result !== null`;
  Delete — `deleteService.lastRun()` wird gesetzt; Import — `importService.run()` wechselt auf
  `settlement === 'settled'`. Die 7TV-Mutationen sind zu diesem Zeitpunkt abgeschlossen; auf die
  Meldung zu warten hieße, bei gescheiterter Meldung nie zu laden.
- **Bedingung:** der Lauf hat mindestens eine erfolgreiche Zeile (`doneKeys.length > 0` bzw. eine
  `done`-Zeile), und sein Ziel-Set (`targetSetId` / `setId`) ist das gewählte Set der Seite
  (`selectedEmoteSetId()`) **und** nicht ihr aktives (`activeEmoteSetId()`). Dann ruft die Seite
  `reloadLiveMembers()` — den bestehenden lauten Reload mit `refresh: true`. Für das aktive Set
  bleibt der Weg `channel.synced` (5.4), für ein Set, das die Seite nicht zeigt, geschieht beim
  Settle nichts Sichtbares.
- **Festlegung (Vormerkung):** ist das Ziel-Set nicht-aktiv, aber gerade **nicht** gewählt — der
  Fall, den T12 tatsächlich beobachtet hat (Ansicht `test`, Ziel `tttt`, Wechsel 33 s später) —
  merkt sich die Seite die Ziel-Set-ID, und der **nächste** Ladevorgang der Mitgliederliste genau
  dieses Sets trägt `refresh: true`. Die Vormerkung wird vom nächsten Ladevorgang irgendeines Sets
  und von einem Kanalwechsel verworfen (sie lebt in `liveMembersRefreshFor`, das der Stream heute
  schon liest und leert; eine Set-ID ist global eindeutig, ein Kanalvergleich ist nicht nötig).
  Ohne diese Vormerkung bliebe der beobachtete Fall F1 offen, obwohl die Zeile in Abschnitt 7 ihn
  verspricht; mit ihr wird die korrigierte Zeile in beiden Lesarten wahr.
- **Festlegung (Import):** der Auslöser gilt für **jeden** Import-Lauf mit mindestens einer
  `done`-Zeile, nicht nur für einen mit Replace-Zeilen — ein ADD verändert die Mitgliederliste
  ebenso wie ein REMOVE, und eine Ausnahme für Add-only-Läufe wäre eine Sonderregel ohne Nutzen.
  Betrifft den Seed-Fall aus T12 (Import aus einem Kanal in das gewählte nicht-aktive Set).
- Kein neuer Request außerhalb dieser Fälle; die Regel von Spec-200 8.3 („ein lauter Reload
  bezieht die Liste neu, ein stiller nie") bleibt — das Settle eines eigenen Laufs ist ein lauter
  Anlass, weil der Nutzer ihn selbst ausgelöst hat.
- **Festlegung (Tragweite, F7):** für Delete und Restore ins gewählte nicht-aktive Set lud die
  Seite schon vor diesem Nachtrag laut neu — `MassDeletePanel` ruft bei einem terminalen `deleted`
  über `onDeleted()` (Zweig `run.setId !== shownSetId() || isNonActiveView()`) und bei einem
  gescheiterten Bericht über `onReloadRequested()` jeweils `refresh()` → `reloadLiveMembers()` auf.
  Die eigentliche Neuerung von N2 ist deshalb nicht der Reload selbst für Delete und Restore,
  sondern (a) der Import-Pfad, der vorher keinen hatte, und (b) die Vormerkung für ein Ziel-Set,
  das die Seite beim Settle gerade nicht zeigt.

**Grenzfälle.**

- **Restore aus dem Delete-Dock (E16):** Delete und Restore treffen dasselbe Set; jeder Lauf löst
  beim Settle einen Reload aus — zwei Requests, beide gerechtfertigt.
- **Delete ins gewählte nicht-aktive Set (F7):** löst dadurch zwei `refresh=true`-GETs auf die
  Mitgliederroute aus — den bestehenden aus `MassDeletePanel` (`onDeleted`/`onReloadRequested` →
  `refresh()`) und den neuen aus dem hier beschriebenen Settle-Effekt. Akzeptiert: AK 37 hält beim
  Settle exakt einen Request für den hier beschriebenen Weg fest, nicht für die Seite insgesamt;
  der zweite ist redundant, aber harmlos.
- **Settle während die Mitgliederanfrage des gewählten nicht-aktiven Sets noch läuft (Schiedsspruch
  zum zurückgestellten N2-Punkt, kein Code geändert):** Auslöser sind ein Aktualisieren-Klick
  mitten im Lauf, ein später Rückwechsel auf das Set nach mehr als 60 s, eine Rückkehr auf die
  Seite während eines Laufs oder ein fremdes `channel.synced`. `reloadLiveMembers()` liefert dann
  `false` (die Anfrage läuft bereits) und die Vormerkung fällt ersatzlos weg — die laufende, vor
  bzw. während der Mutation gestellte Antwort füllt sowohl die Ansicht als auch beide 60-s-Caches
  mit einem Stand, der die Mutation noch nicht zeigt. Heilung: der Aktualisieren-Knopf, der
  Cache-Ablauf, und beim Restore zusätzlich das `channel.synced` des E12-Resync; offen bleibt es
  damit nur für Delete und Import ins gewählte nicht-aktive Set. AK 37 bleibt wie formuliert
  stehen. Benannter Rest, bewusst nicht behoben — ein Millisekundenfenster, binnen 60 s vom Nutzer
  selbst heilbar, s. `usage-stats-page.ts` (`reloadLiveMembers`, Doku-Kommentar dort).
- **Lauf ohne erfolgreiche Zeile:** nichts hat sich geändert, kein Reload (und keine Meldung).
- **Ziel ist das aktive Set der Seite:** kein Reload über diesen Weg; das Raster folgt
  `channel.synced` wie bisher.
- **Set-Wechsel während des Laufs:** die Bedingung wird beim Settle gegen den dann gewählten Wert
  geprüft; wer während des Laufs auf das Ziel-Set gewechselt hat, bekommt den Reload; wer davon
  weg gewechselt hat, die Vormerkung.
- **7TVs eigener REST-Cache** (SevenTV#81) kann auch mit `refresh=true` kurz veraltet sein; das
  ist die bekannte Grenze der Mitgliederroute und nicht Teil dieses Nachtrags.

**Betroffene Abschnitte.** Abschnitt 7 (Zeile „anderes, nicht-aktives Set des Seitenkanals"
korrigiert; neue Zeile „gewähltes, nicht-aktives Set"), 6.6 (Seite), Spec-200 8.3 (nur im
Nachtrag dort erwähnt, s. 12.2). Kein eigener DECISIONS-Eintrag: es ist eine Korrektur des
Seitenverhaltens innerhalb des bestehenden Reload-Vertrags, keine neue Konvention.

**Tests.** `usage-stats-page.spec.ts`: Settle eines Restore-/Delete-/Import-Laufs mit dem gewählten
nicht-aktiven Set als Ziel ⇒ genau ein Request mit `refresh=true` auf die Mitgliederroute; Ziel
nicht gewählt ⇒ kein Request beim Settle, der nächste Ladevorgang dieses Sets trägt `refresh=true`,
der eines anderen Sets nicht; Ziel = aktives Set ⇒ kein Request; Lauf ohne `done`-Zeile ⇒ keiner.

**AK 37.** Settelt ein Restore-, Delete- oder Import-Lauf mit mindestens einer erfolgreichen Zeile,
dessen Ziel-Set das gewählte, nicht-aktive Set der Seite ist, geht genau ein
`GET /api/seventv/channels/{kanal}/emotes?emoteSetId=<ziel>&refresh=true`, und die Ansicht zeigt
danach den neuen Stand ohne Warten auf den Cache-Ablauf. Ist das Ziel-Set nicht-aktiv, aber nicht
gewählt, geht beim Settle kein Request, und der nächste Ladevorgang genau dieses Sets trägt
`refresh=true`; ein Ladevorgang eines anderen Sets nicht. Ist das Ziel das aktive Set, geht über
diesen Weg kein Request.

### N3 — B2: Kanal im Papier-Eintrag

**Befund (T12, Punkt 3 und B2).** Die set-zentrische Meldung schreibt den Papier-Eintrag immer mit
`ChannelName = null` (5.5, erste Fassung). Er steht dann nur in der globalen Admin-Ansicht und fehlt
in `GET /api/channels/{c}/audit-log`, weil `AuditLogQueryService.ApplyFilter` exakt auf
`ChannelName` filtert (`:109-115`). Folgen: (a) Delete und Restore in einem nicht-aktiven Set des
**eigenen getrackten** Kanals verschwinden für Broadcaster und Mods aus der Kanal-Ansicht — vor #253
trugen dieselben Flows den Kanal (Einträge 126/128/129 vom 23.09. mit
`targetIsActiveSetOfChannel: false`), das ist eine Regression; (b) in einem Replace-Lauf steht
`syncImported` (kanalgebunden) in der Kanal-Ansicht, das zugehörige `syncDeleted` nur global; (c) der
Mismatch-Eintrag 153 nennt `unresolvedChannelName: olaf_olaf_son`, erscheint in dessen Kanal-Ansicht
aber nicht.

**Entscheidung (Betreiber).** Hat der Ziel-Account (Besitzer des Sets) einen getrackten Kanal, trägt
der Papier-Eintrag dessen Kanal. Nur echte ungetrackte Ziele bleiben ohne Kanal.

**Vertrag.**

1. **„Getrackt" bestimmt der Service über die Twitch-ID des Besitzers** (5.2 Schritt 3a): die
   Kanalzeile mit `TwitchChannelId == ownerTwitchUserId && IsBotActive`, deren `TwitchChannelId`
   nicht auf `Channels:ExcludedChannelIds` steht. Das ist wörtlich die Regel von
   `IChannelService.GetActiveByTwitchChannelIdAsync` (`ChannelService.cs:206-223`) und damit
   dieselbe, nach der die Zielliste `trackedChannelName` liefert (4.2 Nr. 5): was der Client als
   Kanal des Ziels gesehen hat, steht nachher im Eintrag. Über die ID, nicht den Login, weil Logins
   wandern (#44) und die ID unveränderlich ist. Fehlt die Zeile, ist sie `IsBotActive == false` oder
   gesperrt ⇒ kein Besitzerkanal, `ChannelName = null` — ein gesperrter Kanal wird so wenig
   offengelegt wie in Schritt 3 (`notTracked`), weil ein verlassener Kanal genauso aussieht.
2. **Die Twitch-ID des Besitzers liefert die Besitzprüfung**, ohne neuen 7TV-Request:
   `SevenTvEmoteSetOwnershipCheckResult` bekommt `OwnerTwitchUserId` (non-null genau bei `Owner`,
   wie die beiden anderen Felder). Beide Trefferpfade kennen sie schon — das eigene Konto ist
   `actor.TwitchUserId`, ein Grant trägt `SevenTvEditorGrantEntry.TwitchChannelId`; der
   F16-Lookup-Pfad ordnet den gefundenen Besitzer ohnehin einem dieser Accounts zu
   (`evidence.LoginOfAccount`). Die Leiter (5.1 Stufe 4/5) reicht sie an
   `MarkDeletedInSetAsync`/`MarkRestoredInSetAsync` durch (E7, 5.2). `sync-imported`
   (`MarkImportedToSetAsync`) ist **nicht** betroffen: es wird nur für ungetrackte Ziele
   aufgerufen (`reportImported`, 6.5), sein Eintrag ist also ohnehin nur dann kanallos, wenn es
   keinen Kanal gibt.
3. **Was der Papier-Eintrag trägt** (5.5, zwei Zeilen statt einer):
   - **mit Besitzerkanal:** `ChannelName = <Besitzerkanal>`, Details `{ emoteCount, emoteSetId,
     targetIsActiveSetOfChannel, unresolved*? }` — **ohne** `targetOwnerSevenTvUserId`
     und `targetOwnerTwitchLogin`. Der Kanal nennt den Besitzer; die Form ist die der Einträge vor
     #253, `audit-row.ts` rendert „(nicht das aktive Set)" (`:141-142`), wenn das Flag `false` ist,
     und der Eintrag steht in der Kanal-Ansicht.
   - **ohne Besitzerkanal:** unverändert `ChannelName = null`, Details mit beiden
     `targetOwner*`-Feldern, ohne `targetIsActiveSetOfChannel` → „für <ownerLogin>".
   - **Festlegung (Invariante):** ein Eintrag trägt entweder Kanal **und**
     `targetIsActiveSetOfChannel`, oder keinen Kanal **und** die `targetOwner*`-Felder — nie
     beides. Das ist genau die Zweiteilung, die `AuditLogTargetEmoteSet` dokumentiert
     (`IAuditLogQueryService.cs:18-30`) und nach der `audit-row.ts` die Zeile wählt (Besitzer vor
     Aktiv-Flag). Projektion (`ReadTargetEmoteSet`) und Ansicht bleiben **unverändert**; die
     Alternative — beide Feldgruppen schreiben und die Ansicht umsortieren — hätte die Invariante
     gebrochen und #255 vorgegriffen.
   - **Festlegung (Flag-Herkunft, Fix-Welle nach diesem Nachtrag, Befund F1).** `targetIsActiveSetOfChannel`
     ist im Kanal-Zweig nicht hart `false`, sondern spiegelt den gespeicherten Zustand des
     Besitzerkanals: `true` genau dann, wenn der Besitzerkanal selbst einer der Treffer-Kanäle aus
     Schritt 2 ist (sein `ActiveEmoteSetId` ist das gemeldete Set), sonst `false`. Ohne diese
     Ableitung trug ein eigener Kanal-Treffer mit null passenden Zeilen fälschlich `false`, obwohl
     das Set bei ihm aktiv ist (Schritt 5 schreibt ihm ja keinen Kanal-Eintrag, weil der auf einen
     Treffer-Zähler über 0 gated ist) — und ein geteiltes Set, dessen Besitzerkanal Treffer ist,
     während ein zweiter Kanal hinterherhinkt, schrieb einen Kanal-Eintrag mit `true` direkt neben
     einem Papier-Eintrag desselben Kanals mit `false`.
4. **Der Mismatch-Eintrag** folgt derselben Regel und demselben Flag, nicht einer eigenen — und der
   unaufgelöste Kanal ist **nicht immer** der Besitzerkanal (das behauptete die erste Fassung dieses
   Nachtrags fälschlich): `ChannelName` und `targetIsActiveSetOfChannel` kommen wie in Punkt 3 aus
   dem Besitzerkanal (Schritt 3a), `unresolvedChannelName` aus dem tatsächlich unaufgelösten Kanal —
   beide fallen nur zusammen, wenn die meldende Seite die des Besitzers selbst ist. Im klassischen
   Fall (Befund c) hinkt der eigene Kanal des Besitzers hinterher: er ist dann selbst kein Treffer,
   `targetIsActiveSetOfChannel` liest `false`, und `ChannelName` = `unresolvedChannelName`. Bei
   einem geteilten Set, das am Besitzerkanal aktiv ist (ein Treffer, Flag `true`) und an einem
   zweiten getrackten Kanal X hinterherhinkt (dessen `ActiveEmoteSetId` noch das alte Set zeigt),
   sendet die Seite von X `expectedChannelName = X`; Schritt 3 löst X nicht auf → `activeSetDiffers`
   mit `unresolvedChannelName = X`, aber der Papier-Eintrag trägt trotzdem `ChannelName =
   <Besitzerkanal>` mit `targetIsActiveSetOfChannel: true` — der Eintrag nennt den Besitzer, nicht
   X, plus die drei `unresolved*`-Felder zu X. Der Eintrag erscheint in der Kanal-Ansicht des
   Besitzerkanals. Bei `notTracked` ist der Besitzerkanal nach derselben Regel nicht auflösbar, der
   Eintrag bleibt ohne Kanal; `unresolvedChannelName` nennt den Kanal weiterhin (das ist die Eingabe
   des Clients, kein Geheimnis).
5. **Kanal-Einträge (Treffer) und der Auslöser des Papier-Eintrags ändern sich nicht:** der
   Papier-Eintrag entsteht weiterhin genau dann, wenn kein Kanal-Eintrag geschrieben wurde oder
   ein Kanal unaufgelöst blieb. Nur sein `ChannelName` und seine Detailform hängen jetzt vom
   Besitzerkanal ab.

**Grenzfälle.**

- **Nicht-aktives Set des eigenen getrackten Kanals** (T12 Punkte 1, 2, 4, 9): Eintrag mit
  Kanal, in der Kanal-Ansicht sichtbar — die Regression (a) ist damit zurückgenommen, und in
  einem Replace-Lauf stehen `syncImported` und `syncDeleted` wieder nebeneinander (b).
- **Ungetracktes Ziel:** ohne Kanal, „für <ownerLogin>", nur global — wie E6 es immer meinte.
- **Geteiltes Set, Besitzerkanal nicht getroffen** (Set von A aktiv bei X, nicht bei A): nur der
  Kanal-Eintrag für X, kein Papier-Eintrag, nichts in As Kanal-Ansicht. Benannter Rest derselben
  Klasse wie F13 (der Auslöser des Papier-Eintrags bleibt bewusst unverändert; ihn auf „Besitzerkanal
  ohne eigenen Kanal-Eintrag" auszudehnen wäre eine eigene Entscheidung).
- **Besitzerkanal umbenannt, `expectedChannelName` alt:** Schritt 3 verfehlt den Namen
  (`notTracked`), Schritt 3a findet die Zeile über die ID → Eintrag mit neuem Kanalnamen,
  `unresolvedChannelName` alt. Selbstheilend mit der nächsten Zielliste.
- **Gesperrter Besitzerkanal:** kein Kanal im Eintrag, keine Zeile berührt, kein Resync — und
  der Eintrag sieht aus wie der eines verlassenen Kanals (E8, AK 12).
- **Sichtbarkeit:** die Kanal-Ansicht lesen Broadcaster, Mods und Editoren des Kanals
  (`UsageStatsAccessAuthorizationFilter`); ein Editor, der in einem nicht-aktiven Set löscht, war
  dort vor #253 ebenso sichtbar.

**Betroffene Abschnitte.** E6, E7, 4.7 Nr. 23, 5.1 Stufe 4/5, 5.2 (Signatur, Schritt 3a), 5.5
(zwei Zeilen, Invariante), 7 (Mismatch-Zeilen, „anderes nicht-aktives Set", „ungetracktes Ziel",
zwei neue Zeilen), AK 13, 17, 18, 28. **DECISIONS-Eintrag 2 vom 2026-09-25** („Delete, restore and
a replace's removals report per emote set — report plus resync"): der Absatz **„Audit"** wird
ergänzt — der Papier-Eintrag trägt den getrackten Kanal des Besitzer-Accounts (aufgelöst über dessen
Twitch-ID, aktive Zeile, Sperrliste), mit `targetIsActiveSetOfChannel` (Flag-Herkunft: ist dieser
Kanal selbst ein Treffer, s. o.) und ohne `targetOwner*`; nur ohne Besitzerkanal bleibt er kanallos
mit `targetOwner*`; Begründung: die erste Fassung hatte die Einträge nicht-aktiver Sets des eigenen
Kanals aus der Kanal-Ansicht genommen, was vor #253 nicht so war. Den Eintrag schreibt der
Implementer im selben Commit wie die Service-Änderung (Regel 3). `docs/Architectur.md` und
`docs/UI-Designsprache.md` nennen den Papier-Eintrag nicht in dieser Tiefe; keine weitere
Doku-Stelle.

**Tests.** `EmoteServiceTests.cs` (9.2): nicht-aktives Set eines getrackten Kanals ⇒ Papier-Eintrag
mit `ChannelName` = Kanal, `targetIsActiveSetOfChannel: false`, ohne `targetOwner*`; ungetrackt ⇒
`null` mit `targetOwner*`; gesperrter Besitzerkanal (echter `ExcludedChannelFilter`) ⇒ `null`;
inaktiver Besitzerkanal ⇒ `null`; Mismatch `activeSetDiffers` ⇒ Kanal gesetzt **und**
`unresolved*`; Mismatch `notTracked` ⇒ `null` **und** `unresolved*`. Ownership-Tests:
`OwnerTwitchUserId` auf allen drei Trefferpfaden. `SevenTvEmoteSetSyncInSetEndpointTests` (9.1):
die Twitch-ID wird an den Service durchgereicht. Ein Integrationstest über
`AuditLogQueryService.ListAsync` mit `ChannelName`-Filter, der den Papier-Eintrag findet.
**Fix-Welle (F1):** ein Treffer-Besitzerkanal ohne passende Zeile ⇒ Papier-Eintrag mit
`targetIsActiveSetOfChannel: true`; ein geteiltes Set mit Treffer-Besitzerkanal und einem zweiten,
hinterherhinkenden Kanal ⇒ Kanal-Eintrag und Papier-Eintrag desselben Kanals stimmen im Flag
überein (beide `true`), der Papier-Eintrag trägt zusätzlich die drei `unresolved*`-Felder zum
zweiten Kanal.

**AK 38.** Für ein Set, dessen Besitzer-Account einen aktiven, nicht gesperrten Kanal hat, entsteht
der Papier-Eintrag mit `ChannelName` = diesem Kanal und Details
`{ emoteCount, emoteSetId, targetIsActiveSetOfChannel }` ohne `targetOwner*`-Felder, und
`GET /api/channels/{kanal}/audit-log` listet ihn; `targetIsActiveSetOfChannel` ist `true` genau
dann, wenn der Besitzerkanal selbst ein Treffer aus Schritt 2 ist (sein `ActiveEmoteSetId` ist das
gemeldete Set — auch ein Treffer ohne eine einzige passende Zeile), sonst `false`. Für ein Set ohne
solchen Kanal (ungetrackt, verlassen oder gesperrt) entsteht er mit `ChannelName = null` und beiden
`targetOwner*`-Feldern ohne `targetIsActiveSetOfChannel`. Kein Eintrag trägt beide Feldgruppen.

**AK 39.** Ein Mismatch `activeSetDiffers` schreibt den Papier-Eintrag mit dem Besitzerkanal als
`ChannelName` (Schritt 3a) — mit dem unaufgelösten Kanal identisch nur, wenn die meldende Seite die
des Besitzers ist —, `targetIsActiveSetOfChannel` nach derselben Ableitung wie AK 38, und den drei
`unresolved*`-Feldern zum tatsächlich unaufgelösten Kanal; sichtbar in der Kanal-Audit-Ansicht des
Besitzerkanals. Ein Mismatch `notTracked` schreibt ihn ohne Kanal, mit `targetOwner*` und den drei
`unresolved*`-Feldern — für einen gesperrten Kanal byte-gleich mit dem eines verlassenen.

### N4 — B3: kein „Erneut melden" bei `channelMismatch`

**Befund (T12, Punkt 5 und B3).** Bei `partial`/`channelMismatch` bot das Dock „Erneut melden" an.
Ein erneuter Versuch schickt dieselbe Meldung: bei unverändertem Stand ein weiterer Mismatch, ein
weiterer Papier-Eintrag, ein weiterer Resync-Versuch in den Cooldown — und der Resync, der den
Mismatch heilt, läuft nach der ersten Antwort bereits (`resyncTriggered`). Der Retry-Knopf kann in
diesem Zustand nichts verbessern. Der **Wortlaut** der Grundzeile („Rückmeldung fehlgeschlagen …
konnte es nicht vermerken" ist für einen vermerkten Bericht falsch) gehört zu #255 und ist nicht
Teil dieses Nachtrags.

**Entscheidung (Betreiber).** Für `channelMismatch` gibt es keinen Retry-Knopf.

**Vertrag.**

- **Retry-Regel (6.4, 6.5):** „Erneut melden" wird angeboten bei `syncReport === 'failed'` (jeder
  Grund — dort kann ein späterer Versuch gelingen) und bei `partial`/`shortfall`; **nicht** bei
  `partial`/`channelMismatch`. Die Notiz selbst (Titel, Grundzeile) bleibt in allen drei Fällen
  stehen; nur die Aktion fehlt. Gilt für alle drei Docks: `RunProgressPanel` (Delete, Restore) und
  die Notiz der Löschmeldung in `ImportProgressSection` (`retryRemovalReport`).
- **Festlegung (Doppelboden):** die Dienste weisen `retrySyncReport()` bzw. `retryRemovalReport()`
  bei `syncReportReason === 'channelMismatch'` ab, wie sie heute `pending` und einen fehlenden
  Laufdatensatz abweisen — damit der Vertrag am Dienst prüfbar ist und nicht nur am Template.
- `shortfall` behält den Knopf: ob ein Retry dort etwas ändern kann, ist hier nicht entschieden
  und bleibt #255 bzw. einem Folgepunkt überlassen.
- Die Resync-Zeile „wird abgeglichen" (`backendTriggered`) bleibt die Auskunft darüber, dass der
  Stand von selbst kommt.

**Grenzfälle.**

- **Mismatch mit `notTracked`:** ebenfalls kein Knopf — der Kanal ist weg oder gesperrt, eine
  Wiederholung ändert daran nichts; die Notiz sagt, was sie sagt (#255).
- **Ein Retry, der schon lief** (vor diesem Nachtrag, oder per DevTools): das Backend behandelt
  ihn wie heute — Duplikat schlägt Lücke (5.5).
- **`failed` nach einem früheren Mismatch** gibt es nicht: ein Lauf meldet einmal, der Zustand
  ist entweder `partial` oder `failed`.

**Betroffene Abschnitte.** E23, 4.4 Nr. 14, 6.4 (Retry-Regel), 6.5 (Delete, Import), 7 (beide
Mismatch-Zeilen). Kein DECISIONS-Eintrag: eine UI-Regel innerhalb des bestehenden E23-Vertrags.

**Tests.** `run-progress-panel.spec.ts`: `partial`/`channelMismatch` ⇒ Notiz ohne Retry-Knopf;
`partial`/`shortfall` und `failed`/`unavailable` ⇒ mit. `import-progress-section.spec.ts`: dasselbe
für die Löschmeldung. Die drei Dienst-Specs: `retrySyncReport()`/`retryRemovalReport()` bei
`channelMismatch` ⇒ kein Request.

**AK 40.** Steht ein Meldungszustand auf `partial` mit `syncReportReason === 'channelMismatch'`,
zeigt keines der drei Docks einen „Erneut melden"-Knopf, und ein Aufruf von
`retrySyncReport()`/`retryRemovalReport()` löst keinen Request aus; bei `failed` (jeder Grund) und
bei `partial`/`shortfall` ist der Knopf vorhanden und löst genau eine erneute Meldung aus.

### Zusammenfassung der neuen Akzeptanzkriterien

| AK | Nachtrag | Kern |
|---|---|---|
| 36 | N1 | Fallback-Resync des Clients bei endgültig gescheiterter erster Meldung (Restore: `resyncChannelName ?? expectedChannelName`, Delete: `expectedChannelName`; Import unverändert) |
| 37 | N2 | Mitgliederliste des gewählten nicht-aktiven Ziel-Sets lädt beim Settle mit `refresh=true`; nicht gewähltes Ziel: Vormerkung für den nächsten Ladevorgang dieses Sets |
| 38 | N3 | Papier-Eintrag trägt den Besitzerkanal (aktiv, ungesperrt, über Twitch-ID) mit `targetIsActiveSetOfChannel` (Flag = ist der Besitzerkanal selbst Treffer); sonst kanallos mit `targetOwner*`; nie beides |
| 39 | N3 | Mismatch-Eintrag folgt derselben Regel (`activeSetDiffers` mit Kanal, `notTracked` ohne) |
| 40 | N4 | Kein „Erneut melden" bei `channelMismatch`, in allen drei Docks und am Dienst |
