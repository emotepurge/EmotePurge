import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { Component, DestroyRef, Signal, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { Observable, of, tap, throwError } from 'rxjs';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { ImportOrigin } from '../../core/seven-tv/import-source';
import { LEADERBOARD_SORT_LABEL_KEYS } from '../../core/seven-tv/leaderboard.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvSetEntries, loadSevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import {
  UNDO_SKIP_REASONS,
  UndoLiveCounterpart,
  UndoOmittedEntry,
  UndoPlan,
  UndoPlanRow,
  UndoPlanSummary,
  UndoSkippedRow,
  classifyUndoRows,
  summarizeUndoPlan,
  undoLiveCounterpart,
} from '../../core/seven-tv/undo-plan';
import { EmoteSprite } from '../emotes/emote-sprite';
import { emoteStillUrl } from '../emotes/emote-url';
import { JSON_MIME } from '../export/export-envelope';
import { downloadFile } from '../export/file-download';
import { UndoCandidate, UndoSourceFileInfo } from '../export/transfer-run-export';
import {
  buildTransferUndoPlanRecord,
  transferUndoJson,
  transferUndoPlanFilename,
} from '../export/transfer-undo-export';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NoticeBanner } from '../ui/notice-banner';
import { StatusBadge } from '../ui/status-badge';
import { RecoveryFileGate, RecoveryFileSave } from './recovery-file-gate';
import { ResolvedRestoreTarget } from './restore-flow';
import { loadRestoreSlotPreview } from './restore-slot-preview';
import { projectSlots } from './slot-projection';

export interface UndoConfirmDialogData {
  /** Every candidate the transfer-run file offers (`parseTransferRunForUndo`), in file order. */
  candidates: readonly UndoCandidate[];
  /** The set the undo runs against, as the file step's pre-check resolved it (E13). */
  target: ResolvedRestoreTarget;
  /** Where the transfer-run file came from — shown, and carried into the recovery file (F6). */
  sourceFile: UndoSourceFileInfo;
  /**
   * The flow's own live read of the target (spec 17 K3) — classified at once, without a request of
   * the dialog's own. `null` when that read failed: the dialog then opens in its error state and
   * releases nothing until "Ziel neu laden" brings a complete read (AK 7). A read with
   * `complete: false` is treated the same way.
   */
  initialRead: SevenTvSetEntries | null;
  /** When `initialRead` arrived (epoch ms) — the `verifiedAt` of a recovery file saved from it: the
   *  file vouches for the moment the classified read was taken, not for the click that saved it. */
  initialReadAt: number;
}

/**
 * What the flow starts the undo with (spec 17 K2, seam 2.5 to `startUndo(target, runnable, skipped,
 * acknowledgedUnproven)`): the **effective** plan of one read, explicit. `runnable` are the rows
 * that run — after the origin lock, so without the confirmation no `full` row of an unproven
 * candidate is in it; `skipped` are every other candidate with its reason, `skippedUnproven`
 * included. `acknowledgedUnproven` is `true` only when the confirmation was given *and* it covered
 * at least one unproven `full` row. `read` is the read all of it was classified from — for a plan
 * with a `full` row, the very read the recovery file was built from.
 */
export interface UndoConfirmOutcome {
  runnable: UndoPlanRow[];
  skipped: UndoSkippedRow[];
  acknowledgedUnproven: boolean;
  read: SevenTvSetEntries;
}

/**
 * One classification of one read under one state of the confirmation — the plan the recovery gate
 * holds. Deliberately a fresh object whenever the read or the confirmation changes (no custom
 * equality anywhere on the way): the gate compares plans by identity, so a reload or a toggle after
 * "Rückweg sichern" sends it back to `idle` (AK 6, K2) — the file on disk describes the old plan.
 */
interface EffectiveUndoPlan {
  read: SevenTvSetEntries;
  /** When `read` arrived (epoch ms) — the recovery file's `verifiedAt`. */
  readAt: number;
  runnable: UndoPlanRow[];
  /** In file order, the origin lock's `skippedUnproven` rows included. */
  skipped: UndoSkippedRow[];
  acknowledgedUnproven: boolean;
  /** `full` rows of unproven candidates in the classification, confirmed or not — the rows the
   *  confirmation is about. */
  unprovenFullCount: number;
  /** Target entries already in the set, over **every** candidate that reached that check — skipped
   *  rows included (`UndoPlanCounts.alreadyPresent`). */
  alreadyPresent: number;
  summary: UndoPlanSummary;
}

