import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable, shareReplay } from 'rxjs';

import { ChannelUsageSeries, EmoteUsageSeries, EmoteUsageTotalDto } from './usage-stat.model';

@Injectable({ providedIn: 'root' })
export class UsageStatService {
  private readonly http = inject(HttpClient);

  /**
   * Series cache per (channel, emote, range): /daily shares the InteractiveRead budget with the
   * totals reload, and a drilldown opened twice is the same request. Entries live until
   * clearSeriesCache() — the pages call it when the channel or date range changes.
   */
  private readonly seriesCache = new Map<string, Observable<EmoteUsageSeries>>();

  /** The same, one level up: per (channel, range), because /series answers for the whole set. */
  private readonly channelSeriesCache = new Map<string, Observable<ChannelUsageSeries>>();

  getTotals(channelName: string, from: string, to: string): Observable<EmoteUsageTotalDto[]> {
    return this.http.get<EmoteUsageTotalDto[]>(`/api/channels/${channelName}/usage-stats/totals`, {
      params: { from, to },
    });
  }

  getDailySeries(
    channelName: string,
    emoteId: string,
    from: string,
    to: string,
  ): Observable<EmoteUsageSeries> {
    const key = `${channelName}|${emoteId}|${from}|${to}`;
    let series$ = this.seriesCache.get(key);
    if (!series$) {
      series$ = this.http
        .get<EmoteUsageSeries>(`/api/channels/${channelName}/usage-stats/daily`, {
          params: { emoteId, from, to },
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
   * Every emote's days for one channel and range in a single request.
   *
   * This is what keeps the atlas's hover readout off the wire: asking {@link getDailySeries} for
   * whichever emote the pointer is over would turn mouse movement into requests, and each one costs
   * a permit from the same 40/min bucket plus two uncached 7TV lookups in the endpoint's access
   * filter. Cached like the per-emote series, so re-inspecting is free and the range menu is the
   * only thing that ever triggers a new one.
   */
  getChannelSeries(channelName: string, from: string, to: string): Observable<ChannelUsageSeries> {
    const key = `${channelName}|${from}|${to}`;
    let series$ = this.channelSeriesCache.get(key);
    if (!series$) {
      series$ = this.http
        .get<ChannelUsageSeries>(`/api/channels/${channelName}/usage-stats/series`, {
          params: { from, to },
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
