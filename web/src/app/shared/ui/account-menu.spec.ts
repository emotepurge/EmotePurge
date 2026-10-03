import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Dialog } from '@angular/cdk/dialog';
import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { Subject, firstValueFrom, of } from 'rxjs';
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
    delete: {
      trigger: 'Konto löschen',
      title: 'Dein Konto unwiderruflich löschen',
      message: 'Das lässt sich nicht rückgängig machen.',
      inputLabel: 'Login eingeben',
      confirm: 'Konto endgültig löschen',
      pending: 'Konto wird gelöscht …',
      failed: 'Dein Konto konnte nicht gelöscht werden. Es ist alles unverändert.',
      unconfirmed:
        'Wir konnten nicht bestätigen, ob dein Konto gelöscht wurde. Lade die Seite neu.',
    },
  },
  common: {
    cancel: 'Abbrechen',
    typedConfirmHint: 'Zum Fortfahren exakt „{{text}}“ eingeben.',
  },
  errors: { status: { server: 'Serverfehler.' } },
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

  describe('delete account', () => {
    // The dialog lives in the CDK overlay container on <body>, outside the fixture's host element.
    const dialogEl = () => document.querySelector<HTMLElement>('app-typed-confirm-dialog');
    const dialogInput = () => document.querySelector<HTMLInputElement>('#typed-confirm-input')!;
    const dialogButton = (label: string) =>
      Array.from(dialogEl()!.querySelectorAll('button')).find(
        (candidate) => candidate.textContent?.trim() === label,
      )!;

    function type(value: string): void {
      const input = dialogInput();
      input.value = value;
      input.dispatchEvent(new Event('input'));
      TestBed.tick();
    }

    function openDialog(menu: Harness): void {
      menu.resolve(USER);
      menu.trigger().click();
      menu.detect();
      menu.button('Konto löschen').click();
      menu.detect();
      TestBed.tick();
    }

    afterEach(() => {
      // A dialog left open by a failing assertion would leak into the next spec through <body>.
      TestBed.inject(Dialog).closeAll();
    });

    it('closes the panel and opens a dialog that is locked until the login is retyped exactly', () => {
      const menu = render();
      openDialog(menu);

      expect(menu.panel()).toBeNull();
      expect(dialogEl()).not.toBeNull();
      expect(dialogButton('Konto endgültig löschen').disabled).toBe(true);

      type('Sensitron'); // display name, wrong case of nothing the user is asked for
      expect(dialogButton('Konto endgültig löschen').disabled).toBe(true);

      type('sensitron');
      expect(dialogButton('Konto endgültig löschen').disabled).toBe(false);
    });

    it('sends nothing and keeps the session when the dialog is cancelled', () => {
      const menu = render();
      const deleteAccount = vi.spyOn(authService, 'deleteAccount');
      openDialog(menu);

      dialogButton('Abbrechen').click();
      menu.detect();

      expect(deleteAccount).not.toHaveBeenCalled();
      expect(authService.currentUser()).toEqual(USER);
    });

    it('deletes through AuthService on confirmation and does not reopen the panel', () => {
      const menu = render();
      const deleteAccount = vi
        .spyOn(authService, 'deleteAccount')
        .mockReturnValue(of(undefined as void));
      openDialog(menu);

      type('sensitron');
      dialogButton('Konto endgültig löschen').click();
      menu.detect();

      expect(deleteAccount).toHaveBeenCalledOnce();
      expect(menu.panel()).toBeNull();
    });

    it('reopens the panel with an alert and keeps the session when the deletion fails', () => {
      const menu = render();
      const router = TestBed.inject(Router);
      const navigate = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);
      openDialog(menu);

      type('sensitron');
      dialogButton('Konto endgültig löschen').click();
      TestBed.inject(HttpTestingController)
        .expectOne({ method: 'DELETE', url: '/api/auth/me' })
        .flush(null, { status: 500, statusText: 'Internal Server Error' });
      menu.detect();

      expect(authService.currentUser()).toEqual(USER);
      expect(navigate).not.toHaveBeenCalled();
      const alert = menu.panel()!.querySelector('[role="alert"]');
      expect(alert?.textContent).toContain('Dein Konto konnte nicht gelöscht werden.');
      expect(alert?.textContent).toContain('Serverfehler.');
    });

    it('resets to the root view when the deletion fails, even if the user had moved to preferences', async () => {
      const menu = render();
      vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
      const pending = new Subject<void>();
      vi.spyOn(authService, 'deleteAccount').mockReturnValue(pending);
      openDialog(menu);
      type('sensitron');
      dialogButton('Konto endgültig löschen').click();
      menu.detect();

      // Reopen while pending and wander into the preferences view.
      menu.trigger().click();
      menu.detect();
      menu.button('Einstellungen').click();
      menu.detect();
      expect(menu.hasButton('Konto löschen')).toBe(false);

      pending.error(new HttpErrorResponse({ status: 500 }));
      menu.detect();
      await menu.fixture.whenStable();

      expect(menu.hasButton('Konto löschen')).toBe(true);
      const alert = menu.panel()!.querySelector<HTMLElement>('[role="alert"]');
      expect(alert?.textContent).toContain('Dein Konto konnte nicht gelöscht werden.');
      expect(document.activeElement).toBe(alert);
    });

    it('on a 401 resets the client for the login page and shows no deletion-failed notice', () => {
      const menu = render();
      const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
      openDialog(menu);
      type('sensitron');
      dialogButton('Konto endgültig löschen').click();
      TestBed.inject(HttpTestingController)
        .expectOne({ method: 'DELETE', url: '/api/auth/me' })
        .flush(null, { status: 401, statusText: 'Unauthorized' });
      menu.detect();

      expect(authService.currentUser()).toBeNull();
      expect(navigate).toHaveBeenCalledWith('/login');
      expect(authService.takeLoginNotice()).toBe('deletionUnconfirmed');
      expect(menu.panel()).toBeNull();
    });

    function confirmDeletion(menu: Harness): void {
      openDialog(menu);
      type('sensitron');
      dialogButton('Konto endgültig löschen').click();
    }

    it.each([0, 502, 503, 504])(
      'on status %i says the outcome is unknown, never "nothing has changed", and keeps the session',
      async (status) => {
        const menu = render();
        const navigate = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
        confirmDeletion(menu);
        TestBed.inject(HttpTestingController)
          .expectOne({ method: 'DELETE', url: '/api/auth/me' })
          .error(new ProgressEvent('error'), { status, statusText: 'x' });
        menu.detect();
        await menu.fixture.whenStable();

        expect(authService.currentUser()).toEqual(USER);
        expect(navigate).not.toHaveBeenCalled();
        const alert = menu.panel()!.querySelector<HTMLElement>('[role="alert"]')!;
        expect(alert.textContent).toContain('nicht bestätigen');
        expect(alert.textContent).not.toContain('unverändert');
        expect(document.activeElement).toBe(alert);
      },
    );

    it('keeps the outcome after the menu is destroyed mid-request and shows it on a recreated menu', async () => {
      const menu = render();
      const pending = new Subject<void>();
      vi.spyOn(authService, 'deleteAccount').mockReturnValue(pending);
      confirmDeletion(menu);
      menu.detect();

      menu.fixture.destroy(); // the visitor navigated to another page
      expect(() => pending.error(new HttpErrorResponse({ status: 0 }))).not.toThrow();

      const second = render(); // the next page's menu; /me is cached, so no request
      second.detect();
      await second.fixture.whenStable();
      expect(second.panel()!.querySelector('[role="alert"]')?.textContent).toContain(
        'nicht bestätigen',
      );
    });

    it('moves focus to the alert when the deletion fails', async () => {
      const menu = render();
      vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
      openDialog(menu);
      type('sensitron');
      dialogButton('Konto endgültig löschen').click();
      TestBed.inject(HttpTestingController)
        .expectOne({ method: 'DELETE', url: '/api/auth/me' })
        .flush(null, { status: 500, statusText: 'Internal Server Error' });
      menu.detect();
      await menu.fixture.whenStable();

      const alert = menu.panel()!.querySelector<HTMLElement>('[role="alert"]');
      expect(document.activeElement).toBe(alert);
    });

    it('shows progress and blocks a second submission while the request is in flight', () => {
      const menu = render();
      const pending = new Subject<void>();
      const deleteAccount = vi.spyOn(authService, 'deleteAccount').mockReturnValue(pending);
      openDialog(menu);
      type('sensitron');
      dialogButton('Konto endgültig löschen').click();
      menu.detect();

      menu.trigger().click(); // reopen while the request runs
      menu.detect();
      const row = menu.button('Konto wird gelöscht …');
      expect(row.disabled).toBe(true);
      expect(row.getAttribute('aria-busy')).toBe('true');
      row.click();
      expect(deleteAccount).toHaveBeenCalledOnce();

      pending.error(new HttpErrorResponse({ status: 500 }));
      menu.detect();
      expect(menu.button('Konto löschen').disabled).toBe(false);
    });

    it('clears the failure notice once the panel is closed and opened again', () => {
      const menu = render();
      vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
      openDialog(menu);
      type('sensitron');
      dialogButton('Konto endgültig löschen').click();
      TestBed.inject(HttpTestingController)
        .expectOne({ method: 'DELETE', url: '/api/auth/me' })
        .flush(null, { status: 500, statusText: 'Internal Server Error' });
      menu.detect();

      menu.trigger().click(); // close
      menu.detect();
      menu.trigger().click(); // reopen
      menu.detect();

      expect(menu.panel()!.querySelector('[role="alert"]')).toBeNull();
    });
  });
});
