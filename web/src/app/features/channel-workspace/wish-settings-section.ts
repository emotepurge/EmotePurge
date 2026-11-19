import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { Subscription } from 'rxjs';

import { apiErrorCode, GENERIC_ERROR_TRANSLATION_KEY } from '../../core/i18n/api-error';
import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { liveReload } from '../../core/live/live-reload';
import { wishErrorKey } from '../../core/wishes/wish-error';
import {
  parsePendingExpiryDays,
  parseProbationLeadDays,
  parseWishDurationDays,
  WISH_DURATION_DAYS_MAX,
  WISH_PENDING_EXPIRY_DAYS_MAX,
  WISH_PROBATION_LEAD_DAYS_MAX,
} from '../../core/wishes/wish-input';
import type { WishSettingsRead, WishSettingsUpdate } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { Button } from '../../shared/ui/button';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { SkeletonRows } from '../../shared/ui/skeleton-rows';
import { WishRewardsSection } from './wish-rewards-section';

/** UI-Designsprache §4.5: an acknowledgement fades after this long. */
const SAVED_FEEDBACK_MS = 4000;
/** Same burst window the other live-reloading pages use. */
const RELOAD_DEBOUNCE_MS = 500;

/**
 * The `PUT` body for the fields, or `null` while one is not a valid whole number in its range. The
 * protection threshold is deliberately never part of it: an absent key leaves the stored value
 * alone, whoever changed it meanwhile (#485, plan T10). The same holds for the open-wish expiry
 * (K6): it is sent only while its field is shown (`pendingExpiry` given), i.e. while the capture is on.
 */
export function wishSettingsBody(
  duration: string,
  lead: string,
  pendingExpiry?: string,
): WishSettingsUpdate | null {
  const durationDays = parseWishDurationDays(duration);
  const probationLeadDays = parseProbationLeadDays(lead);
  const pendingExpiryDays =
    pendingExpiry === undefined ? null : parsePendingExpiryDays(pendingExpiry);
  if (!durationDays.ok || !probationLeadDays.ok || pendingExpiryDays?.ok === false) {
    return null;
  }
  return {
    defaultDurationDays: durationDays.value,
    probationLeadDays: probationLeadDays.value,
    ...(pendingExpiryDays?.ok ? { pendingExpiryDays: pendingExpiryDays.value } : {}),
  };
}

/** How a settings read is applied: first load, a live nudge, or the manager asking to discard edits. */
type LoadMode = 'initial' | 'quiet' | 'discard';

/**
 * The "Wünsche" block of the settings tab (#485, plan T10): the channel's default duration and the
 * probation lead. Read-only when the server says the caller may not write (`canEdit`) and, once a
 * `PUT` answers 403, from then on. A live nudge never overwrites unsaved input: it only says so and
 * offers a reload.
 */
