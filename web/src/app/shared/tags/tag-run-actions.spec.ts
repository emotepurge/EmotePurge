import { Dialog } from '@angular/cdk/dialog';
import { HttpClient, HttpErrorResponse } from '@angular/common/http';
import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, Subject, firstValueFrom, of, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { EmoteAdminService } from '../../core/emotes/emote-admin.service';
import { DeleteRunInfo, SevenTvDeleteService } from '../../core/seven-tv/seven-tv-delete.service';
import { SevenTvEmoteSetService } from '../../core/seven-tv/seven-tv-emote-set.service';
import { ImportRunInfo, SevenTvImportService } from '../../core/seven-tv/seven-tv-import.service';
import { SevenTvRunArbiter, SevenTvRunKind } from '../../core/seven-tv/seven-tv-run-arbiter';
import { SevenTvTokenService } from '../../core/seven-tv/seven-tv-token.service';
import { EmoteTagSummary } from '../../core/tags/emote-tag.model';
import { EmoteTagService } from '../../core/tags/emote-tag.service';
import {
  TagRunActions,
  TagRunFeedback,
  runButtonFocusTarget,
  settledTagPlayIn,
  settledTagRemoval,
} from './tag-run-actions';
import { TagRunNoticeSink } from './tag-run-notice-sink';

const DE = {
  sevenTvRun: { kind: { undo: 'Rückgängig', import: 'Kopieren' } },
  tags: {
    actions: {
      playIn: 'Ins Set holen',
      remove: 'Aus dem Set entfernen',
      removeCount: 'Aus dem Set entfernen ({{count}})',
    },
    errors: {
      otherRunActive: 'Ein Lauf ({{kind}}) läuft auf einer anderen Seite.',
      ownershipUnavailable: 'Besitz gerade nicht prüfbar.',
      retry: 'Erneut versuchen',
    },
  },
};

function summary(over: Partial<EmoteTagSummary> = {}): EmoteTagSummary {
  return {
    id: 7,
    name: 'Stronghold',
    entryCount: 1,
    inSetCount: 0,
    placedCount: 0,
    active: false,
    activatedAtUtc: null,
    ...over,
  };
}

function importRun(over: Record<string, unknown>): ImportRunInfo {
  return {
    runId: 'import-1',
    phase: 'reporting',
    tag: { tagId: 7, operationId: 'op-1' },
    tagPlacementReport: 'pending',
    ...over,
  } as unknown as ImportRunInfo;
}

function deleteRun(over: Record<string, unknown>): DeleteRunInfo {
  return {
    runId: 'delete-1',
    phase: 'reporting',
    tag: { tagId: 7, operationId: 'op-2' },
    tagRemovalReport: 'pending',
    ...over,
  } as unknown as DeleteRunInfo;
}

