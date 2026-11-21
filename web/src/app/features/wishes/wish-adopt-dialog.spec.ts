import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import de from '../../../../public/i18n/de.json';
import { LanguageService } from '../../core/i18n/language.service';
import { toLocale } from '../../core/i18n/locale';
import { SevenTvEmoteLookup } from '../../core/seven-tv/seven-tv-emote-lookup';
import type {
  AdoptWishBody,
  Wish,
  WishAdoptableSlot,
  WishDetail,
  WishSettingsRead,
} from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import {
  ADOPT_START_CLOCK_MARGIN_MS,
  adoptEntryKind,
  adoptStartOptions,
  adoptStartsAtUtc,
  WishAdoptDialog,
  WishAdoptDialogData,
  WishAdoptDialogResult,
} from './wish-adopt-dialog';

const NOW = Date.parse('2026-11-18T12:00:00Z');
const DAY = 86_400_000;

function slot(slotId: string, overrides: Partial<WishAdoptableSlot> = {}): WishAdoptableSlot {
  return {
    slotId,
    sevenTvEmoteId: `emote-${slotId}`,
    alias: `alias-${slotId}`,
    addedAtUtc: '2026-11-15T10:00:00.123456Z',
    addedAtAuthoritative: true,
    imageUrl: null,
    heldByWishId: null,
    runningWishId: null,
    runningWishAdoptable: false,
    ...overrides,
  };
}

