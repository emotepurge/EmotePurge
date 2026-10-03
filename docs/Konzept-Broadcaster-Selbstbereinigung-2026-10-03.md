# Konzept: Broadcaster-Selbstbereinigung (#245, Epic #248 D)

Stand 2026-10-03. Denkwerkzeug des Betreibers, kein Code. Die Entscheidungen in Abschnitt 4 sind
am 2026-10-03 gefallen; Abschnitte 3, 5 und 6 sind auf sie hin ausgearbeitet. Gelesen: Issue #245,
Epic #248, `CLAUDE.md`, `PRODUCT.md`, `docs/Architectur.md` (B.3, 5), die DECISIONS-Einträge zu
#243/#244/#252 und zur Kanal-Sperrliste, `ChannelService` (Join/Leave/Purge/Retention-Purge),
`ChannelAccessService`, `ChannelDeactivation`, `AuthEndpoints` (`DELETE /api/auth/me`),
`Worker.cs` (LEAVE), `UsageStatFlushService`, `AppDbContext` (Kaskaden), die Datenschutzerklärung
§ 9/§ 13 in `infra-docs` und die Dateninventur. Was nur auf `feat/emote-sets-200` existiert, ist
als solches markiert.

---

## 1. Ziel und Abgrenzung

**Problem.** `CanManageChannelAsync` lässt jeden Live-Moderator einen Kanal joinen. Ein Streamer,
dessen Kanal ein Mod ohne sein Zutun hinzugefügt hat, hat heute zwei Wege: „Verlassen"
(deaktiviert nur, behält 180 Tage alles) oder eine E-Mail an den Betreiber, der dann per Hand
`EXCLUDED_CHANNEL_IDS` pflegt, `api`+`worker` neu erstellt und im Admin-Bereich purged.

**Ziel.** Der Broadcaster — verifiziert über die unveränderliche `Channel.TwitchChannelId`, nicht
über Mod-Status — löscht die Daten seines Kanals selbst, mit derselben Wirkung wie der Admin-Purge,
und der Kanal kann danach nicht von einem Mod wieder hinzugefügt werden; nur er selbst oder ein
Global-Admin kann ihn zurückholen.

**Rechtlicher Rahmen — nur, was das Repo schon festgelegt hat.** Die Datenschutzerklärung (live
seit 2026-09-24) sagt in § 9 zu: *„Auf Ihre Anfrage an contact@… entfernen wir Ihren Kanal
sofort, löschen diese Daten und sperren ihn anhand seiner unveränderlichen Twitch-Kennung gegen ein
erneutes Hinzufügen – auch durch andere Personen wie Ihre Moderatoren."* Audit-Einträge, die den
Kanal nennen, bleiben bis zur regulären Löschung nach 12 Monaten; Backups bis zu 60 Tage (§ 13).
Kanaldaten laufen auf Art. 6 (1) f, der Widerspruch (Art. 21) ist in § 15 und über die Sperrliste
abgebildet (DECISIONS 2026-09-24). **#245 erfindet kein neues Versprechen, sondern macht ein
gegebenes zum Selbstbedienungsweg** — die Dateninventur ordnet „Kanaldaten sofort löschen"
ausdrücklich #245 zu. Ob die Selbstbedienung rechtlich *nötig* ist, beantwortet dieses Dokument
nicht; bis zum Deploy bleibt der E-Mail-Weg der versprochene Weg, und der funktioniert.

