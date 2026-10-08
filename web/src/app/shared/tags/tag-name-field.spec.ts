import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { beforeEach, describe, expect, it } from 'vitest';

import {
  TAG_NAME_INVALID_KEY,
  TAG_NAME_MAX_LENGTH,
  TagNameField,
  tagNameFieldErrorKey,
  validTagName,
} from './tag-name-field';

const DE = {
  errors: {
    api: {
      tag_name_invalid: 'Ungültiger Name.',
      tag_name_taken: 'Name schon vergeben.',
      tag_limit_reached: 'Zu viele Tags.',
      tag_not_found: 'Tag weg.',
    },
  },
};

function apiError(status: number, errorCode?: string): HttpErrorResponse {
  return new HttpErrorResponse({ status, error: errorCode ? { errorCode } : null });
}

describe('validTagName', () => {
  it('trims and accepts a normal name', () => {
    expect(validTagName('  Stronghold ')).toBe('Stronghold');
  });

  it('rejects empty and whitespace-only names', () => {
    expect(validTagName('')).toBeNull();
    expect(validTagName('   ')).toBeNull();
  });

  it('accepts exactly the maximum length and rejects one more, measured after trimming', () => {
    expect(validTagName(` ${'a'.repeat(TAG_NAME_MAX_LENGTH)} `)).toBe(
      'a'.repeat(TAG_NAME_MAX_LENGTH),
    );
    expect(validTagName('a'.repeat(TAG_NAME_MAX_LENGTH + 1))).toBeNull();
  });

  it('rejects control characters inside the name', () => {
    expect(validTagName('a\tb')).toBeNull();
    expect(validTagName('a\u0085b')).toBeNull();
    expect(validTagName('a\u007fb')).toBeNull();
  });
});

describe('tagNameFieldErrorKey', () => {
  it('maps the three name/limit codes to the field', () => {
    expect(tagNameFieldErrorKey(apiError(409, 'tag_name_taken'))).toBe('errors.api.tag_name_taken');
    expect(tagNameFieldErrorKey(apiError(400, 'tag_name_invalid'))).toBe(TAG_NAME_INVALID_KEY);
    expect(tagNameFieldErrorKey(apiError(409, 'tag_limit_reached'))).toBe(
      'errors.api.tag_limit_reached',
    );
  });

  it('leaves every other failure to the host', () => {
    expect(tagNameFieldErrorKey(apiError(404, 'tag_not_found'))).toBeNull();
    expect(tagNameFieldErrorKey(apiError(500))).toBeNull();
    expect(tagNameFieldErrorKey(apiError(0))).toBeNull();
  });
});

describe('TagNameField', () => {
  let submitted: string[];

  beforeEach(() => {
    submitted = [];
    TestBed.configureTestingModule({
      imports: [
        TagNameField,
        TranslocoTestingModule.forRoot({
          langs: { de: DE },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    });
  });

  function render(initialName = '') {
    const fixture = TestBed.createComponent(TagNameField);
    fixture.componentRef.setInput('label', 'Name');
    fixture.componentRef.setInput('initialName', initialName);
    fixture.componentInstance.submitted.subscribe((name) => submitted.push(name));
    fixture.detectChanges();
    const host: HTMLElement = fixture.nativeElement;
    const input = host.querySelector('input') as HTMLInputElement;
    return {
      fixture,
      input,
      type(value: string) {
        input.value = value;
        input.dispatchEvent(new Event('input'));
        fixture.detectChanges();
      },
      enter() {
        input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter' }));
        fixture.detectChanges();
      },
      error: () => host.querySelector('p')?.textContent?.trim() ?? null,
    };
  }

  it('prefills from initialName', () => {
    expect(render('Alt').input.value).toBe('Alt');
  });

  it('shows no error before the first attempt, then reports an invalid name accessibly', () => {
    const view = render();
    expect(view.error()).toBeNull();
    view.enter();
    expect(view.error()).toBe('Ungültiger Name.');
    expect(view.input.getAttribute('aria-invalid')).toBe('true');
    const describedBy = view.input.getAttribute('aria-describedby');
    expect(describedBy).not.toBeNull();
    expect(view.fixture.nativeElement.querySelector(`#${describedBy}`)).not.toBeNull();
    expect(submitted).toEqual([]);
  });

  it('emits the trimmed name on Enter', () => {
    const view = render();
    view.type('  Stronghold ');
    view.enter();
    expect(submitted).toEqual(['Stronghold']);
    expect(view.error()).toBeNull();
  });

  it('shows a server field error and clears it when the user edits', () => {
    const view = render();
    view.type('Stronghold');
    expect(view.fixture.componentInstance.applyServerError(apiError(409, 'tag_name_taken'))).toBe(
      true,
    );
    view.fixture.detectChanges();
    expect(view.error()).toBe('Name schon vergeben.');
    view.type('Stronghold2');
    expect(view.error()).toBeNull();
  });

  it('does not claim a form-wide failure', () => {
    const view = render();
    expect(view.fixture.componentInstance.applyServerError(apiError(500))).toBe(false);
    view.fixture.detectChanges();
    expect(view.error()).toBeNull();
  });

  it('reset empties the field and its errors', () => {
    const view = render();
    view.enter();
    view.fixture.componentInstance.reset();
    view.fixture.detectChanges();
    expect(view.input.value).toBe('');
    expect(view.error()).toBeNull();
  });
});
