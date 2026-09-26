import { Dialog } from '@angular/cdk/dialog';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, provideRouter, Route, RouterStateSnapshot } from '@angular/router';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom, Observable, of } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvUndoService } from '../../core/seven-tv/seven-tv-undo.service';
import { usageStatsLeaveGuard } from './usage-stats-leave.guard';

// Two distinct route-definition objects, standing in for the real `usage-stats` and
// `vote-sessions` config entries from app.routes.ts — what matters for the guard is that they are
// different object references, not their concrete path strings.
const USAGE_STATS_ROUTE: Route = { path: 'usage-stats' };
const OTHER_ROUTE: Route = { path: 'vote-sessions' };

function routeSnapshot(routeConfig: Route | null): ActivatedRouteSnapshot {
  return { routeConfig } as unknown as ActivatedRouteSnapshot;
}

function routerState(leafRouteConfig: Route | null): RouterStateSnapshot {
  return {
    root: { routeConfig: leafRouteConfig, firstChild: null } as unknown as ActivatedRouteSnapshot,
  } as unknown as RouterStateSnapshot;
}

const DE_TRANSLATIONS = {
  import: {
    leaveWhileRunning: {
      message: 'Der Kopierlauf läuft noch.',
      confirm: 'Verlassen',
    },
  },
  undo: {
    leaveWhileRunning: {
      message: 'Die Rücknahme der Ersetzungen läuft noch.',
      confirm: 'Trotzdem verlassen',
    },
  },
};

describe('usageStatsLeaveGuard', () => {
  let isRunning: ReturnType<typeof signal<boolean>>;
  let undoRunning: ReturnType<typeof signal<boolean>>;
  let dialogOpen: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    isRunning = signal(false);
    undoRunning = signal(false);
    dialogOpen = vi.fn();

    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideRouter([]),
        { provide: SevenTvImportService, useValue: { isRunning } },
        { provide: SevenTvUndoService, useValue: { isRunning: undoRunning } },
        { provide: Dialog, useValue: { open: dialogOpen } },
      ],
    });
  });

  // Defaults simulate navigating away to a different page (the common case every existing test
  // exercises); the two new cases below override these to simulate a pure channel switch instead.
  function runGuard(
    currentRouteConfig: Route | null = USAGE_STATS_ROUTE,
    nextLeafRouteConfig: Route | null = OTHER_ROUTE,
  ): Observable<boolean> {
    return TestBed.runInInjectionContext(() =>
      usageStatsLeaveGuard(
        {} as never,
        routeSnapshot(currentRouteConfig) as never,
        {} as never,
        routerState(nextLeafRouteConfig) as never,
      ),
    ) as Observable<boolean>;
  }

  it('lets the navigation through without a dialog when no run is active', async () => {
    expect(await firstValueFrom(runGuard())).toBe(true);
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  it('resolves to true when the dialog is confirmed', async () => {
    isRunning.set(true);
    dialogOpen.mockReturnValue({ closed: of(true) });

    expect(await firstValueFrom(runGuard())).toBe(true);
    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  it('resolves to false when the dialog is dismissed (Escape/backdrop)', async () => {
    isRunning.set(true);
    dialogOpen.mockReturnValue({ closed: of(undefined) });

    expect(await firstValueFrom(runGuard())).toBe(false);
  });

  it('lets a pure channel switch through without a dialog while an import is running', async () => {
    isRunning.set(true);

    expect(await firstValueFrom(runGuard(USAGE_STATS_ROUTE, USAGE_STATS_ROUTE))).toBe(true);
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  it('still asks when navigating to a different page while an import is running', async () => {
    isRunning.set(true);
    dialogOpen.mockReturnValue({ closed: of(true) });

    expect(await firstValueFrom(runGuard(USAGE_STATS_ROUTE, OTHER_ROUTE))).toBe(true);
    expect(dialogOpen).toHaveBeenCalledTimes(1);
  });

  // #254 spec 6.5: a running undo asks too, in its own words; should both ever run at once (the
  // arbiter starts one run at a time), the import's wording wins (plan Festlegung 8).
  it('asks in the undo wording while an undo runs, and in the import wording while both do', async () => {
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
    undoRunning.set(true);
    dialogOpen.mockReturnValue({ closed: of(false) });

    expect(await firstValueFrom(runGuard())).toBe(false);
    expect(dialogOpen.mock.calls[0][1].data.message).toBe(
      'Die Rücknahme der Ersetzungen läuft noch.',
    );

    isRunning.set(true);
    await firstValueFrom(runGuard());
    expect(dialogOpen.mock.calls[1][1].data.message).toBe('Der Kopierlauf läuft noch.');
  });
});
