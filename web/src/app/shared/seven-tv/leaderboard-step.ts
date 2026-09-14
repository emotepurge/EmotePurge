import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import {
  LeaderboardImportResult,
  LeaderboardSort,
  SevenTvLeaderboardResponse,
} from '../../core/seven-tv/leaderboard.model';
import { SevenTvLeaderboardService } from '../../core/seven-tv/seven-tv-leaderboard.service';
import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';
import { SkeletonRows } from '../ui/skeleton-rows';
import { ForeignEmoteGrid, ForeignEmoteSortMode } from './foreign-emote-grid';

type LoadState =
  | { status: 'loading' }
  | { status: 'error'; error: HttpErrorResponse }
  | { status: 'loaded'; response: SevenTvLeaderboardResponse };

/** The two lists the server offers, in menu order. `TRENDING_DAILY` leads because it is the
 *  default: "was gerade gut läuft" is the question this source was added for, and "top of all time"
 *  changes so slowly that it is the reference, not the entry point. */
const SORT_OPTIONS: { value: LeaderboardSort; labelKey: string }[] = [
  { value: 'TRENDING_DAILY', labelKey: 'import.leaderboard.sort.TRENDING_DAILY' },
  { value: 'TOP_ALL_TIME', labelKey: 'import.leaderboard.sort.TOP_ALL_TIME' },
];

/** Which score the grid is ordered by for a given list — the list *is* the ordering, so the grid's
 *  own sort control is forced off and this is what it is forced to (spec E12/F2). */
const GRID_SORT_MODE: Record<LeaderboardSort, Exclude<ForeignEmoteSortMode, 'none'>> = {
  TRENDING_DAILY: 'trending',
  TOP_ALL_TIME: 'topAllTime',
};

/** The truncation notice, one key per list. `truncated` is the normal case here, not an edge case,
 *  and the two lists mean wildly different things by it: 500 of ~700 entries is most of the day's
 *  trending list, 500 of ~1.37 million is a sample off the top (spec §4). One shared sentence would
 *  have to be vague enough to be true of both, which is how a notice stops being read. */
const TRUNCATED_KEYS: Record<LeaderboardSort, string> = {
  TRENDING_DAILY: 'import.leaderboard.truncated.TRENDING_DAILY',
  TOP_ALL_TIME: 'import.leaderboard.truncated.TOP_ALL_TIME',
};

/**
 * The "Aus 7TVs Bestenliste" branch of the one import dialog (spec §7) — the third source, and the
 * first one that does not require the user to already know *where* the emotes are: both other
 * branches start from a file they exported or a channel name they can name.
 *
 * The sort `<select>` is a **server** dimension, not a view option: each value is a different list
 * fetched from `GET /api/seventv/leaderboard`, and the grid below is forced to display in that
 * list's own rank order (`forcedSortMode`) with its own sort control suppressed — on a leaderboard
 * the ranking *is* the content, so a second in-grid control re-sorting it on top would contradict
 * the list the user just chose (spec E3/E12/F2).
 *
 * **A sort change empties the selection, visibly** (spec E14, and the reason this step does not
 * simply swap `response.emotes` under a standing grid). `ListSelection` keeps its selected *keys*
 * across a list change and resolves them only against the visible list — so a grid kept alive
 * across the swap would hold an invisible selection made against the other list, which is exactly
 * the defect class of #132/#133. Instead the state goes back through `'loading'`, which unmounts
 * the `@case ('loaded')` branch and with it the grid; the new list gets a new `ForeignEmoteGrid`
 * and a new `ListSelection`, `selectedRows` is cleared, {@link result} falls back to `null` and the
 * dialog's "Weiter" locks again. The same mechanism `ForeignChannelStep` relies on for a new
 * channel query, for the same reason: a selection made against one list has no honest meaning in
 * another.
 *
 * While a list is in flight the chooser is marked `aria-disabled` and refuses changes — announced as
 * unavailable, but still focusable and tabbable. It must stay focusable because it is what the
 * dialog hands the caret to on the way in, at a moment when a request is always outstanding; a real
 * `disabled` would make that focus call a silent no-op and drop the caret on `<body>` every time.
 * Refusing the change in the handler (and restoring the visible value) is what makes the
 * announcement honest, and it is also what makes the swap race-free: there is never a second request
 * that could resolve after the first and hand the grid a list the chooser no longer names.
 *
 * {@link focusFirstControl} is the dialog's focus contract (#147): CDK autofocuses once when the
 * overlay opens and never again for a swap inside it, so entering a step has to place the caret
 * itself. Here that is the sort select — the one thing this step is operated by before the grid
 * exists.
 */
