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
import { EmoteSetListResponse } from '../../core/seven-tv/seven-tv-emote-set.model';
import { ForeignChannelStep } from './foreign-channel-step';
import { ForeignEmoteGrid } from './foreign-emote-grid';

const DE_TRANSLATIONS = {
  import: {
    clearSelection: 'Auswahl aufheben',
    animated: 'animiert',
    foreignChannel: {
      channelLabel: 'Kanalname',
      placeholder: 'z. B. handofblood',
      invalidChannelName: 'Kein gültiger Twitch-Kanalname.',
      load: 'Set laden',
      reload: 'Neu laden',
      setsLabel: 'Quell-Set',
      active: 'aktiv',
      kindPersonal: 'persönliches Set',
      kindUnavailable: 'kein Quellset',
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

function setsResponse(overrides: Partial<EmoteSetListResponse> = {}): EmoteSetListResponse {
  return {
    activeEmoteSetId: 'set-1',
    sets: [
      {
        id: 'set-1',
        name: 'Hauptset',
        capacity: 250,
        kind: 'NORMAL',
        isActive: true,
        isPersonal: false,
        ownerDisplayName: 'Owner',
        observations: [],
      },
    ],
    ...overrides,
  };
}

function previewResponse(
  overrides: Partial<ForeignEmoteSetResponse> = {},
): ForeignEmoteSetResponse {
  return {
    channelName: 'handofblood',
    sevenTvUserId: null,
    emoteSetId: 'set-1',
    emoteSetName: 'Hauptset',
    capacity: 250,
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

  /** Drives the two-request pipeline (6.3 set list, then 6.4 preview of the active set) to a
   *  fully loaded state — the shared setup almost every test below starts from. */
  function loadChannel(channelName = 'handofblood', sets = setsResponse()): void {
    component['channelNameControl'].setValue(channelName);
    component['submit']();
    httpMock.expectOne(`/api/seventv/channels/${channelName}/emote-sets`).flush(sets);
    fixture.detectChanges();
    httpMock
      .expectOne(
        (req) =>
          req.url === `/api/seventv/channels/${channelName}/emotes` &&
          req.params.get('emoteSetId') === sets.activeEmoteSetId,
      )
      .flush(previewResponse({ emoteSetId: sets.activeEmoteSetId, channelName }));
    fixture.detectChanges();
  }

  it('rejects an obviously invalid channel name locally, without making a request', () => {
    component['channelNameControl'].setValue('ab');
    component['submit']();
    fixture.detectChanges();

    httpMock.expectNone(() => true);
    expect(host.textContent).toContain('Kein gültiger Twitch-Kanalname.');
  });

  it('loads the channel and renders its set once both requests resolve', () => {
    loadChannel();

    expect(host.textContent).toContain('#handofblood');
    expect(host.querySelector('app-foreign-emote-grid')).not.toBeNull();
  });

  it('surfaces a set-list load failure and offers a retry that re-issues the request', () => {
    component['channelNameControl'].setValue('unknownchannel');
    component['submit']();
    fixture.detectChanges();

    httpMock
      .expectOne('/api/seventv/channels/unknownchannel/emote-sets')
      .flush({ errorCode: 'not_a_known_code' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(host.textContent).toContain('Nicht gefunden.');

    button('Erneut versuchen').click();
    httpMock.expectOne('/api/seventv/channels/unknownchannel/emote-sets').flush(setsResponse());
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/unknownchannel/emotes' &&
          req.params.get('emoteSetId') === 'set-1',
      )
      .flush(previewResponse());
  });

  it('surfaces a preview load failure without losing the already-resolved radiogroup, and retries only the preview', () => {
    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emote-sets').flush(
      setsResponse({
        sets: [
          setsResponse().sets[0],
          {
            id: 'set-2',
            name: 'Zweitset',
            capacity: 250,
            kind: 'NORMAL',
            isActive: false,
            isPersonal: false,
            ownerDisplayName: 'Owner',
            observations: [],
          },
        ],
      }),
    );
    fixture.detectChanges();
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-1',
      )
      .flush({ errorCode: 'not_a_known_code' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    // The radiogroup survived the preview failure — only the grid area shows the error.
    expect(host.querySelector('[role="radiogroup"]')).not.toBeNull();
    expect(host.textContent).toContain('Nicht gefunden.');

    button('Erneut versuchen').click();
    const retryReq = httpMock.expectOne(
      (req) =>
        req.url === '/api/seventv/channels/handofblood/emotes' &&
        req.params.get('emoteSetId') === 'set-1',
    );
    expect(retryReq.request.params.get('refresh')).toBeNull();
    retryReq.flush(previewResponse());
  });

  it('reload bypasses the preview cache via refresh=true, without re-fetching the set list', () => {
    loadChannel();

    button('Neu laden').click();

    const refreshReq = httpMock.expectOne(
      (candidate) =>
        candidate.url === '/api/seventv/channels/handofblood/emotes' &&
        candidate.params.get('emoteSetId') === 'set-1' &&
        candidate.params.get('refresh') === 'true',
    );
    refreshReq.flush(previewResponse());
    httpMock.expectNone('/api/seventv/channels/handofblood/emote-sets');
  });

  it('reports no result until a loaded preview has a selection, then the picked rows', () => {
    expect(component.result()).toBeNull();

    loadChannel();

    // Loaded but nothing marked — still nothing to carry forward.
    expect(component.result()).toBeNull();

    const picked = previewResponse().emotes;
    component['onSelectionChange'](picked);
    fixture.detectChanges();

    expect(component.result()).toEqual({
      channelName: 'handofblood',
      sevenTvUserId: null,
      emoteSetId: 'set-1',
      rows: picked,
    });
  });

  it("locks the result again once the grid's clear-selection button empties the pick", () => {
    loadChannel();

    component['onSelectionChange'](previewResponse().emotes);
    fixture.detectChanges();
    expect(component.result()).not.toBeNull();

    // The same method the grid's button calls.
    grid()['clearSelection']();
    fixture.detectChanges();

    expect(component.result()).toBeNull();
  });

  it('drops a selection made against the previous channel when a new query starts', () => {
    loadChannel();
    component['onSelectionChange'](previewResponse().emotes);
    expect(component.result()).not.toBeNull();

    component['channelNameControl'].setValue('otherchannel');
    component['submit']();

    // A selection of one channel's 7TV ids has no honest meaning in another channel's set.
    expect(component.result()).toBeNull();
    httpMock.expectOne('/api/seventv/channels/otherchannel/emote-sets').flush(setsResponse());
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/otherchannel/emotes' &&
          req.params.get('emoteSetId') === 'set-1',
      )
      .flush(previewResponse({ channelName: 'otherchannel' }));
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

  // K3 (spec 8.7, AK 47-49): the source-set radiogroup.

  it('shows no radiogroup at all when the account has only one set — nothing to pick between', () => {
    loadChannel();

    expect(host.querySelector('[role="radiogroup"]')).toBeNull();
    expect(host.querySelectorAll('input[type="radio"]')).toHaveLength(0);
  });

  it('shows a radiogroup with the active set preselected and labelled, and a non-NORMAL set disabled and labelled', () => {
    loadChannel(
      'handofblood',
      setsResponse({
        activeEmoteSetId: 'set-1',
        sets: [
          {
            id: 'set-1',
            name: 'Hauptset',
            capacity: 250,
            kind: 'NORMAL',
            isActive: true,
            isPersonal: false,
            ownerDisplayName: 'Owner',
            observations: [],
          },
          {
            id: 'set-2',
            name: 'Persönlich',
            capacity: 5,
            kind: 'PERSONAL',
            isActive: false,
            isPersonal: true,
            ownerDisplayName: 'Owner',
            observations: [],
          },
        ],
      }),
    );

    const group = host.querySelector('[role="radiogroup"]');
    expect(group).not.toBeNull();
    expect(group?.getAttribute('aria-label')).toBe('Quell-Set');

    const radios = Array.from(host.querySelectorAll('input[type="radio"]')) as HTMLInputElement[];
    expect(radios).toHaveLength(2);
    expect(radios[0].checked).toBe(true);
    expect(radios[0].disabled).toBe(false);
    expect(radios[1].checked).toBe(false);
    expect(radios[1].disabled).toBe(true);
    expect(host.textContent).toContain('Hauptset');
    expect(host.textContent).toContain('aktiv');
    expect(host.textContent).toContain('persönliches Set');
  });

  it('keeps the active set selected without a second preview request (AK 48)', () => {
    loadChannel(
      'handofblood',
      setsResponse({
        sets: [
          setsResponse().sets[0],
          {
            id: 'set-2',
            name: 'Zweitset',
            capacity: 250,
            kind: 'NORMAL',
            isActive: false,
            isPersonal: false,
            ownerDisplayName: 'Owner',
            observations: [],
          },
        ],
      }),
    );

    // Re-clicking the already-selected (active) radio must not fire a new HTTP request at all.
    const activeRadio = host.querySelectorAll('input[type="radio"]')[0] as HTMLInputElement;
    activeRadio.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    httpMock.expectNone(() => true);
  });

  it('switching the radiogroup selection reloads the preview for the newly picked set and carries its id into the result (AK 49)', () => {
    loadChannel(
      'handofblood',
      setsResponse({
        sets: [
          setsResponse().sets[0],
          {
            id: 'set-2',
            name: 'Zweitset',
            capacity: 250,
            kind: 'NORMAL',
            isActive: false,
            isPersonal: false,
            ownerDisplayName: 'Owner',
            observations: [],
          },
        ],
      }),
    );
    component['onSelectionChange'](previewResponse().emotes);
    expect(component.result()).not.toBeNull();

    component['selectSet']('set-2');
    fixture.detectChanges();

    // A different set's rows have no honest meaning carried over — same as a fresh channel query.
    expect(component.result()).toBeNull();

    const switchReq = httpMock.expectOne(
      (req) =>
        req.url === '/api/seventv/channels/handofblood/emotes' &&
        req.params.get('emoteSetId') === 'set-2',
    );
    switchReq.flush(
      previewResponse({
        emoteSetId: 'set-2',
        emotes: [
          {
            sevenTvEmoteId: 'e2',
            name: 'PogU',
            defaultName: 'PogU',
            imageUrl: 'https://cdn.7tv.app/e2/4x.webp',
            topAllTime: null,
            trending: null,
          },
        ],
      }),
    );
    fixture.detectChanges();

    component['onSelectionChange']([
      {
        sevenTvEmoteId: 'e2',
        name: 'PogU',
        defaultName: 'PogU',
        imageUrl: 'https://cdn.7tv.app/e2/4x.webp',
        topAllTime: null,
        trending: null,
      },
    ]);
    fixture.detectChanges();

    expect(component.result()?.emoteSetId).toBe('set-2');
  });
});
