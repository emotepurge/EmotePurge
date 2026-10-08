import { Location, NgOptimizedImage } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import {
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { TranslocoPipe } from '@jsverse/transloco';
import { catchError, of } from 'rxjs';

import { AuthService } from '../../core/auth/auth.service';
import { ContactConfigResponse } from '../../core/contact/contact.model';
import { ContactService } from '../../core/contact/contact.service';
import { TURNSTILE_LOADER, TurnstileApi } from '../../core/contact/turnstile';
import { apiErrorTranslationKey } from '../../core/i18n/api-error';
import { LanguageService } from '../../core/i18n/language.service';
import { NavigationHistoryService } from '../../core/routing/navigation-history.service';
import { ThemeService } from '../../core/theme/theme.service';
import { LOGO_SRC } from '../../shared/branding/logo';
import { AccountMenu } from '../../shared/ui/account-menu';
import { BackLink } from '../../shared/ui/back-link';
import { Button } from '../../shared/ui/button';
import { NoticeBanner } from '../../shared/ui/notice-banner';
import { resolveLegalBackTarget } from '../legal/legal-back-target';

const NOT_AVAILABLE_CONFIG: ContactConfigResponse = { available: false, turnstileSiteKey: null };

// Mirrors ContactValidation.cs (Api layer) — kept in sync by hand, the two projects deploy
// separately and neither can reference the other. The point here is not to duplicate the server's
// authority (it re-validates regardless) but to give a visitor an inline reason before they submit,
// rather than a round trip that comes back contact_invalid.
const MAX_NAME_LENGTH = 100;
const MAX_EMAIL_LENGTH = 254;
const MIN_MESSAGE_LENGTH = 10;
const MAX_MESSAGE_LENGTH = 5000;

// Deliberately as permissive as ContactValidation.cs's own EmailPattern: one @, something on both
// sides, a dot in the domain part — not full RFC 5322 grammar. Turnstile plus the server's own check
// are the real gate on whether the address is genuine.
const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/**
 * `/contact` (docs/DECISIONS.md 2026-09-24, "contact form"): the electronic contact route § 5 DDG
 * requires next to the e-mail address in the imprint. Outside the app shell and every auth guard,
 * reachable before login — same reasoning, same page frame and the same `resolveLegalBackTarget`
 * back control as `LegalPage` (see its own doc comment for why `Location.back()` is safe here: this
 * page sits outside the shell's route tree too and is reachable from everywhere in it).
 *
 * The Turnstile widget script loads only from here, and only once the form is actually about to be
 * shown (`available === true`) — never eagerly, and never on any other page (see
 * `core/contact/turnstile.ts`).
 */
@Component({
  selector: 'app-contact-page',
  imports: [
    AccountMenu,
    BackLink,
    Button,
    NgOptimizedImage,
    NoticeBanner,
    RouterLink,
    TranslocoPipe,
  ],
  template: `
    <div class="flex min-h-screen flex-col bg-page text-fg">
      <header class="flex items-center justify-between px-4 py-3">
        <a routerLink="/welcome" class="flex items-center gap-2 text-lg font-semibold">
          <img
            [ngSrc]="logoSrc"
            width="24"
            height="24"
            disableOptimizedSrcset
            alt=""
            class="h-6 w-6"
          />
          Emote Purge
        </a>
        <app-account-menu />
      </header>

      <main class="mx-auto w-full max-w-7xl flex-1 px-4 py-8">
        @if (backTarget(); as target) {
          @if (target.kind === 'back') {
            <button
              type="button"
              (click)="goBack()"
              class="inline-flex items-center gap-1 rounded-md border border-accent-selected px-3 py-1.5 text-sm whitespace-nowrap text-accent-fg transition hover:bg-accent-wash"
            >
              <span aria-hidden="true">←</span>{{ 'legal.backAction' | transloco }}
            </button>
          } @else {
            <app-back-link [link]="target.link" [label]="target.labelKey | transloco" />
          }
        }

        <h1 class="mt-6 text-2xl font-semibold">{{ 'contact.title' | transloco }}</h1>

        @if (configResource.isLoading()) {
          <div
            role="status"
            [attr.aria-label]="'common.loading' | transloco"
            class="mt-6 flex max-w-lg flex-col gap-3"
          >
            <div class="app-skeleton h-10 w-full"></div>
            <div class="app-skeleton h-10 w-full"></div>
            <div class="app-skeleton h-24 w-full"></div>
          </div>
        } @else if (isAvailable()) {
          @if (submitted()) {
            <app-notice-banner variant="info" class="mt-6 block max-w-lg">
              {{ 'contact.success' | transloco }}
            </app-notice-banner>
          } @else {
            <form class="mt-6 flex max-w-lg flex-col gap-4" (submit)="submit($event)">
              @if (submitError(); as errorKey) {
                <app-notice-banner variant="error">{{ errorKey | transloco }}</app-notice-banner>
              }

              <label class="flex flex-col gap-1 text-sm text-fg-secondary">
                {{ 'contact.nameLabel' | transloco }}
                <input
                  id="contact-name"
                  type="text"
                  autocomplete="name"
                  [value]="name()"
                  (input)="name.set($any($event.target).value)"
                  (blur)="nameTouched.set(true)"
                  class="app-input"
                  [attr.aria-invalid]="nameTouched() && nameError() ? 'true' : null"
                  [attr.aria-describedby]="
                    nameTouched() && nameError() ? 'contact-name-error' : null
                  "
                />
              </label>
              @if (nameTouched() && nameError(); as errorKey) {
                <p id="contact-name-error" class="text-sm text-danger-fg">
                  {{ errorKey | transloco }}
                </p>
              }

              <label class="flex flex-col gap-1 text-sm text-fg-secondary">
                {{ 'contact.emailLabel' | transloco }}
                <input
                  id="contact-email"
                  type="email"
                  autocomplete="email"
                  required
                  [value]="email()"
                  (input)="email.set($any($event.target).value)"
                  (blur)="emailTouched.set(true)"
                  class="app-input"
                  [attr.aria-invalid]="emailTouched() && emailError() ? 'true' : null"
                  [attr.aria-describedby]="
                    emailTouched() && emailError() ? 'contact-email-error' : null
                  "
                />
              </label>
              @if (emailTouched() && emailError(); as errorKey) {
                <p id="contact-email-error" class="text-sm text-danger-fg">
                  {{ errorKey | transloco }}
                </p>
              }

              <label class="flex flex-col gap-1 text-sm text-fg-secondary">
                {{ 'contact.messageLabel' | transloco }}
                <textarea
                  id="contact-message"
                  rows="6"
                  required
                  [value]="message()"
                  (input)="message.set($any($event.target).value)"
                  (blur)="messageTouched.set(true)"
                  class="app-input"
                  [attr.aria-invalid]="messageTouched() && messageError() ? 'true' : null"
                  [attr.aria-describedby]="messageDescribedById()"
                ></textarea>
              </label>
              @if (messageTouched() && messageError(); as errorKey) {
                <p id="contact-message-error" class="text-sm text-danger-fg">
                  {{ errorKey | transloco }}
                </p>
              } @else {
                <p id="contact-message-counter" class="text-xs text-fg-muted">
                  {{ 'contact.messageCounter' | transloco: { count: messageLength(), max: 5000 } }}
                </p>
              }

              <!-- Honeypot: invisible and unreachable by a real visitor, never announced to a
                   screen reader. A bot filling every field it can find sets this one too, which the
                   API answers as an unconditional success without doing anything. -->
              <input
                type="text"
                name="website"
                tabindex="-1"
                autocomplete="off"
                aria-hidden="true"
                [value]="website()"
                (input)="website.set($any($event.target).value)"
                class="absolute -left-[9999px] h-px w-px overflow-hidden"
              />

              <div #turnstileContainer></div>
              @if (turnstileLoadFailed()) {
                <app-notice-banner variant="error">{{
                  'contact.turnstileLoadFailed' | transloco
                }}</app-notice-banner>
              }

              @if (!turnstileToken() && !isSending()) {
                <p id="contact-submit-hint" class="text-xs text-fg-muted">
                  {{ 'contact.completeChallengeHint' | transloco }}
                </p>
              }
              <button
                type="submit"
                appButton="primary"
                buttonSize="lg"
                [disabled]="!canSubmit()"
                [attr.aria-describedby]="
                  !turnstileToken() && !isSending() ? 'contact-submit-hint' : null
                "
                class="self-start"
              >
                {{ 'contact.submit' | transloco }}
              </button>

              <p class="text-xs text-fg-muted">
                {{ 'contact.privacyHint' | transloco }}
                <a routerLink="/privacy" class="underline">{{
                  'contact.privacyLinkLabel' | transloco
                }}</a>
              </p>
            </form>
          }
        } @else {
          <app-notice-banner variant="info" class="mt-6 block max-w-lg">
            {{ 'contact.notAvailable' | transloco }}
            <a routerLink="/imprint" class="underline">{{
              'contact.notAvailableImprintLink' | transloco
            }}</a>
          </app-notice-banner>
        }
      </main>
    </div>
  `,
})
export class ContactPage {
  private readonly authService = inject(AuthService);
  private readonly contactService = inject(ContactService);
  private readonly destroyRef = inject(DestroyRef);
  private readonly languageService = inject(LanguageService);
  private readonly location = inject(Location);
  private readonly navigationHistoryService = inject(NavigationHistoryService);
  private readonly themeService = inject(ThemeService);
  private readonly turnstileLoader = inject(TURNSTILE_LOADER);

  protected readonly logoSrc = LOGO_SRC;

  protected readonly name = signal('');
  protected readonly email = signal('');
  protected readonly message = signal('');
  protected readonly website = signal('');

  // Shown only once a field has been left (blur) or a blocked submit was attempted — matching
  // §5.3's touched-gated field-error pattern — so an untouched, still-empty required field never
  // flashes an error the moment the page renders.
  protected readonly nameTouched = signal(false);
  protected readonly emailTouched = signal(false);
  protected readonly messageTouched = signal(false);

  protected readonly isSending = signal(false);
  protected readonly submitted = signal(false);
  protected readonly submitError = signal<string | null>(null);

  protected readonly turnstileToken = signal<string | null>(null);
  protected readonly turnstileLoadFailed = signal(false);
  private readonly turnstileApi = signal<TurnstileApi | null>(null);
  private readonly turnstileWidgetId = signal<string | null>(null);
  private readonly turnstileContainer = viewChild<ElementRef<HTMLDivElement>>('turnstileContainer');

  protected readonly configResource = rxResource({
    stream: () => this.contactService.getConfig().pipe(catchError(() => of(NOT_AVAILABLE_CONFIG))),
  });

  protected readonly isAvailable = computed(
    () => this.configResource.hasValue() && this.configResource.value().available,
  );

  // Same reasoning as LegalPage.backTarget: a wrong initial guess for one microtask is cheap, and
  // gating on AuthService.isResolved() would only delay the correct label by that same microtask.
  protected readonly backTarget = computed(() =>
    resolveLegalBackTarget(
      this.navigationHistoryService.hasPreviousPage(),
      this.authService.currentUser() !== null,
    ),
  );

  protected readonly nameError = computed<string | null>(() =>
    this.name().length > MAX_NAME_LENGTH ? 'contact.nameTooLongHint' : null,
  );

  protected readonly emailError = computed<string | null>(() => {
    const value = this.email().trim();
    if (value.length === 0) {
      return null; // the native `required` attribute already blocks an empty submit
    }
    if (value.length > MAX_EMAIL_LENGTH) {
      return 'contact.emailTooLongHint';
    }
    return EMAIL_PATTERN.test(value) ? null : 'contact.emailInvalidHint';
  });

  protected readonly messageLength = computed(() => this.message().trim().length);

  protected readonly messageError = computed<string | null>(() => {
    const length = this.messageLength();
    if (length === 0) {
      return null; // the native `required` attribute already blocks an empty submit
    }
    if (length < MIN_MESSAGE_LENGTH) {
      return 'contact.messageTooShortHint';
    }
    return length > MAX_MESSAGE_LENGTH ? 'contact.messageTooLongHint' : null;
  });

  // Which <p> the message field's aria-describedby points at: the error once one applies and the
  // field has been touched, the always-visible character counter otherwise — never both, and never
  // neither, so the accessible description is always exactly the text currently shown.
  protected readonly messageDescribedById = computed(() =>
    this.messageTouched() && this.messageError()
      ? 'contact-message-error'
      : 'contact-message-counter',
  );

  // Mirrors the server's own shape check (ContactValidation.cs) so a click that would only come
  // back contact_invalid never leaves this page at all. Independent of *Touched — those only gate
  // whether the inline hint is *shown*, not whether the field is valid.
  protected readonly isLocallyValid = computed(() => {
    const email = this.email().trim();
    const messageLength = this.messageLength();
    return (
      this.name().length <= MAX_NAME_LENGTH &&
      email.length > 0 &&
      email.length <= MAX_EMAIL_LENGTH &&
      EMAIL_PATTERN.test(email) &&
      messageLength >= MIN_MESSAGE_LENGTH &&
      messageLength <= MAX_MESSAGE_LENGTH
    );
  });

  protected readonly canSubmit = computed(
    () => !this.isSending() && this.turnstileToken() !== null && this.isLocallyValid(),
  );

  constructor() {
    // Renders the Turnstile widget the moment the form becomes available AND its container exists
    // in the DOM, and re-renders it whenever the resolved theme or the active language changes
    // afterwards — both read unconditionally up front so they are always tracked dependencies of
    // this effect, not only inside the `.then()` below (a read inside an async callback runs after
    // the effect has already finished executing, so it is never tracked at all — before this fix,
    // changing either through this page's own AccountMenu left the widget showing its old
    // language/theme until the next full render, Codex P2, docs/DECISIONS.md 2026-09-24 revision).
    //
    // `turnstileApi`/`turnstileWidgetId` are deliberately read through `untracked()`: this effect
    // itself is what writes them (directly here, and asynchronously once `turnstileLoader()`
    // resolves), and tracking them too would make each of those writes reschedule this very effect —
    // remove, re-render, which writes the id again, which reschedules again, forever. Only
    // config/container/theme/language should ever cause a re-render.
    effect(() => {
      const config = this.configResource.hasValue() ? this.configResource.value() : null;
      const container = this.turnstileContainer();
      const theme = this.themeService.resolved();
      const language = this.languageService.lang();

      if (!config?.available || !config.turnstileSiteKey || !container) {
        return;
      }

      const siteKey = config.turnstileSiteKey;

      const previousApi = untracked(() => this.turnstileApi());
      const previousWidgetId = untracked(() => this.turnstileWidgetId());
      if (previousWidgetId !== null) {
        previousApi?.remove(previousWidgetId);
        this.turnstileWidgetId.set(null);
        // The completed/expired state of a rendered widget belongs to that widget instance, which
        // is about to be removed — force a fresh challenge under the new language/theme rather than
        // keep a token issued under the old one.
        this.turnstileToken.set(null);
      }

      this.turnstileLoader()
        .then((api) => {
          this.turnstileApi.set(api);
          const widgetId = api.render(container.nativeElement, {
            sitekey: siteKey,
            theme,
            language,
            callback: (token) => this.turnstileToken.set(token),
            'expired-callback': () => this.turnstileToken.set(null),
            'error-callback': () => this.turnstileToken.set(null),
          });
          this.turnstileWidgetId.set(widgetId);
        })
        .catch(() => this.turnstileLoadFailed.set(true));
    });

    this.destroyRef.onDestroy(() => {
      const api = this.turnstileApi();
      const widgetId = this.turnstileWidgetId();
      if (api && widgetId !== null) {
        api.remove(widgetId);
      }
    });
  }

  // Only rendered when backTarget() is 'back' — see LegalPage's identical method for why that
  // makes this safe from leaving the app.
  protected goBack(): void {
    this.location.back();
  }

  protected submit(event: Event): void {
    event.preventDefault();

    // Reveals every applicable inline hint on a blocked attempt, not only on the fields the visitor
    // happened to blur — the same "surface it now" behaviour disabled-submit buttons everywhere else
    // in this app rely on (§5.4).
    this.nameTouched.set(true);
    this.emailTouched.set(true);
    this.messageTouched.set(true);

    if (!this.canSubmit()) {
      // Deliberately does not touch turnstileToken/turnstileApi here: a client-side block (e.g. the
      // visitor edited the message back below 10 characters after already solving the challenge)
      // must not force them through Turnstile a second time once they fix the field — only a
      // rejection from the server does that (see the error handler below).
      return;
    }
    const token = this.turnstileToken();
    if (token === null) {
      return;
    }

    this.submitError.set(null);
    this.isSending.set(true);
    this.contactService
      .submit({
        name: this.name().trim() || undefined,
        email: this.email().trim(),
        message: this.message().trim(),
        turnstileToken: token,
        website: this.website(),
      })
      .subscribe({
        next: () => {
          this.isSending.set(false);
          this.submitted.set(true);
        },
        error: (error: HttpErrorResponse) => {
          this.isSending.set(false);
          this.submitError.set(apiErrorTranslationKey(error));
          // A rejected token cannot be resubmitted — force a fresh challenge before the next try.
          this.turnstileToken.set(null);
          const api = this.turnstileApi();
          const widgetId = this.turnstileWidgetId();
          if (api && widgetId !== null) {
            api.reset(widgetId);
          }
        },
      });
  }
}
