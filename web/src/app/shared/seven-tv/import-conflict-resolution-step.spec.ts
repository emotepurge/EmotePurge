import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ImportRow } from '../../core/seven-tv/import-source';
import { ResolutionDecisions, RowDecision, Violation } from './conflict-resolution';
import {
  ConflictStepRow,
  ImportConflictResolutionStep,
  collisionStepRows,
  mismatchStepRows,
  violationMessages,
} from './import-conflict-resolution-step';
import { AliasMismatchRow, NameCollisionRow } from './import-preview';

// Only the keys this step translates — the real German texts, so an assertion reads as the
// sentence the user gets.
const DE_TRANSLATIONS = {
  import: {
    resolve: {
      listLabelCollisions: 'Namenskollisionen',
      listLabelMismatches: 'Abweichende Namen',
      rowLabel: '{{ source }}, im Ziel: {{ target }}',
      targetGone: 'nicht mehr im Zielset',
      aliaslessEntry: '+ ein Eintrag ohne Namen',
      actionGroupLabel: 'Aktion für {{ sourceName }}',
      action: {
        skip: 'Überspringen',
        renameSource: 'Umbenennen',
        replaceTarget: 'Ziel ersetzen',
        adoptSourceName: 'Namen übernehmen',
      },
      replaceNeedsTracked: 'Nur für getrackte Kanäle wiederherstellbar',
      reloadTargetFirst: 'Ziel hat sich geändert — erst neu laden',
      adoptBlocked: {
        nameTaken: 'Name im Zielset vergeben',
        duplicateTarget: 'im Ziel doppelt vorhanden',
      },
      renameLabel: 'Neuer Name',
      fieldError: {
        invalid: 'Diesen Namen nimmt 7TV nicht an.',
        taken: 'Dieser Name ist im Zielset schon vergeben.',
        duplicate: 'Diesen Namen erzeugt auch eine andere Zeile.',
      },
    },
  },
};

/** jsdom has no ResizeObserver, and the CDK viewport reads it during init too. */
class FakeResizeObserver {
  observe(): void {
    /* no-op */
  }
  unobserve(): void {
    /* no-op */
  }
  disconnect(): void {
    /* no-op */
  }
}

function importRow(id: string, name: string, imageUrl: string | null = null): ImportRow {
  return { sevenTvEmoteId: id, name, imageUrl };
}

function collision(
  id: string,
  name: string,
  overrides: Partial<NameCollisionRow> = {},
): NameCollisionRow {
  return {
    row: importRow(id, name),
    target: {
      sevenTvEmoteId: `target-${id}`,
      name,
      imageUrl: `https://cdn.7tv.app/emote/target-${id}/2x.webp`,
    },
    targetAliases: [name],
    targetHasAliaslessEntry: false,
    ...overrides,
  };
}

function mismatch(
  id: string,
  name: string,
  adoptBlocked: AliasMismatchRow['adoptBlocked'] = null,
): AliasMismatchRow {
  return { row: importRow(id, name), targetAliases: [`${name}Old`], adoptBlocked };
}

function optionKinds(row: ConflictStepRow): string[] {
  return row.options.map((option) => option.kind);
}

