import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { SevenTvSetEntries } from '../../core/seven-tv/seven-tv-set-entries';
import { UndoCandidate, UndoSourceFileInfo } from '../export/transfer-run-export';
import { TransferUndoPlanRecord } from '../export/transfer-undo-export';
import { ResolvedRestoreTarget } from './restore-flow';
import {
  UndoConfirmDialog,
  UndoConfirmDialogData,
  UndoConfirmOutcome,
} from './undo-confirm-dialog';
import de from '../../../../public/i18n/de.json';
import en from '../../../../public/i18n/en.json';

/*
 * The dialog's wiring, not the gate's machine: `RecoveryFileGate` has its own cases for
 * `idle → verifying → saved`, newest-read and teardown. What is tested here is what this dialog
 * decides — which read it classifies, what the origin lock takes out, which label and lock reason
 * the action row shows, what the file and the outcome carry, and which requests it makes.
 * Real German texts (the app's own de.json), so an assertion reads as the sentence the user gets.
 */

const GQL = 'https://7tv.io/v4/gql';
const SAVE = 'Rückweg sichern';
const START = 'Starten';
const CANCEL = 'Abbrechen';
const RELOAD = 'Ziel neu laden';

interface LiveEntry {
  id: string;
  alias: string | null;
  /** Omitted ⇒ no flag in the read at all. */
  animated?: boolean;
}

function setRead(entries: LiveEntry[], options: { complete?: boolean } = {}): SevenTvSetEntries {
  const aliasesById = new Map<string, string[]>();
  const aliaslessIds = new Set<string>();
  const defaultNameById = new Map<string, string>();
  const animatedById = new Map<string, boolean>();
  for (const entry of entries) {
    const aliases = aliasesById.get(entry.id) ?? [];
    if (entry.alias === null) {
      aliaslessIds.add(entry.id);
    } else {
      aliases.push(entry.alias);
    }
    aliasesById.set(entry.id, aliases);
    defaultNameById.set(entry.id, entry.alias ?? '');
    if (entry.animated !== undefined) {
      animatedById.set(entry.id, entry.animated);
    }
  }
  return {
    aliasesById,
    aliaslessIds,
    defaultNameById,
    animatedById,
    occupiedSlots: entries.length,
    complete: options.complete ?? true,
  };
}

/** 7TV's GraphQL answer for one page holding `entries`. */
function gqlAnswer(entries: LiveEntry[], totalCount = entries.length): object {
  return {
    data: {
      emoteSets: {
        emoteSet: {
          emotes: {
            totalCount,
            pageCount: 1,
            items: entries.map((entry) => ({
              alias: entry.alias,
              emote: {
                id: entry.id,
                defaultName: entry.alias ?? '',
                flags: { animated: entry.animated ?? false },
              },
            })),
          },
        },
      },
    },
  };
}

function candidate(
  index: number,
  overrides: Partial<Omit<UndoCandidate, 'target'>> & {
    target?: Partial<UndoCandidate['target']>;
  } = {},
): UndoCandidate {
  const alias = overrides.alias ?? `Alias${index}`;
  return {
    sourceSevenTvEmoteId: overrides.sourceSevenTvEmoteId ?? `src-${index}`,
    sourceName: overrides.sourceName ?? `Source${index}`,
    alias,
    fileStatus: overrides.fileStatus ?? 'done',
    provenance: overrides.provenance ?? 'confirmed',
    target: {
      sevenTvEmoteId: `tgt-${index}`,
      entries: [{ alias }],
      defaultName: `Target${index}`,
      ...overrides.target,
    },
  };
}

/** Live: the source holds exactly its alias, the target is gone — a `full` row. */
function fullLive(index: number, animated?: boolean): LiveEntry {
  return { id: `src-${index}`, alias: `Alias${index}`, animated };
}

const FINISHED_FILE: UndoSourceFileInfo = {
  stage: 'finished',
  exportedAt: '2026-09-20T10:00:00.000Z',
  verifiedAt: null,
  finishedAt: '2026-09-20T10:05:00.000Z',
  origin: { kind: 'channel', channelName: 'sourcechannel' },
};

