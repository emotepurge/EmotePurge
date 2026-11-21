# Plan #500: Wunsch-Emotes, Kind K7a: beendete Wünsche löschen

Erstellt am 2026-11-21 im Worktree `agent-a9d73614672ee1285` (Branch `feat/wish-renew-delete`, Basis `main` @ `dbc24973`
plus die Spec-Commits des Nachtrags K7). Alle Codestellen dieses Plans sind an diesem Stand gelesen.
**Status: Revision 0, vor der adversarialen Prüfung durch Sol.**
Quellen: [Spec](../superpowers/specs/2026-11-17-wunsch-emotes-spec.md), Nachtrag „K7“, Teil „K7a: Löschen beendeter Wünsche“
(E32, E34 bis E41, E44, AK109 bis AK119, Testplan, Files Reference, Auslieferung, RR15) samt Betreiberentscheidungen Y2, Y3,
O1, O2 und der Tabelle „Prüfbefunde K7“; [Plan-483](Plan-483-Wunsch-Emotes-Backend-Kern.md) (Sperrkonvention 2.3),
[Plan-486](Plan-486-Wunsch-Emotes-Chat-Erfassung.md) (2.4a Zugriff über die Twitch-ID); DECISIONS „Wish emotes, child 7
(spec addendum)“ vom 2026-11-21; `CLAUDE.md` (Regeln 1 bis 3, 7, 11, 12, 16, 18, Schichtentreue, Tests, Sprache),
`web/.claude/CLAUDE.md`, `docs/UI-Designsprache.md` §4.2, §7, §8.1, §8.7, §11; Issue #500, Epic #481.

**Was dieser Plan trägt:** den Befund am Code, die geteilten Verträge, den Schnitt in Tasks mit Grenzfällen und
Testabsicht, die AK-Zuordnung, die Live-Verifikation, Gates und Auslieferung. **Kein Code**; Namen und Signaturen nur als
Form. Jeder Implementer liest seinen Task hier, die genannten Spec-Festlegungen **und** die genannten Bestandsdateien, bevor
er schreibt. Vor jeder UI-Arbeit `docs/UI-Designsprache.md` §11 und `DESIGN.md`.

**Lesehinweis:** **[E]** Planer-Entscheidung (der Betreiber kann widersprechen) · **[P]** Präzisierung ohne
Entscheidungsgehalt · **[W*n*]** Abweichung zwischen Spec und Code aus Abschnitt 0 · „E32“, „AK109“ meinen die Spec.

---

## 0. Befund am Code

### 0.1 Was die Spec behauptet und am Code hält

| Behauptung der Spec | Prüfung |
|---|---|
| Nur zwei Fremdschlüssel zeigen auf `Wishes`: `MergedIntoWishId` (SET NULL) und `WishSlotExclusions.WishId` (CASCADE), E40 | hält: `AppDbContext.cs` (Wunsch-Block ab `:487`, `:548-551`, `:653-662`); im Modell-Snapshot gibt es genau diese beiden `HasOne("…Wish")`; `IX_Wishes_MergedIntoWishId` existiert. Das Audit-Log hat keinen FK auf Wünsche |
| Kein neuer Fehlercode nötig (Regel 7) | hält: `invalid_request_body`, `wish_not_found`, `channel_not_found`, `wishes_disabled`, `wish_channel_unbound`, `wish_state_conflict` stehen in `ApiErrorCodes.cs`, `api-error.ts` und beiden Locale-Dateien |
| Jeder Eintritt in einen Endzustand setzt `EndedAtUtc` (E44) | hält für alle Schreiber: `StampStateChange` (`WishService.cs:1625`), Abgleich (`WishReconcileService.cs:580`, `:697`), Verfall offener Wünsche (`WishCaptureService.cs:421`), Kanal-Zusammenführung (`ChannelIdentityService.cs:1308-1319`) |
| `SameInstant` vergleicht auf Datenbankgenauigkeit | hält: `WishService.SameInstant` (`:1641`), `internal static`, Mikrosekunden |
| Muster „Pflicht-Query an einem DELETE“ | hält: `DELETE …/rewards/learning?sessionId=` (`WishEndpoints.cs:599-617`, als `string?` gebunden, damit ein unlesbarer Wert dieselbe übersetzbare 400 gibt) |
| `WishRewardOutcome.AccessDenied` → 403 ohne Rumpf | hält: `MapRewardResult` (`WishEndpoints.cs`) mit `Results.Forbid()` |
| Zugriffsprüfung über die Twitch-ID gibt es seit K6 | hält: `WishCaptureAccessFilter` + `IChannelAccessService.ResolveWishCaptureAccessAsync`; `WishService.IsConfirmedOn` (`:1716`) |
| Sperrkonvention Kanal → Wunsch, Wunschzeilen aufsteigend | hält: `RowLocks.LockChannelsByIdsAsync` (`FOR NO KEY UPDATE`), `LockWishesByIdsAsync` (`FOR UPDATE`, aufsteigend) |
| Retention löscht und leert in ungeordneten Stapeln | hält: `DataRetentionService.RunEndedWishesAsync` (`:462`), `RunWishRequestersAsync` (`:502`), je `ExecuteDelete`/`ExecuteUpdate` über eine ID-Liste ohne eigene Sperrreihenfolge |
| „Bestand übernehmen“ zeigt am `kept`-Halter den Sperrtext `wishes.adopt.locked.kept` | hält: `wish-adopt-dialog.ts:719-733` |
| `wishActions` liefert für `kept` nichts | hält: `wish-view.ts:255-288` (`default: []`) |

### 0.2 Abweichungen und Lücken (mit Entscheidung)

- **[W1] Der 404 auf `complete-removal` liegt nicht in `wish-removal-flow.ts`**, wie die Files Reference der Spec sagt,
  sondern in `web/src/app/core/wishes/wish-removal-tracker.ts`: `isFinalRefusal` macht aus 409 **und** 404 den Endzustand
  `notBooked`; angezeigt wird er in `channel-wishes-page.ts` (Seitenhinweis) und `wish-operation.ts` (Zeile) mit
  `wishes.remove.notBooked` („Entfernt in 7TV. Der Wunsch wurde inzwischen anders gebucht.“). Das Entfernen gilt dort also
  schon als geschehen; es fehlt nur der eigene Grund „inzwischen gelöscht“ (N3). **[E]** `notBooked` bekommt eine
  Unterscheidung „gelöscht“, die nur bei 404 mit `wish_not_found` gilt; T4.
- **[W2] Der E2E-Mock ordnet jeden Nicht-GET auf `/{id}` als `update` ein** (`web/e2e/support/mocks.ts`, Abschnitt
  „Wish emotes“, Klassifizierung `numeric !== null && rest.length === 1`). Ein `DELETE` braucht eine eigene Art `delete`,
  und `MockWishCall` muss den Query-Wert `endedAtUtc` mitschreiben; T6.
- **[W3] `RowLocks` kennt kein `NOWAIT`**, und im ganzen Backend wird SQLSTATE `55P03` noch nirgends behandelt. T2 legt
  den Eintrag an und legt im Test fest, in welcher Ausnahmeform Npgsql den Fehler aus einer `SqlQuery` wirft.
