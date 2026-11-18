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
import { WISH_EMOTE_IDS_INVALID_KEY, wishErrorKey } from '../../core/wishes/wish-error';
import {
  normalizeRequesterLogin,
  parseEmoteInput,
  parseWishDurationDays,
  validateWishAlias,
  WISH_ALIAS_MAX_LENGTH,
  WISH_DURATION_DAYS_MAX,
} from '../../core/wishes/wish-input';
import type { CreateWishBody, Wish } from '../../core/wishes/wish.model';
import { wishConflict, WishService } from '../../core/wishes/wish.service';
import { wishActions } from '../../core/wishes/wish-view';
import { Button } from '../../shared/ui/button';
import { openAppDialog } from '../../shared/ui/dialog';
import { DialogShell } from '../../shared/ui/dialog-shell';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { WishImage } from './wish-image';

/** Typing settles this long before the 7TV read goes out; the read state flips at once regardless. */
export const WISH_ENTRY_LOOKUP_DEBOUNCE_MS = 300;

export interface WishEntryDialogData {
  /** Normalised. */
  channelName: string;
}

/**
 * How the dialog closed: a wish was filed (with the set-full hint), the manager wants to go to a wish,
 * to take the emote over from the set instead (the 409 of plan 2.8 a, outcomes 1 and 3), or extended
 * the wish that already runs. `undefined`: dismissed.
 */
export type WishEntryDialogResult =
  | { kind: 'created'; wish: Wish; setFull: boolean }
  | { kind: 'jump'; wishId: number }
  | { kind: 'adopt'; sevenTvEmoteId: string; slotIds: string[]; wish: Wish | null }
  | { kind: 'extended'; wish: Wish };

/**
 * The 7TV read of the emote field, always tied to the id it ran for (Sol-P2-10). `none` while the field
 * holds no valid id.
 */
export type WishEntryReadState =
  | { kind: 'none' }
  | { kind: 'running'; id: string }
  | { kind: 'found'; id: string; name: string; animated: boolean | null }
  | { kind: 'unknown'; id: string }
  | { kind: 'unreachable'; id: string; throttled: boolean };

/** What the dialog says after a 409 (plan 2.8 a), each with its way on. */
type EntryConflict =
  | { kind: 'resolving' }
  | { kind: 'adopt'; sevenTvEmoteId: string; slotIds: string[]; wish: Wish | null }
  | { kind: 'running'; wish: Wish; canExtend: boolean }
  | { kind: 'kept'; wishId: number }
  | { kind: 'other'; wishId: number };

/**
 * "Nachtragen" (#485, plan T6, AK33): a wish filed by hand from a 7TV link or id.
 *
 * **The read and the fields hang on the current emote id (Sol-P2-10, verbindlich).** The dialog holds
 * exactly one current id (what the parser makes of the field, in its canonical form) and a read state
 * that belongs to an id. When the current id changes, also to "invalid", the read state flips at once,
 * not after the debounce, to `none` or `running`, the preview goes and an alias the read had filled in
 * is removed; an alias the manager typed stays. A read result counts only while its id is still the
 * current one and its request the latest; anything else is dropped. Sending needs `found` or
 * `unreachable` for the current id: `running` and `unknown` block it with a visible reason; with 7TV
 * unreachable the alias is typed by hand and `emoteName` is left out. The name sent is always the one
 * from the read of the id sent.
 *
 * **A 409 is decided by the wish id in its body, never by looking through loaded lists (plan 2.8 a).**
 * `wish_emote_already_in_set` without a wish: take the reported entries over. With a wish that holds a
 * slot: "already running", with "Verlängern" where it can be extended and the way to the wish. With a
 * wish that has no slot yet and may take one: take over with that wish. A kept wish: said so, without
 * "Verlängern". `wish_already_running` (the emote is not in the set) names its wish the same way.
 */