const PLANNED_FILE: UndoSourceFileInfo = {
  stage: 'planned',
  exportedAt: '2026-09-20T10:00:00.000Z',
  verifiedAt: '2026-09-20T10:00:00.000Z',
  finishedAt: null,
  origin: null,
};

function resolvedTarget(overrides: Partial<ResolvedRestoreTarget> = {}): ResolvedRestoreTarget {
  return {
    emoteSetId: 'set-1',
    setName: 'Hauptset',
    ownerDisplayName: 'OwnerName',
    twitchLogin: 'ownerlogin',
    trackedChannelName: 'targetchannel',
    isActiveSet: true,
    hostChannelName: 'targetchannel',
    hostSelectedSetId: 'set-1',
    ...overrides,
  };
}

interface RenderOptions {
  candidates?: UndoCandidate[];
  target?: ResolvedRestoreTarget;
  sourceFile?: UndoSourceFileInfo;
  initialRead?: SevenTvSetEntries | null;
  capacity?: number | null;
}

interface Harness {
  fixture: ComponentFixture<UndoConfirmDialog>;
  detect(): void;
  text(): string;
  button(label: string): HTMLButtonElement;
  hasButton(label: string): boolean;
  element(id: string): HTMLElement | null;
  rows(): HTMLLIElement[];
  checkbox(): HTMLInputElement | null;
}

interface CapturedDownload {
  filename: string;
  blob: Blob;
}

