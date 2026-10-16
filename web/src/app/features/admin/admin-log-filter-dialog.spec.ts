import { DIALOG_DATA, DialogRef } from '@angular/cdk/dialog';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import en from '../../../../public/i18n/en.json';
import { SavedLogFilter } from '../../core/admin/admin-logs.model';
import {
  AdminLogFilterDialog,
  LogFilterDialogData,
  logFilterSaveBlockedKey,
  mapLogFilterSaveError,
} from './admin-log-filter-dialog';

const SAVED: SavedLogFilter = { id: 9, name: 'Mine', filter: 'level>=warn', sortOrder: 5 };

function http(status: number, body: unknown): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: body });
}

describe('mapLogFilterSaveError', () => {
  it('puts a taken or invalid name on the name field', () => {
    expect(mapLogFilterSaveError(http(409, { errorCode: 'log_filter_name_taken' }))).toEqual({
      place: 'name',
      messageKey: 'errors.api.log_filter_name_taken',
    });
    expect(mapLogFilterSaveError(http(400, { errorCode: 'invalid_log_filter_name' }))).toEqual({
      place: 'name',
      messageKey: 'errors.api.invalid_log_filter_name',
    });
  });

  it('puts an invalid filter on the filter field and keeps position and length', () => {
    const body = {
      errorCode: 'invalid_log_filter',
      reason: 'invalid_level',
      position: 6,
      length: 5,
    };
    expect(mapLogFilterSaveError(http(400, body))).toEqual({ place: 'filter', error: body });
  });

  it('puts the limit, a vanished filter and anything else on the dialog', () => {
    expect(mapLogFilterSaveError(http(409, { errorCode: 'log_filter_limit_reached' }))).toEqual({
      place: 'dialog',
      messageKey: 'errors.api.log_filter_limit_reached',
    });
    expect(mapLogFilterSaveError(http(404, { errorCode: 'log_filter_not_found' }))).toEqual({
      place: 'dialog',
      messageKey: 'errors.api.log_filter_not_found',
    });
    expect(mapLogFilterSaveError(http(0, null)).place).toBe('dialog');
  });
});

describe('logFilterSaveBlockedKey', () => {
  it('blocks an empty filter before an empty name, and nothing else', () => {
    expect(logFilterSaveBlockedKey('', ' ')).toBe('admin.logs.savedFilters.blockedEmptyFilter');
    expect(logFilterSaveBlockedKey(' ', 'x')).toBe('admin.logs.savedFilters.blockedEmptyName');
    expect(logFilterSaveBlockedKey('n', 'x')).toBeNull();
  });
});

