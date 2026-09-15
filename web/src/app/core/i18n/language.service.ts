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

  /** The language last passed to `setLang`, so a load overtaken by a later switch can drop itself. */
  private requestedLang: AppLang = this.lang();

  constructor() {
    document.documentElement.lang = this.lang();
  }

  /** `lang()` flips only after the locale loaded, so no `computed()` translates against a missing
   *  table; `<html lang>` and storage update immediately. */
  setLang(lang: AppLang): void {
    document.documentElement.lang = lang;
    localStorage.setItem(LANG_STORAGE_KEY, lang);
    this.requestedLang = lang;

    this.translocoService.load(lang).subscribe({
      next: () => {
        if (this.requestedLang !== lang) {
          return;
        }
        this.lang.set(lang);
        this.translocoService.setActiveLang(lang);
      },
      error: () => {
        if (this.requestedLang !== lang) {
          return;
        }
        // Point both back at the rendered language, or the next boot would pick the failed one again.
        const renderedLang = this.lang();
        document.documentElement.lang = renderedLang;
        localStorage.setItem(LANG_STORAGE_KEY, renderedLang);
      },
    });
  }
}
