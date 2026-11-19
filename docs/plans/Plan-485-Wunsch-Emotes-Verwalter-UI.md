# Plan #485: Wunsch-Emotes, Kind K3: Verwalter-UI (manueller Modus)

Erstellt am 2026-11-18 im Worktree `EmotePurge-wt-485` (Branch `feat/485-wish-ui`, Basis `origin/main` @ `135eae07`; K1 ist mit
PR #492, K2 mit PR #493 gemergt. Alle Codestellen dieses Plans sind an diesem Stand gelesen).
**Status: Revision 3 (2026-11-18), Endstand des Plans; eine weitere Plan-Runde gibt es nicht. Sol adversarial dreimal durch
(`gpt-6.2-sol`; Runde 1: 2 × P1, 9 × P2, 2 × P3; Runde 2: 2 × P1, 6 × P2, 3 × P3; Runde 3, nur Lösch- und Meldepfad: 2 × P1,
4 × P2). Jeder Befund ist am Code nachgeprüft, alle halten. Der Betreiber hat nach Runde 3 Variante A entschieden: vier
Befunde werden behoben, zwei mit reinem Buchungsschaden sind als begrenztes Risiko hingenommen (R19, R20) und gehen in ein
Folge-Issue (Abschnitt 14). Keine Migration. Keine offene Betreiberfrage. Die Code-Prüfung durch Sol folgt vor dem Merge.**
Quellen: [Spec](../superpowers/specs/2026-11-17-wunsch-emotes-spec.md) Abschnitte 1 bis 17 und „K3: Frontend manueller Modus“
(AK32 bis AK45); aus K1 und K2 die Verträge; [Entwurf](../designs/Wunsch-Emotes-mit-Ablauf-2026-11-17.md);
[Plan-483](Plan-483-Wunsch-Emotes-Backend-Kern.md) und [Plan-484](Plan-484-Wunsch-Emotes-Ablauf-Lauf.md) als Vorbild; die
Live-Protokolle `docs/nachtlaeufe/live-T12-2026-11-18.md` und `docs/nachtlaeufe/live-484-2026-11-18.md`; DECISIONS „Wish emotes,
child 1“ und „child 2“; `CLAUDE.md` (Regeln 4, 7, 11, 12, 13, 14, 16, Tests, E2E, Sprache), `web/.claude/CLAUDE.md`,
`docs/UI-Designsprache.md` (verbindlich), `DESIGN.md`; Issue #485, Epic #481.

**Was dieser Plan trägt:** die Abweichungen zwischen Spec und gemergtem Backend, die Entscheidungen dazu, den Schnitt in Tasks
mit Verträgen, Grenzfällen und Tests, die AK-Zuordnung, Specs nach Regel 12, E2E-Fälle, die Live-Verifikation, Gates. Kein
Code; Namen nur als Form. Die Spec reist mit: Jeder Implementer liest seinen Task hier, die genannten Spec-Abschnitte **und** die
genannten Bestandsdateien, bevor er schreibt. Vor jeder UI-Arbeit `docs/UI-Designsprache.md` §11 (Checkliste) und `DESIGN.md`.

**Lesehinweis:** **[E]** Planer-Entscheidung (der Betreiber kann widersprechen) · **[P]** Präzisierung ohne Entscheidungsgehalt ·
**[B]** Betreiberentscheidung (Spec Abschnitt 17 und 2026-11-18) · **Sol-P1-*n*/P2-*n*/P3-*n*** Befund der adversarialen Prüfung,
Zählung in Abschnitt 13 · **[F*n*]** Betreiberfrage aus Abschnitt 11 · „Spec E19“ meint die Festlegung der Spec, „AK35“ das
Akzeptanzkriterium aus K3, „T12-Auff. 3“ die Auffälligkeit 3 aus dem Live-Protokoll T12.

### Revision 1 (2026-11-18): was sich geändert hat

| Änderung | Anlass |
|---|---|
| Backend-Vorlauf T0a (Lesewege), T0b (Schreibverträge), T0c (Live-Probe der Api) ersetzt das bedingte T0. **Keine Migration** (2.1a) | [B], Sol-P1-1, P1-2, P2-4, P2-5, P2-6, P2-11, P3-12, P3-13 |
| `WishDto` trägt die gebundene Slot-Generation und ein Bild; Löschziel und Bestätigung hängen daran und am eingefrorenen Set; die „Slot-Karte“ aus `usage-stats/totals` entfällt | [B], Sol-P1-1 |
| `complete-removal` ist an Slot, Generation und Set gebunden und idempotent; `EndedAtUtc` bleibt beim Übergang aus `removed_externally` erhalten (4d wird im Backend behoben, nicht in der Anzeige kaschiert) | [B], Sol-P1-2, P3-12 |
| Beide 409 des Übernehmens nennen den Konflikt-Wunsch; eigener Leseweg für übernehmbare Einträge mit dem Filter von `adopt` | [B], Sol-P2-5, P2-6 |
| `PUT` der Einstellungen lässt eine nicht gesendete Schwelle unberührt; `canEdit` im `GET` | [B], Sol-P2-4, F1 = A |
| Die Detailantwort liefert die Tagesreihe ihres eigenen Fensters; die Sparkline zeigt genau das, kein zweiter Abruf je Zeile | [B], Sol-P2-11 |
| Verbindliche Verträge: Start eines 7TV-Vorgangs (T8, T9), Abschluss je Lauf (T9), Gültigkeit und Aufholen der Listen (T4), Kopplung von Read und Feldern im Dialog (T6) | [B], Sol-P2-3, P2-7, P2-8, P2-10 |
| Live-Verifikation: Stufe B ist Pflicht und wird vom Orchestrator gefahren; Testdaten getrennt je Zweck; LAN-Durchgang gegen dieselbe Kopie | [B], Sol-P2-9 |

### Revision 2 (2026-11-18): Nachprüfung durch Sol über Revision 1

| Änderung | Anlass |
|---|---|
| Wunsch-Löschungen lesen das Set vor **jedem** Sendeversuch frisch, auch nach einem 429; der 5-Sekunden-Wiedergebrauch der Bindung gilt für diesen Weg nicht. Die Lücke zwischen Read und REMOVE ist als hingenommenes Risiko benannt (7TV kennt kein bedingtes Entfernen) | [B], Sol-R2-P1-1 |
| `complete-removal` sperrt Kanal vor Wunsch, prüft das Set in jeder Zeile und weist ab, wenn in der Slot-Inventur schon ein Ersatz liegt; Beleg des Sets ist das aktuell aktive Set, fehlender Beleg ist 409. Weiterhin **keine Migration** | [B], Sol-R2-P1-2 |
| Startvertrag: autoritativer Read des aktiven Sets benannt; erst alle Reads, dann ein synchroner Schritt für Seite, Token, Startanspruch und Übernahme; Regeln für Wiederholungen nach 429 | [B], Sol-R2-P2-3 |
| `adopt` bekommt erwarteten Alias und erwartete Generation und vergleicht unter der Kanalsperre; undatierter Slot geregelt | [B], Sol-R2-P2-4 |
| Eintragsliste unterscheidet „gezielt verknüpfen“ von „gesperrt“ (`runningWishAdoptable`); Sprungziel für ungeladene Zeilen; fester Ablauf bei veraltetem Konfliktstand | [B], Sol-R2-P2-5 |
| Nutzungswerte verfallen nach 5 Minuten; Wiederverbindung wird je Kanal-Stream beobachtet | [B], Sol-R2-P2-6 |
| Live-Probe B5 mit eigens angelegtem Testeintrag; Vorher-nachher-Vergleich mit Set-ID, Alias und `addedAt` | [B], Sol-R2-P2-7 |
| Durchgang am echten Handy ist Pflichtschritt in Gate 7 mit beobachtetem Ergebnis | [B], Sol-R2-P2-8, F8 |
| Bild: die einfache Fassung nur bei bestätigt unbewegtem Emote, bei unklarem Ladefehler bleibt die Fläche leer | [B], Sol-R2-P3, F7 |
| Routen werden im Task ihrer Zielseite eingehängt (T4, T11); T3 nur Guards und Tab-Bedingungen | Sol-R2-P3 |
| Audit prüft bei 360 px die vollständige Sichtbarkeit samt beschnittener Vorfahren; Stufe 3 ist eine ausdrückliche Abweichung von AK44 | Sol-R2-P3 |

### Revision 3 (2026-11-18): dritte, enge Sol-Runde über T0b, T9 und 2.1a

| Änderung | Anlass |
|---|---|
| Vor **jedem** Wunsch-REMOVE: aktives Set am Anwendungs-Cache vorbei gelesen, Generation geprüft, Beobachtung des Ziels höchstens 5 Sekunden alt, bestätigtes Token unverändert; sonst kein Senden. Die Set-Liste bekommt dafür einen Refresh-Parameter (T0a) | [B], Sol-R3-P1 (inaktives Set), R3-P2 (Alter der Bindung), R3-P2 (Token) |
| Zeile 6 der Meldetabelle deckt jeden Eintrag ab, an den der Abgleich den Wunsch zurückführen könnte, auch einen älteren mit demselben Alias | [B], Sol-R3-P1 (Rückkehrkandidat) |
| R16 nennt das tatsächlich mögliche Fenster; zwei Buchungsrisiken sind benannt (R19, R20) und einem Folge-Issue zugewiesen (Abschnitt 14); die Aussage „beim Set-Wechsel wird pauschal archiviert“ ist gestrichen | [B], Sol-R3-P2 (Set-Wechsel), R3-P2 (Zuordnung) |

---

## 0. Befund vor dem Plan

Alle Stellen am Stand `135eae07` nachgelesen. Pfade ohne Präfix liegen unter `web/src/app/`.

1. **Die Frontend-Hälfte der Fehlercodes ist schon da.** K1 hat die zwölf `wish_*`-Codes bereits in `core/i18n/api-error.ts` und
   als `errors.api.wish_*` in beide Locale-Dateien geschrieben (Spec sah das für K3 vor). **[P]** K3 prüft Vollständigkeit und
   Wortlaut gegen die fertige Oberfläche (T13), legt aber nichts doppelt an.
2. **Elf Audit-Aktionen, nicht neun.** `AuditActions` (`src/EmotePurge.Core/Entities/AuditLogEntry.cs:54-68`) kennt
   `wish.create`, `wish.adopt`, `wish.extend`, `wish.keep`, `wish.remove`, `wish.close`, `wish.retry`, `wish.readd`,
   `wish.settings`, `wish.due` (K2, Akteur „system“) und `wish.update` (PATCH). Im Frontend fehlen alle elf: `ACTION_KEYS`
   (`shared/audit/audit-actions.ts`), der Typ `AuditAction` (`core/audit/audit.model.ts`) und `audit.actions.*` in beiden Locales.
   Die Filter beider Audit-Ansichten leiten sich aus `ACTION_KEYS` ab; `audit-actions.spec.ts` nagelt die Zahlen 23 und 20 fest.
3. **Audit-Zeilen der Wünsche haben kein Detail.** `AuditLogQueryService.ProjectDetail` kennt die Wunsch-Details nicht und
   liefert `detail: null` (T12 Schritt 7h). **[E]** K3 liefert nur die Aktionsnamen; ein Detail-Kind ist Folgearbeit. Der
   Vorlagegrund `unconfirmed_start` bekommt seine Übersetzung dort, wo er sichtbar wird: in `wishes.reason.*` an der Zeile.
4. **`wishesEnabled` fehlt im TS-Modell.** `/permissions` liefert das Feld (`ChannelEndpoints.cs:130`, `:733`),
   `ChannelPermissions` (`core/channels/channel.model.ts:14-26`) kennt es nicht; `mockChannelPermissions` im E2E ebenso wenig.
5. **`emote_ids_invalid` mit fremdem Text.** `WishOutcome.EmoteIdInvalid` wird auf den Bestandscode `emote_ids_invalid`
   abgebildet (`WishEndpoints.cs:298`); dessen Text stimmt für die Abstimmungs- und Tag-Routen. Ebenso landen `StartsAtInvalid`
   und `ModeInvalid` auf `invalid_request_body`. Vorbild für einen kontextabhängigen Text ohne Eingriff in den Bestand:
   `backfillStartErrorKey` (`features/channel-workspace/backfill-section.ts:69`).
6. **Vorrang der 409 beim Handeintrag (T12-Auff. 1) ist Absicht des Backends.** `WishService.CreateAsync` prüft „im Set“ vor
   „läuft schon“. Der anschließende `adopt` prüft zuerst, ob der Slot gehalten wird (`WishService.cs:~303-309`), und antwortet
   dann `wish_slot_already_linked` **ohne** `wishId`; erst danach käme `wish_already_running` mit ID. Für den Fall aus T12
   (Emote im Set, Wunsch läuft und hält den Slot) erfährt der Client die ID des laufenden Wunsches heute also nie (Sol-P2-5).
7. **Handeintrag ohne Alias und ohne Namen → 400 `wish_alias_invalid`** (T12-Auff. 2): Der Server leitet Alias und Name
   wechselseitig ab; fehlt beides, ist der Alias leer.
8. **`emoteName` nach `adopt` ist der Slot-Name** (T12-Auff. 3; `WishService.cs:333`, `slot.Emote.Name`), nicht der
   7TV-Standardname. Nach einer Umbenennung folgt `alias` dem Slot, `emoteName` bleibt stehen.
9. **`complete-removal` setzt `EndedAtUtc` auf die Befehlszeit** (T12-Auff. 4): `RunTransitionAsync` (`WishService.cs:~826`)
   stempelt bei jedem Zustandswechsel in einen Endzustand „jetzt“, auch beim Wechsel von einem Endzustand in den anderen
   (`removed_externally` → `removed`). Die Fristen der Retention rechnen gegen `EndedAtUtc`
   (`DataRetentionService.cs:744`, `:756`); über Mitternacht verschiebt sich auch der angezeigte Tag (Sol-P3-12).
10. **`WishDto` trägt weder die Generation des Slots noch ein Bild.** Die Tabelle hat `SlotAddedAtUtc` (die Generation der
    Verknüpfung, vom Abgleich bei jedem Halten gesetzt, `WishReconcileService.HoldGeneration`), das DTO gibt sie nicht aus.
    Dieselbe Slot-Zeile kann nach Entfernen und Wieder-Hinzufügen mit neuem `AddedAt` wiederbelebt werden (T12-Auff. 6): Slot-ID
    und Alias allein benennen den Aufenthalt nicht (Sol-P1-1). Ein Bild-Merkmal fehlt ganz; `Emote.ImageUrl` der Kanalzeile
    desselben 7TV-Emotes trägt es (die `_static`-Form nur bei animierten Emotes), sobald das Emote je im Set gesehen wurde.
11. **`complete-removal` prüft nur den Zustand.** `CompleteRemovalAsync` (`WishService.cs:533-553`) kennt weder die entfernte
    Generation noch einen Vorgang. Kehrt ein extern entfernter Wunsch binnen 6 Stunden an einen neuen Eintrag zurück
    (`Return`, wieder `active`), bucht eine verspätete oder wiederholte Meldung des alten Laufs diesen neuen Aufenthalt als
    `removed`, obwohl der Eintrag im Set liegt (Sol-P1-2). Eine verlorene 200-Antwort ist nicht wiederholbar: Der zweite Aufruf
    antwortet 409.
12. **Die Einstellungen sagen nicht, ob der Aufrufer sie ändern darf**, und `PUT` überschreibt immer alle drei Werte
    (`UpdateSettingsAsync`, `WishService.cs:47-107`). Ein Client, der die Schwelle nicht zeigt und den geladenen Wert
    zurückschickt, setzt eine inzwischen von jemand anderem geänderte Schwelle zurück und verwirft damit nach E25 Einreihungen
    (Sol-P2-4). `UpdateWishSettingsRequest.ProtectionThresholdPerDay` unterscheidet heute nicht zwischen „fehlt“ und `null`.
13. **`usage-stats/totals` ist keine vollständige Slot-Quelle.** `UsageViewRows` liefert für ein Emote mit einem ungesetzelten
    Seed des Sets bewusst eine Emote-Zeile mit `slotId: null`, auch wenn daneben ein eigener aktiver Slot existiert
    (`UsageViewRows.cs:114-117`); `adopt` akzeptiert diesen Slot aber (Filter: Kanal, nicht archiviert, kein ungesetzelter Seed)
    (Sol-P2-6).
14. **Die Kennzahlen der Detailantwort und eine Slot-Reihe beschreiben Verschiedenes.** `WishUsageQueries.ReadAsync` summiert
    `UseCount` über **alle** eigenen Slots des Emotes im Kanal an den UTC-Tagen des E4-Fensters (höchstens die letzten 7 Tage).
    Eine Tagesreihe eines einzelnen Slots seit Wunschbeginn daneben kann eine andere Tendenz zeigen (Sol-P2-11).
15. **Kein 7TV-Schreiben ohne Maus ist bestehende Doktrin.** UI-Designsprache §2.5: Auswahl, Dock und alle 7TV-Schreibpfade
    liegen hinter `PointerModeService.isCoarse`; was dort wegfällt, wird nicht erklärt. Dieselbe Stelle schließt bei
    `prefers-reduced-motion` das Laden und Abspielen von Animationen aus (Sol-P3-13).
16. **Es gibt keine Kartenklasse** (UI-Designsprache §2.1). **[P]** Die „Karte“ der Spec ist eine Zeile einer linierten Liste;
    die Komponente heißt `wish-row`.
17. **Die Kopfzeile ist bei 360 px schon voll.** `app-shell.ts`: Ein Status-Badge allein drückt die Wortmarke auf 3 px
    (Szenario `shell-both-warnings`). Ein zweiter Link mit Zahl passt daneben nicht ohne Regel für schmale Breiten (AK44).
18. **AK38 gegen UI-Designsprache §4.2.** §4.2: gesperrte 7TV-Startknöpfe „ohne Hinweistext“, das Dock ist der Hinweis. AK38
    verlangt den Grund am Knopf. Auflösung in 2.7.
19. **Vorbild für einen Lösch-Lauf außerhalb der Nutzungsseite existiert:** `shared/tags/tag-removal-flow.ts` startet
    `SevenTvDeleteService` mit eigener Bestätigung über `resolveDeleteTarget`, `confirmTimeRefusal`, `toDeleteQueueEmotes` aus
    `shared/seven-tv/delete-flow.ts` und zeigt den Lauf in einem seiteneigenen Dock (`tags-page.html:532-540`). Der Lauf bringt
    die Bindung je Eintrag (frischer Read vor jedem Versuch; verglichen wird der Zeitpunkt in Millisekunden, `entryTimeKey`),
    das Nachlesen bei unbekanntem Ausgang, den Bericht `sync-deleted`, Protokoll und Tab-Schließ-Sperre mit.
    **Aber:** `startDelete` gibt nichts zurück, `run`/`lastRun` projizieren nur den *gezeigten* Lauf, und ein abgebrochener
    Lauf mit bereits abgesendetem REMOVE endet nach dem Nachlesen unter Umständen als `done` (Sol-P2-7).
20. **Der Arbiter reserviert nichts.** `startLocked` ist aus Signalen abgeleitet; zwischen einer Prüfung und dem Schreiben
    liegen Token-Dialog und Vorab-Check. `confirmTimeRefusal` prüft nur laufenden Vorgang und Token (Sol-P2-3). Für genau
    dieses Fenster kennt der Arbiter `startCheckPending` je Teilnehmer (#280).
21. **`liveReload` entprellt Ereignisse, ordnet aber keine Antworten** und kennt kein Aufholen; der Publisher von
    `wish.changed` darf nach dem Commit scheitern (K1 (i)) (Sol-P2-8). `LiveUpdateService.status` zeigt den Verbindungszustand.
22. **`latestOnly` zählt beim Abonnieren, nicht beim Eingabewechsel** (`core/http/latest-only.ts`): Zwischen einer neuen
    Eingabe und dem Ende der Entprellung darf die alte Antwort noch liefern (Sol-P2-10).
23. **Kein Browser-Read für ein einzelnes Emote.** Der Browser liest heute nur ganze Sets (`core/seven-tv/seven-tv-set-entries.ts`,
    v4-GQL). Die Form einer Einzelabfrage ist live zu belegen, nicht zu raten.
24. **`AddEmote` liegt dreifach vor** (`seven-tv-import.service.ts:74`, `seven-tv-restore.service.ts:54`,
    `seven-tv-undo.service.ts:75`), wortgleich.
25. **Ein anonymer 401 auf einem `/api/`-Aufruf meldet die Sitzung ab** (`core/http/api-auth.interceptor.ts`). In fast allen
    ~100 bestehenden E2E-Fällen ist `GET /api/wishes/summary` nicht gemockt.
26. **`recheck` antwortet in K1 immer 409**, `pending` hat vor K6 keinen Erzeuger, `failed`, `unconfirmed` und alle Gründe
    außer `expired` entstehen erst mit dem Bot (K4). Live erreichbar sind in K3: `approved`, `active`, `presented` (`expired`),
    `removed`, `removed_externally`, `kept`, `closed`.
27. **`adopt` eines alten Eintrags ergibt einen sofort abgelaufenen Wunsch** (T12-Auff. 5): Ohne `startsAtUtc` zählt das
    `AddedAt` des Slots.
28. **`setFull` kommt nur, wenn wahr** (`CreatedWishDto`). **Rate-Limit:** Lese-Routen auf `InteractiveRead` (Eimer 300,
    Nachfüllung 5 je Sekunde, je Nutzer). **Die Schutzschwelle wirkt im manuellen Modus nicht** (Plan-484 Befund 16).
29. **Die Bindung des Lösch-Laufs darf einen jungen Read wiederverwenden.** `bindBeforeRemove`
    (`seven-tv-delete.service.ts:~725-742`) liest nur dann frisch, wenn kein Read gehalten wird, der gehaltene ein Lücken-Read
    ist, älter als `ENTRY_READ_REUSE_MS` (5 s) ist oder das Emote zwei Einträge hat. Die Engine fragt den Haken vor jedem
    Versuch erneut, auch vor der Wiederholung nach einem 429; der Read davor kann dann noch der alte sein (Sol-R2-P1-1).
    **7TV kennt kein bedingtes Entfernen:** Die Mutation nimmt Set, Emote-ID und Alias (`REMOVE_SLOT_ENTRY_MUTATION`); der
    Entwurf hält das ausdrücklich fest („7TV kennt kein bedingtes Entfernen“).
30. **Slots werden vor dem Wunschabgleich committet.** Vollsync und Delta sperren die Kanalzeile, schreiben die Slots und
    committen (`SevenTvSyncService.cs:928-942`, `:738`); der Abgleich läuft danach in eigener Transaktion (`:224`, `:314`). In
    diesem Fenster steht ein Ersatz-Eintrag schon in der Inventur, während der Wunsch noch die alte Generation trägt
    (Sol-R2-P1-2). Weder Wunsch noch Slot speichern eine Set-ID; `Channels.ActiveEmoteSetId` ist der einzige Set-Bezug.
31. **`GET …/emotes/active-set` ist der Postgres-Stand** (`EmoteAdminService`, `Channel.ActiveEmoteSetId`). 7TVs eigenes
    `style.activeEmoteSetId` liefert `SevenTvEmoteSetService.listForeignChannelEmoteSets`
    (`GET /api/seventv/channels/{c}/emote-sets`, `seven-tv-emote-set.service.ts:~393-398`) (Sol-R2-P2-3).
32. **`AdoptAsync` übernimmt, was unter der Slot-ID gerade liegt** (`WishService.cs:~278-284`, `:~357`): aktueller Alias,
    aktuelles Datum; der Rumpf nennt keine erwartete Generation (Sol-R2-P2-4).
33. **`LiveUpdateService.status` ist dienstweit** („service-wide, so with two concurrent streams the last writer wins“,
    `live-update.service.ts:~62-64`); das Wieder-Öffnen eines bestimmten Streams ist daraus nicht ablesbar. Die Detailwerte
    (`LoadUsageAsync`) hängen am Auswertungszeitpunkt, nicht an Feldern des Wunsches (Sol-R2-P2-6).

34. **Befunde der dritten Runde, am Code bestätigt:** (a) Die Set-Liste (`GET /api/seventv/channels/{c}/emote-sets`,
    `SevenTvEndpoints.cs:70`) läuft über den 60-Sekunden-Cache des Härtungs-Dekorators und hat, anders als die
    Schwester-Route `…/emotes` (`:42-55`), keinen `refresh`-Parameter. (b) `SevenTvRunEngine.start` liest das Token einmal
    (`seven-tv-run-engine.ts:420`) und reicht den Wert an jede Wiederholung; `clearToken` leert nur den Sitzungsspeicher.
    (c) `loadSevenTvSetEntries` liest bei mehrseitigen Sets die Seiten 1 bis n−1 zur Verifikation erneut, die letzte nie; der
    Bindungs-Read darf bis zu 20 Sekunden dauern (`ENTRY_BINDING_READ_TIMEOUT_MS`). (d) Der Sync hält einen Set-Wechsel in
    einer eigenen Transaktion fest, bevor er die Kanalzeile für die Inventur sperrt (`SevenTvSyncService.cs:~888-890`,
    `:917`), und gleicht danach Einträge nach Datum und Alias ab; er archiviert beim Wechsel **nicht** pauschal. (e) Das
    Nachlesen eines Lösch-Laufs wertet eine unbekannte Zeile als `done`, sobald die alte Generation fehlt; ob der eigene
    REMOVE oder jemand anders sie entfernt hat, ist daraus nicht ablesbar. (f) `WishReconcileService.Affected` führt einen
    extern entfernten Wunsch an jeden freien Eintrag desselben Emotes und Alias zurück, dessen Datum bis 30 Minuten vor und
    bis 6 Stunden nach dem Ende liegt, also auch an einen **älteren** (`WishReconcileService.cs:~101-111`).

---

## 1. Umfang

### 1.1 Drin

**Backend-Vorlauf (T0a bis T0c):** die Verträge aus 2.1a. **Oberfläche:** Tab „Wünsche“ im Channel-Workspace (Arbeitsliste,
live über den Kanal-Stream), Abschnitt „Wünsche“ im Settings-Tab, Seite `my-wishes`, Link mit Zähler in der Kopfzeile,
Handeintrag („Nachtragen“), „Bestand übernehmen“, alle Buchungs-Aktionen des manuellen Modus, Hinzufügen und Entfernen in 7TV
mit dem eigenen Token, i18n de/en, Audit-Aktionsnamen, `wishesEnabled` im Modell, E2E, Audit-Harness-Szenarien, DECISIONS,
Live-Probe.

### 1.2 Was K3 den anderen Kindern liefert

- **K4:** `complete-removal` verlangt jetzt Slot, Generation und Set und nimmt die Kanalsperre, `adopt` verlangt den
  erwarteten Alias und die erwartete Generation (2.1a); ein Bot, der dieselbe Route nutzte, müsste sie
  mitgeben (der Bot hat laut Spec seinen eigenen Weg, der Vertrag steht hier, damit K4 ihn nicht übersieht). `PUT …/settings`
  ohne Schwelle lässt sie stehen; ausdrücklich `null` schaltet den Schutz weiterhin aus. Der Arbiter kennt die Laufart `wish`.
- **K5:** die Zeile rendert unbekannte Gründe und Zustände neutral; K5 ergänzt die Bot-Gründe (`protected`, `coverage_gap`,
  `not_bot_owned`, `ownership_lost`, `unconfirmed`, `bot_unavailable`, `already_in_set`, `repair_review`, dazu die
  `failed`-Gründe) in `wishes.reason.*`, die Aktionen `recheck` und „Bot entfernen lassen“ (2.3), das Feld Schutzschwelle und
  den Bot-Modus im Settings-Abschnitt. `unconfirmed_start` und `expired` sind schon übersetzt; `canEdit` ist da.
- **K6:** `view=open` liefert `pending` dann mit; die Zeile zeigt `pending` bis dahin neutral. Der 7TV-Einzel-Read (T6), der
  Link-Parser (T2), `imageUrl` am DTO und der Leseweg der übernehmbaren Einträge sind wiederverwendbar. Hinweis: Ein Treffer
  über den Namen darf sich nicht auf `emoteName` übernommener Wünsche stützen (Befund 8).

### 1.3 Draußen

- Bot-Modus aktivieren, Bot-Zustände, Schutzschwelle bedienen (K4, K5). Offene Wünsche, Anlernen, Lückenanzeige (K6).
- **Abgrenzung, kein K3-Thema:** Das Nutzungsfenster zählt den angebrochenen Starttag voll mit (Live-Protokoll #484, Befund 4:
  acht Tageszeilen geteilt durch 7). Das entscheidet erst im Bot-Modus über ein Entfernen und gehört zu **K4**. K3 zeigt
  `total`, `perDayMean` und die Tagesreihe unverändert so, wie die Api sie liefert, und rechnet nichts nach. T0a ändert an der
  Fensterrechnung nichts; die neue Tagesreihe hat deshalb genau die Tage, über die heute summiert wird.
- Audit-Details der Wunsch-Einträge (Befund 3). Eine Korrektur von `emoteName` nach `adopt` (Befund 8; die Oberfläche zeigt
  das Feld nicht). Ein Auslöser für einen sofortigen Sync nach dem Hinzufügen: Die Verknüpfung macht der Abgleich (Spec K3).

---

## 2. Verträge, die mehrere Tasks teilen

### 2.1 Api-Fläche nach dem Vorlauf

Kanal-Gruppe `/api/channels/{c}/wishes`, Fehlerleiter 400 (Name) → 401 → 403 (kein Verwalter) → 404 `wishes_disabled` → Rumpf.
**Fett** = durch T0a/T0b neu oder geändert, alles andere ist der gemergte Stand.

| Route | Rumpf / Query | Erfolg | Ablehnungen (Code) |
|---|---|---|---|
| `GET /` | `view` = `open` (Vorgabe) \| `running` \| `ended`, `page`, `pageSize` (Vorgabe 20, höchstens 100) | Seite von `WishDto` | 400 `invalid_request_body` (unbekannter `view`), 404 `channel_not_found` |
| `GET /{id}` | – | `WishDto` plus `usage` (oder `null` ohne Start), **`usage.daily`** | 404 `wish_not_found` |
| **`GET /slots`** | **`sevenTvEmoteId?`** | **Liste der übernehmbaren Einträge mit Konfliktlage (2.1a)** | 404 `channel_not_found` |
| `POST /` | `sevenTvEmoteId`, `emoteName?`, `alias?`, `durationDays?`, `requesterLogin?` | 201 `WishDto`, bei vollem Set zusätzlich `setFull: true` | 400 `emote_ids_invalid`, `wish_duration_out_of_range`, `wish_alias_invalid`, `wish_requester_invalid`; 409 `wish_emote_already_in_set` (+ `slotIds`, **+ `wishId` des laufenden Wunsches oder `null`**), `wish_already_running` (+ `wishId`) |
| `POST /adopt` | `slotId`, **`expectedAlias`, `expectedAddedAtUtc`**, `wishId?`, `durationDays?`, `startsAtUtc?` | 201 `WishDto` | 404 `wish_slot_not_found` (**auch: Alias oder Generation des Slots weicht vom Erwarteten ab**), `wish_not_found`; 409 `wish_slot_already_linked` (**+ `wishId` des Halters**), `wish_already_running` (+ `wishId`), `wish_state_conflict`; 400 `wish_duration_out_of_range`, `invalid_request_body` |
| `PATCH /{id}` | `alias?`, `durationDays?` | 200 `WishDto` | 409 `wish_state_conflict`, 400 wie oben |
| `POST /{id}/extend` · `/keep` · `/close` · `/retry` · `/recheck` | – | 200 `WishDto` | 404 `wish_not_found`, 409 `wish_state_conflict` (`recheck` bis K4 immer) |
| **`POST /{id}/complete-removal`** | **`slotId`, `slotAddedAtUtc`, `emoteSetId` (alle Pflicht), `evidence?`** | **200 `WishDto`, auch bei Wiederholung (2.1a)** | **400 `invalid_request_body`**, 404 `wish_not_found`, 409 `wish_state_conflict` |
| `POST /{id}/re-add` | `mode` = `timed` \| `permanent`, `durationDays?` | 200 `WishDto` | 400 `invalid_request_body`, `wish_duration_out_of_range`; 409 `wish_already_running`, `wish_state_conflict` |
| `GET /settings` | – | `defaultDurationDays`, `probationLeadDays`, `protectionThresholdPerDay`, `botModeEnabled`, `botSuspendedReason`, **`canEdit`** | – |
| `PUT /settings` | `defaultDurationDays`, `probationLeadDays`, **`protectionThresholdPerDay` optional: fehlt = unverändert, `null` = aus** | 200 Einstellungen | 400 `wish_settings_out_of_range`, 403 `wish_settings_restricted` |

Kanalübergreifend: `GET /api/wishes/mine` → unpaginierte Liste aus `{ channelName, wish }`, serverseitig sortiert nach Ablauf
(ohne Ablauf zuletzt), dann Anlage; enthält Handlungsbedarf und Probezeit, **ohne** Merkmal, welches von beiden.
`GET /api/wishes/summary` → `{ actionCount }`. Beide 404 `wishes_disabled` bei ausgeschaltetem Schalter, 401 ohne Sitzung.

`WishDto` (camelCase, Zeiten ISO-8601 UTC): `id`, `sevenTvEmoteId`, `emoteName`, `alias`, `source`
(`irc`\|`manual`\|`adopted`), `status`, `statusReason`, `requesterLogin`, `durationDays`, `createdAtUtc`, `approvedAtUtc`,
`startsAtUtc`, `startsAtAuthoritative`, `probationStartsAtUtc`, `expiresAtUtc`, `endedAtUtc`, `slotId`, **`slotAddedAtUtc`**,
**`imageUrl`**, `linkOrigin`, `extensionCount`, `permanent`, `botOwned`, `botWritePending`, `botNextAttemptAtUtc`,
`removalQueuedAtUtc`, `mode` (`manual`\|`bot`). `usage`: `windowFromUtc`, `windowToUtc`, `total`, `perDayMean`, `rank`,
`rankOf` (beide `null` ohne aktiven Slot), `coverageGap`, **`daily`**. Zustände und Gründe sind Zeichenketten und Vertrag
(`WishStatus.cs`); das TS-Modell typisiert sie als bekannte Literale **plus** `string`.

Live: `wish.changed` mit `channel`, ohne Nutzlast, nur auf `GET /api/channels/{c}/live`; höchstens eines je Commit.

### 2.1a Die Verträge des Backend-Vorlaufs (T0a, T0b) [B]

Dieser Abschnitt ist für sich lesbar: Er enthält alles, was T0a und T0b an Verträgen umsetzen und worauf T9 sich verlässt.

**Keine Migration (nach Sol-Runde 2 erneut geprüft).** Jedes neue Feld und jede neue Prüfung leitet sich aus vorhandenen
Spalten ab: `Wishes.SlotId`, `Wishes.SlotAddedAtUtc`, `Wishes.Alias`, `EmoteSlots.AddedAt`/`Name`/`IsArchived`/`SeedUnsettled`,
`Emotes.ImageUrl`, `UsageStats`, `Channels.ActiveEmoteSetId`, die Einstellungszeile. Kein Entity und kein Index ändert sich.
Der Fixture-Test auf ausstehende Modelländerungen bleibt grün; schlägt er an, hat ein Task versehentlich das Modell berührt.
Keine neuen `ApiErrorCodes` (Regel 7 bleibt unberührt). **Was ohne Migration bewusst nicht geht:** Weder Wunsch noch Slot
speichern, in welchem 7TV-Set ein Aufenthalt lag. Der Beleg des Sets ist deshalb immer der Vergleich mit dem **jetzt** aktiven
Set des Kanals (siehe unten); eine Meldung, die nach einem Set-Wechsel eintrifft, kann ihr Set nicht mehr belegen und bekommt
409. Ein dauerhaft gespeichertes Set je Wunsch wäre eine neue Spalte, also eine Migration; sie ist **nicht** eingeplant.

**Was der Bestand schon garantiert und worauf die Verträge bauen** (am Stand `135eae07` gelesen):

- Jeder Schreiber der Slot-Inventur sperrt die Kanalzeile in seiner Transaktion: der Vollsync (`SevenTvSyncService.cs:928-942`),
  das Delta (`:738`), die Berichte des Clients. Der Wunschabgleich läuft **danach**, in einer eigenen Transaktion
  (`TriggerWishReconcileAsync`, `:224`, `:314`). Zwischen beiden Commits steht die neue Slot-Lage schon in der Datenbank,
  während der Wunsch noch an der alten Generation hängt.
- Sperrreihenfolge überall: Kanalzeile vor Wunsch-Zeilen (K1 (h)).
- Eine Slot-Zeile kann nach Entfernen und Wieder-Hinzufügen mit neuem `AddedAt` wiederbelebt werden; Slot-ID und Alias
  benennen den Aufenthalt nicht, erst das Tripel aus Slot-ID, Alias und `AddedAt` (die **Generation**).
- Ein `removed`-Wunsch behält `SlotId` und `SlotAddedAtUtc` (erst `re-add` leert die Generation); ein `removed_externally`
  ebenso. Ein bestätigtes (`AddedAtFromRest`) Datum ändert sich für denselben Aufenthalt nicht mehr.

#### Lesefelder (T0a)

- **Slot-Generation am DTO.** `slotAddedAtUtc` ist `Wishes.SlotAddedAtUtc`: die Generation, an die der Wunsch serverseitig
  gebunden ist; `null` ohne Slot oder bei unbekannter Generation (übernommener, undatierter Eintrag). „Bestätigt“ ist die
  Generation genau dann, wenn `startsAtAuthoritative` wahr ist: Der Abgleich zieht dieses Merkmal bei jedem Halten der
  Generation dem `AddedAtFromRest` des Slots nach (`HoldGeneration`, K1 (l)), `adopt` ebenso (`ResolveStart`). T0a belegt das
  mit einem Test über die Wege Verknüpfen, Rückkehr, Datumskorrektur, Übernehmen; findet er einen Weg, auf dem beides
  auseinanderläuft, bekommt das DTO stattdessen ein eigenes Merkmal (Rückmeldung an den Orchestrator, kein stilles Abweichen).
- **Bild am DTO.** `imageUrl` ist das gespeicherte Bild der Kanalzeile desselben 7TV-Emotes (`Emotes.ImageUrl`; die
  `_static`-Form heißt „animiert, dies ist das Standbild“, die Form ohne den Marker heißt „bestätigt unbewegt“); `null`, wenn
  der Kanal das Emote nie im Set gesehen hat oder die Zeile kein Bild trägt. Gilt für Listen, Detail, Befehlsantworten und
  `mine`; je Antwort **eine** zusätzliche Abfrage über die vorkommenden Emote-IDs, nie eine je Zeile.
- **Tagesreihe.** `usage.daily` ist die Liste `{ date, useCount }` für **jeden** UTC-Tag vom Tag des Fensterbeginns bis zum
  Bewertungstag, in Reihenfolge, Tage ohne Nutzung mit 0; dieselben Slots, dasselbe `UseCount`, dieselbe Abfrage wie `total`.
  Vertrag: Die Summe der Reihe ist `total`. Höchstens acht Einträge.
- **Übernehmbare Einträge.** `GET …/wishes/slots` liefert die Einträge, die `adopt` der Lage nach annähme: Slots des Kanals,
  nicht archiviert, kein ungesetzelter Seed. **Ein** gemeinsames Prädikat für diesen Leseweg, für `adopt` und für die
  `slotIds` des 409. Je Eintrag: `slotId`, `sevenTvEmoteId`, `alias`, `addedAtUtc` (`null` = undatiert),
  `addedAtAuthoritative`, `imageUrl`, dazu die Konfliktlage, wie `AdoptAsync` sie entscheiden würde:
  `heldByWishId` (der Wunsch, der **diesen** Slot hält: `active`, `presented`, `kept`; sonst `null`), `runningWishId` (ein
  laufender Wunsch desselben Emotes, der diesen Slot nicht hält; sonst `null`) und `runningWishAdoptable` (wahr genau dann,
  wenn `WishTransitions` für diesen laufenden Wunsch die Aktion „Übernehmen mit `wishId`“ erlaubt: `approved`, `failed`,
  `presented` mit `unconfirmed` oder `already_in_set`, ab K6 `pending`). Optionaler Filter `sevenTvEmoteId`. Unpaginiert,
  sortiert nach Alias. `InteractiveRead`, dieselbe Filterkette wie die Gruppe.
- **Aktives Set am Cache vorbei (Sol-R3-P1).** `GET /api/seventv/channels/{c}/emote-sets` bekommt den Parameter `refresh`,
  nach dem Vorbild der Schwester-Route `…/emotes`: Mit `refresh=true` umgeht der Abruf den 60-Sekunden-Positiv-Cache und
  fragt 7TV; Drosselung, Negativ-Cache und Fehlerabbildung der Härtung bleiben. Ohne den Parameter ändert sich nichts. Die
  Route bleibt in ihrer Rate-Limit-Richtlinie; ist das Budget erschöpft, antwortet sie wie heute mit 429, und der Client
  sendet dann nichts (2.5).
- **`canEdit`** im `GET` der Einstellungen: das Ergebnis derselben Entscheidung, die `WishSettingsAuthorizationFilter` für
  `PUT` trifft (`CanChangeSettingsAsync`); keine zweite Regel.

#### Schreibverträge (T0b)

- **Konflikt-IDs.** 409 `wish_slot_already_linked` trägt `wishId` des Halters (auch eines `kept`). 409
  `wish_emote_already_in_set` trägt zusätzlich `wishId` des laufenden Wunsches desselben Emotes oder `null`. 409
  `wish_already_running` trägt wie bisher `wishId`. Der Vorrang der Prüfungen bleibt, wie er ist.
- **`adopt`, an die gewählte Generation gebunden (Sol-R2-P2-4).** Der Rumpf nennt zusätzlich, was der Verwalter gesehen und
  gewählt hat: `expectedAlias` (Pflicht) und `expectedAddedAtUtc` (das `addedAtUtc` des Eintrags aus `GET …/slots`,
  wortgleich zurückgegeben; `null` oder fehlend heißt „ich habe einen **undatierten** Eintrag gewählt“). Unter der
  Kanalsperre, nach dem Laden des Slots und vor jeder anderen Prüfung: Weicht der Alias des Slots ab, oder weicht sein
  `AddedAt` als Zeitpunkt (Genauigkeit der Datenbank) vom erwarteten ab, oder ist eines von beiden `null` und das andere
  nicht, antwortet die Route 404 `wish_slot_not_found` und schreibt nichts: Der gewählte Eintrag existiert so nicht mehr.
  **Undatierter Slot:** übernehmbar, solange er beim Schreiben immer noch undatiert ist (der Wunsch bekommt dann wie bisher
  keine Generation und einen vorläufigen Start); hat er inzwischen ein Datum bekommen, ist das eine Abweichung (404, neu
  wählen). Fehlt `expectedAlias`: 400 `invalid_request_body`.
- **`complete-removal`: gebunden an Slot, Generation und Set, mit Blick in die Slot-Inventur, idempotent (Sol-P1-2,
  Sol-R2-P1-2).** Der Rumpf nennt, was der Lauf des Clients entfernt hat: `slotId`, `slotAddedAtUtc` (wortgleich aus dem DTO;
  verglichen wird der Zeitpunkt in der Genauigkeit der Datenbank) und `emoteSetId` (das Set, gegen das der Lauf geschrieben
  hat). **Sperren:** erst die Kanalzeile (`RowLocks.LockChannelsByIdsAsync`), dann die Wunsch-Zeile; gelesen wird erst
  dahinter. Damit steht die Meldung in einer Reihe mit jedem Slot-Schreiber: Sie sieht die Inventur entweder vor oder nach
  einem Sync, nie mittendrin. Die Route läuft deshalb **nicht** mehr über den gemeinsamen Helfer der reinen Wunsch-Befehle.
  Entscheidung, erste zutreffende Zeile gewinnt:

  | # | Lage (unter beiden Sperren gelesen) | Antwort | Wirkung |
  |---|---|---|---|
  | 1 | Rumpf fehlt oder eines der drei Felder fehlt | 400 `invalid_request_body` | keine |
  | 2 | Wunsch existiert nicht oder gehört nicht zum Kanal | 404 `wish_not_found` | keine |
  | 3 | **Set nicht belegt:** `emoteSetId` ist nicht das aktive Set des Kanals (`Channels.ActiveEmoteSetId`, auch: der Kanal hat keines) | 409 `wish_state_conflict` | keine |
  | 4 | **Generation weicht ab:** `slotId` oder `slotAddedAtUtc` des Rumpfs ≠ `SlotId`/`SlotAddedAtUtc` des Wunsches (auch: neu gebunden, per `re-add` zurückgesetzt, keine Generation) | 409 `wish_state_conflict` | keine |
  | 5 | Zustand ist schon `removed` | **200 mit dem Wunsch** | keine: kein Schreiben, kein Audit, kein Ereignis, `DecisionVersion` unverändert |
  | 6 | **Ersatz in der Inventur sichtbar** (Definition unten), Zustand `active`, `presented` oder `removed_externally` | 409 `wish_state_conflict` | keine; der ausstehende Wunschabgleich bindet den Wunsch an den Ersatz |
  | 7 | Zustand `removed_externally`, binnen 6 Stunden nach `EndedAtUtc` | 200 | `removed`; **`EndedAtUtc` bleibt**; Audit `wish.remove`, ein Ereignis |
  | 8 | Zustand `removed_externally`, außerhalb der 6 Stunden | 409 `wish_state_conflict` | keine (wie bisher) |
  | 9 | Zustand `active` oder `presented` | 200 | `removed`; `EndedAtUtc` = jetzt; Audit, Ereignis (wie bisher) |
  | 10 | jeder andere Zustand (`kept`, `closed`, `approved`, …) | 409 `wish_state_conflict` | keine |

  **„Ersatz sichtbar“ (Zeile 6, nach Sol-R3-P1 gefasst)** heißt: Die Inventur zeigt einen Eintrag, an den der Wunschabgleich
  den Wunsch führen würde oder schon geführt hätte. Unter den Slots des Kanals mit derselben 7TV-Emote-ID, nicht archiviert,
  kein ungesetzelter Seed, gibt es (a) die Slot-Zeile des Wunsches selbst mit einem `AddedAt`, das **nicht** die gemeldete
  Generation ist (wiederbelebte Zeile), oder (b) **irgendeine andere** Slot-Zeile mit demselben Alias wie der Wunsch, die
  kein anderer Wunsch hält, **gleich ob ihr Datum vor oder nach der gemeldeten Generation liegt oder fehlt**. Die zweite
  Hälfte ist bewusst weiter als das Fenster des Abgleichs (30 Minuten zurück, 6 Stunden vor): Die Meldung kennt das Ende des
  Aufenthalts noch nicht, und ein 409 zu viel bucht nichts falsch (der Abgleich entscheidet dann). Die eigene Slot-Zeile mit
  unveränderter Generation ist kein Ersatz; ein Eintrag unter einem **anderen** Alias auch nicht (E13).

  **Womit das Set belegt wird (Zeile 3):** allein mit der Gleichheit zum **aktuell** aktiven Set unter der Kanalsperre. Slots
  sind Einträge des aktiven Sets. Eine Meldung, deren Set nicht mehr das aktive der Kanalzeile ist, hat keinen Beleg und
  bekommt 409, **auch** die Wiederholung einer schon gebuchten
  Meldung (Zeile 3 steht vor Zeile 5). Folge: Der Wunsch bleibt, wie der Abgleich ihn führt (`removed_externally` bzw. schon
  `removed`); nichts wird falsch gebucht. Die erste erfolgreiche Buchung hält `slotId`, `slotAddedAtUtc` und `emoteSetId`
  zusätzlich in den Details ihres `wish.remove`-Eintrags fest (IDs und ein Zeitpunkt, keine Namen, AK19 bleibt gewahrt), als
  Papierspur, nicht als Prüfgrundlage; dazu, falls der Client es mitgibt, `evidence` (`acknowledged` = 7TV hat den REMOVE
  bestätigt, `absent` = erst das Nachlesen hat das Fehlen festgestellt; siehe R20). **Grenze dieses Belegs (R19):** Der Sync
  hält einen Set-Wechsel in einer eigenen Transaktion fest, bevor er die Kanalzeile für die Inventur sperrt, und er gleicht
  beim Wechsel nach Datum und Alias ab, statt pauschal zu archivieren. In dem kurzen Fenster zwischen beiden Schritten zeigt
  die Kanalzeile noch das alte Set; eine Meldung für das alte Set wird dann angenommen.

  **Die Lagen, die der Vertrag entscheidet:**

  | Lage | Ergebnis |
  |---|---|
  | Meldung vor `sync-deleted` und vor jedem Sync | Zeile 9: `removed` |
  | Meldung nach `sync-deleted` bzw. nach dem Sync, Abgleich hat schon `removed_externally` gebucht | Zeile 7: `removed`, Datum bleibt |
  | Antwort 200 ging verloren, Client wiederholt | Zeile 5: 200 ohne Wirkung, weiterhin ein Audit-Eintrag |
  | Ersatz wurde angelegt, Sync hat ihn geschrieben, **Wunschabgleich steht noch aus** (der Fall aus Sol-R2-P1-2) | Zeile 6: 409; der Abgleich führt den Wunsch danach an den Ersatz |
  | Ein **älterer** Eintrag desselben Emotes wurde auf den Alias des Wunsches umbenannt, der Sync hat es geschrieben, Abgleich steht aus (Sol-R3-P1) | Zeile 6: 409 |
  | Ersatz angelegt, Abgleich hat den Wunsch schon neu gebunden | Zeile 4: 409 |
  | Der Lauf des Clients lief gegen ein anderes Set, oder das Set hat seither gewechselt | Zeile 3: 409 |
  | Wiederholung nach `re-add` | Zeile 4: 409 |
  | Ersatz existiert in 7TV, aber **noch kein Sync** hat ihn geschrieben | Zeile 9 bzw. 7: `removed`. Bewusst so: Für die Datenbank ist der Ersatz ein Hinzufügen nach der Entfernung; ein `removed`-Wunsch nimmt einen späteren Eintrag nie auf (wie bei jedem Entfernen über Emote Purge). Restrisiko R15 |

  Folgen für den Client (T9): 200 heißt gebucht, gleich beim wievielten Versuch. 409 und 404 heißen „diese Meldung bucht
  nichts und soll nichts buchen“; sie sind endgültig und werden nicht wiederholt, die Liste zeigt danach den Stand des Servers.
- **`EndedAtUtc` allgemein:** Ein Befehl, der von einem Endzustand in einen Endzustand führt, lässt `EndedAtUtc` stehen (in
  K1 bis K3 ist das nur Zeile 7). **Wirkung auf die Retention:** Die 30-Tage-Frist der Einlöserdaten und die 365-Tage-Löschung
  rechnen damit ab dem tatsächlichen Verlassen des Sets statt ab der bis zu 6 Stunden späteren Meldung; die Felder werden also
  nie später, höchstens einen Tageslauf früher geleert. Der Rückweg (`Return`) ist nicht berührt (er liest `EndedAtUtc` nur an
  `removed_externally`). Die Pilot-Messung liest das Audit, nicht `EndedAtUtc`.
- **Einstellungen.** `PUT` mit fehlender Schwelle behält den gespeicherten Wert und löst **keine** Entscheidung nach E25 aus
  (kein Sperren der aktiven Wünsche, keine `DecisionVersion`); ausdrücklich `null` schaltet aus, ein Wert setzt, beides wie
  bisher mit E25. Wie das Binding „fehlt“ von `null` unterscheidet, wählt der Implementer; die drei Fälle stehen als Test an
  der echten Route.

### 2.2 Abgeleitete Begriffe (eine reine Datei, T2; nirgends sonst nachgebaut)

| Begriff | Regel |
|---|---|
| Handlungsbedarf | `status` ∈ {`pending`, `failed`, `presented`}, dazu `approved` bei `mode = manual` (deckungsgleich mit `StatusesOf(Open)` des Servers) |
| Probezeit | `status = active` **und** `probationStartsAtUtc` vorhanden **und** jetzt ≥ `probationStartsAtUtc` (AK36: nur aus dem Feld, nie aus Laufzeit und Vorlauf nachgerechnet) |
| fällig | `presented` (jeder Grund), oder `active` mit `expiresAtUtc` ≤ jetzt (der Lauf hat noch nicht reagiert) |
| Restzeit | aus `expiresAtUtc` gegen „jetzt“: volle Tage ab 1 Tag, darunter Stunden; nach Ablauf „seit N Tagen abgelaufen“ (unter einem Tag: „heute abgelaufen“). Pluralformen über `pluralKey` |
| Zeit im Set | aus `startsAtUtc`; bei `startsAtAuthoritative = false` mit dem Zusatz „vorläufig“ |
| Ende | `endedAtUtc` als Datum; seit T0b der Zeitpunkt, zu dem der Eintrag das Set verließ bzw. die Entscheidung fiel |
| Entfernen möglich | `slotId`, `slotAddedAtUtc` vorhanden **und** `startsAtAuthoritative` wahr (2.6) |

„Jetzt“ kommt aus einem Signal, das die Seite minütlich fortschreibt (kein `new Date()` in Vorlagen, kein `computed()` über
eine Uhr ohne Signal, Regel 14). Die reinen Funktionen nehmen „jetzt“ als Parameter.

### 2.3 Aktionstabelle (Zustand → Aktionen; AK35, als Tabelle getestet)

Eine reine Funktion (T2) liefert zu `status`, `statusReason`, `slotId`, `expiresAtUtc` und `mode` die geordnete Aktionsliste.
Die Zeile rendert nur, was die Funktion nennt. Reihenfolge je Zeile: aufbauende Aktionen, dann mit Abstand die zerstörende
(§8.7). 7TV-Aktionen sind mit ★ markiert; sie entfallen auf einem groben Zeiger ersatzlos (2.7).

| Zustand | Aktionen in K3 | erst K5/K6 |
|---|---|---|
| `pending` | keine (neutrale Zeile) | Freigeben, Ablehnen (K6) |
| `approved` | ★ In 7TV hinzufügen · Mit Eintrag im Set verknüpfen (`adopt` mit `wishId`) · Bearbeiten (PATCH) · Schließen | – |
| `unconfirmed` | keine (neutrale Zeile) | – |
| `failed` | Erneut versuchen · Mit Eintrag verknüpfen · Bearbeiten · Schließen | Grundtexte (K5) |
| `active` | Verlängern (nur mit `expiresAtUtc`) · Behalten · ★ Entfernen (nur mit `slotId`) | Zuordnung lösen (K6) |
| `presented` | Verlängern (nur mit `expiresAtUtc`) · Behalten · ★ Entfernen (nur mit `slotId`) · Schließen (nur **ohne** `slotId`) · Mit Eintrag verknüpfen (nur Grund `unconfirmed` oder `already_in_set`) | Erneut prüfen, Bot entfernen lassen (K5) |
| `removed`, `removed_externally` | Wieder hinzufügen (Dialog: befristet mit Laufzeit, oder dauerhaft) | – |
| `kept`, `closed`, `rejected`, `expired`, `merged` | keine | – |
| unbekannter Zustand | keine, neutrale Zeile | – |

„Erneut prüfen“ wird in K3 **nicht** angeboten (Befund 26). Ein unbekannter `statusReason` ändert die Aktionsliste nicht, er
rendert nur die neutrale Grundzeile (`wishes.reason.other`). „Entfernen“ steht in der Liste, sobald ein `slotId` da ist; ob es
bedienbar ist, entscheidet 2.6 (gesperrt mit Grund, nicht ausgeblendet).

### 2.4 Laden, Gültigkeit, Live, Aufholen, Zähler

- **Kanal-Seite:** drei Listen (`open`, `running`, `ended`), je eigene Seite 20. `open` und `running` laden beim Betreten;
  `ended` lädt erst, wenn der Abschnitt geöffnet wird **[E]**. `wishChanged` kommt in `LIVE_EVENT_TYPES`. Kein eigener
  `EventSource`; Neuladen über `liveReload` aus `core/live/live-reload.ts` auf `channelLiveUrl`.
- **Gültigkeit einer Antwort (Sol-P2-8, verbindlich):** Jeder Abruf einer Liste gehört zu einem Tripel aus Kanal, Ansicht und
  Seite und zu einer Abrufnummer je Ansicht, die **beim Auslösen** vergeben wird. Eine Antwort wird nur übernommen, wenn der
  Kanal noch der der Seite ist, die Ansicht noch dieselbe Seite zeigt und kein jüngerer Abruf derselben Ansicht ausgelöst
  wurde. Eine verworfene Antwort ändert weder Daten noch Fehlerzustand. `rxResource.value()` wirft im Fehlerfall; überall
  `hasValue()` prüfen.
- **Auslöser eines Neuladens der Listen:** (a) `wish.changed` (entprellt, ein Schwall → ein Abruf je geladener Ansicht); (b)
  `channel.synced` (der Abgleich läuft nach jedem Sync; ein dabei verlorenes `wish.changed` wird so aufgeholt); (c) **der
  Stream dieses Kanals** wird nach einer Unterbrechung wieder geöffnet; (d) das Fenster wird wieder sichtbar; (e) ein
  Sicherheitstakt alle 5 Minuten bei sichtbarem Fenster (`pollWhileVisible`); (f) ein eigener Befehl (sofort; das eigene
  `wish.changed` löst danach höchstens einen weiteren Abruf aus). Kein optimistisches Umsortieren.
- **Wiederverbindung je Kanal-Stream (Sol-R2-P2-6):** `LiveUpdateService.status` ist dienstweit („der letzte Schreiber
  gewinnt“) und taugt nicht als Signal. T4 ergänzt den Live-Dienst um eine Meldung **je Stream-URL**, die genau dann kommt,
  wenn die Verbindung dieser URL nach einer vorigen Verbindung erneut geöffnet wurde (das erste Öffnen meldet nichts). Die
  dienstweite Statusanzeige bleibt, wie sie ist. Zwei Kanal-Seiten in zwei Streams holen unabhängig voneinander auf.
- **Nutzung je Zeile, mit Ablaufzeit (Sol-R2-P2-6):** ein Abruf `GET /{id}` (die Reihe steckt in `usage.daily`), erst wenn
  die Zeile in den sichtbaren Bereich kommt, nur in `open` und `running`. Der Wert gilt höchstens **5 Minuten** und nur für
  den Schlüssel (`id`, `status`, `expiresAtUtc`, `slotId`, `slotAddedAtUtc`), mit dem er geladen wurde. Neu geladen wird eine
  **sichtbare** Zeile, wenn sich ihr Schlüssel ändert, wenn ihr Wert abgelaufen ist (geprüft beim Sicherheitstakt und beim
  Sichtbarwerden von Zeile oder Fenster) und nach den Auslösern (c) und (d), die alle gehaltenen Werte sofort verfallen
  lassen. Nutzung, Rang und Lückenmerkmal hängen an der Uhr und an neuen Zählungen, nicht am Schlüssel; die Ablaufzeit ist
  deshalb Teil des Vertrags, nicht Optimierung. Auch hier gilt „nur der jüngste Abruf je Zeile zählt“. `usage.flushed` löst
  bewusst nichts aus (alle 30 Sekunden je Kanal wäre ein Abruf je Zeile).
- **Sprungziel für eine Zeile, die nicht geladen ist (Sol-R2-P2-5):** Die Kanal-Seite liest den Query-Parameter `wish`
  (Wunsch-ID). Ist der Wunsch in einer geladenen Ansicht, bekommt seine Zeile den Fokus. Sonst lädt die Seite ihn mit
  `GET /{id}` und zeigt ihn als einzelne, **angeheftete** Zeile über den Abschnitten („Gesuchter Wunsch“), mit seinen
  normalen Aktionen und dem Fokus; 404 → Hinweis „existiert nicht mehr“. Die angeheftete Zeile folgt denselben Neulade- und
  Ablaufregeln und verschwindet, wenn der Parameter entfällt oder der Wunsch in einer geladenen Ansicht auftaucht. Jeder
  „Zum Wunsch“-Sprung der Dialoge und jede Zeile von `my-wishes` nutzt dieses Ziel.
- **Zähler:** `WishSummaryService` (root) fragt `summary` beim ersten Bedarf und danach alle 5 Minuten über
  `pollWhileVisible`/`pageVisible`. **Nur mit geladenem, eingeloggtem Nutzer** (Befund 25). Antwort 404 `wishes_disabled` →
  Merkmal „nicht verfügbar“, **Abfragen enden für diese Sitzung** **[E]**. Anderer Fehler: letzter Wert bleibt, vor dem ersten
  Erfolg gilt „nicht verfügbar“. Kein Stream, kein `/api/channels/mine`. Die Wunsch-Seiten dürfen nach einem eigenen Befehl
  und nach einem Neuladen `refresh()` rufen (eine zusätzliche Anfrage, entprellt) **[E]**.
- **`my-wishes`:** lädt `mine` beim Betreten und im selben 5-Minuten-Takt bei sichtbarem Fenster; kein Stream (Spec E18).
  Auch hier gilt „nur der jüngste Abruf zählt“.

### 2.5 Startvertrag für jeden 7TV-Vorgang aus einer Wunsch-Zeile (T8, T9; Sol-P2-3, Sol-R2-P2-3, verbindlich)

Ein Klick auf „In 7TV hinzufügen“ oder „Entfernen“ durchläuft immer diese Phasen. Zwischen Phase 1 und Phase 3 liegen Dialoge
und Netzwerk; deshalb zählt nur, was Phase 3 feststellt, und Phase 3 trennt streng zwischen Lesen und Entscheiden.

**Der autoritative Read für das aktive Set** ist der Weg, der 7TVs eigenes `style.activeEmoteSetId` des Kanalkontos liefert:
`SevenTvEmoteSetService.listForeignChannelEmoteSets` (`GET /api/seventv/channels/{c}/emote-sets`), **immer mit
`refresh=true`** (T0a), also am 60-Sekunden-Cache vorbei. **Nicht** `GET …/emotes/active-set`: Das ist der Stand des letzten
Postgres-Syncs. Der Read ist budgetiert (Rate-Limit der Route): Ein Wunsch-Vorgang braucht einen Read beim Klick, einen in
Phase 3a und einen vor jeder Wiederholung; antwortet die Route mit 429 oder scheitert sie, gilt „nicht belegt“, und es wird
nichts gesendet.

1. **Einfrieren beim Klick:** Kanal der Seite, aktives Set des Kanals (aus dem autoritativen Read; ist er nicht verfügbar:
   kein Start, mit Grund; nennt er ein **anderes** Set als die Kanalzeile laut `…/emotes/active-set`, hat der Kanal in 7TV
   gewechselt und der Sync steht aus: kein Start, „Set hat in 7TV gewechselt, nach dem nächsten Sync erneut“, denn die Slots
   der Wünsche gehören dann nicht zum aktiven Set), der Wunsch mit `id`, `status`, `sevenTvEmoteId`, `alias`, und für das Entfernen `slotId` und
   `slotAddedAtUtc`. Gesperrt (kein Start), wenn `arbiter.startLocked()` wahr ist.
2. **Vorbereiten (asynchron):** Token vorhanden, sonst `openSevenTvTokenPromptDialog`; gemeinsamer Vorab-Check
   (`resolveEditableSet` bzw. `resolveDeleteTarget`; „nicht prüfbar“ ist nicht „nicht erlaubt“); beim Entfernen der
   Bestätigungsdialog. Während der Phasen 2 und 3 meldet der startende Dienst dem Arbiter `startCheckPending` (#280), damit
   alle anderen Startknöpfe der App gesperrt sind. Ein Abbruch gibt das Merkmal frei und sendet nichts.
3. **Letzte Prüfung, in zwei Schritten:**
   - **3a, Lesen (asynchron, beide Reads laufen und enden, bevor irgendetwas entschieden wird):** das aktive Set über den
     autoritativen Read; der Wunsch über `GET /{id}`. Scheitert einer, ist das eine Abweichung (fail-closed).
   - **3b, Entscheiden und Übernehmen (ein einziger synchroner Schritt, ohne Warten, ohne weiteren Read, ohne Dialog):**
     in dieser Reihenfolge (i) der Aufrufer (Seite) lebt noch und zeigt denselben Kanal; (ii) das Token ist noch da;
     (iii) der Arbiter ist frei: kein `activeClaim`, und kein **fremdes** `startCheckPending` (ein bereits bestätigter
     Bestandslauf, der gerade seinen eigenen letzten Read macht, hat Vorrang; der Wunsch-Vorgang weicht); (iv) das gelesene
     aktive Set ist das eingefrorene; (v) der gelesene Wunsch ist im erlaubten Zustand und in den eingefrorenen Feldern
     unverändert: fürs Hinzufügen `approved` mit derselben Emote-ID und demselben Alias; fürs Entfernen `active` oder
     `presented` mit demselben `slotId` und demselben `slotAddedAtUtc`, `startsAtAuthoritative` wahr. Die erste Abweichung
     bricht mit ihrem Grund ab, **nichts wird an 7TV gesendet**. Trifft keine zu, startet **im selben synchronen Schritt**
     die Engine bzw. `startDelete`, und der Lauf hält den Arbiter; dazwischen liegt kein `await`, kein Timer, kein Abonnement.
4. **Schreiben:** das Ziel sind ausschließlich die eingefrorenen Werte.

**Vor jedem Senden, auch vor dem ersten und vor jeder Wiederholung nach einem 429** (Sol-R3; die Prüfung hängt am
`beforeStep`-Haken der Engine; Seite und Startanspruch werden nach dem Start nicht erneut geprüft, der Lauf überlebt einen
Seitenwechsel absichtlich):

| Prüfung | Hinzufügen | Entfernen |
|---|---|---|
| aktives Set, autoritativ und mit `refresh`, ist noch das eingefrorene | ja | ja |
| das Token ist vorhanden **und dasselbe**, mit dem der Vorgang bestätigt wurde (beim Klick bzw. nach dem Prompt gemerkt, im Speicher des Vorgangs, nie geloggt) | ja | ja |
| der Wunsch (`GET /{id}`) ist noch `approved`, dieselbe Emote-ID, derselbe Alias | ja | – |
| das Set zeigt unter dem Alias einen Eintrag mit genau der gebundenen Generation (2.6) | – | ja |
| die Beobachtung des Ziel-Eintrags ist beim Senden höchstens 5 Sekunden alt (2.6) | – | ja |

Eine Abweichung, ein gescheiterter oder gedrosselter Read, ein gelöschtes oder gewechseltes Token: Die Zeile wird mit ihrem
Grund übersprungen, **es geht kein (weiterer) Schreibversuch hinaus**. Das gilt auch nach dem Abmelden: `clearToken` leert den
Speicher, die Prüfung sieht es, obwohl die Engine den alten Wert noch hielte.

Die Gründe eines Abbruchs sind eigene, kurze Sätze („Der Wunsch hat sich inzwischen geändert“, „Das aktive Set hat
gewechselt“, „Ein anderer 7TV-Vorgang läuft“, „Die Seite wurde verlassen“), jeweils mit „Es wurde nichts geändert“ und einem
Neuladen der Liste.

### 2.5a 7TV: Hinzufügen (T8)

- Eine exportierte Konstante für die AddEmote-Mutation in `core/seven-tv/` ersetzt die drei Kopien (Befund 24); Bytes der
  Anfrage unverändert, die Bestands-Specs der drei Dienste bleiben ohne Änderung grün.
- Neuer Root-Dienst (Form `WishAddService`, `core/wishes/`), eigene `SevenTvRunEngine`-Instanz, registriert sich beim
  `SevenTvRunArbiter` als neue Laufart `wish` (`isRunning` der Engine, kein `settling`, nie `destructiveOpen`, eigenes
  `startCheckPending`). Ein Lauf hat genau eine Zeile: eingefrorenes Set, Emote-ID und Alias des Wunsches. Ablauf nach 2.5,
  ohne Bestätigungsdialog (Hinzufügen ist nicht zerstörend, der Wunsch ist die Freigabe).
- Ergebnis je Wunsch-ID als Signal, das die Zeile liest und das Navigation überlebt: `läuft` · `hinzugefügt, wartet auf den
  Sync` (bleibt, bis der Wunsch nicht mehr `approved` ist) · `abgelehnt` mit Grund · `unbekannt` (Antwort ging verloren: „der
  nächste Sync zeigt es“, kein automatischer Wiederholversuch) · `nicht gestartet` bzw. `übersprungen` mit dem Grund aus 2.5.
  **Nach Erfolg geht keine Meldung an die Api** (Spec K3).
- Grund einer Ablehnung: nur, was die Engine heute sicher unterscheidet (GQL-Status 409 = Namenskonflikt; abgelaufenes oder
  fehlendes Token; Rate-Limit wartet die Engine selbst ab). Alles Übrige zeigt den Text der Engine. Der Wunsch bleibt `approved`.
- Die neue Laufart braucht ihr Substantiv (`sevenTvRun.kind.wish`, der `Record` in `seven-tv-run-arbiter.ts` erzwingt es) und
  einen Blick auf jede Stelle, die Laufarten aufzählt (`UNSHOWN_RUN_KINDS` der Tag-Seite, `tag-run-actions.ts`, die
  Nutzungsseite): Auf Seiten ohne Wunsch-Dock zählt `wish` zu den nicht gezeigten Läufen.

### 2.6 7TV: Entfernen (T9)

Dieser Abschnitt ist zusammen mit 2.1a und 2.5 für sich lesbar.

- **Entfernen läuft über `SevenTvDeleteService` mit genau einer Eintrags-Zeile [B]** (Befund 19), nach dem Muster von
  `tag-removal-flow.ts`; der Lauf meldet wie jeder Lösch-Lauf `sync-deleted` (Audit `emotes.syncDeleted`) und bietet im Dock
  Protokoll und „Wiederherstellen“.
- **Das Ziel kommt allein aus dem Wunsch [B] (Sol-P1-1):** `SevenTvEntryTarget` = `slotId`, `alias` und `slotAddedAtUtc` des
  eingefrorenen Wunsches, das Datum **unverändert als Zeichenkette durchgereicht**; das Set ist das eingefrorene aktive Set.
  Keine zweite Quelle für Alias oder Datum. Voraussetzungen, sonst gesperrt mit Grund: `slotId` und `slotAddedAtUtc` da,
  `startsAtAuthoritative` wahr („Hinzufügedatum noch nicht von 7TV bestätigt, nach dem nächsten Sync möglich“), das Datum
  besteht `isValidEntryGeneration`.
- **Die Bestätigung nennt, was gebunden ist:** Alias, Hinzufügedatum der Generation, Set; Auslöser `danger-quiet` (kehrt je
  Zeile wieder), Ausführen `danger-solid`, mit der Warnung für geteilte Sets wie im Lösch-Dialog.
- **Vor jedem Sendeversuch (Sol-R2-P1-1, Sol-R3, [B]):** Der Lösch-Lauf bindet eine Eintrags-Zeile vor jedem Versuch gegen
  einen Read des Sets (`bindBeforeRemove`), darf dabei aber einen bis zu 5 Sekunden alten Read wiederverwenden
  (`ENTRY_READ_REUSE_MS`). Für eine Wunsch-Löschung trägt die Zeile das Merkmal „Wunsch-Bindung“, und der Dienst prüft dann
  **vor jedem** Versuch, auch vor der Wiederholung nach einem 429, in dieser Reihenfolge; die erste Abweichung überspringt
  die Zeile ohne Senden:
  1. **Token:** vorhanden und dasselbe wie beim Bestätigen (2.5).
  2. **Aktives Set:** autoritativ mit `refresh` gelesen, noch das eingefrorene. Hat der Kanal in der Wartezeit auf B
     gewechselt, wird nichts gesendet, auch wenn der Eintrag in A noch liegt.
  3. **Generation:** ein frischer Read des Sets (nie ein wiederverwendeter) zeigt unter dem Alias einen Eintrag mit genau
     dem gebundenen Zeitpunkt (`entryTimeKey`, auf die Millisekunde).
  4. **Alter der Beobachtung:** Gemessen wird ab dem Absenden der Anfrage, die die Seite mit dem Ziel-Eintrag geliefert hat.
     Ist diese Beobachtung beim Senden älter als **5 Sekunden** (ein mehrseitiger Read verifiziert die Seiten 1 bis n−1, nie
     die letzte, und darf bis zu 20 Sekunden dauern), liest der Dienst **einmal** neu und prüft Punkt 3 erneut; ist auch die
     neue Beobachtung beim Senden zu alt, wird die Zeile ausgelassen („7TV antwortet zu langsam, es wurde nichts entfernt“).
     Das gilt für jede Seite gleich, also auch für einen Eintrag auf der nie verifizierten letzten Seite.
  Ohne das Merkmal (Nutzungsseite, Tag-Seite) bleibt jede Anfrage und jeder Read Byte für Byte, wie er ist. Liegt nach einem
  429 unter demselben Alias eine **neue** Generation, endet die Zeile ohne zweiten REMOVE (AK37).
- **Restlücke, bewusst hingenommen [B] (R16), ehrlich bemessen:** 7TV kennt kein bedingtes Entfernen; die Mutation nimmt
  Set, Emote-ID und Alias (`REMOVE_SLOT_ENTRY_MUTATION`), keine Generation. Zwischen der Beobachtung des Ziels und dem
  Eintreffen des REMOVE bei 7TV liegen **bis zu 5 Sekunden plus der Roundtrip der Mutation**, nicht ein einzelner Roundtrip.
  Entfernt in diesem Fenster ein anderer Editor den Eintrag und legt ihn unter demselben Alias neu an, trifft der REMOVE die
  neue Generation; ebenso bleibt ein Set-Wechsel in den Sekunden nach dem Set-Read unbemerkt. Clientseitig ist das nicht
  schließbar. **Was danach sichtbar ist und was nicht:** `complete-removal` bucht die gemeldete alte Generation, nie die
  neue. Der Sync bemerkt den Verlust des Ersatzes nur, wenn er ihn vorher gesehen hat; einen Ersatz, der zwischen zwei Syncs
  entstand und vom REMOVE getroffen wurde, hat Emote Purge nie gekannt und kann sein Fehlen niemandem zeigen.
- **Abschlussvertrag je Lauf (Sol-P2-7, verbindlich):** `SevenTvDeleteService.startDelete` gibt künftig zurück, ob der Start
  angenommen wurde, und wenn ja mit welcher Laufkennung; und der Dienst bietet **je Laufkennung** genau eine Abschlussmeldung
  mit dem endgültigen Ergebnis der Zeilen (nach dem Nachlesen unbekannter Ausgänge), unabhängig davon, welcher Lauf gerade im
  Dock gezeigt wird und ob eine Seite noch lebt. Die bestehenden Aufrufer ignorieren den Rückgabewert; ihr Verhalten und ihre
  Specs bleiben unverändert. **Das endgültige Ergebnis der Zeile entscheidet, nicht der Weg dorthin:** Ein abgebrochener Lauf,
  dessen REMOVE schon unterwegs war und beim Nachlesen als entfernt bestätigt wird, ist `done` und wird gemeldet; ein
  abgewiesener Start hat keine Kennung und wird nie verfolgt.
- **Erfolgsmeldung:** Ein Root-Dienst (Form `WishRemovalTracker`, `core/wishes/`) hält je angenommener Laufkennung Kanal,
  Wunsch-ID und das eingefrorene Tripel (`slotId`, `slotAddedAtUtc`, Set). Abschlussmeldung mit Zeile `done` → **genau ein**
  `complete-removal` mit diesem Tripel; jeder andere Ausgang (übersprungen, gescheitert, abgebrochen ohne Bestätigung) → keines,
  die Zeile zeigt den Grund. Antworten nach 2.1a: 200 → fertig; 409 oder 404 → **endgültig, nicht gebucht**: Der Eintrag ist
  aus 7TV entfernt, aber der Wunsch hängt inzwischen woanders, ein Ersatz liegt schon im Set, das Set hat gewechselt oder der
  Wunsch ist beendet; kein erneutes Melden, Liste neu laden, ein kurzer Hinweis an der Seite („Entfernt in 7TV. Der Wunsch
  wurde inzwischen anders gebucht.“). Netzfehler, 5xx oder unbekannter Ausgang (`isUnknownOutcome`) → Zeile zeigt „Meldung
  ausstehend“ mit „Erneut melden“; die Wiederholung ist gefahrlos (Zeile 5 der Tabelle in 2.1a). Geht die Meldung nie hinaus,
  bucht der Abgleich `removed_externally`: fachlich richtig, nur ohne `wish.remove`.
- Die Kanal-Seite hängt für den Lauf `app-delete-progress-section` in ein seiteneigenes Dock (Muster Tag-Seite, nur bei
  feinem Zeiger, mit `DockClearanceService`). Ein per „Wiederherstellen“ zurückgeholtes Emote lässt den Wunsch `removed`; der
  Verwalter nimmt „Wieder hinzufügen“.

### 2.7 Sperren und Zeiger

- **Grober Zeiger [B] (F2):** ★-Aktionen werden nicht gerendert (keine Handler, kein ARIA), das Dock auch nicht. Damit ein
  fälliger Wunsch auf dem Handy nicht ins Leere zeigt, trägt die Zeile dort einen Link zur 7TV-Seite des Emotes
  (`emotePageUrl`) mit dem Satz, dass ein Hinzufügen oder Entfernen direkt in 7TV beim nächsten Sync erkannt wird. Derselbe
  Satz steht bei `approved` auch am Rechner als Nebenzeile (der Hauptweg der Mods ist 7TV selbst, Spec 3).
- **Laufender oder startender 7TV-Vorgang:** Solange `arbiter.startLocked()` wahr ist, sind alle ★-Knöpfe `aria-disabled`
  (nicht `disabled`, damit sie fokussierbar bleiben) und verweisen per `aria-describedby` auf **eine** Statuszeile der Seite,
  die den blockierenden Vorgang beim Namen nennt (`sevenTvRun.kind.*`). Kein Hinweistext je Knopf **[E]** (Befund 18: erfüllt
  AK38, hält §4.2 ein; T15 präzisiert §4.2). Ein Klick auf einen gesperrten Knopf startet nichts.
- **Buchungs-Aktionen** hängen nicht am Arbiter. Während die eigene Anfrage läuft, ist der Knopf `disabled` (§6.1), die
  übrigen der Zeile auch.
- **Bestätigung:** Behalten und Schließen beenden einen Wunsch endgültig und bekommen einen `ConfirmDialog` **[E]**;
  Verlängern, Erneut versuchen nicht.

### 2.8 Die vier Auffälligkeiten aus T12, Stand nach Revision 2

a) **`wish_emote_already_in_set` trotz laufendem Wunsch.** Der Vorrang im Backend bleibt (Absicht); **neu [B]:** Das 409
   nennt neben den `slotIds` die `wishId` des laufenden Wunsches (oder `null`), und `wish_slot_already_linked` nennt den
   Halter (T0b, Sol-P2-5). Der Dialog sucht deshalb **nicht** in geladenen Listen. Drei Ausgänge: (1) `wishId` ist `null` →
   „Bestand übernehmen“ mit den gemeldeten Einträgen (AK34); (2) `wishId` da und dieser Wunsch hält einen Slot (`active`,
   `presented`; nach `GET /{id}`) → Hinweis „läuft bereits“ mit „Verlängern“ (nur mit Ablauf) und Sprung zur Zeile, kein
   Übernehmen; (3) `wishId` da, Wunsch ohne Slot (`approved`, `failed`, `presented` mit `unconfirmed`/`already_in_set`) →
   „Bestand übernehmen“ **mit dieser `wishId`**. Hält ein `kept`-Wunsch den Eintrag (das zeigt `heldByWishId` der
   Eintragsliste, sonst das 409 des `adopt`), sagt der Dialog „ist bereits als behaltener Wunsch geführt“ ohne „Verlängern“.
   Dieselbe Unterscheidung trifft der Übernehmen-Dialog für jeden Eintrag aus `runningWishId` und `runningWishAdoptable`
   (T6: frei, gezielt verknüpfen, gesperrt); jeder „Zum Wunsch“-Sprung nutzt das Sprungziel aus 2.4, auch wenn die Zeile
   nicht geladen ist; ein veralteter Konfliktstand führt immer zu Neuladen und erneuter Auswahl (T6).
   `wish_already_running` beim Handeintrag (Emote nicht im Set): Hinweis mit „Verlängern“ bzw. bei `approved` „Bearbeiten“.
b) **Kein Alias und kein Name [E].** Der Dialog kann diesen Rumpf nicht erzeugen: Der 7TV-Read liefert den Standardnamen und
   belegt das Alias-Feld vor; das Alias ist Pflichtfeld (1 bis 100 Codepunkte, keine Leer- oder Steuerzeichen). Ist 7TV nicht
   erreichbar, bleibt das Alias von Hand einzutragen, `emoteName` wird dann weggelassen. **Neu (Sol-P2-10):** Vorbelegung und
   Vorschau hängen an der Emote-ID, für die der Read lief; der Vertrag steht in T6. `wish_alias_invalid` bleibt Rückfalltext.
c) **`emoteName` ist nach `adopt` der Slot-Name [E].** Die Oberfläche zeigt **nur `alias`** als Namen des Wunsches. `emoteName`
   wird nirgends als „7TV-Name“ ausgegeben und dient nur als Rückfall, falls `alias` leer wäre. Den echten Namen trägt der
   Link zur 7TV-Seite. Unverändert; das Backend bleibt hier, wie es ist.
