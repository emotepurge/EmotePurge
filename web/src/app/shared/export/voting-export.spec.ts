import { describe, expect, it } from 'vitest';

import {
  VoteSessionResult,
  VoteSessionResults,
  VoteType,
} from '../../core/voting/vote-session.model';
import {
  VotingExportInput,
  votingCsv,
  votingExportFilename,
  votingJson,
  withheldFields,
} from './voting-export';

function resultRow(overrides: Partial<VoteSessionResult> = {}): VoteSessionResult {
  return {
    emoteId: 'guid-1',
    emoteName: 'PogU',
    sevenTvEmoteId: '01ABC',
    imageUrl: 'https://cdn.7tv.app/x',
    totalUseCount: 42,
    keepVotes: 5,
    deleteVotes: 2,
    score: 3,
    isArchived: false,
    eligible: true,
    myVote: VoteType.Keep,
    ...overrides,
  };
}

function results(
  emotes: VoteSessionResult[],
  overrides: Partial<VoteSessionResults> = {},
): VoteSessionResults {
  return {
    sessionId: 12,
    title: 'Sommerputz',
    allowedVoterRoles: 1,
    isActive: true,
    startedAt: '2026-07-01T00:00:00Z',
    endedAt: null,
    voterCount: 7,
    hideResultsUntilEnd: true,
    emotes,
    emoteSetId: null,
    ...overrides,
  };
}

function input(
  rows: VoteSessionResult[],
  overrides: Partial<VotingExportInput> = {},
): VotingExportInput {
  return { channelName: 'sensitron', results: results(rows), rows, ...overrides };
}

describe('withheldFields', () => {
  it('reports nothing withheld on a fully visible result', () => {
    expect(withheldFields([resultRow()])).toEqual([]);
  });

  it('reports the tallies withheld when every row nulls them (secret ballot)', () => {
    const rows = [resultRow({ keepVotes: null, deleteVotes: null, score: null })];
    expect(withheldFields(rows)).toEqual(['keepVotes', 'deleteVotes', 'score']);
  });

  it('reports usage withheld when every row nulls it (non-manager)', () => {
    expect(withheldFields([resultRow({ totalUseCount: null })])).toEqual(['totalUseCount']);
  });

  it('treats a single null row as a gap, not a withholding', () => {
    // An archived ballot member loses its usage figure while the rest keeps one.
    const rows = [resultRow(), resultRow({ emoteId: 'guid-2', totalUseCount: null })];
    expect(withheldFields(rows)).toEqual([]);
  });

  it('reports nothing on an empty list', () => {
    expect(withheldFields([])).toEqual([]);
  });
});

describe('votingExportFilename', () => {
  it('uses session id and export day, never the free-text title', () => {
    const name = votingExportFilename(input([resultRow()]), 'json');
    expect(name).toMatch(/^emotepurge_sensitron_voting_12_\d{4}-\d{2}-\d{2}\.json$/);
    expect(name).not.toContain('Sommerputz');
  });
});

describe('votingCsv', () => {
  it('emits all columns with language-neutral vote tokens when nothing is withheld', () => {
    const csv = votingCsv(input([resultRow()]));
    const [header, row] = csv.replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(header).toBe(
      'emote_name,seven_tv_emote_id,keep_votes,delete_votes,score,total_use_count,my_vote,is_archived,eligible',
    );
    expect(row).toBe('PogU,01ABC,5,2,3,42,keep,false,true');
  });

  it('drops the tally columns entirely on a running secret ballot', () => {
    const rows = [resultRow({ keepVotes: null, deleteVotes: null, score: null })];
    const [header, row] = votingCsv(input(rows)).replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(header).toBe(
      'emote_name,seven_tv_emote_id,total_use_count,my_vote,is_archived,eligible',
    );
    expect(row).toBe('PogU,01ABC,42,keep,false,true');
  });

  it('keeps a single null as an empty cell instead of dropping the column', () => {
    const rows = [resultRow(), resultRow({ emoteId: 'guid-2', totalUseCount: null, myVote: null })];
    const lines = votingCsv(input(rows)).replace(/^﻿/, '').trimEnd().split('\r\n');
    expect(lines[2]).toBe('PogU,01ABC,5,2,3,,,false,true');
  });

  it('is_archived and eligible can disagree — a set-session member upserted "never active" (K6 whole-branch review)', () => {
    const row = resultRow({ isArchived: true, eligible: true });
    const [, csvRow] = votingCsv(input([row]))
      .replace(/^﻿/, '')
      .trimEnd()
      .split('\r\n');
    // ...,is_archived,eligible — true then true, not the same column read twice by coincidence.
    expect(csvRow.split(',').slice(-2)).toEqual(['true', 'true']);
  });
});

describe('votingJson', () => {
  it('omits withheld fields from the rows and lists them in withheld', () => {
    const rows = [resultRow({ keepVotes: null, deleteVotes: null, score: null })];
    const parsed = JSON.parse(votingJson(input(rows)));
    expect(parsed.withheld).toEqual(['keepVotes', 'deleteVotes', 'score']);
    expect(parsed.rows[0]).not.toHaveProperty('keepVotes');
    expect(parsed.rows[0]).not.toHaveProperty('score');
    expect(parsed.rows[0]).toMatchObject({ emoteName: 'PogU', totalUseCount: 42, myVote: 'keep' });
  });

  it('carries the session metadata including the title', () => {
    const parsed = JSON.parse(votingJson(input([resultRow()])));
    expect(parsed.kind).toBe('voting');
    expect(parsed.meta).toMatchObject({
      sessionId: 12,
      title: 'Sommerputz',
      voterCount: 7,
      hideResultsUntilEnd: true,
    });
    expect(parsed.rows[0]).toMatchObject({ keepVotes: 5, deleteVotes: 2, score: 3 });
  });

  it('carries eligible alongside is_archived, independently (K6 whole-branch review)', () => {
    const parsed = JSON.parse(votingJson(input([resultRow({ isArchived: true, eligible: true })])));
    expect(parsed.rows[0]).toMatchObject({ isArchived: true, eligible: true });
  });
});
