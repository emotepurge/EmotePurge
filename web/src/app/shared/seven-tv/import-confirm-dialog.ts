import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  Signal,
  afterNextRender,
  computed,
  effect,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';
import { Subscription, timeout } from 'rxjs';

import { normalizeChannelName } from '../../core/channels/channel-name';
import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { ImportTargetLoadState } from '../../core/emotes/import-target-loader';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import {
  ImportOrigin,
  ImportSource,
  importOriginSourceChannelName,
} from '../../core/seven-tv/import-source';
import { LEADERBOARD_SORT_LABEL_KEYS } from '../../core/seven-tv/leaderboard.model';
import { SevenTvSetEntries, loadSevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { TransferPlan } from '../../core/seven-tv/transfer-plan';
import { JSON_MIME } from '../export/export-envelope';
import { downloadFile } from '../export/file-download';
import {
  buildTransferPlanRecord,
  transferPlanFilename,
  transferRunJson,
} from '../export/transfer-run-export';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NamePreviewList } from '../ui/name-preview-list';
import { NoticeBanner } from '../ui/notice-banner';
import {
  ReplaceTargetDrift,
  stampReplaceTargets,
  verifyReplaceTargets,
} from './already-present-filter';
import {
  ResolutionContext,
  ResolutionDecisions,
  RowDecision,
  buildTransferPlan,
  sameDecisions,
  summarizeTransferPlan,
  validateResolution,
  withoutSkips,
  withoutViolations,
} from './conflict-resolution';
import {
  ConflictGroup,
  ConflictStepRow,
  ImportConflictResolutionStep,
  collisionStepRows,
  mismatchStepRows,
  violationMessages,
} from './import-conflict-resolution-step';
import { ImportPreview, TargetOverlay, buildImportPreview, overlayPreview } from './import-preview';
import { projectSlots } from './slot-projection';

export interface ImportConfirmDialogData {
  /** The rows on offer, already deduplicated — a snapshot, it cannot change while the dialog is up. */
  source: ImportSource;
  /** `null` for an untracked target (spec 8.6) — the header then names the owner instead of a
   *  channel, and `sameChannelFile` never fires (a file can only ever have come from a *tracked*
   *  channel). */
  targetChannelName: string | null;
  /** The 7TV display name of the target set's owner — only meaningful (non-`null`) when
   *  `targetChannelName` is `null`; every tracked caller passes `null` here, since the header then
   *  names the channel instead (spec 8.6, AK 39). Never compared against anything (E7) — display
   *  only. */
  targetOwnerDisplayName: string | null;
  /** Whether this run's target is the tracked channel's currently *active* 7TV set — true for the
   *  "today" path (an `'activeSet'` door, or a `'chosen'` tracked pick whose set equals
   *  `activeEmoteSetId`, spec 8.6 fourth bullet), false for a tracked *non*-active set and for every
   *  untracked one. Drives the title wording (finding 1, Live-Verifikation K2 2026-09-21): an active
   *  target keeps today's "N Emotes nach {channel} kopieren?", unchanged, because that copy still
   *  lands where the channel's active-set resync (and therefore the channel page) actually shows
   *  it. A non-active target's title instead names the set via {@link titleSetName} — "nach
   *  {channel}" would otherwise claim the active-set destination the run does *not* write to
   *  (finding 3 is the same confusion one step further down, in the dock). */
  targetIsActiveSet: boolean;
  /** The chosen set's display name, but only when {@link targetIsActiveSet} is `false` — `null` for
   *  an active target, whose title never names the set at all. Sourced synchronously from the
   *  picker's own choice (`ImportTargetChoice.setName`, already resolved with an id fallback by
   *  `import-target-choices.ts`), not from the live target load: the title renders before that load
   *  ever answers, and the picker already knows this name from the same click that chose the set. */
  titleSetName: string | null;
  /** Live view of the target's data: the dialog opens on `loading` and fills in (R8). */
  target: Signal<ImportTargetLoadState>;
  /** Re-runs the target load; the flow owns the request, the dialog only asks for it. */
  retry: () => void;
  /** True while any 7TV run (delete, restore, import) is active — locks the executor without a
   *  reason text, because the running progress in the same dock already is the reason (§4.2). */
  runBlocked: Signal<boolean>;
  /** For the live read of the target set before a run that removes target entries — the flow's
   *  own client, the same one its last check before the run uses. */
  httpClient: HttpClient;
}

/** What the caller starts a run with — the plan as of the moment the user confirmed. */
export interface ImportConfirmOutcome {
  targetSetId: string;
  /** The resolved display name of {@link targetSetId} (falls back to the id, same as
   *  {@link ImportConfirmDialog.targetSetLabel}) — threaded onto `ImportRunInfo` so the dock's own
   *  "Ziel: …" line (finding 2, Live-Verifikation K2 2026-09-21) can name the set without a second,
   *  independent lookup after this dialog is already gone. */
  targetSetName: string;
  /** The one plan the dialog's summary counted. Untouched conflicts make it the preview's `toAdd`,
   *  one `add` row each; a plan with a `replace` row only leaves after a clean live read, and then
   *  carries that read's aliases and default names on each replace target. */
  plan: TransferPlan;
}

/** Translation key of the reason the executor is locked, or `null` when it is not locked (or when
 *  the reason is deliberately silent — see `runBlocked`). */
type BlockReason = string | null;

type ReadyTarget = Extract<ImportTargetLoadState, { status: 'ready' }>;

/**
 * The executor's state for a plan that removes target entries (`removeCount > 0`) — three states,
 * one button: `idle` offers to save the recovery file, `verifying` reads the target set live, and
 * `saved` means the file was handed to the browser and the button now starts the run. `verifying`
 * and `saved` hold the plan they were entered for; once the current plan is a different one, the
 * state reads as `idle` again (see `actionState`) — the file on disk describes the old plan.
 */
type ActionState =
  | { kind: 'idle' }
  | { kind: 'verifying'; plan: TransferPlan }
  | { kind: 'saved'; plan: TransferPlan; stampedPlan: TransferPlan };

/** Why the last live read did not release the run — rendered as one `error` banner. */
type TargetCheckNotice =
  /** At least one replace target changed; `rows` names the source rows concerned. */
  | { kind: 'drifted'; rows: string }
  /** The read failed or stopped short of the whole set — nothing it says can vouch for a removal. */
  | { kind: 'readFailed' }
  /** The browser refused the download, so no recovery file exists. */
  | { kind: 'saveFailed' };