d) **`EndedAtUtc` bei `complete-removal`: im Backend behoben [B]** (T0b, 2.1a; Sol-P3-12). Der Zeitpunkt, zu dem der Abgleich
   das Verlassen des Sets festgestellt hat, bleibt stehen. Die Oberfläche zeigt `endedAtUtc` als Datum und muss nichts
   kaschieren; ein Fall über Mitternacht steht als Test in T0b.

### 2.9 Fehlertexte im Wunsch-Kontext

Eine reine Funktion in `core/wishes/` (Form `wishErrorKey(error, kontext)`, Vorbild `backfillStartErrorKey`) übersetzt vor der
allgemeinen Abbildung: `emote_ids_invalid` → eigener Text „kein gültiger 7TV-Link bzw. keine gültige Emote-ID“;
`invalid_request_body` im Kontext `adopt` → „Startdatum liegt in der Zukunft oder vor dem Hinzufügen“; im Kontext `re-add`
→ allgemeiner Text. Alles andere geht unverändert an `apiErrorTranslationKey`. `errors.api.emote_ids_invalid` bleibt, wie es
ist; die Abstimmungs- und Tag-Routen sind nicht berührt. Der Client prüft das ID-Format ohnehin vor dem Senden.

### 2.10 i18n und Gestaltung

