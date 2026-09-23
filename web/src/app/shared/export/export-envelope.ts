/**
 * The one JSON shape every EmotePurge export uses. `kind` + `formatVersion` make a downloaded file
 * self-describing, which is what lets the purge-run protocol (A6) be re-imported and validated
 * instead of trusted.
 */

export const EXPORT_FORMAT_VERSION = 1;

export type ExportKind = 'usage' | 'voting' | 'purge-run' | 'emote-list' | 'transfer-run';

export const JSON_MIME = 'application/json';

export interface ExportEnvelope<TRow, TMeta = Record<string, unknown>> {
  source: 'emotepurge';
  kind: ExportKind;
  formatVersion: number;
  /** ISO timestamp of the export itself. */
  exportedAt: string;
  channelName: string;
  /**
   * Field names the server withheld from the viewer (secret ballot, manager-only usage). Withheld
   * fields are *omitted* from `rows` and listed here instead — a `null` could not tell "withheld"
   * apart from "does not exist", and a consumer must not read either as zero.
   */
  withheld: string[];
  meta: TMeta;
  rows: TRow[];
}

export function buildEnvelope<TRow, TMeta>(
  input: Omit<ExportEnvelope<TRow, TMeta>, 'source' | 'formatVersion' | 'exportedAt'>,
): ExportEnvelope<TRow, TMeta> {
  return {
    source: 'emotepurge',
    formatVersion: EXPORT_FORMAT_VERSION,
    exportedAt: new Date().toISOString(),
    ...input,
  };
}
