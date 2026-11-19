import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EVENT_SOURCE_FACTORY } from './event-source.factory';
import { LIVE_CONNECTION_RELEASE_FACTORY } from './live-connection-release.factory';
import { LiveEvent } from './live-event.model';
import { LiveUpdateService } from './live-update.service';

const READY_STATE_CONNECTING = 0;
const READY_STATE_OPEN = 1;
const READY_STATE_CLOSED = 2;

/**
 * jsdom implements no EventSource at all, so this stands in for it through EVENT_SOURCE_FACTORY —
 * the whole reason that token exists. Only the surface LiveUpdateService touches is modelled.
 */
class FakeEventSource {
  static instances: FakeEventSource[] = [];

  readyState = READY_STATE_OPEN;
  closeCount = 0;
  onopen: (() => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onerror: (() => void) | null = null;

  // Mirrors the browser's "last event ID buffer" (issue #128 follow-up): sticky once set by a
  // frame's `id:` field, and repeated on every later `MessageEvent` until a new one replaces it —
  // not, as an earlier version of this fake modelled, empty on every frame after the first.
  private lastEventId = '';

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  close(): void {
    this.closeCount++;
    this.readyState = READY_STATE_CLOSED;
  }

  /**
   * Delivers a raw SSE frame body, exactly as the browser would hand it over. Omit `lastEventId`
   * for an ordinary later frame with no `id:` field of its own — it then repeats whatever id this
   * stream last received, the real sticky behaviour. Pass it explicitly to simulate a frame that
   * does carry an `id:` field (typically only the first one of a stream), which also becomes the
   * new sticky value; passing the empty string explicitly simulates the one thing real browsers
   * do not do — an `id:`-less frame reported as clearing the buffer — purely for a defensive test.
   */
  emit(data: string, lastEventId?: string): void {
    if (lastEventId !== undefined) {
      this.lastEventId = lastEventId;
    }
    this.onmessage?.({ data, lastEventId: this.lastEventId } as MessageEvent);
  }

  fail(readyState: number): void {
    this.readyState = readyState;
    this.onerror?.();
  }
}

/**
 * Stands in for `Document.defaultView` so tests can dispatch `pagehide` without attaching a
 * listener to jsdom's single real, test-file-wide `window` — the same reasoning as `FakeDocument`
 * below, and why `LiveUpdateService` reads `window` via the injected `DOCUMENT` in the first place.
 */
class FakeWindow {
  private readonly listeners = new Set<() => void>();

  addEventListener(type: string, listener: () => void): void {
    if (type === 'pagehide') {
      this.listeners.add(listener);
    }
  }

  removeEventListener(type: string, listener: () => void): void {
    if (type === 'pagehide') {
      this.listeners.delete(listener);
    }
  }

  dispatchPagehide(): void {
    this.listeners.forEach((listener) => listener());
  }
}

/**
 * Stands in for `DOCUMENT` so tests can drive `visibilityState` and the `visibilitychange`
 * listener the service registers — jsdom's real document exposes `visibilityState` as a read-only
 * getter that cannot be reassigned from a test.
 */
class FakeDocument {
  visibilityState: 'visible' | 'hidden' = 'visible';

  readonly defaultView = new FakeWindow();

  private readonly listeners = new Set<() => void>();

  addEventListener(type: string, listener: () => void): void {
    if (type === 'visibilitychange') {
      this.listeners.add(listener);
    }
  }

  removeEventListener(type: string, listener: () => void): void {
    if (type === 'visibilitychange') {
      this.listeners.delete(listener);
    }
  }

