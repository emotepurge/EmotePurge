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
import { WishEmotePreviewService } from '../../core/wishes/wish-emote-preview.service';
import type {
  ApprovedWish,
  ApproveWishBody,
  Wish,
  WishSettingsRead,
} from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { WishApproveDialog, WishApproveDialogResult } from './wish-approve-dialog';
import { WISH_ENTRY_LOOKUP_DEBOUNCE_MS } from './wish-entry-dialog';

const A = '01EZPHAJ3R000C438200A44F19';
const B = '01F5VW2TKR0003RCV2Z6JBHCST';

function open(overrides: Partial<Wish> = {}): Wish {
  return {
    id: 5,
    sevenTvEmoteId: '',
    emoteName: '',
    alias: '',
    source: 'irc',
    status: 'pending',
    statusReason: null,
    requesterLogin: 'viewer1',
    durationDays: 14,
    createdAtUtc: '2026-11-16T09:00:00Z',
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
    requestText: 'catJAM pls',
    requestedName: 'catJAM',
    matchKind: 'name',
    rewardLabel: 'Emote wünschen',
    ...overrides,
  };
}

/** A link wish: the emote is known, alias and name are not. */
const linkWish = (overrides: Partial<Wish> = {}): Wish =>
  open({
    sevenTvEmoteId: A,
    requestedName: null,
    matchKind: 'link',
    requestText: `https://7tv.app/emotes/${A}`,
    ...overrides,
  });

function apiError(status: number, body: Record<string, unknown>): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

