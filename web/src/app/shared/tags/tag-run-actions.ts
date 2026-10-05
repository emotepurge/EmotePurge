import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { DeleteRunInfo, SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { ImportRunInfo, SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import {
  SEVEN_TV_RUN_KIND_LABEL_KEY,
  SevenTvRunArbiter,
  SevenTvRunKind,
} from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { EmoteTagSummary } from '../../core/tags/emote-tag.model';
import { EmoteTagService } from '../../core/tags/emote-tag.service';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';
import {
  TagRunFlowDeps,
  TagRunNotice,
  TagRunRequest,
  startTagPlayInFlow,
} from './tag-play-in-flow';
import { startTagRemovalFlow } from './tag-removal-flow';

/** Element ids for the lock reason, unique per instance. */
let nextReasonId = 0;

/** A transient message for the host's own status region (spec 7.1/5, 7.2/7). */
export interface TagRunFeedback {
  key: string;
  params: Record<string, unknown>;
}

/** A settled tag play-in, identified by its run and the report's end state — `null` for anything
 *  else. A tag-less import run carries `tag: null` (F37). */
export function settledTagPlayIn(run: ImportRunInfo | null): string | null {
  return run !== null && run.tag !== null && run.phase === 'closed'
    ? `${run.runId}:${run.tagPlacementReport}`
    : null;
}

/** The same for a tag clear-out. A tag-less delete run has **no** `tag` field (`undefined`, F37). */
export function settledTagRemoval(run: DeleteRunInfo | null): string | null {
  return run !== null && run.tag !== undefined && run.phase === 'closed'
    ? `${run.runId}:${run.tagRemovalReport}`
    : null;
}

/**
 * "Einspielen" and "Ausräumen" for one tag (#201 T-C, spec 9.2/9.4) — the same component on the
 * usage page's filter row and on the tags page, so both surfaces run identical flows (9.5).
 *
 * Renders nothing unless the host says so (`enabled`: runs switched on, fine pointer, a known
 * active set). "Ausräumen" exists only for a tag played in to that set (spec 7.2: missing, not
 * locked — there is nothing to explain beyond the state the host shows next to it). Both lock
 * while any 7TV run holds the start (`startLocked`, without a hint: the run's dock is the hint,
 * UI-Designsprache §4.2) and while this component's own flow is busy.
 *
 * One exception to that silence (plan 3.8, §10 "every lock explains itself"): a lock held by a run
 * whose surface the host does not mount (`unshownRunKinds` — on the tags page the undo, started on
 * the usage page) has no dock to explain it, so the reason stands as text beside the buttons and
 * is their `aria-describedby`.
 *
 * `completed` fires when a tag run this page can see closes, or its tag report succeeds on a
 * retry, and after a report sent without a run — the host reloads the tag's numbers then.
 * `started` fires on every click that starts a flow (and on a retry) — the host clears its own
 * stale run notices then.
 */
@Component({
  selector: 'app-tag-run-actions',
  imports: [Button, NoticeBanner, TranslocoPipe],
  template: `
    @if (enabled()) {
      <div class="flex flex-col gap-2">
        <div class="flex flex-wrap items-center gap-2">
          <button
            type="button"
            appButton="neutral"
            class="disabled:cursor-not-allowed"
            [disabled]="locked()"
            [attr.aria-describedby]="otherRunKey() ? otherRunReasonId : null"
            (click)="playIn()"
          >
            {{ 'tags.actions.playIn' | transloco }}
          </button>
          @if (tag().active) {
            <button
              type="button"
              appButton="danger"
              class="disabled:cursor-not-allowed"
              [disabled]="locked()"
              [attr.aria-describedby]="otherRunKey() ? otherRunReasonId : null"
              (click)="remove()"
            >
              {{ 'tags.actions.remove' | transloco }}
            </button>
          }
        </div>
        @if (otherRunKey(); as kindKey) {
          <p [id]="otherRunReasonId" class="text-xs text-fg-muted">
            {{ 'tags.errors.otherRunActive' | transloco: { kind: (kindKey | transloco) } }}
          </p>
        }
        @if (notice(); as current) {
          <app-notice-banner variant="error">
            @if (current.leadKey) {
              {{ current.leadKey | transloco }}
            }
            {{ current.key | transloco: current.params ?? {} }}
            @if (current.retry) {
              <button
                notice-action
                type="button"
                appButton="neutral"
                [disabled]="locked()"
                (click)="retry(current)"
              >
                {{ 'tags.errors.retry' | transloco }}
              </button>
            }
          </app-notice-banner>
        }
      </div>
    }
  `,
})
export class TagRunActions {
  readonly channelName = input.required<string>();
  readonly tag = input.required<EmoteTagSummary>();
  /** The channel's active set, as the host sees it live. */
  readonly activeEmoteSetId = input<string | null>(null);
  /** The active set's display name, `null` to fall back to its id. */
  readonly setName = input<string | null>(null);
  /** The host's gate: tag runs switched on, a fine pointer, a known active set. */
  readonly enabled = input(false);
  /** Run kinds whose surface (dock section) this host does not mount — a lock by one of them is
   *  explained in text here, since nothing else on the page would (plan 3.8). */
  readonly unshownRunKinds = input<readonly SevenTvRunKind[]>([]);

  readonly completed = output<void>();
  /** A click (or a retry) started a flow. */
  readonly started = output<void>();
  readonly feedback = output<TagRunFeedback>();

  protected readonly arbiter = inject(SevenTvRunArbiter);
  private readonly dialog = inject(Dialog);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  private readonly httpClient = inject(HttpClient);
  private readonly tokenService = inject(SevenTvTokenService);
  private readonly importService = inject(SevenTvImportService);
  private readonly deleteService = inject(SevenTvDeleteService);
  private readonly tagService = inject(EmoteTagService);
  private readonly translocoService = inject(TranslocoService);
  private readonly destroyRef = inject(DestroyRef);

  /** This component's flow is between its click and its hand-over (or its report). */
  protected readonly pending = signal(false);
  protected readonly notice = signal<TagRunNotice | null>(null);
  protected readonly locked = computed(() => this.arbiter.startLocked() || this.pending());
  /** The run-kind noun key of a lock the host has no surface for, `null` otherwise — a lock by a
   *  run whose dock the page shows stays without text (§4.2). */
  protected readonly otherRunKey = computed(() => {
    const kind = this.arbiter.activeRun();
    return kind !== null && this.unshownRunKinds().includes(kind)
      ? SEVEN_TV_RUN_KIND_LABEL_KEY[kind]
      : null;
  });
  protected readonly otherRunReasonId = `tag-run-other-run-${nextReasonId++}`;
  /** Which tag of which channel this component acts for — by id, so a reloaded summary of the same
   *  tag is no change. */
  private readonly subject = computed(() => `${this.channelName()}\u0000${this.tag().id}`);

  private readonly deps: TagRunFlowDeps = {
    dialog: this.dialog,
    emoteAdminService: this.emoteAdminService,
    emoteSetService: this.emoteSetService,
    httpClient: this.httpClient,
    tokenService: this.tokenService,
    importService: this.importService,
    arbiter: this.arbiter,
    deleteService: this.deleteService,
    tagService: this.tagService,
    translocoService: this.translocoService,
    destroyRef: this.destroyRef,
  };

  constructor() {
    // A banner, and above all its retry, belongs to the tag it was raised for: the retry replays
    // that click's frozen request. Once the host shows another tag (or channel), "Try again" would
    // run the old tag's flow under the new one's buttons — so the banner goes with the change.
    effect(() => {
      this.subject();
      untracked(() => this.notice.set(null));
    });

    // A run that was already settled when this component mounted is not news; only a change seen
    // from here on is.
    let first = true;
    let lastPlayIn: string | null = null;
    let lastRemoval: string | null = null;
    effect(() => {
      const playIn = settledTagPlayIn(this.importService.run());
      const removal = settledTagRemoval(this.deleteService.run());
      const changed =
        (playIn !== null && playIn !== lastPlayIn) || (removal !== null && removal !== lastRemoval);
      lastPlayIn = playIn;
      lastRemoval = removal;
      if (first) {
        first = false;
        return;
      }
      if (changed) {
        untracked(() => this.completed.emit());
      }
    });
  }

  protected playIn(): void {
    // Guards a click that outraces the lock arriving, like every other 7TV start trigger.
    if (this.locked()) {
      return;
    }
    this.started.emit();
    startTagPlayInFlow(this.deps, this.request());
  }

  protected remove(): void {
    if (this.locked()) {
      return;
    }
    this.started.emit();
    startTagRemovalFlow(this.deps, this.request());
  }

  protected retry(notice: TagRunNotice): void {
    if (this.locked()) {
      return;
    }
    this.started.emit();
    notice.retry?.();
  }

  /** Channel, tag and set name frozen at the click; the active set stays live for the flow's own
   *  comparisons. */
  private request(): TagRunRequest {
    const tag = this.tag();
    const subject = this.subject();
    return {
      channelName: this.channelName(),
      tag: { id: tag.id, name: tag.name },
      setName: this.setName(),
      activeEmoteSetId: this.activeEmoteSetId,
      pending: this.pending,
      notice: this.notice,
      isCurrent: () => this.subject() === subject,
      onFeedback: (key, params) => this.feedback.emit({ key, params }),
      onCompleted: () => this.completed.emit(),
    };
  }
}
