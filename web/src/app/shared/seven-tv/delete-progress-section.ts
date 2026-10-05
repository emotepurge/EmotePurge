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
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { catchError, finalize, of, timeout } from 'rxjs';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { pluralKey } from '../../core/i18n/plural';
import {
  DeleteQueueEmote,
  SevenTvDeleteService,
} from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { unknownCount } from '../../core/seven-tv/seven-tv-run-settlement';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { TargetCheckBlockReason } from '../../core/seven-tv/sync-report-outcome';
import { CSV_MIME } from '../export/csv';
import {
  ExportDialogData,
  FORMAT_EXPORT_OPTIONS_JSON_FIRST,
  openExportDialog,
} from '../export/export-dialog';
import { JSON_MIME } from '../export/export-envelope';
import { downloadFile } from '../export/file-download';
import {
  buildPurgeRunProtocol,
  purgeRunCsv,
  purgeRunFilename,
  purgeRunJson,
} from '../export/purge-run-export';
import { Button } from '../ui/button';
import {
  clipToShown,
  filterAlreadyPresentForRestore,
  loadRestoreConfirmPreview,
  RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS,
  RestoreConfirmPreview,
  restoreConfirmPreviewUnavailable,
  RestoreFilterRow,
} from './already-present-filter';
import { DeleteAbortNotice, LIVE_ALIAS_READ_TIMEOUT_MS, refusedStartNotice } from './delete-flow';
import { ResolvedRestoreTarget, restoreStartTarget } from './restore-flow';
import { RestoreConfirmDialogData, openRestoreConfirmDialog } from './restore-confirm-dialog';
import { loadRestoreSlotPreview, RestoreSlotPreview } from './restore-slot-preview';
import { RunProgressPanel } from './run-progress-panel';
import { openSevenTvTokenPromptDialog } from './seven-tv-token-prompt-dialog';

/** Maps the shared pre-check's block reason (spec 6.2, `TargetCheckBlockReason`) to the restore
 *  entry's own `restore.errors.*` locale family (Plan-253 §6, Nr. 3) — mirrors the family every
 *  other pre-check caller uses under its own prefix (`restore.import.errors.*`, `massDelete.errors.*`,
 *  `import.errors.*`); the wording was finalized in #255 — all four share one sentence core per
 *  reason, only the set reference (`Das Set`/`Dieses Set` here) varies. */
function restoreTargetCheckReasonKey(reason: TargetCheckBlockReason): string {
  switch (reason) {
    case 'notEditable':
      return 'restore.errors.targetNotEditable';
    case 'notSelectable':
      return 'restore.errors.targetNotSelectable';
    case 'unavailable':
      return 'restore.errors.targetCheckUnavailable';
  }
}

/** One row the restore entry hands both duplicate checks: the queue row the run needs, plus the
 *  `uncertain` marker (#275) set from an `unknown` delete row — only the checks read it
 *  (`RestoreFilterRow.uncertain`), the run never does. */
type RestoreCandidate = DeleteQueueEmote & Pick<RestoreFilterRow, 'uncertain'>;

/** The finished delete run's rows the restore entry offers (#275, plan Festlegung 16): every `done`
 *  row — the emote provably left the set — and every `unknown` row, whose delete may or may not
 *  have landed; both with or without a local emote, since the restore is keyed by the 7TV id.
 *  `failed`/`cancelled` rows never left the set and stay out. Whether an `unknown` row is actually
 *  sent is the duplicate checks' call (`filterAlreadyPresentForRestore`, fail-closed), not this
 *  function's. */
function restorableItems(items: readonly RunQueueItem[]): RunQueueItem[] {
  return items.filter((item) => item.status === 'done' || item.status === 'unknown');
}

