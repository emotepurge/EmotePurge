import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AdminAuditLogPage } from './admin-audit-log-page';

const EMPTY = { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 };

describe('AdminAuditLogPage — the action filter', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'admin/audit-log', component: AdminAuditLogPage }]),
        provideLocationMocks(),
      ],
    });
    harness = await RouterTestingHarness.create();
    router = TestBed.inject(Router);
    router.setUpLocationChangeListener();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    requests().forEach((request) => !request.cancelled && request.flush(EMPTY));
    httpMock.verify();
  });

  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    harness.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));
    harness.detectChanges();
  }

  function requests() {
    return httpMock.match((req) => req.url === '/api/admin/audit-log');
  }

  it('sends a known ?action= to the API', async () => {
    await harness.navigateByUrl('/admin/audit-log?action=channel.join', AdminAuditLogPage);
    harness.detectChanges();
    const [first] = requests();

    expect(first.request.params.get('action')).toBe('channel.join');
    first.flush(EMPTY);
  });

  it('treats an unknown ?action= as no filter in the request and in the URL', async () => {
    await harness.navigateByUrl('/admin/audit-log?action=typo', AdminAuditLogPage);
    harness.detectChanges();
    await settle();

    const sent = requests();
    expect(sent.length).toBeGreaterThan(0);
    for (const request of sent) {
      expect(request.request.params.has('action')).toBe(false);
      !request.cancelled && request.flush(EMPTY);
    }
    expect(router.url).not.toContain('action=');
  });

  it('writes the chosen action into the URL and resets to page 1', async () => {
    await harness.navigateByUrl('/admin/audit-log?page=3', AdminAuditLogPage);
    harness.detectChanges();
    requests().forEach((request) => !request.cancelled && request.flush(EMPTY));

    const root = harness.fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('app-audit-action-menu button')!.click();
    harness.detectChanges();
    const radio = Array.from(root.querySelectorAll<HTMLButtonElement>('[role="radio"]')).find(
      (option) => option.textContent?.includes('audit.actions.channelPurge'),
    );
    radio!.click();
    await settle();

    expect(router.url).toContain('action=channel.purge');
    expect(router.url).not.toContain('page=');
  });
});
