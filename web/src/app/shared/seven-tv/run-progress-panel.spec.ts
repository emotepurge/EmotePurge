import { Component } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { RunItemStatus, RunQueueItem } from '../../core/seven-tv/seven-tv-run-engine';
import { SyncReportReason, SyncReportState } from '../../core/seven-tv/sync-report-outcome';
import { RunProgressPanel } from './run-progress-panel';

// Only the keys this panel itself translates — not the full app translation file. Texts are the
// real German ones (`massDelete` family, `web/public/i18n/de.json`), so an assertion reads as the
// sentence the user gets; per rule 12 the wording only identifies *which* notice appeared, it is
// never the thing under test.
const DE_TRANSLATIONS = {
  common: { cancel: 'Abbrechen', close: 'Schließen' },
  massDelete: {
    progress: '{{ finished }} / {{ total }} verarbeitet',
    progressBarLabel: 'Löschfortschritt',
    settling: 'Wird abgeschlossen…',
    deleteFailedFallback: 'Löschen fehlgeschlagen',
    unknownOutcome:
      'Unklar, ob gelöscht — 7TV hat nicht eindeutig geantwortet. Bitte im Set nachsehen.',
    syncFailedTitle: 'Rückmeldung an EmotePurge fehlgeschlagen',
    syncFailed:
      'Die Emotes sind bei 7TV gelöscht, aber EmotePurge konnte es nicht vermerken. Normalerweise zieht sich das innerhalb einer Minute von selbst nach.',
    syncRetry: 'Erneut melden',
    syncRetrySucceeded: 'Rückmeldung erfolgreich nachgeholt.',
    summary: {
      counts: '{{done}} gelöscht · {{failed}} fehlgeschlagen · {{cancelled}} abgebrochen',
    },
  },
  syncReportReason: {
    forbidden: 'Grund: Dein Konto darf dieses Set laut 7TV nicht mehr bearbeiten.',
    setNotFound: 'Grund: Das Set gibt es bei 7TV nicht mehr.',
    unavailable: 'Grund: EmotePurge oder 7TV war gerade nicht erreichbar.',
    channelMismatch:
      'Grund: Der erwartete Kanal nutzt dieses Set laut EmotePurge gerade nicht als aktives Set.',
    shortfall: 'Grund: Nicht alle Emotes waren in EmotePurge vermerkt.',
    other: 'Grund: Unerwarteter Fehler.',
  },
};

/** A queue row typed against the real `RunQueueItem` contract, so a wrong field name here is a
 *  compile error rather than a silently-ignored property. */
function queueItem(key: string, status: RunItemStatus): RunQueueItem {
  const ended = status === 'failed' || status === 'unknown';
  return {
    key,
    sevenTvEmoteId: `7tv-${key}`,
    name: `Emote-${key}`,
    status,
    completedSteps: status === 'done' ? 1 : 0,
    failedStep: ended ? 0 : null,
  };
}

/** The accname precedence this codebase relies on for an accessible name: `aria-labelledby`
 *  (joining the referenced elements' text, space-separated) beats `aria-label`, which beats plain
 *  `textContent`. Resolving it this way instead of reading `aria-label` directly keeps the
 *  assertion honest about what a screen reader would actually announce, not just one of the
 *  attributes that can produce it. */
