# Konzept: Broadcaster-Selbstbereinigung (#245, Epic #248 D)

Stand 2026-10-03, zweite Fassung nach dem adversarialen Review durch Codex Sol (Abschnitt 7).
Denkwerkzeug des Betreibers, kein Code. Die Entscheidungen E1–E9 in Abschnitt 4 sind am 2026-10-03
gefallen; E10–E12 sind aus dem Review entstanden und **offen**. Gelesen: Issue #245, Epic #248,
`CLAUDE.md`, `PRODUCT.md`, `docs/Architectur.md` (B.3, 5), die DECISIONS-Einträge zu #243/#244/#252
und zur Kanal-Sperrliste, `ChannelService` (Join/Leave/Purge/Retention-Purge),
`ChannelAccessService`, `ChannelDeactivation`, `ChannelIdentityService`, `SevenTvSyncService`,
`AuditLogQueryService`, `AuthEndpoints` (`DELETE /api/auth/me`), `Worker.cs` (LEAVE),
`UsageStatFlushService`, `AppDbContext` (Kaskaden), die Datenschutzerklärung § 9/§ 13 in
`infra-docs` und die Dateninventur. Was nur auf `feat/emote-sets-200` existiert, ist markiert.

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
Datenexport (Art. 15/20), die Harness-Sperre (#260), der Fremdkanal-Preview (3.7), die
Admin-Allowlist nach Login (E11 — vorbestehend, eigenes Issue).

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
  geheim. Sie wird an vier Stellen geprüft: Join-Pfad (Identität **und** gewählte Zeile),
  7TV-Sync-Gate (`SevenTvSyncService.cs:369`, die über 7TV aufgelöste ID einer id-losen Zeile,
  **vor** dem Backfill), Roster (`ListActiveChannelNamesAsync`, in-memory) und Reconcile
  (Known-ID-Pass, Deaktivierung **ohne** Zeilensperre, `ChannelIdentityService.cs:403`).
- **Kontolöschung #243** (`DELETE /api/auth/me`, seit 03.10. live): löscht User-Zeile, Stimmen,
  pseudonymisiert Audit-Einträge. **Kanaldaten sind ausdrücklich nicht betroffen** (§ 5.5). Der
  Kanal eines gelöschten Broadcaster-Kontos **bleibt aktiv und wird weiter gezählt**. Loggt sich
  der Broadcaster erneut ein, entsteht ein leeres Konto mit derselben Twitch-ID.
- **Kanal-Audit-Log** (`GET /{name}/audit-log`, `ChannelManagementAuthorizationFilter`): filtert
  nur nach dem Namens-Snapshot (`AuditLogQueryService.cs:94-99`). **Existiert keine Zeile, fällt
  `IsBroadcaster` auf den Login-Vergleich zurück** (`ChannelAccessService.cs:95-97`) — wer nach
  einem Purge (gleich welcher Art) den freigewordenen Login registriert, liest 12 Monate lang das
  Audit-Log des Vorgängers; dessen Mods ebenso (Helix-Live-Check gegen den Login). Vorbestehend.

### 2.3 Was der Broadcaster-Check kann und nicht kann

`IsBroadcaster` vergleicht `Channel.TwitchChannelId` mit `principal.TwitchUserId`. Ist die ID
`null` (Zeile während eines Helix-Ausfalls angelegt — `ChannelService.cs:419` legt sie mit
`TwitchChannelId = null` an —, oder id-loses Umbenennungs-Duplikat), fällt der Check auf einen
**Login-Vergleich** zurück. Der Reconcile löst nur **aktive** Zeilen auf
(`ChannelIdentityService.cs:43`); eine inaktive id-lose Zeile bleibt id-los, bis sie jemand joint
oder die Retention sie entsorgt. Für Lesezugriffe vertretbar; für eine unwiderrufliche Löschung ist
der Login-Fallback die Lücke, die DECISIONS beim Rename schon einmal geschlossen hat.

---

## 3. Vorschlag

### 3.1 Nutzerfluss

1. **Ort (E2):** Channel-Workspace, neben „Kanal verlassen"/„Bot reaktivieren". Ein zweiter,
   leiserer Knopf „Kanaldaten löschen" (`danger-quiet`, §4.2 Designsprache), **nur sichtbar bei
   `permissions.canPurgeAsBroadcaster`** — neues Feld der `ChannelPermissionsDto`. Sichtbar auch
   für einen inaktiven Kanal.
2. **Bestätigung (E3):** `TypedConfirmDialog` mit abgetipptem Kanalnamen. Der Text nennt die
   Zahlen (Emotes, Abstimmungen, Live-Tage) und fünf Sätze: unwiderruflich; gelöscht wird der Kanal
   unter diesem Namen samt allem, was daran hängt — **eine Altzeile von vor einer Umbenennung, die
   sich nicht mehr als deine nachweisen lässt, wird 180 Tage nach ihrer Deaktivierung automatisch
   entfernt** (3.3); Audit-Einträge bleiben 12 Monate; Backups bis 60 Tage; „Danach kann nur du
   selbst den Kanal wieder hinzufügen, deine Moderatoren nicht."
3. **Konto-Bindung:** `expectedTwitchUserId` als Query-Parameter wie bei `DELETE /api/auth/me`.
   Mismatch → 409 `account_mismatch`.
4. **Danach:** Navigation zur Übersicht; der Kanal erscheint als „nicht beobachtet". Mods sehen
   ihn untracked; ihr Join scheitert mit 403 und dem Text aus 3.2 — **das ist die Benachrichtigung
   des Mod-Teams.** Das Kanal-Audit-Log ist nach dem Purge **nicht** mehr erreichbar (3.2,
   Review F1); die erste Fassung hatte sich darauf verlassen, was die Lücke aus 2.2 war.
5. **Rückweg:** Der Broadcaster (oder ein Admin) joint wie bei jedem untracked Kanal; der Join hebt
   die Sperre auf (3.4). Keine eigene Oberfläche dafür.

### 3.2 Api-Vertrag

- **Route:** `DELETE /api/channels/{channelName}/data` (Name offen; `/purge` bleibt dem Admin).
  Gruppe `/api/channels` → erbt Auth + `ChannelNameValidation`.
- **Neuer Filter `ChannelBroadcasterAuthorizationFilter`** (`Auth/`): 400 `invalid_channel_name`
  · 401 ohne Principal · 404 keine Zeile · **403 wenn die Zeile eine ID trägt und sie nicht die
  des Principals ist** — kein Admin-Durchgriff, kein Mod, **kein Login-Fallback (E7)**. Eine Zeile
  **ohne** ID lässt der Filter durch; die Auflösung übernimmt der Service (3.3), damit die
  Helix-Abfrage nicht im Filter landet.
- **Antworten:** 204 gelöscht · 404 nicht getrackt · 403 · 409 `account_mismatch` · 409 neuer
  Code `channel_identity_unresolved` (Regel 7: `ApiErrorCodes` + `api-error.ts` + beide Locales;
  Text: „Die Kanal-Identität konnte gerade nicht bestätigt werden — bitte später erneut"). Der
  Code ist **kein Endzustand** mehr (Review F9): der Service versucht die Auflösung live (3.3).
- **Join-Pfad (bestehend):** neue Ablehnung 403 `channel_locked_by_broadcaster` („Der Streamer hat
  diesen Kanal aus EmotePurge entfernt. Nur er selbst kann ihn wieder hinzufügen."). **Bewusst
  nicht** der neutrale `channel_excluded`-Text: der gilt dem operatorseitigen Widerspruch, dessen
  Existenz schutzwürdig ist; hier handelt der Betroffene sichtbar selbst, und ein Mod, der nur „kann
  nicht hinzugefügt werden" liest, schreibt sonst dem Betreiber.
- **Kanal-Audit-Log (bestehend, Review F1):** `GET /{name}/audit-log` antwortet **404, wenn keine
  Kanalzeile existiert** — vor dem Autorisierungsfilter geprüft, damit kein Login-Fallback
  greift. Das schließt die Lücke aus 2.2 für jeden Purge (Admin, Retention, Broadcaster), nicht
  nur für diesen. Preis: das Mod-Team sieht nach dem Purge keinen Verlauf mehr; Ersatz ist der
  403-Text beim Join. Die vollständige Lösung — Audit-Einträge zusätzlich an die unveränderliche
  Kanal-ID binden und darüber filtern — ist eine neue Spalte mit Backfill und bleibt als
  Folgearbeit notiert, nicht Teil von #245. Für einen **aktiven** Kanal mit ID ändert sich nichts.
- **Rate-Limit:** `Bookkeeping`. Kein Antiforgery-Token (Konvention, DECISIONS 2026-10-03).

### 3.3 Service-Vertrag (Infrastructure)

Neue Methode `IChannelService.PurgeByBroadcasterAsync(channelName, actor, ct)` →
`Purged | NotFound | NotBroadcaster | IdentityUnresolved`. `PurgeAsync` bleibt; Kaskade und
Audit-Schritt wandern in einen privaten Helfer, den alle drei Purge-Pfade teilen.

- **Identität vor der Transaktion.** Trägt die Zeile unter dem Routennamen keine ID, fragt der
  Service `LookupByLoginAsync(name)` (Helix, dieselbe Drei-Zustands-Abfrage wie der Join) —
  **außerhalb** der Transaktion, kein Lock über einen HTTP-Roundtrip. `Found` mit der ID des
  Akteurs → weiter, die ID wird in der Transaktion auf die Zeile geschrieben (sofern frei);
  `Found` mit fremder ID → `NotBroadcaster`; `NotFound`/`Unavailable` → `IdentityUnresolved`.
  Damit ist das 409 nur noch ein „gerade nicht", kein „nie" (Review F9).
- **Zielmenge: alle nachweisbar eigenen Zeilen, nicht eine** (Review F3). In der Transaktion
  werden gesperrt, in dieser Reihenfolge (die Ordnung des Merge/Join): (1) die Zeile, die die
  **ID des Akteurs** trägt (`LoadChannelByTwitchIdForUpdateAsync`), (2) die Zeile unter dem
  **Routennamen** (`LoadChannelForUpdateAsync`), falls es eine andere ist und sie id-los ist und
  ihr Login sich eben live auf den Akteur aufgelöst hat. Beide werden gelöscht, je mit eigener
  Kaskade und je einem `channel.purge`-Eintrag. So verschwindet ein Umbenennungs-Duplikat
  **mit** der Hauptzeile, solange sein Login noch dem Akteur gehört.
- **Was nicht nachweisbar ist, wird nicht gelöscht — und das steht im Dialog.** Eine id-lose
  Zeile unter einem **alten** Login, der inzwischen niemandem oder jemand anderem gehört, kann
  das System dem Akteur nicht zuordnen; nach seiner eigenen Regel (ID schlägt Login) darf sie
  nicht auf sein Wort hin gelöscht werden. Sie ist inaktiv oder wird vom Reconcile deaktiviert
  und läuft in die 180-Tage-Retention. Das ist die ausdrückliche Definition von „unvollständig":
  keine zweite Antwortform, sondern eine benannte Grenze — im Dialog, in Operations.md und in § 9.
- **ID-Nachprüfung im Service** unter der Sperre (`channel.TwitchChannelId == actor.TwitchUserId`).
- **Audit (E4):** `channel.purge`, `ChannelName` gesetzt, Details `{ reason: "broadcasterRequest" }`
  — je gelöschter Zeile ein Eintrag; die Sperre braucht keinen eigenen.
- **Sperrzeile vor dem Commit** (3.4), **LEAVE je gelöschter Zeile nach dem Commit** — die Zeile
  ist Quelle der Wahrheit, der Roster-Prune holt einen verlorenen LEAVE in ≤ 2 Ticks nach, der
  Flush verträgt fehlende Emote-FKs.

### 3.4 Die Broadcaster-Sperre (E1 = B) — Datenmodell und Lebenszyklus

**Warum eine eigene Tabelle.** Der Purge löscht die `Channels`-Zeile samt Login. Ein Flag auf der
Zeile („Tombstone") behielte den Login (widerspricht „löschen") oder liefe nach 180 Tagen mit dem
Retention-Purge still ab. Die Sperre muss ohne Kanalzeile existieren und ist allein über die
unveränderliche Twitch-ID adressierbar — die Kennung, die § 9 nennt.

**Warum nicht in `IExcludedChannelFilter` hineinfalten.** Die env-Liste ist (a) synchron und
speicherresident — ein Cache davor hätte genau die Lücke, die die Sperre schließen soll; (b) ohne
Ausnahme, auch nicht für Admins; (c) geheim. Die Broadcaster-Sperre ist (a) ein indizierter
Lookup auf Pfaden, die ohnehin die DB berühren, (b) aufhebbar, (c) nicht geheim. **Zwei
Mechanismen nebeneinander**, env gewinnt immer (wird zuerst geprüft, kein Aufheben per Join).
**Aber: die Sperre wird an denselben vier Stellen durchgesetzt wie die env-Liste** (Review F2) —
die erste Fassung hatte sie auf Join und Reconcile beschränkt und damit die Beobachtung bis zum
stündlichen Reconcile offen gelassen.

**Tabelle `BroadcasterChannelLocks`** (Name offen):

| Spalte | Typ | Bedeutung |
|---|---|---|
| `TwitchChannelId` | `string`, PK | die unveränderliche Broadcaster-ID — **kein Login**, kein Anzeigename |
| `LockedAtUtc` | `timestamptz` | Zeitpunkt des Purges |

Kein FK auf `Users`, kein Grund-Feld, kein Akteur. Minimal, damit die Zeile nach einer
Kontolöschung nichts enthält, was die Pseudonymisierung aus #243 hätte erfassen müssen.

**Schreiben:** nur `PurgeByBroadcasterAsync`, in der Purge-Transaktion **vor** dem Commit (Upsert).
Admin- und Retention-Purge schreiben **keine** Sperre: der Admin nutzt die env-Liste, der
Retention-Purge folgt einem gewöhnlichen Verlassen, nach dem ein Mod wieder joinen darf — diese
Policy ändert #245 nicht.

**Lesen — vier Stellen, über ein neues `IBroadcasterChannelLockService` in Infrastructure:**

1. **`ChannelService.JoinAsync`**, direkt nach der env-Prüfung, auf die aufgelöste Identität **und**
   auf die `TwitchChannelId` der tatsächlich gewählten Zeile. Treffer: Inhaber (`actor.TwitchUserId
   == TwitchChannelId`) oder Global-Admin (`isGlobalAdmin`) → Sperrzeile in der Join-Transaktion
   löschen, Join läuft weiter, `channel.join` bekommt `{ broadcasterLockLifted: true }`; sonst →
   `ChannelJoinStatus.LockedByBroadcaster` → 403, nichts geschrieben. **Helix-Ausfall:** ein Mod
   mit positivem Rollen-Cache (bis 10 min) passiert den Filter, `Unavailable` liefert keine ID,
   `ChannelService.cs:419` legt eine aktive id-lose Zeile an — dieselbe, bewusst akzeptierte Lücke
   wie bei der env-Liste. Sie wird von Punkt 2 **sofort** und von Punkt 4 **dauerhaft** gefangen.
2. **7TV-Sync-Gate (`SevenTvSyncService`, neben `SevenTvSyncService.cs:369`):** die für eine
   id-lose Zeile über 7TV aufgelöste ID wird gegen die Sperre geprüft, **vor** dem Backfill und vor
   der Duplikat-Suche — derselbe Platz, an dem die env-Liste geprüft wird, dieselbe Wirkung
   (`RefuseExcludedChannel`-Analog): kein Set, keine Emotes, kein Match-Cache. Der Worker sitzt bis
   zum nächsten Reconcile im IRC und verarbeitet Nachrichten im RAM, **zählt aber nichts**, weil
   es für den Kanal keine Emotes gibt. Fail-closed für die Beobachtung, nicht fail-open.
3. **Roster (`ListActiveChannelNamesAsync`):** SQL-Anti-Join gegen die Sperrtabelle auf der
   gespeicherten ID — die Methode liest ohnehin die DB. Deckt Boot-Recovery, periodischen Resync
   und Live-Poll ab, sobald eine Zeile ihre ID trägt. (Eine id-lose Zeile kann hier nicht matchen;
   dafür ist Punkt 2 da.)
4. **Reconcile (`ChannelIdentityService`), Known-ID-Pass und Backfill:** eine **aktive** Zeile mit
   gesperrter ID wird deaktiviert — `ChannelDeactivation.DeactivateAsync` **mit** Kanalname und
   `{ reason: "locked" }` (die Sperre ist nicht geheim). **Unter Zeilensperre** (Review F7): eigene
   Transaktion, `LoadChannelByTwitchIdForUpdateAsync`, dann `IsLockedAsync` **erneut** unter der
   Sperre, erst dann schreiben. Der Inhaber-Join hält dieselbe Zeilensperre, während er die
   Sperrzeile löscht — die beiden serialisieren sich, und der Reconcile findet die Sperre weg und
   schreibt nichts. Das weicht bewusst von `DeactivateExcludedRowAsync` ab, das ohne Sperre
   schreibt und sich auf den `DbUpdateException`-Catch verlässt: dort gibt es keinen legitimen
   gleichzeitigen Gegenschreiber, hier schon. Wann kommt eine aktive gesperrte Zeile vor? (a) ein
   Duplikat, das 3.3 nicht erfassen konnte; (b) der Ausfall-Join aus Punkt 1, nach dem Backfill
   durch Punkt 2 bzw. hier; (c) ein Restore von vor dem Purge (E10). Die Deaktivierung löscht
   keine Daten — die Zeile läuft als „verlassen" in die 180-Tage-Retention.

**Aufheben:** ausschließlich durch einen Join des Inhabers oder eines Admins (Punkt 1). Kein eigener
Endpoint, keine Admin-Oberfläche: der Admin-Kanal-Join auf der Admin-Seite ist die Admin-Aufhebung
(zu deren Login-basierter Allowlist s. E11). Ein Broadcaster, der sein Konto gelöscht hat, loggt sich
neu ein (leeres Konto, dieselbe ID) und joint — die Sperre fällt.

**Retention:** keine. `RetentionPolicy` und `DataRetentionWorker` kennen die Tabelle nicht; eine
ablaufende Sperre würde die Tür für Mods still wieder öffnen, und § 9 verspricht die Sperre ohne
Frist. Die Datenschutzerklärung führt für die Chatter-Sperrliste schon „solange Ihr Widerspruch
gilt"; § 13 bekommt den Eintrag **„Sperrvermerk (nur die Twitch-Kanal-ID): bis Sie den Kanal selbst
wieder hinzufügen"** (E9).

**Datenschutz — eine ID speichern, nachdem alles andere gelöscht ist.** Das ist das in § 9
ausdrücklich zugesagte Verhalten („sperren anhand seiner unveränderlichen Twitch-Kennung"); die
Zeile *ist* die Sperre. Rechtsgrundlage wie bei der Chatter-Sperrliste (Art. 6 (1) c i. V. m.
Art. 21; hier auf Wunsch des Betroffenen). Gespeichert wird nur die numerische ID; sie erscheint in
keiner Logzeile. Nach einer Kontolöschung bleibt die Zeile stehen — § 5.5 sagt, dass Kanaldaten von
der Kontolöschung unberührt sind, und die Sperre ist Kanaldatum.

**Backup/Restore und Rollback:** s. E10 und E12 — beides operative Fragen, die das Datenmodell
allein nicht löst.

### 3.5 Hinweis in der Kontolöschung (E5 = B)

Der `TypedConfirmDialog` der Kontolöschung (`account-menu.ts`) bekommt einen bedingten Absatz:
„Die Daten deines Kanals *{login}* bleiben erhalten und werden weiter gezählt. Wenn du sie löschen
willst, tu das zuerst im Workspace deines Kanals." Bedingung: der eigene Kanal ist getrackt (aktiv
**oder** inaktiv). Datenquelle: `GET /api/channels/mine` beim Öffnen des Dialogs. Scheitert der
Abruf, fehlt der Hinweis (fail-open ist für einen Hinweis richtig). Kein Backend-Vertrag ändert
sich; der Dialog-Zustandsautomat aus #243 bleibt unangetastet.

### 3.6 Wiederverwendung

Kaskade, Audit-Snapshot, `TypedConfirmDialog`, `account_mismatch`, Sperrhelfer,
`LookupByLoginAsync`, Filter-Muster, `pendingChannel`-Doppelklickschutz, `ChannelDeactivation`,
`RefuseExcludedChannel`-Muster im Sync-Gate, der Admin-Join als Aufhebungsweg. Neu: Filter,
Endpoint, Service-Methode, Permissions-Feld, Knopf+Dialog, zwei Error-Codes, Sperrtabelle + Service
+ Migration, vier Lesestellen, 404 am Audit-Log für untracked Kanäle.

### 3.7 Epic #200 (Emote-Sets)

`ChannelEmoteSetObservations` kaskadiert bereits am Kanal. Zwei Nähte: (a) `emotes.syncImported`-
Einträge **anderer** Kanäle nennen den gelöschten Kanal als Quelle und dessen Besitzer-Login; § 9
deckt das, `privacy-notes.md` hat dazu einen Merker für den Prod-Gang des Epics — #245 benennt es im
DECISIONS-Eintrag. (b) Der Fremdkanal-Preview liest das öffentliche 7TV-Set **jedes** Kanals ohne
Zeile — ob eine Sperre das deckt, ist laut DECISIONS 2026-09-24 eine offene Betreiberfrage; die
Sperrtabelle wäre, falls ja, der natürliche Lesepunkt.

---

## 4. Entscheidungen

### 4.1 Gefallen (Betreiber, 2026-10-03)

| | Entscheidung | Warum |
|---|---|---|
| **E1** | **B — Broadcaster-Sperre in der DB.** Mods → 403 beim Join; nur Inhaber (ID-geprüft) oder Global-Admin heben sie per Join auf. | Löst das Problem des Issues, hält § 9 ein, lässt dem Streamer den Rückweg. Kostet Migration + Worker-Änderung. |
| **E2** | **A — Workspace-Header neben „Verlassen"**, `danger-quiet`, nur Broadcaster. | Der Workspace ist der Ort, an dem der Streamer seinen Kanal sieht. |
| **E3** | **A — abgetippter Kanalname.** | Unwiderruflich in beide Richtungen; dieselbe Latte wie Admin-Purge und #243. |
| **E4** | **A — `channel.purge` mit Kanalname + `reason: "broadcasterRequest"`.** | § 9 erlaubt es; Geheimhaltung gilt nur dem operatorseitigen Widerspruch. |
| **E5** | **B — Hinweissatz im Kontolösch-Dialog** für Broadcaster getrackter Kanäle. | Vermeidet „Konto weg, Kanal wird weiter gezählt", ohne zwei Transaktionen zu verknüpfen. |
| **E6** | **A — immer purgen**, Dialog nennt die Zahlen. | Der Admin-Purge tut es; § 9 zählt Abstimmungen zu den Kanaldaten. |
| **E7** | **A — 409 `channel_identity_unresolved`** bei Zeilen ohne Twitch-ID. | Keine Löschung auf Basis eines wiedervergebbaren Namens. Seit der zweiten Fassung mit Live-Auflösung, kein Endzustand (3.3). |
| **E8** | **A — Konzept und Plan jetzt, Bau und Deploy von Api+Worker nach dem bindenden #69-Lauf (ab 08.10.).** | Worker-Freeze bis 07.10.; der E-Mail-Weg trägt bis dahin. |
| **E9** | **A — § 9 um die Selbstbedienung und die Nachweisgrenze, § 13 um den Sperrvermerk ergänzen**, in `infra-docs` beim Prod-Gang. | Der Text muss den Weg nennen, der existiert — und seine Grenze. |

Kleinigkeit mit Vorschlag statt Entscheidung: der Ablehnungstext für Mods — eigener Code
`channel_locked_by_broadcaster` statt des neutralen `channel_excluded` (3.2).

### 4.2 Offen (aus dem Review, Abschnitt 7)

**E10 — Restore eines Backups von vor dem Purge** (Review F4). Ein Restore stellt Kanalzeile,
Daten und den Zustand *ohne* Sperre und *ohne* Audit-Eintrag wieder her; die Boot-Recovery joint den
Kanal, die Löschung ist vergessen. § 13 nennt zwar „bis zu 60 Tage in Backups", aber ein Restore
holt die Daten in die **laufende** Verarbeitung zurück — das ist mehr als „liegt noch in einem
Backup".
- (A) **Runbook-Schritt in `infra-docs` (EmotePurge-Backup-und-Restore.md):** vor jedem Restore
  `BroadcasterChannelLocks` und die `channel.purge`-Einträge mit `broadcasterRequest` aus der
  **laufenden** DB exportieren (zwei SQL-Zeilen); nach dem Restore die Sperren wieder einspielen
  und die betroffenen Kanäle per Admin-Purge erneut löschen, **bevor** `api`/`worker` starten.
  Billig; verlässt sich auf Disziplin in einem seltenen, gestressten Moment.
- (B) **Journal außerhalb der DB:** die Api schickt nach dem Commit eine Mail an den Betreiber
  (bestehender SMTP-Pfad des Kontaktformulars, best-effort wie die Twitch-Revocation) mit der
  Twitch-ID und dem Zeitpunkt; die Mailbox ist das Journal, das Restore-Runbook liest es. Robust,
  aber ein Nebeneffekt im Purge und eine ID in einer Mail (der Betreiber bekommt Widersprüche ohnehin
  per Mail).
- (C) Dokumentierte Grenze in § 13: „Nach der Wiederherstellung eines Backups kann ein gelöschter
  Kanal bis zum nächsten Reconcile wieder beobachtet werden" — ehrlich, aber schwach.
- **Empfehlung: A**, mit B als Zusatz, falls der Betreiber die Disziplin nicht tragen will.
  Restores sind selten; die Sperrtabelle liegt in derselben DB und ist in zwei Zeilen gesichert.

**E11 — Admin-Aufhebung über eine Login-basierte Allowlist** (Review F5). `IsGlobalAdmin`
vergleicht Logins (`ChannelAccessService.cs:65-68`, `Auth:AdminTwitchLogins`), nicht IDs. Ein
freigegebener und neu vergebener Admin-Login hätte **alle** Admin-Rechte — Purge jedes Kanals,
Nutzerlöschung, Session-Revoke —, die Sperr-Aufhebung ist davon die kleinste. Vorbestehend, nicht
von #245 verursacht.
- (A) **Dokumentierte Grenze + eigenes Issue** für `Auth:AdminTwitchUserIds` (ID-Vergleich, Login
  nur als Anzeige). Die Sperr-Aufhebung bleibt beim Admin.
- (B) ID-Allowlist **in #245** umsetzen. Querschnitt durch jede Admin-Prüfung, eigener Review-Bedarf;
  bläht den Branch.
- (C) Keine Admin-Aufhebung: nur der Inhaber hebt per Join auf; der Betreiber greift im Supportfall
  per SQL ein.
- **Empfehlung: A.** Die Schwäche ist real, aber die Sperre vergrößert sie nicht; ihr Fix gehört in
  einen eigenen, kleinen, gut reviewbaren Branch.

**E12 — Rollback auf ein Image, das die Sperre nicht kennt** (Review F6). Ein älteres `api`-Image
prüft die Tabelle nicht: ein Mod-Join geht durch, der alte Reconcile deaktiviert nichts. Der
`PendingMigrationGuard` blockt nur fehlende Vorwärts-Migrationen, keinen Downgrade.
- (A) **Runbook-Regel in `infra-docs`:** kein Rollback von `api`/`worker` unter die Sperr-Version;
  wenn ein Rollback unvermeidbar ist, vorher die IDs aus `BroadcasterChannelLocks` in
  `EXCLUDED_CHANNEL_IDS` übernehmen (die env-Liste kennt jedes Image seit 2026-09-24). Billig.
- (B) **Durchsetzung in der DB:** ein `BEFORE INSERT OR UPDATE`-Trigger auf `Channels`, der eine
  **aktive** Zeile mit gesperrter ID zurückweist. Wirkt gegen jedes Image; der Inhaber-Join löscht
  die Sperrzeile in derselben Transaktion vor dem Schreiben und passiert. Ein altes Image bekommt
  statt 403 einen 500 — fail-closed. Erste Logik in der DB in diesem Repo; Raw-SQL-Migration.
- (C) Akzeptieren: Rollbacks im Messfenster sind ohnehin verboten; danach sind sie selten.
- **Empfehlung: A.** B ist die robuste Variante, falls Rollbacks je Routine werden; dann als
  eigener Eintrag mit eigenem Review.

---

## 5. Grenzfälle

- **Rename des Twitch-Logins.** Route trägt den Namen, Check die ID. Zeigt der alte Name auf eine
  fremde Zeile → 403. Die Sperre folgt der ID.
- **Duplizierte Zeilen.** Trägt die Zeile unter dem Routennamen keine ID, löst der Service den
  Login live auf (3.3): gehört er dem Akteur, werden Haupt- und Duplikatzeile gemeinsam gelöscht;
  gehört er niemandem oder jemand anderem, bleibt das Duplikat als nicht nachweisbar stehen (180-
  Tage-Retention) — die benannte Grenze.
- **Broadcaster ist Global-Admin.** Beide Wege offen; der Audit-`reason` sagt, welcher. Nur der
  Broadcaster-Weg schreibt eine Sperre.
- **Twitch nicht erreichbar.** Purge einer Zeile **mit** ID braucht Helix nicht. Ohne ID → 409,
  später erneut. Der Inhaber-Join (Aufhebung) braucht die aufgelöste ID — im Ausfall legt er eine
  id-lose Zeile an, die Sperre bleibt; das Sync-Gate hält den Kanal unbeobachtet, der Reconcile
  deaktiviert die Zeile, und ein erneuter Join nach dem Ausfall hebt mit ID auf. Falsch herum,
  aber selbstheilend und fail-closed; im DECISIONS-Eintrag zu nennen.
- **Mod-Join im Helix-Ausfall nach dem Purge** (Review F2). Positiver Rollen-Cache + `Unavailable`
  → aktive id-lose Zeile. Sync-Gate (7TV-ID) verweigert Set und Emotes **beim ersten Sync-Tick**
  (60 s), nicht erst beim Reconcile; Roster filtert, sobald die ID steht; Reconcile deaktiviert unter
  Zeilensperre. Beobachtet wird in diesem Fenster nichts; der Worker hängt bis zu einer Stunde im
  IRC, ohne zu zählen.
- **Rennen Purge ↔ Join.** Zeilensperren (ID-Zeile vor Namenszeile): Join wartet; nach dem Commit
  keine Zeile, aber die Sperrzeile → Mod 403, Inhaber hebt auf und legt neu an. Retention-Purge
  sieht `NotFound`. Admin-Purge bleibt ungesperrt (bewusst unverändert).
- **Rennen Reconcile ↔ Inhaber-Join** (Review F7). Beide locken die Zeile per Twitch-ID; der
  Reconcile prüft die Sperre erneut unter dem Lock und schreibt nichts, wenn sie weg ist.
- **Doppelklick / zwei Tabs.** Zweiter Aufruf → 404; `pendingChannel`-Muster. Konto-Wechsel → 409.
- **Redis-Ausfall.** LEAVE nach Commit → Worker zählt bis zu zwei Resync-Ticks in einen Kanal ohne
  Zeile; der Flush verwirft am FK, die Subscription fällt beim Prune.
- **Backups.** 14/30/60 Tage; Dialog nennt 60. Restore von vor dem Purge → E10.
- **Rollback der Images.** → E12.
- **Kapazitätsdeckel.** Purge gibt einen Slot frei; scheitert der Inhaber-Rejoin am Deckel, rollt
  die Transaktion zurück und die Sperre bleibt. Der Admin ist deckelbefreit.
- **Kanal zusätzlich auf der env-Sperrliste.** Env gewinnt; Broadcaster-Join → 403
  `channel_excluded`, Sperre unberührt. Purge trotzdem erlaubt.
- **Kontolöschung nach dem Purge.** Sperrzeile bleibt. Der `channel.purge`-Eintrag wird als Akteur
  pseudonymisiert, nennt aber weiter den Kanal (wie #243 es festgelegt hat).
- **Audit-Log nach dem Purge** (Review F1). 404 für jeden, auch den früheren Inhaber und einen
  späteren Inhaber des Logins. Für Admins bleibt das globale Audit-Log mit Kanalfilter.
- **Messfenster #69 (bis einschließlich 07.10.).** Ein Purge im Fenster zerstört die Live-Baseline
  des Kanals; nicht verweigerbar, aber vor dem 08.10. nicht anzubieten (E8).

---

## 6. Task-Reihenfolge und Teststrategie

Jeder Task ein Subagent; Pläne ohne fertigen Code. Reihenfolge nach Abhängigkeit.

**T1 — Sperrtabelle + Service (Infrastructure, Regel 11).** Entität, Migration,
`IBroadcasterChannelLockService` (`IsLockedAsync`, `LockAsync`/`UnlockAsync` im Kontext des
Aufrufers, damit sie dessen Transaktion teilen), Registrierung. Integrationstests: Upsert, Lookup,
Unlock idempotent. **Prod-Migration von Hand vor dem Deploy** (additiv).

**T2 — Purge-Pfad (Infrastructure, Regel 11).** `PurgeByBroadcasterAsync`: Live-Auflösung vor der
Transaktion, Zielmenge (ID-Zeile + nachgewiesene Namenszeile) unter Sperren in Merge-Reihenfolge,
ID-Nachprüfung, geteilter Kaskaden-/Audit-Helfer, Sperrzeile vor Commit, LEAVE je Zeile danach.
Tests: ID-Mismatch → nichts, kein Audit, keine Sperre; id-lose Zeile mit `Found`-eigener-ID →
gelöscht; `Found`-fremd → `NotBroadcaster`; `Unavailable` → `IdentityUnresolved`, nichts
geschrieben; Haupt+Duplikat beide weg mit zwei Audit-Einträgen; nicht nachweisbares Duplikat bleibt;
Kaskade vollständig; Sperrzeile nach Commit; LEAVE-Reihenfolge; Rennen mit `JoinAsync`.

**T3 — Join, Sync-Gate, Roster, Reconcile (Infrastructure/Worker, Regel 11).** `JoinAsync`:
Sperrprüfung auf Identität und gewählte Zeile, Aufhebung für Inhaber/Admin mit Audit-Detail, neuer
`ChannelJoinStatus`. `SevenTvSyncService`: Sperrprüfung neben der env-Prüfung vor dem Backfill.
`ListActiveChannelNamesAsync`: Anti-Join. `ChannelIdentityService`: Deaktivierung aktiver
gesperrter Zeilen **unter `FOR UPDATE` mit erneuter Sperrprüfung**, `reason: "locked"` mit Namen.
Tests: Mod-Join → Status, nichts geschrieben; Inhaber-/Admin-Join → Sperre weg, Audit-Detail;
Deckel-Fehlschlag lässt Sperre stehen; Sync-Gate verweigert id-lose gesperrte Zeile ohne Backfill;
Roster lässt gesperrte ID aus; **deterministischer Rennen-Test** Reconcile ↔ Join mit zwei
Kontexten (einer hält die Zeilensperre, der andere wartet und schreibt nichts); env gewinnt.

**T4 — Api (Regel 11).** `ChannelBroadcasterAuthorizationFilter`, Endpoint, `account_mismatch`,
zwei Error-Codes, `canPurgeAsBroadcaster`, 403-Mapping im Join-Handler (CS8509), **404 am
Kanal-Audit-Log ohne Zeile**. `AuthFilterMatrixTests`: 400/401/403/404/409 (Mismatch, unresolved)
/204; Join → 403 neuer Code; Audit-Log untracked → 404 auch für Login-gleichen Principal;
Filter-Reihenfolge unverändert. Architectur.md B.3-Matrix.

**T5 — Web (Regel 12).** Permissions-Model, `purgeOwnData()`, Knopf + Dialog mit Nachweisgrenze im
Text, Join-Fehlertext, Kontolösch-Hinweis (E5), beide Locales, `api-error.ts`. Spec nur für Logik:
Sichtbarkeit, Dialog-Rückgabe, Fehler-Mapping, Hinweis-Bedingung. E2E gegen gemocktes `/api/**`:
Broadcaster sieht Knopf, Mod nicht; Flow bis 204; 409-Pfade; Mod-Join → 403-Text; Dialog mit/ohne
Hinweis. **Suite nur ohne Api auf `:5151`.**

**T6 — Doku im selben Commit (Regel 3).** DECISIONS-Eintrag (englisch; Trennung env-Liste/Sperre,
vier Lesestellen, Nachweisgrenze, Ausfall-Restfall, Audit-Log-404, Epic-#200-Naht), Operations.md
(Selbstbedienung + E-Mail; Sperrtabelle; Nachweisgrenze; Aufhebung per Admin-Join; **Restore- und
Rollback-Regeln je nach E10/E12**), Epic #248, Datenschutzerklärung § 9/§ 13 in `infra-docs` (E9),
Issue für E11.

**T7 — Gates und Live-Verifikation (Regel 16).** `dotnet test`, Vitest, E2E, `coverage-local.mjs`;
live per Cookie: Purge als Broadcaster (mit und ohne ID auf der Zeile), LEAVE im Worker-Log,
Mod-Join → 403, Inhaber-Join → Sperre weg, Sync-Gate bei von Hand angelegter id-loser Zeile,
Reconcile deaktiviert ein reaktiviertes Duplikat. Codex-Zweitmeinung vor dem Merge; **Deploy nach
dem 08.10., Migration zuerst, dann Api und Worker gemeinsam.**

---

## 7. Review Codex Sol 2026-10-03

Adversariales Review der ersten Fassung, Urteil „needs-attention". Jede Behauptung wurde gegen den
Code geprüft; keine war falsch.

| # | Finding (kurz) | Geprüft | Auflösung |
|---|---|---|---|
| F1 (high) | Audit-Einträge nach dem Purge lesbar für einen späteren Inhaber des Logins | ja — `ChannelAccessService.cs:95-97` (Login-Fallback ohne Zeile), `AuditLogQueryService.cs:94-99` (Filter nur nach Name) | **Design:** `GET /{name}/audit-log` → 404 ohne Kanalzeile, vor dem Filter (3.2). Mod-Benachrichtigung wandert in den 403-Text. ID-Bindung der Einträge als Folgearbeit notiert. |
| F2 (high) | Helix-Ausfall: Mod mit Cache-Rolle legt aktive id-lose Zeile an, Sync füllt ID nach, Beobachtung läuft bis zum Reconcile | ja — `ChannelService.cs:419`, `SevenTvSyncService.cs:355-384` | **Design:** Sperre an denselben vier Stellen wie die env-Liste — Sync-Gate vor dem Backfill (fail-closed ab dem ersten Tick), Roster-Anti-Join, Reconcile (3.4). Die id-lose Zeile selbst bleibt die bekannte, akzeptierte Lücke. |
| F3 (high) | Purge löscht eine Zeile, Duplikate bleiben 180 Tage; inaktive werden nie gescannt; 204 übertreibt | ja — `ChannelIdentityService.cs:43` (nur aktive), Unique-Index auf der ID | **Design:** Zielmenge = ID-Zeile + live nachgewiesene Namenszeile, beide gelöscht (3.3). Nicht nachweisbare Altzeilen: benannte Grenze im Dialog, in Operations.md und § 9 — bewusst keine zweite Antwortform. |
| F4 (high) | Restore eines Backups rollt Sperre + Audit zurück, Boot-Recovery joint | ja — Tabelle liegt im Dump | **E10** (Runbook-Export/-Reimport, Mail-Journal, dokumentierte Grenze; Empfehlung A). |
| F5 (high) | Admin-Aufhebung vertraut dem Login-basierten `IsGlobalAdmin` | ja — `ChannelAccessService.cs:65-68` | **E11** — vorbestehend, nicht von #245 verursacht, Sperr-Aufhebung ist das kleinste Admin-Recht (Empfehlung: dokumentieren + eigenes Issue). |
| F6 (high) | Rollback auf sperr-unkundige Images schaltet alle Sperren still ab | ja — Guard prüft nur Vorwärts-Migrationen | **E12** (Runbook-Regel mit env-Übernahme, DB-Trigger, akzeptieren; Empfehlung A). |
| F7 (medium) | Reconcile-Deaktivierung ohne Zeilensperre macht einen gleichzeitigen Inhaber-Join rückgängig | ja — `ChannelIdentityService.cs:403-405` | **Design:** Deaktivierung unter `FOR UPDATE` per Twitch-ID mit erneuter Sperrprüfung; deterministischer Zwei-Kontext-Test (3.4, T3). |
| F8/F9 (medium) | Inaktive id-lose Zeilen werden nie aufgelöst → 409 ist ein Endzustand | ja — `ChannelIdentityService.cs:43` | **Design:** Live-Auflösung per `LookupByLoginAsync` im Purge-Pfad vor der Transaktion (3.3); 409 nur noch bei `NotFound`/`Unavailable`. Der Reconcile bleibt auf aktive Zeilen beschränkt — die Erweiterung wäre für diesen Fall nicht nötig. |

Was das Review **nicht** verlangt hat und was die zweite Fassung bewusst **nicht** tut: den Join im
Helix-Ausfall für neue Zeilen verweigern. Die Drei-Zustands-Abfrage existiert, damit Rejoins im
Ausfall funktionieren (DECISIONS 2026-09-24); eine Verweigerung nur für *neue* Zeilen wäre eine
eigene Policy-Änderung mit Wirkung weit über #245 hinaus. Fail-closed für die **Beobachtung** (F2)
genügt dem Zweck der Sperre.