const IDLE: ActionState = { kind: 'idle' };
const WIDE_PANEL_CLASS = 'app-dialog-panel-wide';
/** Same budget as the other live reads of a set before a destructive step (`mass-delete-panel.ts`,
 *  `seven-tv-import.service.ts`). */
const LIVE_READ_TIMEOUT_MS = 20_000;

/**
 * The screen where the copy is decided: what would be added, into which set, and everything that
 * would make the result differ from the naive reading of "copy N emotes".
 *
 * It opens *before* its target data exists — the picker already answered "where", so making the
 * user watch a spinner-shaped nothing while three requests run would only delay the first thing
 * worth reading (the origin and the count). The target block is a skeleton until
 * `loadImportTarget` answers, which it always does exactly once and never with an error (R8).
 *
 * Three failure states, deliberately not one: a failed `getSetStatus`/`listEmotes` blocks the run
 * (`failed`), a target channel without an active set blocks it for a different reason (`no-set`),
 * and a failed ownership check blocks nothing at all — it only downgrades the foreign-set finding
 * to "could not check", exactly as the delete dialog does.
 *
 * Colour follows §7: only the finding that makes *this* run unusual gets a banner. The row order
 * below is a contract (§7), not a layout preference — in particular `discardedRows` stands *before*
 * `duplicatesCollapsed`, because a discarded row is data actually lost while a collapsed duplicate
 * is only folded, and the heavier finding reads first.
 *
 * **Two steps, one dialog.** Both conflict groups (name collisions, alias mismatches) open a second
 * step in the same pane — `ImportConflictResolutionStep`, one group per opening — where each row
 * gets its own action. The decisions live here until the dialog closes: the step edits a draft,
 * "Übernehmen" commits the draft of its group (blocked while the whole run would violate a rule of
 * `validateResolution`), "Zurück" leaves the committed decisions as they were and keeps the draft
 * for the next opening. The first step then counts the one plan those decisions resolve to — the
 * same plan `execute()` closes with. With no decision taken, that plan is the preview's `toAdd` and
 * the dialog reads and behaves exactly as it did before resolutions existed.
 *
 * **A plan that removes target entries changes the action row** (`removeCount > 0`): the executor
 * first reads the target set live and checks every replace target against what the user confirmed
 * (`verifyReplaceTargets`), then downloads the recovery file built from that read, and only then
 * turns into "Starten". A target that drifted sends the row back to `idle`: its row falls back to
 * skip, and the step shows the live counterpart in place of the stale one, so the user confirms
 * against what is actually there. A read that fails or stops short releases nothing.
 */
