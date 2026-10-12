import { Component, computed, input, model } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

export interface SegmentedControlOption {
  value: string;
  labelKey: string;
  /**
   * Optional small trailing figure (a count). Shown smaller and hidden from assistive tech; the
   * accessible name gets `detailLabel` instead (already translated, falling back to `detail`), so
   * a placeholder like "–" can be read as "unknown" rather than as a dash.
   */
  detail?: string;
  detailLabel?: string;
}

/**
 * Single-select segmented control for a small set of mutually exclusive options (time-range
 * presets etc.). Radiogroup semantics with a roving tabindex: the group is one tab stop,
 * arrow keys move and select within it.
 */
@Component({
  selector: 'app-segmented-control',
  imports: [TranslocoPipe],
  // 'lg' stretches to its container, which needs a block-level host to stretch inside. Bound
  // conditionally rather than set unconditionally so that every existing 'sm' call site — all of
  // them inline in a flex row — keeps the inline host it was laid out against.
  host: { '[class.block]': "size() === 'lg'" },
  template: `
    <div role="radiogroup" [attr.aria-label]="ariaLabel()" [class]="groupClass()">
      <!-- Separators come from the container background showing through the 1px gaps, not from
           per-button borders: with flex-wrap (long option sets on narrow screens) that draws the
           dividers between rows too, which a border-l on each button cannot.

           The trick depends on the carrier contrasting with the segments, and the direction of that
           contrast flips between modes: inset-hover is LIGHTER than inset in dark and DARKER than it
           in light. Naming the two roles instead of two greys is what makes the flip automatic —
           hardcoded slate-700-over-slate-800 would have inverted into an invisible divider. -->
      @for (option of options(); track option.value; let index = $index) {
        <button
          type="button"
          role="radio"
          [attr.aria-checked]="value() === option.value"
          [tabindex]="tabIndexFor(option)"
          [class]="
            segmentClass() +
            (value() === option.value
              ? 'bg-accent-selected font-medium text-on-accent'
              : 'bg-surface-inset text-fg-secondary hover:bg-surface-inset-hover')
          "
          (click)="value.set(option.value)"
          (keydown)="onKeydown($event, index)"
        >
          {{ option.labelKey | transloco }}
          @if (option.detail; as detail) {
            <span aria-hidden="true" class="ml-1 text-xs font-normal tabular-nums">{{
              detail
            }}</span>
            <span class="sr-only">{{ option.detailLabel ?? detail }}</span>
          }
        </button>
      }
    </div>
  `,
})
export class SegmentedControl {
  readonly options = input.required<SegmentedControlOption[]>();
  readonly ariaLabel = input('');
  /**
   * 'lg' is for a control that is the row rather than a chip in one — currently only the account
   * menu's panel. It lets the group fill its container and follows the house rule for menu rows
   * (§7.1): a 44 px thumb target on narrow viewports, 36 px from `sm` up, where a mouse does not
   * need the comfort margin and the extra height only makes a set-once control the loudest thing
   * in the panel. 'sm' is every other call site and is unchanged by this input existing.
   */
  readonly size = input<'sm' | 'lg'>('sm');
  readonly value = model.required<string>();

  protected readonly groupClass = computed(
    () =>
      (this.size() === 'lg' ? 'flex w-full ' : 'inline-flex ') +
      'flex-wrap gap-px overflow-hidden rounded-md border border-surface-inset-hover bg-surface-inset-hover',
  );

  protected readonly segmentClass = computed(
    () =>
      'grow px-3 py-1.5 text-sm whitespace-nowrap transition ' +
      (this.size() === 'lg' ? 'min-h-11 sm:min-h-9 ' : ''),
  );

  protected tabIndexFor(option: SegmentedControlOption): number {
    const options = this.options();
    const selectedExists = options.some((candidate) => candidate.value === this.value());
    const isTabStop = selectedExists ? option.value === this.value() : option === options[0];
    return isTabStop ? 0 : -1;
  }

  protected onKeydown(event: KeyboardEvent, index: number): void {
    let delta: number;
    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        delta = 1;
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        delta = -1;
        break;
      default:
        return;
    }
    event.preventDefault();
    const options = this.options();
    const next = (index + delta + options.length) % options.length;
    this.value.set(options[next].value);
    const group = (event.currentTarget as HTMLElement).closest('[role="radiogroup"]');
    group?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[next]?.focus();
  }
}
