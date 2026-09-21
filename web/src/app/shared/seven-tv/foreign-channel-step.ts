import { HttpErrorResponse } from '@angular/common/http';
import { Component, ElementRef, computed, inject, signal, viewChild } from '@angular/core';
import { FormControl, ReactiveFormsModule } from '@angular/forms';
import { TranslocoPipe } from '@jsverse/transloco';

import { channelNameValidator, normalizeChannelName } from '../../core/channels/channel-name';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import {
  ForeignEmoteRow,
  ForeignEmoteSetResponse,
} from '../../core/seven-tv/foreign-emote-set.model';
import { EmoteSetSummary } from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { Button } from '../ui/button';
import { NoticeBanner } from '../ui/notice-banner';
import { SkeletonRows } from '../ui/skeleton-rows';
import { ForeignEmoteGrid } from './foreign-emote-grid';

/**
 * Per-row height of one radiogroup option, in rem — a labelled `py-1` row at the app's `text-sm` line
 * height (spec addendum 2026-09-21, review finding P2-1). Folded into {@link ForeignChannelStep.gridReservedRem}
 * so {@link ForeignEmoteGrid}'s own height budget accounts for however many rows this step's
 * radiogroup actually renders, instead of assuming a fixed shape.
 */
const RADIO_ROW_REM = 1.75;
/** The one extra `gap-3` the radiogroup's own flex item costs the step's host stack, on top of the
 *  `gap-1` between its own rows (already folded into {@link RADIO_ROW_REM}) — see gridReservedRem. */
const RADIOGROUP_GAP_REM = 0.75;

/**
 * What this step yields once the user has picked emotes to bring over — a self-contained payload,
 * not an `ImportSource`/`ImportOrigin`. Those types (`core/seven-tv/import-source.ts`) carry the
 * third `'seventv-channel'` variant, and `buildForeignImportSource` turns this result into one; the
 * step stays unaware of that union so it does not have to change again the day it grows a fourth
 * member.
 */
export interface ForeignChannelImportResult {
  /** Normalized (Regel 9) — what the resolved channel is actually called. */
  channelName: string;
  /**
   * Always `null` since K3 (spec 2026-09-20, E8): every preview this step loads goes through the
   * set-ID read mode (8.7), which never resolves a 7TV identity at all. Nothing downstream reads
   * this field — it is carried only because the payload shape predates the set-ID-only flow.
   */
  sevenTvUserId: string | null;
  emoteSetId: string;
  /** Only the rows the user marked, in the grid's selection order. Never the full set — the whole
   *  point of this step (spec Falle F4, the user's "NICHT alle direkt übernehmen"). */
  rows: ForeignEmoteRow[];
}

/** The picked set's preview, nested under the resolved set list below — a separate, smaller state
 *  machine because switching the radiogroup selection (or retrying just the preview) must not
 *  re-fetch the set list itself (spec 8.7, "kein zweiter Request, wenn das aktive Set gewählt
 *  bleibt"). `'none'` is the one state that fires no HTTP request at all and renders a notice instead
 *  of the grid (review finding P2-2): 7TV reported no active set for this account at all, or the
 *  reported one is not a usable import source (a `PERSONAL` set — spec addendum 2026-09-21, hidden
 *  from this picker entirely), and nothing has been picked yet either. */
type PreviewState =
  | { status: 'none' }
  | { status: 'loading' }
  | { status: 'error'; error: HttpErrorResponse }
  | { status: 'loaded'; response: ForeignEmoteSetResponse };

type LoadState =
  | { status: 'idle' }
  | { status: 'loadingSets' }
  | { status: 'setsError'; error: HttpErrorResponse }
  | {
      status: 'ready';
      channelName: string;
      sets: EmoteSetSummary[];
      /** `''` when there is no *usable* active set: 7TV reports no active set for this account at
       *  all (rare — spec 6.3's own "" spelling, the same one `Channel.ActiveEmoteSetId` uses before
       *  a first sync, E21), or it reports one that is `PERSONAL` (review finding P2-2 — treated the
       *  same as "none" because the spec addendum hides `PERSONAL` sets from this picker outright). */
      activeEmoteSetId: string;
      selectedEmoteSetId: string;
      preview: PreviewState;
    };

