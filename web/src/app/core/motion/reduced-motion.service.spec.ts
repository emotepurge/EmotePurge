import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ReducedMotionService } from './reduced-motion.service';

/**
 * Same fake as in core/pointer/pointer-mode.service.spec.ts: jsdom has no matchMedia, and the
 * global stub in test-setup.ts never matches and never fires, so the "reduce" and change cases need
 * a list that actually keeps its listeners.
 */
class FakeMediaQueryList {
  readonly listeners = new Set<(event: MediaQueryListEvent) => void>();

  constructor(public matches: boolean) {}

  addEventListener(_type: 'change', listener: (event: MediaQueryListEvent) => void): void {
    this.listeners.add(listener);
  }

  removeEventListener(_type: 'change', listener: (event: MediaQueryListEvent) => void): void {
    this.listeners.delete(listener);
  }

  emit(matches: boolean): void {
    this.matches = matches;
    for (const listener of this.listeners) {
      listener({ matches } as MediaQueryListEvent);
    }
  }
}

let motionQuery: FakeMediaQueryList;
let queriedFor: string[];

function installMatchMedia(reduce: boolean): void {
  motionQuery = new FakeMediaQueryList(reduce);
  queriedFor = [];
  vi.stubGlobal('matchMedia', (query: string) => {
    queriedFor.push(query);
    return motionQuery;
  });
}

describe('ReducedMotionService', () => {
  beforeEach(() => TestBed.resetTestingModule());

  afterEach(() => {
    vi.unstubAllGlobals();
    TestBed.resetTestingModule();
  });

  it('asks for the reduce preference', () => {
    installMatchMedia(false);
    TestBed.inject(ReducedMotionService);

    expect(queriedFor).toContain('(prefers-reduced-motion: reduce)');
  });

  it('reports no preference as motion allowed', () => {
    installMatchMedia(false);

    expect(TestBed.inject(ReducedMotionService).prefersReducedMotion()).toBe(false);
  });

  it('reports the reduce preference', () => {
    installMatchMedia(true);

    expect(TestBed.inject(ReducedMotionService).prefersReducedMotion()).toBe(true);
  });

  it('follows the setting being changed while the app is open', () => {
    installMatchMedia(false);
    const service = TestBed.inject(ReducedMotionService);

    motionQuery.emit(true);

    expect(service.prefersReducedMotion()).toBe(true);
  });

  it('drops the media listener when the injector goes down', () => {
    installMatchMedia(false);
    TestBed.inject(ReducedMotionService);
    expect(motionQuery.listeners.size).toBe(1);

    TestBed.resetTestingModule();

    expect(motionQuery.listeners.size).toBe(0);
  });
});
