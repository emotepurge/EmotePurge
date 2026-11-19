import { describe, expect, it } from 'vitest';
import { headerBadgeShown, wishesCountVisibleBelowSm } from './header-badge';

describe('header badge precedence', () => {
  it('reports a badge for the worker warning and for the paused live stream', () => {
    expect(headerBadgeShown(true, false)).toBe(true);
    expect(headerBadgeShown(false, true)).toBe(true);
    expect(headerBadgeShown(true, true)).toBe(true);
  });

  it('reports none while the frame is silent', () => {
    expect(headerBadgeShown(false, false)).toBe(false);
  });

  it('lets the wishes count give way below sm only while a badge stands', () => {
    expect(wishesCountVisibleBelowSm(headerBadgeShown(false, false))).toBe(true);
    expect(wishesCountVisibleBelowSm(headerBadgeShown(true, false))).toBe(false);
    expect(wishesCountVisibleBelowSm(headerBadgeShown(false, true))).toBe(false);
  });
});
