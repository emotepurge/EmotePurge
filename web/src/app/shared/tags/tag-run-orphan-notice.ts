import { Component, computed, inject, input, output } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';

import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { Button } from '../ui/button';
import type { TagRunFeedback } from './tag-run-actions';
import { TagRunNotice, TagRunNoticeSink } from './tag-run-notice-sink';

/** Element ids for the region, unique per instance. */
let nextRegionId = 0;

/**
 * The page's rendering of `TagRunNoticeSink` (#201 T-C): what a tag flow still said after its
 * `TagRunActions` was torn down (on the tags page, by a tag list reloaded with a new set) — for the
 * channel this page shows.
 *
 * The notice stands in for the destroyed component's error banner, so it stays like that banner did
 * (§4.5: a state, not an acknowledgement) until the user closes it or a new tag run starts. A
 * permanently mounted sr-only `role="status"` region carries the text and a visible `aria-hidden`
 * twin shows it (§4.5: a region that mounts with its text announces nothing); the buttons stay in
 * the accessibility tree, the retry described by the region. The retry — a report resend, the only
 * one a sink notice keeps — locks while any 7TV run holds the start, like every other start.
 *
 * The sink's events come out as the same `feedback`/`completed` outputs `TagRunActions` has, so the
 * page wires both to the same handlers.
 */
@Component({
  selector: 'app-tag-run-orphan-notice',
  imports: [Button, TranslocoPipe],
  host: { class: 'contents' },
  template: `
    <span [id]="regionId" role="status" class="sr-only">
      @if (notice(); as current) {
        @if (current.leadKey) {
          {{ current.leadKey | transloco }}
        }
        {{ current.key | transloco: current.params ?? {} }}
      }
    </span>
    @if (notice(); as current) {
      <div class="flex w-full flex-wrap items-center gap-x-3 gap-y-1 text-sm">
        <p aria-hidden="true" class="min-w-0 text-danger-fg">
          @if (current.leadKey) {
            {{ current.leadKey | transloco }}
          }
          {{ current.key | transloco: current.params ?? {} }}
        </p>
        @if (current.retry) {
          <button
            type="button"
            appButton="neutral"
            class="disabled:cursor-not-allowed"
            [disabled]="arbiter.startLocked()"
            [attr.aria-describedby]="regionId"
            (click)="retry(current)"
          >
            {{ 'tags.errors.retry' | transloco }}
          </button>
        }
        <button type="button" appButton="outline" (click)="dismiss()">
          {{ 'common.close' | transloco }}
        </button>
      </div>
    }
  `,
})
export class TagRunOrphanNotice {
  readonly channelName = input.required<string>();

  readonly completed = output<void>();
  readonly feedback = output<TagRunFeedback>();

  protected readonly arbiter = inject(SevenTvRunArbiter);
  private readonly sink = inject(TagRunNoticeSink);

  protected readonly regionId = `tag-run-orphan-notice-${nextRegionId++}`;
  /** The sink's notice when it was raised for this page's channel. */
  protected readonly notice = computed(() => {
    const orphaned = this.sink.notice();
    return orphaned !== null && orphaned.channelName === this.channelName()
      ? orphaned.notice
      : null;
  });

  constructor() {
    this.sink.events.pipe(takeUntilDestroyed()).subscribe((event) => {
      if (event.channelName !== this.channelName()) {
        return;
      }
      if (event.kind === 'feedback') {
        this.feedback.emit({ key: event.key, params: event.params });
      } else {
        this.completed.emit();
      }
    });
  }

  protected retry(notice: TagRunNotice): void {
    // Guards a click that outraces the lock arriving, like every other 7TV start trigger.
    if (this.arbiter.startLocked()) {
      return;
    }
    this.sink.clear();
    notice.retry?.();
  }

  protected dismiss(): void {
    this.sink.clear();
  }
}
