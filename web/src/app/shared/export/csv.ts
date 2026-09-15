/**
 * Minimal RFC-4180 CSV writer for the client-side exports. Headers are English snake_case and the
 * values are never locale-formatted: the file is data, not UI — a German UI must not change the
 * shape of a file that scripts and spreadsheets were written against.
 */

export const CSV_MIME = 'text/csv;charset=utf-8';

export interface CsvColumn<T> {
  /** English snake_case, language-independent. */
  header: string;
  /** `null`/`undefined` render as an empty cell. */
  value: (row: T) => string | number | null | undefined;
  /**
   * Absent → the column is always emitted. Returning `false` drops the column entirely (header
   * and cells): a withheld value must be missing, not empty — an empty tally column sums to
   * "0 votes" in a spreadsheet, which is exactly the misreading the secret ballot exists against.
   */
  present?: (rows: readonly T[]) => boolean;
}

/**
 * UTF-8 BOM + CRLF line endings + RFC-4180 quoting. Without the BOM, Excel decodes non-ASCII emote
 * names as mojibake — practically the most important byte of the file.
 */
export function toCsv<T>(rows: readonly T[], columns: readonly CsvColumn<T>[]): string {
  const emitted = columns.filter((column) => column.present?.(rows) ?? true);
  const lines = [emitted.map((column) => encodeCell(column.header)).join(',')];
  for (const row of rows) {
    lines.push(emitted.map((column) => encodeCell(column.value(row))).join(','));
  }
  return `﻿${lines.join('\r\n')}\r\n`;
}

function encodeCell(value: string | number | null | undefined): string {
  if (value === null || value === undefined) {
    return '';
  }
  // Numbers are our own and never start with a formula trigger a spreadsheet acts on — prefixing
  // a negative score with `'` would turn it into text and break summing over the column.
  if (typeof value === 'number') {
    return String(value);
  }

  // Emote names are attacker-controlled text: a leading =, +, -, @, tab or CR turns a cell into a
  // formula in Excel/LibreOffice (CSV injection). A leading apostrophe forces text mode.
  let cell = /^[=+\-@\t\r]/.test(value) ? `'${value}` : value;
  if (/[",\r\n]/.test(cell) || cell !== cell.trim()) {
    cell = `"${cell.replaceAll(/"/g, '""')}"`;
  }
  return cell;
}
