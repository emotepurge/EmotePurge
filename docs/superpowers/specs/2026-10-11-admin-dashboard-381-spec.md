# Admin-Dashboard: Aktivität und Last (#381)

**Stand:** 2026-10-11  
**Status:** Umsetzungsspec, kein Code  
**Zielseite:** bestehende Admin-Monitoring-Seite unter `/admin/monitoring`  
**Lieferung:** A ohne Lastsammlung; B auf Basis der Sammlung aus #380/#385

## 1. Ziel und Erfolgskriterium

Die Seite beantwortet beim ersten Blick zwei Betreiberfragen:

1. **Wie stark ist die VPS- und Containerlast gerade, und steigt sie?**
2. **Welche beobachtete Arbeit läuft zur selben Zeit?**

Primärzweck ist die empirische Kapazitätsplanung für ein VPS-Upgrade. Die Seite zeigt Host-CPU und Host-RAM getrennt von den Ressourcen der Api- und Worker-Container. Sie legt diese Reihen neben die erfassten Kanäle, Chatzeilen und Emote-Treffer. Das macht zeitliche Zusammenhänge sichtbar; es beweist keine Kausalität und ersetzt keine Messung von Disk, Netzwerk oder Datenbanklast.

**Lieferstufe A** zeigt nur Werte, die im Checkout vor #385 bereits erhoben werden. Damit kann A Aktivität und den Zustand der vorhandenen Rate-Limit-Telemetrie einordnen, aber noch nicht sagen, wie nah der VPS an einer Ressourcenbegrenzung ist. **Lieferstufe B** ergänzt die Rohmessungen aus #380. Erst B beantwortet die VPS-Frage direkt.

Die Seite erweitert die bestehende Admin-Monitoring-Ansicht, erzeugt keine zweite Dashboard-Route und ersetzt weder #382 (Such- und Filterlisten) noch #383 (Log-Viewer).

## 2. Geprüfter Bestand und Grenzen der Daten

Die Angaben in diesem Abschnitt beziehen sich auf den angeforderten Checkout `aa603260` vor #385. Die relevanten Dateien wurden dort geprüft. Die späteren Rohdaten aus #380 wurden read-only aus `origin/feat/380-load-sampling` gelesen; die Entscheidung vom 2026-10-11 steht in dessen `docs/DECISIONS.md`.

### 2.1 Admin-Seite, API und Live-Stream

- `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs:26-31` legt die `/api/admin`-Gruppe mit `GlobalAdminAuthorizationFilter` an. Health, Rate-Limit-Snapshot und Roster liegen bereits darin; `/api/admin/roster` liefert unter anderem `trackedChannelCount` und konfigurierte Obergrenzen (`AdminEndpoints.cs:189-256`).
- `GET /api/admin/health` trennt den privaten Worker-Snapshot von `GET /api/worker/health`. Die bestehende Monitoring-Seite zeigt Worker-, Twitch-, 7TV- und Flush-Zustand. `web/src/app/features/admin/admin-monitoring-page.ts:61-65,648-772` aktualisiert Health über `worker.health` und den Rate-Limit-Snapshot derzeit im 30-Sekunden-Takt.
- `GET /api/admin/rate-limits` liefert effektive Policy-Konfiguration und globale Zähler je Policy: angenommene und abgelehnte Anfragen im gleitenden Minuten- und 24-Stunden-Fenster. Redis liefert zusätzlich Cache-Zähler, Provider-Anfragen und echte Upstream-429er sowie eine letzte Rate-Limit-Stichprobe. Der bestehende Vertrag bleibt erhalten.
- Der Admin-Stream ist `GET /api/admin/live`. Er überträgt dünne Änderungsereignisse; die Seite lädt die betroffenen REST-Ressourcen nach. Das ist im Architekturüberblick `docs/Architectur.md:60-70` und in `src/EmotePurge.Api/Endpoints/LiveEndpoints.cs:44-50,153-169` beschrieben.

### 2.2 Vorhandene Daten für A

- `UsageStats` enthält pro Emote, Emote-Set, UTC-Kalendertag und Quelle `UseCount`, `BotUseCount` und `SharedChatUseCount` (`src/EmotePurge.Core/Entities/UsageStat.cs`). `Source=Live` bezeichnet den Live-Chatpfad, `Source=ChatLogArchive` den Backfill (`UsageStatSource.cs`). Das ist **keine Zahl aller Chatzeilen**.
- `ChannelLiveDays` enthält je Channel und UTC-Tag höchstens eine Zeile mit `LiveMinutes`. Das bedeutet „durch den Poller an diesem Tag als live beobachtet“, nicht „jetzt live“ und nicht eine vollständige Streamdauer.
- `Channel` und `/api/admin/roster` können den aktuellen Bestand aktiver Channels zeigen. `Channels:MaxActiveChannels` ist ein konfiguriertes Admission-Limit (Default 80); Admins sind davon ausgenommen und ein Overshoot ist laut DECISIONS akzeptiert. Der Wert ist weder ein VPS-Limit noch eine sichere Restplatzberechnung (`docs/DECISIONS.md`, 2026-09-23, „A hard cap on simultaneously active channels“).
- `User` hat `LastLogin` und `LastSeenAtUtc`, aber kein Erstellungsdatum und keine Login-Ereignishistorie (`src/EmotePurge.Core/Entities/User.cs:3-34`). Eine historische Registrierungs- oder Login-Kurve lässt sich daraus nicht korrekt rekonstruieren.
- Der Purge eines inaktiven Channels löscht nach 180 Tagen den Channel mit seiner Historie (`src/EmotePurge.Core/Services/RetentionPolicy.cs:38-42`; die Cascades für `UsageStat` und `ChannelLiveDay` stehen im EF-Modell). Darum sind diese Reihen maximal 180 Tage lang und können frühere Beiträge bereits gepurgter Channels nicht enthalten.

### 2.3 Indizes des Checkouts vor #385

Die konkreten Bestandsindexe stehen in `src/EmotePurge.Infrastructure/Migrations/AppDbContextModelSnapshot.cs`:

- `UsageStats`: eindeutiger Index `(EmoteId, EmoteSetId, Date)` mit `UseCount` als Include (`:643-646`). Er beginnt **nicht** mit `Date`; ein globaler Zeitbereich ist damit nicht abgedeckt. `BotUseCount`, `SharedChatUseCount` und `Source` sind nicht enthalten.
- `ChannelLiveDays`: eindeutiger Index `(ChannelId, Date)` mit `LiveMinutes` als Include (`:197-200`); auch hier beginnt kein Index mit `Date`.
- `AuditLogEntries`: absteigender Index auf `OccurredAtUtc` und `(ChannelName, OccurredAtUtc)` (`:68-71`). Die Audit-Retention beträgt 365 Tage.
- `Channels`: eindeutige Indexe auf `ChannelName` und `TwitchChannelId` (`:132-135`); kein Index auf `IsBotActive`.
- `Users`: eindeutiger Index auf `TwitchUsername`; keiner auf `LastLogin` oder `LastSeenAtUtc` (`:687` und Folgezeilen).
- `VoteSessions`: `(ChannelId, IsActive)` (`:766`); `Votes`: `EmoteId`, `UserId` und eindeutiger `(VoteSessionId, EmoteId, UserId)`-Index (`:720-724`).
- `ChatLogBackfillRuns`: partieller aktiver Channel-Index, `RequestedByTwitchUserId` und `(Status, Id)` (`:348-355`); kein Zeitindex auf `FinishedAtUtc`.

Diese Fakten sind für die Abfrageplanung relevant. A liest keine Audit-, Vote- oder Backfill-Historie; B nutzt die eigenen Zeitindexe aus der #380-Migration.

### 2.4 Rohdaten aus #380/#385

Der erste Schnitt wurde im angeforderten Branch `origin/feat/380-load-sampling` geprüft: `git diff --stat origin/main...origin/feat/380-load-sampling` sowie `git show` auf Entitäten, Migration, Sampler, Rollup, Retention und DECISIONS. Die Tabellen heißen `SystemLoadSamples` und `SystemLoadHourly`; es gibt keine API- oder UI-Implementierung in diesem ersten Schnitt.

`SystemLoadSamples` speichert pro Beobachtung:

