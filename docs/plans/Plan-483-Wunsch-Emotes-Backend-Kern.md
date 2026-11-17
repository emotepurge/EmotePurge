# Plan #483: Wunsch-Emotes, Kind K1: Backend-Kern

Erstellt am 2026-11-17 im Worktree `EmotePurge-wt-483` (Branch `feat/483-wish-core`, Basis `docs/wunsch-emotes-design` @ `14439289`).
**Status: Revision 1 (2026-11-17). Betreiberfrage F1 entschieden (Variante c), Sol adversarial durch (5 × P2 eingearbeitet). Der Branch
liegt inzwischen auf `origin/main`; die Codestellen sind unverändert. Keine offene Betreiberfrage, eine Lesart zur Bestätigung (Abschnitt 10).**
Quellen: [Spec](../superpowers/specs/2026-11-17-wunsch-emotes-spec.md) Abschnitte 1 bis 17 und „K1: Backend-Kern“ (AK1 bis AK21); aus K2,
K3, K6 und K4 nur die Verträge, die K1 ihnen liefert; Issue #483, Epic #481; `CLAUDE.md` (Schichten, Regeln 1 bis 22, Gates, Sprache,
Prod-Migration von Hand). Formatvorlage: Plan-379, Plan-384.

**Was dieser Plan trägt:** Abweichungen zwischen Spec und Code, die Planer-Entscheidungen dazu, den Schnitt in Tasks mit Verträgen,
Grenzfällen und Tests, die AK-Zuordnung, die Live-Verifikation, Gates, Migration und Deploy. Kein Code; Namen und Signaturen nur als
Form, Schema nur als Skizze. Die Spec reist mit: Jeder Implementer liest seinen Task hier **und** die genannten Spec-Abschnitte.

**Lesehinweis:** **[E]** Planer-Entscheidung (der Betreiber kann widersprechen) · **[P]** Präzisierung ohne Entscheidungsgehalt ·
**[B]** Betreiberentscheidung 2026-11-17 · **Sol-P2-*n*** Befund des adversarialen Reviews (gpt-6.1-sol), am Code geprüft und
eingearbeitet · „Spec 6.3“ meint den Abschnitt der Spec, „AK7“ das Akzeptanzkriterium aus K1.

### Revision 1 (2026-11-17): was sich geändert hat

| Änderung | Anlass |
|---|---|
| Fenster von `removed_externally`: 30 Minuten Rückgriff vor `EndedAtUtc`, Kandidat nur mit dem Alias des Wunsches; damit höchstens ein Kandidat (Befund 6, T3, T7, Abschnitt 5) | [B] F1, Sol-P2-5 |
| Generation des verknüpften Slots am Wunsch (`SlotAddedAtUtc`, `SlotConfirmedAtUtc`); reaktivierte eigene Zeile ist Kandidat; verpasster Archiv-Hook heilt (Befund 19, T7, T8) | Sol-P2-1 |
| Befehle und Abgleich lesen und schreiben einen Wunsch nur unter Zeilensperre; Reihenfolge Kanal vor Wunsch (2.3, T5, T7) | Sol-P2-2 |
| Abgleich schreibt je Wunsch einzeln hinter einem Savepoint, mit Vorfilter nach laufenden Emote-IDs (T7) | Sol-P2-3 |
| `FirstActivatedAtUtc`, unveränderlich; E14 (c) hängt daran statt an `StartsAtUtc` (Befund 20, T1, T2, T9) | Sol-P2-4 |

---

## 0. Befund vor dem Plan

Alle Stellen sind am Stand `14439289` nachgelesen. Wo die Spec eine Zeile nennt, die nicht trägt, steht die tragende daneben.

1. **Spalte `LinkOrigin` fehlt in der Tabelle 6.1**, steht aber im `WishDto`, in 6.3 („K1 baut … die Spalte `LinkOrigin`“) und in den
   Übergängen (`LinkOrigin = adopted`). **[E]** `AddWishes` legt `LinkOrigin` an: text, null, Werte `auto`, `adopted`, `bot`.
2. **Die Hook-Stellen der Spec in `EmoteService` tragen nicht.** `EmoteService.cs:733` und `:800` liegen in den statischen Helfern
   `RestoreSlots` und `ArchiveSlot`, ohne Datenbankzugriff. Der einzige Commit dieser Slot-Schreiben ist
   `MarkInSetLockedOnceAsync` (`:374-375`), **eine** Transaktion über alle Trefferkanäle, mit der Wiederholung in `MarkInSetAsync`
   (`:227`). **[E]** Der Abgleich läuft nach der Rückkehr von `MarkInSetAsync`, einmal je Trefferkanal mit Änderung. Das Ergebnis
   trägt heute nur Kanalnamen; es bekommt intern die Kanal-IDs der geänderten Treffer. Die Wege `MarkDeletedAsync`,
   `MarkRestoredAsync`, `MarkImportedAsync`, `MarkImportedToSetAsync` schreiben nach Durchsicht keine Slots (T8 prüft das gegen).
3. **Der Delta-Weg hat drei Commit-Punkte**, alle in `ApplySlotDeltaAttemptAsync`: `:533` (nur Leave, kein Inventar), `:563`
   (angewendet), `:605` (mehrdeutig, kein Inventar). `ApplyEmoteSetUpdateAsync:259` ist der Methodenkopf. **[E]** Der Abgleich hängt am
   Ergebnis `Applied`, in `ApplyEmoteSetUpdateAsync` nach der Rückkehr des Versuchs, also außerhalb der Wiederholungsschleife.
   Beim Vollsync stimmt `:209`: nach `SaveSyncAsync` mit Ergebnis ungleich null.
4. **Geteilter `AppDbContext`.** Sync und Bericht arbeiten mit einem scoped Kontext voller getrackter Zeilen. Ein Abgleich im selben
   Kontext, der scheitert, ließe getrackte Wunsch-Änderungen zurück, die das nächste `SaveChangesAsync` des Syncs mitschriebe.
   **[E]** `WishReconcileService` arbeitet in einem eigenen Scope mit eigenem Kontext (`IServiceScopeFactory`, Muster
   `AdminDashboardReader`), nie auf dem Kontext des Aufrufers.
5. **D7-Wiederaufnahme: „neuer Slot“ ist oft derselbe Slot.** Wird ein Emote unter gleichem Alias entfernt und wieder hinzugefügt,
   reaktiviert der Vollsync die archivierte Slot-Zeile (`EmoteSlotReconcilePlanner.ResumeArchivedSlots`, gleiche `Id`, neues
   `AddedAt`, `ArchivedAt` wieder null); der Restore-Bericht ebenso (`EmoteService.RestoreSlots`, vorläufiges `AddedAt` = jetzt).
   AK10 spricht von einem neuen Slot. **[E]** Ein Wunsch behält `SlotId` in jedem Endzustand; der partielle Eindeutigkeitsindex
   bindet nur `active` und `presented`. Für `removed_externally` ist die reaktivierte eigene Zeile Kandidat wie jede andere
   (Sol-P2-1). Was daraus für die Erkennung folgt, steht in Befund 19.
6. **„Ohne Toleranz“ gegen 7TVs Daten, entschieden [B] F1 (Variante c).** Fensterbeginn von `removed_externally` war nach Spec
   `EndedAtUtc`, also der Zeitpunkt, zu dem **wir** das Entfernen beobachten; `AddedAt` des Ersatzes ist 7TVs Datum und liegt
   davor, wenn (a) Entfernen und Wieder-Hinzufügen zwischen zwei Vollsyncs fallen oder (b) der REST-Cache 10 bis 30 Minuten
   nachhängt (Konstante `TagLeaveCredibilityWindow`, gleicher Grund). **[B]** Für `removed_externally` gilt: Kandidat ist nur ein
   Slot derselben Emote-ID, der **den Alias des Wunsches trägt** (ordinal) und dessen `AddedAt` nicht vor `EndedAtUtc` minus
   30 Minuten liegt. `approved` bleibt ohne Toleranz. **Abweichung von der Spec** (6.3 „ohne Toleranz“, Runde 5), vom Betreiber
   entschieden. Folgen: Aliase sind im Set eindeutig, es gibt also höchstens einen Kandidaten, und „genau ein Kandidat“ aus AK10
   ist erfüllt oder nicht, ohne Stichentscheid (Sol-P2-5). Ein Wieder-Hinzufügen unter **anderem** Namen wird nicht übernommen;
   der Ausweg ist „Bestand übernehmen“ als neuer Wunsch (der alte bleibt `removed_externally`). Lesart von AK10 in Abschnitt 10.
7. **`EmoteSlot.AddedAt` ist nullbar** („unbekannt“, bei undatierten Einträgen), dazu gibt es unsettled Seeds und Backfill-Slots
   („war nie drin“: archiviert, `ArchivedAt` null). **[E]** Kandidat ist nur ein Slot mit `IsArchived = false`,
   `SeedUnsettled = false` und gesetztem `AddedAt`; der nächste datierte Sync holt ihn nach. `adopt` auf einen aktiven Slot ohne
   Datum und ohne `startsAtUtc` startet vorläufig „jetzt“ mit `StartsAtAuthoritative = false`; AK8 verschiebt später. `adopt` auf
   einen archivierten oder unsettled Slot: 404 `wish_slot_not_found`.
8. **Route `recheck` ohne erfüllbaren Übergang in K1.** Ihr einziger Übergang (`presented` mit Grund `unconfirmed` → `unconfirmed`)
   braucht den Bot und den Code `wish_bot_unavailable`, der zu K4 gehört. **[E]** K1 registriert die Route und die Zeile in
   `WishTransitions`; der Befehl antwortet in K1 für jeden Zustand 409 `wish_state_conflict`. K4 füllt ihn. Im PR benennen.
