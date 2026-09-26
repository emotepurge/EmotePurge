/**
 * #256 P1 (Plan-256 review): regresses a bug introduced by T2's own migration of Delete/Restore
 * onto the run lifecycle. The constructor's channel-change effect calls
 * `resetIfChannelChanged(channelName)` on both services — since T2 that method reads `this.run()`,
 * a signal. Without `untracked`, that read makes the *effect itself* a dependent of the run record
 * it merely inspects: the moment a carried-over run's report reaches `closed` (Plan-256 Festlegung
 * 13 lets a still-`reporting` run follow the user across a channel switch), the effect reruns with
 * the *same* `channelName` and the now-`closed` run is reset at once — the dock, its failure reason
 * and its retry button vanish the instant the report finishes, with no `console.warn` and no way for
 * the user to ever see the outcome.
 *
 * These tests drive the real `ChannelWorkspaceLayout` effect end to end against the real
 * `SevenTvDeleteService`/`SevenTvRestoreService` (root-provided, undoubled) — a hand call to
 * `resetIfChannelChanged()` would not exercise the effect wiring the bug actually lived in. `run` is
 * `WritableSignal` by contract precisely so a spec can drive it directly (see either service's own
 * doc comment on the field) — the same seam usage-stats-page.spec.ts already uses. The template is
 * replaced with a bare `<div>`, the same technique usage-stats-page.spec.ts and
 * usage-stats.routes.spec.ts already use: nothing here is under test in `BackLink`/`TabLink`/
 * `NoticeBanner`, only the constructor's effect and the two services' own state.
 */
