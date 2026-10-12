/**
 * The channel list's filter and search state: what the URL selects, what a click or typing writes
 * back to it, and that neither is lost by a reload. The matching rules themselves are covered in
 * admin-channel-filter.spec.ts; this spec only covers the page's wiring around them.
 */
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideLocationMocks } from '@angular/common/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { AdminChannel } from '../../core/admin/admin.model';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { AdminChannelsPage } from './admin-channels-page';

/** jsdom ships no `EventSource`; the page subscribes to the admin live stream on construction. */
class FakeEventSource {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  constructor(readonly url: string) {}
  close(): void {
    /* no-op */
  }
}

function channel(
  channelName: string,
  isBotActive: boolean,
  liveState: AdminChannel['liveState'],
): AdminChannel {
  return {
    channelName,
    twitchChannelId: null,
    isBotActive,
    createdAt: '2026-01-01T00:00:00Z',
    emoteCount: 0,
    archivedEmoteCount: 0,
    activeVoteSessionCount: 0,
    voteSessionCount: 0,
    lastSyncedAtUtc: null,
    lastInventoryChangeUtc: null,
    activeEmoteSetId: null,
    activeEmoteSetCapacity: null,
    lastSyncFailureReason: null,
    lastSyncAttemptAtUtc: null,
    trackingResumedAt: null,
    liveState,
  } as AdminChannel;
}

const FLEET = [
  channel('alpha', true, 'live'),
  channel('handofblood', true, 'live'),
  channel('hansolo', true, 'offline'),
  channel('beta', true, 'unknown'),
  channel('sensitron', false, 'unknown'),
];

describe('AdminChannelsPage filter and search', () => {
  let harness: RouterTestingHarness;
  let router: Router;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([{ path: 'admin/channels', component: AdminChannelsPage }]),
        provideLocationMocks(),
        { provide: EVENT_SOURCE_FACTORY, useValue: (url: string) => new FakeEventSource(url) },
      ],
    });
    harness = await RouterTestingHarness.create();
    router = TestBed.inject(Router);
    router.setUpLocationChangeListener();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
  });

  const root = () => harness.fixture.nativeElement as HTMLElement;

  async function tick(ms = 0): Promise<void> {
    await new Promise((resolve) => setTimeout(resolve, ms));
    harness.detectChanges();
  }

  function flushList(channels: AdminChannel[]): void {
    httpMock
      .expectOne('/api/admin/channels')
      .flush({ channels, livePolledAtUtc: '2026-10-12T10:00:00Z' });
  }

  async function open(url: string, channels: AdminChannel[] = FLEET): Promise<void> {
    await harness.navigateByUrl(url, AdminChannelsPage);
    harness.detectChanges();
    flushList(channels);
    await tick();
  }

  const rowNames = () =>
    Array.from(root().querySelectorAll<HTMLAnchorElement>('a[href^="/admin/channels/"]')).map((a) =>
      a.textContent?.trim().replace('#', ''),
    );

  const radio = (state: string) =>
    Array.from(root().querySelectorAll<HTMLButtonElement>('[role="radio"]')).find((r) =>
      r.textContent?.includes(`admin.channels.filter.state.${state}`),
    )!;

  const searchBox = () => root().querySelector<HTMLInputElement>('input[type="search"]')!;

  it('reads state and search from the URL and lists only the matching rows', async () => {
    await open('/admin/channels?state=live&q=HAND');

    expect(rowNames()).toEqual(['handofblood']);
    expect(radio('live').getAttribute('aria-checked')).toBe('true');
    expect(searchBox().value).toBe('HAND');
  });

  it('falls back to all for an unknown state', async () => {
    await open('/admin/channels?state=bogus');

    expect(rowNames()).toHaveLength(5);
    expect(radio('all').getAttribute('aria-checked')).toBe('true');
  });

  it('shows counts over the whole fleet, regardless of the search', async () => {
    await open('/admin/channels?q=hand');

    expect(radio('all').textContent).toContain('5');
    expect(radio('live').textContent).toContain('2');
    expect(radio('inactive').textContent).toContain('1');
  });

  it('names live and offline as unknown while no live status exists', async () => {
    await open('/admin/channels', [channel('alpha', true, 'unknown')]);

    expect(radio('live').textContent).toContain('admin.channels.filter.countUnknown');
    expect(radio('offline').textContent).toContain('admin.channels.filter.countUnknown');
  });

  it('writes a selected state to the URL by replacing the entry, and drops it again for all', async () => {
    await open('/admin/channels');

    radio('inactive').click();
    await tick();
    expect(router.url).toBe('/admin/channels?state=inactive');
    expect(rowNames()).toEqual(['sensitron']);

    radio('all').click();
    await tick();
    expect(router.url).toBe('/admin/channels');
  });

  it('writes the search to the URL once typing settles', async () => {
    await open('/admin/channels');

    searchBox().value = 'han';
    searchBox().dispatchEvent(new Event('input'));
    harness.detectChanges();
    expect(router.url).toBe('/admin/channels');

    await tick(400);
    await tick();
    expect(router.url).toBe('/admin/channels?q=han');
    expect(rowNames()).toEqual(['handofblood', 'hansolo']);
  });

  it('offers a reset when the combination matches nothing, and reset clears both', async () => {
    await open('/admin/channels?state=live&q=zzz');

    expect(rowNames()).toEqual([]);
    const reset = Array.from(root().querySelectorAll<HTMLButtonElement>('button')).filter(
      (b) => b.textContent?.trim() === 'admin.channels.filter.reset',
    );
    expect(reset.length).toBeGreaterThan(0);

    reset[reset.length - 1].click();
    await tick();
    expect(router.url).toBe('/admin/channels');
    expect(rowNames()).toHaveLength(5);
    expect(searchBox().value).toBe('');
  });

  it('keeps state and search across a reload of the list', async () => {
    await open('/admin/channels?state=live&q=hand');

    const refresh = Array.from(root().querySelectorAll<HTMLButtonElement>('button')).find(
      (b) => b.textContent?.trim() === 'admin.channels.refresh',
    )!;
    refresh.click();
    harness.detectChanges();
    flushList([...FLEET, channel('handsome', true, 'live')]);
    await tick();

    expect(rowNames()).toEqual(['handofblood', 'handsome']);
    expect(router.url).toBe('/admin/channels?state=live&q=hand');
  });

  it('links every row to the channel workspace as well as to the admin drilldown', async () => {
    await open('/admin/channels');

    const hrefs = Array.from(root().querySelectorAll<HTMLAnchorElement>('a')).map((a) =>
      a.getAttribute('href'),
    );
    expect(hrefs).toContain('/channels/alpha/usage-stats');
    expect(hrefs).toContain('/admin/channels/alpha');
  });
});