9. **Regel 7 gegen Spec.** Die Spec legt die Frontend-Hälfte der zwölf Codes in K3. Regel 7 verlangt Code, `api-error.ts` und beide
   Locales zusammen; genau an dieser Naht ist die Liste zweimal auseinandergelaufen. **[E], Projektregel geht vor:** K1 trägt die
   zwölf Codes in `api-error.ts`, `de.json` und `en.json` ein (`errors.api.wish_*`, `errors.api.wishes_disabled`). AK43 in K3 wird
   damit zur Gegenprobe. Die Audit-Übersetzungen `audit.actions.wish.*` bleiben bei K3 (Risiko R6).
10. **`WishOptions` in Core.** Das genannte Muster `ChatLogBackfillOptions` liegt in Infrastructure, nicht in Core. **[E]** `WishOptions`
    bleibt wie in der Spec in Core (reines POCO mit `Validate()`, keine Paketabhängigkeit; K2 braucht es von Core aus). Gebunden
    wird wie `ServiceCollectionExtensions.cs:338-341`.
11. **Sperrreihenfolge.** `Wishes` hängt zweifach am Kaskadenbaum (`ChannelId` Kaskade; `SlotId` SET NULL, der Slot stirbt über
    Kanal → Emote → Slot). Wer `SlotId` setzt, hält eine Schlüsselsperre auf der Slot-Zeile; der Purge nimmt zuerst die Kanalzeile
    (Z6). **[E]** Jede Wunsch-Transaktion, die Slots liest und `SlotId` schreibt (Abgleich, `adopt`), sperrt zuerst die Kanalzeile
    über `RowLocks.LockChannelsByIdsAsync`. Ist die Zeile weg: Abgleich ohne Wirkung, Befehl 404.
12. **Ort der Veröffentlichung.** Bestehende Live-Events aus der Api veröffentlicht der Handler; der Abgleich läuft aber im Worker
    (Sync) **und** in der Api (Bericht). **[E]** `wish.changed` veröffentlicht der Dienst in Infrastructure nach seinem Commit,
    schlucken und loggen wie `ChatLogImportRemovalService.cs:214`.
13. **AK15, „binnen 5 Minuten kein Helix-Aufruf“** trägt der bestehende `ModRoleCache` (TTL `Auth:ModCheckCacheTtlMinutes`,
    Default 10). **[E]** kein eigener Cache in K1. Setzt ein Betreiber die TTL unter 5, gilt die Zusage nicht; das steht im
    DECISIONS-Eintrag.
14. **Retention-Summenzeile.** `RetentionRunSummary` (Core) wird in `RetentionRunSummaryFormatter` und `DataRetentionWorker`
    (Worker) gelesen und in `Worker.Tests` geprüft. Die Dateiliste der Spec nennt nur `DataRetentionService.cs:94-120`. T9 fasst
    alle vier an.
15. **Kanal-Zusammenführung:** Stellen der Spec stimmen (`ChannelIdentityService.cs:902-903` Verweigerung, `:1009` Löschen des
    Verlierers). **[E]** Hat der Überlebende keine Zeile in `ChannelWishSettings`, wandert die des Verlierers; hat er eine, gewinnt
    sie und die des Verlierers fällt mit der Kaskade. „Jünger“ bei einer Indexkollision heißt größeres `CreatedAtUtc`, bei Gleichstand
    größere `Id`.
16. **Kontolöschung:** Stelle stimmt (`AccountDeletionService.cs:131-137`). **[P]** Geleert wird nach `RequesterTwitchUserId`. Ein
    Handeintrag trägt nur ein Login ohne ID und wird hier nicht getroffen; ihn leert die 30-Tage-Regel.
17. **`view` der Liste.** Nach den Definitionen der Spec fiele `approved` im Bot-Modus mit verfügbarem Bot in keine Sicht. **[E]**
    `open` = Handlungsbedarf, `running` = `active`, `unconfirmed` und jedes `approved`, das kein Handlungsbedarf ist, `ended` =
    Endzustände. In K1 ist jeder Kanal manuell, `approved` steht also immer in `open`.
18. **Doku über die Dateiliste hinaus.** **[E]** K1 ergänzt `docs/Operations.md` (englisch) um einen kurzen Abschnitt „Wish emotes“
    (Schalter, Migration, zwei Zeilen unter „Data retention“, RR9) und `CLAUDE.md` um eine Zeile im Umsetzungsstand. `Architectur.md`
    und `PRODUCT.md` bleiben beim Epic-Ende (Spec 12, Punkt 11) bzw. bei K4.
19. **Slot-Generation am Wunsch (Sol-P2-1), Abweichung von Spec 6.1: zwei neue Spalten.** Ein reaktivierter Slot trägt dieselbe
    `Id` und denselben Alias, `ArchivedAt` ist wieder null; läuft der Abgleich zwischen Archivieren und Reaktivieren nicht oder
    scheitert er (T8 schluckt Fehler bewusst), sieht ein reiner Zustandsvergleich danach einen unveränderten aktiven Slot, auch
    nach sieben Stunden Abwesenheit. Die Slot-Zeile selbst bewahrt den Entfernungszeitpunkt nur, solange sie archiviert ist.
    **[E]** `Wishes.SlotAddedAtUtc` hält `AddedAt` des verknüpften Slots zum Zeitpunkt der letzten Übereinstimmung (die
    Generation), `Wishes.SlotConfirmedAtUtc` den letzten Abgleich, der den Slot in dieser Generation aktiv gesehen hat
    (geschrieben beim Verknüpfen, danach höchstens alle 30 Minuten, damit nicht jeder Sync jede Wunsch-Zeile schreibt).
    Weicht `AddedAt` des aktiven Slots von der Generation ab und ist es keine Datums-Bestätigung (vorläufig → maßgeblich mit
    Datum nicht nach dem vorläufigen), gilt: entfernt zum Zeitpunkt `SlotConfirmedAtUtc` (frühestmöglich, also zu Lasten der
    Übernahme), und dieselbe Fensterregel wie in Befund 6 entscheidet im selben Durchlauf über die Rückkehr. Ist der Slot noch
    archiviert, nimmt `EndedAtUtc` dessen `ArchivedAt` statt „jetzt“. Bestehende Tabellen bleiben unberührt (AK1).
20. **Aktivierungshistorie (Sol-P2-4), Abweichung von Spec 6.1: eine neue Spalte.** E14 (c) gilt für Wünsche, die „nie `active`
    waren“. `StartsAtUtc` taugt dafür nicht: `re-add` leert es (T5), ein 121 Tage alter, einmal aktiver und heute wieder
    freigegebener Wunsch verlöre seine Einlöser-Daten sofort. **[E]** `Wishes.FirstActivatedAtUtc`, gesetzt beim ersten Eintritt
    in `active` (Abgleich oder `adopt`), danach nie geändert und nie geleert; E14 (c) prüft „ist null“.
21. **Nebenläufigkeit von Abgleich und Befehlen (Sol-P2-2).** Die Datumskorrektur aus AK8 ändert weder Zustand noch Eigentum,
    erhöhte also `DecisionVersion` nicht; ein paralleles `extend` hätte mit dem alten Ablauf gerechnet und die Korrektur
    überschrieben. `DecisionVersion` dafür mitzuzählen verwürfe nach E25 jede eingereihte Bot-Entfernung und verfälschte die
    Pilot-Messung über `wish.due`. **[E]** Stattdessen Zeilensperre: Jeder Befehl und der Abgleich lesen den Wunsch unter
    `FOR UPDATE` und rechnen erst danach (2.3). Das ist dieselbe Sperre, die Spec 8 dem Bot vor `removeEmote` vorschreibt.

## 1. Umfang

### 1.1 Drin

Spec 5, 6.1 bis 6.3, 6.5 und der K1-Abschnitt: zwei Tabellen mit Migration `AddWishes`, Zustände als Konstanten, `WishTransitions`,
`WishSlotMatcher` (Treffer über die Emote-ID), Verwalter-Befehle und Lesewege, kanalübergreifende Übersicht und Zähler, Abgleich
samt Hooks, `wish.changed`, Audit, Retention, Kontolöschung, Kanal-Zusammenführung, Schalter `Wishes:Enabled`, zwölf Fehlercodes
samt Frontend-Hälfte (Befund 9), `wishesEnabled` in `/permissions`, Doku.

### 1.2 Was K1 den anderen Kindern liefert (Vertrag, in den Tasks benannt)

| Für | Vertrag aus K1 |
|---|---|
| K2 | `IWishReconcileService.ReconcileChannelAsync`; die reine Fensterfunktion E4 samt Lückenmerkmal; `WishTransitions` mit der Zeile `active` → `presented`; Spalten `RemovalQueuedAtUtc`, `RemovalQueuedVersion`, `DecisionVersion`; `WishOptions` (K2 ergänzt `ExpiryIntervalMinutes`); der Veröffentlichungsweg für `wish.changed` |
| K3 | alle Routen, `WishDto`, `WishDetailDto`, Fehlercodes, `wishesEnabled`, Event-Typ, `complete-removal` |
| K6 | alle Zustandswerte inklusive `pending`, `rejected`, `expired`, `merged`; `WishSlotMatcher` so geschnitten, dass Zustand `pending` und Namens-Treffer ergänzt werden, ohne die ID-Regel anzufassen; `adopt` mit `wishId`; `LinkOrigin`; `WishSettingsAuthorizationFilter` |
| K4 | Bot-Spalten an `Wishes` und alle Spalten aus 6.2 (angelegt, von nichts gesetzt); die Ausnahme „`BotWriteAtUtc` gesetzt“ im Abgleich; E25 in jedem Befehl; `recheck` als Route |

### 1.3 Draußen

