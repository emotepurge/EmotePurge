import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Subject, filter } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { TagAssignDialog, TagAssignDialogData, TagAssignDialogResult } from './tag-assign-dialog';

const DE = {
  common: { cancel: 'Abbrechen', close: 'Schließen', loading: 'Lädt' },
  errors: {
    api: {
      tag_name_taken: 'Name schon vergeben.',
      tag_name_invalid: 'Ungültiger Name.',
      tag_entry_limit_reached: 'Tag voll.',
    },
    status: { server: 'Serverfehler.' },
  },
  tags: {
    assignDialog: {
      title: 'Tag zuweisen',
      listLabel: 'Tags',
      newTagLabel: 'Neuer Tag',
      createButton: 'Anlegen',
      noTagsHint: 'Noch keine Tags.',
      confirm: { one: '{{count}} Emote zuweisen', other: '{{count}} Emotes zuweisen' },
      lockReason: {
        noneChecked: 'Wähle mindestens einen Tag aus.',
        submitting: 'Die Zuweisung läuft.',
      },
      partial: 'Schon zugewiesen: {{tags}}.',
    },
  },
};

const BASE = '/api/channels/sensitron/tags';

function tagList(...names: [number, string][]) {
  return {
    emoteSetId: 's1',
    isActiveSet: true,
    tags: names.map(([id, name]) => ({ id, name, entryCount: 0, inSetCount: 0 })),
  };
}