@Component({
  selector: 'app-wish-settings-section',
  imports: [Button, NoticeBanner, SkeletonRows, TranslocoPipe, WishRewardsSection],
  host: { class: 'flex flex-col gap-4' },
  template: `
    <section class="flex flex-col gap-4" aria-labelledby="wish-settings-heading">
      <h3 id="wish-settings-heading" class="text-base font-semibold">
        {{ 'wishes.settings.title' | transloco }}
      </h3>

      <!-- Permanently mounted (UI-Designsprache §4.4/§4.5): the acknowledgement and the "changed
           meanwhile" notice arrive while the section stays; their visible twins are aria-hidden. -->
      <p class="sr-only" role="status">
        @if (savedFeedback()) {
          {{ 'wishes.settings.saved' | transloco }}
        } @else if (changedUpstream()) {
          {{ 'wishes.settings.changed' | transloco }}
        }
      </p>

      @if (baseline(); as current) {
        <p class="text-sm text-fg-secondary">{{ 'wishes.settings.intro' | transloco }}</p>
        @if (current.botModeEnabled) {
          <p class="text-sm text-fg-secondary">{{ 'wishes.settings.botActive' | transloco }}</p>
        }
        @if (readOnly()) {
          <p id="wish-settings-read-only" class="text-sm text-fg-muted">
            {{ 'wishes.settings.readOnly' | transloco }}
          </p>
        }
        @if (changedUpstream()) {
          <app-notice-banner variant="warning">
            <span aria-hidden="true">{{ 'wishes.settings.changed' | transloco }}</span>
            <button
              notice-action
              type="button"
              appButton="outline"
              [disabled]="saving()"
              (click)="reloadDiscarding()"
            >
              {{ 'wishes.settings.reload' | transloco }}
            </button>
          </app-notice-banner>
        }

        <div class="flex flex-col gap-1">
          <label for="wish-settings-duration" class="text-sm font-medium">
            {{ 'wishes.settings.duration.label' | transloco }}
          </label>
          <input
            id="wish-settings-duration"
            type="text"
            inputmode="numeric"
            autocomplete="off"
            class="app-input max-w-40"
            [value]="duration()"
            [disabled]="readOnly()"
            [readOnly]="saving()"
            [attr.aria-invalid]="durationError() ? 'true' : null"
            [attr.aria-describedby]="describedBy('duration')"
            (input)="setDuration($any($event.target).value)"
            (blur)="durationTouched.set(true)"
          />
          <p id="wish-settings-duration-hint" class="text-xs text-fg-muted">
            {{ 'wishes.settings.duration.hint' | transloco }}
          </p>
          @if (durationError(); as key) {
            <p id="wish-settings-duration-error" class="text-sm text-danger-fg">
              {{ key | transloco: { max: durationMax } }}
            </p>
          }
        </div>

        <div class="flex flex-col gap-1">
          <label for="wish-settings-lead" class="text-sm font-medium">
            {{ 'wishes.settings.lead.label' | transloco }}
          </label>
          <input
            id="wish-settings-lead"
            type="text"
            inputmode="numeric"
            autocomplete="off"
            class="app-input max-w-40"
            [value]="lead()"
            [disabled]="readOnly()"
            [readOnly]="saving()"
            [attr.aria-invalid]="leadError() ? 'true' : null"
            [attr.aria-describedby]="describedBy('lead')"
            (input)="setLead($any($event.target).value)"
            (blur)="leadTouched.set(true)"
          />
          <p id="wish-settings-lead-hint" class="text-xs text-fg-muted">
            {{ 'wishes.settings.lead.hint' | transloco }}
          </p>
          @if (leadError(); as key) {
            <p id="wish-settings-lead-error" class="text-sm text-danger-fg">
              {{ key | transloco: { max: leadMax } }}
            </p>
          }
        </div>

        @if (captureEnabled()) {
          <div class="flex flex-col gap-1">
            <label for="wish-settings-pending" class="text-sm font-medium">
              {{ 'wishes.settings.pending.label' | transloco }}
            </label>
            <input
              id="wish-settings-pending"
              type="text"
              inputmode="numeric"
              autocomplete="off"
              class="app-input max-w-40"
              [value]="pending()"
              [disabled]="readOnly()"
              [readOnly]="saving()"
              [attr.aria-invalid]="pendingError() ? 'true' : null"
              [attr.aria-describedby]="describedBy('pending')"
              (input)="setPending($any($event.target).value)"
              (blur)="pendingTouched.set(true)"
            />
            <p id="wish-settings-pending-hint" class="text-xs text-fg-muted">
              {{ 'wishes.settings.pending.hint' | transloco }}
            </p>
            @if (pendingError(); as key) {
              <p id="wish-settings-pending-error" class="text-sm text-danger-fg">
                {{ key | transloco: { max: pendingMax } }}
              </p>
            }
          </div>
        }

        @if (!readOnly()) {
          <div class="flex flex-wrap items-center gap-3">
            <button type="button" appButton="primary" [disabled]="!canSave()" (click)="save()">
              {{ 'wishes.settings.save' | transloco }}
            </button>
            @if (savedFeedback()) {
              <span class="text-sm text-fg-secondary" aria-hidden="true">{{
                'wishes.settings.saved' | transloco
              }}</span>
            }
          </div>
          @if (saveErrorKey(); as key) {
            <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
          }
        }

        @if (captureEnabled()) {
          <app-wish-rewards-section
            class="border-t border-border pt-4"
            [channelName]="channelName()"
            [defaultDurationDays]="current.defaultDurationDays"
          />
        }
      } @else if (loadError()) {
        <app-notice-banner variant="error">
          {{ 'wishes.settings.loadFailed' | transloco }}
          <button notice-action type="button" appButton="outline" (click)="retryLoad()">
            {{ 'wishes.settings.retry' | transloco }}
          </button>
        </app-notice-banner>
      } @else {
        <app-skeleton-rows [count]="3" />
      }
    </section>
  `,
})
export class WishSettingsSection {
  readonly channelName = input.required<string>();
  /** The chat capture is on (`wishCaptureEnabled`, K6): the open-wish expiry and the rewards show. */
  readonly captureEnabled = input(false);

