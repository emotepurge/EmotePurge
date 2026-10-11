import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { LineChart, LineChartSeries } from './line-chart';

// Only the keys the chart translates — not the full app translation file.
const EN = {
  admin: {
    dashboard: {
      chart: {
        noData: 'no data',
        incomplete: 'The last value is still incomplete.',
        incompleteShort: 'incomplete',
        cursorHint: '{{ title }} - arrows',
        summary:
          '{{ label }}: min {{ min }}, max {{ max }}, last {{ last }}, {{ known }}/{{ total }}',
        unit: { count: 'Unit: count', cores: 'Unit: cores' },
      },
    },
  },
  series: { a: 'Alpha', b: 'Beta', c: 'Gamma' },
};

const STARTS = [
  '2026-10-11T00:00:00Z',
  '2026-10-11T01:00:00Z',
  '2026-10-11T02:00:00Z',
  '2026-10-11T03:00:00Z',
];

function series(id: string, values: (number | null)[]): LineChartSeries {
  return { id, labelKey: `series.${id}`, values };
}

describe('LineChart', () => {
  let fixture: ComponentFixture<LineChart>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        LineChart,
        TranslocoTestingModule.forRoot({
          langs: { en: EN },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('en'));
  });

  function render(
    seriesList: LineChartSeries[],
    extra: { partialLastBucket?: boolean; starts?: string[] } = {},
  ): HTMLElement {
    fixture = TestBed.createComponent(LineChart);
    fixture.componentRef.setInput('series', seriesList);
    fixture.componentRef.setInput('xStartsUtc', extra.starts ?? STARTS);
    fixture.componentRef.setInput('bucketSeconds', 3600);
    fixture.componentRef.setInput('unit', 'count');
    fixture.componentRef.setInput('title', 'Live channels');
    fixture.componentRef.setInput('partialLastBucket', extra.partialLastBucket ?? false);
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

  it('exposes the chart as an image named by its title and described by a generated text', () => {
    const root = render([series('a', [1, 5, 3, 2])]);
    const img = root.querySelector('svg[role="img"]') as SVGElement;
    const description = root.querySelector(`#${img.getAttribute('aria-describedby')}`);

    expect(img.getAttribute('aria-label')).toBe('Live channels');
    expect(description?.textContent).toContain('Alpha: min 1, max 5, last 2, 4/4');
  });

  it('draws a gap as two separate sub-paths', () => {
    const root = render([series('a', [1, null, 3, 4])]);
    const d = root.querySelector('svg[role="img"] g g path')?.getAttribute('d') ?? '';

    expect(d.match(/M/g)).toHaveLength(2);
  });

  it('starts without a cursor and an empty live line', () => {
    const root = render([series('a', [1, 5, 3, 2])]);

    expect(liveLine(root).textContent?.trim()).toBe('');
  });

  it('moves the cursor with the arrow keys and announces bucket time and values', () => {
    const root = render([series('a', [1, 5, 3, 2]), series('b', [9, 8, 7, 6])]);

    press(root, 'ArrowLeft'); // first press lands on the newest bucket
    expect(liveLine(root).textContent).toContain('Alpha: 2');
    expect(liveLine(root).textContent).toContain('Beta: 6');
    expect(liveLine(root).textContent).toMatch(/3:00/);

    press(root, 'ArrowLeft');
    expect(liveLine(root).textContent).toContain('Alpha: 3');

    press(root, 'Home');
    expect(liveLine(root).textContent).toContain('Alpha: 1');

    press(root, 'ArrowLeft'); // clamps at the first bucket
    expect(liveLine(root).textContent).toContain('Alpha: 1');

    press(root, 'End');
    press(root, 'ArrowRight'); // clamps at the last bucket
    expect(liveLine(root).textContent).toContain('Alpha: 2');
  });

  it('names an unknown value as such instead of reading it as zero', () => {
    const root = render([series('a', [1, null, 3, 4])]);

    press(root, 'Home');
    press(root, 'ArrowRight');

    expect(liveLine(root).textContent).toContain('Alpha: no data');
  });

  it('clears the cursor on Escape and leaves other keys alone', () => {
    const root = render([series('a', [1, 5, 3, 2])]);
    press(root, 'End');
    expect(liveLine(root).textContent).not.toBe('');

    const handled = new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true });
    group(root).dispatchEvent(handled);
    expect(handled.defaultPrevented).toBe(false);

    press(root, 'Escape');
    expect(liveLine(root).textContent?.trim()).toBe('');
  });

  it('makes the live line a polite live region and the plot reachable by keyboard', () => {
    const root = render([series('a', [1, 2, 3, 4])]);

    expect(liveLine(root).getAttribute('aria-live')).toBe('polite');
    expect(group(root).tabIndex).toBe(0);
    expect(group(root).getAttribute('aria-label')).toContain('Live channels');
  });

  it('sets the same cursor from the pointer', () => {
    const root = render([series('a', [1, 5, 3, 2])]);
    const svg = root.querySelector('svg[role="img"]') as SVGElement;
    svg.getBoundingClientRect = () => ({ left: 0, width: 640 }) as DOMRect;

    const event = new MouseEvent('pointermove', { clientX: 640, bubbles: true });
    group(root).dispatchEvent(event);
    fixture.detectChanges();

    expect(liveLine(root).textContent).toContain('Alpha: 2');
  });

  it('renders a text legend with name and last value only from three series on', () => {
    const two = render([series('a', [1, 2, 3, 4]), series('b', [4, 3, 2, 1])]);
    expect(two.querySelector('[data-testid="chart-legend"]')).toBeNull();

    const three = render([
      series('a', [1, 2, 3, 4]),
      series('b', [4, 3, 2, 1]),
      series('c', [7, 7, 7, 9]),
    ]);
    const legend = three.querySelector('[data-testid="chart-legend"]')?.textContent ?? '';
    expect(legend).toContain('Gamma: 9');
    expect(legend).toContain('Beta: 1');
  });

  it('says in the text alternative and at the cursor that the last bucket is incomplete', () => {
    const root = render([series('a', [1, 5, 3, 2])], { partialLastBucket: true });
    const alternative = root.querySelector('[data-testid="chart-alternative"]')?.textContent;

    expect(alternative).toContain('still incomplete');

    press(root, 'End');
    expect(liveLine(root).textContent).toContain('incomplete');
    press(root, 'ArrowLeft');
    expect(liveLine(root).textContent).not.toContain('incomplete');
  });

  it('survives an empty series list and empty buckets', () => {
    const root = render([], { starts: [] });

    press(root, 'ArrowLeft');

    expect(liveLine(root).textContent?.trim()).toBe('');
  });
});
