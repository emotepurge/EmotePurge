import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import de from '../../../../public/i18n/de.json';
import type { ReAddWishBody, Wish, WishSettingsRead } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { WishReAddDialog, WishReAddDialogResult, wishReAddBody } from './wish-readd-dialog';

function wish(overrides: Partial<Wish> = {}): Wish {
  return {
    id: 3,
    sevenTvEmoteId: 'A',
    emoteName: 'Pag',
    alias: 'Pag',
    source: 'manual',
    status: 'removed',
    statusReason: null,
    requesterLogin: null,
    durationDays: 14,
    createdAtUtc: '2026-11-01T10:00:00Z',
    approvedAtUtc: null,
    startsAtUtc: null,
    startsAtAuthoritative: false,
    probationStartsAtUtc: null,
    expiresAtUtc: null,
    endedAtUtc: null,
    slotId: null,
    slotAddedAtUtc: null,
    imageUrl: null,
    linkOrigin: null,
    extensionCount: 0,
    permanent: false,
    botOwned: false,
    botWritePending: false,
    botNextAttemptAtUtc: null,
    removalQueuedAtUtc: null,
    mode: 'manual',
    ...overrides,
  };
}

function apiError(status: number, body: Record<string, unknown>): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

describe('wishReAddBody', () => {
  it('never carries a duration for permanent, and always one for timed', () => {
    expect(wishReAddBody('permanent', 7)).toEqual({ mode: 'permanent' });
    expect(wishReAddBody('permanent', null)).toEqual({ mode: 'permanent' });
    expect(wishReAddBody('timed', 7)).toEqual({ mode: 'timed', durationDays: 7 });
    expect(wishReAddBody('timed', null)).toBeNull();
  });
});

