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

const LOCKED_409 = new HttpErrorResponse({
  status: 409,
  error: { errorCode: 'channel_locked_by_broadcaster', lockedAtUtc: '2026-10-01T10:00:00Z' },
});

function setup(
  joinImpl: (options?: { liftBroadcasterLock?: boolean }) => Observable<ChannelStatus>,
) {
  const join = vi.fn((_name: string, options?: { liftBroadcasterLock?: boolean }) =>
    joinImpl(options),
  );
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

  it('asks on the 409, names the date, and retries with the flag once confirmed', () => {
    const { join, open, closed, translate, results } = setup((options) =>
      options?.liftBroadcasterLock ? of(STATUS) : throwError(() => LOCKED_409),
    );
    expect(open).toHaveBeenCalledTimes(1);
    expect(translate).toHaveBeenCalledWith(
      'broadcasterLock.liftConfirm',
      expect.objectContaining({ date: 'Oct 1, 2026' }),
    );
    expect(join).toHaveBeenCalledTimes(1);

    closed.next(true);
    expect(join).toHaveBeenCalledTimes(2);
    expect(join).toHaveBeenLastCalledWith('sensitron', { liftBroadcasterLock: true });
    expect(results).toEqual([STATUS]);
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
