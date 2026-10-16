import { Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AdminLogsService } from '../../core/admin/admin-logs.service';
import { LogStatus, isLogCaptureComplete } from '../../core/admin/admin-logs.model';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { StatusBadge } from '../../shared/ui/status-badge';

/** One line of "why this number may be too low"; `key` is under `admin.monitoring.logErrors.reasons`. */
export interface LogErrorsReason {
  key: 'stale' | 'disabled' | 'missing' | 'incomplete' | 'retentionOverdue';
  params: { source?: string; time?: string };
}

/**
 * The "recorded errors of the last 24 h" row at the top of /admin/monitoring (spec section 11).
 * It owns its own request so a failure shows "not available" here and nowhere else; the page
 * refreshes it on its existing visibility-aware dashboard timer via {@link reload} and never on
 * `logs.appended` (the row only has to be noticed within a minute or two, not be live).
 */
@Component({
  selector: 'app-admin-log-errors-row',
  imports: [RouterLink, StatusBadge, TranslocoPipe],
  template: `
    <section
      class="flex min-h-11 flex-wrap items-center justify-between gap-x-4 gap-y-1 border-y border-border py-2"
      data-testid="admin-log-errors"
      [attr.aria-busy]="loading()"
    >
      <!-- One line: label, value and the reasons it may be too low (spec §11 "in derselben Zeile").
           It wraps on a narrow screen, but on the desktop the loaded row is as tall as its skeleton. -->
      <div class="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 text-sm">
        <h3 class="font-medium text-fg-body">
          {{ 'admin.monitoring.logErrors.label' | transloco }}
        </h3>
        @if (loading()) {
          <span role="status" [attr.aria-label]="'common.loading' | transloco">
            <span class="app-skeleton block h-5 w-24" aria-hidden="true"></span>
          </span>
        } @else if (status(); as s) {
          @if (complete()) {
            @if (s.coverage24h.recordedErrors === 0) {
              <span class="text-fg-muted" data-testid="admin-log-errors-value">
                {{ 'admin.monitoring.logErrors.none' | transloco }}
              </span>
            } @else {
              <app-status-badge tone="danger">
                <span data-testid="admin-log-errors-value">
                  {{ formatNumber(s.coverage24h.recordedErrors) }}
                </span>
              </app-status-badge>
            }
          } @else {
            <app-status-badge tone="warning">
              <span data-testid="admin-log-errors-value">
                @if (s.coverage24h.recordedErrors === 0) {
                  {{ 'admin.monitoring.logErrors.zeroIncomplete' | transloco }}
                } @else {
                  {{
                    'admin.monitoring.logErrors.atLeast'
                      | transloco: { count: formatNumber(s.coverage24h.recordedErrors) }
                  }}
                }
              </span>
            </app-status-badge>
          }
          @for (reason of reasons(); track reason.key + (reason.params.source ?? '')) {
            <span class="text-xs text-warning-fg">
              <span aria-hidden="true">· </span
              ><span data-testid="admin-log-errors-reason">{{
                'admin.monitoring.logErrors.reasons.' + reason.key | transloco: reason.params
              }}</span>
            </span>
          }
        } @else {
          <span class="text-fg-muted" data-testid="admin-log-errors-unavailable">
            {{ 'admin.monitoring.logErrors.unavailable' | transloco }}
          </span>
        }
      </div>
      <a
        class="inline-flex min-h-11 items-center px-1 text-sm text-accent-fg underline-offset-2 hover:underline lg:min-h-6"
        routerLink="/admin/logs"
        [queryParams]="{ q: 'level>=error', range: '24h' }"
      >
        {{ 'admin.monitoring.logErrors.view' | transloco }}<span aria-hidden="true"> →</span
        ><span class="sr-only"> {{ 'admin.monitoring.logErrors.label' | transloco }}</span>
      </a>
    </section>
  `,
})
export class AdminLogErrorsRow {
  private readonly logs = inject(AdminLogsService);
  private readonly languageService = inject(LanguageService);

  private readonly statusResource = rxResource({ stream: () => this.logs.getStatus() });

  // value() throws in the error state, so it is only read behind hasValue(). A failed *reload* keeps
  // no value either, which is the honest outcome: stale "keine" must not outlive a failed check.
  protected readonly status = computed(() =>
    this.statusResource.hasValue() ? this.statusResource.value() : undefined,
  );
  protected readonly loading = computed(
    () => this.statusResource.status() === 'loading' && !this.statusResource.hasValue(),
  );
  protected readonly complete = computed(() => {
    const status = this.status();
    return status !== undefined && isLogCaptureComplete(status);
  });
  protected readonly reasons = computed(() => {
    const status = this.status();
    return status ? this.reasonsFor(status) : [];
  });

  reload(): void {
    this.statusResource.reload();
  }

  protected formatNumber(value: number): string {
    return new Intl.NumberFormat(toLocale(this.languageService.lang())).format(value);
  }

  private reasonsFor(status: LogStatus): LogErrorsReason[] {
    const reasons: LogErrorsReason[] = [];
    for (const source of status.sources) {
      if (source.state === 'stale' && source.lastFlushUtc) {
        reasons.push({
          key: 'stale',
          params: { source: source.source, time: this.utcTime(source.lastFlushUtc) },
        });
      } else if (source.state === 'stale') {
        // No last flush to name: say the plain "no status report" rather than "since —".
        reasons.push({ key: 'missing', params: { source: source.source } });
      } else if (source.state === 'disabled' || source.state === 'missing') {
        reasons.push({ key: source.state, params: { source: source.source } });
      }
    }
    // A source that is not healthy is already explained above; a coverage gap is only news for a
    // source whose heartbeat looks fine.
    for (const gap of status.coverage24h.incompleteSince) {
      const state = status.sources.find((s) => s.source === gap.source)?.state;
      if (state === undefined || state === 'healthy') {
        reasons.push({
          key: 'incomplete',
          params: { source: gap.source, time: this.utcDateTime(gap.lastIncompleteAtUtc) },
        });
      }
    }
    if (status.retention.overdue) {
      reasons.push({ key: 'retentionOverdue', params: {} });
    }
    return reasons;
  }

  private utcTime(iso: string | null): string {
    if (!iso) {
      return '—';
    }
    return (
      new Date(iso).toLocaleTimeString(toLocale(this.languageService.lang()), {
        hour: '2-digit',
        minute: '2-digit',
        hour12: false,
        timeZone: 'UTC',
      }) + ' UTC'
    );
  }

  private utcDateTime(iso: string): string {
    return (
      new Date(iso).toLocaleString(toLocale(this.languageService.lang()), {
        dateStyle: 'short',
        timeStyle: 'short',
        hour12: false,
        timeZone: 'UTC',
      }) + ' UTC'
    );
  }
}
