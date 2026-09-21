import { ListRange } from '@angular/cdk/collections';
import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import {
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  linkedSignal,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { ListSelection } from '../selection/list-selection';
import { EmoteSprite } from '../emotes/emote-sprite';
import { EmoteSpriteAnimated } from '../emotes/emote-sprite-animated';
import { isAnimatedEmoteUrl } from '../emotes/emote-url';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';

/** Sprite edge and gutter in px — same numbers as the usage atlas (`ATLAS_CELL_PX`/`ATLAS_GAP_PX` in
 *  `shared/grid/atlas-grid.ts`), kept as local constants rather than imported: that file's row type
 *  and `packAtlasRows` are coupled to `UsageBandKey` and `EmoteUsageTotal`, neither of which this
 *  grid has (spec Falle F4 — this grid is built fresh, not layered onto the atlas). */
const CELL_PX = 64;
const GAP_PX = 4;
/** The name line under each sprite. Picking emotes one by one is a decision about *which* emote,
 *  and the name is what that decision is made on — it is what lands in the target set and what the
 *  collision hint in the confirmation is about. */
const LABEL_PX = 16;
const TILE_PX = CELL_PX + LABEL_PX;
const ROW_PX = TILE_PX + GAP_PX;
/** EmoteSprite's own default, restated so the cell's still can add one class to it. */
const SPRITE_CLASS = 'h-full w-full object-contain p-1';

/**
 * Which 7TV score field the grid is currently sorted by, or `'none'` for the set's own order.
 * `'none'` is the required default (spec P5'/AK16) — a score must never be the pre-selected sort,
 * since it reads as "popular in this channel" the moment it visually leads, and there is no such
 * thing for a channel this account has no role in.
 */
export type ForeignEmoteSortMode = 'none' | 'topAllTime' | 'trending';

interface SortOption {
  value: ForeignEmoteSortMode;
  labelKey: string;
}

/**
 * The sort options, in menu order. The wording is load-bearing and has now been wrong twice, in
 * opposite directions, so all three constraints are contracts rather than copy:
 *  - it names a property of a **single emote**, never the origin of the list. "7TV global · Top
 *    aller Zeiten" in a tab-bar-shaped control had the operator conclude the grid was showing 7TV's
 *    global emotes rather than this channel's set;
 *  - it claims **no unit**. The replacement wording ("Verbreitung", "in wie vielen 7TV-Sets") traded
 *    that misreading for an invented quantity: the value is `Emote.scores.topAllTime`/`trendingDay`,
 *    a ranking score, and emphatically NOT `Emote.channels.totalCount` — this feature never asks for
 *    that field, because it hangs off 7TV's search bucket and overrunning it locks us out for about
 *    an hour (spec, score contract). A comparison value without a unit is honest; a made-up unit is
 *    not;
 *  - and never plain "Beliebtheit" — a channel-relative popularity does not exist for a channel
 *    nobody here has a role in, and the word would promise one (spec P5').
 */
const SORT_OPTIONS: SortOption[] = [
  { value: 'none', labelKey: 'import.foreignChannel.sort.none' },
  { value: 'topAllTime', labelKey: 'import.foreignChannel.sort.topAllTime' },
  { value: 'trending', labelKey: 'import.foreignChannel.sort.trending' },
];

/** Instance-unique suffix for {@link ForeignEmoteGrid.sortSelectId} (spec E12) — the id was
 *  hardcoded (`'foreign-emote-sort'`), and while the picker step and the leaderboard step never
 *  render two grids at once today, a colliding `id`/`for` pair costs nothing to avoid and removes
 *  the trap for whoever adds a second simultaneous instance later. */
let foreignEmoteGridInstanceCount = 0;

function columnsForWidth(width: number): number {
  if (!Number.isFinite(width) || width <= 0) {
    return 1;
  }
  return Math.max(1, Math.floor((width + GAP_PX) / (CELL_PX + GAP_PX)));
}

function chunkIntoRows<T>(items: readonly T[], columns: number): T[][] {
  const safeColumns = Math.max(1, columns);
  const rows: T[][] = [];
  for (let i = 0; i < items.length; i += safeColumns) {
    rows.push(items.slice(i, i + safeColumns));
  }
  return rows;
}

/**
 * The selection grid for a foreign channel's 7TV set (spec Falle F4). Deliberately not the
 * `usage-stats-page.html` atlas reused: that one is keyed on our internal `emoteId` Guid and reads
 * `EmoteUsageTotal` for its bands/sparkline/fill-bar, none of which exists for a channel this
 * account has no role in. This grid owns its own `ListSelection<ForeignEmoteRow>` keyed on
 * `sevenTvEmoteId`, the 7TV ObjectID (Regel 8).
 *
 * Virtualized with `CdkVirtualScrollViewport` (HandOfBlood's set is ~956 emotes) — items are
 * chunked into fixed-column rows the same way the atlas is, and for the same reason: a *row*-level
 * `trackBy` (by index) keeps the virtualized row views stable across a resize or a sort change,
 * while the *inner* `@for` tracks each cell by `sevenTvEmoteId` so individual cells reconcile
 * correctly instead of being torn down and rebuilt — a rebuilt cell means a rebuilt
 * `app-emote-sprite`, which starts `visibility: hidden` again until it reloads, i.e. the atlas's
 * known flicker (see `usage-stats-page.ts`'s `trackRow`; this repo has hit that bug once already
 * on a virtualized grid without a stable trackBy).
 *
 * **This viewport is the only scroll container in the dialog**, and the height expression is what
 * keeps it that way. The pane around it scrolls too by default (`.app-dialog-panel`), and two nested
 * scrollbars over the same list was the reported defect; a percentage height chain is not available
 * as a fix, because it dies on the two `display: inline` component hosts between the pane and this
 * element (design language §7). So the viewport is measured against `dvh` instead:
 * `min(34rem, max(4rem, 100dvh - (26rem + reservedRem)))`.
 *
 *  - `34rem` is the ceiling — a tall desktop screen should not turn the dialog into a wall of emotes.
 *  - `26rem` is the allowance for the chrome this component always renders itself (the sort/count row,
 *    the score hint). Measured at 22rem (the dialog chrome plus the pane's own 2rem margin) with no
 *    caller chrome above the grid, so this carries about 4rem of slack on its own.
 *  - {@link reservedRem} is a caller's *own* chrome above the grid, inside the same scrolling pane —
 *    e.g. the foreign-channel step's source-set radiogroup, which renders zero to N rows depending on
 *    the channel. `0` by default (unset), reproducing the original fixed 26rem exactly. This exists
 *    because a fixed allowance sized for "no caller chrome" regressed to the exact double-scrollbar
 *    defect described above the moment K3's source-set radiogroup started rendering unconditionally
 *    (spec addendum 2026-09-21, review finding P2-1): four rows of radiogroup ate the 4rem slack many
 *    times over on any but the tallest windows. This component cannot size for markup it does not
 *    render itself, so the caller computes its own reservedRem and passes it through.
 *  - `4rem` is a **floor, not a minimum useful size**, and it was deliberately lowered from 16rem:
 *    a floor of F re-creates the double scrollbar for every viewport below `F + 22rem + reservedRem`,
 *    so 16rem put the defect back on any window under ~608 px — a 1366×768 laptop, or any zoomed one.
 *    Measured at 500 px the pane overflowed by 107 px. At 4rem the band is under ~416 px, i.e. shorter
 *    than the dialog's own chrome, where nothing can help.
 *
 * No positive floor removes that band entirely; only a real height chain from the pane could, and
 * that would mean making `DialogShell`'s host a flex column for all twelve of its dialogs. Not worth
 * it for a band this small — but that is the fix if the floor ever has to rise again. The numbers
 * above (with `reservedRem` at its default of `0`) are pinned by an E2E case ("the grid shrinks on a
 * short window…", `emote-import.e2e.spec.ts`); a second case pins the non-zero-reservedRem path
 * across several window heights and set counts ("a multi-set radiogroup does not grow a second
 * scrollbar…", same file) — jsdom has no layout and nothing else here can see either.
 */
@Component({
  selector: 'app-foreign-emote-grid',
  imports: [Button, EmoteSprite, EmoteSpriteAnimated, NoticeBanner, ScrollingModule, TranslocoPipe],
  template: `
    @if (truncated()) {
      <app-notice-banner variant="warning">
        {{ truncatedMessageKey() | transloco: truncatedNoticeParams() }}
      </app-notice-banner>
    }

    @if (emotes().length === 0) {
      <p class="text-sm text-fg-muted">{{ emptyMessageKey() | transloco }}</p>
    } @else {
      <div class="flex flex-wrap items-center justify-between gap-2">
        <!-- A labelled select, not a segmented control: the old segmented control read as a tab bar
             announcing what the list *was*, which is exactly the misreading this whole rewording
             fixes. "Sortieren nach" in front of it says what the choice does. Rendered only without
             a forcedSortMode (spec E12/F2) — on the leaderboard the sort IS the list, and a second
             control that re-sorts it on top would be a second, contradicting way to do the same
             thing. -->
        @if (forcedSortMode() === null) {
          <div class="flex items-center gap-2">
            <label class="text-sm text-fg-secondary" [for]="sortSelectId">
              {{ 'import.foreignChannel.sort.label' | transloco }}
            </label>
            <select [id]="sortSelectId" class="app-input-sm" (change)="onSortChange($event)">
              @for (option of sortOptions; track option.value) {
                <option [value]="option.value" [selected]="option.value === sortMode()">
                  {{ option.labelKey | transloco }}
                </option>
              }
            </select>
          </div>
        }
        <!-- Count and clear grouped tightly (§7). -->
        <div class="flex items-center gap-2">
          <span class="text-xs text-fg-muted">
            {{ 'import.foreignChannel.selectedCount' | transloco: selectedCountParams() }}
          </span>
          @if (selection.selectedKeys().length > 0) {
            <button type="button" appButton="neutral" (click)="clearSelection()">
              {{ 'import.clearSelection' | transloco }}
            </button>
          }
        </div>
      </div>

      <!-- The hint belongs to the TILES, not to the sort row: it explains the number printed on
           each of them, and says what it is not — network-wide, this one emote, no unit claimed. So it wraps together with the grid in its own tight column (§7 — spacing is
           the shell's flex gap, and what belongs together more closely than that rhythm wraps
           itself in its own tighter flex column), instead of floating at equal distance between the two
           and reading as a caption for neither. It appears only while a score sort is active — under
           the *effective* mode (spec F2), so a forcedSortMode shows it too even though there is no
           select to have driven it; with the set order showing there is no number to explain. -->
      <div class="flex flex-col gap-1">
        @if (effectiveSortMode() !== 'none') {
          <p class="text-xs text-fg-muted">
            {{ scoreHintKey() | transloco }}
          </p>
        }

        <!-- tabindex -1: where focus goes when the clear button unmounts itself. -->
        <div
          #gridContainer
          role="group"
          tabindex="-1"
          [attr.aria-label]="'import.foreignChannel.grid.ariaLabel' | transloco"
          (mouseleave)="onGridLeave()"
        >
          <cdk-virtual-scroll-viewport [itemSize]="rowPx" [style.height]="viewportHeight()">
            <div
              *cdkVirtualFor="let row of rows(); trackBy: trackRowIndex"
              [style.height.px]="rowPx"
            >
              <div
                class="grid gap-1"
                [style.grid-template-columns]="'repeat(' + columns() + ', ' + cellPx + 'px)'"
              >
                @for (emote of row; track emote.sevenTvEmoteId) {
                  <button
                    type="button"
                    class="flex w-16 flex-col items-stretch"
                    [attr.aria-pressed]="selection.isSelected(emote)"
                    [attr.aria-label]="cellLabel(emote)"
                    [title]="cellLabel(emote)"
                    (click)="onCellClick(emote, $event)"
                    (mousedown)="$event.shiftKey && $event.preventDefault()"
                    (mouseenter)="onCellEnter(emote)"
                    (focus)="onCellFocus(emote)"
                    (mouseleave)="onCellLeave(emote)"
                    (blur)="onCellBlur(emote)"
                  >
                    <span
                      class="app-sprite-cell relative block h-16 w-16 transition-shadow hover:inset-ring-1 hover:inset-ring-border-strong"
                    >
                      <!-- The still stays mounted under the animation and hides once that has painted. -->
                      <app-emote-sprite
                        [url]="emote.imageUrl"
                        [size]="cellPx"
                        [spriteClass]="stillHidden(emote) ? hiddenSpriteClass : spriteClass"
                      />
                      @if (playsAnimation(emote)) {
                        <span class="absolute inset-0">
                          <app-emote-sprite-animated
                            [url]="emote.imageUrl"
                            [size]="cellPx"
                            (animationShown)="revealedKey.set(emote.sevenTvEmoteId)"
                          />
                        </span>
                      }
                      @if (isAnimated(emote)) {
                        <!-- Decorative: the accessible name already says animated. -->
                        <span
                          class="pointer-events-none absolute top-0 right-0 flex h-3 w-3 items-center justify-center"
                          [style.background-color]="'var(--ep-sprite-scrim)'"
                          [style.color]="'var(--ep-sprite-scrim-fg)'"
                          aria-hidden="true"
                        >
                          <svg class="h-2 w-2" viewBox="0 0 8 8" aria-hidden="true">
                            <path d="M2 1v6l5-3z" fill="currentColor" />
                          </svg>
                        </span>
                      }
                      @if (effectiveSortMode() !== 'none') {
                        <span
                          class="absolute bottom-0 left-0 px-1 font-mono text-[9px] leading-[1.4] font-medium"
                          [style.background-color]="'var(--ep-sprite-scrim)'"
                          [style.color]="'var(--ep-sprite-scrim-fg)'"
                          >{{ scoreBadge(emote) }}</span
                        >
                      }
                      @if (selection.isSelected(emote)) {
                        <span
                          class="pointer-events-none absolute inset-0 bg-accent-wash/70 inset-ring-2 inset-ring-accent-fg"
                          aria-hidden="true"
                        ></span>
                      }
                    </span>
                    <!-- aria-hidden: the accessible name of the tile already carries the alias and,
                       where it differs, the global default name — announcing the visible line as
                       well would read the alias twice. -->
                    <span
                      [class]="
                        'block truncate text-center text-[10px] leading-4 ' +
                        (selection.isSelected(emote) ? 'font-medium text-fg' : 'text-fg-muted')
                      "
                      aria-hidden="true"
                      >{{ emote.name }}</span
                    >
                  </button>
                }
              </div>
            </div>
          </cdk-virtual-scroll-viewport>
        </div>
      </div>
    }
  `,
  // The grid's own children are the shell's rhythm one level down: without a flex host they would
  // stack as bare blocks with no gap at all, which is what made the score hint cling to whatever
  // happened to sit above it.
  host: { class: 'flex flex-col gap-3' },
})
export class ForeignEmoteGrid {
  readonly emotes = input.required<ForeignEmoteRow[]>();
  /** Whether the source's page cap was hit while 7TV reported more entries (spec F3) — never
   *  silently swallowed, see the notice above. */
  readonly truncated = input(false);
  /** What 7TV reports as the set's total entry count — only meaningful together with `truncated`. */
  readonly totalCount = input<number | null>(null);
  /**
   * Additive, optional (spec E12): a caller-supplied sort that wins over the grid's own `<select>`,
   * which is not rendered while this is set. On the leaderboard the sort is the server-side
   * dimension that picked the list in the first place — a second, in-grid control that re-sorts it
   * on top would be a bug, not a feature (spec F2). `'none'` is deliberately not a legal value here:
   * the grid's own default already covers "no score sort", and allowing `'none'` would let a caller
   * force the `<select>` away while asking for the un-forced default, which is indistinguishable
   * from simply leaving this input unset.
   */
  readonly forcedSortMode = input<Exclude<ForeignEmoteSortMode, 'none'> | null>(null);
  /** Caption overrides (spec E12/F2) — the default texts are written for a channel's own set
   *  ("…dieses Kanals", "…des Sets") and read wrong for a network-wide list. Left unset, the grid
   *  behaves exactly as it does today. */
  readonly emptyMessageKey = input('import.foreignChannel.empty');
  readonly truncatedMessageKey = input('import.foreignChannel.truncated');
  readonly scoreHintKey = input('import.foreignChannel.sort.scoreHint');

  /** Extra vertical rem a caller's own chrome above this grid takes inside the same scrolling pane —
   *  folded into the viewport's height budget (see the class doc above). `0` (the default) reproduces
   *  the original, caller-agnostic height exactly. */
  readonly reservedRem = input(0);

  /** The current selection, emitted on every change so a host (the picker step) can gate the
   *  dialog's "weiter" button and build the eventual `ImportRow[]`. */
  readonly selectionChange = output<ForeignEmoteRow[]>();

  private readonly languageService = inject(LanguageService);
  // Needed in `cellLabel`, which builds a string rather than rendering one — the tile's accessible
  // name has to carry the score, and an aria-label cannot be assembled by the template pipe.
  private readonly transloco = inject(TranslocoService);

  private readonly gridContainerRef = viewChild<ElementRef<HTMLElement>>('gridContainer');
  private readonly viewport = viewChild(CdkVirtualScrollViewport);
  private readonly containerWidth = signal(0);
  /** The rows the viewport currently renders, mirrored from `renderedRangeStream`. */
  private readonly renderedRange = signal<ListRange>({ start: 0, end: 0 });

  /** Never pre-selected (spec P5'/AK16) — see {@link ForeignEmoteSortMode}. Only meaningful without
   *  a `forcedSortMode`; see {@link effectiveSortMode}. */
  protected readonly sortMode = signal<ForeignEmoteSortMode>('none');

  protected readonly sortOptions = SORT_OPTIONS;
  /** Instance-unique (spec E12) so two grids in the same document never collide on `id`/`for`. */
  protected readonly sortSelectId = `foreign-emote-sort-${++foreignEmoteGridInstanceCount}`;

  /**
   * The mode that actually governs display order, the score tile, the explainer, and the cell's
   * accessible name (spec F2 — one signal driving all four, not four separate reads of `sortMode`).
   * A `forcedSortMode` wins outright; otherwise this is exactly the `<select>`'s own state. Regel
   * 14: this is a `computed()` over signals, not a plain field, so every consumer below reacts the
   * same way to either input changing.
   */
  protected readonly effectiveSortMode = computed<ForeignEmoteSortMode>(
    () => this.forcedSortMode() ?? this.sortMode(),
  );

  /**
   * Sorting rearranges display order only — it never touches `ListSelection`'s `selectedKeySet`,
   * which is authoritative and key-based (see that class's doc comment), so a selection made before
   * a sort change survives it. `null` scores sort last regardless of direction, since "no 7TV score"
   * is not "the lowest score".
   */
  protected readonly sortedEmotes = computed<ForeignEmoteRow[]>(() => {
    const mode = this.effectiveSortMode();
    const items = this.emotes();
    if (mode === 'none') {
      return items;
    }
    return [...items].sort((a, b) => {
      const scoreA = a[mode];
      const scoreB = b[mode];
      if (scoreA === null && scoreB === null) {
        return 0;
      }
      if (scoreA === null) {
        return 1;
      }
      if (scoreB === null) {
        return -1;
      }
      return scoreB - scoreA;
    });
  });

  /** The option currently sorted by, or `null` for the set's own order — the one place that maps
   *  the mode back onto its label, so the tile's accessible name and the control agree by
   *  construction. */
  protected readonly activeSortOption = computed<SortOption | null>(
    () =>
      SORT_OPTIONS.find(
        (option) => option.value === this.effectiveSortMode() && option.value !== 'none',
      ) ?? null,
  );

  protected readonly columns = computed(() => columnsForWidth(this.containerWidth()));
  protected readonly rows = computed(() => chunkIntoRows(this.sortedEmotes(), this.columns()));

  /** The viewport's `height` style — a `computed()` rather than the static Tailwind class the pane
   *  used before {@link reservedRem} existed: Tailwind's arbitrary-value classes are resolved at
   *  build time from the literal string in source and cannot take a runtime input, so a dynamic
   *  allowance has to be a plain inline style instead (Regel 14 applies to it the same as to
   *  anything else the template reads reactively). */
  protected readonly viewportHeight = computed(
    () => `min(34rem, max(4rem, calc(100dvh - ${26 + this.reservedRem()}rem)))`,
  );

  protected readonly selection = new ListSelection<ForeignEmoteRow>(
    this.sortedEmotes,
    (row) => row.sevenTvEmoteId,
  );

  /**
   * Transloco interpolation prints its params as raw JS numbers — no grouping. 7TV's "top overall"
   * leaderboard reports totals in the millions (1372094, not 1.372.094/1,372,094), and `loaded`
   * runs into the low thousands on a truncated load, so both need locale-aware formatting rather
   * than a straight pass-through. Regel 14: a `computed()` over `languageService.lang()`, so a
   * language switch reformats the separator instead of freezing on whatever was active on first
   * render.
   */
  protected readonly truncatedNoticeParams = computed(() => {
    const locale = toLocale(this.languageService.lang());
    const total = this.totalCount();
    return {
      loaded: this.emotes().length.toLocaleString(locale),
      totalCount: total === null ? total : total.toLocaleString(locale),
    };
  });

  /** Same reasoning as {@link truncatedNoticeParams}: a channel's 7TV set can hold well over 1000
   *  emotes (`SevenTvApiClient.cs`'s "subscriber-sized sets" note), so a "N selected" count reaches
   *  four digits once a large set is mostly picked. */
  protected readonly selectedCountParams = computed(() => ({
    count: this.selection
      .selectedKeys()
      .length.toLocaleString(toLocale(this.languageService.lang())),
  }));

  protected readonly cellPx = CELL_PX;
  protected readonly rowPx = ROW_PX;
  protected readonly spriteClass = SPRITE_CLASS;
  protected readonly hiddenSpriteClass = `${SPRITE_CLASS} invisible`;

  /** The cell the pointer rests on and the focused cell, by 7TV id, each ended only by its own events. */
  private readonly pointerKey = signal<string | null>(null);
  private readonly focusKey = signal<string | null>(null);
  /** The one cell that may play, pointer first. One per grid because `EmoteSpriteAnimated` starts its
   *  dwell on mount, in every cell it is rendered in. */
  protected readonly playingKey = computed(() => this.pointerKey() ?? this.focusKey());

  /**
   * Which playing cell's animation has painted, so its still can hide (see `stillHidden`).
   * Reset whenever the playing key changes, including the hand-back from pointer to focus — the same
   * race `EmoteSpriteAnimated.revealedAnimatedUrl` guards against: coming back to a cell must not hide
   * its still before the new animation has painted.
   */
  protected readonly revealedKey = linkedSignal<string | null, string | null>({
    source: this.playingKey,
    computation: () => null,
  });

  constructor() {
    // Same pattern as `usage-stats-page.ts`'s sheet-width effect: the column count follows the
    // element that actually holds the cells, not the viewport, and jsdom has no ResizeObserver at
    // all — specs stub the global the same way that page's do.
    effect((onCleanup) => {
      const element = this.gridContainerRef()?.nativeElement;
      if (!element) {
        return;
      }
      this.containerWidth.set(element.clientWidth);
      const observer = new ResizeObserver((entries) => {
        this.containerWidth.set(entries[0].contentRect.width);
      });
      observer.observe(element);
      onCleanup(() => observer.disconnect());
    });

    effect((onCleanup) => {
      const viewport = this.viewport();
      if (!viewport) {
        return;
      }
      const subscription = viewport.elementScrolled().subscribe(() => this.onViewportScroll());
      this.renderedRange.set(viewport.getRenderedRange());
      subscription.add(
        viewport.renderedRangeStream.subscribe((range) => this.renderedRange.set(range)),
      );
      onCleanup(() => subscription.unsubscribe());
    });

    // Either key outlives its cell otherwise: virtualisation removes a focused cell without a blur,
    // and a click focuses the cell in Chrome and Firefox. The cell would then play again when it
    // renders with no hover and no focus on it. Checked against the rendered range, not the scroll
    // event, because the range can change after the last scroll event has been handled.
    for (const key of [this.pointerKey, this.focusKey]) {
      effect(() => {
        const value = key();
        if (value !== null && !this.isRendered(value)) {
          key.set(null);
        }
      });
    }
  }

  protected onSortChange(event: Event): void {
    this.sortMode.set((event.target as HTMLSelectElement).value as ForeignEmoteSortMode);
  }

  protected onCellClick(emote: ForeignEmoteRow, event: MouseEvent): void {
    this.selection.onRowClick(emote, event);
    this.selectionChange.emit(this.selection.selectedItems());
  }

  /** Must emit: host steps see the selection only through selectionChange. */
  protected clearSelection(): void {
    // The clicked button unmounts with the selection; focus must not fall to <body> (WCAG 2.4.3).
    this.gridContainerRef()?.nativeElement.focus({ preventScroll: true });
    this.selection.clear();
    this.selectionChange.emit([]);
  }

  protected onCellEnter(emote: ForeignEmoteRow): void {
    this.pointerKey.set(emote.sevenTvEmoteId);
  }

  /** Only if the pointer key is still this cell's: events from another cell must not end it. */
  protected onCellLeave(emote: ForeignEmoteRow): void {
    if (this.pointerKey() === emote.sevenTvEmoteId) {
      this.pointerKey.set(null);
    }
  }

  protected onCellFocus(emote: ForeignEmoteRow): void {
    this.focusKey.set(emote.sevenTvEmoteId);
  }

  /** Ends focus playback only: a clicked cell keeps its pointer key until the pointer leaves. */
  protected onCellBlur(emote: ForeignEmoteRow): void {
    if (this.focusKey() === emote.sevenTvEmoteId) {
      this.focusKey.set(null);
    }
  }

  /** Hands playback back to the focused cell, if any. */
  protected onGridLeave(): void {
    this.pointerKey.set(null);
  }

  /** Both import sources encode 7TV's animated flag into the url (`4x_static.webp`), so the marker
   *  needs no field of its own. */
  protected isAnimated(emote: ForeignEmoteRow): boolean {
    return isAnimatedEmoteUrl(emote.imageUrl);
  }

  /** Whether this cell mounts the animated sprite: the playing one, and only if it has an animation.
   *  A hovered still mounts nothing and requests nothing. */
  protected playsAnimation(emote: ForeignEmoteRow): boolean {
    return this.playingKey() === emote.sevenTvEmoteId && this.isAnimated(emote);
  }

  /** The cell's own still hides only once its animation has painted over it. */
  protected stillHidden(emote: ForeignEmoteRow): boolean {
    return this.playsAnimation(emote) && this.revealedKey() === emote.sevenTvEmoteId;
  }

  protected trackRowIndex(index: number): number {
    return index;
  }

  /**
   * The tile's accessible name — and its mouse tooltip, since the visible line under the sprite is
   * truncated at 64 px. The alias comes first because that is the name being copied; the global
   * default name follows in brackets only where the two differ, which they do in 296 of
   * HandOfBlood's 956 entries.
   *
   * **The active score belongs in here too.** An explicit `aria-label` *replaces* the descendant
   * text in the accessibility tree, so the number printed on the tile simply does not exist for a
   * screen reader unless it is named here — and it is the very thing the user is sorting by.
   * It is announced under the same label the sort control carries, which is what gives the bare
   * number its meaning without inventing a unit for it. The number itself is the same compact text
   * the tile shows; only the *missing* case differs, because the tile's dash is a typographic
   * placeholder and reads as nothing at all when spoken.
   *
   * "Animated" sits between the names and the score, for the same reason: the play marker in the
   * corner is `aria-hidden`, so this is the only place a screen reader learns it.
   */
  protected cellLabel(emote: ForeignEmoteRow): string {
    const aliased =
      emote.name === emote.defaultName ? emote.name : `${emote.name} (${emote.defaultName})`;
    const names = this.isAnimated(emote)
      ? `${aliased}, ${this.transloco.translate('import.animated')}`
      : aliased;
    const active = this.activeSortOption();
    if (active === null) {
      return names;
    }
    return `${names}, ${this.transloco.translate(active.labelKey)}: ${this.spokenScore(emote)}`;
  }

  protected scoreBadge(emote: ForeignEmoteRow): string {
    const value = emote[this.effectiveSortMode() as Exclude<ForeignEmoteSortMode, 'none'>];
    if (value === null || value === undefined) {
      return '–';
    }
    const locale = toLocale(this.languageService.lang());
    return value >= 1000
      ? `${(value / 1000).toLocaleString(locale, { maximumFractionDigits: 1 })}k`
      : value.toLocaleString(locale);
  }

  /**
   * A cell recycled under a resting pointer fires no mouseleave, so a scroll clears the pointer key.
   * The focus key survives it: Tab scrolls a partly hidden cell into view, and that cell should play.
   * Focus leaving the cell ends it through blur, the cell leaving the rendered range through the
   * range check in the constructor.
   */
  private onViewportScroll(): void {
    this.pointerKey.set(null);
  }

  /** Whether the cell for this key is among the rows the viewport renders. */
  private isRendered(key: string): boolean {
    const index = this.sortedEmotes().findIndex((emote) => emote.sevenTvEmoteId === key);
    if (index < 0) {
      return false;
    }
    const rowIndex = Math.floor(index / this.columns());
    const { start, end } = this.renderedRange();
    return rowIndex >= start && rowIndex < end;
  }

  /** The score as it is announced: the tile's own text, except that the typographic dash it uses
   *  for "no score" becomes a word — a screen reader says nothing at all for the dash. */
  private spokenScore(emote: ForeignEmoteRow): string {
    const value = emote[this.effectiveSortMode() as Exclude<ForeignEmoteSortMode, 'none'>];
    return value === null || value === undefined
      ? this.transloco.translate('import.foreignChannel.sort.noScore')
      : this.scoreBadge(emote);
  }
}
