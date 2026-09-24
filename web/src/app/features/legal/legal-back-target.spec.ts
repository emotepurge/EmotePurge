import { describe, expect, it } from 'vitest';

import { resolveLegalBackTarget } from './legal-back-target';

describe('resolveLegalBackTarget', () => {
  it('returns "back" for a previous page while logged in', () => {
    expect(resolveLegalBackTarget(true, true)).toEqual({ kind: 'back' });
  });

  it('returns "back" for a previous page while logged out — the origin page wins over login state', () => {
    expect(resolveLegalBackTarget(true, false)).toEqual({ kind: 'back' });
  });

  it('falls back to the overview for a logged-in visitor with no previous page', () => {
    expect(resolveLegalBackTarget(false, true)).toEqual({
      kind: 'fallback',
      link: '/',
      labelKey: 'nav.overview',
    });
  });

  it('falls back to the landing page for a logged-out visitor with no previous page', () => {
    expect(resolveLegalBackTarget(false, false)).toEqual({
      kind: 'fallback',
      link: '/welcome',
      labelKey: 'legal.back',
    });
  });
});