- Schlüsselbereiche: `wishes.*` (Seite, Abschnitte, Zeile, Dialoge; `wishes.status.<wert>` für alle dreizehn Zustände,
  `wishes.reason.expired`, `wishes.reason.unconfirmed_start`, `wishes.reason.other`, `wishes.source.*`, `wishes.errors.*`,
  `wishes.lock.*`, `wishes.start.*` für die Abbruchgründe aus 2.5), `channelWorkspace.tabs.wishes`,
  `channelWorkspace.settings.wishes.*`, `shell.wishes*`, `myWishes.*`, `sevenTvRun.kind.wish`, `audit.actions.wish*`. **Jeder
  Task legt seine Schlüssel in beiden Locales im selben Commit an.** Keine „…“ an Knöpfen. Zustandsnamen nach Spec 5.
- `unconfirmed_start` (de, sinngemäß): „Der Startzeitpunkt ist von 7TV noch nicht bestätigt, deshalb entscheidet hier ein
  Mensch.“ Den genauen Wortlaut legt T4 fest; er nennt weder „Bot“ als Schuldigen noch technische Feldnamen.
- Flächen: linierte Liste (`-mx-3 divide-y …`), randlose Abschnitte mit Überschrift, `StatusBadge` für Zustand und
  „Probezeit“, `EmptyState` je Abschnitt mit Grund (§6.2), `SkeletonRows` beim Laden, `NoticeBanner` für Fehler, `Pager`,
  Dialoge über `DialogShell`/`openAppDialog`, Felder nach §5. Kein Layout-Sprung zwischen den Tabs.
