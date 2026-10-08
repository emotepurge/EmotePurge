import { HttpErrorResponse } from '@angular/common/http';
import { Component, computed, effect, input, output, signal } from '@angular/core';
import { TranslocoPipe } from '@jsverse/transloco';

import { apiErrorTranslationKey } from '../../core/i18n/api-error';

/** Mirrors the backend's `EmoteTagName.MaxLength`; the server stays the authority. */
export const TAG_NAME_MAX_LENGTH = 40;

/** Same translation key whether the client or the server called the name invalid: one sentence. */
export const TAG_NAME_INVALID_KEY = 'errors.api.tag_name_invalid';

// `char.IsControl` on the server: Unicode category Cc.
const CONTROL_CHARS = /[\u0000-\u001f\u007f-\u009f]/;

/** The trimmed name the server will store, or `null` when it would reject it with `tag_name_invalid`. */
export function validTagName(raw: string): string | null {
  const trimmed = raw.trim();
  if (trimmed.length === 0 || trimmed.length > TAG_NAME_MAX_LENGTH) {
    return null;
  }
  return CONTROL_CHARS.test(trimmed) ? null : trimmed;
}

const FIELD_ERROR_CODES: ReadonlySet<string> = new Set([
  'tag_name_invalid',
  'tag_name_taken',
  'tag_limit_reached',
]);

/**
 * The translation key for a server rejection that belongs at the field (the name is the problem, or
 * the channel is full), or `null` for everything else — those are form-wide and go in a banner.
 */
export function tagNameFieldErrorKey(error: HttpErrorResponse): string | null {
  const key = apiErrorTranslationKey(error);
  const code = key.startsWith('errors.api.') ? key.slice('errors.api.'.length) : null;
  return code !== null && FIELD_ERROR_CODES.has(code) ? key : null;
}

/**
 * The one name input for creating and renaming a tag, with its field error (§5.3): shared by the
 * assign dialog and the tags page so the validation and the server-error mapping exist once.
 *
 * Owns the text, not the button: the host places its own action (an inline button via the
 * `[field-action]` slot, or the dialog's action row) and calls `submit()`; Enter calls it too.
 * `submitted` emits only a name that passed the client check, already trimmed.
 */
@Component({
  selector: 'app-tag-name-field',
  imports: [TranslocoPipe],
  host: { class: 'flex flex-col gap-1' },
  template: `
    <label [for]="inputId()" class="text-sm text-fg-secondary">{{ label() }}</label>
    <div class="flex items-center gap-2">
      <input
        [id]="inputId()"
        type="text"
        autocomplete="off"
        class="app-input min-w-0 flex-1"
        [value]="text()"
        (input)="onInput($event)"
        (keydown.enter)="onEnter($event)"
        [attr.aria-invalid]="errorKey() !== null ? 'true' : null"
        [attr.aria-describedby]="errorKey() !== null ? inputId() + '-error' : null"
      />
      <ng-content select="[field-action]" />
    </div>
    @if (errorKey(); as key) {
      <p [id]="inputId() + '-error'" class="text-sm text-danger-fg">{{ key | transloco }}</p>
    }
  `,
})
export class TagNameField {
  /** Already translated. */
  readonly label = input.required<string>();
  readonly inputId = input('tag-name-input');
  /** Prefill for a rename; re-applied whenever it changes. */
  readonly initialName = input('');
  /** Emits the trimmed, client-valid name. */
  readonly submitted = output<string>();

  protected readonly text = signal('');
  private readonly attempted = signal(false);
  private readonly serverErrorKey = signal<string | null>(null);

  /** The field error to show, translation key or `null`: the server's verdict wins over ours. */
  readonly errorKey = computed(() => {
    const server = this.serverErrorKey();
    if (server !== null) {
      return server;
    }
    return this.attempted() && validTagName(this.text()) === null ? TAG_NAME_INVALID_KEY : null;
  });

  constructor() {
    effect(() => this.text.set(this.initialName()));
  }

  /** Validates and emits; shows the field error instead when the name is not acceptable. */
  submit(): void {
    this.serverErrorKey.set(null);
    this.attempted.set(true);
    const name = validTagName(this.text());
    if (name !== null) {
      this.submitted.emit(name);
    }
  }

  /** Routes a failed request to the field when it is the name's problem; `false` = host's job. */
  applyServerError(error: HttpErrorResponse): boolean {
    const key = tagNameFieldErrorKey(error);
    this.serverErrorKey.set(key);
    return key !== null;
  }

  /** Empties the field after a successful create. */
  reset(): void {
    this.text.set('');
    this.attempted.set(false);
    this.serverErrorKey.set(null);
  }

  protected onInput(event: Event): void {
    this.text.set((event.target as HTMLInputElement).value);
    this.serverErrorKey.set(null);
  }

  protected onEnter(event: Event): void {
    event.preventDefault();
    this.submit();
  }
}