  private readonly wishService = inject(WishService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly durationMax = WISH_DURATION_DAYS_MAX;
  protected readonly leadMax = WISH_PROBATION_LEAD_DAYS_MAX;
  protected readonly pendingMax = WISH_PENDING_EXPIRY_DAYS_MAX;

  /** The last settings answer the form was filled from; `null` until the first one. */
  protected readonly baseline = signal<WishSettingsRead | null>(null);
  protected readonly loadError = signal(false);
  protected readonly duration = signal('');
  protected readonly lead = signal('');
  protected readonly pending = signal('');
  protected readonly durationTouched = signal(false);
  protected readonly leadTouched = signal(false);
  protected readonly pendingTouched = signal(false);
  protected readonly saving = signal(false);
  protected readonly saveErrorKey = signal<string | null>(null);
  protected readonly savedFeedback = signal(false);
  /** A live nudge arrived while the form held unsaved input. */
  protected readonly changedUpstream = signal(false);
  /** A `PUT` answered 403: whatever `canEdit` said, the caller may not write. */
  private readonly restricted = signal(false);

  protected readonly readOnly = computed(
    () => this.baseline()?.canEdit === false || this.restricted(),
  );

  protected readonly durationError = computed(() => {
    const result = parseWishDurationDays(this.duration());
    return !result.ok && this.durationTouched()
      ? `wishes.dialog.durationInvalid.${result.reason}`
      : null;
  });

  protected readonly leadError = computed(() => {
    const result = parseProbationLeadDays(this.lead());
    return !result.ok && this.leadTouched() ? `wishes.settings.leadInvalid.${result.reason}` : null;
  });

  protected readonly pendingError = computed(() => {
    const result = parsePendingExpiryDays(this.pending());
    return !result.ok && this.pendingTouched()
      ? `wishes.settings.pendingInvalid.${result.reason}`
      : null;
  });

  /** The fields differ from what the server last said; the expiry only while it is shown. */
  protected readonly dirty = computed(() => {
    const current = this.baseline();
    return (
      current !== null &&
      (this.duration().trim() !== String(current.defaultDurationDays) ||
        this.lead().trim() !== String(current.probationLeadDays) ||
        (this.captureEnabled() && this.pending().trim() !== String(current.pendingExpiryDays)))
    );
  });

  protected readonly canSave = computed(
    () => !this.readOnly() && !this.saving() && this.dirty() && this.body() !== null,
  );

  private loadSubscription: Subscription | null = null;
  /** The mode of the read in `loadSubscription`. */
  private loadMode: LoadMode = 'initial';
  /** Something may have changed upstream while a save was out (a read dropped for it, or a live
   *  event during it); a failed save turns that into `changedUpstream`, a successful one into the
   *  server's answer. */
  private missedUpstream = false;
  /** Number of the latest settings read; a save bumps it too, so no older answer counts. */
  private loadSeq = 0;
  private feedbackTimer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    effect((onCleanup) => {
      const channel = this.channelName();
      untracked(() => {
        this.reset();
        this.load(channel, 'initial');
      });
      onCleanup(() => this.loadSubscription?.unsubscribe());
    });

    liveReload(
      computed(() => channelLiveUrl(this.channelName())),
      { accept: [LIVE_EVENT_TYPES.wishChanged], debounceMs: RELOAD_DEBOUNCE_MS },
    ).subscribe(() => this.onWishChanged());

    this.destroyRef.onDestroy(() => this.clearFeedbackTimer());
  }

  protected setDuration(value: string): void {
    // Read-only while a save is out (the field is `readonly` then; this covers a script's input).
    if (this.saving()) {
      return;
    }
    this.duration.set(value);
    this.durationTouched.set(true);
    this.saveErrorKey.set(null);
  }

  protected setLead(value: string): void {
    if (this.saving()) {
      return;
    }
    this.lead.set(value);
    this.leadTouched.set(true);
    this.saveErrorKey.set(null);
  }

  protected setPending(value: string): void {
    if (this.saving()) {
      return;
    }
    this.pending.set(value);
    this.pendingTouched.set(true);
    this.saveErrorKey.set(null);
  }

  /** The ids a field's `aria-describedby` names: its hint, its error, and the read-only sentence. */
  protected describedBy(field: 'duration' | 'lead' | 'pending'): string {
    const error =
      field === 'duration'
        ? this.durationError()
        : field === 'lead'
          ? this.leadError()
          : this.pendingError();
    return [
      `wish-settings-${field}-hint`,
      error ? `wish-settings-${field}-error` : null,
      this.readOnly() ? 'wish-settings-read-only' : null,
    ]
      .filter((id) => id !== null)
      .join(' ');
  }

