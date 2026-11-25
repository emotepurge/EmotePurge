import { describe, expect, it } from 'vitest';

import { utilizationTone } from './utilization-tone';

describe('utilizationTone', () => {
  it('grades an empty budget as success', () => {
    expect(utilizationTone(0)).toBe('success');
  });

  it('stays success just below the warn threshold', () => {
    expect(utilizationTone(79.9)).toBe('success');
  });

  it('warns exactly at the warn threshold', () => {
    expect(utilizationTone(80)).toBe('warning');
  });

  it('stays warning just below the critical threshold', () => {
    expect(utilizationTone(94.9)).toBe('warning');
  });

  it('alarms exactly at the critical threshold', () => {
    expect(utilizationTone(95)).toBe('danger');
  });

  it('alarms at full capacity', () => {
    expect(utilizationTone(100)).toBe('danger');
  });

  it('alarms on an uncapped over-budget percentage', () => {
    expect(utilizationTone(130)).toBe('danger');
  });

  it('grades NaN as success instead of warning about nothing', () => {
    expect(utilizationTone(Number.NaN)).toBe('success');
  });

  it('grades a negative percentage as success', () => {
    expect(utilizationTone(-5)).toBe('success');
  });

  describe('with custom thresholds (channel capacity: 90 / 100)', () => {
    const capacity = { warnAt: 90, criticalAt: 100 };

    it('stays success just below the custom warn threshold', () => {
      expect(utilizationTone(89.9, capacity)).toBe('success');
    });

    it('warns exactly at 90', () => {
      expect(utilizationTone(90, capacity)).toBe('warning');
    });

    it('stays warning just below 100', () => {
      expect(utilizationTone(99.9, capacity)).toBe('warning');
    });

    it('alarms exactly at 100 and above', () => {
      expect(utilizationTone(100, capacity)).toBe('danger');
      expect(utilizationTone(130, capacity)).toBe('danger');
    });

    it('leaves the default ladder untouched when no thresholds are passed', () => {
      expect(utilizationTone(85)).toBe('warning');
      expect(utilizationTone(96)).toBe('danger');
    });
  });
});
