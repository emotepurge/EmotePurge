-- Synthetic system-load history for the load-dashboard concurrency gate (#381 B6).
--
-- Produces the same data shape as the B5 fixture tests/EmotePurge.Infrastructure.Tests/Fixtures/
-- SystemLoadSampleSeed.cs, in plain SQL so the gate needs no product code to seed:
--   * SystemLoadSamples for both sources at a fixed cadence, ending at now(): the raw window
--     (window_hours, default 24) plus history_days (default 6) of earlier samples, so the read has to
--     find its window by index in a table that holds a week, like production keeps it;
--   * monotone counters per process instance with per-interval workload counters (Worker only);
--   * two process-instance changes per source inside the window (counters start over, no predecessor);
--   * one 10-minute gap shared by both sources, 12 h before now();
--   * SystemLoadHourly rows for both sources for hourly_days (default 400) of full UTC hours, so the
--     13mo range rebuilds over a full year of hourly history instead of an empty table.
-- Values are plausible, not a rollup of the raw rows; the gate measures read cost, not chart content.
--
-- THIS SCRIPT EMPTIES SystemLoadSamples AND SystemLoadHourly. It is meant for a throwaway database
-- and refuses to run unless called with -v confirm_truncate=yes.
--
-- Usage (psql variables, all optional except confirm_truncate):
--   psql "$CONN" -v ON_ERROR_STOP=1 -v confirm_truncate=yes -v cadence=1 \
--        [-v window_hours=24] [-v history_days=6] [-v hourly_days=400] [-v random_seed=0.42] \
--        -f scripts/seed-system-load-samples.sql
-- 1-second cadence writes ~1.2 million raw rows (about 20-40 s); 30-second cadence ~40,000.

\set ON_ERROR_STOP on

\if :{?confirm_truncate}
\else
\set confirm_truncate no
\endif
-- Raising (instead of \quit) makes psql exit non-zero under ON_ERROR_STOP.
SELECT :'confirm_truncate' = 'yes' AS seed_confirmed \gset
\if :seed_confirmed
\else
DO $$ BEGIN RAISE EXCEPTION 'Refusing to run: pass -v confirm_truncate=yes (this script empties SystemLoadSamples and SystemLoadHourly).'; END $$;
\endif

\if :{?cadence}
\else
\set cadence 1
\endif
\if :{?window_hours}
\else
\set window_hours 24
\endif
\if :{?history_days}
\else
\set history_days 6
\endif
\if :{?hourly_days}
\else
\set hourly_days 400
\endif
\if :{?random_seed}
\else
\set random_seed 0.42
\endif

BEGIN;

TRUNCATE "SystemLoadSamples", "SystemLoadHourly";

SELECT setseed(:random_seed);

CREATE TEMP TABLE seed_params ON COMMIT DROP AS
SELECT
    date_trunc('second', now()) AS now_utc,
    :cadence::int AS cadence,
    (:window_hours * 3600 / :cadence)::int AS window_slots,
    ((:window_hours * 3600 + :history_days * 86400) / :cadence)::int AS total_slots;

-- One instance id per source and segment: segment 0 started in the history, segments 1 and 2 start at
-- one and two thirds of the window (process restarts).
CREATE TEMP TABLE seed_instances ON COMMIT DROP AS
SELECT source, segment, gen_random_uuid() AS instance_id
FROM (VALUES ('Api'), ('Worker')) AS sources(source)
CROSS JOIN generate_series(0, 2) AS segments(segment);

INSERT INTO "SystemLoadSamples" (
    "SampledAtUtc", "Source", "ProcessInstanceId", "PreviousSampledAtUtc",
    "ProcessCpuTimeTicks", "CgroupCpuUsageMicroseconds", "CgroupCpuQuotaMicroseconds", "CgroupCpuPeriodMicroseconds",
    "CgroupMemoryCurrentBytes", "CgroupMemoryMaxBytes", "HostCpuBusyJiffies", "HostCpuTotalJiffies",
    "HostCpuIowaitJiffies", "HostCpuStealJiffies", "HostLoadAverage1", "HostLoadAverage5", "HostLoadAverage15",
    "HostMemoryTotalBytes", "HostMemoryAvailableBytes", "HostSwapTotalBytes", "HostSwapFreeBytes",
    "ActiveChannels", "LiveChannels", "ChatLines", "EmoteMatches")
SELECT
    r.sampled_at,
    r.source,
    i.instance_id,
    lag(r.sampled_at) OVER w,
    sum(r.cpu_ticks_step) OVER w,
    sum(r.cgroup_cpu_step) OVER w,
    CASE WHEN r.source = 'Worker' THEN 100000 ELSE 200000 END,
    100000,
    r.cgroup_memory,
    536870912,
    sum(r.host_busy_step) OVER w,
    sum(r.host_total_step) OVER w,
    sum(r.host_iowait_step) OVER w,
    sum(r.host_steal_step) OVER w,
    r.load1,
    r.load1 * 0.9,
    r.load1 * 0.8,
    25769803776,
    r.host_available,
    8589934592,
    8000000000,
    r.active_channels,
    r.live_channels,
    r.chat_lines,
    r.emote_matches
FROM (
    -- Volatile random() keeps this subquery from being flattened, so every value is drawn once per row.
    SELECT
        s.source,
        p.now_utc - make_interval(secs => (p.total_slots - 1 - g.slot)::double precision * p.cadence) AS sampled_at,
        g.slot,
        CASE
            WHEN g.slot < p.total_slots - p.window_slots + p.window_slots / 3 THEN 0
            WHEN g.slot < p.total_slots - p.window_slots + 2 * p.window_slots / 3 THEN 1
            ELSE 2
        END AS segment,
        (s.cores * (0.5 + random()) * p.cadence * 10000000)::bigint AS cpu_ticks_step,
        (s.cores * (0.5 + random()) * p.cadence * 1000000)::bigint AS cgroup_cpu_step,
        -- Gauge between 100 and 450 MB with a two-hour wave.
        (275000000 + 150000000 * sin(g.slot * p.cadence * 2 * pi() / 7200) + random() * 2000000)::bigint AS cgroup_memory,
        -- Six host cores at 100 Hz; utilisation wanders between 5 % and 45 %.
        (600 * p.cadence * (0.05 + 0.4 * random()))::bigint AS host_busy_step,
        (600 * p.cadence)::bigint AS host_total_step,
        (600 * p.cadence * 0.01 * random())::bigint AS host_iowait_step,
        (600 * p.cadence * 0.005 * random())::bigint AS host_steal_step,
        greatest(0.05, 0.6 + 0.5 * sin(g.slot * p.cadence * 2 * pi() / 10800) + 0.1 * random()) AS load1,
        (16000000000 + 5000000000 * sin(g.slot * p.cadence * 2 * pi() / 86400) + random() * 20000000)::bigint AS host_available,
        CASE WHEN s.source = 'Worker' THEN (45 + 15 * sin(g.slot * p.cadence * 2 * pi() / 43200))::int END AS active_channels,
        CASE WHEN s.source = 'Worker' THEN (12 + 10 * sin(g.slot * p.cadence * 2 * pi() / 43200))::int END AS live_channels,
        -- Interval counters, as the schema stores them: what was counted since the previous sample.
        CASE WHEN s.source = 'Worker' THEN floor(random() * 14)::bigint * p.cadence END AS chat_lines,
        CASE WHEN s.source = 'Worker' THEN floor(random() * 5)::bigint * p.cadence END AS emote_matches
    FROM seed_params AS p
    CROSS JOIN (VALUES ('Api', 0.05), ('Worker', 0.15)) AS s(source, cores)
    CROSS JOIN LATERAL generate_series(0, p.total_slots - 1) AS g(slot)
) AS r
JOIN seed_instances AS i ON i.source = r.source AND i.segment = r.segment
CROSS JOIN seed_params AS p
-- The shared 10-minute gap, centred 12 h before now.
WHERE NOT (r.sampled_at >= p.now_utc - interval '12 hours' - interval '5 minutes'
       AND r.sampled_at < p.now_utc - interval '12 hours' + interval '5 minutes')
WINDOW w AS (PARTITION BY r.source, r.segment ORDER BY r.slot);

-- Hourly history: every full UTC hour of the last hourly_days days for both sources.
INSERT INTO "SystemLoadHourly" (
    "Source", "HourUtc", "SampleCount",
    "ProcessCpuRateMean", "ProcessCpuRateMax", "ProcessCpuRateP95",
    "CgroupCpuRateMean", "CgroupCpuRateMax", "CgroupCpuRateP95",
    "HostCpuBusyMean", "HostCpuBusyMax", "HostCpuBusyP95",
    "CgroupMemoryCurrentMeanBytes", "CgroupMemoryCurrentMaxBytes", "CgroupMemoryCurrentP95Bytes",
    "HostMemoryUsedMeanBytes", "HostMemoryUsedMaxBytes", "HostMemoryUsedP95Bytes",
    "HostLoadAverage1Mean", "HostLoadAverage1Max", "HostLoadAverage1P95",
    "LastCpuQuotaMicroseconds", "LastCpuPeriodMicroseconds", "LastCgroupMemoryMaxBytes", "LastHostMemoryTotalBytes",
    "ChatLines", "EmoteMatches", "ActiveChannelsMean", "ActiveChannelsMax", "LiveChannelsMean", "LiveChannelsMax")
SELECT
    h.source, h.hour_utc, 3600 / p.cadence,
    h.cpu, h.cpu * 1.8, h.cpu * 1.5,
    h.cpu, h.cpu * 1.8, h.cpu * 1.5,
    h.busy, least(1.0, h.busy * 1.9), least(1.0, h.busy * 1.6),
    h.memory, (h.memory * 1.2)::bigint, (h.memory * 1.15)::bigint,
    h.host_used, (h.host_used * 1.1)::bigint, (h.host_used * 1.08)::bigint,
    h.load1, h.load1 * 1.7, h.load1 * 1.5,
    CASE WHEN h.source = 'Worker' THEN 100000 ELSE 200000 END, 100000, 536870912, 25769803776,
    CASE WHEN h.source = 'Worker' THEN (3600 * 6.5 * (0.6 + 0.8 * random()))::bigint END,
    CASE WHEN h.source = 'Worker' THEN (3600 * 2 * (0.6 + 0.8 * random()))::bigint END,
    CASE WHEN h.source = 'Worker' THEN 45 + 10 * random() END,
    CASE WHEN h.source = 'Worker' THEN (55 + 5 * random())::int END,
    CASE WHEN h.source = 'Worker' THEN 12 + 6 * random() END,
    CASE WHEN h.source = 'Worker' THEN (20 + 5 * random())::int END
FROM (
    SELECT
        s.source,
        hours.hour_utc,
        s.cores * (0.8 + 0.4 * random()) AS cpu,
        0.05 + 0.3 * random() AS busy,
        (200000000 + 150000000 * random())::double precision AS memory,
        (9000000000 + 4000000000 * random())::double precision AS host_used,
        0.3 + 1.2 * random() AS load1
    FROM seed_params AS p
    CROSS JOIN (VALUES ('Api', 0.05), ('Worker', 0.15)) AS s(source, cores)
    CROSS JOIN LATERAL generate_series(
        date_trunc('hour', p.now_utc) - make_interval(days => :hourly_days),
        date_trunc('hour', p.now_utc) - interval '1 hour',
        interval '1 hour') AS hours(hour_utc)
) AS h
CROSS JOIN seed_params AS p;

SELECT
    (SELECT now_utc FROM seed_params) AS seeded_until_utc,
    (SELECT cadence FROM seed_params) AS cadence_seconds,
    (SELECT count(*) FROM "SystemLoadSamples") AS raw_samples,
    (SELECT count(*) FROM "SystemLoadSamples"
       WHERE "SampledAtUtc" > (SELECT now_utc FROM seed_params) - make_interval(hours => :window_hours)) AS raw_samples_in_window,
    (SELECT count(*) FROM "SystemLoadHourly") AS hourly_rows;

COMMIT;

ANALYZE "SystemLoadSamples";
ANALYZE "SystemLoadHourly";
