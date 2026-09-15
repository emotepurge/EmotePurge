import { Location } from '@angular/common';
import { provideLocationMocks } from '@angular/common/testing';
import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { listQueryState } from './list-query-state';

/** Short enough to keep the spec fast, long enough that "not written yet" stays observable. */
const DEBOUNCE_MS = 20;

@Component({ template: '' })
class ListHost {
  readonly query = listQueryState({ action: '', actor: '' });
}

@Component({ template: '' })
class FilterHost {
  readonly query = listQueryState({ action: '', actor: '' });
  readonly draft = this.query.textFilter('actor', DEBOUNCE_MS);
}

describe('list query state', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let location: Location;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [
        provideRouter([
          { path: 'list', component: ListHost },
          { path: 'filtered', component: FilterHost },
        ]),
        provideLocationMocks(),
      ],
    });
    harness = await RouterTestingHarness.create();
    router = TestBed.inject(Router);
    location = TestBed.inject(Location);
    // The harness navigates imperatively and never subscribes the router to the location, so without
    // this `location.back()` would move the mock's history and reach nobody.
    router.setUpLocationChangeListener();
  });

  /** Lets the fire-and-forget `router.navigate` inside the state object finish. */
  async function settle(): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    await harness.fixture.whenStable();
    harness.detectChanges();
  }

  /**
   * Runs `body` with fake timers installed, so a pending `textFilter` `debounceTime` can be advanced
   * deterministically instead of waiting real time for it — real time was observed to flake this
   * suite under CPU load (2 failures in 26 runs).
   *
   * Fake timers must go in *before* `FilterHost` (and its `textFilter`'s `toObservable`/`debounceTime`
   * pipeline) is created, not merely before the value change that starts the debounce window: in
   * RxJS 7.8, a value arriving while `debounceTime` already has a pending task only updates its
   * `lastValue`/`lastTime` — it does not reschedule. The pending action re-checks `scheduler.now()`
   * when it fires and, since it recomputes the same target delay, `AsyncAction.recycleAsyncId` keeps
   * reusing the interval it was first scheduled with. A `debounceTime` first scheduled under real
   * timers therefore keeps running on a real interval no matter how many fake-timer values arrive
   * afterwards — it has to be installed before the very first schedule call.
   */
  async function withFakeTimers(body: () => Promise<void>): Promise<void> {
    vi.useFakeTimers();
    try {
      await body();
    } finally {
      vi.useRealTimers();
    }
  }

  describe('listQueryState', () => {
    it('reads page and filters out of the URL', async () => {
      const host = await harness.navigateByUrl(
        '/list?page=3&action=channel.join&actor=somemod',
        ListHost,
      );

      expect(host.query.page()).toBe(3);
      expect(host.query.params()).toEqual({ action: 'channel.join', actor: 'somemod' });
    });

    it('falls back to the defaults for params the URL does not carry', async () => {
      const host = await harness.navigateByUrl('/list?action=channel.join', ListHost);

      expect(host.query.page()).toBe(1);
      expect(host.query.params()).toEqual({ action: 'channel.join', actor: '' });
    });

    // A signal could never hold these; a hand-edited URL can, and "Seite -3 von 12" is not a state
    // the pager should ever be asked to render.
    it.each(['abc', '0', '-3', '2.5', ''])(
      'clamps a nonsense page (?page=%s) to 1',
      async (raw) => {
        const host = await harness.navigateByUrl(`/list?page=${raw}`, ListHost);

        expect(host.query.page()).toBe(1);
      },
    );

    it('writes the page into the URL and keeps page 1 out of it', async () => {
      const host = await harness.navigateByUrl('/list', ListHost);

      host.query.goToPage(4);
      await settle();
      expect(router.url).toBe('/list?page=4');

      host.query.goToPage(1);
      await settle();
      expect(router.url).toBe('/list');
    });

    it('drops a filter back at its default out of the URL instead of writing it empty', async () => {
      const host = await harness.navigateByUrl('/list?action=channel.join&actor=somemod', ListHost);

      host.query.setParams({ actor: '' });
      await settle();

      expect(router.url).toBe('/list?action=channel.join');
    });

    it('returns to page 1 on every filter change — the old page belongs to the old result set', async () => {
      const host = await harness.navigateByUrl('/list?page=5', ListHost);

      host.query.setParams({ action: 'channel.join' });
      await settle();

      expect(router.url).toBe('/list?action=channel.join');
      expect(host.query.page()).toBe(1);
    });

    // The whole point of moving this state into the URL, and the reason filters and pages navigate
    // differently: back must undo a page change, and must not undo typing one character at a time.
    it('makes a page change a history step and a filter change not', async () => {
      const host = await harness.navigateByUrl('/list', ListHost);

      host.query.setParams({ actor: 'somemod' });
      await settle();
      host.query.goToPage(3);
      await settle();
      expect(router.url).toBe('/list?actor=somemod&page=3');

      location.back();
      await settle();

      // One step back is the page, not the filter — the filter replaced its entry instead of pushing.
      expect(router.url).toBe('/list?actor=somemod');
      expect(host.query.page()).toBe(1);
      expect(host.query.params().actor).toBe('somemod');
    });
  });

  describe('textFilter', () => {
    it('starts from the value the URL was opened with', async () => {
      const host = await harness.navigateByUrl('/filtered?actor=somemod', FilterHost);

      expect(host.draft()).toBe('somemod');
    });

    it('shows typing immediately and reaches the URL only once it settles', async () => {
      await withFakeTimers(async () => {
        const host = await harness.navigateByUrl('/filtered', FilterHost);

        host.draft.set('som');
        harness.detectChanges();

        // Immediate on screen — a router navigation between the key and the character would eat input.
        expect(host.draft()).toBe('som');
        expect(router.url).toBe('/filtered');

        await vi.advanceTimersByTimeAsync(DEBOUNCE_MS - 1);
        expect(router.url).toBe('/filtered');

        await vi.advanceTimersByTimeAsync(DEBOUNCE_MS * 3);
        expect(router.url).toBe('/filtered?actor=som');
      });
    });

    // The reset button clears the *URL*, not the input — so the input has to follow, or it keeps
    // showing a filter that is no longer applied.
    it('follows the URL when the page clears the filter', async () => {
      await withFakeTimers(async () => {
        const host = await harness.navigateByUrl('/filtered', FilterHost);

        host.draft.set('somemod');
        harness.detectChanges();
        await vi.advanceTimersByTimeAsync(DEBOUNCE_MS * 3);
        expect(router.url).toBe('/filtered?actor=somemod');

        host.query.setParams({ actor: '' });
        harness.detectChanges();
        await vi.advanceTimersByTimeAsync(DEBOUNCE_MS * 3);

        expect(router.url).toBe('/filtered');
        expect(host.draft()).toBe('');
      });
    });

    // The other direction the URL can move on its own: a back button or a pasted deep link.
    it('follows the URL when it moves outside the component', async () => {
      const host = await harness.navigateByUrl('/filtered?actor=somemod', FilterHost);
      expect(host.draft()).toBe('somemod');

      await router.navigateByUrl('/filtered?actor=someone-else');
      await settle();

      expect(host.draft()).toBe('someone-else');
    });

    // The case an existing e2e caught: clearing only the URL leaves a value that never reached it,
    // and one debounce window later that value writes itself back — under a filter the page just
    // ruled out. `setParams` clears the draft with the URL because the two are one state.
    it('drops a draft that has not reached the URL yet when the page clears the key', async () => {
      await withFakeTimers(async () => {
        const host = await harness.navigateByUrl('/filtered', FilterHost);

        // Typed and still inside the debounce window — nothing has been written anywhere.
        host.draft.set('somemod');
        harness.detectChanges();
        expect(router.url).toBe('/filtered');

        host.query.setParams({ action: 'user.revokeSessions', actor: '' });
        await vi.advanceTimersByTimeAsync(DEBOUNCE_MS * 3);

        expect(host.draft()).toBe('');
        expect(router.url).toBe('/filtered?action=user.revokeSessions');
      });
    });
  });
});