- **Bild (Sol-P3-13, Sol-R2-P3, [B] F7 = ableiten ohne Migration):** Die Zeile zeigt nie eine Fassung, die sich bewegen
  kann, solange nicht belegt ist, dass das Emote unbewegt ist. Drei Fälle: (1) `imageUrl` des DTO vorhanden → genau diese
  über `EmoteSprite` (sie ist das Standbild eines animierten oder das einzige Bild eines bestätigt unbewegten Emotes).
  (2) `imageUrl = null` und die Sitzung weiß aus dem 7TV-Einzel-Read (T6) „nicht animiert“ → die einfache Fassung.
  (3) `imageUrl = null` und nichts bekannt → es wird **nur** die `_static`-Fassung angefragt; lädt sie, wird sie gezeigt;
  scheitert sie, **gleich aus welchem Grund**, bleibt die Bildfläche leer. Ein Ladefehler ist kein Beleg für „nicht
  animiert“ (ein `<img>` kann 404 nicht von einem kurzen Netzfehler unterscheiden), deshalb gibt es keinen Rückfall auf die
  einfache Fassung. Folge: Ein unbewegtes Emote, das der Kanal noch nie im Set hatte und das jemand anders nachgetragen hat,
  zeigt bis zu seinem ersten Sync kein Bild, nur Alias und 7TV-Link. Die Regel ist eine kleine reine Entscheidung mit Spec
  (T4, `wish-image`).

---

## 3. Tasks

