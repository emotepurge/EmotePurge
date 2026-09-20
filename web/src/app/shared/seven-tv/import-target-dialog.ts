import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';

import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { ExportScope } from '../export/export-dialog';
import { Button } from '../ui/button';
import { openAppDialog } from '../ui/dialog';
import { DialogShell } from '../ui/dialog-shell';
import { NoticeBanner } from '../ui/notice-banner';
import { SkeletonRows } from '../ui/skeleton-rows';
import {
  ImportTargetAccountGroup,
  ImportTargetChoices,
  ImportTargetSetChoice,
  importTargetChoices,
} from './import-target-choices';

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
  /** `CapturedImportScope.emoteSetId` (spec 8.6) — the run's own source set. Disabled wherever it
   *  appears in the offer list ("das ist die Quelle"); the source *channel* otherwise stays fully
   *  in the list (spec 8.6, third bullet) — only this one set is off limits. */
  sourceEmoteSetId: string;
}

/** What was chosen: a scope over the source rows, plus the set they should go into (spec 8.6,
 *  replaces the channel-only shape). `channelName` is the tracked class's attribute — `null` for an
 *  untracked target, where `ownerDisplayName`/`setName` are what names the destination instead.
 *
 *  `twitchLogin` (spec 6.2) is the account's own — never compared against anything and never shown
 *  (E7 bars that role for any account identifier here); its one job is routing `loadImportTarget`'s
 *  live-list read to the right URL for an *untracked* target, which has no `channelName` to route
 *  with instead (`import-flow.ts`'s `toTargetSelection`). Carried for every choice, tracked or not,
 *  so the shape does not have to change again the day a tracked choice needs it too.
 *
 *  `activeEmoteSetId` (spec 6.2, `EmoteSetTargetAccount.activeEmoteSetId`) is the account's current
 *  active set — `null` for an untracked account. This is what lets `import-flow.ts`'s
 *  `toTargetSelection` tell "the chosen set happens to be the account's active one" apart from any
 *  other choice *before* firing a request (spec 8.6, fourth bullet; AK 36: the active case must not
 *  change which requests fire at all) — comparing `emoteSetId === activeEmoteSetId` here, rather
 *  than reusing `ImportTargetSetChoice.isActive` a second time, keeps that one decision explicit at
 *  the exact point it is made instead of trusting an already-baked-in boolean from an earlier step. */
export interface ImportTargetChoice {
  scope: ExportScope;
  emoteSetId: string;
  channelName: string | null;
  ownerDisplayName: string | null;
  setName: string;
  isTracked: boolean;
  twitchLogin: string;
  activeEmoteSetId: string | null;
}

type TargetSelection = Omit<ImportTargetChoice, 'scope'> | null;

