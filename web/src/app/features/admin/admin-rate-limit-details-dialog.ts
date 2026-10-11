import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, inject } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import {
  ProviderRateLimitHeaderSample,
  RateLimitPolicySnapshot,
  RateLimitTelemetrySnapshot,
} from '../../core/admin/admin.model';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';

export interface RateLimitDetailsDialogData {
  /** The snapshot at the moment of opening; the dialog does not follow later polls. */
  snapshot: RateLimitTelemetrySnapshot;
}

/** Shown when a value is absent. */
const NO_VALUE = '—';

/**
 * The full rate-limit picture: every registered policy with its configuration and counters, the
 * last local rejection, the server caches and the provider counters. The monitoring page itself
 * only shows the exceptions; everything that is quiet lives here, so a policy without a single
 * rejection is still inspectable.
 */
@Component({
  selector: 'app-admin-rate-limit-details-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'admin.dashboard.rateLimits.details.title' | transloco">
      <div class="flex flex-col gap-4">
        @if (!data.telemetryAvailable) {
          <app-notice-banner variant="warning">
            {{ 'admin.rateLimits.degraded' | transloco }}
          </app-notice-banner>
        }
        <!-- Effective configuration + accepted/rejected counts, one row per registered policy —
         always every policy from options, even one the counter store never saw traffic for. -->
        <div class="flex flex-col gap-2">
          <h4 class="text-sm font-medium text-fg-body">
            {{ 'admin.rateLimits.policies.title' | transloco }}
          </h4>
          <ul class="-mx-3 divide-y divide-border border-y border-border">
            @for (policy of data.policies; track policy.name) {
              <li class="flex flex-col gap-2 px-3 py-3">
                <div class="text-sm font-medium text-fg-body">
                  {{ 'admin.rateLimits.policies.names.' + policy.name | transloco }}
                </div>
                <dl class="grid grid-cols-2 gap-x-4 gap-y-1 text-xs sm:grid-cols-4">
                  <div class="flex justify-between gap-2 sm:block">
                    <dt class="text-fg-muted">
                      {{ 'admin.rateLimits.policies.capacity' | transloco }}
                    </dt>
                    <dd class="text-fg-body">{{ formatNumber(policy.capacity) }}</dd>
                  </div>
                  <div class="flex justify-between gap-2 sm:block">
                    <dt class="text-fg-muted">
                      {{ 'admin.rateLimits.policies.refill' | transloco }}
                    </dt>
                    <dd class="text-fg-body">
                      {{ refillDescriptionKey(policy) | transloco: refillParams(policy) }}
                    </dd>
                  </div>
                  <div class="flex justify-between gap-2 sm:block">
                    <dt class="text-fg-muted">
                      {{ 'admin.rateLimits.policies.partition' | transloco }}
                    </dt>
                    <dd class="text-fg-body">{{ policy.partition }}</dd>
                  </div>
                  <div class="flex justify-between gap-2 sm:block">
                    <dt class="text-fg-muted">
                      {{ 'admin.rateLimits.policies.queueLimit' | transloco }}
                    </dt>
                    <dd class="text-fg-body">{{ formatNumber(policy.queueLimit) }}</dd>
                  </div>
                  @if (data.telemetryAvailable) {
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.policies.acceptedMinute' | transloco }}
                      </dt>
                      <dd class="text-fg-body">{{ formatNumber(policy.acceptedLastMinute) }}</dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.policies.rejectedMinute' | transloco }}
                      </dt>
                      <dd class="text-fg-body">{{ formatNumber(policy.rejectedLastMinute) }}</dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.policies.accepted24h' | transloco }}
                      </dt>
                      <dd class="text-fg-body">
                        {{ formatNumber(policy.acceptedLast24Hours) }}
                      </dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.policies.rejected24h' | transloco }}
                      </dt>
                      <dd class="text-fg-body">
                        {{ formatNumber(policy.rejectedLast24Hours) }}
                      </dd>
                    </div>
                  }
                </dl>
              </li>
            }
          </ul>
        </div>

        <!-- Last local rejection: one entry across all policies, overwritten each time. -->
        <div class="flex flex-col gap-2">
          <h4 class="text-sm font-medium text-fg-body">
            {{ 'admin.rateLimits.lastRejection.title' | transloco }}
          </h4>
          @if (!data.telemetryAvailable) {
            <p class="text-sm text-fg-muted">
              {{ 'admin.rateLimits.lastRejection.unavailable' | transloco }}
            </p>
          } @else if (data.lastLocalRejection; as rejection) {
            <dl class="grid grid-cols-1 gap-x-6 gap-y-2 text-sm sm:grid-cols-2">
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.rateLimits.lastRejection.observedAt' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ formatDateTime(rejection.observedAtUtc) }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.rateLimits.lastRejection.method' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ rejection.httpMethod }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.rateLimits.lastRejection.route' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ rejection.routeTemplate }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.rateLimits.lastRejection.policy' | transloco }}
                </dt>
                <dd class="text-fg-body">
                  {{ 'admin.rateLimits.policies.names.' + rejection.policyName | transloco }}
                </dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.rateLimits.lastRejection.partition' | transloco }}
                </dt>
                <dd class="text-fg-body">{{ rejection.partition }}</dd>
              </div>
              <div class="flex justify-between gap-4 sm:block">
                <dt class="text-fg-muted">
                  {{ 'admin.rateLimits.lastRejection.retryAfter' | transloco }}
                </dt>
                <dd class="text-fg-body">
                  {{ formatRetryAfterSeconds(rejection.retryAfterSeconds) }}
                </dd>
              </div>
            </dl>
          } @else {
            <p class="text-sm text-fg-muted">
              {{ 'admin.rateLimits.lastRejection.none' | transloco }}
            </p>
          }
        </div>

        <!-- Server-cache hit/miss counters. Numbers only while telemetry is available — with
         Redis unreachable, the counter store has no entries at all rather than zeroed ones. -->
        <div class="flex flex-col gap-2">
          <h4 class="text-sm font-medium text-fg-body">
            {{ 'admin.rateLimits.caches.title' | transloco }}
          </h4>
          @if (!data.telemetryAvailable) {
            <p class="text-sm text-fg-muted">
              {{ 'admin.rateLimits.caches.unavailable' | transloco }}
            </p>
          } @else if (data.caches.length) {
            <ul class="-mx-3 divide-y divide-border border-y border-border">
              @for (cache of data.caches; track cache.cacheName) {
                <li class="flex flex-col gap-2 px-3 py-3">
                  <div class="text-sm font-medium text-fg-body">
                    {{ 'admin.rateLimits.caches.names.' + cache.cacheName | transloco }}
                  </div>
                  <dl class="grid grid-cols-2 gap-x-4 gap-y-1 text-xs sm:grid-cols-4">
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.caches.hitsMinute' | transloco }}
                      </dt>
                      <dd class="text-fg-body">{{ formatNumber(cache.hitsLastMinute) }}</dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.caches.missesMinute' | transloco }}
                      </dt>
                      <dd class="text-fg-body">{{ formatNumber(cache.missesLastMinute) }}</dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.caches.hits24h' | transloco }}
                      </dt>
                      <dd class="text-fg-body">{{ formatNumber(cache.hitsLast24Hours) }}</dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.caches.misses24h' | transloco }}
                      </dt>
                      <dd class="text-fg-body">{{ formatNumber(cache.missesLast24Hours) }}</dd>
                    </div>
                  </dl>
                </li>
              }
            </ul>
          } @else {
            <p class="text-sm text-fg-muted">{{ 'admin.rateLimits.caches.empty' | transloco }}</p>
          }
        </div>

        <!-- Real provider 429s per (provider, call-source) pair, plus the Twitch header sample —
         deliberately no percentage anywhere in here (neither provider publishes a quota we
         could divide by). -->
        <div class="flex flex-col gap-2">
          <h4 class="text-sm font-medium text-fg-body">
            {{ 'admin.rateLimits.providers.title' | transloco }}
          </h4>
          @if (!data.telemetryAvailable) {
            <p class="text-sm text-fg-muted">
              {{ 'admin.rateLimits.providers.unavailable' | transloco }}
            </p>
          } @else if (data.providers.length) {
            <ul class="-mx-3 divide-y divide-border border-y border-border">
              @for (
                provider of data.providers;
                track provider.providerName + '|' + provider.callSource
              ) {
                <li class="flex flex-col gap-2 px-3 py-3">
                  <div class="text-sm font-medium text-fg-body">
                    {{ 'admin.rateLimits.providers.names.' + provider.providerName | transloco }}
                    ·
                    {{
                      'admin.rateLimits.providers.callSources.' + provider.callSource | transloco
                    }}
                  </div>
                  <dl class="grid grid-cols-2 gap-x-4 gap-y-1 text-xs sm:grid-cols-4">
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.providers.requestsMinute' | transloco }}
                      </dt>
                      <dd class="text-fg-body">
                        {{ formatNumber(provider.requestsLastMinute) }}
                      </dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.providers.requests24h' | transloco }}
                      </dt>
                      <dd class="text-fg-body">
                        {{ formatNumber(provider.requestsLast24Hours) }}
                      </dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.providers.rateLimitedMinute' | transloco }}
                      </dt>
                      <dd class="text-fg-body">
                        {{ formatNumber(provider.rateLimitedLastMinute) }}
                      </dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.providers.rateLimited24h' | transloco }}
                      </dt>
                      <dd class="text-fg-body">
                        {{ formatNumber(provider.rateLimitedLast24Hours) }}
                      </dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.providers.lastRateLimitedAt' | transloco }}
                      </dt>
                      <dd class="text-fg-body">
                        {{ formatDateTime(provider.lastRateLimitedAtUtc) }}
                      </dd>
                    </div>
                    <div class="flex justify-between gap-2 sm:block">
                      <dt class="text-fg-muted">
                        {{ 'admin.rateLimits.providers.retryAfter' | transloco }}
                      </dt>
                      <dd class="text-fg-body">
                        {{ formatRetryAfterSeconds(provider.lastRetryAfterSeconds) }}
                      </dd>
                    </div>
                  </dl>
                  @if (provider.lastHeaderSample; as sample) {
                    <p class="text-xs text-fg-muted">
                      <span class="font-medium">
                        {{ 'admin.rateLimits.providers.headerSample' | transloco }}:
                      </span>
                      {{
                        'admin.rateLimits.providers.headerSampleValue'
                          | transloco: headerSampleParams(sample)
                      }}
                    </p>
                  }
                </li>
              }
            </ul>
          } @else {
            <p class="text-sm text-fg-muted">
              {{ 'admin.rateLimits.providers.empty' | transloco }}
            </p>
          }
        </div>

        <!-- The two structural gaps every count above has, called out plainly rather than
         implied — a missing number here would otherwise read as zero requests. -->
        <div class="flex flex-col gap-1 text-xs text-fg-muted">
          <p>{{ 'admin.rateLimits.hints.clientSevenTv' | transloco }}</p>
          <p>{{ 'admin.rateLimits.hints.policyFreeRoutes' | transloco }}</p>
        </div>
      </div>
      <button dialog-actions type="button" appButton="outline" buttonSize="lg" (click)="close()">
        {{ 'common.close' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class AdminRateLimitDetailsDialog {
  protected readonly data = inject<RateLimitDetailsDialogData>(DIALOG_DATA).snapshot;
  private readonly dialogRef = inject<DialogRef<void>>(DialogRef);
  private readonly languageService = inject(LanguageService);

  protected close(): void {
    this.dialogRef.close();
  }

  protected formatDateTime(iso: string | null): string {
    if (!iso) {
      return NO_VALUE;
    }
    return new Date(iso).toLocaleString(toLocale(this.languageService.lang()), {
      dateStyle: 'short',
      timeStyle: 'medium',
    });
  }

  protected formatNumber(value: number | null): string {
    if (value === null || value === undefined) {
      return NO_VALUE;
    }
    return new Intl.NumberFormat(toLocale(this.languageService.lang())).format(value);
  }

  protected formatRetryAfterSeconds(value: number | null): string {
    return value === null ? NO_VALUE : `${this.formatNumber(value)} s`;
  }

  // A token bucket has a refill rate, a fixed window has a reset period — the two never coexist on
  // one policy (RateLimitPolicyDescriptor in AdminEndpoints.cs), so the key picks the one that
  // applies instead of the template guessing from which fields happen to be non-null.
  protected refillDescriptionKey(policy: RateLimitPolicySnapshot): string {
    return policy.type === 'token-bucket'
      ? 'admin.rateLimits.policies.refillTokenBucket'
      : 'admin.rateLimits.policies.refillFixedWindow';
  }

  protected refillParams(policy: RateLimitPolicySnapshot): Record<string, string> {
    return policy.type === 'token-bucket'
      ? {
          tokensPerPeriod: this.formatNumber(policy.tokensPerPeriod),
          periodSeconds: this.formatNumber(policy.replenishmentPeriodSeconds),
        }
      : { windowSeconds: this.formatNumber(policy.windowSeconds) };
  }

  protected headerSampleParams(sample: ProviderRateLimitHeaderSample): Record<string, string> {
    return {
      limit: sample.limit ?? NO_VALUE,
      remaining: sample.remaining ?? NO_VALUE,
      reset: sample.reset ?? NO_VALUE,
      observedAt: this.formatDateTime(sample.observedAtUtc),
    };
  }
}

export function openRateLimitDetailsDialog(
  dialog: Dialog,
  snapshot: RateLimitTelemetrySnapshot,
): DialogRef<void> {
  return openAppDialog<void, RateLimitDetailsDialogData>(dialog, AdminRateLimitDetailsDialog, {
    data: { snapshot },
  });
}
