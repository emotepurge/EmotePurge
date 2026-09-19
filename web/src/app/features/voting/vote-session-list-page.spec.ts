/**
 * The first spec for `VoteSessionListPage`. What is under test is the create-entry lock that
 * replaced the old inline form (see the class's/template's own comments): a manager gets a link to
 * the usage-stats page in the page header, a plain voter gets none — hidden, not merely disabled,
 * because a voter cannot reach the destination's write path either way. The empty state follows the
 * same split: only a manager sees the two-sentence how-to, a voter sees the plain "nothing here yet"
 * notice.
 *
 * The real template is rendered (unlike usage-stats-page.spec.ts's stubbed one) — there is no
 * `viewChild.required<ElementRef>` here that would need a stand-in element, and the whole point of
 * this spec is what actually reaches the DOM under each permission. `langs: { de: {} }` is
 * deliberate, the same choice `usage-stats-page.spec.ts` makes: Transloco's default missing-key
 * fallback renders the key path itself, which is enough to tell `noSessionsManagerHint` and
 * `noSessionsVoterHint` apart without pinning their translated wording (Regel 12 — wording is not
 * the subject under test).
 */
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, describe, expect, it } from 'vitest';

import { PagedResult } from '../../core/models/paged-result.model';
import { VoteSessionSummary } from '../../core/voting/vote-session.model';
import { VoteSessionListPage } from './vote-session-list-page';

const CHANNEL = 'sensitron';

function emptyPage(): PagedResult<VoteSessionSummary> {
  return { items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 };
}

/** Same helper as vote-session-detail-page.spec.ts: `HttpTestingController.match` compares against
 *  the request's plain `url` (no query string), which is exactly how both endpoints here are meant. */
function flushByPath(mock: HttpTestingController, path: string, body: unknown): void {
  mock.match((req) => req.url === path).forEach((testReq) => testReq.flush(body as object));
}

describe('VoteSessionListPage — the create entry point is a manager-only lock', () => {
  let fixture: ComponentFixture<VoteSessionListPage>;
  let httpMock: HttpTestingController;

  /** Drives the page through its initial mount: the session list and the permissions probe both
   *  `rxResource`s fire on first run, keyed on `canManage` alone since neither test cares about the
   *  sessions themselves. */
  async function mount(canManage: boolean): Promise<void> {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });

    fixture = TestBed.createComponent(VoteSessionListPage);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.componentRef.setInput('channelName', CHANNEL);
    fixture.detectChanges();

    flushByPath(httpMock, `/api/channels/${CHANNEL}/vote-sessions`, emptyPage());
    flushByPath(httpMock, `/api/channels/${CHANNEL}/permissions`, {
      canManage,
      canViewUsageStats: true,
      isGlobalAdmin: false,
      isTracked: true,
      isBotActive: true,
    });
    // rxResource commits a flushed observable's emission to its signal asynchronously (a microtask
    // beyond the flush itself), so a synchronous detectChanges() right after flush still renders the
    // pre-resolution state — whenStable() is what actually waits for it.
    await fixture.whenStable();
    fixture.detectChanges();
  }

  afterEach(() => {
    httpMock.verify();
  });

  it('renders the usage-stats entry link in the page header for a manager', async () => {
    await mount(true);

    const link: HTMLAnchorElement | null = fixture.nativeElement.querySelector(
      `a[href="/channels/${CHANNEL}/usage-stats"]`,
    );
    expect(link).not.toBeNull();
  });

  it('hides the entry link entirely for a plain voter, not merely disables it', async () => {
    await mount(false);

    const link = fixture.nativeElement.querySelector(`a[href="/channels/${CHANNEL}/usage-stats"]`);
    expect(link).toBeNull();
  });

  it('shows the manager how-to in the empty state, keyed by the untranslated fallback text', async () => {
    await mount(true);

    expect(fixture.nativeElement.textContent).toContain('voting.list.noSessionsManagerHint');
    expect(fixture.nativeElement.textContent).not.toContain('voting.list.noSessionsVoterHint');
  });

  it('shows only the plain empty notice to a voter, never the manager how-to', async () => {
    await mount(false);

    expect(fixture.nativeElement.textContent).toContain('voting.list.noSessionsVoterHint');
    expect(fixture.nativeElement.textContent).not.toContain('voting.list.noSessionsManagerHint');
  });
});
