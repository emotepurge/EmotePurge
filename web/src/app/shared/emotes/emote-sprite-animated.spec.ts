import { Component, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ReducedMotionService } from '../../core/motion/reduced-motion.service';
import { EmoteSpriteAnimated } from './emote-sprite-animated';

const ANIMATED_A = 'https://cdn.7tv.app/emote/aaa/4x_static.webp';
const ANIMATED_B = 'https://cdn.7tv.app/emote/bbb/4x_static.webp';
const STILL_ONLY = 'https://cdn.7tv.app/emote/ccc/4x.webp';

@Component({
  imports: [EmoteSpriteAnimated],
  template: `<app-emote-sprite-animated [url]="url()" [size]="56" />`,
})
class Host {
  readonly url = signal(ANIMATED_A);
}

describe('EmoteSpriteAnimated', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;

  function images(): HTMLImageElement[] {
    return [...fixture.nativeElement.querySelectorAll('img')];
  }

  function sources(): string[] {
    return images().map((img) => img.getAttribute('src') ?? '');
  }

  function stillImage(): HTMLImageElement {
    return images()[0];
  }

  function overlayImage(): HTMLImageElement {
    return images()[1];
  }

  beforeEach(async () => {
    vi.useFakeTimers();
    await TestBed.configureTestingModule({ imports: [Host] }).compileComponents();
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  // The still is what the atlas already loaded for this emote, so it paints from cache. Asking for
  // the animation in the same breath would spend up to 1.2 MB on a pointer that is merely passing.
  it('shows only the still before the dwell has passed', () => {
    expect(sources()).toEqual([ANIMATED_A]);
  });

  it('adds the animation once the pointer has rested', () => {
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    expect(sources()).toEqual([ANIMATED_A, 'https://cdn.7tv.app/emote/aaa/2x.webp']);
  });

  // A sweep across the atlas rebinds this per cell. Without the reset, every cell touched on the
  // way would still fire its request a moment later.
  it('never requests an animation the pointer moved off before the dwell', () => {
    host.url.set(ANIMATED_B);
    fixture.detectChanges();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    expect(sources()).not.toContain('https://cdn.7tv.app/emote/aaa/2x.webp');
  });

  it('drops the previous animation the moment the emote changes', () => {
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    host.url.set(ANIMATED_B);
    fixture.detectChanges();

    expect(sources()).toEqual([ANIMATED_B]);
  });

  // A still emote has no animated variant to fetch — animatedEmoteUrl hands back the same url, and
  // a second <img> on it would be a duplicate request for a picture already on screen.
  it('never stacks a second image on a still emote', () => {
    host.url.set(STILL_ONLY);
    fixture.detectChanges();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    expect(sources()).toEqual([STILL_ONLY]);
  });

  // The actual bug: the still painted forever underneath the animation, so wherever the animation's
  // motion left its own bounding box the still showed through, doubled up with it.
  it('keeps the still visible while the animation is still loading', () => {
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    expect(stillImage().className).not.toContain('invisible');
  });

  it('hides the still once the animation has settled', () => {
    vi.advanceTimersByTime(200);
    fixture.detectChanges();

    overlayImage().dispatchEvent(new Event('load'));
    fixture.detectChanges();

    expect(stillImage().className).toContain('invisible');
  });

  // The url change has to win synchronously, with nothing left to wait on — a moment where neither
  // picture is visible would be worse than the flicker this whole fix exists to remove.
  it('shows the still again immediately once the url moves on, with no waiting on an effect', () => {
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    overlayImage().dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(stillImage().className).toContain('invisible');

    host.url.set(ANIMATED_B);
    fixture.detectChanges();

    expect(stillImage().className).not.toContain('invisible');
  });

  // Found by review, not by hand: the reveal marker used to survive the trip away, so coming back
  // hid the still at once — while the overlay was gone and its dwell had started over. A blank
  // cell for the dwell, and a permanent one if the animation then failed to load. Revisiting is
  // the normal case on a dense grid, not an edge case.
  it('does not hide the still when the pointer returns to an emote it already dwelt on', () => {
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    overlayImage().dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(stillImage().className).toContain('invisible');

    host.url.set(ANIMATED_B);
    fixture.detectChanges();
    host.url.set(ANIMATED_A);
    fixture.detectChanges();

    // The overlay remounts at once — `upgradedUrl` still names A, so the dwell is not re-earned —
    // but it is a fresh instance and therefore unloaded and invisible. The still has to carry the
    // box until that instance has actually painted, exactly as on the first visit.
    expect(images()).toHaveLength(2);
    expect(stillImage().className).not.toContain('invisible');

    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    expect(stillImage().className).not.toContain('invisible');

    overlayImage().dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(stillImage().className).toContain('invisible');
  });

  it('reports the animation as shown once it has painted', () => {
    const shown = subscribeShown();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    expect(shown).toEqual([]);

    overlayImage().dispatchEvent(new Event('load'));
    fixture.detectChanges();

    expect(shown).toEqual(['https://cdn.7tv.app/emote/aaa/2x.webp']);
  });

  // A settle report still in flight for an emote the url has moved past.
  it('ignores a settle report for an animation the url has already left', () => {
    const shown = subscribeShown();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    host.url.set(ANIMATED_B);
    fixture.detectChanges();

    sprite()['onOverlaySettled']('https://cdn.7tv.app/emote/aaa/2x.webp');
    fixture.detectChanges();

    expect(shown).toEqual([]);
    expect(sprite()['stillHidden']()).toBe(false);
  });

  function sprite(): EmoteSpriteAnimated {
    return fixture.debugElement.query(By.directive(EmoteSpriteAnimated))
      .componentInstance as EmoteSpriteAnimated;
  }

  function subscribeShown(): string[] {
    const shown: string[] = [];
    sprite().animationShown.subscribe((url) => shown.push(url));
    return shown;
  }
});

describe('EmoteSpriteAnimated under prefers-reduced-motion', () => {
  let fixture: ComponentFixture<Host>;
  let host: Host;
  const reduce = signal(true);

  function sources(): string[] {
    return [...fixture.nativeElement.querySelectorAll('img')].map(
      (img: HTMLImageElement) => img.getAttribute('src') ?? '',
    );
  }

  function overlayImage(): HTMLImageElement {
    return fixture.nativeElement.querySelectorAll('img')[1];
  }

  function stillHidden(): boolean {
    return fixture.debugElement
      .query(By.directive(EmoteSpriteAnimated))
      .componentInstance['stillHidden']();
  }

  beforeEach(async () => {
    vi.useFakeTimers();
    reduce.set(true);
    await TestBed.configureTestingModule({
      imports: [Host],
      providers: [
        {
          provide: ReducedMotionService,
          useValue: { prefersReducedMotion: reduce.asReadonly() },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(Host);
    host = fixture.componentInstance;
    fixture.detectChanges();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  // The whole point of the preference: the pointer resting on an emote is no longer a reason to
  // fetch and play its animation.
  it('never requests the animation, however long the pointer rests', () => {
    vi.advanceTimersByTime(5000);
    fixture.detectChanges();

    expect(sources()).toEqual([ANIMATED_A]);
  });

  it('does not upgrade a later emote either', () => {
    host.url.set(ANIMATED_B);
    fixture.detectChanges();
    vi.advanceTimersByTime(5000);
    fixture.detectChanges();

    expect(sources()).toEqual([ANIMATED_B]);
  });

  // Switched on in the OS while an animation is already playing: it stops at once.
  it('withdraws an animation already showing once the preference is switched on', () => {
    reduce.set(false);
    host.url.set(ANIMATED_B);
    fixture.detectChanges();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    expect(sources()).toEqual([ANIMATED_B, 'https://cdn.7tv.app/emote/bbb/2x.webp']);
    overlayImage().dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(stillHidden()).toBe(true);

    reduce.set(true);
    fixture.detectChanges();

    expect(sources()).toEqual([ANIMATED_B]);
    // The still has to come back with it — a withdrawn overlay over a hidden still is a blank cell.
    expect(stillHidden()).toBe(false);
  });

  // Switched off again with the same emote under the pointer: a remembered upgrade would mount an
  // unloaded overlay at once and hide the still under it, a blank cell until (or unless) it loads.
  it('makes the animation earn its dwell and reveal again once the preference is switched off', () => {
    reduce.set(false);
    host.url.set(ANIMATED_B);
    fixture.detectChanges();
    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    overlayImage().dispatchEvent(new Event('load'));
    fixture.detectChanges();
    reduce.set(true);
    fixture.detectChanges();

    reduce.set(false);
    fixture.detectChanges();
    expect(sources()).toEqual([ANIMATED_B]);
    expect(stillHidden()).toBe(false);

    vi.advanceTimersByTime(200);
    fixture.detectChanges();
    expect(sources()).toEqual([ANIMATED_B, 'https://cdn.7tv.app/emote/bbb/2x.webp']);
    expect(stillHidden()).toBe(false);

    overlayImage().dispatchEvent(new Event('load'));
    fixture.detectChanges();
    expect(stillHidden()).toBe(true);
  });

  it('leaves a still emote a single image', () => {
    host.url.set(STILL_ONLY);
    fixture.detectChanges();
    vi.advanceTimersByTime(5000);
    fixture.detectChanges();

    expect(sources()).toEqual([STILL_ONLY]);
  });
});
