import { DestroyRef, Injectable, computed, effect, inject, signal } from '@angular/core';

export const THEME_PREFERENCES = ['system', 'light', 'dark'] as const;
export type ThemePreference = (typeof THEME_PREFERENCES)[number];
export type ResolvedTheme = 'light' | 'dark';

const THEME_STORAGE_KEY = 'emotepurge.theme';
const DARK_QUERY = '(prefers-color-scheme: dark)';

function isThemePreference(value: string | null): value is ThemePreference {
  return (THEME_PREFERENCES as readonly string[]).includes(value ?? '');
}

/**
 * Three states, not two. A plain light/dark toggle cannot express "follow the system", so anyone
 * who flipped it once would be stuck with that choice forever — including through a system-wide
 * change they made deliberately. Absent or unparseable storage means `'system'`.
 */
export function resolveInitialPreference(): ThemePreference {
  const stored = localStorage.getItem(THEME_STORAGE_KEY);
  return isThemePreference(stored) ? stored : 'system';
}

/**
 * Owns the display theme: the user's preference, the resolved light/dark that follows from it, and
 * the two places it has to land in the DOM (`<html data-theme>` and the `theme-color` metas).
 *
 * `data-theme` sits on `<html>` rather than on the app shell's root element because the CDK overlay
 * container is a sibling of the app, outside the shell's DOM — dialogs and popovers would otherwise
 * keep whichever mode they were not opened in (design doc §7, CDK trap 1).
 *
 * `public/theme-init.js` has already stamped the same attribute before the first paint; this
 * service does not re-derive it so much as take it over. The one thing the guard script cannot do
 * is the `theme-color` correction below, which needs to know whether the choice was explicit.
 *
 * Storing the preference in `localStorage` does not conflict with "the auth session never goes into
 * localStorage" (web/.claude/CLAUDE.md) — this is a display preference, not session state.
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly destroyRef = inject(DestroyRef);

  private readonly darkQuery = window.matchMedia(DARK_QUERY);

  readonly preference = signal<ThemePreference>(resolveInitialPreference());

  /** A signal rather than a read of `darkQuery.matches` on demand: a `computed()` that reaches
   *  through an object into mutable state never re-evaluates when that state changes (root
   *  CLAUDE.md rule 14). The listener below is what keeps it honest. */
  private readonly systemPrefersDark = signal(this.darkQuery.matches);

  readonly resolved = computed<ResolvedTheme>(() => {
    const preference = this.preference();
    if (preference !== 'system') {
      return preference;
    }
    return this.systemPrefersDark() ? 'dark' : 'light';
  });

  constructor() {
    // A local closure rather than a field: add and remove have to hand over the identical
    // reference, and keeping the pair in one scope is what makes that obvious at a glance.
    const onSystemChange = (event: MediaQueryListEvent) =>
      this.systemPrefersDark.set(event.matches);
    this.darkQuery.addEventListener('change', onSystemChange);
    this.destroyRef.onDestroy(() => this.darkQuery.removeEventListener('change', onSystemChange));

    effect(() => this.apply(this.resolved(), this.preference() !== 'system'));
  }

  setPreference(preference: ThemePreference): void {
    this.preference.set(preference);
    localStorage.setItem(THEME_STORAGE_KEY, preference);
  }

  private apply(theme: ResolvedTheme, isExplicit: boolean): void {
    document.documentElement.dataset['theme'] = theme;

    // Only the `media` attributes move; the colours stay in index.html so they exist in exactly one
    // place. `not all` never matches, which is how the losing tag is taken out of the running when
    // the user's choice contradicts their system preference.
    for (const mode of ['dark', 'light'] as const) {
      const tag = document.querySelector<HTMLMetaElement>(
        `meta[name="theme-color"][data-theme-mode="${mode}"]`,
      );
      if (!tag) {
        continue;
      }
      if (!isExplicit) {
        tag.media = `(prefers-color-scheme: ${mode})`;
        continue;
      }
      tag.media = mode === theme ? 'all' : 'not all';
    }
  }
}
