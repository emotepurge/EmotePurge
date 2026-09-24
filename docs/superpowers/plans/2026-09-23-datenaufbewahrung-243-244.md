# Datenaufbewahrung und Kontolöschung (#243 Backend, #244) — Umsetzungsplan

> **Für ausführende Agenten:** Jeder Task läuft als eigener Subagent mit frischem Kontext
> (Regel 21). Der Task bekommt diesen Plan, die beiden Issues und die Entscheidungskommentare
> vom 2026-09-23; er argumentiert daraus und rollt die Entscheidungen nicht neu auf. Schritte
> sind als Checkbox (`- [ ]`) geführt. **Kein fertiger Code in diesem Plan** — Verträge, Namen,
> Grenzfälle und Reihenfolge ja, Rümpfe nein.
>
> **Revision 2 (2026-09-23):** sechs Befunde des Codex-Sol-Adversarial-Reviews eingearbeitet —
> Nutzer-Backfill, Recheck unter Zeilensperre, Marker bei Selbstlöschung und DB-Wächter für
> nachlaufende Audit-Schreiber, Join/Purge-Serialisierung, Redis-Inventar, Kaskadenzahlen im
> Trockenlauf. Stellen sind mit „(R2)" markiert.

**Ziel:** Ein täglicher Job im Worker setzt die am 2026-09-23 entschiedenen Aufbewahrungsfristen
durch, ein Admin kann ein einzelnes Konto auf Anfrage löschen, und beide laufen über **einen**
Kontolöschpfad, der Stimmen entfernt und das Audit-Log pseudonymisiert statt löscht. Der Job ist
beim ersten Prod-Deploy gefahrlos: er zählt nur, bis der Betreiber ihn scharf schaltet.

**Quellen der Wahrheit:** Issue #243 (nur Backend-Teil; die Selbstbedienung im Konto-Menü ist
**nicht** Teil dieses Plans) und #244, jeweils mit dem Entscheidungskommentar vom 2026-09-23.
Die Fristen daraus sind bindend:

| Daten | Frist | Messpunkt |
|---|---|---|
| Verschlüsselte Twitch-Tokens | 30 Tage nach letzter Nutzung leeren | `max(LastLogin, LastSeenAtUtc)` (neu, s. Befund 1) |
| Nutzerkonto | 12 Monate ohne Login löschen | dito — über den Kontolöschpfad aus #243 |
| Beendete Abstimmungen samt Stimmen | 12 Monate nach Ende löschen, keine Summen behalten | `EndedAt` (existiert) |
| Audit-Log-Einträge | älter als 12 Monate löschen | `OccurredAtUtc` (existiert) |
| Audit-Einträge bei Kontolöschung | pseudonymisieren, nicht löschen | — |
| Kanal nach „Leave" | 180 Tage nach Deaktivierung vollständig löschen | `DeactivatedAtUtc` (neu) |
| Aktive Kanäle und ihre Statistiken | bleiben | — |

**Architektur in vier Sätzen:** Zwei neue Zeitstempel (`User.LastSeenAtUtc`,
`Channel.DeactivatedAtUtc`) machen die Fristen messbar; eine Migration legt sie an und setzt
**beide** für Bestandszeilen auf den Migrationszeitpunkt. Ein neuer Core-Vertrag
`IAccountDeletionService` löscht ein Konto atomar unter Zeilensperre (Recheck → Stimmen →
Pseudonymisierung → Zeile) und wird vom Admin-Endpoint wie vom Job gerufen. Ein zweiter Vertrag
`IDataRetentionService` führt einen Durchlauf über die fünf Kategorien aus und liefert Zählwerte
samt Kaskaden; ob er schreibt oder nur zählt, sagt `Retention:Enforce` (Default `false`). Ein
zehnter Hosted Service `DataRetentionWorker` ruft ihn nach dem Boot-Gate und dann alle 24 h.

## Globale Randbedingungen

- **Schichten (CLAUDE.md):** Interfaces in `Core/Services/`, Implementierungen in
  `Infrastructure/Services/`, Registrierung nur in `AddEmotePurgeInfrastructure`. Der Worker
  kennt keinen `AppDbContext` (`CoreAssemblyReferenceTests`, DECISIONS 2026-08-02), der Api-Handler
  auch nicht (Regel 4).
