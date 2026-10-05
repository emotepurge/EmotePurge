import { Dialog } from '@angular/cdk/dialog';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Component, ElementRef, signal, WritableSignal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { provideRouter, Router } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, Subject, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import de from '../../../../public/i18n/de.json';
import { ROUTER_FEATURES } from '../../app.config';
import { ChannelPermissions } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';
import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { DockClearanceService } from '../../core/layout/dock-clearance.service';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { channelLiveUrl, LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import { CHANNEL_RELOAD_DEBOUNCE_MS } from '../../core/live/live-reload';
import { WideViewportService } from '../../core/layout/wide-viewport.service';
import { PointerModeService } from '../../core/pointer/pointer-mode.service';
import { SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { EmoteTagEntry, EmoteTagList, EmoteTagSummary } from '../../core/tags/emote-tag.model';
import {
  TagRemovalConfirmDialog,
  TagRemovalConfirmDialogData,
} from '../../shared/tags/tag-removal-confirm-dialog';
import { TagRunActions } from '../../shared/tags/tag-run-actions';
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

/** jsdom has no EventSource; the page follows `channel.synced` through one. */
class FakeEventSource {
  static instances: FakeEventSource[] = [];
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  close(): void {
    /* no-op */
  }

  emit(event: { type: string; channel?: string }): void {
    this.onmessage?.({ data: JSON.stringify(event) } as MessageEvent);
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
  tagRunsEnabled: false,
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
  return { id, name, entryCount, inSetCount, placedCount: 0, active: false, activatedAtUtc: null };
}

function activeTag(id: number, name: string, placedCount = 1): EmoteTagSummary {
  return { ...tag(id, name), placedCount, active: true, activatedAtUtc: '2026-10-01T10:00:00Z' };
}

function queueItem(id: string): RunQueueItem {
  return {
    sevenTvEmoteId: id,
    name: id,
    status: 'pending',
    completedSteps: 0,
    failedStep: null,
  } as unknown as RunQueueItem;
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
    placedByThisTag: false,
    placedAtUtc: null,
    placementOperationId: null,
    heldByActiveTags: [],
    placedByOtherTags: [],
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
    FakeEventSource.instances = [];
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
        {
          provide: EmoteAdminService,
          useValue: {
            getSetStatus: () => statusAnswer(),
            getSetWarning: () =>
              of({
                available: true,
                isOwnSet: true,
                otherTrackedChannelsSharingSet: [],
                otherModeratedChannelsSharingSet: [],
              }),
          },
        },
        {
          provide: SevenTvEmoteSetService,
          useValue: {
            listChannelEmoteSets: () =>
              of({
                activeEmoteSetId: 'set-a',
                sets: [
                  { id: 'set-a', name: 'Herbst' },
                  { id: 'set-b', name: 'Halloween' },
                ],
              }),
            resolveEditableSet: () =>
              of({
                status: 'editable',
                target: {
                  emoteSetId: 'set-a',
                  setName: 'Herbst',
                  ownerDisplayName: 'A',
                  twitchLogin: 'a',
                  trackedChannelName: 'a',
                  isActiveSet: true,
                  ownerTwitchChannelId: 'tw-a',
                },
              }),
          },
        },
        { provide: SevenTvTokenService, useValue: { hasToken: signal(true) } },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
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

  /** The set-removal button whether or not a marking adds its count ("Aus dem Set entfernen (2)"). */
  function removeButton(harness: RouterTestingHarness): HTMLButtonElement {
    return Array.from(
      (harness.routeNativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('button'),
    ).find((button) =>
      /^Aus dem Set entfernen( \(\d+\))?$/.test(button.textContent?.trim() ?? ''),
    )!;
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

    it('explains the tag, with the run sentence only where the run buttons exist', async () => {
      const first = await open('/channels/a/tags');
      expectList().flush(tagList(tag(1, 'Stronghold')));
      await settle(first.harness);
      expect(text(first.harness)).toContain(de.tags.page.intro);
      expect(text(first.harness)).not.toContain(de.tags.page.introRuns);
    });

    it('adds the run sentence when tag runs are on and a fine pointer can use them', async () => {
      permissions = { ...MANAGER, tagRunsEnabled: true };
      const { harness } = await open('/channels/a/tags');
      expectList().flush(tagList(tag(1, 'Stronghold')));
      await settle(harness);

      expect(text(harness)).toContain(de.tags.page.intro);
      expect(text(harness)).toContain(de.tags.page.introRuns);
    });

    it('drops the run sentence on a coarse pointer, where the buttons are absent', async () => {
      permissions = { ...MANAGER, tagRunsEnabled: true };
      isCoarse.set(true);
      const { harness } = await open('/channels/a/tags');
      expectList().flush(tagList(tag(1, 'Stronghold')));
      await settle(harness);

      expect(text(harness)).toContain(de.tags.page.intro);
      expect(text(harness)).not.toContain(de.tags.page.introRuns);
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
      // The row still counts its entries, but nothing that needs a set to count in: neither "im
      // Set" nor a played-in state.
      const row = harness.routeNativeElement?.querySelector('ul[aria-label="Tags"] li');
      const rowText = row?.textContent ?? '';
      expect(rowText).toContain('2 Einträge');
      expect(rowText).not.toContain('im Set');
      expect(rowText).not.toContain('ins Set geholt');
    });
  });

  describe('rights and pointer', () => {
    it('a manager gets "Neuer Tag", rename, delete and a selecting grid', async () => {
      const { harness, page } = await openDetail();

      expect(buttonByName(harness, 'Neuer Tag')).not.toBeNull();
      expect(buttonByName(harness, 'Umbenennen')).not.toBeNull();
      expect(buttonByName(harness, 'Tag löschen')).not.toBeNull();
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
      expect(buttonByName(harness, 'Tag löschen')).toBeNull();
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
      expect(buttonByName(harness, 'Tag löschen')).not.toBeNull();
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

    it('a tag without entries says where to pick them, instead of showing a second empty text', async () => {
      const { harness } = await openDetail([]);

      expect(text(harness)).toContain(de.tags.page.emptyEntries.description);
      expect(text(harness).split(de.tags.page.emptyEntries.title)).toHaveLength(2);
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

      buttonByName(harness, 'Tag löschen')!.click();
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

      buttonByName(harness, 'Tag löschen')!.click();
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

      buttonByName(harness, 'Tag löschen')!.click();

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

      buttonByName(harness, 'Aus dem Tag entfernen (2)')!.click();
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

      const button = buttonByName(harness, 'Aus dem Tag entfernen (1)')!;
      expect(button.getAttribute('aria-label')).toBe('Aus dem Tag entfernen (1) – Stronghold');
      // The visible label is the start of the accessible name (WCAG 2.5.3).
      expect(button.getAttribute('aria-label')).toContain(button.textContent!.trim());
    });

    it('a failed removal shows its reason and keeps the marks', async () => {
      const { harness, page } = await openDetail();
      cells(harness)[0].click();
      await settle(harness);

      buttonByName(harness, 'Aus dem Tag entfernen (1)')!.click();
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

  describe('tag runs, state and placements (#201 T-C)', () => {
    beforeEach(() => {
      permissions = { ...MANAGER, tagRunsEnabled: true };
    });

    async function openTag(
      summary: EmoteTagSummary,
      entries: EmoteTagEntry[] = [entry('e1'), entry('e2')],
    ) {
      const opened = await open(`/channels/a/tags?tag=${summary.id}`);
      expectList().flush(tagList(summary, tag(2, 'Halloween')));
      await settle(opened.harness);
      expectEntries(summary.id).flush({
        emoteSetId: 'set-a',
        isActiveSet: true,
        activationOperationId: summary.active ? 'act-1' : null,
        entries,
      });
      await settle(opened.harness);
      return opened;
    }

    const GQL = 'https://7tv.io/v4/gql';

    /** Answers the flow's live 7TV read with the given emotes in the set (alias `alias-<id>`). */
    function flushLiveRead(ids: string[] = ['e1']): void {
      for (let round = 0; round < 5; round++) {
        for (const request of httpMock.match(GQL)) {
          request.flush({
            data: {
              emoteSets: {
                emoteSet: {
                  emotes: {
                    totalCount: ids.length,
                    pageCount: 1,
                    items: ids.map((id) => ({
                      alias: `alias-${id}`,
                      emote: { id, defaultName: id.toUpperCase() },
                    })),
                  },
                },
              },
            },
          });
        }
      }
    }

    function runActions(harness: RouterTestingHarness): TagRunActions | null {
      const found = harness.fixture.debugElement.query(By.directive(TagRunActions));
      return found ? (found.componentInstance as TagRunActions) : null;
    }

    /** The dock through the page's own `#dock` view child — not by its look. */
    function dock(harness: RouterTestingHarness): HTMLElement | null {
      const view = pageOf(harness) as unknown as {
        dockRef: () => ElementRef<HTMLElement> | undefined;
      };
      return view.dockRef()?.nativeElement ?? null;
    }

    describe('the run buttons (spec 9.4, 8)', () => {
      it('offers "Ins Set holen" in the detail head, without "Aus dem Set entfernen" for a tag not played in with nothing in the set', async () => {
        const { harness } = await openTag(tag(1, 'Stronghold', 2, 0));

        expect(buttonByName(harness, 'Ins Set holen')).not.toBeNull();
        expect(buttonByName(harness, 'Aus dem Set entfernen')).toBeNull();
      });

      // Operator decision 2026-10-05: something of the tag in the set is enough to clear it out.
      it('offers "Aus dem Set entfernen" for a tag not played in once one of its emotes is in the set', async () => {
        const { harness } = await openTag(tag(1, 'Stronghold', 2, 1));

        expect(buttonByName(harness, 'Ins Set holen')).not.toBeNull();
        expect(buttonByName(harness, 'Aus dem Set entfernen')).not.toBeNull();
      });

      it('offers "Aus dem Set entfernen" for a played-in tag', async () => {
        const { harness } = await openTag(activeTag(1, 'Stronghold'));

        expect(buttonByName(harness, 'Ins Set holen')).not.toBeNull();
        expect(buttonByName(harness, 'Aus dem Set entfernen')).not.toBeNull();
      });

      it('hides "Ins Set holen" for a tag whose emotes are all in the set — an inactive one keeps "Aus dem Set entfernen"', async () => {
        const full = { ...tag(1, 'Stronghold', 2, 2) };
        const { harness } = await openTag(full);
        expect(buttonByName(harness, 'Ins Set holen')).toBeNull();
        expect(buttonByName(harness, 'Aus dem Set entfernen')).not.toBeNull();
      });

      it('keeps only "Aus dem Set entfernen" for a played-in tag with nothing missing', async () => {
        const { harness } = await openTag({ ...activeTag(1, 'Stronghold'), inSetCount: 2 });

        expect(buttonByName(harness, 'Ins Set holen')).toBeNull();
        expect(buttonByName(harness, 'Aus dem Set entfernen')).not.toBeNull();
      });

      it('takes focus onto the detail heading when the run buttons lose it with no button left', async () => {
        const { harness } = await openTag(activeTag(1, 'Stronghold'));

        runActions(harness)!.focusLost.emit();
        await settle(harness);

        expect(document.activeElement?.id).toBe('tag-detail-title');
      });

      it('needs no management right (E9), and passes the live active set', async () => {
        permissions = { ...MANAGER, canManage: false, tagRunsEnabled: true };
        const { harness, page } = await openTag(tag(1, 'Stronghold'));

        expect(buttonByName(harness, 'Ins Set holen')).not.toBeNull();
        expect(buttonByName(harness, 'Umbenennen')).toBeNull();
        expect(runActions(harness)!.activeEmoteSetId()).toBe(page.activeEmoteSetId());
      });

      it('are absent while tag runs are switched off', async () => {
        permissions = { ...MANAGER, tagRunsEnabled: false };
        const { harness } = await openTag(activeTag(1, 'Stronghold'));

        expect(buttonByName(harness, 'Ins Set holen')).toBeNull();
        expect(buttonByName(harness, 'Umbenennen')).not.toBeNull();
      });

      it('are absent on a coarse pointer — rename and delete stay', async () => {
        isCoarse.set(true);
        const { harness } = await openTag(activeTag(1, 'Stronghold'));

        expect(buttonByName(harness, 'Ins Set holen')).toBeNull();
        expect(buttonByName(harness, 'Aus dem Set entfernen')).toBeNull();
        expect(buttonByName(harness, 'Tag löschen')).not.toBeNull();
      });

      it('are absent on a channel without an active set', async () => {
        statusAnswer = () => throwError(() => new HttpErrorResponse({ status: 404 }));
        const { harness } = await open('/channels/a/tags?tag=1');
        expectList(false).flush({
          emoteSetId: null,
          isActiveSet: false,
          tags: [tag(1, 'Stronghold', 2, null)],
        });
        await settle(harness);
        httpMock
          .expectOne((req) => req.url === `${BASE}/1/entries`)
          .flush({
            emoteSetId: null,
            isActiveSet: false,
            activationOperationId: null,
            entries: [],
          });
        await settle(harness);

        expect(buttonByName(harness, 'Ins Set holen')).toBeNull();
      });

      it('explains a lock held by an undo — whose dock this page does not show', async () => {
        const { harness } = await openTag(tag(1, 'Stronghold'));

        expect(runActions(harness)!.unshownRunKinds()).toEqual(['undo']);
      });
    });

    describe('state line and list micro line', () => {
      it('names the activation date of a played-in tag', async () => {
        const { harness } = await openTag(activeTag(1, 'Stronghold'));
        const date = new Date('2026-10-01T10:00:00Z').toLocaleDateString(
          toLocale(TestBed.inject(LanguageService).lang()),
          { dateStyle: 'short' },
        );

        expect(text(harness)).toContain(`über den Tag ins Set geholt am ${date}`);
      });

      it('names no state for a tag that added nothing to the set', async () => {
        const { harness } = await openTag(tag(1, 'Stronghold'));

        expect(text(harness)).not.toContain('ins Set geholt am');
        expect(text(harness)).not.toContain('nicht über den Tag');
      });

      it('says "über den Tag ins Set geholt" for an active tag whose activation date is missing', async () => {
        const { harness } = await openTag({ ...activeTag(1, 'Stronghold'), activatedAtUtc: null });

        const lines = Array.from(
          harness.routeNativeElement!.querySelectorAll('p.text-fg-secondary'),
        ).map((line) => line.textContent?.trim());
        expect(lines).toContain(de.tags.page.state.activeUndated);
      });

      it('says in the list how many placements a played-in tag holds', async () => {
        const { harness } = await openTag(activeTag(1, 'Stronghold', 3));
        const rows = Array.from(
          harness.routeNativeElement!.querySelectorAll('ul[aria-label="Tags"] li'),
        ).map((row) => row.textContent ?? '');

        expect(rows[0]).toContain('3 über den Tag ins Set geholt');
        expect(rows[1]).not.toContain('ins Set geholt');
      });

      it('says nothing in the list for a played-in tag without placements', async () => {
        const { harness } = await openTag(activeTag(1, 'Stronghold', 0));
        const row = harness.routeNativeElement!.querySelector(
          'ul[aria-label="Tags"] li',
        )!.textContent!;

        expect(row).not.toContain('ins Set geholt');
      });
    });

    describe('deleting a tag with placements (spec 8)', () => {
      it('adds the placement hint to the message, the label stays "Tag löschen"', async () => {
        const { harness } = await openTag(activeTag(1, 'Stronghold', 12));
        dialogResult = false;

        buttonByName(harness, 'Tag löschen')!.click();

        const data = dialogOpen.mock.calls[0][1].data;
        expect(data.message).toContain('Stronghold wird gelöscht; die Emotes bleiben im Set.');
        expect(data.message).toContain(
          '12 Emotes dieses Tags wurden über ihn ins Set geholt und sind noch dort — vorher aus dem Set entfernen?',
        );
        expect(data.confirmLabel).toBe('Tag löschen');
      });

      it('says nothing about placements when there are none', async () => {
        const { harness } = await openTag(tag(1, 'Stronghold'));
        dialogResult = false;

        buttonByName(harness, 'Tag löschen')!.click();

        expect(dialogOpen.mock.calls[0][1].data.message).not.toContain('ins Set geholt');
      });
    });

    describe('the run dock (plan 3.8) — the real tagRunDockHasContent over the services', () => {
      it('has no dock with nothing to show', async () => {
        const { harness } = await openTag(tag(1, 'Stronghold'));

        expect(dock(harness)).toBeNull();
      });

      it('mounts for a delete run alone, with the delete section — and gives the space back on leaving', async () => {
        const { harness } = await openTag(tag(1, 'Stronghold'));
        const clearance = TestBed.inject(DockClearanceService);
        const reserve = vi.spyOn(clearance, 'reserve');

        TestBed.inject(SevenTvDeleteService).queue.set([queueItem('e1')]);
        await settle(harness);

        expect(dock(harness)).not.toBeNull();
        expect(dock(harness)!.querySelector('app-delete-progress-section')).not.toBeNull();
        expect(reserve).toHaveBeenCalled();

        await harness.navigateByUrl('/channels/a/usage-stats');
        expect(clearance.px()).toBe(0);
        TestBed.inject(SevenTvDeleteService).queue.set([]);
      });

      it('mounts for a restore run alone, with the restore section', async () => {
        const { harness } = await openTag(tag(1, 'Stronghold'));

        TestBed.inject(SevenTvRestoreService).queue.set([queueItem('e1')]);
        await settle(harness);

        expect(dock(harness)).not.toBeNull();
        expect(dock(harness)!.querySelector('app-restore-progress-section')).not.toBeNull();
        TestBed.inject(SevenTvRestoreService).queue.set([]);
      });

      it('mounts for a pending import notice alone (a refused play-in leaves no run)', async () => {
        const { harness } = await openTag(tag(1, 'Stronghold'));

        TestBed.inject(SevenTvImportService).duplicateNoticePending.set(true);
        await settle(harness);

        expect(dock(harness)).not.toBeNull();
        expect(dock(harness)!.querySelector('app-import-progress-section')).not.toBeNull();
        TestBed.inject(SevenTvImportService).duplicateNoticePending.set(false);
      });

      it('stays at page level: shown with the list alone, and the drilldown keeps its up-link', async () => {
        isWide.set(false);
        const { harness } = await openTag(tag(1, 'Stronghold'));
        TestBed.inject(SevenTvDeleteService).queue.set([queueItem('e1')]);
        await settle(harness);

        expect(dock(harness)).not.toBeNull();
        expect(
          harness.routeNativeElement?.querySelector('a[aria-label="Zurück zu Tags"]'),
        ).not.toBeNull();

        await harness.navigateByUrl('/channels/a/tags');
        await settle(harness);
        expect(harness.routeNativeElement?.querySelector('ul[aria-label="Tags"]')).not.toBeNull();
        expect(dock(harness)).not.toBeNull();
        TestBed.inject(SevenTvDeleteService).queue.set([]);
      });

      it('has no dock on a coarse pointer', async () => {
        isCoarse.set(true);
        const { harness } = await openTag(tag(1, 'Stronghold'));
        TestBed.inject(SevenTvDeleteService).queue.set([queueItem('e1')]);
        await settle(harness);

        expect(dock(harness)).toBeNull();
        TestBed.inject(SevenTvDeleteService).queue.set([]);
      });
    });

    describe('the run status region', () => {
      it("speaks the arbiter's refused start in the page's permanent region", async () => {
        const { harness } = await openTag(tag(1, 'Stronghold'));
        const arbiter = TestBed.inject(SevenTvRunArbiter);
        arbiter.register({
          kind: 'undo',
          isRunning: signal(true),
          isSettling: signal(false),
          destructiveOpen: signal(false),
        });
        arbiter.noteRefusedStart('import');
        await settle(harness);

        const regions = Array.from(
          harness.routeNativeElement!.querySelectorAll('header [role="status"].sr-only'),
        ).map((region) => region.textContent ?? '');
        expect(regions.join(' ')).toContain('Nichts gestartet');
      });

      it('clears a restore refusal when a tag flow starts, and when a run starts', async () => {
        const { harness, page } = await openTag(tag(1, 'Stronghold'));
        const view = page as unknown as {
          runNotice: WritableSignal<{ leadKey: string; reasonKey: string } | null>;
        };
        const notice = { leadKey: 'restore.errors.lead', reasonKey: 'restore.errors.x' };

        view.runNotice.set(notice);
        runActions(harness)!.started.emit();
        expect(view.runNotice()).toBeNull();

        view.runNotice.set(notice);
        const running = signal(false);
        TestBed.inject(SevenTvRunArbiter).register({
          kind: 'restore',
          isRunning: running,
          isSettling: signal(false),
          destructiveOpen: signal(false),
        });
        await settle(harness);
        expect(view.runNotice()).not.toBeNull();
        running.set(true);
        await settle(harness);
        expect(view.runNotice()).toBeNull();
      });
    });

    describe('reloads (spec 9.6)', () => {
      const OWN = entry('e1', {
        placedByThisTag: true,
        placedAtUtc: '2026-10-01T10:00:00Z',
        placementOperationId: 'rev-1',
      });

      function emitSynced(): Promise<void> {
        FakeEventSource.instances
          .find((source) => source.url === channelLiveUrl('a'))!
          .emit({ type: LIVE_EVENT_TYPES.channelSynced, channel: 'a' });
        return new Promise((resolve) => setTimeout(resolve, CHANNEL_RELOAD_DEBOUNCE_MS + 20));
      }

      /** Opens the clear-out dialog of a played-in tag and leaves it open on `closed`. */
      async function openRemovalDialog(harness: RouterTestingHarness, closed: Subject<unknown>) {
        dialogOpen.mockImplementation((component: unknown) =>
          component === TagRemovalConfirmDialog ? { closed } : { closed: of(dialogResult) },
        );
        removeButton(harness).click();
        await settle(harness);
        httpMock
          .expectOne((req) => req.url === `${BASE}/1/entries` && req.method === 'GET')
          .flush({
            emoteSetId: 'set-a',
            isActiveSet: true,
            activationOperationId: 'act-1',
            entries: [OWN],
          });
        await settle(harness);
        httpMock
          .expectOne(`${BASE}/1/operations`)
          .flush({ registeredAtUtc: '2026-10-05T10:00:00Z' });
        await settle(harness);
        flushLiveRead();
        await settle(harness);
        expect(
          dialogOpen.mock.calls.some(([component]) => component === TagRemovalConfirmDialog),
        ).toBe(true);
      }

      async function answerReload(harness: RouterTestingHarness, activeSet: string) {
        await settle(harness);
        statusAnswer = () => of(setStatus(activeSet));
      }

      it('a channel.synced of the same set keeps TagRunActions and its open dialog: the clear-out goes ahead', async () => {
        const { harness } = await openTag(activeTag(1, 'Stronghold'), [OWN]);
        const before = runActions(harness);
        const startDelete = vi
          .spyOn(TestBed.inject(SevenTvDeleteService), 'startDelete')
          .mockImplementation(() => undefined);
        const closed = new Subject<unknown>();
        await openRemovalDialog(harness, closed);

        await answerReload(harness, 'set-a');
        await emitSynced();
        await settle(harness);
        // Mid-reload: same component, and the set it hands its flow never went away.
        expect(runActions(harness)).toBe(before);
        expect(before!.activeEmoteSetId()).toBe('set-a');
        expectList().flush(tagList(activeTag(1, 'Stronghold'), tag(2, 'Halloween')));
        expectEntries(1).flush({
          emoteSetId: 'set-a',
          isActiveSet: true,
          activationOperationId: 'act-1',
          entries: [OWN],
        });
        await settle(harness);
        expect(runActions(harness)).toBe(before);

        closed.next({ checkedIds: ['e1'] });
        closed.complete();
        await settle(harness);
        // The entry read right before the delete (R4): unchanged, so the clear-out goes ahead.
        expect(startDelete).not.toHaveBeenCalled();
        expectEntries(1).flush({
          emoteSetId: 'set-a',
          isActiveSet: true,
          activationOperationId: 'act-1',
          entries: [OWN],
        });
        await settle(harness);

        expect(startDelete).toHaveBeenCalledTimes(1);
        expect(startDelete.mock.calls[0][0]).toBe('set-a');
      });

      it('a channel.synced that switches the active set tears the detail down, so the open clear-out never starts', async () => {
        const { harness, page } = await openTag(activeTag(1, 'Stronghold'), [OWN]);
        const before = runActions(harness);
        const startDelete = vi
          .spyOn(TestBed.inject(SevenTvDeleteService), 'startDelete')
          .mockImplementation(() => undefined);
        const closed = new Subject<unknown>();
        await openRemovalDialog(harness, closed);

        await answerReload(harness, 'set-b');
        await emitSynced();
        await settle(harness);
        expect(page.activeEmoteSetId()).toBe('set-b');
        // The tags follow the new set.
        for (const request of httpMock.match((req) => req.url === BASE)) {
          if (!request.cancelled) {
            expect(request.request.params.get('emoteSetId')).toBe('set-b');
            request.flush({ emoteSetId: 'set-b', isActiveSet: true, tags: [tag(1, 'Stronghold')] });
          }
        }
        await settle(harness);
        httpMock
          .match((req) => req.url === `${BASE}/1/entries`)
          .forEach((request) =>
            request.flush({
              emoteSetId: 'set-b',
              isActiveSet: true,
              activationOperationId: null,
              entries: [],
            }),
          );
        await settle(harness);

        expect(runActions(harness)).not.toBe(before);

        closed.next({ checkedIds: ['e1'] });
        closed.complete();
        await settle(harness);

        expect(startDelete).not.toHaveBeenCalled();
        // The abort is not silent: the header's own region says it, where the torn-down
        // TagRunActions' banner would have.
        const spoken = Array.from(
          harness.routeNativeElement!.querySelectorAll('header [role="status"].sr-only'),
        )
          .map((region) => region.textContent?.replace(/\s+/g, ' ').trim() ?? '')
          .join(' ');
        expect(spoken).toContain(
          `${de.massDelete.abortedByLock} ${de.massDelete.setChangedDuringConfirm}`,
        );
        expect(buttonByName(harness, 'Schließen')).toBeTruthy();
      });

      it('a failed status reload keeps the last known active set and TagRunActions', async () => {
        const { harness, page } = await openTag(activeTag(1, 'Stronghold'), [OWN]);
        const before = runActions(harness);

        statusAnswer = () => throwError(() => new HttpErrorResponse({ status: 500 }));
        await emitSynced();
        await settle(harness);

        expect(page.activeEmoteSetId()).toBe('set-a');
        expectList().flush(tagList(activeTag(1, 'Stronghold'), tag(2, 'Halloween')));
        expectEntries(1).flush({
          emoteSetId: 'set-a',
          isActiveSet: true,
          activationOperationId: 'act-1',
          entries: [OWN],
        });
        await settle(harness);
        expect(page.activeEmoteSetId()).toBe('set-a');
        expect(runActions(harness)).toBe(before);
      });

      it('does not read tags again for a restore that was already closed when the page opened', async () => {
        const restore = TestBed.inject(SevenTvRestoreService);
        restore.run.set({ runId: 'r0', phase: 'closed', destructive: false } as never);
        const { harness } = await openTag(tag(1, 'Stronghold'));
        await settle(harness);

        httpMock.expectNone((req) => req.url === BASE);
      });

      it('reads tags and entries again when a restore closes, not for one already closed', async () => {
        const restore = TestBed.inject(SevenTvRestoreService);
        const { harness } = await openTag(tag(1, 'Stronghold'));

        restore.run.set({ runId: 'r1', phase: 'running', destructive: false } as never);
        await settle(harness);
        httpMock.expectNone((req) => req.url === BASE);

        restore.run.set({ runId: 'r1', phase: 'closed', destructive: false } as never);
        await settle(harness);
        expectList().flush(tagList(tag(1, 'Stronghold'), tag(2, 'Halloween')));
        expectEntries(1).flush({ emoteSetId: 'set-a', isActiveSet: true, entries: [] });
        restore.run.set(null);
      });
    });

    // Operator decision 2026-10-05: a marking on the grid is what the clear-out proposes.
    describe('a clear-out with a grid marking', () => {
      const PLACED = entry('e1', {
        placedByThisTag: true,
        placedAtUtc: '2026-10-01T10:00:00Z',
        placementOperationId: 'rev-1',
      });
      const ENTRIES = [PLACED, entry('e2'), entry('e3')];
      const TAG_ENTRIES = {
        emoteSetId: 'set-a',
        isActiveSet: true,
        activationOperationId: 'act-1',
        entries: ENTRIES,
      };

      /** Clicks "Aus dem Set entfernen" and answers the flow's reads; `meanwhile` runs between the click and the
       *  dialog, while the flow is still reading. */
      async function clickClearOut(
        harness: RouterTestingHarness,
        closed: Subject<unknown>,
        meanwhile: () => Promise<void> = async () => undefined,
      ): Promise<TagRemovalConfirmDialogData> {
        dialogOpen.mockImplementation((component: unknown) =>
          component === TagRemovalConfirmDialog ? { closed } : { closed: of(dialogResult) },
        );
        removeButton(harness).click();
        await settle(harness);
        await meanwhile();
        expectEntries(1).flush(TAG_ENTRIES);
        await settle(harness);
        httpMock
          .expectOne(`${BASE}/1/operations`)
          .flush({ registeredAtUtc: '2026-10-05T10:00:00Z' });
        await settle(harness);
        flushLiveRead(['e1', 'e2', 'e3']);
        await settle(harness);
        const call = dialogOpen.mock.calls.find(
          ([component]) => component === TagRemovalConfirmDialog,
        ) as unknown as [unknown, { data: TagRemovalConfirmDialogData }];
        return call[1].data;
      }

      it('proposes the marking as it stood at the click, and keeps it on a cancel', async () => {
        const { harness, page } = await openTag(activeTag(1, 'Stronghold'), ENTRIES);
        cells(harness)[1].click();
        await settle(harness);
        const closed = new Subject<unknown>();

        const data = await clickClearOut(harness, closed, async () => {
          // Marked after the click: not part of this clear-out.
          cells(harness)[2].click();
          await settle(harness);
        });

        expect(data.proposal.fromMarking).toBe(true);
        expect(
          data.proposal.rows.map((row) => [row.sevenTvEmoteId, row.checked, row.reason]),
        ).toEqual([
          ['e1', false, 'notMarked'],
          ['e2', true, 'alreadyPresent'],
          ['e3', false, 'notMarked'],
        ]);

        closed.next(undefined);
        await settle(harness);
        expect(page.selection.selectedKeys()).toEqual(['e2', 'e3']);
      });

      it('drops the marking once the confirmed clear-out has started', async () => {
        const { harness, page } = await openTag(activeTag(1, 'Stronghold'), ENTRIES);
        const startDelete = vi
          .spyOn(TestBed.inject(SevenTvDeleteService), 'startDelete')
          .mockImplementation(() => undefined);
        cells(harness)[1].click();
        await settle(harness);
        const closed = new Subject<unknown>();
        await clickClearOut(harness, closed);

        closed.next({ checkedIds: ['e2'] });
        await settle(harness);
        // Still marked while the entry re-read is out: nothing has started yet.
        expect(page.selection.selectedKeys()).toEqual(['e2']);
        expectEntries(1).flush(TAG_ENTRIES);
        await settle(harness);

        expect(startDelete).toHaveBeenCalledTimes(1);
        expect(page.selection.selectedKeys()).toEqual([]);
      });

      it('proposes as before without a marking', async () => {
        const { harness } = await openTag(activeTag(1, 'Stronghold'), ENTRIES);
        const data = await clickClearOut(harness, new Subject<unknown>());

        expect(data.proposal.fromMarking).toBe(false);
        expect(
          data.proposal.rows.filter((row) => row.checked).map((row) => row.sevenTvEmoteId),
        ).toEqual(['e1']);
      });
    });
  });
});
