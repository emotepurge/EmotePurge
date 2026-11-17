# Spec: Wunsch-Emotes mit Ablauf (Epic mit fünf Kindern)

Stand 2026-11-17. Sprache deutsch (Denkwerkzeug des Betreibers). Grundlage ist der freigegebene
Entwurf `docs/designs/Wunsch-Emotes-mit-Ablauf-2026-11-17.md` (Annahmen 1 bis 10, D4, D7, D8,
Fable-Eigentumsvertrag) plus die Betreiberentscheidungen aus der /spec-Befragung vom selben Tag.
Diese Spec rollt keine dieser Entscheidungen neu auf. Wo sie selbst etwas festlegt, steht es in
Abschnitt 4 („Festlegungen dieser Spec“) und der Betreiber kann widersprechen. Wo Entwurf, Brief und
Code einander widersprechen, steht es in Abschnitt 17.

Labels (Epic): `feat`, `epic`. Kinder: K1 bis K5, dazu eine Zeile „Scheibe 3“ ohne Issue.

## 1. Context

In mehreren Kanälen gibt es eine Kanalpunkte-Belohnung „7TV-Emote wünschen“: Ein Zuschauer löst
ein, das Emote kommt für eine vereinbarte Zeit ins Set. Das Entfernen nach Ablauf hängt heute am
Gedächtnis eines Mods, wird vergessen und skaliert nicht über viele Kanäle. Angefragt hat ein Mod,
der viele Kanäle betreut; der Betreiber würde es in seinen eigenen Mod-Kanälen ebenfalls nutzen.

Wer hat was davon:

- **Mods und Broadcaster:** alle fälligen Wünsche über alle Kanäle an einer Stelle, mit Nutzungszahl
  statt Stoppuhr (Produktgrundsatz 1: gemessene Nutzung urteilt nicht, der Mensch entscheidet).
- **Kanäle, die dem Bot-Konto Editor-Rechte geben:** Hinzufügen nach Freigabe und Entfernen bei
  Ablauf ohne weiteres Zutun.
- **Produkt:** erweitert die Positionierung auf „Purge plus Wünsche“ und öffnet „würde ich nicht
  bauen #3“ (`docs/Feature-Ideen-2026-08-01.md:770`) für genau einen eng umrissenen Fall.

Harte Anforderung: Ohne Freigabe durch einen Menschen kommt nichts ins Set (Annahme 1).

## 2. Current State (verifiziert 2026-11-17 im Worktree, Branch `docs/wunsch-emotes-design`)

| Thema | Befund | Stelle |
|---|---|---|
| Wunsch-Datenmodell | existiert nicht; kein Entity, kein i18n-Schlüssel, kein Endpunkt | `src/EmotePurge.Core/Entities/` (34 Dateien, keine mit „Wish“) |
| Letzte Migration | `AddLogCapture`; Prod-Migrationen laufen von Hand vor dem Image | `src/EmotePurge.Infrastructure/Migrations/`, `CLAUDE.md` „Prod-Migration“ |
| Slot | eine Zeile je Aufenthalt; `AddedAt` ist bis zur Bestätigung vorläufig (`AddedAtFromRest`); kein „hinzugefügt von“, kein Ablaufdatum | `src/EmotePurge.Core/Entities/EmoteSlot.cs:14-42` |
| Kanal-Einstellungen | keine generische Tabelle; Einzelspalten auf `Channel` | `src/EmotePurge.Core/Entities/Channel.cs:5-54` |
| Zählbeginn | `TrackedSince(trackingResumedAt, createdAt)` liefert ein Datum, keine Lückentabelle; `TrackingResumedAt` wird bei jeder Reaktivierung gesetzt, `DeactivatedAtUtc` ist nur während der Deaktivierung gefüllt | `src/EmotePurge.Core/Services/TrackingCoverage.cs:18`, `Channel.cs:23`, `Channel.cs:54` |
| Slot-Abgleich | Vollsync `SaveSyncAsync` (:744) → `ReconcileSlotsAsync` (:911, :1410); Delta `ApplyEmoteSetUpdateAsync` (:259); manuell `EmoteService.cs:733`, `:800`. Kein Hook, kein Domain-Event | `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs`, `EmoteService.cs` |
| 7TV-Client | nur lesend, kein Auth-Header; `GqlSetEntriesQuery` liest `alias`, `addedAt`, Emote-ID, nicht `addedById`; verkleidetes 429 (HTTP 200, `extensions.status`) wird erkannt | `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:42-43`, `:189`, HttpClient `ServiceCollectionExtensions.cs:134-140` |
| `addedById` | kommt im gesamten Repo nicht vor (grep über `src/` und `web/src/`) | keine |
| Editor-Lesen | `GqlEditorOfQuery` (v3, `user.editor_of`) und `GetEditorOfChannelsAsync(sevenTvUserId)` | `SevenTvApiClient.cs:61`, `:466` |
| Token-Verschlüsselung | `ITokenCipher`/`AesGcmTokenCipher`, symmetrisch, Schlüssel `Auth:Twitch:TokenEncryptionKey`; der Schlüssel ist in `docker-compose.prod.yml` nur am Dienst `api` gesetzt (:79), nicht am `worker` | `src/EmotePurge.Core/Services/ITokenCipher.cs:6`, `src/EmotePurge.Infrastructure/Services/AesGcmTokenCipher.cs:18` |
| Schalter-Muster | 404 `backfill_disabled` erst **nach** dem Autorisierungsfilter | `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs:321-390`, `Validation/ApiErrorCodes.cs:180` |
| Gruppenmuster | `/api/channels` mit `RequireAuthorization` + `ChannelNameValidationFilter`, je Route `ChannelManagementAuthorizationFilter` + Rate-Limit | `ChannelEndpoints.cs:26-48`; Policies `RateLimiting/RateLimitPolicyNames.cs:17-88` |
| Admin-Gruppe | `/api/admin` mit `GlobalAdminAuthorizationFilter`, bewusst ohne Rate-Limit | `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs:35-37` |
| Live-Events | Allow-Lists `AdminTypes` (:67) und `ChannelTypes` (:71); kanalübergreifender Stream leitet heute nur `live.changed` weiter | `src/EmotePurge.Core/Messaging/LiveEvents.cs`, `src/EmotePurge.Api/Endpoints/LiveEndpoints.cs:80-93` |
| Worker-Kommandos | Präfixe `JOIN:`, `LEAVE:`, `RESYNC:`, `BACKFILL:` (Letzteres reiner Anstoß, Nutzlast nie vertraut) | `src/EmotePurge.Core/Messaging/BotCommands.cs:12-22` |
| Serielle Worker-Schleife | Advisory-Lock auf eigener Verbindung | `src/EmotePurge.Infrastructure/Services/ChatLogBackfillService.cs:58-76`, `:672`, `src/EmotePurge.Worker/ChatLogBackfill/ChatLogBackfillWorker.cs:108` |
| Retention | Konstanten in `RetentionPolicy` (:22-66), je Regel ein `RunXAsync` in `DataRetentionService` (:94-120), nur bei `Retention:Enforce` | `src/EmotePurge.Core/Services/RetentionPolicy.cs`, `src/EmotePurge.Infrastructure/Services/DataRetentionService.cs` |
| Kontolöschung | pseudonymisiert `RequestedBy*` am Backfill-Lauf | `src/EmotePurge.Infrastructure/Services/AccountDeletionService.cs:131-136` |
| Audit | `AuditActions`-Konstanten, Details ohne Freitext | `src/EmotePurge.Core/Entities/AuditLogEntry.cs:9-52` |
| Browser-Schreibfläche | `addEmote(id: {emoteId, alias}) { id }` dreifach kopiert; `REMOVE_SLOT_ENTRY_MUTATION` exportiert; keine Editor-Mutation | `web/src/app/core/seven-tv/seven-tv-import.service.ts:74-88`, `seven-tv-delete.service.ts:165-178` |
| Workspace-Tabs | `usage-stats`, `tags`, `vote-sessions`, `activity` (canManage), `settings` (canManage und Backfill-Schalter) | `web/src/app/features/channel-workspace/channel-workspace-layout.ts:93-110`, `web/src/app/app.routes.ts:102-161` |
| Shell | „ein Badge zur Zeit“ bei 360 px Breite; ein Link `/my-votings` | `web/src/app/features/shell/app-shell.ts:128-134` |
| Zero-Knowledge-Satz | „The backend never stores or processes 7TV tokens at any point“ | `docs/Architectur.md:21`, `PRODUCT.md:81` |

Proben aus dem Entwurf (Scheibe 0): P1 bestanden für ein Mod-Konto (Nicht-Mod offen, betrifft nur
Scheibe 3). P2 bestanden: JWT lebt 30 Tage, Zweit-Login lässt das erste Token gültig, `addedById`
war in einem öffentlichen v4-Read 501 von 501 gefüllt. P3 entfällt per Betreiberentscheidung; der
Bot-Modus läuft inoffiziell.

## 3. Proposed Change: Überblick

Ein eigenes Wunsch-Modell je Kanal mit Lebenszyklus, zwei Betriebsarten je Kanal und drei
Oberflächen:

- **Manueller Modus:** Emote Purge führt Buch und erinnert. Hinzufügen und Entfernen macht ein
  Kanal-Verwalter aus der Karte heraus mit seinem eigenen 7TV-Token über die bestehende
  Browser-Engine. Das Backend schreibt nie nach 7TV.
- **Bot-Modus:** Das Bot-Konto ist 7TV-Editor des Kanals. Nach Freigabe fügt der Worker hinzu, bei
  Ablauf entfernt er, aber nur Einträge, die er nach bestem Wissen selbst hinzugefügt hat.
- **Oberflächen:** Tab „Wünsche“ je Kanal (Queue, laufende Wünsche, Einstellungen), eine
  kanalübergreifende Seite, ein Zähler in der Kopfzeile, live per SSE.

Erfassung in diesem Epic nur per Handeintrag und per Übernahme eines Bestandseintrags. Die
Erfassung über IRC ist Scheibe 3 und hat kein Issue.

## 4. Festlegungen dieser Spec (Betreiber kann widersprechen)

| # | Festlegung | Grund |
|---|---|---|
| E1 | **Laufzeit je Wunsch in ganzen Tagen**, Spalte `DurationDays`, 1 bis 365. Vorbelegt mit dem Kanal-Default (`DefaultDurationDays`, Default 30). Beim Handeintrag und bei der Freigabe abweichend setzbar. | Betreiber-Ergänzung: Kanäle haben mehrere Belohnungen mit verschiedenen Laufzeiten. Obergrenze 365: Darüber ist ein Wunsch faktisch „behalten“, und kein Wunsch-Datensatz soll länger offen sein, als das Audit-Log (365 Tage) seine Freigabe belegt. |
| E2 | **Vorlauf der Probezeit** je Kanal, `ProbationLeadDays`, Default 7, 1 bis 30. Probezeit ist **kein gespeicherter Zustand**, sondern abgeleitet: Beginn = spätester von Start und (Ablauf minus Vorlauf). Ist die Laufzeit kürzer als der Vorlauf, ist der Wunsch ab Start in Probezeit. | Eine Regel für alle Laufzeiten, kein Sonderfall für kurze. |
| E3 | **Verfall offener Wünsche** je Kanal, `PendingExpiryDays`, Default 14, 1 bis 90. | Entwurf Scheibe 1 plus Betreiber-Ergänzung. |
| E4 | **Bewertungsfenster des Schutzes:** Fensterbeginn = spätester von Start des Wunsches und (Bewertungszeitpunkt minus 7 Tage). Bewertungszeitpunkt ist der Lauf, der die Fälligkeit feststellt. Mittel je Tag = Summe der Nutzung über alle Slots desselben Emotes im Kanal an den UTC-Tagen vom Tag des Fensterbeginns bis zum Bewertungstag, geteilt durch die Fensterlänge in Tagen (Bruchteile erlaubt). **Ist das Fenster kürzer als 24 Stunden, gilt das wie eine Zähllücke.** | Betreiber-Korrektur: min(7 Tage, tatsächliche Zeit im Set), eine Regel ohne eigene Logik für kurze Laufzeiten. Bei 1 Tag Laufzeit ist das Fenster bei Fälligkeit genau 24 Stunden und damit gültig. |
| E5 | **Zähllücke** (Entscheidung 2): `TrackedSince(kanal)` liegt nach dem Fensterbeginn, **oder** `DeactivatedAtUtc` ist gesetzt, **oder** `IsBotActive` ist falsch, **oder** E4-Fenster unter 24 Stunden. Die Lückenregel greift nur bei eingeschaltetem Schutz; ohne Schutz spielt die Nutzung für das Entfernen keine Rolle. | Eine Deaktivierung mit Reaktivierung im Fenster setzt `TrackingResumedAt` und fällt damit unter die erste Bedingung (`Channel.cs:23`). |
| E6 | **Ablauf-Lauf alle 15 Minuten** (`Wishes:ExpiryIntervalMinutes`, 1 bis 1440), eigener Worker, erster Lauf 1 Minute nach Start, wartet nicht auf die Boot-Recovery. | Das Beobachtungsziel „binnen 24 h“ und Laufzeiten ab 1 Tag vertragen keinen Tagestakt. Der Lauf berührt keinen Kanal-Purge und braucht die Sperre der Retention nicht. |
| E7 | **Bot-Token asymmetrisch versiegelt:** Die Api hält nur einen öffentlichen Schlüssel und kann damit setzen, aber nicht lesen. Der private Schlüssel liegt nur am Worker. Details in Abschnitt 7. | Erfüllt „nie zurücklesen“ kryptografisch statt per Disziplin. Geht über die zwei Varianten des Briefs hinaus, s. Abschnitt 17 Punkt 2. |
| E8 | **Pilot-Freischaltung über eine Liste:** `Wishes:PilotChannelIds` (Twitch-Kanal-IDs, kommagetrennt). Leer heißt alle Kanäle. Ein Kanal außerhalb der Liste verhält sich wie bei ausgeschaltetem Schalter. | Der Brief verlangt „zuerst in den Kanälen des anfragenden Mods“; ein globaler Schalter allein kann das nicht. |
| E9 | **Abgleich zustandsbasiert statt ereignisbasiert:** Nach jedem erfolgreichen Vollsync, Delta und manuellen Slot-Schreiben läuft `IWishReconcileService.ReconcileChannelAsync(channelId)`, der Wünsche gegen den aktuellen Slot-Bestand vergleicht. Idempotent; der Ablauf-Lauf ruft ihn als Sicherheitsnetz für jeden Kanal mit laufenden Wünschen. | Es gibt keinen Hook für „Slot angelegt/archiviert“ (Abschnitt 2). Ein Vergleich gegen den Ist-Stand heilt verpasste Ereignisse von selbst. |
| E10 | **Übernahmefenster 6 Stunden** nach `extern entfernt` (Entwurf: „Größenordnung Stunden“). | Deckt Entfernen und Wieder-Hinzufügen über mehrere Sync-Läufe (R3-3), ohne ein späteres, unabhängiges Hinzufügen zu übernehmen. |
| E11 | **Bestätigungsschwelle im Bot-Modus 15 Minuten:** Lesen direkt nach dem Schreiben, dann nach 30 s, 2 min, 5 min, 15 min. Danach `vorgelegt` mit Grund `unconfirmed`. | P2-Schwelle des Entwurfs war 5 Minuten für die Machbarkeit; der Betrieb bekommt einen Versuch mehr, bevor ein Mensch gerufen wird. |
| E12 | **Verlängern ohne Obergrenze der Anzahl**, je Vorgang um den Kanal-Default, gerechnet ab dem bisherigen Ablauf, oder ab jetzt, wenn der Ablauf schon vorbei ist. Jede Verlängerung ist ein Audit-Eintrag. | Entscheidung 3 nennt keine Obergrenze; E1 begrenzt die einzelne Laufzeit, das Audit-Log zeigt Ketten. |
| E13 | **Höchstens ein laufender Wunsch je Kanal und 7TV-Emote-ID** (Zustände `approved`, `unconfirmed`, `active`, `presented`). Ein weiterer Wunsch für dasselbe Emote wird bei Freigabe zur Verlängerung des laufenden und endet selbst als `merged`. | Entscheidung 3. Ein zweiter Alias desselben Emotes als eigener Wunsch ist Out of Scope. |
| E14 | **Personendaten in `vorgelegt`:** Bleibt ein Wunsch länger als 30 Tage nach seinem Ablauf unentschieden, werden Twitch-ID und Login des Einlösers trotzdem geleert. | `vorgelegt` hat bewusst keinen Timeout (R3-12); ohne diese Kappe hätte Entscheidung 4 eine Lücke. S. Abschnitt 17 Punkt 3. |
| E15 | **Backlog (F8):** neue Idee `A18` „Wunsch-Emotes mit Ablauf“ in `docs/Feature-Ideen-2026-08-01.md`, mit Querverweis auf A15. A15 (Nachrücker-Liste) bleibt eigenständig; „einer rein, einer raus“ ist die spätere Brücke. Am Eintrag „würde ich nicht bauen #3“ steht ein Verweis auf die enge Ausnahme. Statuszeile pflegt K1 (begonnen) und K5 (umgesetzt). | CLAUDE.md verlangt die Statuszeile im selben Commit. |
| E16 | **Position im Set (R3-7):** Die Karte zeigt „Rang N von M“ der Nutzung unter allen aktiven Slots im selben Fenster wie E4. Nur in der Detailantwort, nicht in Listen. | Macht „wo es im Set steht“ aus dem Entwurf messbar, ohne die Liste zu verteuern. |

