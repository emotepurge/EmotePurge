import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { ExportChoice, ExportDialog, ExportDialogData, ExportDialogOption } from './export-dialog';

// Only the keys this dialog translates — not the full app translation file. Texts are the real
// German ones (from web/public/i18n/de.json) where they carry meaning for a test; the option
// labels/legend used only to prove "supplied by the caller, not hardwired" are made-up test
// strings, so a passing assertion cannot be explained by a hardcoded real key matching by luck.
const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen' },
  export: {
    title: 'Export',
    scopeLabel: 'Exportumfang',
    scopeVisible: 'Gefilterte Liste ({{count}})',
    scopeSelection: 'Auswahl ({{count}})',
    scopeNoSelectionHint: 'Ohne Auswahl im Raster gilt die ganze sichtbare Liste.',
    rowCount: { one: '{{count}} Zeile', other: '{{count}} Zeilen' },
    filteredHint: 'Exportiert wird die aktuell gefilterte Liste.',
    submit: 'Exportieren',
    formatCsv: 'CSV (Tabellenkalkulation)',
    formatJson: 'JSON (Datenauszug)',
    testLegend: 'Testlegende',
    testLabelA: 'Label A',
    testLabelB: 'Label B',
    testHintB: 'Hinweis B',
  },
};

const CANCEL = 'Abbrechen';
const SUBMIT = 'Exportieren';

const FORMAT_LIKE_OPTIONS: readonly ExportDialogOption[] = [
  { id: 'csv', labelKey: 'export.formatCsv' },
  { id: 'json', labelKey: 'export.formatJson' },
];

/** One option without a hint, one with — for the accessible-name case (E2). */
const MIXED_OPTIONS: readonly ExportDialogOption[] = [
  { id: 'a', labelKey: 'export.testLabelA' },
  { id: 'b', labelKey: 'export.testLabelB', hintKey: 'export.testHintB' },
];

function defaultData(overrides: Partial<ExportDialogData> = {}): ExportDialogData {
  return {
    rowCount: 10,
    filtered: false,
    selectionCount: 0,
    noticeKeys: [],
    optionsLegendKey: 'export.testLegend',
    options: FORMAT_LIKE_OPTIONS,
    ...overrides,
  };
}

interface Harness {
  fixture: ComponentFixture<ExportDialog>;
  detect(): void;
  text(): string;
  button(label: string): HTMLButtonElement;
  optionInputs(): HTMLInputElement[];
  optionLabels(): HTMLLabelElement[];
  optionLegend(): string | null;
  scopeInputs(): HTMLInputElement[];
  rowCountText(): string;
}

