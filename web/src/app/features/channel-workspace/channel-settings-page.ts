import { Component, input } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { BackfillSection } from './backfill-section';

/**
 * The channel's settings tab. Holds one section today (the chat-log backfill); the tab itself is
 * shown only while the operator's backfill switch is on (spec 2026-10-09, D17).
 */
@Component({
  selector: 'app-channel-settings-page',
  imports: [BackfillSection, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-6">
      <h2 class="text-lg font-semibold">{{ 'channelWorkspace.settings.title' | transloco }}</h2>
      <app-backfill-section [channelName]="channelName()" />
    </div>
  `,
})
export class ChannelSettingsPage {
  readonly channelName = input.required<string>();
}