## 5. Lebenszyklus

Zustände (`Wish.Status`, als Zeichenkette gespeichert, die Werte sind Vertrag):

| Wert | Deutsch | Bedeutung | Endzustand |
|---|---|---|---|
| `pending` | offen | erfasst, wartet auf Freigabe | nein |
| `approved` | freigegeben | freigegeben, Eintrag noch nicht im Set | nein |
| `unconfirmed` | unbestätigt | Bot hat geschrieben, maßgebliches `addedAt` fehlt noch | nein |
| `failed` | fehlgeschlagen | Bot-Hinzufügen von 7TV abgelehnt | nein |
| `active` | aktiv | Eintrag im Set, Frist läuft | nein |
| `presented` | vorgelegt | braucht eine Entscheidung eines Menschen, Grund in `StatusReason` | nein |
| `rejected` | abgelehnt | Freigabe verweigert | ja |
| `expired` | verfallen | offen geblieben bis `PendingExpiryDays` | ja |
| `removed` | entfernt | über Emote Purge entfernt (Mensch oder Bot) | ja |
| `removed_externally` | extern entfernt | außerhalb von Emote Purge entfernt | ja, mit Rückweg (E10) |
| `kept` | behalten | Eintrag bleibt dauerhaft, Wunsch geschlossen | ja |
| `closed` | geschlossen | ohne Aktion in 7TV beendet | ja |
| `merged` | zusammengeführt | ging als Verlängerung in einen laufenden Wunsch auf | ja |

Begriffe für die Oberfläche: **„Probezeit“** = `active` und jetzt ≥ Probezeitbeginn (E2).
**„fällig“** = `presented` (jeder Grund) sowie `active` mit überschrittenem Ablauf, solange der Lauf
oder der Bot noch nicht reagiert hat. **„Handlungsbedarf“** (Zähler) = `pending`, `failed`,
`presented`, dazu `approved`, wenn der Kanal im manuellen Modus ist oder der Bot ausgefallen ist.

Gründe in `StatusReason` bei `presented`: `expired` (abgelaufen, manuell zu erledigen),
`protected` (Schutzschwelle erreicht), `coverage_gap` (Zähllücke), `not_bot_owned` (Bot-Modus, aber
kein Eigentum, schließt F12 ein), `ownership_lost` (frischer Read wich ab), `unconfirmed` (E11),
`bot_unavailable` (Ausfall bei fälliger Entfernung), `already_in_set`, `repair_review` (nach
Reparatur). Bei `failed`: `alias_conflict`, `set_full`, `emote_gone`, `not_editor`, `unknown`.

Übergänge (jede Zeile ist ein bedingtes Update auf den erwarteten Ausgangszustand; trifft es keine
Zeile, antwortet die Api 409 `wish_state_conflict`):

| Von | Nach | Auslöser | Bedingung und Wirkung |
|---|---|---|---|
| (neu) | `approved` | Handeintrag | gilt als Freigabe (Annahme 1) |
| (neu) | `active` | Bestand übernehmen | Slot schon im Set; nie Bot-Eigentum |
| (neu) | `pending` | IRC-Erfassung | erst Scheibe 3; in diesem Epic nur über Tests erreichbar |
| `pending` | `approved` | Freigeben | Laufzeit und Alias änderbar; läuft schon ein Wunsch desselben Emotes: stattdessen `merged` und der laufende wird verlängert (E13) |
| `pending` | `rejected` | Ablehnen | Oberfläche zeigt den Hinweis auf Twitchs Warteschlange für die Rückerstattung |
| `pending` | `expired` | Ablauf-Lauf | älter als `PendingExpiryDays` |
| `approved` | `active` | Abgleich, manueller Weg | genau ein passender neuer Slot (Abschnitt 6.3); Start = `AddedAt` des Slots |
| `approved` | `unconfirmed` | Bot hat `addEmote` geschrieben | nur Bot-Modus |
| `approved` | `failed` | Bot | 7TV lehnt ab; kein stiller Retry (F2) |
| `approved` | `presented` (`already_in_set`) | Bot | Vor-Read zeigt Emote-ID schon im Set |
| `approved` | `closed` | Schließen | Verwalter verwirft den freigegebenen Wunsch |
| `unconfirmed` | `active` | Bestätigungs-Read | genau ein Eintrag mit Emote-ID und Alias: mit `addedById` = Bot entsteht Eigentum, sonst reiner Erinnerungsfall (R3-4) |
| `unconfirmed` | `presented` (`unconfirmed`) | E11 abgelaufen, oder Bot-Ausfall | Ausgänge: „als Erinnerungsfall verknüpfen“ (→ `active` ohne Eigentum, sobald der Abgleich den Slot findet), „erneut prüfen“ (→ `unconfirmed`, Versuche zurückgesetzt), „schließen“ (→ `closed`) |
| `failed` | `approved` | Erneut versuchen | braucht keine neue Freigabe: Der Klick eines Verwalters ist die Freigabe (R3-12) |
| `failed` | `closed` | Schließen | |
| `active` | `active` | Verlängern | neuer Ablauf nach E12 |
| `active` | `kept` | Behalten | Eintrag wird ein normaler; Bot-Eigentum endet |
| `active` | `removed` | Entfernen über Emote Purge | manuell: Browser-Engine, danach Meldung an die Api; Bot: nach bestandener Prüfung (Abschnitt 8) |
| `active` | `presented` | Ablauf-Lauf bei Fälligkeit | Grund nach der Entscheidungstabelle in K2 |
| `active`, `presented` | `removed_externally` | Abgleich | verknüpfter Slot ist archiviert und Emote Purge hat das Entfernen nicht selbst gemeldet |
| `presented` | `active` | Verlängern | wie oben |
| `presented` | `kept` | Behalten | |
| `presented` | `removed` | Entfernen | manuell über die Browser-Engine; bei `protected`, `coverage_gap` und `bot_unavailable` zusätzlich „Bot entfernen lassen“, wenn das Eigentum noch besteht |
| `presented` | `closed` | Schließen | nur wenn kein Slot verknüpft ist |
| `removed_externally` | `active` oder `presented` | Abgleich binnen 6 h | genau ein neuer, unverknüpfter Slot derselben Emote-ID; Frist bleibt, kein Eigentum |
| `removed_externally` | `removed` | Meldung der Browser-Engine binnen 6 h | löst den Wettlauf zwischen Sync und Erfolgsmeldung auf |
| `removed`, `removed_externally` | `approved` | Wieder hinzufügen | mit neuer Laufzeit, oder `Permanent = true`: dann führt der Abgleich nach `kept` statt nach `active` |

`presented` hat **keinen Timeout** und keinen automatischen Ausgang (R3-12): Im manuellen Modus
bleibt ein abgelaufener, unbehandelter Wunsch `presented` mit Grund `expired`, zählt im Zähler und
steht oben in der Übersicht, bis jemand entscheidet.

Änderungen außerhalb von Emote Purge (R3-2, R3-3): **Umbenennen** behält Slot, Wunsch und Frist;
der Alias am Wunsch wird nachgezogen und ein Bot-Eigentum endet. **Entfernen** führt nach
`removed_externally`. **Erneutes Hinzufügen** folgt E10; mehrere Kandidaten oder ein Slot außerhalb
des Fensters werden nicht übernommen.

## 6. Datenmodell

Eine Migration `AddWishes` (K1) für die Tabellen 6.1 und 6.2, eine zweite `AddWishBot` (K4) für
6.4. Beide rein additiv. Konfiguration inline in `AppDbContext.OnModelCreating` wie der Bestand.

### 6.1 `Wishes` (Entität `Wish` in `EmotePurge.Core.Entities`)

| Spalte | Typ | Bemerkung |
|---|---|---|
| `Id` | bigint, Identity | |
| `ChannelId` | text, FK `Channels`, ON DELETE CASCADE | Purge nimmt Wünsche mit |
| `SevenTvEmoteId` | text, max. 32 | 7TV-ID, nicht `Emote.Id` (Regel 8) |
| `EmoteName` | text, max. 100 | Standardname bei 7TV zum Zeitpunkt des Eintrags, nur Anzeige |
| `Alias` | text, max. 100 | gewünschter bzw. aktueller Name im Set |
| `Source` | text | `manual`, `adopted`, `irc` |
| `Status` | text | Werte aus Abschnitt 5 |
| `StatusReason` | text, null | Grund bei `presented` und `failed` |
| `StatusChangedAtUtc` | timestamptz | |
| `RequesterTwitchUserId` | text, null | Einlöser; beim Handeintrag leer |
| `RequesterLogin` | text, null, max. 25 | beim Handeintrag optional, validiert als Twitch-Login |
| `DurationDays` | int | 1 bis 365 (CHECK) |
| `CreatedAtUtc` | timestamptz | |
| `ApprovedAtUtc` | timestamptz, null | |
| `StartsAtUtc` | timestamptz, null | 7TVs `addedAt` des Eintrags |
| `StartsAtAuthoritative` | bool | falsch, solange `EmoteSlot.AddedAtFromRest` falsch ist |
| `ExpiresAtUtc` | timestamptz, null | Start plus Laufzeit plus Verlängerungen |
| `EndedAtUtc` | timestamptz, null | gesetzt bei jedem Endzustand; Uhr der Retention |
| `SlotId` | text, null, FK `EmoteSlots`, ON DELETE SET NULL | aktueller Slot |
| `ExtensionCount` | int | |
| `Permanent` | bool | „wieder hinzufügen, dauerhaft“ |
| `MergedIntoWishId` | bigint, null | |
| `BotOwned` | bool | Eigentum nach Abschnitt 8 |
| `BotOwnedAddedAtUtc` | timestamptz, null | das beim Entstehen des Eigentums festgehaltene `addedAt` |
| `BotWriteAtUtc` | timestamptz, null | Zeitpunkt des letzten Bot-Schreibens |
| `BotAttemptCount` | int | |
| `BotNextAttemptAtUtc` | timestamptz, null | |
| `RemovalQueuedAtUtc` | timestamptz, null | fällige Bot-Entfernung |

Die Zeile speichert **keine Identität eines Verwalters**; wer was getan hat, steht nur im Audit-Log.

Indizes und Constraints:

- `IX_Wishes_ChannelId_Status`.
- `IX_Wishes_Status_ExpiresAtUtc` (Ablauf-Lauf).
- Eindeutig, partiell: `(ChannelId, SevenTvEmoteId)` WHERE `Status` IN (`approved`, `unconfirmed`,
  `active`, `presented`) (E13).
- Eindeutig, partiell: `(SlotId)` WHERE `SlotId` IS NOT NULL AND `Status` IN (`active`, `presented`).
- `IX_Wishes_EndedAtUtc` partiell WHERE `RequesterLogin` IS NOT NULL OR `RequesterTwitchUserId`
  IS NOT NULL (Retention).
- CHECK `DurationDays BETWEEN 1 AND 365`.

### 6.2 `ChannelWishSettings` (1:1 zu `Channel`, PK = `ChannelId`, ON DELETE CASCADE)

| Spalte | Typ | Default | Grenzen |
|---|---|---|---|
| `DefaultDurationDays` | int | 30 | 1 bis 365 |
| `ProbationLeadDays` | int | 7 | 1 bis 30 |
| `PendingExpiryDays` | int | 14 | 1 bis 90 |
| `ProtectionThresholdPerDay` | numeric(10,2), null | null = Schutz aus | > 0 |
| `BotModeEnabled` | bool | false | |
| `BotModeActivatedByTwitchUserId`, `BotModeActivatedByLogin` | text, null | | bei Kontolöschung pseudonymisiert |
| `BotModeActivatedAtUtc` | timestamptz, null | | |
| `BotEditorCheckedAtUtc` | timestamptz, null | | letzte bestandene Editor-Prüfung |
| `BotSuspendedReason` | text, null | | `not_editor`; gesetzt heißt: Kanal läuft im manuellen Ablauf |

Die Zeile entsteht beim ersten Schreiben; fehlt sie, gelten die Defaults. Eine Änderung der
Kanal-Defaults wirkt nur auf künftige Wünsche und Verlängerungen, nie rückwirkend auf laufende
Fristen.

### 6.3 Verknüpfung von Wunsch und Slot (Abgleich, E9)

- `approved` → `active`: Kandidaten sind nicht archivierte Slots des Kanals mit derselben
  7TV-Emote-ID, die nach `ApprovedAtUtc` entstanden und mit keinem laufenden Wunsch verknüpft sind.
  Genau ein Kandidat mit gleichem Alias (ordinal) wird verknüpft. Gibt es keinen mit gleichem Alias,
  aber genau einen Kandidaten überhaupt, wird dieser verknüpft und der Alias am Wunsch nachgezogen.
  Bei mehreren Kandidaten verknüpft der Abgleich nichts; der Verwalter nutzt „Bestand übernehmen“.
