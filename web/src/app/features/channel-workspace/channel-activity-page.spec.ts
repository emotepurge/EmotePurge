import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { ChannelActivityPage } from './channel-activity-page';

const EMPTY = { items: [], page: 1, pageSize: 25, totalCount: 0, totalPages: 0 };
const BASE = '/channels/sensitron/activity';

describe('ChannelActivityPage — the action filter', () => {
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
        provideRouter(
          [{ path: 'channels/:channelName/activity', component: ChannelActivityPage }],
          withComponentInputBinding(),
        ),
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
    return httpMock.match((req) => req.url === '/api/channels/sensitron/audit-log');
  }

  it('sends a known ?action= to the API', async () => {
    await harness.navigateByUrl(BASE + '?action=voteSession.delete', ChannelActivityPage);
    harness.detectChanges();
    const [first] = requests();

    expect(first.request.params.get('action')).toBe('voteSession.delete');
    first.flush(EMPTY);
  });

  it('treats an unknown or user-scoped ?action= as no filter in the request and in the URL', async () => {
    await harness.navigateByUrl(BASE + '?action=user.delete', ChannelActivityPage);
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
    await harness.navigateByUrl(BASE + '?page=3', ChannelActivityPage);
    harness.detectChanges();
    requests().forEach((request) => !request.cancelled && request.flush(EMPTY));

    const root = harness.fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('app-audit-action-menu button')!.click();
    harness.detectChanges();
    const radio = Array.from(root.querySelectorAll<HTMLButtonElement>('[role="radio"]')).find(
      (option) => option.textContent?.includes('audit.actions.channelJoin'),
    );
    radio!.click();
    await settle();

    expect(router.url).toContain('action=channel.join');
    expect(router.url).not.toContain('page=');
  });

  it('resets both filters from the reset button', async () => {
    await harness.navigateByUrl(BASE + '?action=channel.join', ChannelActivityPage);
    harness.detectChanges();
    requests().forEach((request) => !request.cancelled && request.flush(EMPTY));
    await settle();
    requests().forEach((request) => !request.cancelled && request.flush(EMPTY));
    harness.detectChanges();

    const root = harness.fixture.nativeElement as HTMLElement;
    const reset = Array.from(root.querySelectorAll<HTMLButtonElement>('button')).find((button) =>
      button.textContent?.includes('channelWorkspace.activity.filter.reset'),
    );
    reset!.click();
    await settle();

    expect(router.url).not.toContain('action=');
  });
});
