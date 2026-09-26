import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, Subject, firstValueFrom, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import {
  EditableSetResolution,
  EditableSetTarget,
} from '../../core/seven-tv/seven-tv-emote-set.model';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { TransferRow } from '../../core/seven-tv/transfer-plan';
import { ExportEnvelope } from '../export/export-envelope';
import { buildPurgeRunProtocol, purgeRunJson } from '../export/purge-run-export';
import {
  buildTransferPlanRecord,
  buildTransferRunProtocol,
  transferRunJson,
} from '../export/transfer-run-export';
import { FileImportResult, FileImportStep } from './file-import-step';

/**
 * Only the keys this step itself renders — not the full app translation file. Error texts are the
 * real German ones (`web/public/i18n/de.json`), so an assertion reads as the sentence the user gets;
 * per rule 12 the wording only identifies *which* banner appeared, it is never the thing under test.
 */
const DE_TRANSLATIONS = {
  restore: {
    import: {
      sorts: {
        purgeRun: 'Purge-Protokoll (Wiederherstellen) als JSON',
        transferRun:
          'Übertragungsprotokoll — Rückweg-Datei oder Ergebnisprotokoll (Wiederherstellen) als JSON',
        emoteList: 'Emote-Liste (Kopieren) als JSON',
        usageExport: 'Nutzungs-Export (Kopieren) als JSON',
      },
      fileLabel: 'Datei auswählen',
      errors: {
        notJson: 'Die Datei ist kein gültiges JSON.',
        csvInsteadOfJson:
          'Das ist die CSV-Fassung. Der Import braucht dieselbe Datei als JSON — beim Export das Format JSON wählen.',
        wrongKind:
          'Die Datei ist kein EmotePurge-Export. Importierbar sind Purge-Protokolle, Emote-Listen und Nutzungs-Exporte im JSON-Format.',
        votingExport:
          'Das ist ein Export einer Abstimmung, kein Purge-Protokoll. Ein importierbares Protokoll entsteht erst bei einem Löschlauf und wird direkt danach zum Download angeboten.',
        wrongVersion: 'Die Datei stammt aus einer neueren EmotePurge-Version.',
        noRows: 'Die Datei enthält keine importierbaren Emotes.',
        noRestorableRows:
          'Das Protokoll enthält keine erfolgreich gelöschten Emotes zum Wiederherstellen.',
        transferRunNoRows: 'Diese Übertragungsdatei enthält keine entfernten Emotes.',
        targetNotEditable:
          'Das Set aus der Datei ist nicht (mehr) bearbeitbar oder existiert nicht mehr.',
        targetNotSelectable: 'Das Set aus der Datei ist kein normales Emote-Set.',
        targetCheckUnavailable:
          'Das Set aus der Datei konnte gerade nicht geprüft werden — bitte gleich noch einmal versuchen.',
        noTargetSetForCopy:
          'Diese Seite hat kein Set, in das kopiert werden könnte. Aus einer Datei lässt sich hier nur wiederherstellen.',
      },
    },
  },
};

const CURRENT_CHANNEL = 'somechannel';
const CURRENT_SET = 'set-current';

/** What the shared pre-check answers for the file's set in the default (editable) case — a
 *  deliberately different display name and owner than anything the file or the page carries, so an
 *  assertion on the emitted target proves where each field came from (AK 35). */
const RESOLVED_TARGET: EditableSetTarget = {
  emoteSetId: CURRENT_SET,
  setName: 'Hauptset (aus der Zielliste)',
  ownerDisplayName: 'SomeChannel',
  twitchLogin: 'somechannel',
  trackedChannelName: CURRENT_CHANNEL,
  isActiveSet: true,
};

/** The restore rows `purgeRunText()` (with its default single done row) yields. */
const PURGE_RESTORE_ROWS = [
  {
    emoteId: 'e1',
    sevenTvEmoteId: '7tv-1',
    name: 'PogU',
    aliases: ['PogU'],
    status: 'done',
    errorMessage: null,
  },
];