/** One candidate as the list shows it, in file order. */
interface UndoRowView {
  sourceName: string;
  alias: string;
  sourceImageUrl: string;
  /** The target's default name from the file — `null` when the file has none. */
  targetName: string | null;
  /** The target's entries as the file names them (an aliasless one under the default name), `null`
   *  for an aliasless entry the file cannot name. */
  targetEntries: (string | null)[];
  statusKey: string;
  reasonKey: string | null;
  /** An unproven candidate's `full` row — confirmed and running, or skipped for want of it. */
  unproven: boolean;
  adds: string[];
  omittedEntries: UndoOmittedEntry[];
  foreignNote: boolean;
  /** What the set holds now, for a skipped row and next to a foreign target entry. */
  live: UndoLiveCounterpart | null;
}

/** Why the executor is locked: the element carrying the reason, and the text the action row shows
 *  for it — `null` when the reason already stands in a banner (said once, never twice). */
interface BlockReason {
  elementId: string;
  textKey: string | null;
}

type OriginView =
  | { kind: 'channel'; channel: string }
  | { kind: 'file'; fileName: string }
  | { kind: 'leaderboard'; sortKey: string }
  | { kind: 'unknown' };

/** The recovery gate as this dialog runs it: no drift type — the undo does not verify on save. */
type UndoRecoveryGate = RecoveryFileGate<
  EffectiveUndoPlan,
  never,
  ResolvedRestoreTarget,
  SevenTvSetEntries
>;

const WIDE_PANEL_CLASS = 'app-dialog-panel-wide';
const SPRITE_PX = 40;

/**
 * The confirmation of a replace undo (#254, spec 4.2 Nr. 6–9, 6.3, 17 K1–K3): every candidate of
 * the transfer-run file with its source (image) and target (placeholder, name and aliases) side by
 * side, what the live read makes of it — "Quelle raus, Ziel zurück", "nur Ziel zurück", or skipped
 * with its reason and the live counterpart —, the totals with the number of removals and the slot
 * projection (a warning, never a lock, E17), the target lines and the foreign-to-view hint (#253).
 * Rows keep the file's order; a skipped row stays in place with its reason rather than being hidden
 * (§10 "disabled explains itself"), so toggling the confirmation only changes a row's status line.
 *
 * **No state machine of its own** — the `idle → verifying → saved` gate is `RecoveryFileGate`
 * (#256 point 5, spec 11.3), wired the way its class doc describes for this dialog: the flow's read
 * is `initialRead`, the classification comes from `lastRead()`, "Ziel neu laden" is `reload`, and
 * "Rückweg sichern" saves from the held read (`verifyRead`) without a second request and without a
 * drift check — the undo re-checks at the start and before every REMOVE instead (E14, E19). A plan
 * with a `full` row downloads the `planned` transfer-undo file first and only then offers
 * "Starten"; a plan of `addOnly` rows only removes nothing and starts directly (4.2 Nr. 8).
 *
 * **Origin lock (F17, spec 17 K2; tightened spec §18).** A candidate from a `planned` transfer-run
 * file is unproven: nothing shows its run ever started. A `finished` file's own candidate is
 * unproven too when its row did not settle `done` (`failed`/`unknown`/`cancelled`/a stamped
 * `pending`) — live state proves state, not the file's stage, the same gap K2 closed for `planned`.
 * A candidate's `full` rows are marked either way, and without the file-wide confirmation they are
 * skipped as `skippedUnproven`, the action row following what is left — the service checks the same
 * once more (T4). Only a file whose every candidate settled confirmed shows neither the mark nor
 * the confirmation.
 *
 * Requests: none of its own at open; one live read per "Ziel neu laden"; the slot preview
 * (`loadRestoreSlotPreview`, #253 4.3 Nr. 8).
 */