@Component({
  selector: 'app-import-confirm-dialog',
  imports: [
    Button,
    DialogShell,
    ImportConflictResolutionStep,
    NamePreviewList,
    NoticeBanner,
    TranslocoPipe,
  ],
  template: `
    <app-dialog-shell
      [dialogTitle]="
        resolveGroup() === null
          ? (titleKey()
            | transloco
              : {
                  count: titleParamCount(),
                  channel: titleTargetLabel(),
                  setName: data.titleSetName ?? '',
                })
          : (resolveTitleKey() | transloco: { count: stepRows().length })
      "
    >
      @if (resolveGroup(); as group) {
        <p class="text-sm text-fg-secondary">
          {{
            (group === 'nameCollision'
              ? 'import.resolve.hintCollisions'
              : 'import.resolve.hintMismatches'
            ) | transloco
          }}
        </p>
        <app-import-conflict-resolution-step
          [group]="group"
          [rows]="stepRows()"
          [decisions]="stepDecisions()"
          [violations]="stepViolations()"
          [reservedRem]="stepReservedRem()"
          (decide)="onDecide($event)"
        />
      } @else {
        <!-- Branches on the three computeds below, never on origin.kind: with a fourth origin
             "not a channel" and "is a file" stopped being the same question, and a template test is
             exactly where that goes unnoticed (spec F6). -->
        @if (originChannelName(); as channel) {
          <p class="text-sm text-fg-secondary">
            {{ 'import.confirm.originChannel' | transloco: { channel } }}
          </p>
        } @else if (fileOrigin(); as file) {
          <div class="flex flex-col gap-1">
            <p class="text-sm text-fg-secondary">
              {{ 'import.confirm.originFile' | transloco: { fileName: file.fileName } }}
            </p>
            <p class="text-xs text-fg-muted">
              {{ 'import.confirm.originFileDetails' | transloco: fileDetails() }}
            </p>
          </div>
        } @else if (leaderboardOrigin(); as origin) {
          <p class="text-sm text-fg-secondary">
            {{ 'import.confirm.originLeaderboard' | transloco: origin }}
          </p>
        }

        @if (ready(); as target) {
          <p class="text-sm text-fg-secondary">
            @if (data.targetChannelName !== null) {
              {{
                'import.confirm.target'
                  | transloco: { channel: data.targetChannelName, setName: targetSetLabel(target) }
              }}
            } @else {
              {{
                'import.confirm.targetUntracked'
                  | transloco
                    : {
                        owner: data.targetOwnerDisplayName ?? '',
                        setName: targetSetLabel(target),
                      }
              }}
            }
          </p>
        }

        @switch (data.target().status) {
          @case ('loading') {
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
          }
          @case ('no-set') {
            <app-notice-banner id="import-confirm-no-target-set" variant="warning">
              {{ 'import.confirm.noTargetSet' | transloco }}
            </app-notice-banner>
          }
          @case ('failed') {
            <app-notice-banner id="import-confirm-load-failed" variant="error">
              {{ 'import.confirm.loadFailed' | transloco }}
              <button notice-action type="button" appButton="outline" (click)="data.retry()">
                {{ 'import.confirm.retry' | transloco }}
              </button>
            </app-notice-banner>
          }
        }

        @if (ready(); as target) {
          @if (sharedSetWarning(); as warning) {
            <app-notice-banner variant="error">
              <span class="flex flex-col gap-1">
                <span class="font-medium">{{
                  'massDelete.sharedSetWarningTitle' | transloco
                }}</span>
                @if (!warning.isOwnSet) {
                  <span>{{ 'massDelete.notOwnSet' | transloco }}</span>
                }
                @if (warning.otherTrackedChannelsSharingSet.length > 0) {
                  <span>
                    {{
                      'massDelete.knownAffected'
                        | transloco: { list: warning.otherTrackedChannelsSharingSet.join(', ') }
                    }}
                  </span>
                }
                @if (warning.otherModeratedChannelsSharingSet.length > 0) {
                  <span>
                    {{
                      'massDelete.moderatedAffected'
                        | transloco: { list: warning.otherModeratedChannelsSharingSet.join(', ') }
                    }}
                  </span>
                }
              </span>
            </app-notice-banner>
          } @else if (ownershipCheckUnavailable()) {
            <!-- Two different reasons share ownershipCheckUnavailable(), and they used to share the
                 delete flow's alarm text too — wrong on both counts for an untracked target:
                 loadImportTarget never even attempts a check there (spec 8.6, there is no channel
                 of ours to run EmoteSetOwnershipService against), so "we couldn't check"
                 misdescribes a check that was never applicable, and the picker's own confirmation
                 step (AK 35) already named this exact owner. A short, neutral hint instead — not a
                 warning banner, and never the delete-flow's wording (massDelete.* stays
                 delete-only, import.confirm.* keeps its own copy). A *tracked* target whose check
                 genuinely failed keeps the amber warning, worded for a copy rather than a
                 deletion. -->
            @if (data.targetChannelName === null) {
              <app-notice-banner variant="info">
                {{ 'import.confirm.untrackedTarget' | transloco }}
              </app-notice-banner>
            } @else {
              <app-notice-banner variant="warning">
                {{ 'import.confirm.ownershipCheckUnavailable' | transloco }}
              </app-notice-banner>
            }
          }

          <!-- The one finding that makes this run unusual in kind, not only in degree: it deletes.
               Counted from the same plan the executor closes with (AK 20). -->
          @if (removeCount() > 0) {
            <app-notice-banner id="import-confirm-removals" variant="warning">
              {{ removalsKey() | transloco: { count: removeCount() } }}
            </app-notice-banner>
          }

          <!-- A neutral hint, not a warning: nothing is lost, an existing entry is only renamed
               (docs/UI-Designsprache.md §7.2). -->
          @if (adoptCount() > 0) {
            <p class="text-sm text-fg-secondary">
              {{ renamesKey() | transloco: { count: adoptCount() } }}
            </p>
          }

          <!-- One banner for everything that took a decision or a release away since the user last
               looked: the last live read, and decisions a reload of the target no longer fits. -->
          @if (targetCheckNotice() !== null || droppedDecisionRows() !== null) {
            <app-notice-banner id="import-confirm-target-check" variant="error">
              <span class="flex flex-col gap-1">
                @if (targetCheckNotice(); as notice) {
                  <span>
                    @switch (notice.kind) {
                      @case ('drifted') {
                        {{ 'import.confirm.targetDrifted' | transloco: { rows: notice.rows } }}
                      }
                      @case ('readFailed') {
                        {{ 'import.confirm.targetReadFailed' | transloco }}
                      }
                      @case ('saveFailed') {
                        {{ 'import.confirm.saveRecoveryFailed' | transloco }}
                      }
                    }
                  </span>
                }
                @if (droppedDecisionRows(); as rows) {
                  <span>{{ 'import.confirm.decisionsDropped' | transloco: { rows } }}</span>
                }
              </span>
              <!-- A reload answers a changed or unreadable target; a refused download it cannot,
                   and after a reload there is nothing left to reload. -->
              @if (
                targetCheckNotice()?.kind === 'drifted' ||
                targetCheckNotice()?.kind === 'readFailed'
              ) {
                <button notice-action type="button" appButton="outline" (click)="data.retry()">
                  {{ 'import.confirm.reloadTarget' | transloco }}
                </button>
              }
            </app-notice-banner>
          }

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

          @if (target.syncFailureReason !== null) {
            <p class="text-sm text-fg-muted">{{ 'import.confirm.staleHint' | transloco }}</p>
          }
        }

        @if (preview(); as preview) {
          @if (preview.alreadyPresent > 0) {
            <p class="text-sm text-fg-secondary">
              {{ alreadyPresentKey() | transloco: { count: preview.alreadyPresent } }}
            </p>
          }
          <!-- Each conflict group carries its own trigger into the resolution step — outline, since it
               only opens a step; the run itself still starts from the action row (§4.2). The
               accessible name says which group, the visible text stays short. Locked while the live
               read runs: a change of plan then would only discard that read. -->
          @if (preview.nameCollisionRowCount > 0) {
            <div class="flex flex-col gap-1">
              <div class="flex items-start justify-between gap-3">
                <p id="import-confirm-name-collisions" class="text-sm text-fg-secondary">
                  {{ nameCollisionsKey() | transloco: { count: preview.nameCollisionRowCount } }}
                </p>
                <button
                  id="import-confirm-resolve-nameCollision"
                  type="button"
                  appButton="outline"
                  class="shrink-0"
                  aria-describedby="import-confirm-name-collisions"
                  [attr.aria-label]="'import.resolve.openCollisions' | transloco"
                  [disabled]="isVerifying()"
                  (click)="openResolve('nameCollision')"
                >
                  {{ 'import.resolve.open' | transloco }}
                </button>
              </div>
              @if (resolvedCount().nameCollision > 0) {
                <p class="text-xs text-fg-muted">
                  {{
                    'import.resolve.resolvedCount'
                      | transloco: { count: resolvedCount().nameCollision }
                  }}
                </p>
              }
              <app-name-preview-list [names]="preview.nameCollisions" />
            </div>
          }
          @if (preview.aliasMismatches.length > 0) {
            <div class="flex flex-col gap-1">
              <div class="flex items-start justify-between gap-3">
                <p id="import-confirm-alias-mismatches" class="text-sm text-fg-secondary">
                  {{ aliasMismatchesKey() | transloco: { count: preview.aliasMismatches.length } }}
                </p>
                <button
                  id="import-confirm-resolve-aliasMismatch"
                  type="button"
                  appButton="outline"
                  class="shrink-0"
                  aria-describedby="import-confirm-alias-mismatches"
                  [attr.aria-label]="'import.resolve.openMismatches' | transloco"
                  [disabled]="isVerifying()"
                  (click)="openResolve('aliasMismatch')"
                >
                  {{ 'import.resolve.open' | transloco }}
                </button>
              </div>
              @if (resolvedCount().aliasMismatch > 0) {
                <p class="text-xs text-fg-muted">
                  {{
                    'import.resolve.resolvedCount'
                      | transloco: { count: resolvedCount().aliasMismatch }
                  }}
                </p>
              }
              <app-name-preview-list [names]="aliasMismatchRows()" />
            </div>
          }
          @if (preview.invalidNames.length > 0) {
            <div class="flex flex-col gap-1">
              <p class="text-sm text-fg-secondary">
                {{ invalidNamesKey() | transloco: { count: preview.invalidNames.length } }}
              </p>
              <app-name-preview-list [names]="preview.invalidNames" />
            </div>
          }
        }

        @if (data.source.discardedRows > 0 || data.source.duplicatesCollapsed > 0) {
          <div class="flex flex-col gap-1">
            @if (data.source.discardedRows > 0) {
              <p class="text-sm text-fg-secondary">
                {{ discardedRowsKey | transloco: { count: data.source.discardedRows } }}
              </p>
            }
            @if (data.source.duplicatesCollapsed > 0) {
              <p class="text-sm text-fg-secondary">
                {{ duplicatesCollapsedKey | transloco: { count: data.source.duplicatesCollapsed } }}
              </p>
            }
          </div>
        }

        @if (nothingToAdd()) {
          <app-notice-banner id="import-confirm-nothing-to-add" variant="info">
            {{ nothingToAddKey() | transloco: { count: data.source.rows.length } }}
          </app-notice-banner>
        }

        @if (sameChannelFile()) {
          <p class="text-sm text-fg-secondary">
            {{ 'import.confirm.sameChannelFile' | transloco }}
          </p>
        }

        <p class="text-xs text-fg-muted">{{ 'import.confirm.runNotice' | transloco }}</p>
      }

      <!-- Only the loading and the verifying state still have their own text here: neither has a
           banner of its own (the target block is a skeleton while it loads, and a running read is
           not a finding), unlike the other lock reasons, which say their piece exactly once, in
           the banner above (R8 kept the banner, not this line too). Every action-row element sits
           in its own single-root block — that is what lets the shell's [dialog-actions] slot
           select it. -->
      @if (resolveGroup() === null && isLoading()) {
        <p dialog-actions id="import-confirm-loading-hint" class="mr-auto text-xs text-fg-muted">
          {{ 'import.confirm.loadingHint' | transloco }}
        </p>
      }
      @if (resolveGroup() === null && isVerifying()) {
        <p dialog-actions id="import-confirm-verifying" class="mr-auto text-xs text-fg-muted">
          {{ 'import.confirm.verifying' | transloco }}
        </p>
      }
      @if (resolveGroup() !== null && applyReasons().length > 0) {
        <p
          dialog-actions
          id="import-resolve-apply-reason"
          class="mr-auto text-xs text-fg-secondary"
        >
          @for (reason of applyReasons(); track reason.key) {
            <span class="block">{{ reason.key | transloco: { rows: reason.rows } }}</span>
          }
        </p>
      }
      @if (resolveGroup() === null) {
        <button
          dialog-actions
          type="button"
          appButton="outline"
          buttonSize="lg"
          (click)="dialogRef.close()"
        >
          {{ 'common.cancel' | transloco }}
        </button>
      }
      @if (resolveGroup() === null) {
        <button
          dialog-actions
          type="button"
          appButton="primary"
          buttonSize="lg"
          class="disabled:cursor-not-allowed"
          [disabled]="executeDisabled()"
          [attr.aria-describedby]="blockReasonElementId()"
          (click)="execute()"
        >
          {{ executeLabelKey() | transloco }}
        </button>
      }
      @if (resolveGroup() !== null) {
        <button
          dialog-actions
          type="button"
          appButton="outline"
          buttonSize="lg"
          (click)="leaveResolve()"
        >
          {{ 'import.resolve.back' | transloco }}
        </button>
      }
      @if (resolveGroup() !== null) {
        <button
          dialog-actions
          type="button"
          appButton="primary"
          buttonSize="lg"
          class="disabled:cursor-not-allowed"
          [disabled]="applyReasons().length > 0"
          [attr.aria-describedby]="applyReasons().length > 0 ? 'import-resolve-apply-reason' : null"
          (click)="applyResolution()"
        >
          {{ 'import.resolve.apply' | transloco }}
        </button>
      }
    </app-dialog-shell>
  `,
})
export class ImportConfirmDialog {
  protected readonly data = inject<ImportConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ImportConfirmOutcome | undefined>>(DialogRef);

  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** Replace is only offered where a deletion has a way back (rule 7 of `validateResolution`). */
  private readonly resolutionContext: ResolutionContext = {
    targetIsTracked: this.data.targetChannelName !== null,
  };

