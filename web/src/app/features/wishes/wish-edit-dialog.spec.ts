import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import de from '../../../../public/i18n/de.json';
import type { UpdateWishBody, Wish } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { WishEditDialog, WishEditDialogResult, wishEditBody } from './wish-edit-dialog';

function wish(overrides: Partial<Wish> = {}): Wish {
  return {
    id: 3,
    sevenTvEmoteId: 'A',
    emoteName: 'Pag',
    alias: 'Pag',
    source: 'manual',
    status: 'approved',
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

function apiError(status: number, errorCode: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: { errorCode } });
}

describe('wishEditBody', () => {
  const base = wish();

  it('sends only the changed fields', () => {
    expect(wishEditBody(base, 'Pag', 10)).toEqual({ durationDays: 10 });
    expect(wishEditBody(base, 'Pog', 14)).toEqual({ alias: 'Pog' });
    expect(wishEditBody(base, 'Pog', 10)).toEqual({ alias: 'Pog', durationDays: 10 });
  });

  it('is null without a change', () => {
    expect(wishEditBody(base, 'Pag', 14)).toBeNull();
  });
});

describe('WishEditDialog', () => {
  let updates: { id: number; body: UpdateWishBody; answer: Subject<Wish> }[];
  let closed: (WishEditDialogResult | undefined)[];
  let dialogRef: { close: ReturnType<typeof vi.fn>; disableClose: boolean };
  let fixture: ComponentFixture<WishEditDialog>;

  beforeEach(() => {
    updates = [];
    closed = [];
    dialogRef = {
      close: vi.fn((result?: WishEditDialogResult) => closed.push(result)),
      disableClose: false,
    };
    TestBed.configureTestingModule({
      imports: [
        WishEditDialog,
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
            update: vi.fn((_channel: string, id: number, body: UpdateWishBody) => {
              const answer = new Subject<Wish>();
              updates.push({ id, body, answer });
              return answer;
            }),
          },
        },
      ],
    });
    fixture = TestBed.createComponent(WishEditDialog);
    fixture.detectChanges();
  });

  afterEach(() => fixture.destroy());

  function input(id: string): HTMLInputElement {
    return fixture.nativeElement.querySelector(`#${id}`) as HTMLInputElement;
  }

  function type(id: string, value: string): void {
    const element = input(id);
    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function submit(): HTMLButtonElement {
    return [...fixture.nativeElement.querySelectorAll('button')].find(
      (button) => (button as HTMLButtonElement).textContent?.trim() === de.wishes.edit.submit,
    ) as HTMLButtonElement;
  }

  it('starts with the wish’s own values and blocks sending with the reason', () => {
    expect(input('wish-edit-alias').value).toBe('Pag');
    expect(input('wish-edit-duration').value).toBe('14');
    expect(submit().disabled).toBe(true);
    const reason = fixture.nativeElement.querySelector('#wish-edit-blocked');
    expect(reason.textContent).toContain(de.wishes.edit.blocked.unchanged);
    expect(submit().getAttribute('aria-describedby')).toBe('wish-edit-blocked');
  });

  it('sends only the duration when only it changed', () => {
    type('wish-edit-duration', '10');
    expect(submit().disabled).toBe(false);
    submit().click();
    expect(updates).toHaveLength(1);
    expect(updates[0]).toMatchObject({ id: 3, body: { durationDays: 10 } });
    expect(updates[0].body).not.toHaveProperty('alias');
    expect(dialogRef.disableClose).toBe(true);
  });

  it('sends only the alias when only it changed', () => {
    type('wish-edit-alias', 'Pog');
    submit().click();
    expect(updates[0].body).toEqual({ alias: 'Pog' });
  });

  it('blocks an invalid alias or duration at its field and sends nothing', () => {
    type('wish-edit-alias', 'a b');
    expect(input('wish-edit-alias').getAttribute('aria-invalid')).toBe('true');
    expect(submit().disabled).toBe(true);
    submit().click();
    type('wish-edit-alias', 'Pog');
    type('wish-edit-duration', '0');
    expect(input('wish-edit-duration').getAttribute('aria-invalid')).toBe('true');
    expect(submit().disabled).toBe(true);
    submit().click();
    expect(updates).toEqual([]);
  });

  it('is blocked again after the values go back to the wish’s own', () => {
    type('wish-edit-duration', '10');
    type('wish-edit-duration', '14');
    expect(submit().disabled).toBe(true);
  });

  it('sends nothing a second time while the request is out', () => {
    type('wish-edit-duration', '10');
    submit().click();
    submit().click();
    expect(updates).toHaveLength(1);
  });

  it('closes with the wish the server answered with', () => {
    type('wish-edit-duration', '10');
    submit().click();
    const edited = wish({ durationDays: 10 });
    updates[0].answer.next(edited);
    expect(closed).toEqual([{ kind: 'edited', wish: edited }]);
  });

  it('closes as stale on a state conflict or a vanished wish', () => {
    type('wish-edit-duration', '10');
    submit().click();
    updates[0].answer.error(apiError(409, 'wish_state_conflict'));
    expect(closed).toEqual([{ kind: 'stale', errorKey: 'errors.api.wish_state_conflict' }]);
  });

  it('closes as stale on a vanished wish', () => {
    type('wish-edit-duration', '10');
    submit().click();
    updates[0].answer.error(apiError(404, 'wish_not_found'));
    expect(closed).toEqual([{ kind: 'stale', errorKey: 'errors.api.wish_not_found' }]);
  });

  it('shows a refused duration at its field, frees the dialog and clears it on the next edit', () => {
    type('wish-edit-duration', '10');
    submit().click();
    updates[0].answer.error(apiError(400, 'wish_duration_out_of_range'));
    fixture.detectChanges();
    expect(input('wish-edit-duration').getAttribute('aria-invalid')).toBe('true');
    expect(dialogRef.disableClose).toBe(false);
    expect(closed).toEqual([]);
    type('wish-edit-duration', '9');
    expect(input('wish-edit-duration').getAttribute('aria-invalid')).toBeNull();
  });

  it('shows a refused alias at its field', () => {
    type('wish-edit-alias', 'Pog');
    submit().click();
    updates[0].answer.error(apiError(400, 'wish_alias_invalid'));
    fixture.detectChanges();
    expect(input('wish-edit-alias').getAttribute('aria-invalid')).toBe('true');
  });

  it('blocks the form when the feature is off', () => {
    type('wish-edit-duration', '10');
    submit().click();
    updates[0].answer.error(apiError(404, 'wishes_disabled'));
    fixture.detectChanges();
    expect(submit().disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain(de.errors.api.wishes_disabled);
    expect(closed).toEqual([]);
  });

  it('shows another failure as a banner and keeps the dialog open', () => {
    type('wish-edit-duration', '10');
    submit().click();
    updates[0].answer.error(new HttpErrorResponse({ status: 500 }));
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role="alert"]')).not.toBeNull();
    expect(submit().disabled).toBe(false);
    expect(closed).toEqual([]);
  });
});