- Start ist `AddedAt` des Slots. Ist es noch vorläufig, gilt `StartsAtAuthoritative = false`; wird
  es maßgeblich, verschiebt der Abgleich `StartsAtUtc` und `ExpiresAtUtc` um dieselbe Differenz.
- Umbenennen, Archivieren und Übernahme nach Abschnitt 5.
- Kanal-Zusammenführung (`ChannelIdentityService`): Wünsche wandern zum überlebenden Kanal, dessen
  Einstellungen gewinnen; kollidiert dabei der partielle Eindeutigkeitsindex, endet der jüngere
  Wunsch als `merged`.

### 6.4 `WishBotCredentials` (K4, genau eine Zeile, CHECK `Id = 1`)

| Spalte | Typ | Bemerkung |
|---|---|---|
| `SealedToken` | text, null | Format Abschnitt 7; null = kein Token |
| `ExpiresAtUtc` | timestamptz, null | `exp`-Claim |
| `SetAtUtc` | timestamptz, null | |
| `Status` | text | `none`, `unverified`, `valid`, `invalid`, `expired` |
| `BotSevenTvUserId` | text, null | vom Worker ermittelt |
| `LastVerifiedAtUtc` | timestamptz, null | |
| `LastErrorCode` | text, null | sprachneutral |
| `KillSwitch` | bool | Admin-Notaus |
| `PausedUntilUtc` | timestamptz, null | Abkühlzeit nach 429, übersteht einen Worker-Neustart |

### 6.5 Retention und Löschpfade

| Regel | Konstante in `RetentionPolicy` | Wirkung |
|---|---|---|
| Einlöser-Daten | `WishRequester` = 30 Tage | `RequesterTwitchUserId` und `RequesterLogin` auf null, 30 Tage nach `EndedAtUtc`; zusätzlich E14 |
| Beendete Wünsche | `EndedWish` = 365 Tage | Zeile löschen, 365 Tage nach `EndedAtUtc` |
| Audit | unverändert `AuditLogEntry` = 365 Tage | |

Beides läuft im bestehenden Tagesdurchlauf (`DataRetentionService.RunAsync`), also nur bei
`Retention:Enforce = true`; der Trockenlauf meldet Zählwerte in der Summenzeile. Die Kontolöschung
(`AccountDeletionService`) leert die Einlöser-Felder aller Wünsche mit der Twitch-ID des Kontos
sofort und pseudonymisiert `BotModeActivatedBy*`. Kanal-Purge löscht per Kaskade.

Datenschutz: Twitch-ID und Login des Einlösers sind neue Personendaten. Der Betreiber ergänzt sein
eigenes Markdown der Datenschutzerklärung (nicht im Repo) um Zweck, Datenarten und die 30-Tage-Frist,
nur Pflichtinhalt nach Art. 13. Das ist Voraussetzung für das Einschalten auf Prod, nicht für den
Merge. Mit Handeintrag allein fällt die Twitch-ID noch nicht an; der Text deckt Scheibe 3 gleich mit.

## 7. Vertrauensgrenze und Bot-Token (D8, Entscheidung 1)

Geänderte Vertrauensgrenze, ausdrücklich benannt: Auf dem Server liegt genau ein 7TV-Token, das des
eigenen Bot-Kontos, nie das eines Nutzers. Ein gestohlenes laufendes Token kann bis zum Notaus in
allen Kanälen schreiben, in denen das Bot-Konto Editor ist, an Freigaben und Audit-Log vorbei. In
Kauf genommen mit diesen Schutzmaßnahmen:

1. **Nur der Worker kann das Token lesen (E7).** Die Api versiegelt hybrid: ein zufälliger
   AES-256-Schlüssel verschlüsselt das Token (AES-GCM), ein öffentlicher RSA-Schlüssel (3072 Bit,
   OAEP mit SHA-256) verpackt den AES-Schlüssel. Ablageformat: `v1.` gefolgt von
   base64(verpackter Schlüssel, Nonce, Chiffrat, Tag). Konfiguration:
   `Wishes:BotMode:TokenPublicKey` an der Api, `Wishes:BotMode:TokenPrivateKey` nur am Worker
   (eigene Compose-Variablen, nichts davon im Repo, Regel 17). Unabhängig von
   `Auth:Twitch:TokenEncryptionKey`.
2. **Verworfene Varianten:** (a) symmetrischer Schlüssel an der Api: Der aus dem Internet
   erreichbare Prozess könnte entschlüsseln, „nie zurücklesen“ wäre nur Disziplin. (b) Klartext per
   Redis an den Worker: Das Token läge im Klartext auf dem Pub/Sub-Kanal, wäre für jeden Subscriber
   und für `MONITOR` sichtbar und ginge verloren, wenn der Worker gerade nicht läuft.
3. **Klartext in der Api** existiert nur während der einen Anfrage, die das Token setzt. Die Api
   prüft die Form (drei base64url-Segmente), liest `exp` ohne Signaturprüfung für die Anzeige,
   lehnt ein schon abgelaufenes Token ab, versiegelt, speichert mit `Status = unverified` und stößt
   den Worker über Redis an (`WISHBOT:` ohne Nutzlast, wie `BACKFILL:` nur ein Anstoß). Sie ruft mit
   dem Token **nie** 7TV auf.
4. **Der Worker** öffnet das Token, prüft es mit einem authentifizierten Lesezugriff, ermittelt die
   7TV-ID des Bot-Kontos und setzt `valid` oder `invalid`. Maßgeblich für den Ablauf ist sein
   gelesenes `exp`.
5. **Nie geloggt:** eigener typisierter HttpClient nur im Worker; weder Token noch
   `Authorization`-Kopf erscheinen in Logs, auch nicht auf Trace-Stufe und nicht in der
   Log-Erfassung nach Postgres (AK in K4).
6. **Notaus, zweistufig:** (a) Admin-Schalter `KillSwitch` in der Datenbank, den der Worker vor
   jedem Schreiben liest; wirkt ohne Stack-Update. (b) „Sign Out Everywhere“ in den
   7TV-Einstellungen des Bot-Kontos entwertet das Token bei 7TV. Dazu als harter Schalter
   `Wishes:BotMode:Enabled = false`. Bis geprüft ist, ob ein normaler Logout andere Sitzungen
   beendet, gilt: mit dem Bot-Konto nirgends normal ausloggen.
7. **Ablauf (F5):** Die Admin-Seite zeigt `exp`. Ab 7 Tagen vorher Hinweis auf der Seite und eine
   Log-Warnung je Tag. Erneuert wird von Hand über die Admin-Seite, ohne Stack-Update. Eine
   Automatik wie bei Fumobot ist Out of Scope.
8. **Ausfall und Rückfall (R3-10, R3-11):** Token fehlt, ist `invalid` oder `expired`, Notaus aktiv,
   oder der Schalter ist aus: **alle** Bot-Kanäle laufen im manuellen Ablauf. Editor-Rechte in
   einem Kanal entzogen: **dieser** Kanal läuft im manuellen Ablauf (`BotSuspendedReason`). Was dann
   mit offenen Bot-Aktionen geschieht: `approved` bleibt `approved`, zählt als Handlungsbedarf und
   die Karte zeigt den manuellen Hinzufügen-Knopf; `unconfirmed` wird sofort `presented`
   (`unconfirmed`); eine fällige Entfernung wird `presented` (`bot_unavailable`). Kehrt der Bot
   zurück, nimmt er nur auf, was noch `approved` ist; vorgelegte Wünsche bleiben beim Menschen.

Dokumente, die K4 im selben Commit ändert (R3-6): ein neuer englischer Eintrag in
`docs/DECISIONS.md` („zero-knowledge narrowed to user tokens; one sealed bot token; bot mode is
unofficial and not confirmed by 7TV“, Betrifft-Zeile mit den neuen Dateien), `docs/Architectur.md`
(Grundsatz 4 neu gefasst, Modul- und Worker-Liste), `PRODUCT.md` deutsch (Zero-Knowledge-Satz bei
Zeile 81, Zweck, Nutzer, Positionierung „Purge plus Wünsche“), `docs/Operations.md` englisch
(Runbook: Schlüsselpaar erzeugen, Token setzen und erneuern, Notaus, Rückfall, Rollback).

## 8. Eigentum des Bots (Fable-Schiedsspruch, unverändert übernommen)

- **Eigentum entsteht nur**, wenn ein v4-Read nach dem Hinzufügen genau einen Eintrag mit Emote-ID,
  Alias **und** `addedById` = 7TV-ID des Bot-Kontos zeigt. Dessen `addedAt` wird in
  `BotOwnedAddedAtUtc` festgehalten. Fehlt `addedById` oder ist es fremd: kein Eigentum.
- **Quelle der Bestätigung** ist die v4-Abfrage der Set-Einträge (`GqlSetEntriesQuery`, um
  `addedById` erweitert), nicht der v3-REST-Cache und nicht das EventAPI-Delta.
- **Vor jedem Entfernen** ein frischer Read ohne Wiederverwendung. Stimmen Emote-ID, Alias,
  `addedAt` und `addedById` nicht exakt mit dem Festgehaltenen überein: kein Entfernen, Eigentum
  endet, `presented` (`ownership_lost`).
- **Restrisiko, akzeptiert:** Zwischen diesem Read und `removeEmote` (Fenster unter einer Sekunde)
  kann ein Editor den Eintrag ändern; 7TV kennt nur Emote-ID und Alias. Gleiche Risikoklasse wie
  D33 der Slot-Spec.
- **Erkennung und Reparatur:** Ein Read direkt nach dem Entfernen vergleicht mit dem Read davor.
  Fehlt ein nicht bot-eigener Eintrag derselben Emote-ID, fügt der Bot ihn genau einmal unter
  demselben Alias wieder hinzu (ohne Eigentum, ohne Frist), schreibt `wish.botRepair` und setzt den
  Wunsch auf `presented` (`repair_review`).
- **Eigentum endet** durch Umbenennen, durch Entfernen und Hinzufügen außerhalb des Bots, durch
  „Behalten“ und durch Abschalten des Bot-Modus. Ein Moduswechsel überträgt nie Eigentum an
  Einträgen, die ein Mensch hinzugefügt hat. Ein zweiter Alias desselben Emotes bleibt unberührt.
- **F12:** Einträge, die ein Verwalter mit eigenem Token über Emote Purge hinzugefügt hat, entfernt
  der Bot nie; sie werden vorgelegt (`not_bot_owned`).

## 9. Child Issues

| # | Titel | Priorität | Aufwand | Status | Hängt ab von |
|---|---|---|---|---|---|
| K1 | Backend-Kern: Datenmodell, Lebenszyklus, Handeintrag, Verwalter-API, Slot-Abgleich, Live-Event, Retention, Schalter | P1 | 5 Tage | offen | keine |
| K2 | Ablauf-Lauf im Worker: Verfall, Fälligkeit, Schutz, Lückenregel | P1 | 2 Tage | offen | K1 |
| K3 | Frontend manueller Modus: Tab je Kanal, Einstellungen, kanalübergreifende Seite, Zähler in der Kopfzeile | P1 | 5 Tage | offen | K1 (Vertrag), K2 (nur Live-Probe) |
| K4 | Bot-Backend: Token-Ablage und Admin-Seite, Schreib-Client, Warteschlange, Editor-Prüfung, Eigentum, Notaus, Doku | P2 | 6 Tage | offen | K1, K2 |
| K5 | Bot-Frontend: Aktivierungsdialog mit Editor-Mutation (D7), Abschalten, Bot-Zustände auf den Karten | P2 | 2,5 Tage | offen | K3, K4, Probe „Editor-Mutation“ |
| keine | Scheibe 3: IRC-Erfassung über `custom-reward-id` (kein Issue) | später | ungeschätzt | nicht geplant | K1; P1-Rest (Nicht-Mod-Einlösung) |

Scheibe 3 trägt mit, sobald sie geplant wird: Link-Prüfung, Duplikate über die
IRC-Nachrichten-ID, Begrenzung je Zuschauer, F10 (Bot nicht mehr anonym, Chat-Antworten) und den
Shared-Chat-Nebenbefund: Einlösungen aus fremden Räumen (`source-room-id`) werden ignoriert, die
vorhandene `SharedChatRule` (`src/EmotePurge.Core/Chat/SharedChatRule.cs:23`) deckt das ab.

**Anforderung an das spätere Scheibe-3-Issue: „Belohnung anlernen“.** Ein Mod bekommt die
Belohnungs-ID (`custom-reward-id`), ohne sie je zu sehen:

1. Ein Verwalter startet in den Wunsch-Einstellungen des Kanals das Anlernen.
2. Der Worker meldet für diesen Kanal die nächste Chatnachricht mit `custom-reward-id` innerhalb
   eines Zeitfensters (Richtwert 10 Minuten). Der Mod löst die Belohnung dafür selbst einmal mit
   Text ein oder wartet auf eine echte Einlösung.
3. Die Oberfläche zeigt den eingelösten Text zur Bestätigung. Bestätigen speichert die ID am Kanal;
   ohne Bestätigung oder nach Ablauf des Fensters wird nichts gespeichert.
4. Je Kanal sind mehrere Belohnungen möglich, jede mit eigener Standardlaufzeit. Sie belegt die
   Laufzeit des einzelnen Wunsches vor (E1) und ersetzt für diese Wünsche den Kanal-Default.
5. Das funktioniert nur für Belohnungen **mit Texteingabe** (P1-Befund: ohne Texteingabe erscheint
   keine Chatnachricht). Einen anderen Weg gibt es ohne Broadcaster-Scope nicht: Helix „Get Custom
   Reward“ braucht das Token des Broadcasters.

Das sind Anforderungen für Scheibe 3, keine Akzeptanzkriterien von K1 bis K5. Das Datenmodell aus
K1 ist darauf vorbereitet (`Source = irc`, `DurationDays` je Wunsch, Zustand `pending`); die Tabelle
der angelernten Belohnungen entsteht erst mit Scheibe 3.

## 10. Dependency Graph

```
K1 ──┬──► K2 ──┬──► K4 ──┐
     │         │         ├──► K5
     └──► K3 ──┴─────────┘
                               Scheibe 3 (später, nur nach K1)
```

K2 und K3 können nach K1 parallel laufen. K3 braucht K2 nur für seine Live-Probe mit einem fälligen
Wunsch.

## 11. Sequencing Rationale

- **K1 zuerst**, weil es jeden Vertrag festlegt, gegen den die anderen bauen (Entität, Zustände,
  Routen, Fehlercodes, Event-Typ, Audit-Konstanten). Hinter dem Schalter erreicht es niemand.
- **K2 vor K4**, weil der Bot nur ausführt, was der Ablauf-Lauf als fällig und entfernbar
  entschieden hat. Die Entscheidung (Schutz, Lücke) ist reine Logik und ohne Token testbar.
- **K1 bis K3 sind für sich auslieferbar** (Scheibe 1): Scheitert der Bot-Teil an 7TV, bleibt der
  manuelle Modus vollständig.
