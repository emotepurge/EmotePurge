import { ExportEnvelope } from './export-envelope';

/**
 * The one shared entry point for reading a downloaded EmotePurge export back in (#72, K3):
 * `JSON.parse` and the CSV-vs-corrupt-file heuristic live here and nowhere else, so every parser
 * (purge-run, import source) shares the same "is this even one of ours" gate instead of repeating
 * it with slightly different edge cases. Everything past that — which `kind`, which `formatVersion`,
 * which rows are valid — is the caller's business; this function does not even look at `rows`.
 */
export type ReadEnvelopeResult =
  { ok: true; envelope: ExportEnvelope<unknown> } | { ok: false; errorKey: string };

export function readEnvelope(text: string): ReadEnvelopeResult {
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return {
      ok: false,
      errorKey: looksLikeExportCsv(text)
        ? 'restore.import.errors.csvInsteadOfJson'
        : 'restore.import.errors.notJson',
    };
  }

  const envelope = parsed as Partial<ExportEnvelope<unknown>> | null;
  if (envelope?.source !== 'emotepurge') {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }
  // `kind` is untrusted input — a non-string (missing, number, object, …) can never match a known
  // kind further down, so reject it here instead of letting every caller re-derive the same thing.
  if (typeof envelope.kind !== 'string') {
    return { ok: false, errorKey: 'restore.import.errors.wrongKind' };
  }

  return { ok: true, envelope: envelope as ExportEnvelope<unknown> };
}

/**
 * Every export dialog offers CSV next to JSON, including the ones re-importable as JSON — so
 * "picked the wrong format" is a routine mistake, not a corrupt file, and deserves to be told apart
 * from one. All our CSVs carry the 7TV id column; `toCsv` writes a BOM in front of the header.
 */
function looksLikeExportCsv(text: string): boolean {
  const firstLine = text.replace(/^﻿/, '').split(/\r?\n/, 1)[0] ?? '';
  return firstLine.includes('seven_tv_emote_id');
}