describe('AdminLogFilterDialog', () => {
  let fixture: ComponentFixture<AdminLogFilterDialog>;
  let httpMock: HttpTestingController;
  let closed: unknown[];
  let ref: { close: (r: unknown) => void; disableClose: boolean };

  function create(data: LogFilterDialogData): void {
    closed = [];
    ref = { close: (r: unknown) => closed.push(r), disableClose: false };
    TestBed.configureTestingModule({
      imports: [
        TranslocoTestingModule.forRoot({
          langs: { en },
          translocoConfig: { availableLangs: ['en'], defaultLang: 'en' },
        }),
      ],
      providers: [
        provideHttpClient(),
        provideHttpClientTesting(),
        { provide: DIALOG_DATA, useValue: data },
        { provide: DialogRef, useValue: ref },
      ],
    });
    httpMock = TestBed.inject(HttpTestingController);
    fixture = TestBed.createComponent(AdminLogFilterDialog);
    fixture.detectChanges();
  }

  const root = () => fixture.nativeElement as HTMLElement;
  const name = () => root().querySelector<HTMLInputElement>('#log-filter-name')!;
  const filter = () => root().querySelector<HTMLInputElement>('#log-filter-filter')!;
  const saveButton = () =>
    Array.from(root().querySelectorAll('button')).find((b) => b.textContent?.trim() === 'Save')!;

  function type(input: HTMLInputElement, value: string): void {
    input.value = value;
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
  }

  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('is locked with a visible reason while the filter is empty', () => {
    create({ filter: null, initialFilter: '' });
    type(name(), 'Mine');
    expect(saveButton().disabled).toBe(true);
    expect(root().querySelector('#log-filter-blocked-hint')?.textContent).toContain(
      'Enter a filter before saving.',
    );
    type(filter(), 'level>=warn');
    expect(saveButton().disabled).toBe(false);
  });

  it('creates with the trimmed name and the filter as typed, and closes with the stored filter', () => {
    create({ filter: null, initialFilter: '  level>=warn ' });
    expect(filter().value).toBe('  level>=warn ');
    type(name(), '  Mine ');
    saveButton().click();

    const request = httpMock.expectOne('/api/admin/log-filters');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ name: 'Mine', filter: '  level>=warn ' });
    request.flush(SAVED, { status: 201, statusText: 'Created' });
    expect(closed).toEqual([SAVED]);
  });

  it('edits an existing filter with a PUT and prefilled fields', () => {
    create({ filter: SAVED, initialFilter: 'ignored' });
    expect(name().value).toBe('Mine');
    expect(filter().value).toBe('level>=warn');
    type(name(), 'Mine!');
    saveButton().click();
    const request = httpMock.expectOne('/api/admin/log-filters/9');
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ name: 'Mine!', filter: 'level>=warn' });
  });

  it('shows a taken name on the name field, not on the filter', () => {
    create({ filter: null, initialFilter: 'level>=warn' });
    type(name(), 'Alle Fehler');
    saveButton().click();
    httpMock
      .expectOne('/api/admin/log-filters')
      .flush({ errorCode: 'log_filter_name_taken' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(name().getAttribute('aria-invalid')).toBe('true');
    expect(name().getAttribute('aria-describedby')).toBe('log-filter-name-error');
    expect(root().querySelector('#log-filter-name-error')?.textContent).toContain('already exists');
    expect(filter().getAttribute('aria-invalid')).toBeNull();
    expect(closed).toEqual([]);
    expect(saveButton().disabled).toBe(false);
  });

  it('shows an invalid filter on the filter field and selects the named spot', async () => {
    create({ filter: null, initialFilter: 'level:debug' });
    type(name(), 'Mine');
    saveButton().click();
    httpMock
      .expectOne('/api/admin/log-filters')
      .flush(
        { errorCode: 'invalid_log_filter', reason: 'invalid_level', position: 6, length: 5 },
        { status: 400, statusText: 'Bad Request' },
      );
    fixture.detectChanges();
    await fixture.whenStable();

    expect(filter().getAttribute('aria-invalid')).toBe('true');
    expect(root().querySelector('#log-filter-filter-error')?.textContent).toContain(
      en.admin.logs.filterErrors.invalid_level,
    );
    expect([filter().selectionStart, filter().selectionEnd]).toEqual([6, 11]);
    expect(name().getAttribute('aria-invalid')).toBeNull();
  });

  it('shows the limit and a vanished filter on the dialog itself', () => {
    create({ filter: SAVED, initialFilter: '' });
    saveButton().click();
    httpMock
      .expectOne('/api/admin/log-filters/9')
      .flush({ errorCode: 'log_filter_not_found' }, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();
    expect(root().querySelector('[role="alert"]')?.textContent).toContain('no longer exists');
    expect(name().getAttribute('aria-invalid')).toBeNull();
    expect(filter().getAttribute('aria-invalid')).toBeNull();
  });

  it('cannot be dismissed while the save is out, so a late answer cannot get lost', () => {
    create({ filter: null, initialFilter: 'level>=warn' });
    type(name(), 'Mine');
    const cancel = () =>
      Array.from(root().querySelectorAll('button')).find(
        (b) => b.textContent?.trim() === 'Cancel',
      )!;
    saveButton().click();
    fixture.detectChanges();

    expect(ref.disableClose).toBe(true);
    expect(cancel().disabled).toBe(true);

    httpMock
      .expectOne('/api/admin/log-filters')
      .flush({ errorCode: 'log_filter_name_taken' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(ref.disableClose).toBe(false);
    expect(cancel().disabled).toBe(false);
  });

  it('clears a field error as soon as that field is edited', () => {
    create({ filter: null, initialFilter: 'level>=warn' });
    type(name(), 'Alle Fehler');
    saveButton().click();
    httpMock
      .expectOne('/api/admin/log-filters')
      .flush({ errorCode: 'log_filter_name_taken' }, { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();
    type(name(), 'Alle Fehler 2');
    expect(name().getAttribute('aria-invalid')).toBeNull();
  });
});
