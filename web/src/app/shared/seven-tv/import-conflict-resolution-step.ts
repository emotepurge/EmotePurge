import { CdkVirtualScrollViewport, ScrollingModule } from '@angular/cdk/scrolling';
import {
  Component,
  ElementRef,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { EmoteSprite } from '../emotes/emote-sprite';
import { ResolutionDecisions, RowDecision, Violation, ViolationRule } from './conflict-resolution';
import { AliasMismatchRow, NameCollisionRow, TargetOverlay } from './import-preview';

/** Which of the confirm dialog's two conflict groups one opening of this step shows. */
export type ConflictGroup = 'nameCollision' | 'aliasMismatch';

/** One action a row offers, and why it cannot be chosen there (`null` when it can). A disabled
 *  action is still listed — the user sees that it exists and why it is missing here (spec 8.6's
 *  idiom for a set that cannot be picked). */
export interface ResolutionActionOption {
  kind: RowDecision['kind'];
  disabledReasonKey: string | null;
}

/** One row of the step, already shaped for display — see {@link collisionStepRows} and
 *  {@link mismatchStepRows} for how each conflict kind maps onto it. */
export interface ConflictStepRow {
  /** The source row's `sevenTvEmoteId` — the key the decision map uses. */
  key: string;
  sourceName: string;
  /** `null` draws the empty sprite plate, never a URL derived from the id (AK 4). */
  sourceImageUrl: string | null;
  targetImageUrl: string | null;
  /** Every named alias the target entry holds — two for a #74 duplicate. Empty when gone. */
  targetAliases: string[];
  targetHasAliaslessEntry: boolean;
  /** The target entry is no longer in the set, as the last live read saw it. */
  targetGone: boolean;
  options: ResolutionActionOption[];
}

/** One sentence of the apply button's lock reason: a rule and the source names it involves. */
export interface ViolationMessage {
  key: string;
  rows: string;
}

/** Row heights in px: the wide layout puts source, target and actions side by side, the narrow one
 *  stacks them (source over target over actions). Measured in a real browser (Chromium,
 *  `[data-resolve-index] > div` scrollHeight, plus the row's own 16px vertical padding — `py-2`,
 *  border-box) rather than derived from the CSS, because a virtualized list needs one fixed height
 *  per row and every row, however short its own content, pays for the tallest one any row can
 *  reach.
 *
 *  Re-measured for issue #268's worst-case row (aliasless target entry, untracked-target replace
 *  reason, and a checked rename whose typed alias collides — `fieldError.taken`, the longest field
 *  error in either locale): 255px narrow (German) / 231px English, 126px wide. Both constants moved
 *  to that worst case plus an ~8-16px cross-browser margin.
 *
 *  Re-measured again once the CDK content-wrapper shrink-to-fit bug (fixed in styles.css) stopped
 *  masking a ~50-char unbreakable name/alias: with wrapping now reaching the disabled-reason text,
 *  the worst-case narrow row measures 293px content at the 360px floor AK 22 pins (was 255px); wide
 *  is unaffected (its own longest row already wraps below `NARROW_BELOW_PX`) and stays at 110px.
 *  Narrow moved to 320 (293px + 16px padding + ~11px margin); wide to 136 (110px + margin). */
const ROW_WIDE_PX = 136;
const ROW_NARROW_PX = 320;
/** Content width below which the step switches to the stacked layout. Chosen so that, above it,
 *  the actions column still fits the widest row — three radio options, a bracketed disabled
 *  reason, the rename field and its error line — on one line each, comfortably inside
 *  {@link ROW_WIDE_PX}. Measured in a real browser: below 748px content width the three radio
 *  options wrap onto a second line and the row's content grows from 86px to 110px; 720px (the
 *  previous threshold) sat inside that gap and let exactly this row overflow its fixed height. */
const NARROW_BELOW_PX = 760;
const SPRITE_PX = 40;

const OPTION_LABEL_KEYS: Record<RowDecision['kind'], string> = {
  skip: 'import.resolve.action.skip',
  renameSource: 'import.resolve.action.renameSource',
  replaceTarget: 'import.resolve.action.replaceTarget',
  adoptSourceName: 'import.resolve.action.adoptSourceName',
};

const VIOLATION_KEYS: Record<ViolationRule, string> = {
  duplicateGeneratedAlias: 'import.resolve.violation.duplicateGeneratedAlias',
  aliasHeldByTarget: 'import.resolve.violation.aliasHeldByTarget',
  invalidTypedAlias: 'import.resolve.violation.invalidTypedAlias',
  duplicateReplaceTarget: 'import.resolve.violation.duplicateReplaceTarget',
  targetTouchedByReplaceAndAdopt: 'import.resolve.violation.targetTouchedByReplaceAndAdopt',
  adoptBlocked: 'import.resolve.violation.adoptBlocked',
  replaceNeedsTrackedTarget: 'import.resolve.violation.replaceNeedsTrackedTarget',
};

/** The field error a rename row shows under its text field, most specific first. */
const FIELD_ERROR_KEYS: [ViolationRule, string][] = [
  ['invalidTypedAlias', 'import.resolve.fieldError.invalid'],
  ['aliasHeldByTarget', 'import.resolve.fieldError.taken'],
  ['duplicateGeneratedAlias', 'import.resolve.fieldError.duplicate'],
];

/** The label key for each way a chosen action visibly changes a row, keyed by
 *  {@link RowConsequence}'s `kind`. */
const CONSEQUENCE_KEYS: Record<RowConsequence['kind'], string> = {
  removed: 'import.resolve.consequence.removed',
  becomes: 'import.resolve.consequence.becomes',
  addedAs: 'import.resolve.consequence.addedAs',
};

/**
 * What a chosen action visibly does to one side of a row — `null` for `skip`, which changes
 * nothing. A decision only ever affects one side (issue #268's AK 2): `replaceTarget` and
 * `adoptSourceName` describe the target, `renameSource` the source, through its currently typed
 * alias — `null` while that field is empty or while it carries a field error (issue #269: an
 * invalid, taken or duplicated alias is not going to be added under that name, so the line
 * shows nothing rather than a promise the run cannot keep), so the line shows nothing rather than
 * an empty quote. Pure and independent of the row's other fields on purpose: the whole point is
 * that it is derived from the decision alone, so the rename preview follows every keystroke
 * without any state of its own to fall out of sync (rule 14).
 */
export interface RowConsequence {
  side: 'source' | 'target';
  kind: 'removed' | 'becomes' | 'addedAs';
  /** The name the consequence names — the source name for `becomes`, the typed alias for
   *  `addedAs`, unused (`''`) for `removed`. */
  name: string;
}

export function rowConsequence(
  decision: RowDecision,
  sourceName: string,
  hasFieldError = false,
): RowConsequence | null {
  switch (decision.kind) {
    case 'replaceTarget':
      return { side: 'target', kind: 'removed', name: '' };
    case 'adoptSourceName':
      return { side: 'target', kind: 'becomes', name: sourceName };
    case 'renameSource':
      return decision.alias === '' || hasFieldError
        ? null
        : { side: 'source', kind: 'addedAs', name: decision.alias };
    case 'skip':
      return null;
  }
}

/**
 * Step rows for the name-collision group: skip, rename and replace (AK 6). Replace stays listed but
 * disabled for an untracked target (a deletion there has no way back yet) and for a row whose
 * target drifted away from the name it collides on — the live counterpart no longer holds it, so a
 * replace would remove an entry the collision is not about. `overlays` carries the live counterpart
 * of every replace row whose target drifted, keyed by the row's source id.
 */
export function collisionStepRows(
  rows: readonly NameCollisionRow[],
  targetIsTracked: boolean,
  overlays: ReadonlyMap<string, TargetOverlay>,
): ConflictStepRow[] {
  return rows.map((collision) => {
    const key = collision.row.sevenTvEmoteId;
    const overlaid = overlays.has(key);
    const live = overlays.get(key) ?? null;
    const targetGone = overlaid && live === null;
    const targetAliases = overlaid ? (live?.aliases ?? []) : collision.targetAliases;
    const replaceReason = !targetIsTracked
      ? 'import.resolve.replaceNeedsTracked'
      : !targetAliases.includes(collision.row.name)
        ? 'import.resolve.reloadTargetFirst'
        : null;
    return {
      key,
      sourceName: collision.row.name,
      sourceImageUrl: collision.row.imageUrl,
      targetImageUrl: targetGone ? null : collision.target.imageUrl,
      targetAliases,
      targetHasAliaslessEntry: overlaid
        ? (live?.hasAliaslessEntry ?? false)
        : collision.targetHasAliaslessEntry,
      targetGone,
      options: [
        { kind: 'skip', disabledReasonKey: null },
        { kind: 'renameSource', disabledReasonKey: null },
        { kind: 'replaceTarget', disabledReasonKey: replaceReason },
      ],
    };
  });
}

/**
 * Step rows for the alias-mismatch group: skip and adopt (AK 6). Adopt stays listed but disabled
 * with its reason where `adoptBlocked` says it cannot run (AK 8). The target side is the same emote
 * under its target alias, so its picture comes from the target list (`targetImageUrlById`), falling
 * back to the source's own.
 */
export function mismatchStepRows(
  rows: readonly AliasMismatchRow[],
  targetImageUrlById: ReadonlyMap<string, string>,
): ConflictStepRow[] {
  return rows.map((mismatch) => ({
    key: mismatch.row.sevenTvEmoteId,
    sourceName: mismatch.row.name,
    sourceImageUrl: mismatch.row.imageUrl,
    targetImageUrl: targetImageUrlById.get(mismatch.row.sevenTvEmoteId) ?? mismatch.row.imageUrl,
    targetAliases: mismatch.targetAliases,
    targetHasAliaslessEntry: false,
    targetGone: false,
    options: [
      { kind: 'skip', disabledReasonKey: null },
      {
        kind: 'adoptSourceName',
        disabledReasonKey:
          mismatch.adoptBlocked === null
            ? null
            : `import.resolve.adoptBlocked.${mismatch.adoptBlocked}`,
      },
    ],
  }));
}

/**
 * The apply button's lock reason, one sentence per violation, each naming its rows by source name
 * (AK 10, 11). `nameOf` resolves any source id of the run — a violation can name a row of the other
 * conflict group, or an untouched row that shares a generated name. Deduplicated per rule, so two
 * violations of the same rule read as one sentence.
 */
export function violationMessages(
  violations: readonly Violation[],
  nameOf: (key: string) => string,
): ViolationMessage[] {
  const byRule = new Map<ViolationRule, string[]>();
  for (const violation of violations) {
    const names = byRule.get(violation.rule) ?? [];
    for (const key of violation.rowKeys) {
      const name = nameOf(key);
      if (!names.includes(name)) {
        names.push(name);
      }
    }
    byRule.set(violation.rule, names);
  }
  return [...byRule].map(([rule, names]) => ({
    key: VIOLATION_KEYS[rule],
    rows: names.join(', '),
  }));
}

/**
 * The per-row resolution table of the import confirm dialog — the second step of the same dialog,
 * never an overlay of its own. Shows one conflict group per opening: the source emote on the left,
 * its counterpart in the target set on the right, and a radio group of actions per row.
 *
 * Owns no decisions: it renders the ones it is handed and reports every change through
 * {@link decide}; the dialog keeps them until it closes, and validates them over the whole run.
 *
 * Virtualized with a fixed row height, and the viewport is the only scroll container (same `dvh`
 * sizing as `foreign-emote-grid.ts`) — a group of 200 rows renders only the buffer, so it neither
 * lays out nor loads 200 pairs of pictures. The consequence for the keyboard: rows outside the
 * buffer are not in the DOM, and Tab alone never reaches them. The rows therefore carry a roving
 * tabindex on the row containers only — arrow up/down and Home/End move between rows, scrolling the
 * target row into the viewport before focus moves. The controls inside the rows keep their natural
 * tab stops, so Tab walks through each rendered row's radio group and rename field and on into the
 * next rendered row; it just never gets past the buffer.
 *
 * No bottom-sheet variant and no coarse-pointer branch: every 7TV write path is hidden on a coarse
 * pointer, so this step is only ever reached with a mouse. A narrow desktop window gets the stacked
 * layout instead.
 */
@Component({
  selector: 'app-import-conflict-resolution-step',
  imports: [EmoteSprite, ScrollingModule, TranslocoPipe],
  template: `
    <div
      #container
      role="group"
      [attr.aria-label]="
        (group() === 'nameCollision'
          ? 'import.resolve.listLabelCollisions'
          : 'import.resolve.listLabelMismatches'
        ) | transloco
      "
    >
      <!-- Column headers for the wide layout only — the stacked layout captions each cell instead
           (below), and every row already names both sides itself (rowLabel), so this row is
           decorative and hidden from assistive tech. Sits above the viewport, not inside it: it
           must never scroll with the rows. Same grid columns as a row, so headers and cells line
           up; the arrow is markup here, not translated text, and appears nowhere else. -->
      @if (!narrow()) {
        <div
          aria-hidden="true"
          class="grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,2fr)] gap-3 px-1 pb-1 text-[11px] font-semibold uppercase tracking-[0.13em] text-fg-muted"
        >
          <span>{{ 'import.resolve.header.source' | transloco }}</span>
          <span class="flex min-w-0 items-center gap-1">
            <span class="text-sm font-normal text-fg-muted">→</span>
            <span
              class="min-w-0 truncate"
              [attr.title]="targetHeaderKey() | transloco: { set: targetSetName() ?? '' }"
            >
              {{ targetHeaderKey() | transloco: { set: targetSetName() ?? '' } }}
            </span>
          </span>
          <span>{{ 'import.resolve.header.action' | transloco }}</span>
        </div>
      }
      <!-- Buffers in rows, not the CDK's 100/200 px default, which is less than one tall row.
           data-resolve-viewport: styles.css pins this viewport's CDK content wrapper to a definite
           width instead of CDK's own shrink-to-fit default — see that rule's own comment for why
           (issue #268 follow-up: an unbreakable long name or typed alias could otherwise widen
           every row past the panel, clipping instead of truncating/wrapping). -->
      <cdk-virtual-scroll-viewport
        data-resolve-viewport
        [itemSize]="rowPx()"
        [minBufferPx]="rowPx() * 2"
        [maxBufferPx]="rowPx() * 4"
        [style.height]="viewportHeight()"
      >
        <div
          *cdkVirtualFor="let row of rows(); let index = index; trackBy: trackRow"
          class="flex items-center overflow-hidden border-b border-border px-1 py-2"
          role="group"
          [attr.aria-label]="
            'import.resolve.rowLabel'
              | transloco
                : {
                    source: row.sourceName,
                    target: row.targetGone
                      ? ('import.resolve.targetGone' | transloco)
                      : row.targetAliases.join(', '),
                  }
          "
          [attr.data-resolve-index]="index"
          [tabindex]="index === activeIndex() ? 0 : -1"
          [style.height.px]="rowPx()"
          (focusin)="activeIndex.set(index)"
          (keydown)="onRowKeydown($event, index)"
        >
          <div
            [class]="
              'w-full ' +
              (narrow()
                ? 'flex flex-col gap-1'
                : 'grid grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,2fr)] items-center gap-3')
            "
          >
            <div class="flex min-w-0 items-center gap-2">
              <span class="app-sprite-cell relative block h-10 w-10 shrink-0">
                @if (row.sourceImageUrl; as url) {
                  <app-emote-sprite [url]="url" [size]="spritePx" />
                }
              </span>
              <span class="flex min-w-0 flex-col">
                @if (narrow()) {
                  <span
                    aria-hidden="true"
                    class="text-[11px] font-semibold uppercase tracking-[0.13em] text-fg-muted"
                  >
                    {{ 'import.resolve.header.source' | transloco }}
                  </span>
                }
                <span class="truncate text-sm text-fg">{{ row.sourceName }}</span>
                <!-- Reserved even when empty (issue #268 AK 3): the row's fixed height must not
                     change with the chosen action, on either side. min-h-[1lh] pins one line's
                     worth of height regardless of content — a blockified, content-less span has
                     no line box of its own to derive it from. -->
                <span
                  [id]="'resolve-consequence-' + row.key + '-source'"
                  class="block min-h-[1lh] truncate text-xs text-fg-muted"
                >
                  @if (sourceConsequenceOf(row); as consequence) {
                    {{
                      consequenceLabelKeys[consequence.kind] | transloco: { name: consequence.name }
                    }}
                  }
                </span>
              </span>
            </div>

            <div class="flex min-w-0 items-center gap-2">
              <span
                class="app-sprite-cell relative block h-10 w-10 shrink-0"
                [class.opacity-50]="targetConsequenceOf(row)?.kind === 'removed'"
              >
                @if (row.targetImageUrl; as url) {
                  <app-emote-sprite [url]="url" [size]="spritePx" />
                }
              </span>
              <span class="flex min-w-0 flex-col">
                @if (narrow()) {
                  <span
                    aria-hidden="true"
                    class="block min-w-0 truncate text-[11px] font-semibold uppercase tracking-[0.13em] text-fg-muted"
                    [attr.title]="targetHeaderKey() | transloco: { set: targetSetName() ?? '' }"
                  >
                    {{ targetHeaderKey() | transloco: { set: targetSetName() ?? '' } }}
                  </span>
                }
                @if (row.targetGone) {
                  <span class="truncate text-sm text-fg-muted">
                    {{ 'import.resolve.targetGone' | transloco }}
                  </span>
                } @else {
                  <span
                    class="truncate text-sm"
                    [class.line-through]="targetConsequenceOf(row)?.kind === 'removed'"
                    [class.text-fg-muted]="targetConsequenceOf(row)?.kind === 'removed'"
                    [class.text-fg-secondary]="targetConsequenceOf(row)?.kind !== 'removed'"
                  >
                    {{ row.targetAliases.join(' · ') }}
                  </span>
                  @if (row.targetHasAliaslessEntry) {
                    <!-- A replace's REMOVE takes every entry of the target id, so this aliasless
                         entry is struck through together with the named alias above it, not just
                         the one that happens to carry the collision's name. -->
                    <span
                      class="truncate text-xs text-fg-muted"
                      [class.line-through]="targetConsequenceOf(row)?.kind === 'removed'"
                    >
                      {{ 'import.resolve.aliaslessEntry' | transloco }}
                    </span>
                  }
                }
                <!-- Reserved even when empty — see the source cell's own consequence line above. -->
                <span
                  [id]="'resolve-consequence-' + row.key + '-target'"
                  class="block min-h-[1lh] truncate text-xs"
                  [class.text-danger-fg]="targetConsequenceOf(row)?.kind === 'removed'"
                  [class.text-fg-muted]="targetConsequenceOf(row)?.kind !== 'removed'"
                >
                  @if (targetConsequenceOf(row); as consequence) {
                    {{
                      consequenceLabelKeys[consequence.kind] | transloco: { name: consequence.name }
                    }}
                  }
                </span>
              </span>
            </div>

            <div class="flex min-w-0 flex-col gap-1">
              <div
                class="flex flex-wrap gap-x-4 text-sm text-fg-secondary"
                role="radiogroup"
                [attr.aria-label]="
                  'import.resolve.actionGroupLabel' | transloco: { sourceName: row.sourceName }
                "
                [attr.aria-describedby]="
                  'resolve-consequence-' +
                  row.key +
                  '-source resolve-consequence-' +
                  row.key +
                  '-target'
                "
              >
                @for (option of row.options; track option.kind) {
                  <!-- flex-wrap + min-w-0 (issue #268 follow-up): a bracketed disabled reason has
                       no unbreakable run of its own, but this label's nowrap flex still fed its
                       full one-line width into the row's min-content, which the CDK content
                       wrapper's shrink-to-fit picks up as its own ceiling — widening every row and
                       clipping every truncate span. Wrapping the reason and letting the label
                       shrink keeps its min-content down to its widest single word. -->
                  <label
                    class="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-0.5 py-0.5"
                    [class.opacity-60]="option.disabledReasonKey !== null"
                  >
                    <input
                      type="radio"
                      class="h-4 w-4 accent-accent-solid"
                      [name]="'resolve-action-' + row.key"
                      [value]="option.kind"
                      [disabled]="option.disabledReasonKey !== null"
                      [checked]="decisionOf(row.key).kind === option.kind"
                      (change)="choose(row, option.kind)"
                    />
                    {{ optionLabelKeys[option.kind] | transloco }}
                    @if (option.disabledReasonKey; as reasonKey) {
                      <span class="text-xs text-fg-muted break-words"
                        >({{ reasonKey | transloco }})</span
                      >
                    }
                  </label>
                }
              </div>

              @if (renameAlias(row.key); as alias) {
                <div class="flex min-w-0 items-center gap-2">
                  <label class="shrink-0 text-xs text-fg-muted" [for]="'resolve-alias-' + row.key">
                    {{ 'import.resolve.renameLabel' | transloco }}
                  </label>
                  <input
                    type="text"
                    class="app-input-sm min-w-0 w-full flex-1"
                    [id]="'resolve-alias-' + row.key"
                    [value]="alias.value"
                    [attr.aria-invalid]="fieldErrorKey(row.key) !== null ? 'true' : null"
                    [attr.aria-describedby]="
                      fieldErrorKey(row.key) !== null ? 'resolve-alias-' + row.key + '-error' : null
                    "
                    (input)="rename(row.key, $event)"
                  />
                </div>
                @if (fieldErrorKey(row.key); as errorKey) {
                  <p
                    [id]="'resolve-alias-' + row.key + '-error'"
                    class="text-sm text-danger-fg break-words"
                  >
                    {{ errorKey | transloco }}
                  </p>
                }
              }
            </div>
          </div>
        </div>
      </cdk-virtual-scroll-viewport>
    </div>
  `,
})
export class ImportConflictResolutionStep {
  readonly group = input.required<ConflictGroup>();
  readonly rows = input.required<ConflictStepRow[]>();
  /** The decisions to show — every row absent from the map reads as `skip` (AK 5). */
  readonly decisions = input.required<ResolutionDecisions>();
  /** The whole run's validation, for the rename rows' own field errors. */
  readonly violations = input<Violation[]>([]);
  /** Room the dialog's own chrome above and below this step takes, in rem, on top of the base
   *  allowance — same contract as `ForeignEmoteGrid.reservedRem`. */
  readonly reservedRem = input(0);
  /** The target set's resolved display name (`targetSetLabel`, the same source the dialog's own
   *  title reads) — `null` while the target has not loaded yet. Drives the "Ziel · {set}" header
   *  and narrow-layout caption; `null` or `''` falls back to a plain "Ziel" (issue #268). */
  readonly targetSetName = input<string | null>(null);

  readonly decide = output<{ key: string; decision: RowDecision }>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected readonly spritePx = SPRITE_PX;
  protected readonly optionLabelKeys = OPTION_LABEL_KEYS;
  protected readonly consequenceLabelKeys = CONSEQUENCE_KEYS;

  private readonly viewport = viewChild(CdkVirtualScrollViewport);
  private readonly container = viewChild<ElementRef<HTMLElement>>('container');
  private readonly containerWidth = signal(0);
  /** The alias last typed per row, so switching a row away from rename and back keeps it. */
  private readonly typedAliases = new Map<string, string>();

  protected readonly activeIndex = signal(0);
  protected readonly narrow = computed(() => this.containerWidth() < NARROW_BELOW_PX);
  protected readonly rowPx = computed(() => (this.narrow() ? ROW_NARROW_PX : ROW_WIDE_PX));

  /** Which "Ziel …" label to show — with the set name once it is known, otherwise the plain form.
   *  Read by both the wide header row and the narrow layout's per-cell caption, so the two always
   *  agree. */
  protected readonly targetHeaderKey = computed(() =>
    this.targetSetName() ? 'import.resolve.header.target' : 'import.resolve.header.targetPlain',
  );

  /** See `foreign-emote-grid.ts`'s `viewportHeight` for why this is measured against `dvh`: the
   *  pane would otherwise scroll too, and two nested scrollbars over one list is the known defect. */
  protected readonly viewportHeight = computed(
    () => `min(34rem, max(8rem, calc(100dvh - ${24 + this.reservedRem()}rem)))`,
  );

  /** Field error per rename row, from the rules that concern a typed alias. */
  private readonly fieldErrors = computed(() => {
    const errors = new Map<string, string>();
    for (const [rule, key] of FIELD_ERROR_KEYS) {
      for (const violation of this.violations()) {
        if (violation.rule !== rule) {
          continue;
        }
        for (const rowKey of violation.rowKeys) {
          if (!errors.has(rowKey) && this.decisions().get(rowKey)?.kind === 'renameSource') {
            errors.set(rowKey, key);
          }
        }
      }
    }
    return errors;
  });

  constructor() {
    // Same measurement as the foreign emote grid: the layout follows the element that holds the
    // rows, not the window.
    effect((onCleanup) => {
      const element = this.container()?.nativeElement;
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

    // A shorter list must not leave the tab stop past its end, or the rows become unreachable.
    effect(() => {
      if (this.activeIndex() >= this.rows().length) {
        this.activeIndex.set(0);
      }
    });

    // The step replaces the first step's content, whose button had focus — hand focus to the table.
    afterNextRender(() => this.focusRow(0));
  }

  protected trackRow(_index: number, row: ConflictStepRow): string {
    return row.key;
  }

  protected decisionOf(key: string): RowDecision {
    return this.decisions().get(key) ?? { kind: 'skip' };
  }

  /** The rename field's value, wrapped so `@if` also renders an empty field. */
  protected renameAlias(key: string): { value: string } | null {
    const decision = this.decisionOf(key);
    return decision.kind === 'renameSource' ? { value: decision.alias } : null;
  }

  protected fieldErrorKey(key: string): string | null {
    return this.fieldErrors().get(key) ?? null;
  }

  /** The row's consequence, if it names the source side — reads `decisionOf`, which reads the
   *  `decisions` input signal, so a typed alias re-renders this on every keystroke without any
   *  state of its own (rule 14). Reuses `fieldErrorKey` rather than re-checking the violations
   *  itself, so a rename with a field error (invalid, taken, duplicated) never promises a name
   *  7TV would reject (issue #269). */
  protected sourceConsequenceOf(row: ConflictStepRow): RowConsequence | null {
    const consequence = rowConsequence(
      this.decisionOf(row.key),
      row.sourceName,
      this.fieldErrorKey(row.key) !== null,
    );
    return consequence?.side === 'source' ? consequence : null;
  }

  /** The row's consequence, if it names the target side — see {@link sourceConsequenceOf}. */
  protected targetConsequenceOf(row: ConflictStepRow): RowConsequence | null {
    const consequence = rowConsequence(
      this.decisionOf(row.key),
      row.sourceName,
      this.fieldErrorKey(row.key) !== null,
    );
    return consequence?.side === 'target' ? consequence : null;
  }

  protected choose(row: ConflictStepRow, kind: RowDecision['kind']): void {
    this.decide.emit({ key: row.key, decision: this.decisionFor(row, kind) });
  }

  protected rename(key: string, event: Event): void {
    const alias = (event.target as HTMLInputElement).value;
    this.typedAliases.set(key, alias);
    this.decide.emit({ key, decision: { kind: 'renameSource', alias } });
  }

  protected onRowKeydown(event: KeyboardEvent, index: number): void {
    // Only the row itself navigates: arrow keys inside the radio group already move between its
    // options, and inside the text field they move the caret.
    if (event.target !== event.currentTarget) {
      return;
    }
    const last = this.rows().length - 1;
    const next =
      event.key === 'ArrowDown'
        ? Math.min(index + 1, last)
        : event.key === 'ArrowUp'
          ? Math.max(index - 1, 0)
          : event.key === 'Home'
            ? 0
            : event.key === 'End'
              ? last
              : null;
    if (next === null) {
      return;
    }
    event.preventDefault();
    this.focusRow(next);
  }

  private decisionFor(row: ConflictStepRow, kind: RowDecision['kind']): RowDecision {
    switch (kind) {
      case 'renameSource':
        return { kind, alias: this.typedAliases.get(row.key) ?? row.sourceName };
      case 'skip':
      case 'replaceTarget':
      case 'adoptSourceName':
        return { kind };
    }
  }

  /**
   * Moves focus onto a row the viewport may not have rendered. A rendered row is focused directly
   * (the browser scrolls it into view); one past the buffer is scrolled to first and focused once
   * the viewport has rendered it — same approach as the usage atlas's `focusCell`.
   */
  private focusRow(index: number): void {
    if (index < 0 || index >= this.rows().length) {
      return;
    }
    this.activeIndex.set(index);
    const find = () =>
      this.host.nativeElement.querySelector<HTMLElement>(`[data-resolve-index="${index}"]`);
    const rendered = find();
    if (rendered) {
      rendered.focus();
      return;
    }
    this.viewport()?.scrollToIndex(index);
    requestAnimationFrame(() => find()?.focus());
  }
}
