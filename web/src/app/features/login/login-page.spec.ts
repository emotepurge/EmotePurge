import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { AuthService } from '../../core/auth/auth.service';
import { LoginPage } from './login-page';

const TRANSLATIONS = {
  login: {
    title: 'Anmelden',
    subtitle: 's',
    loginButton: 'Mit Twitch einloggen',
    appButton: 'Zur App',
    titleLoggedIn: 'Angemeldet',
    subtitleLoggedIn: 'Hallo {{name}}',
    notice: {
      deletionSessionEnded: 'Nichts gelöscht.',
      deletionUnknown: 'Unklar.',
      deletionPending: 'Wird noch verarbeitet.',
    },
    scopes: { title: 't', moderatedChannels: 'm', subscriptions: 's', note: 'n' },
  },
};

describe('LoginPage notice', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  function render(): HTMLElement {
    const fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('shows an alert with the unconfirmed-deletion notice once, and not on a later visit', () => {
    const auth = TestBed.inject(AuthService);
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    auth.handleDeletionSessionEnded();

    expect(render().querySelector('[role="alert"]')?.textContent).toContain('Nichts gelöscht.');
    expect(render().querySelector('[role="alert"]')).toBeNull();
  });

  it('shows a notice that arrives after the page exists, e.g. from a deletion still pending when the session ended', () => {
    const fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[role="alert"]')).toBeNull();

    const auth = TestBed.inject(AuthService);
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    auth.startAccountDeletion('1'); // nobody is signed in: the menu could not show the outcome
    TestBed.inject(HttpTestingController)
      .expectOne('/api/auth/me?expectedTwitchUserId=1')
      .error(new ProgressEvent('error'), { status: 0, statusText: '' });
    fixture.detectChanges();

    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Unklar.');
  });

  it('disables sign-in while a deletion is pending, names the reason, and re-enables it afterwards', () => {
    const auth = TestBed.inject(AuthService);
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);
    auth.currentUser.set({ twitchUserId: '1' } as never);
    auth.startAccountDeletion('1');
    auth.handleSessionExpired(); // another request ended the session while the DELETE runs

    const fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    const button = el.querySelector('main button') as HTMLButtonElement;
    const hrefBefore = window.location.href;

    expect(button.getAttribute('aria-disabled')).toBe('true');
    const reason = el.querySelector(`#${button.getAttribute('aria-describedby')}`);
    expect(reason?.textContent).toContain('Wird noch verarbeitet.');
    button.click();
    expect(window.location.href).toBe(hrefBefore);

    TestBed.inject(HttpTestingController)
      .expectOne('/api/auth/me?expectedTwitchUserId=1')
      .error(new ProgressEvent('error'), { status: 0, statusText: '' });
    fixture.detectChanges();

    expect(button.getAttribute('aria-disabled')).toBeNull();
    expect(el.querySelector('[role="alert"]')?.textContent).toContain('Unklar.');
  });

  it('shows no notice by default', () => {
    expect(render().querySelector('[role="alert"]')).toBeNull();
  });
});

describe('LoginPage for a signed-in visitor', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
  });

  function render(): HTMLElement {
    const fixture = TestBed.createComponent(LoginPage);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function resolveAs(user: { twitchUserId: string; displayName: string } | null): void {
    TestBed.inject(AuthService).ensureLoaded().subscribe();
    const req = TestBed.inject(HttpTestingController).expectOne('/api/auth/me');
    if (user) {
      req.flush(user);
    } else {
      req.flush(null, { status: 401, statusText: 'Unauthorized' });
    }
  }

  it('offers a link into the app instead of the login button, and greets by display name', () => {
    resolveAs({ twitchUserId: '1', displayName: 'Streamer_Name' });
    const el = render();

    const link = Array.from(el.querySelectorAll('main a')).find(
      (a) => a.textContent?.trim() === 'Zur App',
    );
    expect(link?.getAttribute('href')).toBe('/');
    expect(el.querySelector('main button')).toBeNull();
    expect(el.querySelector('main')?.textContent).toContain('Streamer_Name');
  });

  it('keeps the login button while the answer is pending, even if a user is already set', () => {
    TestBed.inject(AuthService).currentUser.set({
      twitchUserId: '1',
      displayName: 'Streamer_Name',
    } as never);
    const el = render();

    expect(el.querySelector('main button')).not.toBeNull();
    expect(
      Array.from(el.querySelectorAll('main a')).some((a) => a.getAttribute('href') === '/'),
    ).toBe(false);
  });

  it('keeps the login button for a resolved anonymous visitor', () => {
    resolveAs(null);
    const el = render();

    expect(el.querySelector('main button')).not.toBeNull();
    expect(
      Array.from(el.querySelectorAll('main a')).some((a) => a.getAttribute('href') === '/'),
    ).toBe(false);
  });
});
