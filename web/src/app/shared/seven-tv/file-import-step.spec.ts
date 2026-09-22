import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { ExportEnvelope } from '../export/export-envelope';
import { buildPurgeRunProtocol, purgeRunJson } from '../export/purge-run-export';
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
        wrongChannel: 'Das Protokoll gehört zu einem anderen Channel.',
        wrongSet:
          'Das Protokoll gehört zu einem anderen Emote-Set — der Channel hat das aktive Set gewechselt.',
        noRestorableRows:
          'Das Protokoll enthält keine erfolgreich gelöschten Emotes zum Wiederherstellen.',
      },
    },
  },
};

const CURRENT_CHANNEL = 'somechannel';
const CURRENT_SET = 'set-current';

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
      { key: 'e1', emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done' },
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
  let setId: string;
  let closed: FileImportResult[];

  beforeEach(async () => {
    closed = [];
    channelName = CURRENT_CHANNEL;
    setId = CURRENT_SET;

    await TestBed.configureTestingModule({
      imports: [
        FileImportStep,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(): Harness {
    const fixture = TestBed.createComponent(FileImportStep);
    // Frozen values handed in by the trigger, never read in a constructor (Regel 13).
    fixture.componentRef.setInput('channelName', channelName);
    fixture.componentRef.setInput('setId', setId);
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
    it('reports a restore result carrying only the done rows of a matching purge-run protocol', async () => {
      const dialog = render();

      await dialog.selectFile(
        file(
          purgeRunText({
            items: [
              { key: 'e1', emoteId: 'e1', sevenTvEmoteId: '7tv-1', name: 'PogU', status: 'done' },
              {
                key: 'e2',
                emoteId: 'e2',
                sevenTvEmoteId: '7tv-2',
                name: 'KEKW',
                status: 'failed',
                errorMessage: 'boom',
              },
            ],
          }),
        ),
      );

      expect(closed).toEqual([
        {
          kind: 'restore',
          rows: [
            {
              emoteId: 'e1',
              sevenTvEmoteId: '7tv-1',
              name: 'PogU',
              aliases: ['PogU'],
              status: 'done',
              errorMessage: null,
            },
          ],
        },
      ]);
    });

    it("reports an import result for an emote-list file — the target stays the caller's decision", async () => {
      const dialog = render();

      await dialog.selectFile(file(emoteListText()));

      expect(closed).toHaveLength(1);
      const result = closed[0];
      expect(result?.kind).toBe('import');
      if (result?.kind === 'import') {
        expect(result.source.rows).toEqual([{ sevenTvEmoteId: '7tv-9', name: 'Kappa' }]);
        expect(result.source.origin).toEqual(
          expect.objectContaining({ kind: 'file', channelName: 'otherchannel' }),
        );
      }
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

  describe('the set match is generic over whichever set is shown, not hardcoded to "active" (spec #200, AK 66)', () => {
    it('accepts a protocol naming the Halloween set while the Halloween set is shown', async () => {
      channelName = CURRENT_CHANNEL;
      setId = 'set-halloween';
      const dialog = render();

      await dialog.selectFile(file(purgeRunText({ emoteSetId: 'set-halloween' })));

      expect(closed).toEqual([
        {
          kind: 'restore',
          rows: [
            {
              emoteId: 'e1',
              sevenTvEmoteId: '7tv-1',
              name: 'PogU',
              aliases: ['PogU'],
              status: 'done',
              errorMessage: null,
            },
          ],
        },
      ]);
    });

    it('rejects that same Halloween-set protocol while the main set is shown instead', async () => {
      channelName = CURRENT_CHANNEL;
      setId = 'set-main';
      const dialog = render();

      await dialog.selectFile(file(purgeRunText({ emoteSetId: 'set-halloween' })));

      expect(closed).toEqual([]);
      expect(dialog.alertText()).toBe(DE_TRANSLATIONS.restore.import.errors.wrongSet);
    });
  });

  describe('read/validation errors — all nine keys, none of them report a result', () => {
    it.each([
      ['notJson', () => file('not json{')],
      ['csvInsteadOfJson', () => file('seven_tv_emote_id,name\n7tv-1,PogU\n')],
      ['wrongKind', () => file(wrongKindText())],
      ['wrongChannel', () => file(purgeRunText({ channelName: 'otherchannel' }))],
      ['wrongSet', () => file(purgeRunText({ emoteSetId: 'set-old' }))],
      ['votingExport', () => file(votingText())],
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

      await dialog.selectFile(file(purgeRunText({ channelName: 'otherchannel' })));
      expect(dialog.alertText()).toBe(DE_TRANSLATIONS.restore.import.errors.wrongChannel);

      // Same file content re-selected after correcting nothing but the mistake itself — still a
      // fresh `change`, because the component resets `<input>.value` after every selection.
      await dialog.selectFile(file(purgeRunText()));

      expect(dialog.alertText()).toBeNull();
      expect(closed).toEqual([
        {
          kind: 'restore',
          rows: [
            {
              emoteId: 'e1',
              sevenTvEmoteId: '7tv-1',
              name: 'PogU',
              aliases: ['PogU'],
              status: 'done',
              errorMessage: null,
            },
          ],
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
