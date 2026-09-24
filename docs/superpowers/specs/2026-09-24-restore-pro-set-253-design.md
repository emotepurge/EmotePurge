# Restore pro Set: Restore ohne Kanalseite, set-zentrische Meldungen, Aufhebung der Replace-Sperre — Spec

**Datum:** 2026-09-24 · **Status:** Entwurf, abschnittsweise vom Betreiber freigegebenes Design ausformuliert, gegen den Code auf `feat/253-restore-per-set` (auf `chore/200-sync-main`, PR #263) belegt · **Issues:** #253 (Hauptissue), **#224** (wird durch diese Spec mit Verweis geschlossen, nicht separat gefixt), #256 Punkt 3 (erledigt sich) · **Epic:** #200 · **Vorgänger:** #230 (PR #251), [Plan-230-Namenskonflikte.md](../../plans/Plan-230-Namenskonflikte.md) T6 „Entfallen", T7b, Abschnitte 7 und 8 · **Formatvorlage:** [2026-09-20-emote-sets-200-spec.md](2026-09-20-emote-sets-200-spec.md) (Abschnitte 6.6, 6.7, 7.2, 8.6, F7) · **Nicht Teil:** #254 (Rückgängigmachen eines geglückten Replace), #255 (Wortlaute)

Diese Spec ist ein Denkwerkzeug des Betreibers und deshalb deutsch; Bezeichner, Routen und
Wire-Felder bleiben englisch. Sie enthält keinen fertigen Code — Verträge, Verhalten, Grenzfälle,
Fallen und prüfbare Akzeptanzkriterien. Zeilenangaben sind am 2026-09-24 gegen den Branch
nachgeprüft. Wo das freigegebene Design vom Code abweicht oder eine Lücke hat, steht das **nicht**
stillschweigend korrigiert im Vertrag, sondern in Abschnitt 13 („Offene Punkte für den Betreiber")
mit Empfehlung; kleine, eindeutige Präzisierungen sind im Vertrag als **Festlegung** markiert.

---

## 0. Auftrag in einem Satz

Ein Nutzer kann **jede** Rückweg-Datei — Purge-Protokoll, Rückweg-Datei (`planned`) oder
Ergebnisprotokoll (`finished`) einer Übertragung — auf **jeder** Kanalseite einlesen; **die Datei
bestimmt das Ziel-Set**, die App prüft nur noch, ob der Nutzer dieses Set bearbeiten darf, und meldet
Löschung wie Wiederherstellung **set-zentrisch** an die Api (`POST
/api/seventv/emote-sets/{emoteSetId}/sync-deleted` und `…/sync-restored`), sodass auch ein
ungetracktes Set einen Rückweg und eine Papierspur hat — womit die Sperre „Ziel ersetzen nur für
getrackte Ziele" aus #230 fällt und #224 gegenstandslos wird.

---

## 1. Ausgangslage — was am Code steht

Alles Folgende ist am Branch nachgeprüft; es ist die Begründung für jede Änderung in den
Abschnitten 4 bis 6.

**Der Restore-Weg ist kanalgebunden, von vorn bis hinten.**

- `ImportTrigger.openDialog` friert `channelName` und `setId` der Seite beim Klick ein und reicht
  sie an `openImportSourceDialog` und weiter an `startRestoreFlow`
  (`web/src/app/shared/seven-tv/import-trigger.ts:164-200`,
  `import-source-dialog.ts:160-164`).
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
  Zeilen 47-105, 274-276; `origin/main:…/IEmoteService.cs:24,29`).
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
(`ImportTargetOwnershipService.cs:54-104`, Doku `IImportTargetOwnershipService.cs:59-79`); der
Papier-Eintrag `MarkImportedToSetAsync` mit `ChannelName = null`, `TargetType = "emoteSet"`,
`targetOwnerSevenTvUserId`/`targetOwnerTwitchLogin` (`EmoteService.cs:299-330`). Die Audit-Ansicht
liest `emoteSetId` für `syncDeleted`/`syncRestored` und `targetOwnerTwitchLogin` generisch
(`AuditLogQueryService.cs:153-162`, `:237-263`) und rendert „für <ownerLogin>" bzw. „nicht das
aktive Set" (`audit-row.ts:128-146`).

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

**Die Zielliste des Pickers ist die Quelle für „darf ich dieses Set bearbeiten".**
`GET /api/seventv/me/emote-set-targets` (Spec-200 6.2, `SevenTvEndpoints.cs:127-186`) liefert je
Account (eigener + `editor_of`) `trackedChannelName`, `activeEmoteSetId`, `twitchLogin` und die
Set-Liste mit `kind`, `isActive`, `ownerDisplayName` (`seven-tv-emote-set.model.ts:50-87`);
Frontend-Client `SevenTvEmoteSetService.listEmoteSetTargets()` (`seven-tv-emote-set.service.ts:72-74`),
Policy `ForeignEmoteLookup` = **10 Permits je Nutzer und Minute** (`RateLimitingOptions.cs:66`,
`RateLimitRejection.cs:42`), serverseitig 60-s-Redis-Cache je Twitch-ID mit Koaleszierung (E6/E12).
Die Besitzprüfung des Backends liest **dieselben** Listen (`ImportTargetOwnershipService.cs:59-72`).

**Was den Rest der Seite nach einer Meldung bewegt.** `channel.synced` wird von den
kanalgebundenen Routen nur bei `NewlyArchivedCount > 0` bzw. `NewlyRestoredCount > 0` und nur
für den Routenkanal veröffentlicht (`EmoteEndpoints.cs:129-132`, `:177-180`, `:409-`). Die
Nutzungsseite reagiert darauf mit einem lauten Reload (Totals, Set-Status, Set-Liste, Mitgliederliste
einer nicht-aktiven Ansicht; `usage-stats-page.ts:1900-1933`). Der Restore-Dienst stößt zusätzlich
`POST /api/channels/{name}/resync` an, der **unbedingt** `channel.synced` veröffentlicht; die Route
steht hinter `UsageStatsAccessAuthorizationFilter` und der Policy `ChannelResync` (5/min je Nutzer;
`ChannelEndpoints.cs:217-263`). Für ein nicht-aktives Set der eigenen Seite ist heute **dieser
Resync** das, was die Mitgliederliste der Ansicht nachlädt.

---

## 2. Entscheidungen dieser Spec

E1–E6 sind die vom Betreiber freigegebenen Entscheidungen, hier nur mit Beleg festgehalten;
E7–E16 sind Festlegungen, die beim Nachprüfen am Code nötig wurden.

| # | Frage | Entscheidung | Begründung / Beleg |
|---|---|---|---|
| E1 | Woher kommt das Ziel eines Restore? | **Aus der Datei**, nie aus der Seite: Übertragungsdatei ⇒ `meta.targetEmoteSetId`; Purge-Protokoll ⇒ `meta.emoteSetId`. Die Prüfung „falscher Kanal / falsches Set gegen die Seite" entfällt ganz, für **alle** Rückweg-Dateien | Freigegeben. Der Halloween-Wechsel am 01.10. macht ein Protokoll, das ein inzwischen nicht-aktives Set nennt, zum Normalfall — heute ein `wrongSet` (`purge-run-export.ts:199-201`, Text „der Channel hat das aktive Set gewechselt", `de.json:936`). Die in der Freigabe genannte dritte Klasse „alte Purge-Datei ohne Set-ID" **existiert nicht** (Abschnitt 1, Commit `fe53329b`) — s. Abschnitt 13, Punkt 1 |
| E2 | Was tritt an die Stelle der Kanalprüfung? | Eine **Zielprüfung**: das Set muss in der Antwort von `GET /api/seventv/me/emote-set-targets` stehen, unter einem Account des Nutzers, mit `kind === 'NORMAL'`. Scheitert sie, gibt es einen Fehler mit Grund und **keinen** Lauf | Freigegeben; dieselbe gecachte Quelle wie der Ziel-Picker und wie die Besitzprüfung des Backends — Frontend und Api sagen dasselbe, weil sie dieselben Listen lesen. `kind === 'NORMAL'` ist die positive Regel aus Spec-200 8.6 (E7); ein persönliches Set ist damit **nicht** wiederherstellbar (E11) |
| E3 | Wohin melden Delete, Restore und die Replace-Löschung? | An die **set-zentrischen** Routen `POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted` und `…/sync-restored`. **Alle** Aufrufer wechseln; die kanalgebundene set-scoped Form `{ emoteSetId, sevenTvEmoteIds }` samt Überladung `(channelName, emoteSetId, …)` und Antwortfeld `targetIsActiveSetOfChannel` **entfällt** | Freigegeben. Sie existiert nur auf dem Epic-Branch (Abschnitt 1), hat nach dem Wechsel keinen Aufrufer, und #224 sitzt genau in ihr |
| E4 | Bleibt die kanalgebundene Altform `{ emoteIds }` (Guid)? | **Ja, unverändert, bis zum E3-Tor der Spec-200** (§21, Folge-Issue 1: hinter K7, ≥ 14 Tage, keine Altform-Log-Zeile). Die Route bleibt hinter `UsageStatsAccessAuthorizationFilter`, die Guid-Überladung und ihre Log-Zeile bleiben | Aufruferprüfung (Abschnitt 5.6): nach dem Wechsel gibt es in `web/src` **keinen** Code-Aufrufer der kanalgebundenen Routen mehr — der einzige verbleibende Aufrufer sind **Browser-Tabs mit dem Produktions-Frontend**, die über das Wartungsfenster K7 hinweg offen bleiben und `{ emoteIds }` senden; ihre Meldung kommt **nach** der 7TV-Mutation, ein 404 dort kostete die Papierspur. Das ist exakt der Fall, für den das E3-Tor gebaut ist; diese Spec ändert am Tor nichts und zieht es nicht vor |
| E5 | Wer darf melden? | Wer das Set laut 7TV **bearbeiten** darf — Besitzer oder `editor_of` des Besitzers, geprüft über `IImportTargetOwnershipService.CheckAsync` mit dem **eingeloggten** Twitch-Konto. Die Kanalrolle (Admin-Allowlist, Broadcaster, Live-Moderator) entscheidet nicht mehr | Freigegeben. Deckt sich mit 7TV: ohne Editor-Recht des Token-Kontos scheitert schon die Mutation (`LACKING_PRIVILEGES`, `abortsForMissingPrivileges` im Engine). Was daran bricht, steht als F4 und in Abschnitt 13, Punkt 4 |
| E6 | Audit für ungetrackte und nicht-aktive Ziele | Ein Set-Eintrag **ohne Kanal** (`ChannelName = null`, `TargetType = "emoteSet"`, `TargetId = emoteSetId`) wie heute `MarkImportedToSetAsync`; sichtbar in der globalen Admin-Ansicht, nicht in einem Kanal-Feed (bewusster Rest, Spec-200 6.7) | Freigegeben; kein Schema-Wechsel — `AuditLogEntry.ChannelName` ist nullable (`AuditLogEntry.cs:72`), `AuditActions.EmotesSyncDeleted/-Restored` existieren (`:17-18`) |
| E7 | Signaturen der neuen Service-Methoden | `IEmoteService.MarkDeletedInSetAsync(emoteSetId, ownerSevenTvUserId, ownerTwitchLogin, sevenTvEmoteIds, actor, ct)` und `MarkRestoredInSetAsync(…)` mit identischer Parameterliste | **Festlegung.** Das freigegebene Design nennt `(emoteSetId, sevenTvEmoteIds, actor)`; die beiden Besitzer-Felder kommen dazu, weil der Endpunkt sie aus der Besitzprüfung ohnehin hat und der Papier-Eintrag sie braucht, um in der Audit-Ansicht „für <ownerLogin>" zu rendern (`audit-row.ts:136-141`) — dieselbe Signaturform wie `MarkImportedToSetAsync` (`IEmoteService.cs:85-88`). Ohne sie hätte der Papier-Eintrag eines ungetrackten Sets weder Kanal noch Besitzer, nur eine ID |
| E8 | Woher weiß der Service, welche Kanäle betroffen sind? | Aus `Channels` mit `ActiveEmoteSetId == emoteSetId && IsBotActive`, danach in-memory `!IExcludedChannelFilter.IsExcluded(TwitchChannelId)` — dasselbe Muster wie `ListActiveChannelNamesAsync` (`ChannelService.cs:225-248`). `EmoteService` bekommt `IExcludedChannelFilter` als Konstruktorabhängigkeit | **Festlegung.** Ein gesperrter Kanal (#252) wird nirgends mehr beschrieben; die Prüfung sitzt im Service, nicht im Endpunkt, damit sie in `Infrastructure.Tests` ohne Api-Pipeline geprüft wird |
| E9 | Antwort-DTO der beiden neuen Routen | `{ reportedCount, channels: [{ channelName, archivedCount \| restoredCount, notFoundIds }] }` — `channels` leer heißt „nur Papier". Vertrag in 5.3 | **Festlegung.** Die freigegebene Vorgabe („Liste der betroffenen Kanäle und die Zählungen") in eine Form gebracht, die das Frontend dreiwertig lesen kann (`succeeded`/`partial`/`failed`) und die bei einem geteilten Set je Kanal ehrlich bleibt |
| E10 | Wo läuft die Zielprüfung im Frontend? | Im **`FileImportStep`**, als dritter Prüfschritt nach Envelope und Parser, vor `picked`. Der Schritt zeigt den Fehler in seinem bestehenden Banner; der Dialog schließt erst mit einem geprüften Ziel | **Festlegung.** Der Schritt „liest und prüft die Datei" (UI-Designsprache §7.3) und besitzt den einzigen Fehler-Ort dieser Kette; `startRestoreFlow` hat keinen. Der Schritt bekommt dafür `SevenTvEmoteSetService` injiziert (core → shared ist erlaubt) |
| E11 | Datei mit persönlichem Set als Ziel | **Abgewiesen** (`kind !== 'NORMAL'`), mit eigenem Grund | **Festlegung.** Kein Picker und kein Dropdown bietet ein persönliches Set an (Spec-200 §34, §35, §39), also kann EmotePurge nie eine Löschung daraus erzeugt haben; eine solche Datei ist fremd oder von Hand geändert. Die positive Regel der 8.6 gilt hier unverändert, damit ein künftiger fünfter `kind`-Wert auf der sicheren Seite landet |
| E12 | Resync nach einem Restore | Genau **ein** Resync, und zwar des **getrackten Kanals des Ziel-Accounts** (`trackedChannelName` aus der Zielprüfung), unabhängig davon, ob das Ziel dessen aktives Set ist; kein Resync für ein ungetracktes Ziel | **Festlegung** — bewusst **nicht** die engere Import-Regel (nur bei aktivem Set, `seven-tv-import.service.ts:619-646`): beim Restore ist der Resync heute das, was die Mitgliederliste einer nicht-aktiven Ansicht nachlädt (Abschnitt 1, letzter Absatz; Spec-200 8.3), und die Wiederherstellung landet in genau diesem Set. Weitere betroffene Kanäle eines geteilten Sets bekommen ihr `channel.synced` vom Endpunkt (5.4) und ihren periodischen Resync |
| E13 | Was `RestoreRunInfo` trägt | `{ targetSetId, resyncChannelName: string \| null, hostChannelName, result }` — das Ziel-Set, der Kanal aus E12, und der Kanal der Seite, auf der der Lauf gestartet wurde | **Festlegung** zur freigegebenen Vorgabe „hängt am Ziel-Set statt am `channelName`"; `hostChannelName` ist nötig, weil `resetIfChannelChanged` im Layout nur den Kanal der Seite kennt — s. Abschnitt 13, Punkt 3 |
| E14 | Was mit `emote_set_id_empty` geschieht | **Entfällt** aus `ApiErrorCodes`, `api-error.ts` und beiden Locales | **Festlegung.** Der Code existiert nur für die entfallende Leiter (`EmoteEndpoints.cs:304`, `ApiErrorCodes.cs:102`, `api-error.ts:53`); ein toter Fehlercode in drei Dateien ist genau die Drift, die Regel 7 vermeiden will. `invalid_emote_set_id` und `emote_set_not_found` bleiben |
| E15 | Was mit `wrongChannel` und `wrongSet` geschieht | Beide Fehler und ihre Schlüssel **entfallen** aus beiden Parsern und beiden Locales; der Parser gibt das Ziel zurück statt es zu prüfen | **Festlegung.** Es gibt nach E1 keinen Weg mehr, der sie erzeugt. Die neuen Schlüssel der Zielprüfung stehen in 6.1; ihr Wortlaut ist vorläufig und gehört #255 |
| E16 | Restore direkt aus dem fertigen Delete-Lauf | Bleibt, ohne Zielprüfung: Ziel = `lastRun.setId`, `resyncChannelName = lastRun.channelName`, `hostChannelName` = Seite | **Festlegung.** Der Delete-Lauf hat dasselbe Set Sekunden zuvor über dieselbe Besitzprüfung gemeldet; eine zweite Liste kaufte nichts und kostete ein Permit |

