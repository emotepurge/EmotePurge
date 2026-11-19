import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, Subject, filter, map, share } from 'rxjs';

import { EVENT_SOURCE_FACTORY } from './event-source.factory';
import { LIVE_CONNECTION_RELEASE_FACTORY } from './live-connection-release.factory';
import { LIVE_EVENT_TYPES, LiveEvent } from './live-event.model';

export type LiveStatus = 'idle' | 'connecting' | 'open' | 'closed';

// EventSource.CONNECTING / .CLOSED are deliberately NOT referenced: jsdom ships no EventSource
// global at all, so touching the constructor would throw a ReferenceError in every Vitest run.
// The numbers are fixed by the WHATWG spec.
const READY_STATE_CONNECTING = 0;
const READY_STATE_CLOSED = 2;

// Issue #128: a 429 (per-login quota full) frees up again within seconds on the server side, and a
// 503 (Redis away) self-heals on its own — the `_redisSubscribed` latch that keeps a warm process
// answering 200 during an outage is explicitly untouched (DECISIONS 2026-09-01/2026-09-10), so a
// 503 a client actually sees is itself transient. Both are worth retrying; a 401 (revoked session)
// or 404 is not — but `onerror` cannot tell them apart (no status code, no body), so retrying on
// every fatal close is what LiveQuotaService's probe exists to make sense of after the fact.
const INITIAL_RECONNECT_DELAY_MS = 10_000;
const MAX_RECONNECT_DELAY_MS = 60_000;
const RECONNECT_BACKOFF_MULTIPLIER = 2;

/**
 * Subscribes to a server-sent-events endpoint and hands out parsed {@link LiveEvent}s.
 *
 * A transient drop (Api restart, proxy hiccup, and above all the server's 10-minute lifetime cap)
 * leaves the EventSource in CONNECTING and the browser retries on its own — re-running the whole
 * auth pipeline on the way, which is exactly what makes the lifetime cap a session-revocation
 * window. A *fatal* close (401/404/429/503, readyState CLOSED) is different: the browser will not
 * rebuild it, so this service does, on a capped exponential backoff (10 s, 20 s, … up to 60 s,
 * reset after the next successful `open`) rather than hammering a door that might still be shut.
 *
 * The one case that backoff must not apply to is a revoked session: retrying a 401 forever would
 * be a silent, permanent loop, since `/api/live/status` is deliberately exempt from the app-wide
 * session redirect (`EXPECTED_401_PATHS`, 2026-09-05 decision) so that a refused stream does not
 * itself bounce the tab to `/login`. This service cannot see the 401 itself (see
 * {@link fatalCloseCount}'s doc), so {@link suspendReconnecting} is the seam LiveQuotaService calls
 * once its probe confirms one — see that method for what it stops and when it resumes.
 *
 * There is no polling fallback on purpose: the failure mode is simply today's behaviour, and the
 * manual refresh button stays on every page that uses this.
 */
@Injectable({ providedIn: 'root' })
export class LiveUpdateService {
  private readonly createEventSource = inject(EVENT_SOURCE_FACTORY);
  private readonly releaseConnection = inject(LIVE_CONNECTION_RELEASE_FACTORY);
  private readonly document = inject(DOCUMENT);
  // Read once via the injected DOCUMENT rather than the global `window`, the same reasoning as for
  // `document` itself: it is what makes the pagehide listener below stubbable in Vitest (jsdom's
  // real global `window` would otherwise accumulate one listener per test that constructs this
  // root-provided service).
  private readonly window = this.document.defaultView;

  private readonly statusSignal = signal<LiveStatus>('idle');

  private readonly fatalCloseSignal = signal(0);

  /** Informational only — service-wide, so with two concurrent streams the last writer wins.
   *  Nothing branches on it; it exists for diagnostics and a possible future indicator. */
  readonly status = this.statusSignal.asReadonly();