@Component({
  selector: 'app-wish-entry-dialog',
  imports: [Button, DialogShell, NoticeBanner, TranslocoPipe, WishImage],
  template: `
    <app-dialog-shell [dialogTitle]="'wishes.entry.title' | transloco">
      <p class="text-sm text-fg-secondary">{{ 'wishes.entry.intro' | transloco }}</p>

      <div class="flex flex-col gap-1">
        <label for="wish-entry-emote" class="text-sm text-fg-secondary">
          {{ 'wishes.entry.emote.label' | transloco }}
        </label>
        <input
          id="wish-entry-emote"
          type="text"
          class="app-input"
          autocomplete="off"
          spellcheck="false"
          [value]="emoteRaw()"
          [attr.aria-invalid]="emoteError() ? 'true' : null"
          [attr.aria-describedby]="
            emoteError() ? 'wish-entry-emote-error wish-entry-read' : 'wish-entry-read'
          "
          (input)="onEmoteInput($any($event.target).value)"
          (blur)="emoteTouched.set(true)"
        />
        @if (emoteError(); as key) {
          <p id="wish-entry-emote-error" class="text-sm text-danger-fg">{{ key | transloco }}</p>
        }
        <!-- Always mounted: a read that starts or ends is a change inside this region and is spoken. -->
        <div id="wish-entry-read" role="status" class="flex items-center gap-3 text-sm">
          @switch (currentRead().kind) {
            @case ('running') {
              <span class="text-fg-muted">{{ 'wishes.entry.read.running' | transloco }}</span>
            }
            @case ('found') {
              @if (found(); as hit) {
                <app-wish-image
                  [imageUrl]="null"
                  [sevenTvEmoteId]="hit.id"
                  [knownStill]="hit.animated === false"
                />
                <span class="text-fg-body">{{
                  'wishes.entry.read.found' | transloco: { name: hit.name || hit.id }
                }}</span>
              }
            }
            @case ('unreachable') {
              <span class="text-fg-secondary">{{ unreachableKey() | transloco }}</span>
            }
          }
        </div>
      </div>

      <div class="flex flex-col gap-1">
        <label for="wish-entry-alias" class="text-sm text-fg-secondary">
          {{ 'wishes.entry.alias.label' | transloco }}
        </label>
        <input
          id="wish-entry-alias"
          type="text"
          class="app-input"
          autocomplete="off"
          spellcheck="false"
          [value]="alias()"
          [attr.aria-invalid]="aliasError() ? 'true' : null"
          [attr.aria-describedby]="aliasError() ? 'wish-entry-alias-error' : null"
          (input)="setAlias($any($event.target).value)"
          (blur)="aliasTouched.set(true)"
        />
        @if (aliasError(); as key) {
          <p id="wish-entry-alias-error" class="text-sm text-danger-fg">
            {{ key | transloco: { max: aliasMax } }}
          </p>
        }
      </div>

      <div class="flex flex-col gap-1">
        <label for="wish-entry-duration" class="text-sm text-fg-secondary">
          {{ 'wishes.dialog.duration' | transloco }}
        </label>
        <input
          id="wish-entry-duration"
          type="text"
          inputmode="numeric"
          class="app-input"
          autocomplete="off"
          [value]="duration()"
          [attr.aria-invalid]="durationError() ? 'true' : null"
          [attr.aria-describedby]="durationError() ? 'wish-entry-duration-error' : null"
          (input)="setDuration($any($event.target).value)"
          (blur)="durationTouched.set(true)"
        />
        @if (durationError(); as key) {
          <p id="wish-entry-duration-error" class="text-sm text-danger-fg">
            {{ key | transloco: { max: durationMax } }}
          </p>
        }
      </div>

      <div class="flex flex-col gap-1">
        <label for="wish-entry-login" class="text-sm text-fg-secondary">
          {{ 'wishes.entry.login.label' | transloco }}
        </label>
        <input
          id="wish-entry-login"
          type="text"
          class="app-input"
          autocomplete="off"
          spellcheck="false"
          [value]="login()"
          [attr.aria-invalid]="loginError() ? 'true' : null"
          [attr.aria-describedby]="loginError() ? 'wish-entry-login-error' : null"
          (input)="setLogin($any($event.target).value)"
          (blur)="loginTouched.set(true)"
        />
        @if (loginError(); as key) {
          <p id="wish-entry-login-error" class="text-sm text-danger-fg">{{ key | transloco }}</p>
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
                @case ('adopt') {
                  <button
                    type="button"
                    appButton="outline"
                    [disabled]="busy()"
                    (click)="takeOver()"
                  >
                    {{ 'wishes.entry.conflict.takeOver' | transloco }}
                  </button>
                }
                @case ('running') {
                  @if (notice.canExtend) {
                    <button
                      type="button"
                      appButton="outline"
                      [disabled]="busy()"
                      (click)="extend(notice.wish)"
                    >
                      {{ 'wishes.actions.extend' | transloco }}
                    </button>
                  }
                  <button
                    type="button"
                    appButton="outline"
                    [disabled]="busy()"
                    (click)="jump(notice.wish.id)"
                  >
                    {{ 'wishes.dialog.toWish' | transloco }}
                  </button>
                }
                @case ('kept') {
                  <button
                    type="button"
                    appButton="outline"
                    [disabled]="busy()"
                    (click)="jump(notice.wishId)"
                  >
                    {{ 'wishes.dialog.toWish' | transloco }}
                  </button>
                }
                @case ('other') {
                  <button
                    type="button"
                    appButton="outline"
                    [disabled]="busy()"
                    (click)="jump(notice.wishId)"
                  >
                    {{ 'wishes.dialog.toWish' | transloco }}
                  </button>
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
        <p dialog-actions id="wish-entry-blocked" class="mr-auto text-xs text-fg-muted">
          {{ reason | transloco }}
        </p>
      }
      <button
        dialog-actions
        type="button"
        appButton="outline"
        buttonSize="lg"
        [disabled]="busy()"
        (click)="dialogRef.close(undefined)"
      >
        {{ 'common.cancel' | transloco }}
      </button>
      <button
        dialog-actions
        type="button"
        appButton="primary"
        buttonSize="lg"
        [disabled]="busy() || blockedReason() !== null"
        [attr.aria-describedby]="blockedReason() ? 'wish-entry-blocked' : null"
        (click)="submit()"
      >
        {{ 'wishes.entry.submit' | transloco }}
      </button>
    </app-dialog-shell>
  `,
})
export class WishEntryDialog {
  protected readonly data = inject<WishEntryDialogData>(DIALOG_DATA);
  protected readonly dialogRef = inject<DialogRef<WishEntryDialogResult | undefined>>(DialogRef);
  private readonly wishService = inject(WishService);
  private readonly lookup = inject(SevenTvEmoteLookup);