/**
 * The delete run's surface — progress, the protocol download, the unclear-rows lines and the
 * restore entry together with its whole pre-check chain — extracted from `MassDeletePanel` without
 * a behaviour change (#201 T-A). The panel mounts it in its own template, exactly where the block
 * used to sit, so the DOM nesting, the host pages' gates and every E2E locator scoped to
 * `app-mass-delete-panel` stay as they were. The tag page (#201 T-C) mounts it on its own, without
 * a panel: that is what this extraction is for.
 *
 * The restore **entry** lives here, not in the panel, because it hangs off the last delete run
 * (`SevenTvDeleteService.lastRun()`), never off the current selection — it reads the run's own
 * frozen set, channel and owner hint. The only thing it needs from its host is the set the host is
 * *showing* right now (`hostSelectedSetId`), for the confirmation's "not the set on screen" warning
 * (`foreignToView`). The chain is moved verbatim, not rebuilt on `startRestoreFlow`:
 * `restore-flow.ts` documents that this entry runs its own chain on purpose, and unifying the two
 * would be a behaviour question of its own.
 *
 * The two run latches (`deleted`/`reloadRequested`) stay in `MassDeletePanel`: they feed outputs
 * both host pages bind, and moving them would change that host contract. Only the
 * `protocolSaved` reset, which used to ride on the delete latch, moved here as an effect of its
 * own, so this section holds its state alone.
 *
 * `notice` is an output rather than a signal of this section's: the status region that announces
 * it belongs to the host — the panel's permanently mounted `role="status"` pair, or the tag page's
 * own region — and a region created together with its text announces nothing
 * (docs/UI-Designsprache.md §4.5). Like the panel's own chain, this one never clears a notice:
 * only the panel's next delete attempt does. Everything rendered here stays as it was, with the
 * run's announcements coming from the host page's `DockOutcomeAnnouncer`.
 *
 * `host: { class: 'contents' }`: inside the panel the host element would otherwise be a flex item
 * of its own, adding the column's gap below the action row even while this section renders
 * nothing — `display: contents` keeps the old layout, where the `@if` block left no box at all.
 */
@Component({
  selector: 'app-delete-progress-section',
  imports: [Button, RunProgressPanel, TranslocoPipe],
  host: { class: 'contents' },
  template: `
    @if (deleteService.isRunning() || deleteService.queue().length > 0) {
      <app-run-progress-panel
        [items]="deleteService.queue()"
        [isRunning]="deleteService.isRunning()"
        [settling]="deleteService.run()?.phase === 'settling'"
        [dismissible]="deleteService.run()?.phase === 'closed'"
        [syncReport]="deleteService.syncReport()"
        [syncReportReason]="deleteService.syncReportReason()"
        [rateLimitPauseSeconds]="deleteService.rateLimitPauseSeconds()"
        (cancelled)="deleteService.cancel()"
        (dismissed)="deleteService.reset()"
        (syncRetryRequested)="deleteService.retrySyncReport()"
      >
        <ng-container run-actions>
          @if (deleteService.lastRun(); as run) {
            <button type="button" appButton="neutral" (click)="openProtocolExport()">
              {{ 'massDelete.summary.downloadProtocol' | transloco }}
            </button>
            @if (unknownRowCount() > 0) {
              <!-- #275: the settled run still has rows 7TV's answer never clarified — the backend
                   resync this run already triggered (a report's own, or the client fallback for a
                   run with nothing to report) heals them without another click, but the admin
                   needs telling to go check the set directly, and that a restore from the protocol
                   below still offers these rows back in case the resync finds them still gone. -->
              <span class="text-xs text-fg-muted">
                {{ unknownRowsKey() | transloco: { count: unknownRowCount() } }}
              </span>
              <span class="text-xs text-fg-muted">
                {{ unknownInProtocolKey() | transloco }}
              </span>
            }
            @if (restoreOffered() && arbiter.activeRun() === null) {
              <!-- The two-tier *shape* of the destructive convention, not its colour: outline
                   triggers, the dialog's primary-solid executes — restore is constructive.
                   Disabled while restoreConfirmPending() (#255 P2a): the target check and the
                   open-time duplicate check both run before any dialog is on screen, and a
                   second click in that window must not start a second read racing towards a
                   second confirmation. Likewise while arbiter.startPending() (#280): a confirmed
                   start of any run has closed its confirmation, but its last live read still
                   decides whether the run starts, and the button must not look free meanwhile —
                   DockOutcomeAnnouncer speaks that wait. Hidden only once a run holds the
                   arbiter, as before; disabled rather than hidden here, so nothing jumps. -->
              <button
                type="button"
                appButton="outline"
                class="disabled:cursor-not-allowed"
                [disabled]="restoreConfirmPending() || arbiter.startPending()"
                (click)="openRestoreConfirm()"
              >
                {{ 'restore.button' | transloco }}
              </button>
            }
            @if (!protocolSaved()) {
              <!-- reset() wipes the run — the downloaded file is the only durable artifact. -->
              <span class="text-xs text-fg-muted">
                {{ 'massDelete.summary.protocolNotSaved' | transloco }}
              </span>
            }
          }
        </ng-container>
      </app-run-progress-panel>
    }
  `,
})
export class DeleteProgressSection {
  /** The set the host page is *showing* right now — `null` when it shows none. Not the run's set:
   *  the restore confirmation's `foreignToView` warning ("the run ran in a different set than the
   *  one you are looking at", spec E21) is a statement about the host's view, which the run's
   *  frozen set cannot stand in for. The panel binds its `setId()`, the tag page its active set. */
  readonly hostSelectedSetId = input.required<string | null>();

