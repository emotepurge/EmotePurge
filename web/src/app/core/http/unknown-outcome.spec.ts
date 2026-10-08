import { describe, expect, it } from 'vitest';

import { isUnknownOutcome } from './unknown-outcome';

describe('isUnknownOutcome', () => {
  it.each([0, 500, 502, 503, 504, 520, 527])('treats status %i as an unknown outcome', (status) => {
    expect(isUnknownOutcome(status)).toBe(true);
  });

  it.each([400, 401, 403, 404, 409, 410, 429])(
    'treats status %i as a confirmed rejection',
    (status) => {
      expect(isUnknownOutcome(status)).toBe(false);
    },
  );
});
