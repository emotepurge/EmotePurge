# Admin-Log-Ansicht: gespeicherte, filterbare Logs aus Api und Worker (#383)

**Stand:** 2026-10-14  
**Status:** Umsetzungsspec, kein Code. Inhalt aus dem Brainstorming mit dem Betreiber am 2026-10-14; alles, was dort nicht entschieden wurde, ist als **„Entscheidung des Autors (offen für Einspruch)“** markiert und in §19 gesammelt  
**Geprüft gegen:** `main` bei `92608b70`  
**Zielseiten:** neue Route `/admin/logs` (nur globale Admins) und eine kompakte Zeile auf `/admin/monitoring`  
**Lieferung:** sechs Tickets (§16). T0, die Lärmbeseitigung an der Quelle, ist ein eigener kleiner PR ohne Migration und geht zuerst. T1–T5 bringen **eine** additive Migration mit, die wie jede andere vor den Images von Hand läuft (`PendingMigrationGuard`)

## 1. Ziel und Erfolgskriterium

Heute heißt Diagnose auf Prod: Claude gibt dem Betreiber `docker logs … | grep …`-Befehle für den VPS, der Betreiber führt sie aus und kopiert das Ergebnis zurück. Die typischen Anlässe sind ein sauberer Start nach einem Deploy, die 7TV-Synchronisation und EventAPI sowie TwitchLib-Reconnects. Drei Schwächen kommen dazu:

1. **Fehler bleiben unbemerkt**, weil niemand ungefragt in Logs schaut.
2. **Nach jedem Deploy sind die Logs des alten Containers weg.** Portainer legt die Container neu an, und die Log-Rotation des Hosts begrenzt den Rest.
3. **Eine Gegenprobe braucht Shell-Zugang zur Produktion.**

Die Seite dient zwei Zwecken, mit Vorrang in dieser Reihenfolge:

1. **Gezieltes Nachschlagen.** Eine freie Filterzeile steht in der URL. Claude kann dem Betreiber also einen Link schicken, der die Ansicht vorgefiltert öffnet, und der Betreiber filtert dort weiter. Dazu kommen gespeicherte Filter, serverseitig und auf Handy wie Desktop gleich.
2. **Überhaupt bemerken.** Eine Zeile auf `/admin/monitoring` zeigt „Fehler der letzten 24 h: n“ und öffnet per Klick die Log-Seite mit genau diesem Filter.

**Erfolgskriterium:** Die vier Fragen, für die Claude bisher grep-Befehle ausgibt, beantwortet je ein Link oder ein Klick auf einen gespeicherten Filter: „Start nach Deploy“, „Twitch-Reconnects“, „7TV-Sync/EventAPI“ und „Alle Fehler“. Das gilt über Deploys hinweg für die letzten sieben Tage, ohne Shell-Zugang. Das Logging bremst und blockiert dabei weder Api noch Worker, auch nicht bei vollem Puffer, bei Postgres- oder bei Redis-Ausfall.

## 2. Geprüfter Bestand

### 2.1 Logging heute

