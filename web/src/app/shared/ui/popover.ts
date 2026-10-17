import {
  Component,
  DestroyRef,
  ElementRef,
  Renderer2,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';

/**
 * Marker attribute the host puts on the `position: relative` wrapper that contains both the trigger
 * and the popover. Clicks inside that wrapper never count as outside-clicks, which is what keeps the
 * very click that opened the panel from closing it again in the same dispatch.
 */
export const POPOVER_ANCHOR_ATTRIBUTE = 'data-popover-anchor';

/** Gap kept between a panel and the viewport edge when it has to be pulled back inside. */
export const POPOVER_VIEWPORT_MARGIN = 16;

/**
 * Horizontal shift (px) that brings a panel spanning `left`..`right` inside `margin`..`viewportWidth - margin`.
 * Left wins when the panel is wider than the room, so the start of the text is never the part cut off.
 */
export function viewportShift(
  left: number,
  right: number,
  viewportWidth: number,
  margin: number = POPOVER_VIEWPORT_MARGIN,
): number {
  const overflowRight = right - (viewportWidth - margin);
  const shifted = overflowRight > 0 ? -overflowRight : 0;
  const underflowLeft = margin - (left + shifted);
  return underflowLeft > 0 ? shifted + underflowLeft : shifted;
}

/**
 * Non-modal dropdown panel. The one place the "relative wrapper + absolute panel + document-click +
 * Escape" construction lives — it had been hand-rolled three times (custom-range popover, datetime
 * picker, shell disclosure) with three slightly different dismiss rules and only two of the three
 * guarding against overflowing a 360 px viewport.
 *
 * Deliberately not a CDK overlay: these panels open out of a sticky filter toolbar and must inherit
 * its stacking context (design doc §8.5), which a container appended to `<body>` cannot. Modal
 * surfaces stay `@angular/cdk/dialog`'s job (§7) — this primitive is for the non-modal case and
 * carries no focus trap.
 *
 * Contract with the host: rendered = open. The panel never hides itself; it emits `closed` (outside
 * click, Escape) and the host owns the visibility signal.
 *
 * Escape closes the topmost layer only. An Escape whose focus sits inside the anchor (the trigger or
 * the panel) is consumed there — `stopPropagation()` on the anchor — so it never reaches the CDK's
 * overlay keyboard dispatcher on `<body>`: a popover opened inside a dialog or a bottom sheet closes
 * alone instead of taking the sheet with it. An Escape from anywhere else still closes the panel
 * through the document listener and travels on unchanged. The host also owns focus return — the
 * panel does not know which element opened it. Bring your own padding: the panel is padding-free so
 * full-bleed rows (menus) and padded forms can both live in it.
 */
@Component({
  selector: 'app-popover',
  host: {
    '(document:click)': 'onDocumentClick($event)',
    '(document:keydown.escape)': 'onEscape()',
  },
  template: `
    <div #panel role="dialog" [attr.aria-label]="ariaLabel()" [class]="panelClass()">
      <ng-content />
    </div>
  `,
})
export class Popover {
  readonly ariaLabel = input('');
  /** Which edge the panel lines up with its anchor — `end` for triggers near the right viewport edge. */
  readonly align = input<'start' | 'end'>('start');
  readonly width = input('w-64');

  readonly closed = output<void>();

  private readonly panel = viewChild.required<ElementRef<HTMLElement>>('panel');
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly renderer = inject(Renderer2);
  private readonly destroyRef = inject(DestroyRef);

  // z-30 inside the filter toolbar's z-20 sticky context — sanctioned by design doc §8.5 (dropdowns
  // opening out of a sticky bar inherit its context). max-w keeps the panel inside a 360 px viewport
  // whatever the anchor's position; without it a right-ish anchor pushes the page into horizontal
  // overflow, which the UI audit gates on.
  protected readonly panelClass = computed(
    () =>
      // shadow-overlay rather than Tailwind's shadow-xl: a black-based drop shadow is close to
      // invisible on a dark page and is the only thing lifting the panel off a light one, so the
      // two modes need different shadows, not a different opacity of the same one.
      // text-start is not decoration, it is a repair: `align` is a legacy HTML attribute, so
      // `<app-popover align="end">` leaves align="end" on the element and the UA stylesheet turns
      // that into text-align: end for the whole subtree. Every panel content inherited it; the
      // existing ones only escaped because their rows carry an explicit text-left. Cured here so
      // no future panel has to know, and so nobody has to guess why a caption drifted right.
      'absolute top-full z-30 mt-1 max-w-[calc(100vw-2rem)] overflow-hidden rounded-md border ' +
      'border-border-strong bg-surface text-start shadow-overlay ' +
      (this.align() === 'end' ? 'right-0 ' : 'left-0 ') +
      this.width(),
  );

  constructor() {
    // The anchor is only known once the panel sits in the DOM (an @if inserts it after creation).
    afterNextRender(() => {
      // The panel aligns to its anchor, and the anchor can sit anywhere once a toolbar wraps: an
      // end-aligned panel on a trigger at the left edge ran off-screen. Pull it back inside.
      const panel = this.panel().nativeElement;
      const rect = panel.getBoundingClientRect();
      const shift = viewportShift(rect.left, rect.right, document.documentElement.clientWidth);
      if (shift !== 0) {
        this.renderer.setStyle(panel, 'transform', `translateX(${shift}px)`);
      }
      const stop = this.renderer.listen(this.anchor(), 'keydown', (event: KeyboardEvent) => {
        if (event.key === 'Escape') {
          event.stopPropagation();
          this.closed.emit();
        }
      });
      this.destroyRef.onDestroy(stop);
    });
  }

  protected onDocumentClick(event: Event): void {
    if (!this.anchor().contains(event.target as Node)) {
      this.closed.emit();
    }
  }

  protected onEscape(): void {
    this.closed.emit();
  }

  private anchor(): HTMLElement {
    const host: HTMLElement = this.elementRef.nativeElement;
    return host.closest<HTMLElement>(`[${POPOVER_ANCHOR_ATTRIBUTE}]`) ?? host;
  }
}
