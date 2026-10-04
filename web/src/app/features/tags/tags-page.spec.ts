import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, signal, WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import de from '../../../../public/i18n/de.json';
import { ROUTER_FEATURES } from '../../app.config';
import { ChannelPermissions } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';
import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { WideViewportService } from '../../core/layout/wide-viewport.service';
import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { EmoteTagEntry, EmoteTagList, EmoteTagSummary } from '../../core/tags/emote-tag.model';
import { ConfirmDialog } from '../../shared/ui/confirm-dialog';
import { TagNameDialog } from './tag-name-dialog';
import { TAG_FEEDBACK_MS, TagsPage } from './tags-page';

/** jsdom has no ResizeObserver; the grid's column count is irrelevant here (it falls back to 1). */
class FakeResizeObserver {
  observe(): void {
    /* no-op */
  }
  disconnect(): void {
    /* no-op */
  }
}

@Component({ selector: 'test-other', template: 'other' })
class Other {}

const BASE = '/api/channels/a/tags';

const MANAGER: ChannelPermissions = {
  canManage: true,
  canViewUsageStats: true,
  isGlobalAdmin: false,
  isTracked: true,
  isBotActive: true,
};

function setStatus(activeEmoteSetId: string): EmoteSetStatus {
  return {
    activeEmoteSetId,
    capacity: 1000,
    occupiedSlots: 3,
    trackedSince: '2026-09-01T00:00:00Z',
    syncFailureReason: null,
    lastSyncAttemptAtUtc: null,
  } as EmoteSetStatus;
}

function tag(
  id: number,
  name: string,
  entryCount = 2,
  inSetCount: number | null = 1,
): EmoteTagSummary {
  return { id, name, entryCount, inSetCount };
}

function tagList(...tags: EmoteTagSummary[]): EmoteTagList {
  return { emoteSetId: 'set-a', isActiveSet: true, tags };
}

function entry(id: string, overrides: Partial<EmoteTagEntry> = {}): EmoteTagEntry {
  return {
    sevenTvEmoteId: id,
    alias: `alias-${id}`,
    imageUrl: `https://cdn.7tv.app/emote/${id}`,
    inSet: true,
    currentName: `alias-${id}`,
    ...overrides,
  };
}

/** A view on the page's protected members — what the template reads, asserted as behaviour. */
interface PageView {
  readonly feedback: () => { key: string; params: Record<string, unknown> } | null;
  readonly selectable: () => boolean;
  readonly dockShown: () => boolean;
}

