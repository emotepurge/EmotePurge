import { Component, effect, inject, input, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { ChannelService } from '../../core/channels/channel.service';
import { BackfillSection } from './backfill-section';
import { WishSettingsSection } from './wish-settings-section';

/**
 * The channel's settings tab. The backfill section follows the operator's backfill switch (spec
 * 2026-10-09, D17), the wish settings (#485) their own switch. The tab is shown while either
 * switch is on (E19).
 */
@Component({
  selector: 'app-channel-settings-page',
  imports: [BackfillSection, TranslocoPipe, WishSettingsSection],
  template: `
    <div class="flex flex-col gap-6">
      <h2 class="text-lg font-semibold">{{ 'channelWorkspace.settings.title' | transloco }}</h2>
      <!-- Section stack (UI-Designsprache §2.1): one rule above each section, none inside. -->
      @if (wishesEnabled()) {
        <app-wish-settings-section
          class="border-t border-border pt-4"
          [channelName]="channelName()"
          [captureEnabled]="wishCaptureEnabled()"
        />
      }
      @if (chatLogBackfillEnabled()) {
        <app-backfill-section class="border-t border-border pt-4" [channelName]="channelName()" />
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
  protected readonly wishesEnabled = signal(false);
  /** K6: the chat capture is on; the wishes block then carries the rewards and the open-wish expiry. */
  protected readonly wishCaptureEnabled = signal(false);

  constructor() {
    effect((onCleanup) => {
      const subscription = this.channelService.getPermissions(this.channelName()).subscribe({
        next: (permissions) => {
          this.chatLogBackfillEnabled.set(permissions.chatLogBackfillEnabled);
          this.wishesEnabled.set(permissions.wishesEnabled);
          this.wishCaptureEnabled.set(permissions.wishCaptureEnabled);
        },
        error: () => {
          this.chatLogBackfillEnabled.set(false);
          this.wishesEnabled.set(false);
          this.wishCaptureEnabled.set(false);
        },
      });
      onCleanup(() => subscription.unsubscribe());
    });
  }
}