  /** Which conflict group the second step shows, or `null` while the first step is up. */
  protected readonly resolveGroup = signal<ConflictGroup | null>(null);

  /** The decisions the first step counts — committed by "Übernehmen", never by "Zurück". */
  private readonly appliedDecisions = signal<ResolutionDecisions>(new Map());
  /** Edits made in the second step and not yet committed; kept across "Zurück". */
  private readonly draftDecisions = signal<ResolutionDecisions>(new Map());

  private readonly rawActionState = signal<ActionState>(IDLE);
  /** The live read in flight, if any — cancelled when a newer one starts. */
  private targetRead: Subscription | null = null;

  /**
   * The source channel this copy came from, for both channel-shaped origins — a tracked channel and
   * a foreign 7TV one read the same way here on purpose: the sentence "aus Kanal X" is equally true
   * for both, and giving the foreign one its own wording would claim a difference this flow
   * deliberately does not make (spec E1/E2, "nüchtern gerahmt"). `null` for a file.
   */
  protected readonly originChannelName = computed(() =>
    importOriginSourceChannelName(this.data.source.origin),
  );

  /** The file origin itself, or `null` — the one narrowing the file block and `fileDetails` need,
   *  and the reason neither has to ask for a `kind` any more. */
  protected readonly fileOrigin = computed<Extract<ImportOrigin, { kind: 'file' }> | null>(() => {
    const origin = this.data.source.origin;
    return origin.kind === 'file' ? origin : null;
  });

