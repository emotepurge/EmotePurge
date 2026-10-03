import { ImportRow, dedupeImportRows } from '../../core/seven-tv/import-source';
import { EmoteUsageTotal } from '../../core/usage-stats/usage-stat.model';
import { UsageTrend } from '../emotes/emote-context';
import { CSV_MIME } from './csv';
import { ExportDialogOption, ExportScope } from './export-dialog';
import { JSON_MIME } from './export-envelope';
import { buildEmoteListEnvelope, emoteListFilename, emoteListJson } from './emote-list-export';
import {
  UsageExportInput,
  UsageExportSourceRow,
  usageCsv,
  usageExportFilename,
  usageJson,
} from './usage-export';

/**
 * The three purposes `openExport` offers, in display order (see `usageExportPurposeOptions`).
 * Moved out of the page (#141 follow-up) alongside the two decisions below, so both are testable
 * without mounting the 1400-line page component (Regel 12).
 */
export type ExportPurposeId = 'usage-csv' | 'usage-json' | 'emote-list';

/** What `buildUsageExportPurposeDownload` hands back — plain data, `downloadFile`'s three args. */
export interface UsageExportPurposeDownload {
  readonly filename: string;
  readonly content: string;
  readonly mimeType: string;
}

/** Everything a download needs beyond the purpose id and which rows to use. */
export interface UsageExportPurposeScope {
  readonly channelName: string;
  /** `null` when the channel has no active/selected 7TV set — the emote-list purpose is
   *  unreachable then; the usage purposes still work (spec 7.4, `UsageExportInput.emoteSetId`'s
   *  own doc). */
  readonly emoteSetId: string | null;
  /** The same set's display name, threaded straight into the usage purposes' `emoteSetName`
   *  (spec 7.4) — unused by `emote-list`, which names its set only in `meta`, not in a filename
   *  segment (`emote-list-export.ts`). */
  readonly emoteSetName: string | null;
  /** ISO dates (`yyyy-MM-dd`) of the selected range, both inclusive — only the usage purposes use these. */
  readonly from: string;
  readonly to: string;
  /** Whether a grid filter was active — independent of `scope`, a selection can coexist with it. */
  readonly filtered: boolean;
  /** The rows the caller already resolved for the chosen `scope` (visible list or grid selection). */
  readonly rows: readonly UsageExportSourceRow[];
  readonly scope: ExportScope;
  /** The page owns the trend derivation (it knows `trackedSince`) — injected, not re-derived. */
  readonly trendFor: (row: UsageExportSourceRow) => UsageTrend;
}

/**
 * The one mapping from a usage row to the shape a 7TV import wants. Exported because the push flow
 * in `usage-stats-page.ts` needs the identical mapping — two private copies would drift silently
 * the first time `ImportRow` gains a field.
 */
export const toImportRow = (
  // Both callers carry `imageUrl` since #230: the caller in this file feeds `UsageExportSourceRow`
  // (which now includes it), and `usage-stats-page.ts`'s push flow feeds full `EmoteUsageTotal`
  // rows, which always have.
  emote: Pick<EmoteUsageTotal, 'sevenTvEmoteId' | 'emoteName' | 'imageUrl'>,
): ImportRow => ({
  sevenTvEmoteId: emote.sevenTvEmoteId,
  name: emote.emoteName,
  imageUrl: emote.imageUrl,
});

/**
 * The option list `openExport`'s dialog offers, in display order: analyse (CSV), process (JSON),
 * and — only when `emoteListOfferable` — reimport (emote list) last. `emoteListOfferable` is the
 * caller's decision (E3: an active set that the capture matches), not this function's — it stays
 * pure and never reads a signal.
 */
export function usageExportPurposeOptions(
  emoteListOfferable: boolean,
): (ExportDialogOption & { id: ExportPurposeId })[] {
  const options: (ExportDialogOption & { id: ExportPurposeId })[] = [
    { id: 'usage-csv', labelKey: 'export.purposeAnalyse', hintKey: 'export.purposeAnalyseHint' },
    {
      id: 'usage-json',
      labelKey: 'export.purposeProcess',
      hintKey: 'export.purposeProcessHint',
    },
  ];
  if (emoteListOfferable) {
    options.push({
      id: 'emote-list',
      labelKey: 'export.purposeReimport',
      hintKey: 'export.purposeReimportHint',
    });
  }
  return options;
}

/**
 * Serializes the chosen purpose into a downloadable file. Returns `null` only for the
 * unreachable case: `emote-list` chosen without a captured `emoteSetId`, which cannot happen
 * because that option is only ever offered when `usageExportPurposeOptions` was called with
 * `emoteListOfferable = true` (see there). Narrowing rather than asserting keeps that invariant
 * checked instead of declared — if the offer rule and this branch ever drift apart, nothing is
 * written rather than a wrong file going out.
 */
export function buildUsageExportPurposeDownload(
  purposeId: ExportPurposeId,
  scope: UsageExportPurposeScope,
): UsageExportPurposeDownload | null {
  switch (purposeId) {
    case 'usage-csv':
    case 'usage-json': {
      const input: UsageExportInput = {
        channelName: scope.channelName,
        emoteSetId: scope.emoteSetId,
        emoteSetName: scope.emoteSetName,
        from: scope.from,
        to: scope.to,
        rows: scope.rows,
        scope: scope.scope,
        filtered: scope.filtered,
        trendFor: scope.trendFor,
      };
      return purposeId === 'usage-csv'
        ? {
            filename: usageExportFilename(input, 'csv'),
            content: usageCsv(input),
            mimeType: CSV_MIME,
          }
        : {
            filename: usageExportFilename(input, 'json'),
            content: usageJson(input),
            mimeType: JSON_MIME,
          };
    }
    case 'emote-list': {
      const emoteSetId = scope.emoteSetId;
      if (emoteSetId === null) {
        return null;
      }
      // Same dedupe, envelope and filename the target dialog's file destination used before #141
      // moved it here — the written file is unchanged, only the way in.
      const deduped = dedupeImportRows(scope.rows.map(toImportRow));
      const envelope = buildEmoteListEnvelope({
        channelName: scope.channelName,
        emoteSetId,
        scope: scope.scope,
        rows: deduped.rows,
      });
      return {
        filename: emoteListFilename(scope.channelName, envelope.exportedAt),
        content: emoteListJson(envelope),
        mimeType: JSON_MIME,
      };
    }
    default: {
      // Exhaustiveness check: a new ExportPurposeId that reaches here fails the build.
      const exhaustive: never = purposeId;
      throw new Error(`Unhandled export purpose: ${String(exhaustive)}`);
    }
  }
}