---

## 3. Fallen, im Code nachgeprüft am 2026-09-24

### F1 — Es gibt keine Purge-Datei ohne Set-ID, aber es gibt Purge-Dateien mit inzwischen nicht-aktivem Set

`meta.emoteSetId` ist seit `fe53329b` Pflicht; `parsePurgeRunProtocol` weist ein fehlendes Feld als
`wrongKind` ab (`purge-run-export.ts:196-198`). Der in der Freigabe beschriebene Fallback „aktives
Set des Kanals aus dem Envelope" hätte keinen Eingabefall — und er wäre gefährlich: nach dem
Set-Wechsel am 01.10. würde er ein Protokoll des alten Sets in das neue schieben. **Vorgabe:** kein
Fallback; ein Protokoll nennt sein Set immer, und die Bestätigung sagt, wenn dieses Set nicht das
aktive seines Kanals ist (die bestehende Zeile `restore.confirmSetNotActive`,
`restore-confirm-dialog.ts:51-55`). Abschnitt 13, Punkt 1.

### F2 — Der Envelope einer ungetrackten Übertragungsdatei trägt `''` als Kanal

`buildTransferPlanRecord`/`buildTransferRunProtocol` schreiben `channelName: targetChannelName ??
''` (`transfer-run-export.ts:219`, `:260`). Wer den Kanal aus dem Envelope statt aus
`meta.targetChannelName` liest, hält `''` für einen Kanalnamen. **Vorgabe:** der Restore-Parser
liest für das Ziel ausschließlich `meta.targetEmoteSetId`; der Kanal-Hinweis kommt aus der
Zielprüfung (`trackedChannelName`), nie aus der Datei.

### F3 — Die Zielprüfung teilt sich 10 Permits je Minute mit dem Rest der Seite

`ForeignEmoteLookup` ist mit 10/min je Nutzer die knappste Policy der Seite
(`RateLimitingOptions.cs:66`) und deckt Ziel-Picker, Set-Dropdown, Set-Vorschau der nicht-aktiven
Ansicht und den Quell-Picker. Ein Datei-Pick kostet **ein** Permit, die Slot-Vorschau der
Bestätigung für ein nicht-(getrackt-aktives) Ziel ein zweites; `filterAlreadyPresentForRestore`
liest 7TV direkt und tokenlos (`seven-tv-set-entries.ts:6`) und kostet **keins**. Serverseitig
fängt der 60-s-Cache je Account den Normalfall; ein Miss kostet 1 + *k* v4-Requests (E6). **Vorgabe:**
ein 429 auf der Zielprüfung ist ein „gerade nicht prüfbar" mit Wiederholen-Hinweis, nie ein
„nicht erlaubt"; die Zielprüfung wird **einmal** je Datei-Pick gestellt und ihr Ergebnis bis in
die Bestätigung weitergereicht (Set-Name, Besitzer, Kanal, `isActive`), kein zweiter Aufruf.

### F4 — Token-Identität und Login-Identität sind zwei verschiedene Konten