function accessibleName(el: Element): string {
  const labelledBy = el.getAttribute('aria-labelledby');
  if (labelledBy) {
    const text = labelledBy
      .split(/\s+/)
      .filter(Boolean)
      .map((id) => el.ownerDocument.getElementById(id)?.textContent ?? '')
      .join(' ')
      .trim();
    if (text) {
      return text;
    }
  }
  const label = el.getAttribute('aria-label')?.trim();
  if (label) {
    return label;
  }
  return (el.textContent ?? '').trim();
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
      [syncReportReason]="syncReportReason"
      [rateLimitPauseSeconds]="rateLimitPauseSeconds"
      [dismissible]="dismissible"
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
  syncReportReason: SyncReportReason | null = null;
  rateLimitPauseSeconds: number | null = null;
  dismissible = true;
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
  /** The single `role="progressbar"` element (the track, not the fill) — queried by role like an
   *  assistive-tech user would land on it, rather than by a CSS class. */
  progressBar(): HTMLElement;
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

    return {
      fixture,
      host: fixture.componentInstance,
      text: () => nativeElement.textContent ?? '',
      button: (label) =>
        Array.from(nativeElement.querySelectorAll('button')).find(
          (candidate) => candidate.textContent?.trim() === label,
        ) ?? null,
      progressBar: () => nativeElement.querySelector('[role="progressbar"]') as HTMLElement,
    };
  }

  describe('progress bar', () => {
    it('exposes role="progressbar" with a range of 0..total, not a fixed 0-100', () => {
      const dialog = render({
        items: [queueItem('a', 'done'), queueItem('b', 'pending')],
      });

      expect(dialog.progressBar()).not.toBeNull();
      expect(dialog.progressBar().getAttribute('aria-valuemin')).toBe('0');
      expect(dialog.progressBar().getAttribute('aria-valuemax')).toBe('2');
    });

    it('reports aria-valuenow 0, not NaN, for an empty queue', () => {
      const dialog = render({ items: [] });

      expect(dialog.progressBar().getAttribute('aria-valuenow')).toBe('0');
      expect(dialog.progressBar().getAttribute('aria-valuemin')).toBe('0');
      expect(dialog.progressBar().getAttribute('aria-valuemax')).toBe('1');
    });

    it('reports aria-valuenow 0 while nothing in a non-empty queue has finished yet', () => {
      const dialog = render({
        items: [queueItem('a', 'pending'), queueItem('b', 'in-progress')],
      });

      expect(dialog.progressBar().getAttribute('aria-valuenow')).toBe('0');
      expect(dialog.progressBar().getAttribute('aria-valuemax')).toBe('2');
    });

    it('reports the finished (done + failed) count as aria-valuenow — pending/in-progress do not count', () => {
      // 2 of 5 are finished — the other 3 (pending/in-progress) deliberately do not count, only
      // 'done'/'failed' do (see `finished` in run-progress-panel.ts). This is the partial state.
      const dialog = render({
        items: [
          queueItem('a', 'done'),
          queueItem('b', 'failed'),
          queueItem('c', 'pending'),
          queueItem('d', 'pending'),
          queueItem('e', 'in-progress'),
        ],
      });

      expect(dialog.progressBar().getAttribute('aria-valuenow')).toBe('2');
      expect(dialog.progressBar().getAttribute('aria-valuemax')).toBe('5');
    });

    it('reports an integer count, not a rounded percentage (1 of 3 finished reads "1", not "33")', () => {
      const dialog = render({
        items: [queueItem('a', 'done'), queueItem('b', 'pending'), queueItem('c', 'pending')],
      });

      expect(dialog.progressBar().getAttribute('aria-valuenow')).toBe('1');
      expect(dialog.progressBar().getAttribute('aria-valuemax')).toBe('3');
    });

    it('reports aria-valuenow === aria-valuemax once every item is done or failed (the complete state)', () => {
      const dialog = render({
        items: [queueItem('a', 'done'), queueItem('b', 'failed'), queueItem('c', 'done')],
      });

      expect(dialog.progressBar().getAttribute('aria-valuenow')).toBe('3');
      expect(dialog.progressBar().getAttribute('aria-valuemax')).toBe('3');
      expect(dialog.progressBar().getAttribute('aria-valuenow')).toBe(
        dialog.progressBar().getAttribute('aria-valuemax'),
      );
    });

    it('does not report early completion for a large queue (199 of 200 finished)', () => {
      const dialog = render({
        items: [
          ...Array.from({ length: 199 }, (_, index) => queueItem(`done-${index}`, 'done')),
          queueItem('pending', 'pending'),
        ],
      });

      expect(dialog.progressBar().getAttribute('aria-valuenow')).toBe('199');
      expect(dialog.progressBar().getAttribute('aria-valuemax')).toBe('200');
      expect(dialog.progressBar().getAttribute('aria-valuenow')).not.toBe(
        dialog.progressBar().getAttribute('aria-valuemax'),
      );
    });

    it('does not report a false zero for a large queue (1 of 201 finished)', () => {
      const dialog = render({
        items: [
          queueItem('done', 'done'),
          ...Array.from({ length: 200 }, (_, index) => queueItem(`pending-${index}`, 'pending')),
        ],
      });

      expect(dialog.progressBar().getAttribute('aria-valuenow')).toBe('1');
      expect(dialog.progressBar().getAttribute('aria-valuemax')).toBe('201');
    });

    // The panel's own `role="status"` region already announces the textual progress; the bar's
    // accessible name must not repeat that as visible/live text — it lives only in `aria-label`, an
    // attribute that is not itself live-announced (see the aria-atomic comment on the
    // panel's status region in run-progress-panel.ts). What matters here is that assistive tech resolves a real,
    // translated name — not the raw i18n key, and not empty — for whichever run kind is showing.
    it('resolves a non-empty, translated accessible name via aria-label, not the raw key', () => {
      const dialog = render({
        items: [queueItem('a', 'done')],
        labelPrefix: 'massDelete',
      });

      const name = accessibleName(dialog.progressBar());

      expect(name).toBe(DE_TRANSLATIONS.massDelete.progressBarLabel);
    });

    it('sits in a non-atomic status region, so a progress tick does not re-read the bar', () => {
      const dialog = render({
        items: [queueItem('a', 'done'), queueItem('b', 'pending')],
      });

      const region = dialog.progressBar().closest('[role="status"]');

      expect(region).not.toBeNull();
      expect(region?.getAttribute('aria-atomic')).toBe('false');
    });
  });

  describe('failure list', () => {
    it('counts an unknown row as finished and lists it with its own unknown-outcome wording', () => {
      const unknown: RunQueueItem = {
        ...queueItem('b', 'unknown'),
        errorMessage: 'Keine Verbindung zu 7TV möglich (Netzwerkfehler).',
      };
      const dialog = render({
        items: [queueItem('a', 'done'), unknown, queueItem('c', 'pending')],
        isRunning: true,
      });

      expect(dialog.progressBar().getAttribute('aria-valuenow')).toBe('2');
      const entries = Array.from(
        dialog.fixture.nativeElement.querySelectorAll('[role="alert"] li'),
        (entry: Element) => entry.textContent?.trim(),
      );
      expect(entries).toEqual([`Emote-b: ${DE_TRANSLATIONS.massDelete.unknownOutcome}`]);
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

    // Spec E23: the reason is its own line inside the report notice, one per reason — and no line
    // at all without one.
    it.each([
      ['failed', 'forbidden'],
      ['failed', 'setNotFound'],
      ['failed', 'unavailable'],
      ['failed', 'other'],
      ['partial', 'channelMismatch'],
      ['partial', 'shortfall'],
    ] as const)(
      "shows the reason line for syncReport '%s' with reason '%s', and none without a reason",
      (syncReport, syncReportReason) => {
        const withReason = render({
          items: [queueItem('a', 'done')],
          isRunning: false,
          syncReport,
          syncReportReason,
        });
        expect(withReason.text()).toContain(DE_TRANSLATIONS.syncReportReason[syncReportReason]);

        const withoutReason = render({
          items: [queueItem('a', 'done')],
          isRunning: false,
          syncReport,
          syncReportReason: null,
        });
        expect(withoutReason.text()).toContain('Rückmeldung an EmotePurge fehlgeschlagen');
        expect(withoutReason.text()).not.toContain('Grund:');
      },
    );

    // Nachtrag N4, AK 40: a channel mismatch keeps its notice but loses the retry action — a
    // retry would only repeat the same mismatch; failed (any reason) and shortfall keep it.
    it('offers no retry for partial/channelMismatch, but keeps the notice and its reason', () => {
      const dialog = render({
        items: [queueItem('a', 'done')],
        isRunning: false,
        syncReport: 'partial',
        syncReportReason: 'channelMismatch',
      });

      expect(dialog.text()).toContain('Rückmeldung an EmotePurge fehlgeschlagen');
      expect(dialog.text()).toContain(DE_TRANSLATIONS.syncReportReason.channelMismatch);
      expect(dialog.button('Erneut melden')).toBeNull();
    });

    it.each([
      ['partial', 'shortfall'],
      ['failed', 'unavailable'],
      ['failed', 'forbidden'],
    ] as const)(
      "offers the retry for syncReport '%s' with reason '%s'",
      (syncReport, syncReportReason) => {
        const dialog = render({
          items: [queueItem('a', 'done')],
          isRunning: false,
          syncReport,
          syncReportReason,
        });

        dialog.button('Erneut melden')?.click();

        expect(dialog.host.syncRetryRequestedCount).toBe(1);
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

  // Finding 2: a host (import) gates Close on its own settlement signal, not merely on `isRunning`,
  // so the run stays in the dock — with its protocol and unload cover intact — until that signal
  // says the pending re-read is done. Delete/restore never pass `dismissible`, so it defaults to
  // `true` and their panels behave exactly as before.
  describe('dismissible', () => {
    it('shows neither Cancel nor Close while not running and not dismissible, and explains why', () => {
      const dialog = render({
        items: [queueItem('a', 'done')],
        isRunning: false,
        dismissible: false,
      });

      expect(dialog.button('Abbrechen')).toBeNull();
      expect(dialog.button('Schließen')).toBeNull();
      expect(dialog.text()).toContain(DE_TRANSLATIONS.massDelete.settling);
    });

    it('shows Close once the run is no longer running and dismissible again', () => {
      const dialog = render({
        items: [queueItem('a', 'done')],
        isRunning: false,
        dismissible: true,
      });

      expect(dialog.button('Schließen')).not.toBeNull();
      expect(dialog.text()).not.toContain(DE_TRANSLATIONS.massDelete.settling);
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
