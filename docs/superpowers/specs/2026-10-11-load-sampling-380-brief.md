# Arbeitsauftrag #380, erster Schnitt: Last-Erfassung und Aufbewahrung

Stand 2026-10-11. Entscheidungen bestätigt und als „Decided"-Kommentar in #380 eingetragen. Dieser
Auftrag beschreibt Absicht, Verträge und Grenzfälle, keinen fertigen Code.

## Ziel

Worker und Api messen alle 30 s ihre eigene Last und die Host-Last und schreiben sie zusammen mit
der Arbeitsmenge desselben Intervalls in **eine** Zeile. Stündlich wird zu Stundenwerten verdichtet.
Rohwerte leben 7 Tage, Stundenwerte 13 Monate. Keine Anzeige, kein Endpoint.

## Nicht im Schnitt

UI, Admin-Endpoint, SSE, Zuordnung zu Kanälen, Zuschauerzahlen, Postgres-/Redis-Last.
Ausgeschlossen: Docker-Socket, Prometheus/Grafana/cAdvisor o. Ä.

## Bausteine

1. **Parser (rein, ohne Dateisystem-Zugriff im Kern).** Je Quelle eine Funktion „Dateiinhalt →
   Werte": `cpu.stat` (`usage_usec`), `cpu.max` (Quota und Periode; `max` = unbegrenzt),
   `memory.current`, `memory.max` (`max` = unbegrenzt), `/proc/stat` (aggregierte `cpu`-Zeile:
   Busy- und Gesamt-Jiffies, iowait/steal getrennt halten, falls billig), `/proc/loadavg` (1/5/15),
   `/proc/meminfo` (mindestens `MemTotal`, `MemAvailable`, `SwapTotal`, `SwapFree`).
   Fehlende Datei, leerer oder unerwarteter Inhalt → `null` für den betroffenen Wert, nie eine
   Exception nach außen. cgroup v1 (Dateien fehlen) und `dotnet run` außerhalb eines Containers
   sind Normalfälle, keine Fehler. Getrennt davon ein dünner Leser, der die Dateien holt.
2. **Snapshot-Quelle.** Liefert Zeitpunkt, Prozess-CPU-Zeit (`Process.TotalProcessorTime`) und die
   geparsten Werte. Interface in `Core`, Implementierung in `Infrastructure` (Regel 5).
3. **Arbeitsmengen-Quelle.** Liefert je Sample: aktive Kanäle (beide Quellen), live gemeldete Kanäle
   (nur Worker), Chatzeilen und Emote-Treffer **im Intervall** (nur Worker). In der Api bleiben die
   drei Worker-Spalten `null`. Die Intervallzähler dürfen den bestehenden Usage-Flush **nicht**
   beeinflussen — kein zweites Leeren desselben Puffers, sondern eigene monotone Zähler, deren
   Differenz zum letzten Sample gespeichert wird. Woher „live" kommt, bitte am
   `TwitchLivePollWorker` festmachen (letzter Poll-Stand); ist noch kein Poll gelaufen → `null`,
   nicht `0`.
4. **Sampler.** Ein `BackgroundService`, derselbe Code in Api und Worker, registriert über
   `AddEmotePurgeInfrastructure` oder je Host — Quelle (`Api`/`Worker`) als Spalte. Takt 30 s
   (konfigurierbar, plus Schalter zum Abschalten, Default an). Darf den Host **nie** blockieren oder
   abstürzen lassen: jeder Durchlauf in try/catch, DB-Fehler → gedrosselte Warnung und weiter,
   kein Retry-Stau, Start nicht vor dem Host-Start verzögern. Kein `AppDbContext` im Sampler selbst,
   sondern über einen Service (Regel 4 sinngemäß).
5. **Verdichtung.** Läuft nur im Worker, unabhängig von `Retention:Enforce` (sie löscht nichts).
   Verdichtet je Quelle jede **abgeschlossene** Stunde, für die es Rohwerte, aber noch keine
   Stundenzeile gibt — holt also nach einem Ausfall alle Stunden innerhalb des Roh-Fensters nach.
   Idempotent (Upsert auf `(Quelle, Stunde)`).
6. **Aufbewahrung.** Zwei neue Regeln in `RetentionPolicy`, durchgesetzt vom bestehenden
   `DataRetentionWorker`, gleiche Enforce-/Trockenlauf-Semantik wie die übrigen Regeln.

## Datenvertrag

