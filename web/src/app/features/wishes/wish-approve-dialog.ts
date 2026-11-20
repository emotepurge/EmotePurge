import { DIALOG_DATA, Dialog, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, DestroyRef, inject, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';
import { Subscription } from 'rxjs';

import { apiErrorCode, GENERIC_ERROR_TRANSLATION_KEY } from '../../core/i18n/api-error';
import {
  canonicalSevenTvEmoteId,
  SevenTvEmoteLookup,
  SevenTvEmoteLookupResult,
} from '../../core/seven-tv/seven-tv-emote-lookup';
import { WishEmotePreviewService } from '../../core/wishes/wish-emote-preview.service';
import { WISH_EMOTE_IDS_INVALID_KEY, wishErrorKey } from '../../core/wishes/wish-error';
import {
  parseEmoteInput,
  parseWishDurationDays,
  validateWishAlias,
  WISH_ALIAS_MAX_LENGTH,
  WISH_DURATION_DAYS_MAX,
} from '../../core/wishes/wish-input';
import type { ApproveWishBody, Wish } from '../../core/wishes/wish.model';
import { wishConflict, WishService } from '../../core/wishes/wish.service';
import { approveExtendsRunning, hasEmote } from '../../core/wishes/wish-view';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { WishImage } from './wish-image';
import { WISH_ENTRY_LOOKUP_DEBOUNCE_MS, WishEntryReadState } from './wish-entry-dialog';

/** The refusals of an approve that are about the emote sent in the field, not about the other fields. */
const EMOTE_BOUND_CODES: ReadonlySet<string> = new Set([
  'wish_emote_already_in_set',
  'wish_running_not_extendable',
  'emote_ids_invalid',
  'wish_emote_required',
]);

export interface WishApproveDialogData {
  /** Normalised. */
  channelName: string;
  /** The open (`pending`) wish as the row showed it. */
  wish: Wish;
}

/**
 * How the dialog closed:
 * - `approved`: the wish is `approved`; "In 7TV hinzufügen" now stands at its row (two clicks, F3).
 * - `merged`: a wish for the emote was already running, the approve booked its extension and this
 *   wish ended as `merged` (plan 2.6 (1)). `extendedExpiresAtUtc` is the running wish's new expiry.
 * - `adopt`: the emote is already in the set (409 `wish_emote_already_in_set`); the manager takes the
 *   entry over for this wish instead.
 * - `jump`: the manager wants to go to the running wish that could not be extended.
 * - `stale`: the wish is no longer open (409 `wish_state_conflict`, 404 `wish_not_found`); the page
 *   shows the text at the section and reloads.
 * `undefined`: dismissed.
 */
export type WishApproveDialogResult =
  | { kind: 'approved'; wish: Wish }
  | {
      kind: 'merged';
      wish: Wish;
      runningWishId: number | null;
      extendedExpiresAtUtc: string | null;
    }
  | { kind: 'adopt'; sevenTvEmoteId: string; slotIds: string[] }
  | { kind: 'jump'; wishId: number }
  | { kind: 'stale'; errorKey: string };

/** What the dialog says after a 409 that leaves the wish open. */
type ApproveConflict =
  | { kind: 'inSet'; sevenTvEmoteId: string; slotIds: string[] }
  | { kind: 'notExtendable'; wishId: number | null };

/**
 * "Freigeben" of an open wish (#486, plan 2.10, AK59). Shows what the viewer redeemed and who, then
 * the emote, its alias and the duration.
 *
 * **The emote.** A wish that knows its emote shows it (picture and 7TV's default name, read once per
 * session through `WishEmotePreviewService`); one that does not gets a field for a 7TV link or id.
 * Either way the read and the fields hang on the one current id exactly as in "Nachtragen"
 * (Sol-P2-10): a read counts only for the id it ran for, the alias it filled in goes when the id
 * changes, an alias the manager typed stays, and sending needs the read `found` or `unreachable`.
 * Without an emote sending is blocked with that reason, so the server's `wish_emote_required` cannot
 * arise from this dialog.
 *
 * **The alias** starts with the wish's own (a PATCH), else the name the viewer typed, else 7TV's
 * default name; the duration with the wish's.
 *
 * **What the answer says.** 200 closes with `approved`, or with `merged` when the server booked an
 * extension of the running wish instead. 409 `wish_emote_already_in_set` offers "Bestand übernehmen"
 * for this wish. 409 `wish_running_not_extendable` says the running wish is not in the set yet: the
 * dialog stays and the wish stays open, decidable later (Sol-R2-P2-7). A wish that moved on meanwhile
 * (another manager, the reconcile) closes the dialog with `stale`.
 */
@Component({
  selector: 'app-wish-approve-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe, WishImage],
  template: `
    <app-dialog-shell [dialogTitle]="titleKey | transloco">
      <p class="text-sm text-fg-secondary">{{ introKey | transloco }}</p>

      @if (data.wish.requestText || data.wish.requesterLogin || data.wish.rewardLabel) {
        <div class="flex flex-col gap-1 border-l-2 border-border-strong pl-3">
          @if (data.wish.requestText; as text) {
            <p class="text-sm break-words text-fg-body">
              <span class="sr-only">{{ 'wishes.pending.textLabel' | transloco }}</span>
              <q>{{ text }}</q>
            </p>
          }
          <p class="flex flex-wrap gap-x-3 text-xs text-fg-muted">
            @if (data.wish.requesterLogin; as login) {
              <span>{{ 'wishes.row.requester' | transloco: { login } }}</span>
            }
            @if (data.wish.rewardLabel; as label) {
              <span>{{ 'wishes.pending.reward' | transloco: { label } }}</span>
            }
          </p>
        </div>
      }

      @if (fixedId === null) {
        <div class="flex flex-col gap-1">
          <label for="wish-approve-emote" class="text-sm text-fg-secondary">
            {{ 'wishes.entry.emote.label' | transloco }}
          </label>
          <input
            id="wish-approve-emote"
            type="text"
            class="app-input"
            autocomplete="off"
            spellcheck="false"
            [value]="emoteRaw()"
            [attr.aria-invalid]="emoteError() ? 'true' : null"
            [attr.aria-describedby]="
              emoteError() ? 'wish-approve-emote-error wish-approve-read' : 'wish-approve-read'
            "
            (input)="onEmoteInput($any($event.target).value)"
            (blur)="emoteTouched.set(true)"
          />
          @if (emoteError(); as key) {
            <p id="wish-approve-emote-error" class="text-sm text-danger-fg">
              {{ key | transloco }}
            </p>
          }
        </div>
      }
      <!-- Always mounted: a read that starts or ends is a change inside this region and is spoken. -->
      <div id="wish-approve-read" role="status" class="flex items-center gap-3 text-sm">
        @switch (currentRead().kind) {
          @case ('running') {
            <span class="text-fg-muted">{{ 'wishes.entry.read.running' | transloco }}</span>
          }
          @case ('found') {
            @if (found(); as hit) {
              <app-wish-image
                [imageUrl]="fixedId !== null ? data.wish.imageUrl : null"
                [sevenTvEmoteId]="hit.id"
                [knownStill]="isKnownStill(hit.id)"
              />
              <span class="min-w-0 break-words text-fg-body">{{
                'wishes.entry.read.found' | transloco: { name: hit.name || hit.id }
              }}</span>
            }
          }
          @case ('unknown') {
            @if (fixedId !== null) {
              <span class="text-danger-fg">{{ 'wishes.entry.read.unknown' | transloco }}</span>
            }
          }
          @case ('unreachable') {
            <span class="text-fg-secondary">{{ unreachableKey() | transloco }}</span>
          }
        }
      </div>

      <div class="flex flex-col gap-1">
        <label for="wish-approve-alias" class="text-sm text-fg-secondary">
          {{ 'wishes.entry.alias.label' | transloco }}
        </label>
        <input
          id="wish-approve-alias"
          type="text"
          class="app-input"
          autocomplete="off"
          spellcheck="false"
          [value]="alias()"
          [attr.aria-invalid]="aliasError() ? 'true' : null"
          [attr.aria-describedby]="aliasError() ? 'wish-approve-alias-error' : null"
          (input)="setAlias($any($event.target).value)"
          (blur)="aliasTouched.set(true)"
        />
        @if (aliasError(); as key) {
          <p id="wish-approve-alias-error" class="text-sm text-danger-fg">
            {{ key | transloco: { max: aliasMax } }}
          </p>
        }
      </div>

      <div class="flex flex-col gap-1">
        <label for="wish-approve-duration" class="text-sm text-fg-secondary">
          {{ 'wishes.dialog.duration' | transloco }}
        </label>
        <input
          id="wish-approve-duration"
          type="text"
          inputmode="numeric"
          class="app-input"
          autocomplete="off"
          [value]="duration()"
          [attr.aria-invalid]="durationError() ? 'true' : null"
          [attr.aria-describedby]="durationError() ? 'wish-approve-duration-error' : null"
          (input)="setDuration($any($event.target).value)"
        />
        @if (durationError(); as key) {
          <p id="wish-approve-duration-error" class="text-sm text-danger-fg">
            {{ key | transloco: { max: durationMax } }}
          </p>
        }
      </div>

      <!-- The conflict notice arrives while the dialog stays open: the permanently mounted region
           speaks it (§4.4); the visible text is aria-hidden, its buttons are not. -->
      <p role="status" class="sr-only">
        @if (conflictKey(); as key) {
          {{ key | transloco }}
        }
      </p>
      @if (conflict(); as notice) {
        @if (conflictKey(); as key) {
          <app-notice-banner variant="info">
            <span aria-hidden="true">{{ key | transloco }}</span>
            <span notice-action class="flex flex-wrap gap-2">
              @switch (notice.kind) {
                @case ('inSet') {
                  <button
                    type="button"
                    appButton="outline"
                    [disabled]="saving()"
                    (click)="takeOver()"
                  >
                    {{ 'wishes.entry.conflict.takeOver' | transloco }}
                  </button>
                }
                @case ('notExtendable') {
                  @if (notice.wishId !== null) {
                    <button
                      type="button"
                      appButton="outline"
                      [disabled]="saving()"
                      (click)="jump(notice.wishId)"
                    >
                      {{ 'wishes.dialog.toWish' | transloco }}
                    </button>
                  }
                }
              }
            </span>
          </app-notice-banner>
        }
      }

      @if (disabledFeature()) {
        <app-notice-banner variant="error">{{
          'errors.api.wishes_disabled' | transloco
        }}</app-notice-banner>
      } @else if (errorKey(); as key) {
        <app-notice-banner variant="error">{{ key | transloco }}</app-notice-banner>
      }

      @if (blockedReason(); as reason) {
        <p dialog-actions id="wish-approve-blocked" class="mr-auto text-xs text-fg-muted">
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
        [attr.aria-describedby]="blockedReason() ? 'wish-approve-blocked' : null"
        (click)="submit()"
      >
        {{ submitKey | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class WishApproveDialog {
  protected readonly data = inject<WishApproveDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<WishApproveDialogResult | undefined>>(DialogRef);
  private readonly wishService = inject(WishService);
  private readonly lookup = inject(SevenTvEmoteLookup);
  private readonly preview = inject(WishEmotePreviewService);

  protected readonly aliasMax = WISH_ALIAS_MAX_LENGTH;
  protected readonly durationMax = WISH_DURATION_DAYS_MAX;

  /** The wish's own emote, canonical; `null` while the wish does not know one (the field asks). */
  protected readonly fixedId = hasEmote(this.data.wish)
    ? canonicalSevenTvEmoteId(this.data.wish.sevenTvEmoteId)
    : null;

  private readonly extendsRunning = approveExtendsRunning(this.data.wish);
  protected readonly titleKey = this.extendsRunning
    ? 'wishes.approve.titleExtend'
    : 'wishes.approve.title';
  protected readonly introKey = this.extendsRunning
    ? 'wishes.approve.introExtend'
    : 'wishes.approve.intro';
  protected readonly submitKey = this.extendsRunning
    ? 'wishes.actions.approveExtend'
    : 'wishes.actions.approve';

  protected readonly emoteRaw = signal('');
  protected readonly emoteTouched = signal(false);
  /** The field's read; only used while the wish has no emote of its own. */
  private readonly fieldRead = signal<WishEntryReadState>({ kind: 'none' });
  private readSeq = 0;
  private readTimer: ReturnType<typeof setTimeout> | null = null;
  private readSubscription: Subscription | null = null;
  private lastId: string | null = null;

  /** What the manager typed into the alias; `null` while the default stands. */
  private readonly aliasTyped = signal<string | null>(null);
  protected readonly aliasTouched = signal(false);
  protected readonly duration = signal(String(this.data.wish.durationDays));

  private readonly serverFieldError = signal<{
    field: 'emote' | 'alias' | 'duration';
    key: string;
  } | null>(null);
  protected readonly conflict = signal<ApproveConflict | null>(null);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly disabledFeature = signal(false);
  protected readonly saving = signal(false);

  private readonly parsed = computed(() => parseEmoteInput(this.emoteRaw()));

  /** The one current emote id, canonical: the wish's own, else what the field holds (or `null`). */
  protected readonly currentId = computed(() => {
    if (this.fixedId !== null) {
      return this.fixedId;
    }
    const parsed = this.parsed();
    return parsed.ok ? canonicalSevenTvEmoteId(parsed.id) : null;
  });

  /** The read state, but only as far as it belongs to the current id. */
  protected readonly currentRead = computed<WishEntryReadState>(() => {
    if (this.fixedId !== null) {
      return this.fixedRead(this.fixedId);
    }
    const read = this.fieldRead();
    return read.kind !== 'none' && read.id === this.currentId() ? read : { kind: 'none' };
  });

  protected readonly found = computed(() => {
    const read = this.currentRead();
    return read.kind === 'found' ? read : null;
  });

  protected readonly unreachableKey = computed(() => {
    const read = this.currentRead();
    return read.kind === 'unreachable' && read.throttled
      ? 'wishes.entry.read.throttled'
      : 'wishes.entry.read.unreachable';
  });

  /** The alias the dialog proposes: the wish's own, the viewer's typed name, else 7TV's default name. */
  private readonly defaultAlias = computed(
    () => this.data.wish.alias || this.data.wish.requestedName || this.found()?.name || '',
  );

  protected readonly alias = computed(() => this.aliasTyped() ?? this.defaultAlias());

  private readonly aliasResult = computed(() => validateWishAlias(this.alias()));
  private readonly durationResult = computed(() => parseWishDurationDays(this.duration()));

  protected readonly emoteError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'emote') {
      return server.key;
    }
    if (this.fixedId !== null) {
      return null;
    }
    if (this.currentRead().kind === 'unknown') {
      return 'wishes.entry.read.unknown';
    }
    const parsed = this.parsed();
    if (parsed.ok || !this.emoteTouched()) {
      return null;
    }
    return parsed.reason === 'empty' ? 'wishes.entry.emote.empty' : WISH_EMOTE_IDS_INVALID_KEY;
  });

  protected readonly aliasError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'alias') {
      return server.key;
    }
    const result = this.aliasResult();
    return !result.ok && (this.aliasTouched() || this.aliasTyped() !== null)
      ? `wishes.entry.alias.${result.reason}`
      : null;
  });

  protected readonly durationError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'duration') {
      return server.key;
    }
    const result = this.durationResult();
    return result.ok ? null : `wishes.dialog.durationInvalid.${result.reason}`;
  });

  /** The first reason the wish cannot be approved yet, as a translation key; `null` when it can. */
  protected readonly blockedReason = computed(() => {
    if (this.disabledFeature()) {
      return 'wishes.dialog.blocked.disabled';
    }
    if (this.fixedId === null) {
      const parsed = this.parsed();
      if (!parsed.ok) {
        return parsed.reason === 'empty'
          ? 'wishes.approve.blocked.emoteMissing'
          : 'wishes.entry.blocked.emoteInvalid';
      }
    }
    const read = this.currentRead();
    if (read.kind === 'running' || read.kind === 'none') {
      return 'wishes.entry.blocked.reading';
    }
    if (read.kind === 'unknown') {
      return 'wishes.entry.blocked.unknown';
    }
    if (!this.aliasResult().ok) {
      return 'wishes.entry.blocked.alias';
    }
    if (!this.durationResult().ok) {
      return 'wishes.dialog.blocked.duration';
    }
    return null;
  });

  protected readonly conflictKey = computed(() => {
    const conflict = this.conflict();
    return conflict === null ? null : `wishes.approve.conflict.${conflict.kind}`;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.cancelRead();
      // A closed dialog no longer waits for its emote; a row in view may still (per-consumer interest).
      if (this.fixedId !== null) {
        this.preview.withdraw(this.fixedId, this);
      }
    });
    if (this.fixedId !== null) {
      this.preview.request(this.fixedId, this);
    }
  }

  protected isKnownStill(id: string): boolean {
    return this.lookup.isKnownStill(id);
  }

  /** The emote field changed: a different current id invalidates the read and its alias at once. */
  protected onEmoteInput(raw: string): void {
    this.emoteRaw.set(raw);
    const id = this.currentId();
    if (id === this.lastId) {
      return;
    }
    this.lastId = id;
    this.cancelRead();
    this.conflict.set(null);
    this.errorKey.set(null);
    if (this.serverFieldError()?.field === 'emote') {
      this.serverFieldError.set(null);
    }
    if (id === null) {
      this.fieldRead.set({ kind: 'none' });
      return;
    }
    this.fieldRead.set({ kind: 'running', id });
    const seq = ++this.readSeq;
    this.readTimer = setTimeout(() => {
      this.readTimer = null;
      this.readSubscription = this.lookup
        .lookup(id)
        .subscribe((result) => this.onReadResult(seq, result));
    }, WISH_ENTRY_LOOKUP_DEBOUNCE_MS);
  }

  protected setAlias(value: string): void {
    this.aliasTyped.set(value);
    this.aliasTouched.set(true);
    this.clearServerError('alias');
  }

  protected setDuration(value: string): void {
    this.duration.set(value);
    this.clearServerError('duration');
  }

  protected submit(): void {
    // The button is disabled while blocked; Enter paths and a second click land here and check again.
    if (this.saving() || this.blockedReason() !== null) {
      return;
    }
    const id = this.currentId();
    const alias = this.aliasResult();
    const duration = this.durationResult();
    if (id === null || !alias.ok || !duration.ok) {
      return;
    }
    const name = this.found()?.name ?? '';
    const body: ApproveWishBody = {
      ...(this.fixedId === null ? { sevenTvEmoteId: id } : {}),
      // The name sent is always the one from the read of the id sent; the wish's own wins.
      ...(this.data.wish.emoteName === '' && name !== '' ? { emoteName: name } : {}),
      alias: alias.alias,
      durationDays: duration.value,
    };
    this.errorKey.set(null);
    this.conflict.set(null);
    this.saving.set(true);
    // Not dismissible while the request is out: the page would never hear of an approve made after all.
    this.dialogRef.disableClose = true;
    this.wishService.approve(this.data.channelName, this.data.wish.id, body).subscribe({
      next: (answer) => {
        const { extendedExpiresAtUtc, ...wish } = answer;
        this.dialogRef.close(
          wish.status === 'merged' || extendedExpiresAtUtc
            ? {
                kind: 'merged',
                wish,
                runningWishId: wish.mergedIntoWishId ?? null,
                extendedExpiresAtUtc: extendedExpiresAtUtc ?? null,
              }
            : { kind: 'approved', wish },
        );
      },
      error: (error: unknown) => this.onApproveError(error, id),
    });
  }

  protected takeOver(): void {
    const conflict = this.conflict();
    if (conflict?.kind === 'inSet' && !this.saving()) {
      this.dialogRef.close({
        kind: 'adopt',
        sevenTvEmoteId: conflict.sevenTvEmoteId,
        slotIds: conflict.slotIds,
      });
    }
  }

  protected jump(wishId: number): void {
    if (!this.saving()) {
      this.dialogRef.close({ kind: 'jump', wishId });
    }
  }

  /** The wish's own emote: its read is the session preview's (one read per emote and session). */
  private fixedRead(id: string): WishEntryReadState {
    const preview = this.preview.previewOf(id);
    if (preview === null) {
      return this.preview.stopped()
        ? { kind: 'unreachable', id, throttled: true }
        : { kind: 'running', id };
    }
    switch (preview.kind) {
      case 'found':
        return { kind: 'found', id, name: preview.name, animated: null };
      case 'unknown':
        return { kind: 'unknown', id };
      default:
        return { kind: 'unreachable', id, throttled: false };
    }
  }

  private onReadResult(seq: number, result: SevenTvEmoteLookupResult): void {
    // Only the latest request, and only for the id still in the field (Sol-P2-10).
    if (seq !== this.readSeq || result.id !== this.currentId()) {
      return;
    }
    this.readSubscription = null;
    switch (result.kind) {
      case 'found':
        this.fieldRead.set({
          kind: 'found',
          id: result.id,
          name: result.name,
          animated: result.animated,
        });
        return;
      case 'unknown':
        this.fieldRead.set({ kind: 'unknown', id: result.id });
        return;
      default:
        this.fieldRead.set({
          kind: 'unreachable',
          id: result.id,
          throttled: result.kind === 'throttled',
        });
    }
  }

  private cancelRead(): void {
    if (this.readTimer !== null) {
      clearTimeout(this.readTimer);
      this.readTimer = null;
    }
    this.readSubscription?.unsubscribe();
    this.readSubscription = null;
  }

  private clearServerError(field: 'emote' | 'alias' | 'duration'): void {
    if (this.serverFieldError()?.field === field) {
      this.serverFieldError.set(null);
    }
  }

  private onApproveError(error: unknown, sentId: string): void {
    this.saving.set(false);
    this.dialogRef.disableClose = false;
    if (!(error instanceof HttpErrorResponse)) {
      this.errorKey.set(GENERIC_ERROR_TRANSLATION_KEY);
      return;
    }
    const code = apiErrorCode(error);
    // The current-id rule holds for the answer too: what it says about the sent emote counts only
    // while the field still shows that emote; nothing was written either way.
    if (EMOTE_BOUND_CODES.has(code ?? '') && sentId !== this.currentId()) {
      return;
    }
    switch (code) {
      case 'wish_emote_already_in_set':
        this.conflict.set({
          kind: 'inSet',
          sevenTvEmoteId: sentId,
          slotIds: wishConflict(error).slotIds ?? [],
        });
        return;
      case 'wish_running_not_extendable':
        this.conflict.set({ kind: 'notExtendable', wishId: wishConflict(error).wishId ?? null });
        return;
      case 'wish_state_conflict':
      case 'wish_not_found':
        this.dialogRef.close({ kind: 'stale', errorKey: wishErrorKey(error, 'action') });
        return;
      case 'emote_ids_invalid':
        this.serverFieldError.set({ field: 'emote', key: WISH_EMOTE_IDS_INVALID_KEY });
        return;
      case 'wish_emote_required':
        this.serverFieldError.set({ field: 'emote', key: 'wishes.entry.emote.empty' });
        return;
      case 'wish_alias_invalid':
        this.serverFieldError.set({ field: 'alias', key: 'errors.api.wish_alias_invalid' });
        return;
      case 'wish_duration_out_of_range':
        this.serverFieldError.set({
          field: 'duration',
          key: 'wishes.dialog.durationInvalid.out_of_range',
        });
        return;
      case 'wishes_disabled':
        this.disabledFeature.set(true);
        return;
      default:
        this.errorKey.set(wishErrorKey(error, 'action'));
    }
  }
}

export function openWishApproveDialog(
  dialog: Dialog,
  data: WishApproveDialogData,
): DialogRef<WishApproveDialogResult | undefined> {
  return openAppDialog<WishApproveDialogResult | undefined, WishApproveDialogData>(
    dialog,
    WishApproveDialog,
    { data },
  );
}