| Feldgruppe | Gespeicherte Rohwerte und Einheit | Bedeutung |
|---|---|---|
| Identität/Zeit | `SampledAtUtc` (UTC), `Source` (`Api` oder `Worker`), `ProcessInstanceId` (GUID), `PreviousSampledAtUtc` (UTC oder null) | Prozessinstanz und Bezug zum vorigen erfolgreichen Sample derselben Instanz. |
| Prozess-CPU | `ProcessCpuTimeTicks` (kumulative .NET-`TimeSpan`-Ticks, 100 ns je Tick) | Lebenszeit-Zähler des einzelnen Api- bzw. Worker-Prozesses, keine Intervallrate. |
| cgroup-CPU | `CgroupCpuUsageMicroseconds` (kumulativ, µs), `CgroupCpuQuotaMicroseconds` und `CgroupCpuPeriodMicroseconds` (µs) | `cpu.stat usage_usec` und der beim Sample beobachtete `cpu.max`-Zähler/Limes des Prozess-cgroups. |
| cgroup-Speicher | `CgroupMemoryCurrentBytes`, `CgroupMemoryMaxBytes` | `memory.current` und endliches `memory.max`, in Byte. |
| Host-CPU | `HostCpuBusyJiffies`, `HostCpuTotalJiffies`, `HostCpuIowaitJiffies`, `HostCpuStealJiffies` | Kumulative `/proc/stat`-Zähler des Hosts, nicht des einzelnen Containers. |
| Host-Last | `HostLoadAverage1`, `HostLoadAverage5`, `HostLoadAverage15` | `/proc/loadavg`, mittlere runnable/uninterruptible Task-Anzahl über 1, 5 und 15 Minuten; keine Prozentwerte. |
| Host-Speicher | `HostMemoryTotalBytes`, `HostMemoryAvailableBytes`, `HostSwapTotalBytes`, `HostSwapFreeBytes` | `/proc/meminfo`, Hostwerte in Byte. |
| Arbeit | `ActiveChannels`, `LiveChannels`, `ChatLines`, `EmoteMatches` | Worker-Zähler pro erfolgreichem Sampleintervall. `ActiveChannels` ist der aktuelle DB-Bestand; `LiveChannels` kommt nur aus dem Worker. Api-Zeilen tragen nur `ActiveChannels`; die übrigen Worker-only-Felder sind null. |

Der Worker zählt Chatzeilen und Emote-Treffer über voneinander unabhängige monotone Summenzähler; ein erfolgreich persistiertes Sample bestätigt die neuen Baselines, ohne den 30-Sekunden-Usage-Flush zu leeren. `ChatLines` und `EmoteMatches` in der Tabelle sind die Intervall-Differenzen, keine kumulativen Lebenszeitwerte. Der Worker-Live-Snapshot ist bis zu einem erfolgreichen Poll nach Prozessstart unbekannt. Diese Verträge stehen in `WorkerSystemLoadWorkloadSource.cs`, `ISystemLoadWorkloadSource.cs`, `SystemLoadSamplingService.cs` und im #380-DECISIONS-Eintrag.

`cpu.stat`, `/proc`-Dateien oder cgroup-v2-Dateien, die fehlen oder nicht unterstützt werden, ergeben nullable Rohwerte. `cpu.max` mit `max` ergibt keine endliche Quota; `memory.max` mit `max` ergibt kein endliches Speicherlimit. Der Sampler darf Api und Worker nicht blockieren oder beenden.

`SystemLoadHourly` hat Primärschlüssel `(Source, HourUtc)`, `SampleCount`, Mean/Max/P95 für Prozess- und cgroup-CPU-Raten, Host-CPU-Busy, cgroup- und Host-Speicher sowie die 1-Minuten-Host-Load-Average. Es speichert Workload-Summen (`ChatLines`, `EmoteMatches`), Active-/Live-Channel-Mean und -Max sowie die zuletzt je Stunde beobachteten Quota-/Speichergrenzen. CPU-Mittelwerte sind zeitgewichtete Raten aus gültigen Zählerpaaren; Max/P95 stammen aus den gültigen Einzelraten, P95 verwendet Nearest Rank. Arbeitszähler sind Stundensummen.

Die konkreten Hourly-Spalten und Einheiten sind:

| Hourly-Spalten | Einheit/Definition |
|---|---|
| `ProcessCpuRateMean`, `ProcessCpuRateMax`, `ProcessCpuRateP95`; `CgroupCpuRateMean`, `CgroupCpuRateMax`, `CgroupCpuRateP95` | CPU-Kerne als CPU-Sekunden pro Wandsekunde; aus Differenzen kumulativer Prozess-Ticks bzw. cgroup-µs. |
| `HostCpuBusyMean`, `HostCpuBusyMax`, `HostCpuBusyP95` | Anteil 0–1 aus busy-/total-Jiffies, im UI als Prozent × 100. |
| `CgroupMemoryCurrentMeanBytes`, `CgroupMemoryCurrentMaxBytes`, `CgroupMemoryCurrentP95Bytes`; `HostMemoryUsedMeanBytes`, `HostMemoryUsedMaxBytes`, `HostMemoryUsedP95Bytes` | Byte; Host-used wird pro Sample als `MemTotal - MemAvailable` hergeleitet. |
| `HostLoadAverage1Mean`, `HostLoadAverage1Max`, `HostLoadAverage1P95` | Mittlere Taskzahl der einminütigen Load-Average; keine Prozentangabe. |
| `LastCpuQuotaMicroseconds`, `LastCpuPeriodMicroseconds`, `LastCgroupMemoryMaxBytes`, `LastHostMemoryTotalBytes` | Letzte pro Source/Stunde beobachtete endliche Grenze bzw. Host-Gesamtspeicher, in µs oder Byte. |
| `ChatLines`, `EmoteMatches`, `ActiveChannelsMean`, `ActiveChannelsMax`, `LiveChannelsMean`, `LiveChannelsMax` | Chatzeilen/Stunde, Emote-Treffer/Stunde und Channelzahlen (Mean oder maximal beobachteter Integerwert). |
| `Source`, `HourUtc`, `SampleCount` | Source `Api`/`Worker`, UTC-Stundenbeginn und Zahl gespeicherter Samples dieser Source/Stunde. |

`HostLoadAverage5` und `HostLoadAverage15` existieren nur in den Rohsamples, nicht in `SystemLoadHourly`; `HostSwap*`, `ProcessCpuTimeTicks` und `ProcessInstanceId` haben ebenfalls keine Hourly-Spalten.

Sample-Intervall ist 30 Sekunden. Api und Worker samplen separat. Der Worker rollt closable Stunden nach fünf Minuten Grace beim Start und danach stündlich um, berechnet die jüngsten drei Stunden neu und füllt ältere Stunden nach, für die noch Rohdaten vorliegen. Rohdaten werden sieben Tage, Stundenwerte 13 Kalendermonate behalten; Löschung der Telemetrie folgt `Retention:Enforce`, der reine Rollup nicht. Die Migration legt `IX_SystemLoadSamples_Source_SampledAtUtc` und `IX_SystemLoadSamples_Source_ProcessInstanceId_SampledAtUtc` an. Diese Details sind in `SystemLoadSample.cs`, `SystemLoadHourly.cs`, `20261011113207_AddSystemLoadSampling.cs`, `SystemLoadSampler.cs`, `SystemLoadRollupService.cs`, `SystemLoadRollupWorker.cs` und dem #380-DECISIONS-Eintrag auf `origin/feat/380-load-sampling` geprüft.

## 3. Lieferstufen und Seitenumfang

| Stufe | Inhalt | Daten-/Schemaabhängigkeit | Was sie beantworten kann |
|---|---|---|---|
| **A — Aktivität jetzt** | Aktivitätsverlauf aus heutigen DB-Daten; vorhandene Rate-Limit-Telemetrie lesbar zusammengefasst und als 24-h-Ereignisreihe. Vorhandene Health- und Roster-Bereiche bleiben eingebettet. | Keine neue Erfassung und standardmäßig keine Migration. Die UsageStat-Abfrage hat einen verpflichtenden EXPLAIN-Gate; falls nötig, wird der Index als ausdrücklich benannte Ausnahme vor dem Panel ergänzt. | Umfang und Verlauf gespeicherter Aktivität; welche lokalen Policies oder Provider tatsächlich gedrosselt haben. Keine Aussage zur aktuellen VPS-Auslastung. |
| **B — Last und Arbeit** | Host- und cgroup-Last, Prozessverbrauch sowie Worker-Arbeitsmengen auf gemeinsamer UTC-Zeitachse. | #380/#385 gemergt und Migration angewandt; Zeitreihe muss nach Deployment entstehen. | Host-/Container-Auslastung relativ zu deren endlichen Grenzen und zeitgleiche Emote-Purge-Arbeit. Keine Kanalattribution und kein allgemeiner VPS-Monitor. |

Beide Stufen liegen auf derselben Admin-Monitoring-Seite. A besteht aus zwei neuen Datenbereichen: **Aktivitätsverlauf** und **Rate-Limit-Übersicht**. B fügt einen Bereich **Last und Arbeit** hinzu. Health, Roster und die bestehenden Statusdetails werden nicht zu weiteren Dashboard-Kacheln umgebaut.

## 4. Lieferstufe A: Werte aus dem Checkout vor #385

### 4.1 Aktivitätsbereich

Der aktuelle Channel-Bestand bleibt ein schmaler Kontextwert; den bestehenden `/api/admin/roster`-Aufruf der Roster-Komponente wiederverwenden, nicht ein zweites Mal laden. Der konfigurierte Channel-Cap wird ausdrücklich als Admission-Regel für normale Nutzer beschriftet, nicht als Serverlimit oder „verfügbare VPS-Kanäle“.

