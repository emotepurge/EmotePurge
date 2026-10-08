# Product

<!-- impeccable:product-schema 1 -->

Dauerhafte Produktwahrheit für EmotePurge. Erfasst am 2026-08-06 im Interview mit dem Betreiber,
ergänzt um Belege aus dem Repo. Die englischen Überschriften sind das Schema des Impeccable-Skills
und bleiben stehen; der Inhalt ist deutsch wie die übrige Projektdokumentation.

Dieses Dokument hält **was das Produkt ist** fest — nicht, wie es aussieht. Die visuelle Welt lebt
in [docs/UI-Designsprache.md](docs/UI-Designsprache.md), das Warum technischer Entscheidungen in
[docs/DECISIONS.md](docs/DECISIONS.md).

## Platform

web

## Users

**Heute:** der Betreiber selbst plus ein eingeweihter Kreis — darunter Moderatoren großer Kanäle
(Größenordnung HandOfBlood, ~900 Emotes im Set). Zugang ist technisch offen, faktisch aber
Empfehlungskreis.

**Ziel:** Streamer klein bis mittel, die ihr eigenes 7TV-Set selbst pflegen. Sie sind keine
Dauernutzer und kennen das Werkzeug beim zweiten Besuch nicht mehr auswendig.

**Ausdrücklich nicht die Zielgruppe:** große Mod-Teams mit koordiniertem, parallelem Workflow
(bestätigt 2026-08-06). Der Betrieb ist einzelpersonig — es gibt keine Aufgabenverteilung, keine
Übergabe zwischen Personen und keine gleichzeitige Bearbeitung desselben Sets. Funktionen, die nur
in einem Mehr-Personen-Ablauf Sinn ergeben, lösen kein bestätigtes Problem.

**Zweitpublikum „Abstimmende":** technisch vorhanden (Zuschauer, Subs, Mods können in einer
Vote-Session abstimmen), faktisch bislang kaum genutzt. **Kaum genutzt heißt hier unvalidiert, nicht
wertlos** (präzisiert 2026-08-06): der Betreiber kann nicht einschätzen, ob Streamer und Mods das
Feature wollen, weil es nie unter realen Bedingungen geprüft wurde.

Dahinter stehen zwei getrennte Thesen, die nicht miteinander stehen und fallen:

1. **Zuschauer-Abstimmung** — die Community entscheidet bei umstrittenen Emotes mit. Ungeprüft.
2. **Mod-an-Mod-Übergabe** — wer gerade die Emotes durchsieht und **unsicher** ist, legt genau diese
   Auswahl den übrigen Mods zur Abstimmung vor. Das ist kein Publikums-Feature, sondern ein
   Ventil für den Zweifel einer Einzelperson. Ungeprüft, vom Betreiber aber als die interessantere
   der beiden Thesen benannt.

Für die Gestaltung folgt daraus: Voting braucht keine erste Reihe, aber die **Übergabe aus dem
Zweifel heraus** muss dort erreichbar sein, wo der Zweifel entsteht — in der Emote-Beurteilung
selbst, nicht in einem entfernten Menüpunkt.

**Rollen im System** (`ChannelAccessService`, `src/EmotePurge.Api/Auth/`):

| Rolle | Herkunft | Darf |
|---|---|---|
| Global-Admin | statische Allowlist `Auth:AdminTwitchLogins` | alles, inkl. `/admin/*` |
| Broadcaster | Twitch-User-ID des Kanalinhabers | Kanal verwalten, Usage-Stats, Purge |
| Live-Moderator | live gegen Twitch geprüft, nicht dauerhaft gecacht | wie Broadcaster |
| 7TV-Editor | 7TV-Editor-Grant | **nur lesen** — Usage-Stats, kein Management, kein Activity-Feed |
| eingeloggt, sonst nichts | jeder Twitch-Account | Vote-Session-Listen sehen, abstimmen wo Zielgruppe |

## Product Purpose

EmotePurge misst dauerhaft mit, welche Emotes in einem Twitch-Chat tatsächlich benutzt werden, und
entfernt die ungenutzten anschließend gebündelt aus dem 7TV-Set des Kanals.

Das Problem, das es löst: aktive 7TV-Channel sammeln über Monate hunderte Emotes an, von denen
viele nie oder kaum benutzt werden. Von Hand herauszufinden, welche das sind, ist mühsam und
subjektiv.

**Erfolg** ist ein ausgeführter Mass-Delete: das Set ist danach kleiner und relevanter als vorher,
und die Auswahl beruht auf gemessener Nutzung statt auf Erinnerung. Alles davor ist Vorbereitung.

## Positioning

Messung **und** Vollzug in einem Werkzeug. Ein Nachbarprodukt kann eine Statistik anzeigen oder eine
Löschfunktion anbieten — der Unterschied hier ist, dass die gemessene Zahl und der Löschknopf
dieselbe Oberfläche teilen und dieselbe Auswahl meinen.

Drei Eigenschaften tragen das:

- **Die Messung läuft dauerhaft, sie ist kein Snapshot.** Ein Bot sitzt anonym im IRC des Kanals und
  zählt fortlaufend; Zeiträume sind frei wählbar, weil die Rohdaten über Wochen vorliegen.
