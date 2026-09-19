import { TestBed } from '@angular/core/testing';
import { TranslocoService, TranslocoTestingModule } from '@jsverse/transloco';
import { firstValueFrom } from 'rxjs';
import { beforeEach, describe, expect, it } from 'vitest';

import { NamePreviewList } from './name-preview-list';

// Only the keys this primitive translates — not the full app translation file.
const DE_TRANSLATIONS = {
  common: {
    andMore: {
      one: '… und 1 weiteres',
      other: '… und {{count}} weitere',
    },
  },
};

function names(count: number): string[] {
  return Array.from({ length: count }, (_, index) => `Emote${index + 1}`);
}

describe('NamePreviewList', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [
        NamePreviewList,
        TranslocoTestingModule.forRoot({
          langs: { de: DE_TRANSLATIONS },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
    }).compileComponents();

    await firstValueFrom(TestBed.inject(TranslocoService).load('de'));
  });

  function render(list: string[], cap?: number | null): HTMLElement {
    const fixture = TestBed.createComponent(NamePreviewList);
    fixture.componentRef.setInput('names', list);
    if (cap !== undefined) {
      fixture.componentRef.setInput('cap', cap);
    }
    fixture.detectChanges();
    return fixture.nativeElement;
  }

  it('caps at 50 by default and names the rest', () => {
    const host = render(names(60));

    expect(host.querySelectorAll('li').length).toBe(51); // 50 names + the "and more" row
    expect(host.textContent).toContain('… und 10 weitere');
  });

  it('shows every name under the default cap without an overflow row', () => {
    const host = render(names(3));

    expect(host.querySelectorAll('li').length).toBe(3);
    expect(host.textContent).not.toContain('weitere');
  });

  it('shows all names when cap is null, however many there are', () => {
    const host = render(names(60), null);

    expect(host.querySelectorAll('li').length).toBe(60);
    expect(host.textContent).not.toContain('weitere');
  });
});
