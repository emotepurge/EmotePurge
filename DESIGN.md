---
name: EmotePurge
description: A light table for other people's artwork — graphite or paper, one guide line, nothing else.
colors:
  accent: "#00d3bc"
  accent-solid: "#0a6f64"
  accent-solid-hover: "#085a51"
  accent-wash: "#06231f"
  accent-wash-fg: "#57e8d8"
  on-accent: "#ffffff"
  page: "#0d0f12"
  surface: "#15181c"
  surface-inset: "#1f232a"
  surface-inset-hover: "#282d35"
  field: "#0a0c0f"
  border: "#262b32"
  border-strong: "#353b43"
  border-field: "#6b747e"
  fg: "#e9ecee"
  fg-body: "#d4d9dd"
  fg-secondary: "#b2b9c0"
  fg-muted: "#8b939b"
  fg-disabled: "#565d65"
  emote-canvas: "#1f232a"
typography:
  display:
    fontFamily: "Archivo, ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, sans-serif"
    fontSize: "2.25rem"
    fontWeight: 800
    lineHeight: "2.5rem"
    letterSpacing: "-0.025em"
  headline:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "1.5rem"
    fontWeight: 700
    lineHeight: "2rem"
    letterSpacing: "-0.025em"
  title:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "1.125rem"
    fontWeight: 600
    lineHeight: "1.75rem"
  subtitle:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "1rem"
    fontWeight: 600
    lineHeight: "1.5rem"
  body:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: "1.25rem"
  label:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "0.6875rem"
    fontWeight: 600
    lineHeight: "1rem"
    letterSpacing: "0.13em"
  micro:
    fontFamily: "Archivo, ui-sans-serif, system-ui, sans-serif"
    fontSize: "0.625rem"
    fontWeight: 600
    lineHeight: "0.875rem"
    letterSpacing: "0.11em"
  data:
    fontFamily: "Azeret Mono, ui-monospace, Cascadia Code, Courier New, monospace"
    fontSize: "0.875rem"
    fontWeight: 400
    lineHeight: "1.25rem"
rounded:
  sm: "0.25rem"
  md: "0.375rem"
  lg: "0.5rem"
  sheet: "1rem"
  full: "9999px"
spacing:
  "1": "0.25rem"
  "2": "0.5rem"
  "3": "0.75rem"
  "4": "1rem"
  "6": "1.5rem"
  "8": "2rem"
components:
  button-primary:
    backgroundColor: "{colors.accent-solid}"
    textColor: "{colors.on-accent}"
    rounded: "{rounded.md}"
    padding: "0.375rem 0.75rem"
    typography: "{typography.body}"
  button-primary-hover:
    backgroundColor: "{colors.accent-solid-hover}"
    textColor: "{colors.on-accent}"
  button-neutral:
    backgroundColor: "{colors.surface-inset}"
    textColor: "{colors.fg-body}"
    rounded: "{rounded.md}"
    padding: "0.375rem 0.75rem"
  button-neutral-hover:
    backgroundColor: "{colors.surface-inset-hover}"
    textColor: "{colors.fg-body}"
  button-outline:
    textColor: "{colors.fg-secondary}"
    rounded: "{rounded.md}"
    padding: "0.375rem 0.75rem"
  button-lg:
    rounded: "{rounded.md}"
    padding: "0.5rem 1rem"
    height: "2.75rem"
  input:
    backgroundColor: "{colors.field}"
    textColor: "{colors.fg}"
    rounded: "{rounded.md}"
    padding: "0.5rem 0.75rem"
    typography: "{typography.body}"
  badge-accent:
    backgroundColor: "{colors.accent-wash}"
    textColor: "{colors.accent-wash-fg}"
    rounded: "{rounded.full}"
    padding: "0.125rem 0.5rem"
  segment-selected:
    backgroundColor: "{colors.accent-solid}"
    textColor: "{colors.on-accent}"
    padding: "0.375rem 0.75rem"
  segment-idle:
    backgroundColor: "{colors.surface-inset}"
    textColor: "{colors.fg-secondary}"
    padding: "0.375rem 0.75rem"
  popover-panel:
    backgroundColor: "{colors.surface}"
    rounded: "{rounded.md}"
    width: "16rem"
  emote-cell:
    backgroundColor: "{colors.emote-canvas}"
    rounded: "0"
---

# Design System: EmotePurge

