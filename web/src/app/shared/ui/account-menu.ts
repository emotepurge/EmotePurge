import { Dialog } from '@angular/cdk/dialog';
import { DOCUMENT } from '@angular/common';
import {
  Component,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe, TranslocoService } from '@jsverse/transloco';

import { AuthService } from '../../core/auth/auth.service';
import { Avatar } from './avatar';
import { DisplayPreferences } from './display-preferences';
import { Popover } from './popover';
import { openTypedConfirmDialog } from './typed-confirm-dialog';

/**
 * Everything personal in the app frame behind one trigger: who you are, where your own pages are,
 * how the app looks, which language it speaks, and the way out.
 *
 * The reason is the rule that what stands in the app header stands on every screen in every
 * session. Six permanent controls measured against that are five too many, and the argument holds
 * on a desktop just as it does on a phone — which is why this replaces both the desktop cluster and
 * the mobile disclosure with one thing rather than two.
 *
 * Disclosure semantics, deliberately not role="menu": the panel holds mixed children — router
 * links, two radiogroups, a button — and role="menu" requires menuitem children, which a radiogroup
 * inside it is not. This is a step back from what theme-menu.ts did (menuitemradio) and the same
 * decision the shell's own disclosure already took.
 *
 * It calls ensureLoaded() itself because it renders on the landing and login pages too, outside the
 * shell that is otherwise the only caller. The call is idempotent, so the shell's own stays.
 */
