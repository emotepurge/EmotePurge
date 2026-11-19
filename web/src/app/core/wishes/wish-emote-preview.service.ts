import { Service, inject, signal } from '@angular/core';

import {
  canonicalSevenTvEmoteId,
  SevenTvEmoteLookup,
  SevenTvEmoteLookupResult,
} from '../seven-tv/seven-tv-emote-lookup';

/**
 * What the read of one emote found: its default name (`''` when 7TV sent none), or that 7TV does not
 * know it, or that the read failed. A failed read is not retried in this session either.
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
 *   form, so both id forms share the entry).
 * - **Only rows in view ask** (`request`), and a row that leaves the view before its turn takes its
 *   request back (`withdraw`); a read already on the way finishes.
 * - **After a throttle** (a real 429 or 7TV's "429 as HTTP 200") it stops for the session: the queue is
 *   dropped and nothing more is read, so the rows stay without a name and with K3's plain picture rule.
 */
@Service()
export class WishEmotePreviewService {
  private readonly lookup = inject(SevenTvEmoteLookup);

  private readonly results = signal<ReadonlyMap<string, WishEmotePreview>>(new Map());
  private readonly stoppedState = signal(false);

  /** Asked for and not yet read, oldest first. */
  private readonly queue: string[] = [];
  /** The id whose read is on the way. */
  private current: string | null = null;

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

  /** A row with this emote is in view: read it, unless it was read, is waiting, or reads stopped. */
  request(sevenTvEmoteId: string): void {
    if (sevenTvEmoteId === '' || this.stoppedState()) {
      return;
    }
    const id = canonicalSevenTvEmoteId(sevenTvEmoteId);
    if (this.results().has(id) || this.current === id || this.queue.includes(id)) {
      return;
    }
    this.queue.push(id);
    this.next();
  }

  /** The row left the view before its read started: it is not read for it. */
  withdraw(sevenTvEmoteId: string): void {
    const index = this.queue.indexOf(canonicalSevenTvEmoteId(sevenTvEmoteId));
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
    this.current = id;
    this.lookup.lookup(id).subscribe((result) => this.onResult(id, result));
  }

  private onResult(id: string, result: SevenTvEmoteLookupResult): void {
    this.current = null;
    if (result.kind === 'throttled') {
      this.queue.length = 0;
      this.stoppedState.set(true);
      return;
    }
    const preview: WishEmotePreview =
      result.kind === 'found'
        ? { kind: 'found', name: result.name }
        : result.kind === 'unknown'
          ? { kind: 'unknown' }
          : { kind: 'unavailable' };
    this.results.update((results) => new Map(results).set(id, preview));
    this.next();
  }
}