function wish(id: number, overrides: Partial<Wish> = {}): Wish {
  return {
    id,
    sevenTvEmoteId: 'emote-a',
    emoteName: `name${id}`,
    alias: `alias${id}`,
    source: 'irc',
    status: 'approved',
    statusReason: null,
    requesterLogin: null,
    durationDays: 21,
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

function t(path: string): string {
  return path
    .split('.')
    .reduce<unknown>((node, key) => (node as Record<string, unknown>)[key], de) as string;
}

describe('adoptEntryKind', () => {
  // The three kinds of entries, from the fields of the list alone (plan T6).
  it.each([
    ['free', slot('s'), null, { kind: 'free' }],
    ['free, links the given wish', slot('s'), 7, { kind: 'free' }],
    [
      'held by a wish',
      slot('s', { heldByWishId: 3 }),
      null,
      { kind: 'locked', reason: 'held', wishId: 3 },
    ],
    [
      'running wish that may take it',
      slot('s', { runningWishId: 4, runningWishAdoptable: true }),
      null,
      { kind: 'link', wishId: 4 },
    ],
    [
      'that same running wish was given',
      slot('s', { runningWishId: 4, runningWishAdoptable: true }),
      4,
      { kind: 'free' },
    ],
    [
      'another wish was given',
      slot('s', { runningWishId: 4, runningWishAdoptable: true }),
      7,
      { kind: 'locked', reason: 'otherWish', wishId: 4 },
    ],
    [
      'running wish that cannot take it',
      slot('s', { runningWishId: 4, runningWishAdoptable: false }),
      null,
      { kind: 'locked', reason: 'runningElsewhere', wishId: 4 },
    ],
  ])('%s', (_name, entry, given, expected) => {
    expect(adoptEntryKind(entry, given)).toEqual(expected);
  });
});

describe('adoptStartOptions and adoptStartsAtUtc', () => {
  it('prefers the add date while the deadline from it still lies ahead', () => {
    const added = new Date(NOW - 2 * DAY).toISOString();
    expect(adoptStartOptions(added, 3, NOW)).toEqual({
      canStartAtAdded: true,
      addedExpired: false,
      preferred: 'added',
    });
  });

  it('prefers "now" when the deadline from the add date would be over', () => {
    const added = new Date(NOW - 3 * DAY).toISOString();
    expect(adoptStartOptions(added, 3, NOW)).toEqual({
      canStartAtAdded: true,
      addedExpired: true,
      preferred: 'now',
    });
  });

  it('knows only "now" for an undated entry', () => {
    expect(adoptStartOptions(null, 3, NOW)).toEqual({
      canStartAtAdded: false,
      addedExpired: false,
      preferred: 'now',
    });
  });

  it('sends no start for the add date or an undated entry', () => {
    expect(adoptStartsAtUtc('added', '2026-11-01T00:00:00Z', NOW)).toBeUndefined();
    expect(adoptStartsAtUtc('now', null, NOW)).toBeUndefined();
  });

  it('sends "now" a margin back, and never at or before the add date', () => {
    expect(adoptStartsAtUtc('now', '2026-11-01T00:00:00Z', NOW)).toBe(
      new Date(NOW - ADOPT_START_CLOCK_MARGIN_MS).toISOString(),
    );
    const justAdded = new Date(NOW - ADOPT_START_CLOCK_MARGIN_MS + 1).toISOString();
    expect(adoptStartsAtUtc('now', justAdded, NOW)).toBeUndefined();
    const exactly = new Date(NOW - ADOPT_START_CLOCK_MARGIN_MS).toISOString();
    expect(adoptStartsAtUtc('now', exactly, NOW)).toBeUndefined();
  });
});

describe('WishAdoptDialog', () => {
  let data: WishAdoptDialogData;
  let slotCalls: { channel: string; emoteId?: string; answer: Subject<WishAdoptableSlot[]> }[];
  let getCalls: { id: number; answer: Subject<WishDetail> }[];
  let settings: Subject<WishSettingsRead>;
  let adoptCalls: { channel: string; body: AdoptWishBody; answer: Subject<Wish> }[];
  let closed: (WishAdoptDialogResult | undefined)[];
  let dialogRef: { close: ReturnType<typeof vi.fn>; disableClose: boolean };
  let fixture: ComponentFixture<WishAdoptDialog>;

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    data = { channelName: 'chan' };
    slotCalls = [];
    getCalls = [];
    adoptCalls = [];
    closed = [];
    settings = new Subject();
    dialogRef = {
      close: vi.fn((result?: WishAdoptDialogResult) => closed.push(result)),
      disableClose: false,
    };
    TestBed.configureTestingModule({
      imports: [
        WishAdoptDialog,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: DIALOG_DATA, useFactory: () => data },
        { provide: DialogRef, useValue: dialogRef },
        { provide: SevenTvEmoteLookup, useValue: { isKnownStill: () => false } },
        {
          provide: WishService,
          useValue: {
            listSlots: vi.fn((channel: string, emoteId?: string) => {
              const answer = new Subject<WishAdoptableSlot[]>();
              slotCalls.push({ channel, emoteId, answer });
              return answer;
            }),
            get: vi.fn((_channel: string, id: number) => {
              const answer = new Subject<WishDetail>();
              getCalls.push({ id, answer });
              return answer;
            }),
            getSettings: vi.fn(() => settings),
            adopt: vi.fn((channel: string, body: AdoptWishBody) => {
              const answer = new Subject<Wish>();
              adoptCalls.push({ channel, body, answer });
              return answer;
            }),
          },
        },
      ],
    });
  });

  afterEach(() => {
    fixture?.destroy();
    vi.useRealTimers();
  });

  function open(withData: Partial<WishAdoptDialogData> = {}): void {
    data = { channelName: 'chan', ...withData };
    fixture = TestBed.createComponent(WishAdoptDialog);
    fixture.detectChanges();
  }

  function answerSlots(slots: WishAdoptableSlot[], call = slotCalls[slotCalls.length - 1]): void {
    call.answer.next(slots);
    fixture.detectChanges();
  }

  function answerSettings(defaultDurationDays = 14): void {
    settings.next({
      defaultDurationDays,
      probationLeadDays: 3,
      protectionThresholdPerDay: null,
      botModeEnabled: false,
      botSuspendedReason: null,
      pendingExpiryDays: 14,
      canEdit: true,
    });
    fixture.detectChanges();
  }

  function radio(slotId: string): HTMLInputElement {
    const element = fixture.nativeElement.querySelector(`#wish-adopt-entry-${slotId}`);
    expect(element).not.toBeNull();
    return element as HTMLInputElement;
  }

  function pick(slotId: string): void {
    radio(slotId).click();
    fixture.detectChanges();
  }

  function button(label: string): HTMLButtonElement {
    const found = [...fixture.nativeElement.querySelectorAll('button')].find(
      (candidate) => (candidate as HTMLButtonElement).textContent?.trim() === label,
    );
    expect(found).toBeDefined();
    return found as HTMLButtonElement;
  }

  function submitButton(): HTMLButtonElement {
    return button(t('wishes.adopt.submit'));
  }

  function blockedReason(): string | null {
    const describedBy = submitButton().getAttribute('aria-describedby');
    return describedBy
      ? (fixture.nativeElement.querySelector(`#${describedBy}`)?.textContent?.trim() ?? null)
      : null;
  }

  function noteOf(slotId: string): string {
    return fixture.nativeElement.querySelector(`#wish-adopt-entry-${slotId}-note`).textContent;
  }

  function setDuration(value: string): void {
    const input = fixture.nativeElement.querySelector('#wish-adopt-duration') as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function startRadio(label: string): HTMLInputElement {
    const found = [...fixture.nativeElement.querySelectorAll('label')].find((candidate) =>
      (candidate as HTMLLabelElement).textContent?.includes(label),
    ) as HTMLLabelElement | undefined;
    expect(found).toBeDefined();
    return found!.querySelector('input') as HTMLInputElement;
  }

  it('lists the entries from the wish read, narrowed to the emote it was given', () => {
    open({ sevenTvEmoteId: 'emote-x' });
    expect(slotCalls.map((call) => [call.channel, call.emoteId])).toEqual([['chan', 'emote-x']]);
    answerSlots([slot('a'), slot('b')]);
    expect(radio('a').disabled).toBe(false);
    expect(radio('b').disabled).toBe(false);
  });

  it('offers only the preselected entries, and one the list no longer knows is not pickable', () => {
    open({ slotIds: ['b', 'gone'] });
    answerSlots([slot('a'), slot('b')]);
    expect(fixture.nativeElement.querySelector('#wish-adopt-entry-a')).toBeNull();
    expect(radio('b').disabled).toBe(false);
    expect(radio('gone').disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain(t('wishes.adopt.missing'));
  });

  it('cannot send before an entry is picked, and says why', () => {
    open();
    expect(submitButton().disabled).toBe(true);
    expect(blockedReason()).toBe(t('wishes.adopt.blocked.loading'));
    answerSlots([slot('a')]);
    answerSettings();
    expect(blockedReason()).toBe(t('wishes.adopt.blocked.noSelection'));
  });

  it('adopts a free entry as a new wish with its alias and add date verbatim', () => {
    open();
    answerSettings(14);
    answerSlots([slot('a')]);
    pick('a');
    expect(blockedReason()).toBeNull();
    submitButton().click();
    expect(adoptCalls).toHaveLength(1);
    expect(adoptCalls[0].channel).toBe('chan');
    expect(adoptCalls[0].body).toEqual({
      slotId: 'a',
      expectedAlias: 'alias-a',
      expectedAddedAtUtc: '2026-11-15T10:00:00.123456Z',
      durationDays: 14,
    });
    const adopted = wish(9, { status: 'active' });
    adoptCalls[0].answer.next(adopted);
    expect(closed).toEqual([{ kind: 'adopted', wish: adopted }]);
  });

  it('links a free entry with the wish it was given, with that wish’s duration', () => {
    const given = wish(7, { durationDays: 30 });
    open({ wish: given, sevenTvEmoteId: given.sevenTvEmoteId });
    expect(TestBed.inject(WishService).getSettings).not.toHaveBeenCalled();
    answerSlots([slot('a')]);
    pick('a');
    button(t('wishes.adopt.submitLink')).click();
    expect(adoptCalls[0].body).toMatchObject({ slotId: 'a', wishId: 7, durationDays: 30 });
  });

  it('links an entry with its running wish, after loading that wish’s duration', () => {
    open();
    answerSettings(14);
    answerSlots([slot('a', { runningWishId: 4, runningWishAdoptable: true })]);
    expect(noteOf('a')).toContain(t('wishes.adopt.linkNote'));
    pick('a');
    const link = button(t('wishes.adopt.submitLink'));
    expect(link.disabled).toBe(true);
    expect(getCalls.map((call) => call.id)).toEqual([4]);
    getCalls[0].answer.next({ ...wish(4, { durationDays: 45 }), usage: null });
    fixture.detectChanges();
    expect(link.disabled).toBe(false);
    link.click();
    expect(adoptCalls[0].body).toMatchObject({ slotId: 'a', wishId: 4, durationDays: 45 });
  });

  it('treats a linkable entry whose wish has meanwhile taken a slot as stale', () => {
    open();
    answerSettings();
    answerSlots([slot('a', { runningWishId: 4, runningWishAdoptable: true })]);
    pick('a');
    getCalls[0].answer.next({ ...wish(4, { status: 'active', slotId: 'z' }), usage: null });
    fixture.detectChanges();
    expect(slotCalls).toHaveLength(2);
    expect(adoptCalls).toHaveLength(0);
    answerSlots([slot('a', { runningWishId: 4, runningWishAdoptable: false })]);
    expect(radio('a').disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain(
      t('wishes.adopt.stale.wish_state_conflict'),
    );
  });

  it('locks an entry that would go to another wish than the given one', () => {
    open({ wish: wish(7) });
    answerSlots([slot('a', { runningWishId: 4, runningWishAdoptable: true })]);
    expect(radio('a').disabled).toBe(true);
    expect(noteOf('a')).toContain(t('wishes.adopt.locked.otherWish'));
  });

  it('locks held entries with their reason and offers the way to the wish', () => {
    open();
    answerSettings();
    answerSlots([
      slot('a', { heldByWishId: 3 }),
      slot('b', { heldByWishId: 5 }),
      slot('c', { runningWishId: 6, runningWishAdoptable: false }),
    ]);
    expect(getCalls.map((call) => call.id)).toEqual([3, 5]);
    expect(noteOf('a')).toContain(t('wishes.adopt.locked.held'));
    getCalls[0].answer.next({ ...wish(3, { status: 'kept', slotId: 'a' }), usage: null });
    getCalls[1].answer.next({ ...wish(5, { status: 'active', slotId: 'b' }), usage: null });
    fixture.detectChanges();
    expect(noteOf('a')).toContain(t('wishes.adopt.locked.kept'));
    expect(noteOf('b')).toContain(t('wishes.adopt.locked.heldRunning'));
    expect(noteOf('c')).toContain(t('wishes.adopt.locked.runningElsewhere'));
    for (const id of ['a', 'b', 'c']) {
      expect(radio(id).disabled).toBe(true);
      expect(radio(id).getAttribute('aria-describedby')).toBe(`wish-adopt-entry-${id}-note`);
    }
    const toWish = [...fixture.nativeElement.querySelectorAll('button')].filter(
      (candidate) =>
        (candidate as HTMLButtonElement).textContent?.trim() === t('wishes.dialog.toWish'),
    ) as HTMLButtonElement[];
    expect(toWish).toHaveLength(3);
    toWish[2].click();
    expect(closed).toEqual([{ kind: 'jump', wishId: 6 }]);
  });

  it('starts an undated entry only from now and sends it as undated', () => {
    open();
    answerSettings(14);
    answerSlots([slot('a', { addedAtUtc: null, addedAtAuthoritative: false })]);
    pick('a');
    expect(fixture.nativeElement.textContent).toContain(t('wishes.adopt.start.undatedHint'));
    expect(fixture.nativeElement.textContent).not.toContain(t('wishes.adopt.start.added'));
    submitButton().click();
    expect(adoptCalls[0].body.expectedAddedAtUtc).toBeNull();
    expect('startsAtUtc' in adoptCalls[0].body).toBe(false);
  });

  it('preselects "from now" when the add date would already be past the deadline, and says why', () => {
    open();
    answerSettings(3);
    answerSlots([slot('a', { addedAtUtc: new Date(NOW - 10 * DAY).toISOString() })]);
    pick('a');
    expect(startRadio(t('wishes.adopt.start.now')).checked).toBe(true);
    expect(fixture.nativeElement.textContent).toContain(t('wishes.adopt.start.expiredHint'));
    submitButton().click();
    expect(adoptCalls[0].body.startsAtUtc).toBe(
      new Date(NOW - ADOPT_START_CLOCK_MARGIN_MS).toISOString(),
    );
  });

  it('sends no start for "from the add date" and shows the expiry it leads to', () => {
    open();
    answerSettings(14);
    answerSlots([slot('a')]);
    pick('a');
    expect(startRadio(t('wishes.adopt.start.added')).checked).toBe(true);
    const locale = toLocale(TestBed.inject(LanguageService).lang());
    const expiry = new Date(Date.parse('2026-11-15T10:00:00.123Z') + 14 * DAY).toLocaleDateString(
      locale,
      { dateStyle: 'medium' },
    );
    expect(fixture.nativeElement.textContent).toContain(expiry);
    submitButton().click();
    expect('startsAtUtc' in adoptCalls[0].body).toBe(false);
  });

  it('blocks an invalid duration with the reason at the field', () => {
    open();
    answerSettings(14);
    answerSlots([slot('a')]);
    pick('a');
    setDuration('0');
    const input = fixture.nativeElement.querySelector('#wish-adopt-duration') as HTMLInputElement;
    expect(input.getAttribute('aria-invalid')).toBe('true');
    expect(input.getAttribute('aria-describedby')).toBe('wish-adopt-duration-error');
    expect(blockedReason()).toBe(t('wishes.dialog.blocked.duration'));
  });

  describe('summary in the action row', () => {
    function summaryText(): string | null {
      const line = fixture.nativeElement.querySelector('#wish-adopt-summary');
      return line === null ? null : (line.textContent ?? '').replace(/\s+/g, ' ').trim();
    }

    function dateOf(ms: number): string {
      const locale = toLocale(TestBed.inject(LanguageService).lang());
      return new Date(ms).toLocaleDateString(locale, { dateStyle: 'medium' });
    }

    it('names only the duration before an entry is picked', () => {
      open();
      answerSlots([slot('a')]);
      answerSettings(30);
      expect(summaryText()).toBe('30 Tage');
    });

    it('adds the start and the expiry once an entry is picked, and follows the start choice', () => {
      open();
      answerSettings(14);
      answerSlots([slot('a')]);
      pick('a');
      const added = Date.parse('2026-11-15T10:00:00.123Z');
      expect(summaryText()).toBe(`14 Tage ab Hinzufügedatum · endet ${dateOf(added + 14 * DAY)}`);
      startRadio(t('wishes.adopt.start.now')).click();
      fixture.detectChanges();
      expect(summaryText()).toBe(`14 Tage ab jetzt · endet ${dateOf(NOW + 14 * DAY)}`);
    });

    it('uses the singular for one day', () => {
      open();
      answerSettings(1);
      answerSlots([slot('a')]);
      expect(summaryText()).toBe('1 Tag');
    });

    it('shows nothing while the duration is unusable, so the blocked reason stands alone', () => {
      open();
      answerSettings(14);
      answerSlots([slot('a')]);
      pick('a');
      setDuration('0');
      expect(summaryText()).toBeNull();
      expect(blockedReason()).toBe(t('wishes.dialog.blocked.duration'));
    });

    it('"Ändern" focuses the duration field', () => {
      open();
      answerSettings(14);
      answerSlots([slot('a')]);
      pick('a');
      document.body.appendChild(fixture.nativeElement); // focus needs a connected element
      const scrolled = vi.fn(); // jsdom has no layout and no scrollIntoView
      Element.prototype.scrollIntoView = scrolled;
      const change = button(t('wishes.adopt.summary.change'));
      expect(change.getAttribute('aria-label')).toBe(t('wishes.adopt.summary.changeLabel'));
      change.click();
      expect(scrolled).toHaveBeenCalledTimes(1);
      expect(document.activeElement).toBe(
        fixture.nativeElement.querySelector('#wish-adopt-duration'),
      );
    });
  });

  describe('stale situation', () => {
    it.each([
      [404, 'wish_slot_not_found', undefined],
      [404, 'wish_not_found', undefined],
      [409, 'wish_slot_already_linked', 3],
      [409, 'wish_already_running', 4],
      [409, 'wish_state_conflict', undefined],
    ])('%s %s reloads, drops the choice and sends nothing again', (status, code, wishId) => {
      open();
      answerSettings(14);
      answerSlots([slot('a')]);
      pick('a');
      submitButton().click();
      adoptCalls[0].answer.error(apiError(status, { errorCode: code, wishId }));
      fixture.detectChanges();
      expect(slotCalls).toHaveLength(2);
      expect(adoptCalls).toHaveLength(1);
      expect(fixture.nativeElement.textContent).toContain(t('wishes.adopt.stale.reselect'));
      answerSlots([slot('a')]);
      expect(radio('a').checked).toBe(false);
      expect(blockedReason()).toBe(t('wishes.adopt.blocked.noSelection'));
      if (wishId !== undefined) {
        button(t('wishes.dialog.toWish')).click();
        expect(closed).toEqual([{ kind: 'jump', wishId }]);
      }
    });

    it('sends the new add date only after the reused slot id is picked again', () => {
      open();
      answerSettings(14);
      answerSlots([slot('a')]);
      pick('a');
      submitButton().click();
      adoptCalls[0].answer.error(apiError(404, { errorCode: 'wish_slot_not_found' }));
      fixture.detectChanges();
      answerSlots([slot('a', { addedAtUtc: '2026-11-18T09:00:00Z' })]);
      expect(adoptCalls).toHaveLength(1);
      pick('a');
      submitButton().click();
      expect(adoptCalls).toHaveLength(2);
      expect(adoptCalls[1].body.expectedAddedAtUtc).toBe('2026-11-18T09:00:00Z');
    });

    it('names a kept holder when the refusal points at one', () => {
      open();
      answerSettings(14);
      answerSlots([slot('a')]);
      pick('a');
      submitButton().click();
      adoptCalls[0].answer.error(
        apiError(409, { errorCode: 'wish_slot_already_linked', wishId: 3 }),
      );
      fixture.detectChanges();
      getCalls
        .find((call) => call.id === 3)!
        .answer.next({
          ...wish(3, { status: 'kept', slotId: 'a' }),
          usage: null,
        });
      answerSlots([slot('a', { heldByWishId: 3 })]);
      expect(fixture.nativeElement.textContent).toContain(
        t('wishes.adopt.stale.wish_slot_already_linked_kept'),
      );
      expect(radio('a').disabled).toBe(true);
    });

    it('turns a linkable entry whose wish became active into a locked one', () => {
      open();
      answerSettings(14);
      answerSlots([slot('a', { runningWishId: 4, runningWishAdoptable: true })]);
      pick('a');
      getCalls[0].answer.next({ ...wish(4), usage: null });
      fixture.detectChanges();
      button(t('wishes.adopt.submitLink')).click();
      adoptCalls[0].answer.error(apiError(409, { errorCode: 'wish_state_conflict' }));
      fixture.detectChanges();
      answerSlots([slot('a', { runningWishId: 4, runningWishAdoptable: false })]);
      expect(radio('a').disabled).toBe(true);
      expect(adoptCalls).toHaveLength(1);
      button(t('wishes.dialog.toWish')).click();
      expect(closed).toEqual([{ kind: 'jump', wishId: 4 }]);
    });
  });

  it('sends once on a double click and cannot be dismissed while the request is out', () => {
    open();
    answerSettings(14);
    answerSlots([slot('a')]);
    pick('a');
    submitButton().click();
    submitButton().click();
    expect(adoptCalls).toHaveLength(1);
    expect(dialogRef.disableClose).toBe(true);
  });

  it('shows the start refusal as the take-over text, not as a stale situation', () => {
    open();
    answerSettings(14);
    answerSlots([slot('a')]);
    pick('a');
    submitButton().click();
    adoptCalls[0].answer.error(apiError(400, { errorCode: 'invalid_request_body' }));
    fixture.detectChanges();
    expect(slotCalls).toHaveLength(1);
    expect(fixture.nativeElement.textContent).toContain(t('wishes.errors.adopt_start_invalid'));
    expect(dialogRef.disableClose).toBe(false);
  });

  it('offers loading again when the entries fail, and sends nothing', () => {
    open();
    answerSettings(14);
    slotCalls[0].answer.error(apiError(500, {}));
    fixture.detectChanges();
    expect(blockedReason()).toBe(t('wishes.adopt.blocked.loadFailed'));
    button(t('wishes.page.reload')).click();
    expect(slotCalls).toHaveLength(2);
  });

  it('stops for good when the feature is switched off in the middle', () => {
    open();
    answerSettings(14);
    answerSlots([slot('a')]);
    pick('a');
    submitButton().click();
    adoptCalls[0].answer.error(apiError(404, { errorCode: 'wishes_disabled' }));
    fixture.detectChanges();
    expect(submitButton().disabled).toBe(true);
    expect(blockedReason()).toBe(t('wishes.dialog.blocked.disabled'));
  });
});