  /**
   * The leaderboard origin's sort, already translated and ready to spread as the transloco params
   * for `import.confirm.originLeaderboard` — or `null` for the other three origins. A leaderboard
   * pick has no source channel at all (E2/E8): the sort *is* the origin, and this reads it through
   * the same `audit.details.leaderboardSort.<code>` table the audit view's `renderDetail` uses, so
   * the dialog and a later audit row for the same import say exactly the same thing (E2: "Der
   * Bestätigungsdialog zeigt dasselbe"). Reads `lang()` first so a language switch while the dialog
   * is open re-translates it — same reasoning as `fileDetails`.
   */
  protected readonly leaderboardOrigin = computed<{ sort: string } | null>(() => {
    this.languageService.lang();
    const origin = this.data.source.origin;
    return origin.kind === 'seventv-leaderboard'
      ? { sort: this.translocoService.translate(LEADERBOARD_SORT_LABEL_KEYS[origin.sortBy]) }
      : null;
  });

  protected readonly ready = computed(() => {
    const state = this.data.target();
    return state.status === 'ready' ? state : null;
  });

  protected readonly preview = computed(() => {
    const target = this.ready();
    return target === null ? null : buildImportPreview(this.data.source, target.emotes);
  });

  /** The live counterpart of every replace target the last live read found drifted, keyed by the
   *  row's source id. Reset whenever the preview is rebuilt — a reload already shows the live set. */
  private readonly targetOverlays = linkedSignal<ImportPreview | null, Map<string, TargetOverlay>>({
    source: this.preview,
    computation: () => new Map(),
  });

  /** Why the last live read did not release the run. Cleared by the next read and by a reload. */
  protected readonly targetCheckNotice = linkedSignal<
    ImportPreview | null,
    TargetCheckNotice | null
  >({ source: this.preview, computation: () => null });

  /** The preview with every drifted replace target replaced by its live counterpart, so the plan's
   *  removal count and its verification both work from the target as it now is. */
  private readonly effectivePreview = computed(() => {
    const preview = this.preview();
    return preview === null ? null : overlayPreview(preview, this.targetOverlays());
  });

  /** The committed decisions minus any the current preview no longer carries out — after a reload,
   *  a decision can sit on a row that is no conflict any more, or refer to a target that changed
   *  underneath it and no longer validate; either falls back to skip. */
  private readonly planDecisions = computed<ResolutionDecisions>(() => {
    const preview = this.effectivePreview();
    if (preview === null) {
      return new Map();
    }
    const conflictKeys = new Set([
      ...preview.nameCollisionRows.map((each) => each.row.sevenTvEmoteId),
      ...preview.aliasMismatchRows.map((each) => each.row.sevenTvEmoteId),
    ]);
    const onConflicts = new Map(
      [...this.appliedDecisions()].filter(([key]) => conflictKeys.has(key)),
    );
    return withoutViolations(preview, onConflicts, this.resolutionContext);
  });

  /** Source names of the committed decisions `planDecisions` drops, or `null` when there are none.
   *  Only a reload of the target can cause this; the next "Übernehmen" clears it. */
  protected readonly droppedDecisionRows = computed<string | null>(() => {
    if (this.effectivePreview() === null) {
      return null;
    }
    const kept = this.planDecisions();
    const dropped = [...withoutSkips(this.appliedDecisions()).keys()].filter(
      (key) => !kept.has(key),
    );
    if (dropped.length === 0) {
      return null;
    }
    const names = new Map(this.data.source.rows.map((row) => [row.sevenTvEmoteId, row.name]));
    return dropped.map((key) => names.get(key) ?? key).join(', ');
  });

  /** The one plan the summary counts and `execute()` closes with. */
  protected readonly plan = computed<TransferPlan | null>(() => {
    const preview = this.effectivePreview();
    return preview === null
      ? null
      : buildTransferPlan(preview, this.planDecisions(), this.resolutionContext);
  });

  private readonly summary = computed(() => {
    const plan = this.plan();
    return plan === null ? null : summarizeTransferPlan(plan);
  });

  protected readonly removeCount = computed(() => this.summary()?.removeCount ?? 0);

  protected readonly removalsKey = computed(() =>
    pluralKey(this.removeCount(), 'import.confirm.removals'),
  );

  /** The count of `adoptSourceName` rows in the one plan the summary counts — same source as
   *  `removeCount` and the title (docs/UI-Designsprache.md §7.2). */
  protected readonly adoptCount = computed(() => this.summary()?.adoptCount ?? 0);

  protected readonly renamesKey = computed(() =>
    pluralKey(this.adoptCount(), 'import.confirm.renames'),
  );

  /** Rows of each group that carry a decision other than skip. */
  protected readonly resolvedCount = computed<Record<ConflictGroup, number>>(() => {
    const preview = this.preview();
    const decisions = this.planDecisions();
    const resolved = (keys: string[]) =>
      keys.filter((key) => (decisions.get(key)?.kind ?? 'skip') !== 'skip').length;
    return {
      nameCollision: resolved(
        preview?.nameCollisionRows.map((each) => each.row.sevenTvEmoteId) ?? [],
      ),
      aliasMismatch: resolved(
        preview?.aliasMismatchRows.map((each) => each.row.sevenTvEmoteId) ?? [],
      ),
    };
  });

  // Until the target answers there is no rest list yet, so the title counts the source rows — the
  // honest upper bound, and the number the user just picked. It settles to the plan's ADD count when
  // the preview arrives; a title that waited for that would leave the dialog headless for a moment.
  protected readonly titleCount = computed(
    () => this.summary()?.addCount ?? this.data.source.rows.length,
  );

  // A plan that only adopts has no ADD at all — a title still counting `addCount` would read "0
  // emotes … copy?" while the run renames entries the chat sees immediately. Reads the summary
  // directly rather than `titleCount()`, which falls back to the source row count while the target is
  // still loading and would otherwise fire before there is a plan to judge.
  protected readonly titleIsRenameOnly = computed(() => {
    const summary = this.summary();
    return summary !== null && summary.addCount === 0 && summary.adoptCount > 0;
  });

  // Two base keys, chosen by `targetIsActiveSet` (finding 1, Live-Verifikation K2 2026-09-21) —
  // "nach {channel}" for the active-set target (today's one-click path, unchanged), "in Set
  // '{setName}'" for a non-active tracked target or any untracked one, both of which write into a
  // set the channel page's active-set resync does not show (finding 3 is the same fact one layer
  // down, in the dock). `titleIsRenameOnly` overrides both with a third key that names no channel or
  // set at all, because a rename-only run has no ADD destination to name.
  protected readonly titleKey = computed(() => {
    if (this.titleIsRenameOnly()) {
      return pluralKey(this.adoptCount(), 'import.confirm.titleAlign');
    }
    return pluralKey(
      this.titleCount(),
      this.data.targetIsActiveSet ? 'import.confirm.title' : 'import.confirm.titleSet',
    );
  });

