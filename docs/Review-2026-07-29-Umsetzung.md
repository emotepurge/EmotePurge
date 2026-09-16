# Umsetzung des Reviews vom 2026-07-29 — Fortschritt

Begleitdokument zu [`Review-2026-07-29.md`](Review-2026-07-29.md). Hält fest, welche der 81 Befunde umgesetzt sind, wo bewusst vom vorgeschlagenen Fix abgewichen wurde und was noch offen ist. Die Wellen-Einteilung folgt Abschnitt 8 des Reports.

| Welle | Inhalt | Status |
|---|---|---|
| **A** | Quick Wins | ✅ **abgeschlossen** (2026-07-29) |
| **B** | Sicherheit & Korrektheit (S1/S2) | ✅ **abgeschlossen** (2026-07-30) |
| **C** | Refactorings | ✅ **abgeschlossen** (2026-07-30) |
| **D** | Tests | ✅ **abgeschlossen** (2026-08-02) |
| **E** | Infra & Launch | ⬜ offen |

---

## Welle A — umgesetzt am 2026-07-29

18 Befunde. Umgesetzt in drei parallelen Arbeitsströmen mit getrennter Datei-Eigentümerschaft (7TV/Shared/i18n · Feature-Seiten · Backend/Doku).

### Kritisch

**S1-4 — „Löschen starten" war klickbar, bevor die Shared-Set-Prüfung antwortete**
`web/src/app/shared/seven-tv/delete-confirm-dialog.ts`: Bestätigen-Button hat jetzt `[disabled]="warningLoading()"` plus `disabled:opacity-50 disabled:cursor-not-allowed`. `warningLoading` war bereits als `input()` vorhanden, keine Signaturänderung nötig. Damit kann kein Löschlauf mehr gestartet werden, während die Ownership-Prüfung (1–3 s bei vielen moderierten Channels) noch läuft.

**S1-3 Sofortteil — Auswahl driftete still von der angezeigten Liste ab**
`selection.clear()` an den zwei fehlenden Stellen: `features/usage-stats/usage-stats-page.ts` in `toggleSort()` (Sortierwechsel verschiebt jeden Positionsindex des Shift-Ankers) und `features/voting/vote-session-detail-page.ts` im `next`-Handler von `load()` (Neu-Deserialisierung bricht die Objektidentität).
**Abweichung:** Der Report nennt für die Voting-Seite zwei Stellen (`refresh()` *und* den `getResults`-Handler); im Ist-Code ist `refresh()` nur ein Aufruf von `load()`, und `load()` hat genau einen `next`-Handler, den auch `vote()` durchläuft. Ein `clear()` deckt beide Aufrufer ab.
**Bewusst in Kauf genommene Nebenwirkung:** Auf der Voting-Detailseite verliert man die Auswahl jetzt bei *jedem* Reload, also auch nach jedem einzelnen Vote. Das ist die korrekte, datensichere Richtung, aber schlechtere UX als vorher-scheinbar. Die dauerhafte Lösung ist die keyed `ListSelection` (Welle B, s. u.) — erst danach lässt sich S2-16 (Auswahl über Reloads/Filterwechsel *erhalten*) überhaupt sicher bauen.

### Hoch

**S2-15 — Namensfilter war Exakt-Match statt Teilstring-Suche**
`web/src/app/shared/emotes/emote-usage-filter.ts`: `globToRegExp` verankert nur noch, wenn die Eingabe tatsächlich `*` oder `?` enthält. `peepo` findet jetzt `peepoHappy`; die Glob-Semantik bleibt vollständig erhalten, sobald ein Wildcard getippt wird. Placeholder auf „Name suchen…" vereinfacht, der Glob-Hinweis ist in ein `title`-Attribut gewandert (neuer Key `usageStats.filterNameTitle`).

**S2-18 — Dialog behauptete „Set gehört nicht diesem Channel", obwohl die Prüfung nur fehlgeschlagen war**
`delete-confirm-dialog.ts`: `hasSharedSetWarning` gated jetzt zusätzlich auf `available` — der rote Alarm erscheint nur noch bei `available && !isOwnSet`. Für `available === false` gibt es einen eigenen, gelben, neutral formulierten Block (`massDelete.ownershipCheckUnavailable`). Damit produziert ein 7TV-Ausfall keinen falschen Alarm mehr, der über Alarm-Fatigue genau den echten Fall entwertet hätte.
Im Fehler-Fallback von `mass-delete-panel.ts` bleibt `isOwnSet: false` bewusst stehen (Agent hatte auf `true` gedreht, zurückkorrigiert): Der Wert ist bei `available: false` bedeutungslos, aber sollte ihn künftiger Code ohne `available`-Prüfung lesen, ist „nicht als eigen verifiziert" die sichere Richtung.

**S2-19 — „Beenden" und „Löschen" als gleichfarbige Nachbarn; Löschbestätigung nannte die Abstimmung nicht**
`features/voting/vote-session-list-page.*`: „Beenden" ist jetzt neutral (`text-slate-300`) — es ist keine destruktive Aktion. „Löschen" bleibt rot, ist aber als Outline-Button (`border-red-800`) mit zusätzlichem Abstand abgesetzt. Die Bestätigung nennt den Session-Titel und den Stimmenverlust (`voting.list.deleteConfirm` mit `{{ title }}`-Interpolation).
**Abweichung:** `deleteSession(sessionId: number)` → `deleteSession(session: VoteSessionSummary)`, weil für die Interpolation der Titel gebraucht wird.

### Mittel

**S3-14 — Log-Zeile beim Recreate war im proaktiven Pfad sachlich falsch**
`src/EmotePurge.Worker/TwitchChatManager.cs`: `RecreateClientAsync(string reason)`; beide Aufrufer übergeben einen zutreffenden Grund, die vorher doppelten (teils widersprüchlichen) `LogWarning`-Aufrufe sind entfallen. Das Log behauptet nicht mehr „0 aufeinanderfolgende Verbindungsfehler", wenn der proaktive Pfad greift — relevant, weil Logs beim nächsten Prod-Ausfall wieder das einzige Forensik-Werkzeug sind.

**S3-16 — Modale Dialoge ohne Fokusfalle, Escape und Beschriftung; Statusmeldungen ohne `aria-live`**
`delete-confirm-dialog.ts` und der Token-Fallback-Dialog in `mass-delete-panel.ts`: `cdkTrapFocus` + `cdkTrapFocusAutoCapture`, `cdkFocusInitial` auf den **Abbrechen**-Button (bewusst nicht auf den destruktiven), `(keydown.escape)`, `aria-labelledby` bzw. `aria-label`. `role="alert"` auf alle Fehlerbanner (`overview-page`, `usage-stats-page`, `vote-session-list-page`, `vote-session-detail-page`, `channel-workspace-layout`), `role="status"` auf Kopier-Feedback, Fortschritt (`delete-progress-panel.ts`) und die „X von Y Emotes"-Zeilen.
**Abweichung:** Der Report nannte `mass-delete-panel.ts:54` als Ort eines Banners — dort liegt tatsächlich ein zweiter Modal-Dialog. Dieser hat denselben A11y-Fix bekommen; die echten Fortschritts-/Fehlermeldungen liegen in `delete-progress-panel.ts` und wurden dort ausgezeichnet.