| Betreiberfrage | Kennzahl und genaue Definition | Quelle | Granularität und Zeitraum | Diagrammform und Begründung |
|---|---|---|---|---|
| Wie entwickelt sich der in Emote-Purge beobachtete Live-Chat-Emote-Verbrauch? | **Gespeicherte Emote-Treffer pro UTC-Tag** = Summe `UseCount + BotUseCount + SharedChatUseCount`, nur `Source=Live`. Einheit: Emote-Treffer/Tag. Das ist nicht die Zahl der Chatzeilen und nicht zwingend die Zahl unterschiedlicher Chatnachrichten. | `UsageStats.Date`, `Source`, `UseCount`, `BotUseCount`, `SharedChatUseCount`; kein Join erforderlich. | Tageswerte, auswählbar 30, 90 oder 180 Tage; UTC-Tage. Die Grenze 180 Tage folgt dem Channel-Purge. | Linie mit markierten Tagespunkten und gemeinsamer Tagesachse mit „beobachtete Live-Channels“. Eine Linie zeigt Trend und Spitzen über die Zeit; keine zweite Achse und kein Donut. Lücken werden nicht interpoliert. Beschriftung nennt „gespeichert“, da fehlende Zeilen keine historische Health-Aufzeichnung sind. |
| An wie vielen unterschiedlichen Channels hat der Poller pro Tag einen Live-Stream erkannt? | **Beobachtete Live-Channels/Tag** = Anzahl `ChannelLiveDays`-Zeilen mit `LiveMinutes > 0` pro `Date`. Durch den Unique-Index auf `(ChannelId, Date)` entspricht jede Zeile höchstens einem Channel an einem Tag. Einheit: Channels/Tag. | `ChannelLiveDays.Date`, `LiveMinutes`; kein Join erforderlich. | Tageswerte, derselbe auswählbare Bereich 30/90/180 Tage, UTC. | Eigenes Small Multiple auf derselben x-Achse. Separate y-Achse, weil Channelzahl und Emote-Treffer verschiedene Einheiten haben. Es ist ein Poller-Signal, kein Echtzeit-Live-Count und keine vollständige Zuschauer- oder Streamdauer. |
| Wie viele Channels werden aktuell von Emote Purge verfolgt, und wie steht das zum Admission-Cap? | `trackedChannelCount` aus dem bestehenden Roster sowie der konfigurierte Wert aus `ceilings`. Einheit: Channels; Kontext als „Ist / konfigurierte Admission-Grenze“, ohne Prozentfarbe oder Restplatzversprechen. | `GET /api/admin/roster`; dahinter aktive `Channel`-Namen plus Worker-Roster und Channel-Cap-Konfiguration. | Momentaufnahme mit `GeneratedAtUtc`/Snapshot-Alter; keine Historie. | Kompakte Zahl in der Abschnittsüberschrift statt Kachel. Sie verankert die Aktivitätskurve im aktuellen Betriebsumfang; Admin-Ausnahme und akzeptierter Overshoot werden im Hilfetext genannt. |

Das Zeitmenü gilt nur für diesen Bereich. Die Quelldaten sind Tagesdaten; ein 1-Stunden- oder 24-Stunden-Fenster würde falsche Scheingenauigkeit erzeugen. A zeigt keine „neuen Nutzer pro Monat“-Kurve: `User` hat keinen Erstellungszeitpunkt und `LastLogin` überschreibt frühere Logins.

### 4.2 Rate-Limit-Bereich: von Rohzählern zu lesbaren Ereignissen

Die vorhandene Rate-Limit-Sektion wird exception-first geordnet. Sie beantwortet „Welche Policy hat tatsächlich abgewiesen?“ und „Hat ein Provider mit HTTP 429 geantwortet?“. Sie behauptet **keine Bucket-Auslastung**.

| Betreiberfrage | Kennzahl und genaue Definition | Quelle | Granularität und Zeitraum | Diagrammform und Begründung |
|---|---|---|---|---|
| Wo wurden lokale Requests abgewiesen? | Je Policy: `rejected / (accepted + rejected)` als **Anteil abgewiesener Policy-Entscheidungen** in Prozent, dazu die absoluten angenommenen/abgewiesenen Zähler. Das ist eine aggregierte Policy-Quote über alle Partitionen, **kein** Anteil des verbrauchten Per-User-/Per-IP-Buckets. Bei null Entscheidungen: „keine Requests“, keine Division. | Bestehendes `GET /api/admin/rate-limits`: `acceptedLastMinute`, `rejectedLastMinute`, `acceptedLast24Hours`, `rejectedLast24Hours`, Policy-Kapazität und Partitionstyp. Redis-Keyraum `ratelimit:telemetry`. | Snapshot mit rollendem Fenster 55–60 Sekunden und 24 Stunden. | Oben nur Policies mit mindestens einer 24-h-Ablehnung, absteigend nach Ablehnungen; aktuelle-Minuten-Ablehnungen zuerst als akuter Zusatz. Anteil plus Zähler erklärt das Ereignis besser als gleich laute Rohzahlen aller stillen Policies. Policies ohne Ablehnung bleiben in einem Dialog mit der vollständigen Konfiguration. |
| Wann gab es Häufungen lokaler Ablehnungen oder echte Provider-429er? | Anzahl lokaler Policy-Ablehnungen je Minute, aggregiert; separat reale HTTP-429er je Provider und Call-Source. Einheit: Ablehnungen/Minute bzw. Provider-429er/Minute. Kein Anteil bei Providern: insbesondere die 7TV-Header sind nur eine Stichprobe und kein gemeinsamer Budgetzustand. | Bereits gespeicherte Redis-Minuten-Buckets des `RateLimitTelemetryStore`; lokale `policy|…|rejected`- und Provider-`rate-limited`-Felder. Redis behält Minuten-Buckets 25 Stunden. | 1-Minuten-Buckets für die letzten 24 Stunden. In A immer sichtbar; in B teilt die Reihe die Last-Zeitachse nur bei einer Load-Range bis 24 Stunden. | Ein eigenes Linien-Small-Multiple auf ihrer festen 24-h-UTC-Achse; in B bei höchstens 24 Stunden gemeinsam mit Last und Arbeit. Absolute Zähler lassen Spitzen und Zeitpunkte erkennen; keine Dualachse und keine Prozentfläche. Provider bleiben als getrennte Reihen, weil Quellen und Call-Sources verschieden sind. |
| Welche effektive Regel und welcher letzte konkrete Fall steckt hinter der Zusammenfassung? | Policy-Typ, Capacity, Nachfüllrate oder Fenster, Partitionstyp, Queue-Limit; letzte lokale Ablehnung mit Zeitpunkt, Route, Methode, Policy und Retry-After; Provider-Zähler plus letzte rohe `Ratelimit-*`-Headerstichprobe. | Bestehendes `GET /api/admin/rate-limits`; bestehende Redis-Telemetrie. | Snapshot und 24-h-Zähler; Detail gilt nur so lange wie die Redis-TTL. | Ein Details-Dialog enthält die Rohwerte. Die Hauptansicht zeigt die Information nur bei Bedarf; die letzte Ablehnung ist eine einzelne überschreibbare Beobachtung und wird nicht als Ereignisarchiv ausgegeben. |

Die Werte `accepted/rejected` werden derzeit nach Policy zusammengefasst. Die Policy partitioniert aber je nach Route nach Twitch-User, Remote-IP oder User plus Vote-Session. Es gibt keine historische Zählung pro Partition und keinen gemessenen Füllstand des tatsächlichen Token-/Fixed-Window-Buckets. Deshalb zeigt A **keinen** `used / limit`-Meter, keinen Peak-Füllstand und keine willkürliche 80-%-Schwelle. Ein lokaler Reject belegt, dass ein Limit getroffen wurde; null Rejects belegen nicht, dass niemand nahe am Limit war. Die 24-h-Reihen lassen sich aus bestehenden Redis-Minuten-Buckets lesen; dafür ist keine neue Datensammlung oder Migration erforderlich.

## 5. Lieferstufe B: Lastdaten aus #380/#385

### 5.1 Normalisierung und Grenzen

Die #380-Tabellen speichern Rohwerte beziehungsweise zeitlich aggregierte Rohmetriken. Die Anzeige darf sie nicht als fertige Prozentwerte oder als „Prozess-RAM“ umbenennen.

