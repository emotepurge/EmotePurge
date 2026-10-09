import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of, switchMap } from 'rxjs';

import { ChannelService } from './channel.service';
import { AuthService } from '../auth/auth.service';

/**
 * Guards the channel's settings tab: management rights **and** the operator's backfill switch
 * (`ChatLogBackfill:Enabled`, surfaced as `chatLogBackfillEnabled`). With the switch off the tab does
 * not exist, so a direct visit is redirected exactly like a missing right (spec 2026-10-09, D17).
 * Same fallback as `channelManageGuard`.
 */
export const channelSettingsGuard: CanActivateFn = (route, state) => {
  const authService = inject(AuthService);
  const channelService = inject(ChannelService);
  const router = inject(Router);

  const channelName = route.paramMap.get('channelName');
  if (!channelName) {
    return of(router.createUrlTree(['/']));
  }

  const fallback = () => router.createUrlTree(['/channels', channelName, 'vote-sessions']);

  return authService.ensureLoaded().pipe(
    switchMap((user) => {
      if (!user) {
        authService.stashReturnUrl(state.url);
        return of(router.createUrlTree(['/login']));
      }

      return channelService.getPermissions(channelName).pipe(
        map((permissions) =>
          permissions.canManage && permissions.chatLogBackfillEnabled ? true : fallback(),
        ),
        catchError(() => of(fallback())),
      );
    }),
  );
};
