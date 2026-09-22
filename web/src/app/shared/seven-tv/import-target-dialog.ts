import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { NgTemplateOutlet } from '@angular/common';
import { Component, computed, effect, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { LanguageService } from '../../core/i18n/language.service';
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
 *  `ownerDisplayName` is never `null` and never blank — `import-target-choices.ts`'s
 *  `resolveOwnerLabel` has already fallen back to the account's Twitch login (or, failing that, a
 *  translated "unknown owner" text) by the time a set reaches this shape, so every later consumer
 *  (the picker's own untracked-confirmation banner, the confirm dialog header, the progress section)
 *  can read it directly without inventing its own `?? ''` (Codex round 3 P2).
 *
 *  `twitchLogin` (spec 6.2) is the account's own — never compared against anything as an *identity*
 *  here (E7 bars that role); its primary job is routing `loadImportTarget`'s live-list read to the
 *  right URL for an *untracked* target, which has no `channelName` to route with instead
 *  (`import-flow.ts`'s `toTargetSelection`) — and it is also `resolveOwnerLabel`'s fallback source
 *  for `ownerDisplayName` above. Carried for every choice, tracked or not, so the shape does not
 *  have to change again the day a tracked choice needs it too.
 *
 *  `activeEmoteSetId` (spec 6.2, `EmoteSetTargetAccount.activeEmoteSetId`) is the account's current
 *  active set — `null` for an untracked account, and also `null` whenever the account's reported
 *  active set is itself `PERSONAL` (spec addendum 39, `import-target-choices.ts`'s `toAccountGroup`):
 *  a `PERSONAL` set never becomes a row in this picker at all, so nothing may still describe it as
 *  "the active one" once it is filtered out. This is what lets `import-flow.ts`'s
 *  `toTargetSelection` tell "the chosen set happens to be the account's active one" apart from any
 *  other choice *before* firing a request (spec 8.6, fourth bullet; AK 36: the active case must not
 *  change which requests fire at all) — comparing `emoteSetId === activeEmoteSetId` here, rather
 *  than reusing `ImportTargetSetChoice.isActive` a second time, keeps that one decision explicit at
 *  the exact point it is made instead of trusting an already-baked-in boolean from an earlier step. */
export interface ImportTargetChoice {
  scope: ExportScope;
  emoteSetId: string;
  channelName: string | null;
  ownerDisplayName: string;
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
           banner at all ("kein zweiter Schritt").

           Wording is a target *confirmation*, not an action verb (finding 7, Live-Verifikation K2
           2026-09-21): the live text used to read "In das Set '…' kopieren?" with a "Kopieren"
           button next to the picker's own disabled "Weiter" — two differently-labelled "copy"
           affordances side by side, when this button copies nothing at all, it only locks in the
           target and closes the picker (confirmUntrackedTarget() does exactly what the old
           "Kopieren" button did). "Ja, dieses Set" / "Anderes Set wählen" name what each button
           actually does — confirm this target, or go back to choosing — without echoing "kopieren"
           a second time before the confirm dialog (the actual copy step) has even opened.

           The text and its two buttons are stacked (buttons in their own row *below* the text),
           not laid out side by side (#217, T2 fix): NoticeBanner's own [notice-action] slot is
           right-aligned next to the content and, with two buttons in it, wrapped into a narrow
           column that squeezed each button's label to one or two words per line. Neither button
           uses notice-action here — both live inside the default content slot instead, in their
           own flex row underneath the paragraph, so the banner's single content item gets the
           whole width to stack in rather than sharing a row with a right-aligned action area. -->
      @if (pendingUntrackedTarget(); as pending) {
        <app-notice-banner variant="info">
          <div class="flex flex-col gap-3">
            <p>
              {{
                'import.target.confirmUntracked'
                  | transloco
                    : { setName: pending.setName, ownerDisplayName: pending.ownerDisplayName }
              }}
            </p>
            <div class="flex flex-wrap gap-2">
              <button type="button" appButton="outline" (click)="cancelUntrackedConfirmation()">
                {{ 'import.target.confirmUntrackedReject' | transloco }}
              </button>
              <button
                type="button"
                appButton="primary"
                [disabled]="emptyScopeChosen()"
                (click)="confirmUntrackedTarget()"
              >
                {{ 'import.target.confirmUntrackedAccept' | transloco }}
              </button>
            </div>
          </div>
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
        <!-- One plain heading for every account, tracked or untracked, regardless of how many sets
             it has (spec addendum 39, #217) — the old "channel itself is the radio" shortcut for a
             tracked account's selectable active set is gone: a <p>, never an <input>, so it is
             never a radio and never a stop in the radiogroup's native roving tab order. Every set
             below it, including a single one, gets its own radio row instead — the same shape every
             account gets, one click on that radio being the whole cost either way. -->
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
        @if (group.setsUnavailable) {
          <p class="text-xs text-fg-muted">{{ 'import.target.setsUnavailable' | transloco }}</p>
        } @else if (group.noUsableSets) {
          <!-- The account's set list was read fine but left nothing offerable — genuinely empty, or
               every set on it was PERSONAL and importTargetChoices already filtered it out (spec
               addendum 39, operator decision 2026-09-22). Distinct from setsUnavailable above: this
               is not a read failure, so it gets its own, less alarming wording. -->
          <p class="text-xs text-fg-muted">{{ 'import.target.noUsableSets' | transloco }}</p>
        }
        <!-- group.sets never contains a PERSONAL set (importTargetChoices filters it out before
             this template ever sees it, spec addendum 39) — the only disabled, non-source kind left
             here is notNormalKind, which can now only mean GLOBAL/SPECIAL and always gets the same
             label. -->
        @for (set of group.sets; track set.emoteSetId) {
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
                ({{ 'import.target.kindUnavailable' | transloco }})
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
  private readonly languageService = inject(LanguageService);
  private readonly translocoService = inject(TranslocoService);

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

  // Codex round 3 P2: the one translated fallback resolveOwnerLabel needs for the case 6.2 says
  // "should not happen" (an owner with neither a display name nor a login) — reads lang() first so
  // a language switch while the dialog is open re-resolves it, same reasoning as
  // import-confirm-dialog.ts's fileDetails/aliasMismatchRows.
  protected readonly unknownOwnerLabel = computed(() => {
    this.languageService.lang();
    return this.translocoService.translate('import.target.unknownOwner');
  });

  // Empty on every non-ready state (loading is handled by its own branch above; a failed load has
  // nothing to show either). importTargetChoices does the tracked/untracked split, the per-set
  // disabling (spec 8.6), and the owner-label fallback (Codex round 3 P2) — this component only
  // renders its output and tracks a selection.
  protected readonly choices = computed<ImportTargetChoices>(() => {
    if (!this.targetsResource.hasValue()) {
      return { tracked: [], untracked: [] };
    }
    return importTargetChoices(
      this.targetsResource.value(),
      this.data.sourceEmoteSetId,
      this.unknownOwnerLabel(),
    );
  });

  protected readonly hasAnySet = computed(() => {
    const choices = this.choices();
    return (
      choices.tracked.some((group) => group.sets.length > 0) ||
      choices.untracked.some((group) => group.sets.length > 0)
    );
  });

  constructor() {
    // Initial load-time default only — the caller's own account's active set, and nothing else
    // (spec 8.6 / Konzept 7.5 Baustein 2: "ein Anfangszustand beim Laden: der eigene Account mit
    // seinem aktiven Set"). Since addendum 39 removed the account-header shortcut, this effect is
    // now the *only* place that preselection happens — there is no longer a second, per-account
    // "click the header" path that agrees with it by construction; firstPreselectableTarget derives
    // its answer straight from the own account's own set list instead (see that method's doc for
    // the finding-4 bug this still guards against). Scoped to *tracked* accounts — an untracked
    // target always needs the confirmation step AK 35 adds, so auto-selecting one here would let a
    // submit skip it. Runs once data arrives and only while nothing has been chosen yet; a user's
    // own click always wins and is never overwritten, including across a later
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
   * Laden: der eigene Account mit seinem aktiven Set"). Since addendum 39 (#217) removed the
   * account-header shortcut, there is no longer a second place that already computes "this
   * account's active, selectable set" for this method to reuse — it looks the set up directly in
   * `own.sets`, the same field the template now renders every row from, so this and the rendered
   * rows can never disagree on which one is "the active one".
   *
   * Looks up the account by `isOwnAccount`, never by list position (finding 4, Live-Verifikation K2
   * 2026-09-21): a first draft walked `choices().tracked` in order and returned the first group
   * with a selectable active set, which reads as "the caller's own account" only as long as that
   * account's own active set happens to be selectable. The moment it is not — because it is the
   * copy's own source set, disabled by {@link ImportTargetSetChoice.disabled} — the loop fell
   * through to the *next* tracked account instead, silently landing the preselection on a different
   * streamer's channel (observed live: opening the picker from the caller's own channel with its
   * active set as the source preselected an unrelated moderated channel). `isOwnAccount` answers
   * "is this the caller's own Twitch identity" regardless of where 6.2 placed it in the response and
   * regardless of which channel page the picker was opened from (its own `data.currentChannelName`
   * plays no role here at all) — so a disabled own active set now falls through to "nothing
   * preselected", never to someone else's channel. No own account in the tracked list (it is
   * untracked, or absent — a moderator's own channel need not be tracked), or an own account whose
   * reported active set is itself `PERSONAL` and therefore filtered out of `sets` entirely (spec
   * addendum 39) and never carries `isActive: true` on any rendered row, are the same "nothing
   * preselected" outcome, unchanged from before.
   */
  private firstPreselectableTarget(): TargetSelection {
    const own = this.choices().tracked.find((group) => group.isOwnAccount);
    if (own === undefined) {
      return null;
    }
    const activeSet = own.sets.find((set) => set.isActive && !set.disabled);
    if (activeSet === undefined) {
      return null;
    }
    return {
      emoteSetId: activeSet.emoteSetId,
      channelName: own.channelName,
      ownerDisplayName: activeSet.ownerDisplayName,
      setName: activeSet.setName,
      isTracked: own.isTracked,
      twitchLogin: own.twitchLogin,
      activeEmoteSetId: own.activeEmoteSetId,
    };
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