Zustand `pending` samt Übergängen, Namens-Treffer, `WishSlotExclusions` (K6); Ablauf-Lauf, `wish.due` (K2); jede 7TV-Schreibaktion,
Pilot-Liste, Bot-Routen und ihre Codes (K4); jede Oberfläche, `audit.actions.wish.*` (K3); Datenschutztext (Betreiber, vor dem
Einschalten); `Architectur.md`, `PRODUCT.md`.

## 2. Verträge, die mehrere Tasks teilen

Namen sind Form, nicht Wortlaut; wer davon abweicht, zieht alle Tasks nach.

**2.1 Konstanten (Core, T1).** `WishStatus` (dreizehn Werte aus Spec 5, dazu die Mengen `Running` = `approved`, `unconfirmed`,
`active`, `presented` und `Ended`), `WishStatusReason` (Gründe aus Spec 5), `WishSource` (`irc`, `manual`, `adopted`),
`WishLinkOrigin` (`auto`, `adopted`, `bot`), `WishAction` (die Auslöser der Übergangstabelle). Zeichenketten, die Werte sind Vertrag.

**2.2 `WishTransitions` (Core, rein, T1).** Eine Tabelle (Zustand, Grund, Aktion) → Zielzustand oder „nicht erlaubt“. Einzige Stelle,
die Übergänge kennt; Dienste fragen sie, Handler nie. Sie enthält auch die Zeilen, die erst K2, K4 und K6 auslösen; K1 hat dafür
keinen Aufrufer. Bedingungen, die über Zustand und Grund hinausgehen (`close` aus `presented` nur ohne Slot, `Permanent` führt nach
`kept`), sind Parameter der Abfrage, keine zweite Tabelle.

**2.3 Sperre, dann bedingtes Update (T4, T5, T7; Sol-P2-2).** Jede Transaktion, die einen bestehenden Wunsch ändert, sperrt
zuerst seine Zeile (`FOR UPDATE`, neuer Helfer neben `RowLocks.LockChannelsByIdsAsync`), liest dann und rechnet erst danach.
**Sperrreihenfolge überall: Kanalzeile vor Wunsch-Zeilen, Wunsch-Zeilen nach `Id` aufsteigend.** Befehle, die keinen Slot
schreiben, nehmen nur die Wunsch-Zeile; `adopt` und der Abgleich nehmen zuerst die Kanalzeile (Befund 11). Das Update trägt
zusätzlich den erwarteten Ausgangszustand in der Bedingung; trifft es keine Zeile, ist das Ergebnis „Konflikt“ und nichts wird
geschrieben, auch kein Audit-Eintrag. Update und Audit-Eintrag teilen eine Transaktion. Jeder Befehl und jede Eigentumsänderung
im Abgleich erhöht `DecisionVersion` und leert `RemovalQueuedAtUtc` und `RemovalQueuedVersion` im selben Update (E25); eine reine
Datumskorrektur erhöht sie nicht (Befund 21), ist aber durch die Sperre gegen Befehle serialisiert. Jeder Zustandswechsel setzt
`StatusChangedAtUtc`, jeder Endzustand `EndedAtUtc`, jeder Rückweg aus einem Endzustand leert es. Jeder Eintritt in `active`
setzt `FirstActivatedAtUtc`, falls leer, und schreibt `SlotAddedAtUtc` und `SlotConfirmedAtUtc` (Befund 19, 20).

**2.4 Ergebnisform der Befehle (T4, T5, T10).** Jeder Befehl von `IWishService` liefert ein Ergebnis mit Ausgang (Erfolg oder einer
der Fehlerfälle) und optionaler Nutzlast (`slotIds`, `wishId`, `setFull`). Der Handler bildet den Ausgang auf Status und Code ab
und entscheidet nichts. Ein Test stellt sicher, dass jeder Ausgang eine Abbildung hat (Muster
`ChatLogBackfillEndpointsTests.EveryEnqueueStatus_HasAMappingCase`).

**2.5 `wish.changed` (T4).** Ein interner Helfer in Infrastructure veröffentlicht nach dem Commit genau ein Event je Kanal und Commit,
mit normalisiertem Kanalnamen, ohne Nutzlast; ein Fehler wird geloggt und geschluckt. Abgleich und Befehle nutzen denselben Helfer.

**2.6 Zeit.** Alle Dienste nehmen `TimeProvider`; Zeiten sind UTC-`DateTime` wie im Bestand.

## 3. Tasks

Je Task ein Subagent mit frischem Kontext, strikt nacheinander (Builds teilen `obj/`). Jeder Task endet mit grünem
`dotnet build EmotePurge.slnx`, seinen Tests und einem eigenen Conventional Commit. Der DECISIONS-Eintrag ist **ein** englischer
Eintrag „Wish emotes, child 1: backend core“; T1 legt ihn an, jeder Task mit Vertragsänderung ergänzt seinen Absatz und die
`**Betrifft:**`-Zeile im selben Commit (Regel 3). Kommentare, Logs, Commits englisch.

| Task | Inhalt | Modell (Vorschlag) |
|---|---|---|
| T1 | Domänenmodell, Konstanten, Übergangstabelle, Optionen | sonnet |
| T2 | Persistenz und Migration `AddWishes` | sonnet |
| T3 | Reine Regeln: Zuordnung, Probezeit, Verlängerung, Nutzungsfenster | opus |
| T4 | `WishService` Teil 1: Einstellungen, Handeintrag, Bestand übernehmen, Liste, Schalter, Live-Event | sonnet |
| T5 | `WishService` Teil 2: Übergangsbefehle, Nebenläufigkeit, E25 | sonnet |
| T6 | Detail mit Nutzung und Rang; kanalübergreifende Übersicht und Zähler | sonnet |
| T7 | `WishReconcileService` | opus |
| T8 | Hooks in Sync und Bericht | opus |
| T9 | Fristen und Löschpfade | sonnet |
| T10 | Api: Routen, Filter, Fehlercodes, `/permissions`, Frontend-Hälfte der Codes | sonnet |
| T11 | Doku | sonnet |
| T12 | Live-Verifikation (Abschnitt 5) | sonnet mit Orchestrator |
| T13 | Gates und PR (Abschnitt 6) | Orchestrator |

### T1: Domänenmodell, Konstanten, Übergangstabelle, Optionen

- **Ziel:** Alles Reine, gegen das die übrigen Tasks bauen. Noch keine Datenbank.
- **Dateien (neu, Core):** `Entities/Wish.cs`, `Entities/ChannelWishSettings.cs`, `Entities/WishStatus.cs` (mit den Konstanten aus
  2.1), `Services/WishTransitions.cs`, `Services/WishOptions.cs`. **Geändert:** `Entities/AuditLogEntry.cs` (neun Konstanten
  `wish.create`, `wish.adopt`, `wish.extend`, `wish.keep`, `wish.remove`, `wish.close`, `wish.retry`, `wish.readd`, `wish.settings`),
  `Services/RetentionPolicy.cs` (`WishRequester` 30 Tage, `EndedWish` 365 Tage, dazu die Kappe aus E14 (c) mit 120 Tagen als
  benannte Konstante), `Messaging/LiveEvents.cs` (`WishChanged = "wish.changed"`, nur in `ChannelTypes`), `docs/DECISIONS.md`.
- **Verträge:** Felder von `Wish` nach Spec 6.1 plus `LinkOrigin` (Befund 1), `SlotAddedAtUtc`, `SlotConfirmedAtUtc` (Befund 19)
  und `FirstActivatedAtUtc` (Befund 20), alle drei timestamptz, null; `ChannelWishSettings` nach 6.2 vollständig, mit den
  Defaults 30 und 7 als Konstanten, die auch gelten, wenn die Zeile fehlt. `WishOptions`: `SectionName = "Wishes"`, `Enabled`
  (Default `false`), `Validate()` wirft bei ungültigen Werten beim Start. Grenzen als Konstanten an einer Stelle: Laufzeit 1 bis
  365, Vorlauf 1 bis 30, Schwelle größer 0, Alias und Emote-Name höchstens 100, Login höchstens 25, Emote-ID höchstens 32;
  dazu Übernahmefenster 6 Stunden (E10) und Rückgriff 30 Minuten (Befund 6) als benannte Konstanten.
- **Grenzfälle:** Die Tabelle unterscheidet `presented` nach Grund (`recheck` und `close` hängen daran). `extend` nur aus `active`
  und `presented`. `re-add` nur aus `removed` und `removed_externally`. `adopt` mit `wishId` aus `approved`, `failed`,
  `presented` mit Grund `unconfirmed` oder `already_in_set` (und `pending`, Zeile vorhanden, in K1 ohne Erzeuger).
- **AKs:** AK2 (reiner Teil), AK20 (Core bleibt paketfrei).
- **Tests:** `Infrastructure.Tests/Unit`: vollständige Matrix über alle Zustände mal alle Aktionen (jede nicht gelistete Paarung
  ist „nicht erlaubt“, die Erwartung steht als Tabelle im Test, nicht aus der Implementierung abgeleitet); Validierung von
  `WishOptions`; `LiveEvents.AdminTypes` enthält `wish.changed` nicht. `CoreAssemblyReferenceTests` bleibt grün.
- **Fertig:** Matrix grün; DECISIONS-Eintrag angelegt (Modell, Zustände als Zeichenketten, eine Übergangstabelle, Schalter, die
  vier Spalten über Spec 6.1 hinaus mit Begründung).
- **Commit:** `feat: add wish domain model and transition table`

### T2: Persistenz und Migration `AddWishes`

- **Ziel:** Beide Tabellen mit Indizes und Constraints, additiv, ohne Änderung an Bestandstabellen.
- **Dateien:** `Persistence/AppDbContext.cs` (zwei `DbSet`, Konfiguration inline wie der Bestand, Filter und CHECKs nach dem Muster
  `:357-378`), `Migrations/*_AddWishes.cs` samt Designer und Snapshot (über `dotnet ef migrations add AddWishes`, nicht von Hand).
