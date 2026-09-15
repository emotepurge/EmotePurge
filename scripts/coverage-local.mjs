#!/usr/bin/env node
// Local pre-flight check that estimates whether the current branch would pass SonarCloud's
// Quality Gate condition "coverage on new code >= 80%" (see .github/workflows/sonarcloud.yml)
// — WITHOUT calling SonarCloud at all. Meant to be run before pushing/opening a PR, so a
// coverage shortfall is visible locally instead of only after CI/SonarCloud comes back red.
//
// APPROXIMATION — READ THIS BEFORE TRUSTING A GREEN RESULT:
// SonarCloud's "new code" coverage is computed LINE-BY-LINE: it diffs against the base branch
// (via git blame) and only counts lines that are actually new/changed as the denominator. This
// script approximates that at FILE granularity instead: for every file touched on this branch,
// it takes that file's *entire* coverage (from the same OpenCover/lcov reports Sonar itself would
// read) as a stand-in for "new code coverage" of that file. Line-accurate diffing was
// deliberately not attempted here — re-implementing Sonar's blame-based new-code detection would
// be its own non-trivial piece of software with its own failure modes, and a wrong "close
// approximation" is worse than an honest coarse one.
//
// Lines AND branches both count, but this is still not Sonar's formula. Sonar's own coverage
// number is (CT + CF + LC) / (2*B + EL) — covered true/false branch outcomes plus covered lines,
// over twice the branch count plus executable lines. This script instead counts (covered lines +
// covered branches) / (executable lines + total branches), read straight from the OpenCover
// SequencePoint/BranchPoint and lcov DA/BRDA records. That is CLOSER to Sonar than lines alone —
// a line can be fully "hit" while one of its branches is never taken (see docs/DECISIONS.md's
// rule-12 entry: "135 ungedeckt (dazu 20 von 144 Bedingungen)" for a real example of that gap —
// but it is not a reimplementation of Sonar's exact weighting, just a same-shaped approximation
// with one more input than before.
//
// What this means in practice:
//   - Brand-new files: the approximation is close to accurate (nearly all lines ARE new).
//   - A single new/changed line dropped into a large, already well-tested file: this script
//     will report that file's overall (mostly old, mostly covered) percentage, which can look
//     fine while SonarCloud reports 0% new-code coverage for the same file, because only that
//     one new line counts there. This is the direction where this script can give a false
//     "all clear" that SonarCloud then contradicts.
//   - The opposite also happens: a file with a poor OVERALL percentage (lots of old, untested
//     code) whose few new lines happen to be exactly the ones covered by a new test would score
//     red here while SonarCloud scores that same diff 100% (only the new lines count there).
//   - Weighting, not just per-file scoring, is skewed: since the frontend run is forced to
//     include every production file (--coverage-include), a large untouched-but-untested file
//     with only a handful of changed lines enters the denominator with its FULL line and branch
//     count. Measured example: usage-stats-page.ts contributes 0/391 lines + 0/178 branches here
//     while Sonar would weigh roughly the 166 lines the diff actually touched. Both call it 0%,
//     but this script lets it drag the overall figure down much harder than Sonar does.
// Neither direction is bounded — this is a genuine approximation, not a safe upper or lower
// bound on what SonarCloud will report. Treat a green result as "probably fine, not a
// guarantee" (especially for small, surgical diffs into large files), and treat a red result as
// "worth a closer look", not as proof SonarCloud will also fail the gate.
//
// A changed file that never appears in either report at all (no test imports it, e.g. a brand
// new component with no spec yet) is NOT silently treated as 0% or 100% — it is listed
// separately as "not measured", and its presence forces the overall verdict to stay cautious
// (see the "incomplete" handling in main()) even when the measured files alone would clear
// the threshold. Deliberately no heuristic tries to guess which unmeasured files are "probably
// fine" (e.g. type-only/interface files with nothing to execute) — such a heuristic would
// eventually misclassify a file that DOES have real logic, and it would do so silently, exactly
// where nobody would notice. An honest "can't tell, a human should look" beats a guess that is
// wrong occasionally and invisibly.
//
// The frontend run also passes `--coverage-include=src/**/*.ts` (see runFrontendTests below),
// on top of what .github/workflows/sonarcloud.yml itself runs. That flag only affects the lcov
// report THIS script reads locally — it is not added to angular.json or to the CI workflow, so
// the coverage number SonarCloud actually sees is unaffected. It shrinks (does not eliminate)
// the "not measured" blind spot: Vitest's default coverage.include only reports files that some
// test actually imported, so a completely untested new file simply never appears in lcov.info
// and this script couldn't have told "file has no data" apart from "file wasn't touched by any
// test" without it. With the broader include, such a file now shows up with 0% instead of
// vanishing — verified live against mass-delete-panel.ts (see the coordinator's requested
// verification).
//
// Required tools: git, dotnet, npm (unless the corresponding side is skipped).
// Docker must be running for the backend side (dotnet test uses Testcontainers).
//
// Usage:
//   node scripts/coverage-local.mjs [--base <ref>] [--skip-tests] [--backend-only|--frontend-only]
//
//   --base <ref>      Base ref to diff against (three-dot: <ref>...HEAD). Default: origin/main.
//   --skip-tests      Don't run the test suites; just read whatever reports are already on disk.
//   --backend-only    Only run/evaluate the .NET (OpenCover) side.
//   --frontend-only   Only run/evaluate the Angular (lcov) side.