> **Derived document.** The normative values live in `web/src/styles.css`; here they stand as a
> snapshot so that tools can read them mechanically. Whoever changes the token block regenerates
> this document (`$impeccable document`).
>
> **Contracts, patterns and the build checklist are not here**, they are in
> [docs/UI-Designsprache.md](docs/UI-Designsprache.md) — sticky layers and the z-ladder, the
> `h-14` height contract, the stretched-link contract, ARIA patterns, i18n obligations, the
> audit gates. That document is binding; this one describes the colour world.

## Overview

**Creative North Star: "The Light Table"**

This interface shows other people's material. Several hundred 7TV emotes per channel, fully
saturated, drawn by other people, made for dark chat — and this tool decides which of them get
deleted. Everything else follows from that: the ground stays out of the way. It is graphite and
not slate, because a blue-tinted ground tints every single one of these images. In light mode it
is paper on a table rather than pure white, because surfaces are the largest area of every page
and pure white glared on bright displays.

Exactly one colour is allowed to be loud, and it is a **guide**, not a light source: the
turquoise that carries selection, focus and marking. Packing and layout tools reserve a signal
colour like this for overlays that must never be confused with the material — here it does the
same. It draws hairlines. It does not glow, and it never sits underneath an image.

The third move is restraint by design. The interface keeps quiet in normal operation: no cards
around every row, no pills for things that go without saying, no motion without a state. What is
healthy is silent — so that the one thing that is currently not in order becomes the loudest
element on screen with nobody doing anything. Loudness exists, but it is tied to irreversibility,
not to importance.

**Key Characteristics:**

- A neutral graphite or paper ground that does not tint other people's artwork
- A single lead colour, used as a guide — selection, focus, marking, never decoration
- Two complete modes, maintained as equals; every colour role has a value in both
- Surfaces instead of frames: ruled rows, borderless sections, tinted blocks
- Dense lists up to ~900 rows, numbers in monospace
- Motion only as a response to a state

## Colors

A near-neutral ramp with a trace of green, plus one turquoise as the only saturated colour. The
roles are filled identically in both modes; only the values flip.

### Primary

- **accent** (`#00d3bc`): The guide turquoise. Focus ring, selection edge, active tab, links.
  It marks places — it fills no surfaces and never carries text, because white reaches only about
  2.5:1 on it. In light mode it cannot play that role (1.7:1 on white); there
  **accent-solid** takes over, which does the same job dark instead of light.
- **accent-solid** (`#0a6f64`): The same colour as a *surface*, everywhere text sits on it:
  filled primary actions, the selected segment, the pressed toggle. In light mode it is
  additionally the lead colour itself.
- **accent-solid-hover** (`#085a51`): The hover step below it. See *the Darker-on-hover rule*.
- **accent-wash / accent-wash-fg** (`#06231f` / `#57e8d8`): Tinted surface plus its text, for
  highlighted properties in badges. Two roles of their own, because text *on* the tinted surface
  has a different ground than text on a normal surface.

### Neutral

The ramp carries the whole interface. In dark it is glass, in light it is paper on a table; the
*direction* stays identical — raised moves away from the ground, inset moves back towards it.

- **page** (`#0d0f12`, light: `#eef0f2` "tabletop"): the page ground everything sits on.
- **surface** (`#15181c`, light: `#fafbfc` "sheet of paper"): the normal surface — panels,
  dialogs, overlays.
- **surface-inset** (`#1f232a`, light: `#e5e8eb`): inset blocks, neutral buttons, skeletons.
- **surface-inset-hover** (`#282d35`, light: `#dadde1`): their hover.
- **field** (`#0a0c0f`, light: `#fafbfc`): input fields. In dark they go *below* the page
  ground, because a field reads as pressed into the surface.
- **border** (`#262b32`, light: `#d7dade`) and **border-strong** (`#353b43`, light: `#bec3ca`):
  dividing lines and the stronger variant for outline buttons and overlay edges.
- **border-field** (`#6b747e`): the border that makes a control recognisable as a control.
  **The only colour token with an identical value in both modes** — it has to work against a
  light ground as well as a dark one.
- **fg** (`#e9ecee` → `#14171b`), **fg-body** (`#d4d9dd` → `#23282e`), **fg-secondary**
  (`#b2b9c0` → `#3f464e`), **fg-muted** (`#8b939b` → `#5d656e`), **fg-disabled** (`#565d65` →
  `#a2a9b1`): five steps. *fg-muted* is the weakest text step allowed; its tightest case is
  fg-muted on surface-inset — light 4.81:1, dark 5.06:1.

### Tertiary

