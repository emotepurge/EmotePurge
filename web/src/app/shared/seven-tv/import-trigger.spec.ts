import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, of, Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { ImportSource } from '../../core/seven-tv/import-source';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { PurgeRunRow } from '../export/purge-run-export';
import { FileImportResult } from './file-import-step';
import { ImportSourceDialogResult } from './import-source-dialog';
import { ImportTrigger } from './import-trigger';

/**
 * `ImportTrigger` opens every dialog through the plain `Dialog` it injects, same as
 * `startRestoreFlow`/`startImportFlow` do with the one they are handed: `dialog.open` is one
 * `vi.fn()` standing in for the import-source dialog itself, the token prompt, the restore
 * confirmation and the import confirmation alike, distinguished by call order and by the side
 * effects (`getSetStatus`/`startRestore`/`startImport`) each step is allowed to have triggered by
 * the time it runs.
 */

// Only the key this trigger itself renders.
const DE_TRANSLATIONS = { restore: { import: { trigger: 'Importieren' } } };

const CURRENT_CHANNEL = 'somechannel';
const CURRENT_SET = 'set-current';

function rows(): PurgeRunRow[] {
  return [
    { emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done', errorMessage: null },
  ];
}

function importSource(overrides: Partial<ImportSource> = {}): ImportSource {
  return {
    origin: {
      kind: 'file',
      fileName: 'export.json',
      exportedAt: null,
      channelName: null,
      envelopeKind: 'emote-list',
    },
    rows: [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
    duplicatesCollapsed: 0,
    discardedRows: 0,
    ...overrides,
  };
}

/** A `filterAlreadyPresent` GQL page response (`already-present-filter.ts`) containing exactly the
 *  given 7TV emote ids, as the single (and last) page. */
function emoteSetPage(ids: string[] = []) {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount: ids.length,
            pageCount: 1,
            items: ids.map((id) => ({ emote: { id } })),
          },
        },
      },
    },
  };
}

function readyStatus(overrides: Partial<EmoteSetStatus> = {}): EmoteSetStatus {
  return {
    activeEmoteSetId: CURRENT_SET,
    capacity: 1000,
    occupiedSlots: 10,
    trackedSince: '2026-01-01T00:00:00Z',
    syncFailureReason: null,
    lastSyncAttemptAtUtc: null,
    botsExcludedSince: null,
    sharedChatSeparatedSince: null,
    duplicateNames: [],
    ...overrides,
  };
}

interface Harness {
  fixture: ComponentFixture<ImportTrigger>;
  detect(): void;
  triggerDisabled(): boolean;
  click(): void;
}