/** Always resolves to exactly this string — sidesteps whatever `Blob`/`File.text()` support the
 *  test environment happens to have. */
function file(text: string, name = 'export.json'): File {
  const f = new File([text], name, { type: 'application/json' });
  Object.defineProperty(f, 'text', { value: () => Promise.resolve(text) });
  return f;
}

function purgeRunText(
  overrides: {
    channelName?: string;
    emoteSetId?: string;
    formatVersion?: number;
    items?: (RunQueueItem & { emoteId: string })[];
  } = {},
): string {
  const protocol = buildPurgeRunProtocol({
    channelName: overrides.channelName ?? CURRENT_CHANNEL,
    emoteSetId: overrides.emoteSetId ?? CURRENT_SET,
    startedAt: Date.parse('2026-09-01T10:00:00Z'),
    finishedAt: Date.parse('2026-09-01T10:05:00Z'),
    items: overrides.items ?? [
      {
        key: 'e1',
        emoteId: 'e1',
        sevenTvEmoteId: '7tv-1',
        name: 'PogU',
        status: 'done',
        completedSteps: 1,
        failedStep: null,
      },
    ],
  });
  if (overrides.formatVersion !== undefined) {
    return JSON.stringify({ ...protocol, formatVersion: overrides.formatVersion });
  }
  return purgeRunJson(protocol);
}

function emoteListText(overrides: Partial<ExportEnvelope<unknown>> = {}): string {
  const envelope: ExportEnvelope<unknown> = {
    source: 'emotepurge',
    kind: 'emote-list',
    formatVersion: 1,
    exportedAt: '2026-09-01T10:00:00Z',
    channelName: 'otherchannel',
    withheld: [],
    meta: {},
    rows: [{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }],
    ...overrides,
  };
  return JSON.stringify(envelope);
}

function votingText(): string {
  return JSON.stringify({
    source: 'emotepurge',
    kind: 'voting',
    formatVersion: 1,
    exportedAt: '2026-09-01T10:00:00Z',
    channelName: 'otherchannel',
    withheld: [],
    meta: {},
    rows: [],
  });
}

/** A replace row of `Kappa` against target `tgt-1`, which sat in the set as `KappaOld`. */
const REPLACE_ROW: TransferRow = {
  action: 'replace',
  source: { sevenTvEmoteId: 'src-1', name: 'Kappa', imageUrl: null },
  alias: 'Kappa',
  target: {
    sevenTvEmoteId: 'tgt-1',
    aliases: ['KappaOld'],
    hasAliaslessEntry: false,
    defaultName: 'KappaDefault',
  },
};

/** Either stage of a transfer-run file with that one replace row, into `channelName`'s `set`. The
 *  `finished` stage's REMOVE is confirmed unless `removeConfirmed` is false. */
function transferRunText(
  stage: 'planned' | 'finished',
  options: { channelName?: string; removeConfirmed?: boolean } = {},
): string {
  const target = {
    targetEmoteSetId: CURRENT_SET,
    targetChannelName: options.channelName ?? CURRENT_CHANNEL,
    targetOwnerDisplayName: null,
    origin: { kind: 'channel' as const, channelName: 'quellkanal' },
  };
  if (stage === 'planned') {
    return transferRunJson(
      buildTransferPlanRecord({
        ...target,
        verifiedAt: 0,
        plan: { rows: [REPLACE_ROW] },
        entries: {
          aliasesById: new Map([['tgt-1', ['KappaOld']]]),
          aliaslessIds: new Set(),
          defaultNameById: new Map([['tgt-1', 'KappaDefault']]),
          animatedById: new Map([['tgt-1', false]]),
          occupiedSlots: 1,
          complete: true,
        },
        defaultNameById: new Map([['tgt-1', 'KappaDefault']]),
      }),
    );
  }
  const confirmed = options.removeConfirmed ?? true;
  return transferRunJson(
    buildTransferRunProtocol({
      ...target,
      startedAt: 0,
      finishedAt: 1,
      items: [
        {
          key: 'src-1',
          sevenTvEmoteId: 'src-1',
          name: 'Kappa',
          transfer: REPLACE_ROW,
          status: 'failed',
          completedSteps: confirmed ? 1 : 0,
          failedStep: confirmed ? 1 : 0,
        },
      ],
    }),
  );
}