| Rohwert | Anzeigewert und Regel | Verhalten bei Grenze, Reset oder Lücke |
|---|---|---|
| `ProcessCpuTimeTicks` | Pro gültigem Paar `ΔTicks / 10.000.000 / verstrichene Sekunden` = Prozess-CPU in **Kernen** (CPU-Sekunden pro Wandsekunde). `Api` und `Worker` getrennt; diese Kurve beantwortet, welcher Emote-Purge-Prozess CPU-Zeit verbraucht. | Lebenszeitzähler nie direkt plotten. Paar nur bei gleichem `Source` und `ProcessInstanceId`, monotonem Zähler und positivem Zeitabstand. Nach Neustart erster Punkt ohne Rate; keine Null erfinden. |
| `CgroupCpuUsageMicroseconds` | `Δusage_usec / 1.000.000 / verstrichene Sekunden` = cgroup-verbrauchte **Kerne**. Endliche CPU-Quota in Kernen = `quota_usec / period_usec`; Anteil = `100 × verbrauchte Kerne / Quota-Kerne`. Standardvergleich ist die **cgroup-Quota**, nicht die Host-Kernzahl. | `cpu.max = max`: kein Prozentanteil; absolute Kerne zeigen und „keine endliche Quota“ beschriften. Fehlen Counter/Period, Wert unbekannt. Zählerabfall, Neustart oder zu lange Lücke verwerfen das Paar. Kein Wechsel auf Host-Kerne als stiller Ersatz. |
| `CgroupMemoryCurrentBytes`, `CgroupMemoryMaxBytes` | Verbrauch in Byte; bei endlichem `memory.max > 0` zusätzlich `100 × current / max` Prozent des cgroup-Limits. | Bei `memory.max=max` bleibt nur der absolute Wert, nie ein Anteil am Host-RAM. `current` null heißt nicht verfügbar. Der Rohvertrag speichert für fehlendes/unlesbares `memory.max` und den Textwert `max` beide Male null; UI nennt deshalb „keine endliche Grenze gemeldet“ statt unbelegt „unbegrenzt“. Keine der beiden Lagen bedeutet Nullverbrauch. |
| `HostCpuBusyJiffies`, `HostCpuTotalJiffies` | `100 × Δbusy / Δtotal` = Host-CPU-Beschäftigung in Prozent des ganzen VPS. Der Parser zählt user, nice, system, irq, softirq und steal als busy; idle und iowait stehen im Gesamtzähler, iowait nicht im busy-Zähler. | Host-Zähler sind in Api- und Worker-Zeilen wiederholt. Host-Kurve genau einmal führen: Worker als Quelle, nur wenn im gesamten angefragten Bereich keine Worker-Hostreihe vorhanden ist Api als Ersatz. Nie Api- und Worker-Host-Prozent summieren. Bei Counterabfall Host-Paar verwerfen. |
| `HostMemoryTotalBytes`, `HostMemoryAvailableBytes` | `used = total - available`; Anzeige in Byte/GiB und `100 × used / total` als Host-RAM-Anteil. `MemAvailable`, nicht `MemFree`, ist der Rohwert. | Hostwert genau einmal aus Worker-Quelle, mit derselben Bereichs-Fallback-Regel. Null oder total ≤ 0 ergibt unbekannt. Nicht mit Api-/Worker-cgroup-Speicher addieren. |
| `HostLoadAverage1` | Rohwert als 1-Minuten-Mittel der runnable/uninterruptible Tasks, Einheit **Tasks im Mittel**, nicht Prozent. Er ergänzt CPU-Busy um Warteschlangendruck. Der Code speichert für Hourly nur die 1-Minuten-Reihe. | Keine Normierung durch Host-Kerne: deren Zahl ist in #380 nicht persistiert. Keine farbliche Grenzlinie oder „VPS-Auslastung in Prozent“. 5-/15-Minuten-Rohwerte bleiben Detailwerte ohne separate Hauptdiagramme. |
| `ActiveChannels`, `LiveChannels` | Worker-`ActiveChannels` als aktuell aktive, in der DB getrackte Channels; `LiveChannels` als Worker-Redis-Live-Snapshot. Einheit: Channels. Aktiv und live sind getrennte Reihen. | Worker ist kanonisch. `LiveChannels=null` vor erfolgreichem Poll nach Prozessstart ist unbekannt, nicht null Channels. Api-`LiveChannels` und Api-Workloadzähler sind immer nicht verfügbar. |
| `ChatLines`, `EmoteMatches` | Roh-Samples sind Intervall-Differenzen. Anzeige `count / tatsächliche Intervallsekunden × 60` in **Chatzeilen/Minute** bzw. **Emote-Treffern/Minute**. Für Hourly `Stundensumme / 60` als durchschnittliche Rate pro Minute der Stunde. | Nur Worker. Null bleibt unbekannt; echter Wert null ist keine Aktivität in einem bekannten Intervall. Counter-Baselines werden erst nach erfolgreichem Speichern bestätigt; keine Summen doppelt zählen und keine Lücken mit null auffüllen. |
| Host-Swap | Letzter bekannter Wert `SwapTotalBytes - SwapFreeBytes`, Einheit Byte, nur im Detail bei `SwapTotalBytes > 0`. | #380 rollt Swap nicht in `SystemLoadHourly` hoch. Deshalb kein Langzeitdiagramm und keine über sieben Tage behauptete Historie. |

Rollup-Paare folgen exakt der #380-Regel: gleiche Quelle und Prozessinstanz, nur monotone Zähler, positive Zeit, Abstände höchstens 2,5 Sample-Intervalle (bei 30 Sekunden also höchstens 75 Sekunden). Ein verworfener Paarwert erzeugt eine Lücke. Charts verbinden Lücken nicht und interpolieren nicht. `ProcessInstanceId`-Wechsel verhindert CPU-Differenzen über Neustarts. Host-Counterabfälle nach Hostneustart werden ebenso verworfen.

Die Hourly-Tabelle speichert für jedes Limit nur den letzten in der Stunde gelesenen Wert. Daher beschreibt eine stündliche Quota-/Speicherquote den letzten Stunden-Grenzwert; sie ist keine exakte Aussage über eine innerhalb derselben Stunde geänderte Grenze. Für 30-Sekunden-Rohpunkte wird die jeweilige Sample-Grenze verwendet. Eine nicht endliche cgroup-Grenze wird nie durch Host-RAM oder Host-Kerne ersetzt.

### 5.2 Diagramme und Kennzahlen in B

Alle Last- und Arbeitsreihen verwenden denselben UTC-Zeitbereich. Verschiedene Einheiten bekommen getrennte Small Multiples, nicht zwei y-Achsen. Die Historien-Auswahl in B lautet 1 Stunde, 24 Stunden, 7 Tage, 30 Tage, 90 Tage oder 13 Monate. Default ist 24 Stunden. Für 1 Stunde/24 Stunden werden gültige 30-Sekunden-Rohintervalle gezeigt. Im 7-Tage-Bereich fasst der Service die Rohwerte in festen 5-Minuten-UTC-Fenstern zusammen: CPU-Raten, Host-Load und Speicher zeigen den höchsten gültigen 30-Sekunden-Wert im Fenster; Chatzeilen- und Emote-Matching-Raten sind die Summe der gespeicherten Intervallzähler geteilt durch die Summe ihrer tatsächlichen Intervallsekunden und multipliziert mit 60. Coverage zeigt die Zahl gespeicherter Worker-Samples relativ zu den zehn erwarteten Samples; Lücken werden nicht hochgerechnet oder als Null ergänzt. Längere Zeiträume nutzen `SystemLoadHourly` mit P95 als Hauptlinie und Max als feine obere Markierung. Für Arbeitsmengen zeigt der Hourly-Rollup die in der jeweiligen UTC-Stunde gespeicherte Summe in Chatzeilen/Stunde bzw. Emote-Treffern/Stunde; sie wird nicht durch 60 geteilt und ist bei Teilabdeckung kein hochgerechneter Stundenwert. `SampleCount`/120 ist dort die Aufzeichnungsdichte der Source pro Stunde, keine Metrik-spezifische Zahl gültiger CPU-Paare. Mittelwerte bleiben im Tooltip/Detailsbereich. Coverage wird als Aufzeichnungsdichte benannt, nicht als Auslastung.

