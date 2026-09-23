import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { LanguageService } from '../../core/i18n/language.service';
import { LegalPage } from './legal-page';

// Only the keys LegalPage's own template and its two embedded primitives (BackLink, AccountMenu)
// translate. AccountMenu's own decision logic already has its full coverage in
// account-menu.spec.ts — this file only needs enough of its vocabulary for it to render without
// throwing, never for its own sake.
const DE_TRANSLATIONS = {
  nav: { backTo: 'Zurück zu {{target}}' },
  legal: {
    back: 'Startseite',
    onlyGerman: 'Dieser Text liegt aktuell nur auf Deutsch vor.',
    imprint: { notConfiguredTitle: 'Kein Impressum hinterlegt' },
    privacy: { notConfiguredTitle: 'Keine Datenschutzerklärung hinterlegt' },
    notConfigured: { description: 'Der Betreiber hat diesen Text noch nicht hinterlegt.' },
  },
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
};

const EN_TRANSLATIONS = {
  ...DE_TRANSLATIONS,
  legal: {
    ...DE_TRANSLATIONS.legal,
    onlyGerman: 'This document is currently only available in German.',
  },
};

/**
 * `LegalPage`'s own decision logic (rule 12): which document it requests for the current `kind` +
 * `LanguageService.lang()`, how a settled response (real content vs. the German-fallback flag vs.
 * "not configured") turns into what's on screen, and the loading state in between. Reuses
 * `account-menu.spec.ts`'s provider setup, since `AccountMenu` renders unconditionally in this
 * page's header and fires its own `/api/auth/me` request on construction — its own behaviour is
 * not re-asserted here, only drained so it doesn't leave an unflushed request behind.
 */
