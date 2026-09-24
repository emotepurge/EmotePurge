import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { By } from '@angular/platform-browser';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { LanguageService } from '../../core/i18n/language.service';
import { ForeignEmoteSetResponse } from '../../core/seven-tv/foreign-emote-set.model';
import { ForeignChannelStep } from './foreign-channel-step';
import { ForeignEmoteGrid } from './foreign-emote-grid';

const DE_TRANSLATIONS = {
  import: {
    clearSelection: 'Auswahl aufheben',
    foreignChannel: {
      channelLabel: 'Kanalname',
      placeholder: 'twitch-channel-name',
      invalidChannelName: 'Kein gültiger Twitch-Kanalname.',
      load: 'Set laden',
      reload: 'Neu laden',
      retry: 'Erneut versuchen',
      empty: 'Das aktive 7TV-Set dieses Kanals hat keine Emotes.',
      truncated: 'Nur ein Teil des Sets konnte geladen werden ({{ loaded }} von {{ totalCount }}).',
      selectedCount: '{{ count }} ausgewählt',
      grid: { ariaLabel: 'Emote-Auswahl' },
      sort: {
        label: 'Sortieren nach',
        none: 'Set-Reihenfolge',
        topAllTime: 'Top',
        trending: 'Trend',
        scoreHint: 'Die Zahl sagt, in wie vielen 7TV-Sets das Emote steckt.',
      },
    },
  },
  errors: {
    status: { notFound: 'Nicht gefunden.' },
  },
};

class FakeResizeObserver {
  observe(): void {
    /* no-op */
  }
  unobserve(): void {
    /* no-op */
  }
  disconnect(): void {
    /* no-op */
  }
}

function response(overrides: Partial<ForeignEmoteSetResponse> = {}): ForeignEmoteSetResponse {
  return {
    channelName: 'handofblood',
    sevenTvUserId: 'user-1',
    emoteSetId: 'set-1',
    totalCount: 1,
    truncated: false,
    emotes: [
      {
        sevenTvEmoteId: 'e1',
        name: 'catJAM',
        defaultName: 'catJAM',
        imageUrl: 'https://cdn.7tv.app/e1/4x.webp',
        topAllTime: null,
        trending: null,
      },
    ],
    ...overrides,
  };
}

describe('ForeignChannelStep', () => {
  let fixture: ComponentFixture<ForeignChannelStep>;
  let component: ForeignChannelStep;
  let host: HTMLElement;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);

    await TestBed.configureTestingModule({
      imports: [
        ForeignChannelStep,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: LanguageService,
          useValue: { lang: signal('de') } as unknown as LanguageService,
        },
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));

    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(ForeignChannelStep);
    component = fixture.componentInstance;
    host = fixture.nativeElement;
    fixture.detectChanges();
  });

  function button(label: string): HTMLButtonElement {
    const found = Array.from(host.querySelectorAll('button')).find(
      (candidate) => candidate.textContent?.trim() === label,
    );
    if (!found) {
      throw new Error(`no button labelled "${label}"`);
    }
    return found;
  }

  function grid(): ForeignEmoteGrid {
    return fixture.debugElement.query(By.directive(ForeignEmoteGrid))
      .componentInstance as ForeignEmoteGrid;
  }

  it('rejects an obviously invalid channel name locally, without making a request', () => {
    component['channelNameControl'].setValue('ab');
    component['submit']();
    fixture.detectChanges();

    httpMock.expectNone(() => true);
    expect(host.textContent).toContain('Kein gültiger Twitch-Kanalname.');
  });

  it('loads the channel and renders its set once the request resolves', () => {
    component['channelNameControl'].setValue('HandOfBlood');
    component['submit']();
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/seventv/channels/handofblood/emotes');
    expect(req.request.method).toBe('GET');
    req.flush(response());
    fixture.detectChanges();

    expect(host.textContent).toContain('#handofblood');
    expect(host.querySelector('app-foreign-emote-grid')).not.toBeNull();
  });

  it('surfaces a load failure and offers a retry that re-issues the request', () => {
    component['channelNameControl'].setValue('unknownchannel');
    component['submit']();
    fixture.detectChanges();

    httpMock
      .expectOne('/api/seventv/channels/unknownchannel/emotes')
      .flush({ errorCode: 'not_a_known_code' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(host.textContent).toContain('Nicht gefunden.');

    button('Erneut versuchen').click();
    httpMock.expectOne('/api/seventv/channels/unknownchannel/emotes').flush(response());
  });

  it('reload bypasses the cache via refresh=true', () => {
    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(response());
    fixture.detectChanges();

    button('Neu laden').click();

    const refreshReq = httpMock.expectOne(
      (candidate) =>
        candidate.url === '/api/seventv/channels/handofblood/emotes' &&
        candidate.params.get('refresh') === 'true',
    );
    refreshReq.flush(response());
  });

  it('reports no result until a loaded set has a selection, then the picked rows', () => {
    expect(component.result()).toBeNull();

    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(response());
    fixture.detectChanges();

    // Loaded but nothing marked — still nothing to carry forward.
    expect(component.result()).toBeNull();

    const picked = response().emotes;
    component['onSelectionChange'](picked);
    fixture.detectChanges();

    expect(component.result()).toEqual({
      channelName: 'handofblood',
      sevenTvUserId: 'user-1',
      emoteSetId: 'set-1',
      rows: picked,
    });
  });

  it("locks the result again once the grid's clear-selection button empties the pick", () => {
    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(response());
    fixture.detectChanges();

    component['onSelectionChange'](response().emotes);
    fixture.detectChanges();
    expect(component.result()).not.toBeNull();

    // The same method the grid's button calls.
    grid()['clearSelection']();
    fixture.detectChanges();

    expect(component.result()).toBeNull();
  });

  it('drops a selection made against the previous channel when a new query starts', () => {
    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emotes').flush(response());
    component['onSelectionChange'](response().emotes);
    expect(component.result()).not.toBeNull();

    component['channelNameControl'].setValue('otherchannel');
    component['submit']();

    // A selection of one channel's 7TV ids has no honest meaning in another channel's set.
    expect(component.result()).toBeNull();
    httpMock.expectOne('/api/seventv/channels/otherchannel/emotes').flush(response());
  });

  it('labels the channel field visibly, not only through the placeholder (Codex P3)', () => {
    const input = host.querySelector('input[type="text"]');
    const label = host.querySelector('label');

    expect(input?.getAttribute('id')).toBeTruthy();
    expect(label?.getAttribute('for')).toBe(input?.getAttribute('id'));
    expect(label?.textContent?.trim()).toBe('Kanalname');
  });

  it('wires the field-error text to the input while it is showing', () => {
    component['channelNameControl'].setValue('ab');
    component['submit']();
    fixture.detectChanges();

    const input = host.querySelector('input[type="text"]');
    const describedBy = input?.getAttribute('aria-describedby');
    expect(input?.getAttribute('aria-invalid')).toBe('true');
    expect(describedBy).not.toBeNull();
    expect(host.querySelector(`#${describedBy}`)?.textContent).toContain(
      'Kein gültiger Twitch-Kanalname.',
    );
  });
});
