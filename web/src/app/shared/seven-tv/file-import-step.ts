import { Component, ElementRef, input, output, signal, viewChild } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { ImportSource } from '../../core/seven-tv/import-source';
import { parseImportSource } from '../export/import-source-parser';
import { RestoreRow, parsePurgeRunProtocol } from '../export/purge-run-export';
import { readEnvelope } from '../export/read-envelope';
import { parseTransferRunForRestore } from '../export/transfer-run-export';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';

/**
 * What the file step reports once a file has been read and understood. The discriminant tells the
 * caller which chain to run next — `startRestoreFlow` for `'restore'`, `startImportFlow` for
 * `'import'`; this step starts neither itself and picks no import target.
 */
export type FileImportResult =
  { kind: 'restore'; rows: RestoreRow[] } | { kind: 'import'; source: ImportSource };

/**
 * The read-and-validate step of the file-based restore/import path (#91). Until #147 this was a
 * dialog of its own (`FileImportDialog`); it is now the "Aus einer Datei" branch of the one import
 * dialog (`ImportSourceDialog`, design language §7.3). **Only its housing changed** — the reading,
 * the envelope dispatch, the two-pass purge-run validation and the error handling below are the
 * same code they were, moved. Since then the dispatch has gained its second restore sort, the
 * transfer-run file (both stages), validated the same two-pass way.
 *
 * Body order is a contract (plan §1.1, design language §7.3): the four acceptable file sorts — so
 * the explanation sits *above* the control it explains — then the file control, then the error
 * banner (only on failure). There is no "weiter" step, the file pick itself is the action; the
 * dialog around this step therefore renders a cancel-only action row for it.
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
  imports: [Button, NoticeBanner, TranslocoPipe],
  template: `
    <ul class="list-disc space-y-1 pl-5 text-sm text-fg-secondary">
      <li>{{ 'restore.import.sorts.purgeRun' | transloco }}</li>
      <li>{{ 'restore.import.sorts.transferRun' | transloco }}</li>
      <li>{{ 'restore.import.sorts.emoteList' | transloco }}</li>
      <li>{{ 'restore.import.sorts.usageExport' | transloco }}</li>
    </ul>

    <div>
      <button #pickerButton type="button" appButton="outline" (click)="openFilePicker()">
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
   * Frozen by the caller at the moment of the triggering click (#91) — never a live page signal, so
   * a channel switch or a set change while the dialog is open cannot retarget what a restore file
   * is validated against. Read at file-pick time, never in a constructor (Regel 13).
   */
  readonly channelName = input.required<string>();
  /** The set a restore file is matched against (a purge-run protocol's `meta.emoteSetId`, a
   *  transfer-run file's `meta.targetEmoteSetId`) — the caller's own *selected* set (spec #200,
   *  T4.5), active or not: this check was already generic over whichever set it is handed, so a
   *  protocol naming a non-active set is accepted while that set is shown and rejected while
   *  another is (AK 66). */
  readonly setId = input.required<string>();

  readonly picked = output<FileImportResult>();

  // Named apart from the #fileInput template reference, same reasoning as the panel this was split
  // out of: inside the template the bare name resolves to the reference (the raw element), which is
  // not callable — AOT rejects it.
  private readonly fileInputRef = viewChild.required<ElementRef<HTMLInputElement>>('fileInput');
  private readonly pickerButtonRef = viewChild<ElementRef<HTMLButtonElement>>('pickerButton');
  protected readonly errorKey = signal<string | null>(null);

  /** Where the caret goes when this step is entered — see the class doc. Called by the dialog after
   *  the step has rendered, never from a constructor. */
  focusFirstControl(): void {
    this.pickerButtonRef()?.nativeElement.focus();
  }

  protected openFilePicker(): void {
    this.fileInputRef().nativeElement.click();
  }

  protected async onFileSelected(event: Event): Promise<void> {
    const inputElement = event.target as HTMLInputElement;
    const file = inputElement.files?.[0];
    // Clear the input either way, so re-selecting the same (corrected) file fires change again.
    inputElement.value = '';
    if (!file) {
      return;
    }

    this.errorKey.set(null);
    const text = await file.text();
    const read = readEnvelope(text);
    if (!read.ok) {
      this.errorKey.set(read.errorKey);
      return;
    }

    if (read.envelope.kind === 'purge-run') {
      // parsePurgeRunProtocol deliberately re-reads the very same text: it does its own envelope
      // check and keeps enforcing the channel *and* the set match (`wrongChannel`/`wrongSet`)
      // itself, which readEnvelope knows nothing about. The second pass is the contract, not a slip.
      this.handlePurgeRunProtocol(text);
      return;
    }

    if (read.envelope.kind === 'transfer-run') {
      // Either stage is a restore source, never an import source — the same second pass as the
      // purge-run branch above, against the same frozen channel and set. `parseImportSource` would
      // refuse the kind by name; it is never reached with one from here.
      this.handleTransferRun(text);
      return;
    }

    const parsedSource = parseImportSource(read.envelope, file.name);
    if (!parsedSource.ok) {
      this.errorKey.set(parsedSource.errorKey);
      return;
    }
    this.picked.emit({ kind: 'import', source: parsedSource.source });
  }

  private handlePurgeRunProtocol(text: string): void {
    const parsed = parsePurgeRunProtocol(text, {
      channelName: this.channelName(),
      emoteSetId: this.setId(),
    });
    if (!parsed.ok) {
      this.errorKey.set(parsed.errorKey);
      return;
    }
    this.picked.emit({ kind: 'restore', rows: parsed.rows });
  }

  private handleTransferRun(text: string): void {
    const parsed = parseTransferRunForRestore(text, {
      channelName: this.channelName(),
      emoteSetId: this.setId(),
    });
    if (!parsed.ok) {
      this.errorKey.set(parsed.errorKey);
      return;
    }
    this.picked.emit({ kind: 'restore', rows: parsed.rows });
  }
}