import { Dialog } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting, HttpTestingController } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { of } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ChannelPermissions } from '../../core/channels/channel.model';
import { ChannelService } from '../../core/channels/channel.service';
import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { RunResult } from '../../core/seven-tv/seven-tv-run-engine';
import { DeleteRunInfo, SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import {
  RestoreRunInfo,
  SevenTvRestoreService,
} from '../../core/seven-tv/seven-tv-restore.service';
import { SevenTvUndoService, UndoRunInfo } from '../../core/seven-tv/seven-tv-undo.service';
import { ChannelWorkspaceLayout } from './channel-workspace-layout';

/** jsdom ships no EventSource — same stand-in as usage-stats-page.spec.ts and
 *  core/live/live-reload.spec.ts. The constructor's own live subscription (the resync-feedback
 *  upgrade) needs a working factory to construct at all, even though no test here emits from it. */
class FakeEventSource {
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;
  close(): void {
    /* no-op */
  }
}

const PERMISSIONS: ChannelPermissions = {
  canManage: true,
  canViewUsageStats: true,
  isGlobalAdmin: false,
  isTracked: true,
  isBotActive: true,
};

const DONE_RESULT: RunResult = {
  doneKeys: ['7tv-1'],
  items: [],
  startedAt: 0,
  finishedAt: 1,
};

function reportingDeleteRun(channelName: string): DeleteRunInfo {
  return {
    runId: 'delete-1',
    phase: 'reporting',
    destructive: true,
    channelName,
    expectedChannelName: channelName,
    setId: 'set-1',
    result: DONE_RESULT,
    syncReport: 'pending',
    syncReportReason: null,
  };
}

function reportingRestoreRun(hostChannelName: string): RestoreRunInfo {
  return {
    runId: 'restore-1',
    phase: 'reporting',
    destructive: false,
    targetSetId: 'set-1',
    expectedChannelName: hostChannelName,
    resyncChannelName: null,
    hostChannelName,
    setName: 'set-1',
    ownerOrChannelLabel: hostChannelName,
    result: DONE_RESULT,
    syncReport: 'pending',
    syncReportReason: null,
    resyncTrigger: 'idle',
  };
}

/** A settled undo (#254) started on `hostChannelName`, its removal report still out. */
function reportingUndoRun(hostChannelName: string): UndoRunInfo {
  return {
    runId: 'undo-1',
    phase: 'reporting',
    destructive: true,
    targetSetId: 'set-1',
    expectedChannelName: hostChannelName,
    hostChannelName,
    setName: 'set-1',
    ownerOrChannelLabel: hostChannelName,
    trackedChannelName: hostChannelName,
    ownerDisplayName: null,
    sourceFile: {
      stage: 'finished',
      exportedAt: '2026-09-25T10:00:00.000Z',
      verifiedAt: null,
      finishedAt: '2026-09-25T10:00:00.000Z',
      origin: null,
    },
    acknowledgedUnproven: false,
    rows: [],
    skipped: [],
    recheck: {},
    settlement: 'settled',
    result: { ...DONE_RESULT, items: [] },
    removalReport: 'pending',
    removalReportReason: null,
    restoreReport: 'idle',
    restoreReportReason: null,
    resyncTrigger: 'idle',
    abortedForPrivileges: false,
    protocolSaved: false,
  };
}

describe('ChannelWorkspaceLayout — a carried-over run must not be dropped the instant it closes (#256 P1)', () => {
  let fixture: ComponentFixture<ChannelWorkspaceLayout>;
  let httpMock: HttpTestingController;
  let deleteService: SevenTvDeleteService;
  let restoreService: SevenTvRestoreService;

  beforeEach(async () => {
    TestBed.overrideComponent(ChannelWorkspaceLayout, { set: { template: '<div></div>' } });

    await TestBed.configureTestingModule({
      imports: [
        ChannelWorkspaceLayout,
        TranslocoTestingModule.forRoot({
          langs: { de: {} },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: Dialog, useValue: { open: vi.fn() } },
        { provide: ChannelService, useValue: { getPermissions: () => of(PERMISSIONS) } },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: () => new FakeEventSource() as unknown as EventSource,
        },
      ],
    }).compileComponents();

    httpMock = TestBed.inject(HttpTestingController);
    deleteService = TestBed.inject(SevenTvDeleteService);
    restoreService = TestBed.inject(SevenTvRestoreService);

    fixture = TestBed.createComponent(ChannelWorkspaceLayout);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
  });

  afterEach(() => {
    httpMock.verify();
  });

  it('keeps a reporting delete run visible across a channel switch, still shows it once it closes failed, and only drops it on the next switch', () => {
    // The run was started on 'a' and is still awaiting its sync-deleted answer.
    deleteService.run.set(reportingDeleteRun('a'));

    // Scenario 1: switch to 'b' while the run is still `reporting` — Plan-256 Festlegung 13 lets it
    // follow the user rather than vanish mid-report.
    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();
    expect(deleteService.run()?.runId).toBe('delete-1');
    expect(deleteService.run()?.phase).toBe('reporting');

    // The report now ends in a 403 (or any non-success) while the dock sits on 'b'.
    deleteService.run.set({
      ...deleteService.run()!,
      phase: 'closed',
      syncReport: 'failed',
      syncReportReason: 'unavailable',
    });
    // Nothing changed `channelName` here — before the #256 P1 fix, the effect's own (untracked-free)
    // read of `run()` made this `set()` alone rerun the effect and reset the now-`closed` run at
    // once, on the very same channel. `TestBed.tick()` flushes any pending effect; the run must
    // still be exactly what was just set.
    TestBed.tick();
    expect(deleteService.run()?.runId).toBe('delete-1');
    expect(deleteService.run()?.phase).toBe('closed');
    expect(deleteService.run()?.syncReportReason).toBe('unavailable');

    // Scenario 2: a further switch is what finally lets the user leave a *closed* run behind.
    fixture.componentRef.setInput('channelName', 'c');
    fixture.detectChanges();
    expect(deleteService.run()).toBeNull();
  });

  it('keeps a reporting restore run visible across a channel switch, still shows it once it closes failed, and only drops it on the next switch', () => {
    restoreService.run.set(reportingRestoreRun('a'));

    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();
    expect(restoreService.run()?.runId).toBe('restore-1');
    expect(restoreService.run()?.phase).toBe('reporting');

    restoreService.run.set({
      ...restoreService.run()!,
      phase: 'closed',
      syncReport: 'failed',
      syncReportReason: 'unavailable',
    });
    TestBed.tick();
    expect(restoreService.run()?.runId).toBe('restore-1');
    expect(restoreService.run()?.phase).toBe('closed');
    expect(restoreService.run()?.syncReportReason).toBe('unavailable');

    fixture.componentRef.setInput('channelName', 'c');
    fixture.detectChanges();
    expect(restoreService.run()).toBeNull();
  });

  // #254: the undo is the fourth service reset from the same `untracked` block — same rule.
  it('keeps a reporting undo run visible across a channel switch, still shows it once it closes failed, and only drops it on the next switch', () => {
    const undoService = TestBed.inject(SevenTvUndoService);
    undoService.run.set(reportingUndoRun('a'));

    fixture.componentRef.setInput('channelName', 'b');
    fixture.detectChanges();
    expect(undoService.run()?.runId).toBe('undo-1');

    undoService.run.set({
      ...undoService.run()!,
      phase: 'closed',
      removalReport: 'failed',
      removalReportReason: 'unavailable',
    });
    TestBed.tick();
    expect(undoService.run()?.phase).toBe('closed');
    expect(undoService.run()?.removalReportReason).toBe('unavailable');

    fixture.componentRef.setInput('channelName', 'c');
    fixture.detectChanges();
    expect(undoService.run()).toBeNull();
  });
});