import { spawnSync } from "node:child_process";
import { existsSync, readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

// Quality Gate threshold this script approximates. Must match the SonarCloud project's actual
// gate condition ("Coverage on New Code" >= 80%) — that value lives in SonarCloud's project
// settings, not in this repo, so there is nothing to keep it "in sync" with beyond this comment.
const NEW_CODE_COVERAGE_THRESHOLD_PERCENT = 80;

// Languages SonarCloud actually analyzes coverage for in this project. `.spec.ts` is
// deliberately excluded even though it ends in `.ts`: Sonar does not count test files as
// coverable "new code" — only the production code they exercise is measured.
const ANALYZED_EXTENSIONS = [".cs", ".ts"];

// These four lists MUST be kept in sync by hand with .github/workflows/sonarcloud.yml's
// `sonar.exclusions` / `sonar.coverage.exclusions` scanner arguments (see the "Sonar Scanner
// begin" step there). There is no shared source of truth between the workflow and this script.
const SONAR_EXCLUSIONS = [
  "**/Migrations/**",
  "src/EmotePurge.Api/wwwroot/**",
  "web/dist/**",
  "**/node_modules/**",
];
const SONAR_COVERAGE_EXCLUSIONS = [
  "web/e2e/**",
  "**/Migrations/**",
  "scripts/**",
];
// Not part of sonar.exclusions/sonar.coverage.exclusions: the .NET scanner learns to treat these
// xUnit projects as test code (not "new code") from `<IsTestProject>true</IsTestProject>` in
// their .csproj files, a mechanism this script has no access to. Excluding tests/** by hand
// reproduces the same effect.
const NON_PRODUCTION_EXCLUSIONS = ["tests/**"];
const COVERAGE_EXCLUSION_PATTERNS = [
  ...SONAR_EXCLUSIONS,
  ...SONAR_COVERAGE_EXCLUSIONS,
  ...NON_PRODUCTION_EXCLUSIONS,
];

const OPENCOVER_GLOB_MARKER = "TestResults";
const OPENCOVER_REPORT_FILENAME = "coverage.opencover.xml";
const LCOV_REPORT_PATH = "web/coverage/web/lcov.info";

// Directories never worth descending into while hunting for coverage.opencover.xml files —
// either they can't contain one (node_modules, .git, build output) or descending into them is
// simply wasteful (web/ never contains .NET TestResults).
const WALK_SKIP_DIRECTORIES = new Set([
  "node_modules",
  ".git",
  "bin",
  "obj",
  "web",
  ".angular",
  ".sonar",
  ".github",
]);

function parseArgs(argv) {
  const args = {
    base: "origin/main",
    skipTests: false,
    backendOnly: false,
    frontendOnly: false,
    help: false,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--help" || arg === "-h") {
      args.help = true;
    } else if (arg === "--skip-tests") {
      args.skipTests = true;
    } else if (arg === "--backend-only") {
      args.backendOnly = true;
    } else if (arg === "--frontend-only") {
      args.frontendOnly = true;
    } else if (arg === "--base") {
      const value = argv[i + 1];
      if (!value)
        throw new Error("--base requires a value, e.g. --base origin/main");
      args.base = value;
      i += 1;
    } else {
      throw new Error(`Unknown option: ${arg} (see --help)`);
    }
  }
  if (args.backendOnly && args.frontendOnly) {
    throw new Error(
      "--backend-only and --frontend-only are mutually exclusive.",
    );
  }
  return args;
}

function printHelp() {
  console.log(
    [
      "Local approximation of SonarCloud's Quality Gate condition 'Coverage on New Code >= 80%'.",
      "",
      "Usage:",
      "  node scripts/coverage-local.mjs [--base <ref>] [--skip-tests] [--backend-only|--frontend-only]",
      "",
      "  --base <ref>      Base ref to diff against (three-dot: <ref>...HEAD). Default: origin/main.",
      "  --skip-tests      Skip running the test suites, just evaluate existing reports.",
      "  --backend-only    Only the .NET/OpenCover side.",
      "  --frontend-only   Only the Angular/lcov side.",
    ].join("\n"),
  );
}

