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

/** One of the two run buttons. */
export type TagRunButton = 'playIn' | 'remove';

/** Where focus goes when the run button the user clicked leaves the DOM under them ("Ins Set holen"
 *  after a play-in left nothing missing, "Aus dem Set entfernen" after a clear-out left nothing to clear): the
 *  other run button if it is there, otherwise the host's stable fallback (the detail heading).
 *  `none` when focus is not lost — the user has moved on, leave it alone. */
export function runButtonFocusTarget(state: {
  focusLost: boolean;
  otherShown: boolean;
}): 'other' | 'fallback' | 'none' {
  if (!state.focusLost) {
    return 'none';
  }
  return state.otherShown ? 'other' : 'fallback';
}

/** The same for a tag clear-out. A tag-less delete run has **no** `tag` field (`undefined`). */
export function settledTagRemoval(run: DeleteRunInfo | null): string | null {
  return run !== null && run.tag !== undefined && run.phase === 'closed'
    ? `${run.runId}:${run.tagRemovalReport}`
    : null;
}

/**
 * "Ins Set holen" and "Aus dem Set entfernen" for one tag (#201 T-C, spec 9.4) — hosted by the tags page, the
 * only surface that starts tag runs (operator feedback 2026-10-05: the usage page shows what is in
 * the set, not what is missing).
 *
 * Renders nothing unless the host says so (`enabled`: runs switched on, fine pointer, a known
 * active set). "Ins Set holen" exists only while the tag has an emote missing from the set,
 * "Aus dem Set entfernen" while one of its emotes is in the set or it is played in there (`removeShown`; spec
 * 7.2: missing, not locked — there is nothing to explain beyond the state the host shows next to
 * it). Both lock
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
                #playInButton
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
            @if (removeShown()) {
              <button
                #removeButton
                type="button"
                appButton="danger"
                class="disabled:cursor-not-allowed"
                [disabled]="locked()"
                [attr.aria-describedby]="otherRunKey() ? otherRunReasonId : null"
                (click)="remove()"
              >
                @if (markedCount() > 0) {
                  {{ 'tags.actions.removeCount' | transloco: { count: markedCount() } }}
                } @else {
                  {{ 'tags.actions.remove' | transloco }}
                }
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
  /** The host grid's marking (emote ids). A clear-out started with one proposes exactly the marked
   *  emotes; it is copied at the click, so the open dialog does not follow the grid. */
  readonly markedIds = input<readonly string[]>([]);

  readonly completed = output<void>();
  /** A click (or a retry) started a flow. */
  readonly started = output<void>();
  readonly feedback = output<TagRunFeedback>();
  /** The clicked run button left the DOM under the user and the other one is not there to take
   *  focus — the host moves it to a stable target of its own. */
  readonly focusLost = output<void>();
  /** A confirmed clear-out of the tag this host still shows went ahead (`onClearOutCommitted`) — the
   *  host drops its marking. */
  readonly clearOutCommitted = output<void>();

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
  private readonly playInButton = viewChild<ElementRef<HTMLButtonElement>>('playInButton');
  private readonly removeButton = viewChild<ElementRef<HTMLButtonElement>>('removeButton');

  /** This component's flow is between its click and its hand-over (or its report). */
  protected readonly pending = signal(false);
  protected readonly notice = signal<TagRunNotice | null>(null);
  /** "Ins Set holen" exists only while the tag has an emote that is not in the set (spec 7.2: missing,
   *  not locked) — read off the summary the host already holds. Without a count (`inSetCount:
   *  null`, no set to count in) nothing is known to be present, so a tag with entries offers it. A
   *  click that outraces a change of the set still lands in the flow's own "all present" path. */
  protected readonly playInShown = computed(() => {
    const tag = this.tag();
    return tag.entryCount > (tag.inSetCount ?? 0);
  });
  /** "Aus dem Set entfernen" exists while there is something to clear out (operator decision 2026-10-05,
   *  superseding spec 7.2's "only for a played-in tag"): an emote of the tag in the set — whether
   *  the tag was played in or its emotes were all there already, which leaves it no way to become
   *  active — or the tag being played in at all. The latter keeps a played-in tag with nothing in
   *  the set clearable (an undo of its play-in, entries taken out of it): the clear-out with nothing
   *  ticked is what deactivates it (13.2). */
  protected readonly removeShown = computed(() => {
    const tag = this.tag();
    return tag.active || (tag.inSetCount ?? 0) > 0;
  });
  /** How many emotes the host's grid has marked. The clear-out button carries the number, as the
   *  tags page's own dock button does — a marking is the clear-out's proposal (spec 9.4). */
  protected readonly markedCount = computed(() => this.markedIds().length);
  /** At least one run button stands; the button row and the lock reason exist only then. */
  protected readonly anyButton = computed(() => this.playInShown() || this.removeShown());
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
  /** The run button whose click started the current flow — the one CDK gives focus back to once
   *  the dialog closes, and so the one that may leave the DOM under the user. `null` once that has
   *  been handled, and after a flow that ended without a run (see the constructor). */
  private focusOwner: TagRunButton | null = null;
  /** A report without a run succeeded since the last click (n = 0, "all present"): the flow ended
   *  in a state change, so the button may still go. */
  private reportCompletedSinceClick = false;
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
      untracked(() => {
        this.notice.set(null);
        // Another tag's buttons: a click on the previous one says nothing about them.
        this.focusOwner = null;
      });
    });

    // After a run that left the clicked button nothing to do — a play-in with nothing missing any
    // more, a clear-out with nothing left in the set — that button is removed from the DOM,
    // together with the focus CDK gave back to it. Move it on instead of letting it drop to the body.
    let shownBefore: Record<TagRunButton, boolean> | null = null;
    effect(() => {
      const shown: Record<TagRunButton, boolean> = {
        playIn: this.playInShown(),
        remove: this.removeShown(),
      };
      const before = shownBefore;
      shownBefore = shown;
      const owner = this.focusOwner;
      if (before !== null && owner !== null && before[owner] && !shown[owner]) {
        this.focusOwner = null;
        afterNextRender(() => this.moveFocusOn(owner), { injector: this.injector });
      }
    });

    // A flow that ended without a run — a dismissed dialog or token prompt, an abort, a blocked
    // step — changes nothing the button could go for; a later, unrelated change (a live reload
    // after another tab's run) must not move focus on its behalf. A clear-out's hand-over to its
    // run, and a report without a run that succeeded, keep the owner. The play-in's hand-over to
    // the import flow cannot tell a dismissed import dialog from a started run; only its aborts
    // (a notice) count here.
    let wasPending = false;
    effect(() => {
      const pending = this.pending();
      const ended = wasPending && !pending;
      wasPending = pending;
      if (ended) {
        untracked(() => this.releaseFocusOwnerIfNoRun());
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
    this.claimFocus('playIn');
    startTagPlayInFlow(this.deps, this.request());
    this.releaseFocusOwnerIfEndedAtOnce();
  }

  protected remove(): void {
    if (this.locked()) {
      return;
    }
    this.started.emit();
    this.noticeSink.clear();
    this.claimFocus('remove');
    startTagRemovalFlow(this.deps, this.request());
    this.releaseFocusOwnerIfEndedAtOnce();
  }

  protected retry(notice: TagRunNotice): void {
    if (this.locked()) {
      return;
    }
    this.started.emit();
    this.noticeSink.clear();
    notice.retry?.();
  }

  private claimFocus(button: TagRunButton): void {
    this.focusOwner = button;
    this.reportCompletedSinceClick = false;
  }

  /** A flow can end before its start call returns (a step that answers at once); the `pending`
   *  effect never sees that `true`, so the click handler asks itself. */
  private releaseFocusOwnerIfEndedAtOnce(): void {
    if (!this.pending()) {
      this.releaseFocusOwnerIfNoRun();
    }
  }

  /** Called once `pending` has ended: forgets the clicked button when the flow stopped short of
   *  anything that could change the tag (see the constructor). */
  private releaseFocusOwnerIfNoRun(): void {
    if (this.focusOwner === null) {
      return;
    }
    if (this.notice() !== null) {
      this.focusOwner = null;
      return;
    }
    if (this.focusOwner !== 'remove' || this.reportCompletedSinceClick) {
      return;
    }
    const run = this.deleteService.run();
    const handedOver = run !== null && run.tag?.tagId === this.tag().id && run.phase !== 'closed';
    if (!handedOver) {
      this.focusOwner = null;
    }
  }

  private moveFocusOn(gone: TagRunButton): void {
    const active = this.document.activeElement;
    const other = gone === 'playIn' ? this.removeButton() : this.playInButton();
    const target = runButtonFocusTarget({
      focusLost: active === null || active === this.document.body,
      otherShown: other !== undefined,
    });
    if (target === 'other') {
      other?.nativeElement.focus();
    } else if (target === 'fallback') {
      this.focusLost.emit();
    }
  }

  /** Channel, tag, set name and the grid's marking frozen at the click; the active set stays live
   *  for the flow's own comparisons. */
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
      onCompleted: () => {
        this.reportCompletedSinceClick = true;
        this.completed.emit();
      },
      markedIds: [...this.markedIds()],
      // Only for the tag it was clicked on: once the host shows another one, the marking it would
      // clear belongs to that one.
      onClearOutCommitted: () => {
        if (!this.destroyRef.destroyed && this.subject() === subject) {
          this.clearOutCommitted.emit();
        }
      },
    };
  }
}
