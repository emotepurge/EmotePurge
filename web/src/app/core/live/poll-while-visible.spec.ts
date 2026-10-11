import { Injector, WritableSignal, runInInjectionContext, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subscription } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { pollWhileVisible } from './poll-while-visible';

describe('pollWhileVisible', () => {
  const INTERVAL = 60_000;
  let subscription: Subscription | undefined;

  beforeEach(() => vi.useFakeTimers());
  afterEach(() => {
    subscription?.unsubscribe();
    vi.useRealTimers();
  });

  function start(visible: WritableSignal<boolean>): { ticks: () => number } {
    let ticks = 0;
    subscription = runInInjectionContext(TestBed.inject(Injector), () =>
      pollWhileVisible(INTERVAL, visible),
    ).subscribe(() => ticks++);
    TestBed.tick();
    return { ticks: () => ticks };
  }

  it('does not tick while the page is hidden', () => {
    const { ticks } = start(signal(false));

    vi.advanceTimersByTime(INTERVAL * 5);

    expect(ticks()).toBe(0);
  });

  it('ticks immediately when visible and then once per interval', () => {
    const { ticks } = start(signal(true));
    vi.advanceTimersByTime(0);
    expect(ticks()).toBe(1);

    vi.advanceTimersByTime(INTERVAL * 2);
    expect(ticks()).toBe(3);
  });

  it('pauses while hidden and ticks once immediately on becoming visible again', () => {
    const visible = signal(true);
    const { ticks } = start(visible);
    vi.advanceTimersByTime(0);
    expect(ticks()).toBe(1);

    visible.set(false);
    TestBed.tick();
    vi.advanceTimersByTime(INTERVAL * 30);
    expect(ticks()).toBe(1);

    visible.set(true);
    TestBed.tick();
    vi.advanceTimersByTime(0);
    expect(ticks()).toBe(2);

    vi.advanceTimersByTime(INTERVAL);
    expect(ticks()).toBe(3);
  });
});