- **emote-canvas** (`#1f232a`, light: `#e6e8eb`): the plate an emote is drawn on. A token
  **of its own**, not surface-inset, because the material is foreign. It follows the mode instead
  of staying fixed dark; a deliberately accepted price: an emote with a white outline loses its
  contour in light.

### Semantic Tones

`success`, `warning`, `danger` and `info` bind per mode to Tailwind's palettes (`emerald`,
`amber`, `red`, `blue`) and therefore do **not** stand in the frontmatter — approximations would
otherwise stand there next to the real source. Every tone has up to five roles: `wash` (tinted
surface), `fg` (text on it), `solid` (filled surface), `solid-hover`, `dot` (meaning-bearing small
graphic, which owes 3:1 rather than 4.5:1). Amber alone was moved off the shared 700/600 pattern,
because unlike the other tones its 700/600 steps failed on the page itself, not only on a
secondary surface: light mode sets both `warning-fg` and `warning-dot` a step darker than their
peers — `amber-800` instead of the `700` step every other tone's `fg` uses (amber-700 measured
only 4.40:1 on the page and 4.09:1 on surface-inset, both under the 4.5:1 text gate; amber-800
clears both at 6.21:1 / 5.76:1), and `amber-700` instead of the `600` step every other tone's `dot`
uses (amber-600 only reaches 2.6:1 against surface-inset, under the 3:1 graphic gate; amber-700
clears it at 4.1:1). `success-fg` (emerald-700) clears the page at 4.70:1 and only fails on
`surface-inset` (4.36:1) and `surface-inset-hover` (3.94:1); `success-dot` (emerald-600) measures
2.97:1 on `surface-inset`, under its 3:1 gate — open: not yet moved.

### Named Rules

**The Guide rule.** The lead colour marks, it does not glow. It appears as a hairline, focus
ring, selection edge and link — never as a gradient, never as a coloured shadow, never as a
surface underneath an emote. Exactly one surface of the app carries a line in it: the selection
dock, and there it marks the boundary of a living, reversible state.

**The Darker-on-hover rule.** Filled surfaces go **darker on hover — in both modes**.
`*-solid-hover` always sits one step below `*-solid`. This is not an aesthetic: the print is white
in both modes, so a lighter hover can only take contrast away. No tool catches a violation,
because a hover is never checked rendered.

**The Role rule.** Tone names are meanings, not colours. Whoever asks for `red` is asking for a
value — and there is none, because a different one sits behind `danger` per mode. A new colour
means a new token with a value for **both** modes, never a reach into the palette.

## Typography

**Display/Body Font:** Archivo (with `ui-sans-serif`, `system-ui`, `Segoe UI`, Roboto)
**Data Font:** Azeret Mono (with `ui-monospace`, `Cascadia Code`, `Courier New`)

Both are variable fonts and are served from `/fonts/*.woff2` by us, not from a CDN. Archivo is a
grotesque with tight curves and an upright character — it carries dense lists without posing as
technical. Azeret Mono stands beside it for **data**, never as a costume for "technical".

### Hierarchy

Four levels with fixed class chains, plus two special roles.

- **Display** (800, 2.25rem, `sm:` 3rem, `tracking-tight`): the landing hero only. The public
  surface is deliberately marketing-scaled and does not follow the table below.
- **Headline** (700, 1.5rem, line height 2rem, `tracking-tight`): page titles. `<h1>` in layouts,
  `<h2>` on pages without a layout `<h1>` of their own.
- **Title** (600, 1.125rem, 1.75rem): section titles, `<h2>`.
- **Subtitle** (600, 1rem, 1.5rem): block titles, `<h3>`.
- **Body** (400, 0.875rem, 1.25rem): the working size. Roughly two thirds of all text classes in
  the project are this one.
- **Label** (600, 11px, `letter-spacing: 0.13em`, uppercase): small headings above data pairs and
  band separators. In the mono variant additionally as step markers on landing and login.
- **Data** (Azeret Mono, 0.875rem): every number that stands in a column.

### Named Rules

**The Four-levels rule.** An `<h3>` **never** carries the section size. Two levels that look the
same are one level. The heading *level* follows the document structure, the *look* follows the
table — both to be held to independently.

**The Mono-for-data rule.** Monospace stands for numbers, IDs and markers, never for body text
and never to make something look technical. Alignment in columns runs through the typeface, not
through `tabular-nums` — that utility deliberately exists nowhere in the project.

## Layout

The page scrolls as **one document**, not as an app frame with an inner scroll container. The
content column has **one** width app-wide: `max-w-7xl` (80rem), set on the header and on
`<main>`, with `px-4 py-8` as the page frame. The action dock and the six sections of the landing
page carry the same width — they move together or not at all.

