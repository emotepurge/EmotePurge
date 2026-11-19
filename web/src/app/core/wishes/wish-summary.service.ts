import { HttpErrorResponse } from '@angular/common/http';
import { Service, computed, inject, signal } from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import {
  EMPTY,
  Observable,
  Subject,
  catchError,
  debounceTime,
  map,
  merge,
  of,
  switchMap,
} from 'rxjs';

import { AuthService } from '../auth/auth.service';
import { apiErrorCode } from '../i18n/api-error';
import { pollWhileVisible } from '../live/poll-while-visible';
import { pageVisible } from '../page-visibility/page-visibility';
import { WishService } from './wish.service';

/** How often the header count asks again while the window is visible (plan 2.4). */
export const WISH_SUMMARY_POLL_MS = 5 * 60_000;

/**
 * A burst of `refresh()` calls (a command plus its own `wish.changed`) becomes one request. Must stay
 * above `CHANNEL_RELOAD_DEBOUNCE_MS` (1000 ms): the live event follows the command by that debounce,
 * and a shorter window would fire once for the command and once more for the event.
 */
export const WISH_SUMMARY_REFRESH_DEBOUNCE_MS = 1500;

type SummaryOutcome = { kind: 'count'; count: number } | { kind: 'disabled' } | { kind: 'failed' };

/**
 * The header count of wishes that need the signed-in user's action, across all their channels
 * (#485, plan 2.4). One `GET /api/wishes/summary` on first need, then every five minutes while the
 * window is visible, at once when it becomes visible again, and once more after `refresh()`.
 *
 * Only with a signed-in user: an anonymous 401 on an `/api/` call signs the session out, so asking
 * without one would end a session nobody started. A 404 `wishes_disabled` means the switch is off —
 * the count is unavailable and nothing is asked again until the page is loaded anew. Any other
 * failure keeps the last value; before the first success the count is unavailable. No stream, and no
 * `/api/channels/mine`: the server counts.
 */
@Service()
export class WishSummaryService {
  private readonly wishService = inject(WishService);
  private readonly authService = inject(AuthService);
  private readonly visible = pageVisible();
  private readonly count = signal<number | null>(null);
  private readonly disabled = signal(false);
  private readonly refreshes = new Subject<void>();
  private readonly asking = computed(
    () => this.authService.currentUser() !== null && !this.disabled(),
  );

  /** Wishes needing action; `null` while unknown (signed out, not answered yet, switched off). */
  readonly actionCount = computed(() => (this.asking() ? this.count() : null));

  /** The header shows its link only while this holds (AK40). */
  readonly available = computed(() => this.actionCount() !== null);

  constructor() {
    const ticks = merge(
      pollWhileVisible(
        WISH_SUMMARY_POLL_MS,
        computed(() => this.asking() && this.visible()),
      ),
      this.refreshes.pipe(debounceTime(WISH_SUMMARY_REFRESH_DEBOUNCE_MS)),
    );

    // Signing out (or the switch turning out to be off) drops whatever request is on the way: its
    // answer belongs to a session that no longer shows a count.
    toObservable(this.asking)
      .pipe(
        switchMap((asking) => {
          if (!asking) {
            this.count.set(null);
            return EMPTY;
          }
          return ticks.pipe(switchMap(() => this.fetch()));
        }),
      )
      .subscribe((outcome) => this.apply(outcome));
  }

  /**
   * One more request soon, for the wish pages after a command of their own or a live change. Ignored
   * while nobody is signed in or once the switch was found off.
   */
  refresh(): void {
    if (this.asking()) {
      this.refreshes.next();
    }
  }

  private fetch(): Observable<SummaryOutcome> {
    return this.wishService.summary().pipe(
      map((summary): SummaryOutcome => ({ kind: 'count', count: summary.actionCount })),
      catchError((error: unknown) =>
        of<SummaryOutcome>(
          error instanceof HttpErrorResponse &&
            error.status === 404 &&
            apiErrorCode(error) === 'wishes_disabled'
            ? { kind: 'disabled' }
            : { kind: 'failed' },
        ),
      ),
    );
  }

  private apply(outcome: SummaryOutcome): void {
    switch (outcome.kind) {
      case 'count':
        this.count.set(outcome.count);
        return;
      case 'disabled':
        this.disabled.set(true);
        return;
      default:
        // A failed request keeps the last value standing; the next tick asks again.
        return;
    }
  }
}