Der Lauf schreibt mit dem Token aus `sessionStorage` (`seven-tv-token.service.ts:6-18`) — irgendein
7TV-Token, das der Nutzer eingefügt hat. Die Besitzprüfung fragt nach dem **eingeloggten
Twitch-Konto** (`CheckAsync(actor.TwitchUserId, …)`, `SevenTvEndpoints.cs:227`). Fügt ein
Twitch-Moderator ohne eigenes Editor-Recht das Token eines berechtigten Kontos ein, gelingt die
Mutation und die Meldung wird mit 403 abgewiesen: der Lauf endet mit `failed` im Bericht, die
Papierspur fehlt, die Zeilen heilt der periodische Resync binnen einer Minute. **Heute** lässt
`UsageStatsAccessAuthorizationFilter` diesen Moderator melden (`ChannelAccessService.cs:36-38`).
Das ist die eine Stelle, an der die Rechte-Änderung (E5) einen heutigen Ablauf bricht — und
dieselbe Eigenschaft hat das set-zentrische `sync-imported` seit K2. Beim **Restore** blockt die
Zielprüfung (E2) den Lauf **vor** der ersten Mutation; beim **Delete** und beim **Replace** gibt es
keine Vorprüfung, dort bleibt es beim sichtbaren `failed` mit Retry. Abschnitt 13, Punkt 4.

### F5 — Beide Caches der Besitzprüfung sind älter als der Lauf

Die Set-Listen sind 60 s gecacht (E12 der Spec-200), die Editor-Grants 10 min (F10 dort). Ein Set,
das 7TV in den letzten 60 s gelöscht hat, oder ein Recht, das in den letzten 10 min entzogen wurde,
passiert Zielprüfung **und** Besitzprüfung noch; ein Set, das jünger als 60 s ist, fehlt in der
Liste und wird als „nicht bearbeitbar" gemeldet. **Vorgabe:** beides ist hinnehmbar — die Prüfung
ist Audit-Ehrlichkeit, keine Zugriffskontrolle (`IImportTargetOwnershipService.cs:66-72`); die
7TV-Mutation selbst ist das Tor. Die Fehlermeldung der Zielprüfung sagt deshalb „nicht bearbeitbar
oder nicht (mehr) vorhanden", nie nur eines von beiden.

### F6 — Der `ImportSourceDialog` schließt mit `picked`, und die Zielprüfung ist asynchron

`(picked)="dialogRef.close($event)"` (`import-source-dialog.ts:164`). Heute sind beide Parser
synchron. **Vorgabe:** `picked` feuert erst **nach** der Zielprüfung; während sie läuft, nimmt der
Schritt keinen zweiten Pick an (Dateiknopf gesperrt), ein Fehler bleibt im Banner des Schritts, der
Dialog bleibt offen. Der Nutzer kann abbrechen; eine spät eintreffende Antwort eines geschlossenen
Dialogs ändert nichts mehr (R15-Disziplin wie überall im Lauf).

### F7 — `resetIfChannelChanged` kennt nur den Kanal der Seite

`channel-workspace-layout.ts:151-160` ruft `resetIfChannelChanged(channelName)` je Kanalwechsel;
es gibt dort kein Set. Ein Vergleich „mit dem Set" ist an dieser Stelle nicht ausdrückbar (E13,
Abschnitt 13, Punkt 3).

### F8 — Ein unvollständiger Treffer ist `partial`, kein Fehler — auch bei einem geteilten Set

Heute liest das Frontend `archivedCount >= sevenTvEmoteIds.length` als `succeeded`
(`seven-tv-delete.service.ts:305`). Mit mehreren betroffenen Kanälen muss die Regel **je Kanal**
gelten: ein Kanal, in dem ein Teil der IDs nie synchronisiert wurde, macht den Bericht `partial`,
auch wenn der andere vollständig ist (5.3, 6.4).

### F9 — Die E2E-Suite mockt je Test einzeln; drei bestehende Tests hängen an der kanalgebundenen Route

`vote-ballot.e2e.spec.ts:389`, `:580`, `:650` routen `**/api/channels/{c}/emotes/sync-deleted`
und `:513-515` prüfen den Body; `emote-import.e2e.spec.ts:1835-1919` erwartet, dass ein
**fremdes** Purge-Protokoll mit `wrongChannel` abgewiesen wird. Alle vier ändern sich mit dieser
Spec (10.4). Jeder E2E-Test, der eine Rückweg-Datei einliest, braucht ab jetzt `mockEmoteSetTargets`
(`e2e/support/mocks.ts:855-890`) — ohne Mock fällt die Zielprüfung durch den Dev-Proxy und der
Test sieht nur das Fehlerbanner.

### F10 — `EmoteService` wird in Tests direkt konstruiert

`new EmoteService(db, NullLogger<EmoteService>.Instance)` steht in jedem Fall von
`EmoteServiceTests.cs` (z. B. `:393`). E8 fügt eine dritte Abhängigkeit hinzu; jede Konstruktion im
Test wandert mit, am besten über einen Fixture-Helfer, damit der nächste Parameter nicht wieder
dreißig Stellen berührt. `ExcludedChannelFilterTests.cs:77` zeigt, wie ein echter Filter aus
Konfiguration gebaut wird.

### F11 — Zwei Doku-Stellen behaupten die kanalgebundene Meldung als Normalfall

`docs/Architectur.md:68`, `:133`, `:154`, `:202` und der Kommentar an
`UsageStatsAccessAuthorizationFilter.cs:6-17` beschreiben `sync-deleted` als den Meldeweg und
zählen ihn zum Filter. Nach dieser Spec ist die kanalgebundene Route nur noch die Altform bis zum
E3-Tor. Beide Stellen werden im selben Vorgang angepasst (12.2); UI-Designsprache §7.3 („brings
emotes **into the channel of the page**") ebenso, weil der Datei-Zweig jetzt in ein anderes Set
schreiben kann.

---

## 4. Vertrag: Verhalten

### 4.1 Einstieg und Ziel

1. Der Einstieg bleibt **„Importieren → Datei"** auf jeder Kanalseite (`ImportTrigger`,
   `ImportSourceDialog`, `FileImportStep`). Kein neuer Knopf, keine neue Seite.
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

4. Vor jedem `picked` fragt der Schritt **einmal** `GET /api/seventv/me/emote-set-targets` und
   sucht das Ziel-Set über alle Accounts. Vier Ausgänge:

| Befund | Ausgang |
|---|---|
| Set unter einem Account gefunden, `kind === 'NORMAL'` | **geprüft** — der Schritt emittiert `picked` mit dem aufgelösten Ziel (6.1) |
| Set gefunden, `kind !== 'NORMAL'` | Fehler `targetNotSelectable` (E11), kein Lauf |
| Set in keiner Liste, `sevenTvUnavailable === false` und kein Account mit `setsUnavailable` | Fehler `targetNotEditable` — „nicht bearbeitbar oder nicht (mehr) vorhanden" (F5), kein Lauf |
| Set in keiner Liste, aber `sevenTvUnavailable === true` oder ein `setsUnavailable`-Account, **oder** der Request scheitert (429, 503, Netz) | Fehler `targetCheckUnavailable` — „gerade nicht prüfbar, später erneut", kein Lauf |

5. Aus dem Treffer nimmt der Schritt mit: `emoteSetId`, `setName`, `ownerDisplayName` (nur
   Anzeige, nie verglichen — E7 der Spec-200), `twitchLogin` des Accounts, `trackedChannelName`
   (`null` = ungetrackt), `isActive` (für getrackte Accounts aus `Channel.ActiveEmoteSetId`,
   E21). Das ist das **aufgelöste Ziel** (`ResolvedRestoreTarget`, 6.1); es wird nicht erneut
   abgefragt (F3).

### 4.3 Bestätigung