function runInherited(command, commandArgs, cwd) {
  const result = spawnSync(command, commandArgs, { cwd, stdio: "inherit" });
  if (result.error) {
    if (result.error.code === "ENOENT") {
      throw new Error(
        `Command "${command}" was not found — is it installed and on PATH?`,
      );
    }
    throw new Error(
      `Command "${command} ${commandArgs.join(" ")}" could not be started: ${result.error.message}`,
    );
  }
  return result.status ?? 1;
}

function resolveRepoRoot() {
  const result = spawnSync("git", ["rev-parse", "--show-toplevel"], {
    encoding: "utf8",
  });
  if (result.error) {
    if (result.error.code === "ENOENT") {
      throw new Error(
        '"git" was not found — is it installed and on PATH?',
      );
    }
    throw new Error(
      `"git rev-parse --show-toplevel" could not be started: ${result.error.message}`,
    );
  }
  if (result.status !== 0) {
    throw new Error(
      `"git rev-parse --show-toplevel" failed (exit ${result.status}). Is this script running inside a Git repository?\n${(result.stderr ?? "").trim()}`,
    );
  }
  return result.stdout.trim();
}

function isDockerRunning() {
  const result = spawnSync("docker", ["info"], { stdio: "ignore" });
  return !result.error && result.status === 0;
}

function runBackendTests(repoRoot) {
  if (!isDockerRunning()) {
    throw new Error(
      "Docker is not running (or not reachable) — dotnet test EmotePurge.slnx needs a running " +
        "Docker daemon for the Testcontainers integration tests in EmotePurge.Infrastructure.Tests. " +
        "Start Docker and try again, or use --frontend-only.",
    );
  }
  console.log(
    '--- Backend: dotnet test EmotePurge.slnx --collect:"XPlat Code Coverage;Format=opencover" ---',
  );
  const exitCode = runInherited(
    "dotnet",
    [
      "test",
      "EmotePurge.slnx",
      "--collect:XPlat Code Coverage;Format=opencover",
    ],
    repoRoot,
  );
  if (exitCode !== 0) {
    throw new Error(
      `dotnet test failed with exit code ${exitCode} — see the output above.`,
    );
  }
}

// `--coverage-include=src/**/*.ts` is added on top of the command CI runs (see the big comment
// above): verified live to raise Vitest's lcov output from 80-ish files actually imported by a
// test to 150 of the 151 non-spec .ts files under web/src (only test-setup.ts — itself test
// infrastructure — stays out), so a changed file with zero tests shows up as a real 0% instead
// of being indistinguishable from "report doesn't exist". This is a LOCAL-ONLY addition to this
// script's own npm invocation; it does not touch web/angular.json or
// .github/workflows/sonarcloud.yml, so SonarCloud's own coverage number is unaffected.
function runFrontendTests(repoRoot) {
  console.log(
    "--- Frontend: npm --prefix web test -- --watch=false --coverage --coverage-reporters=lcov --coverage-include=src/**/*.ts ---",
  );
  const exitCode = runInherited(
    "npm",
    [
      "--prefix",
      "web",
      "test",
      "--",
      "--watch=false",
      "--coverage",
      "--coverage-reporters=lcov",
      "--coverage-include=src/**/*.ts",
    ],
    repoRoot,
  );
  if (exitCode !== 0) {
    throw new Error(
      `npm test failed with exit code ${exitCode} — see the output above.`,
    );
  }
}