- **Verträge:** `Wishes.ChannelId` Kaskade; `Wishes.SlotId` SET NULL; **[E]** `MergedIntoWishId` als Selbstverweis ohne Kaskade (Verweis
  überlebt nur, solange das Ziel lebt: SET NULL). Indizes nach 6.1: `(ChannelId, Status)`, `(Status, ExpiresAtUtc)`, eindeutig
  partiell `(ChannelId, SevenTvEmoteId)` für die vier laufenden Zustände, eindeutig partiell `(SlotId)` für `active` und
  `presented` mit gesetztem Slot, partiell auf `EndedAtUtc` für Zeilen mit Einlöser-Daten. CHECKs: Laufzeit, Vorlauf,
  Standardlaufzeit, Schwelle. `ChannelWishSettings`: PK = `ChannelId`, Kaskade.
- **Grenzfälle:** Die Migration darf keine bestehende Tabelle ändern (im generierten Code gegenlesen: nur `CreateTable`,
  `CreateIndex`). `Down` entfernt beide Tabellen. Kein Seed.
- **AKs:** AK1 (Teil „läuft durch, nichts ausstehend“; die Kopie der Dev-Datenbank prüft T12), AK18 (Kaskade).
- **Tests:** `Infrastructure.Tests/Integration` (Postgres-Fixture): zweiter laufender Wunsch derselben Emote-ID scheitert am Index,
  ein beendeter daneben nicht; zwei `active` am selben Slot scheitern, `removed` plus `active` am selben Slot nicht; Laufzeit 0
  und 366 scheitern am CHECK; Löschen des Kanals nimmt Wünsche und Einstellungen mit; Löschen des Slots setzt `SlotId` null und
  lässt den Wunsch stehen; die drei neuen Spalten sind nullbar und ohne Default.
- **Fertig:** Tests grün; `dotnet ef migrations list` gegen die Testdatenbank zeigt nichts Ausstehendes.
- **Commit:** `feat: add wishes and channel wish settings tables`

### T3: Reine Regeln

- **Ziel:** Jede Rechenregel der Spec als reine Funktion in Core, damit K2 und K6 sie wiederverwenden.
- **Dateien (neu, Core/Services):** `WishSlotMatcher.cs`, dazu eine Datei für die Zeitregeln (Form: `WishSchedule`) und eine für
  das Nutzungsfenster (Form: `WishUsageWindow`).
- **Verträge:**
  - *Matcher:* Eingabe sind ein Wunsch-Ausschnitt (Zustand, Emote-ID, Alias, `ApprovedAtUtc` bzw. `EndedAtUtc`) und die
    Kandidaten-Slots des Kanals als einfache Werte (Slot-ID, Emote-ID, Alias, `AddedAt`). Ausgabe: genau ein Slot oder „kein
    Treffer“ mit Grund (keiner, mehrdeutig). Der Aufrufer liefert nur zulässige Slots (Befund 7) und nimmt die mit **anderen**
    laufenden Wünschen verknüpften heraus; der zuvor eigene Slot bleibt drin (Befund 5). Zwei Regeln, je Zustand eine:
    - `approved` (Spec 6.3 unverändert): `AddedAt` nicht vor `ApprovedAtUtc`, ohne Toleranz. Genau ein Slot der Emote-ID → der;
      mehrere → der mit ordinal gleichem Alias; sonst nichts.
    - `removed_externally` **[B]**: Slot der Emote-ID **mit dem Alias des Wunsches**, `AddedAt` nicht vor `EndedAtUtc` minus
      30 Minuten und nicht nach `EndedAtUtc` plus 6 Stunden. Höchstens einer kann zutreffen; trifft keiner zu, nichts. Die
      6 Stunden hängen am `AddedAt` des Ersatzes, nicht am Zeitpunkt des Laufs **[E]**: Ein verspäteter Lauf entscheidet gleich.
    Fensterbeginn und Fensterende je Zustand sind benannte Funktionen, damit K6 `pending` ergänzt, ohne diese zwei anzufassen.
  - *Zeitregeln:* Probezeitbeginn (E2), neuer Ablauf beim Verlängern (E12: ab bisherigem Ablauf, oder ab jetzt, wenn vorbei),
    Ablauf aus Start und Laufzeit, Verschiebung bei maßgeblichem Datum (AK8: Start und Ablauf um dieselbe Differenz).
  - *Nutzungsfenster:* Fensterbeginn (E4), Fensterlänge in Tagen mit Bruchteilen, Mittel je Tag aus einer gelieferten Tagessumme,
    Lückenmerkmal (E5: Zählbeginn nach Fensterbeginn, Kanal deaktiviert, Bot nicht aktiv, Fenster unter 24 Stunden), Rang aus
    einer gelieferten Liste von Summen (E16; Gleichstand teilt den besseren Rang **[E]**).
- **Grenzfälle:** `approved`: Slot eine Sekunde vor `ApprovedAtUtc` ist kein Kandidat (AK7); Wunsch ohne Alias bei mehreren
  Kandidaten: nichts. `removed_externally`: 29 Minuten vor `EndedAtUtc` ist Kandidat, 31 Minuten davor nicht; jenseits von
  6 Stunden nichts; ein einzelner Ersatz unter anderem Alias: nichts; Alias-Vergleich ordinal, andere Großschreibung: nichts. Laufzeit kürzer als Vorlauf: Probezeit ab
  Start. Fenster exakt 24 Stunden ist gültig, 23 Stunden ist Lücke.
- **AKs:** AK7 und AK10 (reiner Teil), AK8 (Rechnung), Zulieferung für AK22 bis AK25 in K2.
- **Tests:** `Infrastructure.Tests/Unit`, tabellengetrieben: Matcher rund sechzehn Fälle. `approved`: einer, zwei mit
  Alias-Treffer, zwei ohne, vor dem Fenster, Fenstergrenze exakt. `removed_externally`: 5 h und 7 h; Rückgriff 29 und 31
  Minuten; **zwei Ersatz-Slots, von denen genau einer den Alias trägt → dieser** (Sol-P2-5); zwei Ersatz-Slots ohne den Alias →
  nichts; ein Ersatz unter anderem Alias → nichts; wiederaufgenommener eigener Slot → dieser; Zeitregeln
  rund acht; Fenster und Rang rund zehn mit `FakeTimeProvider`-Zeitpunkten.
- **Fertig:** Tests grün.
- **Commit:** `feat: add wish slot matcher and schedule rules`

### T4: `WishService` Teil 1

- **Ziel:** Die Wege, mit denen ein Wunsch entsteht und gelesen wird, plus Einstellungen, Schalter und Live-Event.
- **Dateien:** neu `Core/Services/IWishService.cs` (mit den DTO-Records `WishDto`, Einstellungen, Ergebnisformen),
  `Infrastructure/Services/WishService.cs`, der Helfer aus 2.5; geändert `ServiceCollectionExtensions.cs` (Optionen nach dem
  Muster `:338-341`, Dienste), `docker-compose.yml`, `docker-compose.prod.yml`, `.env.example` (`WISHES_ENABLED`, Default `false`,
  an `api` **und** `worker`).
- **Verträge (Befehle und Lesewege, alle mit Kanalname, normalisiert über `ChannelName.Normalize`):**
  - *Einstellungen lesen:* Zeile oder Defaults. *Schreiben:* Upsert, Grenzen geprüft (`wish_settings_out_of_range`), Audit
    `wish.settings`, Event. Dazu eine Entscheidungsmethode „darf dieser Aufrufer die Einstellungen ändern“ für den Filter aus T10
    (manueller Modus: jeder Verwalter; Bot-Modus: Broadcaster, Aktivierender, globaler Admin).
  - *Handeintrag:* validiert Laufzeit (fehlt sie: Kanal-Default), Alias, Emote-ID, Login (getrimmt, kleingeschrieben, dann
    Twitch-Login-Form); prüft „Emote-ID aktiv im Set“ (→ Konflikt mit Slot-IDs) vor „läuft schon“ (→ Konflikt mit Wunsch-ID);
    legt `approved` mit `ApprovedAtUtc` an, Quelle `manual`; `setFull` als Hinweis aus `ActiveEmoteSetCapacity`.
  - *Bestand übernehmen:* ohne `wishId` neuer Wunsch `active`, Quelle `adopted`; mit `wishId` verknüpft er den vorhandenen
    (Übergang aus T1). `LinkOrigin = adopted`, `BotOwned` falsch, bei `Permanent` Ziel `kept`. Start = `startsAtUtc` oder
    `AddedAt` des Slots (Befund 7); ein gesetzter Wert nicht in der Zukunft und nicht vor `AddedAt`. Kanalsperre zuerst (Befund 11),
    mit `wishId` danach die Wunsch-Zeile (2.3). Setzt `SlotAddedAtUtc`, `SlotConfirmedAtUtc`, `FirstActivatedAtUtc`.
  - *Liste:* Sichten nach Befund 17, paginiert mit Seite und Seitengröße wie der Bestand (`PagingQuery` bindet in der Api). *Einzeln:* `WishDto`.
  - *DTO:* Felder nach Spec; `probationStartsAtUtc` abgeleitet (T3), `mode` aus den Einstellungen (in K1 immer `manual`),
    `botWritePending`, `botNextAttemptAtUtc`, `removalQueuedAtUtc` aus den Spalten (in K1 leer). `requesterTwitchUserId` kommt
    in keinem DTO vor.
- **Grenzfälle:** Wettlauf zweier Handeinträge derselben Emote-ID: der Verlierer am Index wird zu `wish_already_running`, nicht zu
  500. `adopt` auf einen Slot mit laufendem Wunsch: `wish_slot_already_linked` (auch über den Index). Slot eines anderen Kanals:
  `wish_slot_not_found`. Unbekannter oder nicht getrackter Kanal: der bestehende Code `channel_not_found` **[E]**. Audit-Details
  nur `wishId`, `sevenTvEmoteId`, `durationDays`.