@Component({
  selector: 'app-leaderboard-step',
  imports: [Button, ForeignEmoteGrid, NoticeBanner, SkeletonRows, TranslocoPipe],
  template: `
    <!-- A labelled select, same shape as the grid's own sort row (§5.2: a control in a dialog body
         says what it does). The label says "Liste", not "Sortieren nach": what this picks is which
         leaderboard is being read, and the ordering follows from that rather than the other way
         round (spec E3). -->
    <div class="flex items-center gap-2">
      <label class="text-sm text-fg-secondary" [for]="sortSelectId">
        {{ 'import.leaderboard.sortLabel' | transloco }}
      </label>
      <!-- aria-disabled, never the disabled attribute: this is the control the dialog hands the
           caret to on the way in (see focusFirstControl), and at that moment a list is always still
           in flight — a truly disabled element cannot take focus, so the caret would land on the
           document body every single time. This says "unavailable right now" to assistive technology
           while the control stays reachable and tabbable; onSortChange is what makes the word
           true. -->
      <select
        #sortSelect
        [id]="sortSelectId"
        class="app-input-sm aria-disabled:opacity-60"
        [attr.aria-disabled]="loading()"
        (change)="onSortChange($event)"
      >
        @for (option of sortOptions; track option.value) {
          <option [value]="option.value" [selected]="option.value === sortBy()">
            {{ option.labelKey | transloco }}
          </option>
        }
      </select>
    </div>

    @switch (state().status) {
      @case ('loading') {
        <app-skeleton-rows [count]="3" />
      }
      @case ('error') {
        <app-notice-banner variant="error">
          {{ errorMessageKey() | transloco }}
          <!-- Retries the same list. There is no cache bypass to offer: the server answers from its
               own hourly stock and a client cannot ask past it (spec §4). -->
          <button notice-action type="button" appButton="outline" (click)="reload()">
            {{ 'import.foreignChannel.retry' | transloco }}
          </button>
        </app-notice-banner>
      }
      @case ('loaded') {
        @if (loadedResponse(); as response) {
          <app-foreign-emote-grid
            [emotes]="response.emotes"
            [truncated]="response.truncated"
            [totalCount]="response.totalCount"
            [forcedSortMode]="gridSortMode()"
            emptyMessageKey="import.leaderboard.empty"
            [truncatedMessageKey]="truncatedMessageKey()"
            scoreHintKey="import.leaderboard.scoreHint"
            (selectionChange)="onSelectionChange($event)"
          />
        }
      }
    }
  `,
  host: { class: 'flex min-h-0 flex-col gap-3' },
})
export class LeaderboardStep implements OnInit {
  private readonly leaderboardService = inject(SevenTvLeaderboardService);

  private readonly sortSelectRef = viewChild<ElementRef<HTMLSelectElement>>('sortSelect');

  protected readonly sortOptions = SORT_OPTIONS;
  protected readonly sortSelectId = 'leaderboard-sort';

  /** Which list is being asked for. `TRENDING_DAILY` is the entry point (spec §7). */
  protected readonly sortBy = signal<LeaderboardSort>('TRENDING_DAILY');
  protected readonly state = signal<LoadState>({ status: 'loading' });
  protected readonly selectedRows = signal<ForeignEmoteRow[]>([]);

  /** Drives both halves of the "unavailable but reachable" pair: the `aria-disabled` the control
   *  announces, and the refusal in {@link onSortChange} that makes the announcement honest. One
   *  signal, so the two can never disagree (Regel 14). */
  protected readonly loading = computed(() => this.state().status === 'loading');

