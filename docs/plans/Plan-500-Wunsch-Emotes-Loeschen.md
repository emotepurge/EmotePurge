# Plan #500: Wunsch-Emotes, Kind K7a: beendete Wünsche löschen

Erstellt am 2026-11-21 im Worktree `agent-a9d73614672ee1285` (Branch `feat/wish-renew-delete`, Basis `origin/main` @
`ac7982eb`, darin #499, #501, #504; darauf nur die Doku-Commits des Nachtrags K7). Revision 0 und 1 sind an `dbc24973`
gelesen; die von #501/#504 berührten Dateien (`WishTextParser`, Hinweis im Wünsche-Tab) betreffen K7a nicht.
**Status: Revision 2 (2026-11-21), nach Sol adversarial Runde 2 über Revision 1 (1 × P1, 3 × P2, 4 × P3; Tabelle
„Revision 2“). Runde 1 (Revision 1) und die Betreiberentscheide zu Abschnitt 8 sind eingearbeitet. Die Spec ist mit
Revision 6 nachgezogen (Block „Plan-Runde 2“ ihrer Prüfbefunde). Nichts implementiert.**
Quellen: [Spec](../superpowers/specs/2026-11-17-wunsch-emotes-spec.md), Nachtrag „K7“, Teil „K7a: Löschen beendeter Wünsche“
(E32, E34 bis E41, E44, E46, AK109 bis AK119, AK133, AK134, Testplan, Files Reference, Auslieferung, RR15 bis RR18) samt
Betreiberentscheidungen Y2, Y3, O1, O2 und der Tabelle „Prüfbefunde K7“; [Plan-483](Plan-483-Wunsch-Emotes-Backend-Kern.md)
(Sperrkonvention 2.3), [Plan-485](Plan-485-Wunsch-Emotes-Verwalter-UI.md) (2.6 Wunschbindung der Entfernung),
[Plan-486](Plan-486-Wunsch-Emotes-Chat-Erfassung.md) (2.4a Zugriff über die Twitch-ID); DECISIONS „Wish emotes, child 7
(spec addendum)“ vom 2026-11-21; `CLAUDE.md` (Regeln 1 bis 3, 7, 11, 12, 16, 18, Schichtentreue, Tests, Sprache),
`web/.claude/CLAUDE.md`, `docs/UI-Designsprache.md` §4.2, §7, §8.1, §8.7, §11; Issue #500, Epic #481.

**Was dieser Plan trägt:** den Befund am Code, die geteilten Verträge, den Schnitt in Tasks mit Grenzfällen und
Testabsicht, die AK-Zuordnung, die Live-Verifikation, Gates und Auslieferung. **Kein Code**; Namen und Signaturen nur als
Form. Jeder Implementer liest seinen Task hier, die genannten Spec-Festlegungen **und** die genannten Bestandsdateien, bevor
er schreibt. Vor jeder UI-Arbeit `docs/UI-Designsprache.md` §11 und `DESIGN.md`.

**Lesehinweis:** **[E]** Planer-Entscheidung (der Betreiber kann widersprechen) · **[B]** Betreiberantwort vom
2026-11-21 · **[P]** Präzisierung ohne Entscheidungsgehalt · **[W*n*]** Abweichung zwischen Spec und Code aus Abschnitt 0 ·
**PR1-*n*** Befund der Sol-Runde 1 über diesen Plan · „E32“, „AK109“ meinen die Spec.

### Revision 2 (2026-11-21): Sol Runde 2

| Befund | Prüfung am Code | Urteil und Ort |
|---|---|---|
| PR2-1 (P1) E46 prüfte das Alter der 7TV-Beobachtung **vor** der Wunschlesung; eine langsame, unveränderte Wunschantwort (bis 20 s Frist) ließe `REMOVE` auf einer über 5 s alten Beobachtung los | hält: `WISH_OBSERVATION_MAX_AGE_MS` 5 s (`seven-tv-delete.service.ts:140-144`), Altersprüfung in `readAndGateWish` (`:989-1001`), `readWish` mit `WISH_START_READ_TIMEOUT_MS` 20 s | **voll:** 2.7 neu: Altersprüfung über **beide** Beobachtungen nach der Wunschlesung, eine Neulesung der Folge, sonst `tooSlow`; Tests in T5, E2E in T7 |
| PR2-2 (P2) Vor dem Deploy offene Tabs führen die alte Engine ohne E46 weiter | hält: `channel-wishes-page.ts:836-854` lädt bei neuem Stream nur Daten; das Frontend hat keinen Versionsabgleich; die alte Engine wiederholt bis zu fünfmal (`MAX_RATE_LIMIT_RETRIES`) | **Umstellungsregel statt Server-Wächter** (Abschnitt 6): verhältnismäßig für eine Betreiber-Instanz mit wenigen Verwaltern; ein Wächter bräuchte einen Versionsvertrag zwischen Browser und Api, der für einen einzigen Wechsel nichts kauft. Kein Eintrag in `docs/Operations.md` (gilt nur für diesen Wechsel), sondern PR-Text und Deploy-Hinweis |
| PR2-3 (P2) AK112 über ID-Reihenfolge nicht garantiert, geteilte Datenbank | hält: `DataRetentionService.cs:468-487`, `:508-533` (ungeordnete `ExecuteDelete`/`ExecuteUpdate`) | **voll:** T2 legt die beiden Retention-Fälle in die eigene Datenbank der Retention-Tests (`DataRetentionServiceTests`, Fixture-Muster mit Trigger-Helfern) und hält das Kind über einen nur im Test angelegten `AFTER`-Zeilen-Trigger mit Advisory-Sperre; spätere Kategorien Pflicht; kein Rückfall auf nachgestellte Anweisungen |
| PR2-4 (P2) Der nach dem Löschen angestoßene Abgleich kann mit dem Leeren der Einlöser-Daten verklemmen | hält: `WishReconcileService.cs:157-166` sperrt mehrere Zeilen wartend; der Worker fängt einen Abbruch (`DataRetentionWorker.cs:151`) | **eingegrenzt (einfacher Weg):** Zusage auf die Transaktion des Löschens begrenzt, RR19; besteht für jeden Abgleich nach einem Sync schon heute (#496 H7). Kein nachgebender Abgleich in K7a |
| PR2-5 (P3) AK133-Prüfung an einer verschwindenden Zeile | hält: Seite lädt bei Ende der Entfernung und bei `wish.changed` neu (`channel-wishes-page.ts:815-839`) | **voll:** T7 entfernt den alten Wunsch aus Detail und Listen, prüft den Grund im abgeschlossenen Dock und null weitere `REMOVE`/`complete-removal` |
| PR2-6 (P3) Commits vor dem Live-Gate widersprechen Regel 16 | hält: `CLAUDE.md` Regel 16 „vor dem Commit live … verifizieren“ | **voll, wörtliche Lesart:** kein Task committet; Sicherungspunkte als Baum- und Commit-Objekt über einen temporären Index (berührt weder Branch noch den geteilten Stash-Stapel); nach bestandenem T9 entstehen die Commits je Task, deren Baum den in T9 festgehaltenen Baum trifft (Abschnitt 3, T9, T10) |
| PR2-7 (P3) Herkunft des Worker-Images unbelegt | hält: `docker-compose.yml:139-143` baut den Worker aus dem Checkout | **voll:** T9 baut Api aus dem Arbeitsstand und Worker aus `origin/main` (genannter Commit), notiert beide IDs beim Bauen, vergleicht vor L1 mit den laufenden Containern |
| PR2-8 (P3) T5 ohne `SkipReasonKey` | hält: `WISH_BINDING_SKIP_KEY` ist auf `SkipReasonKey` typisiert (`seven-tv-delete.service.ts:1687`, `seven-tv-run-engine.ts:145-160`) | **voll:** T5 erweitert `SkipReasonKey` um `wishes.start.wishChanged` und `wishes.start.wishUnverified` (beide Texte gibt es schon) |

**Aufwand:** 2,75 Tage (Revision 1: 2,5): plus etwa ein Viertel Tag für die Frischeprüfung nach der Wunschlesung und die
Testschranke der Retention.

### Revision 1 (2026-11-21): Sol Runde 1 und Betreiberantworten

| Befund | Prüfung am Code | Urteil und Ort |
|---|---|---|
| PR1-1 (P1) Eine Entfernung in einer 429-Pause übersteht Behalten → Löschen → Übernehmen | hält: das Tor vor jedem Versuch (`seven-tv-delete.service.ts:935-1001`) prüft Token, Set, Generation; die Wunschprüfung (`wishDeviation`, `wish-run-start.ts:218-243`) läuft nur beim Start; `adopt` behält die Generation; die Engine fragt das Tor vor jedem Wiederholungsversuch erneut (`seven-tv-delete.service.ts:790-812`) | **voll:** Spec E46, Vertrag 2.7, neuer Task T5, E2E mit zwei Verwaltern in T7 (AK133) |
| PR1-2 (P1) Zwischengespeicherte Moderatorrechte (10 min) erlauben nach dem Entzug ein hartes Löschen | hält: `ModeratedChannelsProvider.cs:33-51` liest den Cache ohne Prüfung, TTL `Auth:ModCheckCacheTtlMinutes` 10 | **eingegrenzt (Empfehlung):** dieselbe Liste berechtigt heute `DELETE` Abstimmung (`VoteSessionEndpoints.cs:114`), `DELETE` Tag (`EmoteTagEndpoints.cs:112`) und das Entfernen importierter Backfill-Daten (`ChannelEndpoints.cs:417`); K7a löscht nur beendete Wünsche ohne 7TV-Wirkung, das Audit nennt den Akteur, „Rollen-Cache invalidieren“ leert `modlist:` sofort (`ModRoleCache.cs:105`). Frisch fragen hieße je Löschen eine Helix-Pagination und für fail-closed einen neuen Fehlercode (Regel-7-Kette) — für K7a allein unverhältnismäßig. Spec E39 „Grenze“, RR16, #496 H6 (gemeinsam für alle harten Aktionen). Vom Betreiber hingenommen (Abschnitt 8, Entscheid 1) |
| PR1-3 (P2) `EndedAtUtc` als Episodenmarke kann kollidieren | geprüft: kein neues Ende verwendet einen alten Wert (`ArchivedAt` nur, wenn nicht vor `SlotConfirmedAtUtc`, `WishReconcileService.cs:366-369`; jede Rückkehr setzt `SlotConfirmedAtUtc` auf jetzt, `:715-720`); offen nur ein Uhrsprung auf dieselbe Mikrosekunde | **eingegrenzt:** ein Episodenzähler bräuchte Migration und Schreibstellen im Worker; Folge im Restfall begrenzt (ein weiterhin beendeter Wunsch wird gelöscht). Spec E44 „Grenze“, RR17; T1 bekommt den Test „altes `ArchivedAt` nach Rückkehr wird nicht wiederverwendet“ |
| PR1-4 (P2) Die Sperre der Wunschzeile wählt global nach ID | hält: `RowLocks.cs:134-146` | **voll:** neue Sperreinträge mit `ChannelId` (2.2a), Test mit zwei Verbindungen (AK134) in T1; die bestehenden Befehle haben dieselbe Eigenschaft → #496 H5, nicht K7a |
| PR1-5 (P2) T1 kann sein Gate nicht bestehen | hält: `EveryOutcome_HasAMappingCase_InThisTheory` (`WishEndpointsTests.cs:415-422`) verlangt jede `WishOutcome`-Zeile, die Theorie liest immer einen JSON-Rumpf, das Inventar zählt 26 Routen (`:781-792`) | **voll:** T1 trägt `AccessDenied`, seinen `MapResult`-Fall und einen eigenen Test ohne Rumpf; T3 hebt das Inventar auf 27 und prüft 204 getrennt |
| PR1-6 (P2) Bestätigung verspricht bei `kept` ohne Eintrag die Neuverknüpfung | hält: `ReleaseKept` leert `SlotId` und lässt `kept` | **voll:** Text nur bei `kept` mit `slotId`, bedingt formuliert; beide Varianten getestet (T6) |
| PR1-7 (P3) E2E-Mock beweist die Freigabe nicht | hält: `mocks.ts` hält `slots` getrennt mit Halter-IDs | **voll:** T7 sät den Eintrag gehalten, prüft gesperrt, der Mock leert den Halter beim `DELETE`, neue ID geprüft |
| PR1-8 (P3) Live-Gate kann das falsche Image prüfen | hält: `docker-compose.yml:43-46` baut aus dem eigenen Checkout | **voll:** T9 baut und taggt den Kandidaten, startet ohne Neubau, schreibt beide Image-IDs ins Protokoll |
| Nächste Schritte | Rundreise mit Mikrosekunden; echte Retention als erster Sperrinhaber samt Leeren der Einlöser-Daten; Live-Ereignis an einer Seite, die nicht selbst löscht; Coverage-Schwerpunkte; Basis `origin/main` | **voll:** T1/T3 (Rundreise zweigeteilt, weil kein Testprojekt HTTP und Postgres zugleich fährt), T2, T7, Abschnitt 6; Branch auf `dbc24973` neu aufgesetzt |

**Betreiberantworten [B]:** (1) Der Eintrag-Dialog erklärt beim `kept`-Konflikt denselben Weg wie der Übernahme-Dialog.
(2) Nach dem Löschen der angehefteten Zeile: Anheftung leeren, Fokus auf den Lösch-Hinweis mit seinem Knopf „Bestand
übernehmen“. (3) Der Hinweis öffnet den Übernahme-Dialog für das ganze Emote (alle Einträge und Aliase), den frei
gewordenen Eintrag vorausgewählt. (4) Keine defensive Bedingung an `endedAtUtc`: Löschen an jedem beendeten Wunsch.

**Aufwand (Stand Revision 1):** 2,5 Tage statt 1,5 (E46 mit Vitest und E2E etwa ein halber Tag, Kanal-Sperre, schärfere
Nebenläufigkeits- und Rundreisetests, Vorauswahl im Dialog); vom Betreiber angenommen. Revision 2: 2,75 Tage.

---

## 0. Befund am Code

### 0.1 Was die Spec behauptet und am Code hält

| Behauptung der Spec | Prüfung |
|---|---|
| Nur zwei Fremdschlüssel zeigen auf `Wishes`: `MergedIntoWishId` (SET NULL) und `WishSlotExclusions.WishId` (CASCADE), E40 | hält: `AppDbContext.cs` (Wunsch-Block ab `:487`, `:548-551`, `:653-662`); im Modell-Snapshot genau diese beiden `HasOne("…Wish")`; `IX_Wishes_MergedIntoWishId` existiert; das Audit-Log hat keinen FK auf Wünsche |
| Kein neuer Fehlercode nötig (Regel 7) | hält: `invalid_request_body`, `wish_not_found`, `channel_not_found`, `wishes_disabled`, `wish_channel_unbound`, `wish_state_conflict` stehen in `ApiErrorCodes.cs`, `api-error.ts` und beiden Locale-Dateien. E46 nutzt Überspring-Gründe der Lösch-Engine (Übersetzungen), keine Fehlercodes |
| Jeder Eintritt in einen Endzustand setzt `EndedAtUtc` (E44) | hält für alle Schreiber: `StampStateChange` (`WishService.cs:1625`), Abgleich (`WishReconcileService.cs:580`, `:697`), Verfall offener Wünsche (`WishCaptureService.cs:421`), Kanal-Zusammenführung (`ChannelIdentityService.cs:1308-1319`) |
| `SameInstant` vergleicht auf Datenbankgenauigkeit | hält: `WishService.SameInstant` (`:1641`), `internal static`, Mikrosekunden |
| Muster „Pflicht-Query an einem DELETE“ | hält: `DELETE …/rewards/learning?sessionId=` (`WishEndpoints.cs:599-617`, als `string?` gebunden) |
| `WishRewardOutcome.AccessDenied` → 403 ohne Rumpf | hält: `MapRewardResult` mit `Results.Forbid()` |
| Zugriffsprüfung über die Twitch-ID gibt es seit K6 | hält: `WishCaptureAccessFilter` + `ResolveWishCaptureAccessAsync`; `WishService.IsConfirmedOn` (`:1716`) |
| Retention löscht und leert in ungeordneten Stapeln | hält: `DataRetentionService.RunEndedWishesAsync` (`:462`), `RunWishRequestersAsync` (`:502`), je `ExecuteDelete`/`ExecuteUpdate` über eine ID-Liste, jede Anweisung in eigener Autocommit-Transaktion |
| Die Lösch-Engine fragt das Wunsch-Tor vor jedem Versuch, auch nach einer 429-Pause | hält: `createEntryOperation` → `beforeStep` → `bindWishBeforeRemove` (`seven-tv-delete.service.ts:790-812`), Wiederholung in `runWithBackoff` (`seven-tv-run-engine.ts:567-600`) |

### 0.2 Abweichungen und Lücken (mit Entscheidung)

- **[W1] Der 404 auf `complete-removal` liegt nicht in `wish-removal-flow.ts`**, sondern in
  `web/src/app/core/wishes/wish-removal-tracker.ts`: `isFinalRefusal` macht aus 409 **und** 404 `notBooked`, angezeigt in
  `channel-wishes-page.ts` und `wish-operation.ts` mit „Entfernt in 7TV. Der Wunsch wurde inzwischen anders gebucht.“
  **[E]** `notBooked` bekommt eine Unterscheidung „gelöscht“, nur bei 404 mit `wish_not_found`; T4. (Spec Revision 5
  nennt den Ort jetzt richtig.)
- **[W2] Der E2E-Mock ordnet jeden Nicht-GET auf `/{id}` als `update` ein** und hält die Einträge (`slots`) mit Halter
  getrennt von den Listen. T7: Art `delete`, Feld `endedAtUtc` in `MockWishCall`, beim erfolgreichen `DELETE` den Halter
  des Eintrags leeren (PR1-7).
- **[W3] `RowLocks` kennt weder `NOWAIT` noch eine Kanalbedingung** (`LockWishesByIdsAsync` wählt global, PR1-4), und
  SQLSTATE `55P03` wird im Backend noch nirgends behandelt. T1 und T2 legen zwei neue Einträge an (2.2a).
- **[W4] `StageWishAudit` kann kein `fromStatus` tragen.** **[P]** T1 erweitert die Hilfsmethode um ein optionales
  `fromStatus` oder stellt die Details für das Löschen selbst zusammen; kein `previousExpiresAtUtc` (E36).
- **[W5] `LockChannelAsync` liest hinter der Sperre nur Name und Kapazität.** Die erneute Prüfung aus E41 braucht die
  `TwitchChannelId` der gesperrten Zeile; T1 liest sie mit.
- **[W6] Genauigkeit von `endedAtUtc` im Browser.** Datenbank Mikrosekunden, `Date` Millisekunden. **Vertrag:** Der Client
  schickt den String aus dem DTO **unverändert** zurück (T4; Rundreise in T1 und T3).
- **[W7] `wish-row.ts` gruppiert destruktive Aktionen über eine eigene Menge** (`DESTRUCTIVE_ACTIONS`); `delete` muss
  dort hinein (T6).
- **[W8] Die Bestätigung der Seite ist generisch** (`wishes.confirm.${action}`); für `kept` mit `slotId` braucht es einen
  eigenen Text (T6, PR1-6).
- **[W9] Das Wunsch-Tor der Lösch-Engine kennt den Wunsch nicht** (`WishEntryBinding` = Set-ID, Token,
  `verifySetAndToken`, `seven-tv-delete.service.ts:318-324`). E46 erweitert den Vertrag (2.7, T5).
- **[W10] Der Abbildungstest der Api erzwingt jede `WishOutcome`-Zeile und liest immer JSON; das Routen-Inventar zählt
  26** (PR1-5). T1 und T3 sind danach geschnitten.

Keine Abweichung berührt eine Betreiberentscheidung.

---

## 1. Umfang

**Drin:** die Route `DELETE …/wishes/{id:long}?endedAtUtc=` mit eigenem Zugriffsfilter (E41), `IWishService.DeleteAsync`
mit Sperrfolge im eigenen Kanal (E32), Zustand (E34), Episode (E44), Wirkung (E35), Audit und Ereignissen (E36); die
Wunschprüfung vor jedem Entfernversuch in der Lösch-Engine des Browsers (E46); in der Oberfläche die Aktion „Löschen“ im
Bereich „Beendet“ und an der angehefteten Zeile, die Bestätigung in zwei Varianten, der Lösch-Hinweis mit „Bestand
übernehmen“ und Vorauswahl, die neuen Texte im Übernahme- und Eintrag-Dialog, der eigene Grund im Entfernungsbericht (N3);
Tests auf allen Ebenen, Live-Verifikation, Doku-Zeilen.

**Draußen:** K7b „Erneut auf Zeit“ (Y3); `decisionVersion` im Vergleich der Entfernung (eigenes Issue #505, Abschnitt 8 Entscheid 2); die allgemeine
Moderatorprüfung über die ID (#496 Punkt 2 / H3), die Kanalbedingung in den bestehenden Befehlen (H5), eine frische
Rollenprüfung für harte Aktionen (H6); jede Migration, jeder Schalter, jede Änderung an Worker, Compose, Runbook, Retention;
Löschen in `my-wishes`, „Handlungsbedarf“ oder „Laufend“.

---

## 2. Verträge, die mehrere Tasks teilen

### 2.1 Route

| | |
|---|---|
| Route | `DELETE /api/channels/{channelName}/wishes/{id:long}?endedAtUtc=<ISO-8601>` in der bestehenden Gruppe (Namensfilter, Verwalter-Filter), dahinter der Zugriffsfilter aus 2.4 |
| Rate-Limit | `Bookkeeping` |
| Query | `endedAtUtc` als `string?` gebunden; fehlt oder unlesbar → 400 `invalid_request_body`. Gelesen invariant als Zeitpunkt in UTC mit voller Genauigkeit (Muster `AdminLogEndpoints`/`ChannelEndpoints`: `AdjustToUniversal`, `AssumeUniversal`); ein Wert ohne Zone gilt als UTC [E] |
| Erfolg | 204 ohne Rumpf; der Handler antwortet selbst, `MapResult` behandelt einen Erfolg ohne Wunsch weiter als unerreichbar |
| Fehler in Reihenfolge | 400 Name (Gruppe) → 401/403 Verwalter (Gruppe) → 404 `wishes_disabled` (Zugriffsfilter) → 404 `channel_not_found` (Filter) → 409 `wish_channel_unbound` (Filter) → 403 ohne Rumpf (Filter) → 400 `invalid_request_body` (Handler, Query) → Dienst: 404 `channel_not_found`, 403 (`AccessDenied`), 404 `wish_not_found` (auch Wunsch eines fremden Kanals, ohne Warten), 409 `wish_state_conflict` |

**[E] Query nach dem Filter:** Ein abgeschaltetes Feature oder ein fremder Kanal antwortet nicht mit einer
Query-Fehlermeldung (Grundsatz „Schalter vor Rumpf“, `SwitchOff_ChecksTheSwitch_BeforeTheBody`).

### 2.2 `IWishService.DeleteAsync`

**Form:** `DeleteAsync(channelName, wishId, expectedEndedAtUtc, actor, access, cancellationToken)` →
`Task<WishCommandResult>`; `access` ist Pflicht. `WishOutcome` bekommt `AccessDenied`. Erfolg trägt weder Wunsch noch
Einstellungen.

**Ablauf, in dieser Reihenfolge, in einer Transaktion:**

1. Kanalzeile `access.ChannelId` sperren (`LockChannelsByIdsAsync`, wartend). Weg → `ChannelNotFound`.
2. Hinter der Sperre Name und `TwitchChannelId` lesen; `IsConfirmedOn(access, rowTwitchChannelId)` falsch →
   `AccessDenied`, nichts geschrieben (E41, AK114). Steht vor der Suche nach dem Wunsch, damit ein abgewiesener Aufrufer
   nicht erfährt, ob die ID existiert.
3. Wunschzeile **im eigenen Kanal** wartend sperren (2.2a, PR1-4), danach lesen. Nicht gesperrt (fehlt oder fremder
   Kanal) → `WishNotFound`.
4. `WishStatus.IsEnded(status)` falsch → `StateConflict`.
5. `SameInstant(wish.EndedAtUtc, expectedEndedAtUtc)` falsch → `StateConflict` (E44).
6. IDs der Wünsche mit `MergedIntoWishId = wishId` lesen (ohne Tracking, nur IDs) und sie im eigenen Kanal aufsteigend
   **mit `NOWAIT`** sperren (2.2a). SQLSTATE `55P03` → Rücknahme der Transaktion, Tracker leeren, `StateConflict`.
7. Audit `wish.delete` stagen (2.3), die Wunschzeile entfernen, speichern, committen. SET NULL und Kaskade erledigt die
   Datenbank; **die Kinder werden nie als Entitäten geladen**, sonst setzte EF den Fremdschlüssel selbst [P].
8. Nach dem Commit: `wish.changed` (bestes Bemühen); war der Wunsch `kept` mit `SlotId`, den Abgleich anstoßen.

**2.2a `RowLocks`, zwei neue Einträge:** „Wunschzeilen eines Kanals `FOR UPDATE`, aufsteigend“ (T1) und dasselbe mit
`NOWAIT` (T2). Beide nehmen die Kanal-ID und tragen sie in der Bedingung, gleiche Regeln wie die bestehenden Einträge
(offene Transaktion Pflicht, IDs heraus, keine Entitäten). Die Klassendoku sagt, warum: eine Sperre darf nie eine Zeile
eines Kanals treffen, den der Aufrufer nicht gesperrt hat; und `NOWAIT` gilt nur für Zeilen, die nach einer gehaltenen
Wunschzeile genommen werden (E32). `LockWishesByIdsAsync` bleibt unverändert (H5).

### 2.3 Audit und Ereignisse

`AuditActions.WishDelete = "wish.delete"` mit Kommentar. Ziel (`wish`, ID), Akteur der Verwalter, Details genau `wishId`,
`sevenTvEmoteId`, `durationDays`, `fromStatus`; nie Alias, Emote-Name, Login, Einlösetext, kein `previousExpiresAtUtc`.
Im Web: `audit.model.ts`, `audit-actions.ts`, beide Locale-Dateien.

### 2.4 Zugriffsfilter (E41)

**[E] Eigene Klasse `WishIdAccessFilter`** in `src/EmotePurge.Api/Auth/`, Variante von `WishCaptureAccessFilter` ohne den
Erfassungsschalter und ohne die GET-Ausnahme: `Wishes:Enabled` aus → 404 `wishes_disabled`; Name ungültig → 400; ohne
Prinzipal → 401; Zugriff null → 404 `channel_not_found`; Zeile ohne ID → 409 `wish_channel_unbound`; nicht bestätigt →
403 ohne Rumpf; sonst Zugriff in `HttpContext.Items`. Gemeinsame Teile dürfen in einen internen Helfer wandern; das
Verhalten von `WishCaptureAccessFilter` bleibt bitgleich. Kein `WishSettingsAuthorizationFilter` (E39). Der Filter fragt
dieselbe zwischengespeicherte Liste der moderierten Kanäle wie alle Verwalter-Routen (RR16, vom Betreiber hingenommen, Abschnitt 8 Entscheid 1).

### 2.5 Client-Verträge

- `WishService.deleteWish(channelName, wishId, endedAtUtc)` → `Observable<void>`, Query über `HttpParams`, **der
  DTO-String unverändert** ([W6]).
- 404 mit `wish_not_found` auf das eigene Löschen gilt als erledigt: keine Fehlermeldung, Listen neu laden (E38). 404 mit
  anderem Code, 403, 409 und Netzfehler zeigen die Fehlermeldung der Zeile wie heute; 409 `wish_state_conflict` lädt
  zusätzlich neu.
- Entfernungsbericht ([W1]): 404 mit `wish_not_found` → Endzustand „nicht gebucht, gelöscht“ mit eigenem Text („Aus 7TV
  entfernt. Der Wunsch wurde inzwischen gelöscht.“, Sinn fest); 409 und 404 mit anderem Code bleiben `notBooked`.
- `WishAdoptDialogData` bekommt ein optionales Feld für den vorauszuwählenden Eintrag [B3]; der Dialog wählt ihn nur, wenn
  der Eintrag in der geladenen Liste wählbar ist (frei oder mit einem laufenden Wunsch verknüpfbar), sonst wählt er nichts
  vor und der Eintrag zeigt seinen Sperrgrund.

### 2.6 Oberfläche (Absicht)

- **Aktionen** (`wishActions`): `kept` → `delete`; `removed`, `removed_externally` → `reAdd`, `delete`; `rejected`,
  `expired`, `closed`, `merged` → `delete`; **ohne weitere Bedingung** [B4]. `delete` ist keine 7TV-Aktion (bleibt auf
  grobem Zeiger sichtbar, F19), bestätigungspflichtig und destruktiv ([W7]). `OFFERED_ACTIONS` nimmt `delete` auf.
- **Orte:** Bereich „Beendet“ und angeheftete Zeile (`?wish=`).
- **Bestätigung** über `openConfirmDialog`: Der Wunsch verschwindet endgültig samt Wünschendem und Einlösetext; das
  Aktivitätsprotokoll behält die Aktionen; in 7TV ändert sich nichts. **Nur bei `kept` mit `slotId`** zusätzlich, bedingt
  formuliert: Das Emote bleibt im Set; der Eintrag lässt sich danach in der Regel mit „Bestand übernehmen“ und neuer
  Laufzeit neu verknüpfen (ordnet der Abgleich ihn vorher einem offenen Wunsch zu, zeigt der Dialog das). `kept` ohne
  `slotId` bekommt den allgemeinen Text (PR1-6). Knopf „Löschen“ ohne „…“. Gesendet wird das `endedAtUtc` der Zeile, die
  die Bestätigung zeigte (beim Öffnen festgehalten).
- **Lösch-Hinweis** (Form wie `mergedNotice`, schließbar): nach dem Löschen eines `kept` mit `slotId` mit Text und Knopf
  „Bestand übernehmen“ (`wishes.page.header.adopt` wiederverwenden), der `openAdopt` mit der `sevenTvEmoteId` des
  gelöschten Wunsches und dessen `slotId` als Vorauswahl öffnet [B3]; nach dem Löschen der angehefteten Zeile ohne diesen
  Weg nur „Wunsch gelöscht“ [E, folgt aus B2]. Der Hinweis verschwindet beim Kanalwechsel, beim Öffnen des Dialogs und beim
  nächsten eigenen Löschen.
- **Fokus danach** (§11, `web/.claude/CLAUDE.md`): im Bereich „Beendet“ auf die nächste Zeile, sonst die vorige, sonst die
  Überschrift `#wishes-ended-title` (ihr Knopf). An der angehefteten Zeile: Anheftung leeren (kein „nicht mehr
  vorhanden“), Fokus auf den Lösch-Hinweis, bei vorhandenem Knopf auf diesen [B2].
- **Texte:** `wishes.adopt.locked.kept` und `wishes.entry.conflict.kept` erklären denselben Weg: „Zum Wunsch“, dort
  löschen, dann den Eintrag mit neuer Laufzeit übernehmen [B1].
- **Mobil:** Aktion in der Zeile wie die übrigen, Bestätigung als Sheet, Fokus sichtbar über der Aktionsleiste (#497,
  #498).
- **i18n (de, en):** `wishes.actions.delete`, `wishes.confirm.delete`, `wishes.confirm.deleteKept`, Lösch-Hinweis (beide
  Varianten, Schließen), `wishes.remove.notBookedDeleted`, neue Texte `wishes.adopt.locked.kept` und
  `wishes.entry.conflict.kept`, `audit.actions.wishDelete`. Die beiden Überspring-Gründe aus 2.7 nutzen die vorhandenen
  Texte `wishes.start.wishChanged` und `wishes.start.wishUnverified`.

### 2.7 Wunschprüfung vor jedem Entfernversuch (E46)

- **Vertrag `WishEntryBinding`** (`seven-tv-delete.service.ts:296-324`) bekommt eine zweite Prüfung `verifyWish()` →
  `Observable<WishBindingSkipReason | null>`, mit demselben Nie-Fehler-Vertrag wie `verifySetAndToken` (Fehler, leere
  Vollendung → `wishUnverified`; eine Frist setzt der Aufrufer). `WishBindingSkipReason` wird um `wishChanged` und
  `wishUnverified` erweitert, `WISH_BINDING_SKIP_KEY` um ihre Texte, `SkipReasonKey` (`seven-tv-run-engine.ts`) um
  `wishes.start.wishChanged` und `wishes.start.wishUnverified`.
- **Ort und Reihenfolge im Tor (PR2-1):** in `readAndGateWish`: (1) frische Set-Lesung, (2) der Eintrag steht an seiner
  Generation (`bindEntry` → `proceed`), (3) `verifyWish()`, (4) **Altersprüfung über beide Beobachtungen**: die
  Set-Beobachtung (wie heute, ab dem Start der Anfrage, die die Seite mit dem Eintrag lieferte) und die Wunschlesung (ab
  dem Start ihrer Anfrage) sind beide höchstens `WISH_OBSERVATION_MAX_AGE_MS` alt; ist eine älter, läuft (1) bis (4) genau
  einmal neu, beim zweiten Mal Überspringen mit `tooSlow`; (5) letzte Token-Prüfung; (6) `gateEntry`. Die bisherige
  einmalige Neulesung bei zu alter Set-Beobachtung geht in dieser einen Neulesung auf. Für Zeilen, deren Eintrag laut
  Lesung weg oder umbenannt ist, entfallen (3) und (4). Weil die Engine das Tor vor jedem Versuch fragt, läuft alles auch
  nach jeder 429-Pause erneut.
- **Bau in `wishEntryRow`** (`wish-removal-flow.ts`): `readWish(wishService, channelName, wishId)` mit der bestehenden
  Frist, dann `wishDeviation('remove', frozen.wish, read)`; `wishChanged` → `wishChanged`, `wishUnverified` →
  `wishUnverified`, `null` → durch. Die Abhängigkeiten von `wishEntryRow` wachsen um den `WishService`.
- **Keine `decisionVersion`** (#505). Restfenster zwischen Prüfung und Mutation: höchstens 5 s plus die Mutation (RR18).

---

## 3. Tasks

Je Task ein Subagent mit frischem Kontext, **strikt nacheinander** (die Backend-Tasks teilen `obj/`, die Frontend-Tasks
die Locale-Dateien, die E2E-Suite den Port). Jeder Backend-Task endet mit grünem `dotnet build EmotePurge.slnx`,
`dotnet test EmotePurge.slnx` (Docker läuft) und `dotnet format EmotePurge.slnx --verify-no-changes`, jeder
Frontend-Task mit `npm --prefix web test -- --watch=false`, `npm --prefix web run lint` und
`npm --prefix web run format:check`. Test zuerst: Jeder Task schreibt seine Tests, sieht sie rot, implementiert, sieht
sie grün. **Nie `docker compose` aus dem Worktree.** Kommentare, Logs, Commit-Texte englisch.

**Commits erst nach dem Live-Gate (Regel 16, PR2-6).** `CLAUDE.md` verlangt die Live-Verifikation von Backend-Features
**vor dem Commit**; die wörtliche Lesart gilt. Kein Task committet. Jeder Task meldet seine Dateiliste und legt am Ende
einen Sicherungspunkt an: Baum des Arbeitsstands samt neuer Dateien über einen temporären Index (`GIT_INDEX_FILE` auf
eine Datei im Scratchpad, `git add -A`, `git write-tree`, `git commit-tree` mit `HEAD` als Eltern). Das erzeugt nur
Objekte, berührt weder den echten Index noch den Branch noch den mit anderen Sitzungen geteilten Stash-Stapel
(`git stash create` schiede aus, weil es neue Dateien nicht mitnimmt); der Orchestrator führt die SHAs in seinem
Protokoll. Nach bestandenem T9 erstellt T10 aus dem
geprüften Arbeitsstand die Commits **je Task** in der Reihenfolge T1 bis T8 mit den unten genannten Texten (Regel 2:
logisch getrennt), dateigenau gestagt; Dateien, die zwei Tasks ändern (Locale-Dateien, `WishEndpointsTests.cs`), landen
vollständig im Commit des späteren Tasks, der sie ändert [E] (Teilstände aus dem Sicherungspunkt zu rekonstruieren wäre
fehleranfällig). Der Baum des letzten Commits muss dem in T9 festgehaltenen Baum gleichen.

| Task | Inhalt | Modell |
|---|---|---|
| T1 | Core-Vertrag samt `AccessDenied`-Abbildung, Sperreintrag im Kanal, `WishService.DeleteAsync` (ohne Kindersperre), Integrationstests | opus |
| T2 | `NOWAIT`-Sperre der Kinder, `55P03`, Rennen mit zweitem `DELETE`, echter Retention-Lauf, Identitätswechsel | opus |
| T3 | Api: `WishIdAccessFilter`, Route, 204, Inventar 27, Filter-Matrix, Rundreise, DECISIONS-Absatz | sonnet |
| T4 | Web-Kern: Aktion, Dienstmethode, Entfernungsbericht „gelöscht“, Audit-Aktion, alle i18n-Schlüssel | sonnet |
| T5 | Lösch-Engine: Wunschprüfung vor jedem Entfernversuch (E46) | opus |
| T6 | Web-Seite: Löschen, Bestätigung in zwei Varianten, Fokus, Lösch-Hinweis, Vorauswahl im Dialog | opus |
| T7 | E2E (AK116, AK118, AK133) und UI-Audit-Lauf | sonnet |
| T8 | Doku und Coverage | sonnet |
| T9 | Live-Verifikation (Abschnitt 5) | Orchestrator mit Betreiber |
| T10 | Gates, Zweitmeinung, PR (Abschnitt 6) | Orchestrator |

Abhängigkeiten: T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8 → T9 → T10. T5 hängt fachlich nur an T4 (Übersetzungen der Gründe)
und darf vor T6 laufen; ein Befund dort ändert sonst spät die Seite.

### T1: Löschen im Dienst

- **Ziel:** 2.2 Schritte 1 bis 5, 7, 8; der wartende Sperreintrag aus 2.2a; 2.3 im Backend; `AccessDenied` bis zur
  Abbildung, damit die Suite grün bleibt (PR1-5).
- **Dateien:** `src/EmotePurge.Core/Entities/AuditLogEntry.cs`, `src/EmotePurge.Core/Services/IWishService.cs`
  (`DeleteAsync`, `WishOutcome.AccessDenied`, XML-Doku mit Ausgängen), `src/EmotePurge.Infrastructure/Persistence/RowLocks.cs`
  (Eintrag „im Kanal, wartend“), `src/EmotePurge.Infrastructure/Services/WishService.cs` (Methode, Klassendoku „Locks“,
  [W4], [W5]), `src/EmotePurge.Api/Endpoints/WishEndpoints.cs` (nur der `MapResult`-Fall `AccessDenied` →
  `Results.Forbid()` und die Doku), `tests/EmotePurge.Api.Tests/WishEndpointsTests.cs` (nur die Abbildung); neu
  `tests/EmotePurge.Infrastructure.Tests/Integration/WishServiceTests.Delete.cs` (Teildatei, Seed-Helfer der Klasse).
- **Tests zuerst (Regel 11):**
  - je Endzustand (sieben, Theorie) mit passendem Ende → Erfolg, Zeile weg, genau ein `wish.delete` mit `fromStatus`,
    Details-Schlüssel genau die vier aus 2.3; je übrigem Zustand (sechs) → `StateConflict`, Zeile und Audit unverändert;
  - Ende mit Mikrosekunden ungleich null über die echten Dienste erzeugt und **aus Postgres gelesen** (`GetAsync`), als
    ISO-String formatiert und zurückgelesen → Erfolg; derselbe Wert plus eine Mikrosekunde → `StateConflict` (AK109,
    Rundreise Teil Postgres);
  - Episoden (AK109): `removed_externally` (T1) → Rückkehr durch den Abgleich → erneut `removed_externally` (T2 > T1) → mit
    T1 Konflikt, mit T2 Erfolg; `removed` → `ReAddAsync` → erneut `removed` → erstes Ende Konflikt; `removed_externally`
    → `removed` über `CompleteRemovalAsync` → gezeigtes Ende Erfolg. Über die echten Dienste, Zeit mit dem `TimeProvider`
    der Fixture vorgerückt;
  - **altes `ArchivedAt` nach Rückkehr (PR1-3):** Slot archiviert mit Stempel A → `removed_externally` (Ende A) →
    Rückkehr → der Slot wird erneut archiviert, behält aber in der Zeile noch A (von Hand gesetzt) → das neue Ende ist
    nicht A (der Abgleich nimmt jetzt) → `DELETE` mit A → Konflikt;
  - zweites Löschen → `WishNotFound`; Wunsch eines anderen Kanals → `WishNotFound`; `CompleteRemovalAsync` auf einen
    gelöschten `removed_externally`-Wunsch → `WishNotFound` (AK111, zweiter Satz);
  - **Sperre im eigenen Kanal (AK134, PR1-4), zwei Verbindungen:** eine zweite Verbindung hält eine Wunschzeile eines
    fremden Kanals → `DeleteAsync` mit deren ID antwortet `WishNotFound` innerhalb einer kurzen Zeitgrenze und sperrt sie
    nicht (die zweite Verbindung kann danach ungestört weiterschreiben; Probe per `NOWAIT` von dritter Seite während des
    Aufrufs ist nicht nötig, die Zeitgrenze genügt);
  - Wirkung (AK110): Sperrzeilen weg; ein Kind bleibt `merged` mit `MergedIntoWishId` null, sonst unverändert; Löschen
    eines `merged` lässt das Ziel unverändert; `kept` mit Slot gelöscht → `ListAdoptableSlotsAsync` ohne Halter; `adopt`
    ohne anderen laufenden Wunsch → Erfolg, mit laufendem zweitem Alias → `AlreadyRunning`; Abgleich-Auslöser genau einmal
    mit der Kanal-ID, nicht bei `kept` ohne Slot oder anderem Zustand; danach den Abgleich direkt laufen lassen: ein
    `approved` Wunsch desselben Emotes mit passendem Fenster bekommt den Eintrag;
  - Dedupe: erfasster Wunsch gelöscht, dieselbe Beobachtung erneut durch den Erfassungsdienst → kein neuer Wunsch
    (Helfer aus `WishCaptureServiceTests.Dedupe.cs`);
  - genau ein `wish.changed` je Erfolg, keines bei Ablehnung; ein werfender Publisher macht den Commit nicht rückgängig;
  - Zugriff: andere `TwitchChannelId` oder `IdConfirmed` falsch → `AccessDenied`, nichts geschrieben, kein Audit, kein
    Ereignis; unbekannte ID mit falschem Zugriff → `AccessDenied`;
  - Audit-Datenschutz (AK115): „Secret…“-Werte für Alias, Namen, Login, Text, `DetailsJson` enthält keinen;
  - Api-Abbildung: `AccessDenied` steht in der Abdeckungsprüfung der Outcomes, hat aber einen **eigenen** Test (403, leerer
    Rumpf), weil die bestehende Theorie immer JSON liest.
- **Grenzfälle:** Kanalzeile während des Wartens gelöscht → `ChannelNotFound`; Wunsch nach einer Kanal-Zusammenführung beim
  Überlebenden, der Zugriff nennt den Verlierer → `ChannelNotFound` oder `WishNotFound`, nie ein Löschen am falschen Kanal.
- **AKs:** AK109, AK110, AK111 (zweiter Satz), AK114 (ohne Rennen), AK115 (zweiter Teil), AK134, AK113 (Abbildung).
- **Commit-Text (nach T9):** `feat: delete an ended wish in the wish service`

### T2: Sperren und Rennen

- **Ziel:** 2.2 Schritt 6, der `NOWAIT`-Eintrag aus 2.2a, Nachweis von E32 und AK111, AK112, AK114 mit mehreren
  Verbindungen.
- **Dateien:** `RowLocks.cs`, `WishService.cs` (Schritt 6, `55P03`, Doku); neu
  `tests/EmotePurge.Infrastructure.Tests/Integration/WishServiceTests.DeleteRaces.cs` (Rennen ohne Retention, geteilte
  Datenbank der Wunsch-Tests) und neu `tests/EmotePurge.Infrastructure.Tests/Integration/DataRetentionServiceTests.WishDelete.cs`
  (die Retention-Fälle in der **eigenen Datenbank** der Retention-Tests, Teildatei von `DataRetentionServiceTests`, weil die
  Retention über alle Zeilen arbeitet; deren Trigger-Helfer `InstallFailureTriggerAsync` als Vorbild). Vorbilder für das
  Warten: `Fixtures/PostgresLockProbe.cs` (`pg_stat_activity`, `wait_event_type = 'Lock'`), `Fixtures/CommandHook.cs`.
- **Tests zuerst:**
  - zwei gleichzeitige Löschungen desselben Wunsches → ein Erfolg, ein `WishNotFound`, ein Audit-Eintrag;
  - **AK112 a, der echte Retention-Lauf hält als Erster genau ein Kind (PR2-3):** Testschranke statt ID-Reihenfolge. Der
    Test legt einen Zeilen-Trigger `AFTER DELETE` (Fall 4c, Ende vor 365 Tagen) bzw. `AFTER UPDATE` (Fall 4d, Leeren der
    Einlöser-Daten) an, der nur für das Kind K feuert und auf eine Advisory-Sperre wartet, die der Test vorher nimmt. Wenn
    der Trigger läuft, hat die Retention-Anweisung K schon geändert und hält die Zeile bis zum Ende ihrer Transaktion (ein
    `BEFORE`-Trigger liefe vor der Zeilensperre und bewiese nichts). Ablauf: Advisory-Sperre nehmen →
    `DataRetentionService.RunAsync(enforce: true)` auf eigener Verbindung starten → warten, bis sie an der Advisory-Sperre
    wartet (`pg_stat_activity`, `wait_event_type = 'Lock'`, `advisory`) → mit einer `NOWAIT`-Probe von dritter Seite
    belegen, dass K gesperrt ist → `DeleteAsync` des Ziels antwortet `StateConflict` innerhalb einer kurzen Zeitgrenze,
    Ziel unverändert, kein Audit → Advisory-Sperre lösen → die Retention endet ohne Ausnahme; die gesäten fälligen Zeilen
    der späteren Kategorien sind entfernt, **alle drei Pflicht**: eine Erfassungslücke, ein deaktivierter Kanal, eine
    Systemlast-Zeile (Seed-Helfer der Retention-Tests, z. B. `SystemLoadSampleSeed`). Trigger und Funktion werden am Ende
    entfernt;
  - **AK112 a, Gegenrichtung** (ebenfalls Retention-Datenbank): per `CommandHook` am Kontext des Löschens hinter der
    Kindersperre anhalten (Kanal, Ziel und Kind gesperrt), die Retention starten, warten bis sie blockiert, fortsetzen → das
    Löschen gelingt, die Retention endet ohne Ausnahme, keine `40P01`;
  - **AK112 b, Ziel gesperrt:** Schranke wie oben, aber für das Ziel → das Löschen wartet sichtbar an der Wunschzeile;
    Schranke lösen → die Retention hat das Ziel gelöscht → `WishNotFound`; Gegenprobe mit einer Retention, die das Ziel nur
    leert (4d) → Erfolg;
  - **AK114 unter Sperre:** Der Aufrufer hält einen Zugriff auf die alte `TwitchChannelId`; eine zweite Verbindung sperrt die
    Kanalzeile, ändert die ID, hält offen → das Löschen wartet an der Kanalsperre; nach dem Commit → `AccessDenied`, nichts
    geschrieben;
  - ein `NOWAIT`-Fehlschlag lässt den Scope sauber: derselbe Dienst löscht danach erneut erfolgreich.
- **Nicht getestet, bewusst:** die Verklemmung zwischen dem nach dem Löschen angestoßenen Abgleich und der Retention
  (RR19, PR2-4) — eingegrenzt, nicht behoben.
- **Grenzfälle:** keine Kinder (keine Anweisung); viele Kinder (eine Anweisung); ein Kind zwischen ID-Lesen und Sperre von
  der Retention gelöscht (fehlt im Ergebnis, kein Fehler).
- **AKs:** AK111 (erster Satz), AK112, AK114.
- **Commit-Text (nach T9):** `feat: lock merged children without waiting when deleting a wish`

### T3: Api

- **Ziel:** 2.1, 2.4.
- **Dateien:** neu `src/EmotePurge.Api/Auth/WishIdAccessFilter.cs` (ggf. gemeinsamer Helfer), `WishEndpoints.cs` (Route,
  Klassendoku), `tests/EmotePurge.Api.Tests/WishEndpointsTests.cs` bzw. neu `WishDeleteEndpointsTests.cs` [E, wenn die
  Datei zu groß wird]; `docs/DECISIONS.md` (Absatz „Implementation (#500)“ im Eintrag vom 2026-11-21, nur Abweichungen
  vom Spec-Stand, `**Betrifft:**`-Zeile ergänzen; Regel 3).
- **Tests zuerst (Regel 11, neuer `IEndpointFilter`):**
  - die Route in die `Routes`-Theorie (Methode `DELETE`, gültige Query), damit 400 Name, 401, 403 Nicht-Verwalter, 404
    `wishes_disabled` für Verwalter, 403 für Nicht-Verwalter bei ausgeschaltetem Schalter gelten (AK113); 7TV-Editor ohne
    Mod-Status → 403;
  - **Inventar** im Schichtentest von 26 auf 27 (Kommentar mitziehen);
  - gleicher Login, andere Twitch-ID mit echtem `ChannelAccessService` (Muster `WishCaptureEndpointsTests.cs:605-644`) → 403
    und kein Aufruf; passende ID → 204;
  - Zeile ohne Twitch-ID → 409 `wish_channel_unbound`; unbekannter Kanal → 404 `channel_not_found`; jeweils kein Aufruf;
  - Bot-Modus, bestätigter Mod ohne Aktivierung → 204 (E39);
  - Query fehlt, leer, unlesbar → 400 `invalid_request_body`, kein Aufruf; Schalter aus → 404 vor 400;
  - **Erfolg → 204 mit leerem Rumpf**, getrennt von `MapResult` geprüft;
  - **Rundreise Teil HTTP:** ein `WishDto` mit `EndedAtUtc` mit Mikrosekunden ungleich null über `GET /{id}` serialisiert,
    genau dieser String als Query an `DELETE` → der Dienst bekommt einen Zeitpunkt mit identischen Ticks und als UTC;
    derselbe String plus eine Mikrosekunde → ein abweichender Zeitpunkt (AK109).
- **Gate zusätzlich:** `api-error-locales.spec.ts` grün (AK117).
- **AKs:** AK113.
- **Commit-Text (nach T9):** `feat: expose deleting an ended wish`

### T4: Web-Kern

- **Ziel:** 2.3 (Web), 2.5 ohne die Dialog-Vorauswahl, die reinen Regeln aus 2.6, **alle** i18n-Schlüssel aus 2.6.
- **Dateien:** `web/src/app/core/wishes/wish-view.ts`, `wish.service.ts`, `wish-removal-tracker.ts` ([W1]),
  `web/src/app/features/wishes/wish-operation.ts`, `web/src/app/core/audit/audit.model.ts`,
  `web/src/app/shared/audit/audit-actions.ts`, `web/public/i18n/de.json`, `en.json`; co-located Specs.
- **Tests zuerst (Regel 12):**
  - `wishActions` als Tabelle über alle dreizehn Zustände, `delete` zuletzt, `kept` nur `delete`, auch ohne
    `endedAtUtc` [B4]; laufende und offene unverändert; `isSevenTvAction('delete')` falsch, `isConfirmedAction('delete')`
    wahr;
  - `deleteWish`: `DELETE` an den richtigen Pfad, Query gleich dem String Zeichen für Zeichen (sieben Nachkommastellen,
    `Z`), kein Rumpf;
  - Entfernungsbericht: 404 `wish_not_found` → „gelöscht“; 404 anderer Code und 409 → `notBooked`; Zeilentexte verschieden;
  - Audit: `wish.delete` hat einen Schlüssel (bestehende Vollständigkeitsprüfung).
- **Nebenwirkung prüfen:** `wish-entry-dialog.ts` und `wish-adopt-dialog.ts` fragen `wishActions(...).includes(...)`;
  `delete` ändert daran nichts.
- **AKs:** AK116 (Aktionsliste, Entfernungsbericht), AK117.
- **Commit-Text (nach T9):** `feat(web): add the delete action and its client contract`

### T5: Lösch-Engine, Wunschprüfung vor jedem Versuch (E46)

- **Ziel:** 2.7.
- **Dateien:** `web/src/app/core/seven-tv/seven-tv-delete.service.ts` (Vertrag, Tor samt Reihenfolge aus 2.7, Gründe,
  Doku der Bindung), `web/src/app/core/seven-tv/seven-tv-run-engine.ts` (`SkipReasonKey` um die beiden vorhandenen
  Schlüssel, PR2-8), `web/src/app/features/wishes/wish-removal-flow.ts` (`wishEntryRow`, Abhängigkeiten), Aufrufer von
  `wishEntryRow` anpassen; Specs `seven-tv-delete.service.spec.ts` (bzw. die bestehende Spec-Datei der Wunschbindung) und
  `wish-removal-flow.spec.ts`.
- **Tests zuerst (Regel 12, Verhalten):**
  - Tor: Wunschprüfung liefert `wishChanged` → kein `REMOVE`, Zeile übersprungen mit dem Grund; `wishUnverified` (Fehler,
    leere Vollendung) → ebenso; `null` → `REMOVE` geht; die Prüfung läuft nach der Set-Lesung und vor dem Senden, und sie
    läuft **vor jedem Versuch erneut**: erster Versuch 429, in der Pause wechselt die Prüfung auf `wishChanged` → nach der
    Pause kein `REMOVE` (AK133, Vitest);
  - **Frische nach der Wunschlesung (PR2-1), mit gefälschter Uhr:** Wunschantwort unverändert, aber so spät, dass die
    Set-Beobachtung beim Freigeben 5 s überschreitet → genau eine Neulesung von Set und Wunsch, danach `REMOVE`, wenn beide
    frisch sind; ist auch die zweite Folge zu alt → `tooSlow`, kein `REMOVE`; eine Wunschlesung, die selbst länger als 5 s
    vor dem Freigeben begann, zählt genauso; Grenzfall genau 5 s wie heute (bestehende Grenze unverändert);
  - Zeile, deren Eintrag laut Lesung weg ist → keine Wunschprüfung, Überspringen wie bisher;
  - `wishEntryRow`: frische Lesung 404 → `wishChanged`; Zustand `kept` → `wishChanged`; anderer Slot oder andere
    Generation → `wishChanged`; Lesung scheitert oder läuft in die Frist → `wishUnverified`; unverändert → `null`;
  - die bestehenden Tests der Wunschbindung bleiben grün (Token, Set, Generation, Alter der Beobachtung).
- **Grenzfälle:** Abmelden während der Wunschlesung (die letzte Token-Prüfung danach greift weiter); zwei Zeilen eines
  Laufs (gibt es bei der Karte nicht, aber das Tor ist je Zeile); die Api antwortet 429 auf die Wunschlesung →
  `wishUnverified`, kein Pausieren.
- **AKs:** AK133 (Vitest-Teil).
- **Commit-Text (nach T9):** `fix(web): check the wish again before every removal attempt`

### T6: Web-Seite

- **Ziel:** 2.6 in `channel-wishes-page.ts`, `wish-row.ts`, `wish-adopt-dialog.ts` (Vorauswahl, 2.5); die Texte stehen
  seit T4.
- **Dateien:** `web/src/app/features/wishes/channel-wishes-page.ts`, `wish-row.ts`, `wish-adopt-dialog.ts`; Specs
  `channel-wishes-page.spec.ts`, `wish-row.spec.ts`, `wish-adopt-dialog.spec.ts`.
- **Tests zuerst (Regel 12: Verhalten, nicht Vorlage):**
  - Löschen öffnet die Bestätigung; Abbrechen sendet nichts; Bestätigen sendet das Ende der gezeigten Zeile, auch wenn eine
    Neuladung die Zeile zwischen Öffnen und Bestätigen ersetzt hat;
  - Bestätigungstext: `kept` mit `slotId` → `wishes.confirm.deleteKept`; `kept` ohne `slotId` und jeder andere Zustand →
    `wishes.confirm.delete` (PR1-6; Schlüssel als Kennung);
  - 204 → Neuladen, keine Fehlermeldung; 404 `wish_not_found` → ebenso; 409 → Fehlermeldung und Neuladen; 403 →
    Fehlermeldung;
  - nach 204 auf `kept` mit `slotId`: Hinweis mit Knopf; der Knopf öffnet den Dialog mit der `sevenTvEmoteId` des gelöschten
    Wunsches, **ohne** `wish`, mit dessen `slotId` als Vorauswahl; ohne `slotId` oder anderer Zustand in „Beendet“: kein
    Hinweis;
  - Dialog: Vorauswahl gesetzt, wenn der Eintrag frei ist; nicht gesetzt, wenn er gesperrt ist (Halter vorhanden) oder in der
    Liste fehlt; die übrigen Einträge des Emotes sind sichtbar;
  - Fokus: „Beendet“ nächste Zeile, sonst vorige, sonst Überschrift; angeheftete Zeile: Anheftung leer, Fokus auf dem
    Hinweis bzw. seinem Knopf [B2], ohne Weg zurück auf dem Hinweis „Wunsch gelöscht“;
  - `wish-row`: `delete` in der destruktiven Gruppe hinter `reAdd`, auf grobem Zeiger sichtbar, während einer 7TV-Sperre
    der Seite nicht gesperrt;
  - Entfernungsbericht „gelöscht“: die Seite meldet das Entfernen als geschehen mit dem eigenen Grund.
- **Grenzfälle:** Doppelklick während eines laufenden Befehls (`pendingIds`); Kanalwechsel bei offener Bestätigung;
  `wish.changed` während der Bestätigung entfernt die Zeile schon (Senden → 404 → still erledigt).
- **UI-Prüfliste** „Neue UI bauen“ (`web/.claude/CLAUDE.md`) abarbeiten; der Hinweis folgt der Form von `mergedNotice`.
- **AKs:** AK116 (Bestätigung, Ende, 404, Hinweis, Vorauswahl, Fokus, Eintrag-Dialog-Text über T4).
- **Commit-Text (nach T9):** `feat(web): delete ended wishes from the wishes page`

### T7: E2E und UI-Audit

- **Ziel:** AK118, AK133 (E2E), AK116 (Live-Ereignisse an einer fremden Seite).
- **Dateien:** `web/e2e/support/mocks.ts` ([W2]: Art `delete`, Feld `endedAtUtc`, Standardantwort 204, Entfernen aus der
  Liste **und** Leeren des Halters am gehaltenen Eintrag in `slots`; über `onCommand` überschreibbar),
  `web/e2e/wishes.e2e.spec.ts`, `web/e2e/wishes-seven-tv.e2e.spec.ts`. Specs importieren aus `web/e2e/support/test.ts`.
- **Fälle:**
  - **Leitfall (Y3, PR1-7):** behaltener Wunsch mit Slot im Bereich „Beendet“, der Eintrag im Mock **als von ihm gehalten**;
    „Bestand übernehmen“ zeigt ihn gesperrt mit dem neuen Text → Dialog schließen → „Löschen“ → Bestätigung → `DELETE` mit
    dem Ende der Zeile → Zeile weg → Hinweis → „Bestand übernehmen“ → der Eintrag ist frei und vorausgewählt → 7 Tage →
    `adopt` mit `durationDays` 7 → neuer Wunsch mit **anderer ID** im Bereich „Laufend“;
  - „Zum Wunsch“ aus dem Übernahme-Dialog → angeheftete Zeile mit „Löschen“ → löschen → Anheftung weg, Fokus auf dem
    Hinweis;
  - Leitfall in mobiler Breite (360 px, grober Zeiger): Bestätigung als Sheet, Fokus sichtbar, nicht unter der Aktionsleiste;
  - **Live-Ereignisse an einer fremden Seite:** Die Seite löscht nichts; der Mock entfernt einen beendeten Wunsch
    „anderswo“ und schickt zwei `wish.changed` kurz nacheinander → die Liste zeigt den neuen Stand ohne Seiten-Reload
    (`page.clock` nach den Regeln in `CLAUDE.md`);
  - **AK133, zwei Verwalter** (`wishes-seven-tv.e2e.spec.ts`, mit dem dortigen 7TV-Mock; PR2-5): Entfernung starten, das
    erste `REMOVE` beantwortet 7TV mit 429 samt kurzer Reset-Zeit; während der Pause stellt der Mock den Zustand nach
    Behalten → Löschen → Übernehmen des anderen Verwalters her: der alte Wunsch fehlt in Detail (404) **und** Listen, ein
    neuer Wunsch hält den Eintrag mit derselben Generation → nach der Pause zählt der Test **null** weitere `REMOVE`-
    Mutationen an 7TV und **null** `complete-removal` an die Api; der abgeschlossene Lauf im Lösch-Dock zeigt den Grund
    `wishChanged`, auch nachdem die alte Karte verschwunden ist;
- **UI-Audit:** Szenario `wishes-page` (`web/e2e/audit/ui-audit.audit.ts`, „Beendet“ ist dort offen) einmal laufen lassen
  (`cd web && npx playwright test --config=playwright.audit.config.ts -g wishes-page`), Gates für kleine Ziele und Überlauf
  ansehen.
- **Gate:** `npm --prefix web run e2e` grün, **ohne** Api auf `:5151`.
- **AKs:** AK118, AK133, AK116 (Live-Ereignisse).
- **Commit-Text (nach T9):** `test(web): e2e for deleting ended wishes and the removal re-check`

### T8: Doku und Coverage

- **Dateien:** `docs/DECISIONS.md` (nur Abweichungen von Spec und Plan), `docs/Feature-Ideen-2026-08-01.md` (Statuszeile
  `A18`), `CLAUDE.md` (Zeile „Wunsch-Emotes“ im Umsetzungsstand); `docs/Operations.md` nichts; `docs/Architectur.md` nur,
  falls dort die Wunsch-Routen aufgezählt sind.
- **Coverage:** `node scripts/coverage-local.mjs`, dann die **geänderten Zeilen und Zweige** im OpenCover- und LCOV-Report
  ansehen, nicht nur die Dateiquote (die Näherung zählt ganze Dateien; Sonar zählt Zeilen und schließt die Playwright-Mocks
  aus). Schwerpunkte: jede Ablehnung von `WishIdAccessFilter`, die `55P03`-Rücknahme, die Aufteilung von 404 nach Code im
  Client (Löschen und Entfernungsbericht), die beiden neuen Gründe im Tor. Fehlende Zeilen gezielt mit einem Test decken.
- **Commit-Text (nach T9):** `docs: record wish deletion (child 7a)`

---

## 4. AK-Abdeckung

| AK | Inhalt | Task(s) |
|---|---|---|
| AK109 | Zustände, Episode, 400/404, Mikrosekunden-Rundreise | T1 (Dienst, Postgres-Teil), T3 (Query, HTTP-Teil, 204) |
| AK110 | Kaskade, SET NULL, Slot frei, `adopt`, Folgezuordnung, Dedupe | T1 |
| AK111 | zwei `DELETE`; `complete-removal` auf gelöschten Wunsch → 404 | T2, T1 |
| AK112 | echte Retention als erster Sperrinhaber, beide Richtungen, keine Verklemmung | T2 |
| AK113 | Filter-Matrix, gleicher Login andere ID, unbound, Bot-Modus, Abbildung ohne Rumpf, Inventar 27, 204 | T1 (Abbildung), T3 |
| AK114 | ID-Wechsel zwischen Filter und Sperre | T1, T2 |
| AK115 | keine Migration; keine Personendaten im Audit | T1; T10 |
| AK116 | Oberfläche | T4, T6, T7 |
| AK117 | Locales grün, neue Schlüssel, „kein Fehlercode“ im PR | T3, T4, T10 |
| AK118 | E2E Leitfall mit Freigabe, angeheftete Zeile, mobil | T7 |
| AK119 | Live mit geprüftem Image | T9 |
| AK133 | Wunschprüfung vor jedem Entfernversuch, zwei Verwalter | T5 (Vitest), T7 (E2E) |
| AK134 | Sperre nur im eigenen Kanal | T1 |

---

## 5. Live-Verifikation (T9, AK119, Regel 16)

Lokaler Stack, echtes 7TV, Probe-Kanal und Testkonto wie im Live-Protokoll K6 (`docs/nachtlaeufe/live-486-2026-11-20.md`;
K1 = Konto des Betreibers, Twitch-Mod und 7TV-Editor im Probe-Kanal; K2 = Testkonto). Protokoll als
`docs/nachtlaeufe/live-500-<Datum>.md` (deutsch, ohne Kontonamen). **Eine Anweisung je Schritt**, auf Rückmeldung warten.

**Geprüfter Stand und Images (Orchestrator, PR1-8, PR2-6, PR2-7):**

1. **Fingerabdruck:** Basis-Commit (`git rev-parse HEAD`) und der Baum des uncommitteten Arbeitsstands über einen
   temporären Index wie bei den Sicherungspunkten (`git write-tree`) ins Protokoll.
2. **Api aus dem geprüften Stand:** im Worktree `docker build -f src/EmotePurge.Api/Dockerfile -t emote-purge-dev-api .`
   (ein `docker build` ist kein `docker compose`); die Image-ID beim Bauen notieren.
3. **Worker aus `main`:** `git archive` des genannten `origin/main`-Commits als Build-Kontext an `docker build -f
   src/EmotePurge.Worker/Dockerfile -t emote-purge-dev-worker -`; Commit und Image-ID notieren. So läuft das Paar, das
   die Auslieferung „nur Api“ in Produktion ergibt.
4. Aus dem **Haupt-Checkout** `docker compose up -d --no-build --force-recreate api worker`.
5. **Vor L1:** die Image-IDs der laufenden Container (`docker inspect` auf `emotepurge-dev-api` und den Worker-Container)
   mit den notierten vergleichen; bei Abweichung kein Live-Lauf.

**Vorbedingungen:** `Wishes:Enabled` und `Wishes:Capture:Enabled` an; Dev-DB ohne ausstehende Migration
(`dotnet ef migrations list` gegen die Dev-DB: nichts `(Pending)`); zweiter Browser-Tab auf derselben Seite offen.

| Schritt | Handlung (Betreiber) | Erwartung / Nachweis |
|---|---|---|
| L1 | Im Probe-Set ein Emote über 7TV hinzufügen (Testkonto darf schreiben), Seite „Wünsche“ → „Bestand übernehmen“ → den Eintrag mit 30 Tagen übernehmen | neuer Wunsch `active` in „Laufend“; Tab 2 zeigt ihn ohne Reload |
| L2 | Am Wunsch „Behalten“ bestätigen | Zeile in „Beendet“ als `kept`, Aktion „Löschen“ |
| L3 | „Bestand übernehmen“ öffnen; danach „Nachtragen“ mit demselben Emote | der Eintrag ist gesperrt mit dem neuen Text, nur „Zum Wunsch“; der Eintrag-Dialog meldet den `kept`-Konflikt mit demselben Weg |
| L4 | „Zum Wunsch“ → an der angehefteten Zeile „Löschen“ → Text lesen → bestätigen | Bestätigung nennt den Weg bedingt; Zeile und Anheftung weg, Fokus auf dem Hinweis bzw. seinem Knopf; Tab 2 ohne Reload aktuell |
| L5 | — (Orchestrator) | SQL: Zeile weg; `wish.delete` mit `fromStatus = kept`, Details ohne Alias/Namen; das Audit-Log im Admin-Bereich zeigt die Aktion übersetzt; 7TV-Set per Lesezugriff unverändert |
| L6 | Im Hinweis „Bestand übernehmen“ → der Eintrag ist vorausgewählt → 7 Tage → übernehmen | neuer Wunsch (neue ID) `active`, Ablauf in 7 Tagen ab dem gewählten Start |
| L7 | Mit K2 die Wunsch-Belohnung mit Text einlösen; den offenen Wunsch ablehnen; in „Beendet“ löschen | 204; Zeile weg; `wish.delete` mit `fromStatus = rejected`, ohne Text |
| L8 | Einen laufenden Wunsch über die Karte aus 7TV entfernen (wird `removed`), dann löschen | Entfernung geht durch (E46 lässt einen unveränderten Wunsch passieren); danach 204, Wunsch weg |
| L9 | Mobile Breite (Handy über den `lan`-Start oder Emulation) an einer beendeten Zeile löschen | Sheet, Fokus sichtbar über der Aktionsleiste |
| L10 | — (Orchestrator) | Probe-Set auf den Stand vor L1 zurück (übernommenen Wunsch schließen, Emote entfernen); Stand und Image-IDs im Protokoll |

**Nicht live** (durch Tests gedeckt): der Wettlauf mit der Retention, der Identitätswechsel, die Entfernung in einer
429-Pause gegen Behalten/Löschen/Übernehmen (AK133; eine 429 lässt sich live nicht gezielt erzeugen), `complete-removal`
gegen ein gleichzeitiges Löschen. Bestanden ist AK119, wenn L1 bis L8 und L10 halten und Fingerabdruck und Image-IDs
stehen; L9 darf als „teilweise“ stehen, wenn nur emuliert. Ändert ein Befund aus dem Live-Lauf Code, wird der Fingerabdruck
neu genommen und der betroffene Teil erneut live geprüft.

---

## 6. Gates, Commits, Zweitmeinung, Auslieferung (T10)

0. **Commits** aus dem in T9 geprüften Stand, je Task wie in Abschnitt 3 beschrieben; danach muss `git rev-parse
   HEAD^{tree}` dem Baum aus T9 gleichen. Die Gates unter 1 laufen auf dem committeten Stand erneut.
1. `dotnet test EmotePurge.slnx` (Docker), `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e` (ohne Api auf
   `:5151`), `npm --prefix web run lint`, `npm --prefix web run format:check`, `dotnet format EmotePurge.slnx
   --verify-no-changes`.
2. **Keine Migration:** `dotnet ef migrations has-pending-model-changes --project src/EmotePurge.Infrastructure
   --startup-project src/EmotePurge.Api` meldet nichts, `git diff --stat origin/main -- src/EmotePurge.Infrastructure/Migrations`
   ist leer.
3. `node scripts/coverage-local.mjs` samt Zeilenblick (T8); maßgeblich ist am PR der required check `analyze` (SonarCloud,
   80 % auf neuem Code).
4. Zweitmeinung vor dem Merge (Regel 22): `/codex:review --model gpt-6.2-sol` über den fertigen Branch, einmal; Ergebnis
   unverändert vorlegen. Bei Widerspruch zu einem Opus-Review entscheidet Fable.
5. PR (englisch): Inhalt, „no new error code“ (AK117), „no migration, Api image only“, E46 als Verhaltensänderung der
   Karten-Entfernung, Testzahlen, Live-Protokoll, `Closes #500`, Teil von #481. Mergen tut der Betreiber (Regel 1).

**Auslieferung:** Nur das Api-Image (enthält das Frontend, also auch E46); der Worker ist unberührt. Kein Schalter, keine
Compose- oder Runbook-Änderung. **Vor dem Deploy:** (a) der Betreiber bestätigt, dass das letzte Backup wiederherstellbar
ist (Restore-Probe nach `docs/Operations.md`), weil hartes Löschen nur so umkehrbar ist; (b) den Deploy-Hinweis gegen alle
ausstehenden Prod-Migrationen prüfen (Befehle für den Betreiber aufbereiten: Tunnel und `dotnet ef migrations list` mit
Passwort-Platzhalter, nicht selbst verbinden); erwartet: nichts `(Pending)`. **Rollback:** voriges Api-Image per `sha`-Tag;
gelöschte Wünsche kommen nur aus dem Backup zurück, E46 fällt mit dem Image weg.

**Umstellung offener Tabs (PR2-2):** E46 steckt im Frontend; ein vor dem Deploy geöffneter Tab führt die alte Lösch-Engine
weiter aus (bei einem neuen Stream lädt die Seite nur Daten nach) und kann nach einer 429-Pause noch bis zu fünf
Wiederholungen ohne Wunschprüfung senden. Für diese vom Betreiber geführte Instanz mit wenigen Verwaltern: (a) Deploy in
einer ruhigen Zeit, ohne laufende Entfernung aus der Karte; (b) die Verwalter laden danach die Seite neu (Hinweis im
Mod-Kanal); (c) in den ersten 10 Minuten nach dem Deploy wird kein behaltener Wunsch gelöscht, dessen Emote gerade
entfernt wurde. Die Regel steht in der PR-Beschreibung und im Deploy-Hinweis an den Betreiber; nicht in
`docs/Operations.md`, weil sie nur für diesen einen Wechsel gilt. Ein Server-Wächter (Versionsvertrag zwischen Browser und
Api) wäre für einen einmaligen Wechsel unverhältnismäßig.

---

## 7. Risiken

| Risiko | Umgang |
|---|---|
| Der Client verliert Mikrosekunden von `endedAtUtc` | Vertrag 2.5, Rundreise in T1/T3, String-Test in T4, Live L4 |
| EF lädt Kinder mit und setzt den Fremdschlüssel selbst | 2.2 Schritt 7; T1-Test „Kind unverändert außer Verweis“ |
| `NOWAIT`-Fehler hinterlässt eine abgebrochene Transaktion | T2-Test „derselbe Scope löscht danach erneut“ |
| Retention-Tests wackeln | Blockade über `pg_stat_activity` und `NOWAIT`-Probe abwarten statt schlafen |
| E46 überspringt zu oft (Api-Lesung langsam oder 429) | gewollt fail-closed; der Verwalter startet erneut; Frist wie die Startlesung |
| RR16 Moderator nach Rollenentzug bis 10 min | eingegrenzt (Revision 1, Tabelle), Admin-Invalidierung, #496 H6 |
| RR17 Uhrsprung auf dieselbe Mikrosekunde | eingegrenzt, Folge begrenzt |
| RR18 höchstens 5 s plus Mutation zwischen Wunschprüfung und `REMOVE` | Frischeprüfung nach der Wunschlesung (PR2-1); Rest Klasse RR14 |
| RR19 Abgleich nach dem Löschen eines `kept` gegen das Leeren der Einlöser-Daten | eingegrenzt (PR2-4), besteht für jeden Abgleich schon heute; Worker fängt den Abbruch, nächster Lauf holt nach; #496 H7 |
| Alte Tabs ohne E46 nach dem Deploy | Umstellungsregel (Abschnitt 6) |
| Uncommittete Arbeit über viele Tasks | Sicherungspunkte je Task über einen temporären Index, SHAs im Protokoll des Orchestrators |
| RR15 Login-Nachtrag der ID | hingenommen (Spec), #496 H4 |

---

## 8. Betreiberentscheide zu den offenen Fragen (2026-11-21)

Alle drei Fragen aus Revision 1 sind entschieden, jeweils wie empfohlen; offen ist keine.

1. **RR16 hingenommen.** Für K7a keine frische Twitch-Abfrage; dieselbe Regel wie für die übrigen harten
   Verwalter-Aktionen. Eine gemeinsame frische Prüfung mit fail-closed bleibt ein eigenes Thema (#496 H6).
2. **#496 H1 nicht in K7a.** `decisionVersion` im `WishDto` und im Vergleich von `wishDeviation` („Verlängern während der
   Pause“) ist ein eigenes kleines Issue: #505 (P3, etwa ein Viertel Tag, baut auf E46 auf).
3. **2,5 Tage angenommen, E46 bleibt in K7a.** Umsetzung nach einer zweiten Sol-Runde über diesen Plan.

---

## 9. Selbstprüfung des Plans

- **Spec-Abdeckung:** E32 (T1, T2), E34 (T1), E35 (T1, T2), E36 (T1, T4), E37 (kein Code nötig), E38 (T1, T3, T6), E39 (T3),
  E40 (T10), E41 (T1, T2, T3), E44 (T1, T3, T4), E46 (T5, T7), N3 (T4, T6), Leitfall Y3 (T6, T7, T9), Betreiberantworten
  B1 bis B4 (T4, T6, T7), AK109 bis AK119, AK133, AK134 (Abschnitt 4), RR16 bis RR19 (Abschnitt 7).
- **Kein fertiger Code:** Signaturen nur als Form, keine Rümpfe, keine Abfragen ausformuliert; Befehle nur für Gates und
  Live-Image.
- **Namen über Tasks hinweg:** `DeleteAsync`, `WishOutcome.AccessDenied`, `AuditActions.WishDelete`, `WishIdAccessFilter`,
  `deleteWish`, Aktion `delete`, Mock-Art `delete`, `verifyWish`, Gründe `wishChanged`/`wishUnverified`, Schlüssel aus 2.6.
- **Jeder Task besteht sein Gate allein:** T1 trägt die Abbildung von `AccessDenied` mit (PR1-5); T3 hebt das Inventar;
  T4 legt alle Schlüssel an, bevor T5 und T6 sie nutzen; T5 erweitert `SkipReasonKey` selbst (PR2-8).
- **Regel 16 wörtlich:** kein Commit vor T9; der Fingerabdruck verbindet den live geprüften Stand mit den Commits.
- **Regel 7:** kein neuer Fehlercode; Regel 11/12 je Task genannt.
- **Umfang:** acht Implementer-Tasks für 2,75 Tage, T9/T10 ohne Implementer. Der Plan ist länger als der Spec-Teil, weil er
  Testabsichten je Task und den Live-Ablauf ausschreibt; Code enthält er keinen.
