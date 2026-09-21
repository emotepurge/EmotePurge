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

import { EmoteSetSummary } from '../../core/seven-tv/seven-tv-emote-set.model';
import { Button } from '../ui/button';
import { Popover } from '../ui/popover';

/**
 * The sets a dropdown built on {@link EmoteSetSummary} may actually offer (spec #200, 8.1, operator
 * decision 2026-09-21, spec addendum §35): every `kind !== 'NORMAL'` set — not only `PERSONAL`, the
 * only kind this account is ever likely to hold besides `NORMAL` — is **hidden entirely**, never
 * shown disabled. This overrides 8.1/AK 50's own wording ("`kind != NORMAL` deaktiviert mit
 * Beschriftung") for this one dropdown; it is the same idiom §34 already applied to the source- and
 * (pending its own follow-up) target-set pickers, for the same reason: a personal, global or special
 * set is not a plausible thing to view usage numbers for, and a disabled row next to it teaches
 * nothing a hidden row does not already say by omission.
 *
 * Exported and tested on its own (Regel 12: this is the one piece of real decision logic in this
 * file, everything else is template) rather than only indirectly through the component.
 */
export function selectableEmoteSets(sets: readonly EmoteSetSummary[]): EmoteSetSummary[] {
  return sets.filter((set) => set.kind === 'NORMAL');
}

/** Per-instance suffix for the lock reason's element id — an `aria-describedby` target has to be
 *  unique in the document. */
let nextLockReasonId = 0;

/**
 * Set-dropdown in the usage page's header, next to {@link DateRangeMenu} and built after the same
 * pattern (`Popover` + `role="radiogroup"`, roving tabindex, arrow keys move focus only — Enter/
 * Space commit, so walking the list with the keyboard does not fire a request per keypress).
 *
 * Purely presentational: it renders whatever {@link selectableEmoteSets} lets through and emits the
 * id the caller picked. Every decision about what that id then *means* — whether it is the active
 * set, whether the URL had a stale or hidden id, whether the set list could be read at all — is the
 * host page's (`usage-stats-page.ts`, spec 8.1's URL and fallback rules), which is also why this
 * component takes `loading`/`unavailable` as plain inputs rather than reading any resource itself.
 */
