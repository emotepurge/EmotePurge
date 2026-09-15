import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { AppLang, LanguageService, SUPPORTED_LANGS } from '../../core/i18n/language.service';
import { THEME_PREFERENCES, ThemePreference, ThemeService } from '../../core/theme/theme.service';
import { SegmentedControl, SegmentedControlOption } from './segmented-control';

const THEME_OPTIONS: SegmentedControlOption[] = THEME_PREFERENCES.map((value) => ({
  value,
  labelKey: `theme.${value}`,
}));

const LANGUAGE_OPTIONS: SegmentedControlOption[] = SUPPORTED_LANGS.map((value) => ({
  value,
  labelKey: `languageSwitcher.${value}`,
}));

/**
 * Theme and language, the two personal display preferences, as one block. After this rebuild it is
 * the only place in the repo where either control exists — they used to be two components that the
 * shell, the landing page and the login page each carried a copy of, in two different layouts.
 *
 * Caption above the group rather than beside it: SegmentedControl takes text labels only, and three
 * theme labels at text-sm/px-3 do not fit next to a row caption in a 256 px panel. Across the full
 * width they do.
 *
 * Both options tables are derived from the services' own constants, so adding a fourth theme or a
 * third language needs a translation and nothing else here.
 */
@Component({
  selector: 'app-display-preferences',
  imports: [SegmentedControl, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-3 px-3 py-3">
      <div class="flex flex-col gap-1.5">
        <span class="text-xs font-medium text-fg-muted">{{ 'theme.label' | transloco }}</span>
        <app-segmented-control
          size="lg"
          [options]="themeOptions"
          [ariaLabel]="'theme.ariaLabel' | transloco"
          [value]="themeService.preference()"
          (valueChange)="setTheme($event)"
        />
      </div>

      <div class="flex flex-col gap-1.5">
        <span class="text-xs font-medium text-fg-muted">{{
          'languageSwitcher.label' | transloco
        }}</span>
        <app-segmented-control
          size="lg"
          [options]="languageOptions"
          [ariaLabel]="'languageSwitcher.ariaLabel' | transloco"
          [value]="languageService.selectedLang()"
          (valueChange)="setLanguage($event)"
        />
      </div>
    </div>
  `,
})
export class DisplayPreferences {
  protected readonly themeService = inject(ThemeService);
  protected readonly languageService = inject(LanguageService);

  protected readonly themeOptions = THEME_OPTIONS;
  protected readonly languageOptions = LANGUAGE_OPTIONS;

  // One-way in, explicit out rather than a two-way binding: both services persist the choice in
  // their setter, so writing the signal directly would change the UI and forget the preference.
  // The casts are safe because both option tables are built from the very constants they narrow to.
  protected setTheme(value: string): void {
    this.themeService.setPreference(value as ThemePreference);
  }

  protected setLanguage(value: string): void {
    this.languageService.setLang(value as AppLang);
  }
}