  // The `count` param the title's own translation reads — `adoptCount` for the rename-only title,
  // `titleCount` (the ADD count) otherwise. A computed of its own rather than folded into
  // `titleCount`, which keeps meaning "the ADD count" for any future reader of that name.
  protected readonly titleParamCount = computed(() =>
    this.titleIsRenameOnly() ? this.adoptCount() : this.titleCount(),
  );

  // Falls back to the owner's display name for an untracked target (spec 8.6, AK 39/35) — the
  // title's `channel` param is really "how the target names itself", and a channel is only one of
  // the two ways this dialog can now have one.
  protected readonly titleTargetLabel = computed(
    () => this.data.targetChannelName ?? this.data.targetOwnerDisplayName ?? '',
  );

  protected readonly alreadyPresentKey = computed(() =>
    pluralKey(this.preview()?.alreadyPresent ?? 0, 'import.confirm.alreadyPresent'),
  );

  protected readonly nameCollisionsKey = computed(() =>
    pluralKey(this.preview()?.nameCollisionRowCount ?? 0, 'import.confirm.nameCollisions'),
  );

  protected readonly aliasMismatchesKey = computed(() =>
    pluralKey(this.preview()?.aliasMismatches.length ?? 0, 'import.confirm.aliasMismatches'),
  );

  // Reads `lang()` first, same reasoning as `fileDetails` — a language switch while the dialog is
  // open re-composes every row through the (then current) translation.
  protected readonly aliasMismatchRows = computed<string[]>(() => {
    this.languageService.lang();
    const mismatches = this.preview()?.aliasMismatches ?? [];
    return mismatches.map((mismatch) =>
      this.translocoService.translate('import.confirm.aliasMismatchRow', {
        source: mismatch.sourceName,
        target: mismatch.targetAlias,
      }),
    );
  });

  protected readonly invalidNamesKey = computed(() =>
    pluralKey(this.preview()?.invalidNames.length ?? 0, 'import.confirm.invalidNames'),
  );

  // Selected on the offered row count, not on `toAdd` — the banner only shows when nothing is
  // left to add, so `toAdd` is always 0 here and would always pick the plural form.
  //
  // Two wordings, not one: `nothingToAdd` claims every offered row is already in the target set,
  // which is only true when `alreadyPresent` alone accounts for all of them. The moment a name
  // collision or an alias mismatch contributes — alone or mixed in with some already-present rows
  // — that claim is false, so this falls back to the honest, reason-agnostic `nothingToAddBlocked`
  // instead of inventing a third and fourth wording for "collision-only" and "mixed" (a signal
  // computed on `preview()`, unlike the sibling `*Key` fields below, because the answer depends on
  // the target load that only arrives after the dialog opens).
  protected readonly nothingToAddKey = computed(() => {
    const total = this.data.source.rows.length;
    const preview = this.preview();
    const allAlreadyPresent = preview !== null && preview.alreadyPresent === total;
    return pluralKey(
      total,
      allAlreadyPresent ? 'import.confirm.nothingToAdd' : 'import.confirm.nothingToAddBlocked',
    );
  });

  protected readonly discardedRowsKey = pluralKey(
    this.data.source.discardedRows,
    'import.confirm.discardedRows',
  );

  protected readonly duplicatesCollapsedKey = pluralKey(
    this.data.source.duplicatesCollapsed,
    'import.confirm.duplicatesCollapsed',
  );

  // Reads `lang()` first so a language switch while the dialog is open re-formats both halves —
  // `translate()` and `toLocaleDateString` are plain calls and would otherwise never be redone.
  protected readonly fileDetails = computed<{ channel: string; date: string }>(() => {
    const locale = toLocale(this.languageService.lang());
    // Off `fileOrigin()` rather than a `!== 'file'` test: that test would have swept the foreign
    // channel origin in here as "not a file" and then read `exportedAt` off something that has no
    // such field (spec F6). The template only renders this next to the file block anyway.
    const origin = this.fileOrigin();
    if (origin === null) {
      return { channel: '', date: '' };
    }
    return {
      channel:
        origin.channelName ?? this.translocoService.translate('import.confirm.channelUnknown'),
      date: this.formatExportDate(origin.exportedAt, locale),
    };
  });

  // Net change, not the ADD count: a replace frees every entry of its target before it adds one
  // back (AK 21).
  protected readonly projection = computed(() => {
    const target = this.ready();
    const summary = this.summary();
    if (target === null || summary === null) {
      return null;
    }
    return projectSlots(
      target.occupiedSlots,
      target.capacity,
      summary.addCount - summary.removedEntryCount,
    );
  });

  // Red only when the check actually ran and found something to flag — `available: false` means the
  // check failed, which is the amber case below, not a confirmed foreign set (delete dialog's rule).
  protected readonly sharedSetWarning = computed<EmoteSetWarning | null>(() => {
    const warning = this.ready()?.warning;
    if (!warning?.available) {
      return null;
    }
    const flagged =
      !warning.isOwnSet ||
      warning.otherTrackedChannelsSharingSet.length > 0 ||
      warning.otherModeratedChannelsSharingSet.length > 0;
    return flagged ? warning : null;
  });

  protected readonly ownershipCheckUnavailable = computed(() => {
    const warning = this.ready()?.warning;
    return warning !== undefined && !warning.available;
  });

  protected readonly nothingToAdd = computed(() => this.plan()?.rows.length === 0);

  // Stays file-only, deliberately: it warns that a *downloaded list* came from the very channel it
  // is about to be copied back into, which a picker cannot produce — the target list excludes the
  // source's own set wherever it appears (`import-target-choices.ts`'s `isSourceSet` disabling), so
  // the same finding is impossible for either channel origin rather than merely unlikely. An
  // untracked target (`targetChannelName === null`, spec 8.6) can never trigger this either: a file
  // only ever names a *tracked* channel as its export source.
  protected readonly sameChannelFile = computed(() => {
    const origin = this.fileOrigin();
    const target = this.data.targetChannelName;
    return (
      origin !== null &&
      origin.channelName !== null &&
      target !== null &&
      normalizeChannelName(origin.channelName) === normalizeChannelName(target)
    );
  });

