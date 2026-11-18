import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { TranslocoPipe } from '@jsverse/transloco';
import { interval } from 'rxjs';

import { apiErrorCode, GENERIC_ERROR_TRANSLATION_KEY } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { pluralKey } from '../../core/i18n/plural';
import { SevenTvEmoteLookup } from '../../core/seven-tv/seven-tv-emote-lookup';
import { wishErrorKey } from '../../core/wishes/wish-error';
import { parseWishDurationDays, WISH_DURATION_DAYS_MAX } from '../../core/wishes/wish-input';
import type { Wish, WishAdoptableSlot, WishStatus } from '../../core/wishes/wish.model';
import { wishConflict, WishService } from '../../core/wishes/wish.service';
import { wishActions } from '../../core/wishes/wish-view';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { WishImage } from './wish-image';

const DAY_MS = 86_400_000;

/**
 * "From now" is sent this far in the past (#485, T6). The server refuses a start after its own clock
 * (`invalid_request_body`), so a browser clock a few seconds ahead would turn every "from now" into a
 * refusal; a minute is nothing against a deadline counted in days.
 */
export const ADOPT_START_CLOCK_MARGIN_MS = 60_000;

/** Without a preselection the dialog lists at most this many matches; the search narrows them. */
export const ADOPT_MAX_LISTED = 50;

/** Holders whose state the dialog looks up to name the reason (kept or running); more stay generic. */
const MAX_HOLDER_LOOKUPS = 20;

/** The answers that say "the situation is no longer the one shown" (plan T6, verbindlich). */
const STALE_CODES: ReadonlySet<string> = new Set([
  'wish_slot_not_found',
  'wish_slot_already_linked',
  'wish_already_running',
  'wish_state_conflict',
  'wish_not_found',
]);

export interface WishAdoptDialogData {
  /** Normalised. */
  channelName: string;
  /** Narrows the entries to one emote (`GET …/slots?sevenTvEmoteId=`). */
  sevenTvEmoteId?: string;
  /** Only these entries are offered (the `slotIds` of a 409); one the list no longer knows is shown, not pickable. */
  slotIds?: readonly string[];
  /** The wish to link ("Mit Eintrag verknüpfen", or the 409 of the manual entry): `adopt` sends its id. */
  wish?: Wish;
}

/** `adopted`: the wish the server answered with. `jump`: the manager asked to go to that wish. */
export type WishAdoptDialogResult =
  { kind: 'adopted'; wish: Wish } | { kind: 'jump'; wishId: number };

/**
 * What an entry of `GET …/slots` is, from its own fields alone (plan T6, Sol-R2-P2-5):
 *
 * - `free`: no wish holds it and none runs for its emote elsewhere; adopting creates a wish, or links
 *   the wish the dialog was given.
 * - `link`: a running wish of the same emote holds no slot and may take this one; adopting sends that
 *   wish's id. Only while the dialog was not given another wish.
 * - `locked`: not pickable. `held`: a wish holds this very entry. `runningElsewhere`: a wish of the
 *   emote runs and cannot take it. `otherWish`: it would go to a running wish other than the given one.
 */
export type AdoptEntryKind =
  | { kind: 'free' }
  | { kind: 'link'; wishId: number }
  | { kind: 'locked'; reason: 'held' | 'runningElsewhere' | 'otherWish'; wishId: number };

export function adoptEntryKind(
  slot: WishAdoptableSlot,
  givenWishId: number | null,
): AdoptEntryKind {
  if (slot.heldByWishId !== null) {
    return { kind: 'locked', reason: 'held', wishId: slot.heldByWishId };
  }
  const running = slot.runningWishId;
  if (running === null || running === givenWishId) {
    return { kind: 'free' };
  }
  if (!slot.runningWishAdoptable) {
    return { kind: 'locked', reason: 'runningElsewhere', wishId: running };
  }
  return givenWishId === null
    ? { kind: 'link', wishId: running }
    : { kind: 'locked', reason: 'otherWish', wishId: running };
}

