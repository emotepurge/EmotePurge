import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AuthUser } from '../../core/auth/auth.model';
import { AuthService } from '../../core/auth/auth.service';
import { AccountMenu } from './account-menu';

// Only the keys this panel and its embedded display-preferences translate — the real German and
// English strings, so a selector built from them (`button`/`radio`) reads as the sentence the user
// gets rather than as a key, same convention as import-confirm-dialog.spec.ts. `account.trigger`
// and `account.preferencesTrigger` double as both the trigger's aria-label and, for the latter, the
// visible text of two different rows (root's "preferences" row, preferences' "back" row) — the
// component's own doc comment on `triggerLabel` calls this out.
const DE_TRANSLATIONS = {
  account: {
    trigger: 'Konto-Menü von {{ name }}',
    preferencesTrigger: 'Einstellungen',
  },
  shell: {
    admin: 'Admin',
    logout: 'Logout',
  },
  theme: {
    label: 'Darstellung',
    ariaLabel: 'Darstellung wählen',
    system: 'System',
    light: 'Hell',
    dark: 'Dunkel',
  },
  languageSwitcher: {
    label: 'Sprache',
    ariaLabel: 'Sprache wählen',
    de: 'Deutsch',
    en: 'English',
  },
};

const EN_TRANSLATIONS = {
  account: {
    trigger: 'Account menu for {{ name }}',
    preferencesTrigger: 'Settings',
  },
  shell: {
    admin: 'Admin',
    logout: 'Logout',
  },
  theme: {
    label: 'Appearance',
    ariaLabel: 'Choose appearance',
    system: 'System',
    light: 'Light',
    dark: 'Dark',
  },
  languageSwitcher: {
    label: 'Language',
    ariaLabel: 'Choose language',
    de: 'Deutsch',
    en: 'English',
  },
};

const USER: AuthUser = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2026-07-28T00:00:00Z',
  isGlobalAdmin: false,
  profileImageUrl: null,
};

/**
 * `#outside` stands in for "focus was somewhere else on the page" when the panel closes — the
 * negative case for the trigger's focus-return rule. `app-account-menu` itself takes no inputs, so
 * a host wrapper only exists to give that sibling something real to sit next to.
 */
@Component({
  imports: [AccountMenu],
  template: `
    <button id="outside" type="button">Outside</button>
    <app-account-menu />
  `,
})
class Host {}

interface Harness {
  fixture: ComponentFixture<Host>;
  /** Answers the one `/api/auth/me` request the component's constructor fires. */
  resolve(user: AuthUser | null): void;
  trigger(): HTMLButtonElement;
  panel(): HTMLElement | null;
  /** Resolves the panel's accessible name the way assistive tech would: an `aria-labelledby`
   *  reference (resolved against the DOM, not just compared as an id string) wins over
   *  `aria-label` when both are present. */
  panelAccessibleName(): string | null;
  outside(): HTMLButtonElement;
  button(label: string): HTMLButtonElement;
  hasButton(label: string): boolean;
  radio(groupAriaLabel: string, label: string): HTMLButtonElement;
  detect(): void;
}