- **[W4] `StageWishAudit` kann kein `fromStatus` tragen** (feste Details `wishId`, `sevenTvEmoteId`, `durationDays`,
  optional Ablauf und Entfernungsbericht). **[P]** T1 erweitert die Hilfsmethode um ein optionales `fromStatus` oder
  stellt die Details für das Löschen selbst zusammen; kein `previousExpiresAtUtc` (E36).
- **[W5] `LockChannelAsync` liest hinter der Sperre nur Name und Kapazität** (`LockedChannel`). Die erneute Prüfung aus
  E41 braucht die `TwitchChannelId` der gesperrten Zeile; T1 liest sie mit.
- **[W6] Genauigkeit von `endedAtUtc` im Browser.** Die Datenbank hält Mikrosekunden, ein JavaScript-`Date` nur
  Millisekunden. Rechnet der Client den Wert über `Date` (oder `parseUtc`) um, scheitert jede Bindung an `SameInstant` mit
  409. **Vertrag:** Der Client schickt den String aus dem DTO **unverändert** zurück (T4, Test mit sieben Nachkommastellen).
- **[W7] `wish-row.ts` gruppiert destruktive Aktionen über eine eigene Menge** (`DESTRUCTIVE_ACTIONS` = `remove`,
  `close`, `reject`). `delete` muss dort hinein, sonst steht es bei den konstruktiven Aktionen; „zuletzt“ ergibt sich aus der
  Reihenfolge in `wishActions` innerhalb der Gruppe (T5).
- **[W8] Die Bestätigung der Seite ist generisch** (`wishes.confirm.${action}` mit `name`, `channel-wishes-page.ts`
  `onAction`). Für `kept` braucht es einen eigenen Text (`wishes.confirm.deleteKept`); die Seite wählt ihn nach dem Zustand
  der Zeile (T5).
- **[W9] Ein beendeter Wunsch ohne `endedAtUtc`** ist nach 0.1 nicht erreichbar. **[E]** Der Client bietet `delete`
  trotzdem nur an, wenn `endedAtUtc` gesetzt ist (der Server antwortete sonst 400); die Live-Verifikation prüft per SQL,
  dass es keine solche Zeile gibt (Abschnitt 5, L0).

Kein Widerspruch berührt eine Betreiberentscheidung; die Spec bleibt unverändert, [W1] wird beim Merge in der Files
Reference nicht nachgezogen (der Plan ist die Korrektur).

---

## 1. Umfang

**Drin:** die Route `DELETE …/wishes/{id:long}?endedAtUtc=` mit eigenem Zugriffsfilter (E41), `IWishService.DeleteAsync`
mit Sperrfolge (E32), Zustand (E34), Episode (E44), Wirkung (E35), Audit und Ereignissen (E36); in der Oberfläche die Aktion
„Löschen“ im Bereich „Beendet“ und an der angehefteten Zeile, Bestätigung, Hinweis „Bestand übernehmen“ nach dem Löschen
eines `kept`, neuer Sperrtext im Übernahme-Dialog, der eigene Grund im Entfernungsbericht (N3); Tests auf allen Ebenen,
Live-Verifikation, Doku-Zeilen.