  protected save(): void {
    const body = this.body();
    const current = this.baseline();
    if (!this.canSave() || body === null || current === null) {
      return;
    }
    // A read still on the way was issued before this write and would undo it once the form is clean
    // again: it is dropped. Reads only start again after the answer (`onWishChanged` waits).
    this.missedUpstream =
      this.loadSubscription !== null && !this.loadSubscription.closed && this.loadMode === 'quiet';
    this.cancelLoad();
    this.saving.set(true);
    this.saveErrorKey.set(null);
    const channel = this.channelName();
    this.wishService.updateSettings(channel, body).subscribe({
      next: (saved) => {
        if (this.channelName() !== channel) {
          return;
        }
        this.saving.set(false);
        this.missedUpstream = false;
        this.fill({ ...saved, canEdit: current.canEdit });
        this.changedUpstream.set(false);
        this.showSavedFeedback();
      },
      error: (error: unknown) => {
        if (this.channelName() !== channel) {
          return;
        }
        this.saving.set(false);
        if (this.missedUpstream) {
          // The draft stays; the manager is told the settings may have moved under it.
          this.missedUpstream = false;
          this.changedUpstream.set(true);
        }
        this.onSaveError(error);
      },
    });
  }

  protected reloadDiscarding(): void {
    this.load(this.channelName(), 'discard');
  }

  protected retryLoad(): void {
    this.loadError.set(false);
    this.load(this.channelName(), 'initial');
  }

  private onWishChanged(): void {
    if (this.baseline() === null) {
      return;
    }
    if (this.saving()) {
      this.missedUpstream = true;
      return;
    }
    if (this.dirty()) {
      this.changedUpstream.set(true);
      return;
    }
    this.load(this.channelName(), 'quiet');
  }

  private load(channel: string, mode: LoadMode): void {
    this.cancelLoad();
    const seq = ++this.loadSeq;
    this.loadMode = mode;
    this.loadSubscription = this.wishService.getSettings(channel).subscribe({
      next: (read) => {
        // Only the youngest read counts, and none issued before a save (`cancelLoad`).
        if (seq !== this.loadSeq) {
          return;
        }
        // A quiet read must not replace what the manager is typing, however late it lands.
        if (mode === 'quiet' && this.dirty()) {
          this.changedUpstream.set(true);
          return;
        }
        this.fill(read);
        this.changedUpstream.set(false);
        this.restricted.set(false);
        this.loadError.set(false);
      },
      error: () => {
        // A failed background read keeps what is on screen; only a section without data says so.
        if (seq === this.loadSeq && this.baseline() === null) {
          this.loadError.set(true);
        }
      },
    });
  }

  /** Drops the read on the way, if any: unsubscribed, and its answer outdated by the number. */
  private cancelLoad(): void {
    this.loadSeq++;
    this.loadSubscription?.unsubscribe();
    this.loadSubscription = null;
  }

  private fill(read: WishSettingsRead): void {
    this.baseline.set(read);
    this.duration.set(String(read.defaultDurationDays));
    this.lead.set(String(read.probationLeadDays));
    this.pending.set(String(read.pendingExpiryDays));
    this.durationTouched.set(false);
    this.leadTouched.set(false);
    this.pendingTouched.set(false);
  }

  /** The body the form would send now; the expiry belongs to it only while its field is shown. */
  private body(): WishSettingsUpdate | null {
    return wishSettingsBody(
      this.duration(),
      this.lead(),
      this.captureEnabled() ? this.pending() : undefined,
    );
  }

  private onSaveError(error: unknown): void {
    if (!(error instanceof HttpErrorResponse)) {
      this.saveErrorKey.set(GENERIC_ERROR_TRANSLATION_KEY);
      return;
    }
    if (apiErrorCode(error) === 'wish_settings_restricted') {
      // Same state as `canEdit = false`: the explanation takes the place of the error.
      this.restricted.set(true);
      return;
    }
    this.saveErrorKey.set(wishErrorKey(error, 'update'));
  }

  private showSavedFeedback(): void {
    this.clearFeedbackTimer();
    this.savedFeedback.set(true);
    this.feedbackTimer = setTimeout(() => {
      this.feedbackTimer = null;
      this.savedFeedback.set(false);
    }, SAVED_FEEDBACK_MS);
  }

  private clearFeedbackTimer(): void {
    if (this.feedbackTimer !== null) {
      clearTimeout(this.feedbackTimer);
      this.feedbackTimer = null;
    }
  }

  /** A channel switch starts over: the router reuses this component between channels. */
  private reset(): void {
    this.baseline.set(null);
    this.loadError.set(false);
    this.duration.set('');
    this.lead.set('');
    this.pending.set('');
    this.durationTouched.set(false);
    this.leadTouched.set(false);
    this.pendingTouched.set(false);
    this.saving.set(false);
    this.saveErrorKey.set(null);
    this.changedUpstream.set(false);
    this.restricted.set(false);
    this.clearFeedbackTimer();
    this.savedFeedback.set(false);
  }
}
