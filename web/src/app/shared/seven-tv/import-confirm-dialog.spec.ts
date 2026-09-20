import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { EmoteSetWarning } from '../../core/emotes/emote-admin.service';
import { ImportTargetLoadState } from '../../core/emotes/import-target-loader';
import { LanguageService } from '../../core/i18n/language.service';
import { ImportRow, ImportSource } from '../../core/seven-tv/import-source';
import {
  ImportConfirmDialog,
  ImportConfirmDialogData,
  ImportConfirmOutcome,
} from './import-confirm-dialog';

// Only the keys this dialog translates — not the full app translation file. Texts are the real
// German ones, so an assertion reads as the sentence the user gets rather than as a key.
const DE_TRANSLATIONS = {
  common: {
    cancel: 'Abbrechen',
    loading: 'Lädt …',
  },
  // Same key namespace the audit view's `renderDetail` reads (`leaderboard.model.ts`,
  // `LEADERBOARD_SORT_LABEL_KEYS`) — the dialog translates a leaderboard origin's sort through it
  // too, so the two surfaces say exactly the same thing (E2).
  audit: {
    details: {
      leaderboardSort: {
        TRENDING_DAILY: '7TV Trend heute',
        TOP_ALL_TIME: '7TV Top insgesamt',
      },
    },
  },
  massDelete: {
    sharedSetWarningTitle:
      'Achtung: Das aktive Emote-Set gehört möglicherweise nicht (nur) diesem Channel.',
    notOwnSet: 'Das aktive Set gehört nicht dem eigenen 7TV-Account dieses Channels.',
    knownAffected: 'Bei uns bekannt betroffen: {{ list }}',
    moderatedAffected: 'Von dir moderiert, ebenfalls betroffen: {{ list }}',
    ownershipCheckUnavailable:
      'Wir konnten gerade nicht prüfen, ob dieses Set wirklich diesem Channel gehört.',
  },
  restore: {
    capacityProjection: 'Das Set hätte danach {{ projected }} von {{ capacity }} Slots belegt.',
    capacityWarning: 'Das überschreitet die Kapazität — 7TV wird überzählige Emotes ablehnen.',
  },
  import: {
    confirm: {
      title: {
        one: '{{ count }} Emote nach {{ channel }} kopieren?',
        other: '{{ count }} Emotes nach {{ channel }} kopieren?',
      },
      originChannel: 'Aus Kanal {{ channel }}',
      originFile: 'Aus Datei {{ fileName }}',
      originFileDetails: 'Export aus {{ channel }}, {{ date }}',
      originLeaderboard: 'Aus 7TVs Bestenliste: {{ sort }}',
      dateUnknown: 'Datum unbekannt',
      channelUnknown: 'Kanal unbekannt',
      target: 'Ziel: {{ channel }} · Set {{ setName }}',
      targetUntracked: 'Ziel: Set {{ setName }} von {{ owner }}',
      loadingHint: 'Zieldaten werden geladen…',
      noTargetSet: 'Der Zielkanal hat noch kein aktives 7TV-Set.',
      loadFailed: 'Die Daten des Zielkanals konnten nicht geladen werden.',
      retry: 'Erneut laden',
      staleHint: 'Der letzte Abgleich des Zielkanals ist fehlgeschlagen.',
      alreadyPresent: {
        one: '{{ count }} Emote ist bereits im Zielset und wird übersprungen.',
        other: '{{ count }} Emotes sind bereits im Zielset und werden übersprungen.',
      },
      nameCollisions: {
        one: '{{ count }} Name ist im Zielset schon vergeben, wird nicht übertragen:',
        other: '{{ count }} Namen sind im Zielset schon vergeben, werden nicht übertragen:',
      },
      aliasMismatches: {
        one: '{{ count }} ist vorhanden, heißt dort aber anders:',
        other: '{{ count }} sind vorhanden, heißen dort aber anders:',
      },
      aliasMismatchRow: '{{ source }} → {{ target }}',
      invalidNames: {
        one: '{{ count }} Name enthält Zeichen, die 7TV nicht anlegen kann:',
        other: '{{ count }} Namen enthalten Zeichen, die 7TV nicht anlegen kann:',
      },
      discardedRows: {
        one: '{{ count }} ungültige Zeile in der Quelle verworfen.',
        other: '{{ count }} ungültige Zeilen in der Quelle verworfen.',
      },
      duplicatesCollapsed: {
        one: '{{ count }} doppelte Zeile in der Quelle zusammengefasst.',
        other: '{{ count }} doppelte Zeilen in der Quelle zusammengefasst.',
      },
      nothingToAdd: {
        one: 'Das einzige Emote ist bereits im Zielset.',
        other: 'Alle {{ count }} Emotes sind bereits im Zielset.',
      },
      sameChannelFile: 'Diese Liste stammt aus diesem Kanal.',
      runNotice: 'Das Hinzufügen läuft danach automatisch nacheinander.',
      execute: 'Kopieren',
    },
  },
};