describe('TagAssignDialog', () => {
  let httpMock: HttpTestingController;
  let closed: (TagAssignDialogResult | undefined)[];
  let keydown: Subject<KeyboardEvent>;
  let backdrop: Subject<MouseEvent>;
  let dialogRef: { close: (r?: TagAssignDialogResult) => void; disableClose: boolean };

  beforeEach(() => {
    closed = [];
    keydown = new Subject();
    backdrop = new Subject();
    dialogRef = { close: (r) => closed.push(r), disableClose: false };
    // Stand-in for the CDK's own handling, which is what the dialog has to cooperate with: Escape
    // and a backdrop click close with `undefined` unless `disableClose` is set.
    const cdkDismiss = () => {
      if (!dialogRef.disableClose) {
        dialogRef.close(undefined);
      }
    };
    keydown.pipe(filter((e) => e.key === 'Escape')).subscribe(cdkDismiss);
    backdrop.subscribe(cdkDismiss);
    TestBed.configureTestingModule({
      imports: [
        TagAssignDialog,
        TranslocoTestingModule.forRoot({
          langs: { de: DE },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        {
          provide: DIALOG_DATA,
          useValue: { channelName: 'sensitron', sevenTvEmoteIds: ['e1', 'e2', 'e3'] },
        },
        {
          provide: DialogRef,
          useValue: Object.assign(dialogRef, {
            keydownEvents: keydown,
            backdropClick: backdrop,
          }),
        },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
  });

  function render(list = tagList([1, 'Stronghold'], [2, 'Boss'])) {
    const fixture: ComponentFixture<TagAssignDialog> = TestBed.createComponent(TagAssignDialog);
    fixture.detectChanges();
    if (list) {
      httpMock.expectOne(BASE).flush(list);
    }
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    const button = (label: string) =>
      Array.from(host.querySelectorAll('button')).find(
        (b) => b.textContent?.trim() === label,
      ) as HTMLButtonElement;
    return {
      fixture,
      host,
      confirm: () => button('3 Emotes zuweisen'),
      checkbox: (i: number) => host.querySelectorAll<HTMLInputElement>('input[type=checkbox]')[i],
      tick(i: number) {
        const box = this.checkbox(i);
        box.checked = !box.checked;
        box.dispatchEvent(new Event('change'));
        fixture.detectChanges();
      },
      typeName(value: string) {
        const input = host.querySelector<HTMLInputElement>('#tag-assign-new-name')!;
        input.value = value;
        input.dispatchEvent(new Event('input'));
        button('Anlegen').click();
        fixture.detectChanges();
      },
      text: () => host.textContent ?? '',
    };
  }

  function added(addedCount: number, skipped: string[] = [], already = 0) {
    return { addedCount, alreadyTaggedCount: already, skippedNotInSetIds: skipped };
  }

  it('is a named dialog and locks confirm with a described reason until a tag is ticked', () => {
    const view = render();
    expect(view.text()).toContain('Tag zuweisen');
    expect(view.confirm().disabled).toBe(true);
    const hintId = view.confirm().getAttribute('aria-describedby')!;
    expect(view.host.querySelector(`#${hintId}`)?.textContent).toContain('Wähle mindestens');
    view.tick(0);
    expect(view.confirm().disabled).toBe(false);
    expect(view.confirm().getAttribute('aria-describedby')).toBeNull();
    httpMock.verify();
  });

  it('shows a skeleton before the list arrives', () => {
    const fixture = TestBed.createComponent(TagAssignDialog);
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('[role=status]')).not.toBeNull();
    httpMock.expectOne(BASE).flush(tagList());
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Noch keine Tags.');
  });

  it('creating a tag adds it ticked; a taken name stays a field error and the dialog open', () => {
    const view = render();
    view.typeName('Boss');
    httpMock
      .expectOne({ method: 'POST', url: BASE })
      .flush({ errorCode: 'tag_name_taken' }, { status: 409, statusText: 'Conflict' });
    view.fixture.detectChanges();
    expect(view.text()).toContain('Name schon vergeben.');
    expect(closed).toEqual([]);

    view.typeName('Neu');
    const req = httpMock.expectOne({ method: 'POST', url: BASE });
    expect(req.request.body).toEqual({ name: 'Neu' });
    req.flush({ id: 7, name: 'Neu' });
    view.fixture.detectChanges();
    expect(view.checkbox(2).checked).toBe(true);
    expect(view.text()).toContain('Neu');
    expect(view.confirm().disabled).toBe(false);
  });

  it('sends one create request however often the name is submitted while it is in flight', () => {
    const view = render();
    const dialog = view.fixture.componentInstance;

    dialog['create']('Neu');
    dialog['create']('Neu');

    const req = httpMock.expectOne({ method: 'POST', url: BASE });
    req.flush({ id: 7, name: 'Neu' });
    httpMock.verify();
  });

  it('inline create while the list is still loading: the new tag stays listed and ticked, confirm sends one POST', () => {
    const fixture = TestBed.createComponent(TagAssignDialog);
    fixture.detectChanges();
    const dialog = fixture.componentInstance;
    dialog['create']('Neu');
    httpMock.expectOne({ method: 'POST', url: BASE }).flush({ id: 7, name: 'Neu' });
    httpMock.expectOne(BASE).flush(tagList([1, 'Stronghold']));
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    const boxes = host.querySelectorAll<HTMLInputElement>('input[type=checkbox]');
    expect(boxes.length).toBe(2);
    expect(boxes[1].checked).toBe(true);
    expect(host.textContent).toContain('Neu');

    const confirm = Array.from(host.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === '3 Emotes zuweisen',
    ) as HTMLButtonElement;
    confirm.click();
    httpMock.expectOne(`${BASE}/7/entries`).flush(added(3));
    expect(closed).toEqual([{ tagNames: ['Neu'], emoteCount: 3, skippedNotInSetCount: 0 }]);
    httpMock.verify();
  });

  it('a failed list read after an inline create keeps the new tag listed, ticked and assignable', () => {
    const fixture = TestBed.createComponent(TagAssignDialog);
    fixture.detectChanges();
    fixture.componentInstance['create']('Neu');
    httpMock.expectOne({ method: 'POST', url: BASE }).flush({ id: 7, name: 'Neu' });
    httpMock.expectOne(BASE).flush({ errorCode: 'x' }, { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    const boxes = host.querySelectorAll<HTMLInputElement>('input[type=checkbox]');
    expect(boxes.length).toBe(1);
    expect(boxes[0].checked).toBe(true);
    expect(host.textContent).toContain('Serverfehler.');

    const confirm = Array.from(host.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === '3 Emotes zuweisen',
    ) as HTMLButtonElement;
    confirm.click();
    httpMock.expectOne(`${BASE}/7/entries`).flush(added(3));
    expect(closed).toEqual([{ tagNames: ['Neu'], emoteCount: 3, skippedNotInSetCount: 0 }]);
    httpMock.verify();
  });

  it('a list read that already contains the created tag does not list it twice or assign it twice', () => {
    const fixture = TestBed.createComponent(TagAssignDialog);
    fixture.detectChanges();
    fixture.componentInstance['create']('Neu');
    httpMock.expectOne({ method: 'GET', url: BASE }).flush(tagList([1, 'Stronghold'], [7, 'Neu']));
    httpMock.expectOne({ method: 'POST', url: BASE }).flush({ id: 7, name: 'Neu' });
    fixture.detectChanges();

    const host: HTMLElement = fixture.nativeElement;
    expect(host.querySelectorAll('input[type=checkbox]').length).toBe(2);

    const confirm = Array.from(host.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === '3 Emotes zuweisen',
    ) as HTMLButtonElement;
    confirm.click();
    httpMock.expectOne(`${BASE}/7/entries`).flush(added(3));
    expect(closed).toEqual([{ tagNames: ['Neu'], emoteCount: 3, skippedNotInSetCount: 0 }]);
    httpMock.verify();
  });

  it('cannot be dismissed while an inline create is pending, and can be afterwards', () => {
    const view = render();
    view.fixture.componentInstance['create']('Neu');
    view.fixture.detectChanges();
    const cancel = Array.from(view.host.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'Abbrechen',
    ) as HTMLButtonElement;
    expect(cancel.disabled).toBe(true);
    keydown.next(new KeyboardEvent('keydown', { key: 'Escape' }));
    backdrop.next(new MouseEvent('click'));
    expect(closed).toEqual([]);

    httpMock.expectOne({ method: 'POST', url: BASE }).flush({ id: 7, name: 'Neu' });
    view.fixture.detectChanges();
    expect(cancel.disabled).toBe(false);
    keydown.next(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(closed).toEqual([undefined]);
  });

  it('assigns tag by tag in list order and closes with one count over all tags', () => {
    const view = render();
    view.tick(1);
    view.tick(0);
    view.confirm().click();

    const first = httpMock.expectOne(`${BASE}/1/entries`);
    expect(first.request.body).toEqual({ sevenTvEmoteIds: ['e1', 'e2', 'e3'] });
    httpMock.expectNone(`${BASE}/2/entries`);
    first.flush(added(2, ['e3'], 0));
    httpMock.expectOne(`${BASE}/2/entries`).flush(added(1, ['e3'], 1));

    expect(closed).toEqual([
      { tagNames: ['Stronghold', 'Boss'], emoteCount: 2, skippedNotInSetCount: 1 },
    ]);
    httpMock.verify();
  });

  it('a failure on the second tag keeps the first in the result and offers only the rest again', () => {
    const view = render();
    view.tick(0);
    view.tick(1);
    view.confirm().click();
    httpMock.expectOne(`${BASE}/1/entries`).flush(added(3));
    httpMock
      .expectOne(`${BASE}/2/entries`)
      .flush({ errorCode: 'tag_entry_limit_reached' }, { status: 409, statusText: 'Conflict' });
    view.fixture.detectChanges();

    expect(closed).toEqual([]);
    expect(view.text()).toContain('Tag voll.');
    expect(view.text()).toContain('Schon zugewiesen: Stronghold.');
    expect(view.checkbox(0).checked).toBe(false);
    expect(view.checkbox(1).checked).toBe(true);
    expect(dialogRef.disableClose).toBe(true);

    keydown.next(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(closed).toEqual([{ tagNames: ['Stronghold'], emoteCount: 3, skippedNotInSetCount: 0 }]);
  });

  it('counts the union of emotes over all tags, not the largest single batch', () => {
    const view = render();
    view.tick(0);
    view.tick(1);
    view.confirm().click();
    httpMock.expectOne(`${BASE}/1/entries`).flush(added(2, ['e3']));
    httpMock.expectOne(`${BASE}/2/entries`).flush(added(2, ['e1']));

    expect(closed).toEqual([
      { tagNames: ['Stronghold', 'Boss'], emoteCount: 3, skippedNotInSetCount: 2 },
    ]);
    httpMock.verify();
  });

  it('names each tag and counts each emote once when a retry repeats a tag that already went through', () => {
    const view = render();
    view.tick(0);
    view.tick(1);
    view.confirm().click();
    httpMock.expectOne(`${BASE}/1/entries`).flush(added(3));
    httpMock
      .expectOne(`${BASE}/2/entries`)
      .flush({ errorCode: 'tag_entry_limit_reached' }, { status: 409, statusText: 'Conflict' });
    view.fixture.detectChanges();

    view.tick(0);
    view.confirm().click();
    httpMock.expectOne(`${BASE}/1/entries`).flush(added(0, [], 3));
    httpMock.expectOne(`${BASE}/2/entries`).flush(added(3));

    expect(closed).toEqual([
      { tagNames: ['Stronghold', 'Boss'], emoteCount: 3, skippedNotInSetCount: 0 },
    ]);
    httpMock.verify();
  });

  it('Escape or a backdrop click without any assignment closes once with undefined', () => {
    render();
    keydown.next(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(closed).toEqual([undefined]);
    closed.length = 0;
    backdrop.next(new MouseEvent('click'));
    expect(closed).toEqual([undefined]);
  });

  it('cannot be dismissed while a request is pending, then closes once with the result', () => {
    const view = render();
    view.tick(0);
    view.confirm().click();
    view.fixture.detectChanges();
    const cancel = Array.from(view.host.querySelectorAll('button')).find(
      (b) => b.textContent?.trim() === 'Abbrechen',
    ) as HTMLButtonElement;
    expect(cancel.disabled).toBe(true);

    keydown.next(new KeyboardEvent('keydown', { key: 'Escape' }));
    backdrop.next(new MouseEvent('click'));
    expect(closed).toEqual([]);

    httpMock.expectOne(`${BASE}/1/entries`).flush(added(3));
    expect(closed).toEqual([{ tagNames: ['Stronghold'], emoteCount: 3, skippedNotInSetCount: 0 }]);
  });

  it('a first-request failure makes the dialog dismissible again', () => {
    const view = render();
    view.tick(0);
    view.confirm().click();
    httpMock.expectOne(`${BASE}/1/entries`).flush({}, { status: 500, statusText: 'x' });
    view.fixture.detectChanges();
    expect(dialogRef.disableClose).toBe(false);
    keydown.next(new KeyboardEvent('keydown', { key: 'Escape' }));
    expect(closed).toEqual([undefined]);
  });
});