| Betreiberfrage | Kennzahl und genaue Definition | Quelle | Granularität und Zeitraum | Diagrammform und Begründung |
|---|---|---|---|---|
| Wie stark ist der gesamte VPS-CPU-Zähler beschäftigt? | Host-CPU-Busy-Prozent nach obiger Deltaformel. | `SystemLoadSamples.HostCpuBusyJiffies/HostCpuTotalJiffies`; danach `SystemLoadHourly.HostCpuBusyMean/Max/P95`. Eine Hostreihe, Worker bevorzugt. | 30 Sekunden bis 7 Tage aus Rohdaten; 1 Stunde pro Punkt bis 13 Monate. | Eigenes Small Multiple, P95-Linie und Max-Markierung; zeigt Host-Sättigung, ohne Prozess- oder Quota-Einheiten zu vermischen. |
| Wie viel Host-RAM ist belegt? | `MemTotal - MemAvailable`, absolut und relativ zu `MemTotal`. | `SystemLoadSamples.HostMemoryTotalBytes/HostMemoryAvailableBytes`; Hourly `HostMemoryUsed*Bytes` und `LastHostMemoryTotalBytes`. | Wie Host-CPU. | Eigenes Small Multiple mit gleicher Zeitachse. RAM-Anteil ist ein eigener Grenzwert neben CPU, keine zweite y-Achse. |
| Wartet Arbeit auf dem Host, obwohl die CPU-Busy-Kurve nicht steigt? | 1-Minuten-Host-Load-Average, Tasks im Mittel. | `HostLoadAverage1`; Hourly `HostLoadAverage1Mean/Max/P95`; eine Hostquelle. | Roh-Samples oder Hourly bis 13 Monate. | Eigenes neutrales Small Multiple. Der Wert ergänzt CPU um runnable/uninterruptible Taskdruck; keine Division durch eine nicht gespeicherte Kernzahl und keine Farbe. |
| Welcher Emote-Purge-Prozess nutzt CPU, und wie nahe ist sein Container an einer CPU-Quota? | Je Quelle getrennt: Prozesskerne aus `ProcessCpuTimeTicks`; zusätzlich cgroup-Kerne und, wenn endlich, Anteil an `cpu.max`. Die cgroup-Kurve ist Containerverbrauch, die Prozesskurve einzelner Api-/Worker-Prozess. | `SystemLoadSamples` CPU-Rohfelder und Quota je `Source`; `SystemLoadHourly.ProcessCpuRate*`, `CgroupCpuRate*`, `LastCpuQuotaMicroseconds`, `LastCpuPeriodMicroseconds`. | Derselbe Zeitbereich. Hourly P95 mit Max-Markierung. | Zwei gleichskalierte Einheiten dürfen nicht in eine Doppelachse: Prozesskerne und cgroup-Anteil sind getrennte Small Multiples, Api/Worker jeweils getrennt. Die Trennung hält Prozessverursacher und Containergrenze sichtbar. Falls `cpu.max=max`, das betroffene Source-Panel in Kernen statt Prozent beschriften. |
| Wie nahe sind Api- und Worker-Container an ihrem Speicherlimit? | Pro Quelle `memory.current` in Byte und, nur für ein endliches Limit, Anteil `current / memory.max`. | `SystemLoadSamples.CgroupMemoryCurrentBytes/CgroupMemoryMaxBytes`; Hourly `CgroupMemoryCurrent*Bytes/LastCgroupMemoryMaxBytes`. | Derselbe Zeitbereich, Hourly P95/Max für Langzeit. | Eigenes Small Multiple je Quelle, Prozent nur bei endlichem Limit; unbegrenzte Quellen erhalten eine Byte-Skala. Container-Memory ist kein Prozess-RSS und wird nicht zum Host-RAM addiert. |
| Steigt die Last, weil mehr Channels arbeiten oder mehr live sind? | Worker-Active-Channels und Worker-Live-Channels, je Sample bzw. als Hourly-Mean/Max. | `SystemLoadSamples.ActiveChannels/LiveChannels`; Hourly gleichnamige Mean-/Max-Felder. | 30 Sekunden bis 7 Tage; danach Hourly, maximal 13 Monate. | Gemeinsames Small Multiple mit zwei Linien derselben Einheit Channels. So lässt sich „getrackt“ von „live beobachtet“ lesen, ohne die Reihen zu addieren. |
| Wie viel Chatarbeit läuft pro Minute? | Rohdaten: gespeicherte `ChatLines` geteilt durch tatsächliche Intervallsekunden × 60, in Chatzeilen/Minute. Hourly: gespeicherte Summe je UTC-Stunde, in Chatzeilen/Stunde; keine Hochrechnung bei unvollständiger Abdeckung. | Worker-`SystemLoadSamples.ChatLines`; Hourly `SystemLoadHourly.ChatLines` und `SampleCount`. | 30-Sekunden-Intervalle bis 24 Stunden, 5-Minuten-Fenster bei 7 Tagen, UTC-Stundensummen bei 30 Tagen bis 13 Monaten. | Eigenes Small Multiple. Achsenbeschriftung folgt der Granularität (Chatzeilen/Minute bzw. Chatzeilen/Stunde); Hourly-Tooltips nennen SampleCount/120 als Aufzeichnungsdichte. So erscheinen unvollständige Stunden nicht als voll beobachtete Rate. |
| Wie viel Matching-Arbeit läuft pro Minute? | Rohdaten: gespeicherte `EmoteMatches` geteilt durch tatsächliche Intervallsekunden × 60, in Emote-Treffern/Minute. Hourly: gespeicherte Summe je UTC-Stunde, in Emote-Treffern/Stunde; keine Hochrechnung bei unvollständiger Abdeckung. | Worker-`SystemLoadSamples.EmoteMatches`; Hourly `SystemLoadHourly.EmoteMatches` und `SampleCount`. | 30-Sekunden-Intervalle bis 24 Stunden, 5-Minuten-Fenster bei 7 Tagen, UTC-Stundensummen bei 30 Tagen bis 13 Monaten. | Eigenes Small Multiple neben Chatzeilen. Achsenbeschriftung folgt der Granularität; Aufzeichnungsdichte ist sichtbar. Getrennte Einheit verhindert, dass Chatzeilen als Emote-Treffer gelesen werden. |

Der Prozess-CPU-Verbrauch in Kernen kann als zwei Linien in **einem** Diagramm liegen, weil Api und Worker dieselbe Einheit haben; die Quota-Auslastung bleibt ein eigenes Diagramm. Werte von Api und Worker werden nicht als ein Hauptwert summiert: sie zeigen gerade die Trennung, die beim Finden des Lasttreibers hilft. Hostwerte sind dagegen duplizierte Beobachtungen und werden genau einmal gewählt.

### 5.3 Langzeitgrenzen

Nach sieben Tagen werden Roh-Samples durch Hourly-Reihen ersetzt. Hourly bewahrt CPU-Rate-Mean/Max/P95, Speicher-Mean/Max/P95 und Workload-Stundensummen, aber keine vollständigen per-Sample-Limitwechsel, keine Swap-Historie und keine paarweise „CPU-Sekunden pro 1.000 Chatzeilen“-Statistik. Der Langzeitvergleich bleibt daher bei synchronen Last- und Arbeitsreihen. Es gibt keine automatische Forecast- oder Upgrade-Empfehlung aus einem Verhältnis, dessen Nenner oder Coverage nicht mehr exakt erhalten ist.

## 6. Endpunktverträge und Abfrageabsicht

Neue Routen liegen in der bestehenden `/api/admin`-Gruppe und erben denselben `GlobalAdminAuthorizationFilter`. Das Core-Modul erhält ein `IAdminDashboardQueryService`-Interface und BCL-only Response-/Punkt-Records; dessen Implementierung liegt in `Infrastructure/Services` und wird über den einzigen Infrastructure-DI-Punkt registriert. Die Redis-Zeitreihe erweitert `IRateLimitTelemetryReader`/`RateLimitTelemetryStore` als read-only Fähigkeit über bereits gespeicherte Buckets. Endpunkte validieren Parameter und delegieren an diese Core-Interfaces; sie injizieren weder `AppDbContext` noch `IConnectionMultiplexer`. Es gibt kein generisches Repository. SQL-Aggregate mit Navigationen reduzieren Channel-/Emote-Join-Pfade vor `GroupBy` zuerst auf skalare ID-Listen. Angular bleibt bei der bestehenden Standalone-Admin-Seite, Services in `core/admin`, Signals/Resources und Transloco (de/en); `core` importiert nichts aus `features` oder `shared`.

| Route | Parameter | Antwortform | Fehler und Berechtigung |
|---|---|---|---|
| `GET /api/admin/dashboard/activity` | `range`: `30d`, `90d` oder `180d`; Default `30d`. | Range, `generatedAtUtc`, `dataFromUtc`, `dataThroughUtc`, Tagespunkte mit `bucketStartUtc`, gespeicherten Emote-Treffern und beobachteten Live-Channels. Fehlende Reihen sind nicht interpoliert. Aktueller Channel-Bestand bleibt dem vorhandenen Roster-Aufruf überlassen. | Unsupported range: `400 invalid_dashboard_range` (neuer sprachneutraler `ApiErrorCodes`-Wert plus Einträge in `api-error.ts` und de/en). `401`/`403` entsprechend bestehendem Admin-Filter; unerwartete Fehler `500 unexpected_error`. |
| `GET /api/admin/rate-limits/trend` | Keine; stets die letzten 24 Stunden. | `telemetryAvailable`, UTC-Minuten-Buckets mit lokalen Ablehnungen sowie Upstream-429er nach Provider und Call-Source. Bei Redis-Ausfall `200` mit `telemetryAvailable=false` und leerer Reihe, analog zu `/api/admin/rate-limits`. | `401`/`403` per bestehendem Filter; kein neuer Fehlercode für Redis-Ausfall. Keine neue Rate-Limit-Policy für diese Admin-Leseroute. |
| `GET /api/admin/dashboard/load` | `range`: `1h`, `24h`, `7d`, `30d`, `90d`, `13mo`; Default `24h`. | Gewählter Zeitraum, UTC-Punkte mit benanntem Metric-Key, Source, normalisiertem Wert/Einheit, `sampleCount`/Coverage und `dataFromUtc`/`dataThroughUtc`. Null bedeutet unbekannt/keine endliche Quote, nicht null Verbrauch. | Vor #385 keine Route. Nach #385: unsupported range `400 invalid_dashboard_range`; leere oder noch nicht gewachsene Historie `200` mit `available=false`/leeren Punkten; `401`/`403` via Filter; DB-Fehler `500 unexpected_error`. |
| `GET /api/admin/dashboard/load/latest` | Keine; begrenztes Zeitfenster der jüngsten zehn Minuten, für beide Sources. | Normalisierte jüngste Rohpunkte samt Sample-Zeit und Source, begrenzt auf wenige Zeilen; keine Roh-GUID und keine sensitiven Daten. | Keine neuen Fehlercodes; ohne Samples `200` mit `available=false`. `401`/`403` wie oben. |

### 6.1 Abfragen, Indexe, Menge und Messauftrag