describe('TagRunActions', () => {
  let fixture: ComponentFixture<TagRunActions>;
  let startLocked: WritableSignal<boolean>;
  let activeRun: WritableSignal<SevenTvRunKind | null>;
  let importRunSignal: WritableSignal<ImportRunInfo | null>;
  let deleteRunSignal: WritableSignal<DeleteRunInfo | null>;
  let registration: () => Observable<unknown>;
  let listEntries: ReturnType<typeof vi.fn>;
  let completed: number;
  let feedback: TagRunFeedback[];

  beforeEach(async () => {
    startLocked = signal(false);
    activeRun = signal<SevenTvRunKind | null>(null);
    importRunSignal = signal<ImportRunInfo | null>(null);
    deleteRunSignal = signal<DeleteRunInfo | null>(null);
    registration = () => of({ registeredAtUtc: '2026-10-05T10:00:00Z' });
    // Every entry is already in the set, so a play-in ends in the empty report without a dialog.
    listEntries = vi.fn(() =>
      of({
        emoteSetId: 'set-active',
        isActiveSet: true,
        activationOperationId: null,
        entries: [
          {
            sevenTvEmoteId: 'a',
            alias: 'A',
            imageUrl: '',
            inSet: true,
            currentName: null,
            placedByThisTag: false,
            placedAtUtc: null,
            placementOperationId: null,
            heldByActiveTags: [],
            placedByOtherTags: [],
          },
        ],
      }),
    );

    await TestBed.configureTestingModule({
      imports: [
        TagRunActions,
        TranslocoTestingModule.forRoot({
          langs: { de: DE },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: Dialog, useValue: { open: vi.fn() } },
        { provide: EmoteAdminService, useValue: {} },
        {
          provide: SevenTvEmoteSetService,
          useValue: {
            resolveEditableSet: () =>
              of({
                status: 'editable',
                target: {
                  emoteSetId: 'set-active',
                  setName: 'Main',
                  ownerDisplayName: 'Owner',
                  twitchLogin: 'owner',
                  trackedChannelName: 'owner',
                  isActiveSet: true,
                  ownerTwitchChannelId: 'tw-owner',
                },
              }),
          },
        },
        {
          provide: HttpClient,
          useValue: {
            post: () =>
              of({
                data: {
                  emoteSets: {
                    emoteSet: {
                      emotes: {
                        totalCount: 1,
                        pageCount: 1,
                        items: [{ alias: 'A', emote: { id: 'a' } }],
                      },
                    },
                  },
                },
              }),
          },
        },
        { provide: SevenTvTokenService, useValue: { hasToken: signal(true) } },
        {
          provide: SevenTvImportService,
          useValue: { run: importRunSignal, startCheckPending: signal(false) },
        },
        { provide: SevenTvDeleteService, useValue: { run: deleteRunSignal } },
        {
          provide: SevenTvRunArbiter,
          useValue: { startLocked, activeClaim: signal(null), activeRun },
        },
        {
          provide: EmoteTagService,
          useValue: {
            listEntries,
            registerOperation: () => registration(),
            reportPlacements: () => of({ replayed: false }),
          },
        },
      ],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));

    fixture = TestBed.createComponent(TagRunActions);
    fixture.componentRef.setInput('channelName', 'handofblood');
    fixture.componentRef.setInput('tag', summary());
    fixture.componentRef.setInput('activeEmoteSetId', 'set-active');
    fixture.componentRef.setInput('setName', 'Main');
    fixture.componentRef.setInput('enabled', true);
    completed = 0;
    feedback = [];
    fixture.componentInstance.completed.subscribe(() => completed++);
    fixture.componentInstance.feedback.subscribe((value) => feedback.push(value));
    fixture.detectChanges();
  });

  function button(name: string): HTMLButtonElement | undefined {
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find(
      (candidate) => candidate.textContent?.trim() === name,
    );
  }

  it('renders nothing when the host does not enable it', () => {
    fixture.componentRef.setInput('enabled', false);
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelectorAll('button')).toHaveLength(0);
  });

  // Operator decision 2026-10-05: something to clear out, not "played in", decides.
  describe('"Aus dem Set entfernen" exists while an emote of the tag is in the set, or the tag is played in', () => {
    it('is missing for a tag that is not played in and has nothing in the set', () => {
      expect(button('Ins Set holen')).toBeDefined();
      expect(button('Aus dem Set entfernen')).toBeUndefined();
    });

    it('is there for a tag that was never played in once one of its emotes is in the set', () => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 3, inSetCount: 1 }));
      fixture.detectChanges();

      expect(button('Aus dem Set entfernen')).toBeDefined();
      expect(button('Ins Set holen')).toBeDefined();
    });

    it('is there for a played-in tag, also with nothing of it left in the set', () => {
      fixture.componentRef.setInput('tag', summary({ inSetCount: 1, active: true }));
      fixture.detectChanges();
      expect(button('Aus dem Set entfernen')).toBeDefined();

      fixture.componentRef.setInput('tag', summary({ entryCount: 1, inSetCount: 0, active: true }));
      fixture.detectChanges();
      expect(button('Aus dem Set entfernen')).toBeDefined();
    });

    it('is missing when the counts have no set to refer to and the tag is not played in', () => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 2, inSetCount: null }));
      fixture.detectChanges();

      expect(button('Aus dem Set entfernen')).toBeUndefined();
    });
  });

  describe('the set-removal button carries the number of marked emotes', () => {
    beforeEach(() => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 3, inSetCount: 3 }));
      fixture.detectChanges();
    });

    it('names no number without a marking', () => {
      expect(button('Aus dem Set entfernen')).toBeDefined();
    });

    it('shows how many are marked, and follows the marking', () => {
      fixture.componentRef.setInput('markedIds', ['a', 'b']);
      fixture.detectChanges();
      expect(button('Aus dem Set entfernen (2)')).toBeDefined();
      expect(button('Aus dem Set entfernen')).toBeUndefined();

      fixture.componentRef.setInput('markedIds', []);
      fixture.detectChanges();
      expect(button('Aus dem Set entfernen')).toBeDefined();
    });
  });

  describe('"Ins Set holen" exists only while the tag has an emote missing from the set (spec 7.2)', () => {
    it('is there while at least one entry is not in the set, played in or not', () => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 3, inSetCount: 2 }));
      fixture.detectChanges();
      expect(button('Ins Set holen')).toBeDefined();

      fixture.componentRef.setInput('tag', summary({ entryCount: 3, inSetCount: 2, active: true }));
      fixture.detectChanges();
      expect(button('Ins Set holen')).toBeDefined();
      expect(button('Aus dem Set entfernen')).toBeDefined();
    });

    it('is missing, not locked, when nothing is missing — only "Aus dem Set entfernen" stays, played in or not', () => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 3, inSetCount: 3 }));
      fixture.detectChanges();
      expect(button('Ins Set holen')).toBeUndefined();
      expect(button('Aus dem Set entfernen')).toBeDefined();

      fixture.componentRef.setInput('tag', summary({ entryCount: 3, inSetCount: 3, active: true }));
      fixture.detectChanges();
      expect(button('Ins Set holen')).toBeUndefined();
      expect(button('Aus dem Set entfernen')).toBeDefined();
    });

    it('is missing for a tag without entries, and there again once one is not in the set', () => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 0, inSetCount: 0 }));
      fixture.detectChanges();
      expect(button('Ins Set holen')).toBeUndefined();

      fixture.componentRef.setInput('tag', summary({ entryCount: 1, inSetCount: 0 }));
      fixture.detectChanges();
      expect(button('Ins Set holen')).toBeDefined();
    });

    it('is offered for a tag with entries when the counts have no set to refer to', () => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 2, inSetCount: null }));
      fixture.detectChanges();

      expect(button('Ins Set holen')).toBeDefined();
    });
  });

  it('locks both while any 7TV run holds the start, without a reason of its own', () => {
    fixture.componentRef.setInput('tag', summary({ active: true }));
    startLocked.set(true);
    fixture.detectChanges();

    expect(button('Ins Set holen')!.disabled).toBe(true);
    expect(button('Aus dem Set entfernen')!.disabled).toBe(true);
    expect(button('Ins Set holen')!.hasAttribute('aria-describedby')).toBe(false);
  });

  describe('a lock by a run the host does not show (plan 3.8, tags.errors.otherRunActive)', () => {
    beforeEach(() => {
      fixture.componentRef.setInput('tag', summary({ active: true }));
      fixture.componentRef.setInput('unshownRunKinds', ['undo']);
      startLocked.set(true);
    });

    it('names an undo run as the reason and links both buttons to it', () => {
      activeRun.set('undo');
      fixture.detectChanges();

      for (const name of ['Ins Set holen', 'Aus dem Set entfernen']) {
        const locked = button(name)!;
        expect(locked.disabled).toBe(true);
        const reason = (fixture.nativeElement as HTMLElement).querySelector(
          `#${locked.getAttribute('aria-describedby')}`,
        );
        expect(reason?.textContent?.trim()).toBe(
          'Ein Lauf (Rückgängig) läuft auf einer anderen Seite.',
        );
      }
    });

    it('stays silent for an import run, whose dock the host shows (§4.2)', () => {
      activeRun.set('import');
      fixture.detectChanges();

      expect(button('Ins Set holen')!.disabled).toBe(true);
      expect(button('Ins Set holen')!.hasAttribute('aria-describedby')).toBe(false);
      expect(fixture.nativeElement.textContent).not.toContain('anderen Seite');
    });

    it('states no lock where no button stands — nothing missing, nothing in the set, not played in', () => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 0, inSetCount: 0 }));
      activeRun.set('undo');
      fixture.detectChanges();

      expect((fixture.nativeElement as HTMLElement).querySelectorAll('button')).toHaveLength(0);
      expect(fixture.nativeElement.textContent).not.toContain('anderen Seite');
    });

    it('stays silent for an undo on a host that shows the undo itself', () => {
      fixture.componentRef.setInput('unshownRunKinds', []);
      activeRun.set('undo');
      fixture.detectChanges();

      expect(button('Ins Set holen')!.hasAttribute('aria-describedby')).toBe(false);
      expect(fixture.nativeElement.textContent).not.toContain('anderen Seite');
    });
  });

  describe('focus when a play-in takes "Ins Set holen" away', () => {
    let focusLost: number;

    beforeEach(() => {
      focusLost = 0;
      fixture.componentInstance.focusLost.subscribe(() => focusLost++);
      fixture.componentRef.setInput('tag', summary({ entryCount: 1, inSetCount: 0 }));
      fixture.detectChanges();
      button('Ins Set holen')!.click();
      fixture.detectChanges();
    });

    it('moves to "Aus dem Set entfernen" when the tag is played in now', () => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 1, inSetCount: 1, active: true }));
      fixture.detectChanges();

      expect(button('Ins Set holen')).toBeUndefined();
      expect(document.activeElement).toBe(button('Aus dem Set entfernen'));
      expect(focusLost).toBe(0);
    });

    it('moves to "Aus dem Set entfernen" also when the reloaded tag reads as not played in', () => {
      fixture.componentRef.setInput('tag', summary({ entryCount: 1, inSetCount: 1 }));
      fixture.detectChanges();

      expect(document.activeElement).toBe(button('Aus dem Set entfernen'));
      expect(focusLost).toBe(0);
    });

    it('hands over to the host when no button is left to take it', () => {
      // The tag lost its entries meanwhile: nothing missing, nothing in the set, not played in.
      fixture.componentRef.setInput('tag', summary({ entryCount: 0, inSetCount: 0 }));
      fixture.detectChanges();

      expect(focusLost).toBe(1);
    });
  });

  describe('focus when a clear-out takes "Aus dem Set entfernen" away', () => {
    let focusLost: number;
    let removalClosed: Subject<unknown>;

    beforeEach(() => {
      // Just enough of the clear-out's services for a flow to reach its dialog and its run.
      removalClosed = new Subject<unknown>();
      Object.assign(TestBed.inject(Dialog), { open: vi.fn(() => ({ closed: removalClosed })) });
      Object.assign(TestBed.inject(EmoteAdminService), {
        getSetWarning: () => of({ available: false }),
      });
      Object.assign(TestBed.inject(SevenTvDeleteService), {
        beginConfirmedRun: vi.fn(),
        clearConfirmedRun: vi.fn(),
        endConfirmedRun: vi.fn(),
        startDelete: vi.fn(() =>
          deleteRunSignal.set(
            deleteRun({ phase: 'running', tag: { tagId: 7, operationId: 'op' } }),
          ),
        ),
      });
      Object.assign(TestBed.inject(EmoteTagService), { reportRemoval: () => of({}) });

      focusLost = 0;
      fixture.componentInstance.focusLost.subscribe(() => focusLost++);
      // Not played in, its one emote in the set: "Aus dem Set entfernen" only.
      fixture.componentRef.setInput('tag', summary({ entryCount: 1, inSetCount: 1 }));
      fixture.detectChanges();
      button('Aus dem Set entfernen')!.click();
      fixture.detectChanges();
    });

    it('moves to "Ins Set holen" when the cleared tag now misses its emote', () => {
      removalClosed.next({ checkedIds: ['a'] });
      fixture.detectChanges();
      fixture.componentRef.setInput('tag', summary({ entryCount: 1, inSetCount: 0 }));
      fixture.detectChanges();

      expect(button('Aus dem Set entfernen')).toBeUndefined();
      expect(document.activeElement).toBe(button('Ins Set holen'));
      expect(focusLost).toBe(0);
    });

    it('hands over to the host when no button is left to take it', () => {
      removalClosed.next({ checkedIds: ['a'] });
      fixture.detectChanges();
      fixture.componentRef.setInput('tag', summary({ entryCount: 0, inSetCount: 0 }));
      fixture.detectChanges();

      expect(focusLost).toBe(1);
    });

    it('leaves focus alone after a dismissed dialog, when a later reload takes the button away', () => {
      removalClosed.next(undefined);
      fixture.detectChanges();
      // Another tab cleared the tag out meanwhile; the live reload lands here.
      fixture.componentRef.setInput('tag', summary({ entryCount: 1, inSetCount: 0 }));
      fixture.detectChanges();

      expect(button('Aus dem Set entfernen')).toBeUndefined();
      expect(document.activeElement).toBe(document.body);
      expect(focusLost).toBe(0);
    });
  });

  it('leaves focus alone after an aborted play-in, when a later reload takes "Ins Set holen" away', () => {
    let focusLost = 0;
    fixture.componentInstance.focusLost.subscribe(() => focusLost++);
    registration = () => throwError(() => new HttpErrorResponse({ status: 503 }));
    fixture.componentRef.setInput('tag', summary({ entryCount: 1, inSetCount: 0 }));
    fixture.detectChanges();
    button('Ins Set holen')!.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Besitz gerade nicht prüfbar.');

    // Played in from another tab; nothing is missing any more.
    fixture.componentRef.setInput('tag', summary({ entryCount: 0, inSetCount: 0 }));
    fixture.detectChanges();

    expect(document.activeElement).toBe(document.body);
    expect(focusLost).toBe(0);
  });

  describe('runButtonFocusTarget', () => {
    it('leaves focus alone when the user has already moved it', () => {
      expect(runButtonFocusTarget({ focusLost: false, otherShown: true })).toBe('none');
    });

    it('prefers the other run button, then the host fallback', () => {
      expect(runButtonFocusTarget({ focusLost: true, otherShown: true })).toBe('other');
      expect(runButtonFocusTarget({ focusLost: true, otherShown: false })).toBe('fallback');
    });
  });

  it('tells the host when a click starts a flow, and when a retry does', () => {
    let started = 0;
    fixture.componentInstance.started.subscribe(() => started++);
    registration = () => throwError(() => new HttpErrorResponse({ status: 503 }));

    button('Ins Set holen')!.click();
    fixture.detectChanges();
    expect(started).toBe(1);

    button('Erneut versuchen')!.click();
    expect(started).toBe(2);
  });

  it('clears the page-level notice of an earlier flow whose host was torn down when a click starts a new one', () => {
    const sink = TestBed.inject(TagRunNoticeSink);
    sink.raise('handofblood', { key: 'tags.errors.setChanged' });

    button('Ins Set holen')!.click();

    expect(sink.notice()).toBeNull();
  });

  it('hands what a flow says after this component is gone to the sink, never to its outputs', () => {
    const sink = TestBed.inject(TagRunNoticeSink);
    const report = new Subject<unknown>();
    const reportPlacements = vi.fn(() => report);
    (TestBed.inject(EmoteTagService) as unknown as { reportPlacements: unknown }).reportPlacements =
      reportPlacements;

    button('Ins Set holen')!.click();
    expect(reportPlacements).toHaveBeenCalledOnce();
    fixture.destroy();
    report.error(new HttpErrorResponse({ status: 500 }));

    expect(sink.notice()?.notice.key).toBe('tags.errors.reportFailed');
    expect(feedback).toEqual([]);
    expect(completed).toBe(0);
  });

  it('starts no flow from a click that outraces the lock', () => {
    // A click on a disabled button never reaches `(click)`, so the handlers' own guard is called
    // directly — the case it exists for is a click already dispatched when the lock arrives.
    fixture.componentRef.setInput('tag', summary({ active: true }));
    startLocked.set(true);
    const actions = fixture.componentInstance as unknown as { playIn(): void; remove(): void };

    actions.playIn();
    actions.remove();

    expect(listEntries).not.toHaveBeenCalled();
  });

  it("passes the flow's feedback and completion on to the host", () => {
    button('Ins Set holen')!.click();
    fixture.detectChanges();

    expect(feedback).toEqual([
      { key: 'tags.feedback.allPresent.one', params: { count: 1, tag: 'Stronghold' } },
    ]);
    expect(completed).toBe(1);
  });

  it("shows a flow's block as an alert whose retry runs the flow again", () => {
    registration = () => throwError(() => new HttpErrorResponse({ status: 503 }));
    button('Ins Set holen')!.click();
    fixture.detectChanges();

    const alert = (fixture.nativeElement as HTMLElement).querySelector('[role="alert"]');
    expect(alert?.textContent).toContain('Besitz gerade nicht prüfbar.');
    expect(button('Ins Set holen')!.disabled).toBe(false);

    registration = () => of({ registeredAtUtc: '2026-10-05T10:01:00Z' });
    button('Erneut versuchen')!.click();
    fixture.detectChanges();

    expect(listEntries).toHaveBeenCalledTimes(2);
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')).toBeNull();
    expect(completed).toBe(1);
  });

  it('drops the banner and its retry once the host shows another tag or channel', () => {
    const alert = (): Element | null =>
      (fixture.nativeElement as HTMLElement).querySelector('[role="alert"]');
    const raiseBanner = (): void => {
      registration = () => throwError(() => new HttpErrorResponse({ status: 503 }));
      button('Ins Set holen')!.click();
      fixture.detectChanges();
      expect(alert()).not.toBeNull();
    };

    raiseBanner();
    // A reloaded summary of the same tag is no change: the banner stays.
    fixture.componentRef.setInput('tag', summary({ inSetCount: 0 }));
    fixture.detectChanges();
    expect(alert()).not.toBeNull();

    fixture.componentRef.setInput('tag', summary({ id: 8, name: 'Other' }));
    fixture.detectChanges();
    expect(alert()).toBeNull();

    raiseBanner();
    fixture.componentRef.setInput('channelName', 'papaplatte');
    fixture.detectChanges();
    expect(alert()).toBeNull();
    expect(listEntries).toHaveBeenCalledTimes(2);
  });

  it("drops a flow's late failure once the host shows another tag", () => {
    const late = new Subject<unknown>();
    registration = () => late;
    button('Ins Set holen')!.click();
    fixture.componentRef.setInput('tag', summary({ id: 8, name: 'Other' }));
    fixture.detectChanges();

    late.error(new HttpErrorResponse({ status: 503 }));
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('[role="alert"]')).toBeNull();
    expect(button('Ins Set holen')!.disabled).toBe(false);
  });

  it('reports completion when a tag run it can see closes, and again when its report succeeds on a retry', () => {
    importRunSignal.set(importRun({}));
    fixture.detectChanges();
    expect(completed).toBe(0);

    importRunSignal.set(importRun({ phase: 'closed', tagPlacementReport: 'failed' }));
    fixture.detectChanges();
    expect(completed).toBe(1);

    importRunSignal.set(importRun({ phase: 'closed', tagPlacementReport: 'succeeded' }));
    fixture.detectChanges();
    expect(completed).toBe(2);

    deleteRunSignal.set(deleteRun({ phase: 'closed', tagRemovalReport: 'succeeded' }));
    fixture.detectChanges();
    expect(completed).toBe(3);
  });

  it('says nothing for runs without a tag', () => {
    importRunSignal.set(importRun({ phase: 'closed', tag: null }));
    deleteRunSignal.set(deleteRun({ phase: 'closed', tag: undefined }));
    fixture.detectChanges();

    expect(completed).toBe(0);
  });
});