  protected readonly isLoading = computed(() => this.data.target().status === 'loading');

  /** `verifying`/`saved` only hold while the plan they were entered for is still the current one. */
  protected readonly actionState = computed<ActionState>(() => {
    const state = this.rawActionState();
    return state.kind !== 'idle' && state.plan !== this.plan() ? IDLE : state;
  });

  protected readonly isVerifying = computed(() => this.actionState().kind === 'verifying');

  // Without a removal the label is today's "Kopieren", unchanged (AK 2, 5).
  protected readonly executeLabelKey = computed(() => {
    if (this.removeCount() === 0) {
      return 'import.confirm.execute';
    }
    return this.actionState().kind === 'saved'
      ? 'import.confirm.start'
      : 'import.confirm.saveRecovery';
  });

  protected readonly blockReason = computed<BlockReason>(() => {
    switch (this.data.target().status) {
      case 'loading':
        return 'import.confirm.loadingHint';
      case 'failed':
        return 'import.confirm.loadFailed';
      case 'no-set':
        return 'import.confirm.noTargetSet';
      default:
        if (this.nothingToAdd()) {
          return this.nothingToAddKey();
        }
        return this.isVerifying() ? 'import.confirm.verifying' : null;
    }
  });

  // Id of the one element that visibly carries `blockReason()`'s text, for the execute button's
  // `aria-describedby` (R8+T5: the reason now lives in exactly one place, never duplicated). The
  // loading case points at its own action-row hint rather than a banner — there is no banner while
  // loading, the target block is a skeleton instead. The other three reasons each own exactly one
  // banner, and their underlying statuses (`failed` / `no-set` / ready-with-nothing-to-add) are
  // mutually exclusive, so at most one of these banners is ever visible at once.
  protected readonly blockReasonElementId = computed<string | null>(() => {
    switch (this.data.target().status) {
      case 'loading':
        return 'import-confirm-loading-hint';
      case 'failed':
        return 'import-confirm-load-failed';
      case 'no-set':
        return 'import-confirm-no-target-set';
      default:
        if (this.nothingToAdd()) {
          return 'import-confirm-nothing-to-add';
        }
        return this.isVerifying() ? 'import-confirm-verifying' : null;
    }
  });

  protected readonly executeDisabled = computed(
    () => this.blockReason() !== null || this.data.runBlocked(),
  );

  protected readonly resolveTitleKey = computed(() =>
    pluralKey(
      this.stepRows().length,
      this.resolveGroup() === 'aliasMismatch'
        ? 'import.resolve.titleMismatches'
        : 'import.resolve.titleCollisions',
    ),
  );

  /** The rows of the group the second step shows, with any live counterpart laid over them. */
  protected readonly stepRows = computed<ConflictStepRow[]>(() => {
    const group = this.resolveGroup();
    const preview = this.preview();
    const target = this.ready();
    if (group === null || preview === null || target === null) {
      return [];
    }
    if (group === 'nameCollision') {
      return collisionStepRows(
        preview.nameCollisionRows,
        this.resolutionContext.targetIsTracked,
        this.targetOverlays(),
      );
    }
    const imageUrlById = new Map<string, string>();
    for (const emote of target.emotes) {
      if (!imageUrlById.has(emote.sevenTvEmoteId)) {
        imageUrlById.set(emote.sevenTvEmoteId, emote.imageUrl);
      }
    }
    return mismatchStepRows(preview.aliasMismatchRows, imageUrlById);
  });

  /** The committed decisions with this group's draft laid over them — what the step shows, and what
   *  "Übernehmen" would commit. Validated over the whole run, so a rule that spans both groups (the
   *  same target replaced here and adopted there) is caught in either. */
  protected readonly stepDecisions = computed<ResolutionDecisions>(() => {
    const combined = new Map(this.planDecisions());
    const draft = this.draftDecisions();
    for (const row of this.stepRows()) {
      const decision = draft.get(row.key);
      if (decision !== undefined) {
        combined.set(row.key, decision);
      }
    }
    return combined;
  });

  protected readonly stepViolations = computed(() => {
    const preview = this.effectivePreview();
    if (preview === null || this.resolveGroup() === null) {
      return [];
    }
    const validation = validateResolution(preview, this.stepDecisions(), this.resolutionContext);
    return validation.ok ? [] : validation.violations;
  });

  /** The apply button's lock reason, naming rows by source name (AK 10, 11). Translated in the
   *  template, so a language switch re-translates it. */
  protected readonly applyReasons = computed(() => {
    const names = new Map(this.data.source.rows.map((row) => [row.sevenTvEmoteId, row.name]));
    return violationMessages(this.stepViolations(), (key) => names.get(key) ?? key);
  });

  /** The chrome around the step's viewport that grows with the lock reason in the action row. */
  protected readonly stepReservedRem = computed(() => this.applyReasons().length * 2);

  constructor() {
    // The resolution table needs the room a form does not: the pane widens for the second step and
    // narrows again on the way back — same pane class and mechanism as `ImportSourceDialog`.
    effect(() => {
      const overlayRef = this.dialogRef.overlayRef;
      if (this.resolveGroup() !== null) {
        overlayRef.addPanelClass(WIDE_PANEL_CLASS);
      } else {
        overlayRef.removePanelClass(WIDE_PANEL_CLASS);
      }
    });
  }

  protected execute(): void {
    const target = this.ready();
    const plan = this.plan();
    if (target === null || plan === null || plan.rows.length === 0) {
      return;
    }
    if (this.removeCount() === 0) {
      this.close(target, plan);
      return;
    }
    if (this.executeDisabled()) {
      return;
    }
    const state = this.actionState();
    if (state.kind === 'saved') {
      this.close(target, state.stampedPlan);
    } else if (state.kind === 'idle') {
      this.verifyAndSave(target, plan);
    }
  }

  protected openResolve(group: ConflictGroup): void {
    this.resolveGroup.set(group);
  }

  protected onDecide(change: { key: string; decision: RowDecision }): void {
    this.draftDecisions.update((draft) => new Map(draft).set(change.key, change.decision));
  }

