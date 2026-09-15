import { inject, Injectable, signal } from '@angular/core';
import { TranslocoService } from '@jsverse/transloco';

export const SUPPORTED_LANGS = ['de', 'en'] as const;
export type AppLang = (typeof SUPPORTED_LANGS)[number];

const LANG_STORAGE_KEY = 'ep_lang';

function isAppLang(value: string | null): value is AppLang {
  return (SUPPORTED_LANGS as readonly string[]).includes(value ?? '');
}

/** Stored preference wins; otherwise the browser's language; otherwise German (primary audience). */
export function resolveInitialLang(): AppLang {
  const stored = localStorage.getItem(LANG_STORAGE_KEY);
  if (isAppLang(stored)) {
    return stored;
  }
  const browserLang = navigator.language.slice(0, 2).toLowerCase();
  return isAppLang(browserLang) ? browserLang : 'de';
}

@Injectable({ providedIn: 'root' })
export class LanguageService {
  private readonly translocoService = inject(TranslocoService);

  readonly lang = signal<AppLang>(resolveInitialLang());

  /** The language the switcher UI shows as selected. Set immediately in `setLang`, alongside
   *  `<html lang>`/storage, and reverted to the rendered language if that load fails. `lang` itself
   *  stays the language actually rendered. */
  readonly selectedLang = signal<AppLang>(this.lang());

  /** Identifies the most recent `setLang` call. A `load` callback compares its own captured id
   *  against this rather than against `selectedLang`'s current value — two different requests for
   *  the same language would otherwise look identical to a value-based check and let a stale one
   *  win. */
  private latestRequestId = 0;

  constructor() {
    document.documentElement.lang = this.lang();
  }

  /** `lang()` flips only after the locale loaded, so no `computed()` translates against a missing
   *  table; `<html lang>`, storage and `selectedLang` update immediately. */
  setLang(lang: AppLang): void {
    document.documentElement.lang = lang;
    localStorage.setItem(LANG_STORAGE_KEY, lang);
    this.selectedLang.set(lang);

    const requestId = ++this.latestRequestId;

    this.translocoService.load(lang).subscribe({
      next: () => {
        if (requestId !== this.latestRequestId) {
          return;
        }
        this.lang.set(lang);
        this.translocoService.setActiveLang(lang);
      },
      error: () => {
        if (requestId !== this.latestRequestId) {
          return;
        }
        // Point all three back at the rendered language, or the next boot would pick the failed one
        // again and the switcher would stay stuck on a language nothing behind it renders.
        const renderedLang = this.lang();
        document.documentElement.lang = renderedLang;
        localStorage.setItem(LANG_STORAGE_KEY, renderedLang);
        this.selectedLang.set(renderedLang);
      },
    });
  }
}
