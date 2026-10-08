import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';

import { PagedResult } from '../models/paged-result.model';
import {
  CastVoteResult,
  CreateVoteSessionRequest,
  MyVoteSession,
  VoteSessionResults,
  VoteSessionSummary,
  VoteType,
} from './vote-session.model';

@Injectable({ providedIn: 'root' })
export class VoteSessionService {
  private readonly http = inject(HttpClient);

  // One-shot guard→page handoff, not a cache — see stashGuardResults/takeGuardResults below.
  private guardResultsStash: {
    channelName: string;
    sessionId: number;
    results: VoteSessionResults;
  } | null = null;

  list(channelName: string, page = 1, pageSize = 20): Observable<PagedResult<VoteSessionSummary>> {
    return this.http.get<PagedResult<VoteSessionSummary>>(
      `/api/channels/${channelName}/vote-sessions`,
      {
        params: { page, pageSize },
      },
    );
  }

  delete(channelName: string, sessionId: number): Observable<void> {
    return this.http.delete<void>(`/api/channels/${channelName}/vote-sessions/${sessionId}`);
  }

  listMine(page = 1, pageSize = 20): Observable<PagedResult<MyVoteSession>> {
    return this.http.get<PagedResult<MyVoteSession>>('/api/vote-sessions/mine', {
      params: { page, pageSize },
    });
  }

  // Optional fields are omitted from the body rather than sent as undefined/false, so the server's
  // own defaults stay the single definition of "not specified" — see CreateVoteSessionRequest.
  create(channelName: string, request: CreateVoteSessionRequest): Observable<VoteSessionSummary> {
    return this.http.post<VoteSessionSummary>(`/api/channels/${channelName}/vote-sessions`, {
      title: request.title,
      allowedVoterRoles: request.allowedVoterRoles,
      ...(request.startedAt ? { startedAt: request.startedAt } : {}),
      ...(request.emoteIds?.length ? { emoteIds: request.emoteIds } : {}),
      ...(request.hideResultsUntilEnd ? { hideResultsUntilEnd: true } : {}),
      ...(request.emoteSetId ? { emoteSetId: request.emoteSetId } : {}),
      ...(request.sevenTvEmoteIds?.length ? { sevenTvEmoteIds: request.sevenTvEmoteIds } : {}),
    });
  }

  end(channelName: string, sessionId: number): Observable<VoteSessionSummary> {
    return this.http.post<VoteSessionSummary>(
      `/api/channels/${channelName}/vote-sessions/${sessionId}/end`,
      {},
    );
  }

  // Requires login + being part of the session's target audience (VoteAudienceFilter,
  // voteSessionAccessGuard) — anonymous viewing was removed, see CLAUDE.md decision log.
  getResults(channelName: string, sessionId: number): Observable<VoteSessionResults> {
    return this.http.get<VoteSessionResults>(
      `/api/channels/${channelName}/vote-sessions/${sessionId}/results`,
    );
  }

  castVote(
    channelName: string,
    sessionId: number,
    emoteId: string,
    type: VoteType,
  ): Observable<CastVoteResult> {
    return this.http.post<CastVoteResult>(
      `/api/channels/${channelName}/vote-sessions/${sessionId}/votes`,
      {
        emoteId,
        type,
      },
    );
  }

  // Returns the emote to the neutral (unvoted) state — clicking the same vote button again.
  retractVote(channelName: string, sessionId: number, emoteId: string): Observable<void> {
    return this.http.delete<void>(
      `/api/channels/${channelName}/vote-sessions/${sessionId}/votes/${emoteId}`,
    );
  }

  /**
   * Lets voteSessionAccessGuard hand its already-fetched `/results` response to the detail page
   * instead of the page re-fetching the same thing a few hundred milliseconds later (baseline (c)
   * measured this as two `/results` requests per page entry). Not a cache: a later stash always
   * replaces whatever is left over from an earlier one, see takeGuardResults.
   */
  stashGuardResults(channelName: string, sessionId: number, results: VoteSessionResults): void {
    this.guardResultsStash = { channelName, sessionId, results };
  }

  /**
   * Consumes the stash — a second call, for any key, returns null. A key that does not match what
   * is currently stashed also returns null and discards the stash, so it cannot go on to serve a
   * *later* call for the original key either: exactly one page load may ever be served by exactly
   * one guard probe.
   */
  takeGuardResults(channelName: string, sessionId: number): VoteSessionResults | null {
    const stash = this.guardResultsStash;
    this.guardResultsStash = null;
    if (stash?.channelName !== channelName || stash.sessionId !== sessionId) {
      return null;
    }
    return stash.results;
  }
}
