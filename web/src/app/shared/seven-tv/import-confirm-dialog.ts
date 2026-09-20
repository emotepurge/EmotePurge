import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, Signal, computed, inject } from '@angular/core';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { normalizeChannelName } from '../../core/channels/channel-name';
import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { ImportTargetLoadState } from '../../core/emotes/import-target-loader';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import {
  ImportOrigin,
  ImportRow,
  ImportSource,
  importOriginSourceChannelName,
} from '../../core/seven-tv/import-source';
import { LEADERBOARD_SORT_LABEL_KEYS } from '../../core/seven-tv/leaderboard.model';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NamePreviewList } from '../ui/name-preview-list';
import { NoticeBanner } from '../ui/notice-banner';
import { buildImportPreview } from './import-preview';
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
  /** Live view of the target's data: the dialog opens on `loading` and fills in (R8). */
  target: Signal<ImportTargetLoadState>;
  /** Re-runs the target load; the flow owns the request, the dialog only asks for it. */
  retry: () => void;
  /** True while any 7TV run (delete, restore, import) is active — locks the executor without a
   *  reason text, because the running progress in the same dock already is the reason (§4.2). */
  runBlocked: Signal<boolean>;
}

/** What the caller starts a run with — the rows as of the moment the user confirmed. */
export interface ImportConfirmOutcome {
  targetSetId: string;
  rows: ImportRow[];
}

/** Translation key of the reason the executor is locked, or `null` when it is not locked (or when
 *  the reason is deliberately silent — see `runBlocked`). */
type BlockReason = string | null;

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
 */
@Component({
  selector: 'app-import-confirm-dialog',
  imports: [Button, DialogShell, NamePreviewList, NoticeBanner, TranslocoPipe],
  template: `
    <app-dialog-shell
      [dialogTitle]="titleKey() | transloco: { count: titleCount(), channel: titleTargetLabel() }"
    >
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
              <span class="font-medium">{{ 'massDelete.sharedSetWarningTitle' | transloco }}</span>
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
          <app-notice-banner variant="warning">
            {{ 'massDelete.ownershipCheckUnavailable' | transloco }}
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
        @if (preview.nameCollisions.length > 0) {
          <div class="flex flex-col gap-1">
            <p class="text-sm text-fg-secondary">
              {{ nameCollisionsKey() | transloco: { count: preview.nameCollisions.length } }}
            </p>
            <app-name-preview-list [names]="preview.nameCollisions" />
          </div>
        }
        @if (preview.aliasMismatches.length > 0) {
          <div class="flex flex-col gap-1">
            <p class="text-sm text-fg-secondary">
              {{ aliasMismatchesKey() | transloco: { count: preview.aliasMismatches.length } }}
            </p>
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
          {{ nothingToAddKey | transloco: { count: data.source.rows.length } }}
        </app-notice-banner>
      }

      @if (sameChannelFile()) {
        <p class="text-sm text-fg-secondary">
          {{ 'import.confirm.sameChannelFile' | transloco }}
        </p>
      }

      <p class="text-xs text-fg-muted">{{ 'import.confirm.runNotice' | transloco }}</p>

      <!-- Only the loading state still has its own text here: it has no banner of its own (the
           target block is a skeleton while it loads), unlike the other lock reasons, which now say
           their piece exactly once, in the banner above (R8 kept the banner, not this line too). -->
      @if (isLoading()) {
        <p dialog-actions id="import-confirm-loading-hint" class="mr-auto text-xs text-fg-muted">
          {{ 'import.confirm.loadingHint' | transloco }}
        </p>
      }
      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        (click)="dialogRef.close()"
      >
        {{ 'common.cancel' | transloco }}
      </button>
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
        {{ 'import.confirm.execute' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class ImportConfirmDialog {
  protected readonly data = inject<ImportConfirmDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ImportConfirmOutcome | undefined>>(DialogRef);

  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);

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

  // Until the target answers there is no rest list yet, so the title counts the source rows — the
  // honest upper bound, and the number the user just picked. It settles to the rest list when the
  // preview arrives; a title that waited for that would leave the dialog headless for a moment.
  protected readonly titleCount = computed(
    () => this.preview()?.toAdd.length ?? this.data.source.rows.length,
  );

  protected readonly titleKey = computed(() =>
    pluralKey(this.titleCount(), 'import.confirm.title'),
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
    pluralKey(this.preview()?.nameCollisions.length ?? 0, 'import.confirm.nameCollisions'),
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
  protected readonly nothingToAddKey = pluralKey(
    this.data.source.rows.length,
    'import.confirm.nothingToAdd',
  );

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

  protected readonly projection = computed(() => {
    const target = this.ready();
    const preview = this.preview();
    if (target === null || preview === null) {
      return null;
    }
    return projectSlots(target.occupiedSlots, target.capacity, preview.toAdd.length);
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

  protected readonly nothingToAdd = computed(() => this.preview()?.toAdd.length === 0);

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

  protected readonly blockReason = computed<BlockReason>(() => {
    switch (this.data.target().status) {
      case 'loading':
        return 'import.confirm.loadingHint';
      case 'failed':
        return 'import.confirm.loadFailed';
      case 'no-set':
        return 'import.confirm.noTargetSet';
      default:
        return this.nothingToAdd() ? this.nothingToAddKey : null;
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
        return this.nothingToAdd() ? 'import-confirm-nothing-to-add' : null;
    }
  });

  protected readonly executeDisabled = computed(
    () => this.blockReason() !== null || this.data.runBlocked(),
  );

  protected execute(): void {
    const target = this.ready();
    const preview = this.preview();
    if (target === null || preview === null || preview.toAdd.length === 0) {
      return;
    }
    this.dialogRef.close({ targetSetId: target.setId, rows: preview.toAdd });
  }

  /** The header's `setName` param (spec 8.6 AK 39) — falls back to the raw id when 7TV reported no
   *  name (or, on the "today" path, when none was ever fetched at all — `ImportTargetLoadState`'s
   *  own doc explains why). Never `''`: an id is always a usable, if less friendly, answer. */
  protected targetSetLabel(target: { setId: string; setName: string | null }): string {
    return target.setName ?? target.setId;
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