- **AKs:** AK4, AK5, AK6 (409-Teil), AK16 (Veröffentlichung), AK19.
- **Tests:** `Infrastructure.Tests/Integration` (Postgres, Redis-Publisher als Double): Grenzen 0, 1, 365, 366 und Default; AK5 als
  Folge Handeintrag → Konflikt → `adopt`; AK6 Konflikt mit ID; Wettlauf am Index; `adopt` mit `startsAtUtc` in der Zukunft und
  vor `AddedAt`; genau ein Event je Commit, keins bei Konflikt; Audit-Details enthalten weder Alias noch Namen noch Login (AK19,
  geprüft am rohen `DetailsJson`); Einstellungen Default, Upsert, Grenzen.
- **Fertig:** Tests grün; Api und Worker starten mit und ohne `Wishes__Enabled`.
- **Commit:** `feat: add wish service with manual entry, adoption and settings`

### T5: `WishService` Teil 2, Übergangsbefehle

- **Ziel:** `PATCH`, `extend`, `keep`, `close`, `retry`, `re-add`, `complete-removal`, `recheck`.
- **Dateien:** `IWishService.cs`, `WishService.cs`.
- **Verträge:** Jeder Befehl folgt 2.3 (Zeilensperre vor dem Lesen) und 2.4 und fragt `WishTransitions`. `PATCH` (Alias, Laufzeit) nur in `approved` und
  `failed`. `extend`: neuer Ablauf nach T3 um den Kanal-Default, `ExtensionCount` plus 1, aus `presented` zurück nach `active`,
  Audit `wish.extend`. `keep`: `kept`, `BotOwned` endet, Audit `wish.keep`. `close`: aus `approved`, `failed` und `presented`
  ohne Slot, Audit `wish.close`. `retry`: `failed` → `approved`, `ApprovedAtUtc` neu, Audit `wish.retry`. `re-add`: aus `removed`
  und `removed_externally` nach `approved` mit neuer Laufzeit oder `Permanent`, `ApprovedAtUtc` neu, Start, Ablauf und Slot-Generation geleert,
  `FirstActivatedAtUtc` und `SlotId` bleiben, Audit `wish.readd`. `complete-removal`: `active` oder `presented` → `removed`; `removed_externally` binnen 6 Stunden nach
  `EndedAtUtc` → `removed` (AK11); Audit `wish.remove`. `recheck` nach Befund 8.
- **Grenzfälle:** `re-add`, während für dieselbe Emote-ID schon wieder ein Wunsch läuft: `wish_already_running`.
  `complete-removal` auf `removed`: Konflikt, kein zweiter Audit-Eintrag. `extend` auf `approved`: Konflikt (AK6). Wunsch-ID eines
  anderen Kanals: `wish_not_found`. `re-add` mit `mode: "timed"` ohne Laufzeit: Kanal-Default.
- **AKs:** AK2 (vollständige Matrix über die Dienstschicht), AK3, AK6, AK11 (Befehlsteil), AK12 (Befehlsteil), AK19.
- **Tests:** `Infrastructure.Tests/Integration`: Matrix (Zustand, Aktion) gegen die Datenbank, jede nicht erlaubte Paarung ergibt
  Konflikt und lässt Zeile und Audit-Log unverändert; AK3 mit zwei parallelen `keep` auf getrennten Kontexten (genau ein Erfolg,
  ein Audit-Eintrag); **Sperrtest (Sol-P2-2):** `extend` gegen eine parallele Datumskorrektur des Abgleichs in beiden
  Commit-Reihenfolgen, gesteuert über `PostgresLockProbe` bzw. `CommandHook` als Schranke: In beiden Fällen ist der Ablauf am
  Ende „korrigierter Ablauf plus Verlängerung“, nie der unkorrigierte Wert (der Testfall liegt bei T7, weil er den Abgleich
  braucht; T5 liefert die Sperre und den Fall „zweiter Befehl wartet und sieht den neuen Stand“); E25: ein von Hand gesetztes `RemovalQueuedAtUtc` ist nach jedem erfolgreichen Befehl leer und
  `DecisionVersion` höher; AK6 beide Äste; AK11 bei 5 h und 7 h.
- **Fertig:** Tests grün.
- **Commit:** `feat: add wish transition commands`

### T6: Detail mit Nutzung, Übersicht und Zähler

- **Ziel:** `GET /{id}` mit `usage`, `GET /api/wishes/mine`, `GET /api/wishes/summary` als Dienste.
- **Dateien:** `IWishService.cs`, `WishService.cs` (Detail); neu `Core/Services/IWishOverviewService.cs`,
  `Infrastructure/Services/WishOverviewService.cs`; Registrierung.
- **Verträge:** *Detail:* `WishDetailDto` = `WishDto` plus `usage` (Fenster, Summe, Mittel, Rang, `rankOf`, `coverageGap`). Summe
  über alle Slots desselben Emotes im Kanal an den UTC-Tagen des Fensters (Regel 10: erst auf Slot-IDs reduzieren, dann
  aggregieren); Rang unter allen aktiven Slots im selben Fenster. Ohne Start: `usage` leer. Bewertungszeitpunkt ist in K1 „jetzt“.
  *Übersicht:* beginnt bei den Kanälen mit Handlungsbedarf oder Wünschen in Probezeit (eine Indexabfrage), prüft nur für diese
  `IChannelAccessService.CanManageChannelAsync`, sortiert nach `ExpiresAtUtc`, dann `CreatedAtUtc`, nennt je Zeile den Kanal.
  *Zähler:* `actionCount` über dieselben Kanäle; ohne Kandidaten 0 ohne jede Rollenprüfung. Keiner der beiden ruft
  `IMyChannelsService`.
- **Grenzfälle:** Deaktivierter Kanal: Wünsche bleiben sichtbar, `coverageGap` wahr. Emote unter zwei Aliasen: Summe über beide.
  Konto, das nur 7TV-Editor ist: sieht nichts.
- **AKs:** AK15; Zulieferung für AK24 und AK30.
- **Tests:** `Infrastructure.Tests/Integration`: Fenster und Rang gegen gesäte `UsageStats`; Übersicht mit zwei Kanälen (einer
  verwaltbar, einer nicht); Zähler ohne Kandidaten ruft den Zugriffsdienst nie; `IMyChannelsService` wird nie aufgerufen (Double,
  das bei Aufruf scheitert); zweiter Aufruf binnen 5 Minuten ohne Helix-Aufruf mit echtem `ChannelAccessService` und zählendem
  Helix-Double (Redis-Fixture, falls der Rollen-Cache sie braucht).
- **Fertig:** Tests grün; die Kosten einer Rollenprüfung bei kaltem Cache sind aus `ChannelAccessService` und `ModRoleCache`
  abgelesen und für den PR-Text notiert.
- **Commit:** `feat: add wish usage detail and cross-channel overview`

### T7: `WishReconcileService`

- **Ziel:** Der zustandsbasierte Abgleich (E9), idempotent, für sich aufrufbar. Noch kein Aufrufer.
- **Dateien:** neu `Core/Services/IWishReconcileService.cs` (Form: `ReconcileChannelAsync(channelId, ct)`),
  `Infrastructure/Services/WishReconcileService.cs`; Registrierung.
- **Verträge:** Eigener Scope und Kontext (Befund 4). Ablauf:
  1. **Vorprüfung, eine Indexabfrage:** Hat der Kanal Wünsche in `approved`, `active`, `presented` oder in
     `removed_externally` mit `EndedAtUtc` in den letzten 24 Stunden **[E]** (großzügig, damit ein verspäteter Lauf heilt;
     ob übernommen wird, entscheidet allein das Fenster am `AddedAt`)? Nein → Rückkehr ohne Transaktion.
  2. **Transaktion:** Kanalzeile sperren (Befund 11), dann die betroffenen Wunsch-Zeilen nach `Id` (2.3), dann Slots lesen.
  3. **Verknüpfte Wünsche in `active` und `presented`**, je nach Lage des Slots (Befund 19):
     - *archiviert* → `removed_externally`; `EndedAtUtc` = `ArchivedAt` des Slots, falls gesetzt und nicht vor
       `SlotConfirmedAtUtc`, sonst jetzt; `BotOwned` endet; `SlotId` und `StatusReason` bleiben stehen **[E]**. So trägt ein verpasster Hook den echten
       Zeitpunkt nach, solange der Slot archiviert ist.
     - *aktiv, `AddedAt` gleich `SlotAddedAtUtc`* → Umbenennung prüfen (`Alias` nachziehen, `BotOwned` endet, Frist bleibt);
       `SlotConfirmedAtUtc` auffrischen, wenn älter als 30 Minuten.
     - *aktiv, `AddedAt` abweichend, Datums-Bestätigung* (Wunsch `StartsAtAuthoritative = false`, Slot jetzt maßgeblich, neues
       Datum nicht nach dem alten) → Start und Ablauf um dieselbe Differenz verschieben, `StartsAtAuthoritative = true`,
       Generation nachziehen (AK8). Hat `adopt` einen eigenen Start gesetzt, bleibt der Start, nur die Generation zieht nach.
     - *aktiv, `AddedAt` abweichend, sonst* → **Generationswechsel:** Der Slot war weg und ist wieder da, ohne dass ein
       Abgleich es gesehen hat. Behandelt als entfernt zum Zeitpunkt `SlotConfirmedAtUtc`: `removed_externally` mit diesem
       `EndedAtUtc`, `BotOwned` endet. Schritt 4 desselben Durchlaufs entscheidet über die Rückkehr.
  4. **Wünsche ohne gültige Verknüpfung** in `approved` und `removed_externally`: Vorfilter (Emote-IDs, für die im Kanal ein
     anderer Wunsch läuft, scheiden für die Rückkehr aus), Kandidaten laden (Befund 7), Matcher fragen (T3). Treffer:
     `approved` → `active`, bei `Permanent` `kept`, Start = `AddedAt`, Ablauf aus Start und Laufzeit, Alias folgt dem Slot,
     `LinkOrigin = auto`. `removed_externally` → zurück in den Zustand davor mit **alter** Frist, ohne Eigentum, `EndedAtUtc`
     geleert: `presented` mit dem stehen gebliebenen Grund, wenn einer gesetzt ist, sonst `active`. Einen abgelaufenen `active`
     legt nicht der Abgleich vor, sondern der Ablauf-Lauf (K2, `wish.due`). Beide setzen Generation und `FirstActivatedAtUtc` (2.3).
  5. **Schreiben je Wunsch einzeln, hinter einem Savepoint (Sol-P2-3):** Jede Änderung ist ein eigenes bedingtes Update, nie
     ein gesammeltes `SaveChangesAsync`. Verletzt eines einen Eindeutigkeitsindex (ein Handeintrag kann ohne Kanalsperre
     dazwischenkommen), wird auf den Savepoint zurückgerollt, der Wunsch bleibt, wie er war, eine Informationszeile ohne
     Alias, weiter mit dem nächsten. Andere Fehler brechen den Durchlauf ab und rollen alles zurück.
  6. **Commit**, danach höchstens ein `wish.changed`, und nur wenn sich fachlich etwas geändert hat (ein bloßes Auffrischen
     von `SlotConfirmedAtUtc` zählt nicht). Kein Audit-Eintrag in K1 (`wish.autoLink` gehört K6).

  Ausnahmen für K4, in K1 nur per Test erreichbar: Wunsch mit gesetztem `BotWriteAtUtc` in `approved` oder `unconfirmed` wird
  nicht zugeordnet; ein `active` ohne `SlotId` bekommt seinen Slot ohne Änderung an Zustand, Start und Eigentum.