  setVisibility(state: 'visible' | 'hidden'): void {
    this.visibilityState = state;
    this.listeners.forEach((listener) => listener());
  }
}

describe('LiveUpdateService', () => {
  let service: LiveUpdateService;
  let fakeDocument: FakeDocument;
  let releaseConnection: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    FakeEventSource.instances = [];
    fakeDocument = new FakeDocument();
    releaseConnection = vi.fn();
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
        { provide: DOCUMENT, useValue: fakeDocument },
        { provide: LIVE_CONNECTION_RELEASE_FACTORY, useValue: releaseConnection },
      ],
    });
    service = TestBed.inject(LiveUpdateService);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  function subscribe(url = '/api/admin/live'): {
    events: LiveEvent[];
    source: FakeEventSource;
    subscription: Subscription;
  } {
    const events: LiveEvent[] = [];
    const subscription = service.stream(url).subscribe((event) => events.push(event));
    return { events, source: FakeEventSource.instances[0], subscription };
  }

  it('is cold — no connection is opened until someone subscribes', () => {
    const stream$ = service.stream('/api/admin/live');
    expect(FakeEventSource.instances).toHaveLength(0);

    const subscription = stream$.subscribe();
    expect(FakeEventSource.instances).toHaveLength(1);
    expect(FakeEventSource.instances[0].url).toBe('/api/admin/live');
    subscription.unsubscribe();
  });

  it('parses a frame and emits it to the subscriber', () => {
    const { events, source, subscription } = subscribe('/api/channels/sensitron/live');

    source.emit(JSON.stringify({ type: 'vote.changed', channel: 'sensitron', sessionId: 42 }));

    expect(events).toEqual([{ type: 'vote.changed', channel: 'sensitron', sessionId: 42 }]);
    subscription.unsubscribe();
  });

  it('swallows the ping heartbeat', () => {
    const { events, source, subscription } = subscribe();

    source.emit(JSON.stringify({ type: 'ping' }));

    expect(events).toEqual([]);
    subscription.unsubscribe();
  });

  it('drops malformed frames without killing the stream', () => {
    const { events, source, subscription } = subscribe();

    source.emit('not json at all');
    source.emit('{"channel":"sensitron"}'); // no type
    source.emit('null');
    source.emit('42');
    source.emit(JSON.stringify({ type: 'worker.health' }));

    expect(events).toEqual([{ type: 'worker.health' }]);
    subscription.unsubscribe();
  });

  it('passes unknown event types through — filtering is the consumer’s job', () => {
    const { events, source, subscription } = subscribe();

    source.emit(JSON.stringify({ type: 'something.new.from.a.future.backend' }));

    expect(events).toEqual([{ type: 'something.new.from.a.future.backend' }]);
    subscription.unsubscribe();
  });

  it('closes the connection on unsubscribe', () => {
    const { source, subscription } = subscribe();
    expect(source.closeCount).toBe(0);

    subscription.unsubscribe();

    expect(source.closeCount).toBe(1);
    expect(service.status()).toBe('idle');
  });

  it('leaves a reconnecting EventSource alone', () => {
    const { source, subscription } = subscribe();

    source.fail(0); // CONNECTING — the browser is retrying by itself

    expect(FakeEventSource.instances).toHaveLength(1);
    expect(service.status()).toBe('connecting');
    subscription.unsubscribe();
  });

  it('does not rebuild the connection immediately after a fatal error', () => {
    const { source, subscription } = subscribe();

    source.fail(READY_STATE_CLOSED); // e.g. 401 after the session was revoked

    expect(FakeEventSource.instances).toHaveLength(1);
    expect(service.status()).toBe('closed');
    subscription.unsubscribe();
  });

  it('reports open once the connection is established', () => {
    const { source, subscription } = subscribe();

    source.onopen?.();

    expect(service.status()).toBe('open');
    subscription.unsubscribe();
  });

  it('shares one connection between two subscribers of the same url', () => {
    // The workspace layout and the page routed into it both listen to this URL. Before this they
    // opened two EventSources, ran two auth handshakes and took two slots in ILiveEventStream.
    const first: LiveEvent[] = [];
    const second: LiveEvent[] = [];
    const url = '/api/channels/sensitron/live';

    const firstSubscription = service.stream(url).subscribe((event) => first.push(event));
    const secondSubscription = service.stream(url).subscribe((event) => second.push(event));

    expect(FakeEventSource.instances).toHaveLength(1);

    FakeEventSource.instances[0].emit(
      JSON.stringify({ type: 'channel.synced', channel: 'sensitron' }),
    );

    expect(first).toEqual([{ type: 'channel.synced', channel: 'sensitron' }]);
    expect(second).toEqual([{ type: 'channel.synced', channel: 'sensitron' }]);

    firstSubscription.unsubscribe();
    secondSubscription.unsubscribe();
  });

  it('keeps the connection open while one subscriber remains', () => {
    const url = '/api/channels/sensitron/live';
    const firstSubscription = service.stream(url).subscribe();
    const secondSubscription = service.stream(url).subscribe();
    const source = FakeEventSource.instances[0];

    firstSubscription.unsubscribe();

    expect(source.closeCount).toBe(0);

    secondSubscription.unsubscribe();

    expect(source.closeCount).toBe(1);
  });

  it('opens a separate connection per url', () => {
    const one = service.stream('/api/channels/one/live').subscribe();
    const two = service.stream('/api/channels/two/live').subscribe();

    expect(FakeEventSource.instances.map((instance) => instance.url)).toEqual([
      '/api/channels/one/live',
      '/api/channels/two/live',
    ]);

    one.unsubscribe();
    two.unsubscribe();
  });

  it('reconnects a url whose last subscriber had left', () => {
    // The switchMap in liveEvents() drops a channel's stream on navigation and picks it up again on
    // the way back — a cached-but-dead observable would leave that user with no live updates at all.
    const url = '/api/channels/sensitron/live';
    service.stream(url).subscribe().unsubscribe();
    expect(FakeEventSource.instances).toHaveLength(1);

    const again = service.stream(url).subscribe();

    expect(FakeEventSource.instances).toHaveLength(2);
    expect(FakeEventSource.instances[1].url).toBe(url);
    again.unsubscribe();
  });

  describe('reopened, per stream url (#485, plan 2.4)', () => {
    function reopenCount(url: string): { count: () => number; subscription: Subscription } {
      let count = 0;
      const subscription = service.reopened(url).subscribe(() => count++);
      return { count: () => count, subscription };
    }

    it('says nothing on the first open of a connection', () => {
      const url = '/api/channels/a/live';
      const reopened = reopenCount(url);
      const { source, subscription } = subscribe(url);

      source.onopen?.();

      expect(reopened.count()).toBe(0);
      subscription.unsubscribe();
      reopened.subscription.unsubscribe();
    });

    it('reports each reopen of the browser’s own reconnect once', () => {
      const url = '/api/channels/a/live';
      const reopened = reopenCount(url);
      const { source, subscription } = subscribe(url);
      source.onopen?.();

      source.fail(READY_STATE_CONNECTING);
      source.onopen?.();
      expect(reopened.count()).toBe(1);

      source.fail(READY_STATE_CONNECTING);
      source.onopen?.();
      expect(reopened.count()).toBe(2);
      subscription.unsubscribe();
      reopened.subscription.unsubscribe();
    });

    it('reports the rebuild after a fatal close as a reopen too', () => {
      const url = '/api/channels/a/live';
      const reopened = reopenCount(url);
      const { source, subscription } = subscribe(url);
      source.onopen?.();

      source.fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(10_000);
      expect(reopened.count()).toBe(0);
      FakeEventSource.instances[1].onopen?.();

      expect(reopened.count()).toBe(1);
      subscription.unsubscribe();
      reopened.subscription.unsubscribe();
    });

    it('reports only for the url whose connection reopened', () => {
      const reopenedA = reopenCount('/api/channels/a/live');
      const reopenedB = reopenCount('/api/channels/b/live');
      const a = service.stream('/api/channels/a/live').subscribe();
      const b = service.stream('/api/channels/b/live').subscribe();
      const [sourceA, sourceB] = FakeEventSource.instances;
      sourceA.onopen?.();
      sourceB.onopen?.();

      sourceA.fail(READY_STATE_CONNECTING);
      sourceA.onopen?.();

      expect(reopenedA.count()).toBe(1);
      expect(reopenedB.count()).toBe(0);
      a.unsubscribe();
      b.unsubscribe();
      reopenedA.subscription.unsubscribe();
      reopenedB.subscription.unsubscribe();
    });

    it('treats a fresh connection after the last subscriber left as a first open', () => {
      const url = '/api/channels/a/live';
      const reopened = reopenCount(url);
      const first = subscribe(url);
      first.source.onopen?.();
      first.subscription.unsubscribe();

      const again = service.stream(url).subscribe();
      FakeEventSource.instances[1].onopen?.();

      expect(reopened.count()).toBe(0);
      again.unsubscribe();
      reopened.subscription.unsubscribe();
    });

    it('leaves the service-wide status and the delivered events as they were', () => {
      const url = '/api/channels/a/live';
      const { events, source, subscription } = subscribe(url);
      source.onopen?.();
      source.fail(READY_STATE_CONNECTING);
      source.onopen?.();

      source.emit(JSON.stringify({ type: 'wish.changed', channel: 'a' }));

      expect(service.status()).toBe('open');
      expect(events).toEqual([{ type: 'wish.changed', channel: 'a' }]);
      subscription.unsubscribe();
    });
  });

  describe('reconnect after a fatal close (issue #128)', () => {
    it('does not reconnect before the 10 second delay elapses', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);

      vi.advanceTimersByTime(9999);

      expect(FakeEventSource.instances).toHaveLength(1);
      subscription.unsubscribe();
    });

    it('reconnects 10 seconds after a fatal close', () => {
      const { source, subscription } = subscribe('/api/channels/sensitron/live');
      source.fail(READY_STATE_CLOSED);

      vi.advanceTimersByTime(10_000);

      expect(FakeEventSource.instances).toHaveLength(2);
      expect(FakeEventSource.instances[1].url).toBe('/api/channels/sensitron/live');
      expect(service.status()).toBe('connecting');
      subscription.unsubscribe();
    });

    it('grows the backoff exponentially and caps it at 60 seconds', () => {
      const { source, subscription } = subscribe();

      source.fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(10_000);
      expect(FakeEventSource.instances).toHaveLength(2); // 10s attempt

      FakeEventSource.instances[1].fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(19_999);
      expect(FakeEventSource.instances).toHaveLength(2); // 20s not elapsed yet
      vi.advanceTimersByTime(1);
      expect(FakeEventSource.instances).toHaveLength(3); // 20s attempt

      FakeEventSource.instances[2].fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(40_000);
      expect(FakeEventSource.instances).toHaveLength(4); // 40s attempt

      FakeEventSource.instances[3].fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(60_000);
      expect(FakeEventSource.instances).toHaveLength(5); // 60s attempt — the cap, not 80s

      FakeEventSource.instances[4].fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(60_000);
      expect(FakeEventSource.instances).toHaveLength(6); // stays at the 60s cap, does not keep growing

      subscription.unsubscribe();
    });

    it('resets the backoff to 10 seconds after a successful open', () => {
      const { source, subscription } = subscribe();

      source.fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(10_000);
      const second = FakeEventSource.instances[1];
      second.onopen?.(); // the reconnect succeeded

      second.fail(READY_STATE_CLOSED);
      vi.advanceTimersByTime(9_999);
      expect(FakeEventSource.instances).toHaveLength(2); // not yet — still short of the reset 10s
      vi.advanceTimersByTime(1);
      expect(FakeEventSource.instances).toHaveLength(3); // 10s again, not 20s — the backoff was reset

      subscription.unsubscribe();
    });

    it('clears the pending reconnect timer when the last subscriber unsubscribes', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);

      subscription.unsubscribe();
      vi.advanceTimersByTime(60_000);

      // No reconnect may open an EventSource for a URL nobody listens to any more, and the timer
      // must not have leaked either (fake timers would otherwise still report it as pending).
      expect(FakeEventSource.instances).toHaveLength(1);
      expect(vi.getTimerCount()).toBe(0);
    });

    it('defers a reconnect while the tab is hidden and fires it once visible again', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);
      fakeDocument.setVisibility('hidden');

      vi.advanceTimersByTime(10_000);
      expect(FakeEventSource.instances).toHaveLength(1); // backgrounded — no attempt spent on it

      fakeDocument.setVisibility('visible');
      expect(FakeEventSource.instances).toHaveLength(2); // honoured immediately, no further delay

      subscription.unsubscribe();
    });

    it('does not reconnect on a visibility change with nothing pending', () => {
      // The one reconnect path is the backoff timer; a plain tab-focus with a healthy connection
      // (or no fatal close at all) must not become a second, duplicate trigger.
      const { subscription } = subscribe();

      fakeDocument.setVisibility('hidden');
      fakeDocument.setVisibility('visible');

      expect(FakeEventSource.instances).toHaveLength(1);
      subscription.unsubscribe();
    });

    it('does not reconnect twice for one fatal close (timer and visibility never race)', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);
      fakeDocument.setVisibility('hidden');

      vi.advanceTimersByTime(10_000); // delay elapses hidden — hands off to the visibility listener
      fakeDocument.setVisibility('visible'); // fires the deferred reconnect exactly once

      expect(FakeEventSource.instances).toHaveLength(2);
      subscription.unsubscribe();
    });
  });

  describe('suspendReconnecting (issue #128 follow-up: a revoked session)', () => {
    it('cancels a pending reconnect timer outright, not just a future guard', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);
      expect(vi.getTimerCount()).toBe(1);

      service.suspendReconnecting();

      expect(vi.getTimerCount()).toBe(0);
      vi.advanceTimersByTime(60_000);
      expect(FakeEventSource.instances).toHaveLength(1);
      subscription.unsubscribe();
    });

    it('cancels the hidden-tab reconnectDue hand-off, not just a live timer', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);
      fakeDocument.setVisibility('hidden');
      vi.advanceTimersByTime(10_000); // the delay elapses hidden — reconnectDue is now pending

      service.suspendReconnecting();
      fakeDocument.setVisibility('visible');

      // Without the cancellation, becoming visible again would fire the deferred reconnect.
      expect(FakeEventSource.instances).toHaveLength(1);
      subscription.unsubscribe();
    });

    it('blocks scheduling a new reconnect on any later fatal close while suspended', () => {
      const { source, subscription } = subscribe();
      source.fail(READY_STATE_CLOSED);
      service.suspendReconnecting();

      source.fail(READY_STATE_CLOSED); // a further spurious close while still suspended
      vi.advanceTimersByTime(60_000);

      expect(FakeEventSource.instances).toHaveLength(1);
      subscription.unsubscribe();
    });

    it('resumes reconnecting once any connection opens successfully, even a different one', () => {
      const { source, subscription } = subscribe('/api/channels/a/live');
      source.fail(READY_STATE_CLOSED); // schedules a(n about-to-be-cancelled) reconnect
      service.suspendReconnecting();

      const otherSubscription = service.stream('/api/channels/b/live').subscribe();
      FakeEventSource.instances[1].onopen?.(); // a wholly different stream reconnects successfully

      source.fail(READY_STATE_CLOSED); // A's session is fine again too — the flag was global
      vi.advanceTimersByTime(20_000); // A's own backoff had already grown from the first fail

      expect(FakeEventSource.instances).toHaveLength(3);
      expect(FakeEventSource.instances[2].url).toBe('/api/channels/a/live');
      subscription.unsubscribe();
      otherSubscription.unsubscribe();
    });
  });

  describe('connection release on purposeful teardown (issue #128 follow-up)', () => {
    it('releases the id received on the stream’s first frame when the last subscriber leaves', () => {
      const { source, subscription } = subscribe();
      source.emit(JSON.stringify({ type: 'ping' }), 'abc123');

      subscription.unsubscribe();

      expect(releaseConnection).toHaveBeenCalledExactlyOnceWith('abc123');
    });

    it('closes the EventSource before firing the release', () => {
      const calls: string[] = [];
      const { source, subscription } = subscribe();
      source.emit(JSON.stringify({ type: 'ping' }), 'abc123');
      const originalClose = source.close.bind(source);
      source.close = () => {
        calls.push('close');
        originalClose();
      };
      releaseConnection.mockImplementation(() => calls.push('release'));

      subscription.unsubscribe();

      expect(calls).toEqual(['close', 'release']);
    });

    it('later frames repeating the same id do not trigger extra releases or duplicate tracking', () => {
      // Real behaviour (per the SSE spec): only the first frame carries an `id:` field, but the
      // browser's last-event-ID buffer is sticky, so every later MessageEvent repeats that same
      // `lastEventId` — it is never empty on a normal stream. FakeEventSource.emit() models exactly
      // that when called without an explicit id.
      const { source, subscription } = subscribe();
      source.emit(JSON.stringify({ type: 'ping' }), 'steady-id');
      source.emit(JSON.stringify({ type: 'vote.changed' })); // sticky repeat, no explicit id
      source.emit(JSON.stringify({ type: 'worker.health' })); // sticky repeat again

      fakeDocument.defaultView.dispatchPagehide();
      subscription.unsubscribe();

      // One release from pagehide, one from teardown — never one per repeated frame, and the
      // pagehide one is for a single tracked id, not three duplicate entries of the same string.
      expect(releaseConnection).toHaveBeenCalledTimes(2);
      expect(releaseConnection).toHaveBeenNthCalledWith(1, 'steady-id');
      expect(releaseConnection).toHaveBeenNthCalledWith(2, 'steady-id');
    });

    it('an empty lastEventId never clears a remembered id (defensive — real browsers never send one after the first frame)', () => {
      const { source, subscription } = subscribe();
      source.emit(JSON.stringify({ type: 'ping' }), 'first-id');
      source.emit(JSON.stringify({ type: 'vote.changed' }), ''); // explicit empty id, not realistic

      subscription.unsubscribe();

      expect(releaseConnection).toHaveBeenCalledExactlyOnceWith('first-id');
    });

    it('forgets the old id on a fatal close and releases the reconnect’s own new id instead', () => {
      const { source, subscription } = subscribe();
      source.emit(JSON.stringify({ type: 'ping' }), 'first-id');

      source.fail(READY_STATE_CLOSED); // fatal — the old id must not be released (see next test)
      vi.advanceTimersByTime(10_000); // triggers the capped-backoff reconnect, a fresh EventSource
      const reconnected = FakeEventSource.instances[1];
      reconnected.emit(JSON.stringify({ type: 'ping' }), 'second-id');

      subscription.unsubscribe();

      expect(releaseConnection).toHaveBeenCalledExactlyOnceWith('second-id');
    });

    it('fires no release when no id was ever received (unsubscribed before the first frame)', () => {
      const { subscription } = subscribe();

      subscription.unsubscribe();

      expect(releaseConnection).not.toHaveBeenCalled();
    });

    it('fires no release when the stream ends via a fatal close — the server already ended it', () => {
      const { source, subscription } = subscribe();
      source.emit(JSON.stringify({ type: 'ping' }), 'doomed-id');

      source.fail(READY_STATE_CLOSED);
      subscription.unsubscribe(); // right after the fatal close, before any reconnect fires

      expect(releaseConnection).not.toHaveBeenCalled();
    });
  });

  describe('native EventSource reconnect brings a new id (issue #128 follow-up, Codex P2)', () => {
    it('releases the previous id exactly once when the same EventSource delivers a new one', () => {
      const { source, subscription } = subscribe();
      source.emit(JSON.stringify({ type: 'ping' }), 'old-id');

      // The browser's own transient reconnect (network switch, edge reset, or the server's 10-min
      // stream lifetime cap) reuses this same EventSource instance and eventually delivers a new id
      // right here in `onmessage` — never through this service's own reconnect path, which always
      // goes through a fatal close first and a brand-new EventSource.
      source.emit(JSON.stringify({ type: 'ping' }), 'new-id');

      expect(releaseConnection).toHaveBeenCalledExactlyOnceWith('old-id');
      subscription.unsubscribe();
    });

    it('tracks only the new id afterwards — pagehide and teardown release just that one', () => {
      const { source, subscription } = subscribe();
      source.emit(JSON.stringify({ type: 'ping' }), 'old-id');
      source.emit(JSON.stringify({ type: 'ping' }), 'new-id');
      releaseConnection.mockClear(); // drop the old-id release already covered by the test above

      fakeDocument.defaultView.dispatchPagehide();
      subscription.unsubscribe();

      expect(releaseConnection).toHaveBeenCalledTimes(2);
      expect(releaseConnection).toHaveBeenNthCalledWith(1, 'new-id'); // pagehide
      expect(releaseConnection).toHaveBeenNthCalledWith(2, 'new-id'); // teardown
      expect(releaseConnection).not.toHaveBeenCalledWith('old-id');
    });
  });

  describe('pagehide releases every currently open connection (issue #128 follow-up: bfcache)', () => {
    it('releases every currently open connection id without closing the EventSources', () => {
      // Two separate URLs on purpose — `subscribe()` always hands back `instances[0]`, so a second
      // connection needs to be indexed directly out of FakeEventSource.instances instead.
      const first = subscribe('/api/channels/a/live');
      const secondSubscription = service.stream('/api/channels/b/live').subscribe();
      const firstSource = FakeEventSource.instances[0];
      const secondSource = FakeEventSource.instances[1];
      firstSource.emit(JSON.stringify({ type: 'ping' }), 'id-a');
      secondSource.emit(JSON.stringify({ type: 'ping' }), 'id-b');

      fakeDocument.defaultView.dispatchPagehide();

      expect(releaseConnection).toHaveBeenCalledTimes(2);
      expect(releaseConnection).toHaveBeenCalledWith('id-a');
      expect(releaseConnection).toHaveBeenCalledWith('id-b');
      // Not closed: a `pagehide` can mean bfcache, not the tab closing for good — see the service's
      // constructor doc for why the browser's own auto-reconnect is left to rebuild these instead.
      expect(firstSource.closeCount).toBe(0);
      expect(secondSource.closeCount).toBe(0);

      first.subscription.unsubscribe();
      secondSubscription.unsubscribe();
    });

    it('releases nothing for a connection whose id was never received', () => {
      const { subscription } = subscribe();

      fakeDocument.defaultView.dispatchPagehide();

      expect(releaseConnection).not.toHaveBeenCalled();
      subscription.unsubscribe();
    });

    it('releases nothing for a connection that already ended via a fatal close', () => {
      const { source, subscription } = subscribe();
      source.emit(JSON.stringify({ type: 'ping' }), 'doomed-id');
      source.fail(READY_STATE_CLOSED);

      fakeDocument.defaultView.dispatchPagehide();

      expect(releaseConnection).not.toHaveBeenCalled();
      subscription.unsubscribe();
    });
  });
});
