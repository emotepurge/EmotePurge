import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { LanguageService, resolveInitialLang } from './language.service';

function setBrowserLanguage(value: string): void {
  vi.spyOn(navigator, 'language', 'get').mockReturnValue(value);
}

describe('resolveInitialLang', () => {
  beforeEach(() => localStorage.clear());
  afterEach(() => vi.restoreAllMocks());

  it('prefers the stored preference over the browser language', () => {
    localStorage.setItem('ep_lang', 'en');
    setBrowserLanguage('de-DE');

    expect(resolveInitialLang()).toBe('en');
  });

  it('falls back to the browser language when nothing is stored', () => {
    setBrowserLanguage('en-GB');

    expect(resolveInitialLang()).toBe('en');
  });

  it('falls back to German for an unsupported browser language', () => {
    // German, not English: the primary audience is a German-speaking Twitch community.
    setBrowserLanguage('fr-FR');

    expect(resolveInitialLang()).toBe('de');
  });

  it('ignores a stored value that is not a supported language', () => {
    // Anyone can hand-edit localStorage; a junk value must not become the active language.
    localStorage.setItem('ep_lang', 'klingon');
    setBrowserLanguage('en-US');

    expect(resolveInitialLang()).toBe('en');
  });
});

describe('LanguageService', () => {
  let service: LanguageService;
  let transloco: TranslocoService;

  beforeEach(() => {
    localStorage.clear();
    setBrowserLanguage('de-DE');
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {}, en: {} },
          translocoConfig: { availableLangs: ['de', 'en'], defaultLang: 'de' },
        }),
      ],
    });
    service = TestBed.inject(LanguageService);
    transloco = TestBed.inject(TranslocoService);
  });

  afterEach(() => vi.restoreAllMocks());

  it('sets the document language on construction', () => {
    expect(document.documentElement.lang).toBe('de');
  });

  it('setLang updates the signal, Transloco, the document and localStorage together', () => {
    service.setLang('en');

    expect(service.lang()).toBe('en');
    expect(transloco.getActiveLang()).toBe('en');
    // <html lang> is what screen readers switch pronunciation on — it must not drift from the
    // language actually being rendered.
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem('ep_lang')).toBe('en');
  });

  it('persists the choice so the next visit skips the browser-language guess', () => {
    service.setLang('en');

    expect(resolveInitialLang()).toBe('en');
  });

  it('sets <html lang> and the stored preference immediately, but defers the signal and Transloco until the locale has loaded', () => {
    const load$ = new Subject<Record<string, never>>();
    vi.spyOn(transloco, 'load').mockReturnValue(load$);

    service.setLang('en');

    // Announced and persisted right away, even though nothing has loaded yet.
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem('ep_lang')).toBe('en');
    // The signal and Transloco's own active language must not jump ahead of the loaded table.
    expect(service.lang()).toBe('de');
    expect(transloco.getActiveLang()).toBe('de');

    load$.next({});
    load$.complete();

    expect(service.lang()).toBe('en');
    expect(transloco.getActiveLang()).toBe('en');
  });

  it('reverts <html lang> and the stored preference to the rendered language when the load fails', () => {
    const load$ = new Subject<Record<string, never>>();
    vi.spyOn(transloco, 'load').mockReturnValue(load$);

    service.setLang('en');
    load$.error(new Error('network error'));

    expect(service.lang()).toBe('de');
    expect(transloco.getActiveLang()).toBe('de');
    // <html lang> must not announce a language with nothing behind it, and the stored preference
    // must not make the next boot pick the failed language again.
    expect(document.documentElement.lang).toBe('de');
    expect(localStorage.getItem('ep_lang')).toBe('de');
  });

  it('selectedLang updates immediately on setLang, before the load resolves', () => {
    const load$ = new Subject<Record<string, never>>();
    vi.spyOn(transloco, 'load').mockReturnValue(load$);

    service.setLang('en');

    expect(service.selectedLang()).toBe('en');
    // Unlike selectedLang, lang() itself only flips once the load actually resolves.
    expect(service.lang()).toBe('de');
  });

  it('selectedLang reverts to the rendered language when the load fails', () => {
    const load$ = new Subject<Record<string, never>>();
    vi.spyOn(transloco, 'load').mockReturnValue(load$);

    service.setLang('en');
    load$.error(new Error('network error'));

    expect(service.selectedLang()).toBe('de');
  });

  it('a superseded failure does not revert selectedLang to the older rendered language', () => {
    const loads = {
      de: new Subject<Record<string, never>>(),
      en: new Subject<Record<string, never>>(),
    };
    vi.spyOn(transloco, 'load').mockImplementation(
      (...args: Parameters<typeof transloco.load>) => loads[args[0] as 'de' | 'en'],
    );

    service.setLang('de');
    service.setLang('en');
    loads.de.error(new Error('network error'));

    // The en pick is still the current selection: the stale de failure must not stomp it.
    expect(service.selectedLang()).toBe('en');
  });

  it('selectedLang equals lang() once a switch succeeds', () => {
    service.setLang('en');

    expect(service.selectedLang()).toBe('en');
    expect(service.selectedLang()).toBe(service.lang());
  });

  it('a stale failure from a request a later pick has already superseded does not revert that later pick, even when both requested the same language', () => {
    // Two independent loads for 'en': the first (A) never resolves, the second (C) is a separate
    // request started after a 'de' switch resolved in between. Comparing selectedLang's *value*
    // against 'en' cannot tell A and C apart — only comparing request identity can.
    const loadA$ = new Subject<Record<string, never>>();
    const loadC$ = new Subject<Record<string, never>>();
    const originalLoad = transloco.load.bind(transloco);
    let enLoadCount = 0;
    vi.spyOn(transloco, 'load').mockImplementation((...args: Parameters<typeof transloco.load>) => {
      if (args[0] !== 'en') {
        return originalLoad(...args);
      }
      enLoadCount += 1;
      return enLoadCount === 1 ? loadA$ : loadC$;
    });

    service.setLang('en'); // load A pending
    service.setLang('de'); // resolves synchronously (already-loaded language)
    service.setLang('en'); // load C pending, independent of A

    loadA$.error(new Error('network error'));

    // A is stale: its failure must not revert the pick (C) that superseded it.
    expect(service.selectedLang()).toBe('en');
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem('ep_lang')).toBe('en');

    loadC$.next({});
    loadC$.complete();

    expect(service.lang()).toBe('en');
    expect(transloco.getActiveLang()).toBe('en');
  });

  it('ignores a failed load that a later switch has already superseded (de fails while en is pending)', () => {
    const loads = {
      de: new Subject<Record<string, never>>(),
      en: new Subject<Record<string, never>>(),
    };
    vi.spyOn(transloco, 'load').mockImplementation(
      (...args: Parameters<typeof transloco.load>) => loads[args[0] as 'de' | 'en'],
    );

    service.setLang('de');
    service.setLang('en');
    loads.de.error(new Error('network error'));

    // The en switch is still the one in flight: a revert to the rendered de would undo it.
    expect(document.documentElement.lang).toBe('en');
    expect(localStorage.getItem('ep_lang')).toBe('en');
    expect(service.lang()).toBe('de');
  });

  it('does not let a late-arriving load overwrite a language picked again in the meantime (de -> en -> de)', () => {
    const enLoad$ = new Subject<Record<string, never>>();
    const originalLoad = transloco.load.bind(transloco);
    vi.spyOn(transloco, 'load').mockImplementation((...args: Parameters<typeof transloco.load>) =>
      args[0] === 'en' ? enLoad$ : originalLoad(...args),
    );

    service.setLang('en');
    service.setLang('de');

    // The de switch resolves instantly (it is the already-loaded, currently active language), so
    // it must win even though it started after the still-pending en request.
    expect(service.lang()).toBe('de');
    expect(transloco.getActiveLang()).toBe('de');

    // The stale en response must be ignored once it does arrive.
    enLoad$.next({});
    enLoad$.complete();

    expect(service.lang()).toBe('de');
    expect(transloco.getActiveLang()).toBe('de');
  });
});
