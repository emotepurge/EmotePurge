import { describe, expect, it } from 'vitest';

import { wishCaptureRow } from './admin-wish-capture';

describe('wishCaptureRow', () => {
  it('is active when worker and Api both have the capture on', () => {
    expect(wishCaptureRow(true, true)).toEqual({ state: 'active' });
  });

  it('shows no row while both sides are off', () => {
    expect(wishCaptureRow(false, false)).toBeNull();
  });

  it('flags a worker that captures while the Api hides it', () => {
    expect(wishCaptureRow(true, false)).toEqual({ state: 'mismatch', workerActive: true });
  });

  it('flags an Api that shows the capture while the worker does not capture', () => {
    expect(wishCaptureRow(false, true)).toEqual({ state: 'mismatch', workerActive: false });
  });

  it('calls a worker that reports nothing unknown, not off, as long as the Api shows it', () => {
    expect(wishCaptureRow(null, true)).toEqual({ state: 'unknown' });
    expect(wishCaptureRow(undefined, true)).toEqual({ state: 'unknown' });
  });

  it('shows nothing for an unknown worker and an Api that is off', () => {
    expect(wishCaptureRow(null, false)).toBeNull();
    expect(wishCaptureRow(undefined, undefined)).toBeNull();
  });

  it('treats an Api that sends no value as off', () => {
    expect(wishCaptureRow(true, undefined)).toEqual({ state: 'mismatch', workerActive: true });
  });
});