/**
 * The "Aus einem Kanal" branch of the one import dialog (spec §7, extended by K3/spec 8.7): a
 * logged-in user types an arbitrary Twitch login, resolves that channel's 7TV emote sets — no role
 * in that channel required — picks one (the active one preselected) in a radiogroup, and marks
 * individual emotes of its preview in the `ForeignEmoteGrid` below.
 *
 * It reports its state rather than closing anything: {@link result} is `null` until a set's preview
 * is loaded *and* something is selected, and `ImportSourceDialog` renders the "Weiter" button of the
 * shared action row against it (§7 keeps the action row with the dialog, not with the step).
 *
 * {@link focusFirstControl} is how the dialog puts the caret in the channel field on the way in —
 * CDK's autofocus fires once when the overlay opens and never again for a swap inside it (#147), so
 * the contract has to be spoken. Here it also earns its keep beyond mere reachability: the step
 * exists to be typed into, and this way it can be typed into at once.
 *
 * A fresh channel query re-renders the `@case ('ready')` branch from scratch (`load()` moves the
 * state back through `'loadingSets'`), which unmounts and remounts `ForeignEmoteGrid` — a new
 * `ListSelection` instance, i.e. a new query always starts unselected. That is deliberate: a
 * selection made against one channel's 7TV ids has no honest meaning carried over to a different
 * channel's set, and the same reasoning applies to switching the radiogroup to a different set of
 * the *same* channel (`selectSet`) — its rows have nothing in common with the previous set's either.
 */
