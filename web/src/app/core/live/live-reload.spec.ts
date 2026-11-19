import { Injector, runInInjectionContext, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EVENT_SOURCE_FACTORY } from './event-source.factory';
import { LIVE_EVENT_TYPES, LiveEvent } from './live-event.model';
import { liveEvents, liveReload, liveReopened } from './live-reload';

const READY_STATE_OPEN = 1;
const READY_STATE_CLOSED = 2;

/** Same stand-in as live-update.service.spec.ts — jsdom ships no EventSource at all. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];

  readyState = READY_STATE_OPEN;
  closeCount = 0;
  onopen: (() => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  close(): void {
    this.closeCount++;
    this.readyState = READY_STATE_CLOSED;
  }

  emit(event: LiveEvent): void {
    this.onmessage?.({ data: JSON.stringify(event) } as MessageEvent);
  }
}

describe('liveEvents / liveReload', () => {
  let injector: Injector;
  const subscriptions: Subscription[] = [];

  beforeEach(() => {
    FakeEventSource.instances = [];
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });
    injector = TestBed.inject(Injector);
  });

  afterEach(() => {
    subscriptions.forEach((subscription) => subscription.unsubscribe());
    subscriptions.length = 0;
    vi.useRealTimers();
  });

  function track(subscription: Subscription): Subscription {
    subscriptions.push(subscription);
    return subscription;
  }

  const only = <T>(): FakeEventSource => {
    expect(FakeEventSource.instances).toHaveLength(1);
    return FakeEventSource.instances[0];
  };

  describe('liveEvents', () => {
    it('emits only the accepted types', () => {
      const seen: LiveEvent[] = [];
      runInInjectionContext(injector, () =>
        track(
          liveEvents('/api/admin/live', [LIVE_EVENT_TYPES.workerHealth]).subscribe((event) =>
            seen.push(event),
          ),
        ),
      );

      only().emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
      only().emit({ type: LIVE_EVENT_TYPES.workerHealth });

      expect(seen).toEqual([{ type: LIVE_EVENT_TYPES.workerHealth }]);
    });

    it('hands the whole event through, not just its type', () => {
      // admin-channels-page depends on this: it needs `channel` to decide whose row hint to upgrade.
      const seen: LiveEvent[] = [];
      runInInjectionContext(injector, () =>
        track(
          liveEvents('/api/admin/live', [LIVE_EVENT_TYPES.channelSynced]).subscribe((event) =>
            seen.push(event),
          ),
        ),
      );

      only().emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'handofblood' });

      expect(seen[0].channel).toBe('handofblood');
    });

    it('accepts a predicate for decisions the type alone cannot make', () => {
      const seen: LiveEvent[] = [];
      runInInjectionContext(injector, () =>
        track(
          liveEvents(
            '/api/channels/x/live',
            (event) => event.type === LIVE_EVENT_TYPES.voteChanged && event.sessionId === 7,
          ).subscribe((event) => seen.push(event)),
        ),
      );

      only().emit({ type: LIVE_EVENT_TYPES.voteChanged, sessionId: 9 });
      only().emit({ type: LIVE_EVENT_TYPES.voteChanged, sessionId: 7 });

      expect(seen).toHaveLength(1);
      expect(seen[0].sessionId).toBe(7);
    });

    it('reconnects to the new url and closes the old connection when a signal url changes', () => {
      const channel = signal('/api/channels/one/live');
      runInInjectionContext(injector, () =>
        track(liveEvents(channel, [LIVE_EVENT_TYPES.usageFlushed]).subscribe()),
      );

      // toObservable delivers even its initial value through an effect, so nothing is connected
      // until change detection has run once.
      expect(FakeEventSource.instances).toHaveLength(0);
      TestBed.tick();

      expect(FakeEventSource.instances).toHaveLength(1);
      const first = FakeEventSource.instances[0];

      channel.set('/api/channels/two/live');
      TestBed.tick();

      expect(FakeEventSource.instances).toHaveLength(2);
      expect(first.closeCount).toBe(1);
      expect(FakeEventSource.instances[1].url).toBe('/api/channels/two/live');
    });
  });

  describe('liveReload', () => {
    it('collapses a burst into one emission carrying every type seen in it', () => {
      const bursts: ReadonlySet<string>[] = [];
      runInInjectionContext(injector, () =>
        track(
          liveReload('/api/channels/x/live', {
            accept: [LIVE_EVENT_TYPES.usageFlushed, LIVE_EVENT_TYPES.channelSynced],
            debounceMs: 1000,
          }).subscribe((seen) => bursts.push(seen)),
        ),
      );

      only().emit({ type: LIVE_EVENT_TYPES.usageFlushed });
      only().emit({ type: LIVE_EVENT_TYPES.channelSynced });
      only().emit({ type: LIVE_EVENT_TYPES.usageFlushed });

      expect(bursts).toHaveLength(0);
      vi.advanceTimersByTime(1000);

      expect(bursts).toHaveLength(1);
      expect([...bursts[0]].sort()).toEqual(
        [LIVE_EVENT_TYPES.channelSynced, LIVE_EVENT_TYPES.usageFlushed].sort(),
      );
    });

    it('starts each burst from an empty set', () => {
      // The regression this guards: a leaked set would report channelSynced forever after one sync,
      // making the pages side-load the active emote set id on every single flush.
      const bursts: ReadonlySet<string>[] = [];
      runInInjectionContext(injector, () =>
        track(
          liveReload('/api/channels/x/live', {
            accept: [LIVE_EVENT_TYPES.usageFlushed, LIVE_EVENT_TYPES.channelSynced],
            debounceMs: 500,
          }).subscribe((seen) => bursts.push(seen)),
        ),
      );

      only().emit({ type: LIVE_EVENT_TYPES.channelSynced });
      vi.advanceTimersByTime(500);

      only().emit({ type: LIVE_EVENT_TYPES.usageFlushed });
      vi.advanceTimersByTime(500);

      expect(bursts).toHaveLength(2);
      expect(bursts[0].has(LIVE_EVENT_TYPES.channelSynced)).toBe(true);
      expect(bursts[1].has(LIVE_EVENT_TYPES.channelSynced)).toBe(false);
      expect(bursts[1].has(LIVE_EVENT_TYPES.usageFlushed)).toBe(true);
    });

    it('ignores events outside the accept list entirely', () => {
      const bursts: ReadonlySet<string>[] = [];
      runInInjectionContext(injector, () =>
        track(
          liveReload('/api/channels/x/live', {
            accept: [LIVE_EVENT_TYPES.usageFlushed],
            debounceMs: 200,
          }).subscribe((seen) => bursts.push(seen)),
        ),
      );

      only().emit({ type: LIVE_EVENT_TYPES.workerHealth });
      vi.advanceTimersByTime(200);

      expect(bursts).toHaveLength(0);
    });

    it('opens exactly one EventSource, so the merged-type set is not a second subscription', () => {
      // The whole reason liveReload returns the set instead of letting callers subscribe twice.
      runInInjectionContext(injector, () =>
        track(
          liveReload('/api/channels/x/live', {
            accept: [LIVE_EVENT_TYPES.usageFlushed],
            debounceMs: 100,
          }).subscribe(),
        ),
      );

      expect(FakeEventSource.instances).toHaveLength(1);
    });
  });

  describe('liveReopened (#485)', () => {
    it('fires when the subscribed stream of its url opens again, not on the first open', () => {
      let reopened = 0;
      runInInjectionContext(injector, () => {
        track(liveEvents('/api/channels/x/live', []).subscribe());
        track(liveReopened('/api/channels/x/live').subscribe(() => reopened++));
      });
      const source = only();

      source.onopen?.();
      expect(reopened).toBe(0);

      source.readyState = 0;
      source.onerror?.();
      source.onopen?.();
      expect(reopened).toBe(1);
    });

    it('follows a url signal and ignores the stream it left', () => {
      const url = signal('/api/channels/x/live');
      let reopened = 0;
      runInInjectionContext(injector, () => {
        track(liveEvents(url, []).subscribe());
        track(liveReopened(url).subscribe(() => reopened++));
      });
      TestBed.tick();
      url.set('/api/channels/y/live');
      TestBed.tick();
      const [left, current] = FakeEventSource.instances;
      expect(current.url).toBe('/api/channels/y/live');

      current.onopen?.();
      current.onopen?.();
      left.onopen?.();
      left.onopen?.();

      expect(reopened).toBe(1);
    });
  });
});
