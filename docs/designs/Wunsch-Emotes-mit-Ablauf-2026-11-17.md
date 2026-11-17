# Design: Wunsch-Emotes mit Ablauf (Kanalpunkte → Freigabe → Probezeit)

Erzeugt von `/office-hours` am 2026-11-17
Branch: `main`
Repo: sensitron/EmotePurge
Status: APPROVED (2026-11-17, durch den Betreiber; 12 kleinere Prüfpunkte gehen in die Spec).
Revision 1 am selben Tag nach Codex-Sol-Prüfung (`needs-attention`, 4 × high, 2 × medium) und
Fable-Schiedsspruch zum Eigentum des Bots: Annahmen 3, 4, 5, 10 und Scheibe 0/2 angepasst,
Betreiber-Entscheidungen D7 und D8, Ergebnis von P1 eingetragen.
Modus: Builder
Vorgänger: keiner. Der letzte Entwurf auf `main` (#148, 7TV-Bestenliste) betrifft ein anderes
Thema, daher kein `Supersedes`.
Wireframe: keiner (Konzeptstand; die Probezeit-Karte entsteht in der Spec gegen
`docs/UI-Designsprache.md`)

## Problemstellung

Ein Mod vieler Kanäle (streamt auch selbst) hat angefragt. In den Kanälen, die er betreut, gibt es
eine Kanalpunkte-Belohnung „7TV-Emote wünschen": Ein Zuschauer löst sie ein, das gewünschte Emote
kommt für eine feste Zeit ins Set (z. B. 30 Tage). **Das Entfernen nach Ablauf hängt am Gedächtnis
eines Mods**, es wird vergessen und skaliert nicht über viele Kanäle. Der Betreiber moderiert selbst
Kanäle und würde es dort ebenfalls nutzen.

Harte Anforderung des Betreibers: Kein Wunsch landet ohne Freigabe durch einen Menschen im Set
(anstößige oder unpassende Emotes).

## Was daran besonders ist

Das Muster „Bot ist 7TV-Editor, fügt per Kanalpunkte hinzu, entfernt nach Frist" gibt es schon
(RukuBot, Nerixyz/rewards; Supibot fügt per Befehl hinzu und entfernt; Quellcode bzw.
community-belegt). Was Emote Purge zusätzlich hat:

- **Probezeit statt Stoppuhr.** Vor Ablauf bekommt der Mod eine Karte: Emote, wer es gewünscht hat,
  wie lange es drin ist, **wie oft es im Chat benutzt wurde und wo es im Set steht**. Behalten,
  verlängern oder laufen lassen. Ein beliebtes Wunsch-Emote muss nicht rausfliegen, nur weil die
  Frist um ist; ein totes geht mit gutem Gewissen. Das ist Produktgrundsatz 1 („gemessene Nutzung
  urteilt nicht, der Mensch entscheidet") angewandt.
- **Der Bot fasst nur an, was er selbst hingelegt hat**, belegt über `addedById`, mit frischer
  Prüfung vor jedem Entfernen und Reparatur, falls es doch den Falschen trifft. Keine harte
  Garantie (7TV kennt kein bedingtes Entfernen), aber eine Grenze, die Emote Purge sichtbar macht
  und prüft (ob andere Bots sie ziehen, ist nicht belegt).
- **Belohnung anlernen statt konfigurieren.** Der Mod löst die Belohnung einmal selbst ein; Emote
  Purge sieht eine unbekannte `custom-reward-id` mit 7TV-Link und fragt „ist das eure
  Wunsch-Belohnung?". Kein Streamer-Login, keine ID abtippen. (Hängt an Probe P1.)
- Spätere Ausbaustufen: „einer rein, einer raus" bei vollem Set (Brücke zu Backlog A1/A15),
  Bestenliste der Wünsche, die sich durchgesetzt haben.

## Rahmenbedingungen (mit Beleg)

Evidenzart in Klammern. „Ungeprüft" heißt: trägt das Konzept, muss vor dem Bau gemessen werden.

**Twitch**
- Belohnungen **mit Texteingabe** erscheinen im IRC als PRIVMSG mit Tag `custom-reward-id`; ohne
  Texteingabe gar nicht (community-belegt, discuss.dev.twitch.com Threads 32404, 42147). Der Tag
  ist **nicht offiziell dokumentiert**, enthält nur die Belohnungs-ID (kein Titel, keine Kosten,
  keine Einlöse-ID). **Ob die anonyme Verbindung ihn bekommt: ungeprüft** → Probe P1.
- Kanalpunkte gibt es nur in Kanälen mit Affiliate- oder Partner-Status.
- Der Worker liest Tags heute schon (`TwitchChatManager.cs:1103`, `UndocumentedTags` für Shared
  Chat), aber nicht `CustomRewardId`, und protokolliert keine rohen Tags. Der Client ist anonym
  (`TwitchChatManager.cs:782`).
- EventSub `channel.channel_points_custom_reward_redemption.add` braucht ein Token **des
  Streamers** (`channel:read:redemptions`); ein Mod-Token reicht nicht (dokumentiert +
  community-belegt). → nicht Teil von v1.
- „Update Redemption Status" (abhaken/ablehnen mit Rückerstattung) geht **nur für Belohnungen, die
  dieselbe Client-ID angelegt hat**, mit Streamer-Token (dokumentiert). → Emote Purge erstattet
  nicht; Ablehnen mit Rückerstattung bleibt in Twitchs eigener Warteschlange.
- Chat senden: Bot-Token mit `user:write:chat`, Bot ist Mod **oder** Streamer gibt `channel:bot`;
  ohne Mod-Status 20 Nachrichten/30 s (dokumentiert). Whisper: verifizierte Telefonnummer, max. 40
  neue Empfänger/Tag, Empfänger blocken Fremde oft, 204 garantiert keine Zustellung (dokumentiert).
  → kein Benachrichtigungskanal für v1.
- Erfassung über IRC und die Nutzungszahlen der Probezeit-Karte setzen voraus, dass der Kanal aktiv
  gejoint ist (zählt gegen `Channels:MaxActiveChannels`, Default 80). Für einen Pilot mit 5–15
  Kanälen unkritisch; Skalieren ist ausdrücklich nicht das Ziel von v1.

**7TV**
- Kein offizieller Bot-, App- oder Partnerzugang gefunden. Das verbreitete Muster: Der Streamer
  trägt das Bot-Konto als Editor ein, der Bot schreibt mit dem **Website-Login-JWT seines eigenen
  Kontos** als Bearer-Token (Quellcode: Supibot `API_7TV_TOKEN` mit Fehlermeldung „Check if I am
  set up as the 7TV editor for this channel"; RukuBot `TWITCH_7TV_EDITOR_TOKEN`; Fumobot erneuert
  das JWT per geskriptetem Website-Login, sobald `exp` unter 1 h Rest fällt). SevenTV#159 wurde vom
  Autor mit „ask users to add an account you control as an editor" geschlossen.
- **Lebensdauer des JWT: rund 30 Tage, und es stirbt beim Logout auf 7tv.app** (community-belegt,
  SevenTV#311; nicht selbst gemessen) → Probe P2. Folge für den Betrieb: Das Bot-Konto wird nie
  nebenbei im Browser benutzt.
- Eine ausdrückliche Aussage von 7TV zu automatisierten Schreibzugriffen fehlt (unbekannt) → Tor P3.
- Bekanntes Verhalten der 7TV-API mit Code-Folgen (Memory/DECISIONS): 429 kommt teils als HTTP 200,
  REST-Cache 10–30 min veraltet. Ein serverseitiger Schreiber muss das genauso behandeln wie die
  Browser-Engine heute.

**Emote Purge**
- **Zero-Knowledge:** „The backend never stores or processes 7TV tokens at any point"
  (`docs/Architectur.md:21`, `PRODUCT.md:81`, DECISIONS „Grundsatz 4"). Der Bot-Modus ändert das
  bewusst und eng (s. Annahme 3).
- Backlog `docs/Feature-Ideen-2026-08-01.md`: „Würde ich nicht bauen" **#3 Geplante oder automatische
  Purges** wird für einen eng umrissenen Fall geöffnet. Beide Gründe dort sind adressiert: das
  Write-Token (Annahme 3) und die endgültige Aktion ohne Menschen davor (Annahme 5: die vereinbarte
  Laufzeit ist die Zustimmung, und es gibt einen Weg zurück). **#1 Chat-Bot mit Commands** bleibt in
  v1 zu. Verwandt: **A15 Nachrücker-Liste** (Positionierungsrisiko „von Purge zu Set-Management").
- Alle 7TV-Schreibzugriffe laufen heute im Browser (`web/src/app/core/seven-tv/`:
  `seven-tv-restore.service.ts`, `seven-tv-delete.service.ts`, Run-Engine). Backend liest nur. Das
  Entfernen zielt auf einen Eintrag über Alias und Hinzufüge-Zeitpunkt
  (`seven-tv-entry-target.ts`).
- `EmoteSlot` (Epic #365) ist „eine Zeile je Aufenthalt": gebunden über 7TVs maßgebliches
  `added_at`, das bis zur Bestätigung durch den Vollsync vorläufig ist (`EmoteSlot.cs`). **Ein
  Umbenennen behält den Slot** (`EmoteSlotReconcilePlanner.BindByDate` bindet über das Datum,
  „whatever the names"; `EmoteSlotDeltaPlanner.PlanRename` benennt den vorhandenen Slot um). Erst
  Entfernen und erneutes Hinzufügen erzeugt eine neue Zeile. Kein „hinzugefügt von", kein
  Ablaufdatum.
- Rollen heute: `CanManageChannelAsync` lässt Admin, Broadcaster und Live-Mods zu; 7TV-Editoren nur
  für Nutzungsdaten. Wie das im Bot-Modus aussieht, regelt Annahme 10.
- Produktgrundsätze: 3 („seltener Besuch") heißt, dass Karten oft ungesehen bleiben; deshalb trägt
  im Bot-Modus die vereinbarte Laufzeit die Entscheidung, nicht das Ansehen der Karte (Annahme 5).
  2 („eine Person entscheidet") wird durch eine Queue gedehnt, asynchrone Übergabe ist erlaubt.
- Datenschutz: Twitch-Name des Wünschenden ist ein neues personenbezogenes Datum → Datenschutztext
  und `RetentionPolicy`.

## Annahmen (vom Betreiber bestätigt, 2026-11-17)

1. **Ohne Freigabe kommt nichts ins Set.** Jeder über IRC erfasste Wunsch geht durch die Queue. Ein
   Handeintrag durch einen Kanal-Verwalter gilt als freigegeben.
2. **Modus pro Kanal:** Bot-Modus (Bot-Konto ist 7TV-Editor, Backend fügt nach Freigabe hinzu und
   entfernt bei Ablauf) oder manueller Modus (Browser mit Token des Mods, Emote Purge erinnert nur).
   Beide Modi sind gleichwertig gewollt; für Editor-Kanäle ist die Automatik der eigentliche Wert.
3. **Zero-Knowledge wird enger gefasst, nicht aufgegeben:** Nie das 7TV-Token eines *Nutzers* auf
   dem Server; erlaubt ist genau ein Token des eigenen Bot-Kontos, verschlüsselt gespeichert. Seine
   Reichweite bestimmt jeder Streamer selbst über die Editor-Vergabe in 7TV. **Das ist eine
   geänderte Vertrauensgrenze beim Schreiben** und wird so benannt: Ein gestohlenes laufendes Token
   kann bis zum Notaus in allen Bot-Kanälen direkt bei 7TV schreiben, an Freigaben und Audit-Log
   vorbei. In Kauf genommen mit Schutzmaßnahmen (D8, s. Scheibe 2). DECISIONS-Eintrag und Anpassung
   von `Architectur.md` und `PRODUCT.md` (Zero-Knowledge-Satz, Positionierung) im selben Commit.
4. **Der Bot entfernt nur Einträge, die er nach bestem Wissen selbst hinzugefügt hat. Eine harte
   Garantie gibt es nicht**, weil 7TVs `removeEmote` nur Emote-ID und Alias kennt, keine Generation
   (geprüft per v4-Schema am 2026-11-17; dasselbe Restrisiko wie D33 der Slot-Spec für die
   Browser-Engine). Eigentum, Prüfung vor dem Entfernen, Restrisiko und Reparatur regelt Scheibe 2
   (Fable-Schiedsspruch).
5. **Die vereinbarte Laufzeit ist die Zustimmung (D4).** Die Frist steht von Anfang an in Queue und
   Karte. Im Bot-Modus entfernt der Bot bei Ablauf, auch wenn niemand die Probezeit-Karte gesehen
   hat. **Optional je Kanal: Schutz für beliebte Emotes.** Liegt ein Wunsch-Emote über einer
   festgelegten Nutzungsschwelle, entfernt der Bot es nicht, sondern legt es zur Entscheidung vor.
   **Ist die Zählung über die Laufzeit lückenhaft oder unbekannt** (Worker-Ausfall, Kanal nicht
   gejoint, Lücken in der Abdeckung), wird bei eingeschaltetem Schutz immer vorgelegt, nie
   automatisch entfernt (Sol-Befund 5).
   **Weg zurück:** „wieder hinzufügen" aus dem Wunsch-Datensatz (gleiche Emote-ID, gleicher Alias).
   Im manuellen Modus bleibt es bei der Erinnerung.
6. **Erfassung zuerst über IRC** (`custom-reward-id`, abhängig von P1), plus **Handeintrag** durch
   einen Mod als gleichwertiger Weg. Kein EventSub in v1.
7. **Bot bleibt in v1 stumm im Chat.** Benachrichtigung über die Web-Oberfläche (Badge, Übersicht,
   live per SSE).
8. **Ablehnen mit Rückerstattung bleibt in Twitchs eigener Warteschlange.**
9. **Positionierung erweitert** („Purge plus Wünsche"); Datenschutztext und Löschfrist für
   Wünschende.
10. **Rechte im Bot-Modus (D7, ersetzt D5): Aktivierung mit eigenem 7TV-Token.** Wer den
    Bot-Modus einschaltet, trägt den Bot **im Browser mit seinem eigenen 7TV-Token** als Editor
    ein (bestehende Browser-Schreibfläche); 7TV prüft dabei, ob er das darf. Vorher erklärt ein
    Dialog: Der Bot entfernt Wunsch-Emotes bei Ablauf selbst, und alle Twitch-Mods des Kanals
    dürfen Wünsche freigeben. Emote Purge hält fest, wer wann aktiviert hat. Danach dürfen alle
    Kanal-Verwalter (Broadcaster, Twitch-Mod, Admin) freigeben und verlängern; Frist,
    Schutzschwelle und Abschalten des Bot-Modus bleiben dem Aktivierenden und dem Broadcaster
    vorbehalten (Spec klärt die Feinheiten). Damit ist ein Twitch-Mod ohne 7TV-Rechte nicht mehr
    ohne Zustimmung eines 7TV-berechtigten Menschen in der Lage, über den Bot zu schreiben
    (Sol-Befund 4). Austragen des Bots in 7TV beendet alles sofort; offene Bot-Aktionen gehen dann
    an den manuellen Ablauf.

## Betrachtete Varianten

- **A: Wunschliste zuerst (minimal)**: nur Handeintrag, Probezeit, manueller Modus. Verworfen als
  Endstand, weil es Editor-Kanälen die Automatik nicht liefert; lebt als Scheibe 1 in B weiter.
- **B: Beide Modi in Scheiben (gewählt).**
- **C: Bot zuerst**: schnellste Automatik, aber Kanäle ohne Editor-Freigabe hätten lange nichts,
  und scheitert P2 oder P3, steht das zuerst gebaute Kernstück.

## Gewählte Variante: B, in vier Scheiben

Reihenfolge so gewählt, dass jede Scheibe auch dann Wert hat, wenn eine Probe schlecht ausgeht.

**Scheibe 0: Proben und Tore (vor jeder Zeile Produktivcode)**
- **P1 IRC:** Instrument ist ein kleiner Wegwerf-Client außerhalb des Workers: anonymer Login
  (`justinfan…`), `CAP REQ :twitch.tv/tags`, `JOIN` auf den Probe-Kanal, rohe Zeilen ausgeben.
  Probe-Kanal: der eigene Kanal des Betreibers, falls Affiliate/Partner; sonst ein Kanal des
  anfragenden Mods mit einer Testbelohnung (Texteingabe). Eingelöst wird mindestens einmal von einem
  **Zuschauer-Konto ohne Mod-Status**. Bestanden, wenn `custom-reward-id` und Text auf der anonymen
  Verbindung ankommen. Durchgefallen → Scheibe 3 entfällt, Erfassung bleibt beim Handeintrag;
  EventSub mit Streamer-Login wäre eine eigene, spätere Entscheidung.
  **Ergebnis 2026-11-17: bestanden für ein Mod-Konto.** Wegwerf-Client (Node, anonym, `CAP REQ
  :twitch.tv/tags twitch.tv/commands`) 10 min auf einem Kanal mit TTS-Belohnung (Texteingabe);
  der Betreiber hat mit seinem Mod-Konto „test" eingelöst. Ergebnis: 81 PRIVMSG, genau eine mit
  `custom-reward-id` (feste UUID), Text vorhanden, zusätzlich `user-id` des Einlösenden. Keine
  Einlöse-ID, kein Titel, keine Kosten. **Offen:** Einlösung durch ein Konto ohne Mod-Status.
  - **Laufender Vertrag (Sol-Befund 6):** Die Erfassung über IRC ist „nach bestem Wissen", kein
    lückenloses Einlöse-Register. Verpasste Einlösungen (Verbindungsabbruch, Tag fällt weg) zeigt
    die Oberfläche als Abdeckungslücke an, Doppelte werden über die IRC-Nachrichten-ID verworfen,
    und ein Mod kann nach einer Lücke von Hand nachtragen. Verschwindet der Tag dauerhaft, fällt
    der Kanal auf den Handeintrag zurück und der Admin sieht einen Hinweis.
  - **Dokumentierte Alternative:** EventSub `channel.chat.message` liefert
    `channel_points_custom_reward_id` offiziell, mit einem Token des eigenen Lese-Kontos
    (`user:read:chat`) statt eines Streamer-Tokens. Setzt voraus, dass der Bot nicht mehr anonym
    mitliest (vgl. F10). In der Spec von Scheibe 3 gegen den IRC-Tag abwägen.
- **P2 7TV-JWT:** Schwelle vorab: bestanden, wenn das Token **mindestens 7 Tage** lebt und einen
  Login desselben Kontos in einem zweiten Browser übersteht. Als Bot-Konto bei 7tv.app einloggen,
  `exp` ablesen; dann Schreibtest mit dem eigenen Testkonto (Writes dort freigegeben): Bot als
  Editor eintragen, über die v4-Mutation ein Emote hinzufügen und wieder entfernen. Dabei
  festhalten, ob die v4-Abfrage der Set-Einträge mit `addedAt` den neuen Eintrag samt `added_at`
  direkt nach dem Schreiben zeigt und nach welcher Zeit, und ob die Mutationsantwort `added_at`
  trägt. **Liefert keine der beiden Quellen das `added_at` innerhalb von 5 Minuten, ist Scheibe 2
  blockiert** (ohne Bestätigung kein Eigentum, also keine Automatik). Zusätzlich (Fable):
  1. Ist `addedById` bei einem Bot-Hinzufügen die ID des Bot-Kontos, bei einem menschlichen die
     des Menschen? Bleibt es beim Umbenennen erhalten?
  2. Verschränkung: Bot fügt hinzu → Mensch entfernt → Mensch fügt denselben Alias wieder hinzu.
     Zeigt der Read ein fremdes `addedById` und ein neues `addedAt`?
  3. Liefert `addEmote { emotes(...) { items { alias addedAt addedById } } }` den neuen Eintrag in
     derselben Antwort?
  4. Der Wettlauf mit einem umbenannten Geschwister-Eintrag aus D33 gegen den Bot-Pfad: greifen
     Erkennung und Wieder-Hinzufügen?
  5. Ist das Wieder-Hinzufügen nach einem falschen Entfernen möglich (Kapazität, Alias-Konflikt),
     und welches `addedAt` entsteht?
  **Lässt sich `addedById` nicht belegen, entfällt das automatische Entfernen;** der Bot-Modus fügt
  dann nur hinzu und erinnert.
- **P3 Duldung durch 7TV (Tor vor der Spec von Scheibe 2):** 7TV direkt fragen (SevenTV#311 oder
  Discord), ob ein Bot-Konto als Editor mit Website-JWT schreiben darf und ob es einen
  Partnerzugang gibt. Bei einem Nein → nur manueller Modus. Bleibt die Antwort aus (Wartezeit legt
  die Spec fest), entscheidet der Betreiber; Supibot und andere arbeiten seit Jahren so (Beleg,
  keine Erlaubnis). Ein Betrieb ohne Antwort wird im DECISIONS-Eintrag ausdrücklich als
  „inoffiziell, nicht von 7TV bestätigt" festgehalten (D8).
  **Entschieden 2026-11-17: P3 entfällt.** Der Betreiber fragt nicht an (7TV antwortet erfahrungsgemäß
  nicht, vgl. SevenTV#311 ohne Reaktion) und stützt sich darauf, dass etablierte Bots so arbeiten.
  Der Bot-Modus läuft damit inoffiziell; das steht so im DECISIONS-Eintrag von Scheibe 2.

**Scheibe 1: Wünsche, Probezeit, manueller Modus**
- Eigenes Wunsch-Datenmodell (Regel 8: 7TV-Emote-ID, nicht das kanalgebundene `Emote`): Kanal,
  7TV-Emote-ID, gewünschter Alias, Wünschender (optional; ob Twitch-ID und/oder Login, klärt F4),
  Quelle (IRC/Hand/Übernahme), Frist, Bezug zum `EmoteSlot`.
- **Lebenszyklus:**
  - `offen` (über IRC erfasst) → `freigegeben` oder `abgelehnt` (Ablehnen zeigt den Hinweis, die
    Rückerstattung in Twitchs Warteschlange zu machen).
  - Offene Wünsche ohne Entscheidung **verfallen** nach einer Kanal-Einstellung (Default 14 Tage)
    → `verfallen`, sichtbar, ohne Aktion in 7TV.
  - `freigegeben` → `aktiv`, sobald der Eintrag im Set ist. **Die Frist zählt ab 7TVs `added_at`
    dieses Eintrags** (im manuellen Modus aus dem Slot-Sync, im Bot-Modus aus der Bestätigung nach
    dem Schreiben, s. Scheibe 2).
  - Bot-Modus: `freigegeben` → `unbestätigt`, solange 7TV das Hinzufügen geschrieben, aber noch
    kein maßgebliches `added_at` für genau diesen Eintrag geliefert hat. Die Frist läuft noch nicht,
    der Bot hält kein Eigentum und entfernt nichts. Bleibt die Bestätigung über eine Schwelle hinaus
    aus (Spec), wird der Wunsch dem Mod zur Prüfung vorgelegt.
  - `freigegeben` → `fehlgeschlagen`, wenn 7TV das Hinzufügen ablehnt (Namenskollision, Set voll,
    Emote bei 7TV gelöscht); Grund steht auf der Karte, erneut versuchen oder schließen.
  - `aktiv` → `Probezeit` (Vorlauf je Kanal) → `behalten` (Wunsch geschlossen, Eintrag wird ein
    normaler) / `verlängert` (zurück zu `aktiv` mit neuem Ende) / `entfernt` / `vorgelegt` (Schutz
    für beliebte, Annahme 5) / `extern entfernt`.
  - **Änderungen außerhalb von Emote Purge** (erkannt per Slot-Reconcile):
    - **Umbenennen:** Der Wunsch bleibt am selben Slot (nur der Alias ändert sich) und behält seine
      Frist. Im Bot-Modus endet das Eigentum des Bots, weil der Alias im Eigentumsschlüssel nicht
      mehr passt; der Wunsch wird zum reinen Erinnerungsfall.
    - **Entfernen:** Der Wunsch wird `extern entfernt` und dem Mod mit Hinweis gezeigt.
    - **Erneutes Hinzufügen:** Erscheint innerhalb eines Übernahmefensters nach dem Entfernen (Wert
      in der Spec, Größenordnung Stunden) **genau ein** neuer Slot mit derselben 7TV-Emote-ID, wandert
      ein Wunsch aus `extern entfernt` auf diesen Nachfolger, behält seine Frist und wird zum reinen
      Erinnerungsfall. Mehrere Kandidaten oder ein neuer Slot außerhalb des Fensters werden nicht
      übernommen.
  - **Weg zurück:** Aus `entfernt` kann ein Kanal-Verwalter „wieder hinzufügen" auslösen; das gilt
    als Freigabe. Er wählt dabei: **mit neuer Frist** (Wunsch wird wieder geöffnet, Frist ab dem
    neuen `added_at`; im Bot-Modus fügt der Bot hinzu und besitzt die neue Generation, entfernt sie
    also bei Ablauf wieder) oder **dauerhaft** (Eintrag wird hinzugefügt, Wunsch schließt als
    `behalten`).
- **Bestand übernehmen:** Ein Kanal-Verwalter verknüpft einen Wunsch mit einem Eintrag, der schon
  im Set liegt, mit Startdatum in der Vergangenheit und Frist. Der Wunsch beginnt bei `aktiv`.
  Übernommene Wünsche gehören nie dem Bot und bleiben reine Erinnerungsfälle (Annahme 4).
- **Oberflächen:**
  - **Kanalübergreifende Übersicht „Wünsche"** für alle Kanäle, die der Nutzer verwalten darf:
    offene, fällige und fehlgeschlagene Wünsche, sortiert nach Fälligkeit.
  - **Badge in der Hauptnavigation** mit der Zahl offener und fälliger Wünsche über alle Kanäle,
    live per SSE aktualisiert.
  - Je Kanal ein Tab im Channel-Workspace mit Queue, aktiven Wünschen und Einstellungen.
- Probezeit-Karte: Nutzung über die Laufzeit aus den vorhandenen Usage-Stats je Slot.
- Hinzufügen/Entfernen im manuellen Modus über die bestehende Browser-Engine, aus der Karte heraus.
- Ablauf-Lauf im Worker nach Vorlage `DataRetentionWorker` (PeriodicTimer, TimeProvider): setzt nur
  Status, schreibt nicht nach 7TV. SSE-Ereignis für Badge und Übersicht.
- Datenschutztext, `RetentionPolicy`-Eintrag für Wünschende, Feature-Flag (Default aus, Muster wie
  `ChatLogBackfill:Enabled`).

**Scheibe 2: Bot-Modus** (nur nach P2 und P3)
- DECISIONS-Eintrag „Zero-Knowledge enger gefasst" + `Architectur.md` + `PRODUCT.md`.
- **Bot-Token (D8: in Kauf genommen mit Schutzmaßnahmen):**
  - Vom Betreiber hinterlegt, verschlüsselt wie `User.TwitchRefreshToken`, nur Admin. Entschlüsselt
    nur im schreibenden Prozess, nie geloggt (Log-Redaktion prüfen), nie über die Api ausgeliefert.
  - **Notaus:** Logout des Bot-Kontos auf 7tv.app macht das Token sofort ungültig (SevenTV#311,
    vor dem Bau in P2 bestätigen); zusätzlich ein Admin-Schalter, der alle Bot-Schreibzugriffe
    stoppt. Beides steht im Runbook in `docs/Operations.md`.
  - Vorwarnung vor `exp` im Admin-Monitoring; erneuert wird von Hand.
  - **Ausfall** (Token ungültig, abgelaufen, Notaus, Editor-Rechte entzogen): Alle Bot-Kanäle
    fallen auf den manuellen Ablauf zurück. Freigegebene, noch nicht hinzugefügte Wünsche,
    `unbestätigt`e und fällige Entfernungen erscheinen in der Übersicht als „manuell erledigen".
  - Vor jedem Schreiben wird der Editor-Status des Bots im Kanal geprüft.
- Serverseitiger 7TV-Schreiber (Add/Remove), mit derselben Behandlung von Rate-Limits und
  Fehlerformen wie die Browser-Engine; Audit-Log für jede Bot-Schreibaktion.
- **Eigentum nach bestem Wissen (Fable-Schiedsspruch 2026-11-17, ersetzt die frühere „Garantie"):**
  - **Eigentum entsteht nur**, wenn ein v4-Read nach dem Hinzufügen genau einen Eintrag mit
    Emote-ID, Alias **und `addedById` = 7TV-Konto des Bots** zeigt; dessen `addedAt` wird
    festgehalten. Fehlt `addedById` oder ist es fremd: kein Eigentum, reiner Erinnerungsfall.
    Grund: Ein späterer Read beweist nur Anwesenheit, nicht Urheberschaft (Sol-Befund 2); die
    Mutation `addEmote` liefert das Set, nicht den neuen Eintrag (geprüft per Schema).
  - **Vor jedem Entfernen** ein frischer Read ohne Wiederverwendung. Stimmen Emote-ID, Alias,
    `addedAt` und `addedById` nicht exakt, wird nicht entfernt.
  - **Restrisiko, ausdrücklich akzeptiert:** Ändert ein Editor den Eintrag zwischen diesem Read und
    dem Entfernen (Fenster deutlich unter einer Sekunde), kann der Bot einen fremden Eintrag
    entfernen. `removeEmote` kennt nur Emote-ID und Alias (Sol-Befund 1); dieselbe Risikoklasse
    wie D33 der Slot-Spec.
  - **Erkennung und Reparatur:** Ein Read direkt nach dem Entfernen prüft, ob ein nicht
    bot-eigener Eintrag fehlt. Dann fügt der Bot ihn einmal unter demselben Alias wieder hinzu
    (ohne Eigentum, ohne Frist), schreibt einen Audit-Eintrag und benachrichtigt die
    Kanal-Verwalter.
  - Die folgenden Punkte zur Quelle und zu Änderungen außerhalb gelten weiter; „genau diese
    Generation" heißt ab jetzt: alle vier Merkmale inklusive `addedById` im frischen Read.
  - **Quelle der Bestätigung:** die v4-Abfrage der Set-Einträge mit `addedAt`, also derselbe Pfad,
    mit dem die Browser-Engine nach einem Lauf abrechnet (`seven-tv-delete.service.ts`,
    `loadSevenTvSetEntries(..., { withAddedAt: true })`) und der Vollsync Slots maßgeblich bindet
    (`SevenTvApiClient` `GqlSetEntriesQuery`); nicht der v3-REST-Cache. Die Antwort der Mutation ist
    nur eine Abkürzung, falls sie `added_at` trägt (heute fragt die Browser-Mutation nur die Set-ID
    ab, unbelegt). Das EventAPI-Delta ist **keine** Quelle: Sein `timestamp` ist das Upload-Datum des
    Emotes und wird bewusst nicht gelesen (`SevenTvDispatchParser.cs`).
  - Ohne Bestätigung bleibt der Wunsch `unbestätigt` (Scheibe 1): kein Eigentum, kein Entfernen,
    keine Frist, Wiederholung nach Spec.
  - Entfernen ist nur erlaubt, wenn **genau diese Generation** noch im Set liegt; dafür nutzt der
    Server dieselbe Generationsbindung wie die Browser-Engine (`seven-tv-entry-target.ts`), nicht nur
    deren Rate-Limit-Behandlung.
  - Umbenennen (gleicher Slot, anderer Alias) oder Entfernen und erneutes Hinzufügen **außerhalb des
    Bots** beenden das Eigentum; der Wunsch folgt den Regeln aus Scheibe 1 und wird ein reiner
    Erinnerungsfall. Ein „wieder hinzufügen mit neuer Frist" über den Bot erzeugt dagegen eine neue,
    eigene Generation.
  - Liegt dasselbe Emote unter einem zweiten Alias im Set, bleibt dieser Eintrag unberührt.
  - Ein Wechsel von manuellem in Bot-Modus überträgt nie Eigentum an Einträgen, die ein Mensch
    hinzugefügt hat.
  - Tests erzwingen alle Fälle oben: fremdes oder fehlendes `addedById`, `unbestätigt`, Abweichung
    im frischen Read vor dem Entfernen, Umbenennen und Wieder-Hinzufügen außerhalb, Wieder-Hinzufügen
    über den Bot, zweiter Alias, Moduswechsel, Erkennung und Reparatur nach falschem Entfernen.
- Einrichtungsprüfung je Kanal: Ist das Bot-Konto Editor? (Editorenliste lesen.) Ohne Bestätigung
  lässt sich der Bot-Modus nicht einschalten (Annahme 10).
- Auto-Entfernen bei Ablauf nach Annahme 5, mit optionaler Schutzschwelle.

**Scheibe 3: IRC-Erfassung** (nur nach bestandenem P1)
- `custom-reward-id` im Worker auswerten, „Belohnung anlernen", Link-Prüfung (7TV-Emote-URL),
  Duplikate, Begrenzung je Zuschauer, Redis-Kommando an die Api analog bestehender Muster.

**Später, nicht Teil dieses Entwurfs:** einer rein, einer raus (A1/A15); Bestenliste der Wünsche;
Chat-Antworten des Bots; Discord-Webhook je Kanal; EventSub mit Streamer-Login.

## Offene Fragen

- **F2** Konflikte beim Freigeben: Emote schon im Set, Namenskollision, Set voll (7TV-Slot-Limit).
  Wiederverwendung der Kollisionslogik aus dem Import (#368) prüfen.
- **F3** Verlängern: beliebig oft, oder mit Obergrenze?
- **F4** Löschfrist für Wünschende; Twitch-ID oder nur Login speichern.
- **F5** Erneuern des Bot-JWT: Ablauf für den Betreiber; ob eine Automatik wie bei Fumobot (Server
  hält die Twitch-Sitzung des Bot-Kontos) je in Frage kommt.
- **F7** Viele gleichzeitige Abläufe über viele Kanäle: eine serialisierte Schreibwarteschlange für
  das Bot-Konto; welcher Prozess (Api oder Worker) sie hält.
- **F8** Backlog: als neue Idee führen oder in A15 aufgehen lassen; Statuszeile pflegen.
- **F9** Schutz für beliebte Emotes: Wie wird die Schwelle ausgedrückt (Nutzungsrang im Set,
  Nutzungen pro Tag)?
- **F10** Später: Würde der Bot als eigenes Konto statt anonym mitlesen und wäre er in den
  Wunsch-Kanälen Mod, zählten diese laut Twitch nicht gegen die 100-Chatroom-Grenze. Das wäre der
  Umbau „Bot nicht mehr anonym", den v1 ausschließt.

## Erfolgskriterien

- Pilot mit dem anfragenden Mod und den Kanälen des Betreibers: **kein vergessenes Entfernen** im
  Pilotzeitraum; alle fälligen Wünsche eines Mods über alle Kanäle an einer Stelle sichtbar.
- Bot-Modus: freigegebener Wunsch ist ohne weiteres Zutun im Set; abgelaufener Wunsch ist nach der
  Probezeit-Frist entfernt oder, bei Schutzschwelle, vorgelegt.
- Im Pilot hat der Bot keinen Eintrag entfernt, den er nicht selbst hinzugefügt hat; falls doch,
  wurde es erkannt und repariert (Tests + Audit-Log).
- Die Probezeit-Karte zeigt die Nutzung des Wunsch-Emotes über seine Laufzeit.

## Auslieferung

Bestehende Pipeline (GHCR-Images, Portainer-Stack). Migrationen von Hand vor den Images. Je
Scheibe ein Feature-Flag, Default aus für andere Betreiber.

## Nächste Schritte

1. Proben P1 und P2, Anfrage P3 (Scheibe 0).
2. Je nach Ergebnis: `/spec` für Scheibe 1 (und Scheibe 2, wenn P2 und P3 grün sind); Epic mit
   Kindern, Spec unter `docs/superpowers/specs/`, adversariale Prüfung durch Codex Sol.
3. Kurze Rückfrage beim anfragenden Mod: Wie viele seiner Kanäle würden dem Bot Editor-Rechte geben?

## Die Aufgabe

P1 ist bis auf die Einlösung durch ein Konto ohne Mod-Status erledigt (2026-11-17). Offen: Logg
dich als Bot-Konto bei 7tv.app ein, lies `exp` ab und mach den Schreibtest auf deinem Testkonto,
inklusive der fünf `addedById`-Fälle aus P2 (P3 entfällt per Entscheidung). Diese
Antworten entscheiden, ob Scheibe 2 so gebaut wird wie hier beschrieben.

## Was mir an deiner Denkweise aufgefallen ist

- Du hast die Grenze vor das Feature gesetzt: „Was wir halt nicht machen dürfen, ist einfach
  irgendwelche Emotes, die sich Leute wünschen, einfach so übernehmen und adden."
- Du planst um die Plattform herum, nicht auf sie hin: „Darauf können wir nicht zählen, dass 7TV
  irgendwie mal was implementiert."
- Meine engere Fassung hast du nicht übernommen: „Beides gleichwertig. Für die Kanäle, die bereit
  sind, Editor-Rechte zu vergeben, ist die Automatik natürlich noch viel wichtiger."
- Bei der Entfernungsregel hast du meine Empfehlung angenommen und nachgeschärft: „A, aber vielleicht
  mit der Möglichkeit, dass man vorher festlegt, dass beliebte Emotes nicht rausfliegen."
- Du hast selbst die Grenze des Wachstums benannt: „wir haben ja auch aktuell nur 100 Channels frei,
  also es wäre vielleicht auch ein falscher Weg, jetzt zu skalieren."

<!-- gstack:office-hours:concerns:start -->
## Reviewer Concerns

Disposition: CONCERNS_RECORDED

Stop: CONVERGENCE

### R3-1 — feasibility (blocking)

**Problem**

> The bot-mode confirmation now names two sources for the authoritative added_at: the v4 mutation response 'falls sie added_at trägt', otherwise the EventAPI slot delta, 'das der Worker ohnehin live empfängt'. The repo contradicts the fallback. SevenTvDispatchParser.cs:132-133 says the push payload's `timestamp` is the emote's upload date and is deliberately not read, and EmoteSlot.cs:22-28 keeps an EventAPI-created slot's AddedAt provisional (AddedAtFromRest = false) until the full sync reports the real date. So the delta does not carry the entry's added_at. 'ohnehin live' is also unsupported: docker-compose.prod.yml:165 defaults SevenTv__EventApi__Enabled to false, and the subscription exists only for actively joined channels. The browser's own add mutation selects only the set id (seven-tv-restore.service.ts:56-63, `addEmote(...) { id }`), so the first source is unproven too. The working source the repo already uses is left out: the v4 set-entries re-read with addedAt after a run (seven-tv-delete.service.ts:921, `loadSevenTvSetEntries(..., { withAddedAt: true })`; SevenTvApiClient.cs:43 GqlSetEntriesQuery). That is the same v4 path that settles slots authoritatively, and it is not the 10-30 min v3 REST cache. If the mutation does not return added_at, every bot wish stays `unbestätigt` forever: the bot never owns or removes anything, and Scheibe 2's automation fails. The same original obligation from R2-2 (name a confirming source that actually delivers the authoritative added_at) remains unmet.

**Remedy**

> Drop the EventAPI delta as an added_at source, or state that it gives only a provisional hint. Name the v4 set-entries read with addedAt (the browser engine's settling read and the full sync's GqlSetEntriesQuery) as the confirmation path, with the mutation response as an optional shortcut. Change P2 to check whether the v4 set-entries read shows the new entry's added_at right after the write and how quickly, and define the P2 result that blocks Scheibe 2 if no source delivers it within the spec threshold.

### R3-2 — feasibility (blocking)

**Problem**

> The new successor rule treats a rename like a re-add: the wish 'wandert auf den neuen Eintrag', matched by 'gleiche 7TV-Emote-ID, neuer Slot', and 'keinen Nachfolger' leads to `extern entfernt`. In the repo a rename does not create a new slot. EmoteSlotReconcilePlanner.BindByDate (lines 159-166) binds an entry to the same authoritative slot by its added_at 'whatever the names (a rename, §6.5)', and EmoteSlotDeltaPlanner.PlanRename renames the existing slot. A rename reads as remove plus add only inside a timestamp tie group. So after an ordinary rename no new slot exists. Applied literally, the rule finds no successor and marks the wish `extern entfernt`, so the expiry reminder for a renamed wish emote is lost. The background claim this rests on ('Umbenennen oder erneutes Hinzufügen erzeugt eine neue Zeile', design line 99-100) is wrong for renames.

**Remedy**

> Base the rule on slot identity as the reconcile defines it. A rename keeps the wish on the same slot (alias changed): it ends bot ownership because the alias in the ownership key no longer matches, and the wish becomes reminder-only. The successor match (same emote id, new slot, exactly one) then applies only to removal followed by a re-add. Correct the EmoteSlot background sentence.

### R3-3 — completeness (blocking)

**Problem**

> Removal and re-add outside Emote Purge are two separate 7TV actions, often minutes apart. With EventAPI on they arrive as separate pull and push deltas, and with a periodic full sync they can fall into different runs. Under the new rule the reconcile that sees the removal finds no successor yet and moves the wish to `extern entfernt` at once. The design does not say whether `extern entfernt` is terminal or whether a later new slot of the same emote id still pulls the wish over. Read as written, the promised 'entfernt und erneut hinzugefügt -> wandert der Wunsch' case is almost never reached, and the re-added wish emote gets no expiry reminder, which breaks the core promise 'kein vergessenes Entfernen'. Without a time limit, a re-match could also adopt an unrelated later add of the same emote.

**Remedy**

> Define the sequencing: either a grace period after a removal before `extern entfernt` is set, or let a wish in `extern entfernt` be re-matched to exactly one new slot of the same emote id that appears within a stated window (spec value). State that outside this window a new slot is not adopted.

### R3-4 — consistency (minor)

**Problem**

> The lifecycle now has `freigegeben` -> `aktiv` 'sobald der Eintrag im Set ist ... im Bot-Modus aus der Bestätigung' and also `freigegeben` -> `unbestätigt` in bot mode. The transition `unbestätigt` -> `aktiv` on confirmation is not listed, and the result of the mod review after the threshold ('dem Mod zur Prüfung vorgelegt') is undefined: link the entry as a reminder-only wish, retry, or close.

**Remedy**

> Route bot mode as `freigegeben` -> `unbestätigt` -> `aktiv`, and list the outcomes of the mod review for an unconfirmed wish.

### R3-5 — clarity (minor)

**Problem**

> Scheibe 2 still says 'Ein Test erzwingt alle vier Fälle', but the ownership section now lists more cases: the unconfirmed state, generation binding, an outside rename or re-add with successor migration, a bot re-add creating a new generation, a second alias, and a mode switch. It is unclear which four are meant.

**Remedy**

> Name the cases the test must enforce, or say 'alle Fälle oben'.

### R3-6 — consistency (minor)

**Problem**

> Persisting from R2-4/R1-10: Annahme 3 and Scheibe 2 still list only the DECISIONS entry and Architectur.md for the narrowed zero-knowledge rule, although the design cites PRODUCT.md:81 for the same sentence, and Annahme 9 also changes PRODUCT.md's positioning.

**Remedy**

> Add PRODUCT.md (zero-knowledge sentence, purpose, users, positioning) to the documents changed with the bot-mode decision.

### R3-7 — clarity (minor)

**Problem**

> Persisting from R2-5/R1-12: the card description still promises 'wo es im Set steht', but Scheibe 1 and the success criteria only define usage over the period, and the position metric is undefined.

**Remedy**

> Define the position metric and carry it into the Scheibe 1 card, or drop it from the card description.

### R3-8 — completeness (minor)

**Problem**

> Persisting from R2-6/R1-13, and more pressing now that a wish can move to a successor slot: the card still takes usage 'je Slot', with no rule on whether usage is summed over every slot linked to the wish (and for the protection threshold).

**Remedy**

> State whether the card and the protection threshold sum usage across all slots linked to the wish.

### R3-9 — clarity (minor)

**Problem**

> Persisting from R2-7/R1-17: the success criteria still name no pilot length and no observable measure for 'kein vergessenes Entfernen'.

**Remedy**

> Add the pilot duration and a measure, for example no wish more than N days past its period still in the set.

### R3-10 — completeness (minor)

**Problem**

> Persisting from R2-8/R1-20: when an invalid bot token is detected, the channel falls back to reminders, but the design does not say what happens to approved wishes not yet added, wishes in `unbestätigt`, and removals already due.

**Remedy**

> Define how pending bot actions are shown (overview, badge) and handed to the manual flow when the fallback triggers.

### R3-11 — completeness (minor)

**Problem**

> Persisting from R2-9: Annahme 10 still says removing the bot as editor ends bot writes at once, but the editor check is defined only when bot mode is switched on. Nothing is said about what happens to the channel and its pending actions when 7TV refuses a write for missing editor rights.

**Remedy**

> Define a periodic or pre-write editor check, and treat a lost editor status like the invalid-token fallback.

### R3-12 — completeness (minor)

**Problem**

> Persisting from R2-10: `vorgelegt` still has no outgoing transitions or timeout. The state of an expired, unhandled wish in manual mode is undefined. 'fällig' (overview, badge) is not mapped to lifecycle states. Retry from `fehlgeschlagen` does not say whether it needs a new approval.

**Remedy**

> Add the exits from `vorgelegt`, the state of an expired, unhandled wish in manual mode, and map 'fällig' to lifecycle states.

### R3-13 — clarity (minor)

**Problem**

> Persisting from R2-11: Gate P3 lets the operator decide if 7TV does not answer, but still sets no waiting period.

**Remedy**

> State how long to wait for 7TV's answer before the operator decides, and record that decision in the DECISIONS entry for bot mode.

### R3-14 — completeness (minor)

**Problem**

> Persisting from R2-12: the protection threshold reads usage that exists only while the channel is actively joined and counted. During outages, leaves or the channel cap, usage is undercounted, and a popular wish emote can be removed automatically.

**Remedy**

> Require an actively joined channel for bot mode with protection, and present the wish instead of removing it when usage coverage over the period has gaps.

### R3-15 — clarity (minor)

**Problem**

> Persisting from R2-13: Annahme 10's consent argument (only the channel owner or a 7TV editor with editor management can add an editor) still has no evidence label.

**Remedy**

> Add the source, or mark it as unverified and check it during the P2 write test.

### Prior finding evidence

**R2-2 → R3-1 (persisting)**

> The unconfirmed case is now defined (`unbestätigt`: no ownership, no removal, no period, mod review after a threshold) and P2 records the source. But the named fallback source, the EventAPI delta, does not carry the entry's added_at (SevenTvDispatchParser.cs:132-133, EmoteSlot.cs:22-28), and the repo's working v4 set-entries read is omitted. A confirming source that delivers the authoritative added_at is still not named.

**R2-4 → R3-6 (persisting)**

> Annahme 3 and Scheibe 2 still name only DECISIONS and Architectur.md, not PRODUCT.md.

**R2-5 → R3-7 (persisting)**

> 'wo es im Set steht' remains in the card description (design line 31); Scheibe 1 still defines only usage over the period.

**R2-6 → R3-8 (persisting)**

> Scheibe 1 still says 'Nutzung über die Laufzeit aus den vorhandenen Usage-Stats je Slot', with no rule for wishes that now move across slots.

**R2-7 → R3-9 (persisting)**

> The success criteria are unchanged: no pilot length or measure.

**R2-8 → R3-10 (persisting)**

> Scheibe 2 still says only 'Kanal fällt automatisch auf Erinnerungen zurück', with no handling of pending adds, unconfirmed wishes or due removals.

**R2-9 → R3-11 (persisting)**

> Annahme 10 and the Scheibe 2 setup check are unchanged; the editor check runs only at switch-on.

**R2-10 → R3-12 (persisting)**

> The lifecycle still has no exits from `vorgelegt`, no manual-mode expired state, no mapping of 'fällig', and no approval rule for a retry from `fehlgeschlagen`.

**R2-11 → R3-13 (persisting)**

> P3 still says only 'Bleibt die Antwort aus, entscheidet der Betreiber', with no waiting period.

**R2-12 → R3-14 (persisting)**

> Annahme 5 and Scheibe 2 still apply the protection threshold with no requirement for usage coverage.

**R2-13 → R3-15 (persisting)**

> Annahme 10's sentence on who can add an editor is unchanged and has no evidence label.

### Unresolved at the cap

1. R3-1 — feasibility (blocking): The bot-mode confirmation now names two sources for the authoritative added_at: the v4 mutation response 'falls sie added_at trägt', otherwise the EventAPI slot delta, 'das der Worker ohnehin live empfängt'. The repo contradicts the fallback. SevenTvDispatchParser.cs:132-133 says the push payload's `timestamp` is the emote's upload date and is deliberately not read, and EmoteSlot.cs:22-28 keeps an EventAPI-created slot's AddedAt provisional (AddedAtFromRest = false) until the full sync reports the real date. So the delta does not carry the entry's added_at. 'ohnehin live' is also unsupported: docker-compose.prod.yml:165 defaults SevenTv__EventApi__Enabled to false, and the subscription exists only for actively joined channels. The browser's own add mutation selects only the set id (seven-tv-restore.service.ts:56-63, `addEmote(...) { id }`), so the first source is unproven too. The working source the repo already uses is left out: the v4 set-entries re-read with addedAt after a run (seven-tv-delete.service.ts:921, `loadSevenTvSetEntries(..., { withAddedAt: true })`; SevenTvApiClient.cs:43 GqlSetEntriesQuery). That is the same v4 path that settles slots authoritatively, and it is not the 10-30 min v3 REST cache. If the mutation does not return added_at, every bot wish stays `unbestätigt` forever: the bot never owns or removes anything, and Scheibe 2's automation fails. The same original obligation from R2-2 (name a confirming source that actually delivers the authoritative added_at) remains unmet.
2. R3-2 — feasibility (blocking): The new successor rule treats a rename like a re-add: the wish 'wandert auf den neuen Eintrag', matched by 'gleiche 7TV-Emote-ID, neuer Slot', and 'keinen Nachfolger' leads to `extern entfernt`. In the repo a rename does not create a new slot. EmoteSlotReconcilePlanner.BindByDate (lines 159-166) binds an entry to the same authoritative slot by its added_at 'whatever the names (a rename, §6.5)', and EmoteSlotDeltaPlanner.PlanRename renames the existing slot. A rename reads as remove plus add only inside a timestamp tie group. So after an ordinary rename no new slot exists. Applied literally, the rule finds no successor and marks the wish `extern entfernt`, so the expiry reminder for a renamed wish emote is lost. The background claim this rests on ('Umbenennen oder erneutes Hinzufügen erzeugt eine neue Zeile', design line 99-100) is wrong for renames.
3. R3-3 — completeness (blocking): Removal and re-add outside Emote Purge are two separate 7TV actions, often minutes apart. With EventAPI on they arrive as separate pull and push deltas, and with a periodic full sync they can fall into different runs. Under the new rule the reconcile that sees the removal finds no successor yet and moves the wish to `extern entfernt` at once. The design does not say whether `extern entfernt` is terminal or whether a later new slot of the same emote id still pulls the wish over. Read as written, the promised 'entfernt und erneut hinzugefügt -> wandert der Wunsch' case is almost never reached, and the re-added wish emote gets no expiry reminder, which breaks the core promise 'kein vergessenes Entfernen'. Without a time limit, a re-match could also adopt an unrelated later add of the same emote.

issues verified: 3 of 3

Verification is deterministic: a fix counts when its `applied_text` is in the current design and absent from the round-3 snapshot. It confirms applied edits, never a review verdict; no reviewer was launched past the cap.
<!-- gstack:office-hours:concerns:end -->