/** What either stage of `transferRunText` restores: the removed target, under its old alias. */
const TRANSFER_RESTORE_ROW = {
  emoteId: null,
  sevenTvEmoteId: 'tgt-1',
  name: 'KappaOld',
  aliases: ['KappaOld'],
  defaultName: 'KappaDefault',
};

function wrongKindText(): string {
  return JSON.stringify({
    source: 'emotepurge',
    kind: 'something-else',
    formatVersion: 1,
    exportedAt: '2026-09-01T10:00:00Z',
    channelName: 'x',
    withheld: [],
    meta: {},
    rows: [],
  });
}

/** The target `picked` must carry for a file naming `emoteSetId`: the pre-check's own answer for
 *  it plus the two host fields this step was handed — nothing taken from the file itself. */
function expectedTarget(emoteSetId: string, host: { channel: string; selected: string | null }) {
  return {
    ...RESOLVED_TARGET,
    emoteSetId,
    hostChannelName: host.channel,
    hostSelectedSetId: host.selected,
  };
}

interface Harness {
  fixture: ComponentFixture<FileImportStep>;
  pickerButton(): HTMLButtonElement;
  alertText(): string | null;
  focusableInOrder(): Element[];
  /** Drives `onFileSelected` directly with a synthetic `Event`/`<input>` pair, awaiting the whole
   *  (async) handler — a real `dispatchEvent('change')` would leave its `await file.text()` still
   *  in flight with nothing in this zoneless setup to signal when it settles. `undefined` models
   *  the native file dialog being cancelled. */
  selectFile(selected: File | undefined): Promise<void>;
}

