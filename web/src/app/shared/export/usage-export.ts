import { EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { UsageTrend } from '../emotes/emote-context';
import { CsvColumn, toCsv } from './csv';
import { ExportScope } from './export-dialog';
import { ExportEnvelope, buildEnvelope } from './export-envelope';
import { sanitizeFilenamePart } from './file-download';

/**
 * The fields a usage export reads off a row. The usage page hands in its merged set-view rows
 * (`EmoteUsageTotal`, spec #200 7.1) since the grid switched to them; `/totals` DTOs satisfy this
 * too. In a non-active set's view a count can be `null` ("no counts under this set", E17) — it then
 * serializes as an empty CSV cell / JSON `null`, never as 0 (spec 7.4; the export's own set fields
 * and the tests for that row shape are T4.5).
 */
export type UsageExportSourceRow = Pick<
  EmoteUsageTotal,
  | 'emoteName'
  | 'sevenTvEmoteId'
  | 'totalUseCount'
  | 'previousWindowUseCount'
  | 'lastUsedDate'
  | 'firstSeenAt'
>;

export interface UsageExportInput {
  channelName: string;
  /** ISO dates (`yyyy-MM-dd`) of the selected range, both inclusive. */
  from: string;
  to: string;
  /**
   * The rows the user chose in the export dialog: the visible (filtered + sorted) list, or the
   * grid selection — exporting rows the user is not looking at surprises.
   */
  rows: readonly UsageExportSourceRow[];
  /** Which of the two `rows` is; recorded in the JSON meta so the file says what subset it holds. */
  scope: ExportScope;
  /** Whether a grid filter was active — independent of `scope`, a selection can coexist with it. */
  filtered: boolean;
  /** The page owns the trend derivation (it knows `trackedSince`) — injected, not re-derived. */
  trendFor: (row: UsageExportSourceRow) => UsageTrend;
}

export interface UsageExportRow {
  emoteName: string;
  sevenTvEmoteId: string;
  totalUseCount: number | null;
  previousWindowUseCount: number | null;
  lastUsedDate: string | null;
  firstSeenAt: string | null;
  /** `unknown` = deliberately not stated (thin data), never a missing value. */
  trend: UsageTrend;
}

export interface UsageExportMeta {
  from: string;
  to: string;
  rowCount: number;
  scope: ExportScope;
  filtered: boolean;
}

export function usageExportFilename(input: UsageExportInput, ext: 'csv' | 'json'): string {
  return `emotepurge_${sanitizeFilenamePart(input.channelName)}_usage_${input.from}_${input.to}.${ext}`;
}

export function usageCsv(input: UsageExportInput): string {
  const columns: CsvColumn<UsageExportSourceRow>[] = [
    { header: 'emote_name', value: (row) => row.emoteName },
    { header: 'seven_tv_emote_id', value: (row) => row.sevenTvEmoteId },
    { header: 'total_use_count', value: (row) => row.totalUseCount },
    { header: 'previous_window_use_count', value: (row) => row.previousWindowUseCount },
    { header: 'last_used_date', value: (row) => row.lastUsedDate },
    { header: 'first_seen_at', value: (row) => row.firstSeenAt },
    { header: 'trend', value: (row) => input.trendFor(row) },
  ];
  return toCsv(input.rows, columns);
}

export function usageJson(input: UsageExportInput): string {
  const envelope: ExportEnvelope<UsageExportRow, UsageExportMeta> = buildEnvelope({
    kind: 'usage',
    channelName: input.channelName,
    // Every usage figure is visible to whoever can open this page — nothing is withheld here.
    withheld: [],
    meta: {
      from: input.from,
      to: input.to,
      rowCount: input.rows.length,
      scope: input.scope,
      filtered: input.filtered,
    },
    rows: input.rows.map((row) => ({
      emoteName: row.emoteName,
      sevenTvEmoteId: row.sevenTvEmoteId,
      totalUseCount: row.totalUseCount,
      previousWindowUseCount: row.previousWindowUseCount,
      lastUsedDate: row.lastUsedDate,
      firstSeenAt: row.firstSeenAt,
      trend: input.trendFor(row),
    })),
  });
  return JSON.stringify(envelope, null, 2);
}
