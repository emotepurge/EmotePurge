import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, of, Subject, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import en from '../../../../public/i18n/en.json';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import type {
  UpdateWishRewardBody,
  WishReward,
  WishRewardLearning,
  WishRewardsRead,
} from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { WishRewardDialog, type WishRewardDialogResult } from './wish-reward-dialog';
import { WishRewardsSection } from './wish-rewards-section';

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  close(): void {
    /* no-op */
  }

  emit(event: { type: string; channel?: string }): void {
    this.onmessage?.({ data: JSON.stringify(event) } as MessageEvent);
  }
}

const NOW = new Date('2026-11-19T12:00:00Z');
const T = en.wishes.settings.rewards;

function reward(over: Partial<WishReward> = {}): WishReward {
  return {
    id: 1,
    label: 'Emote-Wunsch',
    defaultDurationDays: 14,
    isActive: true,
    createdAtUtc: '2026-11-01T00:00:00Z',
    lastRedemptionSeenAtUtc: '2026-11-19T11:00:00Z',
    ...over,
  };
}

function session(over: Partial<WishRewardLearning> = {}): WishRewardLearning {
  return {
    sessionId: 'session-a',
    startedAtUtc: '2026-11-19T11:58:00Z',
    expiresAtUtc: '2026-11-19T12:08:00Z',
    candidate: null,
    ...over,
  };
}

const CANDIDATE = {
  candidateId: 'candidate-1',
  text: 'https://7tv.app/emotes/01FDGSXVJG0004RTSM3QHRP7QN',
  login: 'viewer_one',
  seenAtUtc: '2026-11-19T11:59:00Z',
};

function rewardsRead(over: Partial<WishRewardsRead> = {}): WishRewardsRead {
  return {
    channelBound: true,
    rewards: [reward()],
    limit: 10,
    learning: null,
    gaps: [],
    canEdit: true,
    pendingExpiryDays: 14,
    captureAliveAtUtc: '2026-11-19T11:59:30Z',
    ...over,
  };
}

function apiError(status: number, errorCode?: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: errorCode ? { errorCode } : null });
}

interface OpenedDialog {
  component: unknown;
  data: unknown;
  closed: Subject<unknown>;
  /** The section closed it itself (`DialogRef.close`), with this result. */
  closedBySection: unknown[];
}