/**
 * First step of the copy flow (#72, K3; sets since spec 2026-09-20 K2): choose *what* (scope) and
 * *where* (target set). Loads `GET /api/seventv/me/emote-set-targets` (spec 6.2) itself on open — no
 * `listMine()` anymore (AK 34): that call answers "which channels", this dialog needs "which sets",
 * and only the account-weight endpoint knows both a set list and its tracked/untracked class in one
 * request (spec E6).
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
  imports: [Button, DialogShell, NgTemplateOutlet, NoticeBanner, SkeletonRows, TranslocoPipe],
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

      @if (targetsResource.isLoading()) {
        <app-skeleton-rows [count]="3" />
      } @else {
        @if (loadFailed()) {
          <app-notice-banner variant="error">
            {{ 'import.target.loadFailed' | transloco }}
            <button
              notice-action
              type="button"
              appButton="outline"
              (click)="targetsResource.reload()"
            >
              {{ 'import.target.retry' | transloco }}
            </button>
          </app-notice-banner>
        } @else if (offerIncomplete()) {
          <app-notice-banner variant="info">
            {{ 'import.target.listIncomplete' | transloco }}
          </app-notice-banner>
        }

        <!-- role="radiogroup" only wraps the two @for blocks below, and only when there is at
             least one set to own: ARIA requires a radiogroup to contain at least one radio, and a
             account with zero sets (setsUnavailable, or genuinely none) can legitimately bring the
             total to zero. The "no set" message therefore renders as a sibling, never inside an
             otherwise-empty group — same idiom as the former channel-only picker. -->
        @if (hasAnySet()) {
          <div
            class="flex flex-col gap-3"
            role="radiogroup"
            [attr.aria-label]="'import.target.label' | transloco"
          >
            @for (group of choices().tracked; track group.twitchChannelId) {
              <ng-container *ngTemplateOutlet="accountGroup; context: { group }" />
            }
            @for (group of choices().untracked; track group.twitchChannelId) {
              <ng-container *ngTemplateOutlet="accountGroup; context: { group, untracked: true }" />
            }
          </div>
        } @else if (!loadFailed()) {
          <!-- Only when there genuinely is no set anywhere — after a reauth-less failure or a
               partial load the empty list says nothing about the account, and claiming otherwise
               invites ignoring the notice above. -->
          <p class="text-sm text-fg-muted">{{ 'import.target.none' | transloco }}</p>
        }
      }

      <!-- Untracked class only (spec 8.6, AK 35): choosing one of these sets does not select it
           outright — it stays pending here until confirmed. Cancelling leaves target(), and
           therefore every radio's checked state, exactly as it was before this click; nothing is
           blanked (see cancelUntrackedConfirmation()'s doc). A tracked pick never reaches this
           banner at all ("kein zweiter Schritt"). -->
      @if (pendingUntrackedTarget(); as pending) {
        <app-notice-banner variant="info">
          {{
            'import.target.confirmUntracked'
              | transloco: { setName: pending.setName, ownerDisplayName: pending.ownerDisplayName }
          }}
          <button
            notice-action
            type="button"
            appButton="outline"
            (click)="cancelUntrackedConfirmation()"
          >
            {{ 'common.cancel' | transloco }}
          </button>
          <button
            notice-action
            type="button"
            appButton="primary"
            [disabled]="emptyScopeChosen()"
            (click)="confirmUntrackedTarget()"
          >
            {{ 'import.target.confirmUntrackedSubmit' | transloco }}
          </button>
        </app-notice-banner>
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
        [disabled]="target() === null || emptyScopeChosen() || pendingUntrackedTarget() !== null"
        (click)="submit()"
      >
        {{ 'import.target.submit' | transloco }}
      </button>
    </app-dialog-shell>

    <ng-template #accountGroup let-group="group" let-untracked="untracked">
      <div class="flex flex-col gap-1">
        <!-- A tracked account whose active set is itself selectable gets its header *merged* into
             that set's own radio (headerSet()): clicking the account name is then the one click
             that lands on its active set (spec 8.6 — "die Wahl eines getrackten Accounts X in
             einem Schritt auf dessen aktivem Set landet"). An untracked account, or a tracked one
             without an eligible active set, keeps a plain, non-interactive header instead — see
             headerSet()'s own doc for why. -->
        @if (headerSet(group); as header) {
          <label class="flex items-center gap-2 py-1">
            <input
              type="radio"
              class="h-4 w-4 accent-accent-solid"
              name="import-target"
              [checked]="isSetChecked(header.emoteSetId)"
              (change)="selectSet(group, header)"
            />
            #{{ group.channelName }}
            <span class="text-xs text-fg-muted">
              ({{ 'import.target.active' | transloco }}: {{ header.setName }})
            </span>
          </label>
        } @else {
          <p class="text-xs font-medium text-fg-secondary">
            @if (group.channelName !== null) {
              #{{ group.channelName }}
            } @else {
              {{ group.twitchLogin }}
            }
            @if (untracked) {
              <span class="text-fg-muted">({{ 'import.target.untracked' | transloco }})</span>
            }
          </p>
        }
        @if (group.setsUnavailable) {
          <p class="text-xs text-fg-muted">{{ 'import.target.setsUnavailable' | transloco }}</p>
        }
        @for (set of remainingSets(group); track set.emoteSetId) {
          <label class="flex items-center gap-2 py-1" [class.opacity-60]="set.disabled">
            <input
              type="radio"
              class="h-4 w-4 accent-accent-solid"
              name="import-target"
              [disabled]="set.disabled"
              [checked]="isSetChecked(set.emoteSetId)"
              (change)="selectSet(group, set)"
            />
            {{ set.setName }}
            @if (set.isActive) {
              <span class="text-xs text-fg-muted">({{ 'import.target.active' | transloco }})</span>
            }
            @if (set.disabledReason === 'isSourceSet') {
              <span class="text-xs text-fg-muted"
                >({{ 'import.target.isSource' | transloco }})</span
              >
            } @else if (set.disabledReason === 'notNormalKind') {
              <span class="text-xs text-fg-muted">
                ({{
                  (set.isPersonal ? 'import.target.kindPersonal' : 'import.target.kindUnavailable')
                    | transloco
                }})
              </span>
            }
          </label>
        }
      </div>
    </ng-template>
  `,
})
export class ImportTargetDialog {
  protected readonly data = inject<ImportTargetDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<ImportTargetChoice | undefined>>(DialogRef);

  private readonly emoteSetService = inject(SevenTvEmoteSetService);

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

  // No target chosen yet, unless the preselection effect below already found one: "Weiter" stays
  // disabled until a set is picked one way or the other. Only ever holds an *already confirmed*
  // choice — an untracked pick never lands here directly, see pendingUntrackedTarget below.
  protected readonly target = signal<TargetSelection>(null);

  /** The untracked choice awaiting its confirmation banner (spec 8.6, AK 35) — `null` whenever none
   *  is pending. Deliberately a signal of its own rather than a second state stuffed into `target`:
   *  a cancelled confirmation must leave `target()` — and therefore every radio's checked state —
   *  exactly as it was before the click ("Abbruch lässt die Wahl unverändert", not "leert sie"),
   *  which only works if confirming and choosing are two different pieces of state to begin with.
   *  Confirming closes the whole dialog directly (confirmUntrackedTarget()) rather than ever
   *  promoting this into `target()` — there is no second "Weiter" click for the untracked class. */
  protected readonly pendingUntrackedTarget = signal<TargetSelection>(null);

  protected readonly targetsResource = rxResource({
    stream: () => this.emoteSetService.listEmoteSetTargets(),
  });

  // A genuine transport failure (network, 5xx) — distinct from the degraded-but-200
  // `sevenTvUnavailable` flag below, which the loaded result still carries.
  protected readonly loadFailed = computed(() => this.targetsResource.error() !== undefined);

  protected readonly offerIncomplete = computed(
    () => this.targetsResource.hasValue() && this.targetsResource.value().sevenTvUnavailable,
  );

  // Empty on every non-ready state (loading is handled by its own branch above; a failed load has
  // nothing to show either). importTargetChoices does the tracked/untracked split and the
  // per-set disabling (spec 8.6) — this component only renders its output and tracks a selection.
  protected readonly choices = computed<ImportTargetChoices>(() => {
    if (!this.targetsResource.hasValue()) {
      return { tracked: [], untracked: [] };
    }
    return importTargetChoices(this.targetsResource.value(), this.data.sourceEmoteSetId);
  });

  protected readonly hasAnySet = computed(() => {
    const choices = this.choices();
    return (
      choices.tracked.some((group) => group.sets.length > 0) ||
      choices.untracked.some((group) => group.sets.length > 0)
    );
  });

  constructor() {
    // Initial load-time default only — the *other* half of preselection (spec 8.6 / Konzept 7.5
    // Baustein 2, "das aktive Set beschriftet und vorausgewählt, damit der heutige Ein-Klick-Weg
    // 'in Kanal X' unverändert bleibt") is per-account and lives in headerSet(): every tracked
    // account's own header is already a one-click shortcut to its active set, on its own, without
    // this effect. This effect only supplies the "nothing chosen yet" starting point — the eigene
    // bzw. erste getrackte Account's active set — so the common case still needs zero clicks, not
    // just one. Scoped to *tracked* accounts (via firstPreselectableTarget → headerSet) — an
    // untracked target always needs the confirmation step T2.6 adds, so auto-selecting one here
    // would let a submit skip it. Runs once data arrives and only while nothing has been chosen
    // yet; a user's own click always wins and is never overwritten, including across a later
    // `targetsResource.reload()` — the `target() !== null` guard is exactly what keeps this from
    // re-firing once a choice, theirs or this effect's own, already exists.
    effect(() => {
      if (this.target() !== null || !this.targetsResource.hasValue()) {
        return;
      }
      const preselected = this.firstPreselectableTarget();
      if (preselected !== null) {
        this.target.set(preselected);
      }
    });
  }

  /** A pending untracked confirmation shows *its own* candidate as checked, never `target()`, while
   *  it is up — this is also a technical necessity, not just a UX choice: a native radio input
   *  flips its own DOM `checked` property the instant it is clicked, before Angular ever runs
   *  `selectSet()`. If `isSetChecked` kept comparing against `target()` alone, that comparison
   *  would evaluate to the exact same `false` before and after an untracked click (an untracked
   *  pick never touches `target()`), Angular's binding would see no change to commit, and the
   *  browser's own already-applied `checked = true` would be left standing uncorrected — a radio
   *  showing checked for a choice that was never actually made. Routing the comparison through
   *  `pendingUntrackedTarget()` instead makes the bound expression's value genuinely flip on both
   *  the click and the cancel, so Angular always has something to write back. Cancelling clears the
   *  pending signal and this reverts to comparing against `target()` again, exactly restoring the
   *  prior radio state (AK 35's "Abbruch lässt die Wahl unverändert") — the *logical* choice
   *  (`target()`, and therefore what `submit()`/`confirmUntrackedTarget()` can close with) is what
   *  stays untouched throughout; only this rendering detail is pending-aware. */
  protected isSetChecked(emoteSetId: string): boolean {
    const pending = this.pendingUntrackedTarget();
    if (pending !== null) {
      return pending.emoteSetId === emoteSetId;
    }
    const current = this.target();
    return current !== null && current.emoteSetId === emoteSetId;
  }

  /**
   * The set a tracked account's own header row stands in for — its active set, but only when that
   * set is itself selectable (spec 8.6: a disabled set, source or non-NORMAL, "kommen als
   * Vorauswahl nicht in Frage" — the header then falls back to plain text and that set stays a
   * normal row via {@link remainingSets}). `null` for every untracked account, unconditionally: an
   * untracked target always needs T2.6's confirmation step, so the account header must never become
   * a one-click shortcut past it.
   */
  protected headerSet(group: ImportTargetAccountGroup): ImportTargetSetChoice | null {
    if (!group.isTracked) {
      return null;
    }
    return group.sets.find((set) => set.isActive && !set.disabled) ?? null;
  }

  /** `group.sets` minus whichever one {@link headerSet} already rendered as the account's own
   *  header radio, so the same set never appears twice. */
  protected remainingSets(group: ImportTargetAccountGroup): ImportTargetSetChoice[] {
    const header = this.headerSet(group);
    return header === null ? group.sets : group.sets.filter((set) => set !== header);
  }

  /**
   * A tracked set is selected outright, same as always ("kein zweiter Schritt", spec 8.6). An
   * untracked one only becomes *pending* (AK 35) — it needs `confirmUntrackedTarget()`'s explicit
   * confirmation before it can become `target()` at all, and until then any earlier `target()` is
   * left completely untouched: clicking around in the untracked list, cancelling, clicking again,
   * none of it must overwrite an already-made tracked choice by accident.
   */
  protected selectSet(group: ImportTargetAccountGroup, set: ImportTargetSetChoice): void {
    const candidate: TargetSelection = {
      emoteSetId: set.emoteSetId,
      channelName: group.channelName,
      ownerDisplayName: set.ownerDisplayName,
      setName: set.setName,
      isTracked: group.isTracked,
      twitchLogin: group.twitchLogin,
      activeEmoteSetId: group.activeEmoteSetId,
    };
    if (!group.isTracked) {
      this.pendingUntrackedTarget.set(candidate);
      return;
    }
    // A tracked click abandons any confirmation the user may have had open for a different,
    // untracked set — there is nothing left to confirm once a one-click tracked pick has won.
    this.pendingUntrackedTarget.set(null);
    this.target.set(candidate);
  }

  /** Leaves `target()` — and every radio's checked state derived from it — exactly as it was
   *  before the untracked click that opened this confirmation (AK 35: "Abbruch lässt die Wahl
   *  unverändert", explicitly not "leert sie"). */
  protected cancelUntrackedConfirmation(): void {
    this.pendingUntrackedTarget.set(null);
  }

  /** Closes the whole picker directly with the confirmed untracked choice (AK 35: "Bestätigung
   *  schließt den Picker mit `channelName: null`") — there is no separate "Weiter" click for this
   *  class, unlike a tracked pick, which still goes through submit(). `pending.channelName` is
   *  already `null` here: it came from an untracked account group's own `channelName`, which is
   *  `null` by {@link ImportTargetAccountGroup}'s own contract. */
  protected confirmUntrackedTarget(): void {
    const pending = this.pendingUntrackedTarget();
    if (pending === null || this.emptyScopeChosen()) {
      return;
    }
    this.pendingUntrackedTarget.set(null);
    this.dialogRef.close({ scope: this.scope(), ...pending });
  }

  protected submit(): void {
    const target = this.target();
    if (target === null) {
      return;
    }
    this.dialogRef.close({ scope: this.scope(), ...target });
  }

  /**
   * The dialog's initial `target` (spec 8.6 / Konzept 7.5 Baustein 2: "ein Anfangszustand beim
   * Laden: der eigene bzw. erste getrackte Account mit seinem aktiven Set"). Reuses
   * {@link headerSet} deliberately — the account this picks is exactly the one whose header is
   * already a live one-click shortcut to the same set, so the initial state and the "choose account
   * X" shortcut always agree on what "X's active set" means, by construction rather than by keeping
   * two rules in sync by hand.
   */
  private firstPreselectableTarget(): TargetSelection {
    for (const group of this.choices().tracked) {
      const header = this.headerSet(group);
      if (header !== null) {
        return {
          emoteSetId: header.emoteSetId,
          channelName: group.channelName,
          ownerDisplayName: header.ownerDisplayName,
          setName: header.setName,
          isTracked: group.isTracked,
          twitchLogin: group.twitchLogin,
          activeEmoteSetId: group.activeEmoteSetId,
        };
      }
    }
    return null;
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