describe('WishReAddDialog', () => {
  let reAdds: { id: number; body: ReAddWishBody; answer: Subject<Wish> }[];
  let settings: Subject<WishSettingsRead>;
  let closed: (WishReAddDialogResult | undefined)[];
  let dialogRef: { close: ReturnType<typeof vi.fn>; disableClose: boolean };
  let fixture: ComponentFixture<WishReAddDialog>;

  beforeEach(() => {
    reAdds = [];
    closed = [];
    settings = new Subject();
    dialogRef = {
      close: vi.fn((result?: WishReAddDialogResult) => closed.push(result)),
      disableClose: false,
    };
    TestBed.configureTestingModule({
      imports: [
        WishReAddDialog,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: DIALOG_DATA, useValue: { channelName: 'chan', wish: wish() } },
        { provide: DialogRef, useValue: dialogRef },
        {
          provide: WishService,
          useValue: {
            getSettings: vi.fn(() => settings),
            reAdd: vi.fn((_channel: string, id: number, body: ReAddWishBody) => {
              const answer = new Subject<Wish>();
              reAdds.push({ id, body, answer });
              return answer;
            }),
          },
        },
      ],
    });
    fixture = TestBed.createComponent(WishReAddDialog);
    fixture.detectChanges();
    settings.next({
      defaultDurationDays: 21,
      probationLeadDays: 3,
      protectionThresholdPerDay: null,
      botModeEnabled: false,
      botSuspendedReason: null,
      pendingExpiryDays: 14,
      canEdit: true,
    });
    fixture.detectChanges();
  });

  afterEach(() => fixture.destroy());

  function input(id: string): HTMLInputElement | null {
    return fixture.nativeElement.querySelector(`#${id}`);
  }

  function type(value: string): void {
    const element = input('wish-readd-duration') as HTMLInputElement;
    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function button(label: string): HTMLButtonElement {
    return [...fixture.nativeElement.querySelectorAll('button')].find(
      (candidate) => (candidate as HTMLButtonElement).textContent?.trim() === label,
    ) as HTMLButtonElement;
  }

  function choose(label: string): void {
    const radio = [...fixture.nativeElement.querySelectorAll('label')]
      .find((candidate) => (candidate as HTMLLabelElement).textContent?.trim() === label)
      ?.querySelector('input') as HTMLInputElement;
    radio.click();
    radio.dispatchEvent(new Event('change'));
    fixture.detectChanges();
  }

  it('starts timed with the channel default duration', () => {
    expect(input('wish-readd-duration')?.value).toBe('21');
    expect(button(de.wishes.readd.submit).disabled).toBe(false);
  });

  it('keeps a duration the manager typed when the default arrives later', () => {
    fixture.destroy();
    fixture = TestBed.createComponent(WishReAddDialog);
    fixture.detectChanges();
    type('5');
    settings.next({
      defaultDurationDays: 21,
      probationLeadDays: 3,
      protectionThresholdPerDay: null,
      botModeEnabled: false,
      botSuspendedReason: null,
      pendingExpiryDays: 14,
      canEdit: true,
    });
    fixture.detectChanges();
    expect(input('wish-readd-duration')?.value).toBe('5');
  });

  it('sends the timed mode with the duration', () => {
    type('30');
    button(de.wishes.readd.submit).click();
    expect(reAdds[0]).toMatchObject({ id: 3, body: { mode: 'timed', durationDays: 30 } });
    expect(dialogRef.disableClose).toBe(true);
  });

  it('sends permanent without a duration, hides the field and says the wish is carried as kept', () => {
    choose(de.wishes.readd.mode.permanent);
    expect(input('wish-readd-duration')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain(de.wishes.readd.permanentHint);
    button(de.wishes.readd.submit).click();
    expect(reAdds[0].body).toEqual({ mode: 'permanent' });
  });

  it('blocks timed without a valid duration, but not permanent', () => {
    type('0');
    expect(button(de.wishes.readd.submit).disabled).toBe(true);
    expect(fixture.nativeElement.querySelector('#wish-readd-blocked')).not.toBeNull();
    choose(de.wishes.readd.mode.permanent);
    expect(button(de.wishes.readd.submit).disabled).toBe(false);
  });

  it('sends nothing a second time while the request is out', () => {
    button(de.wishes.readd.submit).click();
    button(de.wishes.readd.submit).click();
    expect(reAdds).toHaveLength(1);
  });

  it('closes with the wish the server answered with', () => {
    button(de.wishes.readd.submit).click();
    const readded = wish({ status: 'active' });
    reAdds[0].answer.next(readded);
    expect(closed).toEqual([{ kind: 'readded', wish: readded }]);
  });

  it('names the running wish and goes to it', () => {
    button(de.wishes.readd.submit).click();
    reAdds[0].answer.error(apiError(409, { errorCode: 'wish_already_running', wishId: 8 }));
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain(de.wishes.readd.running);
    expect(dialogRef.disableClose).toBe(false);
    expect(closed).toEqual([]);
    button(de.wishes.dialog.toWish).click();
    expect(closed).toEqual([{ kind: 'jump', wishId: 8 }]);
  });

  it('shows an error without the way on when the running wish is not named', () => {
    button(de.wishes.readd.submit).click();
    reAdds[0].answer.error(apiError(409, { errorCode: 'wish_already_running' }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="alert"]')).not.toBeNull();
    expect(button(de.wishes.dialog.toWish)).toBeUndefined();
  });

  it('closes as stale on a state conflict and on a vanished wish', () => {
    button(de.wishes.readd.submit).click();
    reAdds[0].answer.error(apiError(409, { errorCode: 'wish_state_conflict' }));
    expect(closed).toEqual([{ kind: 'stale', errorKey: 'errors.api.wish_state_conflict' }]);
  });

  it('closes as stale on a vanished wish', () => {
    button(de.wishes.readd.submit).click();
    reAdds[0].answer.error(apiError(404, { errorCode: 'wish_not_found' }));
    expect(closed).toEqual([{ kind: 'stale', errorKey: 'errors.api.wish_not_found' }]);
  });

  it('shows a refused duration at its field and clears it on the next edit', () => {
    button(de.wishes.readd.submit).click();
    reAdds[0].answer.error(apiError(400, { errorCode: 'wish_duration_out_of_range' }));
    fixture.detectChanges();
    expect(input('wish-readd-duration')?.getAttribute('aria-invalid')).toBe('true');
    type('9');
    expect(input('wish-readd-duration')?.getAttribute('aria-invalid')).toBeNull();
  });

  it('blocks the form when the feature is off', () => {
    button(de.wishes.readd.submit).click();
    reAdds[0].answer.error(apiError(404, { errorCode: 'wishes_disabled' }));
    fixture.detectChanges();
    expect(button(de.wishes.readd.submit).disabled).toBe(true);
    expect(closed).toEqual([]);
  });

  it('shows another failure as a banner and keeps the dialog open', () => {
    button(de.wishes.readd.submit).click();
    reAdds[0].answer.error(new HttpErrorResponse({ status: 500 }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="alert"]')).not.toBeNull();
    expect(button(de.wishes.readd.submit).disabled).toBe(false);
  });
});