  /** What stopped the restore entry — a `restore.errors.*` block reason or a refused start (#256
   *  T4). Never `null`: clearing the notice is the host's job, as it always was (the panel clears
   *  it on its next delete attempt). */
  readonly notice = output<DeleteAbortNotice>();

  protected readonly deleteService = inject(SevenTvDeleteService);
  private readonly restoreService = inject(SevenTvRestoreService);
  /** Read by the template too (the restore entry's own gates), hence `protected`. */
  protected readonly arbiter = inject(SevenTvRunArbiter);
  private readonly tokenService = inject(SevenTvTokenService);
  /** The restore entry's shared pre-check (`resolveEditableSet`) and, for a non-active target, the
   *  live slot preview (spec #200, 8.3, K5) — see `handleRestoreConfirmPreview`. */
  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  /** The active target set's slot preview (`loadRestoreSlotPreview`) — cheap and not
   *  7TV-rate-limited, unlike the live per-set one. */
  private readonly emoteAdminService = inject(EmoteAdminService);
  /** Only for the direct reads against 7TV — both duplicate checks (#149 P1 fix). */
  private readonly httpClient = inject(HttpClient);
  private readonly dialog = inject(Dialog);
  private readonly destroyRef = inject(DestroyRef);
  /** Only for the refused-start notice's kind noun — every other string in this component goes
   *  through the template's own `TranslocoPipe`. */
  private readonly translocoService = inject(TranslocoService);

  /** Whether the current run's protocol was downloaded at least once — drives the reminder next
   *  to Close, since reset() leaves the file as the only artifact. */
  protected readonly protocolSaved = signal(false);

  /** Live slot view for the restore-confirm dialog, loaded when that dialog opens. */
  private readonly restoreSlots = signal<RestoreSlotPreview>(null);

  /** The restore entry's own pre-check chain is out (#255 P2a) — from `openRestoreConfirm`'s own
   *  `resolveEditableSet` call through `openRestoreConfirmDialog`'s open-time duplicate check,
   *  right up until the confirmation opens (or one of the two shortcuts fires instead: the abort
   *  notice, or the "everything already there" restore). `false` again while a token prompt is
   *  open in between the two reads — that dialog already blocks the background on its own, same
   *  reasoning as every other CDK-modal gap in this file.
   *
   *  Aliases `SevenTvRestoreService.restorePreCheckPending` rather than holding a signal of its
   *  own (#255 P2, Codex review): this section's restore button and `ImportTrigger`'s restore-file
   *  door mount together on the usage-stats page, and a component-local flag here only ever
   *  guarded *this* button against itself — a click on `ImportTrigger` while this section's own
   *  read was out (or the reverse) could open a second confirmation stacked on the first, with a
   *  duplicate `app-dialog-title` id. Reading the shared signal here closes that window: the
   *  button's own `[disabled]` binding now also reflects a pre-check the *other* entry started.
   *
   *  Because it is shared and root-level, `openRestoreConfirm`/`openRestoreConfirmDialog` release
   *  it via `finalize` on every read's own pipe rather than a manual `.set(false)` in each outcome
   *  branch (#255 P2, second Codex finding): `takeUntilDestroyed` tears a pipe down silently on
   *  this component's own destroy, calling neither `next` nor `error`, so a manual reset reachable
   *  only from those never ran — and left both restore entries disabled until a full page reload,
   *  not just this button, since the flag they share outlives the component. */
  protected readonly restoreConfirmPending = this.restoreService.restorePreCheckPending;

  /** Whether the finished run has anything the restore entry could offer — a `done` or, since
   *  #275, an `unknown` row (`restorableItems`). An `unknown`-only run shows the entry too, even
   *  though its `doneKeys` are empty. */
  protected readonly restoreOffered = computed(() => {
    const run = this.deleteService.lastRun();
    return run !== null && restorableItems(run.result.items).length > 0;
  });

