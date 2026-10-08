import { WritableSignal, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { SevenTvRunArbiter } from '../../core/seven-tv/seven-tv-run-arbiter';
import type { TagRunFeedback } from './tag-run-actions';
import { TagRunNoticeSink } from './tag-run-notice-sink';
import { TagRunOrphanNotice } from './tag-run-orphan-notice';

const DE = {
  common: { close: 'Schließen' },
  massDelete: {
    abortedByLock: 'Es wurde nichts gelöscht.',
    setChangedDuringConfirm: 'Inzwischen ist ein anderes Set ausgewählt.',
  },
  tags: {
    errors: {
      setChanged: 'Das aktive Set hat gewechselt — Seite neu laden.',
      reportFailed: 'EmotePurge konnte den Stand des Tags nicht vermerken.',
      retry: 'Erneut versuchen',
    },
  },
};

describe('TagRunOrphanNotice', () => {
  let fixture: ComponentFixture<TagRunOrphanNotice>;
  let sink: TagRunNoticeSink;
  let startLocked: WritableSignal<boolean>;
  let completed: number;
  let feedback: TagRunFeedback[];

  beforeEach(async () => {
    startLocked = signal(false);
    await TestBed.configureTestingModule({
      imports: [
        TagRunOrphanNotice,
        TranslocoTestingModule.forRoot({
          langs: { de: DE },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [{ provide: SevenTvRunArbiter, useValue: { startLocked } }],
    }).compileComponents();
    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));

    sink = TestBed.inject(TagRunNoticeSink);
    fixture = TestBed.createComponent(TagRunOrphanNotice);
    fixture.componentRef.setInput('channelName', 'handofblood');
    completed = 0;
    feedback = [];
    fixture.componentInstance.completed.subscribe(() => completed++);
    fixture.componentInstance.feedback.subscribe((value) => feedback.push(value));
    fixture.detectChanges();
  });

  function host(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function region(): HTMLElement {
    return host().querySelector('[role="status"]')!;
  }

  function button(name: string): HTMLButtonElement | undefined {
    return [...host().querySelectorAll('button')].find(
      (candidate) => candidate.textContent?.trim() === name,
    );
  }

  it('keeps an empty status region mounted while there is nothing to say', () => {
    expect(region()).not.toBeNull();
    expect(region().textContent?.trim()).toBe('');
    expect(host().querySelectorAll('button')).toHaveLength(0);
  });

  it("speaks the sink's notice for this channel, lead sentence first, and offers to close it", () => {
    sink.raise('handofblood', {
      leadKey: 'massDelete.abortedByLock',
      key: 'massDelete.setChangedDuringConfirm',
    });
    fixture.detectChanges();

    expect(region().textContent?.replace(/\s+/g, ' ').trim()).toBe(
      'Es wurde nichts gelöscht. Inzwischen ist ein anderes Set ausgewählt.',
    );
    expect(button('Erneut versuchen')).toBeUndefined();

    button('Schließen')!.click();
    fixture.detectChanges();

    expect(sink.notice()).toBeNull();
    expect(region().textContent?.trim()).toBe('');
  });

  it("ignores another channel's notice and events", () => {
    sink.raise('other', { key: 'tags.errors.setChanged' });
    sink.feedback('other', 'tags.feedback.removedNothing', {});
    sink.completed('other');
    fixture.detectChanges();

    expect(region().textContent?.trim()).toBe('');
    expect(feedback).toEqual([]);
    expect(completed).toBe(0);
  });

  it('runs the retry once, clearing the notice, and describes the button by the region', () => {
    const retry = vi.fn();
    sink.raise('handofblood', { key: 'tags.errors.reportFailed', retry });
    fixture.detectChanges();

    const retryButton = button('Erneut versuchen')!;
    expect(retryButton.getAttribute('aria-describedby')).toBe(region().id);
    retryButton.click();

    expect(retry).toHaveBeenCalledOnce();
    expect(sink.notice()).toBeNull();
  });

  it('locks the retry while any 7TV run holds the start', () => {
    const retry = vi.fn();
    sink.raise('handofblood', { key: 'tags.errors.reportFailed', retry });
    startLocked.set(true);
    fixture.detectChanges();

    expect(button('Erneut versuchen')!.disabled).toBe(true);
    // A click already dispatched when the lock arrived is refused by the handler itself.
    (fixture.componentInstance as unknown as { retry(n: unknown): void }).retry(
      sink.notice()!.notice,
    );
    expect(retry).not.toHaveBeenCalled();
    expect(sink.notice()).not.toBeNull();
  });

  it("passes this channel's feedback and completion on to the page", () => {
    sink.feedback('handofblood', 'tags.feedback.removedNothing', { tag: 'Stronghold' });
    sink.completed('handofblood');

    expect(feedback).toEqual([
      { key: 'tags.feedback.removedNothing', params: { tag: 'Stronghold' } },
    ]);
    expect(completed).toBe(1);
  });
});
