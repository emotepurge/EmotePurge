# Nachtlauf 2026-11-18: Plan-485 T10–T15 + Gates

Branch `nacht/485-wish-ui`, Start bei `350727d6` (T9).

## Was ist passiert

_(wird am Ende ausgefüllt)_

## Annahmen und Strittiges

- Orchestrator-Annahme: Die Subagenten schreiben ihre Übergabe nicht in `~/nachtlaeufe/485-kontext/handoff-485.md`
  (Auftrag), sondern melden sie zurück; der Orchestrator hängt sie unten unter „Übergaben“ an. Folgende Subagenten lesen
  beide Dateien.
- T12: Falls Stufe 2 die Messung nicht besteht, bleibt es heute Nacht bei Stufe 2 (Voreinstellung des Plans), Stufe 3 wird
  nicht gebaut; die Frage steht dann unter „Offene Betreiberfragen“.

## Offene Betreiberfragen

_(fortlaufend)_

## Je Task

### Rahmen

- Bash läuft im Modus „don't ask“: Ketten mit `cd`/`&&`/Pipes und `git -C` werden abgelehnt, einzelne Befehle laufen. Der
  erste T10-Agent hielt das für eine Komplettsperre und brach ab; nach Erklärung lief er durch. Kein Umweg genutzt.
- Der T10-Commit endet mit `Claude-Session:` nach der `Co-Authored-By`-Zeile (Vorgabe der Umgebung). Nicht umgeschrieben;
  ab T11 steht `Co-Authored-By` zuletzt.

### T10 Settings-Abschnitt — `f81c87c1` (Sonnet 5.5)

- Gates laut Agent: Vitest 228 Dateien / 5623 Tests grün, lint 0, format:check 0. Die Spec allein hat 21 Tests.
- Diff: 6 Dateien (neu `wish-settings-section.ts` + Spec, `channel-settings-page.ts`, beide Locales, DECISIONS (y)). Umfang
  passt.
- Annahmen: 400 `wish_settings_out_of_range` als Banner über `wishErrorKey(…,'update')` (kein Feldbezug in der Antwort);
  bei `canEdit=false` entfällt der Speichern-Knopf (Felder gesperrt + Satz); Laufzeit-Feldfehler nutzen
  `wishes.dialog.durationInvalid.*` wieder, Vorlauf hat eigene `wishes.settings.leadInvalid.*`.

### T11 Seite `my-wishes` — `6a5d59ad`, `78a03926` (Sonnet 5.5)

- Gates laut Agent nach dem zweiten Commit: Vitest 229 Dateien / 5629 Tests grün, lint 0, format:check 0, `ng build` grün.
  Die Budget-Warnung des Initial-Bundles (512,57 kB gegen 500 kB) wurde nicht gegen den Stand vor T11 verglichen. Die Seite
  ist lazy geladen; die Warnung prüfe ich bei den Schluss-Gates.
- Der Orchestrator hat nachgesteuert: Im ersten Commit hatte die Seite Status, Ton, Grund und Restzeit aus `WishRow`
  nachgebaut (gegen Plan §2.2, „nirgends sonst nachgebaut“). Der zweite Commit zieht das nach `features/wishes/wish-labels.ts`
  (rein, 6 Tests) heraus. Beide Ansichten nutzen die Datei jetzt, die `WishRow`-Specs liefen unverändert grün.
- Keine Seiten-Spec, so wie Plan §5 es vorsieht. Kein DECISIONS-Eintrag.
- Annahme: Der Plan meint mit „Stretched-Link-Vertrag §2.3“ UI-Designsprache §2.3, nicht Plan §2.3.
  404 `wishes_disabled` leitet auf `/` um.

## Gates am Ende

_(folgt)_

## Übergaben (Fortsetzung von handoff-485.md)

## T10 (f81c87c1)
- **Abschnitt:** `features/channel-workspace/wish-settings-section.ts` (`WishSettingsSection`, Selektor `app-wish-settings-section`, Input `channelName`). Exportiert `wishSettingsBody(duration: string, lead: string): WishSettingsUpdate | null`.
- **Einbindung:** `ChannelSettingsPage` hat jetzt das Signal `wishesEnabled` (aus `getPermissions`, bei Fehler false) und zeigt den Abschnitt vor `app-backfill-section`.
- **Ids** (für T14): `#wish-settings-duration`, `#wish-settings-lead`, Hinweise `…-hint`, Fehler `…-error`, Nur-Lesen-Satz `#wish-settings-read-only`, Überschrift `#wish-settings-heading`. Schutzschwelle nie gezeigt oder gesendet.
- **Live:** `liveReload` auf `wishChanged` (500 ms Entprellung); bei ungespeicherten Änderungen Hinweis mit „Neu laden“ statt Neuladen.
- **i18n neu (de+en):** `wishes.settings.{title,intro,botActive,readOnly,duration.{label,hint},lead.{label,hint},leadInvalid.{empty,not_integer,out_of_range},save,saved,changed,reload,loadFailed,retry}`. Wiederverwendet: `wishes.dialog.durationInvalid.*`, `errors.api.wish_settings_*`.
- **Erweiterung:** K5 ergänzt Schwelle und Bot-Bedienung; `canEdit` und 403-Zustand tragen das. DECISIONS (y).

## T11 (6a5d59ad, 78a03926)
- **Seite:** `features/my-wishes/my-wishes-page.ts`, `MyWishesPage`, keine Inputs. Route `my-wishes` in `app.routes.ts` (lazy, `authGuard`, neben `my-votings`). Lädt `WishService.listMine()` beim Betreten, alle `MY_WISHES_RELOAD_MS` (5 min) bei sichtbarem Fenster und beim Sichtbarwerden; Fehler lässt die letzte Liste stehen (Banner + „Erneut laden“). 404 `wishes_disabled` → `navigateByUrl('/')`.
- **Zeilen:** Stretched Link (`app-card-link` auf dem Alias) nach `['/channels', channelName, 'wishes']` + `?wish=<id>`. Abschnitte `section[aria-labelledby="my-wishes-action-title"|"my-wishes-probation-title"]`.
- **`features/wishes/wish-labels.ts`** (rein): `wishStatusKey`, `wishStatusTone`, `wishReasonKey`, `wishRemainingPart(wish, now)`, `TextPart`, `KNOWN_STATUSES`, `EXPLAINED_REASONS` — einzige Quelle für diese Abbildungen; `WishRow` und `MyWishesPage` nutzen sie, jede weitere Ansicht ebenso.
- **i18n neu (de+en):** `myWishes.{title, actionNeeded, probation, empty, emptyHint, loadFailed, retry}`. Wiederverwendet: `wishes.status.*`, `wishes.reason.*`, `wishes.row.{probation, remaining.*}`, `nav.overview`.
- **Für T12:** Der Kopfzeilen-Link braucht eigene Schlüssel (`shell.*`). Die Seite selbst zeigt keine Zahl.
- **Für T14:** `/api/wishes/mine` mocken; Selektoren über die Abschnitte und den Alias-Link.