**Nicht Gegenstand** (laut Issue): ob ein Mod überhaupt ohne Broadcaster joinen darf. Ebenso nicht:
Datenexport (Art. 15/20), die Harness-Sperre (#260), der Fremdkanal-Preview (s. 3.7).

---

## 2. Ist-Zustand

### 2.1 Was je Kanal existiert

| Ort | Daten | Beim Admin-Purge |
|---|---|---|
| `Channels` | Twitch-ID, Login, Set-ID/-Kapazität, Zeitstempel, Flags, `LastSyncFailureReason` | gelöscht |
| `Emotes` → `UsageStats` | Set-Inventar, Tageszähler (Use/Bot/SharedChat) | Kaskade |
| `ChannelLiveDays` | Live-Minuten je UTC-Tag | Kaskade |
| `ChannelEmoteSetObservations` (nur Epic-Branch) | Beobachtungsintervalle je Set | Kaskade |
| `VoteSessions` → `VoteSessionEmotes`, `Votes` | Abstimmungen samt Einzelstimmen fremder Nutzer | Kaskade |
| `AuditLogEntries` | Snapshot-String `ChannelName`, kein FK; auf dem Epic-Branch nennen `emotes.syncImported`-Einträge **anderer** Kanäle diesen Kanal als `sourceChannelName`/`targetOwnerTwitchLogin` | **bleibt** (12 Monate) |
| Redis | `resync:cooldown:{login}` (TTL), `subcheck:{user}:{broadcasterId}` (10 min), `modlist:{user}` nennt den Kanal (10 min), `worker:roster`/`worker:live-status` (je Tick überschrieben) | bleibt, läuft per TTL aus |
| Logs | Api: `LogWarning("Channel {Channel} wurde per Admin-Purge …")`; Join-Pfad nennt den Kanal; nginx-Access-Log mit Kanalname in der URL (~15 Tage) | bleibt bis Rotation |
| Backups | VPS 14 Tage → NAS 30 Tage → OneDrive 60 Tage (`infra-docs`) | bleibt bis Rotation |
| Worker-RAM | `EmoteMatchCache`, `EmoteUsageCounter` (30-s-Flush) | LEAVE räumt den Cache; der Flush verträgt fehlende Emote-FKs seit #41 |

### 2.2 Wer heute was darf

- **Leave** (`DELETE /{name}`, Admin/Broadcaster/Mod): `IsBotActive=false`, `DeactivatedAtUtc`
  gesetzt, Intervall geschlossen, `channel.leave`-Audit, LEAVE nach dem Commit. Historie bleibt;
  der Retention-Job purged 180 Tage später (`PurgeIfInactiveSinceAsync`, unter Zeilensperre,
  `reason: "retention"`).
- **Purge** (`DELETE /{name}/purge`, nur Global-Admin): LEAVE **vor** dem Schreiben (Redis-Ausfall
  → 500, nichts geschrieben), Audit `channel.purge` ohne Details, Kaskade. Zeile wird **ungesperrt**
  geladen (akzeptiertes Restfenster, DECISIONS 2026-09-23). Admin-only gerade *weil* der Mod-Cache
  ein `/unmod` bis zu 10 min überlebt — der Broadcaster-Check hat dieses Problem nicht, er
  vergleicht IDs ohne Cache.
- **Sperrliste** `EXCLUDED_CHANNEL_IDS`: env-basiert, beim Start gelesen, nicht gepollt;
  `IExcludedChannelFilter.IsExcluded(id)` ist synchron und speicherresident; nur der Betreiber
  ändert sie; **niemand** ist ausgenommen, auch kein Admin; ihre Existenz je Kanal ist bewusst
  geheim (keine Namen in Logs/Audit). Der Identity-Reconcile deaktiviert gesperrte aktive Zeilen
  stündlich von selbst.
- **Kontolöschung #243** (`DELETE /api/auth/me`, seit 03.10. live): löscht User-Zeile, Stimmen,
  pseudonymisiert Audit-Einträge. **Kanaldaten sind ausdrücklich nicht betroffen** (§ 5.5: „auch
  wenn es Ihr eigener Kanal ist"). Der Kanal eines gelöschten Broadcaster-Kontos **bleibt aktiv und
  wird weiter gezählt**; die Mods verwalten ihn weiter. Loggt sich der Broadcaster erneut ein,
  entsteht ein leeres Konto mit derselben Twitch-ID — er bleibt per ID Broadcaster.

### 2.3 Was der Broadcaster-Check kann und nicht kann

`IsBroadcaster` vergleicht `Channel.TwitchChannelId` mit `principal.TwitchUserId`. Ist die ID
`null` (Zeile während eines Helix-Ausfalls angelegt, oder id-loses Umbenennungs-Duplikat), fällt
der Check auf einen **Login-Vergleich** zurück. Für Lesezugriffe vertretbar; für eine
unwiderrufliche Löschung ist genau dieser Fallback die Lücke, die DECISIONS beim Rename schon
einmal geschlossen hat (freigegebener Name → neuer Inhaber).

---

## 3. Vorschlag

### 3.1 Nutzerfluss

1. **Ort (E2):** Channel-Workspace, neben „Kanal verlassen"/„Bot reaktivieren"
   (`channel-workspace-layout.ts`). Ein zweiter, leiserer Knopf „Kanaldaten löschen"
   (`danger-quiet`, §4.2 Designsprache), **nur sichtbar bei `permissions.canPurgeAsBroadcaster`**
   — neues Feld der `ChannelPermissionsDto` (der Code-Kommentar dort: „add it together with its
   first consumer"). Sichtbar auch für einen inaktiven Kanal: wer verlassen hat, soll die 180 Tage
   nicht abwarten müssen.
2. **Bestätigung (E3):** `TypedConfirmDialog` mit abgetipptem Kanalnamen, wie Admin-Purge und
   #243. Der Text nennt die Zahlen (Emotes, Abstimmungen, Live-Tage — die Admin-Seite liefert sie
   schon; der Workspace braucht sie aus einem bestehenden oder einem schlanken neuen Read) und
   vier Sätze: unwiderruflich; Audit-Einträge bleiben 12 Monate; Backups bis 60 Tage; **„Danach
   kann nur du selbst den Kanal wieder hinzufügen, deine Moderatoren nicht."**
3. **Konto-Bindung:** `expectedTwitchUserId` als Query-Parameter wie bei `DELETE /api/auth/me`
   (Mehr-Tab-Problem). Mismatch → 409 `account_mismatch`, bestehender Code.
4. **Danach:** Navigation zur Übersicht; der Kanal erscheint als „nicht beobachtet". Mods sehen
   ihn in ihrer Übersicht ebenfalls untracked, ihr Join scheitert mit 403 (3.4). Das Kanal-Audit-Log
   bleibt dem Mod-Team lesbar — `ChannelManagementAuthorizationFilter` prüft Mods live gegen
   Helix, nicht gegen die Zeile — und zeigt `channel.purge` durch den Broadcaster. Das **ist** die
   Benachrichtigung des Mod-Teams; eine eigene braucht es nicht.
5. **Rückweg:** Der Broadcaster (oder ein Admin) klickt „Beobachten"/Join wie bei jedem
   untracked Kanal; der Join hebt die Sperre auf (3.4). Keine eigene Oberfläche dafür.

### 3.2 Api-Vertrag

- **Route:** `DELETE /api/channels/{channelName}/data` (Name offen; `/purge` ist belegt und bleibt
  für den Admin unverändert). Gruppe `/api/channels` → erbt Auth + `ChannelNameValidation`.
- **Neuer Filter `ChannelBroadcasterAuthorizationFilter`** (`Auth/`), Muster der beiden
  bestehenden: 400 `invalid_channel_name` · 401 ohne Principal · 404 keine Zeile ·
  **403 wenn `TwitchChannelId != principal.TwitchUserId`** — kein Admin-Durchgriff (der Admin hat
  `/purge`), kein Mod, **kein Login-Fallback (E7)**. Die Prüfung braucht weder Helix noch Redis
  und funktioniert damit auch im Twitch-Ausfall.
- **Antworten:** 204 gelöscht · 404 nicht getrackt · 409 `account_mismatch` · 409 neuer Code
  `channel_identity_unresolved` für `TwitchChannelId == null` (Regel 7: `ApiErrorCodes` +
  `api-error.ts` + beide Locales; Text sinngemäß „Die Kanal-Identität ist noch nicht bestätigt —
  bitte in etwa einer Stunde erneut", der Reconcile füllt die ID nach).
- **Join-Pfad (bestehend, `POST /{name}/join`):** neue Ablehnung 403 mit neuem Code
  `channel_locked_by_broadcaster` (Text: „Der Streamer hat diesen Kanal aus EmotePurge entfernt.
  Nur er selbst kann ihn wieder hinzufügen."). **Bewusst nicht** der neutrale `channel_excluded`-
  Text: der ist für den operatorseitigen Widerspruch gebaut, dessen Existenz selbst schutzwürdig
  ist. Hier handelt der Betroffene sichtbar selbst (der Eintrag steht ohnehin im Kanal-Audit-Log),
  und ein Mod, der nur „kann nicht hinzugefügt werden" liest, schreibt sonst dem Betreiber.
- **Rate-Limit:** `Bookkeeping`, wie Leave/Purge. Kein Antiforgery-Token (Konvention: `SameSite=Lax`
  + JSON-Binding, DECISIONS 2026-10-03).

### 3.3 Service-Vertrag (Infrastructure)

Neue Methode `IChannelService.PurgeByBroadcasterAsync(channelName, actor, ct)` →
`Purged | NotFound | NotBroadcaster | IdentityUnresolved`. `PurgeAsync` bleibt; Kaskade und
Audit-Schritt wandern in einen privaten Helfer, den alle drei Purge-Pfade teilen.

- **Transaktion + `LoadChannelForUpdateAsync`** wie der Retention-Purge, nicht ungesperrt wie der
  Admin-Purge: ein gleichzeitiger Join (eines Mods oder des Broadcasters) serialisiert sich auf
  dieser Sperre und sieht nach dem Commit **keine Zeile, aber die Sperrzeile** (3.4) — deshalb
  muss die Sperrzeile **in derselben Transaktion vor dem Commit** eingefügt werden.
- **ID-Nachprüfung im Service** (`channel.TwitchChannelId == actor.TwitchUserId`), nicht nur im
  Filter — wie die Kontolöschung die Inaktivität unter der Sperre erneut prüft.
- **Audit (E4):** `channel.purge`, `ChannelName` gesetzt, Details
  `{ reason: "broadcasterRequest" }` — dritter Wert neben „keine Details" (Admin) und
  `retention`. Die Sperre braucht keinen eigenen Eintrag: sie ist Teil dieses Purges.
- **LEAVE nach dem Commit**, nicht davor wie beim Admin-Purge: die Zeile ist Quelle der Wahrheit,
  der Roster-Prune des periodischen Resyncs holt einen verlorenen LEAVE in ≤ 2 Ticks nach, der
  Flush verträgt fehlende Emote-FKs. Ein Redis-Ausfall kostet nur die Beschleunigung, und die
  Zeilensperre wird nicht über einen Redis-Roundtrip gehalten.

### 3.4 Die Broadcaster-Sperre (E1 = B) — Datenmodell und Lebenszyklus

**Warum eine eigene Tabelle.** Der Purge löscht die `Channels`-Zeile samt Login. Ein Flag auf der
Zeile („Tombstone") würde entweder den Login behalten (widerspricht „löschen") oder nach 180 Tagen
vom Retention-Purge stillschweigend mit entsorgt — die Sperre liefe ab, ohne dass es jemand
entschieden hat. Die Sperre muss also ohne Kanalzeile existieren und ist allein über die
unveränderliche Twitch-ID adressierbar — genau die Kennung, die § 9 für die Sperre nennt.

**Warum nicht in `IExcludedChannelFilter` hineinfalten.** Die env-Liste ist (a) synchron und
speicherresident — ein DB-Lookup passt nicht in die Signatur, und ein Cache davor hätte genau die
Lücke, die die Sperre schließen soll (Mod joint im Cache-Fenster nach dem Purge); (b) ohne jede
Ausnahme, auch nicht für Admins; (c) geheim je Kanal. Die Broadcaster-Sperre ist (a) ein
indizierter Lookup auf einem Pfad, der ohnehin die DB berührt, (b) vom Broadcaster und vom Admin
aufhebbar, (c) nicht geheim. **Zwei Mechanismen nebeneinander, mit klarer Trennung:** env-Liste =
Widerspruch beim Betreiber, Tabelle = eigene Entfernung durch den Streamer. Ein Kanal kann auf
beiden stehen; die env-Liste gewinnt immer (wird zuerst geprüft, kein Aufheben per Join).

**Tabelle `BroadcasterChannelLocks`** (Name offen):

| Spalte | Typ | Bedeutung |
|---|---|---|
| `TwitchChannelId` | `string`, PK | die unveränderliche Broadcaster-ID — **kein Login**, kein Anzeigename |
| `LockedAtUtc` | `timestamptz` | Zeitpunkt des Purges |

Nicht mehr. Kein FK auf `Users` (das Konto kann gelöscht sein oder nie existiert haben), kein
Grund-Feld (es gibt nur einen Grund), kein Akteur (der Akteur ist per Definition der Inhaber
dieser ID). Minimal, damit die Zeile nach einer Kontolöschung nichts enthält, was die
Pseudonymisierung aus #243 hätte erfassen müssen.

**Schreiben:** nur `PurgeByBroadcasterAsync`, in der Purge-Transaktion, **vor** dem Commit
(Upsert — ein zweiter Purge nach Rejoin+Purge aktualisiert nur den Zeitstempel). Admin-Purge und
Retention-Purge schreiben **keine** Sperre: der Admin nutzt die env-Liste, der Retention-Purge
folgt einem gewöhnlichen Verlassen, nach dem ein Mod den Kanal heute wie morgen wieder hinzufügen
darf — diese Policy ändert #245 nicht.

**Lesen (zwei Stellen, beide über ein neues `IBroadcasterChannelLockService` in Infrastructure):**

1. **`ChannelService.JoinAsync`**, direkt nach der env-Prüfung auf die aufgelöste Identität — und,
   wie die zweite Revision der Sperrliste es für die env-Liste tat, zusätzlich auf die
   `TwitchChannelId` der Zeile, die `ResolveJoinTargetAsync` tatsächlich gewählt hat. Treffer:
   - Aufrufer ist der Inhaber (`actor.TwitchUserId == TwitchChannelId`) oder Global-Admin
     (`isGlobalAdmin`, wird schon durchgereicht) → Sperrzeile in der Join-Transaktion löschen,
     Join läuft weiter; `channel.join`-Audit bekommt `{ broadcasterLockLifted: true }`.
   - sonst → `ChannelJoinStatus.LockedByBroadcaster` → 403 `channel_locked_by_broadcaster`. Keine
     Zeile angelegt, nichts geschrieben, kein Audit (nichts ist passiert).
   - Helix-Ausfall (`Unavailable`): keine aufgelöste ID, also keine Prüfung möglich — eine
     **neue** id-lose Zeile kann entstehen (dieselbe, bewusst akzeptierte Lücke wie bei der
     env-Liste). Fängt der Reconcile (2.) in seinem nächsten Durchlauf.
2. **`TwitchIdentityReconcileWorker` / `ChannelIdentityService`**, im bestehenden Known-ID-Pass
   und beim Backfill einer aufgelösten ID: eine **aktive** Zeile, deren ID gesperrt ist, wird
   deaktiviert — dieselbe `ChannelDeactivation.DeactivateAsync`, aber **mit** Kanalname und
   Details `{ reason: "locked" }` statt `forExclusion` (die Sperre ist nicht geheim). Wann kommt
   so eine Zeile vor? (a) id-loses Duplikat unter einem alten Login, das den Purge der Hauptzeile
   überlebt hat; (b) Join im Helix-Ausfall (oben); (c) Restore eines Backups von vor dem Purge.
   Die Deaktivierung löscht die Daten nicht — die Zeile läuft dann als gewöhnlich „verlassen" in
   die 180-Tage-Retention. Ein Broadcaster, der ganz sicher gehen will, kann sie nach dem Reconcile
   selbst purgen; das Konzept nimmt den Restfall hin, statt im Worker eine zweite Kaskade zu bauen.

Nicht gelesen wird die Sperre in `ListActiveChannelNamesAsync`, im 7TV-Sync-Gate oder in
`GetActiveByTwitchChannelIdAsync`: diese Filter existieren, damit die env-Liste sofort nach einem
Neustart greift, ohne auf den Reconcile zu warten. Für die Broadcaster-Sperre ist eine aktive
Zeile ein Anomaliefall (oben), kein Normalpfad — der stündliche Reconcile genügt, und die 60-s-Pfade
des Workers bleiben unverändert.

**Aufheben:** ausschließlich durch einen Join des Inhabers oder eines Admins (oben). Kein eigener
Endpoint, keine Admin-Oberfläche in dieser Runde: der Admin-Kanal-Join auf der Admin-Seite ist
die Admin-Aufhebung; wer die Tabelle sehen will, sieht in die DB oder sucht `channel.purge` mit
`broadcasterRequest` im Audit-Log. Ein Broadcaster, der sein Konto gelöscht hat, loggt sich neu
ein (leeres Konto, dieselbe ID) und joint — die Sperre fällt.

**Retention:** keine. `RetentionPolicy` und `DataRetentionWorker` kennen die Tabelle nicht; eine
ablaufende Sperre würde die Tür für Mods still wieder öffnen, und § 9 verspricht die Sperre ohne
Frist. Die Datenschutzerklärung führt für die Chatter-Sperrliste schon die Formel „solange Ihr
Widerspruch gilt"; die Sperrzeile bekommt in § 13 denselben Eintrag: **„Sperrvermerk (nur die
Twitch-Kanal-ID): bis Sie den Kanal selbst wieder hinzufügen"** (E9).

**Datenschutz — eine ID speichern, nachdem alles andere gelöscht ist.** Das ist kein Nebeneffekt,
sondern das in § 9 ausdrücklich zugesagte Verhalten („sperren anhand seiner unveränderlichen
Twitch-Kennung"); die Zeile *ist* die Sperre, und ohne sie wäre die Zusage leer. Die
Rechtsgrundlage ist dieselbe, die die Erklärung für die Chatter-Sperrliste nennt (Art. 6 (1) c
i. V. m. Art. 21; hier auf Wunsch des Betroffenen). Gespeichert wird nur die numerische ID; sie
erscheint in keiner Logzeile (Startlog zählt höchstens wie bei den anderen Listen), und das
Audit-Log nennt den Kanal ohnehin per Namen. Nach einer Kontolöschung bleibt die Zeile stehen
— § 5.5 sagt bereits, dass Kanaldaten von der Kontolöschung unberührt sind, und die Sperre ist
Kanaldatum. Sie verschwindet nur, wenn der Inhaber den Kanal selbst zurückholt.

**Backup/Restore:** die Tabelle liegt in derselben DB wie die Kanäle; ein Restore stellt Kanal
und Sperre aus demselben Stand wieder her. Ein Dump von *vor* dem Purge bringt den Kanal ohne
Sperre zurück — das ist der Fall (c) oben, den nur der Broadcaster selbst erneut schließen kann;
der Dialog nennt die 60 Tage.

### 3.5 Hinweis in der Kontolöschung (E5 = B)

Der `TypedConfirmDialog` der Kontolöschung (`account-menu.ts`, seit 03.10. live) bekommt einen
bedingten Absatz: „Die Daten deines Kanals *{login}* bleiben erhalten und werden weiter gezählt.
Wenn du sie löschen willst, tu das zuerst im Workspace deines Kanals." Bedingung: der eigene Kanal
ist getrackt (aktiv **oder** inaktiv — die 180 Tage laufen auch bei inaktiv). Datenquelle:
`GET /api/channels/mine` beim Öffnen des Dialogs — es liefert den eigenen Kanal samt
Tracking-Status schon heute. Scheitert der Abruf, fehlt der Hinweis (fail-open ist für einen
Hinweis richtig; die Löschung selbst bleibt unverändert). Kein Backend-Vertrag ändert sich; der
Dialog-Zustandsautomat aus #243 bleibt unangetastet — nur Text und eine Bedingung.

### 3.6 Wiederverwendung

Kaskade, Audit-Snapshot, `TypedConfirmDialog`, `account_mismatch`, Sperrhelfer, Filter-Muster,
`pendingChannel`-Doppelklickschutz der Admin-Seite, `ChannelDeactivation`, der Admin-Join als
Aufhebungsweg. Neu: Filter, Endpoint, Service-Methode, Permissions-Feld, Knopf+Dialog, zwei
Error-Codes, Sperrtabelle + Service + Migration, zwei Lesestellen (Join, Reconcile).

### 3.7 Epic #200 (Emote-Sets)

`ChannelEmoteSetObservations` kaskadiert bereits am Kanal — nichts zu tun. Zwei Nähte: (a)
`emotes.syncImported`-Einträge **anderer** Kanäle nennen den gelöschten Kanal als Quelle und dessen
Besitzer-Login; § 9 deckt das („Einträge, die den Kanal nennen, bleiben"), `privacy-notes.md` hat
dazu bereits einen Merker für den Prod-Gang des Epics — #245 ändert daran nichts, benennt es aber
im DECISIONS-Eintrag. (b) Der Fremdkanal-Preview (`ForeignEmoteSetService`) liest das öffentliche
7TV-Set **jedes** Kanals ohne Zeile — ob eine Sperre das deckt, ist laut DECISIONS 2026-09-24 eine
offene Betreiberfrage, nicht Teil von #245. Das Konzept nimmt sie weder vorweg noch verbaut es
sie: die Sperrtabelle wäre, falls ja, der natürliche Lesepunkt.

---

## 4. Entscheidungen (Betreiber, 2026-10-03)

| | Entscheidung | Warum |
|---|---|---|
| **E1** | **B — Broadcaster-Sperre in der DB.** Mods → 403 beim Join; nur Inhaber (ID-geprüft) oder Global-Admin heben sie per Join auf. | Löst das Problem des Issues (der Mod, der ungefragt hinzugefügt hat, kann es nicht wiederholen), hält § 9 ein und lässt dem Streamer den Rückweg. Kostet Migration + Worker-Änderung. |
| **E2** | **A — Workspace-Header neben „Verlassen"**, `danger-quiet`, nur Broadcaster. | Der Workspace ist der Ort, an dem der Streamer seinen Kanal sieht; nicht hinter einer Aktion verstecken, die Mods auch haben. |
| **E3** | **A — abgetippter Kanalname.** | Unwiderruflich in beide Richtungen (Daten und Sperre); dieselbe Latte wie Admin-Purge und #243. |
| **E4** | **A — `channel.purge` mit Kanalname + `reason: "broadcasterRequest"`.** | § 9 erlaubt es; das Mod-Team braucht die Zeile, um zu verstehen, warum der Kanal weg ist. Die Geheimhaltung gilt nur dem operatorseitigen Widerspruch. |
| **E5** | **B — Hinweissatz im Kontolösch-Dialog** für Broadcaster getrackter Kanäle. | Vermeidet die Überraschung „Konto weg, Kanal wird weiter gezählt", ohne zwei Transaktionen zu verknüpfen. |
| **E6** | **A — immer purgen**, Dialog nennt die Zahlen. | Der Admin-Purge tut es, § 9 zählt Abstimmungen zu den Kanaldaten; kein Streamer soll erst Sessions beenden müssen. |
| **E7** | **A — 409 `channel_identity_unresolved`** bei Zeilen ohne Twitch-ID. | Eine Löschung auf Basis eines wiedervergebbaren Namens ist die Rename-Lücke, die der Lesepfad schon einmal geschlossen hat. |
| **E8** | **A — Konzept und Plan jetzt, Bau und Deploy von Api+Worker nach dem bindenden #69-Lauf (ab 08.10.).** | Worker-Freeze bis 07.10.; ein Purge eines Messkanals im Fenster würde den Lauf um diesen Kanal bringen; der E-Mail-Weg trägt bis dahin. |
| **E9** | **A — § 9 um die Selbstbedienung und § 13 um den Sperrvermerk ergänzen**, in `infra-docs` beim Prod-Gang. | Wie bei #243 § 5.5: der Text muss den Weg nennen, der existiert. |

Beim Durcharbeiten offengebliebene Kleinigkeit, hier mit Vorschlag festgehalten statt als eigene
Entscheidung: **der Ablehnungstext für Mods** (3.2) — ein eigener Code
`channel_locked_by_broadcaster` statt des neutralen `channel_excluded`. Begründung dort.

---

## 5. Grenzfälle

- **Rename des Twitch-Logins.** Route trägt den Namen, Check die ID. Zeigt der alte Name
  inzwischen auf eine andere Zeile (Name freigegeben, neu vergeben), schlägt die ID-Prüfung an →
  403, nichts gelöscht. Der Broadcaster findet seinen Kanal nach dem nächsten Reconcile unter dem
  neuen Namen. Die Sperre ist ID-basiert und folgt jedem Rename automatisch.
- **Duplizierte Zeilen.** Ein id-loses Duplikat (Join im Helix-Ausfall) fällt unter E7 → 409, bis
  der Reconcile es zusammengeführt oder die ID nachgetragen hat. Überlebt ein Duplikat den Purge
  der Hauptzeile (es trägt keine ID, der Purge findet es nicht), deaktiviert der Reconcile es beim
  Backfill der nun gesperrten ID (3.4, Fall a); seine Daten laufen in die 180-Tage-Retention.
- **Broadcaster ist Global-Admin.** Beide Wege offen; der Audit-`reason` sagt, welcher genommen
  wurde. Nur der Broadcaster-Weg schreibt eine Sperre. Präzedenz: Admin darf das eigene Konto
  löschen.
- **Twitch nicht erreichbar.** Purge braucht Helix nicht. Der Join (Aufhebung) braucht die
  aufgelöste ID, um den Inhaber zu erkennen — im Ausfall legt ein Broadcaster-Join eine id-lose
  Zeile an, die Sperre bleibt stehen, und der Reconcile deaktiviert die Zeile beim Backfill wieder
  (Fall b). Das ist falsch herum, aber selbstheilend: nach dem Ausfall joint er erneut, dann mit ID,
  und die Sperre fällt. Hinweistext im 403? Nein — die Zeile ist ja entstanden; der Streamer sieht
  nur, dass der Bot nach einer Stunde wieder inaktiv ist, und klickt erneut. Akzeptierter Restfall,
  im DECISIONS-Eintrag zu nennen.
- **Rennen Purge ↔ Join.** Zeilensperre: Join wartet; nach dem Commit findet er keine Zeile, aber
  die Sperrzeile → Mod 403, Inhaber hebt auf und legt neu an. Der Retention-Purge sieht
  `NotFound`. Der Admin-Purge bleibt ungesperrt (bewusst unverändert).
- **Rennen Join (Inhaber hebt auf) ↔ Purge.** Beide locken die Kanalzeile; es gibt noch keine →
  der Join legt sie an und löscht die Sperre in seiner Transaktion; der Purge findet keine Zeile →
  404. Oder umgekehrt: Purge zuerst ist ein No-op auf einer nicht existierenden Zeile. Kein Pfad
  kann eine Sperre mit einer aktiven Zeile zurücklassen, außer über Fall a–c.
- **Doppelklick / zwei Tabs.** Zweiter Aufruf → 404; UI blockt per `pendingChannel`-Muster.
  Konto-Wechsel zwischen Tabs → 409 `account_mismatch`.
- **Redis-Ausfall.** LEAVE nach Commit → Worker zählt bis zu zwei Resync-Ticks in einen Kanal ohne
  Zeile; der Flush verwirft die Zähler am FK, die EventAPI-Subscription fällt beim Prune. Kein
  Datenverlust.
- **Backups.** Gelöschte Daten liegen bis 14/30/60 Tage in VPS/NAS/OneDrive; der Dialog nennt
  60 Tage. Restore von vor dem Purge → Kanal ohne Sperre zurück (Fall c; nur der Broadcaster
  schließt ihn erneut).
- **Kapazitätsdeckel.** Der Purge gibt einen der 80 Slots frei; ein späterer Rejoin des Inhabers
  zählt wieder dagegen und kann an 409 `channel_capacity_reached` scheitern — die Sperre wird
  dann **nicht** aufgehoben (der Join ist gescheitert, die Transaktion rollt zurück). Kein
  Sonderfall; der Admin kann joinen (ist vom Deckel ausgenommen).
- **Kanal zusätzlich auf der env-Sperrliste.** Env gewinnt: Broadcaster-Join → 403
  `channel_excluded` (neutral), die Broadcaster-Sperre bleibt unberührt stehen. Purge ist
  trotzdem erlaubt (löscht Daten).
- **Kontolöschung nach dem Purge.** Sperrzeile bleibt (3.4). Der `channel.purge`-Eintrag wird als
  Akteur pseudonymisiert, nennt aber weiter den Kanal — wie #243 es für jede Kanalzeile festgelegt
  hat (ein Broadcaster-Login ist zugleich ein Kanalname).
- **Messfenster #69 (bis einschließlich 07.10., bindender Lauf ab 08.10.).** Ein Broadcaster-Purge
  im Fenster zerstört die Live-Baseline des Kanals; der Harness bricht für ihn fail-closed ab.
  Verweigern darf man das nicht — anbieten muss man es vor dem 08.10. nicht (E8). Der Worker-Freeze
  macht E1 = B vor dem 08.10. ohnehin undeploybar.

---

## 6. Task-Reihenfolge und Teststrategie

Jeder Task ein Subagent; Pläne ohne fertigen Code. Reihenfolge nach Abhängigkeit.

**T1 — Sperrtabelle + Service (Infrastructure, Regel 11).** Entität, Migration, `IBroadcasterChannelLockService`
(`IsLockedAsync`, `LockAsync` im übergebenen Kontext/Transaktion, `UnlockAsync`), Registrierung in
`AddEmotePurgeInfrastructure`. Tests: Unit-frei, Integration gegen Postgres: Upsert, Lookup,
Unlock idempotent. **Prod-Migration von Hand vor dem Deploy** (CLAUDE.md-Verfahren, additiv).

**T2 — Purge-Pfad (Infrastructure, Regel 11).** `PurgeByBroadcasterAsync` mit Transaktion,
Zeilensperre, ID-Nachprüfung, geteiltem Kaskaden-/Audit-Helfer, Sperrzeile vor dem Commit, LEAVE
danach. Tests in `Integration/ChannelServiceTests`: ID-Mismatch → nichts geschrieben, kein Audit,
keine Sperre; `null`-ID → `IdentityUnresolved`; Kaskade erreicht Emotes/UsageStats/LiveDays/
Sessions/Votes (Observations auf dem Epic-Branch); Audit-Eintrag überlebt mit `reason`; Sperrzeile
existiert nach dem Commit; LEAVE erst nach Commit (Fake-Publisher-Reihenfolge); Rennen mit
`JoinAsync` unter der Sperre nach dem Muster der Retention-Tests.

**T3 — Join-Pfad + Reconcile (Infrastructure/Worker, Regel 11).** `JoinAsync`: Sperrprüfung auf
Identität und gewählte Zeile, Aufhebung für Inhaber/Admin in der Join-Transaktion mit Audit-Detail,
neuer `ChannelJoinStatus.LockedByBroadcaster`. `ChannelIdentityService`: Deaktivierung aktiver
gesperrter Zeilen im Known-ID-Pass und beim Backfill, `reason: "locked"` mit Namen. Tests: Mod-Join
→ Status, nichts geschrieben; Inhaber-Join → Sperre weg, Zeile neu, Audit-Detail; Admin-Join dito;
Join bei gescheitertem Deckel lässt die Sperre stehen; Reconcile deaktiviert id-loses Duplikat nach
Backfill; env-Liste gewinnt über die Sperre.

**T4 — Api (Regel 11).** `ChannelBroadcasterAuthorizationFilter`, Endpoint, `account_mismatch`-
Bindung, zwei Error-Codes, Permissions-Feld `canPurgeAsBroadcaster`, 403-Mapping im Join-Handler
(Switch wächst, CS8509 schlägt an). `AuthFilterMatrixTests`: 400/401/403 (Mod, fremder Nutzer,
Admin-ohne-Broadcaster)/404/409 (Mismatch, unresolved)/204 an der echten Route; Join → 403
`channel_locked_by_broadcaster`; Filter-Reihenfolge der Gruppe unverändert. Architectur.md
B.3-Matrix ergänzen.

**T5 — Web (Regel 12).** Permissions-Model, `ChannelService.purgeOwnData()`, Knopf + Dialog im
Workspace, Join-Fehlertext, Hinweisabsatz in der Kontolöschung (E5), beide Locales, `api-error.ts`.
Spec nur für Logik: Sichtbarkeit aus `permissions`, Dialog-Rückgabe, Fehler-Mapping, Bedingung des
Kontolösch-Hinweises (`api-error-locales.spec` fängt die Locale-Hälfte). E2E: Broadcaster sieht
Knopf, Mod nicht; Flow bis 204; 409-Pfad; Mod-Join → 403-Text; Kontolösch-Dialog mit/ohne Hinweis
— alles gegen gemocktes `/api/**`. **Suite nur ohne Api auf `:5151`.**

**T6 — Doku im selben Commit (Regel 3).** DECISIONS-Eintrag (englisch; nennt die Trennung
env-Liste/Sperrtabelle, den Helix-Ausfall-Restfall, die Epic-#200-Naht), Operations.md
(Widerspruchsverfahren: Selbstbedienung + E-Mail; Sperrtabelle neben `EXCLUDED_CHANNEL_IDS`,
Aufhebung per Admin-Join), Epic #248 Statuszeile, Datenschutzerklärung § 9/§ 13 in `infra-docs`
(E9) beim Prod-Gang.

**T7 — Gates und Live-Verifikation (Regel 16).** `dotnet test`, Vitest, E2E, `coverage-local.mjs`;
live gegen den lokalen Stack per Cookie: Purge als Broadcaster, LEAVE im Worker-Log, Mod-Join →
403, Inhaber-Join → Sperre weg und Zeile neu, Reconcile deaktiviert ein von Hand reaktiviertes
Duplikat. Codex-Zweitmeinung vor dem Merge; **Deploy nach dem 08.10., Migration zuerst, dann Api
und Worker gemeinsam.**