Die Mengen unten sind **Benchmark-Annahmen**, keine gemessenen Prod-Zahlen. Als Implementierungsgate ist jede SQL-Leseabfrage auf einer realistischen Datenkopie mit `EXPLAIN ANALYZE` und Buffer-Zählern zu prüfen. Zielwert für einen uncached Dashboard-Aggregat-Request: <250 ms; wird der Wert verfehlt, Abfrage/Index oder Umfang ändern, nicht den Refresh-Takt erhöhen.

| Abfrage | Filter/Gruppierung und vorhandener Index | Mengenannahme und Prüfauftrag |
|---|---|---|
| A: bestehender Roster-Wert „aktive Channels“ | `/api/admin/roster` ruft bereits `ListActiveChannelNamesAsync` auf und liefert Anzahl plus konfigurierte Ceilings; A nutzt dieselbe Antwort ohne neuen Channel-Count-Query. Der Snapshot hat keinen `IsBotActive`-Index. | Für den bestehenden DB-Read annehmen: 80 aktive Channels plus 120 inaktiven, noch nicht gepurgten Channels = **200 Channel-Zeilen**. Beim Roster-Query `EXPLAIN ANALYZE` mit 200 Zeilen sowie einem Stresstest mit 10.000 Channel-Zeilen ausführen; Plan, Buffers, Ergebniszeilen und Laufzeit notieren. Ein voller Scan bei 200 Zeilen dürfte klein sein, wird aber nicht als gemessen behauptet. |
| A: gespeicherte Emote-Treffer nach UTC-Tag | `UsageStats` auf `[from,to)` nach `Date`, `Source=Live` filtern; pro Datum die drei gespeicherten Zähler summieren. Kein Join, kein Navigation-`GroupBy`. Vorhanden ist `(EmoteId, EmoteSetId, Date) INCLUDE (UseCount)`; der globale Datumsfilter kann den Indexpräfix nicht nutzen und die benötigten Zusatzfelder sind nicht gedeckt. | Annahme: 80 aktive Channels × bis zu 1.000 gezählte Emotes × 180 Tage, grob **14,4 Mio. Zeilen** bei einer Zeile je Emote/Tag; Emote-Set-Wechsel oder weitere Daten erhöhen das. 30-Tage-Fenster davon grob 2,4 Mio. Zeilen. `EXPLAIN ANALYZE` für 30- und 180-Tage-Aggregat auf dieser Menge: tatsächlicher Plan, Zeilen, Buffers, Laufzeit und Index-Only-/Heap-Reads festhalten. Bei >250 ms wäre ein gezielter Date-leading Index nötig, z. B. ein partiell abdeckender Live-Source-Index auf `Date` mit den Summenzählern. Das ist eine **Migration**, also ausdrücklich außerhalb des strikten migrationsfreien A-Anteils; bis zur Entscheidung darf das Panel nicht stillschweigend mit einer teuren Volltabellensuche ausgeliefert werden. |
| A: beobachtete Live-Channels je Tag | `ChannelLiveDays` auf Datum filtern, nach `Date` gruppieren, Zeilen mit `LiveMinutes > 0` zählen. Vorhandener Index `(ChannelId, Date) INCLUDE (LiveMinutes)` beginnt nicht mit Datum; Tabellen-Scan ist zu erwarten, aber eine kleinere Tabelle. | Annahme für Test: 200 aktuell/innerhalb Retention relevante Channels × 180 Tage = **36.000 Zeilen**. `EXPLAIN ANALYZE` mit 30- und 180-Tage-Range: tatsächliche Zeilen, Buffers und Laufzeit protokollieren. Eine neue Date-Index-Migration nur nach Messung; sonst bleibt A migrationsfrei. |
| A: Rate-Limit-Verlauf | Keine SQL-Abfrage. `RateLimitTelemetryStore` liest Minuten-Hash-Buckets des bestehenden Redis-Keyraums; die vorhandene 24-h-Aggregation liest bereits bis zu 1.440 winzige Hashes gepipelined. | Erwartet höchstens **1.440 Minuten-Buckets** und eine begrenzte, konfigurationsbestimmte Zahl Policy-/Provider-Felder; Redis-TTL 25 Stunden. Last und Antwortgröße im Infrastructure-Integrationstest messen. Kein SQL-EXPLAIN. |
| B: 30-Sekunden-Rohhistorie | `SystemLoadSamples` nach Source und Samplezeit filtern/ordnen; für das erste gültige Rate-Paar je Prozessinstanz den benachbarten Vorgänger ergänzen. Die #380-Indizes `(Source, SampledAtUtc)` und `(Source, ProcessInstanceId, SampledAtUtc)` passen zu Zeitfilter und Paarbildung. | 2.880 Zeilen/Source/Tag; **20.160/Source für sieben Tage, 40.320 für Api+Worker**, zuzüglich weniger Vorgänger. `EXPLAIN ANALYZE` für 24 Stunden, sieben Tage und Zehn-Minuten-Tail; Indexplan, Buffers, tatsächliche Zeilen und Laufzeit protokollieren. |
| B: Hourly-Historie | `SystemLoadHourly` auf `Source` und `HourUtc` filtern und zeitlich ordnen; Gruppierung ist bereits vom Rollup vorgenommen. Primärschlüssel `(Source, HourUtc)`. | Maximal 24 × 396 ≈ **9.504 Zeilen je Source**, rund **19.008 für beide Sources**. `EXPLAIN ANALYZE` für 30 Tage und 13 Monate; Indexplan, Buffers und Laufzeit dokumentieren. |

Keine Abfrage in dieser Spec liest `AuditLogEntries`, `Votes`, `VoteSessions`, `Users` oder `ChatLogBackfillRuns`. Damit kommen diese wachsenden bzw. retention-gebundenen Tabellen nicht in den Refresh-Pfad. Falls eine spätere Änderung doch eine Audit-Aggregation einführt, ist vor Lieferung ein eigener `EXPLAIN ANALYZE` auf mindestens **1 Mio. Audit-Zeilen** Pflicht; der vorhandene `OccurredAtUtc`-Index ist keine Zusage, dass ein gruppierter Aktionsverlauf billig ist.

## 7. Abfragefrequenz und Caching

- Aktivitätsbereich: einmal beim Eintritt und beim Wechsel des 30/90/180-Tage-Menüs; API-seitiger Memory-Cache 60 Sekunden je normalisiertem Bereich. Die Datenquelle hat Tagesgranularität; ein 30-Sekunden-Poll wäre sinnlos.
- Rate-Limit-Snapshot und 24-h-Trend: beim Eintritt und höchstens jede 60 Sekunden; Memory-Cache 30 Sekunden je Api-Instanz. Manueller Refresh nutzt dieselben Ressourcen. Das ersetzt den heutigen 30-Sekunden-Poll der Policy-Karte. `RateLimitTelemetryStore` behält seine aktuelle gepipelinete Redis-Leseform und wird nicht pro Chartpunkt separat aufgerufen.
- Load-Historie: einmal beim Eintritt und beim Bereichswechsel; Memory-Cache mindestens 30 Sekunden für den kurzen Bereich und 60 Sekunden für 7 Tage oder länger. Die Antwort enthält den Datenstand, sodass ein Cache nicht als Echtzeitmessung missverstanden wird.
- Load-Tail: nur solange die Seite sichtbar ist, alle 30 Sekunden `GET /api/admin/dashboard/load/latest`; zehn Minuten Lookback erlauben Wiederholung und Lückenreparatur nach einem langsamen Request. Memory-Cache 5–10 Sekunden, damit mehrere Admin-Tabs keine DB-Doppelabfrage erzeugen. Auf `visibilitychange` pausieren und beim Zurückkehren einmal sofort nachladen.
- Historische Range-Reads und Tail-Reads bleiben getrennt: kein 20.000-Zeilen-Reload im 30-Sekunden-Takt. Im Frontend werden Tail-Punkte nach `(Source, SampledAtUtc)` dedupliziert und der alte Fensterüberhang entfernt.

Die Caches sind pro Api-Instanz; eine globale Cache-Topologie wird nicht eingeführt. Durch kurze feste Bereiche, kleine Tail-Reads und vorhandene Zeitindexe bleibt der Dashboard-Refresh klein. `/api/admin` hat aktuell keine Rate-Limit-Policy; neue Routen übernehmen das und begrenzen stattdessen Range und Antwortgröße.

## 8. SSE: bestehende Verbindung wiederverwenden, keine Messwerte pushen

- Die bestehende Admin-Verbindung zu `/api/admin/live` bleibt die einzige SSE-Verbindung dieser Seite. `worker.health` aktualisiert weiter Health und Worker-Zustand; kein zweites `EventSource` und kein Event pro CPU-Sample.
- `usage.flushed` kann den Aktivitätsbereich invalidieren, wenn der gewählte Zeitraum den heutigen Tag enthält. Der Client lädt danach die normale REST-Ressource einmal nach; keine Aggregation wird als SSE-Payload gesendet.
- Load-Tail wird im 30-Sekunden-Takt per REST gelesen. Der vorhandene Stream signalisiert keinen erfolgreichen `SystemLoadSample`-Write und `worker.health` ist keine Sample-Bestätigung. Ein falscher Refresh-Auslöser könnte dieselben alten Daten wieder laden.
- Die Rate-Limit-Übersicht nutzt ihren 60-Sekunden-Refresh; ein neues Event bei jedem Rate-Limit-Entscheid würde bei Request-Last selbst eine Nachrichtenlast erzeugen. Keine Live-Pushs für historische Punkte oder Rohwerte.
- Ein neues SSE-Event wie `system.load.sampled` wäre ein Vertrag-/Topologie-Change und bräuchte einen DECISIONS-Eintrag. Es ist in dieser Spec nicht vorgesehen.