@Component({
  selector: 'app-account-menu',
  imports: [Avatar, DisplayPreferences, Popover, RouterLink, TranslocoPipe],
  template: `
    <div class="relative" data-popover-anchor>
      <!-- 44 px in a 56 px header leaves 6 px of air top and bottom. The plate inside is 32 px and
           is painted before the picture arrives, so nothing in this box ever changes size. -->
      <button
        #trigger
        type="button"
        class="inline-flex h-11 w-11 items-center justify-center rounded-md text-fg-muted transition hover:text-fg disabled:cursor-default"
        aria-haspopup="dialog"
        [attr.aria-expanded]="isOpen()"
        [attr.aria-label]="triggerLabel()"
        [disabled]="!authResolved()"
        (click)="toggle()"
      >
        @if (!authResolved()) {
          <!-- Reserved, silent, letterless: the shape is final, only its content resolves. No
               spinner — it costs one roundtrip, and a spinner in the header would be louder than
               the thing it reports. -->
          <app-avatar displayName="" />
        } @else if (currentUser(); as user) {
          <app-avatar [displayName]="user.displayName" [imageUrl]="user.profileImageUrl" />
        } @else {
          <svg
            class="h-5 w-5"
            viewBox="0 0 24 24"
            fill="none"
            stroke="currentColor"
            stroke-width="1.75"
            stroke-linecap="round"
            stroke-linejoin="round"
            aria-hidden="true"
          >
            <circle cx="12" cy="12" r="3.25" />
            <path
              d="M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 1 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 1 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 1 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 1 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z"
            />
          </svg>
        }
      </button>

      @if (isOpen()) {
        <app-popover align="end" width="w-64" [ariaLabel]="triggerLabel()" (closed)="close()">
          <div class="flex flex-col">
            @if (currentUser(); as user) {
              @if (view() === 'preferences') {
                <!-- One level down. Theme and language are set once and then only confirmed, so
                     they do not earn a permanent share of the panel — the same "how often is this
                     notable?" rule the design language applies to colour, applied to space. -->
                <button
                  #back
                  type="button"
                  class="flex min-h-11 items-center sm:min-h-9 gap-2 px-3 text-left text-sm font-medium text-fg transition hover:bg-surface-inset"
                  (click)="showRoot()"
                >
                  <svg
                    class="h-4 w-4 shrink-0"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="1.75"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    aria-hidden="true"
                  >
                    <path d="M15 6l-6 6 6 6" />
                  </svg>
                  {{ 'account.preferencesTrigger' | transloco }}
                </button>

                <div class="border-t border-border">
                  <app-display-preferences />
                </div>
              } @else {
                <!-- No hover on this row. It carries the same rhythm as the entries below it but is
                     not clickable, and a hover must never promise a click that is not there.
                     Separators sit on the *following* blocks, never here: the admin entry is
                     conditional, so a border-b would double up with the next block's border-t
                     whenever it is absent. -->
                <div class="flex items-center gap-3 px-3 py-3">
                  <app-avatar
                    [displayName]="user.displayName"
                    [imageUrl]="user.profileImageUrl"
                    [size]="36"
                  />
                  <!-- font-medium, not semibold: semibold is reserved for headings, and a fifth
                       weight would be a fifth level in a four-level scale. -->
                  <span class="truncate text-sm font-medium text-fg">{{ user.displayName }}</span>
                </div>

                @if (user.isGlobalAdmin) {
                  <!-- Visibility only — /admin is behind adminGuard and every admin endpoint behind
                       GlobalAdminAuthorizationFilter. The flag rides along on the cached /me. -->
                  <a
                    routerLink="/admin"
                    class="flex min-h-11 items-center sm:min-h-9 border-t border-border px-3 text-sm text-fg-body transition hover:bg-surface-inset"
                    (click)="close()"
                  >
                    {{ 'shell.admin' | transloco }}
                  </a>
                }

                <button
                  #preferencesRow
                  type="button"
                  class="flex min-h-11 items-center sm:min-h-9 border-t border-border px-3 text-left text-sm text-fg-body transition hover:bg-surface-inset"
                  (click)="showPreferences()"
                >
                  {{ 'account.preferencesTrigger' | transloco }}
                  <svg
                    class="ml-auto h-4 w-4 shrink-0 text-fg-muted"
                    viewBox="0 0 24 24"
                    fill="none"
                    stroke="currentColor"
                    stroke-width="1.75"
                    stroke-linecap="round"
                    stroke-linejoin="round"
                    aria-hidden="true"
                  >
                    <path d="M9 6l6 6-6 6" />
                  </svg>
                </button>

                <button
                  type="button"
                  class="flex min-h-11 items-center sm:min-h-9 border-t border-border px-3 text-left text-sm text-fg-body transition hover:bg-surface-inset"
                  (click)="logout()"
                >
                  {{ 'shell.logout' | transloco }}
                </button>

                <!-- Last and set apart by colour alone: the rarest, least reversible entry of the
                     panel. Trigger tier of the destructive ladder (UI-Designsprache §4.2) — the
                     confirmation, and with it the solid button, is the typed dialog behind it. -->
                <button
                  type="button"
                  class="flex min-h-11 items-center sm:min-h-9 border-t border-border px-3 text-left text-sm text-danger-fg transition hover:bg-danger-wash disabled:cursor-progress disabled:opacity-60 disabled:hover:bg-transparent"
                  [disabled]="isDeleting()"
                  [attr.aria-busy]="isDeleting() ? 'true' : null"
                  (click)="deleteAccount()"
                >
                  {{
                    (isDeleting() ? 'account.delete.pending' : 'account.delete.trigger') | transloco
                  }}
                </button>

                @if (deletionNotice(); as notice) {
                  <!-- The panel is reopened for this: the dialog is gone by the time the request
                       fails. A confirmed rejection says nothing changed; a lost answer must not —
                       the account may be gone already, and only a reload tells (signing in would
                       recreate an empty account). -->
                  <div
                    #deleteAlert
                    role="alert"
                    tabindex="-1"
                    class="flex flex-col gap-1 border-t border-border bg-danger-wash px-3 py-3 text-xs text-danger-fg"
                  >
                    @if (notice.status === 'failed') {
                      <span>{{ 'account.delete.failed' | transloco }}</span>
                      <span>{{ notice.errorKey | transloco }}</span>
                    } @else if (notice.status === 'mismatch') {
                      <span>{{ 'account.delete.mismatch' | transloco }}</span>
                    } @else {
                      <span>{{ 'account.delete.unconfirmed' | transloco }}</span>
                    }
                  </div>
                }
              }
            } @else {
              <!-- Logged out the panel holds nothing but these two, so a row that opens a subview
                   with a single occupant would be a door in front of a door. -->
              <app-display-preferences />
            }
          </div>
        </app-popover>
      }
    </div>
  `,
})
export class AccountMenu {
  private readonly authService = inject(AuthService);
  private readonly transloco = inject(TranslocoService);
  private readonly dialog = inject(Dialog);
  private readonly document = inject(DOCUMENT);
  private readonly elementRef = inject(ElementRef<HTMLElement>);
  private readonly injector = inject(Injector);
  private readonly trigger = viewChild<ElementRef<HTMLButtonElement>>('trigger');
  private readonly back = viewChild<ElementRef<HTMLButtonElement>>('back');
  private readonly preferencesRow = viewChild<ElementRef<HTMLButtonElement>>('preferencesRow');
  private readonly deleteAlert = viewChild<ElementRef<HTMLElement>>('deleteAlert');

