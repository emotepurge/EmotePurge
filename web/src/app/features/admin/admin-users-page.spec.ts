/**
 * P2-2 from the Codex Sol review of feat/data-retention: deleting the only user on a page beyond
 * the first used to leave the URL pointing at a page that no longer exists. The reload after the
 * delete came back with an empty `items` array, and the page rendered the empty state with no
 * pager, even though earlier pages still had users on them. `AdminUsersPage`'s constructor now
 * watches the resolved result and clamps the URL back to the last valid page whenever the total
 * shrinks below the requested one (see that constructor's own comment).
 *
 * The scenario is reproduced here through the same `reload()` the delete/revoke handlers call
 * after their action resolves — wired to the visible refresh button — rather than by driving the
 * `TypedConfirmDialog` delete flow end to end. That dialog is already covered in isolation
 * (`typed-confirm-dialog.spec.ts`), and the correction itself reacts to the resource's resolved
 * value, not to *why* it changed, so exercising it through delete specifically would only add a
 * dialog interaction without testing anything the dialog is responsible for.
 */
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AdminUser } from '../../core/admin/admin.model';
import { PagedResult } from '../../core/models/paged-result.model';
import { AdminUsersPage } from './admin-users-page';

const PAGE_SIZE = 25;

function user(twitchUserId: string, displayName: string): AdminUser {
  return {
    twitchUserId,
    twitchUsername: displayName.toLowerCase(),
    displayName,
    lastLogin: '2026-09-20T12:00:00Z',
    sessionsValidFromUtc: null,
    hasRefreshToken: true,
    twitchAccessTokenExpiresAtUtc: null,
    twitchTokenScopes: null,
  };
}

function pageWith(
  items: AdminUser[],
  page: number,
  totalPages: number,
  totalCount: number,
): PagedResult<AdminUser> {
  return { items, page, pageSize: PAGE_SIZE, totalCount, totalPages };
}

describe('AdminUsersPage — recovers when a reload finds the current page emptied', () => {
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
        provideRouter([{ path: 'admin/users', component: AdminUsersPage }]),
        provideLocationMocks(),
      ],
    });
    harness = await RouterTestingHarness.create();
    router = TestBed.inject(Router);
    // Same as list-query-state.spec.ts: the harness navigates imperatively and never subscribes
    // the router to the location on its own.
    router.setUpLocationChangeListener();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  /** A plain macrotask-plus-render tick, with no wait on application stability — the one to reach
   *  for whenever the very thing about to happen is a *new* HTTP request landing in the mock
   *  backend (a click that calls `resource.reload()`, or the correction effect's own `goToPage`
   *  triggering a refetch for the corrected page). `whenStable()` would block until that new
   *  request is flushed, which cannot happen before the code that flushes it has even run. */
  async function tick(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    harness.detectChanges();
  }

  /** Like `tick()`, but also waits for application stability — safe only once nothing left pending
   *  is going to spawn a further request of its own (see `tick()`'s comment). */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  function flushUsers(page: number, body: PagedResult<AdminUser>): void {
    const request = httpMock.expectOne(
      (req) => req.url === '/api/admin/users' && req.params.get('page') === String(page),
    );
    request.flush(body as unknown as object);
  }

  function nativeElement(): HTMLElement {
    return harness.fixture.nativeElement as HTMLElement;
  }

  function clickRefresh(): void {
    const buttons = Array.from(nativeElement().querySelectorAll<HTMLButtonElement>('button'));
    const refresh = buttons.find((button) => button.textContent?.trim() === 'admin.users.refresh');
    expect(refresh).toBeDefined();
    refresh!.click();
  }

  it('moves back to the last valid page once a reload finds the current one empty', async () => {
    await harness.navigateByUrl('/admin/users?page=2', AdminUsersPage);
    harness.detectChanges();
    // Page 2 of 2, one user — a legitimate page, no correction due yet.
    flushUsers(2, pageWith([user('u2', 'PageTwoUser')], 2, 2, 26));
    await settle();

    expect(router.url).toBe('/admin/users?page=2');
    expect(nativeElement().textContent).toContain('PageTwoUser');

    // Same reload() the delete/revoke handlers call after their action resolves. This response
    // simulates that lone user having just been removed: the total dropped by one, so page 2 no
    // longer exists.
    clickRefresh();
    harness.detectChanges();
    flushUsers(2, pageWith([], 2, 1, 25));
    // Not settle(): the correction effect reacts to this very flush by calling goToPage(1), which
    // both navigates and makes the resource issue a *new* request for page 1 — a request settle()'s
    // whenStable() would then block on forever, since nothing has flushed it yet. The navigation
    // itself is also async (router.navigate's own promise), hence two ticks rather than one.
    await tick();
    await tick();

    // The correction: page 2 no longer exists, so the URL moves to page 1 (the last valid one),
    // written the same way a manual "previous" click would (listQueryState.goToPage drops page 1
    // from the URL entirely).
    expect(router.url).toBe('/admin/users');

    flushUsers(1, pageWith([user('u1', 'PageOneUser')], 1, 1, 25));
    await settle();

    expect(nativeElement().textContent).toContain('PageOneUser');
    expect(nativeElement().querySelector('app-empty-state')).toBeNull();
  });

  it('leaves page 1 alone when a reload comes back empty — there is no earlier page to return to', async () => {
    await harness.navigateByUrl('/admin/users', AdminUsersPage);
    harness.detectChanges();
    flushUsers(1, pageWith([user('u1', 'OnlyUser')], 1, 1, 1));
    await settle();

    clickRefresh();
    harness.detectChanges();
    flushUsers(1, pageWith([], 1, 0, 0));
    await settle();

    expect(router.url).toBe('/admin/users');
    expect(nativeElement().querySelector('app-empty-state')).not.toBeNull();
  });
});