describe('ImportTrigger', () => {
  let getSetStatus: ReturnType<typeof vi.fn>;
  let listEmotes: ReturnType<typeof vi.fn>;
  let getSetWarning: ReturnType<typeof vi.fn>;
  /** The fresh #149/T5 duplicate check (`already-present-filter.ts`) — since the P1 fix this is a
   *  raw `HttpClient.post` straight to 7TV, not `emoteAdminService`/`listEmotes`. Defaults to an
   *  empty target set, i.e. every existing expectation below (skip count 0, available true) still
   *  holds unless a test overrides it. */
  let httpPost: ReturnType<typeof vi.fn>;
  let startRestore: ReturnType<typeof vi.fn>;
  let startImport: ReturnType<typeof vi.fn>;
  let loadEmoteSetPreview: ReturnType<typeof vi.fn>;
  let hasToken: WritableSignal<boolean>;
  let activeRun: WritableSignal<SevenTvRunKind | null>;
  let dialogOpen: ReturnType<typeof vi.fn>;

  beforeEach(async () => {
    getSetStatus = vi.fn(() => of(readyStatus()));
    listEmotes = vi.fn(() => of([]));
    getSetWarning = vi.fn(() =>
      of({
        available: true,
        isOwnSet: true,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      }),
    );
    httpPost = vi.fn(() => of(emoteSetPage()));
    startRestore = vi.fn();
    startImport = vi.fn();
    // Never reached by any test that leaves `activeSetId` at its default (`null`) — those always
    // take the 'trackedActive' fast path (see `toImportTarget`'s own doc). Only the non-active-set
    // describe block below overrides this.
    loadEmoteSetPreview = vi.fn();
    hasToken = signal(true);
    activeRun = signal<SevenTvRunKind | null>(null);
    dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));

    await TestBed.configureTestingModule({
      imports: [
        ImportTrigger,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: EmoteAdminService,
          useValue: { getSetStatus, listEmotes, getSetWarning } as unknown as EmoteAdminService,
        },
        { provide: HttpClient, useValue: { post: httpPost } as unknown as HttpClient },
        {
          provide: SevenTvRestoreService,
          useValue: { startRestore } as unknown as SevenTvRestoreService,
        },
        {
          provide: SevenTvImportService,
          useValue: { startImport } as unknown as SevenTvImportService,
        },
        {
          provide: SevenTvEmoteSetService,
          useValue: { loadEmoteSetPreview } as unknown as SevenTvEmoteSetService,
        },
        { provide: SevenTvTokenService, useValue: { hasToken } as unknown as SevenTvTokenService },
        { provide: SevenTvRunArbiter, useValue: { activeRun } as unknown as SevenTvRunArbiter },
        { provide: Dialog, useValue: { open: dialogOpen } as unknown as Dialog },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(
    channelName = CURRENT_CHANNEL,
    setId = CURRENT_SET,
    options: { activeSetId?: string | null; setName?: string | null } = {},
  ): Harness {
    const fixture = TestBed.createComponent(ImportTrigger);
    fixture.componentRef.setInput('channelName', channelName);
    fixture.componentRef.setInput('setId', setId);
    if (options.activeSetId !== undefined) {
      fixture.componentRef.setInput('activeSetId', options.activeSetId);
    }
    if (options.setName !== undefined) {
      fixture.componentRef.setInput('setName', options.setName);
    }
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    return {
      fixture,
      detect: () => fixture.detectChanges(),
      triggerDisabled: () => {
        const button = host.querySelector('button');
        if (!button) {
          throw new Error('no trigger button rendered');
        }
        return button.disabled;
      },
      click: () => {
        const button = host.querySelector('button');
        if (!button) {
          throw new Error('no trigger button rendered');
        }
        button.click();
        fixture.detectChanges();
      },
    };
  }

  /** The `closed` subject a dialog was opened with — call `index` of `dialog.open`. */
  function closedAt<T>(index: number): Subject<T> {
    return dialogOpen.mock.results[index].value.closed as Subject<T>;
  }

  /** The `data` a call to `dialog.open` was handed. */
  function dataAt(index: number): unknown {
    return dialogOpen.mock.calls[index][1].data;
  }

  describe('opening the import-source dialog', () => {
    it('opens exactly one dialog with the current channel and set frozen into its data', () => {
      const dialog = render('achannel', 'aset');

      dialog.click();

      expect(dialogOpen).toHaveBeenCalledTimes(1);
      expect(dataAt(0)).toEqual({
        channelName: 'achannel',
        setId: 'aset',
        restoreEnabled: true,
      });
    });

    it('does nothing further when the import dialog closes with no result (cancel/Escape/backdrop)', () => {
      const dialog = render();
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next(undefined);

      expect(dialogOpen).toHaveBeenCalledTimes(1);
      expect(startRestore).not.toHaveBeenCalled();
      expect(startImport).not.toHaveBeenCalled();
    });

    it('freezes channelName/setId at the moment of the click, not at the moment a later dialog resolves', () => {
      const dialog = render('channel-a', 'set-a');
      dialog.click();

      // Simulate a same-route channel switch while the import dialog is still open.
      dialog.fixture.componentRef.setInput('channelName', 'channel-b');
      dialog.fixture.componentRef.setInput('setId', 'set-b');
      dialog.detect();

      closedAt<FileImportResult | undefined>(0).next({ kind: 'restore', rows: rows() });
      closedAt<boolean>(1).next(true);

      // Fourth argument is the #149/T5 duplicate-check skip count — 0 because the fresh 7TV read
      // (`httpPost`) defaults to an empty target set. Fifth is whether that check actually ran
      // (#149).
      expect(startRestore).toHaveBeenCalledWith(
        'set-a',
        'channel-a',
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' }],
        0,
        true,
      );
    });
  });

  describe('restore result: token prompt before the confirmation', () => {
    it('prompts for a token first when none is stored', () => {
      hasToken.set(false);
      const dialog = render();
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next({ kind: 'restore', rows: rows() });

      // Only the token prompt has opened so far — the restore-specific work (the slot preview
      // read) has not started, proof the confirmation is not up yet.
      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(getSetStatus).not.toHaveBeenCalled();
      expect(startRestore).not.toHaveBeenCalled();

      closedAt<boolean>(1).next(true);

      // The confirmation opens only now, and the slot preview is fetched for it.
      expect(dialogOpen).toHaveBeenCalledTimes(3);
      expect(getSetStatus).toHaveBeenCalledWith(CURRENT_CHANNEL);

      closedAt<boolean>(2).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        CURRENT_SET,
        CURRENT_CHANNEL,
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU' }],
        0,
        true,
      );
    });

    it('goes straight to the confirmation when a token is already stored', () => {
      hasToken.set(true);
      const dialog = render();
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next({ kind: 'restore', rows: rows() });

      // One dialog beyond the source dialog, and it is already the confirmation.
      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(getSetStatus).toHaveBeenCalledWith(CURRENT_CHANNEL);

      closedAt<boolean>(1).next(true);

      expect(startRestore).toHaveBeenCalledTimes(1);
    });

    it('never restores when the token prompt is cancelled', () => {
      hasToken.set(false);
      const dialog = render();
      dialog.click();
      closedAt<FileImportResult | undefined>(0).next({ kind: 'restore', rows: rows() });

      closedAt<boolean>(1).next(false);

      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(startRestore).not.toHaveBeenCalled();
    });

    it('never restores when the confirmation is cancelled', () => {
      const dialog = render();
      dialog.click();
      closedAt<FileImportResult | undefined>(0).next({ kind: 'restore', rows: rows() });

      closedAt<boolean>(1).next(false);

      expect(startRestore).not.toHaveBeenCalled();
    });
  });

  describe('import result: confirmation before the token prompt', () => {
    it('opens the import confirmation first, without prompting for a token yet', () => {
      hasToken.set(false);
      const dialog = render();
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next({
        kind: 'import',
        source: importSource(),
      });

      // Reversed order from the restore path: the confirmation is already open (its own target
      // load fires getSetStatus), the token is not asked for yet.
      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(getSetStatus).toHaveBeenCalledWith(CURRENT_CHANNEL);
      expect(startImport).not.toHaveBeenCalled();

      closedAt<{ targetSetId: string; targetSetName: string; rows: unknown[] }>(1).next({
        targetSetId: CURRENT_SET,
        targetSetName: CURRENT_SET,
        rows: [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
      });

      // Only now, after the confirmation, does the missing-token case appear.
      expect(dialogOpen).toHaveBeenCalledTimes(3);
      expect(startImport).not.toHaveBeenCalled();

      closedAt<boolean>(2).next(true);

      // The target is always the frozen (current) channel, never the file's own origin.
      expect(startImport).toHaveBeenCalledWith(
        {
          setId: CURRENT_SET,
          channelName: CURRENT_CHANNEL,
          ownerDisplayName: null,
          setName: CURRENT_SET,
          isActiveSet: true,
        },
        expect.objectContaining({ kind: 'file' }),
        [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
        0,
        true,
      );
    });

    it('skips the token prompt entirely when a token is already stored', () => {
      hasToken.set(true);
      const dialog = render();
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next({
        kind: 'import',
        source: importSource(),
      });
      expect(dialogOpen).toHaveBeenCalledTimes(2);

      closedAt<{ targetSetId: string; targetSetName: string; rows: unknown[] }>(1).next({
        targetSetId: CURRENT_SET,
        targetSetName: CURRENT_SET,
        rows: [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
      });

      // No third dialog: the flow's own (already-satisfied) token check does not prompt twice.
      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(startImport).toHaveBeenCalledTimes(1);
    });

    it('does not start the import when the confirmation is dismissed without an outcome', () => {
      const dialog = render();
      dialog.click();
      closedAt<FileImportResult | undefined>(0).next({
        kind: 'import',
        source: importSource(),
      });

      closedAt<undefined>(1).next(undefined);

      expect(startImport).not.toHaveBeenCalled();
      expect(dialogOpen).toHaveBeenCalledTimes(2);
    });
  });

  describe('foreign-channel result: straight into the import confirmation, no target picker (#147)', () => {
    it("confirms against this page's channel without asking where the emotes should go", () => {
      hasToken.set(true);
      const dialog = render();
      dialog.click();

      closedAt<ImportSourceDialogResult | undefined>(0).next({
        kind: 'foreign',
        picked: {
          channelName: 'handofblood',
          sevenTvUserId: 'user-1',
          emoteSetId: 'set-source',
          rows: [
            {
              sevenTvEmoteId: '7tv-1',
              name: 'HandLuL',
              defaultName: 'LuL',
              imageUrl: 'https://cdn.7tv.app/7tv-1/2x.webp',
              topAllTime: null,
              trending: null,
            },
          ],
        },
      });

      // Exactly one further dialog, and it is the confirmation: the old target picker in between
      // asked a question that was already answered by the page the trigger sits on.
      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(getSetStatus).toHaveBeenCalledWith(CURRENT_CHANNEL);

      closedAt<{ targetSetId: string; targetSetName: string; rows: unknown[] }>(1).next({
        targetSetId: CURRENT_SET,
        targetSetName: CURRENT_SET,
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'HandLuL' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        {
          setId: CURRENT_SET,
          channelName: CURRENT_CHANNEL,
          ownerDisplayName: null,
          setName: CURRENT_SET,
          isActiveSet: true,
        },
        { kind: 'seventv-channel', channelName: 'handofblood' },
        [{ sevenTvEmoteId: '7tv-1', name: 'HandLuL' }],
        0,
        true,
      );
    });
  });

  describe('leaderboard result: the same target rule, with even less to ask (#148)', () => {
    it("confirms against this page's channel and carries the list as the origin", () => {
      hasToken.set(true);
      const dialog = render();
      dialog.click();

      closedAt<ImportSourceDialogResult | undefined>(0).next({
        kind: 'leaderboard',
        picked: {
          sortBy: 'TOP_ALL_TIME',
          rows: [
            {
              sevenTvEmoteId: '7tv-2',
              name: 'Dance',
              defaultName: 'Dance',
              imageUrl: 'https://cdn.7tv.app/7tv-2/2x.webp',
              topAllTime: 99,
              trending: 12,
            },
          ],
        },
      });

      // One further dialog, and it is the confirmation: a leaderboard row belongs to no channel at
      // all, so the target question the page already answers is even less open than for a channel.
      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(getSetStatus).toHaveBeenCalledWith(CURRENT_CHANNEL);

      closedAt<{ targetSetId: string; targetSetName: string; rows: unknown[] }>(1).next({
        targetSetId: CURRENT_SET,
        targetSetName: CURRENT_SET,
        rows: [{ sevenTvEmoteId: '7tv-2', name: 'Dance' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        {
          setId: CURRENT_SET,
          channelName: CURRENT_CHANNEL,
          ownerDisplayName: null,
          setName: CURRENT_SET,
          isActiveSet: true,
        },
        { kind: 'seventv-leaderboard', sortBy: 'TOP_ALL_TIME' },
        [{ sevenTvEmoteId: '7tv-2', name: 'Dance' }],
        0,
        true,
      );
    });
  });

  describe('a non-active set on screen (#200, T4.5): the three import doors follow it, restore stays locked', () => {
    it('marks the source-dialog data as restore-locked when setId differs from activeSetId', () => {
      const dialog = render(CURRENT_CHANNEL, 'set-halloween', { activeSetId: CURRENT_SET });
      dialog.click();

      expect(dataAt(0)).toEqual({
        channelName: CURRENT_CHANNEL,
        setId: 'set-halloween',
        restoreEnabled: false,
      });
    });

    it('reads the non-active set live instead of assuming it is the channel’s active one — the file/import door', () => {
      loadEmoteSetPreview.mockReturnValue(
        of({
          channelName: CURRENT_CHANNEL,
          sevenTvUserId: null,
          emoteSetId: 'set-halloween',
          emoteSetName: 'Halloween',
          capacity: 1000,
          totalCount: 0,
          truncated: false,
          emotes: [],
        }),
      );
      hasToken.set(true);
      const dialog = render(CURRENT_CHANNEL, 'set-halloween', {
        activeSetId: CURRENT_SET,
        setName: 'Halloween',
      });
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next({ kind: 'import', source: importSource() });

      // Not the "today" path: a non-active target reads the live preview instead of
      // EmoteSetStatus/listEmotes/getSetWarning (spec F5).
      expect(loadEmoteSetPreview).toHaveBeenCalledWith(CURRENT_CHANNEL, 'set-halloween');
      expect(getSetStatus).not.toHaveBeenCalled();

      closedAt<{ targetSetId: string; targetSetName: string; rows: unknown[] }>(1).next({
        targetSetId: 'set-halloween',
        targetSetName: 'Halloween',
        rows: [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        {
          setId: 'set-halloween',
          channelName: CURRENT_CHANNEL,
          ownerDisplayName: null,
          setName: 'Halloween',
          isActiveSet: false,
        },
        expect.objectContaining({ kind: 'file' }),
        [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
        0,
        true,
      );
    });

    it('targets the non-active set for a foreign-channel pick too', () => {
      loadEmoteSetPreview.mockReturnValue(
        of({
          channelName: CURRENT_CHANNEL,
          sevenTvUserId: null,
          emoteSetId: 'set-halloween',
          emoteSetName: 'Halloween',
          capacity: 1000,
          totalCount: 0,
          truncated: false,
          emotes: [],
        }),
      );
      const dialog = render(CURRENT_CHANNEL, 'set-halloween', { activeSetId: CURRENT_SET });
      dialog.click();

      closedAt<ImportSourceDialogResult | undefined>(0).next({
        kind: 'foreign',
        picked: {
          channelName: 'handofblood',
          sevenTvUserId: 'user-1',
          emoteSetId: 'set-source',
          rows: [
            {
              sevenTvEmoteId: '7tv-1',
              name: 'HandLuL',
              defaultName: 'LuL',
              imageUrl: 'https://cdn.7tv.app/7tv-1/2x.webp',
              topAllTime: null,
              trending: null,
            },
          ],
        },
      });

      expect(loadEmoteSetPreview).toHaveBeenCalledWith(CURRENT_CHANNEL, 'set-halloween');

      closedAt<{ targetSetId: string; targetSetName: string; rows: unknown[] }>(1).next({
        targetSetId: 'set-halloween',
        targetSetName: 'Halloween',
        rows: [{ sevenTvEmoteId: '7tv-1', name: 'HandLuL' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        expect.objectContaining({ setId: 'set-halloween', isActiveSet: false }),
        { kind: 'seventv-channel', channelName: 'handofblood' },
        [{ sevenTvEmoteId: '7tv-1', name: 'HandLuL' }],
        0,
        true,
      );
    });

    it('targets the non-active set for a leaderboard pick too', () => {
      loadEmoteSetPreview.mockReturnValue(
        of({
          channelName: CURRENT_CHANNEL,
          sevenTvUserId: null,
          emoteSetId: 'set-halloween',
          emoteSetName: 'Halloween',
          capacity: 1000,
          totalCount: 0,
          truncated: false,
          emotes: [],
        }),
      );
      const dialog = render(CURRENT_CHANNEL, 'set-halloween', { activeSetId: CURRENT_SET });
      dialog.click();

      closedAt<ImportSourceDialogResult | undefined>(0).next({
        kind: 'leaderboard',
        picked: {
          sortBy: 'TOP_ALL_TIME',
          rows: [
            {
              sevenTvEmoteId: '7tv-2',
              name: 'Dance',
              defaultName: 'Dance',
              imageUrl: 'https://cdn.7tv.app/7tv-2/2x.webp',
              topAllTime: 99,
              trending: 12,
            },
          ],
        },
      });

      expect(loadEmoteSetPreview).toHaveBeenCalledWith(CURRENT_CHANNEL, 'set-halloween');

      closedAt<{ targetSetId: string; targetSetName: string; rows: unknown[] }>(1).next({
        targetSetId: 'set-halloween',
        targetSetName: 'Halloween',
        rows: [{ sevenTvEmoteId: '7tv-2', name: 'Dance' }],
      });

      expect(startImport).toHaveBeenCalledWith(
        expect.objectContaining({ setId: 'set-halloween', isActiveSet: false }),
        { kind: 'seventv-leaderboard', sortBy: 'TOP_ALL_TIME' },
        [{ sevenTvEmoteId: '7tv-2', name: 'Dance' }],
        0,
        true,
      );
    });
  });

  describe('trigger lock', () => {
    it('disables exactly while arbiter.activeRun() is not null', () => {
      const dialog = render();
      expect(dialog.triggerDisabled()).toBe(false);

      activeRun.set('delete');
      dialog.detect();
      expect(dialog.triggerDisabled()).toBe(true);

      activeRun.set(null);
      dialog.detect();
      expect(dialog.triggerDisabled()).toBe(false);
    });

    it('disables while importScopeCurrent is false', () => {
      const fixture = TestBed.createComponent(ImportTrigger);
      fixture.componentRef.setInput('channelName', CURRENT_CHANNEL);
      fixture.componentRef.setInput('setId', CURRENT_SET);
      fixture.componentRef.setInput('importScopeCurrent', false);
      fixture.detectChanges();
      const button = (fixture.nativeElement as HTMLElement).querySelector('button');
      if (!button) {
        throw new Error('no trigger button rendered');
      }

      expect(button.disabled).toBe(true);
    });

    it('defaults importScopeCurrent to true when the caller does not pass it', () => {
      const dialog = render();
      expect(dialog.triggerDisabled()).toBe(false);
    });
  });
});