- **Der Schreibzugriff geht direkt gegen 7TV**, gebündelt statt Emote für Emote durch die Liste.
- **Das 7TV-Write-Token verlässt den Browser nicht** (Zero-Knowledge-Prinzip, s. `Architectur.md`) —
  es liegt in `sessionStorage`, nicht auf dem Server.

## Operating Context

**Die Nutzungsszene ist eine bewusste Aufräum-Sitzung am Desktop, nach dem Stream, mit Zeit**
(bestätigt 2026-08-06). Kein Live-Betrieb, kein zweiter Monitor, kein Zeitdruck, keine
Unterbrechungen. Mobile Nutzung ist Pflichtübung, kein Designtreiber.

Das Werkzeug ist **asymmetrisch getaktet**: eine passive Messphase, die wochenlang ohne den Nutzer
läuft, und eine aktive Auswertungssitzung, die selten stattfindet. Wer die Seite öffnet, kommt nach
langer Abwesenheit zurück.

Voraussetzungen und Reibungspunkte, die faktisch zum Ablauf gehören:

- Der Kanal muss zuerst **gejoint** werden, damit überhaupt Daten entstehen. Vor dem ersten Join und
  in den ersten Tagen danach ist das Werkzeug legitim leer.
- Der Mass-Delete verlangt ein **7TV-Write-Token, das der Nutzer manuell aus den DevTools kopiert**.
  7TV bietet keinen Login-Redirect an (untersucht und dokumentiert, s. `docs/`); dieser Handgriff ist
  auf absehbare Zeit nicht wegzugestalten.
- Datenmengen: bis ~900 Emotes je Set, Listen entsprechend lang.
- Löschungen wirken in einem **fremden System** (7TV) und sind von hier aus nicht rückholbar.

## Capabilities and Constraints

**Bestätigte Funktionalität** — neun Flächen unter `web/src/app/features/`: Landing, Login, Overview,
Usage-Stats-Grid je Kanal, Voting (Liste + Detail), eigene Abstimmungen kanalübergreifend,
Channel-Workspace mit Aktivitätsfeed, App-Shell, Admin-Bereich (Monitoring, Kanäle, Nutzer,
Audit-Log).

**Zugang:** offener Twitch-OAuth-Login für jeden, kein Invite-Code, keine Registrierungssperre.
Session als HttpOnly-Cookie, 14 Tage gleitend, serverseitig sofort invalidierbar. Eine Allowlist
existiert nur für den Global-Admin.