  /** How many of the settled run's rows 7TV's answer never clarified (#275) — `0` before the run has
   *  settled (`lastRun()` is `null` while running or settling), same source `restoreOffered` reads. */
  protected readonly unknownRowCount = computed(() => {
    const run = this.deleteService.lastRun();
    return run === null ? 0 : unknownCount(run.result.items);
  });

  protected readonly unknownRowsKey = computed(() =>
    pluralKey(this.unknownRowCount(), 'massDelete.summary.unknownRows'),
  );

  /** Delete-only companion line to `unknownRowsKey` (Plan-275 Festlegung 18): the restore entry
   *  right below already offers these very rows back from the protocol, in case the resync the
   *  settle triggered still finds them gone. Restore's own summary has no such second line — a
   *  restore's protocol is not itself restorable. */
  protected readonly unknownInProtocolKey = computed(() =>
    pluralKey(this.unknownRowCount(), 'massDelete.summary.unknownInProtocol'),
  );

  constructor() {
    // A new run starts with its protocol not yet saved — the reminder must not carry over from the
    // previous run's download. Used to sit in `MassDeletePanel`'s delete latch, which reads the
    // same `isRunning()` edge.
    effect(() => {
      if (this.deleteService.isRunning()) {
        this.protocolSaved.set(false);
      }
    });
  }

  /** Offers the finished run's protocol in both formats — the JSON is the restore list. */
  protected openProtocolExport(): void {
    const run = this.deleteService.lastRun();
    if (!run) {
      return;
    }
    // Every row of the run, unfiltered (spec #200, F3): a row without a local emote is written with
    // `emoteId: null`. Filtering it out here — as this panel once did — produced a silently short
    // protocol, i.e. a deletion without a way back that nobody would notice until they needed it.
    const protocol = buildPurgeRunProtocol({
      channelName: run.channelName,
      emoteSetId: run.setId,
      startedAt: run.result.startedAt,
      finishedAt: run.result.finishedAt,
      items: run.result.items,
      targetOwnerTwitchId: run.targetOwnerTwitchId,
    });
    const data: ExportDialogData = {
      rowCount: protocol.rows.length,
      filtered: false,
      // The protocol is always the whole run — a scope choice would make no sense here.
      selectionCount: null,
      noticeKeys: [],
      optionsLegendKey: 'export.formatLabel',
      options: FORMAT_EXPORT_OPTIONS_JSON_FIRST,
    };
    openExportDialog(this.dialog, data).closed.subscribe((choice) => {
      if (choice?.optionId === 'csv') {
        downloadFile(
          purgeRunFilename(run.channelName, protocol.meta.finishedAt, 'csv'),
          purgeRunCsv(protocol),
          CSV_MIME,
        );
      } else if (choice?.optionId === 'json') {
        downloadFile(
          purgeRunFilename(run.channelName, protocol.meta.finishedAt, 'json'),
          purgeRunJson(protocol),
          JSON_MIME,
        );
      }
      if (choice) {
        this.protocolSaved.set(true);
      }
    });
  }

