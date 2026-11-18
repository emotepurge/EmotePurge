import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AuthUser } from '../auth/auth.model';
import { AuthService } from '../auth/auth.service';
import {
  WISH_SUMMARY_POLL_MS,
  WISH_SUMMARY_REFRESH_DEBOUNCE_MS,
  WishSummaryService,
} from './wish-summary.service';

const SUMMARY_URL = '/api/wishes/summary';

const USER: AuthUser = {
  twitchUserId: '1',
  login: 'sensitron',
  displayName: 'Sensitron',
  tokenExpiresAtUtc: '2099-01-01T00:00:00Z',
  isGlobalAdmin: false,
  profileImageUrl: null,
};

describe('WishSummaryService', () => {
  let httpMock: HttpTestingController;
  let auth: AuthService;

  beforeEach(() => {
    vi.useFakeTimers();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    httpMock = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(AuthService);
  });

  afterEach(() => {
    setVisibility('visible');
    // Never asks the channel list: the server counts (plan 2.4).
    httpMock.expectNone('/api/channels/mine');
    httpMock.verify();
    vi.useRealTimers();
  });

  function setVisibility(state: DocumentVisibilityState): void {
    Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
  }

  /** Lets the signal-to-stream bridges run and the immediate poll tick fire. */
  function settle(): void {
    TestBed.tick();
    vi.advanceTimersByTime(0);
  }

  function start(): WishSummaryService {
    const service = TestBed.inject(WishSummaryService);
    auth.currentUser.set(USER);
    settle();
    return service;
  }

  function answer(actionCount: number): void {
    httpMock.expectOne(SUMMARY_URL).flush({ actionCount });
  }

  function fail(status: number, errorCode?: string): void {
    httpMock
      .expectOne(SUMMARY_URL)
      .flush(errorCode ? { errorCode } : null, { status, statusText: 'Error' });
  }

  it('asks nothing without a signed-in user', () => {
    const service = TestBed.inject(WishSummaryService);
    settle();
    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS * 3);
    service.refresh();
    vi.advanceTimersByTime(WISH_SUMMARY_REFRESH_DEBOUNCE_MS);

    httpMock.expectNone(SUMMARY_URL);
    expect(service.available()).toBe(false);
    expect(service.actionCount()).toBeNull();
  });

  it('asks once a user is signed in, then every five minutes', () => {
    const service = start();
    expect(service.available()).toBe(false);

    answer(3);
    expect(service.actionCount()).toBe(3);
    expect(service.available()).toBe(true);

    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS - 1);
    httpMock.expectNone(SUMMARY_URL);
    vi.advanceTimersByTime(1);
    answer(0);
    expect(service.actionCount()).toBe(0);
    expect(service.available()).toBe(true);

    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS);
    answer(1);
    expect(service.actionCount()).toBe(1);
  });

  it('pauses while the window is hidden and asks at once when it is visible again', () => {
    const service = start();
    answer(2);

    setVisibility('hidden');
    TestBed.tick();
    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS * 4);
    httpMock.expectNone(SUMMARY_URL);
    expect(service.actionCount()).toBe(2);

    setVisibility('visible');
    settle();
    answer(5);
    expect(service.actionCount()).toBe(5);
  });

  it('turns unavailable on 404 wishes_disabled and never asks again', () => {
    const service = start();
    fail(404, 'wishes_disabled');
    TestBed.tick();

    expect(service.available()).toBe(false);
    expect(service.actionCount()).toBeNull();

    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS * 3);
    service.refresh();
    vi.advanceTimersByTime(WISH_SUMMARY_REFRESH_DEBOUNCE_MS);
    setVisibility('hidden');
    TestBed.tick();
    setVisibility('visible');
    settle();
    httpMock.expectNone(SUMMARY_URL);
  });

  it('keeps the last value on another failure and asks again on the next tick', () => {
    const service = start();
    answer(4);

    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS);
    fail(500);
    expect(service.actionCount()).toBe(4);
    expect(service.available()).toBe(true);

    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS);
    answer(1);
    expect(service.actionCount()).toBe(1);
  });

  it('stays unavailable after a failed first request and recovers on the next tick', () => {
    const service = start();
    fail(503);
    expect(service.available()).toBe(false);

    // A plain 404 without the switch's code is a failure like any other, not "switched off".
    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS);
    fail(404);
    expect(service.available()).toBe(false);

    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS);
    answer(2);
    expect(service.available()).toBe(true);
    expect(service.actionCount()).toBe(2);
  });

  it('turns a burst of refresh() calls into exactly one request', () => {
    const service = start();
    answer(1);

    service.refresh();
    service.refresh();
    vi.advanceTimersByTime(WISH_SUMMARY_REFRESH_DEBOUNCE_MS / 2);
    service.refresh();
    httpMock.expectNone(SUMMARY_URL);

    vi.advanceTimersByTime(WISH_SUMMARY_REFRESH_DEBOUNCE_MS);
    answer(3);
    expect(service.actionCount()).toBe(3);

    vi.advanceTimersByTime(WISH_SUMMARY_REFRESH_DEBOUNCE_MS * 4);
    httpMock.expectNone(SUMMARY_URL);
  });

  it('drops the count on sign-out, ignores the answer on the way and stops asking', () => {
    const service = start();
    answer(3);

    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS);
    const pending = httpMock.expectOne(SUMMARY_URL);

    auth.currentUser.set(null);
    TestBed.tick();
    expect(service.available()).toBe(false);
    expect(service.actionCount()).toBeNull();
    expect(pending.cancelled).toBe(true);

    vi.advanceTimersByTime(WISH_SUMMARY_POLL_MS * 3);
    service.refresh();
    vi.advanceTimersByTime(WISH_SUMMARY_REFRESH_DEBOUNCE_MS);
    httpMock.expectNone(SUMMARY_URL);
  });
});
