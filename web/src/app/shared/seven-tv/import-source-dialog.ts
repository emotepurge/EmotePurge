import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { LeaderboardImportResult } from '../../core/seven-tv/leaderboard.model';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { FileImportResult, FileImportStep } from './file-import-step';
import { ForeignChannelImportResult, ForeignChannelStep } from './foreign-channel-step';
import { LeaderboardStep } from './leaderboard-step';

/**
 * Frozen at the moment of the triggering click (#91) — never a live signal, so a channel switch or
 * a set change while this dialog is open cannot retarget what a purge-run protocol is validated
 * against, nor where a foreign channel's emotes end up.
 */
export interface ImportSourceDialogData {
  channelName: string;
  setId: string;
  /** Forwarded to the file step's own `restoreEnabled` (spec #200, T4.5) — `undefined`/omitted
   *  reads as `true`, the behaviour every caller before T4.5 already had. `false` while the
   *  channel's usage page shows a non-active set (see `ImportTrigger`'s class doc for why). */
  restoreEnabled?: boolean;
}

/**
 * What the dialog closes with on success. The two file outcomes are unchanged (`FileImportResult`);
 * `'foreign'` is a foreign channel's set and `'leaderboard'` is 7TV's network-wide ranking.
 * `undefined` on cancel, Escape or a backdrop click, same as every other dialog in the app.
 */
export type ImportSourceDialogResult =
  | FileImportResult
  | { kind: 'foreign'; picked: ForeignChannelImportResult }
  | { kind: 'leaderboard'; picked: LeaderboardImportResult };

/** Which sources the first step offers, in the order it offers them. A further source is one more
 *  entry here plus one more `@case` below — deliberately not a greyed-out placeholder in the
 *  meantime. */
type ImportSourceStep = 'file' | 'channel' | 'leaderboard';

interface SourceOption {
  step: ImportSourceStep;
  labelKey: string;
  hintKey: string;
}

/** Added on top of `app-dialog-panel` for as long as the emote grid is on screen, removed again
 *  when it is not — see the rule of the same name in `styles.css`. */
const WIDE_PANEL_CLASS = 'app-dialog-panel-wide';

const SOURCE_OPTIONS: SourceOption[] = [
  {
    step: 'file',
    labelKey: 'import.source.file.label',
    hintKey: 'import.source.file.hint',
  },
  {
    step: 'channel',
    labelKey: 'import.source.channel.label',
    hintKey: 'import.source.channel.hint',
  },
  // Last, and deliberately so: the two above answer "I know where these emotes are", this one
  // answers "show me something good" (spec §1). A reader scanning the list recognizes their own
  // case in the first two or falls through to the open-ended one.
  {
    step: 'leaderboard',
    labelKey: 'import.source.leaderboard.label',
    hintKey: 'import.source.leaderboard.hint',
  },
];

/**
 * The **one** import dialog (#147). Its first step asks where the emotes come from; the branch the
 * user picks then continues inside the same dialog.
 *
 * Before this there were two header buttons — "Importieren" (file) and "Fremder Kanal" — which broke
 * the spec's E1 ("eine Quelle unter anderen im Import-Dialog, keine eigene Seite") by giving one
 * source a front door of its own. The source choice is now the dialog's first step, so every source
 * is reached the same way and a third one is a third row rather than a third button in the header.
 *
 * No branch opens a dialog of its own: the file step reports its parsed result, the channel step
 * and the leaderboard step report their picked rows, and this dialog closes with whichever came
 * back. The chains that
 * follow (`startRestoreFlow`, `startImportFlow`) are started by the trigger *after* this dialog has
 * closed, which is what keeps the app's one-dialog-at-a-time rule intact and lets each chain keep
 * its own ordering.
 *
 * **The pane is wide exactly while the emote grid is showing.** Width belongs to the pane and not to
 * the content (§7), so the switch is an overlay panel class and not a `max-w-*` somewhere in a
 * template — but it is *not* a property of the whole dialog either: all three form states (the source
 * choice, the file branch, the channel branch before a set has loaded) carry a form's worth of content
 * and looked lost in 72rem. They stay at the ordinary 28rem.
 *
 * That leaves exactly one resize, and it coincides with "Set laden" — i.e. with a content change the
 * reader is already watching, which is the one moment a size change reads as consequence rather than
 * caprice. Nothing else moves. The trigger is therefore the grid's visibility, not the step: entering
 * the channel branch changes nothing until the set is there, and entering the leaderboard branch
 * nothing until its list has arrived.
 *
 * **The action row's cast follows the same state**, and only that one: "Weiter" is absent until the
 * grid is there. Without that, the channel step carried two forward actions at once — "Set laden" at
 * the field and a permanently disabled "Weiter" in the row below — which is what made a dialog around
 * one text input look busy.
 *
 * **Focus contract: entering a step puts the caret on that step's first meaningful control.** The
 * CDK autofocuses once, when the overlay opens, and never again for a swap *inside* it — so what was
 * previously true by accident (the file dialog opened straight onto its own file button) has to be
 * said out loud now that the same content is step two. Without it a keyboard user picks a source and
 * lands on nothing, then tabs the whole dialog from the top; a mouse user sees no difference at all,
 * which is exactly why it needs a test rather than a look. Going *back* returns the caret to the
 * source row it came from — the same courtesy a menu button does when its submenu closes, and it
 * means "wrong branch, try the other one" costs no tabbing.
 */