Three layers stay put while scrolling and have fixed heights, because `sticky` needs exact offsets
for stacked layers: header 3.5rem at `top-0`, tab bars 2.5rem at `top-14`, filter toolbars
variable at `top-24` (= 14 + 10). These numbers are a basis for calculation, not an aesthetic.

The spacing rhythm is tight and repeats: rows `px-3 py-3`, sections `gap-3` above a hairline with
`pt-4`, page roots `gap-4` to `gap-8`, dialogs `p-6`. Groups are set with `gap`, not with margins
on the children.

Of the breakpoints, `sm:` (640px) carries the load — 62 occurrences against 11 for `md:` and 9 for
`lg:`. Responsive behaviour is structural: one column becomes two, a sidecar grid becomes a stack.
Font sizes stay fixed, except in the landing hero.

### Named Rules

**The One-width rule.** No route sets its own content width. Switching between a leaf page and a
list page would otherwise make the frame jump, and a layout that changes its width on every
navigation is more restless than a leaf page that gives away space. If a leaf needs more width,
it takes it *inside* the constant column.

**The Height-contract rule.** Whoever changes 3.5rem or 2.5rem drags every `top` and
`scroll-mt` value of the app along. These heights are a contract.

## Elevation & Depth

**The system is flat.** Depth comes from stacked surface brightness, not from
shadows — page below surface below surface-inset, in the same direction in both modes.

There is exactly **one** shadow token, and it belongs to the only genuinely raised surface of the
app: the overlay. Everything else — panels, rows, sections, blocks — lies on the layer it is
drawn on. A card class with a shadow existed until 2026-08-06 and was removed together with both
of its shadow tokens.

The sticky layers solve their overlap not through shadows but through a darkened blur:
`backdrop-filter: blur(8px)` over a partly transparent page colour. The opacity is itself a
token — light needs 92 %, dark gets by with 85 %, because light would otherwise let the text
underneath show through.

### Shadow Vocabulary

- **Overlay** (`box-shadow: 0 10px 30px -12px rgb(0 0 0 / 0.6)` dark, `0 10px 30px -10px rgb(20 23 27 / 0.22)` light):
  popover panels and dialogs. The only shadow in the system.

### Named Rules

**The One-shadow rule.** Whoever wants to "set off" a new surface picks a different surface
colour, not a shadow. There is only the overlay shadow, and it is tied to overlapping.

## Shapes

Tight radii, no soft shapes. `0.375rem` is the working radius for practically everything —
buttons, panels, input fields, tinted blocks, skeletons. `0.5rem` stays reserved for the dialog
window, `1rem` as the top edge for the sheet on touch devices, `9999px` for badges and
status dots.

Separation preferably comes from surface and hairline, not from a border running all the way
round. Rows are separated by `divide-y` and breathe past the text with a negative margin, so that
the hover sweep is wider than the content edge. Sections carry a single line at the top, not
a box.

**Emote cells deliberately have no radius.** A sprite sheet has no rounded cells; the straight
edge is what makes the grid legible as a sheet rather than as a collection of tiles.

### Named Rules

**The No-card rule.** There is no card class, and none is coming back. The test question before
every new separation: a card is a boundary against a **different kind of** neighbour. If every
neighbour is the same sort of thing, a border draws eight rectangles where one line says "list"
more clearly — and competes with the one thing that has to stand out.

**The Square-cell rule.** Whatever carries artwork stays square and flat: no radius, no alpha
checkerboard, no wash under the image. Selection paints as an `inset-ring` **inside** the cell
area, so that it does not inflate the grid.

## Components

The character is **restrained until it gets serious**: the normal case is quiet, and loudness
is tied to irreversibility, not to importance.

### Buttons

- **Shape:** working radius (0.375rem), two sizes — `md` (`0.375rem 0.75rem`) and `lg`
  (minimum height 2.75rem, `0.5rem 1rem`) as a comfort target for primary actions and touch.
- **Primary:** accent-solid with a white print, semibold. The *one* primary action of a context.
- **Neutral:** surface-inset with fg-body text. Secondary actions with a surface.
- **Outline:** only a border-strong edge plus fg-secondary text, hover fills with surface-inset.
  Quiet secondary actions and cancel in dialogs.
- **Hover / Focus:** fills go one step darker (see *the Darker-on-hover rule*), the
  focus ring is global and the same everywhere: 2px lead colour with a 2px offset.
