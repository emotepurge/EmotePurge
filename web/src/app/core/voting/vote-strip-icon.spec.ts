import { describe, expect, it } from 'vitest';

import { STRIP_ICON_DENSE_OVERFLOW_AT, voteStripIconMode } from './vote-strip-icon';

describe('voteStripIconMode', () => {
  it('is always full on the narrow (mobile) strip, regardless of the tally', () => {
    expect(voteStripIconMode(true, null)).toBe('full');
    expect(voteStripIconMode(true, 3)).toBe('full');
    expect(voteStripIconMode(true, STRIP_ICON_DENSE_OVERFLOW_AT)).toBe('full');
  });

  it('is full on the dense strip when the tally is withheld — nothing shares the width with it', () => {
    expect(voteStripIconMode(false, null)).toBe('full');
  });

  it('is compact on the dense strip for a rendered tally under the overflow threshold', () => {
    expect(voteStripIconMode(false, 0)).toBe('compact');
    expect(voteStripIconMode(false, 1)).toBe('compact');
    expect(voteStripIconMode(false, STRIP_ICON_DENSE_OVERFLOW_AT - 1)).toBe('compact');
  });

  it('drops the icon on the dense strip once the tally reaches the overflow threshold', () => {
    expect(voteStripIconMode(false, STRIP_ICON_DENSE_OVERFLOW_AT)).toBe('none');
    expect(voteStripIconMode(false, STRIP_ICON_DENSE_OVERFLOW_AT + 1234)).toBe('none');
  });
});
