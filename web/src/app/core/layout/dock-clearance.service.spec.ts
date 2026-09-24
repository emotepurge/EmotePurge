import { describe, expect, it } from 'vitest';

import { DockClearanceService } from './dock-clearance.service';

describe('DockClearanceService', () => {
  it('starts at zero', () => {
    const service = new DockClearanceService();

    expect(service.px()).toBe(0);
  });

  it('reserve() sets the requested pixel amount', () => {
    const service = new DockClearanceService();

    service.reserve(160);

    expect(service.px()).toBe(160);
  });

  it('release() resets the reservation back to zero', () => {
    const service = new DockClearanceService();

    service.reserve(160);
    service.release();

    expect(service.px()).toBe(0);
  });

  it('a later reserve() replaces the previous amount rather than adding to it', () => {
    const service = new DockClearanceService();

    service.reserve(160);
    service.reserve(80);

    expect(service.px()).toBe(80);
  });
});