- **Grenzfälle:** Zwei Wünsche konkurrieren im selben Durchlauf um einen Slot: feste Reihenfolge nach `CreatedAtUtc`, der zweite
  bleibt. Rückkehr aus `removed_externally`, während ein neuer Wunsch derselben Emote-ID läuft: der alte bleibt (Vorfilter,
  sonst Savepoint), alle anderen Übergänge des Durchlaufs committen. Datums-Bestätigung mit Datum **nach** dem vorläufigen
  (Uhrenabweichung): läuft als Generationswechsel, kehrt im selben Durchlauf zurück, Frist bleibt unverschoben; in K1 folgenlos,
  für K4 als Eigentumsverlust vermerkt (R10). Slot ohne Datum nach Reaktivierung: kein Kandidat, der nächste datierte Sync
  entscheidet. Zweiter Lauf über denselben Stand: keine fachliche Änderung, kein Event. Kanal während des Laufs gelöscht: ohne
  Wirkung, kein Fehler.
- **AKs:** AK7, AK8, AK9, AK10, AK11 (Ausgangslage), AK12.
- **Tests:** `Infrastructure.Tests/Integration`. *Mit direkt gesäten Slots:* die vier Fälle aus AK7; AK8 mit vorläufigem und dann
  maßgeblichem Datum (Laufzeit unverändert, auch nach einer Verlängerung); AK9; AK10 mit neuem Slot gleichen Alias bei 5 h und
  7 h, zwei Ersatz-Slots mit genau einem Alias-Träger, zwei ohne; AK12; Idempotenz; Kanal ohne laufende Wünsche kostet genau
  eine Abfrage (über `CommandHook` gezählt); `BotWriteAtUtc`-Ausnahme. *Mit den echten Schreibern (Sol-P2-1):* Archivieren und
  Wiederherstellen über `EmoteService` (Bericht Löschen, Bericht Restore) und über den Vollsync mit `ResumeArchivedSlots`
  (gleiche `SlotId`), jeweils (i) Abgleich nach beiden Schritten, Rückkehr binnen Fenster; (ii) **Abgleich nach dem Archivieren
  scheitert oder bleibt aus**, Wiederherstellung nach 7 Stunden, dann Abgleich → `removed_externally`, nicht `active`; (iii) wie
  (ii) mit Wiederherstellung nach 1 Stunde → zurück mit alter Frist; (iv) Hook verpasst, Slot noch archiviert → `EndedAtUtc`
  gleich `ArchivedAt`. *Sperre (Sol-P2-2):* `extend` gegen Datumskorrektur in beiden Commit-Reihenfolgen (siehe T5).
  *Savepoint (Sol-P2-3):* eine Rückkehr, die am Index `(ChannelId, SevenTvEmoteId)` scheitert (Vorfilter im Test über eine
  Schranke umgangen), zusammen mit einem unabhängigen gültigen Übergang im selben Kanal → Letzterer ist committet, der
  kollidierende Wunsch unverändert, kein Fehler nach außen.
- **Fertig:** Tests grün; DECISIONS-Absatz „state-based reconcile, own context, channel lock before wish rows, slot generation,
  savepoint per wish“.
- **Commit:** `feat: add state-based wish reconcile`

### T8: Hooks in Sync und Bericht

- **Ziel:** Der Abgleich läuft nach jedem erfolgreichen Slot-Schreiben, ohne es je zu gefährden.
- **Dateien:** `Infrastructure/Services/SevenTvSyncService.cs`, `Infrastructure/Services/EmoteService.cs`.
- **Verträge:** Aufrufstellen nach Befund 2 und 3: Vollsync nach `SaveSyncAsync` mit Ergebnis; Delta bei Ausgang `Applied`; Bericht
  nach `MarkInSetAsync` je geändertem Trefferkanal. Immer **nach** dem Commit, außerhalb der Wiederholungsschleifen, über einen
  gemeinsamen kleinen Wrapper: Schalter aus → kein Aufruf; jede Ausnahme außer Abbruch wird als Warnung ohne Alias und ohne
  Einlöser geloggt und geschluckt. Der Rückgabewert von Sync, Delta und Bericht ändert sich nie durch den Abgleich.
- **Grenzfälle:** Sync, dessen Zeile nach dem Speichern verschwunden oder deaktiviert ist (`current is null`): Abgleich läuft
  trotzdem oder gar nicht, beides ist unschädlich; **[E]** er läuft direkt nach dem Speichern, vor dem Nachlesen. Bericht ohne
  Änderung in einem Kanal: kein Aufruf für diesen Kanal. Wiederholter Bericht nach Deadlock: genau ein Abgleich.
- **AKs:** Voraussetzung für AK21; Testplan-Zeile „Fehler im Abgleich bricht den Sync nicht ab“.
- **Tests:** `Infrastructure.Tests/Integration`: Vollsync mit einem Abgleich-Double, das wirft → Sync-Ergebnis und Slots
  unverändert korrekt, eine Warnung; Vollsync mit echtem Abgleich macht einen `approved`-Wunsch `active`; Delta `Applied` ruft
  auf, `NoChange` und `AmbiguousDelta` nicht; Bericht (Löschen) führt den verknüpften Wunsch nach `removed_externally`; Schalter
  aus → kein Aufruf; ein geschluckter Fehler nach dem Archivieren wird vom nächsten Abgleich geheilt (Verweis auf T7, Fall iv).
  Alle bestehenden Sync- und Berichts-Tests bleiben unverändert grün (Konstruktoren der Testaufbauten
  nachziehen, sonst nichts).
- **Fertig:** `dotnet test` für `Infrastructure.Tests` vollständig grün.
- **Commit:** `feat: reconcile wishes after slot writes`

### T9: Fristen und Löschpfade

- **Ziel:** Retention, Kontolöschung, Kanal-Zusammenführung, Purge.
- **Dateien:** `Infrastructure/Services/DataRetentionService.cs`, `Core/Services/IDataRetentionService.cs`
  (`RetentionRunSummary`), `Worker/RetentionRunSummaryFormatter.cs`, `Infrastructure/Services/AccountDeletionService.cs`,
  `Infrastructure/Services/ChannelIdentityService.cs`; Tests in `Infrastructure.Tests` und `Worker.Tests`.
- **Verträge:** *Einlöser leeren* (`RequesterTwitchUserId`, `RequesterLogin`): 30 Tage nach `EndedAtUtc`; E14 (a) 30 Tage nach
  `ExpiresAtUtc`, wenn nicht `active`; (b) 30 Tage nach `StatusChangedAtUtc` für `approved`, `unconfirmed`, `failed`,
  `presented` ohne `ExpiresAtUtc`; (c) 120 Tage nach `CreatedAtUtc` für jeden Wunsch, der nie aktiv war: `FirstActivatedAtUtc` ist
  null (Befund 20, Sol-P2-4), ausdrücklich **nicht** `StartsAtUtc`. *Beendete Wünsche löschen:* 365 Tage nach `EndedAtUtc`. Beides als eigene Kategorie im Tageslauf, im
  Trockenlauf nur gezählt; die Summenzeile nennt beide Zählwerte. *Kontolöschung:* Einlöser-Felder aller Wünsche mit der
  Twitch-ID leeren; `BotModeActivatedBy*` und `BotActivationRequestedBy*` auf die Markierung `AuditActor.DeletedUser`, in
  derselben Transaktion wie der Bestand. *Zusammenführung:* vor dem Löschen des Verlierers, in derselben Transaktion: Wünsche
  umhängen; kollidiert der Index, endet der jüngere als `merged` mit `MergedIntoWishId` und `EndedAtUtc` (Befund 15);
  Einstellungen nach Befund 15. *Purge:* keine Codeänderung erwartet, nur Beweis.