6. Der Bestätigungsdialog nennt **immer** Set-Name und Besitzer (`ownerDisplayName`), bei einem
   getrackten Ziel zusätzlich den Kanal (`trackedChannelName`). Die bestehende Zeile „dieses Set
   ist gerade nicht aktiv" (`restore.confirmSetNotActive`) gilt für ein getracktes Ziel mit
   `isActive === false`; für ein ungetracktes Ziel wird sie **nicht** gezeigt (dort gibt es keinen
   beobachteten Zustand, dessen „aktiv" etwas bedeutete).
7. Ist das Ziel **nicht** das Set der aktuellen Seite — `trackedChannelName !== hostChannelName`
   oder ungetrackt — zeigt der Dialog einen eigenen Hinweis, der sagt, dass diese Seite von dem Lauf
   nichts zeigen wird. Wortlaut aller neuen Zeilen: #255; die Struktur (welche Zeile wann) ist hier
   Vertrag.
8. Reihenfolge der Kette bleibt: **Token vor Bestätigung** (`startRestoreFlow`, Doku `:44-48`),
   nicht angleichen. Die Slot-Vorschau liest für ein getracktes **aktives** Ziel
   `getSetStatus(trackedChannelName)`, sonst `loadEmoteSetPreview(trackedChannelName ??
   twitchLogin, emoteSetId)` — dieselbe Gabel wie `loadImportTarget`; ein Fehler dort blendet nur
   die Projektionszeile aus, wie heute.
9. `filterAlreadyPresentForRestore` (Regeln 1–4 inkl. „Name belegt") läuft **unverändert** gegen
   das Ziel-Set — sie liest 7TV nach Set-ID und braucht keinen Kanal.

### 4.4 Lauf, Meldung, Seite

10. Der Lauf sendet die ADDs an `emoteSetId` wie heute (Engine unverändert). Nach dem Lauf meldet
    der Restore-Dienst an `POST /api/seventv/emote-sets/{emoteSetId}/sync-restored` (5.1) — für
    jedes Ziel, getrackt wie ungetrackt, aktiv wie nicht-aktiv. Es gibt **keinen** zweiten Meldeweg
    mehr.
11. Danach genau ein Resync nach E12. Für ein ungetracktes Ziel keiner; das Dock zeigt dann auch
    keine Resync-Zeile.
12. Die Seite, auf der der Lauf gestartet wurde, ändert sich **nur**, wenn sie ein `channel.synced`
    für ihren eigenen Kanal bekommt (5.4, E12). Ist das Ziel ein anderes Set, bleibt das Raster
    stehen; das Dock zeigt den Lauf mit einer Zielzeile (Set-Name, Kanal oder Besitzer), analog
    zur Zielzeile des Import-Docks (`import-progress-section.ts:80-95`).
13. Das **Dock** des Restore-Laufs bleibt an die Seite gebunden, auf der er gestartet wurde
    (`hostChannelName`): ein Kanalwechsel setzt einen **fertigen** Lauf zurück wie heute, nie einen
    laufenden (`resetIfChannelChanged` kehrt bei `isRunning()` früh zurück, `:216-221`). Seine
    Meldungen gehen unabhängig davon raus, weil sie am Laufdatensatz hängen.

### 4.5 Die Sperre fällt

14. Regel 7 `replaceNeedsTrackedTarget` wird aus `validateResolution` **entfernt**; `ViolationRule`
    verliert den Wert, `ResolutionContext` das Feld `targetIsTracked` (danach leer — die
    Schnittstelle bleibt als leeres Objekt oder fällt, das entscheidet der Plan; kein Aufrufer
    darf einen Kontext ohne Bedeutung weiterreichen). Der Dialog zeigt „Ziel ersetzen" für ein
    ungetracktes Ziel wie für ein getracktes; der Deaktivierungsgrund `replaceNeedsTracked`
    (`de.json:1234`) und die Verletzungszeile (`:1254`, `en.json:1254`) entfallen.
15. Der Wächter in `startImport` (`seven-tv-import.service.ts:376-379`) entfällt. Ein Replace-Lauf
    meldet seine bestätigten REMOVEs (`completedSteps >= 1`, unverändert) an
    `…/sync-deleted` des Ziel-Sets (5.1); `reportRemoved` hat keinen Kanal-Zweig mehr.
16. **Die Pflicht-Rückweg-Datei aus #230 bleibt** — für ein ungetrackes Ziel genauso: Live-Read,
    `verifyReplaceTargets`, Download, erst dann „Starten" (DECISIONS 2026-09-23, „The safeguard is
    a file"). Der Dateiname nutzt bei ungetracktem Ziel weiterhin die Set-ID
    (`transferPlanFilename(targetChannelName ?? setId, …)`, `import-confirm-dialog.ts:1259-1261`).
17. `beforeunload` und der `canDeactivate`-Guard decken den laufenden Replace-Lauf unverändert; die
    Frage 6 des Plans #230 (Envelope-`channelName` `''` bei ungetracktem Ziel) bleibt so, weil der
    Restore-Parser das Feld nicht liest (F2).

### 4.6 Audit

18. Löschung und Wiederherstellung erscheinen im Audit-Log auch für ungetrackte Sets — als
    Set-Eintrag ohne Kanal (E6). Für getrackte Kanäle, deren aktives Set das Ziel ist, erscheint je
    Kanal ein Eintrag **mit** Kanal, wie heute der aktive Zweig (5.5). Die Audit-Ansicht braucht
    dafür **keine** neue Projektion: `emoteSetId` und `targetOwnerTwitchLogin` werden bereits
    generisch gelesen (`AuditLogQueryService.cs:153-162`, `:237-263`).

### 4.7 Nicht in #253

19. Das Rückgängigmachen eines geglückten Replace (#254). Regel 4 des Restore-Filters gilt
    unverändert: ein Alias, den inzwischen ein anderes Emote hält, wird ausgelassen und gezählt.
20. Wortlaute und Zählungen in Dock, Dialogen und Audit-Log (#255). Neue Schlüssel bekommen hier
    einen vorläufigen Text, #255 revidiert ihn.
21. #256, Punkte 1, 2, 4, 5. **Punkt 3** („Kanalnamen beim Wiederherstellen exakt verglichen")
    **erledigt sich durch E1**: es gibt keinen Kanalvergleich mehr, der falsch sein könnte. Das
    Issue wird um diesen Punkt gekürzt, nicht geschlossen.
22. Das Laden einer Datei als **Auswahl** (#201).

---

## 5. Vertrag: Backend

### 5.1 Routen

Beide in der bestehenden Gruppe `emoteSetGroup` (`SevenTvEndpoints.cs:198-201`), neben
`/sync-imported`:

```
POST /api/seventv/emote-sets/{emoteSetId}/sync-deleted
POST /api/seventv/emote-sets/{emoteSetId}/sync-restored
Body: { "sevenTvEmoteIds": string[] }
```

Ein gemeinsamer Body-Record für beide Routen (Name Plansache, Vorschlag `SyncInSetRequest`), ohne
`emoteSetId` im Body — die Route trägt es, wie bei `SyncImportedToSetRequest` begründet
(`SevenTvEndpoints.cs:429-436`).

**Leiter, in dieser Reihenfolge (jede Stufe nur, wenn die vorige passiert ist):**

| Stufe | Wirkung |
|---|---|
| 1 Middleware | nicht eingeloggt → 401; `Bookkeeping`-Budget (120/min je Nutzer, `RateLimitingOptions.cs:41`) → 429 |
| 2 `EmoteSetIdValidationFilter` (Gruppe) | ungültiger Routenwert → 400 `invalid_emote_set_id` |
| 3 Body | `sevenTvEmoteIds` fehlt oder leer → 400 `emote_ids_empty`; ein Body ohne Actor-Principal → 401 (wie `sync-imported`, `:220-224`) |
| 4 Besitzprüfung | `IImportTargetOwnershipService.CheckAsync(actor.TwitchUserId, actor.Login, emoteSetId)`: `SetNotFound` → 404 `emote_set_not_found`; `Forbidden` → 403 **bare** (`Results.Forbid()`); `Unavailable` → 503 `foreign_channel_seventv_unavailable`. **Kein Audit-Eintrag** auf diesen drei Ausgängen, kein Service-Aufruf |
| 5 Service | `MarkDeletedInSetAsync` bzw. `MarkRestoredInSetAsync` (5.2) → 200 mit Antwort (5.3) |
| 6 Live-Event | je Kanal der Antwort mit `NewlyChangedCount > 0` ein `channel.synced` (5.4) |

Policy `Bookkeeping`, nicht `ForeignEmoteLookup` — dieselbe Begründung wie am set-zentrischen
`sync-imported` (`SevenTvEndpoints.cs:188-195`): die Mutation ist passiert, ein verbrauchtes
Lesebudget darf die Papierspur nicht kosten; das passt nur, weil die Besitzprüfung keinen
ungebudgetierten 7TV-Request kostet. `IImportTargetOwnershipService` wird **nicht umbenannt**
(der Name ist import-lastig, aber die Umbenennung ist Refactoring ohne Vertragswert; ein Hinweis
in der Doku des Interfaces genügt).

### 5.2 Service

```
Task<SyncDeletedInSetResultDto>  MarkDeletedInSetAsync (string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, IReadOnlyList<string> sevenTvEmoteIds, AuditActor actor, CancellationToken ct)
Task<SyncRestoredInSetResultDto> MarkRestoredInSetAsync(string emoteSetId, string ownerSevenTvUserId, string ownerTwitchLogin, IReadOnlyList<string> sevenTvEmoteIds, AuditActor actor, CancellationToken ct)
```

Verhalten, für beide Richtungen spiegelbildlich:

1. `sevenTvEmoteIds` ordinal deduplizieren (wie `MarkImportedAsync`, `EmoteService.cs:259`);
   `reportedCount` = Anzahl danach.
2. Betroffene Kanäle bestimmen (E8): `IsBotActive && ActiveEmoteSetId == emoteSetId`, danach
   `IExcludedChannelFilter` in-memory. Reihenfolge: `ChannelName` ordinal, damit Antwort und
   Audit-Reihenfolge deterministisch sind.
3. Je betroffenem Kanal: `Emotes` mit `ChannelId == channel.Id && SevenTvEmoteId ∈ ids` laden;
   archivieren (`IsArchived = true`, `ArchivedAt = now`, `LastSyncedAt = now`, **nur** für noch
   nicht archivierte — Datum eines bereits archivierten bleibt) bzw. zurückholen (`IsArchived =
   false`, `ArchivedAt = null`, `LastSyncedAt = now`, nur für archivierte). Zielzustands-Zähler
   (`archivedCount`/`restoredCount`) = gefundene Zeilen, `NewlyChangedCount` = tatsächlich
   geänderte, `notFoundIds` = IDs ohne Zeile in **diesem** Kanal. Das ist die heutige Semantik
   des aktiven Zweigs (`EmoteService.cs:146-178`), nur je Kanal.
4. Audit (5.5).
5. **Ein** `SaveChangesAsync` für alles — Zeilen und Audit in einer Transaktion, wie heute.

Kein Treffer in Schritt 2 (ungetracktes Set, oder Set, das kein getrackter Kanal aktiv hat) ⇒ keine
Zeile berührt, nur der Papier-Eintrag; `channels` leer.

### 5.3 Antwort (Wire)

```
SyncDeletedInSetResponse {
  reportedCount: int                       // deduplizierte 7TV-IDs des Requests
  channels: [                              // betroffene getrackte Kanäle, ordinal nach Name; [] = nur Papier
    { channelName: string, archivedCount: int, notFoundIds: string[] }
  ]
}
SyncRestoredInSetResponse {
  reportedCount: int
  channels: [ { channelName: string, restoredCount: int, notFoundIds: string[] } ]
}
```

`NewlyChangedCount` steht **nicht** auf dem Draht (es steuert nur das Live-Event, wie heute
`NewlyArchivedCount`). Core-DTOs: `SyncDeletedInSetResultDto(ReportedCount, Channels)` und
`SyncRestoredInSetResultDto` mit je einer `…ChannelResultDto(ChannelName, Count, NewlyChangedCount,
NotFoundIds)`; die Namen sind Plansache, die Felder Vertrag.

### 5.4 Live-Event

Je Kanal in `channels` mit `NewlyChangedCount > 0` ein `LiveEvent(LiveEvents.ChannelSynced,
channelName)` über `IRedisPublisher` — dieselbe Hilfsmethode und dieselbe Fehlerbehandlung wie
`PublishChannelSyncedAsync` (`EmoteEndpoints.cs:409-`): im Endpunkt, nicht im Service; Fehler
geloggt und geschluckt; kein Request-Token. Ein geteiltes Set erzeugt so viele Events, wie Kanäle
Zeilen geändert haben. `IRedisPublisher` im Handler ist durch Regel 4 ausdrücklich erlaubt.

### 5.5 Audit

| Fall | Einträge |
|---|---|
| Betroffener Kanal mit Zielzustands-Zähler > 0 | je Kanal **ein** Eintrag: `Action = emotes.syncDeleted`/`syncRestored`, `ChannelName = <Kanal>`, `TargetType = "emoteSet"`, `TargetId = emoteSetId`, Details `{ emoteCount: <Zähler dieses Kanals>, emoteSetId, targetIsActiveSetOfChannel: true }` — byte-gleich mit dem heutigen aktiven Zweig (`EmoteService.cs:162-171`), damit `audit-row` ihn wie bisher rendert |
| Kein Kanal-Eintrag geschrieben (kein Treffer, oder jeder Treffer mit Zähler 0) | **ein** Papier-Eintrag: `ChannelName = null`, `TargetType = "emoteSet"`, `TargetId = emoteSetId`, Details `{ emoteCount: reportedCount, emoteSetId, targetOwnerSevenTvUserId, targetOwnerTwitchLogin }` — ohne `targetIsActiveSetOfChannel`, damit die Ansicht „für <ownerLogin>" wählt (`audit-row.ts:136-141`) |

Es gibt **immer mindestens einen** Eintrag je erfolgreichem Aufruf. Das ist die Antwort auf #224:
kein Erfolg ohne Papierspur. Ein wiederholter Bericht (Retry) darf einen zweiten Eintrag schreiben
— „ein Duplikat schlägt eine Lücke" (`EmoteService.cs:44-48`).

### 5.6 Was entfällt — und das Ergebnis der Aufruferprüfung

**Entfällt:** die set-scoped Überladungen `MarkDeletedAsync(channelName, emoteSetId, …)` und
`MarkRestoredAsync(channelName, emoteSetId, …)` (`IEmoteService.cs:50,53`,
`EmoteService.cs:115-239`); der Body-Zweig `{ emoteSetId, sevenTvEmoteIds }` der kanalgebundenen
Routen samt Stufen 2–4 der Leiter `ValidateSyncBookkeepingBody` (`EmoteEndpoints.cs:269-315`) — die
Records `SyncDeletedRequest`/`SyncRestoredRequest` kehren auf `(IReadOnlyList<string> EmoteIds)`
zurück; das Antwortfeld `targetIsActiveSetOfChannel` und der DTO-Parameter
`TargetIsActiveSetOfChannel` (`IEmoteService.cs:9,13`); `emote_set_id_empty` (E14). Ein Body mit
`sevenTvEmoteIds` an der kanalgebundenen Route landet dann bei `EmoteIds == null` und 400
`emote_ids_empty` — dieselbe Antwort wie auf `main`.

**Aufruferprüfung (am Branch, 2026-09-24).** Code-Aufrufer der kanalgebundenen Routen sind heute
genau drei, alle über `EmoteAdminService.syncDeleted/syncRestored`
(`emote-admin.service.ts:76-91`): `SevenTvDeleteService.reportDeleted`
(`seven-tv-delete.service.ts:284`), `SevenTvRestoreService.reportRestored`
(`seven-tv-restore.service.ts:273`), `SevenTvImportService.reportRemoved`
(`seven-tv-import.service.ts:696`). Die Vote-Session-Detailseite läuft über das eingebettete
`MassDeletePanel` und damit über den Delete-Dienst, kein vierter Weg. Nach dem Wechsel (6.4) gibt es
**null** Code-Aufrufer; `EmoteAdminService.syncDeleted/syncRestored` und `SyncBookkeepingBody`
entfallen im Frontend. Übrig bleibt **ein** Aufrufer außerhalb des Codes: Browser-Tabs mit dem
Produktions-Frontend, die vor K7 geöffnet wurden und nach dem Deploy `{ emoteIds }` senden.
**Entscheidung (E4):** die kanalgebundene Route mit Guid-Body und die Guid-Überladung bleiben,
unverändert und samt Log-Zeile, bis zum E3-Tor der Spec-200 §21 — ein Tab, der nach der Mutation
ein 404 bekommt, verliert die Papierspur, und genau diesen Verlust misst das Tor mit der Log-Zeile,
bevor es die Form abschafft. Ihr Filter (`UsageStatsAccessAuthorizationFilter`) und ihre Policy
(`Bookkeeping`, `EmoteRoutePolicyTests.cs:33`) bleiben ebenfalls. Der Abbau ist Folge-Issue 1 der
Spec-200 §21, nicht Teil von #253.

### 5.7 Rechte-Änderung, ausformuliert

| Heute (kanalgebunden, `CanViewUsageStatsAsync`) | Neu (set-zentrisch, `CheckAsync`) |
|---|---|
| Admin-Allowlist | nur, wenn Besitzer oder `editor_of` |
| Broadcaster (Twitch-Konto = Kanal) | Besitzer des Sets — ja, wenn das Set dem eigenen 7TV-Konto gehört; ein fremdes Set, das der Kanal aktiv hat, **nein** |
| Live-Moderator ohne 7TV-Editor-Recht | **nein** (F4) |
| 7TV-Editor des Kanal-Accounts | ja (`editor_of`) |

Die Prüfung ist keine Zugriffskontrolle (die Mutation ist passiert), sondern Audit-Ehrlichkeit —
niemand schreibt Einträge über ein Set, das er nicht bearbeitet
(`IImportTargetOwnershipService.cs:64-72`). Wer die 7TV-Mutation mit dem eigenen Token schaffte,
ist Besitzer oder Editor und passiert die Prüfung; wer sie nur mit einem fremden Token schaffte,
bekommt ein 403 und ein `failed` im Dock (F4). Der Fall „Broadcaster, dessen Kanal ein fremdes Set
aktiv hat" konnte auch heute nichts löschen, ohne auf dem fremden Konto Editor zu sein.

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
  }
  ```

- `FileImportStep` behält `channelName` als Input (er ist der `hostChannelName`); `setId` entfällt
  als Input des Schritts, weil kein Prüfschritt ihn mehr liest. `ImportSourceDialogData.setId`
  bleibt für die drei Kopier-Türen (sie zielen weiter auf das gewählte Set der Seite, Spec-200 8.6).
- Ablauf im Schritt: Envelope → Parser → Zielprüfung (4.2) → `picked`. Fehler-Schlüssel der
  Zielprüfung, neu in beiden Locales unter `restore.import.errors.*`: `targetNotEditable`,
  `targetNotSelectable`, `targetCheckUnavailable`. Wortlaut vorläufig, #255.
- UI-Designsprache §7.3 erhält den Satz, dass der Datei-Zweig für Rückweg-Dateien das Ziel aus der
  Datei nimmt und es prüft, statt es gegen die Seite zu halten (12.2).

### 6.2 Flow und Bestätigung

- `startRestoreFlow(deps, target: ResolvedRestoreTarget, rows)` ersetzt die vier Positionsparameter
  `channelName, setId, setName, isActiveSet`. Beide Aufrufer (`ImportTrigger` und
  `MassDeletePanel.openRestoreConfirm`, E16) bauen das Ziel; der Panel-Aufrufer ohne Zielprüfung mit
  `setName` aus `setNames`, `ownerDisplayName` = Kanalname, `trackedChannelName = run.channelName`,
  `isActiveSet = run.setId === effectiveActiveSetId`.
- `RestoreConfirmDialogData` bekommt `ownerDisplayName: string`, `trackedChannelName: string |
  null`, `foreignToPage: boolean` (4.3, Punkte 6–7). `setName`/`isActiveSet` bleiben.
- Slot-Vorschau nach 4.3, Punkt 8.

### 6.3 Restore-Dienst

- `startRestore(target: { setId, resyncChannelName: string | null, hostChannelName }, emotes,
  skippedDuplicates, duplicateCheckAvailable, skippedNameTaken)`; `RestoreRunInfo` nach E13.
- `reportRestored` → `SevenTvEmoteSetService.reportRestoredInSet(setId, { sevenTvEmoteIds })`
  (neu, neben `reportImportedToSet`, `seven-tv-emote-set.service.ts:169-171`), Retry-Policy
  unverändert (`MAX_AUTOMATIC_SYNC_RETRIES`, 401/403 ohne Retry).
- `syncReport`-Ableitung aus der Antwort (F8): `succeeded`, wenn `channels` leer ist **oder** für
  jeden Kanal `restoredCount === reportedCount`; sonst `partial`; jeder HTTP-Fehler nach den Retries
  — 404, 403, 503, 429, Netz — `failed`. Ein 404 (Set inzwischen weg) führt damit zu `failed`, was
  #224 im Frontend schließt.
- Resync nach E12: `channelService.resync(resyncChannelName)` nur bei `resyncChannelName !== null`;
  Zustände `succeeded`/`cooldown`/`failed` wie heute; bei `null` bleibt `resyncTrigger` auf `idle`
  und das Dock zeigt keine Resync-Zeile.
- `resetIfChannelChanged(pageChannelName)` vergleicht mit `run.hostChannelName` (E13; Abschnitt 13,
  Punkt 3).
- Die Dock-Zielzeile (4.4, Punkt 12) liest `targetSetName` und `trackedChannelName ??
  ownerDisplayName` aus dem Laufdatensatz; `RestoreRunInfo` trägt beides zusätzlich (Anzeigefelder,
  nie verglichen).

### 6.4 Delete- und Import-Dienst

- `SevenTvDeleteService.reportDeleted` → `reportDeletedInSet(run.setId, { sevenTvEmoteIds })`;
  `syncReport` nach derselben Dreiwertigkeit wie 6.3. `DeleteRunInfo.channelName` **bleibt** — er
  ist Kanal der Seite, Envelope-`channelName` des Purge-Protokolls und Dateiname
  (`mass-delete-panel.ts:592-618`), nur nicht mehr Adressat der Meldung.
- `SevenTvImportService.reportRemoved` → `reportDeletedInSet(run.targetSetId, …)`; `removalReport`
  nach derselben Dreiwertigkeit; der `channelName === null`-Frühausstieg (`:688-691`) entfällt.
  `reportImported` bleibt, wie es ist (kanalgebunden für getrackte, set-zentrisch für ungetrackte
  Ziele — das ist Spec-200 6.7 und nicht Gegenstand hier).
- `EmoteAdminService.syncDeleted/syncRestored`, `SyncBookkeepingBody`, `SyncDeletedResult`,
  `SyncRestoredResult` entfallen; die neuen Antworttypen leben bei `SevenTvEmoteSetService`.

### 6.5 Regel 7 und der Wächter

- `conflict-resolution.ts`: Regel 7, `ruleReplaceNeedsTrackedTarget`, der Union-Wert
  `replaceNeedsTrackedTarget` und `ResolutionContext.targetIsTracked` entfallen; die Doku von
  `validateResolution` zählt sechs Regeln. `import-confirm-dialog.ts:639-642` und die
  Disabled-Reason-Zuordnung in `import-conflict-resolution-step.ts:88` entfallen mit.
- `seven-tv-import.service.ts:376-379` entfällt.
- Beide Locale-Schlüssel (`import.resolve.replaceNeedsTracked`, `de.json:1234`,
  und `import.resolve.violation.replaceNeedsTrackedTarget`, `:1254`) entfallen.

---

## 7. Grenzfälle

| Fall | Verhalten |
|---|---|
| **Set gelöscht zwischen Zielprüfung und Lauf** | Die ADDs scheitern bei 7TV (Zeilen `failed`); ohne `doneKeys` keine Meldung (`onRunComplete`, `:250-252`). Gelingt ein ADD trotzdem (7TV-Race), antwortet die Meldung 404 → `failed`, Retry-Knopf; kein Audit. Kein stiller Erfolg (#224) |
| **Recht entzogen zwischen Zielprüfung und Lauf** | Mutation scheitert mit `LACKING_PRIVILEGES` → Engine bricht ab, Token wird gelöscht (bestehendes Verhalten). Gelingt sie, weil der Grants-Cache noch alt ist (F5), passiert auch die Meldung noch — Audit-Ehrlichkeit, kein Sicherheitsproblem |
| **Geteiltes Set** (zwei getrackte Kanäle mit demselben aktiven Set) | Beide in `channels`, beide Zeilen archiviert/zurückgeholt, je Kanal ein Audit-Eintrag, je Kanal ein `channel.synced`; `succeeded` nur, wenn beide vollständig; Resync nur für den Kanal des Ziel-Accounts (E12) |
| **Ziel ist das aktuelle Seiten-Set** (aktives Set des Seitenkanals) | Wie heute erlebbar: Zeilen im Seitenkanal geändert, `channel.synced` für die Seite, lauter Reload; Dialog ohne Fremd-Hinweis; Audit mit Kanal |
| **Ziel ist ein nicht-aktives Set des Seitenkanals** | Nur Papier (`channels` leer), Dialog mit „nicht aktiv"-Zeile, Resync des Seitenkanals (E12) → `channel.synced` → Mitgliederliste der Ansicht lädt neu, wie heute |
| **Ziel ist ein getrackter Fremdkanal** | Zeilen dort geändert, `channel.synced` dort, Resync dort (erfordert Usage-Stats-Zugriff auf diesen Kanal — ein `editor_of` hat ihn, `ChannelAccessService.cs:46-62`; sonst `resyncTrigger: 'failed'`, sichtbar, unschädlich); die Seite des Nutzers bleibt stehen, Dock mit Zielzeile |
| **Ungetracktes Ziel** | Zielprüfung über `editor_of`; nur Papier mit Besitzer; kein Resync, keine Resync-Zeile; Slot-Vorschau über `loadEmoteSetPreview(twitchLogin, setId)` |
| **Zwei Restore-Läufe gleichzeitig** | Unmöglich: `SevenTvRunArbiter.activeRun()` wird vor der Bestätigung **und** nach dem Filter-Read geprüft (`restore-flow.ts:112`, `:142`), auch am Panel-Einstieg (`mass-delete-panel.ts:630`). Ein zweiter Pick während eines Laufs endet still — der laufende ist im Dock sichtbar. Die Zielprüfung selbst kostet ein Permit, auch wenn der Lauf dann nicht startet |
| **Persönliches Set als Ziel** | `targetNotSelectable` (E11), kein Lauf |
| **Rate-Limit der Zielprüfung** (429 auf `ForeignEmoteLookup`) | `targetCheckUnavailable`, kein Lauf, kein Permit-Verbrauch darüber hinaus; nach dem Minutenfenster erneut wählbar (F3) |
| **Rückweg-Datei eines nie gestarteten Laufs** | Unverändert (#230): jede Zeile fällt über „schon vorhanden" heraus, transiente Notiz, kein Lauf — jetzt auch für ein ungetracktes Ziel |
| **Dieselbe Datei auf zwei verschiedenen Kanalseiten** | Beide Male dasselbe Ziel, dieselbe Prüfung; nur `hostChannelName` unterscheidet sich (Dock-Bindung) |
| **Alter Tab (Produktions-Frontend) während K7** | Meldet `{ emoteIds }` an die kanalgebundene Route; bleibt gültig (E4) |

---

## 8. Akzeptanzkriterien

Jedes Kriterium ist so formuliert, dass ein Test oder ein Handgriff es entscheidet.

1. Eine Übertragungsdatei mit ungetracktem Ziel (`meta.targetChannelName: null`), auf der
   Nutzungsseite eines **anderen** Kanals eingelesen, öffnet die Restore-Bestätigung mit Set-Name
   und Besitzer und startet nach Bestätigung einen Lauf gegen `meta.targetEmoteSetId`.
2. Ein Purge-Protokoll, dessen `meta.emoteSetId` nicht das gewählte Set der Seite ist, wird
   **nicht** abgewiesen; das Ziel ist das Set der Datei; ist es ein nicht-aktives Set seines
   getrackten Kanals, zeigt die Bestätigung die „nicht aktiv"-Zeile.
3. Eine Rückweg-Datei, deren Set in keinem Account der Zielliste steht, führt zum Fehler
   `targetNotEditable` im Banner des Datei-Schritts; kein Dialog schließt, kein Request an 7TV.
4. Antwortet die Zielliste mit `sevenTvUnavailable: true`, mit einem `setsUnavailable`-Account, mit
   429 oder 503, lautet der Fehler `targetCheckUnavailable`; kein Lauf.
5. Ein Ziel mit `kind !== 'NORMAL'` führt zu `targetNotSelectable`; kein Lauf.
6. Je Datei-Pick geht **genau ein** Request an `/api/seventv/me/emote-set-targets`; die Bestätigung
   löst keinen zweiten aus.
7. Die Restore-Meldung geht an `POST /api/seventv/emote-sets/{setId}/sync-restored` mit
   `{ sevenTvEmoteIds }` (dedupliziert, eine ID je Emote auch bei zwei Aliasen); es gibt keinen
   Request an `/api/channels/*/emotes/sync-restored` mehr aus dem Frontend.
8. Die Delete-Meldung (Nutzungsseite **und** Vote-Session-Detailseite) geht an
   `…/sync-deleted` des Laufs-Sets; ebenso die Löschmeldung eines Replace-Laufs.
9. `…/sync-deleted` und `…/sync-restored` antworten 401 anonym, 400 `invalid_emote_set_id` bei
   ungültigem Routenwert, 400 `emote_ids_empty` bei leerer Liste, 404 `emote_set_not_found`, 403
   bare, 503 `foreign_channel_seventv_unavailable` — auf 404/403/503 **ohne** Service-Aufruf und
   ohne Audit-Eintrag; beide Routen tragen die Policy `Bookkeeping`.
10. Für ein Set, das das aktive Set eines getrackten, nicht gesperrten Kanals ist, archiviert bzw.
    reaktiviert der Service die Zeilen dieses Kanals, schreibt einen Audit-Eintrag mit Kanal und
    `targetIsActiveSetOfChannel: true`, und die Antwort nennt den Kanal mit Zähler und `notFoundIds`.
11. Für ein geteiltes Set (zwei Kanäle) geschieht 10 für **beide** Kanäle; die Antwort listet beide.
12. Ein Kanal, dessen `TwitchChannelId` auf `Channels:ExcludedChannelIds` steht, wird nicht berührt
    und nicht genannt, auch wenn sein aktives Set das Ziel ist.
13. Für ein ungetracktes Set und für ein nicht-aktives Set eines getrackten Kanals wird keine Zeile
    berührt; genau ein Papier-Eintrag mit `ChannelName = null`, `TargetType = "emoteSet"`,
    `TargetId = setId`, `targetOwnerTwitchLogin` entsteht; `channels` ist leer.
14. Je Kanal mit tatsächlich geänderten Zeilen wird genau ein `channel.synced` veröffentlicht; ohne
    Änderung keines.
15. Der Delete-Dienst, der Restore-Dienst und der Import-Dienst lesen `channels: []` als
    `succeeded`, einen Kanal mit `count < reportedCount` als `partial`, jeden HTTP-Fehler als
    `failed` — ein 404 der Meldung endet in `failed`, nie in `succeeded` (#224).
16. Der Auflösungsschritt bietet „Ziel ersetzen" für ein ungetracktes Ziel wählbar an;
    `validateResolution` kennt keine Regel `replaceNeedsTrackedTarget`; `startImport` wirft nicht
    mehr für einen Replace gegen `channelName === null`.
17. Ein Replace-Lauf in ein ungetracktes Set verlangt weiterhin die Rückweg-Datei vor dem Start und
    meldet seine bestätigten REMOVEs set-zentrisch; das Audit (globale Ansicht) zeigt danach einen
    `syncImported`- und einen `syncDeleted`-Eintrag ohne Kanal, beide „für <ownerLogin>".
18. Nach einem Restore dieser Datei auf einer anderen Kanalseite zeigt das Audit zusätzlich einen
    `syncRestored`-Eintrag ohne Kanal.
19. Die Bestätigung eines Restore, dessen Ziel nicht das Set der Seite ist, zeigt den
    Fremd-Hinweis; für das Set der Seite nicht.
20. Ein fertiger Restore-Lauf verschwindet beim Wechsel auf einen anderen Kanal aus dem Dock (wie
    heute); seine Meldung ist trotzdem gesendet.
21. Nach einem Restore in ein nicht-aktives Set des Seitenkanals wird `POST
    /api/channels/{seite}/resync` ausgelöst; nach einem Restore in ein ungetracktes Set kein Resync.
22. `wrongChannel`, `wrongSet`, `emote_set_id_empty` und beide Regel-7-Schlüssel existieren in
    keiner Locale, keinem Parser und keinem `ApiErrorCodes` mehr; `api-error-locales.spec.ts` bleibt
    grün.
23. Die kanalgebundene Route mit `{ emoteIds }` antwortet unverändert wie auf `main` (200 mit
    `archivedCount`/`notFoundIds`, Log-Zeile „legacy body form"); ein Body `{ emoteSetId,
    sevenTvEmoteIds }` dort → 400 `emote_ids_empty`.
24. Bestehende `purge-run`-Dateien beider Formatversionen werden zeilen-identisch gelesen wie vor
    dieser Spec (die Parser-Specs für `readProtocolRow` bleiben unverändert grün).
25. Alle vier Gates des Repos grün (`dotnet test`, Vitest, E2E, `coverage-local`), plus die
    Live-Verifikation aus Abschnitt 11 mit Zahlen im PR-Text.

---

## 9. Testpyramide

### 9.1 `tests/EmotePurge.Api.Tests`

- Neue Klasse nach dem Muster `SevenTvEmoteSetSyncImportedEndpointTests.cs` (`:44-136`), für
  **beide** Routen (Theory über den Routen-Suffix): 401 anonym; 400 bei 33-stelligem Routenwert vor
  jedem Service-Aufruf; 400 `emote_ids_empty` bei leerer/fehlender Liste; 404 bei `NotFound` des
  Owner-Lookups; 403 bare ohne Body; 503 ohne Service-Aufruf; 200 mit weitergereichter
  Besitzer-Identität und Live-Event je Kanal mit `NewlyChangedCount > 0` (Substitut
  `IRedisPublisher` wie bei den kanalgebundenen Fällen). AK 9, 14.
- `EmoteRoutePolicyTests.cs:33-48`: zwei neue `InlineData` mit `Bookkeeping`.
- `AuthFilterMatrixTests.cs:622-735`: die Leiterfälle 2–4 (Altform + Neuform, `emote_ids_invalid`,
  `emote_set_id_empty`, `invalid_emote_set_id` am Body) entfallen; der Fall „Guid-Form → 200 und
  Guid-Überladung erreicht" bleibt; neu: `{ emoteSetId, sevenTvEmoteIds }` → 400 `emote_ids_empty`
  (AK 23).

### 9.2 `tests/EmotePurge.Infrastructure.Tests`

`EmoteServiceTests.cs`: die set-scoped Fälle (`:378-560`) werden durch die `…InSetAsync`-Fälle
ersetzt, je Richtung: aktives Set eines getrackten Kanals (Zeilen, Audit mit Kanal, Antwort);
geteiltes Set (zwei Kanäle, zwei Einträge, zwei Kanal-Objekte in der Antwort); gesperrter Kanal
ignoriert (echter `ExcludedChannelFilter` aus Konfiguration, `ExcludedChannelFilterTests.cs:77`);
ungetracktes Set nur Papier mit Besitzer; nicht-aktives Set eines getrackten Kanals nur Papier;
`IsBotActive = false` zählt nicht als Treffer; Deduplizierung vor `reportedCount`; ein Kanal ohne
gefundene Zeile → Kanal in der Antwort mit Zähler 0 **und** Papier-Eintrag. Legacy-Fälle
(`:19-215`, `:457-478`) bleiben unverändert. AK 10–13.

### 9.3 Vitest (`web/`)

- `purge-run-export.spec.ts`, `transfer-run-export.spec.ts`: Ziel-Extraktion für alle drei
  Dateiarten (Purge, `planned`, `finished`), `wrongChannel`/`wrongSet`-Fälle entfallen, die
  `readProtocolRow`-Fälle bleiben (AK 24).
- `file-import-step.spec.ts`: die vier Ausgänge der Zielprüfung (AK 3–5), genau ein Request je
  Pick (AK 6), gesperrter Dateiknopf während der Prüfung, späte Antwort nach Abbruch ohne Wirkung
  (F6).
- `restore-flow.spec.ts`: Ziel statt Positionsparameter; Slot-Vorschau-Gabel für die drei Fälle
  (getrackt aktiv / getrackt nicht-aktiv / ungetrackt); `foreignToPage`.
- `seven-tv-restore.service.spec.ts`, `seven-tv-delete.service.spec.ts`,
  `seven-tv-import.service.spec.ts`: Meldung set-zentrisch (AK 7–8); Dreiwertigkeit aus
  `channels` (AK 15) inklusive geteiltem Set (F8); 404 → `failed`; Resync-Regel E12
  (`resyncChannelName` null → kein Aufruf); `resetIfChannelChanged` gegen `hostChannelName`.
- `conflict-resolution.spec.ts`, `import-confirm-dialog.spec.ts`: Replace für ungetracktes Ziel
  gültig und angeboten; kein `replaceNeedsTrackedTarget` mehr (AK 16).
- `emote-admin.service.spec.ts`, `seven-tv-emote-set.service.spec.ts`: Client-Methoden gewandert.
- `api-error-locales.spec.ts` bleibt das Gate für E14.

### 9.4 Playwright E2E

- Neu: Übertragungsdatei (`finished`, ein bestätigter REMOVE) mit ungetracktem Ziel auf der
  Nutzungsseite eines **fremden** Kanals eingelesen → Bestätigung nennt Set und Besitzer und den
  Fremd-Hinweis → Lauf sendet einen `addEmote` an das Ziel-Set → `POST
  /api/seventv/emote-sets/{set}/sync-restored` mit der ID (AK 1, 7, 18, 19); Mocks:
  `mockEmoteSetTargets` mit einem ungetrackten Account, 7TV-GraphQL-Stub für Read und ADD.
- Angepasst: `emote-import.e2e.spec.ts:1835-1919` — das fremde Purge-Protokoll wird nicht mehr über
  `wrongChannel` abgewiesen, sondern über `targetNotEditable` (Zielliste ohne dieses Set);
  `vote-ballot.e2e.spec.ts:389`, `:580`, `:650` routen die set-zentrische Route und prüfen den
  neuen Body (AK 8).
- Läuft nur, wenn auf `:5151` keine Api lauscht (CLAUDE.md).

---

## 10. Nicht in dieser Spec, mit Grund

- **Vorprüfung vor Delete und Replace** analog zur Zielprüfung des Restore (F4): der Delete läuft
  von einer Seite, die der Nutzer nur mit Usage-Stats-Zugriff sieht, der Replace aus dem Picker, der
  dieselbe Liste eben geladen hat — ein dritter Aufruf derselben Liste kaufte nur den Fall „fremdes
  Token", und den entscheidet der Betreiber (Abschnitt 13, Punkt 4).
- **Aufräumen der kanalgebundenen Altform** — Folge-Issue 1 der Spec-200 §21, an sein Tor gebunden.
- **Umbenennung von `IImportTargetOwnershipService`** — Refactoring ohne Vertragswert.
- **#254**, **#255**, **#256** (Punkte 1, 2, 4, 5), **#201** — je eigene Issues.

---

## 11. Live-Verifikation (Regel 16, Betreiber-Handgriff)

Haupt-Checkout, Api per `dotnet run`, `npm start`; danach `dotnet run` beenden, bevor E2E läuft.
Zu belegen im PR-Text mit Zahlen:

1. **Replace in ein ungetracktes Set** (z. B. olafs Set `test`, Konto `olaf_olaf_son`, auf dem der
   Betreiber `editor_of` ist — Plan-230 T10): Picker → ungetracktes Set bestätigen → Schritt 2 bietet
   „Ziel ersetzen" **wählbar** → „Rückweg sichern" liest das Set, Datei auf der Platte (`stage:
   'planned'`, `meta.targetChannelName: null`) → Lauf: REMOVE → ADD benachbart im Netzwerktab →
   Löschmeldung an `POST /api/seventv/emote-sets/<set>/sync-deleted` mit 200 und `channels: []` →
   Ergebnisprotokoll heruntergeladen (`removedTarget.confirmed: true`).
2. **Restore aus dieser Datei auf einer anderen Kanalseite** (z. B. `sensitron`): Datei einlesen →
   ein Request an `/me/emote-set-targets` → Bestätigung nennt Set `test`, Besitzer, Fremd-Hinweis,
   keine „nicht aktiv"-Zeile → Lauf sendet genau die ADDs der Lücke (Regel 4 lässt den geglückten
   Replace aus, Zeile „Name belegt") → Meldung an `…/sync-restored` mit 200, `channels: []` → kein
   Resync-Request → Read-back des Sets zeigt die Lücke geschlossen.
3. **Audit (globale Admin-Ansicht):** drei Einträge ohne Kanal für das Set — `syncImported`,
   `syncDeleted`, `syncRestored` — alle „für olaf_olaf_son", `TargetId` = Set-ID.
4. **Purge-Protokoll mit nicht-aktivem Set:** ein Purge-Protokoll eines Sets, das der Testkanal
   inzwischen **nicht** mehr aktiv hat (Set-Wechsel im 7TV-Web, Resync abwarten), auf der Seite
   dieses Kanals einlesen → nicht abgewiesen → Bestätigung mit „nicht aktiv"-Zeile → Lauf → Meldung
   200 mit `channels: []` → Resync des Kanals ausgelöst → Mitgliederliste der nicht-aktiven Ansicht
   zeigt die Emotes. Danach dasselbe Protokoll auf der Seite eines **anderen** Kanals → dieselbe
   Bestätigung, zusätzlich Fremd-Hinweis.
5. **Negativprobe Rechte:** dieselbe Datei als Konto ohne `editor_of` auf olafs Konto (Zweitkonto)
   einlesen → `targetNotEditable`, kein Lauf, kein Request an 7TV.
6. **Aktives Set des eigenen Kanals** (Regressionsprobe): Delete von zwei Emotes auf der eigenen
   Nutzungsseite → Meldung an `…/sync-deleted` mit `channels: [{ <kanal>, archivedCount: 2 }]`,
   `channel.synced` im SSE-Stream, Raster aktualisiert; „Wiederherstellen" aus dem Dock → Meldung an
   `…/sync-restored` mit `restoredCount: 2`; Audit des Kanals zeigt beide Einträge **mit** Kanal.

---

## 12. DECISIONS-Einträge und Doku-Pflege (Regel 3)

### 12.1 Vier Einträge, die der Plan im jeweils ersten betroffenen Commit liefert

1. **„Die Datei bestimmt das Ziel eines Restore"** — E1/E2: kein Kanal- und Set-Vergleich gegen
   die Seite mehr; Zielprüfung über die Zielliste; persönliche Sets ausgeschlossen; F1 (es gab nie
   ein Protokoll ohne Set-ID) ausdrücklich festgehalten, damit niemand den Fallback nachbaut.
2. **„Delete, Restore und Replace-Löschung melden set-zentrisch"** — E3/E4/E9: die beiden Routen,
   Leiter, Antwortform, Audit je Kanal plus Papier, Live-Event je Kanal; die kanalgebundene
   set-scoped Form entfällt (nie in Produktion), die Guid-Altform bleibt bis zum E3-Tor; #224
   damit geschlossen.
3. **„Wer melden darf, entscheidet die 7TV-Bearbeitungsberechtigung"** — E5/5.7 mit F4 als
   benannter Grenze.
4. **„Die Replace-Sperre für ungetrackte Ziele fällt"** — löst den Absatz „Replace is **only**
   offered for a tracked target" im Eintrag vom 2026-09-23 (DECISIONS `:535-537`) und den Satz
   „There is no restore into an untracked set" (`:696-699`) ab; die Pflicht-Rückweg-Datei bleibt.

Alle vier englisch (Projektsprache seit #152).

### 12.2 Bestehende Dokumente, die im selben Vorgang wandern

- `docs/Architectur.md:68`, `:133`, `:154`, `:202` (F11).
- `src/EmotePurge.Api/Auth/UsageStatsAccessAuthorizationFilter.cs:6-17` (Kommentar).
- `docs/UI-Designsprache.md` §7.3 (Datei-Zweig, Zielprüfung, vierte Einlesesorte bleibt).
- `docs/superpowers/specs/2026-09-20-emote-sets-200-spec.md`: **kein** Umbau; ein Nachtrag am Ende
  („Restore pro Set, 2026-09-24"), der 6.6 auf diese Spec zeigt, wie es die Nachträge 32–39 für
  frühere Runden tun. F7 dort verweist noch auf 6.6 (die in Plan-230 T9 vorgesehene Korrektur auf 6.7 ist nicht gelandet, `:230`); der Nachtrag setzt den Verweis auf 6.7 und auf diese Spec.
- `docs/Feature-Ideen-2026-08-01.md`: keine Idee betroffen.
- Issues: #253 schließt mit dem Merge in den Epic-Branch (von Hand, wie alle Kind-Issues); #224
  wird mit Verweis auf 5.5/6.3 geschlossen; #256 verliert Punkt 3; das Epic #200 bekommt die Zeile.

---

## 13. Offene Punkte für den Betreiber

Jeder Punkt nennt, was das freigegebene Design sagt, was der Code sagt, und eine Empfehlung. Die
Verträge oben sind so geschrieben, als wäre die Empfehlung angenommen; kippt der Betreiber einen
Punkt, ändert sich nur die genannte Stelle.

1. **„Alte Purge-Datei ohne Set-ID" gibt es nicht (F1).** Das Design sieht als dritte Klasse ein
   Protokoll ohne Set-ID vor, das auf das *aktuell aktive* Set des Envelope-Kanals zielt, mit
   Hinweis im Dialog. `meta.emoteSetId` ist seit dem ersten Commit Pflicht, und ein Fehlen ist
   `wrongKind`. Der Fall, den der Halloween-Wechsel wirklich erzeugt, ist ein Protokoll mit
   **inzwischen nicht-aktivem** Set — und den deckt E1 ohne Sonderpfad. **Empfehlung:** die dritte
   Klasse ersatzlos streichen (so in E1/4.1 geschrieben); der Hinweis wird die bestehende „nicht
   aktiv"-Zeile.
2. **Antwort- und Signaturform (E7, E9).** Das Design lässt Namen und DTO offen; ich habe die
   Besitzer-Felder in die Service-Signatur genommen und `channels[]` als Antwort festgelegt.
   **Empfehlung:** annehmen; die Alternative (nur `emoteSetId, sevenTvEmoteIds, actor`) hieße ein
   Papier-Eintrag ohne Besitzer, den die Audit-Ansicht nur als nackte Set-ID zeigen könnte.
3. **`resetIfChannelChanged` „auf das Set umgestellt" (F7, E13).** Der Aufrufer im Layout kennt nur
   den Kanal der Seite; ein Set-Vergleich ist dort nicht ausdrückbar. Zwei gangbare Lesarten: (a)
   Vergleich mit `hostChannelName` — die Seite, auf der der Lauf gestartet wurde; das Dock verhält
   sich wie heute; (b) Reset ganz streichen wie beim Import (R9, `seven-tv-import.service.ts:209-211`)
   — der Restore-Lauf folgt dem Nutzer auf jede Seite; dann müsste das Restore-Dock aus dem
   Set-Gate des `MassDeletePanel` heraus, weil es sonst auf einer Seite ohne aktives Set
   verschwindet. **Empfehlung:** (a), so in 4.4/6.3 geschrieben — kleiner Eingriff, kein neues
   Dock; (b) ist eine eigene UI-Entscheidung, die #255 oder ein Folge-Issue treffen kann.
4. **Rechte-Änderung: der Moderator mit fremdem Token (F4).** Das Design vermutet, dass kein
   heutiger Ablauf bricht. Am Code gibt es genau einen: ein Twitch-Moderator (oder Admin) ohne
   eigenes `editor_of`, der mit dem Token eines berechtigten Kontos löscht, konnte heute melden
   und kann es künftig nicht — sichtbares `failed`, Papierspur weg, Zeilen heilt der Resync. Dieselbe
   Eigenschaft hat `sync-imported` (set-zentrisch) seit K2 unbeanstandet. **Empfehlung:** hinnehmen
   und im DECISIONS-Eintrag 3 benennen; **keine** Vorprüfung vor Delete/Replace (Abschnitt 10).
   Falls der Betreiber Mod-Teams mit geteiltem Token kennt: dann eine Dock-Zeile „Meldung abgewiesen
   — dieses Konto bearbeitet das Set nicht" (#255) statt eines nackten `failed`.
5. **Resync-Regel für den Restore (E12) weicht von der des Imports ab.** Das Design sagt nichts zum
   Resync. Import: nur bei aktivem Set (K2-Befund 3). Restore heute: immer für den Seitenkanal — und
   darüber lädt eine nicht-aktive Ansicht ihre Mitgliederliste nach. **Empfehlung:** E12 wie
   geschrieben (Kanal des Ziel-Accounts, unabhängig von aktiv); wer die Import-Regel bevorzugt,
   nimmt für nicht-aktive Ziele in Kauf, dass die Ansicht erst mit dem Refresh-Knopf nachzieht.
6. **Persönliche Sets (E11).** Design: „Personal-Sets sind aus den Pickern ausgeschlossen" — ohne
   Aussage, was mit einer Datei geschieht, die eines nennt. **Empfehlung:** abweisen, wie
   geschrieben; die Alternative (zulassen) hätte einen Restore-Weg für ein Set, in das EmotePurge
   nie schreibt.
7. **Die Zielprüfung sitzt im `FileImportStep` (E10).** Das Design sagt nur „vor dem Lauf". Die
   Alternative — Prüfung in `startRestoreFlow` nach dem Schließen des Dialogs — bräuchte einen neuen
   Fehler-Ort (Banner auf der Seite oder ein weiterer Dialog). **Empfehlung:** im Schritt, wie
   geschrieben; §7.3 wird entsprechend ergänzt.

---

## 14. Dateireferenz

**Backend:** `src/EmotePurge.Api/Endpoints/SevenTvEndpoints.cs` (zwei Routen in `emoteSetGroup`,
Live-Event) · `src/EmotePurge.Api/Endpoints/EmoteEndpoints.cs` (Leiter zurück auf Guid-Form,
Records) · `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` (`EmoteSetIdEmpty` entfällt) ·
`src/EmotePurge.Api/Auth/UsageStatsAccessAuthorizationFilter.cs` (Kommentar) ·
`src/EmotePurge.Core/Services/IEmoteService.cs` (zwei Methoden, zwei DTO-Familien; set-scoped
Überladungen und `TargetIsActiveSetOfChannel` entfallen) ·
`src/EmotePurge.Infrastructure/Services/EmoteService.cs` (Konstruktor `IExcludedChannelFilter`,
Implementierung) · `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` (unverändert,
sofern die DI den neuen Konstruktorparameter auflöst).

**Frontend:** `web/src/app/shared/export/purge-run-export.ts`, `transfer-run-export.ts` (Parser
ohne `expected`) · `web/src/app/shared/seven-tv/file-import-step.ts` (Zielprüfung,
`ResolvedRestoreTarget`) · `import-source-dialog.ts` (Bindung) · `import-trigger.ts`,
`mass-delete-panel.ts` (Aufrufer von `startRestoreFlow`) · `restore-flow.ts`,
`restore-confirm-dialog.ts` · `web/src/app/core/seven-tv/seven-tv-restore.service.ts`,
`seven-tv-delete.service.ts`, `seven-tv-import.service.ts` (Meldungen, Laufdatensatz, Wächter) ·
`seven-tv-emote-set.service.ts` (zwei Client-Methoden, Antworttypen) ·
`web/src/app/core/emotes/emote-admin.service.ts` (zwei Methoden entfallen) ·
`web/src/app/shared/seven-tv/conflict-resolution.ts`, `import-conflict-resolution-step.ts`,
`import-confirm-dialog.ts` (Regel 7) · `web/src/app/core/i18n/api-error.ts`,
`web/public/i18n/de.json`, `en.json` (Schlüssel) · `web/src/app/features/channel-workspace/channel-workspace-layout.ts`
(unverändert im Aufruf).

**Tests:** 9.1–9.4. **Doku:** 12.1–12.2.

---

## 15. Rückweg

Kein Schema, keine Migration. Backend und Frontend gehen **zusammen** zurück: ein Frontend mit
set-zentrischen Meldungen gegen ein Backend ohne die Routen verlöre jede Papierspur (404 nach der
Mutation), ein Backend mit den Routen ohne Frontend-Aufrufer ist harmlos. Die kanalgebundene
Guid-Altform ist von dieser Spec unberührt und trägt einen Rollback allein. Bereits geschriebene
Papier-Einträge ohne Kanal bleiben lesbar — die Audit-Ansicht kennt die Form seit K2. Eine
heruntergeladene Rückweg-Datei eines Replace in ein ungetracktes Set bleibt nach einem Revert als
JSON lesbar, aber **nicht mehr einlesbar** (der revertierte Parser weist sie mit `wrongChannel`
ab); wer revertiert, revertiert deshalb nicht zwischen einem solchen Replace und seinem Restore —
dieselbe Regel wie in Plan-230 §8.