describe('LegalPage', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    localStorage.clear();
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('de-DE');

    await TestBed.configureTestingModule({
      imports: [
        LegalPage,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS, en: EN_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.restoreAllMocks();
    localStorage.clear();
    httpMock.verify();
  });

  /** Renders with the given kind and drains the two requests every render fires that this suite
   *  is not about: AccountMenu's /api/auth/me and LegalService's /api/legal/availability (LegalPage
   *  injects LegalService for getDocument(), which fires its availability GET on construction). */
  function render(kind: 'imprint' | 'privacy'): ComponentFixture<LegalPage> {
    const fixture = TestBed.createComponent(LegalPage);
    fixture.componentRef.setInput('kind', kind);
    fixture.detectChanges();

    httpMock.expectOne('/api/auth/me').flush(null);
    httpMock
      .expectOne('/api/legal/availability')
      .flush({ imprintAvailable: false, privacyAvailable: false });
    fixture.detectChanges();

    return fixture;
  }

  function heading(fixture: ComponentFixture<LegalPage>): string | null {
    return fixture.nativeElement.querySelector('h1')?.textContent ?? null;
  }

  /** `rxResource` settles `value()`/`status()` through a microtask rather than synchronously
   *  inside `TestRequest.flush()` (same finding as `vote-session-detail-page.spec.ts`'s `mount()`
   *  helper) — a zero-delay `setTimeout` round-trip drains it before the next `detectChanges()`. */
  async function settle(fixture: ComponentFixture<LegalPage>): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();
  }

  /** The loading skeleton names itself only via `aria-label` (its content is decorative shimmer
   *  bars); the German-fallback notice names itself via its projected, visible text. Checking both
   *  per `role="status"` element covers each without the test needing to know which is which. */
  function statusText(fixture: ComponentFixture<LegalPage>): string[] {
    const elements: Element[] = Array.from(
      fixture.nativeElement.querySelectorAll('[role="status"]'),
    );
    return elements.map(
      (el) => `${el.getAttribute('aria-label') ?? ''} ${el.textContent?.trim() ?? ''}`,
    );
  }

  it('shows a loading state before the document request settles', async () => {
    const fixture = render('imprint');

    // Not yet flushed: the resource is still in flight.
    expect(statusText(fixture).some((text) => text.includes('Lädt'))).toBe(true);
    expect(heading(fixture)).toBeNull();

    httpMock
      .expectOne('/api/legal/imprint/de')
      .flush({ html: '<h1>Impressum</h1>', isGermanFallback: false });
    await settle(fixture);
  });

  it('requests the German document by default and renders its content once it arrives', async () => {
    const fixture = render('imprint');

    const req = httpMock.expectOne('/api/legal/imprint/de');
    expect(req.request.method).toBe('GET');
    req.flush({ html: '<h1>Impressum</h1><p>Testinhalt.</p>', isGermanFallback: false });
    await settle(fixture);

    expect(heading(fixture)).toBe('Impressum');
    expect(fixture.nativeElement.textContent).toContain('Testinhalt.');
    expect(statusText(fixture).some((text) => text.includes('nur auf Deutsch'))).toBe(false);
  });

  it('requests the other document when kind changes, without remounting the component', async () => {
    const fixture = render('imprint');
    httpMock
      .expectOne('/api/legal/imprint/de')
      .flush({ html: '<h1>Impressum</h1>', isGermanFallback: false });
    await settle(fixture);
    expect(heading(fixture)).toBe('Impressum');

    fixture.componentRef.setInput('kind', 'privacy');
    fixture.detectChanges();

    httpMock
      .expectOne('/api/legal/privacy/de')
      .flush({ html: '<h1>Datenschutz</h1>', isGermanFallback: false });
    await settle(fixture);

    expect(heading(fixture)).toBe('Datenschutz');
  });

  it('re-requests the document in the new language when the app locale changes, and shows the German-fallback notice', async () => {
    const fixture = render('privacy');
    httpMock
      .expectOne('/api/legal/privacy/de')
      .flush({ html: '<h1>Datenschutz</h1>', isGermanFallback: false });
    await settle(fixture);

    TestBed.inject(LanguageService).setLang('en');
    fixture.detectChanges();

    // The German-fallback flag: the backend served German text because no English file exists,
    // and the frontend has to say so rather than silently mixing languages.
    httpMock
      .expectOne('/api/legal/privacy/en')
      .flush({ html: '<h1>Datenschutz</h1>', isGermanFallback: true });
    await settle(fixture);

    expect(heading(fixture)).toBe('Datenschutz');
    expect(statusText(fixture).some((text) => text.includes('only available in German'))).toBe(
      true,
    );
  });

  it('shows no fallback notice when the requested language document exists for real', async () => {
    const fixture = render('privacy');

    httpMock
      .expectOne('/api/legal/privacy/de')
      .flush({ html: '<h1>Datenschutz</h1>', isGermanFallback: false });
    await settle(fixture);

    expect(statusText(fixture).some((text) => text.includes('nur auf Deutsch'))).toBe(false);
  });

  it('shows the kind-specific "not configured" empty state on a 404, instead of the document area', async () => {
    const fixture = render('imprint');

    httpMock
      .expectOne('/api/legal/imprint/de')
      .flush({ errorCode: 'legal_document_not_found' }, { status: 404, statusText: 'Not Found' });
    await settle(fixture);

    expect(heading(fixture)).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Kein Impressum hinterlegt');
  });

  it('picks the privacy-specific empty-state title for kind "privacy"', async () => {
    const fixture = render('privacy');

    httpMock
      .expectOne('/api/legal/privacy/de')
      .flush({ errorCode: 'legal_document_not_found' }, { status: 404, statusText: 'Not Found' });
    await settle(fixture);

    expect(fixture.nativeElement.textContent).toContain('Keine Datenschutzerklärung hinterlegt');
  });

  it('treats a failed request the same as "not configured" rather than showing a broken page', async () => {
    const fixture = render('imprint');

    httpMock
      .expectOne('/api/legal/imprint/de')
      .flush(null, { status: 500, statusText: 'Server Error' });
    await settle(fixture);

    expect(heading(fixture)).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('Kein Impressum hinterlegt');
  });
});