- **Disabled:** the border falls away, the surface becomes surface-inset, the text becomes
  fg-disabled.
- **Destructive, three levels** — they encode the **position in the confirmation flow**, not the
  severity: outline triggers, solid executes, quiet is outline in series.

### Chips

Badges are fully rounded pills (`0.125rem 0.5rem`, 0.75rem text) in six tones, each a tinted
surface plus its matching text. States, by contrast, are **not** pills but a dot plus a word:
a 6px circle in the success or border colour next to fg-muted text.

### Cards / Containers

There are none. Surfaces come from three forms: the ruled row list (hairlines above, below and
between the rows, negative margin), the borderless section (one line at the top, `pt-4`, title on
the left and status marker on the right) and the tinted block (surface-inset, working radius,
`0.75rem`) for what genuinely borders on something of a different kind.

### Inputs / Fields

- **Style:** field as the surface, 1px border-field as the border, working radius,
  `0.5rem 0.75rem`. A compact variant with `0.375rem 0.5rem` for filter toolbars.
- **Focus:** The border switches to the lead colour, in addition to the global focus ring.
- **Error:** The border does **not** change. Errors carry a paragraph of their own in danger text
  below the field, wired up via `aria-describedby`.

### Navigation

Tab bars are router links with a 2px bottom edge: active in the lead colour with the full text
colour, inactive transparent with fg-muted text. No ARIA tabs pattern — these are real
navigations. Back-navigation is a single up-link to the parent node of the *information*
hierarchy, with the proper name of the target as its label, never "Back".

### The Sprite Sheet

The signature surface. The usage page and the ballot are not lists but a sheet of uniform cells,
grouped into four bands that are cut from the set itself rather than from fixed thresholds: the
emotes that together make up the first half of the usage, then up to 80 %, then the rest with at
least one hit, then the dead ones. Band headings are a hairline plus a label. The fill bar of a
cell measures against the peak of its *band*, not against that of the set — otherwise every bar
in the lower bands would be empty.

### Named Rules

**The Notable-how-often rule.** A pill marks a *notable* property, not every property. What
stands in every row marks nothing any more and becomes fg-muted text. Subsystems that report
their own health become a dot at `ok` and only become a pill at a warning — so that a healthy
overview carries **not a single coloured pill**, and the first conspicuous subsystem is the
loudest element with nobody doing anything.

**The Chrome-stays-quiet rule.** For the app header this applies one step more strictly than for
a page: what stands there stands on every screen in every session. And what the chrome says, no
page says a second time.

**The Trigger-then-execute rule.** Every destructive action has a trigger **and** an execution:
outline or quiet opens a dialog, solid confirms in it. A destructive button without a
confirmation dialog is not provided for. If the trigger repeats per row, it becomes quiet —
twenty red-outlined buttons stacked up make the rarest action of a page its loudest element.

## Do's and Don'ts

### Do:

- **Do** take every colour from the token set. If a role is missing, the **token** is added — with
  a value for both modes — instead of reaching into Tailwind's palette. `npm run lint` enforces
  this below `web/src/app/`.
- **Do** make filled surfaces **darker** on hover, in dark mode too.
- **Do** solve separation through surface and hairline: ruled row, borderless section,
  tinted block.
- **Do** leave the global focus ring alone. Whoever removes it owes an equivalent replacement.
- **Do** put primary actions and touch targets on `lg` (2.75rem minimum height) — there is
  nothing automatic about it, the default is `md`.
- **Do** set numbers in monospace when they stand in a column.
- **Do** use the existing primitives instead of rebuilding utility chains; the complete
  list is in the build checklist of the UI design language.

### Don't:

- **Don't** let the lead colour glow. No coloured shadows, no gradients, no saturated surfaces as
  an effect — guides are hairlines, not light sources.
- **Don't** make the ground blue-tinted. Slate-tinted surfaces tint several hundred pieces of
  foreign artwork along with them; that is the reason for graphite, and it still holds.
- **Don't** introduce a card class. No rectangle around every row, no `.app-card` under
  a different name.
- **Don't** use motion as ornament. No entrance animations, no staggered reveals, no
  `behavior: 'smooth'` on a page change. Motion answers a state or does not happen.
- **Don't** put a hover on something that is not clickable — it promises a click that
  does not exist.
- **Don't** hand out a pill for something that is the same word on most rows.
- **Don't** use an emoji as an icon. The empty state deliberately has no icon slot.
- **Don't** hardwire a colour because it "works in both modes". There is exactly one theme-fixed
  colour role, and that is the border-field of a control.