  /**
   * Bumped once per *fatal* close (readyState CLOSED — the server answered non-2xx). A counter and
   * not a boolean, so a second failure after the first has been dealt with is still an event a
   * consumer can react to.
   *
   * Exists because this service cannot say *why* the stream died and must not learn: `onerror`
   * carries neither status code nor body, and the alternative — replacing EventSource with a
   * fetch-based reader to read the status — would move the browser's whole reconnect behaviour into
   * our code for one hint. So the signal says "a stream was refused, go and ask"; LiveQuotaService
   * does the asking (issue #42). Deliberately no HttpClient here: keeping this service's dependencies
   * to the EventSource factory and the document is what makes it testable without an HTTP harness.
   */
  readonly fatalCloseCount = this.fatalCloseSignal.asReadonly();

  /** One entry per URL this session has ever subscribed to. Holds no connection — only the (inert)
   *  multicast Observable, so a session that visited twenty channels carries twenty closures. */
  private readonly sharedStreams = new Map<string, Observable<LiveEvent>>();

  /** Set once {@link suspendReconnecting} has been called and cleared again on the next successful
   *  `open` anywhere — see that method's doc for the full reasoning. Read at the moment a reconnect
   *  would otherwise be scheduled or acted on; a plain field, not a signal, because nothing needs to
   *  react to it changing — it only ever gets *checked*. */
  private reconnectSuspended = false;

  /** One cancel callback per currently open {@link connect} closure, so {@link suspendReconnecting}
   *  can reach every live connection's pending timer/hand-off, not just the one that happened to
   *  ask LiveQuotaService's question. Added when a connection opens, removed on its teardown. */
  private readonly pendingReconnectCancellers = new Set<() => void>();

  /**
   * The connection id of every currently open (or reconnecting-in-place) stream, across every URL
   * — the one thing {@link connect}'s per-closure state cannot answer on its own, which is exactly
   * what `pagehide` below needs: "release everything this tab currently holds", not just one URL.
   *
   * An id enters here the moment its stream's first frame carries one, and leaves again in one of
   * three ways: replaced by a different id on the same EventSource — the browser's own transient
   * reconnect, whose old id is released right there in `onmessage` before being forgotten, since the
   * proxy chain can still be holding that old upstream stream and its quota slot; a fatal close
   * (the server already refused/ended it, so there is nothing left to release); or on purpose (the
   * id is released on the way out, see {@link connect}'s teardown). A connection that closes fatally
   * before ever receiving a frame with an id simply never appears here, which is also why `pagehide`
   * never fires a release for one.
   */
  private readonly openConnectionIds = new Set<string>();

  /** The url of every connection that just opened again after an earlier open; see {@link reopened}. */
  private readonly reopenedUrls = new Subject<string>();

  constructor() {
    // Registered once, for the service's own lifetime — not per `connect()` closure, since the
    // point is "the tab is going away", not "this one stream is going away". Reading `defaultView`
    // through the injected `document` rather than the global `window` keeps this stubbable, the same
    // reasoning as `EVENT_SOURCE_FACTORY` (see that token's doc): without it, every test that builds
    // this root-provided service would add another listener to jsdom's single real `window`.
    //
    // Deliberately does not close any EventSource: a `pagehide` can mean the tab is merely going into
    // the back/forward cache, not closing for good. If it returns, the server has already ended
    // these streams (the release below), and the browser's own EventSource auto-reconnect (a
    // transient CONNECTING readyState this service already leaves alone, see `connect`'s `onerror`)
    // rebuilds them — closing them here would just replace one rebuild with another.
    this.window?.addEventListener('pagehide', () => {
      this.openConnectionIds.forEach((connectionId) => this.releaseConnection(connectionId));
    });
  }

