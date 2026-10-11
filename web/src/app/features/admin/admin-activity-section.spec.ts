import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { AdminDashboardActivity } from '../../core/admin/admin.model';
import { AdminActivitySection } from './admin-activity-section';

const EN = {
  admin: {
    dashboard: {
      activity: {
        title: 'Activity',
        chartTitle: 'Live channels per day',
        shownRange: 'Still showing: {{ range }}',
        range: { label: 'Range', '30d': '30 days', '90d': '90 days', '13mo': '13 months' },
        series: { liveChannels: 'Live channels' },
        help: { poller: 'p', purge: 'u', today: 't', cap: 'c' },
      },
      chart: {
        noData: 'no data',
        noRange: 'none',
        legend: { sum: 's', max: 'm' },
        incomplete: 'i',
        incompleteShort: 'i',
        cursorHint: '{{ title }}',
        summary: '{{ label }}',
        utc: 'UTC',
        unitWord: { cores: 'c', tasks: 't' },
        unit: { count: 'count' },
      },
    },
  },
  errors: { api: { invalid_dashboard_range: 'Invalid range' } },
};

function activity(range: '30d' | '90d' | '13mo'): AdminDashboardActivity {
  return {
    range,
    fromDate: '2026-10-10',
    toDate: '2026-10-11',
    todayIsPartial: true,
    generatedAtUtc: '2026-10-11T12:00:00Z',
    days: [
      { date: '2026-10-10', liveChannels: 3 },
      { date: '2026-10-11', liveChannels: 4 },
    ],
  };
}

describe('AdminActivitySection', () => {
  let fixture: ComponentFixture<AdminActivitySection>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        AdminActivitySection,
        TranslocoTestingModule.forRoot({
          langs: { en: EN },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('en'));
    fixture = TestBed.createComponent(AdminActivitySection);
  });

  function setup(inputs: Record<string, unknown>): HTMLElement {
    for (const [key, value] of Object.entries(inputs)) {
      fixture.componentRef.setInput(key, value);
    }
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  const chart = (root: HTMLElement) => root.querySelector('app-line-chart') as HTMLElement;

  it('is not busy and names no other range while the selected range is on screen', () => {
    const root = setup({ range: '30d', activity: activity('30d'), loading: false });
    expect(chart(root).getAttribute('aria-busy')).toBe('false');
    expect(root.querySelector('[data-testid="activity-shown-range"]')).toBeNull();
  });

  it('marks the stale chart busy and names its range while another range loads', () => {
    const root = setup({ range: '90d', activity: activity('30d'), loading: true });
    expect(chart(root).getAttribute('aria-busy')).toBe('true');
    expect(root.querySelector('[data-testid="activity-shown-range"]')?.textContent).toContain(
      '30 days',
    );
  });

  it('keeps naming the shown range after a failed switch, without claiming to be busy', () => {
    const root = setup({
      range: '90d',
      activity: activity('30d'),
      loading: false,
      errorKey: 'errors.api.invalid_dashboard_range',
    });
    expect(chart(root).getAttribute('aria-busy')).toBe('false');
    expect(root.querySelector('[data-testid="activity-shown-range"]')?.textContent).toContain(
      '30 days',
    );
    expect(root.querySelectorAll('app-notice-banner')).toHaveLength(1);
  });

  it('draws day markers for 30 days of buckets', () => {
    const root = setup({ range: '30d', activity: activity('30d'), loading: false });
    // Two buckets; the filling last one is a hollow ring, so one marker remains.
    expect(root.querySelectorAll('[data-testid="chart-point"]')).toHaveLength(1);
  });
});