  /** The restore entry at the finished delete run (spec E16, 4.6 point 22): the pre-check runs
   *  first, like every other first mutation (E19) — in the normal case a cache hit, because the
   *  delete's own report just warmed the target list for this very set. A block emits the abort
   *  notice (`notice`) with a restore-specific lead line and the `restore.errors.*` reason family
   *  (Plan-253 §6, Nr. 3); nothing opens, nothing is sent to 7TV.
   *
   *  `timeout`/`error` and `takeUntilDestroyed` mirror the delete's own pre-check exactly
   *  (review round 1, finding 4): before this fix the subscription had no `error` branch at all, so
   *  a failed request (429, 503, no connection — spec F3) surfaced nothing and silently left the
   *  restore entry inert; a hung one left it inert forever; and a late answer after this section
   *  was torn down could still have opened a confirmation nobody could see or answer. */
  protected openRestoreConfirm(): void {
    // #255 P2a: refuses a second click while the pre-check chain below (this method's own
    // `resolveEditableSet`, or `openRestoreConfirmDialog`'s open-time duplicate check) is still
    // out — belt and suspenders next to the button's own `[disabled]="restoreConfirmPending()"`.
    if (this.restoreConfirmPending() || this.arbiter.startPending()) {
      return;
    }
    const run = this.deleteService.lastRun();
    // The button that calls this is already hidden while the arbiter is busy (the template's own
    // `arbiter.activeRun() === null` guard around it) — this only catches a click outracing such a
    // run starting elsewhere on the page, same shape and same reason as
    // `MassDeletePanel.openConfirm`'s own pre-dialog guard: nothing has been confirmed yet
    // (Festlegung Nr. 8, #256 contract P2), so it stays quiet. The re-checks further down, once a
    // restore actually has something to confirm, do show a reason (#256 T4).
    if (!run || this.arbiter.activeRun() !== null) {
      return;
    }
    // Every done and unknown row (#275), with or without a local emote — see `restorableItems`.
    const restoreItems = restorableItems(run.result.items);
    if (restoreItems.length === 0) {
      return;
    }
    // #256 P3-3 (Plan-256 review): the delete service is a root singleton, so its finished run can
    // still be the one shown here after the workspace has moved to a different channel (Plan-256
    // Festlegung 13 lets a reporting run follow the user; the host panel's own `channelName()`
    // input then updates to the new page while `run` keeps pointing at the old one). Attributing the
    // restore to the *live* page in that case — the pre-#256 behaviour, back when a run's `channelName`
    // and the panel's own input could never drift apart — would tag a run whose actual removals
    // happened on `run.channelName` as belonging to wherever the dock was merely still visible.
    // Fail-closed: use the run's own frozen channel, which is exactly the live page's value in the
    // ordinary case (nothing has been "taken along") and only differs in the carried-over one, where
    // it is the correct answer. `channelName` is a required field of `DeleteRunInfo`, never empty in
    // practice — the guard below only exists so a future run shape that cannot supply one locks the
    // button instead of silently mis-attributing it.
    const hostChannelName = run.channelName;
    if (!hostChannelName) {
      this.emitNotice({
        leadKey: 'restore.nothingRestored',
        reasonKey: 'restore.errors.channelUnknown',
      });
      return;
    }
    this.restoreConfirmPending.set(true);

    // #255 P2 (Codex review, second finding): `handedOff` is `true` exactly when this read's own
    // `next` branch goes on to start the *next* stage of the same shared pre-check chain
    // (`openRestoreConfirmDialog`) without releasing the gate first — every other exit (not
    // editable, the request itself failing, or the caller tearing this section down mid-read) must
    // release it right here instead. `finalize` is what makes teardown release it too:
    // `takeUntilDestroyed` unsubscribes silently, calling neither `next` nor `error`, so a reset
    // living only inside those branches never ran for that exit — and since this gate is the
    // shared, root-level `restorePreCheckPending`, leaving it `true` there left both restore
    // entries disabled until a full page reload, not just this section's own button.
    let handedOff = false;
    this.emoteSetService
      // Owner-hint design 3.6, fourth row: the delete run's own owner hint when it has one, else
      // its frozen channel login (a run carried over from an older tab that started before this
      // field existed).
      .resolveEditableSet(run.setId, {
        twitchChannelId: run.targetOwnerTwitchId,
        twitchLogin: run.channelName,
      })
      .pipe(
        timeout(LIVE_ALIAS_READ_TIMEOUT_MS),
        takeUntilDestroyed(this.destroyRef),
        finalize(() => {
          if (!handedOff) {
            this.restoreConfirmPending.set(false);
          }
        }),
      )
      .subscribe({
        next: (resolution) => {
          if (resolution.status !== 'editable') {
            this.emitNotice({
              leadKey: 'restore.nothingRestored',
              reasonKey: restoreTargetCheckReasonKey(resolution.status),
            });
            return;
          }
          // `hostChannelName` is the delete run's own frozen channel, not necessarily the host's
          // live channel (spec 6.3, E13/E21 — revised by #256 P3-3): in the ordinary case, where
          // nothing has moved the dock to another channel since the delete started, the two are
          // the same value, so this still attributes a restore into a non-active or foreign set to
          // whichever page the delete itself ran on. `hostSelectedSetId` stays the host's live
          // selection — it only feeds the confirmation's `foreignToView` and
          // `RestoreProgressSection`'s "this view shows nothing from this run" line, which are
          // about the set currently on screen, not about ownership.
          const target: ResolvedRestoreTarget = {
            ...resolution.target,
            hostChannelName,
            hostSelectedSetId: this.hostSelectedSetId(),
          };
          if (!this.tokenService.hasToken()) {
            // Released while the token prompt is open (via this pipe's own `finalize` above, since
            // `handedOff` stays `false` on this exit) — a CDK modal already blocks the button
            // behind it, same as every other token-prompt gap in this file — and reclaimed right
            // before the next read starts, whichever way the prompt closes.
            openSevenTvTokenPromptDialog(this.dialog).closed.subscribe((saved) => {
              if (saved) {
                this.restoreConfirmPending.set(true);
                this.openRestoreConfirmDialog(target, restoreItems);
              }
            });
            return;
          }
          handedOff = true;
          this.openRestoreConfirmDialog(target, restoreItems);
        },
        // 429, 503, no connection, or a timeout: "cannot be checked right now", never "not
        // allowed" (F3) — the same distinction the delete's own pre-check makes.
        error: () => {
          this.emitNotice({
            leadKey: 'restore.nothingRestored',
            reasonKey: restoreTargetCheckReasonKey('unavailable'),
          });
        },
      });
  }

