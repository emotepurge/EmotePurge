import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { TranslocoTestingModule } from '@jsverse/transloco';
import { Subject } from 'rxjs';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import de from '../../../../public/i18n/de.json';
import { SevenTvEmoteLookup } from '../../core/seven-tv/seven-tv-emote-lookup';
import { WishEmotePreviewService } from '../../core/wishes/wish-emote-preview.service';
import type { Wish, WishOverviewItem } from '../../core/wishes/wish.model';
import { WishService } from '../../core/wishes/wish.service';
import { MyWishesPage } from './my-wishes-page';

/** jsdom has no layout: rows report "in view" only when a test says so. */
class FakeIntersectionObserver {
  static instances: FakeIntersectionObserver[] = [];

  readonly targets = new Set<Element>();

  constructor(
    private readonly callback: (hits: { target: Element; isIntersecting: boolean }[]) => void,
  ) {
    FakeIntersectionObserver.instances.push(this);
  }

  observe(target: Element): void {
    this.targets.add(target);
  }

  disconnect(): void {
    this.targets.clear();
  }

  show(target: Element, isIntersecting: boolean): void {
    this.callback([{ target, isIntersecting }]);
  }
}

/** An open wish captured from a 7TV link: the emote is known, name and stored picture are not. */
function linked(id: number, emote: string, overrides: Partial<Wish> = {}): WishOverviewItem {
  return {
    channelName: 'chan',
    wish: {
      id,
      sevenTvEmoteId: emote,
      emoteName: '',
      alias: '',
      source: 'irc',
      status: 'pending',
      statusReason: null,
      requesterLogin: 'viewer_one',
      durationDays: 7,
      createdAtUtc: '2026-11-20T10:00:00Z',
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
      requestText: `https://7tv.app/emotes/${emote}`,
      requestedName: null,
      matchKind: 'link',
      ...overrides,
    },
  };
}

describe('MyWishesPage: picture and name of an open wish (plan 2.10, T19)', () => {
  let lists: Subject<WishOverviewItem[]>[];
  let asked: string[];
  let released: unknown[];
  let stillIds: Set<string>;
  let names: Map<string, string>;
  const fixtures: ComponentFixture<MyWishesPage>[] = [];

  beforeEach(() => {
    lists = [];
    asked = [];
    released = [];
    stillIds = new Set();
    names = new Map();
    FakeIntersectionObserver.instances = [];
    vi.stubGlobal('IntersectionObserver', FakeIntersectionObserver);

    TestBed.configureTestingModule({
      imports: [
        MyWishesPage,
        TranslocoTestingModule.forRoot({
          langs: { de },
          translocoConfig: { availableLangs: ['de'], defaultLang: 'de' },
        }),
      ],
      providers: [
        provideRouter([]),
        {
          provide: WishService,
          useValue: {
            listMine: () => {
              const answer = new Subject<WishOverviewItem[]>();
              lists.push(answer);
              return answer;
            },
          },
        },
        {
          provide: WishEmotePreviewService,
          useValue: {
            request: (id: string) => asked.push(id),
            release: (consumer: unknown) => released.push(consumer),
            nameOf: (id: string) => names.get(id) ?? null,
          },
        },
        {
          provide: SevenTvEmoteLookup,
          useValue: { isKnownStill: (id: string) => stillIds.has(id) },
        },
      ],
    });
  });

  afterEach(() => {
    fixtures.splice(0).forEach((fixture) => fixture.destroy());
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    vi.unstubAllGlobals();
  });

  function create(items: WishOverviewItem[]): ComponentFixture<MyWishesPage> {
    const fixture = TestBed.createComponent(MyWishesPage);
    fixtures.push(fixture);
    fixture.detectChanges();
    answer(fixture, items);
    return fixture;
  }

  function answer(fixture: ComponentFixture<MyWishesPage>, items: WishOverviewItem[]): void {
    lists[lists.length - 1].next(items);
    fixture.detectChanges();
  }

  function rowOf(fixture: ComponentFixture<MyWishesPage>, id: number): Element {
    return fixture.nativeElement.querySelector(`[data-wish-key="chan#${id}"]`) as Element;
  }

  /** The row reports whether it is in view, as the page's IntersectionObserver would. */
  function showRow(fixture: ComponentFixture<MyWishesPage>, id: number, visible: boolean): void {
    const row = rowOf(fixture, id);
    FakeIntersectionObserver.instances
      .filter((observer) => observer.targets.has(row))
      .forEach((observer) => observer.show(row, visible));
    fixture.detectChanges();
  }

  it('reads the emote of an open link wish only once its row is in view', () => {
    const fixture = create([linked(1, 'e1'), linked(2, 'e2')]);
    expect(asked).toEqual([]);

    showRow(fixture, 2, true);
    expect(asked).toEqual(['e2']);

    showRow(fixture, 2, false);
    expect(released).toEqual(['my-wishes-row:chan#2']);
  });

  it('releases what a row in view asked for once a reload no longer lists it', () => {
    const fixture = create([linked(1, 'e1'), linked(2, 'e2')]);
    showRow(fixture, 1, true);
    showRow(fixture, 2, true);

    fixture.componentInstance['reload']();
    answer(fixture, [linked(1, 'e1')]);

    expect(released).toEqual(['my-wishes-row:chan#2']);
  });

  it('releases what its rows in view asked for when the page goes away', () => {
    const fixture = create([linked(1, 'e1'), linked(2, 'e2')]);
    showRow(fixture, 1, true);
    showRow(fixture, 2, true);

    fixture.destroy();

    expect(released).toEqual(['my-wishes-row:chan#1', 'my-wishes-row:chan#2']);
  });

  it('reads nothing for a wish that has its picture and name, or no emote', () => {
    const fixture = create([
      linked(1, 'e1', { imageUrl: 'https://cdn.example/e1.webp', alias: 'dino' }),
      linked(2, '', { requestText: 'catJAM pls', requestedName: 'catJAM', matchKind: 'name' }),
    ]);
    showRow(fixture, 1, true);
    showRow(fixture, 2, true);

    expect(asked).toEqual([]);
  });

  it('asks again for the rows in view after a reload and when the window comes back', () => {
    const fixture = create([linked(1, 'e1'), linked(2, 'e2')]);
    showRow(fixture, 1, true);
    asked = [];

    fixture.componentInstance['reload']();
    answer(fixture, [linked(1, 'e1'), linked(2, 'e2')]);
    expect(asked).toEqual(['e1']);

    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    fixture.detectChanges();
    asked = [];
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    fixture.detectChanges();
    expect(asked).toEqual(['e1']);
  });

  it('shows the plain picture once the emote is proven still, and the default name as title', () => {
    stillIds.add('e1');
    names.set('e1', 'dinoCry');
    const fixture = create([linked(1, 'e1'), linked(2, 'e2')]);
    const picture = (id: number) => rowOf(fixture, id).querySelector('img')?.getAttribute('src');

    expect(picture(1)).not.toContain('_static');
    expect(picture(2)).toContain('_static');
    expect(rowOf(fixture, 1).querySelector('a')?.textContent?.trim()).toBe('dinoCry');
  });
});
