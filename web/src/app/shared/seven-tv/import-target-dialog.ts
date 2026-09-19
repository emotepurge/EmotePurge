import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';

import { ChannelService } from '../../core/channels/channel.service';
import { ExportScope } from '../export/export-dialog';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NoticeBanner } from '../ui/notice-banner';
import { SkeletonRows } from '../ui/skeleton-rows';
import { ImportTargetOption, importTargetOptions } from './import-target-options';

export interface ImportTargetDialogData {
  currentChannelName: string;
  /** Row count of the visible list — the `export.scopeVisible` label needs a number. */
  visibleCount: number;
  /** Size of the page's grid selection; 0 means the scope radiogroup is not offered at all. */
  selectionCount: number;
  /** Set by a caller that has already decided the scope for the user (the dock's copy shortcut,
   *  design doc §8.7, always forces `'selection'`) — no radiogroup at all, and the dialog closes
   *  with exactly this scope regardless of `selectionCount`. `undefined` keeps today's behaviour:
   *  the radiogroup shown/hidden and pre-selected purely from `selectionCount`. */
  forcedScope?: ExportScope;
}

/** What was chosen: a scope over the source rows, plus the channel they should go into. */
export interface ImportTargetChoice {
  scope: ExportScope;
  channelName: string;
}

type TargetSelection = { channelName: string } | null;

/**
 * First step of the copy flow (#72, K3): choose *what* (scope) and *where* (target channel).
 * Loads `listMine()` itself on open — the same call the overview page makes, deliberately
 * uncached (`ChannelService.listMine` docstring) so a role granted a moment ago already shows up.
 *
 * Scope defaults to `selection` when a selection exists (R12) — the opposite of the export dialog,
 * which defaults to `visible`. Export's risk is silently *narrowing* an export; this dialog's risk
 * is silently *widening* a copy into a foreign 7TV set to every visible emote, which can be several
 * hundred. Do not "fix" this to match export — the asymmetry is the decision, not an oversight.
 *
 * Closes with an `ImportTargetChoice`, or `undefined` on cancel/Escape/backdrop.
 */