  protected readonly loadedResponse = computed<SevenTvLeaderboardResponse | null>(() => {
    const current = this.state();
    return current.status === 'loaded' ? current.response : null;
  });

  protected readonly errorMessageKey = computed(() => {
    const current = this.state();
    return current.status === 'error' ? apiErrorTranslationKey(current.error) : null;
  });

  /**
   * Both grid captions follow the **loaded** response's `sortBy`, not the select's — the server
   * echoes which list it actually answered with, and during a switch the two differ for exactly as
   * long as the request is in flight. Nothing is rendered in that window, but reading the echo is
   * what makes that true by construction rather than by timing.
   */
  protected readonly gridSortMode = computed<Exclude<ForeignEmoteSortMode, 'none'> | null>(() => {
    const response = this.loadedResponse();
    return response === null ? null : GRID_SORT_MODE[response.sortBy];
  });

  protected readonly truncatedMessageKey = computed(() => {
    const response = this.loadedResponse();
    return response === null
      ? 'import.leaderboard.truncated.TRENDING_DAILY'
      : TRUNCATED_KEYS[response.sortBy];
  });

  /** Whether the emote grid is on screen — the one state the dialog widens its pane against and
   *  hangs its "Weiter" off (§7.3), identical in meaning to `ForeignChannelStep.showsGrid`. */
  readonly showsGrid = computed(() => this.loadedResponse() !== null);

  /**
   * The step's whole outward contract: `null` while there is nothing to carry forward, the picked
   * rows plus the list they were picked off as soon as something is marked. The sort travels with
   * them because it *is* the origin — a leaderboard row has no source channel (spec E2/E8).
   *
   * A signal rather than a method, so the dialog's own `computed()` over its `viewChild` reacts to
   * it (Regel 14).
   */
  readonly result = computed<LeaderboardImportResult | null>(() => {
    const response = this.loadedResponse();
    const rows = this.selectedRows();
    if (response === null || rows.length === 0) {
      return null;
    }
    return { sortBy: response.sortBy, rows };
  });

  /** Loading happens here rather than in the constructor: nothing is read from an input, but the
   *  step is created by the dialog's `@switch` and "entering the step" is precisely this hook —
   *  Regel 13's habit, kept even where no required input is involved. */
  ngOnInit(): void {
    this.load();
  }

  /** Where the caret goes when this step is entered — see the class doc. Called by the dialog after
   *  the step has rendered, never from a constructor (Regel 13). */
  focusFirstControl(): void {
    this.sortSelectRef()?.nativeElement.focus();
  }

  /**
   * The enforcing half of `aria-disabled` (see the template): while a list is in flight this
   * refuses the change and puts the visible value back, so the chooser never names a list other
   * than the one being fetched. That is also what keeps the swap race-free without a generation
   * counter — there is never a second request that could resolve after the first and hand the grid
   * a list nobody asked for. It is not a race being tolerated, it is one that cannot arise.
   */
  protected onSortChange(event: Event): void {
    const select = event.target as HTMLSelectElement;
    if (this.loading()) {
      select.value = this.sortBy();
      return;
    }
    const value = select.value;
    if (value !== 'TRENDING_DAILY' && value !== 'TOP_ALL_TIME') {
      return;
    }
    this.sortBy.set(value);
    this.load();
  }

  protected reload(): void {
    this.load();
  }

  protected onSelectionChange(rows: ForeignEmoteRow[]): void {
    this.selectedRows.set(rows);
  }

  /**
   * Back through `'loading'` on every path, including the retry — that is what tears the grid down
   * and takes the selection with it (see the class doc). Clearing `selectedRows` on top is not
   * redundant: the grid emits its selection outward, so the last emission would otherwise survive
   * the unmount and keep `result()` non-null against a list that is no longer on screen.
   */
  private load(): void {
    this.state.set({ status: 'loading' });
    this.selectedRows.set([]);
    this.leaderboardService.load(this.sortBy()).subscribe({
      next: (response) => this.state.set({ status: 'loaded', response }),
      error: (error: HttpErrorResponse) => this.state.set({ status: 'error', error }),
    });
  }
}