@Component({
  selector: 'app-foreign-channel-step',
  imports: [
    Button,
    ForeignEmoteGrid,
    NoticeBanner,
    ReactiveFormsModule,
    SkeletonRows,
    TranslocoPipe,
  ],
  template: `
    <!-- The 24rem cap is NOT a second opinion on the pane width (§7: that belongs to the pane, and
         the dialog widens it for the grid). It earns its place in the LOADED state, where this form
         stays on screen above a 72rem grid: uncapped, a field for a dozen-character Twitch login
         would stretch across the whole pane. At 24rem it is instead the same size before and after
         "Set laden" — the pane resizes around it, the field does not move. Before the load the cap
         is nearly inert (the 28rem pane leaves ~25rem of content width), which is the point. -->
    <form class="flex max-w-sm flex-col gap-1" (submit)="onFormSubmit($event)">
      <!-- A visible label, not just a placeholder: this is a form field in a dialog body, not a
           filter toolbar, so the design language's label duty (§5.2) applies in full. -->
      <label class="text-sm text-fg-secondary" [for]="channelInputId">
        {{ 'import.foreignChannel.channelLabel' | transloco }}
      </label>
      <!-- No wrap, on purpose rather than by inheritance: in the narrow (pre-grid) pane the button
           would drop under the field, and with it the field's height would fall back from the 44 px
           line to the plain input height — a control that changes size at a breakpoint nobody chose.
           A min-width of zero lets the field shrink instead, so the pair stays one line at any width.
           The gap is the dialog's own rhythm (12 px), not the 8 px chip spacing of a toolbar. -->
      <div class="flex gap-3">
        <input
          #channelInput
          type="text"
          [id]="channelInputId"
          [formControl]="channelNameControl"
          [placeholder]="'import.foreignChannel.placeholder' | transloco"
          [attr.aria-invalid]="
            channelNameControl.invalid && channelNameControl.touched ? 'true' : null
          "
          [attr.aria-describedby]="
            channelNameControl.invalid && channelNameControl.touched
              ? 'foreign-channel-name-error'
              : null
          "
          class="app-input min-h-11 min-w-0 flex-1"
        />
        <!-- The 44 px floor on the field above is the very floor this size tier defines (button.ts
             SIZE_CLASSES.lg), stated rather than inherited from the sibling's height: the two are
             then the same height because both say so, not because flexbox happened to stretch one
             of them to the other. -->
        <button
          type="submit"
          appButton="outline"
          buttonSize="lg"
          [disabled]="state().status === 'loadingSets'"
        >
          {{ 'import.foreignChannel.load' | transloco }}
        </button>
      </div>
      @if (channelNameControl.invalid && channelNameControl.touched) {
        <p id="foreign-channel-name-error" class="text-sm text-danger-fg">
          {{ 'import.foreignChannel.invalidChannelName' | transloco }}
        </p>
      }
    </form>

    @switch (state().status) {
      @case ('loadingSets') {
        <app-skeleton-rows [count]="3" />
      }
      @case ('setsError') {
        <app-notice-banner variant="error">
          {{ setsErrorMessageKey() | transloco }}
          <button notice-action type="button" appButton="outline" (click)="submit()">
            {{ 'import.foreignChannel.retry' | transloco }}
          </button>
        </app-notice-banner>
      }
      @case ('ready') {
        @if (readyState(); as ready) {
          <div class="flex items-center justify-between gap-2">
            <span class="text-xs text-fg-muted">#{{ ready.channelName }}</span>
            <button type="button" appButton="neutral" (click)="reload()">
              {{ 'import.foreignChannel.reload' | transloco }}
            </button>
          </div>

          <!-- Always rendered once there is at least one offerable set (spec addendum 2026-09-21,
               review finding: one layout regardless of set count — a single-set account no longer
               gets a bare "one click on the channel" shortcut, it gets the same one-row radiogroup
               every other account gets, active preselected and labelled). PERSONAL sets are
               filtered out of selectableRadioSets entirely (spec addendum, reversing 8.6's "nie
               ausgeblendet" for PERSONAL only) — never rendered, not even disabled. GLOBAL/SPECIAL
               keep 8.6's original treatment: visible, disabled, labelled. -->
          @if (selectableRadioSets().length > 0) {
            <div
              class="flex flex-col gap-1"
              role="radiogroup"
              [attr.aria-label]="'import.foreignChannel.setsLabel' | transloco"
            >
              @for (set of selectableRadioSets(); track set.id) {
                <label
                  class="flex items-center gap-2 py-1"
                  [class.opacity-60]="set.kind !== 'NORMAL'"
                >
                  <input
                    type="radio"
                    class="h-4 w-4 accent-accent-solid"
                    name="foreign-channel-set"
                    [disabled]="set.kind !== 'NORMAL'"
                    [checked]="ready.selectedEmoteSetId === set.id"
                    (change)="selectSet(set.id)"
                  />
                  {{ set.name }}
                  @if (set.id === ready.activeEmoteSetId) {
                    <span class="text-xs text-fg-muted"
                      >({{ 'import.foreignChannel.active' | transloco }})</span
                    >
                  }
                  @if (set.kind !== 'NORMAL') {
                    <!-- Never PERSONAL here — selectableRadioSets already excludes it, so the only
                         non-NORMAL kinds left are GLOBAL/SPECIAL, both labelled the same way. -->
                    <span class="text-xs text-fg-muted">
                      ({{ 'import.foreignChannel.kindUnavailable' | transloco }})
                    </span>
                  }
                </label>
              }
            </div>
          }

          @switch (ready.preview.status) {
            @case ('none') {
              <!-- P2-2: no usable active set, and either zero or more than one selectable set to
                   fall back to automatically — the radiogroup above (if anything renders in it at
                   all) is where the choice is made; this notice is what used to be silence. -->
              <app-notice-banner variant="info">
                {{ 'import.foreignChannel.noActiveSet' | transloco }}
              </app-notice-banner>
            }
            @case ('loading') {
              <app-skeleton-rows [count]="3" />
            }
            @case ('error') {
              <app-notice-banner variant="error">
                {{ previewErrorMessageKey() | transloco }}
                <button notice-action type="button" appButton="outline" (click)="retryPreview()">
                  {{ 'import.foreignChannel.retry' | transloco }}
                </button>
              </app-notice-banner>
            }
            @case ('loaded') {
              <app-foreign-emote-grid
                [emotes]="ready.preview.response.emotes"
                [truncated]="ready.preview.response.truncated"
                [totalCount]="ready.preview.response.totalCount"
                [reservedRem]="gridReservedRem()"
                (selectionChange)="onSelectionChange($event)"
              />
            }
          }
        }
      }
    }
  `,
  host: { class: 'flex min-h-0 flex-col gap-3' },
})
export class ForeignChannelStep {
  private readonly emoteSetService = inject(SevenTvEmoteSetService);

  // Validates the *normalized* value (Regel 9) — same reasoning and the same validator the admin
  // "join channel" form uses (`admin-channels-page.ts`): an admin/user can paste "HandOfBlood" the
  // way Twitch displays it, since the server normalizes before matching its own lower-case pattern.
  protected readonly channelNameControl = new FormControl('', {
    nonNullable: true,
    validators: [channelNameValidator],
  });

  protected readonly channelInputId = 'foreign-channel-name';

  private readonly channelInputRef = viewChild<ElementRef<HTMLInputElement>>('channelInput');

  protected readonly state = signal<LoadState>({ status: 'idle' });
  protected readonly selectedRows = signal<ForeignEmoteRow[]>([]);

