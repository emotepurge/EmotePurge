import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
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
    notice: { deletionUnconfirmed: 'Nichts gelöscht.' },
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
    auth.handleDeletionUnconfirmed();

    expect(render().querySelector('[role="alert"]')?.textContent).toContain('Nichts gelöscht.');
    expect(render().querySelector('[role="alert"]')).toBeNull();
  });

  it('shows no notice by default', () => {
    expect(render().querySelector('[role="alert"]')).toBeNull();
  });
});
