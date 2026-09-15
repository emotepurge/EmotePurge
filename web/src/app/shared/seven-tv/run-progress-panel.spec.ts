import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { SyncReportState } from '../../core/seven-tv/seven-tv-delete.service';
import { RunItemStatus, RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { RunProgressPanel } from './run-progress-panel';

// Only the keys this panel itself translates — not the full app translation file. Texts are the
// real German ones (`massDelete` family, `web/public/i18n/de.json`), so an assertion reads as the
// sentence the user gets; per rule 12 the wording only identifies *which* notice appeared, it is
// never the thing under test.
const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen', close: 'Schließen' },
  massDelete: {
    progress: '{{ finished }} / {{ total }} verarbeitet',
    deleteFailedFallback: 'Löschen fehlgeschlagen',
    syncFailedTitle: 'Rückmeldung an EmotePurge fehlgeschlagen',
    syncFailed:
      'Die Emotes sind bei 7TV gelöscht, aber EmotePurge konnte es nicht vermerken. Normalerweise zieht sich das innerhalb einer Minute von selbst nach.',
    syncRetry: 'Erneut melden',
    syncRetrySucceeded: 'Rückmeldung erfolgreich nachgeholt.',
    summary: {
      counts: '{{done}} gelöscht · {{failed}} fehlgeschlagen · {{cancelled}} abgebrochen',
    },
  },
};

/** A queue row typed against the real `RunQueueItem` contract, so a wrong field name here is a
 *  compile error rather than a silently-ignored property. */
function queueItem(key: string, status: RunItemStatus): RunQueueItem {
  return { key, sevenTvEmoteId: `7tv-${key}`, name: `Emote-${key}`, status };
}

@Component({
  selector: 'app-host',
  imports: [RunProgressPanel],
  template: `
    <app-run-progress-panel
      [items]="items"
      [isRunning]="isRunning"
      [labelPrefix]="labelPrefix"
      [syncReport]="syncReport"
      [rateLimitPauseSeconds]="rateLimitPauseSeconds"
      (cancelled)="cancelledCount = cancelledCount + 1"
      (dismissed)="dismissedCount = dismissedCount + 1"
      (syncRetryRequested)="syncRetryRequestedCount = syncRetryRequestedCount + 1"
    >
      <!-- Stands in for a real host's projected content (protocol download, resync notice, …) —
           only its presence/absence in the DOM matters, never what it looks like. -->
      @if (projectRunActions) {
        <span run-actions>projected-marker</span>
      }
    </app-run-progress-panel>
  `,
})
class HostComponent {
  items: RunQueueItem[] = [];
  isRunning = false;
  labelPrefix: 'massDelete' | 'restore' | 'import' = 'massDelete';
  syncReport: SyncReportState = 'idle';
  rateLimitPauseSeconds: number | null = null;
  projectRunActions = false;
  cancelledCount = 0;
  dismissedCount = 0;
  syncRetryRequestedCount = 0;
}

interface Harness {
  fixture: ComponentFixture<HostComponent>;
  host: HostComponent;
  text(): string;
  button(label: string): HTMLButtonElement | null;
  /** `progressPercent` is `protected` on `RunProgressPanel` (template-only by design) — bracket
   *  access reaches it directly (compiler-checked against the real class, so a rename is a compile
   *  error) rather than inferring it back out of rendered markup. */
  progressPercent(): number;
}