  /**
   * Multicast per URL, ref-counted: the first subscriber opens the connection, the last one to
   * unsubscribe closes it, and everyone in between shares the same `EventSource`.
   *
   * Still cold — nothing is opened until someone subscribes — and a URL whose subscribers have all
   * left is rebuilt from scratch on the next one, which is what keeps the `switchMap` in
   * {@link liveEvents} working across navigation.
   *
   * Sharing rather than one connection per subscriber, because the subscribers of one URL sit on top
   * of each other: the channel workspace layout and whichever page is routed into it both listen to
   * `/api/channels/{name}/live`, and the admin monitoring page has up to three listeners on
   * `/api/admin/live`. Each of those used to be its own connection, its own auth handshake, and its
   * own slot in ILiveEventStream's connection limits.
   *
   * One behavioural consequence: the reconnect backoff after a fatal close is per connection, not
   * per component. That is the more honest budget — the connection is what failed.
   */
  stream(url: string): Observable<LiveEvent> {
    const shared = this.sharedStreams.get(url);
    if (shared) {
      return shared;
    }

    const created = this.connect(url).pipe(share({ resetOnRefCountZero: true }));
    this.sharedStreams.set(url, created);
    return created;
  }

  /**
   * Emits each time the connection of `url` opens **again** after it had been open before — the
   * browser's own transient reconnect as well as this service's rebuild after a fatal close. The
   * first open of a connection emits nothing, and neither does a fresh connection after the last
   * subscriber had left (whoever subscribes anew loads anyway).
   *
   * Per url on purpose (#485, plan 2.4): `status` is service-wide and the last writer wins, so a
   * page cannot tell from it whether *its* stream was the one that dropped. A page uses this to catch
   * up on what a gap may have swallowed; it says nothing about the stream unless someone holds a
   * {@link stream} subscription to the same url, because only then is there a connection to reopen.
   */
  reopened(url: string): Observable<void> {
    return this.reopenedUrls.pipe(
      filter((reopenedUrl) => reopenedUrl === url),
      map(() => undefined),
    );
  }

  /**
   * Called by LiveQuotaService the moment its post-close probe (`GET /api/live/status`) answers
   * 401 — the one place that ever learns the *cause* of a fatal close, since `onerror` itself
   * carries no status code (see {@link fatalCloseCount}). A 401 there means this login's cookie is
   * gone, and every stream this tab holds shares that one cookie, so every one of them stops:
   * whichever pending reconnect timer or hidden-tab hand-off is currently waiting is cancelled
   * outright, and every later fatal close — on this stream or a new one opened after a navigation —
   * schedules nothing further while the flag stays set.
   *
   * Deliberately not a redirect and not this service's job to perform one: `/api/live/status` is
   * exempt from `apiAuthInterceptor`'s session-expiry redirect on purpose (2026-09-05 decision), so
   * that a refused stream does not itself bounce the tab to `/login`. The expiry still surfaces —
   * on the next *real* API request, exactly as before this fix existed. This method only stops the
   * one thing that was looping silently underneath that: the reconnect attempts.
   *
   * Clears again on the next successful `open`, anywhere — concrete proof the cookie is valid
   * again, whether because the session was renewed or the 401 was answering a fatal close that
   * has since resolved. There is no separate "resume" call and no timeout of its own: a stream that
   * keeps 401ing stays suspended, and a fresh subscription after navigation is just a new
   * connection closure whose first `open` attempt either succeeds (clearing the flag) or fails the
   * same way (leaving it set, and calling this method again — harmless, idempotent).
   */
  suspendReconnecting(): void {
    this.reconnectSuspended = true;
    this.pendingReconnectCancellers.forEach((cancel) => cancel());
  }

