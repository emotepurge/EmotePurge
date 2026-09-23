import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { LegalFooterLinks } from './legal-footer-links';

const DE_TRANSLATIONS = {
  legal: {
    footer: {
      imprint: 'Impressum',
      privacy: 'Datenschutz',
    },
  },
};

describe('LegalFooterLinks', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        LegalFooterLinks,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  function render() {
    const fixture = TestBed.createComponent(LegalFooterLinks);
    fixture.detectChanges();
    return fixture;
  }

  function flushAvailability(imprintAvailable: boolean, privacyAvailable: boolean): void {
    httpMock.expectOne('/api/legal/availability').flush({ imprintAvailable, privacyAvailable });
  }

  it('shows no link before the availability response has arrived', () => {
    const fixture = render();

    expect(fixture.nativeElement.querySelectorAll('a')).toHaveLength(0);

    // Drained rather than left open: httpMock.verify() in afterEach requires every request the
    // component fired to be settled, and LegalService always fires one on construction.
    flushAvailability(false, false);
  });

  it('renders only the imprint link when only the imprint is configured', () => {
    const fixture = render();
    flushAvailability(true, false);
    fixture.detectChanges();

    const links: HTMLAnchorElement[] = Array.from(fixture.nativeElement.querySelectorAll('a'));
    expect(links).toHaveLength(1);
    expect(links[0].getAttribute('href')).toBe('/imprint');
    expect(links[0].textContent?.trim()).toBe('Impressum');
  });

  it('renders both links when both documents are configured', () => {
    const fixture = render();
    flushAvailability(true, true);
    fixture.detectChanges();

    const links: HTMLAnchorElement[] = Array.from(fixture.nativeElement.querySelectorAll('a'));
    expect(links.map((a) => a.getAttribute('href'))).toEqual(['/imprint', '/privacy']);
    expect(links.map((a) => a.textContent?.trim())).toEqual(['Impressum', 'Datenschutz']);
  });

  it('renders nothing when neither document is configured', () => {
    const fixture = render();
    flushAvailability(false, false);
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelectorAll('a')).toHaveLength(0);
  });
});
