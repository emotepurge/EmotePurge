import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import de from '../../../../public/i18n/de.json';
import {
  SevenTvEmoteLookup,
  SevenTvEmoteLookupResult,
} from '../../core/seven-tv/seven-tv-emote-lookup';
import type {
  CreatedWish,
  CreateWishBody,
  Wish,
  WishDetail,
  WishSettingsRead,
} from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import {
  WISH_ENTRY_LOOKUP_DEBOUNCE_MS,
  WishEntryDialog,
  WishEntryDialogResult,
} from './wish-entry-dialog';

const A = '01EZPHAJ3R000C438200A44F19';
const B = '01F5VW2TKR0003RCV2Z6JBHCST';

function t(path: string): string {
  return path
    .split('.')
    .reduce<unknown>((node, key) => (node as Record<string, unknown>)[key], de) as string;
}

function wish(id: number, overrides: Partial<Wish> = {}): Wish {
  return {
    id,
    sevenTvEmoteId: A,
    emoteName: 'Pag',
    alias: 'Pag',
    source: 'manual',
    status: 'approved',
    statusReason: null,
    requesterLogin: null,
    durationDays: 14,
    createdAtUtc: '2026-11-01T10:00:00Z',
    approvedAtUtc: '2026-11-01T10:00:00Z',
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

describe('WishEntryDialog', () => {
  let lookups: { id: string; answer: Subject<SevenTvEmoteLookupResult> }[];
  let creates: { channel: string; body: CreateWishBody; answer: Subject<CreatedWish> }[];
  let gets: { id: number; answer: Subject<WishDetail> }[];
  let extends_: { id: number; answer: Subject<Wish> }[];
  let settings: Subject<WishSettingsRead>;
  let closed: (WishEntryDialogResult | undefined)[];
  let dialogRef: { close: ReturnType<typeof vi.fn>; disableClose: boolean };
  let fixture: ComponentFixture<WishEntryDialog>;

  beforeEach(() => {
    vi.useFakeTimers();
    lookups = [];
    creates = [];
    gets = [];
    extends_ = [];
    closed = [];
    settings = new Subject();
    dialogRef = {
      close: vi.fn((result?: WishEntryDialogResult) => closed.push(result)),
      disableClose: false,
    };
    TestBed.configureTestingModule({
      imports: [
        WishEntryDialog,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: DIALOG_DATA, useValue: { channelName: 'chan' } },
        { provide: DialogRef, useValue: dialogRef },
        {
          provide: SevenTvEmoteLookup,
          useValue: {
            isKnownStill: () => false,
            lookup: vi.fn((id: string) => {
              const answer = new Subject<SevenTvEmoteLookupResult>();
              lookups.push({ id, answer });
              return answer;
            }),
          },
        },
        {
          provide: WishService,
          useValue: {
            getSettings: vi.fn(() => settings),
            create: vi.fn((channel: string, body: CreateWishBody) => {
              const answer = new Subject<CreatedWish>();
              creates.push({ channel, body, answer });
              return answer;
            }),
            get: vi.fn((_channel: string, id: number) => {
              const answer = new Subject<WishDetail>();
              gets.push({ id, answer });
              return answer;
            }),
            extend: vi.fn((_channel: string, id: number) => {
              const answer = new Subject<Wish>();
              extends_.push({ id, answer });
              return answer;
            }),
          },
        },
      ],
    });
    fixture = TestBed.createComponent(WishEntryDialog);
    fixture.detectChanges();
    settings.next({
      defaultDurationDays: 14,
      probationLeadDays: 3,
      protectionThresholdPerDay: null,
      botModeEnabled: false,
      botSuspendedReason: null,
      canEdit: true,
    });
    fixture.detectChanges();
  });

  afterEach(() => {
    fixture.destroy();
    vi.useRealTimers();
  });

  function input(id: string): HTMLInputElement {
    return fixture.nativeElement.querySelector(`#${id}`) as HTMLInputElement;
  }

  function type(id: string, value: string): void {
    const element = input(id);
    element.value = value;
    element.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function typeEmote(value: string): void {
    type('wish-entry-emote', value);
  }

  function waitDebounce(): void {
    vi.advanceTimersByTime(WISH_ENTRY_LOOKUP_DEBOUNCE_MS);
    fixture.detectChanges();
  }

  function answer(index: number, result: SevenTvEmoteLookupResult): void {
    lookups[index].answer.next(result);
    fixture.detectChanges();
  }

  function button(label: string): HTMLButtonElement {
    const found = [...fixture.nativeElement.querySelectorAll('button')].find(
      (candidate) => (candidate as HTMLButtonElement).textContent?.trim() === label,
    );
    expect(found).toBeDefined();
    return found as HTMLButtonElement;
  }

  function hasButton(label: string): boolean {
    return [...fixture.nativeElement.querySelectorAll('button')].some(
      (candidate) => (candidate as HTMLButtonElement).textContent?.trim() === label,
    );
  }

  function submitButton(): HTMLButtonElement {
    return button(t('wishes.entry.submit'));
  }

  function blockedReason(): string | null {
    const describedBy = submitButton().getAttribute('aria-describedby');
    return describedBy
      ? (fixture.nativeElement.querySelector(`#${describedBy}`)?.textContent?.trim() ?? null)
      : null;
  }

  function readText(): string {
    return fixture.nativeElement.querySelector('#wish-entry-read').textContent.trim();
  }

  /** A valid emote looked up and found, ready to send. */
  function foundEmote(id = A, name = 'Pag'): void {
    typeEmote(id);
    waitDebounce();
    answer(lookups.length - 1, { id, kind: 'found', name, animated: false });
  }

  it('prefills the duration with the channel default', () => {
    expect(input('wish-entry-duration').value).toBe('14');
  });

  it('marks the read as running at once and blocks sending until it has answered', () => {
    typeEmote(A);
    expect(readText()).toBe(t('wishes.entry.read.running'));
    expect(blockedReason()).toBe(t('wishes.entry.blocked.reading'));
    expect(lookups).toHaveLength(0);
    waitDebounce();
    expect(lookups.map((call) => call.id)).toEqual([A]);
  });

  it('asks only for the id that is current once the debounce has passed', () => {
    typeEmote(A);
    vi.advanceTimersByTime(WISH_ENTRY_LOOKUP_DEBOUNCE_MS - 1);
    typeEmote(B);
    waitDebounce();
    expect(lookups.map((call) => call.id)).toEqual([B]);
    answer(0, { id: B, kind: 'found', name: 'catKISS', animated: true });
    expect(input('wish-entry-alias').value).toBe('catKISS');
  });

  it('lets a late answer for the previous id set neither alias nor preview', () => {
    typeEmote(A);
    waitDebounce();
    typeEmote(B);
    answer(0, { id: A, kind: 'found', name: 'Pag', animated: false });
    expect(input('wish-entry-alias').value).toBe('');
    expect(readText()).toBe(t('wishes.entry.read.running'));
    waitDebounce();
    answer(1, { id: B, kind: 'found', name: 'catKISS', animated: true });
    submitButton().click();
    expect(creates[0].body).toMatchObject({
      sevenTvEmoteId: B,
      emoteName: 'catKISS',
      alias: 'catKISS',
    });
  });

  it('counts only the latest read when the field goes back to the earlier id', () => {
    typeEmote(A);
    waitDebounce();
    typeEmote(B);
    typeEmote(A);
    expect(readText()).toBe(t('wishes.entry.read.running'));
    waitDebounce();
    expect(lookups.map((call) => call.id)).toEqual([A, A]);
    answer(0, { id: A, kind: 'found', name: 'stale', animated: false });
    expect(input('wish-entry-alias').value).toBe('');
    answer(1, { id: A, kind: 'found', name: 'Pag', animated: false });
    expect(input('wish-entry-alias').value).toBe('Pag');
  });

  it('removes a filled-in alias at once on a change of id, a typed one stays', () => {
    foundEmote(A, 'Pag');
    expect(input('wish-entry-alias').value).toBe('Pag');
    typeEmote('nonsense');
    expect(input('wish-entry-alias').value).toBe('');
    expect(readText()).toBe('');

    type('wish-entry-alias', 'MyAlias');
    typeEmote(B);
    waitDebounce();
    answer(lookups.length - 1, { id: B, kind: 'found', name: 'catKISS', animated: true });
    expect(input('wish-entry-alias').value).toBe('MyAlias');
  });

  it('reads the same emote in another id form as the same current id', () => {
    foundEmote(A, 'Pag');
    typeEmote(A.toLowerCase());
    expect(lookups).toHaveLength(1);
    expect(input('wish-entry-alias').value).toBe('Pag');
  });

  it('looks up and sends an old hex id in its canonical form', () => {
    typeEmote('https://7tv.app/emotes/60ae958e229664e8667aea38');
    waitDebounce();
    expect(lookups[0].id).toBe('01F6MZGCNG000255K4X1K7NTHR');
    answer(0, { id: lookups[0].id, kind: 'found', name: 'GIGACHAD', animated: true });
    submitButton().click();
    expect(creates[0].body.sevenTvEmoteId).toBe('01F6MZGCNG000255K4X1K7NTHR');
  });

  it('blocks an emote 7TV does not know, with the reason at the field', () => {
    typeEmote(A);
    waitDebounce();
    answer(0, { id: A, kind: 'unknown' });
    type('wish-entry-alias', 'Pag');
    expect(blockedReason()).toBe(t('wishes.entry.blocked.unknown'));
    const field = input('wish-entry-emote');
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(field.getAttribute('aria-describedby')).toContain('wish-entry-emote-error');
  });

  it.each([
    ['unreachable', 'wishes.entry.read.unreachable'],
    ['throttled', 'wishes.entry.read.throttled'],
  ] as const)('lets a %s 7TV through with a typed alias and leaves the name out', (kind, key) => {
    typeEmote(A);
    waitDebounce();
    answer(0, { id: A, kind });
    expect(readText()).toBe(t(key));
    expect(blockedReason()).toBe(t('wishes.entry.blocked.alias'));
    type('wish-entry-alias', 'Pag');
    expect(blockedReason()).toBeNull();
    submitButton().click();
    expect(creates[0].body).toEqual({ sevenTvEmoteId: A, alias: 'Pag', durationDays: 14 });
  });

  it('sends the normalised login with id, alias, duration and the name of the same read', () => {
    foundEmote(A, 'Pag');
    type('wish-entry-login', ' @SomeViewer ');
    type('wish-entry-duration', '30');
    submitButton().click();
    expect(creates[0].channel).toBe('chan');
    expect(creates[0].body).toEqual({
      sevenTvEmoteId: A,
      alias: 'Pag',
      durationDays: 30,
      emoteName: 'Pag',
      requesterLogin: 'someviewer',
    });
  });

  it.each([
    ['wish-entry-duration', '0', 'wishes.dialog.blocked.duration'],
    ['wish-entry-duration', '366', 'wishes.dialog.blocked.duration'],
    ['wish-entry-alias', 'two words', 'wishes.entry.blocked.alias'],
    ['wish-entry-login', 'ab', 'wishes.entry.blocked.login'],
  ])('blocks %s = "%s" with the reason, the field pointing at its error', (field, value, key) => {
    foundEmote();
    type(field, value);
    expect(submitButton().disabled).toBe(true);
    expect(blockedReason()).toBe(t(key));
    expect(input(field).getAttribute('aria-invalid')).toBe('true');
    expect(input(field).getAttribute('aria-describedby')).toBe(`${field}-error`);
  });

  it('names a missing or unreadable emote before anything else', () => {
    expect(blockedReason()).toBe(t('wishes.entry.blocked.emoteEmpty'));
    typeEmote('KEKW');
    expect(blockedReason()).toBe(t('wishes.entry.blocked.emoteInvalid'));
  });

  it('closes with the filed wish and the set-full hint', () => {
    foundEmote();
    submitButton().click();
    submitButton().click();
    expect(creates).toHaveLength(1);
    expect(dialogRef.disableClose).toBe(true);
    creates[0].answer.next({ ...wish(5), setFull: true });
    expect(closed).toEqual([{ kind: 'created', wish: wish(5), setFull: true }]);
  });

  describe('409 wish_emote_already_in_set', () => {
    function refuseInSet(wishId: number | null): void {
      foundEmote();
      submitButton().click();
      creates[0].answer.error(
        apiError(409, { errorCode: 'wish_emote_already_in_set', slotIds: ['s1', 's2'], wishId }),
      );
      fixture.detectChanges();
    }

    it('without a wish offers taking the reported entries over, looking nothing up', () => {
      refuseInSet(null);
      expect(gets).toHaveLength(0);
      expect(fixture.nativeElement.textContent).toContain(t('wishes.entry.conflict.adopt'));
      button(t('wishes.entry.conflict.takeOver')).click();
      expect(closed).toEqual([
        { kind: 'adopt', sevenTvEmoteId: A, slotIds: ['s1', 's2'], wish: null },
      ]);
    });

    it('with a wish that holds a slot says it runs, with "Verlängern" and the way to it', () => {
      refuseInSet(8);
      expect(gets.map((call) => call.id)).toEqual([8]);
      const running = wish(8, {
        status: 'active',
        slotId: 's1',
        expiresAtUtc: '2026-12-01T00:00:00Z',
      });
      gets[0].answer.next({ ...running, usage: null });
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain(t('wishes.entry.conflict.running'));
      expect(hasButton(t('wishes.entry.conflict.takeOver'))).toBe(false);
      button(t('wishes.actions.extend')).click();
      expect(extends_.map((call) => call.id)).toEqual([8]);
      extends_[0].answer.next(running);
      expect(closed).toEqual([{ kind: 'extended', wish: running }]);
    });

    it('with a wish that has no slot yet offers taking over with that wish', () => {
      refuseInSet(8);
      const approved = wish(8);
      gets[0].answer.next({ ...approved, usage: null });
      fixture.detectChanges();
      button(t('wishes.entry.conflict.takeOver')).click();
      expect(closed).toEqual([
        {
          kind: 'adopt',
          sevenTvEmoteId: A,
          slotIds: ['s1', 's2'],
          wish: { ...approved, usage: null },
        },
      ]);
    });

    it('with a kept wish says so, without "Verlängern"', () => {
      refuseInSet(8);
      gets[0].answer.next({ ...wish(8, { status: 'kept', slotId: 's1' }), usage: null });
      fixture.detectChanges();
      expect(fixture.nativeElement.textContent).toContain(t('wishes.entry.conflict.kept'));
      expect(hasButton(t('wishes.actions.extend'))).toBe(false);
      button(t('wishes.dialog.toWish')).click();
      expect(closed).toEqual([{ kind: 'jump', wishId: 8 }]);
    });

    it('forgets the notice when the emote changes', () => {
      refuseInSet(null);
      typeEmote(B);
      expect(fixture.nativeElement.textContent).not.toContain(t('wishes.entry.conflict.adopt'));
    });
  });

  describe('409 wish_already_running', () => {
    function refuseRunning(found: Wish): void {
      foundEmote();
      submitButton().click();
      creates[0].answer.error(
        apiError(409, { errorCode: 'wish_already_running', wishId: found.id }),
      );
      fixture.detectChanges();
      gets[0].answer.next({ ...found, usage: null });
      fixture.detectChanges();
    }

    it('offers "Verlängern" for a running wish with a deadline', () => {
      refuseRunning(
        wish(3, { status: 'presented', slotId: 's', expiresAtUtc: '2026-11-10T00:00:00Z' }),
      );
      expect(hasButton(t('wishes.actions.extend'))).toBe(true);
    });

    it('offers only the way to an approved wish', () => {
      refuseRunning(wish(3));
      expect(fixture.nativeElement.textContent).toContain(t('wishes.entry.conflict.running'));
      expect(hasButton(t('wishes.actions.extend'))).toBe(false);
      button(t('wishes.dialog.toWish')).click();
      expect(closed).toEqual([{ kind: 'jump', wishId: 3 }]);
    });
  });

  it('shows a refused alias at its field', () => {
    foundEmote();
    submitButton().click();
    creates[0].answer.error(apiError(400, { errorCode: 'wish_alias_invalid' }));
    fixture.detectChanges();
    expect(input('wish-entry-alias').getAttribute('aria-invalid')).toBe('true');
    expect(dialogRef.disableClose).toBe(false);
  });

  it('stops for good when the feature is switched off in the middle', () => {
    foundEmote();
    submitButton().click();
    creates[0].answer.error(apiError(404, { errorCode: 'wishes_disabled' }));
    fixture.detectChanges();
    expect(blockedReason()).toBe(t('wishes.dialog.blocked.disabled'));
  });
});
