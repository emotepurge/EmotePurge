import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, shareReplay } from 'rxjs';

import { ChannelUsageSeries, EmoteUsageSeries, EmoteUsageTotalDto } from './usage-stat.model';

/**
 * Adds `emoteSetId` to a params object only when it is a concrete id — `null` means "no explicit
 * set chosen, let the endpoint fall back to `Channel.ActiveEmoteSetId`" (spec #200, 6.5), and the
 * validation filter on the other end rejects an empty string outright, so there is no "send it
 * anyway, blank" option to fall back to instead.
 */
function withEmoteSetId(
  params: Record<string, string>,
  emoteSetId: string | null,
): Record<string, string> {
  return emoteSetId ? { ...params, emoteSetId } : params;
}

@Injectable({ providedIn: 'root' })
export class UsageStatService {
  private readonly http = inject(HttpClient);

  /**
   * Series cache per (channel, set, emote, range): /daily shares the InteractiveRead budget with the
   * totals reload, and a drilldown opened twice is the same request. Entries live until
   * clearSeriesCache() — the pages call it when the channel, date range or selected set changes.
   * The set is part of the key (spec #200, 7.3, AK 64): two sets over the same range are two
   * different answers, not the same series read twice.
   */
  private readonly seriesCache = new Map<string, Observable<EmoteUsageSeries>>();

  /** The same, one level up: per (channel, set, range), because /series answers for the whole set. */
  private readonly channelSeriesCache = new Map<string, Observable<ChannelUsageSeries>>();

  /**
   * `emoteSetId: null` omits the query parameter entirely rather than sending an empty one — the
   * endpoint then falls back to `Channel.ActiveEmoteSetId` itself (spec 6.5), which is also what
   * "no set resolved yet" (a channel with no active set at all) has to mean here: there is nothing
   * more specific to ask for.
   */
  getTotals(
    channelName: string,
    from: string,
    to: string,
    emoteSetId: string | null,
  ): Observable<EmoteUsageTotalDto[]> {
    return this.http.get<EmoteUsageTotalDto[]>(`/api/channels/${channelName}/usage-stats/totals`, {
      params: withEmoteSetId({ from, to }, emoteSetId),
    });
  }

  getDailySeries(
    channelName: string,
    emoteId: string,
    from: string,
    to: string,
    /** `null` omits the parameter (the channel's active set) — see {@link getTotals}. The drilldown
     *  passes the set frozen into its dialog data, never a live read (spec #200, 7.2, F4). */
    emoteSetId: string | null,
  ): Observable<EmoteUsageSeries> {
    const key = `${channelName}|${emoteSetId ?? ''}|${emoteId}|${from}|${to}`;
    let series$ = this.seriesCache.get(key);
    if (!series$) {
      series$ = this.http
        .get<EmoteUsageSeries>(`/api/channels/${channelName}/usage-stats/daily`, {
          params: withEmoteSetId({ emoteId, from, to }, emoteSetId),
        })
        // refCount:false keeps the replayed value alive with no subscriber — that is the cache.
        .pipe(shareReplay({ bufferSize: 1, refCount: false }));
      this.seriesCache.set(key, series$);
      // A failed request must not stick as a cached error — the next open should retry.
      series$.subscribe({ error: () => this.seriesCache.delete(key) });
    }
    return series$;
  }

  /**
   * Every emote's days for one channel, set and range in a single request.
   *
   * This is what keeps the atlas's hover readout off the wire: asking {@link getDailySeries} for
   * whichever emote the pointer is over would turn mouse movement into requests, and each one costs
   * a permit from the same 40/min bucket plus two uncached 7TV lookups in the endpoint's access
   * filter. Cached like the per-emote series, so re-inspecting is free and the range menu — or the
   * set dropdown — is the only thing that ever triggers a new one.
   */
  getChannelSeries(
    channelName: string,
    from: string,
    to: string,
    emoteSetId: string | null,
  ): Observable<ChannelUsageSeries> {
    const key = `${channelName}|${emoteSetId ?? ''}|${from}|${to}`;
    let series$ = this.channelSeriesCache.get(key);
    if (!series$) {
      series$ = this.http
        .get<ChannelUsageSeries>(`/api/channels/${channelName}/usage-stats/series`, {
          params: withEmoteSetId({ from, to }, emoteSetId),
        })
        .pipe(shareReplay({ bufferSize: 1, refCount: false }));
      this.channelSeriesCache.set(key, series$);
      series$.subscribe({ error: () => this.channelSeriesCache.delete(key) });
    }
    return series$;
  }

  clearSeriesCache(): void {
    this.seriesCache.clear();
    this.channelSeriesCache.clear();
  }
}
