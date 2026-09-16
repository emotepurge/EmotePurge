#!/usr/bin/env node
/**
 * Forbids raw Tailwind palette utilities under web/src/app/.
 *
 * Colour is a role, not a value (docs/UI-Designsprache.md §2.0): templates, variant maps and
 * component classes write the semantic utilities (bg-surface, text-fg-muted, border-border, the
 * tone quads), and palette names appear at exactly one place — the token block in
 * web/src/styles.css. Without a gate that is a request rather than a rule, and the first person in
 * a hurry writes bg-slate-800 again.
 *
 * A plain `rg` step would have been shorter, but ripgrep is not guaranteed on a CI runner, and
 * `npm run lint` already runs in the pipeline.
 *
 * THE EXEMPTION LIST IS SELF-EXPIRING. An entry whose file no longer has any violation is itself an
 * error. That is the whole point: the list was introduced with the admin and landing files still
 * open, and the wave that cleans a file up cannot forget to delete its entry, because forgetting
 * turns the build red just as surely as a new violation does. It must end up empty.
 */
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const WEB_ROOT = fileURLToPath(new URL('..', import.meta.url));
const SCAN_ROOT = join(WEB_ROOT, 'src', 'app');
const EXTENSIONS = ['.ts', '.html'];

const PALETTES =
  'slate|purple|red|amber|emerald|blue|pink|gray|zinc|neutral|stone|orange|yellow|lime|green|teal|cyan|sky|indigo|violet|fuchsia|rose';
const PREFIXES =
  'bg|text|border|ring|inset-ring|divide|from|via|to|accent|shadow|outline|fill|stroke|placeholder|caret|decoration';

// Two shapes: a numbered palette step (bg-slate-800, text-red-400/40) and the two unnumbered
// absolutes (text-white, border-black). Optional variant prefixes (hover:, sm:, disabled:) sit in
// front and are matched by the leading boundary, not enumerated.
const VIOLATION = new RegExp(
  String.raw`\b(?:${PREFIXES})-(?:${PALETTES})-\d{2,3}\b|\b(?:${PREFIXES})-(?:white|black)\b`,
  'g',
);

/**
 * Comments are not code. Naming a palette colour while explaining why it is gone has to stay
 * possible, or the rule ends up discouraging exactly the comments that make it understandable.
 * `//` is only treated as a comment when it is not part of a scheme (`https://`).
 */
function stripComments(source) {
  return source
    .replace(/<!--[\s\S]*?-->/g, ' ')
    .replace(/\/\*[\s\S]*?\*\//g, ' ')
    .replace(/(^|[^:])\/\/[^\n]*/g, '$1');
}

function* walk(dir) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      yield* walk(full);
    } else if (EXTENSIONS.some((ext) => entry.endsWith(ext))) {
      yield full;
    }
  }
}

function findViolations(file) {
  const lines = stripComments(readFileSync(file, 'utf8')).split('\n');
  const hits = [];
  lines.forEach((line, index) => {
    for (const match of line.matchAll(VIOLATION)) {
      hits.push({ line: index + 1, utility: match[0] });
    }
  });
  return hits;
}

/**
 * Files still allowed to carry palette utilities, with the wave that clears them. Repo-relative,
 * forward slashes. Delete an entry in the same commit that cleans the file — see the note above.
 *
 * EMPTY, and it is meant to stay that way. It held the seven admin files and the landing page while
 * the light mode was being rolled out; both waves cleared their own entries because leaving them
 * would have failed the stale check. A new entry here is a temporary debt with a named owner, not
 * a way to opt a file out.
 */
const EXEMPT = new Map([]);

const failures = [];
const usedExemptions = new Set();

for (const file of walk(SCAN_ROOT)) {
  const key = `web/${relative(WEB_ROOT, file).split(sep).join('/')}`;
  const hits = findViolations(file);
  if (hits.length === 0) {
    continue;
  }
  if (EXEMPT.has(key)) {
    usedExemptions.add(key);
    continue;
  }
  for (const hit of hits) {
    failures.push(`${key}:${hit.line}  ${hit.utility}`);
  }
}

const stale = [...EXEMPT.keys()].filter((key) => !usedExemptions.has(key));

if (failures.length > 0) {
  console.error(
    `\nPalette utilities are not allowed under web/src/app/ (docs/UI-Designsprache.md §2.0).`,
  );
  console.error(
    `Use a semantic token, or add one with values for BOTH modes and a contrast check.`,
  );
  console.error(
    `Palette names belong in the token block of web/src/styles.css and nowhere else.\n`,
  );
  for (const failure of failures) {
    console.error(`  ${failure}`);
  }
  console.error('');
}

if (stale.length > 0) {
  console.error(
    `\nStale exemptions in scripts/check-color-tokens.mjs — these files are clean now.`,
  );
  console.error(`Delete their entries from EXEMPT; the list has to reach zero.\n`);
  for (const key of stale) {
    console.error(`  ${key}  (${EXEMPT.get(key)})`);
  }
  console.error('');
}

if (failures.length > 0 || stale.length > 0) {
  process.exit(1);
}

const remaining = EXEMPT.size;
console.log(
  remaining === 0
    ? 'check-color-tokens: clean, no exemptions left.'
    : `check-color-tokens: clean (${remaining} file${remaining === 1 ? '' : 's'} still exempt).`,
);
