import { computed, signal } from '@angular/core';

import { isUnderObservation } from './emote-context';

interface FilterableEmote {
  emoteName: string;
  // The key tags are stored by (rule 8: not the internal Emote.Id).
  sevenTvEmoteId: string;
  // null = usage withheld/unknown (non-manager voting view). Usage bounds never match null —
  // "unused" means a confirmed 0, not "no data".
  totalUseCount: number | null;
  // Optional because the voting grid's rows do not carry it. Missing reads as "not under
  // observation": a filter must never hide a row because data about it is absent.
  firstSeenAt?: string | null;
}

function globToRegExp(pattern: string): RegExp {
  const trimmed = pattern.trim();
  const escaped = trimmed
    .replace(/[.+^${}()|[\]\\]/g, String.raw`\$&`)
    .replaceAll('*', '.*')
    .replaceAll('?', '.');

  // With ~1,000 emotes per channel, the expected default is a plain substring search ("peepo"
  // should match "peepoHappy", "peepoSad", ...) — fully anchoring every query made that
  // impossible unless the user also typed wildcards. Only anchor (classic glob semantics) once
  // the user opts in by actually typing `*`/`?`; a bare query stays an unanchored substring match.
  const hasWildcard = trimmed.includes('*') || trimmed.includes('?');
  return hasWildcard ? new RegExp(`^${escaped}$`, 'i') : new RegExp(escaped, 'i');
}

/**
 * Min/Max-usage + name-wildcard filtering, used identically on the Usage-Stats and
 * Voting-Detail emote grids. Not a service — page-local UI state constructed per host
 * component, like ListSelection.
 */
export class EmoteUsageFilter<T extends FilterableEmote> {
  private readonly minCount = signal<number | null>(null);
  private readonly maxCount = signal<number | null>(null);
  private readonly nameFilter = signal('');
  private readonly hideObserved = signal(false);
  private readonly selectedTagId = signal<number | null>(null);
  private readonly selectedTagKeys = signal<ReadonlySet<string> | null>(null);

  readonly min = this.minCount.asReadonly();
  readonly max = this.maxCount.asReadonly();
  readonly nameQuery = this.nameFilter.asReadonly();
  readonly isUnusedActive = computed(() => this.minCount() === 0 && this.maxCount() === 0);
  readonly isHideObservedActive = this.hideObserved.asReadonly();
  readonly tagId = this.selectedTagId.asReadonly();
  /** The chosen tag's 7TV emote ids; `null` until the page has loaded them (see {@link apply}). */
  readonly tagKeys = this.selectedTagKeys.asReadonly();

  private readonly nameFilterRegex = computed(() => {
    const query = this.nameFilter();
    return query.trim() === '' ? null : globToRegExp(query);
  });

  apply(items: readonly T[], now: Date = new Date()): T[] {
    const min = this.minCount();
    const max = this.maxCount();
    const nameRegex = this.nameFilterRegex();
    const hideObserved = this.hideObserved();
    // A tag whose keys are not loaded yet lets everything through: filtering to empty meanwhile
    // would flash the empty state on every tag switch. The usage page does not show that pass-through
    // while the keys are merely on their way — it holds its view empty under a skeleton
    // (`tagFilterPending`) — only once their load failed, under its error banner.
    const tagKeys = this.selectedTagId() === null ? null : this.selectedTagKeys();
    return items.filter((item) => {
      if (min !== null && (item.totalUseCount === null || item.totalUseCount < min)) return false;
      if (max !== null && (item.totalUseCount === null || item.totalUseCount > max)) return false;
      if (nameRegex && !nameRegex.test(item.emoteName)) return false;
      if (tagKeys && !tagKeys.has(item.sevenTvEmoteId)) return false;
      if (hideObserved && isUnderObservation(item.firstSeenAt ?? null, now)) return false;
      return true;
    });
  }

  setMinCount(value: string): void {
    const parsed = value.trim() === '' ? null : Number(value);
    this.minCount.set(parsed === null || Number.isNaN(parsed) ? null : parsed);
  }

  setMaxCount(value: string): void {
    const parsed = value.trim() === '' ? null : Number(value);
    this.maxCount.set(parsed === null || Number.isNaN(parsed) ? null : parsed);
  }

  setNameFilter(value: string): void {
    this.nameFilter.set(value);
  }

  /** Choosing (or clearing) a tag drops the old keys; the page loads the new ones and calls setTagKeys. */
  setTag(tagId: number | null): void {
    this.selectedTagId.set(tagId);
    this.selectedTagKeys.set(null);
  }

  setTagKeys(keys: ReadonlySet<string>): void {
    this.selectedTagKeys.set(keys);
  }

  /**
   * Both bounds at once, for a control that offers named ranges rather than two free fields.
   *
   * Replaces `toggleUnused()` (2026-08-06), which was a toggle for a state that is really one of
   * several: "never used" is just the 0/0 range, and it lived as a button beside the very two inputs
   * it silently overwrote. Toggling is also the wrong verb once the ranges are presented as a
   * choice — re-picking the selected option in a radio group must not clear it.
   */
  setRange(min: number | null, max: number | null): void {
    this.minCount.set(min);
    this.maxCount.set(max);
  }

  /**
   * Hides emotes still within their observation period. Deliberately its own toggle rather than a
   * rule baked into the unused filter: silently dropping fresh emotes from the "0x" list would make
   * the "x of y" count stop adding up and leave a moderator unable to find the emote they just
   * added. Opting in is a different act from asking for unused emotes.
   */
  toggleHideObserved(): void {
    this.hideObserved.update((active) => !active);
  }

  /** True whenever any filter would exclude items — the empty state uses this to offer a reset. */
  isAnyActive(): boolean {
    return (
      this.minCount() !== null ||
      this.maxCount() !== null ||
      this.nameFilter().trim() !== '' ||
      this.hideObserved() ||
      this.selectedTagId() !== null
    );
  }

  reset(): void {
    this.minCount.set(null);
    this.maxCount.set(null);
    this.nameFilter.set('');
    this.hideObserved.set(false);
    this.selectedTagId.set(null);
    this.selectedTagKeys.set(null);
  }
}
