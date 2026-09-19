/**
 * The first spec for `VoteSessionListPage`. What is under test is the create-entry lock that
 * replaced the old inline form (see the class's/template's own comments): a manager gets a link to
 * the usage-stats page in the page header, a plain voter gets none — hidden, not merely disabled,
 * because a voter cannot reach the destination's write path either way.
 *
 * The how-to that explains that link (`voting.list.createEntryHint`) used to live only inside the
 * empty state, which meant it vanished the moment the first session existed — exactly when a
 * manager creating a second or third one would look for it again. The load-bearing case below is
 * `keeps showing the create-entry hint once the list is no longer empty`: it fails against the old
 * template (the hint lived inside the `sessions().length === 0` branch) and is the regression test
 * for that report. The empty state itself is now the same plain notice for a manager as for a
 * voter — no second copy of the how-to inside it.
 *
 * The real template is rendered (unlike usage-stats-page.spec.ts's stubbed one) — there is no
 * `viewChild.required<ElementRef>` here that would need a stand-in element, and the whole point of
 * this spec is what actually reaches the DOM under each permission. `langs: { de: {} }` is
 * deliberate, the same choice `usage-stats-page.spec.ts` makes: Transloco's default missing-key
 * fallback renders the key path itself, which is enough to tell `createEntryHint` and
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

function pageWith(items: VoteSessionSummary[]): PagedResult<VoteSessionSummary> {
  return {
    items,
    page: 1,
    pageSize: 20,
    totalCount: items.length,
    totalPages: items.length ? 1 : 0,
  };
}

function oneSession(): VoteSessionSummary {
  return {
    id: 1,
    title: 'Aufräumen im August',
    allowedVoterRoles: 1,
    isActive: true,
    startedAt: '2026-09-01T00:00:00Z',
    endedAt: null,
    emoteCount: null,
    hideResultsUntilEnd: false,
  };
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
   *  `rxResource`s fire on first run. `sessions` defaults to empty for the tests that don't care —
   *  the hint tests below pass a non-empty list deliberately, because that is exactly the state the
   *  old template hid the hint in. */
  async function mount(canManage: boolean, sessions: VoteSessionSummary[] = []): Promise<void> {
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

    flushByPath(httpMock, `/api/channels/${CHANNEL}/vote-sessions`, pageWith(sessions));
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

  it('shows the create-entry hint for a manager when the list is empty', async () => {
    await mount(true, []);

    expect(fixture.nativeElement.textContent).toContain('voting.list.createEntryHint');
  });

  // The regression this spec exists to guard: the hint used to live only inside the empty state
  // and disappeared the moment a session existed. It must survive a non-empty list.
  it('keeps showing the create-entry hint once the list is no longer empty', async () => {
    await mount(true, [oneSession()]);

    expect(fixture.nativeElement.textContent).toContain('voting.list.createEntryHint');
  });

  it('never shows the create-entry hint to a plain voter, list empty or not', async () => {
    await mount(false, []);
    expect(fixture.nativeElement.textContent).not.toContain('voting.list.createEntryHint');
  });

  it('never shows the create-entry hint to a plain voter when the list has sessions', async () => {
    await mount(false, [oneSession()]);
    expect(fixture.nativeElement.textContent).not.toContain('voting.list.createEntryHint');
  });

  it('shows the plain empty notice to a manager, not a second copy of the how-to', async () => {
    await mount(true, []);

    // The hint appears exactly once — as the permanent line above the list, not repeated inside
    // the empty state below it.
    const occurrences = (
      fixture.nativeElement.textContent.match(/voting\.list\.createEntryHint/g) ?? []
    ).length;
    expect(occurrences).toBe(1);
    expect(fixture.nativeElement.textContent).toContain('voting.list.noSessions');
    // `toContain('voting.list.noSessions')` alone does not pin this down: that key path is a plain
    // string prefix of `voting.list.noSessionsVoterHint`'s own key path, so it would stay green even
    // if the voter's description leaked into a manager's empty state instead of staying `null`. This
    // is the assertion that actually nails the fall-through in the template's ternary.
    expect(fixture.nativeElement.textContent).not.toContain('voting.list.noSessionsVoterHint');
  });

  it('shows only the plain empty notice to a voter, never the manager how-to', async () => {
    await mount(false);

    expect(fixture.nativeElement.textContent).toContain('voting.list.noSessionsVoterHint');
    expect(fixture.nativeElement.textContent).not.toContain('voting.list.createEntryHint');
  });
});