@Component({
  selector: 'app-undo-confirm-dialog',
  imports: [Button, DialogShell, EmoteSprite, NoticeBanner, StatusBadge, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey | transloco: { count: data.candidates.length }">
      <!-- Order: the file and where its transfer came from, the target lines (#253: set, set id,
           owner, channel and "not active" for a tracked target), the foreign-to-view hint, the
           removal finding, the rows, the totals, and the confirmation right above the action row. -->
      <div class="flex flex-col gap-1">
        <p class="text-sm text-fg-secondary">
          {{ fileLineKey | transloco: { date: fileDate() } }}
        </p>
        <p class="text-xs text-fg-muted">
          @switch (origin().kind) {
            @case ('channel') {
              {{ 'undo.confirm.origin.channel' | transloco: origin() }}
            }
            @case ('file') {
              {{ 'undo.confirm.origin.file' | transloco: origin() }}
            }
            @case ('leaderboard') {
              {{
                'undo.confirm.origin.leaderboard'
                  | transloco: { sort: (leaderboardSortKey() | transloco) }
              }}
            }
            @default {
              {{ 'undo.confirm.origin.unknown' | transloco }}
            }
          }
        </p>
      </div>

      <div class="flex flex-col gap-1">
        <p class="text-sm text-fg-secondary">
          {{ 'undo.confirm.setLine' | transloco: { setName: data.target.setName } }}
        </p>
        <p class="text-sm text-fg-secondary">
          {{ 'restore.confirmSetIdLine' | transloco: { emoteSetId: data.target.emoteSetId } }}
        </p>
        <p class="text-sm text-fg-secondary">
          {{
            'restore.confirmOwnerLine'
              | transloco: { ownerDisplayName: data.target.ownerDisplayName }
          }}
        </p>
        @if (data.target.trackedChannelName !== null) {
          <p class="text-sm text-fg-secondary">
            {{
              'restore.confirmChannelLine'
                | transloco: { channelName: data.target.trackedChannelName }
            }}
          </p>
          @if (!data.target.isActiveSet) {
            <p class="text-sm text-fg-secondary">{{ 'restore.confirmSetNotActive' | transloco }}</p>
          }
        }
      </div>
      @if (foreignToView) {
        <app-notice-banner variant="info">
          {{ 'restore.confirmForeignToView' | transloco }}
        </app-notice-banner>
      }

      @if (plan(); as plan) {
        @if (plan.summary.removeCount > 0) {
          <app-notice-banner id="undo-confirm-removals" variant="warning">
            {{ removalsKey() | transloco: { count: plan.summary.removeCount } }}
          </app-notice-banner>
        }

        <div class="flex items-start justify-between gap-3">
          <p class="text-xs text-fg-muted">{{ 'undo.confirm.readNote' | transloco }}</p>
          <button type="button" appButton="outline" class="shrink-0" (click)="reload()">
            {{ 'undo.confirm.reloadTarget' | transloco }}
          </button>
        </div>

        <ul class="flex flex-col" [attr.aria-label]="'undo.confirm.listLabel' | transloco">
          @for (row of rows(); track $index) {
            <li
              class="flex flex-col gap-2 border-b border-border py-2 sm:grid sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_minmax(0,1.4fr)] sm:items-start sm:gap-3"
            >
              <div class="flex min-w-0 items-center gap-2">
                <span class="app-sprite-cell relative block h-10 w-10 shrink-0">
                  <app-emote-sprite [url]="row.sourceImageUrl" [size]="spritePx" />
                </span>
                <span class="flex min-w-0 flex-col">
                  <span class="truncate text-sm text-fg">{{ row.sourceName }}</span>
                  <span class="truncate text-xs text-fg-muted">
                    {{ 'undo.confirm.sourceAlias' | transloco: { alias: row.alias } }}
                  </span>
                </span>
              </div>

              <!-- The target is in no set after a successful replace, so there is no picture to
                   show: the resolution step's empty plate, with the name and entries beside it. -->
              <div data-target-column class="flex min-w-0 items-center gap-2">
                <span
                  data-target-placeholder
                  aria-hidden="true"
                  class="app-sprite-cell block h-10 w-10 shrink-0"
                ></span>
                <span class="flex min-w-0 flex-col">
                  <span class="truncate text-sm text-fg-secondary">
                    {{ row.targetName ?? ('undo.confirm.targetUnnamed' | transloco) }}
                  </span>
                  <span class="truncate text-xs text-fg-muted">
                    @for (entry of row.targetEntries; track $index) {
                      @if (!$first) {
                        ·
                      }
                      {{ entry ?? ('undo.confirm.unnamedEntry' | transloco) }}
                    }
                  </span>
                </span>
              </div>

              <div class="flex min-w-0 flex-col gap-1 text-xs text-fg-muted">
                <span class="flex flex-wrap items-center gap-2">
                  <span
                    class="text-sm"
                    [class.text-fg]="row.reasonKey === null"
                    [class.text-fg-muted]="row.reasonKey !== null"
                  >
                    @if (row.reasonKey; as reasonKey) {
                      {{ row.statusKey | transloco: { reason: (reasonKey | transloco) } }}
                    } @else {
                      {{ row.statusKey | transloco }}
                    }
                  </span>
                  @if (row.unproven) {
                    <app-status-badge tone="warning">
                      {{ 'undo.confirm.unproven' | transloco }}
                    </app-status-badge>
                  }
                </span>
                @if (row.adds.length > 0) {
                  <span>{{
                    'undo.confirm.adds' | transloco: { names: row.adds.join(' · ') }
                  }}</span>
                }
                @for (entry of row.omittedEntries; track $index) {
                  <span>
                    {{ 'undo.confirm.omitted.' + entry.reason | transloco: { alias: entry.alias } }}
                  </span>
                }
                @if (row.foreignNote) {
                  <span>{{ 'undo.confirm.foreignNote' | transloco }}</span>
                }
                @if (row.live; as live) {
                  <span>
                    {{ 'undo.confirm.liveSource' | transloco }}
                    @for (entry of live.sourceEntries; track $index) {
                      @if (!$first) {
                        ·
                      }
                      {{ entry ?? ('undo.confirm.unnamedEntry' | transloco) }}
                    } @empty {
                      {{ 'undo.confirm.liveAbsent' | transloco }}
                    }
                    — {{ 'undo.confirm.liveTarget' | transloco }}
                    @for (entry of live.targetEntries; track $index) {
                      @if (!$first) {
                        ·
                      }
                      {{ entry ?? ('undo.confirm.unnamedEntry' | transloco) }}
                    } @empty {
                      {{ 'undo.confirm.liveAbsent' | transloco }}
                    }
                  </span>
                }
              </div>
            </li>
          }
        </ul>

        <div class="flex flex-col gap-1">
          <p class="text-sm text-fg-secondary">
            {{
              'undo.confirm.modeCounts'
                | transloco
                  : {
                      full: plan.summary.removeCount,
                      addOnly: plan.runnable.length - plan.summary.removeCount,
                      skipped: plan.skipped.length,
                    }
            }}
          </p>
          <p class="text-sm text-fg-secondary">
            {{ additionsKey() | transloco: { count: plan.summary.addCount } }}
          </p>
          @if (plan.alreadyPresent > 0) {
            <p class="text-sm text-fg-secondary">
              {{ alreadyPresentKey() | transloco: { count: plan.alreadyPresent } }}
            </p>
          }
          @if (plan.summary.omittedEntryCount > 0) {
            <p class="text-sm text-fg-secondary">
              {{ omittedCountKey() | transloco: { count: plan.summary.omittedEntryCount } }}
            </p>
          }
          @for (group of skippedByReason(); track group.reason) {
            <p class="text-xs text-fg-muted">
              {{
                'undo.confirm.skippedCount'
                  | transloco
                    : {
                        reason: ('undo.confirm.reason.' + group.reason | transloco),
                        count: group.count,
                      }
              }}
            </p>
          }
        </div>

        @if (projection(); as slots) {
          @if (slots.overflow) {
            <app-notice-banner variant="warning">
              <span class="flex flex-col gap-1">
                <span>
                  {{
                    'restore.capacityProjection'
                      | transloco: { projected: slots.projected, capacity: slots.capacity }
                  }}
                </span>
                <span>{{ 'restore.capacityWarning' | transloco }}</span>
              </span>
            </app-notice-banner>
          } @else {
            <p class="text-sm text-fg-muted">
              {{
                'restore.capacityProjection'
                  | transloco: { projected: slots.projected, capacity: slots.capacity }
              }}
            </p>
          }
        }
      } @else if (isReloading()) {
        <div
          class="flex flex-col gap-3"
          role="status"
          [attr.aria-label]="'common.loading' | transloco"
        >
          <div aria-hidden="true" class="flex flex-col gap-3">
            <div class="app-skeleton h-4 w-64 max-w-full"></div>
            <div class="app-skeleton h-4 w-48 max-w-full"></div>
            <div class="app-skeleton h-4 w-56 max-w-full"></div>
          </div>
        </div>
      } @else {
        <app-notice-banner id="undo-confirm-read-failed" variant="error">
          {{ 'undo.confirm.readFailed' | transloco }}
          <button notice-action type="button" appButton="outline" (click)="reload()">
            {{ 'undo.confirm.reloadTarget' | transloco }}
          </button>
        </app-notice-banner>
      }

      @if (saveFailed()) {
        <app-notice-banner id="undo-confirm-save-failed" variant="error">
          {{ 'undo.confirm.saveFailed' | transloco }}
        </app-notice-banner>
      }

      @if (plan(); as plan) {
        @if (plan.unprovenFullCount > 0) {
          <div class="flex flex-col gap-1 text-sm text-fg-secondary">
            <label class="flex items-start gap-2 py-1">
              <input
                type="checkbox"
                class="mt-0.5 h-4 w-4 shrink-0 accent-accent-solid"
                aria-describedby="undo-confirm-acknowledge-hint"
                [checked]="acknowledged()"
                (change)="acknowledged.set($any($event.target).checked)"
              />
              {{ 'undo.confirm.acknowledgeUnproven' | transloco }}
            </label>
            <span id="undo-confirm-acknowledge-hint" class="text-xs text-fg-muted">
              {{ acknowledgeHintKey() | transloco: { count: plan.unprovenFullCount } }}
            </span>
          </div>
        }
      }

      <!-- Every action-row element sits in its own single-root block, so the shell's
           [dialog-actions] slot can select it. The reason text stands here only when no banner
           already carries it. -->
      @if (blockReason()?.textKey; as textKey) {
        <p
          dialog-actions
          class="mr-auto min-w-0 text-xs text-fg-muted"
          [id]="blockReason()?.elementId"
        >
          {{ textKey | transloco }}
        </p>
      }
      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close(null)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        class="disabled:cursor-not-allowed"
        [disabled]="blockReason() !== null"
        [attr.aria-describedby]="blockReason()?.elementId ?? null"
        (click)="execute()"
      >
        {{ executeLabelKey() | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class UndoConfirmDialog {
  protected readonly data = inject<UndoConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<UndoConfirmOutcome | null>>(DialogRef);

  private readonly httpClient = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);
  private readonly languageService = inject(LanguageService);
  private readonly emoteAdminService = inject(EmoteAdminService);
  private readonly emoteSetService = inject(SevenTvEmoteSetService);

  protected readonly spritePx = SPRITE_PX;
  protected readonly titleKey = pluralKey(this.data.candidates.length, 'undo.confirm.title');
  protected readonly fileLineKey = `undo.confirm.fileLine.${this.data.sourceFile.stage}`;
  protected readonly foreignToView =
    this.data.target.emoteSetId !== this.data.target.hostSelectedSetId;

  /** The file-wide confirmation for unproven rows (F17). Kept across reloads — it is about the file,
   *  not about one read — but it only counts while the classification has an unproven `full` row. */
  protected readonly acknowledged = signal(false);

  /** Capacity for the projection — read once at open; `null` until it answers or when it fails. */
  private readonly slotPreview = toSignal(
    loadRestoreSlotPreview(
      { emoteAdminService: this.emoteAdminService, emoteSetService: this.emoteSetService },
      this.data.target,
    ),
    { initialValue: null },
  );

  /** When each read a reload delivered arrived — stamped as it comes in, before the gate sees it. */
  private readonly readArrivals = new WeakMap<SevenTvSetEntries, number>();

  private readonly candidateOrder = new Map(
    this.data.candidates.map((candidate, index) => [candidate, index]),
  );

  /** The classification of the gate's current read — `null` while there is no complete one. The
   *  completeness check comes first: `classifyUndoRows` refuses an incomplete read (T2). */
  private readonly classification: Signal<{
    read: SevenTvSetEntries;
    readAt: number;
    plan: UndoPlan;
  } | null> = computed(() => {
    const read = this.gate.lastRead();
    return read === null || !read.complete
      ? null
      : {
          read,
          readAt: this.readArrivals.get(read) ?? this.data.initialReadAt,
          plan: classifyUndoRows(this.data.candidates, read),
        };
  });

  /** The plan the gate holds and "Starten" closes with. No custom `equal`: see
   *  {@link EffectiveUndoPlan}. */
  protected readonly plan: Signal<EffectiveUndoPlan | null> = computed(() => {
    const classification = this.classification();
    return classification === null
      ? null
      : effectiveUndoPlan(classification, this.acknowledged(), this.candidateOrder);
  });

  private readonly gate: UndoRecoveryGate = new RecoveryFileGate({
    plan: this.plan,
    noticeResetSource: this.plan,
    destroyRef: this.destroyRef,
    initialRead: this.data.initialRead?.complete === true ? this.data.initialRead : null,
    read: (target) =>
      loadSevenTvSetEntries(this.httpClient, target.emoteSetId).pipe(
        tap((read) => this.readArrivals.set(read, Date.now())),
      ),
    // The file is built from the read the plan was classified from: no second request, and nothing
    // newer to drift against — the undo re-checks at the start and before every REMOVE (E14, E19).
    verifyRead: () => this.heldRead(),
    isComplete: (read) => read.complete,
    verify: () => ({ available: true, drifted: [] }),
    stamp: (plan) => plan,
    save: (file) => this.saveRecoveryFile(file),
  });

  protected readonly isReloading = this.gate.isReloading;
  protected readonly saveFailed = computed(() => this.gate.notice()?.kind === 'saveFailed');

  protected readonly rows = computed<UndoRowView[]>(() => {
    const plan = this.plan();
    return plan === null ? [] : rowViews(plan, this.candidateOrder);
  });

  protected readonly skippedByReason = computed(() => {
    const skipped = this.plan()?.skipped ?? [];
    return UNDO_SKIP_REASONS.map((reason) => ({
      reason,
      count: skipped.filter((row) => row.reason === reason).length,
    })).filter((group) => group.count > 0);
  });

  protected readonly removalsKey = computed(() =>
    pluralKey(this.plan()?.summary.removeCount ?? 0, 'undo.confirm.removals'),
  );
  protected readonly additionsKey = computed(() =>
    pluralKey(this.plan()?.summary.addCount ?? 0, 'undo.confirm.additions'),
  );
  protected readonly alreadyPresentKey = computed(() =>
    pluralKey(this.plan()?.alreadyPresent ?? 0, 'undo.confirm.alreadyPresent'),
  );
  protected readonly omittedCountKey = computed(() =>
    pluralKey(this.plan()?.summary.omittedEntryCount ?? 0, 'undo.confirm.omittedCount'),
  );
  protected readonly acknowledgeHintKey = computed(() =>
    pluralKey(this.plan()?.unprovenFullCount ?? 0, 'undo.confirm.acknowledgeHint'),
  );

  /** #253 4.3 Nr. 8: capacity from the slot preview; occupancy from the current read once there is
   *  one — fresher than the preview, and current again after every reload (as in the import
   *  dialog). The delta is the effective plan's net change (E17). */
  protected readonly projection = computed(() => {
    const slots = this.slotPreview();
    const plan = this.plan();
    if (slots === null || plan === null) {
      return null;
    }
    return projectSlots(plan.read.occupiedSlots, slots.capacity, plan.summary.slotDelta);
  });

  protected readonly blockReason = computed<BlockReason | null>(() => {
    const plan = this.plan();
    if (plan === null) {
      return this.isReloading()
        ? { elementId: 'undo-confirm-reading', textKey: 'undo.confirm.reading' }
        : { elementId: 'undo-confirm-read-failed', textKey: null };
    }
    if (plan.runnable.length > 0) {
      return null;
    }
    return {
      elementId: 'undo-confirm-blocked',
      textKey: plan.skipped.some((row) => row.reason === 'skippedUnproven')
        ? 'undo.confirm.blockedUnproven'
        : 'undo.confirm.blockedNothing',
    };
  });

  protected readonly executeLabelKey = computed(() => {
    const plan = this.plan();
    if (plan === null || plan.summary.removeCount === 0) {
      return 'undo.confirm.start';
    }
    return this.gate.state().kind === 'saved' ? 'undo.confirm.start' : 'undo.confirm.saveRecovery';
  });

  protected readonly origin = computed<OriginView>(() => originView(this.data.sourceFile.origin));

  protected readonly leaderboardSortKey = computed(() => {
    const origin = this.origin();
    return origin.kind === 'leaderboard' ? origin.sortKey : '';
  });

  /** The date the file vouches for: when its read was verified (`planned`) or its run finished
   *  (`finished`), else when it was exported. Reads `lang()` so a language switch re-formats it. */
  protected readonly fileDate = computed(() => {
    const locale = toLocale(this.languageService.lang());
    const file = this.data.sourceFile;
    const raw = (file.stage === 'planned' ? file.verifiedAt : file.finishedAt) ?? file.exportedAt;
    const parsed = new Date(raw);
    return Number.isNaN(parsed.getTime())
      ? raw
      : parsed.toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' });
  });

  constructor() {
    // The side-by-side rows need the room of the resolution step (spec 4.2 Nr. 6).
    this.dialogRef.overlayRef.addPanelClass(WIDE_PANEL_CLASS);
  }

  protected reload(): void {
    this.gate.reload(this.data.target);
  }

  protected execute(): void {
    const plan = this.plan();
    if (plan === null || this.blockReason() !== null) {
      return;
    }
    if (plan.summary.removeCount === 0) {
      this.close(plan);
      return;
    }
    const state = this.gate.state();
    if (state.kind === 'saved') {
      this.close(state.stampedPlan);
    } else if (state.kind === 'idle') {
      this.gate.verifyAndSave(this.data.target, plan);
    }
  }

  private close(plan: EffectiveUndoPlan): void {
    this.dialogRef.close({
      runnable: plan.runnable,
      skipped: plan.skipped,
      acknowledgedUnproven: plan.acknowledgedUnproven,
      read: plan.read,
    });
  }

  private heldRead(): Observable<SevenTvSetEntries> {
    const plan = this.plan();
    return plan === null ? throwError(() => new Error('no read to save from')) : of(plan.read);
  }

  /** The `planned` transfer-undo file (spec 6.4, AK 6): the running rows only, each `full` row's
   *  source entries from the read, `verifiedAt` the moment that read arrived (not the gate's save
   *  time). Throws when the browser refuses the download — the gate then settles on `saveFailed`
   *  and stays `idle`. */
  private saveRecoveryFile({
    target,
    stampedPlan,
    read,
  }: RecoveryFileSave<ResolvedRestoreTarget, EffectiveUndoPlan, SevenTvSetEntries>): void {
    const verifiedAt = stampedPlan.readAt;
    const record = buildTransferUndoPlanRecord({
      targetEmoteSetId: target.emoteSetId,
      targetChannelName: target.trackedChannelName,
      targetOwnerDisplayName: target.ownerDisplayName,
      sourceFile: this.data.sourceFile,
      verifiedAt,
      acknowledgedUnproven: stampedPlan.acknowledgedUnproven,
      read,
      rows: stampedPlan.runnable,
    });
    downloadFile(
      transferUndoPlanFilename(
        target.trackedChannelName ?? target.emoteSetId,
        new Date(verifiedAt).toISOString(),
      ),
      transferUndoJson(record),
      JSON_MIME,
    );
  }
}

export function openUndoConfirmDialog(
  dialog: Dialog,
  data: UndoConfirmDialogData,
): DialogRef<UndoConfirmOutcome | null> {
  return openAppDialog<UndoConfirmOutcome | null, UndoConfirmDialogData>(
    dialog,
    UndoConfirmDialog,
    { data },
  );
}

/** Whether the origin lock applies to a row: a `full` row of an unproven candidate (F17). */
function isUnprovenFull(row: UndoPlanRow): boolean {
  return row.mode === 'full' && row.provenance === 'unproven';
}

/**
 * Spec 17 K2 over one classification: without the confirmation, every unproven `full` row leaves
 * the runnable rows as `skippedUnproven` (with its live counterpart); the totals are those of what
 * is left. `acknowledgedUnproven` is only `true` when the confirmation covered such a row.
 */
function effectiveUndoPlan(
  { plan, read, readAt }: { plan: UndoPlan; read: SevenTvSetEntries; readAt: number },
  acknowledged: boolean,
  candidateOrder: ReadonlyMap<UndoCandidate, number>,
): EffectiveUndoPlan {
  const unprovenFull = plan.rows.filter(isUnprovenFull);
  const locked = acknowledged ? [] : unprovenFull;
  const runnable = plan.rows.filter((row) => !locked.includes(row));
  const lockedRows: UndoSkippedRow[] = locked.map((row) => ({
    candidate: row.candidate,
    reason: 'skippedUnproven',
    live: undoLiveCounterpart(row.candidate, read),
    omittedEntries: [],
  }));
  const position = (row: UndoSkippedRow) => candidateOrder.get(row.candidate) ?? 0;
  return {
    read,
    readAt,
    runnable,
    skipped: [...plan.skipped, ...lockedRows].sort((a, b) => position(a) - position(b)),
    acknowledgedUnproven: acknowledged && unprovenFull.length > 0,
    unprovenFullCount: unprovenFull.length,
    alreadyPresent: plan.counts.alreadyPresent,
    summary: summarizeUndoPlan({ rows: runnable }),
  };
}

/** Every candidate in file order, with what the effective plan does with it. */
function rowViews(
  plan: EffectiveUndoPlan,
  candidateOrder: ReadonlyMap<UndoCandidate, number>,
): UndoRowView[] {
  const views = [
    ...plan.runnable.map((row) => ({
      candidate: row.candidate,
      view: runnableView(row, plan.read),
    })),
    ...plan.skipped.map((row) => ({ candidate: row.candidate, view: skippedView(row, plan.read) })),
  ];
  const position = (candidate: UndoCandidate) => candidateOrder.get(candidate) ?? 0;
  return views
    .sort((a, b) => position(a.candidate) - position(b.candidate))
    .map((entry) => entry.view);
}

/** What every row shows about its candidate, whatever the plan does with it (K1: the source's
 *  still from the read's `animated` flag — a missing flag is `false` —, the target as named in the
 *  file). */
function candidateView(
  candidate: UndoCandidate,
  read: SevenTvSetEntries,
): Pick<UndoRowView, 'sourceName' | 'alias' | 'sourceImageUrl' | 'targetName' | 'targetEntries'> {
  return {
    sourceName: candidate.sourceName,
    alias: candidate.alias,
    sourceImageUrl: emoteStillUrl(
      candidate.sourceSevenTvEmoteId,
      read.animatedById.get(candidate.sourceSevenTvEmoteId) ?? false,
    ),
    targetName: candidate.target.defaultName,
    targetEntries: candidate.target.entries.map(
      (entry) => entry.alias ?? candidate.target.defaultName,
    ),
  };
}

function runnableView(row: UndoPlanRow, read: SevenTvSetEntries): UndoRowView {
  const foreignNote = row.notes.includes('targetHasForeignEntries');
  return {
    ...candidateView(row.candidate, read),
    statusKey: `undo.confirm.mode.${row.mode}`,
    reasonKey: null,
    unproven: isUnprovenFull(row),
    adds: row.adds.map((add) => add.alias),
    omittedEntries: row.omittedEntries,
    foreignNote,
    live: foreignNote ? undoLiveCounterpart(row.candidate, read) : null,
  };
}

function skippedView(row: UndoSkippedRow, read: SevenTvSetEntries): UndoRowView {
  return {
    ...candidateView(row.candidate, read),
    statusKey: 'undo.confirm.skippedRow',
    reasonKey: `undo.confirm.reason.${row.reason}`,
    unproven: row.reason === 'skippedUnproven',
    adds: [],
    omittedEntries: row.omittedEntries,
    foreignNote: false,
    live: row.live,
  };
}

/** Where the undone transfer's rows came from — `origin` is `null` when the file does not say (or
 *  says something unreadable, T1); every real origin is named, exhaustively. */
function originView(origin: ImportOrigin | null): OriginView {
  if (origin === null) {
    return { kind: 'unknown' };
  }
  switch (origin.kind) {
    case 'channel':
    case 'seventv-channel':
      return { kind: 'channel', channel: origin.channelName };
    case 'file':
      return { kind: 'file', fileName: origin.fileName };
    case 'seventv-leaderboard':
      return { kind: 'leaderboard', sortKey: LEADERBOARD_SORT_LABEL_KEYS[origin.sortBy] };
    default:
      return unreachableOrigin(origin);
  }
}

/** Reached only when a new {@link ImportOrigin} member skipped the `switch` above — the `never`
 *  parameter makes that a build error instead of a silent "unknown". */
function unreachableOrigin(origin: never): never {
  throw new Error(`Unknown import origin: ${JSON.stringify(origin)}`);
}