**S3-17 — Formularfelder ohne Label; Kontrast unter AA**
Jedes Filter-/Eingabefeld in `usage-stats-page`, `vote-session-detail-page`, `vote-session-list-page`, `overview-page` und `seven-tv-token-input` hat jetzt ein `<label class="sr-only">` bzw. `[attr.aria-label]` (bestehende Placeholder-Keys wiederverwendet, Muster von `language-switcher.ts`). Die beiden Datumsfelder haben sichtbare Labels „von"/„bis" (neue Keys `usageStats.fromLabel`/`toLabel`; der bisherige `dateRangeTo`-Span ist dort zum „bis"-Label geworden, der Key bleibt in `vote-session-detail-page` in anderem Kontext in Gebrauch).
Kontrast (projektweit freigegeben): `placeholder:text-slate-600` → `text-slate-400` (≈2,5:1 → ≈7:1, betrifft auch das 7TV-Token-Feld) und `text-slate-500` → `text-slate-400` für Textinhalte. Icon-/Zustandsfarben blieben bewusst unangetastet (u. a. die Keep/Delete-Button-Icons in `vote-session-detail-page`).

**S3-18 — Ausgewählte Emote-Karten ohne sichtbaren Auswahlzustand**
`usage-stats-page.html` und `vote-session-detail-page.html`: Karten-Container von statischem `class` auf `[class]`-Bindung — ausgewählt → `ring-2 ring-purple-500 bg-purple-950/40`. „Was ist markiert?" ist damit vor einem irreversiblen Löschlauf auf einen Blick erkennbar statt über 36 winzige Checkboxen.

**S3-22 — Logout ohne Fehlerbehandlung**
`web/src/app/core/auth/auth.service.ts`: gemeinsame private `resetClientSession()`, die aus `next` **und** `error` läuft (und von `handleSessionExpired()` mitgenutzt wird). Ein serverseitiger Logout-Fehler lässt jetzt nicht länger Session-State und — sicherheitsrelevant — das 7TV-Schreib-Token im `sessionStorage` stehen. Test dafür in `auth.service.spec.ts` ergänzt.

**S3-30 Akutteil — doppelter Request pro Seitenwechsel**
`vote-session-list-page.ts`: das explizite `this.load()` in `onPageChange` entfernt; der `effect()` erledigt den Reload bereits, weil `load()` `page()` im reaktiven Kontext liest. `my-votings-page.ts` bleibt korrekt unverändert (dort läuft `load()` aus dem Konstruktor, kein Dirty-Effect-Pfad).

### Niedrig

- **S4-1** — Klassenkommentar von `UsageStatsAccessAuthorizationFilter` auf den Ist-Zustand korrigiert: **fünf** Endpoints über zwei Gruppen (`usage-stats`, `usage-stats/totals`, `sync-deleted`, `set-warning`, `active-set`), mit expliziter Begründung, warum der Schreibpfad `sync-deleted` bewusst dazugehört, plus dem Satz, dass echte Management-Semantik hinter den strengeren Filter gehört. `CLAUDE.md` hat einen datierten Nachtrag bekommen, der historische Log-Eintrag selbst blieb unverändert. Gegengeprüft: der Filter lässt tatsächlich 7TV-Editoren zu (`CanViewUsageStatsAsync`).
- **S4-3** — `form-action 'self'` in die CSP (`src/EmotePurge.Api/Program.cs`). Die Direktive fällt nicht auf `default-src` zurück; damit ist die letzte skriptlose Exfiltrations-/Phishing-Lücke der Kette geschlossen. `style-src 'unsafe-inline'` bleibt bewusst (nicht Teil von Welle A).
- **S4-8** — `formatScore()` per `Intl.NumberFormat` statt `DecimalPipe` in `vote-session-detail-page`; reagiert jetzt korrekt auf den Laufzeit-Sprachwechsel („12,5" statt „12.5").
- **S4-9** — letzter hartkodierter nutzersichtbarer String übersetzt (`overview.admin.channelNamePlaceholder`).
- **S4-12** — `SevenTvEmoteJsonMapper.MapFromJsonElement` samt Stale-Kommentar gelöscht (verifiziert aufruferlos); `Program.cs`-Verweise in `IVoteSessionQueryService.cs`, `vote-session-detail-page.ts` und `web/.claude/CLAUDE.md` auf `Endpoints/*.cs` korrigiert; `CLAUDE.md:166` um den Klammerzusatz *(heute `usage-stats-access.guard.ts`)* ergänzt, ohne den Log-Text umzuschreiben; `web/src/app/core/shared/` → `web/src/app/core/models/` umbenannt (einziger Import angepasst).
- **S4-13** — die `BackgroundService`-Ausnahme in `CLAUDE.md` von der Aufzählung auf ein Kriterium umgestellt („ausschließlich per `AddHostedService<T>()`, nirgends injiziert"), Beispielliste jetzt inklusive `Worker` (verifiziert: fünf registrierte Hosted Services).
- **S4-14** — neuer Abschnitt **Sprache** in `CLAUDE.md`, nach Nutzerentscheidung: Bezeichner/Typen/öffentliche APIs englisch · Kommentare in **neuem** Code englisch (Bestand im Worker bleibt deutsch, wird nur nicht fortgeführt) · Log-/`throw`-Messages deutsch · Doku deutsch, Commit-Messages englisch. Kein Bestandscode umgeschrieben.

### Verifikation

| Prüfung | Ergebnis |
|---|---|
| `dotnet build EmotePurge.slnx` | grün, 0 Warnungen |
| `dotnet test EmotePurge.slnx` | 39/39 grün |
| `ng build --configuration production` | grün |
| `npm --prefix web test -- --watch=false` | 79/79 grün (78 vorher, +1 Logout-Fehlerpfad) |
| `npm --prefix web run e2e` | 4/4 grün |
| Locale-Parität de/en | 188/188 Keys, keine Lücke in beiden Richtungen |

Noch **nicht** live gegen echte Twitch-/7TV-Accounts getestet — Welle A enthält mit S2-15 (Filtersemantik), S2-18/S1-4 (Löschdialog) und S3-17 (Kontrast/Labels) genug UI-Verhalten, das im Browser geprüft werden sollte.

---

## Welle B — umgesetzt am 2026-07-30

22 Befunde in drei Blöcken. Die Worker-Zustandsmaschine (B1) wurde Fix für Fix von Hand gebaut, mechanisch umrissene Teile (Backup-Skript, keyed `ListSelection`, Filter-Validierung) liefen über Sub-Agents mit disjunkter Datei-Eigentümerschaft und wurden anschließend gegengelesen.

### Vorab: Verifikation von S2-1 gegen den echten TwitchLib-Quelltext

Der Befund war der einzige mit ausdrücklichem Restvorbehalt (gestützt auf XML-Doku, Strings und Reflection). Gegengelesen wurden `ReconnectionPolicy.cs`, `ClientBase.cs`, `ClientOptions.cs` und `ConnectionWatchDog.cs` in `TwitchLib.Communication`, Commit `d1904be` — und `TwitchLib.Communication 2.0.1` ist auch tatsächlich die aufgelöste Version. **Der Befund trägt vollständig:**

- `ClientOptions(reconnectionPolicy: null)` → `new ReconnectionPolicy(3_000, maxAttempts: 10)`.
- `internal void Reset(bool isReconnect) { if (isReconnect) return; … }` — ein Reconnect setzt `_attemptsMade` nachweislich **nicht** zurück, `ReconnectInternalAsync` ruft immer mit `isReconnect: true`.
- `ProcessValues()` erhöht bei **jedem** Versuch, `AreAttemptsComplete()` ist `_attemptsMade == _maxAttempts`. Ist das Kontingent verbraucht, läuft die `while`-Schleife in `OpenPrivateAsync` null Mal → `RaiseFatal()` → dauerhaft `"Fatal network error."`.
- `ConnectionWatchDog` bricht bei einem gescheiterten Reconnect wirklich per `break` aus seiner Monitor-Schleife und läuft nie wieder.
- Der parameterlose Ctor setzt `_maxAttempts = null`; `_attemptsMade == null` ist nie wahr → tatsächlich unbegrenzt, mit 3s→30s-Backoff.

**Zwei Korrekturen am Report**, beide ohne Auswirkung auf den Fix: (1) Die Default-Policy hat ein **konstantes** 3-Sekunden-Intervall (`min == max == 3000`), das Kontingent ist also nach ~30 s aufgebraucht, nicht nach den im Report genannten 165 s — diese Rampe (3s→30s) gehört zur **neuen** Policy. (2) `TwitchClient.ConnectAsync()` liefert zwar wie beschrieben `Task<bool>`, **`ReconnectAsync()` aber nur `Task`** (verifiziert an `ITwitchClient`, Tag `4.0.1`). S2-4 ist damit nur auf dem Connect-Pfad umsetzbar; beim Reconnect zeigt sich der Ausgang ausschließlich über `OnReconnected`/`OnConnectionError`.

**Eine vom Report nicht gesehene Folge:** Mit unbegrenzten Versuchen verlässt `OpenPrivateAsync` seine Schleife erst, wenn die Verbindung steht — `ConnectAsync()`/`ReconnectAsync()` kehren also nicht mehr mit `false` zurück, sondern blockieren beliebig lange. Ungebremst würde ein Twitch-Ausfall damit den Worker-Start aufhängen (keine Redis-Subscription, keine Join-/Leave-Kommandos). Deshalb sind beide Aufrufe jetzt mit `OpenWaitTimeout` (30 s) **im Warten** begrenzt, nicht im Versuch: der Retry-Loop läuft im Hintergrund weiter und feuert `OnConnected`, sobald er durchkommt.

### B1 — Worker-Cluster

**S2-1 — Verbindungslimit war client-seitig, nicht Twitch-seitig**
`TwitchChatManager.CreateClient()` erzeugt den `TwitchClient` jetzt über einen expliziten `WebSocketClient` mit `new ClientOptions(new ReconnectionPolicy())`. `TwitchLib.Communication 2.0.1` ist dafür als direkte `PackageReference` aufgenommen. `MaxReconnectsBeforeProactiveRecreate` ist **ersatzlos entfallen** — die Prämisse („zählbasiertes Twitch-Limit pro justinfan-Identität") ist widerlegt. `MaxConsecutiveConnectionErrorsBeforeRecreate` bleibt als Sicherheitsnetz.

**S2-4 — Rückgabewert des Connects wird ausgewertet**
`OpenAsync()` wertet den `bool` aus und loggt einen Fehlschlag als `LogError`; ein gescheiterter Connect ist damit im Log vom Erfolg unterscheidbar, und `_isConnected` bleibt korrekt `false`, sodass der Watchdog greift. `ReconnectClientAsync()` ist bewusst getrennt, weil es dort keinen Rückgabewert gibt.

**S2-7 — Health-Key kann nicht mehr „connected" auf einem toten Client melden**
`RecreateClientAsync` setzt `_isConnected = false` direkt nach `UnwireClient` (das `OnDisconnected` unterdrückt). Zusätzlich kennt `GET /api/worker/health` jetzt einen dritten Zustand `stale` (verbunden, aber seit über 5 Minuten keine Chat-Daten). **Abweichung/Ergänzung:** Der Report schlug nur `LastMessageReceivedUtc` als Bezug vor; das hätte einen frisch gestarteten Worker sofort als `stale` gemeldet. Das Payload trägt deshalb zusätzlich `connectAttemptedUtc` als Ersatzbezug — live verifiziert: direkt nach dem Start `connected`, erst nach 5 Minuten Stille `stale`. Das Frontend bildet backend-`stale` und `disconnected` auf denselben Warn-Punkt ab (für den Betrachter ist die Unterscheidung nicht handlungsrelevant).

**Nachtrag: doppelte JOINs bei jedem Reconnect (aus einem Prod-Log vom 2026-07-29, Stand vor dem Review)**
Das Log zeigte 12 Join-Bestätigungen für 6 Channels nach einem einzigen Reconnect — und nebenbei „Reconnect Nr. 8 … Schwelle 8", also einen Lauf, der genau einen erzwungenen Reconnect vom `Fatal network error` entfernt war (Bestätigung der S2-1-Analyse). Ursache am Quelltext geklärt: `TwitchClient._client_OnReconnected` (4.0.1) rejoint alle Channels **selbst**, leert danach `_joinedChannelManager` und feuert erst dann unser `OnReconnected`; `Handle004` feuert zusätzlich unser `OnConnected`. Die Annahme im Änderungslog („TwitchLib rejoint nicht automatisch", 2026-07-24) ist damit widerlegt, und weil die Liste vor unserem Handler geleert wird, greift auch TwitchLibs eigene Duplikatsprüfung nicht mehr.
Für den Zwischenstand dieser Welle war das eine **Verschlechterung**: mit Rejoin in `OnConnected` *und* `OnReconnected` wären es TwitchLib(6) + 6 + 6 = 18 JOINs gewesen. Twitch erlaubt 20 JOINs pro 10 Sekunden, und ein Verstoß kappt die Verbindung — also genau die Verstärkungsschleife, die diese Welle beseitigen soll; bei 20 getrackten Channels wäre sie sicher eingetreten. Behoben: gerejoint wird nur noch auf einem **frischen** Client (Boot, Recreate), erkannt über `_joinsIssuedForCurrentClient`, wo TwitchLibs Liste leer ist. Alles andere deckt `EnsureJoinedAsync` im Minutentakt ab. Lokal verifiziert: genau eine Join-Bestätigung pro Channel.

**S2-2 — absorbierender Totzustand beseitigt**
`ITwitchChatManager` hat `ConnectAttemptedUtc`; der Watchdog nutzt es als Ersatzbezug, wenn nie eine Nachricht ankam. Zusätzlich gibt es einen eigenen Zweig für `!IsConnected` mit kürzerem Cooldown (60 s statt 5 min) — ein Client, der sich selbst als getrennt meldet, ist kein Abuse-Risiko und braucht keine Stille-Schwelle.

**S2-3 — Join-Absicht statt bestätigter Joins**
`_joinedChannels` ist zu `_desiredChannels` (`ConcurrentDictionary<string, bool>`, Wert = von Twitch bestätigt) geworden. Die Absicht wird **vor** dem Versuch eingetragen, `OnJoinedChannel` bestätigt per `TryUpdate` (fügt bewusst nicht ein, damit eine spät eintreffende Bestätigung einen bereits verlassenen Channel nicht wiederbelebt). `EnsureJoinedAsync` ist neu auf dem Interface und wird vom Resync-Worker im Minutentakt aufgerufen — bestätigte Channels werden dabei übersprungen, es gibt also keinen JOIN-Sturm für gesunde Channels. `OnConnected` rejoint jetzt ebenfalls (nicht nur `OnReconnected`), womit der explizite Rejoin nach einem Recreate entfallen konnte, der sonst jeden JOIN doppelt abgesetzt hätte.

**S2-5 — Boot-Recovery kann den Host nicht mehr mitreißen**
`try/catch` pro Channel **und** um die ganze Schleife. Neu ist `BootRecoveryGate` (Worker-internes Singleton): der Resync-Worker wartet darauf, bevor sein Tick startet, damit Boot-Sync und periodischer Sync nicht auf demselben Channel kollidieren. Das Gate wird in einem `finally` freigegeben — eine gescheiterte Boot-Recovery darf den Konvergenzpfad nicht dauerhaft blockieren.

**S2-6 — Resync-Worker vollständig abgesichert**
`ResyncOnceAsync` ist komplett in `try/catch`, inklusive Scope-Erzeugung und Channel-Abfrage. `OperationCanceledException` beim Shutdown wird getrennt und still behandelt.

**S2-8 — Reihenfolgegarantie zurückgeholt**
`RedisSubscriber` nutzt `ChannelMessageQueue.OnMessage(async …)` statt des fire-and-forget-Callbacks; Handler werden wieder streng sequenziell awaited. Für die zweite Kollisionsquelle (Resync-Tick × JOIN auf demselben Channel) gibt es `ChannelSyncGate` — ein Singleton mit einem `SemaphoreSlim` pro Channelname, das `SevenTvSyncService.SyncChannelAsync` serialisiert.

**S3-12 — leeres 7TV-Set wird nicht mehr angewendet**
Meldet 7TV null aktive Emotes, obwohl bereits welche bekannt sind, bricht der Sync mit `LogWarning` ab, statt alles zu archivieren und den Match-Cache zu leeren.

**S3-13 — atomarer Upsert statt Read-then-Insert**
`UsageStatFlushService` schreibt jetzt per `INSERT … SELECT FROM UNNEST(…) ON CONFLICT ("EmoteId","Date") DO UPDATE`, was nebenbei den 1.000er-`IN`-SELECT alle 30 Sekunden spart. `UsageFlushWorker.StopAsync` ruft `base.StopAsync` **zuerst** (beendet die Timer-Schleife) und flusht erst danach.
**Über den Report hinaus:** Ein fehlgeschlagener Flush wird nicht mehr verworfen, sondern über das neue `IEmoteUsageCounter.Merge` zurückgestellt — begrenzt auf fünf aufeinanderfolgende Versuche. Der Grund für die Grenze ist **nicht** Speicher (die Zahl der Emotes ist beschränkt), sondern Zuordnung: `UsageStat.Date` ist der Tag, an dem der Flush *gelingt*, sodass über einen langen Ausfall mitgeschleppte Counts sonst auf dem falschen Kalendertag landen.

### B2 — Datenverlust

**S1-1 — Channel-Leave löscht nichts mehr**
`ChannelService.LeaveAsync` setzt `IsBotActive = false` und publisht weiterhin `LEAVE:`. Neu ist `PurgeAsync` plus `DELETE /api/channels/{channelName}/purge` hinter `GlobalAdminAuthorizationFilter` — bewusst **nicht** hinter `ChannelManagementAuthorizationFilter`, weil der Moderatoren zulässt und ein positiv gecachter Mod-Status ein `/unmod` bis zu zehn Minuten überlebt. Kein UI dafür (Nutzerentscheidung). Die Leave-Bestätigung im Frontend versprach bisher wörtlich die Löschung aller Daten und ist in beiden Locales korrigiert.
**Testanpassung:** `LeaveAsync_RemovesChannel_AndPublishesLeaveCommand` kodierte das alte Verhalten und ist zu `LeaveAsync_DeactivatesChannel_ButKeepsTheRow` geworden; dazu drei neue Tests (Rejoin reaktiviert dieselbe Zeile, Purge löscht, Purge auf unbekanntem Channel).

**Nachtrag zu S1-1 — Rejoin-Möglichkeit im Frontend (beim Live-Test gefunden)**
Der Soft-Deactivate hat eine Lücke aufgerissen, die es vorher nicht geben konnte: Solange ein Leave die Zeile löschte, tauchte der Channel danach wieder als *ungetrackt* auf und bekam den „Hinzufügen"-Button. Jetzt bleibt er getrackt (nur `IsBotActive=false`), fällt also nie in diesen Zweig — ein Nicht-Admin sah „Bot inaktiv" und **hatte nirgends im UI einen Weg zurück**; nur Admins konnten sich über die manuelle Namenseingabe behelfen. Behoben an beiden Stellen: In der Übersicht steht bei getrackten, inaktiven Channels jetzt ein „Bot reaktivieren"-Button (in der Nutzer-Sektion für Broadcaster/Mods, in der Admin-Sektion generell), der die Zeile ohne Navigation an Ort und Stelle umschaltet. Im Channel-Workspace ersetzt derselbe Button den „Channel verlassen"-Button, solange der Bot inaktiv ist, plus ein Hinweisbanner — der Bot-Status kam dabei gratis aus der ohnehin laufenden `canManage`-Probe, die ihn bisher verwarf. Backend unverändert: `JoinAsync` reaktiviert die bestehende Zeile bereits.

**S1-2 — Backup**
`scripts/backup-postgres.sh` plus `docs/Backup-und-Restore.md`. Das Skript vermeidet die `pg_dump | gzip`-Falle (gzip liefert auch bei abgebrochenem Dump Exit 0) über Dump in eine `.tmp`-Datei, Prüfung von Exit-Code **und** Dateigröße, dann atomares `mv`. Rotation greift nur auf das eigene Namensmuster, nie blind aufs Zielverzeichnis. Off-Site (`rclone`) ist optional und darf niemals ein erfolgreiches lokales Backup als Fehlschlag melden. **Was du selbst auf dem VPS tun musst, steht in `docs/Backup-und-Restore.md`** — Kurzfassung: Skript ablegen, `chmod +x`, Zielverzeichnis anlegen, Cron-Eintrag setzen, einmal manuell testen, und mindestens einmal einen **Restore** proben.

**S1-3 — keyed `ListSelection`**
Auswahl liegt jetzt in einem `signal<ReadonlySet<string>>` über `keyFn(item)`, der Shift-Anker ist das Item (über seinen Key), aufgelöst gegen die **aktuelle** Liste zur Klickzeit; ist der Anker nicht mehr sichtbar, fällt es sauber auf Einzel-Toggle zurück. Zwei getrennte Sichten: `selectedKeys` (autoritativ, überlebt Refetch/Sortierung) und `selectedItems` (für Anzeigezwecke, nur aktuell sichtbare Zeilen). Damit entfallen zwei `clear()`-Notbehelfe aus Welle A — insbesondere geht **die Auswahl auf der Voting-Detailseite nicht mehr bei jedem Vote verloren**. Bewusst stehen geblieben: das `clear()` im Ladepfad der Usage-Stats-Seite (dort wechseln mit Channel/Zeitraum auch die Zahlen, gegen die ausgewählt wurde) und die Filter-`clear()`s — Letztere zu entfernen wäre S2-16 und ist ausdrücklich nicht Teil dieser Welle.

**S2-12 — `sync-deleted` schluckt keine Fehler mehr**
`SevenTvDeleteService` hat ein `syncReport`-Signal (`idle`/`pending`/`succeeded`/`partial`/`failed`), zwei automatische Retries mit 2s/4s (bei `401`/`403` bewusst keine — eine abgelaufene Session heilt nicht durch Warten) und `retrySyncReport()` für den manuellen Versuch. `archivedCount`/`notFoundIds` werden ausgewertet: kommen alle IDs in `notFoundIds` zurück, ist das jetzt `partial` statt scheinbarem Vollerfolg. Das Panel emittiert `deleted` **erst nach** bestätigter Rückmeldung; scheitert sie, feuert das neue Output `reloadRequested`, und die Host-Seite lädt neu, statt optimistisch zu filtern.
**Abweichung:** Der ursprünglich beauftragte Sub-Agent brach an einem Sessionlimit ab, nachdem er nur Imports und Konstanten geschrieben hatte; der Rest ist von Hand entstanden. Die Panel-Schnittstelle hat dabei ein zweites Output bekommen — beide Host-Seiten sind entsprechend verdrahtet.

### B3 — Session und Transport

**S2-9 + S2-10 — gemeinsam, wie in Z2 gefordert**
Data-Protection-Keys werden persistiert (`DataProtection__KeyPath=/keys`, Volume `dataprotection-keys` in beiden Compose-Dateien, `mkdir`/`chown` in der `final`-Stage des Api-Dockerfiles, da `appuser` mit `--no-create-home` angelegt ist). Lokales `dotnet run` bleibt unberührt, weil die Persistenz nur bei gesetztem Pfad aktiviert wird. Gleichzeitig — nicht später — der Widerruf: neue Spalte `User.SessionsValidFromUtc` (Migration `AddUserSessionsValidFrom`), Claim `twitch:session_issued_at` beim Callback, Prüfung in `OnValidatePrincipal`, und `logout` setzt den Zeitstempel, wirkt also serverseitig. Cookies **ohne** den neuen Claim werden abgelehnt statt geduldet, sonst bliebe eine dauerhafte Umgehung der Prüfung offen.
Transport: `Cookie.SecurePolicy = Always` (unabhängig von `X-Forwarded-Proto`), `SameSite = Lax`, `HttpOnly`, plus `Strict-Transport-Security` als echter Response-Header. `UseHttpsRedirection()` ist mit Begründung entfernt — im Container ein No-Op, der Schutz vortäuschte.
**Abweichung:** **kein** `includeSubDomains` und kein `preload`. Beides ist nach dem ersten Ausliefern ein Jahr lang praktisch unumkehrbar und würde Subdomains binden, über die diese App nichts weiß (auf dem VPS läuft eine zweite Anwendung). Nachträglich ergänzen ist gefahrlos, zurücknehmen nicht.
**Kosten, bewusst in Kauf genommen:** `OnValidatePrincipal` macht pro authentifiziertem Request einen Primärschlüssel-Lookup (auf eine Spalte projiziert, ungetrackt). Ein Redis-Cache dafür wäre der naheliegende nächste Schritt, falls das je auffällt.

**S3-3 — Validierung in den Filtern + Redis-Grenze**
Alle vier Autorisierungsfilter prüfen `ChannelNameValidation.IsValid()` statt nur auf „nicht leer", bevor irgendein Redis-Key geschrieben wird. Gegengeprüft: `IsValid` normalisiert intern, gemischt geschriebene Namen wie `HandOfBlood` bestehen die Prüfung also unverändert — genau die Falle, in die das Projekt beim Frontend-Validator schon einmal gelaufen ist. Beide Compose-Dateien setzen jetzt `--maxmemory 256mb --maxmemory-policy allkeys-lru`.

**S2-11 — Policy-Schnitt und Caches**
Aus `ExpensiveOps` sind zwei Policies geworden: `ExternalApi` (20/min) für alles, was Helix oder 7TV anfasst — inklusive `GET /api/channels/mine`, der ganzen `EmoteEndpoints`- und `UsageStatsEndpoints`-Gruppe (deren **Autorisierungsfilter selbst** zwei ungecachte 7TV-Calls macht) sowie der Vote-Session-Liste — und `Bookkeeping` (120/min) allein für `sync-deleted`. Dazu zwei neue Caches in `IModRoleCache` mit derselben TTL wie der Mod-Check: 7TV-Editor-Grants (`7tveditor:{uid}:{channel}`) und Sub-Status (`subcheck:{uid}:{broadcasterId}`). Ein `null` von Helix bzw. 7TV wird bewusst **nicht** gecacht, sonst sperrte ein transientes 429 legitime Nutzer für die volle TTL aus.

**S3-1 — eine Manager-Definition statt zweier**
`VoteEligibilityService` prüft nicht mehr selbst Broadcaster-Login und Mod-Status, sondern delegiert an `IChannelAccessService.CanManageChannelAsync`. Damit ist der Admin-Zweig automatisch enthalten (er fehlte in der Kopie), und die Frage „wer verwaltet diesen Channel?" ist nur noch an einer Stelle beantwortet. Das `IsActive`-Gate in `EvaluateAsync` blieb unberührt.

**S3-4 — Rohnutzung nur noch für Manager**
`GetResultsAsync` nimmt `includeRawUsage`; für Nicht-Manager ist `TotalUseCount` 0, während `NormalizedUsageScore`, `Score` und die Sortierung unverändert aus den echten Zahlen berechnet werden. Zusätzlich ist das rückdatierbare `StartedAt` auf 366 Tage begrenzt — ohne die Grenze umfasste eine einmalig weit zurückdatierte `Everyone`-Session die gesamte Historie.

**S3-7 — Doppelklick auf „Daumen hoch" ergibt keinen 500 mehr**
`CastVoteAsync` fängt `DbUpdateException`, lädt neu und behandelt den Konflikt als das Update, das er in Wahrheit ist. Dazu global `UseExceptionHandler` mit neuem Fehlercode `unexpected_error` (in `ApiErrorCodes`, `api-error.ts` und beiden Locales) — bewusst ohne Exception-Details im Body.

**S3-2 — Broadcaster-Identität an der Twitch-ID**
`CanManageChannelAsync` und der 7TV-Editor-Abgleich vergleichen die unveränderliche `Channel.TwitchChannelId` gegen `principal.TwitchUserId`; der Login-Vergleich bleibt nur als Fallback für Zeilen ohne aufgelöste ID. Stimmt der Login überein, die ID aber nicht, wird abgelehnt **und geloggt** — sonst wäre das eine Ablehnung, die niemand erklären könnte.

### Verifikation

| Prüfung | Ergebnis |
|---|---|
| `dotnet build EmotePurge.slnx` | grün, 0 Warnungen |
| `dotnet test EmotePurge.slnx` | 47/47 grün (39 vorher: +5 Flush-Upsert, +3 Leave/Purge-Semantik, −1 ersetzt) |
| `ng build --configuration production` | grün |
| `npm --prefix web test -- --watch=false` | 90/90 grün (79 vorher: +7 Sync-Report, +4 keyed Selection) |
| `npm --prefix web run e2e` | 4/4 grün |
| Locale-Parität de/en | 197/197 Keys, keine Lücke in beiden Richtungen |
| `docker compose up -d --build` | Stack läuft, Worker verbindet und joint alle vier Channels inkl. 7TV-Sync |
| Smoke-Test | `Strict-Transport-Security` gesetzt, CSP unverändert, `401` ohne Cookie, Health `connected` direkt nach Start |

Der neue `INSERT … ON CONFLICT`-Pfad war der einzige handgeschriebene SQL-Code der Welle und ließ sich anders als durch einen Test nicht prüfen — `UsageStatFlushServiceTests` (5 Tests gegen echtes Postgres) belegt insbesondere, dass die Arbiter-Inferenz auf dem Unique-Index trotz dessen `INCLUDE`-Spalte greift.

**Noch nicht live gegen echte Twitch-/7TV-Accounts getestet.** Besonders prüfenswert: Login/Logout (der Widerruf ist neu und meldet **alle** bestehenden Sessions einmalig ab), ein Leave mit anschließendem Rejoin, ein Löschlauf inklusive Rückmeldung, und das Verhalten des Health-Punkts.

### Was beim Deployment auf Prod zu tun ist

1. **Migration anwenden**: `AddUserSessionsValidFrom` (additiv, eine nullable Spalte auf `Users`). Lokal bereits angewendet.
2. **Alle Nutzer werden einmalig ausgeloggt** — sowohl weil der Data-Protection-Schlüsselring wechselt als auch weil Cookies ohne den neuen Claim abgelehnt werden. Ab dann überleben Sessions Redeploys.
3. Der Portainer-Stack braucht das neue Volume `dataprotection-keys` (steht in `docker-compose.prod.yml`).
4. ~~Backup-Cronjob einrichten, siehe `docs/Backup-und-Restore.md`.~~ **Erledigt 2026-08-04** (VPS-Härtung): Cron 03:00 → NAS-Pull → OneDrive-Crypt, s. den Stand-Kasten in [Backup-und-Restore.md](Backup-und-Restore.md).

---

## Welle C — umgesetzt am 2026-07-30

Zehn Befunde, in der Reihenfolge aus Abschnitt 8 des Reports. Alles, was Semantik verschiebt, ist Fix für Fix von Hand entstanden; ein Sonnet-Sub-Agent hat mit ausschließlicher Eigentümerschaft an `Architectur.md` gearbeitet (Ergebnis gegengelesen, ein Fehler korrigiert — s. u.).

### S2-13 Schritt 1 — ein HTTP-Interceptor statt sechs Kopien

`web/src/app/core/http/api-auth.interceptor.ts`, registriert per `provideHttpClient(withFetch(), withInterceptors([apiAuthInterceptor]))`. Der 401-Block stand sechsmal, fünfmal wortgleich, und fehlte in `channel-workspace-layout.ts` ganz.

Die erste Zeile `if (!req.url.startsWith('/api/')) return next(req)` ist der tragende Teil: die Mass-Delete-Engine spricht direkt mit `7tv.io/v3/gql` und mit einer **anderen** Credential. Ein `401` von dort heißt „das 7TV-Schreib-Token ist abgelaufen", nie „deine Session ist weg" — vorher existierte diese Unterscheidung nur darin, dass niemand den 401-Block dorthin kopiert hatte.

**Über den Report hinaus:** `/api/auth/me` und `/api/auth/logout` sind ausgenommen. `auth/me` antwortet für **jeden** anonymen Besucher mit 401 (jeder Guard ruft es zuerst) — ohne die Ausnahme hätte der Interceptor die öffentliche Landing-Page auf `/login` umgeleitet. Fehler werden weitergeworfen, nicht geschluckt, damit Aufrufer ihre Spinner beenden und eine Meldung rendern können.

### S2-13 Schritt 2 — Status-Fallback im Fehler-Mapping

`apiErrorTranslationKey` las ausschließlich `error.error.errorCode`. Body-lose Antworten — alle vier Autorisierungsfilter (`Results.Forbid()`), der Rate Limiter, ein Verbindungsabbruch (Status 0) — fielen damit auf „Etwas ist schiefgelaufen. Bitte versuch es erneut." und sagten einem frisch gemoddeten Nutzer, er solle etwas wiederholen, das bis zum Ablauf des Mod-Cache nicht gelingen kann. Neu: sieben `errors.status.*`-Keys (0/401/403/404/409/429/≥500) in beiden Locales, mit dem 10-Minuten-Hinweis im 403-Text.

**Über den Report hinaus:** 429 ist mit aufgenommen — der Policy-Schnitt aus Welle B (`ExternalApi`, 20/min auf `channels/mine`, den Emote- und Usage-Stats-Gruppen) hat diesen Fall real erreichbar gemacht.

**Abweichung:** Der Sonderweg in `vote-session-detail-page.ts` ist auf `apiErrorTranslationKey` umgestellt — **außer für 403**. Dort kommt der Code von `VoteEligibilityFilter` und bedeutet genau eine Sache („falsche Rolle für *diese* Session"), während der generische 403-Text auf den Mod-Cache zeigt und in einer Subs-Session aktiv irreführend wäre. Vier dadurch unbenutzte Keys unter `voting.detail.errors.*` sind entfallen.

### S3-26 — `ChannelName.Normalize` und `LoadChannelSessionAsync`

`Trim().ToLowerInvariant()` stand 28-mal; der Gewinn ist nicht die gesparte Zeichenzahl, sondern dass die stille Invariante „`Channel.ChannelName` ist in der DB immer getrimmt und lowercase" jetzt einen Namen und eine XML-Doc hat. `EmoteMatchCache.Normalize` und `ChannelNameValidation` delegieren dorthin.

Der Block „Channel laden → Session per `Id == sessionId && ChannelId == channel.Id` laden" stand sechsmal in drei Services — und dieses `ChannelId`-Prädikat ist die **einzige** Absicherung dagegen, dass Channel A eine Session von Channel B beendet, löscht oder abstimmt. Jetzt einmal in `src/EmotePurge.Infrastructure/Persistence/ChannelQueries.cs`, kein generisches Repository. Die Entitäten bleiben getrackt geladen wie an allen sechs Stellen vorher, weil `EndAsync`/`DeleteAsync` das brauchen.

### S3-32 — `ISevenTvEditorService`

Zwei Abweichungen vom vorgeschlagenen Fix, beide notwendig:

1. **Rückgabe ist nicht `IReadOnlySet<string>`**, sondern ein `SevenTvEditorGrants`-Record mit Logins **und** Twitch-IDs. Eine reine Login-Menge hätte die S3-2-Härtung aus Welle B (Vergleich auf der unveränderlichen `TwitchChannelId`, weil Twitch freigewordene Logins zur Neuregistrierung freigibt) für den Editor-Pfad wieder zurückgedreht.
2. **Rückgabetyp ist nullable.** `null` = „7TV konnte nicht antworten", unterscheidbar von der leeren Menge = „antwortete: editiert nichts". `MyChannelsService` braucht diese Unterscheidung für sein `sevenTvUnavailable`-Flag, und ein Ausfall darf nicht als Negativ gecacht werden.

**Redis-Topologie geändert:** aus dem Per-Channel-Bool `7tveditor:{uid}:{channel}` ist der Grant-Satz `7tveditor:{uid}` (JSON) geworden, `IModRoleCache.TryGetIsSevenTvEditorAsync`/`SetIsSevenTvEditorAsync` sind durch `TryGetSevenTvEditorGrantsAsync`/`SetSevenTvEditorGrantsAsync` ersetzt. Grund: Der 7TV-Call liefert ohnehin alle Grants; der alte Schnitt zahlte die zwei Calls für den zweiten Channel desselben Nutzers erneut, und die Übersicht — die die volle Liste braucht — konnte ihn gar nicht nutzen und lief bei **jedem** Aufruf ungecacht. Ein unlesbares Payload gilt als Cache-Miss, nicht als „keine Grants": bei einer Autorisierungs-Eingabe ist Neuauflösen die sichere Richtung.

### S2-13 Schritt 3 — `GET /api/channels/{channelName}/permissions`

Löst alle vier UI-Sonden ab (Nutzerentscheidung, inkl. Guard): zwei in `ChannelWorkspaceLayout`, eine in `VoteSessionListPage`, eine im `usageStatsAccessGuard` — Letztere war eine vollständige, sofort weggeworfene Aggregat-Query pro Navigation.

Bewusst **hinter keinem** Autorisierungsfilter: der Endpoint berichtet, ob man sie passieren würde. `CanViewUsageStatsAsync` wird nur aufgerufen, wenn `CanManageChannelAsync` schon `false` war — der Unterschied zwischen beiden *ist* der 7TV-Editor-Zweig, und den in den Request-Pfad jedes Managers zu ziehen hätte mehr gekostet als der Endpoint spart.

**Ergänzung gegenüber dem Report:** Payload trägt `isTracked` und `isBotActive`. Ohne Letzteres wäre beim Ablösen der Sonden der Reaktivieren-Button verschwunden, den Welle B eingeführt hat (ein Leave deaktiviert nur noch).

**Abweichung:** kein `isSevenTvEditor`-Feld. Es hat heute keinen Konsumenten und würde die obige Abkürzung zunichte machen — kommt mit seinem ersten Konsumenten.

Der Guard klärt jetzt zuerst den Login (`ensureLoaded`, Muster von `voteSessionAccessGuard`), damit sein Aufruf nicht 401t und dabei mit dem Redirect des Interceptors um dieselbe Navigation konkurriert.

### S3-31 / Zielkonflikt Z4 — Gruppen-Filter, und der Service als Wahrheit

**Nutzerentscheidung: Gruppen-Filter, kein Route-Constraint.** `ChannelNameValidationFilter` läuft als **erster** Filter jeder der vier Gruppen, vor den Autorisierungsfiltern; diese Reihenfolge *ist* der Fehlercode-Vertrag (400 mit `invalid_channel_name`, nicht 403 und nicht 404). Ein Constraint `{channelName:regex(...)}` wäre kürzer, liefert aber 404 ohne Body — und könnte nicht normalisieren, hätte also gemischt geschriebene Logins wie `HandOfBlood` abgelehnt.

Zehn inline-Prüfungen entfallen; die fünf channel-scoped Endpoints, die gar keine hatten (`end`, `delete`, `results`, Vote setzen, Vote zurücknehmen), sind mit abgedeckt. Der Filter verlangt keinen `channelName`, er prüft ihn nur, wenn einer da ist — `GET /api/channels` und `/api/channels/mine` liegen in derselben Gruppe und gehen unberührt durch. Die Prüfung in den vier Autorisierungsfiltern (S3-3, Welle B) bleibt bewusst stehen: letzte Instanz vor dem ersten Redis-Key, und sie gilt weiter für einen künftigen Endpoint, der außerhalb einer validierten Gruppe registriert wird.

Zweiter Teil: `CreateAsync` gibt `CreateVoteSessionResult` zurück (Muster von `VoteCastResult`), der Endpoint mappt nur noch. Die 24 Zeilen Vorab-Validierung sind weg, die vier `ArgumentException`-Regeln im Service — vorher unerreichbar — sind der einzige Ort. Vorher divergierten die Ausfallmodi: eine fünfte Regel nur im Service ergab eine 500 statt einer 400, nur im Endpoint einen für jeden anderen Aufrufer durchlässigen Service. `VoteSessionLimits.MaxBackdateDays` liegt jetzt in Core, damit die Zahl, die der Endpoint zurückmeldet, nicht von der abweichen kann, die der Service durchsetzt.

### S3-30 — `rxResource`-Pilot auf `VoteSessionListPage`

Seite nach Nutzerentscheidung: zwei Parameter, und keine RxJS-Poll-Nachbarschaft wie auf der Usage-Stats-Seite. `params: () => ({ channel, page })` ersetzt `effect(() => this.load())` und löst denselben Grund mit, aus dem der Effect existierte (NG0950 beim Lesen eines Route-Inputs im Konstruktor) — ohne dessen Falle, dass jeder weitere Signal-Read in `load()` still zum Reload-Trigger wird.

**Verhaltensänderung, bewusst:** `create` und `delete` laden jetzt neu statt die Liste optimistisch zu patchen. Beides verschiebt die Paginierung; die alte Variante konnte eine Seite auf `pageSize + 1` Zeilen wachsen lassen oder eine neue Session auf Seite 3 anzeigen, wo sie nicht hingehört. `end` patcht weiterhin lokal — In-Place-Feldänderung an einer sichtbaren Zeile, keine Paginierungswirkung. Fehler aus Aktionen liegen in einem eigenen Signal, damit ein fehlgeschlagenes Löschen nicht vom nächsten erfolgreichen Listen-Reload weggewischt wird und umgekehrt.

Die anderen vier Loader-`effect()`s bleiben unverändert, bis dieser Pilot sich getragen hat.

### S3-33 — alle drei Flags aktiviert, vorher gemessen

`tsc --strict` über `tsconfig.app.json` **und** `tsconfig.spec.json`: **0 Fehler**. Ein voller Produktions-`ng build` mit zusätzlich `strictTemplates` + `typeCheckHostBindings`: **0 Fehler**, Build grün. Damit war die im Report offengelassene Frage („erst zählen, dann entscheiden") beantwortet, bevor der Schalter umgelegt wurde — alle drei sind gesetzt. `strict` hat sofort etwas gefangen: einen falsch typisierten `vi.fn()`-Mock im neuen Interceptor-Spec.

### S4-11 — `IWorkerHealthReader`

Der einzige Ort, an dem die Api an ihrem eigenen Service-Layer vorbei auf Infrastruktur zugriff: `IConnectionMultiplexer` direkt im Handler, der Key `"worker:health:twitch"` als String-Literal, das Payload in ein privat definiertes Record deserialisiert — dasselbe Wire-Format zweimal deklariert, in Api und Worker, ohne Verbindung (die Projekte referenzieren sich nicht). Jetzt besitzt `WorkerHealthKeys` Key und TTL, `WorkerHealthSnapshot` (Core) das Format, und beide Seiten serialisieren denselben Typ. `grep StackExchange.Redis src/EmotePurge.Api` findet danach nichts mehr; die Schichtentabelle ist auf der Api-Zeile verstoßfrei.

### S3-6 Teil 1 — `ReconnectPolicy` extrahiert

`src/EmotePurge.Worker/ReconnectPolicy.cs`: TwitchLib-frei, ohne Uhr (die verstrichene Zeit wird hereingegeben), mit `Decide(TimeSpan?) → {Reconnect, Recreate, Wait}` plus den beiden `Interlocked`-Zählern und den Registrierungsmethoden für die Event-Handler. Die Fallunterscheidung ist zeichengleich zur vorherigen Fassung übernommen und einzeln gegengeprüft (Fehlerstreak ≥ 3 → Recreate; kein Open aktiv → Reconnect; Open < 10 min → Wait; Open ≥ 10 min → Recreate; unbekannte Laufzeit zählt als „gerade gestartet"). `TwitchChatManager` behält nur den Transport und ist um die beiden Zählerfelder und zwei Konstanten leichter. Die Tests dazu sind Welle D.

### S3-27 — Doku-Umbau

Die 72 Log-Einträge sind **wortgleich** nach `docs/DECISIONS.md` verschoben, absteigend nach Datum, jeder mit `### <Datum> — <Titel>` und einer neu ergänzten `**Betrifft:**`-Zeile (macht das Log per `grep <dateiname> docs/DECISIONS.md` durchsuchbar). Verifiziert per Skript: jede der 72 Original-Zeilen kommt zeichengleich in der neuen Datei vor, 0 Verluste. Die 27 Einträge aus der Anfangsphase sind **nicht** nachträglich datiert worden und stehen am Ende — ein geschätztes Datum wäre schlechter als keins. Dass der erste Absatz jedes Eintrags seinen Titel nun ein zweites Mal nennt, ist der Preis dafür, den Originaltext nicht anzufassen.

`CLAUDE.md`: 96 KB → **14,3 KB**. Überblick, Umsetzungsstand als Sechs-Zeilen-Tabelle statt 7,7-KB-Absatz, Commands, die Schichtentreue-Tabelle aus Anhang E, 17 geltende Regeln als imperative Liste, Sprachkonvention, und ein kurzer Abschnitt „Bekannte offene Grenzen" (JOIN-Limit, fehlendes Refresh-Token).
**Abweichung:** Der Report nannte ≤ 12 KB. 14,3 KB ist das Ergebnis, wenn man die Commands vollständig behält und die Regeln so ausschreibt, dass sie ohne Nachschlagen anwendbar sind — weiter zu kürzen hätte Inhalt gekostet, nicht Redundanz.

`Architectur.md`: 337 → 214 Zeilen, kürzer trotz mehr Inhalt, weil die gespiegelten YAML- und C#-Blöcke wegfielen. Korrigiert: „`User`/`VoteSession`/`Vote` noch nicht implementiert" (seit 2026-07-25 falsch), der nicht mehr existierende Guard und „Voting bewusst ohne Login-Zwang" (am 2026-07-27 umgekehrt), „WebSocket"-Recovery in Grundsatz 3, Abschnitt 6 in **6a Lokal** / **6b Produktion** geteilt mit Unterschieds-Tabelle statt zweier YAML-Kopien, und Modul D um i18n, Fehlercode-Vertrag und Pagination ergänzt. Der Sub-Agent hatte dabei genau einen Fehler in der Tabelle („lokal ohne `maxmemory`-Limit" — beide Compose-Dateien setzen es); korrigiert und als bewusste Nicht-Abweichung ausformuliert. Zwei Stellen hat er über den Auftrag hinaus richtig mitkorrigiert (`sync-deleted` hängt an `UsageStatsAccessAuthorizationFilter`, der Ergebnis-Endpoint an `VoteAudienceFilter`).

Zusätzlich zwei neue Log-Einträge in `docs/DECISIONS.md` (Regel: ein Commit, der eine Konvention ändert, enthält seinen Eintrag): der Welle-C-Eintrag selbst, und ein **Nachtrag am 2026-07-28-Eintrag**, der die dort dokumentierte Begründung „Validierung bewusst als Helfer im Handler-Body, nicht als `IEndpointFilter`" ausdrücklich für überholt erklärt — genau wie Z4 es verlangt, als datierter Nachtrag statt als Umschreiben des historischen Texts.

### Verifikation

| Prüfung | Ergebnis |
|---|---|
| `dotnet build EmotePurge.slnx` | grün, 0 Warnungen |
| `dotnet test EmotePurge.slnx` | 47/47 grün (unverändert — Welle C ändert kein getestetes Verhalten) |
| `ng build --configuration production` | grün, **mit** `strict` + `strictTemplates` + `typeCheckHostBindings` |
| `npm --prefix web test -- --watch=false` | 110/110 grün (90 vorher: +6 Interceptor, +11 Fehler-Mapping, +1 `getPermissions`, +2 Guard-Fälle) |
| `npm --prefix web run e2e` | 4/4 grün |
| Locale-Parität de/en | 201/201 Keys, keine Lücke in beiden Richtungen (197 vorher: +7 `errors.status.*`, −4 ungenutzte `voting.detail.errors.*`) |
| `docker compose up -d --build` | Stack läuft, Worker verbindet und joint alle Channels inkl. 7TV-Sync |
| Smoke-Test | `GET /api/worker/health` liefert über den neuen `IWorkerHealthReader` `{"status":"connected"}`; `/permissions` ohne Cookie `401`; unbekannter `/api/*`-Pfad weiterhin `404`, SPA-Shell `200` |

**Noch nicht live gegen echte Twitch-/7TV-Accounts getestet.** Besonders prüfenswert:

- **Der Fehlercode-Vertrag mit Cookie.** Ein malformierter Channel-Name in der URL muss **400** mit `invalid_channel_name` liefern, nicht 403 — ohne Cookie greift die Auth-Middleware vorher, der Fall ist per curl also nicht prüfbar. Er hängt daran, dass ASP.NET Core Gruppen-Filter vor Endpoint-Filtern ausführt; das ist dokumentiertes Framework-Verhalten, aber hier zum ersten Mal tragend für einen Fehlercode.
- **Der Interceptor an einer echten ablaufenden Session** (401 → Redirect auf `/login`), und dass ein 7TV-401 im Löschlauf **nicht** ausloggt.
- **Die 403-Meldungen**: ein Nicht-Manager auf einer Aktion sollte jetzt den Mod-Cache-Hinweis sehen statt „Etwas ist schiefgelaufen".
- **`/permissions`** für alle vier Rollen (Admin, Broadcaster, Mod, 7TV-Editor) — insbesondere, dass der „Nutzung"-Tab für einen reinen 7TV-Editor weiter erscheint und der Reaktivieren-Button bei `isBotActive: false`.
- **Die Vote-Session-Liste** (rxResource): Seitenwechsel, Erstellen, Beenden, Löschen — Letztere laden jetzt neu statt lokal zu patchen.

---

## Welle D — umgesetzt am 2026-08-02

Vier Befunde (S3-5, S3-6 Teil 2 und 3, S3-28, S3-29), dazu die Nachpflege dieses Dokuments: drei der in der Welle-D-Zeile als offen geführten Punkte waren längst erledigt und nur nie nachgetragen worden — der Struktur-Review vom 2026-08-01 hat das als Nebenbefund festgehalten. Alles von Hand geschrieben, Fix für Fix; zwei Sonnet-Sub-Agenten haben ausschließlich gelesen (Doku-Extrakte, Bestandsaufnahme der Testdateien), nichts davon hat Code angefasst.

Die Reihenfolge folgt dem Risiko: erst die Autorisierungspfade, dann die Filter-Matrix darüber, zuletzt die Struktur-Tests. **Ein echter Produktionsfehler ist dabei nicht aufgefallen** — die vier Autorisierungs-Services verhalten sich in jedem geprüften Fall so, wie ihre Kommentare es behaupten, einschließlich des `?? false` aus S3-5, das der Report als Ein-Zeichen-Falle benannt hatte. Was aufgefallen ist, sind drei Abweichungen zwischen Report und Bestand und ein nicht offensichtliches Framework-Verhalten; alle vier stehen unten.

### S3-5 — der Entscheidungskern der Autorisierung hatte kein Testpendant

`tests/EmotePurge.Infrastructure.Tests/Unit/ChannelAccessServiceTests.cs` (13 Fälle) und `Unit/ModeratorCheckServiceTests.cs` (7) sind container-frei wie vom Report vorgesehen; `Integration/VoteEligibilityServiceTests.cs` (22) und `Integration/MyChannelsServiceTests.cs` (10) nicht.

Die wertvollste Zeile der ganzen Welle ist `CanViewUsageStatsAsync_DeniesAccess_WhenSevenTvCannotAnswer`. Der Report hatte den Fall als Ein-Zeichen-Falle beschrieben — `?? false` gegen ein übersehenes `?? true` in der Grants-Verzweigung —, und genau daran hängt, ob ein 7TV-Ausfall die Usage-Stats jedes Channels für jeden eingeloggten Nutzer öffnet. Der Bestand ist korrekt; ab jetzt ist er es nachweislich. Daneben stehen die beiden Isolationsfälle, die der Report nicht nennt: ein 7TV-Editor-Grant für Channel B darf Channel A nicht aufschließen, auch wenn A's Login zufällig in der Login-Menge liegt (gematcht wird auf der unveränderlichen Twitch-ID, wo eine da ist), und der Rename-Squatting-Schutz aus Welle B — Login stimmt, hinterlegte Twitch-ID nicht — wird verweigert statt durchgelassen.

**Abweichung:** Zwei der vier Testklassen liegen in `Integration/`, nicht in `Unit/`. Die Report-Aussage „kein Container nötig, alle Abhängigkeiten sind Interfaces bzw. `IConfiguration`" trägt für `ChannelAccessService` und `ModeratorCheckService`, aber nicht für `VoteEligibilityService` und `MyChannelsService`: beide nehmen `AppDbContext` im Konstruktor und laden ihre Channel-/Session-Daten selbst. Nach Regel 11 gehören sie damit in `Integration/` mit der vorhandenen Postgres-Fixture — alles außer der Datenbank ist auch dort substituiert.

**Abweichung:** Der Report verlangt für `ModeratorCheckService` den Fall „`principal.AccessToken is null` → `false` ohne Helix-Call". Diese Zeile gibt es seit dem Refresh-Token-Umbau vom 2026-07-30 nicht mehr — der Service fragt `ITwitchUserTokenService.GetValidAccessTokenAsync`, das den Claim-Token bedient, solange er gilt, und danach serverseitig erneuert. Der Fall ist deshalb als „der Token-Service liefert auch nach dem Refresh-Versuch keinen Token" getestet, was dieselbe Invariante an der heutigen Stelle prüft. Die beiden Nicht-Cachen-Fälle sind wörtlich wie im Report umgesetzt: weder ein fehlender Token noch ein Helix-`null` darf in den Cache, sonst sperrt ein transienter Twitch-Ausfall alle Mods für die volle TTL aus.

**Über den Report hinaus:** `VoteEligibilityServiceTests` nagelt die kontraintuitivste Regel des Projekts fest, die bisher ausschließlich in einem Kommentar lebte — `EvaluateAsync` lehnt eine beendete Session ab, `EvaluateAudienceAsync` nicht. Beide Methoden teilen sich ihre gesamte Rollenauswertung, der Unterschied ist ein einzelnes vorgezogenes `return`; verschöbe es sich, würde jede abgeschlossene Session ihr eigenes Publikum von der Ergebnisseite werfen, ohne dass irgendetwas rot wird. Ebenfalls über den Report hinaus: die Rollenmatrix ist als `[Theory]` über alle `AllowedRoles`-Kombinationen ohne `Everyone`/`Subs` ausgeführt, inklusive `VIPs` — die dokumentierte Lücke (kein Helix-Self-Check für VIPs) ist damit als Verhalten festgeschrieben statt als Fußnote.

### S3-6 Teil 2 und 3 — die Filter-Matrix, und ein drittes Testprojekt

`tests/EmotePurge.Api.Tests/` (39 Fälle, container-frei, Laufzeit unter einer Sekunde). Teil 1 (`ReconnectPolicy` extrahieren) war Welle C, Teil 2 (Unit-Tests dafür und für `EmoteUsageCounter`) am 2026-07-30 erledigt; offen war der dritte: „für die Api ein einziger `WebApplicationFactory`-Test, der die Filter-Matrix (401/403/404/409) über alle Filter-Pfade abfährt".

**Nutzerentscheidung: jetzt bauen statt vertagen.** Das bedeutet ein drittes Testprojekt und mit `Microsoft.AspNetCore.Mvc.Testing` das erste neue Testpaket seit NSubstitute. `AuthFilterMatrixTests` fährt die echte Route-Tabelle ab — nicht nachgebaute Endpoints —, weil die Hälfte des Werts darin liegt, dass ein Filter am *richtigen* Endpoint hängt. Belegt sind: 401 für jede der 14 geschützten Routen anonym; 401 statt 403, wenn die Session zwar authentifiziert ist, aber die `twitch:login`-Claim fehlt (das Problem des Aufrufers ist seine Session, nicht seine Berechtigung); 403 aus allen vier Autorisierungsfiltern; 404 mit `vote_session_not_found` und 409 mit `vote_session_ended` aus `VoteEligibilityFilter`; und die eine Switch-Arm-Differenz, um die es dem Report ging — die Ergebnis-Route ruft nachweislich `EvaluateAudienceAsync` und nachweislich **nicht** `EvaluateAsync`.

Zwei Registrierungs-Eigenheiten sind mitfestgeschrieben, weil beide leicht als Fehler zu lesen sind: `UsageStatsAccessAuthorizationFilter` hängt trotz seines Namens auch an der Emote-Gruppe, und die Admin-Gruppe hat **keinen** `ChannelNameValidationFilter`, antwortet auf einen malformierten Namen also 403 statt 400.

**Über den Report hinaus:** Die Welle C hatte unter „noch nicht live getestet" notiert, der Fehlercode-Vertrag „malformierter Channel-Name ⇒ 400 `invalid_channel_name`, nicht 403" sei per curl nicht prüfbar, weil ohne Cookie die Auth-Middleware vorher greift. Genau dieser Fall ist jetzt als Test da — inklusive der Gegenprobe, dass 401 die 400 schlägt, und dass `HandOfBlood` durchkommt, weil der Filter normalisiert (ein Route-Constraint hätte das abgelehnt; das war der Grund für Z4).

**Bewusst in Kauf genommene Nebenwirkung:** Zwei Zugeständnisse an die Testbarkeit im Produktionscode. `Program.cs` bekommt am Ende ein `public partial class Program;`, weil Top-Level-Statements sonst eine interne `Program`-Klasse erzeugen, die `WebApplicationFactory<T>` nicht erreicht. Und `EmotePurge.Api.csproj` bekommt `InternalsVisibleTo` für das Testprojekt — die Alternative wäre gewesen, `ApiErrorCodes`-Literale wie `"vote_session_ended"` im Test abzutippen, womit ein umbenannter Code den Test nicht mehr bricht, sondern ihn still gegen einen Wert prüfen ließe, den niemand mehr zurückgibt. Beides erweitert die Oberfläche der Api-Assembly minimal; der Gegenwert ist, dass die Tests die echte Pipeline fahren statt einer Kopie davon.

*Nachtrag 2026-09-16:* Die `public partial class Program;`-Zeile ist seit Commit `fa634e8` entfernt (Sonar/ASP0027 — .NET 10 erzeugt die Klasse per Source Generator auch ohne die explizite Deklaration). Das zweite Zugeständnis, `InternalsVisibleTo`, besteht unverändert fort. Details in `docs/DECISIONS.md`, Eintrag „`Program.cs` drops the explicit `public partial class Program;`" (2026-09-16).

**Über den Report hinaus, und der interessanteste Fund der Welle:** `RequestDelegateFactory` löst die injizierten Services eines Handlers auf, **bevor** die Endpoint-Filter-Pipeline läuft. Ein Request, den ein Filter gleich mit 403 abweist, konstruiert also trotzdem den gesamten Service-Graph des Handlers — und mehrere dieser Services nehmen `IConnectionMultiplexer`, dessen Registrierung `ConnectionMultiplexer.Connect` sofort ausführt. In der ersten Fassung liefen sieben Fälle deshalb zwölf Sekunden lang in einen Redis-Timeout und antworteten 500 statt des erwarteten Codes. In Produktion ist das folgenlos (der Multiplexer ist ein längst verbundener Singleton), aber es ist eine Eigenschaft, die man kennen muss, bevor man einen Handler mit einer teuren Abhängigkeit hinter einen Filter hängt. Die Testfactory substituiert deshalb `IConnectionMultiplexer` und lässt den restlichen Service-Graph echt.

**Testanpassung:** `ApiFactory` ist eine `IClassFixture`, also über alle Tests der Klasse geteilt — die Substitutes sammeln damit die Aufrufe der Nachbartests ein, und die `DidNotReceive()`-Zusicherung der Audience-Filter-Prüfung war dadurch zunächst falsch grün bzw. falsch rot. Der Konstruktor der Testklasse setzt die aufgezeichneten Aufrufe jetzt pro Test zurück.

**Nicht umgesetzt und bewusst so:** `TwitchChatManager` und `SevenTvEventClient` bekommen weiterhin keine Tests gegen Fakes (Regel 11) — die Reconnect-*Entscheidung* liegt seit Welle C TwitchLib-frei in `ReconnectPolicy` und ist dort getestet, der Transport bleibt live verifiziert.

### S3-28 — der Worker greift nicht mehr an der Schicht vorbei, und die Regel ist jetzt ein Test

`src/EmotePurge.Core/Services/IChannelService.cs`, `src/EmotePurge.Infrastructure/Services/ChannelService.cs`, `src/EmotePurge.Worker/Worker.cs`, `src/EmotePurge.Worker/SevenTvPeriodicResyncWorker.cs`, `tests/EmotePurge.Infrastructure.Tests/Unit/CoreAssemblyReferenceTests.cs`.

**Nutzerentscheidung: der Code-Teil gehört mit in diesen Auftrag**, obwohl er streng genommen Architektur und nicht Test ist. Beide Hosted Services schrieben zeichengleich dieselbe Query gegen `AppDbContext`; sie liegt jetzt als `IChannelService.ListActiveChannelNamesAsync` in der Infrastructure, mit `AsNoTracking` (der Resync-Worker führt sie jede Minute für immer aus) und stabiler Sortierung. Damit fällt die Fußnote „aktuell noch 2 Verstöße" aus der Schichtentreue-Tabelle in `CLAUDE.md` weg, und beide `using`-Direktiven auf `EmotePurge.Infrastructure.Persistence` sind aus dem Worker verschwunden — der Worker kennt EF Core nicht mehr.

Der Struktur-Test ist reines Reflection wie im Report vorgeschlagen, kein NetArchTest: `typeof(Channel).Assembly.GetReferencedAssemblies()` darf nichts enthalten, das mit `Microsoft.EntityFrameworkCore`, `StackExchange.Redis`, `Microsoft.AspNetCore` oder `System.Net.Http` beginnt. Aus der Kernregel des Projekts wird damit ein roter Build statt eines Review-Kommentars, und die Assert-Message nennt die verletzende Assembly beim Namen.

**Über den Report hinaus:** ein zweiter Fall im selben Test — Core darf auch kein *Geschwisterprojekt* referenzieren. Das ist die Gegenrichtung derselben Regel: eine Referenz auf `EmotePurge.Infrastructure` würde die Abhängigkeitsrichtung umdrehen, ohne eine der vier verbotenen Technologien zu nennen, und wäre vom ersten Fall nicht erfasst.

### S3-29 — war bereits erledigt, nur nie nachgetragen

`web/src/app/core/i18n/api-error.spec.ts` existiert samt beidseitigem Abgleich zwischen `KNOWN_API_ERROR_CODES` und den `errors.api.*`-Keys beider Locale-Dateien. Der Befund stand trotzdem noch als offener Struktur-Test in der Welle-D-Zeile; die Durchstreichung dort ist mit diesem Commit nachgeholt. Teil 2 des vorgeschlagenen Fixes — die zwei Health-Konstanten in eine eigene `WorkerHealthReasonCodes`-Klasse zu verschieben — bleibt offen und steht unten unter „Was noch offen ist".

### Verifikation

| Prüfung | Ergebnis |
|---|---|
| `dotnet build EmotePurge.slnx` | grün, 0 Warnungen |
| `dotnet test EmotePurge.slnx` | 383/383 grün (284 vorher: +13 `ChannelAccessService`, +7 `ModeratorCheckService`, +22 `VoteEligibilityService`, +10 `MyChannelsService`, +5 `CastVoteAsync`, +2 Struktur, +1 `ListActiveChannelNames`, +39 neues Api-Projekt) |
| Aufteilung | `EmotePurge.Infrastructure.Tests` 306 (13 s, Testcontainers) · `EmotePurge.Worker.Tests` 38 (0,5 s) · `EmotePurge.Api.Tests` 39 (0,9 s) |
| `dotnet format EmotePurge.slnx --verify-no-changes` | grün |
| Struktur-Gegenprobe | `CoreAssemblyReferenceTests` schlägt fehl, sobald `EmotePurge.Core` eine der vier Technologien referenziert — die Regel ist ab jetzt ein CI-Gate (`publish.yml` fährt die Solution, das neue Projekt läuft automatisch mit) |
| Redis-/Postgres-Freiheit der Api-Tests | belegt: die Suite läuft mit gestopptem Docker durch, 39/39 in 0,9 s |

**Frontend-Suiten nicht gelaufen** — diese Welle fasst `web/` nicht an. Der einzige Frontend-Bezug (S3-29) war bereits erledigt.

**Live-Verifikation nach Regel 16 steht aus.** Der Code-Teil von S3-28 verändert Produktionsverhalten: beide Hosted Services holen ihre Channel-Liste jetzt über den Service statt über den DbContext. Vor dem Deploy zu prüfen: Boot-Recovery joint nach einem Neustart weiterhin alle aktiven Channels, und der periodische Resync läuft im Minutentakt ohne neue Warnungen im Log.

---

## Was noch offen ist

### Direkte Anschlussarbeiten aus den Wellen A und B

Diese Punkte sind Teil eines Befunds, dessen Rest bewusst einer späteren Welle zugeordnet ist:

- ~~**S2-16 (Auswahl über Filterwechsel erhalten)** ist durch die keyed `ListSelection` jetzt sicher baubar — aber nicht automatisch fertig: beide Host-Seiten leiten ihre Löschliste über `selectedItems()` ab, sodass eine über einen Filterwechsel erhaltene Auswahl beim Löschen still auf die sichtbaren Zeilen zusammenschrumpfen würde. Die Richtung ist ungefährlich (es würde weniger gelöscht, nie das Falsche), widerspräche aber der vom Befund geforderten Anzeige „50 ausgewählt (12 ausgeblendet)". Wer S2-16 baut, muss die Zählung auf `selectedKeys()` umstellen und eine Key→Zeile-Zuordnung für ausgeblendete Einträge mitführen.~~ Am 2026-07-30 in Welle 1 der UI/UX-Überarbeitung erledigt — bewusst als **Prune-Variante** (`retainVisible()`: sichtbar Bleibendes überlebt den Filterwechsel, Weggefiltertes fliegt aus der Auswahl), nicht als „n ausgewählt (m ausgeblendet)"-Zählung: `selectedKeys` bleibt so die autoritative Menge für den Löschpfad, ein unsichtbar-ausgewähltes Emote kann dort nie landen. Begründung im DECISIONS-Eintrag „`SegmentedControl` … Filterwechsel beschneiden die Auswahl" (2026-07-30).
- ~~**`ReconnectPolicy` extrahieren (S3-6)**~~ — in Welle C erledigt. ~~Die Unit-Tests dafür bleiben Welle D.~~ Am 2026-07-30 mit der 7TV-WebSocket-Wiedereinführung erledigt: neues container-freies Testprojekt `tests/EmotePurge.Worker.Tests` mit `ReconnectPolicyTests` (die fünf dokumentierten Entscheidungsfälle), s. docs/DECISIONS.md „Worker-Testprojekt eingeführt".
- **Redis-Cache für `OnValidatePrincipal`** — der Widerruf kostet einen Primärschlüssel-Lookup pro authentifiziertem Request. Sauber, aber cachebar, falls es je auffällt.
- **S3-29 Teil 2** — die Paritätsprüfung steht (Welle D), aber `NoHealthData` und `HealthDataUnreadable` teilen sich weiterhin `ApiErrorCodes` mit den echten Fehlercodes, obwohl sie zu einem anderen Vertrag gehören (`reasonCode` in einem 200-Body). Der Report schlägt eine eigene `WorkerHealthReasonCodes`-Klasse vor; der Ausfallmodus ist heute nur Verwirrung, kein Fehler.
- **S3-30 restliche ~~vier~~ drei Loader-`effect()`s** — der Pilot auf `VoteSessionListPage` steht (Welle C); ~~`my-votings-page.ts`~~ (am 2026-07-30 in Welle 2 der UI/UX-Überarbeitung auf `rxResource` nachgezogen, Commit `21cb1c0`), `channel-workspace-layout.ts`, `usage-stats-page.ts` und `vote-session-detail-page.ts` laden weiter über `effect()` bzw. den Konstruktor. Erst nachziehen, wenn der Pilot sich im Live-Betrieb getragen hat. Bei der Usage-Stats-Seite ist zusätzlich der 7TV-Sync-Poll in dieselbe Ressource einzuweben — der aufwändigste der verbleibenden drei.
- ~~**S3-16 `@angular/cdk/dialog`** (mittelfristig) — Fokusfalle/Escape sind nachgerüstet, aber weiter handgebaut.~~ Am 2026-07-30 in Welle 2 der UI/UX-Überarbeitung erledigt: alle Dialoge (Mass-Delete-Confirm, 7TV-Token-Prompt, neuer generischer `ConfirmDialog` statt der zwei verbliebenen `window.confirm`) laufen über `Dialog.open()`; die zwei live gefundenen CDK-Fallen (Overlay-Container erbt keine Textfarbe; Laufzeit-`<style>` schlägt gleich-spezifische Regeln) stehen im DECISIONS-Eintrag vom 2026-07-30.
- **S4-3 `style-src` ohne `unsafe-inline`** — braucht `ngCspNonce`, mit `MapFallbackToFile("index.html")` nicht ohne Weiteres möglich; im Report ausdrücklich kein Blocker.
- ~~**S3-17 Restfall** — `my-votings-page.ts` hat denselben `text-slate-500`-Statustext, war im Report aber nicht als Fundort genannt und blieb unangetastet. Trivialer Nachzug.~~ Mit dem `rxResource`-Umbau von `my-votings-page.ts` am 2026-07-30 miterledigt (Seite komplett neu geschrieben, Statustexte auf `text-slate-400`).
- **`isSevenTvEditor` im `/permissions`-Payload** — bewusst weggelassen (kein Konsument, und es würde die Kurzschluss-Optimierung zunichte machen). Wer es braucht, ergänzt es zusammen mit dem Konsumenten.

### Offenes Skalierungsthema: Twitchs JOIN-Limit ab ~20 Channels

Kein Befund des Reports — beim Auswerten eines Prod-Logs am 2026-07-30 aufgefallen und am TwitchLib-Quelltext nachgerechnet.

Twitch erlaubt einer nicht-verifizierten Verbindung **20 JOINs pro 10 Sekunden**. TwitchLib drosselt JOINs überhaupt nicht: sie gehen an `ThrottlingService` vorbei (der deckt nur Chat-Nachrichten ab), und `QueueingJoinCheckAsync` schickt den nächsten, sobald `Handle366` die Bestätigung des vorherigen meldet — auf Prod gemessen ~180 ms pro Channel. 20 Channels am Stück liegen damit exakt auf der Grenze, darüber reißt jeder Reconnect sie.

**Was dann passiert** (am Code belegt, nicht vermutet): Bleibt die Bestätigung aus, verfällt der Eintrag nach 5 s, `OnFailureToReceiveJoinConfirmation` feuert, der Channel fliegt aus TwitchLibs `_joinedChannelManager` — und **TwitchLib versucht es von sich aus nicht erneut**, es rejoint ihn auch beim nächsten Reconnect nicht mehr. Der Durchsatz fällt dabei auf etwa einen Channel alle 5–6 s, das System drosselt sich unter Last also selbst, statt in eine Schleife zu laufen. Die betroffenen Channels erfassen aber nichts, bis `EnsureJoinedAsync` sie im Minutentakt nachholt — ohne dieses Netz aus Welle B wären sie dauerhaft stumm geblieben.

**Umgesetzt am 2026-07-30:** Alle Join-Pfade, die wir selbst auslösen (Boot-Recovery, Rejoin nach einem Recreate, Redis-Join-Kommandos, die `EnsureJoinedAsync`-Runde), laufen über eine gemeinsame Drossel in `TwitchChatManager.TryJoinAsync` — 600 ms Mindestabstand, also ~16 JOINs pro 10 Sekunden. Ein einzelner Join aus dem UI wird dadurch nicht spürbar verzögert, solange der vorherige lange genug her ist.

**Nicht gelöst und vor einem größeren Ausbau zu entscheiden:** TwitchLibs eigener Rejoin nach einem Reconnect läuft innerhalb der Bibliothek und lässt sich nicht drosseln. Zwei Auswege:
- **Verifizierter Bot-Account** (2.000 JOINs/10 s statt 20) — vom Nutzer als vorgesehene Richtung benannt (2026-07-30). Bedeutet: echter Twitch-Account statt anonymer `justinfan`-Verbindung, OAuth-Token für den Bot, `ConnectionCredentials` mit Nick+Token statt parameterlos, und ein Verifizierungsantrag bei Twitch. Macht nebenbei das Senden von Nachrichten möglich, was heute nicht geht.
- **Sharding** über mehrere `TwitchClient`-Instanzen. Ungeklärt und nur empirisch beantwortbar: ob Twitch anonyme Verbindungen pro `justinfan`-Kennung limitiert (dann vervielfacht Sharding das Budget) oder pro IP (dann bringt es nichts).

Billigster Vorabtest: lokal ~25 Channels tracken und prüfen, ob `Twitch hat den Join für … nicht bestätigt` auftaucht.

**Vorabtest durchgeführt am 2026-07-30, Ergebnis: die Grenze biss nicht.** Aufbau: 25 Test-Channels zusätzlich zu 3 bestehenden lokal geseedet (28 aktiv), dann zwei Szenarien. (1) Gedrosselter Pfad (`EnsureJoinedAsync`): alle 25 im 600-ms-Takt sauber gejoint, wie erwartet. (2) Der eigentlich riskante Pfad, provoziert über einen 6,5-minütigen Netz-Cut des Worker-Containers: Der `TwitchConnectionWatchdog` feuerte bei 303 s Stille, `ForceReconnectAsync` → TwitchLibs `ReconnectAsync`, und nach Netz-Rückkehr lief **TwitchLibs interner, ungedrosselter Rejoin: 28 JOINs in 5,0 s (~200 ms Abstand, Log-Timestamps 17:47:13–17:47:18)** — deutlich über den dokumentierten 20 JOINs/10 s. Trotzdem **0× `OnFailureToReceiveJoinConfirmation`**; 27 Channels wurden im Burst bestätigt, einer 27 s später ohne Fehlversuch. Die dokumentierte 20er-Grenze wurde bei 28 Channels auf einer anonymen Verbindung also empirisch nicht durchgesetzt (Einzelmessung von einer Wohn-IP; ob Twitch weich drosselt, das Bucket größer ist oder anonyme Verbindungen anders behandelt werden, bleibt offen). Nebenbefund: Der 6,5-Minuten-Totalausfall (Postgres, Redis, 7TV, Twitch gleichzeitig weg) wurde vollständig selbst geheilt — Watchdog-Reconnect, 7TV-Backoff bis 70 s ohne Eskalation, Gap-Filling-Vollsync nach Reconnect, keine Container-Restarts, kein Datenverlust-Fenster über die 30-s-Flush-Grenze hinaus. **Konsequenz:** Der verifizierte Bot-Account bleibt die richtige Richtung vor einem Ausbau deutlich über ~30 Channels, ist aber weniger dringlich als angenommen; die nächste Messung lohnt erst bei realer Channel-Zahl ≥ 30 oder nach einem beobachteten Prod-Fehlversuch.

### Offene Wellen

**Welle D — Tests.** ~~In dieser Reihenfolge, die ersten beiden ohne Container:~~ ~~`ChannelAccessServiceTests`~~ → ~~`VoteEligibilityServiceTests`~~ → ~~`VoteSessionService.CastVoteAsync`~~ → ~~`SevenTvSyncService`~~ (am 2026-07-30 mit der WS-Wiedereinführung angelegt: `SevenTvSyncServiceTests`, 11 Fälle inkl. Delta-Pfad — Achtung, `SyncChannelAsync` hat seitdem eine andere Signatur als im Report beschrieben) → ~~`UsageStatFlushService`~~ (existierte längst: `UsageStatFlushServiceTests`, 8 Fälle — nie nachgetragen) → ~~`ReconnectPolicy`~~ (2026-07-30 erledigt, s. o.)/~~`EmoteUsageCounter`~~ (existierte längst: `EmoteUsageCounterTests`, 4 Fälle im container-freien `tests/EmotePurge.Worker.Tests` — nie nachgetragen) → ~~zwei Struktur-Tests (Core-Assembly-Referenzen, Fehlercode-Key-Parität beider Locale-Dateien)~~ (der zweite existierte längst als `web/src/app/core/i18n/api-error.spec.ts`, S3-29 — nie nachgetragen).

**Am 2026-08-02 abgeschlossen** — s. Abschnitt „Welle D — umgesetzt am 2026-08-02" oben. Die drei „existierte längst"-Nachträge sind der Nebenbefund, den der Struktur-Review vom 2026-08-01 an dieser Liste gefunden hatte. Zwei Punkte gehörten zu Welle D, standen aber nie in dieser Zeile und sind mit erledigt: die Api-Filter-Matrix (S3-6 Teil 3, jetzt `tests/EmotePurge.Api.Tests`) und die beiden übrigen Testklassen aus S3-5, `ModeratorCheckServiceTests` und `MyChannelsServiceTests`. Die Reihenfolgeangabe „die ersten beiden ohne Container" ist gestrichen, weil sie nicht stimmte: `VoteEligibilityService` nimmt `AppDbContext`.

**Welle E — Infra & Launch.** ~~S2-21 (Ressourcenlimits, vor dem Stresstest)~~ (2026-08-05 erledigt **und noch am selben Abend mit Realdaten kalibriert**: `deploy.resources.limits` 1.0 CPU / 512 M je für `api`/`worker` in beiden Compose-Dateien; Prod-Messung idle vs. 17k-Live-Channel zeigt nur ~10–15 MB Live-Delta — Werte bestätigt, der ursprünglich geplante Stresstest ist verworfen, s. DECISIONS-Eintrag „S2-21-Kalibrierung") → ~~S3-34~~ (2026-08-05 erledigt: `IPendingMigrationGuard` wirft beim Start von Api **und** Worker bei ausstehenden Migrationen — bewusst kein Auto-Migrate; `PendingMigrationGuardTests` gegen echtes Postgres) → ~~S3-38~~ (2026-08-05 erledigt, **abweichend vom Report-Vorschlag auf der Dev-Seite**: `docker-compose.yml` heißt jetzt `emote-purge-dev` mit Containern `emotepurge-dev-*`; die Prod-Datei blieb unangetastet, weil ein neuer Prod-Projekt-/Volume-Name das bestehende `postgres-data`-Volume verwaist hätte — die Api liefe nach dem Redeploy gegen eine leere DB) → ~~Z1-Aufteilung der Health-Endpoints + S3-35~~ (2026-08-05 erledigt: neuer payloadfreier `GET /api/health` — 200 nur bei `connected`, sonst 503, Policy `PublicHealth` —, `curl -f`-HEALTHCHECK im Api-Dockerfile, Datei-Heartbeat `/tmp/alive` + HEALTHCHECK im Worker-Dockerfile; s. DECISIONS-Eintrag) → ~~S3-36~~ (**2026-08-05 abgeschlossen**, in zwei Schritten: der externe Uptime-Check kam mit der VPS-Härtung am 2026-08-04 — Uptime Kuma auf dem NAS, 60-s-Takt, Telegram-Alerts, statt des vorgeschlagenen UptimeRobot. Der vorgeschlagene `healthchecks.io`-Push-Dead-Man's-Switch ist **verworfen**: Kuma ist vom VPS aus unerreichbar (Heimnetz); stattdessen wird der Kuma-Monitor auf `/api/health` umgestellt, dessen 503-Semantik den Pull zum Dead-Man's-Switch macht — **dieser Umstell-Handgriff in der Kuma-UI steht noch aus**. Log-Aggregation bewusst minimal geschlossen: Logs strukturiert, Rotation hostweit seit 2026-08-04, Zugriffsweg in infra-docs dokumentiert. Ebenfalls im Zuge der VPS-Härtung erledigt: die frühere „Offene Frage 15", Docker-Log-Rotation, s. u.**)** → ~~S3-37 (`pull_request`-Trigger)~~ (2026-07-30 erledigt, zusammen mit `paths-ignore` für Doku-only-Pushes — s. DECISIONS-Eintrag „CI: Doku-only-Pushes überspringen die Pipeline") → ~~S4-15~~/~~S4-16~~ (**2026-08-05 komplett**: alle Actions auf Commit-SHA gepinnt; Dependabot für github-actions/npm/nuget, Minor+Patch gruppiert — bewusst ohne `npm audit`-Gate und ohne NuGet-Cache, Begründung im DECISIONS-Eintrag. Der Format-/Lint-Teil war schon am 2026-08-01 erledigt: `prettier --check`, `eslint` und `dotnet format --verify-no-changes` gaten in `publish.yml`) → ~~S4-17~~ (2026-08-05 erledigt: `COPY src/ src/` statt `COPY . .`, tote `build`-Stage entfernt) → ~~S4-18~~ (**2026-08-05 komplett**: README-Teil war seit 2026-08-01 erledigt; LICENSE ist jetzt **AGPL-3.0** — Nutzerentscheidung, s. DECISIONS; das leere `marketing/` existiert nicht mehr, CONTRIBUTING für ein Solo-Repo bewusst verzichtbar) → S2-20 (Rechtstexte) → `robots.txt` öffnen. **Damit ist von Welle E nur noch S2-20 + `robots.txt` offen.**

### Offene Fragen, die weiterhin unbeantwortet sind

Abschnitt 10 des Reports listet 21. Diese blockieren oder verbilligen konkret die nächsten Wellen:

1. ~~**Setzt der Host-Reverse-Proxy `X-Forwarded-Proto`?**~~ — durch S2-10 gegenstandslos: `CookieSecurePolicy.Always` hängt nicht mehr davon ab. **Aber:** Setzt der Proxy den Header *nicht*, ist der Login nach dem Deploy sichtbar kaputt statt unsicher funktionierend. Nach dem Prod-Deploy einmal prüfen.
2. ~~**`ReconnectionPolicy.Reset(bool)`-Verhalten**~~ — am Quelltext verifiziert, s. Welle B oben.
3. ~~**Existiert außerhalb des Repos schon ein Backup?**~~ — beantwortet: nein, keins. S1-2 ist damit der erste überhaupt.
4. **Wie viele Vote-Sessions hat ein Channel typisch?** `SELECT "ChannelId", COUNT(*) FROM "VoteSessions" GROUP BY 1` — durch den Sub-Cache aus S2-11 entschärft, aber weiterhin relevant für die Kosten der Listen-Route.
5. ~~**Kosten von `strictTemplates`**~~ — gemessen (2026-07-30): **0 Fehler**, wie auch `strict` und `typeCheckHostBindings`. Alle drei sind gesetzt.
6. **Wird `VoteSession.IsActive` je automatisch beendet?** Produktentscheidung. Durch die 366-Tage-Grenze auf rückdatiertes `StartedAt` entschärft, aber eine nie beendete Session erweitert ihr Auswertungsfenster weiter laufend.

Die restlichen (Stresstest-Messungen, 7TV-Editor-Permissions-Bitfeld, GHCR-Sichtbarkeit, Branch-Protection) sind in Abschnitt 10 des Reports unverändert nachlesbar — bis auf **Frage 17** („Cacht der Proxy Antworten?"), die seit dem 2026-08-05 beantwortet ist: die Api sendet jetzt `Cache-Control: no-store` auf allem unter `/api/` (Header-Middleware in `Program.cs`, Tests in `ApiCacheControlTests`). Die **Docker-Log-Rotation** (Frage 15) ist seit dem 2026-08-04 beantwortet: hostweit per `/etc/docker/daemon.json` (`json-file`, 10 MB × 3 Dateien, VPS-Härtung — Bericht im privaten Repo `sensitron/infra-docs`); greift je Container erst beim nächsten Redeploy.

### Nicht erneut untersuchen

Die **Multi-Tenant-Isolation** wurde über eine vollständige Endpoint-×-Rollen-Matrix geprüft (Anhang A) und ist **intakt** — ein Mod oder 7TV-Editor von Channel A kommt nicht an Daten von Channel B.