describe('ImportConflictResolutionStep', () => {
  describe('which actions a row offers (AK 6, 8, R5)', () => {
    it('offers skip, rename and replace for a name collision, and skip and adopt for a mismatch', () => {
      const [collisionRow] = collisionStepRows([collision('a', 'Kappa')], true, new Map());
      const [mismatchRow] = mismatchStepRows([mismatch('b', 'Pog')], new Map());

      expect(optionKinds(collisionRow)).toEqual(['skip', 'renameSource', 'replaceTarget']);
      expect(collisionRow.options.every((option) => option.disabledReasonKey === null)).toBe(true);
      expect(optionKinds(mismatchRow)).toEqual(['skip', 'adoptSourceName']);
      expect(mismatchRow.options.every((option) => option.disabledReasonKey === null)).toBe(true);
    });

    it('keeps replace listed but disabled with its reason for an untracked target or a target that is gone', () => {
      const [untracked] = collisionStepRows([collision('a', 'Kappa')], false, new Map());
      const [gone] = collisionStepRows([collision('a', 'Kappa')], true, new Map([['a', null]]));

      expect(untracked.options.find((o) => o.kind === 'replaceTarget')?.disabledReasonKey).toBe(
        'import.resolve.replaceNeedsTracked',
      );
      // Rename stays available: it deletes nothing.
      expect(untracked.options.find((o) => o.kind === 'renameSource')?.disabledReasonKey).toBe(
        null,
      );
      // The live counterpart laid over the row: gone, so no picture and nothing left to replace.
      expect(gone.targetGone).toBe(true);
      expect(gone.targetImageUrl).toBeNull();
      expect(gone.options.find((o) => o.kind === 'replaceTarget')?.disabledReasonKey).toBe(
        'import.resolve.reloadTargetFirst',
      );
    });
  });

  it('names every row a violation involves, one sentence per rule (AK 10, 11)', () => {
    const names = new Map([
      ['a', 'Kappa'],
      ['b', 'Pog'],
      ['c', 'Sadge'],
    ]);
    const violations: Violation[] = [
      { rule: 'duplicateGeneratedAlias', rowKeys: ['a', 'b'] },
      { rule: 'invalidTypedAlias', rowKeys: ['c'] },
      { rule: 'invalidTypedAlias', rowKeys: ['a'] },
    ];

    expect(violationMessages(violations, (key) => names.get(key) ?? key)).toEqual([
      { key: 'import.resolve.violation.duplicateGeneratedAlias', rows: 'Kappa, Pog' },
      { key: 'import.resolve.violation.invalidTypedAlias', rows: 'Sadge, Kappa' },
    ]);
  });

  describe('rendered', () => {
    let fixture: ComponentFixture<ImportConflictResolutionStep>;
    let host: HTMLElement;
    let decided: { key: string; decision: RowDecision }[];

    beforeEach(async () => {
      vi.stubGlobal('ResizeObserver', FakeResizeObserver);
      decided = [];
      await TestBed.configureTestingModule({
        imports: [
          ImportConflictResolutionStep,
          TranslocoTestingModule.forRoot({
            langs: { de: DE_TRANSLATIONS },
            translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
          }),
        ],
      }).compileComponents();
      await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
      fixture = TestBed.createComponent(ImportConflictResolutionStep);
      host = fixture.nativeElement;
      fixture.componentInstance.decide.subscribe((change) => decided.push(change));
    });

    afterEach(() => vi.unstubAllGlobals());

    /** jsdom has no layout: the viewport measures 0 px and renders the rows its buffer covers on an
     *  animation frame — let real frames pass until the first row is there. */
    async function render(
      group: 'nameCollision' | 'aliasMismatch',
      rows: ConflictStepRow[],
      decisions: ResolutionDecisions = new Map(),
      violations: Violation[] = [],
    ): Promise<void> {
      fixture.componentRef.setInput('group', group);
      fixture.componentRef.setInput('rows', rows);
      fixture.componentRef.setInput('decisions', decisions);
      fixture.componentRef.setInput('violations', violations);
      fixture.detectChanges();
      for (let attempt = 0; attempt < 50 && rowElements().length === 0; attempt++) {
        await new Promise((resolve) => setTimeout(resolve, 10));
        await fixture.whenStable();
        fixture.detectChanges();
      }
      expect(rowElements().length).toBeGreaterThan(0);
    }

    function rowElements(): HTMLElement[] {
      return Array.from(host.querySelectorAll<HTMLElement>('[data-resolve-index]'));
    }

    function actionGroup(sourceName: string): HTMLElement {
      const found = host.querySelector<HTMLElement>(
        `[role="radiogroup"][aria-label="Aktion für ${sourceName}"]`,
      );
      if (!found) {
        throw new Error(`no action group for ${sourceName}`);
      }
      return found;
    }

    function radio(sourceName: string, kind: RowDecision['kind']): HTMLInputElement {
      const found = actionGroup(sourceName).querySelector<HTMLInputElement>(
        `input[value="${kind}"]`,
      );
      if (!found) {
        throw new Error(`no ${kind} option for ${sourceName}`);
      }
      return found;
    }

    it('names each action group after its row and starts every row on skip (AK 5, 23)', async () => {
      await render('nameCollision', collisionStepRows([collision('a', 'Kappa')], true, new Map()));

      expect(radio('Kappa', 'skip').checked).toBe(true);
      expect(radio('Kappa', 'renameSource').checked).toBe(false);
      expect(radio('Kappa', 'replaceTarget').checked).toBe(false);
      // The row itself names both sides, so arrowing onto it says which conflict it is.
      expect(rowElements()[0].getAttribute('aria-label')).toBe('Kappa, im Ziel: Kappa');
    });

    it('shows a blocked adopt as a disabled option that says why (AK 8)', async () => {
      await render(
        'aliasMismatch',
        mismatchStepRows(
          [mismatch('a', 'Kappa', 'nameTaken'), mismatch('b', 'Pog', 'duplicateTarget')],
          new Map(),
        ),
      );

      expect(radio('Kappa', 'adoptSourceName').disabled).toBe(true);
      expect(actionGroup('Kappa').textContent).toContain('(Name im Zielset vergeben)');
      expect(radio('Pog', 'adoptSourceName').disabled).toBe(true);
      expect(actionGroup('Pog').textContent).toContain('(im Ziel doppelt vorhanden)');
      expect(radio('Pog', 'skip').disabled).toBe(false);
    });

    it('opens a prefilled rename field and shows its field error for an alias 7TV rejects (AK 10)', async () => {
      const rows = collisionStepRows([collision('a', 'Kappa')], true, new Map());
      await render('nameCollision', rows);

      radio('Kappa', 'renameSource').click();
      // Prefilled with the source name — the user types the new one over it.
      expect(decided).toEqual([{ key: 'a', decision: { kind: 'renameSource', alias: 'Kappa' } }]);

      fixture.componentRef.setInput(
        'decisions',
        new Map([['a', { kind: 'renameSource', alias: 'Kappa neu' }]]),
      );
      fixture.componentRef.setInput('violations', [
        { rule: 'invalidTypedAlias', rowKeys: ['a'] },
      ] satisfies Violation[]);
      fixture.detectChanges();

      const field = host.querySelector<HTMLInputElement>('#resolve-alias-a');
      expect(field?.value).toBe('Kappa neu');
      expect(field?.getAttribute('aria-invalid')).toBe('true');
      const errorId = field?.getAttribute('aria-describedby') ?? '';
      expect(host.querySelector(`#${errorId}`)?.textContent).toContain(
        'Diesen Namen nimmt 7TV nicht an.',
      );

      field!.value = 'KappaNeu';
      field!.dispatchEvent(new Event('input'));
      expect(decided.at(-1)).toEqual({
        key: 'a',
        decision: { kind: 'renameSource', alias: 'KappaNeu' },
      });
    });

    it('draws the empty plate without a picture for a row without an image url (AK 4)', async () => {
      const rows = collisionStepRows(
        [collision('a', 'Kappa', { row: importRow('a', 'Kappa', null) })],
        true,
        new Map(),
      );
      await render('nameCollision', rows);

      const plates = rowElements()[0].querySelectorAll('.app-sprite-cell');
      expect(plates).toHaveLength(2);
      // No request to a URL derived from the id — the source plate has no <img> at all.
      expect(plates[0].querySelector('img')).toBeNull();
      expect(plates[1].querySelector('img')?.getAttribute('src')).toContain('target-a');
    });

    it('moves between rows with the arrow keys and scrolls a row outside the buffer into view first', async () => {
      const many = Array.from({ length: 200 }, (_, index) =>
        collision(`r${index}`, `Emote${index}`),
      );
      await render('nameCollision', collisionStepRows(many, true, new Map()));
      const viewport = fixture.debugElement.query(By.directive(CdkVirtualScrollViewport))
        .componentInstance as CdkVirtualScrollViewport;
      // jsdom has no Element.scrollTo — only the call itself is the subject here.
      const scrollToIndex = vi.spyOn(viewport, 'scrollToIndex').mockImplementation(() => undefined);

      const first = rowElements()[0];
      expect(first.tabIndex).toBe(0);
      first.focus();
      first.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }));
      fixture.detectChanges();

      // One tab stop across the rows, and it moved with the focus.
      const second = host.querySelector<HTMLElement>('[data-resolve-index="1"]');
      expect(document.activeElement).toBe(second);
      expect(second?.tabIndex).toBe(0);
      expect(first.tabIndex).toBe(-1);

      // Row 199 is far outside the rendered buffer: not in the DOM, so Tab could never reach it.
      expect(host.querySelector('[data-resolve-index="199"]')).toBeNull();
      second!.dispatchEvent(new KeyboardEvent('keydown', { key: 'End', bubbles: true }));
      expect(scrollToIndex).toHaveBeenCalledWith(199);
    });
  });
});