- **Grenzfälle:** `active` mit 365 Tagen Laufzeit behält die Einlöser-Daten. `failed` mit wiederholtem `retry` fällt unter (c).
  Ein einmal aktiver, entfernter und heute per `re-add` wieder freigegebener Wunsch mit `CreatedAtUtc` vor 121 Tagen fällt
  **nicht** unter (c); für ihn läuft (b) ab dem neuen `StatusChangedAtUtc`. Beim Zusammenführen von Kanälen wandern die drei
  neuen Spalten unverändert mit.
  `removed_externally` im 6-Stunden-Fenster ist weit unter jeder Frist. Verlierer mit Wünschen, aber ohne Emotes und Tags:
  Zusammenführung läuft (heute würde die Kaskade die Wünsche still löschen).
- **AKs:** AK17, AK18.
- **Tests:** `Infrastructure.Tests/Integration`: jede Zeile aus AK17 als Fall mit `FakeTimeProvider` (31 und 29 Tage, 121 Tage,
  366 Tage), `enforce = false` ändert nichts und zählt richtig; **aktiv → entfernt → `re-add` mit `CreatedAtUtc` vor 121 Tagen
  behält die Einlöser-Daten, derselbe Wunsch 31 Tage nach dem `re-add` ohne Slot verliert sie über (b)** (Sol-P2-4); Kontolöschung mit zwei Wünschen; Zusammenführung mit und ohne
  Kollision, mit und ohne Einstellungen des Überlebenden; Purge über alle drei Purge-Wege des Bestands hinterlässt keine Zeile.
  `Worker.Tests`: Formatter nennt die neuen Zählwerte.
- **Fertig:** Tests grün; DECISIONS-Absatz zu den Fristen.
- **Commit:** `feat: add wish retention, account deletion and channel merge handling`

### T10: Api

- **Ziel:** Routen, Filter, Codes; dünne Handler.
- **Dateien:** neu `Api/Endpoints/WishEndpoints.cs` (`MapWishEndpoints`), `Api/Auth/WishSettingsAuthorizationFilter.cs`; geändert
  `Program.cs` (nur der Map-Aufruf), `Api/Validation/ApiErrorCodes.cs` (zwölf Codes mit Kommentar zu Status und Anlass),
  `Api/Endpoints/ChannelEndpoints.cs` (`wishesEnabled` in `/permissions`), `tests/EmotePurge.Api.Tests/ApiFactory.cs`
  (`IWishService`, `IWishOverviewService` als Doubles), neue Testdatei; `web/src/app/core/i18n/api-error.ts`,
  `web/public/i18n/de.json`, `en.json` (Befund 9).
- **Verträge:** Kanal-Gruppe `/api/channels/{channelName}/wishes`: `RequireAuthorization` → `ChannelNameValidationFilter` →
  `ChannelManagementAuthorizationFilter` → im Handler zuerst der Schalter (404 `wishes_disabled`), dann der Körper. Routen und
  Rate-Limits nach der Tabelle der Spec. `PUT /settings` zusätzlich `WishSettingsAuthorizationFilter` (fragt die
  Entscheidungsmethode aus T4, 403 `wish_settings_restricted`; **[E]** bei ausgeschaltetem Schalter lässt der Filter durch, damit
  der Handler 404 antwortet). Gruppe `/api/wishes` (`RequireAuthorization`, `InteractiveRead`): `mine`, `summary`; Schalter aus →
  404 `wishes_disabled`. Abbildung der Ausgänge nach 2.4; 201 für Handeintrag und `adopt`. Routenreihenfolge: `settings` und
  `adopt` sind Literale neben `{id}`; `{id}` als Zahl einschränken.
- **Grenzfälle:** Unbindbarer Körper: der bestehende Code für ungültige Körper. Schalterprüfung vor der Körperprüfung (Muster
  `FlagOff_ChecksTheFlag_BeforeTheBody`). `wish.changed` erscheint nicht im Prädikat von `/api/channels/live-events`
  (`LiveEndpoints.cs:91` bleibt unberührt).
- **AKs:** AK13, AK14, AK16 (Routenteil), AK20.
- **Tests:** `Api.Tests`: Matrix AK13 über jede Route (400, 401, 403, 403 für den 7TV-Editor ohne Mod-Status, 404 für den
  Verwalter bei Schalter aus, 403 für den Nicht-Verwalter bei Schalter aus, Dienst nie aufgerufen); AK14 mit den vier Rollen;
  Abbildung jedes Ausgangs auf Status und Code samt Nutzlast (`slotIds`, `wishId`); Wire-Form von `WishDto` (camelCase,
  Zeiten mit `Z`, kein `requesterTwitchUserId`); Kanal-Stream leitet `wish.changed` für den eigenen Kanal weiter, für einen
  fremden nicht, der kanalübergreifende Stream nie; Strukturtest: kein Handler in `WishEndpoints.cs` nimmt `AppDbContext` oder
  `IConnectionMultiplexer`. Frontend: `npm --prefix web test -- --watch=false` grün (`api-error-locales.spec.ts`).
- **Fertig:** `dotnet test EmotePurge.slnx` und Vitest grün; die zwölf Codes stehen von Hand gegengeprüft in `ApiErrorCodes.cs`
  und `api-error.ts`.
- **Commit:** `feat: add wish endpoints behind the Wishes:Enabled switch`

### T11: Doku