- **Fristen sind Konstanten, keine Konfiguration.** Sie stehen in der Datenschutzerklärung
  (#247); eine Umgebungsvariable, die sie still verändert, macht diese zur Lüge. Konfigurierbar
  sind nur Schalter und Takt (`Retention:Enforce`, `Retention:IntervalHours`,
  `Retention:StartupDelayMinutes`, `Retention:MaxAccountsPerRun`).
- **Alle Zeitstempel UTC** (`DateTime.UtcNow`, `timestamp with time zone`), alle Cutoffs werden
  **einmal pro Durchlauf** berechnet und durch die Kategorien gereicht.
- **Zeilensperren statt Hoffnung (R2).** Wo eine Löschung mit einer Aktivierung derselben Zeile
  rennen kann, sperrt jede Seite die Zeile (`SELECT … FOR UPDATE` bzw. `FOR SHARE`) in ihrer
  eigenen Transaktion, bevor sie den Zustand liest. Ein Persistence-Helfer je Entität
  (`ChannelQueries.LoadChannelForUpdateAsync`, `UserQueries.LockUserAsync(mode)`) über `FromSql`;
  die vorhandenen ungesperrten Loader bleiben für reine Lesepfade.
- **Dev-Worker und `:8080` gehören einer laufenden Messung.** Kein `docker compose` aus dem
  Worktree (weder `up` noch `build`), kein zweiter Worker gegen die Dev-Datenbank — s. Abschnitt
  „Live-Verifikation".
- **Regel 3:** Jeder Task, der einen Vertrag ändert, trägt seinen DECISIONS-Eintrag (englisch) im
  selben Commit. Die Einträge sind unten je Task benannt.
- **Sprache:** Bezeichner, Kommentare, Log-Zeilen, Commit-Messages englisch. Log-Zeilen des Jobs
  tragen **nur Zahlen**, nie Logins oder IDs (#246-Linie: keine Identitäten im Worker-Log).

## Befund: was existiert, was fehlt

1. **„Letzte Nutzung" ist heute nicht messbar.** `User.LastLogin` wird ausschließlich im
   OAuth-Callback gesetzt (`UserService.UpsertLoginAsync`). Das Session-Cookie ist 14 Tage
   *sliding* (`Program.cs`): wer wöchentlich vorbeischaut, loggt sich nie neu ein — „12 Monate
   ohne Login" würde nach `LastLogin` allein aktive Nutzer löschen. Der Token-Refresh
   (`TwitchUserTokenService`) läuft nur im Request-Kontext eines gültigen Cookies, ist also
   ebenfalls an „gesehen" gebunden. **Neu: `User.LastSeenAtUtc` (nullable)**, gestempelt aus
   `OnValidatePrincipal` über `IUserService`, **höchstens einmal je 24 h** (Schreibzugriff nur, wenn
   `null` oder älter als 24 h; der Lesezugriff auf `SessionsValidFromUtc` läuft dort ohnehin je
   Request — dieselbe Projektion trägt beide Spalten, kein zweiter Roundtrip). Ein Nutzer, der
   30 Tage nicht gesehen wurde, hat kein gültiges Cookie mehr (14 Tage) — nichts kann sein Token
   noch benutzen. Damit gilt für beide Nutzerfristen dieselbe Größe:
   `lastActivity = max(LastLogin, LastSeenAtUtc)`.
   **Backfill (R2): die Migration setzt `LastSeenAtUtc = now()` für alle Bestandszeilen.** Ohne
   das wäre ein Bestandsnutzer, der seit dem Login vor Wochen wöchentlich da war, am ersten
   scharfen Lauf sein Token los (30 Tage nach `LastLogin`) und nach 12 Monaten sein Konto — und
   der Trockenlauf könnte ihn von einer echten Karteileiche nicht unterscheiden, weil beide nur
   `LastLogin` tragen. Mit dem Backfill wird niemand Bestehendes vor 30 Tagen bzw. 12 Monaten nach
   der Migration fällig; die Kosten sind eine um höchstens die bisherige Laufzeit (seit
   2026-07-25) verlängerte Frist für echte Karteileichen. Die Alternative — eine globale
   Schonfrist „nichts löschen vor Deploy + 12 Monate" als Konstante — wäre ein zweiter
   Mechanismus für dieselbe Aussage und müsste später wieder ausgebaut werden; der Stempel sagt
   dasselbe („seit hier messen wir") in der Spalte, in der die Frist ohnehin gelesen wird, und
   ist mit dem Kanal-Backfill (Befund 2) ein Muster statt zweier.
2. **Kanal-Deaktivierung hat keinen Zeitpunkt.** `ChannelService.LeaveAsync` setzt nur
   `IsBotActive = false`; `TrackingResumedAt` ist die Gegenrichtung. **Neu:
   `Channel.DeactivatedAtUtc` (nullable)**, gesetzt in `LeaveAsync`, **genullt** an jeder Stelle,
   die eine inaktive Zeile aktiviert: `CompleteJoinAsync` (der `!channel.IsBotActive`-Zweig, der
   heute `TrackingResumedAt` stempelt) und der Merge in `ChannelIdentityService`
   (`survivor.IsBotActive |= loser.IsBotActive` — wird der Survivor dadurch aktiv, Stempel nullen).
   Backfill in der Migration: `DeactivatedAtUtc = now()` für alle Zeilen mit `IsBotActive = false`.
   Der Stempel sagt „seit wann wir messen", nicht „seit wann inaktiv"; die 180 Tage beginnen mit
   der Migration. Genauer wäre der jüngste `channel.leave`-Audit-Eintrag je Kanal, aber das
   Audit-Log existiert erst seit 2026-07-31 und die Differenz ist Wochen, nicht Monate — das
   Sicherheitsnetz (nichts fällt beim ersten Lauf) wiegt schwerer als die Genauigkeit.
   Zusätzlich stempelt der Job jede inaktive Zeile **ohne** Stempel mit `now` (Zeilen aus dem
   Fenster zwischen Prod-Migration und Deploy des neuen Images, in dem das alte Image `LeaveAsync`
   noch ohne Stempel ausführt). Dieses Stempeln ist ein Schreibzugriff, der **auch im Trockenlauf**
   stattfindet — sonst beginnt die Frist nie — und wird als eigener Zähler ausgewiesen.
3. **`VoteSession.EndedAt` existiert** und wird in `EndAsync` zusammen mit `IsActive = false`
   gesetzt; das ist die einzige Stelle, die eine Session beendet. Kein Backfill, aber ein
   Grenzfall (s. unten).
4. **`AuditLogEntry.OccurredAtUtc` existiert**, mit absteigendem Index — ein Bereichs-Delete
   nutzt ihn direkt.
5. **Die laufende Session eines gelöschten Nutzers bleibt gültig.** `OnValidatePrincipal` fragt
   `GetSessionsValidFromUtcAsync`; für eine fehlende Zeile kommt `null` zurück, was heute „nie
   widerrufen" heißt. Nach einer Kontolöschung wäre der Cookie also weiter angemeldet, und der
   nächste Vote würde mit FK-Verletzung auf den fehlenden User laufen. Der Vertrag muss
   „unbekannter Nutzer" von „nie widerrufen" unterscheiden, und `Program.cs` weist den
   unbekannten Nutzer ab (`RejectPrincipal` + `SignOutAsync`, wie beim fehlenden Claim). Gültige
   Sessions ohne Zeile gibt es nicht — der Callback upsertet vor `SignInAsync`.
6. **Vote → User ist `Restrict`.** Die Stimmen müssen vor der Zeile weg; `Votes.UserId` hat den
   FK-Index, den EF Core standardmäßig anlegt.
7. **Audit-Details mit Nutzer-Identität** gibt es nur bei `user.revokeSessions` und
   `user.invalidateRoleCache` (`{ login }`, `TargetType = "user"`, `TargetId = <Twitch-ID>`). Die
   Channel-Aktionen tragen Kanal-Logins (`oldLogin`, `newLogin`, `sourceChannelName`) — das sind
   öffentliche Kanalnamen, keine Kontodaten, und sie bleiben unangetastet. Eine generische
   „ersetze jeden String, der dem Login gleicht"-Regel wäre falsch: ein Broadcaster, der sein
   Konto löscht, hat einen Kanal gleichen Namens, dessen Historie er nicht mitnimmt.
8. **Nachlaufende Audit-Schreiber (R2).** `UserService.InvalidateRoleCacheAsync` liest die Zeile
   *untracked*, wartet auf Redis und fügt dann den Eintrag mit `TargetId = ID` und `{ login }`
   ein — ohne UPDATE an der User-Zeile im selben `SaveChanges`. Committet eine Löschung
   dazwischen, landet nach der Pseudonymisierung ein identifizierender Eintrag (kein FK, nichts
   hält ihn auf). `RevokeSessionsAsync` ist dagegen heute schon geschützt: sein User-UPDATE liegt
   im selben `SaveChanges` wie der Eintrag; trifft es null Zeilen, wirft EF
   `DbUpdateConcurrencyException` und der Eintrag kommt nicht. Der ungeschützte Schreiber
   bekommt den DB-Wächter aus „Kontolöschpfad", Schritt 0.
9. **Join und Purge rennen (R2).** `JoinAsync` lädt die Zeile ungesperrt (`LoadChannelAsync`),
   prüft die Kappe, setzt `IsBotActive = true` und speichert — ohne Transaktion. Committet ein
   bedingtes Retention-DELETE zwischen Laden und Speichern, trifft es eine (noch) inaktive Zeile,
   kaskadiert durch Emotes, Statistiken, Live-Tage, Sessions und Stimmen, und der Join scheitert
   danach mit `DbUpdateConcurrencyException` (500). Unter READ COMMITTED reicht die WHERE-Bedingung
   allein nicht, weil die Ladeoperation keine Sperre hält. Lösung: beide Seiten sperren die Zeile
   (Randbedingung „Zeilensperren"), s. Verträge.
10. **Redis-Zustand mit Nutzer-ID (R2, Inventar).** `modlist:{id}`
    (`ModeratedChannelsProvider`, TTL `Auth:ModCheckCacheTtlMinutes`, Default 10 min),
    `7tveditor:{id}` und `subcheck:{id}:*` (`ModRoleCache`, dieselbe TTL) — alle drei löscht
    `IModRoleCache.InvalidateUserAsync(id)` **ohne** die User-Zeile zu brauchen, der Aufruf ist
    also nach der Löschung wiederholbar. Dazu genau ein weiterer Schlüssel:
    `ratelimit:telemetry:last-rejection` (`RateLimitTelemetryStore`, ein einzelner Slot, 25 h,
    vom nächsten abgewiesenen Request beliebigen Nutzers überschrieben), dessen Partition
    `user:{id}` lauten kann. Sonst nichts: Rate-Limit-Partitionen leben im ASP.NET-Limiter
    (In-Process), die SSE-Registry (`LiveStreamConnectionRegistry`) ebenfalls, Worker-Keys tragen
    Kanäle, keine Nutzer. Umgang: s. Kontolöschpfad, Schritt 8.
11. **Muster für den Job:** `TwitchIdentityReconcileWorker` — wartet auf `BootRecoveryGate`,
    erster Lauf sofort, dann `PeriodicTimer`, ein Scope je Tick, `catch` um jeden Tick, damit ein
    Postgres-Schluckauf den Host nicht mitnimmt. `WorkerServiceRegistrationTests` zählt die Hosted
    Services (heute 9). Explizite Transaktionen gibt es in `VoteSessionService.CreateAsync` als
    Präzedenz.
12. **Migration:** eine, additiv, zwei nullable Spalten plus die beiden Backfill-`UPDATE`s aus
    Befund 1 und 2. Prod-Migration manuell per Tunnel **vor** dem Deploy (CLAUDE.md
    „Prod-Migration"); das alte Image ignoriert die Spalten.

## Verträge

**`IAccountDeletionService` (Core)** — `DeleteAsync(twitchUserId, AuditActor actor,
AccountDeletionReason reason, DateTime? onlyIfInactiveBeforeUtc, ct)` → Ergebnis `Deleted |
NotFound | StillActive`, dazu die Zählwerte (gelöschte Stimmen, pseudonymisierte
Audit-Einträge). `AccountDeletionReason` ist ein Enum (`AdminRequest`, `Inactivity`).
**(R2)** `onlyIfInactiveBeforeUtc` ist beim Grund `Inactivity` Pflicht und wird **unter der
Zeilensperre** erneut geprüft (`lastActivity < cutoff`), beim Grund `AdminRequest` `null`. Der
Actor ist Pflicht (Admin aus dem Principal, `AuditActor.System` aus dem Job).

**`IDataRetentionService` (Core)** — `RunAsync(bool enforce, ct)` → `RetentionRunSummary` mit je
einem Zähler pro Kategorie (Tokens geleert, Konten gelöscht, Sessions gelöscht, Audit-Einträge
gelöscht, Kanäle gelöscht, Kanäle nachgestempelt) plus `Enforced`-Flag. **(R2) Zusätzlich die
Kaskaden je Kategorie**, berechnet aus **denselben** selektierten Eltern-IDs, die der scharfe Lauf
löscht: je Kanal Emotes, Nutzungszeilen, Live-Tage, Sessions, Stimmen; je Session Stimmen und
Ballot-Zeilen; je Konto Stimmen, getrennt nach offenen und beendeten Sessions, und zu
pseudonymisierende Audit-Einträge. Im Trockenlauf sind Eltern- und Kaskadenzähler die
`COUNT`-Ergebnisse derselben Prädikate — **ein** Prädikat je Kategorie, in einer Methode
formuliert, von beiden Modi benutzt, damit die gezählte und die gelöschte Menge nicht
auseinanderlaufen können.

**`RetentionPolicy` (Core, statisch)** — die sechs Fristen als benannte `TimeSpan`-Konstanten
(30 d, 365 d, 365 d, 365 d, 180 d; „12 Monate" = 365 Tage, dokumentiert). Einziger Ort, an dem
Zahlen stehen; Tests und Datenschutzerklärung verweisen darauf.

**`RetentionOptions` (Infrastructure, Muster `ChannelCapacityOptions`)** — Sektion `Retention`:
`Enforce` (bool, Default `false`), `IntervalHours` (Default 24), `StartupDelayMinutes` (Default
10), `MaxAccountsPerRun` (Default 100). `Validate()` beim Start.

**`IUserService`** — `GetSessionsValidFromUtcAsync` wird ersetzt durch eine Methode, die für den
Hot-Path von `OnValidatePrincipal` beides liefert: `null` heißt „Zeile fehlt → abweisen", sonst
der Widerrufs-Cutoff; als Nebeneffekt der gedrosselte `LastSeenAtUtc`-Stempel. Der Nebeneffekt
im Lesepfad ist bewusst (ein Roundtrip je Request) und im Interface-Kommentar begründet.
**(R2)** `InvalidateRoleCacheAsync` bekommt den DB-Wächter (Kontolöschpfad, Schritt 0).

**`IChannelService`** — **(R2)** `JoinAsync` läuft in einer expliziten Transaktion und lädt eine
**bestehende** Zeile (Name- wie ID-Pfad) über den `FOR UPDATE`-Helfer; Redis-Publishes bleiben
nach dem Commit. Neue Methode `PurgeIfInactiveSinceAsync(channelName, DateTime
deactivatedBeforeUtc, AuditActor actor, ct)` → `Purged | NotFound | StillActive`: eigene
Transaktion, Zeile `FOR UPDATE`, Bedingung `IsBotActive = false AND DeactivatedAtUtc < cutoff`
**nach** der Sperre geprüft, dann Remove (DB-Cascade) und Audit `channel.purge` mit Detail
`{ reason: "retention" }`. Kein LEAVE-Publish: der Worker ist in einem inaktiven Kanal nicht
drin, und hält ein Join die Sperre, sieht der Purge danach `IsBotActive = true` → `StillActive`.
Hält der Purge die Sperre, wartet der Join, findet danach keine Zeile mehr und legt eine neue an —
kein 500. Der Merge in `ChannelIdentityService` lädt den Survivor über denselben Helfer.

**`IRateLimitTelemetry`** — **(R2)** `ForgetPartitionAsync(partition, ct)`: löscht den
Last-Rejection-Slot, wenn seine Partition gleich `user:{id}` ist; fail-open wie der Rest des
Stores.

**Audit-Vokabular** — `AuditActions.UserDelete = "user.delete"`. Marker für pseudonymisierte
Einträge: `AuditActor.DeletedUser = ("deleted-user", "deleted-user")` — analog `System`; der
Bindestrich ist in Twitch-Logins nicht erlaubt und der String keine Twitch-ID, eine Kollision mit
einem echten Konto ist ausgeschlossen. Das Frontend rendert ihn wie `system` als Text; eine
Übersetzung ist optional (`audit.actors.deletedUser`), nicht Pflicht.

**Api** — `DELETE /api/admin/users/{twitchUserId}` in der `/api/admin`-Gruppe (erbt
`GlobalAdminAuthorizationFilter`), 204 bei Erfolg, 404 über den generischen Fallback, **kein neuer
`ApiErrorCode`**. Frontend: `AdminService.deleteUser`, Knopf je Zeile in `admin-users-page.ts`
hinter `TypedConfirmDialog` (unwiderruflich *und* zeilenbezogen — exakt die Purge-Begründung,
DECISIONS 2026-07-31); nachzutippen ist der Login.

## Der Kontolöschpfad im Detail

Eine Transaktion (`BeginTransactionAsync`), in dieser Reihenfolge:

0. **(R2) DB-Wächter für nachlaufende Audit-Schreiber:** Jeder Schreiber, der einen Eintrag mit
   `TargetType = "user"` erzeugt, ohne die User-Zeile im selben `SaveChanges` zu ändern (heute nur
   `InvalidateRoleCacheAsync`), öffnet eine Transaktion, sperrt die Zeile `FOR SHARE`
   (`UserQueries.LockUserAsync`), fügt den Eintrag ein und committet. Der Löschpfad sperrt
   `FOR UPDATE`: hält der Schreiber die Sperre, wartet die Löschung und pseudonymisiert den neuen
   Eintrag mit; hält die Löschung die Sperre, findet der Schreiber danach keine Zeile und schreibt
   nichts (Redis ist dann bereits geleert — die harmlose Richtung, Rückgabe `null`/404).
   Einträge, in denen der Nutzer **Actor** ist, schreibt nur ein Request dieses Nutzers selbst;
   für `Inactivity` ist das durch den Recheck ausgeschlossen (ein Request stempelt `LastSeenAtUtc`
   vor dem Handler, oder der Stempel ist < 24 h alt und die Zeile kein Kandidat), für
   `AdminRequest` bleibt ein Restfenster von einem in-flight Request des Nutzers, der gerade
   gelöscht wird — akzeptiert und im DECISIONS-Eintrag genannt (Entscheidung 9).
1. Zeile `FOR UPDATE` laden; fehlt sie → `NotFound`, nichts geschrieben, kein Audit-Eintrag
   (No-op-Regel). **(R2)** Bei `Inactivity`: `lastActivity < onlyIfInactiveBeforeUtc` unter der
   Sperre prüfen, sonst `StillActive` — ein Login oder ein `LastSeenAtUtc`-Stempel zwischen
   Kandidatenauswahl und Reihe im Batch gewinnt. Der Stempel-UPDATE aus `OnValidatePrincipal`
   wartet an der Sperre und trifft nach dem Commit null Zeilen; der Request selbst wird beim
   nächsten Mal abgewiesen (Befund 5).
2. `SessionsValidFromUtc = now` ist **nicht** nötig — die Zeile verschwindet, und Befund 5 macht
   die fehlende Zeile zur Abweisung. Ein bereits offener SSE-Stream läuft bis zu seinem Ende
   weiter; das ist akzeptiert (nächster Request wird abgewiesen).
3. Stimmen des Nutzers löschen (`Votes WHERE UserId`), über alle Sessions, offene eingeschlossen.
   **Folge:** Der Score offener und beendeter Abstimmungen ändert sich um die Stimmen dieses
   Nutzers. Das ist gewollt: eine Stimme ist eine Meinung mit Urheber, also personenbezogen;
   die Alternative — Umhängen auf einen Platzhalter-User — kollidiert am Unique-Index
   `(VoteSessionId, EmoteId, UserId)` beim zweiten gelöschten Nutzer und wäre re-linkbar. Keine
   Summen-Snapshots (#244-Entscheidung). Der Endpoint für laufende Abstimmungen liest live, die
   UI zeigt danach schlicht weniger Stimmen.
4. Audit pseudonymisieren, zwei disjunkte Mengen: (a) Einträge mit
   `ActorTwitchUserId = id` → `ActorTwitchUserId`/`ActorLogin` auf den Marker; (b) Einträge mit
   `TargetType = "user" AND TargetId = id` → `TargetId` auf den Marker, im `DetailsJson` der
   Schlüssel `login` auf den Marker (Objekt parsen, Wert ersetzen, neu serialisieren; jsonb
   normalisiert ohnehin). Alle anderen Spalten und Detailschlüssel bleiben. Zählwert je Menge.
5. Twitch-Tokens: fallen mit der Zeile; kein separater Schritt.
6. Zeile löschen.
7. Audit-Eintrag `user.delete` mit dem Actor des Auslösers, `TargetType = "user"`,
   **`TargetId = Marker`** und Details `{ reason, votesDeleted, auditEntriesPseudonymised }` —
   ohne ID, ohne Login. **(R2) Ist der Actor der gelöschte Nutzer selbst** (Admin löscht sich über
   die Admin-Liste; später die Selbstbedienung aus #243), wird als Actor der Marker geschrieben —
   sonst stellte dieser eine Eintrag ID und Login wieder her, die Schritt 4 gerade entfernt hat.
   Der Nachweis „am Tag X wurde ein Konto auf Anfrage gelöscht" reicht; die E-Mail-Anfrage hält
   der Betreiber außerhalb der App.
8. Commit, **danach** die Redis-Bereinigung (Befund 10): `IModRoleCache.InvalidateUserAsync(id)`
   und `IRateLimitTelemetry.ForgetPartitionAsync("user:{id}")`. **(R2)** Beide brauchen die Zeile
   nicht und sind idempotent; der Löschpfad versucht sie bei Fehlschlag **einmal erneut** und
   loggt danach mit Warning und Zählwert (keine ID). Verbleibende Schranke, wenn auch der zweite
   Versuch scheitert: die TTLs — 10 min für die Rollen-Keys, 25 h für den Telemetrie-Slot, der
   zudem vom nächsten abgewiesenen Request überschrieben wird. Beides steht so in
   `docs/Operations.md`. Der In-Process-Zustand von `TwitchTokenRefreshGate` bleibt; er ist ein
   Semaphor je ID ohne Nutzdaten.

**Idempotenz:** zweiter Aufruf → `NotFound`. **Nebenläufigkeit:** Admin-Löschung und Job-Löschung
desselben Kontos serialisieren an der Zeilensperre; der Zweite findet keine Zeile. Ein Vote, der
zwischen Schritt 3 und 6 einschlägt, lässt Schritt 6 an der FK scheitern → Rollback → der
Aufrufer bekommt den Fehler (500 bzw. der Job zählt den Fehlschlag und versucht es beim nächsten
Tick). Kein Serializable nötig. **Re-Login nach Löschung:** neue Zeile mit derselben Twitch-ID,
`LastLogin = now` — die pseudonymisierten Einträge lassen sich nicht rückverknüpfen, weil der
Marker die ID nicht mehr trägt.

## Der Job

- **Ort:** `DataRetentionWorker` im Worker (Hosted Service Nr. 10), nicht in der Api: die Api
  hat mehrere Replicas potenziell, der Worker ist per Konstruktion einer, und die
  Issue-Vorgabe nennt den Worker. Er wartet auf `BootRecoveryGate.Completed` (Kanal-Purge
  berührt Zeilen, die die Boot-Recovery liest) plus `StartupDelayMinutes`, damit ein
  Restart-Loop nicht jeden Start mit einem Durchlauf beginnt. Danach `PeriodicTimer`
  (`IntervalHours`).
- **Ein Scope je Tick, `IDataRetentionService.RunAsync(options.Enforce)`,** Log am Ende:
  Information im scharfen Modus, **Warning im Trockenlauf** („retention dry run: … would be
  affected, Retention:Enforce is false") mit Eltern- **und** Kaskadenzählern — die Warnung ist
  der Erinnerungsmechanismus gegen ein für immer vergessenes `false`. Jeder Tick loggt, auch bei
  lauter Nullen: er ist der Lebensbeweis des Jobs.
- **Kategorien in fester Reihenfolge, je eigene Transaktion(en):**
  1. Tokens: `ExecuteUpdate` der vier Token-Spalten auf `null` für `TwitchRefreshToken IS NOT
     NULL AND lastActivity < now − 30 d`. Ein Statement, kein Audit.
  2. Konten: Kandidaten `lastActivity < now − 365 d`, höchstens `MaxAccountsPerRun` je Tick,
     je Konto `IAccountDeletionService.DeleteAsync(id, AuditActor.System, Inactivity,
     onlyIfInactiveBeforeUtc: cutoff)` — eigene Transaktion je Konto, `StillActive` und
     Fehlschlag eines Kontos brechen die Kategorie nicht ab, beide werden gezählt.
  3. Sessions: `IsActive = false AND COALESCE(EndedAt, StartedAt) < now − 365 d`, gelöscht in
     ID-Batches (z. B. 500); Stimmen und Ballot-Zeilen fallen per DB-Cascade. Kein Audit je Zeile
     (s. Entscheidungen).
  4. Audit-Log: `OccurredAtUtc < now − 365 d`, in Batches (z. B. 5.000) bis nichts mehr trifft.
     Achtung: `ExecuteDelete` mit `Take` übersetzt nicht überall — ID-Auswahl plus
     `Contains`-Delete oder rohes SQL mit `LIMIT`-Subselect, der Implementierer wählt und
     begründet im Kommentar.
  5. Kanäle: erst Nachstempeln (`IsBotActive = false AND DeactivatedAtUtc IS NULL` → `now`, auch
     im Trockenlauf), dann Kandidaten `DeactivatedAtUtc < now − 180 d`, je Kanal
     `IChannelService.PurgeIfInactiveSinceAsync` in eigener Transaktion.
- **Batchgrößen** sind Konstanten in der Implementierung, keine Konfiguration; sie begrenzen die
  Transaktionsdauer, nicht die Menge — der Lauf iteriert, bis die Kategorie leer ist (außer
  Konten, s. `MaxAccountsPerRun`).
- **Nebenläufigkeit Api ↔ Worker (R2):** Konten und Kanäle serialisieren an ihren Zeilensperren
  mit Recheck unter der Sperre; Tokens, Sessions und Audit-Einträge laufen als bedingte
  Einzelstatements, deren WHERE Postgres unter READ COMMITTED gegen die aktuelle Zeilenversion
  auswertet — dort gibt es keine Lade-dann-Schreibe-Lücke.

**Warum Trockenlauf als Default:** Der Fehler, der hier teuer ist, ist irreversibel (Löschung)
und würde beim ersten Lauf auf der gesamten Bestandsdatenbank passieren; der Fehler der anderen
Richtung (vergessenes `Enforce`) kostet nichts Unwiederbringliches und macht sich durch eine
tägliche Warning-Zeile in der Log-Aggregation bemerkbar. Ein sicherer Default, der laut ist,
schlägt einen scharfen Default, der leise falsch sein kann. `docker-compose.prod.yml` bekommt
`Retention__Enforce=${RETENTION_ENFORCE:-false}` und `.env.example` den Schlüssel, damit das
Scharfschalten ein Env-Edit plus Stack-Update ist, kein Image-Build.

## Grenzfälle

- **Admin auf der Allowlist, > 12 Monate inaktiv:** wird gelöscht wie jeder andere. Admin-Rechte
  hängen an `Auth:AdminTwitchLogins` (Konfiguration), nicht an der Zeile; beim nächsten Login
  entsteht die Zeile neu und die Rechte sind da. Keine Ausnahme — sie wäre Sonderbehandlung ohne
  Datenschutz-Grund.
- **Kanal tritt nach Leave wieder bei:** `CompleteJoinAsync` nullt `DeactivatedAtUtc`; innerhalb
  von 180 Tagen bleibt die Historie (Entscheidung). Nach 180 Tagen ist sie weg und der Join legt
  eine neue Zeile an — `TrackingResumedAt` ist dann `null`, `CreatedAt` ehrlich. **(R2)** Fällt
  der Join genau in den Purge, entscheidet die Zeilensperre: Join zuerst → Purge `StillActive`;
  Purge zuerst → Join wartet und legt neu an. Beides ohne 500.
- **Abstimmung ohne `EndedAt`:** `IsActive = false` wird nur in `EndAsync` gesetzt, und dort mit
  `EndedAt` — der Fall ist theoretisch. Regel trotzdem: `COALESCE(EndedAt, StartedAt)`; eine
  beendete Session ist mindestens so alt wie ihr Start. `StartedAt` darf bis 366 Tage
  zurückdatiert sein, das trifft aber nur Sessions ohne `EndedAt`, die es nicht gibt.
- **Nutzer, der Kanal-Manager ist:** Es gibt keine DB-Kante User → Channel; Manager-Rechte werden
  live gegen Twitch/7TV geprüft. Die Kontolöschung berührt keinen Kanal, auch nicht den eigenen —
  Kanaldaten folgen der Kanal-Frist, und das steht so in der Datenschutzerklärung.
- **Nutzer gerade eingeloggt:** nächster Request → abgewiesen (Befund 5). Löscht sich ein Admin
  selbst über die Admin-Liste, ist das erlaubt (Präzedenz Revoke), der Dialog sagt es
  (`selfHint`-Muster), und der `user.delete`-Eintrag trägt den Marker als Actor (Schritt 7).
- **Uhr/Zeitzonen:** nur UTC; die Cutoffs kommen aus einem `TimeProvider`, der in
  `AddEmotePurgeInfrastructure` per `TryAddSingleton(TimeProvider.System)` registriert wird
  (Harness-Präzedenz), damit Tests Fristen ohne Wartezeit prüfen können — Seed-Zeilen mit alten
  Stempeln plus fixe Uhr.
- **Trockenlauf und Nachstempeln:** die einzige Schreiboperation im Trockenlauf; sie ist
  gerade *nicht* destruktiv und eröffnet die Frist erst. Im Log als eigener Zähler.
- **Konto ohne Tokens, aber mit alten Stimmen:** die Kontofrist greift unabhängig von der
  Tokenfrist; die Stimmen fallen mit dem Konto, auch in noch offenen Abstimmungen (s. Pfad, 3).

## Tasks

Jeder Task ist einzeln von einem Subagent mit frischem Kontext umsetz- und prüfbar. „Fertig"
heißt je Task: die genannten Tests grün, `dotnet format`, Commit(s) nach Regel 2. Backend-Gates
laut CLAUDE.md „Arbeitsweise".

### T1 — Zeitstempel und Migration (`sonnet`)

- [ ] `User.LastSeenAtUtc` und `Channel.DeactivatedAtUtc` als nullable `DateTime` mit
      Kommentar, der Semantik und `null`-Bedeutung nennt (Muster `TrackingResumedAt`).
- [ ] `ChannelService.LeaveAsync` stempelt; `CompleteJoinAsync` nullt im Reaktivierungszweig;
      `ChannelIdentityService`-Merge nullt am Survivor, wenn er durch den Merge aktiv wird.
- [ ] Migration `AddRetentionTimestamps`: zwei Spalten, **zwei** Backfill-`UPDATE`s — alle
      Nutzer `LastSeenAtUtc = now()`, inaktive Kanäle `DeactivatedAtUtc = now()` (Begründung
      Befund 1 und 2 im Migrationskommentar).
- [ ] Tests (`ChannelServiceTests`, `ChannelIdentityServiceTests`): Leave stempelt; Rejoin nullt;
      Join auf aktiven Kanal ändert nichts; Merge-Fall.
- [ ] DECISIONS: „Two retention timestamps, both backfilled to migration time: `LastSeenAtUtc`
      and `DeactivatedAtUtc`" (kann mit T2 zusammengelegt werden, wenn derselbe Commit).

### T2 — Session-Gültigkeit und „zuletzt gesehen" (`sonnet`)

- [ ] `IUserService`: Vertrag aus „Verträge" (null = unbekannt; gedrosselter Stempel).
      `GetSessionsValidFromUtcAsync` entfällt.
- [ ] `Program.cs` `OnValidatePrincipal`: unbekannter Nutzer → `RejectPrincipal` + `SignOutAsync`.
- [ ] `AdminUserDto` bekommt `LastSeenAtUtc` (die Admin-Liste sortiert weiter nach `LastLogin`;
      Anzeige des neuen Felds optional, keine UI-Pflicht in diesem Task).
- [ ] Tests (`UserServiceTests`): unbekannt → null; erster Request stempelt; zweiter innerhalb
      24 h stempelt nicht; nach 24 h wieder. `ApiFactory` ersetzt das Cookie-Schema, darum ist
      `OnValidatePrincipal` dort nicht prüfbar — der Fall gehört in die Live-Verifikation.
- [ ] DECISIONS: „A session whose user row is gone is rejected, and `LastSeenAtUtc` is written at
      most daily from the principal check".

### T3 — Kontolöschpfad (`opus`)

- [ ] `UserQueries.LockUserAsync` (Persistence, `FOR UPDATE`/`FOR SHARE` per `FromSql`);
      `IAccountDeletionService`, `AccountDeletionReason`, `AuditActions.UserDelete`,
      `AuditActor.DeletedUser`, `IRateLimitTelemetry.ForgetPartitionAsync`; Implementierung nach
      „Kontolöschpfad im Detail" inkl. Recheck unter Sperre, Selbst-Marker und
      Redis-Bereinigung mit einem Wiederholungsversuch; Registrierung.
- [ ] **(R2)** `UserService.InvalidateRoleCacheAsync`: Transaktion + `FOR SHARE` vor dem
      Audit-Insert (Schritt 0).
- [ ] Tests (Integration, Postgres + Redis-Fixture): Stimmen weg, Zeile weg, Audit als Actor
      pseudonymisiert, Audit als Target inkl. `login`-Detail pseudonymisiert, **Channel-Details
      unberührt** (Seed eines `channel.rename` mit gleichem Login als Kanalname),
      `user.delete`-Eintrag ohne Identität, **Selbstlöschung → Actor ist der Marker**, `NotFound`
      beim zweiten Aufruf, **`StillActive`, wenn `LastSeenAtUtc` unter der Sperre jünger als der
      Cutoff ist**, Rollen-Cache geleert, Telemetrie-Slot nur dann gelöscht, wenn er diesen Nutzer
      trägt, offene Session verliert die Stimme und der Score sinkt (über
      `IVoteSessionQueryService`).
- [ ] **(R2) Überlappungstest:** zwei `AppDbContext`s; Schreiber A hält `FOR SHARE` und hat den
      Insert noch nicht committet, Löschung B startet → B wartet; A committet; B läuft durch und
      der Eintrag von A ist pseudonymisiert. Gegenrichtung: B hält `FOR UPDATE`, A findet danach
      keine Zeile und schreibt nichts.
- [ ] `AuditLogEntry`-Klassenkommentar: „Retention is unbounded on purpose" ersetzen durch den
      Verweis auf die Frist und die Pseudonymisierung.
- [ ] DECISIONS: „Account deletion: row lock and recheck, votes go, audit entries are
      pseudonymised, the deletion entry carries no identity, late audit writers lock the row,
      Redis cleanup is retried once and otherwise bounded by TTL".

### T4 — Admin-Endpoint und Admin-UI (`sonnet`)

- [ ] `DELETE /api/admin/users/{twitchUserId}` (Muster `revoke-sessions`), Actor aus Principal,
      `AccountDeletionReason.AdminRequest`, `onlyIfInactiveBeforeUtc: null`.
- [ ] `AuthFilterMatrixTests`: die Route in die 401/403-Matrix (Regel 11: neue Route in einer
      gefilterten Gruppe).
- [ ] Frontend: `AdminService.deleteUser`, Knopf `danger`-Stufe je Zeile, `TypedConfirmDialog`
      mit Login, Self-Hint, Reload der Liste nach Erfolg; i18n `de.json`/`en.json` (Titel,
      Text mit Nennung von Stimmen und Audit-Pseudonymisierung, Eingabelabel, Bestätigen).
      Optional `audit.actors.deletedUser`.
- [ ] Ein Playwright-Fall (gemockt): disabled → Beinahe-Treffer bleibt disabled → exakter Login
      aktiviert → `DELETE` geht raus (Purge-Präzedenz). Kein Component-Spec (Regel 12).
- [ ] `npm --prefix web test -- --watch=false`, `npm --prefix web run e2e` (nur ohne Api auf
      `:5151`), `format`, `lint`.

### T5 — Kanal-Purge und Join-Serialisierung (`opus`) (R2)

- [ ] `ChannelQueries.LoadChannelForUpdateAsync` (Name und Twitch-ID); `JoinAsync` in einer
      expliziten Transaktion, bestehende Zeilen über den Helfer geladen, Publishes nach dem
      Commit; Merge-Survivor ebenso.
- [ ] `IChannelService.PurgeIfInactiveSinceAsync` nach „Verträge".
- [ ] Tests (Integration): Purge trifft eine inzwischen aktive Zeile nicht und schreibt dann
      keinen Audit-Eintrag; **Nebenläufigkeitstest** in beiden Reihenfolgen (Purge hält Sperre →
      Join wartet und legt neu an, ohne Exception; Join hält Sperre → Purge `StillActive`);
      `ChannelServiceTests` bleiben grün (Kappe, Idempotenz, Rename-Pfad).
- [ ] DECISIONS: „Join and retention purge serialise on the channel row".

### T6 — Retention-Service (`opus`)

- [ ] `RetentionPolicy`, `IDataRetentionService`, `RetentionRunSummary` (mit Kaskaden),
      `RetentionOptions` (+ `Validate`, Registrierung, `TimeProvider`-Registrierung);
      Implementierung nach „Der Job" (Kategorien, Prädikate einmal, Kaskadenzähler aus denselben
      IDs, Batches, Transaktionsgrenzen, Cutoff an `DeleteAsync` durchgereicht).
- [ ] Tests (Integration): je Kategorie ein „knapp drüber"/„knapp drunter"-Paar gegen die fixe
      Uhr; Trockenlauf zählt Eltern **und Kaskaden** identisch zum scharfen Lauf und **schreibt
      nichts** außer Nachstempeln; Nachstempeln macht eine Zeile beim selben Lauf nicht fällig;
      offene Session bleibt; Konto-Kappe `MaxAccountsPerRun`; `StillActive`/Fehlschlag eines
      Kontos stoppt die anderen nicht.
- [ ] DECISIONS: „Retention periods as code constants, one predicate per category for count and
      delete, cascade counts in the dry run, and the dry-run default".

### T7 — Hosted Service, Konfiguration, Compose (`sonnet`)

- [ ] `DataRetentionWorker` (Muster Reconcile-Worker), Registrierung als zehnter Hosted Service;
      `WorkerServiceRegistrationTests` 9 → 10 und die „nine"-Kommentare in
      `WorkerServiceRegistration`.
- [ ] `appsettings.json` des Workers: `Retention`-Sektion mit Defaults; `docker-compose.prod.yml`
      und `.env.example`: `Retention__Enforce`.
- [ ] CLAUDE.md-Zeile zum Worker („neun" → „zehn", neuer Service mit Schlüssel), `docs/
      Architectur.md` Worker-Liste.
- [ ] Test: Registrierung; die Tick-Logik ist dünn und wird live geprüft (s. unten).

### T8 — Dokumentation für Betreiber (`sonnet`)

- [ ] `docs/Operations.md`: Abschnitt „Data retention" (englisch) — Tabelle der Fristen mit
      Verweis auf `RetentionPolicy`, der Trockenlauf und seine Kaskadenzähler, wie man scharf
      schaltet, wie die Log-Zeile aussieht, die Admin-Kontolöschung als Weg für E-Mail-Anfragen,
      die Redis-Restschranke (10 min / 25 h).
- [ ] Issue-Kommentar-Vorlage für #247 (Datenschutzerklärung): die Tabelle in zitierbarer Form —
      als Text im PR, nicht im Repo.
- [ ] PR-Text: Prod-Migration-Hinweis (Tunnel, `list` → `update` → `list`), Reihenfolge
      Migration → Deploy → Trockenlauf-Beobachtung → `RETENTION_ENFORCE=true` → Stack-Update.

### T9 — Gates (Orchestrator, kein Subagent-Task)

- [ ] `dotnet test EmotePurge.slnx`, Frontend-Unit, E2E ohne Api auf `:5151`,
      `node scripts/coverage-local.mjs` (als Anlass, nicht Urteil).
- [ ] Live-Verifikation (unten), dann `/codex:review --model gpt-6-sol --scope branch`.

## Live-Verifikation (Regel 16), ohne den Dev-Worker zu stören

Der Dev-Worker (`docker compose`, `:8080`) und die Dev-Datenbank gehören einer laufenden
Messung. Ein zweiter Worker gegen dieselbe Datenbank würde Nutzungszeilen doppelt zählen
(additiver UPSERT), ein zweiter Worker am selben Redis dessen Health-/Roster-Keys überschreiben.
Darum:

1. **Eigenes, wegwerfbares Paar** per `docker run` (nicht compose): `postgres:16-alpine` und
   `redis` auf freien Ports, Schema per `dotnet ef database update --connection …` aus dem
   Worktree.
2. **Worker aus dem Worktree** per `dotnet run --project src/EmotePurge.Worker` mit
   Connection-Strings auf das Paar. Er verbindet sich anonym zu IRC, joint aber **keinen** Kanal
   (kein aktiver Kanal in der Wegwerf-DB) — kein Doppelzählen, kein 7TV-Traffic. Erst mit
   `Retention:Enforce=false` beobachten, dann `true`.
3. **Fixtures** per SQL in der Wegwerf-DB: je Kategorie eine fällige und eine nicht fällige
   Zeile, ein inaktiver Kanal ohne Stempel, ein Nutzer mit Stimmen in offener und beendeter
   Session, Audit-Einträge als Actor und als Target, ein Kanal mit Emotes/Statistiken/Live-Tagen
   für die Kaskadenzähler. Erwartung: Trockenlauf loggt Eltern- und Kaskadenzahlen und ändert
   nur den Stempel; scharfer Lauf löscht exakt die fälligen Zeilen; Audit-Marker sitzen.
4. **Api aus dem Worktree** auf `:5151` (nur, wenn dort nichts läuft; die Twitch-Redirect-URI ist
   an diesen Port gebunden) gegen dasselbe Paar, `ng serve` aus dem Worktree, frischer Login —
   Cookies der Haupt-Checkout-Api gelten hier nicht (DataProtection-Ring hängt am Pfad). Prüfen:
   Admin-Löschung über die UI mit getipptem Login; danach ist die Session des gelöschten Nutzers
   beim nächsten Request abgewiesen (Befund 5) — dafür ein zweiter Browser/Profil mit einem
   Testkonto, das der Admin löscht. Danach Redis prüfen: keine `modlist:`/`7tveditor:`/
   `subcheck:`-Keys des Testkontos.
5. Paar wieder entfernen.

## Entscheidungen des Betreibers (mit Empfehlung)

**Freigegeben am 2026-09-23:** Der Betreiber hat alle neun Punkte mit der jeweiligen Empfehlung
bestätigt. Die Fristen selbst standen nicht zur Debatte.

1. **„Letzte Nutzung" über `LastSeenAtUtc` (täglich gestempelt), und die Migration setzt ihn für
   alle Bestandsnutzer auf den Migrationszeitpunkt** — wie beim Kanal-Backfill. Empfehlung: ja —
   ohne den Stempel löscht die 12-Monats-Regel Nutzer, die täglich da sind; ohne den Backfill
   verlieren wöchentliche Bestandsnutzer am ersten scharfen Lauf ihre Tokens, und der Trockenlauf
   kann sie von Karteileichen nicht unterscheiden (Befund 1). Preis: echte Karteileichen aus den
   ersten zwei Monaten leben bis zu zwei Monate länger.
2. **Der `user.delete`-Audit-Eintrag trägt keine Identität** (nur Grund und Zählwerte), und bei
   Selbstlöschung ist auch der Actor der Marker. Empfehlung: ja — sonst unterläuft der Eintrag
   die Pseudonymisierung derselben Transaktion; der Nachweis der Anfrage liegt beim Betreiber in
   der E-Mail.
3. **Keine Audit-Einträge je gelöschter Session/je geleertem Token**, nur der Kanal-Purge
   (`channel.purge`, `reason: retention`) und die Kontolöschung (`user.delete`) werden auditiert.
   Empfehlung: ja — das Audit-Log zeigt seltene administrative Handlungen; tausend
   Retention-Zeilen im Jahr würden es fluten. Der Job loggt Zählwerte inkl. Kaskaden.
4. **Trockenlauf als Default, Scharfschalten per `RETENTION_ENFORCE=true` in der Prod-`.env`.**
   Empfehlung: ja, mit täglicher Warning-Zeile im Trockenlauf als Erinnerung.
5. **Kein Admin-Sonderfall bei Inaktivität.** Empfehlung: keiner — Rechte hängen an der
   Konfiguration, nicht an der Zeile.
6. **Selbstlöschung eines Admins über die Admin-Liste erlaubt** (Revoke-Präzedenz, Hinweis im
   Dialog). Empfehlung: erlauben — ein Verbot bräuchte einen neuen Fehlercode für einen Fall, der
   harmlos ist.
7. **`MaxAccountsPerRun = 100`** als Kappe je Tick. Empfehlung: ja — begrenzt die Laufzeit des
   ersten scharfen Laufs; der Rest folgt am nächsten Tag.
8. **(R2) Redis nach der Löschung: Rollen-Keys und Telemetrie-Slot werden aktiv gelöscht, ein
   Wiederholungsversuch, danach TTL-Schranke (10 min / 25 h) statt einer eigenen Nacharbeits-
   Warteschlange.** Empfehlung: ja — beide Aufrufe sind idempotent und brauchen die Zeile nicht,
   eine Outbox für einen 10-Minuten-Key wäre mehr Mechanik als Nutzen. Der Telemetrie-Slot wird
   gelöscht statt auf die Log-Entscheidung aus #246 verwiesen: die betraf flüchtige Log-Zeilen,
   dieser Slot ist Zustand, der eine Löschanfrage überleben könnte, und der Handgriff ist klein.
9. **(R2) Restfenster bei Admin-Löschung:** ein Request, den der gerade gelöschte Nutzer
   *selbst* in-flight hat, kann nach der Pseudonymisierung noch einen Eintrag mit ihm als Actor
   committen (kein FK, keine Sperre auf fremden Tabellen). Empfehlung: akzeptieren und im
   DECISIONS-Eintrag nennen — der Fall verlangt, dass der Betroffene im Sekundenfenster seiner
   per E-Mail erbetenen Löschung aktiv handelt; die Inaktivitätslöschung ist durch den Recheck
   davon frei. Wer es dicht will, zieht die Pseudonymisierung einmal je Tick des Jobs für
   Einträge mit `ActorTwitchUserId` ohne zugehörige User-Zeile nach — das wäre ein kleiner
   Folgetask, kein Blocker.
