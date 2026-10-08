import {
  VoteSessionResult,
  VoteSessionResults,
  VoteType,
} from '../../core/voting/vote-session.model';
import { CsvColumn, toCsv } from './csv';
import { ExportEnvelope, buildEnvelope } from './export-envelope';
import { sanitizeFilenamePart } from './file-download';

export interface VotingExportInput {
  channelName: string;
  results: VoteSessionResults;
  /** The visible (filtered + ordered) list, not necessarily `results.emotes`. */
  rows: readonly VoteSessionResult[];
}

export interface VotingExportMeta {
  sessionId: number;
  title: string;
  isActive: boolean;
  startedAt: string;
  endedAt: string | null;
  voterCount: number;
  hideResultsUntilEnd: boolean;
}

/**
 * Which fields the server withheld from this viewer, derived from the read model itself: `null`
 * across the board *is* the server's visibility decision (secret ballot, manager-only usage), so
 * the export never re-implements the rule — it reads the verdict. Drives the dropped CSV columns,
 * the omitted JSON fields, and the notice text in the export dialog.
 */
export function withheldFields(rows: readonly VoteSessionResult[]): string[] {
  const withheld: string[] = [];
  if (rows.length > 0 && rows.every((row) => row.keepVotes === null)) {
    withheld.push('keepVotes', 'deleteVotes', 'score');
  }
  if (rows.length > 0 && rows.every((row) => row.totalUseCount === null)) {
    withheld.push('totalUseCount');
  }
  return withheld;
}

export function votingExportFilename(input: VotingExportInput, ext: 'csv' | 'json'): string {
  // Deliberately the session id + export date, never the free-text title — see the DECISIONS entry.
  const day = new Date().toISOString().slice(0, 10);
  const channel = sanitizeFilenamePart(input.channelName);
  return `emotepurge_${channel}_voting_${input.results.sessionId}_${day}.${ext}`;
}

export function votingCsv(input: VotingExportInput): string {
  const withheld = withheldFields(input.rows);
  const talliesPresent = () => !withheld.includes('keepVotes');
  const columns: CsvColumn<VoteSessionResult>[] = [
    { header: 'emote_name', value: (row) => row.emoteName },
    { header: 'seven_tv_emote_id', value: (row) => row.sevenTvEmoteId },
    { header: 'keep_votes', value: (row) => row.keepVotes, present: talliesPresent },
    { header: 'delete_votes', value: (row) => row.deleteVotes, present: talliesPresent },
    { header: 'score', value: (row) => row.score, present: talliesPresent },
    {
      header: 'total_use_count',
      value: (row) => row.totalUseCount,
      present: () => !withheld.includes('totalUseCount'),
    },
    { header: 'my_vote', value: (row) => formatVote(row.myVote) },
    // is_archived kept for compatibility (an existing consumer's column, spec section 9's own
    // history) even though it no longer gates anything in the app — eligible is what actually
    // decides the vote lock/badge now, and the two can disagree: a set-session's member reads
    // is_archived true the moment it was upserted "never active" (T6.1, ArchivedAt = null) while
    // staying eligible true throughout, since a set-session's fixed ballot never closes.
    { header: 'is_archived', value: (row) => String(row.isArchived) },
    { header: 'eligible', value: (row) => String(row.eligible) },
  ];
  return toCsv(input.rows, columns);
}

export function votingJson(input: VotingExportInput): string {
  const withheld = withheldFields(input.rows);
  const includeTallies = !withheld.includes('keepVotes');
  const includeUsage = !withheld.includes('totalUseCount');
  const envelope: ExportEnvelope<Record<string, unknown>, VotingExportMeta> = buildEnvelope({
    kind: 'voting',
    channelName: input.channelName,
    withheld,
    meta: {
      sessionId: input.results.sessionId,
      title: input.results.title,
      isActive: input.results.isActive,
      startedAt: input.results.startedAt,
      endedAt: input.results.endedAt,
      voterCount: input.results.voterCount,
      hideResultsUntilEnd: input.results.hideResultsUntilEnd,
    },
    rows: input.rows.map((row) => ({
      emoteName: row.emoteName,
      sevenTvEmoteId: row.sevenTvEmoteId,
      // Withheld fields are omitted rather than nulled — `withheld` in the envelope carries the
      // "kept from you" statement, so a null here could only be misread.
      ...(includeTallies
        ? { keepVotes: row.keepVotes, deleteVotes: row.deleteVotes, score: row.score }
        : {}),
      ...(includeUsage ? { totalUseCount: row.totalUseCount } : {}),
      myVote: formatVote(row.myVote),
      isArchived: row.isArchived,
      eligible: row.eligible,
    })),
  });
  return JSON.stringify(envelope, null, 2);
}

/** Language-independent tokens, empty when the viewer never voted on the row. */
function formatVote(vote: VoteType | null): string {
  switch (vote) {
    case VoteType.Keep:
      return 'keep';
    case VoteType.Delete:
      return 'delete';
    default:
      return '';
  }
}