- **Dateien:** `docs/DECISIONS.md` (Eintrag vervollständigen, `**Betrifft:**` mit allen neuen Dateien, die Befunde 4, 5, 9, 11 bis
  13 als Begründung), `docs/Feature-Ideen-2026-08-01.md` (Idee `A18` mit Statuszeile „begonnen, K1“, Querverweis auf A15, Zeile in
  der Stands-Tabelle, Verweis am Eintrag „würde ich nicht bauen #3“), `docs/Operations.md` (englisch, Befund 18), `CLAUDE.md`
  (deutsch: Zeile im Umsetzungsstand, „hinter `Wishes:Enabled`, auf Prod aus“).
- **AKs:** keine eigenen; E15, Regel 3.
- **Fertig:** Verweise auflösbar, keine Namen, keine Hostnamen. **Commit:** `docs: document wish emotes backend core`

## 4. AK-Abdeckung

| AK | Task | AK | Task | AK | Task |
|---|---|---|---|---|---|
| AK1 | T2, T12 | AK8 | T3, T7 | AK15 | T6 |
| AK2 | T1, T5 | AK9 | T7, T12 | AK16 | T4, T10 |
| AK3 | T5, T7 (Sperre) | AK10 | T3, T7, T8, T12 | AK17 | T1, T2, T9 |
| AK4 | T4 | AK11 | T5, T7 | AK18 | T2, T9 |
| AK5 | T4 | AK12 | T5, T7 | AK19 | T4, T5 |
| AK6 | T4, T5 | AK13 | T10 | AK20 | T1, T10 |
| AK7 | T3, T7 | AK14 | T10 | AK21 | T12 |

## 5. Live-Verifikation (T12; Regel 16, AK21, AK1)

**Randbedingungen.** Kein `ssh`, kein Prod. Nie `docker compose` aus dem Worktree; die geteilte Dev-Datenbank wird nur gelesen
(Dump). Ports (`:5151`, Redis) vorher mit anderen laufenden Sitzungen abstimmen (Orchestrator). 7TV-Schreibzugriffe nur auf das Set
des Testkontos des Betreibers; die Freigabe dafür klärt der Orchestrator für diesen Anlass. Vor dem ersten Schreibzugriff den
Ausgangszustand des Sets nach (Emote-ID, Alias) in den Scratchpad schreiben, am Ende danach wiederherstellen und gegenlesen. Alle
Zahlen sind Dev-Zahlen.

| Ressource | Wert |
|---|---|
| Datenbank | Kopie der Dev-Datenbank unter eigenem Namen im Dev-Postgres-Container (`createdb`, dann Dump und Einspielen im Container); Zugangsdaten aus der Container-Umgebung, nie ausgeben |
| Redis | eigener Container per `docker run` auf abgestimmtem Port |
| Api, Worker | `dotnet run` mit absolutem Pfad aus dem Worktree; Umgebung: Verbindungszeichenfolgen auf Kopie und eigenen Redis, `Wishes__Enabled=true`, Twitch-Secrets wie im Bestand der Live-Läufe; nur der Kanal des Testkontos bleibt auf der Kopie aktiv |
| Sitzung | Login im Browser gegen die Worktree-Api, Cookie in eine Scratchpad-Datei (Modus 600) |

**Ablauf.**
1. **AK1:** `dotnet ef migrations list` gegen die Kopie zeigt genau `AddWishes` als ausstehend (mehr → Abbruch und Rückfrage);
   `database update`; `list` zeigt nichts Ausstehendes; beide Tabellen leer; Schema der Bestandstabellen vor und nach dem Update
   als Dump verglichen, ohne Unterschied.
2. **Schalter:** mit `Wishes__Enabled=false` antwortet `GET …/wishes` dem Verwalter 404 `wishes_disabled`; danach mit `true` neu
   starten. `/permissions` zeigt `wishesEnabled`.
3. **Handeintrag:** `POST …/wishes` per `curl` mit Cookie für ein Emote, das nicht im Set liegt → 201 `approved`. Parallel den
   Kanal-Stream offen halten: genau ein `wish.changed`; auf `/api/channels/live-events` keins.
4. **Hinzufügen in 7TV** von Hand (Testkonto). Nach dem nächsten Sync: Wunsch `active`, `slotId` gesetzt, `startsAtUtc` gleich
   7TVs `added_at` (an der Datenbank und in 7TV gegenlesen), `expiresAtUtc` = Start plus Laufzeit. Einmal mit EventAPI aus (reiner
   Vollsync), einmal mit EventAPI an: dort zuerst `startsAtAuthoritative = false`, nach dem Vollsync verschoben (AK8).
5. **Umbenennen** in 7TV → nach dem Sync derselbe Slot, `alias` nachgezogen, `expiresAtUtc` unverändert (AK9).
6. **Entfernen und Wieder-Hinzufügen (AK10, [B] F1),** jeweils `AddedAt` des Ersatzes, `EndedAtUtc`, `SlotId` vor und nach
   sowie `SlotAddedAtUtc` notieren:
   a. Entfernen, Sync abwarten → `removed_externally`; unter **gleichem Alias** wieder hinzufügen, Sync → zurück nach `active`
      mit alter Frist, `botOwned = false`. Ablesen, ob dieselbe `SlotId` reaktiviert wurde (Befund 5).
   b. Entfernen und unter gleichem Alias wieder hinzufügen **zwischen zwei Vollsyncs** (EventAPI aus): ein Durchlauf sieht
      beides, `AddedAt` liegt vor `EndedAtUtc`, aber im Rückgriff → am Ende `active`.
   c. Entfernen, unter **anderem Alias** wieder hinzufügen → bleibt `removed_externally`. Ausweg prüfen: `adopt` ohne `wishId`
      auf den neuen Slot → neuer Wunsch `active`, der alte unverändert. (`re-add` taugt hier nicht: Der Slot liegt dann vor
      dem neuen `ApprovedAtUtc`.)
   d. Ein verpasster Hook lässt sich live nicht sauber herstellen (ein gestoppter Worker ergibt Lage b, nicht einen
      gescheiterten Abgleich). Diese Lage beweisen die Tests aus T7 mit den echten Schreibern; im Protokoll so benennen.
7. **Bestand übernehmen** (AK5) für ein Emote, das schon im Set liegt; `extend`, `keep`, `complete-removal` je einmal; Audit-Log
   der Kopie auf `wish.*` lesen: Details ohne Alias, Namen, Login (AK19). Aktivitäts-Feed des Kanals einmal ansehen (R6).
8. **Fehlerisolation:** Abgleich künstlich scheitern lassen ist kein Live-Schritt (T8 deckt es); hier nur: Worker-Log über den
   ganzen Lauf ohne Fehler aus dem Abgleich.
9. **Aufräumen:** Set wiederherstellen und gegenlesen; Prozesse per `ps -o args= -p <pid>` prüfen, dann SIGTERM; Redis-Container
   und Datenbankkopie entfernen. Protokoll mit jedem abgelesenen Wert in den PR.

## 6. Gates vor dem PR (T13)

1. `dotnet build EmotePurge.slnx --no-incremental` ohne neue Warnungen; `dotnet format EmotePurge.slnx` und
   `npm --prefix web run format`, `npm --prefix web run lint` sauber.
2. `dotnet test EmotePurge.slnx` grün (Docker läuft, Testcontainers).
3. `npm --prefix web test -- --watch=false` grün (wegen der Locales). E2E ist nicht nötig, K1 ändert keine Oberfläche **[E]**.
4. `node scripts/coverage-local.mjs` (nur Committetes zählt; Ergebnis als Anlass zum Hinsehen, 80 % auf neuem Code).
5. Live-Verifikation aus Abschnitt 5 bestanden und protokolliert.
6. `/codex:review --model gpt-6.1-sol` einmal über den fertigen Branch; das Ergebnis geht unverändert an den Betreiber. Der
   Merge gehört dem Betreiber.
7. PR-Text (englisch) nennt: die zwölf Codes, Befund 8 (`recheck`), Befund 9, die Kosten der Rollenprüfung aus T6, die Antwort
   auf F1 samt der Lesart von AK10, die vier Spalten über die Spec hinaus, „migration `AddWishes` must be applied by hand
   before the images“.

## 7. Migration und Deploy

- **Reihenfolge:** `AddWishes` von Hand über den Tunnel **vor** den Images (`list`, `update`, `list`), dann Api und Worker. Den
  Tunnel öffnet und die Befehle fährt der Betreiber; die Sitzung bereitet sie nur vor, Passwort als Platzhalter.
- **Alle ausstehenden Migrationen prüfen**, nicht nur die eigene: `list` muss genau `AddWishes` als ausstehend zeigen.
- **Additiv:** zwei neue Tabellen, keine Bestandsspalte; das laufende alte Image ist davon unberührt. Rollback: Schalter aus
  genügt, die Tabellen bleiben liegen und stören kein älteres Image.
- **Schalter:** `WISHES_ENABLED` bleibt auf Prod **aus** bis Freigabe 1 (K1, K2, K3, K6 gemergt, Datenschutztext ergänzt). In
  Portainer ist für K1 keine Variable zu setzen; das Compose-Mapping hat den Default `false`.
- **Ohne Migration deployt** startet die Api trotzdem nicht in einen kaputten Zustand: Der Migrations-Wächter beim Start schlägt
  an. Genau deshalb zuerst die Migration.

## 8. Risiken und offene Punkte

| # | Risiko | Umgang |
|---|---|---|
| R1 | Variante (c) übernimmt kein Wieder-Hinzufügen unter anderem Namen und keinen Ersatz, der älter als der Rückgriff ist (Worker länger als 30 Minuten aus) | [B], bewusst; Ausweg „Bestand übernehmen“ als neuer Wunsch; Schritt 6 der Live-Verifikation fährt die Lagen |
| R2 | Der Abgleich verlängert den kritischen Pfad jedes Syncs | eine Indexabfrage für Kanäle ohne laufende Wünsche (Test in T7); eigener Kontext; Fehler isoliert (T8) |
| R3 | Neue Sperrzyklen mit Purge, Sync und Bericht | Kanalzeile zuerst (Befund 11); der Abgleich hält sonst nur Wunsch-Zeilen; Sol-Review mit Blick auf die Sperrreihenfolge |
| R4 | Der Bericht schreibt mehrere Kanäle in einer Transaktion, der Abgleich danach je Kanal einzeln | gewollt: je Kanal eigene Transaktion, Fehler in einem Kanal trifft die anderen nicht |
| R5 | `coverage-local` unterschätzt oder überschätzt bei den chirurgischen Änderungen in `SevenTvSyncService` und `EmoteService` | Hooks über einen kleinen, eigens getesteten Wrapper (T8), damit die neuen Zeilen gedeckt sind |
| R6 | Der Aktivitäts-Feed kennt `wish.*` bis K3 nicht | nur lokal sichtbar (Schalter auf Prod aus); in der Live-Verifikation ansehen, Befund in den PR |
| R7 | Zusage aus AK15 hängt an der Cache-TTL eines anderen Features (Befund 13) | in DECISIONS benannt |
| R8 | `MergedIntoWishId` und `SlotId` an beendeten Wünschen zeigen auf Zeilen, die verschwinden können | beide SET NULL, Test in T2 |
| R9 | Einlöser-Daten werden nur bei `Retention:Enforce = true` geleert (RR9 der Spec) | Operations-Abschnitt nennt es (T11) |
| R10 | Generationswechsel ist eine Heuristik am Datum: Eine Datums-Bestätigung mit späterem Datum (Uhrenabweichung) läuft als Entfernen und Rückkehr | Frist bleibt, in K1 ohne sichtbare Folge; K4 muss wissen, dass dabei Bot-Eigentum endet (DECISIONS) |
| R11 | `SlotConfirmedAtUtc` ist bis zu 30 Minuten alt; bei verpasstem Hook verkürzt das die 6 Stunden entsprechend | Richtung ist die sichere (eher keine Übernahme); K2 ruft den Abgleich alle 15 Minuten |
| R12 | Zeilensperren verlängern Wartezeiten: Ein Befehl wartet auf einen laufenden Abgleich des Kanals | Abgleich hält die Sperren nur für Lesen der Slots und einzelne Updates; kein Netzaufruf in der Transaktion |

## 9. Selbstprüfung des Plans

- Jeder Abschnitt der Spec, den K1 nennt (5, 6.1 bis 6.3, 6.5, Routen, Codes, Event, E25, Audit), hat einen Task; AK1 bis AK21
  sind zugeordnet (Abschnitt 4).
- Namen sind über die Tasks gleich: `IWishService`, `IWishOverviewService`, `IWishReconcileService.ReconcileChannelAsync`,
  `WishTransitions`, `WishSlotMatcher`, `WishOptions`, `WishSettingsAuthorizationFilter`, `MapWishEndpoints`.
- Lagen, die die Spec nicht ausbuchstabiert und die ein Test festhält: wiederaufgenommener Slot (T3, T7), verpasster Archiv-Hook
  (T7), Slot ohne Datum (T4, T7), Wettlauf am Eindeutigkeitsindex (T4, T7), Befehl gegen Datumskorrektur (T5, T7),
  Verlierer-Kanal mit Wünschen (T9), `re-add` nach 120 Tagen (T9), scheiternder Abgleich im Sync (T8).
- Spalten über Spec 6.1 hinaus: `LinkOrigin`, `SlotAddedAtUtc`, `SlotConfirmedAtUtc`, `FirstActivatedAtUtc`. Alle in `AddWishes`,
  keine Bestandstabelle berührt.

## 10. Betreiberentscheidungen und Lesarten

**F1, entschieden [B] 2026-11-17: Variante (c).** Für `removed_externally` gilt ein Rückgriff von 30 Minuten vor `EndedAtUtc`, und
der Kandidat muss den Alias des Wunsches tragen. Eingearbeitet in Befund 6, T3, T7, Abschnitt 5. `approved` bleibt ohne Toleranz.

**Lesart von AK10 unter [B], zur Bestätigung beim Review des Plans (blockiert nichts).** AK10 sagt wörtlich „zwei neue Slots
binnen 6 h → bleibt `removed_externally`“. Mit der Alias-Bedingung heißt das: Es bleibt, wenn **keiner** der beiden den Alias des
Wunsches trägt. Trägt genau einer ihn, ist er der eine Kandidat und wird übernommen (Test in T3 und T7). Zwei aktive Slots mit
demselben Alias kann es in einem Set nicht geben.

Offene Betreiberfragen gibt es nicht; alles Übrige ist als **[E]** entschieden und widerrufbar.
