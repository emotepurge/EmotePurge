import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { WideViewportService } from './wide-viewport.service';

/** Same fake as in core/motion/reduced-motion.service.spec.ts: the global matchMedia stub in
 *  test-setup.ts never matches and never fires. */
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

let wideQuery: FakeMediaQueryList;
let queriedFor: string[];

function installMatchMedia(wide: boolean): void {
  wideQuery = new FakeMediaQueryList(wide);
  queriedFor = [];
  vi.stubGlobal('matchMedia', (query: string) => {
    queriedFor.push(query);
    return wideQuery;
  });
}

describe('WideViewportService', () => {
  beforeEach(() => TestBed.resetTestingModule());

  afterEach(() => {
    vi.unstubAllGlobals();
    TestBed.resetTestingModule();
  });

  it("asks for Tailwind's lg breakpoint", () => {
    installMatchMedia(false);
    TestBed.inject(WideViewportService);

    expect(queriedFor).toContain('(min-width: 64rem)');
  });

  it('reports the current width class', () => {
    installMatchMedia(true);

    expect(TestBed.inject(WideViewportService).isWide()).toBe(true);
  });

  it('follows a resize across the breakpoint', () => {
    installMatchMedia(true);
    const service = TestBed.inject(WideViewportService);

    wideQuery.emit(false);

    expect(service.isWide()).toBe(false);
  });

  it('drops the media listener when the injector goes down', () => {
    installMatchMedia(false);
    TestBed.inject(WideViewportService);
    expect(wideQuery.listeners.size).toBe(1);

    TestBed.resetTestingModule();

    expect(wideQuery.listeners.size).toBe(0);
  });
});