  /** Commits this group's draft. A commit that changes nothing keeps the plan — and with it a
   *  recovery file already saved for it; any real change makes the file stale (see `actionState`). */
  protected applyResolution(): void {
    if (this.applyReasons().length > 0) {
      return;
    }
    const next = withoutSkips(this.stepDecisions());
    if (!sameDecisions(next, withoutSkips(this.appliedDecisions()))) {
      this.appliedDecisions.set(next);
    }
    const groupKeys = new Set(this.stepRows().map((row) => row.key));
    this.draftDecisions.update(
      (draft) => new Map([...draft].filter(([key]) => !groupKeys.has(key))),
    );
    this.leaveResolve();
  }

  /** Back to the first step, focus on the trigger that opened this one. */
  protected leaveResolve(): void {
    const group = this.resolveGroup();
    this.resolveGroup.set(null);
    afterNextRender(
      () => {
        this.host.nativeElement
          .querySelector<HTMLElement>(`#import-confirm-resolve-${group}`)
          ?.focus();
      },
      { injector: this.injector },
    );
  }

  /** The header's `setName` param (spec 8.6 AK 39) — falls back to the raw id when 7TV reported no
   *  name (or, on the "today" path, when none was ever fetched at all — `ImportTargetLoadState`'s
   *  own doc explains why). Never `''`: an id is always a usable, if less friendly, answer. */
  protected targetSetLabel(target: { setId: string; setName: string | null }): string {
    return target.setName ?? target.setId;
  }

  private close(target: ReadyTarget, plan: TransferPlan): void {
    this.dialogRef.close({
      targetSetId: target.setId,
      targetSetName: this.targetSetLabel(target),
      plan,
    });
  }

  /** Reads the target set live, verifies every replace target against it and, if they all still
   *  match, saves the recovery file (docs/plans/Plan-230-Namenskonflikte.md, section 2).
   *
   *  Only the newest read may answer: starting one cancels the one before it (closing the dialog
   *  cancels it too), and each answer first checks that the `verifying` state it set is still the
   *  current one — an answer that lands after anything else moved the state on changes nothing. */
  private verifyAndSave(target: ReadyTarget, plan: TransferPlan): void {
    this.targetRead?.unsubscribe();
    const verifying: ActionState = { kind: 'verifying', plan };
    const isCurrent = () => this.rawActionState() === verifying;
    this.targetCheckNotice.set(null);
    this.rawActionState.set(verifying);
    this.targetRead = loadSevenTvSetEntries(this.data.httpClient, target.setId)
      .pipe(timeout(LIVE_READ_TIMEOUT_MS), takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (entries) => {
          if (isCurrent()) {
            this.onTargetRead(target, plan, entries);
          }
        },
        // A failed read vouches for nothing, so it releases nothing. The decisions stay as they
        // are: which target changed, if any, is exactly what the read could not tell.
        error: () => {
          if (!isCurrent()) {
            return;
          }
          this.rawActionState.set(IDLE);
          // Mirrors onTargetRead's guard: the plan can have moved on (a target reload) while this
          // read was still in flight. An error about a plan that is gone names nothing.
          if (this.plan() === plan) {
            this.targetCheckNotice.set({ kind: 'readFailed' });
          }
        },
      });
  }

  private onTargetRead(target: ReadyTarget, plan: TransferPlan, entries: SevenTvSetEntries): void {
    // The plan changed while the read was running (a reload of the target) — this answer is about
    // a plan that is gone.
    if (this.plan() !== plan) {
      this.rawActionState.set(IDLE);
      return;
    }
    const verification = verifyReplaceTargets(entries, plan);
    if (!verification.available) {
      this.rawActionState.set(IDLE);
      this.targetCheckNotice.set({ kind: 'readFailed' });
      return;
    }
    if (verification.drifted.length > 0) {
      this.applyDrift(verification.drifted);
      return;
    }

    const stampedPlan = stampReplaceTargets(plan, entries);
    const verifiedAt = Date.now();
    const record = buildTransferPlanRecord({
      targetEmoteSetId: target.setId,
      targetChannelName: this.data.targetChannelName,
      targetOwnerDisplayName: this.data.targetOwnerDisplayName,
      origin: this.data.source.origin,
      verifiedAt,
      plan: stampedPlan,
      entries,
      defaultNameById: entries.defaultNameById,
    });
    try {
      downloadFile(
        transferPlanFilename(
          this.data.targetChannelName ?? target.setId,
          new Date(verifiedAt).toISOString(),
        ),
        transferRunJson(record),
        JSON_MIME,
      );
    } catch {
      this.rawActionState.set(IDLE);
      this.targetCheckNotice.set({ kind: 'saveFailed' });
      return;
    }
    this.rawActionState.set({ kind: 'saved', plan, stampedPlan });
  }

  /** Lays each drifted target's live counterpart over its row, sets the row back to skip (committed
   *  and draft alike) and names the rows in the banner. The other decisions stay; `planDecisions`
   *  re-validates them against the new state. */
  private applyDrift(drifted: readonly ReplaceTargetDrift[]): void {
    const keys = new Set(drifted.map((drift) => drift.key));
    this.targetOverlays.update((overlays) => {
      const next = new Map(overlays);
      for (const drift of drifted) {
        next.set(drift.key, drift.live);
      }
      return next;
    });
    const withoutDrifted = (decisions: ResolutionDecisions) =>
      new Map([...decisions].filter(([key]) => !keys.has(key)));
    this.appliedDecisions.update(withoutDrifted);
    this.draftDecisions.update(withoutDrifted);

    const names = new Map(this.data.source.rows.map((row) => [row.sevenTvEmoteId, row.name]));
    this.targetCheckNotice.set({
      kind: 'drifted',
      rows: drifted.map((drift) => names.get(drift.key) ?? drift.key).join(', '),
    });
    this.rawActionState.set(IDLE);
  }

  private formatExportDate(exportedAt: string | null, locale: string): string {
    if (exportedAt === null) {
      return this.translocoService.translate('import.confirm.dateUnknown');
    }
    const parsed = new Date(exportedAt);
    if (Number.isNaN(parsed.getTime())) {
      return this.translocoService.translate('import.confirm.dateUnknown');
    }
    return parsed.toLocaleDateString(locale);
  }
}

export function openImportConfirmDialog(
  dialog: Dialog,
  data: ImportConfirmDialogData,
): DialogRef<ImportConfirmOutcome | undefined> {
  return openAppDialog<ImportConfirmOutcome | undefined, ImportConfirmDialogData>(
    dialog,
    ImportConfirmDialog,
    { data },
  );
}
