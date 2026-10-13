# Plan #403 — Emote-Slots: eine Namensordnung für D13 und D2 (UTF-8-Bytefolge statt UTF-16)

Erstellt am 2026-10-13 im Worktree `EmotePurge-403` (Branch `fix/403-name-order`, Basis `origin/main` @ `94a84782`).
**Status: Entwurf, vor der adversarialen Prüfung (Codex Sol).**
Quellen: Issue #403 (keine Kommentare), [Spec](../superpowers/specs/2026-10-10-emote-slots-spec.md) D2, D12, D13, §6.2 Schritt 8,
§6.3, §9.1 (`mergeSetView`, `siblingNames`), §13 AK 10; DECISIONS #365 („Child 2", „Child 3", „Child 6");
[Plan-375](https://github.com/emotepurge/EmotePurge/blob/feat/375-backfill-slots/docs/plans/Plan-375-Emote-Slots-Backfill.md) §12 A
(Branch `feat/375-backfill-slots`). Formatvorlage: Plan-373, bewusst klein gehalten.

**Lesehinweis:** **[E]** = Planer-Entscheidung, kein Spec-Text. **Betreiber 13.10.** = vom Betreiber entschieden.

## 1. Umfang

### 1.1 Drin (Betreiber 13.10.: eng, nur Vergleicher-Tausch, kein Umbau)

1. `EmoteSlotReconcilePlanner.Derive` — der Namens-Gleichstand von D13 (`ThenBy(Name, StringComparer.Ordinal)`) wird UTF-8-Bytefolge.
2. `EmoteSlotReconcilePlanner.Settle` — nur die Gleichstandszeile der Kandidatenwahl („ältester Eintrag, Gleichstand kleinster Name").
3. `EmoteSlotDeltaPlanner` — **keine Codeänderung**: er ruft `Derive` und folgt damit automatisch; er bekommt einen Regressionstest (§7).
   Dasselbe gilt für die set-zentrischen Berichte (`EmoteService.ApplyDerivedNameAndDate`), die ebenfalls nur `Derive` aufrufen.
4. Client-Spiegel `d2Names` in `web/src/app/core/usage-stats/merge-set-view.ts` (heute JS-`<` = UTF-16) auf UTF-8-Bytefolge, über
   einen neuen reinen Vergleicher in `core/` mit eigenem Spec.
5. Spec: D13 und die beiden Stellen, die den Gleichstand wiederholen (§6.3, AK 10) — s. Rückfrage R2.
6. Ein DECISIONS-Satz im Eintrag #365 plus der neue Dateipfad in dessen `Betrifft`-Zeile.
7. Der Regressionstest Ergänzungsebene (Surrogatpaar) gegen U+E000–U+FFFF bei `AddedAt`-Gleichstand.
8. Die Bestandsprüfung aus dem Issue (§6).

### 1.2 Draußen (mit Besitzer)

| Was | Warum / wer |
|---|---|
| `confirmationNamesFor` in `web/src/app/features/usage-stats/usage-stats-page.ts` (zweiter Client-Spiegel, ebenfalls JS-`<`) | #378 baut die Stelle gerade um (Betreiber 13.10.). Der neue Vergleicher aus §4 liegt in `core/` und ist von dort aus wiederverwendbar; #378 entscheidet selbst. |
| Aufteilen von `Settle` in Zielwahl und Mutation | #375 T4. #403 tauscht nur die Vergleicherzeile, damit es vor T4 mergen kann (Plan-375 §12 A). |
| `EmoteSlotDeltaPlanner` Z. 115 (`names … Order(StringComparer.Ordinal)`) und Z. 414 (`created.OrderBy(Name, Ordinal)`) | **[E]** Reine Iterations-/Ausgabereihenfolge für Determinismus; keine davon entscheidet „ältester Alias" — `Derive` bildet das Minimum über alle Slots selbst. |
| `ArchivedSlotUpsert` (`ThenBy(Name, Ordinal)`), `BindByName`, `ResumeArchivedSlots`, `BindByDate` | Ordnen nur nach `Id` (ASCII-GUIDs, in beiden Ordnungen gleich) oder vergleichen Namen auf **Gleichheit**; Gleichheit bleibt ordinal (Spec §4.1 „Name … ordinal"). |
| `compareByName` in `merge-set-view.ts` | Gleichstand dort ist `slotId` (ASCII), nicht der Name. |
| Plan-372 §2.3 („`Name` ordinal") | Historischer Plan, wird nicht nachgezogen; maßgeblich sind Spec und DECISIONS. |

### 1.3 Migration

**Keine.** Kein Schema, keine Datenkorrektur: gespeicherte Slot-Namen hängen nicht von der Ordnung ab, und `Emote.Name` leitet der nächste
Abgleich ohnehin neu ab (§6). Der Deploy-Hinweis im PR nennt trotzdem wie üblich alle auf Prod ausstehenden Migrationen von `main`.

## 2. Die Regel (Vertrag)

- **Eine Namensordnung für jeden Gleichstand „ältester Alias":** UTF-8-Bytefolge = Vergleich der Unicode-Skalarwerte = was Postgres'
  `COLLATE "C"` auf einer UTF-8-Datenbank tut. Implementiert ist sie schon als `BaseSlotRule.Utf8Order` (D2-Basis-Slot, `siblingNames`,
  `OldestAliasFirst` für Stimmzettel und Tags, Seed- und Rollback-SQL). D13 (`Derive`) und die Settle-Kandidatenwahl übernehmen genau diesen
  Vergleicher — keine zweite Implementierung im Backend.
- UTF-16-Ordinal und UTF-8 unterscheiden sich nur, wenn sich zwei Namen an einer Stelle erstmals unterscheiden, an der der eine ein
  Zeichen ≥ U+10000 (Surrogatpaar, Codeeinheit 0xD800–0xDBFF) und der andere eines aus U+E000–U+FFFF hat. UTF-16 setzt dann das
  Ergänzungszeichen vorn, UTF-8 (Leitbyte 0xF0 gegen 0xEE–0xEF) dahinter.
- **Gleichheit bleibt ordinal und case-sensitiv** (Bindung nach Namen, Dedup, `DistinctBy`). Nur *Ordnungen* zwischen Alias-Namen wechseln.
- `Derive`: `AddedAt` aufsteigend, `null` zuletzt, dann `Name` in `Utf8Order`, dann `Id` ordinal. **[E]** Die `Id`-Stufe bleibt ordinal
  (GUID-Strings, ASCII → identisch zu `D2Order`); kein Umbau auf `D2Order`/`BaseSlotCandidate` (Rückfrage R3).
- Settle-Kandidat bei |G| ≥ 2: kleinstes `AddedAt`, Gleichstand kleinster Name in `Utf8Order`.
- **Client:** dieselbe Ordnung für `siblingNames` der Live-Einträge. Der Client-Vergleicher bildet `CompareUtf8` 1:1 nach, inklusive
  Randfall: einzelne (ungepaarte) Surrogate zählen als U+FFFD, und sind zwei Namen danach skalarweise gleich, entscheidet der
  UTF-16-Vergleich. **[E]** — eine Zeile mehr, dafür ist die Parität vollständig und testbar.

## 3. Backend (`src/EmotePurge.Infrastructure/Services/EmoteSlotReconcilePlanner.cs`)

- `Derive`: die Namensstufe auf `BaseSlotRule.Utf8Order`. XML-Kommentar „then name ordinal" → UTF-8-Bytefolge mit Verweis auf D2/D13.
- `Settle`: die Zeile `.ThenBy(i => entries[i].Name, StringComparer.Ordinal)` auf `BaseSlotRule.Utf8Order`; Kommentar „tie →
  ordinally smallest name" entsprechend. Sonst nichts an `Settle` (Zielsuche, Mutation, Reihenfolge bleiben).
- Klassenkommentar „All name comparisons and orderings are ordinal and case-sensitive (U14)" präzisieren: Gleichheit ordinal und
  case-sensitiv; Namens-Gleichstände in Ordnungen „ältester Alias" in UTF-8-Bytefolge (#403).
- `BaseSlotRule` bleibt unverändert (der Vergleicher ist `public static` in einer `internal` Klasse derselben Assembly). Die Kommentare an
  `D2Order`/`Utf8Order` dürfen um „auch D13 und die Settle-Wahl" ergänzt werden.
- `EmoteSlotDeltaPlanner`, `EmoteService`: kein Code.

## 4. Frontend

- **Neue Datei [E]:** `web/src/app/core/usage-stats/d2-order.ts` mit zwei reinen, exportierten Funktionen:
  - `compareUtf8(a, b)` — Vorzeichen wie `BaseSlotRule.Utf8Order` (negativ/0/positiv), Iteration über Codepoints, Randfall wie §2.
  - ein D2-Vergleicher für Einträge der Form `{ name, instant: number | null }`: `instant` aufsteigend, `null` zuletzt, dann `compareUtf8`
    auf dem Namen. Genau die Form, die `d2Names` heute inline hat — und die `confirmationNamesFor` (#378) ebenfalls inline nachbaut, damit
    #378 ohne Anpassung darauf umsteigen kann.
  - Ort `core/usage-stats/`: `merge-set-view.ts` liegt in `core/` und darf nicht aus `shared/` importieren (Schichtentabelle CLAUDE.md);
    `features/` darf aus `core/` importieren.
- `d2Names` sortiert über den neuen Vergleicher; die Dedup per `Set` (ordinale Gleichheit) bleibt. Doc-Kommentar „then name ordinal" →
  „UTF-8-Bytefolge wie der Server (D2)".
- Keine sichtbare UI-Änderung außer der Reihenfolge von `siblingNames` in genau dem Randfall → **kein E2E** nötig.

## 5. Spec und DECISIONS

- **Spec (deutsch):** D13 „Gleichstand ordinal" → Gleichstand nach Namen in UTF-8-Bytefolge (Unicode-Skalarwerte, wie `COLLATE "C"`;
  dieselbe Ordnung wie D2/`siblingNames`), dann `Id`; mit Kurzbegründung „UTF-16-Ordinal weicht bei Ergänzungsebene gegen U+E000–U+FFFF
  ab (#403)". §6.3 („Gleichstand: ordinal kleinster Name") und AK 10 („Gleichstand ordinal; alle `null` → ordinal") verweisen auf D13
  statt das Wort „ordinal" zu wiederholen (R2). Nichts sonst an der Spec.
- **DECISIONS (englisch, Regel 3, im selben Commit wie der Backend-Tausch):** ein Satz im Eintrag #365, Absatz „**Child 2, addition
  2026-10-12: the slot reconcile, its locks and its recoveries**" (Z. ~503), direkt hinter dem Satz „… follow D13 strictly …". Inhalt:
  seit #403 (2026-10-13) brechen D13 und die Settle-Kandidatenwahl einen Namens-Gleichstand in derselben UTF-8-Bytefolge wie D2
  (`BaseSlotRule.Utf8Order`), der Client spiegelt sie in `d2Names`; Gleichheit bleibt ordinal. **Nicht** zwischen „Child 4" und „Child 6"
  (dort schreibt #375 „Child 5"), **nie** „Child 8" anfassen (#378).
- **`Betrifft`:** den neuen Pfad `web/src/app/core/usage-stats/d2-order.ts` in Zeile 468 hinter `merge-set-view.ts` einfügen — **nicht** an
  die letzte `Betrifft`-Zeile anhängen, dort hängt #378 eine Zeile an (Textkonflikt).

## 6. Bestandsprüfung: würden gesettelte Slots umbenannt?

**Analyse [E].** Die Ordnung schreibt an genau zwei Stellen:
1. **Settle** benennt einen Seed beim Umschlüsseln. Das passiert nur für **unsettled** Seeds des aktiven Sets; ein gesettelter Slot wird nie
   wieder gesettelt. Gesettelte Slot-Namen ändern sich durch #403 also **nie** — betroffen wäre nur ein künftiger Settle, und der läuft
   dann schon mit der neuen Ordnung.
2. **`Derive`** schreibt `Emote.Name`/`FirstSeenAt` (nicht den Slot-Namen) bei jedem Abgleich, Delta und set-zentrischen Bericht, sobald
   ein Slot sich ändert. Ein Namenswechsel nach dem Deploy setzt voraus: ein Emote mit ≥ 2 **aktiven eigenen** Slots (nicht archiviert,
   `SeedUnsettled = false`), gleiches `AddedAt` (oder beide `null`), und Namen, die sich erstmals an der Klassen-Grenze aus §2 unterscheiden.
   Folge wäre einmalig ein anderes `Emote.Name` plus ein `channel.synced` — kosmetisch, keine Zahl ändert sich.

**Dev (gemessen bei der Planerstellung, 13.10., nur lesend gegen `emotepurge-dev-postgres`):** 4 335 Slots, davon **0 eigene** (alle
unsettled Seeds), 0 Slot-Namen mit Zeichen ≥ U+E000; 5 434 Emotes, 57 mit Nicht-ASCII-Namen, **0** mit Ergänzungsebene, **0** mit
U+E000–U+FFFF. Die Kandidatenabfrage unten liefert 0 Zeilen. → Auf Dev wird nichts umbenannt.

**Prod (Betreiber führt aus, Tunnel wie CLAUDE.md „Prod-Migration", Passwort als Platzhalter):** Erwartet 0 eigene Slots, weil
`SevenTv:SlotReconcile:Enabled` aus ist (Issue). Abfrage, nur lesend:

```
psql 'host=localhost port=15432 dbname=emotepurge user=emotepurge password=<PROD-PW>' -c 'BEGIN READ ONLY' \
 -c 'SELECT count(*) FILTER (WHERE NOT "SeedUnsettled") AS own, count(*) FILTER (WHERE NOT "SeedUnsettled" AND NOT "IsArchived") AS own_active FROM "EmoteSlots"' \
 -c $'SELECT "EmoteId", "AddedAt", array_agg("Name" ORDER BY "Id") FROM "EmoteSlots" WHERE NOT "SeedUnsettled" AND NOT "IsArchived" GROUP BY "EmoteId", "AddedAt" HAVING count(*) >= 2 AND bool_or("Name" ~ \'[\\U00010000-\\U0010FFFF]\') AND bool_or("Name" ~ \'[\\uE000-\\uFFFF]\')' \
 -c 'ROLLBACK'
```

Die zweite Abfrage ist eine Obermenge (sie prüft nicht, ob die Grenze die *erste* Abweichung ist; `GROUP BY` fasst `NULL`-Daten
zusammen). Jede Zeile wird von Hand gegen beide Ordnungen verglichen. Die Zeichenklassen sind auf Dev gegen `U+1F600`/`U+FF5E` geprüft.
T3 legt dem Betreiber die Abfrage im PR-Text vor (für bash auf der Devbox angepasst). **Wo festgehalten:** Ergebnis als Kommentar auf #403
und im PR-Text; spätestens vor dem Einschalten in #379. **Reihenfolge:** #403 deployt vor dem Flag-an von #379 → selbst theoretische
Kandidaten entstehen erst unter der neuen Ordnung.

## 7. Tests

**Backend (`tests/EmotePurge.Infrastructure.Tests/Unit/`, container-frei).** Testpaar wie in `BaseSlotD2OrderTests`: `"\U0001F600"`
(UTF-16 vorn, UTF-8 hinten) gegen `"～"`; zusätzlich `"a\U0001F600"` gegen `"a￿"` (Abweichung nicht an Stelle 0).
- `EmoteSlotReconcilePlannerTests`, Abschnitt `Derive`: Gleichstand mit beiden `AddedAt = null` und mit gleichem Datum → `"～"` gewinnt,
  unabhängig von der Eingabereihenfolge. **Gleichlauf-Test:** für eine Reihe von Namenspaaren (ASCII, Latin-1 gegen U+E000, die beiden
  Grenzfälle) ist der Name von `Derive` gleich dem Minimum der Kandidaten unter `BaseSlotRule.D2Order` — pinnt „eine Regel" und schlägt an,
  wenn jemand (etwa bei der #375-Konfliktauflösung) die Zeile zurücktauscht.
- Dieselbe Datei, Settle: Seed des aktiven Sets, Live `[emoji(t), tilde(t)]` → Seed wird auf `tilde` umgeschlüsselt, `emoji` neu. Der
  bestehende ASCII-Fall `Settle_OldestTie_PicksTheOrdinallySmallestName` bleibt (in beiden Ordnungen gleich), ebenso
  `DerivedNameAndFirstSeenAtFollowTheOldestActiveSlot` ('B' < 'a' gilt in beiden).
- Über `PlanLiveGroup`: undatierte Live-Gruppe `[emoji, tilde]` ohne Slots → `Derivation.Name = tilde`.
- `EmoteSlotDeltaPlannerTests`: S0 (Emote fehlt), **ein** Dispatch mit zwei `pushed`-Namen `[emoji, tilde]` → beide neuen Slots tragen
  dasselbe `now` (Gleichstand), `Derivation.Name = tilde`. Das ist der realistischste Weg zum Gleichstand und deckt „Delta folgt `Derive`".
- Set-zentrische Berichte: **[E]** kein eigener Test — reiner Aufruf von `Derive`, durch die Unit-Tests gedeckt.

**Frontend (Vitest, Regel 12, co-located).**
- `core/usage-stats/d2-order.spec.ts`: `compareUtf8` mit denselben Paaren wie `Utf8Order_AgreesWithAByteComparisonOfTheUtf8Encoding`
  (C#-InlineData) plus Emoji/Tilde — Erwartung jeweils gegen einen Bytevergleich der `TextEncoder`-Kodierung, beide Richtungen
  (Antisymmetrie), Gleichheit → 0, Präfix zuerst; der Randfall „ungepaartes Surrogat" (gegen U+E000: UTF-8-Regel sagt hinten, JS-`<` vorn).
  D2-Vergleicher: Datum vor Name, `null` zuletzt, Name in UTF-8.
- `merge-set-view.spec.ts`: ein Fall, in dem zwei Live-Geschwister ohne Datum (oder gleiches `addedAt`) `"\U0001F600"`/`"～"` heißen →
  `siblingNames` der dritten Zeile in UTF-8-Reihenfolge. Bestehende ASCII-Erwartungen (Z. 431–434) unverändert.

## 8. Gates, Live-Verifikation, Deploy

- `dotnet test EmotePurge.slnx` (Docker läuft; die Änderungen sind Unit, die Suite trotzdem ganz), `npm --prefix web test -- --watch=false`,
  `npm --prefix web run lint`, `npm --prefix web run format` (Prüfung) und `dotnet format EmotePurge.slnx --verify-no-changes`. **Kein E2E**
  (keine UI-Verhaltensänderung außer dem Randfall, der im Unit-Spec steckt).
- `node scripts/coverage-local.mjs` **nach** dem Commit (zählt nur Committetes). Neue TS-Datei voll gedeckt; die zwei geänderten C#-Zeilen
  laufen durch die neuen Tests.
- Live-Verifikation (Regel 16): s. Rückfrage R1.
- Deploy: nur Images, keine Migration, Flag bleibt aus; kein Verhalten sichtbar, bis #379 das Flag einschaltet.

## 9. Aufgabenliste (je Aufgabe ein Subagent mit frischem Kontext; nacheinander — gemeinsamer Git-Index)

| # | Aufgabe | Modell | Aufwand | Liest | Fertig, wenn |
|---|---|---|---|---|---|
| T1 | Backend-Tausch (`Derive`, Settle-Zeile, Kommentare) samt Tests aus §7 Backend, Spec-Text §5, DECISIONS-Satz + `Betrifft`-Pfad aus §5 | sonnet | 0,25 T | dieser Plan §2, §3, §5, §7; `BaseSlotD2OrderTests` als Muster | neue Tests vorher rot, danach grün; `dotnet test EmotePurge.slnx` grün; `dotnet format` sauber; ein Commit `fix(slots): …` mit Spec und DECISIONS (Regel 3) |
| T2 | Client: `d2-order.ts` + Spec, `d2Names` umstellen, Fall in `merge-set-view.spec.ts` | sonnet | 0,25 T | §2, §4, §7 Frontend; `BaseSlotRule.CompareUtf8` als Referenz | Vitest, Lint, Prettier grün; `usage-stats-page.ts` unberührt; ein Commit `fix(web): …` |
| T3 | Review, Gates, PR | **opus** (Review); Codex Sol `/codex:review --model gpt-6.1-sol` | 0,25 T | Branch-Diff, §1.2, §6, §8, §10 | alle Gates aus §8 grün, Coverage-Schätzung ≥ 80 %, Codex-Befund unverändert vorgelegt, PR mit Prod-Abfrage aus §6, Deploy-Hinweis (keine eigene Migration, ausstehende `main`-Migrationen gelistet) und Hinweis an #375 (Gleichstandszeile liegt jetzt in UTF-8; T4 verschiebt sie nur) |

## 10. Überschneidungen

- **#375 (T4 teilt `Settle`):** #403 soll vor T4 mergen (Plan-375 §12 A, Betreiber 13.10.). Der Diff in `Settle` ist eine Zeile; der
  Gleichlauf-Test aus §7 ist der Wächter, falls ein späterer Rebase die Zeile auf `StringComparer.Ordinal` zurücksetzt. Schafft #403 den
  Merge vor T4 nicht, wartet es bis nach #375 und tauscht die Zeile in der neuen Zielwahl (gleiche Änderung, andere Stelle).
  DECISIONS: #375 schreibt zwischen „Child 4" und „Child 6" — #403 schreibt in „Child 2" (Z. ~503) und in `Betrifft` Z. 468.
- **#378:** berührt `usage-stats-page.ts`, die letzte `Betrifft`-Zeile, „Child 8" und Spec §9.3 — alles außerhalb von #403. Nach dem Merge
  kann #378 `confirmationNamesFor` auf den D2-Vergleicher aus `core/usage-stats/d2-order.ts` umstellen (Hinweis im PR an #378).

## 11. Rückfragen an den Betreiber

- **R1 — Live-Verifikation (Regel 16).** Empfehlung: **keine** Live-Probe gegen 7TV. Die Änderung ist ein reiner Vergleicher; der
  Gleichstand lässt sich live kaum herstellen (REST-`added_at` ist je Eintrag eigen, ein Delta-Gleichstand braucht zwei Pushes in einem
  Dispatch), und Prod hat das Flag aus. Die Unit-Tests aus §7 sind das Orakel. Alternative: lokal ein Wegwerf-Set mit zwei Aliassen
  „😀…"/„～…" und Flag an, Abgleich, `Emote.Name` lesen — kostet ein `:5151`-Fenster (Absprache mit der #378-Sitzung).
- **R2 — Spec über D13 hinaus.** Empfehlung: §6.3 und AK 10 im selben Commit auf D13 verweisen lassen; sonst sagt die Spec an zwei Stellen
  weiter „ordinal" für dieselbe Wahl. (Die Kollaps-SQL-Zeile im Rollback, §12.2, sagt ebenfalls „ordinal", ist aber mit `COLLATE "C"`
  implementiert, also schon UTF-8 — bleibt unangetastet.)
- **R3 — `Derive` nur Namensstufe tauschen oder ganz auf `D2Order` abbilden.** Empfehlung: nur die Namensstufe (kleinster Diff vor #375 T4;
  `Id` ist ASCII, also gleich). Der Gleichlauf-Test sichert die Gleichheit mit `D2Order` trotzdem ab.
