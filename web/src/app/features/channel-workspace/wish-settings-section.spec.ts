import { HttpErrorResponse } from '@angular/common/http';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Observable, of, Subject, throwError } from 'rxjs';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { EVENT_SOURCE_FACTORY } from '../../core/live/event-source.factory';
import { LIVE_EVENT_TYPES } from '../../core/live/live-event.model';
import type {
  WishSettings,
  WishSettingsRead,
  WishSettingsUpdate,
} from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import en from '../../../../public/i18n/en.json';
import { WishSettingsSection, wishSettingsBody } from './wish-settings-section';

class FakeEventSource {
  static instances: FakeEventSource[] = [];
  onmessage: ((event: MessageEvent) => void) | null = null;
  onopen: (() => void) | null = null;
  onerror: (() => void) | null = null;

  constructor(readonly url: string) {
    FakeEventSource.instances.push(this);
  }

  close(): void {
    /* no-op */
  }

  emit(event: { type: string; channel?: string }): void {
    this.onmessage?.({ data: JSON.stringify(event) } as MessageEvent);
  }
}

function read(over: Partial<WishSettingsRead> = {}): WishSettingsRead {
  return {
    defaultDurationDays: 14,
    probationLeadDays: 3,
    protectionThresholdPerDay: 5,
    botModeEnabled: false,
    botSuspendedReason: null,
    canEdit: true,
    ...over,
  };
}

function httpError(status: number, errorCode?: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: errorCode ? { errorCode } : null });
}

describe('wishSettingsBody', () => {
  it('carries the two whole-day values and never the protection threshold', () => {
    const body = wishSettingsBody(' 21 ', '2');

    expect(body).toEqual({ defaultDurationDays: 21, probationLeadDays: 2 });
    expect(body && 'protectionThresholdPerDay' in body).toBe(false);
  });

  it.each([
    ['', '3'],
    ['0', '3'],
    ['366', '3'],
    ['14', '0'],
    ['14', '31'],
    ['1.5', '3'],
    ['14', 'x'],
  ])('is null for duration %j and lead %j', (duration, lead) => {
    expect(wishSettingsBody(duration, lead)).toBeNull();
  });

  it('accepts both bounds', () => {
    expect(wishSettingsBody('1', '1')).not.toBeNull();
    expect(wishSettingsBody('365', '30')).not.toBeNull();
  });
});

