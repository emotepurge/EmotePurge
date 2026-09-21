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
      noActiveSet: 'Kein aktives Set.',
      kindUnavailable: 'kein Quellset',
      retry: 'Erneut versuchen',
      empty: 'Dieses Set hat keine Emotes.',
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

  it('always shows a radiogroup, even with exactly one set — active preselected and labelled (spec addendum 2026-09-21)', () => {
    loadChannel();

    const group = host.querySelector('[role="radiogroup"]');
    expect(group).not.toBeNull();
    expect(group?.getAttribute('aria-label')).toBe('Quell-Set');

    const radios = Array.from(host.querySelectorAll('input[type="radio"]')) as HTMLInputElement[];
    expect(radios).toHaveLength(1);
    expect(radios[0].checked).toBe(true);
    expect(host.textContent).toContain('Hauptset');
    expect(host.textContent).toContain('aktiv');
  });

  it('hides a PERSONAL set from the radiogroup entirely, not merely disabled (spec addendum 2026-09-21)', () => {
    loadChannel(
      'handofblood',
      setsResponse({
        activeEmoteSetId: 'set-1',
        sets: [
          setsResponse().sets[0],
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

    // Not just "disabled" — absent. No radio, no name, no label anywhere in the step.
    const radios = Array.from(host.querySelectorAll('input[type="radio"]')) as HTMLInputElement[];
    expect(radios).toHaveLength(1);
    expect(radios[0].checked).toBe(true);
    expect(host.textContent).not.toContain('Persönlich');
  });

  it('still shows a non-NORMAL, non-PERSONAL set disabled and labelled — 8.6 unchanged for GLOBAL/SPECIAL', () => {
    loadChannel(
      'handofblood',
      setsResponse({
        activeEmoteSetId: 'set-1',
        sets: [
          setsResponse().sets[0],
          {
            id: 'set-2',
            name: 'Globales Set',
            capacity: 1000,
            kind: 'GLOBAL',
            isActive: false,
            isPersonal: false,
            ownerDisplayName: 'Owner',
            observations: [],
          },
        ],
      }),
    );

    const radios = Array.from(host.querySelectorAll('input[type="radio"]')) as HTMLInputElement[];
    expect(radios).toHaveLength(2);
    expect(radios[0].disabled).toBe(false);
    expect(radios[1].disabled).toBe(true);
    expect(host.textContent).toContain('Globales Set');
    expect(host.textContent).toContain('kein Quellset');
  });

  // P2-2 (K3 review finding): no active set must not dead-end the step.

  it('preselects the lone selectable set when 7TV reports no active set at all', () => {
    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emote-sets').flush(
      setsResponse({
        activeEmoteSetId: '',
        sets: [
          setsResponse().sets[0],
          {
            id: 'set-global',
            name: 'Globales Set',
            capacity: 1000,
            kind: 'GLOBAL',
            isActive: false,
            isPersonal: false,
            ownerDisplayName: 'Owner',
            observations: [],
          },
        ],
      }),
    );
    fixture.detectChanges();

    // Two sets render (the radiogroup shows whenever there is more than one) — only the lone
    // NORMAL one is a real choice, and it is the one auto-picked; the non-selectable GLOBAL set
    // never is, active-set-report or not.
    const radios = Array.from(host.querySelectorAll('input[type="radio"]')) as HTMLInputElement[];
    expect(radios).toHaveLength(2);
    expect(radios[0].checked).toBe(true);
    expect(radios[1].checked).toBe(false);

    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-1',
      )
      .flush(previewResponse());
    fixture.detectChanges();

    expect(host.querySelector('app-foreign-emote-grid')).not.toBeNull();
  });

  it('shows a notice and preselects nothing when there is no active set and more than one selectable set', () => {
    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emote-sets').flush(
      setsResponse({
        activeEmoteSetId: '',
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

    // No preview request at all — nothing was auto-picked.
    httpMock.expectNone(() => true);
    const radios = Array.from(host.querySelectorAll('input[type="radio"]')) as HTMLInputElement[];
    expect(radios.some((radio) => radio.checked)).toBe(false);
    expect(host.querySelector('app-foreign-emote-grid')).toBeNull();
    expect(host.textContent).toContain('Kein aktives Set.');
  });

  it('treats a PERSONAL active set exactly like no active set at all', () => {
    component['channelNameControl'].setValue('handofblood');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/handofblood/emote-sets').flush(
      setsResponse({
        activeEmoteSetId: 'set-personal',
        sets: [
          {
            id: 'set-personal',
            name: 'Persönlich',
            capacity: 5,
            kind: 'PERSONAL',
            isActive: true,
            isPersonal: true,
            ownerDisplayName: 'Owner',
            observations: [],
          },
        ],
      }),
    );
    fixture.detectChanges();

    // PERSONAL is hidden from the radiogroup entirely, so there is nothing left to pick between —
    // and, being the only set, nothing to auto-preselect either.
    httpMock.expectNone(() => true);
    expect(host.querySelector('[role="radiogroup"]')).toBeNull();
    expect(host.querySelector('app-foreign-emote-grid')).toBeNull();
    expect(host.textContent).toContain('Kein aktives Set.');
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

  // P3-4/P3-5 (K3 review): a stale preview response must never overwrite a fresher one — including
  // when both requests target the very same set id, which comparing `selectedEmoteSetId` alone
  // cannot tell apart.

  it('ignores a stale preview response for a set the user has already left, arriving after the newer pick resolved (P3-4)', () => {
    // Three sets, not two: B must be a set that was never loaded before, or the K3 follow-up fix's
    // cache would answer pick B from that cache instead of a real second request, and this test
    // would stop exercising the race it is named for. Set-1 (loaded and cached by loadChannel
    // itself) plays no further part here.
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
          {
            id: 'set-3',
            name: 'Drittset',
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

    // Pick A (set-2) — request fires, left unresolved.
    component['selectSet']('set-2');
    const requestA = httpMock.expectOne(
      (req) =>
        req.url === '/api/seventv/channels/handofblood/emotes' &&
        req.params.get('emoteSetId') === 'set-2',
    );

    // Pick B (set-3, never loaded before) before A resolves — a second request fires.
    component['selectSet']('set-3');
    const requestB = httpMock.expectOne(
      (req) =>
        req.url === '/api/seventv/channels/handofblood/emotes' &&
        req.params.get('emoteSetId') === 'set-3',
    );

    // B (the current pick) resolves; A's late answer for the set the user has already left arrives
    // after and must be ignored.
    requestB.flush(
      previewResponse({
        emoteSetId: 'set-3',
        emotes: [
          {
            sevenTvEmoteId: 'e3',
            name: 'Kappa',
            defaultName: 'Kappa',
            imageUrl: 'https://cdn.7tv.app/e3/4x.webp',
            topAllTime: null,
            trending: null,
          },
        ],
      }),
    );
    fixture.detectChanges();
    requestA.flush(
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

    expect(grid().emotes()[0].sevenTvEmoteId).toBe('e3');
    expect(component.result()).toBeNull(); // B's preview never got a selection, and A's never applied.
  });

  it('a same-set race: an older response for the currently selected set does not overwrite a newer one (P3-5)', () => {
    loadChannel(); // single set 'set-1', already resolved once by the initial load.

    // Two requests for the very same set id — e.g. a double-clicked retry. The old guard
    // (selectedEmoteSetId only) could never tell these two apart, since both target 'set-1'.
    component['retryPreview']();
    const first = httpMock.expectOne(
      (req) =>
        req.url === '/api/seventv/channels/handofblood/emotes' &&
        req.params.get('emoteSetId') === 'set-1',
    );
    component['retryPreview']();
    const second = httpMock.expectOne(
      (req) =>
        req.url === '/api/seventv/channels/handofblood/emotes' &&
        req.params.get('emoteSetId') === 'set-1',
    );

    // The newer request (second) resolves first; the older one (first) then arrives late with
    // different content and must be ignored.
    second.flush(
      previewResponse({
        emotes: [
          {
            sevenTvEmoteId: 'newer',
            name: 'Newer',
            defaultName: 'Newer',
            imageUrl: 'https://cdn.7tv.app/newer/4x.webp',
            topAllTime: null,
            trending: null,
          },
        ],
      }),
    );
    fixture.detectChanges();
    first.flush(
      previewResponse({
        emotes: [
          {
            sevenTvEmoteId: 'older',
            name: 'Older',
            defaultName: 'Older',
            imageUrl: 'https://cdn.7tv.app/older/4x.webp',
            topAllTime: null,
            trending: null,
          },
        ],
      }),
    );
    fixture.detectChanges();

    expect(grid().emotes()[0].sevenTvEmoteId).toBe('newer');
  });

  // K3 follow-up fix: every radiogroup switch re-fetched, including switching *back* to a set
  // already shown, and all of it shares the per-user ForeignEmoteLookup rate limit (spec 6.10,
  // 10/min) with the set-list call and K2's target list. Found live: toggling between HandOfBlood's
  // 3 sets a few times hit 429 after ~8 switches. A successfully loaded preview is now cached per
  // set id within the open channel and served without a request when picked again.

  it('reuses an already-loaded preview when switching back to it — A, B, A issues exactly two preview requests total', () => {
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
    // loadChannel already issued and resolved the first (and, for this test, only expected)
    // request for the active set-1.

    component['selectSet']('set-2');
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-2',
      )
      .flush(
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
    expect(grid().emotes()[0].sevenTvEmoteId).toBe('e2');

    // Back to set-1 — no third request, its preview is already cached from the initial load.
    component['selectSet']('set-1');
    fixture.detectChanges();
    httpMock.expectNone(() => true);
    expect(grid().emotes()[0].sevenTvEmoteId).toBe('e1');
  });

  it('drops a selection made on set A when switching to an already-cached set B, not just when B still needs a request', () => {
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

    // Visit B once so its preview is cached too, then back to A (itself a cache hit, from the
    // initial load) — both sets are now cached going into the part this test is actually about.
    component['selectSet']('set-2');
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-2',
      )
      .flush(previewResponse({ emoteSetId: 'set-2' }));
    fixture.detectChanges();
    component['selectSet']('set-1');
    fixture.detectChanges();

    // Select rows on A (set-1).
    component['onSelectionChange'](previewResponse().emotes);
    fixture.detectChanges();
    expect(component.result()).not.toBeNull();

    // Switch to the already-cached B (set-2) — no request at all — and the pick made on A must
    // not survive: it has no honest meaning for a different set (class doc), cache hit or not.
    component['selectSet']('set-2');
    fixture.detectChanges();
    httpMock.expectNone(() => true);

    expect(component['selectedRows']()).toEqual([]);
    expect(component.result()).toBeNull();
  });

  it('requests again for a set whose only load attempt failed, after switching away and back to it', () => {
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

    component['selectSet']('set-2');
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-2',
      )
      .flush({ errorCode: 'not_a_known_code' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    // Back to set-1 (cached from the initial load) — no request.
    component['selectSet']('set-1');
    fixture.detectChanges();
    httpMock.expectNone(() => true);

    // Back to set-2 — an error is never cached, so this must request again rather than replay it.
    component['selectSet']('set-2');
    fixture.detectChanges();
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-2',
      )
      .flush(previewResponse({ emoteSetId: 'set-2' }));
  });

  it('clears cached previews when a new channel query starts, even if a set id happens to repeat', () => {
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
    component['selectSet']('set-2');
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-2',
      )
      .flush(previewResponse({ emoteSetId: 'set-2' }));
    fixture.detectChanges();

    // A new channel query — its own active set happens to share the id "set-2" (an artificial
    // clash for this test only; real 7TV set ids are unique) — must not serve the previous
    // channel's cached preview for it.
    component['channelNameControl'].setValue('otherchannel');
    component['submit']();
    httpMock.expectOne('/api/seventv/channels/otherchannel/emote-sets').flush(
      setsResponse({
        activeEmoteSetId: 'set-2',
        sets: [
          {
            id: 'set-2',
            name: 'Hauptset',
            capacity: 250,
            kind: 'NORMAL',
            isActive: true,
            isPersonal: false,
            ownerDisplayName: 'Owner',
            observations: [],
          },
        ],
      }),
    );
    fixture.detectChanges();

    // Still requests the preview — nothing was skipped by a stale cache hit.
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/otherchannel/emotes' &&
          req.params.get('emoteSetId') === 'set-2',
      )
      .flush(previewResponse({ channelName: 'otherchannel', emoteSetId: 'set-2' }));
  });

  it('"Neu laden" clears every cached preview, not just the set being refreshed', () => {
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
    component['selectSet']('set-2');
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-2',
      )
      .flush(previewResponse({ emoteSetId: 'set-2' }));
    fixture.detectChanges();

    // "Neu laden" refreshes the currently selected set (set-2) with refresh=true...
    button('Neu laden').click();
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-2' &&
          req.params.get('refresh') === 'true',
      )
      .flush(previewResponse({ emoteSetId: 'set-2' }));
    fixture.detectChanges();

    // ...and wipes set-1's cached preview too, even though the refresh never touched it —
    // switching back to it must request again, not replay what "Neu laden" was told not to trust.
    component['selectSet']('set-1');
    fixture.detectChanges();
    httpMock
      .expectOne(
        (req) =>
          req.url === '/api/seventv/channels/handofblood/emotes' &&
          req.params.get('emoteSetId') === 'set-1',
      )
      .flush(previewResponse({ emoteSetId: 'set-1' }));
  });
});
