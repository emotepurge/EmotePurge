import { Component, computed, input } from '@angular/core';

import { TranslocoPipe } from '@jsverse/transloco';

import { voteAudience } from '../../core/voting/vote-audience';

/**
 * Names the audience a vote session was created for. Both voting pages carry it: the roles are
 * fixed at creation and cannot be changed afterwards, so without the badge the only place they were
 * ever visible was the create dialog.
 *
 * It is no longer a badge (2026-08-06), and the two steps that got here are worth keeping.
 *
 * First the open case lost its pill: a grey badge reading "everyone" spends a badge on the default,
 * and it sat in a run of three others that were also defaults. Then the restricted case lost its
 * pill too — rendered against a real list, half the rows were restricted, and ten blue rectangles
 * down one column is a colour ladder, not an exception. Which is the point the badge was making:
 * something *notable* about this session. It cannot be notable ten times.
 *
 * The audience is a fact, and "Nur Mods/Streamer" versus "Alle Zuschauer" states it completely on
 * its own. Restriction now shows as one step of contrast, not as a coloured box. The component
 * stays because it owns the roles-bitmask-to-audience mapping and its translation key; only the
 * chrome is gone.
 */
@Component({
  selector: 'app-vote-audience-badge',
  imports: [TranslocoPipe],
  template: `<span [class]="isRestricted() ? 'text-fg-secondary' : ''">{{
    'voting.audience.' + audience() | transloco
  }}</span>`,
})
export class VoteAudienceBadge {
  readonly roles = input.required<number>();

  protected readonly audience = computed(() => voteAudience(this.roles()));
  protected readonly isRestricted = computed(() => this.audience() !== 'everyone');
}
