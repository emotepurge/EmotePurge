# Spec: Wunsch-Emotes mit Ablauf (Epic mit sechs Kindern)

Stand 2026-11-17. Sprache deutsch (Denkwerkzeug des Betreibers). Grundlage ist der freigegebene
Entwurf `docs/designs/Wunsch-Emotes-mit-Ablauf-2026-11-17.md` (Annahmen 1 bis 10, D4, D7, D8,
Fable-Eigentumsvertrag) plus die Betreiberentscheidungen aus der /spec-Befragung vom selben Tag.
Diese Spec rollt keine dieser Entscheidungen neu auf. Wo sie selbst etwas festlegt, steht es in
Abschnitt 4 („Festlegungen dieser Spec“) und der Betreiber kann widersprechen. Wo Entwurf, Brief und
Code einander widersprachen, hat der Betreiber entschieden; die Entscheide stehen in Abschnitt 17.

Labels (Epic): `feat`, `epic`. Kinder: K1 bis K6, gebaut in der Reihenfolge K1, K2, K3, K6, K4, K5.

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

Harte Anforderung (Annahme 1, in Runde 3 so gefasst): Kein Wunsch kommt ohne einen Menschen ins
Set. Freigabe ist entweder die Freigabe in Emote Purge oder das Hinzufügen des Emotes im Set durch
jemanden, der das in 7TV darf. Der Bot fügt nur nach ausdrücklicher Freigabe in Emote Purge hinzu.

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

Proben aus dem Entwurf (Scheibe 0): P1 bestanden, am 2026-11-17 abends auch für ein Konto ohne Mod- und VIP-Status (Befund in AK46, betrifft
K6). P2 bestanden: JWT lebt 30 Tage, Zweit-Login lässt das erste Token gültig, `addedById`
war in einem öffentlichen v4-Read 501 von 501 gefüllt. P3 entfällt per Betreiberentscheidung; der
Bot-Modus läuft inoffiziell.

## 3. Proposed Change: Überblick

Ein eigenes Wunsch-Modell je Kanal mit Lebenszyklus, zwei Betriebsarten je Kanal und drei
Oberflächen:

- **Manueller Modus (Hauptweg):** Der Mod arbeitet wie gewohnt in 7TV. Emote Purge erkennt die
  Einlösung im Chat, erkennt das hinzugefügte Emote im Set, ordnet beides einander zu, merkt sich
  die Frist und erinnert. Hinzufügen und Entfernen gehen auch aus der Karte heraus mit dem eigenen
  7TV-Token über die bestehende Browser-Engine. Das Backend schreibt nie nach 7TV.
- **Bot-Modus:** Das Bot-Konto ist 7TV-Editor des Kanals. Nach Freigabe fügt der Worker hinzu, bei
  Ablauf entfernt er, aber nur Einträge, die er nach bestem Wissen selbst hinzugefügt hat.
- **Oberflächen:** Tab „Wünsche“ je Kanal als reine Arbeitsliste (live per SSE über den
  kanaleigenen Stream), ein Abschnitt „Wünsche“ im Settings-Tab, eine kanalübergreifende Seite und
  ein Zähler in der Kopfzeile (lädt beim Seitenaufruf und alle 5 Minuten).

**Erfassung:** Der Hauptweg ist die Chat-Erfassung (K6): Der Worker liest Einlösungen über den
IRC-Tag `custom-reward-id`, der Text der Einlösung nennt das Emote per 7TV-Link oder Namen. Ohne
diese automatische Erkennung nutzt das Feature niemand, weil Mods nichts von Hand in Emote Purge
eintragen (Betreiber-Klarstellung). Der Handeintrag und die Übernahme eines Bestandseintrags
bleiben als Nebenwege für Korrektur und Nachtrag.

## 4. Festlegungen dieser Spec (Betreiber kann widersprechen)