  /** `target` is the resolved target the pre-check produced (spec 6.2) — the restore puts the
   *  emotes back into `target.emoteSetId`, never into whatever the host shows by now. Named and
   *  slot-previewed against *that* set (spec 8.8), which the dropdown may since have moved past
   *  (it only locks while the run is still writing). */
  private openRestoreConfirmDialog(
    target: ResolvedRestoreTarget,
    restoreItems: readonly RunQueueItem[],
  ): void {
    // #275: an `unknown` row carries the `uncertain` marker into both duplicate checks, which drop
    // it whenever their read cannot vouch for it — the rule lives in the filter, not here.
    const emotes: RestoreCandidate[] = restoreItems.map((item) => ({
      emoteId: item.emoteId,
      sevenTvEmoteId: item.sevenTvEmoteId,
      name: item.name,
      aliases: item.aliases,
      ...(item.status === 'unknown' ? { uncertain: true as const } : {}),
    }));

    // Operator decision 2026-09-25 (#255, "Slot-Zahl nach dem Skip-Filter") — same open-time
    // check as `startRestoreFlow` (`restore-flow.ts`), reused here rather than duplicated: see
    // `loadRestoreConfirmPreview`'s doc for why this is not a second 7TV read next to the slot
    // preview above, and the confirm-time re-check below for why it still runs fresh again.
    //
    // #255 P2a: bounded by the same timeout budget as this chain's other reads
    // (`LIVE_ALIAS_READ_TIMEOUT_MS`, exported as `RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS` for
    // `restore-flow.ts` to share) and dropped on teardown via `takeUntilDestroyed`. A `timeout`
    // error lands outside `loadRestoreConfirmPreview`'s own `catchError`, so it is treated exactly
    // like the fetch failure that filter already fails open on: `restoreConfirmPreviewUnavailable`
    // builds the identical "could not verify" shape by hand.
    //
    // #255 P2 (Codex review, second finding): this is the last stage of the shared pre-check chain
    // — whatever happens next (the "everything already there" shortcut, the confirmation opening,
    // or nothing at all) no longer needs `restoreConfirmPending` held, so `finalize` releases it
    // unconditionally on every exit, teardown included, rather than the single manual reset that
    // used to sit at the top of `handleRestoreConfirmPreview` and could not run when
    // `takeUntilDestroyed` tore this down first.
    loadRestoreConfirmPreview(this.httpClient, target.emoteSetId, emotes)
      .pipe(
        timeout(RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS),
        takeUntilDestroyed(this.destroyRef),
        finalize(() => this.restoreConfirmPending.set(false)),
      )
      .subscribe({
        next: (preview) => this.handleRestoreConfirmPreview(target, emotes, preview),
        error: () =>
          this.handleRestoreConfirmPreview(
            target,
            emotes,
            restoreConfirmPreviewUnavailable(emotes),
          ),
      });
  }