  /** Bumped on every {@link loadPreview} call — the only way {@link applyPreviewResult} tells a
   *  stale answer from the latest one when two requests target the very same set id (review finding
   *  P3-5: a double-fired retry, or a fast pick-A-then-B-then-A-again), which comparing
   *  `selectedEmoteSetId` alone cannot distinguish — that guard only rules out an answer for a set
   *  that is no longer selected, not an older answer for the set that still is. */
  private latestPreviewRequestId = 0;

  protected readonly readyState = computed(() => {
    const current = this.state();
    return current.status === 'ready' ? current : null;
  });

  /** The sets the radiogroup renders — every set except `PERSONAL` (spec addendum 2026-09-21):
   *  `GLOBAL`/`SPECIAL` still render, disabled and labelled (8.6, unchanged); `PERSONAL` is hidden
   *  entirely, not merely disabled. Empty (and therefore no radiogroup at all — see the template)
   *  when the account has nothing but personal sets, the same shape as an account with no sets. */
  protected readonly selectableRadioSets = computed<EmoteSetSummary[]>(() => {
    const ready = this.readyState();
    return ready === null ? [] : ready.sets.filter((set) => !set.isPersonal);
  });

  /** Extra vertical rem the radiogroup above the grid takes inside the same scrolling pane, passed
   *  straight through to {@link ForeignEmoteGrid.reservedRem} (review finding P2-1) — see that
   *  input's own doc for the height-budget reasoning. `0` when nothing renders. */
  protected readonly gridReservedRem = computed(() => {
    const count = this.selectableRadioSets().length;
    return count === 0 ? 0 : count * RADIO_ROW_REM + RADIOGROUP_GAP_REM;
  });

  protected readonly setsErrorMessageKey = computed(() => {
    const current = this.state();
    return current.status === 'setsError' ? apiErrorTranslationKey(current.error) : null;
  });

  protected readonly previewErrorMessageKey = computed(() => {
    const current = this.state();
    return current.status === 'ready' && current.preview.status === 'error'
      ? apiErrorTranslationKey(current.preview.error)
      : null;
  });

  private readonly loadedResponse = computed<ForeignEmoteSetResponse | null>(() => {
    const current = this.state();
    return current.status === 'ready' && current.preview.status === 'loaded'
      ? current.preview.response
      : null;
  });

  /**
   * Whether the emote grid is on screen. The dialog widens its pane against exactly this and
   * nothing else (§7.3): entering this step is a form, and a form does not need 72rem — the grid
   * does, and it arrives once a set's preview has actually loaded (not merely once the set list
   * resolved — the radiogroup alone fits the narrow pane).
   */
  readonly showsGrid = computed(() => this.loadedResponse() !== null);

  /**
   * The step's whole outward contract: `null` while there is nothing to carry forward, the payload
   * as soon as a loaded preview has at least one marked emote. A signal rather than a method so the
   * dialog's own `computed()` over a `viewChild` reacts to it (Regel 14).
   */
  readonly result = computed<ForeignChannelImportResult | null>(() => {
    const response = this.loadedResponse();
    const rows = this.selectedRows();
    if (response === null || rows.length === 0) {
      return null;
    }
    return {
      channelName: response.channelName,
      sevenTvUserId: response.sevenTvUserId,
      emoteSetId: response.emoteSetId,
      rows,
    };
  });

  /** Where the caret goes when this step is entered — see the class doc. Called by the dialog after
   *  the step has rendered, never from a constructor (Regel 13). */
  focusFirstControl(): void {
    this.channelInputRef()?.nativeElement.focus();
  }

  protected onFormSubmit(event: Event): void {
    event.preventDefault();
    this.submit();
  }

  protected submit(): void {
    if (this.channelNameControl.invalid) {
      this.channelNameControl.markAsTouched();
      return;
    }
    this.load();
  }

  /**
   * Re-fetches only the currently selected set's preview with `refresh: true` — never the set list,
   * which has no such bypass on the backend (`ISevenTvEmoteSetListService.ListByTwitchIdAsync` takes
   * no `refresh` parameter at all, spec 6.1/6.3: its own 60 s cache is not client-bypassable). "Neu
   * laden" therefore means "bypass the preview cache for this set", not "start over".
   */
  protected reload(): void {
    const current = this.state();
    if (current.status !== 'ready' || current.selectedEmoteSetId === '') {
      return;
    }
    this.selectedRows.set([]);
    this.state.set({ ...current, preview: { status: 'loading' } });
    this.loadPreview(current.channelName, current.selectedEmoteSetId, true);
  }