export type AdoptStartChoice = 'added' | 'now';

/**
 * Where the deadline may start (F6): at the entry's add date or now. An undated entry has only "now".
 * With "from the add date" the deadline would already be over (`addedExpired`), so "now" is the default
 * then; otherwise the add date is.
 */
export function adoptStartOptions(
  addedAtUtc: string | null,
  durationDays: number | null,
  now: number,
): { canStartAtAdded: boolean; addedExpired: boolean; preferred: AdoptStartChoice } {
  const added = addedAtUtc === null ? Number.NaN : Date.parse(addedAtUtc);
  const canStartAtAdded = !Number.isNaN(added);
  const addedExpired =
    canStartAtAdded && durationDays !== null && added + durationDays * DAY_MS <= now;
  return {
    canStartAtAdded,
    addedExpired,
    preferred: canStartAtAdded && !addedExpired ? 'added' : 'now',
  };
}

/** The expiry the choice leads to, in milliseconds; `null` without a usable start or duration. */
export function adoptExpiry(
  choice: AdoptStartChoice,
  addedAtUtc: string | null,
  durationDays: number | null,
  now: number,
): number | null {
  const start = choice === 'now' ? now : addedAtUtc === null ? Number.NaN : Date.parse(addedAtUtc);
  return durationDays === null || Number.isNaN(start) ? null : start + durationDays * DAY_MS;
}

/**
 * `startsAtUtc` for the request. "From the add date" sends none (the server takes the entry's date).
 * "From now" sends now minus the clock margin, but never a time at or before the add date: the server
 * refuses a start before it (`invalid_request_body`, seen live in T0c), so in that case none is sent and
 * the server starts at the add date, which is then less than a minute ago. An undated entry sends none
 * either; the server starts it at its own now.
 */
export function adoptStartsAtUtc(
  choice: AdoptStartChoice,
  addedAtUtc: string | null,
  now: number,
): string | undefined {
  if (choice === 'added' || addedAtUtc === null) {
    return undefined;
  }
  const start = now - ADOPT_START_CLOCK_MARGIN_MS;
  const added = Date.parse(addedAtUtc);
  return Number.isNaN(added) || start <= added ? undefined : new Date(start).toISOString();
}

type EntriesState =
  { kind: 'loading' } | { kind: 'error' } | { kind: 'ready'; slots: readonly WishAdoptableSlot[] };

/** One line of the list: an entry, or a preselected id the list no longer knows. */
type ListedEntry =
  | { key: string; kind: 'entry'; slot: WishAdoptableSlot; entryKind: AdoptEntryKind }
  | { key: string; kind: 'missing'; slotId: string };

type LinkWishState =
  | { wishId: number; kind: 'loading' }
  | { wishId: number; kind: 'ready'; durationDays: number }
  | { wishId: number; kind: 'error' };

interface StaleNotice {
  code: string;
  wishId: number | null;
}

/**
 * "Bestand übernehmen" and "Mit Eintrag verknüpfen" (#485, plan T6, Sol-P2-6, Sol-R2-P2-4, Sol-R2-P2-5).
 *
 * **The entries come from `GET …/wishes/slots` and nowhere else** (narrowed to an emote when one is
 * known). Each is free, linkable to a running wish, or locked with a reason and a way to that wish
 * (`adoptEntryKind`). **What is sent is what was picked:** `slotId`, `expectedAlias` and
 * `expectedAddedAtUtc` of the chosen entry verbatim (`null` for an undated one), so a slot id reused
 * with a new date in between is refused (404) instead of adopted.
 *
 * **A stale situation is never retried:** every refusal that says the situation changed (404
 * `wish_slot_not_found`, `wish_not_found`; 409 `wish_slot_already_linked`, `wish_already_running`,
 * `wish_state_conflict`) reloads the entries, drops the choice and says what changed, with "Zum Wunsch"
 * where the body names a wish. The manager has to pick again; nothing is sent a second time on its own,
 * and no other wish id is switched to silently.
 *
 * Closes with the adopted wish, with a wish id to jump to, or with nothing.
 */