  protected readonly currentUser = this.authService.currentUser;
  protected readonly authResolved = this.authService.isResolved;
  protected readonly isOpen = signal(false);
  /**
   * Which level of the panel is showing. Reset on close, so the menu always opens where it was
   * left in the visitor's mind — at the top — rather than in a subview they last saw minutes ago.
   */
  protected readonly view = signal<'root' | 'preferences'>('root');
  /**
   * The deletion's progress lives in AuthService, not here: this component is destroyed with its
   * page, and an answer arriving afterwards must neither be lost nor touch a dead view.
   */
  protected readonly deletionNotice = computed(() => {
    const state = this.authService.deletionState();
    return state.status === 'failed' ||
      state.status === 'unconfirmed' ||
      state.status === 'mismatch'
      ? state
      : null;
  });
  /**
   * True from the confirmation until the server answers. The request can take a while (the server
   * revokes tokens at Twitch before it responds), and the panel can be reopened meanwhile — the row
   * is disabled and says what is happening, so a second submission is not possible.
   */
  protected readonly isDeleting = computed(
    () => this.authService.deletionState().status === 'pending',
  );

  /**
   * Translated imperatively rather than through the pipe, because it carries an interpolated name
   * into an attribute. Reading activeLang is what makes it follow a language switch made in this
   * very panel — translate() is a plain call and would otherwise never re-run.
   */
  private readonly activeLang = toSignal(this.transloco.langChanges$, {
    initialValue: this.transloco.getActiveLang(),
  });

  protected readonly triggerLabel = computed(() => {
    this.activeLang();
    const user = this.currentUser();
    return user
      ? this.transloco.translate('account.trigger', { name: user.displayName })
      : this.transloco.translate('account.preferencesTrigger');
  });

  constructor() {
    // The landing and login pages render outside AppShell, which is otherwise the only caller.
    // Idempotent, so the shell's own call is untouched and no second request is made.
    this.authService.ensureLoaded().subscribe();

    // An outcome is shown by reopening the panel at its root, wherever the user wandered meanwhile
    // — also for a menu created after the request started, on another page. An effect, so it dies
    // with the component and its focus request never reaches a destroyed view.
    effect(() => {
      if (this.deletionNotice()) {
        untracked(() => {
          this.view.set('root');
          this.isOpen.set(true);
          // The dialog that held focus is gone and the panel is new: the alert is the thing to read.
          this.focusAfterRender(() => this.deleteAlert());
        });
      }
    });
  }

  protected toggle(): void {
    if (this.isOpen()) {
      this.close();
      return;
    }
    this.isOpen.set(true);
  }

  protected close(): void {
    if (!this.isOpen()) {
      return;
    }
    // Focus would otherwise fall to <body> together with the panel that held it.
    const hadFocus = this.elementRef.nativeElement.contains(this.document.activeElement);
    this.isOpen.set(false);
    this.view.set('root');
    this.authService.dismissDeletionOutcome();
    if (hadFocus) {
      this.trigger()?.nativeElement.focus();
    }
  }

  protected showPreferences(): void {
    this.view.set('preferences');
    this.focusAfterRender(() => this.back());
  }

  protected showRoot(): void {
    this.view.set('root');
    this.focusAfterRender(() => this.preferencesRow());
  }

  protected logout(): void {
    this.close();
    this.authService.logout();
  }

  /**
   * The panel closes first, so the dialog is not stacked on top of a popover that outside-click
   * rules could dismiss mid-confirmation. Typing the login is the lock (TypedConfirmDialog). Only a
   * confirmed, server-acknowledged deletion resets the client (AuthService.startAccountDeletion);
   * a rejection or a lost answer reopens the panel with the matching notice (see deletionNotice),
   * a 401 goes to the login page instead.
   */
  protected deleteAccount(): void {
    const user = this.currentUser();
    if (!user || this.isDeleting()) {
      return;
    }
    // Bound to this account now: the server refuses if the session is someone else's by then.
    const expectedTwitchUserId = user.twitchUserId;
    this.close();

    openTypedConfirmDialog(this.dialog, {
      title: this.transloco.translate('account.delete.title'),
      message: this.transloco.translate('account.delete.message'),
      requiredText: user.login,
      inputLabel: this.transloco.translate('account.delete.inputLabel'),
      confirmLabel: this.transloco.translate('account.delete.confirm'),
    }).closed.subscribe((confirmed) => {
      if (!confirmed) {
        return;
      }
      this.authService.startAccountDeletion(expectedTwitchUserId);
    });
  }

  /**
   * Changing the level swaps the whole panel content, so the element that had focus stops existing.
   * Without this the caret lands on <body> and the next Tab starts at the top of the document —
   * the panel would be keyboard-reachable but not keyboard-usable. Deferred to after the next
   * render because the target is only queried into existence by that render.
   */
  private focusAfterRender(target: () => ElementRef<HTMLElement> | undefined): void {
    afterNextRender(() => target()?.nativeElement.focus(), { injector: this.injector });
  }
}