**Rechtliche Fläche:** Impressum und Datenschutzerklärung (de/en) sind seit 2026-09-24 live (#247)
— das Impressum nennt Klarnamen und c/o-Adresse, der Zugang zur Seite bleibt offen. `robots.txt`
sperrt weiterhin bewusst alle Crawler (`Disallow: /`, entschieden 2026-09-24) — Wachstum läuft über
Empfehlung, nicht über Suchmaschinen (Brand Commitment/Prinzip 5, „Erklären statt verkaufen"); eine
Öffnung ist damit unabhängig von den Rechtstexten eine eigene, spätere Produktentscheidung.

**Sprache:** de und en, je 537 Schlüssel (`web/public/i18n/`). Deutsch ist die längere Sprache und
die Referenz für Wortlängen.

**Harte technische Decken**, die Produktentscheidungen begrenzen:

- Ein Twitch-Account darf **maximal 100 Chatrooms gleichzeitig** gejoint haben. Das begrenzt die
  Anzahl gleichzeitig betreuter Kanäle, unabhängig von Servergröße.
- 7TVs REST-Cache kann 10–30 min veraltet sein; die EventAPI trägt 500 Subscriptions je Verbindung
  (2 je Kanal). Frisch angelegte Emotes können deshalb kurzzeitig fehlen.

**Terminologie** (gilt in beiden Sprachen und im Code): Channel · Emote-Set · Nutzung/Usage ·
Vote-Session · Purge · Slot.

**Architektur-Constraint mit Designfolge:** Farbwerte stehen an genau einer Stelle
(`web/src/styles.css`), und `npm run lint` verbietet Tailwind-Paletten-Utilities unterhalb
`web/src/app/`. Das ist eine Architektur-, keine Ästhetikentscheidung — und sie ist der Grund,
warum ein kompletter visueller Weltwechsel den Tokenblock plus `shared/ui/` kostet und nicht die
Templates. **Diese Disziplin bleibt**, unabhängig von der Barrierefreiheits-Entscheidung unten.

**Explizit unentschieden** (nicht erfinden, nicht stillschweigend beantworten):

- **Der Wert des Votings ist unvalidiert** (s. Users). Es wurde nie unter realen Bedingungen
  geprüft, ob Streamer oder Mods es wollen. Es darf deshalb weder ausgebaut noch abgeschrieben
  werden, bevor jemand es benutzt hat — beides wäre eine Entscheidung ohne Datenlage, und das ist
  bei einem Werkzeug, dessen ganzer Zweck „messen statt raten" ist, die falsche Bewegung.

## Brand Commitments

**Bindend** (bestätigt 2026-08-06):

- **Der Name EmotePurge.**
- **Zweisprachigkeit de + en.** Jede Typografie- und Layoutentscheidung muss deutsche Wortlängen
  aushalten.

- **Die Marke ist `web/public/logo.svg`** — eine Datei, flaches Guide-Türkis, trägt auf Graphit wie
  auf Papierweiß. Es gibt keine helle Zweitfassung und keine getrennte Icon-Version; alle
  ausgelieferten Rasterdateien werden daraus abgeleitet (`web/branding/README.md`).

**Ausdrücklich nicht bindend** — ein visueller Neuanfang dürfte es ersetzen:

- **Die Gleichwertigkeit von Hell- und Dunkelmodus.** Achtung, sauber getrennt: „nicht bindend"
  heißt **nicht** „wird abgeschafft". Beide Modi sind vollständig gebaut, ausgeliefert und über
  `theme-init.js` schaltbar. Ihre Abschaffung wäre eine eigene, zu treffende Entscheidung — kein
  Nebeneffekt eines Redesigns.

**Beobachteter Tonfall** (aus der bestehenden Copy abgelesen, vom Betreiber nicht als Zusage
bestätigt): duzend, nüchtern-direkt, ohne Superlative — „Bring Ordnung in dein 7TV-Emote-Set",
„Kein Muss". Wer den Ton ändert, ändert ihn bewusst.

## Evidence on Hand

**Vorhanden:**

- Echte Produktionsdaten auf `emotepurge.app` (VPS), inklusive Kanälen in der ~900-Emote-Größenordnung.
- Marke/Icons: `web/public/logo*.png`, `favicon.ico`, `apple-touch-icon.png`, PWA-Icons in 192/512
  inkl. maskable, `manifest.webmanifest`.
- `LICENSE` am Repo-Root: GNU AGPL v3 — real und zitierfähig.

**Nicht vorhanden. Zukünftige Arbeit darf das nicht erfinden:**

- Keine Produkt-Screenshots und **kein OG-Image**.
- Keine Testimonials, Nutzerzahlen, Bewertungen, Fallstudien oder Presse.
- Keine Preise, keine Pläne, keine Verfügbarkeitszusagen (kein SLA).

Wo eine Fläche einen Produktbeweis braucht, ist der einzige ehrliche Beweis das Produkt selbst —
ein echter Screenshot müsste erst erzeugt werden.

## Product Principles

1. **Gemessene Nutzung schlägt Meinung — aber sie urteilt nicht.** Die Zahl ist Beweismittel, nicht
   Urteil: ein seltenes Emote kann aus gutem Grund bleiben (Insider, Meme, Sub-Perk). Die
   Entscheidung fällt pro Emote und beim Menschen (bestätigt 2026-08-06). Voting würzt diese
   Entscheidung, ersetzt sie nie.
2. **Eine Person entscheidet — Abstimmung ist ein Ventil, kein Ablauf.** Es gibt keinen
   koordinierten, gleichzeitigen Team-Workflow und nichts muss für Parallelarbeit gebaut werden.
   Die *asynchrone* Übergabe bei Unsicherheit (eine Person legt anderen eine Auswahl vor) ist davon
   ausdrücklich ausgenommen — sie bedient den Zweifel einer Einzelperson, nicht ein Team.
3. **Seltener Besuch, kein Dauerbetrieb.** Wer die Seite öffnet, war lange weg. Sie muss beim
   Wiedereinstieg von selbst erklären, wo man steht — ohne zur Anleitung zu werden.
4. **Unwiderruflichkeit ist ein Produktversprechen.** Gelöscht wird in einem fremden System, das wir
   nicht zurückdrehen können. Die Stufung Auslösen → Bestätigen → Vollziehen ist eine Zusage an den
   Nutzer, keine Stilfrage.
5. **Erklären statt verkaufen.** Das Produkt ist offen zugänglich, wird aber nicht beworben; Wachstum
   läuft über Empfehlung. Die öffentliche Fläche schuldet Verständlichkeit und Vertrauen, keine
   Conversion.

## Accessibility & Inclusion

**Es besteht keine formale WCAG-Zusage mehr** (Entscheidung des Betreibers, 2026-08-06). Der
bestehende Bestand ist gegen WCAG AA gebaut — mit gerechneten Kontrastnachweisen im Tokenblock und
axe-Prüfungen — aber das war eine Annahme der KI, keine Produktzusage. Der Anspruch lautet ab jetzt:
**lesbar**.

Was daraus folgt, damit die Entscheidung nicht mehr trägt als gemeint:

- Neue Farben schulden keinen gerechneten Nachweis und kein Kontrast-Gate mehr.
- Bestehende Kontrastwerte werden dadurch **nicht schlechter** — niemand muss sie senken.
- Die Trennung von Farbwert und Farbrolle bleibt trotzdem bestehen (s. Capabilities and
  Constraints); sie hat nichts mit Barrierefreiheit zu tun.
- Tastaturbedienbarkeit und Fokus-Sichtbarkeit bleiben Grundhandwerk, kein Zertifikat.
