import {
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';

import { ImportSource } from '../../core/seven-tv/import-source';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { TargetCheckBlockReason } from '../../core/seven-tv/sync-report-outcome';
import { UndoCandidate, UndoSourceFileInfo } from '../../core/seven-tv/undo-candidate';
import { ExportKind } from '../export/export-envelope';
import { parseImportSource } from '../export/import-source-parser';
import { RestoreFileTarget, RestoreRow, parsePurgeRunProtocol } from '../export/purge-run-export';
import { readEnvelope } from '../export/read-envelope';
import {
  TransferRunUndoParseResult,
  parseTransferRunForRestore,
  parseTransferRunForUndo,
} from '../export/transfer-run-export';
import { parseTransferUndoForRestore } from '../export/transfer-undo-export';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';
import { StatusBadge } from '../ui/status-badge';
import { ResolvedRestoreTarget } from './restore-flow';

/**
 * What the file step reports once a file has been read and understood. The discriminant tells the
 * caller which chain to run next — `startRestoreFlow` for `'restore'`, `startUndoFlow` for
 * `'transfer-undo'`, `startImportFlow` for `'import'`; this step starts none of them itself and
 * picks no import target. A `'restore'` and a `'transfer-undo'` result always carry a target the
 * shared pre-check has already cleared (spec #253, 6.1): the set the file names, as the target list
 * describes it, plus the two host fields of the page it was read on.
 *
 * `'transfer-undo'` (#254, spec 6.1) is what a transfer-run file becomes when the user picks "undo
 * the replacements" at the switch: its undo candidates (`parseTransferRunForUndo`) and where the
 * file came from, carried into the undo's recovery file and protocol (F6). The name is the undo's,
 * not the file's: a `transfer-undo` *file* is read back as a plain `'restore'` result.
 */
export type FileImportResult =
  | { kind: 'restore'; rows: RestoreRow[]; target: ResolvedRestoreTarget }
  | {
      kind: 'transfer-undo';
      candidates: UndoCandidate[];
      target: ResolvedRestoreTarget;
      sourceFile: UndoSourceFileInfo;
    }
  | { kind: 'import'; source: ImportSource };

/**
 * A transfer-run file that passed the pre-check, waiting for the user's direction (spec 4.1 point
 * 3): the same target and both readings of the file. `undo` holds the undo parser's own answer —
 * it can fail where the restore parser succeeded (a hand-edited file whose replace rows lost their
 * alias), and that failure is shown on the undo option rather than inferred away.
 */
interface TransferRunChoice {
  target: ResolvedRestoreTarget;
  restoreRows: RestoreRow[];
  undo: TransferRunUndoParseResult;
}

/** The copy sorts (`import-source-parser.ts`) — the two a page without a selected set refuses up
 *  front (spec #253, E22), since they have no set to copy into. */
const COPY_ENVELOPE_KINDS: ReadonlySet<ExportKind> = new Set(['emote-list', 'usage']);

/** The banner for each way the target check can block a restore file (spec 4.2, 6.1). */
const TARGET_CHECK_ERROR_KEYS: Record<TargetCheckBlockReason, string> = {
  notEditable: 'restore.import.errors.targetNotEditable',
  notSelectable: 'restore.import.errors.targetNotSelectable',
  unavailable: 'restore.import.errors.targetCheckUnavailable',
};

/**
 * The read-and-validate step of the file-based restore/import path (#91). Until #147 this was a
 * dialog of its own (`FileImportDialog`); it is now the "Aus einer Datei" branch of the one import
 * dialog (`ImportSourceDialog`, design language §7.3). **Only its housing changed** — the reading,
 * the envelope dispatch, the two-pass purge-run validation and the error handling below are the
 * same code they were, moved. Since then the dispatch has gained two more restore sorts, the
 * transfer-run file and the transfer-undo file (both stages each), validated the same two-pass way —
 * and a transfer-run file no longer reports straight away: after the pre-check it ends at a switch,
 * "close gaps" (the restore) or "undo the replacements" (#254, spec 4.1 point 3).
 *
 * **The file names the target of a restore, and this step checks it (spec #253, E1/E2/E10).** A
 * restore file is never held against the page any more — no channel or set comparison; the set
 * its `meta` names is looked up through the shared pre-check (`resolveEditableSet`) as the third
 * step after the envelope and the parser, and `picked` only fires with the target that check
 * resolved. The file is untrusted, and this is the one place where it becomes a target: what goes
 * out is the target list's description of that set (name, owner, tracked channel, whether it is
 * active), never a value copied from the file. A blocked check keeps the dialog open with its own
 * banner. While the check runs the file control refuses a second pick, and an answer arriving after
 * the step is gone (dialog cancelled, "Zurück") is dropped — `picked` closes the dialog, so a late
 * one must not (F6). On a page without a selected set only restore files are read; the two copy
 * sorts are refused before their own parser runs, because there is no set to copy into (E22).
 *
 * Body order is a contract (plan §1.1, design language §7.3): the acceptable file sorts — so the
 * explanation sits *above* the control it explains — then the file control, then the switch (only
 * for a transfer-run file that passed the pre-check), then the error banner (only on failure).
 * There is no "weiter" step: the file pick itself is the action, or for a transfer-run file the
 * pick of a direction at the switch; the dialog around this step therefore renders a cancel-only
 * action row for it.
 *
 * The file control is the step's entry point for the keyboard, and {@link focusFirstControl} is how
 * the dialog puts the caret there on the way in. It used to happen by itself — the file dialog
 * opened straight onto this content, so the CDK's `first-tabbable` default landed on the button —
 * and that stopped being true the moment this became step two of a dialog that opens on the source
 * choice (#147): CDK autofocuses once, when the overlay opens, and never again for a swap *inside*
 * it. A hidden `<input type="file">` cannot take focus itself, so the visible button in front of it
 * is what gets it.
 *
 * The file input has to live inside the open dialog rather than behind it: a programmatic click on
 * an `<input type="file">` *after* a CDK dialog's `closed` runs outside the user gesture and the
 * browser silently declines to open the file window.
 */
@Component({
  selector: 'app-file-import-step',
  imports: [Button, NoticeBanner, StatusBadge, TranslocoPipe],
  template: `
    <ul class="list-disc space-y-1 pl-5 text-sm text-fg-secondary">
      <li>{{ 'restore.import.sorts.purgeRun' | transloco }}</li>
      <li>{{ 'restore.import.sorts.transferRun' | transloco }}</li>
      <li>{{ 'restore.import.sorts.transferUndo' | transloco }}</li>
      <li>{{ 'restore.import.sorts.emoteList' | transloco }}</li>
      <li>{{ 'restore.import.sorts.usageExport' | transloco }}</li>
    </ul>

    <div>
      <!-- aria-disabled, never the disabled attribute: the button is where the caret sits after
           the native file window closes, and a disabled element would drop it to <body> for the
           length of the check. openFilePicker() enforces the lock. -->
      <button
        #pickerButton
        type="button"
        appButton="outline"
        class="aria-disabled:opacity-60"
        [attr.aria-disabled]="checking() ? 'true' : null"
        (click)="openFilePicker()"
      >
        {{ 'restore.import.fileLabel' | transloco }}
      </button>
      <input
        #fileInput
        type="file"
        accept="application/json"
        class="hidden"
        (change)="onFileSelected($event)"
      />
    </div>

    @if (choice()) {
      <!-- The switch (spec 4.1 point 3, #254): where a transfer-run file used to report straight
           away. A menu of ruled rows like the source choice, not a radiogroup — picking a direction
           navigates, the confirmation of what it does comes after (the restore's or the undo's own
           dialog). The undo row names itself destructive; when the file cannot be read as an undo it
           stays visible, disabled with its reason (§10 "Disabled explains itself"). -->
      <p id="file-import-choice-legend" class="text-sm text-fg-secondary">
        {{ 'restore.import.choice.legend' | transloco }}
      </p>
      <div
        role="group"
        aria-labelledby="file-import-choice-legend"
        class="flex flex-col border-t border-border"
      >
        <button #firstChoice type="button" [class]="choiceRowClass" (click)="chooseRestore()">
          <span class="text-sm font-medium text-fg">{{
            'restore.import.choice.restore.label' | transloco
          }}</span>
          <span class="text-xs text-fg-secondary">{{
            'restore.import.choice.restore.hint' | transloco
          }}</span>
        </button>
        <button
          type="button"
          [class]="choiceRowClass"
          [disabled]="undoUnavailable()"
          (click)="chooseUndo()"
        >
          <span class="flex items-center gap-2">
            <span class="text-sm font-medium text-fg">{{
              'restore.import.choice.undo.label' | transloco
            }}</span>
            <app-status-badge tone="danger">{{
              'restore.import.choice.undo.destructive' | transloco
            }}</app-status-badge>
          </span>
          <span class="text-xs text-fg-secondary">{{
            'restore.import.choice.undo.hint' | transloco
          }}</span>
          @if (undoUnavailable()) {
            <span class="text-xs text-fg-muted">{{
              'restore.import.choice.undo.unavailable' | transloco
            }}</span>
          }
        </button>
      </div>
    }

    @if (errorKey(); as error) {
      <app-notice-banner variant="error">{{ error | transloco }}</app-notice-banner>
    }
  `,
  // The step is a plain block in the dialog shell's flex column; without this it would be an inline
  // host and its three children would collapse into one line box.
  host: { class: 'flex flex-col gap-3' },
})
export class FileImportStep {
  /**
   * The channel of the page the dialog was opened on, frozen by the caller at the moment of the
   * triggering click (#91) — the restore target's `hostChannelName` (spec E13), never something a
   * file is compared against. Read at file-pick time, never in a constructor (Regel 13).
   */
  readonly channelName = input.required<string>();
  /** The page's *selected* set, frozen the same way — `null` when the page has none (E22). It is
   *  never a restore target: it only travels on as `hostSelectedSetId` (the confirmation's
   *  "not the set on screen" hint, E21) and, when `null`, refuses the two copy sorts. */
  readonly hostSelectedSetId = input.required<string | null>();

  readonly picked = output<FileImportResult>();

  private readonly emoteSetService = inject(SevenTvEmoteSetService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly injector = inject(Injector);

  // Named apart from the #fileInput template reference, same reasoning as the panel this was split
  // out of: inside the template the bare name resolves to the reference (the raw element), which is
  // not callable — AOT rejects it.
  private readonly fileInputRef = viewChild.required<ElementRef<HTMLInputElement>>('fileInput');
  private readonly pickerButtonRef = viewChild<ElementRef<HTMLButtonElement>>('pickerButton');
  private readonly firstChoiceRef = viewChild<ElementRef<HTMLButtonElement>>('firstChoice');
  protected readonly errorKey = signal<string | null>(null);
  /** True while a restore file's target is being checked — locks the file control (F6). */
  protected readonly checking = signal(false);
  /** The transfer-run file waiting at the switch, `null` whenever there is none. */
  protected readonly choice = signal<TransferRunChoice | null>(null);
  protected readonly undoUnavailable = computed(() => this.choice()?.undo.ok === false);
  /** The source choice's ruled-row look (`import-source-dialog.ts`), so both menus read alike. */
  protected readonly choiceRowClass =
    'flex min-h-11 flex-col items-start justify-center gap-0.5 border-b border-border px-2 py-3 text-left transition enabled:hover:bg-surface-inset disabled:cursor-not-allowed disabled:opacity-60';

  /** Where the caret goes when this step is entered — see the class doc. Called by the dialog after
   *  the step has rendered, never from a constructor. */
  focusFirstControl(): void {
    this.pickerButtonRef()?.nativeElement.focus();
  }

  protected openFilePicker(): void {
    if (this.checking()) {
      return;
    }
    this.fileInputRef().nativeElement.click();
  }

  protected async onFileSelected(event: Event): Promise<void> {
    const inputElement = event.target as HTMLInputElement;
    const file = inputElement.files?.[0];
    // Clear the input either way, so re-selecting the same (corrected) file fires change again.
    inputElement.value = '';
    if (!file || this.checking()) {
      return;
    }

    this.errorKey.set(null);
    this.choice.set(null);
    const text = await file.text();
    const read = readEnvelope(text);
    if (!read.ok) {
      this.errorKey.set(read.errorKey);
      return;
    }

    if (this.readRestoreSort(read.envelope.kind, text)) {
      return;
    }

    if (this.hostSelectedSetId() === null && COPY_ENVELOPE_KINDS.has(read.envelope.kind)) {
      this.errorKey.set('restore.import.errors.noTargetSetForCopy');
      return;
    }

    const parsedSource = parseImportSource(read.envelope, file.name);
    if (!parsedSource.ok) {
      this.errorKey.set(parsedSource.errorKey);
      return;
    }
    this.picked.emit({ kind: 'import', source: parsedSource.source });
  }

  protected chooseRestore(): void {
    const offered = this.choice();
    if (offered === null) {
      return;
    }
    this.picked.emit({ kind: 'restore', rows: offered.restoreRows, target: offered.target });
  }

  protected chooseUndo(): void {
    const offered = this.choice();
    if (offered === null || !offered.undo.ok) {
      return;
    }
    this.picked.emit({
      kind: 'transfer-undo',
      candidates: offered.undo.candidates,
      target: offered.target,
      sourceFile: offered.undo.sourceFile,
    });
  }

  /**
   * The three restore sorts, dispatched before the copy sorts ever see the envelope — true when
   * `kind` was one of them (handled, whatever the outcome). Each re-reads the very same text through
   * its own parser: they do their own envelope check and their own `meta`/row validation, which
   * `readEnvelope` knows nothing about. The second pass is the contract, not a slip.
   *
   * - `purge-run` → restore, as before.
   * - `transfer-run` (either stage) → both parsers, the pre-check, then the switch (spec 4.1 point
   *   2/3): the file can be restored *or* undone, and only the user can say which. The restore
   *   parser decides whether the file is readable at all; the undo parser's answer rides along to
   *   the switch, where a failure shows on the undo option.
   * - `transfer-undo` (either stage) → the source emotes it removed, back as a restore, with no
   *   switch (spec 4.1 point 2, E12): there is no undo of an undo. `parseImportSource` would refuse
   *   the kind by name, which is why it is dispatched here, before that parser.
   */
  private readRestoreSort(kind: ExportKind, text: string): boolean {
    switch (kind) {
      case 'purge-run': {
        const parsed = parsePurgeRunProtocol(text);
        this.pickRestore(parsed);
        return true;
      }
      case 'transfer-undo': {
        const parsed = parseTransferUndoForRestore(text);
        this.pickRestore(parsed);
        return true;
      }
      case 'transfer-run': {
        const parsed = parseTransferRunForRestore(text);
        if (!parsed.ok) {
          this.errorKey.set(parsed.errorKey);
          return true;
        }
        const undo = parseTransferRunForUndo(text);
        this.checkTarget(parsed.target, (target) =>
          this.offerChoice({ target, restoreRows: parsed.rows, undo }),
        );
        return true;
      }
      default:
        return false;
    }
  }

  /** A restore sort without a switch: its parser's error, or the pre-check and then `picked`. */
  private pickRestore(
    parsed:
      { ok: true; rows: RestoreRow[]; target: RestoreFileTarget } | { ok: false; errorKey: string },
  ): void {
    if (!parsed.ok) {
      this.errorKey.set(parsed.errorKey);
      return;
    }
    this.checkTarget(parsed.target, (target) =>
      this.picked.emit({ kind: 'restore', rows: parsed.rows, target }),
    );
  }

  /** Shows the switch and puts the caret on its first option — the file window has just closed
   *  onto the picker button, and the question the step now asks sits below it. */
  private offerChoice(choice: TransferRunChoice): void {
    this.choice.set(choice);
    afterNextRender(() => this.firstChoiceRef()?.nativeElement.focus(), {
      injector: this.injector,
    });
  }

  /**
   * The third step for a restore file (spec 4.2): the set the file names, looked up through the
   * shared pre-check. Only an `'editable'` answer goes on, as the pre-check's own target — its set
   * id included, the one the confirmation shows (AK 35) — plus the two host fields; a blocked check
   * ends in the banner, before any switch (AK 2). The host values are read here, at answer time,
   * from the inputs the caller froze at its click.
   */
  private checkTarget(
    fileTarget: RestoreFileTarget,
    onEditable: (target: ResolvedRestoreTarget) => void,
  ): void {
    this.checking.set(true);
    this.emoteSetService
      .resolveEditableSet(fileTarget.emoteSetId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (resolution) => {
          this.checking.set(false);
          if (resolution.status !== 'editable') {
            this.errorKey.set(TARGET_CHECK_ERROR_KEYS[resolution.status]);
            return;
          }
          onEditable({
            ...resolution.target,
            hostChannelName: this.channelName(),
            hostSelectedSetId: this.hostSelectedSetId(),
          });
        },
        // 429, 503 or no connection: "cannot be checked right now", never "not allowed" (F3).
        error: () => {
          this.checking.set(false);
          this.errorKey.set(TARGET_CHECK_ERROR_KEYS.unavailable);
        },
      });
  }
}
