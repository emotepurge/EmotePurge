import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import {
  TURNSTILE_LOADER,
  TurnstileApi,
  TurnstileRenderOptions,
} from '../../core/contact/turnstile';
import { ContactPage } from './contact-page';

const DE_TRANSLATIONS = {
  nav: { backTo: 'Zurück zu {{target}}', overview: 'Übersicht' },
  legal: { back: 'Startseite', backAction: 'Zurück' },
  common: { loading: 'Lädt …' },
  account: { trigger: 'Konto-Menü von {{ name }}', preferencesTrigger: 'Einstellungen' },
  shell: { admin: 'Admin', logout: 'Logout' },
  theme: {
    label: 'Darstellung',
    ariaLabel: 'Darstellung wählen',
    system: 'System',
    light: 'Hell',
    dark: 'Dunkel',
  },
  languageSwitcher: { label: 'Sprache', ariaLabel: 'Sprache wählen', de: 'Deutsch', en: 'English' },
  contact: {
    title: 'Kontakt',
    nameLabel: 'Name (optional)',
    emailLabel: 'E-Mail-Adresse',
    messageLabel: 'Nachricht',
    submit: 'Nachricht senden',
    completeChallengeHint: 'Bitte zuerst die Sicherheitsabfrage oben abschließen.',
    nameTooLongHint: 'Höchstens 100 Zeichen.',
    emailInvalidHint: 'Bitte eine gültige E-Mail-Adresse eingeben.',
    emailTooLongHint: 'Höchstens 254 Zeichen.',
    messageTooShortHint: 'Mindestens 10 Zeichen.',
    messageTooLongHint: 'Höchstens 5000 Zeichen.',
    messageCounter: '{{ count }} / {{ max }} Zeichen',
    success: 'Danke — deine Nachricht wurde versendet.',
    notAvailable:
      'Das Kontaktformular ist aktuell nicht verfügbar. Bitte nutze stattdessen die E-Mail-Adresse im',
    notAvailableImprintLink: 'Impressum',
    privacyHint: 'Die Nachricht wird per E-Mail an den Betreiber verschickt.',
    privacyLinkLabel: 'Datenschutzerklärung',
    turnstileLoadFailed: 'Die Sicherheitsabfrage konnte nicht geladen werden.',
  },
  errors: {
    api: {
      contact_captcha_failed:
        'Die Sicherheitsabfrage konnte nicht bestätigt werden. Bitte versuch es erneut.',
      contact_unavailable: 'Das Kontaktformular ist gerade nicht erreichbar.',
    },
    status: { server: 'Serverfehler.' },
  },
};

class StubTurnstileApi implements TurnstileApi {
  readonly renderedOptions: TurnstileRenderOptions[] = [];
  readonly removedWidgetIds: string[] = [];
  readonly resetWidgetIds: string[] = [];
  private nextId = 0;

  render(_container: HTMLElement, options: TurnstileRenderOptions): string {
    this.renderedOptions.push(options);
    return `widget-${++this.nextId}`;
  }

  remove(widgetId: string): void {
    this.removedWidgetIds.push(widgetId);
  }

  reset(widgetId: string): void {
    this.resetWidgetIds.push(widgetId);
  }
}

/**
 * `ContactPage`'s own decision logic (rule 12): available vs. unavailable, the submit button's
 * disabled state and the reason it carries, mapping a server error code to its translated message,
 * and that the honeypot field's raw value reaches the outgoing request unmodified. Reuses
 * `legal-page.spec.ts`'s provider setup (AccountMenu fires its own `/api/auth/me` on construction
 * and needs draining regardless of what this suite is about).
 */
