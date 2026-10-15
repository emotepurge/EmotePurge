import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { HistogramSelection, TimeHistogram } from './time-histogram';

// Only the keys the histogram translates, not the full app translation file.
const EN = {
  admin: {
    logs: {
      histogram: {
        unit: 'Entries per interval',
        utc: 'times in UTC',
        empty: 'no entries in this period',
        cursorHint: '{{ title }} - arrows, Enter selects',
        summary:
          '{{ total }} entries, maximum {{ max }} per interval, {{ warnBuckets }} intervals with warnings, {{ errorBuckets }} with errors, {{ buckets }} intervals.',
        partial: 'The last interval is still incomplete.',
        partialShort: 'incomplete',
        reading: 'Information {{ info }}, Warning {{ warn }}, Error {{ error }}',
        processStart: 'start of {{ source }} at {{ time }}',
        level: { info: 'Information', warn: 'Warning', error: 'Error' },
      },
    },
  },
};

const STARTS = [
  '2026-10-11T00:00:00Z',
  '2026-10-11T01:00:00Z',
  '2026-10-11T02:00:00Z',
  '2026-10-11T03:00:00Z',
];

describe('TimeHistogram', () => {
  let fixture: ComponentFixture<TimeHistogram>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        TimeHistogram,
        TranslocoTestingModule.forRoot({
          langs: { en: EN },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('en'));
  });

  function render(
    counts: { info: number[]; warn: number[]; error: number[] } = {
      info: [4, 0, 2, 1],
      warn: [1, 0, 0, 3],
      error: [0, 0, 5, 0],
    },
    extra: {
      starts?: string[];
      processStarts?: { source: string; startedUtc: string }[];
      lastBucketPartial?: boolean;
      bucketSeconds?: number;
      compact?: boolean;
    } = {},
  ): HTMLElement {
    fixture = TestBed.createComponent(TimeHistogram);
    fixture.componentRef.setInput('bucketStartsUtc', extra.starts ?? STARTS);
    fixture.componentRef.setInput('info', counts.info);
    fixture.componentRef.setInput('warn', counts.warn);
    fixture.componentRef.setInput('error', counts.error);
    fixture.componentRef.setInput('bucketSeconds', extra.bucketSeconds ?? 3600);
    fixture.componentRef.setInput('compact', extra.compact ?? false);
    fixture.componentRef.setInput('label', 'Log volume');
    fixture.componentRef.setInput('processStarts', extra.processStarts ?? []);
    fixture.componentRef.setInput('lastBucketPartial', extra.lastBucketPartial ?? false);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function group(root: HTMLElement): HTMLElement {
    return root.querySelector('[role="group"]') as HTMLElement;
  }

  function liveLine(root: HTMLElement): HTMLElement {
    return root.querySelector('[aria-live="polite"]') as HTMLElement;
  }

  function press(root: HTMLElement, key: string): void {
    group(root).dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true }));
    fixture.detectChanges();
  }

  it('exposes an image named by its label and described by sum, maximum and bucket counts', () => {
    const root = render();
    const img = root.querySelector('svg[role="img"]') as SVGElement;
    const description = root.querySelector(`#${img.getAttribute('aria-describedby')}`);

    expect(img.getAttribute('aria-label')).toBe('Log volume');
    expect(description?.textContent).toContain('16 entries');
    expect(description?.textContent).toContain('maximum 7 per interval');
    expect(description?.textContent).toContain('2 intervals with warnings');
    expect(description?.textContent).toContain('1 with errors');
    expect(description?.textContent).toContain('4 intervals');
  });

  it('selects the bucket on a click with its start and start plus the bucket width', () => {
    const root = render();
    const selected: HistogramSelection[] = [];
    fixture.componentInstance.bucketSelected.subscribe((s) => selected.push(s));

    (root.querySelectorAll('[data-testid="histogram-hit"]')[2] as SVGElement).dispatchEvent(
      new MouseEvent('click', { bubbles: true }),
    );

    expect(selected).toEqual([{ fromUtc: '2026-10-11T02:00:00Z', toUtc: '2026-10-11T03:00:00Z' }]);
  });

  it('moves the reading cursor with the arrow keys and reads the three counts', () => {
    const root = render();

    press(root, 'ArrowRight');
    expect(liveLine(root).textContent).toContain('Information 1, Warning 3, Error 0');

    press(root, 'ArrowLeft');
    press(root, 'ArrowLeft');
    expect(liveLine(root).textContent).toContain('Information 0, Warning 0, Error 0');

    press(root, 'Home');
    expect(liveLine(root).textContent).toContain('Information 4, Warning 1, Error 0');

    press(root, 'Escape');
    expect(liveLine(root).textContent?.trim()).toBe('');
  });

  it('selects the cursor bucket on Enter and does nothing without a cursor', () => {
    const root = render();
    const selected: HistogramSelection[] = [];
    fixture.componentInstance.bucketSelected.subscribe((s) => selected.push(s));

    press(root, 'Enter');
    expect(selected).toEqual([]);

    press(root, 'Home');
    press(root, 'ArrowRight');
    press(root, 'Enter');
    expect(selected).toEqual([{ fromUtc: '2026-10-11T01:00:00Z', toUtc: '2026-10-11T02:00:00Z' }]);
  });

  it('names the process starts that fall into the cursor bucket', () => {
    const root = render(undefined, {
      processStarts: [
        { source: 'worker', startedUtc: '2026-10-11T02:30:00Z' },
        { source: 'api', startedUtc: '2026-10-10T12:00:00Z' },
      ],
    });

    press(root, 'End');
    press(root, 'ArrowLeft');

    expect(liveLine(root).textContent).toContain('start of worker');
    expect(liveLine(root).textContent).not.toContain('start of api');
  });

  it('says there are no entries for an empty range and still renders', () => {
    const root = render({ info: [0, 0, 0, 0], warn: [0, 0, 0, 0], error: [0, 0, 0, 0] });

    expect(root.querySelector('[data-testid="histogram-empty"]')?.textContent).toContain(
      'no entries in this period',
    );
    expect(root.querySelector('svg[role="img"]')).toBeTruthy();
  });

  it('renders without buckets and ignores keys', () => {
    const root = render({ info: [], warn: [], error: [] }, { starts: [] });

    expect(root.querySelector('[data-testid="histogram-empty"]')).toBeTruthy();
    expect(() => press(root, 'ArrowRight')).not.toThrow();
    expect(liveLine(root).textContent?.trim()).toBe('');
  });

  it('marks the incomplete last bucket in the text alternative and the reading line', () => {
    const root = render(undefined, { lastBucketPartial: true });

    expect(root.querySelector('[data-testid="histogram-alternative"]')?.textContent).toContain(
      'still incomplete',
    );
    press(root, 'End');
    expect(liveLine(root).textContent).toContain('incomplete');
  });

  function yTickLabels(root: HTMLElement): string[] {
    return Array.from(root.querySelectorAll('svg[role="img"] text'))
      .map((t) => t.textContent?.trim() ?? '')
      .filter((t) => /^[\d.,]+$/.test(t));
  }

  it('uses whole-number y ticks for a small maximum', () => {
    const three = render(
      { info: [3, 1], warn: [0, 0], error: [0, 0] },
      { starts: STARTS.slice(0, 2) },
    );
    expect(yTickLabels(three)).toEqual(['0', '1', '2', '3']);

    const one = render(
      { info: [1, 0], warn: [0, 0], error: [0, 0] },
      { starts: STARTS.slice(0, 2) },
    );
    expect(yTickLabels(one)).toEqual(['0', '1']);
  });

  it('gives every instance its own hatch pattern that its fills reference', () => {
    const a = render();
    const b = render();
    const idOf = (root: HTMLElement) => root.querySelector('pattern')?.getAttribute('id');
    const refs = (root: HTMLElement) =>
      Array.from(root.querySelectorAll('rect[fill^="url("]')).map((r) => r.getAttribute('fill'));

    expect(idOf(a)).toBeTruthy();
    expect(idOf(a)).not.toBe(idOf(b));
    for (const [root] of [[a], [b]]) {
      const expected = `url(#${idOf(root)})`;
      expect(refs(root).length).toBeGreaterThan(0);
      expect(refs(root).every((r) => r === expected)).toBe(true);
    }
  });

  it.each([
    [300, '2026-10-11T00:05:00Z'],
    [86_400, '2026-10-12T00:00:00Z'],
  ])('ends a selected bucket of %i seconds at start plus its width', (bucketSeconds, to) => {
    const root = render(undefined, { bucketSeconds });
    const selected: HistogramSelection[] = [];
    fixture.componentInstance.bucketSelected.subscribe((s: HistogramSelection) => selected.push(s));

    (root.querySelectorAll('[data-testid="histogram-hit"]')[0] as SVGElement).dispatchEvent(
      new MouseEvent('click', { bubbles: true }),
    );

    expect(selected).toEqual([{ fromUtc: '2026-10-11T00:00:00Z', toUtc: to }]);
  });

  it('emits both ends of a selection in the same whole-second format', () => {
    const root = render(undefined, {
      starts: [
        '2026-10-11T00:00:00.000Z',
        '2026-10-11T01:00:00.000Z',
        '2026-10-11T02:00:00.000Z',
        '2026-10-11T03:00:00.000Z',
      ],
    });
    const selected: HistogramSelection[] = [];
    fixture.componentInstance.bucketSelected.subscribe((s: HistogramSelection) => selected.push(s));

    (root.querySelectorAll('[data-testid="histogram-hit"]')[1] as SVGElement).dispatchEvent(
      new MouseEvent('click', { bubbles: true }),
    );

    expect(selected).toEqual([{ fromUtc: '2026-10-11T01:00:00Z', toUtc: '2026-10-11T02:00:00Z' }]);
  });

  it('drops the caption and legend in compact mode but keeps the text alternative', () => {
    const root = render(undefined, { compact: true });

    expect(root.textContent).not.toContain('times in UTC');
    expect(root.querySelector('[data-testid="histogram-legend"]')).toBeNull();
    expect(root.querySelector('[data-testid="histogram-alternative"]')?.textContent).toContain(
      '16 entries',
    );
    expect(root.querySelector('svg[role="img"]')?.getAttribute('aria-label')).toBe('Log volume');
  });

  it('writes to the live region for the keyboard only, never for the pointer', () => {
    const root = render();
    const svg = root.querySelector('svg') as SVGElement;
    svg.getBoundingClientRect = () => ({ left: 0, width: 640, top: 0, height: 100 }) as DOMRect;

    group(root).dispatchEvent(new MouseEvent('pointermove', { bubbles: true, clientX: 100 }));
    fixture.detectChanges();
    expect(liveLine(root).textContent?.trim()).toBe('');

    press(root, 'ArrowRight');
    expect(liveLine(root).textContent).toContain('Information');
  });
});