- **K4 vor K5**, weil der Aktivierungsdialog die Editor-Prüfung des Workers braucht. K5 startet
  erst, wenn die Probe zur Editor-Mutation ein Schema geliefert hat; fällt sie durch, bleibt K5 bei
  einer Anleitung („Bot-Konto in 7TV von Hand als Editor eintragen“) plus Prüfknopf.
- Umgekehrte Reihenfolge (Bot zuerst) wäre Variante C des Entwurfs und wurde verworfen.

## 12. Definition of Done (Epic)

1. K1 bis K5 gemergt; Zweitmeinung (Codex Sol) je Branch eingeholt und vorgelegt.
2. `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false` und
   `npm --prefix web run e2e` grün; `node scripts/coverage-local.mjs` für jeden PR gelaufen.
3. Beide Migrationen von Hand auf Prod angewendet, jeweils vor dem Image.
4. `Wishes:Enabled` auf Prod an, `Wishes:PilotChannelIds` auf die Pilot-Kanäle gesetzt,
   Datenschutztext ergänzt.
5. Bot-Modus in mindestens einem Kanal aktiv; die Live-Proben AK58 bis AK63 und AK80 dokumentiert.
6. Ein Wunsch mit 7 Tagen Laufzeit hat im Pilot den ganzen Weg live durchlaufen: Freigabe,
   Bot-Hinzufügen, Bestätigung, Probezeit, Bot-Entfernung oder Vorlage.
7. DECISIONS, Architectur.md, PRODUCT.md, Operations.md, CLAUDE.md (Umsetzungsstand, Worker-Liste:
   sechzehn Hosted Services) und Backlog `A18` sind nachgezogen.

## 13. Beobachtung nach Aktivierung (Pilot)

Kein formales Fenster und keine Freigabebedingung. Freigeschaltet wird zuerst in den Kanälen des
anfragenden Mods; der eigene Kanal des Betreibers ist kein Affiliate und hat keine Kanalpunkte.

- **Erster Funktionstest mit 7 Tagen Laufzeit**, damit Probezeit, Fälligkeit, Vorlage und
  Bot-Entfernung binnen einer Woche live sichtbar werden. Kürzere Laufzeiten (1 bis 6 Tage) bleiben
  einstellbar und folgen denselben Regeln (E2, E4).
- **Beobachtungszeitraum: 30 Tage ab Aktivierung** (gemeint ist der Zeitraum, nicht die Laufzeit;
  unabhängig von den 30 Tagen Lebensdauer des Bot-JWT).
- **Messgrößen (R3-9):** (a) mindestens 90 % der fällig gewordenen Wünsche sind binnen 24 Stunden
  nach `ExpiresAtUtc` in einem Endzustand oder verlängert; (b) 0 vom Bot entfernte fremde Einträge,
  also 0 Audit-Einträge `wish.botRepair`.
- **Auswertung:** eine SQL-Abfrage über `Wishes` (Ablauf gegen `StatusChangedAtUtc`) und eine über
  das Audit-Log; beide stehen im Operations-Runbook. Keine eigene Oberfläche dafür.

## 14. Rollout und Rollback

Schalter (alle Default `false` bzw. leer, also aus für andere Betreiber):

| Schlüssel | Compose-Variable | Wirkung |
|---|---|---|
| `Wishes:Enabled` | `WISHES_ENABLED` | Api-Routen, Tab, Zähler, Ablauf-Lauf |
| `Wishes:PilotChannelIds` | `WISHES_PILOT_CHANNEL_IDS` | E8 |
| `Wishes:ExpiryIntervalMinutes` | `WISHES_EXPIRY_INTERVAL_MINUTES` | E6 |
| `Wishes:BotMode:Enabled` | `WISHES_BOT_MODE_ENABLED` | Bot-Worker, Aktivierung, Admin-Seite |
| `Wishes:BotMode:TokenPublicKey` | nur am Dienst `api` | E7 |
| `Wishes:BotMode:TokenPrivateKey` | nur am Dienst `worker` | E7 |
| `Wishes:BotMode:WriteDelaySeconds` | keine | Abstand zwischen zwei Schreibzugriffen, Default 2 |

Reihenfolge je Auslieferung: Migration von Hand über den Tunnel (`list`, `update`, `list`), dann
Images, dann Schalter. Additive Migrationen stören das laufende alte Image nicht. Den Deploy-Hinweis
gegen **alle** ausstehenden Migrationen prüfen, nicht nur gegen die eigene.

Rollback: Schalter aus genügt in jedem Fall; die Tabellen bleiben liegen und stören kein älteres
Image. Bot-Notaus in dieser Reihenfolge: Admin-Schalter, dann „Sign Out Everywhere“, dann
`WISHES_BOT_MODE_ENABLED=false`, dann Token löschen. Nach einem Notaus gilt der Rückfall aus
Abschnitt 7 Punkt 8; nichts geht verloren, alles Offene steht als Handlungsbedarf in der Übersicht.

## 15. Out of Scope

- **Scheibe 3** (IRC-Erfassung, Anlernen der Belohnung) samt F10 und der Rest von P1.
- **F11:** Bot-Entfernen auf der normalen Nutzungsseite. Die Zustimmung nach D7 deckt es nicht, es
  öffnete „würde ich nicht bauen #3“ weit über dieses Epic hinaus. Eigene Entscheidung.
- **Helix-Rückerstattung** („Update Redemption Status“): bleibt in Twitchs eigener Warteschlange.
- **Emote Purge legt die Belohnung selbst an.** Helix „Create Custom Rewards“ braucht ein Token
  des Broadcasters mit `channel:manage:redemptions`; ein Mod-Status des Bot-Kontos reicht nicht. Der
  Vorteil wäre, dass Emote Purge Einlösungen dann auch erledigen oder erstatten könnte (Helix
  erlaubt das nur für Belohnungen derselben Client-ID). Mögliche spätere Ausbaustufe mit
  Broadcaster-Login, eigene Entscheidung.