// Walks the repo looking for coverage.opencover.xml files under any TestResults/ directory.
// Each `dotnet test` run creates a NEW GUID subfolder under TestResults/ and leaves old ones
// behind — a real trap: reading all of them would double-count stale runs, and reading an old
// one instead of the newest would silently score a previous (possibly unrelated) code state.
// So: group by the TestResults/ directory itself (one group per test project) and keep only the
// file with the newest mtime in each group.
function findLatestOpenCoverReports(repoRoot) {
  const found = []; // { dir: <.../TestResults>, file: <full path>, mtimeMs }

  function walk(dir) {
    let entries;
    try {
      entries = readdirSync(dir, { withFileTypes: true });
    } catch {
      return; // unreadable directory (permissions, race) — skip rather than crash the whole scan
    }
    for (const entry of entries) {
      if (entry.name.startsWith(".") && entry.name !== ".") continue;
      const fullPath = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        if (WALK_SKIP_DIRECTORIES.has(entry.name)) continue;
        walk(fullPath);
      } else if (
        entry.isFile() &&
        entry.name === OPENCOVER_REPORT_FILENAME &&
        dir.includes(OPENCOVER_GLOB_MARKER)
      ) {
        const testResultsDir = findAncestorNamed(dir, OPENCOVER_GLOB_MARKER);
        found.push({
          dir: testResultsDir ?? dir,
          file: fullPath,
          mtimeMs: statSync(fullPath).mtimeMs,
        });
      }
    }
  }

  walk(repoRoot);

  const latestByGroup = new Map();
  for (const entry of found) {
    const current = latestByGroup.get(entry.dir);
    if (!current || entry.mtimeMs > current.mtimeMs)
      latestByGroup.set(entry.dir, entry);
  }
  return [...latestByGroup.values()].map((entry) => entry.file);
}

function findAncestorNamed(dir, name) {
  let current = dir;
  while (current && current !== path.dirname(current)) {
    if (path.basename(current) === name) return current;
    current = path.dirname(current);
  }
  return null;
}

function extractAttr(tag, attrName) {
  const match = new RegExp(`${attrName}="([^"]*)"`).exec(tag);
  return match ? match[1] : null;
}

// A per-file coverage record: lines AND branches, each a Map keyed by a dedup identity to a
// covered boolean. Lines dedupe by line number alone (a line counts covered if ANY sequence
// point on it was hit); branches dedupe by their own distinct-branch identity (see below) since,
// unlike lines, each branch is its own coverable unit — collapsing them into their line would
// throw away exactly the information branch coverage exists to capture.
function emptyFileCoverage() {
  return { lines: new Map(), branches: new Map() };
}

// Parses one OpenCover XML report into `fullPath -> { lines, branches }`.
//   - Lines: a line counts as covered if ANY SequencePoint on that (fileid, line) pair has
//     vc > 0 — a line can carry several sequence points (e.g. multiple statements), so points
//     are deduplicated by (fileid, sl) and OR-ed together rather than counted individually.
//   - Branches: each BranchPoint is one coverable branch outcome, identified within a file by
//     (sl, offset, path) — offset/path are the IL offset and branch index OpenCover itself uses
//     to distinguish sibling branches at the same line (e.g. the two arms of an `if`), so this
//     mirrors OpenCover's own notion of "distinct branch" rather than inventing one.
function parseOpenCoverXml(xmlText) {
  const pathByFileUid = new Map();
  for (const tag of xmlText.match(/<File\b[^>]*\/?>/g) ?? []) {
    const uid = extractAttr(tag, "uid");
    const fullPath = extractAttr(tag, "fullPath");
    if (uid && fullPath) pathByFileUid.set(uid, fullPath);
  }

  const linesByFileUid = new Map(); // fileUid -> Map<line, covered>
  for (const tag of xmlText.match(/<SequencePoint\b[^>]*\/>/g) ?? []) {
    const fileid = extractAttr(tag, "fileid");
    const startLine = extractAttr(tag, "sl");
    const visitCount = extractAttr(tag, "vc");
    if (!fileid || startLine === null) continue;
    const line = Number(startLine);
    const covered = Number(visitCount) > 0;
    if (!linesByFileUid.has(fileid)) linesByFileUid.set(fileid, new Map());
    const lineMap = linesByFileUid.get(fileid);
    lineMap.set(line, (lineMap.get(line) ?? false) || covered);
  }

  const branchesByFileUid = new Map(); // fileUid -> Map<"sl:offset:path", covered>
  for (const tag of xmlText.match(/<BranchPoint\b[^>]*\/>/g) ?? []) {
    const fileid = extractAttr(tag, "fileid");
    const startLine = extractAttr(tag, "sl");
    const offset = extractAttr(tag, "offset");
    const branchPath = extractAttr(tag, "path");
    const visitCount = extractAttr(tag, "vc");
    if (!fileid || startLine === null || offset === null || branchPath === null)
      continue;
    const key = `${startLine}:${offset}:${branchPath}`;
    const covered = Number(visitCount) > 0;
    if (!branchesByFileUid.has(fileid))
      branchesByFileUid.set(fileid, new Map());
    const branchMap = branchesByFileUid.get(fileid);
    branchMap.set(key, (branchMap.get(key) ?? false) || covered);
  }

  const result = new Map(); // fullPath -> { lines, branches }
  const allFileUids = new Set([
    ...linesByFileUid.keys(),
    ...branchesByFileUid.keys(),
  ]);
  for (const uid of allFileUids) {
    const fullPath = pathByFileUid.get(uid);
    if (!fullPath) continue;
    result.set(fullPath, {
      lines: linesByFileUid.get(uid) ?? new Map(),
      branches: branchesByFileUid.get(uid) ?? new Map(),
    });
  }
  return result;
}