  /** The single-subscriber connection the multicast above wraps. */
  private connect(url: string): Observable<LiveEvent> {
    return new Observable<LiveEvent>((subscriber) => {
      let source: EventSource | null = null;
      // The connection id of *this* stream's current EventSource — captured from the `id:` field of
      // its first SSE frame (surfaced by the browser as `MessageEvent.lastEventId`, read in
      // `onmessage` below). Forgotten (set back to null) the moment that connection stops being
      // releasable: a fatal close, because the server already ended it, or this closure's own
      // teardown, right after the release fires. A fresh EventSource — our own reconnect, or the
      // browser's transient auto-reconnect after a `pagehide` release — gets its own id from its own
      // first frame, which replaces (and, for the browser's own reconnect on this same EventSource,
      // also releases) whatever was remembered before — see `onmessage` below.
      let connectionId: string | null = null;
      let reconnectTimer: ReturnType<typeof setTimeout> | null = null;
      let reconnectDelayMs = INITIAL_RECONNECT_DELAY_MS;
      // Set once a scheduled reconnect's delay has elapsed while the tab was hidden — the single
      // hand-off point to the visibility listener below. At most one of {reconnectTimer pending,
      // reconnectDue} is ever true, which is what keeps this a single reconnect path per URL rather
      // than two racing ones.
      let reconnectDue = false;
      // Whether this closure's connection has been open before: the next `open` is then a reopen
      // (see `reopened`). Per closure, so it survives our own rebuild with a new EventSource.
      let openedBefore = false;

      const open = (): void => {
        this.statusSignal.set('connecting');
        const opened = this.createEventSource(url);
        source = opened;

        opened.onopen = () => {
          this.statusSignal.set('open');
          // A successful connection is proof the backoff has done its job — the next fatal close,
          // whenever it comes, starts back at the short delay rather than continuing to escalate.
          reconnectDelayMs = INITIAL_RECONNECT_DELAY_MS;
          // Also proof the session cookie is valid again — see suspendReconnecting's doc for why
          // that is the one condition allowed to lift it, and why it is fine to lift it globally
          // rather than just for this one connection.
          this.reconnectSuspended = false;
          if (openedBefore) {
            this.reopenedUrls.next(url);
          }
          openedBefore = true;
        };

        opened.onmessage = (event: MessageEvent) => {
          // Only the first frame of a stream carries an `id:` field (issue #128 follow-up), but per
          // the SSE spec the browser's "last event ID buffer" is sticky: every later dispatched
          // MessageEvent repeats that same `lastEventId` until a frame with a new one replaces it
          // (and, after a browser auto-reconnect, resends it as the `Last-Event-ID` request header,
          // which the server ignores). The `!==` guard is what makes a mere repeat a no-op — nothing
          // to release, nothing to re-track — and is also what makes the branch below correct: the
          // only way to reach a *different*, non-null `connectionId` here is the browser's own,
          // transient reconnect of this same EventSource (this service's own reconnect always nulls
          // `connectionId` first, in the fatal-close branch of `onerror` below, so it never lands
          // here with one still set). That old upstream stream can still be held by the proxy chain
          // for another 15-25 s and keep counting against the quota (issue #128 follow-up), and
          // nothing else will ever release it — so release it now, before forgetting it. If the
          // server had already ended it itself (its own 10-minute lifetime cap), this DELETE is a
          // harmless, idempotent 204. The `event.lastEventId` guard on the outside stays for the
          // defensive case of an empty one — a fake EventSource in tests, or a browser that never
          // sent an id at all — read independently of parsing below, since a malformed body must
          // still not cost us the id.
          if (event.lastEventId && event.lastEventId !== connectionId) {
            if (connectionId) {
              this.openConnectionIds.delete(connectionId);
              this.releaseConnection(connectionId);
            }
            connectionId = event.lastEventId;
            this.openConnectionIds.add(connectionId);
          }

          const parsed = parseLiveEvent(event.data);
          // A malformed frame must never kill the stream — drop it and keep listening.
          if (parsed && parsed.type !== LIVE_EVENT_TYPES.ping) {
            subscriber.next(parsed);
          }
        };

        opened.onerror = () => {
          if (opened.readyState === READY_STATE_CONNECTING) {
            // The browser is already retrying with its own backoff. Doing anything here would
            // either duplicate the connection or cancel a retry that is about to succeed.
            this.statusSignal.set('connecting');
            return;
          }
          if (opened.readyState === READY_STATE_CLOSED) {
            // Fatal (the server answered non-2xx: 401 after a revoked session, 404, 429 over the
            // per-login quota, 503 while Redis is away). No rebuild here — neither erroring nor
            // completing the subscriber, since a reconnect is scheduled below instead — but see the
            // class doc for why retrying on all of these is the right call regardless of cause.
            this.statusSignal.set('closed');
            this.fatalCloseSignal.update((count) => count + 1);
            // The server already refused/ended this connection, so there is nothing left to
            // release — forgetting the id here (rather than waiting for teardown) is what keeps
            // this connection's now-dead id out of both a later release call and `pagehide`'s set.
            if (connectionId) {
              this.openConnectionIds.delete(connectionId);
              connectionId = null;
            }
            scheduleReconnect();
          }
        };
      };

      // Clears whatever this connection currently has pending (a live timer, or the hidden-tab
      // hand-off flag) without touching anything else — the one function both suspendReconnecting
      // and this connection's own teardown need, so there is exactly one place that does it.
      const cancelPendingReconnect = (): void => {
        if (reconnectTimer !== null) {
          clearTimeout(reconnectTimer);
          reconnectTimer = null;
        }
        reconnectDue = false;
      };

      const scheduleReconnect = (): void => {
        if (this.reconnectSuspended) {
          // The session is gone (LiveQuotaService's probe confirmed a 401) — see
          // suspendReconnecting's doc. Retrying would just collect more 401s forever; the next real
          // API request surfaces the expiry instead, exactly as apiAuthInterceptor already does.
          return;
        }
        const delay = reconnectDelayMs;
        reconnectDelayMs = Math.min(delay * RECONNECT_BACKOFF_MULTIPLIER, MAX_RECONNECT_DELAY_MS);
        reconnectTimer = setTimeout(() => {
          reconnectTimer = null;
          if (this.reconnectSuspended) {
            // Suspended while this delay was running out — see cancelPendingReconnect for the
            // usual path; this is just a defensive second check for the same condition.
            return;
          }
          if (this.document.visibilityState === 'visible') {
            open();
            return;
          }
          // Backgrounded tabs get no reconnect attempts of their own — spending a slot to keep a
          // stream nobody is looking at alive would be exactly the budget pressure issue #128 is
          // about. Hand off to the visibility listener instead; it fires the moment the tab is
          // looked at again, which is also the realistic case for "the outage has since resolved".
          reconnectDue = true;
        }, delay);
      };

      // The other half of the hand-off above: a backoff delay that elapsed while hidden is honoured
      // as soon as the tab becomes visible, without waiting out a further delay.
      const onVisibilityChange = (): void => {
        if (
          this.reconnectSuspended ||
          !reconnectDue ||
          this.document.visibilityState !== 'visible'
        ) {
          return;
        }
        reconnectDue = false;
        source?.close();
        open();
      };

      this.document.addEventListener('visibilitychange', onVisibilityChange);
      this.pendingReconnectCancellers.add(cancelPendingReconnect);
      open();

      return () => {
        this.document.removeEventListener('visibilitychange', onVisibilityChange);
        this.pendingReconnectCancellers.delete(cancelPendingReconnect);
        cancelPendingReconnect();
        // Close first, release second (issue #128 follow-up) — the release call is what actually
        // frees the server-side slot; closing the EventSource only stops the browser from reading a
        // connection whose slot Cloudflare would otherwise keep counted for another 15-25 s. No
        // release fires when no id was ever received (nothing to release) or when the last thing
        // that happened to this connection was a fatal close (already handled in `onerror` above,
        // which is exactly why `connectionId` is null by the time we get here in that case).
        source?.close();
        source = null;
        if (connectionId) {
          this.openConnectionIds.delete(connectionId);
          this.releaseConnection(connectionId);
          connectionId = null;
        }
        this.statusSignal.set('idle');
      };
    });
  }
}

/** Returns null for anything that is not a JSON object carrying a string `type`. */
function parseLiveEvent(data: unknown): LiveEvent | null {
  if (typeof data !== 'string') {
    return null;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(data);
  } catch {
    return null;
  }
  if (!parsed || typeof parsed !== 'object') {
    return null;
  }
  const candidate = parsed as Partial<LiveEvent>;
  // Unknown types pass through untouched — filtering is the consumer's job, and silently ignoring
  // types this build does not know about is what keeps deployments order-independent.
  return typeof candidate.type === 'string' ? (candidate as LiveEvent) : null;
}