  protected readonly aliasMax = WISH_ALIAS_MAX_LENGTH;
  protected readonly durationMax = WISH_DURATION_DAYS_MAX;

  protected readonly emoteRaw = signal('');
  protected readonly emoteTouched = signal(false);
  private readonly read = signal<WishEntryReadState>({ kind: 'none' });
  private readSeq = 0;
  private readTimer: ReturnType<typeof setTimeout> | null = null;
  private readSubscription: Subscription | null = null;
  /** The id the last input event led to; a change of it is what invalidates the read. */
  private lastId: string | null = null;

  protected readonly alias = signal('');
  /** The alias holds the read's name, not something the manager typed. */
  private readonly aliasAuto = signal(false);
  protected readonly aliasTouched = signal(false);
  protected readonly duration = signal('');
  protected readonly durationTouched = signal(false);
  protected readonly login = signal('');
  protected readonly loginTouched = signal(false);

  /** A field the server refused, as the translation key to show there. */
  private readonly serverFieldError = signal<{
    field: 'emote' | 'alias' | 'duration' | 'login';
    key: string;
  } | null>(null);
  protected readonly conflict = signal<EntryConflict | null>(null);
  protected readonly errorKey = signal<string | null>(null);
  protected readonly disabledFeature = signal(false);
  protected readonly saving = signal(false);

  private readonly parsed = computed(() => parseEmoteInput(this.emoteRaw()));

  /** The one current emote id, canonical (`canonicalSevenTvEmoteId`), or `null` while invalid. */
  protected readonly currentId = computed(() => {
    const parsed = this.parsed();
    return parsed.ok ? canonicalSevenTvEmoteId(parsed.id) : null;
  });

  /** The read state, but only as far as it belongs to the current id. */
  protected readonly currentRead = computed<WishEntryReadState>(() => {
    const read = this.read();
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

  private readonly aliasResult = computed(() => validateWishAlias(this.alias()));
  private readonly durationResult = computed(() => parseWishDurationDays(this.duration()));
  private readonly loginResult = computed(() => normalizeRequesterLogin(this.login()));

  protected readonly emoteError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'emote') {
      return server.key;
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
    return !result.ok && this.aliasTouched() ? `wishes.entry.alias.${result.reason}` : null;
  });