describe('WishSettingsSection', () => {
  let getSettings: ReturnType<typeof vi.fn<(channel: string) => Observable<WishSettingsRead>>>;
  let updateSettings: ReturnType<
    typeof vi.fn<(channel: string, body: WishSettingsUpdate) => Observable<WishSettings>>
  >;

  async function create(): Promise<ComponentFixture<WishSettingsSection>> {
    FakeEventSource.instances = [];
    TestBed.configureTestingModule({
      imports: [
        WishSettingsSection,
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [
        { provide: WishService, useValue: { getSettings, updateSettings } },
        {
          provide: EVENT_SOURCE_FACTORY,
          useValue: (url: string) => new FakeEventSource(url) as unknown as EventSource,
        },
      ],
    });
    const fixture = TestBed.createComponent(WishSettingsSection);
    fixture.componentRef.setInput('channelName', 'sensitron');
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    return fixture;
  }

  function el(fixture: ComponentFixture<unknown>): HTMLElement {
    return fixture.nativeElement as HTMLElement;
  }

  function input(fixture: ComponentFixture<unknown>, field: 'duration' | 'lead'): HTMLInputElement {
    return el(fixture).querySelector<HTMLInputElement>(`#wish-settings-${field}`)!;
  }

  function saveButton(fixture: ComponentFixture<unknown>): HTMLButtonElement | undefined {
    return Array.from(el(fixture).querySelectorAll('button')).find((button) =>
      /^\s*Save\s*$/.test(button.textContent ?? ''),
    );
  }

  function type(
    fixture: ComponentFixture<unknown>,
    field: 'duration' | 'lead',
    value: string,
  ): void {
    const target = input(fixture, field);
    target.value = value;
    target.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  async function nudge(fixture: ComponentFixture<unknown>): Promise<void> {
    vi.useFakeTimers();
    try {
      FakeEventSource.instances[0].emit({
        type: LIVE_EVENT_TYPES.wishChanged,
        channel: 'sensitron',
      });
      await vi.advanceTimersByTimeAsync(600);
    } finally {
      vi.useRealTimers();
    }
    await fixture.whenStable();
    fixture.detectChanges();
  }

  beforeEach(() => {
    getSettings = vi.fn(() => of(read()));
    updateSettings = vi.fn((_channel, body) =>
      of({
        ...read(),
        defaultDurationDays: body.defaultDurationDays,
        probationLeadDays: body.probationLeadDays,
      }),
    );
  });

  it('fills the fields from the loaded settings', async () => {
    const fixture = await create();

    expect(input(fixture, 'duration').value).toBe('14');
    expect(input(fixture, 'lead').value).toBe('3');
    expect(getSettings).toHaveBeenCalledWith('sensitron');
  });

  it('keeps Save locked until a valid change, then sends only the two values', async () => {
    const fixture = await create();
    expect(saveButton(fixture)!.disabled).toBe(true);

    type(fixture, 'duration', '30');
    expect(saveButton(fixture)!.disabled).toBe(false);

    saveButton(fixture)!.click();
    fixture.detectChanges();

    expect(updateSettings).toHaveBeenCalledTimes(1);
    expect(updateSettings).toHaveBeenCalledWith('sensitron', {
      defaultDurationDays: 30,
      probationLeadDays: 3,
    });
    const body = updateSettings.mock.calls[0][1];
    expect('protectionThresholdPerDay' in body).toBe(false);
  });

  it('names a value outside its range at the field and does not send it', async () => {
    const fixture = await create();

    type(fixture, 'duration', '400');
    type(fixture, 'lead', '31');

    expect(input(fixture, 'duration').getAttribute('aria-invalid')).toBe('true');
    expect(input(fixture, 'lead').getAttribute('aria-invalid')).toBe('true');
    const describedBy = input(fixture, 'lead').getAttribute('aria-describedby') ?? '';
    expect(describedBy).toContain('wish-settings-lead-error');
    expect(el(fixture).querySelector('#wish-settings-lead-error')).not.toBeNull();
    expect(saveButton(fixture)!.disabled).toBe(true);
    expect(updateSettings).not.toHaveBeenCalled();
  });

  it('confirms a save in place through a permanently mounted status region and keeps focus', async () => {
    const fixture = await create();
    const status = el(fixture).querySelector('[role="status"]')!;
    expect(status.textContent?.trim()).toBe('');

    type(fixture, 'lead', '5');
    const button = saveButton(fixture)!;
    button.focus();
    button.click();
    fixture.detectChanges();

    expect(el(fixture).querySelector('[role="status"]')).toBe(status);
    expect(status.textContent).toContain('Settings saved');
    expect(document.activeElement).toBe(button);
    // The saved values are the new baseline, so nothing is left to save.
    expect(saveButton(fixture)!.disabled).toBe(true);
  });

  it('locks Save while the request is out', async () => {
    updateSettings.mockReturnValue(new Observable<WishSettings>(() => undefined));
    const fixture = await create();

    type(fixture, 'duration', '30');
    saveButton(fixture)!.click();
    fixture.detectChanges();
    expect(saveButton(fixture)!.disabled).toBe(true);
    saveButton(fixture)!.click();

    expect(updateSettings).toHaveBeenCalledTimes(1);
  });

  it('holds both fields read-only while the save is out, and editable again after its answer', async () => {
    const answer = new Subject<WishSettings>();
    updateSettings.mockReturnValue(answer);
    const fixture = await create();

    type(fixture, 'duration', '30');
    saveButton(fixture)!.click();
    fixture.detectChanges();
    expect(input(fixture, 'duration').readOnly).toBe(true);
    expect(input(fixture, 'lead').readOnly).toBe(true);

    answer.next({ ...read(), defaultDurationDays: 30 });
    fixture.detectChanges();
    expect(input(fixture, 'duration').readOnly).toBe(false);
    expect(input(fixture, 'lead').readOnly).toBe(false);
    expect(input(fixture, 'duration').value).toBe('30');
  });

  it('lets no read issued before a save undo the saved values', async () => {
    const fixture = await create();
    // A live nudge while the form is clean sends a read that stays on the way …
    const staleRead = new Subject<WishSettingsRead>();
    getSettings.mockReturnValue(staleRead);
    await nudge(fixture);
    expect(getSettings).toHaveBeenCalledTimes(2);

    // … the manager edits and saves; the save answers first …
    type(fixture, 'duration', '30');
    saveButton(fixture)!.click();
    fixture.detectChanges();
    expect(input(fixture, 'duration').value).toBe('30');

    // … and the old read lands last: the saved value stays.
    staleRead.next(read({ defaultDurationDays: 14 }));
    fixture.detectChanges();
    expect(input(fixture, 'duration').value).toBe('30');
    expect(saveButton(fixture)!.disabled).toBe(true);
    expect(el(fixture).querySelector('[role="status"]')?.textContent).toContain('Settings saved');
  });

  it('is read-only with an explanation when the server says the caller cannot edit', async () => {
    getSettings.mockReturnValue(of(read({ canEdit: false })));
    const fixture = await create();

    expect(input(fixture, 'duration').disabled).toBe(true);
    expect(input(fixture, 'lead').disabled).toBe(true);
    expect(saveButton(fixture)).toBeUndefined();
    expect(el(fixture).querySelector('#wish-settings-read-only')).not.toBeNull();
    expect(input(fixture, 'duration').getAttribute('aria-describedby')).toContain(
      'wish-settings-read-only',
    );
  });

  it('turns read-only when a save is answered with 403 wish_settings_restricted', async () => {
    updateSettings.mockReturnValue(throwError(() => httpError(403, 'wish_settings_restricted')));
    const fixture = await create();

    type(fixture, 'duration', '30');
    saveButton(fixture)!.click();
    fixture.detectChanges();

    expect(input(fixture, 'duration').disabled).toBe(true);
    expect(saveButton(fixture)).toBeUndefined();
    expect(el(fixture).querySelector('#wish-settings-read-only')).not.toBeNull();
  });

  it('shows a server refusal of the range as a banner and keeps the input', async () => {
    updateSettings.mockReturnValue(throwError(() => httpError(400, 'wish_settings_out_of_range')));
    const fixture = await create();

    type(fixture, 'duration', '30');
    saveButton(fixture)!.click();
    fixture.detectChanges();

    expect(el(fixture).querySelector('[role="alert"]')?.textContent).toContain(
      'outside the allowed range',
    );
    expect(input(fixture, 'duration').value).toBe('30');
    expect(saveButton(fixture)!.disabled).toBe(false);
  });

  it('shows only a fact when the bot mode is on', async () => {
    getSettings.mockReturnValue(of(read({ botModeEnabled: true, botSuspendedReason: 'x' })));
    const fixture = await create();

    expect(el(fixture).textContent).toContain('Bot mode is active.');
    expect(input(fixture, 'duration').disabled).toBe(false);
  });

  it('reloads quietly on wish.changed while the form is clean', async () => {
    const fixture = await create();
    getSettings.mockReturnValue(of(read({ defaultDurationDays: 20 })));

    await nudge(fixture);

    expect(getSettings).toHaveBeenCalledTimes(2);
    expect(input(fixture, 'duration').value).toBe('20');
  });

  it('does not overwrite unsaved input on wish.changed, but says so and offers a reload', async () => {
    const fixture = await create();
    type(fixture, 'duration', '30');
    getSettings.mockReturnValue(of(read({ defaultDurationDays: 20 })));

    await nudge(fixture);

    expect(getSettings).toHaveBeenCalledTimes(1);
    expect(input(fixture, 'duration').value).toBe('30');
    expect(el(fixture).querySelector('[role="status"]')?.textContent).toContain(
      'changed in the meantime',
    );

    const reload = Array.from(el(fixture).querySelectorAll('button')).find((button) =>
      /Reload/.test(button.textContent ?? ''),
    )!;
    reload.click();
    fixture.detectChanges();

    expect(input(fixture, 'duration').value).toBe('20');
    expect(el(fixture).querySelector('[role="status"]')?.textContent?.trim()).toBe('');
  });

  it('says the load failed and loads again on retry', async () => {
    getSettings.mockReturnValueOnce(throwError(() => httpError(503)));
    const fixture = await create();

    expect(input(fixture, 'duration')).toBeNull();
    expect(el(fixture).querySelector('[role="alert"]')).not.toBeNull();

    const retry = Array.from(el(fixture).querySelectorAll('button')).find((button) =>
      /Try again/.test(button.textContent ?? ''),
    )!;
    retry.click();
    fixture.detectChanges();

    expect(input(fixture, 'duration').value).toBe('14');
  });
});