**Rohzeile** (Name nach Bestandskonvention, z. B. `SystemLoadSample`): Zeitpunkt (UTC), Quelle,
Prozess-ID oder Start-Zeitpunkt des Prozesses (um Neustarts zu erkennen), Intervalllänge bzw.
Zeitpunkt des Vorgänger-Samples, alle Rohwerte aus Baustein 1 und 2, die Arbeitsmenge aus
Baustein 3. Alles außer Zeitpunkt/Quelle nullable. Index auf `(Quelle, Zeitpunkt)`.

**Gespeichert werden Rohwerte, normalisiert wird erst in der Anzeige.** Für die Stundenwerte heißt
das aber: kumulative Zähler (Prozess-CPU-Zeit, `usage_usec`, `/proc/stat`-Jiffies) sind als
Rohwert nicht verdichtbar — Mittel/Max/p95 eines monoton steigenden Zählers ist sinnlos. Die
Verdichtung bildet deshalb je Sample-Paar die Rate (genutzte CPU-Sekunden pro Wandsekunde bzw.
Host-Busy-Anteil) und verdichtet diese. Normalisierung auf das Quota (`cpu.max`) bleibt Sache der
Anzeige; das Quota wird mitgeführt (letzter Wert der Stunde genügt).

**Stundenzeile** (z. B. `SystemLoadHourly`): Stunde (UTC, Beginn), Quelle, Sample-Zahl; für jede
Last-Größe (Prozess-CPU-Rate, cgroup-CPU-Rate, Host-CPU-Busy-Anteil, `memory.current`,
Host-Speicher belegt, loadavg 1) **Mittel, Max, p95**; die Grenzwerte (`cpu.max`, `memory.max`,
`MemTotal`) als letzter Wert; für die Arbeitsmenge **Summen** (Chatzeilen, Emote-Treffer) sowie
Max/Mittel für aktive und live Kanäle. Eindeutigkeit `(Quelle, Stunde)`.

## Grenzfälle, die abgedeckt sein müssen

- Prozess-Neustart oder Zählerrücksprung: negatives Delta → Paar verwerfen, nicht als 0 werten.
- Lücke (Worker lag 10 min still): Paare über mehr als ~2 Intervalle nicht als ein Sample werten.
- Stunde mit nur einem Sample oder ohne gültiges Paar: Raten `null`, Gauges trotzdem verdichten.
- Api-Zeilen: Worker-Spalten `null`, Summen dürfen daraus nicht `0` machen, wo „unbekannt" gemeint ist.
- `max`/unbegrenzt in `cpu.max`/`memory.max` → `null` (oder eigener Marker), nicht `long.MaxValue`.
- Uhr: alles UTC, Stundengrenze per Zeitstempel, nicht per Sample-Zähler.
- Zwei Api-Replicas sind heute nicht vorgesehen; die Prozesskennung in der Zeile hält das trotzdem
  auseinander.

## Tests (Regeln 11/12)

- Parser: reine Unit-Tests in `tests/EmotePurge.Infrastructure.Tests/Unit/` mit echten
  Beispieldateien aus cgroup v2 und `/proc` (inkl. `max`, fehlender Datei, Müll).
- Raten-/Verdichtungslogik, soweit rein: Unit-Tests (Neustart, Lücke, Einzel-Sample, p95).
- Verdichtung + Aufbewahrung gegen Postgres: Integrationstests (Testcontainers) — Upsert-Idempotenz,
  Nachholen nach Lücke, Löschung nach 7 Tagen bzw. 13 Monaten, Trockenlauf löscht nichts.
- Sampler-Robustheit: ein Durchlauf mit werfender Quelle/DB lässt den Dienst weiterlaufen.
- Arbeitsmengen-Zähler im Worker: container-frei in `tests/EmotePurge.Worker.Tests`, falls sie dort leben.

## Doku im selben Paket

- `docs/DECISIONS.md` (englisch, neuer Eintrag oben): neue Tabellen, Aufbewahrung 7 Tage/13 Monate,
  Verdichtung unabhängig von Enforce, warum Raten statt Rohzählern verdichtet werden. Im selben
  Commit wie Tabelle/Retention (Regel 3).
- `docs/Architectur.md`: DB-Schema und Hosted-Service-Liste nachziehen.
- `CLAUDE.md`: Zahl und Liste der Worker-Hosted-Services.
- `docs/Operations.md`: neue Konfigurationsschlüssel; Hinweis, dass Prod eine Migration braucht.
- Datenschutzerklärung: prüfen, ob die Aufbewahrungsliste Messdaten nennen muss. Erwartet: nein
  (keine personenbezogenen Daten) — Ergebnis im PR festhalten.

## Migration

Eine Migration (Name z. B. `AddSystemLoadSampling`). Auf Prod läuft sie von Hand vor den Images.