Je Task ein Subagent mit frischem Kontext, **strikt nacheinander** (die Backend-Tasks teilen `obj/`; die Frontend-Tasks
schreiben in dieselben zwei Locale-Dateien; die E2E-Suite teilt Port 4300, Läufe per `flock` serialisieren). Jeder
Backend-Task endet mit grünem `dotnet build EmotePurge.slnx` und seinen Tests, jeder Frontend-Task mit grünem
`npm --prefix web test -- --watch=false`, `npm --prefix web run lint`, `npm --prefix web run format:check`; jeder mit einem
eigenen Conventional Commit (englisch). Der DECISIONS-Eintrag ist **ein** englischer Eintrag „Wish emotes, child 3: manager UI
(#485, epic #481)“ über dem K2-Eintrag; T0a legt ihn an, jeder Task mit Vertrags- oder Konventionsänderung ergänzt seinen
Absatz und die `**Betrifft:**`-Zeile im selben Commit (Regel 3). Befehle einzeln absetzen, keine `cd … &&`-Ketten; **nie
`docker compose` aus dem Worktree**. Kommentare, Logs, Commit-Texte englisch.

| Task | Inhalt | Modell |
|---|---|---|
| T0a | Api-Lesewege: Generation und Bild am DTO, Tagesreihe, übernehmbare Einträge mit Konfliktlage, `canEdit` | opus |
| T0b | Api-Schreibverträge: `complete-removal` (gebunden, inventurgeprüft, idempotent), gebundenes `adopt`, `EndedAtUtc`, Konflikt-IDs, Schwelle erhalten | opus |
| T0c | Live-Probe der Api-Verträge (Regel 16) | sonnet mit Orchestrator |
| T1 | Modell, `WishService` (HTTP), `wishesEnabled`, Live-Typ | sonnet |
| T2 | Reine Regeln: Ableitungen, Aktionstabelle, Eingaben, Fehlertexte | sonnet |
| T3 | Guards und Tab-Bedingungen (E19), ohne Routen | sonnet |
| T4 | Kanal-Seite samt Route und Tab: Listen, Zeile, Gültigkeit und Aufholen, Sprungziel, Buchungs-Aktionen | opus |
| T5 | Nutzung in der Zeile: Kennzahlen und Sparkline desselben Fensters, mit Ablaufzeit | sonnet |
| T6 | Dialoge „Nachtragen“ und „Bestand übernehmen“, 7TV-Einzel-Read | opus |
| T7 | Dialoge „Bearbeiten“ und „Wieder hinzufügen“ | sonnet |
| T8 | 7TV hinzufügen: gemeinsame Mutation, Startvertrag, `WishAddService`, Laufart `wish` | opus |
| T9 | 7TV entfernen: frischer Read je Versuch, Abschlussvertrag des Lösch-Laufs, Fluss, Tracker, Dock | opus |
| T10 | Settings-Abschnitt „Wünsche“ | sonnet |
| T11 | Seite `my-wishes` samt Route | sonnet |
| T12 | Kopfzeile: `WishSummaryService`, Link mit Zahl, 360 px | opus |
| T13 | Audit-Aktionen, i18n-Abschluss, Gegenprobe der Fehlercodes | sonnet |
| T14 | E2E und Audit-Harness-Szenarien | sonnet |
| T15 | Doku | sonnet |
| T16 | Live-Verifikation im Browser, Stufen A und B (Abschnitt 7) | Orchestrator |
| T17 | Gates (Abschnitt 8) | Orchestrator |

### T0a: Api-Lesewege

- **Ziel:** Alles, was die Oberfläche lesen muss und heute nicht bekommt (2.1a „Lesefelder“: Generation, Bild, Tagesreihe,
  Einträge mit Konfliktlage, `canEdit`, `refresh` an der Set-Liste). Dazu geändert: `SevenTvEndpoints.cs` (Route
  `…/emote-sets`), `IForeignEmoteSetService` und seine Implementierung samt Härtungs-Dekorator, mit Test nach dem Vorbild
  des `refresh` der Schwester-Route: warmer Cache plus `refresh=true` → 7TV wird gefragt und der neue Stand geliefert; ohne
  Parameter → Cache wie bisher; Drosselung bleibt wirksam.
- **Dateien:** `src/EmotePurge.Core/Services/IWishService.cs` (DTOs, neuer Lesevertrag der Einträge),
  `src/EmotePurge.Infrastructure/Services/WishService.cs`, `WishOverviewService.cs`,
  `src/EmotePurge.Infrastructure/Persistence/WishUsageQueries.cs`, `src/EmotePurge.Api/Endpoints/WishEndpoints.cs`;
  Tests: `tests/EmotePurge.Infrastructure.Tests/Integration/WishServiceTests*.cs`, `WishOverviewServiceTests*`,
  `tests/EmotePurge.Api.Tests/WishEndpointsTests.cs`; `docs/DECISIONS.md`.
- **Vertrag:** nach 2.1a. Handler bleiben dünn (Regeln 4, 6); kein `AppDbContext` im Handler. `canEdit` braucht den Aufrufer
  im `GET`-Handler; die Entscheidung bleibt `CanChangeSettingsAsync`. Das Prädikat „übernehmbar“ steht einmal und wird von
  `AdoptAsync`, den `slotIds` in `CreateAsync` und dem neuen Leseweg benutzt. `runningWishAdoptable` fragt `WishTransitions`
  (dieselbe Zeile, die `AdoptAsync` fragt), es wird nichts nachgebaut. Das Bild wird je Antwort mit einer Abfrage über die
  Emote-IDs geholt (Listen, `mine` über Kanal und Emote-ID). Aggregat nach Regel 10 (IDs erst auf Skalarlisten).
- **Grenzfälle:** Wunsch ohne Slot; Generation unbekannt; Emote-Zeile fehlt, ist Platzhalter oder trägt kein Bild; Emote unter
  zwei Aliasen (zwei Einträge, eine Reihe über beide; der zweite Eintrag nennt den laufenden Wunsch am ersten als
  `runningWishId`, nicht übernehmbar); Fenster von einem Tag; Tage ohne Nutzung; Kanal ohne Einstellungszeile; Kanal ohne
  aktive Einträge (leere Liste, kein 404); ein Eintrag, den ein `kept`-Wunsch hält; ein freier Eintrag, zu dessen Emote ein
  `approved`-Wunsch läuft (`runningWishAdoptable` wahr); ein undatierter Eintrag (`addedAtUtc: null`); ein ungesetzelter Seed
  neben einem eigenen Slot desselben Emotes (der eigene steht in der Liste, der Seed nicht: der Fall aus Sol-P2-6).
- **AKs:** AK34, AK37, AK41, stützt AK35/AK45. **Tests (Regel 11):** Integration gegen Postgres: Generation am DTO auf den vier
  Wegen (Verknüpfen, Rückkehr mit neuer Generation derselben Slot-Zeile, Datumskorrektur, Übernehmen) samt der Aussage
  „`startsAtAuthoritative` folgt dem Slot“; Bild je Lage; Summe der Tagesreihe = `total`, zwei Aliase, Nulltage; Eintragsliste
  gegen `adopt` (jeder gelistete Eintrag ohne Halter und ohne nicht übernehmbaren Lauf-Wunsch ist übernehmbar, jeder nicht
  gelistete wird abgelehnt), Halter-ID, Lauf-ID und Übernehmbarkeit je Zustand des laufenden Wunsches, Filter. Api-Tests: die
  neue Route in der bestehenden Filter-Matrix (400/401/403/404), `canEdit` je Rolle.
- **Fertig:** `dotnet test EmotePurge.slnx` grün, Fixture-Test „keine ausstehende Modelländerung“ grün.
  **Commit:** `feat: expose slot generation, image and adoptable entries for wishes`

### T0b: Api-Schreibverträge

Dieser Task ist mit 2.1a („Schreibverträge“) für sich lesbar; er setzt T0a voraus (das DTO trägt `slotAddedAtUtc`).

- **Ziel:** Die Schreibwege, an denen die Oberfläche sonst Falsches buchen könnte: `complete-removal` (gebunden,
  inventurgeprüft, idempotent), `adopt` (an die gewählte Generation gebunden), Konflikt-IDs, die erhaltene Schwelle, das
  erhaltene `EndedAtUtc`.
- **Dateien:** `src/EmotePurge.Core/Services/IWishService.cs` (Anfrageformen von `complete-removal` und `adopt`, Ergebnis),
  `src/EmotePurge.Infrastructure/Services/WishService.cs`, `src/EmotePurge.Api/Endpoints/WishEndpoints.cs` (eigener Handler
  für `complete-removal` mit Rumpf statt der rumpflosen Sammelroute; 409-Rümpfe; Settings-Binding); Tests:
  `tests/EmotePurge.Infrastructure.Tests/Integration/WishServiceTests*.cs` (neue Teildatei für die Meldung),
  `WishServiceTests.LateDecisions.cs` (das Audit-Feld `previousExpiresAtUtc` bleibt unverändert),
  `DataRetentionServiceTests.Wishes.cs`, `tests/EmotePurge.Api.Tests/WishEndpointsTests.cs`; `docs/DECISIONS.md`.
- **Vertrag `complete-removal`:** die Zehn-Zeilen-Tabelle aus 2.1a wörtlich, erste zutreffende Zeile gewinnt.
  **Sperrreihenfolge:** eine Transaktion; zuerst die Kanalzeile (`RowLocks.LockChannelsByIdsAsync`, wie jeder Slot-Schreiber
  und wie `CreateAsync`/`AdoptAsync`), dann die Wunsch-Zeile (`RowLocks.LockWishesByIdsAsync`); **erst danach** werden Kanal
  (`ActiveEmoteSetId`), Wunsch und die Slots des Emotes gelesen. Keine weitere Sperre, keine Slot-Sperre: Die Kanalsperre
  genügt, weil jeder Slot-Schreiber sie hält. Die Definition „Ersatz sichtbar“ aus 2.1a steht als **eine** benannte Abfrage
  (Emote-ID und Kanal, nicht archiviert, kein ungesetzelter Seed; eigene Zeile mit anderer Generation, oder andere Zeile mit
  demselben Alias, die kein anderer Wunsch hält, gleich mit welchem Datum). Ein optionales `evidence` des Rumpfs
  (`acknowledged` | `absent`; anderes → 400) geht nur in die Audit-Details. Die Zuordnung Ergebnis → Status/Code bleibt in `MapResult`; der Test
  „jedes Ergebnis ist abgebildet“ bleibt vollständig. Das Audit von Zeile 7 und 9 trägt zusätzlich `slotId`,
  `slotAddedAtUtc`, `emoteSetId`; `previousExpiresAtUtc` wie bisher.
- **Vertrag `adopt`:** `expectedAlias` und `expectedAddedAtUtc` nach 2.1a; der Vergleich steht unter der Kanalsperre direkt
  nach dem Laden des Slots, vor Halter- und Lauf-Prüfung. Die bestehende Kanalsperre von `AdoptAsync` bleibt die erste Sperre.
- **Vertrag Konflikt-IDs, Schwelle, `EndedAtUtc`:** nach 2.1a.
- **Grenzfälle `complete-removal`:** zwei gleichzeitige Meldungen desselben Laufs (eine bucht, die andere bekommt 200 ohne
  Wirkung, ein Audit); Meldung, während der Sync seine Slot-Transaktion offen hält (sie wartet an der Kanalsperre und sieht
  danach die neue Inventur); Meldung zwischen Slot-Commit und Wunschabgleich bei wiederbelebter eigener Slot-Zeile (409) und
  bei neuer Slot-Zeile unter demselben Alias (409); Eintrag desselben Emotes unter **anderem** Alias kommt dazu (kein Ersatz,
  Buchung normal); Meldung nach Rückkehr an einen neuen Eintrag (409, Wunsch bleibt `active`); nach `re-add` (409); Datum mit
  Bruchteilen unterhalb der Millisekunde; `removed_externally` genau an der 6-Stunden-Grenze; Meldung über Mitternacht
  (`EndedAtUtc` behält den Vortag); Set stimmt nicht; Kanal ohne aktives Set; Set hat zwischen Buchung und Wiederholung
  gewechselt (409 statt 200, nichts geändert).
- **Grenzfälle `adopt`:** Slot-Zeile mit derselben ID, aber neuem Datum (404, nichts geschrieben); Alias geändert (404);
  undatiert erwartet und undatiert vorgefunden (übernommen); undatiert erwartet, inzwischen datiert (404); datiert erwartet,
  Slot inzwischen archiviert (404 wie bisher). **Grenzfälle Schwelle:** fehlt bei vorhandener Schwelle, bei fehlender
  Einstellungszeile, bei gleichzeitig geänderter Laufzeit.
- **AKs:** AK37, AK34, AK41. **Tests (Regel 11), Integration gegen Postgres:**
  - je Zeile der Tabelle ein Fall;
  - **pausierter bzw. noch nicht gelaufener Wunschabgleich:** die Slot-Inventur wird mit den echten Schreibern des Syncs auf
    den Ersatz gebracht (einmal wiederbelebte Zeile, einmal neue Zeile), der Abgleich läuft **nicht**; die Meldung mit dem
    alten Tripel → 409, Wunsch unverändert; danach läuft der Abgleich → Wunsch `active` am Ersatz; dieselbe Meldung erneut →
    weiterhin 409;
  - **älterer Rückkehrkandidat (Sol-R3-P1):** das Emote liegt unter zwei Aliasen im Set; der Eintrag des Wunsches wird
    entfernt, der **ältere** zweite Eintrag wird auf den Alias des Wunsches umbenannt; der Vollsync committet (Slot des
    Wunsches archiviert, älterer Slot behält ID und Datum unter dem neuen Namen), der Abgleich ist angehalten; die Meldung mit
    dem alten Tripel → 409; danach läuft der Abgleich und führt den Wunsch an den älteren Eintrag (sofern im Fenster);
  - **falsches Set:** Meldung mit fremder Set-ID bei `active`, bei `removed_externally` und als Wiederholung nach Buchung →
    jeweils 409, nichts geändert;
  - **verlorene Antwort:** zweiter Aufruf mit demselben Tripel → 200, weiterhin genau ein Audit-Eintrag, ein Ereignis,
    `DecisionVersion` und `EndedAtUtc` unverändert;
  - beide Reihenfolgen mit `sync-deleted`; die Nebenläufigkeit zweier Meldungen; Meldung gegen eine offene Slot-Transaktion;
  - Retention: ein Wunsch, dessen `EndedAtUtc` aus dem Abgleich stammt und der danach gemeldet wird, fällt am selben Stichtag
    wie ohne Meldung (30 Tage und 365 Tage);
  - `adopt`: die Grenzfälle oben; Schwelle: fehlt → Wert und `DecisionVersion` aller aktiven Wünsche unverändert und keine
    Einreihung verworfen, `null` und Wert → wie bisher (die K2-Tests zur Schwelle bleiben grün).
  Api-Tests: 400 ohne Rumpf bzw. ohne `expectedAlias`, 409-Rümpfe mit `wishId`, die drei Schwellen-Fälle am echten Binding.
- **Fertig:** `dotnet test EmotePurge.slnx` grün; Fixture-Test „keine ausstehende Modelländerung“ grün (keine Migration).
  **Commit:** `feat: bind wish removal reports and take-overs to the slot generation`

### T0c: Live-Probe der Api-Verträge (Regel 16)

- **Ziel:** Die Verträge aus T0a/T0b an echter Postgres-, Redis- und 7TV-Lage zeigen, bevor die Oberfläche darauf baut.
- **Umgebung:** die Kopie und der Stack aus Abschnitt 7 (dieselbe Kopie dient später T16). Per `curl` mit dem Session-Cookie.
- **Schritte:** (1) Liste und Detail tragen `slotAddedAtUtc` und `imageUrl`; `slotAddedAtUtc` ist der `AddedAt` des Slots in
  der Datenbank. (2) Detail eines Wunsches mit gezählter Nutzung: Summe von `daily` = `total`. (3) `GET …/slots` gegen den
  Bestand der Kopie, ein gehaltener Eintrag nennt seinen Wunsch. (4) Handeintrag für ein Emote im Set mit laufendem Wunsch →
  409 mit `slotIds` und `wishId`; `adopt` des gehaltenen Eintrags → 409 mit `wishId`. (5) Test-Emote in 7TV hinzufügen (Wunsch
  wird `active`), in 7TV entfernen, Sync abwarten (`removed_externally`, `EndedAtUtc` notieren), `complete-removal` mit dem
  richtigen Tripel → 200 `removed`, `EndedAtUtc` unverändert; derselbe Aufruf noch einmal → 200, weiterhin ein `wish.remove`.
  (6) Zweiter Durchgang: entfernen, unter gleichem Alias wieder hinzufügen, Sync (Wunsch wieder `active`, neue Generation),
  dann `complete-removal` mit dem **alten** Tripel → 409, Wunsch bleibt `active`. (6a) Dritter Durchgang mit **angehaltenem
  Worker-Abgleich** (Worker mit `Wishes__Enabled=false` neu starten: Er synchronisiert Slots, gleicht aber keine Wünsche ab):
  entfernen, unter gleichem Alias wieder hinzufügen, Sync abwarten (Slot trägt die neue Generation, der Wunsch noch die alte),
  `complete-removal` mit dem alten Tripel → 409; Worker wieder mit `true`, nach dem Abgleich ist der Wunsch `active` am
  Ersatz. (6b) `complete-removal` mit fremder Set-ID → 409. (6d) Set-Liste zweimal kurz hintereinander, dazwischen das aktive Set des Testkontos in 7TV wechseln und zurück: ohne
  `refresh` der alte Stand, mit `refresh` der neue. (6c) `adopt` mit richtigem `slotId`, aber dem Datum der vorigen
  Generation → 404, nichts geschrieben; mit dem aktuellen Datum → 201. (7) `PUT …/settings` ohne Schwelle bei
  gesetzter Schwelle → Schwelle steht noch; mit `null` → aus. (8) `canEdit` wahr für das Testkonto.
- **Fertig:** Protokoll als erster Teil von `docs/nachtlaeufe/live-485-<Datum>.md`; 7TV-Set des Testkontos wie vorher.

### T1: Modell, Dienst, Schalter, Live-Typ

- **Ziel:** Die typisierte Naht zur Api; alles Weitere baut darauf.
- **Dateien:** neu `core/wishes/wish.model.ts`, `core/wishes/wish.service.ts`, `wish.service.spec.ts`. Geändert:
  `core/channels/channel.model.ts` (`wishesEnabled`), `core/live/live-event.model.ts` (`wishChanged`), `docs/DECISIONS.md`.
- **Vertrag:** Typen nach 2.1 (Zustände, Gründe, Quellen als bekannte Literale plus `string`). `WishService` hat je Route aus
  2.1 genau eine Methode und gibt die Antwort unverändert als Observable zurück; kein Zustand, kein Cache, keine Fehlerumdeutung
  (die macht 2.9). `completeRemoval` verlangt das Tripel als Pflichtargument, `adopt` den erwarteten Alias und das erwartete
  Datum des gewählten Eintrags (`null` bei undatiert). Die Antwort des Handeintrags ist `WishDto` mit
  optionalem `setFull`. Die 409-Rümpfe (`slotIds`, `wishId`) sind typisiert lesbar (ein kleiner Leser neben `apiErrorCode`).
  `ChannelPermissions.wishesEnabled` ist Pflichtfeld des Typs; „fehlt“ gilt als falsch.
- **Grenzfälle:** `usage: null`; `rank`/`rankOf` null; `daily` leer oder fehlend (älterer Server) gilt als „keine Reihe“.
- **AKs:** Grundlage für alle; AK32 (Feld). **Tests:** siehe 5. **Commit:** `feat(web): add wish model and api client`

### T2: Reine Regeln

- **Ziel:** Jede Entscheidung der Oberfläche als reine, tabellarisch getestete Funktion, ohne Angular.
- **Dateien:** neu in `core/wishes/`: `wish-view.ts` (2.2, 2.3, Gruppierung für `my-wishes`), `wish-input.ts` (Eingaben),
  `wish-error.ts` (2.9), je mit `*.spec.ts`.
- **Vertrag:**
  - Ableitungen und Aktionstabelle exakt nach 2.2 und 2.3; „jetzt“ ist Parameter.
  - Gruppierung für `my-wishes` (AK42): stabile Zweiteilung der Serverliste in „Handlungsbedarf“ und „Probezeit“; die
    Reihenfolge innerhalb jeder Gruppe ist die des Servers. Was in keine Gruppe fällt (neuerer Server), wandert zu
    „Probezeit“ ans Ende statt zu verschwinden.
  - Emote-Eingabe: akzeptiert einen Link auf die 7TV-Emote-Seite (mit oder ohne Schrägstrich am Ende, mit Query; auch die
    Alt-Domain) oder eine nackte ID. Eine nackte ID gilt nur in den zwei Formen, die 7TV vergibt (24 Hex-Zeichen, 26 Zeichen
    ULID) **[E]**: Sonst läse der Parser einen Emote-Namen wie „KEKW“ als ID. Ergebnis: ID oder Fehlerart.
  - Login: trimmen, ein führendes „@“ entfernen, kleinschreiben, dann gegen dasselbe Muster wie der Server (4 bis 25 Zeichen
    aus Kleinbuchstaben, Ziffern, Unterstrich). Leer heißt „nicht angegeben“ (Regel 9 sinngemäß, AK33).
  - Laufzeit 1 bis 365 ganze Tage, Vorlauf 1 bis 30; Alias 1 bis 100 Codepunkte ohne Leer- und Steuerzeichen. Den vorhandenen
    Alias-Prüfer des Imports wiederverwenden, falls er dieselbe Regel trägt; sonst hier, nicht doppelt.
- **Grenzfälle:** Laufzeit kürzer als Vorlauf; `expiresAtUtc` genau jetzt; fehlende Zeitfelder; unbekannter Zustand und Grund;
  `presented` mit und ohne Slot; „Entfernen möglich“ bei fehlender Generation oder vorläufigem Datum; Dezimal- und Leereingaben.
- **AKs:** AK33, AK35, AK36, AK42 (reine Teile). **Tests:** siehe 5. **Commit:** `feat(web): add wish view rules and input checks`

### T3: Guards und Tab-Bedingungen

- **Ziel:** Die Bedingungen nach Spec E19 und der Guard der Wunsch-Seite. **Keine Route** (Sol-R2-P3): Jede Route wird im
  Task ihrer Zielseite eingehängt, `wishes` in T4, `my-wishes` in T11.
- **Dateien:** neu `core/channels/channel-wishes.guard.ts` (+ Spec; **[P]** die Guards liegen in `core/channels/`). Geändert:
  `core/channels/channel-settings.guard.ts` (+ Spec), `features/channel-workspace/channel-workspace-layout.ts` (+ Spec),
  `features/channel-workspace/channel-settings-page.ts`, `e2e/support/mocks.ts` (`wishesEnabled` in
  `mockChannelPermissions`, Vorgabe `false`), `docs/DECISIONS.md` (E19).
- **Vertrag:** `channelWishesGuard`: eingeloggt, `canManage` **und** `wishesEnabled`; sonst derselbe Rückfall wie
  `channelManageGuard`, ohne Login zur Login-Seite mit gemerkter Rückkehr-URL; ein Fehler der Rechteabfrage zählt als „nein“.
  Settings-Tab und `channelSettingsGuard`: `canManage && (chatLogBackfillEnabled || wishesEnabled)`. Die Settings-Seite zeigt
  den Backfill-Abschnitt nur bei seinem Schalter. Das Layout liest beide Schalter als Signale aus derselben
  `/permissions`-Antwort und setzt sie im Fehlerfall auf falsch.
- **Zwischenstand, damit jeder Task mit grüner Typprüfung endet:** Nach T3 existiert der Guard mit Spec, ist aber von keiner
  Route benutzt; es gibt **keinen** Tab-Link „Wünsche“ (ein Link ohne Route wäre tot) und keinen Import einer noch nicht
  vorhandenen Seite. Der Settings-Tab erscheint bei nur eingeschaltetem Wunsch-Schalter schon und zeigt bis T10 nur seine
  Überschrift (Zwischenstand auf dem Branch, nie ausgeliefert). T4 hängt Route und Tab-Link „Wünsche“ ein, T10 den
  Settings-Abschnitt, T11 die Route `my-wishes`, T12 den Link in der Kopfzeile.
- **Grenzfälle:** beide Schalter aus → kein Settings-Tab, Umleitung wie bisher (Backfill-Spec D17, AK14 bleiben); der veraltete
  Kommentar „Temporary condition“ am Settings-Link wird ersetzt.
- **AKs:** AK32 (Guard), AK41 (Tab-Teil). **Tests:** siehe 5; `channel-settings.e2e.spec.ts` bleibt grün (Mock-Vorgabe `false`).
- **Commit:** `feat(web): guard and tab conditions for channel wishes`

### T4: Kanal-Seite, Route, Zeile, Gültigkeit und Aufholen, Buchungs-Aktionen

- **Ziel:** Die Arbeitsliste: drei Abschnitte, die Zeile mit Zustand und Fristen, alle Aktionen ohne 7TV; dazu Route und Tab.
- **Dateien:** neu `features/wishes/channel-wishes-page.ts` (+ Spec), `features/wishes/wish-row.ts` (+ Spec),
  `features/wishes/wish-image.ts` (+ Spec, die Bildregel aus 2.10), ggf. `features/wishes/wishes.routes.ts`; geändert
  `app.routes.ts` (Kindroute `wishes` unter `channels/:channelName`, lazy, mit `channelWishesGuard`),
  `features/channel-workspace/channel-workspace-layout.ts` (+ Spec; Tab-Link „Wünsche“ hinter
  `canManage() && wishesEnabled()`, Position nach „Aktivität“, vor „Einstellungen“ **[E]**),
  `core/live/live-update.service.ts` und `core/live/live-reload.ts` (+ Specs; Wiederverbindung je Stream-URL, 2.4); Locales;
  DECISIONS.
- **Vertrag Seite:** Eingabe `channelName` (required; nie im Konstruktor lesen, Regel 13). Abschnitte: „Handlungsbedarf“
  (`open`), „Laufend“ (`running`), „Beendet“ (`ended`, zugeklappt, lädt beim Öffnen). Je Abschnitt Überschrift mit Anzahl,
  Ladegerüst, leerer Zustand mit Grund, Pager nur bei mehr als einer Seite (lokaler Zustand). Kopf der Seite: Nebenaktionen
  „Nachtragen“ und „Bestand übernehmen“ (ab T6). **Laden, Gültigkeit und Aufholen exakt nach 2.4**: Abrufnummer je Ansicht
  beim Auslösen, Verwerfen überholter und fremder Antworten, die sechs Auslöser (a) bis (f), die Wiederverbindung je
  Kanal-Stream. **Sprungziel** nach 2.4: Query-Parameter `wish`, Fokus auf die geladene Zeile oder angeheftete Einzelzeile.
  Ein Fehler eines Abschnitts zeigt dessen Banner und lässt die anderen stehen. Die Seite hält das Minuten-Signal „jetzt“.
- **Vertrag Live-Dienst:** je Stream-URL eine Meldung „erneut geöffnet“ (nicht beim ersten Öffnen); nichts an der
  dienstweiten Statusanzeige und nichts am Verhalten der bestehenden Abonnenten ändert sich.
- **Vertrag Zeile:** Eingaben: der Wunsch, „jetzt“, optional Nutzung (T5), Sperrzustand und Vorgangszustand (T8/T9), ob gerade
  eine eigene Anfrage läuft. Ausgabe: genau ein Ereignis „Aktion gewählt“ mit der Aktionsart. Die Zeile entscheidet nichts
  außer über 2.2/2.3 und ruft keinen Dienst. Inhalt: Bild (2.10), Alias, Zustand als `StatusBadge`, bei Probezeit zusätzlich
  „Probezeit“, Grundzeile bei `presented`/`failed`, Wünschender, Quelle, Laufzeit, Zeit im Set, Restzeit bzw. „abgelaufen
  seit“, Anzahl der Verlängerungen, „dauerhaft“, bei beendeten das Enddatum; Link zur 7TV-Seite.
- **Vertrag Bild:** nach 2.10; die einfache Fassung nur bei bestätigt unbewegtem Emote, sonst Standbild oder leere Fläche.
- **Buchungs-Aktionen (Seite):** Verlängern, Erneut versuchen direkt; Behalten, Schließen nach `ConfirmDialog`. Erfolg →
  betroffene Listen neu, `WishSummaryService.refresh()` (ab T12). Ablehnung → Banner am Abschnitt mit `wishErrorKey`; bei 409
  `wish_state_conflict` und 404 `wish_not_found` zusätzlich neu laden.
- **Grenzfälle:** Antwort von Seite 1 trifft nach dem Wechsel auf Seite 2 ein; die ältere von zwei Antworten derselben Ansicht
  trifft zuletzt ein; Antwort für den vorigen Kanal nach einem Kanalwechsel; `wish.changed` geht verloren und der nächste Sync
  holt auf; **dieser** Stream fällt aus und kommt wieder, während ein anderer offen bleibt; Fenster war verborgen; `active`
  schon abgelaufen, aber noch nicht vorgelegt; `presented` ohne Ablauf; Wunsch verschwindet zwischen Klick und Antwort;
  letzte Seite wird leer; ein Schwall von Ereignissen; Sprung zu einem Wunsch auf einer ungeladenen Seite, in „Beendet“
  (zugeklappt), und zu einem, den es nicht mehr gibt; Standbild-Anfrage scheitert einmal kurz.
- **AKs:** AK32 (Route und Tab), AK35, AK36, AK39 (Live-Teil). **Tests:** siehe 5. **Commit:** `feat(web): channel wishes work list`

### T5: Nutzung in der Zeile

- **Ziel:** „Nutzungszahl statt Stoppuhr“: Summe, Mittel je Tag, Rang und eine Linie, die **dasselbe** zeigt wie die Zahlen,
  und die nicht veraltet stehen bleibt.
- **Dateien:** neu `features/wishes/wish-usage.ts` (+ Spec für Lade-, Schlüssel- und Ablauf-Entscheidung); `wish-row.ts`,
  `channel-wishes-page.ts`; Locales.
- **Vertrag (Sol-P2-11, Sol-R2-P2-6):** Je sichtbarer Zeile in `open` und `running` **ein** Abruf `GET /{id}`. Die Sparkline
  (`app-usage-sparkline`) zeichnet `usage.daily`, also genau das Fenster `windowFromUtc` bis `windowToUtc` über alle eigenen
  Slots des Emotes; daneben als Text Summe und Mittel, die Fensterangabe („letzte 7 Tage“ bzw. „seit dem Start“, abgeleitet
  aus den Fenstergrenzen, nie fest verdrahtet), „Rang N von M“ nur mit beiden Werten, bei `coverageGap` der Hinweis, dass die
  Zählung das Fenster nicht ganz abdeckt. Kein Abruf von `usage-stats/daily`, keine Slot-Reihe. Ohne `usage` kein
  Nutzungsblock; ohne `daily` Zahlen ohne Linie. **Halten und Verfallen nach 2.4:** je Schlüssel, höchstens 5 Minuten; eine
  sichtbare Zeile lädt nach Ablauf, bei geändertem Schlüssel und nach Wiederverbindung bzw. Sichtbarwerden neu; eine
  unsichtbare Zeile lädt erst wieder, wenn sie sichtbar wird. Während des Neuladens bleibt der alte Wert stehen (kein
  Flackern), ein Fehler beim Neuladen lässt ihn stehen und versucht es beim nächsten Takt wieder. Zahlen werden angezeigt,
  nicht nachgerechnet.
- **Grenzfälle:** Detail 404 (Zeile ohne Nutzung, kein Banner); 429 → Block leer mit „später erneut“, kein Dauerversuch;
  Fenster von einem Tag (ein bis zwei Punkte: Zahlen ohne Linie); die Nutzung ändert sich bei gleichem Schlüssel (neue
  Zählung, Tageswechsel, geänderte Abdeckung): spätestens nach der Ablaufzeit sichtbar.
- **AKs:** stützt AK35/AK45. **Commit:** `feat(web): usage on wish rows`

### T6: „Nachtragen“ und „Bestand übernehmen“

- **Ziel:** Die zwei Nebenwege der Erfassung samt der Konfliktbehandlung aus 2.8 a und b.
- **Dateien:** neu `core/seven-tv/seven-tv-emote-lookup.ts` (+ Spec), `features/wishes/wish-entry-dialog.ts` (+ Spec),
  `features/wishes/wish-adopt-dialog.ts` (+ Spec); `channel-wishes-page.ts`; `e2e/support/mocks.ts` (Anfrageart des
  Einzel-Reads in `mockSevenTvGql`); Locales; `docs/DECISIONS.md`.
- **Vertrag 7TV-Einzel-Read:** Eingabe Emote-ID; Ergebnis **mit der ID, für die gefragt wurde**: „gefunden“ (Standardname,
  „animiert“ ja/nein, falls die Antwort es hergibt) · „unbekannt“ · „nicht erreichbar“ · „gedrosselt“ (7TV verkleidet 429 als
  HTTP 200 mit Status in den `extensions`). Öffentlich, ohne Token, mit Zeitlimit. Das Ergebnis „gefunden, nicht animiert“
  wird für die Sitzung je Emote-ID gemerkt (die Bildregel aus 2.10 liest es). **Die Form der Abfrage wird vor dem Schreiben
  live belegt** (ein Aufruf von Hand gegen v4-GQL; blockt der Klassifikator die Sonde, nach zwei Versuchen abbrechen und den
  Betreiber um den einen `curl` bitten) und im DECISIONS-Absatz festgehalten.
- **Vertrag Nachtragen-Dialog:** Felder: Emote (Link oder ID, Pflicht), Alias (Pflicht), Laufzeit (vorbelegt mit
  `defaultDurationDays` aus `GET …/settings`, AK33), Login des Wünschenden (optional). Gesendet wird der normalisierte Login.
  Rückgabe: der angelegte Wunsch, oder die ID eines Wunsches zum Springen (2.4), oder nichts. Bei `setFull: true` schließt
  der Dialog mit einem Hinweis an der Seite.
- **Kopplung von Read und Feldern (Sol-P2-10, verbindlich):** Der Dialog hält zu jedem Zeitpunkt genau eine „aktuelle
  Emote-ID“ (das Ergebnis des Parsers) und einen Read-Zustand, der **zu einer ID gehört**: `kein` · `läuft(ID)` ·
  `gefunden(ID)` · `unbekannt(ID)` · `nicht erreichbar(ID)`. (1) Ändert sich die aktuelle ID (auch auf „ungültig“), werden
  **sofort**, nicht erst nach der Entprellung, der Read-Zustand auf `kein` bzw. `läuft` gesetzt, die Vorschau geleert und ein
  **automatisch** gesetzter Alias entfernt; ein vom Nutzer getippter Alias bleibt. (2) Ein Read-Ergebnis wird nur übernommen,
  wenn seine ID die aktuelle ist; sonst wird es verworfen (zusätzlich zu `latestOnly`, das erst beim Abonnieren zählt). (3)
  Senden ist nur möglich bei `gefunden(aktuelle ID)` oder `nicht erreichbar(aktuelle ID)` (Ausweichweg aus 2.8 b, Alias von
  Hand, `emoteName` entfällt, Hinweis sichtbar); `läuft` und `unbekannt` sind Sperrgründe mit sichtbarem Text. (4) Gesendet
  wird die aktuelle ID zusammen mit dem Namen aus `gefunden(aktuelle ID)`, nie ein Name aus einem früheren Read. Weitere
  Sperrgründe: ungültige Eingabe, leeres oder ungültiges Alias, Laufzeit außerhalb 1 bis 365, ungültiger Login.
- **409 im Nachtragen-Dialog:** nach 2.8 a, gestützt auf die `wishId` im Rumpf. Andere Fehler: Feldfehler am zugehörigen
  Feld bzw. Banner im Dialog über `wishErrorKey`.
- **Vertrag Übernehmen-Dialog (Sol-P2-6, Sol-R2-P2-4, Sol-R2-P2-5):** Eingabe: Kanal, optional vorgewählte `slotIds`,
  optional `wishId`, optional Emote-ID. **Quelle der Einträge ist `GET …/wishes/slots`** (mit Emote-Filter, wenn eine ID
  bekannt ist), nichts anderes. Ohne Vorwahl alle Einträge mit Suchfeld; mit Vorwahl nur diese; ein vorgewählter Eintrag, den
  die Liste nicht kennt, steht als „nicht mehr im Set“ da und ist nicht wählbar. Je Eintrag Bild, Alias, Hinzufügedatum
  (oder „Datum unbekannt“). **Drei Arten von Einträgen, allein aus den Feldern der Liste:**
  1. **frei** (`heldByWishId` und `runningWishId` leer): wählbar; legt einen neuen Wunsch an (bzw. verknüpft die mitgegebene
     `wishId`).
  2. **gezielt verknüpfen** (`runningWishId` da, `runningWishAdoptable` wahr): wählbar; die Auswahl sagt „wird mit dem
     laufenden Wunsch verknüpft“ und sendet `adopt` **mit dieser `wishId`**; die Laufzeit ist mit der dieses Wunsches
     vorbelegt. Ist dem Dialog schon eine andere `wishId` mitgegeben, ist der Eintrag gesperrt.
  3. **gesperrt** (`heldByWishId` da, oder `runningWishId` da und nicht übernehmbar): nicht wählbar; der Eintrag nennt den
     Grund („gehört zu einem laufenden Wunsch“, „ist als behaltener Wunsch geführt“, „für dieses Emote läuft schon ein Wunsch
     an einem anderen Eintrag“) und bietet „Zum Wunsch“ (Sprungziel aus 2.4; der Dialog schließt und gibt die ID zurück).
  Felder: Laufzeit und Beginn der Frist **[B] (F6)**: „ab Hinzufügedatum“ (kein `startsAtUtc`) oder „ab jetzt“; der Dialog
  zeigt den Ablauf als Datum; läge er mit „ab Hinzufügedatum“ in der Vergangenheit, ist „ab jetzt“ vorgewählt und ein Satz
  sagt, warum; bei einem undatierten Eintrag gibt es nur „ab jetzt“. **Gesendet wird immer, was gewählt wurde:**
  `slotId`, `expectedAlias` und `expectedAddedAtUtc` des gewählten Eintrags, wortgleich aus der Liste (`null` bei undatiert).
- **Veralteter Stand beim Übernehmen (verbindlich):** Jede Ablehnung, die sagt „die Lage ist nicht mehr die gezeigte“ (404
  `wish_slot_not_found`, 409 `wish_slot_already_linked`, `wish_already_running`, `wish_state_conflict`, 404
  `wish_not_found`), führt zum selben Ablauf: Die Einträge werden **neu geladen**, die Auswahl wird **aufgehoben**, ein Satz
  sagt, was sich geändert hat (mit „Zum Wunsch“, wenn der Rumpf eine `wishId` nennt), und der Verwalter muss **erneut
  wählen**. Nie ein automatischer zweiter Versuch, nie ein stilles Umschalten auf eine andere `wishId`.
- **Grenzfälle:** Eingabe wechselt während der Entprellung und während der Anfrage; zurück auf die vorige ID; ID gültig, aber
  Emote gelöscht; Eintragsliste lädt nicht (Dialog sagt es, bietet erneutes Laden, kein Senden); Slot-ID wurde zwischen Laden
  und Senden mit neuem Datum wiederverwendet (neu laden, neu wählen); der laufende Wunsch eines „gezielt verknüpfen“-Eintrags
  ist inzwischen `active` (neu laden, Eintrag ist jetzt gesperrt); Doppelklick auf Senden; 404 `wishes_disabled` mitten im
  Dialog.
- **AKs:** AK33, AK34. **Tests:** siehe 5. **Commit:** `feat(web): manual entry and take-over dialogs for wishes`

### T7: „Bearbeiten“ und „Wieder hinzufügen“

- **Ziel:** Die zwei kleinen Formulardialoge der Aktionstabelle.
- **Dateien:** neu `features/wishes/wish-edit-dialog.ts`, `features/wishes/wish-readd-dialog.ts` (je mit Spec nur für die
  Sperr- und Rückgabelogik); `channel-wishes-page.ts`; Locales.
- **Vertrag:** Bearbeiten (nur `approved`, `failed`): Alias und Laufzeit, vorbelegt; gesendet werden nur geänderte Felder;
  ohne Änderung ist Senden gesperrt. Wieder hinzufügen (`removed`, `removed_externally`): Wahl „befristet“ mit Laufzeit
  (vorbelegt Kanal-Vorgabe) oder „dauerhaft“ (dann keine Laufzeit; Satz: „wird nach dem Hinzufügen als behalten geführt“).
  Fehler über `wishErrorKey` (Kontext `re-add`), `wish_already_running` → Hinweis mit Sprung zum laufenden Wunsch.
- **AKs:** AK35. **Commit:** `feat(web): edit and re-add dialogs for wishes`

### T8: 7TV hinzufügen

- **Ziel:** „In 7TV hinzufügen“ an `approved`-Zeilen, mit eigenem Token, über den Arbiter, nach dem Startvertrag.
- **Dateien:** `core/seven-tv/seven-tv-import.service.ts`, `seven-tv-restore.service.ts`, `seven-tv-undo.service.ts` (nur die
  Konstante ersetzen), neu die gemeinsame Konstante (eigene kleine Datei in `core/seven-tv/`),
  `core/seven-tv/seven-tv-run-arbiter.ts` (Laufart, Substantiv), neu `core/wishes/wish-run-start.ts` (+ Spec; die Phasen 1 bis
  3 aus 2.5 als gemeinsamer Baustein für T8 und T9, samt dem autoritativen Read des aktiven Sets), neu
  `core/wishes/wish-add.service.ts` (+ Spec), `features/wishes/channel-wishes-page.ts`, `wish-row.ts`; die Aufzählstellen der
  Laufarten; Locales; DECISIONS.
- **Vertrag:** nach 2.5 und 2.5a. Der erste Commit des Tasks ist **nur** das Zusammenziehen der Mutation (eigener
  `refactor:`-Commit, Bestands-Specs unverändert grün); der zweite bringt Startbaustein und Dienst. Der Startbaustein nimmt
  seine Abhängigkeiten gereicht (Muster `DeleteFlowDeps`) und kennt keine Seite; Schritt 3b ist als **eine synchrone
  Funktion** gebaut, die die beiden schon gelesenen Antworten, den Aufrufer-Zustand und den Arbiter bekommt und entweder einen
  Abbruchgrund oder „starten“ liefert, und deren Aufrufer im selben Zug startet. Für Wiederholungen nach 429 hängt der Dienst
  die Tabelle „vor jedem Senden“ aus 2.5 an den `beforeStep`-Haken seiner Engine (Set mit `refresh`, Token-Gleichheit, Wunsch),
  schon vor dem ersten Senden. Der Baustein „Set und Token vor dem Senden“ ist so geschnitten, dass T9 ihn für den Lösch-Lauf
  wiederverwendet.
- **Grenzfälle:** Token fehlt/abgelaufen (Prompt; ein 401 von 7TV meldet nie die EmotePurge-Sitzung ab); Kanal ohne aktives Set
  oder autoritativer Read nicht verfügbar (kein Start, mit Grund); Vorab-Check verzögert und währenddessen startet ein anderer
  Vorgang; **der letzte Read hängt, währenddessen wird die Seite gewechselt** (Abbruch in 3b, nichts gesendet); **der letzte
  Read hängt, währenddessen startet ein bereits bestätigter Bestandslauf** oder meldet seinen eigenen letzten Read
  (Abbruch in 3b); währenddessen wird der Wunsch geschlossen oder verknüpft; währenddessen wechselt das aktive Set in 7TV,
  ohne dass ein Sync gelaufen ist (Abbruch, weil der autoritative Read es sieht); die frischen Reads scheitern; nach einem 429
  hat sich der Wunsch oder das Set geändert (Zeile übersprungen, kein zweiter Schreibversuch); Seitenwechsel nach dem
  Schreiben (Ergebnis steht beim Zurückkehren an der Zeile); der Wunsch wird `active`, bevor die Antwort da ist.
- **AKs:** AK38 (Hinzufügen), stützt AK45. **Tests:** siehe 5. **Commits:** `refactor(web): share the 7tv add-emote mutation`,
  `feat(web): add a wished emote to the set from its row`

### T9: 7TV entfernen

Dieser Task ist mit 2.1a, 2.5 und 2.6 für sich lesbar; er setzt T0a/T0b (Api) und T8 (Startbaustein `wish-run-start`) voraus.

- **Ziel:** „Entfernen“ an `active`- und `presented`-Zeilen: Ziel aus der gebundenen Generation, frischer Set-Read vor jedem
  Sendeversuch, Abschluss je Lauf, genau eine gebundene Erfolgsmeldung.
- **Dateien:** `core/seven-tv/seven-tv-delete.service.ts` (+ Spec: Rückgabe des Starts, Abschlussmeldung je Lauf, Merkmal
  „Wunsch-Bindung“ je Eintrags-Zeile; sonst kein Verhalten geändert), neu `features/wishes/wish-removal-flow.ts`
  (+ Spec), `core/wishes/wish-removal-tracker.ts` (+ Spec); `channel-wishes-page.ts` (Dock, Sperrgründe), `wish-row.ts`;
  Locales; DECISIONS.
- **Vertrag Lösch-Dienst (drei Ergänzungen, je ohne Wirkung für Bestandsaufrufer):**
  1. `startDelete` gibt zurück, ob der Start angenommen wurde, und die Laufkennung.
  2. Je Laufkennung genau eine Abschlussmeldung mit dem endgültigen Ergebnis der Zeilen (nach dem Nachlesen unbekannter
     Ausgänge), unabhängig vom gezeigten Lauf und von jeder Seite.
  3. Eine Eintrags-Zeile kann das Merkmal „Wunsch-Bindung“ tragen, zusammen mit dem eingefrorenen aktiven Set, dem
     bestätigten Token (nur als Vergleichswert im Speicher des Laufs) und einer vom Aufrufer gereichten Prüfung „Set und
     Token“. Trägt sie es, gelten vor **jedem** Versuch dieser Zeile die vier Punkte aus 2.6 (Token, aktives Set mit
     `refresh`, Generation aus einem nie wiederverwendeten Read, Beobachtung höchstens 5 Sekunden alt, sonst einmal neu lesen
     oder auslassen). Ohne das Merkmal ist jede Anfrage und jeder Read Byte für Byte wie heute. Die übrigen Regeln der Bindung
     (drei Lesefehler in Folge, Lücken-Read, Rate-Limit des Reads als Pause) gelten unverändert.
  4. Nicht Pflicht, nur falls ohne Umbau möglich: Die Abschlussmeldung sagt je `done`-Zeile, ob 7TV den REMOVE bestätigt
     hat oder erst das Nachlesen das Fehlen festgestellt hat; der Tracker gibt das als `evidence` mit (R20).
- **Vertrag Fluss (`wish-removal-flow`):** Phasen nach 2.5 über den Startbaustein aus T8 (er ruft `confirmTimeRefusal` und
  geht darüber hinaus). Ziel nach 2.6: `slotId`, `alias`, `slotAddedAtUtc` des eingefrorenen Wunsches, das Datum wortgleich;
  Set = eingefrorenes aktives Set; Merkmal „Wunsch-Bindung“ gesetzt. Vor dem Schreiben `shared/tags/tag-removal-flow.ts`
  und die dort genannten Bausteine aus `delete-flow.ts` lesen und **diese** Funktionen benutzen, keine Kopie. Der
  Dock-Anspruch (`beginConfirmedRun` / `endConfirmedRun` / `clearConfirmedRun`) und `startCheckPending` des Lösch-Dienstes
  folgen der Lösch-Kette; `startCheckPending` steht während der Phasen 2 und 3.
- **Vertrag Tracker (`WishRemovalTracker`):** nach 2.6. Er hält je angenommener Laufkennung Kanal, Wunsch-ID und das
  eingefrorene Tripel. Abschluss `done` → genau ein `complete-removal` mit `slotId`, `slotAddedAtUtc`, `emoteSetId`; sonst
  keines. 200 → fertig. 409/404 → endgültig, kein weiterer Versuch, Hinweis, Liste neu. Netzfehler/5xx/unbekannt →
  „Meldung ausstehend“ mit „Erneut melden“ (dasselbe Tripel).
- **Commits:** der erste bringt nur die drei Ergänzungen im Lösch-Dienst (alle Bestands-Specs der vier Lauf-Dienste, der
  Nutzungs- und der Tag-Seite unverändert grün), der zweite Fluss und Tracker.
- **Grenzfälle:** dieselbe Slot-ID und derselbe Alias mit neuerem `addedAt`, in beiden Antwortreihenfolgen (Wunsch-Antwort
  vor und nach der Set-Antwort): nichts gesendet; **der REMOVE bekommt ein 429, in der Wartezeit entfernt jemand den Eintrag
  und legt ihn unter demselben Alias neu an: der Read vor der Wiederholung zeigt die neue Generation, die Zeile endet ohne
  zweiten REMOVE, keine Meldung**; dasselbe mit einem Read, der jünger als 5 Sekunden wäre (er wird nicht wiederverwendet); **in der 429-Wartezeit wechselt
  der Kanal in 7TV auf Set B, der Eintrag liegt in A unverändert: kein zweiter REMOVE**; **dasselbe bei warmem
  60-Sekunden-Cache der Set-Liste** (der Read geht mit `refresh` und sieht B); **in der 429-Wartezeit wird das Token gelöscht
  (Abmelden): kein zweiter REMOVE**; das Token wird durch ein anderes ersetzt: kein Senden; **das Ziel liegt auf der letzten
  Seite eines mehrseitigen Sets und wird während der Verifikation der vorderen Seiten ersetzt: die Beobachtung ist beim Senden
  zu alt, der zweite Read zeigt die neue Generation, nichts gesendet**; der zweite Read ist ebenfalls zu langsam (ausgelassen);
  der Set-Read wird gedrosselt (ausgelassen);
  Eintrag umbenannt oder weg (Lauf überspringt); Datum vorläufig (gesperrt); Emote unter zwei Aliasen (nur der Eintrag des
  Wunsches geht); Antwort von 7TV geht verloren (erst das endgültige Ergebnis zählt); Abbruch mit nachträglich bestätigtem
  REMOVE (wird gemeldet); Abbruch ohne Bestätigung (keine Meldung); Start abgewiesen (nichts verfolgt); Bericht
  `sync-deleted` scheitert (das Dock bietet den Wiederholversuch; die Meldung hängt nicht daran); der Abgleich bucht vor der
  Meldung `removed_externally` (Meldung macht `removed`, Datum bleibt); in der Inventur liegt schon ein Ersatz oder der Wunsch
  hängt schon daran (409, kein Retry, Hinweis); Meldung 200 geht verloren (Wiederholung 200); Seitenwechsel während des Laufs;
  ein zweiter Lauf startet, bevor der erste gemeldet hat; Tab wird geschlossen (die Schließ-Sperre des Lösch-Laufs greift);
  geteiltes Set (Warnung).
- **Bekannte Restlücken:** bis 5 Sekunden plus Roundtrip zwischen Beobachtung und REMOVE (2.6, R16); Zuordnung einer nur
  durch Nachlesen festgestellten Abwesenheit (R20); beide in DECISIONS benannt, kein Test kann sie schließen.
- **AKs:** AK37, AK38 (Entfernen). **Tests:** siehe 5. **Commits:** `feat(web): report a delete run's outcome per run`,
  `feat(web): remove a wished emote from its row`

### T10: Settings-Abschnitt „Wünsche“

- **Ziel:** Kanal-Vorgaben ändern; im Bot-Modus für Unberechtigte nur lesen.
- **Dateien:** neu `features/channel-workspace/wish-settings-section.ts` (+ Spec); `channel-settings-page.ts`; Locales.
- **Vertrag:** Eingabe `channelName`. Lädt `GET …/settings`. Felder: Standardlaufzeit (1 bis 365) und Vorlauf der Probezeit (1
  bis 30), je mit Erklärsatz. Beide Grenzen werden vor dem Senden geprüft, mit Feldfehler (AK41). **Die Schutzschwelle wird in
  K3 nicht gezeigt [B] (F5) und im `PUT` nicht gesendet** (Sol-P2-4): Der Server lässt sie dann unberührt, gleich wer sie
  inzwischen geändert hat. Speichern ist gesperrt ohne Änderung und während der Anfrage; Erfolg bestätigt an Ort und Stelle
  (§4.5, Fokus bleibt). Der Abschnitt lauscht auf `wish.changed` und lädt neu, **aber nicht**, solange das Formular
  ungespeicherte Änderungen trägt (dann ein Hinweis „inzwischen geändert“ mit „Neu laden“).
- **Nur lesen [B] (F1 = A):** bei `canEdit = false` sind die Felder gesperrt und ein Satz erklärt, wer ändern darf (Text von
  `wish_settings_restricted`). **Zusätzlich immer:** Ein 403 `wish_settings_restricted` auf `PUT` schaltet in denselben Zustand.
- **Grenzfälle:** `botModeEnabled` wahr und `botSuspendedReason` gesetzt (K3 zeigt nur „Bot-Modus aktiv“ als Tatsache); 400
  `wish_settings_out_of_range`; Laden scheitert (Banner mit erneutem Versuch, keine Felder).
- **AKs:** AK41. **Tests:** siehe 5. **Commit:** `feat(web): wish settings section`

### T11: Seite `my-wishes`

- **Ziel:** Alle Wünsche mit Handlungsbedarf und in Probezeit über alle verwaltbaren Kanäle an einer Stelle.
- **Dateien:** neu `features/my-wishes/my-wishes-page.ts` (+ Spec nur, falls über T2 hinaus Entscheidungslogik entsteht);
  `app.routes.ts` (Route `my-wishes` als Geschwister von `my-votings`, lazy, mit `authGuard`; sie wird hier eingehängt, nicht
  in T3); Locales. Vorlage: `features/my-votings/my-votings-page.ts`. Der Link in der Kopfzeile folgt in T12.
- **Vertrag:** Zwei Abschnitte, „Handlungsbedarf“ vor „Probezeit“ (Gruppierung aus T2), je Zeile: Bild (2.10), Alias,
  Zustand, Grund, Restzeit bzw. „abgelaufen seit“, **der Kanal** (AK42). Jede Zeile ist ein Link in den Wünsche-Tab ihres
  Kanals **mit dem Sprungziel aus 2.4** (Parameter `wish`), sodass die Zeile dort auch dann erscheint, wenn sie auf keiner
  geladenen Seite steht (Stretched-Link-Vertrag §2.3); **keine Aktionen auf dieser Seite [E]**. Laden nach 2.4. Leerer Zustand: „nichts zu
  tun“ mit dem Grund. 404 `wishes_disabled` → Umleitung zur Übersicht.
- **AKs:** AK42. **Commit:** `feat(web): cross-channel wishes page`

### T12: Kopfzeile

- **Ziel:** Link „Wünsche“ mit Zahl, ohne die Kopfzeile bei 360 px zu sprengen.
- **Dateien:** neu `core/wishes/wish-summary.service.ts` (+ Spec); `features/shell/app-shell.ts`,
  `shared/ui/account-menu.ts`; `e2e/support/mocks.ts`; `e2e/audit/ui-audit.audit.ts`; Locales; DECISIONS.
- **Vertrag Dienst:** nach 2.4. Öffentlich: Zahl (Signal, `null` solange unbekannt), „verfügbar“ (Signal), `refresh()`.
- **Vertrag Shell:** Link nach `/my-wishes` neben „Abstimmungen“, nur für eingeloggte Nutzer und nur, wenn der Dienst
  „verfügbar“ meldet (AK40). Die Zahl steht am Link, bei 0 keine Zahl; sie ist kein zweites Status-Badge. Der zugängliche
  Name nennt beides („Wünsche, 3 mit Handlungsbedarf“); eine Änderung der Zahl wird nicht angesagt.
- **Schmale Breite [B] (F4):** Ziel ist Stufe 2: unter `sm` nur die Zahl als Link (Ziel mindestens 24 px, zugänglicher Name
  vollständig), sichtbar nur bei Zahl > 0, und der Eintrag „Wünsche“ zusätzlich im Konto-Menü, damit die Seite bei 0
  erreichbar bleibt; ab `sm` Wort und Zahl. Der Task misst zuerst (neues Audit-Szenario „Kopfzeile mit Zähler und einem
  aktiven Status-Badge“, de und en, alle Viewports). Besteht Stufe 2 die Gates nicht, gilt Stufe 3 (unter `sm` kein Element
  in der Kopfzeile, nur der Menüeintrag mit Zahl); passt wider Erwarten Wort plus Zahl überall, bleibt es trotzdem bei Stufe 2
  (entschieden). Stufe und Messwerte stehen im DECISIONS-Absatz. `h-14` und „ein Badge zur Zeit“ bleiben unberührt.
  **Stufe 3 ist eine ausdrückliche Abweichung von AK44** (der Zähler stünde bei 360 px nicht mehr in der Kopfzeile): Reicht
  Stufe 2 nicht, hält der Task an und meldet es mit den Messwerten zurück; Stufe 3 wird nur nach Zustimmung des Betreibers
  gebaut und dann in Spec und PR-Text als Abweichung geführt.
- **Sichtbarkeitsprüfung (Sol-R2-P3):** Die Dokument-Metriken des Harness sehen ein Element nicht, das ein Vorfahre
  beschneidet. Das Kopfzeilen-Szenario prüft deshalb bei 360 px, mit positivem Zähler **und** aktivem Status-Badge, in de und
  en ausdrücklich: Zähler-Link, Status-Badge und Konto-Auslöser liegen mit ihrer ganzen Fläche im Viewport **und** innerhalb
  jedes Vorfahren, der Überlauf beschneidet; Zähler und Badge sind nicht gekürzt (kein abgeschnittener Text); die Elemente
  überdecken einander nicht. Die Wortmarke darf wie bisher kürzen. Das ist ein eigenes Gate neben den Harness-Metriken.
- **E2E-Bestand:** `mockAuthMe` (mit Nutzer) registriert zusätzlich eine Vorgabe für `summary` (404 `wishes_disabled`), ebenso
  die Mocks des Audit-Harness (Befund 25). Wunsch-Specs überschreiben die Vorgabe (spätere Route gewinnt).
- **Grenzfälle:** anonymer Besucher (keine Abfrage); Abmelden (Abfragen enden, Zahl weg); 5xx beim ersten Abruf (kein Link,
  nächster Takt versucht es wieder); Fenster verborgen.
- **AKs:** AK39 (Zähler-Teil), AK40, AK44. **Tests:** siehe 5. **Commit:** `feat(web): wishes link with action count in the header`

### T13: Audit-Aktionen, i18n-Abschluss, Fehlercodes

- **Ziel:** Die Audit-Namen und AK43.
- **Dateien:** `core/audit/audit.model.ts`, `shared/audit/audit-actions.ts` (+ Spec: 34 Aktionen, 31 kanalbezogene), beide
  Locales, `core/i18n/api-error.ts` (nur falls die Gegenprobe eine Lücke zeigt).
- **Vertrag:** Elf Einträge in `AuditAction` und `ACTION_KEYS` mit Schlüsseln `audit.actions.wishCreate`, `wishAdopt`,
  `wishExtend`, `wishKeep`, `wishRemove`, `wishClose`, `wishRetry`, `wishReAdd`, `wishSettings`, `wishDue`, `wishUpdate`.
  Deutsche Benennung im Stil der Bestandszeilen (Partizip, ohne Emote-Namen): etwa „Wunsch nachgetragen“, „Bestand als Wunsch
  übernommen“, „Wunsch verlängert“, „Wunsch-Emote behalten“, „Wunsch-Emote entfernt“, „Wunsch geschlossen“, „Wunsch erneut
  versucht“, „Wunsch wieder aufgenommen“, „Wunsch-Einstellungen geändert“, „Wunsch fällig geworden“, „Wunsch bearbeitet“;
  englisch entsprechend. Alle elf sind kanalbezogen. `wish.due` trägt den Akteur „system“: prüfen, wie die Zeile „von system“
  liest, und nur falls nötig einen eigenen Akteur-Text einführen (dann für jeden Systemeintrag).
- **Gegenprobe AK43:** jede Konstante aus `ApiErrorCodes.cs` gegen `KNOWN_API_ERROR_CODES` halten (von Hand, Ergebnis im
  PR-Text; T0 bringt keinen neuen Code); `api-error-locales.spec.ts` grün. Wortlaut der zwölf `errors.api.wish_*` gegen die
  fertige Oberfläche lesen.
- **Abschluss i18n:** beide Locales auf verwaiste und fehlende `wishes.*`-Schlüssel prüfen; keine „…“ an Knöpfen.
- **AKs:** AK43. **Commit:** `feat(web): name wish actions in the audit views`

### T14: E2E und Audit-Harness

- **Ziel:** Die Abläufe über mehrere Seiten und die Layout-Gates. Inhalt in Abschnitt 6.
- **Dateien:** neu `web/e2e/wishes.e2e.spec.ts`; `web/e2e/support/mocks.ts` (Wunsch-Mocks: Listen je `view` mit steuerbarer
  Antwortreihenfolge, Detail, Befehle mit Aufzeichnung der Rümpfe, Einträge, Einstellungen, `mine`, `summary`);
  `web/e2e/channel-settings.e2e.spec.ts` (Kombinationen E19); `web/e2e/audit/ui-audit.audit.ts` (Szenarien: Kanal-Seite mit
  langen Namen in allen drei Abschnitten, `my-wishes`, Settings-Abschnitt, Nachtragen-Dialog; das Kopfzeilen-Szenario stammt
  aus T12; ein Szenario mit `prefers-reduced-motion` und Wünschen ohne `imageUrl`).
- **Fertig:** `npm --prefix web run e2e` grün (keine Api auf `:5151`), Audit-Harness: `horizontalOverflowPx` 0,
  `contrastViolations` leer, keine neuen `smallTargetsUnder24`. **Commit:** `test(web): e2e and audit scenarios for wishes`

### T15: Doku

- `docs/DECISIONS.md`: Eintrag vervollständigen (die Verträge aus 2.1a mit Begründung und „keine Migration“; Zeile statt
  Karte; Entfernen über den Lösch-Lauf samt Abschlussvertrag; Startvertrag; Laufart `wish`; Tracker; Gültigkeit und Aufholen
  der Listen; Zähler-Regeln; E19; die vier Punkte aus 2.8; kontextabhängige Fehlertexte; Kopfzeilen-Stufe; kein 7TV-Schreiben
  auf grobem Zeiger; Bildregel).
- `docs/UI-Designsprache.md` (englisch, nur Ist-Stand): §4.2 um den Sperrgrund per Beschreibung (2.7), §8.7 um das Dock der
  Wunsch-Seite, §2.5 um den 7TV-Link auf grobem Zeiger; `DESIGN.md` nur, falls ein Token oder eine benannte Regel betroffen ist.
- Spec `2026-11-17-wunsch-emotes-spec.md`: K1-Formen (DTO-Felder, `complete-removal` mit Rumpf und Tabelle, `adopt` mit
  erwarteter Generation, die hingenommene Lücke zwischen Read und REMOVE in Abschnitt 16, 409-Rümpfe,
  `GET /slots`, `PUT /settings` mit optionaler Schwelle, `canEdit`), Abschnitt 5 (Zeile `removed_externally` → `removed`:
  `EndedAtUtc` bleibt), AK38-Wortlaut, K3-Dateipfade, Abschnitt 9 dieses Plans als Fußnoten.
- `docs/Feature-Ideen-2026-08-01.md` (Statuszeile A18: K3), `CLAUDE.md` (Zeile „Wunsch-Emotes“ im Umsetzungsstand),
  `docs/Operations.md` nur, falls ein Betreiber-Handgriff entsteht (keiner erwartet).
- **Commit:** `docs: record wish emotes manager ui`

---

## 4. AK-Abdeckung

| AK | Tasks | AK | Tasks |
|---|---|---|---|
| AK32 Tab und Route | T1, T3 (Guard), T4 (Route, Tab), T14 (E2E 4) | AK39 Live und Zähler-Takt | T4, T12, T14 (E2E 5, 6) |
| AK33 Handeintrag | T2, T6, T14 (E2E 1) | AK40 Zähler-Anzeige | T12, T14 (E2E 6) |
| AK34 409 → Übernehmen | T0a, T0b, T6, T14 (E2E 2) | AK41 Settings-Tab | T0a, T0b, T3, T10, T14 (E2E 7) |
| AK35 Aktionen je Zustand | T2, T4, T7 | AK42 `my-wishes` | T2, T11, T14 (E2E 6) |
| AK36 Probezeit | T2, T4 | AK43 Fehlercodes | T13 |
| AK37 Entfernen mit Bindung | T0a, T0b, T9, T14 (E2E 3) | AK44 360 px | T12 (Sichtbarkeitsprüfung), T14 (Audit) |
| AK38 Sperre mit Grund | T8, T9, T4, T14 (E2E 8) | AK45 Live-Probe | T0c, T16 (Stufe B und echtes Handy Pflicht) |

Punkte des Auftrags: 1 → T4 (`unconfirmed_start`), T13 (Audit) · 2 → T2 (2.9), T6 · 3 → T1, T3 · 4a → T0b, T6 · 4b → T6 ·
4c → T4 · 4d → T0b · 5 → 1.3.

---

## 5. Specs nach Regel 12 (Verhalten, nicht Vorlage)

Vitest, co-located. Nie CSS-Klassen, Tags, Verschachtelung, Schnappschüsse oder Wortlaut als Selbstzweck. Signal-Eingaben über
`setInput`. Zeit über Vitest-Fake-Timer bzw. den Parameter „jetzt“. (Die Backend-Tests nach Regel 11 stehen in T0a und T0b.)

| Datei | Was geprüft wird |
|---|---|
| `wish.service.spec.ts` (`HttpTestingController`) | je Methode: Verb, Pfad, Query, Rumpf unverändert durchgereicht (auch das Tripel von `completeRemoval` und `expectedAlias`/`expectedAddedAtUtc` von `adopt`, die Daten wortgleich, `null` bei undatiert); Antwort unverändert zurück; `setFull` kommt an; die 409-Rümpfe sind lesbar; Fehler werden nicht verschluckt |
| `wish-view.spec.ts` | Aktionstabelle als ausgeschriebene Tabelle über alle dreizehn Zustände mal die Bedingungen (Slot, Ablauf, Grund, Modus), Erwartung im Test; unbekannter Zustand/Grund; Handlungsbedarf gegen die Serverregel; Probezeit nur aus `probationStartsAtUtc` (AK36); Restzeit an den Kanten; „Entfernen möglich“ je fehlendem Feld; Gruppierung für `my-wishes` stabil, Unbekanntes geht nicht verloren |
| `wish-input.spec.ts` | Link-Formen und nackte IDs (gültig), Emote-Name und Unsinn (ungültig); Login-Normalisierung und Grenzen 3/4/25/26; Laufzeit 0/1/365/366, Dezimal, leer; Vorlauf 0/1/30/31; Alias-Grenzen nach Codepunkten |
| `wish-error.spec.ts` | `emote_ids_invalid` → Wunsch-Schlüssel; `invalid_request_body` je Kontext; jeder andere Code und jeder Status ohne Rumpf → identisch zu `apiErrorTranslationKey` |
| `channel-wishes.guard.spec.ts`, `channel-settings.guard.spec.ts` (`runInInjectionContext`) | Matrix aus `canManage` × beide Schalter; nicht eingeloggt → Login mit gemerkter URL; Fehler der Rechteabfrage → Rückfall; fehlender Kanalname |
| `channel-workspace-layout.spec.ts` | Tab „Wünsche“ und Settings-Tab in den Schalter-Kombinationen (über die Rolle „Link“ mit Namen), Fehlerfall blendet beide aus |
| `wish-row.spec.ts` | rendert genau die Aktionen der Tabelle als Knöpfe mit zugänglichem Namen; gibt die gewählte Aktion aus; ★-Aktionen fehlen auf grobem Zeiger; gesperrter Knopf ist `aria-disabled`, beschreibt sich über die Seitenzeile und gibt bei Klick nichts aus; unbekannter Grund → neutrale Zeile; Probezeit-Kennzeichen |
| `wish-image.spec.ts` (**Sol-R2-P3**) | `imageUrl` da → genau diese; `null` und „nicht animiert“ aus dem Einzel-Read bekannt → die einfache Fassung; `null` und nichts bekannt → **nur** die Standbild-Fassung; scheitert sie (einmal kurz oder dauerhaft) → leere Fläche, **nie** eine Anfrage an die einfache Fassung |
| `channel-wishes-page.spec.ts` (**Sol-P2-8, Sol-R2-P2-5, R2-P2-6**) | **vertauschte Antworten:** die ältere von zwei Antworten derselben Ansicht trifft zuletzt ein und wird verworfen; Antwort von Seite 1 nach dem Wechsel auf Seite 2 verworfen; Antwort des vorigen Kanals verworfen; eine verworfene Fehlerantwort setzt kein Banner. **Aufholen:** ohne `wish.changed` lädt die Seite bei `channel.synced`, beim Wieder-Öffnen **ihres** Streams, beim Sichtbarwerden und nach 5 Minuten neu; **zwei Seiten an zwei Streams:** öffnet nur Stream A neu, lädt nur Seite A; ein Wieder-Öffnen eines fremden Streams lädt nichts. **Sprungziel:** Parameter `wish` mit geladener Zeile → Fokus dort; mit ungeladener → angeheftete Zeile mit ihren Aktionen; unbekannte ID → Hinweis. Außerdem: `wish.changed` lädt `open` und `running`, `ended` nur wenn geöffnet; ein Schwall → ein Abruf; eigener Befehl lädt sofort; 409/404 lädt neu und zeigt den Text; Behalten/Schließen senden erst nach Bestätigung; Fehler eines Abschnitts lässt die anderen stehen |
| `wish-usage.spec.ts` (**Sol-R2-P2-6**) | lädt erst bei Sichtbarkeit; unveränderter Schlüssel **innerhalb** der Ablaufzeit → kein neuer Abruf; **geänderte Nutzung bei gleichem Schlüssel: nach Ablauf der 5 Minuten lädt die sichtbare Zeile neu und zeigt die neuen Zahlen**; geänderter Schlüssel (auch nur `slotAddedAtUtc`) → Abruf sofort; Wieder-Öffnen des Streams und Sichtbarwerden lassen alle Werte verfallen; eine unsichtbare Zeile lädt nach Ablauf nicht; der alte Wert bleibt während des Neuladens und bei dessen Fehler stehen; die Linie bekommt genau `usage.daily`; kein Abruf von `usage-stats/daily` |
| `seven-tv-emote-lookup.spec.ts` | die vier Ergebnisse, je mit der angefragten ID; verkleidetes 429; Zeitlimit → „nicht erreichbar“; „gefunden, nicht animiert“ ist für die Sitzung je ID abrufbar, „animiert“ und „unbekannt“ geben nichts frei |
| `live-update.service.spec.ts`, `live-reload.spec.ts` (Ergänzung) | Meldung „erneut geöffnet“ je Stream-URL: nicht beim ersten Öffnen, einmal je Wiederverbindung, nur für die betroffene URL; Bestandsverhalten und dienstweiter Status unverändert |
| `wish-entry-dialog.spec.ts` (**Sol-P2-10**) | **Wechsel der ID während der Entprellung:** die Antwort des alten Reads setzt weder Alias noch Vorschau; **Wechsel während der Anfrage:** ebenso; **zurück auf die alte ID:** nur ein Ergebnis für die aktuelle ID zählt; automatisch gesetzter Alias verschwindet sofort beim Wechsel, getippter bleibt; „Read läuft“ und „unbekannt“ sperren das Senden mit Grund; „nicht erreichbar“ sperrt nicht und lässt `emoteName` weg; der gesendete Rumpf trägt ID und Namen desselben Reads. Außerdem: Laufzeit mit Kanal-Vorgabe vorbelegt; Sperrgründe je Feld samt ARIA-Bezug; Login normalisiert im Rumpf; die Ausgänge nach `wish_emote_already_in_set` aus der `wishId` des Rumpfs (kein Blick in Listen); `wish_already_running` je Zustand; Dialog-Rückgaben |
| `wish-adopt-dialog.spec.ts` (**Sol-R2-P2-4, R2-P2-5**) | Einträge kommen aus dem Wunsch-Leseweg; Vorwahl schränkt ein; unbekannter vorgewählter Eintrag ist nicht wählbar; die drei Arten: frei → neuer Wunsch; **gezielt verknüpfen → `adopt` mit der `runningWishId`**, Laufzeit dieses Wunsches vorbelegt; **gesperrt** (Halter, `kept`, nicht übernehmbarer Lauf-Wunsch) → nicht wählbar, Grund genannt, „Zum Wunsch“ gibt die ID zurück; gesendeter Rumpf trägt `expectedAlias` und `expectedAddedAtUtc` des gewählten Eintrags (`null` bei undatiert, dann nur „ab jetzt“); Vorgabe „ab jetzt“ bei vergangenem Ablauf; **veralteter Stand:** jede der fünf Ablehnungen lädt die Einträge neu, hebt die Auswahl auf und sendet nichts von selbst erneut; nach dem Neuladen ist der vormals wählbare Eintrag gesperrt bzw. trägt das neue Datum |
| `wish-edit-dialog.spec.ts`, `wish-readd-dialog.spec.ts` | nur geänderte Felder im Rumpf; ohne Änderung gesperrt; Modus „dauerhaft“ sendet keine Laufzeit |
| `wish-run-start.spec.ts` (**Sol-P2-3, Sol-R2-P2-3, Sol-R3**) | der autoritative Read des aktiven Sets wird benutzt, immer mit `refresh`, nie der Postgres-Stand; ein gedrosselter Read heißt „kein Start“; 3b ist synchron: je Punkt (i) bis (v) ein Fall mit dem richtigen Abbruchgrund und ohne Schreiben; **der letzte Read hängt, währenddessen wechselt die Seite → nach dem Read Abbruch**; **der letzte Read hängt, währenddessen startet ein Bestandslauf bzw. meldet sein `startCheckPending` → Abbruch**; verzögerter Vorab-Check mit konkurrierendem Start; währenddessen ändert sich der Wunsch (Zustand; Alias; Slot; Generation) oder das Set → Abbruch; frischer Read scheitert → Abbruch; `startCheckPending` steht während der Phasen 2 und 3 und ist nach jedem Ausgang frei; unveränderte Lage → Start im selben Zug mit genau den eingefrorenen Werten |
| `wish-add.service.spec.ts` (mit `seven-tv-mutation.testing.ts`) | registriert sich beim Arbiter; gesendete Variablen (Set, Emote, Alias); Ergebniszustände je Antwort (Erfolg, 409, Transportverlust → unbekannt, nicht gestartet mit Grund); **vor jedem Senden, auch nach einem 429: Wunsch, aktives Set (mit `refresh`) und Token-Gleichheit; ändert sich eines, scheitert ein Read oder ist das Token gelöscht, geht kein (weiterer) Schreibversuch hinaus**; unverändert → genau eine Wiederholung; kein Aufruf an die eigene Api nach Erfolg; Ergebnis verfällt, sobald der Wunsch nicht mehr `approved` ist |
| Bestands-Specs der Lauf-Dienste, der Nutzungs- und der Tag-Seite | unverändert grün nach dem Zusammenziehen der Mutation und nach dem Abschlussvertrag |
| `seven-tv-delete.service.spec.ts` (Ergänzung, **Sol-P2-7, Sol-R2-P1-1, Sol-R3**) | angenommener Start liefert eine Kennung, abgewiesener keine; je Kennung genau eine Abschlussmeldung mit dem endgültigen Zeilenergebnis, auch wenn inzwischen ein anderer Lauf gezeigt wird; Abbruch mit REMOVE unterwegs, das Nachlesen bestätigt das Entfernen → Abschluss `done`; Abbruch ohne Bestätigung → nicht `done`. **Merkmal „Wunsch-Bindung“:** zwei Versuche derselben Zeile binnen weniger als 5 Sekunden → zwei Set-Reads; REMOVE bekommt 429, der Read vor der Wiederholung zeigt denselben Alias mit neuer Generation → kein zweiter REMOVE; **429, danach meldet die Set-Prüfung ein anderes aktives Set, der Eintrag liegt unverändert im alten → kein zweiter REMOVE**; **429, danach ist das Token gelöscht bzw. ein anderes → kein zweiter REMOVE**; **Ziel auf der letzten Seite, Read dauert länger als 5 Sekunden → genau ein zweiter Read; zeigt er eine neue Generation oder ist auch er zu alt → kein REMOVE**; Set-Prüfung gedrosselt oder gescheitert → kein REMOVE. Ohne das Merkmal: Zahl und Reihenfolge der Reads und Anfragen exakt wie vor der Änderung |
| `wish-removal-flow.spec.ts` (**Sol-P1-1, Sol-R2-P1-1**) | Ziel = Slot, Alias, Generation des eingefrorenen Wunsches, Datum wortgleich, Set = eingefrorenes Set, Merkmal „Wunsch-Bindung“ mit Set, Token-Vergleichswert und Prüfung gesetzt; Sperrgründe (kein Slot, keine Generation, Datum vorläufig oder ungültig); dieselbe Slot-ID und derselbe Alias mit neuerer Generation im frischen Wunsch → kein Start, in beiden Antwortreihenfolgen; Bestätigung nennt Alias, Datum, Set; Abbruch der Bestätigung startet nichts und gibt Dock-Anspruch und `startCheckPending` frei |
| `wish-removal-tracker.spec.ts` (**Sol-P1-2, P2-7**) | Abschluss `done` → genau ein `complete-removal` mit dem eingefrorenen Tripel, auch bei wiederholten Signalwechseln; jeder andere Abschluss → keines; 200 → fertig; **409 und 404 → endgültig, kein weiterer Versuch**, Hinweis gesetzt; Netzfehler/5xx → „ausstehend“, „Erneut melden“ sendet dasselbe Tripel noch einmal; abgewiesener Start wird nicht verfolgt; **Seitenwechsel**: die Meldung geht trotzdem hinaus; zwei Läufe nacheinander vermischen ihre Tripel nicht |
| `wish-settings-section.spec.ts` (**Sol-P2-4**) | Grenzen vor dem Senden mit Feldfehler; **der `PUT`-Rumpf enthält die Schwelle nicht**; `canEdit = false` und 403 führen in denselben Nur-Lesen-Zustand mit Erklärung; Live-Neuladen überschreibt keine ungespeicherte Eingabe |
| `wish-summary.service.spec.ts` | keine Abfrage ohne Nutzer; erste Abfrage, dann alle 5 Minuten; verborgen → Pause, sichtbar → sofort; 404 `wishes_disabled` → nicht verfügbar und keine weitere Abfrage; anderer Fehler behält den letzten Wert; `refresh()` löst genau eine aus; ruft `/api/channels/mine` nie |
| `audit-actions.spec.ts` | 34 Aktionen, 31 kanalbezogene, jede in beiden Locales |
| `api-error-locales.spec.ts` | unverändert grün |

Keine Specs für: `my-wishes-page` (Vorlage plus T2-Regel), die reine Darstellung der Nutzung, die Shell-Vorlage.

---

## 6. E2E-Fälle (Playwright)

`web/e2e/wishes.e2e.spec.ts`, Import von `test`/`expect` aus `web/e2e/support/test.ts`, `/api/**` je Fall gemockt, 7TV über
`mockSevenTvGql`/`mockSevenTvSetBackend`, Live über `installLiveStub`/`emitLive`. **Nur ohne Api auf `:5151`.** Zeitfälle:
`page.clock.install()` **vor** `goto`, danach `runFor(...)`, nie `fastForward(...)`; `install()` hält die Uhr nicht an (wo
Echtzeit keinen Timer auslösen darf, vorher `pauseAt`). Port 4300 per `flock` serialisieren.

1. **Nachtragen bis Zeile:** Dialog öffnet mit der Kanal-Vorgabe als Laufzeit; Link eingeben, Read liefert Namen, Alias ist
   vorbelegt; Login mit Großbuchstaben; gesendeter Rumpf trägt den normalisierten Login; neue Zeile steht in
   „Handlungsbedarf“. Laufzeit 0 sperrt den Knopf mit sichtbarem Grund (AK33). **Dazu:** Link A eingeben, vor der Antwort auf
   Link B wechseln, die Antwort für A trifft danach ein → Alias und Vorschau gehören zu B, der Rumpf trägt B (Sol-P2-10).
2. **409 öffnet Übernehmen:** `wish_emote_already_in_set` mit zwei `slotIds` und `wishId: null` → der Dialog zeigt genau diese
   zwei Einträge aus `GET …/slots`; Übernehmen sendet `slotId`; Zeile erscheint in „Laufend“ (AK34). **Dazu:** einer der
   gemeldeten Einträge fehlt in den Nutzungs-Summen des Kanals und steht trotzdem zur Wahl (die Quelle ist der Wunsch-Leseweg,
   Sol-P2-6). **Dazu:** das 409 nennt eine `wishId`, der Wunsch steht auf **keiner** geladenen Seite (Seite 2 von „Laufend“) →
   Hinweis mit „Verlängern“, kein Übernehmen (Sol-P2-5). **Dazu:** `adopt` antwortet `wish_slot_already_linked` mit der
   `wishId` eines `kept`-Wunsches → eigener Hinweis ohne „Verlängern“. **Dazu (Sol-R2-P2-4):** Zwischen dem Laden der
   Einträge und dem Senden wird die Slot-ID mit neuem Datum wiederverwendet; `adopt` antwortet 404 → die Einträge werden neu
   geladen, die Auswahl ist aufgehoben, der Eintrag zeigt das neue Datum, und erst eine **erneute** Auswahl sendet (mit dem
   neuen `expectedAddedAtUtc`). **Dazu (Sol-R2-P2-5), beide Wege:** (a) ein freier Eintrag, zu dessen Emote ein
   `approved`-Wunsch läuft → Auswahl sagt „wird verknüpft“, der Rumpf trägt dessen `wishId`, die Zeile wird „aktiv“;
   (b) ein Eintrag, zu dessen Emote ein Wunsch an einem anderen Eintrag läuft → nicht wählbar, „Zum Wunsch“ führt zur
   Kanal-Seite mit `wish` im Query, und die Zeile erscheint angeheftet, obwohl sie auf Seite 2 von „Laufend“ steht.
   (c) Veralteter Konflikt: der „wird verknüpft“-Eintrag ist beim Senden schon `active` → 409, neu laden, Eintrag ist jetzt
   gesperrt, nichts wurde automatisch erneut gesendet.
3. **Entfernen mit gemocktem 7TV** (feiner Zeiger): vorgelegter Wunsch mit Slot und Generation; Bestätigen; der frische Read
   passt → genau eine Entfernen-Mutation mit Alias, danach genau ein `complete-removal` mit Slot, Generation und Set; Zeile
   wandert nach „Beendet“ (AK37). **Gegenfälle, je ohne Mutation und ohne `complete-removal`, die Zeile nennt den Grund:**
   (a) der Eintrag liegt unter anderem Alias; (b) **derselbe Alias und dieselbe Slot-ID, aber neueres `addedAt`: die Antwort
   des frischen Wunsches trifft vor der Set-Antwort ein;** (c) **dasselbe mit vertauschter Antwortreihenfolge** (Sol-P1-1).
   **Dazu:** `complete-removal` antwortet 409 → kein zweiter Versuch, Hinweis, Liste neu; antwortet erst mit Netzfehler, dann
   200 → „Erneut melden“ sendet denselben Rumpf (Sol-P1-2). **Dazu (Sol-R2-P1-1):** Der REMOVE bekommt ein 429; in der
   Wartezeit liefert der 7TV-Mock denselben Alias mit neuerem `addedAt`; vor der Wiederholung liest der Lauf erneut →
   insgesamt genau **ein** REMOVE, kein zweiter, kein `complete-removal`, die Zeile nennt den Grund. Gegenprobe: 429, Set
   unverändert → genau ein zweiter REMOVE, dann die Meldung. **Dazu (Sol-R3):** 429, in der Wartezeit meldet die Set-Liste
   (mit `refresh` angefragt, der Mock prüft den Parameter) ein anderes aktives Set, der Eintrag liegt im alten weiter → kein
   zweiter REMOVE; 429, in der Wartezeit wird das 7TV-Token entfernt → kein zweiter REMOVE, die Zeile nennt den Grund.
4. **Ohne Schalter kein Tab:** `wishesEnabled: false` → kein Tab „Wünsche“, `/channels/x/wishes` landet auf den Abstimmungen,
   und keine Wunsch-Route wird angefragt; ebenso mit Schalter, aber ohne `canManage` (AK32).
5. **Live, Gültigkeit und Nutzung:** `emitLive` mit `wish.changed` → die Listen werden erneut angefragt und die geänderte Zeile erscheint
   ohne Reload (AK39). **Dazu:** zwei Abrufe kurz nacheinander, die ältere Antwort wird zuletzt geliefert → die Seite zeigt
   den jüngeren Stand. **Dazu:** kein `wish.changed`, aber `channel.synced` → die Listen werden trotzdem neu geladen
   (Sol-P2-8). **Dazu (Sol-R2-P2-6), mit `page.clock`:** eine sichtbare Zeile zeigt Summe 10; der Mock liefert danach 25 bei
   unverändertem Wunsch; nach 5 Minuten (`runFor`) wird das Detail erneut angefragt und die Zeile zeigt 25.
6. **Kopfzeile, Zähler, Übersicht:** `summary` mit 3 → Link „Wünsche“ mit Zahl; mit `page.clock` nach 15 Minuten genau drei
   weitere `summary`-Anfragen, kein Aufruf von `/api/channels/mine` durch den Zähler, kein zusätzlicher Stream; `summary` mit 0
   → Link ohne Zahl; 404 `wishes_disabled` → kein Link (AK39, AK40). Klick führt nach `/my-wishes`: Handlungsbedarf vor
   Probezeit, je Zeile der Kanal, Klick auf eine Zeile öffnet den Tab des Kanals mit der Zeile im Fokus bzw. angeheftet
   (AK42). **Dazu (Sol-P3-13, Sol-R2-P3):** mit `prefers-reduced-motion` und Wünschen ohne `imageUrl` beantwortet der
   CDN-Stub die Standbild-Anfrage einmal mit einem Fehler (`route.abort('failed')`, nie `namenotresolved`): Die Bildfläche
   bleibt leer, und über den ganzen Fall geht **keine einzige** Anfrage an eine Fassung, die sich bewegen kann. Dazu eine
   eigene `page.route` für das CDN, die jede Anfrage aufzeichnet und über `fulfillCdnStub` antwortet.
   **Kopfzeile bei 360 px (AK44, Sol-R2-P3):** mit Zähler 3 und aktivem Status-Badge die Sichtbarkeitsprüfung aus T12.
7. **Settings-Tab, vier Kombinationen** (in `channel-settings.e2e.spec.ts`): nur Backfill → Tab mit Backfill allein; nur
   Wünsche → Tab mit „Wünsche“ allein; beide → beide, „Wünsche“ darunter; beide aus → kein Tab, Umleitung, keine
   Status-Anfrage. Dazu: Vorlauf 31 wird nicht gesendet und zeigt den Feldfehler; `canEdit: false` zeigt den Abschnitt
   schreibgeschützt mit Erklärung; **der gesendete `PUT`-Rumpf enthält keine Schwelle** (AK41, Sol-P2-4).
8. **Sperre und Startvertrag** (feiner Zeiger): Solange die Antwort der Hinzufügen-Mutation zurückgehalten wird, sind
   „In 7TV hinzufügen“ und „Entfernen“ auf den anderen Zeilen `aria-disabled` und tragen die Beschreibung mit dem Namen des
   Vorgangs (AK38). **Dazu:** der Vorab-Check wird zurückgehalten, währenddessen meldet der frische Wunsch `closed` → keine
   Mutation, die Zeile nennt den Grund (Sol-P2-3). **Dazu (Sol-R2-P2-3):** der letzte Read des Wunsches wird zurückgehalten,
   währenddessen wechselt der Nutzer den Tab der Kanal-Seite; nach der Antwort geht keine Mutation hinaus. Und: Das aktive Set
   laut autoritativem Read ist ein anderes als das der Kanalzeile → kein Start, mit Grund.

---

## 7. Live-Verifikation (T0c und T16; AK45, Regel 16)

**Randbedingungen [B].** Kein `ssh`, kein Prod. **Nie `docker compose` aus dem Worktree.** Die Dev-Datenbank `emotepurge` wird
nur gelesen (Dump); gearbeitet wird auf einer **Kopie**. Ports mit anderen laufenden Sitzungen abstimmen; die E2E-Gate läuft
erst, wenn diese Api wieder beendet ist. **Der Orchestrator fährt beide Stufen selbst.** Er bekommt vom Betreiber das
Session-Cookie des Testkontos und das 7TV-Token des Testkontos; beide liegen je in einer Scratchpad-Datei mit Modus 600, werden
**nie** ausgegeben (kein `cat`, kein Echo, kein Log, kein Protokoll, kein Screenshot mit sichtbarem Token-Feld), nur von den
Skripten gelesen, und **nach dem Test gelöscht** (Löschung im Protokoll vermerken). Das Testkonto `olaf_olaf_son` darf in 7TV
schreiben; **das Set wird danach wiederhergestellt** (Vergleich vorher/nachher nach Set-ID, Emote-ID, Alias und `addedAt`).
Alle Zahlen sind Dev-Zahlen. **Stufe B ist Pflicht; eine Morgenliste ersetzt sie nicht** (Abschnitt 8).

| Ressource | Wert |
|---|---|
| Datenbank | Kopie unter eigenem Namen (Form `emotepurge_wish485`) im Dev-Postgres-Container; `dotnet ef migrations list` gegen die Kopie zeigt nichts Ausstehendes (`AddWishes` angewendet, K3 bringt keine Migration) |
| Redis | eigener Container per `docker run` auf abgestimmtem Port |
| Api | `dotnet run` mit absolutem Projektpfad aus dem Worktree auf `:5151`, `Wishes__Enabled=true` (Compose-Name `WISHES_ENABLED`), Verbindungszeichenfolgen auf Kopie und eigenen Redis |
| Worker | ebenso, `Wishes__Enabled=true`, `Wishes__ExpiryIntervalMinutes=1`, `SevenTv__EventApi__Enabled=false` (für Schritt B4 einmal `true`) |
| Web | `npm --prefix <Worktree>/web start` → `http://localhost:4200` (Proxy `/api` → `:5151`) |
| Browser des Orchestrators | Node-Skript mit dem Playwright aus `web/node_modules` (kein Playwright-MCP), feiner Zeiger; Cookie aus der Datei in den Kontext gesetzt; das Token tippt das Skript aus der Datei in den Token-Dialog der Oberfläche |
| **URL für den Betreiber** | `http://localhost:4200/channels/olaf_olaf_son/wishes` (Ports 4200 und 5151 per VS-Code-Weiterleitung; Login über `http://localhost:5151/api/auth/twitch/login`). Dazu `http://localhost:4200/my-wishes` und `…/channels/olaf_olaf_son/settings`. Der Betreiber schaut sich die Oberfläche zusätzlich selbst an; das ersetzt keinen Schritt unten |

**Testdaten, getrennt je Zweck** (alle Test-Emotes liegen vorher nicht im Set; IDs wie in T12 plus weitere, die der
Orchestrator vorab wählt und im Protokoll nennt). **Kein Bestands-Eintrag des Sets wird in 7TV entfernt, umbenannt oder neu
angelegt** (Sol-R2-P2-7): Bestands-Einträge werden nur übernommen, verlängert, behalten, also allein in der Datenbankkopie
geführt; jede 7TV-Schreibprobe läuft an einem eigens angelegten Test-Eintrag. **Ausgangsbestand:** vor dem ersten Schritt die
Set-ID und je Eintrag Emote-ID, Alias und `addedAt` aus einem 7TV-Read festhalten (Scratchpad).

| Kürzel | Emote | Anlage | Zweck |
|---|---|---|---|
| W-S | Test-Emote 1 | Nachtragen (A2) | wird **geschlossen** (A6), sonst nichts |
| W-B | Test-Emote 2 | Nachtragen (A2) | **Schreiben im Browser**: Hinzufügen (B1), Bindungsprobe und Entfernen (B2), Wieder hinzufügen dauerhaft (B3) |
| W-D | Test-Emote 3 | Nachtragen (A2) | **direkter 7TV-Weg**: von Hand in 7TV hinzufügen und entfernen (B4) |
| W-Ü1 | Bestands-Eintrag 1 (seit Wochen im Set) | Übernehmen, „ab Hinzufügedatum“, Laufzeit 7 (A4) | sofort überfällig → vorgelegt; Verlängern (A5); am Ende Behalten (A6) |
| W-Ü2 | Bestands-Eintrag 2 | Übernehmen, „ab jetzt“, Laufzeit 2 (A5) | Probezeit ab Start; wird in 7TV **nie** angefasst |
| W-G | Test-Emote 4, **eigens für B5 in 7TV angelegt** | von Hand in 7TV hinzufügen, nach dem Sync „Bestand übernehmen“ (B5) | Bindungsprobe gegen eine neue Generation; wird am Ende gelöscht |

**Stufe A, ohne 7TV-Schreiben:**

- **A1** Schalter aus (`Wishes__Enabled=false`): kein Tab, kein Link in der Kopfzeile, `/channels/…/wishes` leitet um; im
  Netzwerk-Mitschnitt genau **eine** `summary`-Anfrage mit 404 und danach keine weitere über sechs Minuten (AK32, AK40). Dann
  mit `true` neu starten: Tab und Link sind da.
- **A2** Nachtragen von W-S, W-B, W-D: Vorschau zeigt Bild und Namen, Laufzeit ist die Kanal-Vorgabe, bei W-B Login
  „TestViewer“ → drei Zeilen „freigegeben“ in „Handlungsbedarf“, Login klein geschrieben; Kopfzeile zählt 3 (AK33, AK40).
- **A3** Nachtragen des Emotes von W-B erneut → Hinweis „läuft bereits“ mit „Bearbeiten“; Bearbeiten ändert die Laufzeit auf 10.
- **A4** Nachtragen des Bestands-Emotes 1 → Übernehmen-Dialog mit dem gemeldeten Eintrag, „ab jetzt“ vorgewählt mit
  Begründung; stattdessen „ab Hinzufügedatum“ und Laufzeit 7 → W-Ü1 „aktiv“, sofort „abgelaufen“; nach höchstens einem Takt
  „vorgelegt“ mit Grund „abgelaufen“, **ohne Reload**; Kopfzeile +1; `/my-wishes` nennt Zeile und Kanal unter
  „Handlungsbedarf“ (AK39, AK42, AK45). Nachtragen dieses Emotes jetzt → Hinweis mit „Verlängern“ statt Übernehmen (2.8 a).
- **A5** W-Ü1 zeigt Summe, Mittel, Rang und eine Linie, deren Tageswerte zusammen die Summe ergeben (gegen `GET /{id}`
  gelesen); Verlängern → „aktiv“ mit neuem Ablauf. W-Ü2 anlegen → sofort „Probezeit“ (AK36), in `/my-wishes` unter „Probezeit“.
- **A6** W-S schließen (mit Bestätigung), W-Ü1 behalten (mit Bestätigung) → beide in „Beendet“ mit Datum. Aktivitäts-Tab und
  `/admin/audit-log` zeigen die `wish.*`-Einträge mit übersetzten Namen in de und en, `wish.due` mit Akteur „system“.
- **A7** Settings: Vorgabe auf 14, Vorlauf auf 3, speichern, neu laden → Werte stehen; der Nachtragen-Dialog belegt 14 vor;
  Vorlauf 31 wird nicht gesendet. Vorher per `curl` eine Schwelle setzen (T0c Schritt 7): Nach dem Speichern in der
  Oberfläche steht die Schwelle unverändert in der Antwort von `GET …/settings`. **Handgriff an der Kopie** (im Protokoll
  markieren): `BotModeEnabled` mit fremdem Aktivierenden setzen. Das Testkonto ist Broadcaster seines Kanals und darf
  weiterhin ändern; der Nur-Lesen-Zustand bleibt bei den Tests, sofern keine zweite Sitzung vorliegt. Handgriff zurücknehmen.
- **A8** Audit-Harness-Bilder der neuen Szenarien in de und en, dunkel und hell, ansehen (lange Namen, 360 px).

**Stufe B, mit 7TV-Schreiben (Pflicht):**

- **B1** W-B „In 7TV hinzufügen“: Token-Dialog (aus der Datei getippt), danach „hinzugefügt, wartet auf den Sync“; nach dem
  nächsten Vollsync (bis ~90 s) wird die Zeile „aktiv“ mit Startdatum aus 7TV, ohne Reload (AK45 Teil 1). Während des Laufs
  sind die 7TV-Knöpfe der anderen Zeilen gesperrt und nennen den Grund (AK38). Gegenprobe in 7TV (Read): Eintrag liegt im Set.
- **B2** W-B in 7TV von Hand umbenennen, **vor** dem nächsten Sync „Entfernen“ → nichts gesendet, die Zeile meldet die
  Abweichung, der Eintrag liegt weiter im Set (AK37). Nach dem Sync (Alias nachgezogen) „Entfernen“ → Bestätigung nennt Alias,
  Datum, Set; Dock zeigt den Lauf; Zeile endet in „Beendet“ als „entfernt“; im Audit ein `wish.remove` und ein
  `emotes.syncDeleted`; in 7TV ist der Eintrag weg (AK45 Teil 3).
- **B3** W-B „Wieder hinzufügen“ (dauerhaft) → „freigegeben“; „In 7TV hinzufügen“ → nach dem Sync „behalten“.
- **B4** W-D direkt in 7TV hinzufügen (ohne Emote Purge), einmal mit EventAPI an: Zeile wird „aktiv“, zunächst „vorläufig“
  (Entfernen gesperrt mit Grund), nach dem Vollsync bestätigt. Direkt in 7TV entfernen → „extern entfernt“ mit dem Datum des
  Verlassens.
- **B5 Bindung gegen eine neue Generation (Sol-P1-1/P1-2 live), nur am eigens angelegten W-G:** Test-Emote 4 von Hand in
  7TV hinzufügen, Sync abwarten, „Bestand übernehmen“ → W-G „aktiv“ mit bestätigtem Datum. An der Zeile „Entfernen“,
  Bestätigungsdialog **offen lassen**; währenddessen den Eintrag in 7TV von Hand entfernen und unter gleichem Alias wieder
  hinzufügen, Sync abwarten; dann bestätigen → nichts gesendet, der neue Eintrag liegt weiter im Set, der Wunsch ist „aktiv“
  an der neuen Generation. Danach W-G regulär über die Zeile entfernen (Test-Emote 4 ist wieder draußen).
- **B6 LAN-Durchgang gegen dieselbe Kopie, zwei Teile, beide Pflicht [B] (F8, Sol-R2-P2-8):** Api mit
  `--launch-profile lan` und Web mit `start:lan` (Hostname aus der gitignorierten `appsettings.Lan.json`, nicht ins
  Protokoll), dieselben Verbindungszeichenfolgen wie in Stufe A. Die Sitzung unter dem LAN-Hostnamen ist eine eigene
  (anderes Cookie, eigener Twitch-Login).
  - **B6a, Orchestrator, emuliert:** grober Zeiger bei 360 px: Tab, Zeilen, Nachtragen-Dialog als Bottom-Sheet, Verlängern,
    Behalten, `/my-wishes`, Kopfzeile in de und en; keine 7TV-Knöpfe, stattdessen der 7TV-Link; kein horizontaler Überlauf.
  - **B6b, Betreiber, am eigenen Handy im LAN, solange der Stack läuft:** eigener Login, dann einmal durchtippen: Tab
    „Wünsche“, eine Zeile lesen, Nachtragen-Dialog öffnen und schließen, einen Wunsch verlängern, `/my-wishes` über die
    Kopfzeile, zurück. Der Orchestrator hält das **beobachtete Ergebnis** je Punkt im Protokoll fest (was der Betreiber
    gesehen und gemeldet hat, Gerät und Browser als Typ, keine Namen). B6b ist nicht durch B6a und nicht durch einen Vermerk
    im PR ersetzbar; ohne B6b ist Gate 7 offen.
- **B7 Aufräumen und Vergleich:** alle Test-Emotes aus dem Set. **Vorher-nachher-Vergleich gegen den festgehaltenen
  Ausgangsbestand: dieselbe Set-ID, und je Eintrag dieselbe Emote-ID, derselbe Alias und dasselbe `addedAt`**; null fehlend,
  null zusätzlich, null mit geändertem Datum. Ein abweichendes `addedAt` eines Bestands-Eintrags ist ein Fehlschlag der
  Live-Probe und wird dem Betreiber gemeldet, nicht stillschweigend hingenommen. Prozesse per `ps -o args= -p <pid>` prüfen,
  dann SIGTERM; Redis-Container und Kopie entfernen; **Cookie- und Token-Datei löschen**. Protokoll nach
  `docs/nachtlaeufe/live-485-<Datum>.md` (je Schritt: AK, erwartet, beobachtet, bestanden; ohne Token, ohne Cookie, ohne
  LAN-Hostnamen), eigener `docs:`-Commit.

**Live nicht herstellbar, nur durch Tests belegt:** `failed`, `unconfirmed`, alle Bot-Gründe einschließlich
`unconfirmed_start` (vor K4 kein Bot-Modus; der Text wird im Harness-Szenario und per Vitest gezeigt); der Nur-Lesen-Zustand
ohne zweite Sitzung; verlorene 7TV- und Api-Antworten; `pending`.

---

## 8. Gates vor dem PR (T17), Migration, Deploy

1. `dotnet build EmotePurge.slnx --no-incremental` ohne neue Warnungen, `dotnet format EmotePurge.slnx --verify-no-changes`,
   `dotnet test EmotePurge.slnx` grün (Docker läuft).
2. `npm --prefix web test -- --watch=false` grün (nicht `npx vitest`: das überspringt die Typprüfung).
3. `npm --prefix web run lint` und `npm --prefix web run format:check` grün.
4. `npm --prefix web run e2e` grün, **nur wenn auf `:5151` keine Api lauscht** (vorher prüfen; die Api aus Abschnitt 7 beenden).
   Rote Fälle quer durch fremde Dateien: erst `:5151`, dann Speicherdruck prüfen, Suite allein wiederholen.
5. Audit-Harness (`npx playwright test --config=playwright.audit.config.ts` in `web/`, vorher `.audit-out/` leeren): Gates aus
   T14; Bilder in de und en gesichtet.
6. `node scripts/coverage-local.mjs` (nur Committetes zählt; Schwelle 80 % auf neuem Code; bei den chirurgischen Änderungen in
   `WishService.cs`, `app-shell.ts`, `seven-tv-delete.service.ts` und den drei Lauf-Diensten ist die Näherung unscharf).
7. **Live-Verifikation: T0c, Stufe A und Stufe B bestanden und protokolliert, einschließlich B6b (der Betreiber am eigenen
   Handy im LAN, mit beobachtetem Ergebnis im Protokoll) und B7 (Vergleich mit Set-ID, Alias und `addedAt` ohne Abweichung).
   Ohne jeden dieser Punkte gibt es keinen PR**; ein offener Schritt ist ein offenes Gate, kein Eintrag für eine Morgenliste
   und kein Vermerk im PR-Text **[B]**. Dazu die Sichtbarkeitsprüfung der Kopfzeile aus T12 (AK44).
8. **Orchestrator und Betreiber:** Nachprüfung dieses Plans durch `/codex:adversarial-review --model gpt-6.2-sol` vor dem
   Lauf; `/codex:review --model gpt-6.2-sol` einmal über den fertigen Branch, Ergebnis unverändert vorlegen; Opus-Review; bei
   Widerspruch Fable als Schiedsrichter; Push, PR, Merge. Der Betreiber sieht die Oberfläche unter der URL aus Abschnitt 7.
9. PR-Text (englisch) nennt: **keine Migration**; die geänderten Api-Verträge aus 2.1a (neue DTO-Felder, `complete-removal`
   mit Pflicht-Rumpf, Kanalsperre und Inventurprüfung, `adopt` mit erwarteter Generation, `EndedAtUtc`, 409-Rümpfe,
   `GET /slots`, optionale Schwelle, `canEdit`); die hingenommene Lücke zwischen Read und REMOVE; die Abweichungen aus Abschnitt 9;
   die Laufart `wish`; den Abschlussvertrag des Lösch-Dienstes; dass Entfernen zusätzlich `sync-deleted` meldet; die
   Kopfzeilen-Stufe mit Messwerten; die Gegenprobe AK43; was nur getestet, nicht live gezeigt ist; die Übergaben aus 1.2.

**Migration: keine** (2.1a). **Deploy:** Api und Worker (ein Build, gemeinsame Infrastructure; das Api-Image trägt die
Web-App). `complete-removal` und `adopt` haben vor K3 keinen Aufrufer, die Änderung ihrer Rümpfe bricht also nichts Laufendes.
`WISHES_ENABLED` bleibt auf Prod aus bis Freigabe 1: kein Tab, kein Link, eine `summary`-Anfrage je Seitenaufruf mit 404.
Rollback: Schalter aus genügt. Den Deploy-Hinweis gegen **alle** ausstehenden Migrationen prüfen (`AddWishes` aus K1 muss vor
dem Einschalten angewendet sein).

---

## 9. Abweichungen zwischen Spec und gemergtem Backend

| # | Spec | Gemergter Stand | Umgang in K3 |
|---|---|---|---|
| A1 | K3 liefert `api-error.ts` und `errors.api.wish_*` | K1 hat beides schon geliefert | nur Gegenprobe und Wortlaut (T13) |
| A2 | neun Audit-Aktionen | elf (`wish.update`, `wish.due` dazu) | alle elf übersetzt (T13) |
| A3 | zwölf eigene Codes | drei Ausgänge ohne eigenen Code: `emote_ids_invalid`, zweimal `invalid_request_body` | kontextabhängiger Text (2.9) |
| A4 | E13: laufender Wunsch → `wish_already_running` | „im Set“ gewinnt vor „läuft schon“; `adopt` meldet den Halter ohne ID | **T0b:** beide 409 nennen die `wishId` (2.8 a) |
| A5 | Handeintrag „nur mit ID“ ungeregelt | 400 `wish_alias_invalid` ohne Alias und Namen | Dialog erzeugt den Fall nicht (2.8 b) |
| A6 | `emoteName` = Standardname | nach `adopt` der Slot-Name, bleibt bei Umbenennung stehen | nur `alias` anzeigen (2.8 c); Hinweis an K6 |
| A7 | `EndedAtUtc` bei `complete-removal` ungeregelt | Befehlszeit, auch aus `removed_externally` | **T0b:** bleibt erhalten (2.8 d) |
| A8 | AK37: Bindung über Alias und `addedAt` | `WishDto` trägt die Slot-Generation nicht | **T0a:** `slotAddedAtUtc` am DTO (2.6) |
| A9 | Karte zeigt Emote-Bild | `WishDto` trägt kein Bild | **T0a:** `imageUrl`; ohne Kanalzeile die Bildregel aus 2.10 |
| A10 | AK41: schreibgeschützt für Unberechtigte | Einstellungen sagen nicht, wer ändern darf | **T0a:** `canEdit`, dazu reaktiv auf 403 (T10) |
| A11 | `recheck` als Übergang | antwortet bis K4 immer 409 | in K3 nicht angeboten |
| A12 | `mine` „Handlungsbedarf und Probezeit“ | flache Liste ohne Merkmal, unpaginiert | Gruppierung im Client (T2) |
| A13 | 201 mit `setFull` | Feld nur vorhanden, wenn wahr | optionales Feld (T1) |
| A14 | Files Reference: Settings-Link „nur noch `canManage`“ | – | Spec E19 und AK41 gelten (T3); Spec in T15 angleichen |
| A15 | Guards unter `core/guards/` | liegen unter `core/channels/` | dort (T3) |
| A16 | Testplan: `WishSummaryService` „Entprellen“ | ein Poll hat nichts zu entprellen | Takt, Sichtbarkeit, Abschalten getestet (5) |
| A17 | Audit-Einträge der Wünsche in der Ansicht | Api liefert `detail: null` für alle `wish.*` | nur Aktionsname; Detail ist Folgearbeit (Befund 3) |
| A18 | AK38: Grund am Knopf | UI-Designsprache §4.2: ohne Hinweistext | eine Seitenzeile als Beschreibung (2.7), §4.2 präzisiert (T15) |
| A19 | AK45: Handy-Durchgang | §2.5: kein 7TV-Schreiben auf grobem Zeiger | [B] Handy ohne ★-Aktionen, mit 7TV-Link |
| A20 | „Karte“ | §2.1: keine Kartenklasse | linierte Zeile (`wish-row`) |
| A21 | Übernehmen mit Start = `AddedAt` | alter Eintrag ist sofort überfällig | [B] Vorschau und Vorwahl „ab jetzt“ (T6) |
| A22 | Schutzschwelle als Kanal-Vorgabe; `PUT` mit allen drei Werten | wirkt im manuellen Modus nicht; `PUT` überschreibt sie immer | [B] in K3 nicht gezeigt; **T0b:** nicht gesendet = unverändert |
| A23 | behaltene Wünsche | halten ihren Slot ([B] 2026-11-18) | Eintragsliste und 409 nennen den Halter |
| A24 | Sparkline über `usage-stats/daily` je Slot ab Start | Kennzahlen rechnen über alle Slots des Emotes im E4-Fenster | **T0a:** `usage.daily`, die Linie zeigt das Fenster der Zahlen (T5) |
| A25 | `complete-removal` ohne Rumpf, zustandsgeprüft | bucht auch einen inzwischen neu gebundenen Wunsch oder einen, dessen Ersatz schon in der Inventur liegt; nicht wiederholbar | **T0b:** Kanalsperre, Set, Generation, Inventur; idempotent (2.1a) |
| A26 | Einträge für „Bestand übernehmen“ ohne eigene Quelle | `usage-stats/totals` lässt Einträge neben einem Seed aus | **T0a:** `GET …/wishes/slots` mit dem Prädikat von `adopt` |
| A27 | `adopt` mit `slotId` | übernimmt, was unter der Slot-ID gerade liegt; die ID wird über Generationen wiederverwendet | **T0b:** erwarteter Alias und erwartete Generation im Rumpf, Vergleich unter der Kanalsperre |
| A28 | aktives Set aus dem Kanal | `…/emotes/active-set` ist der Postgres-Stand | autoritativer Read über 7TVs `style.activeEmoteSetId` vor jedem Schreiben (2.5) |

---

## 10. Risiken

| # | Risiko | Umgang |
|---|---|---|
| R1 | Die Form des 7TV-Einzel-Reads ist unbelegt; die Sonde kann am Klassifikator scheitern | T6 belegt sie zuerst; nach zwei Fehlversuchen den Betreiber um einen `curl` bitten; der Dialog funktioniert auch ohne Read |
| R2 | Entfernen über den Lösch-Lauf schreibt zusätzlich `emotes.syncDeleted` und bietet im Dock „Wiederherstellen“ | [B], gewollt; im PR und in DECISIONS benannt |
| R3 | Der Abschlussvertrag greift in den meistgetesteten Lauf-Dienst ein | eigener Commit ohne Verhaltensänderung für Bestandsaufrufer, alle Bestands-Specs unverändert grün, Opus |
| R4 | `startsAtAuthoritative` als Merkmal „Generation bestätigt“ könnte auf einem Weg vom Slot abweichen | T0a belegt es je Weg; bei Abweichung eigenes DTO-Merkmal und Rückmeldung; die zweite Sicherung (frischer Set-Read im Lauf) bleibt davon unabhängig |
| R5 | Ein unbewegtes Emote, das der Kanal noch nie im Set hatte, zeigt bis zum ersten Sync kein Bild (außer in der Sitzung, die es nachgetragen hat) | [B] F7: lieber eine leere Fläche als eine mögliche Animation; nach dem Hinzufügen liefert der Server das Bild |
| R6 | Kopfzeile bei 360 px (AK44) | Messung zuerst, Stufe 2 als Ziel, Stufe 3 als Rückfall, Audit-Gate |
| R7 | Anfragenlast | ein Abruf je sichtbarer Zeile (statt zwei), Schlüssel-Cache, „Beendet“ ohne Nutzung, Seite 20; das Neuladen bei `channel.synced` kostet je offenem Tab zwei Listenabrufe je Sync; Eimer 300 mit 5 je Sekunde |
| R8 | Die neue `summary`-Abfrage berührt jede Seite und jeden Bestands-E2E-Fall | nur mit Nutzer; Vorgabe-Mock in `mockAuthMe` und im Harness; Abschalten nach 404 |
| R9 | Neue Laufart `wish` an Aufzählstellen vergessen | der `Record` erzwingt das Substantiv; T8 sucht alle Leser von `SevenTvRunKind`; E2E 8 |
| R10 | Zwei Locale-Dateien, viele Tasks | strikt nacheinander; T13 schließt ab; die Locale-Specs fangen fehlende Schlüssel |
| R11 | K3 ist mit dem Vorlauf deutlich größer als ein reines Web-Kind (20 Tasks) | Vorlauf zuerst und für sich live geprüft (T0c); danach hängt kein Frontend-Task mehr an einer Backend-Annahme |
| R12 | Stufe B hängt an Cookie und Token des Betreibers | ohne beides kein PR; Übergabe vor T0c einplanen; Dateien Modus 600, Löschung protokolliert |
| R13 | Bot-Zustände sind in K3 nur getestet, nie live gesehen | neutrale Darstellung als Vertrag; K5 wiederholt die Live-Probe im Bot-Modus |
| R14 | Speicherdruck auf der Devbox lässt E2E rot aussehen | `/proc/pressure/memory` lesen, Suite allein wiederholen |
| R15 | Ein Ersatz-Eintrag, den noch kein Sync geschrieben hat, ist für `complete-removal` unsichtbar; die Meldung bucht `removed`, der Ersatz bleibt als gewöhnlicher Eintrag im Set | [B]-nah, bewusst: für die Datenbank ist das ein Hinzufügen nach dem Entfernen; ein `removed`-Wunsch nimmt nie einen späteren Eintrag auf; der Verwalter sieht den Eintrag im Set und kann „Bestand übernehmen“ |
| R16 | Zwischen der Beobachtung des Ziels und dem REMOVE (bis 5 Sekunden plus Roundtrip) kann unter demselben Alias eine neue Generation entstehen oder das aktive Set wechseln; 7TV kennt kein bedingtes Entfernen | [B] hingenommen; Schaden: ein fremder Eintrag desselben Emotes und Alias wird entfernt. Die Meldung bucht die alte Generation; den Verlust eines Ersatzes zeigt der Sync nur, wenn er ihn vorher gesehen hat (2.6). In DECISIONS und im PR benannt |
| R17 | Der Set-Read vor jedem Senden kostet Budget der Set-Listen-Route; bei erschöpftem Budget wird nicht gesendet | gewollt (fail-closed); ein Vorgang braucht zwei Reads plus einen je Wiederholung; die Zeile nennt den Grund, der Verwalter versucht es später |
| R19 | **Buchungsschaden, hingenommen [B]:** Der Sync hält einen Set-Wechsel in eigener Transaktion fest, bevor er die Kanalzeile für die Inventur sperrt. In diesem Fenster (Dauer eines Sync-Schritts, Sekunden) nimmt `complete-removal` eine Meldung für das alte Set noch an und bucht `removed`, bevor die Inventur des neuen Sets da ist | nur die Buchung ist betroffen, keine 7TV-Mutation; Voraussetzung: Set-Wechsel und Meldung im selben Sekundenfenster; Korrektur von Hand über „Bestand übernehmen“; Folge-Issue (Abschnitt 14) |
| R20 | **Buchungsschaden, hingenommen [B]:** Nach Abbruch oder verlorener Antwort wertet das Nachlesen das Fehlen der alten Generation als `done`; hat in Wahrheit jemand anders den Eintrag entfernt, bucht die Meldung trotzdem `removed` mit `wish.remove` des Verwalters, und der Wunsch kehrt an einen späteren Eintrag nicht zurück | nur die Buchung; Voraussetzung: unbeantworteter eigener REMOVE **und** fremdes Entfernen im selben Fenster; wo billig möglich, trägt das Audit `evidence = absent`; Folge-Issue (Abschnitt 14) |
| R18 | `complete-removal` nimmt jetzt die Kanalsperre und wartet damit hinter einem laufenden Sync desselben Kanals | Sperre nur für eine kurze Transaktion; dieselbe Reihenfolge wie alle anderen Schreiber, kein Zyklus |

**Für die Code-Prüfung durch Sol (Fokus):** die Zehn-Zeilen-Tabelle samt Zeile 6; die vier Prüfungen vor jedem REMOVE;
die Tabelle von `complete-removal`, besonders Wiederholung und Neubindung;
dass kein Weg ein Löschziel aus etwas anderem als dem eingefrorenen Wunsch baut; die letzte Prüfung vor jedem Schreiben; genau
eine Meldung je `done`; dass eine fehlende Schwelle keine Entscheidung auslöst; `EndedAtUtc` und Retention; kein 7TV-Schreiben
auf grobem Zeiger; kein `summary`-Abruf ohne Sitzung; kein Login eines Wünschenden und kein Token in Logs, URLs oder Audit.

---

## 11. Betreiberentscheidungen und offene Fragen

**Entschieden [B], 2026-11-18 (nach Sol-Runde 1):**

- **Backend-Vorlauf in K3** (T0a bis T0c) mit den Verträgen aus 2.1a; Ziel und Ergebnis: keine Migration.
- **F1 = A:** `canEdit` im `GET` der Einstellungen (T0a), dazu die reaktive Behandlung des 403.
- **F2:** kein 7TV-Schreiben auf grobem Zeiger; auf dem Handy stehen die Buchungs-Aktionen und der 7TV-Link.
- **F3 entfällt:** Das DTO trägt ein Bild (T0a); der Rest ist F7 (entschieden, siehe unten).
- **F4:** Kopfzeile Stufe 2 (unter `sm` nur die Zahl als Link bei Zahl > 0, Eintrag „Wünsche“ zusätzlich im Konto-Menü).
- **F5:** Die Schutzschwelle wird in K3 nicht gezeigt und nicht gesendet; der Server lässt sie dann stehen (T0b).
- **F6:** Beginn der Frist beim Übernehmen wählbar, mit Ablauf-Vorschau; „ab jetzt“ vorgewählt, wenn „ab Hinzufügedatum“ schon
  abgelaufen wäre.
- **4d** wird im Backend behoben (`EndedAtUtc` bleibt), nicht in der Anzeige.
- **Sol-P1-1** (Löschziel aus der gebundenen Generation samt Set), **P2-3, P2-7, P2-8, P2-10** (verbindliche Verträge mit
  Verhaltens-Specs), **P2-6** (eigene Slot-Quelle), **P2-9** (Stufe B Pflicht, Orchestrator fährt sie), **P2-11** (Sparkline
  zeigt das Fenster der Kennzahlen): wie von Sol empfohlen.
- Unverändert: Entfernen über den Lösch-Lauf samt `emotes.syncDeleted` und „Wiederherstellen“ im Dock; Laufart `wish`.

**Entschieden [B], 2026-11-18 (nach Sol-Runde 2):**

- **F7 = ableiten, ohne Migration.** Kein gespeichertes Merkmal „animiert“ am Wunsch. Die Bildregel aus 2.10 gilt: Standbild
  vom Server, sonst nur die `_static`-Fassung, die einfache Fassung nur bei bestätigt unbewegtem Emote, bei unklarem
  Ladefehler leere Fläche.
- **F8 = Variante a.** Der Betreiber tippt einmal am eigenen Handy im LAN durch, solange der Stack läuft (B6b); Pflichtschritt
  in Gate 7 mit beobachtetem Ergebnis; ein PR-Vermerk ersetzt ihn nicht. Der Orchestrator fährt vorher den emulierten
  Durchgang (B6a).
- **Sol-R2-P1-1:** frischer Set-Read vor jedem Sendeversuch einer Wunsch-Löschung; die Lücke zwischen Read und REMOVE ist
  hingenommen (R16), weil 7TV kein bedingtes Entfernen kennt.
- **Sol-R2-P1-2:** `complete-removal` unter Kanal- vor Wunsch-Sperre mit Inventurprüfung; Set in jeder Zeile; Beleg des Sets
  ist das aktuell aktive Set; ohne Beleg 409; keine Migration.
- **Sol-R2-P2-3 bis P2-7 und die drei P3:** wie in der Tabelle „Revision 2“ und in Abschnitt 13.

**Entschieden [B], 2026-11-18 (nach Sol-Runde 3, Variante A):** behoben werden das Senden in ein nicht mehr aktives Set, das
Alter der Bindung, das gelöschte Token und der ältere Rückkehrkandidat; hingenommen und einem Folge-Issue zugewiesen sind die
Lücke beim Set-Wechsel (R19) und die Zuordnung nach Abbruch oder verlorener Antwort (R20).

**Offene Betreiberfragen: keine.** Eine Rückfrage entsteht nur, wenn (a) T12 feststellt, dass Stufe 2 der Kopfzeile die
Gates nicht besteht (Stufe 3 weicht von AK44 ab und braucht die Zustimmung des Betreibers), oder (b) T0a feststellt, dass
`startsAtAuthoritative` nicht auf jedem Weg dem Slot folgt (dann eigenes DTO-Merkmal, weiterhin ohne Migration). Zur
Kenntnis, keine Frage: Ein dauerhaft gespeichertes Set je Wunsch gibt es nicht (wäre eine Migration); eine Meldung nach einem
Set-Wechsel bekommt deshalb 409 und bucht nichts (2.1a).

Als **[E]** entschieden und widerrufbar, keine Frage: Zeile statt Karte; „Beendet“ lädt erst beim Öffnen; `my-wishes` ohne
Aktionen; Bestätigung für Behalten und Schließen; kein Detail in den Audit-Zeilen; 4b und 4c (2.8); die 409 und 404 von
`complete-removal` bleiben `wish_state_conflict` bzw. `wish_not_found` ohne neuen Code; `slotAddedAtUtc` plus
`startsAtAuthoritative` statt eines dritten Feldes; die zusätzlichen Audit-Details an `wish.remove`; 404
`wish_slot_not_found` für einen `adopt`, dessen Eintrag nicht mehr der gewählte ist; Ablaufzeit der Nutzung 5 Minuten;
Sprungziel über den Query-Parameter `wish`; Neuladen der Listen auch bei `channel.synced`.

---

## 12. Selbstprüfung des Plans

- Jeder Punkt aus Spec „K3“ hat einen Task: Routen und Tab (T3), Settings-Tab E19 (T3, T10), Core (T1, T2, T12), Seiten und
  Dialoge (T4 bis T7, T11), Handeintrag mit 7TV-Read (T6), Zeile mit Nutzung (T4, T5), Hinzufügen und Entfernen über den
  Arbiter samt gemeinsamer Mutation (T8, T9), Kopfzeile (T12), i18n (jeder Task, T13), Design (2.10, T14, T15). AK32 bis AK45
  sind zugeordnet (Abschnitt 4).
- Jede Betreiberentscheidung vom 2026-11-18 hat ihren Ort: Vorlauf (T0a bis T0c, 2.1a), Löschziel (2.6, T9), Verträge (2.4,
  2.5, 2.6, T4, T6, T8, T9), Slot-Quelle (T0a, T6), Stufe B (Abschnitt 7, Gate 7), Sparkline (T0a, T5), F-Entscheide (11).
- Jeder Sol-Befund beider Runden hat eine Zeile in Abschnitt 13 mit Task und Test.
- Namen sind über die Tasks gleich: `WishService`, `wish-view`, `wish-input`, `wishErrorKey`, `wish-row`, `wish-image`,
  `channel-wishes-page`, `wish-run-start`, `WishAddService`, `WishRemovalTracker`, `wish-removal-flow`, `WishSummaryService`,
  `channelWishesGuard`, Laufart `wish`, `slotAddedAtUtc`, `imageUrl`, `usage.daily`, `GET …/wishes/slots`, `canEdit`,
  `expectedAlias`, `expectedAddedAtUtc`, `runningWishAdoptable`, Merkmal „Wunsch-Bindung“, Sprungziel `wish`.
- Kein Task enthält Code; jeder nennt Ziel, Dateien, Vertrag, Grenzfälle, AKs, Tests und sein Modell.
- Kein Task braucht eine Rückfrage, außer in den zwei in Abschnitt 11 genannten Ausnahmefällen. Was der Orchestrator vom
  Betreiber braucht, steht in Abschnitt 7: Cookie und Token vor T0c, und den Durchgang am eigenen Handy (B6b).
- T0b, T9 und 2.1a sind je für sich lesbar (für die dritte Sol-Runde): 2.1a nennt die Garantien des Bestands, alle Verträge
  und die entschiedenen Lagen; T0b nennt Sperrreihenfolge, Grenzfälle und Tests; T9 nennt die drei Ergänzungen am
  Lösch-Dienst, Fluss, Tracker und die Restlücke.

---

## 13. Sol-Runden: Einarbeitung

### Sol-Runde 1

Prüfung vom 2026-11-18 mit `gpt-6.2-sol` über die Erstfassung, Verdict needs-attention. Jeder Befund wurde am Stand `135eae07`
nachgelesen; **alle dreizehn halten am Code**. Zwei Einordnungen: P2-6 trifft nur Kanäle, in denen noch ein ungesetzelter Seed
des aktiven Sets liegt (Altbestand aus der Slot-Migration), ist dort aber real; P3-13 wird ohne gespeichertes Merkmal gelöst
(F7). Wo Runde 2 einen dieser Punkte nachgeschärft hat, steht der heutige Stand in der Tabelle der Runde 2.

| Befund | Kern | Beleg am Code | Gelöst in |
|---|---|---|---|
| P1-1 | Löschziel kann eine neuere Generation derselben Slot-Zeile treffen | `WishDto` ohne `SlotAddedAtUtc`; T12-Auff. 6 (wiederbelebte Slot-Zeile) | T0a (`slotAddedAtUtc`), 2.5 Phase 3, 2.6, T9; Specs `wish-removal-flow`, `wish-run-start`; E2E 3 b/c; Live B5 |
| P1-2 | Alte Erfolgsmeldung beendet einen neuen Aufenthalt; Wiederholung nicht möglich | `CompleteRemovalAsync` prüft nur Zustand und 6-Stunden-Fenster; `Return` macht wieder `active` | T0b (Tabelle 2.1a), T9 (Tracker mit Tripel); Integrationstests je Zeile; Spec `wish-removal-tracker`; E2E 3; Live T0c 5/6 |
| P2-3 | Arbiter reserviert den Start nicht; keine Prüfung vor dem Schreiben | `startLocked` ist abgeleitet; `confirmTimeRefusal` prüft nur Vorgang und Token | 2.5 (Startvertrag), T8 (`wish-run-start`), T9; Spec `wish-run-start`; E2E 8 |
| P2-4 | Speichern setzt eine unsichtbare Schwelle zurück | `UpdateSettingsAsync` überschreibt alle drei Felder und verwirft Einreihungen | T0b (fehlt = unverändert), T10 (sendet sie nicht); Integrationstest, Spec `wish-settings-section`, E2E 7; Live A7 |
| P2-5 | Konflikt-Wunsch außerhalb der geladenen Seite unauffindbar | `AdoptAsync` meldet `SlotAlreadyLinked` vor `AlreadyRunning`, ohne ID | T0b (beide 409 mit `wishId`), T6 (2.8 a); Spec `wish-entry-dialog`, `wish-adopt-dialog`; E2E 2 |
| P2-6 | `totals` ist keine vollständige Slot-Quelle | `UsageViewRows`: Emote-Modus bei Seed des Sets, `slotId: null` | T0a (`GET …/wishes/slots`, ein Prädikat mit `adopt`), T6; Integrationstest, E2E 2 |
| P2-7 | Kein verlässlicher Abschluss je Lauf; Abbruch kann doch `done` sein | `startDelete` gibt nichts zurück; `run`/`lastRun` zeigen nur den gezeigten Lauf; Abbruch → `unknown` → Nachlesen | 2.6 (Abschlussvertrag), T9; Spec `seven-tv-delete.service` (Ergänzung), `wish-removal-tracker` |
| P2-8 | Listen können nach Live-Updates veraltet bleiben | `liveReload` entprellt nur; Publisher darf scheitern | 2.4 (Gültigkeit, sechs Auslöser), T4; Spec `channel-wishes-page`; E2E 5 |
| P2-9 | AK45 konnte ohne Browser-Nachweis durchs Gate; Testdaten lückenhaft | Erstfassung Gate 7 und Schritte 6, 9, 12 | Abschnitt 7 (getrennte Testdaten, Stufe B Pflicht, Orchestrator, LAN gegen dieselbe Kopie), Gate 7 |
| P2-10 | Alias-Vorbelegung nicht an die aktuelle Emote-ID gekoppelt | `latestOnly` zählt beim Abonnieren | T6 (Kopplungsvertrag); Spec `wish-entry-dialog`; E2E 1 |
| P2-11 | Sparkline und Kennzahlen beschreiben Verschiedenes | `WishUsageQueries`: alle Slots, E4-Fenster | T0a (`usage.daily`), T5; Integrationstest (Summe = `total`), Spec `wish-usage`; Live A5 |
| P3-12 | Datum ohne Uhrzeit kaschiert 4d nicht; Retention rechnet gegen `EndedAtUtc` | `RunTransitionAsync` stempelt „jetzt“; `DataRetentionService.cs:744/756` | T0b (`EndedAtUtc` bleibt), 2.8 d; Integrationstest über Mitternacht und zur Retention |
| P3-13 | Ersatzbild umgeht reduzierte Bewegung | `emoteStillUrl(id, false)` liefert bei animierten Emotes die bewegte Fassung | T0a (`imageUrl`), 2.10 (Bildregel), T4 (`wish-image`); Spec, Audit-Szenario, E2E 6; F7 |

### Sol-Runde 2: Einarbeitung

Prüfung vom 2026-11-18 mit `gpt-6.2-sol` über Revision 1, Verdict needs-attention (2 × P1, 6 × P2, 3 × P3). Jeder Befund
wurde am Stand `135eae07` nachgelesen; **alle elf halten am Code bzw. am Plantext**. Eine Einordnung zu R2-P1-1: Der
Wiedergebrauch greift nur, wenn die Wiederholung binnen 5 Sekunden nach dem letzten Read kommt und das Emote einen einzigen
Eintrag hat; ein 429 von 7TV wartet meist länger, ausgeschlossen ist der Fall dadurch nicht. Runde 2 bestätigt die Korrekturen
aus Runde 1 zu P2-4, P2-6, P2-7, P2-10, P2-11 und P3-12.

| Befund | Kern | Beleg am Code | Gelöst in |
|---|---|---|---|
| R2-P1-1 | Ein 7TV-Retry kann die Ersatzgeneration löschen | `bindBeforeRemove` darf einen bis 5 s alten Read wiederverwenden (`seven-tv-delete.service.ts:~725-742`); der REMOVE trägt nur Set, Emote-ID, Alias | 2.6 (frischer Read je Versuch, Restlücke R16), T9 (Merkmal am Lösch-Dienst); Spec `seven-tv-delete.service` (Ergänzung), `wish-removal-flow`; E2E 3 (429 mit neuer Generation, kein zweiter REMOVE) |
| R2-P1-2 | Eine alte Meldung bucht vor dem Wunschabgleich endgültig; Set in zwei Zeilen ungeprüft | Slots werden vor dem Abgleich committet (`SevenTvSyncService.cs:928-942`, `:224`); kein gespeichertes Set je Wunsch | 2.1a (Kanal- vor Wunsch-Sperre, Zehn-Zeilen-Tabelle, „Ersatz sichtbar“, Set-Beleg), T0b; Integrationstests: pausierter Abgleich, falsches Set, verlorene Antwort; Live T0c 6a/6b; keine Migration |
| R2-P2-3 | Startvertrag prüft Seite und Arbiter vor zwei weiteren Reads; aktives Set aus Postgres | Phase 3 der Revision 1; `…/emotes/active-set` liest `Channel.ActiveEmoteSetId` | 2.5 (autoritativer Read, 3a/3b, Wiederholungen nach 429), T8; Spec `wish-run-start`, `wish-add.service`; E2E 8 |
| R2-P2-4 | Übernehmen bestätigt nur die Slot-ID | `AdoptAsync` lädt den aktuellen Slot und übernimmt dessen Daten | 2.1a (`expectedAlias`, `expectedAddedAtUtc`, undatierter Slot), T0b, T6; Integrationstest, Spec `wish-adopt-dialog`, E2E 2 |
| R2-P2-5 | Konflikt-ID allein macht den laufenden Wunsch nicht erreichbar | `AdoptAsync` lehnt ohne `wishId` mit `wish_already_running` ab; kein Sprungziel | T0a (`runningWishAdoptable`), T6 (drei Arten von Einträgen, veralteter Stand), 2.4/T4 (Sprungziel); Specs `wish-adopt-dialog`, `channel-wishes-page`; E2E 2 a/b/c |
| R2-P2-6 | Nutzungsdaten veralten dauerhaft; Reconnect über globalen Status | `LoadUsageAsync` rechnet zum Auswertungszeitpunkt; `LiveUpdateService.status` ist dienstweit | 2.4 (Ablaufzeit 5 min, Wiederverbindung je Stream), T4 (Live-Dienst), T5; Specs `wish-usage`, `channel-wishes-page`, `live-update.service`; E2E 5 |
| R2-P2-7 | B5 verändert einen Bestandseintrag in 7TV | Revision 1, Schritt B5 und B7 | Abschnitt 7 (W-G eigens angelegt, Bestand nie angefasst, Vergleich mit Set-ID, Alias, `addedAt`) |
| R2-P2-8 | Gate lässt AK45 ohne echtes Handy passieren | Revision 1, F8 und Gate 7 | Abschnitt 7 B6b, Gate 7 (Pflicht mit beobachtetem Ergebnis) |
| R2-P3 Bild | Ein Ladefehler kann die Animation freigeben | `EmoteSprite` behandelt alle Ladefehler gleich; ein `<img>` kennt keinen Status | 2.10 (einfache Fassung nur bei bestätigt unbewegtem Emote), T4 (`wish-image`), T6 (Merkmal aus dem Einzel-Read); Spec, E2E 6 |
| R2-P3 T3 | T3 hängt Routen vor ihren Komponenten ein | Lazy-Routen in `app.routes.ts` importieren konkrete Module | T3 (ohne Routen, Zwischenstand festgelegt), T4 und T11 (Route im Task der Zielseite) |
| R2-P3 Audit | Audit wertet eine beschnittene Kopfzeile als bestanden | die Harness-Metriken messen Dokumentüberlauf, nicht Beschnitt durch Vorfahren | T12 (Sichtbarkeitsprüfung als eigenes Gate; Stufe 3 nur als ausdrückliche Abweichung von AK44), E2E 6 |

### Sol-Runde 3: Einarbeitung

Prüfung vom 2026-11-18 mit `gpt-6.2-sol`, eng über T0b, T9 und 2.1a, Verdict needs-attention (2 × P1, 4 × P2). Alle sechs
Befunde sind am Stand `135eae07` nachgelesen (Befund 34) und **halten**. Runde 3 bestätigt: Vollsync, Delta und
Wunschabgleich teilen die Kanalsperre, und Inventurprüfung und datiertes `adopt` kommen ohne Migration aus.

| Befund | Kern | Ergebnis |
|---|---|---|
| R3-P1 Rückkehrkandidat | Zeile 6 übersah einen älteren Eintrag, der auf den Alias umbenannt wurde | behoben in T0b (2.1a, Zeile 6 neu gefasst; Integrationstest mit angehaltenem Abgleich nach dem Vollsync) |
| R3-P1 inaktives Set | Set-Liste 60 s gecacht, kein Refresh; Set-Wechsel in der 429-Wartezeit unbemerkt | behoben in T0a (`refresh`), T8 (Prüfung vor jedem Senden), T9 (Merkmal „Wunsch-Bindung“); Specs und E2E 3: warmer Cache, Wechsel während der Wartezeit |
| R3-P2 Set-Wechsel im Sync | Wechsel wird vor der Inventur in eigener Transaktion festgehalten; kein pauschales Archivieren | hingenommen, R19; Aussage in 2.1a korrigiert; Folge-Issue |
| R3-P2 Alter der Bindung | letzte Seite wird nie neu gelesen, der Read darf 20 s dauern; R16 untertrieb | behoben in T9 (Beobachtung höchstens 5 s alt, sonst einmal neu lesen oder auslassen); R16 neu bemessen; Spec: Ersatz auf der letzten Seite |
| R3-P2 Zuordnung | festgestellte Abwesenheit wird als eigene Löschung gebucht | hingenommen, R20; optional `evidence` im Audit (T0b, T9); Folge-Issue |
| R3-P2 Token | die Engine hält das Token über Wiederholungen, auch nach dem Löschen | behoben in T8/T9 (Token-Gleichheit vor jedem Senden); Spec und E2E 3: Token in der 429-Pause gelöscht |

---

## 14. Folge-Issue (legt der Orchestrator nach dem Merge an; R19 und R20)

**Title:** Book wish removals server-side from a stored removal intent

**Body:** K3 (#485) books a manager's removal of a wished emote through a client report (`complete-removal`) that the server
checks against the wish's slot generation, the channel's current active set and the slot inventory. Two attribution gaps
remain, both limited to bookkeeping (no 7TV mutation is involved):

- **Set switch window.** The sync records a set switch in a transaction of its own before it locks the channel row for the
  inventory. A report for the old set that arrives in that window is still accepted and ends the wish as `removed` before
  the new set's inventory exists.
- **Absence booked as removal.** After a cancelled run or a lost 7TV answer, the client's re-read treats a missing entry as
  its own removal. If another editor removed the entry, the wish is still booked `removed` under the manager's name and
  cannot return to a later entry.

Direction: the server stores the intent ("this wish's entry, generation G in set S, is being removed by manager M") when
the run starts, and the wish reconcile books `removed` when the inventory shows that entry gone, instead of trusting a
client report. That needs a place to store the intent and the set it was made for (a migration), and it should serialize
the switch observation with the inventory under the channel lock or carry the observation interval through start and
completion. Out of scope: the residual window between the last read and 7TV's unconditional `removeEmote` (7TV offers no
conditional removal).

Labels: `feat`, `infrastructure`, `migration`. Related: #485, epic #481.
