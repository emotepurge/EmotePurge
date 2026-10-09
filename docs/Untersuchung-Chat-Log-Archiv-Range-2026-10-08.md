# Untersuchung: Chat-Log-Archiv logs.cyex.app, Range-Abfrage (`from`/`to`) — 2026-10-08

Auftrag: Klären, ob der Backfill-Harness (#69) statt der Tagesabrufe die Range-Abfrage des neuen
Chat-Log-Archivs nutzen kann, und welche Folgen das für die Backfill-Spec hat.
Methodik: zwei Sonden am 2026-10-08 von der Devbox, zusammen 42 Requests (12 + 30), strikt
sequenziell, Abstand mindestens 10 s, `User-Agent: EmotePurge-probe (+https://emotepurge.app)`.
Es wurde kein 429 provoziert und keine Lastprobe gefahren. Kennzeichnung: **gemessen** (Wert aus den
Sonden) / **Inferenz** (eigene Ableitung, nicht direkt geprüft) / **offen**.

Kein Produktivcode geändert, kein `DECISIONS.md`-Eintrag; der kommt mit der Spec.

---

## 1. Fazit vorab

**Die Range-Abfrage taugt als Grundlage des Backfills, und zwar in Wochenblöcken.**

1. Sie liefert dasselbe Format und dieselbe Zeilenmenge wie der Tagesabruf, in einem Request statt
   sieben. Ein halbes Jahr eines großen Kanals (handofblood) kostete 26 Requests und 5,5 min.
2. Es wurde bis 368 MB und 1,06 Mio Zeilen in einem Request kein serverseitiges Limit beobachtet.
   Das ist aber kein Grund, große Blöcke zu fahren (Abschnitt 4).
3. Zwei Annahmen des Harness-Designs fallen weg: ein Tag ohne Daten ist kein Fehler (404 heißt
   "keine Daten"), und ein Gesamt-Hash eignet sich nicht als Identität (Abschnitt 3.7).
4. Die Rate-Limit-Schwelle unter Dauerlast ist **unbekannt** und wurde bewusst nicht erkundet.

## 2. Anlass

Der Harness aus #69 holt pro Tag (`channelid/{id}/{y}/{m}/{d}?raw`, 1,5 s Abstand). Das Design vom
2026-09-05 hatte nur den Tagesendpunkt gemessen, und zwar am alten Archiv (logs.zonian.dev, seit
2026-10-08 offline). Am 2026-10-08 hat der Betreiber von logs.cyex.app unserer Nutzung zugestimmt,
mitgeteilt, dass kein API-Key nötig ist, auf die Range-Abfrage mit `from`/`to`
([Doku](https://logs.cyex.app/docs#get-channel-logs)) hingewiesen und um genau eines gebeten: einen
Link auf https://logs.cyex.app/ überall dort, wo importierte Daten angezeigt werden. Ein
persönliches Credit ist nicht gewünscht.

## 3. Befunde

### 3.1 Doku und Server

- `/docs` ist eine clientgerenderte SPA; der Inhalt steckt im Bundle `/assets/docs-*.js`. **Gemessen.**
- `from`/`to`: RFC 3339, wirken nur gemeinsam. Formate `json`, `jsonBasic`, `ndjson` ("streams well
  for large ranges"), `raw` (IRC-Zeilen wie empfangen), ohne Flag Plain Text. Dazu `reverse`,
  `limit`, `offset`. Die Doku nennt "a narrower range is lighter" und keine Limits.
- Der Server meldet `x-rustlog-capabilities: arbitrary-range-query,search,stats,namehistory,firehose,version,active-channels`
  (Inferenz: rustlog-Fork).

### 3.2 Format und Äquivalenz mit dem Tagesabruf

- `?raw` mit Range liefert `text/plain; charset=utf-8` im selben justlog-Rohformat
  (`@tmi-sent-ts=...;room-id=...;... PRIVMSG #kanal :text`). Alle Zeilen der Sonden hatten `tmi-sent-ts`.
- `channelid/{id}?from&to` funktioniert; für papaplatte 2026-10-02 war das Ergebnis byte-identisch
  zum Tagesabruf (24.317 Zeilen, 9.238.218 Byte).
- Range 10-01..10-04 gegen die Tage 10-01/02/03: 186.114 Zeilen = 81.994 + 24.317 + 79.803, Bytes
  ebenso gleich. Sortiert sind beide Dateien identisch (**gleiche Zeilenmenge**). Ungesortet
  unterscheiden sich 66 Zeilen in der Reihenfolge, alle mit gleicher `tmi-sent-ts` (Inferenz aus den
  ersten drei Diff-Paaren).
- Die 7-Tage-Range ist mengengleich zum entsprechenden Ausschnitt der 30-Tage-Range.

### 3.3 Zeitsemantik

- **`to` ist exklusiv**, auf Millisekunden-Ebene: Fenster `T-60s .. T` mit T = Zeitstempel einer
  bekannten Nachricht lieferte 32 Zeilen, die Zeile bei T fehlte. Millisekunden-Präzision wird
  akzeptiert. **Gemessen.**
- Tage sind **UTC**: die Zeilen der Range gruppieren sich exakt nach UTC-Tagen, die Tageszähler
  decken sich mit den Tagesabrufen. **Gemessen.**
- Die Range-Ausgabe ist aufsteigend nach `tmi-sent-ts` sortiert (geprüft für alle Range-Dateien).
- **`from`-Inklusivität nicht geprüft.** Aneinandergrenzende `[von, bis)` sind trotzdem plausibel
  lückenlos (Inferenz); an Blockgrenzen wurde nicht auf doppelte oder fehlende Zeilen geprüft.

### 3.4 404-Semantik und Lücken

- Unbekannter Kanal (`sensitron`), Zeitraum vor Archivbeginn (papaplatte 2015) und ein **bekannter
  Kanal mit leerem Fenster** (zokka 10-03..05) liefern alle `404`, Body `Not found` (9 Byte). Die
  drei Fälle sind nicht unterscheidbar. **Gemessen.**
- Folge (Inferenz): 404 bedeutet "keine Daten im Fenster", nicht "Fehler". Ein Kanal ohne Archiv ist
  damit von einem Kanal mit Lücke am Fenster nicht zu trennen.
- **Lücken sind unmarkiert.** zokka 30 Tage: 22 UTC-Tage mit Daten (die Wochenenden fehlen, das
  passt zum bekannten Streamplan), ohne Header oder Platzhalterzeile. Eine Lücke ist nur an fehlenden
  Zeitstempeln erkennbar und nicht von einem stillen Tag zu unterscheiden.

### 3.5 Übertragung

- Antworten sind **gestreamt**: kein `Content-Length` in irgendeiner 200-Antwort. TTFB bei 30 Tagen
  0,39 s gegen 13,3 s Gesamtzeit (Inferenz: progressives Streaming). Ein mid-stream-Abbruch ist nur
  an einem Transportfehler oder an abgeschnittenen Zeilen erkennbar; ob der Server dabei einen
  Transportfehler auslöst, wurde nicht getestet.
- **Brotli** wird angewendet (`content-encoding: br`), aber schwach: 15 % Ersparnis, Faktor 1,18 bis
  1,22 (Inferenz: niedrige Stufe fürs Streaming). Der Aufwand pro Kanal bleibt damit praktisch die
  unkomprimierte Größe.
- Durchsatz 10 bis 28 MB/s unkomprimiert je Request; kleine Antworten (< 0,3 MB) sind TTFB-dominiert
  (0,2 bis 2,0 s, einzelne Ausreißer ohne erkennbare Ursache).

### 3.6 Caching und Rate-Limit-Signale

- Doku: `public, max-age=36000` für beendete Ranges, `no-cache` für offene Ranges und datums­
  adressierte Requests. **Beobachtet:** auch Tagesabrufe lieferten `public, max-age=36000`
  (Abweichung von der Doku). Alle Antworten hatten `cf-cache-status: DYNAMIC`, es gab keinen
  Cloudflare-Treffer.
- **Keine** `RateLimit-*`, `Retry-After` oder `X-RateLimit-*` Header in 42 Antworten, kein 429.
  Der Server kündigt ein Limit also nicht an.

### 3.7 Der einmalige Hash-Unterschied

Ein Wiederholungsabruf (papaplatte 2026-10-02) lieferte bei **identischer Byte- und Zeilenzahl** einen
anderen SHA-256 als Sonde 1 und als ein weiterer Abruf 30 s später, der wieder bitgleich zu Sonde 1
war. Der abweichende Body wurde nicht aufgehoben, die Ursache ist **offen** (Inferenz, ungeprüft:
Reihenfolge bei gleicher Millisekunde, wie in 3.2 gesehen; ein geänderter Wert wäre die Alternative).
Ein früherer Befund ("Archiv ändert geschlossene Tage") passt zur Vorsicht. Konsequenz: Identität
und Dedup über die **Nachrichten-`id=`**, nie über einen Body- oder Gesamt-Hash.

## 4. Blockgrößen

papaplatte, ein Request je Fenster:

| Fenster | Wire/Body | Zeilen | Dauer |
|---|---|---|---|
| 3 Tage | 65,0 MB | 186.114 | 3,5 s |
| 7 Tage | 38,0 MB | 107.597 | 2,8 s |
| 30 Tage | 367,9 MB | 1.057.089 | 13,3 s |

Das Tagesvolumen streut stark: 321 bis 128.472 Zeilen pro Tag, 11 von 30 Tagen unter 3.000 Zeilen.
Die Größe eines Fensters ist deshalb aus der Länge nicht vorhersagbar.

handofblood, 6 Monate ab 2026-04-09 in 26 Wochenblöcken `[von, bis)`, alt nach neu, 10 s Pause:

- Alle 26 Blöcke 200, alle 182 UTC-Tage mit Daten, kein Verlangsamungstrend (TTFB 0,19 bis 1,97 s).
- Summe: **705,4 MB** unkomprimiert, **577,5 MB** Wire, **1.732.197 Zeilen**, Wall **327 s**
  (reine Transferzeit rund 47 s, der Rest Pausen). Größter Block 64,8 MB, größter Tag 76.590 Zeilen.

| Monat | Tage mit Daten | Zeilen | ca. MB unkomprimiert |
|---|---|---|---|
| 2026-04 (ab 09.) | 22 | 412.803 | 161 |
| 2026-05 | 31 | 216.590 | 91 |
| 2026-06 | 30 | 421.744 | 173 |
| 2026-07 | 31 | 315.289 | 127 |
| 2026-08 | 31 | 185.249 | 76 |
| 2026-09 | 30 | 180.045 | 78 |
| 2026-10 (bis 07.) | 7 | 477 | 0,2 |

Zeilen und Tage sind gemessen; die MB-Spalte ist **Inferenz** (Blockbytes proportional zur Zeilenzahl
auf Tage verteilt, weil Blöcke Monate überlappen). Die dünnen Wochen ab Mitte August (370 bis 480
Zeilen) widersprechen nicht dem bekannten Urlaub; Block 09-10..09-17 enthält noch 49.187 Zeilen vor
dessen Beginn.

## 5. Folgen für die Backfill-Spec

1. **Wochenblöcke `[von, bis)`** statt Tagen und statt Monats-Ranges: planbare Größe (typisch 10 bis
   65 MB, Spitze unbekannt), kurze Wiederholung bei Abbruch, keine Überlappung nötig.
2. **404 = keine Daten**, kein Fehlerzustand und kein Abbruchgrund. Leere Blöcke zählen als erledigt.
3. **429 als Normalzustand** einplanen: Pause und Wiederaufnahme am letzten abgeschlossenen Block,
   Abstand zwischen Requests konfigurierbar (Startwert aus der Messung: 10 s; die Sonden zeigten bei
   diesem Abstand und 5,5 min Dauer kein Limit).
4. **Keine Body-Hash-Identität** (3.7); Dedup über `id=` der Nachricht, Reihenfolge innerhalb einer
   Millisekunde als unbestimmt behandeln.
5. **Abdeckung als tatsächlich abgedeckter Bereich anzeigen**, nicht als angefragten: unmarkierte
   Lücken (3.4) und fehlender Archivbeginn machen "Zeitraum X bis Y angefragt" zu einer falschen Aussage.
6. **Last pro großem Kanal rund 600 MB Wire je Halbjahres-Backfill** (handofblood: 578 MB). Das ist
   für den Betreiber des Archivs spürbar und gehört deshalb **auf Anfrage**, nicht automatisch oder
   für alle Kanäle.
7. **Abgeschnittene Antworten** (kein `Content-Length`) werden clientseitig als Transportfehler oder
   an einer unvollständigen letzten Zeile erkannt; der Block gilt dann als nicht erledigt.
8. **Quellenangabe**: überall, wo importierte Daten erscheinen, ein Link auf https://logs.cyex.app/
   (Bedingung des Betreibers, ohne persönliches Credit).

## 6. Offene Punkte

- **429-Schwelle unter Dauerlast.** Absichtlich nicht gemessen. Die Sonden (5,5 min bei 10 s Abstand)
  berühren die frühere Erfahrung am alten Archiv (429 nach rund 30 min bei 1,5 s Abstand) nicht und
  bestätigen oder widerlegen sie nicht. Plan: den Betreiber fragen und 429-Antworten im Produktivbetrieb
  protokollieren, statt selbst gegen das Limit zu fahren.
- **Archivbeginn für handofblood.** Der Lauf begann am 2026-04-09 mit Daten am ersten Tag; der
  tatsächliche Beginn liegt am oder vor diesem Datum und wurde nicht ermittelt.
- **`from`-Inklusivität** und Blockgrenzen ohne Doppel- oder Fehlzeilen sind ungeprüft.
- **Obergrenze der Range-Größe** (über 30 Tage oder 368 MB) und Verhalten bei mid-stream-Abbruch.
- **Ursache des Hash-Unterschieds** (3.7) und der TTFB-Ausreißer (1,45 s bei 7 Tagen, 1,97 s bei 0,1 MB).
- **Wirkung gleichzeitiger Last** (mehrere Backfills parallel) und Reproduzierbarkeit der Durchsatzwerte
  (je eine Messung).