@Component({
  selector: 'app-wish-adopt-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe, WishImage],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey() | transloco">
      <p class="text-sm text-fg-secondary">
        @if (data.wish; as given) {
          {{ 'wishes.adopt.introLink' | transloco: { name: given.alias || given.emoteName } }}
        } @else {
          {{ 'wishes.adopt.intro' | transloco }}
        }
      </p>

      <!-- The stale notice appears while the dialog stays open: a permanently mounted region speaks
           it (UI-Designsprache §4.4). The visible text is aria-hidden so it is not read twice; its
           button is not, it stays reachable (axe aria-hidden-focus). -->
      <p role="status" class="sr-only">
        @if (staleText(); as text) {
          {{ text.key | transloco }} {{ 'wishes.adopt.stale.reselect' | transloco }}
        }
      </p>
      @if (staleText(); as text) {
        <app-notice-banner variant="warning">
          <span aria-hidden="true"
            >{{ text.key | transloco }} {{ 'wishes.adopt.stale.reselect' | transloco }}</span
          >
          @if (text.wishId !== null) {
            <button notice-action type="button" appButton="outline" (click)="jump(text.wishId)">
              {{ 'wishes.dialog.toWish' | transloco }}
            </button>
          }
        </app-notice-banner>
      }

      @if (disabledFeature()) {
        <app-notice-banner variant="error">{{
          'errors.api.wishes_disabled' | transloco
        }}</app-notice-banner>
      }

      @switch (entries().kind) {
        @case ('loading') {
          <!-- Skeleton in the outline of the entry list below (§6.1). -->
          <div role="status" [attr.aria-label]="'common.loading' | transloco">
            <ul class="-mx-6 divide-y divide-border border-y border-border" aria-hidden="true">
              @for (row of [0, 1]; track row) {
                <li class="flex items-start gap-3 px-6 py-2">
                  <div class="app-skeleton size-10 shrink-0"></div>
                  <div class="flex min-w-0 flex-1 flex-col gap-1.5">
                    <div class="app-skeleton h-4 w-32 max-w-full"></div>
                    <div class="app-skeleton h-3 w-24 max-w-full"></div>
                  </div>
                </li>
              }
            </ul>
          </div>
        }
        @case ('error') {
          <app-notice-banner variant="error">
            {{ 'wishes.adopt.loadFailed' | transloco }}
            <button notice-action type="button" appButton="outline" (click)="loadEntries()">
              {{ 'wishes.page.reload' | transloco }}
            </button>
          </app-notice-banner>
        }
        @case ('ready') {
          @if (showSearch()) {
            <div class="flex flex-col gap-1">
              <label for="wish-adopt-search" class="text-sm text-fg-secondary">
                {{ 'wishes.adopt.search' | transloco }}
              </label>
              <input
                id="wish-adopt-search"
                type="search"
                class="app-input"
                autocomplete="off"
                [value]="query()"
                (input)="query.set($any($event.target).value)"
              />
            </div>
          }
          @if (listed().length === 0) {
            <p class="text-sm text-fg-muted">
              {{ (query() ? 'wishes.adopt.noMatch' : 'wishes.adopt.empty') | transloco }}
            </p>
          } @else {
            <!-- min-w-0: a fieldset never shrinks below its content by default, so one unbroken long
                 alias would widen the dialog past the viewport instead of truncating. -->
            <fieldset class="flex min-w-0 flex-col">
              <legend class="mb-2 text-sm text-fg-secondary">
                {{ 'wishes.adopt.entriesLegend' | transloco }}
              </legend>
              <ul class="-mx-6 divide-y divide-border border-y border-border">
                @for (line of listed(); track line.key) {
                  <li class="flex items-start gap-3 px-6 py-2">
                    @if (line.kind === 'entry') {
                      <input
                        type="radio"
                        name="wish-adopt-entry"
                        class="mt-3 h-4 w-4 shrink-0 accent-accent-solid"
                        [id]="'wish-adopt-entry-' + line.slot.slotId"
                        [value]="line.slot.slotId"
                        [checked]="selectedSlotId() === line.slot.slotId"
                        [disabled]="line.entryKind.kind === 'locked' || saving()"
                        [attr.aria-describedby]="'wish-adopt-entry-' + line.slot.slotId + '-note'"
                        (change)="select(line.slot.slotId)"
                      />
                      <app-wish-image
                        [imageUrl]="line.slot.imageUrl"
                        [sevenTvEmoteId]="line.slot.sevenTvEmoteId"
                        [knownStill]="isKnownStill(line.slot.sevenTvEmoteId)"
                      />
                      <div class="flex min-w-0 flex-1 flex-col gap-0.5">
                        <label
                          class="truncate font-medium"
                          [for]="'wish-adopt-entry-' + line.slot.slotId"
                          >{{ line.slot.alias }}</label
                        >
                        <p
                          class="flex flex-col text-xs text-fg-muted"
                          [id]="'wish-adopt-entry-' + line.slot.slotId + '-note'"
                        >
                          <span>
                            @if (line.slot.addedAtUtc; as added) {
                              {{ 'wishes.adopt.addedOn' | transloco: { date: formatDate(added) } }}
                            } @else {
                              {{ 'wishes.adopt.addedUnknown' | transloco }}
                            }
                          </span>
                          @switch (line.entryKind.kind) {
                            @case ('link') {
                              <span class="text-fg-secondary">{{
                                'wishes.adopt.linkNote' | transloco
                              }}</span>
                            }
                            @case ('locked') {
                              <span class="text-fg-secondary">{{
                                lockedReasonKey(line.entryKind) | transloco
                              }}</span>
                            }
                          }
                        </p>
                      </div>
                      @if (line.entryKind.kind === 'locked') {
                        <button
                          type="button"
                          appButton="outline"
                          class="shrink-0"
                          [attr.aria-label]="
                            ('wishes.dialog.toWish' | transloco) + ': ' + line.slot.alias
                          "
                          (click)="jump(line.entryKind.wishId)"
                        >
                          {{ 'wishes.dialog.toWish' | transloco }}
                        </button>
                      }
                    } @else {
                      <input
                        type="radio"
                        name="wish-adopt-entry"
                        class="mt-1 h-4 w-4 shrink-0 accent-accent-solid"
                        disabled
                        [id]="'wish-adopt-entry-' + line.slotId"
                      />
                      <label
                        class="text-sm text-fg-muted"
                        [for]="'wish-adopt-entry-' + line.slotId"
                      >
                        {{ 'wishes.adopt.missing' | transloco }}
                      </label>
                    }
                  </li>
                }
              </ul>
              @if (hiddenCount() > 0) {
                <p class="mt-2 text-xs text-fg-muted">
                  {{
                    pluralKey(hiddenCount(), 'wishes.adopt.moreHidden')
                      | transloco: { count: hiddenCount() }
                  }}
                </p>
              }
            </fieldset>
          }
        }
      }

      <div class="flex flex-col gap-1">
        <label for="wish-adopt-duration" class="text-sm text-fg-secondary">
          {{ 'wishes.dialog.duration' | transloco }}
        </label>
        <input
          id="wish-adopt-duration"
          type="text"
          inputmode="numeric"
          class="app-input"
          autocomplete="off"
          [value]="duration()"
          [attr.aria-invalid]="durationError() ? 'true' : null"
          [attr.aria-describedby]="durationError() ? 'wish-adopt-duration-error' : null"
          (input)="setDuration($any($event.target).value)"
        />
        @if (durationError(); as key) {
          <p id="wish-adopt-duration-error" class="text-sm text-danger-fg">
            {{ key | transloco: { max: maxDays } }}
          </p>
        }
      </div>

      @if (selected(); as entry) {
        <fieldset class="flex flex-col gap-1">
          <legend class="mb-1 text-sm text-fg-secondary">
            {{ 'wishes.adopt.start.legend' | transloco }}
          </legend>
          @if (startOptions().canStartAtAdded) {
            <label class="flex items-center gap-2 py-1 text-sm">
              <input
                type="radio"
                name="wish-adopt-start"
                class="h-4 w-4 accent-accent-solid"
                [checked]="startChoice() === 'added'"
                [disabled]="saving()"
                (change)="chooseStart('added')"
              />
              {{ 'wishes.adopt.start.added' | transloco }}
            </label>
          }
          <label class="flex items-center gap-2 py-1 text-sm">
            <input
              type="radio"
              name="wish-adopt-start"
              class="h-4 w-4 accent-accent-solid"
              [checked]="startChoice() === 'now'"
              [disabled]="saving()"
              (change)="chooseStart('now')"
            />
            {{ 'wishes.adopt.start.now' | transloco }}
          </label>
          @if (!startOptions().canStartAtAdded) {
            <p class="text-xs text-fg-muted">{{ 'wishes.adopt.start.undatedHint' | transloco }}</p>
          } @else if (startOptions().addedExpired) {
            <p class="text-xs text-fg-muted">{{ 'wishes.adopt.start.expiredHint' | transloco }}</p>
          }
          @if (expiryText(); as date) {
            <p class="text-sm text-fg-body">
              {{ 'wishes.adopt.start.expires' | transloco: { date } }}
            </p>
          }
        </fieldset>
      }

      @if (errorKey(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      }

      @if (blockedReason(); as reason) {
        <p dialog-actions id="wish-adopt-blocked" class="mr-auto text-xs text-fg-muted">
          {{ reason | transloco }}
        </p>
      }
      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        [disabled]="saving()"
        (click)="dialogRef.close(undefined)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        [disabled]="saving() || blockedReason() !== null"
        [attr.aria-describedby]="blockedReason() ? 'wish-adopt-blocked' : null"
        (click)="submit()"
      >
        {{ submitKey() | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class WishAdoptDialog {
  protected readonly data = inject<WishAdoptDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<WishAdoptDialogResult | undefined>>(DialogRef);
  private readonly wishService = inject(WishService);
  private readonly lookup = inject(SevenTvEmoteLookup);
  private readonly languageService = inject(LanguageService);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly maxDays = WISH_DURATION_DAYS_MAX;
  protected readonly pluralKey = pluralKey;

  private readonly givenWishId = this.data.wish?.id ?? null;

  protected readonly now = signal(Date.now());
  protected readonly entries = signal<EntriesState>({ kind: 'loading' });
  private entriesSeq = 0;
  protected readonly query = signal('');
  protected readonly selectedSlotId = signal<string | null>(null);
  protected readonly duration = signal(this.data.wish ? String(this.data.wish.durationDays) : '');
  private readonly durationTouched = signal(false);
  /** The channel default, for an entry that creates a new wish; `null` until it has arrived. */
  private readonly defaultDuration = signal<number | null>(null);
  private readonly startTouched = signal<AdoptStartChoice | null>(null);
  private readonly linkWish = signal<LinkWishState | null>(null);
  /** The states of holders looked up to name a locked entry's reason. */
  private readonly holderStatus = signal<ReadonlyMap<number, WishStatus>>(new Map());
  private readonly holderAsked = new Set<number>();
  protected readonly stale = signal<StaleNotice | null>(null);
  protected readonly errorKey = signal<string | null>(null);
  private readonly durationServerError = signal(false);
  protected readonly disabledFeature = signal(false);
  protected readonly saving = signal(false);

  protected readonly titleKey = computed(() =>
    this.data.wish ? 'wishes.adopt.titleLink' : 'wishes.adopt.title',
  );

  protected readonly showSearch = computed(() => !this.data.slotIds);

  private readonly kindsBySlot = computed(() => {
    const state = this.entries();
    const kinds = new Map<string, { slot: WishAdoptableSlot; entryKind: AdoptEntryKind }>();
    if (state.kind === 'ready') {
      for (const slot of state.slots) {
        kinds.set(slot.slotId, { slot, entryKind: adoptEntryKind(slot, this.givenWishId) });
      }
    }
    return kinds;
  });

  private readonly allListed = computed<ListedEntry[]>(() => {
    const kinds = this.kindsBySlot();
    const preselected = this.data.slotIds;
    if (preselected) {
      return preselected.map((slotId) => {
        const known = kinds.get(slotId);
        return known
          ? { key: slotId, kind: 'entry', ...known }
          : { key: slotId, kind: 'missing', slotId };
      });
    }
    const query = this.query().trim().toLocaleLowerCase();
    return [...kinds.values()]
      .filter(({ slot }) => query === '' || slot.alias.toLocaleLowerCase().includes(query))
      .map((known) => ({ key: known.slot.slotId, kind: 'entry', ...known }));
  });

  protected readonly listed = computed(() => this.allListed().slice(0, ADOPT_MAX_LISTED));
  protected readonly hiddenCount = computed(() =>
    Math.max(0, this.allListed().length - ADOPT_MAX_LISTED),
  );

  /** The chosen entry, only while it is pickable. */
  protected readonly selected = computed(() => {
    const id = this.selectedSlotId();
    const known = id === null ? undefined : this.kindsBySlot().get(id);
    return known && known.entryKind.kind !== 'locked' ? known : null;
  });

  private readonly durationResult = computed(() => parseWishDurationDays(this.duration()));

  private readonly durationDays = computed(() => {
    const result = this.durationResult();
    return result.ok ? result.value : null;
  });

  protected readonly durationError = computed(() => {
    if (this.durationServerError()) {
      return 'wishes.dialog.durationInvalid.out_of_range';
    }
    const result = this.durationResult();
    return result.ok || (!this.durationTouched() && result.reason === 'empty')
      ? null
      : `wishes.dialog.durationInvalid.${result.reason}`;
  });

  protected readonly startOptions = computed(() =>
    adoptStartOptions(this.selected()?.slot.addedAtUtc ?? null, this.durationDays(), this.now()),
  );

  protected readonly startChoice = computed<AdoptStartChoice>(() => {
    const options = this.startOptions();
    const touched = this.startTouched();
    if (!options.canStartAtAdded) {
      return 'now';
    }
    return touched ?? options.preferred;
  });

  protected readonly expiryText = computed(() => {
    const expiry = adoptExpiry(
      this.startChoice(),
      this.selected()?.slot.addedAtUtc ?? null,
      this.durationDays(),
      this.now(),
    );
    return expiry === null ? null : this.formatDate(new Date(expiry).toISOString());
  });

  /** The first reason the request cannot go out yet, as a translation key; `null` when it can. */
  protected readonly blockedReason = computed(() => {
    if (this.disabledFeature()) {
      return 'wishes.dialog.blocked.disabled';
    }
    const state = this.entries();
    if (state.kind === 'loading') {
      return 'wishes.adopt.blocked.loading';
    }
    if (state.kind === 'error') {
      return 'wishes.adopt.blocked.loadFailed';
    }
    const selected = this.selected();
    if (selected === null) {
      return 'wishes.adopt.blocked.noSelection';
    }
    if (selected.entryKind.kind === 'link' && this.linkWish()?.kind === 'loading') {
      return 'wishes.adopt.blocked.linkLoading';
    }
    if (!this.durationResult().ok) {
      return 'wishes.dialog.blocked.duration';
    }
    return null;
  });

  protected readonly submitKey = computed(() =>
    this.data.wish || this.selected()?.entryKind.kind === 'link'
      ? 'wishes.adopt.submitLink'
      : 'wishes.adopt.submit',
  );

  protected readonly staleText = computed(() => {
    const stale = this.stale();
    if (stale === null) {
      return null;
    }
    let key = `wishes.adopt.stale.${stale.code}`;
    if (
      stale.code === 'wish_slot_already_linked' &&
      stale.wishId !== null &&
      this.holderStatus().get(stale.wishId) === 'kept'
    ) {
      key = 'wishes.adopt.stale.wish_slot_already_linked_kept';
    }
    return { key, wishId: stale.wishId };
  });

  constructor() {
    this.loadEntries();
    if (!this.data.wish) {
      this.wishService.getSettings(this.data.channelName).subscribe({
        next: (settings) => {
          this.defaultDuration.set(settings.defaultDurationDays);
          // A linked wish brings its own duration; the default is for an entry that creates one.
          if (this.selected()?.entryKind.kind !== 'link') {
            this.prefillDuration(settings.defaultDurationDays);
          }
        },
        // Without the default the field stays empty and asks for a value.
        error: () => undefined,
      });
    }
    interval(60_000)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.now.set(Date.now()));
  }

  protected loadEntries(): void {
    const seq = ++this.entriesSeq;
    this.entries.set({ kind: 'loading' });
    this.wishService.listSlots(this.data.channelName, this.data.sevenTvEmoteId).subscribe({
      next: (slots) => {
        if (seq !== this.entriesSeq) {
          return;
        }
        this.entries.set({ kind: 'ready', slots });
        this.lookUpHolders(slots);
      },
      error: (error: unknown) => {
        if (seq !== this.entriesSeq) {
          return;
        }
        if (error instanceof HttpErrorResponse && apiErrorCode(error) === 'wishes_disabled') {
          this.disabledFeature.set(true);
        }
        this.entries.set({ kind: 'error' });
      },
    });
  }

  protected select(slotId: string): void {
    const known = this.kindsBySlot().get(slotId);
    if (!known || known.entryKind.kind === 'locked' || this.saving()) {
      return;
    }
    this.selectedSlotId.set(slotId);
    this.startTouched.set(null);
    this.errorKey.set(null);
    if (known.entryKind.kind === 'link') {
      this.loadLinkWish(known.entryKind.wishId);
    } else {
      this.linkWish.set(null);
      const fallback = this.data.wish?.durationDays ?? this.defaultDuration();
      if (fallback !== null) {
        this.prefillDuration(fallback);
      }
    }
  }

  protected chooseStart(choice: AdoptStartChoice): void {
    this.startTouched.set(choice);
  }

  protected setDuration(value: string): void {
    this.durationTouched.set(true);
    this.durationServerError.set(false);
    this.duration.set(value);
  }

  protected jump(wishId: number): void {
    if (!this.saving()) {
      this.dialogRef.close({ kind: 'jump', wishId });
    }
  }

  protected lockedReasonKey(kind: AdoptEntryKind): string {
    if (kind.kind !== 'locked') {
      return '';
    }
    if (kind.reason !== 'held') {
      return `wishes.adopt.locked.${kind.reason}`;
    }
    const status = this.holderStatus().get(kind.wishId);
    if (status === 'kept') {
      return 'wishes.adopt.locked.kept';
    }
    return status === 'active' || status === 'presented'
      ? 'wishes.adopt.locked.heldRunning'
      : 'wishes.adopt.locked.held';
  }

  protected isKnownStill(sevenTvEmoteId: string): boolean {
    return this.lookup.isKnownStill(sevenTvEmoteId);
  }

  protected formatDate(iso: string): string {
    return new Date(iso).toLocaleDateString(toLocale(this.languageService.lang()), {
      dateStyle: 'medium',
    });
  }

  protected submit(): void {
    // The button is disabled while blocked; a second click while the request is out lands here too.
    if (this.saving() || this.blockedReason() !== null) {
      return;
    }
    const selected = this.selected();
    const durationDays = this.durationDays();
    if (selected === null || durationDays === null) {
      return;
    }
    const { slot, entryKind } = selected;
    const wishId = entryKind.kind === 'link' ? entryKind.wishId : this.givenWishId;
    const startsAtUtc = adoptStartsAtUtc(this.startChoice(), slot.addedAtUtc, Date.now());
    this.errorKey.set(null);
    this.stale.set(null);
    this.saving.set(true);
    // Not dismissible while the request is out: the page would never hear of a wish adopted after all.
    this.dialogRef.disableClose = true;
    this.wishService
      .adopt(this.data.channelName, {
        slotId: slot.slotId,
        expectedAlias: slot.alias,
        expectedAddedAtUtc: slot.addedAtUtc,
        ...(wishId === null ? {} : { wishId }),
        durationDays,
        ...(startsAtUtc === undefined ? {} : { startsAtUtc }),
      })
      .subscribe({
        next: (wish) => this.dialogRef.close({ kind: 'adopted', wish }),
        error: (error: unknown) => this.onError(error, wishId),
      });
  }

  private onError(error: unknown, sentWishId: number | null): void {
    this.saving.set(false);
    this.dialogRef.disableClose = false;
    if (!(error instanceof HttpErrorResponse)) {
      this.errorKey.set(GENERIC_ERROR_TRANSLATION_KEY);
      return;
    }
    const code = apiErrorCode(error);
    if (code !== undefined && STALE_CODES.has(code)) {
      const named = wishConflict(error).wishId ?? null;
      const wishId = named ?? (code === 'wish_state_conflict' ? sentWishId : null);
      this.stale.set({ code, wishId });
      if (wishId !== null) {
        this.lookUpHolder(wishId, true);
      }
      this.selectedSlotId.set(null);
      this.linkWish.set(null);
      this.loadEntries();
      return;
    }
    if (code === 'wishes_disabled') {
      this.disabledFeature.set(true);
      return;
    }
    if (code === 'wish_duration_out_of_range') {
      this.durationServerError.set(true);
      return;
    }
    this.errorKey.set(wishErrorKey(error, 'adopt'));
  }

  private loadLinkWish(wishId: number): void {
    this.linkWish.set({ wishId, kind: 'loading' });
    const current = () => this.linkWish()?.wishId === wishId && this.selected() !== null;
    this.wishService.get(this.data.channelName, wishId).subscribe({
      next: (wish) => {
        if (!current()) {
          return;
        }
        if (!wishActions(wish).includes('adopt')) {
          // The running wish took a slot or ended in the meantime: the list no longer tells the truth.
          this.stale.set({ code: 'wish_state_conflict', wishId });
          this.selectedSlotId.set(null);
          this.linkWish.set(null);
          this.loadEntries();
          return;
        }
        this.linkWish.set({ wishId, kind: 'ready', durationDays: wish.durationDays });
        this.prefillDuration(wish.durationDays);
      },
      // Without it the field keeps its value; the server checks the link when it is sent.
      error: () => {
        if (current()) {
          this.linkWish.set({ wishId, kind: 'error' });
        }
      },
    });
  }

  private prefillDuration(days: number): void {
    if (!this.durationTouched()) {
      this.duration.set(String(days));
    }
  }

  private lookUpHolders(slots: readonly WishAdoptableSlot[]): void {
    for (const slot of slots) {
      if (slot.heldByWishId !== null) {
        this.lookUpHolder(slot.heldByWishId, false);
      }
    }
  }

  /** Learns whether a holder is kept or running; `fresh` asks again after a refusal named it. */
  private lookUpHolder(wishId: number, fresh: boolean): void {
    if (!fresh && (this.holderAsked.has(wishId) || this.holderAsked.size >= MAX_HOLDER_LOOKUPS)) {
      return;
    }
    this.holderAsked.add(wishId);
    this.wishService.get(this.data.channelName, wishId).subscribe({
      next: (wish) => this.holderStatus.update((map) => new Map(map).set(wishId, wish.status)),
      // The generic reason stays.
      error: () => undefined,
    });
  }
}

export function openWishAdoptDialog(
  dialog: Dialog,
  data: WishAdoptDialogData,
): DialogRef<WishAdoptDialogResult | undefined> {
  return openAppDialog<WishAdoptDialogResult | undefined, WishAdoptDialogData>(
    dialog,
    WishAdoptDialog,
    { data },
  );
}
