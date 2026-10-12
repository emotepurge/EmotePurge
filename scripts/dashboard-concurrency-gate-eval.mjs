#!/usr/bin/env node
// Evaluates a CSV written by scripts/dashboard-concurrency-gate.sh against the concurrency gate of the
// admin load dashboard (#381 cut B, spec section 11 "B-Parallelitaets-Gate"). Separate from the
// measurement so that prepared violations (scripts/fixtures/dashboard-gate/) can prove that the
// evaluation actually fails: exit 0 = gate passed, 1 = gate failed, 2 = run invalid (too few rebuild
// cycles or probes inside rebuilds to judge), 64 = usage or malformed input.
//
// CSV columns: kind,phase,tick,range,start_ms,end_ms,status,value,size_bytes,note
//   probe   - one GET /api/health: start_ms = send time, status = HTTP code (000 = timeout or
//             connection error), value = latency in ms
//   load    - one GET /api/admin/dashboard/load: range, status, value = time_total in ms,
//             size_bytes, note = Retry-After header value
//   rebuild - one logged cold rebuild of the facade: range, start/end from the Debug log lines,
//             status = outcome (ok|failed), value = logged duration in ms
//   mem     - one memory snapshot: start/end = previous/this snapshot, status = clear_refs|fallback,
//             value = VmHWM since the previous reset minus VmRSS at that reset, in KiB
//   query   - pg_stat_statements calls of the two load queries between the previous and this snapshot
//
// Criteria (thresholds from the spec):
//   validity: >= 10 main-phase rebuild cycles per key (24h and 13mo) and >= 40 health probes whose
//             send time lies inside a logged rebuild interval
//   (a) every main-phase rebuild cycle has exactly one rebuild for its CLIENTS requests, and every
//       snapshot window has as many load queries (pg_stat_statements) as logged rebuilds ended in it
//   (b) every memory increment <= 64 MiB
//   (c) every health probe answered 200; p95 latency of the probes sent inside a rebuild < 100 ms
//       (200s only)
//   (d) each dual cycle (24h and 13mo at the same instant): the rebuilds ran one after the other,
//       and every request of the key rebuilt second got 200 with a gate wait < 5 s (wait = its
//       rebuild start minus the request send time; the 5 s bound is the facade's wait for the
//       rebuild gate, not the total response time) or 503 with Retry-After
//   (e) uncached 24h load responses: median time_total < 500 ms; every load response <= 256 KiB

import { readFileSync } from 'node:fs';

const MEMORY_LIMIT_KIB = 64 * 1024;
const PROBE_P95_LIMIT_MS = 100;
const UNCACHED_MEDIAN_LIMIT_MS = 500;
const RESPONSE_SIZE_LIMIT_BYTES = 256 * 1024;
const GATE_WAIT_LIMIT_MS = 5000;
const MIN_CYCLES_PER_KEY = 10;
const MIN_PROBES_IN_REBUILD = 40;
const KEYS = ['24h', '13mo'];

const file = process.argv[2];
if (!file) {
  console.error('usage: node scripts/dashboard-concurrency-gate-eval.mjs <gate.csv>');
  process.exit(64);
}

const rows = parseCsv(readFileSync(file, 'utf8'));
const byKind = (kind) => rows.filter((row) => row.kind === kind);
const probes = byKind('probe');
const loads = byKind('load');
const rebuilds = byKind('rebuild');
const mems = byKind('mem');
const queries = byKind('query');

const failures = [];
const invalid = [];
const lines = [];

// Which load requests did a rebuild serve? A rebuild belongs to the requests of its range that were
// in flight when it started.
const rebuildRequests = rebuilds.map((rebuild) => ({
  rebuild,
  requests: loads.filter(
    (load) => load.range === rebuild.range && load.start <= rebuild.start && rebuild.start <= load.end,
  ),
}));
const phaseOf = (entry) => entry.requests[0]?.phase ?? 'none';
const tickOf = (entry) => entry.requests[0]?.tick ?? 'none';

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
const multiRebuildTicks = new Map();
for (const entry of mainCycles) {
  const key = `${tickOf(entry)}/${entry.rebuild.range}`;
  multiRebuildTicks.set(key, (multiRebuildTicks.get(key) ?? 0) + 1);
}
for (const [key, count] of multiRebuildTicks) {
  if (count !== 1) {
    failures.push(`(a) tick/range ${key}: ${count} rebuilds for one set of concurrent requests`);
  }
}
for (const entry of mainCycles) {
  const failed = entry.requests.filter((load) => load.status !== '200');
  if (failed.length > 0) {
    failures.push(`(a) rebuild ${entry.rebuild.range} at tick ${tickOf(entry)}: ${failed.length} of its requests not 200`);
  }
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
  `(a) ${mainCycles.length} main cycles, requests served per rebuild: min ${Math.min(...requestsPerCycle)} max ${Math.max(...requestsPerCycle)}; ` +
    `${queries.length} query windows, ${queryMismatches} log/pg_stat_statements mismatches`,
);