  /** The same request without the cache bypass — offered after a preview (not a set-list) failure. */
  protected retryPreview(): void {
    const current = this.state();
    if (current.status !== 'ready' || current.selectedEmoteSetId === '') {
      return;
    }
    this.state.set({ ...current, preview: { status: 'loading' } });
    this.loadPreview(current.channelName, current.selectedEmoteSetId, false);
  }

  /**
   * A radiogroup click. `kind !== 'NORMAL'` sets are rendered disabled (8.6) so the native radio
   * itself already refuses this, but the guard stays here too — defence for a call site that is not
   * the template, and free given `set.kind` is not otherwise threaded through. Re-picking the
   * already-selected set is the "kein zweiter Request" case (spec 8.7, AK 48) and is a no-op below.
   */
  protected selectSet(setId: string): void {
    const current = this.state();
    if (current.status !== 'ready' || current.selectedEmoteSetId === setId) {
      return;
    }
    // A different set's rows have no honest meaning carried over — same reasoning as a fresh
    // channel query (class doc).
    this.selectedRows.set([]);
    this.state.set({ ...current, selectedEmoteSetId: setId, preview: { status: 'loading' } });
    this.loadPreview(current.channelName, setId, false);
  }

  protected onSelectionChange(rows: ForeignEmoteRow[]): void {
    this.selectedRows.set(rows);
  }

  private load(): void {
    const channelName = normalizeChannelName(this.channelNameControl.value);
    this.state.set({ status: 'loadingSets' });
    this.selectedRows.set([]);
    this.emoteSetService.listForeignChannelEmoteSets(channelName).subscribe({
      next: (listResponse) => {
        const sets = listResponse.sets;
        const reportedActive = sets.find((set) => set.id === listResponse.activeEmoteSetId) ?? null;
        // A PERSONAL active set is no more usable than no active set at all (spec addendum
        // 2026-09-21: PERSONAL is hidden from this picker outright, so nothing here may point the
        // initial selection at one) — folded into the same '' the "genuinely no active set" case
        // already used, rather than a second sentinel the rest of the class would have to know
        // about too.
        const activeEmoteSetId =
          reportedActive !== null && !reportedActive.isPersonal
            ? listResponse.activeEmoteSetId
            : '';
        // P2-2: no usable active set must not dead-end the step. Exactly one selectable (NORMAL)
        // set is an unambiguous choice and gets preselected automatically; anything else — none, or
        // more than one — leaves the pick to the radiogroup and shows a notice instead of nothing.
        const selectableSets = sets.filter((set) => set.kind === 'NORMAL');
        const selectedEmoteSetId =
          activeEmoteSetId !== ''
            ? activeEmoteSetId
            : selectableSets.length === 1
              ? selectableSets[0].id
              : '';
        this.state.set({
          status: 'ready',
          channelName,
          sets,
          activeEmoteSetId,
          selectedEmoteSetId,
          preview: selectedEmoteSetId === '' ? { status: 'none' } : { status: 'loading' },
        });
        if (selectedEmoteSetId !== '') {
          this.loadPreview(channelName, selectedEmoteSetId, false);
        }
      },
      error: (error: HttpErrorResponse) => this.state.set({ status: 'setsError', error }),
    });
  }

  private loadPreview(channelName: string, emoteSetId: string, refresh: boolean): void {
    const requestId = ++this.latestPreviewRequestId;
    this.emoteSetService.loadEmoteSetPreview(channelName, emoteSetId, { refresh }).subscribe({
      next: (response) =>
        this.applyPreviewResult(requestId, emoteSetId, { status: 'loaded', response }),
      error: (error: HttpErrorResponse) =>
        this.applyPreviewResult(requestId, emoteSetId, { status: 'error', error }),
    });
  }

  /**
   * Applies a preview response/error only if it is both the latest request issued at all *and*
   * still matches the currently selected set. The set-id check alone used to be the whole guard,
   * against a stale answer landing after the caller switched to a different set — but it let an
   * older answer for the very same set id win a race against a newer one for that same id (P3-5),
   * since two different requests can share an emoteSetId. The request id closes that gap regardless
   * of which set either request was for.
   */
  private applyPreviewResult(requestId: number, emoteSetId: string, preview: PreviewState): void {
    const current = this.state();
    if (
      requestId !== this.latestPreviewRequestId ||
      current.status !== 'ready' ||
      current.selectedEmoteSetId !== emoteSetId
    ) {
      return;
    }
    this.state.set({ ...current, preview });
  }
}