## 9. UI-Platzierung, Hierarchie und Zustände

Die Seite bleibt unter dem bestehenden Admin-Tab „Monitoring“. Der erste neue Bereich ist die Last-/Arbeitsübersicht (sobald B Daten liefert), danach der Tages-Aktivitätsverlauf und dann die Rate-Limit-Ausnahmen. Worker-Health und Roster bleiben als eigene Betriebsbereiche darunter. In A steht der Aktivitätsverlauf oben, bis Lastdaten erstmals verfügbar werden. Die Rate-Limit-24-h-Reihe steht in A immer in ihrer eigenen festen Zeitachse; nach B teilt sie die Last-Zeitachse nur bei einer Load-Range bis 24 Stunden. Bei längeren Load-Ranges bleibt die 24-h-Zusammenfassung sichtbar, die Rate-Limit-Zeitreihe wird ausgeblendet. Keine zweite Seite, keine neue Shell-Breite.

- **Zurückhaltende Flächen:** Abschnittsstapel und dünne Trennlinien statt gleichartiger KPI-Karten, gemäß `docs/UI-Designsprache.md` §2.1. Der aktuelle Channel-Bestand ist ein kompakter Kontextwert, kein dekorativer Tile-Grid.
- **Typografie und Farbe:** Ebenen aus §3, semantische Farb-Token aus §2.0. Kapazitätskurven bleiben grundsätzlich neutral; Farbe markiert nur tatsächliche Health-/Datenfehler oder echte Limit-Rejections. Keine 80-%-Warnfarbe ohne gemessene Grundlage. Rohanteil plus Einheiten bleiben lesbar ohne Farbe.
- **Small Multiples:** gleiche UTC-x-Achse, je Kennzahl eigene y-Achse und Einheit. Bei CPU und Speicher stehen Quelle und Unit im Abschnittstitel. Keine Doppelachsen, keine Donuts. Mehr Breite entsteht innerhalb der konstanten `max-w-7xl`-Spalte aus §8.4a; die App-Shell-Breite und Sticky-Offsets aus §8.5 wechseln nicht pro Route.
- **Controls:** zwei kompakte Bereichsmenüs direkt an den passenden Abschnitten: 30/90/180 Tage für vorhandene Aktivität und 1h–13 Monate für #380-Last. Sie sind sichtbar, weil die Range-Wahl Kernfunktion der Trendansicht ist. Keine Filterleiste und keine dauerhaft sichtbare Reihe roher Policy-Zahlen; Rate-Limit-Details liegen im Dialog. In A hat die Rate-Limit-Reihe fest 24h; in B wird sie nur bei Load-Bereichen bis 24h an die Load-Achse angelegt.
- **Initiales Laden:** auf der ersten Anfrage Abschnitt-Skeletons, deren Konturen dem Inhalt entsprechen (§6.1). Bei Folge-Refreshes bleiben die letzten gültigen Werte an Ort und Stelle, damit die Seite nicht springt; die bestehende Health-Seite verwendet dieses Muster bereits.
- **Leere oder zu kurze Historie:** `<app-empty-state>` nach abgeschlossenem Laden (§6.2), mit Erklärung „Messung beginnt nach Rollout von #385“ bzw. „Für diesen Zeitraum liegen noch keine Samples vor“. Bis zwei gültige Zählerpunkte vorliegen, gibt es keine CPU-Rate. Keine Nullreihen vortäuschen.
- **Teilweise Messbarkeit:** pro Small Multiple Fehler/Unbekanntes getrennt anzeigen. Nicht verfügbares cgroup-Feld bleibt „nicht verfügbar“; endliche Quota fehlt? Absolute Kerne/Bytes bleiben sichtbar. `LiveChannels=null` bleibt unbekannt. Hourly `SampleCount` und Datenbereich stehen im Detail.
- **Fehler:** `NoticeBanner` nur im betroffenen unabhängigen Abschnitt; Redis-Ausfall lässt bestehende Policy-Konfiguration sichtbar und kennzeichnet Telemetrie als nicht verfügbar. DB-Ausfall der Aktivitätsreihe darf Worker-Health nicht ausblenden.
- **Barrierefreiheit und Übersetzung:** sichtbare Einheiten, Textalternative/Zusammenfassung pro Chart, tastaturbedienbare Bereichswahl und deutsche/englische Transloco-Texte. §9 und §10 gelten; neue Fehlercodes bekommen beide Locale-Einträge. Neue Komponenten folgen dem Audit-Harness aus §12.

## 10. Tests und Liefergates

### Stufe A

- Neues Core-Query-Interface und seine DTOs enthalten nur Verträge. Aggregation und UTC-/Range-Verhalten werden im neuen Infrastructure-Service getestet.
- `UsageStat`- und `ChannelLiveDay`-Aggregate bekommen PostgreSQL-Testcontainer-Integrationstests in `tests/EmotePurge.Infrastructure.Tests`: UTC-Tagesgrenzen, Range-Grenzen, `Source=Live`-Filter, Bot-/Shared-Chat-Felder, leere Tage, Channel-Purge-Lücken und keine Navigation vor einem `GroupBy`.
- Eine neue Rate-Limit-Trendreihe wird im `RateLimitTelemetryStore` mit Redis-Integrationstests abgesichert: 1-Minuten-Zuordnung, Summe lokaler Ablehnungen vs. Provider-429er, 25-Stunden-TTL und `telemetryAvailable=false` bei Redis-Ausfall.
- API-Tests in `tests/EmotePurge.Api.Tests` prüfen Route, Antwortform, erlaubte Ranges und Admin-only `401`/`403`. Handler bleiben dünn und erhalten keine separaten Handler-Unit-Tests; es wird kein Filter eingeführt und keine Filterreihenfolge geändert.
- Frontend-Service-/Range-/Normalisierungs-Utilities erhalten `*.spec.ts` bei Core/Shared. Komponenten werden nur auf nicht-triviale Entscheidungen getestet (Exception-first-Sortierung, kein Prozentmeter, Redis unavailable); Template/CSS wird nicht per Unit-Test abgesichert. Der neue Admin-Flow erhält einen Playwright-E2E mit gemocktem `/api/**` für Bereichswechsel, leere Daten und Fehlerzustände.
- **A-Performance-Gate:** `EXPLAIN ANALYZE`-Aufträge aus §6.1 erfüllen das vereinbarte <250-ms-Ziel auf realistischer Datenmenge. Scheitert `UsageStats`, wird die Migration des Date-Index vor dem Chart ausdrücklich entschieden; kein ungemessener Vollscan in einem wiederholt abgefragten UI.

### Stufe B

- #380-Sampling-/Rollup-Verträge bleiben durch die bereits vorgesehenen `SystemLoadSampler`, `SystemLoadRollupService`- und Retention-Tests in Infrastructure/Worker gedeckt. Neue Read-Services und CPU-/Speicher-Normalisierung werden in `tests/EmotePurge.Infrastructure.Tests` ergänzt; echte EF-Queries laufen über Testcontainers.
- Testfälle decken: Api vs Worker, wiederholte Hostwerte nur einmal, finite/unbegrenzte Quota, endliches/unbegrenztes Memory-Limit, Null vs echter Nullverbrauch, `ProcessInstanceId`-Wechsel, Zählerreset, 75-Sekunden-Grenze, fehlende Paare, Rolling-Raw/Hourly-Grenze, Hourly-P95/Max und `SampleCount`-Coverage ab.
- API-Contracttests in `tests/EmotePurge.Api.Tests` prüfen Admin-Filter, Range-Codes, leere/vorhandene Reihe und `load/latest`; bei unverändertem Filter werden keine unnötigen Filterreihenfolge-Tests hinzugefügt.
- Frontend-Services/Utilities testen Quotaberechnung, Prozent nur bei endlicher Grenze, Fallback Hostquelle ohne Doppelsumme, Tail-Deduplizierung, Lücken und Quelltrennung. Playwright-E2E mit gemocktem `/api/**` deckt B-Range, Live-Tail-Refresh, leere/zu kurze Daten und unabhängige Fehlerbereiche ab.
- Kein Screenshot-/CSS-Unit-Test als Ersatz für den UI-Audit: den bestehenden UI-Audit-Harness mit kleinen/großen Viewports und beiden Themes ausführen; Axe muss WCAG-AA-Gates erfüllen. Transloco de/en vollständig prüfen.
- Für neuen Code gilt das Sonar-Gate **mindestens 80 % Coverage**. Backend-Tests, Frontend-Tests, Lint, E2E, Audit und die B-Performance-Aufträge sind Implementierungs-Gates; in diesem Spec-Auftrag selbst werden keine Tests ausgeführt.