**Draußen:** K7b „Erneut auf Zeit“ (Y3); die allgemeine Moderatorprüfung über die ID (#496 Punkt 2 / H3); jede
Migration, jeder Schalter, jede Änderung an Worker, Compose, Runbook, Retention; Löschen in `my-wishes`, „Handlungsbedarf“
oder „Laufend“.

---

## 2. Verträge, die mehrere Tasks teilen

### 2.1 Route

| | |
|---|---|
| Route | `DELETE /api/channels/{channelName}/wishes/{id:long}?endedAtUtc=<ISO-8601>` in der bestehenden Gruppe (Namensfilter, Verwalter-Filter), dahinter der Zugriffsfilter aus 2.4 |
| Rate-Limit | `Bookkeeping` |
| Query | `endedAtUtc` als `string?` gebunden; fehlt oder unlesbar → 400 `invalid_request_body`. Gelesen invariant als Zeitpunkt in UTC (Muster `AdminLogEndpoints`/`ChannelEndpoints`: `AdjustToUniversal`, `AssumeUniversal`); ein Wert ohne Zone gilt als UTC [E] |
| Erfolg | 204 ohne Rumpf; der Handler antwortet selbst, `MapResult` behandelt einen Erfolg ohne Wunsch als unerreichbar (`WishEndpoints.cs:373-385`) und bleibt so |
| Fehler in Reihenfolge | 400 Name (Gruppe) → 401/403 Verwalter (Gruppe) → 404 `wishes_disabled` (Zugriffsfilter) → 404 `channel_not_found` (Filter) → 409 `wish_channel_unbound` (Filter) → 403 ohne Rumpf (Filter, nicht an der ID bestätigt) → 400 `invalid_request_body` (Handler, Query) → Dienst: 404 `channel_not_found` (Zeile während des Wartens weg), 403 (`AccessDenied`), 404 `wish_not_found`, 409 `wish_state_conflict` |

**[E] Query nach dem Filter:** Ein abgeschaltetes Feature oder ein fremder Kanal antwortet nicht mit einer
Query-Fehlermeldung (gleicher Grundsatz wie „Schalter vor Rumpf“, `SwitchOff_ChecksTheSwitch_BeforeTheBody`).

### 2.2 `IWishService.DeleteAsync`

**Form:** `DeleteAsync(channelName, wishId, expectedEndedAtUtc, actor, access, cancellationToken)` →
`Task<WishCommandResult>`; `access` ist Pflicht (die Route hat immer den Filter). `WishOutcome` bekommt `AccessDenied`
(Kommentar: der zweite Wall hinter dem Filter, wie bei den Belohnungen). Erfolg trägt weder Wunsch noch Einstellungen.

**Ablauf, in dieser Reihenfolge, in einer Transaktion:**

1. Kanalzeile `access.ChannelId` sperren (`LockChannelsByIdsAsync`, wartend). Weg → `ChannelNotFound`.
2. Hinter der Sperre Name und `TwitchChannelId` der Zeile lesen; `IsConfirmedOn(access, rowTwitchChannelId)` falsch →
   `AccessDenied`, nichts geschrieben (E41, AK114). Diese Prüfung steht vor der Suche nach dem Wunsch, damit ein
   abgewiesener Aufrufer nicht erfährt, ob die ID existiert.
3. Wunschzeile sperren (`LockWishesByIdsAsync`, **wartend**), danach lesen, auf den Kanal begrenzt. Fehlt → `WishNotFound`.
4. `WishStatus.IsEnded(status)` falsch → `StateConflict`.
5. `SameInstant(wish.EndedAtUtc, expectedEndedAtUtc)` falsch → `StateConflict` (E44).
6. IDs der Wünsche mit `MergedIntoWishId = wishId` lesen (ohne Tracking, nur IDs) und sie **aufsteigend mit `NOWAIT`**
   sperren (neuer Eintrag in `RowLocks`, 2.2a). SQLSTATE `55P03` → Rücknahme der Transaktion, `StateConflict`.
7. Audit `wish.delete` stagen (2.3), die Wunschzeile entfernen, speichern, committen. SET NULL an den Kindern und die
   Kaskade der Sperrzeilen erledigt die Datenbank; **die Kinder werden nicht als Entitäten geladen**, sonst setzte EF den
   Fremdschlüssel selbst und schriebe sie mit [P].
8. Nach dem Commit: `wish.changed` für den Kanal (bestes Bemühen, wie überall); war der Wunsch `kept` mit `SlotId`, den
   Abgleich anstoßen (`TriggerReconcileAsync`).

**2.2a `RowLocks`:** neuer Eintrag „Wunschzeilen `FOR UPDATE NOWAIT`, aufsteigend“, gleiche Regeln wie
`LockWishesByIdsAsync` (offene Transaktion Pflicht, IDs heraus, keine Entitäten). Die Klassendoku nennt, dass dieser
Eintrag nur für Zeilen gilt, die der Aufrufer **nach** einer gehaltenen Wunschzeile nimmt, und warum (E32: nie auf eine
Wunschzeile warten, während man eine andere hält).

**Warum die Wunschzeile wartet und die Kinder nicht:** E32/E35 und AK112 b. Zwei `DELETE` auf denselben Wunsch
serialisieren an Kanal und Wunsch; der zweite findet die Zeile nicht mehr → 404 (AK111). Hält die Retention den Wunsch,
wartet das Löschen und findet ihn danach gelöscht (404) oder noch da (204). Hält sie ein Kind, gibt das Löschen sofort nach.

### 2.3 Audit und Ereignisse

`AuditActions.WishDelete = "wish.delete"` mit Kommentar (Details, Akteur, „überlebt das Löschen, kein FK“). Ziel
(`wish`, ID), Akteur der Verwalter, Details genau `wishId`, `sevenTvEmoteId`, `durationDays`, `fromStatus`; nie Alias,
Emote-Name, Login, Einlösetext, kein `previousExpiresAtUtc`. Im Web: `audit.model.ts` (Union), `audit-actions.ts`
(`wish.delete` → `audit.actions.wishDelete`), beide Locale-Dateien.

### 2.4 Zugriffsfilter (E41)

**[E] Eigene Klasse `WishIdAccessFilter`** in `src/EmotePurge.Api/Auth/`, Variante von `WishCaptureAccessFilter` ohne
den Erfassungsschalter und ohne die GET-Ausnahme: `Wishes:Enabled` aus → 404 `wishes_disabled`; Name ungültig → 400;
ohne Prinzipal → 401; `ResolveWishCaptureAccessAsync` null → 404 `channel_not_found`; Zeile ohne ID → 409
`wish_channel_unbound`; nicht bestätigt → 403 ohne Rumpf; sonst Zugriff in `HttpContext.Items`, Abruf über eine eigene
statische Methode. Gemeinsame Teile der beiden Filter dürfen in einen kleinen internen Helfer wandern; das Verhalten von
`WishCaptureAccessFilter` bleibt bitgleich (seine Tests bleiben unverändert grün). Kein `WishSettingsAuthorizationFilter`
(E39). Der Name ist bewusst allgemein: Nach #496 Punkt 2 kann er wegfallen oder weitere Routen schützen.

### 2.5 Client-Verträge

- `WishService.deleteWish(channelName, wishId, endedAtUtc)` → `Observable<void>`, `endedAtUtc` als Query über
  `HttpParams`, **der DTO-String unverändert** ([W6]).
- Ein 404 mit `wish_not_found` auf das eigene Löschen gilt als erledigt: keine Fehlermeldung, Listen neu laden (E38).
  404 mit anderem Code, 403, 409 und Netzfehler zeigen die Fehlermeldung der Zeile wie heute (`wishErrorKey(…, 'action')`);
  409 `wish_state_conflict` lädt zusätzlich neu (wie bei den übrigen Befehlen).
- Entfernungsbericht ([W1]): 404 mit `wish_not_found` → Endzustand „nicht gebucht, gelöscht“; Text „Aus 7TV entfernt.
  Der Wunsch wurde inzwischen gelöscht.“ (Wortlaut beim Implementer, Sinn fest). 409 und 404 mit anderem Code bleiben
  `notBooked` wie heute.

### 2.6 Oberfläche (Absicht)

- **Aktionen** (`wishActions`): `kept` → `delete`; `removed`, `removed_externally` → `reAdd`, `delete`; `rejected`,
  `expired`, `closed`, `merged` → `delete`; jeweils nur mit gesetztem `endedAtUtc` ([W9]). `delete` ist keine 7TV-Aktion
  (bleibt auf grobem Zeiger sichtbar, F19), ist bestätigungspflichtig und destruktiv ([W7]). Die Seite nimmt `delete` in
  `OFFERED_ACTIONS` auf.
- **Orte:** Bereich „Beendet“ und angeheftete Zeile (`?wish=`). Andere Orte zeigen beendete Wünsche nicht; `my-wishes`
  benutzt `wishActions` nicht.
- **Bestätigung** über `openConfirmDialog` (Text mit Zeilenumbrüchen möglich): Der Wunsch verschwindet endgültig samt
  Wünschendem und Einlösetext; das Aktivitätsprotokoll behält die Aktionen; in 7TV ändert sich nichts. Bei `kept` zusätzlich:
  Das Emote bleibt im Set, der Eintrag lässt sich danach mit „Bestand übernehmen“ und neuer Laufzeit neu verknüpfen. Knopf
  „Löschen“ ohne „…“. Gesendet wird das `endedAtUtc` **der Zeile, die die Bestätigung zeigte** (festgehalten beim Öffnen,
  nicht nach dem Schließen neu gelesen).
- **Fokus danach** (§11, `web/.claude/CLAUDE.md`): im Bereich „Beendet“ auf die nächste Zeile des Bereichs, sonst auf die
  vorige, sonst auf die Überschrift `#wishes-ended-title` (bzw. ihren Knopf). An der angehefteten Zeile **[E]**: der Anheft-
  Zustand wird geleert, damit nicht „nicht mehr vorhanden“ erscheint; Fokus auf den Hinweis aus dem nächsten Punkt, falls er
  erscheint, sonst auf die Überschrift des Bereichs „Beendet“.
- **Hinweis nach dem Löschen eines `kept` mit `slotId`** (Leitfall Y3): ein Seitenhinweis wie `mergedNotice` (gleiche
  Stelle, gleiche Form, schließbar), mit Knopf „Bestand übernehmen“ (`wishes.page.header.adopt` wiederverwenden), der
  `openAdopt({ sevenTvEmoteId })` des gelöschten Wunsches öffnet. **[E]** Nicht auf den einen Eintrag verengt: Ein
  zweiter Alias desselben Emotes ist sichtbar, `adopt` entscheidet nach seinen Regeln (E35). Der Hinweis verschwindet beim
  Kanalwechsel, beim Öffnen des Dialogs und beim nächsten eigenen Löschen.
- **Übernahme-Dialog:** nur der Text von `wishes.adopt.locked.kept` ändert sich: „Ist als behaltener Wunsch geführt. Über
  ‚Zum Wunsch‘ lässt er sich löschen; danach kannst du den Eintrag mit neuer Laufzeit übernehmen.“ (Sinn fest).
- **Mobil:** Aktion in der Zeile wie die übrigen, Bestätigung als Sheet wie die bestehenden, Fokus sichtbar über der
  Aktionsleiste (vgl. #497, #498).
- **i18n (de, en):** `wishes.actions.delete`, `wishes.confirm.delete`, `wishes.confirm.deleteKept`, Hinweis nach dem
  Löschen (Text plus Schließen), `wishes.remove.notBookedDeleted`, neuer Text `wishes.adopt.locked.kept`,
  `audit.actions.wishDelete`.

---

## 3. Tasks

Je Task ein Subagent mit frischem Kontext, **strikt nacheinander** (die Backend-Tasks teilen `obj/`, die Frontend-Tasks
die Locale-Dateien, die E2E-Suite den Port). Jeder Backend-Task endet mit grünem `dotnet build EmotePurge.slnx` und
`dotnet test EmotePurge.slnx` (Docker läuft), jeder Frontend-Task mit `npm --prefix web test -- --watch=false`,
`npm --prefix web run lint` und `npm --prefix web run format:check`; Backend-Tasks zusätzlich `dotnet format EmotePurge.slnx
--verify-no-changes`. Je Task ein englischer Conventional Commit. Test zuerst: Jeder Task schreibt seine Tests, sieht sie
rot, implementiert, sieht sie grün. **Nie `docker compose` aus dem Worktree** (Speicher: Dev-Stack-Fallen). Kommentare,
Logs, Commit-Texte englisch.

| Task | Inhalt | Modell |
|---|---|---|
| T1 | Core-Vertrag, `WishService.DeleteAsync` (ohne Kindersperre), Audit, Integrationstests Zustand/Episode/Wirkung | opus |
| T2 | `NOWAIT`-Sperre der Kinder, `55P03`, Rennen mit zweitem `DELETE`, Retention und Identitätswechsel | opus |
| T3 | Api: `WishIdAccessFilter`, Route, `MapResult` mit `AccessDenied`, Filter-Matrix, DECISIONS-Absatz | sonnet |
| T4 | Web-Kern: Aktion, Dienstmethode, Entfernungsbericht „gelöscht“, Audit-Aktion, i18n | sonnet |
| T5 | Web-Seite: Löschen in Zeile und Seite, Bestätigung, Fokus, Hinweis „Bestand übernehmen“, Sperrtext | opus |
| T6 | E2E (AK118) und UI-Audit-Lauf | sonnet |
| T7 | Doku und Coverage | sonnet |
| T8 | Live-Verifikation (Abschnitt 5) | Orchestrator mit Betreiber |
| T9 | Gates, Zweitmeinung, PR (Abschnitt 6) | Orchestrator |

Abhängigkeiten: T1 → T2 → T3; T4 braucht nur die Route aus T3 als Vertrag (darf nach T3 laufen); T5 nach T4; T6 nach T5;
T7 nach T6; T8 nach T7; T9 zuletzt.

### T1: Löschen im Dienst

- **Ziel:** 2.2 Schritte 1 bis 5, 7, 8 (Schritt 6 folgt in T2), 2.3 im Backend.
- **Dateien:** `src/EmotePurge.Core/Entities/AuditLogEntry.cs` (`WishDelete`), `src/EmotePurge.Core/Services/IWishService.cs`
  (`DeleteAsync`, `WishOutcome.AccessDenied`, XML-Doku mit Ausgängen), `src/EmotePurge.Infrastructure/Services/WishService.cs`
  (Methode, Klassendoku „Locks“ um das Löschen ergänzen, [W4], [W5]); neu
  `tests/EmotePurge.Infrastructure.Tests/Integration/WishServiceTests.Delete.cs` (Teildatei der bestehenden Klasse, deren
  Seed-Helfer nutzen).
- **Tests zuerst (Regel 11):**
  - je Endzustand (sieben, Theorie) mit passendem Ende → Erfolg, Zeile weg, genau ein `wish.delete` mit `fromStatus` gleich
    dem Zustand, Details-Schlüssel genau die vier aus 2.3; je übrigem Zustand (sechs: `pending`, `approved`, `unconfirmed`,
    `failed`, `active`, `presented`) → `StateConflict`, Zeile und Audit unverändert;
  - Ende abweichend um eine Mikrosekunde → `StateConflict`; Ende mit sieben Nachkommastellen, das auf dieselbe Mikrosekunde
    fällt → Erfolg (Datenbankgenauigkeit);
  - Episoden (AK109): `removed_externally` (Ende T1) → Rückkehr durch den Abgleich → erneut `removed_externally` (T2 > T1)
    → mit T1 Konflikt, mit T2 Erfolg; `removed` → `ReAddAsync` → erneut `removed` → das erste Ende Konflikt;
    `removed_externally` → `removed` über `CompleteRemovalAsync` → das gezeigte Ende Erfolg (dieselbe Episode). Die Rückkehr
    und das zweite Ende über die echten Dienste herstellen, nicht per Hand gesetzt (die Zeitstempel sind der Prüfgegenstand);
    die Zeit mit dem `TimeProvider` der Fixture vorrücken;
  - zweites Löschen derselben ID → `WishNotFound`; Wunsch eines anderen Kanals → `WishNotFound`; `CompleteRemovalAsync`
    auf einen gelöschten `removed_externally`-Wunsch → `WishNotFound` (AK111, zweiter Satz);
  - Wirkung (AK110): Sperrzeilen in `WishSlotExclusions` weg; ein Kind (`merged` mit Verweis) bleibt `merged`,
    `MergedIntoWishId` null, sonst unverändert; Löschen eines `merged` lässt sein Ziel unverändert;
  - `kept` mit Slot gelöscht → `ListAdoptableSlotsAsync` nennt den Eintrag ohne Halter; danach `AdoptAsync` ohne anderen
    laufenden Wunsch → Erfolg; mit laufendem Wunsch desselben Emotes an einem zweiten Alias → `AlreadyRunning`;
    der Abgleich-Auslöser wird genau einmal mit der Kanal-ID gerufen; ein `kept` ohne Slot und jeder andere Zustand rufen ihn
    nicht; danach den Abgleich direkt laufen lassen: ein `approved` Wunsch desselben Emotes, dessen Fenster das `AddedAt`
    deckt, bekommt den Eintrag;
  - Dedupe: ein erfasster Wunsch, gelöscht, dann dieselbe Beobachtung (gleiche `IrcMessageId`) erneut durch den
    Erfassungsdienst → kein neuer Wunsch (Quittung hält); Helfer aus `WishCaptureServiceTests.Dedupe.cs` übernehmen;
  - genau ein `wish.changed` je Erfolg, keines bei einer Ablehnung; ein werfender Publisher macht den Commit nicht rückgängig
    (Muster `AFailingPublisher_DoesNotUndoTheCommit_NorThrow`);
  - Zugriff: `access` mit anderer `TwitchChannelId` als die Zeile, oder `IdConfirmed` falsch → `AccessDenied`, nichts
    geschrieben, kein Audit, kein Ereignis; `AccessDenied` kommt vor `WishNotFound` (unbekannte ID mit falschem Zugriff →
    `AccessDenied`);
  - Audit-Datenschutz (AK115): Alias, Emote-Name, Login und Einlösetext mit „Secret…“-Werten gesät, `DetailsJson`
    enthält keinen davon (Muster `AuditDetails_CarryOnlyIdsAndNumbers_NeverAliasNameOrLogin`).
- **Grenzfälle:** Kanalzeile während des Wartens gelöscht (Purge) → `ChannelNotFound`; der Wunsch gehört nach einer
  Kanal-Zusammenführung zum Überlebenden, der Zugriff nennt den Verlierer → `ChannelNotFound` oder `WishNotFound`, nie ein
  Löschen am falschen Kanal.
- **AKs:** AK109, AK110, AK114 (ohne Rennen), AK115 (zweiter Teil). `HasPendingModelChanges` deckt der bestehende
  `WishSchemaTests` ab; T1 ändert kein Modell.
- **Commit:** `feat: delete an ended wish in the wish service`

### T2: Sperren und Rennen

- **Ziel:** 2.2 Schritt 6 und 2.2a; der Nachweis von E32 und AK111, AK112, AK114 mit zwei Verbindungen.
- **Dateien:** `src/EmotePurge.Infrastructure/Persistence/RowLocks.cs` (neuer Eintrag, Klassendoku), `WishService.cs`
  (Schritt 6, Abfangen von `55P03`, Doku); neu `tests/EmotePurge.Infrastructure.Tests/Integration/WishServiceTests.DeleteRaces.cs`.
  Vorbilder: `Fixtures/CommandHook.cs` (Eingriff nach einem bestimmten Befehl, während die Sperren gehalten werden),
  `WishServiceTests.Threshold.cs` und `WishExpiryServiceTests.Races.cs` (Warten auf eine blockierte Verbindung über
  `pg_stat_activity`, `wait_event_type = 'Lock'`).
- **Tests zuerst:**
  - zwei gleichzeitige Löschungen desselben Wunsches → genau ein Erfolg, ein `WishNotFound`, genau ein Audit-Eintrag;
  - **AK112 a, Kind gesperrt:** eine zweite Verbindung öffnet eine Transaktion und führt auf einem Kind genau die Anweisung
    aus, die die Retention ausführt (Löschen der Zeile bzw. Leeren der Einlöser-Felder), und hält sie offen → das Löschen
    des Ziels antwortet `StateConflict` **ohne zu warten** (Zeitgrenze im Test deutlich unter jedem Sperr-Timeout), Ziel und
    Kind unverändert, kein Audit; danach die zweite Verbindung freigeben;
  - **AK112 a, der echte Retention-Lauf:** per `CommandHook` am Kontext des Löschens hinter der Kindersperre anhalten (Kanal,
    Ziel und Kind gesperrt), in diesem Moment `DataRetentionService.RunAsync(enforce: true)` auf eigener Verbindung starten
    (Ziel und Kind sind fällig gesät, dazu mindestens eine fällige Erfassungslücke; Kanäle und Systemlast, soweit die
    Seed-Helfer aus `DataRetentionServiceTests.Wishes.cs` und den Nachbardateien sie billig liefern), warten, bis sie
    blockiert, das Löschen fortsetzen → das Löschen gelingt, die Retention endet ohne Ausnahme
    und hat die späteren Kategorien ausgeführt; Postgres meldet keine Verklemmung (keine `40P01`);
  - **AK112 b, Ziel gesperrt:** die zweite Verbindung hält das Ziel (Retention-Anweisung auf dem Ziel, offen) → das Löschen
    wartet (blockiert sichtbar in `pg_stat_activity`); Commit der zweiten Verbindung → `WishNotFound`; Rücknahme →
    Erfolg;
  - **AK114 unter Sperre:** Der Aufrufer hält einen Zugriff auf die alte `TwitchChannelId`. Eine zweite Verbindung sperrt
    die Kanalzeile (wie die Identitätsprüfung), ändert die ID und hält die Transaktion offen → das Löschen wartet sichtbar an
    der Kanalsperre; nach dem Commit der zweiten Verbindung liest es die neue ID → `AccessDenied`, nichts geschrieben, kein
    Audit, kein Ereignis. (Der Fall ohne Wettlauf liegt in T1.)
  - ein `NOWAIT`-Fehlschlag lässt den Kontext sauber zurück: derselbe Dienst-Scope kann danach erneut löschen (Transaktion
    zurückgenommen, Tracker geleert).
- **Grenzfälle:** keine Kinder (Schritt 6 schickt keine Anweisung); viele Kinder (aufsteigend in einer Anweisung); ein Kind
  wurde zwischen Lesen der IDs und Sperre von der Retention gelöscht (fehlt im Ergebnis, kein Fehler).
- **AKs:** AK111 (erster Satz), AK112, AK114.
- **Commit:** `feat: lock merged children without waiting when deleting a wish`

### T3: Api

- **Ziel:** 2.1, 2.4.
- **Dateien:** neu `src/EmotePurge.Api/Auth/WishIdAccessFilter.cs` (ggf. gemeinsamer Helfer mit `WishCaptureAccessFilter`),
  `src/EmotePurge.Api/Endpoints/WishEndpoints.cs` (Route, `MapResult`-Fall `AccessDenied` → `Results.Forbid()`, Klassendoku),
  `tests/EmotePurge.Api.Tests/WishEndpointsTests.cs` bzw. eine neue Datei `WishDeleteEndpointsTests.cs` [E, wenn die
  bestehende Datei zu groß wird]; `docs/DECISIONS.md` (Absatz „Implementation (#500)“ im bestehenden Eintrag vom 2026-11-21,
  `**Betrifft:**`-Zeile um Filter und Route ergänzt, Regel 3 — die Route ist ein neuer Vertrag).
- **Tests zuerst (Regel 11, neuer `IEndpointFilter`):**
  - die Route in die `Routes`-Theorie aufnehmen (Methode `DELETE`, Pfad mit gültiger Query), damit 400 Name, 401, 403
    Nicht-Verwalter, 404 `wishes_disabled` für Verwalter, 403 für Nicht-Verwalter bei ausgeschaltetem Schalter automatisch
    gelten (AK113); 7TV-Editor ohne Mod-Status → 403 wie AK13;
  - gleicher Login, andere Twitch-ID mit echtem `ChannelAccessService` (Muster `WishCaptureEndpointsTests.cs:605-644`)
    → 403 und **kein** Aufruf von `DeleteAsync`; mit passender ID → 204;
  - Zeile ohne Twitch-ID → 409 `wish_channel_unbound`, kein Aufruf; unbekannter Kanal → 404 `channel_not_found`;
  - Bot-Modus, auf der ID bestätigter Twitch-Mod ohne Aktivierung → 204 (kein Vorgaben-Filter, E39);
  - Query fehlt, leer, unlesbar → 400 `invalid_request_body`, kein Aufruf; gültige Query → der Dienst bekommt den Zeitpunkt als
    UTC mit voller Genauigkeit, die ID, den Akteur und **dasselbe** Zugriffsobjekt wie der Filter;
  - Abbildung: `EveryOutcome_HasAMappingCase_InThisTheory` und die Fehler-Theorie um `AccessDenied` → 403 ohne Rumpf
    erweitern; Erfolg → 204 ohne Rumpf;
  - Schalter aus: Query wird nicht geprüft (404 vor 400).
- **Gate zusätzlich:** `api-error-locales.spec.ts` grün (Vitest), obwohl sich nichts ändert (AK117 erster Teil).
- **AKs:** AK113.
- **Commit:** `feat: expose deleting an ended wish`

### T4: Web-Kern

- **Ziel:** 2.3 (Web), 2.5, die reinen Regeln aus 2.6, alle i18n-Schlüssel.
- **Dateien:** `web/src/app/core/wishes/wish-view.ts` (`WishAction` um `delete`, `wishActions`, `isConfirmedAction`, Doku),
  `wish.service.ts` (`deleteWish`), `wish-removal-tracker.ts` (Unterscheidung „gelöscht“ an `notBooked`, [W1]),
  `web/src/app/features/wishes/wish-operation.ts` (Text für „gelöscht“), `web/src/app/core/audit/audit.model.ts`,
  `web/src/app/shared/audit/audit-actions.ts`, `web/public/i18n/de.json`, `en.json`; Specs co-located
  (`wish-view.spec.ts`, `wish.service.spec.ts`, `wish-removal-tracker.spec.ts`, `wish-operation.spec.ts`,
  `audit-actions.spec.ts`).
- **Tests zuerst (Regel 12):**
  - `wishActions` als Tabelle über alle dreizehn Zustände: beendete mit `endedAtUtc` wie 2.6, `delete` jeweils zuletzt, `kept`
    nur `delete`; ohne `endedAtUtc` kein `delete`; laufende und offene Zustände unverändert; `isSevenTvAction('delete')`
    falsch, `isConfirmedAction('delete')` wahr;
  - `deleteWish`: `DELETE` an den richtigen Pfad, Query `endedAtUtc` gleich dem übergebenen String Zeichen für Zeichen
    (Beispiel mit sieben Nachkommastellen und `Z`, [W6]), kein Rumpf;
  - Entfernungsbericht: 404 mit `wish_not_found` → „nicht gebucht, gelöscht“; 404 mit anderem Code und 409 →
    `notBooked` wie bisher; der Zeilentext unterscheidet beide;
  - Audit: `wish.delete` hat einen Übersetzungsschlüssel (bestehende Vollständigkeitsprüfung in `audit-actions.spec.ts`).
- **Nebenwirkung prüfen:** `wish-entry-dialog.ts` und `wish-adopt-dialog.ts` fragen `wishActions(...).includes('adopt'|
  'extend'|'edit')`; `delete` ändert daran nichts (die vorhandenen Specs bleiben grün).
- **AKs:** AK116 (Aktionsliste, Entfernungsbericht), AK117 (Schlüssel in beiden Sprachen).
- **Commit:** `feat(web): add the delete action and its client contract`

### T5: Web-Seite

- **Ziel:** 2.6 in `channel-wishes-page.ts`, `wish-row.ts`, `wish-adopt-dialog.ts` (nur über den i18n-Text).
- **Dateien:** `web/src/app/features/wishes/channel-wishes-page.ts` (`OFFERED_ACTIONS`, `onAction` mit eigenem Zweig für
  `delete`: Bestätigungstext nach Zustand, festgehaltenes Ende, Ausgang, Fokus, Hinweis; Seitenhinweis „gelöscht“ des
  Entfernungsberichts neben dem bestehenden `notBooked`-Hinweis), `wish-row.ts` (`DESTRUCTIVE_ACTIONS`, [W7]); Specs
  `channel-wishes-page.spec.ts`, `wish-row.spec.ts`.
- **Tests zuerst (Regel 12: Verhalten, nicht Vorlage):**
  - Löschen öffnet die Bestätigung; Abbrechen sendet nichts; Bestätigen sendet das Ende der Zeile, auch wenn eine
    Listen-Neuladung die Zeile zwischen Öffnen und Bestätigen ersetzt hat (festgehaltener Wert);
  - Bestätigungstext: `kept` nimmt `wishes.confirm.deleteKept`, jeder andere Zustand `wishes.confirm.delete` (Schlüssel als
    Kennung, nicht Wortlaut);
  - 204 → Listen neu geladen, keine Fehlermeldung; 404 `wish_not_found` → ebenso, keine Fehlermeldung; 409 → Fehlermeldung
    der Zeile und Neuladen; 403 → Fehlermeldung, kein Neuladen über das Übliche hinaus;
  - nach 204 auf einen `kept` mit `slotId`: Hinweis sichtbar; sein Knopf öffnet den Übernahme-Dialog mit genau der
    `sevenTvEmoteId` des gelöschten Wunsches und ohne `wish`; ohne `slotId` oder bei anderem Zustand kein Hinweis;
  - Fokus: nach dem Löschen im Bereich „Beendet“ auf der nächsten Zeile, bei der letzten auf der vorigen, bei der einzigen
    auf der Überschrift; an der angehefteten Zeile kein „nicht mehr vorhanden“, Fokus nach 2.6;
  - `wish-row`: `delete` erscheint in der destruktiven Gruppe hinter `reAdd`, auch auf grobem Zeiger, und ist während einer
    7TV-Sperre der Seite **nicht** gesperrt (keine 7TV-Aktion);
  - Entfernungsbericht „gelöscht“: die Seite sagt das Entfernen als geschehen, mit dem eigenen Grund.
- **Grenzfälle:** Doppelklick auf „Löschen“ während eines laufenden Befehls derselben Zeile (das bestehende `pendingIds`
  sperrt); Kanalwechsel bei offener Bestätigung (Ergebnis verwerfen, wie die übrigen Dialoge); `wish.changed` während der
  Bestätigung entfernt die Zeile schon (das Senden antwortet 404 → still erledigt).
- **UI-Prüfliste** „Neue UI bauen“ (`web/.claude/CLAUDE.md`) abarbeiten; der neue Hinweis folgt der Form von `mergedNotice`
  (Rolle, Fokusführung, Schließen).
- **AKs:** AK116 (Bestätigung, Ende, 404, Hinweis), AK119-Vorbereitung.
- **Commit:** `feat(web): delete ended wishes from the wishes page`

### T6: E2E und UI-Audit

- **Ziel:** AK118 und der zweite Teil von AK116 („`wish.changed` lädt ohne Reload, auch zwei Ereignisse kurz nacheinander“).
- **Dateien:** `web/e2e/support/mocks.ts` ([W2]: Art `delete`, Feld `endedAtUtc` in `MockWishCall`, Standardantwort 204 und
  Entfernen aus der Liste, die den Wunsch hält; über `onCommand` überschreibbar), `web/e2e/wishes.e2e.spec.ts`.
  Specs importieren `test`/`expect` aus `web/e2e/support/test.ts`.
- **Fälle:**
  - Leitfall (Y3): behaltener Wunsch mit Slot im Bereich „Beendet“ → „Löschen“ → Bestätigung → `DELETE` mit dem Ende der Zeile
    → Zeile weg → Hinweis → „Bestand übernehmen“ → Dialog mit dem freien Eintrag → 7 Tage → `adopt` mit `durationDays` 7 →
    neuer Wunsch im Bereich „Laufend“;
  - „Zum Wunsch“ aus dem Übernahme-Dialog (gesperrter Eintrag mit dem neuen Sperrtext) → angeheftete Zeile mit „Löschen“;
  - dasselbe wie der Leitfall in mobiler Breite (360 px, grober Zeiger): Bestätigung als Sheet, Fokus nach dem Löschen
    sichtbar und nicht unter der Aktionsleiste;
  - zwei `wish.changed` kurz nacheinander nach dem Löschen → die Listen zeigen den neuen Stand ohne Seiten-Reload (bestehender
    Fall „wish.changed reloads the lists“ als Vorlage, Zeitsteuerung über `page.clock` nach den Regeln in `CLAUDE.md`).
- **UI-Audit:** Das Szenario `wishes-page` (`web/e2e/audit/ui-audit.audit.ts`) öffnet „Beendet“ schon und zeigt damit die
  neuen Knöpfe; einmal laufen lassen (`cd web && npx playwright test --config=playwright.audit.config.ts -g wishes-page`)
  und die Gates für kleine Ziele und Überlauf ansehen. Keine neue Szenarien-Datei.
- **Gate:** `npm --prefix web run e2e` grün, **ohne** Api auf `:5151`.
- **AKs:** AK118, AK116 (Live-Ereignisse).
- **Commit:** `test(web): e2e for deleting ended wishes`

### T7: Doku und Coverage

- **Dateien:** `docs/DECISIONS.md` (nur, falls T1 bis T6 von diesem Plan oder der Spec abweichen: Absatz im Eintrag vom
  2026-11-21), `docs/Feature-Ideen-2026-08-01.md` (Statuszeile `A18`: K7a umgesetzt, Deploy offen), `CLAUDE.md`
  (Zeile „Wunsch-Emotes“ im Umsetzungsstand: K7a umgesetzt, Route, kein Schalter, keine Migration). `docs/Operations.md`
  bekommt nichts (kein Schalter, keine Konfiguration); `docs/Architectur.md` nur, falls dort die Wunsch-Routen aufgezählt
  sind (prüfen, dann eine Zeile).
- **Coverage:** `node scripts/coverage-local.mjs` (beide Suiten). Erwartung: neue Dateien (`WishIdAccessFilter.cs`, die
  Teildateien der Tests zählen nicht) nahe 100 %; die chirurgischen Änderungen in `WishService.cs`,
  `channel-wishes-page.ts` und `mocks.ts` zieht die dateigenaue Näherung in beide Richtungen ([CLAUDE.md, Tests]); maßgeblich
  ist Sonars 80 % auf neuem Code. Bleibt eine geänderte Datei auffällig, die neuen Zeilen gezielt mit einem Test decken,
  nicht die Bestandsdatei.
- **Commit:** `docs: record wish deletion (child 7a)`

---

## 4. AK-Abdeckung

| AK | Inhalt | Task(s) |
|---|---|---|
| AK109 | Zustände, Episode, 400/404 | T1 (Dienst), T3 (400 Query, 204/404 an der Route) |
| AK110 | Kaskade, SET NULL, Slot frei, `adopt`, Folgezuordnung, Dedupe | T1 |
| AK111 | zwei `DELETE`; `complete-removal` auf gelöschten Wunsch → 404 | T2 (erster Satz), T1 (zweiter Satz) |
| AK112 | Retention, zwei Verbindungen, keine Verklemmung | T2 |
| AK113 | Filter-Matrix, gleicher Login andere ID, unbound, Bot-Modus, Abbildung | T3 |
| AK114 | ID-Wechsel zwischen Filter und Sperre | T1 (Zugriffsprüfung), T2 (mit Sperre) |
| AK115 | keine Migration; keine Personendaten im Audit | T1; T9 (`dotnet ef migrations has-pending-model-changes`) |
| AK116 | Oberfläche | T4, T5, T6 |
| AK117 | Locales grün, neue Schlüssel, „kein Fehlercode“ im PR | T3 (Gate), T4 (Schlüssel), T9 (PR-Text) |
| AK118 | E2E Leitfall, angeheftete Zeile, mobil | T6 |
| AK119 | Live | T8 |

---

## 5. Live-Verifikation (T8, AK119, Regel 16)

Lokaler Stack, echtes 7TV, Probe-Kanal und Testkonto wie im Live-Protokoll K6 (`docs/nachtlaeufe/live-486-2026-11-20.md`;
dort K1 = Konto des Betreibers, Twitch-Mod und 7TV-Editor im Probe-Kanal; K2 = Testkonto). Protokoll als
`docs/nachtlaeufe/live-500-<Datum>.md` (deutsch, ohne Kontonamen). **Eine Anweisung je Schritt**, auf Rückmeldung warten.

**Vorbedingungen (Orchestrator):** Images aus dem Worktree-Stand gebaut und **aus dem Haupt-Checkout** gestartet (nie
`docker compose` aus dem Worktree); `--build` bzw. `--force-recreate` nach den Fallen des Dev-Stacks; `Wishes:Enabled` und
`Wishes:Capture:Enabled` an; keine ausstehende Migration in der Dev-DB (`dotnet ef migrations list` gegen die Dev-DB:
nichts `(Pending)`); zweiter Browser-Tab auf derselben Seite offen.

| Schritt | Handlung (Betreiber) | Erwartung / Nachweis |
|---|---|---|
| L0 | — (Orchestrator, SQL an der Dev-DB) | keine Zeile mit beendetem Zustand und leerem `EndedAtUtc` ([W9]) |
| L1 | Im Probe-Set ein Emote über 7TV hinzufügen (Testkonto darf schreiben), Seite „Wünsche“ → „Bestand übernehmen“ → den Eintrag mit 30 Tagen übernehmen | neuer Wunsch `active` im Bereich „Laufend“; Tab 2 zeigt ihn ohne Reload |
| L2 | Am Wunsch „Behalten“ bestätigen | Zeile im Bereich „Beendet“ als `kept`, Aktion „Löschen“ sichtbar |
| L3 | „Bestand übernehmen“ öffnen | der Eintrag ist gesperrt, neuer Sperrtext, nur „Zum Wunsch“ |
| L4 | „Zum Wunsch“ → an der angehefteten Zeile „Löschen“ → Text der Bestätigung lesen → bestätigen | Bestätigung nennt den Weg über „Bestand übernehmen“; Zeile weg, Hinweis mit „Bestand übernehmen“, Fokus sichtbar; Tab 2 ohne Reload aktuell |
| L5 | — (Orchestrator) | SQL: Zeile weg; `wish.delete` im Audit-Log mit `fromStatus = kept`, Details ohne Alias/Namen; das Audit-Log im Admin-Bereich zeigt die Aktion übersetzt; 7TV-Set per Lesezugriff unverändert (Eintrag noch da) |
| L6 | Im Hinweis „Bestand übernehmen“ → 7 Tage → übernehmen | neuer Wunsch (neue ID) `active`, Ablauf in 7 Tagen ab dem gewählten Start |
| L7 | Mit K2 im Probe-Kanal die Wunsch-Belohnung mit Text einlösen; den offenen Wunsch ablehnen; im Bereich „Beendet“ löschen | 204; Zeile weg; Audit `wish.delete` mit `fromStatus = rejected`, ohne Einlösetext |
| L8 | Einen laufenden Wunsch über die Karte aus 7TV entfernen (wird `removed`), dann löschen | 204; der Eintrag ist aus dem Set (vom Entfernen), der Wunsch weg |
| L9 | Mobile Breite (Handy über den `lan`-Start oder Browser-Emulation) an einer beendeten Zeile: Löschen, Bestätigung als Sheet | Fokus sichtbar über der Aktionsleiste |
| L10 | — (Orchestrator) | Probe-Set auf den Stand vor L1 zurücksetzen (übernommenen Wunsch schließen, Emote aus dem Set entfernen); Stand im Protokoll |

**Nicht live** (durch Tests gedeckt): der Wettlauf mit der Retention, der Identitätswechsel, `complete-removal` gegen ein
gleichzeitiges Löschen (N3). Bestanden ist AK119, wenn L1 bis L8 und L10 halten; L9 darf als „teilweise“ stehen, wenn nur
emuliert.

---

## 6. Gates, Zweitmeinung, Auslieferung (T9)

1. `dotnet test EmotePurge.slnx` (Docker), `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e` (ohne Api auf
   `:5151`), `npm --prefix web run lint`, `npm --prefix web run format:check`, `dotnet format EmotePurge.slnx
   --verify-no-changes`.
2. **Keine Migration nachweisen:** `dotnet ef migrations has-pending-model-changes --project src/EmotePurge.Infrastructure
   --startup-project src/EmotePurge.Api` meldet nichts, und `git diff --stat main -- src/EmotePurge.Infrastructure/Migrations`
   ist leer.
3. `node scripts/coverage-local.mjs`, Ergebnis in den PR.
4. Zweitmeinung vor dem Merge (Regel 22): `/codex:review --model gpt-6.2-sol` über den fertigen Branch, einmal; Ergebnis
   unverändert dem Betreiber vorlegen. Bei Widerspruch zu einem Opus-Review entscheidet Fable (global geregelt).
5. PR (englisch): Inhalt, „no new error code“ (AK117), „no migration, Api image only“, Testzahlen, Live-Protokoll, Bezug
   `Closes #500`, Teil von #481. Mergen tut der Betreiber (Regel 1).

**Auslieferung:** Nur das Api-Image (enthält das Frontend); der Worker ist unberührt (ein fehlender Wunsch ist für alle
Läufe schon heute ein normaler Fall). Kein Schalter, keine Compose- oder Runbook-Änderung. **Vor dem Deploy** den
Deploy-Hinweis gegen alle ausstehenden Prod-Migrationen prüfen (Befehle für den Betreiber aufbereiten: Tunnel und
`dotnet ef migrations list` mit Passwort-Platzhalter, nicht selbst verbinden); erwartet: nichts `(Pending)`.
**Rollback:** voriges Api-Image per `sha`-Tag; gelöschte Wünsche kommen nur aus dem Backup zurück, sonst ändert sich nichts.

---

## 7. Risiken

| Risiko | Umgang |
|---|---|
| Der Client verliert Mikrosekunden von `endedAtUtc`, jedes Löschen gibt 409 | Vertrag 2.5, Test in T4 mit sieben Nachkommastellen, Live-Schritt L4 |
| EF lädt Kinder mit und setzt den Fremdschlüssel selbst (zusätzliche Sperren, andere Reihenfolge) | 2.2 Schritt 7: Kinder nie als Entitäten laden; T1-Test „Kind unverändert außer Verweis“ |
| `NOWAIT`-Fehler hinterlässt eine abgebrochene Transaktion im Scope | T2-Test „derselbe Scope löscht danach erneut“ |
| Der Retention-Test ist zeitabhängig und wackelt | Blockade über `pg_stat_activity` abwarten statt schlafen (Vorbilder in T2) |
| RR15 (Login-Nachtrag der ID) | hingenommen (Spec), als H4 bei #496 kommentiert |
| Ein Folge-Abgleich ordnet den freien Eintrag einem offenen Wunsch zu, bevor der Verwalter übernimmt | gewollt (E35); der Hinweis führt in den Dialog, der dann den Halter zeigt |

---

## 8. Offene Fragen an den Betreiber

1. **Text im Eintrag-Dialog:** `wishes.entry.conflict.kept` („Dieses Emote ist bereits als behaltener Wunsch geführt.“)
   nennt den neuen Weg nicht. Die Spec ändert nur den Sperrtext im Übernahme-Dialog. Soll der Eintrag-Dialog denselben
   Hinweis bekommen (eine Übersetzung, kein Verhalten)? Vorschlag: ja, in T5.
2. **Fokus nach dem Löschen der angehefteten Zeile** (2.6 [E]): Anheftung leeren und auf den Hinweis bzw. die Überschrift
   „Beendet“ springen, statt „nicht mehr vorhanden“ zu zeigen. Einverstanden?
3. **Hinweis nach dem Löschen** auf das Emote statt auf den einen Eintrag verengt (2.6 [E]). Einverstanden?
4. **`delete` nur mit gesetztem `endedAtUtc`** ([W9] [E]): rein defensiv, nach dem Code nicht erreichbar.

---

## 9. Selbstprüfung des Plans

- **Spec-Abdeckung:** E32 (T1, T2), E34 (T1), E35 (T1, T2), E36 (T1, T4), E37 (kein Code nötig: das Löschen ändert keinen
  Zähler; ein Folge-Abgleich verhält sich wie jede Zuordnung — kein eigener Test, der Abgleich ist getestet), E38 (T1, T3, T5),
  E39 (T3), E40 (T9), E41 (T1, T2, T3), E44 (T1, T3, T4), N3 (T4, T5), Leitfall Y3 (T5, T6, T8), AK109 bis AK119 (Abschnitt 4).
- **Kein fertiger Code:** Signaturen nur als Form, keine Methodenrümpfe, keine Abfragen ausformuliert.
- **Namen über Tasks hinweg:** `DeleteAsync`, `WishOutcome.AccessDenied`, `AuditActions.WishDelete`, `WishIdAccessFilter`,
  `deleteWish`, Aktion `delete`, Mock-Art `delete`, Schlüssel aus 2.6 — einheitlich.
- **Regel 7:** kein neuer Fehlercode (0.1), nur `audit.actions.wishDelete` und UI-Schlüssel.
- **Regel 11/12:** Dienst und `RowLocks` → `Infrastructure.Tests/Integration`; neuer Filter → `Api.Tests`; Handler bleibt
  dünn; `core/`-Logik und die Entscheidungslogik der Seite → co-located Vitest, nur Verhalten.
- **Umfang:** sieben Implementer-Tasks für 1,5 Tage Aufwand, T8/T9 ohne Implementer. Der Plan ist länger als der
  Spec-Teil K7a, weil er Testabsichten je Task und den Live-Ablauf ausschreibt; Code enthält er keinen.