  protected readonly durationError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'duration') {
      return server.key;
    }
    const result = this.durationResult();
    return !result.ok && this.durationTouched()
      ? `wishes.dialog.durationInvalid.${result.reason}`
      : null;
  });

  protected readonly loginError = computed(() => {
    const server = this.serverFieldError();
    if (server?.field === 'login') {
      return server.key;
    }
    return this.loginResult().kind === 'invalid' && this.loginTouched()
      ? 'wishes.entry.login.invalid'
      : null;
  });

  /** The first reason the wish cannot be sent yet, as a translation key; `null` when it can. */
  protected readonly blockedReason = computed(() => {
    if (this.disabledFeature()) {
      return 'wishes.dialog.blocked.disabled';
    }
    const parsed = this.parsed();
    if (!parsed.ok) {
      return parsed.reason === 'empty'
        ? 'wishes.entry.blocked.emoteEmpty'
        : 'wishes.entry.blocked.emoteInvalid';
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
    if (this.loginResult().kind === 'invalid') {
      return 'wishes.entry.blocked.login';
    }
    return null;
  });

  protected readonly busy = computed(() => this.saving() || this.conflict()?.kind === 'resolving');

  protected readonly conflictKey = computed(() => {
    const conflict = this.conflict();
    return conflict === null || conflict.kind === 'resolving'
      ? null
      : `wishes.entry.conflict.${conflict.kind}`;
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.cancelRead());
    this.wishService.getSettings(this.data.channelName).subscribe({
      next: (settings) => {
        if (this.duration() === '' && !this.durationTouched()) {
          this.duration.set(String(settings.defaultDurationDays));
        }
      },
      // Without the default the field stays empty and asks for a value.
      error: () => undefined,
    });
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
    if (this.aliasAuto()) {
      this.alias.set('');
      this.aliasAuto.set(false);
    }
    if (id === null) {
      this.read.set({ kind: 'none' });
      return;
    }
    this.read.set({ kind: 'running', id });
    const seq = ++this.readSeq;
    this.readTimer = setTimeout(() => {
      this.readTimer = null;
      this.readSubscription = this.lookup
        .lookup(id)
        .subscribe((result) => this.onReadResult(seq, result));
    }, WISH_ENTRY_LOOKUP_DEBOUNCE_MS);
  }

  protected setAlias(value: string): void {
    this.alias.set(value);
    this.aliasAuto.set(false);
    this.aliasTouched.set(true);
    this.clearServerError('alias');
  }

  protected setDuration(value: string): void {
    this.duration.set(value);
    this.durationTouched.set(true);
    this.clearServerError('duration');
  }

  protected setLogin(value: string): void {
    this.login.set(value);
    this.loginTouched.set(true);
    this.clearServerError('login');
  }

  protected submit(): void {
    // The button is disabled while blocked; Enter paths and a second click land here and check again.
    if (this.busy() || this.blockedReason() !== null) {
      return;
    }
    const id = this.currentId();
    const alias = this.aliasResult();
    const duration = this.durationResult();
    const login = this.loginResult();
    if (id === null || !alias.ok || !duration.ok || login.kind === 'invalid') {
      return;
    }
    const found = this.found();
    const body: CreateWishBody = {
      sevenTvEmoteId: id,
      alias: alias.alias,
      durationDays: duration.value,
      ...(found !== null && found.name !== '' ? { emoteName: found.name } : {}),
      ...(login.kind === 'ok' ? { requesterLogin: login.login } : {}),
    };
    this.errorKey.set(null);
    this.conflict.set(null);
    this.saving.set(true);
    // Not dismissible while the request is out: the page would never hear of a wish filed after all.
    this.dialogRef.disableClose = true;
    this.wishService.create(this.data.channelName, body).subscribe({
      next: (wish) => {
        const { setFull, ...created } = wish;
        this.dialogRef.close({ kind: 'created', wish: created, setFull: setFull === true });
      },
      error: (error: unknown) => this.onCreateError(error, id),
    });
  }

  protected takeOver(): void {
    const conflict = this.conflict();
    if (conflict?.kind === 'adopt' && !this.busy()) {
      this.dialogRef.close({
        kind: 'adopt',
        sevenTvEmoteId: conflict.sevenTvEmoteId,
        slotIds: conflict.slotIds,
        wish: conflict.wish,
      });
    }
  }

  protected extend(wish: Wish): void {
    if (this.busy()) {
      return;
    }
    this.errorKey.set(null);
    this.saving.set(true);
    this.dialogRef.disableClose = true;
    this.wishService.extend(this.data.channelName, wish.id).subscribe({
      next: (extended) => this.dialogRef.close({ kind: 'extended', wish: extended }),
      error: (error: unknown) => {
        this.saving.set(false);
        this.dialogRef.disableClose = false;
        this.errorKey.set(
          error instanceof HttpErrorResponse
            ? wishErrorKey(error, 'action')
            : GENERIC_ERROR_TRANSLATION_KEY,
        );
      },
    });
  }

  protected jump(wishId: number): void {
    if (!this.busy()) {
      this.dialogRef.close({ kind: 'jump', wishId });
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
        this.read.set({
          kind: 'found',
          id: result.id,
          name: result.name,
          animated: result.animated,
        });
        if (result.name !== '' && (this.alias() === '' || this.aliasAuto())) {
          this.alias.set(result.name);
          this.aliasAuto.set(true);
          this.clearServerError('alias');
        }
        return;
      case 'unknown':
        this.read.set({ kind: 'unknown', id: result.id });
        return;
      default:
        this.read.set({
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

  private clearServerError(field: 'emote' | 'alias' | 'duration' | 'login'): void {
    if (this.serverFieldError()?.field === field) {
      this.serverFieldError.set(null);
    }
  }

  private onCreateError(error: unknown, sentId: string): void {
    this.saving.set(false);
    this.dialogRef.disableClose = false;
    if (!(error instanceof HttpErrorResponse)) {
      this.errorKey.set(GENERIC_ERROR_TRANSLATION_KEY);
      return;
    }
    switch (apiErrorCode(error)) {
      case 'wish_emote_already_in_set':
        this.resolveInSet(error, sentId);
        return;
      case 'wish_already_running':
        this.resolveRunning(error);
        return;
      case 'emote_ids_invalid':
        this.serverFieldError.set({ field: 'emote', key: WISH_EMOTE_IDS_INVALID_KEY });
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
      case 'wish_requester_invalid':
        this.serverFieldError.set({ field: 'login', key: 'wishes.entry.login.invalid' });
        return;
      case 'wishes_disabled':
        this.disabledFeature.set(true);
        return;
      default:
        this.errorKey.set(wishErrorKey(error, 'create'));
    }
  }

  /** 409 `wish_emote_already_in_set`: decided by the wish id in the body (plan 2.8 a). */
  private resolveInSet(error: HttpErrorResponse, sentId: string): void {
    const { slotIds = [], wishId = null } = wishConflict(error);
    const adoptWith = (wish: Wish | null): EntryConflict => ({
      kind: 'adopt',
      sevenTvEmoteId: sentId,
      slotIds,
      wish,
    });
    if (wishId === null) {
      this.conflict.set(adoptWith(null));
      return;
    }
    this.resolveWish(wishId, (wish) => {
      if (wish === null) {
        // Gone in the meantime: nothing runs, the entries are there to take over.
        return adoptWith(null);
      }
      if (wish.status === 'kept') {
        return { kind: 'kept', wishId };
      }
      if (holdsSlot(wish)) {
        return runningConflict(wish);
      }
      return wishActions(wish).includes('adopt') ? adoptWith(wish) : { kind: 'other', wishId };
    });
  }

  /** 409 `wish_already_running`: the emote is not in the set, a wish for it runs. */
  private resolveRunning(error: HttpErrorResponse): void {
    const wishId = wishConflict(error).wishId ?? null;
    if (wishId === null) {
      this.errorKey.set(wishErrorKey(error, 'create'));
      return;
    }
    this.resolveWish(wishId, (wish) => {
      if (wish === null) {
        return null;
      }
      return wish.status === 'kept' ? { kind: 'kept', wishId } : runningConflict(wish);
    });
  }

  /** Loads the wish a 409 names and turns it into the notice; a failed load still offers the way to it. */
  private resolveWish(wishId: number, decide: (wish: Wish | null) => EntryConflict | null): void {
    this.conflict.set({ kind: 'resolving' });
    const id = this.currentId();
    this.wishService.get(this.data.channelName, wishId).subscribe({
      next: (wish) => {
        if (this.currentId() === id && this.conflict()?.kind === 'resolving') {
          this.conflict.set(decide(wish));
        }
      },
      error: (error: unknown) => {
        if (this.currentId() !== id || this.conflict()?.kind !== 'resolving') {
          return;
        }
        const gone = error instanceof HttpErrorResponse && error.status === 404;
        this.conflict.set(gone ? decide(null) : { kind: 'other', wishId });
        if (gone && this.conflict() === null) {
          this.errorKey.set('errors.api.wish_not_found');
        }
      },
    });
  }
}

function holdsSlot(wish: Wish): boolean {
  return wish.slotId !== null && (wish.status === 'active' || wish.status === 'presented');
}

function runningConflict(wish: Wish): EntryConflict {
  return { kind: 'running', wish, canExtend: wishActions(wish).includes('extend') };
}

export function openWishEntryDialog(
  dialog: Dialog,
  data: WishEntryDialogData,
): DialogRef<WishEntryDialogResult | undefined> {
  return openAppDialog<WishEntryDialogResult | undefined, WishEntryDialogData>(
    dialog,
    WishEntryDialog,
    { data },
  );
}
