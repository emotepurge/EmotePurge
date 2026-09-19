import { describe, expect, it } from 'vitest';

import { voteAudience } from './vote-audience';
import { AllowedRoles } from './vote-session.model';

describe('voteAudience', () => {
  it('maps the three masks the create form produces', () => {
    expect(voteAudience(AllowedRoles.Everyone)).toBe('everyone');
    expect(voteAudience(AllowedRoles.Subs)).toBe('subs');
    expect(voteAudience(AllowedRoles.Mods | AllowedRoles.Broadcaster)).toBe('mods');
  });

  it('treats Everyone as winning over any other flag, like the server does', () => {
    expect(voteAudience(AllowedRoles.Everyone | AllowedRoles.Subs)).toBe('everyone');
  });

  it('labels a single staff flag as mods', () => {
    expect(voteAudience(AllowedRoles.Mods)).toBe('mods');
    expect(voteAudience(AllowedRoles.Broadcaster)).toBe('mods');
  });

  it('falls back to restricted rather than mislabelling an unexpected mask', () => {
    expect(voteAudience(AllowedRoles.Subs | AllowedRoles.Mods)).toBe('restricted');
    expect(voteAudience(AllowedRoles.VIPs)).toBe('restricted');
    expect(voteAudience(0)).toBe('restricted');
  });
});
