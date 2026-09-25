import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, of, Subject } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteSetStatus } from '../../core/emotes/emote-set-status.model';
import { ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRestoreService } from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { TransferPlan } from '../../core/seven-tv/transfer-plan';
import { PurgeRunRow } from '../export/purge-run-export';
import { FileImportResult } from './file-import-step';
import { ImportSourceDialogResult } from './import-source-dialog';
import { ImportTrigger } from './import-trigger';
import { ResolvedRestoreTarget } from './restore-flow';

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
    {
      emoteId: 'e1',
      sevenTvEmoteId: '7tv-1',
      name: 'PogU',
      aliases: ['PogU'],
      status: 'done',
      errorMessage: null,
    },
  ];
}

/**
 * What `ImportSourceDialog` closes with for a restore file: the rows plus the target the file step
 * already resolved and cleared (spec #253, 6.1). Defaults to this page's own active set, as the
 * target list would describe it; a test overrides whatever its case is about.
 */
function restoreResult(target: Partial<ResolvedRestoreTarget> = {}): FileImportResult {
  return {
    kind: 'restore',
    rows: rows(),
    target: {
      emoteSetId: CURRENT_SET,
      setName: 'Hauptset',
      ownerDisplayName: CURRENT_CHANNEL,
      twitchLogin: CURRENT_CHANNEL,
      trackedChannelName: CURRENT_CHANNEL,
      isActiveSet: true,
      hostChannelName: CURRENT_CHANNEL,
      hostSelectedSetId: CURRENT_SET,
      ...target,
    },
  };
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
    rows: [{ sevenTvEmoteId: '7tv-9', name: 'Kappa', imageUrl: null }],
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

/** The plan a confirmation that resolved nothing hands to the run: one `add` row per row. */
function addPlan(rows: ImportRow[]): TransferPlan {
  return { rows: rows.map((row) => ({ action: 'add', source: row, alias: row.name })) };
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
    // Never reached by any test that leaves `activeSetId` omitted (`undefined`) — those always take
    // the 'trackedActive' fast path (see `resolveActiveSetId`). Only the non-active-set and the
    // unknown-active-set describe blocks below override this.
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
    setId: string | null = CURRENT_SET,
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
      });
    });

    // Spec #253, E22: a channel page without a selected set (before its first sync, or after a
    // replace into the untracked) still has a way in — the trigger opens the dialog with a `null`
    // target, which `ImportSourceDialog` uses to disable its two copy doors (own spec).
    it('opens the dialog with setId: null when the page has no selected set', () => {
      const dialog = render(CURRENT_CHANNEL, null);
      dialog.click();

      expect(dialogOpen).toHaveBeenCalledTimes(1);
      expect(dataAt(0)).toEqual({
        channelName: CURRENT_CHANNEL,
        setId: null,
      });
    });

    // A restore file names and clears its own target regardless of the page's set (spec 6.1) — the
    // one result a dialog opened with `setId: null` can actually close with, since its two copy
    // doors are disabled. `hostSelectedSetId` carries the `null` through unchanged, for the
    // confirmation's "not the set on screen" hint (E21).
    it('still starts a restore flow through a dialog opened with setId: null', () => {
      const dialog = render(CURRENT_CHANNEL, null);
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next(
        restoreResult({ hostChannelName: CURRENT_CHANNEL, hostSelectedSetId: null }),
      );
      closedAt<boolean>(1).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: CURRENT_SET, hostChannelName: CURRENT_CHANNEL }),
        expect.any(Array),
        expect.any(Number),
        expect.any(Boolean),
        expect.any(Number),
      );
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

      // The dialog was handed the values of the click, not the ones switched to since.
      expect(dataAt(0)).toEqual({ channelName: 'channel-a', setId: 'set-a' });
      closedAt<FileImportResult | undefined>(0).next(
        restoreResult({
          emoteSetId: 'set-a',
          trackedChannelName: 'channel-a',
          hostChannelName: 'channel-a',
          hostSelectedSetId: 'set-a',
        }),
      );
      closedAt<boolean>(1).next(true);

      // Fourth argument is the #149/T5 duplicate-check skip count — 0 because the fresh 7TV read
      // (`httpPost`) defaults to an empty target set. Fifth is whether that check actually ran
      // (#149), sixth its name-taken count.
      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: 'set-a', hostChannelName: 'channel-a' }),
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
        0,
        true,
        0,
      );
    });
  });

  describe("a restore file: the file step's resolved target goes to the flow unchanged (spec #253, 6.1)", () => {
    it("restores into the set the file named, as the step resolved it — not into this page's set", () => {
      loadEmoteSetPreview.mockReturnValue(
        of({
          channelName: 'besitzerin',
          sevenTvUserId: null,
          emoteSetId: 'set-foreign',
          emoteSetName: 'Fremdes Set',
          capacity: 1000,
          totalCount: 10,
          truncated: false,
          emotes: [],
        }),
      );
      const dialog = render();
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next(
        restoreResult({
          emoteSetId: 'set-foreign',
          setName: 'Fremdes Set',
          ownerDisplayName: 'Besitzerin',
          twitchLogin: 'besitzerin',
          trackedChannelName: null,
          isActiveSet: false,
        }),
      );

      // An untracked target: the slot preview reads the account's own set, never this page's
      // channel status — proof the page's frozen values built no part of the target.
      expect(loadEmoteSetPreview).toHaveBeenCalledWith('besitzerin', 'set-foreign');
      expect(getSetStatus).not.toHaveBeenCalled();

      closedAt<boolean>(1).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({
          setId: 'set-foreign',
          expectedChannelName: null,
          resyncChannelName: null,
          hostChannelName: CURRENT_CHANNEL,
          setName: 'Fremdes Set',
          ownerOrChannelLabel: 'Besitzerin',
        }),
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
        0,
        true,
        0,
      );
    });
  });

  describe('restore result: token prompt before the confirmation', () => {
    it('prompts for a token first when none is stored', () => {
      hasToken.set(false);
      const dialog = render();
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next(restoreResult());

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
        expect.objectContaining({ setId: CURRENT_SET, hostChannelName: CURRENT_CHANNEL }),
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
        0,
        true,
        0,
      );
    });

    it('goes straight to the confirmation when a token is already stored', () => {
      hasToken.set(true);
      const dialog = render();
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next(restoreResult());

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
      closedAt<FileImportResult | undefined>(0).next(restoreResult());

      closedAt<boolean>(1).next(false);

      expect(dialogOpen).toHaveBeenCalledTimes(2);
      expect(startRestore).not.toHaveBeenCalled();
    });

    it('never restores when the confirmation is cancelled', () => {
      const dialog = render();
      dialog.click();
      closedAt<FileImportResult | undefined>(0).next(restoreResult());

      closedAt<boolean>(1).next(false);

      expect(startRestore).not.toHaveBeenCalled();
    });

    // #255 P2a: `startRestoreFlow`'s own open-time duplicate check reports back through
    // `previewPending`, folded into this trigger's `disabled` — a second click on the same button
    // while the read is out must not start a second restore-flow read.
    it('disables the trigger while the open-time duplicate check is out, and re-enables once the confirmation opens', () => {
      const fetch = new Subject<ReturnType<typeof emoteSetPage>>();
      httpPost.mockReturnValueOnce(fetch);

      const dialog = render();
      dialog.click();
      closedAt<FileImportResult | undefined>(0).next(restoreResult());
      dialog.detect();

      expect(dialog.triggerDisabled()).toBe(true);
      expect(dialogOpen).toHaveBeenCalledTimes(1);

      fetch.next(emoteSetPage());
      fetch.complete();
      dialog.detect();

      expect(dialog.triggerDisabled()).toBe(false);
      expect(dialogOpen).toHaveBeenCalledTimes(2);
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

      closedAt<{ targetSetId: string; targetSetName: string; plan: TransferPlan }>(1).next({
        targetSetId: CURRENT_SET,
        targetSetName: CURRENT_SET,
        plan: addPlan([{ sevenTvEmoteId: '7tv-9', name: 'Kappa', imageUrl: null }]),
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
        addPlan([{ sevenTvEmoteId: '7tv-9', name: 'Kappa', imageUrl: null }]),
        0,
        true,
        0,
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

      closedAt<{ targetSetId: string; targetSetName: string; plan: TransferPlan }>(1).next({
        targetSetId: CURRENT_SET,
        targetSetName: CURRENT_SET,
        plan: addPlan([{ sevenTvEmoteId: '7tv-9', name: 'Kappa', imageUrl: null }]),
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

      closedAt<{ targetSetId: string; targetSetName: string; plan: TransferPlan }>(1).next({
        targetSetId: CURRENT_SET,
        targetSetName: CURRENT_SET,
        plan: addPlan([{ sevenTvEmoteId: '7tv-1', name: 'HandLuL', imageUrl: null }]),
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
        addPlan([{ sevenTvEmoteId: '7tv-1', name: 'HandLuL', imageUrl: null }]),
        0,
        true,
        0,
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

      closedAt<{ targetSetId: string; targetSetName: string; plan: TransferPlan }>(1).next({
        targetSetId: CURRENT_SET,
        targetSetName: CURRENT_SET,
        plan: addPlan([{ sevenTvEmoteId: '7tv-2', name: 'Dance', imageUrl: null }]),
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
        addPlan([{ sevenTvEmoteId: '7tv-2', name: 'Dance', imageUrl: null }]),
        0,
        true,
        0,
      );
    });
  });

  describe('a non-active set on screen (#200, T4.5): all four doors follow it, restore included since K5', () => {
    // K5/T5.3: restore's own slot preview follows the same active/non-active fork the other three
    // doors already had (spec 8.3) — since #253 decided by the resolved target's own `isActiveSet`,
    // not by the page's inputs.
    it('restores into the non-active set, reading its slot preview live instead of EmoteSetStatus', () => {
      loadEmoteSetPreview.mockReturnValue(
        of({
          channelName: CURRENT_CHANNEL,
          sevenTvUserId: null,
          emoteSetId: 'set-halloween',
          emoteSetName: 'Halloween',
          capacity: 1000,
          totalCount: 900,
          truncated: false,
          emotes: [],
        }),
      );
      const dialog = render(CURRENT_CHANNEL, 'set-halloween', {
        activeSetId: CURRENT_SET,
        setName: 'Halloween',
      });
      dialog.click();

      // The file names the Halloween set, and the target list says it is not the active one.
      closedAt<FileImportResult | undefined>(0).next(
        restoreResult({
          emoteSetId: 'set-halloween',
          setName: 'Halloween',
          isActiveSet: false,
          hostSelectedSetId: 'set-halloween',
        }),
      );

      expect(loadEmoteSetPreview).toHaveBeenCalledWith(CURRENT_CHANNEL, 'set-halloween');
      expect(getSetStatus).not.toHaveBeenCalled();

      closedAt<boolean>(1).next(true);

      expect(startRestore).toHaveBeenCalledWith(
        expect.objectContaining({ setId: 'set-halloween', hostChannelName: CURRENT_CHANNEL }),
        [{ emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', aliases: ['PogU'] }],
        0,
        true,
        0,
      );
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

      closedAt<{ targetSetId: string; targetSetName: string; plan: TransferPlan }>(1).next({
        targetSetId: 'set-halloween',
        targetSetName: 'Halloween',
        plan: addPlan([{ sevenTvEmoteId: '7tv-9', name: 'Kappa', imageUrl: null }]),
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
        addPlan([{ sevenTvEmoteId: '7tv-9', name: 'Kappa', imageUrl: null }]),
        0,
        true,
        0,
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

      closedAt<{ targetSetId: string; targetSetName: string; plan: TransferPlan }>(1).next({
        targetSetId: 'set-halloween',
        targetSetName: 'Halloween',
        plan: addPlan([{ sevenTvEmoteId: '7tv-1', name: 'HandLuL', imageUrl: null }]),
      });

      expect(startImport).toHaveBeenCalledWith(
        expect.objectContaining({ setId: 'set-halloween', isActiveSet: false }),
        { kind: 'seventv-channel', channelName: 'handofblood' },
        addPlan([{ sevenTvEmoteId: '7tv-1', name: 'HandLuL', imageUrl: null }]),
        0,
        true,
        0,
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

      closedAt<{ targetSetId: string; targetSetName: string; plan: TransferPlan }>(1).next({
        targetSetId: 'set-halloween',
        targetSetName: 'Halloween',
        plan: addPlan([{ sevenTvEmoteId: '7tv-2', name: 'Dance', imageUrl: null }]),
      });

      expect(startImport).toHaveBeenCalledWith(
        expect.objectContaining({ setId: 'set-halloween', isActiveSet: false }),
        { kind: 'seventv-leaderboard', sortBy: 'TOP_ALL_TIME' },
        addPlan([{ sevenTvEmoteId: '7tv-2', name: 'Dance', imageUrl: null }]),
        0,
        true,
        0,
      );
    });
  });

  describe('an unknown active set (#200, K4 fix round): nothing may assume the set on screen is the active one', () => {
    function preview() {
      return of({
        channelName: CURRENT_CHANNEL,
        sevenTvUserId: null,
        emoteSetId: 'set-halloween',
        emoteSetName: 'Halloween',
        capacity: 1000,
        totalCount: 0,
        truncated: false,
        emotes: [],
      });
    }

    it("takes the explicit 'trackedSet' path for the selected set instead of the active-set fast path", () => {
      loadEmoteSetPreview.mockReturnValue(preview());
      const dialog = render(CURRENT_CHANNEL, 'set-halloween', {
        activeSetId: null,
        setName: 'Halloween',
      });
      dialog.click();

      closedAt<FileImportResult | undefined>(0).next({ kind: 'import', source: importSource() });

      // Read live by its id — never EmoteSetStatus, which would describe whatever the active set is.
      expect(loadEmoteSetPreview).toHaveBeenCalledWith(CURRENT_CHANNEL, 'set-halloween');
      expect(getSetStatus).not.toHaveBeenCalled();

      closedAt<{ targetSetId: string; targetSetName: string; plan: TransferPlan }>(1).next({
        targetSetId: 'set-halloween',
        targetSetName: 'Halloween',
        plan: addPlan([{ sevenTvEmoteId: '7tv-9', name: 'Kappa', imageUrl: null }]),
      });

      // Not the active set: no post-run resync of the channel is implied either.
      expect(startImport).toHaveBeenCalledWith(
        expect.objectContaining({ setId: 'set-halloween', isActiveSet: false }),
        expect.objectContaining({ kind: 'file' }),
        addPlan([{ sevenTvEmoteId: '7tv-9', name: 'Kappa', imageUrl: null }]),
        0,
        true,
        0,
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