describe('AccountMenu', () => {
  let authService: AuthService;
  // The Angular/Vitest runner shares one jsdom document across spec files (isolate:false) — the
  // language-switch test below exercises the real LanguageService.setLang('en'), which writes
  // document.documentElement.lang. Captured before this test touches anything, so afterEach can put
  // it back rather than leaking 'en' into whichever spec file runs next.
  let originalDocumentLang: string;

  beforeEach(async () => {
    originalDocumentLang = document.documentElement.lang;

    // LanguageService.resolveInitialLang() reads localStorage first and navigator.language next —
    // both must be pinned, or a leftover stored value (or this environment's default 'en-US') would
    // silently pre-select English before the panel ever opens, same setup as language.service.spec.ts.
    localStorage.clear();
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('de-DE');

    await TestBed.configureTestingModule({
      imports: [
        Host,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS, en: EN_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    authService = TestBed.inject(AuthService);
  });

  afterEach(() => {
    // Restore and clean up the shared-document state first, unconditionally — if verify() below
    // throws (a genuinely unflushed request), the navigator.language spy and the lang/localStorage
    // side effects must still not survive into the next spec file.
    vi.restoreAllMocks();
    localStorage.clear();
    document.documentElement.lang = originalDocumentLang;

    TestBed.inject(HttpTestingController).verify();
  });

  function render(): Harness {
    const fixture = TestBed.createComponent(Host);
    const httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function buttons(): HTMLButtonElement[] {
      return Array.from(host.querySelectorAll('button'));
    }

    return {
      fixture,
      resolve: (user) => {
        httpMock.expectOne('/api/auth/me').flush(user);
        fixture.detectChanges();
      },
      trigger: () => host.querySelector<HTMLButtonElement>('button[aria-haspopup="dialog"]')!,
      panel: () => host.querySelector<HTMLElement>('[role="dialog"]'),
      panelAccessibleName: () => {
        const panelEl = host.querySelector<HTMLElement>('[role="dialog"]');
        if (!panelEl) {
          return null;
        }
        const labelledBy = panelEl.getAttribute('aria-labelledby');
        if (labelledBy) {
          return labelledBy
            .split(/\s+/)
            .map((id) => host.querySelector(`#${id}`)?.textContent?.trim() ?? '')
            .join(' ')
            .trim();
        }
        return panelEl.getAttribute('aria-label');
      },
      outside: () => host.querySelector<HTMLButtonElement>('#outside')!,
      button: (label) => {
        const found = buttons().find((candidate) => candidate.textContent?.trim() === label);
        if (!found) {
          throw new Error(`no button labelled "${label}"`);
        }
        return found;
      },
      hasButton: (label) => buttons().some((candidate) => candidate.textContent?.trim() === label),
      radio: (groupAriaLabel, label) => {
        const group = Array.from(host.querySelectorAll('[role="radiogroup"]')).find(
          (candidate) => candidate.getAttribute('aria-label') === groupAriaLabel,
        );
        const found = group
          ? Array.from(group.querySelectorAll<HTMLButtonElement>('[role="radio"]')).find(
              (candidate) => candidate.textContent?.trim() === label,
            )
          : undefined;
        if (!found) {
          throw new Error(`no radio labelled "${label}" in group "${groupAriaLabel}"`);
        }
        return found;
      },
      detect: () => fixture.detectChanges(),
    };
  }

  describe('isOpen/view state machine', () => {
    it('opens the panel on trigger click into the root view and reflects that in aria-expanded', () => {
      const menu = render();
      menu.resolve(USER);

      expect(menu.trigger().getAttribute('aria-expanded')).toBe('false');
      expect(menu.panel()).toBeNull();

      menu.trigger().click();
      menu.detect();

      expect(menu.trigger().getAttribute('aria-expanded')).toBe('true');
      expect(menu.panel()).not.toBeNull();
      // Root view: both the row that leads into preferences and the logout row are present.
      expect(menu.hasButton('Einstellungen')).toBe(true);
      expect(menu.hasButton('Logout')).toBe(true);
    });

    it('disables the trigger and keeps the panel closed until auth has resolved', () => {
      const menu = render();

      expect(menu.trigger().disabled).toBe(true);

      // A disabled button fires no click event — the guard is enforced by the DOM itself, not by a
      // check inside toggle().
      menu.trigger().click();
      menu.detect();
      expect(menu.panel()).toBeNull();

      menu.resolve(USER);
      expect(menu.trigger().disabled).toBe(false);
    });

    it("switches into the preferences view and back without closing, moving focus to each view's entry point", async () => {
      const menu = render();
      menu.resolve(USER);
      menu.trigger().click();
      menu.detect();

      menu.button('Einstellungen').click(); // preferencesRow -> view: 'preferences'
      menu.detect();
      await menu.fixture.whenStable();

      expect(menu.panel()).not.toBeNull(); // still open
      expect(menu.trigger().getAttribute('aria-expanded')).toBe('true');
      expect(menu.hasButton('Logout')).toBe(false); // root-only row is gone
      // The back row is the only button rendered before the language/theme controls in this view.
      expect(document.activeElement).toBe(menu.button('Einstellungen'));

      menu.button('Einstellungen').click(); // back -> view: 'root'
      menu.detect();
      await menu.fixture.whenStable();

      expect(menu.hasButton('Logout')).toBe(true); // root view restored
      expect(document.activeElement).toBe(menu.button('Einstellungen')); // preferencesRow refocused
    });

    it('closes on a second trigger click and always reopens on the root view, even after being left on preferences', () => {
      const menu = render();
      menu.resolve(USER);
      menu.trigger().click();
      menu.detect();
      menu.button('Einstellungen').click(); // -> preferences
      menu.detect();
      expect(menu.hasButton('Logout')).toBe(false); // confirms it actually reached preferences

      menu.trigger().click(); // close
      menu.detect();

      expect(menu.panel()).toBeNull();
      expect(menu.trigger().getAttribute('aria-expanded')).toBe('false');

      menu.trigger().click(); // reopen
      menu.detect();

      expect(menu.hasButton('Logout')).toBe(true); // root, not the preferences view it was left on
    });
  });

  describe('focus return on close', () => {
    it('returns focus to the trigger when the panel closes while focus was inside it — even from the preferences subview', async () => {
      const menu = render();
      menu.resolve(USER);
      menu.trigger().click();
      menu.detect();
      menu.button('Einstellungen').click(); // preferences view; focus moves to the back row
      menu.detect();
      await menu.fixture.whenStable();
      expect(document.activeElement).toBe(menu.button('Einstellungen')); // sanity: inside the panel

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      menu.detect();

      expect(menu.panel()).toBeNull();
      expect(document.activeElement).toBe(menu.trigger());
    });

    it('leaves focus untouched when the panel closes while focus was outside it entirely', () => {
      const menu = render();
      menu.resolve(USER);
      menu.trigger().click();
      menu.detect();

      menu.outside().focus();
      expect(document.activeElement).toBe(menu.outside());

      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      menu.detect();

      expect(menu.panel()).toBeNull();
      expect(document.activeElement).toBe(menu.outside());
    });
  });

  describe('accessible name', () => {
    it('names the trigger from the generic settings label before resolution, then from the resolved user (interpolated)', () => {
      const menu = render();

      expect(menu.trigger().getAttribute('aria-label')).toBe('Einstellungen');

      menu.resolve(USER);

      expect(menu.trigger().getAttribute('aria-label')).toBe('Konto-Menü von Sensitron');
    });

    it('keeps the generic settings label once resolved logged-out, and the panel it opens holds no account-only rows', () => {
      const menu = render();
      menu.resolve(null);

      expect(menu.trigger().getAttribute('aria-label')).toBe('Einstellungen');

      // The assertions above never opened the panel, so they proved nothing about its contents —
      // open it and look at what actually rendered.
      menu.trigger().click();
      menu.detect();

      expect(menu.panel()).not.toBeNull();
      // account-menu.ts's logged-out branch renders only `<app-display-preferences />`: no Logout
      // row, and no row into a preferences subview either — display-preferences puts its theme and
      // language pickers directly in the root panel as role="radio" segmented controls, which
      // hasButton (a plain <button> text match) correctly does not see.
      expect(menu.hasButton('Logout')).toBe(false);
      expect(menu.hasButton('Einstellungen')).toBe(false);
      expect(menu.panelAccessibleName()).toBe('Einstellungen');
    });

    it("keeps the trigger's and panel's accessible name in sync with a language switched from inside the panel itself", () => {
      const menu = render();
      menu.resolve(USER);
      menu.trigger().click();
      menu.detect();
      menu.button('Einstellungen').click(); // preferences view holds the language control
      menu.detect();

      menu.radio('Sprache wählen', 'English').click();
      menu.detect();

      expect(menu.trigger().getAttribute('aria-label')).toBe('Account menu for Sensitron');
      // Passed straight through to app-popover's own [ariaLabel] — same computed, same value.
      expect(menu.panel()!.getAttribute('aria-label')).toBe('Account menu for Sensitron');
    });
  });

  describe('logout', () => {
    it('closes the panel and delegates to AuthService when the logout row is activated', () => {
      const menu = render();
      menu.resolve(USER);
      menu.trigger().click();
      menu.detect();

      const logout = vi.spyOn(authService, 'logout').mockImplementation(() => {});

      menu.button('Logout').click();
      menu.detect();

      expect(menu.panel()).toBeNull();
      expect(logout).toHaveBeenCalledOnce();
    });
  });
});
