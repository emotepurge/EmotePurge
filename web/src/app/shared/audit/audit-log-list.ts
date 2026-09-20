import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';

import { AuditRow } from './audit-row';

/**
 * The audit log's row list, without any of the fetching, filtering or paging around it — those
 * differ between the global-admin log and a channel's activity feed, the rows do not.
 *
 * Read-only by construction: entries are written by the services that perform the actions, inside
 * those actions' own transactions, and nothing in the UI can create, edit or delete one.
 */
@Component({
  selector: 'app-audit-log-list',
  imports: [RouterLink, TranslocoPipe],
  template: `
    <!-- Hairline-divided rows, same recipe as the other lists. A log is the clearest case for it:
         the rows are strictly homogeneous and their only job is to be read down the column, which a
         stack of bordered rectangles actively works against. -->
    <ul class="-mx-3 divide-y divide-border border-y border-border">
      @for (row of rows(); track row.id) {
        <li class="flex flex-col gap-1 px-3 py-3">
          <div class="flex flex-wrap items-baseline gap-x-3 gap-y-1">
            <span class="font-medium text-fg">
              @if (row.actionKey; as key) {
                {{ key | transloco }}
              } @else {
                <!-- An action this build has no label for: better raw than hidden. -->
                {{ row.action }}
              }
            </span>
            @if (showChannel() && row.channelName; as channel) {
              <a
                [routerLink]="['/channels', channel, 'usage-stats']"
                class="max-w-full truncate font-medium text-fg-secondary underline-offset-2 hover:text-accent-fg hover:underline"
              >
                #{{ channel }}
              </a>
            }
          </div>

          <p class="flex flex-wrap gap-x-2 gap-y-1 text-xs text-fg-muted">
            <time [attr.datetime]="row.occurredAtUtc">{{ row.timestamp }}</time>
            <span aria-hidden="true">·</span>
            <span>{{ 'audit.actor' | transloco: { actor: row.actorLogin } }}</span>
            @if (row.detail; as detail) {
              <span aria-hidden="true">·</span>
              <span>{{ detail.key | transloco: detail.params }}</span>
            }
            @if (row.targetSet; as targetSet) {
              <span aria-hidden="true">·</span>
              <span>{{ targetSet.key | transloco: targetSet.params }}</span>
            }
          </p>
        </li>
      }
    </ul>
  `,
})
export class AuditLogList {
  readonly rows = input.required<AuditRow[]>();

  /**
   * Off inside a single channel's feed, where every row carries the same channel and the link would
   * point at the page the reader is already on.
   */
  readonly showChannel = input(true);
}
