import { DOCUMENT } from '@angular/common';
import {
  Component,
  ElementRef,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { Button } from '../ui/button';
import { Popover } from '../ui/popover';
import { ACTION_KEYS, groupAuditActions } from './audit-actions';

let nextMenuId = 0;

/**
 * Single-choice action filter of the audit lists, as a popover menu instead of a segmented control:
 * a change refetches the list, and 39 segments wrap taller than a phone's viewport. Same pattern as
 * `EmoteSetMenu`: `role="radiogroup"`, roving tabindex, arrows move focus only, Enter/Space commit,
 * Escape closes and returns focus to the trigger. The options are grouped by action family
 * (`groupAuditActions`); the value `''` is the "all" choice above the groups.
 */
@Component({
  selector: 'app-audit-action-menu',
  imports: [Button, Popover, TranslocoPipe],
  template: `
    <div class="relative" data-popover-anchor>
      <button
        #trigger
        type="button"
        appButton="neutral"
        class="max-w-full truncate whitespace-nowrap"
        aria-haspopup="dialog"
        [attr.aria-expanded]="isOpen()"
        [attr.aria-label]="ariaLabel() + ': ' + (currentLabelKey() | transloco)"
        [attr.title]="ariaLabel()"
        (click)="toggle()"
      >
        {{ currentLabelKey() | transloco }}
        <span aria-hidden="true" class="ml-1 text-fg-muted">▾</span>
      </button>

      @if (isOpen()) {
        <app-popover [ariaLabel]="ariaLabel()" width="w-72" (closed)="close()">
          <div
            role="radiogroup"
            [attr.aria-label]="ariaLabel()"
            class="flex max-h-[min(60dvh,28rem)] flex-col gap-0.5 overflow-y-auto p-1"
          >
            @for (option of flatOptions(); track option.value; let index = $index) {
              @if (option.groupLabelKey !== null) {
                <div
                  [id]="menuId + '-group-' + option.groupId"
                  class="px-3 pt-2 pb-1 text-xs font-semibold text-fg-muted uppercase"
                >
                  {{ option.groupLabelKey | transloco }}
                </div>
              }
              <button
                type="button"
                role="radio"
                [attr.aria-checked]="option.value === value()"
                [attr.aria-describedby]="
                  option.groupId !== null ? menuId + '-group-' + option.groupId : null
                "
                [tabindex]="tabIndexFor(index)"
                [class]="
                  'flex min-h-11 items-center justify-between gap-3 rounded px-3 text-left text-sm transition sm:min-h-9 ' +
                  (option.value === value()
                    ? 'bg-accent-selected font-medium text-on-accent'
                    : 'text-fg-body hover:bg-surface-inset')
                "
                (click)="select(option.value)"
                (keydown)="onKeydown($event, index)"
              >
                <span class="min-w-0 break-words">{{ option.labelKey | transloco }}</span>
                @if (option.value === value()) {
                  <span aria-hidden="true">✓</span>
                }
              </button>
            }
          </div>
        </app-popover>
      }
    </div>
  `,
})
export class AuditActionMenu {
  /** The actions on offer, in any order — the menu groups them itself. */
  readonly actions = input.required<readonly string[]>();
  /** The selected action, `''` for "all". */
  readonly value = input.required<string>();
  /** Accessible name of the trigger and the radiogroup (the filter's own label). */
  readonly ariaLabel = input.required<string>();
  /** Translation key of the "all" choice. */
  readonly allLabelKey = input.required<string>();

  readonly valueChange = output<string>();

  private readonly document = inject(DOCUMENT);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  protected readonly menuId = `audit-action-menu-${nextMenuId++}`;
  protected readonly isOpen = signal(false);
  // Roving tabindex: null means "follow the selection".
  private readonly focusedIndex = signal<number | null>(null);

  /** One flat list so arrow keys and the roving index see a single radio sequence; the first option
   *  of each group carries the heading. */
  protected readonly flatOptions = computed(() => {
    const all = {
      value: '',
      labelKey: this.allLabelKey(),
      groupId: null as string | null,
      groupLabelKey: null as string | null,
    };
    const grouped = groupAuditActions(this.actions()).flatMap((group) =>
      group.actions.map((action, i) => ({
        value: action,
        labelKey: ACTION_KEYS[action],
        groupId: group.id as string | null,
        groupLabelKey: i === 0 ? group.labelKey : null,
      })),
    );
    return [all, ...grouped];
  });

  protected readonly currentLabelKey = computed(
    () => this.flatOptions().find((o) => o.value === this.value())?.labelKey ?? this.allLabelKey(),
  );

  protected toggle(): void {
    if (this.isOpen()) {
      this.close();
      return;
    }
    this.isOpen.set(true);
  }

  protected close(): void {
    if (!this.isOpen()) {
      return;
    }
    const hadFocus = this.elementRef.nativeElement.contains(this.document.activeElement);
    this.isOpen.set(false);
    this.focusedIndex.set(null);
    if (hadFocus) {
      this.trigger()?.nativeElement.focus();
    }
  }

  protected select(value: string): void {
    this.valueChange.emit(value);
    this.close();
  }

  protected tabIndexFor(index: number): number {
    return index === (this.focusedIndex() ?? this.selectedIndex()) ? 0 : -1;
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
    const count = this.flatOptions().length;
    event.preventDefault();
    // Focus only: committing here would refetch the list on every keypress.
    const next = (index + delta + count) % count;
    this.focusedIndex.set(next);
    const group = (event.currentTarget as HTMLElement).closest('[role="radiogroup"]');
    group?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[next]?.focus();
  }

  private selectedIndex(): number {
    const index = this.flatOptions().findIndex((o) => o.value === this.value());
    return index === -1 ? 0 : index;
  }
}