// (b) Memory.
const memoryModes = [...new Set(mems.map((mem) => mem.status))];
const worstMemory = mems.reduce((worst, mem) => (mem.value > (worst?.value ?? -Infinity) ? mem : worst), undefined);
const rebuildTicks = new Set(rebuildRequests.map((entry) => String(tickOf(entry))));
const rebuildMems = mems.filter((mem) => rebuildTicks.has(String(mem.tick)));
lines.push(
  `(b) memory mode ${memoryModes.join('+') || 'none'}; increment max ${kibToMib(worstMemory?.value)} MiB over all ${mems.length} snapshots, ` +
    `median ${kibToMib(median(rebuildMems.map((mem) => mem.value)))} MiB over ${rebuildMems.length} rebuild ticks`,
);
if (mems.length === 0) {
  invalid.push('no memory snapshots');
}
for (const mem of mems) {
  if (mem.value > MEMORY_LIMIT_KIB) {
    failures.push(`(b) memory increment ${kibToMib(mem.value)} MiB at tick ${mem.tick} exceeds 64 MiB`);
  }
}

// (c) Health probes.
for (const probe of probes) {
  if (probe.status !== '200') {
    failures.push(`(c) health probe at tick ${probe.tick} answered ${probe.status === '000' ? 'timeout/connection error' : probe.status}`);
  }
}
const okInRebuild = probesInRebuild.filter((probe) => probe.status === '200').map((probe) => probe.value);
const probeP95 = percentile95(okInRebuild);
lines.push(
  `(c) probe p95 inside rebuilds ${fmt(probeP95)} ms (n=${okInRebuild.length}), max ${fmt(Math.max(...okInRebuild))} ms; ` +
    `p95 over all 200s ${fmt(percentile95(probes.filter((p) => p.status === '200').map((p) => p.value)))} ms`,
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
  const tickRebuilds = rebuildRequests
    .filter((entry) => entry.requests.some((load) => load.phase === 'dual' && load.tick === tick))
    .map((entry) => entry.rebuild)
    .sort((x, y) => x.start - y.start);
  const busy = tickLoads.filter((load) => load.status === '503');
  if (tickRebuilds.length === 0) {
    failures.push(`(d) dual tick ${tick}: no rebuild logged`);
    continue;
  }
  const [first, second] = tickRebuilds;
  if (second && second.start < first.end) {
    failures.push(`(d) dual tick ${tick}: rebuilds of ${first.range} and ${second.range} overlapped`);
  }
  const secondKey = second?.range ?? KEYS.find((key) => key !== first.range);
  for (const load of tickLoads.filter((l) => l.range === secondKey)) {
    if (load.status === '503') {
      if (!load.note) {
        failures.push(`(d) dual tick ${tick}: ${secondKey} answered 503 without Retry-After`);
      }
    } else if (load.status !== '200') {
      failures.push(`(d) dual tick ${tick}: ${secondKey} answered ${load.status}`);
    } else if (!second) {
      failures.push(`(d) dual tick ${tick}: ${secondKey} answered 200 without a logged rebuild`);
    } else if (second.start - load.start >= GATE_WAIT_LIMIT_MS) {
      failures.push(`(d) dual tick ${tick}: ${secondKey} waited ${second.start - load.start} ms for the rebuild gate`);
    }
  }
  const secondLoads = tickLoads.filter((l) => l.range === secondKey);
  lines.push(
    `(d) dual tick ${tick}: first ${first.range} ${first.value} ms, second ${secondKey}` +
      (second ? ` gate wait ${second.start - Math.min(...secondLoads.map((l) => l.start))} ms, rebuild ${second.value} ms` : '') +
      `, second-key responses ${secondLoads.map((l) => `${l.status}/${fmt(l.value)}ms`).join(' ')}, 503s ${busy.length}`,
  );
}

// (e) Uncached response time and size.
const uncached = (key) =>
  mainCycles.filter((entry) => entry.rebuild.range === key).flatMap((entry) => entry.requests.filter((l) => l.status === '200'));
for (const key of KEYS) {
  const times = uncached(key).map((load) => load.value);
  const sizes = uncached(key).map((load) => load.size);
  lines.push(
    `(e) uncached ${key}: time_total median ${fmt(median(times))} ms, p95 ${fmt(percentile95(times))} ms, max ${fmt(Math.max(...times))} ms ` +
      `(n=${times.length}); size max ${Math.max(...sizes)} bytes`,
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
if (failures.length > 0) {
  const shown = failures.slice(0, 20);
  console.log(`GATE FAILED (${failures.length} violation(s)):`);
  for (const failure of shown) {
    console.log(`  ${failure}`);
  }
  if (failures.length > shown.length) {
    console.log(`  ... ${failures.length - shown.length} more`);
  }
  process.exit(1);
}
if (invalid.length > 0) {
  console.log('RUN INVALID:');
  for (const reason of invalid) {
    console.log(`  ${reason}`);
  }
  process.exit(2);
}
console.log('GATE PASSED');

function parseCsv(text) {
  const [header, ...body] = text.trim().split(/\r?\n/);
  const names = header.split(',');
  const expected = 'kind,phase,tick,range,start_ms,end_ms,status,value,size_bytes,note';
  if (header !== expected) {
    console.error(`unexpected CSV header: ${header}`);
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
      value: raw.value === '' ? undefined : Number(raw.value),
      size: raw.size_bytes === '' ? undefined : Number(raw.size_bytes),
      note: raw.note,
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
  return kib === undefined ? 'n/a' : (kib / 1024).toFixed(1);
}

function fmt(value) {
  return value === undefined || !Number.isFinite(value) ? 'n/a' : value.toFixed(1);
}