@Component({
  selector: 'app-import-source-dialog',
  imports: [
    Button,
    DialogShell,
    FileImportStep,
    ForeignChannelStep,
    LeaderboardStep,
    TranslocoPipe,
  ],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey() | transloco">
      @switch (step()) {
        @case ('choose') {
          <p id="import-source-legend" class="text-sm text-fg-secondary">
            {{ 'import.source.legend' | transloco }}
          </p>
          <!-- A menu of ruled rows, not a radiogroup: picking a source navigates, it does not
               parametrize a later action, so there is nothing left to confirm afterwards — the same
               reasoning that makes the file step's own "Datei auswählen" the whole action (§7.3). -->
          <div
            role="group"
            aria-labelledby="import-source-legend"
            class="flex flex-col border-t border-border"
          >
            @for (option of sourceOptions; track option.step) {
              <button
                #sourceOption
                type="button"
                class="flex min-h-11 flex-col items-start justify-center gap-0.5 border-b border-border px-2 py-3 text-left transition hover:bg-surface-inset"
                (click)="goTo(option.step)"
              >
                <span class="text-sm font-medium text-fg">{{ option.labelKey | transloco }}</span>
                <span class="text-xs text-fg-secondary">{{ option.hintKey | transloco }}</span>
              </button>
            }
          </div>
        }
        @case ('file') {
          <app-file-import-step
            [channelName]="data.channelName"
            [setId]="data.setId"
            [restoreEnabled]="data.restoreEnabled ?? true"
            (picked)="dialogRef.close($event)"
          />
        }
        @case ('channel') {
          <app-foreign-channel-step />
        }
        @case ('leaderboard') {
          <app-leaderboard-step />
        }
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
      @if (step() !== 'choose') {
        <button dialog-actions type="button" appButton="neutral" buttonSize="lg" (click)="back()">
          {{ 'import.source.back' | transloco }}
        </button>
      }
      <!-- "Weiter" appears with the grid, not with the step. Before the set is loaded the channel
           branch already has a forward action — "Set laden", at the field it acts on — and a second,
           permanently disabled one beside it made a single text input look like it needed four
           buttons. Same condition as the pane width, deliberately: one state, one visible change.
           §7's "Abbrechen zuerst" is untouched by a trailing button that is sometimes absent. -->
      @if (gridVisible()) {
        <button
          dialog-actions
          type="button"
          appButton="primary"
          buttonSize="lg"
          [disabled]="pickedResult() === null"
          (click)="continueWithPicked()"
        >
          {{ 'import.foreignChannel.continue' | transloco }}
        </button>
      }
    </app-dialog-shell>
  `,
})
export class ImportSourceDialog {
  protected readonly data = inject<ImportSourceDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ImportSourceDialogResult | undefined>>(DialogRef);

  private readonly injector = inject(Injector);

  private readonly fileStep = viewChild(FileImportStep);
  private readonly channelStep = viewChild(ForeignChannelStep);
  private readonly leaderboardStep = viewChild(LeaderboardStep);
  private readonly sourceOptionButtons =
    viewChildren<ElementRef<HTMLButtonElement>>('sourceOption');

  protected readonly step = signal<'choose' | ImportSourceStep>('choose');
  /** Which branch "Zurück" came out of, so the caret can land back on the row that opened it. */
  private readonly lastBranch = signal<ImportSourceStep | null>(null);
  protected readonly sourceOptions = SOURCE_OPTIONS;

  /** Each step names itself in the heading, so the dialog always says which branch you are in —
   *  the only orientation cue a one-dialog flow has besides "Zurück". */
  protected readonly titleKey = computed(() => {
    switch (this.step()) {
      case 'file':
        return 'restore.import.title';
      case 'channel':
        return 'import.foreignChannel.title';
      case 'leaderboard':
        return 'import.leaderboard.title';
      default:
        return 'import.source.title';
    }
  });

  protected readonly channelResult = computed(() => this.channelStep()?.result() ?? null);
  protected readonly leaderboardResult = computed(() => this.leaderboardStep()?.result() ?? null);

  /**
   * What the current branch has to carry forward, or `null`. Only one of the two grid-bearing steps
   * is ever mounted — `@switch` destroys the other — so this is a coalesce rather than a decision,
   * and the "Weiter" button stays one button with one condition instead of one per source.
   */
  protected readonly pickedResult = computed<ImportSourceDialogResult | null>(() => {
    const channel = this.channelResult();
    if (channel !== null) {
      return { kind: 'foreign', picked: channel };
    }
    const leaderboard = this.leaderboardResult();
    return leaderboard === null ? null : { kind: 'leaderboard', picked: leaderboard };
  });

  /** The one state this dialog changes shape around — see the class doc. `false` on every step that
   *  has no grid on it, including the channel step before its first successful load and the
   *  leaderboard step while its first list is still loading. It drives both the pane width and
   *  whether the action row carries a "Weiter" at all. */
  protected readonly gridVisible = computed(
    () =>
      (this.channelStep()?.showsGrid() ?? false) || (this.leaderboardStep()?.showsGrid() ?? false),
  );

  constructor() {
    // The overlay ref is CDK's own handle on the pane element, and the pane is where a width has to
    // land (§7). Nothing inside the template can reach it, and `panelClass` at open time cannot
    // either — it is decided before the dialog has any state.
    effect(() => {
      const overlayRef = this.dialogRef.overlayRef;
      if (this.gridVisible()) {
        overlayRef.addPanelClass(WIDE_PANEL_CLASS);
      } else {
        overlayRef.removePanelClass(WIDE_PANEL_CLASS);
      }
    });
  }

  protected goTo(step: ImportSourceStep): void {
    this.lastBranch.set(step);
    this.step.set(step);
    this.focusStepEntryAfterRender();
  }

  protected back(): void {
    this.step.set('choose');
    this.focusStepEntryAfterRender();
  }

  protected continueWithPicked(): void {
    const result = this.pickedResult();
    if (result === null) {
      return;
    }
    this.dialogRef.close(result);
  }

  // Deferred, because the control to focus is only queried into existence by the render that the
  // step change triggers — same reason and same shape as `account-menu.ts`'s panel-level swap.
  private focusStepEntryAfterRender(): void {
    afterNextRender(() => this.focusStepEntry(), { injector: this.injector });
  }

  private focusStepEntry(): void {
    switch (this.step()) {
      case 'file':
        this.fileStep()?.focusFirstControl();
        return;
      case 'channel':
        this.channelStep()?.focusFirstControl();
        return;
      case 'leaderboard':
        this.leaderboardStep()?.focusFirstControl();
        return;
      default: {
        const buttons = this.sourceOptionButtons();
        const cameFrom = this.sourceOptions.findIndex(
          (option) => option.step === this.lastBranch(),
        );
        buttons[cameFrom === -1 ? 0 : cameFrom]?.nativeElement.focus();
      }
    }
  }
}

export function openImportSourceDialog(
  dialog: Dialog,
  data: ImportSourceDialogData,
): DialogRef<ImportSourceDialogResult | undefined> {
  return openAppDialog<ImportSourceDialogResult | undefined, ImportSourceDialogData>(
    dialog,
    ImportSourceDialog,
    { data },
  );
}