describe('WishApproveDialog', () => {
  let lookups: { id: string; answer: Subject<SevenTvEmoteLookupResult> }[];
  let approves: { id: number; body: ApproveWishBody; answer: Subject<ApprovedWish> }[];
  let settingsReads: { channel: string; answer: Subject<WishSettingsRead> }[];
  let closed: (WishApproveDialogResult | undefined)[];
  let dialogRef: { close: ReturnType<typeof vi.fn>; disableClose: boolean };
  let fixture: ComponentFixture<WishApproveDialog>;

  beforeEach(() => {
    vi.useFakeTimers();
    lookups = [];
    approves = [];
    settingsReads = [];
    closed = [];
    dialogRef = {
      close: vi.fn((result?: WishApproveDialogResult) => closed.push(result)),
      disableClose: false,
    };
  });

  afterEach(() => {
    fixture?.destroy();
    vi.useRealTimers();
  });

  /** `before` runs against the configured injector before the dialog opens (the session's preview). */
  function create(value: Wish, before?: () => void): void {
    TestBed.configureTestingModule({
      imports: [
        WishApproveDialog,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        { provide: DIALOG_DATA, useValue: { channelName: 'chan', wish: value } },
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
            approve: vi.fn((_channel: string, id: number, body: ApproveWishBody) => {
              const answer = new Subject<ApprovedWish>();
              approves.push({ id, body, answer });
              return answer;
            }),
            getSettings: vi.fn((channel: string) => {
              const answer = new Subject<WishSettingsRead>();
              settingsReads.push({ channel, answer });
              return answer;
            }),
          },
        },
      ],
    });
    before?.();
    fixture = TestBed.createComponent(WishApproveDialog);
    fixture.detectChanges();
  }

  function element<T extends HTMLElement>(selector: string): T | null {
    return fixture.nativeElement.querySelector(selector) as T | null;
  }

  function type(id: string, value: string): void {
    const input = element<HTMLInputElement>(`#${id}`) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  function settleRead(index: number, result: SevenTvEmoteLookupResult): void {
    lookups[index].answer.next(result);
    lookups[index].answer.complete();
    fixture.detectChanges();
  }

  function buttonNamed(label: string): HTMLButtonElement | undefined {
    return [...fixture.nativeElement.querySelectorAll('button')].find(
      (button) => (button as HTMLButtonElement).textContent?.trim() === label,
    ) as HTMLButtonElement | undefined;
  }

  function submit(label = de.wishes.actions.approve): HTMLButtonElement {
    const found = buttonNamed(label);
    if (!found) {
      throw new Error(`No button "${label}"`);
    }
    return found;
  }

  function blocked(): string | null {
    return element('#wish-approve-blocked')?.textContent?.trim() ?? null;
  }

  function answer(index: number, value: ApprovedWish): void {
    approves[index].answer.next(value);
    approves[index].answer.complete();
  }

  describe('a wish that knows its emote', () => {
    it('still gets its read when a row that asked for the same emote leaves the view first', () => {
      const OTHER = '01F5VW2TKR0003RCV2Z6JBHCST';
      create(linkWish(), () => {
        const preview = TestBed.inject(WishEmotePreviewService);
        // Another row's read is on the way, and a row with this wish's emote waits behind it.
        preview.request(OTHER, 'row-other');
        preview.request(A, 'row-a');
      });
      TestBed.inject(WishEmotePreviewService).withdraw(A, 'row-a');
      settleRead(0, { id: OTHER, kind: 'found', name: 'other', animated: true });

      expect(lookups.map((read) => read.id)).toEqual([OTHER, A]);
      settleRead(1, { id: A, kind: 'found', name: 'catJAM', animated: true });
      expect(blocked()).toBeNull();
    });

    it('releases its emote when it closes before the read started', () => {
      const OTHER = '01F5VW2TKR0003RCV2Z6JBHCST';
      create(linkWish(), () => {
        TestBed.inject(WishEmotePreviewService).request(OTHER, 'row-other');
      });
      fixture.destroy();
      settleRead(0, { id: OTHER, kind: 'found', name: 'other', animated: true });

      expect(lookups.map((read) => read.id)).toEqual([OTHER]);
    });

    it('reads the emote once and proposes its default name as the alias', () => {
      create(linkWish());

      expect(lookups.map((read) => read.id)).toEqual([A]);
      expect(blocked()).toBe(de.wishes.entry.blocked.reading);
      expect(submit().disabled).toBe(true);
      expect(element('#wish-approve-emote')).toBeNull();

      settleRead(0, { id: A, kind: 'found', name: 'catJAM', animated: true });

      expect(element<HTMLInputElement>('#wish-approve-alias')?.value).toBe('catJAM');
      expect(element<HTMLInputElement>('#wish-approve-duration')?.value).toBe('14');
      expect(blocked()).toBeNull();
    });

    it('approves without sending the id, with the read name, the alias and the duration', () => {
      create(linkWish());
      settleRead(0, { id: A, kind: 'found', name: 'catJAM', animated: true });
      type('wish-approve-duration', '7');
      submit().click();

      expect(approves).toEqual([
        expect.objectContaining({
          id: 5,
          body: { emoteName: 'catJAM', alias: 'catJAM', durationDays: 7 },
        }),
      ]);
      expect(dialogRef.disableClose).toBe(true);

      answer(0, { ...linkWish(), status: 'approved', extendedExpiresAtUtc: null });
      expect(closed).toEqual([{ kind: 'approved', wish: { ...linkWish(), status: 'approved' } }]);
    });

    it('lets the manager approve with a typed alias when 7TV cannot be reached', () => {
      create(linkWish());
      settleRead(0, { id: A, kind: 'unreachable' });
      expect(blocked()).toBe(de.wishes.entry.blocked.alias);

      type('wish-approve-alias', 'Kitty');
      submit().click();
      expect(approves[0].body).toEqual({ alias: 'Kitty', durationDays: 14 });
    });

    it('blocks an emote 7TV does not know', () => {
      create(linkWish({ alias: 'Kitty' }));
      settleRead(0, { id: A, kind: 'unknown' });

      expect(blocked()).toBe(de.wishes.entry.blocked.unknown);
      expect(submit().disabled).toBe(true);
    });

    it('names the action an extension when a wish for the emote already runs', () => {
      create(linkWish({ statusReason: 'already_running', alias: 'catJAM' }));

      expect(fixture.nativeElement.textContent).toContain(de.wishes.approve.titleExtend);
      expect(buttonNamed(de.wishes.actions.approveExtend)).toBeDefined();
      expect(buttonNamed(de.wishes.actions.approve)).toBeUndefined();
    });

    describe('an extension', () => {
      const SETTINGS: WishSettingsRead = {
        defaultDurationDays: 30,
        probationLeadDays: 3,
        protectionThresholdPerDay: null,
        botModeEnabled: false,
        botSuspendedReason: null,
        pendingExpiryDays: 14,
        canEdit: true,
      };

      function extension(): string | null {
        return element('#wish-approve-extension')?.textContent?.trim() ?? null;
      }

      it('names the channel default it books instead of offering a duration, and sends none', () => {
        create(linkWish({ statusReason: 'already_running', alias: 'catJAM', durationDays: 7 }));
        settleRead(0, { id: A, kind: 'found', name: 'catJAM', animated: true });

        expect(element('#wish-approve-duration')).toBeNull();
        expect(extension()).toBe(de.wishes.approve.extension.unknown);
        expect(settingsReads.map((read) => read.channel)).toEqual(['chan']);

        settingsReads[0].answer.next(SETTINGS);
        fixture.detectChanges();
        expect(extension()).toBe(de.wishes.approve.extension.other.replace('{{ count }}', '30'));

        submit(de.wishes.actions.approveExtend).click();
        expect(approves[0].body).toEqual({ emoteName: 'catJAM', alias: 'catJAM' });
      });

      it('still approves when the settings cannot be read, naming the default without a number', () => {
        create(linkWish({ statusReason: 'already_running', alias: 'catJAM' }));
        settleRead(0, { id: A, kind: 'found', name: 'catJAM', animated: true });
        settingsReads[0].answer.error(apiError(500, {}));
        fixture.detectChanges();

        expect(extension()).toBe(de.wishes.approve.extension.unknown);
        expect(submit(de.wishes.actions.approveExtend).disabled).toBe(false);
      });

      it("takes 7TV's name as alias for a name wish whose typed name this manager may not see", () => {
        // Not confirmed on the Twitch id: the server sends neither text nor typed name (2.4a).
        create(
          linkWish({
            statusReason: 'already_running',
            matchKind: 'name',
            requestText: null,
            requestedName: null,
            requesterLogin: null,
          }),
        );
        settleRead(0, { id: A, kind: 'found', name: 'catJAM', animated: true });

        expect(element<HTMLInputElement>('#wish-approve-alias')?.value).toBe('catJAM');
        expect(blocked()).toBeNull();
        submit(de.wishes.actions.approveExtend).click();
        expect(approves[0].body).toEqual({ emoteName: 'catJAM', alias: 'catJAM' });
      });

      it('reads no settings for a plain approval', () => {
        create(linkWish());

        expect(settingsReads).toEqual([]);
        expect(element('#wish-approve-duration')).not.toBeNull();
      });
    });
  });

  describe('a wish without an emote', () => {
    it('blocks sending without an emote and says why', () => {
      create(open());

      expect(element('#wish-approve-emote')).not.toBeNull();
      expect(blocked()).toBe(de.wishes.approve.blocked.emoteMissing);
      expect(submit().disabled).toBe(true);
      expect(submit().getAttribute('aria-describedby')).toBe('wish-approve-blocked');
      submit().click();
      expect(approves).toEqual([]);
      expect(lookups).toEqual([]);
    });

    it('reads the emote of the field and sends its canonical id, keeping the typed name as alias', () => {
      create(open());
      type('wish-approve-emote', `https://7tv.app/emotes/${A.toLowerCase()}`);
      expect(element<HTMLInputElement>('#wish-approve-alias')?.value).toBe('catJAM');
      expect(blocked()).toBe(de.wishes.entry.blocked.reading);

      vi.advanceTimersByTime(WISH_ENTRY_LOOKUP_DEBOUNCE_MS);
      expect(lookups.map((read) => read.id)).toEqual([A]);
      settleRead(0, { id: A, kind: 'found', name: 'CatJam7tv', animated: false });

      expect(element<HTMLInputElement>('#wish-approve-alias')?.value).toBe('catJAM');
      submit().click();
      expect(approves[0].body).toEqual({
        sevenTvEmoteId: A,
        emoteName: 'CatJam7tv',
        alias: 'catJAM',
        durationDays: 14,
      });
    });

    it('keeps the duration of a plain approval for a name wish the hint says may extend', () => {
      // The hint already_running on a name wish: whether the id entered is the running emote only the
      // server decides, so the dialog promises no extension and sends the duration (round 3).
      create(open({ statusReason: 'already_running' }));

      expect(fixture.nativeElement.textContent).toContain(de.wishes.approve.title);
      expect(buttonNamed(de.wishes.actions.approveExtend)).toBeUndefined();
      expect(element('#wish-approve-extension')).toBeNull();
      const duration = element<HTMLInputElement>('#wish-approve-duration') as HTMLInputElement;
      expect(duration.getAttribute('aria-describedby')).toBe('wish-approve-duration-hint');
      expect(element('#wish-approve-duration-hint')?.textContent?.trim()).toBe(
        de.wishes.approve.extension.ifRunning,
      );
      expect(settingsReads).toEqual([]);

      type('wish-approve-emote', A);
      vi.advanceTimersByTime(WISH_ENTRY_LOOKUP_DEBOUNCE_MS);
      settleRead(0, { id: A, kind: 'found', name: 'catJAM', animated: true });
      type('wish-approve-duration', '9');
      submit().click();
      expect(approves[0].body).toEqual({
        sevenTvEmoteId: A,
        emoteName: 'catJAM',
        alias: 'catJAM',
        durationDays: 9,
      });
    });

    it('asks for an alias when nothing can fill it in (a manager the redeemer data is hidden from)', () => {
      // A name wish as a manager not confirmed by Twitch ID sees it: the server leaves out what the
      // viewer typed, so neither the wish nor a nameless 7TV read can fill in the alias.
      create(open({ requestedName: null, requestText: null, matchKind: 'name' }));
      type('wish-approve-emote', A);
      vi.advanceTimersByTime(WISH_ENTRY_LOOKUP_DEBOUNCE_MS);
      settleRead(0, { id: A, kind: 'found', name: '', animated: true });

      expect(element<HTMLInputElement>('#wish-approve-alias')?.value).toBe('');
      expect(blocked()).toBe(de.wishes.entry.blocked.alias);
      expect(submit().disabled).toBe(true);
      expect(submit().getAttribute('aria-describedby')).toBe('wish-approve-blocked');
      submit().click();
      expect(approves).toEqual([]);

      type('wish-approve-alias', 'catJAM');
      expect(blocked()).toBeNull();
      submit().click();
      expect(approves[0].body).toEqual({ sevenTvEmoteId: A, alias: 'catJAM', durationDays: 14 });
    });

    it('drops the read name from the alias when the id changes, but keeps a typed alias', () => {
      create(open({ requestedName: null, matchKind: 'none' }));
      type('wish-approve-emote', A);
      vi.advanceTimersByTime(WISH_ENTRY_LOOKUP_DEBOUNCE_MS);
      settleRead(0, { id: A, kind: 'found', name: 'catJAM', animated: true });
      expect(element<HTMLInputElement>('#wish-approve-alias')?.value).toBe('catJAM');

      type('wish-approve-emote', B);
      expect(element<HTMLInputElement>('#wish-approve-alias')?.value).toBe('');
      expect(blocked()).toBe(de.wishes.entry.blocked.reading);

      type('wish-approve-alias', 'mine');
      vi.advanceTimersByTime(WISH_ENTRY_LOOKUP_DEBOUNCE_MS);
      settleRead(1, { id: B, kind: 'found', name: 'other', animated: true });
      expect(element<HTMLInputElement>('#wish-approve-alias')?.value).toBe('mine');
    });

    it('ignores a read that comes back for an id no longer in the field', () => {
      create(open());
      type('wish-approve-emote', A);
      vi.advanceTimersByTime(WISH_ENTRY_LOOKUP_DEBOUNCE_MS);
      type('wish-approve-emote', '');
      settleRead(0, { id: A, kind: 'found', name: 'late', animated: true });

      expect(element('#wish-approve-read')?.textContent).not.toContain('late');
      expect(blocked()).toBe(de.wishes.approve.blocked.emoteMissing);
    });
  });

  describe('what the answer says', () => {
    function readyLinkWish(overrides: Partial<Wish> = {}): void {
      create(linkWish({ alias: 'catJAM', ...overrides }));
      settleRead(0, { id: A, kind: 'found', name: 'catJAM', animated: true });
    }

    it('closes as merged with the running wish and its new expiry', () => {
      readyLinkWish({ statusReason: 'already_running' });
      submit(de.wishes.actions.approveExtend).click();
      answer(0, {
        ...linkWish({ alias: 'catJAM' }),
        status: 'merged',
        mergedIntoWishId: 12,
        extendedExpiresAtUtc: '2026-12-30T10:00:00Z',
      });

      expect(closed).toEqual([
        expect.objectContaining({
          kind: 'merged',
          runningWishId: 12,
          extendedExpiresAtUtc: '2026-12-30T10:00:00Z',
        }),
      ]);
    });

    it('stays open with the reason when the running wish cannot be extended yet', () => {
      readyLinkWish({ statusReason: 'already_running' });
      submit(de.wishes.actions.approveExtend).click();
      approves[0].answer.error(
        apiError(409, { errorCode: 'wish_running_not_extendable', wishId: 12 }),
      );
      fixture.detectChanges();

      expect(closed).toEqual([]);
      expect(dialogRef.disableClose).toBe(false);
      expect(fixture.nativeElement.querySelector('p[role="status"]')?.textContent).toContain(
        de.wishes.approve.conflict.notExtendable,
      );
      // A second try may succeed once the running wish is in the set.
      expect(submit(de.wishes.actions.approveExtend).disabled).toBe(false);

      buttonNamed(de.wishes.dialog.toWish)?.click();
      expect(closed).toEqual([{ kind: 'jump', wishId: 12 }]);
    });

    it('tells the new expiry on a second try that the server merged', () => {
      readyLinkWish({ statusReason: 'already_running' });
      submit(de.wishes.actions.approveExtend).click();
      approves[0].answer.error(
        apiError(409, { errorCode: 'wish_running_not_extendable', wishId: 12 }),
      );
      fixture.detectChanges();
      submit(de.wishes.actions.approveExtend).click();
      answer(1, {
        ...linkWish(),
        status: 'merged',
        mergedIntoWishId: 12,
        extendedExpiresAtUtc: '2026-12-30T10:00:00Z',
      });

      expect(closed).toEqual([
        expect.objectContaining({ kind: 'merged', extendedExpiresAtUtc: '2026-12-30T10:00:00Z' }),
      ]);
    });

    it('offers taking the entry over for this wish when the emote is already in the set', () => {
      readyLinkWish();
      submit().click();
      approves[0].answer.error(
        apiError(409, { errorCode: 'wish_emote_already_in_set', slotIds: ['s1'], wishId: null }),
      );
      fixture.detectChanges();
      expect(closed).toEqual([]);

      buttonNamed(de.wishes.entry.conflict.takeOver)?.click();
      expect(closed).toEqual([{ kind: 'adopt', sevenTvEmoteId: A, slotIds: ['s1'] }]);
    });

    it('closes as stale when the wish is no longer open', () => {
      readyLinkWish();
      submit().click();
      approves[0].answer.error(apiError(409, { errorCode: 'wish_state_conflict' }));

      expect(closed).toEqual([{ kind: 'stale', errorKey: 'errors.api.wish_state_conflict' }]);
    });

    it('shows a refused alias at its field and keeps the dialog', () => {
      readyLinkWish();
      submit().click();
      approves[0].answer.error(apiError(400, { errorCode: 'wish_alias_invalid' }));
      fixture.detectChanges();

      expect(element('#wish-approve-alias')?.getAttribute('aria-invalid')).toBe('true');
      expect(closed).toEqual([]);
    });
  });

  it('shows what the viewer redeemed as plain text', () => {
    create(open({ requestText: '<i>x</i> https://evil.example' }));

    const quote = element('q') as HTMLElement;
    expect(quote.textContent).toBe('<i>x</i> https://evil.example');
    expect(quote.querySelector('i, a')).toBeNull();
    expect(fixture.nativeElement.textContent).toContain('gewünscht von viewer1');
  });
});
