import { Dialog } from '@angular/cdk/dialog';
import { DOCUMENT } from '@angular/common';
import { HttpClient } from '@angular/common/http';
import {
  Component,
  ElementRef,
  DestroyRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
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
import { TagRunFlowDeps, TagRunRequest, startTagPlayInFlow } from './tag-play-in-flow';
import { startTagRemovalFlow } from './tag-removal-flow';
import { TagRunNotice, TagRunNoticeSink } from './tag-run-notice-sink';

/** Element ids for the lock reason, unique per instance. */
let nextReasonId = 0;

/** A transient message for the host's own status region (spec 7.1/5, 7.2/7). */
export interface TagRunFeedback {
  key: string;
  params: Record<string, unknown>;
}

/** A settled tag play-in, identified by its run and the report's end state — `null` for anything
 *  else. A tag-less import run carries `tag: null` — unlike a delete run's, see below; never cross
 *  the two tests. */
export function settledTagPlayIn(run: ImportRunInfo | null): string | null {
  return run !== null && run.tag !== null && run.phase === 'closed'
    ? `${run.runId}:${run.tagPlacementReport}`
    : null;
}

/** Where focus goes when "Einspielen" leaves the DOM under the keyboard user (a play-in made
 *  nothing missing any more): "Ausräumen" if it is there, otherwise the host's stable fallback (the
 *  detail heading). `none` when focus is not lost — the user has moved on, leave it alone. */
export function playInFocusTarget(state: {
  focusLost: boolean;
  removeShown: boolean;
}): 'remove' | 'fallback' | 'none' {
  if (!state.focusLost) {
    return 'none';
  }
  return state.removeShown ? 'remove' : 'fallback';
}

/** The same for a tag clear-out. A tag-less delete run has **no** `tag` field (`undefined`). */
export function settledTagRemoval(run: DeleteRunInfo | null): string | null {
  return run !== null && run.tag !== undefined && run.phase === 'closed'
    ? `${run.runId}:${run.tagRemovalReport}`
    : null;
}

/**
 * "Einspielen" and "Ausräumen" for one tag (#201 T-C, spec 9.4) — hosted by the tags page, the
 * only surface that starts tag runs (operator feedback 2026-10-05: the usage page shows what is in
 * the set, not what is missing).
 *
 * Renders nothing unless the host says so (`enabled`: runs switched on, fine pointer, a known
 * active set). "Einspielen" exists only while the tag has an emote missing from the set,
 * "Ausräumen" only for a tag played in to that set (spec 7.2: missing, not locked — there is
 * nothing to explain beyond the state the host shows next to it). Both lock
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
 *
 * The flows outlive this component (their dialogs, the import hook, a report in flight). Once it is
 * torn down, whatever they still say goes to `TagRunNoticeSink`, which the host page renders; a new
 * start from here clears that page-level notice as it clears this component's own banner.
 */
@Component({
  selector: 'app-tag-run-actions',
  imports: [Button, NoticeBanner, TranslocoPipe],
  template: `
    @if (enabled() && (anyButton() || notice())) {
      <div class="flex flex-col gap-2">
        @if (anyButton()) {
          <div class="flex flex-wrap items-center gap-2">
            @if (playInShown()) {
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
            }
            @if (tag().active) {
              <button
                #removeButton
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
  // No box of its own: with nothing to show the host's flex gap must not see an empty item.
  host: { class: 'contents' },
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
  /** "Einspielen" left the DOM under the user and there is no "Ausräumen" to take focus — the host
   *  moves it to a stable target of its own. */
  readonly focusLost = output<void>();

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
  private readonly noticeSink = inject(TagRunNoticeSink);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private readonly document = inject(DOCUMENT);
  private readonly removeButton = viewChild<ElementRef<HTMLButtonElement>>('removeButton');

  /** This component's flow is between its click and its hand-over (or its report). */
  protected readonly pending = signal(false);
  protected readonly notice = signal<TagRunNotice | null>(null);
  /** "Einspielen" exists only while the tag has an emote that is not in the set (spec 7.2: missing,
   *  not locked) — read off the summary the host already holds. Without a count (`inSetCount:
   *  null`, no set to count in) nothing is known to be present, so a tag with entries offers it. A
   *  click that outraces a change of the set still lands in the flow's own "all present" path. */
  protected readonly playInShown = computed(() => {
    const tag = this.tag();
    return tag.entryCount > (tag.inSetCount ?? 0);
  });
  /** At least one run button stands; the button row and the lock reason exist only then. */
  protected readonly anyButton = computed(() => this.playInShown() || this.tag().active);
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
  /** The last click that started a play-in came from this component's own button — the one that
   *  holds focus again once the dialog closes. */
  private playInStarted = false;
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

    // After a play-in that left nothing missing, "Einspielen" is removed from the DOM — together
    // with the focus CDK gave back to it. Move it on instead of letting it drop to the body.
    let wasShown: boolean | null = null;
    effect(() => {
      const shown = this.playInShown();
      const gone = wasShown === true && !shown;
      wasShown = shown;
      if (gone && this.playInStarted) {
        this.playInStarted = false;
        afterNextRender(() => this.moveFocusOn(), { injector: this.injector });
      }
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
    this.noticeSink.clear();
    this.playInStarted = true;
    startTagPlayInFlow(this.deps, this.request());
  }

  protected remove(): void {
    if (this.locked()) {
      return;
    }
    this.started.emit();
    this.noticeSink.clear();
    startTagRemovalFlow(this.deps, this.request());
  }

  protected retry(notice: TagRunNotice): void {
    if (this.locked()) {
      return;
    }
    this.started.emit();
    this.noticeSink.clear();
    notice.retry?.();
  }

  private moveFocusOn(): void {
    const active = this.document.activeElement;
    const target = playInFocusTarget({
      focusLost: active === null || active === this.document.body,
      removeShown: this.removeButton() !== undefined,
    });
    if (target === 'remove') {
      this.removeButton()?.nativeElement.focus();
    } else if (target === 'fallback') {
      this.focusLost.emit();
    }
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
      hostAlive: () => !this.destroyRef.destroyed,
      sink: this.noticeSink,
      onFeedback: (key, params) => this.feedback.emit({ key, params }),
      onCompleted: () => this.completed.emit(),
    };
  }
}
