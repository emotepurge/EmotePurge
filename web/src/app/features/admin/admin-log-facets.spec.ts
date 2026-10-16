import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import de from '../../../../public/i18n/de.json';
import en from '../../../../public/i18n/en.json';
import { LogCategoryFacetValue, LogSummary } from '../../core/admin/admin-logs.model';
import { AdminLogFacets, LOG_LEVEL_NAMES, categoryFilterValue } from './admin-log-facets';

function facet(value: string, count = 1): LogCategoryFacetValue {
  return { value, short: value.slice(value.lastIndexOf('.') + 1), count };
}

function summaryWith(facets: Partial<LogSummary['facets']>): LogSummary {
  return {
    facets: {
      source: [],
      level: [],
      category: [],
      categoryOther: 0,
      ...facets,
    },
  } as LogSummary;
}

describe('categoryFilterValue', () => {
  it('uses the short name when no other facet value shares it', () => {
    const all = [facet('EmotePurge.Worker.TwitchChatManager'), facet('EmotePurge.Api.Foo')];
    expect(categoryFilterValue(all[0], all)).toBe('TwitchChatManager');
  });

  it('uses the full name when another facet value has the same short name', () => {
    const all = [facet('EmotePurge.Worker.SyncService'), facet('EmotePurge.Api.SyncService')];
    expect(categoryFilterValue(all[0], all)).toBe('EmotePurge.Worker.SyncService');
    expect(categoryFilterValue(all[1], all)).toBe('EmotePurge.Api.SyncService');
  });
});

describe('AdminLogFacets', () => {
  let fixture: ComponentFixture<AdminLogFacets>;
  let terms: string[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        AdminLogFacets,
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('en'));
    terms = [];
  });

  function render(summary: LogSummary): HTMLElement {
    fixture = TestBed.createComponent(AdminLogFacets);
    fixture.componentRef.setInput('summary', summary);
    fixture.componentInstance.termAdded.subscribe((t) => terms.push(t));
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  function button(root: HTMLElement, name: string): HTMLButtonElement {
    const found = Array.from(root.querySelectorAll('button')).find(
      (b) => b.getAttribute('aria-label') === name,
    );
    if (!found) {
      throw new Error(`no button named "${name}"`);
    }
    return found;
  }

  it('adds key:value for the value and -key:value for the exclude button', () => {
    const root = render(
      summaryWith({
        source: [{ value: 'worker', count: 7 }],
        level: [{ value: 'warn', count: 3 }],
      }),
    );

    button(root, 'Add worker (7) to the filter').click();
    button(root, 'Exclude worker').click();
    button(root, 'Add Warning (3) to the filter').click();
    button(root, 'Exclude Warning').click();

    expect(terms).toEqual(['source:worker', '-source:worker', 'level:warn', '-level:warn']);
  });

  it('shows the short category name, keeps the full one in the title and filters by the short one', () => {
    const root = render(
      summaryWith({ category: [facet('EmotePurge.Worker.TwitchChatManager', 4)] }),
    );

    const include = button(root, 'Add TwitchChatManager (4) to the filter');
    expect(include.title).toBe('EmotePurge.Worker.TwitchChatManager');
    include.click();
    button(root, 'Exclude TwitchChatManager').click();

    expect(terms).toEqual(['cat:TwitchChatManager', '-cat:TwitchChatManager']);
  });

  it('filters by the full name when two categories share a short name', () => {
    const root = render(
      summaryWith({
        category: [facet('EmotePurge.Worker.SyncService'), facet('EmotePurge.Api.SyncService')],
      }),
    );

    button(root, 'Add SyncService (1) to the filter').click();

    expect(terms).toEqual(['cat:EmotePurge.Worker.SyncService']);
  });

  it('names the entries outside the listed categories', () => {
    const root = render(summaryWith({ category: [facet('A.B')], categoryOther: 12 }));

    expect(root.textContent).toContain('Other categories: 12');
  });
});

describe('level names', () => {
  it.each([
    ['de', de],
    ['en', en],
  ])('are the same in the histogram legend as in the facets and the list (%s)', (_, locale) => {
    const legend = locale.admin.logs.histogram.level;
    expect(legend.info).toBe(LOG_LEVEL_NAMES.info);
    expect(legend.warn).toBe(LOG_LEVEL_NAMES.warn);
    expect(legend.error).toBe(`${LOG_LEVEL_NAMES.error} / ${LOG_LEVEL_NAMES.crit}`);
  });
});
