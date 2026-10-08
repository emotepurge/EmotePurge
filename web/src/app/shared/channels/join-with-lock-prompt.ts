import { Dialog } from '@angular/cdk/dialog';
import { TranslocoService } from '@jsverse/transloco';
import { Observable, catchError, of, switchMap, take, throwError } from 'rxjs';

import { readBroadcasterLock } from '../../core/channels/broadcaster-lock';
import { ChannelStatus } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';
import { toLocale } from '../../core/i18n/locale';
import { AppLang } from '../../core/i18n/language.service';
import { openConfirmDialog } from '../ui/confirm-dialog';

export interface JoinWithLockPromptDeps {
  channelService: ChannelService;
  dialog: Dialog;
  transloco: TranslocoService;
  lang: AppLang;
}

/**
 * Join with the admin's second thought built in: a broadcaster who deleted their channel's data has
 * locked it, and the API answers an admin's plain join with a 409 instead of lifting that lock
 * silently. This asks, naming the date, and retries with the explicit flag and that very date only on
 * confirmation — the server lifts the lock only while it still carries the confirmed date.
 *
 * If the lock changed in between (the broadcaster locked again), the retry's 409 carries the new
 * date, and this asks once more with it. A 409 naming the date just confirmed is not asked again: it
 * cannot succeed on a repeat, so it passes through as an error instead of looping.
 *
 * Emits the joined status, or `null` when the admin declined (no further request is made). Every
 * other failure passes through untouched — in particular the 403 a moderator gets for the same code,
 * which is a message, not a question.
 *
 * Split from `readBroadcasterLock` (in `core/channels`) because opening a dialog needs `shared/ui`,
 * which `core/` must not import.
 */
export function joinWithBroadcasterLockPrompt(
  deps: JoinWithLockPromptDeps,
  channelName: string,
): Observable<ChannelStatus | null> {
  const { channelService, dialog, transloco, lang } = deps;

  const askAndRetry = (lockedAtUtc: string): Observable<ChannelStatus | null> => {
    const date = new Date(lockedAtUtc).toLocaleDateString(toLocale(lang), { dateStyle: 'medium' });
    return openConfirmDialog(dialog, {
      message: transloco.translate('broadcasterLock.liftConfirm', { date, channelName }),
      confirmLabel: transloco.translate('broadcasterLock.liftConfirmLabel'),
    }).closed.pipe(
      take(1),
      switchMap((confirmed) =>
        confirmed
          ? channelService
              .join(channelName, { liftBroadcasterLock: { confirmedLockedAtUtc: lockedAtUtc } })
              .pipe(
                catchError((error: unknown) => {
                  const lock = readBroadcasterLock(error);
                  return lock && lock.lockedAtUtc !== lockedAtUtc
                    ? askAndRetry(lock.lockedAtUtc)
                    : throwError(() => error);
                }),
              )
          : of(null),
      ),
    );
  };

  return channelService.join(channelName).pipe(
    catchError((error: unknown) => {
      const lock = readBroadcasterLock(error);
      return lock ? askAndRetry(lock.lockedAtUtc) : throwError(() => error);
    }),
  );
}
