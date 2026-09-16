import {
  ApplicationConfig,
  inject,
  isDevMode,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import {
  provideRouter,
  withComponentInputBinding,
  withInMemoryScrolling,
  withRouterConfig,
} from '@angular/router';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { provideTransloco, TranslocoService } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';

import { routes } from './app.routes';
import { apiAuthInterceptor } from './core/http/api-auth.interceptor';
import { LanguageService, resolveInitialLang, SUPPORTED_LANGS } from './core/i18n/language.service';
import { TranslocoHttpLoader } from './transloco-loader';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(
      routes,
      withComponentInputBinding(),
      // Default 'emptyOnly' strategy would stop `channelName` (owned by the ':channelName'
      // segment) from reaching non-empty-path children like 'usage-stats' via input binding.
      // onSameUrlNavigation 'reload': clicking an anchor link for the fragment the URL already
      // carries must still scroll (anchorScrolling only acts on completed navigations).
      withRouterConfig({ paramsInheritanceStrategy: 'always', onSameUrlNavigation: 'reload' }),
      // The router intercepts even plain `href="#features"` clicks as fragment navigations and,
      // without this, neither scrolls itself nor lets the browser's native jump through — the
      // landing page's anchor nav did nothing. scrollPositionRestoration also puts route changes
      // back at the top instead of keeping the previous page's scroll offset.
      withInMemoryScrolling({ anchorScrolling: 'enabled', scrollPositionRestoration: 'enabled' }),
    ),
    provideHttpClient(withInterceptors([apiAuthInterceptor])),
    provideTransloco({
      config: {
        availableLangs: [...SUPPORTED_LANGS],
        // Matches LanguageService's own resolution so the correct translation file loads on the
        // very first HTTP request instead of fetching the config default and re-fetching after.
        defaultLang: resolveInitialLang(),
        reRenderOnLangChange: true,
        prodMode: !isDevMode(),
      },
      loader: TranslocoHttpLoader,
    }),
    // Eagerly instantiate LanguageService so `<html lang>` is corrected before first paint even on
    // routes whose first-rendered component doesn't itself inject it (e.g. no AccountMenu
    // mounted yet in that subtree).
    provideAppInitializer(() => {
      inject(LanguageService);
    }),
    // Block the bootstrap on the active translation file: the app must not render before it can
    // speak. TranslocoPipe reports a late-arriving translation with ChangeDetectorRef.markForCheck(),
    // which marks the view dirty but schedules no change-detection run in a zoneless app — so every
    // binding evaluated before the file landed kept its empty string until some unrelated event
    // happened to run change detection. Components created later (anything inside an @if that flips
    // on a resolved permission) read the loaded value on first render and looked fine, which is why
    // the damage showed up as *some* labels missing rather than all of them: the tab bar and the
    // up-link lost their accessible names, both a contract in UI-Designsprache §8.1/§8.6.
    //
    // Waiting is the honest fix, because there is nothing useful to show untranslated. It costs one
    // same-origin JSON before first paint and removes the flash of empty labels along the way.
    // Registered AFTER the LanguageService initializer above, so the language is resolved first.
    provideAppInitializer(() => {
      const transloco = inject(TranslocoService);
      // A failed translation load must not cost the whole app its bootstrap — an unlabelled UI is
      // bad, a blank page is worse. Transloco keeps retrying and the UI fills in on the next
      // change-detection run.
      return firstValueFrom(transloco.load(transloco.getActiveLang())).catch(() => undefined);
    }),
  ],
};
