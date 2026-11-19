import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of, switchMap } from 'rxjs';

import { ChannelService } from './channel.service';
import { AuthService } from '../auth/auth.service';

/**
 * Guards the wish page of a channel (#485, spec E19): management rights **and** the operator's
 * switch (`Wishes:Enabled`, surfaced as `wishesEnabled`). With the switch off the page does not
 * exist, so a direct visit is turned away exactly like a missing right. Same fallback as
 * `channelManageGuard`; a failing permissions call counts as "no".
 */
export const channelWishesGuard: CanActivateFn = (route, state) => {
  const authService = inject(AuthService);
  const channelService = inject(ChannelService);
  const router = inject(Router);

  const channelName = route.paramMap.get('channelName');
  if (!channelName) {
    return of(router.createUrlTree(['/']));
  }

  // Same fallback as the usage guard: stay inside the channel the user navigated into rather than
  // bouncing them out to the overview.
  const fallback = () => router.createUrlTree(['/channels', channelName, 'vote-sessions']);

  return authService.ensureLoaded().pipe(
    switchMap((user) => {
      if (!user) {
        authService.stashReturnUrl(state.url);
        return of(router.createUrlTree(['/login']));
      }

      return channelService.getPermissions(channelName).pipe(
        map((permissions) =>
          permissions.canManage && permissions.wishesEnabled ? true : fallback(),
        ),
        catchError(() => of(fallback())),
      );
    }),
  );
};
