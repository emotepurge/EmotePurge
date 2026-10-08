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
import { Router, provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { HttpErrorResponse } from '@angular/common/http';
import { Subject, of, throwError } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { AuthService } from '../../core/auth/auth.service';
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
  tagRunsEnabled: false,
  canPurgeAsBroadcaster: false,
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
    targetOwnerTwitchId: null,
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
    targetOwnerTwitchId: null,
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
    targetOwnerTwitchId: null,
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

/**
 * The real template this time: which tabs a user gets is the layout's own decision (spec #201 9.3 —
 * the tags tab follows the usage tab's visibility, `canViewUsageStats`).
 */
describe('ChannelWorkspaceLayout — tabs', () => {
  function tabLabels(permissions: ChannelPermissions): string[] {
    TestBed.configureTestingModule({
      imports: [
        ChannelWorkspaceLayout,
        TranslocoTestingModule.forRoot({
          langs: {
            de: {
              channelWorkspace: {
                tabs: { usage: 'Nutzung', tags: 'Tags', voting: 'Votings', activity: 'Aktivität' },
              },
            },
          },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: Dialog, useValue: { open: vi.fn() } },
        { provide: ChannelService, useValue: { getPermissions: () => of(permissions) } },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: () => new FakeEventSource() as unknown as EventSource,
        },
      ],
    });
    const fixture = TestBed.createComponent(ChannelWorkspaceLayout);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    const nav: HTMLElement = fixture.nativeElement.querySelector('nav');
    return Array.from(nav.querySelectorAll('a')).map((link) => link.textContent?.trim() ?? '');
  }

  it('shows the tags tab right after the usage tab when usage stats may be viewed', () => {
    expect(tabLabels({ ...PERMISSIONS, canManage: false })).toEqual(['Nutzung', 'Tags', 'Votings']);
  });

  it('hides the tags tab together with the usage tab otherwise', () => {
    expect(tabLabels({ ...PERMISSIONS, canManage: false, canViewUsageStats: false })).toEqual([
      'Votings',
    ]);
  });
});

/**
 * #245: the broadcaster's own purge and the lock-aware reactivation, against the real template.
 * Behaviour only — which buttons exist, what the dialog is given and what a confirmation or a
 * refusal leads to — not how the header is laid out.
 */
describe('ChannelWorkspaceLayout — broadcaster self-purge and lock prompt', () => {
  const SUMMARY = { emoteCount: 5, voteSessionCount: 2, liveDayCount: 9, tagCount: 3 };
  const DE = {
    channelWorkspace: {
      leaveChannel: 'Channel verlassen',
      rejoinChannel: 'Bot reaktivieren',
      purgeOwnData: 'Kanaldaten löschen',
      purgeOwnDataDialog: {
        title: 'T',
        message: 'E{{ emotes }} V{{ voteSessions }} L{{ liveDays }} T{{ tags }}',
        inputLabel: 'I',
        confirm: 'C',
      },
      errors: {
        leaveForbidden: 'FORBIDDEN',
        purgeOwnDataFailed: 'PURGE-FAILED',
        purgeOwnDataUnconfirmed: 'PURGE-UNCONFIRMED',
      },
    },
    broadcasterLock: { liftConfirm: 'LOCK {{ date }}', liftConfirmLabel: 'LIFT' },
    errors: {
      generic: 'GENERIC',
      api: {
        account_mismatch: 'MISMATCH',
        channel_locked_by_broadcaster: 'STREAMER-REMOVED',
      },
    },
  };

  let dialogClosed: Subject<boolean | undefined>;
  let dialogOpen: ReturnType<typeof vi.fn>;
  let channelService: {
    getPermissions: ReturnType<typeof vi.fn>;
    getDataSummary: ReturnType<typeof vi.fn>;
    purgeOwnData: ReturnType<typeof vi.fn>;
    join: ReturnType<typeof vi.fn>;
  };
  let navigateByUrl: ReturnType<typeof vi.fn>;

  function render(
    permissions: Partial<ChannelPermissions>,
  ): ComponentFixture<ChannelWorkspaceLayout> {
    dialogClosed = new Subject<boolean | undefined>();
    dialogOpen = vi.fn(() => ({ closed: dialogClosed }));
    channelService = {
      getPermissions: vi.fn(() => of({ ...PERMISSIONS, ...permissions })),
      getDataSummary: vi.fn(() => of(SUMMARY)),
      purgeOwnData: vi.fn(() => of(undefined)),
      join: vi.fn(() => of({})),
    };
    TestBed.configureTestingModule({
      imports: [
        ChannelWorkspaceLayout,
        TranslocoTestingModule.forRoot({
          langs: { de: DE },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        { provide: Dialog, useValue: { open: dialogOpen } },
        { provide: ChannelService, useValue: channelService },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: () => new FakeEventSource() as unknown as EventSource,
        },
      ],
    });
    navigateByUrl = vi
      .spyOn(TestBed.inject(Router), 'navigateByUrl')
      .mockResolvedValue(true) as never;
    TestBed.inject(AuthService).currentUser.set({
      twitchUserId: '42',
      login: 'streamer',
      displayName: 'Streamer',
      tokenExpiresAtUtc: '2099-01-01T00:00:00Z',
      isGlobalAdmin: false,
      profileImageUrl: null,
    });
    const fixture = TestBed.createComponent(ChannelWorkspaceLayout);
    fixture.componentRef.setInput('channelName', 'a');
    fixture.detectChanges();
    return fixture;
  }

  function button(fixture: ComponentFixture<ChannelWorkspaceLayout>, label: string) {
    return Array.from(
      fixture.nativeElement.querySelectorAll('button') as NodeListOf<HTMLButtonElement>,
    ).find((candidate) => candidate.textContent?.trim() === label);
  }

  function text(fixture: ComponentFixture<ChannelWorkspaceLayout>): string {
    return (fixture.nativeElement as HTMLElement).textContent ?? '';
  }

  const dialogData = () => dialogOpen.mock.calls[0][1].data as Record<string, string>;

  it('offers the purge button only when the server says the viewer is the broadcaster — also on an inactive channel', () => {
    expect(button(render({ canPurgeAsBroadcaster: false }), 'Kanaldaten löschen')).toBeUndefined();
    TestBed.resetTestingModule();
    expect(button(render({ canPurgeAsBroadcaster: true }), 'Kanaldaten löschen')).toBeDefined();
    TestBed.resetTestingModule();
    expect(
      button(render({ canPurgeAsBroadcaster: true, isBotActive: false }), 'Kanaldaten löschen'),
    ).toBeDefined();
  });

  it('opens the typed confirmation with the server numbers and the channel name, and purges nothing on cancel', () => {
    const fixture = render({ canPurgeAsBroadcaster: true });
    button(fixture, 'Kanaldaten löschen')!.click();

    expect(channelService.getDataSummary).toHaveBeenCalledWith('a');
    expect(dialogData()['requiredText']).toBe('a');
    expect(dialogData()['message']).toBe('E5 V2 L9 T3');

    dialogClosed.next(false);
    expect(channelService.purgeOwnData).not.toHaveBeenCalled();
    expect(navigateByUrl).not.toHaveBeenCalled();
    fixture.detectChanges();
    expect(button(fixture, 'Kanaldaten löschen')!.disabled).toBe(false);
  });

  it('purges bound to the signed-in account on confirmation and goes to the overview', () => {
    const fixture = render({ canPurgeAsBroadcaster: true });
    button(fixture, 'Kanaldaten löschen')!.click();
    dialogClosed.next(true);

    expect(channelService.purgeOwnData).toHaveBeenCalledWith('a', '42');
    expect(navigateByUrl).toHaveBeenCalledWith('/');
  });

  it('shows the coded refusal of the purge and stays on the page', () => {
    const fixture = render({ canPurgeAsBroadcaster: true });
    channelService.purgeOwnData.mockReturnValue(
      throwError(
        () => new HttpErrorResponse({ status: 409, error: { errorCode: 'account_mismatch' } }),
      ),
    );
    button(fixture, 'Kanaldaten löschen')!.click();
    dialogClosed.next(true);
    fixture.detectChanges();

    expect(text(fixture)).toContain('MISMATCH');
    expect(navigateByUrl).not.toHaveBeenCalled();
  });

  it('falls back to the action’s own sentence when the refusal carries no known code', () => {
    const fixture = render({ canPurgeAsBroadcaster: true });
    channelService.getDataSummary.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 500 })),
    );
    button(fixture, 'Kanaldaten löschen')!.click();
    fixture.detectChanges();

    expect(text(fixture)).toContain('PURGE-FAILED');
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  it.each([0, 500, 502, 504])(
    'says the outcome is unclear when the deletion answers with status %i, since it may have committed',
    (status) => {
      const fixture = render({ canPurgeAsBroadcaster: true });
      channelService.purgeOwnData.mockReturnValue(
        throwError(() => new HttpErrorResponse({ status })),
      );
      button(fixture, 'Kanaldaten löschen')!.click();
      dialogClosed.next(true);
      fixture.detectChanges();

      expect(text(fixture)).toContain('PURGE-UNCONFIRMED');
      expect(text(fixture)).not.toContain('PURGE-FAILED');
      expect(navigateByUrl).not.toHaveBeenCalled();
    },
  );

  it('keeps the “nothing changed” sentence for an uncoded 4xx refusal of the deletion', () => {
    const fixture = render({ canPurgeAsBroadcaster: true });
    channelService.purgeOwnData.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 403 })),
    );
    button(fixture, 'Kanaldaten löschen')!.click();
    dialogClosed.next(true);
    fixture.detectChanges();

    expect(text(fixture)).toContain('PURGE-FAILED');
  });

  it('keeps the “nothing changed” sentence when only the summary failed with an unknown outcome', () => {
    // The summary is a read: a dropped GET deletes nothing, so it is never "unclear".
    const fixture = render({ canPurgeAsBroadcaster: true });
    channelService.getDataSummary.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 0 })),
    );
    button(fixture, 'Kanaldaten löschen')!.click();
    fixture.detectChanges();

    expect(text(fixture)).toContain('PURGE-FAILED');
    expect(text(fixture)).not.toContain('PURGE-UNCONFIRMED');
  });

  it('reactivation: an admin 409 asks first and retries with the flag once confirmed', () => {
    const fixture = render({ isBotActive: false });
    channelService.join.mockReturnValueOnce(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: {
              errorCode: 'channel_locked_by_broadcaster',
              lockedAtUtc: '2026-10-01T10:00:00Z',
            },
          }),
      ),
    );
    button(fixture, 'Bot reaktivieren')!.click();
    expect(dialogData()['message']).toContain('LOCK');

    dialogClosed.next(true);
    expect(channelService.join).toHaveBeenCalledTimes(2);
    expect(channelService.join).toHaveBeenLastCalledWith('a', { liftBroadcasterLock: true });
  });

  it('reactivation: declining the prompt sends no second request and keeps the channel inactive', () => {
    const fixture = render({ isBotActive: false });
    channelService.join.mockReturnValueOnce(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: {
              errorCode: 'channel_locked_by_broadcaster',
              lockedAtUtc: '2026-10-01T10:00:00Z',
            },
          }),
      ),
    );
    button(fixture, 'Bot reaktivieren')!.click();
    dialogClosed.next(false);
    fixture.detectChanges();

    expect(channelService.join).toHaveBeenCalledTimes(1);
    expect(button(fixture, 'Bot reaktivieren')).toBeDefined();
  });

  it('reactivation: a moderator’s 403 for the lock shows the streamer text, not the generic forbidden one, and opens no dialog', () => {
    const fixture = render({ isBotActive: false });
    channelService.join.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 403,
            error: { errorCode: 'channel_locked_by_broadcaster' },
          }),
      ),
    );
    button(fixture, 'Bot reaktivieren')!.click();
    fixture.detectChanges();

    expect(text(fixture)).toContain('STREAMER-REMOVED');
    expect(text(fixture)).not.toContain('FORBIDDEN');
    expect(dialogOpen).not.toHaveBeenCalled();
  });

  it('reactivation: any other 403 keeps its existing text', () => {
    const fixture = render({ isBotActive: false });
    channelService.join.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 403 })));
    button(fixture, 'Bot reaktivieren')!.click();
    fixture.detectChanges();

    expect(text(fixture)).toContain('FORBIDDEN');
  });
});