- **EventSub** mit Streamer-Login und `channel.chat.message` als Erfassungsquelle.
- Chat-Antworten oder Whisper des Bots, Discord-Webhook, E-Mail. Erinnerung nur in der Oberfläche.
- „Einer rein, einer raus“ (A1/A15), Bestenliste der Wünsche.
- Automatische Erneuerung des Bot-JWT (F5, zweiter Teil).
- Ein zweiter Alias desselben Emotes als eigener Wunsch (E13).
- Auflösen des Twitch-Logins beim Handeintrag über Helix.
- Wiederverwendung der Import-Kollisionslogik (#368) für Wünsche: Ein Wunsch ist ein einzelner
  Eintrag; ein Alias-Konflikt wird gemeldet, nicht stapelweise aufgelöst (F2).

## 16. Restrisiken

| # | Risiko | Umgang |
|---|---|---|
| RR1 | Worker-Ausfälle je Kanal sind nicht persistiert; eine Zähllücke durch einen Ausfall bleibt unerkannt und ein beliebtes Emote kann trotz Schutz entfernt werden (Entscheidung 2, R3-14) | benannt; Rückweg „wieder hinzufügen“; eine Lückentabelle wäre ein eigenes Vorhaben |
| RR2 | Wettlauf zwischen frischem Read und `removeEmote` (D33-Klasse) | Erkennung und einmalige Reparatur, Audit, Messgröße im Pilot |
| RR3 | Bot-Modus ist von 7TV nicht bestätigt; 7TV kann das Website-JWT, das Schema oder das Editor-Modell jederzeit ändern | Rückfall auf manuellen Ablauf ist der Normalpfad jedes Fehlers; DECISIONS nennt den Betrieb „inoffiziell“ |
| RR4 | Gestohlenes Token schreibt bis zum Notaus an Emote Purge vorbei | Abschnitt 7; Editor-Vergabe je Kanal begrenzt die Reichweite |
| RR5 | Ob ein normaler Logout das Server-Token entwertet, ist ungeprüft | Betriebsregel „nirgends normal ausloggen“; Probe AK62 |
| RR6 | 7TV-Reads können veralten; ein Bestätigungs-Read kann den neuen Eintrag verspätet zeigen | E11, danach Vorlage statt Annahme |
| RR7 | `presented` ohne Timeout kann liegen bleiben | Zähler und Übersicht halten es sichtbar; E14 begrenzt die Personendaten |
| RR8 | Das Ereignis `wish.changed` nennt auf dem kanalübergreifenden Stream den Kanalnamen jedem eingeloggten Nutzer | gleiche Klasse wie `live.changed` heute; s. Abschnitt 17 Punkt 6 |
| RR9 | Der Einwilligungsgedanke aus D7 (nur wer in 7TV Editoren verwalten darf, kann den Bot eintragen) ist unbelegt (R3-15) | Probe AK80; fällt sie durch, entscheidet der Betreiber über D7 neu |
| RR10 | Einlöser-Daten werden nur bei `Retention:Enforce = true` geleert | auf Prod an; für fremde Betreiber im Operations-Text benannt |

## 17. Offene Widersprüche für den Betreiber

1. **`offen` hat in diesem Epic keinen Erzeuger.** Der Brief legt den ganzen Lebenszyklus in K1,
   der Handeintrag gilt aber als freigegeben und die IRC-Erfassung ist Scheibe 3. Die Spec baut
   Freigeben, Ablehnen und Verfall trotzdem in K1 bis K3 (nur über Tests erreichbar), damit Scheibe 3
   nur die Erfassung ergänzt. Alternative: bis Scheibe 3 weglassen und K1/K3 um etwa 1,5 Tage kürzen.
2. **Token-Schlüssel.** Der Entwurf sagt „verschlüsselt wie `User.TwitchRefreshToken`“ (symmetrisch,
   Schlüssel an der Api), der Brief verlangt einen eigenen Schlüssel und lässt zwei Varianten offen.
   Die Spec wählt eine dritte (E7, asymmetrisch), weil nur sie „nie zurücklesen“ erzwingt.
3. **Entscheidung 4 gegen R3-12.** „30 Tage nach dem Endzustand“ greift nie, solange ein Wunsch
   `vorgelegt` bleibt. E14 ergänzt eine Kappe; das ist eine Erweiterung der Entscheidung.
4. **D7 gegen die Laufzeit je Wunsch.** D7 behält „Frist“ dem Aktivierenden und dem Broadcaster vor;
   die Betreiber-Ergänzung erlaubt jedem Verwalter, die Laufzeit eines einzelnen Wunsches zu setzen.
   Die Spec liest D7 als Regel für die **Kanal-Defaults** und lässt die Laufzeit je Wunsch allen
   Verwaltern. Folge: Ein Twitch-Mod ohne 7TV-Rechte kann im Bot-Modus einen Wunsch mit 1 Tag
   freigeben, den der Bot dann nach 1 Tag entfernt (nur bot-eigene Wunsch-Einträge).
5. **Takt des Ablauf-Laufs.** Der Entwurf nennt `DataRetentionWorker` als Vorlage (Tagestakt hinter
   der Boot-Recovery). Das verträgt sich nicht mit „binnen 24 h“ und Laufzeiten ab 1 Tag; die Spec
   nimmt 15 Minuten (E6).
6. **Kanalübergreifender Zähler gegen Ereignis-Sichtbarkeit.** „Live per SSE“ für den Zähler heißt,
   `wish.changed` auf `/api/channels/live-events` weiterzuleiten, wo jeder eingeloggte Nutzer den
   Kanalnamen sieht. Alternative ohne dieses Leck: Zähler nur beim Laden und alle 5 Minuten.
7. **Pilot-Liste.** Der Brief nennt nur zwei Schalter; „zuerst in den Kanälen des anfragenden Mods“
   braucht einen dritten (E8).
8. **Lückenregel.** Der Brief sagt „bei Lücke immer vorlegen“, der Entwurf „bei eingeschaltetem
   Schutz“. Die Spec folgt dem Entwurf (E5), weil ohne Schutz kein Bewertungsfenster existiert.
9. **Ort der Einstellungen.** Der Entwurf legt sie in den Wünsche-Tab, der Code-Kommentar am
   Settings-Tab (`channel-workspace-layout.ts:105-110`) erwartet dort einen zweiten Abschnitt. Die
   Spec folgt dem Entwurf; der Settings-Tab bleibt unverändert an den Backfill-Schalter gekoppelt.

---

# K1: Backend-Kern

Labels: `feat`, `api`, `infrastructure`, `migration`. Teil des Epics „Wunsch-Emotes mit Ablauf“.

## Ziel

Alles, was die anderen Kinder als Vertrag brauchen: Tabellen, Zustandsmaschine, Verwalter-API,
Abgleich mit Slots, Live-Event, Retention, Schalter. Hinter `Wishes:Enabled`, ohne Oberfläche.

## Umfang

Enthalten: Abschnitte 5, 6.1 bis 6.3, 6.5 dieser Spec; Schalter `Wishes:Enabled`,
`Wishes:PilotChannelIds`. Nicht enthalten: Ablauf-Lauf (K2), jede 7TV-Schreibaktion (K4),
Bot-Felder werden angelegt, aber von nichts gesetzt.

## Implementation Details

**Core** (`src/EmotePurge.Core/`): `Entities/Wish.cs`, `Entities/ChannelWishSettings.cs`,
`Entities/WishStatus.cs` (Konstanten wie `AuditActions`, keine Enum-Zahlen), `Services/IWishService.cs`
(Befehle und Lesen je Kanal), `Services/IWishOverviewService.cs` (kanalübergreifend),
`Services/IWishReconcileService.cs`, `Services/WishTransitions.cs` (reine Tabelle „von, Aktion →
nach“, die einzige Stelle, die Übergänge kennt), `Services/WishOptions.cs` (Muster
`ChatLogBackfillOptions`, `SectionName = "Wishes"`, Eager-Validation), neue Konstanten in
`AuditActions` und `LiveEvents`, `RetentionPolicy.WishRequester`, `RetentionPolicy.EndedWish`.

**Infrastructure:** Implementierungen unter `Services/`, Konfiguration in `AppDbContext`, Migration
`AddWishes`, Registrierung in `ServiceCollectionExtensions`. Aufrufe von
`ReconcileChannelAsync` nach dem erfolgreichen Speichern in `SevenTvSyncService` (Vollsync nach
`SaveSyncAsync`, Delta nach dem Commit in `ApplyEmoteSetUpdateAsync`) und nach den Slot-Schreiben in
`EmoteService` (`:733`, `:800`). Der Aufruf läuft **nach** dem Commit der Slot-Transaktion in einer
eigenen; ein Fehler dort wird geloggt und bricht den Sync nicht ab (K2 heilt nach). Kanäle ohne
laufenden Wunsch kosten genau eine Indexabfrage.

**Api:** neue Datei `Endpoints/WishEndpoints.cs` (`MapWishEndpoints`), neuer Filter
`Auth/WishSettingsAuthorizationFilter.cs`.

Kanal-Gruppe `/api/channels/{channelName}/wishes`: `RequireAuthorization` →
`ChannelNameValidationFilter` → `ChannelManagementAuthorizationFilter` → im Handler Schalterprüfung
(404 `wishes_disabled`, auch für Kanäle außerhalb der Pilot-Liste). Diese Reihenfolge ist der
Fehlervertrag: 400 vor 403 vor 404.

| Route | Rate-Limit | Zweck |
|---|---|---|
| `GET /` `?view=open\|running\|ended&page&pageSize` | `InteractiveRead` | Liste; `open` = Handlungsbedarf, `running` = `active` und `unconfirmed`, `ended` = Endzustände |
| `GET /{id}` | `InteractiveRead` | Detail mit `usage` (E4, E16) |
| `POST /` | `Bookkeeping` | Handeintrag |
| `POST /adopt` | `Bookkeeping` | Bestand übernehmen |
| `PATCH /{id}` | `Bookkeeping` | Alias, Laufzeit; nur in `pending`, `approved`, `failed` |
| `POST /{id}/approve`, `/reject`, `/extend`, `/keep`, `/close`, `/retry`, `/re-add`, `/complete-removal`, `/recheck` | `Bookkeeping` | Übergänge aus Abschnitt 5 |
| `GET /settings` | `InteractiveRead` | Kanal-Einstellungen mit Defaults |
| `PUT /settings` | `Bookkeeping`, zusätzlich `WishSettingsAuthorizationFilter` | im manuellen Modus jeder Verwalter; im Bot-Modus nur Broadcaster, Aktivierender, globaler Admin, sonst 403 `wish_settings_restricted` |

Kanalübergreifend, Gruppe `/api/wishes` (`RequireAuthorization`, `InteractiveRead`):
`GET /mine` (Handlungsbedarf und Wünsche in Probezeit über alle verwaltbaren Kanäle, sortiert nach
`ExpiresAtUtc`, dann `CreatedAtUtc`) und `GET /summary` (`{ "actionCount": 3 }`). Die Menge der
verwaltbaren Kanäle kommt aus `IMyChannelsService` (Broadcaster, Moderator, globaler Admin; nicht
7TV-Editor allein), gefiltert auf Schalter und Pilot-Liste.

Formen (camelCase, Zeiten als ISO-8601 UTC):

```
POST /api/channels/{c}/wishes
{ "sevenTvEmoteId": "01H…", "emoteName": "…", "alias": "…",
  "durationDays": 30, "requesterLogin": null }
→ 201 WishDto

POST …/wishes/adopt        { "slotId": "…", "durationDays": 30, "startsAtUtc": null }
POST …/{id}/approve        { "durationDays": 7, "alias": null }
POST …/{id}/re-add         { "mode": "timed" | "permanent", "durationDays": 30 }
PUT  …/wishes/settings     { "defaultDurationDays": 30, "probationLeadDays": 7,
                             "pendingExpiryDays": 14, "protectionThresholdPerDay": null }

WishDto { id, sevenTvEmoteId, emoteName, alias, source, status, statusReason,
          requesterLogin, durationDays, createdAtUtc, approvedAtUtc, startsAtUtc,
          startsAtAuthoritative, probationStartsAtUtc, expiresAtUtc, endedAtUtc,
          slotId, extensionCount, permanent, botOwned, mode: "manual" | "bot" }
WishDetailDto = WishDto + usage { windowFromUtc, windowToUtc, total, perDayMean,
                                  rank, rankOf, coverageGap }
```

`requesterTwitchUserId` verlässt die Api nie. `adopt` ohne `startsAtUtc` nimmt `AddedAt` des Slots;
ein gesetzter Wert darf nicht in der Zukunft und nicht vor `AddedAt` liegen.

Neue `ApiErrorCodes` (Regel 7: je Code `api-error.ts` und `errors.api.*` in de/en; die
Frontend-Hälfte liefert K3, K1 legt die Codes an und dokumentiert sie im PR): `wishes_disabled`
(404), `wish_not_found` (404), `wish_state_conflict` (409), `wish_emote_already_in_set` (409, mit
`slotIds`), `wish_already_running` (409, mit `wishId`), `wish_duration_out_of_range` (400),
`wish_alias_invalid` (400), `wish_requester_invalid` (400), `wish_settings_out_of_range` (400),
`wish_settings_restricted` (403), `wish_slot_not_found` (404), `wish_slot_already_linked` (409).

F2 beim Handeintrag und bei der Freigabe: Liegt die Emote-ID schon aktiv im Set, antwortet die Api
409 `wish_emote_already_in_set`; der Client bietet „Bestand übernehmen“ an. Läuft schon ein Wunsch
für die Emote-ID: beim Handeintrag 409 `wish_already_running`, bei der Freigabe eines `pending`
greift E13. Ein volles Set (`ActiveEmoteSetCapacity`) blockiert nicht, die Antwort trägt nur
`setFull: true` als Hinweis, weil die Zahl veralten kann.

`GET /api/channels/{c}/permissions` (`ChannelEndpoints.cs`, heute u. a. `TagRunsEnabled`) bekommt
`wishesEnabled` (Schalter und Pilot-Liste für diesen Kanal).

**Live-Event:** `LiveEvents.WishChanged = "wish.changed"`, mit Kanal, ohne weitere Nutzlast. In
`ChannelTypes` aufnehmen; das Prädikat des kanalübergreifenden Streams (`LiveEndpoints.cs:92`) lässt
zusätzlich `wish.changed` durch. Veröffentlicht nach jedem Commit, der einen Wunsch oder die
Einstellungen ändert, höchstens einmal je Commit.

**Audit** (atomar mit dem `SaveChanges` der Aktion, Details nur `wishId`, `sevenTvEmoteId`,
`durationDays`, nie Alias, nie Einlöser): `wish.create`, `wish.adopt`, `wish.approve`,
`wish.reject`, `wish.extend`, `wish.keep`, `wish.remove`, `wish.close`, `wish.retry`,
`wish.readd`, `wish.settings`. Die Frontend-Übersetzungen `audit.actions.*` liefert K3.

## Akzeptanzkriterien

1. **AK1** Migration `AddWishes` läuft auf einer Kopie der Dev-Datenbank durch;
   `dotnet ef migrations list` zeigt danach nichts Ausstehendes; `Wishes` und `ChannelWishSettings`
   sind leer, keine bestehende Tabelle hat eine geänderte Spalte.
2. **AK2** `WishTransitions` lässt genau die Übergänge aus Abschnitt 5 zu; jeder nicht gelistete
   (Zustand, Aktion) ergibt 409 `wish_state_conflict`, geprüft als vollständige Matrix.
3. **AK3** Zwei gleichzeitige `approve` auf denselben Wunsch: genau einer 200, der andere 409, ein
   Audit-Eintrag.
4. **AK4** Handeintrag mit `durationDays` 0 oder 366 → 400 `wish_duration_out_of_range`; mit 1 und
   365 → 201; ohne Wert gilt der Kanal-Default.
5. **AK5** Handeintrag für eine Emote-ID, die aktiv im Set liegt → 409
   `wish_emote_already_in_set` mit den Slot-IDs; anschließendes `adopt` mit dieser Slot-ID → 201,
   `status = active`, `botOwned = false`, `startsAtUtc` = `AddedAt` des Slots.
6. **AK6** Freigabe eines `pending`, während ein `active`-Wunsch derselben Emote-ID läuft: Der
   laufende hat danach `ExpiresAtUtc` um die Laufzeit des neuen verlängert und `ExtensionCount + 1`,
   der neue ist `merged` mit `MergedIntoWishId`.
7. **AK7** Abgleich: `approved` plus genau ein neuer Slot gleicher Emote-ID und gleichen Alias →
   `active`, `SlotId` gesetzt. Mit zwei Kandidaten bleibt der Wunsch `approved`.
8. **AK8** Abgleich: Slot mit vorläufigem `AddedAt` → `startsAtAuthoritative = false`; nach dem
   Vollsync mit maßgeblichem Datum sind `StartsAtUtc` und `ExpiresAtUtc` um dieselbe Differenz
   verschoben, `DurationDays` unverändert.
9. **AK9** Abgleich Umbenennen (R3-2): Der Slot bekommt einen neuen Namen → Wunsch bleibt `active`
   am selben Slot, `Alias` nachgezogen, `ExpiresAtUtc` unverändert, `BotOwned` falsch.
10. **AK10** Abgleich Entfernen und Wieder-Hinzufügen (R3-3): archivierter Slot →
    `removed_externally`; ein neuer Slot derselben Emote-ID nach 5 h → Wunsch wieder `active` am
    neuen Slot mit alter Frist; nach 7 h → bleibt `removed_externally`; zwei neue Slots binnen 6 h →
    bleibt `removed_externally`.
11. **AK11** `complete-removal` binnen 6 h auf einen Wunsch, den der Abgleich schon auf
    `removed_externally` gesetzt hat → `removed`.
12. **AK12** `re-add` mit `mode: "permanent"` → `approved` mit `Permanent`; der Abgleich führt beim
    Erscheinen des Slots nach `kept`, nicht nach `active`.
13. **AK13** Filter-Matrix an den echten Routen (`Api.Tests`): ungültiger Name 400, nicht
    eingeloggt 401, Nicht-Verwalter 403, 7TV-Editor ohne Mod-Status 403, Verwalter bei
    ausgeschaltetem Schalter 404 `wishes_disabled`, Verwalter eines Kanals außerhalb der Pilot-Liste
    404, Nicht-Verwalter bei ausgeschaltetem Schalter 403 (nicht 404).
14. **AK14** `PUT /settings` bei `BotModeEnabled = true`: Twitch-Mod, der nicht aktiviert hat → 403
    `wish_settings_restricted`; Broadcaster, Aktivierender und globaler Admin → 200.
15. **AK15** `GET /api/wishes/mine` enthält nur Wünsche aus Kanälen, die der Aufrufer verwalten
    darf; ein Konto, das nur 7TV-Editor eines Kanals ist, sieht dessen Wünsche nicht.
16. **AK16** `wish.changed` erscheint nach einem Handeintrag auf
    `GET /api/channels/{c}/live` dieses Kanals und auf `/api/channels/live-events`, nicht auf dem
    Kanal-Stream eines anderen Kanals.
17. **AK17** Retention: Ein Wunsch mit `EndedAtUtc` vor 31 Tagen hat nach dem Durchlauf beide
    Einlöser-Felder null, einer vor 29 Tagen nicht; ein `presented` mit `ExpiresAtUtc` vor 31 Tagen
    hat sie ebenfalls null (E14); ein Wunsch mit `EndedAtUtc` vor 366 Tagen ist gelöscht. Mit
    `enforce = false` ändert sich nichts und die Summenzeile nennt die Zählwerte.
18. **AK18** Kontolöschung eines Kontos, dessen Twitch-ID als Einlöser an zwei Wünschen steht:
    beide Felder an beiden Zeilen null. Kanal-Purge: keine Zeile in `Wishes` und
    `ChannelWishSettings` für den Kanal.
19. **AK19** Kein Audit-Eintrag eines Wunsch-Vorgangs enthält Alias, Emote-Namen oder
    Einlöser-Login in `DetailsJson`.
20. **AK20** `CoreAssemblyReferenceTests` bleibt grün; kein Handler in `WishEndpoints.cs` nimmt
    `AppDbContext` oder `IConnectionMultiplexer`.
21. **AK21** Live-Verifikation (Regel 16) auf dem lokalen Stack gegen echtes 7TV mit dem
    Testkonto des Betreibers: Handeintrag per `curl` mit Session-Cookie, Emote von Hand in 7TV
    hinzufügen, nach dem nächsten Sync ist der Wunsch `active` mit dem `added_at` aus 7TV;
    umbenennen, entfernen, wieder hinzufügen und jeweils das Ergebnis aus AK9 und AK10 an der echten
    Datenbank ablesen. Das Set des Testkontos danach wiederherstellen.

## Testplan

| Ebene | Projekt | Was | Anzahl |
|---|---|---|---|
| Unit | `Infrastructure.Tests/Unit` | `WishTransitions` (Matrix), Fensterfunktion E4 samt Rängen, Validierung der Grenzen | +25 |
| Integration | `Infrastructure.Tests/Integration` (Postgres-Fixture) | `WishService` (Befehle, bedingte Updates, E13, Indizes), `WishReconcileService` (AK7 bis AK12, Kanal-Zusammenführung), Retention, Kontolöschung, Purge | +35 |
| Api | `Api.Tests` | Filter-Matrix AK13, AK14, neuer `WishSettingsAuthorizationFilter` | +12 |
| Live | lokal | AK21 | 1 Durchgang |

Keine Tests für die dünnen Handler (Regel 11). Sync-Tests des Bestands bleiben unverändert grün; ein
zusätzlicher Integrationstest zeigt, dass ein Fehler im Abgleich den Sync nicht abbricht.

## Files Reference

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Core/Entities/Wish.cs`, `ChannelWishSettings.cs`, `WishStatus.cs` | neu |
| `src/EmotePurge.Core/Services/IWishService.cs`, `IWishOverviewService.cs`, `IWishReconcileService.cs`, `WishTransitions.cs`, `WishOptions.cs` | neu |
| `src/EmotePurge.Core/Entities/AuditLogEntry.cs:9-52` | Konstanten `wish.*` |
| `src/EmotePurge.Core/Messaging/LiveEvents.cs:67-72` | `WishChanged`, `ChannelTypes` |
| `src/EmotePurge.Core/Services/RetentionPolicy.cs` | zwei Konstanten |
| `src/EmotePurge.Infrastructure/Persistence/AppDbContext.cs` | zwei Entitäten |
| `src/EmotePurge.Infrastructure/Migrations/*_AddWishes.cs` | neu |
| `src/EmotePurge.Infrastructure/Services/WishService.cs`, `WishOverviewService.cs`, `WishReconcileService.cs` | neu |
| `src/EmotePurge.Infrastructure/Services/SevenTvSyncService.cs:209`, `:259` | Abgleich nach Commit |
| `src/EmotePurge.Infrastructure/Services/EmoteService.cs:733`, `:800` | Abgleich nach Commit |
| `src/EmotePurge.Infrastructure/Services/DataRetentionService.cs:94-120` | zwei Regeln, Summenzeile |
| `src/EmotePurge.Infrastructure/Services/AccountDeletionService.cs:131` | Einlöser leeren, Aktivierenden pseudonymisieren |
| `src/EmotePurge.Infrastructure/Services/ChannelIdentityService.cs` | Wünsche beim Zusammenführen mitnehmen |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs:339` | Optionen, Dienste |
| `src/EmotePurge.Api/Endpoints/WishEndpoints.cs`, `Auth/WishSettingsAuthorizationFilter.cs` | neu |
| `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs` (`/permissions`), `LiveEndpoints.cs:92` | Feld, Prädikat |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | zwölf Codes |
| `docker-compose.yml`, `docker-compose.prod.yml`, `.env.example` | `WISHES_ENABLED`, `WISHES_PILOT_CHANNEL_IDS` an `api` und `worker` |
| `docs/DECISIONS.md` | englischer Eintrag: Wunsch-Modell, zustandsbasierter Abgleich, Schalter |
| `docs/Feature-Ideen-2026-08-01.md` | `A18`, Verweis an #3 |

## Abhängigkeiten

Keine. Erster PR des Epics.

---

# K2: Ablauf-Lauf im Worker

Labels: `feat`, `worker`. Hängt ab von K1.

## Ziel

Ein Hosted Service, der Fristen durchsetzt, ohne je nach 7TV zu schreiben: offene Wünsche verfallen
lassen, fällige feststellen, Schutz und Lücke bewerten, vorlegen oder zur Bot-Entfernung vormerken.

## Umfang

`WishExpiryWorker` (konkrete Klasse, nur per `AddHostedService`, Regel 5), `IWishExpiryService`
(Infrastructure) und `WishExpiryPolicy` (rein, Core). Keine Migration.

## Implementation Details

- Takt nach E6 mit `PeriodicTimer` und `TimeProvider`. Bei `Wishes:Enabled = false` beendet sich
  der Dienst nach einer Logzeile (Muster `ChatLogBackfillWorker`).
- Je Durchlauf, in dieser Reihenfolge: (1) `pending` älter als `PendingExpiryDays` des Kanals →
  `expired`. (2) Abgleich für jeden Kanal mit laufenden Wünschen (E9). (3) Für jeden `active` mit
  `ExpiresAtUtc` ≤ jetzt die Entscheidung aus der Tabelle unten. (4) Ein `wish.changed` je
  betroffenem Kanal. (5) Eine Logzeile mit reinen Zählwerten, nur wenn sich etwas geändert hat.
- Ein Wunsch mit `StartsAtAuthoritative = false` wird nicht fällig gestellt, solange der Kanal
  aktiv gesynct wird; ist der letzte erfolgreiche Sync älter als 24 Stunden, gilt das vorläufige
  Datum.
- Jeder Übergang ist ein bedingtes Update; ein zweiter Lauf über denselben Stand ändert nichts.

Entscheidung bei Fälligkeit (`WishExpiryPolicy.Decide`, erste zutreffende Zeile gewinnt):

| # | Bedingung | Ergebnis |
|---|---|---|
| 1 | Kanal im manuellen Modus | `presented` (`expired`) |
| 2 | Bot-Modus, `BotOwned` falsch | `presented` (`not_bot_owned`) |
| 3 | Bot ausgefallen (global oder `BotSuspendedReason`) | `presented` (`bot_unavailable`) |
| 4 | Schutz an und Zähllücke nach E5 | `presented` (`coverage_gap`) |
| 5 | Schutz an und Mittel je Tag (E4) ≥ Schwelle | `presented` (`protected`) |
| 6 | sonst | `RemovalQueuedAtUtc = jetzt`, Zustand bleibt `active` |

Bis K4 liefert die Bot-Verfügbarkeit eine Schnittstelle `IWishBotAvailability` mit einer
Standard-Implementierung „nie verfügbar“; Zeile 6 ist damit vor K4 nur im Unit-Test erreichbar.

Kurze Laufzeiten brauchen keine eigene Logik: Bei 1 bis 6 Tagen ist das Fenster die ganze Zeit im
Set (E4), bei 1 Tag genau 24 Stunden. Stellt ein Lauf die Fälligkeit bis zu 15 Minuten nach Ablauf
fest, ist das Fenster entsprechend länger, nie kürzer.

## Akzeptanzkriterien

22. **AK22** `pending` mit `CreatedAtUtc` vor 15 Tagen und `PendingExpiryDays = 14` → `expired`,
    `EndedAtUtc` gesetzt; mit `PendingExpiryDays = 1` und Alter 23 h bleibt er `pending`, mit 25 h
    verfällt er.
23. **AK23** `WishExpiryPolicy` liefert für jede der sechs Zeilen das genannte Ergebnis; die
    Reihenfolge ist getestet (ein Fall, der Zeile 2 und 5 erfüllt, ergibt `not_bot_owned`).
24. **AK24** Fenster E4: Laufzeit 30 Tage → Fenster genau 7 Tage; Laufzeit 2 Tage → Fenster 2 Tage,
    Mittel = Summe geteilt durch 2; Laufzeit 1 Tag → Fenster 24 h, gültig; ein Wunsch, der 23 h im
    Set ist (künstlich früher Ablauf), gilt als Lücke.
25. **AK25** Mittel je Tag summiert über alle Slots desselben Emotes im Kanal (R3-8): Emote unter
    zwei Aliasen mit 10 und 20 Nutzungen je Tag ergibt 30.
26. **AK26** Lücke E5: `TrackedSince` einen Tag nach Fensterbeginn → `coverage_gap`, auch wenn die
    Nutzung über der Schwelle liegt; `DeactivatedAtUtc` gesetzt → `coverage_gap`; Schutz aus und
    dieselbe Lage → Zeile 6.
27. **AK27** Ein Wunsch mit vorläufigem Start wird bei frischem Sync nicht fällig gestellt, bei
    einem letzten Sync vor 25 h schon.
28. **AK28** Zwei Durchläufe hintereinander über denselben Stand: der zweite ändert keine Zeile und
    veröffentlicht kein Event.
29. **AK29** `Wishes:Enabled = false`: genau eine Logzeile, kein Datenbankzugriff danach.
30. **AK30** Der Worker hat fünfzehn Hosted Services; `LogCaptureWriter` bleibt der erste.
31. **AK31** Live-Verifikation (Regel 16) lokal: per `adopt` einen Bestandseintrag mit
    `startsAtUtc` vor 8 Tagen und `durationDays = 7` übernehmen; nach höchstens einem Takt steht der
    Wunsch auf `presented` (`expired`) und `wish.changed` kommt auf dem Kanal-Stream an. Wiederholen
    mit Schutzschwelle 1 auf einem Emote mit echter Nutzung im Fenster: Ergebnis `presented`
    (`expired`) im manuellen Modus bleibt, die Detailantwort zeigt `perDayMean` > 0 und
    `coverageGap = false`.
32. **AK32** Nach der Aktivierung im Pilot (Beobachtung, kein Merge-Tor): Ein echter Wunsch mit
    7 Tagen Laufzeit zeigt ab Start „Probezeit“ (Vorlauf 7) und steht spätestens 15 Minuten nach
    Ablauf auf `presented`.

## Testplan

| Ebene | Projekt | Was | Anzahl |
|---|---|---|---|
| Unit | `Infrastructure.Tests/Unit` | `WishExpiryPolicy`, Fenster- und Lückenregel mit `FakeTimeProvider` | +18 |
| Integration | `Infrastructure.Tests/Integration` | `WishExpiryService` gegen Postgres (AK22, AK25 bis AK28) | +10 |
| Worker | `Worker.Tests` | nichts: Der Worker ist nur Takt; die Logik liegt in Infrastructure | 0 |
| Live | lokal, später Pilot | AK31, AK32 | keine |

## Files Reference

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Core/Services/WishExpiryPolicy.cs`, `IWishExpiryService.cs`, `IWishBotAvailability.cs` | neu |
| `src/EmotePurge.Infrastructure/Services/WishExpiryService.cs` | neu |
| `src/EmotePurge.Worker/WishExpiryWorker.cs` | neu |
| `src/EmotePurge.Worker/WorkerServiceRegistration.cs:56-70` | Registrierung |
| `src/EmotePurge.Core/Services/WishOptions.cs` | `ExpiryIntervalMinutes` |
| `docker-compose*.yml`, `.env.example` | `WISHES_EXPIRY_INTERVAL_MINUTES` |
| `CLAUDE.md` | Worker-Liste |

## Abhängigkeiten

K1.

---

# K3: Frontend manueller Modus

Labels: `feat`, `web`. Hängt ab von K1 (Vertrag) und K2 (nur für die Live-Probe).

## Ziel

Die drei Oberflächen aus Abschnitt 3 und der manuelle Weg: Handeintrag, Karte mit Nutzung,
Hinzufügen und Entfernen über die bestehende Browser-Engine mit dem Token des Verwalters.

## Umfang

Tab „Wünsche“ im Channel-Workspace, Abschnitt „Einstellungen“ in diesem Tab, Seite `my-wishes`,
Link mit Zähler in der Kopfzeile, i18n, Fehlercodes, Audit-Übersetzungen. Kein Bot-Zustand außer
einer neutralen Darstellung unbekannter Gründe (K5 ergänzt).

## Implementation Details

- **Routen** (`app.routes.ts`): Kindroute `wishes` unter `channels/:channelName` mit neuem
  `channelWishesGuard` (canManage und `wishesEnabled` aus `/permissions`); Route `my-wishes` mit
  `authGuard` nach Vorlage `features/my-votings/`.
- **Tab** (`channel-workspace-layout.ts:93-110`): `<app-tab-link link="wishes">` hinter
  `canManage() && wishesEnabled()`. Der Guard ist die Prüfung, die Bedingung nur Sichtbarkeit
  (UI-Designsprache §8.3). Der Settings-Tab bleibt unverändert.
- **Core** (`core/wishes/`): `wish.model.ts`, `wish.service.ts` (alle Routen aus K1),
  `wish-summary.service.ts` (Zähler als Signal, lädt beim Start der Shell und nach `wish.changed`,
  entprellt auf 5 s), Typ `wishChanged` in `core/live/live-event.model.ts:16-30`.
- **Features** (`features/wishes/`): `channel-wishes-page.ts` (drei Bereiche: Handlungsbedarf,
  laufend, beendet mit Pagination), `wish-card.ts`, `wish-entry-dialog.ts` (Handeintrag: 7TV-Link
  oder ID, Alias, Laufzeit vorbelegt mit dem Kanal-Default, optional Login des Wünschenden),
  `wish-adopt-dialog.ts`, `wish-settings-section.ts`, `my-wishes-page.ts`.
- **Handeintrag:** Der Client löst Emote-ID und Standardnamen aus dem Link über den öffentlichen
  7TV-Read auf, den der Browser schon nutzt, und schickt beides. Normalisierung wie der Server:
  Login getrimmt und kleingeschrieben vor der Prüfung (Regel 9 sinngemäß).
- **Karte:** Emote-Bild, Alias, Wünschender (falls vorhanden), Quelle, Zeit im Set, Restzeit bzw.
  „seit N Tagen abgelaufen“, Laufzeit, Nutzung im Fenster mit Sparkline (`usage-sparkline.ts`,
  `getDailySeries` je Slot), „Rang N von M“, Hinweis bei `coverageGap`. Aktionen je Zustand nach
  Abschnitt 5; bei `rejected` der Hinweis auf Twitchs Warteschlange.
- **Hinzufügen und Entfernen:** über `SevenTvRunArbiter` (ein Lauf zur Zeit, `startLocked` sperrt
  die Knöpfe mit Grund). Hinzufügen nutzt die AddEmote-Mutation; die drei Kopien
  (`seven-tv-restore.service.ts:54`, `seven-tv-undo.service.ts:75`, `seven-tv-import.service.ts:74`)
  werden dabei zu einer exportierten Konstante zusammengezogen, ohne Verhaltensänderung. Entfernen
  nutzt `REMOVE_SLOT_ENTRY_MUTATION` mit der Generationsbindung aus `seven-tv-entry-target.ts`
  (frischer Read, Alias und `addedAt` müssen passen). Nach Erfolg meldet der Client
  `complete-removal`; nach dem Hinzufügen meldet er nichts, die Verknüpfung macht der Abgleich.
  Lehnt 7TV das Hinzufügen ab, bleibt der Wunsch `approved` und die Karte zeigt den Grund.
- **Kopfzeile** (`app-shell.ts`): Link „Wünsche“ neben „Abstimmungen“, sichtbar nur, wenn
  `summary` erreichbar ist und der Schalter an ist; die Zahl steht am Link, nicht als zweites
  Status-Badge (Kommentar `:128-134`).
- **i18n:** Schlüsselbereiche `wishes.*` (Seiten, Karte, Dialoge, Zustände `wishes.status.*`,
  Gründe `wishes.reason.*`), `channelWorkspace.tabs.wishes`, `shell.wishes`, `errors.api.wish_*`,
  `audit.actions.wish.*`, in `de.json` und `en.json`. Keine „…“ an Knöpfen.
- **Design:** gegen `docs/UI-Designsprache.md` §2.1, §4.2 (Entfernen ist destruktiv, Stufe wie das
  Löschen eines Eintrags), §4.3, §6.2 (leerer Zustand je Bereich), §7, §8.1, §8.5, §11. Keine
  Layout-Sprünge zwischen den Tabs.

## Akzeptanzkriterien

33. **AK33** Tab und Route: Verwalter mit `wishesEnabled` sieht den Tab; ohne `canManage` oder ohne
    Schalter fehlt er und `/channels/x/wishes` leitet um wie ein abgelehnter Verwalter-Guard.
34. **AK34** Handeintrag: Der Dialog belegt die Laufzeit mit dem Kanal-Default vor; Werte außerhalb
    1 bis 365 sperren den Knopf mit sichtbarem Grund; ein Login mit Großbuchstaben wird normalisiert
    gesendet.
35. **AK35** 409 `wish_emote_already_in_set` im Handeintrag öffnet „Bestand übernehmen“ mit den
    gemeldeten Einträgen.
36. **AK36** Je Zustand zeigt die Karte genau die Aktionen aus Abschnitt 5 (Zustand → Aktionsliste
    als `computed()`, getestet als Tabelle); ein unbekannter `statusReason` rendert eine neutrale
    Zeile statt eines Fehlers.
37. **AK37** Probezeit ist aus `probationStartsAtUtc` abgeleitet: Ein Wunsch mit 2 Tagen Laufzeit
    und Vorlauf 7 zeigt ab Start „Probezeit“.
38. **AK38** Entfernen aus der Karte: Der Lauf bindet über Alias und `addedAt`; weicht der frische
    Read ab, wird nichts gesendet und die Karte meldet es. Nach Erfolg geht genau ein
    `complete-removal` an die Api.
39. **AK39** Während ein anderer 7TV-Lauf aktiv ist, sind Hinzufügen und Entfernen auf allen Karten
    gesperrt und nennen den Grund (`aria-disabled` plus zugänglicher Name).
40. **AK40** `wish.changed` auf dem Kanal-Stream lädt die Seite ohne Reload neu; fünf Ereignisse in
    2 Sekunden auf dem kanalübergreifenden Stream lösen genau eine `summary`-Anfrage aus.
41. **AK41** Zähler: zeigt `actionCount`, bei 0 keine Zahl; der Link fehlt bei 404 `wishes_disabled`.
42. **AK42** Einstellungen: im Bot-Modus für einen Twitch-Mod, der nicht aktiviert hat, schreibgeschützt
    mit Erklärung; Grenzen 1 bis 365, 1 bis 30, 1 bis 90 werden vor dem Senden geprüft.
43. **AK43** `my-wishes` listet Handlungsbedarf vor Probezeit, innerhalb nach Ablauf aufsteigend,
    und nennt je Zeile den Kanal.
44. **AK44** `api-error-locales.spec.ts` grün mit allen `wish_*`-Codes; `api-error.ts` enthält
    jeden Code aus `ApiErrorCodes.cs` (von Hand gegengeprüft, im PR benannt).
45. **AK45** Bei 360 px Breite hat die Kopfzeile mit Zähler und einem aktiven Status-Badge keinen
    horizontalen Überlauf und kein abgeschnittenes Element (Audit-Harness, `smallTargets`
    mitgeprüft).
46. **AK46** Live-Probe im Browser gegen den lokalen Stack mit dem Testkonto: Handeintrag,
    Hinzufügen aus der Karte mit eigenem Token, Karte wird nach dem Sync `aktiv`; übernommener,
    abgelaufener Eintrag erscheint in Kopfzeile und Übersicht; Entfernen aus der Karte endet in
    `entfernt`. Auf einem echten Handy über den `lan`-Weg einmal durchgeklickt.

## Testplan

| Ebene | Was | Anzahl |
|---|---|---|
| Vitest, `core/wishes` | `WishService` (`HttpTestingController`), `WishSummaryService` (Entprellen), Guard | +14 |
| Vitest, Komponenten | nur Entscheidungslogik: Aktionsliste je Zustand, Sperrgründe, Sortierung, Probezeit-Ableitung, Validierung (Regel 12: Verhalten, nicht Vorlage) | +16 |
| Playwright | `web/e2e/wishes.e2e.spec.ts` mit `e2e/support/mocks.ts`: Handeintrag bis Karte; fälliger Wunsch in Übersicht und Zähler; Entfernen mit gemocktem 7TV; Tab unsichtbar ohne Schalter | +5 |
| Live | AK46 | 1 Durchgang |

E2E nur ohne laufende Api auf `:5151`; Import aus `web/e2e/support/test.ts`.

## Files Reference

| Datei | Änderung |
|---|---|
| `web/src/app/app.routes.ts:52`, `:102-161` | zwei Routen |
| `web/src/app/features/channel-workspace/channel-workspace-layout.ts:93-110` | Tab |
| `web/src/app/features/shell/app-shell.ts` | Link mit Zähler |
| `web/src/app/core/wishes/*` | neu, mit Specs |
| `web/src/app/features/wishes/*` | neu |
| `web/src/app/core/guards/` | `channelWishesGuard` |
| `web/src/app/core/live/live-event.model.ts:16-30` | Typ |
| `web/src/app/core/seven-tv/seven-tv-restore.service.ts:54`, `seven-tv-undo.service.ts:75`, `seven-tv-import.service.ts:74` | eine gemeinsame AddEmote-Konstante |
| `web/src/app/core/i18n/api-error.ts` | Codes |
| `web/public/i18n/de.json`, `en.json` | Schlüsselbereiche |
| `web/e2e/wishes.e2e.spec.ts`, `web/e2e/support/mocks.ts` | neu, Mocks |
| `docs/UI-Designsprache.md`, `DESIGN.md` | nur falls ein neues Muster entsteht (Ist-Stand, englisch) |

## Abhängigkeiten

K1; K2 für AK46.

---

# K4: Bot-Backend

Labels: `feat`, `worker`, `api`, `infrastructure`, `migration`, `security`. Hängt ab von K1, K2.

## Ziel

Der Worker schreibt mit dem Token des Bot-Kontos nach 7TV: hinzufügen nach Freigabe, entfernen bei
Ablauf, beides nur innerhalb der Grenzen aus Abschnitt 7 und 8.

## Umfang

Migration `AddWishBot` (6.4), Versiegeln und Öffnen des Tokens, Admin-Endpunkte und Admin-Seite,
Schreib-Client, `WishBotWorker`, Editor-Prüfung, Eigentum, Reparatur, Rückfall, Notaus, Doku. Die
Admin-Seite ist bewusst hier und nicht in K5: Ohne sie lässt sich nichts live prüfen.

## Implementation Details

**Token:** `IWishBotTokenSealer` (nur in der Api registriert, braucht den öffentlichen Schlüssel)
und `IWishBotTokenOpener` (nur im Worker). Zwei getrennte Registrierungs-Einstiege nach dem Muster
`AddLogCapture`, nie aus `AddEmotePurgeInfrastructure`. Fehlt der jeweilige Schlüssel bei
`Wishes:BotMode:Enabled = true`, startet der Prozess nicht (Fail-Fast mit englischer Meldung).

**Admin-Api** (`/api/admin/wish-bot`, `GlobalAdminAuthorizationFilter`, kein Rate-Limit wie die
Gruppe):

| Route | Antwort |
|---|---|
| `GET /` | `{ tokenPresent, status, expiresAtUtc, expiresInDays, botSevenTvUserId, lastVerifiedAtUtc, lastErrorCode, killSwitch, pausedUntilUtc, queue: { adds, confirms, removals }, botChannels }` |
| `PUT /token` `{ "token": "…" }` | 204; 400 `wish_bot_token_malformed`, 400 `wish_bot_token_expired`, 404 `wish_bot_disabled` |
| `DELETE /token` | 204 |
| `PUT /kill-switch` `{ "enabled": true }` | 204 |

Keine Route gibt das Token oder das Siegel zurück. Audit: `wishBot.tokenSet`,
`wishBot.tokenDelete`, `wishBot.killSwitch` (Details nur `expiresAtUtc` bzw. `enabled`).

**Kanal-Api** (Gruppe aus K1): `GET /api/channels/{c}/wishes/bot` (`{ available, enabled,
suspendedReason, editorCheckedAtUtc, activatedByLogin, activatedAtUtc, botSevenTvUserId }`),
`POST …/wishes/bot/activate` (jeder Verwalter; setzt eine Prüfanforderung und stößt den Worker an;
aktiv wird der Modus erst, wenn der Worker den Editor-Status bestätigt hat; 409
`wish_bot_unavailable`, wenn Token oder Schalter fehlen), `DELETE …/wishes/bot` (Broadcaster,
Aktivierender, globaler Admin über `WishSettingsAuthorizationFilter`; beendet jedes Eigentum im
Kanal, laufende Wünsche werden Erinnerungsfälle). `/permissions` bekommt `wishBotModeAvailable`.
Audit: `wish.botActivate`, `wish.botDeactivate`.

**Schreib-Client:** `ISevenTvWriteClient` (Core) und Implementierung (Infrastructure), eigener
typisierter HttpClient gegen den v4-GraphQL-Endpunkt, 10 s Timeout, nur im Worker registriert.
Operationen: Set-Einträge lesen mit `alias`, `addedAt`, `addedById`, Emote-ID (alle Seiten, sonst
Fehler, fail-closed); `addEmote(id: { emoteId, alias })`; `removeEmote(id: { emoteId, alias })`
(Alias immer gesetzt); Token prüfen und eigene 7TV-ID lesen. Fehlerformen wie im Lesepfad:
verkleidetes 429 (HTTP 200, `extensions.status`), HTTP 429 mit `Retry-After`, 401/403, Timeout.
Telemetrie über den vorhandenen Provider-Handler mit eigener Aufrufquelle.

**`WishBotWorker`** (Hosted Service, konkrete Klasse): eine Schleife unter Postgres-Advisory-Lock
(eigener Schlüssel, Muster `ChatLogBackfillService.cs:58-76`), streng seriell über alle Kanäle,
`WriteDelaySeconds` Abstand. Die Warteschlange ist **abgeleitet**, keine eigene Tabelle: nächster
Wunsch mit fälliger Bot-Aktion (`approved` in einem Bot-Kanal, `unconfirmed` mit
`BotNextAttemptAtUtc` ≤ jetzt, `active` mit `RemovalQueuedAtUtc`), älteste zuerst (F7). Geweckt
durch `WISHBOT:` und einen 30-Sekunden-Takt.

Vor **jedem** Schreiben, in dieser Reihenfolge: Schalter, `KillSwitch`, `PausedUntilUtc`,
Token-Status, Editor-Prüfung des Kanals (frisch, ohne `ModRoleCache`; innerhalb einer Minute je
Kanal wiederverwendbar). Fällt eine Prüfung durch, schreibt der Worker nicht und wendet den Rückfall
aus Abschnitt 7 Punkt 8 an.

Abläufe:

- **Hinzufügen:** Vor-Read; Emote-ID schon im Set → `presented` (`already_in_set`), kein Schreiben.
  Sonst `addEmote`; Erfolg → `unconfirmed`; Ablehnung → `failed` mit Grund, kein Retry. 429 →
  `PausedUntilUtc`, der Wunsch bleibt `approved`.
- **Bestätigen:** nach E11. Genau ein Eintrag mit Emote-ID und Alias: `addedById` = Bot → `active`
  mit Eigentum und maßgeblichem Start; sonst `active` ohne Eigentum. Kein Eintrag nach 15 min →
  `presented` (`unconfirmed`).
- **Entfernen:** frischer Read; vier Merkmale exakt → `removeEmote`; Nach-Read; Wunsch → `removed`.
  Abweichung → `presented` (`ownership_lost`). Eintrag fehlt schon → `removed_externally`.
  Reparaturfall nach Abschnitt 8.
- **„Bot entfernen lassen“** aus `presented` (K1-Route `complete-removal` bleibt dem manuellen Weg
  vorbehalten): neue Route `POST …/{id}/bot-remove`, setzt `RemovalQueuedAtUtc`, nur bei bestehendem
  Eigentum, sonst 409 `wish_not_bot_owned`.

Audit unter `AuditActor.System`: `wish.botAdd`, `wish.botRemove`, `wish.botRepair`,
`wish.botSuspend` (Kanal verliert Editor-Rechte). Token-Zustände `invalid` und `expired` schreiben
je Wechsel einen Eintrag `wishBot.tokenState`.

**Token-Ablauf:** Der Worker setzt `expired`, sobald `exp` erreicht ist, und loggt ab 7 Tagen
vorher höchstens eine Warnung je UTC-Tag.

**Admin-Seite** (`web/src/app/features/admin/`, Route `admin/wish-bot`): Zustand, Ablauf mit
Hinweis ab 7 Tagen, Eingabefeld (Typ Passwort, wird nach dem Senden geleert, nie vorbelegt),
Löschen mit `typed-confirm-dialog`, Notaus-Schalter, Warteschlangentiefe, Zahl der Bot-Kanäle.

## Akzeptanzkriterien

47. **AK47** Migration `AddWishBot` additiv; genau eine Zeile möglich (zweites Insert scheitert am
    CHECK).
48. **AK48** Versiegeln und Öffnen: Rundlauf liefert das Original; ein verändertes Byte, ein
    falscher privater Schlüssel und ein unbekanntes Präfix liefern „kein Token“, keine Ausnahme mit
    Inhalt. Die Api-Assembly-Registrierung enthält keinen `IWishBotTokenOpener` (Test über den
    Service-Provider der `WebApplicationFactory`).
49. **AK49** `PUT /token`: kein JWT → 400 `wish_bot_token_malformed`; `exp` in der Vergangenheit →
    400 `wish_bot_token_expired`; gültig → 204, `Status = unverified`, ein `WISHBOT:`-Kommando,
    ein Audit-Eintrag ohne Token. Kein Admin → 403; Schalter aus → 404 `wish_bot_disabled` nach dem
    Filter.
50. **AK50** Keine Antwort irgendeiner Route enthält Token oder Siegel (Test durchsucht alle
    Antworten der Admin- und Kanal-Routen nach dem gesetzten Wert).
51. **AK51** Das Token erscheint in keinem Log: Test mit Log-Erfassung auf Trace-Stufe über Setzen,
    Prüfen, Hinzufügen, Entfernen und einen 401-Fehler; weder Token noch `Bearer` plus Wert in
    irgendeiner Zeile.
52. **AK52** Schreib-Client gegen einen lokalen Stub: verkleidetes 429 → Pause mit 60 s Default,
    `Retry-After` wird übernommen und bei 6 h gekappt; 401 → Token `invalid`, alle Bot-Kanäle im
    Rückfall; unvollständige Seitenfolge beim Lesen → Fehler, kein Schreiben.
53. **AK53** Eigentum (alle Fälle aus dem Entwurf, R3-5): Bestätigungs-Read mit `addedById` = Bot →
    Eigentum; fremd → `active` ohne Eigentum; fehlend → ohne Eigentum; kein Eintrag bis 15 min →
    `presented` (`unconfirmed`); Abweichung je eines der vier Merkmale im frischen Read → kein
    `removeEmote`; Umbenennen außerhalb → Eigentum endet; Entfernen und Hinzufügen außerhalb → kein
    Eigentum am Nachfolger; „wieder hinzufügen“ über den Bot → neue eigene Generation; zweiter Alias
    bleibt nach dem Entfernen im Set; Moduswechsel überträgt kein Eigentum; fehlender fremder
    Eintrag im Nach-Read → genau ein Wieder-Hinzufügen, `wish.botRepair`, `presented`
    (`repair_review`).
54. **AK54** F12: Ein im Bot-Kanal von einem Verwalter per Browser hinzugefügter Wunsch-Eintrag
    wird bei Ablauf `presented` (`not_bot_owned`), der Worker sendet kein `removeEmote`.
55. **AK55** Rückfall global (R3-10): Token `invalid` → `approved` bleibt und zählt in
    `summary`; `unconfirmed` → `presented` (`unconfirmed`); `RemovalQueuedAtUtc` gesetzt →
    `presented` (`bot_unavailable`). Nach neuem gültigem Token nimmt der Worker nur `approved` auf.
56. **AK56** Rückfall je Kanal (R3-11): Editor-Prüfung schlägt fehl oder 7TV antwortet auf ein
    Schreiben mit fehlender Berechtigung → `BotSuspendedReason = not_editor`, ein Audit-Eintrag,
    andere Kanäle laufen weiter.
57. **AK57** Notaus: `KillSwitch = true` während eines laufenden Durchgangs → spätestens nach dem
    laufenden Request kein weiterer; Zustand wie AK55. Zwei Worker-Instanzen: nur die mit dem
    Advisory-Lock schreibt.
58. **AK58** Live-Probe (Testkonto, Bot als Editor): nach `addEmote` durch den Bot zeigt der
    v4-Read `addedById` = 7TV-ID des Bot-Kontos. Ergebnis im PR und in DECISIONS festgehalten.
    **Fällt sie durch, entfällt das automatische Entfernen** (Bot fügt nur hinzu und erinnert), und
    der Betreiber entscheidet vor dem Merge.
59. **AK59** Live-Probe: Nach dem Umbenennen des Bot-Eintrags durch einen Menschen zeigt der Read
    welches `addedById` und welches `addedAt`; Ergebnis festgehalten. Erwartung der Spec: Eigentum
    endet in jedem Fall über den Alias.
60. **AK60** Live-Probe Sofort-Read: Zeit zwischen `addEmote`-Antwort und erstem v4-Read, der den
    Eintrag mit `addedAt` zeigt, an mindestens fünf Hinzufügungen gemessen. Über 5 Minuten in einem
    Fall → E11 neu festlegen, vor dem Merge.
61. **AK61** Live-Probe: Was `addEmote` zurückgibt, wenn die Abfrage
    `emotes { items { alias addedAt addedById } }` am zurückgegebenen Set mit angefordert wird. Trägt
    die Antwort den neuen Eintrag, darf sie den ersten Bestätigungs-Read ersetzen; sonst bleibt es
    beim Read.
62. **AK62** Live-Probe Notaus: „Sign Out Everywhere“ am Bot-Konto → der nächste Schreibversuch des
    Workers endet in `invalid` und im Rückfall. Zusätzlich einmal geprüft und dokumentiert, ob ein
    normaler Logout in einer anderen Sitzung dasselbe bewirkt.
63. **AK63** Live-Probe Verschränkung und Fehlerformen: Bot fügt hinzu, Mensch entfernt, Mensch fügt
    denselben Alias hinzu → frischer Read zeigt fremdes `addedById`, Bot entfernt nicht. Dazu je ein
    echtes Hinzufügen mit Alias-Konflikt und (falls herstellbar) vollem Set: die Antwortformen sind
    den Gründen `alias_conflict` und `set_full` zugeordnet; was nicht herstellbar war, läuft als
    `unknown` und steht als offen im PR.
64. **AK64** Live-Probe Editor-Lesen: Die Editor-Prüfung erkennt das Bot-Konto nach dem Eintragen
    und nach dem Austragen binnen einer Minute korrekt, und es ist dokumentiert, ob 7TV eine
    Einladung kennt, die das Bot-Konto erst annehmen muss, und ob die Prüfung die nötige
    Berechtigung (Emote-Sets verwalten) unterscheiden kann.
65. **AK65** Live-Verifikation Gesamtweg lokal (Regel 16) auf dem Testkonto: Handeintrag →
    Bot fügt hinzu → `active` mit Eigentum; per Datenbank-Update im Dev-Stack den Ablauf in die
    Vergangenheit legen → nächster Takt entfernt, Wunsch `removed`, zwei Audit-Einträge. Das Set
    danach wiederherstellen.
66. **AK66** Im Pilot (Beobachtung, kein Merge-Tor): ein Wunsch mit 7 Tagen Laufzeit wird vom Bot
    hinzugefügt und nach Ablauf entfernt oder vorgelegt, ohne Handgriff dazwischen.
67. **AK67** DECISIONS (englisch, „unofficial, not confirmed by 7TV“), Architectur.md, PRODUCT.md
    und das Operations-Runbook sind im selben Commit wie die Token-Ablage geändert (Regel 3).
68. **AK68** Der Worker hat sechzehn Hosted Services; bei `Wishes:BotMode:Enabled = false`
    beendet sich `WishBotWorker` nach einer Logzeile und öffnet nie das Token.

## Testplan

| Ebene | Projekt | Was | Anzahl |
|---|---|---|---|
| Unit | `Infrastructure.Tests/Unit` | Siegel (AK48), Eigentumsentscheidung als reine Funktion (AK53), Fehlerzuordnung, JWT-`exp`-Lesen | +30 |
| Unit | `Worker.Tests` | Reihenfolge der Vorprüfungen, Bestätigungsplan E11, Pausenregel (rein, ohne Container) | +12 |
| Integration | `Infrastructure.Tests/Integration` | Bot-Dienst gegen Postgres mit gestubbtem `ISevenTvWriteClient` (AK53 bis AK57), Schreib-Client gegen lokalen HTTP-Stub (AK52), Advisory-Lock | +25 |
| Api | `Api.Tests` | Admin-Routen, Aktivieren/Abschalten, AK49, AK50 | +10 |
| Vitest | Admin-Seite | nur Zustandslogik (Hinweis ab 7 Tagen, Feld wird geleert) | +4 |
| Live | lokal, Testkonto | AK58 bis AK65; der Transport selbst wird live statt gegen Fakes verifiziert (Regel 11, 16) | 1 Durchgang |

Die Live-Proben AK58 bis AK64 laufen **zuerst**, vor dem Ausbau der Warteschlange: Ihre Ergebnisse
entscheiden über Eigentum und Bestätigungsweg.

## Files Reference

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Core/Entities/WishBotCredential.cs` | neu |
| `src/EmotePurge.Core/Services/IWishBotTokenSealer.cs`, `IWishBotTokenOpener.cs`, `IWishBotService.cs`, `WishBotOwnership.cs` | neu |
| `src/EmotePurge.Core/SevenTv/ISevenTvWriteClient.cs` | neu |
| `src/EmotePurge.Core/Messaging/BotCommands.cs:22` | `WishBotPrefix = "WISHBOT:"` |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvWriteClient.cs` | neu |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:42-43` | unverändert; der Lesepfad des Vollsyncs bekommt `addedById` nicht |
| `src/EmotePurge.Infrastructure/Services/WishBotTokenSealer.cs`, `WishBotTokenOpener.cs`, `WishBotService.cs` | neu |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` | zwei getrennte Einstiege |
| `src/EmotePurge.Infrastructure/Migrations/*_AddWishBot.cs` | neu |
| `src/EmotePurge.Worker/WishBot/WishBotWorker.cs`, `WorkerServiceRegistration.cs` | neu, Registrierung |
| `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs:35`, `WishEndpoints.cs`, `Program.cs` | Routen, Registrierung |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | `wish_bot_disabled`, `wish_bot_token_malformed`, `wish_bot_token_expired`, `wish_bot_unavailable`, `wish_not_bot_owned` |
| `web/src/app/features/admin/wish-bot-page.ts`, `app.routes.ts:60-95`, `core/admin/` | Admin-Seite |
| `web/src/app/core/i18n/api-error.ts`, `web/public/i18n/*.json` | Codes, `admin.wishBot.*`, `audit.actions.wishBot.*` |
| `docker-compose*.yml`, `.env.example` | drei Variablen, Schlüssel je nur an einem Dienst |
| `docs/DECISIONS.md`, `docs/Architectur.md:21`, `PRODUCT.md:81`, `docs/Operations.md`, `CLAUDE.md` | nach Abschnitt 7 |

## Abhängigkeiten

K1, K2. Betreiber vorab: Schlüsselpaar erzeugen, Bot-Konto auf dem Testkonto als Editor eintragen.

---

# K5: Bot-Frontend

Labels: `feat`, `web`. Hängt ab von K3, K4 und der Probe AK69.

## Ziel

Ein Verwalter schaltet den Bot-Modus für einen Kanal ein, indem er das Bot-Konto mit seinem
**eigenen** 7TV-Token als Editor einträgt (D7), nach einem Dialog, der die Folgen nennt. Abschalten
und die Bot-Zustände auf den Karten gehören dazu.

## Umfang

Aktivierungsdialog, Editor-Mutation im Browser, Abschalten, Darstellung der Bot-Gründe und der
Aktion „Bot entfernen lassen“. Kein Backend außer dem, was K4 liefert.

## Implementation Details

- **Probe vor Baubeginn (AK69):** Die Editor-Hinzufügen-Mutation existiert im Repo nicht und ihr
  v4-Schema ist ungeprüft. Geklärt wird sie am Testkonto über die Netzwerkansicht der 7TV-Website
  beim Eintragen eines Editors (Mutation, Variablen, Berechtigungsfelder, Antwort) und, falls
  erlaubt, per Schema-Introspektion. Das Ergebnis steht als Sonden-Notiz im PR und als englischer
  DECISIONS-Eintrag.
- **Dialog** (`shared/ui/dialog-shell.ts`, UI-Designsprache §7): nennt vor dem Schreiben wörtlich
  drei Dinge: Der Bot entfernt Wunsch-Emotes bei Ablauf selbst; alle Twitch-Mods des Kanals dürfen
  Wünsche freigeben und ihre Laufzeit setzen; Abschalten geht hier oder durch Austragen des Bots in
  7TV. Bestätigung über eine Checkbox, dann der vorhandene Token-Dialog
  (`shared/seven-tv/seven-tv-token-prompt-dialog.ts`), falls kein Token in der Sitzung liegt.
- **Ablauf:** Editor-Mutation mit minimaler Berechtigung (nur Emote-Sets verwalten, sofern das
  Schema das trennt) über `SevenTvRunArbiter` → `POST …/wishes/bot/activate` → Warten auf die
  Bestätigung des Workers (Abfrage `GET …/wishes/bot` nach `wish.changed`, höchstens 60 s) →
  Erfolgs- oder Fehlzustand mit Grund. Das Token des Verwalters verlässt den Browser nicht.
- **Rückfallweg**, falls die Probe kein nutzbares Schema liefert oder 7TV eine Annahme durch das
  Bot-Konto verlangt: Der Dialog zeigt eine Anleitung zum Eintragen in 7TV und einen Knopf „Jetzt
  prüfen“, der nur `activate` aufruft.
- **Abschalten:** Knopf in den Einstellungen für Broadcaster, Aktivierenden und Admin, mit
  Bestätigung und dem Hinweis, dass laufende Wünsche zu Erinnerungen werden. Das Austragen in 7TV
  bleibt ein getrennter, optionaler Schritt mit eigenem Token.
- **Karten:** Übersetzungen und Aktionen für `unconfirmed`, `failed` samt Gründen, `presented` mit
  den Bot-Gründen, Kennzeichen „Bot entfernt bei Ablauf“ nur bei `botOwned`, Aktion „Bot entfernen
  lassen“ nach Abschnitt 5, Hinweis „manuell erledigen“ im Rückfall.
- **i18n:** `wishes.bot.*`, `wishes.reason.*` ergänzt, `errors.api.wish_bot_*`,
  `audit.actions.wish.bot*`.

## Akzeptanzkriterien

69. **AK69** Probe Editor-Mutation dokumentiert: Name, Variablen, Berechtigungsfelder, Antwortform,
    und ob das Bot-Konto eine Einladung annehmen muss. Ohne dieses Ergebnis beginnt K5 nicht.
70. **AK70** Der Bestätigen-Knopf des Dialogs ist gesperrt, bis die Checkbox gesetzt ist; der Dialog
    enthält die drei Aussagen (über ihre Übersetzungsschlüssel identifiziert, Rolle `dialog`,
    zugänglicher Name gesetzt).
71. **AK71** Reihenfolge: `activate` wird erst nach erfolgreicher Editor-Mutation gesendet; scheitert
    die Mutation, geht keine Anfrage an die Api und der Dialog nennt den Grund.
72. **AK72** Das 7TV-Token erscheint in keiner Anfrage an `/api/**` (E2E prüft alle aufgezeichneten
    Anfragen).
73. **AK73** Bestätigt der Worker binnen 60 s nicht, zeigt der Dialog „noch nicht bestätigt“ mit
    „Erneut prüfen“; der Modus gilt nicht als aktiv.
74. **AK74** Abschalten ist für einen Twitch-Mod, der nicht aktiviert hat, nicht angeboten und
    die Api antwortet ihm 403; nach dem Abschalten zeigt keine Karte mehr „Bot entfernt bei Ablauf“.
75. **AK75** `wishBotModeAvailable = false` (Schalter aus, kein gültiges Token, Notaus): Der
    Einstieg fehlt bzw. erklärt den Grund; bestehende Bot-Kanäle zeigen den Rückfallhinweis.
76. **AK76** Jeder `statusReason` und jeder `failed`-Grund aus Abschnitt 5 hat einen Schlüssel in
    beiden Sprachen (Test über die Liste der Konstanten).
77. **AK77** „Bot entfernen lassen“ erscheint nur bei `botOwned` und den drei Gründen aus Abschnitt
    5; 409 `wish_not_bot_owned` wird als Meldung gezeigt.
78. **AK78** Während des Aktivierungslaufs sind alle anderen 7TV-Startknöpfe gesperrt (Arbiter).
79. **AK79** Live-Probe im Browser auf dem Testkonto: Aktivieren über den Dialog, Wunsch freigeben,
    Bot fügt hinzu, Karte zeigt `aktiv` mit Bot-Kennzeichen; Bot in 7TV austragen → binnen eines
    Schreibversuchs zeigt der Kanal den Rückfall.
80. **AK80** Live-Probe zu R3-15: Dieselbe Editor-Mutation mit dem Token eines Kontos, das im
    Zielkanal weder Inhaber noch Editor mit Editor-Verwaltung ist, wird von 7TV abgelehnt. Ergebnis
    in DECISIONS festgehalten. **Wird sie nicht abgelehnt, trägt D7 nicht**, und der Betreiber
    entscheidet vor dem Merge neu.

## Testplan

| Ebene | Was | Anzahl |
|---|---|---|
| Vitest, `core/seven-tv` | neuer Editor-Dienst mit `HttpTestingController` (Erfolg, Ablehnung, verkleidetes 429) | +6 |
| Vitest, Komponenten | Dialog-Zustände, Sperrgründe, Aktionsliste mit Bot-Gründen | +10 |
| Playwright | `web/e2e/wishes-bot.e2e.spec.ts`: Aktivieren bis bestätigt (7TV und Api gemockt), Ablehnung durch 7TV, Rückfallanzeige, AK72 | +4 |
| Live | AK79, AK80 | 1 Durchgang |

## Files Reference

| Datei | Änderung |
|---|---|
| `web/src/app/core/seven-tv/seven-tv-editor.service.ts` (+ Spec) | neu, Mutation nach AK69 |
| `web/src/app/features/wishes/wish-bot-activate-dialog.ts`, `wish-settings-section.ts`, `wish-card.ts` | Dialog, Abschalten, Bot-Zustände |
| `web/src/app/core/wishes/wish.service.ts`, `wish.model.ts` | Bot-Routen |
| `web/public/i18n/de.json`, `en.json`, `web/src/app/core/i18n/api-error.ts` | Schlüssel |
| `web/e2e/wishes-bot.e2e.spec.ts`, `web/e2e/support/mocks.ts` | neu |
| `docs/DECISIONS.md` | englische Einträge zu AK69 und AK80 |
| `docs/Feature-Ideen-2026-08-01.md` | Statuszeile `A18` |
| `CLAUDE.md` | Umsetzungsstand |

## Abhängigkeiten

K3, K4, Probe AK69.

---

## Anhang: Zuordnung der Prüfpunkte

| Punkt | Aufgelöst in |
|---|---|
| R3-1, R3-2, R3-3 | im Entwurf behoben; hier Abschnitt 8 (Quelle), Abschnitt 5 und E10, AK9, AK10 |
| R3-4 | Abschnitt 5 (`unconfirmed` → `active`, drei Ausgänge der Vorlage), AK53 |
| R3-5 | AK53 nennt alle Fälle |
| R3-6 | Abschnitt 7 letzter Absatz, AK67 |
| R3-7 | E16 |
| R3-8 | E4, AK25 |
| R3-9 | Abschnitt 13 |
| R3-10 | Abschnitt 7 Punkt 8, AK55 |
| R3-11 | Vorprüfung vor jedem Schreiben in K4, AK56 |
| R3-12 | Abschnitt 5 (Ausgänge, „fällig“, kein Timeout, Retry ohne neue Freigabe); Rest als RR7 |
| R3-13 | entfällt: P3 per Betreiberentscheidung gestrichen; DECISIONS nennt den Betrieb „unofficial“ |
| R3-14 | E5, AK26; Worker-Ausfälle als RR1 |
| R3-15 | Probe AK80; bis dahin RR9 |
| F2 | K1 (409-Fälle), K4 (`failed` ohne Retry) |
| F3 | E12, E13 |
| F4 | 6.5, E14 |
| F5 | Abschnitt 7 Punkt 7; Automatik Out of Scope |
| F7 | Entscheidung 1; abgeleitete Warteschlange in K4 |
| F8 | E15 |
| F9 | E4, E5 |
| F10, F11 | Out of Scope (Abschnitt 15) |
| F12 | Abschnitt 8, AK54 |
