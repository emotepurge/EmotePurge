import { ComponentFixture, TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';

import { WishImage, wishImageUrl } from './wish-image';

const STORED_STILL = 'https://cdn.7tv.app/emote/stored/4x_static.webp';

describe('wishImageUrl', () => {
  it('takes the stored image when the wish carries one', () => {
    expect(wishImageUrl(STORED_STILL, 'abc', false)).toBe(STORED_STILL);
    expect(wishImageUrl(STORED_STILL, 'abc', true)).toBe(STORED_STILL);
  });

  it('takes the plain rendition only when the emote is known not to be animated', () => {
    expect(wishImageUrl(null, 'abc', true)).toBe('https://cdn.7tv.app/emote/abc/4x.webp');
  });

  it('asks only for the still rendition while nothing is known', () => {
    expect(wishImageUrl(null, 'abc', false)).toBe('https://cdn.7tv.app/emote/abc/4x_static.webp');
  });
});

describe('WishImage', () => {
  function create(imageUrl: string | null, knownStill = false): ComponentFixture<WishImage> {
    const fixture = TestBed.createComponent(WishImage);
    fixture.componentRef.setInput('imageUrl', imageUrl);
    fixture.componentRef.setInput('sevenTvEmoteId', 'abc');
    fixture.componentRef.setInput('knownStill', knownStill);
    fixture.detectChanges();
    return fixture;
  }

  /** Every url the image element may fetch: its `src` and each `srcset` candidate. */
  function requestedUrls(fixture: ComponentFixture<WishImage>): string[] {
    const images: HTMLImageElement[] = Array.from(fixture.nativeElement.querySelectorAll('img'));
    return images.flatMap((image) => [
      image.getAttribute('src') ?? '',
      ...(image.getAttribute('srcset') ?? '')
        .split(',')
        .map((candidate) => candidate.trim().split(' ')[0])
        .filter((candidate) => candidate !== ''),
    ]);
  }

  it('requests nothing but still renditions while the emote is unknown', () => {
    const fixture = create(null);

    const urls = requestedUrls(fixture);
    expect(urls.length).toBeGreaterThan(0);
    expect(urls.every((url) => url.endsWith('_static.webp'))).toBe(true);
  });

  it('leaves the surface empty and asks for no other rendition when the still fails, once or for good', () => {
    const fixture = create(null);
    const image: HTMLImageElement = fixture.nativeElement.querySelector('img');

    image.dispatchEvent(new Event('error'));
    fixture.detectChanges();
    image.dispatchEvent(new Event('error'));
    fixture.detectChanges();

    expect(image.style.visibility).toBe('hidden');
    expect(fixture.nativeElement.querySelectorAll('img')).toHaveLength(1);
    expect(requestedUrls(fixture).every((url) => url.endsWith('_static.webp'))).toBe(true);
  });

  it('uses the stored image as given', () => {
    const fixture = create('https://cdn.7tv.app/emote/stored/4x.webp');

    expect(requestedUrls(fixture).every((url) => url.includes('/emote/stored/'))).toBe(true);
  });

  it('switches to the plain rendition once the emote is known not to be animated', () => {
    const fixture = create(null, true);

    const urls = requestedUrls(fixture);
    expect(urls.some((url) => url.endsWith('x.webp') && !url.endsWith('_static.webp'))).toBe(true);
  });
});