describe('TagRunActions mounted after a tag run settled', () => {
  it('does not report that run as news', async () => {
    const importRunSignal = signal<ImportRunInfo | null>(
      importRun({ phase: 'closed', tagPlacementReport: 'succeeded' }),
    );
    await TestBed.configureTestingModule({
      imports: [
        TagRunActions,
        TranslocoTestingModule.forRoot({
          langs: { de: DE },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: Dialog, useValue: {} },
        { provide: EmoteAdminService, useValue: {} },
        { provide: SevenTvEmoteSetService, useValue: {} },
        { provide: HttpClient, useValue: {} },
        { provide: SevenTvTokenService, useValue: {} },
        { provide: SevenTvImportService, useValue: { run: importRunSignal } },
        { provide: SevenTvDeleteService, useValue: { run: signal(null) } },
        { provide: SevenTvRunArbiter, useValue: { startLocked: signal(false) } },
        { provide: EmoteTagService, useValue: {} },
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(TagRunActions);
    fixture.componentRef.setInput('channelName', 'handofblood');
    fixture.componentRef.setInput('tag', summary());
    let completed = 0;
    fixture.componentInstance.completed.subscribe(() => completed++);
    fixture.detectChanges();

    expect(completed).toBe(0);
  });
});

describe('settled tag runs (no tag is null on an import run, absent on a delete run)', () => {
  it('names a closed tag play-in by run and report state, and nothing else', () => {
    expect(settledTagPlayIn(importRun({ phase: 'closed', tagPlacementReport: 'failed' }))).toBe(
      'import-1:failed',
    );
    expect(settledTagPlayIn(importRun({}))).toBeNull();
    expect(settledTagPlayIn(importRun({ phase: 'closed', tag: null }))).toBeNull();
    expect(settledTagPlayIn(null)).toBeNull();
  });

  it('names a closed tag clear-out the same way', () => {
    expect(settledTagRemoval(deleteRun({ phase: 'closed', tagRemovalReport: 'succeeded' }))).toBe(
      'delete-1:succeeded',
    );
    expect(settledTagRemoval(deleteRun({}))).toBeNull();
    expect(settledTagRemoval(deleteRun({ phase: 'closed', tag: undefined }))).toBeNull();
    expect(settledTagRemoval(null)).toBeNull();
  });
});