describe('RunProgressPanel', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        HostComponent,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  // A fresh fixture per call, values applied before the one `detectChanges()` — mutating an
  // already-rendered instance's fields and detecting again did not reliably pick up the change in
  // this setup, matching the pattern `mass-delete-panel.spec.ts` uses.
  function render(setup: Partial<HostComponent> = {}): Harness {
    const fixture = TestBed.createComponent(HostComponent);
    Object.assign(fixture.componentInstance, setup);
    fixture.detectChanges();
    const nativeElement: HTMLElement = fixture.nativeElement;
    const instance = fixture.debugElement.query(By.directive(RunProgressPanel)).componentInstance;
    const panel = instance as RunProgressPanel;

    return {
      fixture,
      host: fixture.componentInstance,
      text: () => nativeElement.textContent ?? '',
      button: (label) =>
        Array.from(nativeElement.querySelectorAll('button')).find(
          (candidate) => candidate.textContent?.trim() === label,
        ) ?? null,
      progressPercent: () => panel['progressPercent'](),
    };
  }

  describe('progressPercent', () => {
    it('returns 0, not NaN, for an empty queue', () => {
      const dialog = render({ items: [] });

      expect(dialog.progressPercent()).toBe(0);
    });

    it('returns 0 while nothing in a non-empty queue has finished yet', () => {
      const dialog = render({
        items: [queueItem('a', 'pending'), queueItem('b', 'in-progress')],
      });

      expect(dialog.progressPercent()).toBe(0);
    });

    it('reports the finished (done + failed) fraction as a percentage of the total', () => {
      // 2 of 5 are finished — the other 3 (pending/in-progress) deliberately do not count, only
      // 'done'/'failed' do (see `finished` in run-progress-panel.ts).
      const dialog = render({
        items: [
          queueItem('a', 'done'),
          queueItem('b', 'failed'),
          queueItem('c', 'pending'),
          queueItem('d', 'pending'),
          queueItem('e', 'in-progress'),
        ],
      });

      expect(dialog.progressPercent()).toBe(40);
    });

    it('reports 100 once every item is done or failed', () => {
      const dialog = render({
        items: [queueItem('a', 'done'), queueItem('b', 'failed'), queueItem('c', 'done')],
      });

      expect(dialog.progressPercent()).toBe(100);
    });
  });

  describe('sync-report hint mapping', () => {
    it.each(['failed', 'partial'] as const)(
      "shows the sync-failed notice (title, body, retry) for syncReport '%s'",
      (syncReport) => {
        const dialog = render({
          items: [queueItem('a', 'done')],
          isRunning: false,
          syncReport,
        });

        expect(dialog.text()).toContain('Rückmeldung an EmotePurge fehlgeschlagen');
        expect(dialog.text()).toContain(
          'Die Emotes sind bei 7TV gelöscht, aber EmotePurge konnte es nicht vermerken.',
        );
        expect(dialog.button('Erneut melden')).not.toBeNull();
        // 'partial' is documented (run-progress-panel.ts) as sharing this exact hint with 'failed' —
        // same title, same body, same retry action, not merely "also something is shown".
        expect(dialog.text()).not.toContain('Rückmeldung erfolgreich nachgeholt.');
      },
    );

    it('shows the succeeded hint instead, once settled and not sync-failed', () => {
      const dialog = render({
        items: [queueItem('a', 'done')],
        isRunning: false,
        syncReport: 'succeeded',
      });

      expect(dialog.text()).toContain('Rückmeldung erfolgreich nachgeholt.');
      expect(dialog.text()).not.toContain('Rückmeldung an EmotePurge fehlgeschlagen');
      expect(dialog.button('Erneut melden')).toBeNull();
    });

    it('shows neither hint while still running, even if syncReport is already succeeded', () => {
      const dialog = render({
        items: [queueItem('a', 'done')],
        isRunning: true,
        syncReport: 'succeeded',
      });

      expect(dialog.text()).not.toContain('Rückmeldung erfolgreich nachgeholt.');
      expect(dialog.text()).not.toContain('Rückmeldung an EmotePurge fehlgeschlagen');
    });

    it.each(['idle', 'pending'] as const)(
      "shows neither hint for syncReport '%s'",
      (syncReport) => {
        const dialog = render({
          items: [queueItem('a', 'done')],
          isRunning: false,
          syncReport,
        });

        expect(dialog.text()).not.toContain('Rückmeldung erfolgreich nachgeholt.');
        expect(dialog.text()).not.toContain('Rückmeldung an EmotePurge fehlgeschlagen');
      },
    );
  });

  // The gate is a pinned contract (docs/DECISIONS.md, "2026-09-06 — Der `run-actions`-Slot des
  // Laufpanels ist post-run-only, und das ist ein Vertrag"): `run-progress-panel.ts` wraps its
  // summary row *and* `<ng-content select="[run-actions]" />` in one `@if (!isRunning() &&
  // total() > 0)`. What a host projects there must never be something the user needs mid-run.
  describe('run-actions projection gate', () => {
    it('keeps projected content and the summary row out of the DOM while running', () => {
      const dialog = render({
        items: [queueItem('a', 'done'), queueItem('b', 'pending')],
        isRunning: true,
        projectRunActions: true,
      });

      expect(dialog.fixture.nativeElement.querySelector('[run-actions]')).toBeNull();
      expect(dialog.text()).not.toContain('gelöscht');
    });

    it('keeps projected content out of the DOM once settled if the queue is empty', () => {
      const dialog = render({ items: [], isRunning: false, projectRunActions: true });

      expect(dialog.fixture.nativeElement.querySelector('[run-actions]')).toBeNull();
    });

    it('projects host content and shows the summary row once settled with a non-empty queue', () => {
      const dialog = render({
        items: [queueItem('a', 'done')],
        isRunning: false,
        projectRunActions: true,
      });

      expect(dialog.fixture.nativeElement.querySelector('[run-actions]')).not.toBeNull();
      expect(dialog.text()).toContain('1 gelöscht · 0 fehlgeschlagen · 0 abgebrochen');
    });
  });

  describe('outputs', () => {
    it('emits cancelled when the cancel button is clicked while running', () => {
      const dialog = render({ isRunning: true });

      dialog.button('Abbrechen')?.click();

      expect(dialog.host.cancelledCount).toBe(1);
    });

    it('emits dismissed when the close button is clicked once settled', () => {
      const dialog = render({ isRunning: false });

      dialog.button('Schließen')?.click();

      expect(dialog.host.dismissedCount).toBe(1);
    });

    it('emits syncRetryRequested when the retry action is clicked on a sync-failed hint', () => {
      const dialog = render({
        items: [queueItem('a', 'done')],
        isRunning: false,
        syncReport: 'failed',
      });

      dialog.button('Erneut melden')?.click();

      expect(dialog.host.syncRetryRequestedCount).toBe(1);
    });
  });
});
