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
 * silently. This asks once, naming the date, and retries with the explicit flag only on confirmation.
 *
 * Emits the joined status, or `null` when the admin declined (no second request is made). Every other
 * failure passes through untouched — in particular the 403 a moderator gets for the same code, which
 * is a message, not a question.
 *
 * Split from `readBroadcasterLock` (in `core/channels`) because opening a dialog needs `shared/ui`,
 * which `core/` must not import.
 */
export function joinWithBroadcasterLockPrompt(
  deps: JoinWithLockPromptDeps,
  channelName: string,
): Observable<ChannelStatus | null> {
  const { channelService, dialog, transloco, lang } = deps;
  return channelService.join(channelName).pipe(
    catchError((error: unknown) => {
      const lock = readBroadcasterLock(error);
      if (!lock) {
        return throwError(() => error);
      }
      const date = new Date(lock.lockedAtUtc).toLocaleDateString(toLocale(lang), {
        dateStyle: 'medium',
      });
      return openConfirmDialog(dialog, {
        message: transloco.translate('broadcasterLock.liftConfirm', { date, channelName }),
        confirmLabel: transloco.translate('broadcasterLock.liftConfirmLabel'),
      }).closed.pipe(
        take(1),
        switchMap((confirmed) =>
          confirmed ? channelService.join(channelName, { liftBroadcasterLock: true }) : of(null),
        ),
      );
    }),
  );
}
