import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { BotAccountState } from '../../core/admin/admin.model';
import { AdminBotAccountPage } from './admin-bot-account-page';

const STATE: BotAccountState = {
  tokenPresent: false,
  status: 'none',
  expiresAtUtc: null,
  expiresInDays: null,
  botSevenTvUserId: null,
  lastVerifiedAtUtc: null,
  lastErrorCode: null,
  killSwitch: false,
  pausedUntilUtc: null,
  wishes: {
    queue: { adds: 0, confirms: 0, removals: 0 },
    botChannels: 0,
    suspendedChannels: 0,
    openRepairChecks: 0,
  },
};

describe('AdminBotAccountPage — the token field', () => {
  let httpMock: HttpTestingController;
  let page: AdminBotAccountPage;
  // The protected members are the page's behaviour; reach them without widening the class.
  type Internals = {
    tokenInput: { set(v: string): void; (): string };
    canSubmit: () => boolean;
    submitToken(event: Event): void;
    errorMessage: () => string | null;
  };
  const internals = () => page as unknown as Internals;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    httpMock = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(AdminBotAccountPage);
    page = fixture.componentInstance;
    fixture.detectChanges();
    httpMock.expectOne('/api/admin/bot-account').flush(STATE);
    await fixture.whenStable();
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('keeps the send button locked while the field is empty', () => {
    expect(internals().canSubmit()).toBe(false);
    internals().tokenInput.set('a.b.c');
    expect(internals().canSubmit()).toBe(true);
  });

  it('empties the field after a successful send', () => {
    internals().tokenInput.set('a.b.c');
    internals().submitToken(new Event('submit'));
    expect(internals().tokenInput()).toBe('');
    const req = httpMock.expectOne('/api/admin/bot-account/token');
    expect(req.request.body).toEqual({ token: 'a.b.c' });
    req.flush(null, { status: 204, statusText: 'No Content' });
    TestBed.tick();
    httpMock.expectOne('/api/admin/bot-account').flush(STATE);
  });

  it('empties the field after a failed send and names the error code', () => {
    internals().tokenInput.set('nonsense');
    internals().submitToken(new Event('submit'));
    expect(internals().tokenInput()).toBe('');
    httpMock
      .expectOne('/api/admin/bot-account/token')
      .flush({ errorCode: 'bot_account_token_malformed' }, { status: 400, statusText: 'Bad' });
    expect(internals().errorMessage()).toBe('errors.api.bot_account_token_malformed');
  });
});
