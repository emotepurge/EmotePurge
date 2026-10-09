import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { TranslocoService } from '@jsverse/transloco';
import { Observable, Subject, of, throwError } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { ChannelStatus } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';
import { joinWithBroadcasterLockPrompt } from './join-with-lock-prompt';

const STATUS: ChannelStatus = {
  channelId: 'c1',
  channelName: 'sensitron',
  isBotActive: true,
  activeEmoteSetId: 's1',
};

const LOCKED_409 = locked409('2026-10-01T10:00:00Z');

type JoinOptions = { liftBroadcasterLock?: { confirmedLockedAtUtc: string } };

function locked409(lockedAtUtc: string): HttpErrorResponse {
  return new HttpErrorResponse({
    status: 409,
    error: { errorCode: 'channel_locked_by_broadcaster', lockedAtUtc },
  });
}

function setup(joinImpl: (options?: JoinOptions) => Observable<ChannelStatus>) {
  const join = vi.fn((_name: string, options?: JoinOptions) => joinImpl(options));
  const closed = new Subject<boolean | undefined>();
  const open = vi.fn(() => ({ closed }));
  const translate = vi.fn((key: string, params?: Record<string, unknown>) =>
    key === 'broadcasterLock.liftConfirm' ? `lock:${String(params?.['date'])}` : key,
  );
  const deps = {
    channelService: { join } as unknown as ChannelService,
    dialog: { open } as unknown as Dialog,
    transloco: { translate } as unknown as TranslocoService,
    lang: 'en' as const,
  };
  const results: unknown[] = [];
  const errors: unknown[] = [];
  joinWithBroadcasterLockPrompt(deps, 'sensitron').subscribe({
    next: (value) => results.push(value),
    error: (error: unknown) => errors.push(error),
  });
  return { join, open, closed, translate, results, errors };
}

describe('joinWithBroadcasterLockPrompt', () => {
  it('passes a plain successful join through without a dialog', () => {
    const { join, open, results } = setup(() => of(STATUS));
    expect(results).toEqual([STATUS]);
    expect(join).toHaveBeenCalledTimes(1);
    expect(open).not.toHaveBeenCalled();
  });

  it('asks on the 409, names the date, and retries with the flag and that date once confirmed', () => {
    const { join, open, closed, translate, results } = setup((options) =>
      options?.liftBroadcasterLock ? of(STATUS) : throwError(() => LOCKED_409),
    );
    expect(open).toHaveBeenCalledTimes(1);
    expect(translate).toHaveBeenCalledWith(
      'broadcasterLock.liftConfirm',
      expect.objectContaining({ date: 'Oct 1, 2026', channelName: 'sensitron' }),
    );
    expect(join).toHaveBeenCalledTimes(1);

    closed.next(true);
    expect(join).toHaveBeenCalledTimes(2);
    expect(join).toHaveBeenLastCalledWith('sensitron', {
      liftBroadcasterLock: { confirmedLockedAtUtc: '2026-10-01T10:00:00Z' },
    });
    expect(results).toEqual([STATUS]);
  });

  it('asks again with the new date when the lock changed before the retry', () => {
    // The broadcaster locked again after the dialog opened: the server refuses the stale
    // confirmation with the current date, and only a fresh confirmation may lift that lock.
    const { join, open, closed, translate, results } = setup((options) => {
      const confirmed = options?.liftBroadcasterLock?.confirmedLockedAtUtc;
      if (confirmed === '2026-10-05T08:00:00.5Z') {
        return of(STATUS);
      }
      return throwError(() => (confirmed ? locked409('2026-10-05T08:00:00.5Z') : LOCKED_409));
    });
    closed.next(true);

    expect(open).toHaveBeenCalledTimes(2);
    expect(translate).toHaveBeenCalledWith(
      'broadcasterLock.liftConfirm',
      expect.objectContaining({ date: 'Oct 5, 2026', channelName: 'sensitron' }),
    );
    closed.next(true);
    expect(join).toHaveBeenCalledTimes(3);
    expect(join).toHaveBeenLastCalledWith('sensitron', {
      liftBroadcasterLock: { confirmedLockedAtUtc: '2026-10-05T08:00:00.5Z' },
    });
    expect(results).toEqual([STATUS]);
  });

  it('does not ask again when the retry is refused with the date just confirmed', () => {
    const { join, open, closed, errors } = setup(() => throwError(() => LOCKED_409));
    closed.next(true);

    expect(open).toHaveBeenCalledTimes(1);
    expect(join).toHaveBeenCalledTimes(2);
    expect(errors).toEqual([LOCKED_409]);
  });

  it('makes no second request and emits null when the admin declines', () => {
    const { join, closed, results } = setup(() => throwError(() => LOCKED_409));
    closed.next(false);
    expect(join).toHaveBeenCalledTimes(1);
    expect(results).toEqual([null]);
  });

  it('treats a dismissed dialog like a decline', () => {
    const { join, closed, results } = setup(() => throwError(() => LOCKED_409));
    closed.next(undefined);
    expect(join).toHaveBeenCalledTimes(1);
    expect(results).toEqual([null]);
  });

  it('lets the moderator 403 and any other failure through without a dialog', () => {
    const forbidden = new HttpErrorResponse({
      status: 403,
      error: { errorCode: 'channel_locked_by_broadcaster' },
    });
    const { open, errors } = setup(() => throwError(() => forbidden));
    expect(open).not.toHaveBeenCalled();
    expect(errors).toEqual([forbidden]);
  });
});
