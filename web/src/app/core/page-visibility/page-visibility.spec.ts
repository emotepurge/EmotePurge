import { Injector, runInInjectionContext } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it } from 'vitest';

import { pageVisible } from './page-visibility';

describe('pageVisible', () => {
  function setVisibility(state: DocumentVisibilityState): void {
    Object.defineProperty(document, 'visibilityState', { value: state, configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
  }

  function create() {
    return runInInjectionContext(TestBed.inject(Injector), () => pageVisible());
  }

  afterEach(() => {
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
  });

  it('starts from the current visibility state', () => {
    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });

    expect(create()()).toBe(false);
  });

  it('follows visibilitychange in both directions', () => {
    const visible = create();
    expect(visible()).toBe(true);

    setVisibility('hidden');
    expect(visible()).toBe(false);

    setVisibility('visible');
    expect(visible()).toBe(true);
  });

  it('stops listening once the injecting scope is destroyed', () => {
    const visible = create();

    TestBed.resetTestingModule();
    setVisibility('hidden');

    expect(visible()).toBe(true);
  });
});
