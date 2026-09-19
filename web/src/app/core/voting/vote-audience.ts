import { AllowedRoles } from './vote-session.model';

export type VoteAudience = 'everyone' | 'subs' | 'mods' | 'restricted';

/**
 * Collapses the raw `AllowedRoles` bitmask into the audience the create form offers, so the two
 * voting pages can label an existing session with it. The roles are fixed at creation and were
 * previously visible only while creating — afterwards nobody could tell a subs-only vote from an
 * open one.
 *
 * `Everyone` wins over every other flag because the server does the same: `VoteEligibilityService`
 * lets the flag through before it looks at any other role. The create paths only ever produce
 * `Everyone`, `Subs` or `Mods | Broadcaster`, but the API takes the raw flags — anything else
 * (a mixed mask, `VIPs`, an empty mask) becomes `restricted` rather than being mislabelled as one
 * of the three.
 */
export function voteAudience(allowedVoterRoles: number): VoteAudience {
  if ((allowedVoterRoles & AllowedRoles.Everyone) !== 0) {
    return 'everyone';
  }

  const subs = (allowedVoterRoles & AllowedRoles.Subs) !== 0;
  const staff = (allowedVoterRoles & (AllowedRoles.Mods | AllowedRoles.Broadcaster)) !== 0;
  if (subs && !staff) {
    return 'subs';
  }
  if (staff && !subs) {
    return 'mods';
  }
  return 'restricted';
}
