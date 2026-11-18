import { Component, effect, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { ChannelService } from '../../core/channels/channel.service';
import { BackfillSection } from './backfill-section';

/**
 * The channel's settings tab. The backfill section follows the operator's backfill switch (spec
 * 2026-10-09, D17); the wish settings (#485) join in T10 behind their own switch. The tab is shown
 * while either switch is on (E19), so with only the wish switch it holds nothing but its heading
 * until T10.
 */
@Component({
  selector: 'app-channel-settings-page',
  imports: [BackfillSection, TranslocoPipe],
  template: `
    <div class="flex flex-col gap-6">
      <h2 class="text-lg font-semibold">{{ 'channelWorkspace.settings.title' | transloco }}</h2>
      @if (chatLogBackfillEnabled()) {
        <app-backfill-section [channelName]="channelName()" />
      }
    </div>
  `,
})
export class ChannelSettingsPage {
  readonly channelName = input.required<string>();

  private readonly channelService = inject(ChannelService);

  // Hidden until the permissions answer says otherwise; a failed call keeps it hidden. The guard
  // has already read the same (cached) answer, so this is not a second request in practice.
  protected readonly chatLogBackfillEnabled = signal(false);

  constructor() {
    effect((onCleanup) => {
      const subscription = this.channelService.getPermissions(this.channelName()).subscribe({
        next: (permissions) => this.chatLogBackfillEnabled.set(permissions.chatLogBackfillEnabled),
        error: () => this.chatLogBackfillEnabled.set(false),
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }
}