// Parses web/coverage/web/lcov.info into `path -> { lines, branches }`. Standard lcov tracefile
// grammar: SF: starts a per-file block, DA:<line>,<hits> reports one line's hit count,
// BRDA:<line>,<block>,<branch>,<taken> reports one branch outcome (`taken` is either a hit count
// or the literal "-" for "never reached at all"), end_of_record closes the block. A branch is
// identified within a file by (line, block, branch) — the triple lcov itself uses to tell two
// branches at the same line apart (e.g. the two arms of a ternary).
function parseLcov(lcovText) {
  const result = new Map();
  let currentPath = null;
  let currentLines = null;
  let currentBranches = null;
  for (const rawLine of lcovText.split("\n")) {
    const line = rawLine.trim();
    if (line.startsWith("SF:")) {
      currentPath = line.slice(3).trim();
      currentLines = new Map();
      currentBranches = new Map();
    } else if (line.startsWith("DA:") && currentLines) {
      const [lineNoText, hitsText] = line.slice(3).split(",");
      const lineNo = Number(lineNoText);
      const hits = Number(hitsText);
      if (Number.isFinite(lineNo))
        currentLines.set(
          lineNo,
          (currentLines.get(lineNo) ?? false) || hits > 0,
        );
    } else if (line.startsWith("BRDA:") && currentBranches) {
      const [lineNoText, blockText, branchText, takenText] = line
        .slice(5)
        .split(",");
      const key = `${lineNoText}:${blockText}:${branchText}`;
      const covered = takenText !== "-" && Number(takenText) > 0;
      currentBranches.set(key, (currentBranches.get(key) ?? false) || covered);
    } else if (line === "end_of_record") {
      if (currentPath && currentLines) {
        result.set(currentPath, {
          lines: currentLines,
          branches: currentBranches ?? new Map(),
        });
      }
      currentPath = null;
      currentLines = null;
      currentBranches = null;
    }
  }
  return result;
}

