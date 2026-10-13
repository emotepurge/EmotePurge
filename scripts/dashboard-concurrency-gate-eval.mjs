#!/usr/bin/env node
// Evaluates a CSV written by scripts/dashboard-concurrency-gate.sh against the concurrency gate of the
// admin load dashboard (#381 cut B, spec section 11 "B-Parallelitaets-Gate"). Separate from the
// measurement so that prepared violations (scripts/fixtures/dashboard-gate/) can prove that the
// evaluation actually fails: exit 0 = gate passed, 1 = gate failed, 2 = run invalid (incomplete or
// broken measurement, or too few rebuild cycles or probes inside rebuilds to judge), 64 = usage or
// malformed input. Invalidity wins over a violation (exit 2, both listed): numbers from an
// incomplete or broken run cannot be trusted in either direction.
//
// CSV columns: kind,phase,tick,range,start_ms,end_ms,status,value,size_bytes,note
//   meta    - first data row: note = "clients=..;main_ticks=..;cooldown_ticks=..;dual_cycles=..;..."
//             (the tick plan the measurement ran)
//   probe   - one GET /api/health: start_ms = send time, status = HTTP code (000 = timeout or
//             connection error), value = latency in ms
//   load    - one GET /api/admin/dashboard/load: range, status, value = time_total in ms,
//             size_bytes, note = Retry-After header value
//   rebuild - one logged cold rebuild of the facade: range, start/end from the Debug log lines,
//             status = outcome (ok|failed), value = logged duration in ms
//   mem     - one memory snapshot: start/end = previous/this snapshot, status = clear_refs|fallback,
//             value = VmHWM since the previous reset minus VmRSS at that reset, in KiB;
//             note carries hwm_kib, rss_base_kib, rss_now_kib, cgroup_memory_current_bytes
//   query   - pg_stat_statements calls of the two load queries between the previous and this snapshot
//
// Completeness (else invalid): the meta row; per tick of the plan exactly one probe, one mem and one
// query row; per main tick CLIENTS load rows of its range, per dual tick CLIENTS of each range, none
// in cooldown ticks; every mem row with a positive VmHWM and VmRSS base and an increment that is a
// number not below -4 MiB. Small negative increments are real: the kernel's RSS counters are
// per-CPU approximations, so VmHWM read later can sit a few hundred KiB below the VmRSS read right
// after the reset (measured down to -1.1 MiB). They count as 0; anything further below is a broken
// measurement, not a quiet tick.
//
// A load request counts as served by a rebuild of its range only if it was in flight when the
// rebuild finished: sent before the rebuild ended and completed only after it. A cached answer has
// ended before. This needs request times measured at curl's end (meta timing=curl_end, start =
// end - time_total). CSVs without that flag recorded the send time before spawning curl, which runs
// early by the spawn delay (up to ~160 ms measured); for them the evaluation falls back to a
// heuristic and says so ("legacy attribution"): sent before the rebuild started, the rebuild started
// at most LEGACY_SPAWN_SLACK_MS after the recorded end, and time_total at least half the rebuild.
//
// Criteria (thresholds from the spec):
//   validity: >= 10 main-phase rebuild cycles per key (24h and 13mo) and >= 40 health probes whose
//             send time lies inside a logged rebuild interval
//   (a) at most one rebuild per range and tick, every load request of the main phase answered 200,
//       and every snapshot window has as many load queries (pg_stat_statements) as logged rebuilds
//       ended in it
//   (b) every memory increment <= 64 MiB
//   (c) every health probe answered 200; p95 latency of the probes sent inside a rebuild < 100 ms
//       (200s only; probes outside rebuilds count only for the "every probe 200" rule)
//   (d) each dual cycle (24h and 13mo at the same instant): exactly one rebuild per key, run one after
//       the other; every request of the key rebuilt first answered 200 from that rebuild; every
//       request of the key rebuilt second got 200 from its rebuild with a gate wait < 5 s (wait = its
//       rebuild start minus the request send time; the 5 s bound is the facade's wait for the
//       rebuild gate, not the total response time) or 503 with Retry-After
//   (e) uncached 24h load responses: median time_total < 500 ms (the time limit gates 24h only;
//       13mo is reported); every load response <= 256 KiB

import { lstatSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, extname, isAbsolute, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const MEMORY_LIMIT_KIB = 64 * 1024;
const MEMORY_JITTER_KIB = 4 * 1024;
const PROBE_P95_LIMIT_MS = 100;
const UNCACHED_MEDIAN_LIMIT_MS = 500;
const RESPONSE_SIZE_LIMIT_BYTES = 256 * 1024;
const GATE_WAIT_LIMIT_MS = 5000;
const MIN_CYCLES_PER_KEY = 10;
const MIN_PROBES_IN_REBUILD = 40;
// end_ms - floor(time_total) and the log's millisecond timestamps leave a few ms of rounding.
const END_SLACK_MS = 3;
// Legacy CSVs only: recorded times run early by the curl spawn delay, so the rebuild may start up to
// this long after the recorded end; a served request also lasted at least half the rebuild, which
// keeps fast cached answers (2-30 ms) out.
const LEGACY_SPAWN_SLACK_MS = 200;
const KEYS = ['24h', '13mo'];
// Directories the CSV may live in: the repository (prepared fixtures) and the temp directory
// (default output of dashboard-concurrency-gate.sh).
const ALLOWED_BASES = [
  resolve(dirname(fileURLToPath(import.meta.url)), '..'),
  resolve(process.env.TMPDIR || tmpdir()),
];

const file = process.argv[2];
if (!file) {
  console.error('usage: node scripts/dashboard-concurrency-gate-eval.mjs <gate.csv>');
  process.exit(64);
}

const rows = parseCsv(readCsvFile(file));
const byKind = (kind) => rows.filter((row) => row.kind === kind);
const probes = byKind('probe');
const loads = byKind('load');
const rebuilds = byKind('rebuild');
const mems = byKind('mem');
const queries = byKind('query');

const failures = [];
const invalid = [];
const lines = [];

// Completeness against the tick plan of the meta row.
const meta = parseMeta(byKind('meta')[0]);
const plan = meta ? buildPlan(meta) : [];
if (!meta) {
  invalid.push('no meta row with the tick plan (clients, main_ticks, cooldown_ticks, dual_cycles)');
} else {
  const missing = [];
  plan.forEach((ranges, tick) => {
    for (const [kind, list] of [
      ['probe', probes],
      ['mem', mems],
      ['query', queries],
    ]) {
      const count = list.filter((row) => row.tick === tick).length;
      if (count !== 1) missing.push(`tick ${tick}: ${count} ${kind} rows`);
    }
    for (const key of KEYS) {
      const expected = ranges.includes(key) ? meta.clients : 0;
      const count = loads.filter((row) => row.tick === tick && row.range === key).length;
      if (count !== expected) missing.push(`tick ${tick}: ${count} of ${expected} ${key} load rows`);
    }
  });
  const beyond = rows.filter((row) => row.tick !== undefined && row.tick >= plan.length).length;
  if (beyond > 0) missing.push(`${beyond} rows with a tick beyond the plan of ${plan.length}`);
  lines.push(`plan: ${plan.length} ticks, ${meta.clients} clients; ${missing.length} completeness gaps`);
  for (const gap of missing.slice(0, 10)) invalid.push(`incomplete measurement: ${gap}`);
  if (missing.length > 10) invalid.push(`incomplete measurement: ... ${missing.length - 10} more gaps`);
}
for (const row of [...probes, ...loads, ...rebuilds, ...queries]) {
  if (!Number.isFinite(row.start) || !Number.isFinite(row.end) || !Number.isFinite(row.value)) {
    invalid.push(`broken ${row.kind} row at tick ${row.tick ?? '-'}: non-numeric time or value`);
  }
}
// A 200 without a size would slip past the 256-KiB check.
for (const load of loads.filter((l) => l.status === '200' && !(Number.isFinite(l.size) && l.size > 0))) {
  invalid.push(`broken load row at tick ${load.tick}: ${load.range} answered 200 with size '${load.raw.size_bytes}'`);
}

// Which load requests did a rebuild serve?
const endMeasured = meta?.timing === 'curl_end';
lines.push(
  endMeasured
    ? 'attribution: request in flight when the rebuild finished (times from curl end)'
    : 'attribution: LEGACY heuristic - send times recorded before spawning curl (see header)',
);
const servedBy = endMeasured
  ? (load, rebuild) =>
      load.range === rebuild.range && load.start <= rebuild.end && load.end >= rebuild.end - END_SLACK_MS
  : (load, rebuild) =>
      load.range === rebuild.range &&
      load.start <= rebuild.start &&
      rebuild.start <= load.end + LEGACY_SPAWN_SLACK_MS &&
      load.value >= rebuild.value / 2;
const rebuildRequests = rebuilds.map((rebuild) => ({
  rebuild,
  requests: loads.filter((load) => servedBy(load, rebuild)),
}));
const phaseOf = (entry) => entry.requests[0]?.phase ?? 'none';
const tickOf = (entry) => entry.requests[0]?.tick ?? 'none';
// A gate-key rebuild nobody measurable waited for cannot be attributed to a cycle; the preflight 1h
// read is the only expected rebuild without one.
for (const entry of rebuildRequests.filter((e) => KEYS.includes(e.rebuild.range) && e.requests.length === 0)) {
  invalid.push(`rebuild of ${entry.rebuild.range} at ${entry.rebuild.start} served no measured request`);
}

// Validity.
const mainCycles = rebuildRequests.filter((entry) => phaseOf(entry) === 'main');
for (const key of KEYS) {
  const count = mainCycles.filter((entry) => entry.rebuild.range === key).length;
  lines.push(`cycles ${key}: ${count} main-phase cold rebuilds`);
  if (count < MIN_CYCLES_PER_KEY) {
    invalid.push(`only ${count} main-phase rebuild cycles for ${key} (need >= ${MIN_CYCLES_PER_KEY})`);
  }
}
const inRebuild = (probe) => rebuilds.some((rebuild) => rebuild.start <= probe.start && probe.start <= rebuild.end);
const probesInRebuild = probes.filter(inRebuild);
lines.push(`probes: ${probes.length} total, ${probesInRebuild.length} sent inside a logged rebuild`);
if (probesInRebuild.length < MIN_PROBES_IN_REBUILD) {
  invalid.push(`only ${probesInRebuild.length} health probes inside a rebuild (need >= ${MIN_PROBES_IN_REBUILD})`);
}

// (a) Single flight.
const rebuildsPerTickRange = new Map();
for (const entry of rebuildRequests.filter((e) => phaseOf(e) !== 'none')) {
  const key = `${tickOf(entry)}/${entry.rebuild.range}`;
  rebuildsPerTickRange.set(key, (rebuildsPerTickRange.get(key) ?? 0) + 1);
}
for (const [key, count] of rebuildsPerTickRange) {
  if (count !== 1) {
    failures.push(`(a) tick/range ${key}: ${count} rebuilds for one set of concurrent requests`);
  }
}
for (const load of loads.filter((l) => l.phase === 'main' && l.status !== '200')) {
  failures.push(`(a) main tick ${load.tick}: ${load.range} load answered ${load.status}`);
}
let queryMismatches = 0;
for (const query of queries) {
  const ended = rebuilds.filter((rebuild) => query.start < rebuild.end && rebuild.end <= query.end).length;
  if (query.value !== ended) {
    queryMismatches++;
    failures.push(`(a) snapshot window ending tick ${query.tick}: ${query.value} load queries, ${ended} logged rebuilds`);
  }
}
const requestsPerCycle = mainCycles.map((entry) => entry.requests.length);
lines.push(
  `(a) ${mainCycles.length} main cycles, requests served per rebuild: min ${fmtInt(Math.min(...requestsPerCycle))} ` +
    `max ${fmtInt(Math.max(...requestsPerCycle))}; ${queries.length} query windows, ${queryMismatches} log/pg_stat_statements mismatches`,
);

// (b) Memory.
const memoryModes = [...new Set(mems.map((mem) => mem.status))];
const memoryFields = (mem) => Object.fromEntries((mem.note ?? '').split(';').map((pair) => pair.split('=')));
let jitterCount = 0;
let jitterMin = 0;
const memoryIncrements = [];
for (const mem of mems) {
  const fields = memoryFields(mem);
  const hwm = Number(fields.hwm_kib);
  const base = Number(fields.rss_base_kib);
  if (!['clear_refs', 'fallback'].includes(mem.status)) {
    invalid.push(`broken memory snapshot at tick ${mem.tick}: unknown mode '${mem.status}'`);
  } else if (!(hwm > 0) || !(base > 0)) {
    invalid.push(`broken memory snapshot at tick ${mem.tick}: VmHWM '${fields.hwm_kib ?? ''}', VmRSS base '${fields.rss_base_kib ?? ''}'`);
  } else if (!Number.isFinite(mem.value)) {
    invalid.push(`broken memory snapshot at tick ${mem.tick}: increment '${mem.raw.value}' is not a number`);
  } else if (mem.value < -MEMORY_JITTER_KIB) {
    invalid.push(`broken memory snapshot at tick ${mem.tick}: increment ${kibToMib(mem.value)} MiB is below the -4 MiB counter jitter`);
  } else {
    if (mem.value < 0) {
      jitterCount++;
      jitterMin = Math.min(jitterMin, mem.value);
    }
    memoryIncrements.push({ tick: mem.tick, kib: Math.max(0, mem.value) });
  }
}
if (mems.length === 0) {
  invalid.push('no memory snapshots');
}
const worstMemory = Math.max(...memoryIncrements.map((m) => m.kib));
const rebuildTicks = new Set(rebuildRequests.map((entry) => tickOf(entry)));
const rebuildMemory = memoryIncrements.filter((m) => rebuildTicks.has(m.tick)).map((m) => m.kib);
lines.push(
  `(b) memory mode ${memoryModes.join('+') || 'none'}; increment max ${kibToMib(worstMemory)} MiB over ${memoryIncrements.length} snapshots, ` +
    `median ${kibToMib(median(rebuildMemory))} MiB over ${rebuildMemory.length} rebuild ticks; ` +
    `${jitterCount} negative increments counted as 0 (lowest ${kibToMib(jitterMin)} MiB)`,
);
for (const mem of memoryIncrements) {
  if (mem.kib > MEMORY_LIMIT_KIB) {
    failures.push(`(b) memory increment ${kibToMib(mem.kib)} MiB at tick ${mem.tick} exceeds 64 MiB`);
  }
}

// (c) Health probes.
for (const probe of probes) {
  if (probe.status !== '200') {
    failures.push(`(c) health probe at tick ${probe.tick} answered ${probe.status === '000' ? 'timeout/connection error' : probe.status}`);
  }
}
const okInRebuild = probesInRebuild.filter((probe) => probe.status === '200').map((probe) => probe.value);
const okAll = probes.filter((p) => p.status === '200').map((p) => p.value);
const probeP95 = percentile95(okInRebuild);
lines.push(
  `(c) probe p95 inside rebuilds ${fmt(probeP95)} ms (n=${okInRebuild.length}), max ${fmt(Math.max(...okInRebuild))} ms; ` +
    `all 200s: p95 ${fmt(percentile95(okAll))} ms, max ${fmt(Math.max(...okAll))} ms`,
);
if (probeP95 !== undefined && probeP95 >= PROBE_P95_LIMIT_MS) {
  failures.push(`(c) health probe p95 inside rebuilds ${fmt(probeP95)} ms is not below 100 ms`);
}

// (d) Two keys at once.
const dualTicks = [...new Set(loads.filter((load) => load.phase === 'dual').map((load) => load.tick))];
if (dualTicks.length === 0) {
  invalid.push('no dual-key cycle measured');
}
for (const tick of dualTicks) {
  const tickLoads = loads.filter((load) => load.phase === 'dual' && load.tick === tick);
  const tickEntries = rebuildRequests
    .filter((entry) => entry.requests.some((load) => load.phase === 'dual' && load.tick === tick))
    .sort((x, y) => x.rebuild.start - y.rebuild.start);
  if (tickEntries.length !== 2 || tickEntries[0].rebuild.range === tickEntries[1].rebuild.range) {
    failures.push(
      `(d) dual tick ${tick}: expected one rebuild per key, got ${tickEntries.map((e) => e.rebuild.range).join('+') || 'none'}`,
    );
    for (const load of tickLoads.filter((l) => l.status !== '200' && l.status !== '503')) {
      failures.push(`(d) dual tick ${tick}: ${load.range} answered ${load.status}`);
    }
    continue;
  }
  const [first, second] = tickEntries;
  if (second.rebuild.start < first.rebuild.end) {
    failures.push(`(d) dual tick ${tick}: rebuilds of ${first.rebuild.range} and ${second.rebuild.range} overlapped`);
  }
  for (const load of tickLoads.filter((l) => l.range === first.rebuild.range)) {
    if (load.status !== '200') {
      failures.push(`(d) dual tick ${tick}: first key ${load.range} answered ${load.status}`);
    } else if (!first.requests.includes(load)) {
      failures.push(`(d) dual tick ${tick}: a first-key ${load.range} request was not served by its rebuild`);
    }
  }
  for (const load of tickLoads.filter((l) => l.range === second.rebuild.range)) {
    if (load.status === '503') {
      if (!load.note) {
        failures.push(`(d) dual tick ${tick}: ${load.range} answered 503 without Retry-After`);
      }
    } else if (load.status !== '200') {
      failures.push(`(d) dual tick ${tick}: second key ${load.range} answered ${load.status}`);
    } else if (!second.requests.includes(load)) {
      failures.push(`(d) dual tick ${tick}: a second-key ${load.range} request was not served by its rebuild`);
    } else if (second.rebuild.start - load.start >= GATE_WAIT_LIMIT_MS) {
      failures.push(`(d) dual tick ${tick}: ${load.range} waited ${second.rebuild.start - load.start} ms for the rebuild gate`);
    }
  }
  const secondLoads = tickLoads.filter((l) => l.range === second.rebuild.range);
  lines.push(
    `(d) dual tick ${tick}: first ${first.rebuild.range} ${first.rebuild.value} ms (${first.requests.length} served), ` +
      `second ${second.rebuild.range} gate wait ${second.rebuild.start - Math.min(...secondLoads.map((l) => l.start))} ms, ` +
      `rebuild ${second.rebuild.value} ms (${second.requests.length} served), responses ` +
      `${secondLoads.map((l) => `${l.status}/${fmt(l.value)}ms`).join(' ')}`,
  );
}

// (e) Uncached response time and size.
const uncached = (key) =>
  mainCycles.filter((entry) => entry.rebuild.range === key).flatMap((entry) => entry.requests.filter((l) => l.status === '200'));
for (const key of KEYS) {
  const times = uncached(key).map((load) => load.value);
  const sizes = loads.filter((l) => l.range === key && l.status === '200').map((load) => load.size);
  lines.push(
    `(e) uncached ${key}${key === '24h' ? '' : ' (reported, not gated)'}: time_total median ${fmt(median(times))} ms, ` +
      `p95 ${fmt(percentile95(times))} ms, max ${fmt(Math.max(...times))} ms (n=${times.length}); size max ${fmtInt(Math.max(...sizes))} bytes`,
  );
}
const uncached24 = median(uncached('24h').map((load) => load.value));
if (uncached24 !== undefined && uncached24 >= UNCACHED_MEDIAN_LIMIT_MS) {
  failures.push(`(e) uncached 24h load response median ${fmt(uncached24)} ms is not below 500 ms`);
}
for (const load of loads.filter((l) => l.status === '200' && l.size > RESPONSE_SIZE_LIMIT_BYTES)) {
  failures.push(`(e) load ${load.range} at tick ${load.tick}: ${load.size} bytes exceeds 256 KiB`);
}

for (const line of lines) {
  console.log(line);
}
report('RUN INVALID', invalid);
report('GATE FAILED', failures);
if (invalid.length > 0) {
  process.exit(2);
}
if (failures.length > 0) {
  process.exit(1);
}
console.log('GATE PASSED');

function report(title, reasons) {
  if (reasons.length === 0) return;
  const shown = reasons.slice(0, 20);
  console.log(`${title} (${reasons.length} reason(s)):`);
  for (const reason of shown) console.log(`  ${reason}`);
  if (reasons.length > shown.length) console.log(`  ... ${reasons.length - shown.length} more`);
}

function parseMeta(row) {
  if (!row) return undefined;
  const fields = Object.fromEntries(row.note.split(';').map((pair) => pair.split('=')));
  const numbers = ['clients', 'main_ticks', 'cooldown_ticks', 'dual_cycles'].map((name) => Number(fields[name]));
  if (!numbers.every((n) => Number.isInteger(n) && n >= 0) || numbers[0] < 1) return undefined;
  const [clients, mainTicks, cooldownTicks, dualCycles] = numbers;
  return { clients, mainTicks, cooldownTicks, dualCycles, timing: fields.timing };
}

// Same plan as the measurement script: main ticks alternate 24h/13mo, then per dual cycle the
// cooldown ticks and one tick on both keys.
function buildPlan({ mainTicks, cooldownTicks, dualCycles }) {
  const result = [];
  for (let i = 0; i < mainTicks; i++) result.push([i % 2 === 0 ? '24h' : '13mo']);
  for (let c = 0; c < dualCycles; c++) {
    for (let i = 0; i < cooldownTicks; i++) result.push([]);
    result.push([...KEYS]);
  }
  return result;
}

// Developer harness input: accept only a regular, non-symlink *.csv file.
function readCsvFile(candidate) {
  const resolved = resolve(candidate);
  const inside = ALLOWED_BASES.some((base) => {
    const rel = relative(base, resolved);
    return rel !== '' && !rel.startsWith('..') && !isAbsolute(rel);
  });
  if (!inside) {
    console.error('the gate CSV must lie inside the repository or the temp directory');
    process.exit(64);
  }
  if (extname(resolved).toLowerCase() !== '.csv') {
    console.error('the gate input must be a .csv file');
    process.exit(64);
  }
  let stats;
  try {
    stats = lstatSync(resolved);
  } catch {
    console.error('the gate CSV does not exist or is not readable');
    process.exit(64);
  }
  if (!stats.isFile()) {
    console.error('the gate CSV must be a regular file (no symlink, no directory)');
    process.exit(64);
  }
  return readFileSync(resolved, 'utf8');
}

function parseCsv(text) {
  const [header, ...body] = text.trim().split(/\r?\n/);
  const names = header.split(',');
  const expected = 'kind,phase,tick,range,start_ms,end_ms,status,value,size_bytes,note';
  if (header !== expected) {
    console.error(`unexpected CSV header: does not match the expected columns (${expected})`);
    process.exit(64);
  }
  return body.map((line, index) => {
    const cells = line.split(',');
    if (cells.length !== names.length) {
      console.error(`line ${index + 2}: ${cells.length} cells, expected ${names.length}`);
      process.exit(64);
    }
    const raw = Object.fromEntries(names.map((name, i) => [name, cells[i]]));
    return {
      kind: raw.kind,
      phase: raw.phase,
      tick: raw.tick === '' ? undefined : Number(raw.tick),
      range: raw.range,
      start: Number(raw.start_ms),
      end: Number(raw.end_ms),
      status: raw.status,
      value: raw.value === '' ? Number.NaN : Number(raw.value),
      size: raw.size_bytes === '' ? undefined : Number(raw.size_bytes),
      note: raw.note,
      raw,
    };
  });
}

function median(values) {
  if (values.length === 0) return undefined;
  const sorted = [...values].sort((x, y) => x - y);
  const middle = Math.floor(sorted.length / 2);
  return sorted.length % 2 === 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
}

// Nearest rank, like SystemLoadRollupMath.
function percentile95(values) {
  if (values.length === 0) return undefined;
  const sorted = [...values].sort((x, y) => x - y);
  return sorted[Math.ceil(0.95 * sorted.length) - 1];
}

function kibToMib(kib) {
  return kib === undefined || !Number.isFinite(kib) ? 'n/a' : (kib / 1024).toFixed(1);
}

function fmt(value) {
  return value === undefined || !Number.isFinite(value) ? 'n/a' : value.toFixed(1);
}

function fmtInt(value) {
  return Number.isFinite(value) ? String(value) : 'n/a';
}