describe('UndoConfirmDialog', () => {
  let dialogData: UndoConfirmDialogData;
  let closed: (UndoConfirmOutcome | null)[];
  let panelClasses: Set<string>;
  let getSetStatus: ReturnType<typeof vi.fn>;
  let loadEmoteSetPreview: ReturnType<typeof vi.fn>;
  let capacity: number | null;

  beforeEach(async () => {
    closed = [];
    panelClasses = new Set();
    capacity = 100;
    getSetStatus = vi.fn(() => of({ occupiedSlots: 90, capacity }));
    loadEmoteSetPreview = vi.fn(() => of({ totalCount: 90, capacity }));

    await TestBed.configureTestingModule({
      imports: [
        UndoConfirmDialog,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIALOG_DATA, useFactory: () => dialogData },
        {
          provide: DialogRef,
          useValue: {
            close: (result: UndoConfirmOutcome | null) => closed.push(result),
            overlayRef: { addPanelClass: (name: string) => panelClasses.add(name) },
          },
        },
        { provide: EmoteAdminService, useValue: { getSetStatus } },
        { provide: SevenTvEmoteSetService, useValue: { loadEmoteSetPreview } },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  afterEach(() => {
    // No test may leave a read unanswered — or make one it did not ask for.
    TestBed.inject(HttpTestingController).verify();
    vi.restoreAllMocks();
  });

  /** One dialog per test — `DIALOG_DATA` is resolved once per injector. */
  function render(options: RenderOptions = {}): Harness {
    const candidates = options.candidates ?? [candidate(1)];
    if (options.capacity !== undefined) {
      capacity = options.capacity;
    }
    dialogData = {
      candidates,
      target: options.target ?? resolvedTarget(),
      sourceFile: options.sourceFile ?? FINISHED_FILE,
      initialRead: options.initialRead === undefined ? setRead([fullLive(1)]) : options.initialRead,
    };

    const fixture = TestBed.createComponent(UndoConfirmDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    const buttons = () => Array.from(host.querySelectorAll('button'));

    return {
      fixture,
      detect: () => fixture.detectChanges(),
      text: () => (host.textContent ?? '').replace(/\s+/g, ' '),
      button: (label) => {
        const found = buttons().find((button) => button.textContent?.trim() === label);
        if (!found) {
          throw new Error(`no button labelled "${label}"`);
        }
        return found;
      },
      hasButton: (label) => buttons().some((button) => button.textContent?.trim() === label),
      element: (id) => host.querySelector<HTMLElement>(`#${id}`),
      rows: () => Array.from(host.querySelectorAll<HTMLLIElement>('ul li')),
      checkbox: () => host.querySelector<HTMLInputElement>('input[type="checkbox"]'),
    };
  }

  function executor(dialog: Harness): HTMLButtonElement {
    const actions = Array.from(dialog.fixture.nativeElement.querySelectorAll('button')).filter(
      (button) => [SAVE, START].includes((button as HTMLButtonElement).textContent?.trim() ?? ''),
    ) as HTMLButtonElement[];
    expect(actions).toHaveLength(1);
    return actions[0];
  }

  function click(dialog: Harness, button: HTMLElement): void {
    button.click();
    dialog.detect();
  }

  function toggleAcknowledgement(dialog: Harness): void {
    const box = dialog.checkbox();
    if (!box) {
      throw new Error('no confirmation checkbox');
    }
    box.click();
    dialog.detect();
  }

  /** Spies on the two seams `downloadFile` touches — the unit-test builder refuses `vi.mock` for
   *  relative imports (same approach as `import-confirm-dialog.spec.ts`). */
  function captureDownloads(options: { refuse?: boolean } = {}): CapturedDownload[] {
    const downloads: CapturedDownload[] = [];
    if (!('createObjectURL' in URL)) {
      Object.assign(URL, { createObjectURL: () => '', revokeObjectURL: () => undefined });
    }
    vi.spyOn(URL, 'createObjectURL').mockImplementation((blob: Blob | MediaSource) => {
      if (options.refuse) {
        throw new Error('download refused');
      }
      downloads.push({ filename: '', blob: blob as Blob });
      return 'blob:test';
    });
    vi.spyOn(URL, 'revokeObjectURL').mockImplementation(() => undefined);
    const originalCreateElement = document.createElement.bind(document);
    vi.spyOn(document, 'createElement').mockImplementation((tag: string) => {
      const element = originalCreateElement(tag);
      if (tag === 'a') {
        vi.spyOn(element as HTMLAnchorElement, 'click').mockImplementation(() => {
          const pending = downloads[downloads.length - 1];
          if (pending) {
            pending.filename = (element as HTMLAnchorElement).download;
          }
        });
      }
      return element;
    });
    return downloads;
  }

  async function savedRecord(download: CapturedDownload): Promise<TransferUndoPlanRecord> {
    return JSON.parse(await download.blob.text()) as TransferUndoPlanRecord;
  }

  function http(): HttpTestingController {
    return TestBed.inject(HttpTestingController);
  }

  describe('the read it classifies (K3, AK 7)', () => {
    it('classifies the flow’s read at once, without a request of its own, and widens the pane', () => {
      const dialog = render();

      http().expectNone(GQL);
      expect(dialog.text()).toContain('Quelle raus, Ziel zurück');
      expect(executor(dialog).textContent?.trim()).toBe(SAVE);
      expect(executor(dialog).disabled).toBe(false);
      expect(panelClasses.has('app-dialog-panel-wide')).toBe(true);
    });

    it('opens on the error state without a read — nothing released, the banner names why', () => {
      const dialog = render({ initialRead: null });

      http().expectNone(GQL);
      expect(
        dialog.element('undo-confirm-read-failed')?.querySelector('[role="alert"]'),
      ).not.toBeNull();
      expect(dialog.rows()).toHaveLength(0);
      expect(executor(dialog).disabled).toBe(true);
      expect(executor(dialog).getAttribute('aria-describedby')).toBe('undo-confirm-read-failed');
    });

    it('treats an incomplete read handed in like a failed one — never classifies half a set', () => {
      const dialog = render({ initialRead: setRead([fullLive(1)], { complete: false }) });

      expect(dialog.element('undo-confirm-read-failed')).not.toBeNull();
      expect(executor(dialog).disabled).toBe(true);
    });

    it('reads the target with exactly one request on "Ziel neu laden", locked with a reason meanwhile', () => {
      const dialog = render({ initialRead: null });

      click(dialog, dialog.button(RELOAD));
      const request = http().expectOne(GQL);
      expect(request.request.body.variables.id).toBe('set-1');
      expect(executor(dialog).disabled).toBe(true);
      expect(executor(dialog).getAttribute('aria-describedby')).toBe('undo-confirm-reading');
      expect(dialog.element('undo-confirm-reading')?.textContent).toContain('wird gelesen');

      request.flush(gqlAnswer([fullLive(1)]));
      dialog.detect();

      expect(dialog.element('undo-confirm-read-failed')).toBeNull();
      expect(executor(dialog).textContent?.trim()).toBe(SAVE);
      expect(executor(dialog).disabled).toBe(false);
    });

    it('shows an incomplete reload as a failed read and releases nothing', () => {
      const dialog = render({ initialRead: null });

      click(dialog, dialog.button(RELOAD));
      // One entry delivered, two promised: the read stops short.
      http()
        .expectOne(GQL)
        .flush(gqlAnswer([fullLive(1)], 2));
      dialog.detect();

      expect(dialog.element('undo-confirm-read-failed')).not.toBeNull();
      expect(executor(dialog).disabled).toBe(true);
    });

    it('shows a failed reload as a failed read and releases nothing', () => {
      const dialog = render();

      click(dialog, dialog.button(RELOAD));
      http().expectOne(GQL).flush('boom', { status: 500, statusText: 'Server Error' });
      dialog.detect();

      expect(dialog.element('undo-confirm-read-failed')).not.toBeNull();
      expect(executor(dialog).disabled).toBe(true);
    });
  });

  describe('the recovery file and the outcome (AK 6)', () => {
    it('saves the planned file from the held read, then starts with exactly what the file names', async () => {
      const downloads = captureDownloads();
      const read = setRead([fullLive(1), { id: 'src-2', alias: 'Other' }]);
      const candidates = [candidate(1), candidate(2)];
      const dialog = render({ candidates, initialRead: read });

      click(dialog, executor(dialog));

      http().expectNone(GQL);
      expect(downloads).toHaveLength(1);
      expect(downloads[0].filename).toMatch(
        /^emotepurge_targetchannel_transfer-undo-plan_.*\.json$/,
      );
      const record = await savedRecord(downloads[0]);
      expect(record.kind).toBe('transfer-undo');
      expect(record.meta.stage).toBe('planned');
      expect(record.meta.acknowledgedUnproven).toBe(false);
      expect(record.meta.undoneFile).toEqual(FINISHED_FILE);
      // Only the running row — the skipped one (source under another name) is not in the file.
      expect(record.rows).toHaveLength(1);
      expect(record.rows[0]).toMatchObject({
        kind: 'executed',
        mode: 'full',
        sourceSevenTvEmoteId: 'src-1',
        removedSource: { entries: [{ alias: 'Alias1' }], confirmed: false },
      });
      expect(closed).toEqual([]);
      expect(executor(dialog).textContent?.trim()).toBe(START);

      click(dialog, executor(dialog));

      expect(closed).toHaveLength(1);
      const outcome = closed[0];
      expect(outcome?.read).toBe(read);
      expect(outcome?.acknowledgedUnproven).toBe(false);
      expect(outcome?.runnable.map((row) => [row.candidate, row.mode])).toEqual([
        [candidates[0], 'full'],
      ]);
      expect(outcome?.skipped.map((row) => [row.candidate, row.reason])).toEqual([
        [candidates[1], 'sourceUnderOtherName'],
      ]);
    });

    it('asks for a new file after "Ziel neu laden", even when the target did not change', () => {
      captureDownloads();
      const dialog = render();

      click(dialog, executor(dialog));
      expect(executor(dialog).textContent?.trim()).toBe(START);

      click(dialog, dialog.button(RELOAD));
      http()
        .expectOne(GQL)
        .flush(gqlAnswer([fullLive(1)]));
      dialog.detect();

      expect(executor(dialog).textContent?.trim()).toBe(SAVE);
    });

    it('starts at once and writes no file for a plan that only adds back', () => {
      const downloads = captureDownloads();
      // The source is already gone, the target is missing: nothing to remove.
      const dialog = render({ initialRead: setRead([]) });

      expect(executor(dialog).textContent?.trim()).toBe(START);
      click(dialog, executor(dialog));

      expect(downloads).toHaveLength(0);
      expect(closed).toHaveLength(1);
      expect(closed[0]?.runnable.map((row) => row.mode)).toEqual(['addOnly']);
    });

    it('stays on "Rückweg sichern" with a notice when the browser refuses the download', () => {
      captureDownloads({ refuse: true });
      vi.spyOn(console, 'error').mockImplementation(() => undefined);
      const dialog = render();

      click(dialog, executor(dialog));

      expect(closed).toEqual([]);
      expect(executor(dialog).textContent?.trim()).toBe(SAVE);
      expect(
        dialog.element('undo-confirm-save-failed')?.querySelector('[role="alert"]'),
      ).not.toBeNull();
    });

    it('closes with null on cancel', () => {
      const dialog = render();

      click(dialog, dialog.button(CANCEL));

      expect(closed).toEqual([null]);
    });
  });

  describe('the origin lock of a planned file (K2, AK 28)', () => {
    /** A planned file of a run that — as far as the file knows — may never have started: one row
     *  whose live state looks like a finished replace, and one whose source is already gone. */
    function mixedPlanned(): { candidates: UndoCandidate[]; read: SevenTvSetEntries } {
      return {
        candidates: [
          candidate(1, { provenance: 'unproven' }),
          candidate(2, { provenance: 'unproven' }),
        ],
        read: setRead([fullLive(1)]),
      };
    }

    it('skips the unproven full row without the confirmation and starts the rest directly', () => {
      const downloads = captureDownloads();
      const { candidates, read } = mixedPlanned();
      const dialog = render({ candidates, initialRead: read, sourceFile: PLANNED_FILE });

      const [first] = dialog.rows();
      expect(first.textContent).toContain('unbelegt');
      expect(first.textContent).toContain('Übersprungen: unbelegt und nicht bestätigt');
      expect(executor(dialog).textContent?.trim()).toBe(START);

      click(dialog, executor(dialog));

      expect(downloads).toHaveLength(0);
      const outcome = closed[0];
      expect(outcome?.acknowledgedUnproven).toBe(false);
      expect(outcome?.runnable.map((row) => [row.candidate, row.mode])).toEqual([
        [candidates[1], 'addOnly'],
      ]);
      expect(outcome?.skipped.map((row) => [row.candidate, row.reason])).toEqual([
        [candidates[0], 'skippedUnproven'],
      ]);
      // The skipped row still says what the set holds now (S under its alias).
      expect(outcome?.skipped[0].live.sourceEntries).toEqual(['Alias1']);
    });

    it('names the confirmation as its checkbox’s accessible name and describes its effect', () => {
      const { candidates, read } = mixedPlanned();
      const dialog = render({ candidates, initialRead: read, sourceFile: PLANNED_FILE });

      const box = dialog.checkbox();
      expect(box?.closest('label')?.textContent).toContain(
        'Die eingelesene Datei wurde vor der Übertragung geschrieben',
      );
      const hintId = box?.getAttribute('aria-describedby') ?? '';
      expect(dialog.element(hintId)?.textContent).toContain('übersprungen');
    });

    it('locks the action row with a reason when nothing but unproven full rows would run', () => {
      const dialog = render({
        candidates: [candidate(1, { provenance: 'unproven' })],
        sourceFile: PLANNED_FILE,
      });

      expect(executor(dialog).disabled).toBe(true);
      expect(executor(dialog).getAttribute('aria-describedby')).toBe('undo-confirm-blocked');
      expect(dialog.element('undo-confirm-blocked')?.textContent).toContain('Bestätigung');

      click(dialog, executor(dialog));
      expect(closed).toEqual([]);
    });

    it('runs the unproven full row once confirmed, behind the recovery file that records it', async () => {
      const downloads = captureDownloads();
      const { candidates, read } = mixedPlanned();
      const dialog = render({ candidates, initialRead: read, sourceFile: PLANNED_FILE });

      toggleAcknowledgement(dialog);
      expect(executor(dialog).textContent?.trim()).toBe(SAVE);
      click(dialog, executor(dialog));
      click(dialog, executor(dialog));

      expect((await savedRecord(downloads[0])).meta.acknowledgedUnproven).toBe(true);
      expect(closed[0]?.acknowledgedUnproven).toBe(true);
      expect(closed[0]?.runnable.map((row) => [row.candidate, row.mode])).toEqual([
        [candidates[0], 'full'],
        [candidates[1], 'addOnly'],
      ]);
      expect(closed[0]?.skipped).toEqual([]);
    });

    it('asks for a new file after the confirmation is toggled off and on again', () => {
      captureDownloads();
      const { candidates, read } = mixedPlanned();
      const dialog = render({ candidates, initialRead: read, sourceFile: PLANNED_FILE });

      toggleAcknowledgement(dialog);
      click(dialog, executor(dialog));
      expect(executor(dialog).textContent?.trim()).toBe(START);

      toggleAcknowledgement(dialog);
      toggleAcknowledgement(dialog);

      // The file on disk was written for a plan object that is gone.
      expect(executor(dialog).textContent?.trim()).toBe(SAVE);
    });

    it('shows neither mark nor confirmation for a finished file', () => {
      const dialog = render();

      expect(dialog.checkbox()).toBeNull();
      expect(dialog.text()).not.toContain('unbelegt');
    });
  });

  describe('rows (K1, Festlegung 7)', () => {
    function sourceImage(row: HTMLLIElement): string | null {
      return row.querySelector('img')?.getAttribute('src') ?? null;
    }

    it('draws the source from its animated flag and the target as a placeholder with its names', () => {
      const candidates = [
        candidate(1),
        candidate(2),
        candidate(3, { target: { entries: [{ alias: 'Alias3' }, { alias: null }] } }),
      ];
      const dialog = render({
        candidates,
        initialRead: setRead([fullLive(1, false), fullLive(2, true), fullLive(3)]),
      });

      const [still, animated, withoutFlag] = dialog.rows();
      expect(sourceImage(still)).toBe('https://cdn.7tv.app/emote/src-1/4x.webp');
      expect(sourceImage(animated)).toBe('https://cdn.7tv.app/emote/src-2/4x_static.webp');
      expect(sourceImage(withoutFlag)).toBe('https://cdn.7tv.app/emote/src-3/4x.webp');
      for (const row of dialog.rows()) {
        expect(row.querySelectorAll('img')).toHaveLength(1);
        expect(row.querySelector('[data-target-placeholder] img')).toBeNull();
      }
      // The aliasless entry is shown under the file's default name.
      expect(withoutFlag.textContent?.replace(/\s+/g, ' ')).toContain('Alias3 · Target3');
    });

    it('keeps the file’s order and shows a skipped row in place, naming the entry it left out', () => {
      const candidates = [
        candidate(1),
        // Source gone, A back already, B held by a third emote: nothing left to add.
        candidate(2, { target: { entries: [{ alias: 'Alias2' }, { alias: 'Taken' }] } }),
        candidate(3),
      ];
      const dialog = render({
        candidates,
        initialRead: setRead([
          fullLive(1),
          { id: 'tgt-2', alias: 'Alias2' },
          { id: 'third', alias: 'Taken' },
          fullLive(3),
        ]),
      });

      const rows = dialog.rows();
      expect(rows.map((row) => row.textContent?.includes('Source1'))).toEqual([true, false, false]);
      expect(rows[1].textContent).toContain('Source2');
      expect(rows[1].textContent).toContain('Übersprungen: nichts zu tun');
      expect(rows[1].textContent).toContain('„Taken“ bleibt aus');
      expect(rows[2].textContent).toContain('Source3');
    });

    it('lists the rows under an accessible name', () => {
      const dialog = render();

      expect(dialog.fixture.nativeElement.querySelector('ul')?.getAttribute('aria-label')).toBe(
        'Ersetzungen aus der Datei',
      );
    });
  });

  describe('totals and slots (E17)', () => {
    it('counts removals and additions of the effective plan and projects a duplicate cell’s net change', () => {
      const dialog = render({
        candidates: [
          candidate(1, {
            target: { entries: [{ alias: 'Alias1' }, { alias: 'Second' }, { alias: null }] },
          }),
        ],
        initialRead: setRead([fullLive(1)]),
      });

      expect(dialog.element('undo-confirm-removals')?.textContent).toContain(
        '1 Quell-Emote wird aus dem Set entfernt.',
      );
      expect(dialog.text()).toContain('3 Ziel-Einträge kommen zurück.');
      // One entry in the set, three back, one out: 1 + 3 − 1.
      expect(dialog.text()).toContain('Das Set hätte danach 3 von 100 Slots belegt.');
    });

    it('warns without locking when the projection overflows the capacity', () => {
      const dialog = render({
        candidates: [
          candidate(1, { target: { entries: [{ alias: 'Alias1' }, { alias: 'Second' }] } }),
        ],
        capacity: 1,
      });

      expect(dialog.text()).toContain('Das überschreitet die Kapazität');
      expect(executor(dialog).disabled).toBe(false);
    });

    it('counts entries already present over every row, skipped ones included', () => {
      const dialog = render({
        candidates: [candidate(1), candidate(2)],
        // Row 2: source gone, target entry already back — nothing to do, but still counted.
        initialRead: setRead([fullLive(1), { id: 'tgt-2', alias: 'Alias2' }]),
      });

      expect(dialog.text()).toContain('1 Ziel-Eintrag aus der Datei ist schon im Set.');
    });
  });

  describe('target lines (#253 4.2 Nr. 7)', () => {
    it('reads the slot preview of a tracked active set from the channel status', () => {
      const dialog = render();

      expect(getSetStatus).toHaveBeenCalledWith('targetchannel');
      expect(dialog.text()).toContain('Kanal: targetchannel');
      expect(dialog.text()).not.toContain('Dieses Set ist gerade nicht aktiv.');
    });

    it('says a tracked set is not active', () => {
      const dialog = render({ target: resolvedTarget({ isActiveSet: false }) });

      expect(dialog.text()).toContain('Dieses Set ist gerade nicht aktiv.');
      expect(loadEmoteSetPreview).toHaveBeenCalledWith('targetchannel', 'set-1');
    });

    it('names the owner of an untracked set and reads its slots through the owner’s login', () => {
      const dialog = render({ target: resolvedTarget({ trackedChannelName: null }) });

      expect(dialog.text()).toContain('Besitzer: OwnerName');
      expect(dialog.text()).not.toContain('Kanal:');
      expect(dialog.text()).not.toContain('nicht aktiv');
      expect(loadEmoteSetPreview).toHaveBeenCalledWith('ownerlogin', 'set-1');
    });

    it('points out when the target is not the set the page shows', () => {
      const shown = render({ target: resolvedTarget({ hostSelectedSetId: 'other-set' }) });

      expect(shown.text()).toContain('Diese Ansicht zeigt von diesem Lauf nichts.');
    });

    it('says so when the file does not name where the transfer came from', () => {
      const dialog = render({ sourceFile: PLANNED_FILE });

      expect(dialog.text()).toContain('Woher die Übertragung kam, nennt die Datei nicht.');
    });
  });

  it('has every undo.confirm text in both languages', () => {
    const keys = (node: unknown, prefix = ''): string[] =>
      typeof node === 'object' && node !== null
        ? Object.entries(node).flatMap(([key, value]) => keys(value, `${prefix}.${key}`))
        : [prefix];

    expect(keys(en.undo.confirm)).toEqual(keys(de.undo.confirm));
  });
});