@Component({
  selector: 'app-emote-set-menu',
  imports: [Button, Popover, TranslocoPipe],
  template: `
    <div class="relative flex flex-wrap items-center gap-x-2" data-popover-anchor>
      <button
        #trigger
        type="button"
        appButton="neutral"
        class="max-w-48 truncate whitespace-nowrap disabled:cursor-not-allowed"
        aria-haspopup="dialog"
        [attr.aria-expanded]="isOpen()"
        [disabled]="triggerDisabled()"
        [attr.aria-describedby]="lockedReasonKey() !== null ? lockReasonId : null"
        [title]="unavailable() ? ('emoteSetMenu.unavailable' | transloco) : null"
        (click)="toggle()"
      >
        {{ 'emoteSetMenu.label' | transloco }}:
        @if (loading()) {
          {{ 'emoteSetMenu.loading' | transloco }}
        } @else if (unavailable()) {
          {{ 'emoteSetMenu.unavailable' | transloco }}
        } @else if (selectedSet(); as set) {
          {{ set.name }}
          @if (set.isActive) {
            <span class="text-fg-muted">({{ 'emoteSetMenu.active' | transloco }})</span>
          }
        }
        <span aria-hidden="true" class="ml-1 text-fg-muted">▾</span>
      </button>
      <!-- A lock the host imposes for a reason of its own (a delete run still writing) explains
           itself as text next to the trigger (docs/UI-Designsprache.md §10, "Disabled explains
           itself"). The list's own states need none: the trigger's label already says them. -->
      @if (lockedReasonKey(); as reasonKey) {
        <span [id]="lockReasonId" class="text-xs text-fg-muted">{{ reasonKey | transloco }}</span>
      }

      @if (isOpen()) {
        <app-popover [ariaLabel]="'emoteSetMenu.menuLabel' | transloco" (closed)="close()">
          <div
            role="radiogroup"
            [attr.aria-label]="'emoteSetMenu.menuLabel' | transloco"
            class="flex max-h-80 flex-col gap-0.5 overflow-y-auto p-1"
          >
            <!-- min-h-11 on touch, tighter from sm up (§10: 44 px comfort target for pointer-coarse
                 rows, 24 px is only the floor) — same rule as DateRangeMenu's own rows. -->
            @for (set of options(); track set.id; let index = $index) {
              <button
                type="button"
                role="radio"
                [attr.aria-checked]="set.id === selectedEmoteSetId()"
                [tabindex]="tabIndexFor(index)"
                [class]="
                  'flex min-h-11 items-center justify-between gap-3 rounded px-3 text-left text-sm transition sm:min-h-9 ' +
                  (set.id === selectedEmoteSetId()
                    ? 'bg-accent-selected font-medium text-on-accent'
                    : 'text-fg-body hover:bg-surface-inset')
                "
                (click)="select(set.id)"
                (keydown)="onKeydown($event, index)"
              >
                <span class="truncate">
                  {{ set.name }}
                  @if (set.isActive) {
                    <span
                      [class]="
                        set.id === selectedEmoteSetId() ? 'text-on-accent/80' : 'text-fg-muted'
                      "
                      >({{ 'emoteSetMenu.active' | transloco }})</span
                    >
                  }
                </span>
                @if (set.id === selectedEmoteSetId()) {
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
export class EmoteSetMenu {
  /** The channel's whole set list, unfiltered — filtering to what the radiogroup actually offers is
   *  this component's own job (see {@link selectableEmoteSets}). */
  readonly sets = input.required<readonly EmoteSetSummary[]>();
  /** The id the page currently shows — always a resolved id (the host already folds "follow the
   *  active set" and every fallback rule into this before passing it down), never the sentinel `''`
   *  the URL uses for "no explicit choice". */
  readonly selectedEmoteSetId = input.required<string | null>();
  /** The set list request for this channel has not answered yet — trigger reads as busy, disabled,
   *  no popover. */
  readonly loading = input(false);
  /** The set list could not be read (6.1 answered 503) — trigger locked with a reason, no popover,
   *  no background retry (spec 8.1: "kein Banner, kein Wiederholen im Hintergrund"). */
  readonly unavailable = input(false);
  /** Translation key of a reason the host locks the trigger for (the usage page: a delete run is
   *  still writing into the set on screen), or `null` — shown next to the trigger and wired to it via
   *  `aria-describedby`. A choice made in a popover that was already open when the lock landed is
   *  dropped (`select`). */
  readonly lockedReasonKey = input<string | null>(null);

  readonly emoteSetIdChange = output<string>();

  private readonly document = inject(DOCUMENT);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');

  protected readonly lockReasonId = `emote-set-menu-lock-reason-${nextLockReasonId++}`;

  protected readonly isOpen = signal(false);
  // Roving tabindex: null means "follow the selection", same convention as DateRangeMenu.
  private readonly focusedIndex = signal<number | null>(null);

  protected readonly options = computed(() => selectableEmoteSets(this.sets()));

  protected readonly selectedSet = computed(
    () => this.options().find((set) => set.id === this.selectedEmoteSetId()) ?? null,
  );

  protected readonly triggerDisabled = computed(
    () =>
      this.loading() ||
      this.unavailable() ||
      this.lockedReasonKey() !== null ||
      this.options().length === 0,
  );

  protected toggle(): void {
    if (this.triggerDisabled()) {
      return;
    }
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
    // Focus would otherwise fall to <body> together with the panel that held it.
    const hadFocus = this.elementRef.nativeElement.contains(this.document.activeElement);
    this.isOpen.set(false);
    this.focusedIndex.set(null);
    if (hadFocus) {
      this.trigger()?.nativeElement.focus();
    }
  }

  protected select(id: string): void {
    // A lock can land while the popover is open — the choice is then not the user's to make.
    if (this.lockedReasonKey() !== null) {
      this.close();
      return;
    }
    this.emoteSetIdChange.emit(id);
    this.close();
  }

  protected tabIndexFor(index: number): number {
    const active = this.focusedIndex() ?? this.selectedIndex();
    return index === active ? 0 : -1;
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

    const count = this.options().length;
    if (count === 0) {
      return;
    }

    event.preventDefault();
    // Focus only — committing here would refetch on every keypress, the exact wart DateRangeMenu
    // was built to remove. Enter/Space fire click and go through select().
    const next = (index + delta + count) % count;
    this.focusedIndex.set(next);
    const group = (event.currentTarget as HTMLElement).closest('[role="radiogroup"]');
    group?.querySelectorAll<HTMLButtonElement>('[role="radio"]')[next]?.focus();
  }

  private selectedIndex(): number {
    const index = this.options().findIndex((set) => set.id === this.selectedEmoteSetId());
    return index === -1 ? 0 : index;
  }
}