const CANCEL = 'Abbrechen';
const EXECUTE = 'Kopieren';
const RETRY = 'Erneut laden';

/** The check ran and found nothing worth flagging — the quiet case. */
const OWN_SET: EmoteSetWarning = {
  available: true,
  isOwnSet: true,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

/** What `loadImportTarget` falls back to when only `getSetWarning` failed: not verified, and
 *  deliberately neither a clean bill of health nor an alarm. */
const UNAVAILABLE_WARNING: EmoteSetWarning = {
  available: false,
  isOwnSet: false,
  otherTrackedChannelsSharingSet: [],
  otherModeratedChannelsSharingSet: [],
};

type ReadyTarget = Extract<ImportTargetLoadState, { status: 'ready' }>;

function row(sevenTvEmoteId: string, name: string): ImportRow {
  return { sevenTvEmoteId, name };
}

function channelSource(rows: ImportRow[], overrides: Partial<ImportSource> = {}): ImportSource {
  return {
    origin: { kind: 'channel', channelName: 'sourcechannel' },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
    ...overrides,
  };
}

/** The third source (spec §7): a channel EmotePurge does not track, read live from 7TV. Rows carry
 *  the *alias* of the source set, which is what makes a name collision in the target likely. */
function foreignChannelSource(rows: ImportRow[], channelName = 'handofblood'): ImportSource {
  return {
    origin: { kind: 'seventv-channel', channelName },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
  };
}

/** The fourth source (#148): 7TV's network-wide leaderboard. No channel at all — the sort itself is
 *  the origin (spec E2/E8). */
function leaderboardSource(
  rows: ImportRow[],
  sortBy: 'TRENDING_DAILY' | 'TOP_ALL_TIME' = 'TRENDING_DAILY',
): ImportSource {
  return {
    origin: { kind: 'seventv-leaderboard', sortBy },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
  };
}

function fileSource(
  rows: ImportRow[],
  origin: Partial<Extract<ImportSource['origin'], { kind: 'file' }>> = {},
  overrides: Partial<ImportSource> = {},
): ImportSource {
  return {
    origin: {
      kind: 'file',
      fileName: 'emotes.json',
      exportedAt: '2026-08-01T12:00:00Z',
      channelName: 'sourcechannel',
      envelopeKind: 'emote-list',
      ...origin,
    },
    rows,
    duplicatesCollapsed: 0,
    discardedRows: 0,
    ...overrides,
  };
}

function readyTarget(overrides: Partial<ReadyTarget> = {}): ImportTargetLoadState {
  return {
    status: 'ready',
    setId: 'set-1',
    // `null` by default, matching the "today" (active-set) path, which never has one — a test
    // about the header's setName sets this explicitly (spec 8.6, AK 39).
    setName: null,
    occupiedSlots: 10,
    capacity: 1000,
    syncFailureReason: null,
    emotes: [],
    warning: OWN_SET,
    ...overrides,
  };
}

/**
 * The order the markers actually appear in the rendered dialog. Sorting by position rather than
 * asserting on positions keeps the failure readable — the expectation is the contract's own list.
 */
function inRenderedOrder(text: string, markers: readonly string[]): string[] {
  for (const marker of markers) {
    if (!text.includes(marker)) {
      throw new Error(`marker not rendered: ${marker}`);
    }
  }
  return [...markers].sort((a, b) => text.indexOf(a) - text.indexOf(b));
}

interface RenderOptions {
  source?: ImportSource;
  targetChannelName?: string | null;
  targetOwnerDisplayName?: string | null;
  target?: ImportTargetLoadState;
  runBlocked?: boolean;
}

interface Harness {
  fixture: ComponentFixture<ImportConfirmDialog>;
  /** The flow's live view of the target — the dialog opens on `loading` and fills in. */
  target: WritableSignal<ImportTargetLoadState>;
  runBlocked: WritableSignal<boolean>;
  detect(): void;
  text(): string;
  title(): string;
  button(label: string): HTMLButtonElement;
  hasButton(label: string): boolean;
  element(id: string): HTMLElement | null;
}

describe('ImportConfirmDialog', () => {
  let dialogData: ImportConfirmDialogData;
  let closed: (ImportConfirmOutcome | undefined)[];
  let retryCalls: number;

  beforeEach(async () => {
    closed = [];
    retryCalls = 0;

    await TestBed.configureTestingModule({
      imports: [
        ImportConfirmDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        // Resolved when the component is created, so a test may shape the data first.
        { provide: DIALOG_DATA, useFactory: () => dialogData },
        {
          provide: DialogRef,
          useValue: {
            close: (result?: ImportConfirmOutcome) => closed.push(result),
          },
        },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  /**
   * One dialog per test: `DIALOG_DATA` is resolved once per injector, so a second `render()` in the
   * same test would silently hand the first test data to the second component. Everything that has
   * to change while the dialog is up changes through the two signals in the returned harness —
   * which is also how the flow itself feeds this dialog.
   */
  function render(options: RenderOptions = {}): Harness {
    const target = signal<ImportTargetLoadState>(options.target ?? readyTarget());
    const runBlocked = signal(options.runBlocked ?? false);

    dialogData = {
      source: options.source ?? channelSource([row('new-1', 'Kappa')]),
      targetChannelName:
        options.targetChannelName === undefined ? 'targetchannel' : options.targetChannelName,
      targetOwnerDisplayName: options.targetOwnerDisplayName ?? null,
      target,
      retry: () => {
        retryCalls += 1;
      },
      runBlocked,
    };

    const fixture = TestBed.createComponent(ImportConfirmDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function buttons(): HTMLButtonElement[] {
      return Array.from(host.querySelectorAll('button'));
    }

    return {
      fixture,
      target,
      runBlocked,
      detect: () => fixture.detectChanges(),
      text: () => host.textContent ?? '',
      title: () => host.querySelector('#app-dialog-title')?.textContent?.trim() ?? '',
      button: (label) => {
        const found = buttons().find((button) => button.textContent?.trim() === label);
        if (!found) {
          throw new Error(`no button labelled "${label}"`);
        }
        return found;
      },
      hasButton: (label) => buttons().some((button) => button.textContent?.trim() === label),
      element: (id) => host.querySelector<HTMLElement>(`#${id}`),
    };
  }

  describe('executor lock', () => {
    it('locks the executor while the target data is still loading, and says why next to it', () => {
      const dialog = render({ target: { status: 'loading' } });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      // The one lock reason without a banner: while loading, the target block is a skeleton, so the
      // reason lives in the action row instead — and the button has to point at it.
      expect(execute.getAttribute('aria-describedby')).toBe('import-confirm-loading-hint');
      expect(dialog.element('import-confirm-loading-hint')?.textContent).toContain(
        'Zieldaten werden geladen…',
      );
      // Cancelling is never blocked.
      expect(dialog.button(CANCEL).disabled).toBe(false);
    });

    it('locks it after a failed target load and offers the retry the flow owns', () => {
      const dialog = render({ target: { status: 'failed' } });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      expect(execute.getAttribute('aria-describedby')).toBe('import-confirm-load-failed');
      expect(dialog.element('import-confirm-load-failed')?.textContent).toContain(
        'Die Daten des Zielkanals konnten nicht geladen werden.',
      );

      dialog.button(RETRY).click();
      // The dialog only asks; the flow reloads and pushes a new state into the signal.
      expect(retryCalls).toBe(1);
    });

    it('locks it when the target channel has no active set — a different reason, not "failed"', () => {
      const dialog = render({ target: { status: 'no-set' } });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      expect(execute.getAttribute('aria-describedby')).toBe('import-confirm-no-target-set');
      expect(dialog.element('import-confirm-no-target-set')?.textContent).toContain(
        'Der Zielkanal hat noch kein aktives 7TV-Set.',
      );
      // No retry here: reloading would answer the same, the target has to gain a set first.
      expect(dialog.hasButton(RETRY)).toBe(false);
      expect(dialog.element('import-confirm-load-failed')).toBeNull();
    });

    it('locks it when every offered row is already in the target set', () => {
      const dialog = render({
        source: channelSource([row('existing-1', 'PogU'), row('existing-2', 'Kappa')]),
        target: readyTarget({
          emotes: [
            { sevenTvEmoteId: 'existing-1', name: 'PogU' },
            { sevenTvEmoteId: 'existing-2', name: 'Kappa' },
          ],
        }),
      });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      expect(execute.getAttribute('aria-describedby')).toBe('import-confirm-nothing-to-add');
      // The banner counts the offered rows, not the (empty) rest — "all 2 of them" is the statement.
      expect(dialog.element('import-confirm-nothing-to-add')?.textContent).toContain(
        'Alle 2 Emotes sind bereits im Zielset.',
      );
    });

    it('releases it once the target is ready and something is left to add', () => {
      const dialog = render({ target: { status: 'loading' } });
      expect(dialog.button(EXECUTE).disabled).toBe(true);

      dialog.target.set(readyTarget());
      dialog.detect();

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(false);
      // Nothing to explain any more, so the button describes itself.
      expect(execute.getAttribute('aria-describedby')).toBeNull();
    });

    it('locks it silently while another 7TV run is going — the running progress is the reason', () => {
      const dialog = render({ runBlocked: true });

      const execute = dialog.button(EXECUTE);
      expect(execute.disabled).toBe(true);
      // The second lock source is independent of `blockReason` and deliberately has no text of its
      // own: the dock's progress next to this dialog already says what is happening (§4.2).
      expect(execute.getAttribute('aria-describedby')).toBeNull();

      dialog.runBlocked.set(false);
      dialog.detect();
      expect(dialog.button(EXECUTE).disabled).toBe(false);
    });

    it('keeps the run from starting while it is locked, not just greyed out', () => {
      const dialog = render({ runBlocked: true });

      dialog.button(EXECUTE).click();

      expect(closed).toEqual([]);
    });
  });

  describe('outcome', () => {
    it('closes with the target set and only the rows that would actually be added', () => {
      const dialog = render({
        source: channelSource([
          row('existing-1', 'PogU'),
          row('new-1', 'Kappa'),
          // A name collision is excluded outright since spec 2026-09-20 — 7TV would reject it
          // every time, so it never even reaches the run (AK 40: the run's own `failed` count for
          // this row is 0, not "1 rejected").
          row('new-2', 'Collides'),
        ]),
        target: readyTarget({
          setId: 'set-42',
          emotes: [
            { sevenTvEmoteId: 'existing-1', name: 'PogU' },
            { sevenTvEmoteId: 'existing-9', name: 'Collides' },
          ],
        }),
      });

      dialog.button(EXECUTE).click();

      expect(closed).toEqual([
        {
          targetSetId: 'set-42',
          rows: [row('new-1', 'Kappa')],
        },
      ]);
    });

    it('closes empty-handed on cancel', () => {
      const dialog = render();

      dialog.button(CANCEL).click();

      expect(closed).toEqual([undefined]);
    });
  });

  describe('title', () => {
    it('counts the offered rows until the target answers, then only what is left to add', () => {
      const dialog = render({
        source: channelSource([
          row('existing-1', 'PogU'),
          row('existing-2', 'Kappa'),
          row('new-1', 'Pepega'),
        ]),
        targetChannelName: 'targetchannel',
        target: { status: 'loading' },
      });

      // The honest upper bound while nothing is known about the target — and the number the user
      // just picked, rather than a headless dialog.
      expect(dialog.title()).toBe('3 Emotes nach targetchannel kopieren?');

      dialog.target.set(
        readyTarget({
          emotes: [
            { sevenTvEmoteId: 'existing-1', name: 'PogU' },
            { sevenTvEmoteId: 'existing-2', name: 'Kappa' },
          ],
        }),
      );
      dialog.detect();

      // Settles to the rest list, and picks the singular sibling key for it.
      expect(dialog.title()).toBe('1 Emote nach targetchannel kopieren?');
    });
  });

  describe('set-ownership findings', () => {
    it('stays silent when the check ran and found an unshared own set', () => {
      const dialog = render({ target: readyTarget({ warning: OWN_SET }) });

      expect(dialog.text()).not.toContain('Achtung: Das aktive Emote-Set');
      expect(dialog.text()).not.toContain('Wir konnten gerade nicht prüfen');
    });

    it('raises the shared-set warning when the set is not the channel own', () => {
      const dialog = render({
        target: readyTarget({ warning: { ...OWN_SET, isOwnSet: false } }),
      });

      expect(dialog.text()).toContain('Achtung: Das aktive Emote-Set');
      expect(dialog.text()).toContain('Das aktive Set gehört nicht dem eigenen 7TV-Account');
      expect(dialog.text()).not.toContain('Wir konnten gerade nicht prüfen');
    });

    it('raises it for other channels sharing the set even when the set is the channel own', () => {
      const dialog = render({
        target: readyTarget({
          warning: {
            ...OWN_SET,
            otherTrackedChannelsSharingSet: ['tracked1', 'tracked2'],
            otherModeratedChannelsSharingSet: ['modded1'],
          },
        }),
      });

      expect(dialog.text()).toContain('Bei uns bekannt betroffen: tracked1, tracked2');
      expect(dialog.text()).toContain('Von dir moderiert, ebenfalls betroffen: modded1');
      // The set is the channel's own, so that one line stays out of the banner.
      expect(dialog.text()).not.toContain('Das aktive Set gehört nicht dem eigenen 7TV-Account');
    });

    it('downgrades to "could not check" when the check itself failed — not a confirmed finding', () => {
      const dialog = render({ target: readyTarget({ warning: UNAVAILABLE_WARNING }) });

      // `isOwnSet: false` is part of the fallback shape and must not be read as evidence: an
      // unavailable check is amber ("unknown"), never the red "this set is foreign".
      expect(dialog.text()).toContain('Wir konnten gerade nicht prüfen');
      expect(dialog.text()).not.toContain('Achtung: Das aktive Emote-Set');
      expect(dialog.text()).not.toContain('Das aktive Set gehört nicht dem eigenen 7TV-Account');
      // And it blocks nothing.
      expect(dialog.button(EXECUTE).disabled).toBe(false);
    });
  });

  describe('slot projection', () => {
    it('warns when the copy would push the target set past its capacity', () => {
      const dialog = render({
        source: channelSource([row('new-1', 'Kappa'), row('new-2', 'Pepega')]),
        target: readyTarget({ occupiedSlots: 999, capacity: 1000 }),
      });

      expect(dialog.text()).toContain('Das Set hätte danach 1001 von 1000 Slots belegt.');
      expect(dialog.text()).toContain('Das überschreitet die Kapazität');
      // Informational, not a lock: 7TV decides which of the overflowing adds it rejects.
      expect(dialog.button(EXECUTE).disabled).toBe(false);
    });

    it('states the projection quietly when it still fits', () => {
      const dialog = render({
        source: channelSource([row('new-1', 'Kappa')]),
        target: readyTarget({ occupiedSlots: 999, capacity: 1000 }),
      });

      expect(dialog.text()).toContain('Das Set hätte danach 1000 von 1000 Slots belegt.');
      expect(dialog.text()).not.toContain('Das überschreitet die Kapazität');
    });

    it('says nothing about slots when 7TV reports no usable capacity', () => {
      const dialog = render({ target: readyTarget({ capacity: null }) });

      expect(dialog.text()).not.toContain('Slots belegt');
    });
  });

  describe('origin', () => {
    it('names the file with its export channel and date, formatted in the active language', () => {
      const lang = TestBed.inject(LanguageService).lang;
      lang.set('de');

      const dialog = render({
        source: fileSource([row('new-1', 'Kappa')], {
          fileName: 'emotes-2026.json',
          channelName: 'sourcechannel',
          exportedAt: '2026-08-01T12:00:00Z',
        }),
      });

      expect(dialog.text()).toContain('Aus Datei emotes-2026.json');
      expect(dialog.text()).toContain('Export aus sourcechannel, 1.8.2026');

      // Only the date follows the language signal here — the labels come from the (German-only)
      // test dictionary, and re-formatting on a language switch is the point of reading `lang()`.
      lang.set('en');
      dialog.detect();
      expect(dialog.text()).toContain('Export aus sourcechannel, 8/1/2026');
    });

    it('falls back to "unknown" for a file without a channel or with an unreadable date', () => {
      const dialog = render({
        source: fileSource([row('new-1', 'Kappa')], {
          channelName: null,
          exportedAt: 'not-a-date',
        }),
      });

      expect(dialog.text()).toContain('Export aus Kanal unbekannt, Datum unbekannt');
    });

    it('flags a file that was exported from the target channel itself, comparing normalized names', () => {
      const dialog = render({
        source: fileSource([row('new-1', 'Kappa')], { channelName: 'HandOfBlood' }),
        targetChannelName: 'handofblood',
      });

      expect(dialog.text()).toContain('Diese Liste stammt aus diesem Kanal.');
    });

    it('does not flag a channel origin, even when it is the target channel', () => {
      const dialog = render({
        source: channelSource([row('new-1', 'Kappa')], {
          origin: { kind: 'channel', channelName: 'targetchannel' },
        }),
        targetChannelName: 'targetchannel',
      });

      // A channel origin can only be a *different* channel's grid in this flow, and the line is
      // about a downloaded file having travelled in a circle.
      expect(dialog.text()).toContain('Aus Kanal targetchannel');
      expect(dialog.text()).not.toContain('Diese Liste stammt aus diesem Kanal.');
    });

    it('names a foreign channel as a channel origin, not as a file', () => {
      // The template used to ask `origin.kind === 'channel'` and fell into the *file* branch for
      // everything else — this origin would have been announced as a file and then read a fileName
      // it does not have (spec F6).
      const dialog = render({
        source: foreignChannelSource([row('new-1', 'Kappa')]),
      });

      expect(dialog.text()).toContain('Aus Kanal handofblood');
      expect(dialog.text()).not.toContain('Aus Datei');
      expect(dialog.text()).not.toContain('Export aus');
    });

    it('does not flag a foreign channel origin as a list that came from this channel', () => {
      // The "came back to where it started" line is about a downloaded file; the target picker
      // excludes the source channel, so this pairing cannot even be produced by the flow.
      const dialog = render({
        source: foreignChannelSource([row('new-1', 'Kappa')], 'targetchannel'),
        targetChannelName: 'targetchannel',
      });

      expect(dialog.text()).not.toContain('Diese Liste stammt aus diesem Kanal.');
    });

    it('does not flag a file from another channel', () => {
      const dialog = render({
        source: fileSource([row('new-1', 'Kappa')], { channelName: 'someoneelse' }),
        targetChannelName: 'targetchannel',
      });

      expect(dialog.text()).not.toContain('Diese Liste stammt aus diesem Kanal.');
    });

    it('names the sort as the origin for a leaderboard pick — no channel, no file (spec E2)', () => {
      // AK 17: nothing about the file/channel origin lines changes for this — they are covered by
      // the tests above and left untouched.
      const dialog = render({
        source: leaderboardSource([row('new-1', 'Kappa')], 'TRENDING_DAILY'),
      });

      expect(dialog.text()).toContain('Aus 7TVs Bestenliste: 7TV Trend heute');
      expect(dialog.text()).not.toContain('Aus Kanal');
      expect(dialog.text()).not.toContain('Aus Datei');
    });

    it('names the other sort for the other leaderboard pick', () => {
      const dialog = render({
        source: leaderboardSource([row('new-1', 'Kappa')], 'TOP_ALL_TIME'),
      });

      expect(dialog.text()).toContain('Aus 7TVs Bestenliste: 7TV Top insgesamt');
    });

    it('does not flag a leaderboard pick as a list that came from this channel', () => {
      // A leaderboard row has no source channel at all — `sameChannelFile` stays `fileOrigin`-only.
      const dialog = render({
        source: leaderboardSource([row('new-1', 'Kappa')]),
        targetChannelName: 'targetchannel',
      });

      expect(dialog.text()).not.toContain('Diese Liste stammt aus diesem Kanal.');
    });
  });

  describe('stale target', () => {
    it('stays quiet while the target last synced cleanly', () => {
      const dialog = render({ target: readyTarget({ syncFailureReason: null }) });

      expect(dialog.text()).not.toContain('Der letzte Abgleich des Zielkanals ist fehlgeschlagen.');
    });

    it('points out that the target picture may be stale after a failed sync', () => {
      const dialog = render({ target: readyTarget({ syncFailureReason: 'seventv_unavailable' }) });

      expect(dialog.text()).toContain('Der letzte Abgleich des Zielkanals ist fehlgeschlagen.');
      // A stale picture is a caveat, not a lock — 7TV decides at run time.
      expect(dialog.button(EXECUTE).disabled).toBe(false);
    });
  });

  describe('name collisions for the foreign source (spec E7/AK 17)', () => {
    it('warns about a colliding alias and keeps it out of the run', () => {
      // Nothing new was built for the detection itself — `buildImportPreview` has produced
      // `nameCollisions` since #72. What is pinned here is that it keeps working for the third
      // source, whose rows carry the *alias* of the foreign set and therefore collide more readily
      // than a base name would — and that, since spec 2026-09-20, the row is excluded from the run
      // rather than merely flagged (AK 37/40).
      const dialog = render({
        source: foreignChannelSource([row('new-1', 'Kappa'), row('new-2', 'Collides')]),
        target: readyTarget({
          setId: 'set-42',
          emotes: [{ sevenTvEmoteId: 'existing-9', name: 'Collides' }],
        }),
      });

      expect(dialog.text()).toContain(
        '1 Name ist im Zielset schon vergeben, wird nicht übertragen:',
      );
      expect(dialog.text()).toContain('Collides');
      expect(dialog.button(EXECUTE).disabled).toBe(false);

      dialog.button(EXECUTE).click();

      expect(closed).toEqual([
        {
          targetSetId: 'set-42',
          rows: [row('new-1', 'Kappa')],
        },
      ]);
    });
  });

  describe('row order (§7.2 contract)', () => {
    it('renders every finding in the documented order', () => {
      const dialog = render({
        source: fileSource(
          [
            row('existing-1', 'AlreadyThere'),
            row('new-1', 'Collides'),
            row('new-2', 'Weird/Emote'),
          ],
          { fileName: 'emotes.json', channelName: 'HandOfBlood' },
          { discardedRows: 2, duplicatesCollapsed: 3 },
        ),
        targetChannelName: 'handofblood',
        target: readyTarget({
          setId: 'set-7',
          occupiedSlots: 999,
          capacity: 1000,
          syncFailureReason: 'seventv_unavailable',
          emotes: [
            { sevenTvEmoteId: 'existing-1', name: 'AlreadyThere' },
            { sevenTvEmoteId: 'existing-2', name: 'Collides' },
          ],
          warning: {
            available: true,
            isOwnSet: false,
            otherTrackedChannelsSharingSet: ['tracked1'],
            otherModeratedChannelsSharingSet: ['modded1'],
          },
        }),
      });

      // The contract from docs/UI-Designsprache.md §7.2 — the sequence of statements, not the
      // markup carrying them. Note the two source findings: discarded rows (data actually lost)
      // stand before collapsed duplicates (merely folded), because the heavier finding reads first.
      // The title counts only `new-2` now: `new-1`/'Collides' is a name collision and, since spec
      // 2026-09-20, no longer part of `toAdd` at all (AK 37).
      const contract = [
        '1 Emote nach handofblood kopieren?',
        'Aus Datei emotes.json',
        'Export aus HandOfBlood,',
        'Ziel: handofblood · Set set-7',
        'Achtung: Das aktive Emote-Set',
        'Das Set hätte danach 1000 von 1000 Slots belegt.',
        'Der letzte Abgleich des Zielkanals ist fehlgeschlagen.',
        '1 Emote ist bereits im Zielset',
        '1 Name ist im Zielset schon vergeben, wird nicht übertragen:',
        '1 Name enthält Zeichen, die 7TV nicht anlegen kann:',
        '2 ungültige Zeilen in der Quelle verworfen.',
        '3 doppelte Zeilen in der Quelle zusammengefasst.',
        'Diese Liste stammt aus diesem Kanal.',
        'Das Hinzufügen läuft danach automatisch nacheinander.',
      ];

      expect(inRenderedOrder(dialog.text(), contract)).toEqual(contract);
      // The name lists belong to the two rejection lines above them, in the same order.
      expect(inRenderedOrder(dialog.text(), ['Collides', 'Weird/Emote'])).toEqual([
        'Collides',
        'Weird/Emote',
      ]);
      // No loading hint once the target has answered.
      expect(dialog.text()).not.toContain('Zieldaten werden geladen…');
    });

    it('puts the nothing-to-add banner after the source findings and before the closing lines', () => {
      const dialog = render({
        source: fileSource(
          [row('existing-1', 'PogU')],
          { channelName: 'targetchannel' },
          { duplicatesCollapsed: 1 },
        ),
        targetChannelName: 'targetchannel',
        target: readyTarget({ emotes: [{ sevenTvEmoteId: 'existing-1', name: 'PogU' }] }),
      });

      const contract = [
        '1 Emote ist bereits im Zielset',
        '1 doppelte Zeile in der Quelle zusammengefasst.',
        'Das einzige Emote ist bereits im Zielset.',
        'Diese Liste stammt aus diesem Kanal.',
        'Das Hinzufügen läuft danach automatisch nacheinander.',
      ];

      expect(inRenderedOrder(dialog.text(), contract)).toEqual(contract);
    });
  });

  describe('target label and grouping (spec 8.6, AK 38/39/40)', () => {
    it('names the channel and the set in the header when both are known', () => {
      const dialog = render({
        targetChannelName: 'handofblood',
        target: readyTarget({ setName: 'Halloween' }),
      });

      expect(dialog.text()).toContain('Ziel: handofblood · Set Halloween');
    });

    it('falls back to the raw set id when the loader has no set name (the "today" path)', () => {
      const dialog = render({
        targetChannelName: 'handofblood',
        target: readyTarget({ setId: 'set-9', setName: null }),
      });

      expect(dialog.text()).toContain('Ziel: handofblood · Set set-9');
    });

    it('names the owner and the set for an untracked target, not a channel', () => {
      const dialog = render({
        targetChannelName: null,
        targetOwnerDisplayName: 'SomeEditor',
        target: readyTarget({ setName: 'Wegwerf-Set' }),
      });

      expect(dialog.text()).toContain('Ziel: Set Wegwerf-Set von SomeEditor');
      expect(dialog.text()).not.toContain('Ziel: handofblood');
    });

    it('reproduces the Halloween-set import proportions and the projection without an overflow banner (AK 38)', () => {
      const ALREADY_PRESENT_COUNT = 328;
      const ALIAS_MISMATCH_COUNT = 10;
      const NAME_COLLISION_COUNT = 192;
      const TO_ADD_COUNT = 232;

      const targetEmotes: { sevenTvEmoteId: string; name: string }[] = [];
      const sourceRows: ReturnType<typeof row>[] = [];

      for (let index = 0; index < ALREADY_PRESENT_COUNT; index++) {
        const id = `present-${index}`;
        targetEmotes.push({ sevenTvEmoteId: id, name: `Present${index}` });
        sourceRows.push(row(id, `Present${index}`));
      }
      for (let index = 0; index < ALIAS_MISMATCH_COUNT; index++) {
        const id = `mismatch-${index}`;
        targetEmotes.push({ sevenTvEmoteId: id, name: `TargetAlias${index}` });
        sourceRows.push(row(id, `SourceAlias${index}`));
      }
      for (let index = 0; index < NAME_COLLISION_COUNT; index++) {
        targetEmotes.push({ sevenTvEmoteId: `collision-target-${index}`, name: `Collide${index}` });
        sourceRows.push(row(`collision-source-${index}`, `Collide${index}`));
      }
      for (let index = 0; index < TO_ADD_COUNT; index++) {
        sourceRows.push(row(`new-${index}`, `New${index}`));
      }

      expect(sourceRows.length).toBe(762);

      const dialog = render({
        source: channelSource(sourceRows),
        target: readyTarget({
          setId: 'set-halloween',
          setName: 'Halloween',
          occupiedSlots: 687,
          capacity: 1000,
          emotes: targetEmotes,
        }),
      });

      expect(dialog.title()).toBe(`${TO_ADD_COUNT} Emotes nach targetchannel kopieren?`);
      expect(dialog.text()).toContain(
        `${ALREADY_PRESENT_COUNT} Emotes sind bereits im Zielset und werden übersprungen.`,
      );
      expect(dialog.text()).toContain(
        `${ALIAS_MISMATCH_COUNT} sind vorhanden, heißen dort aber anders:`,
      );
      expect(dialog.text()).toContain(
        `${NAME_COLLISION_COUNT} Namen sind im Zielset schon vergeben, werden nicht übertragen:`,
      );
      // 687 occupied + 232 toAdd = 919, comfortably under the capacity of 1000 — no overflow line.
      expect(dialog.text()).toContain('Das Set hätte danach 919 von 1000 Slots belegt.');
      expect(dialog.text()).not.toContain('Das überschreitet die Kapazität');

      // AK 40: none of the excluded rows (collisions or alias mismatches) reach the run at all —
      // the eventual `failed` count downstream is 0 for them, because they were never attempted.
      dialog.button(EXECUTE).click();
      expect(closed[0]?.rows.length).toBe(TO_ADD_COUNT);
      expect(closed[0]?.rows.some((r) => r.name.startsWith('Collide'))).toBe(false);
      expect(closed[0]?.rows.some((r) => r.name.startsWith('SourceAlias'))).toBe(false);
    });
  });
});
