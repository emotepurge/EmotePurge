import { describe, expect, it } from 'vitest';

import { projectSlots } from './slot-projection';

describe('projectSlots', () => {
  it('returns null when capacity is null', () => {
    expect(projectSlots(10, null, 5)).toBeNull();
  });

  it('returns null when capacity is zero or negative', () => {
    expect(projectSlots(0, 0, 5)).toBeNull();
    expect(projectSlots(0, -1, 5)).toBeNull();
  });

  it('reports no overflow when the projection lands exactly on capacity', () => {
    expect(projectSlots(995, 1000, 5)).toEqual({
      projected: 1000,
      capacity: 1000,
      overflow: false,
    });
  });

  it('reports overflow when the projection exceeds capacity by one', () => {
    expect(projectSlots(996, 1000, 5)).toEqual({ projected: 1001, capacity: 1000, overflow: true });
  });

  // The three net-delta shapes `conflict-resolution.ts`'s `summarizeTransferPlan` can hand this
  // function once a run carries name-conflict resolutions (Codex-Finding 5, AK 21) — each asserted
  // here directly against `projectSlots`, independent of how `summarizeTransferPlan` arrives at the
  // number (that arithmetic is `conflict-resolution.spec.ts`'s job).
  describe('net delta from a run with name-conflict resolutions (Codex-Finding 5)', () => {
    it('projects +1 for a single rename (an ADD with no matching REMOVE)', () => {
      expect(projectSlots(50, 100, 1)).toEqual({ projected: 51, capacity: 100, overflow: false });
    });

    it('projects 0 for an ordinary replace (its ADD cancels its own REMOVE)', () => {
      expect(projectSlots(50, 100, 0)).toEqual({ projected: 50, capacity: 100, overflow: false });
    });

    it('projects a negative delta for a replace on a doubly-occupied target (#74 duplicate)', () => {
      expect(projectSlots(50, 100, -1)).toEqual({ projected: 49, capacity: 100, overflow: false });
    });
  });
});
