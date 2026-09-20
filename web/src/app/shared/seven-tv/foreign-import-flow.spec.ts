import { Dialog } from '@angular/cdk/dialog';
import { HttpClient } from '@angular/common/http';
import { signal } from '@angular/core';
import { Subject, of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { EmoteListItem } from '../../core/emotes/emote-list-item.model';
import { ForeignEmoteRow } from '../../core/seven-tv/foreign-emote-set.model';
import { LeaderboardImportResult } from '../../core/seven-tv/leaderboard.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { ForeignChannelImportResult } from './foreign-channel-step';
import {
  buildForeignImportSource,
  buildLeaderboardImportSource,
  startForeignChannelImportFlow,
  startLeaderboardImportFlow,
} from './foreign-import-flow';
import { ImportFlowDeps } from './import-flow';
import { buildImportPreview } from './import-preview';

/**
 * Like `import-flow.spec.ts`, this runs without a `TestBed`: the flow opens dialogs through the
 * plain `Dialog` it is handed, so `dialog.open` is a `vi.fn()` standing in for every dialog of the
 * chain, distinguished by call order.
 */

function foreignRow(
  sevenTvEmoteId: string,
  name: string,
  defaultName = `${name}Global`,
): ForeignEmoteRow {
  return {
    sevenTvEmoteId,
    name,
    defaultName,
    imageUrl: `https://cdn.7tv.app/emote/${sevenTvEmoteId}/2x.webp`,
    topAllTime: null,
    trending: null,
  };
}

function picked(rows: ForeignEmoteRow[], channelName = 'handofblood'): ForeignChannelImportResult {
  return { channelName, sevenTvUserId: '7tv-user-1', emoteSetId: 'set-source', rows };
}

/** A leaderboard pick. Deliberately built from a type that has no channel, no 7TV user and no set
 *  id — the three fields `buildForeignImportSource` reads and this source simply does not have
 *  (spec F4). */
function pickedFromLeaderboard(
  rows: ForeignEmoteRow[],
  sortBy: LeaderboardImportResult['sortBy'] = 'TRENDING_DAILY',
): LeaderboardImportResult {
  return { sortBy, rows };
}

interface Harness {
  deps: ImportFlowDeps;
  dialogOpen: ReturnType<typeof vi.fn>;
  startImport: ReturnType<typeof vi.fn>;
}

function setup(): Harness {
  const emoteAdminService = {
    getSetStatus: vi.fn(() => new Subject()),
    listEmotes: vi.fn(() => of([])),
    getSetWarning: vi.fn(() =>
      of({
        available: true,
        isOwnSet: true,
        otherTrackedChannelsSharingSet: [],
        otherModeratedChannelsSharingSet: [],
      }),
    ),
  } as unknown as EmoteAdminService;

  // The fresh #149/T5 duplicate check (`already-present-filter.ts`) — since the P1 fix this is a
  // raw `HttpClient.post` straight to 7TV, not `emoteAdminService`/`listEmotes`. Defaults to an
  // empty target set, matching every expectation below (skip count 0, available true).
  const httpClient = {
    post: vi.fn(() =>
      of({
        data: { emoteSets: { emoteSet: { emotes: { totalCount: 0, pageCount: 1, items: [] } } } },
      }),
    ),
  } as unknown as HttpClient;

  const startImport = vi.fn();
  const dialogOpen = vi.fn(() => ({ closed: new Subject<unknown>() }));

  return {
    deps: {
      dialog: { open: dialogOpen } as unknown as Dialog,
      emoteAdminService,
      // Never called: this flow always builds a `'trackedActive'` selection (see
      // `import-flow.ts`'s `load()`), which never reaches `SevenTvEmoteSetService`.
      emoteSetService: { loadEmoteSetPreview: vi.fn() } as unknown as SevenTvEmoteSetService,
      httpClient,
      tokenService: { hasToken: signal(true) } as unknown as SevenTvTokenService,
      importService: { startImport } as unknown as SevenTvImportService,
      arbiter: { activeRun: signal<SevenTvRunKind | null>(null) } as unknown as SevenTvRunArbiter,
    },
    dialogOpen,
    startImport,
  };
}

function closedSubject<T>(dialogOpen: ReturnType<typeof vi.fn>, call: number): Subject<T> {
  return dialogOpen.mock.results[call].value.closed as Subject<T>;
}

describe('buildForeignImportSource', () => {
  it('takes over the source alias, not the global default name', () => {
    // The decision from the design doc, and the reason the collision hint matters for this source:
    // an alias the source channel invented is far likelier to be taken in the target set than a
    // global base name would be.
    const source = buildForeignImportSource(picked([foreignRow('e1', 'HandLuL', 'LuL')]));

    expect(source.rows).toEqual([{ sevenTvEmoteId: 'e1', name: 'HandLuL' }]);
  });

  it('marks the origin as the foreign 7TV channel it came from', () => {
    const source = buildForeignImportSource(picked([foreignRow('e1', 'Kappa')], 'HandOfBlood'));

    expect(source.origin).toEqual({ kind: 'seventv-channel', channelName: 'HandOfBlood' });
  });

  it('deduplicates by 7TV id and discards nothing', () => {
    // Nothing here can be invalid the way a parsed file's rows can: these rows came out of our own
    // endpoint already parsed. Dedup still runs, because the run engine never deduplicates.
    const source = buildForeignImportSource(
      picked([foreignRow('e1', 'Kappa'), foreignRow('e1', 'KappaAgain'), foreignRow('e2', 'PogU')]),
    );

    expect(source.rows).toEqual([
      { sevenTvEmoteId: 'e1', name: 'Kappa' },
      { sevenTvEmoteId: 'e2', name: 'PogU' },
    ]);
    expect(source.duplicatesCollapsed).toBe(1);
    expect(source.discardedRows).toBe(0);
  });
});

describe('startForeignChannelImportFlow', () => {
  it('opens the import confirmation directly — no target picker in between (#147)', () => {
    const { deps, dialogOpen } = setup();

    startForeignChannelImportFlow(deps, picked([foreignRow('e1', 'Kappa')]), 'my_channel');

    // Exactly one dialog, and it is the confirmation: where the emotes go was decided by the page
    // the flow was started from, exactly as it is for the file path.
    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  it('hands the picked rows and the foreign origin to the ordinary import run', () => {
    const { deps, dialogOpen, startImport } = setup();

    startForeignChannelImportFlow(deps, picked([foreignRow('e1', 'Kappa')]), 'my_channel');
    closedSubject<unknown>(dialogOpen, 0).next({
      targetSetId: 'set-target',
      rows: [{ sevenTvEmoteId: 'e1', name: 'Kappa' }],
    });

    // Fourth argument is the #149/T5 fresh duplicate-check skip count — 0 because the fresh 7TV
    // read (`httpClient.post`) defaults to an empty target set. Fifth is whether that check
    // actually ran — true, since the fetch succeeded (#149).
    expect(startImport).toHaveBeenCalledWith(
      { setId: 'set-target', channelName: 'my_channel', ownerDisplayName: null },
      { kind: 'seventv-channel', channelName: 'handofblood' },
      [{ sevenTvEmoteId: 'e1', name: 'Kappa' }],
      0,
      true,
    );
  });

  it('starts no run when the confirmation is dismissed', () => {
    const { deps, dialogOpen, startImport } = setup();

    startForeignChannelImportFlow(deps, picked([foreignRow('e1', 'Kappa')]), 'my_channel');
    closedSubject<unknown>(dialogOpen, 0).next(undefined);

    expect(startImport).not.toHaveBeenCalled();
  });
});

describe('buildLeaderboardImportSource', () => {
  it('takes over 7TV\u2019s global default name, the only name a leaderboard row has', () => {
    // A leaderboard row is an `Emote`, not an `EmoteSetEmote` (spec F7): there is no per-set alias
    // to prefer, so reading `defaultName` says which of the two this source actually carries.
    const source = buildLeaderboardImportSource(
      pickedFromLeaderboard([foreignRow('e1', 'catJAM', 'catJAMGlobal')]),
    );

    expect(source.rows).toEqual([{ sevenTvEmoteId: 'e1', name: 'catJAMGlobal' }]);
  });

  it('marks the origin as the list it was picked off, since there is no source channel', () => {
    const source = buildLeaderboardImportSource(
      pickedFromLeaderboard([foreignRow('e1', 'Kappa')], 'TOP_ALL_TIME'),
    );

    // E2/E8: the sort IS the provenance, and it is what the write-once audit row keeps.
    expect(source.origin).toEqual({ kind: 'seventv-leaderboard', sortBy: 'TOP_ALL_TIME' });
  });

  it('deduplicates by 7TV id and discards nothing', () => {
    const source = buildLeaderboardImportSource(
      pickedFromLeaderboard([
        foreignRow('e1', 'Kappa', 'Kappa'),
        foreignRow('e1', 'Kappa', 'Kappa'),
        foreignRow('e2', 'PogU', 'PogU'),
      ]),
    );

    expect(source.rows).toEqual([
      { sevenTvEmoteId: 'e1', name: 'Kappa' },
      { sevenTvEmoteId: 'e2', name: 'PogU' },
    ]);
    expect(source.duplicatesCollapsed).toBe(1);
    expect(source.discardedRows).toBe(0);
  });

  it('inherits the target set\u2019s name-collision hint without inheriting a block (AK 18)', () => {
    // Regression, not a new feature: the hint lives in `buildImportPreview` and has done since #72.
    // A leaderboard row must reach it the same way the other sources do — and, like them, be
    // reported rather than removed: 7TV decides, not this preview.
    const target: EmoteListItem[] = [{ sevenTvEmoteId: 'other-id', name: 'Kappa' }];
    const source = buildLeaderboardImportSource(
      pickedFromLeaderboard([foreignRow('e1', 'Kappa', 'Kappa'), foreignRow('e2', 'PogU', 'PogU')]),
    );

    const preview = buildImportPreview(source, target);

    expect(preview.nameCollisions).toEqual(['Kappa']);
    // Since spec 2026-09-20 a name collision is excluded from toAdd rather than merely reported
    // (revises the pre-#72 reading pinned here before this task).
    expect(preview.toAdd).toEqual([{ sevenTvEmoteId: 'e2', name: 'PogU' }]);
  });
});

describe('startLeaderboardImportFlow', () => {
  it('opens the import confirmation directly \u2014 there is even less to ask than for a channel', () => {
    const { deps, dialogOpen } = setup();

    startLeaderboardImportFlow(
      deps,
      pickedFromLeaderboard([foreignRow('e1', 'Kappa')]),
      'my_channel',
    );

    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  it('hands the picked rows and the leaderboard origin to the ordinary import run', () => {
    const { deps, dialogOpen, startImport } = setup();

    startLeaderboardImportFlow(
      deps,
      pickedFromLeaderboard([foreignRow('e1', 'Kappa', 'Kappa')], 'TOP_ALL_TIME'),
      'my_channel',
    );
    closedSubject<unknown>(dialogOpen, 0).next({
      targetSetId: 'set-target',
      rows: [{ sevenTvEmoteId: 'e1', name: 'Kappa' }],
    });

    expect(startImport).toHaveBeenCalledWith(
      { setId: 'set-target', channelName: 'my_channel', ownerDisplayName: null },
      { kind: 'seventv-leaderboard', sortBy: 'TOP_ALL_TIME' },
      [{ sevenTvEmoteId: 'e1', name: 'Kappa' }],
      0,
      true,
    );
  });
});
