# UI design language

Binding requirement for all UI work under `web/`.

**This document describes the state that applies today, and nothing else.** What used to be different, which rule superseded an earlier one and why, is in [DECISIONS.md](DECISIONS.md) — there is no prehistory here, no intermediate stage and no "until then it was". Whoever wants to know how a rule came about looks for it there with `grep`; whoever wants to know what to do today reads only here.

Format per rule: **What applies** · **When to apply** · **Reference** (file path of the model implementation). This document is the **how**, DECISIONS.md the **why** log. Where an older DECISIONS wording and this document disagree, this document wins.

Whoever builds new UI works through the [checklist in section 11](#11-checklist-building-new-ui) and verifies with the [audit harness (section 12)](#12-verification-via-the-ui-audit-harness) — then no new UI/UX audit is needed.

---

## 1. Scope of application

- Applies to everything under `web/` — new pages, new components, changes to existing ones.
- Complements [`web/.claude/CLAUDE.md`](../web/.claude/CLAUDE.md) (Angular conventions, signals, standalone) — both apply cumulatively.
- Existing code is not rewritten retroactively (the CLAUDE.md language rule applies analogously): deviations are fixed the next time the spot is touched, not in bulk refactorings.

## 2. Colour & surfaces

### 2.0 Colour comes from tokens, not from the palette

- **What applies:** No template, no variant map and no component class writes a Tailwind palette colour (`slate-*`, `purple-*`, `red-*`, `amber-*`, `emerald-*`, `blue-*`, `pink-*`, `white`, `black`) directly. Only the semantic utilities from the token set are allowed. Palette names live in exactly **one** place: in the token block of `web/src/styles.css`.

  | Role | Utilities |
  |---|---|
  | Surfaces | `bg-page` (the ground) · `bg-surface` (the one raised surface, see 2.1) · `bg-surface-inset` · `bg-surface-inset-hover` · `bg-field` |
  | Borders | `border-border` · `border-border-strong` · `border-border-field` (controls, 3:1 mandatory — see 5.1) |
  | Text | `text-fg` · `text-fg-body` · `text-fg-secondary` · `text-fg-muted` · `text-fg-disabled` |
  | Accent | `bg-accent` · `bg-accent-solid` (+`-hover`) · `bg-accent-selected` · `text-accent-fg` (accent **as text**) · `bg-accent-wash` · `text-on-accent` (text **on** a filled accent surface) |
  | Tones | `{success,warning,danger,info,neutral}-{wash,fg,solid,dot}` — `wash` = tinted surface, `fg` = type on it, `solid` = filled surface with `on-accent` type, `dot` = small meaning-bearing graphic (status dot, bar fill; owes 3:1, not 4.5:1) |
  | Other | `shadow-overlay` (popover/dialog) · `bg-emote-canvas` (image surface of an emote cell) |

- **When to apply:** Always. If a new UI needs a colour that does not exist as a token, **the token is added** — with a value for **both** modes and with a computed contrast proof in the commit message — rather than using the palette. If the modes differ structurally instead of only in value, that is first of all a hint that the token is cut wrong; only after that a CSS variant. **There is no theme-fixed colour any more** — `bg-emote-canvas` was the one documented exception and was withdrawn after looking at it (see 2.4). Having its own token means "own role", not "fixed value".
- **Tone names are meanings, not colours.** `StatusBadgeTone` and `SlotBudgetTone` are named `accent · info · success · neutral · warning · danger`. A caller asking for `red` is asking for a colour value — and there is none, because behind `danger` there is a different one per mode. This holds for every new tone union.
- **Filled buttons get darker on hover — in both modes.** `*-solid-hover` is always one step below `*-solid`. The rule is not aesthetics but the only way to keep the hover contrast-safe: `on-accent` is white in both modes, so a *lighter* hover can only take contrast away. **No tool catches a violation** — axe only evaluates what is rendered right now, and a hover never is.
- **The values deliberately are not here**, only in `web/src/styles.css`. A second value table in Markdown drifts from the first token added afterwards; the token block in the code is necessarily touched on every colour change and cannot deviate from itself. This document carries the **roles**, the code the values. The contrast proof belongs in the commit message of the change that makes it necessary; the tightest pair at any time is in §10 and is carried along there.
- **Enforced**, not requested: `npm run lint` runs `web/scripts/check-color-tokens.mjs` along with it and forbids palette utilities below `web/src/app/`.
- **Reference:** `web/src/styles.css` (token block).

### 2.0a Themes: switching, persistence, no flash

- **Three states, not two:** `'system' | 'light' | 'dark'` (`THEME_PREFERENCES` in `core/theme/theme.service.ts`), default `'system'`. A two-state toggle cannot express "follow the system" — whoever switched once would otherwise be stuck forever.
- **The resolved mode sits as `data-theme` on `<html>`**, not on the shell `<div>`: the CDK overlay container hangs outside the app shell and would otherwise inherit no tokens.
- **`public/theme-init.js` runs synchronously in the `<head>`, before the stylesheet.** Angular boots only after the first paint; without this script the dark page flashed up when the system preference was light. The `ThemeService` **reads** the attribute that was set as its initial state instead of determining it again. The script is deliberately its own file and not an inline block — that way it needs no nonce under the API's CSP.
- **Persistence: `localStorage['emotepurge.theme']`.** A presentation preference, not session information — the rule "the auth session does not belong in `localStorage`" is untouched by this. In addition the service listens to `matchMedia('(prefers-color-scheme: dark)')` so that a system change takes effect immediately while the app is open, as long as `'system'` applies.
- **`theme-color` is two metas** (`data-theme-mode="light"|"dark"` + `media`), so that the `'system'` case is correct without JavaScript; on an explicit choice the service additionally corrects it via the DOM.
- **`color-scheme` is set per mode on `:root` in the token block** — that way `input[type="time"]`, scrollbars and autofill follow on their own, without a single rule of our own.
- **The control is `<app-display-preferences>`** — two labelled `SegmentedControl` groups (appearance, language) that live exclusively in the panel of `<app-account-menu>` (§7.1). There is no second place in the repo where theme or language can be changed; shell, landing and login put the same menu in the same spot. Deliberately not a click-through icon button: its next state cannot be announced. And deliberately `role="radiogroup"` instead of `role="menuitemradio"` — the panel holds mixed children, for which `role="menu"` does not apply.
- **Logged in they sit one level deeper**, behind the row "Settings"; logged out the panel shows them directly. That is the Notable-how-often rule applied to *place* instead of colour: both are set once and afterwards only confirmed, so neither may be the largest item in a panel that grows with the product. Measured on the prototype: the root shrinks from 322 to 190 px, and the gap grows with every further entry. **"Just make it smaller" was the rejected alternative** — it gained 20 px and would have cost the thumb target *everywhere*, including where a thumb is operating. Instead things are only made smaller where there is none: every row and segment of the panel follows the menu-row rule from §7.1 (`min-h-11 sm:min-h-9`). Logged out the level does not apply: a row that opens a sub-view with its only content is a door in front of a door.
- **Reference:** `web/src/app/core/theme/theme.service.ts` (+ `theme.service.spec.ts`), `web/src/app/shared/ui/display-preferences.ts`, `web/src/app/shared/ui/account-menu.ts`, `web/public/theme-init.js`, `web/src/index.html`, flow test `web/e2e/theme.spec.ts`.

### 2.1 Surfaces: ruled row and borderless section

- **There is no card class.** `styles.css` carries neither `.app-card` nor an equivalent, and none is coming back. Surfaces are made from ruled rows, borderless sections and tinted blocks — not from bordered rectangles.
- **The test question before every new demarcation:** a card is a border against a **different kind of** neighbour. If every neighbour is the same kind of thing — in a list every row is a row, on a diagnostics page every section is a subsystem reporting on itself — then a border draws eight rectangles where one line says "list" more clearly, and competes with the only thing that has to stand out: the subsystem that is not okay. Whoever wants to demarcate a surface answers this question first.
- **Ruled list:** `-mx-3 divide-y divide-border border-y border-border` on the `<ul>`, `px-3 py-3` per `<li>`. Clickable rows additionally `relative transition-colors hover:bg-surface-inset`. The negative margin lets the hover wipe breathe out past the text while the contents stay on the left edge of the page. Implemented in `overview-page.html`, `vote-session-list-page.html`, `my-votings-page.ts`, `admin-channels-page.ts`, `admin-users-page.ts`, `audit-log-list.ts`.
- **Section stack:** `flex flex-col gap-3 border-t border-border pt-4` per section, heading on the left and status marker on the right. Implemented in `admin-monitoring-page.ts`, `admin-channel-detail-page.ts`.
- **Tinted block** (`rounded-md bg-surface-inset px-3 py-3`) for what actually borders on something different in kind: panels *inside* a page, the 7TV token form, the progress run. Surface instead of border — the tint says "something else applies here" without drawing a rectangle.
- **The depth effect is produced differently per mode, and that is intentional:** dark separates via surface lightness, light via a real elevation shadow — in light mode sheet and ground are only 1.10:1 apart and could not carry an elevation at all. The *direction* stays the same in both modes: raised moves away from the ground, inset (`surface-inset`) goes back towards it. The overlay is the only raised surface of the app, `--ep-shadow-overlay` therefore the only shadow token.
- **Reference:** `web/src/styles.css`, `web/src/app/features/overview/overview-page.html`.

### 2.2 Hover only where clickable

- **What applies:** A hover reaction comes **only** on rows that are actually clickable — `relative transition-colors hover:bg-surface-inset`. Static rows and sections stay mute: hover must never promise a click that does not exist.
- **When to apply:** Exactly when the row fulfils the stretched-link contract (2.3). Conditional application is allowed and the normal case — a `[class]` binding switches in the hover part only where clickable (`overview-page.html` does that per channel via `isTracked`).
- **Reference:** `web/src/app/features/admin/admin-channels-page.ts` (conditional), `web/src/app/features/overview/overview-page.html`.

### 2.3 Stretched-link contract (rows clickable across their full area)

- **What applies:** Clickable list rows use the stretched-link pattern via `.app-card-link` (the Inclusive Components "Cards" pattern). The class name names the pseudo-element, not a surface — it applies unchanged to ruled rows. The contract has three mandatory parts:
  1. The row container is `relative`.
  2. **One** short real link (title/name) carries `app-card-link` — its `::after` stretches the click area across the whole row; screen readers hear only the short name.
  3. **Every** secondary action in the row (buttons, further links) sits in a container with `relative z-10` and stays separately clickable and focusable.

  Canonical markup:

  ```html
  <li class="relative flex items-center gap-4 px-3 py-3 transition-colors hover:bg-surface-inset">
    <a [routerLink]="[...]" class="app-card-link max-w-full truncate font-medium">#{{ name }}</a>
    <div class="relative z-10 ml-auto flex gap-2">
      <button type="button" appButton="danger" (click)="...">…</button>
    </div>
  </li>
  ```

- **When to apply:** Every list row whose primary action is "open/view". **Not:** wrapping the whole row as an `<a>` (invalid with inner buttons, bloated accessible name) or a JS click handler on the container.
- **Reference:** `web/src/app/features/overview/overview-page.html`, `web/src/app/features/admin/admin-channels-page.ts`, `web/src/app/features/voting/vote-session-list-page.html`.

### 2.4 Image surface of an emote cell

- **What applies:** The surface on which a 7TV emote is drawn is `bg-emote-canvas` — a **token of its own**, not `surface-inset`. Reason: the image material is foreign, drawn for dark chats, and contains white type and light outlines. This surface will therefore at some point have to be decided differently from "some inset surface or other", and then it has to be one line.
- **It follows the theme** instead of staying dark in both modes. A theme-fixed dark canvas would sit on **every** cell and would be the loudest element on a light page — while the emotes it is meant to protect are the minority. The price deliberately accepted: an emote with a white outline loses its contour in light mode. The trade sits in exactly one place and is one line.
- **The selection wash lies on the cell, not under the image.** Otherwise wash and image material fight over the same pixels — the `inset-ring` (8.5) carries the selection, the surface only reinforces it.
- **The surface is flat — no alpha checkerboard.** A checkerboard answers a question these pages never ask (*which pixels are transparent*) and makes the never-used band look like a different kind of thing instead of the same thing with a zero on it.
- **`.app-sprite-cell-void` is ballot-only.** There it marks an **archived** emote in the middle of a mixed grid in which no heading says so — the case a plate of its own exists for. In the atlas it has no business: "never used in the range" is already said there by the band heading, the printed 0 on every cell and the missing fill bar. **A marking that applies to every member of its own group marks nothing** — it only costs the evenness of the sheet.
- **Reference:** `web/src/styles.css` (`--color-emote-canvas`, `.app-sprite-cell`), `web/src/app/features/usage-stats/usage-stats-page.html`, `web/src/app/features/voting/vote-session-detail-page.html`.

### 2.5 The sprite sheet: bands, sidecar, dock

The usage page and the ballot are not lists but **one sheet of uniform cells**. What applies to them applies to nothing else in the app — and vice versa.

- **The bands are Pareto cuts out of the set itself, not fixed thresholds.** `heavy` = the emotes that together make up the first half of usage · `regular` = up to 80% · `rare` = the rest with at least one hit · `dead` = zero. Fixed limits ("from 1000 on it is a lot") are right for a large channel and meaningless for a small one — there every emote would land in the same band and the grouping would carry no information. The order is fixed (`USAGE_BAND_ORDER`, heavy first): the reader scans downwards towards the candidates.
- **The cut is made over values, not over ranks.** A rank cut arbitrarily splits a group of equal counts down the middle; a value cut keeps it together.
- **A cell's fill bar measures against the top of its *band*, not against that of the set.** Otherwise every bar in the tail would be empty and the band would say nothing.
- **Band headings are a hairline plus a label** — the same shape as the `EmptyState` (6.2) and the landing steps, not a fifth kind of heading.
- **A band heading names both numbers with their unit, and the percentage is measured.** `BACKBONE · 53% of usage · 4 emotes`. The 50/80 marks are the thresholds of the *calculation*, not the share a band ends up carrying: the cut lies at the first emote that lifts the cumulative sum above the mark, and takes it along whole. Really measured that is 50–53% instead of 50%, in the E2E fixture even 76%. A label promising "the first half of usage" therefore claims a precision the number does not have — and on top of that reads temporally instead of as a share. The denominator is the usage of the **whole** set, the numerator the **visible** emotes of the band: with an active name filter, percentage and count shrink together instead of claiming a share for three leftover emotes. Anything that rounds to 0% but is not zero appears as `<1%`. **Where one line does not fit, the heading breaks between the two numbers, never inside one** — German at 360 px is that case (`REGELMÄSSIG` plus `27 % der Nutzung · 8 Emotes` needs 345 px of a 328 px column). The hairline keeps its 16 px minimum; the sheet does not grow past its column. A heading that refused to wrap pushed the virtual scroller's content wrapper, and with it every row, into the gutter.
- **The band names are class names in the singular** (`Backbone`, `Regular`, `Rare`, `Never used`), because they do not only stand above a group but also in the sidecar and in the inspector after the rank of a single emote: `#3 · Backbone`. The landing diagram (`SetShape`) carries the same keys — there it is the same division, so the same vocabulary.
- **The four bands have a lightness ramp of the accent colour, not four hues.** `heavy` = `bg-accent-fg` · `regular` = `bg-accent-fg/55` · `rare` = `bg-accent-fg/25` · `dead` = `bg-fg-disabled/40`, centrally in `USAGE_BAND_FILL`. The bands are a ranking, not a set of categories side by side; a second hue would claim a difference in kind. **No new colour token** arises — the ramp is opacity steps of existing colours. The colour is redundant everywhere: next to every surface the band stands as a word, which is why the colour dab in the header row carries `aria-hidden` and needs no contrast proof.
- **The distribution strip carries the same four colours and a 3 px plinth.** Without a plinth a bar of the dead tail is one pixel high, and nobody perceives a colour on one pixel; the plinth also represents "never used" correctly — that is not a small number but a category of its own. Below it a flat segment bar whose widths are the usage shares: the same matter on the other axis, and read together the Pareto statement. It describes the **whole** set even when a filter is active — the strip says "the whole set, ordered by usage", a band heading says what lies underneath it. What gets labelled is not the segment but a wrapping legend beneath it — colour chip, share, band name, sized by content. Forcing the label into the segment width was the attempt to answer a pixel question with a percentage threshold: measured, "11% regular" needs 105 px and gets only 68 even at 1280 px, so it was cut off at **every** width — in English merely less conspicuously, because the words are shorter. Alignment under its own segment is the price for that, and at 11% width it was none anyway.
- **The sidecar is the magnifier from `lg` up** (`<aside>`, sticky, 16 rem; the grid only becomes two-column once something is actually being inspected). Below that a compact meta row (`lg:hidden`) carries the same numbers. **The drilldown dialog stays** — it is the only way on the ballot, below `lg`, by touch and by keyboard, and it additionally carries the range, first and last use and the voting block. Y axis, peak rate and the live-days row appear in both.
- **The sidecar never loads per cell.** Its day series comes from **one** call per (channel, range) — `GET /usage-stats/series`. A surface that hangs off the mouse pointer must not generate requests; sweeping through a band would otherwise be a load profile.
- **Animation is earned by dwelling, and only ever for one emote at a time.** Cells draw every emote as its still. `EmoteSpriteAnimated` (sidecar, inspector row, drilldown dialog, ballot, and the hovered cell of the import grid, §7.3) keeps the still and lays the `2x.webp` animation on top only after the pointer or focus has rested for 200 ms; a still emote never gets a second request. **Under `prefers-reduced-motion: reduce` no animation is fetched or played at all** — the still stays, and switching the preference on while an animation shows withdraws it. This is decided inside `EmoteSpriteAnimated` (via `ReducedMotionService`, `core/motion/`), not at the call sites, so no surface can forget it.
- **`.app-dock` appears only as long as there is something to do or to read:** a selection, or a 7TV run (delete, restore, import) that is running or has just finished — whose summary carries the protocol for download and must survive the last delete. A permanently parked action bar is a control the first visit has to read past. The dock carries, as the only surface of the app, a line in the accent colour — it marks the boundary of a living, reversible state. Which commands may stand in it and which belong in the page header is governed by §8.7.
- **The active emote set gates only the marking half of the dock, not the dock itself.** The count row, the mass-delete panel and the ballot button are about the set of *this* channel and need one; the import section shows a run into a **foreign** set (§7.2) and is therefore mounted outside this gate. Otherwise a writing run together with its cancel button would disappear on a usage page without an active set while it is still running.
- **Selection, dock and the 20 px history trigger are additionally gated behind `PointerModeService.isCoarse` — no 7TV write access without a mouse.** The 7TV write token can only be copied out of the devtools on 7tv.app, which a phone does not have; the gate is therefore the pointer type, not the width (`(pointer: coarse)`, not `any-pointer` — a desktop with an attached touchscreen keeps everything, because devtools remain). On `coarse` a click on the cell no longer marks anything but opens the drilldown dialog directly (§7.1), the mass-delete panel does not render at all, and in the page header of the usage page the same gate removes the entire `@if` block of the 7TV write paths — the Transfer button **and** the file-ingest trigger (§7.3).
- **What falls away on `coarse` is not explained — what points into the void on `coarse` is.** The dock, the mass-delete panel and the two 7TV write paths of the page header disappear without comment: visually nothing is missing, so there is nothing to say. A *pointer* to one of these capabilities is the other case — it stays visibly in place and promises something whose target cannot deliver there. The example: the voting list's page header carries a permanent link to the usage-stats atlas, because that is where a ballot is now assembled (2026-09-19) — on coarse it gives way to a sentence (`voting.list.createEntryDesktopOnly`), since the atlas's own marking and dock are themselves `!isCoarse()`-gated and a mod on a phone would have nowhere to land. The manager how-to beneath it (`voting.list.createEntryHint`) no longer names that destination itself — the button already does — and says only what the button cannot: that a ballot is made of emotes the manager marks. It stands inside the page header itself, as its own row beneath the heading/button row — not merely placed after the header in the page's own `gap-8` flex column (that left it visually equidistant from the header above and the list below, and wrong the moment an error banner sat between them, 2026-09-19 correction) and not inside the empty state either — an empty-state-only placement disappeared the moment a session existed, exactly when a returning manager would look for it again (2026-09-19 correction, docs/DECISIONS.md) — and it is hidden the same way, for the same reason: it must not repeat to a coarse reader what the header already said they cannot do here. Purely visual switching of this kind belongs in the variant pair `pointer-coarse:hidden` / `hidden pointer-coarse:inline`, not in `PointerModeService` — the service is for decisions the code makes.
- **The keyboard is equal, not an afterthought:** roving tabindex across the sheet, arrows move, space marks, Enter opens the history, shift-click transfers the state of the most recently clicked cell to a whole range — so it marks that range, or unmarks it again if the last click removed a mark. A *group* action must not do that: "mark all" stays purely additive, because a second press would otherwise destroy a hand-built selection in one click. The hint text for this is translated and stands visibly at the side — a keyboard operation nobody mentions does not exist for most people.
- **Hover surfaces carry no click targets.** What appears only on mouse-over is unreachable by touch; every action of the sheet has a path without a mouse pointer.
- **What the numbers of the sheet do *not* contain is stated as a sentence in the caption beneath it — not as a control, not as a banner, not as a second number on the cell.** The honesty sentences share **one** `<p>` under the sheet, in a fixed order: counting start → live days → bots → shared chat. That is the order in which the exclusions came about; a new one queues up at the back, it does not push in between and displaces none. Each sentence appears only if there is something to explain for this channel — its date is the **first sighting** of that kind of usage, not the start of the separation, and a missing date means "there was nothing to separate here", never "nothing is separated here". A toggle that shows the excluded usage again is explicitly **not** an option: the first visit should not have to keep two notions of the numbers in mind (DECISIONS 2026-09-01 and 2026-09-08). Reference: `web/src/app/core/emotes/bots-excluded-caption.ts` and `shared-chat-separated-caption.ts`.
- **Reference:** `web/src/app/shared/emotes/usage-bands.ts` (+ `usage-bands.spec.ts`), `usage-series.ts`, `usage-sparkline.ts`, `web/src/styles.css` (`.app-sprite-cell*`, `.app-dock`), `web/src/app/features/usage-stats/usage-stats-page.html`; flow test `web/e2e/usage-atlas.e2e.spec.ts`.

## 3. Typographic hierarchy

- **What applies:** Four levels, fixed class chains:

  | Level | Classes | Element |
  |---|---|---|
  | Page title | `text-2xl font-bold tracking-tight` | `<h1>` in layouts, `<h2>` on pages without a layout `<h1>` of their own |
  | Section title | `text-lg font-semibold` | `<h2>` |
  | Block title | `text-base font-semibold` | `<h3>` |
  | List-row title link | `font-medium` (text size inherits from the context) | `<a class="app-card-link">` / `<span>` |

  An `<h3>` **never** carries the section size `text-lg`: two levels that look the same are one level.
- **When to apply:** Always. The heading **level** follows the document structure (a page under a layout `<h1>` starts at `<h2>`), the **look** follows the table — both are to be observed independently of each other.
- **Exception:** The landing page (`web/src/app/features/landing/landing-page.html`) is deliberately scaled for marketing (`text-4xl`/`sm:text-5xl` hero, `sm:text-3xl` sections) and does not follow the table.
- **Reference:** `web/src/app/features/admin/admin-layout.ts` (page title), `web/src/app/features/usage-stats/usage-stats-page.html` (section title), `web/src/app/features/admin/admin-monitoring-page.ts` (block title).

### 3.1 Micro type: `label` and `micro`

- **What applies:** Below `text-xs` (12 px) there are **two** steps and **no** others:

  | Step | Size | Typical chain | For what |
  |---|---|---|---|
  | `label` | 11 px (`text-[11px]`) | `text-[11px] font-semibold tracking-[0.13em] uppercase`, alternatively `font-mono text-[11px]` | small-caps labels and counters at section level: band titles, status badges, step labels |
  | `micro` | 10 px (`text-[10px]`) | `text-[10px] font-semibold tracking-[0.11em] uppercase`, alternatively `font-mono text-[10px]` | the same role one level deeper, in dense contexts: metric terms in the sidecar, axis labels, labels on crowded buttons |

  Both are deliberately written as pixel literals: Tailwind ends at `text-xs` at the bottom, and steps of our own are defined neither in `styles.css` nor in a Tailwind configuration. The machine-readable equivalent is in [`DESIGN.md`](../DESIGN.md) under `typography.label` and `typography.micro` respectively.
- **When to apply:** `label` as long as the caption stands on its own. `micro` only once it competes for space in a row with other information — sidecar, axes, band head. Whoever builds something new and wavers takes `label`.
- **What does not apply:** No third value. A new `text-[12px]`, `text-[13px]` or `text-[8px]` is not fine-tuning but a fourth level that nobody has named — then it belongs here or it is dropped.
- **One named exception: 9 px** (`text-[9px]`), exclusively for the usage number printed **on** an emote sprite (`usage-stats-page.html`, `vote-session-detail-page.html`, two places in total). It sits over foreign image material and has a scrim next to it, not whitespace; it is therefore not a step of the system but a property of this one building block. Carried as a step it would be a licence for 9 px everywhere. The design-system hook knows it as a deliberate exception.
- **Open reservation:** `label` and `micro` are used for the same thing in individual places — once as an `<h3>`, once as a `<dt>`, both as small caps with `tracking`. Either they really are two levels, or one of them is drift. The stocktaking of 2026-08-30 counts 15 occurrences of `label` in five files and 14 occurrences of `micro` in two; that is enough to carry both as a step, but not enough to call the distinction settled. Whoever wants to merge them changes five files — not a docs entry.
- **Reference:** `web/src/app/features/usage-stats/usage-stats-page.html` (both steps and the exception), `web/src/app/features/landing/landing-page.html` (`label`), `web/src/app/features/voting/vote-session-detail-page.html` (`micro`).

## 4. Buttons, badges, banners

### 4.1 Buttons: `appButton`

- **What applies:** Every button/action link uses the attribute directive `appButton` (`web/src/app/shared/ui/button.ts`) — no copied utility chains. Variants `primary`/`neutral`/`outline`/`danger`/`danger-quiet`/`danger-solid`, sizes `md` (default)/`lg`. Element-specific layout (`ml-auto`, `relative z-10`, …) stays on the element's own `class` attribute, Angular merges both.

  | Variant | Use |
  |---|---|
  | `primary` | the one main action of a context (login, create, save) |
  | `neutral` | secondary actions with a surface (refresh, copy) |
  | `outline` | quiet secondary actions, cancel in dialogs |
  | `danger` | see 4.2 |
  | `danger-quiet` | see 4.2 |
  | `danger-solid` | see 4.2 |

- **Toggles: `[buttonPressed]` instead of a class chain of your own.** A button with `aria-pressed` gets its on state from the primitive; the variant stays alongside it and applies in the off state. The fill is deliberately the same as that of the selected segment of the `SegmentedControl`: "this one is on" should look the same whether alone or as one of several. The only difference is the hover step — a single toggle can be pressed again, a selected radio cannot. **A state that exists only as an arrow appended to the label reaches screen readers and nobody else** and is therefore not an admissible substitute.
- **A filter with named ranges plus free fields is a popover menu**, not a run of individual fields: the trigger names the current setting, the panel contains the presets and swaps its content for the fields as soon as "custom range" is selected (`DateRangeMenu` for time ranges, `UsageRangeMenu` for usage spans). **No toggle that in truth sets field values:** an "unused only" toggle that silently writes min = 0/max = 0 into the fields next to it is not a filter of its own but an invisible override.
- **One-out-of-N is not a pair of toggles but `<app-segmented-control>`.** Two toggles where a second click on the active one secretly switches something else hide an entire setting: sort key and sort direction are two questions and get two controls.
- **Reference:** `web/src/app/shared/ui/button.ts`, `web/src/app/shared/ui/segmented-control.ts`; toggle call sites `web/src/app/features/usage-stats/usage-stats-page.html`.

### 4.2 Destructive-action tiers: position in the flow, not severity

- **What applies:** The destructive tiers encode the **position in the confirmation flow**, not the severity of the action:
  - `danger` (outline): the **triggering** destructive button in the page context, which stands next to other controls and still has a confirmation step ahead of it (leave channel, open channel purge).
  - `danger-quiet` (type only, wash on hover): **the same trigger when it repeats per list row.** Twenty red-outlined "delete" buttons underneath one another make the rarest action of a page its loudest element. The tier is coupled to **repetition**, not to lower severity — the confirmation dialog behind it stays unchanged, only the permanent red box falls away.
  - `danger-solid` (filled): the **executing** button — the confirm button in `ConfirmDialog`/`TypedConfirmDialog`/mass-delete dialog as well as the page-level main CTA of the mass-delete panel.

  Mnemonic: outline triggers, solid executes, quiet is outline in series. That the irreversible purge is **triggered** via outline and the reversible leave is **confirmed** via solid is thereby correct.
- **When to apply:** Every destructive action gets a trigger **and** an execution: `danger`/`danger-quiet` trigger → dialog → `danger-solid` confirmation. A destructive button without a confirmation dialog is not provided for. Which surface the trigger sits on and at which position in an action row is said by §8.7 — this tiering only says how it looks.
- **Severity justifies no exception from the repetition rule.** Purge and session revoke, too, run as `danger-quiet` in the admin lists — the longer the list, the worse the colour ladder. An irreversible action is safeguarded by the typed name confirmation, not by a red frame you see twenty-five times underneath one another.
- **While a 7TV run of any kind (delete, restore, from K3 on import) is in progress, all 7TV start buttons are disabled, without hint text.** The `SevenTvRunArbiter` makes the mutual exclusivity visible without repeating it in words — the running progress is in the same dock and is itself the hint (#70). Since #72 this also holds for the header button "Transfer" (`usage-stats-page.html`, `[disabled]="atlasOrder().length === 0 || arbiter.activeRun() !== null"`) and since #91 for the file-ingest trigger next to it (`shared/seven-tv/file-import-trigger-gate.ts`) — both blocked during **each** of the three kinds of run, not only during an import of their own. The trigger does not inherit all of its neighbour's blocks in doing so: `atlasOrder().length === 0` deliberately does not apply to it, because the file brings its own rows along (§7.3).
- **Reference:** Triggers: `web/src/app/features/channel-workspace/channel-workspace-layout.ts`, header buttons `web/src/app/features/usage-stats/usage-stats-page.html` and `web/src/app/shared/seven-tv/file-import-trigger.ts` (+ `file-import-trigger-gate.ts`); in series: `web/src/app/features/voting/vote-session-list-page.html`, `web/src/app/features/admin/admin-channels-page.ts`, `web/src/app/features/admin/admin-users-page.ts`. Execution: `web/src/app/shared/ui/confirm-dialog.ts`, `web/src/app/shared/seven-tv/mass-delete-panel.ts`.

### 4.3 StatusBadge

- **What applies:** An `<app-status-badge>` marks a **notable** property, not every property. The building block knows only tones, the meaning lies with the caller:

  | Tone | Use in the existing code |
  |---|---|
  | `accent` | highlighted property |
  | `info` | advisory property |
  | `success` | LIVE · "running" states |
  | `neutral` | inactive/neutral |
  | `warning` | degraded/warning |
  | `danger` | error/disconnected |

- **The test question is "notable how often?"** A pill that stands in every row no longer marks anything — it is a colour ladder. What is the same word on most rows becomes quiet text: the **roles** in the overview (broadcaster/moderator/7TV editor) are a fact about *you* and repeat themselves; the **voting audience** carries its restriction as a contrast step instead of as a blue pill; **offline** is the inconspicuous case. The pill stays reserved for what applies *right now* and does not stand on every row — **LIVE** is the model case.
- **State ≠ property.** What a row currently *is* (bot is measuring / session is running / Twitch token present) is `<app-state-dot>` with `tone="on"|"off"` — dot plus word instead of a pill. That way the state does not compete with the properties next to it, and the colour carries no meaning the word does not already carry.
- **Subsystems that report their own health use `<app-health-marker>`.** The building block decides the presentation from the tone: `ok`/`idle` become a dot, `warning`/`danger` a pill. That way a healthy monitoring page carries **not a single coloured pill**, and the first conspicuous subsystem is, without any effort, the loudest element on it. That is the same sentence as above, only read the other way round: the statement arises from **absence**, and absence only works if nothing else spends it. Callers map their domain status onto the `HealthTone` (`admin-monitoring-page.ts`, `admin-roster-card.ts`, `admin-channel-detail-page.ts`).
- **The app frame is entirely silent in the normal case.** For the header area the rule applies one step more strictly than for a page: what stands there stands on **every** screen in **every** session, and even the quiet dot-plus-word state of `HealthMarker` is too much for that. For the worker status **nothing** stands there for `connected` and for `unknown`, and only for `stale` a warning pill. `unknown` keeps silent along with it because it is the state before the first answer of the poll: a warning there would flash up on every cold start. **And what the frame says, no page says a second time** — two statements of the same fact on one screen are exactly what this chapter clears away.
- **The text names the consequence, not the subsystem.** "Chat is not being counted" instead of "worker disconnected": whoever reads the message wants to know what is currently not right *for them*, not which process carries which name.
- **A transient state is not a warning.** The SSE stream stands briefly at `connecting` while every admin page loads; rendered as `warning` that would have flashed a yellow pill on every page view and taught an admin to overlook yellow pills. `connecting` is therefore `idle`.
- **The label goes in as a `label` input, not as projected content.** An `<ng-content>` in two branches of a control-flow block is filled by Angular only once — on the switch between dot and pill the text would otherwise have silently vanished.
- **Reference:** `web/src/app/shared/ui/status-badge.ts`, `web/src/app/shared/ui/state-dot.ts`, `web/src/app/shared/ui/health-marker.ts`; used in `overview-page.html`, `vote-session-list-page.html`, `admin-*`.

### 4.4 NoticeBanner

- **What applies:** Every page-wide message is an `<app-notice-banner>`; no ad-hoc error boxes or coloured paragraphs. `variant="error"` renders `role="alert"` (is read out), `info`/`warning` stay `role="status"` (silent). Action button into the `[notice-action]` slot (right-aligned).
- **When to apply:** `error` = failed request (text via `apiErrorTranslationKey`, see 9), `warning` = degraded state (reauth needed, bot inactive), `info` = benign waiting state (sync pending).
- **Not for something the app frame already says** (4.3). A banner is for what concerns **this page**; everything app-wide is carried by the header area, and by it alone.
- **A `warning`/`info` banner that mounts together with its content announces nothing** — same defect and same fix as 4.5 bullet 4, not specific to the transient pattern: `role="status"` only exists once the surrounding `@if` is true, and most screen reader/browser pairings announce only a *mutation inside* an already-mounted region. Where the banner's own arrival is the thing that has to be heard (a live value shrinking under the user while a dialog stays open, e.g. `create-vote-session-dialog.ts`, #132), pair it with a permanently mounted `sr-only role="status"` twin carrying the same text and mark the visible banner `aria-hidden="true"`. Unlike 4.5's instances this one does **not** self-clear — the condition it names persists for as long as it is true (here: while the dialog's live ballot is smaller than when it opened), per this section's own persisting-state rule above.
- **Reference:** `web/src/app/shared/ui/notice-banner.ts`; used in `overview-page.html`, `usage-stats-page.html`, `create-vote-session-dialog.ts`.

### 4.5 Transient status message

- **What applies:** Feedback that acknowledges something just completed and has nothing more to say afterwards is **not** a banner but a `<span role="status">` that disappears on its own after 4000 ms. There is **no** toast service and none is to arise; the pattern is deliberately written out per place: a constant `…_FEEDBACK_MS = 4000`, a signal with the translation key, a `setTimeout` handle that is cleared **first** when it is set anew, and a cleanup when the component is destroyed.
- **The role is `status`, never `alert`.** An acknowledgement is not an error; `alert` interrupts the screen reader mid-sentence and is reserved for something that demands attention immediately (4.4).
- **When to apply:** "Resync is queued", "n emotes have dropped out of the selection" — things that *have happened*. A state that **persists** (reauth needed, sync pending, request failed) belongs in a `NoticeBanner` and must not fade out while it applies.
- **The place must survive the case it reports.** The message does not belong on a surface that disappears through the same event. Concretely: a message about a shrunken selection must not stand in the dock, because the dock unmounts as soon as the selection is empty (2, 8.7) — that is, in exactly the worst case. It therefore sits on the emote count row, which always stands. The same holds for the *announcement* of what the dock does show: the duplicate and resync notices of a restore and of an import stand visibly in the dock, but the dock mounts and unmounts with its own content (`actionDockHasContent`) — and a fully refused run (every row already present) is exactly what mounts it. A status region inside the dock is then created in the same change-detection pass as its text and announces nothing. Their announcement therefore comes from `DockOutcomeAnnouncer` (`shared/seven-tv/dock-outcome-announcer.ts`), which each host page mounts permanently outside every dock gate, `!isCoarse()` included — `usage-stats-page.html` with import outcomes, `vote-session-detail-page.html` with restore outcomes only, since it shows no import section. **The same holds for a line inside an already-mounted dock whose own `@if` creates it together with its sentence**: the usage-stats dock's "n of them hidden by the filter" row (`hiddenSelectedCount`) is spoken by the same announcer, fed a count that is 0 whenever the row is not on screen, so the region never says something the dock does not show. **The marked-count row differs (Opus review, 2026-09-19):** it is reachable in the accessibility tree itself again, not `aria-hidden` — this is the one visible dock line the announcer does *not* continuously mirror, because an individual mark or unmark already announces itself through its own cell's `aria-pressed` flip, and mirroring the live count here too spoke every one of those a second time. The announcer instead speaks only the outcome of a bulk-mark gesture ("mark all" in the toolbar, §8.7, or the per-band one) — exactly the gesture that can take the row from unmounted to a double-digit count with nothing else to announce it — holding that number until a non-bulk change retires it (an individual mark/unmark, a filter-driven prune, a clear) or the row itself leaves the screen, and *not* merely until the selection empties (follow-up, 2026-09-19): a lone deselect that leaves the selection non-empty must retire the announcement too, otherwise a later bulk gesture landing back on the same total would repeat the paragraph's text unchanged and `role="status"` would announce nothing for a gesture that really happened. The notices in the dock and in the mass-delete panel, and the hidden-by-filter row, are `aria-hidden` — the text only, never a focusable control inside it (axe `aria-hidden-focus`); the hidden-by-filter row's "reset filters" button therefore stays outside the hidden span. With several messages at once it speaks one paragraph each, in the dock's reading order: the marked count first, then the hidden-by-filter line, then restore before import, and within each the skipped count, the check-unavailable notice, then the resync acknowledgement — for an import into a tracked channel's *non-active* set (2026-09-21), the resync never fires at all, and a `copiedNotActiveNotice` (`dock-outcome-announcer.ts`) takes that same slot instead, naming the set the copy actually landed in rather than claiming a channel-page update that never happens. `role="status"` is implicitly `aria-atomic="true"` (WAI-ARIA 1.2, §status); a status region that holds several messages at once, as this one does, must set `aria-atomic="false"` explicitly, otherwise every added or changed paragraph re-reads all of them — a single-message status region elsewhere keeps the implicit default.
- **The live region itself is permanently mounted, only its text comes and goes.** A `role="status"` element that only enters the DOM together with its content (`@if (feedback(); as f) { <span role="status">…</span> }`) announces **nothing** on most screen reader/browser pairings — those announce only a mutation *inside* an already existing region, not its own appearance. Binding, therefore, are two elements: a permanently mounted `sr-only` region with `role="status"` in which only the *content* changes via `@if`, plus a visible twin next to it marked with `aria-hidden="true"` — otherwise the same message is read out twice, once from the live region, once from the visible text. Precedent and rationale in the comment: `app-shell.ts` (the `liveQuotaExhausted` message); since #94 (P2) likewise `usage-stats-page.html`; since #134 likewise `channel-workspace-layout.ts`, `admin-channels-page.ts`, `admin-users-page.ts`, `vote-session-list-page.html`, and for the dock's outcome notices and its hidden-by-filter line `DockOutcomeAnnouncer` on `usage-stats-page.html` and `vote-session-detail-page.html` (see the bullet above for why not inside the dock). **Not everything that cannot announce itself needs a twin:** inside a modal the dialog pulls focus and is read out whole on open, so a block that mounts with the dialog gains nothing from a live region and only competes with the banners already in it — `delete-confirm-dialog.ts`'s hidden-by-filter block is deliberately a plain paragraph for that reason. **One known open instance:** `run-progress-panel.ts` — the whole progress panel is `role="status"` and mounts together with its first state, so the start of a run is not announced.
- **Reference:** `channel-workspace-layout.ts` (`showResyncFeedback`), `admin-channels-page.ts`, `admin-users-page.ts` (role-cache-cleared notice), `vote-session-list-page.html` (copy-link notice), `dock-outcome-announcer.ts` (duplicate/resync notices of the dock, placed by `usage-stats-page.html` and `vote-session-detail-page.html`), `usage-stats-page.ts` (`showSelectionPrunedFeedback`), `app-shell.ts` (live-region precedent).

## 5. Forms & validation

### 5.1 Inputs

- **What applies:** `.app-input` is the only input style, `.app-input-sm` the compact variant for filter toolbars. Both bring an explicit `color` along (necessary in the CDK overlay outside the shell DOM).
- **The border is a contract, not aesthetics:** it carries `border-border-field` and has to reach **at least 3:1** against the surface beneath it (WCAG 1.4.11 — an input field only becomes recognisable as a control through its border at all). This token is the **only one with the same value in both modes**: it deliberately sits in the middle so that it suffices against a dark *and* against a light field. Whoever rebuilds an input-like trigger by hand instead of using `.app-input` (the DateTime trigger does that) owes the same value — and a hover that **increases** the border contrast. `hover:border-fg-muted` does exactly that in both modes (lighter in the dark, darker in the light); a hard-wired direction would be the wrong way round in one of the two modes.
- **Reference:** `web/src/styles.css`, `web/src/app/shared/datetime/datetime-picker.ts` (rebuilt trigger).

### 5.2 Label requirement

- **What applies:** Every field has either a visible `<label for="…">` + `id` on the input, or — only in filter toolbars, where no visible label is provided for — `[attr.aria-label]` (+ `[title]` for the mouse tooltip).
- **Reference:** visible label: `web/src/app/shared/ui/typed-confirm-dialog.ts`; `aria-label` case: `web/src/app/features/usage-stats/usage-stats-page.html` (filter bar).

### 5.3 Field-error pattern

- **What applies:** Field-level validation errors follow a fixed pattern:

  ```html
  <input
    id="feld-id"
    [formControl]="control"
    class="app-input"
    [attr.aria-invalid]="control.invalid && control.touched ? 'true' : null"
    [attr.aria-describedby]="control.invalid && control.touched ? 'feld-id-error' : null"
  />
  @if (control.invalid && control.touched) {
    <p id="feld-id-error" class="text-sm text-danger-fg">{{ 'x.y.error' | transloco }}</p>
  }
  ```

  Fixed: error text `text-sm text-danger-fg`, error `<p>` with `id`, input with `aria-invalid` + `aria-describedby` only in the error case. Errors spanning the **whole form** (request failed), by contrast, run through `NoticeBanner variant="error"` (4.4), not through field errors.
- **When to apply:** Every field with client validation whose error becomes visible. An `invalid` that stays silent without display (7TV token input) is the exception to be avoided.
- **Reference:** `web/src/app/features/usage-stats/create-vote-session-dialog.ts` (title field), `web/src/app/features/admin/admin-channels-page.ts` (channel join).

### 5.4 Validation traps

- **What applies:**
  - Client validators take the server-side **normalisation** into account, not only the server regex (CLAUDE.md rule 9): validate channel names via `channelNameValidator` from `web/src/app/core/channels/channel-name.ts`, which checks the **normalised** value (users type `HandOfBlood`).
  - `(ngSubmit)` **never** fires on a `<form>` with only a standalone `[formControl]` — use `(submit)="onSubmit($event)"` with `event.preventDefault()` instead.
  - Disabled submit/confirm buttons explain their reason as **text** next to them, not only by greying out (WCAG); Enter paths check the condition themselves instead of relying on `disabled`.
- **Reference:** `web/src/app/core/channels/channel-name.ts`, `web/src/app/shared/ui/typed-confirm-dialog.ts` (hint + Enter path).

## 6. Loading & empty states

### 6.1 Skeleton vs. spinner (NN/g rule)

- **What applies:** **Skeleton for page/list loads, disabled button (label stays constant) for isolated actions.** No "loading…" text lines, no spinners for page loads.
  - Ruled lists: `<app-skeleton-rows [count]="3" />`.
  - Section pages (admin monitoring, admin channel detail): `<app-skeleton-sections [count]="3" />`.
  - Deviating shapes (atlas, ballot): hand-rolled skeleton following the same a11y pattern — **one** `role="status"` element with a translated `aria-label`, the shimmer blocks (`.app-skeleton`) in an `aria-hidden="true"` container.
  - Actions (refresh, join, purge): button `[disabled]="isLoading()"`, label stays.
- **The skeleton draws the outline of the real content, not just any placeholder** — spacing, edge and horizontal extent included. **A skeleton whose outline deviates from the content makes the arrival of the content look like a layout bug** and thereby costs exactly the calm it exists for; even a different horizontal padding shows up as text jumping sideways. Whoever changes a list or section shape checks the corresponding skeleton **in the same commit** — no test suite ever sees a skeleton, so there is nothing here to catch the mistake for you.
- **Reference:** `web/src/app/shared/ui/skeleton-rows.ts`, `skeleton-sections.ts`, grid variant `web/src/app/features/usage-stats/usage-stats-page.html`.

### 6.2 EmptyState

- **What applies:** Every empty state is an `<app-empty-state>` with a `title` (why empty) + where possible a `description` and a projected CTA (what to do next). No bare grey sentence.
- **No emoji, no dashed box, not centred.** The building block deliberately has **no** `icon` input, so that no call site can smuggle in an emoji: a picture frame around 🔍 is not an icon system but its absence. And a centred text column in a left-aligned page reads like a forgotten placeholder — for a correct and expected state exactly the wrong reading. Instead the same hairline-plus-label shape that the atlas bands (2.5) and the landing steps use.
- **When to apply:** List/grid without entries, filter without hits — but only **after** loading has finished (the skeleton prevents the EmptyState from flashing up during `rxResource` loads with `defaultValue`).
- **Reference:** `web/src/app/shared/ui/empty-state.ts`; used in `overview-page.html`, `usage-stats-page.html`.

## 7. Dialogs

- **What applies:** **Every** dialog runs through `@angular/cdk/dialog` — never `window.confirm`, never hand-built overlays. Focus trap, Escape, backdrop click, `aria-modal`, focus return come from the CDK.
- **Opening: never `Dialog.open` directly.** Every dialog component exports its own `open<X>Dialog(dialog, data)` function next to it, which internally calls `openAppDialog` (`shared/ui/dialog.ts`) — that is where `backdropClass`, `panelClass` and the naming sit. The reason is measured: the three-liner stood at twelve call sites by hand, five of which had forgotten `ariaLabelledBy`. A new dialog gets its `open…()` function in the same commit as the component.
- **Inside: `<app-dialog-shell>`** (`shared/ui/dialog-shell.ts`) — surface, padding, heading, body, action row. The spacing is done by the shell (flex column), **not** by `mb-*` on every child; content that belongs together more tightly wraps itself in its own `flex flex-col gap-1`. The width **and the height** belong to the pane (`.cdk-overlay-pane.app-dialog-panel`), never to the content.
- **The action row stays visible while the body scrolls (#226), and the sheet's drag handle is pinned by the same rule.** Both are `position: sticky` against the pane, the handle to its top edge, the row to its bottom — `-mx-6 -mt-6` (handle) and `-mx-6 -mb-6` (row) cancel the shell's own `p-6` on those sides so each one's margin box reaches the pane's edge, and the sticky offset itself is plain **`0`** (`top-0`, `bottom-0`) — **not** a second negative value. A margin and a sticky offset do two different jobs: the margin decides where the box sits in normal flow (bled out past the shell's padding), the offset decides where the *stuck* box is clamped to relative to the pane's own edge — `0` means flush with it. An earlier version of both (`-top-6` on the handle, mirrored as `-bottom-6` on the row when #226 was first built) read the margin and the offset as cancelling one another, which happened to look right at rest and at the very end of a scroll — the only two states that are *not* actually stuck — but for every position in between, the real defect, it pinned each one 24 px past the pane's true edge: the handle's own grab bar (the `span` inside the 44 px touch target) was entirely clipped out of view for as long as it was genuinely stuck, and the row's `pb-6` cushion was clipped the same way, leaving its buttons flush against the raw pane edge with no visible padding. Caught by measuring `getBoundingClientRect()` mid-scroll, not at the ends — `web/e2e/dialog-action-row.e2e.spec.ts` and `web/e2e/touch-mobile.e2e.spec.ts` both pin a case for it. The fix is in `DialogShell` itself, once, for every dialog that uses it — never a second, nested scroll area inside one dialog's own content: that was tried for the import target picker and rejected, because a scroll area inside a scrolling pane behaves badly on touch and would make that one dialog behave differently from the other eleven. This is deliberately a sticky offset, not a flex-column height chain (`h-full` on the shell, `overflow-y-auto` on the body): two component hosts sit between the pane and the shell, both `display: inline` by default, and a height chain does not survive them — the same reason the pane, not the shell, is the scroll container in the first place. The row carries its own opaque surface (`bg-surface`) and a `border-t border-border` — without the first, content scrolling underneath would show through; without the second, there would be nothing to mark where "still scrolling" ends and "always visible" begins. It rounds its bottom corners (`rounded-b-lg`) to match the shell's own **only on a fine pointer** — the sheet's shell has no bottom radius to match, it is flush with the screen edge instead. `overflow-hidden` on the shell remains forbidden for the reason given above (it would break both sticky pins the same way); nothing about this changes that constraint or the pane's role as the one scroll container.
- **On a coarse pointer the same dialog is a bottom sheet — the same instance, a second appearance, no second overlay stack.** Two separate `@media (pointer: coarse)` rules carry that, not one: the docking sits on the **wrapper**, not on the pane — `.cdk-global-overlay-wrapper:has(> .app-dialog-panel) { align-items: flex-end !important; }`, the `!important` mandatory because CDK's `GlobalPositionStrategy` writes `align-items` as an inline style onto the same wrapper, and inline is only beaten by `!important`. The second rule stays on the pane class and changes `max-width` (`none` instead of `min(28rem, calc(100vw - 2rem))`) and `max-height` (`85dvh` instead of `calc(100dvh - 2rem)`) — `width: 100%` is already in the base rule and never changes, so it is never "full width instead of 28rem" but "no capping any more". Whoever transfers the docking to a new panel class and copies only the pane rule gets a centred dialog — the same type of mistake as the two CDK traps below, only not yet listed at this spot. The geometry gets by without `openAppDialog()` and without a change to the pane choice; `PointerModeService.isCoarse` decides live instead of once at opening time. The sheet **chrome**, by contrast, sits in `dialog-shell.ts`: `DialogShell` renders the drag area with the attribute `data-sheet-handle` — the `SheetDrag` directive looks for exactly that in order to allow a drag gesture; if the name changes on one side without the other, drag-to-dismiss breaks silently. **The drag area is the entire top edge of the sheet, not the visible bar:** the sticky bar is dimensioned at `min-h-11` (44 px, the comfort target from §10 — the bar itself is 4 px high), and the head block beneath it carries the same attribute, because a drag that starts on the heading means the sheet and not the content. The two have to abut **without a gap**: the head block swallows the shell's `gap-4` via `-mt-4 pt-4` and gives it back as padding of its own. If the gap stays with the shell, it is neither handle nor `touch-none` — a strip in which a gesture has to compete with the browser's scrolling, and precisely where a thumb is aiming. `web/e2e/touch-mobile.e2e.spec.ts` pins one half of this (`toHaveCount(2)` on `[data-sheet-handle]` inside the pane, height of the bar ≥ 44 px, and the two boxes touching) — that a real dialog renders the handles under this name at all; for the other half, the literal string in `SheetDrag`, only the directive's spec stands. The geometry says nothing about either, it comes from the media query and holds even without a handle at all. The handle carries the shell's `rounded-t-2xl` a second time: its negative margins blend it into the corners the shell's radius leaves free — without that the sheet reads square at the top. `overflow-hidden` on the shell would be the other way and is forbidden, it would make the shell its own scroll container and would break the sticky anchoring of the handle. The pane remains the scroll container here as well, see the CDK traps below.
- **Drag-to-dismiss** (`shared/ui/sheet-drag.ts` + `sheet-drag-policy.ts`) closes via `DialogRef.close()` and is therefore not a fourth exit next to backdrop tap and Escape. The gesture begins only at the handle or when the pane is scrolled to the top — otherwise a downward drag means "scroll the content up". The second path is not a substitute for the first: as soon as the content is longer than the sheet, the browser takes a downward movement as a scroll for itself and cancels the pointer. Reliable is only what carries `data-sheet-handle` and is thereby `touch-none`. **And it begins only with the first movement beyond 4 px: a mere press takes no pointer capture and writes no inline style.** That is not a subtlety — `setPointerCapture` on `pointerdown` relocates the following `click` onto the capture element, that is onto the shell, underneath which every button of the dialog lies. With pointer type `touch` that goes unnoticed, with pointer type `mouse` every button in every dialog is dead — and precisely this combination (`pointer: coarse` with mouse input) is the devtools emulation with which this view is checked by hand. The dismissal threshold has **three** numbers, not two: `distancePx ≥ 72` **or** `velocityPxPerMs ≥ 0.5`, but only from a minimum distance of `24 px` — without the lower bound a four-pixel twitch while tapping would close the sheet, because a short movement is by definition fast. **Both quantities are measured at the end of the gesture, not over its course**, and that is the part that was wrong twice: velocity averaged over the whole gesture divides every millisecond of a resting finger into the result (hence `VELOCITY_WINDOW_MS`, the last 100 ms), and the distance, read from the last *reported* movement, lags behind, because the browser coalesces and discards `pointermove` — the hastier the gesture, the more so. A hasty gesture is a short one, so it stands and falls with exactly these two numbers.
- **Naming (every dialog needs an accessible name):** either a visible heading — then `[dialogTitle]` or a `[dialog-header]` slot with `id="app-dialog-title"`, and `openAppDialog` wires up `ariaLabelledBy` itself — **or** an `ariaLabel` with a short action phrase. A dialog with neither is a bug.
- **Action row: cancel always comes first.** That way the CDK's `first-tabbable` default lands on the harmless button; an explicit `cdkFocusInitial` becomes unnecessary. A *choice* (format, scope) belongs in the body as a radio group, not as a second exit button in the footer — otherwise equal-ranking options compete as buttons and one has to be arbitrarily toned down.
- **Colour in a dialog means "this case is unusual".** Notices that apply to *every* run ("irreversible", "not all channels detectable") are quiet — `fg-secondary`/`fg-muted`. Only the finding that distinguishes this run from the others becomes an `<app-notice-banner>`. Four stacked warning colours in the delete dialog effectively said: none of them is important.
- **Name lists** ("this will be deleted") via `<app-name-preview-list>` — capped at 50 plus a counted remainder, ruled rows per §2.1, bleeding full-width against the shell's `p-6`.
- **Choice criterion:**
  - `ConfirmDialog` (`shared/ui/confirm-dialog.ts`): destructive action that needs a yes/no confirmation (leave channel, delete session). The caller passes fully translated `message`/`confirmLabel`. Deliberately **without** a heading — each of its messages already begins with the action; it is named via `ariaLabel`.
  - `TypedConfirmDialog` (`shared/ui/typed-confirm-dialog.ts`): the action is irreversible **and** row-related (channel purge) — retyping proves *which* row was meant. The comparison is trimmed but case-sensitive. `title` is mandatory.
  - A dialog of your own only if neither of the two fits (e.g. mass delete with progress) — then still `DialogShell` + your own `open…()`.
- **A blocked confirm button needs its reason as text** next to it (`mr-auto` in the action row, connected via `aria-describedby`). Greyed out alone is not a perceivable explanation (WCAG) — this holds for the retype field just as much as for the running shared-set check.
- **CDK traps (both found live):**
  1. The overlay container hangs **outside** the app shell DOM — it inherits no text colour. Dialog panel and `.app-input` need an explicit `color` (they have one; keep it in mind for new overlay styles).
  2. CDK injects its overlay styles at runtime **after** all bundle stylesheets. Panel chrome must therefore be defined unlayered and with raised specificity (`.cdk-overlay-pane.app-dialog-panel`) — new panel rules follow the same pattern.
- **Reference:** `web/src/app/shared/ui/dialog.ts`, `dialog-shell.ts`, `name-preview-list.ts`, `confirm-dialog.ts`, `typed-confirm-dialog.ts`, `web/src/styles.css` (dialog classes), callers `channel-workspace-layout.ts`, `admin-channels-page.ts`.

### 7.1 Popover (non-modal)

- **What applies:** Non-modal dropdowns run through **`<app-popover>`** (`shared/ui/popover.ts`) — never yet another hand-built `relative` wrapper plus `absolute` panel. The primitive brings panel chrome, `max-w-[calc(100vw-2rem)]`, outside-click and Escape dismiss with it. The host sets the `position: relative` wrapper with the marker `data-popover-anchor` around trigger **and** popover; clicks inside it never count as an outside click (otherwise the opening click would close the panel again in the same dispatch).
- **Contract:** rendered = open. The panel never hides itself, it emits `closed`; the visibility signal **and** the focus return to the trigger belong to the host. Padding is brought by the content — the panel is padding-free so that full-bleed menu rows and padded forms both fit into it.
- **Demarcation from §7:** popover ≠ dialog. No focus trap, no `aria-modal`, no backdrop. As soon as the interaction is supposed to block the rest of the page, it is a CDK dialog. And **no** CDK overlay for the popover case: these panels open out of sticky bars and have to inherit their stacking context (§8.5), which an overlay container attached to `<body>` cannot do.
- **Mobile:** menu rows `min-h-11 sm:min-h-9` (§10, 44 px comfort target for touch). An opened popover belongs in the audit harness (§12) with `afterLoad` — closed, the overflow and touch-target metrics say nothing about it.
- **The 44 px comfort target is by now no longer a popover special case but anchored in the button size `lg`** (`shared/ui/button.ts`, `SIZE_CLASSES.lg = 'min-h-11 …'`): a button carrying the flow that opts for `lg` — dialog confirmations, the mass-delete trigger, the sheet's close path — gets the 44 px height through that, without rebuilding the class chain itself. There is nothing automatic about it: `buttonSize` is `md` by default (23 call sites deliberately set `lg`), and `md` stays unchanged for dense toolbars, where a mouse is the realistic pointer.
- **The 20 px history trigger on an atlas cell (§2.5) falls away entirely on `coarse`** — there the whole 64 px cell is the target, which beats a 20 px overlay in every respect and is anyway the only action the tap still triggers on coarse.
- **Reference:** `web/src/app/shared/ui/popover.ts`; used in `shared/datetime/date-range-menu.ts`.

### 7.2 Target picker and confirmation dialog (import, #72)

- **What applies:** The flow behind "Transfer" runs through two dialogs, both via
  `openAppDialog` (see above): `ImportTargetDialog` (`shared/seven-tv/import-target-dialog.ts`,
  `openImportTargetDialog`) picks scope and target, `ImportConfirmDialog`
  (`shared/seven-tv/import-confirm-dialog.ts`, `openImportConfirmDialog`) shows the preview and
  starts the run. Both orders below are a contract (2.4/2.5 of the #72 plan), not a
  layout question — whoever changes them changes a contract. Which surfaces may trigger this
  flow is governed by §8.7.
- **Target picker, row order:**
  1. Scope radio group `visible`/`selection` — only if a grid selection exists; without a
     selection the muted hint `export.scopeNoSelectionHint` (#144) stands in the same place
     instead — unless a caller forces the scope
     (`forcedScope`, §8.7 — since #147 only the dock entry point): then both are missing at
     this spot, radio group as well as hint, because a
     forced scope is not an open choice that would need an explanation.
  2. Channel list state: `<app-skeleton-rows [count]="3">` while `listMine()` is loading, after
     that at most **one** message (mutually exclusive) — `reauthRequired` (warning), otherwise `loadFailed` (error +
     "Reload"), otherwise `listIncomplete` (info).
  3. Target radio group: one radio per channel in which the user is broadcaster or 7TV editor
     (`importTargetOptions`), `disabled` + "(channel has to join first)" for untracked channels.
     If there is no such channel, the group stays empty and the `import.target.none` message
     appears in its place (E5) — the dialog opens anyway, it just has nothing to choose from.
  4. Cancel / Continue.
- **The scope default `selection` deliberately breaks with the export dialog** (which preselects `visible` there):
  an export runs the risk of unnoticed **narrowing**; a copy into a foreign 7TV set runs the
  risk of unnoticed **widening** to several hundred visible emotes — the wedge of the feature
  is "select, then copy". Without a grid selection there is no radio group, and the scope is then
  `visible`. Do not align them. The file path, which used to stand as a radio in this group, has
  been in the export dialog (§7.4) since #141 and inherits its scope default `visible` — the target dialog
  stays with its `selection` default, because the two are now separate commands with separate
  risks, no longer two exits of the same dialog.
- **Confirmation dialog, row order:** title (count + target channel) → origin row (channel,
  or file with export date/channel) → target row "Target: channel · set …" as soon as the target data
  are there → exactly **one** of three loading states (hand-rolled skeleton per the §6.1 pattern /
  `no-set` banner / `failed` banner with retry) → shared-set warning (error) or "check not
  possible" (warning) → slot projection (overflow as a warning banner, otherwise quiet text) →
  stale notice if the last sync of the target failed → "already in the target set" row →
  name-collisions row + `NamePreviewList` → invalid-names row + `NamePreviewList`
  (non-ASCII characters in the emote name — both rows say "7TV will reject this" and therefore
  stand next to each other) → **discarded rows before collapsed
  duplicates** (real data loss weighs more than mere consolidation — the reason is stated at
  `discardedRows`/`duplicatesCollapsed`) → "nothing to add" banner → "This list comes from
  this channel" → the quiet notice about the automatic run → (only in the loading state: the
  loading hint next to the action buttons) → Cancel / Copy.
- **The target-data loader (`core/emotes/import-target-loader.ts`, `loadImportTarget`) emits exactly
  once and never throws** — the three inner requests (`getSetStatus`, `listEmotes`,
  `getSetWarning`) catch their own error and deliver a tagged value instead of letting the `forkJoin`
  abort on the first error. A failed `getSetWarning` only degrades the
  set check to "not possible" and lets the run proceed; a failed `getSetStatus`/
  `listEmotes` or a missing active set blocks it (`failed`/`no-set`, `no-set` wins if
  both occur at once — a 404 is the more final statement). The execute button carries its
  blocking reason as text (`aria-describedby`, pattern as above): `loadingHint`, `loadFailed`,
  `noTargetSet`, `nothingToAdd` — only for a simultaneously running 7TV run from somewhere else
  (`runBlocked`) does the button stay blocked without text of its own, because then the running progress in
  the dock is already the explanation (4.2).
- **On import the token prompt comes after the confirmation, not before it** — unlike with delete
  and restore, where it still stands before the confirmation. Reason: picker and preview are purely
  read operations, and on import the preview is the place where the actual decision is made
  — demanding a secret before the user has seen what would happen would be the wrong
  order. If the user cancels the prompt, no run starts. Delete/restore do not change:
  there the confirmation *is* already the whole preview, so there is no reason to demand the token
  later. **This difference is intentional, not a straggler** — when unifying,
  read up here first, do not "correct" the import.
- **Reference:** `web/src/app/shared/seven-tv/import-target-dialog.ts`, `import-confirm-dialog.ts`,
  `import-target-options.ts`, `import-preview.ts`, `slot-projection.ts`,
  `web/src/app/core/emotes/import-target-loader.ts`; caller `web/src/app/shared/seven-tv/import-flow.ts`.

### 7.3 Ingest dialog (#91, since #147 the one import dialog)

- **What applies:** Everything that brings emotes **into** the channel of the page begins in the page
  header with the trigger `<app-import-trigger>` (§8.7 governs the surface, §4.2 the blocks) and runs through **one**
  dialog: `ImportSourceDialog` (`shared/seven-tv/import-source-dialog.ts`,
  `openImportSourceDialog`). **Its first step is the source selection** — until #147 the
  foreign channel had a header button of its own next to it, which contradicted spec decision E1 ("one source among others
  in the import dialog, not a page of its own"). A fourth source is a fourth row in
  this step, not another button and explicitly **not** a greyed-out placeholder as long as it
  does not exist.
- **Step sequence (contract):**
  1. **Source selection** — one ruled row per source with a label and a muted hint line
     beneath it, both part of the accessible name (the same two-line pattern as §7.4). The row
     is a button and **not** a radio group: it navigates, it does not parameterise a later
     action, so there is nothing left to confirm.
  2. The chosen branch, **in the same dialog**. It opens no second one.
- **The action row belongs to the dialog, not to the step:** cancel first (§7), after that
  "Back" as soon as a branch has been entered, and "Continue" **only once the grid stands** —
  coupled to the same state as the pane width, not to a second one. Before loading, the channel branch already has
  a forward action, namely "Load set" at the field it acts on; a
  second, permanently blocked "Continue" next to it made a dialog around **one** text field look like it had four
  buttons and competed with the action row. A button missing at the back does not touch
  the order of the others — "cancel first" applies unchanged.
- **The field row of the channel step is a pair, not a wrap:** field and "Load set" stand at
  the same height (both on the 44 px floor that `buttonSize="lg"` defines — **stated** at the field,
  not inherited from the neighbour), in dialog spacing instead of toolbar spacing, and **without** `flex-wrap`:
  if the button broke below the field, the field would lose its height at the same time. It shrinks instead
  (`min-w-0`).
- **The head names the branch:** "Import emotes" on the first step, "Import file", "Import from
  a channel" or "Import from 7TV's leaderboard" below it. That is the only location cue a
  one-dialog flow has besides "Back".
- **The pane is wide as long as the grid stands — and only then** (`app-dialog-panel-wide`, see §7 and
  `styles.css`). The width belongs to the pane, not to the content, so the switch is the
  panel class and not a `max-w-*` in the template; it is set at runtime via
  `overlayRef.addPanelClass`, because at opening time nobody yet knows whether a grid is coming. The three
  form states — source selection, file branch, channel branch **before** loading — stay at the
  ordinary 28 rem: a form in 72 rem looks lost. **That leaves exactly one
  size change, and it coincides with "Load set"**, that is with a content change that is visible
  anyway — the only place where a size change can be read as a consequence instead of as
  arbitrariness. The trigger is the visibility of the grid, not the step.
- **The channel field nevertheless carries a `max-w-sm`**, and that is not a second opinion about the
  pane width: in the loaded state the form stands **above** the 72 rem grid. Uncapped,
  a field for a dozen characters would span the whole pane there. At 24 rem it is equally wide before and after
  "Load set" — the pane grows around the field, the field does not move.
- **File branch (`FileImportStep`, `shared/seven-tv/file-import-step.ts`).** It **reads and checks**
  the file — nothing more. Until #147 it was a dialog of its own (`FileImportDialog`); what changed is
  only its housing, not its behaviour.
- **Row order in the file branch (contract):**
  1. The **list of the four permissible kinds of file**, each its own list entry with the addition
     "as JSON": purge protocol (restore) · transfer protocol, recovery file or result protocol
     (restore) · emote list (copy) · usage export (copy). The two restore sorts come first, in that
     order. It stands **above** the control it explains, and is a list and not a
     sentence with commas — the German versions would otherwise break at an arbitrary point at 360 px
     (§12).
  2. The **file control**: visibly labelled button plus hidden
     `<input type="file" accept="application/json">`. The button is the first meaningful
     control of the step and receives focus on entry (see the focus contract below).
     §7 "cancel always comes first" applies to the action row and stays untouched by this.
  3. The error banner (`NoticeBanner` `error`) — only in the error case.

  There is **no** "Continue" button in this branch: the file selection itself is the execution.
- **Channel branch (`ForeignChannelStep`, `shared/seven-tv/foreign-channel-step.ts`):** visibly
  labelled channel field (§5.2 applies here in full — it is not a filter bar) plus "Load set", after that
  loading state/error banner and the `ForeignEmoteGrid`. The step closes nothing; it reports its
  result as a signal against which the dialog blocks its "Continue".
- **Leaderboard branch (`LeaderboardStep`, `shared/seven-tv/leaderboard-step.ts`), the third
  source and the only one that starts without the user already knowing where the emotes sit:** a
  labelled "List" `<select>` (`import.leaderboard.sortLabel`, not "Sort by" — what it picks is
  which of 7TV's two network-wide lists is being read, "Trending today" or "Top overall"; the
  ordering follows from that pick, not the other way round) above the same
  loading/error/`ForeignEmoteGrid` sequence as the channel branch. Changing the list is a **server**
  request, not a client-side re-sort: the grid below is handed `[forcedSortMode]` and answers in
  that list's own rank order with its own sort control suppressed entirely — see the sorting
  contract below. While a request is in flight the chooser carries `aria-disabled`, not `disabled`,
  for the same reason the field/button pair elsewhere in this dialog does: it is the control the
  dialog hands focus to on entry (see the focus contract below), and a truly disabled element
  cannot take focus. Switching the list clears the grid's selection visibly and re-locks
  "Continue" — a selection made against one list has no honest meaning against the other, the same
  reasoning `ForeignChannelStep` already applies to a new channel query.
- **`ForeignEmoteGrid` has a clear-selection button next to its "N selected" count**, a neutral
  `appButton` labelled `import.clearSelection`, shown only while at least one emote is marked.
  Clicking it empties the grid's selection and emits the empty selection through `selectionChange`,
  which re-locks "Continue". The button removes itself with the selection, so focus moves to the grid
  group (`role="group"`, `tabindex="-1"`) instead of falling to `<body>`.
- **Animated emotes are marked in the grid and play in place.** An animated emote carries a small
  play triangle in the top-right corner of its cell, on the score badge's scrim tokens
  (`--ep-sprite-scrim`/`--ep-sprite-scrim-fg`) and `aria-hidden`; its accessible name says it instead,
  as `import.animated` between the names and the score ("Pog, animated, 7TV score (all-time): 12.4k").
  Whether an emote is animated comes from its url alone (`isAnimatedEmoteUrl`: `4x_static.webp` for
  animated emotes, `4x.webp` for stills). Hovering or focusing an animated cell plays it after the
  200 ms dwell of §2.5. **Exactly one cell plays:** the grid keeps a single playing key by 7TV id, and
  only that cell mounts `EmoteSpriteAnimated` on top of its own still, which hides once the animation
  has painted (`animationShown`). The pointer's hold ends when the pointer leaves the cell or the
  grid, and whenever the viewport scrolls; focus's hold (in Chrome and Firefox a click focuses the cell
  too) ends when focus leaves the cell. While the pointer rests on a different cell than the focused
  one, that cell plays; when it leaves, the focused cell plays again. Either hold ends once its cell
  leaves the rows the viewport renders, so it does not resume when the cell renders again. There is
  no sidecar and no touch variant (the dialog is unreachable on a coarse pointer, §2.5).
- **Focus contract: whoever enters a step lands on that step's first meaningful control.**
  Concretely: file branch → "Choose file" (the hidden `<input type="file">` cannot take focus
  itself), channel branch → the channel field, "Back" → the source row you came from (the same
  courtesy a menu button shows when closing its submenu — "wrong branch, then the other one"
  costs no tabbing that way). **The CDK does not handle this:** its autofocus runs
  **once**, when the overlay opens, and never again for a switch **inside** the same
  dialog. Until #147 the contract held by accident — the file dialog opened directly onto its own
  button — since then it has to be stated and set, and specifically **after** the new step has been
  rendered (`afterNextRender`, pattern as in `account-menu.ts`), because the target only comes into existence through that
  render. A mouse user notices nothing of a violation; by keyboard the focus lands
  in nothing and the dialog is tabbed through from the start. That is why an E2E case per branch hangs on it
  and not merely a glance. If the grid appears after "Load set", the focus **stays** on "Load set":
  the button stands above the switching point, so it is not cleared away, and it still means something there
  (reload, correct the name) — nothing to move.
- **There is exactly one scroll container in the channel branch, and that is the grid.** Otherwise the pane
  scrolls along (§7) and two nested bars over the same list were the reported
  defect. This is achieved not by a percentage height chain — that does not survive the two
  `display: inline` component hosts — but by dimensioning the virtualised viewport against `dvh`,
  which keeps the dialog content shorter than the pane:
  `min(34rem, max(4rem, 100dvh - 26rem))`.
  **The lower bound is the dangerous part here, not the upper one.** A lower bound F brings
  the double bar back for every window below `F + 22rem` (22 rem = measured chrome height plus
  the 2 rem margin of the pane) — with the original 16 rem, therefore, for every window below ~608 px, and
  that is a 1366×768 laptop or a zoomed window, not an exotic case: at 500 px the pane overran by
  a measured 107 px. At 4 rem that band lies below ~416 px, that is below the chrome itself.
  Eliminating it entirely would only be possible with a real height chain starting at the pane, and that would mean
  making `DialogShell`'s host a flex column for all twelve dialogs — deliberately not done.
  The numbers hang on an E2E case, because jsdom has no layout.
- **Result contract:** on success the dialog closes with a discriminated result —
  "Restore" with the restorable rows of the protocol, "Import" with the `ImportSource` from the
  file or "Foreign" with the rows marked in the grid —, on cancel/Escape/backdrop with
  `undefined`. It starts **no** run, chooses **no** import target and opens **no** further
  dialog. In the error case it stays open and shows the banner; every new attempt resets it, and
  the file input is cleared after every selection so that the same corrected file triggers a
  `change` again.
- **The target is not asked for, it is fixed:** the channel of the page from whose header the trigger
  was clicked — the same for **all** sources. Until #147 the foreign-channel path still had
  `ImportTargetDialog` with `forcedScope: 'selection'` in between; with the scope radio group suppressed,
  exactly one question remained there that the page context had already answered. The step is
  deleted without replacement. `forcedScope` itself stays — the dock entry point in §8.7 still uses it.
- **The chains run one after another, not into one another.** First the import dialog closes with its
  result, then the trigger starts the matching chain — `startRestoreFlow` (token → confirmation)
  or `startImportFlow` (confirmation → token, §7.2). No dialog of these chains is opened out of an
  open dialog; the one-dialog contract from §7 stays intact and both
  orders stay as they are.
- **The file input belongs inside the dialog, not behind it.** A programmatic click on an
  `<input type="file">` **after** a CDK dialog's `closed` runs outside the user gesture; the
  browser then silently does not open the file window. Inside the open dialog the click
  (or Enter/space on the button) is a fresh activation.
- **The sorting in the grid names a property of the individual emote, never the origin of the
  list — and claims no unit. This governs `ForeignEmoteGrid`'s own, in-grid sort control, which
  the channel branch keeps: its default stays `'none'` and its `<select>` stays visible.** Three
  requirements, all three born of one mistake:
  1. *No statement about the list.* "7TV global · top of all time" in a tab bar had led the
     operator to conclude that the grid showed 7TV's global emotes instead of the set of the
     entered channel. Binding since then: a labelled `<select>` ("Sort by"), no
     tab bar.
  2. *No invented unit.* The value is `Emote.scores.topAllTime`/`trendingDay`, a
     ranking score — **not** `Emote.channels.totalCount`, which this feature explicitly does not
     request, because it hangs on 7TV's search bucket and overdrawing it blocks us for about an hour.
     "Spread" and "in how many 7TV sets" traded the first misreading for a quantity claim
     that nobody has substantiated. The options are therefore called "7TV score (all-time)"/"(trending)",
     and the quiet sentence under the sort row says only two things: network-wide, for this **one**
     emote — and **not** the usage in this channel. A comparative value without a unit is honest,
     an invented unit is not.
  3. *Never a preselection, never "popularity"* (concept P5') — a channel-related popularity does not exist
     for a foreign channel.
  4. *Exception, narrowly: the leaderboard branch forces and pre-selects the sort, on purpose*
     (decided 2026-09-14). There the ranking **is** the list — the server fetched
     `TRENDING_DAILY`/`TOP_ALL_TIME` and nothing else — so `[forcedSortMode]` sets the grid's mode
     and its own `<select>` is not rendered at all; showing the rows in any other order would show
     something the user did not ask for. This narrows the 2026-09-10 ban to its wording obligation
     (requirement 2 above — unit and channel-local reading stay forbidden everywhere); requirement 3
     is unchanged for the channel branch.
- **The active score is in the accessible name of the tile.** An explicit `aria-label` **replaces**
  the descendant text in the accessibility tree, so for screen readers the visible number does not
  otherwise exist at all — and it is exactly what is currently being sorted by. It is announced under the same
  caption that the sort control carries; that gives the bare number its meaning without inventing
  a unit. The only difference from the tile: the missing value becomes a word, because the
  tile has only an en dash for it and a screen reader does not pronounce that at all.
- **Reference:** `web/src/app/shared/seven-tv/import-source-dialog.ts`, `file-import-step.ts`,
  `foreign-channel-step.ts`, `leaderboard-step.ts`, `foreign-emote-grid.ts`, `import-trigger.ts`,
  `import-trigger-gate.ts`, `restore-flow.ts`; `core/seven-tv/seven-tv-leaderboard.service.ts`,
  `leaderboard.model.ts`; parsers `shared/export/read-envelope.ts`, `purge-run-export.ts`,
  `import-source-parser.ts`.

### 7.4 Export dialog (purpose instead of format)

- **What applies:** `ExportDialog` (`shared/export/export-dialog.ts`, `openExportDialog`) has three
  callers — usage statistics, voting detail page, delete protocol —, but only one of them
  orders its options by purpose instead of by format. The dialog itself no longer hard-wires anything
  for that: the option list comes from the caller (`ExportDialogData.options`), the dialog treats
  every `id` as opaque and never switches on it itself.
- **Row order in the body:**
  1. Scope radio group `visible`/`selection` (`export.scopeLabel`) — only if a grid selection
     exists (`selectionCount > 0`); if the selection is empty but the concept is present
     (`selectionCount === 0`), the muted hint
     `export.scopeNoSelectionHint` (#144) stands in the same place instead, explaining the absence rather than leaving it
     uncommented. If the caller has no grid-selection concept at all (`selectionCount === null` — voting
     detail page, delete protocol: the set that gets exported is not up for choice there),
     **both** are dropped at this spot, radio group as well as hint — Codex review on PR #145,
     see DECISIONS.
  2. Option group — the legend is `optionsLegendKey`, per option `labelKey` on the first line,
     below it `hintKey`, if set.
  3. Row count (`export.rowCount`, follows the selected scope) plus `filteredHint` if the
     visible list is filtered.
  4. Notice banners (`noticeKeys`) — explanations for missing columns (secret vote, usage figures
     visible only to managers).
  5. Cancel / Export.
- **`options[0]` is the default — the only default rule.** There is no second
  notion of a default in the dialog. That keeps "CSV first" for the two unchanged callers (via the
  shared constant `FORMAT_EXPORT_OPTIONS`) and makes "Analyse the numbers" (= CSV) the default
  of the usage statistics, without the dialog knowing what a "format" is.
- **The purpose ordering of the usage statistics is a wording contract** (`export.purposeLabel` as the
  legend), in this order:
  - "Analyse the numbers" / "Usage statistics as CSV"
  - "Process the numbers further" / "Usage statistics as JSON"
  - "Import the emotes again later" / "Emote list as JSON"
  Voting detail page and delete protocol stay with CSV/JSON (`export.formatLabel`,
  `FORMAT_EXPORT_OPTIONS`) — the purpose list applies only where more than one purpose sits behind the same
  action.
- **The hint line stands inside the `<label>` and is thereby part of the accessible name**
  ("Analyse the numbers Usage statistics as CSV") — intention, not accident. The target picker from §7.2
  hangs "(channel has to join first)" into its name in the same way, and an E2E case there already matches
  against the whole thing. An `aria-describedby` would have been the alternative and would have introduced a deviation
  without occasion. Markup: the `<label>` is `flex items-start` (instead of `items-center`),
  the text pair in a `flex flex-col`. `export-dialog.ts` remains the only file with a
  two-line radio label — a shared radio component is deliberately **not** introduced (three
  occurrences are not a pattern).
- **Reference:** `web/src/app/shared/export/export-dialog.ts`, `export-dialog.spec.ts`; callers
  `features/usage-stats/usage-stats-page.ts`, `features/voting/vote-session-detail-page.ts`,
  `shared/seven-tv/mass-delete-panel.ts`.

## 8. Navigation

### 8.1 Tab bars (router-link pattern)

- **What applies:** Tab bars are router links, **not** an ARIA tabs pattern (`role="tablist"`/`aria-selected` are wrong here, since these are real navigations). A tab is `<app-tab-link>`; the bar stays with the caller, because its sticky position differs per level:

  ```html
  <nav class="app-sticky-bar top-14 mb-6 flex h-10 gap-2 border-b border-border">
    <app-tab-link link="usage-stats" [label]="'x.tab' | transloco" />
  </nav>
  ```

  **The tab itself is a primitive** (`shared/ui/tab-link.ts`) and is never rebuilt as a class chain — a contract that lives in copied string literals drifts on the first edit. `ariaCurrentWhenActive="page"` sits in the primitive and is thereby unforgettable. `display: contents` on the host: the anchor has to be the flex child itself, otherwise it centres in a box of its own instead of carrying the bar's `h-10`.

  `h-10` and `flex items-center` (instead of `py-2`) are part of the sticky contract from §8.5 — the tab bar height is the `top` offset of the filter toolbars.
- **Reference:** `web/src/app/shared/ui/tab-link.ts`; bars in `admin-layout.ts`, `channel-workspace-layout.ts`.

### 8.2 In-page anchors

- **What applies:** Anchors run through `routerLink` + `fragment`, **never** through a bare `href="#…"` (resolves against `<base href="/">` and breaks). The router scrolls by itself (`withInMemoryScrolling` + `onSameUrlNavigation: 'reload'` are configured in `app.config.ts` — do not remove, the reload part is what makes the second click on the same anchor work).
- **Reference:** `web/src/app/app.config.ts`, `web/src/app/features/landing/landing-page.html`.

### 8.3 Role visibility

- **What applies:** The visibility of navigation/areas is decided by a **field that was read** (e.g. `isGlobalAdmin` from the cached `/me`), never by a provoked error (403 probing). Guards for role-gated areas redirect to `/` (not `/login`) and stash no return URL. The server-side filter always remains authoritative.
- **Reference:** `web/src/app/core/auth/admin.guard.ts`, `web/src/app/features/shell/app-shell.ts`.

### 8.4 Pagination

- **What applies:** Paged lists use `<app-pager [page] [totalPages] [scrollTarget] (pageChange)>` against a `PagedResult<T>` from the backend (`items/page/pageSize/totalCount/totalPages`); the pager hides itself when there is one page. `create`/`delete` reload the list instead of patching optimistically (that would otherwise shift the pagination); pure in-place changes may patch locally.
- **A page change repositions the viewport — `[scrollTarget]` is mandatory as soon as the list can become longer than one screen.** The pager stands beneath its list, so the click always happens at the bottom end of the document; without a reposition the rows of the next page arrive entirely *above* the viewport and the reader goes on looking at the end of a list they never scrolled through. What gets passed is a template reference to the head of the results region:

  ```html
  <h2 #resultsTop tabindex="-1" class="scroll-mt-24 text-lg font-semibold">…</h2>
  …
  <app-pager [page]="page()" [totalPages]="totalPages()" [scrollTarget]="resultsTop" … />
  ```

  Three parts, all of them necessary:
  - **`tabindex="-1"`** — the pager focuses the target (WCAG 2.4.3). Without a focus shift the focus falls to `<body>` on a page change, because the clicked button together with the list disappears into the loading branch.
  - **`scroll-mt-*` = height of the sticky stack above the page** (§8.5): `scroll-mt-24` under a tab bar (14 + 10), `scroll-mt-14` directly under the shell. Without that, `scrollIntoView` parks the target behind the header. The filter toolbar gets no share — it is sticky itself and pins at its own `top`.
  - **Reposition before the `pageChange` emit**, encapsulated in the pager: the emit sets the page signal, the resource falls back to its `defaultValue`, and both list and pager are replaced by the skeleton. Anything after that would run against a dead element.

  Instant, never `behavior: 'smooth'`: this is a content swap, not a guided tour, and animating several screen heights is slower than the reader and a motion trigger. The pager's page line is `role="status"` and thereby a live region — the focus shift says *what* is on the screen, never *which page*.
- **The pager deliberately stays in the loading `@else` and is not rendered on during loading.** Angular's `resource` resets `value()` to the `defaultValue` on a params change (only a `reload()` with the same params keeps the stream), so `totalPages()` is `0` during loading and the pager would hide itself anyway. Leaving it standing would need a sticky second state in every page — and the focus is already saved by the reposition.
- **Page and filters live in the URL, not in local signals** — via `listQueryState()` from `core/routing/list-query-state.ts`. Without that, "open row, go back" is a reset to page 1, and a shared link shows a different list from the one that was meant. **A new paginated page takes the helper instead of writing `page = signal(1)`.** What it settles:

  | | Navigation | Effect |
  |---|---|---|
  | `goToPage(n)` | `replaceUrl: false` | a real history step — that is what the back button is for |
  | `setParams(patch)` | `replaceUrl: true` | replaces the entry, jumps back to page 1, clears the drafts of the written keys |

  Filters must produce **no** history step: they act per keystroke, so the back button would otherwise mean "undo one letter". The price is that Back leaves the filtering in one step instead of unravelling it — that is what the reset button is for.

  Both navigations carry **`scroll: 'manual'`**. That is the per-navigation opt-out of scroll restoration and the reason why the anchor reposition above still takes effect at all: the router would otherwise jump to `[0, 0]` — on every keystroke, and on a page change *after* the pager, disregarding the `scroll-mt-*`.

  Default values are **removed** from the URL instead of being written empty (`?page=3`, not `?page=3&action=&channel=&actor=`); page 1 never appears in it.
- **Text filters run through `query.textFilter(key, debounceMs)`, never through a `signal()` + `debounceTime` of your own.** Typing stays local and immediately visible, the URL gets only the settled value — a router navigation between key and character eats input, and a value that comes back out of the URL mid-word overwrites the cursor. That `setParams` also clears the drafts of the keys it writes is part of the contract and not a detail: a value still hanging in the debounce window has never reached the URL and would otherwise write itself back 300 ms later — under a filter combination the page has just excluded.
- **Not covered:** a page number outside the range (`?page=9999`) is passed through — the backend answers with an empty page and the list shows its empty state. Clamping would need `totalPages`, which only exists *after* the response that is requested with this state.
- **Reference:** `web/src/app/shared/pagination/pager.ts` (+ `pager.spec.ts`), `web/src/app/core/routing/list-query-state.ts` (+ `list-query-state.spec.ts`); used in `admin-audit-log-page.ts` (three filters), `channel-activity-page.ts` (two), `admin-users-page.ts`, `my-votings-page.ts` (`scroll-mt-14`), `vote-session-list-page.html` (the anchor is the `<ul>`, not the page header above it — the header (§8.7) names a command complete without any selection, not the results, and that is exactly where a page change must *not* jump back to). Flow test `web/e2e/channel-activity.e2e.spec.ts`.

### 8.4a Content width (one, deliberately)

- **What applies:** The content column has **one** width, app-wide: `max-w-7xl` (80 rem). No page and no route sets one of its own.
- **It stands in nine places, and they always move together.** `app-shell.ts` (header row and `<main>`), `landing-page.html` (six times: navigation, hero, diagram, walkthrough, closing, footer) and `usage-stats-page.html` (the inner container of the `.app-dock`). The ninth is the one that gets overlooked — if the dock stays behind, the action row stands narrower than the sheet it sits above. The `max-w-2xl`/`max-w-3xl` **inside** the landing page are prose widths and not shell widths; they stay where they are.
- **The frame must not jump per route.** A second width for the sprite sheets (2.5) can be justified on the merits — there width is not decoration but emote columns —, but **when switching between a sheet page and a list page the frame then jumps**, and a layout that changes its width on every navigation is more restless than a sheet page that gives away 500 px. A route-driven shell width (`data.wideLayout` or similar) is therefore ruled out, not open.
- **If a sheet needs more width**, it takes it *inside* the constant column — the sheet surface breaks out, the shell column stays put.
- **What is forbidden is the second number, not a different one.** This section does not fix a value for all time — it demands that there be **one**. Changing it means changing it in all nine places at once, carrying the audit harness (§12) along and writing the reason into the decision log.
- **Reference:** `web/src/app/features/shell/app-shell.ts` (comment on the header row).

### 8.5 Sticky layers (header · tabs · filters)

- **What applies:** The page scrolls as **one document** (no app frame with an inner scroll container — that would break CDK virtual scroll, router scroll restoration and the collapsing of the mobile browser bar). Three layers stay visible in the process via `position: sticky`, with **fixed heights as a contract**:

  | Layer | Height | `top` | z |
  |---|---|---|---|
  | Shell header (`app-shell.ts`) | `h-14` | `top-0` | `z-30` |
  | Tab bars (§8.1) | `h-10` | `top-14` | `z-20` (via `.app-sticky-bar`) |
  | Filter toolbars | variable (may wrap) | `top-24` | `z-20` (via `.app-sticky-bar`) |

  Sticky bars use the primitive **`.app-sticky-bar`** (`styles.css`): sticky + `z-20` + darkened blur background; only the `top` offset comes as a Tailwind class at the point of use. Filter toolbars additionally get `py-2` so that the blur has a surface. **New page with a filter toolbar ⇒ `app-sticky-bar top-24 py-2`**, new tab bar ⇒ snippet from §8.1.
- **Virtualised emote grids scroll with the document:** `<cdk-virtual-scroll-viewport scrollWindow>` — no inner scroll container, no fixed viewport height, no frame around the grid. The rows deliberately run underneath the translucent sticky bars while scrolling. Part of the contract is the rule `cdk-virtual-scroll-viewport[scrollWindow] { overflow-anchor: none; }` in `styles.css` (with `scrollWindow` the CDK does not apply `.cdk-virtual-scrollable`, and the browser's scroll anchoring would otherwise jitter on the document) as well as `minBufferPx`/`maxBufferPx` ≥ 1×/2× row height (the CDK defaults are smaller than a grid row). The `ROW_HEIGHT_PX` constants of the pages have to state the cell-height arithmetic as a comment.
- **z-ladder (binding):** row action container/stretched link `z-10` (§2.3) < sticky bars `z-20` < shell/landing header and `.app-dock` `z-30` (the panel of `<app-account-menu>` sits as `z-30` **inside** the header context and thereby above everything). Dropdowns that open out of a sticky bar (e.g. the time-range menu of the usage stats, `shared/ui/popover.ts` via `shared/datetime/date-range-menu.ts`) inherit its `z-20` context and thereby lie above the content; dropdowns in the content (datetime picker in the create form, `z-30` in the `z-10` row context) stay below the bars — they open downwards, away from them.
- **The panel of the account menu must not be clipped.** It is about 320 px high and hangs absolutely positioned out of a 56 px header, so it lies **inside** the header stacking context. That is intentional (`popover.ts:16-19`: panels open out of sticky bars and have to inherit their context, which a CDK overlay container attached to `<body>` cannot do). The price: if any ancestor of the header gets `overflow: hidden` or `overflow: auto`, the panel is clipped. Today none of them carries one. **Whoever works on the shell layout checks this on the device.**
- **Why fixed heights:** `sticky` needs exact `top` offsets for stacked layers. `h-14`/`h-10` are therefore not aesthetics but the basis of a calculation (`top-24` = 14 + 10) — whoever changes them carries all `top` values along. The filter toolbar itself may wrap to any height, its own `top` depends only on the layers **above** it.
- **Selection and hover states paint exclusively *inside* the cell surface** — concretely `inset-ring-2 inset-ring-accent` instead of `ring-2` (Tailwind v4; `ring-inset` no longer exists there). The reason is exactly this sticky construction: the scroll container and the sticky bars are both exactly the content box of `<main class="mx-auto max-w-7xl px-4">`, and an *outset* `ring-2` paints 2 px **outside** the border box. On the cells of the first and last grid column those 2 px would lie to the left and right of the bar's background box and would shimmer through while scrolling. Making the bar wider is not a solution — that hides the edge effect instead of avoiding it, and every future bar would have to follow suit. **New selection state on a cell ⇒ `inset-ring-*`.**
- **The focus outline deliberately stays outset** (global `:focus-visible` ring, §10) — it is transient and must not be clipped at the edge columns. That is exactly why `styles.css` still carries `contain: layout style` instead of CDK's `contain: content` on `.cdk-virtual-scroll-content-wrapper`: paint containment would cut it off. Do not "tidy up" this rule.
- **Reference:** `web/src/styles.css` (`.app-sticky-bar`, CDK containment block), `web/src/app/features/admin/admin-audit-log-page.ts` (toolbar), `web/src/app/features/shell/app-shell.ts` (header), `usage-stats-page.html` + `vote-session-detail-page.html` (selection ring).

### 8.6 Back navigation (hierarchical up-link)

- **What applies:** Every page that is not a root node carries at its top left exactly **one** up-link to its parent node in the *information* hierarchy — as the primitive `<app-back-link [link] [label] />` (`shared/ui/back-link.ts`), never as a hand-built anchor and never as `history.back()`.
- **Why no history back:** the deep pages are regularly entered by deep link and thereby skip their list — a vote session is opened directly after being created from the usage stats, and also from My Votings and from the admin area. So the browser history there points precisely *not* upwards; a "back" would be factually wrong. Precise up-links can be constructed everywhere, because `paramsInheritanceStrategy: 'always'` (`app.config.ts`) passes `channelName` and `sessionId` to every child route.
- **Why no breadcrumb:** NN/g recommends breadcrumbs from three levels on — but here the tab bar (§8.1) already maps levels 2/3 permanently, and only *one* page (`vote-sessions/:sessionId`) lies on level 4 at all. A breadcrumb would have duplicated the tabs and, as a fourth bar, broken the height contract from §8.5. The up-link deliberately scrolls away with the content, so it is not a sticky layer.
- **Where it stands:** as the first element of the page, in one row with its heading (`flex flex-wrap items-center gap-x-4 gap-y-2`) — the way `ChannelWorkspaceLayout` prescribes. On a detail page it stands **outside** the loading `@if`, so that the way out exists during loading and after an error too. Pages below a layout with an up-link (usage stats, vote-session list) get **no second one** — they inherit the layout's.
- **Label:** the proper name of the target, not "back" — and where the target already has a key, **the same** key (the vote-session detail page labels its up-link with `channelWorkspace.tabs.voting`, the name of the tab it lands on). Generic targets live under `nav.*`.
- **Accessibility:** a real `<a routerLink>` (openable in a new tab, not a `<button (click)="navigate()">`), the arrow `←` is `aria-hidden` and thereby decorative, the accessible name is widened in the primitive to `nav.backTo` ("Back to {{target}}") — it contains the visible text and thereby satisfies WCAG 2.5.3. Focus via the global `:focus-visible` ring.
- **Reference:** `web/src/app/shared/ui/back-link.ts` (+ `back-link.spec.ts`); used in `channel-workspace-layout.ts`, `admin-layout.ts`, `my-votings-page.ts`, `vote-session-detail-page.html`; flow test `web/e2e/back-navigation.e2e.spec.ts`.

### 8.7 Action surfaces: which surface carries which command

- **Area of application:** pages with multiple selection on a sheet — today the usage stats page and the voting detail page.
- **The page header** (the header *of the page*, not the shell header) carries commands that are complete without a selection. Each has exactly one intent; if two overlap, one is renamed or dropped — not moved. **Overlap means: two commands with different names for the same intent.** The same action under the same verb in two places is a second entry point, not an overlap — the sameness of the verb is a condition here, not an accessory. **Confirmed since #141:** "Transfer…" and "Export" were exactly this borderline case as long as "Transfer…" was also usable as a file exit — since the target dialog lost its file target, they are two real intents (channel vs. file) instead of two names for one overlapping intent. No rule change, only the case at which it now decides cleanly.
- **Selection-bound commands** — those that cannot be executed at all without a selection (delete, put up for a vote) — do **not** stand in the page header. Where there is a dock, they stand there; where there is none, in the flow beneath the sheet, as on the voting detail page (`vote-session-detail-page.html:149-157`). The dock is an addition, not a requirement.
- **Selection-*creating* commands** — a control whose whole purpose is to produce a selection out of nothing (mark-all) — belong neither in the header nor in the dock. The dock does not render at zero selection (`actionDockHasContent`), so it cannot host the one control that would put something into it; the header's commands, by contrast, are page-wide and carry no notion of "the current view" to mark. It sits with the controls that define that view instead, and the scope decides which controls those are: **scope = the whole view → the filter row**, beside the reset control (the toolbar's "mark all"); **scope = one group inside the sheet → that group's own heading** (the `dead` band's own "mark all", the one existing instance of the narrower case). Both carry a bare verb, not a count in the label (corrects an earlier version of this bullet, docs/DECISIONS.md 2026-09-19): each already has its scope stated right beside it — the emote-count line at the end of the filter row for the toolbar button, the band heading's own count for the band button — so neither needs to say it twice. The toolbar button is additionally absent outright, not merely disabled, whenever the sheet is not actually showing that view (still loading, or the "sync pending" banner standing in its place while `atlasOrder()` still holds an unrelated response); on top of that it is disabled, without disappearing, once the view it would act on is already fully marked.
- **The dock also carries the run state** — progress, protocol, restore after a run. No other surface can do that, because it has to outlast a *finished* run.
- **Permission, not obligation:** a command whose result depends on the selection **may** additionally stand in the dock as a short form — same verb, without the scope question, with the count in the text. The justification is findability at the moment of marking, not saved clicks: the page header scrolls away, the dock does not. **This justification is so far unsubstantiated** (n = 1); it is therefore phrased as "may" and forces no future action into two entry points.

  *Example of why "may" and not "must":* the export deliberately carries **no** short form. Its scope default is `visible` (`export-dialog.ts:139-141`), running counter to the target dialog, which stands at `selection` — §7.2 records the asymmetry as intentional. An export short form with a forced `selection` would silently turn that default into its opposite.
- **Order:** destructive actions stand at the end of the **constructive** group, with a gap before them (NN/G on bulk actions); neutral exits such as "clear selection" may follow. That is an instruction about *position*, not about looks: the button variants stay as §4.2 prescribes them.
- **Reference:** page header `web/src/app/features/usage-stats/usage-stats-page.html`; dock `.app-dock` (§2.5) together with the action row that is projected via `ngProjectAs="[selection-actions]"` **into** `web/src/app/shared/seven-tv/mass-delete-panel.ts`; without a dock, in the flow: `web/src/app/features/voting/vote-session-detail-page.html`.

## 9. i18n duties

- **What applies:**
  - Every visible text (including `aria-label`, skeleton labels, `title` tooltips) is a Transloco key with an entry in **both** locales (`web/public/i18n/de.json` + `en.json`). No hard-coded strings in the template.
  - Request error texts come **exclusively** from `apiErrorTranslationKey(error)` (`web/src/app/core/i18n/api-error.ts`): known backend `errorCode` → `errors.api.<code>`, otherwise status fallback (`errors.status.*`), otherwise `errors.generic`. A new backend `ApiErrorCode` needs the entry in `api-error.ts` **and** both locales — `api-error.spec.ts` enforces that (CLAUDE.md rule 7). 401 is never handled at page level (global `apiAuthInterceptor`).
  - Button/toggle labels stay **constant**; the state is encoded by `aria-pressed` + the look, not by swapping the label (prevents width jumps and toolbar wraps).
  - Unused keys are deleted from both locales when a feature is removed.
- **Reference:** `web/src/app/core/i18n/api-error.ts` + `api-error.spec.ts`; toggle pattern `web/src/app/shared/emotes/emote-usage-filter.ts`.

## 10. Accessibility checklist

Basis: `web/.claude/CLAUDE.md` — **an axe pass and the WCAG AA minimum are mandatory.** Concretely in this project:

- [ ] **Focus:** The global `:focus-visible` ring (`outline: 2px solid var(--ep-accent)` plus offset — `styles.css`) applies to everything interactive. It carries the accent colour and thereby follows the mode by itself. Never `outline-none`/`focus:outline-none` without an equivalent replacement.
- [ ] **Touch targets:** ≥ 24 × 24 px for everything interactive (WCAG 2.5.8 AA), 44 px as a comfort target for primary actions — the vote buttons reach it below `sm` (`min-h-11 sm:min-h-6`). The "equivalent target" exception (a small control in a row that is clickable across its full area) is admissible, but is **documented as a comment in the template**.
- [ ] **ARIA roles:** `role="alert"` only for errors (NoticeBanner `error`), `role="status"` for silent messages and skeleton wrappers; `radiogroup` + roving tabindex for SegmentedControl (reuse `shared/ui/segmented-control.ts`, do not rebuild it) — it selects on focus and is therefore only suitable when a change is cheap; if a refetch hangs on the change, the choice belongs in a popover menu (`shared/datetime/date-range-menu.ts`), where arrows only move the focus and Enter/space commits (permitted by the APG for exactly this case); `ariaCurrentWhenActive="page"` on active nav/tab links.
- [ ] **Field errors:** pattern from 5.3 (`aria-invalid` + `aria-describedby` + error `id`).
- [ ] **Disabled explains itself:** the reason as visible text next to the button (TypedConfirm hint pattern), not merely greying out.
- [ ] **Decorative things hidden:** emoji icons and skeleton shimmer `aria-hidden="true"`.
- [ ] **Accessible names short:** stretched-link rows let the screen reader hear only the short title (2.3), never the whole row as link text.
- [ ] **Contrast, in both modes:** text 4.5:1, borders/focus rings/meaning-bearing graphics 3:1 — **computed for light AND dark, not estimated** (2.0: a new token brings both values plus the proof in the commit message). The weakest permissible text step is `text-fg-muted`. **Its tightest case is `text-fg-muted` on `surface-inset`: light 4.81:1, dark 5.06:1** — the value that breaks first the next time a surface is shifted. Whoever shifts a surface recomputes it and carries it along here. Input borders: `border-border-field`, see 5.1. **`warning-fg` needed the largest correction:** light-mode `amber-700` measured only 4.40:1 on `page` and 4.09:1 on `surface-inset`, under the 4.5:1 text gate — since 2026-09-15 the token is `amber-800` (6.21:1 / 5.76:1). `warning-dot` stays `amber-700` because a graphic only owes 3:1, and 4.09:1 already clears that with reserve. **`success-fg`/`success-dot` do not currently clear their own gates on `surface-inset` either:** `success-fg` (emerald-700) measures 4.36:1 there and 3.94:1 on `surface-inset-hover`, both under 4.5:1; `success-dot` (emerald-600) measures 2.97:1 on `surface-inset`, under its 3:1 gate — open: not yet moved.
- [ ] **Count hover states in:** a hover is a state of its own and owes the same contrast as the resting state. **No tool checks this** — axe knows only what is rendered right now. For filled buttons the rule from 2.0 takes care of it (`*-solid-hover` always one step darker); everything hand-built recomputes it itself.
- [ ] **axe contrast gate:** the audit harness (12) runs `@axe-core/playwright` with the rule `color-contrast` per state and writes `contrastViolations`. **Gate: 0 on `serious`/`critical`.** A limit you have to know: axe computes only what it recognises as text over a determinable surface — it refuses semi-transparent stacks, and graphic contrast (1.4.11: traffic-light dots, bar fills, borders) is not covered by the rule at all. Both remain manual work.
- [ ] **Contrast/native:** `color-scheme` is set **per theme** on `:root` in the token block (no longer fixed on `body`) — that way `input[type="time"]`, scrollbars and autofill follow the mode by themselves. Do not remix the colour pairs of the primitives (badge tones, banner variants) ad hoc — they are tokens (2.0).

## 11. Checklist "Building new UI"

Tick off before finishing every UI change:

1. [ ] **Primitives instead of utility chains** — rebuild none of these: `appButton` · `SegmentedControl` · `StatusBadge` · `StateDot` · `HealthMarker` · `NoticeBanner` · `EmptyState` · `SkeletonRows`/`SkeletonSections` · `Pager` · `TabLink` · `BackLink` · `Popover` · `DialogShell` + `ConfirmDialog`/`TypedConfirmDialog` + `NamePreviewList` · `DateRangeMenu`/`UsageRangeMenu` · `AccountMenu` + `DisplayPreferences` + `Avatar` · `.app-input*`.
2. [ ] **Colour from tokens** (2.0): no palette utility under `web/src/app/` — `npm run lint` enforces that. If a token is missing, it is **added**, with values for **both** modes and a computed contrast in the commit message. And: **looked at both modes**, not only the one that was being worked in.
3. [ ] **Surfaces:** ruled row or borderless section (2.1); `.app-card-link` + stretched-link contract only where really clickable (2.3).
4. [ ] **Type scale** observed (section 3), heading level follows the document structure.
5. [ ] **Destructive flow:** `danger` trigger → dialog → `danger-solid` execution (4.2).
6. [ ] **Loading state:** skeleton (page/list) or disabled button (action) — no loading text (6.1).
7. [ ] **Empty state:** `EmptyState` with a why + CTA (6.2).
8. [ ] **Error path:** `apiErrorTranslationKey` + `NoticeBanner error`; field errors per 5.3.
9. [ ] **i18n:** all keys in **both** locales, constant labels (9).
10. [ ] **A11y checklist** (10) worked through; new forms with a label + field-error ARIA.
11. [ ] **Audit harness** run, gates green including `contrastViolations` (12); for new pages: scenario added.
12. [ ] Convention changed/made more precise? → DECISIONS entry in the same commit (CLAUDE.md rule 3) and update this document.

## 12. Verification via the UI audit harness

- **What it is:** a Playwright harness separate from the e2e suite: it renders the UI state matrix (~30 scenarios × 5 viewports 360/480/768/1024/1536 × de/en × **dark/light**, API mocked) and writes a full-page screenshot + JSON metrics per state.
- **Theme is the fourth dimension, and it deliberately does not run in full.** Dark covers all five viewports, **light only 1536** — the same trade-off the `en` locale already makes. Justification: layout breaks are theme-independent (colour does not change box sizes), and what really needs checking in light mode is the contrast — which the axe gate measures in *every* state. That keeps the runtime well under 2×. **Exception:** whoever shifts the base surfaces of a mode or ships a mode anew removes the skip and reviews the full matrix in both modes.
- **`mobile` emulates a coarse pointer, the other four a fine one — and that is a contract, not convenience.** The app decides via `(pointer: coarse)` (`core/pointer/pointer-mode.service.ts`), not via the width: behind `!isCoarse()` lie the 7TV write paths. Without emulation Chromium under Playwright **always** reports `pointer: fine`, even at 360 px — the harness then renders controls that exist on no phone, and measures their overflow and target sizes as a finding. The emulation runs via a CDP session together with the colour scheme (both have to go into the **same** `Emulation.setEmulatedMedia` call, because the call replaces the feature set instead of extending it) plus `Emulation.setTouchEmulationEnabled` — the pointer values alone have no effect, Chromium derives coarse/fine from the touch emulation. **Two checks safeguard this**, and the second is the important one: one before the navigation and one immediately **before** the metrics are collected; each asserts *both* `(pointer: coarse)` and `(pointer: fine)`, not only the one matching the viewport — a known Chromium quirk can leave a context matching neither after touch emulation is toggled off, and a single-sided assertion cannot tell that apart from a genuine fine-pointer state. Whoever removes them takes away the harness's ability to notice its own breaking away.
- **`narrow` (480) exists because "fine pointer below 768 px" is a real, everyday state, not an edge case.** A desktop window at 200 % browser zoom, a window snapped to half a screen, DevTools docked to one side — none of these switch the OS pointer to touch, yet before this viewport every surface gated on `!isCoarse()` was measured only at 768 px and up. A green run only ever meant "no overflow at the widths this harness happened to look at" for those surfaces, not "no overflow below 768 px". #91's regression sat exactly in that gap: the header button group in `usage-stats-grid` overflowed at a measured 503 px window, and nothing in the matrix covered that width. 480 sits below Tailwind's `sm` breakpoint (640), so it measures the unprefixed, mobile-first layout under a mouse rather than re-measuring `sm:` rules `tablet` already covers. Unlike `requiresFinePointer` scenarios, `narrow` runs for **every** scenario, because the motivating regression was in a header that carried no such flag.
- **The screenshot is taken at viewport size, not with `fullPage: true`.** Playwright's full-page capture discards the emulation **during** the capture — proven on a pixel that carries the fine-pointer colour in the captured image although `matchMedia` still reported coarse immediately before. Affected are the image **and** all metrics collected afterwards; reordering does not help, and via Playwright contexts (`hasTouch`/`isMobile`) instead of CDP the same loss occurs. A raw CDP `Page.captureScreenshot({ captureBeyondViewport: true })` loses it too — the reset sits in Chromium's capture path, not in Playwright's wrapper. Instead the viewport is briefly enlarged to `document.documentElement.scrollHeight`, captured normally and shrunk again. **Counter-check that this introduces no artefact:** during the rebuild not a single scenario outside of `mobile` changed (0 out of 392).
- **On horizontal overflow a second image `<base>--right.png` is produced.** The viewport-sized capture is only `vp.width` wide, so overflowing content would lie outside the frame — of all things the finding the audit is looking for. Widening the viewport is out of the question (different breakpoints, the overflow disappears); scrolling, by contrast, is page state and leaves the emulation untouched. The second image is taken at the right-hand end and is only produced if `scrollWidth > vp.width`. It shifts no metric.
- **Two desktop cases, and both are needed.** `desktop-narrow` (1024) is `lg` at its narrowest point: the sidecar is there, the 80 rem cap does not yet bite, 992 px of content remain — that is where a wrap breaks first. `desktop` (1536) is the capped state, that is what gets delivered on a large screen. A single viewport can show only one of the two. **Whoever changes the content width (§8.4a) checks both values along with it** — the wider one has to lie above the cap, otherwise it stops measuring it.
- **When to run it:** after every layout/style change with an effect on surfaces, for every new page (add it as a scenario in `web/e2e/audit/ui-audit.audit.ts` beforehand — mock the route, use edge-case data with long names) and before finishing every larger UI round.
- **How:**

  ```
  cd web
  npx playwright test --config=playwright.audit.config.ts
  ```

  (No npm script; it starts `ng serve` on port 4300 itself.) Output under `web/.audit-out/` (gitignored): `shots/<scenario>--<viewport>--<locale>--<theme>.png`, `metrics/<scenario>--<viewport>--<locale>--<theme>.json`. **Before a run whose metrics you evaluate, empty `.audit-out/`** — otherwise files from earlier runs stay behind (including ones for scenarios since removed) and falsify every tally over the directory.
- **Reading the metrics:** per JSON file:
  - `horizontalOverflowPx` — horizontal page overflow in px. **Gate: has to be 0.** On `mobile` only what is rendered at all with a coarse pointer counts (see above) — a trigger behind `!isCoarse()` can no longer drive this number.
  - `smallTargetsUnder24` — interactive elements < 24 px (WCAG 2.5.8). **Gate: no new entries compared with the last run** (existing entries are documented "equivalent target" exceptions).
  - `targets24to43` — elements below the 44 px comfort target: observe, no hard gate.
  - `beyondRightEdge` — elements beyond the right viewport edge: treat like overflow.
  - `contrastViolations` — axe findings for the rule `color-contrast`, filtered to `serious`/`critical`. **Gate: has to be empty.** What axe does not see is in §10.
- Review the screenshots in addition (de **and** en — longer German strings are the most frequent wrap break; and in the shipping round of a mode, both themes).
- **A scenario whose `afterLoad` touches a `!isCoarse()` control carries `requiresFinePointer: true`** and is skipped on coarse viewports — it describes no reachable state there. That does not shrink the coverage: what was measured before was only a state that does not exist. The condition hangs on the viewport's pointer, not on its name, so that a future second coarse viewport applies along without rework.
- **Reference:** `web/playwright.audit.config.ts`, `web/e2e/audit/ui-audit.audit.ts`.