- Api und Worker loggen über reines `Microsoft.Extensions.Logging` mit den Default-Providern (Console). Es gibt weder Serilog/NLog noch einen eigenen Provider. Die Api richtet ihren Host in `src/EmotePurge.Api/Program.cs:20` ein (`WebApplication.CreateBuilder`). Der Worker baut je Einstiegspunkt einen eigenen Host: `src/EmotePurge.Worker/Program.cs`, `RunWorkerAsync` und `RunHarnessAsync`.
- **Die Mindest-Level unterscheiden sich.** `src/EmotePurge.Worker/appsettings.json:2-10` setzt `Microsoft.EntityFrameworkCore: Warning`, `System.Net.Http.HttpClient: Warning` und `TwitchLib.Client.TwitchClient: Information`. `src/EmotePurge.Api/appsettings.json:2-8` setzt nur `Microsoft.AspNetCore` und `System.Net.Http.HttpClient` auf Warning. In der Api landet damit jedes SQL-Kommando aus `Microsoft.EntityFrameworkCore.Database.Command` auf Information im Log. Die `Development`-Overlays ändern daran nichts. Laut DECISIONS 2026-09-23 („Api's outgoing HTTP client requests log at Warning“) setzt kein Compose-File `ASPNETCORE_ENVIRONMENT`, also gilt im Container die Basisdatei.
- `docker-compose.prod.yml` überschreibt kein `Logging__*`. Api und Worker laufen dort mit je `cpus: "1.0"` und `memory: 512M` (`:54-58`, `:142-147`). Postgres ist `postgres:16-alpine`.
- **Im Image steht keine Version, die der Prozess lesen könnte.** `.dockerignore` schließt `**/.git` aus, deshalb fehlt den Assemblies im Image der Commit-Hash in `InformationalVersion`. Welcher Deploy ein Eintrag war, lässt sich nur über den Prozessstart erkennen.

### 2.2 Redaction #246 und wo ein neuer Sink sitzt

- `RedactingTwitchClientLoggerFactory` (`src/EmotePurge.Worker/RedactingTwitchClientLoggerFactory.cs`) umhüllt **nur** die Factory, die `TwitchChatManager.CreateClient` dem `TwitchClient` übergibt (`src/EmotePurge.Worker/TwitchChatManager.cs:1145`). Diese Factory gibt Loggern der Kategorie `TwitchLib.Client.TwitchClient` einen `RedactingLogger`. Der ersetzt bei `LogParsingError`, `LogUnaccountedFor` und `LogLeavingChannel` den State durch einen bereinigten (`RedactedState` mit einem einzigen Eintrag `{OriginalFormat}`) und ruft danach den inneren Logger. Der innere Logger ist der Logger der App-weiten `LoggerFactory`, und der verteilt an **alle** registrierten `ILoggerProvider`.
- **Folge:** Ein neuer `ILoggerProvider` sitzt automatisch hinter der Redaction. Er sieht dasselbe wie die Console, also den bereinigten State. Eine eigene Platzierung braucht er nicht. Er darf die Redaction aber nicht umgehen. Er liest State, Message und Exception ausschließlich aus dem `Log`-Aufruf, den er bekommt, und registriert sich nicht als eigene Factory vor dem Wrapper. Bereinigte Einträge haben keine strukturierten Properties, weil `RedactedState` nur `{OriginalFormat}` trägt.
- `TwitchLibRawLineRedaction.Redact` liefert **einen** String der Form `command=<…>, tagKeys=[…]`. Das IRC-Kommando kennt der Wrapper also nur als Teil dieses Strings, nicht als eigenen Wert (§4.2).
- Die übrigen Datenschutz-Vorkehrungen gelten unverändert und gehen dem Sink voraus. `IrcLineSpliceRule.TagBlockForLog` (DECISIONS 2026-09-23) schwärzt identifizierende Tag-Werte. Ausgeschlossene Kanäle nennt der Code nicht beim Namen („Left a channel.“, `Worker.cs`).

### 2.3 Admin-Bereich, Live-Stream, Monitoring

- `src/EmotePurge.Api/Endpoints/AdminEndpoints.cs:34-36` legt die Gruppe `/api/admin` mit `RequireAuthorization()` und `GlobalAdminAuthorizationFilter` an (401/403). **Die Gruppe hat bewusst keine Rate-Limit-Policy** (Kommentar `:31-33`): Schutz ist die Allowlist. Neue Routen in dieser Gruppe sind automatisch admin-only.
- Der Admin-Stream ist `GET /api/admin/live` (`AdminEndpoints.cs:353`, `LiveEndpoints.OpenAdminAsync`). Er filtert auf `LiveEvents.AdminTypes` (`src/EmotePurge.Core/Messaging/LiveEvents.cs:60`): `worker.health`, `worker.roster`, `channel.synced`, `live.changed`. Ereignisse sind dünn: `{type, channel?, sessionId?}`. Clients laden über REST nach, Payload ist nie Daten. Der Kanal ist `live:events`. Im Frontend laufen Streams über `liveEvents()` und `liveReload()` (`web/src/app/core/live/live-reload.ts`, `web/.claude/CLAUDE.md`).
- Der Broker `RedisLiveEventStream` loggt pro zugestelltem Ereignis nur im Fehlerfall (Warning, `:208`). Für die Rekursionsfrage in §5.6 ist das relevant.
- Zum Admin-Frontend gehören `web/src/app/features/admin/admin-layout.ts` (Tableiste: Monitoring, Kanäle, Nutzer, Audit-Log), die Routen in `web/src/app/app.routes.ts:57-95` hinter `adminGuard` und der URL-Zustand für Listen über `listQueryState` (`web/src/app/core/routing/list-query-state.ts`). `/admin/monitoring` (`admin-monitoring-page.ts`) aktualisiert Aktivität und Rate-Limits über einen 60-s-Timer, der bei verstecktem Tab pausiert (#381 §8).
- `web/src/app/shared/charts/line-chart.ts` ist laut `docs/UI-Designsprache.md` §2.6 „the app's one chart primitive“. Einen Balken-Primitiv gibt es nicht.
- `TtlCache<T>` ist eine **private** verschachtelte Klasse von `AdminDashboardReader` (`src/EmotePurge.Infrastructure/Services/AdminDashboardReader.cs:151`).

### 2.4 Retention, Sampler-Muster, DI

- `RetentionPolicy` (`src/EmotePurge.Core/Services/RetentionPolicy.cs`) hält alle Fristen als Konstanten. `DataRetentionService.RunAsync` (`src/EmotePurge.Infrastructure/Services/DataRetentionService.cs:86`) läuft Kategorie für Kategorie. Die System-Load-Kategorien sind einzeln per `try/catch` gekapselt und melden `…Failed` im `RetentionRunSummary` (`src/EmotePurge.Core/Services/IDataRetentionService.cs:58-71`). `RetentionRunSummaryFormatter` gibt eine Zeile pro Lauf aus.
- `DataRetentionWorker` wartet auf die Boot-Recovery plus `Retention:StartupDelayMinutes` (Default 10) und läuft dann alle `Retention:IntervalHours` (Default 24). Geschrieben wird nur bei `Retention:Enforce = true`, Default ist `false`. Auf Prod ist der Wert laut Betreiber `true`.
- **Das Muster für etwas, das in Api und Worker läuft**, ist `SystemLoadSamplerRegistration.AddSystemLoadSampler(source)` (`src/EmotePurge.Infrastructure/Services/SystemLoadSampler.cs:69-76`). Die Api ruft es neben `AddEmotePurgeInfrastructure` auf (`Program.cs:36-37`), der Worker in `AddWorkerHostedServices` (`WorkerServiceRegistration.cs:74`).
- **Der Harness teilt `AddWorkerCore` und damit `AddEmotePurgeInfrastructure`** (`WorkerServiceRegistration.cs:32-50, 86-100`). Was in `AddEmotePurgeInfrastructure` registriert wird, läuft also auch in jedem `harness`-Container.
- `AddEmotePurgeInfrastructure` registriert `AppDbContext` über `AddDbContext(… UseNpgsql(…))` (`ServiceCollectionExtensions.cs:39-40`). EF Core loggt damit über die App-`LoggerFactory`. Das Redis-Multiplexer entsteht über `ConnectionMultiplexer.Connect(string)` ohne `LoggerFactory` (`:49-50`).
- Hosted Services starten in Registrierungsreihenfolge und stoppen in umgekehrter.

### 2.5 Datenschutzrelevanter Inhalt heutiger Logzeilen

Log-Templates enthalten unter anderem `{User}`/`{UserId}` mit numerischen Twitch-IDs (`TwitchUserTokenService.cs:82-119`, `ModeratedChannelsProvider.cs`, `TwitchHelixClient.cs:113,145`), `{Login}` (`MyChannelsService.cs:213`) und durchgehend `{Channel}` (Kanal-Logins). Chattext und Chatter-Identitäten sind seit #246 draußen. IP-Adressen und E-Mail-Adressen kommen in eigenen Templates nicht vor. Exception-Texte von Fremdbibliotheken sind nicht kontrollierbar.

`scripts/backup-postgres.sh:86` sichert die ganze Datenbank per `pg_dump --format=plain`, und die Sicherungskette hält Dumps bis zu 90 Tage (DECISIONS 2026-10-09).

### 2.6 Gemessenes Volumen auf Prod (Betreiber, 2026-10-14)

Fenster rund 20 h ab Containerstart 2026-10-13 17:27 UTC:

| Quelle | Einträge | Rohgröße | Zusammensetzung |
|---|---|---|---|
| Api | 13.167 | 8,0 MB | davon 13.156 `Microsoft.EntityFrameworkCore.Database.Command` auf Information, der Rest ~11 |
| Worker | 3.542 | 0,6 MB | 3.210 Information, 332 Warning. Top-Kategorien: SevenTvEventClient 1455, SevenTvSyncService 902, TwitchLib.Client.TwitchClient 455, TwitchChatManager 288, TwitchLivePollWorker 280, Worker 66, TwitchIdentityReconcileWorker 22, SystemLoadRollupWorker 22, SevenTvEventWorker 20, TwitchConnectionWatchdog 13, SevenTvApiClient 12 |

Bei den Warnungen entfallen 312 auf `TwitchLib.Client.TwitchClient[1006327295]`, das ist `LogUnaccountedFor` (durch Dekompilieren von TwitchLib.Client 4.0.1 bestätigt). Dazu kommen 12 SevenTvApiClient, 5 TwitchConnectionWatchdog, 3 SevenTvSyncService und 1 TwitchChatManager. Die 312 unaccounted lines verteilen sich so: rund 233 USERNOTICE viewermilestone/Watch-Streaks, der Rest USERNOTICE Subs/Gift-Subs/Raids/Announcements, die wir nicht auswerten, und **4 NOTICE**.

Ohne das EF-Rauschen sind das rund 3,6k Einträge pro Tag, also etwa 25k Zeilen in sieben Tagen. Die Größe ist kein Problem. Für Spitzen gilt trotzdem eine Obergrenze (§5.3).

### 2.7 Wo der Code den Annahmen widerspricht oder sie präzisiert

- Das Issue skizziert einen Redis-Stream als Ringpuffer. Der Betreiber hat stattdessen **Postgres** als Speicher entschieden; Redis bleibt reiner Durchlauf (B3).
- „Der Wrapper kennt das Kommando schon aus der Redaction“ stimmt nur halb. Er kennt es als Teil des Ausgabe-Strings. T0 muss es als eigenen Wert herausreichen, ohne den Ausgabe-String erneut zu parsen (§4.2).
- Der Sink darf **nicht** in `AddEmotePurgeInfrastructure` registriert werden, obwohl das sonst der einzige DI-Registrierungspunkt ist. Sonst schriebe jeder Harness-Lauf Einträge mit Quelle `worker` (§5.1).
- Die Backups enthielten die Log-Tabelle bis zu 90 Tage. Die 7-Tage-Aussage stimmt erst, wenn der Dump die Tabellendaten auslässt (§6.4).
- Postgres auf Alpine läuft mit `C`-Locale. `ILIKE` faltet Groß- und Kleinschreibung deshalb nur für ASCII; „Ä“ trifft „ä“ nicht (§7.2).
- Die Retention läuft einmal je 24 h und nur bei `Retention:Enforce = true`. Ein Eintrag lebt also bis zu ~8 Tage, und lokal mit dem Default `false` wird gar nicht gelöscht (§6.4).

## 3. Bindende Betreiberentscheidungen (B1–B12, 2026-10-14)

| Nr. | Entscheidung |
|---|---|
| B1 | Zwei Zwecke: gezieltes Nachschlagen über eine freie Filterzeile ohne feste Kategorien, gespiegelt in der URL, und Bemerken über eine Fehlerzahl auf `/admin/monitoring`. |
| B2 | **Gespeicherte Filter gehören zu v1:** Name plus Filterstring, **serverseitig** in der DB, als Chip-Leiste mit „+ Filter speichern“. Vier sind vorbelegt: „Start nach Deploy“, „Twitch-Reconnects“, „7TV-Sync/EventAPI“, „Alle Fehler“. Die Strings dazu legt diese Spec fest (§7.5); was dem Betreiber nicht gefällt, streicht er. |
| B3 | Speicher ist **Postgres**, Aufbewahrung **7 Tage** über einen neuen `RetentionPolicy`-Eintrag, durchgesetzt vom bestehenden `DataRetentionWorker`. Redis ist nur der Live-Durchlauf über den bestehenden Admin-SSE-Pfad, nicht der Speicher. |
| B4 | Gespeichert wird ab **Information**. Debug und Trace nie. |
| B5 | Die Kachel zeigt **nur Error/Critical, keine Warnungen.** Rund 300 Warnungen pro Tag sind normales Rauschen; ein Warnungszähler stünde dauerhaft hoch und würde ignoriert. Klick öffnet `/admin/logs` mit diesem Filter. |
| B6 | Kein Telegram und kein Alerting in diesem Issue. |
| B7 | **Rauschen wird an der Quelle beseitigt, nicht in der Ansicht versteckt.** Die Api setzt EF Core auf Warning wie der Worker, und unabhängig davon speichert der Sink diese Kategorie nie unter Warning. `LogUnaccountedFor` wird **nur für USERNOTICE** auf Debug herabgestuft; unbekannte **NOTICE** bleiben Warning. Daraus folgt: kein eigenes Stummschalten oder „bekannt, ausblenden“ in der Ansicht, Ausschluss nur über den normalen Filter (Negation). |
| B8 | Seitenaufbau **Variante B**: Filterzeile mit Chips, Zeitachse als Histogramm mit getrennt erkennbaren Warnungen und Fehlern (Klick auf einen Balken grenzt den Zeitraum ein), Facetten nach Quelle, Level und Kategorie (Klick fügt hinzu, auch als Ausschluss), dichte einzeilige Liste neueste zuerst mit aufklappbaren Details und Nachladen beim Scrollen, Live-Einträge oben kurz hervorgehoben. **Kein Springen**, wenn der Nutzer nach unten gescrollt hat: dann ein Hinweis „n neue“. |
| B9 | **Desktop zuerst, das Handy muss trotzdem gut funktionieren.** Auf dem Telefon wird die Zeitachse ein schmaler Streifen, die Facetten wandern hinter einen „Filter“-Knopf, die Liste steht allein mit Zeit, Level und Nachricht, Details öffnen auf Tippen. |
| B10 | Gefiltert wird **serverseitig** über alle 7 Tage, nicht nur über das Geladene. |
| B11 | Unverhandelbar: Logging bremst und blockiert nie, Drops werden gezählt und auf der Seite gezeigt. Keine Rekursion. Die Redaction gilt vor dem Sink. Admin-only. Die Datenschutzerklärung bekommt einen Satz (Vorschlag in §12). |
| B12 | Außerhalb des Umfangs: externe Log-Stacks (Loki/ES/Seq/Grafana), Docker-Socket, Langzeitarchiv (jenseits von 7 Tagen bleiben `docker logs` und die Host-Rotation die Quelle), Alerting/Telegram, Debug-Erfassung. |

## 4. Vorarbeit T0: Rauschen an der Quelle

Eigener kleiner PR ohne Migration und unabhängig von allem Weiteren. Er kann sofort auf Prod.

### 4.1 Api: EF Core auf Warning

`src/EmotePurge.Api/appsettings.json` bekommt `"Microsoft.EntityFrameworkCore": "Warning"`, genau wie der Worker.

**Entscheidung des Autors (offen für Einspruch), A1:** Gesetzt wird der ganze Namensraum `Microsoft.EntityFrameworkCore`, nicht nur `.Database.Command`. So stimmen Api und Worker überein, und es fällt nichts Nützliches weg: unterhalb von Warning loggt EF außer den Kommandos nur Start- und Modellmeldungen.

Wirkung: In 20 h fallen rund 13.156 Zeilen aus der Api-Console weg.

### 4.2 Worker: `LogUnaccountedFor` nur für USERNOTICE herabstufen

- `TwitchLibRawLineRedaction` liefert das erkannte Kommando zusätzlich zum redigierten Text als eigenen Wert. Das ist dieselbe Allow-List-Erkennung wie heute: `<none>`, `<unparsed>` oder ein bekanntes Kommando. Der Ausgabe-String bleibt bytegleich.
- `RedactingTwitchClientLoggerFactory.RedactingLogger.Log` gibt den Aufruf **nur dann** mit `LogLevel.Debug` statt mit dem Original-Level an den inneren Logger weiter, wenn `EventId.Name == "LogUnaccountedFor"` **und** das Kommando exakt `USERNOTICE` ist. Message und EventId bleiben, wie sie sind. Weil das Worker-Mindestlevel für `TwitchLib.Client.TwitchClient` Information ist, verwirft MEL den Eintrag für **alle** Provider, also auch für die Console. Genau das ist gewollt (B7).
- `NOTICE`, `<unparsed>`, `<none>` und jedes andere Kommando bleiben Warning. `LogParsingError` bleibt Error, `LogLeavingChannel` Information.
- Erwartete Wirkung: rund 308 von 312 Warnungen in 20 h fallen weg. Danach bleiben rund 24 Warnungen pro Tag im Worker.

### 4.2a Worker: Exceptions der redigierten TwitchLib-Ereignisse

**Entscheidung des Orchestrators (Builder-Check), O3:** `RedactingLogger.Log` reicht `exception` heute unverändert an den inneren Logger weiter (`RedactingTwitchClientLoggerFactory.cs:82`). Bei `LogParsingError` kommt die Parse-Exception aus TwitchLib, und deren `Message` (auch die einer Inner Exception) kann den Rohtext der IRC-Zeile enthalten. Damit liefe genau das an der Redaction vorbei, was #246 schließen sollte. Das betrifft heute schon die Console.

- **Ort: der Wrapper, in T0.** Für jeden Aufruf, dessen State der Wrapper ersetzt (`LogParsingError`, `LogUnaccountedFor`, `LogLeavingChannel`), gibt er statt der Original-Exception einen **bereinigten Ersatz** weiter. Der Ersatz trägt für die Kette (`InnerException`, bei `AggregateException` alle `InnerExceptions`, höchstens 8 Ebenen) je Glied nur den **vollen Typnamen** und den **Stacktrace**. Er trägt nie `Message`, nie `Data` und nie das Ergebnis von `ToString()` des Originals. Seine eigene Message nennt den Originaltyp und die Marke `message withheld (#246)`. `ToString()` des Ersatzes zeigt damit Typen und Stacks der ganzen Kette und keinen Originaltext. Die Console und `docker logs` profitieren also mit.
- **Diagnose bleibt möglich:** Typ und Stack zeigen, wo im Parser es scheiterte. Die Message war für die Diagnose ohnehin ersetzbar durch die redigierte Zeile `command=…, tagKeys=[…]`, die schon in der Message des Eintrags steht.
- **Im Sink (§5.2):** `ExceptionType` ist der **Originaltyp**, den der Ersatz offenlegt, nicht der Typ des Ersatzes. `Exception` ist der bereinigte Text. **Zweite Linie:** Kommt bei Kategorie `TwitchLib.Client.TwitchClient` mit einem der drei EventNames eine Exception an, die **nicht** der Ersatz ist (der Wrapper wurde also umgangen), speichert der Sink nach derselben Regel nur Typen und Stacks der Kette.
- Alle anderen Exceptions, auch andere TwitchLib-Ereignisse ohne State-Ersatz, bleiben unverändert.

### 4.3 Tests und Akzeptanz T0

- `tests/EmotePurge.Worker.Tests`: Für `LogUnaccountedFor` + USERNOTICE kommt beim inneren Logger Level Debug mit unveränderter redigierter Message an. Für NOTICE, ein unbekanntes Kommando, `<unparsed>` (siehe das „SECRET“-Beispiel der Klasse) und `<none>` bleibt es Warning. `LogParsingError` und `LogLeavingChannel` behalten ihr Level. Die bestehenden Redaction-Tests bleiben grün, der Ausgabe-String ist bytegleich. Zu §4.2a (O3): `LogParsingError` mit einer Exception, deren `Message` und deren Inner-Exception-`Message` einen präparierten Rohtext tragen (Chattext, `display-name=`-Wert), kommt beim inneren Logger als Ersatz an. Weder dessen `Message` noch dessen `ToString()` enthält einen der präparierten Texte, beide enthalten aber den Originaltyp beider Glieder und mindestens einen Stackframe. Eine Exception bei einem nicht ersetzten Ereignis wird unverändert durchgereicht.
- Live (Regel 16): Lokaler Worker gegen echtes Twitch, ein Kanal mit Sub-, Raid- oder Watch-Streak-Aufkommen (z. B. `handofblood`). Die Console zeigt keine `Unaccounted for … command=USERNOTICE` mehr. Die lokale Api zeigt bei einem Seitenaufruf keine `Executed DbCommand`-Zeilen mehr.
- Ein DECISIONS-Eintrag (englisch) nennt die Messzahlen aus §2.6, beide Änderungen und die Begründung „Rauschen an der Quelle statt Stummschalten in der Ansicht“.

## 5. Erfassung: Sink, Puffer, Schreiber

### 5.1 Registrierung und Lebenszyklus

- **Neue Extension `AddLogCapture(LogSource source)`** in Infrastructure, nach dem Muster von `AddSystemLoadSampler`. Sie registriert den `ILoggerProvider` (Sink), den prozessweiten Puffer, den Schreiber als Hosted Service und die Prozessidentität. Die Api ruft sie in `Program.cs` auf, der Worker in `AddWorkerHostedServices`. **`AddHarness` ruft sie nicht auf.**

  **Entscheidung des Autors (offen für Einspruch), A2:** Ein Harness-Lauf ist ein einmaliges CLI. Seine Zeilen unter Quelle `worker` vermischten sich mit denen des laufenden Workers. Der bestehende Strukturtest zur Harness-Registrierung bekommt eine Prüfung, dass im Harness-Graph weder Sink noch Schreiber vorkommen.
- **Reihenfolge:** Der Schreiber wird als **erster** Hosted Service des Prozesses registriert. Er stoppt damit als letzter und nimmt die Abschaltmeldungen der anderen noch mit (§5.5). In der Api heißt das: vor `AddEmotePurgeInfrastructure` und `AddSystemLoadSampler`, oder diese Reihenfolge per Test belegen. Im Worker steht er vor `Worker` am Anfang von `AddWorkerHostedServices`. Die Boot-Recovery-Reihenfolge der übrigen Dienste bleibt unberührt, denn der Schreiber hängt an keinem `BootRecoveryGate`.
- **Prozessidentität:** `InstanceId` sind 8 zufällige Hex-Zeichen pro Prozess, im Format von `WorkerIdentity.InstanceId`. `ProcessStartedUtc` ist die Startzeit des Betriebssystemprozesses, im Container praktisch der Containerstart.

  **Entscheidung des Autors (offen für Einspruch), A3:** Die Identität ist eine eigene Infrastructure-Klasse. `WorkerIdentity` wird in diesem Issue nicht umgebaut, die Worker-Health-Karte zeigt also eine andere Instanzkennung als die Logs. Angleichen ist ein möglicher Nachläufer.
- **Konfiguration:** `LogCapture:Enabled` (Default `true`) und `LogCapture:DailyEntryBudget` (Default `20000`, §5.3). Bei `Enabled = false` registriert `AddLogCapture` gar nichts. `docker-compose.prod.yml` und `docker-compose.yml` reichen `LogCapture__Enabled=${LOG_CAPTURE_ENABLED:-true}` an `api` und `worker` durch, damit der Betreiber ohne Image-Wechsel abschalten kann. `tests/EmotePurge.Api.Tests` setzt den Wert in seiner Factory auf `false`, weil die Suite container-frei läuft.
- **Fest im Code, nicht konfigurierbar:** die Untergrenze Information (B4) und die Ausschlussregeln in §5.2. Eine Provider-spezifische Konfiguration über den Provider-Alias `LogCapture` (`Logging:LogCapture:LogLevel:<Kategorie>`) darf einzelne Kategorien **anheben**, also strenger machen. Unter die Untergrenze kommt sie nie.

### 5.2 Was erfasst wird

**Ausschlussregeln.** Sie gelten vor jeder Konfiguration und werden beim Anlegen des Loggers je Kategorie entschieden, damit ein ausgeschlossener Logger `IsEnabled = false` meldet und nichts kostet:

| Regel | Begründung |
|---|---|
| Level < Information wird nie erfasst | B4 |
| `Microsoft.EntityFrameworkCore.Database.Command` < Warning wird nie erfasst | B7, unabhängig von `appsettings` |
| `Npgsql` und `Npgsql.*` < Warning wird nie erfasst | Treiberrauschen. Fehler bleiben sichtbar |
| Eigene Kategorien der Erfassung (`EmotePurge.Infrastructure.Logging.*`) werden auf **keinem** Level erfasst | Keine Rekursion (§5.4). Die Console sieht sie weiter |

**Felder je Eintrag.** Das Schema steht in §6.1.

- **Zeitpunkt:** UTC zum Zeitpunkt des `Log`-Aufrufs, aus `TimeProvider`.
- **Quelle und Prozess:** `Source` (`api`/`worker`), `InstanceId`, `ProcessStartedUtc`.
- **Level, Kategorie, EventId, EventName:** Kategorie höchstens 256 Zeichen, EventName höchstens 128.
- **Message:** die mit dem `formatter` des Aufrufs gerenderte Nachricht, also derselbe Text wie in der Console. Höchstens **4.096 Zeichen**; danach wird gekürzt, die Marke `…[truncated]` angehängt und `Truncated = true` gesetzt.
- **Exception:** `ExceptionType` (voller Typname der äußersten Exception) und `Exception` (`ToString()` inklusive Inner Exceptions und Stack), höchstens **16.384 Zeichen**, sonst gekürzt wie oben. **Ausnahme für die redigierten TwitchLib-Ereignisse (O3, §4.2a):** dort nur Originaltyp sowie Typen und Stacks der Kette, nie eine Message.
- **Properties:** aus einem State der Form `IReadOnlyList<KeyValuePair<string, object?>>`, ohne `{OriginalFormat}`.
  - Schlüssel werden **kleingeschrieben** (invariant) gespeichert.
  - Werte sind JSON-Skalare. Strings, Zahlen und Bool bleiben, wie sie sind; `DateTime`/`DateTimeOffset` werden zu ISO-8601-UTC. Alles andere wird mit `ToString()` in Invariant Culture gerendert, Aufzählungen (außer `string`) werden mit `", "` verbunden.
  - Grenzen: je Wert höchstens 256 Zeichen, höchstens 16 Schlüssel, serialisiert höchstens 4 KiB. Was darüber hinausgeht, fällt weg, und `Truncated` wird gesetzt.
- **Scopes. Entscheidung des Autors (offen für Einspruch), A4:** Aus aktiven Scopes werden nur `RequestPath` und `TraceId` übernommen, als Properties `requestpath` und `traceid`, wenn vorhanden. Der Provider unterstützt dafür externe Scopes. Begründung: Ein ungefangener Api-Fehler (`Microsoft.AspNetCore.Diagnostics…` auf Error) nennt den Pfad nur im Scope. Weitere Scope-Werte (ConnectionId, RequestId, SpanId) bringen für den Betreiber nichts. `RequestPath` enthält keinen Query-String, kann aber Twitch-IDs in Pfaden tragen, z. B. `/api/admin/users/{id}`. Das ist dieselbe Datenklasse wie die Messages (§12).

**Secret-Scrubber. Entscheidung des Autors (offen für Einspruch), A5:** Ein kleiner, getesteter Scrubber läuft vor dem Puffer über Message, Exception und Property-Werte. Er ersetzt nur eng definierte Geheimnis-Formen durch `[redacted]`: `Password=…` bis `;` oder Zeilenende (Connection-Strings), `oauth:` plus Token-Zeichen, `Bearer` plus Token und `access_token=`/`refresh_token=`/`client_secret=` plus Wert. Das ist eine Verteidigungslinie für Fremd-Exceptions und ersetzt nicht das Prinzip „Redaction an der Quelle“ (#246). Mehr Muster gibt es nicht, damit der Scrubber keine Diagnose-Texte zerstört.

### 5.3 Nicht blockieren: Puffer, Budget, Drops

- **Der Sink ruft nie I/O auf, nimmt keine Locks über Prozessgrenzen und wartet nie.** `Log()` baut den Eintrag (Formatieren, Kürzen, Scrubben) und legt ihn per nicht-blockierendem Schreibversuch in einen **begrenzten In-Memory-Kanal**. `Log()` wirft nie. Jede Ausnahme darin wird geschluckt und als Drop gezählt.
- **Puffergrenzen. Entscheidung des Autors (offen für Einspruch), A6:** höchstens **5.000 Einträge** und höchstens **8 MiB** geschätzte Nutzlast (Zeichen × 2 über Message, Exception und Properties). Die engere Grenze gilt. Ist der Puffer voll, wird der neue Eintrag verworfen und gezählt; ältere werden nicht verdrängt. Die 8 MiB schützen das 512-MiB-Container-Limit: 5.000 Einträge mit vollen 16-KiB-Exceptions wären sonst rund 160 MB.
- **Tagesbudget je Prozess. Entscheidung des Autors (offen für Einspruch), A7:** `LogCapture:DailyEntryBudget` hat den Default **20.000** Einträge je UTC-Tag. Das ist rund das Sechsfache des normalen Worker-Tagesvolumens. Information-Einträge enden bei 80 % des Budgets (16.000); die übrigen 20 % bleiben Warning und höher vorbehalten. Danach wird bis Mitternacht UTC verworfen und gezählt. Der Zähler lebt im Prozess und beginnt nach einem Neustart neu; eine Crash-Schleife erzeugt pro Start wenige Einträge. Ergebnis: Selbst im schlimmsten Fall bleibt die Tabelle bei 2 Quellen × 20.000 × ~8 Tagen ≈ **320.000 Zeilen**. Das ist die Bemessungsgrundlage des Performance-Gates (§13.3).
- **Drop-Gründe:**

  | Grund | Bedeutung |
  |---|---|
  | `queue_full` | Puffer voll |
  | `budget` | Tagesbudget erschöpft |
  | `write_failed` | Batch auch nach Wiederholung nicht schreibbar (§5.5) |
  | `capture_failed` | Ausnahme in `Log()` |

  Die Zähler sind prozesslokal und atomar.
- **Drops werden sichtbar,** und zwar als **synthetischer Eintrag** in der Log-Tabelle. Der Schreiber erzeugt ihn selbst, er läuft nicht über `ILogger`. Kategorie `EmotePurge.LogCapture`, Level Warning, Message `"{n} log entries were dropped ({reason breakdown})"`, Properties `dropped`, `queuefull`, `budget`, `writefailed`, `capturefailed`, `firstdroputc`, `lastdroputc`. Höchstens einer je 60 s und Prozess, nur wenn seit dem letzten etwas verworfen wurde. Synthetische Einträge umgehen Puffer und Budget. Damit erscheinen Drops im Histogramm, sind über `cat:LogCapture` filterbar, verschwinden mit der normalen Retention und lassen sich je Zeitraum summieren (§8).

  **Entscheidung des Autors (offen für Einspruch), A8.** Grenze: Drops, die bis zum Prozessende nicht mehr geschrieben werden konnten, etwa weil Postgres bis zum Shutdown weg war, sind verloren. Die Console hat die Originalzeilen weiterhin, und der Schreiber meldet die Restzahl beim Shutdown in der Console.

### 5.4 Keine Rekursion

Die Erfassung schreibt nichts, was sie selbst wieder erfasst. Drei Schichten sorgen dafür:

1. **Eigener Datenweg ohne Logging.** Der Schreiber nutzt **nicht** den `AppDbContext`. Er arbeitet mit einer **eigenen** `NpgsqlDataSource`, gebaut aus `ConnectionStrings:DefaultConnection` **ohne** Logger-Factory, mit eigenem kleinen Pool (höchstens 2 Verbindungen, `Application Name=emotepurge-logcapture`) und rohem ADO.NET: ein Roundtrip pro Batch, Binary-COPY oder Mehrzeilen-INSERT nach Wahl des Umsetzers. EF Core loggt damit nichts für den Schreibpfad. Bei einer Datenquelle ohne Logger-Factory loggt Npgsql nichts. **Umsetzungsauflage:** Mit einem Test belegen, dass der EF-Npgsql-Provider kein prozessweites Npgsql-Logging einschaltet, das auch fremde Datenquellen erfasst.
2. **Ausführungskontext-Sperre.** Der Schreiber setzt für seine gesamte Schleife ein prozessweit bekanntes `AsyncLocal`-Flag. Alles, was in seinem Ausführungskontext geloggt wird, auch aus Bibliotheken und daraus gestarteten Tasks, verwirft der Sink still, ohne Drop-Zählung. Das gilt für DB-Schreiben, Redis-Publish und Statusschreiben.
3. **Kategorie-Ausschluss.** Der Schreiber loggt eigene Störungen (DB nicht erreichbar, Batch verworfen) über `ILogger` seiner eigenen Kategorie unter `EmotePurge.Infrastructure.Logging`. Die Console zeigt sie, der Sink schließt sie aus (§5.2). Das passiert gedrosselt: höchstens eine Zeile je 5 Minuten und Grund, wie beim `SystemLoadSampler`.

Zusätzlich gilt für den Live-Pfad (§5.6): Eine Zustellung von `logs.appended` darf keine Folge erzeugen, die selbst wieder ein `logs.appended` auslöst.

### 5.5 Schreiber: Batches, Ausfälle, Start und Shutdown

- **Batching:** Der Schreiber nimmt bis zu **500 Einträge** oder wartet höchstens **1 s** und schreibt dann einen Batch in einer Transaktion mit **5 s** Command-Timeout.
- **Fehler:** Scheitert ein Batch, wiederholt der Schreiber ihn **einmal** nach 2 s. Scheitert er wieder, verwirft er ihn und zählt jeden seiner Einträge als `write_failed`. Danach wartet er mit wachsendem Abstand (1 s, 2 s, 4 s … höchstens 30 s) auf den nächsten Versuch. Währenddessen füllt sich der Puffer, und neue Einträge laufen als `queue_full` in den Drop-Zähler. Die App merkt davon nichts. Kommt die DB zurück, schreibt der Schreiber zuerst den synthetischen Drop-Eintrag, dann den Puffer.
- **Status-Herzschlag. Entscheidung des Autors (offen für Einspruch), A9:** Mit jedem erfolgreichen Batch und sonst **mindestens alle 60 s** schreibt der Schreiber `LastFlushUtc` in die Zeile seiner Instanz in `LogCaptureStatus` (§6.2), auch ohne neue Einträge. Begründung: Die Kachel (§11) darf bei toter Erfassung nicht „0 Fehler“ zeigen. Ohne Herzschlag sähe eine Api, die stundenlang nichts loggt (gemessen: ~11 Einträge in 20 h), genauso aus wie eine kaputte Erfassung.
- **Redis down:** Das betrifft nur `logs.appended` (§5.6). Der Publish läuft losgelöst von der Schreibschleife, höchstens einer gleichzeitig. Ist der vorige noch offen, entfällt der nächste. Fehler werden verworfen und gedrosselt in die eigene Kategorie geloggt. **Der Schreiber wartet nie auf Redis.**
- **Postgres down:** siehe „Fehler“ oben. Die App läuft weiter. Postgres beim Start nicht erreichbar betrifft die Erfassung nicht eigens, denn `PendingMigrationGuard` beendet den Prozess dann ohnehin.
- **Start:** Der Sink existiert ab dem Bau der `LoggerFactory`. Einträge aus der Zeit, bevor die Hosted Services starten (Host-Build, `PendingMigrationGuard`), liegen im Puffer und werden mit dem ersten Batch geschrieben, mit ihren eigenen Zeitstempeln. Bricht der Guard den Start ab, gehen sie verloren; die Console hat sie.
- **Shutdown:** Der Schreiber stoppt als letzter Hosted Service. In `StopAsync` leert er den Puffer höchstens **3 s** lang, unter dem Docker-Stop-Grace von 10 s. Danach geschriebene oder verbliebene Einträge verwirft er und nennt die Zahl einmal in der Console. Nach dem Stop verwirft der Sink still.

### 5.6 Live-Signal `logs.appended`

- **Neuer Ereignistyp** `logs.appended` in `LiveEvents`, aufgenommen in `AdminTypes`, **ohne** Kanal und Payload. Er ist dünn wie alle anderen.
- Jeder Schreiber veröffentlicht ihn nach einem committeten Batch mit mindestens einem echten (nicht synthetischen) Eintrag, **höchstens einmal je 2 s** und Prozess.
- **Entscheidung des Autors (offen für Einspruch), A10:** **Rekursionsbremse:** Ein Batch, der ausschließlich aus Einträgen der Zustellkategorien besteht (`EmotePurge.Infrastructure.Redis.RedisLiveEventStream`, `EmotePurge.Api.Endpoints.LiveEndpoints`), löst kein `logs.appended` aus. Sonst könnte ein dauerhaft scheiternder Zustellversuch (Warning je Ereignis) über Batch → Publish → Zustellung → Warning einen endlosen Takt alle 2 s erzeugen.
- **Der Client lädt über REST nach (§8.3), das Ereignis trägt keine Einträge.** Damit bleibt der dünne Vertrag des Streams erhalten, und kein Admin-Stream wird mit Logdaten geflutet.
- Verworfene Alternative: 5-s-Polling der Seite ohne neues Ereignis. Das wäre einfacher und hätte kein Rekursionsrisiko, widerspräche aber dem Muster „Stream meldet, REST liefert“. Der Admin-Stream ist ohnehin offen.

## 6. Speicherung

### 6.1 Tabelle `LogEntries`

| Spalte | Typ | Inhalt |
|---|---|---|
| `Id` | `bigint` Identity, PK | Einfügereihenfolge, Tie-Breaker der Sortierung |
| `TimestampUtc` | `timestamptz` not null | Zeitpunkt des `Log`-Aufrufs |
| `Source` | `smallint` not null | `LogSource`: 1 = Api, 2 = Worker. Neues Core-Enum, bewusst nicht `SystemLoadSource` |
| `InstanceId` | `varchar(8)` not null | Prozesskennung |
| `ProcessStartedUtc` | `timestamptz` not null | Prozessstart |
| `Level` | `smallint` not null | MEL-Zahlwert: 2 Information, 3 Warning, 4 Error, 5 Critical |
| `Category` | `varchar(256)` not null | voller Kategoriename |
| `EventId` | `int` null | nur wenn ≠ 0 |
| `EventName` | `varchar(128)` null | |
| `Message` | `text` not null | ≤ 4.096 Zeichen (§5.2) |
| `ExceptionType` | `varchar(256)` null | |
| `Exception` | `text` null | ≤ 16.384 Zeichen |
| `Properties` | `jsonb` null | ≤ 16 Schlüssel, kleingeschrieben, ≤ 4 KiB |
| `Truncated` | `boolean` not null | irgendein Feld gekürzt |

Die Entität liegt in `EmotePurge.Core/Entities`. `Properties` ist dort ein `string?`, in EF als `jsonb` gemappt. Core bleibt damit BCL-only.

**Indizes. Entscheidung des Autors (offen für Einspruch), A11:**

| Index | Zweck |
|---|---|
| `(TimestampUtc DESC, Id DESC)` | Seiten, Kopf-Neuladen, Zeitbereich für Summary und Retention |
| partiell `(TimestampUtc) WHERE "Level" >= 4` | Fehlerzahl der Kachel |
| `(Source, ProcessStartedUtc DESC)` | `instance:latest` |

**Kein** GIN- und kein Trigramm-Index in v1. Bei ~25k Zeilen pro Woche und höchstens ~320k Zeilen im schlimmsten Fall trägt ein durch den Zeitbereich begrenzter Scan Freitext- und Property-Filter. Das Performance-Gate (§13.3) entscheidet. Reißt es, ist `pg_trgm` die Rückfalloption. Das ist eine Postgres-Extension und braucht eine Betreiberentscheidung, keine stille Ergänzung.

### 6.2 Tabelle `LogCaptureStatus`

| Spalte | Inhalt |
|---|---|
| `Source`, `InstanceId` | PK |
| `ProcessStartedUtc` | Prozessstart |
| `LastFlushUtc` | letzter erfolgreicher Batch oder Herzschlag |

Eine Zeile pro Prozessinstanz, per Upsert geschrieben (§5.5). Sie dient der Lebendigkeitsanzeige und `instance:latest`.

### 6.3 Tabelle `LogFilters`

| Spalte | Inhalt |
|---|---|
| `Id` | `int` Identity, PK |
| `Name` | `varchar(60)`, unique, getrimmt; Vergleich ohne Groß-/Kleinschreibung |
| `Filter` | `varchar(512)` |
| `SortOrder` | `int`. Vorbelegte 1–4, neue erhalten max + 1 |
| `CreatedAtUtc`, `UpdatedAtUtc` | |

**Entscheidung des Autors (offen für Einspruch), A12:** Die Filter sind **global**, für alle Admins dieselben. Es gibt nur einen Admin, und die Logs sind global. Kein Audit-Eintrag beim Anlegen, Ändern oder Löschen: Das ist Ansichtskonfiguration, keine Betriebsaktion an Nutzerdaten.

**Entscheidung des Autors (offen für Einspruch), A13:** Die vier vorbelegten Filter (§7.5) **fügt die Migration einmalig ein**. Der Code sät sie nicht bei jedem Start. Ein vom Betreiber gelöschter Filter bleibt damit gelöscht, und genau das verlangt B2.

### 6.4 Retention und Backups

- Neu in `RetentionPolicy`: `LogEntry = 7 Tage`. Gemessen wird an `TimestampUtc`, exklusiv wie alle anderen. Dieselbe Frist gilt für `LogCaptureStatus` gemessen an `LastFlushUtc`. `LogFilters` laufen nie ab.
- `DataRetentionService.RunAsync` bekommt eine eigene Kategorie **nach** den System-Load-Kategorien. Sie ist wie diese per `try/catch` gekapselt. Gelöscht wird in Batches zu 5.000, im Trockenlauf nur gezählt. Das Ergebnis landet in `RetentionRunSummary` als `LogEntriesDeleted` und `LogEntriesFailed`, angehängt mit Defaults wie die bisherigen Felder, damit bestehende Konstruktionen kompilieren. `RetentionRunSummaryFormatter` hängt `log entries deleted: n` bzw. `log entries: failed (count unknown)` an.
- **Wirksame Lebensdauer:** 7 Tage plus bis zu einem Retention-Intervall (Default 24 h). Nur bei `Retention:Enforce = true`; lokal (Default `false`) wächst die Tabelle, begrenzt durch das Tagesbudget.
- **Backups. Entscheidung des Autors (offen für Einspruch), A14:** `scripts/backup-postgres.sh` lässt die **Daten** von `LogEntries` und `LogCaptureStatus` weg (`--exclude-table-data`), das Schema bleibt drin. Sonst stünden Logzeilen bis zu 90 Tage in der Sicherungskette, und die 7-Tage-Aussage in der Datenschutzerklärung wäre falsch. Nach einem Restore sind die Logs leer. Das ist hinnehmbar, denn sie sind Diagnose, keine Nutzdaten. Die Restore-Probe in `docs/Operations.md` wird entsprechend ergänzt.
- **Kontolöschung (#243). Entscheidung des Autors (offen für Einspruch), A15:** Logzeilen mit der Twitch-ID eines gelöschten Kontos werden **nicht** gezielt gelöscht. Sie laufen mit der 7-Tage-Frist aus, und die Datenschutzerklärung sagt das (§12). Begründung: Eine Suche nach IDs in freiem Text wäre unscharf, und die Frist ist kurz.

## 7. Filtersprache

**Entscheidung des Autors (offen für Einspruch), A16** gilt für die gesamte Grammatik in diesem Abschnitt. Ziel ist eine kleine, in einer Minute lernbare Sprache ohne allgemeine Boolesche Logik: **Terme werden mit UND verknüpft, ODER gibt es nur innerhalb eines Schlüssels als Kommaliste, Negation per Minus.** Der Zeitraum gehört **nicht** in den Filterstring. Er ist ein eigener URL-Parameter (§8.2), sodass gespeicherte Filter zeitunabhängig sind.

### 7.1 Grammatik

```
filter      = term { Leerraum term }
term        = [ "-" ] ( schlüsselterm | freitext )
schlüsselterm = schlüssel operator werteliste
schlüssel   = Buchstabe { Buchstabe | Ziffer | "_" | "." }
operator    = ":" | ">=" | "<=" | ">" | "<"
werteliste  = wert { "," wert }
wert        = wort | "\"" { Zeichen außer "\"" | "\\\"" } "\""
freitext    = wort | zitat
```

- **Groß- und Kleinschreibung** ist bei Schlüsseln und bei den Werten von `level`, `source`, `has` und `instance:latest` egal. Für alle Textvergleiche gilt §7.2.
- **Zitate** `"…"` erlauben Leerzeichen, Kommas und Doppelpunkte; `\"` ist ein Anführungszeichen. Ein zitierter Term ohne Schlüssel ist Freitext: `"http://x"` sucht den Text und nicht den Schlüssel `http`.
- **Wildcard** `*` steht in Werten und Freitext für beliebig viele Zeichen. `%`, `_` und `\` werden für `LIKE` maskiert. **Jeder Wert wird als SQL-Parameter übergeben, nie in SQL eingesetzt.**
- **Grenzen:** Filterstring ≤ 512 Zeichen, ≤ 16 Terme, ≤ 16 Werte je Liste, Wert ≤ 200 Zeichen. Ein leerer Filter ist gültig und bedeutet „alles im Zeitraum“.
- **Negation** gilt für den ganzen Term. `-cat:a,b` heißt „weder a noch b“.

### 7.2 Semantik der Schlüssel

| Schlüssel | Operatoren | Werte | Treffer |
|---|---|---|---|
| `level` | `:` `>=` `<=` `>` `<` | `info`/`information`, `warn`/`warning`, `error`/`err`, `crit`/`critical`/`fatal` | `:` exakt bzw. Liste; Vergleich über die Rangfolge Information < Warning < Error < Critical. `debug` und `trace` sind ungültig (`invalid_level`), denn sie werden nie gespeichert (B4) |
| `source` | `:` | `api`, `worker` | Quelle |
| `cat` | `:` | Kategorie oder Teil davon | Ohne `*`: voller Name gleich **oder** Name endet auf `.` + Wert. `cat:TwitchChatManager` trifft also `EmotePurge.Worker.TwitchChatManager`. Mit `*`: Muster über den vollen Namen **oder** über jedes Suffix nach einem Punkt. `cat:SevenTv*` trifft `EmotePurge.Worker.SevenTv.SevenTvEventClient` und `EmotePurge.Infrastructure.Services.SevenTvSyncService`; `cat:TwitchLib.*` trifft `TwitchLib.Client.TwitchClient` |
| `event` | `:` | Name oder Zahl | `EventName` gleich (ohne Groß-/Kleinschreibung) bzw. `EventId` gleich |
| `instance` | `:` | `latest` oder 8 Hex-Zeichen | `latest`: je Quelle die Instanz mit dem jüngsten `ProcessStartedUtc` laut `LogCaptureStatus`, ersatzweise laut `LogEntries` |
| `uptime` | `<` `<=` `>` `>=` | Dauer `\d+(s|m|h|d)` | `TimestampUtc − ProcessStartedUtc` im Vergleich |
| `has` | `:` | `exception` | `Exception IS NOT NULL` |
| *jeder andere Schlüssel* | `:` | Wert | **Property** mit diesem (kleingeschriebenen) Namen; String-Wert gleich bzw. Muster mit `*`. `channel:handofblood` heißt Property `channel` gleich `handofblood`; `channel:*` heißt „Property vorhanden“ |
| *Freitext* | — | Wort oder Zitat | Teilstring in `Message` **oder** `Exception` |

- **Textvergleiche** (Kategorie, Event, Properties, Freitext) ignorieren Groß- und Kleinschreibung über `ILIKE`. **Bekannte Grenze:** Unter der `C`-Locale des Alpine-Postgres faltet `ILIKE` nur ASCII (§2.7). Schlüssel und Kategorien sind ASCII; ein Umlaut im Freitext muss in der richtigen Schreibung getippt werden.
- Bei `level`, `uptime` und `has` sind Muster mit `*` ungültig (`invalid_value`).
- **Stolperstein, dokumentiert in der Hilfe der Seite:** Ein Wort mit Doppelpunkt ist ein Schlüsselterm. `Fehler:` ohne Wert ist `empty_value`; wer den Text sucht, setzt ihn in Anführungszeichen.

### 7.3 Fehler

Ein ungültiger Filter wird nie teilweise angewandt. Die Api antwortet `400` mit

```json
{ "errorCode": "invalid_log_filter", "reason": "unterminated_quote", "position": 14, "length": 1 }
```

`position` und `length` sind UTF-16-Indizes in den übergebenen String und damit direkt als JavaScript-String-Indizes nutzbar. Ein Feld `message` gibt es nicht (Regel 7).

**Das `reason`-Vokabular ist abgeschlossen und sprachneutral:** `unterminated_quote`, `empty_term`, `empty_value`, `invalid_operator` (Operator für den Schlüssel nicht erlaubt), `invalid_level`, `invalid_source`, `invalid_instance`, `invalid_duration`, `invalid_value`, `too_long`, `too_many_terms`, `too_many_values`. Das Frontend übersetzt es unter `admin.logs.filterErrors.<reason>` in de und en. Das ist dasselbe Muster wie das Fehlervokabular des Backfills.

### 7.4 Wo der Parser lebt

**Entscheidung des Autors (offen für Einspruch), A17:** Es gibt **einen** Parser, serverseitig in Infrastructure. Er ist rein und hat Unit-Tests. Er erzeugt einen typisierten Ausdruck, den der Query-Service in parametrisiertes SQL übersetzt. Das Frontend parst **nicht**: Zwei Implementierungen in zwei Sprachen liefen auseinander.

Das Frontend braucht nur kleine Stringwerkzeuge mit Vitest-Abdeckung: einen Wert bei Bedarf quoten, einen Term anhängen, wenn er noch nicht als ganzer Term vorkommt, und eine Fehlerposition markieren. Facetten- und Property-Klicks hängen Terme an; einen bestehenden Term entfernen sie nicht.

### 7.5 Vorbelegte Filter

| Name (Seed) | Filterstring | Absicht und Grundlage im Code |
|---|---|---|
| Start nach Deploy | `instance:latest uptime<15m` | Alles der jüngsten Api- und Worker-Instanz aus deren ersten 15 Minuten: `Microsoft.Hosting.Lifetime` („Application started“, „Now listening on“), Boot-Recovery in `EmotePurge.Worker.Worker`, Rejoin mit 0,6 s je Kanal, serieller 7TV-Sync mit ~0,85 s je Kanal (#272), erster Live-Poll und der erste Retention-Lauf nach 10 min Startverzögerung. Die Level-Facette zeigt sofort, ob Warnungen oder Fehler dabei sind |
| Twitch-Reconnects | `source:worker cat:TwitchConnectionWatchdog,TwitchChatManager,TwitchLib.* -"angestoßen (Quelle" -" gejoint."` | Watchdog-Meldungen („Twitch-Verbindung verloren …“, „Wiederaufbau #n fehlgeschlagen“, „Twitch-Verbindung steht nach …“, „Erzwinge Reconnect“, „Kontext zum erzwungenen Reconnect: … gejointen Channels …“), Verlust-Signale, `TwitchClient verbunden/getrennt`, der verworfene Join eines ersetzten Clients und TwitchLibs eigene Verbindungszeilen. Ohne die zwei Zeilen pro Kanal und Rejoin. Die Ausschlüsse sind **exakte Phrasen** (siehe Absatz unter der Tabelle). Sie hängen an heutigem deutschem Message-Text (Bestand vor #152); werden diese Messages ins Englische umgestellt, muss der Seed mit |
| 7TV-Sync/EventAPI | `source:worker cat:SevenTv*` | `SevenTvEventClient`, `SevenTvEventWorker`, `SevenTvSyncService`, `SevenTvPeriodicResyncWorker`, `SevenTvApiClient` im Worker. Volumenstark (~2,4k je 20 h); die Facetten Level und Kategorie grenzen weiter ein |
| Alle Fehler | `level>=error` | Error und Critical. Derselbe String, den die Kachel verlinkt (§11), damit der Chip dort aktiv erscheint |

**Ausschlüsse des Seeds „Twitch-Reconnects“ —** **Entscheidung des Orchestrators (Builder-Check), O1:** Die frühere Fassung `-angestoßen -gejoint` war als Teilstring zu breit. `-gejoint` warf auch `TwitchConnectionWatchdog.cs:218` („Kontext zum erzwungenen Reconnect: {LiveCount} von {TotalCount} **gejoint**en Channels laut Helix live.“) und `TwitchChatManager.cs:998` („Channel {Channel} **gejoint** (Client #{Generation}) — bereits ersetzter Client, verworfen.“) hinaus, also genau zwei Reconnect-Zeilen. Gegen die Templates im Code geprüft (`grep -rn "gejoint\|angestoßen" src`, vier Treffer) treffen die beiden Phrasen jetzt nur noch die Zeilen je Kanal:

| Phrase | trifft | trifft nicht |
|---|---|---|
| `"angestoßen (Quelle"` | `TwitchChatManager.cs:540` „JOIN für {Channel} angestoßen (Quelle {Source}, Client #{Generation}).“ | sonst kein Template mit „angestoßen“ |
| `" gejoint."` (mit führendem Leerzeichen und Punkt) | `TwitchChatManager.cs:1010` „Channel {Channel} gejoint.“ | `:998` (danach folgt „ (Client“), `TwitchConnectionWatchdog.cs:218` („gejointen“) |

Der Pflichttest zum Seed rendert die vier Templates mit Beispielwerten, wendet den Seed über den echten Query-Service auf Testzeilen an und belegt beides: Die Zeilen aus `:218` und `:998` bleiben drin, die Zeilen aus `:540` und `:1010` fallen heraus. Zusätzlich sucht er die beiden Phrasen in den Quelltexten, damit eine Umformulierung den Test rot macht und nicht den Seed still verbiegt.

Ein Test in Infrastructure.Tests parst alle vier Seeds und belegt, dass jede genannte Kategorie im Code existiert, über die Namensräume aus §2. Das schützt vor stillem Verrotten bei Umbenennungen.

## 8. Endpunkte

Alle neuen Routen liegen in der Gruppe `/api/admin` und erben `RequireAuthorization` und `GlobalAdminAuthorizationFilter`. Die Handler sind dünn und delegieren an Core-Interfaces mit Implementierung in Infrastructure: `ILogQueryService` (Abfragen) und `ILogFilterService` (gespeicherte Filter). Kein `AppDbContext` und kein `IConnectionMultiplexer` im Handler (Regel 4). Die Lesepfade laufen über `AppDbContext` und dürfen das, weil sie nicht im Schreiber laufen (§5.4). Zeiten sind ISO-8601-UTC.

### 8.1 Übersicht

| Route | Parameter | Antwort | Fehler |
|---|---|---|---|
| `GET /api/admin/logs` | `q`, Zeitraum (§8.2), `before` (Cursor, optional), `limit` (Default 100, höchstens 200) | `{ entries: [Eintrag], nextCursor: string\|null, rangeFromUtc, rangeToUtc }`, sortiert neueste zuerst nach `(TimestampUtc DESC, Id DESC)` | `400 invalid_log_filter` (§7.3), `400 invalid_log_range`, `503` (§8.4) |
| `GET /api/admin/logs/{id}` | — | Eintrag plus `exception`, `processStartedUtc`, `uptimeSeconds` | `404 log_entry_not_found` (z. B. inzwischen gelöscht) |
| `GET /api/admin/logs/summary` | `q`, Zeitraum | `{ rangeFromUtc, rangeToUtc, bucketSeconds, bucketStartsUtc: [...], counts: { info: [...], warn: [...], error: [...] }, total, facets: { source: [{value,count}], level: [{value,count}], category: [{value, short, count}], categoryOther: n }, dropped: { total, queueFull, budget, writeFailed, captureFailed }, processStarts: [{source, instanceId, startedUtc}] }` | wie `GET /logs` |
| `GET /api/admin/logs/status` | — | `{ generatedAtUtc, errorsLast24h, sources: [{ source, instanceId, startedUtc, lastFlushUtc, stale }] }` | — |
| `GET /api/admin/log-filters` | — | `[{ id, name, filter, sortOrder }]` nach `sortOrder` | — |
| `POST /api/admin/log-filters` | `{ name, filter }` | `201` + Filter | `400 invalid_log_filter_name` (leer oder > 60 Zeichen), `400 invalid_log_filter` (gleicher Parser), `409 log_filter_name_taken`, `409 log_filter_limit_reached` (höchstens 30) |
| `PUT /api/admin/log-filters/{id}` | `{ name, filter }` | `200` + Filter | wie `POST`, dazu `404 log_filter_not_found` |
| `DELETE /api/admin/log-filters/{id}` | — | `204` | `404 log_filter_not_found` |

**Eintrag in Listen:** `{ id, timestampUtc, level: "info"|"warn"|"error"|"crit", source: "api"|"worker", instanceId, category, eventId?, eventName?, message, hasException, exceptionType?, properties: { … }, truncated }`.

**Entscheidung des Autors (offen für Einspruch), A18:** Der Exception-Text steht **nur** in `GET /logs/{id}`. Bei 100 Einträgen mit vollen 16-KiB-Exceptions würde eine Seite sonst rund 3 MB groß.

**Entscheidung des Autors (offen für Einspruch), A19:** Summary und Seite sind **getrennte** Aufrufe. Die Seite paginiert, die Summary nicht, und im Live-Modus laufen beide in verschiedenem Takt (§8.3).

**Facetten:** Gezählt wird über die volle gefilterte Menge im Zeitraum. Die Kategorie-Facette zeigt die Top 15 und nennt den Rest in `categoryOther`. `short` ist das letzte Kategoriesegment.

**Drops:** `dropped` summiert die Properties der synthetischen `EmotePurge.LogCapture`-Einträge **im Zeitraum, unabhängig vom Filter**. Ein Filter darf verlorene Einträge nicht verstecken.

**Prozessstarts:** `processStarts` sind die `ProcessStartedUtc` aller Instanzen, deren Start im Zeitraum liegt. Sie werden als Marken im Histogramm gezeigt.

**Neue `ApiErrorCodes`:** `invalid_log_filter`, `invalid_log_range`, `invalid_log_filter_name`, `log_filter_name_taken`, `log_filter_limit_reached`, `log_filter_not_found`, `log_entry_not_found`, `log_query_timeout`. Jeder bekommt seinen Eintrag in `web/src/app/core/i18n/api-error.ts` und in beiden Locale-Dateien (Regel 7; `api-error-locales.spec.ts` prüft die hinteren beiden Schritte).

### 8.2 Zeitraum und Buckets

- **Parameter:** entweder `range` = `1h` | `6h` | `24h` | `7d` (Default `24h`; das Ende ist die Anfragezeit) **oder** `from` + `to`, beide ISO-UTC, für einen absoluten Bereich. `from < to`, höchstens 8 Tage Spanne, `to` höchstens 1 h in der Zukunft. Gemischte oder ungültige Angaben ergeben `400 invalid_log_range`.

  **Entscheidung des Autors (offen für Einspruch), A20:** Die Voreinstellung ist 24 h, umschaltbar bis 7 Tage. Diese Werte hat der Autor im Chat vorgeschlagen.
- **Buckets:** Der Server wählt aus der festen Leiter 10 s, 30 s, 1, 2, 5, 10, 15, 30 min, 1, 2, 3, 6, 12 h die kleinste Breite mit **höchstens 120 Buckets über die nominelle Spanne**. Die Breite hängt damit nur von der Spanne ab, nie von der Uhrzeit der Anfrage. Daraus folgt `1h` → 1 min (60), `6h` → 5 min (72), `24h` → 15 min (96), `7d` → 2 h (84). Ein Klick auf einen Balken setzt `from`/`to` auf diesen Bucket; der neue Bereich wird mit derselben Regel feiner gebucketet. Das Prinzip ist dasselbe wie in #381 §5.2, nur mit eigener Obergrenze.
- **Einrasten der Grenzen —** **Entscheidung des Orchestrators (Builder-Check), O4:** Der Server rastet den Bereich auf Bucket-Grenzen (UTC-Vielfache der Breite) ein, bevor er abfragt. **Relativ** (`range`): Ende = Anfragezeit **aufgerundet** auf die nächste Grenze (liegt sie schon auf einer, bleibt sie), Start = Ende − nominelle Spanne. Weil jede nominelle Spanne ein Vielfaches ihrer Breite ist, ergibt das immer genau die Bucketzahl oben. **Absolut** (`from`/`to`): Breite aus der Spanne `to − from`, Ende = `to` aufgerundet, Start = Ende − k × Breite mit k = ⌈(`to` − `from`) / Breite⌉, sodass `from` abgedeckt bleibt. `rangeFromUtc`/`rangeToUtc` in jeder Antwort (Seite **und** Summary) sind die **eingerasteten** Grenzen. Der letzte Bucket reicht damit in die Zukunft und ist unvollständig; das Histogramm kennzeichnet ihn so. Das Einrasten ist **idempotent**: Schon eingerastete Grenzen als absolute `from`/`to` zurückgeschickt ergeben exakt denselben Bereich und dieselbe Breite. Darauf baut das Nachladen in §8.3.
- `counts` trennt Information, Warning und Error+Critical je Bucket. Leere Buckets sind 0 und nicht `null`, denn eine Zählung kennt keine Unbekannten.

### 8.3 Paginierung und Live-Abruf

- **Cursor:** opak, kodiert `(TimestampUtc, Id)` des letzten Eintrags. Die Folgeseite liest Keyset-basiert `(TimestampUtc, Id) < Cursor` im selben Zeitraum. Bei relativem `range` friert der Client den **eingerasteten** Bereich der ersten Antwort (`rangeFromUtc`/`rangeToUtc`, O4) ein und schickt ihn beim Nachladen als absolutes `from`/`to` mit. Wegen der Idempotenz des Einrastens bleibt er zwischen den Seiten gleich. Sonst verschöbe sich der Bereich.
- **Live-Abruf. Entscheidung des Autors (offen für Einspruch), A21:** Auf `logs.appended` lädt der Client gedrosselt (höchstens alle 2 s) die **Kopfseite** neu: dieselbe Abfrage ohne `before`, `limit=100`. Er führt sie **per `id`** mit dem Bestand zusammen. Ein `afterId`-Verfahren ist verworfen: Api und Worker schreiben gleichzeitig, und eine kleinere Id kann nach einer größeren committen. Ein Hochwasserzeichen auf `Id` verlöre solche Einträge still. Die Kopfseite fängt auch verspätete Einträge mit älterem Zeitstempel, solange sie unter den neuesten 100 sind. Teilt die neue Kopfseite **keinen** Eintrag mit dem bisherigen Kopf, sind mehr als 100 neue gekommen; die Seite bietet dann „Mehr als 100 neue Einträge — Liste neu laden“ an, statt eine Lücke zu verschweigen.
- Die **Summary** wird im Live-Modus höchstens alle 15 s neu geladen.
- **Live gibt es nur bei relativem `range`.** Bei einem absoluten Bereich lädt die Seite nicht nach, und der Live-Hinweis sagt das.
- Öffnet sich der Admin-Stream neu (Reconnect nach `MaxConnectionLifetime` oder nach einem Fehler), lädt die Seite den Kopf einmal nach.

### 8.4 Last- und Missbrauchsgrenzen

Die Gruppe hat bewusst keine Rate-Limit-Policy (§2.3), und das bleibt so. Stattdessen gilt **Entscheidung des Autors (offen für Einspruch), A22:**

- **Jede Log-Abfrage hat ein Zeitlimit von 5 s** (Command-Timeout bzw. `SET LOCAL statement_timeout`). Bei Überschreitung antwortet die Api `503 { errorCode: "log_query_timeout" }`. Das Frontend sagt dann: „Abfrage zu aufwendig — Zeitraum oder Filter eingrenzen“.
- **Höchstens 2 Log-Abfragen gleichzeitig je Api-Instanz,** gezählt über `logs`, `logs/summary` und `logs/{id}`. Eine dritte wartet höchstens 5 s und bekommt dann `503` mit `Retry-After: 5` ohne Body, wie bei `/dashboard/load` in #381. Zwei, weil ein Seitenaufruf Seite und Summary parallel lädt. Mehr Admins gibt es nicht.
- `GET /logs/status` ist über das `TtlCache`-Muster aus #381 **30 s** gecacht (wie `SnapshotTtl`), mit Single-Flight (O2, §11). `TtlCache<T>` wird dafür aus `AdminDashboardReader` in eine eigene `internal`-Klasse in Infrastructure herausgezogen; Verhalten und Tests bleiben. Seite und Summary werden nicht gecacht, denn sie hängen am Filter.

## 9. Oberfläche `/admin/logs`

Neue Route unter `/admin` (Kind von `AdminLayout`, hinter `adminGuard`). Neuer Tab „Logs“ (de und en gleich) zwischen „Monitoring“ und „Kanäle“. Die Komponenten liegen in `features/admin/`; Service und Modelle in `core/admin/` (z. B. `admin-logs.service.ts`), die Stringwerkzeuge des Filters ebenfalls in `core/admin/` mit Spec.

### 9.1 URL-Zustand

Die URL trägt `?q=<Filterstring>&range=24h` bzw. `?q=…&from=…&to=…`. Sie läuft über `listQueryState`. Die Defaults werden aus der URL entfernt, und Filteränderungen ersetzen den History-Eintrag (`replaceUrl`), wie es `listQueryState` für alle Listen festlegt.

**Entscheidung des Autors (offen für Einspruch), A23:** Der Filter wird **beim Abschicken angewandt** (Enter oder Knopf „Anwenden“), nicht entprellt beim Tippen. Ein halb getipptes `level:` erzeugte sonst bei jedem Tastendruck einen Fehler.

Facetten-, Chip- und Balken-Klicks wenden sofort an. Nach dem Eingrenzen per Balken erscheint neben der Zeitwahl „Zurück zu 24 h“ (bzw. dem vorigen relativen Bereich), denn die Zurück-Taste springt wegen `replaceUrl` nicht dorthin.

Ein Beispiel-Link, wie Claude ihn verschickt: `/admin/logs?q=level%3E%3Dwarn%20cat%3ATwitchChatManager&range=7d`.

### 9.2 Aufbau auf dem Desktop (Variante B)

Von oben nach unten, alles innerhalb der einen Inhaltsbreite (`docs/UI-Designsprache.md` §8.4a):

1. **Filterleiste, sticky** (`app-sticky-bar top-24 py-2`, Designsprache §8.5). Sie enthält das Filterfeld mit sichtbarem Label (Designsprache §5.2), „Anwenden“, die Zeitwahl als `SegmentedControl` (1 h · 6 h · 24 h · 7 Tage) und den Live-Status (`HealthMarker` wie auf der Monitoring-Seite). Am Ende der Zeile ist ein **fest reservierter Platz** für den Hinweis „n neue“ (§9.4). Ein Filterfehler erscheint nach dem Feldfehler-Muster (Designsprache §5.3): `aria-invalid`, eine Meldung über `aria-describedby` und die Fehlerstelle im Feld markiert (Selektion auf `position`/`length`).
2. **Chip-Leiste** der gespeicherten Filter, danach „+ Filter speichern“. Ein Chip ist aktiv, wenn sein Filter nach Whitespace-Normalisierung gleich `q` ist. Sie ist nicht sticky und darf umbrechen.
3. **Zeitachse:** neues Histogramm-Primitiv (§9.6) über die volle Breite, 96 px hoch, mit Prozessstart-Marken und der Beschriftung „Zeiten in UTC“.
4. **Facetten und Liste:** Ab `lg` stehen links die Facetten in einer schmalen Spalte (~14 rem), rechts die Liste. Facetten gibt es für Quelle, Level und Kategorie mit Anzahl. Jeder Wert trägt zwei Aktionen: „hinzufügen“ (Klick auf den Wert, hängt `key:wert` an) und „ausschließen“ (eigener kleiner Knopf, hängt `-key:wert` an). Beide sind tastaturbedienbar und mit zugänglichem Namen versehen. Die Kategorie-Facette zeigt den Kurznamen, im `title` den vollen. Als Filterwert nimmt sie den Kurznamen, wenn er unter den Facettenwerten eindeutig ist, sonst den vollen Namen.
5. **Liste,** neueste zuerst, dichte einzeilige Zeilen nach Designsprache §2.1 (gestrichene Zeilen, keine Karten). Spalten: Zeit `HH:mm:ss.fff` in UTC; Level als Kürzel mit Ton (Information neutral, Warning `warning`, Error und Critical `danger`; Text plus Farbe, nie Farbe allein); Quelle (`api`/`worker`); Kategorie-Kurzname; Message einzeilig mit Ellipse. Wechselt das UTC-Datum zwischen zwei Zeilen, steht eine **Tagestrennzeile** dazwischen. Ein Klick klappt die Details auf (Disclosure: `button` mit `aria-expanded`/`aria-controls`).
   - **Details enthalten:** vollen Kategorienamen, Event (Name/Id), Instanz und „n min nach Start“, alle Properties als Definitionsliste mit je „filtern“ und „ausschließen“ (hängt `name:wert` bzw. `-name:wert` an), die Exception aus `GET /logs/{id}` (lazy, mit Skeleton) in einem `pre` mit Zeilenumbruch, dazu „Kopieren“.
   - **Kopieren. Entscheidung des Autors (offen für Einspruch), A24:** Je Eintrag kopiert „Kopieren“ eine Textzeile im Stil `docker logs` plus Exception. Über der Liste kopiert „Sichtbare kopieren“ höchstens die geladenen 500 Zeilen als Text. Begründung: Heute fügt der Betreiber Terminal-Ausgaben bei Claude ein. Dieser Weg bleibt, nur ohne Shell.
6. **Nachladen beim Scrollen:** Ein Sentinel lädt die nächsten 100.

   **Entscheidung des Autors (offen für Einspruch), A25:** Ab **2.000** geladenen Zeilen stoppt das Nachladen mit dem Hinweis „Zeitraum oder Filter eingrenzen“. Grund sind DOM-Größe und Handyspeicher.

### 9.3 Telefon (B9)

- Die Filterleiste bleibt sticky mit Filterfeld und einem Knopf **„Filter“**. Der öffnet einen Dialog mit Zeitwahl, Chips und Facetten; auf grobem Zeiger ist das automatisch das Bottom-Sheet aus Designsprache §7. Der Umschaltpunkt ist die Breite (`lg`), nicht der Zeigertyp.
- Die Zeitachse wird ein **schmaler Streifen** (32 px) ohne Achsenbeschriftung außer Anfang und Ende. Ein Tippen auf einen Balken grenzt ein wie auf dem Desktop.
- Listenzeilen zeigen nur Zeit, Level und Message. Quelle und Kategorie stehen in den Details. Details öffnen sich auf Tippen inline.

  **Entscheidung des Autors (offen für Einspruch), A26:** Inline statt Sheet, gleich wie auf dem Desktop. Ein Sheet je Eintrag nähme den Listenkontext weg.
- Ziel-Touchgrößen nach Designsprache §10 (44 px).
- **Kein horizontales Scrollen der Seite.** Lange Tokens im `pre` brechen um (`break-words`).

### 9.4 Live, ohne Sprünge (B8)

- **„Oben“** heißt: die erste Listenzeile liegt im sichtbaren Bereich unter den Sticky-Leisten, **und** keine Zeile ist aufgeklappt.
- **Steht der Nutzer oben,** werden neue Einträge an ihrer sortierten Position eingefügt und kurz hervorgehoben: Hintergrund mit Akzent-Wash, der über ~2 s ausblendet, bei `prefers-reduced-motion` ohne Animation, nur für 2 s markiert. Ein Screenreader bekommt **eine** höfliche Live-Meldung je Nachladen („3 neue Einträge“), nicht je Eintrag.
- **Hat der Nutzer gescrollt oder eine Zeile aufgeklappt,** fügt die Seite **nichts** in die Liste ein. Der reservierte Platz in der Filterleiste zeigt dann „n neue ↑“. Ein Klick scrollt nach oben und fügt ein. Der Platz ist auch bei n = 0 reserviert (unsichtbar), damit die Leiste nicht springt (Regel „keine Layout-Sprünge“).
- Ist der Stream geschlossen, zeigt der Live-Status das wie auf der Monitoring-Seite, und es gibt ein „Neu laden“. Es gibt **kein** Polling-Fallback, wie im ganzen `LiveUpdateService`.

### 9.5 Gespeicherte Filter (B2)

- „+ Filter speichern“ öffnet einen Dialog (`DialogShell`, eigene `open…Dialog`-Funktion) mit Name und dem aktuellen Filter (bearbeitbar). Er ist gesperrt bei leerem Filter, mit Begründung als Text neben dem Knopf (Designsprache §7). Server-Fehler werden als Feldfehler gezeigt (`log_filter_name_taken` am Namen, `invalid_log_filter` am Filter).
- **Jeder Chip** hat ein kleines Menü (`app-popover`) mit „Bearbeiten“ (derselbe Dialog, vorbefüllt), „Mit aktuellem Filter überschreiben“ und „Löschen“. Löschen geht über `ConfirmDialog`, denn ein vorbelegter Filter kommt nicht von selbst zurück (A13).
- Ein Chip-Klick **ersetzt** `q` durch den Chip-Filter und behält den Zeitraum.
- Ist ein gespeicherter Filter nach einer späteren Grammatikänderung ungültig, bleibt der Chip anklickbar. Die Seite zeigt dann den Filterfehler (§7.3).

### 9.6 Neues Primitiv: Histogramm

**Entscheidung des Autors (offen für Einspruch), A27:** `shared/charts/` bekommt ein zweites Primitiv `app-time-histogram`, inline SVG ohne Bibliothek, gemeinsame Geometrie mit `chart-series.ts`, wo es passt. `docs/UI-Designsprache.md` §2.6 wird um einen Unterabschnitt erweitert, denn heute nennt §2.6 `app-line-chart` „the app's one chart primitive“; dazu ein DECISIONS-Eintrag.

Regeln:

- **Ein gestapelter Balken je Bucket** mit drei Segmenten von unten nach oben: Information neutral, Warning im Warnton, Error und Critical in `danger`. Fehler sind echte Fehler, `danger` ist also nach §2.6 zulässig. Die Segmente unterscheidet zusätzlich ein Muster (Warning schraffiert), damit nichts allein an der Farbe hängt.
- **Ganzzahlige y-Achse** (`count`).
- Prozessstarts sind dünne senkrechte Marken mit Quelle im Lesecursor.
- Bei einem Bereich ganz ohne Einträge zeichnet das Histogramm die Achsen und den Text „keine Einträge in diesem Zeitraum“, damit das Layout nicht springt.
- **Lesecursor wie beim Linienchart:** Pfeiltasten bewegen ihn, eine `aria-live`-Zeile nennt Bucketzeit und die drei Zahlen. **Enter** grenzt auf den Bucket ein. Ein Pointer-Klick tut dasselbe.
- **Textalternative** (`role="img"`): Summe, Maximum, Zahl der Warning- und Error-Buckets.

### 9.7 Zustände

- **Erstes Laden:** Skeletons in der Form von Histogramm, Facetten und Zeilen (Designsprache §6.1).
- **Folgeladen:** Die letzten Werte bleiben stehen.
- **Leer:** `<app-empty-state>` „Keine Einträge für diesen Filter im Zeitraum“, mit Angebot, den Zeitraum auf 7 Tage zu erweitern, wenn er kürzer ist.
- **Drops im Zeitraum:** `NoticeBanner` (`warning`) über der Liste: „Im Zeitraum wurden n Einträge nicht gespeichert (Puffer voll: …, Tagesbudget: …, Schreibfehler: …)“, dazu ein Link „Anzeigen“ auf `cat:LogCapture`.
- **Erfassung steht** (`status.sources[].stale`, §11): `NoticeBanner` (`warning`) „Die Log-Erfassung von {source} hat seit {hh:mm} UTC nichts geschrieben — neuere Einträge fehlen hier, `docker logs` hat sie“.
- **Abfragefehler:** `invalid_log_filter` am Feld (§9.2); `log_query_timeout` und `503` als `NoticeBanner` (`error`) im Listenbereich. Die letzte gute Antwort bleibt dabei stehen und wird als veraltet markiert, wie bei #381.

### 9.8 Barrierefreiheit und i18n

- Alle Texte laufen über Transloco unter `admin.logs.*` (de/en), die Filter-Fehlergründe unter `admin.logs.filterErrors.*`.
- Die Filtersprache bekommt eine kurze, aufklappbare Hilfe (Popover „?“) mit den Schlüsseln aus §7.2 und drei Beispielen.
- Log-**Inhalte** werden nicht übersetzt, denn sie sind Daten.
- Die Seite durchläuft den UI-Audit-Harness (Designsprache §12) auf kleinem und großem Viewport in beiden Themes, mit geöffnetem Facetten-Sheet und aufgeklappter Zeile als `afterLoad`-Zustand.

## 10. Rollen- und Berechtigungsgrenzen

- Serverseitig ist alles admin-only durch die Gruppe (§8), clientseitig durch `adminGuard` und den Tab. Ein Nicht-Admin bekommt `403`, ein Abgemeldeter `401`, auf jeder neuen Route. Die Filtermatrix wird getestet (§13).
- Es gibt keine Route, die Logs schreibt oder löscht. Löschen ist allein Sache der Retention.

## 11. Zeile auf `/admin/monitoring` (B5)

**Entscheidung des Autors (offen für Einspruch), A28:** Die „Kachel“ wird nach Designsprache §2.1 als **eine kompakte, gestrichene Zeile direkt unter dem Seitenkopf** umgesetzt, vor dem Last-Bereich, und nicht als Karte. Die feste Bereichsreihenfolge aus #381 §10 bleibt dahinter unverändert.

Inhalt:

- „Fehler der letzten 24 h: **n**“ und „Ansehen →“. Der Link führt auf `/admin/logs?q=level%3E%3Derror&range=24h`, also exakt den Seed „Alle Fehler“.
- Bei n = 0 neutral „keine“, bei n > 0 `StatusBadge tone="danger"` mit der Zahl.
- Ist `stale` für eine Quelle gesetzt, steht in **derselben** Zeile im Ton `warning`: „Log-Erfassung {source} steht seit {hh:mm} UTC“. Dann ist die Fehlerzahl nicht vertrauenswürdig und wird als „unvollständig“ markiert, statt still 0 zu zeigen.
- **Daten** kommen aus `GET /api/admin/logs/status`: Cache **30 s**. Der Refresh hängt am bestehenden 60-s-Timer der Seite, der bei verstecktem Tab pausiert.

  **Entscheidung des Orchestrators (Builder-Check), O2:** 30-s-Cache wie `SnapshotTtl` in #381, 60-s-Timer der Seite bleibt. Daraus folgen die ehrlichen Obergrenzen bei sichtbarem Tab. Ein neuer Fehler ist **≤ 90 s** nach dem Commit seines Batches sichtbar (30 s Cache + 60 s Timer; der Batch selbst kommt höchstens 1 s nach dem `Log`-Aufruf). „Erfassung steht“ erscheint **≤ 5 min + 90 s** nach dem letzten Herzschlag (5-min-Schwelle + Cache + Timer). AK 14 prüft genau diese Grenzen.

  **Entscheidung des Autors (offen für Einspruch), A29:** Das ist das Cache- und Poll-Muster aus #381, und die Zeile reagiert **nicht** auf `logs.appended`. Eine Fehlerzahl je Minute genügt dem Zweck „bemerken“.
- **`stale`** heißt: Die jüngste Instanz der Quelle hat `LastFlushUtc` älter als **5 min** (Herzschlag 60 s, §5.5).
- **Laden:** Skeleton in Zeilenhöhe, kein Sprung. **Fehler:** „nicht verfügbar“ in der Zeile; die übrigen Bereiche der Seite sind unabhängig.

## 12. Datenschutz

Die Datenschutzerklärung ist Markdown des Betreibers außerhalb des Repos (`Legal:ContentPath`, `docs/Operations.md` „Legal pages“). Diese Spec liefert nur den Vorschlag. Der Betreiber trägt ihn vor dem Prod-Einschalten in `privacy.de.md` und `privacy.en.md` ein, im Abschnitt zur Server-Verarbeitung neben den bestehenden Fristen.

**Deutsch (maßgeblich):**

> **Betriebsprotokolle.** Damit wir Störungen erkennen und beheben können, speichert Emote Purge technische Protokolleinträge der eigenen Server-Anwendung – etwa Verbindungsabbrüche, Ergebnisse der Emote-Synchronisation und Fehlermeldungen – sieben Tage lang in seiner Datenbank. Diese Einträge können Twitch-Kanal- und Anmeldenamen, numerische Twitch-Nutzerkennungen und die aufgerufenen Pfade der Anwendung enthalten, aber keine Chatnachrichten und keine Namen von Personen, die im Chat schreiben. Einsehen können sie nur die Betreiber. Nach sieben Tagen werden die Einträge automatisch gelöscht; sie sind nicht Teil unserer Datensicherungen. Löschst du dein Konto, laufen Einträge mit deiner Kennung mit derselben Frist aus. Rechtsgrundlage ist unser berechtigtes Interesse an einem sicheren und funktionsfähigen Betrieb (Art. 6 Abs. 1 lit. f DSGVO).

**Englisch:**

> **Operational logs.** To detect and fix faults, Emote Purge stores technical log entries of its own server application – such as connection drops, emote synchronisation results and error messages – in its database for seven days. These entries may contain Twitch channel and login names, numeric Twitch user IDs and the paths requested within the application, but no chat messages and no names of people writing in chat. Only the operators can view them. Entries are deleted automatically after seven days and are not part of our backups. If you delete your account, entries containing your ID expire on the same schedule. The legal basis is our legitimate interest in a secure and working service (Art. 6(1)(f) GDPR).

Die Aussage „nicht Teil unserer Datensicherungen“ setzt A14 voraus. Wird A14 abgelehnt, muss der Satz stattdessen auf die bestehende Backup-Aussage (bis zu 90 Tage) verweisen.

## 13. Tests und Liefergates

### 13.1 Backend (Regel 11)

- **`tests/EmotePurge.Infrastructure.Tests/Unit`, ohne Container:**
  - **Parser:** jeder Schlüssel und Operator aus §7.2, Kommalisten, Negation, Zitate mit `\"`, Wildcards inklusive Maskierung von `%`/`_`/`\`, jede `reason` aus §7.3 mit genauer `position`/`length` (auch nach Mehrbyte-Zeichen und Emoji, gemessen in UTF-16), Grenzen und Stolpersteine (`http://x` vs. `"http://x"`, `Fehler:`).
  - **Seeds:** Alle vier parsen; jede genannte Kategorie existiert im Code; für „Twitch-Reconnects“ der Treffer-/Nicht-Treffer-Test aus §7.5 (O1).
  - **Bucket-Leiter und Einrasten (O4):** je Preset Breite und Zahl unabhängig von der Anfragezeit. Beispiele: `24h` um 12:07:30 → `rangeToUtc` 12:15:00, `rangeFromUtc` 12:15:00 des Vortags, 15 min, 96 Buckets; um genau 12:15:00 → dieselben Grenzen; `1h` um 12:00:01 → 12:01:00 / 11:01:00, 60 Buckets; `7d` um Di 13:30 → Di 14:00 / Di 14:00 der Vorwoche, 2 h, 84 Buckets; `6h` um 23:58 → 00:00 / 18:00, 72 Buckets. Absolut: `from` 10:03, `to` 10:17 (14 min) → 10 s, Ende 10:17:00, Start 10:03:00, 84 Buckets; Klick auf einen 15-min-Bucket 10:00–10:15 → 10 s, 90 Buckets. Idempotenz: Eingerastete Grenzen zurückgeschickt ergeben identische Grenzen und Breite. Klick-Eingrenzung bis 10 s.
  - **Sink:** Untergrenze und Ausschlussregeln inklusive Provider-Alias, der anheben, aber nicht senken darf; Kürzungen und `Truncated`; Property-Normalisierung (kleingeschrieben, ISO-Zeit, Aufzählungen, 16er-Grenze, 4 KiB); Scope-Übernahme nur `RequestPath`/`TraceId`; Scrubber-Muster und Nicht-Treffer (normale Diagnose-Texte bleiben unverändert).
  - **Nicht-Blockieren:** Bei vollem Puffer kehrt `Log()` ohne Wartezeit zurück und zählt `queue_full`; die Byte-Grenze greift vor der Zählgrenze; Budget mit 80/20-Aufteilung und Mitternachtsreset über `FakeTimeProvider`; eine Ausnahme im Formatter wird `capture_failed` und nicht geworfen.
  - **`AsyncLocal`-Sperre:** Ein im Schreiberkontext geloggter Eintrag wird verworfen und nicht gezählt; derselbe Aufruf außerhalb wird erfasst.
- **`tests/EmotePurge.Infrastructure.Tests/Integration`, Testcontainers-Postgres:**
  - **Schreiber:** Batch landet vollständig; Postgres-Ausfall (Container pausieren) führt zu `write_failed`/`queue_full` und zu **keiner** Verzögerung eines parallel loggenden Aufrufers (gemessen: `Log()` p99 unter 1 ms während des Ausfalls); nach der Rückkehr kommt zuerst der synthetische Drop-Eintrag mit korrekter Aufschlüsselung, dann der Puffer; Herzschlag ohne Einträge aktualisiert `LastFlushUtc`; Shutdown leert den Puffer innerhalb von 3 s.
  - **Kein Schreiber-SQL in den Logs:** Weder ein EF- noch ein Npgsql-Eintrag des Schreibpfads landet in `LogEntries` oder im erfassenden Test-Provider. Das gilt mit einem Konfigurationstest, der `Microsoft.EntityFrameworkCore` und `Npgsql` absichtlich auf Debug stellt.
  - **Query-Service:** jede Filterart gegen gesetzte Zeilen; Keyset-Paginierung ohne Lücken und Doppelte bei gleichen Zeitstempeln; eingefrorenes `to` beim Nachladen; `instance:latest` mit zwei Instanzen je Quelle; `uptime`; Summary (Bucket-Zählung je Levelklasse, Facetten Top 15 plus Rest, `dropped` unabhängig vom Filter, `processStarts`); `status` mit `stale`; 5-s-Zeitlimit liefert `log_query_timeout` (z. B. durch `pg_sleep` in einem Test-Hook).
  - **Retention:** Ein Eintrag knapp unter 7 Tagen bleibt, knapp darüber geht; Trockenlauf zählt nur; ein Fehler der Kategorie setzt `LogEntriesFailed` und stoppt die übrigen Kategorien nicht; `LogCaptureStatus` folgt `LastFlushUtc`; `LogFilters` bleiben.
  - **Gespeicherte Filter:** CRUD, Namensunikat ohne Groß-/Kleinschreibung, Obergrenze 30, Validierung über den Parser.
- **`tests/EmotePurge.Worker.Tests`:**
  - T0 aus §4.3.
  - Harness-Registrierung ohne Sink und Schreiber (§5.1).
  - Hosted-Service-Reihenfolge: Der Schreiber steht vorn.
- **`tests/EmotePurge.Api.Tests`:**
  - Jede neue Route gibt `401` ohne Session und `403` für einen Nicht-Admin.
  - Der `400`-Vertrag (`invalid_log_filter` mit `reason`/`position`/`length`, `invalid_log_range`, `invalid_log_filter_name`) an den echten Routen, mit substituiertem `ILogQueryService`/`ILogFilterService`; `503` mit `Retry-After` beim Parallelitäts-Gate.
  - Ein Stream-Vertragstest: `logs.appended` steht in `AdminTypes`, nicht in `ChannelTypes`.
  - Die Factory setzt `LogCapture:Enabled=false`.
  - Handler bekommen keine eigenen Tests (Regel 11).

### 13.2 Frontend (Regel 12)

- **Vitest:**
  - `core/admin` Service mit `HttpTestingController`: Parameter, Cursor, eingefrorener eingerasteter Bereich als `from`/`to` (O4).
  - Filter-Stringwerkzeuge: Quoten, Anhängen ohne Dublette, Fehlerposition.
  - Zusammenführen der Kopfseite: Einfügen nach `(Zeit, Id)`, Dedupe per `id`, Erkennung „> 100 neu“.
  - Entscheidung „oben vs. gelesen“ inklusive aufgeklappter Zeile.
  - Chip-Aktiv-Erkennung mit Whitespace-Normalisierung.
  - Kurzname-Wahl der Kategorie-Facette.
  - Histogramm-Klick auf `from`/`to`.
  - Zeile auf `/admin/monitoring`: `stale` macht die Zahl „unvollständig“, n = 0 und n > 0.
  - Accessibility-Semantik (Disclosure, zugängliche Namen der Facetten-Aktionen, Live-Meldung einmal je Nachladen). Keine CSS- oder Vorlagen-Assertions.
- **Playwright** mit gemocktem `/api/**` und `installLiveStub`/`__emitLive`:
  - Deep-Link `?q=…` öffnet vorgefiltert.
  - Ungültiger Filter markiert die Stelle.
  - Chip-Klick ersetzt `q`.
  - Speichern, Bearbeiten und Löschen eines Filters.
  - Facette hinzufügen und ausschließen.
  - Balken-Klick grenzt ein, „Zurück zu 24 h“.
  - Scroll-Nachladen bis zur 2.000er-Grenze mit Hinweis.
  - `logs.appended` oben fügt ein und hebt hervor; nach Scrollen zeigt es „n neue“ ohne Verschiebung des sichtbaren Inhalts (Position eines Ankerelements vor und nach dem Ereignis gleich).
  - „> 100 neue“.
  - Drop-Banner, Stale-Banner, Timeout-Banner.
  - Die Monitoring-Zeile verlinkt auf den Seed.
  - **Mobiler Viewport:** Facetten hinter „Filter“, schmaler Streifen, kein horizontales Scrollen (Dokumentbreite ≤ Viewport).
  - Zeitgesteuerte Fälle über `page.clock` (`install()` vor `goto`, `runFor`).
- **UI-Audit-Harness** für `/admin/logs` und die geänderte Monitoring-Seite, beide Themes, beide Viewports; Axe WCAG AA.

### 13.3 Performance-Gate (vor dem Merge von T3)

Auf Wegwerf-Postgres 16 Alpine mit Migrationsschema, synthetisch **320.000 Zeilen** über 8 Tage (Budgetgrenze aus A7). Verteilung nach §2.6: 90 % Information, 9 % Warning, 1 % Error; Properties bei 60 % der Zeilen; Exceptions bei 2 %. Gemessen werden `EXPLAIN (ANALYZE, BUFFERS)` und die Ende-zu-Ende-Antwort der Api, warm:

| Abfrage | Grenze |
|---|---|
| Kopfseite 24 h und 7 Tage, ohne Filter, mit `level>=warn`, mit `cat:SevenTv*`, mit Freitext, mit `channel:x`, mit Negation | ≤ 200 ms |
| Summary 24 h | ≤ 300 ms |
| Summary 7 Tage mit Freitext | ≤ 800 ms |
| `status` (Fehlerzahl 24 h, `instance:latest`) | ≤ 50 ms |

Reißt eine Grenze, gibt es zwei Wege: eine Summary-Abfrage mit gemeinsamer Teilmenge (CTE oder `GROUPING SETS`) oder `pg_trgm` nach Betreiberentscheidung (§6.1). Die Grenzen bleiben. Bei realem Volumen (~25k Zeilen) wird einmal mitgemessen und protokolliert.

### 13.4 Gates

`dotnet test EmotePurge.slnx`, `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e` (ohne Api auf `:5151`) und `node scripts/coverage-local.mjs` ≥ 80 % auf neuem Code; `dotnet format`, `npm run lint` und `npm run format` sauber. Danach die Live-Verifikation (§15).

## 14. Akzeptanzkriterien (bestanden/nicht bestanden)

1. **Lärm (T0):** Nach T0 schreibt die lokale Api bei einem Seitenaufruf keine `Microsoft.EntityFrameworkCore.Database.Command`-Zeile in die Console. Der lokale Worker an einem Kanal mit Sub- oder Watch-Streak-Ereignissen schreibt keine `Unaccounted for … command=USERNOTICE` mehr. Ein injiziertes unbekanntes NOTICE erscheint weiter als Warning (Worker.Tests).
2. **Erfassung:** Nach dem Start von Api und Worker stehen binnen 5 s die `Microsoft.Hosting.Lifetime`-Zeilen beider Prozesse in `LogEntries`, mit `Source`, `InstanceId` und `ProcessStartedUtc`.
3. **Untergrenze:** Auch mit `Logging__LogLevel__Default=Debug` landet keine Debug- oder Trace-Zeile und keine `Microsoft.EntityFrameworkCore.Database.Command`-Zeile unter Warning in `LogEntries`. Die Console zeigt sie.
4. **Redaction:** Ein im Test über den `RedactingTwitchClientLoggerFactory`-Pfad geloggtes `LogParsingError` mit präpariertem Rohtext (Chattext und `display-name=`) steht in `LogEntries` nur in der redigierten Form `command=…, tagKeys=[…]`, ohne Tag-Werte und ohne Chattext, und ohne Properties. **Mit Exception (O3):** Trägt der Aufruf eine Exception, deren `Message` und Inner-Exception-`Message` denselben präparierten Rohtext enthalten, kommt der Text weder in `Message`, `Exception` noch `ExceptionType` des Eintrags vor, noch in der Ausgabe eines parallel registrierten Test-Providers (Stellvertreter für die Console). `ExceptionType` ist der Originaltyp, und `Exception` enthält die Typen beider Glieder und mindestens einen Stackframe. Derselbe Fall mit umgangenem Wrapper (Exception direkt auf der Kategorie geloggt) speichert ebenfalls nur Typen und Stacks.
5. **Nicht-Blockieren:** Während Postgres gestoppt ist (`docker compose stop postgres`, 2 min), beantwortet die Api `GET /api/health` weiter. Der Worker zählt weiter Chatzeilen, soweit sein eigener Pfad ohne DB geht; der Flush puffert wie heute. Kein Prozess stürzt ab. Nach dem Start von Postgres erscheint binnen 60 s ein `EmotePurge.LogCapture`-Warning-Eintrag mit `dropped > 0`. Die Seite zeigt das Drop-Banner für diesen Zeitraum.
6. **Keine Rekursion:** Bei ruhigem System (kein Chat, kein Seitenaufruf) wächst `LogEntries` über 10 min um höchstens die normalen periodischen Zeilen (Live-Poll, Roster usw.) und nicht durch Einträge des Schreibers, seines SQL oder von `logs.appended`-Zustellungen. Gezählt wird nach Kategorie.
7. **Budget:** Eine Testschleife mit 25.000 Information-Zeilen je Prozess in einer Minute führt dazu, dass höchstens 16.000 Information-Zeilen dieses UTC-Tages gespeichert werden; Warnungen danach werden weiter gespeichert, bis 20.000 erreicht sind. Ein Drop-Eintrag nennt `budget`. Die Seite zeigt das Banner.
8. **Retention:** Mit `Retention:Enforce=true` löscht ein Lauf Einträge älter als 7 Tage und behält jüngere. Die Formatter-Zeile nennt `log entries deleted: n`. Im Trockenlauf wird nur gezählt.
9. **Backup:** Ein lokaler Lauf von `scripts/backup-postgres.sh` erzeugt einen Dump mit dem Schema von `LogEntries`, aber ohne `COPY`-Daten dafür.
10. **Filter:** Jeder Seed liefert lokal die erwarteten Zeilen. „Start nach Deploy“ zeigt nach `docker compose restart worker` die Boot-Recovery der neuen Instanz und keine Zeile der alten. „Alle Fehler“ zeigt nur Error/Critical. Ein ungültiger Filter liefert `400 invalid_log_filter` mit korrekter `position`, und die Seite markiert die Stelle.
11. **Deep-Link:** `/admin/logs?q=level%3E%3Dwarn&range=7d` öffnet die Seite mit genau diesem Filter und Zeitraum; Ändern und Anwenden schreibt die URL um.
12. **Live:** Mit offener Seite oben erscheint ein neu geloggter Warning-Eintrag binnen 5 s oben und hervorgehoben. Nach Scrollen um eine Bildschirmhöhe bleibt der sichtbare Inhalt stehen, und „1 neue“ erscheint.
13. **Gespeicherte Filter:** Anlegen, Bearbeiten und Löschen funktionieren und sind nach Neuladen auf einem zweiten Gerät gleich. Ein gelöschter Seed kommt nach einem Neustart von Api und Worker nicht zurück.
14. **Kachel:** Nach einem bewusst ausgelösten Error (Testroute nur in der Integrationsumgebung oder ein gezielt provozierter Fehler, z. B. 7TV-Sync gegen einen erfundenen Kanal) zeigt `/admin/monitoring` bei sichtbarem Tab **spätestens 90 s** nach dem Commit des Batches „Fehler der letzten 24 h: 1“ (30 s Cache + 60 s Timer, O2; der Batch-Commit liegt höchstens 1 s nach dem Log-Aufruf, gemessen wird mit 91 s Toleranz ab dem Log-Aufruf). Der Klick öffnet `/admin/logs?q=level%3E%3Derror&range=24h`, und der Chip „Alle Fehler“ ist aktiv. Bei gestopptem Worker-Schreiber (`LogCapture__Enabled=false` nur am Worker) zeigt die Zeile **frühestens 5 min, spätestens 5 min + 90 s** nach dem letzten Worker-Herzschlag „Log-Erfassung worker steht seit …“; vor Ablauf von 5 min erscheint sie nicht.
15. **Berechtigung:** Jede neue Route antwortet ohne Session mit `401` und für einen eingeloggten Nicht-Admin mit `403` (Api.Tests). Der Tab ist für Nicht-Admins nicht erreichbar.
16. **Telefon:** Auf 390 × 844 (Playwright und am Gerät nach `docs/Operations.md`, LAN-Weg) gibt es kein horizontales Scrollen; Facetten erreicht man über „Filter“, ein Tippen auf eine Zeile klappt die Details auf, ein Tippen auf einen Streifenbalken grenzt ein.
17. **Performance:** Das Gate aus §13.3 ist bestanden und im PR protokolliert.
18. **Harness:** Nach `docker compose --profile harness run --rm harness <kanal> --days 1` stehen keine Einträge mit der Instanz des Harness-Prozesses in `LogEntries`.
19. **Gates:** §13.4 ist grün.

## 15. Live-Verifikation (Regel 16)

Lokaler Stack mit echtem Postgres, Redis, Twitch und 7TV (`docker compose up -d --build`, Regel 15). Dabei werden geprüft:

- AK 2, 5, 6, 10, 12 und 14 von Hand.
- `docker compose restart worker` mit anschließendem „Start nach Deploy“.
- `docker compose stop redis` für 1 min: Die App läuft weiter; Live bleibt aus; die Einträge werden trotzdem gespeichert; nach dem Start kommen Live-Ereignisse wieder.
- Ein absichtlich hoher Logtakt über einen Testschalter **nur** in der Integrationsumgebung, kein Prod-Code-Pfad für Lasttests.

Auf Prod gibt es keine Verifikation durch die Session (kein SSH, globale Regel). Der Betreiber prüft nach dem Deploy „Start nach Deploy“ und die Monitoring-Zeile und vergleicht mit einem `docker logs --since 15m`. Die Befehle bereitet Claude vor.

## 16. Reihenfolge der Arbeit

| Ticket | Inhalt | Abhängig von | Migration |
|---|---|---|---|
| **T0** | Rauschen an der Quelle (§4) | — | nein |
| **T1** | Schema und Retention: Entitäten `LogEntry`, `LogCaptureStatus`, `LogFilter`, Enum `LogSource`; eine Migration `AddLogCapture` mit drei Tabellen, Indizes (§6) und den vier Seeds (§7.5, als Strings, deren Parse-Test in T3 nachgezogen wird); `RetentionPolicy.LogEntry`, Retention-Kategorie, Summary-Felder, Formatter; `scripts/backup-postgres.sh` (A14) | — | **ja**, additiv |
| **T2** | Erfassung: Sink, Puffer, Budget, Scrubber, Schreiber mit eigener Datenquelle, Herzschlag, Drop-Einträge, `AsyncLocal`-Sperre, Shutdown-Drain, `AddLogCapture` in Api und Worker (nicht Harness), `logs.appended` mit Drossel und Rekursionsbremse; Compose-Variable `LOG_CAPTURE_ENABLED` | T1 | nein |
| **T3** | Abfragen: Parser, `ILogQueryService` (Seite, Detail, Summary, Status), `ILogFilterService` (CRUD), Fehlercodes plus `api-error.ts` und Locales, Zeitlimit und Parallelitäts-Gate, `TtlCache` herausziehen, Api.Tests, Performance-Gate | T1 (parallel zu T2 möglich, mit gesetzten Testzeilen) | nein |
| **T4** | Seite `/admin/logs`: Tab, Route, Service, Stringwerkzeuge, Histogramm-Primitiv mit Designsprache-Abschnitt, Facetten, Liste, Details, Kopieren, Live ohne Sprünge, gespeicherte Filter, Telefon, i18n, Vitest, E2E, Audit | T3-Vertrag (gegen Mocks schon vorher baubar) | nein |
| **T5** | Monitoring-Zeile (§11), Doku (§17), Übergabe des Datenschutztexts an den Betreiber, Live-Verifikation, Abschluss-AK | T2, T3, T4 | nein |

**Deploy:** T0 sofort. T1–T5 gemeinsam oder T1+T2 zuerst, dann wird ab dem Deploy erfasst, die Seite folgt. Die Migration `AddLogCapture` läuft **vor** den Images von Hand (CLAUDE.md „Prod-Migration“); sie ist additiv, alte Images ignorieren die Tabellen. Der Datenschutztext muss live sein, **bevor** der Schreiber auf Prod erstmals schreibt. Andernfalls bleibt `LOG_CAPTURE_ENABLED=false`, bis er steht. Vor dem Merge auf `main` holt die Session die Codex-Zweitmeinung ein (Regel 22); diese Spec wird zusätzlich adversarial geprüft.

## 17. DECISIONS- und Doku-Einträge bei Umsetzung

Neue DECISIONS-Einträge auf Englisch, je im Commit der Änderung (Regel 3):

1. **T0:** Api EF Core auf Warning und USERNOTICE-Herabstufung von `LogUnaccountedFor`, mit Messzahlen und „Rauschen an der Quelle“.
2. **T1/T2:** In-App-Log-Erfassung in Postgres. 7 Tage `RetentionPolicy.LogEntry`, Untergrenze Information, Ausschlussregeln, Puffer- und Budgetgrenzen, Drop-Gründe als synthetische Einträge, Herzschlag, drei Rekursionsschichten, Registrierung außerhalb von `AddEmotePurgeInfrastructure` und ohne Harness, Schreiber als erster Hosted Service, Backup ohne Logdaten.
3. **T2:** Neuer Admin-Ereignistyp `logs.appended` (dünn, gedrosselt, Rekursionsbremse) und Erweiterung von `AdminTypes`.
4. **T3:** Filtersprache und Endpunktverträge (Grammatik, Fehlervokabular, Kopfseiten-Live-Abruf statt `afterId`, Zeitlimit und Parallelitäts-Gate ohne Rate-Limit-Policy), globale gespeicherte Filter mit einmaligem Migrations-Seed.
5. **T4:** Zweites Chart-Primitiv `app-time-histogram`, im selben Commit wie die Erweiterung von `docs/UI-Designsprache.md` §2.6.

Doku:

- `docs/Operations.md` (englisch): neuer Abschnitt „Log viewer“. Inhalt: was erfasst wird und was nicht, 7 Tage, `LogCapture:*` und `LOG_CAPTURE_ENABLED`, Budget und Drops, was „capture stale“ bedeutet und dass `docker logs` dann die Quelle bleibt, Abschalten als Rollback.
- In „Data retention“ eine Tabellenzeile und die neue Formatter-Ausgabe.
- „Backup“: Hinweis auf ausgeschlossene Logdaten.
- „Legal pages“: Hinweis, dass die Datenschutzerklärung die Log-Erfassung nennen muss.
- `docs/Architectur.md` (englisch): Absatz zur Log-Erfassung neben dem SSE-Fluss.
- `CLAUDE.md`: Zahl und Aufzählung der Worker-Hosted-Services (vierzehn, Schreiber vorn), die Api-Hosted-Services und die Admin-Zeile in „Umsetzungsstand“.
- `docs/Feature-Ideen-2026-08-01.md`: keine Änderung, die Idee steht dort nicht.

## 18. Rollback

- **Erfassung abschalten:** `LOG_CAPTURE_ENABLED=false` und Container neu erstellen. Die Seite zeigt dann „Erfassung steht“ und alte Einträge bis zum Auslaufen.
- **Ganz zurück:** alte Images. Die Tabellen bleiben und schaden nicht; die Migration lässt sich danach von Hand zurücknehmen (`Down` entfernt drei Tabellen, die Seeds gehen mit).
- **T0** ist eine Konfigurations- und Level-Änderung und per Revert umkehrbar.

## 19. Entscheidungen des Autors (offen für Einspruch)

| Nr. | Entscheidung | Abschnitt |
|---|---|---|
| A1 | Api: ganzer Namensraum `Microsoft.EntityFrameworkCore` auf Warning, nicht nur `.Database.Command` | §4.1 |
| A2 | Harness erfasst nicht; Registrierung außerhalb von `AddEmotePurgeInfrastructure` | §5.1 |
| A3 | Eigene Prozessidentität in Infrastructure, `WorkerIdentity` bleibt unverändert | §5.1 |
| A4 | Aus Scopes nur `RequestPath` und `TraceId` | §5.2 |
| A5 | Enger Secret-Scrubber als Verteidigungslinie | §5.2 |
| A6 | Puffer 5.000 Einträge / 8 MiB, neue verwerfen statt alte verdrängen | §5.3 |
| A7 | Tagesbudget 20.000 je Prozess, Information bis 80 %; Bemessung 320k Zeilen | §5.3 |
| A8 | Drops als synthetische `EmotePurge.LogCapture`-Warning-Einträge | §5.3 |
| A9 | Status-Herzschlag in `LogCaptureStatus` alle 60 s | §5.5 |
| A10 | `logs.appended` als dünnes Admin-SSE-Ereignis, 2-s-Drossel, Rekursionsbremse; Polling verworfen | §5.6 |
| A11 | Nur drei B-Tree-Indizes, kein GIN/Trigramm in v1 | §6.1 |
| A12 | Gespeicherte Filter global, ohne Audit | §6.3 |
| A13 | Seeds einmalig per Migration, gelöschte bleiben gelöscht | §6.3 |
| A14 | Backup-Dump ohne Logdaten | §6.4 |
| A15 | Keine gezielte Löschung bei Kontolöschung, Auslaufen mit der Frist | §6.4 |
| A16 | Filtersprache: UND zwischen Termen, ODER per Komma, Negation per Minus, Zeit nicht im String | §7 |
| A17 | Ein Parser, serverseitig; Frontend nur Stringwerkzeuge | §7.4 |
| A18 | Exception-Text nur im Detail-Endpunkt | §8.1 |
| A19 | Summary und Seite getrennt | §8.1 |
| A20 | Presets 1 h/6 h/24 h/7 Tage, Default 24 h, höchstens 120 Buckets | §8.2 |
| A21 | Live-Abruf = Kopfseite neu laden und per `id` zusammenführen, kein `afterId` | §8.3 |
| A22 | 5-s-Zeitlimit und höchstens 2 parallele Log-Abfragen statt Rate-Limit-Policy | §8.4 |
| A23 | Filter wird beim Abschicken angewandt, nicht beim Tippen | §9.1 |
| A24 | Kopieren je Eintrag und „Sichtbare kopieren“ (≤ 500) | §9.2 |
| A25 | Nachladen stoppt bei 2.000 Zeilen | §9.2 |
| A26 | Details auf dem Telefon inline statt im Sheet | §9.3 |
| A27 | Zweites Chart-Primitiv `app-time-histogram` mit gestapelten Balken | §9.6 |
| A28 | „Kachel“ als kompakte Zeile unter dem Seitenkopf, keine Karte | §11 |
| A29 | Zeile pollt mit dem 60-s-Takt der Seite, kein Live-Ereignis (Cache-Dauer durch O2 auf 30 s geändert) | §11 |

**Entscheidungen des Orchestrators (Builder-Check), 2026-10-14:**

| Nr. | Entscheidung | Abschnitt |
|---|---|---|
| O1 | Seed „Twitch-Reconnects“ schließt mit exakten Phrasen `"angestoßen (Quelle"` und `" gejoint."` aus; der Seed-Test belegt, dass die Reconnect-Zeilen (`TwitchConnectionWatchdog.cs:218`, `TwitchChatManager.cs:998`) treffen und die Join-Zeilen (`:540`, `:1010`) nicht | §7.5, §13.1 |
| O2 | `GET /logs/status` mit 30-s-Cache, 60-s-Timer bleibt; neuer Fehler ≤ 90 s sichtbar, „Erfassung steht“ ≤ 5 min + 90 s | §8.4, §11, AK 14 |
| O3 | Exceptions der redigierten TwitchLib-Ereignisse: Der Wrapper ersetzt sie in T0 durch Typen und Stacks der Kette ohne Message; der Sink speichert den Originaltyp und hat eine zweite Linie für den Fall eines umgangenen Wrappers | §4.2a, §4.3, §5.2, AK 4 |
| O4 | Bereich auf Bucket-Grenzen eingerastet (Ende aufgerundet, Start = Ende − Spanne); Breite hängt nur von der Spanne ab; die Antwort nennt den eingerasteten Bereich, das Nachladen nutzt ihn | §8.2, §8.3, §13.1, §13.2 |

Weitere Festlegungen ohne eigene Nummer, aber ebenso offen: Tabname „Logs“ und Position (§9), Zeiten in UTC wie im Dashboard (§9.2), Grenzwerte in §7.1 und §8.1 sowie `stale` ab 5 min (§11).

## 20. Bewusst nicht

| Nicht enthalten | Begründung |
|---|---|
| Stummschalten / „bekannt, ausblenden“ | B7: Rauschen an der Quelle, Ausschluss nur per Filter-Negation |
| Warnungszähler auf der Monitoring-Seite | B5: ~300 Warnungen pro Tag vor T0 wären Dauerrauschen. Nach T0 bleiben rund 24 Worker-Warnungen pro Tag, die Entscheidung bleibt trotzdem |
| Alerting, Telegram, Benachrichtigung aus gespeicherten Filtern | B6. Möglicher Nachläufer über die bestehende Kuma-Kette |
| Debug-/Trace-Erfassung, auch zeitweise | B4, B12 |
| Externe Log-Stacks, Docker-Socket, Langzeitarchiv | B12. Jenseits von 7 Tagen bleiben `docker logs` und die Host-Rotation die Quelle |
| Löschen oder Bearbeiten einzelner Einträge | Logs sind Beobachtung; Löschen ist Sache der Retention |
| Allgemeine Boolesche Logik (Klammern, `OR` zwischen Schlüsseln) | A16: kleine, lernbare Sprache; zwei Abfragen nebeneinander sind der Ausweg |
| Gruppierung „gleiche Meldung, andere Werte“ (Message-Templates) | Nicht verlangt. Das Template würde nicht gespeichert; ein möglicher Nachläufer bräuchte eine Spalte mehr |
| Image-Version/Commit je Eintrag | Im Image nicht verfügbar (§2.1). Nachläufer: Build-Arg mit Commit-Hash |
| Scopes außer `RequestPath`/`TraceId` | A4 |
| Facetten über Properties | Kategorie, Level und Quelle genügen dem Zweck; Properties sind über die Details filterbar |
| Zeitzonenwahl | UTC wie das Dashboard (#381) |

## 21. Annahmen und offene Punkte

- **Messwerte** in §2.6 stammen vom Betreiber und sind nicht von dieser Session geprüft (kein Prod-Zugriff). Die Bemessung in A7 und §13.3 rechnet absichtlich mit dem Budget-Höchstwert, nicht mit dem Messwert.
- **TwitchLib-Infozeilen:** Was die übrigen ~143 Information-Zeilen von `TwitchLib.Client.TwitchClient` je 20 h sind, ist nicht ausgewertet. Für den Seed „Twitch-Reconnects“ sollte der Betreiber nach dem ersten Prod-Tag einmal die Kategorie-Facette prüfen und den Seed bei Bedarf schärfen.
- **Npgsql-Logging:** Die Annahme, dass eine `NpgsqlDataSource` ohne Logger-Factory nichts loggt, auch wenn der EF-Provider im selben Prozess eine mit Logger-Factory betreibt, ist eine Umsetzungsauflage mit Test (§5.4), keine geprüfte Tatsache.
- **Datenschutztext:** Er ist ein Vorschlag. Rechtliche Prüfung und das Einpflegen liegen beim Betreiber (§12).
