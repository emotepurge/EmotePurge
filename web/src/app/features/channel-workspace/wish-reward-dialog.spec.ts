import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import en from '../../../../public/i18n/en.json';
import type {
  ConfirmWishRewardBody,
  UpdateWishRewardBody,
  WishReward,
} from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import {
  WishRewardDialog,
  type WishRewardDialogData,
  type WishRewardDialogResult,
} from './wish-reward-dialog';

const SHOWN = {
  sessionId: 'session-a',
  startedAtUtc: '2026-11-19T11:55:00Z',
  expiresAtUtc: '2026-11-19T12:05:00Z',
  candidate: {
    candidateId: 'candidate-1',
    text: 'https://7tv.app/emotes/01FDGSXVJG0004RTSM3QHRP7QN',
    login: 'viewer_one',
    seenAtUtc: '2026-11-19T11:58:00Z',
  },
};

function reward(over: Partial<WishReward> = {}): WishReward {
  return {
    id: 7,
    label: 'Emote-Wunsch',
    defaultDurationDays: 14,
    isActive: true,
    createdAtUtc: '2026-11-19T12:00:00Z',
    lastRedemptionSeenAtUtc: null,
    ...over,
  };
}

function apiError(status: number, errorCode: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: { errorCode } });
}

describe('WishRewardDialog', () => {
  let confirms: { body: ConfirmWishRewardBody; answer: Subject<WishReward> }[];
  let updates: { id: number; body: UpdateWishRewardBody; answer: Subject<WishReward> }[];
  let closed: (WishRewardDialogResult | undefined)[];
  let dialogRef: { close: ReturnType<typeof vi.fn>; disableClose: boolean };
  let fixture: ComponentFixture<WishRewardDialog>;

  function create(data: WishRewardDialogData): void {
    TestBed.configureTestingModule({
      imports: [
        WishRewardDialog,
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: dialogRef },
        {
          provide: WishService,
          useValue: {
            confirmLearning: (_channel: string, body: ConfirmWishRewardBody) => {
              const answer = new Subject<WishReward>();
              confirms.push({ body, answer });
              return answer;
            },
            updateReward: (_channel: string, id: number, body: UpdateWishRewardBody) => {
              const answer = new Subject<WishReward>();
              updates.push({ id, body, answer });
              return answer;
            },
          },
        },
      ],
    });
    fixture = TestBed.createComponent(WishRewardDialog);
    fixture.detectChanges();
  }

  function createConfirm(): void {
    create({
      mode: 'confirm',
      channelName: 'sensitron',
      learning: SHOWN,
      defaultDurationDays: 21,
    });
  }

  function el(): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function type(id: 'label' | 'duration', value: string): void {
    const input = el().querySelector<HTMLInputElement>(`#wish-reward-${id}`)!;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function saveButton(): HTMLButtonElement {
    return Array.from(el().querySelectorAll('button')).find(
      (button) => button.textContent?.trim() === en.wishes.settings.rewards.dialog.save,
    )!;
  }

  function blockedText(): string | null {
    return el().querySelector('#wish-reward-blocked')?.textContent?.trim() ?? null;
  }

  beforeEach(() => {
    vi.useFakeTimers({ toFake: ['Date', 'setInterval', 'clearInterval'] });
    vi.setSystemTime(new Date('2026-11-19T12:00:00Z'));
    confirms = [];
    updates = [];
    closed = [];
    dialogRef = {
      close: vi.fn((result?: WishRewardDialogResult) => closed.push(result)),
      disableClose: false,
    };
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  describe('confirming a learned reward', () => {
    it('shows the candidate it was opened with and starts with the channel duration', () => {
      createConfirm();

      expect(el().textContent).toContain(SHOWN.candidate.text);
      expect(el().textContent).toContain('viewer_one');
      expect(el().querySelector<HTMLInputElement>('#wish-reward-duration')!.value).toBe('21');
    });

    it('blocks saving without a label and names the reason beside the button', () => {
      createConfirm();

      expect(saveButton().disabled).toBe(true);
      expect(saveButton().getAttribute('aria-describedby')).toBe('wish-reward-blocked');
      expect(blockedText()).toBe(en.wishes.settings.rewards.dialog.blocked.label);
    });

    it('sends exactly the ids it showed, with the trimmed label and the duration', () => {
      createConfirm();
      type('label', '  Emote-Wunsch ');
      type('duration', '30');

      saveButton().click();

      expect(confirms).toHaveLength(1);
      expect(confirms[0].body).toEqual({
        sessionId: 'session-a',
        candidateId: 'candidate-1',
        label: 'Emote-Wunsch',
        defaultDurationDays: 30,
      });
      expect(dialogRef.disableClose).toBe(true);

      confirms[0].answer.next(reward());
      expect(closed).toEqual([{ kind: 'saved', reward: reward() }]);
    });

    it('stays open on a stale learning, says so, and closes with stale only on reload', () => {
      createConfirm();
      type('label', 'Emote-Wunsch');
      saveButton().click();

      confirms[0].answer.error(apiError(409, 'wish_learning_stale'));
      fixture.detectChanges();

      expect(closed).toEqual([]);
      expect(el().querySelector('[role="alert"]')?.textContent).toContain(
        en.errors.api.wish_learning_stale,
      );
      expect(saveButton().disabled).toBe(true);
      expect(blockedText()).toBe(en.wishes.settings.rewards.dialog.blocked.stale);
      expect(dialogRef.disableClose).toBe(false);

      const reload = Array.from(el().querySelectorAll('button')).find(
        (button) => button.textContent?.trim() === en.wishes.settings.rewards.reload,
      )!;
      reload.click();

      expect(closed).toEqual([{ kind: 'stale' }]);
      expect(confirms).toHaveLength(1);
    });

    it('closes with gone when the candidate no longer exists', () => {
      createConfirm();
      type('label', 'Emote-Wunsch');
      saveButton().click();

      confirms[0].answer.error(apiError(409, 'wish_reward_no_candidate'));

      expect(closed).toEqual([{ kind: 'gone', errorKey: 'errors.api.wish_reward_no_candidate' }]);
    });

    it('keeps a refusal it cannot resolve in the dialog', () => {
      createConfirm();
      type('label', 'Emote-Wunsch');
      saveButton().click();

      confirms[0].answer.error(apiError(409, 'wish_reward_already_known'));
      fixture.detectChanges();

      expect(closed).toEqual([]);
      expect(el().textContent).toContain(en.errors.api.wish_reward_already_known);
    });

    it('closes with restricted when the caller may not write', () => {
      createConfirm();
      type('label', 'Emote-Wunsch');
      saveButton().click();

      confirms[0].answer.error(apiError(403, 'wish_settings_restricted'));

      expect(closed).toEqual([{ kind: 'restricted' }]);
    });

    it('blocks saving once the session has run out by its clock', () => {
      createConfirm();
      type('label', 'Emote-Wunsch');
      expect(saveButton().disabled).toBe(false);

      vi.advanceTimersByTime(5 * 60 * 1000);
      fixture.detectChanges();

      expect(saveButton().disabled).toBe(true);
      expect(blockedText()).toBe(en.wishes.settings.rewards.dialog.blocked.expired);
      saveButton().click();
      expect(confirms).toHaveLength(0);
    });
  });

  describe('editing a reward', () => {
    beforeEach(() => {
      create({ mode: 'edit', channelName: 'sensitron', reward: reward() });
    });

    it('starts with the reward and blocks saving until something changes', () => {
      expect(el().querySelector<HTMLInputElement>('#wish-reward-label')!.value).toBe(
        'Emote-Wunsch',
      );
      expect(saveButton().disabled).toBe(true);
      expect(blockedText()).toBe(en.wishes.settings.rewards.dialog.blocked.unchanged);
    });

    it('sends only the changed field', () => {
      type('duration', '30');
      saveButton().click();

      expect(updates).toEqual([
        { id: 7, body: { defaultDurationDays: 30 }, answer: expect.anything() },
      ]);
    });

    it('names a label that is too long at the field', () => {
      type('label', 'x'.repeat(51));

      const input = el().querySelector<HTMLInputElement>('#wish-reward-label')!;
      expect(input.getAttribute('aria-invalid')).toBe('true');
      expect(input.getAttribute('aria-describedby')).toContain('wish-reward-label-error');
      expect(saveButton().disabled).toBe(true);
    });

    it('closes with gone when the reward was forgotten meanwhile', () => {
      type('duration', '30');
      saveButton().click();

      updates[0].answer.error(apiError(404, 'wish_reward_not_found'));

      expect(closed).toEqual([{ kind: 'gone', errorKey: 'errors.api.wish_reward_not_found' }]);
    });
  });
});