  /** `emotes` is always the full, unfiltered list `openRestoreConfirmDialog` built from
   *  `restoreItems` — never `preview.rows` — because the confirm-time re-check below (`closed`'s
   *  handler) has to run against the *complete* row set again, fresh, not against this open-time
   *  answer's already-filtered subset (see the comment on that re-check). Its *result*, though, is
   *  clipped back down to `preview.rows` before it ever reaches `startRestore` (#255 P1, Codex
   *  review) — see the comment on that clip for why querying full and clipping after, rather than
   *  querying `preview.rows` directly, is the fix. */
  private handleRestoreConfirmPreview(
    target: ResolvedRestoreTarget,
    emotes: readonly RestoreCandidate[],
    preview: RestoreConfirmPreview<RestoreCandidate>,
  ): void {
    // #275: not when unclear rows were left out — "everything already there" would be untrue for
    // them; the confirmation opens instead and says how many were not offered.
    if (preview.available && preview.rows.length === 0 && preview.uncertainDropped === 0) {
      // Nothing survives the filter — same "everything already there" shortcut `startRestoreFlow`
      // takes, reusing the existing notice instead of a dialog that could only ever show zero
      // names.
      //
      // #255 P2b (the #149 P2 fix's own reasoning, applied to this shortcut too): this call starts
      // a run exactly as much as the regular path's does, so it needs the same mutual-exclusion
      // check right before it — another 7TV-writing run could have claimed the arbiter while this
      // read was out, a window the regular path already closes just above its own `startRestore`
      // call. #256 contract P2, Festlegung Nr. 8: this shortcut only runs once the open-time check
      // found nothing left to confirm — a confirmed start finding nothing to start, same as the
      // regular path below, so it shows the abort notice with the blocking kind rather than
      // vanishing, as it used to.
      const directStartClaim = this.arbiter.activeClaim();
      if (directStartClaim !== null) {
        this.emitNotice(
          refusedStartNotice(this.translocoService, directStartClaim, 'restore.nothingRestored'),
        );
        return;
      }
      this.restoreService.startRestore(
        restoreStartTarget(target),
        [],
        preview.skipped,
        true,
        preview.skippedNameTaken,
      );
      return;
    }

    // Live slot view, so the projection line pops in once the check answers (the dialog is
    // already open by then) — same pattern as the delete confirm's shared-set warning. Started
    // only now rather than up front (#255 P3(10)): the shortcut above already covers the "nothing
    // left to confirm" case, so starting this read before knowing whether a dialog will even open
    // would spend a 7TV request the "everything already there" outcome above then throws away
    // unread — this way it fires exactly once per call, only when there is a confirmation for it
    // to populate. The read itself is `loadRestoreSlotPreview`, the fork this shares with
    // `restore-flow.ts`'s `startRestoreFlow` (spec 4.3, point 8 / spec 8.3, final fix wave A5).
    // Unrelated to the duplicate check above: this one reads occupied/capacity counts, never
    // entries.
    this.restoreSlots.set(null);
    // #275: nothing left to add projects nothing — no slot read either (same as `restore-flow.ts`).
    if (preview.addCount > 0) {
      loadRestoreSlotPreview(
        { emoteAdminService: this.emoteAdminService, emoteSetService: this.emoteSetService },
        target,
      ).subscribe((slotPreview) => this.restoreSlots.set(slotPreview));
    }

    const data: RestoreConfirmDialogData = {
      names: preview.names,
      addCount: preview.addCount,
      // #255 P2, Codex review: same hedge as `restore-flow.ts`'s `startRestoreFlow` — a read that
      // stopped short of the whole target set (`SevenTvSetEntries.complete: false`) still filters
      // `preview.rows` against whatever it saw, but the title and slot projection would otherwise
      // claim an exact number a partial read never verified.
      countIsUpperBound: !preview.available || !preview.complete,
      slots: this.restoreSlots.asReadonly(),
      setName: target.setName,
      isActiveSet: target.isActiveSet,
      emoteSetId: target.emoteSetId,
      ownerDisplayName: target.ownerDisplayName,
      trackedChannelName: target.trackedChannelName,
      // Spec E21: the run's set against the page's *selected* set — a different set of the same
      // channel, and a page with no selection, both count as foreign.
      foreignToView: target.emoteSetId !== target.hostSelectedSetId,
      uncertainDropped: preview.uncertainDropped,
    };
    openRestoreConfirmDialog(this.dialog, data).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      // #149/T5: a restore never had any duplicate protection at all — filter it fresh, right
      // here, against the target set's current contents, read from 7TV itself rather than our
      // database (see `filterAlreadyPresent`'s doc — asking our own mirror is exactly wrong for
      // restore, which runs *because* something already went wrong and our mirror may still be
      // stale) for why this sits at confirm-time and re-reads rather than reusing the open-time
      // preview above, and for the residual race it does not close. Per alias, not per id
      // (operator decision 2026-09-22): a row whose id is present only under some of its own
      // aliases re-adds just the missing ones, and an alias another emote now holds is left out
      // rather than sent into a certain name conflict — see `filterAlreadyPresentForRestore`.
      //
      // #280: held from here until the check has settled, same as `startRestoreFlow`'s own
      // confirm-time check (see there) — bounded, a timeout reading as a failed check. No
      // `takeUntilDestroyed` on purpose: the restore is confirmed and the root service shows it
      // wherever it runs, so this section's teardown must not silently drop it.
      this.restoreService.startCheckPending.set(true);
      filterAlreadyPresentForRestore(this.httpClient, target.emoteSetId, emotes)
        .pipe(
          timeout(RESTORE_CONFIRM_PREVIEW_TIMEOUT_MS),
          catchError(() => of(restoreConfirmPreviewUnavailable(emotes))),
          finalize(() => this.restoreService.startCheckPending.set(false)),
        )
        .subscribe((confirmCheck) => {
          // #149 P2 review fix: openRestoreConfirm()'s own arbiter check ran before this dialog
          // even opened — well outside the mutual-exclusion contract (design doc §4.3) it exists
          // to enforce, since a delete or import can start while the confirm dialog is open and
          // this fetch is in flight. Re-checked here, right before the only remaining call that
          // actually starts anything — a *confirmed* start finding nothing to start (#256 contract
          // P2, Festlegung Nr. 8), so it shows the abort notice with the blocking kind instead of
          // vanishing, as this used to.
          const confirmTimeClaim = this.arbiter.activeClaim();
          if (confirmTimeClaim !== null) {
            this.emitNotice(
              refusedStartNotice(
                this.translocoService,
                confirmTimeClaim,
                'restore.nothingRestored',
              ),
            );
            return;
          }
          // #255 P3(7): a failed confirm-time check normally means every row goes out unfiltered
          // (`filterAlreadyPresentForRestore`'s own fail-open behaviour) — which would silently
          // throw away the open-time check's own, still-valid answer for any row it had already
          // found already present or name-taken. Falls back to that stale-but-real filter instead
          // of no filter at all, whenever the open-time check succeeded. `available` stays what
          // the confirm-time check itself answered either way: it is the freshest check, and its
          // failure still leaves the narrow window since the open-time read unverified, so
          // `duplicateCheckUnavailable` keeps applying — this only changes *which rows* get sent,
          // not whether the caller is told the check could not confirm them just now.
          const fallOnOpenTime = !confirmCheck.available && preview.available;
          // #255 P1 (Codex review): the confirmation only ever showed `preview.rows` — a row (or
          // one alias of a row) the open-time check above had already found present, and which
          // never appeared in the dialog's names or `addCount`, must not come back just because it
          // went missing again by the time this fresher check ran (the target set changing while
          // the confirmation sat open, or between the two reads). `confirmCheck` itself still has
          // to query with every row's full, original aliases — `clipToShown`'s own doc explains why
          // a narrower input here would break the #74 partial-retry case — so the invariant is
          // enforced afterward instead: the confirm-time answer only ever narrows what was shown,
          // `startRestore` can never see more than that. `fallOnOpenTime` already reuses
          // `preview.rows` unclipped — that IS what was shown, nothing to narrow further.
          // Unaffected: the skip counters below, which still come straight from `confirmCheck`'s
          // own fresh count, exactly as before this fix.
          const rows = fallOnOpenTime ? preview.rows : clipToShown(confirmCheck.rows, preview.rows);
          this.restoreService.startRestore(
            restoreStartTarget(target),
            rows,
            fallOnOpenTime ? preview.skipped : confirmCheck.skipped,
            confirmCheck.available,
            fallOnOpenTime ? preview.skippedNameTaken : confirmCheck.skippedNameTaken,
          );
        });
    });
  }

  /** Hands a notice to the host (`notice`). Dropped once this section is destroyed: the confirmed
   *  restore's last re-check outlives it on purpose (see its own comment), and an output emitted
   *  after teardown only logs Angular's "emit for destroyed OutputRef" warning — when this chain
   *  lived in the panel, the same late notice was a silent write into a signal nobody rendered. */
  private emitNotice(notice: DeleteAbortNotice): void {
    if (!this.destroyRef.destroyed) {
      this.notice.emit(notice);
    }
  }
}
