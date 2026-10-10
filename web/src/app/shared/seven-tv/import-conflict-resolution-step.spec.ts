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
  RowConsequence,
  collisionStepRows,
  mismatchStepRows,
  rowConsequence,
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
      rowLabel: 'Quelle: {{ source }}, Ziel: {{ target }}',
      header: {
        source: 'Quelle',
        target: 'Ziel · {{ set }}',
        targetPlain: 'Ziel',
        action: 'Aktion',
      },
      consequence: {
        removed: 'wird entfernt',
        becomes: 'wird zu „{{ name }}“',
        addedAs: 'wird als „{{ name }}“ hinzugefügt',
        kept: 'wird behalten',
      },
      targetGone: 'nicht mehr im Zielset',
      aliaslessEntry: '+ ein Eintrag ohne Namen',
      actionGroupLabel: 'Aktion für {{ sourceName }}',
      action: {
        skip: 'Überspringen',
        renameSource: 'Umbenennen',
        replaceTarget: 'Ziel ersetzen',
        adoptSourceName: 'Namen übernehmen',
      },
      reloadTargetFirst: 'Ziel hat sich geändert — erst neu laden',
      adoptBlocked: {
        nameTaken: 'Name im Zielset vergeben',
        duplicateTarget: 'im Ziel doppelt vorhanden',
      },
      bulk: {
        label: 'Für alle:',
        skipAll: 'Alle überspringen',
        replaceAll: 'Alle ersetzen',
        done: '{{ count }} auf „{{ action }}“ gesetzt',
        renamesKept: {
          one: '{{ count }} Umbenennung bleibt',
          other: '{{ count }} Umbenennungen bleiben',
        },
        replaceUnavailable: {
          one: '{{ count }} nicht ersetzbar, Ziel hat sich geändert',
          other: '{{ count }} nicht ersetzbar, Ziel hat sich geändert',
        },
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

/** A list long enough that its last row starts outside the viewport's rendered buffer (jsdom has
 *  no layout, so the buffer is the first few rows), yet short enough to render in full quickly
 *  once a case grows the viewport to fit it. */
const FAR_ROW_COUNT = 12;

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
      const [collisionRow] = collisionStepRows([collision('a', 'Kappa')], new Map());
      const [mismatchRow] = mismatchStepRows([mismatch('b', 'Pog')], new Map());

      expect(optionKinds(collisionRow)).toEqual(['skip', 'renameSource', 'replaceTarget']);
      expect(collisionRow.options.every((option) => option.disabledReasonKey === null)).toBe(true);
      expect(optionKinds(mismatchRow)).toEqual(['skip', 'adoptSourceName']);
      expect(mismatchRow.options.every((option) => option.disabledReasonKey === null)).toBe(true);
    });

    // #253, spec 4.5 point 15/6.6: the replace lock for an untracked target is gone —
    // `collisionStepRows` no longer takes a middle "is the target tracked" parameter at all, and
    // replace is offered exactly the same for every target (AK 16).
    it('offers replace enabled for an untracked target the same as for a tracked one', () => {
      const [row] = collisionStepRows([collision('a', 'Kappa')], new Map());

      expect(row.options.find((o) => o.kind === 'replaceTarget')?.disabledReasonKey).toBe(null);
      expect(row.options.find((o) => o.kind === 'renameSource')?.disabledReasonKey).toBe(null);
    });

    it('keeps replace listed but disabled with its reason for a target that is gone', () => {
      const [gone] = collisionStepRows([collision('a', 'Kappa')], new Map([['a', null]]));

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

  describe('rowConsequence (issue #268, AK 2)', () => {
    it('names the target as kept for skip', () => {
      expect(rowConsequence({ kind: 'skip' }, 'Kappa')).toEqual({
        side: 'target',
        kind: 'kept',
        name: '',
      } satisfies RowConsequence);
    });

    it('names nothing for skip once the target is already gone — nothing there to keep', () => {
      expect(rowConsequence({ kind: 'skip' }, 'Kappa', false, true)).toBeNull();
    });

    it('names the target as removed for replaceTarget', () => {
      expect(rowConsequence({ kind: 'replaceTarget' }, 'Kappa')).toEqual({
        side: 'target',
        kind: 'removed',
        name: '',
      } satisfies RowConsequence);
    });

    it('names the target as becoming the source name for adoptSourceName', () => {
      expect(rowConsequence({ kind: 'adoptSourceName' }, 'Kappa')).toEqual({
        side: 'target',
        kind: 'becomes',
        name: 'Kappa',
      } satisfies RowConsequence);
    });

    it('names the source as added under the typed alias for renameSource, following each keystroke', () => {
      expect(rowConsequence({ kind: 'renameSource', alias: 'KappaNeu' }, 'Kappa')).toEqual({
        side: 'source',
        kind: 'addedAs',
        name: 'KappaNeu',
      } satisfies RowConsequence);
      // A field the user has cleared names nothing rather than an empty quote.
      expect(rowConsequence({ kind: 'renameSource', alias: '' }, 'Kappa')).toBeNull();
    });

    it('names nothing for renameSource while the typed alias has a field error, even though it is not empty (issue #269)', () => {
      expect(
        rowConsequence({ kind: 'renameSource', alias: 'KappaTaken' }, 'Kappa', true),
      ).toBeNull();
      // Once the error is gone, the same alias names the consequence again.
      expect(rowConsequence({ kind: 'renameSource', alias: 'KappaTaken' }, 'Kappa', false)).toEqual(
        {
          side: 'source',
          kind: 'addedAs',
          name: 'KappaTaken',
        } satisfies RowConsequence,
      );
    });
  });

  describe('rendered', () => {
    let fixture: ComponentFixture<ImportConflictResolutionStep>;
    let host: HTMLElement;
    let decided: { key: string; decision: RowDecision }[];
    let bulked: ReadonlyMap<string, RowDecision>[];

    beforeEach(async () => {
      vi.stubGlobal('ResizeObserver', FakeResizeObserver);
      decided = [];
      bulked = [];
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
      fixture.componentInstance.decideMany.subscribe((changes) => bulked.push(changes));
    });

    afterEach(() => vi.unstubAllGlobals());

    /** jsdom has no layout: the viewport measures 0 px and renders only the rows its buffer covers,
     *  a render pass after this first one. */
    async function render(
      group: 'nameCollision' | 'aliasMismatch',
      rows: ConflictStepRow[],
      decisions: ResolutionDecisions = new Map(),
      violations: Violation[] = [],
      targetSetName: string | null = null,
    ): Promise<void> {
      fixture.componentRef.setInput('group', group);
      fixture.componentRef.setInput('rows', rows);
      fixture.componentRef.setInput('decisions', decisions);
      fixture.componentRef.setInput('violations', violations);
      fixture.componentRef.setInput('targetSetName', targetSetName);
      fixture.detectChanges();
      await flushRenders();
      expect(rowElements().length).toBeGreaterThan(0);
    }

    function rowElements(): HTMLElement[] {
      return Array.from(host.querySelectorAll<HTMLElement>('[data-resolve-index]'));
    }

    function rowAt(index: number): HTMLElement | null {
      return host.querySelector<HTMLElement>(`[data-resolve-index="${index}"]`);
    }

    function keydown(row: HTMLElement, key: string): void {
      row.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
    }

    function viewportOf(): CdkVirtualScrollViewport {
      return fixture.debugElement.query(By.directive(CdkVirtualScrollViewport))
        .componentInstance as CdkVirtualScrollViewport;
    }

    /** jsdom has no layout and no `Element.scrollTo`, so the viewport never scrolls by itself.
     *  This stands in for the browser: `scrollToIndex` moves a simulated scroll offset onto the row
     *  and has the viewport recompute its rendered range from it at once. Returns the spy.
     *
     *  Deliberately not a `scroll` event: the viewport audits those on rxjs's
     *  `animationFrameScheduler`, a module-wide singleton. The unit-test builder runs spec files
     *  non-isolated, so an earlier file whose fake timers ended with a frame still queued there
     *  leaves that scheduler waiting on a frame id that never fires — and the viewport ignores
     *  every later scroll event in that worker (seen in CI: row 199 never rendered). */
    function simulateScrolling(viewport: CdkVirtualScrollViewport) {
      const rowHeight = parseFloat(rowElements()[0].style.height);
      let offset = 0;
      vi.spyOn(viewport, 'measureScrollOffset').mockImplementation(() => offset);
      return vi.spyOn(viewport, 'scrollToIndex').mockImplementation((index) => {
        offset = index * rowHeight;
        viewport.checkViewportSize();
      });
    }

    /** Stands in for the viewport growing tall enough to render every row at once — the only way,
     *  without layout, to have a far row and the first rows in the DOM together. */
    function growViewportToFit(viewport: CdkVirtualScrollViewport, rowCount: number): void {
      const rowHeight = parseFloat(rowElements()[0].style.height);
      vi.spyOn(viewport, 'measureViewportSize').mockReturnValue(rowCount * rowHeight);
      viewport.checkViewportSize();
    }

    /** Runs the render pass the step still owes — rows the viewport has put in range, and whatever
     *  focus target lands in the pass that renders its row. No real time passes and no animation
     *  frame is needed: the viewport schedules its change detection through a chain of microtasks,
     *  which one zero-delay task drains, and `whenStable` then waits for the render pass that
     *  chain scheduled. Once it resolves, nothing else is pending, so it also stands for "nothing
     *  more happens". */
    async function flushRenders(): Promise<void> {
      await new Promise((resolve) => setTimeout(resolve));
      await fixture.whenStable();
      fixture.detectChanges();
    }

    /** The reserved consequence line for one row and side — always present, whether or not it
     *  currently holds text (issue #268 AK 3). */
    function consequenceEl(key: string, side: 'source' | 'target'): HTMLElement {
      const found = host.querySelector<HTMLElement>(`#resolve-consequence-${key}-${side}`);
      if (!found) {
        throw new Error(`no ${side} consequence line for ${key}`);
      }
      return found;
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
      await render('nameCollision', collisionStepRows([collision('a', 'Kappa')], new Map()));

      expect(radio('Kappa', 'skip').checked).toBe(true);
      expect(radio('Kappa', 'renameSource').checked).toBe(false);
      expect(radio('Kappa', 'replaceTarget').checked).toBe(false);
      // The row itself names both sides, so arrowing onto it says which conflict it is.
      expect(rowElements()[0].getAttribute('aria-label')).toBe('Quelle: Kappa, Ziel: Kappa');
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

    it('opens a prefilled rename field and shows its field error once touched, for an alias 7TV rejects (AK 10)', async () => {
      const rows = collisionStepRows([collision('a', 'Kappa')], new Map());
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

      // Editing the field is what touches it — this also drives it to the value under test.
      let field = host.querySelector<HTMLInputElement>('#resolve-alias-a');
      field!.value = 'Kappa neu';
      field!.dispatchEvent(new Event('input'));
      fixture.detectChanges();

      field = host.querySelector<HTMLInputElement>('#resolve-alias-a');
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

    describe('field error only once touched (issue #269, §5.3)', () => {
      it('shows no field error and no aria-invalid right after choosing rename, even though the prefilled alias is already taken', async () => {
        const rows = collisionStepRows([collision('a', 'Kappa')], new Map());
        await render('nameCollision', rows);

        radio('Kappa', 'renameSource').click();
        fixture.componentRef.setInput(
          'decisions',
          new Map([['a', { kind: 'renameSource', alias: 'Kappa' }]]),
        );
        // The prefilled alias equals the collision's own name — always taken — so a violation
        // exists from the first render, before the user has done anything with the field.
        fixture.componentRef.setInput('violations', [
          { rule: 'aliasHeldByTarget', rowKeys: ['a'] },
        ] satisfies Violation[]);
        fixture.detectChanges();

        const field = host.querySelector<HTMLInputElement>('#resolve-alias-a');
        expect(field?.getAttribute('aria-invalid')).toBeNull();
        expect(field?.getAttribute('aria-describedby')).toBeNull();
        expect(host.querySelector('#resolve-alias-a-error')).toBeNull();
      });

      it('shows the field error once the field is touched by leaving it (blur), still on the same taken alias', async () => {
        const rows = collisionStepRows([collision('a', 'Kappa')], new Map());
        await render('nameCollision', rows);

        radio('Kappa', 'renameSource').click();
        fixture.componentRef.setInput(
          'decisions',
          new Map([['a', { kind: 'renameSource', alias: 'Kappa' }]]),
        );
        fixture.componentRef.setInput('violations', [
          { rule: 'aliasHeldByTarget', rowKeys: ['a'] },
        ] satisfies Violation[]);
        fixture.detectChanges();

        host
          .querySelector<HTMLInputElement>('#resolve-alias-a')!
          .dispatchEvent(new FocusEvent('blur'));
        fixture.detectChanges();

        const field = host.querySelector<HTMLInputElement>('#resolve-alias-a');
        expect(field?.getAttribute('aria-invalid')).toBe('true');
        expect(host.querySelector('#resolve-alias-a-error')?.textContent).toContain(
          'Dieser Name ist im Zielset schon vergeben.',
        );
      });

      it('keeps the row touched after switching its action away and back', async () => {
        const rows = collisionStepRows([collision('a', 'Kappa')], new Map());
        await render('nameCollision', rows);

        radio('Kappa', 'renameSource').click();
        fixture.componentRef.setInput(
          'decisions',
          new Map([['a', { kind: 'renameSource', alias: 'Kappa' }]]),
        );
        fixture.detectChanges();
        let field = host.querySelector<HTMLInputElement>('#resolve-alias-a')!;
        field.value = 'Kappa neu';
        field.dispatchEvent(new Event('input'));
        fixture.componentRef.setInput(
          'decisions',
          new Map([['a', { kind: 'renameSource', alias: 'Kappa neu' }]]),
        );
        fixture.componentRef.setInput('violations', [
          { rule: 'invalidTypedAlias', rowKeys: ['a'] },
        ] satisfies Violation[]);
        fixture.detectChanges();
        expect(field.getAttribute('aria-invalid')).toBe('true');

        radio('Kappa', 'renameSource').click();
        fixture.componentRef.setInput('decisions', new Map([['a', { kind: 'skip' }]]));
        fixture.detectChanges();

        radio('Kappa', 'renameSource').click();
        fixture.componentRef.setInput(
          'decisions',
          new Map([['a', { kind: 'renameSource', alias: 'Kappa neu' }]]),
        );
        fixture.detectChanges();

        field = host.querySelector<HTMLInputElement>('#resolve-alias-a')!;
        expect(field.getAttribute('aria-invalid')).toBe('true');
      });
    });

    it('draws the empty plate without a picture for a row without an image url (AK 4)', async () => {
      const rows = collisionStepRows(
        [collision('a', 'Kappa', { row: importRow('a', 'Kappa', null) })],
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
      await render('nameCollision', collisionStepRows(many, new Map()));
      const scrollToIndex = simulateScrolling(viewportOf());

      const first = rowElements()[0];
      expect(first.tabIndex).toBe(0);
      first.focus();
      keydown(first, 'ArrowDown');
      fixture.detectChanges();

      // One tab stop across the rows, and it moved with the focus.
      const second = rowAt(1);
      expect(document.activeElement).toBe(second);
      expect(second?.tabIndex).toBe(0);
      expect(first.tabIndex).toBe(-1);

      // Row 199 is far outside the rendered buffer: not in the DOM, so Tab could never reach it.
      expect(rowAt(199)).toBeNull();
      keydown(second!, 'End');
      expect(scrollToIndex).toHaveBeenCalledWith(199);

      // Focus follows once the row is there — it must not stay behind on a row scrolled away.
      await flushRenders();
      expect(document.activeElement).toBe(rowAt(199));
      expect(rowAt(199)?.tabIndex).toBe(0);
    });

    it('lets a newer focus target win over an older one still waiting for its row', async () => {
      // Few enough rows that growing the viewport renders them all quickly, enough that the last
      // one starts outside the rendered buffer.
      const many = Array.from({ length: FAR_ROW_COUNT }, (_, index) =>
        collision(`r${index}`, `Emote${index}`),
      );
      const last = many.length - 1;
      await render('nameCollision', collisionStepRows(many, new Map()));
      const viewport = viewportOf();
      // The scroll is held back here, so End's row stays unrendered until the test says so.
      vi.spyOn(viewport, 'scrollToIndex').mockImplementation(() => undefined);
      // The initial hand-off has landed already, so it cannot land later and muddle the outcome.
      expect(document.activeElement).toBe(rowAt(0));
      const first = rowAt(0)!;
      expect(rowAt(last)).toBeNull();

      keydown(first, 'End');
      // Pressed before the last row ever rendered: row 1 is right there and takes focus at once.
      keydown(first, 'ArrowDown');
      expect(document.activeElement).toBe(rowAt(1));

      // The last row reaching the DOM only now must not pull focus back to End's outdated target.
      growViewportToFit(viewport, many.length);
      await flushRenders();
      expect(rowAt(last)).not.toBeNull();
      // One more pass: an outdated target would have had its chance to land by now.
      await flushRenders();
      expect(document.activeElement).toBe(rowAt(1));
      expect(rowAt(1)?.tabIndex).toBe(0);
    });

    it('focuses nothing and throws nothing when destroyed before its first row renders', async () => {
      (document.activeElement as HTMLElement | null)?.blur();
      fixture.componentRef.setInput('group', 'nameCollision');
      fixture.componentRef.setInput(
        'rows',
        collisionStepRows([collision('a', 'Kappa'), collision('b', 'Pog')], new Map()),
      );
      fixture.componentRef.setInput('decisions', new Map());
      fixture.detectChanges();
      // The viewport renders its first rows only after this first pass — the hand-off is still owed.
      expect(rowElements()).toHaveLength(0);

      fixture.destroy();
      await new Promise((resolve) => setTimeout(resolve, 60));

      expect(document.activeElement).toBe(document.body);
    });

    it('still hands focus to the first row when focus sits on a focusable ancestor of the step', async () => {
      // Stands in for the dialog's own tabindex="-1" container: on macOS Safari/Firefox a click on
      // the button that opens this step focuses that container instead of the button.
      const wrapper = document.createElement('div');
      wrapper.tabIndex = -1;
      host.parentElement!.insertBefore(wrapper, host);
      wrapper.appendChild(host);
      try {
        fixture.componentRef.setInput('group', 'nameCollision');
        fixture.componentRef.setInput(
          'rows',
          collisionStepRows([collision('a', 'Kappa'), collision('b', 'Pog')], new Map()),
        );
        fixture.componentRef.setInput('decisions', new Map());
        fixture.detectChanges();
        expect(rowElements()).toHaveLength(0);

        wrapper.focus();
        expect(document.activeElement).toBe(wrapper);
        await flushRenders();

        expect(document.activeElement).toBe(rowAt(0));
      } finally {
        // TestBed teardown only removes the root host, not this stand-in ancestor — unwrap it
        // so the empty wrapper doesn't linger in `body` for the rest of the file's tests.
        wrapper.parentElement?.insertBefore(host, wrapper);
        wrapper.remove();
      }
    });

    it('leaves focus alone once the user moves it outside the step before the hand-off lands', async () => {
      const outside = document.createElement('button');
      document.body.appendChild(outside);
      try {
        (document.activeElement as HTMLElement | null)?.blur();
        fixture.componentRef.setInput('group', 'nameCollision');
        fixture.componentRef.setInput(
          'rows',
          collisionStepRows([collision('a', 'Kappa'), collision('b', 'Pog')], new Map()),
        );
        fixture.componentRef.setInput('decisions', new Map());
        fixture.detectChanges();
        expect(rowElements()).toHaveLength(0);

        outside.focus();
        await flushRenders();
        expect(rowElements().length).toBeGreaterThan(0);
        await flushRenders();

        expect(document.activeElement).toBe(outside);
      } finally {
        outside.remove();
      }
    });

    describe('consequence line (issue #268)', () => {
      it('names the target kept while a row is on skip, and leaves the source line empty', async () => {
        await render('nameCollision', collisionStepRows([collision('a', 'Kappa')], new Map()));

        expect(consequenceEl('a', 'source').textContent?.trim()).toBe('');
        expect(consequenceEl('a', 'target').textContent?.trim()).toBe('wird behalten');
      });

      it('leaves both consequence lines empty on skip once the target is already gone — nothing there to keep', async () => {
        await render(
          'nameCollision',
          collisionStepRows([collision('a', 'Kappa')], new Map([['a', null]])),
        );

        expect(consequenceEl('a', 'source').textContent?.trim()).toBe('');
        expect(consequenceEl('a', 'target').textContent?.trim()).toBe('');
      });

      it('shows the target as removed once replaceTarget is chosen', async () => {
        await render(
          'nameCollision',
          collisionStepRows([collision('a', 'Kappa')], new Map()),
          new Map([['a', { kind: 'replaceTarget' }]]),
        );

        expect(consequenceEl('a', 'target').textContent?.trim()).toBe('wird entfernt');
        // Choosing replace says nothing about the source side.
        expect(consequenceEl('a', 'source').textContent?.trim()).toBe('');
      });

      it('shows the target becoming the source name once adoptSourceName is chosen', async () => {
        await render(
          'aliasMismatch',
          mismatchStepRows([mismatch('b', 'Pog')], new Map()),
          new Map([['b', { kind: 'adoptSourceName' }]]),
        );

        expect(consequenceEl('b', 'target').textContent?.trim()).toBe('wird zu „Pog“');
      });

      it('shows the typed alias under the source once renameSource is chosen, and follows further typing', async () => {
        const rows = collisionStepRows([collision('a', 'Kappa')], new Map());
        await render('nameCollision', rows);

        radio('Kappa', 'renameSource').click();
        // The step re-renders once the decision it just emitted comes back through its own
        // `decisions` input — same round trip the confirm dialog performs for real, simulated here
        // since this fixture has no dialog wiring the output back to the input.
        fixture.componentRef.setInput('decisions', new Map([['a', decided.at(-1)!.decision]]));
        fixture.detectChanges();
        expect(consequenceEl('a', 'source').textContent?.trim()).toBe(
          'wird als „Kappa“ hinzugefügt',
        );

        const field = host.querySelector<HTMLInputElement>('#resolve-alias-a')!;
        field.value = 'KappaNeu';
        field.dispatchEvent(new Event('input'));
        fixture.componentRef.setInput('decisions', new Map([['a', decided.at(-1)!.decision]]));
        fixture.detectChanges();
        expect(consequenceEl('a', 'source').textContent?.trim()).toBe(
          'wird als „KappaNeu“ hinzugefügt',
        );

        field.value = '';
        field.dispatchEvent(new Event('input'));
        fixture.componentRef.setInput('decisions', new Map([['a', decided.at(-1)!.decision]]));
        fixture.detectChanges();
        // An emptied field names nothing rather than an empty quote.
        expect(consequenceEl('a', 'source').textContent?.trim()).toBe('');
      });

      it('shows no consequence line while the typed alias has a field error, and shows it again once valid (issue #269)', async () => {
        const rows = collisionStepRows([collision('a', 'Kappa')], new Map());
        await render(
          'nameCollision',
          rows,
          new Map([['a', { kind: 'renameSource', alias: 'Taken' }]]),
          [{ rule: 'aliasHeldByTarget', rowKeys: ['a'] }],
        );

        // A taken alias will not actually be added — the reserved line stays empty rather than
        // promising a name the run cannot keep.
        expect(consequenceEl('a', 'source').textContent?.trim()).toBe('');

        // Once the run no longer sees a violation for the row (the user typed a free name), the
        // same alias names the consequence again.
        fixture.componentRef.setInput('violations', []);
        fixture.detectChanges();
        expect(consequenceEl('a', 'source').textContent?.trim()).toBe(
          'wird als „Taken“ hinzugefügt',
        );
      });

      it("names each row's consequence lines from its radiogroup via aria-describedby", async () => {
        await render(
          'nameCollision',
          collisionStepRows([collision('a', 'Kappa')], new Map()),
          new Map([['a', { kind: 'replaceTarget' }]]),
        );

        const describedBy = actionGroup('Kappa').getAttribute('aria-describedby') ?? '';
        const ids = describedBy.split(/\s+/).filter((id) => id.length > 0);
        expect(ids).toContain('resolve-consequence-a-source');
        expect(ids).toContain('resolve-consequence-a-target');
        // Every id it names actually resolves to an element in the row, and that element carries
        // the consequence text the chosen action produced.
        const described = ids.map((id) => host.querySelector(`#${id}`));
        expect(described.every((el) => el !== null)).toBe(true);
        expect(described.some((el) => el?.textContent?.trim() === 'wird entfernt')).toBe(true);
      });
    });

    describe('"for all" line', () => {
      function bulkButton(name: string): HTMLButtonElement {
        const found = [...host.querySelectorAll<HTMLButtonElement>('button')].find(
          (button) => button.textContent?.trim() === name,
        );
        if (!found) {
          throw new Error(`no "${name}" button`);
        }
        return found;
      }

      function statusText(): string {
        return host.querySelector('[role="status"]')?.textContent?.trim() ?? '';
      }

      function twoCollisions(): ConflictStepRow[] {
        return collisionStepRows([collision('a', 'Kappa'), collision('b', 'Pog')], new Map());
      }

      it('is absent with a single row', async () => {
        await render('nameCollision', collisionStepRows([collision('a', 'Kappa')], new Map()));
        expect(host.querySelector('button')).toBeNull();
      });

      it('is absent for the alias-mismatch group', async () => {
        await render(
          'aliasMismatch',
          mismatchStepRows([mismatch('a', 'Kappa'), mismatch('b', 'Pog')], new Map()),
        );
        expect(host.querySelector('button')).toBeNull();
      });

      it('emits one event carrying every changed row on "Alle ersetzen"', async () => {
        await render('nameCollision', twoCollisions());

        bulkButton('Alle ersetzen').click();

        expect(bulked).toEqual([
          new Map<string, RowDecision>([
            ['a', { kind: 'replaceTarget' }],
            ['b', { kind: 'replaceTarget' }],
          ]),
        ]);
        expect(decided).toEqual([]);
      });

      it('leaves renames and unreplaceable rows alone, and says so', async () => {
        const rows = collisionStepRows(
          [
            collision('a', 'Kappa'),
            collision('b', 'Pog'),
            collision('c', 'Sadge'),
            collision('d', 'Lul'),
          ],
          // Row d's live target no longer holds the name: replace is disabled there.
          new Map([['d', { aliases: ['Other'], hasAliaslessEntry: false }]]),
        );
        await render(
          'nameCollision',
          rows,
          new Map<string, RowDecision>([['b', { kind: 'renameSource', alias: 'Pog2' }]]),
        );

        bulkButton('Alle ersetzen').click();
        fixture.detectChanges();

        expect(bulked).toEqual([
          new Map<string, RowDecision>([
            ['a', { kind: 'replaceTarget' }],
            ['c', { kind: 'replaceTarget' }],
          ]),
        ]);
        expect(statusText()).toBe(
          '2 auf „Ziel ersetzen“ gesetzt · 1 Umbenennung bleibt · 1 nicht ersetzbar, Ziel hat sich geändert',
        );
      });

      it('announces the result in a polite live region and clears it on a single-row change', async () => {
        await render('nameCollision', twoCollisions());
        const region = host.querySelector('[role="status"]');
        expect(region).not.toBeNull();
        expect(statusText()).toBe('');

        bulkButton('Alle ersetzen').click();
        fixture.detectChanges();
        expect(host.querySelector('[role="status"]')).toBe(region);
        expect(statusText()).toBe('2 auf „Ziel ersetzen“ gesetzt');

        radio('Kappa', 'renameSource').click();
        fixture.detectChanges();
        expect(statusText()).toBe('');
      });

      it('disables a button that would change nothing', async () => {
        await render('nameCollision', twoCollisions());
        expect(bulkButton('Alle überspringen').disabled).toBe(true);
        expect(bulkButton('Alle ersetzen').disabled).toBe(false);

        const replaced = new Map<string, RowDecision>([
          ['a', { kind: 'replaceTarget' }],
          ['b', { kind: 'replaceTarget' }],
        ]);
        fixture.componentRef.setInput('decisions', replaced);
        fixture.detectChanges();

        expect(bulkButton('Alle überspringen').disabled).toBe(false);
        expect(bulkButton('Alle ersetzen').disabled).toBe(true);
      });
    });

    describe('target set name caption (issue #268)', () => {
      it('names the target set in the stacked layout caption', async () => {
        // jsdom never lays out `#container`, so `narrow()` reads true in every rendered test here —
        // the stacked layout's own per-cell caption is what this exercises; the wide header row's
        // rendering is checked visually instead (docs/plans, live Playwright screenshot).
        await render(
          'nameCollision',
          collisionStepRows([collision('a', 'Kappa')], new Map()),
          new Map(),
          [],
          'Vault',
        );

        expect(rowElements()[0].textContent).toContain('Ziel · Vault');
      });

      it('falls back to the plain "Ziel" caption without a target set name', async () => {
        await render('nameCollision', collisionStepRows([collision('a', 'Kappa')], new Map()));

        const targetCell = rowElements()[0];
        expect(targetCell.textContent).toContain('Ziel');
        expect(targetCell.textContent).not.toContain('Ziel ·');
      });
    });

    describe('active-row animation only (Codex P2 on #269, docs §113)', () => {
      /** A row with BOTH cells carrying a real (non-null) image url — `collision`'s own default
       *  leaves the source one `null` (AK 4 is a different concern), which would only ever exercise
       *  one of the two cells here. */
      function animatedRow(id: string, name: string): NameCollisionRow {
        return collision(id, name, {
          row: importRow(id, name, `https://cdn.7tv.app/emote/${id}/4x_static.webp`),
        });
      }

      function animatedCellCount(row: Element): number {
        return row.querySelectorAll('app-emote-sprite-animated').length;
      }

      /** The step's own initial "hand focus to the table" must have landed on row 0 before this
       *  describe's own hover/focus interactions, or it could land after them and reclaim focus
       *  mid-test. It lands in the pass that renders row 0, which `render` has already run. */
      function expectInitialFocusOnFirstRow(): void {
        expect(document.activeElement).toBe(rowElements()[0]);
      }

      it('hands focus to the first row on open without animating it, and animates nothing until a row is hovered or focused', async () => {
        await render(
          'nameCollision',
          collisionStepRows([animatedRow('a', 'Kappa'), animatedRow('b', 'Pog')], new Map()),
        );
        expectInitialFocusOnFirstRow();

        // Opening is not the reader settling on row 0 to compare it (WCAG 2.4.3 hand-off only).
        expect(document.activeElement).toBe(rowElements()[0]);
        expect(host.querySelectorAll('app-emote-sprite-animated')).toHaveLength(0);
      });

      it('hovering a row animates only that row, both its cells together', async () => {
        await render(
          'nameCollision',
          collisionStepRows([animatedRow('a', 'Kappa'), animatedRow('b', 'Pog')], new Map()),
        );
        expectInitialFocusOnFirstRow();

        rowElements()[0].dispatchEvent(new MouseEvent('mouseenter'));
        fixture.detectChanges();

        expect(animatedCellCount(rowElements()[0])).toBe(2);
        expect(animatedCellCount(rowElements()[1])).toBe(0);
      });

      it('moving the pointer to another row moves the animation with it', async () => {
        await render(
          'nameCollision',
          collisionStepRows([animatedRow('a', 'Kappa'), animatedRow('b', 'Pog')], new Map()),
        );
        expectInitialFocusOnFirstRow();

        rowElements()[0].dispatchEvent(new MouseEvent('mouseenter'));
        fixture.detectChanges();
        rowElements()[0].dispatchEvent(new MouseEvent('mouseleave'));
        rowElements()[1].dispatchEvent(new MouseEvent('mouseenter'));
        fixture.detectChanges();

        expect(animatedCellCount(rowElements()[0])).toBe(0);
        expect(animatedCellCount(rowElements()[1])).toBe(2);
      });

      it('falls back to the focused row once the pointer leaves', async () => {
        await render(
          'nameCollision',
          collisionStepRows([animatedRow('a', 'Kappa'), animatedRow('b', 'Pog')], new Map()),
        );
        expectInitialFocusOnFirstRow();

        rowElements()[1].focus();
        fixture.detectChanges();
        expect(animatedCellCount(rowElements()[1])).toBe(2);

        // The pointer on a different row wins over the still-focused one.
        rowElements()[0].dispatchEvent(new MouseEvent('mouseenter'));
        fixture.detectChanges();
        expect(animatedCellCount(rowElements()[0])).toBe(2);
        expect(animatedCellCount(rowElements()[1])).toBe(0);

        // Leaving it hands playback back to the row that still holds keyboard focus.
        rowElements()[0].dispatchEvent(new MouseEvent('mouseleave'));
        fixture.detectChanges();
        expect(animatedCellCount(rowElements()[0])).toBe(0);
        expect(animatedCellCount(rowElements()[1])).toBe(2);
      });

      it('keeps focus on a row the user focuses while a keyboard target still waits, and animates it', async () => {
        const many = Array.from({ length: FAR_ROW_COUNT }, (_, index) =>
          animatedRow(`r${index}`, `Emote${index}`),
        );
        const last = many.length - 1;
        await render('nameCollision', collisionStepRows(many, new Map()));
        expectInitialFocusOnFirstRow();
        const viewport = viewportOf();
        vi.spyOn(viewport, 'scrollToIndex').mockImplementation(() => undefined);
        expect(rowAt(last)).toBeNull();

        keydown(rowAt(0)!, 'End');
        // The user's own focus is the newer target: it counts normally, so the row animates.
        rowAt(1)!.focus();
        fixture.detectChanges();
        expect(animatedCellCount(rowAt(1)!)).toBe(2);

        growViewportToFit(viewport, many.length);
        await flushRenders();
        expect(rowAt(last)).not.toBeNull();
        // One more pass: an outdated target would have had its chance to land by now.
        await flushRenders();
        expect(document.activeElement).toBe(rowAt(1));
        expect(animatedCellCount(rowAt(1)!)).toBe(2);
      });
    });
  });
});
