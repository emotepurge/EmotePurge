import { Service, inject, signal } from '@angular/core';

import {
  canonicalSevenTvEmoteId,
  SevenTvEmoteLookup,
  SevenTvEmoteLookupResult,
} from '../seven-tv/seven-tv-emote-lookup';

/**
 * What the read of one emote found: its default name (`''` when 7TV sent none), or that 7TV does not
 * know it, or that the read failed. A failed read is tried once more when the emote is asked for again;
 * a second failure stays for the session.
 */
export type WishEmotePreview =
  { kind: 'found'; name: string } | { kind: 'unknown' } | { kind: 'unavailable' };

/**
 * Picture and default name of the emote an open wish names (#486, plan 2.10). A wish captured from a
 * 7TV link knows the emote id but neither alias nor name, and the channel may never have had the emote
 * (no stored picture). This service reads each such emote once per session through the existing
 * `SevenTvEmoteLookup`, which also remembers "not animated" for the picture rule of K3; it never asks
 * for anything else.
 *
 * - **One read at a time**, in the order the rows asked, each id at most once per session (canonical
 *   form, so both id forms share the entry). The one exception is a failed read (network, deadline, an
 *   answer without data): the next `request` for that id reads it once more (`MAX_READ_FAILURES`), so
 *   a single short failure does not leave a still emote without its picture for the whole session. An
 *   unknown emote is an answer and is not read again.
 * - **Only rows in view ask** (`request`), and a row that leaves the view before its turn takes its
 *   request back (`withdraw`); a read already on the way finishes. Interest is kept **per consumer**
 *   (a row of the work list, a row of `my-wishes`, an open approve dialog): several of them may wait
 *   for the same emote, and a waiting id leaves the queue only once none of them still needs it.
 * - **After a throttle** (a real 429 or 7TV's "429 as HTTP 200") it stops for the session: the queue is
 *   dropped and nothing more is read, so the rows stay without a name and with K3's plain picture rule.
 */
/** Failed reads of one emote after which it is not read again in this session (plan 2.10, T19). */
export const MAX_READ_FAILURES = 2;

/**
 * Who asks for a preview: any value unique to one row or dialog (a key string, or the component itself).
 * Two consumers with the same key count as one.
 */
export type WishEmotePreviewConsumer = string | object;

@Service()
export class WishEmotePreviewService {
  private readonly lookup = inject(SevenTvEmoteLookup);

  private readonly results = signal<ReadonlyMap<string, WishEmotePreview>>(new Map());
  private readonly stoppedState = signal(false);

  /** Asked for and not yet read, oldest first. */
  private readonly queue: string[] = [];
  /** Who still waits for each queued id; an id without anyone leaves the queue. */
  private readonly interest = new Map<string, Set<WishEmotePreviewConsumer>>();
  /** The id whose read is on the way. */
  private current: string | null = null;
  /** Failed reads per canonical id; at `MAX_READ_FAILURES` the failure stays. */
  private readonly failures = new Map<string, number>();

  /** A throttle stopped the reads for this session. */
  readonly stopped = this.stoppedState.asReadonly();

  /** The result for the emote, `null` while it was not (yet) read. Reactive. */
  previewOf(sevenTvEmoteId: string): WishEmotePreview | null {
    if (sevenTvEmoteId === '') {
      return null;
    }
    return this.results().get(canonicalSevenTvEmoteId(sevenTvEmoteId)) ?? null;
  }

  /** The default name the read found; `null` without one. Reactive. */
  nameOf(sevenTvEmoteId: string): string | null {
    const preview = this.previewOf(sevenTvEmoteId);
    return preview?.kind === 'found' && preview.name !== '' ? preview.name : null;
  }

  /**
   * A row or dialog with this emote is in view: read it, unless it was read (a failed read only once
   * more), is waiting, or reads stopped. Asking again for a known or waiting id costs nothing; for a
   * waiting one it records `consumer` among those who wait for it.
   */
  request(sevenTvEmoteId: string, consumer: WishEmotePreviewConsumer): void {
    if (sevenTvEmoteId === '' || this.stoppedState()) {
      return;
    }
    const id = canonicalSevenTvEmoteId(sevenTvEmoteId);
    if (this.current === id) {
      return;
    }
    if (this.queue.includes(id)) {
      this.interest.get(id)?.add(consumer);
      return;
    }
    const known = this.results().get(id);
    if (known !== undefined) {
      if (known.kind !== 'unavailable' || (this.failures.get(id) ?? 0) >= MAX_READ_FAILURES) {
        return;
      }
      // The retry is under way again: the entry reads "not yet read" until it answers.
      this.results.update((results) => {
        const next = new Map(results);
        next.delete(id);
        return next;
      });
    }
    this.queue.push(id);
    this.interest.set(id, new Set([consumer]));
    this.next();
  }

  /**
   * `consumer` no longer needs this emote (its row left the view, its dialog closed). A waiting id
   * leaves the queue once no consumer waits for it any more; a read already on the way finishes.
   */
  withdraw(sevenTvEmoteId: string, consumer: WishEmotePreviewConsumer): void {
    if (sevenTvEmoteId === '') {
      return;
    }
    const id = canonicalSevenTvEmoteId(sevenTvEmoteId);
    const waiting = this.interest.get(id);
    if (waiting === undefined) {
      return;
    }
    waiting.delete(consumer);
    if (waiting.size > 0) {
      return;
    }
    this.interest.delete(id);
    const index = this.queue.indexOf(id);
    if (index >= 0) {
      this.queue.splice(index, 1);
    }
  }

  private next(): void {
    if (this.current !== null || this.stoppedState()) {
      return;
    }
    const id = this.queue.shift();
    if (id === undefined) {
      return;
    }
    this.interest.delete(id);
    this.current = id;
    this.lookup.lookup(id).subscribe((result) => this.onResult(id, result));
  }

  private onResult(id: string, result: SevenTvEmoteLookupResult): void {
    this.current = null;
    if (result.kind === 'throttled') {
      this.queue.length = 0;
      this.interest.clear();
      this.stoppedState.set(true);
      return;
    }
    const preview: WishEmotePreview =
      result.kind === 'found'
        ? { kind: 'found', name: result.name }
        : result.kind === 'unknown'
          ? { kind: 'unknown' }
          : { kind: 'unavailable' };
    if (preview.kind === 'unavailable') {
      this.failures.set(id, (this.failures.get(id) ?? 0) + 1);
    }
    this.results.update((results) => new Map(results).set(id, preview));
    this.next();
  }
}