describe('FileImportStep', () => {
  let channelName: string;
  let hostSelectedSetId: string | null;
  let closed: FileImportResult[];
  /** The shared pre-check (spec 6.2) — `editable` for the file's own set unless a test says
   *  otherwise. Every call is one would-be request to the target list. */
  let resolveEditableSet: ReturnType<
    typeof vi.fn<(emoteSetId: string) => Observable<EditableSetResolution>>
  >;

  beforeEach(async () => {
    closed = [];
    channelName = CURRENT_CHANNEL;
    hostSelectedSetId = CURRENT_SET;
    resolveEditableSet = vi.fn((emoteSetId: string) =>
      of<EditableSetResolution>({
        status: 'editable',
        target: { ...RESOLVED_TARGET, emoteSetId },
      }),
    );

    await TestBed.configureTestingModule({
      imports: [
        FileImportStep,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        {
          provide: SevenTvEmoteSetService,
          useValue: { resolveEditableSet } as unknown as SevenTvEmoteSetService,
        },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(): Harness {
    const fixture = TestBed.createComponent(FileImportStep);
    // Frozen values handed in by the trigger, never read in a constructor (Regel 13).
    fixture.componentRef.setInput('channelName', channelName);
    fixture.componentRef.setInput('hostSelectedSetId', hostSelectedSetId);
    fixture.componentInstance.picked.subscribe((result) => closed.push(result));
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function buttons(): HTMLButtonElement[] {
      return Array.from(host.querySelectorAll('button'));
    }

    return {
      fixture,
      pickerButton: () => {
        const found = buttons()[0];
        if (!found) {
          throw new Error('no file-picker button rendered');
        }
        return found;
      },
      alertText: () => host.querySelector('[role="alert"]')?.textContent?.trim() ?? null,
      focusableInOrder: () => Array.from(host.querySelectorAll('button, input, a[href]')),
      selectFile: async (selected) => {
        const input = document.createElement('input');
        Object.defineProperty(input, 'files', {
          value: selected ? [selected] : [],
          configurable: true,
        });
        const event = { target: input } as unknown as Event;
        const instance = fixture.componentInstance as unknown as {
          onFileSelected(e: Event): Promise<void>;
        };
        await instance.onFileSelected(event);
        fixture.detectChanges();
      },
    };
  }

  describe('reported result by file sort (plan §1.1 — the discriminated result contract)', () => {
    it('reports a restore result carrying only the done rows of a purge-run protocol, and its resolved target', async () => {
      const dialog = render();

      await dialog.selectFile(
        file(
          purgeRunText({
            items: [
              {
                key: 'e1',
                emoteId: 'e1',
                sevenTvEmoteId: '7tv-1',
                name: 'PogU',
                status: 'done',
                completedSteps: 1,
                failedStep: null,
              },
              {
                key: 'e2',
                emoteId: 'e2',
                sevenTvEmoteId: '7tv-2',
                name: 'KEKW',
                status: 'failed',
                completedSteps: 0,
                failedStep: 0,
                errorMessage: 'boom',
              },
            ],
          }),
        ),
      );

      expect(closed).toEqual([
        {
          kind: 'restore',
          rows: PURGE_RESTORE_ROWS,
          target: expectedTarget(CURRENT_SET, { channel: CURRENT_CHANNEL, selected: CURRENT_SET }),
        },
      ]);
    });

    it.each(['planned', 'finished'] as const)(
      'reports a restore result carrying the removed target of a %s transfer-run file',
      async (stage) => {
        const dialog = render();

        await dialog.selectFile(file(transferRunText(stage)));

        expect(closed).toEqual([
          {
            kind: 'restore',
            rows: [TRANSFER_RESTORE_ROW],
            target: expectedTarget(CURRENT_SET, {
              channel: CURRENT_CHANNEL,
              selected: CURRENT_SET,
            }),
          },
        ]);
        expect(dialog.alertText()).toBeNull();
      },
    );

    it("reports an import result for an emote-list file — the target stays the caller's decision", async () => {
      const dialog = render();

      await dialog.selectFile(file(emoteListText()));

      expect(closed).toHaveLength(1);
      const result = closed[0];
      expect(result?.kind).toBe('import');
      if (result?.kind === 'import') {
        expect(result.source.rows).toEqual([
          { sevenTvEmoteId: '7tv-9', name: 'Kappa', imageUrl: null },
        ]);
        expect(result.source.origin).toEqual(
          expect.objectContaining({ kind: 'file', channelName: 'otherchannel' }),
        );
      }
      // A copy file names no target of its own — nothing to check against the target list.
      expect(resolveEditableSet).not.toHaveBeenCalled();
    });

    it('reports an import result for a usage export, same path as an emote-list file', async () => {
      const dialog = render();

      await dialog.selectFile(
        file(
          emoteListText({
            kind: 'usage',
            rows: [{ sevenTvEmoteId: '7tv-1', emoteName: 'PogU', totalUseCount: 3 }],
          }),
        ),
      );

      expect(closed).toHaveLength(1);
      expect(closed[0]?.kind).toBe('import');
    });
  });

  describe('the file names the target, the target list checks it (spec #253, 4.1/4.2, E1/E2)', () => {
    // AK 1, 2, 35: a protocol of another channel's (differently cased) other set is not refused —
    // its set is checked, and what goes out is the pre-check's answer, never the file's own
    // channel name or a page value.
    it("checks the file's own set and emits exactly the pre-check's target plus the host fields, whatever page reads it", async () => {
      resolveEditableSet.mockReturnValue(
        of({
          status: 'editable',
          target: {
            emoteSetId: 'set-halloween',
            setName: 'Halloween',
            ownerDisplayName: 'Andere Besitzerin',
            twitchLogin: 'besitzerin',
            trackedChannelName: null,
            isActiveSet: false,
          },
        }),
      );
      const dialog = render();

      await dialog.selectFile(
        file(purgeRunText({ channelName: 'OtherChannel', emoteSetId: 'set-halloween' })),
      );

      expect(resolveEditableSet).toHaveBeenCalledTimes(1);
      expect(resolveEditableSet).toHaveBeenCalledWith('set-halloween');
      expect(closed).toEqual([
        {
          kind: 'restore',
          rows: PURGE_RESTORE_ROWS,
          target: {
            emoteSetId: 'set-halloween',
            setName: 'Halloween',
            ownerDisplayName: 'Andere Besitzerin',
            twitchLogin: 'besitzerin',
            trackedChannelName: null,
            isActiveSet: false,
            hostChannelName: CURRENT_CHANNEL,
            hostSelectedSetId: CURRENT_SET,
          },
        },
      ]);
      expect(dialog.alertText()).toBeNull();
    });

    // AK 3–5: every blocked outcome keeps the step open with its own banner and reports nothing.
    it.each([
      ['notEditable', 'targetNotEditable'],
      ['notSelectable', 'targetNotSelectable'],
      ['unavailable', 'targetCheckUnavailable'],
    ] as const)(
      'shows the %s outcome of the target check as the %s banner and reports nothing',
      async (status, key) => {
        resolveEditableSet.mockReturnValue(of({ status }));
        const dialog = render();

        await dialog.selectFile(file(transferRunText('finished')));

        expect(closed).toEqual([]);
        expect(dialog.alertText()).toBe(DE_TRANSLATIONS.restore.import.errors[key]);
      },
    );

    // AK 4 / F3: a 429 (or 503, or a dropped connection) is "not checkable right now", never "not
    // allowed".
    it('shows the targetCheckUnavailable banner when the target list request itself fails', async () => {
      resolveEditableSet.mockReturnValue(
        throwError(() => new HttpErrorResponse({ status: 429, statusText: 'Too Many Requests' })),
      );
      const dialog = render();

      await dialog.selectFile(file(purgeRunText()));

      expect(closed).toEqual([]);
      expect(dialog.alertText()).toBe(DE_TRANSLATIONS.restore.import.errors.targetCheckUnavailable);
    });

    // F6: `picked` closes the dialog, and the check is asynchronous — no second pick meanwhile.
    it('locks the file control while the check runs and takes no second pick, then unlocks it', async () => {
      const pending = new Subject<EditableSetResolution>();
      resolveEditableSet.mockReturnValue(pending);
      const dialog = render();
      const fileInput = (dialog.fixture.nativeElement as HTMLElement).querySelector(
        'input[type="file"]',
      ) as HTMLInputElement;
      const openPicker = vi.spyOn(fileInput, 'click').mockImplementation(() => undefined);

      await dialog.selectFile(file(purgeRunText()));

      expect(dialog.pickerButton().getAttribute('aria-disabled')).toBe('true');
      dialog.pickerButton().click();
      expect(openPicker).not.toHaveBeenCalled();
      await dialog.selectFile(file(purgeRunText({ emoteSetId: 'set-other' })));
      expect(resolveEditableSet).toHaveBeenCalledTimes(1);

      pending.next({ status: 'notEditable' });
      pending.complete();
      dialog.fixture.detectChanges();

      expect(dialog.pickerButton().getAttribute('aria-disabled')).toBeNull();
      expect(dialog.alertText()).toBe(DE_TRANSLATIONS.restore.import.errors.targetNotEditable);
      dialog.pickerButton().click();
      expect(openPicker).toHaveBeenCalledTimes(1);
    });

    it('ignores an answer that arrives after the step is gone (dialog cancelled mid-check)', async () => {
      const pending = new Subject<EditableSetResolution>();
      resolveEditableSet.mockReturnValue(pending);
      const dialog = render();

      await dialog.selectFile(file(purgeRunText()));
      dialog.fixture.destroy();
      pending.next({ status: 'editable', target: RESOLVED_TARGET });

      expect(closed).toEqual([]);
      expect(pending.observed).toBe(false);
    });
  });

  describe('a page without a selected set (spec #253, E22, 4.1 point 1)', () => {
    it('still reads a restore file, with hostSelectedSetId null on the emitted target', async () => {
      hostSelectedSetId = null;
      const dialog = render();

      await dialog.selectFile(file(transferRunText('planned')));

      expect(closed).toEqual([
        {
          kind: 'restore',
          rows: [TRANSFER_RESTORE_ROW],
          target: expectedTarget(CURRENT_SET, { channel: CURRENT_CHANNEL, selected: null }),
        },
      ]);
    });

    // Refused before the copy parser runs: an emote list without a single valid row would
    // otherwise be answered `noRows`, which is not the reason it cannot be used here.
    it('refuses a copy file with noTargetSetForCopy before reading its rows', async () => {
      hostSelectedSetId = null;
      const dialog = render();

      await dialog.selectFile(file(emoteListText({ rows: [{ sevenTvEmoteId: '', name: 'x' }] })));

      expect(closed).toEqual([]);
      expect(dialog.alertText()).toBe(DE_TRANSLATIONS.restore.import.errors.noTargetSetForCopy);
      expect(resolveEditableSet).not.toHaveBeenCalled();
    });
  });

  describe('read/validation errors — all eight file keys, none of them report a result', () => {
    it.each([
      ['notJson', () => file('not json{')],
      ['csvInsteadOfJson', () => file('seven_tv_emote_id,name\n7tv-1,PogU\n')],
      ['wrongKind', () => file(wrongKindText())],
      ['votingExport', () => file(votingText())],
      ['transferRunNoRows', () => file(transferRunText('finished', { removeConfirmed: false }))],
      // 2 is PURGE_RUN_FORMAT_VERSION itself (spec #200, K5 finding C) — 99 is unambiguously beyond
      // every version this parser knows.
      ['wrongVersion', () => file(purgeRunText({ formatVersion: 99 }))],
      ['noRows', () => file(emoteListText({ rows: [{ sevenTvEmoteId: '', name: 'x' }] }))],
      [
        'noRestorableRows',
        () =>
          file(
            purgeRunText({
              items: [
                {
                  key: 'e1',
                  emoteId: 'e1',
                  sevenTvEmoteId: '7tv-1',
                  name: 'PogU',
                  status: 'failed',
                  completedSteps: 0,
                  failedStep: 0,
                  errorMessage: 'boom',
                },
              ],
            }),
          ),
      ],
    ] as const)('shows the %s banner and reports nothing', async (key, buildFile) => {
      const dialog = render();

      await dialog.selectFile(buildFile());

      expect(closed).toEqual([]);
      expect(dialog.alertText()).toBe(DE_TRANSLATIONS.restore.import.errors[key]);
    });
  });

  describe('edge cases (plan §1.5)', () => {
    it('reports nothing and shows no banner when the native file dialog is cancelled', async () => {
      const dialog = render();

      await dialog.selectFile(undefined);

      expect(closed).toEqual([]);
      expect(dialog.alertText()).toBeNull();
    });

    it('resets a previous error banner on every new attempt, regardless of the new outcome', async () => {
      const dialog = render();

      await dialog.selectFile(file('not json{'));
      expect(dialog.alertText()).toBe(DE_TRANSLATIONS.restore.import.errors.notJson);

      // The corrected file, picked through the same control — still a fresh `change`, because the
      // component resets `<input>.value` after every selection.
      await dialog.selectFile(file(purgeRunText()));

      expect(dialog.alertText()).toBeNull();
      expect(closed).toEqual([
        {
          kind: 'restore',
          rows: PURGE_RESTORE_ROWS,
          target: expectedTarget(CURRENT_SET, { channel: CURRENT_CHANNEL, selected: CURRENT_SET }),
        },
      ]);
    });
  });

  describe('accessibility', () => {
    it('gives the file control an accessible name and makes it the first focusable element', () => {
      const dialog = render();

      const picker = dialog.pickerButton();
      expect(picker.textContent?.trim()).toBe('Datei auswählen');
      expect(dialog.focusableInOrder()[0]).toBe(picker);
    });

    it('renders the error banner with role="alert"', async () => {
      const dialog = render();

      await dialog.selectFile(file('not json{'));

      const alert = (dialog.fixture.nativeElement as HTMLElement).querySelector('[role="alert"]');
      expect(alert).not.toBeNull();
    });
  });
});