describe('WishRewardsSection', () => {
  let getRewards: ReturnType<typeof vi.fn<(channel: string) => Observable<WishRewardsRead>>>;
  let startLearning: ReturnType<typeof vi.fn<(channel: string) => Observable<WishRewardLearning>>>;
  let cancelLearning: ReturnType<typeof vi.fn<(channel: string, id: string) => Observable<void>>>;
  let confirmLearning: ReturnType<typeof vi.fn>;
  let updateReward: ReturnType<
    typeof vi.fn<
      (channel: string, id: number, body: UpdateWishRewardBody) => Observable<WishReward>
    >
  >;
  let deleteReward: ReturnType<typeof vi.fn<(channel: string, id: number) => Observable<void>>>;
  let opened: OpenedDialog[];
  let fixture: ComponentFixture<WishRewardsSection>;

  async function create(): Promise<void> {
    FakeEventSource.instances = [];
    TestBed.configureTestingModule({
      imports: [
        WishRewardsSection,
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [
        {
          provide: WishService,
          useValue: {
            getRewards,
            startLearning,
            cancelLearning,
            confirmLearning,
            updateReward,
            deleteReward,
          },
        },
        {
          provide: Dialog,
          useValue: {
            open: (component: unknown, config: { data: unknown }) => {
              const closed = new Subject<unknown>();
              const entry: OpenedDialog = {
                component,
                data: config.data,
                closed,
                closedBySection: [],
              };
              opened.push(entry);
              return {
                closed,
                close: (result?: unknown) => {
                  entry.closedBySection.push(result);
                  closed.next(result);
                },
              };
            },
          },
        },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });
    fixture = TestBed.createComponent(WishRewardsSection);
    fixture.componentRef.setInput('channelName', 'sensitron');
    fixture.componentRef.setInput('defaultDurationDays', 21);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function el(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function button(label: string): HTMLButtonElement | undefined {
    return Array.from(el().querySelectorAll('button')).find(
      (candidate) => candidate.textContent?.trim() === label,
    );
  }

  function status(): string {
    return el().querySelector('[role="status"]')?.textContent?.trim() ?? '';
  }

  /** A `wish.changed` through the live stream, past the debounce. */
  async function nudge(): Promise<void> {
    FakeEventSource.instances[0].emit({ type: LIVE_EVENT_TYPES.wishChanged, channel: 'sensitron' });
    await vi.advanceTimersByTimeAsync(600);
    fixture.detectChanges();
  }

  function closeDialog(result: WishRewardDialogResult | undefined): void {
    opened.at(-1)!.closed.next(result);
    fixture.detectChanges();
  }

  beforeEach(() => {
    vi.useFakeTimers({
      toFake: ['Date', 'setInterval', 'clearInterval', 'setTimeout', 'clearTimeout'],
    });
    vi.setSystemTime(NOW);
    opened = [];
    getRewards = vi.fn(() => of(rewardsRead()));
    startLearning = vi.fn(() => of(session()));
    cancelLearning = vi.fn(() => of(undefined));
    confirmLearning = vi.fn();
    updateReward = vi.fn((_channel, id, body) =>
      of(reward({ id, isActive: body.isActive ?? true })),
    );
    deleteReward = vi.fn(() => of(undefined));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  describe('learning a reward', () => {
    it('goes from idle to waiting on start, then to the candidate on wish.changed', async () => {
      await create();
      expect(button(T.learning.start)!.disabled).toBe(false);

      button(T.learning.start)!.click();
      fixture.detectChanges();

      expect(startLearning).toHaveBeenCalledWith('sensitron');
      expect(status()).toBe(T.learning.waiting);
      expect(el().textContent).toContain('8:00');
      expect(button(T.learning.confirm)).toBeUndefined();

      getRewards.mockReturnValue(of(rewardsRead({ learning: session({ candidate: CANDIDATE }) })));
      await nudge();

      expect(status()).toBe(T.learning.candidate);
      expect(el().textContent).toContain(CANDIDATE.text);
      expect(el().textContent).toContain('viewer_one');
      // Showing the candidate saves nothing.
      expect(confirmLearning).not.toHaveBeenCalled();
      expect(button(T.learning.confirm)).toBeDefined();
    });

    it('keeps a candidate a live read showed before the start answered', async () => {
      const started = new Subject<WishRewardLearning>();
      startLearning.mockReturnValue(started);
      await create();
      button(T.learning.start)!.click();
      fixture.detectChanges();

      // The redemption comes in while the start's answer is still on its way.
      getRewards.mockReturnValue(of(rewardsRead({ learning: session({ candidate: CANDIDATE }) })));
      await nudge();
      expect(status()).toBe(T.learning.candidate);

      started.next(session());
      started.complete();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();

      expect(status()).toBe(T.learning.candidate);
      expect(el().textContent).toContain(CANDIDATE.text);
    });

    it('reads again when a live read was still on its way as the start answered', async () => {
      const started = new Subject<WishRewardLearning>();
      startLearning.mockReturnValue(started);
      await create();
      button(T.learning.start)!.click();
      fixture.detectChanges();

      // The reload after the redemption is under way when the start answers; the server holds the
      // candidate, and no further event will come.
      const onItsWay = new Subject<WishRewardsRead>();
      getRewards.mockReturnValueOnce(onItsWay);
      getRewards.mockReturnValue(of(rewardsRead({ learning: session({ candidate: CANDIDATE }) })));
      await nudge();

      started.next(session());
      started.complete();
      await vi.advanceTimersByTimeAsync(0);
      fixture.detectChanges();

      expect(getRewards).toHaveBeenCalledTimes(3);
      expect(status()).toBe(T.learning.candidate);
      expect(el().textContent).toContain(CANDIDATE.text);
    });

    it('opens the confirm dialog with the candidate as shown and the channel duration', async () => {
      const shown = session({ candidate: CANDIDATE });
      getRewards.mockReturnValue(of(rewardsRead({ learning: shown })));
      await create();

      button(T.learning.confirm)!.click();

      expect(opened).toHaveLength(1);
      expect(opened[0].component).toBe(WishRewardDialog);
      expect(opened[0].data).toEqual({
        mode: 'confirm',
        channelName: 'sensitron',
        learning: shown,
        defaultDurationDays: 21,
      });
      expect(confirmLearning).not.toHaveBeenCalled();
    });

    it('keeps the dialog on the candidate it opened with while a reload brings another', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ learning: session({ candidate: CANDIDATE }) })));
      await create();
      button(T.learning.confirm)!.click();

      getRewards.mockReturnValue(
        of(
          rewardsRead({
            learning: session({
              sessionId: 'session-b',
              candidate: { ...CANDIDATE, candidateId: 'candidate-2' },
            }),
          }),
        ),
      );
      await nudge();

      const data = opened[0].data as { learning: WishRewardLearning };
      expect(data.learning.sessionId).toBe('session-a');
      expect(data.learning.candidate?.candidateId).toBe('candidate-1');
    });

    it('takes the saved reward into the list and ends the learning', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ learning: session({ candidate: CANDIDATE }) })));
      await create();
      button(T.learning.confirm)!.click();

      closeDialog({ kind: 'saved', reward: reward({ id: 2, label: 'Neue Belohnung' }) });

      expect(el().textContent).toContain('Neue Belohnung');
      expect(status()).toBe('Reward “Neue Belohnung” learned.');
      expect(button(T.learning.start)).toBeDefined();
    });

    it('reloads after a stale dialog and names a vanished candidate', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ learning: session({ candidate: CANDIDATE }) })));
      await create();
      const reads = getRewards.mock.calls.length;

      button(T.learning.confirm)!.click();
      closeDialog({ kind: 'stale' });
      expect(getRewards.mock.calls.length).toBe(reads + 1);

      getRewards.mockReturnValue(of(rewardsRead()));
      button(T.learning.confirm)!.click();
      closeDialog({ kind: 'gone', errorKey: 'errors.api.wish_reward_no_candidate' });

      expect(el().querySelector('[role="alert"]')?.textContent).toContain(
        en.errors.api.wish_reward_no_candidate,
      );
      expect(button(T.learning.start)).toBeDefined();
    });

    it('cancels the session it shows, not whatever lies there now', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ learning: session() })));
      await create();

      button(T.learning.cancel)!.click();
      fixture.detectChanges();

      expect(cancelLearning).toHaveBeenCalledWith('sensitron', 'session-a');
      expect(button(T.learning.start)).toBeDefined();
    });

    it('names a stale cancel and shows the current state', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ learning: session() })));
      await create();
      cancelLearning.mockReturnValue(throwError(() => apiError(409, 'wish_learning_stale')));
      getRewards.mockReturnValue(
        of(rewardsRead({ learning: session({ sessionId: 'session-b' }) })),
      );

      button(T.learning.cancel)!.click();
      fixture.detectChanges();

      expect(el().querySelector('[role="alert"]')?.textContent).toContain(
        en.errors.api.wish_learning_stale,
      );
      expect(getRewards).toHaveBeenCalledTimes(2);
      expect(status()).toBe(T.learning.waiting);
    });

    it('names a learning another manager started first and loads it', async () => {
      await create();
      startLearning.mockReturnValue(throwError(() => apiError(409, 'wish_learning_active')));
      getRewards.mockReturnValue(of(rewardsRead({ learning: session() })));

      button(T.learning.start)!.click();
      fixture.detectChanges();

      expect(el().querySelector('[role="alert"]')?.textContent).toContain(
        en.errors.api.wish_learning_active,
      );
      expect(status()).toBe(T.learning.waiting);
    });

    it('names an expiry while the candidate is shown, and keeps naming it after the server drops the row', async () => {
      getRewards.mockReturnValue(
        of(
          rewardsRead({
            learning: session({ expiresAtUtc: '2026-11-19T12:00:05Z', candidate: CANDIDATE }),
          }),
        ),
      );
      await create();
      expect(button(T.learning.confirm)).toBeDefined();

      await vi.advanceTimersByTimeAsync(6000);
      fixture.detectChanges();

      expect(status()).toBe(T.learning.expired);
      expect(button(T.learning.confirm)).toBeUndefined();
      expect(button(T.learning.start)).toBeDefined();

      getRewards.mockReturnValue(of(rewardsRead()));
      await nudge();
      expect(status()).toBe(T.learning.expired);

      button(T.learning.start)!.click();
      fixture.detectChanges();
      expect(status()).toBe(T.learning.waiting);
    });
  });

  describe('blocking the start', () => {
    it('locks the start at the reward limit and names the reason beside it', async () => {
      getRewards.mockReturnValue(
        of(rewardsRead({ rewards: Array.from({ length: 10 }, (_, i) => reward({ id: i })) })),
      );
      await create();

      const start = button(T.learning.start)!;
      expect(start.disabled).toBe(true);
      expect(start.getAttribute('aria-describedby')).toBe('wish-rewards-start-blocked');
      expect(el().querySelector('#wish-rewards-start-blocked')?.textContent).toContain('10');
      start.click();
      expect(startLearning).not.toHaveBeenCalled();
    });

    it('explains an unbound channel instead of offering any action', async () => {
      getRewards.mockReturnValue(
        of(rewardsRead({ channelBound: false, rewards: [], learning: null })),
      );
      await create();

      expect(el().textContent).toContain(T.unbound);
      expect(button(T.learning.start)).toBeUndefined();
      expect(button(T.edit)).toBeUndefined();
    });

    it('is read-only without canEdit: list and state, no command', async () => {
      getRewards.mockReturnValue(
        of(rewardsRead({ canEdit: false, learning: session({ candidate: CANDIDATE }) })),
      );
      await create();

      expect(el().textContent).toContain('Emote-Wunsch');
      expect(el().textContent).toContain(CANDIDATE.text);
      expect(button(T.learning.start)).toBeUndefined();
      expect(button(T.learning.confirm)).toBeUndefined();
      expect(button(T.learning.cancel)).toBeUndefined();
      expect(button(T.edit)).toBeUndefined();
      expect(button(T.remove)).toBeUndefined();
    });

    it('turns read-only once a write answers restricted', async () => {
      await create();
      startLearning.mockReturnValue(throwError(() => apiError(403, 'wish_settings_restricted')));

      button(T.learning.start)!.click();
      fixture.detectChanges();

      expect(el().textContent).toContain(T.readOnly);
      expect(button(T.learning.start)).toBeUndefined();
      expect(el().querySelector('[role="alert"]')).toBeNull();
    });
  });

  describe('the learned rewards', () => {
    it('pauses, then offers resuming by its text, sending only isActive', async () => {
      await create();

      button(T.pause)!.click();
      fixture.detectChanges();

      expect(updateReward).toHaveBeenCalledWith('sensitron', 1, { isActive: false });
      // The state is in the words: no pause button left, a resume button in its place.
      expect(button(T.pause)).toBeUndefined();
      expect(button(T.resume)!.hasAttribute('aria-pressed')).toBe(false);
      expect(el().textContent).toContain(T.paused);

      updateReward.mockReturnValue(of(reward({ isActive: true })));
      button(T.resume)!.click();
      fixture.detectChanges();

      expect(updateReward).toHaveBeenLastCalledWith('sensitron', 1, { isActive: true });
      expect(button(T.pause)).toBeDefined();
    });

    it('names what to do when a write is refused without a body', async () => {
      await create();
      updateReward.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 403 })));

      button(T.pause)!.click();
      fixture.detectChanges();

      expect(el().querySelector('[role="alert"]')?.textContent).toContain(T.forbiddenWrite);
    });

    it('names its row in the action group', async () => {
      await create();

      const group = el().querySelector('[role="group"]')!;
      const label = el().querySelector(`#${group.getAttribute('aria-labelledby')}`);
      expect(label?.textContent?.trim()).toBe('Emote-Wunsch');
    });

    it('forgets a reward only after the confirmation', async () => {
      await create();

      button(T.remove)!.click();
      expect(deleteReward).not.toHaveBeenCalled();
      closeDialog(undefined);
      expect(deleteReward).not.toHaveBeenCalled();

      button(T.remove)!.click();
      opened.at(-1)!.closed.next(true);
      fixture.detectChanges();

      expect(deleteReward).toHaveBeenCalledWith('sensitron', 1);
      expect(el().textContent).toContain(T.empty);
    });

    it('opens the edit dialog with the reward and takes its answer', async () => {
      await create();

      button(T.edit)!.click();
      expect(opened[0].data).toEqual({ mode: 'edit', channelName: 'sensitron', reward: reward() });

      closeDialog({ kind: 'saved', reward: reward({ label: 'Umbenannt' }) });
      expect(el().textContent).toContain('Umbenannt');
    });

    it('names a reward forgotten meanwhile and reloads', async () => {
      await create();
      updateReward.mockReturnValue(throwError(() => apiError(404, 'wish_reward_not_found')));
      getRewards.mockReturnValue(of(rewardsRead({ rewards: [] })));

      button(T.pause)!.click();
      fixture.detectChanges();

      expect(el().querySelector('[role="alert"]')?.textContent).toContain(
        en.errors.api.wish_reward_not_found,
      );
      expect(el().textContent).toContain(T.empty);
    });
  });

  describe('the capture', () => {
    it('warns when the life stamp is older than three minutes or missing', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ captureAliveAtUtc: '2026-11-19T11:56:00Z' })));
      await create();
      expect(el().textContent).toContain(T.captureStopped);
    });

    it('does not warn on a fresh stamp', async () => {
      await create();
      expect(el().textContent).not.toContain(T.captureStopped);
    });

    it('starts the warning once the stamp ages while the page is open', async () => {
      // The worker stopped: every read brings the same old stamp.
      await create();
      await vi.advanceTimersByTimeAsync(3 * 60 * 1000);
      fixture.detectChanges();
      expect(el().textContent).toContain(T.captureStopped);
    });

    /** A worker that stamps once a minute: every read brings a stamp half a minute old. */
    function healthyWorker(): void {
      getRewards.mockImplementation(() =>
        of(rewardsRead({ captureAliveAtUtc: new Date(Date.now() - 30_000).toISOString() })),
      );
    }

    function setVisibility(state: DocumentVisibilityState): void {
      Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
      document.dispatchEvent(new Event('visibilitychange'));
    }

    it('reads the stamp again on a quiet channel, so a healthy worker never shows the warning', async () => {
      healthyWorker();
      await create();
      for (let minute = 1; minute <= 10; minute++) {
        await vi.advanceTimersByTimeAsync(60_000);
        fixture.detectChanges();
        expect(el().textContent).not.toContain(T.captureStopped);
      }
      // Not a per-second poll: at most one read a minute besides the first.
      expect(getRewards.mock.calls.length).toBeGreaterThan(1);
      expect(getRewards.mock.calls.length).toBeLessThanOrEqual(11);
    });

    it('clears the warning once the worker stamps again', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ captureAliveAtUtc: '2026-11-19T11:50:00Z' })));
      await create();
      expect(el().textContent).toContain(T.captureStopped);

      healthyWorker();
      await vi.advanceTimersByTimeAsync(60_000);
      fixture.detectChanges();
      expect(el().textContent).not.toContain(T.captureStopped);
    });

    it('does not read for the stamp while the window is hidden', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ captureAliveAtUtc: '2026-11-19T11:50:00Z' })));
      await create();
      setVisibility('hidden');
      try {
        await vi.advanceTimersByTimeAsync(5 * 60 * 1000);
        expect(getRewards).toHaveBeenCalledTimes(1);

        setVisibility('visible');
        await vi.advanceTimersByTimeAsync(1000);
        expect(getRewards).toHaveBeenCalledTimes(2);
      } finally {
        setVisibility('visible');
      }
    });

    it('does not read for the stamp of an unbound channel, which shows no warning', async () => {
      getRewards.mockReturnValue(
        of(rewardsRead({ channelBound: false, captureAliveAtUtc: null, rewards: [] })),
      );
      await create();
      await vi.advanceTimersByTimeAsync(5 * 60 * 1000);
      expect(getRewards).toHaveBeenCalledTimes(1);
    });

    it('lists the gaps on demand, with the approximation said', async () => {
      getRewards.mockReturnValue(
        of(
          rewardsRead({
            gaps: [
              {
                fromUtc: '2026-11-18T10:00:00Z',
                toUtc: '2026-11-18T10:05:00Z',
                reason: 'disconnect',
              },
            ],
          }),
        ),
      );
      await create();

      const toggle = Array.from(el().querySelectorAll('button[aria-expanded]'))[0] as HTMLElement;
      expect(toggle.getAttribute('aria-expanded')).toBe('false');
      toggle.click();
      fixture.detectChanges();

      expect(toggle.getAttribute('aria-expanded')).toBe('true');
      expect(el().textContent).toContain(T.gaps.hint);
      expect(el().textContent).toContain(T.gaps.reason.disconnect);
    });
  });

  describe('loading', () => {
    it('reloads after the live stream reopened', async () => {
      await create();
      const source = FakeEventSource.instances[0];
      source.onopen?.();
      source.onopen?.();
      await vi.advanceTimersByTimeAsync(0);

      expect(getRewards).toHaveBeenCalledTimes(2);
    });

    it('shows nothing when the capture was switched off under the page', async () => {
      getRewards.mockReturnValue(throwError(() => apiError(404, 'wish_capture_disabled')));
      await create();

      expect(el().querySelector('h4')).toBeNull();
    });

    it('says why a manager not confirmed by Twitch sees nothing', async () => {
      getRewards.mockReturnValue(throwError(() => apiError(403)));
      await create();

      expect(el().textContent).toContain(T.forbidden);
      expect(button(T.learning.start)).toBeUndefined();
    });

    it('drops everything it showed when a later read is refused (403)', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ learning: session({ candidate: CANDIDATE }) })));
      await create();
      expect(el().textContent).toContain(CANDIDATE.login);

      getRewards.mockReturnValue(throwError(() => apiError(403)));
      await nudge();

      expect(el().textContent).toContain(T.forbidden);
      expect(el().textContent).not.toContain(CANDIDATE.text);
      expect(el().textContent).not.toContain(CANDIDATE.login);
      expect(el().textContent).not.toContain('Emote-Wunsch');
      expect(button(T.learning.confirm)).toBeUndefined();
      expect(button(T.pause)).toBeUndefined();

      // Confirmed again later: the next read shows the block once more.
      getRewards.mockReturnValue(of(rewardsRead()));
      await nudge();
      expect(button(T.pause)).toBeDefined();
    });

    it('closes the confirm dialog with its candidate when a read is refused (403)', async () => {
      getRewards.mockReturnValue(of(rewardsRead({ learning: session({ candidate: CANDIDATE }) })));
      await create();
      button(T.learning.confirm)!.click();

      getRewards.mockReturnValue(throwError(() => apiError(403)));
      await nudge();

      expect(opened[0].closedBySection).toEqual([undefined]);
      expect(confirmLearning).not.toHaveBeenCalled();
    });
  });
});