@Component({
  selector: 'app-import-target-dialog',
  imports: [Button, DialogShell, NoticeBanner, SkeletonRows, TranslocoPipe],
  template: `
    <app-dialog-shell [dialogTitle]="'import.target.title' | transloco">
      @if (data.forcedScope === undefined) {
        @if (data.selectionCount > 0) {
          <div
            class="flex flex-wrap gap-4 text-sm text-fg-secondary"
            role="radiogroup"
            [attr.aria-label]="'export.scopeLabel' | transloco"
          >
            <label class="flex items-center gap-2 py-1">
              <input
                type="radio"
                class="h-4 w-4 accent-accent-solid"
                name="import-scope"
                [disabled]="data.visibleCount === 0"
                [checked]="scope() === 'visible'"
                (change)="scope.set('visible')"
              />
              {{ 'export.scopeVisible' | transloco: { count: data.visibleCount } }}
            </label>
            <label class="flex items-center gap-2 py-1">
              <input
                type="radio"
                class="h-4 w-4 accent-accent-solid"
                name="import-scope"
                [checked]="scope() === 'selection'"
                (change)="scope.set('selection')"
              />
              {{ 'export.scopeSelection' | transloco: { count: data.selectionCount } }}
            </label>
          </div>
        } @else {
          <p class="text-xs text-fg-muted">{{ 'export.scopeNoSelectionHint' | transloco }}</p>
        }
      }

      @if (channelsResource.isLoading()) {
        <app-skeleton-rows [count]="3" />
      } @else {
        @if (reauthRequired()) {
          <app-notice-banner variant="warning">
            {{ 'import.target.reauthRequired' | transloco }}
          </app-notice-banner>
        } @else if (loadFailed()) {
          <app-notice-banner variant="error">
            {{ 'import.target.loadFailed' | transloco }}
            <button
              notice-action
              type="button"
              appButton="outline"
              (click)="channelsResource.reload()"
            >
              {{ 'import.target.retry' | transloco }}
            </button>
          </app-notice-banner>
        } @else if (listIncomplete()) {
          <app-notice-banner variant="info">
            {{ 'import.target.listIncomplete' | transloco }}
          </app-notice-banner>
        }

        <!-- role="radiogroup" only wraps the @for below, and only when it has something to own:
             ARIA requires a radiogroup to contain at least one radio, and with the file radio gone
             this container can legitimately hold zero (E5 — no channel qualifies). The "no channel"
             message therefore renders as a sibling, never inside an otherwise-empty group. -->
        @if (options().length > 0) {
          <div
            class="flex flex-col gap-1"
            role="radiogroup"
            [attr.aria-label]="'import.target.label' | transloco"
          >
            @for (option of options(); track option.channelName) {
              <label class="flex items-center gap-2 py-1" [class.opacity-60]="option.disabled">
                <input
                  type="radio"
                  class="h-4 w-4 accent-accent-solid"
                  name="import-target"
                  [disabled]="option.disabled"
                  [checked]="isChannelChecked(option.channelName)"
                  (change)="selectChannel(option.channelName)"
                />
                #{{ option.channelName }}
                @if (option.disabled) {
                  <span class="text-xs text-fg-muted">
                    ({{ 'import.target.notTracked' | transloco }})
                  </span>
                }
              </label>
            }
          </div>
        } @else if (!reauthRequired() && !loadFailed() && !listIncomplete()) {
          <!-- Only when there genuinely is no channel. After a reauth, a failed load or a
               partial one the empty list says nothing about the account, and claiming otherwise
               invites ignoring the notice above: an unavailable 7TV hides precisely the editor
               targets, so "no channel fits" would be a claim this list cannot support. -->
          <p class="text-sm text-fg-muted">{{ 'import.target.none' | transloco }}</p>
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
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        [disabled]="target() === null || emptyScopeChosen()"
        (click)="submit()"
      >
        {{ 'import.target.submit' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class ImportTargetDialog {
  protected readonly data = inject<ImportTargetDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ImportTargetChoice | undefined>>(DialogRef);

  private readonly channelService = inject(ChannelService);

  // Pre-selected to 'selection' when a selection exists (R12) — see the class doc for why this is
  // the opposite default from the export dialog. Without a selection there is no radiogroup at all
  // and the scope is 'visible' unconditionally (read via the getter below, never surfaced as UI).
  // A caller-forced scope (design doc §8.7) wins over both: there is no radiogroup to change it
  // away from, so the value set here is also the value the dialog closes with.
  protected readonly scope = signal<ExportScope>(
    this.data.forcedScope ?? (this.data.selectionCount > 0 ? 'selection' : 'visible'),
  );

  // Row count of whichever scope is actually in force — Konzept "Auswahl überlebt Suche und
  // Filter" nachtrag (2026-09-19): a filter can leave `visibleCount` at 0 while `selectionCount`
  // survives it, and the default above already prefers 'selection' whenever it is non-empty, so
  // this mostly matters for the disabled 'visible' radio and for emptyScopeChosen below.
  protected readonly scopeRowCount = computed(() =>
    this.scope() === 'selection' ? this.data.selectionCount : this.data.visibleCount,
  );

  // Blocks a submit that would start a copy run over zero rows. Deliberately excludes a
  // caller-forced scope: the dock shortcut that forces 'selection' is already guarded upstream at
  // two layers (its own button locks at selectionCount === 0 — see import-shortcut.ts — and
  // openImportTarget's defensive check mirrors that), and import-target-dialog.spec.ts exercises
  // forcedScope with selectionCount 0 on purpose to prove the scope itself does not silently fall
  // back — this must not turn that state into a disabled submit.
  protected readonly emptyScopeChosen = computed(
    () => this.data.forcedScope === undefined && this.scopeRowCount() === 0,
  );

  // No pre-selected target: "Weiter" stays disabled until the user picks one, and the radio itself
  // is the prompt — no extra hint text is needed for that particular lock (§4.2's "state the
  // reason" rule is about *why*, and here the why is self-evident from an unset radiogroup).
  protected readonly target = signal<TargetSelection>(null);

  protected readonly channelsResource = rxResource({
    stream: () => this.channelService.listMine(),
  });

  protected readonly reauthRequired = computed(
    () => this.channelsResource.hasValue() && this.channelsResource.value().reauthRequired,
  );

  // A genuine transport failure (network, 5xx) — distinct from the two degraded-but-200 flags
  // below, which the loaded result still carries.
  protected readonly loadFailed = computed(() => this.channelsResource.error() !== undefined);

  protected readonly listIncomplete = computed(() => {
    if (!this.channelsResource.hasValue()) {
      return false;
    }
    const result = this.channelsResource.value();
    return !result.reauthRequired && (result.helixUnavailable || result.sevenTvUnavailable);
  });

  // Empty on every non-ready state (loading is handled by its own branch above; reauth means the
  // backend deliberately withheld channels; a failed load has no channels to show either).
  protected readonly options = computed<ImportTargetOption[]>(() => {
    if (!this.channelsResource.hasValue()) {
      return [];
    }
    const result = this.channelsResource.value();
    return result.reauthRequired ? [] : importTargetOptions(result, this.data.currentChannelName);
  });

  protected isChannelChecked(channelName: string): boolean {
    const current = this.target();
    return current !== null && current.channelName === channelName;
  }

  protected selectChannel(channelName: string): void {
    this.target.set({ channelName });
  }

  protected submit(): void {
    const target = this.target();
    if (target === null) {
      return;
    }
    this.dialogRef.close({ scope: this.scope(), channelName: target.channelName });
  }
}

export function openImportTargetDialog(
  dialog: Dialog,
  data: ImportTargetDialogData,
): DialogRef<ImportTargetChoice | undefined> {
  return openAppDialog<ImportTargetChoice | undefined, ImportTargetDialogData>(
    dialog,
    ImportTargetDialog,
    { data },
  );
}