## 11. DECISIONS-Einträge bei Umsetzung

Ein gemeinsamer Implementierungs-Commit braucht einen neuen Eintrag in `docs/DECISIONS.md`, weil er neue API-Verträge für Zeitreihen und eine UI-Refresh-/Cache-Regel festlegt. Der Eintrag benennt mindestens: A-/B-Routen und maximale Zeiträume, Normalisierung der #380-Rohwerte samt Null-/Reset-Semantik, Aufteilung Api/Worker/Host, Cache-/Pollfrequenzen und Rate-Limit-Lücken.

Zusätzliche DECISIONS-Einträge oder Abschnitte sind erforderlich, wenn:

- `UsageStats` vor A einen neuen globalen Date-Index erhält;
- Rate-Limit-Instrumentierung pro Partition/Füllstand oder eine neue Redis-Key-Struktur eingeführt wird;
- der `SystemLoadHourly`-Rollup um zusätzliche Statistik ergänzt wird (z. B. historisch exakte CPU-pro-Arbeit-Summen);
- ein neues SSE-Ereignis, eine andere Autorisierung/Filterreihenfolge oder eine neue Cache-Topologie gewählt wird.

Die unverrückbaren Ausschlüsse aus #380 bleiben unverändert: kein Docker-Socket, kein Prometheus/Grafana, kein externer Log-Stack und keine Postgres-/Redis-Containerlast in dieser Lieferung.

## 12. Bewusst nicht

| Nicht enthalten | Begründung |
|---|---|
| Aussage „VPS bei X %“ in A | Main-vor-#385 speichert keine Host- oder Prozesslast. Aktivitätsvolumen ist kein Lastmesswert. |
| Alle Chatzeilen/Tag in A | `UsageStats` speichert Emote-Treffer, nicht IRC-Zeilen. Die Chatzeilen beginnen erst mit dem Worker-Sample aus #380. |
| Top-Channels nach Chatvolumen im laufenden Intervall | Weder Bestands-`UsageStat` (nur Tages-/Emote-Aggregate) noch der beschlossene erste #380-Schnitt haben Chatzeilen je Channel. #380 entschied Per-Channel-Attribution ausdrücklich aus dem ersten Schnitt heraus. Ein Emote-Treffer-Ranking wäre kein ehrlicher Ersatz für Chatvolumen. |
| Historische Zahl aktiver/getrackter Channels, Registrierungen oder Logins | `Channel` ist ein aktueller Zustand; `User` speichert keinen CreatedAt-/Login-Verlauf. `ChannelLiveDay` ist ein anderer, beobachteter Tagesindikator und Channel-Historie kann nach 180 Tagen gepurgt werden. |
| Viewer-Zahlen | Der Helix-Poller empfängt sie, aber der aktuelle Datenbestand persistiert sie nicht; neue Erfassung wäre außerhalb von A und ist nicht Teil des #380-Entscheids. |
| Audit-Aktionen als Kapazitätsdiagramm oder Audit-Log-Auszug | Admin-Aktionen erklären Chat-/Workerlast nicht; Audit-Historie ist #383 bzw. bestehender Audit-Bereich. Ein wachsender Audit-Scan wäre unnötige DB-Arbeit und würde den Log-Viewer duplizieren. |
| Vote-, Backfill- und Emote-Gesamt-Kacheln | Sie beantworten die Upgrade-Frage nicht beim ersten Blick. Laufende Datenimporte und Produktaktivität sind keine im #380-Schnitt gemessenen Worker-Lasttreiber. |
| Rate-Limit-Bucket-Meter `used/limit`, Peak-Füllstand und pauschale 80-%-Farbe | `RateLimitTelemetryStore` aggregiert über alle Partitionen. Der aktuelle Füllstand pro User/IP/Session ist nicht gespeichert; aus globalen Requests kann er nicht abgeleitet werden. A zeigt echte Ablehnungen und ihren Anteil an allen beobachteten Entscheidungen. |
| 13-Monats-Quote CPU pro 1.000 Chatzeilen | `SystemLoadHourly` speichert keine paarweisen CPU-Sekunden-/Arbeits-Summen und Coverage kann unvollständig sein. Eine abgeleitete Kennzahl würde historische Abdeckung als Vollstunde unterstellen. |
| Prozess-RSS/GC-Speicher, Disk-I/O, Netzwerk, Postgres-/Redis-Containerlast | Der beschlossene #380-Rohvertrag enthält diese Werte nicht; cgroup memory ist Container-, nicht Prozess-RSS. Den Umfang nicht ausbauen, bevor eine konkrete Betreiberfrage ihn rechtfertigt. |
| Einzelne Channellisten, Such-/Filter-Controls und Log-Inhalte | #382 und #383 sind eigene Issues und bleiben auf ihren vorhandenen Admin-Seiten. |
| Docker-Socket, Prometheus/Grafana oder externer Log-Stack | Durch #380 explizit ausgeschlossen; das Monitoring bleibt in Emote Purge. |

## 13. Offene Entscheidungen

1. **Globaler `UsageStat`-Datumsindex für A**
   - Ohne neuen Index liefern, wenn `EXPLAIN ANALYZE` die 30-/180-Tage-Aggregation unter dem <250-ms-Ziel hält.
   - Vor dem Aktivitätsdiagramm einen partiellen, abdeckenden Date-Index für Live-Zeilen ergänzen.
   - Das Emote-Trefferdiagramm in A auslassen und zunächst nur ChannelLiveDay zeigen.
   - **Empfehlung:** zuerst mit der Annahme aus §6.1 messen. Erwartbar ist ein globaler Scan, daher bei Zielverfehlung die kleine, gezielte Indexmigration explizit freigeben und im DECISIONS-Eintrag nennen; den Rest von A nicht verzögern.

2. **Wie weit A die gespeicherte Aktivität zurückzeigt**
   - 90 Tage als konservativen Default-Maximalbereich nutzen.
   - Bis 180 Tage zeigen und die Channel-Purge-Grenze direkt beschriften.
   - 13 Monate anzeigen, aber mit vollständigem Hinweis auf gelöschte Channel-Historien.
   - **Empfehlung:** 180 Tage, weil es der festgelegten Deaktivierungs-Retention entspricht; 13 Monate wäre für diese Reihen nicht vollständig und könnte als Wachstumskurve täuschen.

3. **Spätere Rate-Limit-Nähe pro Partition**
   - Es bei aggregierten Ablehnungsquoten und echten Ablehnungsereignissen belassen.
   - Eine eigene Telemetrie für Per-Partition-Nähe/Peak-Füllstand mit begrenzter, pseudonymisierter Redis-Retention bauen.
   - Aktuelle globale Zähler als Auslastung labeln.
   - **Empfehlung:** Option 1 für #381. Option 2 nur als separates Vorhaben mit Entscheidung zu Partition-Privatsphäre, Token-Bucket-Semantik und TTL; Option 3 ist datenlogisch falsch.

4. **Historische CPU-pro-Arbeit-Kennzahl**
   - Im ersten B-Schnitt nur rohe Last- und Arbeitskurven auf gemeinsamer Achse zeigen.
   - Eine 7-Tage-Kennzahl direkt aus Rohsamples ableiten, Langzeitwert weglassen.
   - #380-Hourly um ausreichende Summen/Paare erweitern.
   - **Empfehlung:** Option 1. Sie beantwortet die Kapazitätsfrage ohne eine Coverage-abhängige Effizienzquote; eine spätere Langzeitkennzahl braucht zuerst einen eigenen Rollup-Vertrag und DECISIONS-Eintrag.

## 14. Annahmen und nicht verifizierte Punkte

- Es wurde keine Produktionsdatenbank gestartet oder abgefragt. Es gibt daher keine bestätigten Prod-Zeilenzahlen, keine tatsächlichen `EXPLAIN ANALYZE`-Pläne, keine Laufzeiten und keinen Nachweis, ob die vorgeschlagenen Query-Gates eingehalten werden. Die Mengen in §6.1 sind explizite Benchmark-Annahmen.
- Die Annahme „80 Channels × 1.000 Emotes × 180 Tage“ ist eine Testgröße, keine bestätigte Emote-Obergrenze. Set-Wechsel und Backfills können `UsageStats` vergrößern.
- Die #380-Rohverträge wurden auf `origin/feat/380-load-sampling` geprüft; tatsächliche Produktions-Samplingdichte, cgroup-Limits und tägliche Null-/Fehlerraten sind unbekannt.
- **Arbeitsumgebungsabweichung:** der Checkout steht auf `docs/381-dashboard-spec` bei `aa603260`, während das lokale `origin/main` inzwischen `32a46f73` ist und PR #385 enthält (`git status` zeigte `behind 15`). Das widerspricht der Aufgabenbeschreibung „#385 noch nicht gemergt“. Für A wurde deshalb der angeforderte Checkout vor #385 als Baseline verwendet; für B der ausdrücklich benannte #380-Branch. Es wurden keine Git-Metadaten verändert. Die aufrufende Session muss den Branch-Stand beim späteren Integrieren abgleichen.