describe('TagsPage', () => {
  let httpMock: HttpTestingController;
  let permissions: ChannelPermissions;
  let statusAnswer: () => Observable<EmoteSetStatus>;
  let isCoarse: WritableSignal<boolean>;
  let isWide: WritableSignal<boolean>;
  let dialogOpen: ReturnType<typeof vi.fn>;
  let dialogResult: unknown;

  beforeEach(() => {
    vi.stubGlobal('ResizeObserver', FakeResizeObserver);
    permissions = { ...MANAGER };
    statusAnswer = () => of(setStatus('set-a'));
    isCoarse = signal(false);
    isWide = signal(true);
    dialogResult = undefined;
    dialogOpen = vi.fn(() => ({ closed: of(dialogResult) }));

    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter(
          [
            { path: 'channels/:channelName/tags', component: TagsPage },
            { path: 'channels/:channelName/usage-stats', component: Other },
          ],
          ...ROUTER_FEATURES,
        ),
        { provide: ChannelService, useValue: { getPermissions: () => of(permissions) } },
        { provide: EmoteAdminService, useValue: { getSetStatus: () => statusAnswer() } },
        {
          provide: SevenTvEmoteSetService,
          useValue: {
            listChannelEmoteSets: () =>
              of({ activeEmoteSetId: 'set-a', sets: [{ id: 'set-a', name: 'Herbst' }] }),
          },
        },
        { provide: PointerModeService, useValue: { isCoarse } },
        { provide: WideViewportService, useValue: { isWide } },
        { provide: Dialog, useValue: { open: dialogOpen } },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.useRealTimers();
    httpMock.verify();
  });

  async function open(url: string): Promise<{ harness: RouterTestingHarness; page: TagsPage }> {
    const harness = await RouterTestingHarness.create();
    await harness.navigateByUrl(url);
    return { harness, page: pageOf(harness) };
  }

  function pageOf(harness: RouterTestingHarness): TagsPage {
    return harness.fixture.debugElement.query(By.directive(TagsPage)).componentInstance as TagsPage;
  }

  function view(page: TagsPage): PageView {
    return page as unknown as PageView;
  }

  /**
   * Lets effects, resources and router navigations run to rest. Not `whenStable()`: an unanswered
   * request is a pending task, and several tests deliberately leave one open while they look.
   */
  async function settle(harness: RouterTestingHarness): Promise<void> {
    for (let round = 0; round < 30; round++) {
      TestBed.tick();
      harness.detectChanges();
      await Promise.resolve();
    }
  }

  function expectList(withSet = true) {
    return httpMock.expectOne(
      (req) =>
        req.method === 'GET' &&
        req.url === BASE &&
        req.params.get('emoteSetId') === (withSet ? 'set-a' : null),
    );
  }

  function expectEntries(tagId: number) {
    return httpMock.expectOne(
      (req) => req.method === 'GET' && req.url === `${BASE}/${tagId}/entries`,
    );
  }

  function text(harness: RouterTestingHarness): string {
    return (harness.routeNativeElement as HTMLElement).textContent ?? '';
  }

  function buttonByName(harness: RouterTestingHarness, name: string): HTMLButtonElement | null {
    const buttons = Array.from(
      (harness.routeNativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'),
    );
    return buttons.find((button) => button.textContent?.trim() === name) ?? null;
  }

  function cells(harness: RouterTestingHarness): HTMLElement[] {
    return Array.from(
      (harness.routeNativeElement as HTMLElement).querySelectorAll<HTMLElement>(
        '[aria-label^="alias-"]',
      ),
    );
  }

  /** Opens `?tag=1` on a list with tag 1 and answers its entries. */
  async function openDetail(entries: EmoteTagEntry[] = [entry('e1'), entry('e2')]) {
    const opened = await open('/channels/a/tags?tag=1');
    expectList().flush(tagList(tag(1, 'Stronghold', entries.length), tag(2, 'Halloween')));
    await settle(opened.harness);
    expectEntries(1).flush({ emoteSetId: 'set-a', isActiveSet: true, entries });
    await settle(opened.harness);
    return opened;
  }

  describe('URL state', () => {
    it('?tag= chooses the tag and loads its entries for the active set', async () => {
      const { harness, page } = await open('/channels/a/tags?tag=2');
      expect(page.selectedTagId()).toBe(2);

      expectList().flush(tagList(tag(1, 'Stronghold'), tag(2, 'Halloween')));
      await settle(harness);

      expect(page.selectedTag()?.name).toBe('Halloween');
      const req = expectEntries(2);
      expect(req.request.params.get('emoteSetId')).toBe('set-a');
      req.flush({ emoteSetId: 'set-a', isActiveSet: true, entries: [] });
    });

    it('a malformed ?tag= is no tag, and no error', async () => {
      const { harness, page } = await open('/channels/a/tags?tag=abc');
      expect(page.selectedTagId()).toBeNull();

      expectList().flush(tagList(tag(1, 'Stronghold')));
      await settle(harness);

      expect(page.selectedTag()).toBeNull();
      expect(harness.routeNativeElement?.querySelector('[role="alert"]')).toBeNull();
    });

    it('an id the channel does not have is dropped from the URL without a message', async () => {
      const { harness, page } = await open('/channels/a/tags?tag=99');
      expectList().flush(tagList(tag(1, 'Stronghold')));
      await settle(harness);

      expect(page.selectedTagId()).toBeNull();
      expect(TestBed.inject(Router).url).toBe('/channels/a/tags');
      expect(view(page).feedback()).toBeNull();
    });
  });

  describe('header', () => {
    it('names the active set the numbers refer to', async () => {
      const { harness } = await open('/channels/a/tags');
      expectList().flush(tagList(tag(1, 'Stronghold')));
      await settle(harness);

      expect(text(harness)).toContain('Die Zahlen beziehen sich auf das aktive Set Herbst.');
    });

    it('without an active set: says so, and asks for the list without a set id', async () => {
      statusAnswer = () => throwError(() => new HttpErrorResponse({ status: 404 }));
      const { harness, page } = await open('/channels/a/tags');
      expectList(false).flush({
        emoteSetId: null,
        isActiveSet: false,
        tags: [tag(1, 'X', 2, null)],
      });
      await settle(harness);

      expect(page.activeEmoteSetId()).toBeNull();
      expect(text(harness)).toContain(de.tags.page.noActiveSet);
      // No "k im Set" in the row's micro line: there is no set to count in.
      const row = harness.routeNativeElement?.querySelector('ul[aria-label="Tags"] li');
      expect(row?.textContent?.replace(/\s+/g, ' ').trim()).toBe('X 2 Einträge');
    });
  });

  describe('rights and pointer', () => {
    it('a manager gets "Neuer Tag", rename, delete and a selecting grid', async () => {
      const { harness, page } = await openDetail();

      expect(buttonByName(harness, 'Neuer Tag')).not.toBeNull();
      expect(buttonByName(harness, 'Umbenennen')).not.toBeNull();
      expect(buttonByName(harness, 'Löschen')).not.toBeNull();
      expect(view(page).selectable()).toBe(true);
      expect(cells(harness).every((cell) => cell.getAttribute('aria-pressed') === 'false')).toBe(
        true,
      );
    });

    it('without canManage: no write controls, and the cells select nothing', async () => {
      permissions = { ...MANAGER, canManage: false };
      const { harness, page } = await openDetail();

      expect(page.canManage()).toBe(false);
      expect(buttonByName(harness, 'Neuer Tag')).toBeNull();
      expect(buttonByName(harness, 'Umbenennen')).toBeNull();
      expect(buttonByName(harness, 'Löschen')).toBeNull();
      expect(cells(harness)).toHaveLength(2);
      expect(cells(harness).some((cell) => cell.hasAttribute('aria-pressed'))).toBe(false);
    });

    it('on a coarse pointer: no selection and no dock, rename and delete stay', async () => {
      isCoarse.set(true);
      const { harness, page } = await openDetail();

      expect(view(page).selectable()).toBe(false);
      cells(harness)[0].click();
      await settle(harness);
      expect(page.selection.selectedKeys()).toEqual([]);
      expect(view(page).dockShown()).toBe(false);
      expect(buttonByName(harness, 'Umbenennen')).not.toBeNull();
      expect(buttonByName(harness, 'Löschen')).not.toBeNull();
    });

    it('a pointer turning coarse drops the marks', async () => {
      const { harness, page } = await openDetail();
      cells(harness)[0].click();
      await settle(harness);
      expect(page.selection.selectedKeys()).toEqual(['e1']);

      isCoarse.set(true);
      await settle(harness);
      expect(page.selection.selectedKeys()).toEqual([]);
    });
  });

  describe('empty states', () => {
    it('no tags: a manager is pointed at the usage grid', async () => {
      const { harness } = await open('/channels/a/tags');
      expectList().flush(tagList());
      await settle(harness);

      expect(text(harness)).toContain(de.tags.page.empty.title);
      const cta = harness.routeNativeElement?.querySelector<HTMLAnchorElement>('a[href]');
      expect(cta?.textContent?.trim()).toBe(de.tags.page.empty.cta);
      expect(cta?.getAttribute('href')).toBe('/channels/a/usage-stats');
    });

    it('no tags: without canManage there is no pointer to a place they cannot act', async () => {
      permissions = { ...MANAGER, canManage: false };
      const { harness } = await open('/channels/a/tags');
      expectList().flush(tagList());
      await settle(harness);

      expect(text(harness)).toContain(de.tags.page.empty.title);
      expect(text(harness)).not.toContain(de.tags.page.empty.cta);
    });

    it('wide, nothing chosen: asks to pick a tag', async () => {
      const { harness } = await open('/channels/a/tags');
      expectList().flush(tagList(tag(1, 'Stronghold')));
      await settle(harness);

      expect(text(harness)).toContain(de.tags.page.emptySelection);
    });

    it('a tag without entries says so', async () => {
      const { harness } = await openDetail([]);

      expect(text(harness)).toContain(de.tags.page.emptyEntries.title);
    });
  });

  describe('drilldown below lg', () => {
    it('narrow with a tag: the detail alone, with the up-link to the list', async () => {
      isWide.set(false);
      const { harness, page } = await openDetail();

      expect(page.isDrilldown()).toBe(true);
      const backLink = harness.routeNativeElement?.querySelector('a[aria-label="Zurück zu Tags"]');
      expect(backLink?.getAttribute('href')).toBe('/channels/a/tags');
      expect(harness.routeNativeElement?.querySelector('ul[aria-label="Tags"]')).toBeNull();
    });

    it('narrow without a tag: the list alone, no up-link', async () => {
      isWide.set(false);
      const { harness, page } = await open('/channels/a/tags');
      expectList().flush(tagList(tag(1, 'Stronghold')));
      await settle(harness);

      expect(page.isDrilldown()).toBe(false);
      expect(harness.routeNativeElement?.querySelector('ul[aria-label="Tags"]')).not.toBeNull();
      expect(
        harness.routeNativeElement?.querySelector('a[aria-label="Zurück zu Tags"]'),
      ).toBeNull();
      expect(text(harness)).not.toContain(de.tags.page.emptySelection);
    });

    it('wide with a tag: list and detail side by side, no up-link', async () => {
      const { harness, page } = await openDetail();

      expect(page.isDrilldown()).toBe(false);
      const current = harness.routeNativeElement?.querySelector('a[aria-current="true"]');
      expect(current?.textContent?.trim()).toBe('Stronghold');
      expect(
        harness.routeNativeElement?.querySelector('a[aria-label="Zurück zu Tags"]'),
      ).toBeNull();
    });
  });

  describe('actions', () => {
    it('delete: confirm dialog, DELETE, then the list reloads and the choice is gone', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      const { harness, page } = await openDetail();
      dialogResult = true;

      buttonByName(harness, 'Löschen')!.click();
      expect(dialogOpen.mock.calls[0][0]).toBe(ConfirmDialog);
      expect(dialogOpen.mock.calls[0][1].data.message).toBe(
        'Stronghold wird gelöscht; die Emotes bleiben im Set.',
      );

      const del = httpMock.expectOne(`${BASE}/1`);
      expect(del.request.method).toBe('DELETE');
      del.flush(null);
      await settle(harness);

      expectList().flush(tagList(tag(2, 'Halloween')));
      await settle(harness);

      expect(page.selectedTagId()).toBeNull();
      expect(TestBed.inject(Router).url).toBe('/channels/a/tags');
      expect(view(page).feedback()?.key).toBe('tags.page.deleted');

      vi.advanceTimersByTime(TAG_FEEDBACK_MS);
      expect(view(page).feedback()).toBeNull();
    });

    it('a tag picked while another tag is being deleted stays selected when the delete completes', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      const { harness, page } = await openDetail();
      dialogResult = true;

      buttonByName(harness, 'Löschen')!.click();
      const del = httpMock.expectOne(`${BASE}/1`);

      await harness.navigateByUrl('/channels/a/tags?tag=2');
      await settle(harness);
      expectEntries(2).flush({ emoteSetId: 'set-a', isActiveSet: true, entries: [] });
      await settle(harness);
      expect(page.selectedTagId()).toBe(2);

      del.flush(null);
      await settle(harness);
      expectList().flush(tagList(tag(2, 'Halloween')));
      await settle(harness);

      expect(page.selectedTagId()).toBe(2);
      expect(TestBed.inject(Router).url).toBe('/channels/a/tags?tag=2');
      expect(view(page).feedback()?.key).toBe('tags.page.deleted');
    });

    it('delete dismissed: nothing is sent', async () => {
      const { harness } = await openDetail();
      dialogResult = false;

      buttonByName(harness, 'Löschen')!.click();

      httpMock.expectNone(`${BASE}/1`);
    });

    it('create: the new tag is reloaded and chosen', async () => {
      const { harness, page } = await open('/channels/a/tags');
      expectList().flush(tagList(tag(1, 'Stronghold')));
      await settle(harness);
      dialogResult = { id: 5, name: 'Neu' };

      buttonByName(harness, 'Neuer Tag')!.click();
      expect(dialogOpen.mock.calls[0][0]).toBe(TagNameDialog);
      expect(dialogOpen.mock.calls[0][1].data).toEqual({ channelName: 'a' });
      await settle(harness);

      // The URL already names the new tag while the reload is out; it must survive that window.
      expect(page.selectedTagId()).toBe(5);
      expectList().flush(tagList(tag(1, 'Stronghold'), tag(5, 'Neu', 0, 0)));
      await settle(harness);

      expect(page.selectedTag()?.name).toBe('Neu');
      expectEntries(5).flush({ emoteSetId: 'set-a', isActiveSet: true, entries: [] });
    });

    it('rename: the dialog is prefilled with the tag, and the list reloads after it', async () => {
      const { harness } = await openDetail();
      dialogResult = { id: 1, name: 'Burg' };

      buttonByName(harness, 'Umbenennen')!.click();
      expect(dialogOpen.mock.calls[0][1].data).toEqual({
        channelName: 'a',
        tag: { id: 1, name: 'Stronghold' },
      });
      await settle(harness);

      expectList().flush(tagList(tag(1, 'Burg', 2), tag(2, 'Halloween')));
    });

    it('marked cells are taken out of the tag; then tags and entries reload', async () => {
      vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] });
      const { harness, page } = await openDetail([entry('e1'), entry('e2'), entry('e3')]);

      cells(harness)[0].click();
      cells(harness)[2].click();
      await settle(harness);
      expect(view(page).dockShown()).toBe(true);

      buttonByName(harness, 'Aus ‚Stronghold‘ entfernen (2)')!.click();
      const req = httpMock.expectOne(`${BASE}/1/entries/remove`);
      expect(req.request.body).toEqual({ sevenTvEmoteIds: ['e1', 'e3'] });
      req.flush({ removedCount: 2 });
      await settle(harness);

      expect(page.selection.selectedKeys()).toEqual([]);
      expect(view(page).feedback()?.key).toBe('tags.feedback.unassigned.other');
      expectList().flush(tagList(tag(1, 'Stronghold', 1), tag(2, 'Halloween')));
      expectEntries(1).flush({ emoteSetId: 'set-a', isActiveSet: true, entries: [entry('e2')] });
    });

    it('the removal button carries the tag name whole in its accessible name', async () => {
      const { harness } = await openDetail([entry('e1'), entry('e2')]);

      cells(harness)[0].click();
      await settle(harness);

      const button = buttonByName(harness, 'Aus ‚Stronghold‘ entfernen (1)')!;
      expect(button.getAttribute('aria-label')).toBe('Aus ‚Stronghold‘ entfernen (1)');
      // The visible text is the part that may truncate; it is hidden from the accessible name.
      expect(button.querySelector('[aria-hidden="true"]')).not.toBeNull();
    });

    it('a failed removal shows its reason and keeps the marks', async () => {
      const { harness, page } = await openDetail();
      cells(harness)[0].click();
      await settle(harness);

      buttonByName(harness, 'Aus ‚Stronghold‘ entfernen (1)')!.click();
      httpMock
        .expectOne(`${BASE}/1/entries/remove`)
        .flush({ errorCode: 'emote_ids_invalid' }, { status: 400, statusText: 'Bad Request' });
      await settle(harness);

      expect(harness.routeNativeElement?.querySelector('[role="alert"]')?.textContent).toContain(
        de.errors.api.emote_ids_invalid,
      );
      expect(page.selection.selectedKeys()).toEqual(['e1']);
    });
  });

  it('a tag deleted elsewhere falls back to the list with a message', async () => {
    const { harness, page } = await open('/channels/a/tags?tag=1');
    expectList().flush(tagList(tag(1, 'Stronghold')));
    await settle(harness);

    expectEntries(1).flush(
      { errorCode: 'tag_not_found' },
      { status: 404, statusText: 'Not Found' },
    );
    await settle(harness);

    expectList().flush(tagList());
    await settle(harness);

    expect(page.selectedTagId()).toBeNull();
    expect(view(page).feedback()?.key).toBe('tags.page.tagGone');
    expect(text(harness)).toContain(de.tags.page.empty.title);
  });
});