describe('ContactPage', () => {
  let httpMock: HttpTestingController;
  let turnstileApi: StubTurnstileApi;

  beforeEach(async () => {
    turnstileApi = new StubTurnstileApi();

    await TestBed.configureTestingModule({
      imports: [
        ContactPage,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: TURNSTILE_LOADER, useValue: () => Promise.resolve(turnstileApi) },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    httpMock.verify();
  });

  async function settle(fixture: ComponentFixture<ContactPage>): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  function render(): ComponentFixture<ContactPage> {
    const fixture = TestBed.createComponent(ContactPage);
    fixture.detectChanges();
    httpMock.expectOne('/api/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    return fixture;
  }

  async function renderAvailable(fixture = render()): Promise<ComponentFixture<ContactPage>> {
    httpMock
      .expectOne('/api/contact/config')
      .flush({ available: true, turnstileSiteKey: 'test-site-key' });
    await settle(fixture);
    return fixture;
  }

  function fillOutForm(
    fixture: ComponentFixture<ContactPage>,
    overrides: { website?: string } = {},
  ): void {
    const email: HTMLInputElement = fixture.nativeElement.querySelector('#contact-email');
    const message: HTMLTextAreaElement = fixture.nativeElement.querySelector('#contact-message');
    email.value = 'jane@example.com';
    email.dispatchEvent(new Event('input'));
    message.value = 'A message that is definitely long enough.';
    message.dispatchEvent(new Event('input'));

    if (overrides.website !== undefined) {
      const website: HTMLInputElement =
        fixture.nativeElement.querySelector('input[name="website"]');
      website.value = overrides.website;
      website.dispatchEvent(new Event('input'));
    }
    fixture.detectChanges();
  }

  function submitButton(fixture: ComponentFixture<ContactPage>): HTMLButtonElement {
    return fixture.nativeElement.querySelector('button[type="submit"]');
  }

  function completeTurnstile(fixture: ComponentFixture<ContactPage>, token = 'fake-token'): void {
    expect(turnstileApi.renderedOptions.length).toBeGreaterThan(0);
    turnstileApi.renderedOptions[0].callback(token);
    fixture.detectChanges();
  }

  it('shows a loading skeleton before the config request settles', async () => {
    const fixture = render();

    expect(fixture.nativeElement.querySelector('[role="status"]')).not.toBeNull();
    expect(fixture.nativeElement.querySelector('form')).toBeNull();

    httpMock.expectOne('/api/contact/config').flush({ available: false, turnstileSiteKey: null });
    await settle(fixture);
  });

  it('shows the unavailable notice and no form when the API reports available: false', async () => {
    const fixture = render();
    httpMock.expectOne('/api/contact/config').flush({ available: false, turnstileSiteKey: null });
    await settle(fixture);

    expect(fixture.nativeElement.querySelector('form')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('nicht verfügbar');
    expect(fixture.nativeElement.querySelector('a[href="/imprint"]')).not.toBeNull();
  });

  it('shows the form and renders the Turnstile widget when the API reports available: true', async () => {
    const fixture = await renderAvailable();

    expect(fixture.nativeElement.querySelector('form')).not.toBeNull();
    expect(turnstileApi.renderedOptions).toHaveLength(1);
    expect(turnstileApi.renderedOptions[0].sitekey).toBe('test-site-key');
  });

  it('keeps submit disabled until the Turnstile challenge completes', async () => {
    const fixture = await renderAvailable();
    fillOutForm(fixture);

    expect(submitButton(fixture).disabled).toBe(true);

    completeTurnstile(fixture);

    expect(submitButton(fixture).disabled).toBe(false);
  });

  it('disables submit again while the request is in flight', async () => {
    const fixture = await renderAvailable();
    fillOutForm(fixture);
    completeTurnstile(fixture);
    expect(submitButton(fixture).disabled).toBe(false);

    fixture.nativeElement
      .querySelector('form')
      .dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();

    expect(submitButton(fixture).disabled).toBe(true);

    httpMock.expectOne('/api/contact').flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);
  });

  it('passes the honeypot field value through to the outgoing request unmodified', async () => {
    const fixture = await renderAvailable();
    fillOutForm(fixture, { website: 'https://spam.example.com' });
    completeTurnstile(fixture);

    fixture.nativeElement
      .querySelector('form')
      .dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/contact');
    expect(req.request.body.website).toBe('https://spam.example.com');
    req.flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);
  });

  it('sends an empty honeypot value by default for a real visitor', async () => {
    const fixture = await renderAvailable();
    fillOutForm(fixture);
    completeTurnstile(fixture);

    fixture.nativeElement
      .querySelector('form')
      .dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/contact');
    expect(req.request.body.website).toBe('');
    req.flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);
  });

  it('shows the success message in place of the form once the request succeeds', async () => {
    const fixture = await renderAvailable();
    fillOutForm(fixture);
    completeTurnstile(fixture);

    fixture.nativeElement
      .querySelector('form')
      .dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();
    httpMock.expectOne('/api/contact').flush(null, { status: 204, statusText: 'No Content' });
    await settle(fixture);

    expect(fixture.nativeElement.querySelector('form')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Danke');
  });

  it('maps a known error code from the API to its translated message', async () => {
    const fixture = await renderAvailable();
    fillOutForm(fixture);
    completeTurnstile(fixture);

    fixture.nativeElement
      .querySelector('form')
      .dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();
    httpMock
      .expectOne('/api/contact')
      .flush({ errorCode: 'contact_captcha_failed' }, { status: 400, statusText: 'Bad Request' });
    await settle(fixture);

    expect(fixture.nativeElement.textContent).toContain(
      'Die Sicherheitsabfrage konnte nicht bestätigt werden.',
    );
    // Still on the form, not the success state.
    expect(fixture.nativeElement.querySelector('form')).not.toBeNull();
  });

  /**
   * The client-side mirror of the server's own shape check (rule 12: behaviour, not template) —
   * `ContactValidation.cs` rejects a message outside 10–5000 trimmed characters, a name over 100, and
   * an implausible e-mail; a click that would only ever come back `contact_invalid` should never leave
   * this page at all. Each case below completes the Turnstile challenge (proving the block is the
   * field, not a missing token) and checks only the disabled state — never CSS or markup, per rule 12.
   */
  describe('mirrors the server-side shape limits before allowing submit', () => {
    function setFieldValue(
      fixture: ComponentFixture<ContactPage>,
      id: string,
      value: string,
    ): void {
      const element: HTMLInputElement | HTMLTextAreaElement = fixture.nativeElement.querySelector(
        `#${id}`,
      );
      element.value = value;
      element.dispatchEvent(new Event('input'));
      fixture.detectChanges();
    }

    it('blocks a message under 10 trimmed characters', async () => {
      const fixture = await renderAvailable();
      fillOutForm(fixture);
      setFieldValue(fixture, 'contact-message', 'short');
      completeTurnstile(fixture);

      expect(submitButton(fixture).disabled).toBe(true);
    });

    it('blocks a message over 5000 characters', async () => {
      const fixture = await renderAvailable();
      fillOutForm(fixture);
      setFieldValue(fixture, 'contact-message', 'a'.repeat(5001));
      completeTurnstile(fixture);

      expect(submitButton(fixture).disabled).toBe(true);
    });

    it('blocks a name over 100 characters, even with an otherwise valid form', async () => {
      const fixture = await renderAvailable();
      fillOutForm(fixture);
      setFieldValue(fixture, 'contact-name', 'a'.repeat(101));
      completeTurnstile(fixture);

      expect(submitButton(fixture).disabled).toBe(true);
    });

    it('blocks an implausible e-mail address', async () => {
      const fixture = await renderAvailable();
      fillOutForm(fixture);
      setFieldValue(fixture, 'contact-email', 'not-an-email');
      completeTurnstile(fixture);

      expect(submitButton(fixture).disabled).toBe(true);
    });

    it('allows submit again once the field is fixed, without a fresh Turnstile challenge', async () => {
      const fixture = await renderAvailable();
      fillOutForm(fixture);
      setFieldValue(fixture, 'contact-message', 'short');
      completeTurnstile(fixture);
      expect(submitButton(fixture).disabled).toBe(true);

      setFieldValue(fixture, 'contact-message', 'A message that is definitely long enough now.');

      expect(submitButton(fixture).disabled).toBe(false);
    });
  });

  it('shows the too-short-message hint only once the field has been left, not while still typing', async () => {
    const fixture = await renderAvailable();
    const message: HTMLTextAreaElement = fixture.nativeElement.querySelector('#contact-message');
    message.value = 'short';
    message.dispatchEvent(new Event('input'));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).not.toContain('Mindestens 10 Zeichen.');

    message.dispatchEvent(new Event('blur'));
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Mindestens 10 Zeichen.');
  });

  /**
   * The other half of the same fix: a client-side block must never touch the already-completed
   * Turnstile token — only a rejection from the server does that (the existing "resets the Turnstile
   * widget after a failed submission" case below). Submitting the form directly (bypassing the
   * disabled button, the same way every other case in this file drives a submit) with an invalid
   * message must send no request and must not reset the widget.
   */
  it('does not send the request or touch the completed Turnstile token when submitted with an invalid field', async () => {
    const fixture = await renderAvailable();
    fillOutForm(fixture);
    const message: HTMLTextAreaElement = fixture.nativeElement.querySelector('#contact-message');
    message.value = 'short';
    message.dispatchEvent(new Event('input'));
    completeTurnstile(fixture);

    fixture.nativeElement
      .querySelector('form')
      .dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();

    httpMock.expectNone('/api/contact');
    expect(turnstileApi.resetWidgetIds).toEqual([]);

    // Proves the token itself is still held, not just that reset() was never called: fixing the
    // field re-enables submit without going through Turnstile again.
    message.value = 'A message that is definitely long enough now.';
    message.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(submitButton(fixture).disabled).toBe(false);
  });

  it('resets the Turnstile widget after a failed submission, re-blocking submit', async () => {
    const fixture = await renderAvailable();
    fillOutForm(fixture);
    completeTurnstile(fixture);

    fixture.nativeElement
      .querySelector('form')
      .dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();
    httpMock
      .expectOne('/api/contact')
      .flush(
        { errorCode: 'contact_unavailable' },
        { status: 503, statusText: 'Service Unavailable' },
      );
    await settle(fixture);

    expect(submitButton(fixture).disabled).toBe(true);
    expect(turnstileApi.resetWidgetIds).toEqual(['widget-1']);
  });
});