| # | Festlegung | Grund |
|---|---|---|
| E1 | **Laufzeit je Wunsch in ganzen Tagen**, Spalte `DurationDays`, 1 bis 365. Vorbelegt mit dem Kanal-Default (`DefaultDurationDays`, Default 30). Beim Handeintrag und bei der Freigabe abweichend setzbar. | Betreiber-Ergänzung: Kanäle haben mehrere Belohnungen mit verschiedenen Laufzeiten. Obergrenze 365: Darüber ist ein Wunsch faktisch „behalten“, und kein Wunsch-Datensatz soll länger offen sein, als das Audit-Log (365 Tage) seine Freigabe belegt. |
| E2 | **Vorlauf der Probezeit** je Kanal, `ProbationLeadDays`, Default 7, 1 bis 30. Probezeit ist **kein gespeicherter Zustand**, sondern abgeleitet: Beginn = spätester von Start und (Ablauf minus Vorlauf). Ist die Laufzeit kürzer als der Vorlauf, ist der Wunsch ab Start in Probezeit. | Eine Regel für alle Laufzeiten, kein Sonderfall für kurze. |
| E3 | **Verfall offener Wünsche** je Kanal, `PendingExpiryDays`, Default 14, 1 bis 90. Die Verfallsfrist ist zugleich das Zuordnungsfenster eines offenen Wunsches (E20). Gebaut in K6. | Entwurf Scheibe 1; in Runde 2 mit der Chat-Erfassung nach K6 gelegt. |
| E4 | **Bewertungsfenster des Schutzes:** Fensterbeginn = spätester von Start des Wunsches und (Bewertungszeitpunkt minus 7 Tage). Bewertungszeitpunkt ist der Lauf, der die Fälligkeit feststellt. Mittel je Tag = Summe der Nutzung über alle Slots desselben Emotes im Kanal an den UTC-Tagen vom Tag des Fensterbeginns bis zum Bewertungstag, geteilt durch die Fensterlänge in Tagen (Bruchteile erlaubt). **Ist das Fenster kürzer als 24 Stunden, gilt das wie eine Zähllücke.** *Nachtrag 2026-11-21 (Betreiberentscheidung, #496 Punkt 5):* Unter 24 Stunden zeigt die Karte kein „Ø … pro Tag“: die Antwort trägt dann `perDayMeanWithheld: true` (additives Feld; `perDayMean` bleibt eine Zahl, damit ein vor dem Deploy geöffneter Tab nicht bricht); Nutzungszahl, Rang und Lückenhinweis bleiben. Ab genau 24 Stunden erscheint das Mittel wie bisher. | Betreiber-Korrektur: min(7 Tage, tatsächliche Zeit im Set), eine Regel ohne eigene Logik für kurze Laufzeiten. Bei 1 Tag Laufzeit ist das Fenster bei Fälligkeit genau 24 Stunden und damit gültig. |
| E5 | **Zähllücke** (Entscheidung 2): `TrackedSince(kanal)` liegt nach dem Fensterbeginn, **oder** `DeactivatedAtUtc` ist gesetzt, **oder** `IsBotActive` ist falsch, **oder** E4-Fenster unter 24 Stunden. Die Lückenregel greift nur bei eingeschaltetem Schutz; ohne Schutz spielt die Nutzung für das Entfernen keine Rolle. | Eine Deaktivierung mit Reaktivierung im Fenster setzt `TrackingResumedAt` und fällt damit unter die erste Bedingung (`Channel.cs:23`). |
| E6 | **Ablauf-Lauf alle 15 Minuten** (`Wishes:ExpiryIntervalMinutes`, 1 bis 1440), eigener Worker, erster Lauf 1 Minute nach Start, wartet nicht auf die Boot-Recovery. | Das Beobachtungsziel „binnen 24 h“ und Laufzeiten ab 1 Tag vertragen keinen Tagestakt. Der Lauf berührt keinen Kanal-Purge und braucht die Sperre der Retention nicht. |
| E7 | **Bot-Token asymmetrisch versiegelt:** Die Api hält nur einen öffentlichen Schlüssel und kann damit setzen, aber nicht lesen. Der private Schlüssel liegt nur am Worker. Details in Abschnitt 7. | Erfüllt „nie zurücklesen“ kryptografisch statt per Disziplin. Vom Betreiber angenommen (W2). Nachtrag 18.2: Die Twitch-Sitzung des Bot-Kontos liegt nie am Server, nur am Erneuerer-Host. |
| E8 | **Pilot-Liste nur für den Bot-Modus, fail-closed beim Aktivieren und im Betrieb:** `Wishes:PilotChannelIds` (Twitch-Kanal-IDs, kommagetrennt, oder `*` für alle). In einem gelisteten Kanal darf jeder nach E24 Berechtigte den Bot-Modus aktivieren, in jedem anderen nur ein globaler Admin. **Leer heißt: nirgends, außer durch globale Admins.** Der Bot schreibt nur in Kanälen, die gelistet sind oder deren Aktivierung von einem Konto stammt, das zum Zeitpunkt des Schreibens globaler Admin ist. Fällt ein Kanal aus der Liste, schreibt der Bot dort ab dem nächsten Lauf nicht mehr; der Kanal läuft im manuellen Ablauf wie beim Notaus, die Aktivierung bleibt gespeichert, und der Bot nimmt ohne neuen Editor-Eintrag wieder auf, sobald der Kanal wieder gelistet ist. Der manuelle Modus hängt allein an `Wishes:Enabled`. | Entscheide W7 und X2. Der Bot-Modus ist der Teil mit serverseitigem Schreibzugriff; eine vergessene oder leere Variable darf ihn weder öffnen noch offen halten. Globale Admins bleiben ausgenommen, damit sich der Betrieb ohne Stack-Update prüfen lässt; `*` ist die bewusste, ausgeschriebene Öffnung. |
| E9 | **Abgleich zustandsbasiert statt ereignisbasiert:** Nach jedem erfolgreichen Vollsync, Delta und manuellen Slot-Schreiben läuft `IWishReconcileService.ReconcileChannelAsync(channelId)`, der Wünsche gegen den aktuellen Slot-Bestand vergleicht. Idempotent; der Ablauf-Lauf ruft ihn als Sicherheitsnetz für jeden Kanal mit laufenden Wünschen. | Es gibt keinen Hook für „Slot angelegt/archiviert“ (Abschnitt 2). Ein Vergleich gegen den Ist-Stand heilt verpasste Ereignisse von selbst. |
| E10 | **Übernahmefenster 6 Stunden** nach `extern entfernt` (Entwurf: „Größenordnung Stunden“). | Deckt Entfernen und Wieder-Hinzufügen über mehrere Sync-Läufe (R3-3), ohne ein späteres, unabhängiges Hinzufügen zu übernehmen. |
| E11 | **Bestätigungsschwelle im Bot-Modus 15 Minuten:** Lesen direkt nach dem Schreiben, dann nach 30 s, 2 min, 5 min, 15 min. Danach `vorgelegt` mit Grund `unconfirmed`. **G0 (2026-11-24):** bleibt; AK88 maß 0 s, und die Rückgabe von `addEmote` ersetzt das Lesen direkt nach dem Schreiben (AK89). | P2-Schwelle des Entwurfs war 5 Minuten für die Machbarkeit; der Betrieb bekommt einen Versuch mehr, bevor ein Mensch gerufen wird. |
| E12 | **Verlängern ohne Obergrenze der Anzahl**, je Vorgang um den Kanal-Default, gerechnet ab dem bisherigen Ablauf, oder ab jetzt, wenn der Ablauf schon vorbei ist. Jede Verlängerung ist ein Audit-Eintrag. | Entscheidung 3 nennt keine Obergrenze; E1 begrenzt die einzelne Laufzeit, das Audit-Log zeigt Ketten. |
| E13 | **Höchstens ein laufender Wunsch je Kanal und 7TV-Emote-ID** (Zustände `approved`, `unconfirmed`, `active`, `presented`). Ein Handeintrag für ein Emote mit laufendem Wunsch wird abgelehnt (409), und die Oberfläche bietet „Verlängern“ an. Wird ein **offener** Wunsch für ein solches Emote freigegeben, wird er zur Verlängerung des laufenden und endet selbst als `merged` (K6). | Entscheidung 3. Ein zweiter Alias desselben Emotes als eigener Wunsch ist Out of Scope. |
| E14 | **Personendaten hängender Wünsche:** Einlöser-ID, Login und eingelöster Text werden auch ohne Endzustand geleert, und zwar (a) 30 Tage nach `ExpiresAtUtc`, wenn der Wunsch dann nicht `active` ist; (b) 30 Tage nach `StatusChangedAtUtc` für jeden Wunsch ohne `ExpiresAtUtc` in `approved`, `unconfirmed`, `failed` oder `presented`; (c) spätestens 120 Tage nach `CreatedAtUtc` für jeden Wunsch, der nie `active` war. | `vorgelegt` hat bewusst keinen Timeout (R3-12), und ein unbestätigter oder fehlgeschlagener Wunsch hat weder Start noch Ablauf (Sol-Befund 5). (c) fängt Schleifen aus „erneut versuchen“ ab; 120 = 90 Tage größte Verfallsfrist plus 30. Vom Betreiber angenommen (W3). |
| E15 | **Backlog (F8):** neue Idee `A18` „Wunsch-Emotes mit Ablauf“ in `docs/Feature-Ideen-2026-08-01.md`, mit Querverweis auf A15. A15 (Nachrücker-Liste) bleibt eigenständig; „einer rein, einer raus“ ist die spätere Brücke. Am Eintrag „würde ich nicht bauen #3“ steht ein Verweis auf die enge Ausnahme. Statuszeile pflegt K1 (begonnen) und K5 (umgesetzt). | CLAUDE.md verlangt die Statuszeile im selben Commit. |
| E16 | **Position im Set (R3-7):** Die Karte zeigt „Rang N von M“ der Nutzung unter allen aktiven Slots im selben Fenster wie E4. Nur in der Detailantwort, nicht in Listen. | Macht „wo es im Set steht“ aus dem Entwurf messbar, ohne die Liste zu verteuern. |
| E17 | **Die Laufzeit je Wunsch darf jeder Verwalter setzen; D7 gilt nur für die Kanal-Vorgaben** (Standardlaufzeit, Vorlauf, Schutzschwelle, Abschalten des Bot-Modus). | Entscheid W4. Der Bot entfernt nur eigene Einträge, und die sind von demselben Verwalter freigegeben, der ihre Laufzeit setzt. Eine kurze Laufzeit trifft also nie einen Eintrag, der außerhalb dieses Wegs ins Set kam. |
| E18 | **Zähler in der Kopfzeile per Poll:** lädt beim Seitenaufruf und alle 5 Minuten über einen eigenen leichten Zähl-Endpunkt. Kein `wish.changed` auf `/api/channels/live-events`. Live per SSE ist nur der Wünsche-Tab über den kanaleigenen Stream. | Entscheid W6. Der globale Stream zeigte den Kanalnamen jedem eingeloggten Nutzer; 12 Anfragen je Stunde und Sitzung liegen weit unter dem Budget von `InteractiveRead`. |
| E19 | **Einstellungen im Settings-Tab:** neuer Abschnitt „Wünsche“ unter dem Backfill-Abschnitt. Der Tab-Link und der Guard hängen an `canManage() && (chatLogBackfillEnabled() \|\| wishesEnabled())`; jeder Abschnitt blendet sich über seinen eigenen Schalter aus. Der Wünsche-Tab bleibt reine Arbeitsliste. | Entscheide W9 und X1. Sind beide Schalter aus, gibt es weiterhin keinen Tab; D17 und AK14 der Backfill-Spec bleiben gültig, ein leerer Zustand ist nicht nötig. |
| E20 | **Eine Zuordnungsregel für alle Wege** (`WishSlotMatcher`, 6.3): Ein Wunsch ohne Slot wird mit einem Slot verknüpft, der in seinem Fenster im Set auftaucht, egal ob das Emote direkt in 7TV, über Emote Purge oder vom Bot hinzugefügt wurde. Fenster je Zustand: `pending` bis zum Verfall (E3), `approved` unbegrenzt, `removed_externally` 6 Stunden (E10). „Bestand übernehmen“ ist dieselbe Verknüpfung von Hand, für Fälle, die die Regel nicht eindeutig entscheidet. | Runde 2, Punkt 2: vereinheitlichen statt doppeln. Das frühere „Übernahmefenster“ und die Verknüpfung nach dem Handeintrag sind dieselbe Regel mit anderem Fenster. |
| E21 | **Hinzufügen im Set ist Freigabe.** Taucht zu einem offenen Wunsch der passende Slot auf, wird der Wunsch ohne Klick `active`. Im manuellen Modus folgt daraus nur eine Erinnerung; der Bot entfernt weiterhin nur Einträge mit `addedById` = Bot. | Entscheid X4. Wer in 7TV hinzufügen kann, kann es ohnehin; der Wunsch verfolgt den Eintrag nur. |
| E22 | **Eigener Schalter für die Erfassung:** `Wishes:Capture:Enabled`, Default `false`. | Der IRC-Tag ist undokumentiert; fällt er weg oder stört die Erfassung, lässt sie sich abschalten, ohne den manuellen Modus mitzunehmen. |
| E24 | **Aktivieren des Bot-Modus prüft der Server am Aufrufer (D7), in zwei Stufen:** Die Route liegt wie jede Wunsch-Route hinter dem Verwalter-Filter (Broadcaster, Twitch-Mod, globaler Admin) und zusätzlich hinter dem Aktivierungs-Filter. Aktivieren darf damit: der Broadcaster, ein globaler Admin, oder ein Twitch-Mod, den `ISevenTvEditorService` als 7TV-Editor des Kanals bestätigt. Ein reiner Twitch-Mod darf es nicht, ein 7TV-Editor ohne Verwalter-Rolle auch nicht. Jede Unsicherheit lehnt ab. Ein aktiver Bot-Modus lässt sich nicht erneut aktivieren, der Aktivierende wird nie überschrieben. | Sol-Befund 1: Die Reihenfolge im Browser schützt den Endpunkt nicht. Der Verwalter-Filter bleibt, weil die ganze Oberfläche (Settings-Abschnitt, Arbeitsliste, Audit-Feed) nur Verwaltern offensteht; ein Editor ohne diese Rolle hätte einen Bot-Modus eingeschaltet, den er nicht sehen kann. Der globale Admin ist der Betreiber und Halter des Bot-Kontos. |
| E25 | **Jede Entscheidung verwirft eine eingereihte Bot-Entfernung.** `Wishes.DecisionVersion` zählt bei jeder Verwalter-Aktion und jeder Eigentumsänderung hoch; `RemovalQueuedAtUtc` und `RemovalQueuedVersion` werden dabei im selben Update geleert. Der Bot prüft unmittelbar vor `removeEmote` unter Zeilensperre erneut. | Sol-Befund 2: Ein verlängerter Wunsch durfte sonst trotzdem entfernt werden. |
| E26 | **Aufräumen ist vom Erfassungs-Schalter unabhängig.** Der Erfassungs-Worker hat einen Haushalts-Takt (jede Minute), der immer läuft: abgelaufene Anlern-Zeilen löschen, alle 15 Minuten offene Wünsche verfallen lassen. Ein abgelaufener Kandidat gilt beim Lesen und Bestätigen als nicht vorhanden. | Sol-Befund 6: Sonst blieben Text und Login eines Kandidaten nach dem Abschalten liegen, und 15 Minuten Takt hielten die 10-Minuten-Zusage nie. |

## 5. Lebenszyklus

Zustände (`Wish.Status`, als Zeichenkette gespeichert, die Werte sind Vertrag):

| Wert | Deutsch | Bedeutung | Endzustand |
|---|---|---|---|
| `pending` | offen | im Chat erfasst, wartet auf Zuordnung oder Freigabe (K6) | nein |
| `approved` | freigegeben | freigegeben, Eintrag noch nicht im Set | nein |
| `unconfirmed` | unbestätigt | Bot hat geschrieben, maßgebliches `addedAt` fehlt noch | nein |
| `failed` | fehlgeschlagen | Bot-Hinzufügen von 7TV abgelehnt | nein |
| `active` | aktiv | Eintrag im Set, Frist läuft | nein |
| `presented` | vorgelegt | braucht eine Entscheidung eines Menschen, Grund in `StatusReason` | nein |
| `rejected` | abgelehnt | Freigabe verweigert (K6) | ja |
| `expired` | verfallen | offen geblieben bis `PendingExpiryDays` (K6) | ja |
| `removed` | entfernt | über Emote Purge entfernt (Mensch oder Bot) | ja |
| `removed_externally` | extern entfernt | außerhalb von Emote Purge entfernt | ja, mit Rückweg (E10) |
| `kept` | behalten | Eintrag bleibt dauerhaft, Wunsch geschlossen | ja |
| `closed` | geschlossen | ohne Aktion in 7TV beendet | ja |
| `merged` | zusammengeführt | ging als Verlängerung in einen laufenden Wunsch auf (K6), oder unterlag bei einer Kanal-Zusammenführung einem anderen laufenden Wunsch (Regel siehe 6.5) | ja |

K1 legt alle Werte an. Die Übergänge aus `pending` und das Aufgehen in `merged` baut K6; in K1 bis
K5 gibt es dafür keinen Endpunkt, keinen Worker-Zweig und keine Oberfläche.

Begriffe für die Oberfläche: **„Probezeit“** = `active` und jetzt ≥ Probezeitbeginn (E2).
**„fällig“** = `presented` (jeder Grund) sowie `active` mit überschrittenem Ablauf, solange der Lauf
oder der Bot noch nicht reagiert hat. **„Handlungsbedarf“** (Zähler) = `pending`, `failed`,
`presented`, dazu `approved`, wenn der Kanal im manuellen Modus ist oder der Bot ausgefallen ist.

Gründe in `StatusReason` bei `presented`: `expired` (abgelaufen, manuell zu erledigen),
`protected` (Schutzschwelle erreicht), `coverage_gap` (Zähllücke), `not_bot_owned` (Bot-Modus, aber
kein Eigentum, schließt F12 ein), `ownership_lost` (frischer Read wich ab), `unconfirmed` (E11),
`bot_unavailable` (Ausfall bei fälliger Entfernung), `unconfirmed_start` (Bot-Modus, Ablauf erreicht, aber
der Start ist nur vorläufig; K2), `already_in_set`, `repair_review` (nach Reparatur). Bei `failed`: `alias_conflict`, `set_full`, `emote_gone`, `not_editor`, `unknown`.
Bei `pending` nur als Hinweis für die Arbeitsliste, kein eigener Zustand: `ambiguous_match`,
`already_in_set`, `already_running`, `unparsed`.

Übergänge (jede Zeile ist ein bedingtes Update auf den erwarteten Ausgangszustand; trifft es keine
Zeile, antwortet die Api 409 `wish_state_conflict`):

| Von | Nach | Auslöser | Bedingung und Wirkung |
|---|---|---|---|
| (neu) | `approved` | Handeintrag | gilt als Freigabe (Annahme 1) |
| (neu) | `active` | Bestand übernehmen | Slot schon im Set; nie Bot-Eigentum |
| `pending`, `approved`, `failed`, `presented` (`unconfirmed`, `already_in_set`) | `active` | Bestand übernehmen mit `wishId` | Verwalter wählt den Slot; verknüpft den vorhandenen Wunsch statt einen neuen anzulegen; `LinkOrigin = adopted`, nie Bot-Eigentum; bei `Permanent`: `kept` |
| (neu) | `pending` | Chat-Erfassung (K6) | Einlösung einer angelernten Belohnung |
| `pending` | `active` | Abgleich (E20, E21) | passender Slot taucht im Fenster auf; Start = `AddedAt` des Slots; kein Bot-Eigentum |
| `pending` | `approved` | Freigeben (K6) | Emote, Alias und Laufzeit dabei setzbar; im Bot-Modus fügt danach der Bot hinzu. Läuft schon ein Wunsch desselben Emotes: stattdessen `merged`, der laufende wird verlängert (E13) |
| `pending` | `rejected` | Ablehnen (K6) | Oberfläche zeigt den Hinweis auf Twitchs Warteschlange für die Rückerstattung |
| `pending` | `expired` | Erfassungs-Worker (K6) | älter als `PendingExpiryDays` |
| `approved` | `active` | Abgleich, manueller Weg | genau ein passender neuer Slot (Abschnitt 6.3); Start = `AddedAt` des Slots |
| `approved` | `unconfirmed` | Bot hat `addEmote` geschrieben | nur Bot-Modus |
| `approved` | `failed` | Bot | 7TV lehnt ab; kein stiller Retry (F2) |
| `approved` | `presented` (`already_in_set`) | Bot | Vor-Read zeigt Emote-ID schon im Set |
| `approved` | `closed` | Schließen | Verwalter verwirft den freigegebenen Wunsch |
| `unconfirmed` | `active` | Bestätigungs-Read | genau ein Eintrag mit Emote-ID und Alias: mit `addedById` = Bot entsteht Eigentum, sonst reiner Erinnerungsfall (R3-4) |
| `unconfirmed` | `presented` (`unconfirmed`) | E11 abgelaufen, oder Bot-Ausfall | braucht eine Entscheidung; die drei Ausgänge stehen in den nächsten Zeilen |
| `presented` (`unconfirmed`) | `active` | „als Erinnerungsfall verknüpfen“ = Bestand übernehmen mit `wishId` | ohne Eigentum |
| `presented` (`unconfirmed`) | `unconfirmed` | „erneut prüfen“ (`recheck`) | Versuche und `BotNextAttemptAtUtc` zurückgesetzt; nur bei verfügbarem Bot, sonst 409 `wish_bot_unavailable` |
| `presented` (`unconfirmed`) | `closed` | „schließen“ (`close`) | kein Slot verknüpft |
| `failed` | `approved` | Erneut versuchen | braucht keine neue Freigabe: Der Klick eines Verwalters ist die Freigabe (R3-12) |
| `failed` | `closed` | Schließen | |
| `active` | `active` | Verlängern | neuer Ablauf nach E12; eine eingereihte Bot-Entfernung ist damit verworfen (E25). Verlängern gibt es nur aus `active` und `presented`; ein Wunsch ohne Ablauf (`approved`, `unconfirmed`, `failed`) ändert seine Laufzeit über `PATCH` |
| `active` (Quelle `irc`, `LinkOrigin = auto`) | `pending` | Zuordnung lösen (`unlink`, K6) | Felder nach K6 zurückgesetzt, Slot gesperrt für diesen Wunsch |
| `active` | `kept` | Behalten | Eintrag wird ein normaler; Bot-Eigentum endet |
| `active` | `removed` | Entfernen über Emote Purge | manuell: Browser-Engine, danach Meldung an die Api; Bot: nach bestandener Prüfung (Abschnitt 8) |
| `active` | `presented` | Ablauf-Lauf bei Fälligkeit | Grund nach der Entscheidungstabelle in K2 |
| `active`, `presented` | `removed_externally` | Abgleich | verknüpfter Slot ist archiviert und Emote Purge hat das Entfernen nicht selbst gemeldet |
| `presented` | `active` | Verlängern | wie oben |
| `presented` | `kept` | Behalten | |
| `presented` | `removed` | Entfernen | manuell über die Browser-Engine; bei `protected`, `coverage_gap` und `bot_unavailable` zusätzlich „Bot entfernen lassen“, wenn das Eigentum noch besteht: Der Wunsch bleibt `presented` mit gesetztem `RemovalQueuedAtUtc`, bis der Bot entfernt hat (K4) |
| `presented` | `closed` | Schließen | nur wenn kein Slot verknüpft ist |
| `removed_externally` | `active` oder `presented` | Abgleich binnen 6 h | genau ein neuer, unverknüpfter Slot derselben Emote-ID; Frist bleibt, kein Eigentum |
| `removed_externally` | `removed` | Meldung der Browser-Engine binnen 6 h | löst den Wettlauf zwischen Sync und Erfolgsmeldung auf; `EndedAtUtc` bleibt dabei erhalten (Stand K3, Fußnote F7) |
| `removed`, `removed_externally` | `approved` | Wieder hinzufügen | mit neuer Laufzeit, oder `Permanent = true`: dann führt der Abgleich nach `kept` statt nach `active` |

`presented` hat **keinen Timeout** und keinen automatischen Ausgang (R3-12): Im manuellen Modus
bleibt ein abgelaufener, unbehandelter Wunsch `presented` mit Grund `expired`, zählt im Zähler und
steht oben in der Übersicht, bis jemand entscheidet.

Änderungen außerhalb von Emote Purge (R3-2, R3-3): **Umbenennen** behält Slot, Wunsch und Frist;
der Alias am Wunsch wird nachgezogen und ein Bot-Eigentum endet. **Entfernen** führt nach
`removed_externally`. **Erneutes Hinzufügen** folgt E10; mehrere Kandidaten oder ein Slot außerhalb
des Fensters werden nicht übernommen.

## 6. Datenmodell

Eine Migration `AddWishes` (K1) für die Tabellen 6.1 und 6.2, eine zweite `AddBotAccount` (K4) für
6.4, eine dritte `AddWishCapture` (K6) für 6.6. Alle additiv oder lockernd (Spalten werden nullbar), nie einengend. Konfiguration inline in `AppDbContext.OnModelCreating` wie der Bestand.

### 6.1 `Wishes` (Entität `Wish` in `EmotePurge.Core.Entities`)

| Spalte | Typ | Bemerkung |
|---|---|---|
| `Id` | bigint, Identity | |
| `ChannelId` | text, FK `Channels`, ON DELETE CASCADE | Purge nimmt Wünsche mit |
| `SevenTvEmoteId` | text, max. 32 | 7TV-ID, nicht `Emote.Id` (Regel 8) |
| `EmoteName` | text, max. 100 | Standardname bei 7TV zum Zeitpunkt des Eintrags, nur Anzeige |
| `Alias` | text, max. 100 | gewünschter bzw. aktueller Name im Set |
| `Source` | text | `irc` (K6), `manual`, `adopted` |
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
| `RemovalQueuedVersion` | int, null | `DecisionVersion` zum Zeitpunkt des Einreihens (E25) |
| `DecisionVersion` | int | zählt bei jeder Verwalter-Aktion und jeder Eigentumsänderung hoch (E25) |

Die Zeile speichert **keine Identität eines Verwalters**; wer was getan hat, steht nur im Audit-Log.

Indizes und Constraints:

- `IX_Wishes_ChannelId_Status`.
- `IX_Wishes_Status_ExpiresAtUtc` (Ablauf-Lauf).
- Eindeutig, partiell: `(ChannelId, SevenTvEmoteId)` WHERE `Status` IN (`approved`, `unconfirmed`,
  `active`, `presented`) (E13).
- Eindeutig, partiell: `(SlotId)` WHERE `SlotId` IS NOT NULL AND `Status` IN (`active`, `presented`,
  `kept`). Ein `kept`-Wunsch hält seinen Eintrag, solange er im Set ist (Betreiberentscheidungen vom
  18.11.). Verlässt der Slot das Set, leert der Abgleich die `SlotId` des `kept`-Wunsches (6.3), weil der
  Index den Zustand des Slots nicht sehen kann.
- `IX_Wishes_EndedAtUtc` partiell WHERE `RequesterLogin` IS NOT NULL OR `RequesterTwitchUserId`
  IS NOT NULL (Retention).
- CHECK `DurationDays BETWEEN 1 AND 365`.

### 6.2 `ChannelWishSettings` (1:1 zu `Channel`, PK = `ChannelId`, ON DELETE CASCADE)

| Spalte | Typ | Default | Grenzen |
|---|---|---|---|
| `DefaultDurationDays` | int | 30 | 1 bis 365 |
| `ProbationLeadDays` | int | 7 | 1 bis 30 |
| `ProtectionThresholdPerDay` | numeric(10,2), null | null = Schutz aus | > 0 |
| `BotModeEnabled` | bool | false | |
| `BotModeActivatedByTwitchUserId`, `BotModeActivatedByLogin` | text, null | | bei Kontolöschung pseudonymisiert |
| `BotModeActivatedAtUtc` | timestamptz, null | | |
| `BotEditorCheckedAtUtc` | timestamptz, null | | letzte bestandene Editor-Prüfung |
| `BotSuspendedReason` | text, null | | `not_editor`, `not_listed`, seit G0 auch `invitation_pending` (aktivierter Kanal mit offener Einladung, die der Bot noch nicht annehmen konnte, K4 „Einladungen“); gesetzt heißt: Kanal läuft im manuellen Ablauf |
| `BotActivationRequestedAtUtc`, `BotActivationRequestedByTwitchUserId`, `BotActivationRequestedByLogin` | null | | laufende Aktivierung; bei Bestätigung in `BotModeActivated*` übernommen und geleert, bei `not_editor` oder `invitation_not_accepted` geleert |
| `BotActivationError` | text, null | | `not_editor` nach gescheiterter Prüfung, seit G0 auch `invitation_not_accepted` (Annahme der Einladung abgelehnt); geleert beim nächsten `activate` |

Die Zeile entsteht beim ersten Schreiben; fehlt sie, gelten die Defaults. Eine Änderung der
Kanal-Defaults wirkt nur auf künftige Wünsche und Verlängerungen, nie rückwirkend auf laufende
Fristen.

### 6.3 Verknüpfung von Wunsch und Slot (Abgleich E9, eine Regel E20)

`WishSlotMatcher` (Core, rein) ist die einzige Stelle, die einen Wunsch ohne Slot einem Slot
zuordnet. Der Abgleich ruft ihn für jeden Wunsch in `pending`, `approved` und `removed_externally`
(dort nur innerhalb von 6 Stunden nach `EndedAtUtc`).

- **Kandidaten:** nicht archivierte Slots des Kanals, die mit keinem laufenden Wunsch verknüpft
  sind und die kein `kept`-Wunsch hält (Betreiberentscheidung vom 18.11.: `kept` hält seinen Slot,
  solange der Eintrag im Set ist; ist der Slot archiviert oder trägt er ein `AddedAt` nach dem
  `SlotConfirmedAtUtc` des `kept`-Wunsches und, sofern bekannt, nach seiner Generation
  `SlotAddedAtUtc`, gibt der Abgleich ihn vor der Zuordnung frei), die für
  diesen Wunsch nicht gesperrt sind (`WishSlotExclusions`, K6) und deren `AddedAt` nicht
  vor dem Fensterbeginn liegt. Fensterbeginn ist `CreatedAtUtc` (`pending`), `ApprovedAtUtc`
  (`approved`) bzw. `EndedAtUtc` (`removed_externally`), **ohne Toleranz**: Ein Slot mit `AddedAt`
  vor dem Fensterbeginn ist nie Kandidat, auch nicht um Sekunden; dafür gibt es „Bestand
  übernehmen“. Ein vorläufiges `AddedAt` ist der Zeitpunkt der Beobachtung und liegt nie vor dem
  echten, schließt also keinen berechtigten Kandidaten aus.
- **Treffer über die Emote-ID** (Wunsch kennt die 7TV-Emote-ID, aus Link oder Handeintrag): Gibt
  es genau einen Kandidaten mit dieser ID, wird er verknüpft, und der Alias am Wunsch folgt dem
  Slot. Gibt es mehrere, wird der eine verknüpft, dessen Alias dem Alias des Wunsches ordinal
  gleicht (Aliase sind in einem Set eindeutig, es kann höchstens einer sein). Gibt es mehrere und
  keiner trägt den Alias des Wunsches, oder hat der Wunsch keinen Alias, verknüpft die Regel
  nichts.
- **Treffer über den Namen** (nur K6, Wunsch kennt nur einen Namen): genau ein Kandidat, dessen
  Alias dem Namen ordinal gleicht, wird verknüpft; gibt es keinen, zählt genau einer mit gleichem
  Namen ohne Rücksicht auf Groß- und Kleinschreibung. Jede Mehrdeutigkeit verknüpft nichts und
  setzt den Hinweis `ambiguous_match`. Die 7TV-Emote-ID am Wunsch kommt dann aus dem Slot.
- **Von Hand:** „Bestand übernehmen“ verknüpft einen bestimmten Slot, auch einen älteren als das
  Fenster, entweder mit einem vorhandenen Wunsch (`wishId`) oder als neuer Wunsch. Das ist der
  Ausweg für jeden Fall, den die Regel offen lässt.
- **Wirkung:** `pending` und `approved` werden `active` (bei `Permanent`: `kept`),
  `removed_externally` kehrt nach `active` zurück und behält seine Frist. Die Herkunft der
  Verknüpfung steht in `LinkOrigin` (`auto`, `adopted`, `bot`). Bot-Eigentum entsteht hier nie; es
  entsteht nur im Bestätigungsweg des Bots (Abschnitt 8).
- Start ist `AddedAt` des Slots. Ist es noch vorläufig, gilt `StartsAtAuthoritative = false`; wird
  es maßgeblich, verschiebt der Abgleich `StartsAtUtc` und `ExpiresAtUtc` um dieselbe Differenz.
- Umbenennen und Archivieren nach Abschnitt 5.
- **Bot-Wünsche:** Ein Wunsch mit gesetztem `BotWriteAtUtc` in `approved` oder `unconfirmed` ist
  von dieser Regel ausgenommen (K4, Koordination mit dem Abgleich). Ein `active` ohne `SlotId`
  bekommt seinen Slot nachträglich, ohne dass sich Zustand, Start oder Eigentum ändern.
- Kanal-Zusammenführung (`ChannelIdentityService`): Der Verlierer darf heute keine Emotes und
  keine Tags haben (`:902-904`), kann aber Wünsche ohne Slot tragen und wird am Ende gelöscht
  (`:1009`). In derselben Transaktion wandern deshalb Wünsche und Einstellungen zum überlebenden
  Kanal (dessen Einstellungen gewinnen); kollidiert der partielle Eindeutigkeitsindex, gewinnt
  der Wunsch mit Spur in 7TV (verknüpfter Slot in `unconfirmed`, `active` oder `presented`, ab K4
  auch ein Bot-Schreibvorgang), der andere endet als `merged`; stehen beide gleich da, endet der
  jüngere (Betreiberentscheidung 18.11.). K6 erweitert das um Belohnungen, Anlernzustand und Lücken (K6).

K1 baut die Regel mit den Treffern über die Emote-ID und der Spalte `LinkOrigin`; K6 ergänzt den
Zustand `pending` und den Treffer über den Namen.

### 6.4 `BotAccountCredentials` (K4, genau eine Zeile, CHECK `Id = 1`)

Kontoweit, nicht an Wünsche gebunden (Name nach Plan-487, Revision 5a; vorher `WishBotCredentials`).

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

Nachtrag 2026-11-20: weitere Spalten für Twitch-Sitzung und Erneuerung in 18.2.

### 6.5 Retention und Löschpfade

| Regel | Konstante in `RetentionPolicy` | Wirkung |
|---|---|---|
| Einlöser-Daten | `WishRequester` = 30 Tage | `RequesterTwitchUserId`, `RequesterLogin` und ab K6 `RequestText` auf null, 30 Tage nach `EndedAtUtc`; für Wünsche ohne Endzustand nach E14 (a) bis (c) |
| Erfassungslücken (K6) | `WishCaptureGap` = 30 Tage | Zeile löschen |
| Beendete Wünsche | `EndedWish` = 365 Tage | Zeile löschen, 365 Tage nach `EndedAtUtc` |
| Audit | unverändert `AuditLogEntry` = 365 Tage | |

Beides läuft im bestehenden Tagesdurchlauf (`DataRetentionService.RunAsync`), also nur bei
`Retention:Enforce = true`; der Trockenlauf meldet Zählwerte in der Summenzeile. Die Kontolöschung
(`AccountDeletionService`) leert die Einlöser-Felder aller Wünsche mit der Twitch-ID des Kontos
sofort und pseudonymisiert `BotModeActivatedBy*`. Kanal-Purge löscht per Kaskade.

Datenschutz: Twitch-ID und Login des Einlösers sind neue Personendaten. Der Betreiber ergänzt sein
eigenes Markdown der Datenschutzerklärung (nicht im Repo) um Zweck, Datenarten und die 30-Tage-Frist,
nur Pflichtinhalt nach Art. 13. Das ist Voraussetzung für das Einschalten auf Prod, nicht für den
Merge. Mit K6 fallen Twitch-ID, Login und der eingelöste Text an; der Text der Datenschutzerklärung muss
deshalb vor der ersten Freigabe stehen.

### 6.6 Erfassung (K6, Migration `AddWishCapture`)

`ChannelWishRewards` (angelernte Belohnungen, höchstens 10 je Kanal):

| Spalte | Typ | Bemerkung |
|---|---|---|
| `Id` | bigint, Identity | |
| `ChannelId` | text, FK `Channels`, ON DELETE CASCADE | |
| `RewardId` | text, max. 64 | Wert des Tags `custom-reward-id`; eindeutig je `(ChannelId, RewardId)` |
| `Label` | text, max. 50 | vom Verwalter vergeben; der Chat liefert keinen Titel |
| `DefaultDurationDays` | int | 1 bis 365; belegt `DurationDays` der Wünsche dieser Belohnung vor |
| `IsActive` | bool | pausieren, ohne die Belohnung zu vergessen |
| `CreatedAtUtc`, `LastRedemptionSeenAtUtc` | timestamptz | Letzteres für die Anzeige „zuletzt erkannt“ |

`WishRewardLearnings` (höchstens eine Zeile je Kanal, gültig höchstens 10 Minuten): `ChannelId` (PK,
Kaskade), `StartedAtUtc`, `ExpiresAtUtc`, `CandidateRewardId`, `CandidateText` (max. 500),
`CandidateLogin`, `CandidateSeenAtUtc`. Die Zeile wird beim Bestätigen und Abbrechen sofort
gelöscht. Nach `ExpiresAtUtc` gilt sie für jede Route als nicht vorhanden und wird vom
Haushalts-Takt binnen einer Minute gelöscht, auch bei ausgeschalteter Erfassung (E26). Stand der
Worker still, löscht sein erster Takt nach dem Start. Der Kandidat ist die einzige Stelle, an der
Text einer nicht angelernten Belohnung überhaupt gespeichert wird.

`WishCaptureGaps`: `Id`, `ChannelId` (Kaskade), `FromUtc`, `ToUtc`, `Reason` (`disconnect`,
`worker_restart`, `not_joined`). `WishCaptureState` (eine Zeile): `LastAliveAtUtc`.

Ergänzungen an `Wishes`: `RewardId` (bigint, null, FK `ChannelWishRewards`, SET NULL),
`IrcMessageId` (text, null; eindeutig, partiell je `(ChannelId, IrcMessageId)`), `RequestText`
(text, null, max. 500), `RequestedName` (text, null, max. 100), `MatchKind` (`link`, `name`,
`none`). `SevenTvEmoteId`, `EmoteName` und `Alias` werden nullbar, mit CHECK: nur in `pending`,
`rejected` und `expired` dürfen sie fehlen. `ChannelWishSettings` bekommt `PendingExpiryDays`
(int, Default 14, 1 bis 90).

`WishSlotExclusions` (gelöste Zuordnungen): `WishId` (FK `Wishes`, Kaskade), `SlotId` (text),
`CreatedAtUtc`; Primärschlüssel `(WishId, SlotId)`. Kein Fremdschlüssel auf den Slot: Die Sperre
soll einen später archivierten Slot überleben.

## 7. Vertrauensgrenze und Bot-Token (D8, Entscheidung 1)

Geänderte Vertrauensgrenze, ausdrücklich benannt: Auf dem Server liegt genau ein 7TV-Token, das des
eigenen Bot-Kontos, nie das eines Nutzers; die Twitch-Sitzung des Bot-Kontos liegt seit dem Nachtrag
nicht am Server, sondern am Erneuerer-Host (18.3). Ein gestohlenes laufendes Token kann bis zum Notaus in
allen Kanälen schreiben, in denen das Bot-Konto Editor ist, an Freigaben und Audit-Log vorbei. In
Kauf genommen mit diesen Schutzmaßnahmen:

1. **Nur der Worker kann das Token lesen (E7).** Die Api versiegelt hybrid: ein zufälliger
   AES-256-Schlüssel verschlüsselt das Token (AES-GCM), ein öffentlicher RSA-Schlüssel (3072 Bit,
   OAEP mit SHA-256) verpackt den AES-Schlüssel. Ablageformat: `v1.` gefolgt von
   base64(verpackter Schlüssel, Nonce, Chiffrat, Tag). Konfiguration:
   `BotAccount:TokenPublicKey` an der Api, `BotAccount:TokenPrivateKey` nur am Worker
   (eigene Compose-Variablen, nichts davon im Repo, Regel 17). Unabhängig von
   `Auth:Twitch:TokenEncryptionKey`.
2. **Verworfene Varianten:** (a) symmetrischer Schlüssel an der Api: Der aus dem Internet
   erreichbare Prozess könnte entschlüsseln, „nie zurücklesen“ wäre nur Disziplin. (b) Klartext per
   Redis an den Worker: Das Token läge im Klartext auf dem Pub/Sub-Kanal, wäre für jeden Subscriber
   und für `MONITOR` sichtbar und ginge verloren, wenn der Worker gerade nicht läuft.
3. **Klartext in der Api** existiert nur während der einen Anfrage, die das Token setzt. Die Api
   prüft die Form (drei base64url-Segmente), liest `exp` ohne Signaturprüfung für die Anzeige,
   lehnt ein schon abgelaufenes Token ab, versiegelt, speichert mit `Status = unverified` und stößt
   den Worker über Redis an (`BOTACCOUNT:` ohne Nutzlast, wie `BACKFILL:` nur ein Anstoß). Sie ruft mit
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
   `BotAccount:Enabled = false`. Bis geprüft ist, ob ein normaler Logout andere Sitzungen
   beendet, gilt: mit dem Bot-Konto nirgends normal ausloggen. **Geprüft am 2026-11-24 (AK90):** ein
   normaler 7TV-Logout in einer anderen Sitzung lässt das gespeicherte JWT gültig; die Regel gilt für
   7TV nicht mehr. Verboten bleibt, die Twitch-Sitzung des Erneuerers abzumelden (Profil, aus dem die
   Cookies stammen, 18.2). „Sign Out Everywhere“ entwertet das JWT nachweislich (echtes HTTP 401). Mit der Erneuerung ist die
   Reihenfolge aus 18.8 verbindlich: „Sign Out Everywhere“ allein beendet die Twitch-Sitzung nicht
   (bestätigt in derselben Probe: ein Headless-Login aus der gespeicherten Twitch-Sitzung holte danach sofort ein neues JWT).
7. **Ablauf (F5, geändert durch den Nachtrag 18):** Die Admin-Seite zeigt `exp`. Ab 21 Tagen vorher
   (`Renewal:LeadDays`) erneuert der Erneuerer das JWT automatisch über einen Headless-Login mit der
   Twitch-Sitzung des Bot-Kontos (18.4); Hinweis auf der Seite und Log-Warnung je Tag ab 7 Tagen
   bleiben. Scheitert die Erneuerung, alarmiert `GET /api/health/bot-account` über Uptime Kuma (18.7).
   Die Handerneuerung über die Admin-Seite bleibt als Notweg, ohne Stack-Update.
8. **Ausfall und Rückfall (R3-10, R3-11):** Token fehlt, ist `invalid` oder `expired`, Notaus aktiv,
   oder der Schalter ist aus: **alle** Bot-Kanäle laufen im manuellen Ablauf. Editor-Rechte in
   einem Kanal entzogen, oder der Kanal ist nicht mehr durch die Pilot-Liste gedeckt (E8):
   **dieser** Kanal läuft im manuellen Ablauf (`BotSuspendedReason` = `not_editor` bzw.
   `not_listed`; seit G0 auch `invitation_pending`, solange eine neue Einladung eines aktivierten
   Kanals offen ist und der Bot sie noch nicht annehmen konnte, K4 „Einladungen“). Die Aktivierung bleibt gespeichert; entfällt der Grund, nimmt der Bot ohne neuen
   Editor-Eintrag wieder auf. Was dann
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
- **Erkennungsgrenze (Sol-Befund 3):** Ersetzt ein Mensch den Eintrag **zwischen** Vor-Read und
  `removeEmote` (er entfernt den Bot-Eintrag und fügt denselben Alias neu hinzu), entfernt der Bot
  den Ersatz. Diese fremde Generation steht weder im Vor-Read noch im Nach-Read, der Vergleich
  schlägt nicht an, und es gibt keine Reparatur. Erkennbar ist der Fall nur, wenn ein Sync den
  Ersatz zuvor als Slot gesehen hat: Dann archiviert der nächste Abgleich einen Slot, den kein
  Wunsch besaß; der Bot schreibt in diesem Fall `wish.botSuspectRemoval` (Wunsch, Slot) als Hinweis
  im Audit-Log, ohne Reparatur. Null Reparaturen beweisen also nicht null fremde Entfernungen.
- **Vor dem Schreiben erneut prüfen (E25):** Unmittelbar vor `removeEmote` sperrt der Bot die Zeile
  des Wunsches und prüft: `RemovalQueuedAtUtc` gesetzt, `RemovalQueuedVersion` = `DecisionVersion`,
  Zustand `active` mit `ExpiresAtUtc` ≤ jetzt oder `presented` mit ausdrücklichem Auftrag,
  `BotOwned` wahr, `StartsAtAuthoritative` wahr (ein vorläufiger Start führt nie zu einer
  Bot-Entfernung; Plan-484 Revision 2). Die Sperre hält bis zum Schreiben des Ergebnisses; eine gleichzeitige
  Entscheidung wartet höchstens das Timeout des Schreibzugriffs und trifft dann auf den neuen
  Zustand (409).
- **Eigentum endet** durch Umbenennen, durch Entfernen und Hinzufügen außerhalb des Bots, durch
  „Behalten“ und durch Abschalten des Bot-Modus. Ein Moduswechsel überträgt nie Eigentum an
  Einträgen, die ein Mensch hinzugefügt hat. Ein zweiter Alias desselben Emotes bleibt unberührt.
- **F12:** Einträge, die ein Verwalter mit eigenem Token über Emote Purge hinzugefügt hat, entfernt
  der Bot nie; sie werden vorgelegt (`not_bot_owned`).
- **Belegt am 2026-11-24 (Probe T0, AK86, AK87, AK91):** `addedById` trägt nach `addEmote` des Bots dessen
  7TV-ID, öffentlich wie authentifiziert. Ein menschliches Umbenennen lässt `addedById` (Bot) und `addedAt`
  unverändert: Eigentum endet deshalb nur über den exakten Alias, `addedById` allein reicht nie. Entfernt ein
  Mensch den Eintrag und fügt ihn unter demselben Alias neu hinzu, zeigt der Read `addedById` = Inhaber und ein
  neues `addedAt`, und `removeEmote(emoteId, alias)` entfernt diesen Menschen-Eintrag ohne Schutz durch 7TV:
  der Vergleich vor dem Entfernen ist zwingend und erkennt den Fall; das Restrisiko des Fensters danach bleibt.

## 9. Child Issues

In Baureihenfolge:

| # | Titel | Priorität | Aufwand | Status | Hängt ab von |
|---|---|---|---|---|---|
| K1 | Backend-Kern: Datenmodell, Lebenszyklus, Zuordnungsregel, Verwalter-API, Slot-Abgleich, Live-Event, Retention, Schalter | P1 | 4,5 Tage | offen | keine |
| K2 | Ablauf-Lauf im Worker: Fälligkeit, Schutz, Lückenregel | P1 | 2 Tage | offen | K1 |
| K3 | Frontend manueller Modus: Tab je Kanal als Arbeitsliste, Abschnitt im Settings-Tab, kanalübergreifende Seite, Zähler in der Kopfzeile | P1 | 4,5 Tage | auf Branch `nacht/485-wish-ui`, nicht gemergt; Live-Probe (AK45) und AK44 offen | K1 (Vertrag), K2 (nur Live-Probe) |
| K6 | Chat-Erfassung und Belohnung anlernen: IRC-Tag, Anlernen, offene Wünsche mit Zuordnung, Freigeben, Ablehnen, Verfall, Lückenanzeige, samt Oberfläche | P1 | 7 Tage | offen | K1, K3 |
| K4 | Bot-Backend: Token-Ablage und Admin-Seite, Schreib-Client, Warteschlange, Editor-Prüfung, Eigentum, Notaus, Doku | P2 | 6 Tage | offen | K1, K2 |
| K5 | Bot-Frontend: Aktivierungsdialog mit Editor-Mutation (D7), Abschalten, Bot-Zustände auf den Karten | P2 | 2,5 Tage | offen | K3, K4, Probe „Editor-Mutation“ |

Die frühere „Scheibe 3 ohne Issue“ ist K6. Später und ohne Issue bleiben nur F10 (Bot nicht mehr
anonym, Chat-Antworten) und die Punkte aus Abschnitt 15.

## 10. Dependency Graph

```
K1 ──► K2 ──► K3 ──► K6 ══► Freigabe 1 (manueller Modus mit Erfassung)
        │      │
        └──────┴──► K4 ──► K5 ══► Freigabe 2 (Bot-Modus)
```

K3 braucht K2 nur für seine Live-Probe mit einem fälligen Wunsch. K4 hängt nicht an K6 und könnte
parallel zu K6 beginnen; die Reihenfolge ist die vom Betreiber gesetzte.

## 11. Sequencing Rationale

- **K1 zuerst**, weil es jeden Vertrag festlegt, gegen den die anderen bauen (Entität, Zustände,
  Zuordnungsregel, Routen, Fehlercodes, Event-Typ, Audit-Konstanten). Hinter dem Schalter erreicht
  es niemand.
- **K2, dann K3:** Fälligkeit und die Oberfläche des manuellen Modus. K3 liefert den Tab und den
  Settings-Abschnitt, auf denen K6 aufbaut.
- **K6 danach als ein Kind samt Oberfläche.** Sein größtes Risiko, die Einlösung eines Kontos ohne
  Mod-Status auf der anonymen Verbindung, ist seit 2026-11-17 ausgeräumt (AK46 bestanden). Damit
  spricht nichts mehr dafür, K6 vorzuziehen.
- **Freigabe 1 erst mit K1, K2, K3 und K6:** manueller Modus mit automatischer Erfassung. Ohne
  Erfassung nutzt das Feature niemand; bis dahin bleibt `Wishes:Enabled` auf Prod aus.
- **K4 nach K2**, weil der Bot nur ausführt, was der Ablauf-Lauf als fällig und entfernbar
  entschieden hat. **K4 vor K5**, weil der Aktivierungsdialog die Editor-Prüfung des Workers
  braucht. K5 startet erst, wenn die Probe zur Editor-Mutation ein Schema geliefert hat; fällt sie
  durch, bleibt K5 bei einer Anleitung („Bot-Konto in 7TV von Hand als Editor eintragen“) plus
  Prüfknopf.
- Scheitert der Bot-Teil an 7TV, bleibt Freigabe 1 vollständig. Bot zuerst wäre Variante C des
  Entwurfs und wurde verworfen.

## 12. Definition of Done (Epic)

Freigabe 1 (manueller Modus mit Erfassung):

1. K1, K2, K3 und K6 gemergt; Zweitmeinung (Codex Sol) je Branch eingeholt und vorgelegt.
2. `dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false` und
   `npm --prefix web run e2e` grün; `node scripts/coverage-local.mjs` für jeden PR gelaufen.
3. Migrationen `AddWishes` und `AddWishCapture` von Hand auf Prod angewendet, jeweils vor dem Image.
4. Die Live-Probe AK46 (Einlösung ohne Mod-Status) ist bestanden (2026-11-17) und in DECISIONS festgehalten.
5. Datenschutztext ergänzt; danach `Wishes:Enabled` und `Wishes:Capture:Enabled` auf Prod an.
6. Im Kanal des anfragenden Mods ist eine Belohnung angelernt, und ein eingelöster Wunsch mit
   7 Tagen Laufzeit hat den Weg live durchlaufen: Erfassung, Hinzufügen in 7TV, automatische
   Zuordnung, Probezeit, Vorlage bei Ablauf, Entscheidung.

Freigabe 2 (Bot-Modus):

7. K4 (mit dem Erneuerer, PR 3 aus Plan-487) und K5 gemergt, Zweitmeinung je Branch; Migration
   `AddBotAccount` auf Prod. **Stufe 0 (AK120), noch ohne Pilot-Kanal:** alle Images, Bot-Modus und
   Erneuerung an, `Wishes:PilotChannelIds` leer und kein Kanal aktiviert (so schreibt der Bot nirgends),
   Token gesetzt und `valid`, Maschinengeheimnis gesetzt, Erneuerer am Host läuft, „Jetzt erneuern“
   tauscht über die Maschinenroute; Kuma-Monitor auf `GET /api/health/bot-account` eingerichtet, eine
   Testmeldung (ein eindeutiger Fehlschlag mit einer Kopie der Cookie-Datei mit verfälschtem
   `auth-token` über den Eingang, danach Original und „Jetzt erneuern“) und die Entwarnung sind beim Betreiber
   angekommen (18.7, keine Vertretung); bei angehaltenem Kuma hat der unabhängige Heartbeat den Betreiber per E-Mail
   erreicht, samt Entwarnung; nach dem Tausch hat der Worker binnen 15 Minuten genau die neue `CredentialVersion`
   geprüft (AK120). Ein Tausch an der Api allein öffnet das Tor nicht.
8. Erst danach `Wishes:PilotChannelIds` auf die Bot-Pilot-Kanäle gesetzt.
9. Bot-Modus in mindestens einem Kanal aktiv; die Live-Proben AK86 bis AK91, AK108 und AK116
   dokumentiert.
10. Ein Wunsch mit 7 Tagen Laufzeit hat den Bot-Weg live durchlaufen: Erfassung, Freigabe,
    Bot-Hinzufügen, Bestätigung, Probezeit, Bot-Entfernung oder Vorlage.

Für beide:

11. DECISIONS, Architectur.md, PRODUCT.md, Operations.md, CLAUDE.md (Umsetzungsstand, Worker-Liste:
    am Ende siebzehn Hosted Services) und Backlog `A18` sind nachgezogen.

## 13. Beobachtung nach Aktivierung (Pilot)

Kein formales Fenster und keine Freigabebedingung. Die erste Freigabe beim anfragenden Mod ist
Freigabe 1: manueller Modus mit automatischer Erfassung. Der Bot-Modus folgt mit Freigabe 2 über
die Pilot-Liste (E8). Der eigene Kanal des Betreibers ist kein Affiliate und hat keine Kanalpunkte.

- **Erster Funktionstest mit 7 Tagen Laufzeit** (an der angelernten Belohnung als Standardlaufzeit
  gesetzt), damit Erfassung, Zuordnung, Probezeit, Fälligkeit und Vorlage binnen einer Woche live
  sichtbar werden; mit Freigabe 2 dasselbe mit Bot-Hinzufügen und Bot-Entfernung. Kürzere Laufzeiten
  (1 bis 6 Tage) bleiben einstellbar und folgen denselben Regeln (E2, E4).
- **Beobachtungszeitraum: 30 Tage ab der jeweiligen Aktivierung** (gemeint ist der Zeitraum, nicht
  die Laufzeit; unabhängig von den 30 Tagen Lebensdauer des Bot-JWT).
- **Messgrößen (R3-9):** (a) mindestens 90 % der fällig gewordenen Wünsche sind binnen 24 Stunden
  nach `ExpiresAtUtc` in einem Endzustand oder verlängert; (b) ab Freigabe 2: keine **bekannte**
  fremde Entfernung durch den Bot, das heißt 0 Audit-Einträge `wish.botRepair`, 0 Einträge
  `wish.botSuspectRemoval` und keine Meldung eines Verwalters. Das ist eine untere Schranke, kein
  Beweis: Der Wettlauf aus Abschnitt 8 (Erkennungsgrenze) hinterlässt nicht immer eine Spur;
  (c) nur zur Kenntnis: Anteil der erfassten Wünsche, die ohne Handgriff zugeordnet wurden, und
  Zahl der Erfassungslücken.
- **Auswertung über das Audit-Log**, nicht über Felder der Wunsch-Zeile: `ExpiresAtUtc` wird beim
  Verlängern überschrieben, `StatusChangedAtUtc` bei jedem Übergang. Maßgeblich ist der im
  Eintrag `wish.due` festgehaltene Ablauf (`expiresAtUtc` in den Details), nicht der Zeitpunkt des
  Eintrags (Plan-484, Revision 1 und 2). Eine Fälligkeit ist ein Paar (`wishId`, Ablauf); der Ablauf
  kommt aus `expiresAtUtc` in `wish.due` oder aus `previousExpiresAtUtc`, das `wish.extend`,
  `wish.keep`, `wish.close` und `wish.remove` tragen, wenn der Wunsch beim Befehl überfällig war
  (so bleibt eine Fälligkeit im Nenner, die ein Verwalter entscheidet, bevor der Lauf sie gesehen
  hat). Kohorte von (a) sind die Fälligkeiten, deren Ablauf im Beobachtungszeitraum liegt und zum
  Auswertungszeitpunkt mindestens 24 Stunden zurückliegt (Reife). Zähler sind die davon, zu deren
  `wishId` ein Eintrag `wish.extend`, `wish.keep`, `wish.remove`, `wish.botRemove` oder
  `wish.close` mit Zeitpunkt nach dem Ablauf und höchstens 24 Stunden danach vorliegt.
  Stellt der Lauf eine Fälligkeit erst später als 24 Stunden nach dem Ablauf fest (Worker-Ausfall,
  vorläufiger Start), zählt sie als nicht rechtzeitig erledigt und wird in der Auswertung eigens
  ausgewiesen. Ein außerhalb von Emote
  Purge entfernter Eintrag hinterlässt keinen dieser Einträge; solche Fälle zählt der Betreiber aus
  der Liste der Nicht-Treffer von Hand als erledigt (erwartet: einzelne). (c) zählt `wish.autoLink`
  gegen die Zahl der erfassten Wünsche. Die Abfragen stehen im Operations-Runbook. Keine eigene
  Oberfläche dafür.

## 14. Rollout und Rollback

Schalter (alle Default `false` bzw. leer, also aus für andere Betreiber):

| Schlüssel | Compose-Variable | Wirkung |
|---|---|---|
| `Wishes:Enabled` | `WISHES_ENABLED` | Api-Routen, Tab, Zähler, Ablauf-Lauf |
| `Wishes:PilotChannelIds` | `WISHES_PILOT_CHANNEL_IDS` | E8: nur Bot-Modus; leer = nur globale Admins; gilt beim Aktivieren und bei jedem Schreiben |
| `Wishes:Capture:Enabled` | `WISHES_CAPTURE_ENABLED` | E22: Chat-Erfassung, Anlernen, Erfassungs-Worker |
| `Wishes:Capture:MaxPendingPerRequester` | keine | offene Wünsche je Zuschauer und Kanal, Default 3 |
| `Wishes:ExpiryIntervalMinutes` | `WISHES_EXPIRY_INTERVAL_MINUTES` | E6 |
| `BotAccount:Enabled` | `BOT_ACCOUNT_ENABLED` | Bot-Konto: Bot-Worker, Aktivierung, Admin-Seite, Erneuerung |
| `BotAccount:TokenPublicKey` | nur am Dienst `api` | E7 |
| `BotAccount:TokenPrivateKey` | nur am Dienst `worker` | E7 |
| `BotAccount:WriteDelaySeconds` | keine | Abstand zwischen zwei Schreibzugriffen, Default 2 |
| `BotAccount:Renewal:Enabled` | `BOT_ACCOUNT_RENEWAL_ENABLED`, nur `api` | 18.5: Maschinenroute, Alarmzeilen zu Erneuerung, Sitzung und Lebenszeichen |
| `BotAccount:Renewal:LeadDays` | keine | 18.4, Default 21, 7 bis 28 |
| `BotAccount:Renewal:SilenceAlertHours` | keine | 18.7, Default 26, 1 bis 168 |

Der Erneuerer gehört nicht in die Compose-Dateien des Servers; seine Konfiguration (Api-Adresse,
Maschinengeheimnis, Pfad der Cookie-Datei) beschreibt `docs/Operations.md` generisch (18.2).

Reihenfolge je Auslieferung: Migration von Hand über den Tunnel (`list`, `update`, `list`), dann
Images, dann Schalter. Additive Migrationen stören das laufende alte Image nicht. Den Deploy-Hinweis
gegen **alle** ausstehenden Migrationen prüfen, nicht nur gegen die eigene.

`Wishes:Enabled` bleibt auf Prod aus, bis K1, K2, K3 und K6 gemergt sind (Freigabe 1). Der
Bot-Schalter folgt erst mit Freigabe 2.

Rollback: Schalter aus genügt in jedem Fall; die Tabellen bleiben liegen und stören kein älteres
Image (präzisiert in Plan-487, Abschnitt 6: nach einer Aktivierung braucht ein Image-Downgrade die
Prüfabfrage). Bot-Notaus in der Reihenfolge aus 18.8: Admin-Schalter, Maschinengeheimnis widerrufen,
Erneuerer am Host stoppen (wenn erreichbar), Twitch-Sitzungen des Bot-Kontos beenden, „Sign Out Everywhere“, nach
15 Minuten ein zweites, Token löschen; die Löschrouten bleiben auch bei ausgeschalteten Schaltern erreichbar. Nach einem
Restore gilt der SQL-Schritt aus 18.8 vor dem Start von Api und Worker. Rückweg nur für den
Erneuerer: Container am Host stoppen, Geheimnis widerrufen, danach darf `BOT_ACCOUNT_RENEWAL_ENABLED`
aus; dann gilt die Handerneuerung. Nach einem Notaus gilt der Rückfall aus
Abschnitt 7 Punkt 8; nichts geht verloren, alles Offene steht als Handlungsbedarf in der Übersicht.

## 15. Out of Scope

- **F10:** Bot liest nicht mehr anonym mit, Chat-Antworten des Bots.
- **Globaler Admin-Hinweis**, wenn der Tag `custom-reward-id` dauerhaft verschwindet (Entwurf,
  Sol-Befund 6): K6 zeigt je Kanal nur „zuletzt erkannt“ und die Erfassungslücken.
- **Belohnungen ohne Texteingabe:** erscheinen nicht im Chat und sind nicht erfassbar (P1).
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
- ~~Automatische Erneuerung des Bot-JWT (F5, zweiter Teil).~~ Aufgehoben am 2026-11-20 durch den
  Betreiber, s. Abschnitt 18.
- Automatischer Twitch-Login des Bot-Kontos mit Passwort oder 2FA: Am Server liegt nur die
  Sitzung, nie ein Passwort (18.3).
- Ein zweiter Alias desselben Emotes als eigener Wunsch (E13).
- Auflösen des Twitch-Logins beim Handeintrag über Helix.
- Wiederverwendung der Import-Kollisionslogik (#368) für Wünsche: Ein Wunsch ist ein einzelner
  Eintrag; ein Alias-Konflikt wird gemeldet, nicht stapelweise aufgelöst (F2).

## 16. Restrisiken

| # | Risiko | Umgang |
|---|---|---|
| RR1 | Worker-Ausfälle je Kanal sind nicht persistiert; eine Zähllücke durch einen Ausfall bleibt unerkannt und ein beliebtes Emote kann trotz Schutz entfernt werden (Entscheidung 2, R3-14) | benannt; Rückweg „wieder hinzufügen“; eine Lückentabelle wäre ein eigenes Vorhaben |
| RR2 | Wettlauf zwischen frischem Read und `removeEmote` (D33-Klasse). Wird der Eintrag in diesem Fenster entfernt und unter demselben Alias neu hinzugefügt, entfernt der Bot den Ersatz, **ohne dass Vor- und Nach-Read es zeigen** | Erkennung und einmalige Reparatur für den sichtbaren Teil; für den unsichtbaren nur der Hinweis `wish.botSuspectRemoval`, falls ein Sync den Ersatz gesehen hat; Rückweg „wieder hinzufügen“; Messgröße im Pilot ist eine untere Schranke |
| RR3 | Bot-Modus ist von 7TV nicht bestätigt; 7TV kann das Website-JWT, das Schema oder das Editor-Modell jederzeit ändern | Rückfall auf manuellen Ablauf ist der Normalpfad jedes Fehlers; DECISIONS nennt den Betrieb „inoffiziell“ |
| RR4 | Gestohlenes Token schreibt bis zum Notaus an Emote Purge vorbei | Abschnitt 7; Editor-Vergabe je Kanal begrenzt die Reichweite |
| RR5 | Ob ein normaler Logout das Server-Token entwertet, ist ungeprüft | Betriebsregel „nirgends normal ausloggen“; Probe AK90. **Erledigt 2026-11-24:** ein normaler 7TV-Logout entwertet es nicht; die Regel gilt nur noch für die Twitch-Sitzung des Erneuerers (Abschnitt 7 Punkt 6) |
| RR6 | 7TV-Reads können veralten; ein Bestätigungs-Read kann den neuen Eintrag verspätet zeigen | E11, danach Vorlage statt Annahme |
| RR7 | `presented` ohne Timeout kann liegen bleiben | Zähler und Übersicht halten es sichtbar; E14 begrenzt die Personendaten |
| RR8 | Der Einwilligungsgedanke aus D7 (nur wer in 7TV Editoren verwalten darf, kann den Bot eintragen) ist unbelegt (R3-15) | Probe AK108; fällt sie durch, entscheidet der Betreiber über D7 neu |
| RR9 | Einlöser-Daten werden nur bei `Retention:Enforce = true` geleert | auf Prod an; für fremde Betreiber im Operations-Text benannt |
| RR10 | Der Tag `custom-reward-id` ist undokumentiert und kann ohne Ankündigung wegfallen | E22; Rückfall auf Nachtragen von Hand; dokumentierte Alternative EventSub `channel.chat.message` bleibt eine eigene Entscheidung |
| RR11 | Die Erfassung ist „nach bestem Wissen“, kein lückenloses Einlöse-Register | Lückenanzeige in K6; Nachtragen von Hand |
| RR12 | Ein Treffer über den Namen kann ein anderes Emote gleichen Namens treffen, das zufällig im Fenster hinzugefügt wurde | nur eindeutige Treffer; Karte zeigt Bild und Einlösetext; „Zuordnung lösen“ in K6 |
| RR13 | Der eingelöste Text ist Freitext eines Zuschauers und kann Personenbezug oder Anstößiges enthalten | nur für Verwalter sichtbar, nie im Audit-Log, Löschung nach 6.5 |
| RR14 | K3, hingenommene Lücke: Zwischen der letzten Beobachtung eines Eintrags (höchstens 5 s alt) und 7TVs unbedingtem `removeEmote` liegen bis zu 5 s plus die Antwortzeit der Mutation; ein Set-Wechsel direkt nach der Set-Prüfung bleibt unsichtbar. Dazu: `complete-removal` sieht einen Ersatz, den noch kein Sync geschrieben hat, nicht (R15); der Set-Wechsel steht vor der Inventursperre im Protokoll (R19); ein Read, der den Eintrag weg findet, bucht ihn als Entfernen des Verwalters auch dann, wenn ein anderer Editor ihn entfernt hat (R20, `evidence = absent` markiert es) | clientseitig nicht schließbar; jede Abweichung ist ein Überspringen ohne Anfrage; `complete-removal` lehnt mit 409 ab, wenn ein Ersatz sichtbar ist |
| RR15 | Die Twitch-Sitzung des Bot-Kontos am Erneuerer-Host erlaubt, das Konto zu übernehmen und beliebig viele 7TV-JWTs auszustellen (18.3) | nie am Server, Isolation 18.6, Notaus 18.8, Bot-Konto ohne weiteren Wert |
| RR16 | Die Erneuerung ist für Twitch und 7TV inoffiziell; Login-Flow, Token-Ablage, Captcha oder eine Sitzungsbindung können sie jederzeit brechen | Fehlerklassen nur über positive Merkmale, Alarm 18.7, Rückfall und Handerneuerung; G0b am Erneuerer-Host vor T1, Stufe 0 vor dem ersten Pilot-Kanal |
| RR17 | Mit dem Maschinengeheimnis lassen sich Lebenszeichen fälschen (Alarm bleibt aus) und ein formgültiges, falsches JWT einspielen (globaler Rückfall bis Hand-`PUT`) | Geheimnis nur am Erneuerer-Host, widerrufbar; Totmann und JWT-Ablauf hängen nicht am Lebenszeichen; hingenommen ([B] N2) |
| RR18 | Ohne erreichbaren Betreiber wird keine Störung der Erneuerung behoben (keine Vertretung, [B] N1) | 21 Tage Vorlauf als Zeit zum Beheben; danach sicherer Rückfall auf den manuellen Ablauf (18.1) |
| RR19 | Twitch könnte die Erkennung von Automation auf die Authorize-Seite ausdehnen, die der Erneuerer aufruft (Probe 4: der Passwort-Login ist schon gesperrt) | Die Erneuerung scheitert, Alarm, Rückfall über den bestehenden Weg (Handerneuerung); mehr als die Erkennung gibt es nicht |
| RR20 | Der Bot-Modus hängt seit G0 an einer weiteren inoffiziellen Mutation: der Annahme einer Editor-Einladung durch das Bot-Konto (`userEditors.editor(…).updateState(state: ACCEPT)`); 7TV kann sie oder das Einladungsmodell ändern | Annahme scheitert sichtbar (`invitation_not_accepted` beim Aktivieren, `invitation_pending` im aktivierten Kanal), nie ein Schreiben in ein Set ohne angenommene Einladung; Notweg: der Betreiber nimmt als Bot von Hand in einem getrennten Browser-Profil an, nie im Profil des Erneuerers |

## 17. Entschieden (Betreiber, 2026-11-17)

Die neun Widersprüche der ersten Fassung sind entschieden. Je Punkt eine Zeile:

| # | Frage | Entscheid | Steht in |
|---|---|---|---|
| W1 | Zustand `offen` ohne Erzeuger | **Nicht in K1 bis K5.** In Runde 2 präzisiert: Freigeben, Ablehnen und Verfall baut K6 vollständig | E3, E13, Abschnitt 5, Abschnitt 9 |
| W2 | Token symmetrisch oder asymmetrisch | **Asymmetrisch angenommen:** Api nur öffentlicher Schlüssel, Worker privater | E7, Abschnitt 7 |
| W3 | Einlöser-Daten bei dauerhaft `vorgelegt` | **Kappe angenommen:** 30 Tage nach Ablauf geleert | E14, 6.5 |
| W4 | D7 gegen Laufzeit je Wunsch | **Jeder Verwalter setzt die Laufzeit je Wunsch;** D7 gilt nur für Kanal-Vorgaben | E17 |
| W5 | Takt des Ablauf-Laufs | **Eigener Worker, 15 Minuten** | E6, K2 |
| W6 | Zähler live per SSE | **Poll** beim Seitenaufruf und alle 5 Minuten, eigener leichter Endpunkt; kein `wish.changed` auf dem globalen Stream; SSE nur im Wünsche-Tab | E18, K1, K3 |
| W7 | Pilot-Liste | **Nur für den Bot-Modus, fail-closed:** leer = nur globale Admins; gilt auch im Betrieb (X2); manueller Modus nur an `Wishes:Enabled` | E8, K4 |
| W8 | Lückenregel ohne Schutz | **Nur bei eingeschaltetem Schutz** | E5 |
| W9 | Ort der Einstellungen | **Settings-Tab,** neuer Abschnitt „Wünsche“ unter dem Backfill-Abschnitt; Tab-Link an `canManage` und mindestens einem der beiden Schalter (X1) | E19, K3, K5 |

### Runde 2 (Betreiber, 2026-11-17): Chat-Erfassung wird Kind K6

| # | Entscheid | Steht in |
|---|---|---|
| V1 | Die Chat-Erfassung ist kein „später“, sondern Kind K6 mit eigenem Issue; sie nimmt alles auf, was W1 aus K1 bis K5 herausgenommen hat | Abschnitt 9, K6 |
| V2 | Der eingelöste Text wird ausgewertet, und ein offener Wunsch wird automatisch `aktiv`, sobald der passende Slot auftaucht, egal auf welchem Weg | E20, E21, 6.3 |
| V3 | Erste Freigabe erst mit K1, K2, K3 und K6; der Bot-Modus folgt danach (Reihenfolge in Runde 3 gesetzt, X3) | Abschnitte 10 bis 14 |
| V4 | Der Handeintrag bleibt als Nebenweg | Abschnitt 3, K3 |

### Runde 3 (Betreiber, 2026-11-17): die acht Folgepunkte

| # | Frage | Entscheid | Steht in |
|---|---|---|---|
| X1 | W9 gegen Backfill-Spec D17 und AK14 | Tab-Link an `canManage() && (chatLogBackfillEnabled() \|\| wishesEnabled())`; beide Schalter aus heißt weiterhin kein Tab; kein leerer Zustand | E19, K3 |
| X2 | Pilot-Liste nur beim Aktivieren? | **Fail-closed auch im Betrieb:** fällt ein Kanal aus der Liste oder geht `Wishes:BotMode:Enabled` aus, schreibt der Bot nicht mehr; Rückfall wie beim Notaus, Aktivierung bleibt gespeichert | E8, Abschnitt 7 Punkt 8, K4 |
| X3 | Reihenfolge gegen Oberfläche von K6 | **K1, K2, K3, K6, dann K4, K5.** K6 ist ein Kind samt Oberfläche, keine Teilung in zwei PRs | Abschnitte 9 bis 11 |
| X4 | Automatisch `aktiv` gegen Annahme 1 | **Angenommen:** Hinzufügen im Set ist Freigabe; der Bot entfernt nur Einträge mit `addedById` = Bot | Abschnitt 1, E21 |
| X5 | Namens-Treffer | Nur eindeutige Treffer plus „Zuordnung lösen“; **kein** Kanal-Schalter „nur mit Link“; als Restrisiko geführt | 6.3, RR12 |
| X6 | Zuordnungsfenster | **Gleich der Verfallsfrist**, kein eigener Wert | E3, E20 |
| X7 | Admin-Hinweis bei fehlendem Tag | **Out of Scope;** „zuletzt erkannt“ je Kanal reicht | Abschnitt 15 |
| X8 | Eingelöster Text | Wie die Einlöser-Daten nach 30 Tagen geleert; der Datenschutz-Hinweis nennt ihn mit | 6.5 |

Zur Kenntnis, ohne offenen Entscheid: Der Zähler hat bis zu 5 Minuten Verzug (W6). Aus X2 folgt für
Kanäle, die ein globaler Admin außerhalb der Liste aktiviert hat: Der Bot schreibt dort nur, solange
das aktivierende Konto globaler Admin ist (E8).

### Runde 4 (2026-11-17): adversariale Prüfung durch Codex Sol, Stand `e08dfe94`

Alle neun Befunde sind an den zitierten Codestellen nachgeprüft und eingearbeitet:

| # | Befund | Eingearbeitet in |
|---|---|---|
| S1 (P1) | Aktivieren umgeht D7: jeder Twitch-Mod konnte wieder einschalten | E24, K4 (Filter, Fehlercodes), AK-Fall „Aktivieren prüft den Aufrufer“ |
| S2 (P1) | Eingereihte Entfernung überlebt eine Verlängerung | E25, Abschnitt 8, K1, K2, K4, AK-Fall „Einreihen, dann entscheiden“ |
| S3 | Reparatur sieht nicht jede fremde Entfernung | Abschnitt 8 (Erkennungsgrenze), RR2, Abschnitt 13 (b), AK-Fälle Eigentum und Verschränkung |
| S4 | Vorgelegte Wünsche erreichten die Bot-Warteschlange nie | Abschnitt 5, K4 (`bot-remove`, Auswahl), AK-Fall „Bot entfernen lassen“ |
| S5 | Unbestätigte Wünsche entgingen der Löschfrist | E14 (a) bis (c), 6.5, AK17 |
| S6 | Abschalten der Erfassung stoppte das Aufräumen | E26, 6.6, K6 (Haushalts-Takt), AK-Fälle Anlernen und Aufräumen |
| S7 | Erfassung hinter der Rückkehr bei leerem Cache | K6 (Stelle `:1063-1094`), AK-Fall „Leerer Emote-Cache“ |
| S8 | Unbestimmte Shared-Chat-Herkunft | K6 (nur `MessageOrigin.Own`), AK-Fälle Parser und Vertragstest |
| S9 | Zusammenführung verlor Belohnungen | 6.3, K6, AK-Fall „Kanal-Zusammenführung“ |

Runde 5 (Codex-Quality-Gate, neun Unklarheiten) hat keine Produktentscheidung geändert, nur
Verträge geschlossen: Filterfolge beim Aktivieren (E24), vollständige Übergangstabelle,
Koordination von Bot und Abgleich über `BotWriteAtUtc`, Felder und Sperrtabelle beim Lösen einer
Zuordnung, Zuordnung ohne Toleranz, Idempotenz des Ablauf-Laufs, DTO-Felder, Pilot-Messung über
`wish.due`, Grammatik für Namen.

Dazu: Die Live-Probe zur Einlösung ohne Mod-Status ist bestanden (K6, erstes Akzeptanzkriterium),
und der Link-Parser nimmt beide 7TV-ID-Formate an.

## 18. Nachtrag 2026-11-20: automatische Token-Erneuerung

Stand 2026-11-20: Betreiberentscheid, zwei Proben vom selben Tag, drei unabhängige Reviews (Fable, Sol
`gpt-6.2-sol`, Astra `gpt-6.2-astra`) und die Betreiberentscheidungen danach. Der Nachtrag hebt die Zeile
„Automatische Erneuerung des Bot-JWT“ in Abschnitt 15 auf und ändert Abschnitt 7 Punkt 7, 6.4, 12 und 14;
die Stellen verweisen hierher. Kennzeichnung wie in Abschnitt 4: **[E]** Festlegung des Nachtrags (der
Betreiber kann widersprechen), **[B]** entschieden, **[N*n*]** Betreiberfrage (18.12, beide entschieden). Umgesetzt in
Plan-487, Revision 5 (Endstand des Nachtrags): dazu die letzte Sol-Runde über Revision 4 (drei P1, sechs P2, alle
übernommen) und die Betreiberentscheidungen vom 2026-11-20 abends (Helfer für die Cookie-Datei, keine Vertretung, N2).
Namen nach Plan-487, Revision 5a: Erneuerung, Maschinenroute, Alarm-Endpunkt, Erneuerer und Schalter gehören dem Bot-Konto,
nicht den Wünschen, und heißen neutral (`BotAccount…`, `/api/…/bot-account`); eine spätere Nutzung des Kontos (F11) bleibt so
möglich, ist aber nicht Teil dieses Nachtrags.

### 18.1 Ausgangslage

- **[B] Betreiber:** Handerneuerung alle 30 Tage ist ausgeschlossen. Kanäle verlassen sich auf den
  Bot-Modus; der Betrieb darf nicht davon abhängen, dass der Betreiber erreichbar ist (Krankheit,
  Urlaub).
- **[B] Grenze, ehrlich benannt (2026-11-20, N1):** Die Automatik nimmt dem Betreiber das **Erneuern** ab, nicht das
  **Beheben von Störungen**. Es gibt **keine Vertretung**: Klemmt etwas (Login verlangt, Captcha, Erneuerer-Host aus) und ist
  der Betreiber nicht erreichbar, entsteht kein neues Token, und der Bot fällt beim Ablauf des JWT sicher in den manuellen
  Ablauf (Abschnitt 7 Punkt 8). Deshalb wird ab 21 Tagen vor Ablauf erneuert (18.4): Der Vorlauf ist die Zeit, in der der
  Betreiber eine Störung beheben kann, bevor ein Kanal etwas merkt.
- **7TV kennt keine Verlängerung** (Quellcode, bereinigter Spiegel von `SevenTV/SevenTV`): jede
  Sitzung gilt fest 30 Tage (`login_finish`, v3 `handle_callback`), `parse_session` schiebt nur
  `last_used_at`, es gibt keine Refresh-Route, `userSessions.create` verlangt Staff-Recht. Ein
  v3-Login widerruft die alte Sitzung, ein v4-Login laut Live-Messung (P2) nicht. **Eine Automatik
  geht also nur über einen neuen v4-Login.**
- **Probe 1 (reines HTTP) verworfen:** `GET /v4/auth/login?platform=twitch&return_to=/` → 303
  `id.twitch.tv/oauth2/authorize` → 302 `auth.twitch.tv/authorize` → HTTP 200 mit einer JS-Seite;
  ohne Browser entsteht kein `code`.
- **Probe 2 (Headless-Chromium, Devbox) bestanden:** frischer, nicht persistenter Kontext, darin die 15
  Cookies von `twitch.tv` des Bot-Kontos → `7tv.app/` → Login-URL → `7tv.app/login/callback` → die
  Website ruft `/v4/auth/login/finish` → `localStorage["7tv-token"]` hält ein JWT (225 Zeichen, `exp =
  iat + 30 Tage`, `sub` = 7TV-ID des Bots, `users.me` = Bot-Konto). Keine Zustimmungsseite, kein 2FA,
  wenige Sekunden. Ohne die Sperren des späteren Dienstes (Host-Liste, Bild-/Schrift-Abbruch, Sandbox).
- **Probe 3 (Vorlauf G0b, NAS des Betreibers, 2026-11-20 gegen 21:15 UTC) bestanden:** Wegwerf-Container
  `mcr.microsoft.com/playwright:v1.63.0-noble` im eigenen Bridge-Netz (privates Netz und NAS gesperrt, Internet erreichbar,
  geprüft), DNS 1.1.1.1, 1 GB, 1 CPU, `cap_drop: ALL`, `no-new-privileges`, `read_only`, `tmpfs` für `/tmp`, Nicht-Root,
  Host-Liste im Browser, Bild-/Schrift-/Medien-Abbruch, die volle Menge der 15 Cookies. Ergebnis: neues JWT von der
  Heim-IP in 5,6 s gesamt, 30 Tage gültig, `sub` = Bot-ID, `users.me` = Bot-Konto; abgebrochen wurde nur
  `www.googletagmanager.com` (nicht nötig). **Nicht belegt** und deshalb weiter Teil von G0b: (1) die Chromium-Sandbox lief
  nicht (Playwright-Default `chromiumSandbox = false`); (2) die Speicherzahl ist unbrauchbar: `memory.peak` der cgroup stand
  am Limit (1024 MiB), weil sie Seitencache und das `tmpfs` (Paketinstallation nach `/tmp`) mitzählt, bei 171 MiB RSS des
  Treibers; kein OOM-Kill; (3) mit den beiden Läufen auf der Devbox bestehen seit dem 2026-11-20 drei parallele 7TV-Sitzungen
  des Bots, deren fortdauernde Gültigkeit (Überlappung) noch zu prüfen ist; (4) die kleinste Cookie-Menge ist offen.
- **Probe 4 (Vorlauf G0b, Schritte R0 und R2, 2026-11-24) bestanden, mit Korrektur des Helfers:** (a) **Twitch sperrt den
  Passwort-Login in einem von Automation gestarteten Browser** (von Playwright gestartetes Chromium, sichtbar): erst die
  irreführende Meldung „Dieser Benutzername existiert nicht“, dann „Dein Browser wird zurzeit nicht unterstützt“; ein Login in
  einem normalen Firefox oder Brave gelingt. Die Erneuerung selbst (Headless-Chromium mit vorhandener Sitzung, ohne
  Passwort-Login) ist davon nicht betroffen (Probe 2 und 3, R2 heute). (b) **Der Helfer-Weg über einen von Hand gestarteten
  Browser (18.2) hat R0 bestanden:** 15 Cookies, alle Domain `.twitch.tv`, `auth-token` läuft bis zum Login-Tag plus 180 Tage
  (2027-05-23). (c) **R2 bestanden:** drei Headless-Logins nacheinander aus der JSON-Datei; nach jedem blieben alle früheren
  7TV-JWTs gültig (`users.me`), mit dem Token des Helfers vier parallele Sitzungen. (d) **Abmeldung:** Wer sich im
  Browser-Profil abmeldet, aus dem Cookies exportiert wurden, entwertet die exportierte Twitch-Sitzung serverseitig (ein Logout
  im Profil beendete am 2026-11-21 und 2026-11-24 die zuvor exportierte Sitzung). Das stützt den Notaus und bestätigt, dass die
  Sitzung des Erneuerers nie in einem Browser benutzt wird. **Offen bleiben** R1, R3, die kleinste Cookie-Menge und R4 bis R7.
- **Vorbedingung [E]:** Die App-Freigabe für 7TV besteht bei Twitch, bis sie unter Einstellungen →
  Verbindungen widerrufen wird. Fehlt sie, landet der Lauf auf der Zustimmungsseite; der Erneuerer
  erkennt das an positiven Merkmalen der Seite, meldet es und klickt **nie**.
- **Twitch-Sitzung:** `auth-token` lief am 2026-11-20 gegen 20:40 UTC bis 2027-05-19T20:36:09Z, also
  rund 180 Tage. **[E] Ausgelegt wird auf den schlechtesten Fall:** die Sitzung endet fest rund 180 Tage
  nach dem letzten interaktiven Login; Gleiten ist eine Hypothese für G0b und AK116. Endet die Sitzung,
  braucht es einen Login des Betreibers mit Passwort und 2FA über den Helfer (18.2).

**Folge für den Betreiber:** im schlechtesten Fall etwa alle 150 Tage ein Login, mit 30 Tagen Vorwarnung
über die Alarmkette (18.7). Gleitet die Sitzung nachweislich, entfällt auch das.

### 18.2 Ort und Ablage [B]

- **[B] Der Erneuerer läuft nicht auf dem Internet-Server.** Beim Betreiber läuft er auf dessen NAS im
  Heimnetz. Im Repo ist er ein host-agnostisches Projekt mit eigenem GHCR-Image; `docs/Operations.md`
  beschreibt ihn generisch und empfiehlt einen Host außerhalb des aus dem Internet erreichbaren Servers.
  Die NAS-Einrichtung des Betreibers steht in `infra-docs`, nicht im Repo. Deploy und jeder Handgriff am
  NAS sind Sache des Betreibers; keine Session verbindet sich dorthin.
- **[B] Die Cookies des Bot-Kontos liegen nur am Erneuerer-Host:** eine Datei auf einem Volume (Rechte
  600, Eigentümer der Dienstnutzer), vom Betreiber dort abgelegt; rotierte Cookies bleiben dort. Sie
  erreichen nie den VPS, die Api, die Prod-Datenbank oder ein Backup. Die Prod-Datenbank hält wie bisher
  nur das versiegelte 7TV-JWT (Abschnitt 7) und den geheimnisfreien Erneuerungszustand.
- **Format [E, G0b]:** eine JSON-Liste in der Form von Playwrights `cookies()` (`name`, `value`,
  `domain`, `path`, `expires`, `httpOnly`, `secure`, `sameSite`), nur mit den Namen, die G0b als
  mindestens nötig bestimmt. Die Attribute bleiben, wie Twitch sie gesetzt hat; kein Umbau auf ein
  Domain-Cookie für `.twitch.tv`. Formprüfung beim Start und vor
  jedem Lauf: höchstens 16 KB, nur Domains unter `twitch.tv`, jeder Pflichtname genau einmal und nicht
  leer, jeder Ablauf in der Zukunft; sonst kein Lauf, Sitzungszustand `malformed` (18.4).
- **Gewinnung über einen Helfer [B] (2026-11-20, ersetzt die Profilkopie; Weg seit 2026-11-24 geändert, Probe 4):** Chrome-Cookies
  einer Profilkopie sind verschlüsselt und betriebssystemabhängig, und ein von Automation gestarteter Browser kommt bei Twitch
  nicht durch den Passwort-Login (18.1, Probe 4). **Der Betreiber startet deshalb einen Chromium-Browser (zum Beispiel Brave)
  von Hand** mit einem frischen, eigenen Datenverzeichnis (zum Beispiel ein Temp-Verzeichnis) und einem lokalen Debug-Port, der
  nur an `127.0.0.1` lauscht (`--user-data-dir`, `--remote-debugging-port=9222`, `--remote-debugging-address=127.0.0.1`), meldet
  sich dort von Hand bei Twitch an (Passwort, 2FA) und einmal auf 7tv.app. Ein Skript des Helfers (Form: Modus des
  Erneuerer-Projekts oder kleines Skript, das **nur anhängt**, nie einen Browser startet) hängt sich per CDP
  (`connectOverCDP`) an diesen Browser, prüft die 7TV-Anmeldung (positives Merkmal, `localStorage["7tv-token"]` vorhanden, der
  Wert wird nie gelesen oder ausgegeben), und schreibt die Cookies von `twitch.tv` mit allen Attributen und dem konservativen
  Ablauf als JSON in Playwrights `cookies()`-Form (Rechte 600, unter Windows nur der eigene Nutzer), gefiltert auf die
  Pflichtnamen, sobald G0b sie bestimmt hat (bis dahin alle 15). Danach schließt der Betreiber den Browser und löscht das
  Datenverzeichnis. Die Formprüfung bleibt eine Implementierung in Core und läuft im Helfer wie im Erneuerer; scheitert sie,
  schreibt der Helfer nichts. **Das Erneuerer-Image macht nie einen Passwort-Login.** Begründung für ein frisches Verzeichnis
  statt des eigenen Browser-Profils des Betreibers: die Sitzung soll allein dem Erneuerer gehören (ein Logout im Profil hat die
  zuvor exportierte Sitzung beendet, 18.1 Probe 4), Chromium sperrt den Debug-Port für das Standardverzeichnis, und
  Profil-Cookies sind verschlüsselt. Die Datei kommt per `scp` in den **Eingang** am Host (nächster Punkt). Diese Sitzung
  gehört nur dem Erneuerer; kein Browser benutzt sie danach. Das ersetzt die frühere Betriebsregel „Bot-Profil nicht mehr
  benutzen“. Der frühere Entwurf „sichtbarer, vom Helfer gestarteter Browser mit flüchtigem Profil“ ist damit verworfen.
- **Eingang statt Überschreiben [E] (Sol über Revision 4):** Der Betreiber legt eine neue Datei nie über die aktive, sondern
  als `incoming.json` neben sie. Einziger Schreiber der aktiven Datei ist der Erneuerer: Zu Beginn eines Takts, nie während
  eines Laufs, übernimmt er eine formgültige Eingangsdatei, die seit mindestens einer Minute unverändert ist (atomar, dann
  Eingang gelöscht; eine ungültige wird nach zehn Minuten zu `incoming.rejected` und gemeldet). Liegt beim Zurückschreiben nach
  einem Lauf eine Eingangsdatei, wird nicht zurückgeschrieben: die frische Datei gewinnt.
- **Konservativer Ablauf [E]:** gemeldet wird der früheste Ablauf der Pflicht-Cookies beim Ablegen.
  Verkürzt oder löscht Twitch ein Pflicht-Cookie, gilt der neue Wert sofort; verlängert es, bleibt der
  gemeldete Wert beim alten, bis ein Lauf **nach** dem alten Ablauf gelingt (dann ist Gleiten belegt). Der
  Wert steht als Metadatum in derselben Datei.
- **Neuer Login:** Nach Ablauf oder Notaus erzeugt der Helfer eine neue Sitzung und eine neue Datei; eine alte Datei wird
  nicht wiederverwendet.
- **Spalten an `BotAccountCredentials`** (6.4, alle additiv, Zähler mit Default 0, sonst nullbar; kein
  Geheimnis außer einem Hash): `IdentityVersion` (nur `PUT`/`DELETE /token` erhöhen sie),
  `KillSwitchVersion` (bigint, jede Änderung des Notaus erhöht sie, monoton), `TokenKeyFingerprint`
  (SHA-256 des öffentlichen Token-Schlüssels, geschrieben bei `PUT /token`), `RenewalRequestedAtUtc`,
  `RenewalRunId` (uuid, offener Lauf), `RenewalRunStartedAtUtc`, `RenewalRunRequestedAtUtc`,
  `RenewalRunCredentialVersion`, `RenewalRunKillSwitchVersion`, `LastRenewalSucceededAtUtc`,
  `NextRenewalAttemptAtUtc`, `RenewalFailureCount`, `LastRenewalErrorCode`, `RenewerSeenAtUtc`,
  `RenewerSecretHash`, `RenewerSecretSetAtUtc`, `TwitchSessionExpiresAtUtc` (gemeldet). **Revision 5:**
  `LastRenewalRunId` (uuid) und `LastRenewalRunOutcome` (Quittung des zuletzt abgeschlossenen Laufs, 18.4),
  `TwitchSessionState` (`valid`, `malformed`, `lost`; leer = nie gemeldet; getrennt vom Ergebnis der Erneuerung, 18.4),
  `LastVerifiedCredentialVersion` (die `CredentialVersion`, die der Worker zuletzt geprüft hat, 18.7). Dazu die Spalten
  aus Plan-487 Revision 2 (`CredentialVersion`, `RunnerEpoch`), die 6.4 noch nicht nennt.

### 18.3 Vertrauensgrenze, ausdrücklich benannt

- **VPS, Api, Prod-Datenbank:** wie Abschnitt 7, dazu nur der SHA-256-Hash des Maschinengeheimnisses.
  Ein kompromittierter VPS erhält die Twitch-Sitzung nicht.
- **Erneuerer-Host:** die Twitch-Sitzung des Bot-Kontos und das Maschinengeheimnis. Wer die Sitzung hat,
  handelt auf Twitch als Bot-Konto und kann bei 7TV beliebig viele Bot-JWTs ausstellen lassen, bis die
  Sitzung beendet ist; ein Notaus bei 7TV allein beendet das nicht (18.8). Der Rechner, auf dem der Helfer lief
  (18.2), hält die Datei nur bis zum `scp`; danach wird sie dort gelöscht (Runbook). Das Geheimnis erlaubt nur, was
  die Maschinenroute (18.5) annimmt: Lebenszeichen, Ergebnisse und JWTs, die die Api prüft. Neu dadurch
  sind zwei Missbräuche (RR17): eine gefälschte Entwarnung (Lebenszeichen ohne Lauf) und ein gefälschtes
  JWT mit gültiger Form und passendem `sub`, das nach dem Tausch beim ersten Read des Workers 401 liefert
  und damit den globalen Rückfall auslöst, bis ein Admin das Token von Hand setzt.
- In Kauf genommen mit: kein Passwort und kein 2FA-Geheimnis am Host, Bot-Konto ohne weiteren Wert,
  Isolation 18.6, Widerruf und Rotation des Geheimnisses, Notaus 18.8. DECISIONS benennt das als
  Erweiterung der Vertrauensgrenze aus Abschnitt 7 und nennt den Ort außerhalb des Internet-Servers.

### 18.4 Erneuerungslauf [E]

- **Zustand und Politik an der Api, der Erneuerer ist bis auf die Cookie-Datei zustandslos.** Takt 5
  Minuten: `tick` meldet das Lebenszeichen; die Api antwortet „nichts zu tun“ oder mit einem Lauf-Auftrag.
  Mehrere Instanzen sind harmlos, weil nur die Api Aufträge vergibt.
- **Fällig** ist ein Lauf, wenn alle drei Schalter an der Api an sind, `KillSwitch` aus,
  `BotSevenTvUserId` bekannt, `Status` `valid` oder `expired` und entweder `ExpiresAtUtc − jetzt ≤
  Renewal:LeadDays` (Default **21**, 7 bis 28; ein Lauf etwa alle 9 Tage, bei Ausfall bleiben rund drei
  Wochen) oder `RenewalRequestedAtUtc` gesetzt („Jetzt erneuern“), `NextRenewalAttemptAtUtc` erreicht,
  der eben gemeldete Sitzungszustand `valid` und kein Lauf offen (Lease 5 Minuten ab `RenewalRunStartedAtUtc`). **`invalid` wird nie erneuert und bleibt
  bis zur Admin-Aktion** (`PUT` oder `DELETE /token`): der Worker setzt aus `invalid` nie `expired`.
- **Anmeldung unter der Sperre (Revision 5):** `tick` und Ergebnis prüfen nach dem Filter **unter derselben
  Zeilensperre**, mit der sie schreiben, den Hash des vorgelegten Geheimnisses erneut gegen `RenewerSecretHash`; weicht er
  ab (inzwischen rotiert oder widerrufen), 401 ohne jede Schreibung. Setzen und Widerrufen des Geheimnisses schließen im
  selben Commit einen offenen Lauf (Laufspalten leer, Quittung `run_revoked`, zählt für keinen Alarm), sodass kein Ergebnis
  aus der alten Generation mehr trifft.
- **Beanspruchen, vor jedem Browserstart (eine Transaktion, Zeilensperre):** ein offener Lauf mit
  abgelaufener Lease wird als `run_interrupted` gebucht; dann neue `RenewalRunId`, `RenewalRunStartedAtUtc
  = jetzt`, `RenewalRunRequestedAtUtc = RenewalRequestedAtUtc`, `RenewalRunCredentialVersion`,
  `RenewalRunKillSwitchVersion`, `RenewalFailureCount + 1` und `NextRenewalAttemptAtUtc` nach dem Rückzug
  1, 2, 4, 8, dann alle 12 Stunden. Ein Absturz mitten im Lauf zählt damit als Fehlversuch; erst ein
  Erfolg setzt Zähler und nächsten Versuch zurück. Der Auftrag trägt nur Laufkennung und Bot-ID.
- **Lauf am Erneuerer:** frischer, nicht persistenter Browserkontext mit den Cookies samt Attributen;
  Aufruf der 7TV-Website und der Login-URL, **keine Klicks, keine Eingaben**; Erfolg, sobald auf
  `7tv.app` ein Token erscheint (Ausweichweg nach G0b: Antwort von `/v4/auth/login/finish`);
  Gesamtbudget 90 s, danach wird der Browser samt Prozessbaum beendet. Anfragen nur an die Host-Liste aus
  G0b, Bilder, Medien und Schriften abgebrochen; kein Trace, kein HAR, kein Screenshot.
- **Prüfung am Erneuerer, alles muss gelten:** Form wie AK73, `exp` 29 bis 31 Tage nach `iat`, `sub` =
  Bot-ID des Auftrags, `users.me` mit dem neuen Token liefert dieselbe ID.
- **Cookies zurückschreiben, dann melden:** nur nach einem Lauf mit gültigem JWT, nur wenn die Datei
  seit Laufbeginn unverändert ist und kein Eingang liegt (18.2, die frische Datei gewinnt); atomar (temporäre Datei, dann
  Umbenennen), nur die Pflichtnamen mit ihren Attributen, Verkürzung und Löschung übernommen, Ablauf nach 18.2. Erst
  danach geht das JWT an die Api: so verliert ein Abbruch zwischen beiden Schritten nie die rotierte
  Sitzung. Fehlt danach ein Pflicht-Cookie, wird das JWT trotzdem gemeldet, **zusammen** mit dem Sitzungszustand `lost`
  (getrennte Felder, 18.5). Nach einem Fehlschlag bleibt die Datei unverändert.
- **Zaun vor jeder Wirkung (Revision 5, Sol P1):** Unter der Zeilensperre wirkt ein Ergebnis nur, wenn `RenewalRunId` =
  gemeldete Kennung **und** die Lease noch läuft (`jetzt < RenewalRunStartedAtUtc + 5 min`). Sonst ist es wirkungslos (kein
  Tausch, keine Buchung, kein Zähler, kein Audit, das JWT verworfen): gleicht die Kennung `LastRenewalRunId`, antwortet die
  Api 204 (Wiederholung nach verlorener Antwort, die Quittung `LastRenewalRunOutcome` steht schon), sonst 409 ohne Körper.
  Jede wirksame Buchung schreibt `LastRenewalRunId` und `LastRenewalRunOutcome` und leert die Laufspalten im selben Commit;
  das Schließen nach abgelaufener Lease beim nächsten `tick` ebenso (`run_interrupted`).
- **Tausch an der Api (Ergebnisroute, eine Transaktion, Zeile `FOR UPDATE`), nach dem Zaun, alles muss gelten:**
  `CredentialVersion = RenewalRunCredentialVersion` (damit auch
  `IdentityVersion` unverändert, denn jede Erhöhung von `IdentityVersion` erhöht `CredentialVersion` mit),
  `KillSwitchVersion = RenewalRunKillSwitchVersion` und `KillSwitch = false` (ein zwischenzeitlich an- und
  wieder ausgeschalteter Notaus fällt so auf), `Status IN (valid, expired)` (ein inzwischen gebuchtes 401
  bleibt `invalid`), Form, `exp` 29 bis 31 Tage nach `iat` und nach dem gespeicherten `ExpiresAtUtc`, `sub`
  = `BotSevenTvUserId`, `TokenKeyFingerprint` = Fingerprint des eigenen öffentlichen Schlüssels. Dann
  versiegeln; `SealedToken`, `ExpiresAtUtc`, `SetAtUtc = jetzt`, `Status = valid`, `LastErrorCode = null`, `LastVerifiedAtUtc =
  null` (der Worker prüft das neue Token im nächsten Takt; `Status` bleibt `valid`, bis eine Prüfung
  scheitert), `CredentialVersion + 1`, `IdentityVersion` bleibt; Audit `botAccount.renewed` (alter und neuer
  Ablauf); Ergebnis: Zähler 0, `NextRenewalAttemptAtUtc` und `RenewalRunId` leer,
  `LastRenewalSucceededAtUtc`, `LastRenewalErrorCode = null`; der gemeldete Sitzungszustand und -ablauf werden im selben
  Commit gebucht (ein `lost` bleibt so auch dann stehen, wenn der Erneuerer danach verstummt); **Quittung:** `RenewalRequestedAtUtc` wird
  nur geleert, wenn es gleich `RenewalRunRequestedAtUtc` ist (ein späterer Klick bleibt offen). Commit,
  dann `BOTACCOUNT:`. Trifft eine Bedingung nicht, wird das JWT verworfen (nie gespeichert) und
  `swap_rejected` gebucht. Klartext des JWT existiert in der Api nur während dieser Anfrage.
- **Fehlerklassen, nur über positive Merkmale** (Merkmal je Seite aus G0b, im Protokoll und im Code als
  Tabelle): **eindeutig**, sofortiger Alarm: `twitch_login_required` (Anmeldeformular von Twitch),
  `consent_required` (Zustimmungsseite mit Freigabeknopf), `challenge_required` (Captcha oder
  Gerätebestätigung), `identity_mismatch`, `key_mismatch`
  (Tausch); **Revision 5:** `twitch_session_lost` und `session_malformed` sind keine Laufergebnisse mehr, sondern die
  Sitzungszustände `lost` und `malformed` (Alarm über 18.7, unabhängig vom Ergebnis der Erneuerung); **vorübergehend**, Alarm nach drei in Folge: `navigation_timeout` (Budget erschöpft ohne
  Merkmal), `callback_without_token` (Callback von 7TV erreicht, kein Token), `unexpected_page` (geladene
  Seite ohne bekanntes Merkmal), `unavailable` (Netz, DNS, 5xx), `run_interrupted`, `swap_rejected` (außer
  den beiden eindeutigen Gründen). Eine Adresse allein oder eine Wartezeit entscheidet nie auf
  „eindeutig“. Jedes Ergebnis lässt das gespeicherte Token unverändert. „Jetzt erneuern“ wird bei Erfolg
  und bei eindeutigem Fehler quittiert; neue Cookies setzen nichts zurück, der Betreiber stößt danach mit
  „Jetzt erneuern“ an. Audit `botAccount.renewalFailed` nur beim Wechsel in den Alarmzustand (18.7).
- **Kein Rückfall durch die Erneuerung:** Die Erneuerung wechselt das Token, nicht das Konto. Der
  Editor-Nachweis je Kanal bleibt über einen Tausch gültig, weil er an `IdentityVersion` und Bot-ID hängt
  (Plan-487 R2-5 bleibt für Hand-`PUT` wirksam). **Gebucht** wird ein Editor-Ergebnis aus dem
  unauthentifizierten `editor_of` (`SevenTvApiClient.cs:466-475`, nur die 7TV-ID) auf `IdentityVersion` und
  Bot-ID; wählt G0 einen authentifizierten Editor-Read, zusätzlich auf die `CredentialVersion` des
  lesenden Tokens, solange nicht belegt ist, dass das Ergebnis vom Token unabhängig ist. **G0 (2026-11-24):**
  gewählt ist der unauthentifizierte v4-Read `user(Bot).editorFor` (K4 „Einladungen“); die Buchung bleibt
  damit an `IdentityVersion` und Bot-ID, ohne `CredentialVersion`. Ein Tausch lässt
  keinen Kanal in den Rückfall fallen und löst keinen Rückfall-Lauf aus.
- **429 gilt dem Konto:** die Pause nach 429 wird auf `IdentityVersion` gebucht, nicht auf
  `CredentialVersion`, und übersteht damit einen Tausch.
- **Rückfall bleibt der Normalpfad:** Gelingt bis zum `exp` keine Erneuerung, setzt der Worker
  `expired`, und alle Bot-Kanäle laufen im manuellen Ablauf (Abschnitt 7 Punkt 8). Die Handerneuerung über
  `PUT /token` bleibt als Notweg.

### 18.5 Maschinenroute [B]

- **Nur ausgehend, Erneuerer → Api über HTTPS** am öffentlichen Hostnamen; der VPS verbindet sich nie
  ins Heimnetz. Gruppe `/api/internal/bot-account/renewal`: `POST tick` (Körper: Sitzungszustand `valid`,
  `malformed` oder `lost`, gemeldeter Sitzungsablauf, Version des Erneuerers; Antwort 204 oder 200 mit
  `runId` und Bot-ID) und `POST runs/{runId}` (Körper: das JWT **oder** ein Fehlercode, **getrennt davon** Sitzungszustand
  und -ablauf, Revision 5; Antwort 204 oder 409 ohne Körper nach dem Zaun in 18.4). Keine Route liefert Token, Siegel, Geheimnis oder
  Cookies; die Körper enthalten außer dem JWT nur Codes und Zeitpunkte.
- **Anmeldung:** eigener `IEndpointFilter`, Kopf `Authorization: Bearer <Maschinengeheimnis>`. Die Api
  hält nur `RenewerSecretHash` (SHA-256; das Geheimnis sind 32 zufällige Byte, ein langsamer Hash bringt
  nichts) und vergleicht in konstanter Zeit. Keine Cookie- oder Sitzungsanmeldung: eine Anfrage mit
  gültigem Sitzungs-Cookie und ohne Kopf → 401. Die Api hat keine CORS-Middleware (`Program.cs`); die
  Gruppe bekommt keine. 401 ohne Körper; ein Fehlversuch schreibt eine Logzeile ohne Kopfwert, kein
  Audit je Versuch. Der Filter ist die billige Vorprüfung; maßgeblich ist die erneute Prüfung unter der Zeilensperre
  (18.4), sodass ein Widerruf auch eine schon angemeldete, auf die Sperre wartende Anfrage trifft.
- **Geheimnis:** vom Betreiber lokal erzeugt, auf der Admin-Seite gesetzt (`PUT /api/admin/bot-account/
  renewer-secret`, Passwortfeld, die Api speichert nur den Hash, Audit `botAccount.renewerSecretSet`) und
  dort widerrufbar (`DELETE`, Audit). Rotation = neues setzen; das alte gilt sofort nicht mehr [E] (ein
  ausgelassener Takt ist harmlos); beides schließt einen offenen Lauf (18.4).
- **Rate-Limit [E] (Revision 5, Sol P1):** Die Rate-Limit-Middleware läuft vor jedem Endpoint-Filter
  (`Program.cs`: `UseRateLimiter` vor den Routen); ein gemeinsamer Topf wäre also von unangemeldetem Verkehr leerzuräumen.
  Deshalb zwei Töpfe in einer Policy: Der Partitionierer vergleicht den SHA-256 des vorgelegten Bearer-Werts in konstanter
  Zeit mit einem In-Process-Halter des aktuellen Hashes (befüllt beim Start und bei jeder Änderung über die Admin-Routen;
  die Api läuft als eine Instanz). Treffer → eigener, reservierter Topf (30 je Minute); alles andere → je Quell-IP wie
  `PublicHealth` (10 je Minute). Die Einordnung entscheidet nur über den Topf, nie über die Anmeldung.
- **Schalter:** 404 ohne Körper, sobald `Wishes:Enabled` (heute die einzige Nutzung des Bot-Kontos), `BotAccount:Enabled` oder
  `BotAccount:Renewal:Enabled` aus ist. Der Erneuerer hängt damit an allen Schaltern.
- **Lebenszeichen:** jeder `tick` schreibt `RenewerSeenAtUtc` und `TwitchSessionExpiresAtUtc` (leer, wenn
  keine gültige Datei vorliegt).

### 18.6 Erneuerer-Host und Isolation [B]

- **Projekt `EmotePurge.BotAccountRenewer`** nur mit Core und Microsoft.Playwright (nur
  Chromium-Headless-Shell): keine Datenbank, kein Redis, kein Siegel-Schlüssel. Eigenes GHCR-Image.
- **Container:** eigenes Docker-Netz ohne Route ins LAN (nur Internet; private Adressbereiche am Host
  gesperrt), Nicht-Root, `read_only` mit `tmpfs` für `/tmp` und das Browserprofil, `cap_drop: ALL`,
  `no-new-privileges`, Speicherlimit 1 GB, `pids`-Limit; einziges beschreibbares Volume ist das mit der
  Cookie-Datei.
- **Chromium-Sandbox an** (Playwrights Default ist aus). Im Container braucht sie ein seccomp-Profil, das
  Namensräume zulässt; G0b prüft das. Läuft sie nicht, ist `--no-sandbox` nur als benannte
  DECISIONS-Entscheidung mit Begründung zulässig.
- **Browser mit leerer Umgebung** (nur `HOME` und `TMPDIR`; Playwrights Default ist die Umgebung des
  Prozesses); das Maschinengeheimnis kennt nur der .NET-Prozess. Eine Trennung von Browser und Koordinator
  in Prozesse oder Container ist nicht nötig: am Host liegen weder Datenbankzugang noch Schlüssel.
  Restrisiko: ein vollständig kompromittierter Browserprozess desselben Nutzers kann das Geheimnis lesen
  (RR17).
- **Ressourcen:** Planwert Spitze 500 bis 700 MB je Lauf, idle nur der .NET-Dienst; Limit 1 GB; Playwright
  je Lauf neu, danach entsorgt; glibc-Basis (Debian oder Ubuntu wie das Playwright-Image), nicht Alpine. Gemessen wird der
  anonyme Speicher aus `memory.stat` der cgroup mit im Image vorinstalliertem Browser, nicht `memory.peak` (zählt Seitencache
  und `tmpfs` mit, Probe 3); das `tmpfs` ist klein bemessen, weil es auf dasselbe Limit zählt. Der VPS trägt nichts davon.
- **Pflege:** monatlicher Neubau dieses Images per Zeitplan; Microsoft.Playwright kommt als eigener
  Dependabot-PR (ein Sprung wechselt den Browser und braucht einen Live-Kurzlauf).
- **Verworfen:** (a) Chromium im Worker-Image (512-MB-Limit, Chat-Erfassung und Token-Schlüssel daneben);
  (b) eigener Dienst auf dem VPS (vom Betreiber am 2026-11-20 zunächst gewählt und am selben Tag ersetzt:
  die Sitzung läge neben Prod-Datenbank und Backup und bräuchte eigene Rolle, Prozess-Abtrennung und
  Backup-Ausschluss; der VPS hat 3,7 GiB ohne Swap); (c) CI-Zeitplan (Sitzung bei einem Dritten).

### 18.7 Alarmierung ohne Anwesenheit [B]

`GET /api/health` bleibt unverändert und wird durch den Bot-Zustand **nie** 503 (DECISIONS 2026-08-05).
Ein zweiter payloadfreier Endpunkt `GET /api/health/bot-account`, anonym, Policy wie `PublicHealth`, ist ein
eigener Monitor in Uptime Kuma (Telegram, Meldung täglich wiederholt). 404, wenn `BotAccount:Enabled`
aus ist; dann pausiert der Betreiber den Monitor (404 wird nicht als „gesund“ eingetragen). Sonst 200,
außer ein Alarmgrund gilt, dann 503 (Zeilen ab „Totmann“ nur bei `Renewal:Enabled`):

| Grund | Schwelle |
|---|---|
| 7TV-JWT läuft ab | `ExpiresAtUtc − jetzt < 3 Tage`, oder `Status` `invalid`/`expired` |
| Token ungeprüft | `Status = unverified` oder (Revision 5) `LastVerifiedCredentialVersion ≠ CredentialVersion`, jeweils länger als 15 Minuten nach `SetAtUtc` (auch nach einem Tausch: die Api allein beweist nicht, dass der Worker das neue Token benutzt) |
| Totmann | fällig seit über 24 h ohne Erfolg; „fällig seit“ = früherer von `ExpiresAtUtc − LeadDays` und `RenewalRequestedAtUtc` |
| Erneuerung gescheitert | letztes Ergebnis eindeutig, oder drei vorübergehende in Folge |
| Twitch-Sitzung endet | gemeldeter Ablauf `− jetzt < 30 Tage`, oder `TwitchSessionState` `malformed` oder `lost` (Revision 5) |
| Erneuerer still | `RenewerSeenAtUtc` älter als `Renewal:SilenceAlertHours` (Default 26); leer zählt, sobald `RenewerSecretSetAtUtc` länger als dieselbe Frist zurückliegt |

Kein Alarm bei `Status = none` (Token bewusst gelöscht) und bei gesetztem `KillSwitch` (bewusster Notaus).
Der Grund steht nur auf der Admin-Seite. **Zustellung gilt erst als eingerichtet**, wenn eine Testmeldung
des echten Monitors und die Entwarnung beim Betreiber angekommen sind (Abschnitt 12, Punkt 7). Es gibt keine
Vertretung ([B] N1, 18.1); Zustellnachweise gelten nur für den Betreiber.

**Ausfall des Beobachters (Revision 5, Sol P2):** Kuma läuft beim Betreiber auf demselben NAS wie der Erneuerer. Fällt das
NAS oder sein Uplink aus, sieht Kuma weder das 503 noch kann es Telegram senden. Diesen Fall deckt der bestehende,
unabhängige Heartbeat von Kuma bei healthchecks.io mit E-Mail-Alarm (DECISIONS 2026-08-05, S3-36). Er ist Teil von Stufe 0
(Abschnitt 12, Punkt 7); `docs/Operations.md` verlangt generisch: Läuft der Monitor auf dem Erneuerer-Host, braucht er
selbst einen unabhängigen Heartbeat mit eigenem Zustellweg.

Verworfen: Alarm über `/api/health`, Admin-Route mit Schlüsselwort-Monitor, Telegram direkt aus der App.

### 18.8 Notaus, neue Reihenfolge (ersetzt die in Abschnitt 14) [B]

Ziel: Weder der Erneuerer noch ein Dieb der Cookies kann nach dem Notaus ein neues JWT holen oder einspielen.

Revision 5 (Sol P2): serverseitige Sperren zuerst, unabhängig davon, ob der Erneuerer-Host erreichbar ist; die zweite
Abmeldung bei 7TV gehört in den Vorfall, nicht in den Wiederanlauf.

1. **`KillSwitch` an** (Admin-Seite): `KillSwitchVersion + 1`; die Api vergibt keinen Auftrag und lehnt
   jeden Tausch ab, der Worker schreibt nicht mehr.
2. **Maschinengeheimnis widerrufen** (Admin-Seite, auch bei ausgeschalteten Schaltern erreichbar): schließt einen offenen
   Lauf; jeder spätere `tick` und jedes Ergebnis endet mit 401 (18.4).
3. **Erneuerer-Container am Host stoppen** (Betreiber), wenn der Host erreichbar ist. Ist er es nicht, geht es ohne
   Warten mit Schritt 4 weiter; Container stoppen und Cookie-Datei löschen folgen, sobald er erreichbar ist, vor jedem
   Neustart des Containers.
4. **Twitch-Sitzungen des Bot-Kontos beenden:** Passwort ändern, „von allen Geräten abmelden“, unter
   Verbindungen fremde App-Freigaben prüfen und entfernen (die von 7TV bleibt).
5. **„Sign Out Everywhere“ bei 7TV:** entwertet alle Bot-JWTs.
6. **Abklingen, dann zweites „Sign Out Everywhere“ [E]:** frühestens 15 Minuten nach Schritt 4, noch im Vorfall.
   Restfenster dazwischen: ein Twitch-Code, den ein Dieb vor Schritt 4 erhalten hat und erst nach Schritt 5 einlöst. Erst nach
   diesem Schritt gilt der Vorfall als eingedämmt, und erst danach werden neue Zugänge ausgestellt.
7. **`DELETE /token`** (erreichbar auch bei ausgeschalteten Schaltern, 14) und die Cookie-Datei am Host
   löschen (bei unerreichbarem Host nach Schritt 3). Danach dürfen die Schalter aus.

**Belegt am 2026-11-24 (Probe T0, AK90):** Nach „Sign Out Everywhere“ antwortet 7TV dem gespeicherten JWT mit echtem HTTP 401,
und ein Headless-Login aus der gespeicherten Twitch-Sitzung holt unmittelbar danach ein neues gültiges JWT. Schritt 5 allein
stoppt den Erneuerer also nicht; die Reihenfolge oben ist nötig.

Wiederanlauf, erst nach Schritt 6: neue Sitzung über den Helfer (18.2), Datei in den Eingang, neues Maschinengeheimnis,
neues Token per `PUT`, Notaus aus, „Jetzt erneuern“ als Probe.

**Restore (Revision 5, Sol P1):** Ein Dump von vor einem Notaus oder Widerruf brächte alten Geheimnis-Hash, Token und einen
ausgeschalteten Notaus zurück, und beide Restore-Wege in `docs/Operations.md` starten Api und Worker direkt nach dem
Einspielen. Deshalb läuft in **beiden** Wegen vor dem Start ein fester SQL-Schritt: `KillSwitch` an (`KillSwitchVersion +
1`), Geheimnis-Hash und -Zeitpunkt, Laufspalten, `RenewalRequestedAtUtc` und `TwitchSessionState` leer. Danach bleiben
Maschinenroute (kein Hash → 401) und Bot-Schreiben (Notaus) zu, bis der Betreiber geprüft hat, ob nach dem Dump ein Notaus
oder Widerruf lag (dann `DELETE /token` und Wiederanlauf wie oben), das Token vom Worker nach dem Restore geprüft ist und ein
neues Geheimnis gesetzt ist; erst dann Notaus aus.

### 18.9 Inoffiziell

DECISIONS ergänzt den Satz aus Abschnitt 7 um „automated re-login via headless browser with the bot
account's Twitch session, on a host outside the internet-facing server; unofficial for both Twitch and
7TV; may break at any change“. Das widerspricht dem Eintrag „Kein Third-Party-Login bei 7TV“
(2026-09-01) nicht: `return_to` bleibt auf dem Ursprung von 7tv.app, der Erneuerer bedient die eigene
Website von 7TV als Bot-Konto. Ein Bruch endet als Fehlerklasse, Alarm und schließlich Rückfall, nie als
Schreiben ohne gültiges Token.

### 18.10 Akzeptanzkriterien (fortlaufend nach AK108)

109. **AK109** Maschinenroute: ohne, mit falschem oder widerrufenem Geheimnis → 401 ohne Körper; mit
     gültigem Sitzungs-Cookie und ohne Kopf → 401; einer der drei Schalter aus → 404; Rate-Limit greift;
     ist der unangemeldete Topf erschöpft, gelingen ein gültiger `tick` und ein gültiges Ergebnis trotzdem (Revision 5);
     Rotation und Widerruf, während eine angemeldete Anfrage auf die Zeilensperre wartet → 401 ohne Schreibung;
     keine CORS-Kopfzeilen. `PUT`/`DELETE /api/admin/bot-account/renewer-secret`: kein Admin → 403, nur der
     Hash wird gespeichert, keine Antwort enthält das Geheimnis, Audit ohne Wert.
110. **AK110** Trennung: das Erneuerer-Projekt referenziert nur Core (Strukturtest wie
     `CoreAssemblyReferenceTests`) und kennt weder Connection-String noch Schlüssel; weder Api noch Worker
     kennen Cookies; keine Route liefert Token, Siegel oder Geheimnis.
111. **AK111** Tausch: Erfolg nach 18.4 mit `CredentialVersion + 1`, unveränderter `IdentityVersion`,
     kein Kanal nach `fallback`, kein Rückfall-Lauf, keine Einreihung verloren. Kein Tausch, wenn während
     des Laufs `PUT /token`, `DELETE /token`, Notaus an, Notaus an und wieder aus, ein 401 (`invalid`) oder
     ein neuer Lauf dazwischen kam, oder der Fingerprint abweicht (`key_mismatch`); `invalid` bleibt nach
     `exp` `invalid`. **Zaun (Revision 5):** ein Ergebnis nach abgelaufener Lease (auch vor dem nächsten `tick`) oder mit
     fremder Kennung ist wirkungslos; die Wiederholung eines gebuchten Ergebnisses (verlorene Antwort) antwortet 204 und
     ändert nichts, auch nicht nach einem Erfolg; ein JWT mit Sitzungszustand `lost` wird getauscht, und der Alarm „Twitch-
     Sitzung endet“ steht im selben Commit, auch wenn der Erneuerer danach verstummt.
112. **AK112** Fehlerklassen: jede Klasse aus 18.4 an einer bereinigten Testseite nur über ihr positives
     Merkmal; eine langsame Zustimmungsseite ohne Merkmal ist `navigation_timeout`, nicht
     `consent_required`; ohne Klick, binnen 90 s, Browser samt Prozessbaum beendet; das gespeicherte Token
     bleibt.
113. **AK113** Cookies am Host: zurückgeschrieben nur nach gültigem JWT, unveränderter Datei und leerem Eingang, vor der
     Meldung an die Api, atomar, mit erhaltenen Attributen; Verkürzung und Löschung sofort, Verlängerung erst
     nach belegtem Gleiten; ein Fehlschlag lässt die Datei unverändert. Eine Eingangsdatei, die unmittelbar vor dem
     Zurückschreiben erscheint, gewinnt; übernommen wird sie nur formgültig, außerhalb eines Laufs und nach einer Minute Ruhe.
114. **AK114** `GET /api/health/bot-account` ist payloadfrei und folgt der Tabelle in 18.7 (je Grund ein Fall
     mit eigenem Ausgangszustand, `none` und `KillSwitch` → 200, Schalter aus → 404). `GET /api/health`
     antwortet in jedem dieser Fälle wie ohne Bot.
115. **AK115** Notaus: mit `KillSwitch` kein Auftrag und kein Tausch; der Ablauf 18.8 (Widerruf vor dem Host, zweite
     Abmeldung im Vorfall, Weg bei unerreichbarem Host) steht im Runbook; der Live-Lauf zeigt, dass nach dem Beenden der
     Twitch-Sitzungen ein Lauf mit `twitch_login_required` endet.
116. **AK116** G0b (vor T1, am Erneuerer-Host, Betreiber führt aus): Login über den Helfer-Prototyp (von Hand gestarteter Browser mit frischem
     Datenverzeichnis, Anhängen per CDP; R0 am 2026-11-24 bestanden) gelingt; kleinste funktionierende Cookie-Menge
     samt Attributen; zwei Läufe mit der jeweils zurückgeschriebenen Datei; Gleiten; das alte JWT bleibt
     nach einem neuen Login gültig (Überlappung); 7TV duldet drei bis vier gleichzeitig gültige Sitzungen;
     positive Merkmale je Seite; Host-Liste; Sandbox im Container; Spitzen-Speicher; ein ungültiger
     `auth-token` endet als `twitch_login_required`; widerrufene Verbindung → Zustimmungsseite; nach „Sign
     Out Everywhere“ holt ein Lauf ein neues JWT. Ergebnis in DECISIONS. Der Vorlauf (Probe 3, 18.1) belegt den Login
     vom Host aus mit Isolation; Probe 4 (2026-11-24) belegt R0 und R2 (Überlappung, vier parallele Sitzungen); offen sind Sandbox, Speicher, R1 (Gleiten),
     R3 und die kleinste Menge.
117. **AK117** Ressourcen: Image-Größe, anonymer Speicher idle und Spitze (`memory.stat`, Browser im Image
     vorinstalliert), Dauer gemessen und im PR genannt; Limit gesetzt; Worker-Image und Worker-Limit unverändert.
118. **AK118** DECISIONS (Vertrauensgrenze 18.3, Ort, „unofficial for both Twitch and 7TV“) im selben
     Commit wie die Maschinenroute (Regel 3); im selben PR das Operations-Runbook (generisch: Host-Wahl,
     Isolation, Cookie-Datei mit dem Helfer gewinnen und über den Eingang ablegen, Geheimnis, Kuma-Monitor samt
     unabhängigem Heartbeat, Notaus 18.8, Wiederanmeldung, Restore-Schritt in beiden Wegen) und
     Architectur.md (Erneuerer außerhalb des Stacks).
119. **AK119** Lauf-Buchhaltung: der Fehlversuch ist vor dem Browserstart gebucht; ein Lauf ohne
     Ergebnis wird nach der Lease `run_interrupted`; zwei Instanzen bekommen nie gleichzeitig einen
     Auftrag; „Jetzt erneuern“ wird genau einmal quittiert (Klick, zwei weitere Takte ohne Lauf; zweiter
     Klick während eines Laufs bleibt offen); Totmann nach 24 h; Quittung `LastRenewalRunId`/`LastRenewalRunOutcome` je
     wirksam abgeschlossenem Lauf, auch für `run_interrupted` und `run_revoked`.
120. **AK120** Freigabe-Stufe 0 (Abschnitt 12, Punkt 7) ist durchlaufen und protokolliert: Test- und Entwarnungsmeldung
     beim Betreiber zugestellt; bei angehaltenem Kuma (NAS oder Uplink unterbrochen) hat der unabhängige Heartbeat den
     Betreiber per E-Mail erreicht, samt Entwarnung; nach „Jetzt erneuern“ hat der Worker binnen 15 Minuten genau die neue
     `CredentialVersion` geprüft (`LastVerifiedCredentialVersion`); ein Tausch allein lässt das Tor offen.
121. **AK121** Helfer (Revision 5, Weg seit 2026-11-24): hängt sich per CDP an einen vom Betreiber von Hand gestarteten Browser
     mit frischem Datenverzeichnis (startet selbst nie einen), prüft die 7TV-Anmeldung, schreibt nur die Pflichtnamen unter
     `twitch.tv` mit allen Attributen und Rechten 600, nie das 7TV-JWT, nichts bei gescheiterter Formprüfung; dieselbe
     Formprüfung wie der Erneuerer (Test); der Browser ist danach geschlossen und das Datenverzeichnis gelöscht (Betreiberschritt
     im Runbook); das Erneuerer-Image macht nie einen Passwort-Login.
122. **AK122** Restore (Revision 5): ein lokal eingespielter Dump von vor einem Widerruf mit dem SQL-Schritt aus 18.8
     → altes Geheimnis 401, Notaus an, Worker schreibt nichts, bis Geheimnis und Prüfung neu sind; beide Restore-Wege im
     Runbook enthalten den Schritt vor dem Start von Api und Worker.

### 18.11 Entschieden (Betreiber, 2026-11-20)

- **[FT1] Wo läuft der Browser?** [B] Außerhalb des Internet-Servers, beim Betreiber auf dem NAS (18.2,
  18.6). Ersetzt den ersten Entscheid desselben Tages (eigener Container auf dem VPS).
- **[FT2] Alarm?** [B] Eigener payloadfreier Endpunkt, ergänzt um Totmann, Startfrist des
  Lebenszeichens und Zustellnachweis (18.7).
- **[FT3] Eigene Datenbankrolle?** [B] Entfällt: der Erneuerer hat keinen Datenbankzugang; die
  Maschinenroute führt an der Api nur die fest definierten Operationen aus (18.5).
- **[FT4] PR-Schnitt:** [B] alle Spalten in `AddBotAccount` (PR 1), weil G0b vor T1 liegt; [E] Maschinenroute,
  Alarm-Endpunkt und Image in PR 3 (Plan-487, Abschnitt 9).
- Alle Befunde der drei Reviews sind übernommen (Plan-487, Tabelle „Revision 4“).
- **Abends, nach Sol über Revision 4 [B]:** alle neun Befunde übernommen (Plan-487, Tabelle „Revision 5“); Cookie-Datei über
  den Helfer statt aus einer Profilkopie (18.2); N1 und N2 entschieden (18.12). Revision 5 ist der Endstand des Nachtrags;
  weitere Prüfung nur noch als Code-Review je PR.

### 18.12 Betreiberfragen des Nachtrags (entschieden 2026-11-20)

- **[N1] Vertretung?** [B] Es gibt **keine** Vertretung. Ist der Betreiber nicht erreichbar und klemmt etwas, entsteht kein
  neues Token; der Bot fällt beim Ablauf sicher in den manuellen Ablauf. Alle Stellen, die eine benannte Vertretung oder
  eine Zustellung an sie verlangten (12, 18.7, AK120), sind gestrichen; Zustellnachweise gelten nur für den Betreiber. Die
  Grenze steht in 18.1.
- **[N2] Gefälschtes JWT über das Maschinengeheimnis (RR17)?** [B] Hingenommen als bekanntes Risiko, kein Schritt mit
  einem vorläufigen Token. Ein Dieb der Cookies kann ohnehin echte JWTs ausstellen; das Geheimnis liegt am selben Host; der
  Fall endet mit Alarm und einem Hand-`PUT`.

---

# K1: Backend-Kern

Labels: `feat`, `api`, `infrastructure`, `migration`. Teil des Epics „Wunsch-Emotes mit Ablauf“.

## Ziel

Alles, was die anderen Kinder als Vertrag brauchen: Tabellen, Zustandsmaschine, Verwalter-API,
Abgleich mit Slots, Live-Event, Retention, Schalter. Hinter `Wishes:Enabled`, ohne Oberfläche.

## Umfang

Enthalten: Abschnitte 5, 6.1 bis 6.3, 6.5 dieser Spec; Schalter `Wishes:Enabled`.
Nicht enthalten: der Zustand `offen` und der Treffer über den Namen (K6), die Pilot-Liste (K4), Ablauf-Lauf (K2), jede 7TV-Schreibaktion (K4),
Bot-Felder werden angelegt, aber von nichts gesetzt.

## Implementation Details

**Core** (`src/EmotePurge.Core/`): `Entities/Wish.cs`, `Entities/ChannelWishSettings.cs`,
`Entities/WishStatus.cs` (Konstanten wie `AuditActions`, keine Enum-Zahlen), `Services/IWishService.cs`
(Befehle und Lesen je Kanal), `Services/IWishOverviewService.cs` (kanalübergreifend),
`Services/IWishReconcileService.cs`, `Services/WishSlotMatcher.cs` (die Zuordnungsregel aus 6.3, rein), `Services/WishTransitions.cs` (reine Tabelle „von, Aktion →
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
(404 `wishes_disabled`). Diese Reihenfolge ist der
Fehlervertrag: 400 vor 403 vor 404.

| Route | Rate-Limit | Zweck |
|---|---|---|
| `GET /` `?view=open\|running\|ended&page&pageSize` | `InteractiveRead` | Liste; `open` = Handlungsbedarf, `running` = `active` und `unconfirmed`, `ended` = Endzustände |
| `GET /{id}` | `InteractiveRead` | Detail mit `usage` (E4, E16) |
| `POST /` | `Bookkeeping` | Handeintrag |
| `POST /adopt` | `Bookkeeping` | Bestand übernehmen |
| `PATCH /{id}` | `Bookkeeping` | Alias, Laufzeit; nur in `approved`, `failed` (ab K6 auch `pending`); in `unconfirmed` 409, weil der Bot schon geschrieben hat |
| `POST /{id}/extend`, `/keep`, `/close`, `/retry`, `/re-add`, `/complete-removal`, `/recheck` | `Bookkeeping` | Übergänge aus Abschnitt 5 |
| `GET /settings` | `InteractiveRead` | Kanal-Einstellungen mit Defaults |
| `PUT /settings` | `Bookkeeping`, zusätzlich `WishSettingsAuthorizationFilter` | im manuellen Modus jeder Verwalter; im Bot-Modus nur Broadcaster, Aktivierender, globaler Admin, sonst 403 `wish_settings_restricted` |

Kanalübergreifend, Gruppe `/api/wishes` (`RequireAuthorization`, `InteractiveRead`):
`GET /mine` (Handlungsbedarf und Wünsche in Probezeit über alle verwaltbaren Kanäle, sortiert nach
`ExpiresAtUtc`, dann `CreatedAtUtc`) und `GET /summary` (`{ "actionCount": 3 }`), der leichte
Zähl-Endpunkt für die Kopfzeile (E18). Beide gehen **nicht** über `IMyChannelsService` und nicht
über `/api/channels/mine`. Sie beginnen bei den Kanälen, die überhaupt Wünsche mit Handlungsbedarf
oder in Probezeit haben (eine Indexabfrage), und prüfen nur für diese das Verwaltungsrecht über
`IChannelAccessService.CanManageChannelAsync` (Broadcaster, Moderator, globaler Admin; nicht
7TV-Editor allein). Hat kein Kanal einen solchen Wunsch, antwortet `summary` ohne jede
Rollenprüfung mit 0. Was eine Rollenprüfung bei kaltem Cache an Helix-Aufrufen kostet, ist in K1
an `ChannelAccessService` und `ModRoleCache` nachzulesen und im PR zu nennen; der Vertrag steht in
AK15.

Formen (camelCase, Zeiten als ISO-8601 UTC):

```
POST /api/channels/{c}/wishes
{ "sevenTvEmoteId": "01H…", "emoteName": "…", "alias": "…",
  "durationDays": 30, "requesterLogin": null }
→ 201 WishDto

POST …/wishes/adopt        { "slotId": "…", "wishId": null, "durationDays": 30, "startsAtUtc": null }
POST …/{id}/re-add         { "mode": "timed" | "permanent", "durationDays": 30 }
PUT  …/wishes/settings     { "defaultDurationDays": 30, "probationLeadDays": 7,
                             "protectionThresholdPerDay": null }

WishDto { id, sevenTvEmoteId, emoteName, alias, source, status, statusReason,
          requesterLogin, durationDays, createdAtUtc, approvedAtUtc, startsAtUtc,
          startsAtAuthoritative, probationStartsAtUtc, expiresAtUtc, endedAtUtc,
          slotId, linkOrigin, extensionCount, permanent, botOwned,
          botWritePending, botNextAttemptAtUtc, removalQueuedAtUtc,
          mode: "manual" | "bot" }
WishDetailDto = WishDto + usage { windowFromUtc, windowToUtc, total, perDayMean,
                                  rank, rankOf, coverageGap }
```

`requesterTwitchUserId` verlässt die Api nie. `botWritePending` ist wahr, solange `BotWriteAtUtc`
gesetzt und der Wunsch noch nicht `active` ist; `removalQueuedAtUtc` zeigt der Karte „wird
entfernt“. Beide Felder und `botNextAttemptAtUtc` sind bis K4 immer leer. **Tagesnutzung steht
nicht im DTO:** Die Sparkline der Karte liest den bestehenden Endpunkt
`GET /api/channels/{c}/usage-stats/daily?slotId=<slotId>&from=<Tag von startsAtUtc>&to=<heute>`;
ohne `slotId` zeigt die Karte keine Sparkline. `adopt` ohne `startsAtUtc` nimmt `AddedAt` des Slots;
ein gesetzter Wert darf nicht in der Zukunft und nicht vor `AddedAt` liegen.

Neue `ApiErrorCodes` (Regel 7: je Code `api-error.ts` und `errors.api.*` in de/en; die
Frontend-Hälfte liefert K3, K1 legt die Codes an und dokumentiert sie im PR): `wishes_disabled`
(404), `wish_not_found` (404), `wish_state_conflict` (409), `wish_emote_already_in_set` (409, mit
`slotIds`), `wish_already_running` (409, mit `wishId`), `wish_duration_out_of_range` (400),
`wish_alias_invalid` (400), `wish_requester_invalid` (400), `wish_settings_out_of_range` (400),
`wish_settings_restricted` (403), `wish_slot_not_found` (404), `wish_slot_already_linked` (409).

F2 beim Handeintrag: Liegt die Emote-ID schon aktiv im Set, antwortet die Api
409 `wish_emote_already_in_set`; der Client bietet „Bestand übernehmen“ an. Läuft schon ein Wunsch
für die Emote-ID: 409 `wish_already_running` mit der ID des laufenden Wunsches; der Client bietet
„Verlängern“ an (E13). Ein volles Set (`ActiveEmoteSetCapacity`) blockiert nicht, die Antwort trägt nur
`setFull: true` als Hinweis, weil die Zahl veralten kann.

`GET /api/channels/{c}/permissions` (`ChannelEndpoints.cs`, heute u. a. `TagRunsEnabled`) bekommt
`wishesEnabled` (der Schalter `Wishes:Enabled`).

**Live-Event:** `LiveEvents.WishChanged = "wish.changed"`, mit Kanal, ohne weitere Nutzlast. Nur in
`ChannelTypes` aufnehmen, also nur auf `GET /api/channels/{channelName}/live`. Das Prädikat des
kanalübergreifenden Streams (`LiveEndpoints.cs:92`) bleibt unverändert und lässt `wish.changed`
nicht durch (E18). Veröffentlicht nach jedem Commit, der einen Wunsch oder die Einstellungen
ändert, höchstens einmal je Commit.

**Entscheidungsversion (E25):** Jede schreibende Route und jede Eigentumsänderung im Abgleich erhöht
`DecisionVersion` und leert `RemovalQueuedAtUtc` und `RemovalQueuedVersion` im selben Update. In K1
setzt noch nichts eine Einreihung; die Regel steht hier, damit keine spätere Route sie vergisst.

**Audit** (atomar mit dem `SaveChanges` der Aktion, Details nur `wishId`, `sevenTvEmoteId`,
`durationDays`, nie Alias, nie Einlöser): `wish.create`, `wish.adopt`,
`wish.extend`, `wish.keep`, `wish.remove`, `wish.close`, `wish.retry`,
`wish.readd`, `wish.settings`. Die Frontend-Übersetzungen `audit.actions.*` liefert K3.
`wish.extend`, `wish.keep`, `wish.close` und `wish.remove` tragen zusätzlich `previousExpiresAtUtc`,
wenn der Wunsch beim Befehl überfällig war (K2, Plan-484 Revision 2; Grundlage der Pilot-Messung).

## Akzeptanzkriterien
1. **AK1** Migration `AddWishes` läuft auf einer Kopie der Dev-Datenbank durch;
   `dotnet ef migrations list` zeigt danach nichts Ausstehendes; `Wishes` und `ChannelWishSettings`
   sind leer, keine bestehende Tabelle hat eine geänderte Spalte.
2. **AK2** `WishTransitions` lässt genau die Übergänge aus Abschnitt 5 zu; jeder nicht gelistete
   (Zustand, Aktion) ergibt 409 `wish_state_conflict`, geprüft als vollständige Matrix.
3. **AK3** Zwei gleichzeitige `keep` auf denselben Wunsch: genau einer 200, der andere 409, ein
   Audit-Eintrag.
4. **AK4** Handeintrag mit `durationDays` 0 oder 366 → 400 `wish_duration_out_of_range`; mit 1 und
   365 → 201; ohne Wert gilt der Kanal-Default.
5. **AK5** Handeintrag für eine Emote-ID, die aktiv im Set liegt → 409
   `wish_emote_already_in_set` mit den Slot-IDs; anschließendes `adopt` mit dieser Slot-ID → 201,
   `status = active`, `botOwned = false`, `startsAtUtc` = `AddedAt` des Slots.
6. **AK6** Handeintrag für eine Emote-ID mit laufendem Wunsch → 409 `wish_already_running` mit
   dessen ID. Ist der laufende `active` oder `presented`, verlängert ein anschließendes `extend`
   `ExpiresAtUtc` um den Kanal-Default und erhöht `ExtensionCount` um 1. Ist er `approved`, antwortet
   `extend` 409 `wish_state_conflict`, und `PATCH` mit `durationDays` ändert die Laufzeit.
7. **AK7** Abgleich: `approved` plus genau ein neuer Slot gleicher Emote-ID → `active`, `SlotId`
   gesetzt, Alias folgt dem Slot. Zwei neue Slots gleicher Emote-ID, einer mit dem Alias des
   Wunsches → genau dieser. Zwei neue Slots, keiner mit dem Alias des Wunsches → bleibt
   `approved`. Ein Slot mit `AddedAt` eine Sekunde vor `ApprovedAtUtc` ist kein Kandidat.
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
    ausgeschaltetem Schalter 404 `wishes_disabled`,
    Nicht-Verwalter bei ausgeschaltetem Schalter 403 (nicht 404).
14. **AK14** `PUT /settings` bei `BotModeEnabled = true`: Twitch-Mod, der nicht aktiviert hat → 403
    `wish_settings_restricted`; Broadcaster, Aktivierender und globaler Admin → 200.
15. **AK15** `GET /api/wishes/mine` enthält nur Wünsche aus Kanälen, die der Aufrufer verwalten
    darf; ein Konto, das nur 7TV-Editor eines Kanals ist, sieht dessen Wünsche nicht. `GET /summary`
    ruft `IMyChannelsService` nie auf, antwortet ohne Wünsche mit Handlungsbedarf ohne Rollenprüfung
    mit 0, und ein zweiter Aufruf desselben Kontos binnen 5 Minuten löst keinen Helix-Aufruf aus.
16. **AK16** `wish.changed` erscheint nach einem Handeintrag auf
    `GET /api/channels/{c}/live` dieses Kanals, nicht auf `/api/channels/live-events` und nicht auf dem
    Kanal-Stream eines anderen Kanals.
17. **AK17** Retention: Ein Wunsch mit `EndedAtUtc` vor 31 Tagen hat nach dem Durchlauf beide
    Einlöser-Felder null, einer vor 29 Tagen nicht. Ohne Endzustand (E14): `presented` mit
    `ExpiresAtUtc` vor 31 Tagen → null; `presented` (`unconfirmed`), `failed` und `approved` ohne
    `ExpiresAtUtc` mit `StatusChangedAtUtc` vor 31 Tagen → null, vor 29 Tagen nicht; ein `failed`,
    der alle 20 Tage erneut versucht wurde, mit `CreatedAtUtc` vor 121 Tagen → null; ein `active`
    mit 365 Tagen Laufzeit behält sie. Ein Wunsch mit `EndedAtUtc` vor 366 Tagen ist gelöscht. Mit
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
| Integration | `Infrastructure.Tests/Integration` (Postgres-Fixture) | `WishService` (Befehle, bedingte Updates, E13, Indizes), `WishReconcileService` (AK7 bis AK12), Kanal-Zusammenführung mit emote-losem Verlierer, der Wünsche trägt, Retention, Kontolöschung, Purge | +35 |
| Api | `Api.Tests` | Filter-Matrix AK13, AK14, neuer `WishSettingsAuthorizationFilter` | +12 |
| Live | lokal | AK21 | 1 Durchgang |

Keine Tests für die dünnen Handler (Regel 11). Sync-Tests des Bestands bleiben unverändert grün; ein
zusätzlicher Integrationstest zeigt, dass ein Fehler im Abgleich den Sync nicht abbricht.

## Files Reference

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Core/Entities/Wish.cs`, `ChannelWishSettings.cs`, `WishStatus.cs` | neu |
| `src/EmotePurge.Core/Services/IWishService.cs`, `IWishOverviewService.cs`, `IWishReconcileService.cs`, `WishSlotMatcher.cs`, `WishTransitions.cs`, `WishOptions.cs` | neu |
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
| `src/EmotePurge.Api/Endpoints/ChannelEndpoints.cs` (`/permissions`) | Feld `wishesEnabled` |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | zwölf Codes |
| `docker-compose.yml`, `docker-compose.prod.yml`, `.env.example` | `WISHES_ENABLED` an `api` und `worker` |
| `docs/DECISIONS.md` | englischer Eintrag: Wunsch-Modell, zustandsbasierter Abgleich, Schalter |
| `docs/Feature-Ideen-2026-08-01.md` | `A18`, Verweis an #3 |

## Abhängigkeiten

Keine. Erster PR des Epics.

---

# K2: Ablauf-Lauf im Worker

Labels: `feat`, `worker`. Hängt ab von K1.

## Ziel

Ein Hosted Service, der Fristen durchsetzt, ohne je nach 7TV zu schreiben:
fällige Wünsche feststellen, Schutz und Lücke bewerten, vorlegen oder zur Bot-Entfernung vormerken.

## Umfang

`WishExpiryWorker` (konkrete Klasse, nur per `AddHostedService`, Regel 5), `IWishExpiryService`
(Infrastructure) und `WishExpiryPolicy` (rein, Core). Keine Migration.

## Implementation Details

- Takt nach E6 mit `PeriodicTimer` und `TimeProvider`. Bei `Wishes:Enabled = false` beendet sich
  der Dienst nach einer Logzeile (Muster `ChatLogBackfillWorker`).
- Je Durchlauf, in dieser Reihenfolge: (1) Abgleich für jeden Kanal mit laufenden Wünschen (E9).
  (2) Für jeden `active` mit `ExpiresAtUtc` ≤ jetzt die Entscheidung aus der Tabelle unten. (3) Ein
  `wish.changed` je betroffenem Kanal. (4) Eine Logzeile mit reinen Zählwerten, nur wenn sich etwas
  geändert hat. Einen Zweig für offene Wünsche gibt es nicht (W1).
- Ein Wunsch mit `StartsAtAuthoritative = false` wird zurückgehalten (nicht fällig gestellt), solange
  der letzte erfolgreiche Sync des Kanals jünger als 24 Stunden ist **und** der Ablauf weniger als
  24 Stunden zurückliegt. Danach wird er fällig, damit kein Wunsch dauerhaft hängt (ein Eintrag ohne
  Datum bei 7TV bliebe sonst für immer vorläufig). Ein vorläufiger Start führt aber **nie** zur
  Einreihung einer Bot-Entfernung: Zeile 3 der Tabelle legt ihn vor, noch vor dem Bot-Ausfall (Betreiberentscheidung
  2026-11-18, Plan-484 Revision 1).
- Eine Änderung der Schutzschwelle (`PUT …/wishes/settings`) ist eine Entscheidung im Sinn von E25: Sie
  sperrt nach der Kanalzeile die aktiven Wünsche des Kanals, erhöht deren `DecisionVersion` und leert
  deren Einreihung im selben Commit. Der Lauf selbst erhöht `DecisionVersion` nie (Plan-484 Revision 1).
- Jeder Übergang ist ein bedingtes Update; ein zweiter Lauf über denselben Stand ändert nichts.
  Der Lauf betrachtet nur Wünsche mit `Status = active`, `ExpiresAtUtc` ≤ jetzt **und
  `RemovalQueuedAtUtc IS NULL`**; ein schon eingereihter Wunsch wird weder erneut bewertet noch
  erneut eingereiht, seine Einreihung und ihre Version bleiben unverändert. Ein vorgelegter Wunsch
  ist nicht mehr `active` und fällt von selbst heraus.
- Je Fälligkeit schreibt der Lauf genau einen Audit-Eintrag `wish.due` unter `AuditActor.System`
  (Details `wishId`, `outcome`: der Grund der Vorlage oder `queued`, und `expiresAtUtc`: der Ablauf,
  auf den sich der Eintrag bezieht), im selben Update wie die
  Entscheidung. Nach einer Verlängerung ist die nächste Fälligkeit ein neuer Eintrag. Wird derselbe
  Ablauf erneut bewertet (eine Einreihung wurde ohne Verlängerung verworfen, etwa weil das Eigentum
  durch Umbenennen endete), entsteht **kein** zweiter Eintrag. Das ist die
  Grundlage der Pilot-Messung (Abschnitt 13).

Entscheidung bei Fälligkeit (`WishExpiryPolicy.Decide`, erste zutreffende Zeile gewinnt):

| # | Bedingung | Ergebnis |
|---|---|---|
| 1 | Kanal im manuellen Modus | `presented` (`expired`) |
| 2 | Bot-Modus, `BotOwned` falsch | `presented` (`not_bot_owned`) |
| 3 | Start nur vorläufig (`StartsAtAuthoritative = false`) | `presented` (`unconfirmed_start`) |
| 4 | Bot ausgefallen (global oder `BotSuspendedReason`) | `presented` (`bot_unavailable`) |
| 5 | Schutz an und Zähllücke nach E5 | `presented` (`coverage_gap`) |
| 6 | Schutz an und Mittel je Tag (E4) ≥ Schwelle | `presented` (`protected`) |
| 7 | sonst | `RemovalQueuedAtUtc = jetzt`, `RemovalQueuedVersion = DecisionVersion`, Zustand bleibt `active` |

Bis K4 liefert die Bot-Verfügbarkeit eine Schnittstelle `IWishBotAvailability` mit einer
Standard-Implementierung „nie verfügbar“; Zeile 7 ist damit vor K4 nur im Test erreichbar.

Kurze Laufzeiten brauchen keine eigene Logik: Bei 1 bis 6 Tagen ist das Fenster die ganze Zeit im
Set (E4), bei 1 Tag genau 24 Stunden. Stellt ein Lauf die Fälligkeit bis zu 15 Minuten nach Ablauf
fest, ist das Fenster entsprechend länger, nie kürzer.

## Akzeptanzkriterien
22. **AK22** `WishExpiryPolicy` liefert für jede der sieben Zeilen das genannte Ergebnis; die
    Reihenfolge ist getestet (ein Fall, der Zeile 2 und 5 erfüllt, ergibt `not_bot_owned`).
23. **AK23** Fenster E4: Laufzeit 30 Tage → Fenster genau 7 Tage; Laufzeit 2 Tage → Fenster 2 Tage,
    Mittel = Summe geteilt durch 2; Laufzeit 1 Tag → Fenster 24 h, gültig; ein Wunsch, der 23 h im
    Set ist (künstlich früher Ablauf), gilt als Lücke.
24. **AK24** Mittel je Tag summiert über alle Slots desselben Emotes im Kanal (R3-8): Emote unter
    zwei Aliasen mit 10 und 20 Nutzungen je Tag ergibt 30.
25. **AK25** Lücke E5: `TrackedSince` einen Tag nach Fensterbeginn → `coverage_gap`, auch wenn die
    Nutzung über der Schwelle liegt; `DeactivatedAtUtc` gesetzt → `coverage_gap`; Schutz aus und
    dieselbe Lage → Zeile 7.
26. **AK26** Ein Wunsch mit vorläufigem Start wird bei frischem Sync nicht fällig gestellt, solange
    sein Ablauf weniger als 24 Stunden zurückliegt; bei einem letzten Sync vor 25 h oder einem
    Ablauf vor 25 h wird er fällig. Im manuellen Modus ergibt das `presented` (`expired`); im
    Bot-Modus mit Eigentum `presented` (`unconfirmed_start`), auch bei ausgefallenem Bot, nie eine
    Einreihung, auch bei ausgeschaltetem Schutz.
27. **AK27** Zwei Durchläufe hintereinander über denselben Stand: der zweite ändert keine Zeile,
    veröffentlicht kein Event und schreibt keinen Audit-Eintrag. Ein eingereihter Wunsch behält
    `RemovalQueuedAtUtc` und `RemovalQueuedVersion` unverändert. Je Fälligkeit gibt es genau einen
    Eintrag `wish.due`; verlängern und erneut ablaufen ergibt einen zweiten.
28. **AK28** `Wishes:Enabled = false`: genau eine Logzeile, kein Datenbankzugriff danach.
29. **AK29** Der Worker hat fünfzehn Hosted Services; `LogCaptureWriter` bleibt der erste.
30. **AK30** Live-Verifikation (Regel 16) lokal: per `adopt` einen Bestandseintrag mit
    `startsAtUtc` vor 8 Tagen und `durationDays = 7` übernehmen; nach höchstens einem Takt steht der
    Wunsch auf `presented` (`expired`) und `wish.changed` kommt auf dem Kanal-Stream an. Wiederholen
    mit Schutzschwelle 1 auf einem Emote mit echter Nutzung im Fenster: Ergebnis `presented`
    (`expired`) im manuellen Modus bleibt, die Detailantwort zeigt `perDayMean` > 0 und
    `coverageGap = false`.
31. **AK31** Nach der Aktivierung im Pilot (Beobachtung, kein Merge-Tor): Ein echter Wunsch mit
    7 Tagen Laufzeit zeigt ab Start „Probezeit“ (Vorlauf 7) und steht spätestens 15 Minuten nach
    Ablauf auf `presented`.

## Testplan

| Ebene | Projekt | Was | Anzahl |
|---|---|---|---|
| Unit | `Infrastructure.Tests/Unit` | `WishExpiryPolicy`, Fenster- und Lückenregel mit `FakeTimeProvider` | +18 |
| Integration | `Infrastructure.Tests/Integration` | `WishExpiryService` gegen Postgres (AK24 bis AK27) | +10 |
| Worker | `Worker.Tests` | nichts: Der Worker ist nur Takt; die Logik liegt in Infrastructure | 0 |
| Live | lokal, später Pilot | AK30, AK31 | keine |

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

Tab „Wünsche“ im Channel-Workspace als reine Arbeitsliste, Abschnitt „Wünsche“ im Settings-Tab,
Seite `my-wishes`,
Link mit Zähler in der Kopfzeile, i18n, Fehlercodes, Audit-Übersetzungen. Kein Bot-Zustand außer
einer neutralen Darstellung unbekannter Gründe (K5 ergänzt). Die Bereiche für offene Wünsche, das
Anlernen und die Lückenanzeige ergänzt K6.

## Implementation Details

- **Routen** (`app.routes.ts`): Kindroute `wishes` unter `channels/:channelName` mit neuem
  `channelWishesGuard` (canManage und `wishesEnabled` aus `/permissions`); Route `my-wishes` mit
  `authGuard` nach Vorlage `features/my-votings/`.
- **Tab** (`channel-workspace-layout.ts:93-110`): `<app-tab-link link="wishes">` hinter
  `canManage() && wishesEnabled()`. Der Guard ist die Prüfung, die Bedingung nur Sichtbarkeit
  (UI-Designsprache §8.3).
- **Settings-Tab** (E19): Der Link (`channel-workspace-layout.ts:105-110`) und `channelSettingsGuard`
  hängen an `canManage() && (chatLogBackfillEnabled() || wishesEnabled())`.
  `channel-settings-page.ts` zeigt `<app-backfill-section>` nur bei `chatLogBackfillEnabled()` und
  darunter den neuen Abschnitt `<app-wish-settings-section>` nur bei `wishesEnabled()`. Sind beide
  Schalter aus, gibt es wie bisher keinen Tab und die Route leitet um; D17 und AK14 der
  Backfill-Spec bleiben gültig. Der bestehende E2E-Fall in `web/e2e/channel-settings.e2e.spec.ts`
  bleibt grün und bekommt die neuen Kombinationen dazu.
- **Core** (`core/wishes/`): `wish.model.ts`, `wish.service.ts` (alle Routen aus K1),
  `wish-summary.service.ts` (Zähler als Signal, lädt beim Start der Shell und danach alle 5 Minuten,
  pausiert bei verborgenem Fenster, kein SSE), Typ `wishChanged` in `core/live/live-event.model.ts:16-30`.
- **Features** (`features/wishes/`): `channel-wishes-page.ts` (drei Bereiche: Handlungsbedarf,
  laufend, beendet mit Pagination), `wish-card.ts`, `wish-entry-dialog.ts` (Handeintrag: 7TV-Link
  oder ID, Alias, Laufzeit vorbelegt mit dem Kanal-Default, optional Login des Wünschenden),
  `wish-adopt-dialog.ts`, `my-wishes-page.ts`; für den Settings-Tab
  `features/channel-workspace/wish-settings-section.ts`.
- **Handeintrag** (Nebenweg für Korrektur und Nachtrag; in der Oberfläche die Nebenaktion
  „Nachtragen“, nicht die Hauptaktion der Seite): Der Client löst Emote-ID und Standardnamen aus dem Link über den öffentlichen
  7TV-Read auf, den der Browser schon nutzt, und schickt beides. Normalisierung wie der Server:
  Login getrimmt und kleingeschrieben vor der Prüfung (Regel 9 sinngemäß).
- **Karte:** Emote-Bild, Alias, Wünschender (falls vorhanden), Quelle, Zeit im Set, Restzeit bzw.
  „seit N Tagen abgelaufen“, Laufzeit, Nutzung im Fenster mit Sparkline (`usage-sparkline.ts`,
  `getDailySeries` je Slot), „Rang N von M“, Hinweis bei `coverageGap`. Aktionen je Zustand nach
  Abschnitt 5.
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
  Gründe `wishes.reason.*`), `channelWorkspace.tabs.wishes`, `channelSettings.wishes.*`, `shell.wishes`, `errors.api.wish_*`,
  `audit.actions.wish.*`, in `de.json` und `en.json`. Keine „…“ an Knöpfen.
- **Design:** gegen `docs/UI-Designsprache.md` §2.1, §4.2 (Entfernen ist destruktiv, Stufe wie das
  Löschen eines Eintrags), §4.3, §6.2 (leerer Zustand je Bereich), §7, §8.1, §8.5, §11. Keine
  Layout-Sprünge zwischen den Tabs.

### Stand der Umsetzung (Branch `nacht/485-wish-ui`, Plan `docs/plans/Plan-485-Wunsch-Emotes-Verwalter-UI.md`)

K3 liegt auf dem Branch, ist **nicht gemergt und nicht deployt**; die Live-Verifikation (AK45, Plan T16)
steht aus. Die obigen Implementation Details sind der Entwurf; wo der Bau abweicht, gilt dieser Block.

**Dateipfade.** Seiten: `web/src/app/features/wishes/channel-wishes-page.ts` mit `wish-row.ts`,
`wish-image.ts`, `wish-usage.ts`, `wish-labels.ts`, `wish-operation.ts`, den Dialogen `wish-entry-dialog.ts`,
`wish-adopt-dialog.ts`, `wish-edit-dialog.ts`, `wish-readd-dialog.ts`, `wish-removal-confirm-dialog.ts` und
`wish-removal-flow.ts`; `web/src/app/features/my-wishes/my-wishes-page.ts`;
`web/src/app/features/channel-workspace/wish-settings-section.ts`. Core: `web/src/app/core/wishes/`
(`wish.model.ts`, `wish.service.ts`, `wish-view.ts`, `wish-input.ts`, `wish-error.ts`, `wish-run-start.ts`,
`wish-add.service.ts`, `wish-removal.ts`, `wish-removal-tracker.ts`, `wish-summary.service.ts`); die Guards
liegen unter `core/channels/` (`channel-wishes.guard.ts`, `channel-settings.guard.ts`), nicht unter
`core/guards/`; `web/src/app/core/seven-tv/seven-tv-emote-lookup.ts` (der einzelne 7TV-Read der Formulare),
`seven-tv-add-emote.ts` (die eine AddEmote-Konstante).

**Fußnoten aus Plan §9 (Spec ↔ gemergter K1-Stand).**

- F1: `errors.api.wish_*` und `api-error.ts` lieferte schon K1; K3 hat nur gegengeprobt (92 Konstanten in
  `ApiErrorCodes.cs`, 92 in `KNOWN_API_ERROR_CODES`, 92 `errors.api.*` je Locale).
- F2: Es gibt elf Audit-Aktionen (`wish.update` und `wish.due` zusätzlich zu den neun), alle übersetzt.
- F3: Drei Ausgänge haben keinen eigenen Code (`emote_ids_invalid`, zweimal `invalid_request_body`); der
  Text ist kontextabhängig (`wishErrorKey`).
- F4: 409 `wish_emote_already_in_set` und `wish_slot_already_linked` nennen die `wishId` des Halters bzw. eines
  laufenden Wunsches; „im Set“ gewinnt vor „läuft schon“.
- F5: Handeintrag ohne Alias und ohne Namen ist 400 `wish_alias_invalid`; der Dialog erzeugt den Fall nicht.
- F6: `emoteName` bleibt nach `adopt` der Slot-Name; die Oberfläche zeigt nur den `alias`.
- F7: `complete-removal` hat einen Rumpf (`slotId`, `slotAddedAtUtc`, `emoteSetId`, optional `evidence`) und prüft
  unter der Kanalsperre Set, Generation und Inventur; ein bereits gebuchter Wunsch antwortet 200 ohne Schreiben.
  `EndedAtUtc` bleibt beim Übergang `removed_externally` → `removed` erhalten.
- F8: `WishDto` trägt `slotAddedAtUtc` (Generation des Slots) und `imageUrl`; `usage.daily` ist die auf acht Tage
  begrenzte, nullgefüllte Tagesreihe aus derselben Abfrage wie `total`. Die Sparkline liest nur diese Reihe.
- F9: `GET …/wishes/settings` liefert `canEdit`; `PUT …/wishes/settings` mit nicht gesendeter Schwelle lässt sie
  unverändert (`null` schaltet den Schutz ab); die Schwelle zeigt K3 nicht.
- F10: `adopt` verlangt `expectedAlias` und `expectedAddedAtUtc` (verbatim aus `GET …/wishes/slots`); abweichend: 404
  `wish_slot_not_found`.
- F11: `GET …/wishes/slots` listet die Einträge, die `adopt` annähme, samt Halter und laufendem Wunsch.
- F12: `recheck` antwortet bis K4 immer 409 und wird nicht angeboten.
- F13: `mine` ist eine flache Liste; die Gruppierung (Handlungsbedarf, Probezeit) macht der Client.
- F14: 201 trägt `setFull` nur, wenn es wahr ist; das Feld ist optional.
- F15: Der Settings-Link hängt an `canManage && (chatLogBackfillEnabled || wishesEnabled)` (E19, AK41), nicht mehr
  nur an `canManage`.
- F16: Es gibt keine Entprellung im Zähler-Dienst (ein Poll hat nichts zu entprellen); getestet sind Takt,
  Sichtbarkeit und Abschalten.
- F17: Die Audit-Ansicht zeigt nur den Aktionsnamen; die Api liefert `detail: null` für alle `wish.*`.
- F18: siehe AK38.
- F19: Auf grobem Zeiger gibt es keine 7TV-Schreibaktion (§2.5 der Designsprache); dort stehen die Aktionen, die nur
  in Emote Purge buchen, und der Link zum Emote auf 7TV.
- F20: „Karte“ ist eine linierte Zeile (`wish-row`), keine Kartenklasse (Designsprache §2.1).
- F21: „Bestand übernehmen“ mit dem Start = `AddedAt` machte einen alten Eintrag sofort überfällig; der Dialog zeigt
  die Vorschau und wählt „ab jetzt“ vor, wenn der Ablauf sonst schon verstrichen wäre.
- F22: Die Schutzschwelle wirkt im manuellen Modus nicht; K3 zeigt und sendet sie nicht.
- F23: Behaltene Wünsche halten ihren Slot; Eintragsliste und 409 nennen den Halter.
- F24: Die Kennzahlen rechnen über alle Slots des Emotes im Fenster der Zahlen, nicht je Slot ab Start.
- F25: Das aktive Set kommt vor jedem 7TV-Schreiben aus einem autoritativen Read (`…/emote-sets?refresh=true`,
  7TVs `style.activeEmoteSetId`), nicht aus dem Postgres-Stand von `…/emotes/active-set`. Der Postgres-Stand dient
  nur dem Vergleich: weicht er ab, startet nichts.

**Kopfzeile.** Gebaut ist Stufe 2 (AK44, offen). Beim Zähler gilt der Poll aus W6; `GET /api/wishes/summary` wird nur
bei eingeloggtem Nutzer abgefragt.

## Akzeptanzkriterien
32. **AK32** Tab und Route: Verwalter mit `wishesEnabled` sieht den Tab; ohne `canManage` oder ohne
    Schalter fehlt er und `/channels/x/wishes` leitet um wie ein abgelehnter Verwalter-Guard.
33. **AK33** Handeintrag: Der Dialog belegt die Laufzeit mit dem Kanal-Default vor; Werte außerhalb
    1 bis 365 sperren den Knopf mit sichtbarem Grund; ein Login mit Großbuchstaben wird normalisiert
    gesendet.
34. **AK34** 409 `wish_emote_already_in_set` im Handeintrag öffnet „Bestand übernehmen“ mit den
    gemeldeten Einträgen.
35. **AK35** Je Zustand zeigt die Karte genau die Aktionen aus Abschnitt 5 (Zustand → Aktionsliste
    als `computed()`, getestet als Tabelle); ein unbekannter `statusReason` rendert eine neutrale
    Zeile statt eines Fehlers.
36. **AK36** Probezeit ist aus `probationStartsAtUtc` abgeleitet: Ein Wunsch mit 2 Tagen Laufzeit
    und Vorlauf 7 zeigt ab Start „Probezeit“.
37. **AK37** Entfernen aus der Karte: Der Lauf bindet über Alias und `addedAt`; weicht der frische
    Read ab, wird nichts gesendet und die Karte meldet es. Nach Erfolg geht genau ein
    `complete-removal` an die Api.
38. **AK38** Während ein anderer 7TV-Lauf aktiv ist, sind Hinzufügen und Entfernen auf allen Zeilen
    gesperrt (`aria-disabled`) und nennen den Grund. *Stand K3 (F18):* Der Grund steht nicht am
    Knopf, sondern in einer Seitenzeile (`wishes-run-lock`), auf die jeder gesperrte 7TV-Knopf per
    `aria-describedby` zeigt; UI-Designsprache §4.2 sieht für Sperren ohne Hinweistext vor, die Zeile
    ist die dort beschriebene Beschreibung. Die Zeile spricht nur, solange die Seite eine 7TV-Aktion
    anbietet.
39. **AK39** `wish.changed` auf dem Kanal-Stream lädt den Wünsche-Tab ohne Reload neu. Der Zähler
    lädt beim Seitenaufruf und danach alle 5 Minuten (mit `page.clock`: nach 15 Minuten genau drei
    weitere `summary`-Anfragen), öffnet keinen eigenen Stream und fragt `/api/channels/mine` nicht
    ab.
40. **AK40** Zähler: zeigt `actionCount`, bei 0 keine Zahl; der Link fehlt bei 404 `wishes_disabled`.
41. **AK41** Settings-Tab: nur Backfill-Schalter an → Tab mit Backfill-Abschnitt allein; nur
    `wishesEnabled` → Tab mit dem Abschnitt „Wünsche“ allein; beide an → beide, „Wünsche“ darunter;
    beide aus → kein Tab, `/channels/x/settings` leitet um wie bisher. Im Bot-Modus ist der
    Abschnitt für einen Twitch-Mod, der nicht aktiviert hat, schreibgeschützt mit Erklärung; die
    Grenzen 1 bis 365 und 1 bis 30 werden vor dem Senden geprüft.
42. **AK42** `my-wishes` listet Handlungsbedarf vor Probezeit, innerhalb nach Ablauf aufsteigend,
    und nennt je Zeile den Kanal.
43. **AK43** `api-error-locales.spec.ts` grün mit allen `wish_*`-Codes; `api-error.ts` enthält
    jeden Code aus `ApiErrorCodes.cs` (von Hand gegengeprüft, im PR benannt).
44. **AK44** Bei 360 px Breite hat die Kopfzeile mit Zähler und einem aktiven Status-Badge keinen
    horizontalen Überlauf und kein abgeschnittenes Element (Audit-Harness, `smallTargets`
    mitgeprüft). **Stand 2026-11-19: offen, nicht erfüllt.** Gebaut ist Stufe 2 (Link mit Zähler als
    Chip, unter `sm` nur die Zahl). Die Fit-Prüfung (`assertHeaderFits`, Szenario
    `shell-wishes-count-badge`) besteht ab 480 px in beiden Sprachen; bei 360 px in en nur knapp
    (Wortmarke bei 0 px, Logo bei 1 px, neuer `smallTargetsUnder24`-Eintrag „Emote Purge 1×28“); bei
    360 px in de **nicht**: „Abstimmungen“ überdeckt das Badge um 33 px (Worker-Warnung, Zahl 12),
    26 px (Worker-Warnung, Zahl 3) bzw. 22 px (Live-Quota-Badge, Zahl 3). Die üblichen
    Dokumentmetriken (Überlauf, Kontrast, kleine Ziele) bleiben dabei sauber, das ist die vom Plan
    benannte Messlücke. Der Betreiber entscheidet (Optionen: Stufe 3 ohne Kopfzeilenelement unter
    `sm`, nur Menüeintrag mit Zahl, eine Abweichung von diesem AK; Vorrang des Badges vor dem Zähler
    unter `sm`; engere Abstände, geschätzt höchstens 28 px gegen 33 px nötige, ungemessen). Bis dahin
    ist der Audit-Fall `shell-wishes-count-badge` bei `mobile`/de erwartet rot.
45. **AK45** Live-Probe im Browser gegen den lokalen Stack mit dem Testkonto: Handeintrag,
    Hinzufügen aus der Karte mit eigenem Token, Karte wird nach dem Sync `aktiv`; übernommener,
    abgelaufener Eintrag erscheint in Kopfzeile und Übersicht; Entfernen aus der Karte endet in
    `entfernt`. Auf einem echten Handy über den `lan`-Weg einmal durchgeklickt.

## Testplan

| Ebene | Was | Anzahl |
|---|---|---|
| Vitest, `core/wishes` | `WishService` (`HttpTestingController`), `WishSummaryService` (Entprellen), Guard | +14 |
| Vitest, Komponenten | nur Entscheidungslogik: Aktionsliste je Zustand, Sperrgründe, Sortierung, Probezeit-Ableitung, Validierung (Regel 12: Verhalten, nicht Vorlage) | +16 |
| Playwright | `web/e2e/wishes.e2e.spec.ts` mit `e2e/support/mocks.ts`: Handeintrag bis Karte; fälliger Wunsch in Übersicht und Zähler; Entfernen mit gemocktem 7TV; Tab unsichtbar ohne Schalter; Settings-Tab in den vier Schalter-Kombinationen | +6 |
| Live | AK45 | 1 Durchgang |

E2E nur ohne laufende Api auf `:5151`; Import aus `web/e2e/support/test.ts`.

## Files Reference

| Datei | Änderung |
|---|---|
| `web/src/app/app.routes.ts:52`, `:102-161` | zwei Routen |
| `web/src/app/features/channel-workspace/channel-workspace-layout.ts:93-110` | Tab „Wünsche“; Settings-Link nur noch `canManage` (:105-110) |
| `web/src/app/features/channel-workspace/channel-settings-page.ts`, `wish-settings-section.ts` | zweiter Abschnitt; jeder Abschnitt hinter seinem eigenen Schalter |
| `web/src/app/core/guards/` (`channelSettingsGuard`) | `canManage` und mindestens einer der beiden Schalter |
| `web/e2e/channel-settings.e2e.spec.ts` | um die Kombinationen aus E19 ergänzt |
| `docs/DECISIONS.md` | englischer Eintrag zu E19 (Bedingung des Settings-Tabs erweitert, D17 bleibt gültig) |
| `web/src/app/features/shell/app-shell.ts` | Link mit Zähler |
| `web/src/app/core/wishes/*` | neu, mit Specs |
| `web/src/app/features/wishes/*` | neu |
| `web/src/app/core/guards/` | `channelWishesGuard` |
| `web/src/app/core/live/live-event.model.ts:16-30` | Typ |
| `web/src/app/core/seven-tv/seven-tv-restore.service.ts:54`, `seven-tv-undo.service.ts:75`, `seven-tv-import.service.ts:74` | eine gemeinsame AddEmote-Konstante |
| `web/src/app/core/i18n/api-error.ts` | Codes |
| `web/public/i18n/de.json`, `en.json` | Schlüsselbereiche |
| `web/e2e/wishes.e2e.spec.ts`, `web/e2e/support/mocks.ts` | neu, Mocks |
| `docs/UI-Designsprache.md`, `DESIGN.md` | `UI-Designsprache.md` §2.5, §4.2, §8.7 (Ist-Stand, englisch); `DESIGN.md` unberührt, weil weder ein Token noch eine benannte Regel betroffen ist |

## Abhängigkeiten

K1; K2 für AK45.

---

# K6: Chat-Erfassung und Belohnung anlernen

Labels: `feat`, `worker`, `api`, `infrastructure`, `web`, `migration`. Hängt ab von K1 und K3 (Tab
und Settings-Abschnitt). Ein Kind samt Oberfläche.

## Ziel

Emote Purge erkennt Einlösungen der Wunsch-Belohnung selbst und ordnet sie dem Emote zu, das der Mod
wie gewohnt in 7TV hinzufügt. Der Mod trägt nichts ein: Er lernt die Belohnung einmal an, danach
entstehen Wünsche von allein und werden von allein `aktiv`.

## Umfang

Backend: Tag `custom-reward-id` im Worker lesen, Anlernen, Wünsche in `pending` erzeugen, Text
auswerten, Treffer über den Namen in der Zuordnungsregel, Freigeben, Ablehnen, Verfall, Dedupe,
Begrenzung je Zuschauer, Erfassungslücken, Schalter `Wishes:Capture:Enabled`, Migration
`AddWishCapture` (6.6). Oberfläche: Bereich „Offen“ in der Arbeitsliste, Anlernen und
Belohnungsliste im Settings-Abschnitt, Lückenanzeige. Nicht enthalten: EventSub, Chat-Antworten,
Belohnungen ohne Texteingabe.

## Implementation Details

**Lesen im Worker.** `TwitchChatManager.OnMessageReceived` wertet den Tag `custom-reward-id` **nach**
dem Ausschluss-Filter für widersprechende Chatter (`src/EmotePurge.Worker/TwitchChatManager.cs:1063`)
und **vor** der Rückkehr bei leerem Emote-Cache (`:1089-1094`) aus. Die heutige Stelle von
`SharedChatRule.FromTags` (`:1103`) liegt hinter dieser Rückkehr und wäre für einen Kanal mit
leerem Set oder kaltem Cache nie erreicht (Sol-Befund 7). Reihenfolge im Handler: Lebenszeichen,
Ausschluss, Erfassung, dann wie bisher. Die Raum-Einordnung wird für die Erfassung nur berechnet,
wenn die Nachricht den Tag trägt; der Normalfall kostet eine Tag-Abfrage.

Die Entscheidung liegt TwitchLib-frei in `RewardRedemptionParser` (Worker, rein): Eingabe sind die
Tags, die Raum-Einordnung und der Text einer PRIVMSG, Ausgabe ist nichts oder eine Beobachtung
`{ roomId, rewardId, messageId (Tag id), userId, login, text, sentAtUtc (Tag tmi-sent-ts) }`.
**Nur `MessageOrigin.Own` ergibt eine Beobachtung**, für das Anlernen wie für Einlösungen:
`Foreign` (anderer Raum) und `Indeterminate` (Quell-Marker ohne `source-room-id`,
`SharedChatRule.cs:41-50`) ergeben nie eine (Sol-Befund 8). Ein Chatter auf der Ausschlussliste
erzeugt nie einen Wunsch und nie einen Kandidaten. Die Emote-Zählung der Nachricht bleibt
unverändert. Der Manager legt Beobachtungen in eine begrenzte In-Memory-Warteschlange
(1.000 Einträge, bei Überlauf verwerfen und zählen) und tut sonst nichts; die Lese-Schleife wartet
nie auf die Datenbank.

**`WishCaptureWorker`** (Hosted Service, konkrete Klasse) leert die Warteschlange über
`IWishCaptureService`. Er kennt je Kanal die angelernten Belohnungen und ein laufendes Anlernen aus
einer Konfiguration, die er beim Start, auf das Kommando `WISHCAPTURE:` (ohne vertraute Nutzlast)
und alle 60 Sekunden neu lädt. Je Beobachtung:

1. Läuft für den Kanal ein Anlernen und ist die Belohnung dort nicht schon bekannt: als Kandidat
   speichern (die erste gewinnt), `wish.changed` senden, fertig.
2. Belohnung unbekannt oder pausiert: verwerfen, nichts speichern.
3. Belohnung bekannt: Wunsch in `pending` anlegen (`Source = irc`, Laufzeit aus der Belohnung,
   Einlöser-ID und Login, `RequestText`), `LastRedemptionSeenAtUtc` setzen, `wish.changed` senden.
   Schon vorhandene `IrcMessageId` im Kanal: verwerfen (Dedupe). Hat der Zuschauer im Kanal schon
   `MaxPendingPerRequester` offene Wünsche (Default 3): verwerfen und zählen. Mehr als 200 offene
   Wünsche im Kanal: verwerfen und einmal je Stunde warnen.

**Haushalts-Takt (E26):** jede Minute, unabhängig von `Wishes:Capture:Enabled` und von
`Wishes:Enabled`, erster Takt direkt beim Start: abgelaufene Anlern-Zeilen löschen,
`LastAliveAtUtc` schreiben; alle 15 Minuten zusätzlich offene Wünsche, deren `StatusChangedAtUtc` älter ist als
`PendingExpiryDays`, nach `expired`. Bei `Wishes:Capture:Enabled = false` erzeugt der Manager keine
Beobachtungen und der Dienst verarbeitet keine, der Haushalts-Takt läuft weiter. Jede Route, die
den Anlernzustand liest oder bestätigt, behandelt eine Zeile mit `ExpiresAtUtc` ≤ jetzt als nicht
vorhanden und löscht sie im selben Aufruf.

**Text auswerten** (`WishTextParser`, Core, rein): Enthält der Text irgendwo einen 7TV-Emote-Link,
ist `MatchKind = link` und die Emote-ID gesetzt. Als Link gilt: optional `http://` oder
`https://`, Host `7tv.app`, `www.7tv.app` oder `old.7tv.app` ohne Rücksicht auf Groß- und
Kleinschreibung, Pfad `/emotes/<id>`. **Beide ID-Formate** werden angenommen: die neue ID mit
26 Zeichen (Ziffern und Buchstaben, so in der Live-Probe AK46) und die Hex-ObjectId alter Links
mit 24 Zeichen. Nach der ID dürfen Query-String, Fragment, Schrägstrich und Satzzeichen folgen,
davor und danach beliebiger Text. Eine ID anderer Länge ist kein Link. Bei mehreren Links zählt
der erste.

**Bloße ID (Betreiberentscheidung 2026-11-21):** Ohne Link gilt auch ein eigenständiges Token, das
genau eine ID ist, als `MatchKind = link` mit kanonisierter ID, denn ein Zuschauer löst die Belohnung
auch mit der nackten ID ein. Das Token wird wie in der Namensgrammatik gebildet (Leerraum-Split,
Adressen und Erwähnungen verworfen, Randzeichen `. , ; ! ? " '` entfernt) und gilt als ID nur, wenn
es entweder 24 Hex-Zeichen hat oder 26 Zeichen aus dem ULID-Alphabet (Ziffern und Buchstaben ohne
`I`, `L`, `O`, `U`, ohne Rücksicht auf Groß- und Kleinschreibung) mit erstem Zeichen `0` bis `7`.
Alles andere (25 oder 27 Zeichen, ein verbotener Buchstabe, erstes Zeichen `8` bis `Z`, ein ID-Stück
in einem längeren Wort) bleibt ein Name. Das erste ID-förmige Token irgendwo im Text schlägt jeden
Namen, auch einen früheren; ein Link schlägt weiterhin die bloße ID.

Ohne Link und ohne bloße ID gilt diese Grammatik für den Namen, in dieser Reihenfolge:

1. Den Text an Leerraum in Tokens teilen.
2. Tokens verwerfen, die eine Adresse sind (enthalten `://` oder beginnen mit `www.`) oder eine
   Erwähnung (beginnen mit `@`).
3. Von jedem übrigen Token am Anfang und am Ende die Zeichen `. , ; ! ? " '` entfernen. Andere
   Zeichen bleiben, weil Emote-Namen Klammern, Doppelpunkte und Nicht-ASCII-Zeichen tragen
   können; die Spec stützt sich bewusst nicht auf 7TVs Namensregel, die im Repo nicht steht.
4. Das **erste** Token mit 2 bis 100 Zeichen, das mindestens einen Buchstaben oder eine Ziffer
   enthält, ist `RequestedName`, in der Schreibweise des Textes. `MatchKind = name`.
5. Gibt es keines: `MatchKind = none`, Hinweis `unparsed`.

Groß- und Kleinschreibung bleibt erhalten; verglichen wird erst in der Zuordnung (6.3: ordinal,
dann ohne Rücksicht auf die Schreibweise, jeweils nur eindeutig). Die Regel rät nicht: Bei „bitte
KEKW“ ist der Name `bitte`, es taucht kein passender Slot auf, und der Wunsch bleibt offen für
den Verwalter, der den eingelösten Text sieht.

| Eingelöster Text | `MatchKind` | Ergebnis |
|---|---|---|
| `KEKW` | `name` | `KEKW` |
| `kekw bitte` | `name` | `kekw` |
| `bitte KEKW` | `name` | `bitte` (kein Raten) |
| `@streamer KEKW!` | `name` | `KEKW` |
| `"peepoHappy".` | `name` | `peepoHappy` |
| `(ditto)` | `name` | `(ditto)` |
| `https://example.org/x KEKW` | `name` | `KEKW` |
| `a` | `none` | `unparsed` (zu kurz) |
| `!!! ???` | `none` | `unparsed` |
| leer | `none` | `unparsed` |
| 7TV-Emote-Link plus `KEKW` | `link` | Emote-ID aus dem Link, Name unbeachtet |

Api und Worker rufen für die Auswertung 7TV nicht auf; Bild und Standardname eines Link-Wunsches
löst die Oberfläche über den öffentlichen 7TV-Read
auf.

**Zuordnen.** K6 schaltet in `WishSlotMatcher` den Zustand `pending` und den Treffer über den Namen
frei (6.3). Hinweise am offenen Wunsch: `ambiguous_match` (mehrere Kandidaten), `already_in_set`
(das Emote lag schon vor der Einlösung im Set), `already_running` (für das Emote läuft schon ein
Wunsch). Jede automatische Zuordnung schreibt `wish.autoLink` unter `AuditActor.System`. Ein
Verwalter kann eine automatische Zuordnung wieder lösen (`POST /{id}/unlink`), solange der Wunsch
`active` ist, aus dem Chat stammt und `LinkOrigin = auto` trägt; sonst 409 `wish_state_conflict`.
Wirkung, in einem Update: `Status = pending`, `StatusReason = null`, `StatusChangedAtUtc = jetzt`;
`SlotId`, `StartsAtUtc`, `ExpiresAtUtc`, `LinkOrigin`, `BotOwnedAddedAtUtc`, `RemovalQueuedAtUtc`
und `RemovalQueuedVersion` auf null; `StartsAtAuthoritative` und `BotOwned` auf falsch;
`ExtensionCount` auf 0; `DecisionVersion` plus 1. Unverändert bleiben `DurationDays`,
`CreatedAtUtc`, `EndedAtUtc` (war null und bleibt null), Einlöser-Daten und, bei einem Wunsch
mit Link, `SevenTvEmoteId`; bei einem Namens-Wunsch werden `SevenTvEmoteId`, `EmoteName` und
`Alias` wieder geleert. Der Slot im Set bleibt unberührt. Die Sperre steht dauerhaft in
`WishSlotExclusions` (6.6): Der Abgleich schlägt diesen Slot diesem Wunsch nie wieder vor,
„Bestand übernehmen“ darf ihn weiter wählen und löscht dabei die Sperre. Die Verfallsfrist eines
offenen Wunsches zählt ab `StatusChangedAtUtc`, läuft nach dem Lösen also neu; das
Zuordnungsfenster beginnt weiter bei `CreatedAtUtc`.

**Kanal-Zusammenführung (Sol-Befund 9).** `ChannelIdentityService` verschiebt in der Transaktion der
Zusammenführung, vor dem Löschen des Verlierers (`:1009`), auch `ChannelWishRewards`,
`WishRewardLearnings` und `WishCaptureGaps` zum überlebenden Kanal. Hat der Überlebende dieselbe
`RewardId` schon, gewinnt seine Zeile, und die Wünsche des Verlierers zeigen danach auf sie; kein
Wunsch verliert dabei seine `RewardId`. Läuft in beiden ein Anlernen, bleibt das des Überlebenden.
Über zehn Belohnungen nach dem Zusammenführen sind erlaubt, nur neue lassen sich dann nicht mehr
anlernen. Danach stößt der Dienst `WISHCAPTURE:` an, damit der Worker neu lädt.

**Erfassungslücken.** Eine Lücke entsteht für jeden Kanal mit mindestens einer aktiven Belohnung,
wenn die Chat-Verbindung getrennt war (von der letzten empfangenen Zeile bis zum abgeschlossenen
Rejoin, Quelle ist der vorhandene Reconnect-Pfad), wenn der Worker neu startete (von
`LastAliveAtUtc` bis zum abgeschlossenen Join) oder wenn der Kanal nicht gejoint war. Lücken unter
60 Sekunden werden nicht gespeichert. Die Oberfläche zeigt Lücken der letzten 14 Tage mit dem
Hinweis, in Twitchs Warteschlange nachzusehen und von Hand nachzutragen.

**Api** (Kanal-Gruppe aus K1; alle Routen 404 `wish_capture_disabled` bei ausgeschaltetem Schalter,
nach dem Filter):

| Route | Rate-Limit | Zweck |
|---|---|---|
| `GET …/wishes/rewards` | `InteractiveRead` | Belohnungen, Stand des Anlernens samt Kandidat, Lücken der letzten 14 Tage |
| `POST …/wishes/rewards/learning` | `Bookkeeping` | Anlernen starten (10 Minuten); 409 `wish_learning_active` |
| `POST …/wishes/rewards/learning/confirm` `{ "label": "…", "defaultDurationDays": 7 }` | `Bookkeeping` | Kandidat als Belohnung speichern; 409 `wish_reward_no_candidate`, 409 `wish_reward_already_known`, 409 `wish_reward_limit` |
| `DELETE …/wishes/rewards/learning` | `Bookkeeping` | abbrechen |
| `PATCH …/wishes/rewards/{rewardId}`, `DELETE …/{rewardId}` | `Bookkeeping` | Bezeichnung, Standardlaufzeit, pausieren; vergessen |
| `POST …/wishes/{id}/approve` `{ "sevenTvEmoteId": null, "emoteName": null, "alias": null, "durationDays": null }` | `Bookkeeping` | Freigeben; ohne bekannte Emote-ID 400 `wish_emote_required` |
| `POST …/wishes/{id}/reject`, `/unlink` | `Bookkeeping` | Ablehnen; Zuordnung lösen |

Schreibende Belohnungs-Routen sind Kanal-Vorgaben und laufen über den
`WishSettingsAuthorizationFilter` aus K1 (im Bot-Modus nur Broadcaster, Aktivierender, Admin).
`adopt` aus K1 nimmt mit `wishId` einen offenen Wunsch auf. `PATCH /{id}` ist auch in `pending`
erlaubt. `WishDto` bekommt `requestText`, `requestedName`, `matchKind`, `rewardLabel`, `linkOrigin`.
`/permissions` bekommt `wishCaptureEnabled`. Audit: `wish.approve`, `wish.reject`, `wish.autoLink`,
`wish.unlink`, `wish.rewardAdd`, `wish.rewardChange`, `wish.rewardRemove` (Details ohne Text, ohne
Bezeichnung, ohne Einlöser).

**Oberfläche** (in den Dateien aus K3): Bereich „Offen“ oben in der Arbeitsliste mit
Einlösetext, Zuschauer, Belohnung, Alter, Hinweis und den Aktionen Freigeben, Ablehnen, „Bestand
übernehmen“; bei einem automatisch zugeordneten Wunsch die Aktion „Zuordnung lösen“. Im
Settings-Abschnitt „Wünsche“ die Belohnungsliste mit Bezeichnung, Standardlaufzeit, „zuletzt
erkannt“ und dem Anlern-Ablauf: starten, Restzeit, Kandidat mit Text und Login zur Bestätigung,
Bezeichnung und Laufzeit eingeben, bestätigen. Der Dialog sagt, dass nur Belohnungen mit
Texteingabe erkannt werden. i18n `wishes.pending.*`, `channelSettings.wishes.rewards.*`,
`errors.api.wish_*`, `audit.actions.wish.*`.

## Akzeptanzkriterien
46. **AK46** Live-Probe (P1-Rest): **erledigt, bestanden am 2026-11-17.** Ein Konto ohne Mod- und
     VIP-Status (`mod=0`, `user-type` leer, nur Abo-Abzeichen) hat im Probe-Kanal eine Belohnung mit
     Texteingabe eingelöst; auf der anonymen Verbindung kam die PRIVMSG mit derselben festen
     `custom-reward-id` wie in P1, mit Nachrichten-`id` und Text an, ohne `source-room-id`. Der
     Text war genau ein 7TV-Emote-Link mit 26-stelliger ID. Offen bleibt nur: das Ergebnis als
     englischen DECISIONS-Eintrag festhalten. Die Prüfung bleibt Regressionsfall der
     Live-Verifikation (AK70).
47. **AK47** `RewardRedemptionParser`: aufgezeichnete Rohzeile mit Tag → Beobachtung mit allen
     sieben Feldern; ohne Tag → nichts; mit `source-room-id` ungleich Raum → nichts; mit
     `source-room-id` gleich Raum → Beobachtung; ohne `source-room-id`, aber mit je einem der
     Marker `source-id`, `source-badges`, `source-badge-info` (drei Fälle, auch mit leerem Wert) →
     nichts.
48. **AK48** Ein Vertragstest hält fest, über welches TwitchLib-Feld der Tag gelesen wird, und
     bricht, wenn eine neue TwitchLib-Version ihn nicht mehr liefert. Dieselben Rohzeilen wie in
     `SharedChatRuleTwitchLibTests` (nur Quell-Marker, ohne `source-room-id`), um den Tag ergänzt,
     ergeben über die echte TwitchLib-Zerlegung keine Beobachtung, weder beim Anlernen noch als
     Einlösung.
49. **AK49** Die Emote-Zählung einer Einlöse-Nachricht ist dieselbe wie ohne K6.
50. **AK50** Anlernen: Start → die nächste Nachricht mit unbekannter Belohnung wird Kandidat, eine
     zweite ersetzt ihn nicht; Bestätigen legt die Belohnung an und löscht die Anlern-Zeile sofort.
     Nach 10 Minuten liefert `GET …/rewards` keinen Kandidaten mehr und `confirm` antwortet 409
     `wish_reward_no_candidate`, auch wenn die Zeile noch liegt; spätestens 11 Minuten nach dem
     Start ist sie samt Text gelöscht.
51. **AK51** Ohne laufendes Anlernen wird von einer Nachricht mit unbekannter Belohnung nichts
     gespeichert (keine Zeile in irgendeiner Tabelle).
52. **AK52** Zwei Belohnungen im selben Kanal mit 7 und 30 Tagen: Wünsche tragen die Laufzeit
     ihrer Belohnung, nicht den Kanal-Default. Elfte Belohnung → 409 `wish_reward_limit`.
53. **AK53** Dedupe: dieselbe `IrcMessageId` zweimal → ein Wunsch. Vierter offener Wunsch
     desselben Zuschauers bei Grenze 3 → verworfen, Zähler in der Logzeile.
54. **AK54** `WishTextParser`, getestet als Tabelle: Link allein mit 26-stelliger ID; Link mit
     24-stelliger Hex-ID; ohne Schema, mit `http://`, mit `www.`, mit `old.`, Host in
     Großbuchstaben; mit Query-String, mit Fragment, mit abschließendem Schrägstrich, Punkt oder
     Klammer; Link mitten im Satz; zwei Links (erster zählt) ergeben je `link` mit genau der ID.
     Eine ID mit 23, 25 oder 27 Zeichen, ein anderer Host und ein anderer Pfad ergeben keinen
     Link. Für Namen jede Zeile der Beispieltabelle aus K6 (Eingabe → `MatchKind` und Ergebnis).
55. **AK55** Zuordnung über Link: offener Wunsch, danach taucht ein Slot mit dieser Emote-ID auf,
     gleich ob durch Vollsync, Delta oder manuelles Slot-Schreiben → `active`, `StartsAtUtc` =
     `AddedAt` des Slots, `LinkOrigin = auto`, `BotOwned` falsch, ein Audit-Eintrag `wish.autoLink`.
56. **AK56** Zuordnung über Namen: genau ein neuer Slot mit gleichem Alias → `active`, Emote-ID
     aus dem Slot; zwei neue Slots, die nur in der Groß- und Kleinschreibung abweichen → bleibt
     `pending` mit `ambiguous_match`.
57. **AK57** Kein Treffer außerhalb des Fensters: Slot mit `AddedAt` vor `CreatedAtUtc` des
     Wunsches, auch um eine Sekunde (keine Toleranz, 6.3) → `pending` mit
     `already_in_set`; Slot taucht nach dem Verfall auf → Wunsch bleibt `expired`.
58. **AK58** Zwei offene Wünsche für dasselbe Emote, ein neuer Slot: der ältere wird `active`,
     der jüngere bleibt `pending` mit `already_running`; dessen Freigabe verlängert den laufenden
     und endet als `merged` mit `MergedIntoWishId` (E13).
59. **AK59** Freigeben ohne Emote-ID bei `MatchKind = name` → 400 `wish_emote_required`; mit ID →
     `approved`. Ablehnen → `rejected`, `EndedAtUtc` gesetzt. Zwei gleichzeitige `approve` → einmal
     200, einmal 409.
60. **AK60** Verfall: `PendingExpiryDays = 14`, Alter 15 Tage → `expired`; `PendingExpiryDays = 1`,
     Alter 23 h bleibt `pending`, 25 h verfällt.
61. **AK61** `unlink` setzt einen automatisch zugeordneten Wunsch nach `pending` zurück und leert
     genau die in K6 genannten Felder (Start, Ablauf, Slot, Herkunft, Eigentum, Einreihung);
     `EndedAtUtc` bleibt null. Der Slot bleibt im Set; der nächste Abgleich ordnet ihn diesem
     Wunsch nicht erneut zu, auch nach einem Worker-Neustart (Zeile in `WishSlotExclusions`), wohl
     aber einem anderen offenen Wunsch desselben Emotes. `unlink` auf einen von Hand übernommenen
     oder vom Bot bestätigten Wunsch → 409.
62. **AK62** Lücken: getrennte Verbindung von 5 Minuten → eine Zeile mit `disconnect` für jeden
     Kanal mit aktiver Belohnung, keine für Kanäle ohne; 30 Sekunden → keine Zeile; Worker-Neustart
     → `worker_restart` ab `LastAliveAtUtc`. Retention löscht Lücken nach 30 Tagen.
63. **AK63** Filter-Matrix: Belohnungs-Routen 403 für Nicht-Verwalter, 404
     `wish_capture_disabled` für Verwalter bei ausgeschaltetem Schalter, im Bot-Modus 403
     `wish_settings_restricted` für einen Twitch-Mod, der nicht aktiviert hat.
64. **AK64** `requestText` und `requesterLogin` erscheinen nur in Antworten der Verwalter-Routen,
     nie in Audit-Details, nie in Logs; Retention und Kontolöschung leeren `RequestText` zusammen
     mit den Einlöser-Feldern (AK17, AK18 erweitert).
65. **AK65** `Wishes:Capture:Enabled = false`: der Manager erzeugt keine Beobachtung, der Dienst
     verarbeitet keine, bestehende Wünsche laufen weiter, der Haushalts-Takt läuft weiter. Der
     Worker hat mit K6 sechzehn Hosted Services.
66. **AK66** Aufräumen unabhängig vom Schalter (E26): Kandidat liegt vor, dann
     `Wishes:Capture:Enabled = false` und Worker-Neustart → nach dem ersten Takt ist die Zeile
     gelöscht; dasselbe mit `Wishes:Enabled = false`. Worker steht 30 Minuten still, während ein
     Kandidat abläuft → die Api zeigt ihn in dieser Zeit nicht, der erste Takt nach dem Start
     löscht ihn. Ein offener Wunsch jenseits der Verfallsfrist verfällt auch bei ausgeschalteter
     Erfassung.
67. **AK67** Leerer Emote-Cache (Sol-Befund 7): Mit einem leeren Snapshot für den Kanal ergibt
     eine Nachricht mit Tag eine Beobachtung, im Anlernen wie als Einlösung; eine Nachricht eines
     ausgeschlossenen Chatters ergibt keine. Geprüft auf der Ebene, auf der die Reihenfolge
     Ausschluss, Erfassung, Cache-Prüfung liegt; gibt es dort keinen Test, wird diese Reihenfolge
     in eine reine Funktion gezogen, die der Handler aufruft. Zusätzlich live in einem Kanal mit
     leerem Set (AK70).
68. **AK68** Kanal-Zusammenführung (Sol-Befund 9): Verlierer ohne Emotes mit zwei Belohnungen,
     einem offenen Wunsch und laufendem Anlernen, Überlebender mit einer der beiden Belohnungen →
     nach dem Zusammenführen hat der Überlebende beide, jede `RewardId` genau einmal, der Wunsch
     zeigt auf eine vorhandene Belohnung, die Zeile des Verlierers ist gelöscht, und eine
     anschließende Einlösung im überlebenden Kanal erzeugt einen Wunsch.
69. **AK69** Oberfläche: Anlernen zeigt den Kandidaten mit Text zur Bestätigung und speichert erst
     auf Bestätigen; der Bereich „Offen“ zeigt je Hinweis die passende Aktion; `wish.changed`
     aktualisiert beides ohne Reload (E2E über `__emitLive`).
70. **AK70** Live-Verifikation Gesamtweg (Regel 16) auf dem lokalen Stack im Probe-Kanal:
     Belohnung anlernen, einlösen mit 7TV-Link, Emote **direkt in 7TV** hinzufügen, nach dem
     nächsten Sync steht der Wunsch ohne Handgriff auf `aktiv` mit dem `added_at` aus 7TV;
     wiederholen mit einem Namen statt Link und einmal mit dem Hinzufügen aus der Karte. Dabei
     als Regressionsfälle: Einlösung durch ein Konto ohne Mod- und VIP-Status (AK46) und
     Einlösung in einem Kanal mit leerem Emote-Set.

## Testplan

| Ebene | Projekt | Was | Anzahl |
|---|---|---|---|
| Unit | `Worker.Tests` | `RewardRedemptionParser`, Vertragstest zum Tag, Warteschlangen-Überlauf | +10 |
| Unit | `Infrastructure.Tests/Unit` | `WishTextParser` (Tabelle), `WishSlotMatcher` mit `pending` und Namens-Treffern | +22 |
| Integration | `Infrastructure.Tests/Integration` | `WishCaptureService` (Anlernen, Dedupe, Grenzen, Verfall, Lücken), Abgleich AK55 bis AK58, AK61, Retention | +28 |
| Api | `Api.Tests` | Filter-Matrix AK63 | +8 |
| Vitest | `core/wishes`, Komponenten | Dienst-Routen; Aktionsliste je Hinweis, Anlern-Zustände | +12 |
| Playwright | `web/e2e/wishes-capture.e2e.spec.ts` | Anlernen bis Bestätigung; offener Wunsch wird nach Ereignis `aktiv`; Ablehnen | +3 |
| Live | Probe-Kanal | AK46, AK70; die Transportklasse wird live statt gegen Fakes verifiziert (Regel 11) | 2 Durchgänge |

## Files Reference

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Worker/TwitchChatManager.cs:1063-1094` | Tag lesen nach dem Ausschluss-Filter und vor der Rückkehr bei leerem Cache; Beobachtung in die Warteschlange |
| `src/EmotePurge.Infrastructure/Services/ChannelIdentityService.cs:902-1009` | Belohnungen, Anlernzustand und Lücken beim Zusammenführen mitnehmen |
| `src/EmotePurge.Worker/WishCapture/RewardRedemptionParser.cs`, `WishCaptureWorker.cs`, `RewardObservationQueue.cs` | neu |
| `src/EmotePurge.Worker/TwitchConnectionWatchdog.cs`, `WorkerServiceRegistration.cs:56-70` | Lückenquelle, Registrierung |
| `src/EmotePurge.Core/Chat/SharedChatRule.cs:23` | unverändert genutzt |
| `src/EmotePurge.Core/Entities/ChannelWishReward.cs`, `WishRewardLearning.cs`, `WishCaptureGap.cs`, `WishCaptureState.cs`, `WishSlotExclusion.cs` | neu |
| `src/EmotePurge.Core/Entities/Wish.cs`, `ChannelWishSettings.cs` | Spalten aus 6.6 |
| `src/EmotePurge.Core/Services/WishTextParser.cs`, `IWishCaptureService.cs`, `WishSlotMatcher.cs`, `WishTransitions.cs` | neu bzw. um `pending` und Namens-Treffer erweitert |
| `src/EmotePurge.Core/Messaging/BotCommands.cs:22` | `WishCapturePrefix = "WISHCAPTURE:"` |
| `src/EmotePurge.Infrastructure/Services/WishCaptureService.cs`, `WishService.cs`, `WishReconcileService.cs` | neu bzw. erweitert |
| `src/EmotePurge.Infrastructure/Migrations/*_AddWishCapture.cs` | neu |
| `src/EmotePurge.Infrastructure/Services/DataRetentionService.cs`, `AccountDeletionService.cs` | `RequestText`, Lücken |
| `src/EmotePurge.Api/Endpoints/WishEndpoints.cs`, `Validation/ApiErrorCodes.cs` | Routen; `wish_capture_disabled`, `wish_learning_active`, `wish_reward_no_candidate`, `wish_reward_already_known`, `wish_reward_limit`, `wish_reward_not_found`, `wish_emote_required` |
| `web/src/app/features/wishes/*`, `features/channel-workspace/wish-settings-section.ts`, `core/wishes/*` | Bereich „Offen“, Anlernen, Lücken |
| `web/public/i18n/*.json`, `web/src/app/core/i18n/api-error.ts`, `web/e2e/wishes-capture.e2e.spec.ts` | Schlüssel, Codes, E2E |
| `docker-compose*.yml`, `.env.example` | `WISHES_CAPTURE_ENABLED` an `api` und `worker` |
| `docs/DECISIONS.md`, `docs/Architectur.md`, `docs/Operations.md`, `CLAUDE.md` | englische Einträge zur Erfassung und zum Probe-Ergebnis AK46; Worker-Liste |

## Abhängigkeiten

K1, K3. Für die Live-Verifikation: Probe-Kanal mit einer Belohnung mit
Texteingabe und ein Zuschauer-Konto ohne Mod-Status für AK46.

---

# K4: Bot-Backend

Labels: `feat`, `worker`, `api`, `infrastructure`, `migration`, `security`. Hängt ab von K1, K2.

## Ziel

Der Worker schreibt mit dem Token des Bot-Kontos nach 7TV: hinzufügen nach Freigabe, entfernen bei
Ablauf, beides nur innerhalb der Grenzen aus Abschnitt 7 und 8.

## Umfang

Migration `AddBotAccount` (6.4), Versiegeln und Öffnen des Tokens, Admin-Endpunkte und Admin-Seite,
Schreib-Client, `BotAccountWorker`, Editor-Prüfung, Eigentum, Reparatur, Rückfall, Notaus, Doku. Die
Admin-Seite ist bewusst hier und nicht in K5: Ohne sie lässt sich nichts live prüfen. Was zum Bot-Konto gehört und nicht zum
Ablauf der Wünsche, heißt neutral (`BotAccount…`), damit eine spätere Nutzung des Kontos außerhalb der Wünsche (F11) nicht
verbaut ist; gebaut wird sie nicht, und sie bräuchte eigene Sicherheitsregeln und eine eigene Zustimmung (Plan-487, 1.5 und
Tabelle „Revision 5a“).

## Implementation Details

**Token:** `IBotAccountTokenSealer` (nur in der Api registriert, braucht den öffentlichen Schlüssel)
und `IBotAccountTokenOpener` (nur im Worker). Zwei getrennte Registrierungs-Einstiege nach dem Muster
`AddLogCapture`, nie aus `AddEmotePurgeInfrastructure`. Fehlt der jeweilige Schlüssel bei
`BotAccount:Enabled = true`, startet der Prozess nicht (Fail-Fast mit englischer Meldung).

**Admin-Api** (`/api/admin/bot-account`, `GlobalAdminAuthorizationFilter`, kein Rate-Limit wie die
Gruppe):

| Route | Antwort |
|---|---|
| `GET /` | `{ tokenPresent, status, expiresAtUtc, expiresInDays, botSevenTvUserId, lastVerifiedAtUtc, lastErrorCode, killSwitch, pausedUntilUtc, wishes: { queue: { adds, confirms, removals }, botChannels } }` |
| `PUT /token` `{ "token": "…" }` | 204; 400 `bot_account_token_malformed`, 400 `bot_account_token_expired`, 404 `bot_account_disabled` |
| `DELETE /token` | 204 |
| `PUT /kill-switch` `{ "enabled": true }` | 204 |

Keine Route gibt das Token oder das Siegel zurück. Audit: `botAccount.tokenSet`,
`botAccount.tokenDelete`, `botAccount.killSwitch` (Details nur `expiresAtUtc` bzw. `enabled`).

**Kanal-Api** (Gruppe aus K1): `GET /api/channels/{c}/wishes/bot` (`{ available, listed, enabled,
activationPending, activationRequestedAtUtc, activationError, canActivate, canDeactivate,
suspendedReason, editorCheckedAtUtc, activatedByLogin, activatedAtUtc, botSevenTvUserId }`;
`activationPending` ist wahr zwischen `activate` und der Bestätigung des Workers,
`activationError` ist `not_editor`, wenn der Worker den Bot nicht als Editor fand, und beendet das
Warten; `canActivate` und `canDeactivate` spiegeln E24 und D7 für den Aufrufer, die Route bleibt
die Prüfung; **seit G0:** `activationPending` bleibt wahr, solange der Worker eine frische Einladung
noch nicht sieht (bis 2 Minuten nach der Anfrage) oder sie gerade annimmt; `activationError` kann
zusätzlich `invitation_not_accepted` sein; `suspendedReason` zusätzlich `invitation_pending`. Beides
sind Feldwerte, keine neuen Fehlercodes),
`POST …/wishes/bot/activate` (Filterfolge: `ChannelNameValidationFilter` →
`ChannelManagementAuthorizationFilter` → neuer `WishBotActivationAuthorizationFilter`, E24. Der
erste Autorisierungsfilter lehnt jeden ab, der den Kanal nicht verwaltet, auch einen 7TV-Editor
ohne Twitch-Rolle (403). Der zweite lässt von den Verwaltern nur durch: den Broadcaster, einen
globalen Admin, und einen Twitch-Mod, für den `ISevenTvEditorService.GetEditorGrantsAsync` mit
seiner Twitch-ID `Ok` antwortet und dessen Grants die Twitch-ID dieses Kanals enthalten.
`NoSevenTvAccount` oder Kanal nicht enthalten → 403 `wish_bot_activation_forbidden`;
`Unavailable` → 503, nie ein Durchlassen. Ein reiner Twitch-Mod
ist damit ausgeschlossen, auch wenn der Bot schon Editor ist. Dazu die Pilot-Liste, E8: 403
`wish_bot_channel_not_listed`. Ist der Bot-Modus schon aktiv: 409 `wish_bot_already_active`, der
gespeicherte Aktivierende wird nie überschrieben; er ändert sich nur durch Abschalten und eine
neue, wieder geprüfte Aktivierung. Die Route setzt eine Prüfanforderung und stößt den Worker an;
aktiv wird der Modus erst, wenn der Worker den Editor-Status des Bots bestätigt hat; 409
`wish_bot_unavailable`, wenn Token oder Schalter fehlen), `DELETE …/wishes/bot` (Broadcaster,
Aktivierender, globaler Admin über `WishSettingsAuthorizationFilter`; beendet jedes Eigentum im
Kanal, laufende Wünsche werden Erinnerungsfälle). `/permissions` bekommt `wishBotModeAvailable` (Schalter an, Token gültig, kein Notaus, und Kanal
gelistet oder Aufrufer globaler Admin).
Audit: `wish.botActivate`, `wish.botDeactivate`.

**Schreib-Client:** `ISevenTvWriteClient` (Core) und Implementierung (Infrastructure), eigener
typisierter HttpClient gegen den v4-GraphQL-Endpunkt, 10 s Timeout, nur im Worker registriert.
Operationen: Set-Einträge lesen mit `alias`, `addedAt`, `addedById`, Emote-ID (alle Seiten, sonst
Fehler, fail-closed); `addEmote(id: { emoteId, alias })`; `removeEmote(id: { emoteId, alias })`
(Alias immer gesetzt); Token prüfen und eigene 7TV-ID lesen. Fehlerformen wie im Lesepfad:
verkleidetes 429 (HTTP 200, `extensions.status`), HTTP 429 mit `Retry-After`, 401/403, Timeout.
Telemetrie über den vorhandenen Provider-Handler mit eigener Aufrufquelle.
**Gemessen in G0 (2026-11-24, Protokoll `docs/nachtlaeufe/live-487-probe-2026-11-24.md`):** Prüfabfrage
`users.me { id }` (Claim `sub` = dieselbe ID, Laufzeit 30 Tage); `addEmote` nimmt die Rückgabe-Auswahl
`emotes(page, perPage) { items { alias addedAt addedById emote { id } } }` am zurückgegebenen Set an und liefert
den neuen Eintrag mit; Antwortzeit bis 4,4 s, das Zeitlimit von 10 s bleibt. Alias-Konflikt = HTTP 200,
`extensions.status = 409` (`code = BAD_REQUEST`). Fehlende Berechtigung = HTTP 200, `LACKING_PRIVILEGES`,
`extensions.status = 403`, mit zwei Meldungen (kein Editor; Einladung noch offen); zugeordnet wird **nur über den
Code**, nie über den Text. Entwertetes Token = echtes HTTP 401 mit Körper außerhalb des Schemas. Volles Set und
entferntes Emote waren nicht herstellbar und bleiben `unknown`. Rate-Limit-Eimer `emote_set_change`: 100 je 60 s.
Dazu eine Operation **Einladung annehmen** (`userEditors.editor(editorId: <Bot>, userId: <Inhaber>).updateState(state:
ACCEPT)`, mit dem Bot-Token); der Client kennt keinen anderen Zustand, kann also weder ablehnen noch austragen.

**`BotAccountWorker`** (Hosted Service, konkrete Klasse): eine Schleife unter Postgres-Advisory-Lock
(eigener Schlüssel, Muster `ChatLogBackfillService.cs:58-76`), streng seriell über alle Kanäle,
`WriteDelaySeconds` Abstand. Die Warteschlange ist **abgeleitet**, keine eigene Tabelle: nächster
Wunsch mit fälliger Bot-Aktion (`approved` in einem Bot-Kanal, `unconfirmed` mit
`BotNextAttemptAtUtc` ≤ jetzt, `active` **oder `presented`** mit `RemovalQueuedAtUtc`), älteste zuerst (F7). Geweckt
durch `BOTACCOUNT:` und einen 30-Sekunden-Takt.

Vor **jedem** Schreiben, in dieser Reihenfolge: Schalter, Pilot-Liste (E8), `KillSwitch`, `PausedUntilUtc`,
Token-Status, Editor-Prüfung des Kanals (frisch, ohne `ModRoleCache`; innerhalb einer Minute je
Kanal wiederverwendbar). Fällt eine Prüfung durch, schreibt der Worker nicht und wendet den Rückfall
aus Abschnitt 7 Punkt 8 an.

**Editor-Prüfung und Einladungen (Ergebnis G0 und Betreiberentscheid, beide 2026-11-24):** 7TV trägt einen
eingeladenen Editor zuerst als `PENDING` ein; schreiben darf das Bot-Konto erst, wenn es selbst angenommen hat
(`ACCEPTED`). Daraus folgt:

- **Was als Editor gilt:** nur ein v4-Editor-Eintrag mit `state = ACCEPTED` **und** `permissions.emoteSet.manage`.
  Gelesen wird öffentlich über `user(Bot).editorFor` (alle Kanäle in einem Read, Ein- und Austragen binnen
  einer Minute sichtbar). `users.me.editableEmoteSetIds` ist **kein** Signal (enthält das Set schon bei
  `PENDING`); v3 `editor_of` dient dem Bot nicht mehr (listet erst nach der Annahme). Ein Eintrag ohne `manage`
  gilt als nicht Editor.
- **Wann der Bot annimmt:** nur für einen Kanal, dessen Bot-Modus gerade aktiviert wird (offene
  Aktivierungsanfrage), oder bei der Editor-Prüfung eines Kanals, dessen Bot-Modus schon aktiviert ist (etwa
  nachdem ein Verwalter den Bot neu eingeladen hat); in beiden Fällen nur bei Deckung durch die Pilot-Liste (E8)
  und global verfügbarem Bot. **Jede andere Einladung bleibt unbeantwortet:** nie annehmen, nie ablehnen.
- **Wer annimmt:** der Worker, unter denselben Schranken wie jedes Schreiben (Advisory-Lock, Schalter, Pilot-Liste,
  `KillSwitch`, Pause, Token-Status) und unter der Kanalsperre, damit ein Abschalten oder eine neue Anfrage nicht
  dazwischenfällt. Die Api ruft 7TV nie mit dem Token; `activate` setzt nur die Anfrage, die die Annahme erlaubt.
  Die Annahme ist ein Schreiben in die Editor-Beziehung, nicht in ein Set, und idempotent: ist die Einladung schon
  angenommen, wird nichts gesendet.
- **Ergebnisse:** angenommen → Audit `wish.botAcceptInvitation` (System), sofortiges Neu-Lesen, dann Bestätigung
  bzw. Wiederaufnahme wie bisher. 429 → Pause; 401 → Token `invalid`, globaler Rückfall; Netzfehler → der nächste
  Read entscheidet; abgelehnt → beim Aktivieren `activationError = invitation_not_accepted`, im aktivierten Kanal
  `BotSuspendedReason = invitation_pending` und ein neuer Versuch frühestens nach 15 Minuten.
- **Frische Einladung:** v4 zeigt eine neue Einladung erst nach bis zu rund 30 Sekunden; eine Anfrage ohne
  sichtbaren Eintrag bleibt deshalb 2 Minuten offen, bevor `not_editor` gilt.
- **Schreibfehler:** ein `LACKING_PRIVILEGES` beim Schreiben setzt den Kanal wie bisher auf `not_editor` (AK80),
  gleich welche Meldung; ob stattdessen eine Einladung offen ist, entscheidet der nächste Editor-Read.

Abläufe:

- **Hinzufügen:** Zuerst die Absicht festschreiben: ein bedingtes Update auf `Status = approved`
  und `BotWriteAtUtc IS NULL` setzt `BotWriteAtUtc` und erhöht `DecisionVersion`, in eigener
  Transaktion **vor** jedem 7TV-Aufruf. Trifft es keine Zeile (der Abgleich oder ein Verwalter war
  schneller), schreibt der Bot nicht. Danach Vor-Read; Emote-ID schon im Set → `BotWriteAtUtc`
  leeren, `presented` (`already_in_set`), kein Schreiben. Sonst `addEmote`; Erfolg →
  `unconfirmed`; Ablehnung → `BotWriteAtUtc` leeren, `failed` mit Grund, kein Retry. 429 →
  `BotWriteAtUtc` leeren, `PausedUntilUtc`, der Wunsch bleibt `approved`. Findet der Bot nach
  einem Absturz einen `approved` mit gesetztem `BotWriteAtUtc`, behandelt er ihn wie
  `unconfirmed` und fügt nicht erneut hinzu.
- **Koordination mit dem Abgleich:** Der Abgleich (6.3) fasst einen Wunsch mit gesetztem
  `BotWriteAtUtc` in `approved` oder `unconfirmed` nicht an; nur der Bestätigungsweg des Bots führt
  ihn nach `active`. Damit gewinnt immer genau einer: Hat der Abgleich den Wunsch schon auf
  `active` gesetzt, scheitert das Festschreiben der Absicht, der Bot schreibt nicht, und der
  Wunsch ist ein Erinnerungsfall ohne Eigentum (das Emote kam nicht vom Bot). Hat der Bot die
  Absicht festgeschrieben, wartet der Abgleich, und das Eigentum entscheidet sich allein am
  `addedById` des Bestätigungs-Reads. Den Slot zu einem vom Bot bestätigten Wunsch verknüpft der
  Abgleich nachträglich: `active` ohne `SlotId` mit `BotOwnedAddedAtUtc` oder `StartsAtUtc` wird
  dem Slot gleicher Emote-ID und gleichen Alias zugeordnet, ohne Zustand oder Eigentum zu ändern.
- **Bestätigen:** nach E11. Genau ein Eintrag mit Emote-ID und Alias: `addedById` = Bot → `active`
  mit Eigentum und maßgeblichem Start; sonst `active` ohne Eigentum. Kein Eintrag nach 15 min →
  `presented` (`unconfirmed`). Seit G0 (AK89) ersetzt die Rückgabe von `addEmote` das Lesen direkt
  nach dem Schreiben; trägt sie den Eintrag nicht, beginnt der Plan mit dem Read wie bisher.
- **Entfernen:** Zeile des Wunsches sperren und die Berechtigung erneut prüfen (Abschnitt 8, E25):
  Version, Zustand, Ablauf, Eigentum. Nicht mehr berechtigt → nichts senden,
  `RemovalQueuedAtUtc` leeren. Sonst frischer Read; vier Merkmale exakt → `removeEmote`;
  Nach-Read; Wunsch → `removed`, alles unter derselben Sperre. Abweichung → `presented`
  (`ownership_lost`). Eintrag fehlt schon → `removed_externally`. Reparaturfall und
  Erkennungsgrenze nach Abschnitt 8.
- **Entscheidungen verwerfen die Einreihung (E25):** `extend`, `keep`, `close`,
  `complete-removal`, `re-add`, `PATCH`, das Abschalten des Bot-Modus und jede Eigentumsänderung im
  Abgleich erhöhen `DecisionVersion` und leeren `RemovalQueuedAtUtc` und `RemovalQueuedVersion` im
  selben Update. Das gilt ab K1 für alle Routen; K4 ergänzt die Prüfung im Bot.
- **„Bot entfernen lassen“** für vorgelegte Wünsche: neue Route `POST …/{id}/bot-remove`, jeder
  Verwalter (Kanal-Filter aus K1), nur im Zustand `presented` mit Grund `protected`,
  `coverage_gap` oder `bot_unavailable`, nur bei maßgeblichem Start (`StartsAtAuthoritative` wahr,
  sonst 409; den Code legt K4 fest), nur bei bestehendem Eigentum (sonst 409
  `wish_not_bot_owned`) und nur bei verfügbarem Bot (sonst 409 `wish_bot_unavailable`). Sie setzt
  `RemovalQueuedAtUtc` und `RemovalQueuedVersion`, lässt den Zustand `presented`, schreibt
  `wish.botRemoveRequest` mit dem Verwalter als Akteur und stößt den Worker an. Der Bot nimmt den
  Wunsch aus der Warteschlange wie eine fällige Entfernung; der Ablauf muss dafür nicht erneut
  geprüft werden, der Auftrag ist die Entscheidung. Ergebnis `removed`; scheitert die Prüfung des
  Eigentums, bleibt er `presented` mit Grund `ownership_lost`. Die K1-Route `complete-removal`
  bleibt dem manuellen Weg vorbehalten.

Audit unter `AuditActor.System`: `wish.botAdd`, `wish.botRemove`, `wish.botRepair`, `wish.botSuspectRemoval`,
`wish.botSuspend` (Kanal verliert Editor-Rechte), seit G0 `wish.botAcceptInvitation` (Einladung angenommen). Token-Zustände `invalid` und `expired` schreiben
je Wechsel einen Eintrag `botAccount.tokenState`.

**Token-Ablauf:** Der Worker setzt `expired`, sobald `exp` erreicht ist, und loggt ab 7 Tagen
vorher höchstens eine Warnung je UTC-Tag.

**Admin-Seite** (`web/src/app/features/admin/`, Route `admin/bot-account`): Zustand, Ablauf mit
Hinweis ab 7 Tagen, Eingabefeld (Typ Passwort, wird nach dem Senden geleert, nie vorbelegt),
Löschen mit `typed-confirm-dialog`, Notaus-Schalter, Warteschlangentiefe, Zahl der Bot-Kanäle.

## Akzeptanzkriterien
71. **AK71** Migration `AddBotAccount` additiv; genau eine Zeile möglich (zweites Insert scheitert am
    CHECK).
72. **AK72** Versiegeln und Öffnen: Rundlauf liefert das Original; ein verändertes Byte, ein
    falscher privater Schlüssel und ein unbekanntes Präfix liefern „kein Token“, keine Ausnahme mit
    Inhalt. Die Api-Assembly-Registrierung enthält keinen `IBotAccountTokenOpener` (Test über den
    Service-Provider der `WebApplicationFactory`).
73. **AK73** `PUT /token`: kein JWT → 400 `bot_account_token_malformed`; `exp` in der Vergangenheit →
    400 `bot_account_token_expired`; gültig → 204, `Status = unverified`, ein `BOTACCOUNT:`-Kommando,
    ein Audit-Eintrag ohne Token. Kein Admin → 403; Schalter aus → 404 `bot_account_disabled` nach dem
    Filter.
74. **AK74** Keine Antwort irgendeiner Route enthält Token oder Siegel (Test durchsucht alle
    Antworten der Admin- und Kanal-Routen nach dem gesetzten Wert).
75. **AK75** Das Token erscheint in keinem Log: Test mit Log-Erfassung auf Trace-Stufe über Setzen,
    Prüfen, Hinzufügen, Entfernen und einen 401-Fehler; weder Token noch `Bearer` plus Wert in
    irgendeiner Zeile.
76. **AK76** Schreib-Client gegen einen lokalen Stub: verkleidetes 429 → Pause mit 60 s Default,
    `Retry-After` wird übernommen und bei 6 h gekappt; 401 → Token `invalid`, alle Bot-Kanäle im
    Rückfall; unvollständige Seitenfolge beim Lesen → Fehler, kein Schreiben.
77. **AK77** Eigentum (alle Fälle aus dem Entwurf, R3-5): Bestätigungs-Read mit `addedById` = Bot →
    Eigentum; fremd → `active` ohne Eigentum; fehlend → ohne Eigentum; kein Eintrag bis 15 min →
    `presented` (`unconfirmed`); Abweichung je eines der vier Merkmale im frischen Read → kein
    `removeEmote`; Umbenennen außerhalb → Eigentum endet; Entfernen und Hinzufügen außerhalb → kein
    Eigentum am Nachfolger; „wieder hinzufügen“ über den Bot → neue eigene Generation; zweiter Alias
    bleibt nach dem Entfernen im Set; Moduswechsel überträgt kein Eigentum; fehlender fremder
    Eintrag im Nach-Read → genau ein Wieder-Hinzufügen, `wish.botRepair`, `presented`
    (`repair_review`). **Erkennungsgrenze:** Der gestubbte Client ersetzt den Eintrag zwischen
    Vor-Read und `removeEmote` durch eine fremde Generation gleichen Alias → der Test hält fest,
    dass der Bot entfernt, dass **kein** `wish.botRepair` entsteht, und dass ein vorher per Sync
    gesehener Ersatz-Slot zu genau einem `wish.botSuspectRemoval` führt, ein nie gesehener zu
    keinem Eintrag.
78. **AK78** F12: Ein im Bot-Kanal von einem Verwalter per Browser hinzugefügter Wunsch-Eintrag
    wird bei Ablauf `presented` (`not_bot_owned`), der Worker sendet kein `removeEmote`.
79. **AK79** Rückfall global (R3-10): Token `invalid` → `approved` bleibt und zählt in
    `summary`; `unconfirmed` → `presented` (`unconfirmed`); `RemovalQueuedAtUtc` gesetzt →
    `presented` (`bot_unavailable`). Nach neuem gültigem Token nimmt der Worker nur `approved` auf.
80. **AK80** Rückfall je Kanal (R3-11): Editor-Prüfung schlägt fehl oder 7TV antwortet auf ein
    Schreiben mit fehlender Berechtigung → `BotSuspendedReason = not_editor`, ein Audit-Eintrag,
    andere Kanäle laufen weiter.
81. **AK81** Notaus: `KillSwitch = true` während eines laufenden Durchgangs → spätestens nach dem
    laufenden Request kein weiterer; Zustand wie AK79. Zwei Worker-Instanzen: nur die mit dem
    Advisory-Lock schreibt.
82. **AK82** Aktivieren prüft den Aufrufer (E24, Sol-Befund 1), an der echten Route: Bot ist
    Editor, ein Berechtigter schaltet ab, der Bot bleibt in 7TV Editor → direktes
    `POST …/wishes/bot/activate` durch einen reinen Twitch-Mod 403
    `wish_bot_activation_forbidden`, der Modus bleibt aus. Angenommen werden genau: Broadcaster,
    globaler Admin, Twitch-Mod mit bestätigtem 7TV-Editor-Grant für diesen Kanal. Ein
    7TV-Editor ohne Twitch-Rolle → 403 vom Verwalter-Filter. Editor-Auskunft `Unavailable` →
    503, `NoSevenTvAccount` → 403. Aktivieren bei aktivem Modus → 409 `wish_bot_already_active`,
    `BotModeActivatedBy*` unverändert.
83. **AK83** Bot gegen Abgleich: Ein Sync verknüpft einen `approved` (Mensch hat in 7TV
    hinzugefügt), bevor der Bot die Absicht festschreibt → der Bot sendet kein `addEmote`, Wunsch
    `active` ohne Eigentum. Der Bot hat die Absicht festgeschrieben, dann läuft ein Sync, der den
    neuen Slot sieht → der Abgleich ändert den Wunsch nicht; der Bestätigungs-Read setzt `active`
    mit Eigentum, der nächste Abgleich trägt nur `SlotId` nach. Absturz zwischen `addEmote` und
    `unconfirmed` → nach dem Neustart kein zweites `addEmote`. Einreihen, dann entscheiden (E25,
    Sol-Befund 2): Ablauf-Lauf reiht ein, danach
    `extend` → `RemovalQueuedAtUtc` ist null, der Bot sendet kein `removeEmote`, der Eintrag bleibt.
    Dasselbe für `keep`, `PATCH` der Laufzeit und Abschalten des Bot-Modus. Nebenläufig: `extend`
    wird festgeschrieben, nachdem der Bot den Wunsch ausgewählt, aber bevor er die Zeile gesperrt
    hat → die erneute Prüfung schlägt an (Version), kein `removeEmote`. `extend` trifft auf die
    gehaltene Sperre → wartet und antwortet nach dem Entfernen 409 `wish_state_conflict`.
84. **AK84** „Bot entfernen lassen“ von der Route bis zum Schreiben (Sol-Befund 4): `presented`
    (`protected`) mit Eigentum → `POST …/{id}/bot-remove` 202, Audit `wish.botRemoveRequest` mit
    dem Verwalter; der nächste Lauf des Bots sendet genau ein `removeEmote` mit Emote-ID und
    Alias, Wunsch `removed`, Audit `wish.botRemove`. Ohne Eigentum 409 `wish_not_bot_owned`; im
    Rückfall 409 `wish_bot_unavailable`; Grund `expired` oder `not_bot_owned` → 409
    `wish_state_conflict`; Nicht-Verwalter 403.
85. **AK85** Pilot-Liste (E8): Liste leer → `activate` durch Broadcaster und Mod 403
    `wish_bot_channel_not_listed`, durch einen globalen Admin erfolgreich; Kanal gelistet →
    die nach E24 Berechtigten erfolgreich, ein reiner Twitch-Mod weiterhin 403
    `wish_bot_activation_forbidden`; `*` → jeder Kanal, gleiche Berechtigten. Im Betrieb: Ein aktiver Bot-Kanal fällt aus der
    Liste → der nächste Lauf schreibt dort nicht, `BotSuspendedReason = not_listed`, offene
    Bot-Aktionen wie in AK79; `BotModeEnabled` und der Aktivierende bleiben gespeichert; wieder
    gelistet → der Bot nimmt ohne neue Aktivierung auf, was noch `approved` ist. Der manuelle
    Modus ist von der Liste in keinem Fall berührt.
86. **AK86** Live-Probe (Testkonto, Bot als Editor): nach `addEmote` durch den Bot zeigt der
    v4-Read `addedById` = 7TV-ID des Bot-Kontos. Ergebnis im PR und in DECISIONS festgehalten.
    **Fällt sie durch, entfällt das automatische Entfernen** (Bot fügt nur hinzu und erinnert), und
    der Betreiber entscheidet vor dem Merge.
    **Ergebnis 2026-11-24: bestanden** (`addedById` = Bot-ID in allen Fällen, öffentlich wie authentifiziert);
    das automatische Entfernen bleibt.
87. **AK87** Live-Probe: Nach dem Umbenennen des Bot-Eintrags durch einen Menschen zeigt der Read
    welches `addedById` und welches `addedAt`; Ergebnis festgehalten. Erwartung der Spec: Eigentum
    endet in jedem Fall über den Alias.
    **Ergebnis 2026-11-24:** `addedById` (Bot) und `addedAt` bleiben unverändert, der alte Alias ist weg; die
    Erwartung ist bestätigt, Eigentum endet nur über den exakten Alias.
88. **AK88** Live-Probe Sofort-Read: Zeit zwischen `addEmote`-Antwort und erstem v4-Read, der den
    Eintrag mit `addedAt` zeigt, an mindestens fünf Hinzufügungen gemessen. Über 5 Minuten in einem
    Fall → E11 neu festlegen, vor dem Merge.
    **Ergebnis 2026-11-24:** 0 s in allen fünf Fällen (auch öffentlich); E11 bleibt. Antwortzeit von `addEmote` bis
    4,4 s, das Zeitlimit von 10 s bleibt.
89. **AK89** Live-Probe: Was `addEmote` zurückgibt, wenn die Abfrage
    `emotes { items { alias addedAt addedById } }` am zurückgegebenen Set mit angefordert wird. Trägt
    die Antwort den neuen Eintrag, darf sie den ersten Bestätigungs-Read ersetzen; sonst bleibt es
    beim Read.
    **Ergebnis 2026-11-24: trägt ihn** (ganzes Set samt neuem Eintrag mit `addedAt` und `addedById`); die Rückgabe
    ersetzt den ersten Bestätigungs-Read, der Read bleibt Rückfall.
90. **AK90** Live-Probe Notaus: „Sign Out Everywhere“ am Bot-Konto → der nächste Schreibversuch des
    Workers endet in `invalid` und im Rückfall. Zusätzlich einmal geprüft und dokumentiert, ob ein
    normaler Logout in einer anderen Sitzung dasselbe bewirkt.
    **Ergebnis 2026-11-24: bestanden.** „Sign Out Everywhere“ → echtes HTTP 401, Token `invalid`; ein normaler
    7TV-Logout in einer anderen Sitzung entwertet das JWT nicht (Abschnitt 7 Punkt 6, RR5).
91. **AK91** Live-Probe Verschränkung und Fehlerformen: Bot fügt hinzu, Mensch entfernt, Mensch fügt
    denselben Alias hinzu → frischer Read zeigt fremdes `addedById`, Bot entfernt nicht. Dieselbe
    Folge **nach** dem Vor-Read lässt sich live nicht zuverlässig treffen (Fenster unter einer
    Sekunde); die Probe hält stattdessen fest, dass `removeEmote` mit Emote-ID und Alias einen
    von einem Menschen neu hinzugefügten Eintrag gleichen Alias tatsächlich entfernt, und
    bestätigt damit die Erkennungsgrenze aus Abschnitt 8. Dazu je ein echtes Hinzufügen mit
    Alias-Konflikt und (falls herstellbar) vollem Set: die Antwortformen sind den Gründen
    `alias_conflict` und `set_full` zugeordnet; was nicht herstellbar war, läuft als `unknown`
    und steht als offen im PR.
    **Ergebnis 2026-11-24:** `removeEmote` entfernt den Menschen-Eintrag gleichen Alias (`addedById` = Inhaber, neues
    `addedAt`), der Vergleich vor dem Entfernen erkennt den Fall; `alias_conflict` = HTTP 200 mit `extensions.status =
    409`; volles Set und entferntes Emote nicht herstellbar, beide `unknown` und offen im PR.
92. **AK92** Live-Probe Editor-Lesen: Die Editor-Prüfung erkennt das Bot-Konto nach dem Eintragen
    und nach dem Austragen binnen einer Minute korrekt, und es ist dokumentiert, ob 7TV eine
    Einladung kennt, die das Bot-Konto erst annehmen muss, und ob die Prüfung die nötige
    Berechtigung (Emote-Sets verwalten) unterscheiden kann.
    **Ergebnis 2026-11-24: bestanden, mit Folge.** Ein- und Austragen über v4 in 26 bis 37 s sichtbar; 7TV kennt eine
    Einladung (`PENDING`), die das Bot-Konto annehmen muss, und der Bot kann das per API; `permissions.emoteSet.manage`
    ist lesbar. Folge: „Editor-Prüfung und Einladungen“ oben, AK123 bis AK125.
93. **AK93** Live-Verifikation Gesamtweg lokal (Regel 16) auf dem Testkonto: Handeintrag →
    Bot fügt hinzu → `active` mit Eigentum; per Datenbank-Update im Dev-Stack den Ablauf in die
    Vergangenheit legen → nächster Takt entfernt, Wunsch `removed`, zwei Audit-Einträge. Das Set
    danach wiederherstellen.
94. **AK94** Im Pilot (Beobachtung, kein Merge-Tor): ein Wunsch mit 7 Tagen Laufzeit wird vom Bot
    hinzugefügt und nach Ablauf entfernt oder vorgelegt, ohne Handgriff dazwischen.
95. **AK95** DECISIONS (englisch, „unofficial, not confirmed by 7TV“), Architectur.md, PRODUCT.md
    und das Operations-Runbook sind im selben Commit wie die Token-Ablage geändert (Regel 3).
96. **AK96** Der Worker hat siebzehn Hosted Services; bei `BotAccount:Enabled = false`
    beendet sich `BotAccountWorker` nach einer Logzeile und öffnet nie das Token.

AK109 bis AK122 (automatische Erneuerung, Nachtrag 2026-11-20) stehen in Abschnitt 18.10.

**Nachtrag G0 (2026-11-24), fortlaufend nach AK122** (Editor-Prüfung und Einladungen, oben):

123. **AK123** Annahme nur nach dem Betreiberentscheid (Integration mit gestubbtem Editor-Read und Schreib-Client;
     live in der Verifikation AK93): offene Aktivierungsanfrage und Eintrag `PENDING` mit `manage` → genau eine Annahme,
     ein `wish.botAcceptInvitation`, danach im selben Durchgang `BotModeEnabled = true`; aktivierter Kanal (auch
     ausgesetzt `not_editor`), den ein Verwalter neu eingeladen hat → Annahme beim nächsten Editor-Read, der Kanal schreibt
     wieder ohne neue Aktivierung. **Keine** Annahme und keine Ablehnung bei: Einladung ohne Anfrage und ohne Aktivierung
     (auch über viele Takte, auch für einen Kanal, den Emote Purge nicht kennt), ungedecktem Kanal (E8), `KillSwitch`,
     ungültigem Token, Bot-Schalter aus, Eintrag ohne `manage`, schon angenommener Einladung (idempotent). Ein
     `DELETE …/wishes/bot` zwischen Editor-Read und Annahme verhindert die Annahme. Live: Einladung im Testkanal plus
     `activate` → angenommen und aktiv; Einladung in einem zweiten Kanal ohne Aktivierung bleibt `PENDING`.
124. **AK124** Editor-Prüfung über Zustand und Berechtigung: als Editor gilt nur `state = ACCEPTED` mit
     `permissions.emoteSet.manage`; ein `PENDING`-Eintrag, dessen Set in `users.me.editableEmoteSetIds` steht, gilt
     **nicht** als schreibbar (die Abfrage liest das Feld gar nicht). `LACKING_PRIVILEGES` wird mit jeder Meldung
     gleich zugeordnet (Test mit beiden gemessenen und einer fremden Meldung), der Kanal geht nach AK80 auf `not_editor`,
     und erst der nächste Editor-Read macht daraus eine Annahme bzw. `invitation_pending`. Eine Aktivierungsanfrage ohne
     sichtbaren Eintrag bleibt 2 Minuten offen (`activationPending` wahr), danach `not_editor`.
125. **AK125** Annahme, Fehlerformen: 429 → Pause, Anfrage bleibt bzw. `invitation_pending`; 401 → Token `invalid`,
     globaler Rückfall, Anfrage bleibt; Timeout oder Netzfehler → nichts gebucht, der nächste Durchgang darf erneut
     annehmen; Ablehnung (`LACKING_PRIVILEGES` oder unbekannter Fehler) → beim Aktivieren `activationError =
     invitation_not_accepted` und Anfrage geleert, im aktivierten Kanal `invitation_pending` und kein neuer Versuch vor
     15 Minuten. Der Anfragekörper der Annahme enthält nur `ACCEPT` als Zustand; weder Token noch Fehlertext von 7TV
     erscheinen in Log, Audit oder Antwort (AK74, AK75).

## Testplan

| Ebene | Projekt | Was | Anzahl |
|---|---|---|---|
| Unit | `Infrastructure.Tests/Unit` | Siegel (AK72), Eigentumsentscheidung als reine Funktion (AK77), Fehlerzuordnung, JWT-`exp`-Lesen | +30 |
| Unit | `Worker.Tests` | Reihenfolge der Vorprüfungen, Bestätigungsplan E11, Pausenregel (rein, ohne Container) | +12 |
| Integration | `Infrastructure.Tests/Integration` | Bot-Dienst gegen Postgres mit gestubbtem `ISevenTvWriteClient` (AK77 bis AK81), Schreib-Client gegen lokalen HTTP-Stub (AK76), Advisory-Lock | +25 |
| Api | `Api.Tests` | Admin-Routen, Aktivieren/Abschalten, AK73, AK74 | +10 |
| Vitest | Admin-Seite | nur Zustandslogik (Hinweis ab 7 Tagen, Feld wird geleert) | +4 |
| Live | lokal, Testkonto | AK86 bis AK93; der Transport selbst wird live statt gegen Fakes verifiziert (Regel 11, 16) | 1 Durchgang |

Die Live-Proben AK86 bis AK92 laufen **zuerst**, vor dem Ausbau der Warteschlange: Ihre Ergebnisse
entscheiden über Eigentum und Bestätigungsweg. **Gelaufen am 2026-11-24, alle bestanden** (Protokoll
`docs/nachtlaeufe/live-487-probe-2026-11-24.md`, Tor G0 in Plan-487); neue Folge ist die Annahme von Einladungen
(AK123 bis AK125, Integration und Live-Verifikation).

## Files Reference

| Datei | Änderung |
|---|---|
| `src/EmotePurge.Core/Entities/BotAccountCredential.cs` | neu |
| `src/EmotePurge.Core/Services/IBotAccountTokenSealer.cs`, `IBotAccountTokenOpener.cs`, `IBotAccountService.cs`, `IBotAccountAvailability.cs`, `IWishBotService.cs`, `WishBotOwnership.cs` | neu |
| `src/EmotePurge.Core/SevenTv/ISevenTvWriteClient.cs` | neu |
| `src/EmotePurge.Core/Messaging/BotCommands.cs:22` | `BotAccountPrefix = "BOTACCOUNT:"` |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvWriteClient.cs` | neu |
| `src/EmotePurge.Infrastructure/SevenTv/SevenTvApiClient.cs:42-43` | unverändert; der Lesepfad des Vollsyncs bekommt `addedById` nicht |
| `src/EmotePurge.Infrastructure/Services/BotAccountTokenSealer.cs`, `BotAccountTokenOpener.cs`, `BotAccountService.cs`, `WishBotService.cs` | neu |
| `src/EmotePurge.Infrastructure/ServiceCollectionExtensions.cs` | zwei getrennte Einstiege |
| `src/EmotePurge.Infrastructure/Migrations/*_AddBotAccount.cs` | neu |
| `src/EmotePurge.Worker/BotAccount/BotAccountWorker.cs`, `WorkerServiceRegistration.cs` | neu, Registrierung |
| `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs:35`, `WishEndpoints.cs`, `Program.cs` | Routen, Registrierung |
| `src/EmotePurge.Api/Auth/WishBotActivationAuthorizationFilter.cs` | neu (E24), mit Fall in `Api.Tests` |
| `src/EmotePurge.Api/Validation/ApiErrorCodes.cs` | `bot_account_disabled`, `bot_account_token_malformed`, `bot_account_token_expired`, `wish_bot_unavailable`, `wish_bot_channel_not_listed`, `wish_bot_activation_forbidden`, `wish_bot_already_active`, `wish_not_bot_owned` |
| `web/src/app/features/admin/bot-account-page.ts`, `app.routes.ts:60-95`, `core/admin/` | Admin-Seite |
| `web/src/app/core/i18n/api-error.ts`, `web/public/i18n/*.json` | Codes, `admin.botAccount.*`, `audit.actions.botAccount.*` |
| `docker-compose*.yml`, `.env.example` | vier Variablen (mit `WISHES_PILOT_CHANNEL_IDS`), Schlüssel je nur an einem Dienst |
| `docs/DECISIONS.md`, `docs/Architectur.md:21`, `PRODUCT.md:81`, `docs/Operations.md`, `CLAUDE.md` | nach Abschnitt 7 |

## Abhängigkeiten

K1, K2. Betreiber vorab: Schlüsselpaar erzeugen, Bot-Konto auf dem Testkonto als Editor eintragen.

---

# K5: Bot-Frontend

Labels: `feat`, `web`. Hängt ab von K3, K4 und der Probe AK97.

## Ziel

Ein Verwalter schaltet den Bot-Modus für einen Kanal ein, indem er das Bot-Konto mit seinem
**eigenen** 7TV-Token als Editor einträgt (D7), nach einem Dialog, der die Folgen nennt. Abschalten
und die Bot-Zustände auf den Karten gehören dazu.

## Umfang

Aktivierungsdialog, Editor-Mutation im Browser, Abschalten, Darstellung der Bot-Gründe und der
Aktion „Bot entfernen lassen“. Kein Backend außer dem, was K4 liefert.

## Implementation Details

- **Probe vor Baubeginn (AK97):** Die Editor-Hinzufügen-Mutation existiert im Repo nicht und ihr
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
  Bestätigung des Workers (Abfrage `GET …/wishes/bot` nach `wish.changed`, höchstens 60 s, bis
  `activationPending` falsch ist; dann entscheidet `enabled` oder `activationError`) →
  Erfolgs- oder Fehlzustand mit Grund. Das Token des Verwalters verlässt den Browser nicht.
- **Rückfallweg**, falls die Probe kein nutzbares Schema liefert oder 7TV eine Annahme durch das
  Bot-Konto verlangt: Der Dialog zeigt eine Anleitung zum Eintragen in 7TV und einen Knopf „Jetzt
  prüfen“, der nur `activate` aufruft. **Ergebnis G0 (2026-11-24):** 7TV verlangt die Annahme, aber der Bot
  nimmt eine Einladung beim Aktivieren selbst an (K4 „Editor-Prüfung und Einladungen“); dieser Grund allein
  macht den Rückfallweg also nicht nötig. Der Dialog wartet auf `activationPending` (die Annahme liegt darin)
  und übersetzt zusätzlich `activationError = invitation_not_accepted` und `suspendedReason =
  invitation_pending` („Einladung offen, wird angenommen“); `not_editor` erklärt er mit dem Hinweis auf die
  Berechtigung „Emote-Sets verwalten“.
- **Ort:** Einstieg zum Aktivieren, Zustand des Bot-Modus und Abschalten liegen im Abschnitt
  „Wünsche“ des Settings-Tabs (E19), nicht im Wünsche-Tab.
- **Abschalten:** Knopf in diesem Abschnitt für Broadcaster, Aktivierenden und Admin, mit
  Bestätigung und dem Hinweis, dass laufende Wünsche zu Erinnerungen werden. Das Austragen in 7TV
  bleibt ein getrennter, optionaler Schritt mit eigenem Token.
- **Karten:** Übersetzungen und Aktionen für `unconfirmed`, `failed` samt Gründen, `presented` mit
  den Bot-Gründen, Kennzeichen „Bot entfernt bei Ablauf“ nur bei `botOwned`, Aktion „Bot entfernen
  lassen“ nach Abschnitt 5, Hinweis „manuell erledigen“ im Rückfall.
- **i18n:** `wishes.bot.*`, `wishes.reason.*` ergänzt, `errors.api.wish_bot_*`,
  `audit.actions.wish.bot*`.

## Akzeptanzkriterien
97. **AK97** Probe Editor-Mutation dokumentiert: Name, Variablen, Berechtigungsfelder, Antwortform,
    und ob das Bot-Konto eine Einladung annehmen muss. Ohne dieses Ergebnis beginnt K5 nicht.
    (Die Seite des Bots ist seit G0 geklärt: ja, und der Bot nimmt selbst an; offen bleibt die Mutation des
    Verwalters, die die Einladung anlegt.)
98. **AK98** Der Bestätigen-Knopf des Dialogs ist gesperrt, bis die Checkbox gesetzt ist; der Dialog
    enthält die drei Aussagen (über ihre Übersetzungsschlüssel identifiziert, Rolle `dialog`,
    zugänglicher Name gesetzt).
99. **AK99** Reihenfolge: `activate` wird erst nach erfolgreicher Editor-Mutation gesendet; scheitert
    die Mutation, geht keine Anfrage an die Api und der Dialog nennt den Grund.
100. **AK100** Das 7TV-Token erscheint in keiner Anfrage an `/api/**` (E2E prüft alle aufgezeichneten
    Anfragen).
101. **AK101** Bestätigt der Worker binnen 60 s nicht, zeigt der Dialog „noch nicht bestätigt“ mit
    „Erneut prüfen“; der Modus gilt nicht als aktiv.
102. **AK102** Abschalten ist für einen Twitch-Mod, der nicht aktiviert hat, nicht angeboten und
    die Api antwortet ihm 403; nach dem Abschalten zeigt keine Karte mehr „Bot entfernt bei Ablauf“.
103. **AK103** `wishBotModeAvailable = false` (Schalter aus, kein gültiges Token, Notaus, Kanal nicht gelistet): Der
    Einstieg fehlt bzw. erklärt den Grund; bestehende Bot-Kanäle zeigen den Rückfallhinweis.
104. **AK104** Jeder `statusReason` und jeder `failed`-Grund aus Abschnitt 5 hat einen Schlüssel in
    beiden Sprachen (Test über die Liste der Konstanten).
105. **AK105** „Bot entfernen lassen“ erscheint nur bei `botOwned` und den drei Gründen aus Abschnitt
    5; nach dem Auslösen zeigt die Karte „wird entfernt“, bis der Wunsch `entfernt` ist; 409
    `wish_not_bot_owned` und `wish_bot_unavailable` werden als Meldung gezeigt. Lehnt die Api das
    Aktivieren mit 403 `wish_bot_activation_forbidden` ab, nennt der Dialog den Grund.
106. **AK106** Während des Aktivierungslaufs sind alle anderen 7TV-Startknöpfe gesperrt (Arbiter).
107. **AK107** Live-Probe im Browser auf dem Testkonto: Aktivieren über den Dialog, Wunsch freigeben,
    Bot fügt hinzu, Karte zeigt `aktiv` mit Bot-Kennzeichen; Bot in 7TV austragen → binnen eines
    Schreibversuchs zeigt der Kanal den Rückfall.
108. **AK108** Live-Probe zu R3-15: Dieselbe Editor-Mutation mit dem Token eines Kontos, das im
    Zielkanal weder Inhaber noch Editor mit Editor-Verwaltung ist, wird von 7TV abgelehnt. Ergebnis
    in DECISIONS festgehalten. **Wird sie nicht abgelehnt, trägt D7 nicht**, und der Betreiber
    entscheidet vor dem Merge neu.

## Testplan

| Ebene | Was | Anzahl |
|---|---|---|
| Vitest, `core/seven-tv` | neuer Editor-Dienst mit `HttpTestingController` (Erfolg, Ablehnung, verkleidetes 429) | +6 |
| Vitest, Komponenten | Dialog-Zustände, Sperrgründe, Aktionsliste mit Bot-Gründen | +10 |
| Playwright | `web/e2e/wishes-bot.e2e.spec.ts`: Aktivieren bis bestätigt (7TV und Api gemockt), Ablehnung durch 7TV, Rückfallanzeige, AK100 | +4 |
| Live | AK107, AK108 | 1 Durchgang |

## Files Reference

| Datei | Änderung |
|---|---|
| `web/src/app/core/seven-tv/seven-tv-editor.service.ts` (+ Spec) | neu, Mutation nach AK97 |
| `web/src/app/features/wishes/wish-bot-activate-dialog.ts`, `wish-card.ts` | Dialog, Bot-Zustände |
| `web/src/app/features/channel-workspace/wish-settings-section.ts` | Einstieg, Zustand, Abschalten |
| `web/src/app/core/wishes/wish.service.ts`, `wish.model.ts` | Bot-Routen |
| `web/public/i18n/de.json`, `en.json`, `web/src/app/core/i18n/api-error.ts` | Schlüssel |
| `web/e2e/wishes-bot.e2e.spec.ts`, `web/e2e/support/mocks.ts` | neu |
| `docs/DECISIONS.md` | englische Einträge zu AK97 und AK108 |
| `docs/Feature-Ideen-2026-08-01.md` | Statuszeile `A18` |
| `CLAUDE.md` | Umsetzungsstand |

## Abhängigkeiten

K3, K4, Probe AK97.

---

## Anhang: Zuordnung der Prüfpunkte

| Punkt | Aufgelöst in |
|---|---|
| R3-1, R3-2, R3-3 | im Entwurf behoben; hier Abschnitt 8 (Quelle), Abschnitt 5 und E10, AK9, AK10 |
| R3-4 | Abschnitt 5 (`unconfirmed` → `active`, drei Ausgänge der Vorlage), AK77 |
| R3-5 | AK77 nennt alle Fälle |
| R3-6 | Abschnitt 7 letzter Absatz, AK95 |
| R3-7 | E16 |
| R3-8 | E4, AK24 |
| R3-9 | Abschnitt 13 |
| R3-10 | Abschnitt 7 Punkt 8, AK79 |
| R3-11 | Vorprüfung vor jedem Schreiben in K4, AK80 |
| R3-12 | Abschnitt 5 (Ausgänge, „fällig“, kein Timeout, Retry ohne neue Freigabe); Rest als RR7 |
| R3-13 | entfällt: P3 per Betreiberentscheidung gestrichen; DECISIONS nennt den Betrieb „unofficial“ |
| R3-14 | E5, AK25; Worker-Ausfälle als RR1 |
| R3-15 | Probe AK108; bis dahin RR8 |
| F2 | K1 (409-Fälle), K4 (`failed` ohne Retry) |
| F3 | E12, E13 |
| F4 | 6.5, E14 |
| F5 | Abschnitt 7 Punkt 7; Automatik Out of Scope |
| F7 | Entscheidung 1; abgeleitete Warteschlange in K4 |
| F8 | E15 |
| F9 | E4, E5 |
| F10, F11 | Out of Scope (Abschnitt 15) |
| F12 | Abschnitt 8, AK78 |
| Sol-Prüfung der Spec, S1 bis S9 | Abschnitt 17, Runde 4 |
| Entwurf Scheibe 3, Sol-Befund 6, P1-Rest | K6 (Erfassung, Dedupe, Lückenanzeige, Live-Probe AK46) |