// Normalizes an OpenCover/lcov path (absolute machine path, or a path already relative to some
// base) to a repo-relative POSIX path, so backend and frontend coverage data — and the `git diff`
// output — all key into the same namespace.
function normalizeToRepoRelativePosix(rawPath, repoRoot) {
  const slashed = rawPath.replaceAll(/\\/g, "/");
  const looksAbsolute =
    path.isAbsolute(rawPath) || /^[A-Za-z]:\//.test(slashed);
  if (looksAbsolute) {
    return path.relative(repoRoot, rawPath).replaceAll(/\\/g, "/");
  }
  const withoutDotSlash = slashed.replace(/^\.\//, "");
  // lcov entries are sometimes relative to the Angular project (web/) rather than the repo root —
  // if the raw relative path doesn't exist from the repo root but does under web/, it's the
  // web-relative form.
  if (
    !existsSync(path.join(repoRoot, withoutDotSlash)) &&
    existsSync(path.join(repoRoot, "web", withoutDotSlash))
  ) {
    return `web/${withoutDotSlash}`;
  }
  return withoutDotSlash;
}

// Merges a raw (path -> { lines, branches }) report into the accumulator, keyed by repo-relative
// POSIX path. Multiple reports can legitimately cover the same file (e.g. a Core class exercised
// by more than one test project) — lines and branches are each OR-ed together, never overwritten.
function mergeCoverageReport(accumulator, rawReport, repoRoot) {
  for (const [rawPath, fileCoverage] of rawReport) {
    const normalizedPath = normalizeToRepoRelativePosix(rawPath, repoRoot);
    if (!accumulator.has(normalizedPath))
      accumulator.set(normalizedPath, emptyFileCoverage());
    const target = accumulator.get(normalizedPath);
    for (const [line, covered] of fileCoverage.lines) {
      target.lines.set(line, (target.lines.get(line) ?? false) || covered);
    }
    for (const [branchKey, covered] of fileCoverage.branches) {
      target.branches.set(
        branchKey,
        (target.branches.get(branchKey) ?? false) || covered,
      );
    }
  }
}

function loadBackendCoverage(repoRoot) {
  const reportFiles = findLatestOpenCoverReports(repoRoot);
  const coverage = new Map();
  for (const file of reportFiles) {
    const xmlText = readFileSync(file, "utf8");
    mergeCoverageReport(coverage, parseOpenCoverXml(xmlText), repoRoot);
  }
  return { coverage, reportCount: reportFiles.length };
}

function loadFrontendCoverage(repoRoot) {
  const lcovPath = path.join(repoRoot, LCOV_REPORT_PATH);
  if (!existsSync(lcovPath)) {
    return { coverage: new Map(), reportCount: 0 };
  }
  const lcovText = readFileSync(lcovPath, "utf8");
  const coverage = new Map();
  mergeCoverageReport(coverage, parseLcov(lcovText), repoRoot);
  return { coverage, reportCount: 1 };
}

// Converts one exclusion glob (as used in sonar.exclusions / sonar.coverage.exclusions) into a
// RegExp. Supports the two forms actually used in this project's config: `**/x/**` (any depth on
// both sides) and `x/**` (a fixed prefix directory). `*` matches within one path segment.
function globToRegExp(pattern) {
  let out = "";
  let i = 0;
  while (i < pattern.length) {
    if (pattern.startsWith("**/", i)) {
      out += "(?:.*/)?";
      i += 3;
    } else if (pattern.startsWith("/**", i)) {
      out += "(?:/.*)?";
      i += 3;
    } else if (pattern.startsWith("**", i)) {
      out += ".*";
      i += 2;
    } else if (pattern[i] === "*") {
      out += "[^/]*";
      i += 1;
    } else {
      const ch = pattern[i];
      out += /[.+^${}()|[\]\\]/.test(ch) ? `\\${ch}` : ch;
      i += 1;
    }
  }
  return new RegExp(`^${out}$`);
}

const EXCLUSION_REGEXES = COVERAGE_EXCLUSION_PATTERNS.map(globToRegExp);

function isExcludedFromCoverage(relativePath) {
  return EXCLUSION_REGEXES.some((regex) => regex.test(relativePath));
}

function isAnalyzedSourceFile(relativePath) {
  if (relativePath.endsWith(".spec.ts")) return false;
  return ANALYZED_EXTENSIONS.some((ext) => relativePath.endsWith(ext));
}

function getChangedFiles(repoRoot, base) {
  const diffRange = `${base}...HEAD`;
  const result = spawnSync("git", ["diff", "--name-only", diffRange], {
    cwd: repoRoot,
    encoding: "utf8",
  });
  if (result.error) {
    if (result.error.code === "ENOENT") {
      throw new Error(
        '"git" was not found — is it installed and on PATH?',
      );
    }
    throw new Error(
      `"git diff --name-only ${diffRange}" could not be started: ${result.error.message}`,
    );
  }
  if (result.status !== 0) {
    throw new Error(
      `Base ref "${base}" could not be resolved (git diff --name-only ${diffRange} failed). ` +
        `Check the value of --base (default: origin/main) and whether a "git fetch" is needed.\n` +
        `Git error message:\n${result.stderr.trim()}`,
    );
  }
  return result.stdout
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .filter((relativePath) => existsSync(path.join(repoRoot, relativePath))); // ignore deleted files
}

function countCovered(map) {
  let covered = 0;
  for (const isCovered of map.values()) {
    if (isCovered) covered += 1;
  }
  return covered;
}

function buildFileReport(changedFiles, coverageByPath) {
  const measured = [];
  const unmeasured = [];
  for (const filePath of changedFiles) {
    const fileCoverage = coverageByPath.get(filePath);
    const totalLines = fileCoverage?.lines.size ?? 0;
    const totalBranches = fileCoverage?.branches.size ?? 0;
    if (!fileCoverage || totalLines + totalBranches === 0) {
      // No SequencePoint/BranchPoint at all for this file in either report — could mean "not
      // exercised by any test" or "genuinely has nothing executable" (a pure interface/type
      // file). Deliberately not distinguished (see the header comment): both are reported the
      // same, cautious way.
      unmeasured.push(filePath);
      continue;
    }
    const coveredLines = countCovered(fileCoverage.lines);
    const coveredBranches = countCovered(fileCoverage.branches);
    const covered = coveredLines + coveredBranches;
    const total = totalLines + totalBranches;
    measured.push({
      filePath,
      coveredLines,
      totalLines,
      coveredBranches,
      totalBranches,
      covered,
      total,
      percent: (covered / total) * 100,
    });
  }
  measured.sort((a, b) => a.percent - b.percent);
  return { measured, unmeasured };
}

function formatPercent(percent) {
  return `${percent.toFixed(1)}%`;
}

// Lines and branches are shown as separate columns (not just the combined percent) so a bad
// number is diagnosable at a glance: "lines fine, branches bad" points at untested error paths
// inside otherwise-executed code, which a single blended percentage would hide.
function printFileTable(measured) {
  if (measured.length === 0) {
    console.log("  (no measured files)");
    return;
  }
  const pathWidth = Math.max(
    ...measured.map((row) => row.filePath.length),
    "File".length,
  );
  const linesWidth = Math.max(
    ...measured.map((row) => `${row.coveredLines}/${row.totalLines}`.length),
    "Lines".length,
  );
  const branchesWidth = Math.max(
    ...measured.map(
      (row) => `${row.coveredBranches}/${row.totalBranches}`.length,
    ),
    "Branches".length,
  );
  const header =
    `  ${"File".padEnd(pathWidth)}  ${"Lines".padStart(linesWidth)}  ` +
    `${"Branches".padStart(branchesWidth)}  Share`;
  console.log(header);
  console.log(`  ${"-".repeat(header.length - 2)}`);
  for (const row of measured) {
    const linesText = `${row.coveredLines}/${row.totalLines}`.padStart(
      linesWidth,
    );
    const branchesText = `${row.coveredBranches}/${row.totalBranches}`.padStart(
      branchesWidth,
    );
    console.log(
      `  ${row.filePath.padEnd(pathWidth)}  ${linesText}  ${branchesText}  ${formatPercent(row.percent)}`,
    );
  }
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  if (args.help) {
    printHelp();
    return;
  }

  const runBackend = !args.frontendOnly;
  const runFrontend = !args.backendOnly;

  const repoRoot = resolveRepoRoot();

  if (!args.skipTests) {
    if (runBackend) runBackendTests(repoRoot);
    if (runFrontend) runFrontendTests(repoRoot);
  } else {
    console.log(
      "--skip-tests set — evaluating only existing coverage reports, without re-running tests.",
    );
  }

  console.log("");
  console.log(
    `Determining changed files against ${args.base} (git diff --name-only ${args.base}...HEAD)...`,
  );
  const changedFiles = getChangedFiles(repoRoot, args.base);

  const relevantFiles = changedFiles
    .filter(isAnalyzedSourceFile)
    .filter((filePath) => !isExcludedFromCoverage(filePath));

  console.log(
    `${changedFiles.length} changed file(s) total, ${relevantFiles.length} of which relevant ` +
      "(.cs/.ts, no .spec.ts, not excluded).",
  );

  let backendCoverage = new Map();
  let backendReportCount = 0;
  let frontendCoverage = new Map();
  let frontendReportCount = 0;

  if (runBackend) {
    const backend = loadBackendCoverage(repoRoot);
    backendCoverage = backend.coverage;
    backendReportCount = backend.reportCount;
    if (backendReportCount === 0) {
      throw new Error(
        `No OpenCover report found (pattern **/${OPENCOVER_GLOB_MARKER}/**/${OPENCOVER_REPORT_FILENAME}). ` +
          'Has dotnet test been run before with --collect:"XPlat Code Coverage;Format=opencover"? ' +
          "Without --skip-tests this should have happened automatically — check the output above for errors.",
      );
    }
  }
  if (runFrontend) {
    const frontend = loadFrontendCoverage(repoRoot);
    frontendCoverage = frontend.coverage;
    frontendReportCount = frontend.reportCount;
    if (frontendReportCount === 0) {
      throw new Error(
        `No lcov report found at ${LCOV_REPORT_PATH}. Has ` +
          '"npm --prefix web test -- --watch=false --coverage --coverage-reporters=lcov" been run before? ' +
          "Without --skip-tests this should have happened automatically — check the output above for errors.",
      );
    }
  }

  // OR-merge rather than overwrite: backend and frontend paths practically never collide (.cs
  // vs .ts), but a plain `.set()` would silently drop one side's data for a key that DID exist
  // in both, exactly the kind of quiet loss this script's whole point is to avoid.
  const combinedCoverage = new Map();
  for (const [filePath, fileCoverage] of backendCoverage) {
    combinedCoverage.set(filePath, {
      lines: new Map(fileCoverage.lines),
      branches: new Map(fileCoverage.branches),
    });
  }
  for (const [filePath, fileCoverage] of frontendCoverage) {
    if (!combinedCoverage.has(filePath)) {
      combinedCoverage.set(filePath, {
        lines: new Map(fileCoverage.lines),
        branches: new Map(fileCoverage.branches),
      });
      continue;
    }
    const target = combinedCoverage.get(filePath);
    for (const [line, covered] of fileCoverage.lines) {
      target.lines.set(line, (target.lines.get(line) ?? false) || covered);
    }
    for (const [branchKey, covered] of fileCoverage.branches) {
      target.branches.set(
        branchKey,
        (target.branches.get(branchKey) ?? false) || covered,
      );
    }
  }

  const { measured, unmeasured } = buildFileReport(
    relevantFiles,
    combinedCoverage,
  );

  console.log(
    "\n=== Coverage per changed file (file-granular approximation, ascending by share) ===",
  );
  printFileTable(measured);

  if (unmeasured.length > 0) {
    console.log(
      "\n=== Not measured (no coverage data found — report missing or no tests) ===",
    );
    for (const filePath of unmeasured) console.log(`  - ${filePath}`);
  }

  if (relevantFiles.length === 0) {
    console.log(
      "\nNo relevant changed files (.cs/.ts, no .spec.ts, not excluded) — nothing to evaluate.",
    );
    return;
  }

  const totalCoveredLines = measured.reduce(
    (sum, row) => sum + row.coveredLines,
    0,
  );
  const totalLines = measured.reduce((sum, row) => sum + row.totalLines, 0);
  const totalCoveredBranches = measured.reduce(
    (sum, row) => sum + row.coveredBranches,
    0,
  );
  const totalBranches = measured.reduce(
    (sum, row) => sum + row.totalBranches,
    0,
  );
  const totalCovered = totalCoveredLines + totalCoveredBranches;
  const totalUnits = totalLines + totalBranches;
  const overallPercent =
    totalUnits > 0 ? (totalCovered / totalUnits) * 100 : null;
  const incomplete = unmeasured.length > 0;

  console.log("\n=== Overall verdict ===");
  if (overallPercent === null) {
    console.log(
      "None of the relevant files have coverage data — overall percentage cannot be computed.",
    );
    process.exitCode = 1;
    return;
  }

  console.log(
    `Overall percentage across ${measured.length} measured file(s): ${totalCovered}/${totalUnits} ` +
      `(${totalCoveredLines}/${totalLines} lines, ${totalCoveredBranches}/${totalBranches} branches) ` +
      `= ${formatPercent(overallPercent)}.`,
  );
  if (incomplete) {
    console.log(
      `Additionally ${unmeasured.length} unmeasured file(s) — the overall percentage says nothing about these.`,
    );
  }

  const belowThreshold = overallPercent < NEW_CODE_COVERAGE_THRESHOLD_PERCENT;

  // An incomplete picture (changed, potentially coverable files with zero data) overrides a
  // clean verdict either way: a passing percentage computed only over the files that DID report
  // data says nothing about the ones that didn't, so this can never resolve to a plain "✓"/exit 0.
  if (incomplete) {
    const relation = belowThreshold ? "below" : "above";
    console.log(
      `\n⚠ Percentage ${formatPercent(overallPercent)} is ${relation} the ${NEW_CODE_COVERAGE_THRESHOLD_PERCENT}% threshold, ` +
        `BUT ${unmeasured.length} file(s) have no coverage data — verdict incomplete. Check these files by ` +
        "hand (when in doubt: did any test even import them?) before treating this branch as covered.",
    );
  } else if (belowThreshold) {
    console.log(
      `\n⚠ WARNING: ${formatPercent(overallPercent)} is below the ${NEW_CODE_COVERAGE_THRESHOLD_PERCENT}% threshold. ` +
        "That's a clear signal that SonarCloud's Quality Gate might reject this branch — but this direction, too, " +
        "is only a file-granular approximation, not a guarantee: Sonar only counts actually new/" +
        "changed lines, not the whole file, and can in individual cases turn out more favorable than this number.",
    );
  } else {
    console.log(
      `\n✓ ${formatPercent(overallPercent)} is above the ${NEW_CODE_COVERAGE_THRESHOLD_PERCENT}% threshold — but this is ` +
        "a file-granular approximation, not a guarantee: SonarCloud measures line-accurately on actually new/changed " +
        "lines and can judge more strictly than this number for small changes in large, old files.",
    );
  }

  if (belowThreshold || incomplete) {
    process.exitCode = 1;
  }
}

// `file://${process.argv[1]}` is NOT the canonical form of import.meta.url once the path
// contains spaces, non-ASCII characters, or (on Windows) backslashes/a drive letter — the
// comparison would then silently be false and the script would do nothing at all, with exit 0.
// pathToFileURL() builds the same URL Node itself used for import.meta.url.
const isDirectRun =
  process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isDirectRun) {
  try {
    await main();
  } catch (error) {
    console.error(`Aborted with error: ${error.message}`);
    process.exitCode = 1;
  }
}

export {
  parseArgs,
  globToRegExp,
  isAnalyzedSourceFile,
  parseOpenCoverXml,
  parseLcov,
  normalizeToRepoRelativePosix,
  buildFileReport,
};