describe('ExportDialog', () => {
  let dialogData: ExportDialogData;
  let closed: (ExportChoice | undefined)[];

  beforeEach(async () => {
    closed = [];

    await TestBed.configureTestingModule({
      imports: [
        ExportDialog,
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
            close: (result?: ExportChoice) => closed.push(result),
          },
        },
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(data: ExportDialogData = defaultData()): Harness {
    dialogData = data;
    const fixture = TestBed.createComponent(ExportDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;

    function buttons(): HTMLButtonElement[] {
      return Array.from(host.querySelectorAll('button'));
    }

    function optionInputs(): HTMLInputElement[] {
      return Array.from(host.querySelectorAll<HTMLInputElement>('input[name="export-option"]'));
    }

    function optionLabels(): HTMLLabelElement[] {
      return optionInputs()
        .map((input) => input.closest('label'))
        .filter((label): label is HTMLLabelElement => label !== null);
    }

    return {
      fixture,
      detect: () => fixture.detectChanges(),
      text: () => host.textContent ?? '',
      button: (label) => {
        const found = buttons().find((button) => button.textContent?.trim() === label);
        if (!found) {
          throw new Error(`no button labelled "${label}"`);
        }
        return found;
      },
      optionInputs,
      optionLabels,
      optionLegend: () => {
        const group = Array.from(host.querySelectorAll('[role="radiogroup"]')).find((element) =>
          element.querySelector('input[name="export-option"]'),
        );
        return group?.getAttribute('aria-label') ?? null;
      },
      scopeInputs: () =>
        Array.from(host.querySelectorAll<HTMLInputElement>('input[name="export-scope"]')),
      rowCountText: () => host.querySelector('p.text-sm')?.textContent?.trim() ?? '',
    };
  }

  /** Normalizes a label's full text to one space-separated string — the same thing an accessible
   *  name computation collapses whitespace/newlines into. */
  function normalizedLabelText(label: HTMLLabelElement): string {
    return (label.textContent ?? '').trim().replace(/\s+/g, ' ');
  }

  /** The label/hint text pair as two separate DOM nodes, so order/content assertions don't depend
   *  on whether adjacent block elements happen to produce a whitespace text node between them. */
  function optionTextParts(label: HTMLLabelElement): { label: string; hint: string | null } {
    const wrapper = label.querySelector('span.flex.flex-col');
    const children = wrapper ? Array.from(wrapper.children) : [];
    return {
      label: children[0]?.textContent?.trim() ?? '',
      hint: children[1] ? (children[1].textContent?.trim() ?? '') : null,
    };
  }

  describe('preselection and order (E1 — options[0] is the default, no second default concept)', () => {
    it('preselects the first supplied option', () => {
      const dialog = render(defaultData({ options: FORMAT_LIKE_OPTIONS }));

      const inputs = dialog.optionInputs();
      expect(inputs).toHaveLength(2);
      expect(inputs[0].checked).toBe(true);
      expect(inputs[1].checked).toBe(false);
    });

    it('preselects options[0] even when the caller supplies a different order', () => {
      const reordered: readonly ExportDialogOption[] = [
        { id: 'json', labelKey: 'export.formatJson' },
        { id: 'csv', labelKey: 'export.formatCsv' },
      ];
      const dialog = render(defaultData({ options: reordered }));

      dialog.button(SUBMIT).click();

      expect(closed).toEqual([{ optionId: 'json', scope: 'visible' }]);
    });

    it('renders options in the exact order supplied, not a hardcoded csv-then-json order', () => {
      const dialog = render(defaultData({ options: MIXED_OPTIONS }));

      const parts = dialog.optionLabels().map(optionTextParts);
      expect(parts).toEqual([
        { label: 'Label A', hint: null },
        { label: 'Label B', hint: 'Hinweis B' },
      ]);
    });
  });

  describe('accessible name (E2 — the hint line lives inside the label)', () => {
    it('an option with a hintKey carries both lines in its accessible name', () => {
      const dialog = render(defaultData({ options: MIXED_OPTIONS }));

      const withHint = normalizedLabelText(dialog.optionLabels()[1]);
      expect(withHint).toContain('Label B');
      expect(withHint).toContain('Hinweis B');
    });

    it('an option without a hintKey carries only the label, no stray hint text', () => {
      const dialog = render(defaultData({ options: MIXED_OPTIONS }));

      const withoutHint = normalizedLabelText(dialog.optionLabels()[0]);
      expect(withoutHint).toBe('Label A');
    });
  });

  describe('scope group', () => {
    it('is absent when selectionCount is 0', () => {
      const dialog = render(defaultData({ selectionCount: 0 }));

      expect(dialog.scopeInputs()).toHaveLength(0);
    });

    it('is present when selectionCount is greater than 0', () => {
      const dialog = render(defaultData({ selectionCount: 3 }));

      expect(dialog.scopeInputs()).toHaveLength(2);
    });

    // #144: the scope radiogroup only exists once something is selected, so a viewer who never
    // selects never learns it's there. This hint fills exactly the slot the radiogroup would
    // otherwise occupy, so it must not coexist with it.
    it('shows the no-selection hint in place of the radiogroup when selectionCount is 0', () => {
      const dialog = render(defaultData({ selectionCount: 0 }));

      expect(dialog.text()).toContain('Ohne Auswahl im Raster gilt die ganze sichtbare Liste.');
      expect(dialog.scopeInputs()).toHaveLength(0);
    });

    it('does not show the no-selection hint once a selection exists', () => {
      const dialog = render(defaultData({ selectionCount: 3 }));

      expect(dialog.text()).not.toContain('Ohne Auswahl im Raster gilt die ganze sichtbare Liste.');
    });

    // #145: null is a caller that has no grid-selection concept at all (voting export, purge
    // protocol) — distinct from 0, a real but currently-empty selection. Neither the radiogroup
    // nor the hint belongs here, because there is nothing to explain the absence of.
    it('shows neither the radiogroup nor the no-selection hint when selectionCount is null', () => {
      const dialog = render(defaultData({ selectionCount: null }));

      expect(dialog.scopeInputs()).toHaveLength(0);
      expect(dialog.text()).not.toContain('Ohne Auswahl im Raster gilt die ganze sichtbare Liste.');
    });

    it('follows the displayed row count to the chosen scope', () => {
      const dialog = render(defaultData({ rowCount: 10, selectionCount: 3 }));

      expect(dialog.rowCountText()).toBe('10 Zeilen');

      dialog.scopeInputs()[1].dispatchEvent(new Event('change'));
      dialog.detect();

      expect(dialog.rowCountText()).toBe('3 Zeilen');
    });
  });

  // Konzept "Auswahl überlebt Suche und Filter" nachtrag (2026-09-19): once the selection survives
  // a filter, the visible list and the selection are independent, and a filter narrowing the
  // visible list to zero must not leave an empty, submittable "sichtbare Liste (0)" scope behind.
  describe('empty-scope guard (nachtrag 2026-09-19)', () => {
    it('preselects "selection" and disables the "visible" radio when the visible list is empty but the selection is not', () => {
      const dialog = render(defaultData({ rowCount: 0, selectionCount: 5 }));

      const [visible, selection] = dialog.scopeInputs();
      expect(visible.disabled).toBe(true);
      expect(visible.checked).toBe(false);
      expect(selection.disabled).toBe(false);
      expect(selection.checked).toBe(true);
      expect(dialog.rowCountText()).toBe('5 Zeilen');
    });

    it('submits the selection scope in that state, never the empty visible one', () => {
      const dialog = render(defaultData({ rowCount: 0, selectionCount: 5 }));

      dialog.button(SUBMIT).click();

      expect(closed).toEqual([{ optionId: 'csv', scope: 'selection' }]);
    });

    it('still prefers "visible" as the default once both scopes have rows', () => {
      const dialog = render(defaultData({ rowCount: 10, selectionCount: 5 }));

      const [visible, selection] = dialog.scopeInputs();
      expect(visible.disabled).toBe(false);
      expect(visible.checked).toBe(true);
      expect(selection.checked).toBe(false);
    });

    it('disables submit outright when the resolved scope has zero rows (defensive — the header button and the default above already keep this state out of reach)', () => {
      const dialog = render(defaultData({ rowCount: 0, selectionCount: 0 }));

      expect(dialog.button(SUBMIT).disabled).toBe(true);
    });
  });

  describe('submit / cancel', () => {
    it('closes with the chosen optionId and scope on submit', () => {
      const dialog = render(defaultData({ options: MIXED_OPTIONS, selectionCount: 4 }));

      dialog.optionInputs()[1].dispatchEvent(new Event('change'));
      dialog.detect();
      dialog.scopeInputs()[1].dispatchEvent(new Event('change'));
      dialog.detect();
      dialog.button(SUBMIT).click();

      expect(closed).toEqual([{ optionId: 'b', scope: 'selection' }]);
    });

    it('closes with undefined on cancel', () => {
      const dialog = render();

      dialog.button(CANCEL).click();

      expect(closed).toEqual([undefined]);
    });
  });

  describe('optionsLegendKey (caller-supplied legend, not a hardcoded format label)', () => {
    it('uses optionsLegendKey as the option group legend', () => {
      const dialog = render(defaultData({ optionsLegendKey: 'export.testLegend' }));

      expect(dialog.optionLegend()).toBe('Testlegende');
    });
  });
});
