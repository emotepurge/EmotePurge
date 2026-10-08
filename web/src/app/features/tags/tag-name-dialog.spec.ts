import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import de from '../../../../public/i18n/de.json';
import { EmoteTag } from '../../core/tags/emote-tag.model';
import { TagNameDialog, TagNameDialogData } from './tag-name-dialog';

const BASE = '/api/channels/sensitron/tags';

describe('TagNameDialog', () => {
  let httpMock: HttpTestingController;
  let closed: (EmoteTag | undefined)[];
  let dialogRef: { close: (result?: EmoteTag) => void; disableClose: boolean };

  function setUp(data: TagNameDialogData) {
    closed = [];
    dialogRef = { close: (result) => closed.push(result), disableClose: false };
    TestBed.configureTestingModule({
      imports: [
        TagNameDialog,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: dialogRef },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);

    const fixture = TestBed.createComponent(TagNameDialog);
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    const input = () => host.querySelector<HTMLInputElement>('#tag-name-dialog-input')!;
    const button = (label: string) =>
      Array.from(host.querySelectorAll('button')).find(
        (candidate) => candidate.textContent?.trim() === label,
      ) as HTMLButtonElement;
    return {
      fixture,
      host,
      input,
      button,
      type(value: string) {
        input().value = value;
        input().dispatchEvent(new Event('input'));
        fixture.detectChanges();
      },
    };
  }

  beforeEach(() => TestBed.resetTestingModule());

  afterEach(() => httpMock.verify());

  it('create: posts the trimmed name and closes with the stored tag', () => {
    const view = setUp({ channelName: 'sensitron' });
    expect(view.host.textContent).toContain(de.tags.nameDialog.createTitle);

    view.type('  Stronghold ');
    view.button(de.tags.nameDialog.confirmCreate).click();
    const req = httpMock.expectOne(BASE);
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ name: 'Stronghold' });
    // Not dismissible while the request is out.
    expect(dialogRef.disableClose).toBe(true);
    expect(view.button(de.tags.nameDialog.confirmCreate).disabled).toBe(false);
    view.fixture.detectChanges();
    expect(view.button(de.tags.nameDialog.confirmCreate).disabled).toBe(true);

    req.flush({ id: 7, name: 'Stronghold' });
    expect(closed).toEqual([{ id: 7, name: 'Stronghold' }]);
  });

  it('rename: prefilled with the current name, sends a PATCH', () => {
    const view = setUp({ channelName: 'sensitron', tag: { id: 3, name: 'Alt' } });
    expect(view.host.textContent).toContain(de.tags.nameDialog.renameTitle);
    expect(view.input().value).toBe('Alt');

    view.type('Neu');
    view.button(de.tags.nameDialog.confirmRename).click();
    const req = httpMock.expectOne(`${BASE}/3`);
    expect(req.request.method).toBe('PATCH');
    expect(req.request.body).toEqual({ name: 'Neu' });
    req.flush({ id: 3, name: 'Neu' });
    expect(closed).toEqual([{ id: 3, name: 'Neu' }]);
  });

  it('rename: a taken name is a field error on the input, and the dialog stays open', () => {
    const view = setUp({ channelName: 'sensitron', tag: { id: 3, name: 'Alt' } });
    view.type('Halloween');
    view.button(de.tags.nameDialog.confirmRename).click();

    httpMock
      .expectOne(`${BASE}/3`)
      .flush({ errorCode: 'tag_name_taken' }, { status: 409, statusText: 'Conflict' });
    view.fixture.detectChanges();

    expect(closed).toEqual([]);
    expect(view.input().getAttribute('aria-invalid')).toBe('true');
    const errorId = view.input().getAttribute('aria-describedby')!;
    expect(view.host.querySelector(`#${errorId}`)?.textContent).toContain(
      de.errors.api.tag_name_taken,
    );
    expect(view.host.querySelector('[role="alert"]')).toBeNull();
    expect(dialogRef.disableClose).toBe(false);
  });

  it('a failure that is not about the name goes to a banner', () => {
    const view = setUp({ channelName: 'sensitron', tag: { id: 3, name: 'Alt' } });
    view.type('Neu');
    view.button(de.tags.nameDialog.confirmRename).click();

    httpMock
      .expectOne(`${BASE}/3`)
      .flush({ errorCode: 'tag_not_found' }, { status: 404, statusText: 'Not Found' });
    view.fixture.detectChanges();

    expect(view.host.querySelector('[role="alert"]')?.textContent).toContain(
      de.errors.api.tag_not_found,
    );
    expect(view.input().getAttribute('aria-invalid')).toBeNull();
    expect(closed).toEqual([]);
  });

  it('an empty name is refused before any request', () => {
    const view = setUp({ channelName: 'sensitron' });
    view.type('   ');
    view.button(de.tags.nameDialog.confirmCreate).click();
    view.fixture.detectChanges();

    httpMock.expectNone(BASE);
    expect(view.input().getAttribute('aria-invalid')).toBe('true');
  });

  it('cancel closes without a result', () => {
    const view = setUp({ channelName: 'sensitron' });
    view.button(de.common.cancel).click();
    expect(closed).toEqual([undefined]);
  });
});
